using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using NekoChat.Core;
using NekoChat.Core.Roster;
using NekoChat.Core.Storage;

namespace NekoChat.Windows;

internal sealed class RosterImportWindow : Window
{
    private readonly ChatRuntime _runtime;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 8) };
    private readonly ComboBox _encoding = new() { Width = 120, Margin = new(8, 0, 8, 0) };
    private readonly ComboBox _choice = new() { MinWidth = 160, MaxWidth = 520, Margin = new(8) };
    private readonly DataGrid _grid = new() { AutoGenerateColumns = false, IsReadOnly = true, EnableRowVirtualization = true, EnableColumnVirtualization = true, CanUserAddRows = false, SelectionMode = DataGridSelectionMode.Single };
    private readonly Button _apply = new() { Content = "套用已確認預覽", Padding = new(12, 6, 12, 6), IsEnabled = false };
    private IReadOnlyList<RosterSnapshotRow> _snapshot = [];
    private readonly Dictionary<int, RosterImportChoice> _choices = [];
    private readonly HashSet<int> _skips = [];
    private byte[]? _bytes;
    private RosterCsvResult? _csv;
    private RosterImportPreview? _preview;
    private bool _updating, _busy;
    internal RosterImportWindow(ChatRuntime runtime)
    {
        _runtime = runtime; Title = "匯入名單預覽"; Width = 860; Height = 640; MinWidth = 440; MinHeight = 360; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new DockPanel { Margin = new(16) }; Content = panel;
        var intro = new StackPanel(); DockPanel.SetDock(intro, Dock.Top); panel.Children.Add(intro);
        intro.Children.Add(new TextBlock { Text = "先檢查新增、更新、略過與重複資料；套用不會自動配對身分。CSV 欄名：student_number,name，可選 ip,role（student／teacher）。原名單未出現的列會保留。", TextWrapping = TextWrapping.Wrap });
        var tools = new WrapPanel { Margin = new(0, 12, 0, 0) }; intro.Children.Add(tools);
        var open = new Button { Content = "選擇 CSV…", Padding = new(8, 4, 8, 4) }; open.Click += async (_, _) => await OpenCsv(); tools.Children.Add(open);
        _encoding.Items.Add("UTF-8"); _encoding.Items.Add("Big5（手動選擇）"); _encoding.SelectedIndex = 0; _encoding.SelectionChanged += (_, _) => { if (!_busy && _bytes is not null) { _choices.Clear(); _skips.Clear(); Parse(); } }; tools.Children.Add(_encoding);
        var refresh = new Button { Content = "重新讀取名單並重做預覽", Padding = new(8, 4, 8, 4) }; refresh.Click += async (_, _) => { if (_busy) return; try { _snapshot = await _runtime.Worker.Run(s => s.GetRosterSnapshot()); _choices.Clear(); _skips.Clear(); Parse(); } catch { _status.Text = "無法讀取名單，請重試。"; } }; tools.Children.Add(refresh); intro.Children.Add(_status);
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); panel.Children.Add(footer);
        var chooser = new WrapPanel(); chooser.Children.Add(new TextBlock { Text = "選取歧義列後明確選擇：", VerticalAlignment = VerticalAlignment.Center }); chooser.Children.Add(_choice); footer.Children.Add(chooser); _choice.IsEnabled = false; _choice.SelectionChanged += ChoiceChanged;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 8, 0, 0) };
        var cancel = new Button { Content = "取消", IsCancel = true, Padding = new(12, 6, 12, 6), Margin = new(0, 0, 8, 0) }; actions.Children.Add(cancel); actions.Children.Add(_apply); footer.Children.Add(actions); _apply.Click += async (_, _) => await Apply();
        foreach (var column in new[] { ("行", "Line"), ("動作", "Action"), ("姓名", "Name"), ("學號", "Number"), ("IP", "Ip"), ("老師", "Teacher"), ("問題", "Error") })
            _grid.Columns.Add(new DataGridTextColumn { Header = column.Item1, Binding = new System.Windows.Data.Binding(column.Item2), Width = DataGridLength.Auto });
        _grid.SelectionChanged += (_, _) => UpdateChoice(); panel.Children.Add(_grid);
        Loaded += (_, _) => { DisplayScreens.FitChat(this); cancel.Focus(); };
    }
    private async Task OpenCsv()
    {
        if (_busy) return; var picker = new OpenFileDialog { Filter = "CSV 名單|*.csv|所有檔案|*.*", Title = "選擇要預覽的名單" }; if (picker.ShowDialog(this) != true) return;
        _busy = true; _apply.IsEnabled = false;
        try
        {
            _bytes = await Task.Run(() => { using var file = new FileStream(picker.FileName, FileMode.Open, FileAccess.Read, FileShare.Read); if (file.Length > RosterCsv.MaxBytes) throw new IOException(); byte[] data = new byte[checked((int)file.Length)]; file.ReadExactly(data); if (file.ReadByte() != -1) throw new IOException(); return data; });
            _snapshot = await _runtime.Worker.Run(s => s.GetRosterSnapshot()); _choices.Clear(); _skips.Clear(); Parse();
        }
        catch { _bytes = null; _preview = null; _grid.ItemsSource = null; _status.Text = "無法讀取 CSV，請確認檔案可讀且不超過 1 MiB。"; }
        finally { _busy = false; }
    }
    private void Parse()
    {
        if (_bytes is null) return;
        _csv = RosterCsv.Parse(_bytes, _encoding.SelectedIndex == 1 ? RosterCsvEncoding.Big5 : RosterCsvEncoding.Utf8, LanInvitation.LocalAddresses().ToArray());
        if (_csv.FatalError is not null) { _preview = null; _grid.ItemsSource = null; _apply.IsEnabled = false; _status.Text = _csv.FatalError == "encoding_choice_required" ? "無法以 UTF-8 讀取；若檔案為 Big5，請手動切換編碼。" : "CSV 格式或欄名不正確，請修改檔案後重新選擇。"; return; }
        Rebuild();
    }
    private void Rebuild()
    {
        if (_csv is null) return;
        _preview = RosterImportPreview.Create(_snapshot, _csv, _choices.Values);
        _grid.ItemsSource = _preview.Items.Select(i => new PreviewRow(i, _skips.Contains(i.Line))).ToArray();
        int unresolved = _preview.Items.Count(i => i.Action == RosterImportAction.Ambiguous && !_skips.Contains(i.Line));
        _status.Text = $"新增 {_preview.Items.Count(i => i.Action == RosterImportAction.Add)} · 更新 {_preview.Items.Count(i => i.Action == RosterImportAction.Update)} · 不變 {_preview.Items.Count(i => i.Action == RosterImportAction.Unchanged)} · 略過／錯誤 {_preview.Items.Count(i => i.Action == RosterImportAction.Error || _skips.Contains(i.Line))} · 尚待選擇 {unresolved}。";
        _apply.IsEnabled = unresolved == 0; UpdateChoice();
    }
    private void UpdateChoice()
    {
        _updating = true;
        try
        {
            _choice.Items.Clear(); _choice.IsEnabled = false;
            if (_grid.SelectedItem is not PreviewRow row || row.Item.Action != RosterImportAction.Ambiguous) return;
            _choice.Items.Add(new Choice("請選擇", 0, null)); _choice.Items.Add(new Choice("略過此列", 1, null)); _choice.Items.Add(new Choice("新增為另一位同學", 2, null));
            foreach (string id in row.Item.CandidateIds) { var entry = _snapshot.Single(e => e.Entry.Id == id).Entry; _choice.Items.Add(new Choice($"更新 {entry.DisplayName} · 學號 {entry.StudentNumber ?? "無"} · {entry.Endpoint ?? "無 IP"}", 3, id)); }
            _choice.SelectedIndex = _skips.Contains(row.Line) ? 1 : 0; _choice.IsEnabled = true;
        }
        finally { _updating = false; }
    }
    private void ChoiceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || _busy || _grid.SelectedItem is not PreviewRow row || _choice.SelectedItem is not Choice choice) return;
        _choices.Remove(row.Line); _skips.Remove(row.Line);
        if (choice.Kind == 1) _skips.Add(row.Line); else if (choice.Kind is 2 or 3) _choices[row.Line] = new(row.Line, choice.Target);
        Rebuild();
    }
    private async Task Apply()
    {
        if (_busy || _preview is null || !_apply.IsEnabled) return;
        if (MessageBox.Show(this, _status.Text + "\n套用這份預覽？有問題或明確略過的列不會匯入。", "確認匯入名單", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        var captured = _preview; _busy = true; _apply.IsEnabled = false;
        try
        {
            await _runtime.InvalidatePairingAsync("roster_import");
            var result = await _runtime.Worker.Run(s => s.ApplyRosterPreview(captured));
            if (result.Applied) { DialogResult = true; return; }
            _status.Text = "名單或位址已改變；尚未套用。請按重新讀取並重新檢查預覽。";
        }
        catch { _status.Text = "無法套用名單；請重試，並重新檢查預覽。"; }
        finally { _busy = false; }
    }
    private sealed record Choice(string Label, int Kind, string? Target) { public override string ToString() => Label; }
    private sealed record PreviewRow(RosterImportItem Item, bool Skip)
    {
        public int Line => Item.Line;
        public string Action => Skip ? "略過" : Item.Action switch { RosterImportAction.Add => "新增", RosterImportAction.Update => "更新", RosterImportAction.Unchanged => "不變", RosterImportAction.Ambiguous => "請選擇", _ => "錯誤／略過" };
        public string Name => Item.Row?.Name ?? ""; public string Number => Item.Row?.StudentNumber ?? ""; public string Ip => Item.Row?.Endpoint ?? "";
        public string Teacher => Item.Row?.IsTeacher == true ? "是" : ""; public string Error => string.Join("、", Item.ErrorCodes.Select(e => e switch { "endpoint_conflict" => "IP 衝突", "duplicate_student_number" => "學號重複", "invalid_endpoint" => "IP 不可使用", "invalid_choice" => "選擇已失效", "duplicate_target" => "多列指向同一同學", _ => "欄位或重複資料問題" }));
    }
}

public partial class ChatWindow
{
    private void ImportRosterClick(object sender, RoutedEventArgs e)
    {
        if (_runtime is null) { SetStatus("本機資料尚未就緒，請先重新讀取。"); return; }
        var preview = new RosterImportWindow(_runtime) { Owner = this };
        if (preview.ShowDialog() == true) { _runtime.Presence?.Refresh(); ResetRecipientProtocolHints(); DataChanged(); SetStatus("已套用名單；不會自動配對身分。"); }
    }
}
