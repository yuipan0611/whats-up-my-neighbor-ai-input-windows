using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using NekoChat.Core.Classroom.Transport;
using NekoChat.Core.Classroom.Wire;
using NekoChat.Core.Storage;
using ClassroomRoute = NekoChat.Core.Storage.ClassroomRoute;

namespace NekoChat.Tests;

/// <summary>
/// Independent checks for the classroom sender's route decision (NCM3 review findings F1/F2, contract NCMW-v2 CM-14 and CM-07).
/// Written from the contract, not from the sender's code: it only uses public API (ClassroomSender, IChatStore, ClassroomPacketWriter)
/// and talks to real loopback TCP "peers" that script the HTTP replies byte by byte.
///
/// What must hold:
///   R1  A hello that fails in any way other than the byte-exact legacy signal (HTTP 400 {"error":"empty_text"}) never sends the user's text anywhere.
///   R2  Only that exact reply selects the legacy route; near-misses and non-NekoChat services never receive the text.
///   R3  A legacy recipient gets at most 2,000 Unicode scalar values; longer text is not sent and the draft stays.
///   C   Controls: the harness itself works (classroom happy path, exact legacy happy path).
/// It collects every failure and throws once at the end, so one run shows the whole picture.
/// Not part of the default suite on purpose: it is a red baseline against 78a8829 until the sender is fixed.
/// </summary>
internal static class ClassroomSenderIndependentChecks
{
    // OwnIp is the sender's real local endpoint: every connection is redirected to a loopback peer, so the socket's local address is 127.0.0.1 and the receiver must echo that (F4).
    private const string PeerIp = "192.168.1.50", OwnIp = "127.0.0.1";
    private const int LegacyMaxScalars = 2000; // CM-07, restated here on purpose (not read from product code)
    private static readonly byte[] ExactLegacyBody = "{\"error\":\"empty_text\"}"u8.ToArray();
    private static readonly byte[] DeliveredBody = "{\"status\":\"delivered\"}"u8.ToArray();

    private sealed record Req(int Seq, string Path, byte[] Body)
    {
        public bool IsHello => Path.EndsWith("/nekochat/classroom/v1/hello", StringComparison.Ordinal);
        public bool IsClassroomMessage => Path.EndsWith("/nekochat/classroom/v1/message", StringComparison.Ordinal);
        public bool IsLegacyDirect => Path.EndsWith("/nekochat/v1/direct", StringComparison.Ordinal);
        /// <summary>Any request whose JSON body has a non-empty "text" string: the user's message reached the wire.</summary>
        public bool CarriesUserText
        {
            get
            {
                if (Body.Length == 0) return false;
                try
                {
                    using var doc = JsonDocument.Parse(Body);
                    return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("text", out var t) &&
                        t.ValueKind == JsonValueKind.String && t.GetString()!.Length > 0;
                }
                catch (JsonException) { return false; }
            }
        }
    }

    private abstract record Reply;
    private sealed record Raw(int Status, string ContentType, byte[] Body) : Reply;
    private sealed record Silence : Reply;

    private static Reply Json(int status, byte[] body) => new Raw(status, "application/json; charset=utf-8", body);
    private static Reply ClassroomError(string code) { var (status, body) = ClassroomPacketWriter.Error(code); return Json(status, body); }

    private sealed class FakePeer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Func<Req, Reply> _script;
        private readonly List<Req> _seen = new();
        private int _seq;
        internal int Port { get; }
        internal IReadOnlyList<Req> Seen { get { lock (_seen) return _seen.ToArray(); } }

        internal FakePeer(Func<Req, Reply> script)
        {
            _script = script; _listener.Start(); Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(AcceptLoop);
        }

