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
        // 項目 7（Ben 必修修法 B）：ClassroomSendRegistry.Start 原子登記佔位與清理驗證
        // =========================================================================
        var registry = new ClassroomSendRegistry();
        using var linkedCts = new CancellationTokenSource();
        string startMsgId = "msg-start-sync-1";
        int startTry = 1;

        // 7a. 同步立即完成的 work：登記一定會被建立，並在完成後被移除
        var completedResult = new ClassroomSendResult(true, "ok", new Confirmed(1, true), ClassroomRoute.Classroom);
        var syncTask = registry.Start(startMsgId, startTry, ct => Task.FromResult(completedResult), linkedCts.Token);
        var syncOutcome = syncTask.GetAwaiter().GetResult();
        C(syncOutcome.Success, "7a. Sync task completes successfully");
        C(!registry.TryGet(startMsgId, startTry).HasValue, "7a. Synchronously completed work has its registration cleanly removed (TryGet is null)");

        // 7b. 進行中時 TryGet 能取得，重複 Start 同 key 拋出 ClassroomSendAlreadyRunningException
        var tcsHold = new TaskCompletionSource<ClassroomSendResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        string asyncMsgId = "msg-start-async-2";
        int asyncTry = 1;
        var asyncTask = registry.Start(asyncMsgId, asyncTry, async ct => await tcsHold.Task, linkedCts.Token);

        var registered = registry.TryGet(asyncMsgId, asyncTry);
        C(registered.HasValue, "7b. In-flight send is visible in TryGet");
        C(registry.CanCancel(asyncMsgId, asyncTry), "7b. In-flight send can be cancelled");

        bool threwDupOnStart = false;
        try
        {
            registry.Start(asyncMsgId, asyncTry, ct => Task.FromResult(completedResult), linkedCts.Token);
        }
        catch (ClassroomSendAlreadyRunningException ex)
        {
            threwDupOnStart = ex.MessageId == asyncMsgId && ex.TryCount == asyncTry;
        }
        C(threwDupOnStart, "7b. Parallel Start on same key throws ClassroomSendAlreadyRunningException");

        // 釋放 hold 任務並確認完成後 TryGet 為空
        tcsHold.SetResult(completedResult);
        var asyncOutcome = asyncTask.GetAwaiter().GetResult();
        C(asyncOutcome.Success, "7b. Async task completes successfully");
        C(!registry.TryGet(asyncMsgId, asyncTry).HasValue, "7b. After completion, TryGet is empty");

        // =========================================================================
        // 項目 8（必修 2 & 3）：DecideAttemptBanner(state, cancelling) 橫幅文字測試
        // PM 裁定：cancelling:true 為「已要求取消，等待結果…」不做任何送達判斷
        // =========================================================================
        string bannerCancelling = ClassroomSendUiPolicy.DecideAttemptBanner("sending", true);
        C(bannerCancelling == "已要求取消，等待結果…", "8. Banner cancelling is exactly 已要求取消，等待結果…");
        C(!bannerCancelling.Contains("尚未確認") && !bannerCancelling.Contains("可能已收到") && !bannerCancelling.Contains("可能已送達"), "8. Banner cancelling makes no delivery claim");

        C(ClassroomSendUiPolicy.DecideAttemptBanner("sending", false).Contains("手動傳送中"), "8. Banner sending says 手動傳送中");
        var bannerCancelled = ClassroomSendUiPolicy.DecideAttemptBanner("cancelled", false);
        C(bannerCancelled.Contains("未送出") && !bannerCancelled.Contains("可能已收到"), "8. Banner cancelled says 未送出 and never says 可能已收到");
        var bannerFailed = ClassroomSendUiPolicy.DecideAttemptBanner("failed", false);
        C(bannerFailed.Contains("未送出") && !bannerFailed.Contains("尚未確認"), "8. Banner failed says 未送出 and never says 尚未確認");
        C(ClassroomSendUiPolicy.DecideAttemptBanner("unconfirmed", false).Contains("尚未確認送達"), "8. Banner unconfirmed says 尚未確認送達");

        // =========================================================================
        // 項目 9（Tom 審查保護 C2, C3, C5, C9 直接斷言）：
        // =========================================================================
        // C2: Confirmed classroom vs legacy 精確文案
        var dConfClassroom = ClassroomSendUiPolicy.DecideOutcome(new ClassroomSendResult(true, "ok", new Confirmed(1, true), ClassroomRoute.Classroom));
        C(dConfClassroom.StatusText == "已驗證對方的儲存確認。", "9. C2 classroom confirmed exact status text match");
        var dConfLegacy = ClassroomSendUiPolicy.DecideOutcome(new ClassroomSendResult(true, "ok", new Confirmed(1, true), ClassroomRoute.Legacy));
        C(dConfLegacy.StatusText == "已送達（舊版同學）。", "9. C2 legacy confirmed exact status text match");

        // C3: cancelled-before-write vs message-stage cancel 精確文案
        var dCancelBefore = ClassroomSendUiPolicy.DecideOutcome(new ClassroomSendResult(false, "cancelled", null, ClassroomRoute.Classroom));
        C(dCancelBefore.StatusText == "已取消，訊息未送出", "9. C3 cancel before write exact status text match");
        var dCancelAfter = ClassroomSendUiPolicy.DecideOutcome(new ClassroomSendResult(false, "cancelled", new StateUpdated("unconfirmed"), ClassroomRoute.Classroom));
        C(dCancelAfter.StatusText == "已取消，但對方可能已收到；不會自動重送。", "9. C3 cancel after write exact status text match");

        // C5: failed 帶 reasonCode 格式
        var dFailedWithCode = ClassroomSendUiPolicy.DecideOutcome(new ClassroomSendResult(false, "network_error", new StateUpdated("failed"), ClassroomRoute.Classroom));
        C(dFailedWithCode.StatusText == "未送出（network_error）；草稿仍在本機。", "9. C5 failed with reason code exact format match");
        var dFailedWithoutCode = ClassroomSendUiPolicy.DecideOutcome(new ClassroomSendResult(false, "failed", new StateUpdated("failed"), ClassroomRoute.Classroom));
        C(dFailedWithoutCode.StatusText == "未送出；草稿仍在本機。", "9. C5 failed without code exact format match");

        // C9: UI-S Discarded 中斷未送出文案
        var dDiscardedStale = ClassroomSendUiPolicy.DecideOutcome(new ClassroomSendResult(false, "stale_attempt", new Discarded("stale"), ClassroomRoute.Classroom));
        C(dDiscardedStale.StatusText == "這次嘗試已中斷，訊息未送出；請重新讀取後再送。", "9. C9 UI-S Discarded exact text match");
        C(!dDiscardedStale.DraftCleared, "9. C9 UI-S Discarded draft is preserved");

        if (failures.Count > 0)
        {
            foreach (var f in failures) Console.Error.WriteLine($"FAIL: {f}");
            throw new InvalidOperationException($"ClassroomSendUiPolicyChecks: {failures.Count} of {checks} checks failed.");
        }

        Console.WriteLine($"Classroom send UI policy checks: {checks} checks passed.");
    }
}
