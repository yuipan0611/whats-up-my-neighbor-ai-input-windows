namespace NekoChat.Core.Storage;

public sealed record ClassroomPendingMessage(long Sequence, string CreatedUtc, ClassroomAttemptSummary Attempt);
public sealed record LocalDeleteResult(bool Deleted, string Reason);

public sealed partial class ChatStore
{
    public IReadOnlyList<ClassroomPendingMessage> ListClassroomPendingMessages(ClassroomPrincipalKey principal, int limit = 200, long? beforeSequence = null)
    {
        RequirePrincipal(principal);
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        if (beforeSequence is <= 0) throw new ArgumentOutOfRangeException(nameof(beforeSequence));
        return Read(c => {
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                SELECT a.rowid,a.createdUtc,a.messageId,a.attemptState,a.rawSnapshot,a.tryCount,a.draftEpoch,a.draftRevision,
                  (SELECT route FROM ClassroomAttemptRoute r WHERE r.messageId=a.messageId ORDER BY tryCount DESC LIMIT 1)
                FROM ClassroomAttempt a WHERE a.principal=@p AND a.attemptState IN ('sending','failed','unconfirmed','cancelled') AND (@before IS NULL OR a.rowid<@before)
                ORDER BY a.rowid DESC LIMIT @limit
                """;
            if (_migrations.Count >= 13) cmd.CommandText = cmd.CommandText.Replace("a.attemptState,a.rawSnapshot", "coalesce((SELECT state FROM ClassroomOutbox o WHERE o.messageId=a.messageId),a.attemptState),a.rawSnapshot");
            cmd.Parameters.AddWithValue("@p", principal.Value); cmd.Parameters.AddWithValue("@limit", limit);
            cmd.Parameters.AddWithValue("@before", (object?)beforeSequence ?? DBNull.Value);
            using var reader = cmd.ExecuteReader(); var rows = new List<ClassroomPendingMessage>();
            while (reader.Read()) rows.Add(new(reader.GetInt64(0), reader.GetString(1), new(reader.GetString(2), reader.GetString(3),
                reader.GetString(4), checked((int)reader.GetInt64(5)), reader.GetInt64(6), reader.GetInt64(7),
                reader.IsDBNull(8) ? null : reader.GetString(8) == "legacy" ? ClassroomRoute.Legacy : ClassroomRoute.Classroom)));
            rows.Reverse(); return rows.AsReadOnly();
        });
    }

    /// <summary>Local deletion retains the dedup tombstone; attachments and pending receipts cascade with the message.</summary>
    public LocalDeleteResult DeleteClassroomMessage(ClassroomPrincipalKey principal, long sequence)
    {
        RequirePrincipal(principal); if (sequence <= 0) throw new ArgumentOutOfRangeException(nameof(sequence));
        return Write("classroom-delete-message", ctx => {
            var message = ctx.Query("SELECT direction,messageId FROM ClassroomMessage WHERE principal=@p AND localSeq=@seq",
                r => (Direction: r.GetString(0), Id: r.GetString(1)), ("@p", principal.Value), ("@seq", sequence));
            if (message.Count == 0) return (new LocalDeleteResult(false, "not-found"), false);
            var row = message.Single();
            if (_migrations.Count >= 13) ctx.Exec("DELETE FROM ClassroomOutbox WHERE messageId=@id AND principal=@p", ("@id", row.Id), ("@p", principal.Value));
            ctx.Exec("INSERT OR IGNORE INTO ClassroomCleared(principal,direction,messageId,clearedUtc) VALUES(@p,@d,@id,@now)",
                ("@p", principal.Value), ("@d", row.Direction), ("@id", row.Id), ("@now", ctx.NowUtc));
            if (row.Direction == "outgoing") ctx.Exec("DELETE FROM ClassroomAttempt WHERE principal=@p AND messageId=@id", ("@p", principal.Value), ("@id", row.Id));
            ctx.Exec("DELETE FROM ClassroomMessage WHERE principal=@p AND localSeq=@seq", ("@p", principal.Value), ("@seq", sequence));
            return (new LocalDeleteResult(true, "deleted"), true);
        });
    }

    /// <summary>Dismiss only an idle exact attempt. A newer draft and other pending messages survive.</summary>
    public LocalDeleteResult DeleteClassroomPendingMessage(ClassroomPrincipalKey principal, string messageId, int expectedTryCount)
    {
        RequirePrincipal(principal); ValidateMessageId(messageId);
        if (expectedTryCount < 1) throw new ArgumentOutOfRangeException(nameof(expectedTryCount));
        return Write("classroom-delete-pending", ctx => {
            var old = FindClassroomAttempt(ctx, messageId);
            if (old is null || old.Principal != principal.Value) return (new LocalDeleteResult(false, "not-found"), false);
            if (old.Count != expectedTryCount) return (new LocalDeleteResult(false, "stale-attempt"), false);
            if (_migrations.Count >= 13 && ctx.Scalar("SELECT count(*) FROM ClassroomOutbox WHERE messageId=@id AND state='sending'", ("@id", messageId)) is long active && active != 0)
                return (new LocalDeleteResult(false, "not-idle"), false);
            if (old.State is "confirmed" or "superseded" || old.State == "sending" && (_migrations.Count < 13 || ctx.Scalar("SELECT count(*) FROM ClassroomOutbox WHERE messageId=@id", ("@id", messageId)) is not (long and 1))) return (new LocalDeleteResult(false, "not-idle"), false);
            if (_migrations.Count >= 13) ctx.Exec("DELETE FROM ClassroomOutbox WHERE messageId=@id AND principal=@p", ("@id", messageId), ("@p", principal.Value));
            var draft = ReadClassroomBinding(ctx, principal.Value);
            bool hasAttachment = _migrations.Count >= 11 && ctx.Scalar("SELECT count(*) FROM AttachmentDraft WHERE principal=@p", ("@p", principal.Value)) is long n && n > 0;
            if (!hasAttachment && old.Kind == "text" && draft is not null && draft.Epoch == old.Epoch && draft.Revision == old.Revision && draft.Text == old.Text)
            {
                if (draft.Epoch == long.MaxValue) throw new InvalidOperationException("Draft epoch exhausted.");
                ctx.Exec("DELETE FROM ClassroomDraft WHERE principal=@p", ("@p", principal.Value));
                ctx.Exec("UPDATE ClassroomConversation SET draftEpoch=draftEpoch+1 WHERE principal=@p", ("@p", principal.Value));
            }
            ctx.Exec("INSERT OR IGNORE INTO ClassroomCleared(principal,direction,messageId,clearedUtc) VALUES(@p,'outgoing',@id,@now)",
                ("@p", principal.Value), ("@id", messageId), ("@now", ctx.NowUtc));
            ctx.Exec("DELETE FROM ClassroomAttempt WHERE principal=@p AND messageId=@id", ("@p", principal.Value), ("@id", messageId));
            return (new LocalDeleteResult(true, "deleted"), true);
        });
    }

    public LocalDeleteResult DeleteLegacyMessage(LegacyHistoryScope scope, long sequence)
    {
        ValidateLegacyScope(scope); if (sequence <= 0) throw new ArgumentOutOfRangeException(nameof(sequence));
        return Write("legacy-delete-message", ctx => {
            int changed = ctx.Exec("DELETE FROM LegacyMessage WHERE legacySeq=@seq AND " + LegacyScopeWhere,
                ("@seq", sequence), ("@row", scope.RosterEntryId), ("@ip", scope.UnknownSourceIp));
            return (new LocalDeleteResult(changed != 0, changed != 0 ? "deleted" : "not-found"), changed != 0);
        });
    }
}
