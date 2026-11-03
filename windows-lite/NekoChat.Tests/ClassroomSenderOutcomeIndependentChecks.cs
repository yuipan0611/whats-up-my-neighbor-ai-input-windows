using Microsoft.Data.Sqlite;
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

    private sealed record Run(ClassroomSendResult? Result, string? Thrown, IReadOnlyList<WireReq> Seen, string? DraftText, IReadOnlyList<(string Method, object?[] Args)> Calls, string AttemptState = "", bool? RetryMayHave = null, string AttemptRows = "")
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
            return Loopback_Json(ClassroomPacketWriter.HelloOk(doc.RootElement.GetProperty("client_nonce").GetString()!, ClassroomPacketWriter.NewToken(), PeerIp, observed, new[] { "msg.plain", "receipt.read" }));
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
            string draft = store.GetClassroomDraftState(principal).RawText ?? "";
            string state = AttemptStateOf(dir, started.MessageId);
            string rows = AttemptTryOf(dir, started.MessageId);
            bool? mayHave = store.BeginClassroomRetry(started.MessageId, principal, Text, 0, 1) is AttemptStarted retry ? retry.PreviousAttemptMayHaveDelivered : null;
            return new Run(result, thrown, peer?.Seen ?? Array.Empty<WireReq>(), draft, recorder.Calls, state, mayHave, rows);
        }
        finally { NekoChat.Tests.Loopback.Cleanup(dir); }
    }

    /// <summary>The attempt's stored state, read straight from the SQLite file (read-only): the public store API has no accessor for it.</summary>
    private static string AttemptStateOf(string dir, string messageId)
    {
        string db = Directory.GetFiles(dir, "nekochat.db", SearchOption.AllDirectories).Single();
        using var c = new SqliteConnection("Data Source=" + db + ";Mode=ReadOnly;Pooling=False"); c.Open();
        using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT attemptState FROM ClassroomAttempt WHERE messageId=@id"; cmd.Parameters.AddWithValue("@id", messageId);
        return Convert.ToString(cmd.ExecuteScalar()) ?? "<none>";
    }

    private sealed record ReceiptRun(ClassroomReceiptSendResult? Result, string? Thrown, int PendingBefore, int PendingAfter, IReadOnlyList<WireReq> Seen)
    {
        internal bool ReceiptRequestSent => Seen.Any(r => r.Path.EndsWith("/nekochat/classroom/v1/receipt", StringComparison.Ordinal));
    }

    /// <param name="observedOverride">What the peer claims as observed_sender_ip. null = truthful (the real remote address of the connection).</param>
    private static Func<WireReq, WireReply> ReceiptPeer(Func<WireReq, WireReply>? onReceipt = null, string? observedOverride = null) => r =>
    {
        string observed = observedOverride ?? r.Remote?.ToString() ?? Loopback;
        using var doc = JsonDocument.Parse(r.Body); var root = doc.RootElement;
        if (r.IsHello)
            return Loopback_Json(ClassroomPacketWriter.HelloOk(root.GetProperty("client_nonce").GetString()!, ClassroomPacketWriter.NewToken(), PeerIp, observed, new[] { "msg.plain", "receipt.read" }));
        if (r.Path.EndsWith("/nekochat/classroom/v1/receipt", StringComparison.Ordinal))
            return onReceipt is not null ? onReceipt(r) : Loopback_Json(ClassroomPacketWriter.ReceiptAck("ok", root.GetProperty("message_id").GetString()!, root.GetProperty("session").GetString()!, observed, PeerIp));
        return Loopback_Json("{}"u8.ToArray(), 400);
    };

    /// <summary>One pending outbound read receipt (policy on, one visible incoming message), then SendReceiptAsync against the scripted peer.</summary>
    private static async Task<ReceiptRun> ExerciseReceipt(Func<WireReq, WireReply> script, Func<int?, HttpClient>? clientFactory = null)
    {
        string dir = NekoChat.Tests.Loopback.TempDir("classroom-receipt-indep-");
        try
        {
            using var store = NekoChat.Tests.Loopback.OpenStore(dir);
            var principal = ClassroomPrincipalKey.ForSource(PeerIp)!.Value;
            store.SetReadReceiptPolicy(true);
            var stored = store.ReceiveClassroomMessage(PeerIp, new(ClassroomPacketWriter.NewMessageId(), "你好 receipt-marker"), () => true) as ClassroomStored ?? throw new InvalidOperationException("fixture: incoming message not stored");
            store.MarkClassroomVisibleRead(principal, new[] { stored.LocalSeq }, new VisibleReadContext(principal.Value, true, true, false, true));
            var pending = store.ListPendingClassroomReceipts();
            if (pending.Count != 1) throw new InvalidOperationException("fixture: expected exactly one pending receipt, got " + pending.Count);
            await using var peer = new LoopbackPeer(script);
            using var http = clientFactory is not null ? clientFactory(peer.Port) : NekoChat.Tests.Loopback.Client(peer.Port);
            var sender = new ClassroomSender(store, http);
            ClassroomReceiptSendResult? result = null; string? thrown = null;
            try { result = await sender.SendReceiptAsync(pending[0], 8768, Loopback, () => true); }
            catch (Exception e) { thrown = e.GetType().Name + ": " + e.Message; }
            return new ReceiptRun(result, thrown, pending.Count, store.ListPendingClassroomReceipts().Count, peer.Seen);
        }
        finally { NekoChat.Tests.Loopback.Cleanup(dir); }
    }

    private static WireReply StoredAck(WireReq r)
    {
        using var doc = JsonDocument.Parse(r.Body); var root = doc.RootElement;
        string? reaction = root.TryGetProperty("reaction", out var re) && re.ValueKind == JsonValueKind.String ? re.GetString() : null;
        return Loopback_Json(ClassroomPacketWriter.MessageAck("stored", root.GetProperty("message_id").GetString()!, root.GetProperty("session").GetString()!, r.Remote?.ToString() ?? Loopback, PeerIp, reaction, root.GetProperty("text").GetString()!));
    }
    private static string JsonField(WireReq r, string name) { using var d = JsonDocument.Parse(r.Body); return d.RootElement.GetProperty(name).GetString() ?? ""; }
    private static string Kind(WireReq r) => r.IsHello ? "hello" : r.IsClassroomMessage ? "message" : "other:" + r.Path;
    private static string AttemptTryOf(string dir, string messageId)
    {
        string db = Directory.GetFiles(dir, "nekochat.db", SearchOption.AllDirectories).Single();
        using var c = new SqliteConnection("Data Source=" + db + ";Mode=ReadOnly;Pooling=False"); c.Open();
        using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT count(*) || '/' || max(tryCount) FROM ClassroomAttempt WHERE messageId=@id"; cmd.Parameters.AddWithValue("@id", messageId);
        return Convert.ToString(cmd.ExecuteScalar()) ?? "";
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
            Check(o.AttemptState == "failed" && o.DraftText == Text, "F4b hello with no recorded local endpoint: the attempt is failed (CM-18: nothing was sent) and the draft is kept", Describe(o));
        }
        {
            var o = await Exercise(Peer(), Loopback, clientFactory: p => PlainClient(p, recordForHello: true));
            Check(o.Thrown is null && !o.Confirmed && o.Result is { Success: false }, "F4b message with no recorded local endpoint (hello had one): a well-formed stored ACK is still not trusted", Describe(o));
            Check(o.CompletionState == "unconfirmed" && o.DraftText == Text, "F4b message with no recorded local endpoint: the attempt is unconfirmed and the draft is kept", Describe(o));
        }

        // ---- H (CM-18, NCMW-v2 §1.4 / §5 / §10 / §11.1): any failure while the HELLO is still the only request means no message byte was written, so the attempt is
        // `failed` (not `unconfirmed`) and a manual retry must not warn that the previous try "may have been delivered". Only a user cancel is `cancelled`. ----
        static byte[] Bytes(string json) => System.Text.Encoding.UTF8.GetBytes(json);
        var helloFailures = new (string Name, Func<WireReq, WireReply>? Script, Func<int?, HttpClient>? Client)[]
        {
            ("400 with a body that is not exactly the legacy signal", _ => NekoChat.Tests.Loopback.Json(400, Bytes("{\"error\":\"empty_text\",\"x\":1}")), null),
            ("404", _ => NekoChat.Tests.Loopback.Json(404, Bytes("{}")), null),
            ("a well-formed classroom error envelope (rate_limited)", _ => NekoChat.Tests.Loopback.ClassroomError(ClassroomCodes.RateLimited), null),
            ("a well-formed classroom error envelope (receiver_not_ready)", _ => NekoChat.Tests.Loopback.ClassroomError(ClassroomCodes.ReceiverNotReady), null),
            ("connection refused", null, null),
            ("no answer (timeout)", _ => new WireSilence(), null),
            ("connection closed without a response", _ => new WireHangup(), null),
            ("200 with a malformed hello.ok (invalid_response)", _ => NekoChat.Tests.Loopback.Json(200, Bytes("{}")), null),
            ("200 hello.ok echoing another client_nonce (ack_mismatch)", r =>
            {
                using var d = JsonDocument.Parse(r.Body);
                return NekoChat.Tests.Loopback.Json(200, ClassroomPacketWriter.HelloOk(ClassroomPacketWriter.NewToken(), ClassroomPacketWriter.NewToken(), PeerIp, r.Remote?.ToString() ?? Loopback, new[] { "msg.plain" }));
            }, null),
            ("200 hello.ok without the msg.plain capability (capability_missing)", r =>
            {
                using var d = JsonDocument.Parse(r.Body);
                return NekoChat.Tests.Loopback.Json(200, ClassroomPacketWriter.HelloOk(d.RootElement.GetProperty("client_nonce").GetString()!, ClassroomPacketWriter.NewToken(), PeerIp, r.Remote?.ToString() ?? Loopback, new[] { "receipt.read" }));
            }, null),
            ("no recorded local endpoint (local_endpoint_unknown)", Peer(), p => PlainClient(p, recordForHello: false)),
        };
        foreach (var (name, script, client) in helloFailures)
        {
            var o = await Exercise(script, Loopback, clientFactory: client);
            Check(o.Thrown is null && !o.Confirmed && !o.TextOnWire && !o.Calls.Any(c => c.Method == "RecordClassroomRoute"), $"H hello {name}: nothing sent, no route, not confirmed", Describe(o));
            Check(o.AttemptState == "failed", $"H hello {name}: the attempt is failed", Describe(o) + " state=" + o.AttemptState);
            Check(o.RetryMayHave == false, $"H hello {name}: a manual retry does not claim the previous try may have been delivered", "retryMayHave=" + o.RetryMayHave);
            Check(o.DraftText == Text, $"H hello {name}: the draft is kept", "draft=" + o.DraftText);
        }
        {
            var o = await Exercise(r => r.IsHello ? new WireSilence() : Loopback_Json("{}"u8.ToArray()), Loopback, cancelAfterMs: 250);
            Check(!o.TextOnWire && !o.Calls.Any(c => c.Method == "RecordClassroomRoute") && !o.Confirmed, "H cancelled while hello is pending: nothing sent, no route, not confirmed", Describe(o));
            Check(o.AttemptState == "cancelled", "H cancelled while hello is pending: the attempt is cancelled (§11.1), not left `sending` and not unconfirmed", Describe(o) + " state=" + o.AttemptState);
        }
        {
            var o = await Exercise(Peer(_ => NekoChat.Tests.Loopback.ClassroomError(ClassroomCodes.ReceiverNotReady)), Loopback); // control: after the message bytes went out the same kind of reply is unconfirmed (§10)
            Check(o.AttemptState == "unconfirmed" && o.RetryMayHave == true, "H control: a message-stage receiver_not_ready stays unconfirmed and a retry warns", Describe(o) + " state=" + o.AttemptState + " retryMayHave=" + o.RetryMayHave);
        }

        // ---- RC (CM-13 §9 + §6/§5 echo check): an outbound read receipt leaves the outbox only for a VERIFIED, bound receipt.ack. If the real local endpoint of the
        // receipt request (or of its hello) is unknown, nothing may be trusted: the event stays queued. ----
        string Describe2(ReceiptRun o) => (o.Thrown ?? $"completed={o.Result?.Completed} status={o.Result?.Status} error={o.Result?.ErrorCode}") + $"; pending {o.PendingBefore}->{o.PendingAfter}; requests=[{string.Join(", ", o.Seen.Select(r => r.Path.Replace("/nekochat/classroom/v1/", "")))}]";
        {
            var o = await ExerciseReceipt(ReceiptPeer());
            Check(o.Thrown is null && o.Result is { Completed: true } && o.PendingAfter == 0, "RC control: a truthful, bound receipt.ack completes the receipt and removes the outbox event", Describe2(o));
        }
        {
            var o = await ExerciseReceipt(ReceiptPeer(), p => PlainClient(p, recordForHello: true)); // hello had its endpoint recorded, the receipt request did not
            Check(o.Thrown is null && o.ReceiptRequestSent && o.Result is { Completed: false } && o.PendingAfter == 1, "RC receipt request with no recorded local endpoint: a well-formed ok ack is not trusted and the outbox event is kept", Describe2(o));
        }
        {
            var o = await ExerciseReceipt(ReceiptPeer(), p => PlainClient(p, recordForHello: false));
            Check(o.Thrown is null && !o.ReceiptRequestSent && o.Result is { Completed: false } && o.PendingAfter == 1, "RC hello with no recorded local endpoint: no receipt is sent and the outbox event is kept", Describe2(o));
        }
        {
            var o = await ExerciseReceipt(ReceiptPeer(observedOverride: WrongGuess)); // peer claims it saw 192.168.1.10, which is not this connection's address
            Check(o.Thrown is null && o.Result is { Completed: false } && o.PendingAfter == 1, "RC an ack whose observed_sender_ip is not the connection's real address is not accepted; the event is kept", Describe2(o));
        }
        {
            var o = await ExerciseReceipt(ReceiptPeer(_ => NekoChat.Tests.Loopback.ClassroomError(ClassroomCodes.Transient)));
            Check(o.Thrown is null && o.Result is { Completed: false } && o.PendingAfter == 1, "RC control: transient keeps the outbox event (CM-13 §9 item 3)", Describe2(o));
        }

        // ---- H-transient (frozen spec §10: "transient" on the message/hello routes is ALWAYS treated as unconfirmed). The CM-18 rule "hello failures are failed" has this one exception. ----
        {
            var o = await Exercise(r => NekoChat.Tests.Loopback.ClassroomError(ClassroomCodes.Transient), Loopback);
            Check(o.Thrown is null && !o.Confirmed && !o.TextOnWire && !o.Calls.Any(c => c.Method == "RecordClassroomRoute"), "H-transient hello answered transient: nothing sent, no route, not confirmed", Describe(o));
            Check(o.AttemptState == "unconfirmed", "H-transient hello answered transient: the attempt is unconfirmed (§10), not failed", Describe(o) + " state=" + o.AttemptState);
            Check(o.DraftText == Text, "H-transient hello answered transient: the draft is kept", "draft=" + o.DraftText);
        }

        // ---- C (frozen spec §11.1): `cancelled` only happens before any message byte was written, so the other side cannot have it. A manual retry of a cancelled attempt must NOT
        // warn "may have been delivered"; of an unconfirmed attempt it must; of a failed attempt it must not. (Store level; no sender involved.) ----
        {
            string dir = NekoChat.Tests.Loopback.TempDir("classroom-cancel-retry-indep-");
            try
            {
                using var store = NekoChat.Tests.Loopback.OpenStore(dir);
                var principal = ClassroomPrincipalKey.ForSource(PeerIp)!.Value;
                bool? MayHaveAfter(Func<string, int, object> finish)
                {
                    var begun = (AttemptStarted)(store.GetClassroomDraftState(principal).RawText is null ? store.BeginClassroomSend(principal, Text, store.GetClassroomDraftState(principal).Epoch, store.GetClassroomDraftState(principal).Revision + 1) : store.BeginClassroomSend(principal, Text, store.GetClassroomDraftState(principal).Epoch, store.GetClassroomDraftState(principal).Revision));
                    finish(begun.MessageId, begun.TryCount);
                    var d = store.GetClassroomDraftState(principal);
                    var retry = store.BeginClassroomRetry(begun.MessageId, principal, Text, d.Epoch, d.Revision) as AttemptStarted;
                    if (retry is not null) store.CancelClassroomAttempt(begun.MessageId);   // leave no attempt in `sending` for the next case
                    return retry?.PreviousAttemptMayHaveDelivered;
                }
                bool? afterCancelled = MayHaveAfter((id, n) => store.CancelClassroomAttempt(id, n));
                bool? afterUnconfirmed = MayHaveAfter((id, n) => store.CompleteClassroomAttempt(id, new UnconfirmedOutcome(), PeerIp, n));
                bool? afterFailed = MayHaveAfter((id, n) => store.CompleteClassroomAttempt(id, new FailedOutcome("invalid_payload"), PeerIp, n));
                Check(afterCancelled == false, "C retry of a CANCELLED attempt does not claim the previous try may have been delivered (§11.1)", "mayHave=" + afterCancelled);
                Check(afterUnconfirmed == true, "C control: retry of an UNCONFIRMED attempt does warn", "mayHave=" + afterUnconfirmed);
                Check(afterFailed == false, "C control: retry of a FAILED attempt does not warn", "mayHave=" + afterFailed);
            }
            finally { NekoChat.Tests.Loopback.Cleanup(dir); }
        }

        // ---- S (frozen spec §6.9): on stale_session the sender does a NEW hello and sends the SAME message_id ONCE more. Still one manual attempt. A second stale_session is `failed`. ----
        {
            int messages = 0;
            var o = await Exercise(Peer(r => Interlocked.Increment(ref messages) == 1 ? NekoChat.Tests.Loopback.ClassroomError(ClassroomCodes.StaleSession) : StoredAck(r)), Loopback);
            string seq = string.Join(",", o.Seen.Select(Kind));
            var sent = o.Seen.Where(r => r.IsClassroomMessage).ToArray();
            Check(seq == "hello,message,hello,message", "S stale_session on the first message: new hello, then the same message once more", "sequence=" + seq + "; " + Describe(o));
            Check(sent.Length == 2 && JsonField(sent[0], "message_id") == JsonField(sent[1], "message_id") && JsonField(sent[0], "text") == JsonField(sent[1], "text"), "S the retry carries the same message_id and the same text", "messages=" + sent.Length);
            Check(sent.Length == 2 && JsonField(sent[0], "session") != JsonField(sent[1], "session"), "S the retry uses the fresh session from the new hello", "");
            Check(o.Thrown is null && o.Confirmed, "S the second answer (stored) confirms the send", Describe(o));
            Check(o.AttemptRows == "1/1", "S the whole sequence is ONE manual attempt (one attempt row, tryCount 1)", "rows/maxTry=" + o.AttemptRows);
        }
        {
            var o = await Exercise(Peer(_ => NekoChat.Tests.Loopback.ClassroomError(ClassroomCodes.StaleSession)), Loopback);
            string seq = string.Join(",", o.Seen.Select(Kind));
            var sent = o.Seen.Where(r => r.IsClassroomMessage).ToArray();
            Check(seq == "hello,message,hello,message", "S stale_session twice: exactly one retry, no third try", "sequence=" + seq + "; " + Describe(o));
            Check(o.Thrown is null && !o.Confirmed && o.AttemptState == "failed", "S stale_session twice: the attempt is failed (§10: stale_session stored nothing)", Describe(o) + " state=" + o.AttemptState);
            Check(sent.Length == 2 && JsonField(sent[0], "message_id") == JsonField(sent[1], "message_id"), "S stale_session twice: both tries used the same message_id", "messages=" + sent.Length);
            Check(o.AttemptRows == "1/1" && o.DraftText == Text, "S stale_session twice: still one manual attempt and the draft is kept", "rows/maxTry=" + o.AttemptRows + " draft=" + o.DraftText);
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
