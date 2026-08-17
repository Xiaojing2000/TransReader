using System.Net;

namespace TransReader.Core.Net;

/// <summary>HTTP 瞬时故障判定与退避策略，供在线翻译/文献分析等出站调用复用。</summary>
public static class HttpTransientRetry
{
    /// <summary>每次重试前的退避时长（首项即首次失败后的等待）；数组长度 = 最大重试次数。</summary>
    public static readonly TimeSpan[] Backoff =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(3)
    ];

    /// <summary>最大尝试次数（初次 + 重试）。</summary>
    public static int MaxAttempts => Backoff.Length + 1;

    /// <summary>
    /// 计算第 attempt 次失败后的等待：优先尊重服务端 Retry-After（封顶 30s），
    /// 否则按基础退避加 ±25% 抖动（避免多客户端同步重试加剧限流）。
    /// </summary>
    public static TimeSpan GetDelay(int attempt, System.Net.Http.Headers.RetryConditionHeaderValue? retryAfter = null)
    {
        if (retryAfter is not null)
        {
            var value = retryAfter.Delta ?? (retryAfter.Date - DateTimeOffset.UtcNow);
            if (value > TimeSpan.Zero)
            {
                return value > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : value.Value;
            }
        }
        var baseDelay = Backoff[Math.Min(attempt, Backoff.Length - 1)];
        var jitter = 0.75 + Random.Shared.NextDouble() * 0.5;
        return TimeSpan.FromMilliseconds(baseDelay.TotalMilliseconds * jitter);
    }

    /// <summary>判定为可重试的异常：连接失败或 HttpClient 超时（非用户取消）。</summary>
    public static bool IsTransient(Exception? exception) => exception switch
    {
        HttpRequestException => true,
        TaskCanceledException tce when !tce.CancellationToken.IsCancellationRequested => true,
        _ => false
    };

    /// <summary>判定为可重试的 HTTP 状态码：5xx、408、429。</summary>
    public static bool IsTransientStatus(int statusCode) =>
        statusCode is >= 500 and <= 599 or 408 or 429;

    /// <summary>判定为可重试的 HTTP 状态码（重载）。</summary>
    public static bool IsTransientStatus(HttpStatusCode statusCode) =>
        IsTransientStatus((int)statusCode);
}
