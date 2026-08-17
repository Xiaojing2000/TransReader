using System.Reflection;
using System.Threading.Channels;

namespace TransReader.App.Services;

/// <summary>
/// Appends diagnostics to a rolling log file under LocalAppData.
/// 写入经无界通道异步批量落盘：调用方（OCR/翻译/UI 线程）不再在同一把锁上排队；
/// 超容时轮转为 .1 保留历史，而不是清空丢现场。
/// </summary>
internal static class AppLog
{
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TransReader",
        "logs");
    private static readonly string LogPath = Path.Combine(LogDirectory, "app.log");
    private static readonly string CrashPath = Path.Combine(LogDirectory, "crashes.log");
    private static readonly string AppVersion =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
    private const long LogCapBytes = 1_000_000;
    private const long CrashCapBytes = 2_000_000;

    private sealed record LogEntry(string Path, long CapBytes, string Text);

    private static readonly Channel<LogEntry> Queue = Channel.CreateUnbounded<LogEntry>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private static readonly Task Writer = Task.Run(WriteLoopAsync);

    public static void Error(string context, Exception exception) =>
        Enqueue(LogPath, LogCapBytes, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [ERROR] {context}\n{exception}\n");

    public static void Info(string message) =>
        Enqueue(LogPath, LogCapBytes, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [INFO] {message}\n");

    /// <summary>记录未处理异常（崩溃），独立轮转文件，避免覆盖丢失上次崩溃现场。</summary>
    public static void Crash(Exception exception, string context) =>
        Enqueue(CrashPath, CrashCapBytes,
            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [CRASH v{AppVersion}] {context}\n{exception}\n\n");

    /// <summary>关闭前冲刷：完成通道并等待写盘循环排空（限时 2 秒，绝不阻塞退出）。</summary>
    public static async Task ShutdownAsync()
    {
        Queue.Writer.TryComplete();
        try
        {
            await Writer.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch
        {
            // Logging must never break the app.
        }
    }

    private static void Enqueue(string path, long capBytes, string entry)
    {
        try
        {
            Queue.Writer.TryWrite(new LogEntry(path, capBytes, entry + Environment.NewLine));
        }
        catch
        {
            // Logging must never break the app.
        }
    }

    private static async Task WriteLoopAsync()
    {
        var batch = new List<LogEntry>();
        while (await Queue.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            DrainAndFlush(batch);
            // 写盘间隔：日志突发时合并更多条，降低 IO 次数。
            await Task.Delay(200).ConfigureAwait(false);
        }
        // 通道已完成：排空剩余后退出。
        DrainAndFlush(batch);
    }

    private static void DrainAndFlush(List<LogEntry> batch)
    {
        try
        {
            while (batch.Count < 256 && Queue.Reader.TryRead(out var next))
            {
                batch.Add(next);
            }
            if (batch.Count == 0) return;
            Directory.CreateDirectory(LogDirectory);
            foreach (var group in batch.GroupBy(entry => entry.Path))
            {
                var path = group.Key;
                var capBytes = group.First().CapBytes;
                // 轮转：超容不丢历史，把当前文件改名为 .1 后新开（只保留一代）。
                if (File.Exists(path) && new FileInfo(path).Length > capBytes)
                {
                    var rolled = path + ".1";
                    if (File.Exists(rolled)) File.Delete(rolled);
                    File.Move(path, rolled);
                }
                File.AppendAllText(path, string.Concat(group.Select(entry => entry.Text)));
            }
        }
        catch
        {
            // Logging must never break the app.
        }
        finally
        {
            batch.Clear();
        }
    }
}
