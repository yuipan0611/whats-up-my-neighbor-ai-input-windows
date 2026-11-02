using NekoChat.Core.Roster;
using NekoChat.Core.Storage;

namespace NekoChat.Core.Presence;

public enum InstalledStatus { Unknown, ConfirmedInstalled, ConfirmedNotInstalled }
public enum PresenceStatus { Unknown, Online, Offline }
public enum ProbeFailure { Network, UnverifiedHello, KeyMismatch, CapabilityMismatch }
public sealed record PresenceTarget(string RowId, string? PeerId, string? KeySha256, PairingState? Pairing,
    string? DeclaredEndpoint, string? ObservedEndpoint, string? LocalIdentityId,
    InstalledStatus RestoredInstalled = InstalledStatus.Unknown, DateTime? LastSeenUtc = null,
    long LocalRevision = 0, long PeerRevision = 0, long RosterRevision = 0)
{
    public string? EffectiveEndpoint => ObservedEndpoint is not null && RosterFields.ValidEndpoint(ObservedEndpoint) ? ObservedEndpoint : DeclaredEndpoint;
    public bool SameSecurityRoute(PresenceTarget? other) => other is not null && RowId == other.RowId && PeerId == other.PeerId && KeySha256 == other.KeySha256 && Pairing == other.Pairing &&
        DeclaredEndpoint == other.DeclaredEndpoint && ObservedEndpoint == other.ObservedEndpoint && LocalIdentityId == other.LocalIdentityId &&
        LocalRevision == other.LocalRevision && PeerRevision == other.PeerRevision && RosterRevision == other.RosterRevision;
}
public sealed record PresenceSnapshot(InstalledStatus Installed, PresenceStatus Presence, bool EndpointResponds,
    bool AddressConflict, DateTime? LastSeenUtc, long Generation, int Failures, bool ProbeSuppressed);
/// <summary>Trusted adapter metadata after the real v2 verifier has checked structure, fresh nonce, pin, signature and capabilities; never a JSON target.</summary>
public sealed record HelloPresenceProof(bool Verified, string ReceiverId, string KeySha256);
public sealed class PresenceProbe
{
    internal object Owner { get; }
    public long Id { get; }
    public PresenceTarget Target { get; }
    public long Generation { get; }
    public long StartedTimestamp { get; }
    internal PresenceProbe(object owner, long id, PresenceTarget target, long generation, long started)
    { Owner = owner; Id = id; Target = target; Generation = generation; StartedTimestamp = started; }
}
public sealed record PresenceWorkBatch(IReadOnlyList<PresenceProbe> Start, IReadOnlyList<long> Cancel);
public sealed class PresenceWriteJob
{
    internal object Owner { get; }
    public long Id { get; }
    public PresenceTarget Target { get; }
    public long Generation { get; }
    public string SourceEndpoint { get; }
    public long ObservedTimestamp { get; }
    public DateTime ObservedUtc { get; }
    public PresenceProbe? Probe { get; }
    internal PresenceWriteJob(object owner, long id, PresenceTarget target, long generation, string source, long observed, DateTime utc, PresenceProbe? probe)
    { Owner = owner; Id = id; Target = target; Generation = generation; SourceEndpoint = source; ObservedTimestamp = observed; ObservedUtc = utc; Probe = probe; }
}
public sealed record PresenceDecision(bool Accepted, string Reason, PresenceWriteJob? Write = null);
