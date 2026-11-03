using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NekoChat.Core.Pairing;
using NekoChat.Core.Roster;
using NekoChat.Core.Storage;
using NekoChat.Core.V2;

namespace NekoChat.Tests;

internal static class PairingRosterChecks
{
    internal static async Task<int> Run()
    {
        int checks = 0;
        void Check(bool value, string label) { checks++; if (!value) throw new Exception("P1 roster transaction: " + label); }
        using (var f = new Db())
        {
            var target = f.Target; var candidate = f.Candidate(target);
            Check(target.DisplayName == f.Name && target.StudentNumber == "001" && target.IsTeacher && target.Endpoint is null && target.BoundPeerId is null && f.Store.IsPairingRosterTargetCurrent(target) && !target.ToString().Contains(f.Name), "capture immutable local selection without credential/private/name logging");
            Check(candidate.Ready && candidate.Current.Pairing == PairingState.Pending && f.Row.PeerId is null && f.Number("SELECT count(*) FROM Message") == 0, "candidate prepares key but never binds roster or sends private messages");
            var result = f.Store.CompleteRosterPairingIfCurrent(candidate.Current, target, () => true);
            Check(result is { Completed: true, BindingChanged: true } && f.Row.PeerId == f.Peer && f.Security.Pairing == PairingState.Paired && f.Row.DisplayName == f.Name && f.Row.IsTeacher && f.Row.StudentNumber == "001" && !f.Store.GetReadReceiptPolicy().Enabled, "one final transaction changes trust and binding while preserving labels/privacy");
            Check(!f.Store.IsPairingRosterTargetCurrent(target) && !f.Store.CompleteRosterPairingIfCurrent(candidate.Current, target, () => true).Completed, "old pre-binding selection is never adopted as fresh baseline");
            long? before = f.Security.PeerRevision; var current = f.Target;
            Check(f.Store.CompleteRosterPairingIfCurrent(f.Security, current, () => true) is { Completed: true, BindingChanged: false, Reason: "unchanged" } && f.Security.PeerRevision == before && f.Store.IsPairingRosterTargetCurrent(current), "already paired/bound is guarded idempotent success without revision churn");
        }
        foreach (string change in new[] { "name", "student", "teacher", "endpoint", "route-aba", "binding-aba", "delete", "recreate", "local-aba", "revoke", "peer-aba" })
        {
            using var f = new Db(); var target = f.Target; var candidate = f.Candidate(target);
            if (change == "name") f.Edit(name: "合成變更名稱");
            if (change == "student") f.Edit(number: "002");
            if (change == "teacher") f.Edit(teacher: false);
            if (change is "endpoint" or "route-aba") { f.Edit(endpoint: "10.0.0.9"); if (change == "route-aba") f.Edit(); }
            if (change == "binding-aba") { f.RegisterOther(); f.Store.BindPairedRosterPeer(f.RowId, f.Other, f.Token); f.Store.BindPairedRosterPeer(f.RowId, null, f.Token); }
            if (change is "delete" or "recreate") { f.Store.DeleteRosterEntry(f.RowId, f.Token); if (change == "recreate") f.Recreate(); }
            if (change == "local-aba") f.Execute("UPDATE LocalIdentity SET credentialRef='other'; UPDATE LocalIdentity SET credentialRef='fixture'");
            if (change == "revoke") f.Store.SetPairingState(f.Peer, PairingState.Revoked);
            if (change == "peer-aba") { f.Store.SetPairingState(f.Peer, PairingState.Revoked); f.Store.SetPairingState(f.Peer, PairingState.Pending); }
            var completed = f.Store.CompleteRosterPairingIfCurrent(candidate.Current, target, () => true);
            Check(!completed.Completed && f.Security.Pairing != PairingState.Paired && f.Store.GetRosterSnapshot().All(r => r.Entry.PeerId != f.Peer), "captured selection/peer epoch rejects without half binding: " + change);
            if (change == "revoke") Check(f.Security.Pairing == PairingState.Revoked, "stale pairing never resurrects revoked trust");
        }
        using (var f = new Db())
        {
            var target = f.Target; f.Edit(name: "候選前名稱變更");
            Check(!f.Candidate(target).Ready && f.Number("SELECT count(*) FROM Peer") == 0 && f.Number("SELECT count(*) FROM Conversation") == 0, "stale row rejected in candidate transaction before pin/conversation creation");
        }
        using (var f = new Db())
        {
            var target = f.Target;
            f.Execute("CREATE TRIGGER fixture_mutation AFTER INSERT ON Peer BEGIN UPDATE RosterEntry SET displayName='unexpected synthetic candidate'; END");
            Check(!f.Candidate(target).Ready && f.Number("SELECT count(*) FROM Peer") == 0 && f.Number("SELECT count(*) FROM Conversation") == 0 && f.Row.DisplayName == f.Name, "candidate final durable guard rolls back unexpected metadata, key pin and conversation together");
        }
        using (var f = new Db())
        {
            f.Candidate(f.Target); f.Execute("UPDATE LocalIdentity SET credentialRef=NULL");
            Check(!f.Store.CompleteRosterPairingIfCurrent(f.Security, f.Target, () => true).Completed && f.Row.PeerId is null && f.Security.Pairing == PairingState.Pending, "fresh roster selection is not proof of a provisioned local signing identity");
        }
        using (var a = new Db())
        using (var b = new Db())
        {
            Check(!b.Store.IsPairingRosterTargetCurrent(a.Target) && !b.Store.BeginRosterPairingCandidateIfCurrent(b.Security, b.Public, a.Target, () => true).Ready && b.Number("SELECT count(*) FROM Peer") == 0, "another store/runtime cannot reuse an opaque selection");
            var old = a.Target; var candidate = a.Candidate(old); a.Reopen();
            Check(!a.Store.IsPairingRosterTargetCurrent(old) && !a.Store.CompleteRosterPairingIfCurrent(candidate.Current, old, () => true).Completed, "same DB/revisions after reopen still require a fresh local selection");
            Check(a.Store.CompleteRosterPairingIfCurrent(a.Security, a.Target, () => true).Completed, "explicit fresh selection after reopen can complete unchanged pending candidate");
        }
        using (var f = new Db())
        {
            var target = f.Target; var candidate = f.Candidate(target);
            f.Store.SaveManualRosterEntry(null, new("另一個合成列", "099"), f.Token);
            Check(f.Store.IsPairingRosterTargetCurrent(target) && f.Store.CompleteRosterPairingIfCurrent(candidate.Current, target, () => true).Completed && f.Store.GetRosterSnapshot().Single(r => r.Entry.Id != f.RowId).Entry.PeerId is null, "unrelated roster rows neither invalidate nor inherit selected binding");
        }
        using (var f = new Db())
        {
            f.RegisterOther(); f.Store.BindPairedRosterPeer(f.RowId, f.Other, f.Token); var target = f.Target;
            Check(!f.Candidate(target).Ready && f.Row.PeerId == f.Other && !f.Security.PeerExists, "different existing binding needs explicit unbind/review, never automatic reassignment");
        }
        foreach (string failure in new[] { "cancel", "before-io", "after-io", "row-trigger", "peer-trigger", "row-overflow", "peer-overflow" })
        {
            using var f = new Db(); if (failure == "row-overflow") f.Execute("UPDATE RosterSecurityRevision SET revision=9223372036854775807");
            var target = f.Target; var candidate = f.Candidate(target); bool live = true, threw = false;
            if (failure == "peer-overflow") { f.Execute("UPDATE PeerSecurityRevision SET revision=9223372036854775807"); candidate = candidate with { Current = f.Security }; }
            if (failure == "cancel") f.Faults.Before = op => { if (op == "pair-and-bind") live = false; };
            if (failure == "before-io") f.Faults.Before = op => { if (op == "pair-and-bind") throw new IOException("synthetic transaction failure"); };
            if (failure == "after-io") f.Faults.After = op => { if (op == "pair-and-bind") throw new IOException("synthetic transaction failure"); };
            if (failure == "row-trigger") f.Execute("CREATE TRIGGER fixture_mutation AFTER UPDATE OF peerId ON RosterEntry BEGIN UPDATE RosterEntry SET displayName='unexpected synthetic value' WHERE id=NEW.id; END");
            if (failure == "peer-trigger") f.Execute("CREATE TRIGGER fixture_mutation AFTER UPDATE OF pairingState ON Peer BEGIN UPDATE PeerSecurityRevision SET revision=revision+1 WHERE peerId=NEW.id; END");
            RosterPairingResult? result = null;
            try { result = f.Store.CompleteRosterPairingIfCurrent(candidate.Current, target, () => live); } catch (IOException) { threw = true; }
            bool committed = failure == "after-io";
            Check((f.Security.Pairing == PairingState.Paired) == committed && (f.Row.PeerId == f.Peer) == committed && f.Row.DisplayName == f.Name && (failure.EndsWith("io", StringComparison.Ordinal) ? threw : result?.Completed == false), "exact post-state guard/rollback never accepts a newer snapshot: " + failure);
        }
        using (var f = new Db())
        using (var second = ChatStore.Open(new(f.Root)))
        {
            var candidate = f.Candidate(f.Target); var a = f.Target; var b = second.CapturePairingRosterTarget(f.RowId)!; var expected = candidate.Current; using var start = new Barrier(2);
            async Task<RosterPairingResult> Complete(ChatStore store, PairingRosterTarget target) => await Task.Run(() => { if (!start.SignalAndWait(TimeSpan.FromSeconds(5))) throw new Exception("Pair-and-bind race barrier"); return store.CompleteRosterPairingIfCurrent(expected, target, () => true); });
            var results = await Task.WhenAll(Complete(f.Store, a), Complete(second, b));
            Check(results.Count(r => r.Completed) == 1 && f.Row.PeerId == f.Peer && f.Security.PeerRevision == expected.PeerRevision + 1, "two real SQLite handles with independent original selections commit exactly one binding/trust transition");
        }
        foreach (string scenario in new[] { "ok", "cross", "edit-i", "edit-r", "route-aba" })
        {
            await using var a = new Node("本機合成乙"); await using var b = new Node("本機合成甲"); var ai = a.Data.Target; var bi = b.Data.Target;
            if (scenario == "cross")
            {
                a.Endpoint.Hold = b.Endpoint.Hold = true;
                var ab = a.Coordinator.BeginRosterAsync(ai, "127.0.0.1", b.Port, () => true); var ba = b.Coordinator.BeginRosterAsync(bi, "127.0.0.1", a.Port, () => true);
                await Task.WhenAll(a.Endpoint.Arrived.Task, b.Endpoint.Arrived.Task).WaitAsync(TimeSpan.FromSeconds(3)); a.Endpoint.Release.TrySetResult(); b.Endpoint.Release.TrySetResult(); await Task.WhenAll(ab, ba);
            }
            else { await b.Coordinator.OpenRosterWindowAsync(bi, () => true); Check((await a.Coordinator.BeginRosterAsync(ai, "127.0.0.1", b.Port, () => true)).Code == "awaiting_user", "actual scoped TCP handshake: " + scenario); }
            Node i = a.Coordinator.Role == PairingSessionRole.Initiator ? a : b, r = ReferenceEquals(i, a) ? b : a;
            Check(a.Coordinator.View.Sas == b.Coordinator.View.Sas && ReferenceEquals(a.Coordinator.View.Consent!.RosterTarget, ai) && ReferenceEquals(b.Coordinator.View.Consent!.RosterTarget, bi) && a.Data.Row.PeerId is null && b.Data.Row.PeerId is null, "each ticket retains its own immutable row, including cross-role switch: " + scenario);
            if (scenario == "edit-i") i.Data.Edit(name: "核對後合成更名");
            if (scenario == "edit-r") r.Data.Edit(name: "核對後合成更名");
            if (scenario == "route-aba") { i.Data.Edit(endpoint: "10.0.0.9"); i.Data.Edit(); }
            var rc = r.Coordinator.View.Consent!; var ic = i.Coordinator.View.Consent!; await r.Coordinator.ConfirmAsync(rc); var done = await i.Coordinator.ConfirmAsync(ic);
            if (scenario is "ok" or "cross")
            {
                Check(done.Code == "paired" && a.Data.Row.PeerId == b.Identity.PeerId && b.Data.Row.PeerId == a.Identity.PeerId && a.Data.Row.Endpoint is null && b.Data.Row.Endpoint is null, "real two-sided final trust/binding preserves endpoints and requires both tickets: " + scenario);
                using var messaging = new V2LanClient(a.Data.Store, a.Writer); var sent = await messaging.SendPreparedAsync(a.Prepare(b.Identity.PeerId), "127.0.0.1", b.Port, () => true);
                Check(sent is { Code: "ok", Completion: Confirmed { DraftCleared: true } } && b.Data.Number("SELECT count(*) FROM Message") == 1, "bound identity immediately authorizes real signed message/storage ACK: " + scenario);
            }
            else Check(done.Code != "paired" && i.Data.Row.PeerId is null && r.Data.Row.PeerId is null && i.Data.Store.GetPeerSecuritySnapshot(r.Identity.PeerId).Pairing != PairingState.Paired, "post-SAS metadata/route changes cannot bind either row from stale dialog: " + scenario);
            Check(a.Endpoint.Frames.Concat(b.Endpoint.Frames).All(bytes => !HasLabel(bytes, a.Data.Name) && !HasLabel(bytes, b.Data.Name)), "private local roster labels never enter pairing wire: " + scenario);
        }
        await using (var n = new Node("路由合成列"))
        {
            n.Data.Edit(endpoint: "10.0.0.7"); var target = n.Data.Target;
            Check((await n.Coordinator.BeginRosterAsync(target, "127.0.0.1", n.Port, () => true)).Code == "roster_route_mismatch" && n.Data.Number("SELECT count(*) FROM Peer") == 0, "explicit outgoing route must match captured declared endpoint");
            await n.Coordinator.OpenRosterWindowAsync(target, () => true); using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            Check((await Post(n.Port, PairingWire.Request(PairingWire.NewNonce(), Convert.ToBase64String(other.ExportSubjectPublicKeyInfo())))).StatusCode == 403 && n.Data.Number("SELECT count(*) FROM Peer") == 0, "incoming actual127 source cannot use a row preselected for another declared IP");
        }
        await using (var n = new Node("失效列合成"))
        {
            var stale = n.Data.Target; n.Data.Edit(name: "明確變更後合成");
            Check(!await n.Coordinator.OpenRosterWindowAsync(stale, () => true) && n.Coordinator.Role == PairingSessionRole.None && n.Coordinator.View is { Stage: PairingStage.Ended, Consent: null }, "failed stale roster open ends intent instead of displaying an indefinitely preparing window");
        }
        Console.WriteLine($"Conditional P1 roster: {checks} real SQLite/TCP selected-row, immutable consent, ABA, atomic trust/binding, post-state, concurrency and privacy checks; no human/WPF claim."); return checks;
    }
    private static bool HasLabel(byte[] packet, string label)
    {
        using var doc = JsonDocument.Parse(packet);
        bool Has(JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.String) return e.GetString()!.Contains(label, StringComparison.Ordinal);
            if (e.ValueKind == JsonValueKind.Object) return e.EnumerateObject().Any(p => p.Name.Contains(label, StringComparison.Ordinal) || Has(p.Value));
            return e.ValueKind == JsonValueKind.Array && e.EnumerateArray().Any(Has);
        }
        return Has(doc.RootElement);
    }
    private static async Task<V2HttpResponse> Post(int port, byte[] bytes)
    { using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(4) }; using var body = new ByteArrayContent(bytes); body.Headers.ContentType = new("application/json"); using var response = await client.PostAsync($"http://127.0.0.1:{port}/nekochat/v2/pair", body); return new((int)response.StatusCode, await response.Content.ReadAsByteArrayAsync()); }
    private static ECDsa Clone(ECDsa key) { var p = key.ExportParameters(true); try { return ECDsa.Create(p); } finally { CryptographicOperations.ZeroMemory(p.D!); } }
    private sealed class Db : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "neko-pair-roster-" + Guid.NewGuid().ToString("N"));
        internal readonly ECDsa Key = ECDsa.Create(ECCurve.NamedCurves.nistP256), Remote = ECDsa.Create(ECCurve.NamedCurves.nistP256), Third = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        internal Faults Faults { get; } = new();
        internal ChatStore Store { get; private set; }
        internal string Name { get; }
        internal string RowId { get; }
        internal byte[] Public => Remote.ExportSubjectPublicKeyInfo();
        internal string Peer => V2Crypto.PeerId(Public);
        internal string Other => V2Crypto.PeerId(Third.ExportSubjectPublicKeyInfo());
        internal PeerSecuritySnapshot Security => Store.GetPeerSecuritySnapshot(Peer);
        internal RosterEntryView Row => Store.GetRosterSnapshot().Single(r => r.Entry.Id == RowId).Entry;
        internal PairingRosterTarget Target => Store.CapturePairingRosterTarget(RowId)!;
        internal string Token => RosterImportPreview.ComputeSnapshotToken(Store.GetRosterSnapshot());
        internal Db(string name = "本機範例名單")
        {
            Name = name; Store = Open(); Store.EnsureLocalIdentity(V2Crypto.PeerId(Key.ExportSubjectPublicKeyInfo()), Key.ExportSubjectPublicKeyInfo(), "fixture");
            RowId = Store.SaveManualRosterEntry(null, new(Name, "001", true), Token).EntryId!;
        }
        private ChatStore Open() => ChatStore.Open(new(Root) { Faults = Faults, TextValidator = t => V2Text.Check(t, 16384) == V2TextProblem.None });
        internal void Reopen() { Store.Dispose(); Store = Open(); }
        internal PairingCandidateResult Candidate(PairingRosterTarget target) => Store.BeginRosterPairingCandidateIfCurrent(Security, Public, target, () => true);
        internal void Edit(string? name = null, string number = "001", bool teacher = true, string? endpoint = null) => Store.SaveManualRosterEntry(RowId, new(name ?? Name, number, teacher, endpoint), Token);
        internal void RegisterOther() => Store.RegisterPeer(Other, PairingState.Paired, Third.ExportSubjectPublicKeyInfo());
        internal void Recreate() { using var c = Connection(); using var q = c.CreateCommand(); q.CommandText = "INSERT INTO RosterEntry(id,displayName,studentNumber,isTeacher,endpoint,peerId,source,importBatchId) VALUES(@id,@name,'001',1,NULL,NULL,'manual',NULL)"; q.Parameters.AddWithValue("@id", RowId); q.Parameters.AddWithValue("@name", Name); q.ExecuteNonQuery(); }
        internal SqliteConnection Connection() { var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Store.DatabasePath, Pooling = false }.ToString()); c.Open(); return c; }
        internal void Execute(string sql) { using var c = Connection(); using var q = c.CreateCommand(); q.CommandText = sql; q.ExecuteNonQuery(); }
        internal long Number(string sql) { using var c = Connection(); using var q = c.CreateCommand(); q.CommandText = sql; return Convert.ToInt64(q.ExecuteScalar()); }
        public void Dispose() { Store.Dispose(); Key.Dispose(); Remote.Dispose(); Third.Dispose(); Directory.Delete(Root, true); }
    }
    private sealed class Node : IAsyncDisposable
    {
        internal Db Data { get; }
        internal PairingIdentity Identity { get; }
        internal PairingCoordinator Coordinator { get; }
        internal V2PacketWriter Writer { get; }
        internal Capture Endpoint { get; }
        internal V2TcpListener Listener { get; }
        internal int Port => Listener.Port;
        internal Node(string name) { Data = new(name); Identity = new(Data.Store, Clone(Data.Key)); Writer = new(Clone(Data.Key), 16384); Coordinator = new(Identity, TimeProvider.System, false); Endpoint = new(Coordinator); Listener = new(new V2Receiver(Data.Store, Writer), new() { BindAddress = IPAddress.Loopback, Port = 0, RequestsPerSourcePerMinute = 10000 }, Endpoint); Listener.Start(); }
        internal PreparedSend Prepare(string peer)
        { using var c = Data.Connection(); using var q = c.CreateCommand(); q.CommandText = "SELECT id FROM Conversation WHERE peerId=@peer"; q.Parameters.AddWithValue("@peer", peer); string id = (string)q.ExecuteScalar()!; var d = Data.Store.GetDraftState(id)!; Data.Store.SaveDraft(id, "scoped synthetic message", d.Epoch, d.Revision + 1); var started = (AttemptStarted)Data.Store.BeginManualSend(id, "scoped synthetic message", d.Epoch, d.Revision + 1); return Data.Store.GetPreparedSend(started.MessageId)!; }
        public async ValueTask DisposeAsync() { Endpoint.Release.TrySetResult(); await Listener.DisposeAsync(); await Coordinator.DisposeAsync(); Identity.Dispose(); Writer.Dispose(); Data.Dispose(); }
    }
    private sealed class Capture(PairingCoordinator coordinator) : IPairingEndpoint
    {
        internal bool Hold;
        internal TaskCompletionSource Arrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal List<byte[]> Frames { get; } = new();
        public async Task<V2HttpResponse> HandleAsync(ReadOnlyMemory<byte> body, string source, CancellationToken cancellation = default)
        { lock (Frames) Frames.Add(body.ToArray()); if (Hold && PairingWire.TryParse(body.Span, out var p) && p!.Kind == PairingKind.Request) { Arrived.TrySetResult(); await Release.Task; } var response = await coordinator.HandleAsync(body, source, cancellation); lock (Frames) Frames.Add(response.Body.ToArray()); return response; }
        public Task StopAsync() { Release.TrySetResult(); return coordinator.StopAsync(); }
    }
    private sealed class Faults : IStoreFaults
    {
        internal Action<string>? Before, After;
        public void AfterBegin(string op) { }
        public void BeforeCommit(string op) => Before?.Invoke(op);
        public void AfterCommit(string op) => After?.Invoke(op);
    }
}
