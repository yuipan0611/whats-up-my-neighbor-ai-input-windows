using System.Security.Cryptography;
using System.Text;
using NekoChat.Core;
using NekoChat.Core.Storage;
using NekoChat.Core.V2;

namespace NekoChat.Tests;

/// <summary>
/// NCM2 / D-06 (user decision 2026-11-03: "改為 64 KiB 上限") and CM-07 (PM engineering default).
/// v2 body text: exactly 65,536 UTF-8 bytes sends, 65,537 is rejected unchanged (never truncated, draft kept).
/// v1 recipients: existing 2,000 Unicode scalar values with the notice "對方是舊版，最多 2,000 字".
/// The boundary suite is run against the product rule and against deliberately wrong rules (negative control).
/// </summary>
internal static class Limit64kChecks
{
    private const int L = V2Text.ProductMaxUtf8Bytes;
    private const string Cat = "\U0001F408";   // 4 UTF-8 bytes, surrogate pair in UTF-16
    private const string Han = "貓";       // 3 UTF-8 bytes

    /// <summary>Boundary cases every "accepts(text, L)" implementation must satisfy. Returns the first failing case name, or null.</summary>
    internal static string? BoundarySuite(Func<string, int, bool> accepts)
    {
        string han21845 = string.Concat(Enumerable.Repeat(Han, 21845)), cats16383 = string.Concat(Enumerable.Repeat(Cat, 16383));
        var cases = new (string Name, string Text, bool Expect)[]
        {
            ("ascii exactly L", new string('a', L), true),
            ("ascii L+1", new string('a', L + 1), false),
            ("ascii L-1", new string('a', L - 1), true),
            ("3-byte chars 21845 = 65535", han21845, true),
            ("3-byte chars 21845 + a = L", han21845 + "a", true),
            ("3-byte chars 21845 + aa = L+1", han21845 + "aa", false),
            ("3-byte chars 21846 = 65538", han21845 + Han, false),
            ("4-byte chars 16384 = exactly L", cats16383 + Cat, true),
            ("4-byte chars 16384 + a = L+1", cats16383 + Cat + "a", false),
            ("4-byte char ends exactly at L", new string('a', L - 4) + Cat, true),
            ("4-byte char straddles L (1 byte over)", new string('a', L - 3) + Cat, false),
            ("4-byte char straddles L (3 bytes over)", new string('a', L - 1) + Cat, false),
            ("16383 cats + 4 ascii = L", cats16383 + "aaaa", true),
            ("16383 cats + 3-byte + a = L", cats16383 + Han + "a", true),
            ("16383 cats + 3-byte + aa = L+1", cats16383 + Han + "aa", false),
            ("old 16,384 value is no longer the limit", new string('a', 16385), true),
            ("LF counts one byte", new string('\n', L - 1) + "a", true),
        };
        foreach (var c in cases) if (accepts(c.Text, L) != c.Expect) return c.Name;
        return null;
    }

