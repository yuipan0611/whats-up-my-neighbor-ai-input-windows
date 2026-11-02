using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NekoChat.Core.V2;

namespace NekoChat.Tests;

internal static class V2VectorChecks
{
    public static async Task<int> Run()
    {
        int checks = 0, vectors = 0;
        void Check(bool ok, string id) { checks++; if (!ok) throw new Exception("Product V2 vector mismatch: " + id); }
        using var file = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "V2", "vectors-v3.json")));
        var root = file.RootElement; var verifier = new V2Verifier(16384);
        foreach (var c in root.GetProperty("cases").EnumerateArray())
        {
            string id = S(c, "id"), group = S(c, "group"); var expect = c.GetProperty("expect"); bool ok;
            if (group == "text_rules")
            {
                string text = Expand(c.GetProperty("text")); var problem = V2Text.Check(text, 16384);
                string reason = problem switch { V2TextProblem.None => "", V2TextProblem.InvalidUtf16 => "invalid_utf16", V2TextProblem.ControlCharacter => "control_char", V2TextProblem.Blank => "blank", _ => "too_large" };
                ok = (problem == V2TextProblem.None) == expect.GetProperty("valid").GetBoolean() && reason == S(expect, "reason") &&
                    (!expect.TryGetProperty("utf8_bytes", out var length) || Encoding.UTF8.GetByteCount(text) == length.GetInt32());
            }
            else if (group == "content_fingerprint")
            {
                var p = c.GetProperty("input"); ok = V2Signing.Fingerprint(S(p, "recipient_id"), Expand(p.GetProperty("text")), SN(p, "reaction")) == S(expect, "fingerprint_hex");
            }
            else if (group == "nonce_store") ok = await NonceVector(c, root.GetProperty("keys"));
            else
            {
                var p = c.GetProperty("presented"); var context = c.GetProperty("context"); byte[] wire = Wire(p, group); string code; string? level = null;
                switch (group)
                {
                    case "hello.ok":
                        var hello = verifier.VerifyHello(wire, new(S(context, "client_nonce"), new(SN(context, "peer_state"), SN(context, "peer_public_key")), Strings(context, "required_capabilities")));
                        code = hello.Code; level = hello.Value?.EvidenceLevel; break;
                    case "message.send": code = verifier.VerifyMessage(wire, new(S(context, "local_recipient_id"), new(SN(context, "peer_state"), SN(context, "peer_public_key")))).Code; break;
                    case "receipt": code = verifier.VerifyReceipt(wire, new(S(context, "local_recipient_id"), new(SN(context, "peer_state"), SN(context, "peer_public_key")))).Code; break;
                    case "message.ack": code = verifier.VerifyMessageAck(wire, AckContext(context)).Code; break;
                    case "receipt.ack": code = verifier.VerifyReceiptAck(wire, AckContext(context)).Code; break;
                    default: throw new InvalidDataException("Unknown fixed vector group.");
                }
                byte[] input = SigningInput(group, p, context);
                ok = code == S(expect, "result") && (!expect.TryGetProperty("level", out var wantLevel) || level == wantLevel.GetString()) &&
                    Convert.ToHexString(input).ToLowerInvariant() == S(c, "verifier_signing_input_hex") && Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant() == S(c, "verifier_sha256_hex");
                if (!ok) Console.WriteLine($"V2 fixed {id}: result={code} wanted={S(expect, "result")} level={level ?? "none"}");
            }
            Check(ok, id); vectors++;
        }
        Check(vectors == 269, "all original fixed cases executed");
        Console.WriteLine($"Product V2 fixed vectors: {vectors}/269 passed through product APIs; {checks} checks."); return checks;
    }
    internal static byte[] Wire(JsonElement p, string kind)
    {
        var b = new StringBuilder("{\"protocol\":\"nekochat\",\"version\":2,\"kind\":").Append(JsonSerializer.Serialize(kind));
        if (kind is "hello.ok" or "message.send") b.Append(",\"cert\":null");
        foreach (var member in p.EnumerateObject())
        {
            string key = member.Name == "max_text_bytes_raw" ? "max_text_bytes" : member.Name;
            b.Append(',').Append(JsonSerializer.Serialize(key)).Append(':');
            if (member.Name == "max_text_bytes_raw") b.Append(member.Value.GetString());
            else if (member.Name == "text") b.Append(Quote(Expand(member.Value)));
            else b.Append(member.Value.GetRawText());
        }
        return Encoding.UTF8.GetBytes(b.Append('}').ToString());
    }
    internal static string Quote(string text)
    {
        var b = new StringBuilder("\"");
        foreach (char c in text) b.Append("\\u").Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
        return b.Append('"').ToString();
    }
    private static V2AckContext AckContext(JsonElement c) => new(S(c, "expected_message_id"), S(c, "expected_sender_id"), S(c, "pinned_receiver_spki"), SN(c, "expected_fingerprint"));
    private static byte[] SigningInput(string group, JsonElement p, JsonElement c) => group switch
    {
        "hello.ok" => V2Signing.Hello(S(p, "receiver_id"), S(c, "client_nonce"), S(p, "nonce"), Strings(p, "capabilities"), S(p, "max_text_bytes_raw"), SN(p, "kex_spki")),
        "message.send" => V2Signing.Message(S(p, "message_id"), S(p, "sender_id"), S(p, "recipient_id"), S(p, "nonce"), Expand(p.GetProperty("text")), SN(p, "reaction")),
        "receipt" => V2Signing.Receipt(S(p, "message_id"), S(p, "sender_id"), S(p, "recipient_id"), S(p, "nonce"), S(p, "state")),
        "message.ack" => V2Signing.MessageAck(S(p, "status"), S(p, "message_id"), S(p, "sender_id"), S(p, "receiver_id"), S(p, "content_fingerprint")),
        _ => V2Signing.ReceiptAck(S(p, "status"), S(p, "message_id"), S(p, "sender_id"), S(p, "receiver_id"))
    };
    private static string Expand(JsonElement spec)
    {
        if (spec.TryGetProperty("literal", out var literal)) return literal.GetString()!;
        if (spec.TryGetProperty("repeat", out var repeat)) return string.Concat(Enumerable.Repeat(repeat.GetString()!, spec.GetProperty("count").GetInt32()));
        byte[] bytes = Convert.FromHexString(spec.GetProperty("utf16_hex").GetString()!); var text = new StringBuilder();
        for (int i = 0; i < bytes.Length; i += 2) text.Append((char)((bytes[i] << 8) | bytes[i + 1]));
        return text.ToString();
    }
    private static async Task<bool> NonceVector(JsonElement c, JsonElement keys)
    {
        var clock = new Clock(); string next = "";
        byte[] Entropy(string label) => SHA256.HashData(Encoding.UTF8.GetBytes(label))[..16];
        string Nonce(string label) => V2Format.ToBase64Url(Entropy(label));
        string Sender(string label) => S(keys.GetProperty(label == "X" ? "X_attacker" : "A_sender"), "peer_id");
        var store = new V2NonceStore(clock, c.GetProperty("max_pending").GetInt32(), TimeSpan.FromSeconds(c.GetProperty("ttl_seconds").GetInt32()), () => Entropy(next));
        var steps = c.GetProperty("steps").EnumerateArray().ToArray(); var results = new string[steps.Length];
        // N-04 lists independent observations at61 then59; execute by event time and retain original result indices.
        foreach (var item in steps.Select((step, index) => (Step: step, Index: index)).OrderBy(x => x.Step.GetProperty("t").GetDouble()).ThenBy(x => x.Index))
        {
            var s = item.Step; clock.Ticks = TimeSpan.FromSeconds(s.GetProperty("t").GetDouble()).Ticks;
            string nonce = Nonce(S(s, "nonce")), sender = Sender(S(s, "sender"));
            switch (S(s, "op"))
            {
                case "issue": next = S(s, "nonce"); results[item.Index] = store.Issue(sender) == nonce ? "issued" : "wrong-nonce"; break;
                case "consume":
                    // The abstract fixture's sig_ok=false means the verifier rejected before nonce consumption. Actual crypto-gate tests are separate.
                    results[item.Index] = !s.GetProperty("sig_ok").GetBoolean() ? "bad_signature" : store.TryConsumeVerified(nonce, sender) ? "ok" : "stale_nonce"; break;
                case "consume_race":
                    using (var gate = new ManualResetEventSlim())
                    {
                        var work = Enumerable.Range(0, s.GetProperty("parties").GetInt32()).Select(_ => Task.Factory.StartNew(() => {
                            if (!gate.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("nonce vector gate"); return store.TryConsumeVerified(nonce, sender);
                        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
                        gate.Set(); var consumed = await Task.WhenAll(work).WaitAsync(TimeSpan.FromSeconds(8));
                        results[item.Index] = consumed.Count(v => v) == 1 ? "exactly_one_ok" : "wrong-race-count";
                    }
                    break;
            }
        }
        return results.SequenceEqual(Strings(c.GetProperty("expect"), "results"));
    }
    private static string S(JsonElement e, string key) => SN(e, key) ?? "";
    private static string? SN(JsonElement e, string key) => e.TryGetProperty(key, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;
    private static string[] Strings(JsonElement e, string key) => e.GetProperty(key).EnumerateArray().Select(v => v.GetString()!).ToArray();
    private sealed class Clock : TimeProvider
    {
        internal long Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Ticks;
    }
}
