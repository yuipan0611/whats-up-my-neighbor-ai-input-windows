using Microsoft.Data.Sqlite;
using NekoChat.Core.Roster;

namespace NekoChat.Core.Storage;

public sealed partial class ChatStore
{
    private const string RosterColumns = "id,displayName,studentNumber,isTeacher,endpoint,peerId,source,importBatchId";

    public IReadOnlyList<RosterSnapshotRow> GetRosterSnapshot() => Read(c => {
        using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT " + RosterColumns + " FROM RosterEntry ORDER BY id";
        using var r = cmd.ExecuteReader(); var rows = new List<RosterSnapshotRow>();
        while (r.Read()) rows.Add(MapRoster(r));
        return rows.AsReadOnly();
    });

    public RosterMutationResult SaveManualRosterEntry(string? entryId, RosterEdit edit, string expectedSnapshotToken) =>
        SaveManualRosterEntry(entryId, edit, expectedSnapshotToken, null).Mutation;

    /// <summary>
    /// Same as the three-argument save, plus an optional confirmed merge of "名單外" messages from the row's new address (CM-02).
    /// With <paramref name="mergeConsent"/>=null the unknown-source history stays where it is.
    /// </summary>
    public RosterSaveResult SaveManualRosterEntry(string? entryId, RosterEdit edit, string expectedSnapshotToken, LegacyMergeConsent? mergeConsent)
    {
        ArgumentNullException.ThrowIfNull(edit); ValidateRosterToken(expectedSnapshotToken);
        if (entryId is not null) ValidateRosterId(entryId);
        string? name = edit.Name?.Trim(), number = NullIfEmpty(edit.StudentNumber), endpoint = NullIfEmpty(edit.Endpoint);
        string? error = !RosterFields.ValidName(name) ? "invalid-name" : !RosterFields.ValidStudent(number) ? "invalid-student-number" :
            endpoint is not null && !AllowedRosterEndpoint(endpoint) ? "invalid-endpoint" : null;
        if (error is not null) return new(new(false, error, entryId, expectedSnapshotToken), 0);
        return Write("save-roster", ctx => {
            var snapshot = ReadRoster(ctx); string token = RosterImportPreview.ComputeSnapshotToken(snapshot);
            RosterSaveResult Refuse(string reason) => new(new(false, reason, entryId, token), 0);
            if (token != expectedSnapshotToken) return (Refuse("roster_changed"), false);
            if (endpoint is not null && !AllowedRosterEndpoint(endpoint)) return (Refuse("invalid-endpoint"), false);
            var old = snapshot.SingleOrDefault(r => r.Entry.Id == entryId);
            if (Blocked(ctx,endpoint) && edit.IsTeacher || old is not null && old.Entry.Endpoint!=endpoint && Blocked(ctx,old.Entry.Endpoint)) return (Refuse("blocked"),false);
            if (entryId is not null && old is null) return (Refuse("not-found"), false);
            if (snapshot.Any(r => r.Entry.Id != entryId && number is not null && r.Entry.StudentNumberKey == RosterFields.StudentKey(number)))
                return (Refuse("duplicate-student-number"), false);
            if (snapshot.Any(r => r.Entry.Id != entryId && endpoint is not null && r.Entry.Endpoint == endpoint))
                return (Refuse("duplicate-endpoint"), false);
            if (old is not null && old.Entry.DisplayName == name && old.Entry.StudentNumber == number && old.Entry.IsTeacher == edit.IsTeacher && old.Entry.Endpoint == endpoint)
                return (Refuse("unchanged"), false);
            // CM-02c: giving a row an address that an out-of-roster conversation already uses is refused as a whole when the two cannot be merged
            // losslessly, with or without a consent and with or without messages.
            if (endpoint is not null && entryId is not null && old?.Entry.Endpoint != endpoint &&
                MergeConflictReason((sql, a, b) => Convert.ToInt64(ctx.Scalar(sql, ("@from", a), ("@to", b)), System.Globalization.CultureInfo.InvariantCulture), endpoint, entryId) is not null) return (Refuse("merge_conflict"), false);
            if (mergeConsent is not null)
            {
                if (endpoint is null || mergeConsent.Endpoint != endpoint) return (Refuse("legacy_merge_mismatch"), false);
                if (CountUnknownSource(ctx, endpoint) != mergeConsent.ExpectedMessages) return (Refuse("legacy_merge_changed"), false);
            }
            string id = entryId ?? NewRosterId(ctx);
            if (_migrations.Count >= 14 && edit.IsTeacher) ctx.Exec("DELETE FROM ClassroomAutoSource WHERE rosterEntryId=@id", ("@id", id));
            if (_migrations.Count >= 14 && edit.IsTeacher) ctx.Exec("UPDATE RosterEntry SET isTeacher=0 WHERE isTeacher=1 AND id<>@id", ("@id", id));
            if (entryId is null)
                ctx.Exec("INSERT INTO RosterEntry(id,displayName,studentNumber,isTeacher,endpoint,source) VALUES(@id,@name,@number,@teacher,@endpoint,'manual')",
                    ("@id", id), ("@name", name), ("@number", number), ("@teacher", edit.IsTeacher ? 1 : 0), ("@endpoint", endpoint));
            else
                ctx.Exec("UPDATE RosterEntry SET displayName=@name,studentNumber=@number,isTeacher=@teacher,endpoint=@endpoint,source=CASE WHEN source='auto' AND @teacher=0 THEN 'auto' ELSE 'manual' END,importBatchId=NULL WHERE id=@id",
                    ("@id", id), ("@name", name), ("@number", number), ("@teacher", edit.IsTeacher ? 1 : 0), ("@endpoint", endpoint));
            int merged = _migrations.Count >= 14 && endpoint is not null && (entryId is null || old!.Entry.Endpoint != endpoint || edit.IsTeacher)
                ? MergeUnknownSource(ctx, id, endpoint) : mergeConsent is { ExpectedMessages: > 0 } ? MergeUnknownSource(ctx, id, mergeConsent.Endpoint) : 0;
            return (new RosterSaveResult(new(true, entryId is null ? "added" : "updated", id, RosterImportPreview.ComputeSnapshotToken(ReadRoster(ctx))), merged), true);
        });
    }

