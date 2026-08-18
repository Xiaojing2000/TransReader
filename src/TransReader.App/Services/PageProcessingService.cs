using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using System.Text;
using TransReader.Core.Ocr;
using TransReader.Core.Storage;
using TransReader.Core.Translation;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace TransReader.App.Services;

/// <summary>
/// Bounded per-page pipeline. Foreground render/OCR work follows navigation
/// cancellation, while an API request that has already started owns a document
/// token and is allowed to finish and persist in the background.
/// </summary>
internal sealed class PageProcessingService : IDisposable
{
    private const double FullPageMaxDimension = 1600d;
    private const int FullPageCacheRadius = 1;

    private readonly PageOcrCache _ocrCache;
    private readonly PageTranslationCache _translationCache;
    private readonly OcrCoordinator _ocrCoordinator;
    private readonly OpenAiCompatibleTranslator _translator;
    private readonly LocalTextTranslationService _localTranslator;
    private readonly LocalModelManager _localModels;
    private readonly TranslationUsageStore? _usageStore;
    private readonly SemaphoreSlim _renderGate = new(1, 1);
    // 缩略图用独立的门：不再与整页渲染/预取互相排队（滚动缩略图时不再被整页渲染堵住）。
    private readonly SemaphoreSlim _thumbnailRenderGate = new(1, 1);
    private readonly SemaphoreSlim _translationGate = new(2, 2);
    private int _foregroundRenderWaiting;
    private readonly ConcurrentDictionary<uint, Lazy<Task<PageRender>>> _renders = new();
    private readonly ConcurrentDictionary<uint, Lazy<Task<PageData>>> _pageData = new();
    private readonly ConcurrentDictionary<string, Lazy<PageTranslationJob>> _translationJobs = new();

    private PdfDocument? _document;
    private string? _documentKey;
    private CancellationTokenSource? _documentWork;
    private CancellationTokenSource? _prefetchWork;

    public PageProcessingService(
        PageOcrCache ocrCache,
        PageTranslationCache translationCache,
        OcrCoordinator ocrCoordinator,
        OpenAiCompatibleTranslator translator,
        LocalTextTranslationService localTranslator,
        LocalModelManager localModels,
        TranslationUsageStore? usageStore = null)
    {
        _ocrCache = ocrCache;
        _translationCache = translationCache;
        _ocrCoordinator = ocrCoordinator;
        _translator = translator;
        _localTranslator = localTranslator;
        _localModels = localModels;
        _usageStore = usageStore;
    }

    public bool HasDocument => _document is not null;
    public uint PageCount => _document?.PageCount ?? 0;

    /// <summary>空闲时预翻译下一页（仅在线模式；默认关闭，由设置驱动，尊重 API 计费）。</summary>
    public bool PrefetchTranslationEnabled { get; set; }

    /// <summary>在线翻译瞬时故障时自动改用本地模型兜底（需本地模型已安装；4xx 配置错误不兜底）。</summary>
    public bool LocalFallbackEnabled { get; set; }

    /// <summary>翻译类型提示（领域键，如 math / computer_science；空 = 通用）。
    /// 注入到所有翻译请求的上下文与缓存指纹（切换后相关页自动按新类型重译）。</summary>
    public string TranslationDomainHint { get; set; } = string.Empty;

    public void CancelActiveTranslations()
    {
        foreach (var job in _translationJobs.Values)
            if (job.IsValueCreated) job.Value.Cancel();
        _translationJobs.Clear();
    }

    public Task<DocumentTranslationContext> GetDocumentContextAsync(
        uint pageIndex,
        TranslationSettings settings,
        CancellationToken cancellationToken = default) =>
        BuildContextAsync(pageIndex, settings, cancellationToken)
            .ContinueWith(task => WithDomainHint(task.Result), cancellationToken);

    /// <summary>把当前翻译类型提示注入上下文（进 prompt 与缓存指纹；空 hint 原样返回）。</summary>
    private DocumentTranslationContext WithDomainHint(DocumentTranslationContext context) =>
        string.IsNullOrEmpty(TranslationDomainHint) || !string.IsNullOrEmpty(context.Domain)
            ? context
            : context with { Domain = TranslationDomainHint };

    public async Task DeleteDocumentCacheAsync(string documentKey)
    {
        if (string.Equals(_documentKey, documentKey, StringComparison.Ordinal))
        {
            await ResetActiveDocumentWorkAsync();
        }
        _ocrCache.DeleteDocument(documentKey);
        _translationCache.DeleteDocument(documentKey);
    }

    /// <summary>
    /// 清理该文献当前 provider/OCR 引擎版本下的过期/损坏 page 文件，返回删除数。打开文档时后台懒触发。
    /// 不删除其他 provider 的译文目录（切换模型后旧译文保留复用）；容量由全局 CacheSweeper 按 LRU 管理。
    /// </summary>
    public async Task<int> PruneDocumentCacheAsync(
        string documentKey,
        TranslationSettings settings,
        CancellationToken cancellationToken = default)
    {
        var deleted = await _translationCache.PruneDocumentAsync(documentKey, settings, cancellationToken);
        // OCR 缓存：删 EngineVersion 不匹配的过期 page 文件（与翻译缓存对齐）。
        deleted += await _ocrCache.PruneDocumentAsync(documentKey, _ocrCoordinator.EngineVersion, cancellationToken);
        return deleted;
    }

    public async Task ClearPersistentCachesAsync()
    {
        await ResetActiveDocumentWorkAsync();
        _ocrCache.Clear();
        _translationCache.Clear();
    }

