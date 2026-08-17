using TransReader.Core.Ocr;
using TransReader.Core.Storage;

namespace TransReader.Core.Tests;

public sealed class PageOcrCacheTests
{
    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "TransReader.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task TryRead_SkipsEntryWithMismatchedEngineVersion()
    {
        var root = NewRoot();
        try
        {
            var cache = new PageOcrCache(root);
            await cache.WriteAsync("doc", 0, new OcrPage(10, 10, [], "old-engine"));

            // 期望版本不同（升级模型后）→ 旧条目静默跳过，下次 OCR 覆盖写。
            Assert.Null(await cache.TryReadAsync("doc", 0, default, "new-engine"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task TryRead_ReturnsEntryWithMatchingEngineVersion()
    {
        var root = NewRoot();
        try
        {
            var cache = new PageOcrCache(root);
            await cache.WriteAsync("doc", 1, new OcrPage(20, 30, [], "engine-v1"));

            var page = await cache.TryReadAsync("doc", 1, default, "engine-v1");
            Assert.NotNull(page);
            Assert.Equal("engine-v1", page!.EngineVersion);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task TryRead_NoValidationWhenExpectedVersionEmpty()
    {
        var root = NewRoot();
        try
        {
            var cache = new PageOcrCache(root);
            // 模拟旧版本条目（EngineVersion 为空，向后兼容）。
            await cache.WriteAsync("doc", 2, new OcrPage(5, 5, []));

            // 调用方未传 expectedEngineVersion（默认空）→ 不校验，直接返回（保持旧行为）。
            var page = await cache.TryReadAsync("doc", 2, default);
            Assert.NotNull(page);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task PruneDocument_DeletesStaleEngineVersionKeepsCurrent()
    {
        var root = NewRoot();
        try
        {
            var cache = new PageOcrCache(root);
            await cache.WriteAsync("doc", 0, new OcrPage(10, 10, [], "old-engine"));
            await cache.WriteAsync("doc", 1, new OcrPage(10, 10, [], "new-engine"));

            var deleted = await cache.PruneDocumentAsync("doc", "new-engine");

            Assert.Equal(1, deleted);
            Assert.Null(await cache.TryReadAsync("doc", 0, default, "new-engine"));
            Assert.NotNull(await cache.TryReadAsync("doc", 1, default, "new-engine"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task PruneDocument_NoopWhenExpectedVersionEmpty()
    {
        var root = NewRoot();
        try
        {
            var cache = new PageOcrCache(root);
            await cache.WriteAsync("doc", 0, new OcrPage(10, 10, [], "old-engine"));

            var deleted = await cache.PruneDocumentAsync("doc", expectedEngineVersion: "");

            Assert.Equal(0, deleted);
            Assert.NotNull(await cache.TryReadAsync("doc", 0, default, "old-engine"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
