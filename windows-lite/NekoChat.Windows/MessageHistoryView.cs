using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using NekoChat.Core.Presentation;

namespace NekoChat.Windows;

internal sealed class MessageView(string key, string rawText, bool outgoing, bool legacy, long sequence, string utc, string delivery, ImageSource? peerAvatar = null) : INotifyPropertyChanged
{
    public string Key { get; } = key;
    public string RawText { get; } = rawText;
    public bool Outgoing { get; } = outgoing;
    public bool Legacy { get; } = legacy;
    public long Sequence { get; } = sequence;
    public string Utc { get; } = utc;
    public ImageSource? PeerAvatar { get; } = peerAvatar;
    private string _delivery = delivery;
    public string Delivery { get => _delivery; set { if (_delivery != value) { _delivery = value; PropertyChanged?.Invoke(this, new(nameof(Delivery))); } } }
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

    private void Render(object newContent)
    {
        if (newContent is not MessageView message) return;
        var body = new StackPanel();
        var document = MessageDocument.Parse(message.RawText);

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

            var menu = new ContextMenu();
            foreach (var mode in new[] { ("保留原換行", CodeCopyNewlines.Preserve), ("使用 LF 換行", CodeCopyNewlines.Lf), ("使用 CRLF 換行", CodeCopyNewlines.CrLf) })
            {
                var choice = new MenuItem { Header = mode.Item1 };
                choice.Click += (_, _) => ShowCopyFeedback(document.CopyCode(block, mode.Item2));
                menu.Items.Add(choice);
            }
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
            Foreground = Brush("#596B80"),
            Margin = new(0, 4, 0, 0),
            HorizontalAlignment = message.Outgoing ? HorizontalAlignment.Right : HorizontalAlignment.Left
        };
        string prefix = DateTimeOffset.TryParse(message.Utc, out var utc) ? utc.ToLocalTime().ToString("HH:mm") + " · " : "";
        metadata.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(MessageView.Delivery)) { Source = message, StringFormat = prefix + "{0}" });
        body.Children.Add(metadata);

        var card = new Border
        {
            Child = body,
            Background = Brush(message.Outgoing ? "#E9EFFD" : "#F3F6FA"),
            CornerRadius = new(12),
            Padding = new(12, 8, 12, 8),
            MaxWidth = 520
        };

        var rawMenu = new ContextMenu();
        var rawCopy = new MenuItem { Header = "複製整則原文" };
        rawCopy.Click += (_, _) => ShowCopyFeedback(message.RawText);
        rawMenu.Items.Add(rawCopy);
        card.ContextMenu = rawMenu;

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

        Content = rowGrid;
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
