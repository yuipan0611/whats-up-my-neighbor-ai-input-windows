using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NekoChat.Core.Pairing;
using NekoChat.Core.V2;

namespace NekoChat.Tests;

internal static class PairingWireChecks
{
    internal static int Run()
    {
        int checks = 0;
        void Check(bool good, string name) { checks++; if (!good) throw new Exception("Pairing wire: " + name); }
        bool Reject(Action action) { try { action(); return false; } catch (ArgumentException) { return true; } }
        using var vectors = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Pairing", "pair-vectors-v2.json")));
        foreach (var c in vectors.RootElement.GetProperty("cases").EnumerateArray())
        {
            string Text(string name) => c.GetProperty(name).GetString()!;
            bool good;
            if (Text("group") == "formula")
            {
                byte[] sid = Convert.FromHexString(Text("session_id_hex")), pi = Convert.FromBase64String(Text("pk_initiator_b64")), pr = Convert.FromBase64String(Text("pk_responder_b64")),
                    na = Convert.FromHexString(Text("na_hex")), nb = Convert.FromHexString(Text("nb_hex"));
                byte[] transcript = PairingCrypto.Transcript(sid, pi, pr, na, nb);
                good = V2Format.ToLowerHex(PairingCrypto.Commitment(sid, pi, pr, nb)) == Text("commit_hex") && V2Format.ToLowerHex(transcript) == Text("transcript_hex") && PairingCrypto.Sas(transcript) == Text("sas");
                foreach (var (field, role, key) in new[] { ("confirm_initiator", PairingRole.Initiator, pi), ("confirm_responder", PairingRole.Responder, pr) })
                {
                    if (!c.TryGetProperty(field, out var confirm)) continue;
                    good &= Encoding.UTF8.GetString(PairingCrypto.ConfirmationInput(role, transcript)) == confirm.GetProperty("signing_input_utf8").GetString() &&
                        PairingCrypto.Verify(key, role, transcript, Convert.FromBase64String(confirm.GetProperty("sig_b64").GetString()!)) == confirm.GetProperty("expect_verify").GetBoolean();
                }
                if (c.TryGetProperty("other_leg_transcript_hex", out var other)) good &= other.GetString() != V2Format.ToLowerHex(transcript);
            }
            else
            {
                byte[] bytes = Encoding.UTF8.GetBytes(c.GetProperty("message").GetRawText());
                good = PairingWire.TryParse(bytes, out var packet) == (c.GetProperty("expect").GetProperty("result").GetString() == "ok");
                if (packet is not null) good &= V2Format.IsNonce22(packet.SessionId);
            }
            Check(good, "frozen independent vector " + Text("id"));
        }
        using var a = ECDsa.Create(ECCurve.NamedCurves.nistP256); using var b = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] pkA = a.ExportSubjectPublicKeyInfo(), pkB = b.ExportSubjectPublicKeyInfo(); string spkiA = Convert.ToBase64String(pkA), spkiB = Convert.ToBase64String(pkB);
        string sidText = PairingWire.NewNonce(), naText = PairingWire.NewNonce(), nbText = PairingWire.NewNonce();
        V2Format.TryDecodeNonce22(sidText, out var sidBytes); V2Format.TryDecodeNonce22(naText, out var naBytes); V2Format.TryDecodeNonce22(nbText, out var nbBytes);
        byte[] th = PairingCrypto.Transcript(sidBytes, pkA, pkB, naBytes, nbBytes), commit = PairingCrypto.Commitment(sidBytes, pkA, pkB, nbBytes);
        var packets = new[] {
            (PairingKind.Request, PairingWire.Request(sidText, spkiA)), (PairingKind.Commitment, PairingWire.Commitment(sidText, commit)),
            (PairingKind.Nonce, PairingWire.Nonce(sidText, naText)), (PairingKind.Reveal, PairingWire.Reveal(sidText, spkiB, nbText)),
            (PairingKind.Pending, PairingWire.Pending(sidText)), (PairingKind.Cancel, PairingWire.Cancel(sidText, "superseded"))
        };
        foreach (var (kind, bytes) in packets) Check(PairingWire.TryParse(bytes, out var packet) && packet!.Kind == kind && packet.SessionId == sidText && bytes.Length <= PairingWire.MaxBodyBytes, "bounded writer/parser round trip " + kind);
        foreach (var role in new[] { PairingRole.Initiator, PairingRole.Responder })
        {
            byte[] bytes = PairingWire.Confirmation(sidText, role, th, a); Check(PairingWire.TryParse(bytes, out var p) && p!.Kind == PairingKind.Confirmation &&
                PairingCrypto.Verify(pkA, role, th, Convert.FromBase64String(p.SignatureBase64!)), "actual P256 signed confirmation " + role);
            PairingWire.TryParse(bytes, out var packet); byte[] signature = Convert.FromBase64String(packet!.SignatureBase64!); byte[] changed = th.ToArray(); changed[0] ^= 1;
            Check(!PairingCrypto.Verify(pkA, role == PairingRole.Initiator ? PairingRole.Responder : PairingRole.Initiator, th, signature) && !PairingCrypto.Verify(pkA, role, changed, signature) && !PairingCrypto.Verify(pkB, role, th, signature), "role/transcript/key reflection refused " + role);
        }
        byte[] alteredCommit = commit.ToArray(); alteredCommit[31] ^= 1;
        Check(PairingCrypto.MatchesCommitment(commit, commit.ToArray()) && !PairingCrypto.MatchesCommitment(commit, alteredCommit) && !PairingCrypto.MatchesCommitment(commit, new byte[31]), "fixed-length commitment equality rejects altered or short commitment");
        string request = Encoding.UTF8.GetString(packets[0].Item2);
        foreach (string version in new[] { "2.0", "2e0", "true", "\"2\"" })
            Check(!PairingWire.TryParse(Encoding.UTF8.GetBytes(request.Replace("\"version\":2", "\"version\":" + version)), out _), "version literal strict: " + version);
        foreach (string end in new[] { "\n", "\r", "\u0085", "\u2028", "\u2029" })
            Check(!PairingWire.TryParse(Encoding.UTF8.GetBytes(request.Replace(sidText, sidText + (end == "\n" ? "\\n" : end == "\r" ? "\\r" : end))), out _), "session whole-string rejects line terminator " + ((int)end[0]));
        foreach (string malformed in new[] {
            request.Replace("\"kind\":", "\"k\\u0069nd\":\"pair.request\",\"kind\":"), request.Replace("\"initiator_spki\":", "\"extra\":0,\"initiator_spki\":"),
            request + "{}", "[" + request + "]", request.Replace("\"kind\":\"pair.request\"", "\"kind\":null"), request.Replace("\"protocol\":\"nekochat\"", "\"protocol\":\"other\"")
        }) Check(!PairingWire.TryParse(Encoding.UTF8.GetBytes(malformed), out _), "malformed envelope/duplicate/extra/root/trailing data rejected");
        Check(!PairingWire.TryParse([0xef, 0xbb, 0xbf, .. packets[0].Item2], out _) && !PairingWire.TryParse([0xff, .. packets[0].Item2], out _) &&
            !PairingWire.TryParse(Encoding.UTF8.GetBytes(request.Replace("\"kind\":\"pair.request\"", "\"kind\":\"\\uD800\"")), out _), "BOM/invalid UTF8/lone surrogate rejected");
        byte[] boundary = new byte[1024]; Array.Fill(boundary, (byte)' '); packets[0].Item2.CopyTo(boundary, 0);
        Check(PairingWire.TryParse(boundary, out _) && !PairingWire.TryParse([.. boundary, (byte)' '], out _), "exact 1KiB bound accepts legal whitespace, next byte rejected");
        string hash = V2Format.ToBase64Url(commit); const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
        int index = alphabet.IndexOf(hash[^1]); string noncanonical = hash[..^1] + alphabet[index | 1];
        Check(PairingWire.TryDecodeHash(hash, out var decoded) && decoded.SequenceEqual(commit) && !PairingWire.TryDecodeHash(noncanonical, out _) && !PairingWire.TryDecodeHash(hash + "\n", out _), "32-byte commitment base64url roundtrip enforces canonical padding bits");
        Check(Reject(() => PairingWire.Request(sidText + "\n", spkiA)) && Reject(() => PairingWire.Request(sidText, new string('A', 1048576))) &&
            Reject(() => PairingWire.ConfirmationSignature(sidText, Convert.ToBase64String(new byte[63]))) && Reject(() => PairingWire.Cancel(sidText, "revoked")), "invalid writer fields rejected, oversized fields bounded before serialization");
        Check(Reject(() => PairingCrypto.Commitment(new byte[15], pkA, pkB, nbBytes)) && Reject(() => PairingCrypto.Transcript(sidBytes, pkA, pkB, new byte[15], nbBytes)) &&
            Reject(() => PairingCrypto.ConfirmationInput((PairingRole)42, th)) && Reject(() => PairingCrypto.Sas(new byte[31])), "formula lengths and role vocabulary checked");
        var nonces = Enumerable.Range(0, 64).Select(_ => PairingWire.NewNonce()).ToArray();
        Check(nonces.All(V2Format.IsNonce22) && nonces.Distinct().Count() == 64, "production entropy helper produces distinct canonical16-byte values using CSPRNG");
        Console.WriteLine($"Conditional P1 wire: {checks} golden/strict-JSON/formula/real-signature checks; no human consent or trust implied."); return checks;
    }
}
