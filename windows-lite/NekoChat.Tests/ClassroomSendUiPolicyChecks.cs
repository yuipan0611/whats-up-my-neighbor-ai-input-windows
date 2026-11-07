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
        // (a) D-30 Confirmed -> terminal/confirmed -> "已送出"
        var rConfirmed = new ClassroomSendResult(true, "ok", new Confirmed(1, true), ClassroomRoute.Classroom);
        var cConfirmed = ClassroomSendUiPolicy.DecideCancelResultFromOutcome(rConfirmed);
        C(!cConfirmed.Cancelled && cConfirmed.Reason == "confirmed", "2a. Confirmed maps to confirmed");
        C(ClassroomSendUiPolicy.DecideCancelClickResult(cConfirmed) == "已送出（無法再取消）。", "2a. D-30 Confirmed cancellation text means sent, not stored");

        // (b) D-30 AlreadyConfirmed -> terminal/already_confirmed -> "已送出"
        var rAlreadyConfirmed = new ClassroomSendResult(true, "ok", new AlreadyConfirmed(1), ClassroomRoute.Classroom);
        var cAlreadyConfirmed = ClassroomSendUiPolicy.DecideCancelResultFromOutcome(rAlreadyConfirmed);
        C(!cAlreadyConfirmed.Cancelled && cAlreadyConfirmed.Reason == "already_confirmed", "2b. AlreadyConfirmed maps to already_confirmed");
        C(ClassroomSendUiPolicy.DecideCancelClickResult(cAlreadyConfirmed) == "已送出（無法再取消）。", "2b. D-30 AlreadyConfirmed cancellation text means sent, not stored");

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
        // 項目 7（Ben 必修 1 & 2）：ClassroomSendRegistry.Start 測試保護與順序驗證
        // (a) 迴圈 2000 次，work 第一行立即斷言看得到自己的登記（攔截 M-A 晚登記）
        // (b) 重複 Start 拋出例外，TryGet 仍是原本那組 cts/task（攔截 M-B 覆蓋）
        // (c) 第二個 work 從未被呼叫（攔截 M-C 執行第二個 work）
        // (d) 第一筆完成後正常清除登記（攔截殘留）
        // =========================================================================
        var registry = new ClassroomSendRegistry();
        using var linkedCts = new CancellationTokenSource();
        var completedResult = new ClassroomSendResult(true, "ok", new Confirmed(1, true), ClassroomRoute.Classroom);

        // 7a. 迴圈 2000 次：work 的第一行立即斷言看得到自己的登記
        int visibleCount = 0;
        for (int i = 0; i < 2000; i++)
        {
            string loopId = $"loop-msg-{i}";
            var t = registry.Start(loopId, 1, ct =>
            {
                if (registry.TryGet(loopId, 1).HasValue)
                {
                    Interlocked.Increment(ref visibleCount);
                }
                return Task.FromResult(completedResult);
            }, linkedCts.Token);
            t.GetAwaiter().GetResult();
        }
        C(visibleCount == 2000, "7a. Work in first line sees its own registration across 2000 iterations (M-A loop guard)", $"visibleCount={visibleCount}/2000");

        // 7b. 重複 Start 防護（攔截 M-B 覆蓋與 M-C 執行第二個 work）：
        var tcsHold = new TaskCompletionSource<ClassroomSendResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        string dupMsgId = "msg-dup-check";
        int dupTry = 1;
        var firstTask = registry.Start(dupMsgId, dupTry, async ct => await tcsHold.Task, linkedCts.Token);

        var firstRegistration = registry.TryGet(dupMsgId, dupTry);
        C(firstRegistration.HasValue, "7b. First send is registered");

        bool secondWorkCalled = false;
        bool threwDupOnSecond = false;
        try
        {
            registry.Start(dupMsgId, dupTry, ct =>
            {
                secondWorkCalled = true;
                return Task.FromResult(completedResult);
            }, linkedCts.Token);
        }
        catch (ClassroomSendAlreadyRunningException ex)
        {
            threwDupOnSecond = ex.MessageId == dupMsgId && ex.TryCount == dupTry;
        }

        // (b) 重複 Start 之後，TryGet 仍是原本那組 cts／task
        var postDupRegistration = registry.TryGet(dupMsgId, dupTry);
        C(threwDupOnSecond, "7b. Repeated Start throws ClassroomSendAlreadyRunningException");
        C(postDupRegistration.HasValue &&
          postDupRegistration.Value.Cts == firstRegistration?.Cts &&
          postDupRegistration.Value.Task == firstRegistration?.Task,
          "7b. Repeated Start preserves original cts and task (intercepts M-B overwrite)");

        // (c) 第二個 work 從未被呼叫（用旗標）
        C(!secondWorkCalled, "7c. Second work is never invoked (intercepts M-C invoke second work)");

        // (d) 第一筆完成後會正常清除
        tcsHold.SetResult(completedResult);
        var firstOutcome = firstTask.GetAwaiter().GetResult();
        C(firstOutcome.Success, "7d. First send completes successfully");
        C(!registry.TryGet(dupMsgId, dupTry).HasValue, "7d. Registration cleanly removed after completion");

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
        // 項目 9（Tom 審查保護 3 條實質直接斷言）：
        // (a) 任何 unconfirmed 結果（AckMismatch、transient、503 qualified transient 等）的文案都要含「尚未確認」和「可能已收到」，而且不得含「未送出」
        // (b) storage_unavailable 的文案（不論出現在 Code 或 RemoteCode）不得含「本機」
        // (c) DecideAttemptBanner("sending", false) 不得含任何送達判斷（不含「尚未確認」「可能已送達」「可能已收到」「未送出」）
        // =========================================================================
        ClassroomSendResult[] unconfirmedResults =
        [
            new ClassroomSendResult(false, "ack_mismatch", new AckMismatch(), ClassroomRoute.Classroom),
            new ClassroomSendResult(false, "transient", new StateUpdated("unconfirmed"), ClassroomRoute.Classroom),
            new ClassroomSendResult(false, "storage_unavailable", new StateUpdated("unconfirmed"), ClassroomRoute.Classroom),
            new ClassroomSendResult(false, "remote_error", new StateUpdated("unconfirmed"), ClassroomRoute.Classroom, RemoteCode: "storage_unavailable"),
            new ClassroomSendResult(false, "timeout", new StateUpdated("unconfirmed"), ClassroomRoute.Classroom),
            new ClassroomSendResult(false, "invalid_response", new StateUpdated("unconfirmed"), ClassroomRoute.Classroom),
        ];

        foreach (var r in unconfirmedResults)
        {
            var d = ClassroomSendUiPolicy.DecideOutcome(r);
            C(d.StatusText.Contains("尚未確認"), $"9. Tom (a) unconfirmed result ({r.Code}/{r.RemoteCode}) must contain '尚未確認'", d.StatusText);
            C(d.StatusText.Contains("可能已收到"), $"9. Tom (a) unconfirmed result ({r.Code}/{r.RemoteCode}) must contain '可能已收到'", d.StatusText);
            C(!d.StatusText.Contains("未送出"), $"9. Tom (a) unconfirmed result ({r.Code}/{r.RemoteCode}) must not contain '未送出'", d.StatusText);
        }

        // (b) storage_unavailable 的文案（不論出現在 Code 或 RemoteCode）不得含「本機」
        ClassroomSendResult[] storageUnavailableResults =
        [
            new ClassroomSendResult(false, "storage_unavailable", new StateUpdated("unconfirmed"), ClassroomRoute.Classroom),
            new ClassroomSendResult(false, "remote_error", new StateUpdated("unconfirmed"), ClassroomRoute.Classroom, RemoteCode: "storage_unavailable"),
            new ClassroomSendResult(false, "storage_unavailable", null, ClassroomRoute.Classroom),
            new ClassroomSendResult(false, "remote_error", null, ClassroomRoute.Classroom, RemoteCode: "storage_unavailable")
        ];

        foreach (var r in storageUnavailableResults)
        {
            var d = ClassroomSendUiPolicy.DecideOutcome(r);
            C(!d.StatusText.Contains("本機"), $"9. Tom (b) storage_unavailable ({r.Code}/{r.RemoteCode}) must not blame '本機'", d.StatusText);
        }

        // (c) DecideAttemptBanner("sending", false) 不得含任何送達判斷
        string sendingBanner = ClassroomSendUiPolicy.DecideAttemptBanner("sending", false);
        C(!sendingBanner.Contains("尚未確認"), "9. Tom (c) sending banner must not contain '尚未確認'", sendingBanner);
        C(!sendingBanner.Contains("可能已送達"), "9. Tom (c) sending banner must not contain '可能已送達'", sendingBanner);
        C(!sendingBanner.Contains("可能已收到"), "9. Tom (c) sending banner must not contain '可能已收到'", sendingBanner);
        C(!sendingBanner.Contains("未送出"), "9. Tom (c) sending banner must not contain '未送出'", sendingBanner);
        C(sendingBanner.Contains("手動傳送中"), "9. Tom (c) sending banner contains '手動傳送中'", sendingBanner);

        // =========================================================================
        // 項目 10（Ben 複審必修）：補回 8 條精確文案與 DraftCleared 旗標斷言
        // (a) Confirmed＋Classroom＝「已送出。」，Confirmed＋Legacy＝「已送達（舊版同學）。」，驗 DraftCleared
        // (b) UI-S Discarded 的精確文案，驗 DraftCleared=false
        // (c) failed 帶 reasonCode、不帶 reasonCode 的精確格式，驗 DraftCleared=false
        // (d) cancel-before、cancel-after 的精確文案，驗 DraftCleared=false
        // =========================================================================
        // (a) Confirmed 文案與 DraftCleared 旗標
        var dConfClassroom = ClassroomSendUiPolicy.DecideOutcome(new ClassroomSendResult(true, "ok", new Confirmed(1, true), ClassroomRoute.Classroom));
        C(dConfClassroom.StatusText == "已送出。", "10a. Confirmed classroom route exact status text match");
        C(dConfClassroom.DraftCleared, "10a. Confirmed classroom route with DraftCleared=true reports true");

        var dConfLegacy = ClassroomSendUiPolicy.DecideOutcome(new ClassroomSendResult(true, "ok", new Confirmed(1, false), ClassroomRoute.Legacy));
        C(dConfLegacy.StatusText == "已送達（舊版同學）。", "10a. Confirmed legacy route exact status text match");
        C(!dConfLegacy.DraftCleared, "10a. Confirmed legacy route with DraftCleared=false reports false");

        // (b) UI-S Discarded 精確文案與草稿保留
        var dDiscarded = ClassroomSendUiPolicy.DecideOutcome(new ClassroomSendResult(false, "stale_attempt", new Discarded("stale"), ClassroomRoute.Classroom));
        C(dDiscarded.StatusText == "這次嘗試已中斷，訊息未送出；請重新讀取後再送。", "10b. UI-S Discarded exact status text match");
        C(!dDiscarded.DraftCleared, "10b. UI-S Discarded preserves draft");

        // (c) Failed 帶與不帶 reasonCode 之精確格式
        var dFailedCode = ClassroomSendUiPolicy.DecideOutcome(new ClassroomSendResult(false, "network_error", new StateUpdated("failed"), ClassroomRoute.Classroom));
        C(dFailedCode.StatusText == "未送出（network_error）；草稿仍在本機。", "10c. Failed with reasonCode exact text match");
        C(!dFailedCode.DraftCleared, "10c. Failed with reasonCode preserves draft");

        var dFailedNoCode = ClassroomSendUiPolicy.DecideOutcome(new ClassroomSendResult(false, "failed", new StateUpdated("failed"), ClassroomRoute.Classroom));
        C(dFailedNoCode.StatusText == "未送出；草稿仍在本機。", "10c. Failed without specific code exact text match");
        C(!dFailedNoCode.DraftCleared, "10c. Failed without specific code preserves draft");

        // (d) cancel-before 與 cancel-after 精確文案
        var dCancelBefore = ClassroomSendUiPolicy.DecideOutcome(new ClassroomSendResult(false, "cancelled", new StateUpdated("cancelled"), ClassroomRoute.Classroom));
        C(dCancelBefore.StatusText == "已取消，訊息未送出", "10d. Cancel-before-write (StateUpdated cancelled) exact status text match");
        C(!dCancelBefore.DraftCleared, "10d. Cancel-before-write (StateUpdated cancelled) preserves draft");

        var dCancelAfter = ClassroomSendUiPolicy.DecideOutcome(new ClassroomSendResult(false, "cancelled", new StateUpdated("unconfirmed"), ClassroomRoute.Classroom));
        C(dCancelAfter.StatusText == "已取消，但對方可能已收到；不會自動重送。", "10d. Cancel-after-write exact status text match");
        C(!dCancelAfter.DraftCleared, "10d. Cancel-after-write preserves draft");

        // (e) Code=="cancelled" 而且 Completion==null（hello 階段取消，Tom C5）
        var dHelloCancel = ClassroomSendUiPolicy.DecideOutcome(new ClassroomSendResult(false, "cancelled", null, ClassroomRoute.Classroom));
        C(dHelloCancel.StatusText == "已取消，訊息未送出", "10e. Hello-stage cancel (Code==cancelled, Completion==null) exact status text match");
        C(!dHelloCancel.DraftCleared, "10e. Hello-stage cancel (Code==cancelled, Completion==null) preserves draft");

        if (failures.Count > 0)
        {
            foreach (var f in failures) Console.Error.WriteLine($"FAIL: {f}");
            throw new InvalidOperationException($"ClassroomSendUiPolicyChecks: {failures.Count} of {checks} checks failed.");
        }

        Console.WriteLine($"Classroom send UI policy checks: {checks} checks passed.");
    }
}
