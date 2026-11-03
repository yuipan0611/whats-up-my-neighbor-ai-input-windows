using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NekoChat.Core.Classroom.Wire;
using NekoChat.Core.V2;

namespace NekoChat.Tests;

/// <summary>
/// NCMWC: the frozen NCMW-v2 vectors (711 cases, SHA-256 22dbf4dc…3d) are fed into the REAL product API in NekoChat.Core/Classroom/Wire.
/// Beyond the vectors: writer → validator round trips, real concurrent session consumption, and writer refusals.
/// Product-level negative controls live in ClassroomWire/product-mutants.py (they edit the product source and expect this suite to fail).
/// </summary>
internal static class ClassroomWireVectorChecks
{
    internal const string VectorsSha256 = "22dbf4dc95e53a7c9cf3fa0dc3b399d951786c955dbe5194d3963a171cee083d";
    internal static string VectorsPath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Classroom", "ncmw-vectors-v2.json");

    internal static byte[] Segments(JsonElement segs)
    {
        using var ms = new MemoryStream();
        foreach (var g in segs.EnumerateArray())
        {
            if (g.TryGetProperty("s", out var s)) ms.Write(Encoding.UTF8.GetBytes(s.GetString()!));
            else if (g.TryGetProperty("rep", out var rep)) { byte[] unit = Encoding.UTF8.GetBytes(rep.GetString()!); for (int i = 0, n = g.GetProperty("n").GetInt32(); i < n; i++) ms.Write(unit); }
            else ms.Write(Convert.FromHexString(g.GetProperty("hex").GetString()!.Replace(" ", "")));
        }
        return ms.ToArray();
    }

    private sealed class Clock : TimeProvider
    {
        public long Ms;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Ms;
    }
    private sealed class DictLookup(JsonElement sessions) : IClassroomSessionLookup
    {
        public bool TryGet(string session, out ClassroomSessionState state)
        {
            if (sessions.TryGetProperty(session, out var s)) { state = new(s.GetProperty("source").GetString()!, TimeSpan.FromMilliseconds(s.GetProperty("age_ms").GetInt64())); return true; }
            state = default; return false;
        }
    }

    internal static ClassroomRequestKind RequestKind(string route) => route switch { "hello" => ClassroomRequestKind.Hello, "message" => ClassroomRequestKind.Message, _ => ClassroomRequestKind.Receipt };
    internal static ClassroomResponseKind ResponseKind(string route) => route switch { "hello" => ClassroomResponseKind.HelloOk, "message" => ClassroomResponseKind.MessageAck, _ => ClassroomResponseKind.ReceiptAck };

    internal static ClassroomResponseContext ResponseContext(JsonElement c)
    {
        var sent = c.GetProperty("sent");
        string? reaction = sent.GetProperty("reaction").ValueKind == JsonValueKind.Null ? null : sent.GetProperty("reaction").GetString();
        string? need = c.GetProperty("required_capability").ValueKind == JsonValueKind.Null ? null : c.GetProperty("required_capability").GetString();
        return new(c.GetProperty("target_ip").GetString()!, c.GetProperty("own_ip").GetString()!,
            new(sent.GetProperty("client_nonce").GetString()!, sent.GetProperty("session").GetString()!, sent.GetProperty("message_id").GetString()!, reaction,
                new UTF8Encoding(false, true).GetString(Segments(sent.GetProperty("text")))), need);
    }

