using System.IO;

namespace TransReader.Core.Storage;

/// <summary>
/// 缓存目录的全局容量清扫：当 cacheRoot 下所有文档目录合计超过上限时，
/// 按"最近写入时间"升序（LRU）逐个整目录删除，直到降到上限内。
/// </summary>
public static class CacheSweeper
{
    /// <summary>单类缓存默认上限（字节）。OCR 与翻译缓存各自独立适用。</summary>
    public const long DefaultMaxBytes = 750L * 1024 * 1024;

    /// <summary>
    /// 清扫 cacheRoot：枚举其下的文档目录，按最后写入时间升序删除 LRU 目录，
    /// 直到总大小 ≤ maxBytes。文件/目录级 IO 错误被吞掉（清扫不得中断应用）。
    /// 返回被删除的文档目录数。
    /// </summary>
    public static Task<int> SweepAsync(string cacheRoot, long maxBytes, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(cacheRoot)) return Task.FromResult(0);
        return Task.Run(() =>
        {
            var directories = SafeEnumerateDirectories(cacheRoot);
            var entries = new List<(string Path, long Size, DateTime LastWrite)>();
            long total = 0;
            foreach (var dir in directories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (size, lastWrite) = MeasureDirectory(dir);
                entries.Add((dir, size, lastWrite));
                total += size;
            }
            if (total <= maxBytes) return 0;

            // LRU：最后写入最早的先删。
            entries.Sort((a, b) => a.LastWrite.CompareTo(b.LastWrite));
            var deleted = 0;
            foreach (var entry in entries)
            {
                if (total <= maxBytes) break;
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    Directory.Delete(entry.Path, recursive: true);
                    total -= entry.Size;
                    deleted++;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return deleted;
        }, cancellationToken);
    }

    private static List<string> SafeEnumerateDirectories(string root)
    {
        try
        {
            return Directory.GetDirectories(root).ToList();
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    private static (long Size, DateTime LastWrite) MeasureDirectory(string directory)
    {
        long size = 0;
        var lastWrite = Directory.GetLastWriteTimeUtc(directory);
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var info = new FileInfo(file);
                    size += info.Length;
                    if (info.LastWriteTimeUtc > lastWrite) lastWrite = info.LastWriteTimeUtc;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return (size, lastWrite);
    }
}
