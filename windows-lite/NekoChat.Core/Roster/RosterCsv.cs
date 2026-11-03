using System.Text;

namespace NekoChat.Core.Roster;

public enum RosterCsvEncoding { Utf8, Big5 }
/// <summary>EndpointProvided=false means the CSV had no ip column: an update keeps the existing address (CM-05).
/// With the column present, an empty cell (Endpoint=null) means the address is cleared.</summary>
public sealed record RosterCsvRow(int Line, string? StudentNumber, string Name, string? Endpoint, bool IsTeacher, bool EndpointProvided = true);
public sealed record RosterCsvError(int Line, string Field, string Code);

public sealed class RosterCsvResult
{
    public string? FatalError { get; }
    public IReadOnlyList<RosterCsvRow> Rows { get; }
    public IReadOnlyList<RosterCsvError> Errors { get; }

    internal RosterCsvResult(string? fatal, IEnumerable<RosterCsvRow>? rows = null, IEnumerable<RosterCsvError>? errors = null)
    {
        FatalError = fatal;
        Rows = Array.AsReadOnly(rows?.ToArray() ?? []);
        Errors = Array.AsReadOnly(errors?.ToArray() ?? []);
    }
}

/// <summary>In-memory parsing only. Applying a preview must separately validate the current DB snapshot.</summary>
public static class RosterCsv
{
    public const int MaxBytes = 1_048_576;
    public const int MaxRows = 2000;

    // The adapter supplies local interface addresses and directed broadcasts (which require subnet masks).
    public static RosterCsvResult Parse(ReadOnlySpan<byte> bytes, RosterCsvEncoding encoding = RosterCsvEncoding.Utf8,
        IReadOnlyCollection<string>? excludedAddresses = null)
    {
        if (bytes.Length > MaxBytes) return new("file_too_large");
        if (encoding is not (RosterCsvEncoding.Utf8 or RosterCsvEncoding.Big5)) throw new ArgumentOutOfRangeException(nameof(encoding));
        string text;
        try
        {
            if (encoding == RosterCsvEncoding.Utf8)
            {
                if (bytes.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) bytes = bytes[3..];
                text = new UTF8Encoding(false, true).GetString(bytes);
            }
            else
            {
                var big5 = CodePagesEncodingProvider.Instance.GetEncoding(950, EncoderFallback.ExceptionFallback,
                    DecoderFallback.ExceptionFallback) ?? throw new NotSupportedException("Big5 unavailable.");
                text = big5.GetString(bytes);
            }
        }
        catch (DecoderFallbackException) { return new(encoding == RosterCsvEncoding.Utf8 ? "encoding_choice_required" : "invalid_big5"); }
        if (text.Contains('\0')) return new("invalid_csv");
        if (!TryRead(text, out var records)) return new("invalid_csv");
        if (records.Count == 0) return new("invalid_header");
        string[] header = records[0].Fields;
        if (header.Length is < 2 or > 4 || header.Distinct(StringComparer.Ordinal).Count() != header.Length ||
            !header.Contains("student_number", StringComparer.Ordinal) || !header.Contains("name", StringComparer.Ordinal) ||
            header.Any(h => h is not ("student_number" or "name" or "ip" or "role"))) return new("invalid_header");
        if (records.Count - 1 > MaxRows) return new("too_many_rows");
        var rows = new List<RosterCsvRow>();
        var errors = new List<RosterCsvError>();
        var allKeys = new List<(int Line, string? Student, string? Endpoint)>();
        foreach (var record in records.Skip(1))
        {
            if (record.Fields.Length != header.Length)
            {
                errors.Add(new(record.Line, "", "column_count"));
                continue;
            }
            string Field(string key) => Array.IndexOf(header, key) is var i && i >= 0 ? record.Fields[i].Trim() : "";
            string name = Field("name"), student = Field("student_number"), endpoint = Field("ip"), role = Field("role");
            string? number = student.Length == 0 ? null : student;
            string? ip = endpoint.Length == 0 ? null : endpoint;
            int before = errors.Count;
            if (!RosterFields.ValidName(name)) errors.Add(new(record.Line, "name", "invalid_name"));
            if (!RosterFields.ValidStudent(number)) errors.Add(new(record.Line, "student_number", "invalid_student_number"));
            if (ip is not null && (!RosterFields.ValidEndpoint(ip) || excludedAddresses?.Contains(ip, StringComparer.Ordinal) == true))
                errors.Add(new(record.Line, "ip", "invalid_endpoint"));
            if (role is not ("" or "student" or "teacher")) errors.Add(new(record.Line, "role", "invalid_role"));
            // Even an otherwise invalid row participates in duplicates: removing it must not silently pick a winner.
            allKeys.Add((record.Line, RosterFields.ValidStudent(number) ? RosterFields.StudentKey(number) : null,
                ip is not null && RosterFields.ValidEndpoint(ip) ? ip : null));
            if (errors.Count == before) rows.Add(new(record.Line, number, name, ip, role == "teacher", Array.IndexOf(header, "ip") >= 0));
        }
        foreach (var group in allKeys.Where(k => k.Student is not null).GroupBy(k => k.Student, StringComparer.Ordinal).Where(g => g.Count() > 1))
            foreach (var key in group) errors.Add(new(key.Line, "student_number", "duplicate_student_number"));
        foreach (var group in allKeys.Where(k => k.Endpoint is not null).GroupBy(k => k.Endpoint, StringComparer.Ordinal).Where(g => g.Count() > 1))
            foreach (var key in group) errors.Add(new(key.Line, "ip", "duplicate_endpoint"));
        var rejected = errors.Select(e => e.Line).ToHashSet();
        return new(null, rows.Where(r => !rejected.Contains(r.Line)), errors.OrderBy(e => e.Line).ThenBy(e => e.Field, StringComparer.Ordinal));
    }

    private sealed record CsvRecord(int Line, string[] Fields);

    private static bool TryRead(string text, out List<CsvRecord> records)
    {
        records = [];
        var fields = new List<string>();
        var field = new StringBuilder();
        int state = 0, line = 1, recordLine = 1;
        bool touched = false;
        // 0 = field start, 1 = unquoted, 2 = quoted, 3 = after closing quote.
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (state == 2)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else state = 3;
                }
                else
                {
                    field.Append(c);
                    if (c == '\n') line++;
                }
                continue;
            }
            if (c is '\r' or '\n')
            {
                if (c == '\r' && (i + 1 >= text.Length || text[++i] != '\n')) return false;
                fields.Add(field.ToString());
                if (touched || fields.Count > 1) records.Add(new(recordLine, fields.ToArray()));
                if (records.Count > MaxRows + 1) return true;
                fields.Clear(); field.Clear(); state = 0; touched = false;
                line++; recordLine = line;
            }
            else if (c == ',') { fields.Add(field.ToString()); field.Clear(); state = 0; touched = true; }
            else if (state == 3) return false;
            else if (c == '"')
            {
                if (state != 0) return false;
                state = 2; touched = true;
            }
            else { field.Append(c); state = 1; touched = true; }
        }
        if (state == 2) return false;
        if (touched || fields.Count > 0) { fields.Add(field.ToString()); records.Add(new(recordLine, fields.ToArray())); }
        return true;
    }
}