    public RosterMutationResult DeleteRosterEntry(string entryId, string expectedSnapshotToken) => ChangeRosterBindingOrDelete(entryId, expectedSnapshotToken, null, delete: true);

    /// <summary>CM-17: nothing is written. The UI shows <see cref="RosterDeletePreview.Notice"/> and deletes only after the user confirms.</summary>
    public RosterDeletePreview PreviewRosterDelete(string entryId)
    {
        ValidateRosterId(entryId);
        var snapshot = GetRosterSnapshot(); string token = RosterImportPreview.ComputeSnapshotToken(snapshot);
        var row = snapshot.SingleOrDefault(r => r.Entry.Id == entryId)?.Entry;
        if (row is null) return new(false, "not-found", entryId, "", 0, 0, token);
        var (classroom, legacy) = Read(c => {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT (SELECT count(*) FROM ClassroomMessage WHERE principal=@p),(SELECT count(*) FROM LegacyMessage WHERE rosterEntryId=@id)";
            cmd.Parameters.AddWithValue("@p", "roster:" + entryId); cmd.Parameters.AddWithValue("@id", entryId);
            using var r = cmd.ExecuteReader(); r.Read(); return ((int)r.GetInt64(0), (int)r.GetInt64(1));
        });
        return new(true, "ok", entryId, row.DisplayName, classroom, legacy, token);
    }

    /// <summary>Delete after the user confirmed the preview. Refused if the row's name is no longer the one that was shown.</summary>
    public RosterMutationResult DeleteRosterEntry(string entryId, string expectedSnapshotToken, RosterDeleteConfirmation confirmation)
    {
        ArgumentNullException.ThrowIfNull(confirmation);
        if (confirmation.EntryId != entryId) return new(false, "confirmation_mismatch", entryId, expectedSnapshotToken);
        return ChangeRosterBindingOrDelete(entryId, expectedSnapshotToken, null, delete: true, confirmation.DisplayName);
    }
    /// <summary>Explicit human selection after pairing. Never creates a Peer or changes pairingState.</summary>
    public RosterMutationResult BindPairedRosterPeer(string entryId, string? peerId, string expectedSnapshotToken)
    {
        if (peerId is not null && !StoreIds.IsValid(peerId)) throw new ArgumentException("Invalid peer id.", nameof(peerId));
        return ChangeRosterBindingOrDelete(entryId, expectedSnapshotToken, peerId, delete: false);
    }

