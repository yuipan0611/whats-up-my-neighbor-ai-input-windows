using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NekoChat.Core.Storage;
using NekoChat.Core.V2;

namespace NekoChat.Tests;

internal static class V2TcpChecks
{
    public static async Task<int> Run()
    {
        int checks = 0;
        void Check(bool ok, string name) { checks++; if (!ok) throw new Exception("V2 TCP: " + name); }
        await using (var f = new Fixture())
        {
            Check(f.A.Listener.Running && f.B.Listener.Running && f.A.Rows.Count == 0 && f.B.Rows.Count == 0, "starting paired endpoints sends no messages");
            var prepared = f.A.Prepare("程式碼🐈\n\treturn x;\r\n");
            var result = await f.A.Client.SendPreparedAsync(prepared, "127.0.0.1", f.B.Port, () => true);
            Check(result is { Code: "ok", Completion: Confirmed { DraftCleared: true } }, "actual signed hello/message/ACK confirms outgoing");
            Check(f.A.Rows.Single() is { Direction: "outgoing", RemoteStoredAt: not null } && f.B.Rows.Single() is { Direction: "incoming", LocalReadAt: null } &&
                f.A.Rows.Single().RawText == f.B.Rows.Single().RawText && f.B.Notices == 1, "same raw text committed both sides exactly once");
            Check(f.A.Store.GetDraftState(f.A.Conversation)?.RawText is null && f.B.Store.GetUnreadCount(f.B.Conversation) == 1, "ACK clears own saved snapshot and receiver unread increments");
            var incoming = f.B.Rows.Single();
            f.B.Store.MarkVisibleRead(f.B.Conversation, new[] { incoming.LocalSeq }, new(f.B.Conversation, true, true, false, true));
            Check(f.B.Store.ListPendingReadReceipts().Count == 0 && f.A.Rows.Single().RemoteReadAt is null, "privacy default emits no receipt for local read");
            f.B.Store.SetReadReceiptPolicy(true);
            Check(f.B.Store.ListPendingReadReceipts().Count == 0, "enabling receipts does not backfill");
            prepared = f.A.Prepare("second read"); result = await f.A.Client.SendPreparedAsync(prepared, "127.0.0.1", f.B.Port, () => true);
            incoming = f.B.Rows.Single(m => m.MessageId == prepared.MessageId);
            f.B.Store.MarkVisibleRead(f.B.Conversation, new[] { incoming.LocalSeq }, new(f.B.Conversation, true, true, false, true));
            var receipt = f.B.Store.ListPendingReadReceipts().Single();
            var read = await f.B.Client.SendReadReceiptAsync(receipt, "127.0.0.1", f.A.Port, () => true);
            Check(read is { Code: "ok", Completed: true } && f.B.Store.ListPendingReadReceipts().Count == 0, "real signed receipt ACK deletes exact pending row");
            Check(f.A.Rows.Single(m => m.MessageId == prepared.MessageId).RemoteReadAt is not null && f.A.Rows.Single(m => m.MessageId != prepared.MessageId).RemoteReadAt is null,
                "receipt reads only exact confirmed message");
        }
        await using (var f = new Fixture())
        {
            var hello = await Hello(f.A, f.B); string id = V2PacketWriter.NewMessageId();
            byte[] packet = f.A.Writer.Message(id, f.B.Writer.PeerId, hello.Nonce, "original");
            var bad = JsonNode.Parse(packet)!.AsObject(); bad["text"] = "tampered";
            Check((await Post(f.B.Port, "/nekochat/v2/message", JsonSerializer.SerializeToUtf8Bytes(bad))).StatusCode == 403 && f.B.Rows.Count == 0, "bad real signature rejected without history");
            Check((await Post(f.B.Port, "/nekochat/v2/message", packet)).StatusCode == 200 && f.B.Rows.Count == 1, "bad signature did not burn nonce");
            Check(Code(await Post(f.B.Port, "/nekochat/v2/message", packet)) == "stale_nonce" && f.B.Rows.Count == 1, "same signed packet replay rejected");
            hello = await Hello(f.A, f.B);
            var duplicate = await Post(f.B.Port, "/nekochat/v2/message", f.A.Writer.Message(id, f.B.Writer.PeerId, hello.Nonce, "original"));
            Check(Status(duplicate) == "duplicate" && f.B.Rows.Count == 1 && f.B.Notices == 1, "fresh nonce same id duplicate no unread or notification increment");
            hello = await Hello(f.A, f.B);
            Check(Code(await Post(f.B.Port, "/nekochat/v2/message", f.A.Writer.Message(id, f.B.Writer.PeerId, hello.Nonce, "changed"))) == "conflict" && f.B.Rows.Single().RawText == "original", "signed same id different content conflict preserves original");
            hello = await Hello(f.A, f.B); string next = V2PacketWriter.NewMessageId();
            Check(Code(await Post(f.B.Port, "/nekochat/v2/message", f.A.Writer.Message(next, f.A.Writer.PeerId, hello.Nonce, "wrong target"))) == "wrong_recipient", "signed wrong recipient rejected");
            Check((await Post(f.B.Port, "/nekochat/v2/message", f.A.Writer.Message(next, f.B.Writer.PeerId, hello.Nonce, "right target"))).StatusCode == 200, "wrong recipient did not burn legitimate challenge");
            f.B.Store.SetPairingState(f.A.Writer.PeerId, PairingState.Revoked); hello = await Hello(f.A, f.B);
            Check(Code(await Post(f.B.Port, "/nekochat/v2/message", f.A.Writer.Message(V2PacketWriter.NewMessageId(), f.B.Writer.PeerId, hello.Nonce, "blocked"))) == "unpaired_sender" && f.B.Rows.Count == 2, "revoked returns generic unpaired and no write");
        }
        await using (var f = new Fixture(remoteL: 32))
        {
            var prepared = f.A.Prepare(new string('a', 33)); var result = await f.A.Client.SendPreparedAsync(prepared, "127.0.0.1", f.B.Port, () => true);
            Check(result.Code == "payload_too_large" && !result.MayHaveDelivered && f.B.Rows.Count == 0 && f.A.Store.GetDraftState(f.A.Conversation)?.RawText?.Length == 33,
                "negotiated remote L rejects before sending and retains draft");
        }
        foreach (string scenario in new[] { "fake-hello", "fake-ack", "lost-ack", "stale-once", "stale-always", "wrong-id", "wrong-fingerprint", "revoke-before-send", "revoke-before-ack", "storage-fail", "bad-pin", "oversize-response", "cancel-before", "early-read", "receive-cancel" })
        {
            await using var f = new Fixture(); int hellos = 0, messages = 0; string storageReply = ""; V2ReceiptSendResult? earlyRead = null;
            using var attacker = new V2PacketWriter(ECDsa.Create(ECCurve.NamedCurves.nistP256), 16384);
            await using var proxy = new ScriptedServer(async (path, body) => {
                if (path.EndsWith("/hello", StringComparison.Ordinal))
                {
                    hellos++;
                    if (scenario == "fake-hello") return new(200, "{\"status\":\"delivered\"}"u8.ToArray());
                    if (scenario == "bad-pin") { using var p = JsonDocument.Parse(body); return new(200, attacker.HelloReply(p.RootElement.GetProperty("client_nonce").GetString()!, V2PacketWriter.NewClientNonce())); }
                    if (scenario == "oversize-response") return new(200, new byte[8193]);
                    var reply = await Post(f.B.Port, path, body);
                    if (scenario == "revoke-before-send") f.A.Store.SetPairingState(f.B.Writer.PeerId, PairingState.Revoked);
                    if (scenario == "stale-once" && hellos == 1) f.B.Receiver.SetReady(true);
                    return reply;
                }
                messages++;
                if (scenario == "fake-ack") return new(200, "{\"status\":\"delivered\"}"u8.ToArray());
                if (scenario == "stale-always") return V2HttpResponse.Error("stale_nonce");
                if (scenario == "storage-fail") f.B.Faults.Before = op => { if (op == "receive") throw new IOException("private fixture contents must not be returned"); };
                if (scenario == "receive-cancel") f.B.Faults.Before = op => { if (op == "receive") f.B.Receiver.SetReady(false); };
                var response = await Post(f.B.Port, path, body);
                if (scenario == "storage-fail") storageReply = Encoding.UTF8.GetString(response.Body);
                if (scenario == "early-read")
                {
                    var incoming = f.B.Rows.Single(); f.B.Store.SetReadReceiptPolicy(true);
                    f.B.Store.MarkVisibleRead(f.B.Conversation, new[] { incoming.LocalSeq }, new(f.B.Conversation, true, true, false, true));
                    earlyRead = await f.B.Client.SendReadReceiptAsync(f.B.Store.ListPendingReadReceipts().Single(), "127.0.0.1", f.A.Port, () => true);
                }
                if (scenario == "lost-ack") return null;
                if (scenario == "revoke-before-ack") { f.A.Store.SetPairingState(f.B.Writer.PeerId, PairingState.Revoked); f.A.Store.SetPairingState(f.B.Writer.PeerId, PairingState.Paired); }
                if (scenario is "wrong-id" or "wrong-fingerprint")
                {
                    using var p = JsonDocument.Parse(body); var m = p.RootElement;
                    string id = scenario == "wrong-id" ? V2PacketWriter.NewMessageId() : m.GetProperty("message_id").GetString()!;
                    string fp = scenario == "wrong-fingerprint" ? new string('0', 64) : V2Signing.Fingerprint(f.B.Writer.PeerId, m.GetProperty("text").GetString()!, null);
                    return new(200, f.B.Writer.MessageAck("stored", id, f.A.Writer.PeerId, fp));
                }
                return response;
            });
            var prepared = f.A.Prepare("fault case " + scenario); using var cancel = new CancellationTokenSource(); if (scenario == "cancel-before") cancel.Cancel();
            var result = await f.A.Client.SendPreparedAsync(prepared, "127.0.0.1", proxy.Port, () => true, cancel.Token);
            if (scenario == "stale-once")
                Check(result.Code == "ok" && hellos == 2 && messages == 2 && f.A.Rows.Count == 1 && f.B.Rows.Count == 1, "one stale nonce protocol retry converges with same user message");
            else if (scenario == "early-read")
            {
                Check(result.Code == "ok" && earlyRead is { Code: "storage_unavailable", Completed: false } && f.B.Store.ListPendingReadReceipts().Count == 1 && f.A.Rows.Single().RemoteReadAt is null,
                    "early read before outgoing confirmation gets transient503 and preserves reader queue");
                var receipt = f.B.Store.ListPendingReadReceipts().Single(); var read = await f.B.Client.SendReadReceiptAsync(receipt, "127.0.0.1", f.A.Port, () => true);
                Check(read.Completed && f.B.Store.ListPendingReadReceipts().Count == 0 && f.A.Rows.Single().RemoteReadAt is not null, "queued early read succeeds after message ACK without losing read fact");
            }
            else
            {
                Check(result.Code != "ok" && f.A.Rows.Count == 0 && f.A.Store.GetDraftState(f.A.Conversation)?.RawText == prepared.RawText, scenario + " cannot confirm or clear draft");
                if (scenario == "fake-hello") Check(hellos == 1 && messages == 0 && result.Code == "hello_proof_invalid", "HTTP200 fake hello no downgrade or message");
                if (scenario == "fake-ack") Check(result.Code == "ack_invalid" && f.B.Rows.Count == 0 && messages == 1, "HTTP200 fake ACK no success");
                if (scenario == "bad-pin") Check(result.Code == "hello_key_mismatch" && messages == 0 && f.B.Rows.Count == 0, "real signed attacker hello fails pinned receiver identity");
                if (scenario == "oversize-response") Check(result.Code == "invalid_response" && messages == 0, "client bounds response before JSON or signing work");
                if (scenario == "cancel-before") Check(result.Code == "cancelled" && hellos == 0 && messages == 0, "already cancelled manual attempt makes no network request");
                if (scenario == "stale-always") Check(hellos == 2 && messages == 2 && result.Code == "stale_nonce", "stale nonce retry stops after exactly one retry");
                if (scenario == "revoke-before-send") Check(messages == 0 && f.B.Rows.Count == 0, "local revoke while awaiting hello prevents message network send");
                if (scenario == "revoke-before-ack") Check(result.Code == "stale_security" && f.B.Rows.Count == 1, "valid ACK after local ABA cannot finalize old job");
                if (scenario == "storage-fail") Check(result.Code == "storage_unavailable" && f.B.Rows.Count == 0 && f.B.Notices == 0 &&
                    !storageReply.Contains("private fixture", StringComparison.Ordinal) && !storageReply.Contains(prepared.RawText, StringComparison.Ordinal), "storage rollback returns generic failure without stored notice, message text or exception content");
                if (scenario == "receive-cancel") Check(result.Code == "receiver_not_ready" && f.B.Rows.Count == 0 && f.B.Notices == 0, "receiver lifecycle cancellation before COMMIT rolls back actual TCP delivery");
                if (scenario == "wrong-id") Check(result.Code == "ack_invalid" && f.B.Rows.Count == 1, "properly signed wrong id ACK rejected");
                if (scenario == "wrong-fingerprint") Check(result.Code == "AckMismatch" && f.B.Rows.Count == 1, "properly signed wrong content ACK rejected");
                if (scenario == "lost-ack")
                {
                    Check(f.B.Rows.Count == 1 && f.B.Notices == 1 && result.MayHaveDelivered && messages == 1, "receiver commits and notifies before lost ACK without automatic user-message resend");
                    var draft = f.A.Store.GetDraftState(f.A.Conversation)!;
                    var retry = (AttemptStarted)f.A.Store.BeginManualRetry(prepared.MessageId, prepared.RawText, f.A.Conversation, draft.Epoch, draft.Revision);
                    result = await f.A.Client.SendPreparedAsync(f.A.Store.GetPreparedSend(retry.MessageId)!, "127.0.0.1", f.B.Port, () => true);
                    Check(result.Code == "ok" && retry.MessageId == prepared.MessageId && f.A.Rows.Count == 1 && f.B.Rows.Count == 1 && f.B.Notices == 1, "explicit manual retry after lost ACK confirms same id without duplicate notification");
                }
            }
        }
        await using (var f = new Fixture())
        {
            var prepared = f.A.Prepare("privacy toggle"); _ = await f.A.Client.SendPreparedAsync(prepared, "127.0.0.1", f.B.Port, () => true);
            var incoming = f.B.Rows.Single(); f.B.Store.SetReadReceiptPolicy(true);
            f.B.Store.MarkVisibleRead(f.B.Conversation, new[] { incoming.LocalSeq }, new(f.B.Conversation, true, true, false, true));
            var receipt = f.B.Store.ListPendingReadReceipts().Single(); int transmissions = 0;
            await using var proxy = new ScriptedServer(async (path, body) => {
                if (path.EndsWith("/hello", StringComparison.Ordinal)) { var reply = await Post(f.A.Port, path, body); f.B.Store.SetReadReceiptPolicy(false); return reply; }
                transmissions++; return await Post(f.A.Port, path, body);
            });
            var result = await f.B.Client.SendReadReceiptAsync(receipt, "127.0.0.1", proxy.Port, () => true);
            Check(result is { Code: "policy_changed", Completed: false } && transmissions == 0 && f.A.Rows.Single().RemoteReadAt is null && f.B.Store.ListPendingReadReceipts().Count == 0,
                "privacy off during hello prevents receipt transmission and drops queued intent");
        }
        await using (var f = new Fixture())
        {
            byte[] hello = f.A.Writer.HelloRequest(V2PacketWriter.NewClientNonce());
            string Valid(string extras = "", string? length = null, string path = "/nekochat/v2/hello", string method = "POST", string media = "application/json") =>
                $"{method} {path} HTTP/1.1\r\nHost: localhost\r\nContent-Type: {media}\r\nContent-Length: {length ?? hello.Length.ToString()}\r\n{extras}\r\n";
            foreach (string header in new[] {
                Valid("Content-Length: 1\r\n"), Valid("content-length: 1\r\n"), Valid("Transfer-Encoding: chunked\r\n"), Valid("Expect: 100-continue\r\n"),
                Valid(length: "-1"), Valid(length: "+1"), Valid(length: "abc"), Valid(length: "2147483648"), Valid(path: "/wrong"), Valid(path: "/nekochat/v2/hello?x=1"),
                Valid(method: "GET"), Valid(media: "text/plain"), Valid(media: "application/json-bad"), Valid(media: "application/json; charset=utf-16"),
                Valid(" folded: x\r\n"), Valid().Replace("Host: localhost\r\n", ""), Valid().Replace("Host:", "Höst:"), Valid("X: a\nb\r\n") })
                Check((await Raw(f.B.Port, Encoding.UTF8.GetBytes(header).Concat(hello).ToArray())).StartsWith("HTTP/1.1 400 ", StringComparison.Ordinal), "raw HTTP framing/path/media rejected " + checks);
            Check((await Raw(f.B.Port, Encoding.ASCII.GetBytes(Valid(length: "8193")))).StartsWith("HTTP/1.1 413 ", StringComparison.Ordinal), "control body upper bound rejected from header before allocation");
            Check((await Raw(f.B.Port, Encoding.ASCII.GetBytes(Valid(length: (f.B.Receiver.MaxMessageBodyBytes + 1).ToString(), path: "/nekochat/v2/message")))).StartsWith("HTTP/1.1 413 ", StringComparison.Ordinal), "message bound follows explicit L before allocation");
            Check((await Raw(f.B.Port, Encoding.ASCII.GetBytes("POST /nekochat/v2/hello HTTP/1.1\r\nX: " + new string('a', 8200)))).StartsWith("HTTP/1.1 400 ", StringComparison.Ordinal), "header8KiB hard bound");
            Check((await Raw(f.B.Port, Encoding.ASCII.GetBytes(Valid()).Concat(hello[..^1]).ToArray())).StartsWith("HTTP/1.1 400 ", StringComparison.Ordinal), "truncated body not zero padded or accepted");
            Check((await Raw(f.B.Port, Encoding.ASCII.GetBytes(Valid()).Concat(hello).Concat("extra"u8.ToArray()).ToArray())).StartsWith("HTTP/1.1 400 ", StringComparison.Ordinal), "already buffered bytes past content length rejected");
            Check((await Raw(f.B.Port, Encoding.ASCII.GetBytes(Valid(media: "application/json; charset=\"utf-8\"")).Concat(hello).ToArray())).StartsWith("HTTP/1.1 200 ", StringComparison.Ordinal), "valid framed raw hello accepted");
            Check(f.B.Rows.Count == 0, "all parser tests create no chat history");
        }
        var clock = new Clock();
        var sourceLimit = new V2TcpListener.SourceRateLimit(clock, 2, 2);
        Check(sourceLimit.Allow("10.0.0.1") && sourceLimit.Allow("10.0.0.2") && !sourceLimit.Allow("10.0.0.3"), "source accounting capacity rejects new source instead of evicting live budget");
        Check(sourceLimit.Allow("10.0.0.1") && !sourceLimit.Allow("10.0.0.1"), "existing sources keep independent bounded sliding quotas");
        clock.Ticks = TimeSpan.FromSeconds(60).Ticks;
        Check(sourceLimit.Allow("10.0.0.3"), "expired accounting releases source capacity"); clock.Ticks = 0;
        await using (var f = new Fixture(listenerOptions: new() { BindAddress = IPAddress.Loopback, Port = 0, Clock = clock }))
        {
            for (int i = 0; i < 20; i++) Check((await Post(f.B.Port, "/nekochat/v2/hello", f.A.Writer.HelloRequest(V2PacketWriter.NewClientNonce()))).StatusCode == 200, "default source budget request " + i);
            Check(Code(await Post(f.B.Port, "/nekochat/v2/hello", f.A.Writer.HelloRequest(V2PacketWriter.NewClientNonce()))) == "rate_limited", "21st connection rejected with429");
            clock.Ticks = TimeSpan.FromSeconds(59.999).Ticks;
            Check(Code(await Post(f.B.Port, "/nekochat/v2/hello", f.A.Writer.HelloRequest(V2PacketWriter.NewClientNonce()))) == "rate_limited", "rate window not expired early");
            clock.Ticks = TimeSpan.FromSeconds(60).Ticks;
            Check((await Post(f.B.Port, "/nekochat/v2/hello", f.A.Writer.HelloRequest(V2PacketWriter.NewClientNonce()))).StatusCode == 200, "rate window expires exact monotonic minute");
        }
        await using (var f = new Fixture(listenerOptions: new() { BindAddress = IPAddress.Loopback, Port = 0, MaxConnections = 2, RequestsPerSourcePerMinute = 1000, ConnectionTimeout = TimeSpan.FromMilliseconds(500) }))
        {
            using var first = new TcpClient(); using var second = new TcpClient();
            await first.ConnectAsync(IPAddress.Loopback, f.B.Port); await first.GetStream().WriteAsync("POST "u8.ToArray());
            await second.ConnectAsync(IPAddress.Loopback, f.B.Port); await second.GetStream().WriteAsync("POST "u8.ToArray());
            await Until(() => f.B.Listener.ActiveConnections == 2);
            Check(Code(await Post(f.B.Port, "/nekochat/v2/hello", f.A.Writer.HelloRequest(V2PacketWriter.NewClientNonce()))) == "rate_limited" && f.B.Listener.ActiveConnections <= 2, "slow clients cannot create workers beyond limit");
            await Until(() => f.B.Listener.ActiveConnections == 0);
            Check((await first.GetStream().ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(2))) == 0, "incomplete header deadline closes socket and releases slot");
            Check((await Post(f.B.Port, "/nekochat/v2/hello", f.A.Writer.HelloRequest(V2PacketWriter.NewClientNonce()))).StatusCode == 200, "new request works after slow clients expire");
            using var held = new TcpClient(); await held.ConnectAsync(IPAddress.Loopback, f.B.Port); var heldStream = held.GetStream(); await heldStream.WriteAsync("POST "u8.ToArray());
            await Until(() => f.B.Listener.ActiveConnections == 1);
            int port = f.B.Port; await f.B.Listener.DisposeAsync();
            Check(!f.B.Listener.Running && f.B.Listener.ActiveConnections == 0, "dispose drains handlers and closes listener");
            Check((await heldStream.ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(2))) == 0, "dispose cancels in-flight incomplete request");
            bool closed = false; using var client = new TcpClient(); try { await client.ConnectAsync(IPAddress.Loopback, port); } catch (SocketException) { closed = true; }
            Check(closed, "disposed listener no longer accepts");
        }
        Console.WriteLine($"Product V2 actual TCP: {checks} delivery, signatures, privacy, retry, framing and resource checks."); return checks;
    }
    private static async Task Until(Func<bool> condition)
    { for (int i = 0; i < 150; i++) { if (condition()) return; await Task.Delay(10); } throw new TimeoutException("TCP fixture condition"); }
    private static string Code(V2HttpResponse response) { using var p = JsonDocument.Parse(response.Body); return p.RootElement.TryGetProperty("code", out var c) ? c.GetString()! : ""; }
    private static string Status(V2HttpResponse response) { using var p = JsonDocument.Parse(response.Body); return p.RootElement.GetProperty("status").GetString()!; }
    private static async Task<V2HttpResponse> Post(int port, string path, byte[] body)
    {
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(5) };
        using var content = new ByteArrayContent(body); content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await http.PostAsync($"http://127.0.0.1:{port}{path}", content);
        return new((int)response.StatusCode, await response.Content.ReadAsByteArrayAsync());
    }
    private static async Task<V2HelloInfo> Hello(Side sender, Side receiver)
    {
        string nonce = V2PacketWriter.NewClientNonce(); var response = await Post(receiver.Port, "/nekochat/v2/hello", sender.Writer.HelloRequest(nonce));
        var result = new V2Verifier(sender.Writer.MaxTextBytes).VerifyHello(response.Body, new(nonce, new("paired", receiver.Writer.PublicSpkiBase64), new[] { "msg.sign" }));
        return result.Value ?? throw new Exception("fixture hello " + result.Code);
    }
    private static async Task<string> Raw(int port, byte[] bytes)
    {
        using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, port); var stream = client.GetStream(); await stream.WriteAsync(bytes); client.Client.Shutdown(SocketShutdown.Send);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3)); using var header = new MemoryStream(); byte[] one = new byte[1];
        while (header.Length < 8192)
        {
            await stream.ReadExactlyAsync(one, timeout.Token); header.WriteByte(one[0]);
            if (header.Length >= 4 && header.GetBuffer().AsSpan((int)header.Length - 4, 4).SequenceEqual("\r\n\r\n"u8)) break;
        }
        string text = Encoding.ASCII.GetString(header.ToArray());
        int length = int.Parse(text.Split("\r\n").Single(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).Split(':')[1]);
        if (length is < 1 or > 8192) throw new InvalidDataException("fixture response bound");
        byte[] body = new byte[length]; await stream.ReadExactlyAsync(body, timeout.Token);
        // Stop at the complete HTTP response, not at TCP EOF: closing a rejected request with unread junk can send a reset after the response.
        return text + Encoding.ASCII.GetString(body);
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal Side A { get; }
        internal Side B { get; }
        internal Fixture(int remoteL = 16384, V2ListenerOptions? listenerOptions = null)
        {
            A = new(16384, null); B = new(remoteL, listenerOptions);
            A.Pair(B); B.Pair(A); A.Listener.Start(); B.Listener.Start();
        }
        public async ValueTask DisposeAsync() { await A.DisposeAsync(); await B.DisposeAsync(); }
    }
    private sealed class Side : IAsyncDisposable
    {
        internal string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "neko-v2-tcp-" + Guid.NewGuid().ToString("N"));
        internal V2PacketWriter Writer { get; }
        internal ChatStore Store { get; }
        internal V2Receiver Receiver { get; }
        internal V2TcpListener Listener { get; }
        internal V2LanClient Client { get; }
        internal Faults Faults { get; } = new();
        internal string Conversation { get; private set; } = "";
        internal int Notices;
        internal int Port => Listener.Port;
        internal IReadOnlyList<StoredMessage> Rows => Store.ListMessages(Conversation, pageSize: 200).Items;
        internal Side(int max, V2ListenerOptions? options)
        {
            Directory.CreateDirectory(DirectoryPath); Writer = new(ECDsa.Create(ECCurve.NamedCurves.nistP256), max);
            Store = ChatStore.Open(new(DirectoryPath) { TextValidator = t => V2Text.Check(t, max) == V2TextProblem.None, Faults = Faults });
            Store.EnsureLocalIdentity(Writer.PeerId, Convert.FromBase64String(Writer.PublicSpkiBase64), "fixture");
            Receiver = new(Store, Writer); Listener = new(Receiver, options ?? new() { BindAddress = IPAddress.Loopback, Port = 0, RequestsPerSourcePerMinute = 10000 });
            Listener.MessageStored += _ => Interlocked.Increment(ref Notices); Client = new(Store, Writer);
        }
        internal void Pair(Side other) => Conversation = ((PeerRegistered)Store.RegisterPeer(other.Writer.PeerId, PairingState.Paired, Convert.FromBase64String(other.Writer.PublicSpkiBase64))).Conversation.ConversationId;
        internal PreparedSend Prepare(string text)
        {
            var draft = Store.GetDraftState(Conversation)!; Store.SaveDraft(Conversation, text, draft.Epoch, draft.Revision + 1);
            var started = (AttemptStarted)Store.BeginManualSend(Conversation, text, draft.Epoch, draft.Revision + 1); return Store.GetPreparedSend(started.MessageId)!;
        }
        public async ValueTask DisposeAsync() { Client.Dispose(); await Listener.DisposeAsync(); Store.Dispose(); Writer.Dispose(); Directory.Delete(DirectoryPath, true); }
    }
    private sealed class Faults : IStoreFaults
    {
        internal Action<string>? Before;
        public void AfterBegin(string op) { }
        public void BeforeCommit(string op) => Before?.Invoke(op);
        public void AfterCommit(string op) { }
    }
    private sealed class Clock : TimeProvider
    {
        internal long Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Ticks;
    }
    private sealed class ScriptedServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        internal int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        internal ScriptedServer(Func<string, byte[], Task<V2HttpResponse?>> handler) { _listener.Start(); _loop = Loop(handler); }
        private async Task Loop(Func<string, byte[], Task<V2HttpResponse?>> handler)
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stop.Token); var stream = client.GetStream(); using var header = new MemoryStream(); byte[] one = new byte[1];
                    while (header.Length < 8192)
                    {
                        await stream.ReadExactlyAsync(one, _stop.Token); header.WriteByte(one[0]);
                        if (header.Length >= 4 && header.GetBuffer().AsSpan((int)header.Length - 4, 4).SequenceEqual("\r\n\r\n"u8)) break;
                    }
                    string[] lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n"); string path = lines[0].Split(' ')[1];
                    int length = int.Parse(lines.Single(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).Split(':')[1]);
                    if (length is < 1 or > 400000) throw new Exception("fixture request limit");
                    byte[] body = new byte[length]; await stream.ReadExactlyAsync(body, _stop.Token); var reply = await handler(path, body);
                    if (reply is not null) await V2TcpListener.RespondAsync(stream, reply, _stop.Token);
                }
            }
            catch (Exception e) when (_stop.IsCancellationRequested && e is OperationCanceledException or SocketException or ObjectDisposedException) { }
        }
        public async ValueTask DisposeAsync() { _stop.Cancel(); _listener.Stop(); await _loop.WaitAsync(TimeSpan.FromSeconds(6)); _stop.Dispose(); }
    }
}
