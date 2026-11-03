using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NekoChat.Core.Pairing;
using NekoChat.Core.Storage;
using NekoChat.Core.Roster;
using NekoChat.Core.V2;

namespace NekoChat.Tests;

internal static class PairingCrashChecks
{
    private const string ChildMode = "--pairing-crash-child";
    private sealed record Input(string SyntheticKey, string RemotePeer, bool Scoped = false)
    { public override string ToString() => "Synthetic pairing fixture key input"; }
    private sealed record Command(string Kind, int Port = 0);
    private sealed record Report(string Code, int Port, string PeerId, string PublicSpki, long LocalRevision, PairingSessionRole Role,
        PairingStage Stage, string? SessionId, string? Sas, bool HasConsent, bool MayBeAsymmetric, PairingState? Pairing,
        long Messages, long Attempts, bool DraftPresent, bool ReceiptsEnabled, string? RosterPeer);
    internal static async Task<bool> RunChild(string[] args)
    {
        if (args.Length == 0 || args[0] != ChildMode) return false;
        if (args.Length != 3 || args[2] is not ("before" or "after" or "normal")) throw new ArgumentException("Invalid pairing child arguments.");
        var input = JsonSerializer.Deserialize<Input>(await Console.In.ReadLineAsync() ?? throw new EndOfStreamException())!;
        byte[] bytes = Convert.FromBase64String(input.SyntheticKey); var key = ECDsa.Create();
        try { key.ImportPkcs8PrivateKey(bytes, out int used); if (used != bytes.Length) throw new InvalidDataException("Synthetic key framing."); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        var faults = new Faults(); int armed = 0;
        void Boundary(string op)
        {
            if (op is not ("security-change" or "pair-and-bind") || Interlocked.Exchange(ref armed, 0) != 1) return;
            Console.WriteLine("BOUNDARY:" + args[2]); Console.Out.Flush();
            using var stop = new ManualResetEventSlim(false); stop.Wait(TimeSpan.FromSeconds(15)); throw new TimeoutException("Parent failed to kill fixture.");
        }
        if (args[2] == "before") faults.Before = Boundary; else if (args[2] == "after") faults.After = Boundary;
        using var store = ChatStore.Open(new(args[1]) { Faults = faults, TextValidator = t => V2Text.Check(t, 16384) == V2TextProblem.None });
        // Only explicit synthetic fixture provisioning; never RegisterPeer/force paired, even on restart.
        store.EnsureLocalIdentity(V2Crypto.PeerId(key.ExportSubjectPublicKeyInfo()), key.ExportSubjectPublicKeyInfo(), "synthetic-stdin-fixture");
        string? rowId = null;
        if (input.Scoped)
        {
            var rows = store.GetRosterSnapshot();
            if (rows.Count == 0) store.SaveManualRosterEntry(null, new("synthetic child selection", "001"), RosterImportPreview.ComputeSnapshotToken(rows));
            rowId = store.GetRosterSnapshot().Single().Entry.Id;
        }
        using var writer = new V2PacketWriter(Clone(key), 16384); using var identity = new PairingIdentity(store, key);
        await using var coordinator = new PairingCoordinator(identity); using var messaging = new V2LanClient(store, writer);
        await using var listener = new V2TcpListener(new V2Receiver(store, writer), new() { BindAddress = IPAddress.Loopback, Port = 0, RequestsPerSourcePerMinute = 10000 }, coordinator);
        listener.Start();
        Report Snapshot(string code)
        {
            var view = coordinator.View;
            return new(code, listener.Port, identity.PeerId, identity.PublicSpkiBase64, store.GetLocalIdentitySnapshot().Revision, coordinator.Role, view.Stage,
                view.SessionId, view.Sas, view.Consent is not null, view.MayBeAsymmetric, store.GetPeerSecuritySnapshot(input.RemotePeer).Pairing,
                Number(store.DatabasePath, "SELECT count(*) FROM Message"), Number(store.DatabasePath, "SELECT count(*) FROM ManualSendAttempt"),
                Number(store.DatabasePath, "SELECT count(*) FROM Draft") != 0, store.GetReadReceiptPolicy().Enabled, rowId is null ? null : store.GetRosterSnapshot().Single().Entry.PeerId);
        }
        void Output(string code) { Console.WriteLine(JsonSerializer.Serialize(Snapshot(code))); Console.Out.Flush(); }
        Output("ready");
        while (await Console.In.ReadLineAsync() is { } line)
        {
            if (line.Length > 1024) throw new InvalidDataException("Fixture control size.");
            var command = JsonSerializer.Deserialize<Command>(line) ?? throw new InvalidDataException("Fixture command.");
            string code;
            switch (command.Kind)
            {
                case "OPEN": code = await (rowId is null ? coordinator.OpenWindowAsync(input.RemotePeer, () => true) : coordinator.OpenRosterWindowAsync(store.CapturePairingRosterTarget(rowId)!, () => true)) ? "opened" : "not_opened"; break;
                case "BEGIN": code = (await (rowId is null ? coordinator.BeginAsync("127.0.0.1", command.Port, input.RemotePeer, () => true) : coordinator.BeginRosterAsync(store.CapturePairingRosterTarget(rowId)!, "127.0.0.1", command.Port, () => true))).Code; break;
                case "CONFIRM":
                    var consent = coordinator.View.Consent;
                    if (consent is null) code = "no_consent";
                    else { Volatile.Write(ref armed, args[2] == "normal" ? 0 : 1); code = (await coordinator.ConfirmAsync(consent)).Code; }
                    break;
                case "SAVE":
                    string conversation = Conversation(store.DatabasePath, input.RemotePeer); var draft = store.GetDraftState(conversation)!;
                    code = store.SaveDraft(conversation, "retained synthetic draft", draft.Epoch, draft.Revision + 1).Saved ? "saved" : "not_saved"; break;
                case "SEND":
                    conversation = Conversation(store.DatabasePath, input.RemotePeer); draft = store.GetDraftState(conversation)!;
                    store.SaveDraft(conversation, "explicit synthetic restart message", draft.Epoch, draft.Revision + 1);
                    var attempt = store.BeginManualSend(conversation, "explicit synthetic restart message", draft.Epoch, draft.Revision + 1);
                    code = attempt is AttemptStarted started ? (await messaging.SendPreparedAsync(store.GetPreparedSend(started.MessageId)!, "127.0.0.1", command.Port, () => true)).Code : ((InvalidSend)attempt).Reason; break;
                case "VIEW": code = "view"; break;
                case "STOP": await listener.DisposeAsync(); Output("stopped"); return true;
                default: throw new InvalidDataException("Unknown fixture command.");
            }
            Output(code);
        }
        return true;
    }
    internal static async Task<int> Run()
    {
        int checks = 0, killed = 0, restarted = 0;
        void Check(bool value, string label) { checks++; if (!value) throw new Exception("P1 crash/restart: " + label); }
        foreach (bool scoped in new[] { false, true })
        foreach (string role in new[] { "responder", "initiator" })
        foreach (string point in new[] { "before", "after" })
        {
            string root = Path.Combine(Path.GetTempPath(), "neko-p1-crash-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            string childData = Path.Combine(root, "child"), db = Path.Combine(childData, "nekochat.db");
            using var childKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            try
            {
                await using var parent = new Node(Path.Combine(root, "parent"), scoped);
                using var child = Start(childData, point); Task<string> errors = child.StandardError.ReadToEndAsync();
                byte[]? iConfirmation = null; bool interruptedProxy = false;
                // This is a real socket forwarder; no fake transport, signature or direct paired setup.
                await using var proxy = new V2TcpChecks.ScriptedServer(async (_, body) => {
                    if (PairingWire.TryParse(body, out var request) && request!.Kind == PairingKind.Confirmation) iConfirmation = body.ToArray();
                    try { return await Post(parent.RemotePort, body); }
                    catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException) { interruptedProxy = true; return null; }
                });
                try
                {
                    await Supply(child, childKey, parent.Identity.PeerId, scoped); var ready = await Read(child);
                    parent.RemotePort = ready.Port;
                    Check(ready is { Code: "ready", Role: PairingSessionRole.None, Stage: PairingStage.Closed, HasConsent: false, Pairing: null } && ready.PeerId == V2Crypto.PeerId(childKey.ExportSubjectPublicKeyInfo()), "new process starts closed with only correct synthetic public metadata: " + role + "/" + point);
                    Report sas;
                    if (role == "responder")
                    {
                        Check((await Ask(child, new("OPEN"))).Code == "opened", "R requires explicit local open before actual request");
                        Check((await parent.Begin(proxy.Port, ready.PeerId)).Code == "awaiting_user", "parent I handshakes with actual R child over TCP");
                        sas = await Ask(child, new("VIEW"));
                    }
                    else
                    {
                        Check(await parent.Open(ready.PeerId), "parent R explicitly opens for actual I child");
                        // Parent's endpoint records both confirm frames for replay probes; child targets actual parent listener.
                        sas = await Ask(child, new("BEGIN", parent.Port));
                        Check(sas.Code == "awaiting_user", "actual I child completes commitment/reveal over TCP");
                    }
                    Check(sas.HasConsent && sas.Sas == parent.Coordinator.View.Sas && sas.Pairing == PairingState.Pending && parent.Store.GetPeerSecuritySnapshot(ready.PeerId).Pairing == PairingState.Pending, "both real stores await separate local SAS actions");
                    string oldSid = sas.SessionId!;
                    Check((await Ask(child, new("SAVE"))).Code == "saved", "independent durable draft exists before pairing kill");
                    using var abortConfirm = new CancellationTokenSource(); Task<PairingStep>? inFlight = null;
                    if (role == "responder")
                    {
                        Check((await Ask(child, new("CONFIRM"))).Code == "local_confirmed", "child R alone cannot become paired");
                        inFlight = parent.Coordinator.ConfirmAsync(parent.Coordinator.View.Consent!, abortConfirm.Token);
                    }
                    else
                    {
                        Check((await parent.Coordinator.ConfirmAsync(parent.Coordinator.View.Consent!)).Code == "local_confirmed", "parent R alone cannot become paired");
                        await Send(child, new("CONFIRM"));
                    }
                    Check(await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) == "BOUNDARY:" + point, "real " + role + " final security-change transaction reaches " + point + " COMMIT boundary");
                    child.Kill(entireProcessTree: true); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); killed++;
                    Check(child.ExitCode != 0 && (await errors).Length == 0 && (await child.StandardOutput.ReadToEndAsync()).Length == 0, "true process kill without graceful actor/store drain or secret output");
                    if (inFlight is not null)
                    {
                        abortConfirm.Cancel(); var result = await inFlight.WaitAsync(TimeSpan.FromSeconds(5));
                        Check(interruptedProxy && iConfirmation is not null && result.Code != "paired" && parent.Coordinator.View.MayBeAsymmetric && parent.Store.GetPeerSecuritySnapshot(ready.PeerId).Pairing == PairingState.Unpaired, "lost child R result leaves honest unconfirmed I and no invented paired state");
                        await parent.Coordinator.CancelAsync();
                        Check(parent.Coordinator.View.MayBeAsymmetric, "closing uncertain I preserves result uncertainty instead of claiming remote cancellation");
                    }
                    else
                        Check(parent.Store.GetPeerSecuritySnapshot(ready.PeerId).Pairing == PairingState.Paired, "R may commit before I crashes; cannot pretend two databases are atomic");
                    Check(Text(db, "SELECT pairingState FROM Peer") == (point == "before" ? "pending" : "paired"), "direct SQLite read before product reopen proves exact committed trust boundary");
                    if (scoped) Check(Text(db, "SELECT peerId FROM RosterEntry") == (point == "after" ? parent.Identity.PeerId : null), "direct SQLite before reopen proves scoped roster binding and trust share the exact COMMIT boundary");
                    Check(Text(db, "PRAGMA integrity_check") == "ok" && Number(db, "SELECT count(*) FROM Message") == 0 && Number(db, "SELECT count(*) FROM ManualSendAttempt") == 0 && Text(db, "SELECT rawText FROM Draft") == "retained synthetic draft", "kill preserves database integrity/draft and pairing creates no message attempts");
                    using var next = Start(childData, "normal"); Task<string> restartErrors = next.StandardError.ReadToEndAsync();
                    try
                    {
                        await Supply(next, childKey, parent.Identity.PeerId, scoped); var reopened = await Read(next);
                        Check(reopened.PeerId == ready.PeerId && reopened.PublicSpki == ready.PublicSpki && reopened.LocalRevision == ready.LocalRevision && reopened.Pairing == (point == "before" ? PairingState.Pending : PairingState.Paired), "restart preserves same public identity/revision and committed local trust only");
                        if (scoped) Check(reopened.RosterPeer == (point == "after" ? parent.Identity.PeerId : null), "restart preserves only committed roster binding, never completes missing half");
                        Check(reopened is { Role: PairingSessionRole.None, Stage: PairingStage.Closed, SessionId: null, Sas: null, HasConsent: false, DraftPresent: true, ReceiptsEnabled: false } && (await Ask(next, new("CONFIRM"))).Code == "no_consent", "restart never restores session/SAS/consent/window or changes receipt privacy");
                        byte[] old = role == "responder" ? iConfirmation! : parent.Endpoint.LastRConfirmation!;
                        Check(old is not null && (await Post(reopened.Port, old)).StatusCode == 409 && (await Post(reopened.Port, PairingWire.Request(PairingWire.NewNonce(), parent.Identity.PublicSpkiBase64))).StatusCode == 403, "old signed confirmation cannot revive RAM session; new request still needs explicit open");
                        var send = await Ask(next, new("SEND", parent.Port)); bool bothPaired = role == "initiator" && point == "after";
                        Check(send.Code == (bothPaired ? "ok" : point == "before" ? "peer-not-paired" : "unpaired_sender") && send.Messages == (bothPaired ? 1 : 0) && send.DraftPresent != bothPaired, "actual restart send respects both sides' local trust, keeps failed draft and requires signed storage ACK");
                        if (!bothPaired)
                        {
                            Report fresh;
                            if (role == "responder")
                            {
                                Check((await Ask(next, new("OPEN"))).Code == "opened", "explicit repair reopens R window");
                                Check((await parent.Begin(reopened.Port, ready.PeerId)).Code == "awaiting_user", "explicit repair starts fresh outgoing session");
                                fresh = await Ask(next, new("VIEW"));
                                Check(fresh.HasConsent && fresh.SessionId != oldSid && fresh.Sas == parent.Coordinator.View.Sas && !parent.Coordinator.View.LocalConfirmed, "repair has fresh transcript/tickets; old human actions never inherited");
                                Check((await Ask(next, new("CONFIRM"))).Code == "local_confirmed", "repair requires fresh R human action");
                                Check((await parent.Coordinator.ConfirmAsync(parent.Coordinator.View.Consent!)).Code == "paired", "fresh I action and verified signature complete repair");
                            }
                            else
                            {
                                // Restarting I does not reset retained R admission. Prove the refusal on actual TCP at the original parent clock before advancing it.
                                Check(await parent.Open(ready.PeerId) && (await Ask(next, new("BEGIN", parent.Port))).Code == "remote_ended",
                                    "restarted I cannot bypass retained R two-second fresh-request cooldown");
                                // Explicit new action after2s on injected monotonic clock; no production limiter reset, relaxation or automatic retry.
                                parent.Clock.Advance(2);
                                Check(await parent.Open(ready.PeerId), "already paired R explicitly opens repair without downgrading trust");
                                fresh = await Ask(next, new("BEGIN", parent.Port));
                                Check(fresh is { Code: "awaiting_user", HasConsent: true } && fresh.SessionId != oldSid && fresh.Sas == parent.Coordinator.View.Sas, "restarted I needs new SID/SAS and independent action: code=" + fresh.Code + ", consent=" + fresh.HasConsent + ", sameSas=" + (fresh.Sas == parent.Coordinator.View.Sas));
                                Check((await parent.Coordinator.ConfirmAsync(parent.Coordinator.View.Consent!)).Code == "local_confirmed" && (await Ask(next, new("CONFIRM"))).Code == "paired", "existing paired R unchanged CAS and repaired I complete with fresh consent");
                            }
                            var delivered = await Ask(next, new("SEND", parent.Port));
                            Check(delivered is { Code: "ok", Pairing: PairingState.Paired, Messages: 1, DraftPresent: false } && parent.Number("SELECT count(*) FROM Message") == 1, "explicit repaired pair converges to real signed message/storage ACK and draft clearing");
                        }
                        if (scoped) Check((await Ask(next, new("VIEW"))).RosterPeer == parent.Identity.PeerId && parent.BoundPeer == ready.PeerId, "explicit fresh scoped repair leaves both selected rows bound to the verified counterpart");
                        Check(NoPrivateKeyFiles(root, childKey) && NoPrivateKeyFiles(root, parent.KeyForScan), "raw synthetic private keys/base64 never persist in child/parent fixture files");
                        Check((await Ask(next, new("STOP"))).Code == "stopped", "restart listener/control drains cleanly");
                        await next.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); restarted++;
                        Check(next.ExitCode == 0 && (await restartErrors).Length == 0, "restarted process exits0 without private diagnostic output");
                    }
                    finally { await KillIfLive(next); _ = await restartErrors; }
                }
                finally { await KillIfLive(child); _ = await errors; }
            }
            finally { Directory.Delete(root, true); }
        }
        Check(killed == 8 && restarted == 8, "exactly eight actual boundary kills and eight clean restart processes, including four pair-and-bind cases");
        Console.WriteLine($"Conditional P1 crash/restart: {checks} actual socket/P256/SQLite checks,8 killed I/R processes and8 clean restarts (4 scoped atomic binding); no native Windows claim."); return checks;
    }
    private static ECDsa Clone(ECDsa key)
    { var p = key.ExportParameters(true); try { return ECDsa.Create(p); } finally { CryptographicOperations.ZeroMemory(p.D!); } }
    private static Process Start(string data, string boundary)
    {
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Missing test executable.");
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) info.ArgumentList.Add(typeof(PairingCrashChecks).Assembly.Location);
        foreach (string arg in new[] { ChildMode, data, boundary }) info.ArgumentList.Add(arg);
        return Process.Start(info) ?? throw new InvalidOperationException("Fixture process missing.");
    }
    private static async Task Supply(Process child, ECDsa key, string peer, bool scoped)
    {
        byte[] bytes = key.ExportPkcs8PrivateKey();
        try { await child.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new Input(Convert.ToBase64String(bytes), peer, scoped))); await child.StandardInput.FlushAsync(); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private static async Task Send(Process child, Command command) { await child.StandardInput.WriteLineAsync(JsonSerializer.Serialize(command)); await child.StandardInput.FlushAsync(); }
    private static async Task<Report> Ask(Process child, Command command) { await Send(child, command); return await Read(child); }
    private static async Task<Report> Read(Process child) => JsonSerializer.Deserialize<Report>(await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(12)) ?? throw new EndOfStreamException("Pairing fixture output missing.")) ?? throw new InvalidDataException("Fixture metadata.");
    private static async Task KillIfLive(Process child) { if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } }
    private static async Task<V2HttpResponse> Post(int port, byte[] body)
    {
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(12) };
        using var content = new ByteArrayContent(body); content.Headers.ContentType = new("application/json"); using var response = await http.PostAsync($"http://127.0.0.1:{port}/nekochat/v2/pair", content);
        return new((int)response.StatusCode, await response.Content.ReadAsByteArrayAsync());
    }
    private static object? Query(string db, string sql)
    { using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()); c.Open(); using var q = c.CreateCommand(); q.CommandText = sql; return q.ExecuteScalar(); }
    private static long Number(string db, string sql) => Convert.ToInt64(Query(db, sql));
    private static string? Text(string db, string sql) => Query(db, sql) as string;
    private static string Conversation(string db, string peer)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()); c.Open(); using var q = c.CreateCommand();
        q.CommandText = "SELECT id FROM Conversation WHERE peerId=@peer"; q.Parameters.AddWithValue("@peer", peer); return (string)q.ExecuteScalar()!;
    }
    private static bool NoPrivateKeyFiles(string root, ECDsa key)
    {
        byte[] raw = key.ExportPkcs8PrivateKey(), encoded = Encoding.ASCII.GetBytes(Convert.ToBase64String(raw));
        try { foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) { if (new FileInfo(file).Length > 4 * 1024 * 1024) return false; var bytes = File.ReadAllBytes(file); if (bytes.AsSpan().IndexOf(raw) >= 0 || bytes.AsSpan().IndexOf(encoded) >= 0) return false; } return true; }
        finally { CryptographicOperations.ZeroMemory(raw); CryptographicOperations.ZeroMemory(encoded); }
    }
    private sealed class Node : IAsyncDisposable
    {
        internal readonly ECDsa KeyForScan = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        internal Clock Clock { get; } = new();
        internal ChatStore Store { get; }
        internal PairingIdentity Identity { get; }
        internal PairingCoordinator Coordinator { get; }
        internal V2PacketWriter Writer { get; }
        internal Capture Endpoint { get; }
        internal V2TcpListener Listener { get; }
        internal int Port => Listener.Port;
        internal int RemotePort;
        private readonly string? _rowId;
        internal string? BoundPeer => _rowId is null ? null : Store.GetRosterSnapshot().Single().Entry.PeerId;
        internal Task<bool> Open(string peer) => _rowId is null ? Coordinator.OpenWindowAsync(peer, () => true) : Coordinator.OpenRosterWindowAsync(Store.CapturePairingRosterTarget(_rowId)!, () => true);
        internal Task<PairingStep> Begin(int port, string peer) => _rowId is null ? Coordinator.BeginAsync("127.0.0.1", port, peer, () => true) : Coordinator.BeginRosterAsync(Store.CapturePairingRosterTarget(_rowId)!, "127.0.0.1", port, () => true);
        internal Node(string root, bool scoped)
        {
            Writer = new(Clone(KeyForScan), 16384); Store = ChatStore.Open(new(root) { TextValidator = t => V2Text.Check(t, 16384) == V2TextProblem.None });
            Store.EnsureLocalIdentity(Writer.PeerId, Convert.FromBase64String(Writer.PublicSpkiBase64), "synthetic-parent"); Identity = new(Store, Clone(KeyForScan)); Coordinator = new(Identity, Clock, false); Endpoint = new(Coordinator);
            if (scoped) _rowId = Store.SaveManualRosterEntry(null, new("synthetic parent selection", "002"), RosterImportPreview.ComputeSnapshotToken(Store.GetRosterSnapshot())).EntryId;
            Listener = new(new V2Receiver(Store, Writer), new() { BindAddress = IPAddress.Loopback, Port = 0, RequestsPerSourcePerMinute = 10000 }, Endpoint); Listener.Start();
        }
        internal long Number(string sql) => PairingCrashChecks.Number(Store.DatabasePath, sql);
        public async ValueTask DisposeAsync() { await Listener.DisposeAsync(); await Coordinator.DisposeAsync(); Identity.Dispose(); Store.Dispose(); Writer.Dispose(); KeyForScan.Dispose(); }
    }
    private sealed class Capture(PairingCoordinator inner) : IPairingEndpoint
    {
        internal byte[]? LastRConfirmation;
        public async Task<V2HttpResponse> HandleAsync(ReadOnlyMemory<byte> body, string source, CancellationToken cancellation = default)
        { var reply = await inner.HandleAsync(body, source, cancellation); if (PairingWire.TryParse(reply.Body, out var p) && p!.Kind == PairingKind.Confirmation) LastRConfirmation = reply.Body.ToArray(); return reply; }
        public Task StopAsync() => inner.StopAsync();
    }
    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        internal void Advance(double seconds) => Interlocked.Add(ref _ticks, (long)(seconds * TimeSpan.TicksPerSecond));
    }
    private sealed class Faults : IStoreFaults
    {
        internal Action<string>? Before, After;
        public void AfterBegin(string op) { }
        public void BeforeCommit(string op) => Before?.Invoke(op);
        public void AfterCommit(string op) => After?.Invoke(op);
    }
}
