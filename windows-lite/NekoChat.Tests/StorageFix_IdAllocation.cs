using NekoChat.Core.Storage;
using static NekoChat.Tests.StorageFixKit;

namespace NekoChat.Tests;

/// <summary>Review item E: message-id allocation failures are typed results with a full rollback, never a stray exception.</summary>
internal static class StorageFix_IdAllocation
{
    internal static int Run()
    {
        var k = new Counter();
        using var area = new Area();
        string mode = "real"; int calls = 0; string fixedId = Guid.NewGuid().ToString("D");
        string Generate()
        {
            if (mode == "real") return Guid.NewGuid().ToString("D");
            calls++;
            return mode == "same" ? fixedId : "not-a-uuid";
        }
        using var store = ChatStore.Open(new(area.Data) { NewId = Generate });
        string db = store.DatabasePath;
        store.EnsureLocalIdentity("ME_A");
        string conv = ((PeerRegistered)store.RegisterPeer("P1", PairingState.Paired)).Conversation.ConversationId;
        store.SaveDraft(conv, "first", 0, 1);

        mode = "same";
        var first = store.BeginManualSend(conv, "first", 0, 1) as AttemptStarted;
        k.Check(first is { MessageId: var id } && id == fixedId, "first attempt takes the id");
        store.CompleteAttempt(fixedId, new UnconfirmedOutcome());
        string attemptsBefore = Text(db, "SELECT group_concat(messageId||attemptState||draftRevision||rawSnapshot) FROM ManualSendAttempt");

        // The generator now keeps returning an id that is already an attempt id: bounded retries, typed failure, nothing written.
        calls = 0;
        var collision = store.BeginManualSend(conv, "second text", 0, 2);
        k.Check(collision is InvalidSend { Reason: "id-allocation-failed" }, "id collision → InvalidSend(id-allocation-failed)");
        k.Check(calls == 3, "allocation retries are bounded (3 calls, saw " + calls + ")");
        k.Check(Text(db, "SELECT group_concat(messageId||attemptState||draftRevision||rawSnapshot) FROM ManualSendAttempt") == attemptsBefore, "the existing attempt is neither overwritten nor duplicated");
        k.Check(Number(db, "SELECT draftRevision FROM Conversation WHERE id=@c", ("@c", conv)) == 1 && Text(db, "SELECT rawText FROM Draft WHERE conversationId=@c", ("@c", conv)) == "first", "the draft binding done earlier in the transaction was rolled back");
        k.Check(Number(db, "SELECT count(*) FROM Message") == 0, "no history written");

        // A malformed generator is also a typed result with full rollback.
        mode = "bad";
        var bad = store.BeginManualSend(conv, "second text", 0, 2);
        k.Check(bad is InvalidSend { Reason: "id-generator-invalid" } && Number(db, "SELECT draftRevision FROM Conversation WHERE id=@c", ("@c", conv)) == 1, "malformed generated id → InvalidSend(id-generator-invalid), rolled back");

        // Retry path (a different text starts a new attempt) behaves the same way.
        mode = "same";
        var retry = store.BeginManualRetry(fixedId, "changed text", conv, 0, 2);
        k.Check(retry is InvalidSend { Reason: "id-allocation-failed" } && Number(db, "SELECT draftRevision FROM Conversation WHERE id=@c", ("@c", conv)) == 1, "retry with new text: same typed failure and rollback");

        // And it recovers as soon as the generator does.
        mode = "real";
        k.Check(store.BeginManualSend(conv, "second text", 0, 2) is AttemptStarted { ReusedId: false }, "normal allocation works again");
        return k.Count;
    }
}
