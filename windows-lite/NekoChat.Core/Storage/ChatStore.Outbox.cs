using System.Globalization;
using NekoChat.Core.Attachments;

namespace NekoChat.Core.Storage;

internal static class ClassroomOutboxSchema
{
    internal const string Sql = """
        CREATE TABLE ClassroomOutbox(
          sequence INTEGER PRIMARY KEY AUTOINCREMENT,
          messageId TEXT NOT NULL UNIQUE,
          principal TEXT NOT NULL REFERENCES ClassroomConversation(principal),
          targetIp TEXT NOT NULL,
          peerPort INTEGER NOT NULL CHECK(peerPort BETWEEN 1 AND 65535),
          attachmentSeq INTEGER REFERENCES Attachment(localSeq) ON DELETE CASCADE,
          state TEXT NOT NULL CHECK(state IN ('queued','sending','failed','cancelled','complete')),
          failures INTEGER NOT NULL DEFAULT 0 CHECK(failures>=0),
          nextAttemptUtc TEXT NOT NULL,
          error TEXT,
          createdUtc TEXT NOT NULL
        ) STRICT;
        CREATE INDEX ClassroomOutbox_due ON ClassroomOutbox(state,nextAttemptUtc);
        """;
}

public sealed record QueuedMessageReceipt(string MessageId, DraftState Draft);

public sealed record OutboxItem(long Sequence, string MessageId, ClassroomPrincipalKey Principal, string TargetIp,
    int PeerPort, long? AttachmentSequence, string State, int Failures, string? Error);

public sealed partial class ChatStore
{
    public string EnqueueClassroomMessage(ClassroomPrincipalKey principal, string text, long epoch, long revision, int port) =>
        EnqueueClassroomMessageWithDraft(principal, text, epoch, revision, port).MessageId;

    public QueuedMessageReceipt EnqueueClassroomMessageWithDraft(ClassroomPrincipalKey principal, string text, long epoch, long revision, int port)
    {
        RequirePrincipal(principal); ValidateRevision(epoch, revision);
        if (port is < 1 or > 65535 || !ValidSendPayload(text, "text", null)) throw new ArgumentException("Invalid queued message.");
        return Write("outbox-enqueue", ctx => {
            if (!EnsureClassroomConversation(ctx, principal) || ClassroomTarget(ctx, principal) is not { } target)
                throw new InvalidOperationException("The recipient is unavailable.");
            var draft = ReadClassroomBinding(ctx, principal.Value)!;
            if (draft.Epoch != epoch || draft.Revision != revision || draft.Text != text || epoch == long.MaxValue)
                throw new InvalidOperationException("The draft changed before it could be queued.");
            if (ctx.Scalar("SELECT count(*) FROM AttachmentDraft WHERE principal=@p", ("@p", principal.Value)) is long n && n > 0)
                throw new InvalidOperationException("An attachment must not be sent as plain text.");
            string id = Guid.NewGuid().ToString("D");
            ctx.Exec("INSERT INTO ClassroomAttempt(messageId,principal,rawSnapshot,contentFingerprint,draftEpoch,draftRevision,attemptState,createdUtc,updatedUtc) VALUES(@id,@p,@text,@fp,@e,@r,'failed',@now,@now)",
                ("@id", id), ("@p", principal.Value), ("@text", text), ("@fp", ClassroomFingerprint(text, null)), ("@e", epoch), ("@r", revision), ("@now", ctx.NowUtc));
            InsertOutbox(ctx, id, principal, target, port, null);
            ClearEnqueuedDraft(ctx, principal, epoch);
            return (new QueuedMessageReceipt(id, new DraftState(null, checked(epoch + 1), revision)), true);
        });
    }

    public AttachmentRecord EnqueueAttachment(ClassroomPrincipalKey principal, string caption, long epoch, long revision, int port) =>
        EnqueueAttachmentWithDraft(principal, caption, epoch, revision, port).Attachment;

