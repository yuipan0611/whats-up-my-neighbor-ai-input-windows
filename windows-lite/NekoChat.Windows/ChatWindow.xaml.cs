using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NekoChat.Core;

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
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }

    internal void ShowChat(string? targetIp = null)
    {
        if (!string.IsNullOrWhiteSpace(targetIp))
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
    }

    internal void UpdateWanderEnabled(bool enabled) => UpdateSettings(settings with { WanderEnabled = enabled });

    internal void SetStatus(string text) => Status.Text = text;

    internal void SetReceiverState(bool running, int port, string? error)
    {
        SendButton.IsEnabled = running;
        TeacherReactions.IsEnabled = running;
        ScrollReaction.IsEnabled = running;
        ReceiverStatus.Foreground = running ? System.Windows.Media.Brushes.DarkGreen : System.Windows.Media.Brushes.Firebrick;
        ReceiverStatus.Text = running
            ? $"接收已啟動（TCP {port}）；對方可輸入你的 IP 傳訊。"
            : $"無法接收訊息（TCP {port}）：{error}。請先從舊版貓咪右鍵選「離開」，再重開此版本；仍失敗請將此提示交給教室管理者。";
    }

    internal void AddHistory(string text)
    {
        History.AppendText(text + Environment.NewLine);
        if (History.Text.Length > 30000) History.Text = History.Text[^20000..];
        History.ScrollToEnd();
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

    private void SaveContactClick(object sender, RoutedEventArgs e)
    {
        var ip = RecipientIp.Text.Trim();
        var name = ContactName.Text.Trim();
        if (string.IsNullOrWhiteSpace(ip) || !LanInvitation.PrivateIPv4(ip))
        {
            SetStatus("請先輸入有效的內網 IP（例如 192.168.1.25）。");
            RecipientIp.Focus();
            return;
        }
        if (string.IsNullOrWhiteSpace(name))
        {
            SetStatus("請輸入備註名稱（例如 隔壁小明）。");
            ContactName.Focus();
            return;
        }

        UpdateSettings(settings.WithContact(ip, name));
        PopulateContactsCombo();
        UpdateComboSelection(ip);
        SetStatus($"已記住：{name} ({ip})");
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
        if (!SendButton.IsEnabled) return;
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
        UpdateSettings(settings with { AutoStartOnLogin = true });
        AutoStartChanged?.Invoke(true);
        SetStatus("已開啟開機啟動。");
    }

    private void AutoStartUnchecked(object sender, RoutedEventArgs e)
    {
        UpdateSettings(settings with { AutoStartOnLogin = false });
        AutoStartChanged?.Invoke(false);
        SetStatus("已關閉開機啟動。");
    }

    private void UpdateSettings(Settings updated)
    {
        settings = updated;
        settings.Save();
        SettingsChanged?.Invoke(settings);
    }

    internal void RememberCoat(string id) => UpdateSettings(settings with { Coat = id });

    internal void SetSending(bool sending)
    {
        SendButton.IsEnabled = !sending;
        TeacherReactions.IsEnabled = !sending;
        ScrollReaction.IsEnabled = !sending;
    }

    private void SaveTeacherClick(object sender, RoutedEventArgs e)
    {
        string ip = TeacherIpBox.Text.Trim();
        if (!LanInvitation.PrivateIPv4(ip)) { SetStatus("請輸入老師完整的內網 IP。"); return; }
        UpdateSettings(settings with { TeacherIp = ip });
        RefreshReactionTargets();
        SetStatus($"老師主機已設為 {ip}。");
    }

    private void ReactionSendClick(object sender, RoutedEventArgs e)
    {
        if (!SendButton.IsEnabled || sender is not Button button ||
            ClassroomReactions.Find(button.Tag as string) is not { } reaction) return;
        string target = reaction.TeacherOnly ? settings.TeacherIp : RecipientIp.Text.Trim();
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
        ScrollTarget.Text = string.IsNullOrWhiteSpace(RecipientIp.Text)
            ? "你再給我滑！ → 請先選定上方聊天對象"
            : $"你再給我滑！ → {RecipientIp.Text.Trim()}";
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

