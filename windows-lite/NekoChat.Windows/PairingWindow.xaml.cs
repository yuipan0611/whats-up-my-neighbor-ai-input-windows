using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using NekoChat.Core;
using NekoChat.Core.Pairing;
using NekoChat.Core.Storage;

namespace NekoChat.Windows;

/// <summary>Protected-focus, explicit human consent adapter. Timer reads RAM projections only; store work stays off dispatcher.</summary>
public partial class PairingWindow : Window
{
    private readonly ChatRuntime _runtime;
    private readonly PairingDialogSession _session;
    private readonly int _port;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private PairingConsent? _rendered;
    private bool _checking, _closing, _allowClose;
    internal PairingWindow(ChatRuntime runtime, PairingRosterTarget target, int port)
    {
        _runtime = runtime; _port = port;
        _session = (runtime.Pairing ?? throw new InvalidOperationException("Pairing is not configured.")).OpenDialog(target);
        InitializeComponent();
        OriginalName.Text = target.DisplayName;
        OriginalLabels.Text = (target.StudentNumber is { } number ? "學號 " + number : "未填學號") + (target.IsTeacher ? " · 老師標籤（不授權）" : "") +
            (target.BoundPeerId is { } peer ? "\n目前綁定身分：" + peer : "\n目前尚未綁定身分");
        Endpoint.Text = target.Endpoint ?? ""; Endpoint.IsReadOnly = target.Endpoint is not null;
        EndpointHint.Text = target.Endpoint is null ? "僅供本輪連線，不會自動寫入名單；等候時可留白。" : "沿用開啟時的名單 IP；要變更請關閉並先編輯名單。";
        Loaded += (_, _) => { DisplayScreens.FitChat(this); if (Owner is not null) Owner.IsVisibleChanged += OwnerVisibilityChanged; _timer.Start(); CloseButton.Focus(); Render(); };
        _timer.Tick += (_, _) => Render();
        Activated += (_, _) => Render();
        Deactivated += (_, _) => { Compared.IsChecked = false; Render(); };
        StateChanged += (_, _) => Render();
        IsVisibleChanged += (_, _) => { if (IsLoaded && !IsVisible) CloseSafely(); };
        Closing += OnClosing;
        Closed += (_, _) => { _timer.Stop(); if (Owner is not null) Owner.IsVisibleChanged -= OwnerVisibilityChanged; };
        Render();
    }
    private void OwnerVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) { if (Owner?.IsVisible != true) CloseSafely(); }
    private void Render()
    {
        var view = _session.View; var presentation = PairingDialogPresentation.From(view, _session.Active, _session.Busy || _checking || _closing);
        if (!ReferenceEquals(_rendered, view.Consent)) { _rendered = view.Consent; Compared.IsChecked = false; }
        StateTitle.Text = presentation.Status; StateDetail.Text = presentation.Detail; SasNumber.Text = presentation.Number; Countdown.Text = presentation.Countdown;
        bool usable = !_closing && _session.Active && IsActive && WindowState != WindowState.Minimized && IsVisible;
        bool allowedState = _session.Target.Pairing is not (PairingState.Revoked or PairingState.Suspended);
        BeginButton.IsEnabled = ListenButton.IsEnabled = presentation.CanStart && usable && allowedState;
        Endpoint.IsEnabled = presentation.CanStart && !_checking && !_closing;
        Compared.IsEnabled = presentation.CanCompare && usable;
        ConfirmButton.IsEnabled = Compared.IsEnabled && Compared.IsChecked == true && ReferenceEquals(_rendered?.RosterTarget, _session.Target);
        RejectButton.IsEnabled = presentation.CanCompare && usable;
    }
    private void ComparedChanged(object sender, RoutedEventArgs e) { if (IsInitialized) Render(); }
    private async void BeginClick(object sender, RoutedEventArgs e) => await BeginAsync(false);
    private async void ListenClick(object sender, RoutedEventArgs e) => await BeginAsync(true);
    private async Task BeginAsync(bool listen)
    {
        if (_checking || _closing || !(listen ? ListenButton.IsEnabled : BeginButton.IsEnabled)) return;
        string endpoint = Endpoint.Text.Trim(); _checking = true; ActionStatus.Text = ""; Render();
        try
        {
            if (!listen)
            {
                bool valid = await Task.Run(() => LanInvitation.PrivateIPv4(endpoint) &&
                    (_runtime.IsolatedData || !endpoint.StartsWith("127.", StringComparison.Ordinal) && !LanInvitation.LocalAddresses().Contains(endpoint, StringComparer.Ordinal)));
                if (!valid) { ActionStatus.Text = "請填寫對方的內網 IPv4；不能使用自己或回送位址。"; return; }
            }
            if (!_session.Active || _closing) return;
            var work = _session.BeginAsync(listen ? null : endpoint, _port, listen, _runtime.Lifetime); _checking = false; Render();
            var step = await work;
            if (step.Code is not ("awaiting_user" or "listening")) ActionStatus.Text = "本輪尚未開始或已中止。請檢查名單、位址與對方畫面，再明確重試。";
        }
        catch (OperationCanceledException) { ActionStatus.Text = "配對已停止。請關閉後重新開啟。"; }
        catch { ActionStatus.Text = "無法完成本輪核對。請關閉，檢查本機狀態與對方畫面後重新開始。"; }
        finally { _checking = false; Render(); }
    }
    private async void ConfirmClick(object sender, RoutedEventArgs e)
    {
        var consent = _rendered; Render();
        if (!ConfirmButton.IsEnabled || consent is null || !ReferenceEquals(consent, _rendered)) return;
        Compared.IsChecked = false; ActionStatus.Text = "";
        try { var work = _session.ConfirmAsync(consent, _runtime.Lifetime); Render(); await work; }
        catch { ActionStatus.Text = "尚未確認結果。請查看本機名單與對方畫面；兩台可能狀態不同。"; }
        finally { Render(); }
    }
    private async void RejectClick(object sender, RoutedEventArgs e)
    {
        var consent = _rendered; Render();
        if (!RejectButton.IsEnabled || consent is null || !ReferenceEquals(consent, _rendered)) return;
        Compared.IsChecked = false;
        try { await _session.RejectAsync(consent); }
        catch { ActionStatus.Text = "無法確認對方是否已中止。請關閉並查看對方畫面。"; }
        finally { Render(); }
    }
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
    private void OnClosing(object? sender, CancelEventArgs e) { if (_allowClose) return; e.Cancel = true; CloseSafely(); }
    internal async void CloseSafely()
    {
        if (_closing || _allowClose) return;
        _closing = true; _timer.Stop();
        try { var drain = _session.DisposeAsync(); Render(); await drain; }
        catch { /* Scope was already invalidated synchronously; no stale consent can commit. */ }
        finally { _allowClose = true; Close(); }
    }
}