    public static Task<int> Run()
    {
        int checks = 0;
        void Check(bool ok, string name) { checks++; if (!ok) throw new Exception("Classroom wire: " + name); }
        byte[] raw = File.ReadAllBytes(VectorsPath);
        Check(Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant() == VectorsSha256, "fixture is the frozen NCMW-v2 vectors.json");
        using var doc = JsonDocument.Parse(raw);
        var failures = new List<string>(); int total = 0, roundTrips = 0, acceptedVectors = 0;
        var byKind = new Dictionary<string, int>();

        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            total++; string id = c.GetProperty("id").GetString()!, kind = c.GetProperty("kind").GetString()!; var expect = c.GetProperty("expect");
            byKind[kind] = byKind.GetValueOrDefault(kind) + 1;
            string got, want;
            try
            {
                switch (kind)
                {
                    case "request":
                    {
                        string route = c.GetProperty("route").GetString()!; var ctx = c.GetProperty("ctx");
                        var lookup = new DictLookup(ctx.GetProperty("sessions")); string local = ctx.GetProperty("local_ip").GetString()!, source = ctx.GetProperty("source_ip").GetString()!;
                        var r = ClassroomRequestValidator.Validate(RequestKind(route), Segments(c.GetProperty("body")), local, source, lookup);
                        got = route == "hello" ? r.Code : r.Code + "/" + (r.Ok ? "consumed" : "kept");
                        want = expect.GetProperty("result").GetString()! + (expect.TryGetProperty("session_after", out var after) ? "/" + after.GetString() : "");
                        if (r.Ok) { roundTrips++; if (!RoundTripRequest(r.Request!, local, source, lookup)) got += " (round trip failed)"; }
                        break;
                    }
                    case "response":
                    {
                        string route = c.GetProperty("route").GetString()!; var context = ResponseContext(c.GetProperty("ctx"));
                        byte[] body = Segments(c.GetProperty("body")); int status = c.GetProperty("status").GetInt32();
                        var r = ClassroomResponseValidator.Validate(ResponseKind(route), status, body, context);
                        got = r.Label; want = expect.GetProperty("result").GetString()!;
                        if (r.Ok) { roundTrips++; if (!RoundTripResponse(route, r, context)) got += " (round trip failed)"; }
                        break;
                    }
                    case "head":
                    {
                        got = ClassroomHead.Check(c.GetProperty("head").GetString()!).Label;
                        want = expect.GetProperty("result").GetString()! == "ok" ? $"ok|{expect.GetProperty("route").GetString()}|{expect.GetProperty("length").GetInt64()}" : expect.GetProperty("result").GetString()!;
                        break;
                    }
                    case "hash":
                        got = ClassroomWire.ContentSha256(c.GetProperty("message_id").GetString()!, c.GetProperty("reaction").ValueKind == JsonValueKind.Null ? null : c.GetProperty("reaction").GetString(),
                            new UTF8Encoding(false, true).GetString(Segments(c.GetProperty("text"))));
                        want = expect.GetProperty("sha256").GetString()!; break;
                    case "source": got = ClassroomWire.CanonicalSource(c.GetProperty("address").GetString()) ?? "reject"; want = expect.GetProperty("result").GetString()!; break;
                    case "source_policy": got = ClassroomWire.AdmitSource(c.GetProperty("address").GetString()) is null ? "reject" : "accept"; want = expect.GetProperty("result").GetString()!; break;
                    case "legacy_probe":
                        got = ClassroomLegacyProbe.Classify(c.GetProperty("status").GetInt32(), Segments(c.GetProperty("body")), c.GetProperty("message_possibly_written").GetBoolean()) == LegacyProbeResult.LegacyCandidate ? "legacy_candidate" : "not_legacy";
                        want = expect.GetProperty("result").GetString()!; break;
                    case "session_store":
                    {
                        var clock = new Clock();
                        var table = new ClassroomSessionTable(clock, c.GetProperty("capacity").GetInt32(), c.GetProperty("per_source").GetInt32(), TimeSpan.FromMilliseconds(c.GetProperty("ttl_ms").GetInt64()));
                        var outcomes = new List<string>();
                        foreach (var e in c.GetProperty("events").EnumerateArray())
                        {
                            clock.Ms = e.GetProperty("at").GetInt64(); string op = e.GetProperty("op").GetString()!;
                            if (op == "clear") { table.Clear(); outcomes.Add("cleared"); }
                            else if (op == "issue") outcomes.Add(table.Add(e.GetProperty("id").GetString()!, e.GetProperty("source").GetString()!) == ClassroomSessionAdd.Issued ? "issued" : "collision");
                            else outcomes.Add(table.TryConsume(e.GetProperty("id").GetString()!, e.GetProperty("source").GetString()!) ? "ok" : "stale");
                        }
                        got = string.Join(",", outcomes); want = string.Join(",", expect.GetProperty("outcomes").EnumerateArray().Select(x => x.GetString())); break;
                    }
                    default: got = "unknown kind " + kind; want = ""; break;
                }
            }
            catch (Exception e) { got = "CRASH " + e.GetType().Name + ": " + e.Message; want = "?"; }
            if ((kind == "request" || kind == "response") && want.StartsWith("ok", StringComparison.Ordinal)) acceptedVectors++;
            if (got != want) failures.Add($"{id} ({kind}): expected {want}, got {got}");
        }
        Check(failures.Count == 0, $"{total - failures.Count}/{total} vectors agree with the product API; first failures: {string.Join(" | ", failures.Take(5))}");
        Check(total == 711 && byKind["request"] == 277 && byKind["response"] == 291 && byKind["head"] == 66 && byKind["session_store"] == 11 && byKind["legacy_probe"] == 16 && byKind["source_policy"] == 26 && byKind["hash"] == 9 && byKind["source"] == 15, "all 711 vectors were executed (per-kind counts match the frozen file)");
        Check(acceptedVectors > 40 && roundTrips == acceptedVectors, $"writer → validator round trip for every accepted request/response vector ({roundTrips}/{acceptedVectors})");
        Console.WriteLine($"Classroom wire (NCMW-v2) product API: {total}/{total} vectors, {roundTrips} round trips.");

