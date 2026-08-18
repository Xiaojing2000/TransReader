using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace TransReader.App.Services;

internal sealed record VerifiedDownloadProgress(
    string Host,
    long BytesReceived,
    long TotalBytes,
    int SourceIndex,
    int SourceCount);

internal sealed record VerifiedDownloadResult(string Host, bool UsedExistingFile);

/// <summary>HTTPS multi-source downloader with resume, stall detection and pinned integrity.</summary>
internal static class VerifiedDownloader
{
    private static readonly HttpClient Client = new() { Timeout = Timeout.InfiniteTimeSpan };

    public static async Task<VerifiedDownloadResult> DownloadAsync(
        IReadOnlyList<string> urls,
        string destination,
        long expectedSize,
        string expectedSha256,
        Action<VerifiedDownloadProgress>? progress = null,
        bool forceDownload = false,
        CancellationToken cancellationToken = default)
    {
        if (urls.Count == 0) throw new ArgumentException("至少需要一个下载源。", nameof(urls));
        var partial = destination + ".partial";
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (forceDownload)
        {
            TryDelete(destination);
            TryDelete(partial);
        }
        if (await VerifyFileAsync(destination, expectedSize, expectedSha256, cancellationToken).ConfigureAwait(false))
            return new VerifiedDownloadResult("本地缓存", true);
        if (await VerifyFileAsync(partial, expectedSize, expectedSha256, cancellationToken).ConfigureAwait(false))
        {
            File.Move(partial, destination, true);
            return new VerifiedDownloadResult("本地缓存", true);
        }

        var failures = new List<string>();
        for (var source = 0; source < urls.Count; source++)
        {
            var uri = new Uri(urls[source]);
            if (uri.Scheme != Uri.UriSchemeHttps) throw new InvalidDataException("下载源必须使用 HTTPS。");
            for (var attempt = 0; attempt < 2; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await DownloadFromSourceAsync(
                        uri, partial, expectedSize,
                        value => progress?.Invoke(new VerifiedDownloadProgress(
                            uri.Host, value, expectedSize, source, urls.Count)),
                        cancellationToken).ConfigureAwait(false);
                    if (await VerifyFileAsync(partial, expectedSize, expectedSha256, cancellationToken).ConfigureAwait(false))
                    {
                        File.Move(partial, destination, true);
                        return new VerifiedDownloadResult(uri.Host, false);
                    }
                    failures.Add($"{uri.Host}：下载内容校验失败");
                    TryDelete(partial);
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failures.Add($"{uri.Host}：{ex.Message}");
                    if (ex is TimeoutException) break;
                    if (attempt == 0)
                        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                }
            }
            if (source + 1 < urls.Count)
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
        }
        throw new IOException($"下载 {Path.GetFileName(destination)} 失败，已尝试全部 {urls.Count} 个来源：{string.Join("；", failures)}");
    }

    public static async Task<bool> VerifyFileAsync(
        string path,
        long expectedSize,
        string expectedSha256,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != expectedSize) return false;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task DownloadFromSourceAsync(
        Uri uri,
        string partial,
        long expectedSize,
        Action<long>? progress,
        CancellationToken cancellationToken)
    {
        var existing = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (existing >= expectedSize)
        {
            TryDelete(partial);
            existing = 0;
        }
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (existing > 0) request.Headers.Range = new RangeHeaderValue(existing, null);
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (existing > 0 && response.StatusCode != HttpStatusCode.PartialContent) existing = 0;
        response.EnsureSuccessStatusCode();
        progress?.Invoke(existing);
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(partial, existing == 0 ? FileMode.Create : FileMode.Append,
            FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[128 * 1024];
        var received = existing;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            if (read <= 0) break;
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            received += read;
            progress?.Invoke(received);
        }
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
    }
}
