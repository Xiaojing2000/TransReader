using System.Text;
using System.Text.Json;
using TransReader.Core;
using TransReader.Core.Library;
using TransReader.Core.Ocr;
using TransReader.Core.Storage;
using TransReader.Core.Translation;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace TransReader.App.Services;

/// <summary>Uses local OCR plus the local Qwen model to analyze the first five pages.</summary>
internal sealed class LibraryClassificationService
{
    private const int AnalysisPageLimit = 5;
    private const int MaximumContextCharacters = 10000;
    private const string PromptVersion = "library-local-v2";
    private static readonly int[] RetryBackoffSeconds = [1, 3];
    private readonly PageOcrCache _ocrCache;
    private readonly OcrCoordinator _ocrCoordinator;
    private readonly LocalModelManager _models;
    private readonly OpenAiCompatibleTranslator _translator;

    public LibraryClassificationService(
        PageOcrCache ocrCache,
        OcrCoordinator ocrCoordinator,
        LocalModelManager models,
        OpenAiCompatibleTranslator translator)
    {
        _ocrCache = ocrCache;
        _ocrCoordinator = ocrCoordinator;
        _models = models;
        _translator = translator;
    }

    /// <summary>整理模型来源："local"（本地 Qwen3）/ 其他值（在线，由 <see cref="ResolveOnlineProfileAsync"/> 解析）。</summary>
    public string AnalysisSource { get; set; } = "local";

    /// <summary>在线整理时的 profile 解析（follow/钉选由宿主实现；返回 null 表示未配置）。</summary>
    public Func<Task<TranslationProfile?>>? ResolveOnlineProfileAsync { get; set; }

    /// <summary>当前来源下整理功能是否就绪（本地=模型已装；在线=profile 已配置）。</summary>
    public async Task<bool> IsReadyAsync()
    {
        if (AnalysisSource == "local") return _models.IsInstalled;
        var profile = ResolveOnlineProfileAsync is null ? null : await ResolveOnlineProfileAsync();
        return profile is { IsConfigured: true };
    }

