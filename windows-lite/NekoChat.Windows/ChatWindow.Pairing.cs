using System.Windows;
using NekoChat.Core.Pairing;
using NekoChat.Core.Storage;

namespace NekoChat.Windows;

public partial class ChatWindow
{
    private PairingRosterTarget? _pairingSelection;
    private PairingWindow? _pairingWindow;
    private bool _peerAction;
    private void RefreshPairingControls()
    {
        var target = _pairingSelection;
        bool usable = _runtime?.ArchivedIdentityFixture == true && !_closingRequested && !_peerAction && _runtime is not null && target is not null;
        bool missingKey = target?.BoundPeerId is not null && !target.HasBoundSigningKey;
        bool blocked = target?.Pairing is PairingState.Revoked or PairingState.Suspended;
        OpenPairingButton.IsEnabled = usable && !blocked && !missingKey && _runtime?.Pairing is not null && !_runtime.Lifetime.IsCancellationRequested;
        UnlinkPeerButton.IsEnabled = usable && target?.BoundPeerId is not null;
        RevokePeerButton.IsEnabled = usable && target?.Pairing is not null and not PairingState.Revoked;
        ResetPeerButton.IsEnabled = usable && blocked;
        PairingStatus.Text = target is null ? "先選擇已儲存的名單；未儲存的欄位不會帶入配對。" :
            target.DisplayName + "：" + (missingKey ? "舊綁定缺少身分金鑰；請先解除名單綁定，再重新核對。" : blocked ? "這個身分目前不可配對；若要重新核對，請先明確允許。" :
            _runtime?.Pairing is null ? "配對方案尚未設定，核對入口停用。" : "請與對方面對面核對數字，再綁定這筆名單。");
    }
    private async void OpenPairingClick(object sender, RoutedEventArgs e)
    {
        RefreshPairingControls();
        if (!OpenPairingButton.IsEnabled || _pairingSelection is not { } target || _runtime is not { Pairing: not null } runtime || _pairingWindow is not null) return;
        try
        {
            _pairingWindow = new(runtime, target, _peerPort) { Owner = this };
            _pairingWindow.ShowDialog();
            if (ReferenceEquals(_runtime, runtime) && !_closingRequested) { await LoadManualFormAsync(target.RowId); runtime.Presence?.Refresh(); await RefreshInboxAsync(true); }
        }
        catch (Exception ex) { ShowStorageError(ex); }
        finally { _pairingWindow = null; RefreshPairingControls(); }
    }
    private async void UnlinkPeerClick(object sender, RoutedEventArgs e) => await ChangePeerAsync(null);
    private async void RevokePeerClick(object sender, RoutedEventArgs e) => await ChangePeerAsync(PairingState.Revoked);
    private async void ResetPeerClick(object sender, RoutedEventArgs e) => await ChangePeerAsync(PairingState.Unpaired);
    private async Task ChangePeerAsync(PairingState? next)
    {
        if (_peerAction || _closingRequested || _runtime is not { ArchivedIdentityFixture: true } runtime || _pairingSelection is not { BoundPeerId: not null } target) return;
        string title = next is null ? "解除名單綁定" : next == PairingState.Revoked ? "撤銷身分信任" : "允許重新核對";
        string effect = next is null ? "只解除這筆名單與身分的連結。對話與這個身分的信任仍保留；不會撤回任何訊息。" :
            next == PairingState.Revoked ? "停止信任這個身分，影響所有綁定同一身分的名單。移除尚未送出的已讀回條；已送出的資料無法撤回。" :
            "將這個身分設為未配對，移除尚未送出的已讀回條；之後仍需重新面對面核對，才會建立信任。";
        long generation = _manualFormGeneration;
        if (MessageBox.Show(this, target.DisplayName + "\n身分：" + target.BoundPeerId + "\n\n" + effect + "\n\n是否繼續？", title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        _peerAction = true; RefreshPairingControls();
        try
        {
            await runtime.InvalidatePairingAsync("trust_changed");
            var result = await runtime.Worker.Run(s => s.ChangeSelectedPeerIfCurrent(target, next, () =>
                ReferenceEquals(Volatile.Read(ref _runtime), runtime) && !Volatile.Read(ref _closingRequested) && generation == Interlocked.Read(ref _manualFormGeneration)));
            if (!ReferenceEquals(_runtime, runtime) || _closingRequested) return;
            if (result.Reason is "changed" or "unchanged")
            {
                SetStatus(title + "已完成；對話保留。"); await LoadManualFormAsync(target.RowId); runtime.Presence?.Refresh(); await RefreshInboxAsync(true);
            }
            else StorageStatus.Text = "名單、身分或信任已改變，尚未套用。請重新開啟設定後檢查。";
        }
        catch (Exception ex) { ShowStorageError(ex); }
        finally { _peerAction = false; RefreshPairingControls(); }
    }
}
