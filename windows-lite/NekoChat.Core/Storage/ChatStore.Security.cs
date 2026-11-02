using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace NekoChat.Core.Storage;

public sealed partial class ChatStore
{
    private const string PeerSecuritySql = "SELECT l.id,ls.revision,p.id,p.pairingState,p.publicKey,ps.revision,l.publicKey FROM LocalSecurityRevision ls LEFT JOIN LocalIdentity l ON l.singleton=1 LEFT JOIN Peer p ON p.id=@peer LEFT JOIN PeerSecurityRevision ps ON ps.peerId=@peer WHERE ls.singleton=1";

    public PeerVerificationContext GetPeerVerificationContext(string peerId)
    {
        if (!StoreIds.IsValid(peerId)) throw new ArgumentException("Invalid peer id.", nameof(peerId));
        return Read(c => {
            using var cmd = c.CreateCommand(); cmd.CommandText = PeerSecuritySql; cmd.Parameters.AddWithValue("@peer", peerId);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) throw new StoreException(StoreErrorKind.Corrupt, "Security revision records are missing.");
            return new PeerVerificationContext(MapPeerSecurity(r, peerId), r.IsDBNull(4) ? null : Convert.ToBase64String((byte[])r.GetValue(4)),
                r.IsDBNull(6) ? null : Convert.ToBase64String((byte[])r.GetValue(6)));
        });
    }

    private sealed record SecurityWriteFence(PeerSecuritySnapshot Expected, string Peer, string Local, Func<bool> StillCurrent);
    private static bool SecurityMatches(WriteContext ctx, SecurityWriteFence fence)
    {
        var expected = fence.Expected;
        if (!fence.StillCurrent() || !expected.PeerExists || expected.Pairing != PairingState.Paired || expected.KeySha256 is null ||
            expected.PeerId != fence.Peer || expected.LocalIdentityId != fence.Local) return false;
        var current = ctx.Query(PeerSecuritySql, r => MapPeerSecurity(r, fence.Peer), ("@peer", fence.Peer));
        return current.Count == 1 && current[0] == expected;
    }

    public PeerSecuritySnapshot GetPeerSecuritySnapshot(string peerId)
    {
        if (!StoreIds.IsValid(peerId)) throw new ArgumentException("Invalid peer id.", nameof(peerId));
        return Read(c => {
            using var cmd = c.CreateCommand(); cmd.CommandText = PeerSecuritySql; cmd.Parameters.AddWithValue("@peer", peerId);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) throw new StoreException(StoreErrorKind.Corrupt, "Security revision records are missing.");
            return MapPeerSecurity(r, peerId);
        });
    }

    /// <summary>Trusted local flow only. A pairing session must supply its live consent/session guard; this API does not establish human consent.</summary>
    public PeerSecurityChange SetPairingStateIfCurrent(PeerSecuritySnapshot expected, PairingState next, Func<bool> stillCurrent)
    {
        ArgumentNullException.ThrowIfNull(expected); ArgumentNullException.ThrowIfNull(stillCurrent);
        if (!StoreIds.IsValid(expected.PeerId) || !Enum.IsDefined(next)) throw new ArgumentException("Invalid security change.");
        PeerSecuritySnapshot ReadCurrent(WriteContext ctx) => ctx.Query(PeerSecuritySql, r => MapPeerSecurity(r, expected.PeerId), ("@peer", expected.PeerId)).Single();
        return Write("security-change", ctx => {
            var current = ReadCurrent(ctx);
            if (!stillCurrent() || current != expected) return (new PeerSecurityChange(false, "stale-security", current), false);
            if (!current.PeerExists) return (new PeerSecurityChange(false, "unknown-peer", current), false);
            if (next == PairingState.Paired && (current.LocalIdentityId is null || current.KeySha256 is null))
                return (new PeerSecurityChange(false, "identity-not-provisioned", current), false);
            if (current.Pairing == next) return (new PeerSecurityChange(false, "unchanged", current), false);
            ctx.Exec("UPDATE Peer SET pairingState=@state,pairedUtc=CASE WHEN @state='paired' AND pairedUtc IS NULL THEN @now ELSE pairedUtc END WHERE id=@peer",
                ("@state", PairingStates.ToWire(next)), ("@now", ctx.NowUtc), ("@peer", expected.PeerId));
            return (new PeerSecurityChange(true, "changed", ReadCurrent(ctx)), true);
        }, commitGuard: stillCurrent, guardRejected: () => new PeerSecurityChange(false, "stale-security", GetPeerSecuritySnapshot(expected.PeerId)));
    }

    private static PeerSecuritySnapshot MapPeerSecurity(SqliteDataReader r, string peer)
    {
        PairingState? state = !r.IsDBNull(3) && PairingStates.TryParse(r.GetString(3), out var parsed) ? parsed : null;
        string? fingerprint = !r.IsDBNull(4) && (byte[])r.GetValue(4) is { Length: > 0 } key ? Convert.ToHexString(SHA256.HashData(key)).ToLowerInvariant() : null;
        return new(peer, r.IsDBNull(0) ? null : r.GetString(0), r.GetInt64(1), !r.IsDBNull(2), state, fingerprint, r.IsDBNull(5) ? null : r.GetInt64(5));
    }
}
