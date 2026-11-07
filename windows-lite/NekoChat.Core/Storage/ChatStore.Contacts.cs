using Microsoft.Data.Sqlite;
using NekoChat.Core.Roster;

namespace NekoChat.Core.Storage;

public sealed record ReadReceiptPreferences(bool ExplicitChoice, bool NoticeShown);

public sealed partial class ChatStore
{
    public event Action<string>? ClassroomCommunicationSucceeded;
    public void RecordClassroomCommunicationSuccess(string sourceIp)
    {
        if (ClassroomPrincipalKey.ForSource(sourceIp) is null) return;
        try { ClassroomCommunicationSucceeded?.Invoke(sourceIp); } catch { }
    }

    private void ContactDiagnostic(string code) { try { _options.DiagnosticSink?.Invoke(code); } catch { } }

    // D-27: the accepted socket address is the only key. No name, key, teacher role or peer binding comes from the sender.
    private ClassroomPrincipalKey AutoContact(WriteContext ctx, string ip)
    {
        var current = ResolveClassroomPrincipal(ctx, ip);
        if (_migrations.Count < 14 || current.IsRoster) return current;
        if (!AllowedRosterEndpoint(ip)) { ContactDiagnostic("auto-contact-source-excluded"); return current; }
        ctx.Exec("SAVEPOINT auto_contact");
        try
        {
            _options.Faults?.AfterBegin("auto-contact");
            var removed = ctx.Query("SELECT d.rosterEntryId,d.displayName,a.studentNumber,a.source FROM ClassroomArchivedContact a JOIN RosterRemoved d ON d.rosterEntryId=a.rosterEntryId WHERE a.endpoint=@ip ORDER BY d.removedUtc DESC,d.rowid DESC LIMIT 1",
                r => (Id: r.GetString(0), Name: r.GetString(1), Number: r.IsDBNull(2) ? null : r.GetString(2), Source: r.IsDBNull(3) ? null : r.GetString(3)), ("@ip", ip)).SingleOrDefault();
            string id = removed.Id ?? NewRosterId(ctx);
            ctx.Exec("INSERT INTO RosterEntry(id,displayName,studentNumber,isTeacher,endpoint,source) VALUES(@id,@name,@number,0,@ip,@source)",
                ("@id", id), ("@name", removed.Name ?? ip), ("@number", removed.Number), ("@ip", ip), ("@source", "auto"));
            MergeUnknownSource(ctx, id, ip);
            ctx.Exec("UPDATE LegacyMessage SET rosterEntryId=@id WHERE rosterEntryId IS NULL AND scopeKey='legacy-row:'||@id", ("@id", id));
            ctx.Exec("DELETE FROM RosterRemoved WHERE rosterEntryId=@id", ("@id", id));
            ctx.Exec("INSERT INTO ClassroomAutoSource(ip,rosterEntryId) VALUES(@ip,@id) ON CONFLICT(ip) DO UPDATE SET rosterEntryId=excluded.rosterEntryId", ("@ip", ip), ("@id", id));
            ctx.Exec("RELEASE auto_contact");
            return ClassroomPrincipalKey.ForRosterEntry(id);
        }
        catch (Exception e) when (e is InvalidOperationException or IOException || e is SqliteException { SqliteErrorCode: 19 })
        {
            ctx.Exec("ROLLBACK TO auto_contact"); ctx.Exec("RELEASE auto_contact");
            ContactDiagnostic("auto-contact-failed");
            return current;
        }
    }

    private bool UsesAutomaticBudget(WriteContext ctx, ClassroomPrincipalKey principal) => !principal.IsRoster ||
        _migrations.Count >= 14 && ctx.Scalar("SELECT count(*) FROM RosterEntry WHERE id=@id AND source='auto'", ("@id", principal.RosterEntryId)) is long n && n != 0;

    public bool IsAutomaticContact(string ip) => Read(c => {
        using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT count(*) FROM RosterEntry WHERE endpoint=@ip AND source='auto'";
        cmd.Parameters.AddWithValue("@ip", ip); return Convert.ToInt64(cmd.ExecuteScalar()) != 0;
    });
    public bool CanAutoReceiveAttachments(ClassroomPrincipalKey principal) => principal.IsRoster && Read(c => {
        using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT count(*) FROM RosterEntry WHERE id=@id AND coalesce(source,'')<>'auto'";
        cmd.Parameters.AddWithValue("@id", principal.RosterEntryId); return Convert.ToInt64(cmd.ExecuteScalar()) != 0;
    });

    public RosterMutationResult ConfirmAutomaticContact(string entryId, string expectedSnapshotToken)
    {
        ValidateRosterId(entryId); ValidateRosterToken(expectedSnapshotToken);
        return Write("confirm-contact", ctx => {
            var rows = ReadRoster(ctx); string token = RosterImportPreview.ComputeSnapshotToken(rows);
            if (token != expectedSnapshotToken) return (new RosterMutationResult(false, "roster_changed", entryId, token), false);
            var row = rows.SingleOrDefault(r => r.Entry.Id == entryId);
            if (row is null) return (new RosterMutationResult(false, "not-found", entryId, token), false);
            if (row.Source != "auto") return (new RosterMutationResult(false, "unchanged", entryId, token), false);
            ctx.Exec("UPDATE RosterEntry SET source='manual' WHERE id=@id", ("@id", entryId));
            ctx.Exec("DELETE FROM ClassroomAutoSource WHERE rosterEntryId=@id", ("@id", entryId));
            return (new RosterMutationResult(true, "updated", entryId, RosterImportPreview.ComputeSnapshotToken(ReadRoster(ctx))), true);
        });
    }

