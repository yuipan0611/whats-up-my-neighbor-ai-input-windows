using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using NekoChat.Core.Storage;

namespace NekoChat.Core.V2;

public sealed record V2ClientOptions
{
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromSeconds(15);
}
public sealed record V2SendResult(string Code, CompleteResult? Completion, bool MayHaveDelivered);
public sealed record V2ReceiptSendResult(string Code, bool Completed);

/// <summary>No message queue or automatic user action. A call requires the currently prepared manual attempt.</summary>
public sealed class V2LanClient : IDisposable
{
    private readonly IChatStore _store;
    private readonly V2PacketWriter _writer;
    private readonly V2Verifier _verifier;
    private readonly V2ClientOptions _options;
    private readonly HttpClient _http;
    public V2LanClient(IChatStore store, V2PacketWriter writer, V2ClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(store); ArgumentNullException.ThrowIfNull(writer);
        _store = store; _writer = writer; _verifier = new(writer.MaxTextBytes); _options = options ?? new();
        if (_options.ConnectTimeout <= TimeSpan.Zero || _options.ConnectTimeout > _options.RequestTimeout || _options.RequestTimeout > _options.OperationTimeout ||
            _options.OperationTimeout > TimeSpan.FromMinutes(1)) throw new ArgumentException("Invalid client timeouts.", nameof(options));
        _http = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, ConnectTimeout = _options.ConnectTimeout, MaxResponseHeadersLength = 8 }) { Timeout = Timeout.InfiniteTimeSpan };
    }
    public async Task<V2SendResult> SendPreparedAsync(PreparedSend prepared, string targetIp, int port, Func<bool> sessionCurrent, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(prepared); ArgumentNullException.ThrowIfNull(sessionCurrent); ValidateEndpoint(targetIp, port);
        bool mayHaveDelivered = false;
        V2SendResult Fail(string code)
        {
            var outcome = mayHaveDelivered || code is "cancelled" or "network_unconfirmed" ? (AttemptOutcome)new UnconfirmedOutcome() : new FailedOutcome(code);
            try { return new(code, _store.CompleteAttempt(prepared.MessageId, outcome, prepared.TryCount), mayHaveDelivered); }
            catch (Exception e) when (e is StoreException or ObjectDisposedException) { return new("storage_unavailable", null, mayHaveDelivered); }
        }
        try
        {
            if (_store.GetPreparedSend(prepared.MessageId) != prepared) return new("stale_attempt", new Discarded("stale-attempt"), false);
            var captured = _store.GetPeerVerificationContext(prepared.RecipientId);
            string? invalid = CheckContext(captured, prepared.SenderId);
            if (invalid is not null) return Fail(invalid);
            bool CurrentBeforeNetwork() => sessionCurrent() && _store.GetPeerSecuritySnapshot(prepared.RecipientId) == captured.Security;
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellation); operation.CancelAfter(_options.OperationTimeout);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                operation.Token.ThrowIfCancellationRequested(); if (!CurrentBeforeNetwork()) return Fail("receiver_not_ready");
                var hello = await HelloAsync(targetIp, port, captured, new[] { "msg.sign" }, operation.Token).ConfigureAwait(false);
                if (!hello.Success) return Fail(hello.Code);
                if (V2Text.Check(prepared.RawText, Math.Min(_writer.MaxTextBytes, hello.Value!.MaxTextBytes)) != V2TextProblem.None) return Fail("payload_too_large");
                if (!CurrentBeforeNetwork()) return Fail("receiver_not_ready");
                byte[] packet = _writer.Message(prepared.MessageId, prepared.RecipientId, hello.Value.Nonce, prepared.RawText, prepared.Reaction);
                mayHaveDelivered = true;
                var response = await ExchangeAsync(targetIp, port, "/nekochat/v2/message", packet, operation.Token).ConfigureAwait(false);
                if (response.Error == "stale_nonce" && attempt == 0) continue;
                if (response.Error is not null) return Fail(response.Error);
                var ack = _verifier.VerifyMessageAck(response.Body, new(prepared.MessageId, prepared.SenderId, captured.PeerSpkiBase64!, prepared.FingerprintHex));
                if (!ack.Success) return Fail(ack.Code);
                // Cancellation cannot erase a fully received, verified remote storage fact. Identity/lifetime changes still fence it.
                var result = _store.CompleteV2Send(ack.Value!, captured.Security, sessionCurrent);
                return new(result is Confirmed or AlreadyConfirmed ? "ok" : "stale_security", result, true);
            }
            return Fail("stale_nonce");
        }
        catch (OperationCanceledException) { return Fail(cancellation.IsCancellationRequested ? "cancelled" : "network_unconfirmed"); }
        catch (HttpRequestException) { return Fail("network_unconfirmed"); }
        catch (IOException) { return Fail("network_unconfirmed"); }
        catch (StoreException) { return new("storage_unavailable", null, mayHaveDelivered); }
        catch (ObjectDisposedException) { return new("receiver_not_ready", null, mayHaveDelivered); }
    }
    public async Task<V2ReceiptSendResult> SendReadReceiptAsync(PendingReadReceipt receipt, string targetIp, int port, Func<bool> sessionCurrent, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(receipt); ArgumentNullException.ThrowIfNull(sessionCurrent); ValidateEndpoint(targetIp, port);
        try
        {
            var captured = _store.GetPeerVerificationContext(receipt.OriginalSenderId);
            string? invalid = CheckContext(captured, receipt.ReaderId); if (invalid is not null) return new(invalid, false);
            bool Allowed() => sessionCurrent() && _store.GetPeerSecuritySnapshot(receipt.OriginalSenderId) == captured.Security &&
                _store.GetReadReceiptPolicy() == new ReadReceiptPolicy(true, receipt.PolicyGeneration) && _store.ListPendingReadReceipts(200).Contains(receipt);
            if (!Allowed()) return new("policy_changed", false);
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellation); operation.CancelAfter(_options.OperationTimeout);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                operation.Token.ThrowIfCancellationRequested();
                var hello = await HelloAsync(targetIp, port, captured, new[] { "receipt.read" }, operation.Token).ConfigureAwait(false);
                if (!hello.Success) return new(hello.Code, false);
                if (!Allowed()) return new("policy_changed", false);
                var response = await ExchangeAsync(targetIp, port, "/nekochat/v2/receipt", _writer.Receipt(receipt.MessageId, receipt.OriginalSenderId, hello.Value!.Nonce), operation.Token).ConfigureAwait(false);
                if (response.Error == "stale_nonce" && attempt == 0) continue;
                if (response.Error is not null) return new(response.Error, false);
                var ack = _verifier.VerifyReceiptAck(response.Body, new(receipt.MessageId, receipt.ReaderId, captured.PeerSpkiBase64!));
                if (!ack.Success) return new(ack.Code, false);
                bool completed = _store.CompleteV2ReadReceipt(receipt, ack.Value!, captured.Security, sessionCurrent);
                return new(completed ? "ok" : "stale_security", completed);
            }
            return new("stale_nonce", false);
        }
        catch (OperationCanceledException) { return new(cancellation.IsCancellationRequested ? "cancelled" : "network_unconfirmed", false); }
        catch (Exception e) when (e is HttpRequestException or IOException) { return new("network_unconfirmed", false); }
        catch (StoreException) { return new("storage_unavailable", false); }
        catch (ObjectDisposedException) { return new("receiver_not_ready", false); }
    }
    private string? CheckContext(PeerVerificationContext context, string sender)
    {
        if (sender != _writer.PeerId || context.Security.LocalIdentityId != sender || context.LocalSpkiBase64 != _writer.PublicSpkiBase64) return "receiver_not_ready";
        if (context.Security.Pairing != PairingState.Paired) return "unpaired_sender";
        return context.PeerSpkiBase64 is null ? "key_mismatch" : null;
    }
    private async Task<V2Verification<V2HelloInfo>> HelloAsync(string ip, int port, PeerVerificationContext captured, string[] capabilities, CancellationToken cancellation)
    {
        string nonce = V2PacketWriter.NewClientNonce();
        var response = await ExchangeAsync(ip, port, "/nekochat/v2/hello", _writer.HelloRequest(nonce), cancellation).ConfigureAwait(false);
        if (response.Error is not null) return new(response.Error);
        var hello = _verifier.VerifyHello(response.Body, new(nonce, new("paired", captured.PeerSpkiBase64), capabilities));
        return hello.Success && hello.Value!.ReceiverId != captured.Security.PeerId ? new("hello_key_mismatch") : hello;
    }
    private sealed record Response(byte[] Body, string? Error = null);
    private async Task<Response> ExchangeAsync(string ip, int port, string path, byte[] packet, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(_options.RequestTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://{ip}:{port}{path}") { Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact, Content = new ByteArrayContent(packet) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        var media = response.Content.Headers.ContentType;
        if (response.Content.Headers.ContentLength is > 8192 || media?.MediaType?.Equals("application/json", StringComparison.OrdinalIgnoreCase) != true ||
            media.CharSet is { } charset && !charset.Trim('"').Equals("utf-8", StringComparison.OrdinalIgnoreCase) || response.Content.Headers.ContentEncoding.Count != 0) return new([], "invalid_response");
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        byte[] buffer = new byte[8193]; int count = 0;
        while (count < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(count), timeout.Token).ConfigureAwait(false); if (read == 0) break; count += read;
        }
        if (count > 8192) return new([], "invalid_response");
        byte[] body = buffer[..count];
        if (response.StatusCode == HttpStatusCode.OK) return new(body);
        if (V2Json.TryParseObject(body, V2JsonLimits.Control, out var error, out _) && error!.HasExactKeys("protocol", "version", "kind", "code") &&
            error.TryGetString("protocol", out var protocol) && protocol == "nekochat" && error.TryGetNumberToken("version", out var version) && version == "2" &&
            error.TryGetString("kind", out var kind) && kind == "error" && error.TryGetString("code", out var code) &&
            AllowedErrors.Contains(code) && V2HttpResponse.Error(code).StatusCode == (int)response.StatusCode) return new(body, code);
        return new([], "invalid_response");
    }
    private static readonly HashSet<string> AllowedErrors = new(StringComparer.Ordinal) { "invalid_payload", "payload_too_large", "unsupported_version", "unsupported_capability", "unpaired_sender", "key_mismatch", "bad_signature", "wrong_recipient", "stale_nonce", "conflict", "rate_limited", "storage_unavailable", "receiver_not_ready" };
    private static void ValidateEndpoint(string ip, int port)
    { if (!LanInvitation.PrivateIPv4(ip) || port is < 1 or > 65535) throw new ArgumentException("A literal private IPv4 endpoint is required."); }
    public void Dispose() => _http.Dispose();
}
