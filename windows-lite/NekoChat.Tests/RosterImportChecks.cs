using System.Text;
using NekoChat.Core.Roster;

namespace NekoChat.Tests;

internal static class RosterImportChecks
{
    public static int Run()
    {
        int checks = 0;
        void Check(bool condition, string description)
        {
            checks++;
            if (!condition) throw new Exception("Roster import: " + description);
        }
        void Reject(Action action, string description)
        {
            bool rejected = false;
            try { action(); } catch (ArgumentException) { rejected = true; }
            Check(rejected, description);
        }
        RosterCsvResult Csv(string body) => RosterCsv.Parse(Encoding.UTF8.GetBytes("student_number,name,ip,role\n" + body));
        RosterSnapshotRow Row(string id, string name, string? number = null, string? ip = null, string? peer = null, bool teacher = false) =>
            new(new RosterEntryView(id, name, number, teacher, ip, peer));
        var a = Row("a", "甲", "1", "192.168.1.1", "Peer_A");
        var b = Row("b", "乙", "2", "192.168.1.2", "Peer_B");
        var c = Row("c", "丙", "3", "192.168.1.3");
        RosterSnapshotRow[] current = [a, b, c];
        var normal = RosterImportPreview.Create(current, Csv("01,甲改名,192.168.1.4,teacher\n2,乙,192.168.1.2,student\n4,丁,,student"));
        Check(normal.Items.Select(i => i.Action).SequenceEqual(new[] { RosterImportAction.Update, RosterImportAction.Unchanged, RosterImportAction.Add }),
            "numeric-key match, no-op and add produce the expected actions");
        Check(normal.Items[0].TargetId == "a" && normal.Items[0].Row!.StudentNumber == "01" && normal.Changes.Count == 2,
            "number presentation can change without moving the target row");
        Check(normal.NotReferencedIds.SequenceEqual(new[] { "c" }) && current[2].Entry == c.Entry,
            "absent rows are reported and retained");
        Check(a.Entry.PeerId == "Peer_A" && a.Entry.DisplayName == "甲" && a.Entry.Endpoint == "192.168.1.1",
            "preview never mutates bound rows or their identity");
        Check(normal.MatchesSnapshot(current.Reverse()), "snapshot token independent of input enumeration order");

        var swap = RosterImportPreview.Create(current, Csv("1,甲,192.168.1.2,student\n2,乙,192.168.1.1,student"));
        Check(swap.Changes.Count == 2 && swap.Changes.All(i => i.Action == RosterImportAction.Update), "complete endpoint swap accepted together");
        var swapReverse = RosterImportPreview.Create(current, Csv("2,乙,192.168.1.1,student\n1,甲,192.168.1.2,student"));
        Check(swapReverse.Changes.Select(i => i.TargetId).Order().SequenceEqual(new[] { "a", "b" }), "swap decision independent of CSV order");
        var cycle = RosterImportPreview.Create(current, Csv("1,甲,192.168.1.2,student\n2,乙,192.168.1.3,student\n3,丙,192.168.1.1,student"));
        Check(cycle.Changes.Count == 3, "three-row endpoint cycle accepted");
        var partial = RosterImportPreview.Create(current, Csv("1,甲,192.168.1.2,student\n2,,192.168.1.1,student"));
        Check(partial.Changes.Count == 0 && partial.Items[0].ErrorCodes.Single() == "endpoint_conflict" &&
            partial.Items[1].ErrorCodes.Single() == "invalid_name", "invalid row retains its old endpoint and blocks a dependent update");
        var cascading = RosterImportPreview.Create(current, Csv("1,甲,192.168.1.2,student\n2,乙,192.168.1.3,student\n4,丁,192.168.1.4,student"));
        Check(cascading.Items.Take(2).All(i => i.Action == RosterImportAction.Error && i.ErrorCodes.Single() == "endpoint_conflict") &&
            cascading.Changes.Single().Row!.StudentNumber == "4", "conflict removal reaches fixed point without discarding independent additions");
        var occupied = RosterImportPreview.Create(current, Csv("4,丁,192.168.1.1,student"));
        Check(occupied.Changes.Count == 0 && occupied.Items.Single().ErrorCodes.Single() == "endpoint_conflict", "new row cannot steal an untouched endpoint");
        var clears = RosterImportPreview.Create(current, Csv("1,甲,,student\n4,丁,192.168.1.1,student"));
        Check(clears.Changes.Count == 2 && clears.Items[0].Row!.Endpoint is null, "explicit clearing can free an endpoint for a new row");

        var n1 = Row("n1", "同名", ip: "10.0.0.1", peer: "Pinned_1");
        var n2 = Row("n2", "同名", ip: "10.0.0.2", peer: "Pinned_2");
        var names = Csv(",同名,10.0.0.3,student");
        var ambiguous = RosterImportPreview.Create(new[] { n1, n2 }, names);
        Check(ambiguous.Changes.Count == 0 && ambiguous.Items.Single().Action == RosterImportAction.Ambiguous &&
            ambiguous.Items.Single().CandidateIds.SequenceEqual(new[] { "n1", "n2" }), "multiple exact-name candidates require a choice");
        var choose = RosterImportPreview.Create(new[] { n1, n2 }, names, new[] { new RosterImportChoice(2, "n2") });
        Check(choose.Changes.Single().TargetId == "n2" && n1.Entry.PeerId == "Pinned_1" && n2.Entry.PeerId == "Pinned_2",
            "explicit match selects an existing row without moving either binding");
        var chooseNew = RosterImportPreview.Create(new[] { n1, n2 }, names, new[] { new RosterImportChoice(2, null) });
        Check(chooseNew.Changes.Single().Action == RosterImportAction.Add && chooseNew.Changes.Single().TargetId is null,
            "explicit new row does not inherit a candidate's peer id");
        var single = RosterImportPreview.Create(new[] { n1 }, names);
        Check(single.Changes.Single().TargetId == "n1" && single.Items.Single().CandidateIds.Single() == "n1", "single exact-name candidate is visible in preview");
        var doubleNames = Csv(",同名,10.0.0.3,student\n,同名,10.0.0.4,student");
        var unresolved = RosterImportPreview.Create(new[] { n1 }, doubleNames);
        Check(unresolved.Items.All(i => i.Action == RosterImportAction.Ambiguous), "two input rows matching one candidate remain unresolved");
        var duplicateTarget = RosterImportPreview.Create(new[] { n1 }, doubleNames,
            new[] { new RosterImportChoice(2, "n1"), new RosterImportChoice(3, "n1") });
        Check(duplicateTarget.Changes.Count == 0 && duplicateTarget.Items.All(i => i.ErrorCodes.Single() == "duplicate_target"),
            "explicit choices cannot let the later line overwrite the earlier line");
        var noOpDuplicate = RosterImportPreview.Create(new[] { n1 }, Csv(",同名,10.0.0.1,student\n,同名,10.0.0.3,student"),
            new[] { new RosterImportChoice(2, "n1"), new RosterImportChoice(3, "n1") });
        Check(noOpDuplicate.Changes.Count == 0 && noOpDuplicate.Items.All(i => i.ErrorCodes.Contains("duplicate_target")),
            "duplicate-target protection includes an unchanged row");
        var mixedChoice = RosterImportPreview.Create(new[] { n1 }, doubleNames, new[] { new RosterImportChoice(2, "n1") });
        Check(mixedChoice.Changes.Single().Line == 2 && mixedChoice.Items[1].Action == RosterImportAction.Ambiguous, "unresolved choices are skipped, not guessed");
        Check(RosterImportPreview.Create(Array.Empty<RosterSnapshotRow>(), doubleNames).Items.All(i => i.Action == RosterImportAction.Ambiguous),
            "same-file no-number names require explicit choice even before any existing candidate");
        Check(RosterImportPreview.Create(Array.Empty<RosterSnapshotRow>(), doubleNames,
            new[] { new RosterImportChoice(2, null), new RosterImportChoice(3, null) }).Changes.Count == 2, "two explicit new rows may share a name");
        Check(RosterImportPreview.Create(new[] { n1 }, Csv(",同名,10.0.0.3,teacher")).Items.Single().Action == RosterImportAction.Add,
            "role label is part of no-number candidate matching");
        Check(RosterImportPreview.Create(current, Csv("1,甲,,student"), new[] { new RosterImportChoice(2, "b") }).Items.Single().ErrorCodes.Single() == "invalid_choice",
            "school-number target cannot be redirected");
        Check(RosterImportPreview.Create(current, Csv("1,甲,,student"), new[] { new RosterImportChoice(2, null) }).Changes.Count == 0,
            "school-number match cannot be forced into a duplicate new row");
        Check(RosterImportPreview.Create(new[] { n1 }, names, new[] { new RosterImportChoice(2, "missing") }).Items.Single().Action == RosterImportAction.Error,
            "unknown target does not fall back to automatic matching");
        Reject(() => RosterImportPreview.Create(current, Csv("4,丁,,student"), new[] { new RosterImportChoice(9, null) }), "choice references a valid line");
        Reject(() => RosterImportPreview.Create(current, Csv("4,丁,,student"), new[] { new RosterImportChoice(2, null), new RosterImportChoice(2, null) }), "duplicate choice rejected");
        Reject(() => RosterImportPreview.Create(current, RosterCsv.Parse(Array.Empty<byte>())), "fatal CSV cannot become an empty successful preview");

        var golden = new RosterSnapshotRow(new RosterEntryView("a", "甲🐈", "02", endpoint: "192.168.1.2", peerId: "Peer_A"), "csv");
        string digest = RosterImportPreview.ComputeSnapshotToken(new[] { golden });
        Check(digest == "2d7dd324ea4cc79ee1f64bb61a4f3c0b36f1093419683b5f22a6363f09e4ff68",
            "golden canonical digest independently calculated in Python using UTF8 byte lengths and signed big-endian Int32 prefixes");
        var variants = new[] {
            golden with { Entry = Row("b", "甲🐈", "02", "192.168.1.2", "Peer_A").Entry },
            golden with { Entry = Row("a", "乙🐈", "02", "192.168.1.2", "Peer_A").Entry },
            golden with { Entry = Row("a", "甲🐈", "2", "192.168.1.2", "Peer_A").Entry },
            golden with { Entry = Row("a", "甲🐈", "02", "192.168.1.2", "Peer_A", true).Entry },
            golden with { Entry = Row("a", "甲🐈", "02", "192.168.1.3", "Peer_A").Entry },
            golden with { Entry = Row("a", "甲🐈", "02", "192.168.1.2", "Peer_B").Entry },
            golden with { Source = "manual" }, golden with { ImportBatchId = "batch" }, golden with { ImportBatchId = "" } };
        foreach (var variant in variants)
            Check(RosterImportPreview.ComputeSnapshotToken(new[] { variant }) != digest, "all stored columns participate in snapshot token");
        Check(!normal.MatchesSnapshot(current.Take(2)) && !normal.MatchesSnapshot(current.Append(Row("d", "新增"))), "addition and deletion invalidate preview");
        Check(!normal.MatchesSnapshot(new[] { a, b, c with { Source = "manual" } }), "source metadata changes invalidate preview");
        Reject(() => RosterImportPreview.ComputeSnapshotToken(new[] { a, a }), "snapshot duplicate ids rejected");
        Reject(() => RosterImportPreview.ComputeSnapshotToken(new[] { a, Row("x", "別人", "01") }), "snapshot duplicate normalized numbers rejected");
        Reject(() => RosterImportPreview.ComputeSnapshotToken(new[] { a, Row("x", "別人", ip: "192.168.1.1") }), "snapshot duplicate endpoints rejected");
        Reject(() => RosterImportPreview.ComputeSnapshotToken(new[] { golden with { ImportBatchId = "\ud800" } }), "snapshot UTF8 encoder never replaces invalid surrogate");
        Check(((ICollection<RosterImportItem>)normal.Items).IsReadOnly && ((ICollection<string>)ambiguous.Items[0].CandidateIds).IsReadOnly,
            "preview and candidate containers cannot be edited after creation");
        return checks;
    }
}
