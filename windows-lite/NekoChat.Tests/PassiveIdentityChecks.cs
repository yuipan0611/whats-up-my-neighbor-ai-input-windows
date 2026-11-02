using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using NekoChat.Core.Presence;
using NekoChat.Core.Roster;
using NekoChat.Core.Storage;
using NekoChat.Core.V2;

namespace NekoChat.Tests;

internal static class PassiveIdentityChecks
{
    public static async Task<int> Run()
    {
        int checks = 0;
        void Check(bool ok, string name) { checks++; if (!ok) throw new Exception("Passive identity: " + name); }
        using (var f = new Fixture())
        {
            var activity = new List<V2VerifiedActivity>(); f.Receiver.VerifiedActivity += activity.Add;
            var first = f.Send("10.0.0.1");
            Check(first.StatusCode == 200 && activity.Count == 1 && activity[0].Current, "verified nonce and committed message publish one current activity");
            Check(activity[0].Security == f.Store.GetPeerSecuritySnapshot(f.A.PeerId) && activity[0].SourceEndpoint == "10.0.0.1" && activity[0].ObservedUtc == f.Clock.GetUtcNow().UtcDateTime,
                "activity carries exact public security epoch, source and origin time");
            Check(typeof(V2VerifiedActivity).GetConstructors().Length == 0 && typeof(V2VerifiedActivity).GetProperties().All(p => !p.Name.Contains("Text") && !p.Name.Contains("Body")),
                "activity cannot be constructed publicly and exposes no text or body");
            using var cloneKey = ECDsa.Create(); cloneKey.ImportParameters(f.PrivateClone);
            using var clone = new V2PacketWriter(cloneKey, 16384);
            Check(clone.PeerId == f.A.PeerId, "second in-memory signer is a true copy of the synthetic identity key");
            var second = f.Send("10.0.0.2", writer: clone);
            Check(second.StatusCode == 403 && Code(second) == "unpaired_sender" && !System.Text.Encoding.UTF8.GetString(second.Body).Contains("identity_conflict"),
                "second authenticated source gets generic wire rejection");
            Check(f.Store.GetPeerSecuritySnapshot(f.A.PeerId).Pairing == PairingState.Suspended && f.Count("Message") == 1 && activity.Count == 1 && !activity[0].Current,
                "clone suspends current trust, stores no offending message and invalidates prior activity");
            Check(f.Diagnostics.Contains("identity_conflict") && f.Count("ReceiptOutbox") == 0 && f.Count("Message WHERE remoteReadAt IS NOT NULL OR localReadAt IS NOT NULL") == 0,
                "fixed local conflict diagnostic does not mark messages read or queue receipts");
            f.Store.SetPairingState(f.A.PeerId, PairingState.Paired);
            Check(f.Send("10.0.0.2").StatusCode == 200 && f.Count("Message") == 2, "explicit trust epoch change permits fresh proof without historical-source reseeding");
        }
        foreach (string attack in new[] { "bad-signature", "stale-nonce", "hello-claim" })
        {
            using var f = new Fixture(); f.Send("10.0.0.1"); byte[] body = f.Packet();
            if (attack == "bad-signature") { var o = JsonNode.Parse(body)!.AsObject(); var sig = Convert.FromBase64String(o["sig"]!.GetValue<string>()); sig[0] ^= 1; o["sig"] = Convert.ToBase64String(sig); body = JsonSerializer.SerializeToUtf8Bytes(o); }
            if (attack == "stale-nonce") body = f.A.Message(V2PacketWriter.NewMessageId(), f.B.PeerId, V2PacketWriter.NewClientNonce(), "not accepted");
            var result = attack == "hello-claim" ? f.Receiver.Handle("/nekochat/v2/hello", f.A.HelloRequest(V2PacketWriter.NewClientNonce()), "10.0.0.2", () => true) :
                f.Receiver.Handle("/nekochat/v2/message", body, "10.0.0.2", () => true);
            Check((attack == "hello-claim" ? result.StatusCode == 200 : result.StatusCode != 200) && f.Store.GetPeerSecuritySnapshot(f.A.PeerId).Pairing == PairingState.Paired && f.Count("Message") == 1,
                attack + " from another source cannot suspend a pinned peer or add a message");
        }
        foreach (double elapsed in new[] { 59.999, 60.0 })
        {
            using var f = new Fixture(); f.Send("10.0.0.1"); f.Clock.Advance(elapsed); f.Clock.WallOffset = TimeSpan.FromDays(-100);
            var result = f.Send("10.0.0.2");
            Check(elapsed < 60 ? result.StatusCode == 403 : result.StatusCode == 200, "conflict window is monotonic and exact at " + elapsed);
        }
        using (var f = new Fixture())
        {
            V2VerifiedActivity? old = null; f.Receiver.VerifiedActivity += a => old ??= a; f.Send("10.0.0.1");
            f.Receiver.ResetReplayProtection();
            Check(!old!.Current && f.Send("10.0.0.2").StatusCode == 200, "runtime reset invalidates tickets and starts with fresh source observations");
            f.Receiver.SetReady(false); f.Receiver.ResetReplayProtection();
            Check(f.Send("10.0.0.2").StatusCode == 503, "resume reset cannot make a not-ready receiver ready");
            f.Receiver.SetReady(true); f.Receiver.VerifiedActivity += _ => throw new InvalidOperationException("private text");
            Check(f.Send("10.0.0.2").StatusCode == 200 && f.Diagnostics.Contains("activity_callback_failed") && !f.Diagnostics.Contains("private text"),
                "subscriber failure cannot turn a committed ACK into failure or leak exception text");
        }
        using (var f = new Fixture(capacity: 1))
        {
            f.Send("10.0.0.1"); using var other = new V2PacketWriter(ECDsa.Create(ECCurve.NamedCurves.nistP256), 16384);
            f.Store.RegisterPeer(other.PeerId, PairingState.Paired, Convert.FromBase64String(other.PublicSpkiBase64));
            Check(f.Send("10.0.0.3", writer: other).StatusCode == 429 && f.Monitor.TrackedPeers == 1 && f.Count("Message") == 1 && f.Store.GetPeerSecuritySnapshot(other.PeerId).Pairing == PairingState.Paired,
                "full fresh identity table fails bounded without evicting live proof or suspending another peer");
            f.Clock.Advance(120);
            Check(f.Send("10.0.0.3", writer: other).StatusCode == 200 && f.Monitor.TrackedPeers == 1, "exact retention expiry frees a bounded identity slot");
        }
        using (var f = new Fixture())
        {
            string row = f.AddRow(); f.Send("10.0.0.1"); var controller = new PresenceController(f.Clock); controller.UpdateTargets(f.Store.GetPresenceTargets());
            var target = f.Store.GetPresenceTarget(row)!; var job = controller.ObservePassive(row, f.A.PeerId, target.KeySha256!, "10.0.0.1").Write!; int guards = 0;
            var result = f.Store.ApplyRosterPresenceEvidence(job, () => ++guards < 3);
            Check(result.Reason == "stale-target" && guards == 3 && !controller.CompletePersistence(job, result) && controller.GetSnapshot(row).Presence == PresenceStatus.Unknown,
                "throttled no-write evidence still checks final lifetime before it can publish Online");
        }
        using (var f = new Fixture())
        {
            var activity = new List<V2VerifiedActivity>(); f.Receiver.VerifiedActivity += activity.Add; string id = V2PacketWriter.NewMessageId();
            f.Send("10.0.0.1", id); f.Clock.Advance(1); f.Send("10.0.0.1", id);
            Check(activity.Count == 2 && f.Count("Message") == 1 && !activity[0].Current && activity[1].Current,
                "fresh authenticated duplicate is presence evidence without a second message");
            f.Store.ClearConversation(f.Conversation); f.Clock.Advance(1); var result = f.Send("10.0.0.1", id);
            Check(result.StatusCode == 200 && f.Count("Message") == 0 && activity.Count == 3, "fresh cleared duplicate proves activity without resurrecting erased content");
        }
        await using (var f = new Fixture())
        {
            var activities = new ConcurrentQueue<V2VerifiedActivity>(); f.Receiver.VerifiedActivity += activities.Enqueue;
            await using var listener = new V2TcpListener(f.Receiver, new V2ListenerOptions { BindAddress = IPAddress.Loopback, Port = 0 }); listener.Start();
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(5) };
            async Task<byte[]> Post(string path, byte[] data)
            {
                using var content = new ByteArrayContent(data); content.Headers.ContentType = new("application/json");
                using var response = await http.PostAsync($"http://127.0.0.1:{listener.Port}{path}", content); response.EnsureSuccessStatusCode(); return await response.Content.ReadAsByteArrayAsync();
            }
            string cn = V2PacketWriter.NewClientNonce(); byte[] body = await Post("/nekochat/v2/hello", f.A.HelloRequest(cn));
            var proof = new V2Verifier(16384).VerifyHello(body, new(cn, new("paired", f.B.PublicSpkiBase64), new[] { "msg.sign" })); string id = V2PacketWriter.NewMessageId();
            byte[] packet = f.A.Message(id, f.B.PeerId, proof.Value!.Nonce, "TCP activity"); byte[] ack = await Post("/nekochat/v2/message", packet);
            var verified = new V2Verifier(16384).VerifyMessage(packet, new(f.B.PeerId, new("paired", f.A.PublicSpkiBase64))).Value!;
            Check(new V2Verifier(16384).VerifyMessageAck(ack, new(id, f.A.PeerId, f.B.PublicSpkiBase64, verified.ContentFingerprint)).Success && activities.TryPeek(out var a) && a.SourceEndpoint == "127.0.0.1" && a.Current && f.Count("Message") == 1,
                "actual loopback HTTP publishes activity after durable storage and returns a verifiable signed ACK");
            Check(f.Service.PendingActivities == 0, "loopback-only transport proof is not relabeled as a roster LAN address");
        }
        using (var f = new Fixture())
        {
            byte[] first = f.Packet(); using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            var verified = new V2Verifier(16384).VerifyMessage(first, new(f.B.PeerId, new("paired", f.A.PublicSpkiBase64))).Value!;
            var firstObservation = f.Monitor.Observe(verified, f.Store.GetPeerVerificationContext(f.A.PeerId), "10.0.0.1").Proof!;
            using var secondStore = ChatStore.Open(f.Options); var secondReceiver = new V2Receiver(secondStore, f.B, f.Clock, f.Monitor);
            byte[] secondHello = secondReceiver.Handle("/nekochat/v2/hello", f.A.HelloRequest(V2PacketWriter.NewClientNonce()), "10.0.0.2", () => true).Body;
            using var hello = JsonDocument.Parse(secondHello); byte[] second = f.A.Message(V2PacketWriter.NewMessageId(), f.B.PeerId, hello.RootElement.GetProperty("nonce").GetString()!, "blocked clone");
            f.Faults.Before = op => { if (op == "receive") { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(6))) throw new TimeoutException("fixture commit gate"); } };
            Task<V2HttpResponse> one = Task.Run(() => f.Receiver.Handle("/nekochat/v2/message", first, "10.0.0.1", () => true));
            await Until(() => entered.IsSet);
            // Same receiver enters its monitor only after capturing the pinned context. Use a second DB handle so that read can proceed while the first handle owns its write mutex.
            Task<V2HttpResponse> two = Task.Run(() => secondReceiver.Handle("/nekochat/v2/message", second, "10.0.0.2", () => true));
            try
            {
                await Until(() => !firstObservation.Allowed);
            }
            finally { f.Faults.Before = null; release.Set(); }
            var replies = await Task.WhenAll(one, two).WaitAsync(TimeSpan.FromSeconds(8));
            Check(replies.All(r => r.StatusCode != 200) && f.Count("Message") == 0 && f.Count("Peer WHERE lastSeenUtc IS NOT NULL") == 0 && secondStore.GetPeerSecuritySnapshot(f.A.PeerId).Pairing == PairingState.Suspended,
                "concurrent second source invalidates the first transaction before suspension can acquire SQLite writer");
        }
        await using (var f = new Fixture())
        {
            string row = f.AddRow(); var service = f.Service; service.Start(); await Until(() => service.GetSnapshot(row) is not null);
            f.Send("10.0.0.1"); await Until(() => service.GetSnapshot(row)?.Presence == PresenceStatus.Online);
            Check(service.GetSnapshot(row) is { Installed: InstalledStatus.ConfirmedInstalled, EndpointResponds: true } && f.Connections == 0,
                "hidden service accepts real verified incoming activity through SQLite with zero outgoing probes");
            Check(!f.Store.GetReadReceiptPolicy().Enabled && f.Count("ReceiptOutbox") == 0 && f.Count("Message WHERE localReadAt IS NOT NULL") == 0,
                "hidden passive presence respects default-off remote receipts and local visible-read semantics");
            f.Clock.Advance(1); var discarded = f.Send("10.0.0.1", receipt: true); await Until(() => service.GetSnapshot(row)?.LastSeenUtc == f.Clock.GetUtcNow().UtcDateTime);
            Check(discarded.StatusCode == 200 && f.Count("Message") == 1 && f.Count("Message WHERE remoteReadAt IS NOT NULL") == 0,
                "authentic discarded receipt proves activity without inventing delivery/read state");
            f.Send("10.0.0.2"); await Until(() => service.GetSnapshot(row)?.Presence == PresenceStatus.Unknown);
            Check(f.Store.GetPeerSecuritySnapshot(f.A.PeerId).Pairing == PairingState.Suspended && service.GetSnapshot(row)?.Installed == InstalledStatus.ConfirmedInstalled,
                "hidden conflict refresh removes transient Online while retaining historical installed memo");
        }
        await using (var f = new Fixture())
        {
            string row = f.AddRow(); DateTime origin = f.Clock.GetUtcNow().UtcDateTime; f.Send("10.0.0.1");
            Check(f.Service.PendingActivities == 1, "activity is retained in bounded queue before worker starts");
            f.Clock.Advance(30); f.Service.SetView(false, [], row); f.Service.Start(); await Until(() => f.Service.GetSnapshot(row)?.Presence == PresenceStatus.Online);
            Check(f.Service.GetSnapshot(row)?.LastSeenUtc == origin && f.Store.GetPresenceTarget(row)?.LastSeenUtc == origin && f.Connections == 0,
                "queued activity preserves original UTC in memory and durable evidence");
            f.Clock.Advance(15);
            Check(f.Service.GetSnapshot(row)?.Presence == PresenceStatus.Offline, "current-row45s TTL expires from packet origin, not delayed dequeue");
        }
        await using (var f = new Fixture())
        {
            string row = f.AddRow(); var activities = new List<V2VerifiedActivity>(); f.Receiver.VerifiedActivity += activities.Add;
            for (int i = 0; i < 30; i++) { f.Clock.Advance(.01); f.Send("10.0.0.1", receipt: true); }
            Check(f.Service.PendingActivities == 1 && activities.Count == 30 && !activities[0].Current && activities[^1].Current,
                "bursts coalesce by peer and superseded evidence cannot be replayed");
            f.Service.ObserveActivity(activities[0]); f.Service.ResetAfterResume(); f.Service.Start(); await Until(() => f.Service.GetSnapshot(row) is not null);
            Check(f.Service.PendingActivities == 0 && f.Service.GetSnapshot(row)?.Presence == PresenceStatus.Unknown && !activities[^1].Current,
                "resume clears queued proof and receiver tickets without reviving old evidence");
            f.Send("10.0.0.1"); await Until(() => f.Service.GetSnapshot(row)?.Presence == PresenceStatus.Online);
            Check(f.Connections == 0, "fresh incoming proof recovers hidden presence after resume");
            await f.Service.DisposeAsync(); f.Send("10.0.0.1");
            Check(f.Service.PendingActivities == 0 && f.Service.ActiveOperations == 0 && f.Store.GetPresenceTargets().Count == 1,
                "disposed service unsubscribes and drains without disposing borrowed store or receiver");
        }
        await using (var f = new Fixture())
        {
            string row = f.AddRow(); f.Send("10.0.0.1"); f.Store.SetPairingState(f.A.PeerId, PairingState.Revoked); f.Store.SetPairingState(f.A.PeerId, PairingState.Paired);
            f.Service.Start(); await Until(() => f.Service.GetSnapshot(row) is not null); await Until(() => f.Service.ActiveOperations == 0);
            Check(f.Service.GetSnapshot(row)?.Presence == PresenceStatus.Unknown, "queued proof cannot cross durable revoke-restore epoch");
            f.Send("10.0.0.1"); await Until(() => f.Service.GetSnapshot(row)?.Presence == PresenceStatus.Online);
            Check(f.Connections == 0, "fresh epoch proof recovers without automatic trust change");
        }
        await using (var f = new Fixture())
        {
            string row = f.AddRow(); f.Service.Start(); await Until(() => f.Service.GetSnapshot(row) is not null);
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            f.Faults.Begin = op => { if (op == "roster-presence") { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(6))) throw new TimeoutException("fixture presence gate"); } };
            try
            {
                f.Send("10.0.0.1"); await Until(() => entered.IsSet); f.Service.SetView(true, [], null); f.Service.SetView(false, [], null);
                Check(f.Service.ActiveOperations == 1, "hidden passive SQL still occupies one shared physical slot during view cycle");
            }
            finally { f.Faults.Begin = null; release.Set(); }
            await Until(() => f.Service.GetSnapshot(row)?.Presence == PresenceStatus.Online);
            Check(f.Connections == 0 && f.Service.ActiveOperations <= 4, "view generation change does not revoke passive security generation");
        }
        await using (var f = new Fixture())
        {
            string one = f.AddRow("10.0.0.1"), two = f.AddRow("10.0.0.2");
            await using var server = new V2TcpChecks.ScriptedServer((_, body) => { using var p = JsonDocument.Parse(body); return Task.FromResult<V2HttpResponse?>(new(200, f.A.HelloReply(p.RootElement.GetProperty("client_nonce").GetString()!, V2PacketWriter.NewClientNonce()))); });
            f.DialPort = server.Port;
            var first = await f.Client.ProbeAsync(f.Store.GetPresenceTarget(one)!, 8768, () => true);
            var second = await f.Client.ProbeAsync(f.Store.GetPresenceTarget(two)!, 8768, () => true);
            Check(first.Success && first.Value?.EvidenceLevel == "E2" && second.Code == "identity_conflict" && f.Store.GetPeerSecuritySnapshot(f.A.PeerId).Pairing == PairingState.Suspended,
                "actual signed hello client shares clone detection for two logical LAN endpoints");
        }
        Console.WriteLine($"Passive activity and duplicate identity: {checks} cryptographic, SQLite, lifecycle and concurrency checks."); return checks;
    }
    private static string Code(V2HttpResponse response) { using var p = JsonDocument.Parse(response.Body); return p.RootElement.GetProperty("code").GetString()!; }
    private static async Task Until(Func<bool> condition)
    { for (int i = 0; i < 500; i++) { if (condition()) return; await Task.Delay(10); } throw new TimeoutException("passive identity fixture condition"); }
    private sealed class Fixture : IDisposable, IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "neko-passive-identity-" + Guid.NewGuid().ToString("N"));
        internal ECParameters PrivateClone { get; }
        internal V2PacketWriter A { get; }
        internal V2PacketWriter B { get; } = new(ECDsa.Create(ECCurve.NamedCurves.nistP256), 16384);
        internal Clock Clock { get; } = new();
        internal Faults Faults { get; } = new();
        internal ChatStoreOptions Options => new(_root) { Faults = Faults, UtcNow = () => Clock.GetUtcNow().UtcDateTime };
        internal ChatStore Store { get; }
        internal string Conversation { get; }
        internal V2IdentityMonitor Monitor { get; }
        internal V2Receiver Receiver { get; }
        internal V2LanClient Client { get; }
        internal PresenceService Service { get; }
        internal ConcurrentQueue<string> Diagnostics { get; } = new();
        internal int Connections, DialPort;
        internal Fixture(int capacity = 1024)
        {
            Directory.CreateDirectory(_root); var key = ECDsa.Create(ECCurve.NamedCurves.nistP256); PrivateClone = key.ExportParameters(true); A = new(key, 16384);
            Store = ChatStore.Open(Options); Store.EnsureLocalIdentity(B.PeerId, Convert.FromBase64String(B.PublicSpkiBase64), "fixture");
            Conversation = ((PeerRegistered)Store.RegisterPeer(A.PeerId, PairingState.Paired, Convert.FromBase64String(A.PublicSpkiBase64))).Conversation.ConversationId;
            Monitor = new(Clock, capacity); Receiver = new(Store, B, Clock, Monitor); Receiver.Diagnostic += Diagnostics.Enqueue;
            var handler = new SocketsHttpHandler { ConnectCallback = async (_, token) => {
                Interlocked.Increment(ref Connections); var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try { await socket.ConnectAsync(IPAddress.Loopback, DialPort, token); return new NetworkStream(socket, true); } catch { socket.Dispose(); throw; }
            } };
            Client = new(Store, B, null, handler, Monitor); Service = new(Store, Client, Clock, () => 0); Service.AttachReceiver(Receiver); Service.Diagnostic += Diagnostics.Enqueue;
        }
        internal byte[] Packet(string? id = null, bool receipt = false, V2PacketWriter? writer = null)
        {
            writer ??= A; var hello = Receiver.Handle("/nekochat/v2/hello", writer.HelloRequest(V2PacketWriter.NewClientNonce()), "10.0.0.1", () => true);
            if (hello.StatusCode != 200) return [];
            using var p = JsonDocument.Parse(hello.Body); string nonce = p.RootElement.GetProperty("nonce").GetString()!;
            return receipt ? writer.Receipt(id ?? V2PacketWriter.NewMessageId(), B.PeerId, nonce) : writer.Message(id ?? V2PacketWriter.NewMessageId(), B.PeerId, nonce, "private fixture message");
        }
        internal V2HttpResponse Send(string source, string? id = null, bool receipt = false, V2PacketWriter? writer = null)
        {
            return Receiver.Handle(receipt ? "/nekochat/v2/receipt" : "/nekochat/v2/message", Packet(id, receipt, writer), source, () => true);
        }
        internal string AddRow(string endpoint = "10.0.0.1")
        {
            string Token() => RosterImportPreview.ComputeSnapshotToken(Store.GetRosterSnapshot());
            string row = Store.SaveManualRosterEntry(null, new("Test peer", null, false, endpoint), Token()).EntryId!;
            if (!Store.BindPairedRosterPeer(row, A.PeerId, Token()).Changed) throw new Exception("fixture roster bind"); return row;
        }
        internal long Count(string table)
        { using var c = new SqliteConnection("Data Source=" + Store.DatabasePath + ";Mode=ReadOnly;Pooling=False"); c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT count(*) FROM " + table; return Convert.ToInt64(cmd.ExecuteScalar()); }
        public void Dispose() { Service.DisposeAsync().AsTask().GetAwaiter().GetResult(); Client.Dispose(); Store.Dispose(); A.Dispose(); B.Dispose(); Directory.Delete(_root, true); }
        public async ValueTask DisposeAsync() { await Service.DisposeAsync(); Dispose(); }
    }
    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        internal TimeSpan WallOffset;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero).AddTicks(GetTimestamp()).Add(WallOffset);
        internal void Advance(double seconds) => Interlocked.Add(ref _ticks, TimeSpan.FromSeconds(seconds).Ticks);
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
