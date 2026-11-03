using NekoChat.Core.Storage;
using static NekoChat.Tests.StorageFixKit;

namespace NekoChat.Tests;

/// <summary>Review item D + CM-12: the draft limit is its own byte limit (default 1 MiB), independent of TextValidator (the send limit); injectable.</summary>
internal static class StorageFix_DraftLimit
{
    private static string Conv(ChatStore store) => ((PeerRegistered)store.RegisterPeer("P1", PairingState.Paired)).Conversation.ConversationId;

    internal static int Run()
    {
        var k = new Counter();

        // 1. default: 1 MiB of UTF-8 (CM-12), independent of the send limit
        using (var area = new Area())
        using (var store = ChatStore.Open(new(area.Data)))
        {
            string c = Conv(store); string db = store.DatabasePath;
            string max = new('a', 1_048_576), over = max + "a";
            k.Check(store.SaveDraft(c, max, 0, 1) is { Saved: true }, "draft of exactly 1 MiB is saved");
            var rejected = store.SaveDraft(c, over, 0, 2);
            k.Check(rejected is { Saved: false, Reason: "draft-too-large", CurrentEpoch: 0, CurrentRevision: 1 }, "1 MiB + 1 byte is rejected with the current guard");
            k.Check(Number(db, "SELECT draftRevision FROM Conversation WHERE id=@c", ("@c", c)) == 1 && Text(db, "SELECT rawText FROM Draft WHERE conversationId=@c", ("@c", c)) == max, "rejected save changes neither the guard nor the stored draft (no truncation)");
            k.Check(store.SaveDraft(c, "   ", 0, 2) is { Saved: true } && Text(db, "SELECT rawText FROM Draft WHERE conversationId=@c", ("@c", c)) == "   ", "a whitespace-only draft (user pressed space) is allowed");
            k.Check(store.SaveDraft(c, "", 0, 3) is { Saved: true } && Number(db, "SELECT count(*) FROM Draft") == 0, "clearing the draft is always allowed");
            k.Check(store.SaveDraft(c, over, 0, 4) is { Saved: false, Reason: "draft-too-large", CurrentRevision: 3 }, "an over-limit draft does not consume the next revision");
            k.Check(store.SaveDraft(c, "ok", 0, 4) is { Saved: true }, "the same revision number is still usable afterwards");
            k.Check(store.SaveDraft(c, over, 1, 9) is { Saved: false, Reason: "stale-epoch" }, "stale epoch is reported before size so the UI resynchronizes first");
            string cat = "\U0001F408", straddleOk = new string('a', 1_048_576 - 4) + cat, straddleOver = new string('a', 1_048_576 - 3) + cat;
            k.Check(store.SaveDraft(c, straddleOk, 0, 5).Saved, "4-byte character ending exactly at 1 MiB is saved");
            k.Check(store.SaveDraft(c, straddleOver, 0, 6) is { Saved: false, Reason: "draft-too-large" }, "4-byte character straddling 1 MiB (1 byte over) is rejected");
            k.Check(Text(db, "SELECT rawText FROM Draft WHERE conversationId=@c", ("@c", c)) == straddleOk, "straddling rejection leaves the previous draft intact");
        }

        // 2. the draft limit does not follow TextValidator any more (CM-12), but its own byte option
        using (var area = new Area())
        using (var store = ChatStore.Open(new(area.Data) { TextValidator = t => !string.IsNullOrWhiteSpace(t) && t.Length <= 5, MaxDraftUtf8Bytes = 8 }))
        {
            string c = Conv(store);
            k.Check(store.SaveDraft(c, "123456", 0, 1).Saved, "a draft longer than the send limit is kept (send limit is checked when sending)");
            k.Check(store.SaveDraft(c, "12345678", 0, 2).Saved && !store.SaveDraft(c, "123456789", 0, 3).Saved, "MaxDraftUtf8Bytes is counted in UTF-8 bytes");
            k.Check(store.SaveDraft(c, "貓貓", 0, 3).Saved == true && !store.SaveDraft(c, "貓貓貓", 0, 4).Saved, "3-byte characters count 3 bytes (6 ok, 9 over)");
        }

        // 3. DraftValidator overrides it
        using (var area = new Area())
        using (var store = ChatStore.Open(new(area.Data) { TextValidator = t => t.Length <= 3, DraftValidator = t => t.Length <= 10 }))
        {
            string c = Conv(store);
            k.Check(store.SaveDraft(c, "0123456789", 0, 1).Saved && !store.SaveDraft(c, "01234567890", 0, 2).Saved, "DraftValidator override is honored");
        }
        return k.Count;
    }
}
