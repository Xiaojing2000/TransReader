using TransReader.Core.Ocr;

namespace TransReader.App.Services;

/// <summary>OCR 引擎生命周期提供者：预热、按需获取、失败重建。从 MainWindow 抽出，使 OcrCoordinator 不再反向依赖 view。</summary>
internal interface IOcrEngineProvider
{
    Task<IOcrEngine> EnsureAsync();
    Task<IOcrEngine> ResetAsync(IOcrEngine failedEngine);
    Task WarmupAsync();
}

internal sealed class OcrEngineProvider : IOcrEngineProvider, IDisposable
{
    private readonly object _gate = new();
    private Task<IOcrEngine>? _warmup;

    public Task<IOcrEngine> EnsureAsync()
    {
        lock (_gate)
        {
            return _warmup ??= CreateAsync();
        }
    }

    public Task<IOcrEngine> ResetAsync(IOcrEngine failedEngine)
    {
        lock (_gate)
        {
            if (_warmup?.IsCompletedSuccessfully == true &&
                ReferenceEquals(_warmup.Result, failedEngine))
            {
                failedEngine.Dispose();
                _warmup = CreateAsync();
            }
            return _warmup ??= CreateAsync();
        }
    }

    public Task WarmupAsync() => EnsureAsync();

    private static Task<IOcrEngine> CreateAsync() =>
        Task.Run<IOcrEngine>(() => new ProcessOcrEngine(
            Path.Combine(AppContext.BaseDirectory, "models"),
            threads: 8));

    public void Dispose()
    {
        Task<IOcrEngine>? task;
        lock (_gate)
        {
            task = _warmup;
        }
        if (task?.IsCompletedSuccessfully == true)
        {
            task.Result.Dispose();
        }
    }
}
