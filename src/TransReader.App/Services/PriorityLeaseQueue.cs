namespace TransReader.App.Services;

/// <summary>优先级 FIFO 租约队列：同一时刻只放行一个租约，高优先级先出队。</summary>
/// <typeparam name="T">优先级枚举类型，取值 0 为最高优先级。</typeparam>
internal sealed class PriorityLeaseQueue<T> : IDisposable where T : struct, Enum
{
    private readonly object _gate = new();
    private readonly Queue<TaskCompletionSource<IDisposable>>[] _queues;
    private bool _busy;
    private bool _disposed;
    private int _consecutiveHighPriorityServes;

    public PriorityLeaseQueue(int priorityCount)
    {
        _queues = new Queue<TaskCompletionSource<IDisposable>>[priorityCount];
        for (var index = 0; index < _queues.Length; index++)
        {
            _queues[index] = new Queue<TaskCompletionSource<IDisposable>>();
        }
    }

    public Task<IDisposable> AcquireAsync(T priority, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_busy && _queues.All(queue => queue.Count == 0))
            {
                _busy = true;
                return Task.FromResult<IDisposable>(new Lease(this));
            }
            var completion = new TaskCompletionSource<IDisposable>(TaskCreationOptions.RunContinuationsAsynchronously);
            _queues[Convert.ToInt32(priority)].Enqueue(completion);
            if (cancellationToken.CanBeCanceled)
            {
                var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
                // 注册句柄随等待结束释放，避免回调挂在调用方 token 上滞留。
                completion.Task.ContinueWith(
                    _ => registration.Dispose(),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            return completion.Task;
        }
    }

    private void Release()
    {
        lock (_gate)
        {
            var firstNonEmpty = -1;
            for (var index = 0; index < _queues.Length; index++)
            {
                if (_queues[index].Count > 0) { firstNonEmpty = index; break; }
            }
            if (firstNonEmpty < 0)
            {
                _busy = false;
                return;
            }
            var lastNonEmpty = -1;
            for (var index = _queues.Length - 1; index > firstNonEmpty; index--)
            {
                if (_queues[index].Count > 0) { lastNonEmpty = index; break; }
            }
            var selected = firstNonEmpty;
            if (lastNonEmpty > firstNonEmpty)
            {
                // 老化：高优先级连续放行 4 次后让最低优先级出队一次，防止后台任务
                // （如文献库批量分析）在密集前台操作下饿死。
                if (_consecutiveHighPriorityServes >= 4)
                {
                    selected = lastNonEmpty;
                    _consecutiveHighPriorityServes = 0;
                }
                else
                {
                    _consecutiveHighPriorityServes++;
                }
            }
            else
            {
                _consecutiveHighPriorityServes = 0;
            }
            while (_queues[selected].TryDequeue(out var next))
            {
                if (next.TrySetResult(new Lease(this))) return;
            }
            // 所选队列里全是已取消的等待者：重新走完整释放流程选下一个。
            Release();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (var queue in _queues)
            {
                while (queue.TryDequeue(out var next)) next.TrySetCanceled();
            }
        }
    }

    private sealed class Lease(PriorityLeaseQueue<T> owner) : IDisposable
    {
        private PriorityLeaseQueue<T>? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }
}
