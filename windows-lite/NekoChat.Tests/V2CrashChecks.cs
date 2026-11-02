using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NekoChat.Core.Storage;
using NekoChat.Core.V2;

namespace NekoChat.Tests;

internal static class V2CrashChecks
{
    private const string ChildMode = "--v2-crash-receiver";
    private sealed record ChildInput(string SyntheticPrivateKeyBase64, string SenderId, string SenderSpkiBase64)
    { public override string ToString() => "Synthetic crash fixture inputs"; }
    private sealed record Ready(int Port, string ReceiverId, string PublicSpkiBase64);
    public static async Task<bool> RunChild(string[] args)
    {
        if (args.Length == 0 || args[0] != ChildMode) return false;
        if (args.Length != 3 || args[2] is not ("before" or "after" or "normal")) throw new ArgumentException("Invalid V2 crash child arguments.");
        // Synthetic keys are delivered only through the anonymous stdin pipe, never argv, env, a file, stdout or diagnostics.
        var input = JsonSerializer.Deserialize<ChildInput>(await Console.In.ReadLineAsync() ?? throw new EndOfStreamException())!;
        byte[] privateBytes = Convert.FromBase64String(input.SyntheticPrivateKeyBase64); var key = ECDsa.Create();
        try { key.ImportPkcs8PrivateKey(privateBytes, out int used); if (used != privateBytes.Length) throw new InvalidDataException("Fixture key framing."); }
        finally { CryptographicOperations.ZeroMemory(privateBytes); }
        using var writer = new V2PacketWriter(key, 16384); var faults = new Faults();
        void Boundary(string op)
        {
            if (op != "receive") return;
            Console.WriteLine("BOUNDARY:" + args[2]); Console.Out.Flush();
            using var stop = new ManualResetEventSlim(false); stop.Wait(TimeSpan.FromSeconds(15));
            throw new TimeoutException("Parent did not kill the crash fixture.");
        }
        if (args[2] == "before") faults.Before = Boundary; else if (args[2] == "after") faults.After = Boundary;
        using var store = ChatStore.Open(new(args[1]) { Faults = faults, TextValidator = t => V2Text.Check(t, 16384) == V2TextProblem.None });
        store.EnsureLocalIdentity(writer.PeerId, Convert.FromBase64String(writer.PublicSpkiBase64), "synthetic-pipe-key");
        var peer = (PeerRegistered)store.RegisterPeer(input.SenderId, PairingState.Paired, Convert.FromBase64String(input.SenderSpkiBase64));
        if (args[2] != "normal") store.SaveDraft(peer.Conversation.ConversationId, "preserved receiver draft", 0, 1);
        var receiver = new V2Receiver(store, writer); await using var listener = new V2TcpListener(receiver, new() { BindAddress = IPAddress.Loopback, Port = 0 });
        int notices = 0; listener.MessageStored += _ => Interlocked.Increment(ref notices); listener.Start();
        Console.WriteLine(JsonSerializer.Serialize(new Ready(listener.Port, writer.PeerId, writer.PublicSpkiBase64))); Console.Out.Flush();
        while (await Console.In.ReadLineAsync() is { } command)
        {
            if (command == "STOP") break;
            if (command != "STATS") throw new InvalidDataException("Fixture control command.");
            Console.WriteLine("NOTICES:" + Volatile.Read(ref notices)); Console.Out.Flush();
        }
        await listener.DisposeAsync(); Console.WriteLine("STOPPED"); Console.Out.Flush(); return true;
    }
    public static async Task<int> Run()
    {
        int checks = 0, killed = 0, stopped = 0;
        void Check(bool ok, string name) { checks++; if (!ok) throw new Exception("V2 crash: " + name); }
        foreach (string stage in new[] { "before", "after" })
        {
            string root = Path.Combine(Path.GetTempPath(), "neko-v2-crash-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            string receiverData = Path.Combine(root, "receiver"), senderData = Path.Combine(root, "sender"); Directory.CreateDirectory(receiverData);
            using var receiverKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var sender = new V2PacketWriter(ECDsa.Create(ECCurve.NamedCurves.nistP256), 16384);
            try
            {
                using var senderStore = ChatStore.Open(new(senderData) { TextValidator = t => V2Text.Check(t, 16384) == V2TextProblem.None });
                senderStore.EnsureLocalIdentity(sender.PeerId, Convert.FromBase64String(sender.PublicSpkiBase64), "synthetic-parent-key");
                using var child = Start(receiverData, stage); Task<string> stderr = child.StandardError.ReadToEndAsync(); Ready ready;
                try
                {
                    await Input(child, receiverKey, sender); ready = await ReadReady(child);
                    Check(ready.ReceiverId == V2Crypto.PeerId(receiverKey.ExportSubjectPublicKeyInfo()) && ready.PublicSpkiBase64 == Convert.ToBase64String(receiverKey.ExportSubjectPublicKeyInfo()), "child receives synthetic key through pipe and publishes only correct public identity");
                    string conversation = ((PeerRegistered)senderStore.RegisterPeer(ready.ReceiverId, PairingState.Paired, Convert.FromBase64String(ready.PublicSpkiBase64))).Conversation.ConversationId;
                    senderStore.SaveDraft(conversation, "actual TCP crash " + stage, 0, 1);
                    var attempt = (AttemptStarted)senderStore.BeginManualSend(conversation, "actual TCP crash " + stage, 0, 1);
                    var prepared = senderStore.GetPreparedSend(attempt.MessageId)!;
                    string clientNonce = V2PacketWriter.NewClientNonce(); var helloResponse = await Post(ready.Port, "/nekochat/v2/hello", sender.HelloRequest(clientNonce));
                    var hello = new V2Verifier(16384).VerifyHello(helloResponse.Body, new(clientNonce, new("paired", ready.PublicSpkiBase64), new[] { "msg.sign" }));
                    Check(hello.Success, "actual child signed hello passes real product verifier");
                    byte[] oldPacket = sender.Message(prepared.MessageId, prepared.RecipientId, hello.Value!.Nonce, prepared.RawText);
                    Task<V2HttpResponse> inFlight = Post(ready.Port, "/nekochat/v2/message", oldPacket);
                    Check(await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(12)) == "BOUNDARY:" + stage, "real TCP request reaches exact product transaction boundary " + stage);
                    child.Kill(entireProcessTree: true); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); killed++;
                    Check(child.ExitCode != 0, "receiver is truly killed without graceful store disposal");
                    Check((await stderr).Length == 0 && (await child.StandardOutput.ReadToEndAsync()).Length == 0, "killed fixture emits only bounded public ready and boundary metadata");
                    bool noAck = false;
                    try { _ = await inFlight; } catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException) { noAck = true; }
                    Check(noAck, "killed receiver cannot return stored ACK");
                    senderStore.CompleteAttempt(prepared.MessageId, new UnconfirmedOutcome(), prepared.TryCount);
                    Check(senderStore.ListMessages(conversation).Items.Count == 0 && senderStore.GetDraftState(conversation)?.RawText == prepared.RawText,
                        "unconfirmed sender has no fabricated outgoing history and retains draft");
                    string db = Path.Combine(receiverData, "nekochat.db"); int expected = stage == "after" ? 1 : 0;
                    Check(Count(db, "Message") == expected && Count(db, "Message WHERE localReadAt IS NULL") == expected, "direct native read before reopening product proves committed row count " + stage);
                    Check(Count(db, "Peer WHERE installEvidence='appVerified'") == expected && Text(db, "SELECT rawText FROM Draft") == "preserved receiver draft", "message, installation evidence and existing receiver draft survive at exact boundary");
                    using var restarted = Start(receiverData, "normal"); Task<string> restartErrors = restarted.StandardError.ReadToEndAsync();
                    try
                    {
                        await Input(restarted, receiverKey, sender); var next = await ReadReady(restarted);
                        Check(next.ReceiverId == ready.ReceiverId && next.PublicSpkiBase64 == ready.PublicSpkiBase64, "restart uses same synthetic identity without exporting it to disk");
                        var replay = await Post(next.Port, "/nekochat/v2/message", oldPacket);
                        using (var p = JsonDocument.Parse(replay.Body)) Check(replay.StatusCode == 409 && p.RootElement.GetProperty("code").GetString() == "stale_nonce", "RAM nonce from killed process is never restored");
                        Check(Count(db, "Message") == expected, "old packet cannot write or count twice after restart");
                        var draft = senderStore.GetDraftState(conversation)!;
                        var retry = (AttemptStarted)senderStore.BeginManualRetry(prepared.MessageId, prepared.RawText, conversation, draft.Epoch, draft.Revision);
                        using var client = new V2LanClient(senderStore, sender);
                        var result = await client.SendPreparedAsync(senderStore.GetPreparedSend(retry.MessageId)!, "127.0.0.1", next.Port, () => true);
                        Check(retry.MessageId == prepared.MessageId && result is { Code: "ok", Completion: Confirmed { DraftCleared: true } }, "explicit actual client retry confirms same id after receiver crash");
                        Check(Count(db, "Message") == 1 && Count(db, "Message WHERE localReadAt IS NULL") == 1 && senderStore.ListMessages(conversation).Items.Count == 1,
                            "both sides converge to exactly one message and one receiver unread");
                        Check(Text(db, "SELECT rawText FROM Message") == prepared.RawText && Text(db, "SELECT rawText FROM Draft") == "preserved receiver draft", "crash retry preserves original bytes and independent receiver composer");
                        await restarted.StandardInput.WriteLineAsync("STATS"); await restarted.StandardInput.FlushAsync();
                        Check(await restarted.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) == "NOTICES:" + (stage == "after" ? 0 : 1), "new stored message notifies; recovered duplicate stays silent");
                        await restarted.StandardInput.WriteLineAsync("STOP"); await restarted.StandardInput.FlushAsync();
                        Check(await restarted.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) == "STOPPED", "normal restarted receiver drains listener");
                        await restarted.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); stopped++;
                        Check(restarted.ExitCode == 0 && (await restartErrors).Length == 0, "restarted receiver exits cleanly after verified delivery");
                        Check(NoPrivateKeyFiles(root, receiverKey), "synthetic private key raw bytes and base64 never persisted in fixture files");
                    }
                    finally { await KillIfLive(restarted); }
                }
                finally { await KillIfLive(child); _ = await stderr; }
            }
            finally { Directory.Delete(root, true); }
        }
        Check(killed == 2 && stopped == 2, "exactly two truly killed and two gracefully stopped receiver children");
        Console.WriteLine($"Product V2 TCP crashes: {checks} actual socket/SQLite checks; 2 killed receiver processes and 2 clean restart processes."); return checks;
    }
    private static Process Start(string data, string stage)
    {
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Missing executable.");
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) info.ArgumentList.Add(typeof(V2CrashChecks).Assembly.Location);
        info.ArgumentList.Add(ChildMode); info.ArgumentList.Add(data); info.ArgumentList.Add(stage);
        return Process.Start(info) ?? throw new InvalidOperationException("Child did not start.");
    }
    private static async Task Input(Process child, ECDsa key, V2PacketWriter sender)
    {
        byte[] bytes = key.ExportPkcs8PrivateKey();
        try { await child.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new ChildInput(Convert.ToBase64String(bytes), sender.PeerId, sender.PublicSpkiBase64))); await child.StandardInput.FlushAsync(); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private static async Task<Ready> ReadReady(Process child)
    {
        string line = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(12)) ?? throw new EndOfStreamException("Crash child did not become ready.");
        return JsonSerializer.Deserialize<Ready>(line) ?? throw new InvalidDataException("Fixture public ready metadata.");
    }
    private static async Task KillIfLive(Process child)
    { if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } }
    private static async Task<V2HttpResponse> Post(int port, string path, byte[] bytes)
    {
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
        using var content = new ByteArrayContent(bytes); content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await http.PostAsync($"http://127.0.0.1:{port}{path}", content);
        return new((int)response.StatusCode, await response.Content.ReadAsByteArrayAsync());
    }
    private static object? Query(string db, string sql)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()); c.Open();
        using var cmd = c.CreateCommand(); cmd.CommandText = sql; return cmd.ExecuteScalar();
    }
    private static long Count(string db, string table) => Convert.ToInt64(Query(db, "SELECT count(*) FROM " + table));
    private static string? Text(string db, string sql) => Query(db, sql) as string;
    private static bool NoPrivateKeyFiles(string root, ECDsa key)
    {
        byte[] raw = key.ExportPkcs8PrivateKey(), encoded = Encoding.ASCII.GetBytes(Convert.ToBase64String(raw));
        try
        {
            foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                if (new FileInfo(path).Length > 4 * 1024 * 1024) return false;
                byte[] bytes = File.ReadAllBytes(path);
                if (bytes.AsSpan().IndexOf(raw) >= 0 || bytes.AsSpan().IndexOf(encoded) >= 0) return false;
            }
            return true;
        }
        finally { CryptographicOperations.ZeroMemory(raw); CryptographicOperations.ZeroMemory(encoded); }
    }
    private sealed class Faults : IStoreFaults
    {
        internal Action<string>? Before, After;
        public void AfterBegin(string op) { }
        public void BeforeCommit(string op) => Before?.Invoke(op);
        public void AfterCommit(string op) => After?.Invoke(op);
    }
}