    /// <summary>导出缓存的译文为 Markdown（纯译文或原文+译文双语），适用于任意已入库文献。</summary>
    public async Task ExportAsync(
        string documentKey,
        string title,
        uint pageCount,
        TranslationSettings settings,
        string destinationPath,
        bool bilingual,
        CancellationToken cancellationToken = default)
    {
        if (bilingual)
        {
            await TranslationExporter.ExportBilingualAsync(documentKey, title, pageCount, settings,
                _translationCache, _ocrCache, destinationPath, cancellationToken);
        }
        else
        {
            await TranslationExporter.ExportMarkdownAsync(documentKey, title, pageCount, settings,
                _translationCache, destinationPath, cancellationToken);
        }
    }

    /// <summary>记录一次在线翻译请求的 token 用量（本地模式 usage 为 null，自动跳过）。</summary>
    private async Task RecordUsageAsync(TranslationProfile profile, TranslationUsage? usage, CancellationToken cancellationToken)
    {
        if (_usageStore is null || usage is null) return;
        try
        {
            await _usageStore.RecordAsync(profile.Settings.ProviderId, profile.Settings.Model, usage, cancellationToken);
        }
        catch (Exception ex)
        {
            AppLog.Error("翻译用量记录失败", ex);
        }
    }

    public void OpenDocument(PdfDocument document, string documentKey)
    {
        CloseDocument();
        _document = document;
        _documentKey = documentKey;
        _documentWork = new CancellationTokenSource();
    }

    public void PrepareForNavigation(uint pageIndex)
    {
        _prefetchWork?.Cancel();
        _prefetchWork?.Dispose();
        _prefetchWork = null;
        PruneToWindow(pageIndex);
    }

    /// <summary>丢弃全部本地翻译内存断点，下次翻译从头开始（用户显式"重新翻译"时使用）。</summary>
    public void ClearLocalResumePoints() => _localTranslator.ClearAllResumePoints();

    public void CloseDocument()
    {
        _prefetchWork?.Cancel();
        _prefetchWork?.Dispose();
        _prefetchWork = null;
        _documentWork?.Cancel();
        _documentWork?.Dispose();
        _documentWork = null;
        _document = null;
        // 内存断点只在当前文档会话内有效，关闭/切换文档时清空。
        _localTranslator.ClearAllResumePoints();
        _documentKey = null;

        foreach (var key in _renders.Keys.ToArray())
        {
            EvictPage(key);
        }
        _renders.Clear();
        _pageData.Clear();
        _translationJobs.Clear();
    }

    private async Task ResetActiveDocumentWorkAsync()
    {
        if (_document is null)
        {
            return;
        }

        _prefetchWork?.Cancel();
        _prefetchWork?.Dispose();
        _prefetchWork = null;
        _documentWork?.Cancel();
        _documentWork?.Dispose();
        _documentWork = new CancellationTokenSource();

        var pendingTasks = _renders.Values
            .Where(value => value.IsValueCreated)
            .Select(value => (Task)value.Value)
            .Concat(_pageData.Values
                .Where(value => value.IsValueCreated)
                .Select(value => (Task)value.Value))
            .Concat(_translationJobs.Values
                .Where(value => value.IsValueCreated)
                .Select(value => (Task)value.Value.Completion))
            .Distinct()
            .ToArray();

        foreach (var key in _renders.Keys.ToArray())
        {
            EvictPage(key);
        }
        _renders.Clear();
        _pageData.Clear();
        _translationJobs.Clear();

        try
        {
            await Task.WhenAll(pendingTasks);
        }
        catch
        {
            // Expected when cleanup cancels the active render/OCR/API pipeline.
        }
    }

    public Task<PageRender> GetPageRenderAsync(uint pageIndex, CancellationToken cancellationToken)
    {
        ThrowIfNoDocument();
        return GetOrCreateTaskAsync(
            _renders,
            pageIndex,
            token => RenderPageCoreAsync(pageIndex, token),
            cancellationToken);
    }

    public Task<PageData> GetPageDataAsync(uint pageIndex, CancellationToken cancellationToken)
    {
        ThrowIfNoDocument();
        return GetOrCreateTaskAsync(
            _pageData,
            pageIndex,
            token => OcrPageCoreAsync(pageIndex, token),
            cancellationToken);
    }