    public RosterMutationResult SetClassroomTeacherByIp(string ip, string token)
    {
        var row = GetRosterSnapshot().FirstOrDefault(r => r.Entry.Endpoint == ip)?.Entry;
        return row is null ? SaveManualRosterEntry(null, new RosterEdit("老師", null, true, ip), token) : SetClassroomTeacher(row.Id, token);
    }

    public string? GetClassroomTeacherIp() => GetRosterSnapshot().SingleOrDefault(r => r.Entry.IsTeacher)?.Entry.Endpoint;

    public RosterMutationResult SetClassroomTeacher(string entryId, string expectedSnapshotToken) =>
        ChangeClassroomTeacher(entryId, expectedSnapshotToken, true);
    public RosterMutationResult ClearClassroomTeacher(string entryId, string expectedSnapshotToken) =>
        ChangeClassroomTeacher(entryId, expectedSnapshotToken, false);

    private RosterMutationResult ChangeClassroomTeacher(string id, string token, bool teacher)
    {
        ValidateRosterId(id); ValidateRosterToken(token);
        return Write("classroom-teacher", ctx => {
            var rows = ReadRoster(ctx); string actual = RosterImportPreview.ComputeSnapshotToken(rows);
            if (token != actual) return (new RosterMutationResult(false, "roster_changed", id, actual), false);
            var row = rows.SingleOrDefault(r => r.Entry.Id == id)?.Entry;
            if (row is null) return (new RosterMutationResult(false, "not-found", id, actual), false);
            if (teacher && row.Endpoint is null) return (new RosterMutationResult(false, "no-address", id, actual), false);
            if (teacher)
            {
                if (MergeConflictReason((sql, a, b) => Convert.ToInt64(ctx.Scalar(sql, ("@from", a), ("@to", b))), row.Endpoint!, id) is not null)
                    return (new RosterMutationResult(false, "merge_conflict", id, actual), false);
                ctx.Exec("UPDATE RosterEntry SET isTeacher=0 WHERE isTeacher=1 AND id<>@id", ("@id", id));
                MergeUnknownSource(ctx, id, row.Endpoint!);
            }
            if (teacher && _migrations.Count >= 14) ctx.Exec("DELETE FROM ClassroomAutoSource WHERE rosterEntryId=@id", ("@id", id));
            ctx.Exec("UPDATE RosterEntry SET isTeacher=@v,source=CASE WHEN @v=1 THEN 'manual' ELSE source END WHERE id=@id", ("@id", id), ("@v", teacher ? 1 : 0));
            return (new RosterMutationResult(true, "updated", id, RosterImportPreview.ComputeSnapshotToken(ReadRoster(ctx))), true);
        });
    }

    public ReadReceiptPreferences GetReadReceiptPreferences() => Read(c => {
        using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT explicitChoice,noticeShown FROM ReadReceiptPolicy WHERE singleton=1";
        using var r = cmd.ExecuteReader(); r.Read(); return new ReadReceiptPreferences(r.GetInt64(0) == 1, r.GetInt64(1) == 1);
    });
    public void AcknowledgeReadReceiptNotice() => Write("read-policy-notice", ctx => {
        int n = ctx.Exec("UPDATE ReadReceiptPolicy SET noticeShown=1 WHERE singleton=1 AND noticeShown=0"); return (n, n != 0);
    });

    private void NormalizeChatUxHistory()
    {
        if (_migrations.Count < 14) return;
        Write("chat-ux-normalize", ctx => {
            int merged = 0;
            foreach (var row in ReadRoster(ctx).Where(r => r.Entry.Endpoint is not null))
            {
                var e = row.Entry; string ip = e.Endpoint!;
                if (CountUnknownSource(ctx, ip) == 0 && ctx.Scalar("SELECT (SELECT count(*) FROM ClassroomDraft WHERE principal=@p)+(SELECT count(*) FROM ClassroomAttempt WHERE principal=@p)+(SELECT count(*) FROM ClassroomCleared WHERE principal=@p)+(SELECT count(*) FROM AttachmentDraft WHERE principal=@p)+(SELECT count(*) FROM ClassroomOutbox WHERE principal=@p)", ("@p", "ip:" + ip)) is long n && n == 0) continue;
                if (MergeConflictReason((sql, f, t) => Convert.ToInt64(ctx.Scalar(sql, ("@from", f), ("@to", t))), ip, e.Id) is not null)
                { ContactDiagnostic("contact-merge-conflict"); continue; }
                merged += MergeUnknownSource(ctx, e.Id, ip);
            }
            return (merged, true);
        });
    }
}