    public static async Task<int> Run()
    {
        int checks = 0;
        void Check(bool ok, string name) { checks++; if (!ok) throw new Exception("64KiB limit: " + name); }

        // ---- constants ----
        Check(L == 65536 && V2Text.DefaultMaxUtf8Bytes == 65536, "product L is 65,536 bytes (D-06)");
        Check(MessageLimitPolicy.LegacyMaxScalars == 2000 && MessageLimitPolicy.LegacyTooLargeNotice == "對方是舊版，最多 2,000 字", "CM-07 constants");

        // ---- real rule vs boundary suite ----
        string? failV2 = BoundarySuite((t, l) => V2Text.Check(t, l) == V2TextProblem.None);
        Check(failV2 is null, "V2Text.Check boundary case failed: " + failV2);
        string? failPolicy = BoundarySuite((t, l) => MessageLimitPolicy.Check(t, false, l) == MessageLimitVerdict.Ok);
        Check(failPolicy is null, "MessageLimitPolicy (v2 recipient) boundary case failed: " + failPolicy);
        Check(V2Text.Check(new string('a', L + 1), L) == V2TextProblem.TooLarge && MessageLimitPolicy.Check(new string('a', L + 1), false) == MessageLimitVerdict.V2TooLarge, "L+1 reports TooLarge");
        Check(V2Text.Check(new string('a', 16385), 16384) == V2TextProblem.TooLarge, "an explicitly smaller L is still enforced (injectable)");

        // ---- negative control: each wrong rule must be caught by the same suite ----
        var mutants = new (string Name, Func<string, int, bool> Accepts)[]
        {
            ("off-by-one: L itself rejected", (t, l) => Encoding.UTF8.GetByteCount(t) < l),
            ("off-by-one: L+1 accepted", (t, l) => Encoding.UTF8.GetByteCount(t) <= l + 1),
            ("counts UTF-16 chars, not bytes", (t, l) => t.Length <= l),
            ("counts Unicode scalars, not bytes", (t, l) => t.EnumerateRunes().Count() <= l),
            ("still the old 16,384 limit", (t, l) => Encoding.UTF8.GetByteCount(t) <= 16384),
            ("truncates instead of rejecting", (t, l) => true),
            ("counts 3-byte-or-more chars as 3 bytes", (t, l) => t.Sum(c => c < 0x80 ? 1 : c < 0x800 ? 2 : 3) <= l),
        };
        foreach (var (name, accepts) in mutants) Check(BoundarySuite(accepts) is not null, "negative control catches mutant: " + name);

        // ---- frame / JSON body bound contains L even with worst-case escaping, and is still finite ----
        var limits = V2JsonLimits.Message(L);
        Check(limits.MaxBytes == 6 * L + 4096 && limits.MaxBytes == 397312, "body bound is 6*L+4096 = 397,312 bytes");
        Check(new V2Verifier(L).MessageLimits.MaxBytes == limits.MaxBytes, "verifier uses the same bound");
        Check(limits.MaxBytes < 1_000_000 && V2JsonLimits.Message(L * 2).MaxBytes > limits.MaxBytes, "bound is finite and tracks L");

        using var a = new V2PacketWriter(ECDsa.Create(ECCurve.NamedCurves.nistP256), L);
        using var b = new V2PacketWriter(ECDsa.Create(ECCurve.NamedCurves.nistP256), L);
        var verifier = new V2Verifier(L); var context = new V2IncomingContext(b.PeerId, new("paired", a.PublicSpkiBase64));
        var nonces = new V2NonceStore(); string nonce = nonces.Issue(a.PeerId);
        foreach (var (name, text) in new[]
        {
            ("ASCII a x L", new string('a', L)),
            ("'<' x L (escaped, 6 bytes each on the wire)", new string('<', L)),
            ("'\"' x L (escaped)", new string('"', L)),
            ("CJK x 21845 + a", string.Concat(Enumerable.Repeat(Han, 21845)) + "a"),
            ("emoji x 16384", string.Concat(Enumerable.Repeat(Cat, 16384))),
        })
        {
            byte[] packet = a.Message(V2PacketWriter.NewMessageId(), b.PeerId, nonce, text);
            var verified = verifier.VerifyMessage(packet, context);
            Check(packet.Length <= limits.MaxBytes, $"{name}: packet {packet.Length} fits in {limits.MaxBytes}");
            Check(verified.Success && verified.Value!.Text == text, $"{name}: real signed packet verifies with untruncated text");
        }
        byte[] worst = a.Message(V2PacketWriter.NewMessageId(), b.PeerId, nonce, new string('<', L));
        Check(worst.Length > 5 * L && worst.Length < limits.MaxBytes, "escaping really expands: worst case is above 5*L yet inside the bound");
        foreach (var over in new[] { new string('a', L + 1), new string('<', L + 1), string.Concat(Enumerable.Repeat(Cat, 16384)) + "a", new string('a', L - 3) + Cat })
        {
            bool refused = false; try { _ = a.Message(V2PacketWriter.NewMessageId(), b.PeerId, nonce, over); } catch (ArgumentException) { refused = true; }
            Check(refused, "writer refuses L+1 (no truncated packet is produced)");
        }
        using var big = new V2PacketWriter(ECDsa.Create(ECCurve.NamedCurves.nistP256), L + 4);
        byte[] bigPacket = big.Message(V2PacketWriter.NewMessageId(), b.PeerId, nonces.Issue(big.PeerId), new string('a', L + 1));
        Check(verifier.VerifyMessage(bigPacket, new V2IncomingContext(b.PeerId, new("paired", big.PublicSpkiBase64))).Code == "payload_too_large", "receiver rejects L+1 from a sender whose own L is larger");
        Check(verifier.VerifyMessage(new byte[limits.MaxBytes + 1], context).Code == "payload_too_large", "raw body one byte over the bound is refused before parsing");

        // ---- store: L accepted, L+1 refused and the draft stays byte-for-byte ----
        string root = Path.Combine(Path.GetTempPath(), "neko-limit64k-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            using var store = ChatStore.Open(new(root) { TextValidator = t => V2Text.Check(t, L) == V2TextProblem.None });
            store.EnsureLocalIdentity(b.PeerId, Convert.FromBase64String(b.PublicSpkiBase64), "fixture");
            string conversation = ((PeerRegistered)store.RegisterPeer(a.PeerId, PairingState.Paired, Convert.FromBase64String(a.PublicSpkiBase64))).Conversation.ConversationId;
            string tooLong = new string('a', L - 3) + Cat;
            store.SaveDraft(conversation, tooLong, 0, 1);
            Check(store.BeginManualSend(conversation, tooLong, 0, 1) is InvalidSend && store.GetDraftState(conversation)!.RawText == tooLong, "L+1 refused by the store; draft kept byte-for-byte, not truncated");
            string exact = new string('a', L - 4) + Cat;
            store.SaveDraft(conversation, exact, 0, 2);
            Check(store.BeginManualSend(conversation, exact, 0, 2) is AttemptStarted, "exactly L accepted by the store");
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

        // ---- v1 recipient: 2,000 scalar values, notice, nothing sent ----
        string cats2000 = string.Concat(Enumerable.Repeat(Cat, 2000)), cats2001 = cats2000 + Cat;
        Check(Encoding.UTF8.GetByteCount(cats2001) < L, "fixture: 2001 scalars are far below the v2 byte limit");
        Check(MessageLimitPolicy.Check(cats2000, true) == MessageLimitVerdict.Ok && MessageLimitPolicy.Check(cats2001, true) == MessageLimitVerdict.LegacyTooLarge, "legacy: 2000 scalars ok, 2001 refused");
        Check(MessageLimitPolicy.Check(cats2001, false) == MessageLimitVerdict.Ok, "the same text is fine for a v2 recipient");
        Check(MessageLimitPolicy.Check(new string('a', 2000), true) == MessageLimitVerdict.Ok && MessageLimitPolicy.Check(new string('a', 2001), true) == MessageLimitVerdict.LegacyTooLarge, "legacy counts scalars, ASCII included");
        Check(MessageLimitPolicy.Check("  ", true) == MessageLimitVerdict.Invalid && MessageLimitPolicy.Check(null, true) == MessageLimitVerdict.Invalid && MessageLimitPolicy.Check(" ", false) == MessageLimitVerdict.Invalid, "blank text is Invalid for both");
        bool notice = false;
        try { await DirectLanMessaging.SendDirectAsync("127.0.0.1", cats2001, 1); }
        catch (ArgumentException e) { notice = e.Message.Contains(MessageLimitPolicy.LegacyTooLargeNotice, StringComparison.Ordinal); }
        catch (Exception) { }
        Check(notice, "SendDirectAsync refuses 2001 scalars with the old-version notice (ArgumentException, before any connection)");
        // negative control for the legacy rule: counting UTF-16 chars or bytes gives the wrong answer on these texts
        Check(cats2000.Length == 4000 && Encoding.UTF8.GetByteCount(cats2000) == 8000, "fixture: 2000 cats are 4000 UTF-16 chars and 8000 bytes");
        Check(!(cats2000.Length <= 2000) && Encoding.UTF8.GetByteCount(cats2001) <= L, "negative control: UTF-16 counting would wrongly refuse 2000 scalars; the byte rule would wrongly accept 2001");

        Console.WriteLine($"64 KiB message limit (D-06) and legacy 2,000-scalar rule (CM-07): {checks} checks.");
        return checks;
    }
}
