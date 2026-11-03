using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace NekoChat.Core.Roster;

public sealed record RosterSnapshotRow(RosterEntryView Entry, string? Source = null, string? ImportBatchId = null);
/// <summary>ExistingId=null explicitly chooses a new row; choices never change peer bindings.</summary>
public sealed record RosterImportChoice(int Line, string? ExistingId);
public enum RosterImportAction { Add, Update, Unchanged, Ambiguous, Error }
/// <summary>How an import line changes the row's address. Cleared must be shown to the user as "清除位址" (CM-05).</summary>
public enum RosterAddressChange { None, Set, Changed, Cleared }
public sealed record RosterImportItem(int Line, RosterImportAction Action, RosterCsvRow? Row,
    string? TargetId, IReadOnlyList<string> CandidateIds, IReadOnlyList<string> ErrorCodes,
    RosterAddressChange AddressChange = RosterAddressChange.None)
{
    public const string ClearAddressText = "清除位址";
    public string? AddressChangeText => AddressChange switch
    {
        RosterAddressChange.Cleared => ClearAddressText,
        RosterAddressChange.Set => "設定位址",
        RosterAddressChange.Changed => "變更位址",
        _ => null
    };
}

/// <summary>A pure preview. The storage adapter must recheck the snapshot and apply all changes in one transaction.</summary>
public sealed class RosterImportPreview
{
    public string SnapshotToken { get; }
    public IReadOnlyList<RosterImportItem> Items { get; }
    public IReadOnlyList<RosterImportItem> Changes { get; }
    public IReadOnlyList<string> NotReferencedIds { get; }
    /// <summary>Lines whose address collides with another row or line (CM-03). Any entry here blocks the whole apply.</summary>
    public IReadOnlyList<int> IpConflictLines { get; }
    /// <summary>Lines that would clear an existing address; the preview must list them explicitly.</summary>
    public IReadOnlyList<RosterImportItem> ClearedAddresses { get; }
    public bool CanApply => IpConflictLines.Count == 0;

    private RosterImportPreview(string token, RosterImportItem[] items, string[] notReferenced)
    {
        SnapshotToken = token;
        IpConflictLines = Array.AsReadOnly(items.Where(i => i.ErrorCodes.Any(c => c is "duplicate_endpoint" or "endpoint_conflict"))
            .Select(i => i.Line).Distinct().Order().ToArray());
        ClearedAddresses = Array.AsReadOnly(items.Where(i => i.Action == RosterImportAction.Update && i.AddressChange == RosterAddressChange.Cleared).ToArray());
        Items = Array.AsReadOnly(items);
        Changes = Array.AsReadOnly(items.Where(i => i.Action is RosterImportAction.Add or RosterImportAction.Update).ToArray());
        NotReferencedIds = Array.AsReadOnly(notReferenced);
    }

    public bool MatchesSnapshot(IEnumerable<RosterSnapshotRow> current) =>
        StringComparer.Ordinal.Equals(SnapshotToken, ComputeSnapshotToken(current));