    private RosterMutationResult ChangeRosterBindingOrDelete(string entryId, string expectedToken, string? peer, bool delete, string? confirmedName = null)
    {
        ValidateRosterId(entryId); ValidateRosterToken(expectedToken);
        return Write(delete ? "delete-roster" : "bind-roster", ctx => {
            var snapshot = ReadRoster(ctx); string token = RosterImportPreview.ComputeSnapshotToken(snapshot);
            if (token != expectedToken) return (new RosterMutationResult(false, "roster_changed", entryId, token), false);
            var old = snapshot.SingleOrDefault(r => r.Entry.Id == entryId);
            if (old is null) return (new RosterMutationResult(false, "not-found", entryId, token), false);
            if (!delete && peer is not null && !IsPaired(ctx, peer)) return (new RosterMutationResult(false, "peer-not-paired", entryId, token), false);
            if (!delete && old.Entry.PeerId == peer) return (new RosterMutationResult(false, "unchanged", entryId, token), false);
            if (delete && confirmedName is not null && confirmedName != old.Entry.DisplayName) return (new RosterMutationResult(false, "confirmation_mismatch", entryId, token), false);
            if (delete)
            {
                // CM-17: keep the name the row had, in the same transaction, so its conversation stays labelled and readable.
                ctx.Exec("INSERT OR REPLACE INTO RosterRemoved(rosterEntryId,displayName,removedUtc) VALUES(@id,@name,@now)", ("@id", entryId), ("@name", old.Entry.DisplayName), ("@now", ctx.NowUtc));
                if (_migrations.Count >= 14)
                    ctx.Exec("INSERT OR REPLACE INTO ClassroomArchivedContact(rosterEntryId,endpoint,studentNumber,source) VALUES(@id,@ip,@number,@source)",
                        ("@id", entryId), ("@ip", old.Entry.Endpoint), ("@number", old.Entry.StudentNumber), ("@source", old.Source));
                ctx.Exec("DELETE FROM RosterEntry WHERE id=@id", ("@id", entryId));
            }
            else ctx.Exec("UPDATE RosterEntry SET peerId=@peer WHERE id=@id", ("@peer", peer), ("@id", entryId));
            return (new RosterMutationResult(true, delete ? "deleted" : "bound", entryId, RosterImportPreview.ComputeSnapshotToken(ReadRoster(ctx))), true);
        });
    }

    /// Known IPs in removal snapshots; pre-v14 snapshots have no endpoint and cannot be inferred.
    public IReadOnlyList<string> GetRemovedRosterEndpoints() => _migrations.Count < 14 ? [] : Read(c => {
        using var command = c.CreateCommand();
        command.CommandText = "SELECT DISTINCT a.endpoint FROM ClassroomArchivedContact a JOIN RosterRemoved d ON d.rosterEntryId=a.rosterEntryId WHERE a.endpoint IS NOT NULL ORDER BY a.endpoint";
        using var reader = command.ExecuteReader(); var addresses = new List<string>();
        while (reader.Read()) addresses.Add(reader.GetString(0));
        return (IReadOnlyList<string>)addresses;
    });

    /// <summary>Only after the user has reviewed and confirmed this immutable preview.</summary>
    public RosterApplyResult ApplyRosterPreview(RosterImportPreview preview) => ApplyRosterPreview(preview, null);

