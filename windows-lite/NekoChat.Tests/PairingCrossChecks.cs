using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using NekoChat.Core.Pairing;
using NekoChat.Core.Storage;
using NekoChat.Core.V2;

namespace NekoChat.Tests;

internal static class PairingCrossChecks
{
    internal static async Task<int> Run()
    {
        int checks = 0;
        void Check(bool value, string label) { checks++; if (!value) throw new Exception("P1 cross-initiation: " + label); }
        foreach (string order in new[] { "reject-first", "yield-first", "concurrent", "unknown-peer", "concurrent-confirm" })
        {
            await using var f = new Pair(timers: order != "concurrent-confirm");
            Check(string.CompareOrdinal(f.Small.Identity.PeerId, f.Big.Identity.PeerId) < 0 && StringComparer.OrdinalIgnoreCase.Compare(f.Small.Identity.PeerId, f.Big.Identity.PeerId) > 0, "actual synthetic PeerIds require ordinal byte order, not case folding: " + order);
            var small = f.Small.Coordinator.BeginAsync("127.0.0.1", f.Big.Port, order == "unknown-peer" ? null : f.Big.Identity.PeerId, () => true);
            var big = f.Big.Coordinator.BeginAsync("127.0.0.1", f.Small.Port, order == "unknown-peer" ? null : f.Small.Identity.PeerId, () => true);
            await f.BothRequests();
            if (order == "reject-first") { f.Small.Endpoint.Release.TrySetResult(); Check((await big).Code == "remote_ended", "larger outgoing403 may finish before incoming cross-request"); f.Big.Endpoint.Release.TrySetResult(); }
            else if (order == "yield-first") { f.Big.Endpoint.Release.TrySetResult(); await Until(() => f.Big.Coordinator.Role == PairingSessionRole.Responder); f.Small.Endpoint.Release.TrySetResult(); }
            else { f.Small.Endpoint.Release.TrySetResult(); f.Big.Endpoint.Release.TrySetResult(); }
            await Task.WhenAll(small, big).WaitAsync(TimeSpan.FromSeconds(5));
            Check((await small).Code == "awaiting_user" && f.Small.Coordinator.Role == PairingSessionRole.Initiator && f.Big.Coordinator.Role == PairingSessionRole.Responder, "crossed real TCP requests converge to mandated roles: " + order);
            Check(f.Small.Coordinator.View.Sas == f.Big.Coordinator.View.Sas && f.Small.Coordinator.View.Sas?.Length == 6 && f.Small.Coordinator.View.SessionId == f.Big.Coordinator.View.SessionId && f.Big.Coordinator.View.SessionId != f.Small.Endpoint.FirstSession, "one surviving transcript and fresh R consent, abandoned big-I sid never reused: " + order);
            Check(f.Small.State(f.Big) == PairingState.Pending && f.Big.State(f.Small) == PairingState.Pending && !f.Big.Coordinator.View.LocalConfirmed, "outgoing user intent opens R window but never stands for SAS consent: " + order);
            var oldCancel = await Post(f.Big.Port, PairingWire.Cancel(f.Small.Endpoint.FirstSession!, "superseded"));
            Check(oldCancel.StatusCode == 409 && f.Big.State(f.Small) == PairingState.Pending, "old-session cancellation cannot terminate surviving R session: " + order);
            Check((await f.Big.Coordinator.ConfirmAsync(f.Small.Coordinator.View.Consent!)).Code == "stale_consent", "I dialog cannot confirm R after role selection: " + order);
            if (order == "concurrent-confirm")
            {
                var before = f.Big.Store.GetPeerSecuritySnapshot(f.Small.Identity.PeerId).PeerRevision;
                using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
                f.Big.Faults.Read = op => { if (op == "GetPeerSecuritySnapshot") { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(5))) throw new Exception("Concurrent confirmation real read barrier timeout"); } };
                var rConfirm = f.Big.Coordinator.ConfirmAsync(f.Big.Coordinator.View.Consent!);
                if (!await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(4)))) throw new Exception("Concurrent confirmation SQL barrier missing");
                var iConfirm = f.Small.Coordinator.ConfirmAsync(f.Small.Coordinator.View.Consent!);
                try { await f.Big.Endpoint.ConfirmationUnavailable.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
                finally { release.Set(); }
                await Task.WhenAll(rConfirm, iConfirm).WaitAsync(TimeSpan.FromSeconds(8));
                Check((await iConfirm).Code == "paired" && f.Small.State(f.Big) == PairingState.Paired && f.Big.State(f.Small) == PairingState.Paired && f.Big.Store.GetPeerSecuritySnapshot(f.Small.Identity.PeerId).PeerRevision == before + 1, "real concurrent local/remote confirm causes exactly one R trust revision: I=" + (await iConfirm).Code + ", R=" + (await rConfirm).Code + ", states=" + f.Small.State(f.Big) + "/" + f.Big.State(f.Small) + ", revision=" + before + "->" + f.Big.Store.GetPeerSecuritySnapshot(f.Small.Identity.PeerId).PeerRevision);
            }
            else
            {
                Check((await f.Big.Coordinator.ConfirmAsync(f.Big.Coordinator.View.Consent!)).Code == "local_confirmed" && f.Big.State(f.Small) == PairingState.Pending, "R local action alone does not pair: " + order);
                Check((await f.Small.Coordinator.ConfirmAsync(f.Small.Coordinator.View.Consent!)).Code == "paired" && f.Small.State(f.Big) == PairingState.Paired && f.Big.State(f.Small) == PairingState.Paired, "both human-action APIs plus bound signatures pair over actual TCP: " + order);
            }
            Check(f.Small.Number("SELECT count(*) FROM ManualSendAttempt") == 0 && f.Big.Number("SELECT count(*) FROM Message") == 0, "cross coordination never invents user messages: " + order);
            if (order == "concurrent")
            {
                var sent = await f.Small.Messaging.SendPreparedAsync(f.Small.Prepare(f.Big, "交叉配對後真訊息"), "127.0.0.1", f.Big.Port, () => true);
                Check(sent is { Code: "ok", Completion: Confirmed { DraftCleared: true } } && f.Big.Number("SELECT count(*) FROM Message") == 1, "surviving roles authorize real signed chat/storage ACK");
            }
        }
        // R accepts while the abandoned-I best-effort cancellation has no reply. It remains one retained/drained outbound job.
        await using (var f = new Pair())
        {
            var oldArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseOld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var stalled = new V2TcpChecks.ScriptedServer(async (_, body) => {
                PairingWire.TryParse(body, out var packet);
                if (packet!.Kind == PairingKind.Request) { oldArrived.TrySetResult(); await releaseOld.Task.WaitAsync(TimeSpan.FromSeconds(6)); }
                return PairingResponses.Error("pair_state");
            });
            var big = f.Big.Coordinator.BeginAsync("127.0.0.1", stalled.Port, f.Small.Identity.PeerId, () => true); await oldArrived.Task.WaitAsync(TimeSpan.FromSeconds(3));
            f.Big.Endpoint.Release.TrySetResult(); var watch = Stopwatch.StartNew();
            var small = await f.Small.Coordinator.BeginAsync("127.0.0.1", f.Big.Port, f.Big.Identity.PeerId, () => true);
            Check(small.Code == "awaiting_user" && watch.Elapsed < TimeSpan.FromSeconds(2) && (await big).Code == "role_changed", "blocked old cancel reply never holds up new R commitment/reveal beyond wire deadline");
            Check(f.Big.Coordinator.View.LocalConfirmed == false && f.Big.State(f.Small) == PairingState.Pending, "role switch does not import old confirmation state");
            var stopping = f.Big.Coordinator.CancelAsync(); await Task.Delay(50);
            Check(!stopping.IsCompleted, "cancel owns and drains outstanding best-effort notification instead of detaching it");
            releaseOld.TrySetResult(); await stopping.WaitAsync(TimeSpan.FromSeconds(5));
            Check(f.Big.State(f.Small) == PairingState.Unpaired && f.Big.Coordinator.Role == PairingSessionRole.None, "stop closes R and drains abandoned outgoing work");
        }
        await using (var f = new Pair(fakeClock: true))
        {
            var small = f.Small.Coordinator.BeginAsync("127.0.0.1", f.Big.Port, f.Big.Identity.PeerId, () => true);
            var big = f.Big.Coordinator.BeginAsync("127.0.0.1", f.Small.Port, f.Small.Identity.PeerId, () => true); await f.BothRequests();
            f.Clock.Advance(119); f.Big.Endpoint.Release.TrySetResult(); f.Small.Endpoint.Release.TrySetResult(); await Task.WhenAll(small, big);
            Check(f.Big.Coordinator.Role == PairingSessionRole.Responder && f.Big.Coordinator.View.Remaining == TimeSpan.FromSeconds(1) && f.Small.State(f.Big) == PairingState.Pending, "late role switch keeps original intent deadline, no new120-second window");
            var i = f.Small.Coordinator.View.Consent!; var r = f.Big.Coordinator.View.Consent!; f.Clock.Advance(1);
            Check((await f.Small.Coordinator.ConfirmAsync(i)).Code == "stale_consent" && (await f.Big.Coordinator.ConfirmAsync(r)).Code == "stale_consent" && f.Small.State(f.Big) != PairingState.Paired && f.Big.State(f.Small) != PairingState.Paired, "exact original120-second boundary rejects both surviving human tickets");
            await f.Small.Coordinator.InvalidateAsync(); await f.Big.Coordinator.InvalidateAsync();
            Check(f.Small.State(f.Big) == PairingState.Unpaired && f.Big.State(f.Small) == PairingState.Unpaired, "expired role-switch candidates clean only pending states");
        }
        foreach (string action in new[] { "cancel", "scope", "dispose" })
        {
            await using var f = new Pair(); int current = 1;
            var small = f.Small.Coordinator.BeginAsync("127.0.0.1", f.Big.Port, f.Big.Identity.PeerId, () => true);
            var big = f.Big.Coordinator.BeginAsync("127.0.0.1", f.Small.Port, f.Small.Identity.PeerId, () => Volatile.Read(ref current) == 1); await f.BothRequests();
            Task stop;
            if (action == "cancel") stop = f.Big.Coordinator.CancelAsync();
            else if (action == "dispose") stop = f.Big.Coordinator.DisposeAsync().AsTask();
            else { current = 0; stop = f.Big.Coordinator.InvalidateAsync(); }
            f.Big.Endpoint.Release.TrySetResult(); f.Small.Endpoint.Release.TrySetResult(); await Task.WhenAll(stop, small, big).WaitAsync(TimeSpan.FromSeconds(5));
            Check(f.Big.Coordinator.View.Consent is null && f.Big.Coordinator.Role == PairingSessionRole.None && f.Big.State(f.Small) != PairingState.Paired, "late cross-request cannot recreate canceled/invalidated/stopped intent: " + action);
        }
        // An unrelated key/source claim must not trigger the abandon-I rule.
        await using (var f = new Pair())
        {
            var big = f.Big.Coordinator.BeginAsync("127.0.0.1", f.Small.Port, f.Small.Identity.PeerId, () => true); await f.Small.Endpoint.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(3));
            f.Big.Endpoint.Release.TrySetResult();
            using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256); byte[] request = PairingWire.Request(PairingWire.NewNonce(), Convert.ToBase64String(attacker.ExportSubjectPublicKeyInfo()));
            Check((await Post(f.Big.Port, request)).StatusCode == 403 && f.Big.Coordinator.Role == PairingSessionRole.Initiator && f.Big.Number("SELECT count(*) FROM Peer") == 0, "unrelated actual-TCP public key cannot switch expected initiator");
            byte[] matching = PairingWire.Request(PairingWire.NewNonce(), f.Small.Identity.PublicSpkiBase64);
            var wrongSource = await f.Big.Coordinator.HandleAsync(matching, "10.0.0.99");
            Check(wrongSource.StatusCode == 403 && f.Big.Coordinator.Role == PairingSessionRole.Initiator, "injected logical source mismatch does not switch role (source API fixture, not packet/IP ownership claim)");
            f.Small.Endpoint.Release.TrySetResult(); await big;
        }
        // Existing SAS ticket from an older outbound session cannot confirm the later responder transcript.
        await using (var f = new Pair())
        {
            f.Small.Endpoint.Release.TrySetResult(); f.Big.Endpoint.Release.TrySetResult();
            Check(await f.Small.Coordinator.OpenWindowAsync(f.Big.Identity.PeerId, () => true), "explicit listen action opens bounded window");
            Check((await f.Big.Coordinator.BeginAsync("127.0.0.1", f.Small.Port, f.Small.Identity.PeerId, () => true)).Code == "awaiting_user", "larger node can initially be I when only it initiates");
            var old = f.Big.Coordinator.View.Consent!;
            Check((await f.Small.Coordinator.BeginAsync("127.0.0.1", f.Big.Port, f.Big.Identity.PeerId, () => true)).Code == "awaiting_user" && f.Big.Coordinator.Role == PairingSessionRole.Responder, "later explicit smaller-I request switches the existing larger-I SAS session");
            Check(f.Big.Coordinator.View.SessionId != old.SessionId && (await f.Big.Coordinator.ConfirmAsync(old)).Code == "stale_consent" && !await f.Big.Coordinator.RejectAsync(old) && !f.Big.Coordinator.View.LocalConfirmed, "old dialog cannot confirm or reject newly selected role/session");
            var fresh = f.Big.Coordinator.View.Consent!;
            Check(await f.Big.Coordinator.RejectAsync(fresh) && f.Big.Coordinator.Role == PairingSessionRole.None && f.Big.State(f.Small) == PairingState.Unpaired, "current mismatch rejection closes user intent and cleans its candidate");
        }
        foreach (string change in new[] { "scope", "revoke" })
        {
            await using var f = new Pair(); int current = 1;
            var small = f.Small.Coordinator.BeginAsync("127.0.0.1", f.Big.Port, f.Big.Identity.PeerId, () => true);
            var big = f.Big.Coordinator.BeginAsync("127.0.0.1", f.Small.Port, f.Small.Identity.PeerId, () => Volatile.Read(ref current) == 1); await f.BothRequests();
            f.Big.Endpoint.Release.TrySetResult(); f.Small.Endpoint.Release.TrySetResult(); await Task.WhenAll(small, big);
            await f.Big.Coordinator.ConfirmAsync(f.Big.Coordinator.View.Consent!);
            if (change == "scope") f.Big.Faults.Before = op => { if (op == "security-change") Volatile.Write(ref current, 0); };
            else f.Big.Store.SetPairingState(f.Small.Identity.PeerId, PairingState.Revoked);
            var result = await f.Small.Coordinator.ConfirmAsync(f.Small.Coordinator.View.Consent!);
            Check(result.Code != "paired" && f.Small.State(f.Big) != PairingState.Paired && f.Big.State(f.Small) == (change == "revoke" ? PairingState.Revoked : PairingState.Unpaired), "new responder final transaction respects captured scope/trust: " + change);
        }
        await using (var f = new Pair())
        {
            int current = 1; using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            f.Big.Faults.Read = op => { if (op == "GetLocalIdentitySnapshot") { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(5))) throw new Exception("Coordinator scope-readiness barrier timeout"); } };
            var opening = f.Big.Coordinator.OpenWindowAsync(f.Small.Identity.PeerId, () => Volatile.Read(ref current) == 1);
            if (!await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(4)))) throw new Exception("Coordinator real read-completion barrier missing");
            current = 0; release.Set();
            Check(!await opening && f.Big.Coordinator.View.Stage != PairingStage.Listening && f.Big.Number("SELECT count(*) FROM Peer") == 0, "scope loss during real SQL read completion cannot publish a newly ready responder");
        }
        await using (var f = new Pair(timers: false))
        {
            f.Small.Endpoint.Release.TrySetResult(); f.Big.Endpoint.Release.TrySetResult();
            await f.Small.Coordinator.OpenWindowAsync(f.Big.Identity.PeerId, () => true);
            await f.Big.Coordinator.BeginAsync("127.0.0.1", f.Small.Port, f.Small.Identity.PeerId, () => true);
            string old = f.Big.Coordinator.View.SessionId!; using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            f.Big.Faults.Read = op => { if (op == "GetLocalIdentitySnapshot") { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(5))) throw new Exception("New-intent display readiness barrier timeout"); } };
            var beginning = f.Big.Coordinator.BeginAsync("127.0.0.1", f.Small.Port, f.Small.Identity.PeerId, () => true);
            if (!await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(4)))) throw new Exception("New-intent display real metadata read barrier missing");
            var displayed = f.Big.Coordinator.View; release.Set(); await beginning;
            Check(displayed is { Sas: null, SessionId: null, Consent: null } && displayed.Stage != PairingStage.Paired, "new intent awaiting actual SQL readiness cannot display the prior actor SAS/session/paired state: " + old);
        }
        await using (var f = new Pair(timers: false))
        {
            f.Small.Endpoint.Release.TrySetResult(); f.Big.Endpoint.Release.TrySetResult();
            await f.Small.Coordinator.OpenWindowAsync(f.Big.Identity.PeerId, () => true);
            await f.Big.Coordinator.BeginAsync("127.0.0.1", f.Small.Port, f.Small.Identity.PeerId, () => true);
            string old = f.Big.Coordinator.View.SessionId!;
            using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = f.Big.Store.DatabasePath, Pooling = false }.ToString()))
            { c.Open(); using var q = c.CreateCommand(); q.CommandText = "UPDATE LocalIdentity SET credentialRef='changed-synthetic-reference'"; q.ExecuteNonQuery(); }
            var next = await f.Big.Coordinator.BeginAsync("127.0.0.1", f.Small.Port, f.Small.Identity.PeerId, () => true);
            Check(next.Code != "awaiting_user" && f.Big.Coordinator.View is { Sas: null, SessionId: null, Consent: null } && f.Big.Coordinator.View.Stage != PairingStage.Paired,
                "failed fresh intent after durable identity change cannot redisplay the old SAS/session/result: " + old);
        }
        Console.WriteLine($"Conditional P1 cross-initiation: {checks} actual TCP/P256/two-store role ordering, intent deadline, cancellation, stale ticket and physical-drain checks; no human/WPF claim."); return checks;
    }
    internal static async Task<int> RunMitm()
    {
        int checks = 0; bool sawMismatch = false;
        void Check(bool value, string label) { checks++; if (!value) throw new Exception("P1 actual MITM: " + label); }
        // Real CSPRNG can legitimately collide in a six-digit SAS. At most3 independent fixture scenarios; this is not a collision-rate/security proof.
        for (int scenario = 0; scenario < 3 && !sawMismatch; scenario++)
        {
            await using var a = new Node(ECDsa.Create(ECCurve.NamedCurves.nistP256), TimeProvider.System, false);
            await using var b = new Node(ECDsa.Create(ECCurve.NamedCurves.nistP256), TimeProvider.System, false);
            await using var mr = new Node(ECDsa.Create(ECCurve.NamedCurves.nistP256), TimeProvider.System, false);
            await using var mi = new Node(ECDsa.Create(ECCurve.NamedCurves.nistP256), TimeProvider.System, false);
            foreach (var n in new[] { a, b, mr, mi }) n.Endpoint.Release.TrySetResult();
            await mr.Coordinator.OpenWindowAsync(a.Identity.PeerId, () => true); await b.Coordinator.OpenWindowAsync(null, () => true);
            Check((await a.Coordinator.BeginAsync("127.0.0.1", mr.Port, null, () => true)).Code == "awaiting_user" &&
                (await mi.Coordinator.BeginAsync("127.0.0.1", b.Port, b.Identity.PeerId, () => true)).Code == "awaiting_user", "malicious two-identity relay legitimately completes both real commitment/reveal handshakes");
            Check(a.Coordinator.View.PeerId == mr.Identity.PeerId && b.Coordinator.View.PeerId == mi.Identity.PeerId && a.Coordinator.View.Sas == mr.Coordinator.View.Sas && b.Coordinator.View.Sas == mi.Coordinator.View.Sas,
                "real substituted public keys are bound to each separate transcript, not to the users' intended A/B identity");
            Check(a.State(mr) == PairingState.Pending && b.State(mi) == PairingState.Pending && a.State(b) is null && b.State(a) is null, "unknown expected identity never bypasses human consent or creates intended-peer trust");
            if (a.Coordinator.View.Sas == b.Coordinator.View.Sas)
            { await a.Coordinator.CancelAsync(); await b.Coordinator.CancelAsync(); await mr.Coordinator.CancelAsync(); await mi.Coordinator.CancelAsync(); continue; }
            sawMismatch = true;
            Check((await mr.Coordinator.ConfirmAsync(mr.Coordinator.View.Consent!)).Code == "local_confirmed", "attacker may confirm its own R leg without obtaining user's I action");
            var forgedLocalAction = mi.Coordinator.ConfirmAsync(mi.Coordinator.View.Consent!);
            try
            {
                await Until(() => b.Coordinator.View.RemoteConfirmed && Volatile.Read(ref b.Endpoint.LastIConfirmation) is not null);
                Check(a.State(mr) == PairingState.Pending && b.State(mi) == PairingState.Pending && !a.Coordinator.View.LocalConfirmed && !b.Coordinator.View.LocalConfirmed, "valid remote proofs/attacker local actions do not replace A/B human comparison");
                byte[] other = Volatile.Read(ref b.Endpoint.LastIConfirmation)!; PairingWire.TryParse(other, out var packet);
                Check((await Post(mr.Port, other)).StatusCode == 409, "actual other-leg confirmation cannot splice the session id");
                Check((await Post(mr.Port, PairingWire.ConfirmationSignature(a.Coordinator.View.SessionId!, packet!.SignatureBase64!))).StatusCode == 403 && mr.State(a) == PairingState.Pending,
                    "rebinding the other-leg signature to current sid still fails real role/key/transcript verification");
                Check(await a.Coordinator.RejectAsync(a.Coordinator.View.Consent!) && await b.Coordinator.RejectAsync(b.Coordinator.View.Consent!), "both intended users explicitly reject their different displayed SAS values");
                await mi.Coordinator.CancelAsync(); await forgedLocalAction.WaitAsync(TimeSpan.FromSeconds(5));
                Check(a.State(mr) == PairingState.Unpaired && b.State(mi) == PairingState.Unpaired && mr.State(a) == PairingState.Unpaired && mi.State(b) == PairingState.Unpaired && a.State(b) is null && b.State(a) is null,
                    "different-SAS rejection leaves no paired identity on either real network leg");
                Check(new[] { a, b, mr, mi }.All(n => n.Number("SELECT count(*) FROM Message") == 0 && n.Number("SELECT count(*) FROM ManualSendAttempt") == 0), "malicious pairing control creates no private message or send attempt");
            }
            finally { await mi.Coordinator.CancelAsync(); await forgedLocalAction.WaitAsync(TimeSpan.FromSeconds(5)); }
        }
        Check(sawMismatch, "bounded real-CSPRNG fixture produced at least one unequal-SAS MITM case, not a claimed exhaustive security proof");
        Console.WriteLine($"Conditional P1 actual MITM: {checks} real four-node/two-leg TCP/P256/SQLite key-substitution, signature-splice and explicit mismatch-rejection checks; no human/collision-rate proof."); return checks;
    }
    private static async Task Until(Func<bool> predicate) { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4)); while (!predicate()) await Task.Delay(10, timeout.Token); }
    private static async Task<V2HttpResponse> Post(int port, byte[] body)
    {
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(4) };
        using var content = new ByteArrayContent(body); content.Headers.ContentType = new("application/json"); using var response = await http.PostAsync($"http://127.0.0.1:{port}/nekochat/v2/pair", content);
        return new((int)response.StatusCode, await response.Content.ReadAsByteArrayAsync());
    }
    private sealed class Pair : IAsyncDisposable
    {
        internal Node Small { get; }
        internal Node Big { get; }
        internal Clock Clock { get; } = new();
        internal Pair(bool fakeClock = false, bool timers = true) { Small = new(Key('_'), fakeClock ? Clock : TimeProvider.System, !fakeClock && timers); Big = new(Key('a'), fakeClock ? Clock : TimeProvider.System, !fakeClock && timers); }
        private static ECDsa Key(char group)
        {
            for (int i = 0; i < 1024; i++) { var key = ECDsa.Create(ECCurve.NamedCurves.nistP256); char first = V2Crypto.PeerId(key.ExportSubjectPublicKeyInfo())[0]; if (group == '_' ? first == '_' : first is >= 'a' and <= 'z') return key; key.Dispose(); }
            throw new Exception("Synthetic fixture could not find ordinal/case-folding counterexample key.");
        }
        internal async Task BothRequests() => await Task.WhenAll(Small.Endpoint.Arrived.Task, Big.Endpoint.Arrived.Task).WaitAsync(TimeSpan.FromSeconds(3));
        public async ValueTask DisposeAsync() { Small.Endpoint.Release.TrySetResult(); Big.Endpoint.Release.TrySetResult(); await Small.DisposeAsync(); await Big.DisposeAsync(); }
    }
    private sealed class Node : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "neko-p1-cross-" + Guid.NewGuid().ToString("N"));
        internal Faults Faults { get; } = new();
        internal ChatStore Store { get; }
        internal PairingIdentity Identity { get; }
        internal PairingCoordinator Coordinator { get; }
        internal Gate Endpoint { get; }
        internal V2PacketWriter Writer { get; }
        internal V2LanClient Messaging { get; }
        internal V2TcpListener Listener { get; }
        internal int Port => Listener.Port;
        internal Node(ECDsa key, TimeProvider clock, bool timers)
        {
            var parameters = key.ExportParameters(true); try { Writer = new(ECDsa.Create(parameters), 16384); } finally { CryptographicOperations.ZeroMemory(parameters.D!); }
            Store = ChatStore.Open(new(_root) { Faults = Faults, TextValidator = t => V2Text.Check(t, 16384) == V2TextProblem.None });
            Store.EnsureLocalIdentity(Writer.PeerId, Convert.FromBase64String(Writer.PublicSpkiBase64), "fixture"); Identity = new(Store, key); Coordinator = new(Identity, clock, timers); Endpoint = new(Coordinator);
            Messaging = new(Store, Writer); Listener = new(new V2Receiver(Store, Writer), new() { BindAddress = IPAddress.Loopback, Port = 0, RequestsPerSourcePerMinute = 10000 }, Endpoint); Listener.Start();
        }
        internal PairingState? State(Node other) => Store.GetPeerSecuritySnapshot(other.Identity.PeerId).Pairing;
        internal long Number(string sql) { using var c = Connection(); using var q = c.CreateCommand(); q.CommandText = sql; return Convert.ToInt64(q.ExecuteScalar()); }
        private SqliteConnection Connection() { var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Store.DatabasePath, Pooling = false }.ToString()); c.Open(); return c; }
        internal PreparedSend Prepare(Node other, string text)
        {
            using var c = Connection(); using var q = c.CreateCommand(); q.CommandText = "SELECT id FROM Conversation WHERE peerId=@peer"; q.Parameters.AddWithValue("@peer", other.Identity.PeerId); string conversation = (string)q.ExecuteScalar()!;
            var draft = Store.GetDraftState(conversation)!; Store.SaveDraft(conversation, text, draft.Epoch, draft.Revision + 1); var attempt = (AttemptStarted)Store.BeginManualSend(conversation, text, draft.Epoch, draft.Revision + 1); return Store.GetPreparedSend(attempt.MessageId)!;
        }
        public async ValueTask DisposeAsync() { Endpoint.Release.TrySetResult(); await Listener.DisposeAsync(); await Coordinator.DisposeAsync(); Messaging.Dispose(); Identity.Dispose(); Writer.Dispose(); Store.Dispose(); Directory.Delete(_root, true); }
    }
    private sealed class Gate(PairingCoordinator inner) : IPairingEndpoint
    {
        internal TaskCompletionSource Arrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal string? FirstSession;
        internal byte[]? LastIConfirmation;
        internal TaskCompletionSource ConfirmationUnavailable { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _first;
        public async Task<V2HttpResponse> HandleAsync(ReadOnlyMemory<byte> body, string source, CancellationToken cancellation = default)
        {
            if (PairingWire.TryParse(body.Span, out var packet) && packet!.Kind == PairingKind.Request && Interlocked.CompareExchange(ref _first, 1, 0) == 0)
            { FirstSession = packet.SessionId; Arrived.TrySetResult(); await Release.Task; }
            if (packet?.Kind == PairingKind.Confirmation) Volatile.Write(ref LastIConfirmation, body.ToArray());
            var response = await inner.HandleAsync(body, source, cancellation);
            if (response.StatusCode == 403 && packet?.Kind == PairingKind.Confirmation) ConfirmationUnavailable.TrySetResult();
            return response;
        }
        public Task StopAsync() { Release.TrySetResult(); return inner.StopAsync(); }
    }
    private sealed class Faults : IStoreFaults
    {
        internal Action<string>? Before, Read;
        public void AfterBegin(string op) { }
        public void BeforeCommit(string op) => Before?.Invoke(op);
        public void AfterRead(string op) => Read?.Invoke(op);
        public void AfterCommit(string op) { }
    }
    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        internal void Advance(double seconds) => Interlocked.Add(ref _ticks, (long)(seconds * TimeSpan.TicksPerSecond));
    }
}
