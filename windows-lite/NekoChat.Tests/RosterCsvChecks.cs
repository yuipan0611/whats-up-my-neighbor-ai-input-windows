using System.Globalization;
using System.Text;
using NekoChat.Core.Roster;

namespace NekoChat.Tests;

internal static class RosterCsvChecks
{
    public static int Run()
    {
        int checks = 0;
        void Check(bool value, string description)
        {
            checks++;
            if (!value) throw new Exception("Roster: " + description);
        }
        void RejectView(Action action, string description)
        {
            bool rejected = false;
            try { action(); } catch (ArgumentException) { rejected = true; }
            Check(rejected, description);
        }
        RosterCsvResult Parse(string text) => RosterCsv.Parse(Encoding.UTF8.GetBytes(text));
        string header = "student_number,name,ip,role\r\n";

        var simple = Parse(header + "002, 王小明 ,192.168.1.2,student\r\n,老師,10.1.2.3,teacher\r\n");
        Check(simple.FatalError is null && simple.Errors.Count == 0 && simple.Rows.Count == 2, "valid four-column fixture");
        Check(simple.Rows[0] == new RosterCsvRow(2, "002", "王小明", "192.168.1.2", false), "leading zero and trimmed display fields preserved");
        Check(simple.Rows[1] == new RosterCsvRow(3, null, "老師", "10.1.2.3", true), "teacher without student number");
        var minimum = Parse("name,student_number\n中文🐈,000\n沒有學號,\n");
        Check(minimum.Rows.Count == 2 && minimum.Rows[0].Name == "中文🐈" && minimum.Rows[0].StudentNumber == "000" &&
            minimum.Rows.All(r => !r.IsTeacher && r.Endpoint is null), "reordered minimal headers and defaults");
        var quoted = Parse("student_number,name\n1,\"王,小\"\"明\"\n2,第二位");
        Check(quoted.Errors.Count == 0 && quoted.Rows[0].Name == "王,小\"明" && quoted.Rows[1].Line == 3, "commas and doubled quotes");
        var multiline = Parse("student_number,name\n1,\"兩\r\n行\"\n2,正常\n");
        Check(multiline.FatalError is null && multiline.Errors.Single().Line == 2 && multiline.Rows.Single().Line == 4,
            "quoted newline parsed safely, then name control rejected with physical start line");
        Check(Parse("\n\r\nstudent_number,name\n\n1,貓\n\n").Rows.Single().Line == 5, "empty physical lines skipped without changing diagnostics");
        foreach (string bad in new[] { "", "name\n貓", "name,Name,student_number\n", "name,name,student_number\n",
            "student_number,name,unknown\n", " student_number,name\n" })
            Check(Parse(bad).FatalError == "invalid_header", "missing, duplicate or unknown header rejected");
        foreach (string bad in new[] { "1,\"unterminated", "1,not\"quoted", "1,\"closed\"junk", "1,\"closed\" \n", "1,貓\r2,狗", "1,貓\0" })
        {
            var result = Parse("student_number,name\n" + bad);
            Check(result.FatalError == "invalid_csv" && result.Rows.Count == 0, "malformed syntax never produces a partial applicable plan");
        }
        var columns = Parse("student_number,name\n1,貓,多一欄\n2\n3,正常\n");
        Check(columns.Errors.Count == 2 && columns.Errors.All(e => e.Code == "column_count") && columns.Rows.Single().StudentNumber == "3",
            "row-local column count errors leave well-formed rows available for preview");
        foreach (string number in new[] { "-1", "1.0", "１", "A12", "1234567890123", "1 2" })
            Check(Parse($"student_number,name\n{number},貓").Errors.Single().Code == "invalid_student_number", "strict ASCII student number");
        foreach (string name in new[] { "", "   ", "a\tb", "a\u007fb", new string('貓', 65) })
            Check(Parse($"student_number,name\n1,{name}").Errors.Single().Code == "invalid_name", "invalid display name");
        Check(Parse($"student_number,name\n1,{string.Concat(Enumerable.Repeat("🐈", 64))}").Rows.Count == 1,
            "64 Unicode scalar name fits SQLite length semantics");
        foreach (string ip in new[] { "127.0.0.1", "8.8.8.8", "192.168.01.2", "192.168.1", "3232235778", "0.0.0.0", "255.255.255.255",
            "224.0.0.1", "::1", "192.168.1.2:8768", "192.168.1.256", "172.15.1.1", "172.32.1.1" })
            Check(Parse(header + $"1,貓,{ip},student").Errors.Single().Code == "invalid_endpoint", "canonical private IPv4 only");
        foreach (string ip in new[] { "10.0.0.1", "172.16.0.1", "172.31.255.254", "192.168.1.2" })
            Check(Parse(header + $"1,貓,{ip},student").Rows.Count == 1, "private range boundary accepted");
        var excluded = RosterCsv.Parse(Encoding.UTF8.GetBytes(header + "1,自己,192.168.1.2,student\n2,廣播,192.168.1.255,student"),
            excludedAddresses: new[] { "192.168.1.2", "192.168.1.255" });
        Check(excluded.Rows.Count == 0 && excluded.Errors.Count == 2, "adapter supplies local and subnet broadcast exclusions");
        Check(Parse(header + "1,貓,,admin").Errors.Single().Code == "invalid_role", "teacher is a label with exact vocabulary");
        var duplicates = Parse(header + "01,第一位,192.168.1.1,student\n1,,192.168.1.2,student\n2,第三位,192.168.1.1,student\n3,正常,,student\n");
        Check(duplicates.Rows.Single().StudentNumber == "3" && duplicates.Errors.Count(e => e.Code == "duplicate_student_number") == 2 &&
            duplicates.Errors.Count(e => e.Code == "duplicate_endpoint") == 2, "all duplicates rejected even when another field is invalid");
        Check(duplicates.Errors.All(e => e.Field is "name" or "student_number" or "ip") && duplicates.Errors.All(e => !e.ToString().Contains("第一位")),
            "diagnostics contain only line, field and stable error code");

        byte[] utf8 = Encoding.UTF8.GetBytes("student_number,name\n1,貓🐈");
        Check(RosterCsv.Parse(new byte[] { 0xEF, 0xBB, 0xBF }.Concat(utf8).ToArray()).Rows.Single().Name == "貓🐈", "UTF8 BOM");
        foreach (byte[] invalid in new[] { new byte[] { 0xC0, 0xAF }, new byte[] { 0xED, 0xA0, 0x80 }, new byte[] { 0xF4, 0x90, 0x80, 0x80 }, new byte[] { 0xE4, 0xB8 } })
            Check(RosterCsv.Parse(utf8.Concat(invalid).ToArray()).FatalError == "encoding_choice_required", "strict UTF8 rejects malformed sequences without fallback");
        var big5 = CodePagesEncodingProvider.Instance.GetEncoding(950)!;
        byte[] legacy = big5.GetBytes("student_number,name\r\n1,王小明\r\n");
        Check(RosterCsv.Parse(legacy).FatalError == "encoding_choice_required", "Big5 requires explicit selection");
        Check(RosterCsv.Parse(legacy, RosterCsvEncoding.Big5).Rows.Single().Name == "王小明", "explicit Big5 round trip");
        Check(RosterCsv.Parse(new byte[] { 0x81 }, RosterCsvEncoding.Big5).FatalError == "invalid_big5", "invalid Big5 rejected");
        string many = "student_number,name\n" + string.Concat(Enumerable.Range(1, 2000).Select(i => $"{i},貓\n"));
        Check(Parse(many).Rows.Count == 2000 && Parse(many + "2001,狗\n").FatalError == "too_many_rows", "2000 row boundary");
        byte[] exact = Enumerable.Repeat((byte)' ', RosterCsv.MaxBytes).ToArray();
        utf8.CopyTo(exact, 0);
        Check(RosterCsv.Parse(exact).FatalError != "file_too_large", "exactly one MiB reaches parsing");
        Check(RosterCsv.Parse(new byte[RosterCsv.MaxBytes + 1]).FatalError == "file_too_large", "one byte over limit rejected before decoding");

        var entries = new[] {
            new RosterEntryView("z", "老師無號", isTeacher: true),
            new RosterEntryView("t", "老師有號", "10", true),
            new RosterEntryView("b", "相同", "0002", endpoint: "192.168.1.2", peerId: "AbC_shortTail"),
            new RosterEntryView("c", "十號", "10"), new RosterEntryView("a", "相同", "2"),
            new RosterEntryView("y", "沒有學號"), new RosterEntryView("x", "最後但id較小") };
        Check(string.Join(',', RosterEntryView.SortAndFilter(entries).Select(e => e.Id)) == "t,z,a,b,c,x,y", "teachers, numeric keys, missing numbers, stable ordinal id");
        Check(entries[2].StudentNumber == "0002" && entries[2].StudentNumberKey == "2", "normalization preserves original student text");
        Check(RosterEntryView.SortAndFilter(entries, "相同").Count == 2, "name search");
        Check(RosterEntryView.SortAndFilter(entries, "0002").Single().Id == "b", "student text search");
        Check(RosterEntryView.SortAndFilter(entries, "168.1.2").Single().Id == "b", "endpoint search");
        Check(RosterEntryView.SortAndFilter(entries, "shortTAIL").Single().Id == "b", "identity suffix search uses ordinal ignore case");
        Check(RosterEntryView.SortAndFilter(entries, "找不到").Count == 0 && RosterEntryView.SortAndFilter(Array.Empty<RosterEntryView>()).Count == 0,
            "search-empty and roster-empty remain distinguishable to consumer");
        var culture = CultureInfo.CurrentCulture;
        try {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            Check(RosterEntryView.SortAndFilter(entries, "SHORTTAIL").Single().Id == "b", "search and order independent of current culture");
        } finally { CultureInfo.CurrentCulture = culture; }
        RejectView(() => new RosterEntryView("a", "\ud800"), "public display model rejects unpaired surrogate");
        RejectView(() => new RosterEntryView("a", "貓", "１２"), "public model rejects non-ASCII school number");
        RejectView(() => new RosterEntryView("a", "貓", endpoint: "8.8.8.8"), "public display model shares endpoint rules");
        Check(new RosterEntryView("a", "貓", "000").StudentNumberKey == "0", "all-zero student number key");
        return checks;
    }
}
