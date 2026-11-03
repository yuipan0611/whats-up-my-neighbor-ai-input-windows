using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using NekoChat.Core;
using NekoChat.Core.Classroom.Transport;
using NekoChat.Core.Storage;

namespace NekoChat.Tests;

/// <summary>Real TCP admission and real shared SQLite lifecycle tests. Codec fixture is deliberately not NCMWC acceptance.</summary>
internal static class ClassroomListenerChecks
{
    internal static async Task<int> Run()
    {
        int checks = 0;
        void Check(bool value, string label) { checks++; if (!value) throw new Exception("Classroom listener: " + label); }
        // Separate classroom120 / legacy20 quotas, including an exact monotonic 60s boundary.
        var clock = new Clock(); var endpoint = new Endpoint();
        await using (var server = new ClassroomTcpListener(endpoint, new() { BindAddress = IPAddress.Loopback, Port = 0, Clock = clock }))
        {
            server.Start(); bool legacy = true, classroom = true;
            for (int i = 0; i < 20; i++) legacy &= await Exchange(server, "/nekochat/v1/direct") == 200;
            Check(legacy && await Exchange(server, "/nekochat/v1/direct") == 429, "legacy still has exactly20 requests/source/minute");
            for (int i = 0; i < 120; i++) classroom &= await Exchange(server, "/nekochat/classroom/v1/hello") == 200;
            Check(classroom && await Exchange(server, "/nekochat/classroom/v1/receipt") == 429, "three classroom routes share120, independently of exhausted legacy quota");
            clock.Advance(59999); Check(await Exchange(server, "/nekochat/classroom/v1/message") == 429, "quota does not expire early at59999ms");
            clock.Advance(1); Check(await Exchange(server, "/nekochat/classroom/v1/message") == 200 && await Exchange(server, "/nekochat/v1/direct") == 200, "both quota windows expire at60000ms");
            Check(endpoint.Connection is { SourceIp: "127.0.0.1", LocalIp: "127.0.0.1" }, "endpoint receives actual accepted source/local socket addresses");
        }
        // A real SQLite commit gate remains physically occupied after its socket deadline and through disposal.
        using (var fixture = new SqlFixture())
        {
            var sql = new Endpoint(fixture.Store);
            var server = new ClassroomTcpListener(sql, new() { BindAddress = IPAddress.Loopback, Port = 0, MaximumConnections = 1, ConnectionTimeout = TimeSpan.FromSeconds(2) });
            Task? stopping = null;
            try
            {
                server.Start(); using var sender = await SendWithoutReading(server.Port, "h");
                await Until(() => fixture.Entered.IsSet);
                using (var closeDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                {
                    try { int n = await sender.GetStream().ReadAsync(new byte[1], closeDeadline.Token); Check(n == 0, "deadline physically closes the held request socket"); }
                    catch (IOException) { Check(true, "deadline physically closes the held request socket with reset"); }
                }
                await Task.Delay(50);
                Check(server.ActiveConnections == 1, "timed-out SQL work retains its physical connection slot");
                Check(await Exchange(server, "/nekochat/classroom/v1/hello", waitForIdle: false) == 429 && sql.Calls == 1,
                    "new connection cannot pass the physical cap while canceled SQL still drains");
                stopping = server.DisposeAsync().AsTask();
                Check(!stopping.IsCompleted && !sql.Current!(), "stop invalidates the transaction guard but awaits physical SQL work");
                fixture.Release.Set(); await stopping.WaitAsync(TimeSpan.FromSeconds(10));
                Check(server.ActiveConnections == 0 && fixture.Store.ListLegacyHistories().Count == 0, "actual canceled SQLite transaction rolls back before shutdown returns");
            }
            finally { fixture.Release.Set(); await server.DisposeAsync(); if (sql.Work is not null) await sql.Work; }
        }
        // Resume invalidates old jobs/sessions without dropping their slots; only fresh work can commit.
        using (var fixture = new SqlFixture())
        {
            var sql = new Endpoint(fixture.Store);
            await using var server = new ClassroomTcpListener(sql, new() { BindAddress = IPAddress.Loopback, Port = 0, MaximumConnections = 1 });
            try
            {
                server.Start(); using var sender = await SendWithoutReading(server.Port, "o");
                await Until(() => fixture.Entered.IsSet); server.ResetAfterResume();
                Check(server.ActiveConnections == 1 && !sql.Current!(), "resume immediately invalidates old guard without releasing SQL slot");
                fixture.Release.Set(); await Until(() => server.ActiveConnections == 0);
                Check(fixture.Store.ListLegacyHistories().Count == 0, "pre-resume SQLite work cannot commit under the new listener generation");
                Check(await Exchange(server, "/nekochat/classroom/v1/message", text: "n") == 200 && fixture.Store.ListLegacyHistories().Sum(x => x.Messages) == 1,
                    "fresh post-resume request alone commits after old work drains");
                Check(sql.ReadyChanges.Count(x => !x) >= 1 && sql.ReadyChanges.Last(), "resume closes and reopens endpoint readiness for RAM session invalidation");
            }
            finally { fixture.Release.Set(); if (sql.Work is not null) await sql.Work; }
        }
        Console.WriteLine($"Classroom listener: {checks} actual TCP quota/source/physical-slot and shared SQLite timeout/resume/shutdown checks; classroom codec/storage mapping remains separate.");
        return checks;
    }
    private static async Task<TcpClient> SendWithoutReading(int port, string text)
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port);
            byte[] body = Encoding.UTF8.GetBytes(text), header = Encoding.ASCII.GetBytes($"POST /nekochat/classroom/v1/message HTTP/1.1\r\nHost: localhost\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\n\r\n");
            await client.GetStream().WriteAsync(header); await client.GetStream().WriteAsync(body); return client;
        }
        catch { client.Dispose(); throw; }
    }
    private static async Task<int> Exchange(ClassroomTcpListener server, string path, bool waitForIdle = true, string text = "x")
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port, timeout.Token);
        byte[] request = Encoding.ASCII.GetBytes($"POST {path} HTTP/1.1\r\nHost: localhost\r\nContent-Type: application/json\r\nContent-Length: {text.Length}\r\n\r\n{text}");
        await client.GetStream().WriteAsync(request, timeout.Token);
        using var reader = new StreamReader(client.GetStream(), Encoding.ASCII, leaveOpen: true);
        string line = await reader.ReadLineAsync(timeout.Token) ?? "";
        int status = int.Parse(line.Split(' ')[1]); client.Dispose();
        if (waitForIdle) await Until(() => server.ActiveConnections == 0);
        return status;
    }
    private static async Task Until(Func<bool> condition)
    { for (int i = 0; i < 1000; i++) { if (condition()) return; await Task.Delay(5); } throw new TimeoutException("classroom listener fixture condition"); }
    private sealed class Endpoint(ChatStore? store = null) : IClassroomRequestEndpoint
    {
        internal int Calls;
        internal ClassroomConnectionContext? Connection;
        internal Func<bool>? Current;
        internal Task<ClassroomTransportResponse>? Work;
        internal List<bool> ReadyChanges { get; } = [];
        public void SetReady(bool ready) { lock (ReadyChanges) ReadyChanges.Add(ready); }
        public ClassroomHeadDecision ValidateHead(ReadOnlyMemory<byte> header)
        {
            string[] lines = Encoding.ASCII.GetString(header.Span).Split("\r\n");
            int length = int.Parse(lines.Single(x => x.StartsWith("Content-Length:", StringComparison.Ordinal)).Split(':')[1]);
            return new("ok", new(lines[0].Split(' ')[1], length, 8192));
        }
        public ClassroomTransportResponse Error(string code) => new(code == "rate_limited" ? 429 : 503,
            JsonSerializer.SerializeToUtf8Bytes(new { protocol = "nekochat-classroom", version = 1, kind = "error", code }));
        public Task<ClassroomTransportResponse> HandleAsync(ClassroomRequestFrame frame, ClassroomConnectionContext connection, Func<bool> stillCurrent, CancellationToken cancellation)
        {
            Interlocked.Increment(ref Calls); Connection = connection; Current = stillCurrent;
            return Work = Task.Run(() => {
                if (store is not null)
                {
                    var expected = store.GetLegacyReceiveContext(connection.SourceIp);
                    var result = store.ReceiveLegacyMessage(new(Encoding.UTF8.GetString(frame.Body.Span), null), expected, stillCurrent);
                    if (!result.Stored) return Error("receiver_not_ready");
                }
                return new ClassroomTransportResponse(200, "{\"ok\":true}"u8.ToArray());
            });
        }
    }
    private sealed class SqlFixture : IDisposable, IStoreFaults
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "classroom-listener-" + Guid.NewGuid().ToString("N"));
        internal ManualResetEventSlim Entered { get; } = new();
        internal ManualResetEventSlim Release { get; } = new();
        internal ChatStore Store { get; }
        internal SqlFixture() { Directory.CreateDirectory(_root); Store = ChatStore.Open(new(_root) { Faults = this }); }
        public void AfterBegin(string operation) { }
        public void AfterCommit(string operation) { }
        public void BeforeCommit(string operation)
        { if (operation == "legacy-receive") { Entered.Set(); if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("SQL fixture gate"); } }
        public void Dispose() { Release.Set(); Store.Dispose(); Entered.Dispose(); Release.Dispose(); Directory.Delete(_root, true); }
    }
    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        internal void Advance(int milliseconds) => Interlocked.Add(ref _ticks, TimeSpan.FromMilliseconds(milliseconds).Ticks);
    }
}
