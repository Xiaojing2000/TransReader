namespace TransReader.Core.Translation;

public sealed class TranslationException : Exception
{
    /// <summary>触发异常的 HTTP 状态码（0 表示非 HTTP 错误）。用于 stream_options 不兼容时的回退判断。</summary>
    public int StatusCode { get; }

    public TranslationException(string message) : base(message) { }

    public TranslationException(string message, int statusCode) : base(message)
    {
        StatusCode = statusCode;
    }
}

