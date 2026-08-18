namespace TransReader.App.Services;

/// <summary>
/// 内置翻译模型预设。用户可在设置对话框中切换，也可基于预设改写为自定义配置。
/// 预设只含公开端点；API Key 一律写入 Windows 凭据库，绝不进入配置文件或代码仓库。
/// </summary>
internal sealed record TranslationModelPreset(
    string Id,
    string DisplayName,
    string HomepageUrl,
    string BaseUrl,
    string Model,
    string AuthenticationMode,
    bool IsMultimodal,
    double Temperature = 0.1,
    bool DisableThinking = true);

internal static class TranslationModelPresets
{
    public static IReadOnlyList<TranslationModelPreset> Defaults { get; } = new[]
    {
        new TranslationModelPreset(
            "mimo",
            "MiMo V2.5",
            "https://platform.xiaomimimo.com",
            "https://api.xiaomimimo.com/v1",
            "mimo-v2.5",
            "bearer",
            IsMultimodal: true,
            DisableThinking: false),
        new TranslationModelPreset(
            "kimi",
            "Kimi K2.5",
            "https://platform.kimi.com",
            "https://api.moonshot.cn/v1",
            "kimi-k2.5",
            "bearer",
            IsMultimodal: false,
            Temperature: 0.6,
            DisableThinking: false),
        new TranslationModelPreset(
            "glm",
            "GLM 5.2",
            "https://bigmodel.cn",
            "https://open.bigmodel.cn/api/paas/v4",
            "glm-5.2",
            "bearer",
            IsMultimodal: false,
            Temperature: 0.6,
            DisableThinking: false),
        new TranslationModelPreset(
            "deepseek",
            "DeepSeek V4 Flash",
            "https://platform.deepseek.com",
            "https://api.deepseek.com",
            "deepseek-v4-flash",
            "bearer",
            IsMultimodal: false,
            Temperature: 0.6,
            DisableThinking: false),
    };

    /// <summary>UI 中用于表示用户改写预设后得到的自定义条目。</summary>
    public const string CustomId = "custom";

    public const string CustomDisplayName = "自定义…";

    public static TranslationModelPreset? Find(string? id) =>
        string.IsNullOrEmpty(id)
            ? null
            : Defaults.FirstOrDefault(p => p.Id == id);
}
