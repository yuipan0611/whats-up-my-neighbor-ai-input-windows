using System.Net;
using System.Text;
using System.Text.Json;
using NekoChat.Core.Classroom.Transport;
using NekoChat.Core.Classroom.Wire;

namespace NekoChat.Tests;

internal static class ClassroomEndpointAdapterChecks
{
    internal static async Task<int> Run()
    {
        int checks = 0;
        void Check(bool value, string label) { checks++; if (!value) throw new Exception("Classroom adapter: " + label); }

        var adapter = new ClassroomEndpointAdapter();
        adapter.SetReady(true);

        // 1. ValidateHead: signed.v2.* paths return unsupported_version
        byte[] Head(string path, long length = 100) =>
            Encoding.ASCII.GetBytes($"POST {path} HTTP/1.1\r\nHost: 10.0.0.1\r\nContent-Type: application/json\r\nContent-Length: {length}\r\n\r\n");

        Check(adapter.ValidateHead(Head("/nekochat/v2/hello")).Code == ClassroomCodes.UnsupportedVersion, "v2 hello returns unsupported_version");
        Check(adapter.ValidateHead(Head("/nekochat/v2/message")).Code == ClassroomCodes.UnsupportedVersion, "v2 message returns unsupported_version");
        Check(adapter.ValidateHead(Head("/nekochat/v2/receipt")).Code == ClassroomCodes.UnsupportedVersion, "v2 receipt returns unsupported_version");
        Check(adapter.ValidateHead(Head("/nekochat/v2/pair")).Code == ClassroomCodes.UnsupportedVersion, "v2 pair returns unsupported_version");

        // 2. ValidateHead: legacy.v1 enforces its own 65,536 bound
        var legOk = adapter.ValidateHead(Head("/nekochat/v1/direct", 65536));
        Check(legOk.Code == "ok" && legOk.Head is { Route: "legacy.v1", ContentLength: 65536, MaximumBodyBytes: 65536 }, "legacy.v1 at exactly 65536 is accepted with 65536 max");
        var legTooLarge = adapter.ValidateHead(Head("/nekochat/v1/direct", 65537));
        Check(legTooLarge.Code == ClassroomCodes.PayloadTooLarge, "legacy.v1 at 65537 is payload_too_large");
        var legHuge = adapter.ValidateHead(Head("/nekochat/v1/direct", 9999999999));
        Check(legHuge.Code == ClassroomCodes.PayloadTooLarge, "legacy.v1 above int.MaxValue is payload_too_large");

        // 3. ValidateHead: classroom routes bounds
        var helloOk = adapter.ValidateHead(Head("/nekochat/classroom/v1/hello", 8192));
        Check(helloOk.Code == "ok" && helloOk.Head is { Route: "classroom.hello", MaximumBodyBytes: 8192 }, "classroom hello accepts 8192 max");
        Check(adapter.ValidateHead(Head("/nekochat/classroom/v1/hello", 8193)).Code == ClassroomCodes.PayloadTooLarge, "classroom hello rejects 8193");

        var msgOk = adapter.ValidateHead(Head("/nekochat/classroom/v1/message", ClassroomWire.MessageBodyMaxBytes));
        Check(msgOk.Code == "ok" && msgOk.Head is { Route: "classroom.message", MaximumBodyBytes: ClassroomWire.MessageBodyMaxBytes }, "classroom message accepts MessageBodyMaxBytes");
        Check(adapter.ValidateHead(Head("/nekochat/classroom/v1/message", ClassroomWire.MessageBodyMaxBytes + 1)).Code == ClassroomCodes.PayloadTooLarge, "classroom message rejects above MessageBodyMaxBytes");

        var rcptOk = adapter.ValidateHead(Head("/nekochat/classroom/v1/receipt", 8192));
        Check(rcptOk.Code == "ok" && rcptOk.Head is { Route: "classroom.receipt", MaximumBodyBytes: 8192 }, "classroom receipt accepts 8192 max");
        Check(adapter.ValidateHead(Head("/nekochat/classroom/v1/receipt", 8193)).Code == ClassroomCodes.PayloadTooLarge, "classroom receipt rejects 8193");

        // 4. SetReady and session clearance
        string source = "10.0.0.2", local = "10.0.0.1";
        string s1 = adapter.SessionTable.Issue(source);
        Check(adapter.SessionTable.Count == 1, "session table holds 1 session");
        adapter.SetReady(false);
        Check(adapter.SessionTable.Count == 0, "SetReady(false) clears RAM sessions");
        Check(!adapter.Ready, "Ready is false");

        var ctx = new ClassroomConnectionContext(source, local);
        var notReadyResp = await adapter.HandleAsync(new("classroom.hello", "{\"protocol\":\"nekochat-classroom\",\"version\":1,\"kind\":\"hello\",\"client_nonce\":\"1234567890123456789012\"}"u8.ToArray()), ctx, () => true, default);
        Check(notReadyResp.StatusCode == 503, "HandleAsync while not ready returns 503");

        adapter.SetReady(true);

        // 5. Source IP restrictions (non-private or invalid IP is rejected)
        var publicCtx = new ClassroomConnectionContext("8.8.8.8", local);
        var publicResp = await adapter.HandleAsync(new("classroom.hello", "{\"protocol\":\"nekochat-classroom\",\"version\":1,\"kind\":\"hello\",\"client_nonce\":\"1234567890123456789012\"}"u8.ToArray()), publicCtx, () => true, default);
        Check(publicResp.StatusCode == 403, "Public IP source returns 403");

        // 6. Full flow: Hello -> Message -> Duplicate -> Replay Protection (Atomic Consume)
        string nonce = "AAAAAAAAAAAAAAAAAAAAAA";
        byte[] helloReq = ClassroomPacketWriter.HelloRequest(nonce);
        var helloResp = await adapter.HandleAsync(new("classroom.hello", helloReq), ctx, () => true, default);
        Check(helloResp.StatusCode == 200, "Hello returns 200 OK");
        using var helloDoc = JsonDocument.Parse(helloResp.Body);
        string session = helloDoc.RootElement.GetProperty("session").GetString()!;
        Check(helloDoc.RootElement.GetProperty("client_nonce").GetString() == nonce, "Hello echos client nonce");

        // Send message with issued session
        string msgId = Guid.NewGuid().ToString("D");
        byte[] msgReq = ClassroomPacketWriter.MessageSend(msgId, session, local, "Hello classroom teacher!");
        var msgResp = await adapter.HandleAsync(new("classroom.message", msgReq), ctx, () => true, default);
        Check(msgResp.StatusCode == 503, "Message without sink returns 503 receiver_not_ready");
        using var msgDoc = JsonDocument.Parse(msgResp.Body);
        Check(msgDoc.RootElement.GetProperty("code").GetString() == ClassroomCodes.ReceiverNotReady, "Message error is receiver_not_ready");

        // Replay with consumed session -> stale_session (409)
        var replayResp = await adapter.HandleAsync(new("classroom.message", msgReq), ctx, () => true, default);
        Check(replayResp.StatusCode == 409, "Consumed session on replay returns 409 stale_session");

        // 7. Legacy v1 handling
        byte[] legacyBody = Encoding.UTF8.GetBytes("{\"text\":\"hello legacy peer\"}");
        var legacyResp = await adapter.HandleAsync(new("legacy.v1", legacyBody), ctx, () => true, default);
        Check(legacyResp.StatusCode == 503, "Legacy v1 without sink returns 503 receiver_not_ready");
        using var legDoc = JsonDocument.Parse(legacyResp.Body);
        Check(legDoc.RootElement.GetProperty("code").GetString() == ClassroomCodes.ReceiverNotReady, "Legacy error is receiver_not_ready");

        Console.WriteLine($"Classroom endpoint adapter: {checks} head routing, bounds, readiness, session lifecycle and replay checks passed.");
        return checks;
    }
}
