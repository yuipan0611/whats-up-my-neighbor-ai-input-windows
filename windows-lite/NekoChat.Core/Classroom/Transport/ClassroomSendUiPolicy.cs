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

    public static ClassroomSendDecision DecideOutcome(ClassroomSendResult outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        bool draftCleared = outcome.Completion is Confirmed { DraftCleared: true };
        if (outcome.Code == "ok")
        {
            string text = outcome.Route == ClassroomRoute.Classroom ? "已驗證對方的儲存確認。" : "已送達（舊版同學）。";
            return new ClassroomSendDecision(AttemptActionKind.None, null, text, draftCleared);
        }

        if (outcome.Code == "cancelled")
        {
            string text = outcome.Completion is null
                ? "已取消，訊息未送出"
                : "已取消，但對方可能已收到；不會自動重送";
            return new ClassroomSendDecision(AttemptActionKind.None, null, text, false);
        }

        // CM-07: legacy_payload_too_large 顯示 MessageLimitPolicy.LegacyTooLargeNotice 且保留草稿
        if (outcome.Code == "legacy_payload_too_large")
        {
            return new ClassroomSendDecision(
                AttemptActionKind.None,
                null,
                $"{MessageLimitPolicy.LegacyTooLargeNotice}。訊息未送出，草稿保留。",
                false);
        }

        if (outcome.Code == "classroom-may-have-delivered")
        {
            return new ClassroomSendDecision(AttemptActionKind.None, null, "先前嘗試可能已送達教室，不降級為舊版重送；請確認後再試。", false);
        }

        if (outcome.Code == "payload_too_large")
        {
            return new ClassroomSendDecision(AttemptActionKind.None, null, "訊息超過對方可接受的上限，請修改後手動傳送。", false);
        }

        if (outcome.Code == "storage_unavailable")
        {
            return new ClassroomSendDecision(AttemptActionKind.None, null, "本機無法儲存確認；對方可能已收到，請檢查後手動重送。", false);
        }

        // hello 階段失敗（http_503, rate_limited, capability_missing, hello_failed, connect_refused 等）
        // 顯示「未送出；草稿仍在本機」並附帶原因代碼，不能說「尚未確認送達」
        if (IsHelloFailure(outcome.Code))
        {
            return new ClassroomSendDecision(
                AttemptActionKind.None,
                null,
                $"未送出（{outcome.Code}）；草稿仍在本機。",
                false);
        }

        return new ClassroomSendDecision(AttemptActionKind.None, null, "尚未確認送達；不會自動重送。要再送請按重送。", false);
    }

    public static bool IsHelloFailure(string code) =>
        code is "http_503"
            or "rate_limited"
            or "capability_missing"
            or "hello_failed"
            or "connect_refused"
            or "connect_timeout"
            or "invalid_response"
            or "ack_mismatch"
            or "local_endpoint_unknown"
            or "peer_unreachable"
            || code.StartsWith("hello_", StringComparison.Ordinal);

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
            "confirmed" or "already_confirmed" or "terminal" => "已送達（對方已確認儲存）。",
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
