using NekoChat.Core.Storage;

namespace NekoChat.Core.V2;

/// <summary>Cancellation of one explicit network attempt, not a security epoch or an automatic retry queue.</summary>
public sealed class ManualSendOperation : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _source;
    private readonly CancellationToken _token;
    private Task? _cancellation, _disposal;
    private bool _finished, _userCancellationRequested;
    public string MessageId { get; }
    public int TryCount { get; }
    public CancellationToken Token => _token;
    public bool CancellationRequested => _token.IsCancellationRequested;
    public bool UserCancellationRequested { get { lock (_gate) return _userCancellationRequested; } }
    public bool CanCancel { get { lock (_gate) return !_finished && !_token.IsCancellationRequested; } }
    public ManualSendOperation(PreparedSend prepared, CancellationToken lifetime = default)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (string.IsNullOrEmpty(prepared.MessageId) || prepared.TryCount <= 0) throw new ArgumentException("Invalid manual attempt.");
        MessageId = prepared.MessageId; TryCount = prepared.TryCount;
        _source = CancellationTokenSource.CreateLinkedTokenSource(lifetime); _token = _source.Token;
    }
    /// <summary>Sets intent before returning. Matching callbacks run asynchronously; caller persists cancellation with the same try count.</summary>
    public Task<bool> CancelAsync(string messageId, int tryCount)
    {
        lock (_gate)
        {
            if (_finished || MessageId != messageId || TryCount != tryCount) return Task.FromResult(false);
            _userCancellationRequested = true; _cancellation ??= _source.CancelAsync();
            return Accepted(_cancellation);
        }
    }
    private static async Task<bool> Accepted(Task callbacks) { await callbacks.ConfigureAwait(false); return true; }
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposal is not null) return new(_disposal);
            _finished = true; _cancellation ??= _source.CancelAsync(); return new(_disposal = DisposeCore(_cancellation));
        }
    }
    private async Task DisposeCore(Task callbacks)
    { try { await callbacks.ConfigureAwait(false); } finally { _source.Dispose(); } }
}
