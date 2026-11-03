using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using NekoChat.Core;
using NekoChat.Core.Classroom.Transport;
using NekoChat.Core.V2;

namespace NekoChat.Tests;

/// <summary>Real accepted sockets with controlled accept/send faults; no classroom codec or store acceptance claim.</summary>
internal static class ListenerFailurePolicyChecks
{
    internal static async Task<int> Run()
    {
        int checks = 0;
        void Check(bool ok, string label) { checks++; if (!ok) throw new Exception("Listener policy: " + label); }
        foreach (bool classroom in new[] { true, false })
        {
            string kind = classroom ? "classroom" : "shared";
            int accepted = 0;
            void Fault(TcpClient _) { if (Interlocked.Increment(ref accepted) != 50) throw new SocketException((int)SocketError.ConnectionAborted); }
            await using (var server = new Server(classroom, Fault))
            {
                bool visibleFailure = false;
                server.Diagnostic += code => { if (code == "listener_failed") visibleFailure = !server.Running && server.Failed; };
                server.Start();
                bool first = true;
                for (int i = 0; i < 49 && server.Running; i++) first &= await Closed(server.Port);
                Check(first && server.Running && accepted == 49, kind + " survives the first49 consecutive faults");
                Check(await server.Healthy() && server.Running && server.Failures == 49, kind + " successful endpoint capture resets the streak");
                bool next = true;
                for (int i = 0; i < 49 && server.Running; i++) next &= await Closed(server.Port);
                Check(next && server.Running && !server.Failed && accepted == 99, kind + " reset prevents premature failure on the next49 faults");
                Check(await Closed(server.Port), kind + " closes the fiftieth consecutive faulted socket");
                await Until(() => !server.Running, kind + " permanent fault becomes externally visible at50");
                Check(server.Failed && visibleFailure && server.Diagnostics.Count(x => x == "listener_failed") == 1 && (!classroom || server.NotReadyCalls == 1),
                    kind + " terminal diagnostic observes failed and not-running state exactly once");
                Check(server.Failures == 99 && server.Diagnostics.Count(x => x == "accept_retry") == 10,
                    kind + " lifetime failures remain counted while retry diagnostics are throttled");
                bool refused = false; try { server.Start(); } catch (InvalidOperationException) { refused = true; }
                Check(refused, kind + " failed listener requires replacement instead of reviving old workers");
                await server.DisposeAsync();
                Check(server.Active == 0, kind + " permanent failure drains on disposal");
            }

            accepted = 0;
            void RejectFault(TcpClient socket)
            {
                int n = Interlocked.Increment(ref accepted);
                if (n is 2 or 3) socket.Client.Shutdown(SocketShutdown.Send); // endpoint capture still works, rejection write fails on the real socket
            }
            await using (var server = new Server(classroom, RejectFault, 1))
            using (var held = new TcpClient())
            {
                server.Start(); await held.ConnectAsync(IPAddress.Loopback, server.Port);
                await Until(() => server.Active == 1); // first real connection occupies the only physical slot, waiting for its head
                Check(await Closed(server.Port) && await Closed(server.Port), kind + " closes actual failed rejection writes");
                Check(server.Running && !server.Failed && server.Failures == 0 && !server.Diagnostics.Contains("accept_retry"),
                    kind + " rejection writes never enter accept failure accounting or backoff");
                held.Dispose(); await Until(() => server.Active == 0);
                Check(await server.Healthy(), kind + " healthy request follows failed rejections");
                await server.DisposeAsync();
                Check(server.Active == 0, kind + " rejection faults preserve physical-slot drain");
            }
        }
        Console.WriteLine($"Listener failure policy: {checks} actual TCP streak reset,50-fault shutdown,throttling,rejection-write isolation and drain checks; controlled internal hook only.");
        return checks;
    }
    private static async Task Until(Func<bool> ready, string label = "physical-slot transition completes")
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!ready())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(6)) throw new Exception("Listener policy: " + label);
            await Task.Delay(10);
        }
    }
    private static async Task<bool> Closed(int port)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6)); using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            return await client.GetStream().ReadAsync(new byte[1], timeout.Token) == 0;
        }
        catch (Exception error) when (error is IOException or SocketException) { return true; }
        catch (OperationCanceledException) { return false; }
    }
    private sealed class Server : IAsyncDisposable
    {
        private readonly ClassroomTcpListener? _classroom;
        private readonly Endpoint? _endpoint;
        private readonly V2TcpListener? _shared;
        internal readonly ConcurrentQueue<string> Diagnostics = new();
        internal event Action<string>? Diagnostic;
        internal Server(bool classroom, Action<TcpClient> fault, int slots = 8)
        {
            if (classroom)
            {
                _endpoint = new();
                _classroom = new(_endpoint, new() { BindAddress = IPAddress.Loopback, Port = 0, MaximumConnections = slots, AcceptedForTest = fault, ConnectionTimeout = TimeSpan.FromSeconds(20) });
                _classroom.Diagnostic += Report;
            }
            else
            {
                _shared = new(null, new() { BindAddress = IPAddress.Loopback, Port = 0, MaxConnections = slots, RequestsPerSourcePerMinute = 1000, AcceptedForTest = fault, ConnectionTimeout = TimeSpan.FromSeconds(20) });
                _shared.Diagnostic += Report;
            }
        }
        private void Report(string code) { Diagnostics.Enqueue(code); Diagnostic?.Invoke(code); }
        internal bool Running => _classroom?.Running ?? _shared!.Running;
        internal bool Failed => _classroom?.Failed ?? _shared!.Failed;
        internal int Port => _classroom?.Port ?? _shared!.Port;
        internal long Failures => _classroom?.AcceptFailures ?? _shared!.AcceptFailures;
        internal int NotReadyCalls => _endpoint?.NotReadyCalls ?? 0;
        internal int Active => _classroom?.ActiveConnections ?? _shared!.ActiveConnections;
        internal void Start() { if (_classroom is not null) _classroom.Start(); else _shared!.Start(); }
        internal async Task<bool> Healthy()
        {
            if (_shared is not null) return await DirectLanMessaging.SendDirectAsync("127.0.0.1", "listener policy", Port);
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(6)); using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, Port, limit.Token);
            await client.GetStream().WriteAsync("POST /nekochat/classroom/v1/hello HTTP/1.1\r\nHost: localhost\r\nContent-Length: 1\r\nContent-Type: application/json\r\n\r\nx"u8.ToArray(), limit.Token);
            using var reader = new StreamReader(client.GetStream(), Encoding.ASCII, leaveOpen: true);
            return (await reader.ReadLineAsync(limit.Token))?.StartsWith("HTTP/1.1 200 ", StringComparison.Ordinal) == true;
        }
        public ValueTask DisposeAsync() => _classroom?.DisposeAsync() ?? _shared!.DisposeAsync();
    }
    private sealed class Endpoint : IClassroomRequestEndpoint
    {
        internal int NotReadyCalls;
        public void SetReady(bool ready) { if (!ready) Interlocked.Increment(ref NotReadyCalls); }
        public ClassroomHeadDecision ValidateHead(ReadOnlyMemory<byte> header) => new("ok", new("hello", 1, 8192));
        public ClassroomTransportResponse Error(string code) => new(429, "{}"u8.ToArray());
        public Task<ClassroomTransportResponse> HandleAsync(ClassroomRequestFrame frame, ClassroomConnectionContext connection, Func<bool> current, CancellationToken cancellation)
            => Task.FromResult(new ClassroomTransportResponse(200, "{}"u8.ToArray()));
    }
}
