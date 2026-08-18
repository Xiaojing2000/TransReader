using TransReader.Core.Ocr;
using TransReader.Core.Translation;
using TransReader.Core;

namespace TransReader.App.Services;

internal sealed record LocalPageTranslation(
    MultimodalTranslationResult Result,
    bool IsComplete,
    string? Warning,
    bool OutputDegraded = false);

internal sealed class LocalTextTranslationService
{
    private readonly LocalModelManager _models;
    private readonly OpenAiCompatibleTranslator _translator;
    private readonly Dictionary<string, IReadOnlyList<MultimodalTranslationResult>> _resumePoints =
        new(StringComparer.Ordinal);

    public LocalTextTranslationService(LocalModelManager models, OpenAiCompatibleTranslator translator)
    {
        _models = models;
        _translator = translator;
    }

    /// <summary>清除指定页面的内存断点，使其下次从头重翻。</summary>
    public void ClearResume(string resumeKey) => _resumePoints.Remove(resumeKey);

    /// <summary>清除全部内存断点（文档关闭、模式切换或用户显式"重新翻译"时调用）。</summary>
    public void ClearAllResumePoints() => _resumePoints.Clear();

    public TranslationProfile CreateProfile(
        string targetLanguage,
        Uri? baseUri = null,
        LocalModelDescriptor? descriptor = null)
    {
        descriptor ??= _models.PreferredTranslationModel;
        var modelTargetLanguage = descriptor.Purpose == LocalModelPurpose.Translation
            ? NormalizeHyMtLanguage(targetLanguage)
            : targetLanguage;
        return new TranslationProfile(
        descriptor.ProviderId,
        new TranslationSettings(
            (baseUri ?? new Uri("http://127.0.0.1:1/v1")).ToString().TrimEnd('/'),
            descriptor.Id,
            modelTargetLanguage,
            "none",
            IsMultimodal: false,
            Temperature: descriptor.Temperature,
            DisableThinking: true,
            ProviderId: descriptor.ProviderId,
            CacheIdentity: descriptor.CacheIdentity,
            Provider: TranslationProvider.Local),
        string.Empty);
    }

    private static string NormalizeHyMtLanguage(string language) => language switch
    {
        "简体中文" => "中文",
        "英文" => "英语",
        _ => language
    };

    public async Task<LocalPageTranslation> TranslateAsync(
        string resumeKey,
        TranslationProfile cacheProfile,
        TextTranslationRequest request,
        IProgress<MarkdownRenderUpdate>? progress,
        CancellationToken cancellationToken)
    {
        var chunks = LocalTranslationChunker.Split(request.Ocr.Blocks);
        if (chunks.Count == 0)
            throw new TranslationException("本页 OCR 没有识别到可翻译的文字。");

        using var session = await _models.OpenSessionAsync(
            LocalAiPriority.ForegroundTranslation, LocalModelPurpose.Translation, cancellationToken).ConfigureAwait(false);
        var runtimeProfile = CreateProfile(cacheProfile.Settings.TargetLanguage, session.BaseUri, session.Descriptor);
        var completed = _resumePoints.TryGetValue(resumeKey, out var saved)
            ? saved.ToList()
            : [];
        if (completed.Count > chunks.Count)
        {
            completed.Clear();
            _resumePoints.Remove(resumeKey);
        }
        var combined = completed.Select(result => result.Text).ToList();
        string? warning = LooksComplex(request.Ocr)
            ? "本页可能包含表格、公式或多栏版面，本地纯文本模型可能无法准确还原布局；需要时请主动切换在线翻译。"
            : null;
        if (completed.Count > 0)
        {
            progress?.Report(new MarkdownRenderUpdate(
                string.Join("\n\n", combined),
                TranslationPipelineStage.Drafting,
                false,
                Step: completed.Count,
                StepCount: chunks.Count));
        }
        for (var index = completed.Count; index < chunks.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var blocks = chunks[index];
            var source = string.Join("\n\n", blocks.Select(block => block.Text));
            var context = BuildChunkContext(request.Context, completed.LastOrDefault());
            try
            {
                var streamedChunk = string.Empty;
                // 输出预算随服务实际上下文档位走：满档 12288 给 4096 余量（1.7B 在稠密版面偶发长篇），
                // 降档 8192 保持 3072（提示词最坏 ~4900 + 3072 ≈ 7972，不超窗）。
                var completionBudget = _models.ActiveContextSize >= 12288 ? 4096 : 3072;
                var result = await _translator.TranslateTextStreamingAsync(
                    runtimeProfile.Settings,
                    string.Empty,
                    new TextTranslationRequest(source,
                        new OcrPage(request.Ocr.Width, request.Ocr.Height, blocks),
                        request.PageNumber,
                        context),
                    new InlineProgress<string>(value =>
                    {
                        streamedChunk = value;
                        var visible = string.Join("\n\n", combined.Append(value));
                        progress?.Report(new MarkdownRenderUpdate(
                            visible,
                            TranslationPipelineStage.Drafting,
                            false,
                            Step: index + 1,
                            StepCount: chunks.Count));
                    }),
                    cancellationToken,
                    completionBudget).ConfigureAwait(false);
                completed.Add(result);
                combined.Add(result.Text);
                _resumePoints[resumeKey] = completed.ToArray();
                progress?.Report(new MarkdownRenderUpdate(
                    string.Join("\n\n", combined),
                    TranslationPipelineStage.Drafting,
                    false,
                    Step: index + 1,
                    StepCount: chunks.Count));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (combined.Count == 0) throw;
                warning = $"本地翻译在第 {index + 1}/{chunks.Count} 段中断：{ex.Message} 可点击重新翻译继续尝试。";
                break;
            }
        }

        var last = completed.Last();
        var isComplete = completed.Count == chunks.Count;
        if (isComplete) _resumePoints.Remove(resumeKey);
        // 与在线路径对齐：占位符/重复句退化时展示清理后文本、不落缓存、提示重译。
        var sanitized = TranslationOutputSanitizer.Sanitize(
            TranslationMarkdownNormalizer.Normalize(string.Join("\n\n", combined)));
        var degraded = sanitized.RequiresReview;
        if (degraded)
        {
            warning = "检测到模型输出异常（占位符或重复句），已自动清理；建议点击重新翻译。";
        }
        return new LocalPageTranslation(
            last with
            {
                Text = sanitized.Text,
                ContextFingerprint = request.Context.Fingerprint(),
                WasReviewed = false,
                OcrAvailable = true
            },
            isComplete,
            warning,
            degraded);
    }

    private static bool LooksComplex(OcrPage page)
    {
        if (page.Blocks.Count >= 60) return true;
        var text = string.Join(' ', page.Blocks.Select(block => block.Text));
        string[] markers = ["∑", "∫", "√", "matrix", "Table ", "Figure ", "表 ", "图 ", "=", "→"];
        return markers.Count(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase)) >= 2;
    }

    private static DocumentTranslationContext BuildChunkContext(
        DocumentTranslationContext page,
        MultimodalTranslationResult? previousChunk)
    {
        var summary = TextUtil.LimitTail(previousChunk?.Summary ?? page.Summary, 600);
        var terms = (previousChunk?.Terms ?? page.Terms).TakeLast(30).ToList();
        var previousTranslation = previousChunk is null
            ? TextUtil.LimitTail(page.PreviousTranslation, 2000)
            : TextUtil.LimitTail(previousChunk.Text, 1000);
        return new DocumentTranslationContext(
            summary,
            terms,
            TextUtil.LimitTail(page.PreviousSourceText, 2000),
            previousTranslation);
    }

}
