using System.Net;
using TransReader.Core.Net;

namespace TransReader.Core.Tests;

public sealed class HttpTransientRetryTests
{
    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    [InlineData(HttpStatusCode.RequestTimeout)]   // 408
    [InlineData(HttpStatusCode.TooManyRequests)]  // 429
    public void IsTransientStatus_ClassifiesServerAndThrottling(HttpStatusCode code) =>
        Assert.True(HttpTransientRetry.IsTransientStatus(code));

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.PaymentRequired)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public void IsTransientStatus_RejectsClientErrors(HttpStatusCode code) =>
        Assert.False(HttpTransientRetry.IsTransientStatus(code));

    [Fact]
    public void IsTransient_RecognizesHttpRequestException() =>
        Assert.True(HttpTransientRetry.IsTransient(new HttpRequestException("boom")));

    [Fact]
    public void IsTransient_IgnoresUserCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.False(HttpTransientRetry.IsTransient(new TaskCanceledException(null, null, cts.Token)));
    }

    [Fact]
    public void IsTransient_ReturnsFalseForUnrelatedExceptions() =>
        Assert.False(HttpTransientRetry.IsTransient(new InvalidOperationException()));

    [Fact]
    public void MaxAttempts_IsOnePlusBackoffLength() =>
        Assert.Equal(HttpTransientRetry.Backoff.Length + 1, HttpTransientRetry.MaxAttempts);
}
