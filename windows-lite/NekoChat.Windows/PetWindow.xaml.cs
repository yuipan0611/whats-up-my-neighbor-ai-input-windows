using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using NekoChat.Core;

namespace NekoChat.Windows;

public partial class PetWindow : Window
{
    internal event Action<string?>? OpenChat;
    internal event Action<string>? SelectCoat;
    internal event Action? ImportCoat;
    internal event Action? CheckUpdates;
    internal event Action<bool>? AutoStartToggled;

    internal readonly Dictionary<string, MenuItem> CoatMenuItems = new();
    private readonly MenuItem autoStartMenuItem;
    private string? sender;
    private sealed record ChatNotice(string Text, string From, string Name);
    private readonly LinkedList<ChatNotice> chatNotices = new();
    private ChatNotice? currentChat;
    private bool persistentNotice;
    internal int PendingChatCount => chatNotices.Count;
    private Point down;
    private bool dragging;
    private bool doubleClick;
    private readonly DispatcherTimer bubbleTimer = new() { Interval = TimeSpan.FromSeconds(12) };

    public PetWindow()
    {
        InitializeComponent();
        InitializeAnimation();
        InitializeReactions();
        Left = SystemParameters.WorkArea.Right - 310;
        Top = SystemParameters.WorkArea.Bottom - 330;

        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            SetWindowLongPtr(handle, -20, GetWindowLongPtr(handle, -20) | 0x08000000 | 0x00000080);
        };

        bubbleTimer.Tick += (_, _) => AdvanceChatNotice();

        var menu = new ContextMenu();
        var open = new MenuItem { Header = "💬 開啟聊天與通訊…" };
        open.Click += (_, _) => OpenChat?.Invoke(null);
        menu.Items.Add(open);

        var coats = new MenuItem { Header = "🎨 選擇貓咪花色" };
        string[] names = ["原版黑貓", "純黑貓", "純白貓", "灰貓", "橘虎斑", "賓士貓", "三花貓", "玳瑁貓", "重點色貓"];
        for (int i = 0; i < names.Length; i++)
        {
            var id = Appearance.Presets[i];
            var item = new MenuItem { Header = names[i], IsCheckable = true, IsChecked = id == "original" };
            item.Click += (_, _) =>
            {
                UpdateCoatChecked(id);
                SelectCoat?.Invoke(id);
            };
            CoatMenuItems[id] = item;
            coats.Items.Add(item);
        }
        menu.Items.Add(coats);

        autoStartMenuItem = new MenuItem { Header = "⚙️ 開機時自動啟動", IsCheckable = true, IsChecked = LoginStartup.IsEnabled() };
        autoStartMenuItem.Click += (_, _) =>
        {
            bool nextState = autoStartMenuItem.IsChecked;
            AutoStartToggled?.Invoke(nextState);
        };
        menu.Items.Add(autoStartMenuItem);

        var import = new MenuItem { Header = "📁 匯入 cat.config.json…" };
        import.Click += (_, _) => ImportCoat?.Invoke();
        menu.Items.Add(import);

        var checkUpdate = new MenuItem { Header = "🔄 檢查更新…" };
        checkUpdate.Click += (_, _) => CheckUpdates?.Invoke();
        menu.Items.Add(checkUpdate);

        menu.Items.Add(new Separator());

        var exit = new MenuItem { Header = "❌ 離開 NekoChat" };
        exit.Click += (_, _) => Application.Current.Shutdown();
        menu.Items.Add(exit);

        Cat.ContextMenu = menu;
    }

    internal void UpdateCoatChecked(string? activeId)
    {
        foreach (var (id, item) in CoatMenuItems)
            item.IsChecked = id == activeId;
    }

    internal void UpdateAutoStartChecked(bool isChecked)
    {
        autoStartMenuItem.IsChecked = isChecked;
    }

    internal void SetArt(ImageSource image) => UseStaticArtwork(image);

    internal void Notify(string text, string? from = null, bool persistent = false, string? displayName = null)
    {
        if (currentChat is not null)
        {
            chatNotices.AddFirst(currentChat);
            currentChat = null;
            TrimChatNotices();
        }
        persistentNotice = persistent;
        sender = from;
        BubbleText.Text = from is null ? text : $"{text}\n點擊回覆";
        Bubble.Visibility = Visibility.Visible;
        bubbleTimer.Stop();
        if (!persistent && !ReactionActive) bubbleTimer.Start();
    }

    internal void NotifyChat(string text, string from, string displayName)
    {
        chatNotices.AddLast(new ChatNotice(text, from, displayName));
        TrimChatNotices();
        if (currentChat is null && !persistentNotice)
            AdvanceChatNotice();
        else if (currentChat is not null)
            RenderChatNotice();
    }

    private void TrimChatNotices()
    {
        while (chatNotices.Count > 100) chatNotices.RemoveLast();
    }

    private void RenderChatNotice()
    {
        if (currentChat is null) return;
        sender = currentChat.From;
        BubbleText.Text = $"{currentChat.Text}\n點擊回覆" + (chatNotices.Count > 0 ? $" · 另有 {chatNotices.Count} 則" : "");
    }

    internal void AdvanceChatNotice()
    {
        bubbleTimer.Stop();
        currentChat = null;
        persistentNotice = false;
        if (chatNotices.First is not { } next)
        {
            sender = null;
            Bubble.Visibility = Visibility.Collapsed;
            return;
        }
        currentChat = next.Value;
        chatNotices.RemoveFirst();
        RenderChatNotice();
        Bubble.Visibility = Visibility.Visible;
        if (!ReactionActive) bubbleTimer.Start();
    }

    internal void ClearChatNotices()
    {
        chatNotices.Clear();
        if (currentChat is not null)
        {
            currentChat = null;
            sender = null;
            bubbleTimer.Stop();
            Bubble.Visibility = Visibility.Collapsed;
        }
    }

    private void BubbleClick(object sender, RoutedEventArgs e)
    {
        OpenChat?.Invoke(this.sender);
        if (currentChat is not null) AdvanceChatNotice();
    }

    private void CatDown(object sender, MouseButtonEventArgs e)
    {
        down = e.GetPosition(this);
        dragging = false;
        doubleClick = e.ClickCount == 2;
        automaticMotion = false;
        nextAction = AnimationNow + 5;
        if (animatedCoat is not null) animation.Press(AnimationNow);
        Cat.CaptureMouse();
    }

    private void CatMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || !Cat.IsMouseCaptured) return;
        var point = e.GetPosition(this);
        var delta = point - down;
        if (dragging || Math.Abs(delta.X) > SystemParameters.MinimumHorizontalDragDistance || Math.Abs(delta.Y) > SystemParameters.MinimumVerticalDragDistance)
        {
            if (animatedCoat is not null) animation.Drag(AnimationNow);
            dragging = true;
            Left += delta.X;
            Top += delta.Y;
        }
    }

    private void CatUp(object sender, MouseButtonEventArgs e)
    {
        if (!Cat.IsMouseCaptured) return;
        if (animatedCoat is not null) animation.Release(AnimationNow);
        Cat.ReleaseMouseCapture();
        nextAction = AnimationNow + 5;
        Left = Math.Clamp(Left, SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenLeft + Math.Max(0, SystemParameters.VirtualScreenWidth - Width));
        Top = Math.Clamp(Top, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenTop + Math.Max(0, SystemParameters.VirtualScreenHeight - Height));
        if (!dragging && doubleClick) OpenChat?.Invoke(null);
        doubleClick = false;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
}