        private async Task AcceptLoop()
        {
            while (!_stop.IsCancellationRequested)
            {
                try { var client = await _listener.AcceptTcpClientAsync(_stop.Token); _ = Task.Run(() => Serve(client)); }
                catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException) { return; }
            }
        }

        private async Task Serve(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var buffer = new List<byte>(); var chunk = new byte[8192]; int headEnd = -1;
                    while (headEnd < 0)
                    {
                        int n = await stream.ReadAsync(chunk, _stop.Token); if (n == 0) return;
                        buffer.AddRange(chunk.AsSpan(0, n).ToArray());
                        headEnd = IndexOf(buffer, "\r\n\r\n"u8);
                    }
                    string head = Encoding.ASCII.GetString(buffer.GetRange(0, headEnd).ToArray());
                    string[] lines = head.Split("\r\n");
                    string path = lines[0].Split(' ')[1];
                    int length = 0;
                    foreach (string line in lines.Skip(1))
                        if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line[15..].Trim());
                    while (buffer.Count < headEnd + 4 + length)
                    {
                        int n = await stream.ReadAsync(chunk, _stop.Token); if (n == 0) break;
                        buffer.AddRange(chunk.AsSpan(0, n).ToArray());
                    }
                    var body = buffer.Skip(headEnd + 4).Take(length).ToArray();
                    Req request; lock (_seen) { request = new Req(++_seq, path, body); _seen.Add(request); }
                    switch (_script(request))
                    {
                        case Raw raw:
                            string reason = raw.Status switch { 200 => "OK", 400 => "Bad Request", 404 => "Not Found", 429 => "Too Many Requests", 503 => "Service Unavailable", _ => "Status" };
                            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {raw.Status} {reason}\r\nContent-Type: {raw.ContentType}\r\nContent-Length: {raw.Body.Length}\r\nConnection: close\r\n\r\n"), _stop.Token);
                            await stream.WriteAsync(raw.Body, _stop.Token); await stream.FlushAsync(_stop.Token);
                            break;
                        case Silence:
                            await Task.Delay(Timeout.Infinite, _stop.Token);
                            break;
                    }
                }
                catch (Exception e) when (e is OperationCanceledException or IOException or SocketException or ObjectDisposedException) { }
            }
        }

        private static int IndexOf(List<byte> data, ReadOnlySpan<byte> pattern)
        {
            for (int i = 0; i + pattern.Length <= data.Count; i++)
            {
                bool hit = true; for (int j = 0; j < pattern.Length && hit; j++) hit = data[i + j] == pattern[j];
                if (hit) return i;
            }
            return -1;
        }

        public ValueTask DisposeAsync() { _stop.Cancel(); _listener.Stop(); _stop.Dispose(); return ValueTask.CompletedTask; }
    }

    /// <summary>Every connection goes to the fake peer on loopback (or is refused), whatever IP the sender targets.</summary>
    private static HttpClient Client(int? peerPort) => new(ClassroomSender.CreateDefaultHandler(async (_, cancellation) =>
    {
        // Only the way the connection is made changed: the product's own handler wraps this callback and records the real LocalEndPoint in the request Options.
        if (peerPort is not { } port) throw new SocketException((int)SocketError.ConnectionRefused);
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try { await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), cancellation); return new NetworkStream(socket, ownsSocket: true); }
        catch { socket.Dispose(); throw; }
    })) { Timeout = TimeSpan.FromMilliseconds(1500) };

    private sealed record Outcome(ClassroomSendResult? Result, string? Thrown, IReadOnlyList<Req> Seen, string? DraftText)
    {
        internal bool TextOnWire => Seen.Any(r => r.CarriesUserText);
        internal bool Delivered => Result is { Success: true, Completion: Confirmed };
    }

    private static async Task<Outcome> Exercise(string text, Func<Req, Reply>? script)
    {
        string dir = Path.Combine(Path.GetTempPath(), "classroom-sender-indep-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        try
        {
            // Same text limit the app configures (65,536 UTF-8 bytes, D-06); the store default would refuse >2,000 scalars before the sender is reached.
            using var store = ChatStore.Open(new(Path.Combine(dir, "data")) { TextValidator = text => NekoChat.Core.V2.V2Text.Check(text, NekoChat.Core.V2.V2Text.ProductMaxUtf8Bytes) == NekoChat.Core.V2.V2TextProblem.None });
            var principal = ClassroomPrincipalKey.ForSource(PeerIp)!.Value;
            var started = (AttemptStarted)store.BeginClassroomSend(principal, text, 0, 1);
            var prepared = store.GetClassroomPreparedSend(started.MessageId) ?? throw new InvalidOperationException("no prepared send");
            await using var peer = script is null ? null : new FakePeer(script);
            using var http = Client(peer?.Port);
            var sender = new ClassroomSender(store, http);
            ClassroomSendResult? result = null; string? thrown = null;
            try { result = await sender.SendPreparedAsync(prepared, 8768, OwnIp, () => true); }
            catch (Exception e) { thrown = e.GetType().Name + ": " + e.Message; }
            return new Outcome(result, thrown, peer?.Seen ?? Array.Empty<Req>(), store.GetClassroomDraftState(principal).RawText);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    /// <summary>A peer that answers hello with <paramref name="hello"/> and is otherwise maximally permissive, so any leak looks like a success.</summary>
    private static Func<Req, Reply> HelloThenPermissive(Reply hello) => r => r.IsHello ? hello : Json(200, DeliveredBody);

    private static string Repeat(string unit, int count) => string.Concat(Enumerable.Repeat(unit, count));

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    /// <summary>A peer that speaks the classroom protocol correctly (hello_ok, then a valid stored ACK).</summary>
    private static Func<Req, Reply> ClassroomPeer() => r =>
    {
        if (r.IsHello)
        {
            using var doc = JsonDocument.Parse(r.Body);
            string nonce = doc.RootElement.GetProperty("client_nonce").GetString()!;
            return Json(200, ClassroomPacketWriter.HelloOk(nonce, ClassroomPacketWriter.NewToken(), PeerIp, OwnIp, new[] { "receipts" }));
        }
        if (r.IsClassroomMessage)
        {
            using var doc = JsonDocument.Parse(r.Body);
            var root = doc.RootElement;
            string? reaction = root.TryGetProperty("reaction", out var re) && re.ValueKind == JsonValueKind.String ? re.GetString() : null;
            return Json(200, ClassroomPacketWriter.MessageAck("stored", root.GetProperty("message_id").GetString()!, root.GetProperty("session").GetString()!, OwnIp, PeerIp, reaction, root.GetProperty("text").GetString()!));
        }
        return Json(400, "{}"u8.ToArray());
    };

    internal static async Task<int> RunAsync()
    {
        int checks = 0; var failures = new List<string>();
        void Check(bool ok, string label, string detail = "") { checks++; if (!ok) failures.Add(label + (detail.Length > 0 ? " — " + detail : "")); }
        string Describe(Outcome o) => o.Thrown ?? $"success={o.Result?.Success} code={o.Result?.Code} route={o.Result?.Route} completion={o.Result?.Completion?.GetType().Name}; requests=[{string.Join(", ", o.Seen.Select(r => r.Path.Replace("/nekochat/", "") + (r.CarriesUserText ? "(TEXT)" : "")))}]";
        const string Text = "使用者的私密訊息 secret-marker-0042";

        // ---- C: controls. These must pass on any implementation; if they fail the harness, not the sender, is wrong. ----
        {
            var o = await Exercise(Text, ClassroomPeer());
            Check(o.Delivered && o.Result!.Route == ClassroomRoute.Classroom, "C1 classroom happy path delivers", Describe(o));
            Check(o.Seen.Count == 2 && o.Seen[0].IsHello && o.Seen[1].IsClassroomMessage && !o.Seen.Any(r => r.IsLegacyDirect), "C1 classroom path is hello then message and never legacy", Describe(o));
        }
        {
            var o = await Exercise(Text, r => r.IsHello ? Json(400, ExactLegacyBody) : r.IsLegacyDirect ? Json(200, DeliveredBody) : Json(404, Ascii("nope")));
            Check(o.Delivered && o.Result!.Route == ClassroomRoute.Legacy, "C2 exact legacy signal delivers over the legacy route", Describe(o));
            Check(o.Seen.Count(r => r.CarriesUserText) == 1 && o.Seen.First(r => r.CarriesUserText).IsLegacyDirect, "C2 the text is sent exactly once, on /nekochat/v1/direct", Describe(o));
            Check(o.Seen.Count > 0 && o.Seen[0].IsHello && !o.Seen.Any(r => r.IsClassroomMessage), "C2 hello comes first and no classroom message is attempted", Describe(o));
        }

        // ---- R1: a failed hello is not a license to use the legacy route. ----
        var r1 = new (string Name, Reply Hello)[]
        {
            ("429 rate_limited", ClassroomError(ClassroomCodes.RateLimited)),
            ("503 receiver_not_ready", ClassroomError(ClassroomCodes.ReceiverNotReady)),
            ("503 transient", ClassroomError(ClassroomCodes.Transient)),
            ("503 storage_unavailable", ClassroomError(ClassroomCodes.StorageUnavailable)),
            ("capacity_exceeded", ClassroomError(ClassroomCodes.CapacityExceeded)),
            ("403 source_not_allowed", ClassroomError(ClassroomCodes.SourceNotAllowed)),
            ("200 with a malformed hello_ok", Json(200, Ascii("{\"protocol\":\"nekochat-classroom\",\"version\":1,\"kind\":\"hello.ok\"}"))),
            ("never answers (timeout)", new Silence()),
        };
        foreach (var (name, hello) in r1)
        {
            var o = await Exercise(Text, HelloThenPermissive(hello));
            Check(!o.TextOnWire, "R1 hello " + name + ": the user's text never reaches the wire", Describe(o));
            Check(o.Thrown is null && o.Result is { Success: false } && o.Result.Completion is not Confirmed, "R1 hello " + name + ": reported as not delivered", Describe(o));
            Check(o.DraftText == Text, "R1 hello " + name + ": the draft is kept", "draft=" + o.DraftText);
        }
        {
            var o = await Exercise(Text, null);
            Check(!o.TextOnWire && o.Thrown is null && o.Result is { Success: false } && o.Result.Completion is not Confirmed, "R1 connection refused: not delivered", Describe(o));
            Check(o.DraftText == Text, "R1 connection refused: the draft is kept", "draft=" + o.DraftText);
        }

        // ---- R2: only the byte-exact reply selects legacy; anything else is not a NekoChat v1 peer. ----
        var r2 = new (string Name, Reply Hello)[]
        {
            ("200 HTML page", new Raw(200, "text/html", Ascii("<html><body>Hello</body></html>"))),
            ("200 JSON accept-everything service", Json(200, DeliveredBody)),
            ("404 Not Found", new Raw(404, "text/plain", Ascii("Not Found"))),
            ("404 JSON error", Json(404, Ascii("{\"error\":\"not_found\"}"))),
            ("400 empty_text plus trailing newline", Json(400, Ascii("{\"error\":\"empty_text\"}\n"))),
            ("400 empty_text plus trailing space", Json(400, Ascii("{\"error\":\"empty_text\"} "))),
            ("400 empty_text with spaces inside", Json(400, Ascii("{ \"error\": \"empty_text\" }"))),
            ("400 different error", Json(400, Ascii("{\"error\":\"invalid_json\"}"))),
            ("400 empty body", Json(400, Array.Empty<byte>())),
            ("200 carrying the exact legacy body", Json(200, ExactLegacyBody)),
            ("500 carrying the exact legacy body", Json(500, ExactLegacyBody)),
        };
        foreach (var (name, hello) in r2)
        {
            var o = await Exercise(Text, HelloThenPermissive(hello));
            Check(!o.TextOnWire, "R2 " + name + ": the user's text never reaches the wire", Describe(o));
            Check(o.Thrown is null && o.Result is { Success: false } && o.Result.Completion is not Confirmed, "R2 " + name + ": reported as not delivered", Describe(o));
            Check(o.DraftText == Text, "R2 " + name + ": the draft is kept", "draft=" + o.DraftText);
        }

        // ---- R3: legacy recipients take at most 2,000 Unicode scalar values (CM-07), counted in scalars, not UTF-16 units or bytes. ----
        Func<Req, Reply> exactLegacyPeer = r => r.IsHello ? Json(400, ExactLegacyBody) : r.IsLegacyDirect ? Json(200, DeliveredBody) : Json(404, Ascii("nope"));
        foreach (var (unit, label) in new[] { ("a", "ASCII"), ("貓", "CJK (3 bytes)"), ("🐈", "emoji (surrogate pair)") })
        {
            string over = Repeat(unit, LegacyMaxScalars + 1), exact = Repeat(unit, LegacyMaxScalars);
            var refused = await Exercise(over, exactLegacyPeer);
            Check(!refused.TextOnWire, $"R3 {label} ×{LegacyMaxScalars + 1} to a legacy peer: nothing carrying the text is sent", Describe(refused));
            Check(refused.Thrown is null && refused.Result is { Success: false } && refused.Result.Completion is not Confirmed, $"R3 {label} ×{LegacyMaxScalars + 1}: reported as not delivered", Describe(refused));
            Check(refused.DraftText == over, $"R3 {label} ×{LegacyMaxScalars + 1}: the draft is kept untruncated", "draft length=" + refused.DraftText?.Length);
            var accepted = await Exercise(exact, exactLegacyPeer);
            Check(accepted.Delivered && accepted.Result!.Route == ClassroomRoute.Legacy, $"R3 {label} ×{LegacyMaxScalars} to a legacy peer is delivered", Describe(accepted));
        }
        {
            // The same long text over the classroom route is fine (limit is 65,536 UTF-8 bytes): the 2,000 limit belongs to the legacy recipient only.
            string longText = Repeat("a", LegacyMaxScalars + 1);
            var o = await Exercise(longText, ClassroomPeer());
            Check(o.Delivered && o.Result!.Route == ClassroomRoute.Classroom, "R3 control: 2,001 scalars to a classroom peer is delivered", Describe(o));
        }

        foreach (string failure in failures) Console.WriteLine("FAIL: " + failure);
        Console.WriteLine($"Classroom sender independent checks: {checks - failures.Count}/{checks} passed, {failures.Count} failed.");
        if (failures.Count > 0) throw new InvalidOperationException($"{failures.Count} of {checks} independent sender checks failed.");
        return checks;
    }
}
