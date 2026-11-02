using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NekoChat.Core.V2;

namespace NekoChat.Tests;

internal static class V2BoundaryChecks
{
    public static async Task<int> Run()
    {
        int checks = 0;
        void Check(bool ok, string name) { checks++; if (!ok) throw new Exception("V2 boundary: " + name); }
        void Reject(Action action, string name)
        {
            bool rejected = false;
            try { action(); } catch (Exception e) when (e is ArgumentException or InvalidOperationException or CryptographicException or OverflowException) { rejected = true; }
            Check(rejected, name);
        }
        bool Parse(byte[] raw, V2JsonLimits? limits = null) => V2Json.TryParseObject(raw, limits ?? V2JsonLimits.Control, out _, out _);
        foreach (string raw in new[] {
            "{\"a\":1,\"a\":2}", "{\"a\":1,\"\\u0061\":2}", "{\"x\":{\"a\":1,\"a\":2}}", "{\"x\":[{\"a\":1,\"a\":2}]}",
            "{\"x\":\"\\ud800\"}", "{\"x\":\"\\udc00\"}", "{\"\\ud800\":1}", "{\"x\":\"\\ud800a\"}", "{\"x\":\"\\ud800\\ud800\"}",
            "{\"x\":1,}", "{\"x\":/*a*/1}", "{} {}", "{}true", "[]", "null", "", "{\"x\":NaN}", "{\"x\":01}" })
            Check(!Parse(Encoding.UTF8.GetBytes(raw)), "reject malformed raw JSON " + checks);
        foreach (string raw in new[] { "{\"x\":\"\\ud83d\\udc08\"}", "{\"x\":\"\\\\ud800\"}", "{\"x\":\"\\\\\\\\ud800\"}", "{\"🐈\":\"貓\"} \r\n" })
            Check(Parse(Encoding.UTF8.GetBytes(raw)), "accept valid escaped/raw Unicode " + checks);
        Check(!Parse([0xef, 0xbb, 0xbf, (byte)'{', (byte)'}']), "BOM rejected");
        foreach (byte[] invalid in new byte[][] { [0xc0, 0xaf], [0xed, 0xa0, 0x80], [0xf4, 0x90, 0x80, 0x80], [0xff], [0xe2, 0x82] })
            Check(!Parse("{\"x\":\""u8.ToArray().Concat(invalid).Concat("\"}"u8.ToArray()).ToArray()), "invalid UTF8 rejected " + checks);
        string Nested(int depth) => string.Concat(Enumerable.Repeat("{\"x\":", depth)) + "0" + new string('}', depth);
        Check(Parse(Encoding.UTF8.GetBytes(Nested(8))) && !Parse(Encoding.UTF8.GetBytes(Nested(9))), "depth exact eight");
        Check(Parse(Encoding.UTF8.GetBytes("{\"x\":[" + string.Join(',', Enumerable.Repeat("0", 64)) + "]}")) &&
            !Parse(Encoding.UTF8.GetBytes("{\"x\":[" + string.Join(',', Enumerable.Repeat("0", 65)) + "]}")), "array item limit");
        Check(Parse(Encoding.UTF8.GetBytes("{" + string.Join(',', Enumerable.Range(0, 64).Select(i => $"\"k{i}\":0")) + "}")) &&
            !Parse(Encoding.UTF8.GetBytes("{" + string.Join(',', Enumerable.Range(0, 65).Select(i => $"\"k{i}\":0")) + "}")), "object member limit");
        Check(Parse("{} "u8.ToArray(), new(3, 8, 64)) && !Parse("{}  "u8.ToArray(), new(3, 8, 64)), "byte limit inclusive");
        Reject(() => Parse("{}"u8.ToArray(), new(0, 8, 64)), "reject invalid parser limits");
        Reject(() => new V2Verifier(0), "explicit positive L required");
        Reject(() => new V2Verifier(int.MaxValue), "L arithmetic cannot overflow");

        using var a = new V2PacketWriter(ECDsa.Create(ECCurve.NamedCurves.nistP256), 16384);
        using var b = new V2PacketWriter(ECDsa.Create(ECCurve.NamedCurves.nistP256), 65536);
        var verifier = new V2Verifier(16384); var context = new V2IncomingContext(b.PeerId, new("paired", a.PublicSpkiBase64));
        var clock = new Clock(); var nonces = new V2NonceStore(clock); string nonce = nonces.Issue(a.PeerId), cn = V2PacketWriter.NewClientNonce();
        string id = V2PacketWriter.NewMessageId();
        Check(id[14] == '4' && "89ab".Contains(id[19]), "new message id v4 variant");
        byte[] helloRequest = a.HelloRequest(cn), hello = b.HelloReply(cn, nonce);
        Check(verifier.VerifyHelloRequest(helloRequest).Value?.SenderId == a.PeerId, "actual hello request");
        var hc = new V2HelloContext(cn, new("paired", b.PublicSpkiBase64), new[] { "msg.sign", "receipt.read" });
        var hv = verifier.VerifyHello(hello, hc);
        Check(hv.Success && hv.Value!.MaxTextBytes == 65536 && hv.Value.EvidenceLevel == "E2" && hv.Value.KeyExchangeSpkiBase64 is null, "actual signed hello explicit remote L");
        Check(hv.Value!.Capabilities.SequenceEqual(new[] { "msg.sign", "receipt.read" }), "only implemented capabilities advertised");
        Check(verifier.VerifyHello(hello, hc with { ClientNonce = V2PacketWriter.NewClientNonce() }).Code == "hello_proof_invalid", "hello replay different client nonce");
        Check(verifier.VerifyHello(hello, hc with { Peer = new("revoked", a.PublicSpkiBase64) }).Value?.EvidenceLevel == "E1", "signed hello does not pair revoked identity");
        Check(verifier.VerifyHello(hello, hc with { RequiredCapabilities = new[] { "msg.aead" } }).Code == "hello_downgrade", "unimplemented encryption required rejected");
        byte[] message = a.Message(id, b.PeerId, nonce, "中文🐈\n\tcode\r\n", "too-fast");
        var good = verifier.VerifyMessage(message, context);
        Check(good.Success && good.Value!.Text == "中文🐈\n\tcode\r\n" && good.Value.Reaction == "too-fast", "actual signed message roundtrip");
        byte[] receipt = a.Receipt(id, b.PeerId, nonce);
        Check(verifier.VerifyReceipt(receipt, context).Success, "actual signed receipt roundtrip");
        string fingerprint = good.Value!.ContentFingerprint;
        var ac = new V2AckContext(id, a.PeerId, b.PublicSpkiBase64, fingerprint);
        foreach (string status in new[] { "stored", "duplicate" }) Check(verifier.VerifyMessageAck(b.MessageAck(status, id, a.PeerId, fingerprint), ac).Success, "actual message ACK " + status);
        foreach (string status in new[] { "ok", "duplicate", "discarded" }) Check(verifier.VerifyReceiptAck(b.ReceiptAck(status, id, a.PeerId), ac).Success, "actual receipt ACK " + status);
        Check(verifier.VerifyMessageAck(b.MessageAck("stored", id, a.PeerId, fingerprint), ac with { ExpectedFingerprint = new string('0', 64) }).Code == "AckMismatch", "valid ACK wrong content");
        Check(verifier.VerifyMessageAck(b.MessageAck("stored", id, a.PeerId, fingerprint), ac with { ExpectedMessageId = V2PacketWriter.NewMessageId() }).Code == "ack_invalid", "valid ACK wrong attempt");

        foreach (var packet in new[] { (Raw: message, Kind: "message.send"), (Raw: receipt, Kind: "receipt"), (Raw: hello, Kind: "hello.ok"),
            (Raw: b.MessageAck("stored", id, a.PeerId, fingerprint), Kind: "message.ack"), (Raw: b.ReceiptAck("ok", id, a.PeerId), Kind: "receipt.ack") })
        {
            bool Success(byte[] raw) => packet.Kind switch {
                "message.send" => verifier.VerifyMessage(raw, context).Success, "receipt" => verifier.VerifyReceipt(raw, context).Success,
                "hello.ok" => verifier.VerifyHello(raw, hc).Success, "message.ack" => verifier.VerifyMessageAck(raw, ac).Success,
                _ => verifier.VerifyReceiptAck(raw, ac).Success };
            foreach (string field in new[] { "protocol", "version", "kind" })
            {
                Check(!Success(Edit(packet.Raw, p => p.Remove(field))), packet.Kind + " missing " + field);
                Check(!Success(Edit(packet.Raw, p => p[field] = field == "version" ? JsonValue.Create(1) : JsonValue.Create("wrong"))), packet.Kind + " wrong " + field);
            }
            Check(!Success(Edit(packet.Raw, p => p["unknown"] = null)), packet.Kind + " extra key");
            string signatureField = packet.Kind == "hello.ok" ? "proof_sig" : packet.Kind.EndsWith("ack") ? "ack_sig" : "sig";
            Check(!Success(Edit(packet.Raw, p => { byte[] sig = Convert.FromBase64String(p[signatureField]!.GetValue<string>()); sig[0] ^= 1; p[signatureField] = Convert.ToBase64String(sig); })), packet.Kind + " actual signature mutation");
            Check(!Success(Edit(packet.Raw, p => p[signatureField] = p[signatureField]!.GetValue<string>().TrimEnd('='))), packet.Kind + " unpadded signature");
            Check(!Success(Edit(packet.Raw, p => p[signatureField] = Convert.ToBase64String(new byte[63]))), packet.Kind + " truncated P1363");
        }
        Check(!verifier.VerifyMessage(Edit(message, p => p.Remove("cert")), context).Success &&
            !verifier.VerifyMessage(Edit(message, p => p["cert"] = new JsonObject()), context).Success, "message cert required null");
        foreach (string field in new[] { "cert", "kex_spki" }) Check(!verifier.VerifyHello(Edit(hello, p => p.Remove(field)), hc).Success, "hello required " + field);
        Check(!verifier.VerifyHello(Edit(hello, p => p["capabilities"] = new JsonArray("msg.sign", "receipt.read", "msg.aead")), hc).Success, "AEAD without KEX rejected");
        using (var kex = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256))
        {
            Check(V2Crypto.IsCanonicalKeyExchange(kex.ExportSubjectPublicKeyInfo()), "valid ECDH SPKI");
            Check(!verifier.VerifyHello(Edit(hello, p => p["kex_spki"] = Convert.ToBase64String(kex.ExportSubjectPublicKeyInfo())), hc).Success, "KEX without AEAD rejected");
        }
        byte[] spki = Convert.FromBase64String(a.PublicSpkiBase64);
        using (var key = V2Crypto.OpenCanonicalSigningKey(spki)) Check(key is not null, "canonical signing SPKI");
        Check(V2Crypto.OpenCanonicalSigningKey(spki.Concat(new byte[] { 0 }).ToArray()) is null, "SPKI trailing bytes");
        using (var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384))
        {
            Check(V2Crypto.OpenCanonicalSigningKey(p384.ExportSubjectPublicKeyInfo()) is null, "wrong curve rejected");
            Reject(() => V2Crypto.Sign(p384, "x"u8), "wrong curve cannot sign packet");
        }
        // Signature malleability is allowed by the contract: both s and n-s must verify, not become deduplication identifiers.
        byte[] highS = Edit(message, p => {
            byte[] sig = Convert.FromBase64String(p["sig"]!.GetValue<string>());
            var n = BigInteger.Parse("0FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551", System.Globalization.NumberStyles.HexNumber);
            byte[] other = (n - new BigInteger(sig.AsSpan(32), true, true)).ToByteArray(true, true);
            Array.Clear(sig, 32, 32); other.CopyTo(sig, 64 - other.Length); p["sig"] = Convert.ToBase64String(sig);
        });
        Check(verifier.VerifyMessage(highS, context).Success && verifier.VerifyMessage(highS, context).Value?.ContentFingerprint == fingerprint, "high/low S accepted stable content fingerprint");

        byte[] tampered = Edit(message, p => p["text"] = "tampered");
        var bad = verifier.VerifyMessage(tampered, context);
        Check(bad.Code == "bad_signature" && bad.Value is null && nonces.PendingCount == 1, "bad real signature yields no consumable object or nonce mutation");
        var otherContext = new V2IncomingContext(b.PeerId, new("paired", b.PublicSpkiBase64));
        var wrongSender = verifier.VerifyMessage(b.Message(id, b.PeerId, nonce, "signed wrong sender"), otherContext);
        Check(wrongSender.Success && !nonces.TryConsume(wrongSender.Value!) && nonces.PendingCount == 1, "valid wrong sender cannot burn challenge");
        Check(verifier.VerifyMessage(message, context with { LocalRecipientId = a.PeerId }).Code == "wrong_recipient" && nonces.PendingCount == 1, "wrong recipient yields no consumable object");
        using (var gate = new ManualResetEventSlim())
        {
            var work = Enumerable.Range(0, 32).Select(_ => Task.Factory.StartNew(() => { if (!gate.Wait(TimeSpan.FromSeconds(8))) throw new TimeoutException(); return nonces.TryConsume(good.Value!); },
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
            gate.Set(); var results = await Task.WhenAll(work).WaitAsync(TimeSpan.FromSeconds(10));
            Check(results.Count(v => v) == 1 && nonces.PendingCount == 0, "32 actual consumers exactly one wins");
        }
        Check(!nonces.TryConsume(verifier.VerifyMessage(highS, context).Value!), "different valid signature cannot replay nonce");
        nonce = nonces.Issue(a.PeerId); var validReceipt = verifier.VerifyReceipt(a.Receipt(id, b.PeerId, nonce), context).Value!;
        clock.Ticks = TimeSpan.FromSeconds(59.999).Ticks; clock.Utc = DateTimeOffset.UtcNow.AddYears(20);
        Check(nonces.TryConsume(validReceipt), "wall clock jump does not expire live receipt nonce");
        nonce = nonces.Issue(a.PeerId); validReceipt = verifier.VerifyReceipt(a.Receipt(id, b.PeerId, nonce), context).Value!;
        clock.Ticks += TimeSpan.FromSeconds(60).Ticks; clock.Utc = DateTimeOffset.UtcNow.AddYears(-20);
        Check(!nonces.TryConsume(validReceipt), "exact TTL expired despite wall clock rollback");
        _ = nonces.Issue(a.PeerId); Check(nonces.PendingCount == 1, "issue purges expired pending challenge");
        var bounded = new V2NonceStore(); string oldest = bounded.Issue(a.PeerId);
        for (int i = 0; i < 511; i++) _ = bounded.Issue(a.PeerId);
        string newest = bounded.Issue(a.PeerId);
        Check(bounded.PendingCount == 512 && !bounded.TryConsume(verifier.VerifyMessage(a.Message(id, b.PeerId, oldest, "old"), context).Value!) &&
            bounded.TryConsume(verifier.VerifyMessage(a.Message(id, b.PeerId, newest, "new"), context).Value!), "513th challenge evicts oldest only");
        var collision = new V2NonceStore(clock, 1, TimeSpan.FromSeconds(60), () => new byte[16]); string retained = collision.Issue(a.PeerId);
        Reject(() => collision.Issue(a.PeerId), "entropy collisions terminate boundedly");
        Check(collision.PendingCount == 1 && collision.TryConsume(verifier.VerifyMessage(a.Message(id, b.PeerId, retained, "retained"), context).Value!), "failed allocation does not evict existing nonce");
        string full = new('a', 16384);
        Check(verifier.VerifyMessage(a.Message(id, b.PeerId, nonce, full), context).Success, "actual packet inclusive L");
        Reject(() => a.Message(id, b.PeerId, nonce, full + "a"), "writer L plus one rejected");
        Check(verifier.VerifyMessage(b.Message(id, b.PeerId, nonce, full + "a"), otherContext).Code == "payload_too_large", "receiver independent explicit L enforced");
        Check(verifier.VerifyMessage(new byte[verifier.MessageLimits.MaxBytes + 1], context).Code == "payload_too_large", "raw body bound before parse");
        foreach (string value in new[] { id + "\n", id.ToUpperInvariant(), "../../x" }) Reject(() => a.Message(value, b.PeerId, nonce, "x"), "writer bad id " + checks);
        Reject(() => a.Message(id, b.PeerId, nonce, "x", "too-fast\n"), "writer signed LF reaction refused");
        Reject(() => a.MessageAck("stored\n", id, b.PeerId, fingerprint), "writer signed LF ACK refused");
        Reject(() => a.ReceiptAck("invalid", id, b.PeerId), "writer invalid receipt ACK refused");
        var disposed = new V2PacketWriter(ECDsa.Create(ECCurve.NamedCurves.nistP256), 16384); disposed.Dispose(); disposed.Dispose();
        Reject(() => disposed.HelloReply(cn, nonce), "disposed writer cannot sign");
        Console.WriteLine($"Product V2 raw/crypto/concurrency boundaries: {checks} checks."); return checks;
    }
    private static byte[] Edit(byte[] raw, Action<JsonObject> mutation)
    { var p = JsonNode.Parse(raw)!.AsObject(); mutation(p); return JsonSerializer.SerializeToUtf8Bytes(p); }
    private sealed class Clock : TimeProvider
    {
        internal long Ticks;
        internal DateTimeOffset Utc = DateTimeOffset.UtcNow;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Ticks;
        public override DateTimeOffset GetUtcNow() => Utc;
    }
}