    /// <summary>
    /// CM-02b: <paramref name="confirmedMerges"/> are the notices the user saw ("此 IP 的 N 則名單外訊息會併入○○") and accepted.
    /// They merge in the same transaction as the import; if any count changed since the notice, nothing is applied.
    /// </summary>
    public RosterApplyResult ApplyRosterPreview(RosterImportPreview preview, IReadOnlyList<RosterImportMergeNotice>? confirmedMerges)
    {
        ArgumentNullException.ThrowIfNull(preview);
        if (preview.Items.Count > RosterCsv.MaxRows) throw new ArgumentOutOfRangeException(nameof(preview));
        int skipped = preview.Items.Count(i => i.Action is RosterImportAction.Error or RosterImportAction.Ambiguous);
        int unchanged = preview.Items.Count(i => i.Action == RosterImportAction.Unchanged);
        RosterApplyResult Failure(string reason, string token) => new(reason == "no-changes", reason, 0, 0, unchanged, skipped, token);
        bool RowsStillValid() => preview.Changes.All(item => item.Row is { } row && RosterFields.ValidName(row.Name) && RosterFields.ValidStudent(row.StudentNumber) &&
            (row.Endpoint is null || AllowedRosterEndpoint(row.Endpoint)));

        // 1. Pre-flight on a short read connection: no write lock is held while the (possibly slow) backup runs,
        //    so incoming messages are never blocked by a roster import. Nothing is backed up for a stale or empty preview.
        var preflight = Read(c => {
            using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT " + RosterColumns + " FROM RosterEntry ORDER BY id";
            using var r = cmd.ExecuteReader(); var rows = new List<RosterSnapshotRow>();
            while (r.Read()) rows.Add(MapRoster(r));
            return rows;
        });
        string preToken = RosterImportPreview.ComputeSnapshotToken(preflight);
        if (!preview.MatchesSnapshot(preflight)) return Failure("roster_changed", preToken);
        // CM-03: an address collision blocks the whole import; refuse before any backup is taken.
        if (!preview.CanApply) return Failure("ip_conflict", preToken);
        if (preview.Changes.Count == 0) return Failure("no-changes", preToken);
        if (!RowsStillValid()) return Failure("endpoint-or-row-no-longer-valid", preToken);
        if (Read(c => ImportMergeConflict(preview, (ip, row) => MergeConflictReason((sql, a, b) => ScalarOn(c, sql, a, b), ip, row))) is { } earlyConflict) return Failure(earlyConflict, preToken);
        if (confirmedMerges is { Count: > 0 } && Read(c => MergeNoticeProblem(preview, preflight, confirmedMerges, ip => CountUnknownSource(c, ip), (ip, row) => MergeConflictReason((sql, a, b) => ScalarOn(c, sql, a, b), ip, row))) is { } early) return Failure(early, preToken);
        // 2. Backup (independent read-only connection), then 3. take the write lock and re-verify the snapshot.
        BackupBeforeRosterImport();
        return Write("apply-roster", ctx => {
            var snapshot = ReadRoster(ctx); string token = RosterImportPreview.ComputeSnapshotToken(snapshot);
            RosterApplyResult Result(bool applied, string reason, int added = 0, int updated = 0, string? next = null) =>
                new(applied, reason, added, updated, unchanged, skipped, next ?? token);
            // The roster may have changed between the pre-flight and the lock: the backup is kept, nothing is applied.
            if (!preview.MatchesSnapshot(snapshot)) return (Result(false, "roster_changed"), false);
            // CM-03: one seat, one person. An address collision blocks the whole import until the file is corrected.
            if (!preview.CanApply) return (Result(false, "ip_conflict"), false);
            if (preview.Changes.Count == 0) return (Result(true, "no-changes"), false);
            if (!RowsStillValid()) return (Result(false, "endpoint-or-row-no-longer-valid"), false);
            if (ImportMergeConflict(preview, (ip, row) => MergeConflictReason((sql, a, b) => Convert.ToInt64(ctx.Scalar(sql, ("@from", a), ("@to", b)), System.Globalization.CultureInfo.InvariantCulture), ip, row)) is { } txConflict) return (Result(false, txConflict), false);
            if (confirmedMerges is { Count: > 0 } && MergeNoticeProblem(preview, snapshot, confirmedMerges, ip => CountUnknownSource(ctx, ip), (ip, row) => MergeConflictReason((sql, a, b) => Convert.ToInt64(ctx.Scalar(sql, ("@from", a), ("@to", b)), System.Globalization.CultureInfo.InvariantCulture), ip, row)) is { } problem) return (Result(false, problem), false);
            string batch = NewUuid(); var newIds = new Dictionary<int, string>();
            foreach (var item in preview.Changes.Where(i => i.Action == RosterImportAction.Update))
            {
                var old = snapshot.Single(s => s.Entry.Id == item.TargetId);
                if (old.Entry.Endpoint != RosterImportPreview.EffectiveEndpoint(item.Row!, old.Entry))
                    ctx.Exec("UPDATE RosterEntry SET endpoint=NULL WHERE id=@id", ("@id", item.TargetId));
            }
            string? teacher = snapshot.FirstOrDefault(r => r.Entry.IsTeacher)?.Entry.Id;
            int added = 0, updated = 0;
            foreach (var item in preview.Changes)
            {
                var row = item.Row!;
                bool teacherFlag = _migrations.Count < 14 ? row.IsTeacher : teacher is not null ? teacher == item.TargetId : row.IsTeacher;
                teacherFlag &= !Blocked(ctx,row.Endpoint);
                if (_migrations.Count >= 14 && row.IsTeacher && !teacherFlag) ContactDiagnostic("multiple-teachers-ignored");
                if (item.Action == RosterImportAction.Add)
                {
                    string newId = newIds[item.Line] = NewRosterId(ctx);
                    ctx.Exec("INSERT INTO RosterEntry(id,displayName,studentNumber,isTeacher,endpoint,source,importBatchId) VALUES(@id,@name,@number,@teacher,@endpoint,'csv',@batch)",
                        ("@id", newId), ("@name", row.Name), ("@number", row.StudentNumber), ("@teacher", teacherFlag ? 1 : 0), ("@endpoint", row.Endpoint), ("@batch", batch));
                    if (teacherFlag) teacher = newId;
                    added++;
                }
                else
                {
                    // No ip column in the file: keep the stored address (CM-05); an empty ip cell is the only thing that clears it.
                    string? endpoint = RosterImportPreview.EffectiveEndpoint(row, snapshot.Single(s => s.Entry.Id == item.TargetId).Entry);
                    ctx.Exec("UPDATE RosterEntry SET displayName=@name,studentNumber=@number,isTeacher=@teacher,endpoint=@endpoint,source='csv',importBatchId=@batch WHERE id=@id",
                        ("@id", item.TargetId), ("@name", row.Name), ("@number", row.StudentNumber), ("@teacher", teacherFlag ? 1 : 0), ("@endpoint", endpoint), ("@batch", batch));
                    if (teacherFlag) teacher = item.TargetId;
                    updated++;
                }
            }
            int merged = 0;
            foreach (var notice in confirmedMerges ?? [])
            {
                var item = preview.Changes.Single(i => i.Line == notice.Line);
                merged += MergeUnknownSource(ctx, item.Action == RosterImportAction.Add ? newIds[item.Line] : item.TargetId!, notice.Endpoint);
            }
            return (Result(true, "applied", added, updated, RosterImportPreview.ComputeSnapshotToken(ReadRoster(ctx))) with { MergedMessages = merged }, true);
        });
    }

