using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using NekoChat.Core.Storage;
using NekoChat.Core.V2;

namespace NekoChat.Tests;

internal static class V2StorageFenceChecks
{
    public static int Run()
    {
        int checks = 0;
        void Check(bool ok, string name) { checks++; if (!ok) throw new Exception("V2 storage fence: " + name); }
        using (var f = new Fixture())
        {
            var captured = f.Store.GetPeerVerificationContext(f.A.PeerId);
            Check(captured.Security == f.Security && captured.PeerSpkiBase64 == f.A.PublicSpkiBase64 && captured.LocalSpkiBase64 == f.B.PublicSpkiBase64, "atomic public key and revision read");
            var nonce = new V2NonceStore(); string n = nonce.Issue(captured.Security); var message = f.Message(n);
            Check(!nonce.TryConsume(message) && nonce.PendingCount == 1, "bound nonce cannot use unbound consumption");
            using (var second = ChatStore.Open(f.Options)) { second.SetPairingState(f.A.PeerId, PairingState.Revoked); second.SetPairingState(f.A.PeerId, PairingState.Paired); }
            Check(!nonce.TryConsume(message, f.Security) && nonce.PendingCount == 1, "revoke-restore rejects prior nonce under fresh security");
            Check(nonce.TryConsume(message, captured.Security) && f.Store.ReceiveV2Message(message, captured.Security, () => true, "10.0.0.1") is StaleSecurity,
                "even consuming old snapshot cannot bypass transactional ABA fence");
            Check(f.Count("Message") == 0 && f.Count("Peer WHERE lastSeenUtc IS NOT NULL") == 0, "stale message no data or endpoint evidence");
            n = nonce.Issue(f.Security); message = f.Message(n);
            Check(nonce.TryConsume(message, f.Security) && f.Store.ReceiveV2Message(message, f.Security, () => true, "10.0.0.1") is Stored, "fresh proof recovers after ABA");
            Check(f.Count("Message") == 1 && f.Count("Peer WHERE installEvidence='appVerified'") == 1, "committed message and verified evidence");
            Check(f.Store.ReceiveV2Message(message, f.Security, () => true) is Duplicate, "fresh guarded duplicate no extra row");
            int calls = 0;
            Check(f.Store.ReceiveV2Message(message, f.Security, () => ++calls == 1) is StaleSecurity && calls == 2 && f.Count("Message") == 1,
                "read-only duplicate result also gets final session guard");
            Check(f.Store.ReceiveV2Message(f.Message(n, message.MessageId, "different"), f.Security, () => true) is Conflict && f.Count("Message") == 1, "guarded conflict preserves original");
            f.Store.ClearConversation(f.Conversation);
            Check(f.Store.ReceiveV2Message(message, f.Security, () => true) is ClearedDuplicate && f.Count("Message") == 0, "guarded cleared duplicate does not resurrect");
            Check(f.Store.ReceiveV2Message(f.Message(n), f.Security with { PeerId = f.B.PeerId }, () => true) is StaleSecurity, "snapshot bound to correct sender");
        }
        {
            using var f = new Fixture(); var captured = f.Security; var nonces = new V2NonceStore(); string n = nonces.Issue(captured); var message = f.Message(n);
            f.Execute("UPDATE LocalIdentity SET credentialRef='temp'"); f.Execute("UPDATE LocalIdentity SET credentialRef='fixture'");
            Check(f.Security != captured && !nonces.TryConsume(message, f.Security), "credential restore still invalidates nonce");
            Check(f.Store.ReceiveV2Message(message, captured, () => true) is StaleSecurity && f.Count("Message") == 0, "credential restore rejected inside DB");
        }
        using (var f = new Fixture())
        {
            var captured = f.Security; using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            bool refused = false; try { f.Execute("UPDATE Peer SET publicKey=@key", key.ExportSubjectPublicKeyInfo()); } catch (SqliteException e) when (e.SqliteErrorCode == 19) { refused = true; }
            Check(refused && f.Security == captured, "pinned key mutation rejected with unchanged security state");
            Check(f.Store.ReceiveV2Message(f.Message(V2PacketWriter.NewClientNonce()), captured, () => true) is Stored, "failed pin attack does not invalidate legitimate captured trust");
        }
        foreach (string stage in new[] { "begin", "commit" })
        {
            using var f = new Fixture(); bool live = true; var nonces = new V2NonceStore(); string n = nonces.Issue(f.Security); var message = f.Message(n);
            Check(nonces.TryConsume(message, f.Security), stage + " signature and nonce passed");
            if (stage == "begin") f.Faults.Begin = op => { if (op == "receive") live = false; };
            else f.Faults.Before = op => { if (op == "receive") live = false; };
            Check(f.Store.ReceiveV2Message(message, f.Security, () => live, "10.0.0.1") is StaleSecurity && f.Count("Message") == 0 &&
                f.Count("Peer WHERE lastSeenUtc IS NOT NULL") == 0, stage + " session invalidation leaves no partial writes");
            Check(!nonces.TryConsume(message, f.Security), stage + " rollback never restores consumed nonce");
        }
        using (var f = new Fixture())
        {
            var captured = f.Security; var message = f.Message(V2PacketWriter.NewClientNonce());
            f.Faults.Before = op => { if (op == "receive") throw new IOException("injected"); };
            bool failed = false; try { f.Store.ReceiveV2Message(message, captured, () => true, "10.0.0.1"); } catch (IOException) { failed = true; }
            f.Faults.Before = null;
            Check(failed && f.Count("Message") == 0 && f.Count("Peer WHERE lastSeenUtc IS NOT NULL") == 0, "IO rollback no rows or evidence");
            Check(f.Store.ReceiveV2Message(message, captured, () => true) is Stored, "storage API recovers after IO failure under a current fence");
        }
        using (var f = new Fixture())
        {
            var prepared = f.Prepare(); var snapshot = f.Security; var ack = f.Ack(prepared); bool live = true;
            f.Faults.Before = op => { if (op == "complete-attempt") live = false; };
            Check(f.Store.CompleteV2Send(ack, snapshot, () => live) is Discarded { Reason: "stale-security" } && f.Count("Message") == 0 && f.Store.GetDraftState(f.Conversation)?.RawText == "outgoing",
                "signed ACK final cancellation cannot persist message or clear draft");
            f.Faults.Before = null;
            f.Store.SetPairingState(f.A.PeerId, PairingState.Revoked); f.Store.SetPairingState(f.A.PeerId, PairingState.Paired);
            Check(f.Store.CompleteV2Send(ack, snapshot, () => true) is Discarded { Reason: "stale-security" } && f.Count("Message") == 0, "outgoing ACK fenced after revoke-restore");
            Check(f.Store.CompleteV2Send(f.Ack(prepared), f.Security, () => true) is Confirmed { DraftCleared: true }, "reverified ACK under fresh snapshot can confirm and clear saved snapshot");
            var receipt = f.Receipt(prepared.MessageId); snapshot = f.Security; live = true;
            f.Faults.Before = op => { if (op == "apply-receipt") live = false; };
            Check(f.Store.ApplyV2Receipt(receipt, snapshot, () => live).Reason == "stale-security" && f.Count("Message WHERE remoteReadAt IS NOT NULL") == 0, "receipt final guard rolls back read status");
            f.Faults.Before = null;
            f.Store.SetPairingState(f.A.PeerId, PairingState.Revoked); f.Store.SetPairingState(f.A.PeerId, PairingState.Paired);
            Check(f.Store.ApplyV2Receipt(receipt, snapshot, () => true).Reason == "stale-security", "receipt rejects trust ABA");
            Check(f.Store.ApplyV2Receipt(f.Receipt(prepared.MessageId), f.Security, () => true).Changed && f.Count("Message WHERE remoteReadAt IS NOT NULL") == 1, "fresh receipt applies one exact message");
            Check(f.Store.ApplyV2Receipt(receipt, f.Security, () => true).Reason == "unchanged", "repeated read idempotent");
            Check(f.Store.CompleteV2Send(f.ReceiptAck(prepared.MessageId), f.Security, () => true) is Discarded { Reason: "invalid-ack-kind" }, "receipt ACK cannot confirm message send");
        }
        using (var f = new Fixture())
        {
            var prepared = f.Prepare();
            Check(f.Store.ApplyV2Receipt(f.Receipt(prepared.MessageId), f.Security, () => true).Reason == "pending-local-confirmation", "early read remains transient pending not successful discard");
            var incoming = f.Message(V2PacketWriter.NewClientNonce()); var stored = (Stored)f.Store.ReceiveV2Message(incoming, f.Security, () => true);
            Check(!f.Store.GetReadReceiptPolicy().Enabled, "wire integration leaves privacy default off");
            f.Store.SetReadReceiptPolicy(true);
            f.Store.MarkVisibleRead(f.Conversation, new[] { stored.LocalSeq }, new(f.Conversation, true, true, false, true));
            var pending = f.Store.ListPendingReadReceipts().Single(); var ack = f.ReceiptAck(pending.MessageId); var snapshot = f.Security; bool live = true;
            f.Faults.Before = op => { if (op == "complete-receipt") live = false; };
            Check(!f.Store.CompleteV2ReadReceipt(pending, ack, snapshot, () => live) && f.Count("ReceiptOutbox") == 1, "final receipt ACK guard rollback preserves queue");
            f.Faults.Before = null;
            f.Store.SetPairingState(f.A.PeerId, PairingState.Revoked); f.Store.SetPairingState(f.A.PeerId, PairingState.Paired);
            Check(!f.Store.CompleteV2ReadReceipt(pending, ack, snapshot, () => true) && f.Count("ReceiptOutbox") == 1, "old receipt ACK cannot delete queue after ABA");
            Check(!f.Store.CompleteV2ReadReceipt(pending, f.ReceiptAck(V2PacketWriter.NewMessageId()), f.Security, () => true) && f.Count("ReceiptOutbox") == 1, "receipt ACK binds exact queued message");
            Check(f.Store.CompleteV2ReadReceipt(pending, f.ReceiptAck(pending.MessageId), f.Security, () => true) && f.Count("ReceiptOutbox") == 0, "fresh receipt ACK removes exact queue row");
        }
        using (var f = new Fixture())
        {
            using var second = ChatStore.Open(f.Options); var snapshot = f.Security; var message = f.Message(V2PacketWriter.NewClientNonce());
            using var gate = new ManualResetEventSlim();
            var workers = new[] { f.Store, second }.Select(s => Task.Factory.StartNew(() => {
                if (!gate.Wait(TimeSpan.FromSeconds(8))) throw new TimeoutException(); return s.ReceiveV2Message(message, snapshot, () => true);
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
            gate.Set(); var results = Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(12)).GetAwaiter().GetResult();
            Check(results.Count(r => r is Stored) == 1 && results.Count(r => r is Duplicate) == 1 && f.Count("Message") == 1, "two guarded SQLite handles converge to one row");
            var nonces = new V2NonceStore(); string n = nonces.Issue(f.Security); var bound = f.Message(n); nonces.Clear(); nonces.Clear();
            Check(nonces.PendingCount == 0 && !nonces.TryConsume(bound, f.Security), "lifecycle reset invalidates old challenges idempotently");
            n = nonces.Issue(f.Security); Check(nonces.TryConsume(f.Message(n), f.Security), "new challenge works after lifecycle reset");
        }
        Console.WriteLine($"Product V2 storage fences: {checks} real signature, nonce epoch and SQLite commit checks."); return checks;
    }
    private sealed class Fixture : IDisposable
    {
        internal string Data { get; } = Path.Combine(Path.GetTempPath(), "neko-v2-fence-" + Guid.NewGuid().ToString("N"));
        internal V2PacketWriter A { get; } = new(ECDsa.Create(ECCurve.NamedCurves.nistP256), 16384);
        internal V2PacketWriter B { get; } = new(ECDsa.Create(ECCurve.NamedCurves.nistP256), 16384);
        internal Faults Faults { get; } = new();
        internal ChatStoreOptions Options => new(Data) { TextValidator = t => V2Text.Check(t, 16384) == V2TextProblem.None, Faults = Faults };
        internal ChatStore Store { get; }
        internal string Conversation { get; }
        internal PeerSecuritySnapshot Security => Store.GetPeerSecuritySnapshot(A.PeerId);
        private readonly V2Verifier _verifier = new(16384);
        internal Fixture()
        {
            Directory.CreateDirectory(Data); Store = ChatStore.Open(Options);
            Store.EnsureLocalIdentity(B.PeerId, Convert.FromBase64String(B.PublicSpkiBase64), "fixture");
            Conversation = ((PeerRegistered)Store.RegisterPeer(A.PeerId, PairingState.Paired, Convert.FromBase64String(A.PublicSpkiBase64))).Conversation.ConversationId;
        }
        internal V2VerifiedMessage Message(string nonce, string? id = null, string text = "incoming") => _verifier.VerifyMessage(
            A.Message(id ?? V2PacketWriter.NewMessageId(), B.PeerId, nonce, text), new(B.PeerId, new("paired", A.PublicSpkiBase64))).Value!;
        internal V2VerifiedReceipt Receipt(string id) => _verifier.VerifyReceipt(A.Receipt(id, B.PeerId, V2PacketWriter.NewClientNonce()), new(B.PeerId, new("paired", A.PublicSpkiBase64))).Value!;
        internal PreparedSend Prepare()
        {
            var draft = Store.GetDraftState(Conversation)!; Store.SaveDraft(Conversation, "outgoing", draft.Epoch, draft.Revision + 1);
            var attempt = (AttemptStarted)Store.BeginManualSend(Conversation, "outgoing", draft.Epoch, draft.Revision + 1);
            return Store.GetPreparedSend(attempt.MessageId)!;
        }
        internal V2VerifiedAck Ack(PreparedSend p) => _verifier.VerifyMessageAck(A.MessageAck("stored", p.MessageId, B.PeerId, p.FingerprintHex),
            new(p.MessageId, B.PeerId, A.PublicSpkiBase64, p.FingerprintHex)).Value!;
        internal V2VerifiedAck ReceiptAck(string id) => _verifier.VerifyReceiptAck(A.ReceiptAck("ok", id, B.PeerId), new(id, B.PeerId, A.PublicSpkiBase64)).Value!;
        internal long Count(string table)
        {
            using var c = new SqliteConnection("Data Source=" + Path.Combine(Data, "nekochat.db") + ";Mode=ReadOnly;Pooling=False"); c.Open();
            using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT count(*) FROM " + table; return Convert.ToInt64(cmd.ExecuteScalar());
        }
        internal void Execute(string sql, byte[]? key = null)
        {
            using var c = new SqliteConnection("Data Source=" + Path.Combine(Data, "nekochat.db") + ";Pooling=False"); c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = sql;
            if (key is not null) cmd.Parameters.AddWithValue("@key", key); cmd.ExecuteNonQuery();
        }
        public void Dispose() { Store.Dispose(); A.Dispose(); B.Dispose(); Directory.Delete(Data, true); }
    }
    private sealed class Faults : IStoreFaults
    {
        internal Action<string>? Begin;
        internal Action<string>? Before;
        public void AfterBegin(string op) => Begin?.Invoke(op);
        public void BeforeCommit(string op) => Before?.Invoke(op);
        public void AfterCommit(string op) { }
    }
}
