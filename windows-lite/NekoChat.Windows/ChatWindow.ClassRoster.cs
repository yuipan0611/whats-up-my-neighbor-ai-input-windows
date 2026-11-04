using System.IO;
using System.Windows;
using System.Windows.Controls;
using NekoChat.Core.Storage;
using NekoChat.Core;
using NekoChat.Core.Roster;

namespace NekoChat.Windows;

public partial class ChatWindow
{
    private async void RenameNicknameClick(object sender, RoutedEventArgs e)
    {
        if (_runtime is null || sender is not MenuItem { CommandParameter: InboxRowView view } || view.Entry.RosterId is not { } id) return;
        try
        {
            var rows = await _runtime.Worker.Run(s => s.GetRosterSnapshot());
            var entry = rows.FirstOrDefault(r => r.Entry.Id == id)?.Entry;
            if (entry is null) { SetStatus("這位好友已移除。"); return; }
            var input = new TextBox { Text = entry.DisplayName, MaxLength = 100, Margin = new(0, 8, 0, 12) };
            var panel = new StackPanel { Margin = new(20) };
            panel.Children.Add(new TextBlock { Text = "綽號只保存在這台電腦", TextWrapping = TextWrapping.Wrap }); panel.Children.Add(input);
            var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
            var cancel = new Button { Content = "取消", IsCancel = true, Margin = new(0,0,8,0) };
            var save = new Button { Content = "儲存", IsDefault = true };
            buttons.Children.Add(cancel); buttons.Children.Add(save); panel.Children.Add(buttons);
            var dialog = new Window { Owner = this, Title = "編輯綽號", Width = 340, SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel };
            var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.Firebrick };
            panel.Children.Insert(2, error);
            save.Click += (_, _) => {
                try { _ = new RosterEntryView(entry.Id, input.Text.Trim()); dialog.DialogResult = true; }
                catch (ArgumentException) { error.Text = "請輸入 1～64 字的綽號，不能含控制字元。"; input.Focus(); }
            };
            dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
            if (dialog.ShowDialog() != true) return;
            var token = RosterImportPreview.ComputeSnapshotToken(rows);
            var result = await _runtime.Worker.Run(s => s.SaveManualRosterEntry(id,
                new RosterEdit(input.Text.Trim(), entry.StudentNumber, entry.IsTeacher, entry.Endpoint), token));
            if (!result.Changed && result.Reason != "unchanged") { SetStatus(RosterFailure(result.Reason)); return; }
            await RefreshInboxAsync(); SetStatus("綽號已儲存在這台電腦。");
        }
        catch (Exception ex) { ShowStorageError(ex); }
    }

    private async Task BootstrapClassRosterAsync()
    {
        if (_runtime is null || _runtime.ArchivedIdentityFixture || settings.ClassRosterInitialized) return;
        string path = Path.Combine(AppContext.BaseDirectory, "classroom-roster.csv");
        if (!File.Exists(path)) return;
        try
        {
            byte[] bytes = await Task.Run(() => {
                using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (input.Length > RosterCsv.MaxBytes) throw new IOException();
                byte[] data = new byte[checked((int)input.Length)]; input.ReadExactly(data);
                if (input.ReadByte() != -1) throw new IOException(); return data;
            });
            var local = LanInvitation.LocalAddresses().ToArray();
            bool applied = await _runtime.Worker.Run(store => {
                var preview = ClassroomRosterSeed.Preview(bytes, store.GetRosterSnapshot(), local);
                // Existing contacts require the explicit import preview, never an automatic update.
                return preview is null || preview.Changes.Count == 0 || store.ApplyRosterPreview(preview).Applied;
            });
            if (applied) UpdateSettings(settings with { ClassRosterInitialized = true });
            else SetStatus("班級名單尚未載入，請使用「匯入班級名單」檢查。");
        }
        catch { SetStatus("無法載入班級名單，請使用「匯入班級名單」檢查檔案。"); }
    }
}
