namespace NekoChat.Core.Storage;

public sealed record BlockedContact(string Ip, string? RosterEntryId, string DisplayName);

internal static class BlockingSchema
{
    internal const string Sql = """
        CREATE TABLE BlockedSource(
          ip TEXT PRIMARY KEY,
          rosterEntryId TEXT,
          displayName TEXT NOT NULL,
          blockedUtc TEXT NOT NULL
        ) STRICT;
        """;
}

public sealed partial class ChatStore
{
    private static bool Blocked(WriteContext ctx, string? ip) => ip is not null &&
        ctx.Scalar("SELECT count(*) FROM sqlite_master WHERE name='BlockedSource'") is long n && n != 0 &&
        ctx.Scalar("SELECT count(*) FROM BlockedSource WHERE ip=@ip", ("@ip", ip)) is long count && count != 0;
    private static bool BlockedPrincipal(WriteContext ctx, ClassroomPrincipalKey p) => Blocked(ctx,
        p.IsRoster ? ctx.Scalar("SELECT endpoint FROM RosterEntry WHERE id=@id", ("@id", p.RosterEntryId)) as string : p.SourceIp);
    public bool IsSourceBlocked(string ip) => _migrations.Count >= 15 && Read(c => {
        using var cmd=c.CreateCommand();cmd.CommandText="SELECT count(*) FROM BlockedSource WHERE ip=@ip";cmd.Parameters.AddWithValue("@ip",ip);return Convert.ToInt64(cmd.ExecuteScalar())!=0;
    });
    public IReadOnlyList<BlockedContact> GetBlockedContacts() => _migrations.Count < 15 ? [] : Read(c => {
        using var cmd=c.CreateCommand();cmd.CommandText="SELECT b.ip,b.rosterEntryId,coalesce(r.displayName,b.displayName) FROM BlockedSource b LEFT JOIN RosterEntry r ON r.id=b.rosterEntryId ORDER BY b.ip";
        using var r=cmd.ExecuteReader();var rows=new List<BlockedContact>();while(r.Read())rows.Add(new(r.GetString(0),r.IsDBNull(1)?null:r.GetString(1),r.GetString(2)));return (IReadOnlyList<BlockedContact>)rows;
    });
    public RosterMutationResult SetSourceBlocked(string ip, bool blocked, string? expectedSnapshotToken = null)
    {
        if(!Roster.RosterFields.ValidEndpoint(ip))throw new ArgumentException("Invalid contact address.",nameof(ip));
        return Write("contact-block",ctx=>{
            var rows=ReadRoster(ctx);string token=Roster.RosterImportPreview.ComputeSnapshotToken(rows);
            if(expectedSnapshotToken is not null && expectedSnapshotToken!=token)return(new RosterMutationResult(false,"roster_changed",null,token),false);
            var row=rows.FirstOrDefault(r=>r.Entry.Endpoint==ip)?.Entry;
            if(!blocked){int removed=ctx.Exec("DELETE FROM BlockedSource WHERE ip=@ip",("@ip",ip));return(new RosterMutationResult(removed!=0,"unblocked",row?.Id,token),removed!=0);}
            var archived=ctx.Query("SELECT a.rosterEntryId,d.displayName FROM ClassroomArchivedContact a JOIN RosterRemoved d ON d.rosterEntryId=a.rosterEntryId WHERE a.endpoint=@ip ORDER BY d.removedUtc DESC LIMIT 1",r=>(Id:r.GetString(0),Name:r.GetString(1)),("@ip",ip)).SingleOrDefault();
            string? id=row?.Id??archived.Id;
            ctx.Exec("INSERT OR IGNORE INTO BlockedSource(ip,rosterEntryId,displayName,blockedUtc) VALUES(@ip,@id,@name,@now)",("@ip",ip),("@id",id),("@name",row?.DisplayName??archived.Name??ip),("@now",ctx.NowUtc));
            ctx.Exec("UPDATE RosterEntry SET isTeacher=0 WHERE endpoint=@ip",("@ip",ip));
            ctx.Exec("UPDATE ClassroomOutbox SET state='cancelled',error='source_blocked' WHERE targetIp=@ip AND state IN ('queued','sending','failed')",("@ip",ip));
            ctx.Exec("UPDATE ClassroomAttempt SET attemptState='cancelled',updatedUtc=@now WHERE attemptState NOT IN ('confirmed','superseded') AND (principal=@p OR principal=@r OR messageId IN (SELECT messageId FROM ClassroomOutbox WHERE targetIp=@ip AND error='source_blocked'))",("@now",ctx.NowUtc),("@p","ip:"+ip),("@r",id is null?null:"roster:"+id),("@ip",ip));
            ctx.Exec("DELETE FROM ClassroomReceiptOutbox WHERE principal=@p OR principal=@r",("@p","ip:"+ip),("@r",id is null?null:"roster:"+id));
            ctx.Exec("UPDATE Attachment SET state='cancelled',problem='source_blocked' WHERE peerIp=@ip AND state NOT IN ('complete','unavailable')",("@ip",ip));
            return(new RosterMutationResult(true,"blocked",id,Roster.RosterImportPreview.ComputeSnapshotToken(ReadRoster(ctx))),true);
        });
    }
}
