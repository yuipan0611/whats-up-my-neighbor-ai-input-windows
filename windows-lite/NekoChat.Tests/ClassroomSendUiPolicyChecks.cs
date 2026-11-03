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
    public static void Run()
    {
        int checks = 0;
        var failures = new List<string>();
        void C(bool cond, string name, string detail = "")
        {
            checks++;
            if (!cond) failures.Add(name + (detail.Length > 0 ? " — " + detail : ""));
        }

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

        // 對照組（Ben/Tom 必修）：ClassroomSendAlreadyRunningException 回傳 AttemptActionKind.None，不得把進行中那次改成 unconfirmed
        var exRunning = new ClassroomSendAlreadyRunningException("msg-run-1", 1);
        var d1Running = ClassroomSendUiPolicy.DecideException(exRunning);
        C(d1Running.Action == AttemptActionKind.None, "1. ClassroomSendAlreadyRunningException returns Action None (leaves running attempt untouched)");

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
        var rLegacyTooLarge = new ClassroomSendResult(false, "legacy_payload_too_large", new StateUpdated("failed"), ClassroomRoute.Legacy);
        var d3 = ClassroomSendUiPolicy.DecideOutcome(rLegacyTooLarge);
        C(!d3.DraftCleared, "3. legacy_payload_too_large preserves draft");
        C(d3.StatusText.Contains(MessageLimitPolicy.LegacyTooLargeNotice), "3. legacy_payload_too_large contains LegacyTooLargeNotice");
        C(d3.StatusText.Contains("對方是舊版，最多 2,000 字"), "3. legacy_payload_too_large notice string match");
        C(d3.StatusText.Contains("未送出"), "3. legacy_payload_too_large contains 未送出");
        C(!d3.StatusText.Contains("尚未確認"), "3. legacy_payload_too_large never says 尚未確認");

        // =========================================================================
        // 項目 4（Tom 4 & 5）：hello / failed 階段真實代碼（network_error, timeout, http_404...）顯示「未送出」附原因代碼
        // =========================================================================
        string[] realFailureCodes = ["http_503", "rate_limited", "capability_missing", "hello_failed", "network_error", "timeout", "http_404", "receiver_not_ready"];
        foreach (var code in realFailureCodes)
        {
            var r = new ClassroomSendResult(false, code, new StateUpdated("failed"), ClassroomRoute.Classroom);
            var d = ClassroomSendUiPolicy.DecideOutcome(r);
            C(!d.DraftCleared, $"4. {code} preserves draft");
            C(d.StatusText.Contains("未送出") && d.StatusText.Contains("草稿仍在本機"), $"4. {code} contains 未送出 and 草稿仍在本機");
            C(d.StatusText.Contains(code), $"4. {code} includes error code");
            C(!d.StatusText.Contains("尚未確認送達"), $"4. {code} does not say 尚未確認送達");
            C(!d.StatusText.Contains("可能已送達"), $"4. {code} does not say 可能已送達");
        }

        // =========================================================================
        // 項目 5（Tom 5）：CancelAttemptClick 按下當下顯示中性的「正在取消…」
        // =========================================================================
        C(ClassroomSendUiPolicy.CancelInitiatedStatusText == "正在取消…", "5. CancelInitiatedStatusText is 正在取消…");

        // =========================================================================
        // 項目 6（Ben 1 & b）：SafeCancel 於 disposed CTS 不拋例外；標註真機 await 範圍
        // =========================================================================
        var cts = new CancellationTokenSource();
        cts.Dispose();
        bool safeCancelResult = ClassroomSendUiPolicy.SafeCancel(cts);
        C(!safeCancelResult, "6. SafeCancel on disposed CTS returns false without throwing");

        // 政策驗證（依指示註記：ChatRuntime await 的實際執行，留到 Windows 驗）
        var cancelPolicyCheck = ClassroomSendUiPolicy.DecideCancelResultFromOutcome(rConfirmed);
        C(!cancelPolicyCheck.Cancelled && cancelPolicyCheck.Reason == "confirmed", "6. UI policy maps confirmed outcome (ChatRuntime await 的實際執行，留到 Windows 驗)");

        // =========================================================================
        // 項目 7（Ben a & Tom 3）：直接呼叫 Core 產品型別 ClassroomSendRegistry 與負控測試
        // =========================================================================
        var registry = new ClassroomSendRegistry();
        var dummyCts = new CancellationTokenSource();
        var dummyTask = Task.FromResult(rConfirmed);
        string testMsgId = "msg-prod-reg-1";
        int testTry = 1;

        // 正向測試：Register 與 TryGet
        registry.Register(testMsgId, testTry, dummyCts, dummyTask);
        var retrieved = registry.TryGet(testMsgId, testTry);
        C(retrieved.HasValue && retrieved.Value.Cts == dummyCts && retrieved.Value.Task == dummyTask, "7. ClassroomSendRegistry product code registers and retrieves successfully");

        // 負控測試 1：CheckNotRunning 遇到已登記 key 拋出專用 ClassroomSendAlreadyRunningException
        bool threwOnCheck = false;
        try
        {
            registry.CheckNotRunning(testMsgId, testTry);
        }
        catch (ClassroomSendAlreadyRunningException ex)
        {
            threwOnCheck = ex.MessageId == testMsgId && ex.TryCount == testTry;
        }
        C(threwOnCheck, "7. ClassroomSendRegistry.CheckNotRunning throws ClassroomSendAlreadyRunningException on duplicate key");

        // 負控測試 2：Register 遇到已登記 key 拋出專用例外，且原有登記完好保留
        bool threwOnRegister = false;
        var secondCts = new CancellationTokenSource();
        var secondTask = Task.FromResult(rFailed);
        try
        {
            registry.Register(testMsgId, testTry, secondCts, secondTask);
        }
        catch (ClassroomSendAlreadyRunningException ex)
        {
            threwOnRegister = ex.MessageId == testMsgId && ex.TryCount == testTry;
        }
        C(threwOnRegister, "7. ClassroomSendRegistry.Register throws ClassroomSendAlreadyRunningException on duplicate key");

        var preserved = registry.TryGet(testMsgId, testTry);
        C(preserved.HasValue && preserved.Value.Cts == dummyCts && preserved.Value.Task == dummyTask, "7. ClassroomSendRegistry preserves existing registration on duplicate rejection");

        // 反註冊測試
        C(registry.TryRemove(testMsgId, testTry), "7. ClassroomSendRegistry removes registration");
        C(!registry.TryGet(testMsgId, testTry).HasValue, "7. ClassroomSendRegistry slot is empty after remove");

        // =========================================================================
        // 項目 8（必修 2）：DecideAttemptBanner(state, cancelling) 橫幅文字測試
        // =========================================================================
        C(ClassroomSendUiPolicy.DecideAttemptBanner("sending", true).Contains("已要求取消，等待當次工作結束"), "8. Banner cancelling says waiting for finish");
        C(ClassroomSendUiPolicy.DecideAttemptBanner("sending", false).Contains("手動傳送中"), "8. Banner sending says 手動傳送中");
        var bannerCancelled = ClassroomSendUiPolicy.DecideAttemptBanner("cancelled", false);
        C(bannerCancelled.Contains("未送出") && !bannerCancelled.Contains("可能已收到"), "8. Banner cancelled says 未送出 and never says 可能已收到");
        var bannerFailed = ClassroomSendUiPolicy.DecideAttemptBanner("failed", false);
        C(bannerFailed.Contains("未送出") && !bannerFailed.Contains("尚未確認"), "8. Banner failed says 未送出 and never says 尚未確認");
        C(ClassroomSendUiPolicy.DecideAttemptBanner("unconfirmed", false).Contains("尚未確認送達"), "8. Banner unconfirmed says 尚未確認送達");

        if (failures.Count > 0)
        {
            foreach (var f in failures) Console.Error.WriteLine($"FAIL: {f}");
            throw new InvalidOperationException($"ClassroomSendUiPolicyChecks: {failures.Count} of {checks} checks failed.");
        }

        Console.WriteLine($"Classroom send UI policy checks: {checks} checks passed.");
    }
}
