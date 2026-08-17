using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TransReader.Core.Translation;

namespace TransReader.Core.Storage;

public sealed record CachedPageTranslation(
    string Text,
    string Summary,
    IReadOnlyList<TranslationTerm> Terms,
    string ContextFingerprint,
    string PromptVersion,
    string FormatVersion,
    bool WasReviewed,
    bool OcrAvailable,
    string OcrFingerprint);

public sealed class PageTranslationCache
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly string _cacheRoot;

    public PageTranslationCache(string cacheRoot)
    {
        _cacheRoot = cacheRoot;
    }

    public async Task<CachedPageTranslation?> TryReadAsync(
        string documentKey,
        uint pageIndex,
        TranslationSettings settings,
        string expectedContextFingerprint,
        string expectedOcrFingerprint = "",
        CancellationToken cancellationToken = default)
    {
        var entry = await TryReadAnyAsync(documentKey, pageIndex, settings, cancellationToken);
        return entry is not null &&
               entry.PromptVersion == OpenAiCompatibleTranslator.PromptVersion &&
               entry.FormatVersion == OpenAiCompatibleTranslator.FormatVersion &&
               entry.ContextFingerprint == expectedContextFingerprint &&
               (string.IsNullOrEmpty(expectedOcrFingerprint) || entry.OcrFingerprint == expectedOcrFingerprint)
            ? entry
            : null;
    }

    public async Task<CachedPageTranslation?> TryReadAnyAsync(
        string documentKey,
        uint pageIndex,
        TranslationSettings settings,
        CancellationToken cancellationToken = default)
    {
        var path = GetPagePath(documentKey, pageIndex, settings);
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<CachedPageTranslation>(
                stream,
                SerializerOptions,
                cancellationToken);
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
        TranslationSettings settings,
        MultimodalTranslationResult result,
        string ocrFingerprint = "",
        CancellationToken cancellationToken = default)
    {
        var path = GetPagePath(documentKey, pageIndex, settings);
        var entry = new CachedPageTranslation(
            result.Text,
            result.Summary,
            result.Terms,
            result.ContextFingerprint,
            OpenAiCompatibleTranslator.PromptVersion,
            result.FormatVersion,
            result.WasReviewed,
            result.OcrAvailable,
            OcrFingerprint: ocrFingerprint);
        await AtomicJsonFile.WriteAsync(path, entry, SerializerOptions, cancellationToken);
    }

    /// <summary>
    /// 读取本页任一 provider 目录下的有效译文（Prompt/Format 版本校验通过，多份时取最新写入）。
    /// 用于"换模型后展示此前由其他模型生成的译文"，避免已译页面被自动重译（重复计费）。
    /// </summary>
    public async Task<CachedPageTranslation?> TryReadAnyProviderAsync(
        string documentKey,
        uint pageIndex,
        CancellationToken cancellationToken = default)
    {
        var documentDirectory = GetDocumentDirectory(documentKey);
        if (!Directory.Exists(documentDirectory)) return null;
        CachedPageTranslation? best = null;
        var bestWriteUtc = DateTime.MinValue;
        foreach (var providerDirectory in Directory.EnumerateDirectories(documentDirectory))
        {
            var path = Path.Combine(providerDirectory, $"page-{pageIndex:D6}.json");
            if (!File.Exists(path)) continue;
            CachedPageTranslation? entry;
            try
            {
                await using var stream = File.OpenRead(path);
                entry = await JsonSerializer.DeserializeAsync<CachedPageTranslation>(
                    stream, SerializerOptions, cancellationToken);
            }
            catch (JsonException) { continue; }
            catch (IOException) { continue; }
            if (entry is null ||
                entry.PromptVersion != OpenAiCompatibleTranslator.PromptVersion ||
                entry.FormatVersion != OpenAiCompatibleTranslator.FormatVersion)
            {
                continue;
            }
            var writeUtc = File.GetLastWriteTimeUtc(path);
            if (writeUtc > bestWriteUtc)
            {
                best = entry;
                bestWriteUtc = writeUtc;
            }
        }
        return best;
    }

    /// <summary>读取一篇文献全部页的译文文本（未缓存的页返回 null，不抛异常）。用于导出译文。</summary>
    public async Task<IReadOnlyList<string?>> ReadAllTranslationTextAsync(
        string documentKey,
        uint pageCount,
        TranslationSettings settings,
        CancellationToken cancellationToken = default)
    {
        var pages = new string?[pageCount];
        for (uint i = 0; i < pageCount; i++)
        {
            var entry = await TryReadAnyAsync(documentKey, i, settings, cancellationToken);
            pages[i] = entry?.Text;
        }
        return pages;
    }

    public void DeleteDocument(string documentKey)
    {
        var directory = GetDocumentDirectory(documentKey);
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    public void Clear()
    {
        if (Directory.Exists(_cacheRoot))
        {
            Directory.Delete(_cacheRoot, recursive: true);
        }
    }

    private string GetPagePath(string documentKey, uint pageIndex, TranslationSettings settings)
        => Path.Combine(GetDocumentDirectory(documentKey), GetProviderKey(settings), $"page-{pageIndex:D6}.json");

    // Keep the exact legacy online identity so existing paid translations remain reusable.
    internal static string GetProviderKey(TranslationSettings settings)
    {
        var identity = string.IsNullOrWhiteSpace(settings.CacheIdentity)
            ? $"v3|{settings.BaseUrl}|{settings.Model}|{settings.TargetLanguage}|{OpenAiCompatibleTranslator.FormatVersion}"
            : $"v4|{settings.ProviderCacheIdentity}|{settings.TargetLanguage}|{OpenAiCompatibleTranslator.FormatVersion}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16];
    }

    /// <summary>
    /// 仅清理当前 provider 目录内 PromptVersion/FormatVersion 过期或损坏的 page 文件，返回删除的文件数。
    /// 不删除其他 provider 的目录：切换模型/provider 后旧译文仍保留复用（避免重译与重复计费）。
    /// 缓存容量由全局 CacheSweeper 按 LRU 管理；整体删除由用户显式操作（Clear/DeleteDocument）负责。
    /// </summary>
    public async Task<int> PruneDocumentAsync(
        string documentKey,
        TranslationSettings settings,
        CancellationToken cancellationToken = default)
    {
        var providerDirectory = Path.Combine(GetDocumentDirectory(documentKey), GetProviderKey(settings));
        if (!Directory.Exists(providerDirectory)) return 0;
        var deleted = 0;
        foreach (var pageFile in Directory.GetFiles(providerDirectory, "page-*.json"))
        {
            if (await IsStaleAsync(pageFile, cancellationToken))
            {
                try { File.Delete(pageFile); deleted++; }
                catch (IOException) { }
            }
        }
        return deleted;
    }

    private static async Task<bool> IsStaleAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            var entry = await JsonSerializer.DeserializeAsync<CachedPageTranslation>(stream, SerializerOptions, cancellationToken);
            return entry is null
                || entry.PromptVersion != OpenAiCompatibleTranslator.PromptVersion
                || entry.FormatVersion != OpenAiCompatibleTranslator.FormatVersion;
        }
        catch (JsonException) { return true; }
        catch (IOException) { return false; } // 文件被占用等情况不删
    }

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
