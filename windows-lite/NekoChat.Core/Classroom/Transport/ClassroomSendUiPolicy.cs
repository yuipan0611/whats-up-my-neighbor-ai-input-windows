using System;
using System.Threading;
using NekoChat.Core.Storage;
using NekoChat.Core.V2;

namespace NekoChat.Core.Classroom.Transport;

public enum AttemptActionKind
{
    None,
    MarkFailed,
    MarkUnconfirmed
}

public sealed record ClassroomSendDecision(
    AttemptActionKind Action,
    string? FailedReason,
    string StatusText,
    bool DraftCleared);

/// <summary>
/// Pure functions mapping send outcome / exceptions / cancel outcomes to UI decisions and attempt dispositions.
/// Isolated in Core for deterministic non-WPF testing.
/// </summary>
public static class ClassroomSendUiPolicy
{
    public const string CancelInitiatedStatusText = "正在取消…";

    public static ClassroomSendDecision DecideException(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);

        // 重複登記：進行中的嘗試維持進行中，嚴禁將其狀態改成 unconfirmed
        if (ex is ClassroomSendAlreadyRunningException)
        {
            return new ClassroomSendDecision(
                AttemptActionKind.None,
                null,
                "該次傳送嘗試已在進行中，請等待完成。",
                false);
        }

        if (ex is LocalEndpointResolutionException)
        {
            return new ClassroomSendDecision(
                AttemptActionKind.MarkFailed,
                "local_endpoint_unknown",
                "傳送失敗（本機端點未知）；草稿仍在本機，未傳送。",
                false);
        }