    public async Task<PageTranslationResult> GetTranslationAsync(
        uint pageIndex,
        TranslationProfile profile,
        IProgress<MarkdownRenderUpdate>? progress,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        ThrowIfNoDocument();
        var documentKey = _documentKey!;
        var documentToken = DocumentToken();
        var context = WithDomainHint(await BuildContextAsync(pageIndex, profile.Settings, documentToken));
        var contextFingerprint = context.Fingerprint();

        if (!forceRefresh)
        {
            var cachedOcr = await _ocrCache.TryReadAsync(documentKey, pageIndex, cancellationToken, _ocrCoordinator.EngineVersion);
            var knownOcrFingerprint = cachedOcr is null ? string.Empty : OcrFingerprint(cachedOcr);
            var cached = await _translationCache.TryReadAsync(
                documentKey,
                pageIndex,
                profile.Settings,
                contextFingerprint,
                knownOcrFingerprint,
                cancellationToken);
            if (cached is not null)
            {
                var normalizedText = TranslationMarkdownNormalizer.Normalize(cached.Text);
                progress?.Report(new MarkdownRenderUpdate(normalizedText, TranslationPipelineStage.Final, true));
                return new PageTranslationResult(normalizedText, CacheHit: true, 0,
                    cached.WasReviewed, IsFinal: true, Warning: null);
            }

            // 当前模型无缓存：回退展示任一 provider 的有效译文（换模型后已译页面不再自动重译、
            // 不重复计费；用户显式"重新翻译"才会用当前模型重译该页）。
            var anyProvider = await _translationCache.TryReadAnyProviderAsync(documentKey, pageIndex, cancellationToken);
            if (anyProvider is not null)
            {
                var fallbackText = TranslationMarkdownNormalizer.Normalize(anyProvider.Text);
                progress?.Report(new MarkdownRenderUpdate(fallbackText, TranslationPipelineStage.Final, true));
                return new PageTranslationResult(fallbackText, CacheHit: true, 0,
                    anyProvider.WasReviewed, IsFinal: true,
                    Warning: "当前译文由此前的其他模型生成；点击“重新翻译”使用当前模型重译本页。");
            }
        }

        var key = TranslationJobKey(pageIndex, profile.Settings, contextFingerprint);
        if (forceRefresh && _translationJobs.TryGetValue(key, out var existing) &&
            existing.IsValueCreated && existing.Value.Completion.IsCompleted)
        {
            _translationJobs.TryRemove(key, out _);
        }

        var lazy = _translationJobs.GetOrAdd(
            key,
            _ => new Lazy<PageTranslationJob>(
                () => CreateTranslationJob(
                    key,
                    documentKey,
                    pageIndex,
                    profile,
                    context,
                    contextFingerprint),
                LazyThreadSafetyMode.ExecutionAndPublication));
        var job = lazy.Value;
        using var subscription = job.Subscribe(progress);
        try
        {
            var result = await job.Completion.WaitAsync(cancellationToken);
            return new PageTranslationResult(
                result.Result.Text,
                result.FromCache,
                result.ElapsedMilliseconds,
                result.Result.WasReviewed,
                result.IsFinal,
                result.Warning);
        }
        catch when (job.Completion.IsFaulted || job.Completion.IsCanceled)
        {
            ((ICollection<KeyValuePair<string, Lazy<PageTranslationJob>>>)_translationJobs)
                .Remove(new KeyValuePair<string, Lazy<PageTranslationJob>>(key, lazy));
            throw;
        }
    }

