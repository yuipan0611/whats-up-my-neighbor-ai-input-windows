using System.Security.Cryptography;
using System.Threading.Channels;
using NekoChat.Core.Roster;
using NekoChat.Core.Storage;
using NekoChat.Core.V2;

namespace NekoChat.Core.Presence;

/// <summary>Signed hello worker. Borrows the store/client; dispose and await this service before disposing either dependency.</summary>
public sealed class PresenceService : IAsyncDisposable
{
    private sealed class Running(long key, PresenceProbe? probe, V2VerifiedActivity? activity, CancellationToken parent)
    {
        internal long Key { get; } = key;
        internal PresenceProbe? Probe { get; } = probe;
        internal V2VerifiedActivity? Activity { get; } = activity;
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
    private PresenceController _controller;
    private readonly Func<double>? _random;
    private readonly TimeProvider _clock;
    private readonly int _port;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<bool> _pulse = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Dictionary<long, Running> _running = new();
    private readonly Dictionary<string, V2VerifiedActivity> _pendingActivity = new(StringComparer.Ordinal);
    private long _passiveOperation;
    private V2Receiver? _receiver;
    private Dictionary<string, PresenceTarget> _targets = new(StringComparer.Ordinal);
    private Dictionary<string, PresenceSnapshot> _published = new(StringComparer.Ordinal);
    private string[] _visible = [];
    private string? _current;
    private bool _shown, _started, _initialized, _stopping;
    private int _healthy = 1;
    private int _storageOperations;
    // Supervision (v2 fix): a transient failure restarts the worker with backoff; only repeated failures make presence unavailable until TryRestart().
    private const int MaxConsecutiveFailures = 5;
    private int _failures, _faulted, _targetReads;
    // Roster targets are re-read from SQLite on events (Refresh, activity, resume, conflict) and at most every TargetRefreshInterval, not on every 250 ms tick.
    private long _targetsVersion, _targetsApplied = -1, _targetsReadAt;
    private IReadOnlyList<PresenceTarget> _targetList = [];
    internal TimeSpan TargetRefreshInterval { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>Base restart delay; the n-th consecutive failure waits base * 2^(n-1), at most 30 times the base.</summary>
    internal TimeSpan RestartBackoff { get; init; } = TimeSpan.FromSeconds(1);
    /// <summary>Number of SQLite roster-target reads performed by this service (diagnostics and tests).</summary>
    public int TargetReads => Volatile.Read(ref _targetReads);
    /// <summary>False after repeated worker failures; <see cref="TryRestart"/> can recover it.</summary>
    public bool Healthy => Volatile.Read(ref _healthy) == 1;
    private Task _loop = Task.CompletedTask, _timer = Task.CompletedTask;
    private Task? _disposal;
    public event Action? StateChanged;
    public event Action<string>? Diagnostic;
    public int ActiveOperations { get { lock (_operations) return _running.Count; } }
    public int StorageOperations => Volatile.Read(ref _storageOperations);
    public int PendingActivities { get { lock (_control) return _pendingActivity.Count; } }
    public PresenceService(IChatStore store, V2LanClient client, TimeProvider? clock = null, Func<double>? random = null, int port = DirectLanMessaging.DefaultPort)
    {
        ArgumentNullException.ThrowIfNull(store); ArgumentNullException.ThrowIfNull(client); if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        _store = store; _client = client; _clock = clock ?? TimeProvider.System; _random = random; _controller = new(_clock, random); _port = port;
    }
    public void Start()
    {
        lock (_control)
        {
            ObjectDisposedException.ThrowIf(_stopping, this); if (_started) return; _started = true; MarkTargetsDirty();
            _loop = Task.Run(Loop); _timer = Task.Run(Timer);
        }
        Wake();
    }
    public void AttachReceiver(V2Receiver receiver)
    {
        ArgumentNullException.ThrowIfNull(receiver);
        lock (_control)
        {
            if (_started || _stopping || _receiver is not null || !ReferenceEquals(receiver.Clock, _clock)) throw new InvalidOperationException("Attach one same-clock receiver before starting presence.");
            _receiver = receiver; receiver.VerifiedActivity += ObserveActivity; receiver.Diagnostic += ReceiverDiagnostic;
        }
    }
    public void ObserveActivity(V2VerifiedActivity activity)
    {
        ArgumentNullException.ThrowIfNull(activity); bool overflow = false;
        lock (_control)
        {
            if (_stopping || Volatile.Read(ref _healthy) == 0 || !ReferenceEquals(activity.Clock, _clock) || !activity.Current || !RosterFields.ValidEndpoint(activity.SourceEndpoint)) return;
            string peer = activity.Security.PeerId;
            if (!_pendingActivity.TryGetValue(peer, out var old) && _pendingActivity.Count >= 1024) overflow = true;
            else if (old is null || activity.ObservedTimestamp >= old.ObservedTimestamp) _pendingActivity[peer] = activity;
        }
        MarkTargetsDirty();
        if (overflow) Report("presence_activity_overflow"); else Wake();
    }
    private void ReceiverDiagnostic(string code) { if (code == "identity_conflict") { MarkTargetsDirty(); Wake(); } Report(code); }
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
        IReadOnlyList<long> cancel; Running[] old;
        lock (_control)
        {
            if (_stopping || Volatile.Read(ref _healthy) == 0) return; cancel = _controller.ResetAfterResume(); _pendingActivity.Clear();
            lock (_operations) old = _running.Values.ToArray();
        }
        MarkTargetsDirty(); _receiver?.ResetReplayProtection(); Cancel(cancel); foreach (var work in old) work.Cancel(); Publish(); Wake();
    }
    public PresenceSnapshot? GetSnapshot(string rowId)
    { lock (_control) return _initialized && !_stopping && Volatile.Read(ref _healthy) == 1 && _targets.ContainsKey(rowId) ? _controller.GetSnapshot(rowId) : null; }
    /// <summary>Wake after a committed roster/trust change. Never reads SQLite on the UI caller.</summary>
    public void Refresh() { MarkTargetsDirty(); Wake(); }
    private void MarkTargetsDirty() => Interlocked.Increment(ref _targetsVersion);
    private void Wake() => _pulse.Writer.TryWrite(true);
    /// <summary>Manual recovery after the service gave up (for example after resume or a user retry). Starts fresh worker state; no effect while stopping.</summary>
    public bool TryRestart()
    {
        lock (_control)
        {
            if (_stopping || !_started) return false;
            if (Volatile.Read(ref _healthy) == 1 && !_loop.IsCompleted) return true;
            Volatile.Write(ref _failures, 0); Volatile.Write(ref _faulted, 0);
            _controller = new(_clock, _random); _initialized = false; _targets = new(StringComparer.Ordinal); _published = new(StringComparer.Ordinal); _pendingActivity.Clear(); MarkTargetsDirty();
            Volatile.Write(ref _healthy, 1);
            if (_loop.IsCompleted) _loop = Task.Run(Loop);
            if (_timer.IsCompleted) _timer = Task.Run(Timer);
        }
        Wake(); return true;
    }
    private async Task Timer()
    {
        for (int failures = 0; ; )
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250), _clock);
                while (await timer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(false))
                {
                    if (Volatile.Read(ref _healthy) == 0) return;
                    bool refresh; lock (_control) refresh = _shown || !_initialized;
                    if (refresh || ActiveOperations != 0 || PendingActivities != 0) Wake();
                    failures = 0;
                }
                return;
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
            catch
            {
                Report("presence_timer_restarting");
                if (++failures > MaxConsecutiveFailures) { Volatile.Write(ref _healthy, 0); CancelAll(); Report("presence_unavailable"); Notify(); return; }
                try { await Task.Delay(TimeSpan.FromTicks(Math.Min(30, 1 << (failures - 1)) * RestartBackoff.Ticks), _clock, _lifetime.Token).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
            }
        }
    }
    private async Task Loop()
    {
        while (true)
        {
            try { await LoopCore().ConfigureAwait(false); return; }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
            catch { if (!await RecoverAsync().ConfigureAwait(false)) return; }
        }
    }
    /// <summary>One failure: cancel work, rebuild controller state from SQLite, back off 1,2,4,8,16 s. After more than <see cref="MaxConsecutiveFailures"/> consecutive failures presence becomes unavailable.</summary>
    private async Task<bool> RecoverAsync()
    {
        int attempt = Interlocked.Increment(ref _failures); CancelAll();
        if (attempt > MaxConsecutiveFailures) { Volatile.Write(ref _healthy, 0); Report("presence_unavailable"); Notify(); return false; }
        lock (_control)
        {
            if (_stopping) return false;
            _controller = new(_clock, _random); _initialized = false; _targets = new(StringComparer.Ordinal); _published = new(StringComparer.Ordinal); _pendingActivity.Clear(); MarkTargetsDirty();
        }
        Report("presence_restarting"); Notify();
        try { await Task.Delay(TimeSpan.FromTicks(Math.Min(30, 1 << (attempt - 1)) * RestartBackoff.Ticks), _clock, _lifetime.Token).ConfigureAwait(false); } catch (OperationCanceledException) { return false; }
        return true;
    }
    private async Task LoopCore()
    {
            await foreach (bool _ in _pulse.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
            {
                if (Interlocked.Exchange(ref _faulted, 0) == 1) throw new InvalidOperationException("Presence publish failed.");
                V2VerifiedActivity[] activities;
                lock (_control)
                {
                    int free; lock (_operations) free = Math.Max(0, 4 - _running.Count);
                    activities = _pendingActivity.Values.Take(free).ToArray(); foreach (var activity in activities) _pendingActivity.Remove(activity.Security.PeerId);
                }
                IReadOnlyList<PresenceTarget> targets; bool reload; long version = Volatile.Read(ref _targetsVersion);
                lock (_control) reload = !_initialized || _targetsApplied != version || _clock.GetElapsedTime(_targetsReadAt) >= TargetRefreshInterval;
                if (reload)
                {
                    try { targets = _store.GetPresenceTargets(); Interlocked.Increment(ref _targetReads); }
                    catch (StoreException)
                    {
                        lock (_control) foreach (var activity in activities)
                            if (activity.Current && _pendingActivity.Count < 1024 && !_pendingActivity.ContainsKey(activity.Security.PeerId)) _pendingActivity[activity.Security.PeerId] = activity;
                        Report("storage_unavailable"); continue;
                    }
                }
                else lock (_control) targets = _targetList;
                IReadOnlyList<long> cancel; PresenceWorkBatch batch;
                lock (_control)
                {
                    if (_stopping) break;
                    if (reload)
                    {
                        cancel = _controller.UpdateTargets(targets); _targets = targets.ToDictionary(t => t.RowId, StringComparer.Ordinal);
                        _targetList = targets; _targetsApplied = version; _targetsReadAt = _clock.GetTimestamp();
                    }
                    else cancel = [];
                    if (!_initialized) { _initialized = true; cancel = cancel.Concat(_controller.SetView(_shown, _visible, _current)).ToArray(); }
                    int free; lock (_operations) free = Math.Max(0, 4 - _running.Count);
                    batch = _controller.TakeDueProbes(Math.Max(0, free - activities.Length));
                }
                Cancel(cancel.Concat(batch.Cancel));
                foreach (var probe in batch.Start)
                {
                    var running = new Running(probe.Id, probe, null, _lifetime.Token); running.Source.CancelAfter(TimeSpan.FromSeconds(2));
                    lock (_operations) { _running.Add(probe.Id, running); running.Work = Task.Run(() => Run(running)); }
                }
                foreach (var activity in activities)
                {
                    if (!activity.Current) continue;
                    long key; lock (_control) { if (_passiveOperation == long.MinValue) throw new InvalidOperationException("Presence operation counter exhausted."); key = --_passiveOperation; }
                    var running = new Running(key, null, activity, _lifetime.Token);
                    lock (_operations) { _running.Add(key, running); running.Work = Task.Run(() => Run(running)); }
                }
                Publish();
                Volatile.Write(ref _failures, 0);
            }
    }
    private async Task Run(Running running)
    {
        PresenceWriteJob? job = null; var probe = running.Probe;
        bool Live() => Volatile.Read(ref _healthy) == 1 && !_lifetime.IsCancellationRequested && !running.Source.IsCancellationRequested;
        try
        {
            if (running.Activity is { } activity)
            {
                PresenceTarget[] targets; lock (_control) targets = _targets.Values.Where(t =>
                    t.PeerId == activity.Security.PeerId && t.Pairing == PairingState.Paired && t.KeySha256 == activity.Security.KeySha256 &&
                    t.LocalIdentityId == activity.Security.LocalIdentityId && t.LocalRevision == activity.Security.LocalRevision && t.PeerRevision == activity.Security.PeerRevision).ToArray();
                bool refresh = false;
                foreach (var target in targets)
                {
                    if (!Live() || !activity.Current) break;
                    var decision = _controller.ObservePassive(target.RowId, activity.Security.PeerId, activity.Security.KeySha256!, activity.SourceEndpoint, activity.ObservedTimestamp, activity.ObservedUtc);
                    if (decision.Write is not { } pending) continue;
                    job = pending; Interlocked.Increment(ref _storageOperations);
                    try
                    {
                        var result = _store.ApplyRosterPresenceEvidence(job, () => Live() && activity.Current && _controller.IsCurrent(job));
                        _controller.CompletePersistence(job, result); refresh |= result.Reason == "stale-target"; MarkTargetsDirty();
                    }
                    finally { Interlocked.Decrement(ref _storageOperations); }
                }
                if (refresh && Live()) ObserveActivity(activity);
                return;
            }
            if (probe is null) return;
            var proof = await _client.ProbeAsync(probe.Target, _port, () => Live() && _controller.IsCurrent(probe), running.Source.Token).ConfigureAwait(false);
            if (!proof.Success)
                _controller.FailProbe(probe, proof.Code is "hello_key_mismatch" or "identity_conflict" ? ProbeFailure.KeyMismatch : proof.Code == "hello_downgrade" ? ProbeFailure.CapabilityMismatch :
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
                        _controller.CompletePersistence(job, result); MarkTargetsDirty();   // the write changed lastSeen/evidence that the cached targets carry
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
            lock (_operations) _running.Remove(running.Key);
            Publish(); Wake();
        }
        void Fail(string code)
        {
            try { if (job is not null) _controller.CompletePersistence(job, new(false, code)); else if (probe is not null) _controller.FailProbe(probe); }
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
        catch { Interlocked.Exchange(ref _faulted, 1); failed = true; }
        if (failed) { Report("presence_publish_failed"); Wake(); } if (changed) Notify();
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
            _pendingActivity.Clear();
            if (_initialized) { try { _controller.SetView(false, [], null); } catch { } }
        }
        _ = Stop(done); return new(done.Task);
    }
    private async Task Stop(TaskCompletionSource done)
    {
        try
        {
            if (_receiver is { } receiver) { receiver.VerifiedActivity -= ObserveActivity; receiver.Diagnostic -= ReceiverDiagnostic; }
            await _lifetime.CancelAsync().ConfigureAwait(false); _pulse.Writer.TryComplete(); CancelAll();
            await Task.WhenAll(_loop, _timer).ConfigureAwait(false);
            Task[] tasks; lock (_operations) tasks = _running.Values.Select(r => r.Work).ToArray();
            await Task.WhenAll(tasks).ConfigureAwait(false); _lifetime.Dispose(); done.SetResult();
        }
        catch (Exception e) { done.SetException(e); }
    }
}
