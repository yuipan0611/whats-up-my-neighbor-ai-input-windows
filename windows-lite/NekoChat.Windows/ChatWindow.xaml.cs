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

    internal event Action<string, string>? SendRequested;
    internal event Action<bool>? AutoStartChanged;

    internal ChatWindow(Settings initial)
    {
        settings = initial;
        InitializeComponent();
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

    internal void SetStatus(string text) => Status.Text = text;

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

        settings = settings.WithContact(ip, name);
        settings.Save();
        PopulateContactsCombo();
        UpdateComboSelection(ip);
        SetStatus($"已記住：{name} ({ip})");
    }

    private void DraftKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            SendClick(sender, e);
        }
    }

    private void SendClick(object sender, RoutedEventArgs e)
    {
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

        settings = settings with { LastRecipientIp = targetIp };
        settings.Save();
        SendRequested?.Invoke(targetIp, text);
    }

    private void AutoStartChecked(object sender, RoutedEventArgs e)
    {
        settings = settings with { AutoStartOnLogin = true };
        settings.Save();
        AutoStartChanged?.Invoke(true);
        SetStatus("已開啟開機啟動。");
    }

    private void AutoStartUnchecked(object sender, RoutedEventArgs e)
    {
        settings = settings with { AutoStartOnLogin = false };
        settings.Save();
        AutoStartChanged?.Invoke(false);
        SetStatus("已關閉開機啟動。");
    }
}
