namespace TransReader.App.Services;

internal sealed class LibraryAnalysisQueue : IDisposable
{
    private readonly object _gate = new();
    private readonly Queue<string> _manual = new();
    private readonly Queue<string> _automatic = new();
    private readonly Dictionary<string, WorkItem> _pending = new(StringComparer.Ordinal);
    private readonly HashSet<string> _active = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Func<string, bool, CancellationToken, Task> _handler;
    private readonly Task _consumer;

    public LibraryAnalysisQueue(Func<string, bool, CancellationToken, Task> handler)
    {
        _handler = handler;
        _consumer = Task.Run(ConsumeAsync);
    }

    public void Enqueue(string documentId, bool manual)
    {
        lock (_gate)
        {
            if (_active.Contains(documentId)) return;
            if (_pending.TryGetValue(documentId, out var existing))
            {
                if (!manual || existing.Manual) return;
                _pending[documentId] = existing with { Manual = true };
                _manual.Enqueue(documentId);
                _signal.Release();
                return;
            }
            _pending.Add(documentId, new WorkItem(documentId, manual));
            (manual ? _manual : _automatic).Enqueue(documentId);
        }
        _signal.Release();
    }

    /// <summary>延迟后重新入队（例如本地模型暂时不可用，等待安装/修复后继续分析）。</summary>
    public Task ReenqueueAfterAsync(string documentId, bool manual, TimeSpan delay)
    {
        return Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, _shutdown.Token).ConfigureAwait(false);
                Enqueue(documentId, manual);
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        });
    }

    private async Task ConsumeAsync()
    {
        try
        {
            while (true)
            {
                await _signal.WaitAsync(_shutdown.Token);
                WorkItem? item = null;
                lock (_gate)
                {
                    item = TryTakeNext(_manual, manual: true) ?? TryTakeNext(_automatic, manual: false);
                    if (item is not null)
                    {
                        _pending.Remove(item.DocumentId);
                        _active.Add(item.DocumentId);
                    }
                }
                if (item is null) continue;
                try { await _handler(item.DocumentId, item.Manual, _shutdown.Token); }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { return; }
                finally { lock (_gate) _active.Remove(item.DocumentId); }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }

    private WorkItem? TryTakeNext(Queue<string> queue, bool manual)
    {
        while (queue.TryDequeue(out var documentId))
        {
            if (_pending.TryGetValue(documentId, out var item) && item.Manual == manual)
                return item;
        }
        return null;
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _signal.Release();
        try { _consumer.Wait(2000); } catch { }
        _shutdown.Dispose();
        _signal.Dispose();
    }

    private sealed record WorkItem(string DocumentId, bool Manual);
}