        // PM 裁定：泛用例外記 UnconfirmedOutcome（讓 attempt 進入 unconfirmed 終態，按重送時不會撞 AlreadySending）
        // UI 顯示「結果未確認，可以按重送；重送時會警告可能已送達」
        return new ClassroomSendDecision(
            AttemptActionKind.MarkUnconfirmed,
            null,
            "結果未確認，可以按重送；重送時會警告可能已送達。",
            false);
    }

    /// <summary>
    /// PM 裁定：主依據為 attempt 終態（Completion：StateUpdated failed/unconfirmed/cancelled、Confirmed、AlreadyConfirmed、AckMismatch、Discarded）
    /// 決定是「未送出」「可能已收到」「已取消，未送出」還是「已送達」；Code 與 RemoteCode 僅用來補充原因文字。
    /// 絕不使用白名單判斷 hello 失敗，避免把 message 階段失敗誤報為「未送出」。
    /// </summary>
    public static ClassroomSendDecision DecideOutcome(ClassroomSendResult outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        // 1. 成功確認送達（Confirmed、AlreadyConfirmed 或 outcome.Success）
        if (outcome.Success || outcome.Completion is Confirmed or AlreadyConfirmed)
        {
            bool draftCleared = outcome.Completion is Confirmed { DraftCleared: true };
            string text = outcome.Route == ClassroomRoute.Classroom ? "已送出。" : "已送達（舊版同學）。";
            return new ClassroomSendDecision(AttemptActionKind.None, null, text, draftCleared);
        }

        // 2. 取消（cancelled-before-write，位元組寫出前取消）
        if (outcome.Completion is StateUpdated { NewState: "cancelled" } || (outcome.Code == "cancelled" && outcome.Completion is null))
        {
            return new ClassroomSendDecision(AttemptActionKind.None, null, "已取消，訊息未送出", false);
        }

        // UI-S（Tom 紅 1、2）：Completion 為 Discarded（例如 stillCurrent 在 hello 前後變成 false）
        // 此時尚未寫出任何訊息位元組，對端不可能收到，絕不能說「尚未確認送達」或「可能已收到」
        if (outcome.Completion is Discarded)
        {
            return new ClassroomSendDecision(
                AttemptActionKind.None,
                null,
                "這次嘗試已中斷，訊息未送出；請重新讀取後再送。",
                false);
        }

        // 原因文字萃取：優先使用對端回傳的 RemoteCode，若無則使用 Code
        string? reasonCode = !string.IsNullOrWhiteSpace(outcome.RemoteCode) ? outcome.RemoteCode : outcome.Code;

        // 3. 終態為 failed（明確未送出 / 對端確定未儲存）
        // 規則：絕不能出現「尚未確認」「可能已送達」「可能已收到」，必須明確說「未送出」
        if (outcome.Completion is StateUpdated { NewState: "failed" })
        {
            // CM-07 legacy 超限文字特殊文案
            if (outcome.Code == "legacy_payload_too_large" || reasonCode == "payload_too_large" && outcome.Route == ClassroomRoute.Legacy)
            {
                return new ClassroomSendDecision(
                    AttemptActionKind.None,
                    null,
                    $"{MessageLimitPolicy.LegacyTooLargeNotice}。訊息未送出，草稿保留。",
                    false);
            }

            if (outcome.Code == "payload_too_large" || reasonCode == "payload_too_large")
            {
                return new ClassroomSendDecision(
                    AttemptActionKind.None,
                    null,
                    "訊息超過對方可接受的上限，未送出；請修改後手動傳送。",
                    false);
            }

            string text = !string.IsNullOrWhiteSpace(reasonCode) && reasonCode != "failed"
                ? $"未送出（{reasonCode}）；草稿仍在本機。"
                : "未送出；草稿仍在本機。";

            return new ClassroomSendDecision(AttemptActionKind.None, null, text, false);
        }

        // 4. 終態為 unconfirmed（位元組已寫出或 transient，對方可能已收到；或者 AckMismatch 等）
        // 規則：絕不能出現「未送出」「未傳送」，必須包含「尚未確認」或「可能已收到」或「可能已送達」
        if (outcome.Code == "cancelled")
        {
            return new ClassroomSendDecision(AttemptActionKind.None, null, "已取消，但對方可能已收到；不會自動重送。", false);
        }

        // 對方的儲存失敗（storage_unavailable），文案不得怪罪「本機」
        if (outcome.Code == "storage_unavailable" || reasonCode == "storage_unavailable")
        {
            return new ClassroomSendDecision(
                AttemptActionKind.None,
                null,
                "對方未能完成接收回應，尚未確認送達；對方可能已收到，請檢查後手動重送。",
                false);
        }

        if (outcome.Code == "classroom-may-have-delivered")
        {
            return new ClassroomSendDecision(
                AttemptActionKind.None,
                null,
                "先前嘗試可能已送達教室，不降級為舊版重送；請確認後再試。",
                false);
        }

        string unconfirmedText = !string.IsNullOrWhiteSpace(reasonCode) && reasonCode != "unconfirmed"
            ? $"尚未確認送達（{reasonCode}）；對方可能已收到，不會自動重送。要再送請按重送。"
            : "尚未確認送達；對方可能已收到，不會自動重送。要再送請按重送。";

        return new ClassroomSendDecision(AttemptActionKind.None, null, unconfirmedText, false);
    }

    /// <summary>
    /// 對應 ChatWindow.Inbox.cs:243 橫幅狀態文字
    /// PM 裁定：取消中的橫幅不做任何送達判斷，顯示「已要求取消，等待結果…」
    /// </summary>
    public static string DecideAttemptBanner(string? state, bool cancelling)
    {
        if (cancelling) return "已要求取消，等待結果…";
        return state switch
        {
            "queued" => "等待傳送；連線恢復後會自動續送。",
            "sending" => "手動傳送中，等待接收回應。",
            "cancelled" => "當次嘗試已取消，訊息未送出；草稿仍在本機。",
            "failed" => "上一次嘗試未送出；草稿仍在本機，可以重新手動傳送。",
            "unconfirmed" => "上一次嘗試尚未確認送達（對方可能已收到，也可能沒收到）；不會自動重送，要再送請按重送。",
            _ => "尚未確認送達；不會自動重送。要再送請按重送。"
        };
    }

    public static CancelResult DecideCancelResultFromOutcome(ClassroomSendResult outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (outcome.Completion is Confirmed) return new CancelResult(false, "confirmed");
        if (outcome.Completion is AlreadyConfirmed) return new CancelResult(false, "already_confirmed");
        if (outcome.Completion is AckMismatch) return new CancelResult(false, "ack_mismatch");
        if (outcome.Completion is Discarded) return new CancelResult(false, "discarded");
        if (outcome.Completion is StateUpdated { NewState: "unconfirmed" }) return new CancelResult(false, "unconfirmed");
        if (outcome.Completion is StateUpdated { NewState: "failed" }) return new CancelResult(false, "failed");
        if (outcome.Completion is StateUpdated { NewState: "cancelled" }) return new CancelResult(true, "cancelled");
        if (outcome.Code == "cancelled" && outcome.Completion is null) return new CancelResult(true, "cancelled");

        return new CancelResult(false, "unconfirmed");
    }

    public static CancelResult DecideCancelResultFromException(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return new CancelResult(false, "exception");
    }

    public static string DecideCancelClickResult(CancelResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Cancelled && result.Reason == "cancelled")
        {
            return "已取消，訊息未送出";
        }

        return result.Reason switch
        {
            "confirmed" or "already_confirmed" or "terminal" => "已送出（無法再取消）。",
            "unconfirmed" or "ack_mismatch" => "已取消，但對方可能已收到；不會自動重送。",
            "discarded" or "stale" => "當次嘗試已改變，未取消新的嘗試。請重新讀取。",
            "failed" => "傳送失敗，未送出；草稿仍在本機。",
            "exception" => "結果未確認，可以按重送；重送時會警告可能已送達。",
            _ => "結果未確認，可以按重送；重送時會警告可能已送達。"
        };
    }

    public static bool SafeCancel(CancellationTokenSource cts)
    {
        ArgumentNullException.ThrowIfNull(cts);
        try
        {
            cts.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }
}