    private static List<RosterSnapshotRow> ReadRoster(WriteContext ctx) => ctx.Query("SELECT " + RosterColumns + " FROM RosterEntry ORDER BY id", MapRoster);
    private static RosterSnapshotRow MapRoster(SqliteDataReader r)
    {
        try
        {
            return new(new(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetInt64(3) == 1,
                r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5)),
                r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7));
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        { throw new StoreException(StoreErrorKind.Corrupt, "The local roster contains invalid data.", e); }
    }
    private bool AllowedRosterEndpoint(string endpoint) => RosterFields.ValidEndpoint(endpoint) && !_options.RosterExcludedEndpoints().Contains(endpoint, StringComparer.Ordinal);
    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private string NewUuid()
    {
        string id = _options.NewId();
        if (!StoreIds.IsUuid(id)) throw new InvalidOperationException("NewId produced an invalid UUID.");
        return id;
    }
    private string NewRosterId(WriteContext ctx)
    {
        for (int i = 0; i < 3; i++)
        {
            string id = NewUuid();
            if (Convert.ToInt64(ctx.Scalar("SELECT (SELECT count(*) FROM RosterEntry WHERE id=@id)" + (_migrations.Count >= 8 ? "+(SELECT count(*) FROM RosterRemoved WHERE rosterEntryId=@id)" : ""), ("@id", id)), System.Globalization.CultureInfo.InvariantCulture) == 0) return id;
        }
        throw new InvalidOperationException("Roster id allocation failed.");
    }
    private static void ValidateRosterId(string id) { if (!StoreIds.IsValid(id)) throw new ArgumentException("Invalid roster id.", nameof(id)); }
    private static void ValidateRosterToken(string token)
    {
        if (token is null || token.Length != 64 || token.Any(c => c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f'))))
            throw new ArgumentException("Invalid roster snapshot token.", nameof(token));
    }
}
