using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NekoChat.Core.Attachments;
using NekoChat.Core.Storage;

namespace NekoChat.Windows;

internal sealed class TeacherFileView(TeacherFileEntry entry, string? path, Func<AttachmentRecord, string, Task> action, Func<Task> original) : INotifyPropertyChanged
{
    internal TeacherFileEntry Entry { get; } = entry;
    internal string? Path { get; } = path;
    internal Func<AttachmentRecord, string, Task> Action { get; } = action;
    internal Func<Task> Original { get; } = original;
    private int _progress;
    internal int Progress { get => _progress; set { _progress = value; PropertyChanged?.Invoke(this, new(nameof(Progress))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class TeacherFileCard : UserControl
{
    public object File { get => GetValue(FileProperty); set => SetValue(FileProperty, value); }
    public static readonly DependencyProperty FileProperty = DependencyProperty.Register(nameof(File), typeof(object), typeof(TeacherFileCard),
        new PropertyMetadata(null, (d, e) => ((TeacherFileCard)d).Observe(e.NewValue)));
    private TeacherFileView? _observed;
    private void Observe(object value)
    {
        if (_observed is not null) _observed.PropertyChanged -= Changed;
        _observed = value as TeacherFileView;
        if (_observed is not null) { _observed.PropertyChanged += Changed; Build(_observed); }
    }
    private void Changed(object? sender, PropertyChangedEventArgs e) { if (_observed is { } row) Build(row); }
    private void Build(TeacherFileView view)
    {
        var row = view.Entry.Attachment;
        var grid = new Grid(); grid.ColumnDefinitions.Add(new() { Width = new(42) }); grid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var type = new TextBlock { Text = System.IO.Path.GetExtension(row.File.Name).TrimStart('.').ToUpperInvariant(), FontSize = 10, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        var icon = new Border { Child = type, Width = 34, Height = 42, Background = new SolidColorBrush(Color.FromRgb(237,242,251)), CornerRadius = new(6) }; grid.Children.Add(icon);
        var text = new StackPanel { Margin = new(10,0,12,0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = row.File.Name, ToolTip = row.File.Name, FontWeight = FontWeights.SemiBold, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis });
        string time = DateTimeOffset.TryParse(view.Entry.CreatedUtc, out var utc) ? utc.ToLocalTime().ToString("MM/dd HH:mm") : "";
        text.Children.Add(new TextBlock { Text = ChatWindow.AttachmentSize(row.File.Size) + " · " + view.Entry.TeacherName + " · " + time, Foreground = Brushes.SlateGray, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new(0,4,0,0) });
        string state = row.State == "complete" && view.Path is null ? "本機副本不可用，可重新下載" : row.StateText;
        if (row.State == "receiving") state += " " + view.Progress + "%";
        text.Children.Add(new TextBlock { Text = state, Foreground = Brushes.SlateGray, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new(0,3,0,0) });
        Grid.SetColumn(text,1); grid.Children.Add(text);
        var actions = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        void Add(string label, string action)
        {
            var button = new Button { Content = label, FontSize = 12, MinWidth = 60, Padding = new(8,4,8,4), Margin = new(0,0,0,4) };
            System.Windows.Automation.AutomationProperties.SetName(button, label + row.File.Name);
            button.Click += async (_,_) => { button.IsEnabled = false; try { await view.Action(row, action); } finally { button.IsEnabled = true; } }; actions.Children.Add(button);
        }
        if (row.State == "receiving") Add("取消", "cancel");
        else if (view.Path is null) Add("下載", "download");
        else Add("另存…", "save");
        if (row.State == "complete" && row.Problem == "confirmation_pending") Add("重送確認", "download");
        var original = new Button { Content = "原訊息", FontSize = 11, BorderThickness = new(0), Foreground = Brushes.SlateGray, Padding = new(6,3,6,3) };
        original.Click += async (_,_) => await view.Original(); actions.Children.Add(original);
        Grid.SetColumn(actions,2); grid.Children.Add(actions);
        Content = new Border { Child = grid, Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(227,232,239)), BorderThickness = new(1), CornerRadius = new(10), Padding = new(14), Margin = new(0,0,0,10) };
    }
}
