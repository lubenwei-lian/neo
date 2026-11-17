using System.Threading.Channels;

namespace Neo.Core.Diagnostics;

/// <summary>有限队列、批量文件写入与显式刷盘屏障；错误日志保留同步写入保障。</summary>
public sealed class BufferedLogWriter<T> : IAsyncDisposable where T : class
{
    private sealed record Work(T? Item, TaskCompletionSource<bool>? Barrier = null);
    private readonly Channel<Work> _queue;
    private readonly Action<IReadOnlyList<T>> _sink;
    private readonly object _sinkGate = new();
    private readonly Task _worker;
    private long _dropped, _failed;
    private long _reportedFailures;
    public long Dropped => Interlocked.Read(ref _dropped);
    public long FailedBatches => Interlocked.Read(ref _failed);

    public BufferedLogWriter(Action<IReadOnlyList<T>> sink, int capacity = 4096)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _sink = sink;
        _queue = Channel.CreateBounded<Work>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });
        _worker = Task.Run(RunAsync);
    }

    public bool Write(T item, bool critical = false)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (critical) return Persist(new[] { item });
        if (_queue.Writer.TryWrite(new Work(item))) return true;
        Interlocked.Increment(ref _dropped); return false;
    }

    public async Task<bool> FlushAsync(TimeSpan timeout)
    {
        using var budget = new CancellationTokenSource(timeout);
        var barrier = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await _queue.Writer.WriteAsync(new Work(null, barrier), budget.Token).ConfigureAwait(false);
            return await barrier.Task.WaitAsync(budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return false; }
        catch (ChannelClosedException) { return false; }
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _worker.ConfigureAwait(false);
    }

    private bool Persist(IReadOnlyList<T> batch)
    {
        try { lock (_sinkGate) _sink(batch); return true; }
        catch { Interlocked.Increment(ref _failed); return false; }
    }

    private async Task RunAsync()
    {
        var batch = new List<T>(128);
        while (await _queue.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            // 短暂合并突发日志；不会在调用者（UI/内核输出线程）执行文件 I/O。
            await Task.Delay(20).ConfigureAwait(false);
            do
            {
                while (batch.Count < 128 && _queue.Reader.TryRead(out var work))
                {
                    if (work.Item != null) batch.Add(work.Item);
                    if (work.Barrier != null)
                    {
                        if (batch.Count > 0) { Persist(batch); batch.Clear(); }
                        var failed = FailedBatches;
                        work.Barrier.TrySetResult(failed == Interlocked.Exchange(ref _reportedFailures, failed));
                    }
                }
                if (batch.Count > 0) { Persist(batch); batch.Clear(); }
            }
            while (_queue.Reader.TryPeek(out _));
        }
    }
}