        // ---- beyond the vectors: writer, concurrency, size bound
        var errCodes = new[] { "invalid_payload", "unsupported_version", "payload_too_large", "rate_limited", "wrong_recipient", "stale_session", "conflict", "source_not_allowed", "capacity_exceeded", "receiver_not_ready", "storage_unavailable", "transient" };
        var ctxOk = new ClassroomResponseContext("10.0.0.2", "10.0.0.1", new(ClassroomPacketWriter.NewToken(), ClassroomPacketWriter.NewToken(), ClassroomPacketWriter.NewMessageId(), null, "x"), null);
        foreach (string code in errCodes)
        {
            var (status, body) = ClassroomPacketWriter.Error(code);
            var r = ClassroomResponseValidator.Validate(ClassroomResponseKind.MessageAck, status, body, ctxOk);
            Check(r.Label == "remote:" + code && ClassroomWire.TryErrorStatus(code, out int s2) && s2 == status, "error writer round trip " + code);
        }
        Check(Throws(() => ClassroomPacketWriter.Error("teapot")), "writer refuses an unknown error code");
        string id1 = ClassroomPacketWriter.NewMessageId(), tok = ClassroomPacketWriter.NewToken();
        Check(V2Format.IsNonce22(tok) && V2Format.IsUuid(id1), "generated tokens use the wire formats");
        Check(Throws(() => ClassroomPacketWriter.MessageSend(id1, tok, "10.0.0.2", new string('a', ClassroomWire.MaxTextBytes + 1))) &&
              Throws(() => ClassroomPacketWriter.MessageSend(id1.ToUpperInvariant(), tok, "10.0.0.2", "x")) && Throws(() => ClassroomPacketWriter.MessageSend(id1, tok, "10.0.0.02", "x")) &&
              Throws(() => ClassroomPacketWriter.MessageSend(id1, tok, "10.0.0.2", " \t")) && Throws(() => ClassroomPacketWriter.HelloRequest(tok + "A")) &&
              Throws(() => ClassroomPacketWriter.ReceiptAck("stored", id1, tok, "10.0.0.1", "10.0.0.2")) && Throws(() => ClassroomPacketWriter.MessageAck("ok", id1, tok, "10.0.0.1", "10.0.0.2", null, "x")),
            "writer refuses every invalid field instead of emitting a packet the receiver would reject");
        byte[] worst = ClassroomPacketWriter.MessageSend(id1, tok, "10.0.0.2", new string('<', ClassroomWire.MaxTextBytes));
        var nonceTable = new ClassroomSessionTable(); string session = nonceTable.Issue("10.0.0.1");
        byte[] worstWithSession = ClassroomPacketWriter.MessageSend(id1, session, "10.0.0.2", new string('<', ClassroomWire.MaxTextBytes));
        Check(worstWithSession.Length <= ClassroomWire.MessageBodyMaxBytes && worstWithSession.Length > 5 * ClassroomWire.MaxTextBytes &&
              ClassroomRequestValidator.Validate(ClassroomRequestKind.Message, worstWithSession, "10.0.0.2", "10.0.0.1", nonceTable).Ok, "writer's worst-case escaped 64 KiB message fits the body bound and validates");

