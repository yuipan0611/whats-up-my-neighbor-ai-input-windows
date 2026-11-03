namespace NekoChat.Core.Pairing;

/// <summary>Product copy/state projection only; never supplies consent or claims a remote database result.</summary>
public sealed record PairingDialogPresentation(string Status, string Detail, string Number, string Countdown, bool CanStart, bool CanCompare, bool Finished)
{
    public static PairingDialogPresentation From(PairingView view, bool active, bool busy)
    {
        bool compare = active && !busy && view.Stage == PairingStage.SasShown && view.Consent is not null;
        bool start = active && !busy && view.Stage is PairingStage.Closed or PairingStage.Ended && !view.MayBeAsymmetric && view.Reason is not ("trust_changed" or "stale_roster" or "scope_ended" or "stale_security");
        string number = view.Stage is PairingStage.SasShown or PairingStage.Waiting && view.Sas is { Length: 6 } ? view.Sas.Insert(3, " ") : "— — —   — — —";
        string countdown = view.Remaining > TimeSpan.Zero && view.Stage is PairingStage.Listening or PairingStage.Requested or PairingStage.Committed or PairingStage.SasShown or PairingStage.Waiting ? $"剩餘 {Math.Max(0, (int)Math.Ceiling(view.Remaining.TotalSeconds))} 秒" : "";
        (string title, string detail) = view.Stage switch {
            PairingStage.Paired => ("本機已完成配對與綁定", "請查看對方畫面是否也已完成；兩台的儲存結果並非同時提交。"),
            PairingStage.SasShown => ("請與對方面對面核對六位數字", "姓名、IP 或對方已按確認都不能代替核對。只有兩邊數字完全相同時才繼續。"),
            PairingStage.Waiting => ("等候配對結果", "你已確認。到期或取消後，兩台可能狀態不同；本機取消不能撤回對方已儲存的結果。"),
            PairingStage.Listening => ("等待對方發起核對", "請對方選擇你的 IP 並發起配對。視窗到期不會自動重新開始。"),
            PairingStage.Requested or PairingStage.Committed => ("正在交換核對資料…", "尚未建立信任，請等候六位核對碼。"),
            _ when view.MayBeAsymmetric => ("結果未確認，兩台可能狀態不同", "關閉後查看本機名單與對方畫面，再明確重新核對；不會自動重送或恢復配對。"),
            _ when view.Reason == "user_rejected" => ("本輪已中止", "這一輪不再接受確認；既有信任不會因中止而撤銷。請先確認對方與網路，再明確重新開始。"),
            _ when view.Reason == "commitment_mismatch" => ("配對已中止，可能有網路干擾", "核對資料不一致。請先確認對方與網路，再關閉並重新開始。"),
            _ when view.Reason is "trust_changed" or "stale_roster" or "stale_security" or "scope_ended" => ("名單、身分或信任已變更", "這一輪不能繼續。請關閉並重新選取名單，再核對新的數字。"),
            _ when view.Reason == "timeout" => ("核對已到期", "沒有自動恢復或重試；請明確重新開始。"),
            _ when !active => ("配對視窗已失效", "請關閉並從最新名單重新開啟。"),
            _ => ("尚未開始核對", "選擇發起核對，或等待對方發起。配對不會自動取得老師權限。")
        };
        return new(title, detail, number, countdown, start, compare, view.Stage == PairingStage.Paired);
    }
}
