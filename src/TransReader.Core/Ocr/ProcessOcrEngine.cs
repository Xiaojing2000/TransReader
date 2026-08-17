using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace TransReader.Core.Ocr;

/// <summary>
/// OCR engine running in a dedicated worker process (TransOcrNative.Host.exe).
/// The vendored PaddleOCR pipeline can terminate its own process on internal
/// errors (exit/abort/fail-fast); out here that only kills the worker, and the
/// caller can simply create a new engine instance to recover.
/// </summary>
public sealed class ProcessOcrEngine : IOcrEngine
{
    private const int ExitInterceptedStatus = -1;

    /// <summary>工作者进程死亡/协议损坏/初始化失败时的统一 Status（负值）。
    /// 宿主原生状态码（&gt;0）表示单张图像识别失败，引擎本身仍健康。</summary>
    public const int WorkerDeadStatus = ExitInterceptedStatus;

    /// <summary>OCR 引擎/模型版本标识。升级 PaddleOCR 模型后改它，旧 OCR 缓存即按版本失效重算。</summary>
    public const string EngineVersion = "paddleocr-ppocrv5-mobile-cpu-v1";

    private readonly object _gate = new();
    private readonly Process _process;
    private readonly Stream _input;
    private readonly AnonymousPipeServerStream _responsePipe;
    private readonly Stream _output;
    private bool _disposed;

    public ProcessOcrEngine(string modelDirectory, int threads = 8)
    {
        var hostPath = Path.Combine(AppContext.BaseDirectory, "TransOcrNative.Host.exe");
        if (!File.Exists(hostPath))
        {
            throw new NativeOcrException(ExitInterceptedStatus,
                $"未找到 OCR 工作者进程：{hostPath}");
        }

        // Response frames travel over a dedicated anonymous pipe whose handle the
        // worker inherits. Its stdout is left to the vendored Paddle logging
        // (fprintf(stdout, ...)), so a log flush can never corrupt a frame.
        _responsePipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);

        var startInfo = new ProcessStartInfo
        {
            FileName = hostPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(_responsePipe.GetClientHandleAsString());
        startInfo.ArgumentList.Add(modelDirectory);
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

        // stdout now carries native logs only. It must be drained continuously —
        // otherwise a full pipe buffer would block the worker — but the content
        // itself is discarded (AppLog lives in the App layer, not in Core).
        _ = Task.Run(async () =>
        {
            try
            {
                while (await _process.StandardOutput.ReadLineAsync() is not null)
                {
                }
            }
            catch
            {
            }
        });

        // The host writes one int32 (0) once the native models are loaded.
        var ready = ReadExact(4);
        if (ready is null || BitConverter.ToInt32(ready) != 0)
        {
            var stderr = ReadStderrSafe();
            _responsePipe.Dispose();
            throw new NativeOcrException(ExitInterceptedStatus,
                $"OCR 工作者进程初始化失败。{stderr}");
        }
    }

    public OcrPage Recognize(ReadOnlySpan<byte> bgraPixels, int width, int height, int stride)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_process.HasExited)
            {
                throw WorkerDeadException();
            }

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
                if (responseHeader is null)
                {
                    throw WorkerDeadException();
                }
                var status = BitConverter.ToInt32(responseHeader);
                var payloadLength = BitConverter.ToUInt64(responseHeader.AsSpan(4));
                if (payloadLength > (64UL << 20))
                {
                    throw WorkerDeadException("OCR 工作者进程返回了非法数据帧。");
                }
                var payload = ReadExact((int)payloadLength)
                    ?? throw WorkerDeadException();
                var payloadText = Encoding.UTF8.GetString(payload);
                if (status != 0)
                {
                    throw new NativeOcrException(status, payloadText);
                }
                var page = JsonSerializer.Deserialize<OcrPage>(payloadText)
                    ?? throw new NativeOcrException(status, "OCR 工作者进程返回了无效 JSON。");
                return page with { EngineVersion = EngineVersion };
            }
            catch (IOException)
            {
                throw WorkerDeadException();
            }
            catch (ObjectDisposedException)
            {
                throw WorkerDeadException();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        try
        {
            _input.Dispose(); // Closing stdin makes the host exit its loop.
            if (!_process.WaitForExit(2000))
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch
            {
            }
        }
        _responsePipe.Dispose();
        _process.Dispose();
        GC.SuppressFinalize(this);
    }

    private byte[]? ReadExact(int count)
    {
        var buffer = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            int read;
            try
            {
                read = _output.Read(buffer, offset, count - offset);
            }
            catch (IOException)
            {
                return null;
            }
            catch (ObjectDisposedException)
            {
                return null;
            }
            if (read == 0)
            {
                return null; // EOF: worker exited.
            }
            offset += read;
        }
        return buffer;
    }

    private string ReadStderrSafe()
    {
        try
        {
            if (_process.WaitForExit(3000))
            {
                return _process.StandardError.ReadToEnd();
            }
            _process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
        return string.Empty;
    }

    private NativeOcrException WorkerDeadException(string? detail = null)
    {
        var exitInfo = _process.HasExited ? $"退出码 0x{_process.ExitCode:X8}" : "进程无响应";
        var stderr = string.Empty;
        if (_process.HasExited)
        {
            try
            {
                stderr = _process.StandardError.ReadToEnd();
            }
            catch
            {
            }
        }
        var message = $"OCR 工作者进程已终止（{exitInfo}）。" +
            (string.IsNullOrWhiteSpace(detail) ? string.Empty : detail) +
            (string.IsNullOrWhiteSpace(stderr) ? string.Empty : $"\n原生输出：{stderr.Trim()}");
        return new NativeOcrException(ExitInterceptedStatus, message);
    }
}
