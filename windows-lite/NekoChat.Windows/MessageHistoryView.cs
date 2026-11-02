using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NekoChat.Core.Presentation;

namespace NekoChat.Windows;

internal sealed class MessageView(string key, string rawText, bool outgoing, bool legacy, long sequence, string utc, string delivery) : INotifyPropertyChanged
{
    public string Key { get; } = key;
    public string RawText { get; } = rawText;
    public bool Outgoing { get; } = outgoing;
    public bool Legacy { get; } = legacy;
    public long Sequence { get; } = sequence;
    public string Utc { get; } = utc;
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
        var body = new StackPanel(); var document = MessageDocument.Parse(message.RawText);
        for (int i = 0; i < document.Blocks.Count; i++)
        {
            int block = i; var part = document.Blocks[i];
            if (part.Kind == MessageBlockKind.Text) { body.Children.Add(Literal(document.GetContent(i), wrap: true)); continue; }
            var code = new DockPanel(); var header = new DockPanel { LastChildFill = true, Background = Brush("#F3F6FA") };
            var copy = new Button { Content = "複製", MinWidth = 44, MinHeight = 32, Margin = new(4), Padding = new(6, 2, 6, 2) };
            copy.Click += (_, _) => ShowCopyFeedback(document.CopyCode(block));
            var menu = new ContextMenu();
            foreach (var mode in new[] { ("保留原換行", CodeCopyNewlines.Preserve), ("使用 LF 換行", CodeCopyNewlines.Lf), ("使用 CRLF 換行", CodeCopyNewlines.CrLf) })
            { var choice = new MenuItem { Header = mode.Item1 }; choice.Click += (_, _) => ShowCopyFeedback(document.CopyCode(block, mode.Item2)); menu.Items.Add(choice); }
            copy.ContextMenu = menu; DockPanel.SetDock(copy, Dock.Right); header.Children.Add(copy);
            header.Children.Add(new TextBlock { Text = (part.Language ?? "程式碼") + (part.IsClosed ? " · 只供閱讀" : " · 未結束的程式碼區塊"), Margin = new(8, 8, 4, 4), TextWrapping = TextWrapping.Wrap, Foreground = Brush("#596B80"), FontSize = 12 });
            DockPanel.SetDock(header, Dock.Top); code.Children.Add(header);
            var text = Literal(document.GetContent(i), wrap: false); text.FontFamily = new("Consolas, Microsoft JhengHei UI");
            code.Children.Add(new ScrollViewer { Content = text, MaxHeight = 160, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            body.Children.Add(new Border { Child = code, Background = Brushes.White, BorderBrush = Brush("#DCE3EC"), BorderThickness = new(1), CornerRadius = new(4), Margin = new(0, 4, 0, 4) });
        }
        var metadata = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Brush("#596B80"), Margin = new(0, 4, 0, 0) };
        string prefix = DateTimeOffset.TryParse(message.Utc, out var utc) ? utc.ToLocalTime().ToString("HH:mm") + " · " : "";
        metadata.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(MessageView.Delivery)) { Source = message, StringFormat = prefix + "{0}" }); body.Children.Add(metadata);
        var card = new Border { Child = body, Background = Brush(message.Outgoing ? "#E9EFFD" : "#F3F6FA"), CornerRadius = new(12), Padding = new(12, 8, 12, 8), Margin = new(0, 4, 0, 8),
            MaxWidth = 520, HorizontalAlignment = message.Outgoing ? HorizontalAlignment.Right : HorizontalAlignment.Left };
        var rawMenu = new ContextMenu(); var rawCopy = new MenuItem { Header = "複製整則原文" }; rawCopy.Click += (_, _) => ShowCopyFeedback(message.RawText); rawMenu.Items.Add(rawCopy); card.ContextMenu = rawMenu;
        var surface = new Grid(); surface.Children.Add(card); Content = surface;
    }
    private void ShowCopyFeedback(string text)
    {
        string feedback = NativeInput.Copy(text, "程式碼／原文");
        if (Window.GetWindow(this) is ChatWindow window) window.SetStatus(feedback);
    }
    private static TextBox Literal(string text, bool wrap) => new() { Text = text, IsReadOnly = true, IsReadOnlyCaretVisible = false, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        Background = Brushes.Transparent, BorderThickness = new(0), Padding = new(0), FontSize = 13, FontFamily = new("Microsoft JhengHei UI, Microsoft JhengHei"), Foreground = Brush("#1C2C41"), AcceptsReturn = true };
    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
}
