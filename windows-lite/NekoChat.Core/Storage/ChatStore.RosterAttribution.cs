using Microsoft.Data.Sqlite;
using NekoChat.Core.Roster;

namespace NekoChat.Core.Storage;

/// <summary>"此 IP 的 N 則名單外訊息會併入○○" for one import line. Shown in the preview; accepting it is passing it back to ApplyRosterPreview.</summary>
public sealed record RosterImportMergeNotice(int Line, string DisplayName, string Endpoint, int Messages, string? ConflictReason = null)
{
    /// <summary>With a conflict the text states why it cannot merge and how to fix it (CM-02c); such a notice cannot be confirmed.</summary>
    public string Text => ConflictReason is null ? $"此 IP 的 {Messages} 則名單外訊息會併入{DisplayName}" :
        $"此 IP 的 {Messages} 則名單外訊息無法併入{DisplayName}：{MergeConflicts.Explain(ConflictReason)}";
}

/// <summary>CM-02c: merging is all or nothing. Two different non-empty drafts, or one message id with different content on the two sides, is a conflict.</summary>
public static class MergeConflicts
{
    public const string Draft = "draft-conflict", Message = "message-conflict";
    public static string Explain(string reason) => reason switch
    {
        Draft => "兩邊都有內容不同的草稿。請先到「名單外・IP」對話送出或刪除該草稿，再加入。",
        Message => "同一個訊息代號在兩邊的內容不同。請先清除該「名單外・IP」對話（D-12）後，再加入。",
        _ => reason
    };
}

/// <summary>
/// Classroom mode (CM-01～05): the roster's IP decides who a message belongs to. This file adds the preview/merge around that rule;
/// the ownership lookup itself is the roster-endpoint join in the receive paths.
/// </summary>
public sealed partial class ChatStore
{
    /// <summary>Nothing is written. Use before <c>SaveManualRosterEntry</c> so the user sees conflicts, address changes and the merge count.</summary>
    public RosterEditPreview PreviewManualRosterEntry(string? entryId, RosterEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        if (entryId is not null) ValidateRosterId(entryId);
        var snapshot = GetRosterSnapshot(); string token = RosterImportPreview.ComputeSnapshotToken(snapshot);
        string? name = edit.Name?.Trim(), number = NullIfEmpty(edit.StudentNumber), endpoint = NullIfEmpty(edit.Endpoint);
        var old = snapshot.SingleOrDefault(r => r.Entry.Id == entryId)?.Entry;
        RosterEditPreview Blocked(string reason, RosterEntryView? conflict = null) =>
            new(false, reason, conflict?.Id, conflict?.DisplayName, RosterAddressChange.None, endpoint, 0, token);
        if (!RosterFields.ValidName(name)) return Blocked("invalid-name");
        if (!RosterFields.ValidStudent(number)) return Blocked("invalid-student-number");
        if (endpoint is not null && !AllowedRosterEndpoint(endpoint)) return Blocked("invalid-endpoint");
        if (entryId is not null && old is null) return Blocked("not-found");
        var sameStudent = snapshot.FirstOrDefault(r => r.Entry.Id != entryId && number is not null && r.Entry.StudentNumberKey == RosterFields.StudentKey(number));
        if (sameStudent is not null) return Blocked("duplicate-student-number", sameStudent.Entry);
        var sameIp = snapshot.FirstOrDefault(r => r.Entry.Id != entryId && endpoint is not null && r.Entry.Endpoint == endpoint);
        if (sameIp is not null) return Blocked("duplicate-endpoint", sameIp.Entry);
        if (old is not null && old.DisplayName == name && old.StudentNumber == number && old.IsTeacher == edit.IsTeacher && old.Endpoint == endpoint) return Blocked("unchanged");
        var change = old?.Endpoint == endpoint ? RosterAddressChange.None : endpoint is null ? RosterAddressChange.Cleared
            : old?.Endpoint is null ? RosterAddressChange.Set : RosterAddressChange.Changed;
        int merge = endpoint is not null && change != RosterAddressChange.None ? Read(c => CountUnknownSource(c, endpoint)) : 0;
        // CM-02c: the conflict check does not depend on a consent or on there being messages (two different drafts conflict on their own).
        string? conflict = endpoint is not null && change != RosterAddressChange.None && entryId is not null ? Read(c => MergeConflictReason((sql, a, b) => ScalarOn(c, sql, a, b), endpoint, entryId)) : null;
        if (conflict is not null) return new(false, "merge_conflict", null, null, change, endpoint, merge, token) { MergeConflictReason = conflict };
        return new(true, "ok", null, null, change, endpoint, merge, token);
    }