    public (AttachmentRecord Attachment, QueuedMessageReceipt Receipt) EnqueueAttachmentWithDraft(ClassroomPrincipalKey principal, string caption, long epoch, long revision, int port)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(caption) > 65536 || caption.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t'))) throw new AttachmentException("invalid_caption");
        return Write("outbox-enqueue-attachment", ctx => {
            var row = PrepareAttachmentSend(ctx, principal, caption, port, epoch, revision);
            if (epoch == long.MaxValue) throw new AttachmentException("cleared");
            InsertOutbox(ctx, row.MessageId, principal, row.PeerIp, port, row.LocalSeq);
            ctx.Exec("DELETE FROM AttachmentDraft WHERE principal=@p AND blobId=@b", ("@p", principal.Value), ("@b", row.File.BlobId));
            ClearEnqueuedDraft(ctx, principal, epoch);
            return ((row, new QueuedMessageReceipt(row.MessageId, new DraftState(null, checked(epoch + 1), revision))), true);
        });
    }

    private static void InsertOutbox(WriteContext ctx, string id, ClassroomPrincipalKey principal, string target, int port, long? attachment) =>
        ctx.Exec("INSERT INTO ClassroomOutbox(messageId,principal,targetIp,peerPort,attachmentSeq,state,nextAttemptUtc,createdUtc) VALUES(@id,@p,@ip,@port,@a,'queued',@now,@now)",
            ("@id", id), ("@p", principal.Value), ("@ip", target), ("@port", port), ("@a", attachment), ("@now", ctx.NowUtc));

    private static void ClearEnqueuedDraft(WriteContext ctx, ClassroomPrincipalKey principal, long epoch)
    {
        ctx.Exec("DELETE FROM ClassroomDraft WHERE principal=@p", ("@p", principal.Value));
        ctx.Exec("UPDATE ClassroomConversation SET draftEpoch=@e WHERE principal=@p", ("@e", checked(epoch + 1)), ("@p", principal.Value));
    }

    public IReadOnlyList<OutboxItem> ListOutbox() => Read(c => {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT sequence,messageId,principal,targetIp,peerPort,attachmentSeq,state,failures,error FROM ClassroomOutbox ORDER BY sequence";
        using var r = cmd.ExecuteReader(); var rows = new List<OutboxItem>();
        while (r.Read()) rows.Add(new(r.GetInt64(0), r.GetString(1), ClassroomPrincipalKey.TryParse(r.GetString(2), out var p) ? p : throw new InvalidOperationException("Invalid queued principal"),
            r.GetString(3), r.GetInt32(4), r.IsDBNull(5) ? null : r.GetInt64(5), r.GetString(6), r.GetInt32(7), r.IsDBNull(8) ? null : r.GetString(8)));
        return rows.AsReadOnly();
    });

    public OutboxItem? ClaimOutbox(string id, bool force = false) => Write<OutboxItem?>("outbox-claim", ctx => {
        var row = ListOutbox().SingleOrDefault(x => x.MessageId == id);
        if (row is not null && Blocked(ctx,row.TargetIp)) return (null,false);
        if (row is null || row.State != "queued") return (null, false);
        if (!force && ctx.Scalar("SELECT count(*) FROM ClassroomOutbox WHERE messageId=@id AND nextAttemptUtc<=@now", ("@id", id), ("@now", ctx.NowUtc)) is not (long and 1)) return (null, false);
        if (!row.MessageId.StartsWith("download:", StringComparison.Ordinal) && ClassroomTarget(ctx, row.Principal) != row.TargetIp)
        {
            ctx.Exec("UPDATE ClassroomOutbox SET state='failed',error='recipient_changed' WHERE messageId=@id", ("@id", id));
            return (null, true);
        }
        if (ctx.Scalar("SELECT count(*) FROM ClassroomOutbox WHERE principal=@p AND state='sending'", ("@p", row.Principal.Value)) is long active && active > 0) return (null, false);
        if (row.AttachmentSequence is null)
        {
            if (ctx.Scalar("SELECT count(*) FROM ClassroomAttempt WHERE messageId=@id AND attemptState IN ('confirmed','superseded')", ("@id", id)) is long terminal && terminal > 0) {
                ctx.Exec("UPDATE ClassroomOutbox SET state='complete' WHERE messageId=@id", ("@id", id)); return (null, true);
            }
            if (ctx.Scalar("SELECT count(*) FROM ClassroomAttempt WHERE principal=@p AND attemptState='sending'", ("@p", row.Principal.Value)) is long sending && sending > 0) return (null, false);
            int changed = ctx.Exec("UPDATE ClassroomAttempt SET attemptState='sending',tryCount=tryCount+1,updatedUtc=@now WHERE messageId=@id AND attemptState IN ('failed','unconfirmed','cancelled') AND tryCount<2147483647", ("@id", id), ("@now", ctx.NowUtc));
            if (changed == 0)
            {
                ctx.Exec("UPDATE ClassroomOutbox SET state='complete' WHERE messageId=@id", ("@id", id));
                return (null, true);
            }
        }
        ctx.Exec("UPDATE ClassroomOutbox SET state='sending',error=NULL WHERE messageId=@id", ("@id", id));
        return (row with { State = "sending" }, true);
    });

    public bool OutboxCurrent(string id) => Read(c => {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM ClassroomOutbox o LEFT JOIN RosterEntry r ON o.principal='roster:'||r.id WHERE o.messageId=@id AND o.state='sending' AND (CASE WHEN o.principal GLOB 'ip:*' THEN substr(o.principal,4) ELSE r.endpoint END)=o.targetIp";
        if (_migrations.Count>=15) cmd.CommandText += " AND NOT EXISTS(SELECT 1 FROM BlockedSource b WHERE b.ip=o.targetIp)";
        cmd.Parameters.AddWithValue("@id", id); return cmd.ExecuteScalar() is long count && count == 1;
    });

    public void FinishOutbox(string id, bool success, bool retryable, string? error) => Write("outbox-finish", ctx => {
        var row = ListOutbox().SingleOrDefault(x => x.MessageId == id);
        if (row is null || row.State != "sending") return (false, false);
        if (!success && row.AttachmentSequence is null) ctx.Exec("UPDATE ClassroomAttempt SET attemptState='unconfirmed',updatedUtc=@now WHERE messageId=@id AND attemptState='sending'", ("@id", id), ("@now", ctx.NowUtc));
        int seconds = row.Failures switch { 0 => 1, 1 => 2, 2 => 4, 3 => 8, 4 => 16, _ => 30 };
        string next = Iso(DateTime.Parse(ctx.NowUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).AddSeconds(seconds));
        ctx.Exec("UPDATE ClassroomOutbox SET state=@state,failures=failures+@f,nextAttemptUtc=@next,error=@error WHERE messageId=@id",
            ("@id", id), ("@state", success ? "complete" : retryable ? "queued" : "failed"), ("@f", success ? 0 : 1), ("@next", next), ("@error", error));
        return (true, true);
    });

    public bool RetryOutbox(string id) => Write("outbox-retry", ctx => {
        if (Blocked(ctx,ctx.Scalar("SELECT targetIp FROM ClassroomOutbox WHERE messageId=@id",("@id",id)) as string)) return (false,false);
        int changed = ctx.Exec("UPDATE ClassroomOutbox SET state='queued',nextAttemptUtc=@now,error=NULL WHERE messageId=@id AND state IN ('queued','failed','cancelled')", ("@id", id), ("@now", ctx.NowUtc));
        return (changed > 0, changed > 0);
    });

    public bool RequeueClassroomAttempt(ClassroomPrincipalKey principal, string id, int port)
    {
        lock (_gate)
        {
            if (ListOutbox().Any(x => x.MessageId == id && x.Principal == principal)) return RetryOutbox(id);
            return Write("outbox-adopt-retry", ctx => {
                var old = FindClassroomAttempt(ctx, id);
                if (old is null || old.Principal != principal.Value || old.State is "sending" or "confirmed" or "superseded" || ClassroomTarget(ctx, principal) is not { } target) return (false, false);
                InsertOutbox(ctx, id, principal, target, port, null);
                ctx.Exec("UPDATE ClassroomOutbox SET createdUtc=(SELECT createdUtc FROM ClassroomAttempt WHERE messageId=@id) WHERE messageId=@id", ("@id", id));
                return (true, true);
            });
        }
    }

    public bool CancelOutbox(string id) => Write("outbox-cancel", ctx => {
        int changed = ctx.Exec("UPDATE ClassroomOutbox SET state='cancelled',error='user_cancelled' WHERE messageId=@id AND state IN ('queued','sending','failed')", ("@id", id));
        if (changed > 0) ctx.Exec("UPDATE ClassroomAttempt SET attemptState='cancelled',updatedUtc=@now WHERE messageId=@id AND attemptState NOT IN ('confirmed','superseded')", ("@id", id), ("@now", ctx.NowUtc));
        return (changed > 0, changed > 0);
    });

    public bool RequeueAttachment(AttachmentRecord row) => Write("outbox-adopt-attachment", ctx => {
        if (!AttachmentCurrent(row) || row.Direction != "outgoing" || row.State == "complete" || !ClassroomPrincipalKey.TryParse(row.Principal, out var principal)) return (false, false);
        if (ListOutbox().Any(x => x.MessageId == row.MessageId))
        {
            int changed = ctx.Exec("UPDATE ClassroomOutbox SET state='queued',nextAttemptUtc=@now,error=NULL WHERE messageId=@id AND state IN ('queued','failed','cancelled')", ("@id", row.MessageId), ("@now", ctx.NowUtc));
            return (changed > 0, changed > 0);
        }
        InsertOutbox(ctx, row.MessageId, principal, row.PeerIp, row.PeerPort, row.LocalSeq);
        return (true, true);
    });

    public void EnqueueAttachmentDownload(AttachmentRecord row) => Write("outbox-download", ctx => {
        if(Blocked(ctx,row.PeerIp)) return (0,false);
        string id = "download:" + row.LocalSeq.ToString(CultureInfo.InvariantCulture);
        int changed = ctx.Exec("INSERT OR IGNORE INTO ClassroomOutbox(messageId,principal,targetIp,peerPort,attachmentSeq,state,nextAttemptUtc,createdUtc) VALUES(@id,@p,@ip,@port,@a,'queued',@now,@now)",
            ("@id", id), ("@p", row.Principal), ("@ip", row.PeerIp), ("@port", row.PeerPort), ("@a", row.LocalSeq), ("@now", ctx.NowUtc));
        return (changed, changed > 0);
    });
    public void RecoverOutbox()
    {
        foreach (var row in ListOutbox().Where(x => x.State == "sending")) RecoverOutboxItem(row.MessageId);
    }

    public void RecoverOutboxItem(string id) => Write("outbox-recover", ctx => {
        bool legacy = ctx.Scalar("SELECT count(*) FROM ClassroomAttemptRoute WHERE messageId=@id AND route='legacy'", ("@id", id)) is long n && n > 0;
        int changed = ctx.Exec("UPDATE ClassroomOutbox SET state=@state,error=@error,nextAttemptUtc=@now WHERE messageId=@id AND state='sending'", ("@id", id),
            ("@state", legacy ? "failed" : "queued"), ("@error", legacy ? "legacy_delivery_unconfirmed" : "interrupted"), ("@now", ctx.NowUtc));
        if (changed > 0) ctx.Exec("UPDATE ClassroomAttempt SET attemptState='unconfirmed',updatedUtc=@now WHERE messageId=@id AND attemptState='sending'", ("@id", id), ("@now", ctx.NowUtc));
        return (changed, changed > 0);
    });

    public void WakeOutbox() => Write("outbox-wake", ctx => {
        int changed = ctx.Exec("UPDATE ClassroomOutbox SET nextAttemptUtc=@now WHERE state='queued'", ("@now", ctx.NowUtc));
        return (changed, changed > 0);
    });
}
