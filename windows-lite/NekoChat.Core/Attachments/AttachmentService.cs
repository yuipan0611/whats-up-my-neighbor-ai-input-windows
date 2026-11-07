using NekoChat.Core.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NekoChat.Core.Classroom.Transport;
using NekoChat.Core.Classroom.Wire;
using NekoChat.Core.Storage;

namespace NekoChat.Core.Attachments;

public sealed class AttachmentService : IAsyncDisposable
{
    private readonly ChatStore _store;
    private readonly DiagnosticJournal? _diagnostics;
    public AttachmentVault Vault { get; }
    private readonly HttpClient _http = new(ClassroomSender.CreateDefaultHandler()) { Timeout = TimeSpan.FromSeconds(10) };
    private readonly object _gate = new();
    private readonly Dictionary<long, (CancellationTokenSource Cancellation, Task Work)> _jobs = [];
    // One download and one serving lane: at most two binary transfers, with text slots reserved by the listener.
    private readonly SemaphoreSlim _download = new(1), _serve = new(1), _filesGate = new(1);
    internal SemaphoreSlim DownloadLane => _download;
    internal SemaphoreSlim ServeLane => _serve;
    internal SemaphoreSlim FilesGate => _filesGate;
    internal bool CacheClearing => _cacheClearing;
    private volatile bool _cacheClearing;
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _disposal;
    private long _generation;
    private bool _ready;
    public int Port { get; set; }
    public event Action? Changed;
    public event Action<string, string>? MessageStored;
    public event Action<long, long, long>? Progress;

