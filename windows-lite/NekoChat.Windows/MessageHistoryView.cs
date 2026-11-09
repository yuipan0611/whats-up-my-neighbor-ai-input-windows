using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using NekoChat.Core.Presentation;
using NekoChat.Core.Attachments;

namespace NekoChat.Windows;

internal sealed class MessageView(string key, string rawText, bool outgoing, bool legacy, long sequence, string utc, string delivery, ImageSource? peerAvatar = null) : INotifyPropertyChanged
{
    public string Key { get; } = key;
    public string? MessageId { get; init; }
    public string RawText { get; } = rawText;
    private string? _reactionHistoryLabel;
    public string? ReactionHistoryLabel { get => _reactionHistoryLabel; set { if (_reactionHistoryLabel != value) { _reactionHistoryLabel = value; PropertyChanged?.Invoke(this, new(nameof(ReactionHistoryLabel))); } } }
    public bool Outgoing { get; } = outgoing;
    public bool Legacy { get; } = legacy;
    public long Sequence { get; } = sequence;
    private string _utc = utc;
    public string Utc { get => _utc; set { if (_utc != value) { _utc = value; PropertyChanged?.Invoke(this, new(nameof(MetadataText))); } } }
    public string MetadataText => MessageMetadataPolicy.Format(Utc, Delivery);
    public ImageSource? PeerAvatar { get; } = peerAvatar;
    private string _delivery = delivery;
    public string Delivery { get => _delivery; set { if (_delivery != value) { _delivery = value; PropertyChanged?.Invoke(this, new(nameof(Delivery))); PropertyChanged?.Invoke(this, new(nameof(MetadataText))); } } }
    private AttachmentRecord? _attachment;
    public AttachmentRecord? Attachment { get => _attachment; set { if (_attachment != value) { _attachment = value; PropertyChanged?.Invoke(this, new(nameof(Attachment))); } } }
    private int _progress;
    public int AttachmentProgress { get => _progress; set { _progress = value; PropertyChanged?.Invoke(this, new(nameof(AttachmentProgress))); } }
    public string? AttachmentPath { get; set; }
    public Func<AttachmentRecord, string, Task>? AttachmentAction { get; set; }
    public string? AttemptId { get; init; }
    public int AttemptTryCount { get; set; }
    public bool AttemptFailed { get; set; }
    public Func<Task>? RetrySend { get; set; }
    public string? RetryDisabledReason { get; set; }
    public Func<Task>? CancelSend { get; set; }
    public Func<Task>? DeleteLocal { get; set; }
    public Func<string?>? DeleteDisabledReason { get; set; }
    public bool AttemptSending { get; set; }
    public bool ContactReadOnly { get; set; }
    internal void RefreshActions() => PropertyChanged?.Invoke(this, new(nameof(RetrySend)));
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class MessageHistoryView : ListBox
{
    internal ObservableCollection<MessageView> Rows { get; } = [];
    public string Text => string.Join(Environment.NewLine, Rows.Select(r => r.RawText));
    public MessageHistoryView() { ItemsSource = Rows; }
    internal void AddTransient(string text) => Rows.Add(new("local:" + Guid.NewGuid(), text, false, true, 0, "", "本機檢查"));
}

public sealed class MessageCard : UserControl
{
    public object Message { get => GetValue(MessageProperty); set => SetValue(MessageProperty, value); }
    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(nameof(Message), typeof(object), typeof(MessageCard), new PropertyMetadata(null, (d, e) => ((MessageCard)d).Render(e.NewValue)));

