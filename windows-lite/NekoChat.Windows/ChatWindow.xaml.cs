using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NekoChat.Core;
using NekoChat.Core.Diagnostics;

namespace NekoChat.Windows;

public partial class ChatWindow : Window
{
    private Settings settings;
    private bool updatingCombo;
    private bool updatingDisplays;
    private bool composingText;

    internal event Action<string, string>? SendRequested;
    internal event Action<bool>? AutoStartChanged;
    internal event Action<Settings>? SettingsChanged;
    internal event Action<string, ClassroomReaction>? ReactionRequested;

    internal ChatWindow(Settings initial)
    {
        settings = initial;
        InitializeComponent();
        RefreshSettingsSaveStatus();
        InitializeWorkAreaSizing();
        // Observe composition completion even when TextBox/IME handles the event.
        Draft.AddHandler(TextCompositionManager.PreviewTextInputStartEvent,
            new TextCompositionEventHandler((_, _) => composingText = true), true);
        Draft.AddHandler(TextCompositionManager.PreviewTextInputEvent,
            new TextCompositionEventHandler((_, _) => composingText = false), true);
        Draft.LostKeyboardFocus += (_, _) => composingText = false;
        var version = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(ChatWindow).Assembly)?.InformationalVersion.Split('+')[0];
        Title = $"NekoChat Lite {version}";
        Closing += OnClosing;

        AutoStartCheck.IsChecked = settings.AutoStartOnLogin;
        if (!string.IsNullOrWhiteSpace(settings.LastRecipientIp))
        {
            RecipientIp.Text = settings.LastRecipientIp;
            var match = settings.Contacts.FirstOrDefault(c => c.Ip == settings.LastRecipientIp);
            if (match is not null) ContactName.Text = match.Name;
        }

