using TransReader.Core.Ocr;

namespace TransReader.App.Services;

internal enum OcrWorkPriority
{
    Foreground,
    Background
}

internal sealed class OcrCoordinator : IDisposable
{
    private readonly IOcrEngineProvider _engineProvider;
    private readonly PriorityLeaseQueue<OcrWorkPriority> _leases = new(2);

    public OcrCoordinator(IOcrEngineProvider engineProvider)
    {
        _engineProvider = engineProvider;
    }

    /// <summary>当前 OCR 引擎/模型的期望版本，供 OCR 缓存按版本校验（升级后旧缓存自动失效）。</summary>
    public string EngineVersion => ProcessOcrEngine.EngineVersion;

    public async Task<OcrPage> RecognizeAsync(
        ReadOnlyMemory<byte> bgraPixels,
        int width,
        int height,
        int stride,
        OcrWorkPriority priority,
        CancellationToken cancellationToken)
    {
        using var lease = await _leases.AcquireAsync(priority, cancellationToken);
        var engine = await _engineProvider.EnsureAsync();
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await Task.Run(
                () => engine.Recognize(bgraPixels.Span, width, height, stride),
                cancellationToken);
        }
        // 仅工作者进程死亡/协议损坏（Status<0）才重建引擎并重试；
        // 单张图像识别失败（宿主原生状态码>0）直接当页报错，不付出整机重启的秒级代价。
        catch (NativeOcrException ex) when (ex.Status == ProcessOcrEngine.WorkerDeadStatus)
        {
            cancellationToken.ThrowIfCancellationRequested();
            engine = await _engineProvider.ResetAsync(engine);
            cancellationToken.ThrowIfCancellationRequested();
            return await Task.Run(
                () => engine.Recognize(bgraPixels.Span, width, height, stride),
                cancellationToken);
        }
    }

    public void Dispose() => _leases.Dispose();
}
