using NekoChat.Core.Storage;

namespace NekoChat.Tests;

internal static class ReadThroughChecks
{
    private sealed class LeaveView : IStoreFaults
    {
        internal bool Current = true;
        public void AfterBegin(string op) { }
        public void BeforeCommit(string op) { if (op == "classroom-read-through") Current = false; }
        public void AfterCommit(string op) { }
    }
    public static void Run()
    {
        int checks = 0;
        void Check(bool value, string name) { checks++; if (!value) throw new Exception("Read through: " + name); }
        string root = Path.Combine(Path.GetTempPath(), "neko-read-through-" + Guid.NewGuid().ToString("N"));
        var principal = ClassroomPrincipalKey.ForSourceIp("10.0.0.11");
        var scope = new LegacyHistoryScope(null, "10.0.0.11");
        var active = new VisibleReadContext(principal.Value, true, true, false, true);
        DateTime clock = DateTime.UtcNow;
        long middle = 0, last = 0;
        try
        {
            using (var store = ChatStore.Open(new(root) { UtcNow = () => clock }))
            {
                for (int i = 0; i < 260; i++)
                {
                    clock = clock.AddMilliseconds(10);
                    var received = (ClassroomStored)store.ReceiveClassroomMessage("10.0.0.11", new(Guid.NewGuid().ToString("D"), "classroom " + i), () => true);
                    if (i == 129) middle = received.LocalSeq;
                    last = received.LocalSeq;
                    clock = clock.AddMilliseconds(10);
                    store.ReceiveLegacyMessage(new("legacy " + i, null), store.GetLegacyReceiveContext("10.0.0.11"), () => true);
                }
                var other = (ClassroomStored)store.ReceiveClassroomMessage("10.0.0.12", new(Guid.NewGuid().ToString("D"), "other"), () => true);
                Check(store.GetClassroomInboxSnapshot().Entries.Single(x => x.Principal == principal).TotalUnread == 520, "both streams begin unread across many unloaded pages");
                foreach (var context in new[] { active with { WindowActive = false }, active with { Minimized = true }, active with { UserOpened = false }, active with { ViewportReady = false }, active with { ActiveConversationId = ClassroomPrincipalKey.ForSourceIp("10.0.0.12").Value } })
                    Check(store.MarkClassroomReadThrough(principal, scope, middle, false, context, () => true).Marked == 0, "foreground/open/conversation guards");
                Check(store.MarkClassroomReadThrough(principal, scope, middle, false, active, () => false).Marked == 0, "stale work is rejected");
                Check(store.MarkClassroomReadThrough(principal, new(null, "10.0.0.12"), middle, false, active, () => true).Reason == "scope-guard", "foreign legacy scope cannot be read");
                var read = store.MarkClassroomReadThrough(principal, scope, middle, false, active, () => true);
                Check(read.Marked == 259 && read.Queued == 0, "reading to middle advances both streams but excludes newer messages");
                Check(store.GetClassroomInboxSnapshot().Entries.Single(x => x.Principal == principal).TotalUnread == 261, "newer offscreen messages stay unread");
                Check(store.ListPendingClassroomReceipts().Count == 0, "local read works with receipt policy disabled");
                Check(store.MarkClassroomReadThrough(principal, scope, middle, false, active, () => true).Marked == 0, "reading position never moves backward or double counts");
                store.SetReadReceiptPolicy(true);
                long legacyLast = store.ListLegacyMessages(scope).Items.Last().LegacySeq;
                read = store.MarkClassroomReadThrough(principal, scope, legacyLast, true, active, () => true);
                Check(read.Marked == 261 && read.Queued == 130, "latest visible legacy anchor clears all earlier unloaded pages and queues only newly read classroom receipts");
                Check(store.GetClassroomInboxSnapshot().Entries.Single(x => x.Principal == principal).TotalUnread == 0, "latest view clears entire prior unread count");
                Check(store.ListClassroomMessages(ClassroomPrincipalKey.ForSourceIp("10.0.0.12")).Items.Single().LocalReadAt is null, "other conversation is untouched");
                clock = clock.AddMilliseconds(10);
                store.ReceiveClassroomMessage("10.0.0.11", new(Guid.NewGuid().ToString("D"), "new after reading"), () => true);
                Check(store.MarkClassroomReadThrough(principal, scope, last, false, active, () => true).Marked == 0, "old anchor cannot consume future incoming message");
            }
            using (var store = ChatStore.Open(new(root)))
                Check(store.GetClassroomInboxSnapshot().Entries.Single(x => x.Principal == principal).TotalUnread == 1, "restart preserves reading position and future unread message");
            var leave = new LeaveView();
            using (var store = ChatStore.Open(new(Path.Combine(root, "stale-commit")) { Faults = leave }))
            {
                var received = (ClassroomStored)store.ReceiveClassroomMessage("10.0.0.11", new("stale", "leave before COMMIT"), () => true);
                store.SetReadReceiptPolicy(true);
                var result = store.MarkClassroomReadThrough(principal, scope, received.LocalSeq, false, active, () => leave.Current);
                Check(result.Reason == "stale-view" && store.ListClassroomMessages(principal).Items.Single().LocalReadAt is null, "leaving view before COMMIT rolls back all local read changes");
                Check(store.ListPendingClassroomReceipts().Count == 0, "stale transaction leaves no outbound read receipts");
            }
            // Timestamp ties follow the exact classroom-before-legacy/sequence order used by WPF.
            using (var store = ChatStore.Open(new(Path.Combine(root, "ties")) { UtcNow = () => clock }))
            {
                var a = (ClassroomStored)store.ReceiveClassroomMessage("10.0.0.11", new("tie-a", "A"), () => true);
                var b = (ClassroomStored)store.ReceiveClassroomMessage("10.0.0.11", new("tie-b", "B"), () => true);
                var l = store.ReceiveLegacyMessage(new("L", null), store.GetLegacyReceiveContext("10.0.0.11"), () => true);
                Check(store.MarkClassroomReadThrough(principal, scope, a.LocalSeq, false, active, () => true).Marked == 1, "classroom tie respects sequence and does not consume later legacy row");
                Check(store.MarkClassroomReadThrough(principal, scope, l.LegacySeq!.Value, true, active, () => true).Marked == 2, "legacy tie includes earlier classroom rows");
                Check(store.MarkClassroomReadThrough(ClassroomPrincipalKey.ForSourceIp("10.0.0.12"), null, b.LocalSeq, false, active with { ActiveConversationId = ClassroomPrincipalKey.ForSourceIp("10.0.0.12").Value }, () => true).Reason == "missing-anchor", "foreign anchor cannot clear another conversation");
            }
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine($"Read through: {checks} real SQLite cursor, privacy, merged timeline and restart checks passed.");
    }
}
