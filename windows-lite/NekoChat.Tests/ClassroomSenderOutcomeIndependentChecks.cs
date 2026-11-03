using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using NekoChat.Core.Classroom.Transport;
using NekoChat.Core.Classroom.Wire;
using NekoChat.Core.Storage;
using ClassroomRoute = NekoChat.Core.Storage.ClassroomRoute;

namespace NekoChat.Tests;

/// <summary>
/// Independent checks for the classroom sender's outcome handling (NCM3 review F3, F4, F5), written from NCMW-v2 §10/§11 and the PM ruling.
///   F3  A reply the receiver defined as "nothing was stored" (contract §10, first nine rows) makes the attempt `failed`, not `unconfirmed`.
///       Everything else that is ambiguous (receiver_not_ready, storage_unavailable, transient on message, invalid/mismatching ACK, timeout, hang-up) stays `unconfirmed`.
///   F4  The sender validates the receiver's observed_sender_ip against the ACTUAL local address of the connection, not against a caller-supplied guess.
///   F5  A cancelled attempt records no route and sends nothing.
/// Public API only (ClassroomSender, IChatStore through a recording proxy, ClassroomPacketWriter) and real loopback TCP peers. Collects all failures, throws once.
/// </summary>
internal static class ClassroomSenderOutcomeIndependentChecks
{
    private const string PeerIp = "192.168.1.50", Loopback = "127.0.0.1", WrongGuess = "192.168.1.10";
    private const string Text = "使用者的私密訊息 outcome-marker-0042";

    private sealed record Run(ClassroomSendResult? Result, string? Thrown, IReadOnlyList<WireReq> Seen, string? DraftText, IReadOnlyList<(string Method, object?[] Args)> Calls)
    {
        internal bool TextOnWire => Seen.Any(r => r.CarriesUserText);
        internal bool Confirmed => Result?.Completion is Confirmed;
        internal string? CompletionState => (Result?.Completion as StateUpdated)?.NewState;
    }

    /// <param name="observedOverride">What the peer claims as observed_sender_ip. null = truthful (the real remote address of the connection).</param>
    private static Func<WireReq, WireReply> Peer(Func<WireReq, WireReply>? onMessage = null, string? observedOverride = null) => r =>
    {
        string observed = observedOverride ?? r.Remote?.ToString() ?? Loopback;
        using var doc = JsonDocument.Parse(r.Body);
        if (r.IsHello)
            return Loopback_Json(ClassroomPacketWriter.HelloOk(doc.RootElement.GetProperty("client_nonce").GetString()!, ClassroomPacketWriter.NewToken(), PeerIp, observed, new[] { "receipts" }));
        if (r.IsClassroomMessage)
        {
            if (onMessage is not null) return onMessage(r);
            var root = doc.RootElement;
            string? reaction = root.TryGetProperty("reaction", out var re) && re.ValueKind == JsonValueKind.String ? re.GetString() : null;
            return Loopback_Json(ClassroomPacketWriter.MessageAck("stored", root.GetProperty("message_id").GetString()!, root.GetProperty("session").GetString()!, observed, PeerIp, reaction, root.GetProperty("text").GetString()!));
        }
        return Loopback_Json("{}"u8.ToArray(), 400);
    };
    private static WireReply Loopback_Json(byte[] body, int status = 200) => NekoChat.Tests.Loopback.Json(status, body);

    private static async Task<Run> Exercise(Func<WireReq, WireReply>? script, string ownIpParameter, int? cancelAfterMs = null, bool cancelBefore = false, Func<int?, HttpClient>? clientFactory = null)
    {
        string dir = NekoChat.Tests.Loopback.TempDir("classroom-outcome-indep-");
        try
        {
            using var store = NekoChat.Tests.Loopback.OpenStore(dir);
            var principal = ClassroomPrincipalKey.ForSource(PeerIp)!.Value;
            var started = (AttemptStarted)store.BeginClassroomSend(principal, Text, 0, 1);
            var prepared = store.GetClassroomPreparedSend(started.MessageId) ?? throw new InvalidOperationException("no prepared send");
            var (proxy, recorder) = RecordingStore.Wrap(store);
            await using var peer = script is null ? null : new LoopbackPeer(script);
            using var http = clientFactory is not null ? clientFactory(peer?.Port) : NekoChat.Tests.Loopback.Client(peer?.Port);
            var sender = new ClassroomSender(proxy, http);
            using var cancel = new CancellationTokenSource();
            if (cancelBefore) cancel.Cancel(); else if (cancelAfterMs is { } ms) cancel.CancelAfter(ms);
            ClassroomSendResult? result = null; string? thrown = null;
            try { result = await sender.SendPreparedAsync(prepared, 8768, ownIpParameter, () => true, cancel.Token); }
            catch (OperationCanceledException) { thrown = "OperationCanceledException"; }
            catch (Exception e) { thrown = e.GetType().Name + ": " + e.Message; }
            return new Run(result, thrown, peer?.Seen ?? Array.Empty<WireReq>(), store.GetClassroomDraftState(principal).RawText, recorder.Calls);
        }
        finally { NekoChat.Tests.Loopback.Cleanup(dir); }
    }