    public void PrefetchNextRender(uint pageIndex)
    {
        if (_document is null || pageIndex + 1 >= _document.PageCount)
        {
            return;
        }
        _prefetchWork?.Cancel();
        _prefetchWork?.Dispose();
        _prefetchWork = CancellationTokenSource.CreateLinkedTokenSource(DocumentToken());
        var token = _prefetchWork.Token;
        var target = pageIndex + 1;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(400, token);
                await GetPageRenderAsync(target, token);
                AppLog.Info($"预渲染完成: 第 {target + 1} 页");
                // 分级预取：渲染后继续预热下一页 OCR（本地推理无 API 成本、可随时取消），
                // 用户翻到该页时 OCR 已就绪，翻译能立刻起跑。翻译预取仍是单独的 opt-in 设置。
                await GetPageDataAsync(target, token);
                AppLog.Info($"预取 OCR 完成: 第 {target + 1} 页");
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                AppLog.Error($"预渲染失败: 第 {target + 1} 页", ex);
            }
        }, token);
    }

    /// <summary>
    /// 空闲时后台预翻译下一页（仅在线模式、设置开启时）。必须在当前页译文落盘后调用：
    /// 下一页的跨页上下文指纹依赖当前页缓存，复用任务去重机制——用户翻到该页时
    /// 直接挂接同一任务或命中缓存，不会重复计费。本地模式不预取（单租约会让前台排队）。
    /// </summary>
    public void PrefetchNextTranslation(uint pageIndex, TranslationProfile profile)
    {
        if (!PrefetchTranslationEnabled || _document is null || profile.Settings.IsLocal ||
            pageIndex + 1 >= _document.PageCount)
        {
            return;
        }
        var target = pageIndex + 1;
        var token = DocumentToken();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(400, token);
                var result = await GetTranslationAsync(target, profile, progress: null, forceRefresh: false, token);
                if (!result.CacheHit)
                {
                    AppLog.Info($"预翻译完成: 第 {target + 1} 页");
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                AppLog.Error($"预翻译失败: 第 {target + 1} 页", ex);
            }
        }, token);
    }

    public async Task<byte[]?> RenderThumbnailAsync(
        uint pageIndex,
        double maxDimension,
        CancellationToken cancellationToken)
    {
        var document = _document;
        if (document is null || pageIndex >= document.PageCount)
        {
            return null;
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            DocumentToken(), cancellationToken);
        // Fair queueing: renders take only a few hundred ms, so thumbnails simply
        // wait their turn. The old foreground-priority spin could starve them
        // indefinitely during prefetch bursts, leaving permanently blank slots.
        await _thumbnailRenderGate.WaitAsync(linked.Token);
        try
        {
            return await RenderBytesOnlyAsync(
                document,
                pageIndex,
                maxDimension,
                BitmapEncoder.JpegEncoderId,
                linked.Token);
        }
        finally
        {
            _thumbnailRenderGate.Release();
        }
    }

    private PageTranslationJob CreateTranslationJob(
        string key,
        string documentKey,
        uint pageIndex,
        TranslationProfile profile,
        DocumentTranslationContext context,
        string contextFingerprint)
    {
        var job = new PageTranslationJob();
        job.Completion = TranslateAndCacheAsync(
            key,
            documentKey,
            pageIndex,
            profile,
            context,
            contextFingerprint,
            job);
        return job;
    }

    private async Task<CachedTranslation> TranslateAndCacheAsync(
        string key,
        string documentKey,
        uint pageIndex,
        TranslationProfile profile,
        DocumentTranslationContext context,
        string contextFingerprint,
        PageTranslationJob job)
    {
        using var work = CancellationTokenSource.CreateLinkedTokenSource(DocumentToken(), job.WorkToken);
        var documentToken = work.Token;
        var timer = Stopwatch.StartNew();

        if (profile.Settings.IsLocal)
        {
            return await TranslateLocalAsync(
                documentKey, pageIndex, profile, context, contextFingerprint, job, documentToken, timer);
        }

        try
        {
            return await TranslateOnlineAndCacheAsync(
                key, documentKey, pageIndex, profile, context, contextFingerprint, job, documentToken, timer);
        }
        catch (Exception ex) when (LocalFallbackEnabled && _localModels.IsTranslationModelInstalled && IsTransientOnlineFailure(ex))
        {
            // 在线瞬时故障（网络/限流/5xx）：自动改用本地模型兜底，结果按本地 provider key 落缓存。
            // 4xx 配置/鉴权错误不兜底——那是用户必须自己修的配置问题。
            AppLog.Error($"在线翻译失败 p{pageIndex + 1}，自动改用本地模型兜底", ex);
            var localProfile = _localTranslator.CreateProfile(profile.Settings.TargetLanguage);
            var fallback = await TranslateLocalAsync(
                documentKey, pageIndex, localProfile, context, contextFingerprint, job, documentToken, timer);
            return fallback with { Warning = $"在线翻译失败，已改用本地模型兜底（{ex.Message}）" };
        }
    }

    private static bool IsTransientOnlineFailure(Exception ex) => ex switch
    {
        TranslationException te => te.StatusCode == 0 || te.StatusCode >= 500 || te.StatusCode is 408 or 429,
        HttpRequestException => true,
        TimeoutException => true,
        _ => false
    };

    private async Task<CachedTranslation> TranslateOnlineAndCacheAsync(
        string key,
        string documentKey,
        uint pageIndex,
        TranslationProfile profile,
        DocumentTranslationContext context,
        string contextFingerprint,
        PageTranslationJob job,
        CancellationToken documentToken,
        Stopwatch timer)
    {
        if (!profile.Settings.IsMultimodal)
        {
            return await TranslateTextOnlyAsync(
                documentKey, pageIndex, profile, context, contextFingerprint, job, documentToken, timer);
        }

        var renderTask = GetPageRenderAsync(pageIndex, documentToken);
        var ocrTask = GetPageDataAsync(pageIndex, documentToken);
        VisualDraftResult? draft = null;
        Exception? draftFailure = null;

        try
        {
            var render = await renderTask;
            using var queuedDraft = CancellationTokenSource.CreateLinkedTokenSource(documentToken, job.PendingToken);
            await _translationGate.WaitAsync(queuedDraft.Token);
            try
            {
                AppLog.Info($"[管线] 视觉草译开始 p{pageIndex + 1}");
                draft = await _translator.TranslateVisualDraftStreamingAsync(
                    profile.Settings, profile.ApiKey,
                    new VisualDraftRequest(render.EncodedImage, render.ImageMediaType, pageIndex + 1, context),
                    new InlineProgress<string>(text => job.Report(
                        new MarkdownRenderUpdate(text, TranslationPipelineStage.Drafting, false))),
                    documentToken);
                AppLog.Info($"[管线] 视觉草译完成 p{pageIndex + 1}");
                await RecordUsageAsync(profile, draft.Usage, documentToken);
                job.Report(new MarkdownRenderUpdate(draft.Markdown, TranslationPipelineStage.OcrRunning, false));
            }
            finally
            {
                _translationGate.Release();
            }
        }
        catch (Exception ex) when (ex is TranslationException or HttpRequestException)
        {
            draftFailure = ex;
            AppLog.Error($"视觉草译失败 p{pageIndex + 1}", ex);
        }

        PageData? data = null;
        Exception? ocrFailure = null;
        try { data = await ocrTask; }
        catch (Exception ex) when (ex is NativeOcrException or DllNotFoundException)
        {
            ocrFailure = ex;
            AppLog.Error($"OCR 失败，使用视觉结果 p{pageIndex + 1}", ex);
        }

        if (draft is null && data is null)
            throw new TranslationException($"视觉翻译和 OCR 均失败：{draftFailure?.Message ?? ocrFailure?.Message}");

        MultimodalTranslationResult result;
        var isFinal = true;
        string? warning = null;
        if (draft is null)
        {
            var render = data!.Render;
            await _translationGate.WaitAsync(documentToken);
            try
            {
                job.Report(new MarkdownRenderUpdate("", TranslationPipelineStage.Reviewing, false));
                result = await _translator.TranslateStreamingAsync(profile.Settings, profile.ApiKey,
                    new MultimodalTranslationRequest(data.SourceText, render.EncodedImage, render.ImageMediaType,
                        pageIndex + 1, context),
                    new InlineProgress<string>(text => job.Report(
                        new MarkdownRenderUpdate(text, TranslationPipelineStage.Reviewing, false))), documentToken);
            }
            finally { _translationGate.Release(); }
        }
        else if (data is null)
        {
            result = new MultimodalTranslationResult(draft.Markdown, draft.Summary, draft.Terms,
                contextFingerprint, WasReviewed: false, OcrAvailable: false);
            warning = "本页 OCR 失败，已保留视觉译文。";
        }
        else if (RequiresFusion(draft, data, context))
        {
            job.Report(new MarkdownRenderUpdate(draft.Markdown, TranslationPipelineStage.Reviewing, false));
            try
            {
                await _translationGate.WaitAsync(documentToken);
                try
                {
                    AppLog.Info($"[管线] 融合校订开始 p{pageIndex + 1}");
                    result = await _translator.FuseStreamingAsync(profile.Settings, profile.ApiKey,
                        new FusionTranslationRequest(draft, data.Ocr, data.SourceText, data.Render.EncodedImage,
                            data.Render.ImageMediaType, pageIndex + 1, context),
                        new InlineProgress<string>(text => job.Report(
                            new MarkdownRenderUpdate(text, TranslationPipelineStage.Reviewing, false))), documentToken);
                }
                finally { _translationGate.Release(); }
            }
            catch (TranslationException ex)
            {
                AppLog.Error($"融合校订失败，保留草译 p{pageIndex + 1}", ex);
                result = new MultimodalTranslationResult(draft.Markdown, draft.Summary, draft.Terms,
                    contextFingerprint, WasReviewed: false, OcrAvailable: true);
                isFinal = false;
                warning = "融合校订失败，当前显示视觉草译；可点击重新翻译。";
            }
        }
        else
        {
            result = new MultimodalTranslationResult(draft.Markdown, draft.Summary, draft.Terms,
                contextFingerprint, WasReviewed: false, OcrAvailable: true);
        }

        result = result with { ContextFingerprint = contextFingerprint };
        if (result.OutputDegraded)
        {
            // 消毒器检出占位符残留或重复退化：文本已清理展示，但不写入缓存，
            // 防止污染随跨页上下文自我复制到后续页面。
            isFinal = false;
            warning ??= "检测到模型输出异常（占位符或重复句），已自动清理；建议点击重新翻译。";
            AppLog.Info($"[管线] 输出退化 p{pageIndex + 1}：占位符/重复句已清理，结果不落缓存");
        }
        if (result.UnappliedTerms.Count > 0 && warning is null)
            warning = $"部分术语未统一（共 {result.UnappliedTerms.Count} 条），可重新翻译";
        await RecordUsageAsync(profile, result.Usage, documentToken);
        if (isFinal)
            await _translationCache.WriteAsync(documentKey, pageIndex, profile.Settings, result,
                data is null ? string.Empty : OcrFingerprint(data.Ocr), documentToken);
        timer.Stop();
        job.Report(new MarkdownRenderUpdate(result.Text, TranslationPipelineStage.Final, isFinal));
        AppLog.Info($"[管线] 自适应翻译完成 p{pageIndex + 1} ({timer.ElapsedMilliseconds}ms, reviewed={result.WasReviewed})");
        return new CachedTranslation(profile.Settings, result, FromCache: false,
            timer.ElapsedMilliseconds, isFinal, warning);
    }

    // 纯文本模型（deepseek-v4-flash / glm-5.2 等）：OCR 必须成功，再单次文本翻译，不带图片。
    private async Task<CachedTranslation> TranslateTextOnlyAsync(
        string documentKey,
        uint pageIndex,
        TranslationProfile profile,
        DocumentTranslationContext context,
        string contextFingerprint,
        PageTranslationJob job,
        CancellationToken documentToken,
        Stopwatch timer)
    {
        PageData data;
        try
        {
            data = await GetPageDataAsync(pageIndex, documentToken);
        }
        catch (Exception ex) when (ex is NativeOcrException or DllNotFoundException)
        {
            AppLog.Error($"纯文本模型 OCR 失败 p{pageIndex + 1}", ex);
            throw new TranslationException($"纯文本模型依赖 OCR，但 OCR 失败：{ex.Message}");
        }

        job.Report(new MarkdownRenderUpdate(string.Empty, TranslationPipelineStage.OcrRunning, false));
        AppLog.Info($"[管线] 纯文本翻译开始 p{pageIndex + 1}");
        await _translationGate.WaitAsync(documentToken);
        MultimodalTranslationResult result;
        try
        {
            job.Report(new MarkdownRenderUpdate(string.Empty, TranslationPipelineStage.Drafting, false));
            result = await _translator.TranslateTextStreamingAsync(
                profile.Settings, profile.ApiKey,
                new TextTranslationRequest(data.SourceText, data.Ocr, pageIndex + 1, context),
                new InlineProgress<string>(text => job.Report(
                    new MarkdownRenderUpdate(text, TranslationPipelineStage.Drafting, false))),
                documentToken);
        }
        finally
        {
            _translationGate.Release();
        }

        result = result with { ContextFingerprint = contextFingerprint };
        await RecordUsageAsync(profile, result.Usage, documentToken);
        // 消毒器检出退化时展示清理后文本但不落缓存（同多模态路径策略）。
        var isFinal = !result.OutputDegraded;
        string? warning = result.OutputDegraded
            ? "检测到模型输出异常（占位符或重复句），已自动清理；建议点击重新翻译。"
            : null;
        if (isFinal)
        {
            await _translationCache.WriteAsync(documentKey, pageIndex, profile.Settings, result,
                OcrFingerprint(data.Ocr), documentToken);
        }
        timer.Stop();
        job.Report(new MarkdownRenderUpdate(result.Text, TranslationPipelineStage.Final, isFinal));
        AppLog.Info($"[管线] 纯文本翻译完成 p{pageIndex + 1} ({timer.ElapsedMilliseconds}ms)");
        return new CachedTranslation(profile.Settings, result, FromCache: false,
            timer.ElapsedMilliseconds, isFinal, warning);
    }

    private async Task<CachedTranslation> TranslateLocalAsync(
        string documentKey,
        uint pageIndex,
        TranslationProfile profile,
        DocumentTranslationContext context,
        string contextFingerprint,
        PageTranslationJob job,
        CancellationToken documentToken,
        Stopwatch timer)
    {
        var data = await GetPageDataAsync(pageIndex, documentToken);
        job.Report(new MarkdownRenderUpdate(string.Empty, TranslationPipelineStage.OcrRunning, false));
        AppLog.Info($"[本地 AI] 翻译开始 p{pageIndex + 1}, provider={profile.Settings.ProviderId}");
        var resumeKey = string.Join('|', documentKey, pageIndex,
            profile.Settings.ProviderCacheIdentity, profile.Settings.TargetLanguage,
            contextFingerprint, OcrFingerprint(data.Ocr));
        var translated = await _localTranslator.TranslateAsync(
            resumeKey,
            profile,
            new TextTranslationRequest(data.SourceText, data.Ocr, pageIndex + 1, context),
            new InlineProgress<MarkdownRenderUpdate>(job.Report),
            documentToken);
        var result = translated.Result with { ContextFingerprint = contextFingerprint };
        // 退化输出（占位符/重复句）不落缓存，避免污染跨页上下文自我复制（同在线策略）。
        var isFinal = translated.IsComplete && !translated.OutputDegraded;
        if (isFinal)
        {
            await _translationCache.WriteAsync(documentKey, pageIndex, profile.Settings, result,
                OcrFingerprint(data.Ocr), documentToken);
        }
        timer.Stop();
        job.Report(new MarkdownRenderUpdate(result.Text, TranslationPipelineStage.Final, isFinal));
        AppLog.Info($"[本地 AI] 翻译结束 p{pageIndex + 1} ({timer.ElapsedMilliseconds}ms, complete={translated.IsComplete}, degraded={translated.OutputDegraded})");
        return new CachedTranslation(profile.Settings, result, FromCache: false,
            timer.ElapsedMilliseconds, isFinal, translated.Warning);
    }

    internal static bool RequiresFusion(
        VisualDraftResult draft,
        PageData data,
        DocumentTranslationContext context)
    {
        var blocks = data.Ocr.Blocks.Where(block => !string.IsNullOrWhiteSpace(block.Text)).ToList();
        if (draft.NeedsReview || blocks.Count == 0) return true;
        if (draft.UnappliedTerms.Count > 0) return true;
        if (blocks.Average(block => block.Confidence) < 0.94 || blocks.Min(block => block.Confidence) < 0.80)
            return true;
        var complexKinds = new[] { "table", "formula", "multicolumn", "footnote", "mixed" };
        if (complexKinds.Any(kind => draft.PageKind.Contains(kind, StringComparison.OrdinalIgnoreCase)))
            return true;
        var normalizedOcr = NormalizeAnchor(data.SourceText);
        if (draft.Anchors.Any(anchor => !normalizedOcr.Contains(NormalizeAnchor(anchor), StringComparison.OrdinalIgnoreCase)))
            return true;
        return draft.Terms.Any(candidate => context.Terms.Any(existing =>
            existing.Source.Equals(candidate.Source, StringComparison.OrdinalIgnoreCase) &&
            !existing.Target.Equals(candidate.Target, StringComparison.Ordinal)));
    }

    private static string NormalizeAnchor(string value) => string.Concat(
        value.Where(character => !char.IsWhiteSpace(character) && !char.IsPunctuation(character)));

    private static string OcrFingerprint(OcrPage page)
    {
        var value = string.Join("\n", page.Blocks.OrderBy(block => block.ReadingOrder)
            .Select(block => $"{block.ReadingOrder}|{block.Confidence:F4}|{block.Text}"));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..20];
    }

    private async Task<DocumentTranslationContext> BuildContextAsync(
        uint pageIndex,
        TranslationSettings settings,
        CancellationToken cancellationToken)
    {
        if (pageIndex == 0 || _documentKey is null)
        {
            return DocumentTranslationContext.Empty;
        }

        for (var candidate = (long)pageIndex - 1; candidate >= 0; candidate--)
        {
            var cached = await _translationCache.TryReadAnyAsync(
                _documentKey,
                (uint)candidate,
                settings,
                cancellationToken);
            if (cached is null || cached.PromptVersion != OpenAiCompatibleTranslator.PromptVersion ||
                cached.FormatVersion != OpenAiCompatibleTranslator.FormatVersion)
            {
                continue;
            }

            var exactPrevious = candidate == pageIndex - 1;
            var previousSource = string.Empty;
            if (exactPrevious)
            {
                if (_pageData.TryGetValue((uint)candidate, out var data) &&
                    data.IsValueCreated && data.Value.IsCompletedSuccessfully)
                {
                    previousSource = data.Value.Result.SourceText;
                }
                else
                {
                    var ocr = await _ocrCache.TryReadAsync(
                        _documentKey,
                        (uint)candidate,
                        cancellationToken,
                        _ocrCoordinator.EngineVersion);
                    if (ocr is not null)
                    {
                        previousSource = JoinBlocks(ocr);
                    }
                }
            }
            return new DocumentTranslationContext(
                cached.Summary,
                cached.Terms,
                previousSource,
                exactPrevious ? cached.Text : string.Empty);
        }
        return DocumentTranslationContext.Empty;
    }

    private async Task<PageRender> RenderPageCoreAsync(
        uint pageIndex,
        CancellationToken cancellationToken)
    {
        var document = _document ?? throw new InvalidOperationException("No document is open.");
        var timer = Stopwatch.StartNew();
        AppLog.Info($"[管线] 渲染开始 p{pageIndex + 1}");
        Interlocked.Increment(ref _foregroundRenderWaiting);
        try
        {
            await _renderGate.WaitAsync(cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _foregroundRenderWaiting);
        }
        try
        {
            var rendered = await RenderBitmapAndBytesAsync(
                document,
                pageIndex,
                FullPageMaxDimension,
                cancellationToken);
            timer.Stop();
            AppLog.Info($"[管线] 渲染完成 p{pageIndex + 1} ({timer.ElapsedMilliseconds}ms)");
            return new PageRender(
                rendered.Bitmap,
                rendered.Bytes,
                "image/jpeg",
                rendered.Bitmap.PixelWidth,
                rendered.Bitmap.PixelHeight,
                timer.ElapsedMilliseconds);
        }
        finally
        {
            _renderGate.Release();
        }
    }

    private async Task<PageData> OcrPageCoreAsync(
        uint pageIndex,
        CancellationToken cancellationToken)
    {
        var render = await GetPageRenderAsync(pageIndex, cancellationToken);
        if (_documentKey is not null)
        {
            var cached = await _ocrCache.TryReadAsync(_documentKey, pageIndex, cancellationToken, _ocrCoordinator.EngineVersion);
            if (cached is not null)
            {
                return new PageData(render, cached, JoinBlocks(cached), 0, OcrCacheHit: true);
            }
        }

        var pixelCount = checked(render.Width * render.Height * 4);
        var pixelBuffer = new Windows.Storage.Streams.Buffer((uint)pixelCount);
        render.Bitmap.CopyToBuffer(pixelBuffer);
        // 8MB 级像素缓冲走 ArrayPool：每页一次的 LOH 分配降为零，长会话显著降 Gen2 压力。
        var pixels = ArrayPool<byte>.Shared.Rent(pixelCount);
        try
        {
            using (var reader = DataReader.FromBuffer(pixelBuffer))
            {
                var chunk = reader.ReadBuffer((uint)pixelCount);
                chunk.CopyTo(0, pixels, 0, pixelCount);
            }

            var timer = Stopwatch.StartNew();
            AppLog.Info($"[管线] OCR 推理开始 p{pageIndex + 1}");
            var ocrPage = await _ocrCoordinator.RecognizeAsync(
                pixels.AsMemory(0, pixelCount), render.Width, render.Height, render.Width * 4,
                OcrWorkPriority.Foreground, cancellationToken);
            timer.Stop();
            AppLog.Info($"[管线] OCR 推理完成 p{pageIndex + 1} ({timer.ElapsedMilliseconds}ms)");
            if (_documentKey is not null)
            {
                await _ocrCache.WriteAsync(_documentKey, pageIndex, ocrPage, cancellationToken);
            }
            return new PageData(render, ocrPage, JoinBlocks(ocrPage), timer.ElapsedMilliseconds, OcrCacheHit: false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(pixels);
        }
    }

    private static async Task<(SoftwareBitmap Bitmap, byte[] Bytes)> RenderBitmapAndBytesAsync(
        PdfDocument document,
        uint pageIndex,
        double maxDimension,
        CancellationToken cancellationToken)
    {
        // BMP 渲染：无 DCT，编码/解码都快且无损——OCR 吃无损位图（优于 JPEG 解码图）。
        using var stream = await PdfPageRenderer.RenderToStreamAsync(
            document, pageIndex, maxDimension, BitmapEncoder.BmpEncoderId, cancellationToken).ConfigureAwait(false);
        var bitmap = await PdfPageRenderer.DecodeBitmapAsync(stream, cancellationToken).ConfigureAwait(false);
        // 页面显示与多模态 API 上传仍需要 JPEG：从无损位图现编一次（这步本来就省不掉）。
        var bytes = await PdfPageRenderer.EncodeJpegAsync(bitmap, cancellationToken).ConfigureAwait(false);
        return (bitmap, bytes);
    }

    private static Task<byte[]> RenderBytesOnlyAsync(
        PdfDocument document,
        uint pageIndex,
        double maxDimension,
        Guid encoderId,
        CancellationToken cancellationToken) =>
        PdfPageRenderer.RenderBytesAsync(document, pageIndex, maxDimension, encoderId, cancellationToken);

    private async Task<T> GetOrCreateTaskAsync<T>(
        ConcurrentDictionary<uint, Lazy<Task<T>>> dictionary,
        uint key,
        Func<CancellationToken, Task<T>> factory,
        CancellationToken callerToken)
    {
        while (true)
        {
            var linked = CancellationTokenSource.CreateLinkedTokenSource(DocumentToken(), callerToken);
            var candidate = new Lazy<Task<T>>(
                async () =>
                {
                    using (linked)
                    {
                        return await factory(linked.Token);
                    }
                },
                LazyThreadSafetyMode.ExecutionAndPublication);
            var lazy = dictionary.GetOrAdd(key, candidate);
            if (!ReferenceEquals(lazy, candidate))
            {
                linked.Dispose();
            }
            try
            {
                return await lazy.Value.WaitAsync(callerToken);
            }
            catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
            {
                // 取消来自 DocumentToken（换文档/模式切换重置）：若当前 document token
                // 仍是已取消状态，直接放弃而不是自旋重建注定失败的 linked CTS；
                // 若已换上新 token，则正常重试。
                ((ICollection<KeyValuePair<uint, Lazy<Task<T>>>>)dictionary)
                    .Remove(new KeyValuePair<uint, Lazy<Task<T>>>(key, lazy));
                if (DocumentToken().IsCancellationRequested)
                {
                    throw;
                }
            }
            catch
            {
                if (lazy.Value.IsFaulted || lazy.Value.IsCanceled)
                {
                    ((ICollection<KeyValuePair<uint, Lazy<Task<T>>>>)dictionary)
                        .Remove(new KeyValuePair<uint, Lazy<Task<T>>>(key, lazy));
                }
                throw;
            }
        }
    }

    private void PruneToWindow(uint around)
    {
        foreach (var key in _renders.Keys.ToArray())
        {
            if (Math.Abs((long)key - around) > FullPageCacheRadius)
            {
                EvictPage(key);
            }
        }
    }

    private void EvictPage(uint key)
    {
        _pageData.TryRemove(key, out var data);
        if (!_renders.TryRemove(key, out var render))
        {
            return;
        }
        if (data is { IsValueCreated: true } && !data.Value.IsCompleted)
        {
            _ = data.Value.ContinueWith(
                _ => DisposeRender(render),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        else
        {
            DisposeRender(render);
        }
    }

    private static void DisposeRender(Lazy<Task<PageRender>> render)
    {
        if (!render.IsValueCreated)
        {
            return;
        }
        if (render.Value.IsCompletedSuccessfully)
        {
            render.Value.Result.Bitmap.Dispose();
        }
        else if (!render.Value.IsCompleted)
        {
            _ = render.Value.ContinueWith(
                task =>
                {
                    if (task.IsCompletedSuccessfully)
                    {
                        task.Result.Bitmap.Dispose();
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private static string TranslationJobKey(
        uint pageIndex,
        TranslationSettings settings,
        string contextFingerprint) =>
        $"{pageIndex}|{settings.ProviderCacheIdentity}|{settings.TargetLanguage}|{contextFingerprint}";

    private CancellationToken DocumentToken() =>
        _documentWork?.Token ?? throw new InvalidOperationException("No document is open.");

    private void ThrowIfNoDocument()
    {
        if (_document is null)
        {
            throw new InvalidOperationException("No document is open.");
        }
    }

    private static string JoinBlocks(OcrPage page) => string.Join(
        "\n\n",
        page.Blocks.OrderBy(block => block.ReadingOrder).Select(block => block.Text));

    public void Dispose()
    {
        CloseDocument();
        _renderGate.Dispose();
        _translationGate.Dispose();
    }
}

internal sealed record PageRender(
    SoftwareBitmap Bitmap,
    byte[] EncodedImage,
    string ImageMediaType,
    int Width,
    int Height,
    long RenderMilliseconds);

internal sealed record PageData(
    PageRender Render,
    OcrPage Ocr,
    string SourceText,
    long OcrMilliseconds,
    bool OcrCacheHit);

internal sealed record PageTranslationResult(
    string Text,
    bool CacheHit,
    long Milliseconds,
    bool WasReviewed,
    bool IsFinal,
    string? Warning);

internal sealed record CachedTranslation(
    TranslationSettings Settings,
    MultimodalTranslationResult Result,
    bool FromCache,
    long ElapsedMilliseconds,
    bool IsFinal,
    string? Warning);

internal sealed class PageTranslationJob
{
    private readonly object _gate = new();
    private readonly HashSet<IProgress<MarkdownRenderUpdate>> _observers = [];
    private readonly CancellationTokenSource _pendingWork = new();
    private MarkdownRenderUpdate? _latestUpdate;
    private long _contentVersion;

    public string TaskId { get; } = Guid.NewGuid().ToString("N");

    public Task<CachedTranslation> Completion { get; set; } = null!;
    public CancellationToken PendingToken => _pendingWork.Token;
    public CancellationToken WorkToken => _pendingWork.Token;

    public void Cancel() => _pendingWork.Cancel();

    public IDisposable Subscribe(IProgress<MarkdownRenderUpdate>? progress)
    {
        lock (_gate)
        {
            if (progress is not null)
            {
                _observers.Add(progress);
                if (_latestUpdate is not null)
                {
                    progress.Report(_latestUpdate);
                }
            }
        }
        return new ActionSubscription(() =>
        {
            lock (_gate)
            {
                if (progress is not null)
                {
                    _observers.Remove(progress);
                }
                // 订阅者（页面）离开不再取消排队/进行中的任务：快速翻页时，跳过的页面
                // 应继续在后台翻译并原子写入缓存，回到该页直接复用。
                // 真正的取消入口：CancelActiveTranslations（模式切换）与 CloseDocument。
            }
        });
    }

    public void Report(MarkdownRenderUpdate update)
    {
        IProgress<MarkdownRenderUpdate>[] observers;
        lock (_gate)
        {
            update = update with { TaskId = TaskId, ContentVersion = ++_contentVersion };
            _latestUpdate = update;
            observers = _observers.ToArray();
        }
        foreach (var observer in observers)
        {
            observer.Report(update);
        }
    }

    private sealed class ActionSubscription(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }

}

internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
