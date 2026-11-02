namespace NekoChat.Core.Storage;

public interface IChatStore : IDisposable
{
    /// <summary>Creates the identity row if absent; if present returns its id untouched (never overwrites). Private keys are never passed in.</summary>
    string EnsureLocalIdentity(string? id = null, byte[]? publicKey = null, string? credentialRef = null);

    /// <summary>Human-confirmed flows only. Retires the current identity, supersedes open attempts, drops unsent receipts.</summary>
    ReplaceIdentityResult ReplaceLocalIdentity(string newId, byte[]? newPublicKey, string? newCredentialRef);

    /// <summary>Read-only. Reports; never rebuilds an identity.</summary>
    IdentityCheckResult CheckLocalIdentity(IIdentityKeyProbe probe, Func<byte[], string>? deriveId = null);

    /// <summary>pairingState is required (no default). Existing peers are returned unchanged (idempotent).</summary>
    RegisterPeerResult RegisterPeer(string peerId, PairingState pairingState, byte[]? publicKey = null);

    void SetPairingState(string peerId, PairingState pairingState);

    /// <summary>Handshake/verified-signature path: at most one write per 30 s per peer, with immediate-write exceptions.</summary>
    PeerSeenResult TouchPeerSeen(string peerId, string sourceEndpoint);

    StoreDiagnostics GetDiagnostics();
}
