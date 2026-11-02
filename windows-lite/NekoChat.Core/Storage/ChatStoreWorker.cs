using System.Threading.Channels;

namespace NekoChat.Core.Storage;

/// <summary>Bounded serial work queue. Borrows the store; drain this worker before disposing the store.</summary>
public sealed class ChatStoreWorker : IAsyncDisposable
{
    private interface ICommand { void Run(IChatStore store); }
    private sealed class Command<T>(Func<IChatStore, T> action, CancellationToken cancellation) : ICommand
    {
        internal TaskCompletionSource<T> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Run(IChatStore store)
        {
            if (cancellation.IsCancellationRequested) { Completion.TrySetCanceled(cancellation); return; }
            try { Completion.TrySetResult(action(store)); }
            catch (Exception e) { Completion.TrySetException(e); }
        }
    }
    private readonly Channel<ICommand> _queue;
    private readonly Task _work;
    private readonly CancellationToken _lifetime;
    private int _stopping;
    public ChatStoreWorker(IChatStore store, int capacity = 64, CancellationToken lifetime = default)
    {
        ArgumentNullException.ThrowIfNull(store); if (capacity is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(capacity));
        _lifetime = lifetime; _queue = Channel.CreateBounded<ICommand>(new BoundedChannelOptions(capacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        _work = Task.Run(async () => { await foreach (var command in _queue.Reader.ReadAllAsync().ConfigureAwait(false)) command.Run(store); });
    }
    public Task<T> Run<T>(Func<IChatStore, T> action, CancellationToken? cancellation = null)
    {
        ArgumentNullException.ThrowIfNull(action); ObjectDisposedException.ThrowIf(Volatile.Read(ref _stopping) != 0, this);
        var effective = cancellation ?? _lifetime; effective.ThrowIfCancellationRequested(); var command = new Command<T>(action, effective);
        if (!_queue.Writer.TryWrite(command)) throw new InvalidOperationException("Storage work queue is full.");
        return command.Completion.Task;
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _stopping, 1) == 0) _queue.Writer.TryComplete();
        await _work.ConfigureAwait(false);
    }
}
