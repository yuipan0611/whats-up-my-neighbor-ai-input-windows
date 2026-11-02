using NekoChat.Core.Roster;
using NekoChat.Core.Storage;

namespace NekoChat.Core.Presence;

/// <summary>Owns memory evidence and bounded scheduling. No timers, sockets, SQL or cancellation callbacks run here.</summary>
public sealed class PresenceController
{
    private sealed class Row(PresenceTarget target, long generation, int order)
    {
        internal PresenceTarget Target = target;
        internal long Generation = generation;
        internal int Order = order, Failures;
        internal bool Active, Suppressed, AwaitingStorage;
        internal PresenceProbe? Probe;
        internal long ScheduleAt, LastFinishedJob, PendingJobId;
        internal TimeSpan Delay;
        internal long? LastResponse, LastVerified, Conflict;
        internal string? VerifiedEndpoint;
        internal DateTime? LastSeenUtc = target.LastSeenUtc;
        internal InstalledStatus Installed = target.RestoredInstalled;
    }
    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private readonly Func<double> _random;
    private readonly Dictionary<string, Row> _rows = new(StringComparer.Ordinal);
    private string[] _visible = [];
    private string? _current;
    private bool _windowVisible, _everShown, _disabled;
    private long _generation, _operation, _lastClock = long.MinValue;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(2);

    public PresenceController(TimeProvider? clock = null, Func<double>? random = null)
    { _clock = clock ?? TimeProvider.System; _random = random ?? Random.Shared.NextDouble; }

