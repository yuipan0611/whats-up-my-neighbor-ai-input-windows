using System.Globalization;
using System.Net;
using System.Text;

namespace NekoChat.Core.Roster;

/// <summary>Validated display data. A label or endpoint never grants identity or permissions.</summary>
public sealed class RosterEntryView
{
    public string Id { get; }
    public string DisplayName { get; }
    public string? StudentNumber { get; }
    public string? StudentNumberKey { get; }
    public bool IsTeacher { get; }
    public string? Endpoint { get; }
    public string? PeerId { get; }

    public RosterEntryView(string id, string displayName, string? studentNumber = null,
        bool isTeacher = false, string? endpoint = null, string? peerId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (!RosterFields.ValidName(displayName)) throw new ArgumentException("Invalid name.", nameof(displayName));
        if (!RosterFields.ValidStudent(studentNumber)) throw new ArgumentException("Invalid student number.", nameof(studentNumber));
        if (endpoint is not null && !RosterFields.ValidEndpoint(endpoint)) throw new ArgumentException("Invalid endpoint.", nameof(endpoint));
        Id = id;
        DisplayName = displayName;
        StudentNumber = studentNumber;
        StudentNumberKey = RosterFields.StudentKey(studentNumber);
        IsTeacher = isTeacher;
        Endpoint = endpoint;
        PeerId = peerId;
    }

    public static IReadOnlyList<RosterEntryView> SortAndFilter(IEnumerable<RosterEntryView> entries, string query = "")
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(query);
        string search = query.Trim();
        return Array.AsReadOnly(entries.Where(e => search.Length == 0 ||
                e.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                (e.StudentNumber?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (e.Endpoint?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (e.PeerId?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false))
            .OrderByDescending(e => e.IsTeacher)
            .ThenBy(e => e.StudentNumberKey is null)
            .ThenBy(e => e.StudentNumberKey?.Length ?? 0)
            .ThenBy(e => e.StudentNumberKey, StringComparer.Ordinal)
            .ThenBy(e => e.Id, StringComparer.Ordinal).ToArray());
    }
}

internal static class RosterFields
{
    internal static bool ValidName(string? value) => value is not null && ValidUtf16(value) && value == value.Trim() &&
        value.EnumerateRunes().Count() is >= 1 and <= 64 &&
        value.EnumerateRunes().All(r => Rune.GetUnicodeCategory(r) is not (UnicodeCategory.Control or UnicodeCategory.Surrogate));

    private static bool ValidUtf16(string value)
    {
        for (int i = 0; i < value.Length; i++)
            if (char.IsSurrogate(value[i]) && (!char.IsHighSurrogate(value[i]) || ++i >= value.Length || !char.IsLowSurrogate(value[i])))
                return false;
        return true;
    }

    internal static bool ValidStudent(string? value) => value is null ||
        value.Length is >= 1 and <= 12 && value.All(c => c is >= '0' and <= '9');

    internal static string? StudentKey(string? value) => value is null ? null :
        value.TrimStart('0') is { Length: > 0 } key ? key : "0";

    internal static bool ValidEndpoint(string value)
    {
        if (!IPAddress.TryParse(value, out var address) || address.ToString() != value) return false;
        byte[] b = address.GetAddressBytes();
        return b.Length == 4 && (b[0] == 10 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168);
    }
}
