using TransReader.Core.Storage;

namespace TransReader.Core.Tests;

public sealed class CacheSweeperTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"transreader-sweeper-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task SweepAsync_NoOpWhenUnderCap()
    {
        CreateDoc("a", "page-1.json", new string('x', 100));
        CreateDoc("b", "page-1.json", new string('x', 100));

        var deleted = await CacheSweeper.SweepAsync(_root, maxBytes: 1000);

        Assert.Equal(0, deleted);
        Assert.True(Directory.Exists(Path.Combine(_root, "a")));
        Assert.True(Directory.Exists(Path.Combine(_root, "b")));
    }

    [Fact]
    public async Task SweepAsync_DeletesLruUntilUnderCapAndKeepsRecent()
    {
        // 三个文档目录各约 500 字节；上限 1000 → 需删 1 个最旧的。
        CreateDoc("oldest", "page-1.json", new string('x', 500), lastWrite: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        CreateDoc("middle", "page-1.json", new string('x', 500), lastWrite: new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        CreateDoc("newest", "page-1.json", new string('x', 500), lastWrite: new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc));

        var deleted = await CacheSweeper.SweepAsync(_root, maxBytes: 1000);

        Assert.Equal(1, deleted);
        Assert.False(Directory.Exists(Path.Combine(_root, "oldest")));
        Assert.True(Directory.Exists(Path.Combine(_root, "middle")));
        Assert.True(Directory.Exists(Path.Combine(_root, "newest")));
    }

    [Fact]
    public async Task SweepAsync_NoopWhenRootMissing()
    {
        var missing = Path.Combine(_root, "does-not-exist");
        var deleted = await CacheSweeper.SweepAsync(missing, maxBytes: 1);
        Assert.Equal(0, deleted);
    }

    private void CreateDoc(string docKey, string fileName, string content, DateTime? lastWrite = null)
    {
        var dir = Path.Combine(_root, docKey);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        File.WriteAllText(path, content);
        if (lastWrite is { } time)
        {
            File.SetLastWriteTimeUtc(path, time);
            Directory.SetLastWriteTimeUtc(dir, time);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