    private MessageView? _observed;
    private void Render(object newContent)
    {
        if (_observed is not null) _observed.PropertyChanged -= MessageUpdated;
        _observed = newContent as MessageView;
        if (_observed is not null) { _observed.PropertyChanged += MessageUpdated; Build(_observed); }
    }
    private void MessageUpdated(object? sender, PropertyChangedEventArgs e)
    { if (sender is MessageView message && e.PropertyName is nameof(MessageView.Attachment) or nameof(MessageView.AttachmentProgress) or nameof(MessageView.Delivery) or nameof(MessageView.RetrySend) or nameof(MessageView.ReactionHistoryLabel)) Build(message); }
    private void Build(MessageView message)
    {
        var body = new StackPanel();
        if (message.Attachment is { } attachment)
        {
            var fileBody = new StackPanel();
            fileBody.Children.Add(new AttachmentPreviewView(attachment, message.AttachmentPath));
            fileBody.Children.Add(Literal(attachment.File.Name, wrap: true));
            fileBody.Children.Add(Literal(ChatWindow.AttachmentSize(attachment.File.Size) + " · " + attachment.StateText + (attachment.State == "receiving" ? $" {message.AttachmentProgress}%" : attachment.Direction == "outgoing" && attachment.State == "offered" && message.AttachmentProgress > 0 ? $" · 下載進度 {message.AttachmentProgress}%" : ""), wrap: true));
            var actions = new WrapPanel { Margin = new(0,6,0,0) };
            void Button(string label, string action) {
                var button = new Button { Content = label, Margin = new(0,0,8,4), Padding = new(8,4,8,4), MinHeight = 30 };
                if(message.ContactReadOnly && action is "download" or "retry") { button.IsEnabled=false; button.ToolTip="此對話為唯讀，無法啟動附件傳輸。"; ToolTipService.SetShowOnDisabled(button,true); }
                button.Click += async (_,_) => { if (message.AttachmentAction is { } invoke) await invoke(attachment, action); };
                actions.Children.Add(button);
            }
            if (attachment.State is "queued" or "sending" or "receiving" || attachment.Direction == "outgoing" && attachment.State == "offered") Button("取消傳輸", "cancel");
            else if (attachment.Direction == "incoming" && attachment.State != "complete") Button("下載", "download");
            else if (attachment.Direction == "outgoing" && attachment.State is not ("complete" or "offered" or "unavailable")) Button("重試", "retry");
            if (attachment.State == "complete" && attachment.Problem == "confirmation_pending") Button("重送確認", "download");
            if (message.AttachmentPath is not null) Button("另存", "save");
            fileBody.Children.Add(actions);
            body.Children.Add(new Border { Child = fileBody, Background = Brush("#F1F5FA"), BorderBrush = Brush("#DCE3EC"), BorderThickness = new(1), CornerRadius = new(8), Padding = new(10), Margin = new(0,4,0,6) });
        }
        if (message.ReactionHistoryLabel is { } label)
            body.Children.Add(new TextBlock { Text = label, FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = Brush("#596B80") });
        var document = MessageDocument.Parse(message.ReactionHistoryLabel is null ? message.Attachment?.Caption ?? message.RawText : "");

        for (int i = 0; i < document.Blocks.Count; i++)
        {
            int block = i;
            var part = document.Blocks[i];
            if (part.Kind == MessageBlockKind.Text)
            {
                body.Children.Add(Literal(document.GetContent(i), wrap: true));
                continue;
            }

            // Dark Syntax-Highlighted Code Card
            var codeContainer = new Border
            {
                Background = Brush("#18181B"),
                BorderBrush = Brush("#27272A"),
                BorderThickness = new(1),
                CornerRadius = new(8),
                Margin = new(0, 4, 0, 4),
                ClipToBounds = true
            };

            var codeLayout = new DockPanel();

            // Header (Language label on left, Copy button on right)
            var headerPanel = new DockPanel { LastChildFill = true };
            var headerBorder = new Border
            {
                Child = headerPanel,
                Background = Brush("#222226"),
                Padding = new Thickness(10, 4, 8, 4)
            };

            var copyBtn = new Button
            {
                Content = "📋 複製",
                MinWidth = 64,
                MinHeight = 24,
                Padding = new(8, 2, 8, 2),
                Background = Brush("#2E2E33"),
                Foreground = Brush("#E4E4E7"),
                BorderThickness = new(0),
                Cursor = System.Windows.Input.Cursors.Hand,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold
            };

            var copyTemplate = new ControlTemplate(typeof(Button));
            var borderFactory = new FrameworkElementFactory(typeof(Border));
            borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            borderFactory.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
            borderFactory.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Button.PaddingProperty));
            var cpFactory = new FrameworkElementFactory(typeof(ContentPresenter));
            cpFactory.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cpFactory.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            borderFactory.AppendChild(cpFactory);
            copyTemplate.VisualTree = borderFactory;
            copyBtn.Template = copyTemplate;

            copyBtn.Click += (_, _) =>
            {
                ShowCopyFeedback(document.CopyCode(block));
                copyBtn.Content = "✅ 已複製！";
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                timer.Tick += (_, _) =>
                {
                    copyBtn.Content = "📋 複製";
                    timer.Stop();
                };
                timer.Start();
            };