    public async Task<LibraryClassificationAnalysis?> AnalyzeAsync(
        LibraryDocument document,
        IReadOnlyList<LibraryFolder> folders,
        LocalAiPriority priority,
        CancellationToken cancellationToken)
    {
        var useLocal = AnalysisSource == "local";
        if (useLocal && !_models.IsInstalled) throw new LocalAiNotInstalledException(
            "文献正在等待本地 AI 模型安装，安装后会自动继续分析。");

        TranslationSettings settings;
        string apiKey;
        LocalAiSession? session = null;
        try
        {
            if (useLocal)
            {
                session = await _models.OpenSessionAsync(priority, cancellationToken);
                settings = new TranslationSettings(
                    session.BaseUri.AbsoluteUri,
                    LocalAiManifest.ModelId,
                    "简体中文",
                    "none",
                    IsMultimodal: false,
                    ProviderId: LocalAiManifest.ProviderId,
                    CacheIdentity: LocalAiManifest.CacheIdentity,
                    Provider: TranslationProvider.Local);
                apiKey = string.Empty;
            }
            else
            {
                // 在线整理：使用宿主解析的 profile（follow=当前活动模型，或钉选的 provider）。
                var profile = ResolveOnlineProfileAsync is null ? null : await ResolveOnlineProfileAsync();
                if (profile is null || !profile.IsConfigured)
                    throw new InvalidOperationException("文献库整理所选的在线模型未配置。请在 AI 中心检查文献库整理来源。");
                settings = profile.Settings;
                apiKey = profile.ApiKey;
            }

            var pageTexts = await ReadFrontPageTextAsync(document, cancellationToken);
            var existingPaths = folders.Select(folder => folder.Path).Where(path => path.Length > 0).ToArray();
            var context = BuildContext(document, existingPaths, pageTexts);

            var system = """
                你是文献库的归档助手。根据文件信息和前五页 OCR，返回严格 JSON。
                目录最多三级，必须优先精确复用现有目录；确实没有合适目录时 needsNewFolder=true。
                不确定时降低 confidence；不要编造作者和年份，未知作者用空字符串，未知年份用 0。
                中文摘要不超过120字，reason 不超过60字，标签最多5个。
                判断文献所属学科领域，domain 从给定枚举中选最贴近的一个；无法判断用 general。
                """;
            // 复用 OpenAiCompatibleTranslator 的 HttpClient + 鉴权；5xx/连接/超时按 [1,3] 秒退避重试。
            // 在线服务若不接受 response_format=json_schema 或 thinking 字段（400），逐项降级重试。
            var includeSchema = true;
            // kimi-k3 等推理型网关模型：DisableThinking=false 的 profile 绝不发送 thinking 字段
            // （网关会以误导性错误拒绝）；其他 400 场景由下方逐项降级兜底。
            var includeThinking = !useLocal && settings.DisableThinking;
            Exception? lastError = null;
            for (var attempt = 0; attempt <= RetryBackoffSeconds.Length; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var body = await _translator.PostJsonSchemaAsync(settings, apiKey,
                        BuildRequestBody(settings.Model, settings.Temperature, system, context, useLocal, includeSchema, includeThinking), cancellationToken);
                    return ParseResult(body);
                }
                catch (TranslationException ex) when (includeThinking && ex.StatusCode == 400)
                {
                    // 部分服务不认识 thinking 字段：先剥离它再试。
                    includeThinking = false;
                    lastError = ex;
                }
                catch (TranslationException ex) when (includeSchema && ex.StatusCode == 400)
                {
                    includeSchema = false;
                    lastError = ex;
                }
                catch (TranslationException ex) when (attempt < RetryBackoffSeconds.Length &&
                                                     (ex.StatusCode == 0 || ex.StatusCode >= 500))
                {
                    lastError = ex;
                    await Task.Delay(TimeSpan.FromSeconds(RetryBackoffSeconds[attempt]), cancellationToken).ConfigureAwait(false);
                }
            }
            throw lastError ?? new InvalidOperationException("文献分析失败。");
        }
        finally
        {
            session?.Dispose();
        }
    }

    private static object BuildRequestBody(string model, double temperature, string system, string context, bool isLocal, bool includeSchema, bool includeThinking)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = new object[]
            {
                new { role = "system", content = system },
                new { role = "user", content = isLocal ? $"/no_think\n{context}" : context }
            },
            // 温度必须跟随 profile（如 kimi-k3 只允许 0.6），硬编码 0.1 会被网关 400 拒绝。
            ["temperature"] = temperature,
            ["stream"] = false,
            ["max_completion_tokens"] = isLocal ? 2000 : 8192,
        };
        if (includeThinking)
        {
            // 结构化抽取任务必须关掉推理输出：否则推理型模型会把 token 预算耗在思考上、content 为空。
            // 注意：kimi-k3 等网关会拒绝该字段（400）——调用方只在 profile.DisableThinking 时才传。
            body["thinking"] = new { type = "disabled" };
        }
        if (includeSchema)
        {
            body["response_format"] = new
            {
                type = "json_schema",
                json_schema = new
                {
                    name = "library_analysis",
                    strict = true,
                    schema = new
                    {
                        type = "object",
                        properties = new
                        {
                            suggestedPath = new { type = "array", items = new { type = "string" }, maxItems = 3 },
                            needsNewFolder = new { type = "boolean" },
                            confidence = new { type = "number", minimum = 0, maximum = 1 },
                            reason = new { type = "string" },
                            title = new { type = "string" },
                            authors = new { type = "string" },
                            publicationYear = new { type = "integer" },
                            summary = new { type = "string" },
                            tags = new { type = "array", items = new { type = "string" }, maxItems = 5 },
                            domain = new { type = "string", @enum = TranslationDomainProfiles.All.Select(profile => profile.Key).ToArray() }
                        },
                        required = new[] { "suggestedPath", "needsNewFolder", "confidence", "reason", "title", "authors", "publicationYear", "summary", "tags", "domain" },
                        additionalProperties = false
                    }
                }
            };
        }
        return body;
    }

    private async Task<IReadOnlyList<string>> ReadFrontPageTextAsync(
        LibraryDocument document,
        CancellationToken cancellationToken)
    {
        var result = new List<string>();
        StorageFile? file = null;
        PdfDocument? pdf = null;
        var count = Math.Min(document.PageCount, AnalysisPageLimit);
        for (uint pageIndex = 0; pageIndex < count; pageIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cached = await _ocrCache.TryReadAsync(document.ContentHash, pageIndex, cancellationToken, _ocrCoordinator.EngineVersion);
            if (cached is null)
            {
                file ??= await StorageFile.GetFileFromPathAsync(document.ManagedPath);
                pdf ??= await PdfDocument.LoadFromFileAsync(file);
                cached = await RenderAndRecognizeAsync(pdf, pageIndex, cancellationToken);
                await _ocrCache.WriteAsync(document.ContentHash, pageIndex, cached, cancellationToken);
            }
            var text = string.Join("\n", cached.Blocks.OrderBy(block => block.ReadingOrder)
                .Select(block => block.Text).Where(text => !string.IsNullOrWhiteSpace(text)));
            result.Add(TextUtil.LimitTrimmed(text, MaximumContextCharacters / AnalysisPageLimit));
        }
        return result;
    }

    private async Task<OcrPage> RenderAndRecognizeAsync(
        PdfDocument document,
        uint pageIndex,
        CancellationToken cancellationToken)
    {
        const double maxDimension = 1600d;
        using var stream = await PdfPageRenderer.RenderToStreamAsync(
            document, pageIndex, maxDimension, BitmapEncoder.PngEncoderId, cancellationToken).ConfigureAwait(false);
        using var bitmap = await PdfPageRenderer.DecodeBitmapAsync(stream, cancellationToken).ConfigureAwait(false);
        var buffer = new Windows.Storage.Streams.Buffer((uint)(bitmap.PixelWidth * bitmap.PixelHeight * 4));
        bitmap.CopyToBuffer(buffer);
        var pixels = new byte[buffer.Length];
        using (var reader = DataReader.FromBuffer(buffer)) reader.ReadBytes(pixels);
        return await _ocrCoordinator.RecognizeAsync(
            pixels, bitmap.PixelWidth, bitmap.PixelHeight, bitmap.PixelWidth * 4,
            OcrWorkPriority.Background, cancellationToken);
    }

    private static string BuildContext(
        LibraryDocument document,
        IReadOnlyList<string> existingPaths,
        IReadOnlyList<string> pages)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"文件名：{Path.GetFileName(document.SourcePaths.FirstOrDefault() ?? document.ManagedPath)}");
        builder.AppendLine($"页数：{document.PageCount}");
        builder.AppendLine("现有目录：");
        builder.AppendLine(existingPaths.Count == 0 ? "（尚无目录）" : string.Join("\n", existingPaths));
        for (var index = 0; index < pages.Count; index++)
        {
            builder.AppendLine($"[第 {index + 1} 页 OCR]");
            builder.AppendLine(pages[index]);
        }
        return TextUtil.LimitTrimmed(builder.ToString(), MaximumContextCharacters);
    }

    private static LibraryClassificationAnalysis ParseResult(string body)
    {
        using var envelope = JsonDocument.Parse(body);
        var content = envelope.RootElement.GetProperty("choices")[0].GetProperty("message")
            .GetProperty("content").GetString() ?? throw new JsonException("本地模型返回内容为空。");
        var json = ExtractJsonObject(content) ?? throw new JsonException(
            $"模型没有返回 JSON 对象。响应片段：{content[..Math.Min(200, content.Length)]}");
        using var parsed = JsonDocument.Parse(json);
        var root = parsed.RootElement;
        var path = root.GetProperty("suggestedPath").EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!.Trim()).Where(value => value.Length > 0).Take(3).ToList();
        var tags = root.GetProperty("tags").EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!.Trim()).Where(value => value.Length > 0).Distinct().Take(5).ToList();
        int? year = root.TryGetProperty("publicationYear", out var yearElement) &&
                    yearElement.ValueKind == JsonValueKind.Number && yearElement.TryGetInt32(out var parsedYear) &&
                    parsedYear > 0
            ? parsedYear
            : null;
        // domain 缺失或不在枚举内 → general；合法值取规范键。
        var domain = TranslationDomainProfiles.Find(ReadString(root, "domain"))?.Key ?? "general";
        return new LibraryClassificationAnalysis(
            path,
            root.GetProperty("needsNewFolder").GetBoolean(),
            Math.Clamp(root.GetProperty("confidence").GetDouble(), 0, 1),
            TextUtil.LimitTrimmed(ReadString(root, "reason"), 60),
            ReadString(root, "title"),
            ReadString(root, "authors"),
            year,
            TextUtil.LimitTrimmed(ReadString(root, "summary"), 120),
            tags,
            $"local:{LocalAiManifest.ModelId}:{LocalAiManifest.ModelSha256[..12]}:{PromptVersion}",
            domain);
    }

    private static string ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? string.Empty
            : string.Empty;

    private static string? ExtractJsonObject(string text)
    {
        var start = text.IndexOf('{');
        if (start < 0) return null;
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var index = start; index < text.Length; index++)
        {
            var character = text[index];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (character == '\\') escaped = true;
                else if (character == '"') inString = false;
                continue;
            }
            if (character == '"') inString = true;
            else if (character == '{') depth++;
            else if (character == '}' && --depth == 0) return text[start..(index + 1)];
        }
        return null;
    }
}
