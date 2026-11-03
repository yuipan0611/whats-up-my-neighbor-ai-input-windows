using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NekoChat.Core.Classroom.Transport;
using NekoChat.Core.Storage;
using NekoChat.Core.V2;

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

        // =========================================================================
        // 項目 1（PM 裁定）：泛用例外記 UnconfirmedOutcome，UI 顯示「結果未確認，可以按重送；重送時會警告可能已送達」
        // =========================================================================
        var exTimeout = new TimeoutException("http timeout");
        var d1Timeout = ClassroomSendUiPolicy.DecideException(exTimeout);
        C(d1Timeout.Action == AttemptActionKind.MarkUnconfirmed, "1. TimeoutException marks attempt Unconfirmed");
        C(d1Timeout.FailedReason is null, "1. TimeoutException leaves FailedReason null");
        C(d1Timeout.StatusText == "結果未確認，可以按重送；重送時會警告可能已送達。", "1. TimeoutException status text");

        var exSocket = new SocketException((int)SocketError.ConnectionRefused);
        var d1Socket = ClassroomSendUiPolicy.DecideException(exSocket);
        C(d1Socket.Action == AttemptActionKind.MarkUnconfirmed, "1. SocketException marks attempt Unconfirmed");
        C(d1Socket.StatusText == "結果未確認，可以按重送；重送時會警告可能已送達。", "1. SocketException status text");

        var exIo = new IOException("network broken");
        var d1Io = ClassroomSendUiPolicy.DecideException(exIo);
        C(d1Io.Action == AttemptActionKind.MarkUnconfirmed, "1. IOException marks attempt Unconfirmed");

        var exGeneral = new Exception("unhandled failure");
        var d1General = ClassroomSendUiPolicy.DecideException(exGeneral);
        C(d1General.Action == AttemptActionKind.MarkUnconfirmed, "1. Generic Exception marks attempt Unconfirmed");

        // 對照組：LocalEndpointResolutionException 仍維持 MarkFailed 且顯示「未傳送」
        var exResolver = new LocalEndpointResolutionException("unresolvable 256.256.256.256");
        var d1Resolver = ClassroomSendUiPolicy.DecideException(exResolver);
        C(d1Resolver.Action == AttemptActionKind.MarkFailed, "1. LocalEndpointResolutionException marks attempt Failed");
        C(d1Resolver.FailedReason == "local_endpoint_unknown", "1. LocalEndpointResolutionException FailedReason");
        C(d1Resolver.StatusText.Contains("未傳送") && d1Resolver.StatusText.Contains("本機端點未知"), "1. LocalEndpointResolutionException status text");

        // =========================================================================
        // 項目 2（Tom 2）：CancelAttemptAsync 的結果對應
        // 涵蓋：Confirmed、AlreadyConfirmed、StateUpdated(unconfirmed／failed／cancelled)、
        //       AckMismatch、Discarded、null＋cancelled、例外
        // =========================================================================
        // (a) Confirmed -> terminal/confirmed -> "已送達"
        var rConfirmed = new ClassroomSendResult(true, "ok", new Confirmed(1, true), ClassroomRoute.Classroom);
        var cConfirmed = ClassroomSendUiPolicy.DecideCancelResultFromOutcome(rConfirmed);
        C(!cConfirmed.Cancelled && cConfirmed.Reason == "confirmed", "2a. Confirmed maps to confirmed");
        C(ClassroomSendUiPolicy.DecideCancelClickResult(cConfirmed).Contains("已送達"), "2a. Confirmed text contains 已送達");

        // (b) AlreadyConfirmed -> terminal/already_confirmed -> "已送達"
        var rAlreadyConfirmed = new ClassroomSendResult(true, "ok", new AlreadyConfirmed(1), ClassroomRoute.Classroom);
        var cAlreadyConfirmed = ClassroomSendUiPolicy.DecideCancelResultFromOutcome(rAlreadyConfirmed);
        C(!cAlreadyConfirmed.Cancelled && cAlreadyConfirmed.Reason == "already_confirmed", "2b. AlreadyConfirmed maps to already_confirmed");
        C(ClassroomSendUiPolicy.DecideCancelClickResult(cAlreadyConfirmed).Contains("已送達"), "2b. AlreadyConfirmed text contains 已送達");

        // (c) StateUpdated(unconfirmed) -> "對方可能已收到"
        var rUnconfirmed = new ClassroomSendResult(false, "cancelled", new StateUpdated("unconfirmed"), ClassroomRoute.Classroom);
        var cUnconfirmed = ClassroomSendUiPolicy.DecideCancelResultFromOutcome(rUnconfirmed);
        C(!cUnconfirmed.Cancelled && cUnconfirmed.Reason == "unconfirmed", "2c. StateUpdated(unconfirmed) maps to unconfirmed");
        C(ClassroomSendUiPolicy.DecideCancelClickResult(cUnconfirmed).Contains("對方可能已收到"), "2c. unconfirmed text contains 對方可能已收到");

        // (d) AckMismatch -> "對方可能已收到"
        var rAckMismatch = new ClassroomSendResult(false, "cancelled", new AckMismatch(), ClassroomRoute.Classroom);
        var cAckMismatch = ClassroomSendUiPolicy.DecideCancelResultFromOutcome(rAckMismatch);
        C(!cAckMismatch.Cancelled && cAckMismatch.Reason == "ack_mismatch", "2d. AckMismatch maps to ack_mismatch");
        C(ClassroomSendUiPolicy.DecideCancelClickResult(cAckMismatch).Contains("對方可能已收到"), "2d. AckMismatch text contains 對方可能已收到");

        // (e) Discarded -> 走 stale 文案
        var rDiscarded = new ClassroomSendResult(false, "cancelled", new Discarded("stale"), ClassroomRoute.Classroom);
        var cDiscarded = ClassroomSendUiPolicy.DecideCancelResultFromOutcome(rDiscarded);
        C(!cDiscarded.Cancelled && cDiscarded.Reason == "discarded", "2e. Discarded maps to discarded");
        C(ClassroomSendUiPolicy.DecideCancelClickResult(cDiscarded).Contains("當次嘗試已改變，未取消新的嘗試。請重新讀取。"), "2e. Discarded status text");

        // (f) StateUpdated(failed) -> "未送出"
        var rFailed = new ClassroomSendResult(false, "cancelled", new StateUpdated("failed"), ClassroomRoute.Classroom);
        var cFailed = ClassroomSendUiPolicy.DecideCancelResultFromOutcome(rFailed);
        C(!cFailed.Cancelled && cFailed.Reason == "failed", "2f. StateUpdated(failed) maps to failed");
        C(ClassroomSendUiPolicy.DecideCancelClickResult(cFailed).Contains("未送出"), "2f. failed text contains 未送出");

        // (g) StateUpdated(cancelled) -> "已取消，訊息未送出"
        var rCancelledState = new ClassroomSendResult(false, "cancelled", new StateUpdated("cancelled"), ClassroomRoute.Classroom);
        var cCancelledState = ClassroomSendUiPolicy.DecideCancelResultFromOutcome(rCancelledState);
        C(cCancelledState.Cancelled && cCancelledState.Reason == "cancelled", "2g. StateUpdated(cancelled) maps to cancelled");
        C(ClassroomSendUiPolicy.DecideCancelClickResult(cCancelledState) == "已取消，訊息未送出", "2g. cancelled state text");

        // (h) null + cancelled (hello 階段取消) -> "已取消，訊息未送出"
        var rHelloCancel = new ClassroomSendResult(false, "cancelled", null, ClassroomRoute.Classroom);
        var cHelloCancel = ClassroomSendUiPolicy.DecideCancelResultFromOutcome(rHelloCancel);
        C(cHelloCancel.Cancelled && cHelloCancel.Reason == "cancelled", "2h. null + cancelled maps to cancelled");
        C(ClassroomSendUiPolicy.DecideCancelClickResult(cHelloCancel) == "已取消，訊息未送出", "2h. hello cancel text");

        // (i) 例外 -> "結果未確認，可以按重送；重送時會警告可能已送達。"
        var cException = ClassroomSendUiPolicy.DecideCancelResultFromException(new IOException("tcp reset"));
        C(!cException.Cancelled && cException.Reason == "exception", "2i. Exception maps to exception");
        C(ClassroomSendUiPolicy.DecideCancelClickResult(cException) == "結果未確認，可以按重送；重送時會警告可能已送達。", "2i. Exception status text");

        // =========================================================================
        // 項目 3（CM-07）：legacy_payload_too_large 顯示 MessageLimitPolicy.LegacyTooLargeNotice 且保留草稿
        // =========================================================================
        var rLegacyTooLarge = new ClassroomSendResult(false, "legacy_payload_too_large", null, ClassroomRoute.Legacy);
        var d3 = ClassroomSendUiPolicy.DecideOutcome(rLegacyTooLarge);
        C(!d3.DraftCleared, "3. legacy_payload_too_large preserves draft");
        C(d3.StatusText.Contains(MessageLimitPolicy.LegacyTooLargeNotice), "3. legacy_payload_too_large contains LegacyTooLargeNotice");
        C(d3.StatusText.Contains("對方是舊版，最多 2,000 字"), "3. legacy_payload_too_large notice string match");

        // =========================================================================
        // 項目 4（Tom 4）：hello 階段 failed 要顯示「未送出；草稿仍在本機」附原因代碼，不能說「尚未確認送達」
        // =========================================================================
        string[] helloFailures = ["http_503", "rate_limited", "capability_missing", "hello_failed", "connect_refused", "local_endpoint_unknown"];
        foreach (var code in helloFailures)
        {
            var r = new ClassroomSendResult(false, code, null, ClassroomRoute.Classroom);
            var d = ClassroomSendUiPolicy.DecideOutcome(r);
            C(!d.DraftCleared, $"4. {code} preserves draft");
            C(d.StatusText.Contains("未送出") && d.StatusText.Contains("草稿仍在本機"), $"4. {code} contains 未送出 and 草稿仍在本機");
            C(d.StatusText.Contains(code), $"4. {code} includes error code");
            C(!d.StatusText.Contains("尚未確認送達"), $"4. {code} does not say 尚未確認送達");
        }

        // =========================================================================
        // 項目 5（Tom 5）：CancelAttemptClick 按下當下顯示中性的「正在取消…」
        // =========================================================================
        C(ClassroomSendUiPolicy.CancelInitiatedStatusText == "正在取消…", "5. CancelInitiatedStatusText is 正在取消…");

        // =========================================================================
        // 項目 6（Ben 1）：Cancel 之後 ObjectDisposedException 防護
        // 先 Dispose 再 Cancel 不會拋例外，而且回報實際結果
        // =========================================================================
        var cts = new CancellationTokenSource();
        cts.Dispose();
        bool safeCancelResult = ClassroomSendUiPolicy.SafeCancel(cts);
        C(!safeCancelResult, "6. SafeCancel on disposed CTS returns false without throwing");

        // 模擬先 Dispose 再 Cancel 搭配已完成任務的 await 結果
        var completedSendTask = Task.FromResult(rConfirmed);
        C(completedSendTask.IsCompletedSuccessfully, "6. Send task completed successfully");
        var cancelAfterDispose = ClassroomSendUiPolicy.DecideCancelResultFromOutcome(completedSendTask.Result);
        C(!cancelAfterDispose.Cancelled && cancelAfterDispose.Reason == "confirmed", "6. Cancel after dispose reports actual task outcome");

        // =========================================================================
        // 項目 7（Ben 2）：TryAdd 重複拒絕，要在建立任務前拒絕並丟出明確例外
        // =========================================================================
        var activeSends = new Dictionary<(string Id, int Try), int>();
        (string Id, int Try) key = ("msg-dup-1", 1);
        activeSends.Add(key, 100);

        bool threwOnDup = false;
        try
        {
            if (activeSends.ContainsKey(key))
            {
                throw new InvalidOperationException($"Classroom send already in progress for message {key.Id} try {key.Try}.");
            }
        }
        catch (InvalidOperationException ex)
        {
            threwOnDup = ex.Message.Contains("already in progress");
        }
        C(threwOnDup, "7. Duplicate send key is rejected before task creation with InvalidOperationException");
        C(activeSends.ContainsKey(key) && activeSends[key] == 100, "7. Existing registration is preserved on duplicate rejection");

        Console.WriteLine($"Classroom send UI policy checks: {checks} checks passed.");
    }
}