            var menu = new ContextMenu { Tag = "code-copy" };
            foreach (var mode in new[] { ("保留原換行", CodeCopyNewlines.Preserve), ("使用 LF 換行", CodeCopyNewlines.Lf), ("使用 CRLF 換行", CodeCopyNewlines.CrLf) })
            {
                var choice = new MenuItem { Header = mode.Item1 };
                choice.Click += (_, _) => ShowCopyFeedback(document.CopyCode(block, mode.Item2));
                menu.Items.Add(choice);
            }
            menu.Items.Add(new Separator());
            var codeDelete = DeleteItem(message); menu.Items.Add(codeDelete);
            menu.Opened += (_, _) => UpdateDeleteItem(codeDelete, message);
            copyBtn.ContextMenu = menu;

            DockPanel.SetDock(copyBtn, Dock.Right);
            headerPanel.Children.Add(copyBtn);

            string langLabel = (part.Language ?? "code").ToLowerInvariant();
            var langText = new TextBlock
            {
                Text = langLabel + (part.IsClosed ? "" : " · 未結束"),
                Foreground = Brush("#9CA3AF"),
                FontSize = 11,
                FontFamily = new FontFamily("Consolas, Menlo, Cascadia Code"),
                VerticalAlignment = VerticalAlignment.Center
            };
            headerPanel.Children.Add(langText);

            DockPanel.SetDock(headerBorder, Dock.Top);
            codeLayout.Children.Add(headerBorder);

            // Code Content with Syntax Highlighting
            var codeTextBlock = new TextBlock
            {
                FontFamily = new FontFamily("Consolas, Menlo, Cascadia Code, Courier New"),
                FontSize = 12.5,
                LineHeight = 18,
                TextWrapping = TextWrapping.NoWrap
            };
            SyntaxHighlighter.HighlightInto(codeTextBlock.Inlines, document.GetContent(i), part.Language);

            var codeScroll = new ScrollViewer
            {
                Content = codeTextBlock,
                MaxHeight = 240,
                Padding = new Thickness(12, 8, 12, 10),
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            codeLayout.Children.Add(codeScroll);

            codeContainer.Child = codeLayout;
            body.Children.Add(codeContainer);
        }

