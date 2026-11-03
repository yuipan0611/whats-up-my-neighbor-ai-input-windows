using NekoChat.Core.Roster;

namespace NekoChat.Core.Storage;

/// <summary>Immutable local selection, not permission or proof of consent. Capture before pairing; never serialize into wire/diagnostics.</summary>
public sealed class PairingRosterTarget
{
    internal object Owner { get; }
    internal RosterSnapshotRow Original { get; }
    internal string Token { get; }
    internal string? LocalIdentityId { get; }
    internal long LocalRevision { get; }
    internal long RosterRevision { get; }
    internal PeerSecuritySnapshot? BoundSecurity { get; }
    public PairingState? Pairing => BoundSecurity?.Pairing;
    public bool HasBoundSigningKey => BoundSecurity?.KeySha256 is not null;
    public string RowId => Original.Entry.Id;
    public string DisplayName => Original.Entry.DisplayName;
    public string? StudentNumber => Original.Entry.StudentNumber;
    public bool IsTeacher => Original.Entry.IsTeacher;
    public string? Endpoint => Original.Entry.Endpoint;
    public string? BoundPeerId => Original.Entry.PeerId;
    internal PairingRosterTarget(object owner, RosterSnapshotRow row, long revision, string? local, long localRevision, PeerSecuritySnapshot? boundSecurity = null)
    { Owner = owner; Original = row; RosterRevision = revision; LocalIdentityId = local; LocalRevision = localRevision; Token = RosterImportPreview.ComputeSnapshotToken([row]); BoundSecurity = boundSecurity; }
    internal PairingRosterTarget AfterBinding(string peer)
    {
        if (BoundPeerId == peer) return this;
        var e = Original.Entry;
        return new(Owner, Original with { Entry = new(e.Id, e.DisplayName, e.StudentNumber, e.IsTeacher, e.Endpoint, peer) }, checked(RosterRevision + 1), LocalIdentityId, LocalRevision, BoundSecurity);
    }
    internal PairingRosterTarget AfterUnbinding()
    {
        var e = Original.Entry;
        return new(Owner, Original with { Entry = new(e.Id, e.DisplayName, e.StudentNumber, e.IsTeacher, e.Endpoint, null) }, checked(RosterRevision + 1), LocalIdentityId, LocalRevision);
    }
    public override string ToString() => "Captured local pairing selection";
}
public sealed record RosterPairingResult(bool Completed, string Reason, PeerSecuritySnapshot Current, bool BindingChanged);