        PopulateContactsCombo();
        UpdateLocalIPDisplay();
        TeacherIpBox.Text = settings.TeacherIp;
        RefreshReactionTargets();
        RefreshDisplays();
        ReactionPreviewToggle.IsChecked = settings.ShowReactionPreviews;
        InitializeInbox();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        e.Cancel = true;
        _pairingWindow?.CloseSafely();
        Hide();
    }

    internal void ShowChat(string? targetIp = null)
    {
        _userOpened = true;
        if (!string.IsNullOrWhiteSpace(targetIp) && !targetIp.StartsWith("peer:", StringComparison.Ordinal) && !targetIp.StartsWith("legacy-", StringComparison.Ordinal))
        {
            RecipientIp.Text = targetIp.Trim();
            var match = settings.Contacts.FirstOrDefault(c => c.Ip == targetIp.Trim());
            ContactName.Text = match?.Name ?? "";
            UpdateComboSelection(targetIp.Trim());
        }
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Draft.Focus();
        SelectEndpoint(targetIp);
    }

    internal void UpdateWanderEnabled(bool enabled) => UpdateSettings(settings with { WanderEnabled = enabled });

    internal void SetStatus(string text) => Status.Text = text;

    internal void SetReceiverState(bool running, int port, string? error)
    {
        _receiverRunning = running;
        ReceiverStatus.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        ReceiverStatus.Foreground = running ? System.Windows.Media.Brushes.DarkGreen : System.Windows.Media.Brushes.Firebrick;
        ReceiverStatus.Text = running
            ? "接收已啟動；對方可輸入你的 IP 傳訊。"
            : error?.Contains("本機資料", StringComparison.Ordinal) == true ? "接收尚未啟動：本機資料尚未就緒。請先處理資料錯誤並重新讀取。"
            : $"無法接收訊息：{error}。請先從舊版貓咪右鍵選「離開」，再重開此版本；仍失敗請將此提示交給教室管理者。";
        UpdateComposer();
    }

    internal void AddHistory(string text)
    {
        History.AddTransient(text);
        if (History.Rows.Count > 0) History.ScrollIntoView(History.Rows[^1]);
    }

    internal void UpdateLocalIPDisplay()
    {
        var ip = LanInvitation.PrimaryLocalIP();
        MyDirectIP.Text = string.IsNullOrWhiteSpace(ip) ? "我的 IP：未連線" : $"我的 IP：{ip}";
    }

    private void CopyMyIPClick(object sender, RoutedEventArgs e)
    {
        var ip = LanInvitation.PrimaryLocalIP();
        if (!string.IsNullOrWhiteSpace(ip))
            SetStatus(NativeInput.Copy(ip, "我的 IP 已複製"));
        else
            SetStatus("尚未偵測到內網 IP，請確認網路連線。");
    }

    private void PopulateContactsCombo()
    {
        updatingCombo = true;
        ContactsCombo.Items.Clear();
        ContactsCombo.Items.Add("常用名單…");
        foreach (var contact in settings.Contacts)
        {
            ContactsCombo.Items.Add($"{contact.Name} ({contact.Ip})");
        }
        ContactsCombo.SelectedIndex = 0;
        updatingCombo = false;
    }

    private void UpdateComboSelection(string ip)
    {
        updatingCombo = true;
        for (int i = 1; i < ContactsCombo.Items.Count; i++)
        {
            if (ContactsCombo.Items[i]?.ToString()?.Contains($"({ip})") == true)
            {
                ContactsCombo.SelectedIndex = i;
                updatingCombo = false;
                return;
            }
        }
        ContactsCombo.SelectedIndex = 0;
        updatingCombo = false;
    }

    private void ContactsComboChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingCombo || ContactsCombo.SelectedIndex <= 0) return;
        var selected = settings.Contacts.ElementAtOrDefault(ContactsCombo.SelectedIndex - 1);
        if (selected is not null)
        {
            RecipientIp.Text = selected.Ip;
            ContactName.Text = selected.Name;
            Draft.Focus();
        }
    }

    private async void SaveContactClick(object sender, RoutedEventArgs e)
    {
        if (_runtime is null) { SetStatus("本機資料尚未就緒，請先重新讀取。"); return; }
        var ip = RecipientIp.Text.Trim();
        var name = ContactName.Text.Trim();
        if (!LanInvitation.PrivateIPv4(ip))
        { SaveContactFeedback.Text = "請輸入有效的內網 IP 位址"; RecipientIp.Focus(); return; }
        if (string.IsNullOrWhiteSpace(name)) name = TeacherLabel.IsChecked == true ? "老師" : ip;

        await SaveRosterAsync(name, ip);
    }

    private void DraftPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // TextBox handles bubbling Enter itself when AcceptsReturn is true.
        // Leave IME confirmation and modified Enter to the input method/TextBox.
        if (e.Key != Key.Enter || composingText || Keyboard.Modifiers != ModifierKeys.None) return;
        e.Handled = true;
        if (!e.IsRepeat) SendClick(sender, e);
    }

    private void SendClick(object sender, RoutedEventArgs e)
    {
        if (!SendButton.IsEnabled) { _runtime?.Diagnostics.Record(DiagnosticStage.SendAdmission, DiagnosticOutcome.Deferred, DiagnosticCode.UiNotReady); return; }
        _runtime?.Diagnostics.Record(DiagnosticStage.SendAdmission, DiagnosticOutcome.Started);
        if (_runtime is not null) { _ = SendCurrentAsync(false); return; }
        var targetIp = RecipientIp.Text.Trim();
        var text = Draft.Text;
        if (string.IsNullOrWhiteSpace(targetIp))
        {
            SetStatus("請輸入收件同學的內網 IP。");
            RecipientIp.Focus();
            return;
        }
        if (string.IsNullOrWhiteSpace(text))
        {
            SetStatus("請先輸入訊息文字。");
            Draft.Focus();
            return;
        }

        UpdateSettings(settings with { LastRecipientIp = targetIp });
        SendRequested?.Invoke(targetIp, text);
    }

    private void AutoStartChecked(object sender, RoutedEventArgs e)
    {
        bool saved = UpdateSettings(settings with { AutoStartOnLogin = true });
        AutoStartChanged?.Invoke(true);
        if (saved) SetStatus("已開啟開機啟動。");
    }

    private void AutoStartUnchecked(object sender, RoutedEventArgs e)
    {
        bool saved = UpdateSettings(settings with { AutoStartOnLogin = false });
        AutoStartChanged?.Invoke(false);
        if (saved) SetStatus("已關閉開機啟動。");
    }

    internal void RefreshSettingsSaveStatus()
    {
        SettingsSaveStatus.Text = settings.StatusNote ?? (Settings.LastSaveProblem is null ? "" : "設定未能儲存；目前的設定變更可能在重開後遺失，請處理設定檔讀寫問題。");
        SettingsSaveStatus.Visibility = SettingsSaveStatus.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private bool UpdateSettings(Settings updated)
    {
        settings = updated;
        bool saved = settings.Save();
        RefreshSettingsSaveStatus();
        SettingsChanged?.Invoke(settings);
        if (!saved) SetStatus("設定檔未儲存；請先處理設定檔的讀寫問題，原檔已保留。");
        return saved;
    }

    internal void RememberCoat(string id) => UpdateSettings(settings with { Coat = id });

    internal void SetSending(bool sending)
    {
        _sending = sending; UpdateComposer();
    }

    private void SaveTeacherClick(object sender, RoutedEventArgs e)
    {
        string ip = TeacherIpBox.Text.Trim();
        if (!LanInvitation.PrivateIPv4(ip)) { SetStatus("請輸入老師完整的內網 IP。"); return; }
        bool saved = UpdateSettings(settings with { TeacherIp = ip });
        RefreshReactionTargets();
        if (saved) SetStatus($"老師主機已設為 {ip}。");
    }

    private void ReactionSendClick(object sender, RoutedEventArgs e)
    {
        if (!CanSendReaction || sender is not FrameworkElement element ||
            ClassroomReactions.Find(element.Tag as string) is not { } reaction) return;
        if (_selected is not { Removed: false, CanSend: true } recipient || !NekoChat.Core.Presentation.ClassroomReactionPolicy.CanSend(recipient.IsTeacher, recipient.Removed, recipient.CanSend, reaction.TeacherOnly))
        { SetStatus("請先開啟對應的聊天室，再傳送貼圖。"); return; }
        string target = recipient.Endpoint ?? "";
        if (!LanInvitation.PrivateIPv4(target)) { SetStatus("請先設定有效的收件對象 IP。"); return; }
        ReactionRequested?.Invoke(target, reaction);
    }

    private void RecipientChanged(object sender, TextChangedEventArgs e)
    {
        if (ScrollTarget is not null) RefreshReactionTargets();
    }

    private void RefreshReactionTargets()
    {
        TeacherTarget.Text = $"上面四款 → 老師 {settings.TeacherIp}";
        string target = _runtime is null ? RecipientIp.Text.Trim() : _selected?.Endpoint ?? "";
        ScrollTarget.Text = string.IsNullOrWhiteSpace(target)
            ? "你再給我滑！ → 請先選定上方聊天對象"
            : $"你再給我滑！ → {target}";
    }

    private void RefreshDisplays()
    {
        updatingDisplays = true;
        try
        {
            var choices = new List<DisplayScreen> { new("", "跟隨貓咪所在螢幕", IntPtr.Zero, default, default) };
            choices.AddRange(DisplayScreens.All());
            ReactionDisplay.ItemsSource = choices;
            ReactionDisplay.SelectedValue = choices.Any(c => c.DeviceName == settings.ReactionDisplayDevice)
                ? settings.ReactionDisplayDevice : "";
        }
        finally { updatingDisplays = false; }
    }

    private void ReactionDisplayOpened(object? sender, EventArgs e) => RefreshDisplays();

    private void ReactionDisplayChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!updatingDisplays && ReactionDisplay.SelectedItem is DisplayScreen screen)
            UpdateSettings(settings with { ReactionDisplayDevice = screen.DeviceName });
    }
}