        var metadata = new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush(message.AttemptFailed ? "#B53737" : "#596B80"),
            Margin = new(0, 4, 0, 0),
            HorizontalAlignment = message.Outgoing ? HorizontalAlignment.Right : HorizontalAlignment.Left
        };
        metadata.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(MessageView.MetadataText)) { Source = message });
        body.Children.Add(metadata);
        if (message.AttemptId is not null)
        {
            var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0,4,0,0) };
            var retry = new Button { Content = "重試傳送", IsEnabled = message.RetrySend is not null, FontSize = 12, Margin = new(0,0,8,0),
                ToolTip = message.RetryDisabledReason ?? (message.RetrySend is null ? "請先傳送或清空正在編輯的其他內容，再重試這則訊息。" : "重試這則訊息，不會重複建立新的訊息") };
            retry.Click += async (_,_) => { if (message.RetrySend is { } invoke) await invoke(); }; actions.Children.Add(retry);
            if (message.CancelSend is { } cancel)
            { var button = new Button { Content = "停止後續重試", FontSize = 12, ToolTip = "停止本機後續重試，不會向對方傳送訊息，也不會收回已送出的訊息。" }; button.Click += async (_,_) => await cancel(); actions.Children.Add(button); }
            body.Children.Add(actions);
        }

        // Assign Child only after choosing its layout; a body cannot belong to Border and overlay simultaneously.
        var card = new Border
        {
            Background = Brush(message.Outgoing ? "#E9EFFD" : "#F3F6FA"),
            CornerRadius = new(12),
            Padding = new(12, 8, 12, 8),
            MaxWidth = 520
        };

        var more = new Button { Content = "⋯", ToolTip = "訊息操作", Padding = new(8,0,8,0),
            HorizontalAlignment = HorizontalAlignment.Right, Opacity = 0 };
        System.Windows.Automation.AutomationProperties.SetName(more, "訊息操作");
        more.Click += (_, _) => { var menu = ActionMenu(message); menu.PlacementTarget = more; menu.IsOpen = true; };
        if (message.ReactionHistoryLabel is not null)
        {
            // Keep the existing action reachable without reserving an empty row under a small annotation.
            var overlay = new Grid(); body.Margin = new Thickness(0, 0, 40, 0);
            more.Width = 32; more.Height = 28; more.VerticalAlignment = VerticalAlignment.Center;
            overlay.Children.Add(body); overlay.Children.Add(more); card.Child = overlay;
        }
        else { body.Children.Add(more); card.Child = body; }

        var avatarImage = new Image
        {
            Width = 36,
            Height = 36,
            VerticalAlignment = VerticalAlignment.Top,
            Source = message.Outgoing ? CatArtwork.Preset("original") : (message.PeerAvatar ?? CatArtwork.Preset("original")),
            Margin = message.Outgoing ? new Thickness(8, 2, 0, 0) : new Thickness(0, 2, 8, 0)
        };

        var rowGrid = new Grid
        {
            Margin = new Thickness(0, 4, 0, 8),
            HorizontalAlignment = message.Outgoing ? HorizontalAlignment.Right : HorizontalAlignment.Left
        };

        if (message.Outgoing)
        {
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Grid.SetColumn(card, 0);
            Grid.SetColumn(avatarImage, 1);
            rowGrid.Children.Add(card);
            rowGrid.Children.Add(avatarImage);
        }
        else
        {
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            Grid.SetColumn(avatarImage, 0);
            Grid.SetColumn(card, 1);
            rowGrid.Children.Add(avatarImage);
            rowGrid.Children.Add(card);
        }

        rowGrid.MouseEnter += (_, _) => more.Opacity = 1;
        rowGrid.MouseLeave += (_, _) => { if (!rowGrid.IsKeyboardFocusWithin) more.Opacity = 0; };
        rowGrid.GotKeyboardFocus += (_, _) => more.Opacity = 1;
        rowGrid.LostKeyboardFocus += (_, _) => { if (!rowGrid.IsMouseOver) more.Opacity = 0; };
        rowGrid.ContextMenu = new ContextMenu();
        rowGrid.AddHandler(ContextMenuService.ContextMenuOpeningEvent, new ContextMenuEventHandler((_, e) => {
            for (DependencyObject? node = e.Source as DependencyObject; node is not null;
                node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
                if ((node as FrameworkElement)?.ContextMenu?.Tag as string == "code-copy") return;
            e.Handled = true;
            var menu = ActionMenu(message, e.OriginalSource as TextBox);
            menu.PlacementTarget = rowGrid; menu.IsOpen = true;
        }), true);
        Content = rowGrid;
    }

    private MenuItem DeleteItem(MessageView message)
    {
        var item = new MenuItem { Header = message.AttemptId is null ? "從這台電腦刪除…" : "停止傳送並刪除…" };
        UpdateDeleteItem(item, message);
        item.Click += async (_, _) => { if (message.DeleteLocal is { } remove) await remove(); };
        return item;
    }
    private static void UpdateDeleteItem(MenuItem item, MessageView message)
    {
        string? reason = message.DeleteDisabledReason?.Invoke() ?? (message.DeleteLocal is null ? "此紀錄不支援本機刪除。" : null);
        item.IsEnabled = reason is null; item.ToolTip = reason;
        ToolTipService.SetShowOnDisabled(item, true);
    }
    private ContextMenu ActionMenu(MessageView message, TextBox? selected = null)
    {
        var menu = new ContextMenu();
        if (selected?.SelectionLength > 0) {
            string text = selected.SelectedText;
            var copySelection = new MenuItem { Header = "複製選取文字" };
            copySelection.Click += (_, _) => ShowCopyFeedback(text); menu.Items.Add(copySelection);
        }
        var copy = new MenuItem { Header = "複製整則原文" };
        copy.Click += (_, _) => ShowCopyFeedback(message.RawText); menu.Items.Add(copy);
        menu.Items.Add(DeleteItem(message)); return menu;
    }

    private void ShowCopyFeedback(string text)
    {
        string feedback = NativeInput.Copy(text, "程式碼／原文");
        if (Window.GetWindow(this) is ChatWindow window) window.SetStatus(feedback);
    }

    private static TextBox Literal(string text, bool wrap) => new()
    {
        Text = text,
        IsReadOnly = true,
        IsReadOnlyCaretVisible = false,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        Background = Brushes.Transparent,
        BorderThickness = new(0),
        Padding = new(0),
        FontSize = 13,
        FontFamily = new("Microsoft JhengHei UI, Microsoft JhengHei"),
        Foreground = Brush("#1C2C41"),
        AcceptsReturn = true
    };

    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
}
