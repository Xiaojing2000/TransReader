using TransReader.Core.Ocr;

namespace TransReader.App.Services;

internal interface IOcrEngineProvider
{
    Task<IOcrEngine> EnsureAsync();
    Task<IOcrEngine> ResetAsync(IOcrEngine failedEngine);
    Task WarmupAsync();
    Task ReloadAsync();
    void Unload();
}

/// <summary>Retryable OCR worker lifecycle backed by an optional verified component.</summary>
internal sealed class OcrEngineProvider : IOcrEngineProvider, IDisposable
{
    private readonly object _gate = new();
    private readonly OcrComponentManager _components;
    private Task<IOcrEngine>? _warmup;
    private CancellationTokenSource? _warmupCancellation;
    private bool _disposed;

    public OcrEngineProvider(OcrComponentManager components)
    {
        _components = components;
        _components.ComponentChanging += Unload;
    }

    public async Task<IOcrEngine> EnsureAsync()
    {
        if (!_components.IsEnabled)
            throw new OcrComponentUnavailableException("OCR 已关闭，请在“本地组件”中开启。");
        if (!_components.IsInstalled)
            throw new OcrComponentUnavailableException("OCR 组件尚未安装，请在“本地组件”中下载安装。");

        Task<IOcrEngine> task;
        lock (_gate)
        {
            if (_warmup is null)
            {
                _warmupCancellation?.Dispose();
                _warmupCancellation = new CancellationTokenSource();
                _warmup = CreateAsync(_warmupCancellation.Token);
            }
            task = _warmup;
        }
        try
        {
            var engine = await task.ConfigureAwait(false);
            _components.MarkReady();
            return engine;
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_warmup, task))
                {
                    _warmup = null;
                    _warmupCancellation?.Dispose();
                    _warmupCancellation = null;
                }
            }
            _components.MarkError(ex);
            throw;
        }
    }

    public async Task<IOcrEngine> ResetAsync(IOcrEngine failedEngine)
    {
        lock (_gate)
        {
            if (_warmup?.IsCompletedSuccessfully == true && ReferenceEquals(_warmup.Result, failedEngine))
            {
                failedEngine.Dispose();
                _warmup = null;
                _warmupCancellation?.Dispose();
                _warmupCancellation = null;
            }
        }
        return await EnsureAsync().ConfigureAwait(false);
    }

    public Task WarmupAsync() => EnsureAsync();

    public async Task ReloadAsync()
    {
        Unload();
        try
        {
            var engine = await EnsureAsync().ConfigureAwait(false);
            var whitePixels = Enumerable.Repeat((byte)255, 32 * 32 * 4).ToArray();
            await Task.Run(() => engine.Recognize(whitePixels, 32, 32, 32 * 4))
                .WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
            _components.MarkReady();
        }
        catch
        {
            Unload();
            throw;
        }
    }

    public void Unload()
    {
        Task<IOcrEngine>? task;
        lock (_gate)
        {
            task = _warmup;
            _warmup = null;
            _warmupCancellation?.Cancel();
            _warmupCancellation?.Dispose();
            _warmupCancellation = null;
        }
        if (task?.IsCompletedSuccessfully == true) task.Result.Dispose();
    }

    private async Task<IOcrEngine> CreateAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _components.MarkStarting();
        return await ProcessOcrEngine.CreateAsync(
            _components.RuntimePaths,
            threads: Math.Max(1, Math.Min(Environment.ProcessorCount, 8)),
            startupTimeout: TimeSpan.FromSeconds(60),
            cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _components.ComponentChanging -= Unload;
        Unload();
    }
}
