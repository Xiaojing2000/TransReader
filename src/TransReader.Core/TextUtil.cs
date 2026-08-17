namespace TransReader.Core;

/// <summary>字符串截断工具（收敛各处重复实现）。</summary>
public static class TextUtil
{
    /// <summary>截取头部最多 length 个字符；null/空串返回空串（用于提示词上下文）。</summary>
    public static string LimitHead(string? value, int length)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Length <= length ? value : value[..length];
    }

    /// <summary>截取头部最多 length 个字符并去除两端空白。</summary>
    public static string LimitTrimmed(string value, int length) =>
        value.Length <= length ? value.Trim() : value[..length].Trim();

    /// <summary>截取尾部最多 limit 个字符（保留最近内容，用于断点续译回放）。</summary>
    public static string LimitTail(string value, int limit) =>
        value.Length <= limit ? value : value[^limit..];
}
