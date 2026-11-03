using System;
using System.IO;
using System.Net.Sockets;
using NekoChat.Core.Classroom.Transport;
using NekoChat.Core.Storage;

namespace NekoChat.Tests;

public static class ClassroomSendUiPolicyChecks
{
    private static void Check(bool cond, string name)
    {
        if (!cond) throw new InvalidOperationException($"Check failed: {name}");
    }

    public static void Run()
    {
        int checks = 0;
        void C(bool cond, string name) { checks++; Check(cond, name); }

        // 1. LocalEndpointResolutionException -> MarkFailed, local_endpoint_unknown, displays "未傳送"
        var exResolver = new LocalEndpointResolutionException("unresolvable target ip 256.256.256.256");
        var d1 = ClassroomSendUiPolicy.DecideException(exResolver);
        C(d1.Action == AttemptActionKind.MarkFailed, "LocalEndpointResolutionException marks attempt Failed");
        C(d1.FailedReason == "local_endpoint_unknown", "Failed reason is local_endpoint_unknown");
        C(d1.StatusText.Contains("未傳送") && d1.StatusText.Contains("本機端點未知"), "Status displays 未傳送 and 本機端點未知");
        C(!d1.DraftCleared, "Draft is not cleared on endpoint resolution failure");

        // 2. Generic exceptions -> Action None (leaves attempt untouched), displays "結果未確認"
        var exTimeout = new TimeoutException("http timeout");
        var d2a = ClassroomSendUiPolicy.DecideException(exTimeout);
        C(d2a.Action == AttemptActionKind.None, "TimeoutException leaves attempt untouched (Action.None)");
        C(d2a.FailedReason is null, "No failed reason recorded for generic exception");
        C(d2a.StatusText.Contains("結果未確認") && d2a.StatusText.Contains("傳送中斷"), "Status displays 傳送中斷，結果未確認");

        var exSocket = new SocketException((int)SocketError.ConnectionRefused);
        var d2b = ClassroomSendUiPolicy.DecideException(exSocket);
        C(d2b.Action == AttemptActionKind.None, "SocketException leaves attempt untouched");
        C(d2b.StatusText.Contains("結果未確認"), "SocketException displays 結果未確認");

        var exIo = new IOException("network broken");
        var d2c = ClassroomSendUiPolicy.DecideException(exIo);
        C(d2c.Action == AttemptActionKind.None, "IOException leaves attempt untouched");

        // 3. Hello stage cancellation -> Completion == null, displays "已取消，訊息未送出"
        var helloCancelOutcome = new ClassroomSendResult(false, "cancelled", null, ClassroomRoute.Classroom);
        var d3 = ClassroomSendUiPolicy.DecideOutcome(helloCancelOutcome);
        C(d3.Action == AttemptActionKind.None, "Hello cancel Action is None");
        C(d3.StatusText == "已取消，訊息未送出", "Hello cancel displays 已取消，訊息未送出");
        C(!d3.DraftCleared, "Draft not cleared on hello cancellation");

        // 4. Message stage cancellation -> unconfirmed, displays "已取消，但對方可能已收到；不會自動重送"
        var unconfirmedComp = new StateUpdated("unconfirmed");
        var msgCancelOutcome = new ClassroomSendResult(false, "cancelled", unconfirmedComp, ClassroomRoute.Classroom);
        var d4 = ClassroomSendUiPolicy.DecideOutcome(msgCancelOutcome);
        C(d4.Action == AttemptActionKind.None, "Message cancel Action is None");
        C(d4.StatusText == "已取消，但對方可能已收到；不會自動重送", "Message cancel displays 已取消，但對方可能已收到；不會自動重送");
        C(!d4.DraftCleared, "Draft not cleared on message cancellation");

        // 5. Confirmed outcome -> displays verification / delivered, clears draft
        var confComp = new Confirmed(1, true);
        var confOutcome = new ClassroomSendResult(true, "ok", confComp, ClassroomRoute.Classroom);
        var d5 = ClassroomSendUiPolicy.DecideOutcome(confOutcome);
        C(d5.DraftCleared, "Draft cleared on confirmed classroom send");
        C(d5.StatusText.Contains("已驗證對方的儲存確認"), "Classroom confirmed displays 已驗證對方的儲存確認");

        var legacyConfOutcome = new ClassroomSendResult(true, "ok", confComp, ClassroomRoute.Legacy);
        var d5b = ClassroomSendUiPolicy.DecideOutcome(legacyConfOutcome);
        C(d5b.DraftCleared, "Draft cleared on confirmed legacy send");
        C(d5b.StatusText.Contains("已送達（舊版同學）"), "Legacy confirmed displays 已送達（舊版同學）");

        // 6. Tom Requirement B: DecideCancelResultFromOutcome coordinates cancellation result
        var cancelFromConfirmed = ClassroomSendUiPolicy.DecideCancelResultFromOutcome(confOutcome);
        C(!cancelFromConfirmed.Cancelled && cancelFromConfirmed.Reason == "terminal", "Outcome Confirmed maps to CancelResult terminal");

        var cancelFromUnconfirmed = ClassroomSendUiPolicy.DecideCancelResultFromOutcome(msgCancelOutcome);
        C(!cancelFromUnconfirmed.Cancelled && cancelFromUnconfirmed.Reason == "unconfirmed", "Outcome unconfirmed maps to CancelResult unconfirmed");

        var cancelFromHello = ClassroomSendUiPolicy.DecideCancelResultFromOutcome(helloCancelOutcome);
        C(cancelFromHello.Cancelled && cancelFromHello.Reason == "cancelled", "Outcome cancelled (hello stage) maps to CancelResult cancelled");

        // 7. Tom Requirement C: DecideCancelClickResult button click status texts
        C(ClassroomSendUiPolicy.DecideCancelClickResult(cancelFromHello) == "已取消，訊息未送出", "Cancel button displays 已取消，訊息未送出 on cancelled");
        C(ClassroomSendUiPolicy.DecideCancelClickResult(cancelFromUnconfirmed) == "已取消，但對方可能已收到；不會自動重送", "Cancel button displays 已取消，但對方可能已收到；不會自動重送 on unconfirmed");
        C(ClassroomSendUiPolicy.DecideCancelClickResult(cancelFromConfirmed).Contains("這次傳送可能已確認或被取代"), "Cancel button displays terminal text on already confirmed");

        Console.WriteLine($"Classroom send UI policy checks: {checks} checks passed.");
    }
}
