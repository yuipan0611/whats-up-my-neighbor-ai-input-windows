using System.Net;
using System.Security.Cryptography;
using NekoChat.Core;
using NekoChat.Core.Storage;
using NekoChat.Core.V2;

namespace NekoChat.Tests;

internal static class ReceiptServiceChecks
{
    public static async Task<int> Run()
    {
        int checks = 0;
        void Check(bool ok, string label) { checks++; if (!ok) throw new Exception("Receipt worker: " + label); }
        await using var f = new Fixture(); var clock = new Clock(); await using var service = new ReadReceiptService(f.A, f.ClientA, f.ListenerB.Port, clock);
        string first = await f.Send(); f.Read(first); service.Start(); await Until(() => Volatile.Read(ref service.Passes) > 0);
        Check(!f.A.GetReadReceiptPolicy().Enabled && f.A.ListPendingReadReceipts().Count == 0 && Volatile.Read(ref service.Attempts) == 0 && f.B.ListMessages(f.ConvB).Items.Single().RemoteReadAt is null,
            "defaultOFF local visible-read queues/sends no receipt through an actual running service");
        f.A.SetReadReceiptPolicy(true); string second = await f.Send(); f.Read(second); service.Pulse();
        await Until(() => f.B.ListMessages(f.ConvB).Items.Single(m => m.MessageId == second).RemoteReadAt is not null && f.A.ListPendingReadReceipts().Count == 0);
        Check(Volatile.Read(ref service.Attempts) == 1 && f.B.ListMessages(f.ConvB).Items.Single(m => m.MessageId == first).RemoteReadAt is null,
            "real signed hello/receipt/ACK dispatch confirms exact queued message and never backfills prior local reads");
        string third = await f.Send(); f.Read(third); await PulseAndWait(service);
        Check(Volatile.Read(ref service.Attempts) == 1 && f.A.ListPendingReadReceipts().Count == 1, "same peer is bounded to one receipt per15s and retains pending queue");
        clock.Advance(14.999); await PulseAndWait(service); Check(Volatile.Read(ref service.Attempts) == 1, "monotonic receipt pacing cannot expire early");
        clock.Advance(.001); service.Pulse(); await Until(() => f.A.ListPendingReadReceipts().Count == 0);
        Check(f.B.ListMessages(f.ConvB).Items.Single(m => m.MessageId == third).RemoteReadAt is not null, "exact15s fresh nonce dispatch updates third message only");
        string fourth = await f.Send(); f.Read(fourth); f.A.SetReadReceiptPolicy(false); clock.Advance(30); await PulseAndWait(service);
        Check(Volatile.Read(ref service.Attempts) == 2 && f.A.ListPendingReadReceipts().Count == 0 && f.B.ListMessages(f.ConvB).Items.Single(m => m.MessageId == fourth).RemoteReadAt is null,
            "turning privacyOFF atomically removes unsent queue and worker cannot send captured old policy");
        f.A.SetReadReceiptPolicy(true); await PulseAndWait(service); Check(Volatile.Read(ref service.Attempts) == 2, "re-enabling privacy policy never backfills read fourth message");
        string fifth = await f.Send(); f.Read(fifth); f.A.SetPairingState(f.WriterB.PeerId, PairingState.Suspended); clock.Advance(30); await PulseAndWait(service);
        Check(Volatile.Read(ref service.Attempts) == 2 && f.B.ListMessages(f.ConvB).Items.Single(m => m.MessageId == fifth).RemoteReadAt is null,
            "suspended trust cannot dispatch pending metadata even with read receipts enabled");
        f.A.SetPairingState(f.WriterB.PeerId, PairingState.Paired); service.ResetAfterResume(); await Until(() => f.A.ListPendingReadReceipts().Count == 0);
        Check(f.B.ListMessages(f.ConvB).Items.Single(m => m.MessageId == fifth).RemoteReadAt is not null && f.A.GetUnreadCount(f.ConvA) == 0,
            "explicit fresh trust and resume generation can dispatch still-allowed queue; local reads remain independent");
        LegacyDeliveryNotice? notice = null; f.ListenerA.LegacyMessageStored += n => notice = n; f.ListenerA.LegacyMessageStored += _ => throw new InvalidOperationException("fixture callback");
        Check(await DirectLanMessaging.SendDirectAsync("127.0.0.1", "synthetic private legacy body", f.ListenerA.Port) && notice is { SourceIp: "127.0.0.1", LegacySeq: > 0 } &&
            notice.Scope == new LegacyHistoryScope(null, "127.0.0.1") && typeof(LegacyDeliveryNotice).GetProperties().All(p => p.Name is not ("Text" or "Body" or "RawText")),
            "actual legacy stored event has immutable arrival scope and no body; throwing subscriber cannot turn committed receipt into wire failure");
        await service.DisposeAsync(); Check(f.A.GetInboxSnapshot().Entries.Count == 2 && f.A.GetReadReceiptPolicy().Enabled, "drained receipt service leaves borrowed store/client alive");
        Console.WriteLine($"Receipt worker: {checks} actual TCP/signature/SQLite privacy, pacing, trust and legacy notice checks."); return checks;
    }
    private static async Task PulseAndWait(ReadReceiptService service)
    { long before = Volatile.Read(ref service.Passes); service.Pulse(); await Until(() => Volatile.Read(ref service.Passes) > before); }
    private static async Task Until(Func<bool> predicate)
    { for (int i = 0; i < 500; i++) { if (predicate()) return; await Task.Delay(10); } throw new TimeoutException("receipt worker fixture"); }
    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        internal void Advance(double seconds) => Interlocked.Add(ref _ticks, TimeSpan.FromSeconds(seconds).Ticks);
    }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "neko-receipt-worker-" + Guid.NewGuid().ToString("N"));
        internal V2PacketWriter WriterA { get; } = new(ECDsa.Create(ECCurve.NamedCurves.nistP256), 16384);
        internal V2PacketWriter WriterB { get; } = new(ECDsa.Create(ECCurve.NamedCurves.nistP256), 16384);
        internal ChatStore A { get; }
        internal ChatStore B { get; }
        internal string ConvA { get; }
        internal string ConvB { get; }
        internal DirectLanListener ListenerA { get; }
        internal V2TcpListener ListenerB { get; }
        internal V2LanClient ClientA { get; }
        private readonly V2LanClient _clientB;
        internal Fixture()
        {
            Directory.CreateDirectory(_root); A = ChatStore.Open(new(Path.Combine(_root, "a"))); B = ChatStore.Open(new(Path.Combine(_root, "b")));
            A.EnsureLocalIdentity(WriterA.PeerId, Convert.FromBase64String(WriterA.PublicSpkiBase64), "fixture"); B.EnsureLocalIdentity(WriterB.PeerId, Convert.FromBase64String(WriterB.PublicSpkiBase64), "fixture");
            ConvA = ((PeerRegistered)A.RegisterPeer(WriterB.PeerId, PairingState.Paired, Convert.FromBase64String(WriterB.PublicSpkiBase64))).Conversation.ConversationId;
            ConvB = ((PeerRegistered)B.RegisterPeer(WriterA.PeerId, PairingState.Paired, Convert.FromBase64String(WriterA.PublicSpkiBase64))).Conversation.ConversationId;
            var options = new V2ListenerOptions { BindAddress = IPAddress.Loopback, Port = 0, RequestsPerSourcePerMinute = 1000 };
            ListenerA = new(new V2Receiver(A, WriterA), options, A); ListenerB = new(new V2Receiver(B, WriterB), options); ListenerA.Start(); ListenerB.Start();
            ClientA = new(A, WriterA); _clientB = new(B, WriterB);
        }
        internal async Task<string> Send()
        {
            var draft = B.GetDraftState(ConvB)!; string text = "fixture receipt " + Guid.NewGuid(); B.SaveDraft(ConvB, text, draft.Epoch, draft.Revision + 1);
            var started = (AttemptStarted)B.BeginManualSend(ConvB, text, draft.Epoch, draft.Revision + 1); var result = await _clientB.SendPreparedAsync(B.GetPreparedSend(started.MessageId)!, "127.0.0.1", ListenerA.Port, () => true);
            if (result.Code != "ok") throw new Exception("fixture initial signed delivery failed: " + result.Code); return started.MessageId;
        }
        internal void Read(string id) { var message = A.ListMessages(ConvA).Items.Single(m => m.MessageId == id); A.MarkVisibleRead(ConvA, [message.LocalSeq], new(ConvA, true, true, false, true)); }
        public async ValueTask DisposeAsync() { await ListenerA.DisposeAsync(); await ListenerB.DisposeAsync(); ClientA.Dispose(); _clientB.Dispose(); A.Dispose(); B.Dispose(); WriterA.Dispose(); WriterB.Dispose(); Directory.Delete(_root, true); }
    }
}
