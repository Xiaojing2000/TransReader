using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace TransReader.Core.Ocr;

/// <summary>Absolute locations for one verified OCR component installation.</summary>
public sealed record OcrRuntimePaths(
    string HostPath,
    string ModelDirectory,
    string PipelineConfigPath);

/// <summary>
/// OCR engine running in a dedicated worker process (TransOcrNative.Host.exe).
/// Native failures are isolated to the worker and reported with a bounded tail
/// of its stderr output.
/// </summary>
public sealed class ProcessOcrEngine : IOcrEngine
{
    private const int ExitInterceptedStatus = -1;
    private const int NativeLogLineLimit = 80;

    public const int WorkerDeadStatus = ExitInterceptedStatus;

    /// <summary>Cache identity. v2 also pins an explicit PaddleOCR pipeline config.</summary>
    public const string EngineVersion = "paddleocr-ppocrv5-mobile-cpu-v2";

    private readonly object _gate = new();
    private readonly object _logGate = new();
    private readonly Queue<string> _stderrLines = new();
    private readonly Process _process;
    private readonly Stream _input;
    private readonly AnonymousPipeServerStream _responsePipe;
    private readonly Stream _output;
    private readonly Task _stdoutDrainTask;
    private readonly Task _stderrDrainTask;
    private bool _disposed;

    private ProcessOcrEngine(OcrRuntimePaths paths, int threads)
    {
        ValidatePaths(paths);
        _responsePipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        var startInfo = new ProcessStartInfo
        {
            FileName = paths.HostPath,
            WorkingDirectory = Path.GetDirectoryName(paths.HostPath)!,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(_responsePipe.GetClientHandleAsString());
        startInfo.ArgumentList.Add(paths.ModelDirectory);
        startInfo.ArgumentList.Add(paths.PipelineConfigPath);
        startInfo.ArgumentList.Add(threads.ToString());

        try
        {
            _process = Process.Start(startInfo)
                ?? throw new NativeOcrException(ExitInterceptedStatus, "无法启动 OCR 工作者进程。");
        }
        catch
        {
            _responsePipe.Dispose();
            throw;
        }

        _responsePipe.DisposeLocalCopyOfClientHandle();
        _input = _process.StandardInput.BaseStream;
        _output = _responsePipe;
        _stdoutDrainTask = DrainOutputAsync(_process.StandardOutput, keepTail: false);
        _stderrDrainTask = DrainOutputAsync(_process.StandardError, keepTail: true);
    }

    /// <summary>Starts the worker and waits for native model initialization.</summary>
    public static async Task<ProcessOcrEngine> CreateAsync(
        OcrRuntimePaths paths,
        int threads = 8,
        TimeSpan? startupTimeout = null,
        CancellationToken cancellationToken = default)
    {
        var engine = new ProcessOcrEngine(paths, threads);
        try
        {
            var ready = await engine.ReadExactAsync(4, cancellationToken)
                .WaitAsync(startupTimeout ?? TimeSpan.FromSeconds(60), cancellationToken)
                .ConfigureAwait(false);
            if (ready is null || BitConverter.ToInt32(ready) != 0)
            {
                await engine.WaitForDiagnosticsAsync().ConfigureAwait(false);
                throw engine.InitializationException("OCR 工作者进程未返回就绪信号。");
            }
            return engine;
        }
        catch (TimeoutException ex)
        {
            var wrapped = engine.InitializationException("OCR 初始化超过 60 秒，已停止工作进程。", ex);
            engine.Dispose();
            throw wrapped;
        }
        catch (OperationCanceledException)
        {
            engine.Dispose();
            throw;
        }
        catch (NativeOcrException)
        {
            engine.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            var wrapped = engine.InitializationException("OCR 工作者进程初始化失败。", ex);
            engine.Dispose();
            throw wrapped;
        }
    }

    public OcrPage Recognize(ReadOnlySpan<byte> bgraPixels, int width, int height, int stride)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_process.HasExited) throw WorkerDeadException();

