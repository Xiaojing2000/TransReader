using System.Text;
using System.Text.RegularExpressions;

namespace TransReader.Core.Translation;

/// <summary>
/// 模型输出消毒：剥离渲染层占位符/控制标记残留，并检测句子级重复退化。
/// 占位符（TRMATHPLACEHOLDER…END、TRUNFINISHED…END）只属于前端渲染层（reader.js 的
/// 数学保护机制）；模型输出此类字面量说明发生了退化或幻觉，必须清理并标记，
/// 避免写入缓存后随跨页上下文把污染自我复制到后续页面。
/// </summary>
public static partial class TranslationOutputSanitizer
{
    /// <summary>清理译文文本；同时报告是否剥离过占位符/控制标记、是否存在句子级重复退化。</summary>
    public static SanitizeResult Sanitize(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return new SanitizeResult(string.Empty, false, false);
        }

        var hadArtifacts = ArtifactToken().IsMatch(markdown) || ContainsPrivateUse(markdown);
        var cleaned = StripPrivateUse(ArtifactToken().Replace(markdown, string.Empty));
        // 修复剥离后的确定伪影：连续枚举顿号折叠、两侧带空格的孤立顿号、行首孤立顿号。
        // 中文正文不会以空格包围顿号，出现即剥离伪影。保守优先，不做激进改写。
        cleaned = CollapsedEnumeration().Replace(cleaned, "、");
        cleaned = SpacedEnumeration().Replace(cleaned, " ");
        cleaned = StrayEnumerationAtLineStart().Replace(cleaned, string.Empty);
        return new SanitizeResult(cleaned.Trim(), hadArtifacts, HasDegenerateRepetition(cleaned));
    }

    private static bool HasDegenerateRepetition(string text)
    {
        // 规则 1：同一 ≥10 字句子出现 ≥3 次视为退化（模型陷入重复循环的典型特征）。
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var segment in SentenceSplitter().Split(text))
        {
            var sentence = segment.Trim();
            if (sentence.Length < 10) continue;
            counts[sentence] = counts.TryGetValue(sentence, out var count) ? count + 1 : 1;
            if (counts[sentence] >= 3) return true;
        }
        // 规则 2：归一化后任意 ≥20 字子串出现 ≥5 次——变量名逐次变化的"同句重复"退化
        // （句级规则抓不到：每次重复的变量/编号不同）。阈值从严，避免误伤正文正常复现的短语。
        var normalized = string.Concat(text.Where(character => !char.IsWhiteSpace(character)));
        const int shingleLength = 20;
        if (normalized.Length < shingleLength * 5) return false;
        var shingles = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index + shingleLength <= normalized.Length; index++)
        {
            var shingle = normalized.Substring(index, shingleLength);
            shingles[shingle] = shingles.TryGetValue(shingle, out var count) ? count + 1 : 1;
            if (shingles[shingle] >= 5) return true;
        }
        return false;
    }

    /// <summary>私用区字符（U+E000–U+F8FF）：只属于前端渲染层的数学占位符，模型输出/文本中出现即伪影。
    /// 剥离时整段去掉 \uE000…\uE001 包裹区（含索引数字），其余零散私用区字符一并抹除。</summary>
    private static bool ContainsPrivateUse(string value) =>
        value.Any(character => character is >= (char)0xE000 and <= (char)0xF8FF);

    private static string StripPrivateUse(string value)
    {
        if (!ContainsPrivateUse(value)) return value;
        var builder = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character == (char)0xE000)
            {
                var close = value.IndexOf((char)0xE001, index + 1);
                if (close > index)
                {
                    index = close;
                    continue;
                }
            }
            if (character is >= (char)0xE000 and <= (char)0xF8FF) continue;
            builder.Append(character);
        }
        return builder.ToString();
    }

    [GeneratedRegex(@"TRMATHPLACEHOLDER\d+END|TRUNFINISHED(?:LPAREN|RPAREN|LBRACKET|RBRACKET)END|<<<TRANSREADER_CONTEXT>>>|[□▯⊠■◻▢]\d+[□▯⊠■◻▢]", RegexOptions.IgnoreCase)]
    private static partial Regex ArtifactToken();

    [GeneratedRegex(@"、\s*、+")]
    private static partial Regex CollapsedEnumeration();

    [GeneratedRegex(@"[ \t]+、[ \t]?")]
    private static partial Regex SpacedEnumeration();

    [GeneratedRegex(@"(?m)^[、，,]\s*")]
    private static partial Regex StrayEnumerationAtLineStart();

    [GeneratedRegex(@"[。！？；!?;\n]")]
    private static partial Regex SentenceSplitter();
}

public sealed record SanitizeResult(string Text, bool HadArtifacts, bool HasDegenerateRepetition)
{
    /// <summary>输出是否不可信（剥过占位符或存在退化重复）：调用方应重新校订，且不得写入缓存。</summary>
    public bool RequiresReview => HadArtifacts || HasDegenerateRepetition;
}
