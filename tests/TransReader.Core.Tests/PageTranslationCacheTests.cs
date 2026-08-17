using TransReader.Core.Storage;
using TransReader.Core.Translation;
using TransReader.Core.Ocr;

namespace TransReader.Core.Tests;

public sealed class PageTranslationCacheTests
{
    [Fact]
    public async Task PersistsOnlyForMatchingContextFingerprint()
    {
        var root = Path.Combine(Path.GetTempPath(), "TransReader.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new PageTranslationCache(root);
            var result = new MultimodalTranslationResult(
                "缓存译文",
                "摘要",
                [new TranslationTerm("term", "术语")],
                "context-a");
            await cache.WriteAsync("document", 3, TranslationSettings.MiMoDefault, result);

            var reopenedCache = new PageTranslationCache(root);
            var hit = await reopenedCache.TryReadAsync(
                "document", 3, TranslationSettings.MiMoDefault, "context-a");
            var miss = await reopenedCache.TryReadAsync(
                "document", 3, TranslationSettings.MiMoDefault, "context-b");

            Assert.NotNull(hit);
            Assert.Equal("缓存译文", hit.Text);
            Assert.Equal(OpenAiCompatibleTranslator.FormatVersion, hit.FormatVersion);
            Assert.Null(miss);
            Assert.Empty(Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task InvalidatesWhenKnownOcrFingerprintChanges()
    {
        var root = Path.Combine(Path.GetTempPath(), "TransReader.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new PageTranslationCache(root);
            var result = new MultimodalTranslationResult("# 译文", "摘要", [], "context-a");
            await cache.WriteAsync("document", 1, TranslationSettings.MiMoDefault, result, "ocr-a");

            Assert.NotNull(await cache.TryReadAsync(
                "document", 1, TranslationSettings.MiMoDefault, "context-a", "ocr-a"));
            Assert.Null(await cache.TryReadAsync(
                "document", 1, TranslationSettings.MiMoDefault, "context-a", "ocr-b"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteDocumentRemovesOnlySelectedDocumentCaches()
    {
        var root = Path.Combine(Path.GetTempPath(), "TransReader.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var translation = new PageTranslationCache(Path.Combine(root, "translation"));
            var ocr = new PageOcrCache(Path.Combine(root, "ocr"));
            var result = new MultimodalTranslationResult("译文", "摘要", [], "context-a");
            var ocrPage = new OcrPage(10, 10, []);

            await translation.WriteAsync("document-a", 0, TranslationSettings.MiMoDefault, result);
            await translation.WriteAsync("document-b", 0, TranslationSettings.MiMoDefault, result);
            await ocr.WriteAsync("document-a", 0, ocrPage);
            await ocr.WriteAsync("document-b", 0, ocrPage);

            translation.DeleteDocument("document-a");
            ocr.DeleteDocument("document-a");

            Assert.Null(await translation.TryReadAnyAsync("document-a", 0, TranslationSettings.MiMoDefault));
            Assert.NotNull(await translation.TryReadAnyAsync("document-b", 0, TranslationSettings.MiMoDefault));
            Assert.Null(await ocr.TryReadAsync("document-a", 0));
            Assert.NotNull(await ocr.TryReadAsync("document-b", 0));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ClearRemovesAllPersistentCaches()
    {
        var root = Path.Combine(Path.GetTempPath(), "TransReader.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var translationRoot = Path.Combine(root, "translation");
            var ocrRoot = Path.Combine(root, "ocr");
            var translation = new PageTranslationCache(translationRoot);
            var ocr = new PageOcrCache(ocrRoot);
            await translation.WriteAsync(
                "document", 0, TranslationSettings.MiMoDefault,
                new MultimodalTranslationResult("译文", "摘要", [], "context-a"));
            await ocr.WriteAsync("document", 0, new OcrPage(10, 10, []));

            translation.Clear();
            ocr.Clear();

            Assert.False(Directory.Exists(translationRoot));
            Assert.False(Directory.Exists(ocrRoot));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LocalProviderIdentityIsIndependentOfLoopbackPortAndSeparatedFromOnline()
    {
        var root = Path.Combine(Path.GetTempPath(), "TransReader.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new PageTranslationCache(root);
            var localA = new TranslationSettings("http://127.0.0.1:12001/v1", "qwen", "简体中文", "none",
                false, ProviderId: "local-qwen", CacheIdentity: "local:qwen:sha-a:prompt-v1");
            var localB = localA with { BaseUrl = "http://127.0.0.1:32003/v1" };
            var online = TranslationSettings.MiMoDefault;
            var result = new MultimodalTranslationResult("本地译文", "摘要", [], "context-a");

            await cache.WriteAsync("document", 0, localA, result, "ocr-a");

            Assert.NotNull(await cache.TryReadAsync("document", 0, localB, "context-a", "ocr-a"));
            Assert.Null(await cache.TryReadAsync("document", 0, online, "context-a", "ocr-a"));
            Assert.Null(await cache.TryReadAsync("document", 0,
                localB with { CacheIdentity = "local:qwen:sha-b:prompt-v1" }, "context-a", "ocr-a"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Prune_KeepsOtherProviderDirectories()
    {
        var root = Path.Combine(Path.GetTempPath(), "TransReader.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new PageTranslationCache(root);
            var current = TranslationSettings.MiMoDefault;
            var other = new TranslationSettings("https://api.deepseek.com/v1", "deepseek-chat", "简体中文", "bearer");
            await cache.WriteAsync("document", 0, current, new MultimodalTranslationResult("mimo 译文", "摘要", [], "context-a"));
            await cache.WriteAsync("document", 0, other, new MultimodalTranslationResult("deepseek 译文", "摘要", [], "context-a"));

            var deleted = await cache.PruneDocumentAsync("document", current);

            Assert.Equal(0, deleted); // 不做跨 provider 删除
            Assert.NotNull(await cache.TryReadAsync("document", 0, current, "context-a")); // mimo 仍在
            Assert.NotNull(await cache.TryReadAsync("document", 0, other, "context-a")); // deepseek 译文保留复用
            Assert.Equal(2, Directory.GetDirectories(Path.Combine(root, "document")).Length); // 两个 provider 目录都在
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Prune_DeletesStalePromptVersionFilesInCurrentProviderDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "TransReader.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new PageTranslationCache(root);
            var current = TranslationSettings.MiMoDefault;
            await cache.WriteAsync("document", 0, current, new MultimodalTranslationResult("有效译文", "摘要", [], "context-a"));
            // 在当前 provider 目录里把 page 文件覆写为旧 PromptVersion 的孤儿。
            var pagePath = Path.Combine(root, "document", PageTranslationCache.GetProviderKey(current), "page-000000.json");
            Assert.True(File.Exists(pagePath));
            await File.WriteAllTextAsync(pagePath, """{"text":"x","summary":"","terms":[],"contextFingerprint":"cf","promptVersion":"old","formatVersion":"old","wasReviewed":false,"ocrAvailable":true,"ocrFingerprint":""}""");

            var deleted = await cache.PruneDocumentAsync("document", current);

            Assert.Equal(1, deleted);
            Assert.False(File.Exists(pagePath)); // stale 文件已删
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task TryReadAnyProvider_ReturnsValidEntryAcrossProviders()
    {
        var root = Path.Combine(Path.GetTempPath(), "TransReader.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new PageTranslationCache(root);
            var mimo = TranslationSettings.MiMoDefault;
            var kimi = TranslationSettings.MiMoDefault with { BaseUrl = "https://example.test/v1", Model = "kimi-k3" };
            await cache.WriteAsync("document", 5, mimo, new MultimodalTranslationResult("MiMo 译文", "摘要", [], "ctx"));
            await cache.WriteAsync("document", 5, kimi, new MultimodalTranslationResult("Kimi 译文", "摘要", [], "ctx"));

            // 换模型语义：当前 provider（第三种模型）未缓存时，任一 provider 的有效译文仍可回退展示。
            var any = await cache.TryReadAnyProviderAsync("document", 5);
            Assert.NotNull(any);
            Assert.Contains(any.Text, new[] { "MiMo 译文", "Kimi 译文" });

            var third = TranslationSettings.MiMoDefault with { BaseUrl = "https://other.test/v1", Model = "glm" };
            Assert.Null(await cache.TryReadAsync("document", 5, third, "ctx"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task TryReadAnyProvider_SkipsStaleVersionEntries()
    {
        var root = Path.Combine(Path.GetTempPath(), "TransReader.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new PageTranslationCache(root);
            var current = TranslationSettings.MiMoDefault;
            await cache.WriteAsync("document", 2, current, new MultimodalTranslationResult("x", "s", [], "ctx"));
            var pagePath = Path.Combine(root, "document", PageTranslationCache.GetProviderKey(current), "page-000002.json");
            await File.WriteAllTextAsync(pagePath, """{"text":"x","summary":"","terms":[],"contextFingerprint":"cf","promptVersion":"old","formatVersion":"old","wasReviewed":false,"ocrAvailable":true,"ocrFingerprint":""}""");

            Assert.Null(await cache.TryReadAnyProviderAsync("document", 2));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