            Span<byte> header = stackalloc byte[20];
            BitConverter.TryWriteBytes(header, width);
            BitConverter.TryWriteBytes(header[4..], height);
            BitConverter.TryWriteBytes(header[8..], stride);
            BitConverter.TryWriteBytes(header[12..], (ulong)bgraPixels.Length);
            try
            {
                _input.Write(header);
                _input.Write(bgraPixels);
                _input.Flush();

                var responseHeader = ReadExact(12);
                if (responseHeader is null) throw WorkerDeadException();
                var status = BitConverter.ToInt32(responseHeader);
                var payloadLength = BitConverter.ToUInt64(responseHeader.AsSpan(4));
                if (payloadLength > (64UL << 20))
                    throw WorkerDeadException("OCR 工作者进程返回了非法数据帧。");
                var payload = ReadExact((int)payloadLength) ?? throw WorkerDeadException();
                var payloadText = Encoding.UTF8.GetString(payload);
                if (status != 0) throw new NativeOcrException(status, payloadText);
                var page = JsonSerializer.Deserialize<OcrPage>(payloadText)
                    ?? throw new NativeOcrException(status, "OCR 工作者进程返回了无效 JSON。");
                return page with { EngineVersion = EngineVersion };
            }
            catch (IOException) { throw WorkerDeadException(); }
            catch (ObjectDisposedException) { throw WorkerDeadException(); }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _input.Dispose();
            if (!_process.WaitForExit(2000)) _process.Kill(entireProcessTree: true);
        }
        catch
        {
            try { _process.Kill(entireProcessTree: true); } catch { }
        }
        _responsePipe.Dispose();
        _process.Dispose();
        GC.SuppressFinalize(this);
    }

    private static void ValidatePaths(OcrRuntimePaths paths)
    {
        if (!File.Exists(paths.HostPath))
            throw new NativeOcrException(ExitInterceptedStatus, $"未找到 OCR 工作者进程：{paths.HostPath}");
        if (!Directory.Exists(paths.ModelDirectory))
            throw new NativeOcrException(ExitInterceptedStatus, $"未找到 OCR 模型目录：{paths.ModelDirectory}");
        if (!File.Exists(paths.PipelineConfigPath))
            throw new NativeOcrException(ExitInterceptedStatus, $"未找到 OCR 配置文件：{paths.PipelineConfigPath}");
    }

    private async Task DrainOutputAsync(StreamReader reader, bool keepTail)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (!keepTail || string.IsNullOrWhiteSpace(line)) continue;
                lock (_logGate)
                {
                    _stderrLines.Enqueue(line);
                    while (_stderrLines.Count > NativeLogLineLimit) _stderrLines.Dequeue();
                }
            }
        }
        catch { }
    }

    private byte[]? ReadExact(int count)
    {
        var buffer = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            int read;
            try { read = _output.Read(buffer, offset, count - offset); }
            catch (IOException) { return null; }
            catch (ObjectDisposedException) { return null; }
            if (read == 0) return null;
            offset += read;
        }
        return buffer;
    }

    private async Task<byte[]?> ReadExactAsync(int count, CancellationToken cancellationToken)
    {
        var buffer = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            int read;
            try
            {
                read = await _output.ReadAsync(buffer.AsMemory(offset, count - offset), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (IOException) { return null; }
            catch (ObjectDisposedException) { return null; }
            if (read == 0) return null;
            offset += read;
        }
        return buffer;
    }

    private string StderrTail()
    {
        lock (_logGate) return string.Join(Environment.NewLine, _stderrLines);
    }

    private async Task WaitForDiagnosticsAsync()
    {
        try
        {
            if (!_process.HasExited)
                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
        catch { }
        try { await _stderrDrainTask.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
        catch { }
    }

    private NativeOcrException InitializationException(string detail, Exception? inner = null)
    {
        var stderr = StderrTail();
        var exit = _process.HasExited ? $"退出码 0x{_process.ExitCode:X8}" : "进程未就绪";
        var message = $"{detail}（{exit}）" +
            (string.IsNullOrWhiteSpace(stderr) ? string.Empty : $"\n原生输出：{stderr}") +
            (inner is null ? string.Empty : $"\n{inner.Message}");
        return new NativeOcrException(ExitInterceptedStatus, message);
    }

    private NativeOcrException WorkerDeadException(string? detail = null)
    {
        var exitInfo = _process.HasExited ? $"退出码 0x{_process.ExitCode:X8}" : "进程无响应";
        var stderr = StderrTail();
        var message = $"OCR 工作者进程已终止（{exitInfo}）。" +
            (string.IsNullOrWhiteSpace(detail) ? string.Empty : detail) +
            (string.IsNullOrWhiteSpace(stderr) ? string.Empty : $"\n原生输出：{stderr}");
        return new NativeOcrException(ExitInterceptedStatus, message);
    }
}
