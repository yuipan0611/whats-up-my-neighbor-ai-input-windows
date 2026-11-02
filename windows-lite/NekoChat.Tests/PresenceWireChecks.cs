using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using NekoChat.Core;
using NekoChat.Core.Presence;
using NekoChat.Core.Roster;
using NekoChat.Core.Storage;
using NekoChat.Core.V2;

namespace NekoChat.Tests;

internal static class PresenceWireChecks
{
    public static async Task<int> Run()
    {
        int checks = 0;
        void Check(bool ok, string name) { checks++; if (!ok) throw new Exception("Signed presence: " + name); }
        await using (var f = new Fixture())
        {
            string paired = f.Add("Paired", "10.0.0.1", true), unbound = f.Add("Unbound", "10.0.0.2", false), hidden = f.Add("Hidden", "10.0.0.3", false);
            f.Service.Start(); await Until(() => f.Service.GetSnapshot(paired) is not null); await Task.Delay(100);
            Check(f.Connections == 0 && f.Service.GetSnapshot(paired)?.Presence == PresenceStatus.Unknown, "starting a hidden worker sends no network traffic and restores no Online");
            f.Service.SetView(true, new[] { paired }, unbound);
            await Until(() => f.Service.GetSnapshot(paired)?.Presence == PresenceStatus.Online && f.Service.GetSnapshot(unbound)?.EndpointResponds == true);
            Check(f.Service.GetSnapshot(paired) is { Installed: InstalledStatus.ConfirmedInstalled, Presence: PresenceStatus.Online } && f.Evidence == 1, "actual signed paired hello becomes Online only after SQLite evidence");
            Check(f.Service.GetSnapshot(unbound) is { Installed: InstalledStatus.Unknown, Presence: PresenceStatus.Unknown, EndpointResponds: true } && f.Service.GetSnapshot(hidden)?.EndpointResponds == false,
                "unbound fresh signed hello is E1 only; hidden rows receive no probe");
            Check(f.Dials.All(ip => ip is "10.0.0.1" or "10.0.0.2") && f.Dials.Contains("10.0.0.2"), "currently selected offscreen row is included but unrelated hidden row excluded");
            Check(Number(f.Store.DatabasePath, "SELECT count(*) FROM Peer") == 1 && Number(f.Store.DatabasePath, "SELECT count(*) FROM Message") == 0, "hello worker creates no peers, pairings or messages");
            f.Service.SetView(false, new[] { paired }, paired); await Until(() => f.Service.ActiveOperations == 0); int stopped = f.Connections;
            f.Clock.Advance(45); Check(f.Service.GetSnapshot(paired) is { Presence: PresenceStatus.Offline, Installed: InstalledStatus.ConfirmedInstalled }, "current45s exact TTL expires without forgetting installed evidence");
            await Task.Delay(300); Check(f.Connections == stopped, "hidden view stops even probes that became due while hidden");
            f.Service.ResetAfterResume(); Check(f.Service.GetSnapshot(paired) is { Presence: PresenceStatus.Unknown, Installed: InstalledStatus.ConfirmedInstalled, EndpointResponds: false }, "resume clears transient proof and retains installed memo");
            f.Service.SetView(true, new[] { paired }, paired); await Until(() => f.Service.GetSnapshot(paired)?.Presence == PresenceStatus.Online);
            Check(f.Evidence == 1, "fresh proof after resume recovers Online without adding peers or duplicate evidence rows");
        }
        foreach (PairingState state in new[] { PairingState.Unpaired, PairingState.Pending, PairingState.Suspended, PairingState.Revoked })
        {
            await using var f = new Fixture(); string row = f.Add("State", "10.0.0.1", true); f.Store.SetPairingState(f.B.PeerId, state);
            f.Service.SetView(true, new[] { row }, row); f.Service.Start(); await Until(() => f.Service.GetSnapshot(row)?.EndpointResponds == true);
            Check(f.Service.GetSnapshot(row) is { Presence: PresenceStatus.Unknown, Installed: InstalledStatus.Unknown } && f.Evidence == 0 && f.Store.GetPeerSecuritySnapshot(f.B.PeerId).Pairing == state,
                state + " valid signed hello remains E1 with zero trust/evidence writes");
        }
        await using (var f = new Fixture(jitter: 1))
        {
            string current = f.Add("Current", "10.0.0.1", true), visible = f.Add("Staggered", "10.0.0.2", false);
            f.Service.SetView(true, new[] { visible }, current); f.Service.Start(); await Until(() => f.Service.GetSnapshot(current)?.Presence == PresenceStatus.Online);
            Check(!f.Dials.Contains("10.0.0.2"), "view set before async initialization still staggers noncurrent visible rows");
            f.Clock.Advance(9.999); f.Service.Refresh(); await Task.Delay(300);
            Check(!f.Dials.Contains("10.0.0.2"), "startup10second stagger not due early");
            f.Clock.Advance(.001); f.Service.Refresh(); await Until(() => f.Service.GetSnapshot(visible)?.EndpointResponds == true);
            Check(f.Dials.Contains("10.0.0.2"), "staggered candidate probes at exact monotonic deadline");
        }
        foreach (string scenario in new[] { "bad-signature", "wrong-pin", "other-unpaired", "replay" })
        {
            await using var f = new Fixture(); string row = f.Add("Peer", "10.0.0.1", true); byte[]? previous = null;
            if (scenario == "other-unpaired") f.Store.SetPairingState(f.B.PeerId, PairingState.Pending);
            using var other = new V2PacketWriter(ECDsa.Create(ECCurve.NamedCurves.nistP256), 16384);
            await using var stub = new V2TcpChecks.ScriptedServer((_, body) => {
                using var p = JsonDocument.Parse(body); string cn = p.RootElement.GetProperty("client_nonce").GetString()!;
                byte[] reply = (scenario is "wrong-pin" or "other-unpaired" ? other : f.B).HelloReply(cn, V2PacketWriter.NewClientNonce());
                if (scenario == "bad-signature") { var o = JsonNode.Parse(reply)!.AsObject(); var signature = Convert.FromBase64String(o["proof_sig"]!.GetValue<string>()); signature[0] ^= 1; o["proof_sig"] = Convert.ToBase64String(signature); reply = JsonSerializer.SerializeToUtf8Bytes(o); }
                if (scenario == "replay") { previous ??= f.B.HelloReply(V2PacketWriter.NewClientNonce(), V2PacketWriter.NewClientNonce()); reply = previous; }
                return Task.FromResult<V2HttpResponse?>(new(200, reply));
            });
            f.DialPort = stub.Port; f.Service.SetView(true, new[] { row }, row); f.Service.Start();
            await Until(() => f.Service.GetSnapshot(row) is { Failures: > 0 } or { EndpointResponds: true });
            if (scenario == "other-unpaired") Check(f.Service.GetSnapshot(row) is { EndpointResponds: true, AddressConflict: true, Presence: PresenceStatus.Unknown } && f.Evidence == 0, "different self-proved unpaired identity reports address conflict, never updates bound peer");
            else Check(f.Service.GetSnapshot(row) is { EndpointResponds: false, Presence: PresenceStatus.Unknown, Installed: InstalledStatus.Unknown } && f.Evidence == 0, scenario + " provides no E1/E2 or persistent evidence");
            if (scenario == "wrong-pin") { int before = f.Connections; f.Clock.Advance(150); f.Service.Refresh(); await Task.Delay(350); Check(f.Connections == before && f.Service.GetSnapshot(row)?.ProbeSuppressed == true, "paired key mismatch suppresses future automatic probes until intent changes"); }
        }
        await using (var f = new Fixture())
        {
            string row = f.Add("ABA", "10.0.0.1", true); var target = f.Store.GetPresenceTarget(row)!;
            await using var stub = new V2TcpChecks.ScriptedServer((_, body) => {
                using var p = JsonDocument.Parse(body); f.Store.SetPairingState(f.B.PeerId, PairingState.Revoked); f.Store.SetPairingState(f.B.PeerId, PairingState.Paired);
                return Task.FromResult<V2HttpResponse?>(new(200, f.B.HelloReply(p.RootElement.GetProperty("client_nonce").GetString()!, V2PacketWriter.NewClientNonce())));
            });
            f.DialPort = stub.Port;
            var result = await f.Client.ProbeAsync(target, 8768, () => true);
            Check(result.Code == "stale_target" && f.Evidence == 0, "real hello response after durable revoke-restore cannot become proof for old target");
            Check(target.SameSecurityRoute(target with { LastSeenUtc = DateTime.UtcNow, RestoredInstalled = InstalledStatus.ConfirmedInstalled }) && !target.SameSecurityRoute(f.Store.GetPresenceTarget(row)),
                "route/security comparison ignores display memo but includes durable revisions");
        }
        await using (var f = new Fixture())
        {
            string row = f.Add("Late", "10.0.0.1", true); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var stub = new V2TcpChecks.ScriptedServer(async (_, body) => { entered.TrySetResult(); await release.Task; using var p = JsonDocument.Parse(body); return new(200, f.B.HelloReply(p.RootElement.GetProperty("client_nonce").GetString()!, V2PacketWriter.NewClientNonce())); });
            f.DialPort = stub.Port; f.Service.SetView(true, new[] { row }, row); f.Service.Start(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            f.Service.SetView(false, [], null); release.SetResult(); await Until(() => f.Service.ActiveOperations == 0);
            Check(f.Evidence == 0 && f.Service.GetSnapshot(row)?.Presence == PresenceStatus.Unknown, "hidden view rejects delayed signed reply even when TCP exchange later completes");
        }
        await using (var f = new Fixture())
        {
            string row = f.Add("Timeout", "10.0.0.1", true); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var stub = new V2TcpChecks.ScriptedServer(async (_, body) => { entered.TrySetResult(); await release.Task; using var p = JsonDocument.Parse(body); return new(200, f.B.HelloReply(p.RootElement.GetProperty("client_nonce").GetString()!, V2PacketWriter.NewClientNonce())); });
            f.DialPort = stub.Port; f.Service.SetView(true, new[] { row }, row); f.Service.Start(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); var watch = Stopwatch.StartNew();
            try
            {
                await Until(() => f.Service.ActiveOperations == 0);
                Check(watch.Elapsed < TimeSpan.FromSeconds(4) && f.Evidence == 0 && f.Service.GetSnapshot(row)?.Presence == PresenceStatus.Unknown, "real2second network deadline frees the physical slot while UI remains visible");
            }
            finally { release.TrySetResult(); }
            await Task.Delay(100); Check(f.Evidence == 0, "late valid signed response after network timeout cannot revive online proof");
        }
        await using (var f = new Fixture())
        {
            var rows = Enumerable.Range(1, 8).Select(i => f.Add("Peer" + i, "10.0.0." + i, true)).ToArray();
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            f.Faults.Before = op => { if (op == "roster-presence") { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(6))) throw new TimeoutException("blocked SQLite fixture"); } };
            try
            {
                f.Service.SetView(true, rows[..4], rows[0]); f.Service.Start();
                await Until(() => entered.IsSet && f.Service.ActiveOperations == 4 && f.Service.StorageOperations == 4); int before = f.Connections; var watch = Stopwatch.StartNew();
                f.Service.SetView(true, rows[4..], rows[4]); watch.Stop();
                await Task.Delay(150);
                if (!(watch.Elapsed < TimeSpan.FromMilliseconds(500) && f.Service.ActiveOperations == 4 && f.Connections == before))
                    Console.WriteLine($"Presence blocked stage: active={f.Service.ActiveOperations},storage={f.Service.StorageOperations},connectionsBefore={before},connectionsAfter={f.Connections},viewMs={watch.Elapsed.TotalMilliseconds:0}");
                Check(watch.Elapsed < TimeSpan.FromMilliseconds(500) && f.Service.ActiveOperations == 4 && f.Connections == before, "view changes stay responsive while SQLite blocked, and cancelled SQL waiters still occupy all four physical slots");
            }
            finally { f.Faults.Before = null; release.Set(); }
            await Until(() => f.Service.GetSnapshot(rows[4])?.Presence == PresenceStatus.Online);
            Check(f.Service.ActiveOperations <= 4 && Number(f.Store.DatabasePath, "SELECT count(*) FROM Peer WHERE installEvidence='appVerified'") == 1, "new visible probes resume only after old storage work drains");
        }
        await using (var f = new Fixture())
        {
            string row = f.Add("Storage failure", "10.0.0.1", true); f.Faults.Before = op => { if (op == "roster-presence") throw new IOException("private fixture"); };
            f.Service.SetView(true, new[] { row }, row); f.Service.Start(); await Until(() => f.Diagnostics.Contains("storage_unavailable"));
            Check(f.Evidence == 0 && f.Service.GetSnapshot(row)?.Presence == PresenceStatus.Unknown && !f.Diagnostics.Any(d => d.Contains("private fixture", StringComparison.Ordinal)), "signed proof cannot become Online when local persistence fails; diagnostics fixedgeneric");
            f.Faults.Before = null; f.Service.SetView(false, [], null); await Until(() => f.Service.ActiveOperations == 0);
            f.Service.SetView(true, new[] { row }, row); await Until(() => f.Service.GetSnapshot(row)?.Presence == PresenceStatus.Online);
            Check(f.Evidence == 1, "explicit view refresh after storage failure obtains fresh proof and recovers");
            await f.Service.DisposeAsync(); await f.Service.DisposeAsync(); Check(f.Service.ActiveOperations == 0 && f.Service.GetSnapshot(row) is null, "service disposal drains operations before borrowed store/client are disposed");
            Check(f.Store.GetPresenceTargets().Count == 1, "service disposal leaves borrowed store usable");
        }
        Console.WriteLine($"Signed presence worker: {checks} actual hello/SQLite, E1/E2, lifecycle, ABA and physical concurrency checks."); return checks;
    }
    private static async Task Until(Func<bool> condition)
    { for (int i = 0; i < 400; i++) { if (condition()) return; await Task.Delay(10); } throw new TimeoutException("signed presence fixture condition"); }
    private static long Number(string path, string sql)
    { using var c = new SqliteConnection("Data Source=" + path + ";Mode=ReadOnly;Pooling=False"); c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = sql; return Convert.ToInt64(cmd.ExecuteScalar()); }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "neko-presence-wire-" + Guid.NewGuid().ToString("N"));
        internal V2PacketWriter A { get; } = new(ECDsa.Create(ECCurve.NamedCurves.nistP256), 16384);
        internal V2PacketWriter B { get; } = new(ECDsa.Create(ECCurve.NamedCurves.nistP256), 16384);
        internal ChatStore Store { get; }
        private readonly ChatStore _remote;
        private readonly DirectLanListener _listener;
        internal V2LanClient Client { get; }
        internal PresenceService Service { get; }
        internal Clock Clock { get; } = new();
        internal Faults Faults { get; } = new();
        internal ConcurrentQueue<string> Dials { get; } = new();
        internal ConcurrentQueue<string> Diagnostics { get; } = new();
        internal int DialPort, Connections;
        internal long Evidence => Number(Store.DatabasePath, "SELECT count(*) FROM Peer WHERE installEvidence='appVerified'");
        internal Fixture(double jitter = 0)
        {
            Directory.CreateDirectory(_root); Store = ChatStore.Open(new(Path.Combine(_root, "a")) { Faults = Faults, UtcNow = () => Clock.GetUtcNow().UtcDateTime });
            _remote = ChatStore.Open(new(Path.Combine(_root, "b")));
            Store.EnsureLocalIdentity(A.PeerId, Convert.FromBase64String(A.PublicSpkiBase64), "fixture"); _remote.EnsureLocalIdentity(B.PeerId, Convert.FromBase64String(B.PublicSpkiBase64), "fixture");
            Store.RegisterPeer(B.PeerId, PairingState.Paired, Convert.FromBase64String(B.PublicSpkiBase64)); _remote.RegisterPeer(A.PeerId, PairingState.Paired, Convert.FromBase64String(A.PublicSpkiBase64));
            _listener = new(new V2Receiver(_remote, B), new V2ListenerOptions { BindAddress = IPAddress.Loopback, Port = 0, RequestsPerSourcePerMinute = 1000 }); _listener.Start(); DialPort = _listener.Port;
            var handler = new SocketsHttpHandler { ConnectCallback = async (context, cancellation) => {
                Dials.Enqueue(context.DnsEndPoint.Host); Interlocked.Increment(ref Connections);
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try { await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, Volatile.Read(ref DialPort)), cancellation); return new NetworkStream(socket, true); }
                catch { socket.Dispose(); throw; }
            } };
            Client = new(Store, A, null, handler); Service = new(Store, Client, Clock, () => jitter); Service.Diagnostic += Diagnostics.Enqueue;
        }
        internal string Add(string name, string ip, bool paired)
        {
            string Token() => RosterImportPreview.ComputeSnapshotToken(Store.GetRosterSnapshot());
            string row = Store.SaveManualRosterEntry(null, new(name, null, false, ip), Token()).EntryId!;
            if (paired && !Store.BindPairedRosterPeer(row, B.PeerId, Token()).Changed) throw new Exception("fixture roster bind"); return row;
        }
        public async ValueTask DisposeAsync() { await Service.DisposeAsync(); Client.Dispose(); await _listener.DisposeAsync(); Store.Dispose(); _remote.Dispose(); A.Dispose(); B.Dispose(); Directory.Delete(_root, true); }
    }
    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero).AddTicks(GetTimestamp());
        internal void Advance(double seconds) => Interlocked.Add(ref _ticks, TimeSpan.FromSeconds(seconds).Ticks);
    }
    private sealed class Faults : IStoreFaults
    {
        internal Action<string>? Before;
        public void AfterBegin(string op) { }
        public void BeforeCommit(string op) => Before?.Invoke(op);
        public void AfterCommit(string op) { }
    }
}