    public AttachmentService(ChatStore store, string root, int port, long capacity = AttachmentLimits.StorageBytes, DiagnosticJournal? diagnostics = null)
    {
        _diagnostics = diagnostics; _store = store; Port = port; Vault = new(root, capacity);
        _store.RecoverAttachments(); Vault.Collect(_store.AttachmentReferences().Concat(_store.ClassroomFileReferences()).ToHashSet());
        foreach (var row in _store.ListAttachments().Where(r => r.State == "complete" && !Vault.Exists(r.File)))
            _store.SetAttachmentState(row, "unavailable", "missing_file");
    }
    public void SetReady(bool ready)
    {
        (CancellationTokenSource Cancellation, Task Work)[] cancel = [];
        lock (_gate) { _ready = ready && _disposal is null; if (!ready) { _generation++; cancel = _jobs.Values.ToArray(); } }
        foreach (var job in cancel) SafeCancel(job.Cancellation);
    }
    private bool Live(long generation) { lock (_gate) return _ready && !_cacheClearing && _generation == generation && !_lifetime.IsCancellationRequested; }
    private void Notify() { try { Changed?.Invoke(); } catch { /* observers cannot change durable transfer outcomes */ } }
    public Task<AttachmentDraft?> DraftAsync(ClassroomPrincipalKey principal) => Task.Run(() => _store.GetAttachmentDraft(principal));
    public async Task<AttachmentDraft> AddDraftAsync(ClassroomPrincipalKey principal, string path, CancellationToken cancellation = default)
    {
        long epoch = await Task.Run(() => _store.GetClassroomDraftState(principal).Epoch, cancellation).ConfigureAwait(false);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _lifetime.Token);
        await _filesGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try { if (_cacheClearing) throw new AttachmentException("busy"); var file = await Vault.StageAsync(path, linked.Token).ConfigureAwait(false); return await SaveDraftAsync(principal, file, epoch, linked.Token).ConfigureAwait(false); }
        finally { _filesGate.Release(); }
    }
    public async Task<AttachmentDraft> AddDraftAsync(ClassroomPrincipalKey principal, Stream png, string name, CancellationToken cancellation = default)
    {
        long epoch = await Task.Run(() => _store.GetClassroomDraftState(principal).Epoch, cancellation).ConfigureAwait(false);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _lifetime.Token);
        await _filesGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try { if (_cacheClearing) throw new AttachmentException("busy"); var file = await Vault.StageAsync(png, name, png.Length, linked.Token).ConfigureAwait(false); return await SaveDraftAsync(principal, file, epoch, linked.Token).ConfigureAwait(false); }
        finally { _filesGate.Release(); }
    }
    private async Task<AttachmentDraft> SaveDraftAsync(ClassroomPrincipalKey principal, AttachmentFile file, long epoch, CancellationToken cancellation)
    {
        try
        {
            bool saved = await Task.Run(() => !cancellation.IsCancellationRequested && _store.SaveAttachmentDraft(principal, file, epoch), cancellation).ConfigureAwait(false);
            if (!saved) throw new AttachmentException("cleared");
            Notify(); return new(file, epoch);
        }
        catch { Vault.Delete(file.BlobId); throw; }
    }
    public async Task RemoveDraftAsync(ClassroomPrincipalKey principal, string blob)
    { await _filesGate.WaitAsync().ConfigureAwait(false); try { await Task.Run(() => { _store.RemoveAttachmentDraft(principal, blob); Notify(); }).ConfigureAwait(false); } finally { _filesGate.Release(); } }
    public Task<IReadOnlyList<AttachmentRecord>> HistoryAsync(string principal) => Task.Run(() => _store.ListAttachments(principal));
    public async Task<AttachmentRecord> SendAsync(ClassroomPrincipalKey principal, string caption, long epoch, long revision, int peerPort)
    {
        var row = await Task.Run(() => _store.PrepareAttachmentSend(principal, caption, peerPort, epoch, revision)).ConfigureAwait(false);
        await Start(row, async (generation, token) => {
            bool offerMayHaveWritten = false;
            try
            {
                if (!Vault.Exists(row.File)) throw new AttachmentException("unavailable");
                if (row.State != "complete") _store.SetAttachmentState(row, "sending", current: () => Current(row, generation, token));
                var hello = await HelloAsync(row.PeerIp, row.PeerPort, token).ConfigureAwait(false);
                EnsureCurrent(row, generation, token);
                offerMayHaveWritten = true;
                using var response = await RequestAsync(row.PeerIp, row.PeerPort, "offer", new {
                    protocol = "nekochat.attachments", version = 1, kind = "offer", session = hello.Session,
                    message_id = row.MessageId, blob_id = row.File.BlobId, name = row.File.Name, size = row.File.Size,
                    sha256 = row.File.Sha256, caption = row.Caption, token = row.Token, port = Port
                }, token).ConfigureAwait(false);
                using var body = await JsonResponseAsync(response, token).ConfigureAwait(false);
                var result = body.RootElement;
                ValidateAck(result, row);
                EnsureCurrent(row, generation, token);
                if (Get(result, "status") == "cleared") throw new AttachmentException("cleared");
                if (Get(result, "status") is not ("offered" or "complete")) throw new AttachmentException("invalid_response");
                if (!_store.AcknowledgeAttachmentOffer(row, epoch, revision)) throw new AttachmentException("cleared");
                if (Get(result, "status") == "complete") _store.SetAttachmentState(row, "complete", current: () => Current(row, generation, token));
            }
            catch (Exception error)
            {
                _diagnostics?.Record(DiagnosticStage.Attachment, DiagnosticOutcome.Failed, error: error, messageId: row.MessageId, cancellationRequested: token.IsCancellationRequested);
                _store.SetAttachmentState(row, offerMayHaveWritten ? "unconfirmed" : error is OperationCanceledException ? "cancelled" : "failed", ErrorCode(error));
                throw;
            }
        }).ConfigureAwait(false);
        return _store.GetAttachment(row.LocalSeq) ?? throw new AttachmentException("cleared");
    }
    public Task RetryOfferAsync(AttachmentRecord row, Func<bool>? current = null, CancellationToken cancellation = default) => Start(row, async (generation, jobToken) => {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(jobToken, cancellation); var token = linked.Token;
        try
        {
            if (row.Direction != "outgoing" || !Vault.Exists(row.File)) throw new AttachmentException("unavailable");
            if (row.State != "complete") _store.SetAttachmentState(row, "sending", current: () => Current(row, generation, token));
            var hello = await HelloAsync(row.PeerIp, row.PeerPort, token).ConfigureAwait(false);
            EnsureCurrent(row, generation, token);
            if (current?.Invoke() == false) throw new AttachmentException("cleared");
            using var response = await RequestAsync(row.PeerIp, row.PeerPort, "offer", new { protocol = "nekochat.attachments", version = 1, kind = "offer", session = hello.Session,
                message_id = row.MessageId, blob_id = row.File.BlobId, name = row.File.Name, size = row.File.Size, sha256 = row.File.Sha256, caption = row.Caption, token = row.Token, port = Port }, token).ConfigureAwait(false);
            using var body = await JsonResponseAsync(response, token).ConfigureAwait(false); ValidateAck(body.RootElement, row);
            string state = Get(body.RootElement, "status"); if (state is not ("offered" or "complete")) throw new AttachmentException("cleared");
            if (!_store.SetAttachmentState(row, state, current: () => Current(row, generation, token) && (current?.Invoke() ?? true))) throw new AttachmentException("cleared");
        }
        catch (Exception error) { _diagnostics?.Record(DiagnosticStage.Attachment, DiagnosticOutcome.Unconfirmed, error: error, messageId: row.MessageId, cancellationRequested: token.IsCancellationRequested); _store.SetAttachmentState(row, error is OperationCanceledException ? "cancelled" : "unconfirmed", ErrorCode(error)); throw; }
    });
    public Task DownloadAsync(long sequence)
    {
        var row = _store.GetAttachment(sequence) ?? throw new AttachmentException("cleared");
        if (row.Direction != "incoming") throw new AttachmentException("invalid_direction");
        return Start(row, async (generation, token) => {
            await _download.WaitAsync(token).ConfigureAwait(false);
            bool committed = row.State == "complete" && Vault.Exists(row.File);
            try
            {
                EnsureCurrent(row, generation, token);
                if (!committed)
                {
                    if (!_store.SetAttachmentState(row, "receiving", current: () => Current(row, generation, token))) throw new AttachmentException("cleared");
                    Notify();
                    // Retry always starts at zero. A previous partial file is never advertised as completed.
                    Vault.Delete(row.File.BlobId);
                    await using (var output = Vault.Reserve(row.File.BlobId, row.File.Size))
                    {
                        long offset = 0;
                        while (offset < row.File.Size)
                        {
                            EnsureCurrent(row, generation, token);
                            int count = (int)Math.Min(AttachmentLimits.ChunkBytes, row.File.Size - offset);
                            using var response = await RequestAsync(row.PeerIp, row.PeerPort, "chunk", new {
                                protocol = "nekochat.attachments", version = 1, kind = "chunk", message_id = row.MessageId, token = row.Token, offset, count
                            }, token).ConfigureAwait(false);
                            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType != "application/octet-stream") throw new AttachmentException("unavailable");
                            byte[] bytes = await ReadBoundedAsync(response.Content, count, token).ConfigureAwait(false);
                            if (bytes.Length != count) throw new AttachmentException("invalid_length");
                            await output.WriteAsync(bytes, token).ConfigureAwait(false); offset += bytes.Length;
                            try { Progress?.Invoke(row.LocalSeq, offset, row.File.Size); } catch { }
                        }
                        await output.FlushAsync(token).ConfigureAwait(false); output.Flush(true);
                    }
                    await Vault.CommitAsync(row.File, token).ConfigureAwait(false);
                    if (!_store.SetAttachmentState(row, "complete", current: () => Current(row, generation, token))) throw new AttachmentException("cleared");
                    committed = true; Notify();
                }
                await ConfirmAsync(row, token).ConfigureAwait(false);
                _store.SetAttachmentState(row, "complete", current: () => Current(row, generation, token));
            }
            catch (Exception error)
            {
                _diagnostics?.Record(DiagnosticStage.Attachment, DiagnosticOutcome.Failed, error: error, messageId: row.MessageId, cancellationRequested: token.IsCancellationRequested);
                if (committed) _store.SetAttachmentState(row, "complete", "confirmation_pending");
                else { Vault.Delete(row.File.BlobId); _store.SetAttachmentState(row, error is OperationCanceledException ? "cancelled" : "failed", ErrorCode(error)); }
                throw;
            }
            finally { _download.Release(); }
        });
    }
    private async Task ConfirmAsync(AttachmentRecord row, CancellationToken cancellation)
    {
        using var response = await RequestAsync(row.PeerIp, row.PeerPort, "complete", new { protocol = "nekochat.attachments", version = 1, kind = "complete",
            message_id = row.MessageId, token = row.Token, size = row.File.Size, sha256 = row.File.Sha256 }, cancellation).ConfigureAwait(false);
        using var body = await JsonResponseAsync(response, cancellation).ConfigureAwait(false); ValidateAck(body.RootElement, row);
        if (Get(body.RootElement, "status") != "complete") throw new AttachmentException("invalid_response");
    }
    private void EnsureCurrent(AttachmentRecord row, long generation, CancellationToken token)
    { token.ThrowIfCancellationRequested(); if (!Current(row, generation, token)) throw new AttachmentException("cleared"); }
    private bool Current(AttachmentRecord row, long generation, CancellationToken token) => !token.IsCancellationRequested && Live(generation) && _store.AttachmentCurrent(row);
    private Task Start(AttachmentRecord row, Func<long, CancellationToken, Task> work)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposal is not null, this);
            if (!_ready || _cacheClearing) throw new AttachmentException("receiver_not_ready");
            if (_jobs.TryGetValue(row.LocalSeq, out var existing)) return existing.Work;
            if (_jobs.Count >= 16) throw new AttachmentException("busy");
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); long generation = _generation;
            var task = Task.Run(async () => {
                try { await work(generation, cancellation.Token).ConfigureAwait(false); }
                finally { lock (_gate) _jobs.Remove(row.LocalSeq); cancellation.Dispose(); Notify(); }
            });
            _jobs.Add(row.LocalSeq, (cancellation, task)); return task;
        }
    }
    private static void SafeCancel(CancellationTokenSource cancellation) { try { _ = cancellation.CancelAsync(); } catch (ObjectDisposedException) { } }
    public void Cancel(long sequence) { CancellationTokenSource? token; lock (_gate) token = _jobs.TryGetValue(sequence, out var job) ? job.Cancellation : null; if (token is not null) SafeCancel(token); }
    public async Task CancelAsync(long sequence)
    {
        Cancel(sequence);
        await Task.Run(() => { if (_store.GetAttachment(sequence) is { State: not "complete" } row) _store.SetAttachmentState(row, "cancelled", "user_cancelled"); }).ConfigureAwait(false);
        Notify();
    }
    public async Task ClearCacheAsync()
    {
        lock (_gate) { if (_cacheClearing || _disposal is not null) throw new AttachmentException("busy"); _cacheClearing = true; }
        await _filesGate.WaitAsync().ConfigureAwait(false);
        try
        {
            Task[] tasks; CancellationTokenSource[] tokens;
            lock (_gate) { tokens = _jobs.Values.Select(j => j.Cancellation).ToArray(); tasks = _jobs.Values.Select(j => j.Work).ToArray(); }
            foreach (var token in tokens) SafeCancel(token);
            try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch (Exception) { /* each job persisted its result */ }
            await _serve.WaitAsync().ConfigureAwait(false);
            try { await Task.Run(() => { _store.ForgetAttachmentFiles(); _store.ForgetClassroomFileCaches(); Vault.Collect(_store.AttachmentDraftReferences().Concat(_store.ClassroomFileReferences()).ToHashSet()); }).ConfigureAwait(false); }
            finally { _serve.Release(); }
        }
        finally { _filesGate.Release(); lock (_gate) _cacheClearing = false; Notify(); }
    }
    public async Task<ClassroomTransportResponse> HandleAsync(string operation, ReadOnlyMemory<byte> body, string sourceIp, ClassroomSessionTable sessions, Func<bool> current, CancellationToken cancellation)
    {
        try
        {
            if (_cacheClearing || !current() || !LanInvitation.PrivateIPv4(sourceIp)) throw new AttachmentException("receiver_not_ready");
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 4 }); var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != root.EnumerateObject().Count() ||
                Get(root, "protocol") != "nekochat.attachments" || root.GetProperty("version").GetInt32() != 1 || Get(root, "kind") != operation)
                throw new AttachmentException("invalid_payload");
            string id = Get(root, "message_id"), token = Get(root, "token");
            if (!Guid.TryParseExact(id, "D", out var parsed) || parsed.ToString("D") != id || !AttachmentLimits.IsHash(token)) throw new AttachmentException("invalid_payload");
            if (operation == "offer")
            {
                Exact(root, "protocol", "version", "kind", "message_id", "token", "session", "blob_id", "name", "size", "sha256", "caption", "port");
                if (!sessions.TryConsume(Get(root, "session"), sourceIp)) throw new AttachmentException("stale_session");
                int port = root.GetProperty("port").GetInt32(); if (port is < 1 or > 65535) throw new AttachmentException("invalid_payload");
                var file = new AttachmentFile(Get(root, "blob_id"), Get(root, "name"), root.GetProperty("size").GetInt64(), Get(root, "sha256"));
                (AttachmentRecord? Record, bool New) received;
                try { received = _store.ReceiveAttachmentOffer(sourceIp, port, id, file, Get(root, "caption"), token, current); }
                catch (AttachmentException error) when (error.Code == "source_not_allowed")
                {
                    // D-30: accepted offer, never downloaded/complete. The block check and refusal
                    // happened in the receiving writer transaction; nothing identifying is retained.
                    if (!current()) throw new AttachmentException("receiver_not_ready");
                    return Ack(id, file, "offered");
                }
                if (received.Record is not { } record) return Ack(id, file, "cleared");
                if (received.New)
                {
                    try { MessageStored?.Invoke(sourceIp, record.Caption.Length == 0 ? "附件：" + file.Name : record.Caption); } catch { }
                    Notify();
                    if (ClassroomPrincipalKey.TryParse(record.Principal, out var principal) && AttachmentLimits.Automatic(_store.CanAutoReceiveAttachments(principal), file.Size))
                    {
                        _store.EnqueueAttachmentDownload(record);
                        try { _ = DownloadAsync(record.LocalSeq).ContinueWith(task => { _ = task.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default); }
                        catch (AttachmentException) { /* busy: offer stays visible for manual download */ }
                    }
                }
                return Ack(id, file, record.State == "complete" ? "complete" : "offered");
            }
            if (_store.IsSourceBlocked(sourceIp)) throw new AttachmentException("source_not_allowed");
            var outgoing = _store.ListAttachments().SingleOrDefault(r => r.Direction == "outgoing" && r.MessageId == id && r.PeerIp == sourceIp);
            if (outgoing is null || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(outgoing.Token), Convert.FromHexString(token)) || !_store.AttachmentCurrent(outgoing)) throw new AttachmentException("unavailable");
            if (operation == "complete")
            {
                Exact(root, "protocol", "version", "kind", "message_id", "token", "size", "sha256");
                if (Get(root, "sha256") != outgoing.File.Sha256 || root.GetProperty("size").GetInt64() != outgoing.File.Size) throw new AttachmentException("hash_mismatch");
                if (!_store.SetAttachmentState(outgoing, "complete", current: current)) throw new AttachmentException("cleared");
                try { Progress?.Invoke(outgoing.LocalSeq, outgoing.File.Size, outgoing.File.Size); } catch { }
                Notify(); return Ack(id, outgoing.File, "complete");
            }
            if (operation != "chunk") throw new AttachmentException("invalid_payload");
            Exact(root, "protocol", "version", "kind", "message_id", "token", "offset", "count");
            long offset = root.GetProperty("offset").GetInt64(); int count = root.GetProperty("count").GetInt32();
            if (offset < 0 || count is < 1 or > AttachmentLimits.ChunkBytes || offset > outgoing.File.Size - count || outgoing.State is "unavailable" or "cancelled" || !Vault.Exists(outgoing.File)) throw new AttachmentException("unavailable");
            await _serve.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                await using var file = new FileStream(Vault.PathFor(outgoing.File.BlobId), FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
                file.Position = offset; byte[] bytes = new byte[count]; await file.ReadExactlyAsync(bytes, cancellation).ConfigureAwait(false);
                await Task.Delay(10, cancellation).ConfigureAwait(false);
                if (!current() || !_store.AttachmentCurrent(outgoing)) throw new AttachmentException("cleared");
                // The next chunk request proves the preceding offset was received; this is not a completed-file ACK.
                try { Progress?.Invoke(outgoing.LocalSeq, offset, outgoing.File.Size); } catch { }
                return new(200, bytes, Binary: true);
            }
            finally { _serve.Release(); }
        }
        catch (Exception error) when (error is AttachmentException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException or IOException or UnauthorizedAccessException)
        { _diagnostics?.Record(DiagnosticStage.Attachment, DiagnosticOutcome.Rejected, error: error); return Failure(ErrorCode(error)); }
    }
    private async Task<ClassroomHelloInfo> HelloAsync(string ip, int port, CancellationToken token)
    {
        string nonce = ClassroomPacketWriter.NewToken();
        using var request = MakeRequest(ip, port, "/nekochat/classroom/v1/hello", ClassroomPacketWriter.HelloRequest(nonce));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        byte[] bytes = await ReadBoundedAsync(response.Content, 8192, token).ConfigureAwait(false);
        if (!request.Options.TryGetValue(ClassroomSender.LocalEndpointOptionKey, out var ownIp)) throw new AttachmentException("local_endpoint_unknown");
        var result = ClassroomResponseValidator.Validate(ClassroomResponseKind.HelloOk, (int)response.StatusCode, bytes,
            new(ip, ownIp, new(nonce, "", "", null, ""), AttachmentLimits.Capability));
        if (!result.Ok || result.Hello is null) throw new AttachmentException(result.Code == "capability_missing" || response.StatusCode == HttpStatusCode.NotFound || result.Code == "invalid_response" ? "unsupported_version" : result.Code);
        return result.Hello;
    }
    private static HttpRequestMessage MakeRequest(string ip, int port, string path, byte[] bytes)
    {
        if (!LanInvitation.PrivateIPv4(ip) || port is < 1 or > 65535) throw new AttachmentException("invalid_endpoint");
        var request = new HttpRequestMessage(HttpMethod.Post, new UriBuilder("http", ip, port, path).Uri) { Content = new ByteArrayContent(bytes) };
        request.Headers.ConnectionClose = true; request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" }; return request;
    }
    private async Task<HttpResponseMessage> RequestAsync(string ip, int port, string operation, object payload, CancellationToken cancellation)
    {
        using var request = MakeRequest(ip, port, AttachmentEndpoint.Prefix + operation, JsonSerializer.SerializeToUtf8Bytes(payload));
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
    }
    internal static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maximum, CancellationToken cancellation)
    {
        if (content.Headers.ContentEncoding.Count != 0 || content.Headers.ContentLength is not { } length || length < 0 || length > maximum) throw new AttachmentException("invalid_length");
        await using var stream = await content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        byte[] bytes = new byte[checked((int)length)]; await stream.ReadExactlyAsync(bytes, cancellation).ConfigureAwait(false);
        byte[] extra = new byte[1]; if (await stream.ReadAsync(extra, cancellation).ConfigureAwait(false) != 0) throw new AttachmentException("invalid_length");
        return bytes;
    }
    private static async Task<JsonDocument> JsonResponseAsync(HttpResponseMessage response, CancellationToken token)
    {
        byte[] bytes = await ReadBoundedAsync(response.Content, 8192, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new AttachmentException((int)response.StatusCode is 429 or 503 ? "busy" : "unavailable");
        return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
    }
    private static void ValidateAck(JsonElement body, AttachmentRecord row)
    {
        Exact(body, "protocol", "version", "kind", "message_id", "size", "sha256", "status");
        if (Get(body, "protocol") != "nekochat.attachments" || body.GetProperty("version").GetInt32() != 1 || Get(body, "kind") != "ack" ||
            Get(body, "message_id") != row.MessageId || Get(body, "sha256") != row.File.Sha256 || body.GetProperty("size").GetInt64() != row.File.Size) throw new AttachmentException("invalid_response");
    }
    private static void Exact(JsonElement body, params string[] fields)
    {
        var keys = body.EnumerateObject().Select(p => p.Name).ToArray();
        if (keys.Length != fields.Length || keys.Distinct(StringComparer.Ordinal).Count() != fields.Length || keys.Any(k => !fields.Contains(k, StringComparer.Ordinal))) throw new AttachmentException("invalid_payload");
    }
    private static string Get(JsonElement body, string name) => body.GetProperty(name).GetString() ?? throw new AttachmentException("invalid_payload");
    private static ClassroomTransportResponse Ack(string id, AttachmentFile file, string status) => new(200, JsonSerializer.SerializeToUtf8Bytes(new { protocol = "nekochat.attachments", version = 1, kind = "ack", message_id = id, size = file.Size, sha256 = file.Sha256, status }));
    private static ClassroomTransportResponse Failure(string code) => new(code is "unavailable" ? 404 : code is "stale_session" or "conflict" or "cleared" ? 409 : code == "rate_limited" ? 429 : 400,
        JsonSerializer.SerializeToUtf8Bytes(new { protocol = "nekochat.attachments", version = 1, kind = "error", code }));
    private static string ErrorCode(Exception error) => error is AttachmentException a ? a.Code : error is OperationCanceledException ? "cancelled" : "transfer_failed";
    public ValueTask DisposeAsync() { lock (_gate) return new(_disposal ??= StopAsync()); }
    private async Task StopAsync()
    {
        SetReady(false); await _lifetime.CancelAsync().ConfigureAwait(false);
        Task[] tasks; lock (_gate) tasks = _jobs.Values.Select(j => j.Work).ToArray();
        try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch (Exception) { /* outcomes persisted; drain before disposing store */ }
        await _filesGate.WaitAsync().ConfigureAwait(false); _filesGate.Release();
        _http.Dispose(); _download.Dispose(); _serve.Dispose(); _filesGate.Dispose(); _lifetime.Dispose();
    }
}
