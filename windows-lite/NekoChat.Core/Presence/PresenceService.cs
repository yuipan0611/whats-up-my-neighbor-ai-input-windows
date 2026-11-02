using System.Security.Cryptography;
using System.Threading.Channels;
using NekoChat.Core.Storage;
using NekoChat.Core.V2;

namespace NekoChat.Core.Presence;

/// <summary>Signed hello worker. Borrows the store/client; dispose and await this service before disposing either dependency.</summary>
public sealed class PresenceService : IAsyncDisposable
{
    private sealed class Running(PresenceProbe probe, CancellationToken parent)
    {
        internal PresenceProbe Probe { get; } = probe;
        internal CancellationTokenSource Source { get; } = CancellationTokenSource.CreateLinkedTokenSource(parent);
        internal Task Work = Task.CompletedTask;
        private readonly object _gate = new();
        private bool _finished;
        private Task? _cancel;
        internal void Cancel()
        { lock (_gate) { if (!_finished) _cancel ??= Source.CancelAsync(); } }
        internal async Task Finish()
        {
            Task cancellation; lock (_gate) { _finished = true; cancellation = _cancel ?? Task.CompletedTask; }
            try { await cancellation.ConfigureAwait(false); } finally { Source.Dispose(); }
        }
    }
    private readonly object _control = new(), _operations = new();
    private readonly IChatStore _store;
    private readonly V2LanClient _client;
    private readonly PresenceController _controller;
    private readonly TimeProvider _clock;
    private readonly int _port;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<bool> _pulse = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Dictionary<long, Running> _running = new();
    private Dictionary<string, PresenceTarget> _targets = new(StringComparer.Ordinal);
    private Dictionary<string, PresenceSnapshot> _published = new(StringComparer.Ordinal);
    private string[] _visible = [];
    private string? _current;
    private bool _shown, _started, _initialized, _stopping;
    private int _healthy = 1;
    private int _storageOperations;
    private Task _loop = Task.CompletedTask, _timer = Task.CompletedTask;
    private Task? _disposal;
    public event Action? StateChanged;
    public event Action<string>? Diagnostic;
    public int ActiveOperations { get { lock (_operations) return _running.Count; } }
    public int StorageOperations => Volatile.Read(ref _storageOperations);
    public PresenceService(IChatStore store, V2LanClient client, TimeProvider? clock = null, Func<double>? random = null, int port = DirectLanMessaging.DefaultPort)
    {
        ArgumentNullException.ThrowIfNull(store); ArgumentNullException.ThrowIfNull(client); if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        _store = store; _client = client; _clock = clock ?? TimeProvider.System; _controller = new(_clock, random); _port = port;
    }
    public void Start()
    {
        lock (_control)
        {
            ObjectDisposedException.ThrowIf(_stopping, this); if (_started) return; _started = true;
            _loop = Task.Run(Loop); _timer = Task.Run(Timer);
        }
        Wake();
    }
    public void SetView(bool windowVisible, IEnumerable<string> visibleRows, string? currentRow)
    {
        ArgumentNullException.ThrowIfNull(visibleRows); string[] rows = visibleRows.Distinct(StringComparer.Ordinal).ToArray(); IReadOnlyList<long> cancel = [];
        if (rows.Any(r => !StoreIds.IsValid(r)) || currentRow is not null && !StoreIds.IsValid(currentRow)) throw new ArgumentException("Invalid presence view.");
        lock (_control)
        {
            if (_stopping || Volatile.Read(ref _healthy) == 0) return;
            _shown = windowVisible; _visible = rows; _current = currentRow;
            if (_initialized) cancel = _controller.SetView(windowVisible, rows, currentRow);
        }
        Cancel(cancel); Publish(); Wake();
    }
    public void ResetAfterResume()
    {
        IReadOnlyList<long> cancel;
        lock (_control) { if (_stopping || Volatile.Read(ref _healthy) == 0) return; cancel = _controller.ResetAfterResume(); }
        Cancel(cancel); Publish(); Wake();
    }
    public PresenceSnapshot? GetSnapshot(string rowId)
    { lock (_control) return _initialized && !_stopping && Volatile.Read(ref _healthy) == 1 && _targets.ContainsKey(rowId) ? _controller.GetSnapshot(rowId) : null; }
    /// <summary>Wake after a committed roster/trust change. Never reads SQLite on the UI caller.</summary>
    public void Refresh() => Wake();
    private void Wake() => _pulse.Writer.TryWrite(true);
    private async Task Timer()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250), _clock);
            while (await timer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(false))
            {
                if (Volatile.Read(ref _healthy) == 0) break;
                bool refresh; lock (_control) refresh = _shown || !_initialized;
                if (refresh || ActiveOperations != 0) Wake();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch { Volatile.Write(ref _healthy, 0); CancelAll(); Report("presence_unavailable"); Notify(); }
    }
    private async Task Loop()
    {
        try
        {
            await foreach (bool _ in _pulse.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
            {
                IReadOnlyList<PresenceTarget> targets;
                try { targets = _store.GetPresenceTargets(); }
                catch (StoreException) { Report("storage_unavailable"); continue; }
                IReadOnlyList<long> cancel; PresenceWorkBatch batch;
                lock (_control)
                {
                    if (_stopping) break;
                    cancel = _controller.UpdateTargets(targets); _targets = targets.ToDictionary(t => t.RowId, StringComparer.Ordinal);
                    if (!_initialized) { _initialized = true; cancel = cancel.Concat(_controller.SetView(_shown, _visible, _current)).ToArray(); }
                    int free; lock (_operations) free = Math.Max(0, 4 - _running.Count);
                    batch = _controller.TakeDueProbes(free);
                }
                Cancel(cancel.Concat(batch.Cancel));
                foreach (var probe in batch.Start)
                {
                    var running = new Running(probe, _lifetime.Token); running.Source.CancelAfter(TimeSpan.FromSeconds(2));
                    lock (_operations) { _running.Add(probe.Id, running); running.Work = Task.Run(() => Run(running)); }
                }
                Publish();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch { Volatile.Write(ref _healthy, 0); CancelAll(); Report("presence_unavailable"); Notify(); }
    }
    private async Task Run(Running running)
    {
        PresenceWriteJob? job = null; var probe = running.Probe;
        bool Live() => Volatile.Read(ref _healthy) == 1 && !_lifetime.IsCancellationRequested && !running.Source.IsCancellationRequested;
        try
        {
            var proof = await _client.ProbeAsync(probe.Target, _port, () => Live() && _controller.IsCurrent(probe), running.Source.Token).ConfigureAwait(false);
            if (!proof.Success)
                _controller.FailProbe(probe, proof.Code == "hello_key_mismatch" ? ProbeFailure.KeyMismatch : proof.Code == "hello_downgrade" ? ProbeFailure.CapabilityMismatch :
                    proof.Code is "hello_proof_invalid" or "invalid_response" ? ProbeFailure.UnverifiedHello : ProbeFailure.Network);
            else
            {
                var hello = proof.Value!;
                var decision = _controller.CompleteProbe(probe, new(true, hello.ReceiverId, Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(hello.PublicSpkiBase64))).ToLowerInvariant()));
                if (decision.Write is { } pending)
                {
                    job = pending; Interlocked.Increment(ref _storageOperations);
                    try
                    {
                        var result = _store.ApplyRosterPresenceEvidence(job, () => Live() && _controller.IsCurrent(job));
                        _controller.CompletePersistence(job, result);
                    }
                    finally { Interlocked.Decrement(ref _storageOperations); }
                }
            }
        }
        catch (StoreException) { Fail("storage_unavailable"); }
        catch (IOException) { Fail("storage_unavailable"); }
        catch (ObjectDisposedException) { Fail("presence_unavailable"); }
        catch { Fail("presence_unavailable"); }
        finally
        {
            try { await running.Finish().ConfigureAwait(false); } catch { Report("cancellation_failed"); }
            lock (_operations) _running.Remove(probe.Id);
            Publish(); Wake();
        }
        void Fail(string code)
        {
            try { if (job is not null) _controller.CompletePersistence(job, new(false, code)); else _controller.FailProbe(probe); }
            catch { Volatile.Write(ref _healthy, 0); }
            Report(code);
        }
    }
    private void Cancel(IEnumerable<long> ids)
    {
        Running[] work; lock (_operations) work = ids.Distinct().Where(_running.ContainsKey).Select(id => _running[id]).ToArray();
        foreach (var item in work) item.Cancel();
    }
    private void CancelAll() { Running[] work; lock (_operations) work = _running.Values.ToArray(); foreach (var item in work) item.Cancel(); }
    private void Publish()
    {
        bool changed = false, failed = false;
        try
        {
            lock (_control)
            {
                if (!_initialized || _stopping || Volatile.Read(ref _healthy) == 0) return;
                var current = _targets.Keys.ToDictionary(id => id, _controller.GetSnapshot, StringComparer.Ordinal);
                changed = current.Count != _published.Count || current.Any(p => !_published.TryGetValue(p.Key, out var old) || old != p.Value); _published = current;
            }
        }
        catch { Volatile.Write(ref _healthy, 0); failed = true; }
        if (failed) { CancelAll(); Report("presence_unavailable"); } if (changed || failed) Notify();
    }
    private void Notify() { try { StateChanged?.Invoke(); } catch { Report("notification_failed"); } }
    private void Report(string code) { try { Diagnostic?.Invoke(code); } catch { } }
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource done;
        lock (_control)
        {
            if (_disposal is not null) return new(_disposal);
            _stopping = true; Volatile.Write(ref _healthy, 0); done = new(TaskCreationOptions.RunContinuationsAsynchronously); _disposal = done.Task;
            if (_initialized) { try { _controller.SetView(false, [], null); } catch { } }
        }
        _ = Stop(done); return new(done.Task);
    }
    private async Task Stop(TaskCompletionSource done)
    {
        try
        {
            await _lifetime.CancelAsync().ConfigureAwait(false); _pulse.Writer.TryComplete(); CancelAll();
            await Task.WhenAll(_loop, _timer).ConfigureAwait(false);
            Task[] tasks; lock (_operations) tasks = _running.Values.Select(r => r.Work).ToArray();
            await Task.WhenAll(tasks).ConfigureAwait(false); _lifetime.Dispose(); done.SetResult();
        }
        catch (Exception e) { done.SetException(e); }
    }
}
