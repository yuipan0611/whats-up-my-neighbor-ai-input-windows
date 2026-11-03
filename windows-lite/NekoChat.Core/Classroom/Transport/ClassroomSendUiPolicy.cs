using System;
using NekoChat.Core.Storage;

namespace NekoChat.Core.Classroom.Transport;

public enum AttemptActionKind
{
    None,
    MarkFailed
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

        return new ClassroomSendDecision(
            AttemptActionKind.None,
            null,
            "傳送中斷，結果未確認；不會自動重送，要再送請按重送。",
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

        string defaultText = outcome.Code switch
        {
            "classroom-may-have-delivered" => "先前嘗試可能已送達教室，不降級為舊版重送；請確認後再試。",
            "payload_too_large" => "訊息超過對方可接受的上限，請修改後手動傳送。",
            "storage_unavailable" => "本機無法儲存確認；對方可能已收到，請檢查後手動重送。",
            _ => "尚未確認送達；不會自動重送。要再送請按重送。"
        };

        return new ClassroomSendDecision(AttemptActionKind.None, null, defaultText, false);
    }

    public static CancelResult DecideCancelResultFromOutcome(ClassroomSendResult outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (outcome.Completion is Confirmed)
        {
            return new CancelResult(false, "terminal");
        }

        if (outcome.Completion is StateUpdated { NewState: "unconfirmed" })
        {
            return new CancelResult(false, "unconfirmed");
        }

        return new CancelResult(true, "cancelled");
    }

    public static string DecideCancelClickResult(CancelResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Cancelled) return "已取消，訊息未送出";
        if (result.Reason == "unconfirmed") return "已取消，但對方可能已收到；不會自動重送";
        if (result.Reason == "terminal") return "這次傳送可能已確認或被取代；取消不能撤回，請查看對話狀態。";
        return "當次嘗試已改變，未取消新的嘗試。請重新讀取。";
    }
}