    public static RosterImportPreview Create(IEnumerable<RosterSnapshotRow> current, RosterCsvResult csv,
        IEnumerable<RosterImportChoice>? choices = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(csv);
        if (csv.FatalError is not null) throw new ArgumentException("Cannot preview an invalid CSV document.", nameof(csv));
        var snapshot = ValidateSnapshot(current);
        string token = HashSnapshot(snapshot);
        var byId = snapshot.ToDictionary(s => s.Entry.Id, s => s.Entry, StringComparer.Ordinal);
        var byStudent = snapshot.Where(s => s.Entry.StudentNumberKey is not null)
            .ToDictionary(s => s.Entry.StudentNumberKey!, s => s.Entry, StringComparer.Ordinal);
        var selected = new Dictionary<int, RosterImportChoice>();
        var csvLines = csv.Rows.Select(r => r.Line).ToHashSet();
        foreach (var choice in choices ?? [])
            if (!csvLines.Contains(choice.Line) || !selected.TryAdd(choice.Line, choice))
                throw new ArgumentException("Choices must refer to distinct valid CSV lines.", nameof(choices));
        var sameNames = csv.Rows.Where(r => r.StudentNumber is null).GroupBy(r => (r.Name, r.IsTeacher))
            .ToDictionary(g => g.Key, g => g.Count());
        var items = new List<RosterImportItem>();
        foreach (var row in csv.Rows)
        {
            RosterEntryView? target = null;
            RosterEntryView[] candidates;
            if (row.StudentNumber is not null)
            {
                byStudent.TryGetValue(RosterFields.StudentKey(row.StudentNumber)!, out target);
                candidates = target is null ? [] : [target];
            }
            else candidates = snapshot.Select(s => s.Entry).Where(e => e.StudentNumber is null &&
                    StringComparer.Ordinal.Equals(e.DisplayName, row.Name) && e.IsTeacher == row.IsTeacher)
                .OrderBy(e => e.Id, StringComparer.Ordinal).ToArray();
            var ids = Array.AsReadOnly(candidates.Select(e => e.Id).ToArray());

            if (selected.TryGetValue(row.Line, out var choice))
            {
                // School-number matching cannot be redirected to an unrelated person or forced into a duplicate row.
                bool valid = row.StudentNumber is not null
                    ? StringComparer.Ordinal.Equals(choice.ExistingId, target?.Id)
                    : choice.ExistingId is null || candidates.Any(e => StringComparer.Ordinal.Equals(e.Id, choice.ExistingId));
                if (!valid) { items.Add(Item(row, RosterImportAction.Error, null, ids, "invalid_choice")); continue; }
                target = choice.ExistingId is null ? null : byId[choice.ExistingId];
            }
            else if (row.StudentNumber is null)
            {
                if (candidates.Length > 1 || sameNames[(row.Name, row.IsTeacher)] > 1)
                {
                    items.Add(Item(row, RosterImportAction.Ambiguous, null, ids));
                    continue;
                }
                target = candidates.SingleOrDefault();
            }
            var action = target is null ? RosterImportAction.Add : SameValues(target, row)
                ? RosterImportAction.Unchanged : RosterImportAction.Update;
            items.Add(Item(row, action, target?.Id, ids) with { AddressChange = AddressChangeOf(target, row) });
        }
        foreach (var group in csv.Errors.GroupBy(e => e.Line))
            items.Add(new(group.Key, RosterImportAction.Error, null, null, Array.AsReadOnly(Array.Empty<string>()),
                Array.AsReadOnly(group.Select(e => e.Code).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray())));

        // Explicit choices may resolve two input lines to one target. Reject all participants, including no-op updates.
        var duplicateTargets = items.Select((item, index) => (item, index))
            .Where(x => x.item.TargetId is not null && x.item.Action is RosterImportAction.Update or RosterImportAction.Unchanged)
            .GroupBy(x => x.item.TargetId, StringComparer.Ordinal).Where(g => g.Count() > 1)
            .SelectMany(g => g.Select(x => x.index)).ToArray();
        foreach (int index in duplicateTargets) items[index] = Error(items[index], "duplicate_target");

        // Removing a failed change restores its original endpoint, which can invalidate another change.
        // Recompute until the set of accepted changes stops shrinking; valid complete swaps survive together.
        while (true)
        {
            var changes = items.Select((item, index) => (item, index))
                .Where(x => x.item.Action is RosterImportAction.Add or RosterImportAction.Update).ToArray();
            var changedIds = changes.Where(x => x.item.TargetId is not null).Select(x => x.item.TargetId!).ToHashSet(StringComparer.Ordinal);
            var endpoints = snapshot.Where(s => !changedIds.Contains(s.Entry.Id) && s.Entry.Endpoint is not null)
                .Select(s => (Endpoint: s.Entry.Endpoint!, Index: -1))
                .Concat(changes.Select(x => (Endpoint: EffectiveEndpoint(x.item.Row!, x.item.TargetId is null ? null : byId[x.item.TargetId]), Index: x.index))
                    .Where(x => x.Endpoint is not null).Select(x => (Endpoint: x.Endpoint!, Index: x.Index)));
            var conflicts = endpoints.GroupBy(x => x.Endpoint, StringComparer.Ordinal).Where(g => g.Count() > 1)
                .SelectMany(g => g.Where(x => x.Index >= 0).Select(x => x.Index)).Distinct().ToArray();
            if (conflicts.Length == 0) break;
            foreach (int index in conflicts) items[index] = Error(items[index], "endpoint_conflict");
        }
        var referenced = items.SelectMany(i => i.CandidateIds).Concat(items.Where(i => i.TargetId is not null).Select(i => i.TargetId!))
            .ToHashSet(StringComparer.Ordinal);
        return new(token, items.OrderBy(i => i.Line).ToArray(), snapshot.Select(s => s.Entry.Id).Where(id => !referenced.Contains(id)).ToArray());
    }

