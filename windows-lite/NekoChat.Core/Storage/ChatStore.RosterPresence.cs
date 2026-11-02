using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using NekoChat.Core.Presence;

namespace NekoChat.Core.Storage;

public sealed partial class ChatStore
{
    private const string PresenceTargetSql = "SELECT r.id,r.peerId,p.publicKey,p.pairingState,r.endpoint,p.lastSeenEndpoint,l.id,p.installEvidence,p.lastSeenUtc,ls.revision,ps.revision,rs.revision FROM RosterEntry r LEFT JOIN Peer p ON p.id=r.peerId LEFT JOIN LocalIdentity l ON l.singleton=1 JOIN LocalSecurityRevision ls ON ls.singleton=1 LEFT JOIN PeerSecurityRevision ps ON ps.peerId=r.peerId JOIN RosterSecurityRevision rs ON rs.rosterId=r.id";

    public IReadOnlyList<PresenceTarget> GetPresenceTargets() => Read(c => {
        using var cmd = c.CreateCommand(); cmd.CommandText = PresenceTargetSql + " ORDER BY r.id";
        using var r = cmd.ExecuteReader(); var targets = new List<PresenceTarget>();
        while (r.Read()) targets.Add(MapPresenceTarget(r));
        return targets.AsReadOnly();
    });

    public PresenceTarget? GetPresenceTarget(string rowId)
    {
        if (!StoreIds.IsValid(rowId)) throw new ArgumentException("Invalid roster row.", nameof(rowId));
        return Read(c => {
            using var cmd = c.CreateCommand(); cmd.CommandText = PresenceTargetSql + " WHERE r.id=@row"; cmd.Parameters.AddWithValue("@row", rowId);
            using var r = cmd.ExecuteReader(); return r.Read() ? MapPresenceTarget(r) : null;
        });
    }

    /// <summary>The actual v2 verifier produced this job. The controller guard is evaluated inside the transaction and immediately before COMMIT.</summary>
    public PresenceWriteResult ApplyRosterPresenceEvidence(PresenceWriteJob job, Func<bool> stillCurrent)
    {
        ArgumentNullException.ThrowIfNull(job); ArgumentNullException.ThrowIfNull(stillCurrent);
        if (job.Target.PeerId is null || job.Target.KeySha256 is null) throw new ArgumentException("Presence job is not paired.", nameof(job));
        return Write("roster-presence", ctx => {
            if (!stillCurrent()) return (new PresenceWriteResult(false, "stale-target"), false);
            var rows = ctx.Query(PresenceTargetSql + " WHERE r.id=@row", MapPresenceTarget, ("@row", job.Target.RowId));
            if (rows.Count != 1) return (new PresenceWriteResult(false, "stale-target"), false);
            var current = rows[0]; var expected = job.Target;
            if (current.PeerId != expected.PeerId || current.DeclaredEndpoint != expected.DeclaredEndpoint || current.ObservedEndpoint != expected.ObservedEndpoint ||
                current.LocalRevision != expected.LocalRevision || current.PeerRevision != expected.PeerRevision || current.RosterRevision != expected.RosterRevision ||
                current.LocalIdentityId is null || current.LocalIdentityId != expected.LocalIdentityId)
                return (new PresenceWriteResult(false, "stale-target"), false);
            if (current.Pairing != PairingState.Paired) return (new PresenceWriteResult(false, "unknown-or-untrusted-peer"), false);
            if (current.KeySha256 is null || current.KeySha256 != expected.KeySha256) return (new PresenceWriteResult(false, "key-mismatch"), false);
            var result = UpdateEvidence(ctx, current.PeerId!, job.SourceEndpoint, install: true, TimeSpan.FromSeconds(60));
            return (new PresenceWriteResult(result.Written, result.Reason), result.Written);
        }, commitGuard: stillCurrent, guardRejected: () => new PresenceWriteResult(false, "stale-target"));
    }

    private static PresenceTarget MapPresenceTarget(SqliteDataReader r)
    {
        PairingState? state = !r.IsDBNull(3) && PairingStates.TryParse(r.GetString(3), out var parsed) ? parsed : null;
        string? fingerprint = !r.IsDBNull(2) && (byte[])r.GetValue(2) is { Length: > 0 } key ? Convert.ToHexString(SHA256.HashData(key)).ToLowerInvariant() : null;
        DateTime? seen = !r.IsDBNull(8) && TryParseIso(r.GetString(8), out var utc) ? utc : null;
        return new(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), fingerprint, state,
            r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6),
            !r.IsDBNull(7) && r.GetString(7) == "appVerified" ? InstalledStatus.ConfirmedInstalled : InstalledStatus.Unknown, seen,
            r.GetInt64(9), r.IsDBNull(10) ? 0 : r.GetInt64(10), r.GetInt64(11));
    }
}
