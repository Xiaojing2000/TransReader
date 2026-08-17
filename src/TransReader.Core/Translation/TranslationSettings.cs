namespace TransReader.Core.Translation;

public enum TranslationExecutionMode
{
    Online,
    Local
}

/// <summary>显式 provider 类型，替代旧 "local-" 字符串前缀分发。</summary>
public enum TranslationProvider
{
    Online,
    Local
}

// 模态标记：true=多模态（图文），false=纯文本（仅 OCR 文本）。默认 true 保持旧行为兼容。
public sealed record TranslationSettings(
    string BaseUrl,
    string Model,
    string TargetLanguage,
    string AuthenticationMode,
    bool IsMultimodal = true,
    double Temperature = 0.1,
    bool DisableThinking = true,
    string ProviderId = "",
    string CacheIdentity = "",
    TranslationProvider Provider = TranslationProvider.Online)
{
    public static TranslationSettings MiMoDefault { get; } = new(
        "https://api.xiaomimimo.com/v1",
        "mimo-v2.5",
        "简体中文",
        "api-key",
        IsMultimodal: true);

    public string GetChatCompletionsUrl()
    {
        var value = BaseUrl.Trim().TrimEnd('/');
        return value.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? value
            : $"{value}/chat/completions";
    }

    // 读显式 Provider，不再靠 "local-" 字符串前缀启发式（旧 JSON 无 Provider 时默认 Online，
    // 本地 profile 由 LocalTextTranslationService.CreateProfile / 文献分类显式设 Local）。
    public bool IsLocal => Provider == TranslationProvider.Local;

    public string ProviderCacheIdentity => string.IsNullOrWhiteSpace(CacheIdentity)
        ? $"{BaseUrl}|{Model}"
        : CacheIdentity.Trim();
}
