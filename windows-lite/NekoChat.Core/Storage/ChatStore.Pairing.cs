using System.Security.Cryptography;
using NekoChat.Core.V2;

namespace NekoChat.Core.Storage;

public sealed partial class ChatStore
{
    /// <summary>One transaction validates the pre-request revision/state and creates or pins a candidate. This is NOT human consent.</summary>
    public PairingCandidateResult BeginPairingCandidateIfCurrent(PeerSecuritySnapshot expected, byte[] canonicalPublicKey, Func<bool> stillCurrent)
    {
        ArgumentNullException.ThrowIfNull(expected); ArgumentNullException.ThrowIfNull(canonicalPublicKey); ArgumentNullException.ThrowIfNull(stillCurrent);
        if (canonicalPublicKey.Length != 91) throw new ArgumentException("Invalid pairing candidate key.");
        byte[] key = canonicalPublicKey.ToArray(); using var publicKey = V2Crypto.OpenCanonicalSigningKey(key);
        if (publicKey is null || V2Crypto.PeerId(key) != expected.PeerId) throw new ArgumentException("Invalid pairing candidate key.");
        string fingerprint = Convert.ToHexString(SHA256.HashData(key)).ToLowerInvariant();
        PeerSecuritySnapshot Current(WriteContext context) => context.Query(PeerSecuritySql, row => MapPeerSecurity(row, expected.PeerId), ("@peer", expected.PeerId)).Single();
        return Write("pair-candidate", context => {
            var current = Current(context);
            if (!stillCurrent() || current != expected) return (new PairingCandidateResult(false, "stale-security", current), false);
            var local = context.Query(LocalIdentitySql, MapLocalIdentity).Single();
            if (local.Id is null || local.PublicSpkiBase64 is null || local.CredentialRef is null)
                return (new PairingCandidateResult(false, "identity-not-provisioned", current), false);
            byte[] localPublic = Convert.FromBase64String(local.PublicSpkiBase64);
            using var localKey = V2Crypto.OpenCanonicalSigningKey(localPublic);
            if (localKey is null || V2Crypto.PeerId(localPublic) != local.Id)
                return (new PairingCandidateResult(false, "identity-not-provisioned", current), false);
            if (current.PeerId == local.Id || current.PeerExists && current.Pairing is not (PairingState.Unpaired or PairingState.Pending or PairingState.Paired))
                return (new PairingCandidateResult(false, "pair-unavailable", current), false);
            if (current.KeySha256 is not null && current.KeySha256 != fingerprint)
                return (new PairingCandidateResult(false, "key-mismatch", current), false);
            if (!current.PeerExists)
                context.Exec("INSERT INTO Peer(id,publicKey,pairingState,createdUtc) VALUES(@peer,@key,'pending',@now)", ("@peer", expected.PeerId), ("@key", key), ("@now", context.NowUtc));
            else if (current.KeySha256 is null)
                context.Exec("UPDATE Peer SET publicKey=@key WHERE id=@peer", ("@peer", expected.PeerId), ("@key", key));
            _ = ConversationFor(context, expected.PeerId);
            // Even an existing unchanged paired candidate must pass the final in-transaction session guard.
            return (new PairingCandidateResult(true, "candidate-ready", Current(context)), true);
        }, commitGuard: stillCurrent,
            guardRejected: () => new PairingCandidateResult(false, "stale-security", GetPeerSecuritySnapshot(expected.PeerId)));
    }
}