    private static string Describe(Run o) =>
        (o.Thrown ?? $"success={o.Result?.Success} code={o.Result?.Code} route={o.Result?.Route} completion={o.Result?.Completion?.GetType().Name}/{o.CompletionState}") +
        $"; requests=[{string.Join(", ", o.Seen.Select(r => r.Path.Replace("/nekochat/", "") + (r.CarriesUserText ? "(TEXT)" : "")))}]; storeCalls=[{string.Join(",", o.Calls.Select(c => c.Method))}]";

    internal static async Task<int> RunAsync()
    {
        int checks = 0; var failures = new List<string>();
        void Check(bool ok, string label, string detail = "") { checks++; if (!ok) failures.Add(label + (detail.Length > 0 ? " — " + detail : "")); }

        // ---- F3: definitive "nothing stored" replies make the attempt failed (contract §10). ----
        // stale_session is excluded on purpose: §6.9 says to retry it once before deciding.
        string[] definitive = { ClassroomCodes.InvalidPayload, ClassroomCodes.UnsupportedVersion, ClassroomCodes.PayloadTooLarge, ClassroomCodes.RateLimited,
            ClassroomCodes.WrongRecipient, ClassroomCodes.Conflict, ClassroomCodes.SourceNotAllowed, ClassroomCodes.CapacityExceeded };
        foreach (string code in definitive)
        {
            var o = await Exercise(Peer(_ => NekoChat.Tests.Loopback.ClassroomError(code)), Loopback);
            Check(o.Thrown is null && o.Result is { Success: false } && o.CompletionState == "failed", $"F3 message reply {code}: the attempt is failed", Describe(o));
            Check(o.DraftText == Text, $"F3 message reply {code}: the draft is kept", "draft=" + o.DraftText);
        }
        // Ambiguous results stay unconfirmed ("the other side may have it"): guards against over-correcting F3.
        var ambiguous = new (string Name, Func<WireReq, WireReply> OnMessage)[]
        {
            ("receiver_not_ready", _ => NekoChat.Tests.Loopback.ClassroomError(ClassroomCodes.ReceiverNotReady)),
            ("storage_unavailable", _ => NekoChat.Tests.Loopback.ClassroomError(ClassroomCodes.StorageUnavailable)),
            ("transient on the message route", _ => NekoChat.Tests.Loopback.ClassroomError(ClassroomCodes.Transient)),
            ("never answers (timeout)", _ => new WireSilence()),
            ("connection closed without a response", _ => new WireHangup()),
            ("200 with a malformed ACK", _ => Loopback_Json("{\"protocol\":\"nekochat-classroom\",\"version\":1,\"kind\":\"message.ack\",\"status\":\"stored\"}"u8.ToArray())),
            ("a well-formed ACK for another message_id", r =>
            {
                using var doc = JsonDocument.Parse(r.Body); var root = doc.RootElement;
                return Loopback_Json(ClassroomPacketWriter.MessageAck("stored", ClassroomPacketWriter.NewMessageId(), root.GetProperty("session").GetString()!, Loopback, PeerIp, null, root.GetProperty("text").GetString()!));
            }),
        };
        foreach (var (name, onMessage) in ambiguous)
        {
            var o = await Exercise(Peer(onMessage), Loopback);
            Check(o.Thrown is null && o.Result is { Success: false } && o.CompletionState == "unconfirmed", $"F3 control {name}: stays unconfirmed", Describe(o));
            Check(o.DraftText == Text, $"F3 control {name}: the draft is kept", "draft=" + o.DraftText);
        }

        // ---- F4: observed_sender_ip is checked against the actual connection, not against the caller's guess. ----
        {
            var o = await Exercise(Peer(), WrongGuess); // truthful peer, wrong caller guess (multi-homed host: first adapter != the route used)
            Check(o.Result is { Success: true } && o.Confirmed && o.Result.Route == ClassroomRoute.Classroom, "F4 a truthful ACK is accepted even when the caller passed a different local address", Describe(o));
        }
        {
            var o = await Exercise(Peer(observedOverride: WrongGuess), WrongGuess); // peer repeats the caller's guess, which is not what it really saw
            Check(o.Thrown is null && !o.Confirmed, "F4 an ACK whose observed_sender_ip is only the caller's guess is not accepted", Describe(o));
        }
        {
            var o = await Exercise(Peer(observedOverride: "192.168.1.99"), Loopback); // control: an address nobody can claim
            Check(o.Thrown is null && !o.Confirmed, "F4 control: an ACK naming an unrelated address is not accepted", Describe(o));
        }
        {
            var o = await Exercise(Peer(), Loopback); // control: guess happens to be right
            Check(o.Result is { Success: true } && o.Confirmed && o.Result.Route == ClassroomRoute.Classroom, "F4 control: caller's guess equal to the real address is accepted", Describe(o));
        }

        // ---- F4b: if the real local endpoint is NOT known for a request, the sender must not trust the reply (fail closed), never fall back to a guess. ----
        // A handler that does not record the endpoint (a plain SocketsHttpHandler instead of the product's) simulates "endpoint unknown".
        static HttpClient PlainClient(int? port, bool recordForHello)
        {
            var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, ConnectCallback = async (ctx, cancellation) =>
            {
                if (recordForHello && ctx.InitialRequestMessage?.RequestUri?.AbsolutePath.EndsWith("/hello", StringComparison.Ordinal) == true)
                    ctx.InitialRequestMessage.Options.Set(ClassroomSender.LocalEndpointOptionKey, Loopback);
                if (port is not { } p) throw new SocketException((int)SocketError.ConnectionRefused);
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try { await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, p), cancellation); return new NetworkStream(socket, ownsSocket: true); }
                catch { socket.Dispose(); throw; }
            } };
            return new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(1500) };
        }
        {
            var o = await Exercise(Peer(), Loopback, clientFactory: p => PlainClient(p, recordForHello: false));
            Check(o.Thrown is null && !o.Confirmed && o.Result is { Success: false }, "F4b hello with no recorded local endpoint: nothing is confirmed", Describe(o));
            Check(!o.TextOnWire && !o.Calls.Any(c => c.Method == "RecordClassroomRoute"), "F4b hello with no recorded local endpoint: the text is never sent and no route is recorded", Describe(o));
            Check(o.CompletionState == "unconfirmed" && o.DraftText == Text, "F4b hello with no recorded local endpoint: the attempt is unconfirmed and the draft is kept", Describe(o));
        }
        {
            var o = await Exercise(Peer(), Loopback, clientFactory: p => PlainClient(p, recordForHello: true));
            Check(o.Thrown is null && !o.Confirmed && o.Result is { Success: false }, "F4b message with no recorded local endpoint (hello had one): a well-formed stored ACK is still not trusted", Describe(o));
            Check(o.CompletionState == "unconfirmed" && o.DraftText == Text, "F4b message with no recorded local endpoint: the attempt is unconfirmed and the draft is kept", Describe(o));
        }

        // ---- F5: cancellation never records a route and never sends the text (contract §11: cancelled is unconfirmed, nothing is retried). ----
        bool RouteRecorded(Run o) => o.Calls.Any(c => c.Method == "RecordClassroomRoute");
        {
            var o = await Exercise(Peer(), Loopback, cancelBefore: true);
            Check(!RouteRecorded(o), "F5 cancelled before the first byte: no route is recorded", Describe(o));
            Check(!o.TextOnWire && !o.Confirmed, "F5 cancelled before the first byte: nothing sent, not confirmed", Describe(o));
            Check(o.DraftText == Text, "F5 cancelled before the first byte: the draft is kept", "draft=" + o.DraftText);
        }
        {
            var o = await Exercise(r => r.IsHello ? new WireSilence() : Loopback_Json("{}"u8.ToArray()), Loopback, cancelAfterMs: 250);
            Check(!RouteRecorded(o), "F5 cancelled while hello is pending: no route is recorded", Describe(o));
            Check(!o.TextOnWire && !o.Confirmed, "F5 cancelled while hello is pending: nothing sent, not confirmed", Describe(o));
            Check(o.DraftText == Text, "F5 cancelled while hello is pending: the draft is kept", "draft=" + o.DraftText);
        }
        {
            var o = await Exercise(Peer(_ => new WireSilence()), Loopback, cancelAfterMs: 250); // control: cancel after the route was legitimately decided
            var routes = o.Calls.Where(c => c.Method == "RecordClassroomRoute").ToList();
            Check(routes.Count == 1 && routes[0].Args.OfType<ClassroomRoute>().SingleOrDefault() == ClassroomRoute.Classroom, "F5 control: cancelled while the message is pending keeps the single classroom route", Describe(o));
            Check(!o.Confirmed && o.DraftText == Text, "F5 control: cancelled message is not confirmed and the draft is kept", Describe(o));
        }
        {
            var o = await Exercise(Peer(), Loopback); // control: an undisturbed send records exactly one classroom route
            var routes = o.Calls.Where(c => c.Method == "RecordClassroomRoute").ToList();
            Check(o.Confirmed && routes.Count == 1 && routes[0].Args.OfType<ClassroomRoute>().SingleOrDefault() == ClassroomRoute.Classroom, "F5 control: an undisturbed send records one classroom route and confirms", Describe(o));
        }

        foreach (string failure in failures) Console.WriteLine("FAIL: " + failure);
        Console.WriteLine($"Classroom sender outcome independent checks: {checks - failures.Count}/{checks} passed, {failures.Count} failed.");
        if (failures.Count > 0) throw new InvalidOperationException($"{failures.Count} of {checks} independent sender outcome checks failed.");
        return checks;
    }
}
