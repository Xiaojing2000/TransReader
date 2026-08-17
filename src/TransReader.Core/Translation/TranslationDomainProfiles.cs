namespace TransReader.Core.Translation;

/// <summary>学科领域 profile：文献库分类产出 domain 键，翻译时经 <see cref="TranslationDomainProfiles.Find"/> 注入 prompt。</summary>
public sealed record TranslationDomainProfile(string Key, string DisplayName, string PromptHint);

/// <summary>领域 profile 注册表。键集合与文献库分类的 domain 枚举一致；未知/空一律回落到 general。</summary>
public static class TranslationDomainProfiles
{
    private const string GenericHintTemplate = "本书为{0}领域文献：术语采用该领域标准译名，保持该学科表达规范。";

    private static readonly TranslationDomainProfile General = new("general", "通用", string.Empty);

    private static readonly object OverridesGate = new();
    private static IReadOnlyDictionary<string, string> _overrides = new Dictionary<string, string>();

    /// <summary>用户自定义领域提示词覆盖（键=领域键，值=提示文本；空白文本的条目会被丢弃，等于无覆盖）。</summary>
    public static void SetOverrides(IReadOnlyDictionary<string, string>? overrides)
    {
        lock (OverridesGate)
        {
            _overrides = overrides is null
                ? new Dictionary<string, string>()
                : overrides.Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>某领域的用户覆盖提示词（经 <see cref="Find"/> 归一到已知键后查表）；无覆盖返回 null。</summary>
    public static string? OverrideFor(string? domain)
    {
        var key = Find(domain)?.Key ?? General.Key;
        lock (OverridesGate)
        {
            return _overrides.TryGetValue(key, out var custom) ? custom : null;
        }
    }

    /// <summary>某领域的生效提示词：用户覆盖优先于内置默认；未知/空领域 → general（默认空提示）。</summary>
    public static string EffectiveHint(string? domain)
    {
        var profile = Find(domain) ?? General;
        return OverrideFor(profile.Key) ?? profile.PromptHint;
    }

    public static IReadOnlyList<TranslationDomainProfile> All { get; } =
    [
        new TranslationDomainProfile("math", "数学",
            "本书为数学文献：公式一律 LaTeX 精确转写，定理、定义、证明、引理、编号与交叉引用的结构严格保留，术语采用标准数学译名，证明步骤清晰分行。"),
        new TranslationDomainProfile("computer_science", "计算机科学",
            "本书为计算机科学文献：代码、命令、API、协议与框架名称保留英文及原格式，术语采用行业标准译名，流程与表格描述保持结构化。"),
        CreateGeneric("physics", "物理"),
        CreateGeneric("engineering", "工程"),
        CreateGeneric("medicine", "医学"),
        CreateGeneric("literature", "文学"),
        CreateGeneric("history", "历史"),
        CreateGeneric("social_science", "社科"),
        CreateGeneric("business", "商业"),
        General
    ];

    /// <summary>按键查找 profile；null/未知/general 均返回 general 项（PromptHint 为空，调用方据此决定是否追加）。</summary>
    public static TranslationDomainProfile? Find(string? domain)
    {
        if (!string.IsNullOrWhiteSpace(domain))
        {
            foreach (var profile in All)
            {
                if (profile.Key.Equals(domain.Trim(), StringComparison.OrdinalIgnoreCase)) return profile;
            }
        }
        return General;
    }

    private static TranslationDomainProfile CreateGeneric(string key, string displayName) =>
        new(key, displayName, string.Format(System.Globalization.CultureInfo.InvariantCulture, GenericHintTemplate, displayName));
}
