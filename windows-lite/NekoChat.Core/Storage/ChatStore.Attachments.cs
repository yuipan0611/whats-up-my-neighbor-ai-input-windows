using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using NekoChat.Core.Attachments;

namespace NekoChat.Core.Storage;

public sealed partial class ChatStore
{
    private const string AttachmentSelect = "SELECT a.localSeq,m.principal,m.direction,m.messageId,a.blobId,a.name,a.size,a.sha256,a.caption,a.peerIp,a.peerPort,a.token,a.epoch,a.state,a.problem,a.completedUtc FROM Attachment a JOIN ClassroomMessage m ON m.localSeq=a.localSeq ";
    private static AttachmentRecord MapAttachment(SqliteDataReader r) => new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3),
        new(r.GetString(4), r.GetString(5), r.GetInt64(6), r.GetString(7)), r.GetString(8), r.GetString(9), r.GetInt32(10), r.GetString(11), r.GetInt64(12), r.GetString(13), r.IsDBNull(14) ? null : r.GetString(14), r.IsDBNull(15) ? null : r.GetString(15));
    private static void RequireAttachment(AttachmentFile file)
    {
        if (!AttachmentLimits.IsId(file.BlobId) || !AttachmentLimits.ValidName(file.Name) || file.Size < 0 || file.Size > AttachmentLimits.MaximumFileBytes || !AttachmentLimits.IsHash(file.Sha256))
            throw new AttachmentException("invalid_metadata");
    }
    public AttachmentDraft? GetAttachmentDraft(ClassroomPrincipalKey principal) => Read(c => {
        using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT a.blobId,a.name,a.size,a.sha256,a.epoch FROM AttachmentDraft a JOIN ClassroomConversation c ON c.principal=a.principal WHERE a.principal=@p AND a.epoch=c.draftEpoch";
        cmd.Parameters.AddWithValue("@p", principal.Value); using var r = cmd.ExecuteReader();
        return r.Read() ? new AttachmentDraft(new(r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetString(3)), r.GetInt64(4)) : null;
    });
    public bool SaveAttachmentDraft(ClassroomPrincipalKey principal, AttachmentFile file, long expectedEpoch)
    {
        RequireAttachment(file);
        return Write("attachment-draft", ctx => {
            if (!EnsureClassroomConversation(ctx, principal) || ClassroomTarget(ctx, principal) is null || ReadClassroomBinding(ctx, principal.Value)!.Epoch != expectedEpoch) return (false, false);
            ctx.Exec("INSERT INTO AttachmentDraft(principal,blobId,name,size,sha256,epoch) VALUES(@p,@b,@n,@s,@h,@e) ON CONFLICT(principal) DO UPDATE SET blobId=excluded.blobId,name=excluded.name,size=excluded.size,sha256=excluded.sha256,epoch=excluded.epoch",
                ("@p", principal.Value), ("@b", file.BlobId), ("@n", file.Name), ("@s", file.Size), ("@h", file.Sha256), ("@e", expectedEpoch));
            return (true, true);
        });
    }
    public void RemoveAttachmentDraft(ClassroomPrincipalKey principal, string blobId) => Write("attachment-remove-draft", ctx => {
        int count = ctx.Exec("DELETE FROM AttachmentDraft WHERE principal=@p AND blobId=@b", ("@p", principal.Value), ("@b", blobId)); return (count, count != 0);
    });
    public AttachmentRecord? GetAttachment(long sequence) => Read(c => {
        using var cmd = c.CreateCommand(); cmd.CommandText = AttachmentSelect + "WHERE a.localSeq=@seq"; cmd.Parameters.AddWithValue("@seq", sequence);
        using var r = cmd.ExecuteReader(); return r.Read() ? MapAttachment(r) : null;
    });
    public IReadOnlyList<AttachmentRecord> ListAttachments(string? principal = null) => Read(c => {
        using var cmd = c.CreateCommand(); cmd.CommandText = AttachmentSelect + "WHERE (@p IS NULL OR m.principal=@p) ORDER BY a.localSeq";
        cmd.Parameters.AddWithValue("@p", (object?)principal ?? DBNull.Value); using var r = cmd.ExecuteReader(); var rows = new List<AttachmentRecord>();
        while (r.Read()) rows.Add(MapAttachment(r)); return rows.AsReadOnly();
    });
    public IReadOnlySet<string> AttachmentReferences() => Read(c => {
        using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT blobId FROM Attachment UNION SELECT blobId FROM AttachmentDraft";
        using var r = cmd.ExecuteReader(); var ids = new HashSet<string>(StringComparer.Ordinal); while (r.Read()) ids.Add(r.GetString(0)); return ids;
    });
    public IReadOnlySet<string> AttachmentDraftReferences() => Read(c => {
        using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT blobId FROM AttachmentDraft";
        using var r = cmd.ExecuteReader(); var ids = new HashSet<string>(StringComparer.Ordinal); while (r.Read()) ids.Add(r.GetString(0)); return ids;
    });
    public long AttachmentGeneration(ClassroomPrincipalKey principal) => Read(c => {
        using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT attachmentEpoch FROM ClassroomConversation WHERE principal=@p"; cmd.Parameters.AddWithValue("@p", principal.Value);
        return cmd.ExecuteScalar() is long epoch ? epoch : 0;
    });
    public AttachmentRecord PrepareAttachmentSend(ClassroomPrincipalKey principal, string caption, int peerPort, long draftEpoch, long draftRevision)
    {
        if (Encoding.UTF8.GetByteCount(caption) > 65536 || caption.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t'))) throw new AttachmentException("invalid_caption");
        return Write("attachment-prepare", ctx => (PrepareAttachmentSend(ctx, principal, caption, peerPort, draftEpoch, draftRevision), true));
    }
    private AttachmentRecord PrepareAttachmentSend(WriteContext ctx, ClassroomPrincipalKey principal, string caption, int peerPort, long draftEpoch, long draftRevision)
    {
            string? ip = ClassroomTarget(ctx, principal); var binding = ReadClassroomBinding(ctx, principal.Value);
            if (ip is null || binding is null || binding.Epoch != draftEpoch || binding.Revision != draftRevision || (binding.Text ?? "") != caption) throw new AttachmentException("cleared");
            var draft = ctx.Query("SELECT blobId,name,size,sha256 FROM AttachmentDraft WHERE principal=@p AND epoch=@e", r => new AttachmentFile(r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetString(3)), ("@p", principal.Value), ("@e", draftEpoch)).SingleOrDefault()
                ?? throw new AttachmentException("missing_draft");
            var old = ctx.Query(AttachmentSelect + "WHERE m.principal=@p AND m.direction='outgoing' AND a.blobId=@b AND a.caption=@c AND a.peerIp=@ip ORDER BY a.localSeq DESC LIMIT 1", MapAttachment,
                ("@p", principal.Value), ("@b", draft.BlobId), ("@c", caption), ("@ip", ip)).SingleOrDefault();
            if (old is not null) return old;
            long epoch = Convert.ToInt64(ctx.Scalar("SELECT attachmentEpoch FROM ClassroomConversation WHERE principal=@p", ("@p", principal.Value)), CultureInfo.InvariantCulture);
            string messageId = Guid.NewGuid().ToString("D");
            return InsertAttachment(ctx, principal, "outgoing", messageId, draft, caption, ip, peerPort, AttachmentLimits.Token(), epoch, "sending");
    }

    public (AttachmentRecord? Record, bool New) ReceiveAttachmentOffer(string sourceIp, int sourcePort, string messageId, AttachmentFile wireFile, string caption, string token, Func<bool> current)
    {
        RequireAttachment(wireFile);
        if (!StoreIds.IsUuid(messageId) || !AttachmentLimits.IsHash(token) || Encoding.UTF8.GetByteCount(caption) > 65536 || caption.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')) || ClassroomPrincipalKey.ForSource(sourceIp) is null)
            throw new AttachmentException("invalid_metadata");
        return Write<(AttachmentRecord?, bool)>("attachment-receive-offer", ctx => {
            if (Blocked(ctx,sourceIp)) throw new AttachmentException("source_not_allowed");
            var principal = ResolveClassroomPrincipal(ctx, sourceIp);
            if (ctx.Scalar("SELECT count(*) FROM ClassroomCleared WHERE principal=@p AND direction='incoming' AND messageId=@id", ("@p", principal.Value), ("@id", messageId)) is long cleared && cleared != 0) return ((null, false), false);
            var prior = ctx.Query(AttachmentSelect + "WHERE m.principal=@p AND m.direction='incoming' AND m.messageId=@id", MapAttachment, ("@p", principal.Value), ("@id", messageId)).SingleOrDefault();
            if (prior is not null)
            {
                if (prior.File.Name != wireFile.Name || prior.File.Size != wireFile.Size || prior.File.Sha256 != wireFile.Sha256 || prior.Caption != caption || prior.Token != token || prior.PeerIp != sourceIp || prior.PeerPort != sourcePort)
                    throw new AttachmentException("conflict");
                return ((prior, false), false);
            }
            if (ctx.Scalar("SELECT count(*) FROM ClassroomMessage WHERE principal=@p AND direction='incoming' AND messageId=@id", ("@p", principal.Value), ("@id", messageId)) is long collision && collision != 0) throw new AttachmentException("conflict");
            if (UsesAutomaticBudget(ctx, principal) && OffRosterLimit(ctx, sourceIp, Encoding.UTF8.GetByteCount(caption) + 512) is not null) throw new AttachmentException("rate_limited");
            principal = AutoContact(ctx, sourceIp);
            EnsureClassroomConversation(ctx, principal);
            long epoch = Convert.ToInt64(ctx.Scalar("SELECT attachmentEpoch FROM ClassroomConversation WHERE principal=@p", ("@p", principal.Value)), CultureInfo.InvariantCulture);
            var localFile = wireFile with { BlobId = Guid.NewGuid().ToString("N") };
            return ((InsertAttachment(ctx, principal, "incoming", messageId, localFile, caption, sourceIp, sourcePort, token, epoch, "offered"), true), true);
        }, guardRejected: () => (null, false), transactionGuard: _ => current());
    }
    private static AttachmentRecord InsertAttachment(WriteContext ctx, ClassroomPrincipalKey principal, string direction, string messageId, AttachmentFile file, string caption, string ip, int port, string token, long epoch, string state)
    {
        string display = caption.Length == 0 ? "附件：" + file.Name : caption;
        ctx.Exec("INSERT INTO ClassroomMessage(principal,direction,messageId,rawText,kind,contentFingerprint,sourceIp,createdUtc) VALUES(@p,@d,@id,@text,'attachment',@fp,@ip,@now)",
            ("@p", principal.Value), ("@d", direction), ("@id", messageId), ("@text", display), ("@fp", System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new { caption, file.Name, file.Size, file.Sha256 })))), ("@ip", ip), ("@now", ctx.NowUtc));
        long seq = Convert.ToInt64(ctx.Scalar("SELECT last_insert_rowid()"), CultureInfo.InvariantCulture);
        ctx.Exec("INSERT INTO Attachment(localSeq,blobId,name,size,sha256,caption,peerIp,peerPort,token,epoch,state) VALUES(@seq,@b,@n,@s,@h,@c,@ip,@port,@t,@e,@st)",
            ("@seq", seq), ("@b", file.BlobId), ("@n", file.Name), ("@s", file.Size), ("@h", file.Sha256), ("@c", caption), ("@ip", ip), ("@port", port), ("@t", token), ("@e", epoch), ("@st", state));
        return new(seq, principal.Value, direction, messageId, file, caption, ip, port, token, epoch, state, null);
    }
    public bool SetAttachmentState(AttachmentRecord expected, string state, string? problem = null, Func<bool>? current = null) => Write("attachment-state", ctx => {
        if (Blocked(ctx,expected.PeerIp)) return (false,false);
        int changed = ctx.Exec("UPDATE Attachment SET state=@st,problem=@why,completedUtc=CASE WHEN @st='complete' THEN coalesce(completedUtc,@now) ELSE completedUtc END WHERE localSeq=@seq AND epoch=@e AND (state<>'complete' OR @st IN ('complete','unavailable')) AND EXISTS(SELECT 1 FROM ClassroomMessage m JOIN ClassroomConversation c ON c.principal=m.principal WHERE m.localSeq=Attachment.localSeq AND m.principal=@p AND c.attachmentEpoch=@e)",
            ("@seq", expected.LocalSeq), ("@e", expected.Epoch), ("@st", state), ("@why", problem), ("@now", ctx.NowUtc), ("@p", expected.Principal));
        return (changed != 0, changed != 0);
    }, guardRejected: () => false, transactionGuard: _ => current?.Invoke() ?? true);
    public bool AttachmentCurrent(AttachmentRecord record) => Read(c => {
        using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT count(*) FROM Attachment a JOIN ClassroomMessage m ON m.localSeq=a.localSeq JOIN ClassroomConversation c ON c.principal=m.principal WHERE a.localSeq=@seq AND m.principal=@p AND a.epoch=@e AND c.attachmentEpoch=@e";
        if (_migrations.Count>=15) cmd.CommandText += " AND NOT EXISTS(SELECT 1 FROM BlockedSource b WHERE b.ip=a.peerIp)";
        cmd.Parameters.AddWithValue("@seq", record.LocalSeq); cmd.Parameters.AddWithValue("@p", record.Principal); cmd.Parameters.AddWithValue("@e", record.Epoch); return (long)cmd.ExecuteScalar()! == 1;
    });
    public bool AcknowledgeAttachmentOffer(AttachmentRecord record, long epoch, long revision) => Write("attachment-ack-offer", ctx => {
        if (Blocked(ctx,record.PeerIp)) return (false,false);
        if (ctx.Scalar("SELECT count(*) FROM Attachment WHERE localSeq=@s", ("@s", record.LocalSeq)) is not (long and 1)) return (false, false);
        ctx.Exec("UPDATE Attachment SET state='offered',problem=NULL WHERE localSeq=@s AND state<>'complete'", ("@s", record.LocalSeq));
        var binding = ReadClassroomBinding(ctx, record.Principal);
        if (binding is not null && binding.Epoch == epoch && binding.Revision == revision && (binding.Text ?? "") == record.Caption)
        {
            ctx.Exec("DELETE FROM ClassroomDraft WHERE principal=@p", ("@p", record.Principal));
            ctx.Exec("UPDATE ClassroomConversation SET draftEpoch=draftEpoch+1 WHERE principal=@p", ("@p", record.Principal));
        }
        ctx.Exec("DELETE FROM AttachmentDraft WHERE principal=@p AND blobId=@b", ("@p", record.Principal), ("@b", record.File.BlobId));
        return (true, true);
    });
    public void RecoverAttachments() => Write("attachment-recovery", ctx => {
        int count = ctx.Exec("UPDATE Attachment SET state=CASE WHEN state='receiving' THEN 'failed' ELSE 'unconfirmed' END,problem='interrupted' WHERE state IN ('receiving','sending')");
        return (count, count != 0);
    });
    public void ForgetAttachmentFiles() => Write("attachment-forget-files", ctx => {
        int count = ctx.Exec("UPDATE Attachment SET state='unavailable',problem='local_cleanup'"); return (count, count != 0);
    });
}
