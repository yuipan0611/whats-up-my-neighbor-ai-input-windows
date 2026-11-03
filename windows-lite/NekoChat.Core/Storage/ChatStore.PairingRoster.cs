using Microsoft.Data.Sqlite;
using NekoChat.Core.V2;

namespace NekoChat.Core.Storage;

public sealed partial class ChatStore
{
    private readonly object _pairingRosterOwner = new();
    private const string PairingRosterSql = "SELECT r.id,r.displayName,r.studentNumber,r.isTeacher,r.endpoint,r.peerId,r.source,r.importBatchId,rs.revision,l.id,ls.revision,p.id,p.pairingState,p.publicKey,ps.revision FROM RosterEntry r LEFT JOIN Peer p ON p.id=r.peerId LEFT JOIN PeerSecurityRevision ps ON ps.peerId=r.peerId JOIN RosterSecurityRevision rs ON rs.rosterId=r.id JOIN LocalSecurityRevision ls ON ls.singleton=1 LEFT JOIN LocalIdentity l ON l.singleton=1 WHERE r.id=@row";
    private PairingRosterTarget MapPairingRoster(SqliteDataReader row)
    {
        var original = MapRoster(row); string? local = row.IsDBNull(9) ? null : row.GetString(9); long localRevision = row.GetInt64(10);
        PairingState? state = !row.IsDBNull(12) && PairingStates.TryParse(row.GetString(12), out var parsed) ? parsed : null;
        string? hash = !row.IsDBNull(13) && (byte[])row.GetValue(13) is { Length: > 0 } key ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(key)).ToLowerInvariant() : null;
        var bound = original.Entry.PeerId is { } peer ? new PeerSecuritySnapshot(peer, local, localRevision, !row.IsDBNull(11), state, hash, row.IsDBNull(14) ? null : row.GetInt64(14)) : null;
        return new(_pairingRosterOwner, original, row.GetInt64(8), local, localRevision, bound);
    }
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
    { ArgumentNullException.ThrowIfNull(target); var current = ReferenceEquals(target.Owner, _pairingRosterOwner) ? CapturePairingRosterTarget(target.RowId) : null; return SamePairingRoster(target, current) && target.BoundSecurity == current!.BoundSecurity; }
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
            (target.BoundPeerId is null || target.BoundPeerId == expected.PeerId && target.BoundSecurity == expected) && Current(ctx) == phasePeer && PairingRosterMatches(ctx, phaseRow);
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
    public RosterPeerActionResult ChangeSelectedPeerIfCurrent(PairingRosterTarget target, PairingState? next, Func<bool> stillCurrent)
    {
        ArgumentNullException.ThrowIfNull(target); ArgumentNullException.ThrowIfNull(stillCurrent);
        if (next is not (null or PairingState.Revoked or PairingState.Suspended or PairingState.Unpaired)) throw new ArgumentException("This action cannot grant paired trust.");
        var original = target.BoundSecurity;
        if (target.BoundPeerId is null || original is not { PeerExists: true, PeerRevision: not null }) return new(false, "no-bound-peer", 0);
        PairingRosterTarget phaseRow = target; PeerSecuritySnapshot phasePeer = original;
        bool Guard(WriteContext ctx) => stillCurrent() && PairingRosterMatches(ctx, phaseRow) && ctx.Query(PeerSecuritySql, r => MapPeerSecurity(r, original.PeerId), ("@peer", original.PeerId)).Single() == phasePeer;
        return Write("selected-peer-action", ctx => {
            bool unlink = next is null; bool change = unlink || original.Pairing != next;
            if (change && (unlink ? target.RosterRevision : original.PeerRevision) == long.MaxValue) return (new RosterPeerActionResult(false, "revision-exhausted", 0), false);
            int dropped = 0;
            if (unlink)
            { ctx.Exec("UPDATE RosterEntry SET peerId=NULL WHERE id=@row", ("@row", target.RowId)); phaseRow = target.AfterUnbinding(); }
            else
            {
                dropped = ctx.Exec("DELETE FROM ReceiptOutbox WHERE recipientId=@peer", ("@peer", original.PeerId));
                if (change) { ctx.Exec("UPDATE Peer SET pairingState=@state WHERE id=@peer", ("@state", PairingStates.ToWire(next!.Value)), ("@peer", original.PeerId)); phasePeer = original with { Pairing = next, PeerRevision = checked(original.PeerRevision!.Value + 1) }; }
            }
            return (new RosterPeerActionResult(change, change ? "changed" : "unchanged", dropped), change || dropped > 0);
        }, guardRejected: () => new(false, "stale-roster-security", 0), transactionGuard: Guard);
    }

}

public sealed record RosterPeerActionResult(bool Changed, string Reason, int DroppedReceipts);
