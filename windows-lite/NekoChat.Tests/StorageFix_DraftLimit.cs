using NekoChat.Core.Storage;
using static NekoChat.Tests.StorageFixKit;

namespace NekoChat.Tests;

/// <summary>Review item D: draft size limit, injectable, defaulting to the same source as TextValidator.</summary>
internal static class StorageFix_DraftLimit
{
    private static string Conv(ChatStore store) => ((PeerRegistered)store.RegisterPeer("P1", PairingState.Paired)).Conversation.ConversationId;

    internal static int Run()
    {
        var k = new Counter();

        // 1. default: same limit as the shipped TextValidator
        using (var area = new Area())
        using (var store = ChatStore.Open(new(area.Data)))
        {
            string c = Conv(store); string db = store.DatabasePath;
            string max = string.Concat(Enumerable.Repeat("🐈", 2000)), over = string.Concat(Enumerable.Repeat("🐈", 2001));
            k.Check(store.SaveDraft(c, max, 0, 1) is { Saved: true }, "draft at the send limit (2000 scalars, surrogate pairs) is saved");
            var rejected = store.SaveDraft(c, over, 0, 2);
            k.Check(rejected is { Saved: false, Reason: "draft-too-large", CurrentEpoch: 0, CurrentRevision: 1 }, "one scalar over the limit is rejected with the current guard");
            k.Check(Number(db, "SELECT draftRevision FROM Conversation WHERE id=@c", ("@c", c)) == 1 && Text(db, "SELECT rawText FROM Draft WHERE conversationId=@c", ("@c", c)) == max, "rejected save changes neither the guard nor the stored draft");
            k.Check(store.SaveDraft(c, "   ", 0, 2) is { Saved: true } && Text(db, "SELECT rawText FROM Draft WHERE conversationId=@c", ("@c", c)) == "   ", "a whitespace-only draft (user pressed space) is allowed");
            k.Check(store.SaveDraft(c, "", 0, 3) is { Saved: true } && Number(db, "SELECT count(*) FROM Draft") == 0, "clearing the draft is always allowed");
            k.Check(store.SaveDraft(c, over, 0, 4) is { Saved: false, Reason: "draft-too-large", CurrentRevision: 3 }, "an over-limit draft does not consume the next revision");
            k.Check(store.SaveDraft(c, "ok", 0, 4) is { Saved: true }, "the same revision number is still usable afterwards");
            k.Check(store.SaveDraft(c, over, 1, 9) is { Saved: false, Reason: "stale-epoch" }, "stale epoch is reported before size so the UI resynchronizes first");
        }

        // 2. the limit follows an injected TextValidator (no separate hard-coded number)
        using (var area = new Area())
        using (var store = ChatStore.Open(new(area.Data) { TextValidator = t => !string.IsNullOrWhiteSpace(t) && t.Length <= 5 }))
        {
            string c = Conv(store);
            k.Check(store.SaveDraft(c, "12345", 0, 1).Saved && !store.SaveDraft(c, "123456", 0, 2).Saved, "default draft rule tracks TextValidator (5 chars)");
            k.Check(store.SaveDraft(c, "    ", 0, 2).Saved, "whitespace within the injected limit is allowed");
            k.Check(!store.SaveDraft(c, "         ", 0, 3).Saved, "whitespace beyond the injected limit is rejected");
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
