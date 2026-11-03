using System.Text.Json;
using NekoChat.Core;
using NekoChat.Core.Classroom.Transport;
using NekoChat.Core.Classroom.Wire;

namespace NekoChat.Tests;

/// <summary>
/// Independent checks for the receiving side of the classroom endpoint (NCM3 review B1, B2), written from NCMW-v2 §6/§7/§10:
///   B1  A stored ACK is only ever the result of a commit. With no sink, or a sink that returns null, the adapter must refuse (503), never ACK.
///   B2  A storage failure after the one-shot session was consumed answers 503 storage_unavailable (contract §7: 儲存失敗) instead of
///       throwing out of the endpoint (the listener would then close the socket with no response) and never answers stored.
/// Note: an earlier review comment of mine said `transient` for B2. The contract says `storage_unavailable`; `transient` is receipt-route only (§10). These checks follow the contract.
/// Public API only: ClassroomEndpointAdapter, IClassroomTransportSink, ClassroomStoreSink, ClassroomPacketWriter. Collects all failures, throws once.
/// </summary>
internal static class ClassroomAdapterIndependentChecks
{
    private const string Source = "10.0.0.2", Local = "10.0.0.1";

    private sealed class Sink(Func<ClassroomRequest, Task<ClassroomTransportResponse?>> onMessage) : IClassroomTransportSink
    {
        public Task<ClassroomTransportResponse?> OnMessageAsync(string sourceIp, string localIp, ClassroomRequest request, Func<bool> stillCurrent, CancellationToken cancellation) => onMessage(request);
        public Task<ClassroomTransportResponse?> OnReceiptAsync(string sourceIp, string localIp, ClassroomRequest request, Func<bool> stillCurrent, CancellationToken cancellation) => Task.FromResult<ClassroomTransportResponse?>(null);
        public Task<ClassroomTransportResponse?> OnLegacyDirectAsync(string sourceIp, DirectMessage message, Func<bool> stillCurrent, CancellationToken cancellation) => Task.FromResult<ClassroomTransportResponse?>(null);
    }

    private sealed record Answer(ClassroomTransportResponse? Response, string? Thrown)
    {
        internal int Status => Response?.StatusCode ?? -1;
        internal string? Kind => Field("kind"); internal string? Code => Field("code"); internal string? AckStatus => Field("status");
        internal bool IsStoredAck => Status == 200 && Kind == "message.ack" && AckStatus is "stored" or "duplicate";
        private string? Field(string name)
        {
            if (Response is null) return null;
            try { using var doc = JsonDocument.Parse(Response.Body); return doc.RootElement.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null; }
            catch (JsonException) { return null; }
        }
        public override string ToString() => Thrown ?? $"status={Status} kind={Kind} code={Code} ack={AckStatus}";
    }

    /// <summary>One valid hello, then one valid message with the issued session.</summary>
    private static async Task<Answer> Message(ClassroomEndpointAdapter adapter, string text = "adapter-marker-0042")
    {
        var connection = new ClassroomConnectionContext(Source, Local);
        var hello = await adapter.HandleAsync(new("classroom.hello", ClassroomPacketWriter.HelloRequest(ClassroomPacketWriter.NewToken())), connection, () => true, CancellationToken.None);
        string session;
        using (var doc = JsonDocument.Parse(hello.Body)) session = doc.RootElement.GetProperty("session").GetString()!;
        try
        {
            var response = await adapter.HandleAsync(new("classroom.message", ClassroomPacketWriter.MessageSend(ClassroomPacketWriter.NewMessageId(), session, Local, text)), connection, () => true, CancellationToken.None);
            return new Answer(response, null);
        }
        catch (Exception e) { return new Answer(null, e.GetType().Name + ": " + e.Message); }
    }

    private static ClassroomEndpointAdapter Ready(IClassroomTransportSink? sink) { var adapter = new ClassroomEndpointAdapter(sink); adapter.SetReady(true); return adapter; }

    internal static async Task<int> RunAsync()
    {
        int checks = 0; var failures = new List<string>();
        void Check(bool ok, string label, string detail = "") { checks++; if (!ok) failures.Add(label + (detail.Length > 0 ? " — " + detail : "")); }
        bool Refused503(Answer a) => a.Thrown is null && a.Status == 503 && a.Kind == "error" && a.Code is ClassroomCodes.ReceiverNotReady or ClassroomCodes.StorageUnavailable;

        // ---- Controls: the harness and the happy paths work. ----
        {
            var a = await Message(Ready(new Sink(r => Task.FromResult<ClassroomTransportResponse?>(new(200, ClassroomPacketWriter.MessageAck("stored", r.MessageId!, r.Session!, Source, Local, r.Reaction, r.Text!))))));
            Check(a.IsStoredAck, "C1 a sink that persisted and answered stored passes through", a.ToString());
        }
        {
            string dir = Loopback.TempDir("classroom-adapter-indep-");
            try
            {
                using var store = Loopback.OpenStore(dir);
                var a = await Message(Ready(new ClassroomStoreSink(store)));
                Check(a.IsStoredAck && a.AckStatus == "stored", "C2 the real store sink answers stored for a new message", a.ToString());
            }
            finally { Loopback.Cleanup(dir); }
        }

        // ---- B1: nothing persisted means nothing may be acknowledged as stored. ----
        {
            var a = await Message(Ready(null));
            Check(!a.IsStoredAck, "B1 no sink: never answers stored", a.ToString());
            Check(Refused503(a), "B1 no sink: refuses with 503 receiver_not_ready", a.ToString());
        }
        {
            var a = await Message(Ready(new Sink(_ => Task.FromResult<ClassroomTransportResponse?>(null))));
            Check(!a.IsStoredAck, "B1 sink returns null: never answers stored", a.ToString());
            Check(Refused503(a), "B1 sink returns null: refuses with 503", a.ToString());
        }

        // ---- B2: a store that fails answers 503 storage_unavailable, never throws out of the endpoint, never says stored. ----
        var failing = new (string Name, Func<Exception> Make)[]
        {
            ("IOException (disk full / file locked)", () => new IOException("disk full")),
            ("InvalidOperationException (store closed)", () => new InvalidOperationException("store closed")),
            ("TimeoutException (database busy)", () => new TimeoutException("busy")),
        };
        foreach (var (name, make) in failing)
        {
            var viaSink = await Message(Ready(new Sink(_ => throw make())));
            Check(viaSink.Thrown is null, $"B2 sink throws {name}: the exception does not escape the endpoint", viaSink.ToString());
            Check(!viaSink.IsStoredAck && viaSink.Status == 503 && viaSink.Code == ClassroomCodes.StorageUnavailable, $"B2 sink throws {name}: answers 503 storage_unavailable", viaSink.ToString());

            var viaStore = await Message(Ready(new ClassroomStoreSink(FailingStore.Down(make))));
            Check(viaStore.Thrown is null, $"B2 real store sink over a store that throws {name}: the exception does not escape the endpoint", viaStore.ToString());
            Check(!viaStore.IsStoredAck && viaStore.Status == 503 && viaStore.Code == ClassroomCodes.StorageUnavailable, $"B2 real store sink over a store that throws {name}: answers 503 storage_unavailable", viaStore.ToString());
        }

        foreach (string failure in failures) Console.WriteLine("FAIL: " + failure);
        Console.WriteLine($"Classroom adapter independent checks: {checks - failures.Count}/{checks} passed, {failures.Count} failed.");
        if (failures.Count > 0) throw new InvalidOperationException($"{failures.Count} of {checks} independent adapter checks failed.");
        return checks;
    }
}
