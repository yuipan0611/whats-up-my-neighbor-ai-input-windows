using System.Threading.Channels;
using NekoChat.Core.Storage;

namespace NekoChat.Core.V2;

/// <summary>System receipts only. Never sends a user draft/message; borrows and drains before store/client/key disposal.</summary>
public sealed class ReadReceiptService : IAsyncDisposable
{
    private readonly IChatStore _store;
    private readonly V2LanClient _client;
    private readonly TimeProvider _clock;
    private readonly int _port;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Dictionary<string, (long At, int Delay)> _peers = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private Task _loop = Task.CompletedTask, _timer = Task.CompletedTask;
    private Task? _disposal;
    private long _generation, _seenGeneration, _last = long.MinValue;
    private bool _started;
    internal long Passes, Attempts;
    public event Action? ReceiptsChanged;
    public event Action<string>? Diagnostic;
    public ReadReceiptService(IChatStore store, V2LanClient client, int port = DirectLanMessaging.DefaultPort, TimeProvider? clock = null)
    { ArgumentNullException.ThrowIfNull(store); ArgumentNullException.ThrowIfNull(client); if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port)); _store = store; _client = client; _port = port; _clock = clock ?? TimeProvider.System; }
    public void Start()
    { lock (_gate) { ObjectDisposedException.ThrowIf(_disposal is not null, this); if (_started) return; _started = true; _loop = Task.Run(Loop); _timer = Task.Run(Timer); } Pulse(); }
    public void Pulse() => _wake.Writer.TryWrite(true);
    public void ResetAfterResume() { Interlocked.Increment(ref _generation); Pulse(); }
    private async Task Timer()
    {
        try { using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2), _clock); while (await timer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(false)) Pulse(); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }
    private async Task Loop()
    {
        try
        {
            await foreach (bool _ in _wake.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
            {
                try
                {
                    long now = _clock.GetTimestamp(), generation = Volatile.Read(ref _generation);
                    if (now < _last) { Report("receipt_clock_unavailable"); break; } _last = now;
                    if (generation != _seenGeneration) { _peers.Clear(); _seenGeneration = generation; }
                    foreach (string expired in _peers.Where(p => _clock.GetElapsedTime(p.Value.At, now) >= TimeSpan.FromMinutes(2)).Select(p => p.Key).ToArray()) _peers.Remove(expired);
                    if (!_store.GetReadReceiptPolicy().Enabled) continue;
                    var pending = _store.ListPendingReadReceipts(200); if (pending.Count == 0) continue;
                    var inbox = _store.GetInboxSnapshot();
                    var routes = _store.GetPresenceTargets();
                    var selected = pending.GroupBy(r => r.OriginalSenderId, StringComparer.Ordinal).Where(g =>
                        !_peers.TryGetValue(g.Key, out var previous) || _clock.GetElapsedTime(previous.At, now) >= TimeSpan.FromSeconds(previous.Delay)).Take(2).Select(g => g.First()).ToArray();
                    foreach (var receipt in selected)
                    {
                        if (_lifetime.IsCancellationRequested || generation != Volatile.Read(ref _generation)) break;
                        if (!_peers.ContainsKey(receipt.OriginalSenderId) && _peers.Count >= 1024) continue;
                        var target = inbox.Entries.FirstOrDefault(e => e.PeerId == receipt.OriginalSenderId && e.Pairing == PairingState.Paired && e.Endpoint is not null);
                        string? endpoint = routes.FirstOrDefault(t => t.PeerId == receipt.OriginalSenderId && t.Pairing == PairingState.Paired && t.EffectiveEndpoint is not null)?.EffectiveEndpoint ?? target?.Endpoint;
                        if (endpoint is null || !LanInvitation.PrivateIPv4(endpoint)) { _peers[receipt.OriginalSenderId] = (now, 30); continue; }
                        Interlocked.Increment(ref Attempts);
                        var result = await _client.SendReadReceiptAsync(receipt, endpoint, _port, () => !_lifetime.IsCancellationRequested && generation == Volatile.Read(ref _generation), _lifetime.Token).ConfigureAwait(false);
                        _peers[receipt.OriginalSenderId] = (_clock.GetTimestamp(), result.Completed ? 15 : 30);
                        if (result.Completed) { try { ReceiptsChanged?.Invoke(); } catch { Report("receipt_notification_failed"); } }
                    }
                }
                catch (Exception e) when (e is StoreException or IOException or ObjectDisposedException) { Report("receipt_storage_unavailable"); }
                finally { Interlocked.Increment(ref Passes); }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch { Report("receipt_service_unavailable"); }
    }
    private void Report(string code) { try { Diagnostic?.Invoke(code); } catch { } }
    public ValueTask DisposeAsync() { lock (_gate) return new(_disposal ??= Stop()); }
    private async Task Stop()
    { Interlocked.Increment(ref _generation); await _lifetime.CancelAsync().ConfigureAwait(false); _wake.Writer.TryComplete(); await Task.WhenAll(_loop, _timer).ConfigureAwait(false); _lifetime.Dispose(); }
}
