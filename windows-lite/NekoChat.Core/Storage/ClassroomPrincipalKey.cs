namespace NekoChat.Core.Storage;

/// <summary>
/// CM-10 (frozen): who a classroom conversation belongs to. <c>roster:&lt;RosterEntryId&gt;</c> for a roster row,
/// <c>ip:&lt;canonical IPv4&gt;</c> for a source that is not on the roster. Decided only from the address of the accepted socket;
/// no field inside a message is ever used. Only IPv4 is accepted (IPv4-mapped IPv6 is normalised first).
/// </summary>
public readonly record struct ClassroomPrincipalKey
{
    public string Value { get; }
    private ClassroomPrincipalKey(string value) => Value = value;

    public bool IsRoster => Value.StartsWith("roster:", StringComparison.Ordinal);
    public string? RosterEntryId => IsRoster ? Value[7..] : null;
    public string? SourceIp => IsRoster ? null : Value[3..];
    public override string ToString() => Value;

    public static ClassroomPrincipalKey ForRosterEntry(string rosterEntryId)
    {
        if (!StoreIds.IsValid(rosterEntryId)) throw new ArgumentException("Invalid roster entry id.", nameof(rosterEntryId));
        return new("roster:" + rosterEntryId);
    }

    public static ClassroomPrincipalKey ForSourceIp(string canonicalIPv4)
    {
        if (!TryCanonicalIPv4(canonicalIPv4, out string? canonical)) throw new ArgumentException("Invalid IPv4 source.", nameof(canonicalIPv4));
        return new("ip:" + canonical);
    }

    /// <summary>
    /// <paramref name="canonicalIPv4"/> is what the transport adapter produced from the accepted socket (see <c>ClassroomSource</c>, which also maps
    /// IPv4-mapped IPv6). Null for anything that is not a canonical unicast dotted IPv4, so the caller rejects and diagnoses.
    /// </summary>
    public static ClassroomPrincipalKey? ForSource(string? canonicalIPv4) => TryCanonicalIPv4(canonicalIPv4, out string? c) ? new("ip:" + c) : null;

    public static bool TryParse(string? value, out ClassroomPrincipalKey key)
    {
        key = default;
        if (value is null) return false;
        if (value.StartsWith("roster:", StringComparison.Ordinal) && StoreIds.IsValid(value[7..])) { key = new(value); return true; }
        if (value.StartsWith("ip:", StringComparison.Ordinal) && TryCanonicalIPv4(value[3..], out _)) { key = new(value); return true; }
        return false;
    }

    /// <summary>Strict dotted decimal: four octets, no leading zeros, not 0.x.x.x, multicast, reserved or broadcast. The Storage layer does no network parsing.</summary>
    internal static bool TryCanonicalIPv4(string? text, out string? canonical)
    {
        canonical = null;
        if (text is null || text.Length is < 7 or > 15) return false;
        var parts = text.Split('.');
        if (parts.Length != 4) return false;
        int first = 0;
        for (int i = 0; i < 4; i++)
        {
            string part = parts[i];
            if (part.Length is < 1 or > 3 || part.Any(c => c is < '0' or > '9') || part.Length > 1 && part[0] == '0') return false;
            int value = int.Parse(part, System.Globalization.CultureInfo.InvariantCulture);
            if (value > 255) return false;
            if (i == 0) first = value;
        }
        if (first == 0 || first >= 224) return false;
        canonical = text;
        return true;
    }
}
