using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TransReader.Core.Ocr;

namespace TransReader.Core.Storage;

public sealed class PageOcrCache
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly string _cacheRoot;

    public PageOcrCache(string cacheRoot)
    {
        _cacheRoot = cacheRoot;
    }

    public static string GetDocumentKey(string filePath)
    {
        var file = new FileInfo(filePath);
        var identity = $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    public async Task<OcrPage?> TryReadAsync(
        string documentKey,
        uint pageIndex,
        CancellationToken cancellationToken = default,
        string expectedEngineVersion = "")
    {
        var path = GetPagePath(documentKey, pageIndex);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var page = await JsonSerializer.DeserializeAsync<OcrPage>(stream, SerializerOptions, cancellationToken);
            if (page is null) return null;
            // 升级 OCR 引擎/模型后：旧条目 EngineVersion 为 "" 或旧值，与期望不符即视为失效（静默跳过，下次 OCR 覆盖写）。
            // expectedEngineVersion 为空（调用方未传）时不校验，保持向后兼容。
            if (!string.IsNullOrEmpty(expectedEngineVersion) &&
                !string.Equals(page.EngineVersion, expectedEngineVersion, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            return page;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public async Task WriteAsync(
        string documentKey,
        uint pageIndex,
        OcrPage page,
        CancellationToken cancellationToken = default)
    {
        var path = GetPagePath(documentKey, pageIndex);
        await AtomicJsonFile.WriteAsync(path, page, SerializerOptions, cancellationToken);
    }

    public void DeleteDocument(string documentKey)
    {
        var directory = GetDocumentDirectory(documentKey);
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// 清理该文档下 EngineVersion 与期望不符的过期 page 文件（升级 OCR 引擎/模型后遗留）。
    /// expectedEngineVersion 为空则不删（向后兼容）。返回删除的文件数。
    /// </summary>
    public async Task<int> PruneDocumentAsync(
        string documentKey,
        string expectedEngineVersion,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(expectedEngineVersion)) return 0;
        var directory = GetDocumentDirectory(documentKey);
        if (!Directory.Exists(directory)) return 0;
        var deleted = 0;
        foreach (var pageFile in Directory.GetFiles(directory, "page-*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await IsStaleAsync(pageFile, expectedEngineVersion, cancellationToken))
            {
                try { File.Delete(pageFile); deleted++; }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        return deleted;
    }

    private static async Task<bool> IsStaleAsync(string path, string expectedEngineVersion, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            var page = await JsonSerializer.DeserializeAsync<OcrPage>(stream, SerializerOptions, cancellationToken);
            return page is null
                || !string.Equals(page.EngineVersion, expectedEngineVersion, StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException) { return true; }
        catch (IOException) { return false; } // 文件被占用等情况不删
    }

    public void Clear()
    {
        if (Directory.Exists(_cacheRoot))
        {
            Directory.Delete(_cacheRoot, recursive: true);
        }
    }

    private string GetPagePath(string documentKey, uint pageIndex) =>
        Path.Combine(GetDocumentDirectory(documentKey), $"page-{pageIndex:D6}.json");

    private string GetDocumentDirectory(string documentKey)
    {
        if (string.IsNullOrWhiteSpace(documentKey) ||
            documentKey is "." or ".." ||
            documentKey.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException("Invalid document cache key.", nameof(documentKey));
        }
        return Path.Combine(_cacheRoot, documentKey);
    }
}
