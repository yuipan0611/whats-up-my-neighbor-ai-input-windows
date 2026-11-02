using System.Security.Cryptography;
using NekoChat.Core.Storage;

namespace NekoChat.Core.V2;

/// <summary>Bounded source observations from real verified proofs only. A short-window multi-address key is suspended for human review.</summary>
public sealed class V2IdentityMonitor
{
    internal sealed class Observation(V2IdentityMonitor owner, PeerSecuritySnapshot security, string source, long id, long timestamp, DateTime utc)
    {
        internal V2IdentityMonitor Owner { get; } = owner;
        internal PeerSecuritySnapshot Security { get; } = security;
        internal string Source { get; } = source;
        internal long Id { get; } = id;
        internal long Timestamp { get; } = timestamp;
        internal DateTime Utc { get; } = utc;
        internal bool Allowed => Owner.Allowed(this, latest: false);
        internal bool Latest => Owner.Allowed(this, latest: true);
    }
    internal sealed record Decision(string Code, Observation? Proof = null);
    private sealed record Entry(PeerSecuritySnapshot Security, string Source, long Timestamp, long Id, bool Conflict);
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _peers = new(StringComparer.Ordinal);
    internal TimeProvider Clock { get; }
    private readonly int _capacity;
    private readonly TimeSpan _window, _retention;
    private long _next, _last = long.MinValue;
    private bool _failed;
    public int TrackedPeers { get { lock (_gate) return _peers.Count; } }
    public V2IdentityMonitor(TimeProvider? clock = null, int capacity = 1024, TimeSpan? conflictWindow = null)
    {
        Clock = clock ?? TimeProvider.System; _capacity = capacity; _window = conflictWindow ?? TimeSpan.FromSeconds(60);
        if (capacity is < 1 or > 65536 || _window <= TimeSpan.Zero || _window > TimeSpan.FromMinutes(10)) throw new ArgumentOutOfRangeException(nameof(capacity));
        _retention = TimeSpan.FromTicks(Math.Max(TimeSpan.FromMinutes(2).Ticks, checked(_window.Ticks * 2)));
    }
    public void Reset() { lock (_gate) _peers.Clear(); }
    internal Decision Observe(V2VerifiedMessage message, PeerVerificationContext context, string source) =>
        message.SenderId == context.Security.PeerId && message.RecipientId == context.Security.LocalIdentityId ? Observe(context, source) : new("monitor_unavailable");
    internal Decision Observe(V2VerifiedReceipt receipt, PeerVerificationContext context, string source) =>
        receipt.SenderId == context.Security.PeerId && receipt.RecipientId == context.Security.LocalIdentityId ? Observe(context, source) : new("monitor_unavailable");
    internal Decision Observe(V2HelloInfo hello, PeerVerificationContext context, string source)
    {
        if (hello.EvidenceLevel != "E2" || hello.ReceiverId != context.Security.PeerId ||
            Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(hello.PublicSpkiBase64))).ToLowerInvariant() != context.Security.KeySha256) return new("monitor_unavailable");
        return Observe(context, source);
    }
    private Decision Observe(PeerVerificationContext context, string source)
    {
        var security = context.Security;
        if (!LanInvitation.PrivateIPv4(source) || !security.PeerExists || security.Pairing != PairingState.Paired || security.KeySha256 is null) return new("monitor_unavailable");
        lock (_gate)
        {
            long now = Clock.GetTimestamp();
            if (_failed || now < _last || _next == long.MaxValue) { _failed = true; return new("monitor_unavailable"); }
            _last = now;
            foreach (string expired in _peers.Where(p => Clock.GetElapsedTime(p.Value.Timestamp, now) >= _retention).Select(p => p.Key).ToArray()) _peers.Remove(expired);
            if (!_peers.TryGetValue(security.PeerId, out var previous) || previous.Security != security)
                previous = null; // Historical UTC/endpoint is not bound to the current trust epoch and cannot seed a fresh proof.
            if (!_peers.ContainsKey(security.PeerId) && _peers.Count >= _capacity) return new("rate_limited");
            bool conflict = previous is not null && (previous.Conflict || previous.Source != source && Clock.GetElapsedTime(previous.Timestamp, now) < _window);
            long id = ++_next; _peers[security.PeerId] = new(security, source, now, id, conflict);
            return conflict ? new("identity_conflict") : new("ok", new(this, security, source, id, now, Clock.GetUtcNow().UtcDateTime));
        }
    }
    private bool Allowed(Observation proof, bool latest)
    {
        lock (_gate) return !_failed && ReferenceEquals(proof.Owner, this) && _peers.TryGetValue(proof.Security.PeerId, out var entry) &&
            !entry.Conflict && entry.Security == proof.Security && (!latest || entry.Id == proof.Id);
    }
}

/// <summary>Immutable metadata from a nonce-verified, storage-guarded incoming packet. No message text and no JSON constructor.</summary>
public sealed class V2VerifiedActivity
{
    private readonly V2Receiver _owner;
    private readonly long _generation;
    private readonly V2IdentityMonitor.Observation _proof;
    public PeerSecuritySnapshot Security => _proof.Security;
    public string SourceEndpoint => _proof.Source;
    public long ObservedTimestamp => _proof.Timestamp;
    public DateTime ObservedUtc => _proof.Utc;
    internal TimeProvider Clock => _proof.Owner.Clock;
    internal bool Current => _owner.GenerationCurrent(_generation) && _proof.Latest;
    internal V2VerifiedActivity(V2Receiver owner, long generation, V2IdentityMonitor.Observation proof) { _owner = owner; _generation = generation; _proof = proof; }
}
