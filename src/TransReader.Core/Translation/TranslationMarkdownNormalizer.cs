using System.Text;
using System.Text.RegularExpressions;

namespace TransReader.Core.Translation;

public static partial class TranslationMarkdownNormalizer
{
    public static string Normalize(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return string.Empty;
        }

        var value = markdown.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
        value = RemoveWholeDocumentFence(value);
        value = MarkdownImageWithDuplicateCaption().Replace(value, match => match.Groups[1].Value.Trim());
        value = MarkdownImage().Replace(value, match => match.Groups[1].Value.Trim());

        var lines = value.Split('\n').ToList();
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[0]))
        {
            lines.RemoveAt(0);
        }
        if (lines.Count > 0 && PageHeading().IsMatch(lines[0].Trim()))
        {
            lines.RemoveAt(0);
        }

        var normalized = new List<string>(lines.Count);
        var blankCount = 0;
        foreach (var line in lines)
        {
            var trimmedEnd = line.TrimEnd();
            if (trimmedEnd.Length == 0)
            {
                if (++blankCount <= 1)
                {
                    normalized.Add(string.Empty);
                }
            }
            else
            {
                blankCount = 0;
                normalized.Add(trimmedEnd);
            }
        }

        return CollapseCjkSpaces(string.Join("\n", normalized).Trim());
    }

    /// <summary>
    /// 清除中文字符之间的多余空格（模型输出常见伪影，如"若有 条线具有"）。
    /// 仅当空格两侧都是 CJK 表意/标点时才删除——英文、LaTeX、代码、URL 均不受影响。
    /// </summary>
    private static string CollapseCjkSpaces(string value)
    {
        if (value.IndexOf(' ') < 0) return value;
        var builder = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == ' ')
            {
                var prev = index - 1;
                while (prev >= 0 && value[prev] == ' ') prev--;
                var next = index + 1;
                while (next < value.Length && value[next] == ' ') next++;
                if (prev >= 0 && next < value.Length && IsCjk(value[prev]) && IsCjk(value[next]))
                {
                    continue; // 中文字符之间的空格（含连续空格）整段跳过
                }
            }
            builder.Append(value[index]);
        }
        return builder.ToString();
    }

    private static bool IsCjk(char character) =>
        character is >= (char)0x4E00 and <= (char)0x9FFF ||
        character is >= (char)0x3000 and <= (char)0x303F ||
        character is >= (char)0xFF00 and <= (char)0xFFEF;

    private static string RemoveWholeDocumentFence(string value)
    {
        var match = WholeDocumentFence().Match(value);
        return match.Success ? match.Groups[1].Value.Trim() : value;
    }

    [GeneratedRegex(@"\A```(?:markdown|md)?[ \t]*\n([\s\S]*?)\n```[ \t]*\z", RegexOptions.IgnoreCase)]
    private static partial Regex WholeDocumentFence();

    [GeneratedRegex(@"^#{1,6}\s*(?:第\s*\d+\s*页|Page\s+\d+)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex PageHeading();

    [GeneratedRegex(@"!\[([^\]]*)\]\([^\r\n)]*\)")]
    private static partial Regex MarkdownImage();

    [GeneratedRegex(@"!\[([^\]]+)\]\([^\r\n)]*\)[ \t]*\n[ \t]*\1[。.]*")]
    private static partial Regex MarkdownImageWithDuplicateCaption();
}
