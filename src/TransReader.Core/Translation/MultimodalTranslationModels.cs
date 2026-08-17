using System.Security.Cryptography;
using System.Text;
using TransReader.Core.Ocr;

namespace TransReader.Core.Translation;

public sealed record TranslationTerm(string Source, string Target);

/// <summary>单次请求的 token 用量（来自流式响应的 usage 字段，仅在线模式统计）。</summary>
public sealed record TranslationUsage(int PromptTokens, int CompletionTokens)
{
    public int TotalTokens => PromptTokens + CompletionTokens;
}

internal sealed record StreamingResponse(string Raw, TranslationUsage? Usage);

public sealed record DocumentTranslationContext(
    string Summary,
    IReadOnlyList<TranslationTerm> Terms,
    string PreviousSourceText,
    string PreviousTranslation,
    string Domain = "")
{
    public static DocumentTranslationContext Empty { get; } = new(string.Empty, [], string.Empty, string.Empty, string.Empty);

    public string Fingerprint()
    {
        var value = string.Join("\n", Summary,
            string.Join("|", Terms.Select(term => $"{term.Source}={term.Target}")),
            PreviousSourceText,
            PreviousTranslation,
            Domain);
        // 用户自定义的领域提示词参与指纹（改提示词 → 该领域缓存自动失效重译）；
        // 无覆盖时保持原指纹不变，避免既有缓存被无谓清空。
        var hintOverride = TranslationDomainProfiles.OverrideFor(Domain);
        if (hintOverride is not null)
        {
            value += "\n" + hintOverride;
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..20];
    }
}

public sealed record MultimodalTranslationRequest(
    string SourceText,
    ReadOnlyMemory<byte> PageImage,
    string ImageMediaType,
    uint PageNumber,
    DocumentTranslationContext Context);

public sealed record VisualDraftRequest(
    ReadOnlyMemory<byte> PageImage,
    string ImageMediaType,
    uint PageNumber,
    DocumentTranslationContext Context);

public sealed record VisualDraftResult(
    string Markdown,
    string Genre,
    string PageKind,
    bool NeedsReview,
    IReadOnlyList<string> Anchors,
    string Summary,
    IReadOnlyList<TranslationTerm> Terms)
{
    /// <summary>草译未命中的术语来源列表（由 <see cref="OpenAiCompatibleTranslator"/> 后验填充）。</summary>
    public IReadOnlyList<string> UnappliedTerms { get; init; } = Array.Empty<string>();
    /// <summary>本次草译请求的 token 用量（仅在线；本地为 null）。</summary>
    public TranslationUsage? Usage { get; init; }
}

public sealed record FusionTranslationRequest(
    VisualDraftResult Draft,
    OcrPage Ocr,
    string SourceText,
    ReadOnlyMemory<byte> PageImage,
    string ImageMediaType,
    uint PageNumber,
    DocumentTranslationContext Context);

/// <summary>
/// 纯文本模型（如 deepseek-v4-flash / glm-5.2）翻译请求：仅 OCR 文本，不带页面图片。
/// </summary>
public sealed record TextTranslationRequest(
    string SourceText,
    OcrPage Ocr,
    uint PageNumber,
    DocumentTranslationContext Context);

public sealed record MultimodalTranslationResult(
    string Text,
    string Summary,
    IReadOnlyList<TranslationTerm> Terms,
    string ContextFingerprint,
    bool WasReviewed = false,
    bool OcrAvailable = true,
    string FormatVersion = "markdown-v2")
{
    /// <summary>最终译文未命中的术语来源列表（由 <see cref="OpenAiCompatibleTranslator"/> 后验填充）。</summary>
    public IReadOnlyList<string> UnappliedTerms { get; init; } = Array.Empty<string>();
    /// <summary>本次翻译请求的 token 用量（仅在线；本地为 null）。</summary>
    public TranslationUsage? Usage { get; init; }
    /// <summary>消毒器检出占位符残留或重复退化：文本已清理，但输出不可信，调用方不得写入缓存。</summary>
    public bool OutputDegraded { get; init; }
}

/// <summary>翻译提示词上下文长度上限（收口散落的魔法数，在线/本地/问答共用）。</summary>
internal static class ContextLimits
{
    public const int Summary = 600;
    public const int Terms = 1800;
    public const int TermsTake = 30;
    public const int PreviousTranslation = 2000;
    public const int PreviousSourceText = 2000;
    public const int DraftMarkdown = 6000;
    public const int SourceText = 8000;
    public const int OcrBlocksTake = 40;
}

public enum TranslationPipelineStage
{
    Drafting,
    OcrRunning,
    Reviewing,
    Final
}

public sealed record MarkdownRenderUpdate(
    string Markdown,
    TranslationPipelineStage Stage,
    bool IsFinal,
    bool AutoFollow = true,
    string TaskId = "",
    long ContentVersion = 0,
    int Step = 0,
    int StepCount = 0);