    /// <summary>CM-02b: one notice per import line that gives an address to a row while out-of-roster messages exist for it. Writes nothing.</summary>
    public IReadOnlyList<RosterImportMergeNotice> GetRosterImportMergeNotices(RosterImportPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        var snapshot = GetRosterSnapshot();
        if (!preview.MatchesSnapshot(snapshot) || !preview.CanApply) return [];
        return Read(c => preview.Changes.Where(i => i.AddressChange is RosterAddressChange.Set or RosterAddressChange.Changed && i.Row!.Endpoint is not null)
            .Select(i => new RosterImportMergeNotice(i.Line, i.Row!.Name, i.Row.Endpoint!, CountUnknownSource(c, i.Row.Endpoint!),
                i.TargetId is null ? null : MergeConflictReason((sql, a, b) => ScalarOn(c, sql, a, b), i.Row.Endpoint!, i.TargetId))).Where(n => n.Messages > 0 || n.ConflictReason is not null).ToArray());
    }

    /// <summary>
    /// CM-03b/02c: a merge conflict on any line blocks the whole import, whether or not a confirmation was passed back (null or empty included) and whether or not
    /// messages exist (two different drafts conflict on their own). Null when there is none.
    /// </summary>
    private static string? ImportMergeConflict(RosterImportPreview preview, Func<string, string, string?> conflict)
    {
        foreach (var item in preview.Changes.Where(i => i.AddressChange is RosterAddressChange.Set or RosterAddressChange.Changed && i.Row!.Endpoint is not null && i.TargetId is not null))
            if (conflict(item.Row!.Endpoint!, item.TargetId!) is not null) return "merge_conflict";
        return null;
    }

    /// <summary>Null when every confirmed notice still matches its import line and current count; otherwise the refusal reason.</summary>
    private static string? MergeNoticeProblem(RosterImportPreview preview, IEnumerable<RosterSnapshotRow> snapshot, IReadOnlyList<RosterImportMergeNotice> notices, Func<string, int> count, Func<string, string, string?> conflict)
    {
        if (notices.Select(n => n.Line).Distinct().Count() != notices.Count) return "legacy_merge_mismatch";
        var byId = snapshot.ToDictionary(s => s.Entry.Id, s => s.Entry, StringComparer.Ordinal);
        foreach (var notice in notices)
        {
            var item = preview.Changes.SingleOrDefault(i => i.Line == notice.Line);
            if (item?.Row is null || item.AddressChange is not (RosterAddressChange.Set or RosterAddressChange.Changed) || item.Row.Endpoint != notice.Endpoint) return "legacy_merge_mismatch";
            if (count(notice.Endpoint) != notice.Messages) return "legacy_merge_changed";
        }
        return null;
    }

    /// <summary>Read at the moment of use, so a stale list cannot enable sending to a row whose address was just cleared (CM-04).</summary>
    public RosterSendTarget ResolveRosterSendTarget(string rowId)
    {
        ValidateRosterId(rowId);
        var row = GetRosterSnapshot().SingleOrDefault(r => r.Entry.Id == rowId)?.Entry;
        return row is null ? new(false, "not-found", null) : row.CanSend ? new(true, "ok", row.Endpoint) : new(false, "no-address", null);
    }

    // ---- out-of-roster history for an address (v1 legacy rows + classroom conversation "ip:<address>") ---------------------------------

