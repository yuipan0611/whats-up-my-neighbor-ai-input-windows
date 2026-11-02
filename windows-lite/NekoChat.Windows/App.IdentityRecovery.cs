using System.Windows;
using NekoChat.Core.Identity;
using NekoChat.Core.Storage;

namespace NekoChat.Windows;

public partial class App
{
    private bool _recoveringIdentity, _shutdownAfterIdentityRecovery;
    private CancellationTokenSource? _identityRecoveryCancellation;
    private async Task RecoverIdentityAsync()
    {
        if (_recoveringIdentity || startingData || closingApp || chatRuntime is not { } runtime) return;
        _recoveringIdentity = true;
        using var cancellation = new CancellationTokenSource(); _identityRecoveryCancellation = cancellation;
        bool quiesced = false; string status = "未變更本機身分。";
        chat.SetIdentityRecoveryStatus("正在檢查本機身分…", busy: true);
        try
        {
            var prompt = await runtime.Worker.Run(_ => runtime.IdentityRecovery.Inspect(), cancellation.Token);
            if (cancellation.IsCancellationRequested) return;
            if (prompt is null)
            { status = "目前沒有可復原的遺失身分。未建立或替換金鑰；首次使用仍需選定身分方案。"; return; }
            string problem = prompt.IsMismatch ? "本機身分與金鑰不符" : "本機身分金鑰遺失或無法使用";
            if (MessageBox.Show(chat,
                $"{problem}（原身分 {prompt.PreviousIdPrefix}…）。\n\n重新建立後，你會有新的身分，對方會視為新對象。既有配對會暫停，必須重新核對；對外已讀回條會關閉。\n\n聊天歷史與草稿會保留，舊金鑰檔不會刪除。這不會恢復原身分，也不會自動重送訊息。\n\n要重新建立本機身分嗎？",
                "重新建立本機身分", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            { status = "已取消，原身分與資料保留。"; return; }
            cancellation.Token.ThrowIfCancellationRequested();
            // Stop old wire jobs, preserve every owned draft, then perform exactly one guarded replacement on the storage worker.
            quiesced = true; chat.BeginClosing();
            await runtime.StopReceivingAsync(); await _activeLegacySend; await chat.PrepareForShutdownAsync();
            cancellation.Token.ThrowIfCancellationRequested();
            var result = await runtime.Worker.Run(_ => runtime.IdentityRecovery.Confirm(prompt, () => !cancellation.IsCancellationRequested), CancellationToken.None);
            status = result is IdentityReplaced
                ? "已建立新的本機身分；歷史與草稿保留，既有配對已暫停。私訊仍需完成身分與訊息上限設定。"
                : "身分或確認條件已改變，這次未重建。請重新檢查；資料與金鑰檔保留。";
        }
        catch (OperationCanceledException) { status = "已取消，未繼續重建身分。請檢查目前身分狀態。"; }
        catch
        { status = "無法完成檢查、存稿或身分重建；請保留目前文字並處理本機資料問題後重試。舊金鑰檔保留。"; }
        finally
        {
            runtime.IdentityRecovery.Cancel();
            if (quiesced)
            {
                try { await runtime.DisposeAsync(); }
                catch { status += " 資料關閉未完成，請查看接收與儲存提示。"; }
                chatRuntime = null; directListener = null; chat.DetachRuntime(); chat.AbortClosing();
                if (!closingApp && (!_shutdownAfterIdentityRecovery || chat.HasUnsavedDrafts)) await StartDataAsync();
            }
            _identityRecoveryCancellation = null; _recoveringIdentity = false;
            chat.SetIdentityRecoveryStatus(status, busy: false); chat.SetStatus(status);
            if (_shutdownAfterIdentityRecovery)
            { _shutdownAfterIdentityRecovery = false; await RequestShutdownAsync(); }
        }
    }
}
