using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using NekoChat.Core;
using NekoChat.Core.Pairing;
using NekoChat.Core.Storage;
using NekoChat.Core.V2;

namespace NekoChat.Tests;

internal static class PairingTcpChecks
{
    internal static async Task<int> Run()
    {
        int checks = 0;
        void Check(bool value, string label) { checks++; if (!value) throw new Exception("P1 TCP: " + label); }
        await using (var a = new Node())
        await using (var b = new Node())
        {
            Check((await a.Client.BeginAsync("127.0.0.1", b.Port, b.Identity.PeerId, () => true)).Code == "remote_ended" && a.Peers == 0 && b.Peers == 0, "closed real endpoint refuses request without metadata");
            await b.Responder.OpenWindowAsync(a.Identity.PeerId);
            var begin = await a.Client.BeginAsync("127.0.0.1", b.Port, b.Identity.PeerId, () => true);
            Check(begin.Code == "awaiting_user" && a.Initiator.View.Sas == b.Responder.View.Sas && a.State(b) == PairingState.Pending && b.State(a) == PairingState.Pending, "actual HTTP handshake only exposes matching SAS and pending candidates");
            Check((await a.Client.ConfirmAsync(b.Responder.View.Consent!)).Code == "stale_consent" && a.State(b) == PairingState.Pending, "transport cannot exchange sides' human tickets");
            Check(await b.Responder.ConfirmLocalAsync(b.Responder.View.Consent!), "responder accepts its local explicit action");
            var result = await a.Client.ConfirmAsync(a.Initiator.View.Consent!);
            Check(result.Code == "paired" && a.State(b) == PairingState.Paired && b.State(a) == PairingState.Paired, "two real SQLite stores pair only after both actions and verified TCP confirmation");
            Check(a.Number("SELECT count(*) FROM Message") == 0 && b.Number("SELECT count(*) FROM Message") == 0 && a.Number("SELECT count(*) FROM ManualSendAttempt") == 0, "pair control creates no private messages or send attempts");
            var attempt = a.Prepare(b, "配對後真正簽章訊息🐈\nreturn x;");
            var sent = await a.Messaging.SendPreparedAsync(attempt, "127.0.0.1", b.Port, () => true);
            Check(sent is { Code: "ok", Completion: Confirmed { DraftCleared: true } } && a.Number("SELECT count(*) FROM Message") == 1 && b.Number("SELECT count(*) FROM Message") == 1, "new pairing immediately authorizes real signed hello/message/storage ACK and exact draft clearing");
            Check(b.Store.ListPendingReadReceipts().Count == 0 && !b.Store.GetReadReceiptPolicy().Enabled, "pairing leaves receipt privacy default OFF");
            Check((await a.Client.CancelAsync()).Code == "already_complete" && a.State(b) == PairingState.Paired && b.State(a) == PairingState.Paired, "late cancel never downgrades completed pairing");
        }
        // A real socket disappears after R commits. The only retry is identical human-approved pair.confirm.
        await using (var a = new Node())
        await using (var b = new Node())
        {
            int confirmations = 0; var packets = new List<byte[]>();
            await using var proxy = new V2TcpChecks.ScriptedServer(async (_, body) => {
                var response = await Post(b.Port, body);
                PairingWire.TryParse(body, out var packet);
                if (packet!.Kind == PairingKind.Confirmation) { packets.Add(body.ToArray()); if (++confirmations == 1) return null; }
                return response;
            });
            await b.Responder.OpenWindowAsync(a.Identity.PeerId);
            Check((await a.Client.BeginAsync("127.0.0.1", proxy.Port, b.Identity.PeerId, () => true)).Code == "awaiting_user", "proxy forwards actual commitment and reveal over TCP");
            await b.Responder.ConfirmLocalAsync(b.Responder.View.Consent!); var watch = Stopwatch.StartNew();
            var result = await a.Client.ConfirmAsync(a.Initiator.View.Consent!);
            Check(result.Code == "paired" && confirmations == 2 && packets[0].SequenceEqual(packets[1]) && watch.Elapsed >= TimeSpan.FromSeconds(5), "lost TCP ACK converges with identical confirmation after >=5 seconds");
            Check(a.State(b) == PairingState.Paired && b.State(a) == PairingState.Paired && a.Number("SELECT count(*) FROM ManualSendAttempt") == 0, "cached ACK retry does not send a user message or start a new session");
        }
        foreach (string attack in new[] { "fake200", "empty200", "oversize", "wrong-kind", "duplicate-json", "bad-error-status", "redirect", "wrong-peer", "swapped-reveal" })
        {
            await using var a = new Node(); await using var b = new Node(); int requests = 0;
            await b.Responder.OpenWindowAsync(a.Identity.PeerId);
            await using var server = new V2TcpChecks.ScriptedServer(async (_, body) => {
                requests++;
                if (attack == "fake200") return new(200, "{\"status\":\"delivered\"}"u8.ToArray());
                if (attack == "empty200") return new(200, "{}"u8.ToArray());
                if (attack == "oversize") return new(200, new byte[1025]);
                if (attack == "wrong-kind") return new(200, b.Writer.HelloRequest(V2PacketWriter.NewClientNonce()));
                if (attack == "duplicate-json") return new(200, "{\"version\":2,\"version\":2}"u8.ToArray());
                if (attack == "bad-error-status") return new(200, PairingResponses.Error("pair_state").Body);
                if (attack == "redirect") return new(302, "{}"u8.ToArray());
                var response = await Post(b.Port, body);
                if (attack == "swapped-reveal" && requests == 2)
                {
                    PairingWire.TryParse(response.Body, out var reveal); using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                    return new(200, PairingWire.Reveal(reveal!.SessionId, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), reveal.Nonce!));
                }
                return response;
            });
            using var expected = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            string peer = attack == "wrong-peer" ? V2Crypto.PeerId(expected.ExportSubjectPublicKeyInfo()) : b.Identity.PeerId;
            var result = await a.Client.BeginAsync("127.0.0.1", server.Port, peer, () => true);
            Check(result.Code != "awaiting_user" && a.Peers == 0 && a.Initiator.View.Consent is null && a.State(b) != PairingState.Paired, "malicious HTTP reply cannot create consent/trust: " + attack);
            Check(requests == (attack is "wrong-peer" or "swapped-reveal" ? 2 : 1), "failure has no automatic fresh-session retry: " + attack);
        }
        foreach (string attack in new[] { "charset", "encoding", "chunked-overflow", "oversize-header", "redirect-location", "status-mismatch" })
        {
            await using var a = new Node();
            string response = attack switch {
                "charset" => "HTTP/1.1 200 OK\r\nContent-Type: application/json; charset=utf-16\r\nContent-Length: 2\r\n\r\n{}",
                "encoding" => "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Encoding: gzip\r\nContent-Length: 2\r\n\r\n{}",
                "chunked-overflow" => "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nTransfer-Encoding: chunked\r\n\r\n401\r\n" + new string(' ', 1025) + "\r\n0\r\n\r\n",
                "oversize-header" => "HTTP/1.1 200 OK\r\nX-Pad: " + new string('a', 9000) + "\r\nContent-Type: application/json\r\nContent-Length: 2\r\n\r\n{}",
                "redirect-location" => "HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:" + a.Port + "/nekochat/v2/pair\r\nContent-Type: application/json\r\nContent-Length: 2\r\n\r\n{}",
                _ => "HTTP/1.1 403 Forbidden\r\nContent-Type: application/json\r\nContent-Length: " + PairingResponses.Error("pair_state").Body.Length + "\r\n\r\n" + Encoding.UTF8.GetString(PairingResponses.Error("pair_state").Body)
            };
            await using var peer = new RawPeer(response);
            var result = await a.Client.BeginAsync("127.0.0.1", peer.Port, null, () => true);
            Check(result.Code is "invalid_response" or "transport_error" && a.Peers == 0 && a.Initiator.View.Consent is null && peer.Requests == 1, "bounded HTTP validation rejects without redirect/trust: " + attack);
        }
        foreach (string attack in new[] { "wrong-role", "wrong-sid", "wrong-key" })
        {
            await using var a = new Node(); await using var b = new Node(); await b.Responder.OpenWindowAsync();
            await using var proxy = new V2TcpChecks.ScriptedServer(async (_, body) => {
                var response = await Post(b.Port, body); PairingWire.TryParse(body, out var sent);
                if (sent!.Kind != PairingKind.Confirmation) return response;
                PairingWire.TryParse(response.Body, out var ack);
                if (attack == "wrong-role") return new(200, body);
                if (attack == "wrong-sid") return new(200, PairingWire.ConfirmationSignature(PairingWire.NewNonce(), ack!.SignatureBase64!));
                byte[] wrong = new byte[64]; RandomNumberGenerator.Fill(wrong);
                return new(200, PairingWire.ConfirmationSignature(sent.SessionId, Convert.ToBase64String(wrong)));
            });
            await a.Client.BeginAsync("127.0.0.1", proxy.Port, b.Identity.PeerId, () => true); await b.Responder.ConfirmLocalAsync(b.Responder.View.Consent!);
            Check((await a.Client.ConfirmAsync(a.Initiator.View.Consent!)).Code == "invalid_response" && a.State(b) == PairingState.Unpaired && b.State(a) == PairingState.Paired, "TCP confirmation is bound to responder role/session/key: " + attack);
        }
        await using (var a = new Node())
        await using (var b = new Node())
        {
            bool current = true; await b.Responder.OpenWindowAsync();
            await a.Client.BeginAsync("127.0.0.1", b.Port, b.Identity.PeerId, () => Volatile.Read(ref current));
            var consent = a.Initiator.View.Consent!; current = false;
            Check((await a.Client.ConfirmAsync(consent)).Code == "scope_changed" && a.State(b) == PairingState.Unpaired && b.State(a) == PairingState.Pending, "changed endpoint/session scope cannot authorize local ticket or assume remote cancellation");
        }
        // Flip the actual scope at the last local SQLite COMMIT, after receiving R's valid signature.
        await using (var a = new Node())
        await using (var b = new Node())
        {
            int current = 1; await b.Responder.OpenWindowAsync();
            await a.Client.BeginAsync("127.0.0.1", b.Port, b.Identity.PeerId, () => Volatile.Read(ref current) == 1);
            await b.Responder.ConfirmLocalAsync(b.Responder.View.Consent!);
            a.Faults.Before = op => { if (op == "security-change") Volatile.Write(ref current, 0); };
            var result = await a.Client.ConfirmAsync(a.Initiator.View.Consent!);
            Check(result.Code != "paired" && a.State(b) == PairingState.Unpaired && b.State(a) == PairingState.Paired, "scope guard participates in real final transaction, preserving honest asymmetric result");
        }
        await using (var a = new Node())
        await using (var b = new Node())
        {
            await b.Responder.OpenWindowAsync(); await a.Client.BeginAsync("127.0.0.1", b.Port, b.Identity.PeerId, () => true);
            var confirm = a.Client.ConfirmAsync(a.Initiator.View.Consent!);
            await Until(() => b.Responder.View.RemoteConfirmed);
            Check((await a.Client.BeginAsync("127.0.0.1", b.Port, null, () => true)).Code == "busy", "one physical outbound control operation, no queue of fresh requests");
            var cancel = a.Client.CancelAsync(); await cancel.WaitAsync(TimeSpan.FromSeconds(6)); await confirm.WaitAsync(TimeSpan.FromSeconds(6));
            Check(a.State(b) == PairingState.Unpaired && b.State(a) == PairingState.Unpaired && a.Initiator.View.Stage == PairingStage.Ended, "cancel interrupts five-second polling and forwards best-effort remote cancellation");
        }
        await using (var a = new Node())
        await using (var b = new Node())
        {
            await b.Responder.OpenWindowAsync(); await a.Client.BeginAsync("127.0.0.1", b.Port, b.Identity.PeerId, () => true);
            var confirm = a.Client.ConfirmAsync(a.Initiator.View.Consent!); await Until(() => b.Responder.View.RemoteConfirmed);
            using var input = new CancellationTokenSource();
            var disposed = a.Client.DisposeAsync().AsTask(); var canceled = a.Client.CancelAsync();
            await Task.WhenAll(disposed, canceled, confirm).WaitAsync(TimeSpan.FromSeconds(6));
            Check((await a.Client.BeginAsync("127.0.0.1", b.Port, null, () => true)).Code == "stopped" && a.State(b) == PairingState.Unpaired, "concurrent dispose/cancel drains without callback-lock deadlock or new session");
        }
        foreach (string action in new[] { "cancel", "caller", "dispose" })
        {
            await using var a = new Node(); await using var peer = new RawPeer("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 2\r\n\r\n{}");
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); using var caller = new CancellationTokenSource();
            a.Faults.Read = op => { if (op == "GetLocalIdentitySnapshot") { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(5))) throw new Exception("P1 client read barrier timeout"); } };
            var starting = a.Client.BeginAsync("127.0.0.1", peer.Port, null, () => true, caller.Token);
            if (!await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(5)))) throw new Exception("P1 client read barrier missing");
            Task stopped; Task<PairingStep>? cancel = null;
            if (action == "cancel") stopped = cancel = a.Client.CancelAsync();
            else if (action == "caller") { caller.Cancel(); stopped = Task.CompletedTask; }
            else stopped = a.Client.DisposeAsync().AsTask();
            release.Set(); await Task.WhenAll(starting, stopped).WaitAsync(TimeSpan.FromSeconds(6));
            Check(peer.Requests == 0 && a.Peers == 0 && a.Initiator.View.Stage != PairingStage.Requested, "invalidation during real SQL readiness emits no stale TCP request: " + action);
            if (cancel is not null) Check((await cancel).Code == "user_cancelled", "background cancellation cannot overwrite explicit user-cancel reason");
        }
        // A blocked real SQLite write keeps the class slot even after the wire has closed.
        await using (var a = new Node())
        await using (var b = new Node(pairTimeout: TimeSpan.FromMilliseconds(250)))
        {
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            b.Faults.Before = op => { if (op == "pair-candidate") { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(8))) throw new Exception("P1 physical barrier timeout"); } };
            await b.Responder.OpenWindowAsync();
            var request = PostClosed(b.Port, PairingWire.Request(PairingWire.NewNonce(), a.Identity.PublicSpkiBase64));
            if (!await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(5)))) throw new Exception("P1 SQL barrier missing");
            try
            {
                await request.WaitAsync(TimeSpan.FromSeconds(3));
                Check(b.Listener.PairSlotOccupied && b.Listener.ActivePairingJobs == 1, "wire deadline frees socket but never physical job/class lease");
                Check((await Post(b.Port, PairingWire.Request(PairingWire.NewNonce(), a.Identity.PublicSpkiBase64))).StatusCode == 403 && b.Listener.ActivePairingJobs == 1, "second pairing cannot queue behind stuck storage");
                Check(await DirectLanMessaging.SendDirectAsync("127.0.0.1", "legacy independent route", b.Port), "other route on same port stays responsive while pairing storage blocks");
                var stopping = b.Listener.DisposeAsync().AsTask(); await Task.Delay(50);
                Check(!stopping.IsCompleted, "listener stop waits actual borrowed storage job before disposal");
                release.Set(); await stopping.WaitAsync(TimeSpan.FromSeconds(5));
                Check(!b.Listener.PairSlotOccupied && b.Listener.ActivePairingJobs == 0 && b.Peers == 0, "late canceled candidate rolls back; stop drains slot exactly once");
            }
            finally { release.Set(); }
        }
        // Reserve class on the request line, before slow headers/body; max2s budget also covers header delay.
        await using (var node = new Node(pairTimeout: TimeSpan.FromMilliseconds(250), pairOnly: true))
        {
            await node.Responder.OpenWindowAsync(); using var slow = new TcpClient(); await slow.ConnectAsync(IPAddress.Loopback, node.Port);
            var watch = Stopwatch.StartNew(); await slow.GetStream().WriteAsync("POST /nekochat/v2/pair HTTP/1.1\r\n"u8.ToArray());
            await Until(() => node.Listener.PairSlotOccupied);
            Check((await Post(node.Port, "{}"u8.ToArray())).StatusCode == 403, "slow pair header already owns single class slot");
            await ReadClosed(slow.GetStream()); await Until(() => !node.Listener.PairSlotOccupied);
            Check(watch.Elapsed < TimeSpan.FromSeconds(2) && node.Peers == 0, "classified pairing header has bounded wire lifetime and no candidate");
            Check((await Post(node.Port, new byte[1025])).StatusCode == 400, "pair route has1KiB limit before storage");
            Check((await Post(node.Port, node.Writer.HelloRequest(V2PacketWriter.NewClientNonce()))).StatusCode == 400, "hello packet on pair route rejected without candidate");
        }
        await using (var off = new V2TcpListener(null, Options()))
        {
            off.Start(); Check((await Post(off.Port, "{}"u8.ToArray())).StatusCode == 400 && !off.PairSlotOccupied, "old constructor keeps pair endpoint disabled");
        }
        await using (var a = new Node())
        await using (var b = new Node(pairOnly: true))
        {
            await b.Responder.OpenWindowAsync();
            Check((await a.Client.BeginAsync("127.0.0.1", b.Port, b.Identity.PeerId, () => true)).Code == "awaiting_user", "pair-only listener works before ordinary text L/V2 receiver configuration");
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            Check((await a.Client.CancelAsync()).Code == "user_cancelled" && a.State(b) == PairingState.Unpaired, "pair-only control can be canceled without configuring text policy");
        }
        await using (var a = new Node())
        {
            foreach (string ip in new[] { "example.test", "8.8.8.8", "127.1", "::1", "010.0.0.1" })
            {
                bool rejected = false; try { await a.Client.BeginAsync(ip, 8768, null, () => true); } catch (ArgumentException) { rejected = true; }
                Check(rejected && a.Peers == 0, "reject target before network: " + ip);
            }
        }
        Console.WriteLine($"Conditional P1 TCP: {checks} real two-store/P256/socket consent, lost-ACK, scope, HTTP attack, deadline and physical-drain checks; no human/WPF claim."); return checks;
    }
    private static V2ListenerOptions Options(TimeSpan? timeout = null) => new() { BindAddress = IPAddress.Loopback, Port = 0, RequestsPerSourcePerMinute = 10000, PairingWireTimeout = timeout ?? TimeSpan.FromSeconds(2) };
    private static async Task Until(Func<bool> predicate) { using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while (!predicate()) await Task.Delay(10, stop.Token); }
    private static async Task<V2HttpResponse> Post(int port, byte[] body)
    {
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(5) };
        using var content = new ByteArrayContent(body); content.Headers.ContentType = new("application/json");
        using var response = await http.PostAsync($"http://127.0.0.1:{port}/nekochat/v2/pair", content);
        return new((int)response.StatusCode, await response.Content.ReadAsByteArrayAsync());
    }
    private static async Task PostClosed(int port, byte[] body)
    {
        using var socket = new TcpClient(); await socket.ConnectAsync(IPAddress.Loopback, port); var stream = socket.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"POST /nekochat/v2/pair HTTP/1.1\r\nHost: localhost\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\n\r\n"));
        await stream.WriteAsync(body); await ReadClosed(stream);
    }
    private static async Task ReadClosed(NetworkStream stream)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3)); byte[] bytes = new byte[4096];
        try { while (await stream.ReadAsync(bytes, stop.Token) > 0) { } } catch (IOException) { }
    }
    private sealed class Node : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "neko-p1-tcp-" + Guid.NewGuid().ToString("N"));
        internal Faults Faults { get; } = new();
        internal ChatStore Store { get; }
        internal PairingIdentity Identity { get; }
        internal PairingInitiator Initiator { get; }
        internal PairingResponder Responder { get; }
        internal PairingLanClient Client { get; }
        internal V2PacketWriter Writer { get; }
        internal V2LanClient Messaging { get; }
        internal V2TcpListener Listener { get; }
        internal int Port => Listener.Port;
        internal long Peers => Number("SELECT count(*) FROM Peer");
        internal Node(TimeSpan? pairTimeout = null, bool pairOnly = false)
        {
            var key = ECDsa.Create(ECCurve.NamedCurves.nistP256); var parameters = key.ExportParameters(true);
            try { Writer = new(ECDsa.Create(parameters), 16384); } finally { CryptographicOperations.ZeroMemory(parameters.D!); }
            Store = ChatStore.Open(new(_root) { Faults = Faults, TextValidator = t => V2Text.Check(t, 16384) == V2TextProblem.None });
            Store.EnsureLocalIdentity(Writer.PeerId, Convert.FromBase64String(Writer.PublicSpkiBase64), "fixture");
            Identity = new(Store, key); Initiator = new(Identity); Responder = new(Identity); Client = new(Initiator); Messaging = new(Store, Writer);
            Listener = new(pairOnly ? null : new V2Receiver(Store, Writer), Options(pairTimeout), Responder); Listener.Start();
        }
        internal PairingState? State(Node other) => Store.GetPeerSecuritySnapshot(other.Identity.PeerId).Pairing;
        internal long Number(string sql) { using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Store.DatabasePath, Pooling = false }.ToString()); c.Open(); using var q = c.CreateCommand(); q.CommandText = sql; return Convert.ToInt64(q.ExecuteScalar()); }
        internal PreparedSend Prepare(Node other, string text)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Store.DatabasePath, Pooling = false }.ToString()); connection.Open();
            using var query = connection.CreateCommand(); query.CommandText = "SELECT id FROM Conversation WHERE peerId=@peer"; query.Parameters.AddWithValue("@peer", other.Identity.PeerId);
            string conversation = (string)query.ExecuteScalar()!;
            var draft = Store.GetDraftState(conversation)!; Store.SaveDraft(conversation, text, draft.Epoch, draft.Revision + 1);
            var attempt = (AttemptStarted)Store.BeginManualSend(conversation, text, draft.Epoch, draft.Revision + 1); return Store.GetPreparedSend(attempt.MessageId)!;
        }
        public async ValueTask DisposeAsync() { await Client.DisposeAsync(); await Listener.DisposeAsync(); await Initiator.DisposeAsync(); await Responder.DisposeAsync(); Messaging.Dispose(); Identity.Dispose(); Writer.Dispose(); Store.Dispose(); Directory.Delete(_root, true); }
    }
    private sealed class RawPeer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        internal int Requests;
        internal int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        internal RawPeer(string response) { _listener.Start(); _loop = Run(response); }
        private async Task Run(string response)
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token); var stream = client.GetStream();
                using var header = new MemoryStream(); byte[] one = new byte[1];
                while (header.Length < 8192)
                {
                    await stream.ReadExactlyAsync(one, _stop.Token); header.WriteByte(one[0]);
                    if (header.Length >= 4 && header.GetBuffer().AsSpan((int)header.Length - 4, 4).SequenceEqual("\r\n\r\n"u8)) break;
                }
                string[] lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n");
                int length = int.Parse(lines.Single(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).Split(':')[1]);
                byte[] body = new byte[length]; await stream.ReadExactlyAsync(body, _stop.Token); Interlocked.Increment(ref Requests);
                try { await stream.WriteAsync(Encoding.ASCII.GetBytes(response), _stop.Token); } catch (IOException) { }
            }
            catch (Exception e) when (_stop.IsCancellationRequested && e is OperationCanceledException or SocketException or ObjectDisposedException) { }
        }
        public async ValueTask DisposeAsync() { _stop.Cancel(); _listener.Stop(); await _loop.WaitAsync(TimeSpan.FromSeconds(5)); _stop.Dispose(); }
    }
    private sealed class Faults : IStoreFaults
    {
        internal Action<string>? Before;
        internal Action<string>? Read;
        public void AfterRead(string op) => Read?.Invoke(op);
        public void AfterBegin(string op) { }
        public void BeforeCommit(string op) => Before?.Invoke(op);
        public void AfterCommit(string op) { }
    }
}