        // real concurrent consumption: exactly one winner, wrong source never wins nor burns it
        for (int round = 0; round < 20; round++)
        {
            var table = new ClassroomSessionTable(); string sid = table.Issue("10.0.0.1"); int winners = 0; using var gate = new ManualResetEventSlim(false);
            var tasks = Enumerable.Range(0, 32).Select(i => Task.Run(() => { gate.Wait(); if (table.TryConsume(sid, i % 4 == 0 ? "10.0.0.9" : "10.0.0.1")) Interlocked.Increment(ref winners); })).ToArray();
            gate.Set(); Task.WaitAll(tasks);
            Check(winners == 1 && table.Count == 0, "32 concurrent consumers: exactly one wins (round " + round + ")");
        }
        var cleared = new ClassroomSessionTable(); string kept = cleared.Issue("10.0.0.1"); cleared.Clear();
        Check(!cleared.TryGet(kept, out _) && cleared.Count == 0, "Clear drops every session");
        checks += HeadBoundaryChecks();
        return Task.FromResult(checks);
    }

    /// <summary>VCMWC: Content-Length of 1–10 digits is legal and may exceed int.MaxValue; above the route limit it is payload_too_large, never invalid_payload.</summary>
    private static int HeadBoundaryChecks()
    {
        int checks = 0;
        static string Head(string path, string length) => $"POST {path} HTTP/1.1\r\nHost: h\r\nContent-Type: application/json\r\nContent-Length: {length}\r\n\r\n";
        var routes = new (string Path, long Limit, bool Classroom)[]
        {
            ("/nekochat/classroom/v1/hello", 8192, true), ("/nekochat/classroom/v1/receipt", 8192, true), ("/nekochat/classroom/v1/message", ClassroomWire.MessageBodyMaxBytes, true)
        };
        foreach (var (path, limit, _) in routes)
        {
            foreach (string over in new[] { (limit + 1).ToString(), "2147483647", "2147483648", "2147483649", "4294967295", "4294967296", "9999999999", "0009999999", "1000000000" })
            {
                var r = ClassroomHead.Check(Head(path, over)); checks++;
                if (long.Parse(over) > limit ? r.Code != ClassroomCodes.PayloadTooLarge : !r.Ok) throw new Exception($"Classroom wire head boundary: {path} Content-Length {over} → {r.Code}");
            }
            foreach (string fine in new[] { "1", limit.ToString(), (limit - 1).ToString(), "0000000001" })
            {
                var r = ClassroomHead.Check(Head(path, fine)); checks++;
                if (!r.Ok || r.Length != long.Parse(fine)) throw new Exception($"Classroom wire head boundary: {path} Content-Length {fine} → {r.Code}/{r.Length}");
            }
            foreach (string bad in new[] { "0", "0000000000", "-1", "+5", "12345678901", "99999999999", "", "1 2", "0x10" })
            {
                var r = ClassroomHead.Check(Head(path, bad)); checks++;
                if (r.Code != ClassroomCodes.InvalidPayload) throw new Exception($"Classroom wire head boundary: {path} Content-Length '{bad}' must be malformed, got {r.Code}");
            }
        }
        // Routes without a classroom limit: still never a malformed head for a legal 10-digit value; the value is simply too large for an int.
        foreach (string over in new[] { "2147483648", "9999999999" })
        {
            checks += 2;
            if (ClassroomHead.Check(Head("/nekochat/v1/direct", over)).Code != ClassroomCodes.PayloadTooLarge || ClassroomHead.Check(Head("/nekochat/v2/message", over)).Code != ClassroomCodes.PayloadTooLarge)
                throw new Exception("Classroom wire head boundary: oversize Content-Length on a non-classroom route");
        }
        return checks;
    }

    private static bool Throws(Action action) { try { action(); return false; } catch (ArgumentException) { return true; } }

    private static bool RoundTripRequest(ClassroomRequest r, string local, string source, IClassroomSessionLookup lookup)
    {
        byte[] again = r.Kind switch
        {
            ClassroomRequestKind.Hello => ClassroomPacketWriter.HelloRequest(r.ClientNonce!),
            ClassroomRequestKind.Message => ClassroomPacketWriter.MessageSend(r.MessageId!, r.Session!, r.ToIp!, r.Text!, r.Reaction),
            _ => ClassroomPacketWriter.ReceiptRequest(r.MessageId!, r.Session!, r.ToIp!)
        };
        var second = ClassroomRequestValidator.Validate(r.Kind, again, local, source, lookup);
        return second.Ok && second.Request == r;
    }

    private static bool RoundTripResponse(string route, ClassroomResponseResult r, ClassroomResponseContext c)
    {
        byte[] again = route switch
        {
            "hello" => ClassroomPacketWriter.HelloOk(c.Sent.ClientNonce, r.Hello!.Session, r.Hello.ReceiverIp, r.Hello.ObservedSenderIp, r.Hello.Capabilities),
            "message" => ClassroomPacketWriter.MessageAck(r.Status!, c.Sent.MessageId, c.Sent.Session, c.OwnIp, c.TargetIp, c.Sent.Reaction, c.Sent.Text),
            _ => ClassroomPacketWriter.ReceiptAck(r.Status!, c.Sent.MessageId, c.Sent.Session, c.OwnIp, c.TargetIp)
        };
        var second = ClassroomResponseValidator.Validate(ResponseKind(route), 200, again, c);
        return second.Ok && second.Status == r.Status;
    }
}
