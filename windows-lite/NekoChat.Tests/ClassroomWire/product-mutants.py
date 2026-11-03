#!/usr/bin/env python3 -I
"""Product-level negative controls for NekoChat.Core/Classroom/Wire (NCMWC).

Each mutant edits the PRODUCT source (a scratch copy of NekoChat.Core), rebuilds it, and runs the real vector suite
(NekoChat.Tests/ClassroomWireVectorChecks.cs against the frozen NCMW-v2 vectors). The suite must fail; the unmutated copy must pass.
Nothing in the repository is modified. Usage:
  python3 -I product-mutants.py <windows-lite dir> <scratch dir> <dotnet> [mutant-number ...]
Environment (NUGET_PACKAGES, DOTNET_CLI_HOME, TMPDIR) is inherited."""
import os, shutil, subprocess, sys, textwrap

LITE, SCRATCH, DOTNET = (os.path.abspath(a) for a in sys.argv[1:4])
ONLY = {int(a) for a in sys.argv[4:]}
W = "NekoChat.Core/Classroom/Wire/"
M = [
 ("ClassroomWire.cs", "public const int MaxTextBytes = V2Text.ProductMaxUtf8Bytes;", "public const int MaxTextBytes = 16384;", "text limit reverted to 16,384"),
 ("ClassroomWire.cs", "6 * MaxTextBytes + 4096", "5 * MaxTextBytes + 4096", "message body bound too small for worst-case escaping"),
 ("ClassroomWire.cs", 'messageId + "\\n" + (reaction ?? "") + "\\n"', 'messageId + "\\n" + "" + "\\n"', "content hash ignores the reaction"),
 ("ClassroomWire.cs", "b is >= 16 and <= 31", "b is >= 16 and <= 32", "private range accepts 172.32"),
 ("ClassroomWire.cs", "!messagePossiblyWritten && ", "", "legacy probe ignores 'message may be written'"),
 ("ClassroomWire.cs", "httpStatus == 400 && ", "", "legacy probe accepts any HTTP status"),
 ("ClassroomWire.cs", "ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4().ToString() : null", "ip.ToString()", "any IPv6 accepted as a source"),
 ("ClassroomWire.cs", "if (len > 1 && s[start] == '0') return false;", "", "IPv4 accepts leading zeros"),
 ("ClassroomHead.cs", "head.Length > ClassroomWire.HeadMaxBytes", "false", "head block unbounded"),
 ("ClassroomHead.cs", 'headers.ContainsKey("Transfer-Encoding") ||', "false ||", "chunked tolerated"),
 ("ClassroomHead.cs", 'case "/nekochat/v2/hello": route = ClassroomRoute.SignedV2Hello;', 'case "/nekochat/v2/hello": route = ClassroomRoute.Hello;', "signed V2 hello routed as classroom"),
 ("ClassroomRequests.cs", "if (protocol != ClassroomWire.Protocol || version != \"1\") return Fail(ClassroomCodes.UnsupportedVersion);", "if (protocol != ClassroomWire.Protocol || version != \"1\") return Fail(ClassroomCodes.InvalidPayload);", "foreign protocol reported as malformed"),
 ("ClassroomRequests.cs", "if (!string.Equals(toIp, localIp, StringComparison.Ordinal)) return Fail(ClassroomCodes.WrongRecipient);", "", "recipient binding dropped"),
 ("ClassroomRequests.cs", "state.Age >= ClassroomWire.SessionTtl", "state.Age > ClassroomWire.SessionTtl", "session TTL boundary inclusive"),
 ("ClassroomRequests.cs", "!string.Equals(state.SourceIp, sourceIp, StringComparison.Ordinal) ||", "", "session not bound to its source"),
 ("ClassroomRequests.cs", '!o.TryGetString("receipt_kind", out var receiptKind) || receiptKind != "read"', '!o.TryGetString("receipt_kind", out var receiptKind)', "receipt_kind not pinned to read"),
 ("ClassroomRequests.cs", "case V2TextProblem.TooLarge: return Fail(ClassroomCodes.PayloadTooLarge);", "case V2TextProblem.TooLarge: return Fail(ClassroomCodes.InvalidPayload);", "oversize text reported as malformed"),
 ("ClassroomRequests.cs", "if (body.Length > limit) return Fail(ClassroomCodes.PayloadTooLarge);", "", "size gate before parsing removed"),
 ("ClassroomResponses.cs", "if (id != sent.MessageId || ", "if (", "ACK message_id echo not checked"),
 ("ClassroomResponses.cs", "session != sent.Session || receiver != context.TargetIp || observed != context.OwnIp", "session != sent.Session || receiver != context.TargetIp", "ACK source binding not checked"),
 ("ClassroomResponses.cs", "kind == ClassroomResponseKind.MessageAck && hash != ClassroomWire.ContentSha256(sent.MessageId, sent.Reaction, sent.Text)", "false", "ACK content hash not checked"),
 ("ClassroomResponses.cs", "!ClassroomWire.TryErrorStatus(code, out int expected) || expected != httpStatus", "!ClassroomWire.TryErrorStatus(code, out int expected)", "error envelope accepted with any HTTP status"),
 ("ClassroomResponses.cs", 'if (httpStatus != 200 || kindText != KindText(kind)) return Invalid();', 'if (kindText != KindText(kind)) return Invalid();', "ACK accepted on non-200"),
 ("ClassroomResponses.cs", "max != ClassroomWire.MaxTextBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)", "false", "hello accepts any max_text_bytes"),
 ("ClassroomResponses.cs", "if (nonce != sent.ClientNonce || ", "if (", "hello nonce echo not checked"),
 ("ClassroomSessionTable.cs", "_clock.GetElapsedTime(e.Issued, now) >= _ttl", "_clock.GetElapsedTime(e.Issued, now) > _ttl", "TTL boundary inclusive"),
 ("ClassroomSessionTable.cs", "e.Id == id && e.Source == source", "e.Id == id", "consume ignores the source"),
 ("ClassroomSessionTable.cs", "_entries.RemoveAt(index); return true;", "return true;", "session reusable"),
 ("ClassroomSessionTable.cs", "while (_entries.Count(e => e.Source == source) >= _perSource)", "while (false)", "per-source cap ignored"),
 ("ClassroomSessionTable.cs", "public void Clear() { lock (_gate) _entries.Clear(); }", "public void Clear() { }", "Clear does nothing"),
 ("ClassroomSessionTable.cs", "if (_entries.Any(e => e.Id == id)) return ClassroomSessionAdd.Collision;", "", "collision overwrites"),
 ("ClassroomPacketWriter.cs", 'p["receipt_kind"] = "read"', 'p["receipt_kind"] = "seen"', "writer emits another receipt_kind"),
 ("ClassroomPacketWriter.cs", 'p["max_text_bytes"] = ClassroomWire.MaxTextBytes', 'p["max_text_bytes"] = 16384', "writer advertises 16,384"),
]

