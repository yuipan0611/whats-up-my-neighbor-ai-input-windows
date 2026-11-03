using Microsoft.Data.Sqlite;
using NekoChat.Core.V2;

namespace NekoChat.Core.Storage;

public sealed partial class ChatStore
{
    private readonly object _pairingRosterOwner = new();
    private const string PairingRosterSql = "SELECT r.id,r.displayName,r.studentNumber,r.isTeacher,r.endpoint,r.peerId,r.source,r.importBatchId,rs.revision,l.id,ls.revision FROM RosterEntry r JOIN RosterSecurityRevision rs ON rs.rosterId=r.id JOIN LocalSecurityRevision ls ON ls.singleton=1 LEFT JOIN LocalIdentity l ON l.singleton=1 WHERE r.id=@row";
    private PairingRosterTarget MapPairingRoster(SqliteDataReader row) => new(_pairingRosterOwner, MapRoster(row), row.GetInt64(8), row.IsDBNull(9) ? null : row.GetString(9), row.GetInt64(10));
    public PairingRosterTarget? CapturePairingRosterTarget(string rowId)
    {
        ValidateRosterId(rowId);
        return Read(c => { using var q = c.CreateCommand(); q.CommandText = PairingRosterSql; q.Parameters.AddWithValue("@row", rowId); using var r = q.ExecuteReader(); return r.Read() ? MapPairingRoster(r) : null; });
    }
    private bool SamePairingRoster(PairingRosterTarget expected, PairingRosterTarget? current) =>
        ReferenceEquals(expected.Owner, _pairingRosterOwner) && current is not null && expected.LocalIdentityId is not null &&
        expected.LocalIdentityId == current.LocalIdentityId && expected.LocalRevision == current.LocalRevision &&
        expected.RosterRevision == current.RosterRevision && expected.Token == current.Token;
    private bool PairingRosterMatches(WriteContext ctx, PairingRosterTarget target) => SamePairingRoster(target,
        ctx.Query(PairingRosterSql, MapPairingRoster, ("@row", target.RowId)).SingleOrDefault());
    public bool IsPairingRosterTargetCurrent(PairingRosterTarget target)
    { ArgumentNullException.ThrowIfNull(target); return ReferenceEquals(target.Owner, _pairingRosterOwner) && SamePairingRoster(target, CapturePairingRosterTarget(target.RowId)); }
    /// <summary>Trusted local consent flow only. Peer trust and the selected binding commit together; a snapshot never supplies consent.</summary>
    public RosterPairingResult CompleteRosterPairingIfCurrent(PeerSecuritySnapshot expected, PairingRosterTarget target, Func<bool> stillCurrent)
    {
        ArgumentNullException.ThrowIfNull(expected); ArgumentNullException.ThrowIfNull(target); ArgumentNullException.ThrowIfNull(stillCurrent);
        if (!StoreIds.IsValid(expected.PeerId)) throw new ArgumentException("Invalid pairing target.");
        PeerSecuritySnapshot phasePeer = expected; PairingRosterTarget phaseRow = target;
        PeerSecuritySnapshot Current(WriteContext ctx) => ctx.Query(PeerSecuritySql, r => MapPeerSecurity(r, expected.PeerId), ("@peer", expected.PeerId)).Single();
        bool LocalReady(WriteContext ctx)
        {
            var local = ctx.Query(LocalIdentitySql, MapLocalIdentity).Single();
            if (local.Id is null || local.PublicSpkiBase64 is null || local.CredentialRef is null) return false;
            byte[] spki = Convert.FromBase64String(local.PublicSpkiBase64); using var canonical = V2Crypto.OpenCanonicalSigningKey(spki);
            return canonical is not null && V2Crypto.PeerId(spki) == local.Id;
        }
        bool Guard(WriteContext ctx) => stillCurrent() && LocalReady(ctx) && expected.LocalIdentityId == target.LocalIdentityId && expected.LocalRevision == target.LocalRevision &&
            expected.PeerExists && expected.KeySha256 is not null && expected.PeerRevision is not null &&
            expected.Pairing is PairingState.Pending or PairingState.Unpaired or PairingState.Paired &&
            (target.BoundPeerId is null || target.BoundPeerId == expected.PeerId) && Current(ctx) == phasePeer && PairingRosterMatches(ctx, phaseRow);
        return Write("pair-and-bind", ctx => {
            bool bind = target.BoundPeerId != expected.PeerId, trust = expected.Pairing != PairingState.Paired;
            if (bind && target.RosterRevision == long.MaxValue || trust && expected.PeerRevision == long.MaxValue)
                return (new RosterPairingResult(false, "revision-exhausted", expected, false), false);
            if (trust)
                ctx.Exec("UPDATE Peer SET pairingState='paired',pairedUtc=COALESCE(pairedUtc,@now) WHERE id=@peer", ("@now", ctx.NowUtc), ("@peer", expected.PeerId));
            if (bind) ctx.Exec("UPDATE RosterEntry SET peerId=@peer WHERE id=@row", ("@peer", expected.PeerId), ("@row", target.RowId));
            // Derive the only authorized post-state from the original selection. Never adopt a newer DB snapshot after mutation.
            phasePeer = trust ? expected with { Pairing = PairingState.Paired, PeerRevision = checked(expected.PeerRevision!.Value + 1) } : expected;
            phaseRow = target.AfterBinding(expected.PeerId);
            return (new RosterPairingResult(true, trust || bind ? "paired-and-bound" : "unchanged", phasePeer, bind), trust || bind);
        }, guardRejected: () => new(false, "stale-roster-security", GetPeerSecuritySnapshot(expected.PeerId), false), transactionGuard: Guard);
    }
}