    public static string ComputeSnapshotToken(IEnumerable<RosterSnapshotRow> current) => HashSnapshot(ValidateSnapshot(current));

    private static RosterSnapshotRow[] ValidateSnapshot(IEnumerable<RosterSnapshotRow> current)
    {
        ArgumentNullException.ThrowIfNull(current);
        var rows = current.ToArray();
        if (rows.Any(s => s is null || s.Entry is null)) throw new ArgumentException("Invalid roster snapshot.", nameof(current));
        if (rows.Select(s => s.Entry.Id).Distinct(StringComparer.Ordinal).Count() != rows.Length ||
            rows.Where(s => s.Entry.StudentNumberKey is not null).GroupBy(s => s.Entry.StudentNumberKey, StringComparer.Ordinal).Any(g => g.Count() > 1) ||
            rows.Where(s => s.Entry.Endpoint is not null).GroupBy(s => s.Entry.Endpoint, StringComparer.Ordinal).Any(g => g.Count() > 1))
            throw new ArgumentException("Roster snapshot has duplicate keys.", nameof(current));
        return rows.OrderBy(s => s.Entry.Id, StringComparer.Ordinal).ToArray();
    }

    private static string HashSnapshot(RosterSnapshotRow[] rows)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var utf8 = new UTF8Encoding(false, true);
        hash.AppendData("NekoChat.RosterSnapshot.v1\0"u8);
        void Number(int n)
        {
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(length, n);
            hash.AppendData(length);
        }
        void Field(string? value)
        {
            if (value is null) { Number(-1); return; }
            byte[] bytes;
            try { bytes = utf8.GetBytes(value); }
            catch (EncoderFallbackException) { throw new ArgumentException("Invalid Unicode in roster snapshot."); }
            Number(bytes.Length); hash.AppendData(bytes);
        }
        Number(rows.Length);
        foreach (var row in rows)
        {
            Field(row.Entry.Id); Field(row.Entry.DisplayName); Field(row.Entry.StudentNumber);
            hash.AppendData(row.Entry.IsTeacher ? new byte[] { 1 } : new byte[] { 0 });
            Field(row.Entry.Endpoint); Field(row.Entry.PeerId); Field(row.Source); Field(row.ImportBatchId);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static bool SameValues(RosterEntryView entry, RosterCsvRow row) => entry.DisplayName == row.Name &&
        entry.StudentNumber == row.StudentNumber && entry.IsTeacher == row.IsTeacher && entry.Endpoint == EffectiveEndpoint(row, entry);

    /// <summary>The address a row ends up with: the CSV value when an ip column exists, otherwise whatever the row already has.</summary>
    internal static string? EffectiveEndpoint(RosterCsvRow row, RosterEntryView? target) => row.EndpointProvided ? row.Endpoint : target?.Endpoint;

    private static RosterAddressChange AddressChangeOf(RosterEntryView? target, RosterCsvRow row)
    {
        if (!row.EndpointProvided) return RosterAddressChange.None;
        string? old = target?.Endpoint;
        if (old == row.Endpoint) return RosterAddressChange.None;
        return row.Endpoint is null ? RosterAddressChange.Cleared : old is null ? RosterAddressChange.Set : RosterAddressChange.Changed;
    }

    private static RosterImportItem Item(RosterCsvRow row, RosterImportAction action, string? id, IReadOnlyList<string> candidates, string? error = null) =>
        new(row.Line, action, row, id, candidates, Array.AsReadOnly(error is null ? Array.Empty<string>() : [error]));
    private static RosterImportItem Error(RosterImportItem item, string code) =>
        item with { Action = RosterImportAction.Error, ErrorCodes = Array.AsReadOnly(new[] { code }), AddressChange = RosterAddressChange.None };
}