def sh(cmd, cwd):
    return subprocess.run(cmd, cwd=cwd, capture_output=True, text=True)

def main():
    if os.path.exists(SCRATCH): shutil.rmtree(SCRATCH)
    lite = os.path.join(SCRATCH, "windows-lite"); core = os.path.join(lite, "NekoChat.Core"); h = os.path.join(lite, "harness")
    shutil.copytree(os.path.join(LITE, "..", "Shared"), os.path.join(SCRATCH, "Shared"))   # Core links ../../Shared/UpdateDownloader.cs
    shutil.copytree(os.path.join(LITE, "NekoChat.Core"), core, ignore=shutil.ignore_patterns("bin", "obj"))
    os.makedirs(os.path.join(h, "Fixtures/Classroom"))
    shutil.copy(os.path.join(LITE, "NekoChat.Tests/ClassroomWireVectorChecks.cs"), h)
    shutil.copy(os.path.join(LITE, "NekoChat.Tests/Fixtures/Classroom/ncmw-vectors-v2.json"), os.path.join(h, "Fixtures/Classroom"))
    open(os.path.join(h, "harness.csproj"), "w").write(textwrap.dedent("""\
        <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>
        <AssemblyName>NekoChat.Tests</AssemblyName><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup>
        <ItemGroup><ProjectReference Include="../NekoChat.Core/NekoChat.Core.csproj"/><None Update="Fixtures/Classroom/ncmw-vectors-v2.json" CopyToOutputDirectory="PreserveNewest"/></ItemGroup></Project>"""))
    open(os.path.join(h, "Program.cs"), "w").write("int n = await NekoChat.Tests.ClassroomWireVectorChecks.Run(); Console.WriteLine($\"harness: {n} checks\"); return 0;\n")
    def run():
        r = sh([DOTNET, "run", "-c", "Release"], h)
        return r.returncode, (r.stdout + r.stderr)
    code, out = run()
    print("baseline (unmutated product):", "PASS" if code == 0 else "FAIL", flush=True)
    if code != 0: print(out[-1500:]); sys.exit(2)
    caught = escaped = 0
    for i, (f, old, new, name) in enumerate(M, 1):
        if ONLY and i not in ONLY: continue
        path = os.path.join(core, "Classroom/Wire", f); src = open(path, encoding="utf-8").read()
        if src.count(old) != 1: print(f"{i:2d} PATCH NOT UNIQUE/FOUND ({src.count(old)}x): {name}"); sys.exit(3)
        open(path, "w", encoding="utf-8").write(src.replace(old, new, 1))
        try: code, out = run()
        finally: open(path, "w", encoding="utf-8").write(src)
        built = "error CS" not in out
        if code != 0 and built: caught += 1; print(f"{i:2d} CAUGHT   {name}", flush=True)
        elif code != 0: print(f"{i:2d} DID NOT BUILD (invalid mutant): {name}"); sys.exit(4)
        else: escaped += 1; print(f"{i:2d} ESCAPED  {name}", flush=True)
    print(f"product negative control: {caught}/{caught + escaped} mutants caught")
    sys.exit(1 if escaped else 0)
main()
