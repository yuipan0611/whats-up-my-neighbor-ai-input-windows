namespace NekoChat.Core.Storage;

/// <summary>Public metadata only. Capture before an asynchronous trust operation, then compare inside its final write transaction.</summary>
public sealed record PeerSecuritySnapshot(string PeerId, string? LocalIdentityId, long LocalRevision,
    bool PeerExists, PairingState? Pairing, string? KeySha256, long? PeerRevision);
public sealed record PeerSecurityChange(bool Changed, string Reason, PeerSecuritySnapshot Current);
public sealed record PairingCandidateResult(bool Ready, string Reason, PeerSecuritySnapshot Current);
/// <summary>One atomic read of public key metadata and the revisions used to fence asynchronous verification.</summary>
public sealed record PeerVerificationContext(PeerSecuritySnapshot Security, string? PeerSpkiBase64, string? LocalSpkiBase64);
