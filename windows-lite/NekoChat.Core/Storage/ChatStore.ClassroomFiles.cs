using System.Text.Json;
using NekoChat.Core.Attachments;
using NekoChat.Core.ClassroomFiles;

namespace NekoChat.Core.Storage;

public sealed partial class ChatStore
{
    public string EnsureClassroomFileNode() => Write("share-node", ctx => {
        ctx.Exec("INSERT OR IGNORE INTO ClassroomFileNode(singleton,nodeId) VALUES(1,@id)", ("@id", Guid.NewGuid().ToString("N")));
        return ((string)ctx.Scalar("SELECT nodeId FROM ClassroomFileNode WHERE singleton=1")!, true);
    });
    public bool MergeClassroomFile(ShareOperation op, Func<bool>? current = null, byte[]? protectedKey = null, string? blobId = null)
    {
        if (!ShareProtocol.Valid(op)) throw new AttachmentException("invalid_signature");
        if (blobId is not null && !AttachmentLimits.IsId(blobId)) throw new AttachmentException("invalid_id");
        return Write("share-merge", ctx => {
            var existing = ctx.Query("SELECT json FROM ClassroomFileOperation WHERE shareId=@id AND kind=@k", r => r.GetString(0), ("@id", op.File.Id), ("@k", op.Kind)).SingleOrDefault();
            if (existing is not null)
            {
                if (JsonSerializer.Deserialize<ShareOperation>(existing)!.File != op.File) throw new AttachmentException("conflict");
                return (false, false);
            }
            if ((long)ctx.Scalar("SELECT count(*) FROM ClassroomFileOperation")! >= ShareProtocol.MaximumOperations) throw new AttachmentException("catalog_full");
            if (op.Kind == "add" && (long)ctx.Scalar("SELECT count(*) FROM ClassroomFileOperation a WHERE a.kind='add' AND NOT EXISTS(SELECT 1 FROM ClassroomFileOperation w WHERE w.shareId=a.shareId AND w.kind='withdraw')")! >= 1024) throw new AttachmentException("catalog_full");
            ctx.Exec("INSERT INTO ClassroomFileOperation(shareId,kind,json) VALUES(@id,@k,@j)", ("@id", op.File.Id), ("@k", op.Kind), ("@j", JsonSerializer.Serialize(op)));
            if (protectedKey is not null) ctx.Exec("INSERT INTO ClassroomFileLocal(shareId,protectedKey,blobId) VALUES(@id,@key,@b)", ("@id", op.File.Id), ("@key", protectedKey), ("@b", blobId));
            return (true, true);
        }, commitGuard: current, guardRejected: current is null ? null : () => throw new AttachmentException("receiver_not_ready"));
    }
    public IReadOnlyList<ShareLogEntry> ClassroomFileOperations(long after = 0, int limit = 2) => Read(c => {
        if (after < 0 || limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(after));
        using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT seq,json FROM ClassroomFileOperation WHERE seq>@a ORDER BY seq LIMIT @n";
        cmd.Parameters.AddWithValue("@a", after); cmd.Parameters.AddWithValue("@n", limit);
        using var r = cmd.ExecuteReader(); var list = new List<ShareLogEntry>(); while (r.Read()) list.Add(new(r.GetInt64(0), JsonSerializer.Deserialize<ShareOperation>(r.GetString(1))!)); return list.AsReadOnly();
    });
    public IReadOnlyList<SharedFile> ListClassroomFiles(long? before = null, int limit = 50) => Read(c => {
        if (limit is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(limit));
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT a.json,l.protectedKey IS NOT NULL,l.blobId,coalesce(l.state,'available'),l.problem,a.seq FROM ClassroomFileOperation a LEFT JOIN ClassroomFileLocal l ON l.shareId=a.shareId WHERE a.kind='add' AND NOT EXISTS(SELECT 1 FROM ClassroomFileOperation w WHERE w.shareId=a.shareId AND w.kind='withdraw') AND (@b IS NULL OR a.seq<@b) ORDER BY a.seq DESC LIMIT @n";
        if (_migrations.Count>=15) cmd.CommandText=cmd.CommandText.Replace("ORDER BY a.seq", "AND NOT EXISTS(SELECT 1 FROM BlockedSource b WHERE b.ip=json_extract(a.json,'$.File.UploaderIp')) ORDER BY a.seq");
        cmd.Parameters.AddWithValue("@b", (object?)before ?? DBNull.Value); cmd.Parameters.AddWithValue("@n", limit); using var r = cmd.ExecuteReader(); var list = new List<SharedFile>();
        while (r.Read()) list.Add(new(JsonSerializer.Deserialize<ShareOperation>(r.GetString(0))!.File, r.GetInt32(1) != 0, r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), 0, r.GetInt64(5))); return list.AsReadOnly();
    });
    public SharedFile? GetClassroomFile(string id) => ListClassroomFileById(id);
    private SharedFile? ListClassroomFileById(string id) => Read(c => {
        using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT a.json,l.protectedKey IS NOT NULL,l.blobId,coalesce(l.state,'available'),l.problem,a.seq FROM ClassroomFileOperation a LEFT JOIN ClassroomFileLocal l ON l.shareId=a.shareId WHERE a.shareId=@id AND a.kind='add' AND NOT EXISTS(SELECT 1 FROM ClassroomFileOperation w WHERE w.shareId=a.shareId AND w.kind='withdraw')";
        if (_migrations.Count>=15) cmd.CommandText += " AND NOT EXISTS(SELECT 1 FROM BlockedSource b WHERE b.ip=json_extract(a.json,'$.File.UploaderIp'))";
        cmd.Parameters.AddWithValue("@id", id); using var r = cmd.ExecuteReader(); return r.Read() ? new SharedFile(JsonSerializer.Deserialize<ShareOperation>(r.GetString(0))!.File,r.GetInt32(1)!=0,r.IsDBNull(2)?null:r.GetString(2),r.GetString(3),r.IsDBNull(4)?null:r.GetString(4),0,r.GetInt64(5)) : null;
    });
    public byte[]? ClassroomFileKey(string id) => Read(c => { using var cmd=c.CreateCommand(); cmd.CommandText="SELECT protectedKey FROM ClassroomFileLocal WHERE shareId=@id"; cmd.Parameters.AddWithValue("@id",id); return cmd.ExecuteScalar() is byte[] b ? b : null; });
    public bool SetClassroomFileLocal(string id, string? blob, string state, string? problem = null) => Write("share-local", ctx => {
        if (blob is not null && !AttachmentLimits.IsId(blob)) throw new AttachmentException("invalid_id");
        if ((long)ctx.Scalar("SELECT count(*) FROM ClassroomFileOperation WHERE shareId=@id AND kind='withdraw'",("@id",id))! != 0) return (false,false);
        ctx.Exec("INSERT INTO ClassroomFileLocal(shareId,blobId,state,problem) VALUES(@id,@b,@s,@p) ON CONFLICT(shareId) DO UPDATE SET blobId=coalesce(excluded.blobId,blobId),state=excluded.state,problem=excluded.problem",("@id",id),("@b",blob),("@s",state),("@p",problem)); return (true,true);
    });
    public void RecoverClassroomFiles() => Write("share-recover",ctx=> { ctx.Exec("UPDATE ClassroomFileLocal SET state='failed',problem='interrupted' WHERE state='receiving'"); return(0,true); });
    public IReadOnlySet<string> ClassroomFileReferences() => Read(c => { using var cmd=c.CreateCommand(); cmd.CommandText="SELECT blobId FROM ClassroomFileLocal WHERE blobId IS NOT NULL"; using var r=cmd.ExecuteReader(); var ids=new HashSet<string>(); while(r.Read())ids.Add(r.GetString(0));return ids; });
    public void ForgetClassroomFileCaches() => Write("share-clear-cache",ctx=> {ctx.Exec("UPDATE ClassroomFileLocal SET blobId=NULL, state='available',problem=NULL WHERE protectedKey IS NULL OR EXISTS(SELECT 1 FROM ClassroomFileOperation w WHERE w.shareId=ClassroomFileLocal.shareId AND w.kind='withdraw')");return(0,true);});
}