    public IReadOnlyList<long> UpdateTargets(IEnumerable<PresenceTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets); var input = targets.ToArray();
        if (input.Any(t => t is null) || input.Select(t => t.RowId).Distinct(StringComparer.Ordinal).Count() != input.Length)
            throw new ArgumentException("Duplicate or null presence targets.", nameof(targets));
        foreach (var target in input) Validate(target);
        lock (_gate)
        {
            long now = Now(); var cancel = new List<long>(); var ids = input.Select(t => t.RowId).ToHashSet(StringComparer.Ordinal);
            foreach (string removed in _rows.Keys.Where(id => !ids.Contains(id)).ToArray()) { Cancel(_rows[removed], cancel); _rows.Remove(removed); }
            for (int i = 0; i < input.Length; i++)
            {
                var target = input[i];
                if (!_rows.TryGetValue(target.RowId, out var row))
                {
                    row = new(target, Next(ref _generation), i); _rows.Add(target.RowId, row); row.Active = Active(target.RowId); row.ScheduleAt = now;
                    continue;
                }
                var old = row.Target;
                bool identitySame = old.PeerId == target.PeerId && old.KeySha256 == target.KeySha256 && old.Pairing == target.Pairing && old.LocalIdentityId == target.LocalIdentityId;
                bool changed = !identitySame || old.DeclaredEndpoint != target.DeclaredEndpoint || old.ObservedEndpoint != target.ObservedEndpoint;
                if (changed)
                {
                    Cancel(row, cancel); row.Generation = Next(ref _generation); row.Failures = 0; row.Suppressed = false; row.ScheduleAt = now; row.Delay = TimeSpan.Zero;
                    if (!identitySame || old.EffectiveEndpoint != target.EffectiveEndpoint && target.EffectiveEndpoint != row.VerifiedEndpoint)
                    { row.LastVerified = null; row.VerifiedEndpoint = null; }
                    row.LastResponse = row.Conflict = null;
                }
                if (old.PeerId != target.PeerId) { row.Installed = target.RestoredInstalled; row.LastSeenUtc = target.LastSeenUtc; }
                else
                {
                    if (row.Installed != InstalledStatus.ConfirmedInstalled) row.Installed = target.RestoredInstalled;
                    row.LastSeenUtc = target.LastSeenUtc ?? row.LastSeenUtc;
                }
                row.Target = target; row.Order = i; row.Active = Active(target.RowId);
            }
            return cancel.AsReadOnly();
        }
    }

    public IReadOnlyList<long> SetView(bool windowVisible, IEnumerable<string> visibleRows, string? currentRow)
    {
        ArgumentNullException.ThrowIfNull(visibleRows); var visible = visibleRows.Distinct(StringComparer.Ordinal).ToArray();
        if (visible.Any(id => !StoreIds.IsValid(id)) || currentRow is not null && !StoreIds.IsValid(currentRow)) throw new ArgumentException("Invalid view row id.");
        lock (_gate)
        {
            long now = Now();
            if (_windowVisible == windowVisible && _current == currentRow && _visible.SequenceEqual(visible)) return Array.Empty<long>();
            bool firstShow = windowVisible && !_everShown; _everShown |= windowVisible; _windowVisible = windowVisible; _visible = visible; _current = currentRow;
            var cancel = new List<long>();
            foreach (var (id, row) in _rows)
            {
                Cancel(row, cancel); row.Generation = Next(ref _generation); row.Active = Active(id); row.Suppressed = false;
                row.ScheduleAt = now; row.Delay = firstShow && id != currentRow ? TimeSpan.FromSeconds(10 * RandomUnit()) : TimeSpan.Zero;
            }
            return cancel.AsReadOnly();
        }
    }

    public IReadOnlyList<long> ResetAfterResume()
    {
        lock (_gate)
        {
            long now = Now(); var cancel = new List<long>();
            foreach (var row in _rows.Values)
            {
                Cancel(row, cancel); row.Generation = Next(ref _generation); row.LastVerified = row.LastResponse = row.Conflict = null;
                row.VerifiedEndpoint = null; row.Failures = 0; row.Suppressed = false; row.ScheduleAt = now; row.Delay = TimeSpan.Zero;
            }
            return cancel.AsReadOnly();
        }
    }

    public PresenceWorkBatch TakeDueProbes()
    {
        lock (_gate)
        {
            long now = Now(); var cancel = new List<long>();
            foreach (var row in _rows.Values)
                if (row.Probe is { } probe && Elapsed(probe.StartedTimestamp, now) >= Deadline) { Cancel(row, cancel); Failed(row, now); }
            int capacity = 4 - _rows.Values.Count(r => r.Probe is not null); var start = new List<PresenceProbe>();
            foreach (var (id, row) in _rows.OrderByDescending(p => p.Key == _current).ThenBy(p => p.Value.Order))
            {
                if (capacity == 0) break;
                if (!row.Active || row.Suppressed || row.Target.EffectiveEndpoint is null || row.Probe is not null || Elapsed(row.ScheduleAt, now) < row.Delay) continue;
                row.Probe = new(this, Next(ref _operation), row.Target, row.Generation, now); start.Add(row.Probe); capacity--;
            }
            return new(start.AsReadOnly(), cancel.AsReadOnly());
        }
    }

    public PresenceDecision FailProbe(PresenceProbe probe, ProbeFailure failure = ProbeFailure.Network)
    {
        ArgumentNullException.ThrowIfNull(probe); if (!Enum.IsDefined(failure)) throw new ArgumentOutOfRangeException(nameof(failure));
        lock (_gate)
        {
            long now = Now();
            if (!TryCurrentProbe(probe, out var row)) return new(false, "stale-target");
            row.Probe = null; Failed(row, now);
            if (Elapsed(probe.StartedTimestamp, now) >= Deadline) return new(false, "expired");
            if (failure == ProbeFailure.KeyMismatch) { row.Conflict = now; row.Suppressed = true; }
            return new(false, failure.ToString());
        }
    }

    public PresenceDecision CompleteProbe(PresenceProbe probe, HelloPresenceProof proof)
    {
        ArgumentNullException.ThrowIfNull(probe); ArgumentNullException.ThrowIfNull(proof);
        lock (_gate)
        {
            long now = Now();
            if (!TryCurrentProbe(probe, out var row)) return new(false, "stale-target");
            row.Probe = null;
            if (Elapsed(probe.StartedTimestamp, now) >= Deadline) { Failed(row, now); return new(false, "expired"); }
            if (!proof.Verified || !StoreIds.IsValid(proof.ReceiverId) || !ValidKeyHash(proof.KeySha256)) { Failed(row, now); return new(false, "unverified"); }
            if (row.Target.Pairing == PairingState.Paired && (row.Target.PeerId != proof.ReceiverId || row.Target.KeySha256 != proof.KeySha256))
            { Failed(row, now); row.Conflict = now; row.Suppressed = true; return new(false, "key-mismatch"); }
            row.LastResponse = now; row.Conflict = row.Target.PeerId is not null && row.Target.PeerId != proof.ReceiverId ? now : null;
            row.Failures = 0; Schedule(row, now);
            if (row.Target.PeerId != proof.ReceiverId || row.Target.Pairing != PairingState.Paired || row.Target.KeySha256 != proof.KeySha256)
                return new(true, row.Conflict.HasValue ? "address-conflict" : "endpoint-responds");
            var job = Job(row, probe.Target.EffectiveEndpoint!, now, probe);
            row.Probe = probe; row.AwaitingStorage = true; row.PendingJobId = job.Id;
            return new(true, "awaiting-storage", job);
        }
    }

    /// <summary>After the actual v2 message/receipt verifier accepts identity and pin; valid even while the chat window is hidden.</summary>
    public PresenceDecision ObservePassive(string rowId, string verifiedPeerId, string keySha256, string sourceEndpoint)
    {
        if (!RosterFields.ValidEndpoint(sourceEndpoint)) throw new ArgumentException("Invalid source endpoint.", nameof(sourceEndpoint));
        lock (_gate)
        {
            long now = Now();
            if (!_rows.TryGetValue(rowId, out var row) || row.Target.PeerId != verifiedPeerId || row.Target.Pairing != PairingState.Paired ||
                !ValidKeyHash(keySha256) || row.Target.KeySha256 != keySha256) return new(false, "untrusted-peer");
            row.Failures = 0; row.Suppressed = false; Schedule(row, now);
            return new(true, "awaiting-storage", Job(row, sourceEndpoint, now, null));
        }
    }

    /// <summary>Use inside the storage transaction after its writer wait and again immediately before commit.</summary>
    public bool IsCurrent(PresenceWriteJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        lock (_gate) return CurrentJob(job, Now(), out _);
    }

    public bool CompletePersistence(PresenceWriteJob job, PresenceWriteResult result)
    {
        ArgumentNullException.ThrowIfNull(job); ArgumentNullException.ThrowIfNull(result);
        lock (_gate)
        {
            long now = Now();
            bool current = CurrentJob(job, now, out var row);
            if (ReferenceEquals(job.Owner, this) && _rows.TryGetValue(job.Target.RowId, out var pending) && pending.PendingJobId == job.Id)
            { pending.Probe = null; pending.AwaitingStorage = false; pending.PendingJobId = 0; }
            if (!current) return false;
            row.LastFinishedJob = job.Id;
            if (!(result.Written && result.Reason == "written" || !result.Written && result.Reason == "throttled")) return false;
            row.LastVerified = job.ObservedTimestamp; row.VerifiedEndpoint = job.SourceEndpoint;
            row.LastResponse = job.ObservedTimestamp; row.Conflict = null; row.LastSeenUtc = job.ObservedUtc; row.Installed = InstalledStatus.ConfirmedInstalled;
            return true;
        }
    }

    public PresenceSnapshot GetSnapshot(string rowId)
    {
        lock (_gate)
        {
            long now = Now();
            if (!_rows.TryGetValue(rowId, out var row)) throw new ArgumentException("Unknown presence row.", nameof(rowId));
            var ttl = TimeSpan.FromSeconds(rowId == _current ? 45 : 90);
            return new(row.Installed, row.LastVerified is null ? PresenceStatus.Unknown : Elapsed(row.LastVerified.Value, now) < ttl ? PresenceStatus.Online : PresenceStatus.Offline,
                row.LastResponse is not null && Elapsed(row.LastResponse.Value, now) < ttl,
                row.Conflict is not null && Elapsed(row.Conflict.Value, now) < ttl, row.LastSeenUtc, row.Generation, row.Failures, row.Suppressed);
        }
    }

    private bool CurrentJob(PresenceWriteJob job, long now, out Row row)
    {
        row = null!;
        return !_disabled && ReferenceEquals(job.Owner, this) && _rows.TryGetValue(job.Target.RowId, out row!) && row.Generation == job.Generation &&
            job.Id > row.LastFinishedJob &&
            row.Target.PeerId == job.Target.PeerId && row.Target.Pairing == PairingState.Paired && row.Target.KeySha256 == job.Target.KeySha256 &&
            (job.Probe is null || row.Active && row.Probe?.Id == job.Probe.Id && row.PendingJobId == job.Id && Elapsed(job.Probe.StartedTimestamp, now) < Deadline);
    }
    private bool TryCurrentProbe(PresenceProbe probe, out Row row)
    {
        row = null!;
        return !_disabled && ReferenceEquals(probe.Owner, this) && _rows.TryGetValue(probe.Target.RowId, out row!) && row.Active &&
            row.Generation == probe.Generation && row.Probe?.Id == probe.Id && !row.AwaitingStorage;
    }
    private PresenceWriteJob Job(Row row, string source, long now, PresenceProbe? probe) => new(this, Next(ref _operation), row.Target, row.Generation, source, now, _clock.GetUtcNow().UtcDateTime, probe);
    private bool Active(string id) => _windowVisible && (_current == id || _visible.Contains(id, StringComparer.Ordinal));
    private void Failed(Row row, long now) { row.Failures = Math.Min(3, row.Failures + 1); Schedule(row, now); }
    private void Schedule(Row row, long now)
    {
        row.ScheduleAt = now;
        row.Delay = row.Failures >= 3 ? TimeSpan.FromSeconds(120) : TimeSpan.FromSeconds((row.Target.RowId == _current ? 15 : 30) * (.8 + .4 * RandomUnit()));
    }
    private static void Cancel(Row row, List<long> cancel)
    { if (row.Probe is { } probe) cancel.Add(probe.Id); row.Probe = null; row.AwaitingStorage = false; row.PendingJobId = 0; }
    private double RandomUnit() { double n = _random(); return double.IsFinite(n) && n is >= 0 and <= 1 ? n : throw new InvalidOperationException("Invalid probe jitter sample."); }
    private long Now()
    {
        if (_disabled) throw new InvalidOperationException("Presence controller is unavailable.");
        long n = _clock.GetTimestamp();
        if (n < _lastClock) { _disabled = true; throw new InvalidOperationException("Monotonic clock moved backward."); }
        _lastClock = n; return n;
    }
    private long Next(ref long counter)
    {
        if (counter == long.MaxValue) { _disabled = true; throw new InvalidOperationException("Presence counter exhausted."); }
        return ++counter;
    }
    private TimeSpan Elapsed(long start, long now) => _clock.GetElapsedTime(start, now);
    private static bool ValidKeyHash(string? hash) => hash is { Length: 64 } && hash.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
    private static void Validate(PresenceTarget t)
    {
        if (!StoreIds.IsValid(t.RowId) || t.PeerId is not null && !StoreIds.IsValid(t.PeerId) || t.LocalIdentityId is not null && !StoreIds.IsValid(t.LocalIdentityId) ||
            t.KeySha256 is not null && !ValidKeyHash(t.KeySha256) || t.Pairing.HasValue && !Enum.IsDefined(t.Pairing.Value) || !Enum.IsDefined(t.RestoredInstalled) ||
            t.DeclaredEndpoint is not null && !RosterFields.ValidEndpoint(t.DeclaredEndpoint) || t.PeerId is null && (t.KeySha256 is not null || t.Pairing.HasValue))
            throw new ArgumentException("Invalid presence target.");
    }
}
