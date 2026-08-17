namespace TransReader.App.Services;

/// <summary>
/// 内置翻译模型预设。用户可在设置对话框中切换，也可基于预设改写为自定义配置。
/// 预设只含公开端点；API Key 一律写入 Windows 凭据库，绝不进入配置文件或代码仓库。
/// </summary>
internal sealed record TranslationModelPreset(
    string Id,
    string DisplayName,
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
            "MiMo 2.5",
            "https://api.xiaomimimo.com/v1",
            "mimo-v2.5",
            "api-key",
            IsMultimodal: true),
        new TranslationModelPreset(
            "kimi-k3",
            "Kimi K2",
            "https://api.moonshot.cn/v1",
            "kimi-k2-0905-preview",
            "bearer",
            IsMultimodal: false,
            Temperature: 0.6,
            DisableThinking: false),
        new TranslationModelPreset(
            "deepseek",
            "DeepSeek Chat",
            "https://api.deepseek.com/v1",
            "deepseek-chat",
            "bearer",
            IsMultimodal: false,
            Temperature: 0.6,
            DisableThinking: false),
        new TranslationModelPreset(
            "glm",
            "GLM 4 Flash",
            "https://open.bigmodel.cn/api/paas/v4",
            "glm-4-flash",
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
