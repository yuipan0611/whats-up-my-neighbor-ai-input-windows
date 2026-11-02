namespace NekoChat.Core.Storage;

/// <summary>Public metadata only. Capture before an asynchronous trust operation, then compare inside its final write transaction.</summary>
public sealed record PeerSecuritySnapshot(string PeerId, string? LocalIdentityId, long LocalRevision,
    bool PeerExists, PairingState? Pairing, string? KeySha256, long? PeerRevision);
public sealed record PeerSecurityChange(bool Changed, string Reason, PeerSecuritySnapshot Current);