    private static int CountUnknownSource(SqliteConnection c, string ip)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT (SELECT count(*) FROM LegacyMessage WHERE rosterEntryId IS NULL AND sourceIp=@ip)+(SELECT count(*) FROM ClassroomMessage WHERE principal=@p)";
        cmd.Parameters.AddWithValue("@ip", ip); cmd.Parameters.AddWithValue("@p", "ip:" + ip);
        return (int)Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
    private static int CountUnknownSource(WriteContext ctx, string ip) => (int)Convert.ToInt64(
        ctx.Scalar("SELECT (SELECT count(*) FROM LegacyMessage WHERE rosterEntryId IS NULL AND sourceIp=@ip)+(SELECT count(*) FROM ClassroomMessage WHERE principal=@p)", ("@ip", ip), ("@p", "ip:" + ip)),
        System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Only after the user confirmed the count. Messages already owned by a roster row never move.</summary>
    private static int MergeUnknownSource(WriteContext ctx, string rowId, string ip)
    {
        string scope = "legacy-row:" + rowId;
        ctx.Exec("INSERT OR IGNORE INTO LegacyScopeRevision(scopeKey) VALUES(@scope)", ("@scope", scope));
        int moved = ctx.Exec("UPDATE LegacyMessage SET rosterEntryId=@row,scopeKey=@scope WHERE rosterEntryId IS NULL AND sourceIp=@ip", ("@row", rowId), ("@scope", scope), ("@ip", ip));
        return moved + MergeClassroomConversation(ctx, ClassroomPrincipalKey.ForSourceIp(ip), ClassroomPrincipalKey.ForRosterEntry(rowId));
    }

    private const string DraftConflictSql = "SELECT count(*) FROM ClassroomDraft f JOIN ClassroomDraft t ON t.principal=@to WHERE f.principal=@from AND f.rawText<>t.rawText";
    private const string MessageConflictSql = "SELECT count(*) FROM ClassroomMessage f JOIN ClassroomMessage t ON t.principal=@to AND t.direction=f.direction AND t.messageId=f.messageId WHERE f.principal=@from AND (f.rawText<>t.rawText OR coalesce(f.reaction,'')<>coalesce(t.reaction,'') OR f.kind<>t.kind OR f.contentFingerprint<>t.contentFingerprint)";
    private static long ScalarOn(SqliteConnection c, string sql, string from, string to)
    {
        using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.Parameters.AddWithValue("@from", from); cmd.Parameters.AddWithValue("@to", to);
        return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
    /// <summary>CM-02c. Null when merging <c>ip:ip</c> into <c>roster:rowId</c> loses nothing; otherwise draft-conflict or message-conflict. Nothing is written.</summary>
    private static string? MergeConflictReason(Func<string, string, string, long> scalar, string ip, string rowId)
    {
        string from = "ip:" + ip, to = "roster:" + rowId;
        if (scalar("SELECT count(*) FROM sqlite_master WHERE name='AttachmentDraft'", from, to) != 0 &&
            scalar("SELECT count(*) FROM AttachmentDraft f JOIN AttachmentDraft t ON t.principal=@to WHERE f.principal=@from AND (f.name<>t.name OR f.size<>t.size OR f.sha256<>t.sha256)", from, to) != 0) return MergeConflicts.Draft;
        if (scalar(DraftConflictSql, from, to) != 0) return MergeConflicts.Draft;
        if (scalar(MessageConflictSql, from, to) != 0) return MergeConflicts.Message;
        return null;
    }

    /// <summary>
    /// CM-10: messages, draft and attempts of <c>ip:x</c> move to <c>roster:id</c> in the caller's transaction. A message id present on both sides with identical content is kept once;
    /// different content, or two different non-empty drafts, is a conflict that the callers refuse before getting here (CM-02c). If both have a sending attempt the moved one is superseded.
    /// Both draft epochs advance so any in-flight draft save or send bound to the old guard is rejected.
    /// </summary>
    private static int MergeClassroomConversation(WriteContext ctx, ClassroomPrincipalKey from, ClassroomPrincipalKey to)
    {
        // The callers have already refused a conflicting merge (MergeConflictReason); this is the last line of defence and never drops data.
        if (MergeConflictReason((sql, a, b) => Convert.ToInt64(ctx.Scalar(sql, ("@from", a), ("@to", b)), System.Globalization.CultureInfo.InvariantCulture), from.SourceIp!, to.RosterEntryId!) is { } conflict)
            throw new InvalidOperationException("Merge conflict reached the merge step: " + conflict);
        if (Convert.ToInt64(ctx.Scalar("SELECT count(*) FROM ClassroomConversation WHERE principal=@p", ("@p", from.Value)), System.Globalization.CultureInfo.InvariantCulture) == 0) return 0;
        int total = (int)Convert.ToInt64(ctx.Scalar("SELECT count(*) FROM ClassroomMessage WHERE principal=@p", ("@p", from.Value)), System.Globalization.CultureInfo.InvariantCulture);
        ctx.Exec("INSERT OR IGNORE INTO ClassroomConversation(principal,createdUtc) VALUES(@to,@now)", ("@to", to.Value), ("@now", ctx.NowUtc));
        ctx.Exec("INSERT OR IGNORE INTO ClassroomCleared(principal,direction,messageId,clearedUtc) SELECT @to,direction,messageId,clearedUtc FROM ClassroomCleared WHERE principal=@from", ("@to", to.Value), ("@from", from.Value));
        ctx.Exec("DELETE FROM ClassroomCleared WHERE principal=@from", ("@from", from.Value));
        ctx.Exec("DELETE FROM ClassroomMessage WHERE principal=@from AND EXISTS(SELECT 1 FROM ClassroomMessage t WHERE t.principal=@to AND t.direction=ClassroomMessage.direction AND t.messageId=ClassroomMessage.messageId)", ("@from", from.Value), ("@to", to.Value));
        ctx.Exec("UPDATE ClassroomMessage SET principal=@to WHERE principal=@from", ("@to", to.Value), ("@from", from.Value));
        ctx.Exec("UPDATE ClassroomReceiptOutbox SET principal=@to WHERE principal=@from", ("@to", to.Value), ("@from", from.Value));
        if (Convert.ToInt64(ctx.Scalar("SELECT count(*) FROM ClassroomAttempt WHERE principal=@to AND attemptState='sending'", ("@to", to.Value)), System.Globalization.CultureInfo.InvariantCulture) != 0)
            ctx.Exec("UPDATE ClassroomAttempt SET attemptState='superseded',updatedUtc=@now WHERE principal=@from AND attemptState='sending'", ("@now", ctx.NowUtc), ("@from", from.Value));
        ctx.Exec("UPDATE ClassroomAttempt SET principal=@to WHERE principal=@from", ("@to", to.Value), ("@from", from.Value));
        // Not a conflict here: only one side has a draft, or both are identical. Keep the non-empty one (once).
        if (ctx.Scalar("SELECT rawText FROM ClassroomDraft WHERE principal=@from", ("@from", from.Value)) is string draft)
        {
            ctx.Exec("INSERT OR IGNORE INTO ClassroomDraft(principal,rawText,updatedUtc) VALUES(@to,@text,@now)", ("@to", to.Value), ("@text", draft), ("@now", ctx.NowUtc));
            ctx.Exec("DELETE FROM ClassroomDraft WHERE principal=@from", ("@from", from.Value));
        }
        if (ctx.Scalar("SELECT count(*) FROM sqlite_master WHERE name='AttachmentDraft'") is long tables && tables != 0)
        {
            ctx.Exec("INSERT OR IGNORE INTO AttachmentDraft(principal,blobId,name,size,sha256,epoch) SELECT @to,blobId,name,size,sha256,0 FROM AttachmentDraft WHERE principal=@from", ("@from", from.Value), ("@to", to.Value));
            ctx.Exec("DELETE FROM AttachmentDraft WHERE principal=@from", ("@from", from.Value));
            ctx.Exec("UPDATE AttachmentDraft SET epoch=(SELECT draftEpoch+1 FROM ClassroomConversation WHERE principal=@to) WHERE principal=@to", ("@to", to.Value));
            ctx.Exec("UPDATE Attachment SET epoch=(SELECT attachmentEpoch FROM ClassroomConversation WHERE principal=@to) WHERE localSeq IN (SELECT localSeq FROM ClassroomMessage WHERE principal=@to)", ("@to", to.Value));
        }
        ctx.Exec("UPDATE ClassroomConversation SET draftEpoch=draftEpoch+1 WHERE principal IN (@from,@to)", ("@from", from.Value), ("@to", to.Value));
        return total;
    }
}
