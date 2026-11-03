using NekoChat.Core.Roster;

namespace NekoChat.Core.Storage;

/// <summary>
/// Classroom mode (CM-01～05): the roster's IP decides who a message belongs to. This file only adds the preview/merge
/// around that rule; the ownership lookup itself is the existing roster-endpoint join in the legacy receive path.
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
        int merge = endpoint is not null && change != RosterAddressChange.None
            ? Read(c => { using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT count(*) FROM LegacyMessage WHERE rosterEntryId IS NULL AND sourceIp=@ip"; cmd.Parameters.AddWithValue("@ip", endpoint);
                return (int)Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture); })
            : 0;
        return new(true, "ok", null, null, change, endpoint, merge, token);
    }

    /// <summary>Read at the moment of use, so a stale list cannot enable sending to a row whose address was just cleared (CM-04).</summary>
    public RosterSendTarget ResolveRosterSendTarget(string rowId)
    {
        ValidateRosterId(rowId);
        var row = GetRosterSnapshot().SingleOrDefault(r => r.Entry.Id == rowId)?.Entry;
        return row is null ? new(false, "not-found", null) : row.CanSend ? new(true, "ok", row.Endpoint) : new(false, "no-address", null);
    }

    private static int CountUnknownLegacy(WriteContext ctx, string ip) =>
        (int)Convert.ToInt64(ctx.Scalar("SELECT count(*) FROM LegacyMessage WHERE rosterEntryId IS NULL AND sourceIp=@ip", ("@ip", ip)), System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Only after the user confirmed the count. Moves messages with no roster owner; messages already owned by a row never move.</summary>
    private static int MergeUnknownLegacy(WriteContext ctx, string rowId, string ip)
    {
        string scope = "legacy-row:" + rowId;
        ctx.Exec("INSERT OR IGNORE INTO LegacyScopeRevision(scopeKey) VALUES(@scope)", ("@scope", scope));
        return ctx.Exec("UPDATE LegacyMessage SET rosterEntryId=@row,scopeKey=@scope WHERE rosterEntryId IS NULL AND sourceIp=@ip",
            ("@row", rowId), ("@scope", scope), ("@ip", ip));
    }
}
