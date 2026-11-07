using System.IO;
using System.Windows;
using System.Windows.Controls;
using NekoChat.Core.Storage;
using NekoChat.Core;
using NekoChat.Core.Roster;

namespace NekoChat.Windows;

public partial class ChatWindow
{
    private void NicknameMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not ListBox list) return;
        var source = e.OriginalSource as DependencyObject;
        var container = source is null ? null : ItemsControl.ContainerFromElement(list, source) as ListBoxItem;
        if (container is null && e.CursorLeft < 0 && System.Windows.Input.Keyboard.FocusedElement is DependencyObject focused)
            container = ItemsControl.ContainerFromElement(list, focused) as ListBoxItem;
        if (container?.DataContext is not InboxRowView row)
        { e.Handled = true; return; }
        var menu = list.ContextMenu;
        menu.Items.Clear();
        var fresh=ContactMenu(row.Entry);var items=fresh.Items.Cast<object>().ToArray();fresh.Items.Clear();foreach(var item in items)menu.Items.Add(item);
    }

    private async Task AddContactDialogAsync(bool teacher,string? initialIp=null)
    {
        if (_runtime is not { } runtime) { SetStatus("本機資料尚未就緒，請先重新讀取。"); return; }
        var input = new TextBox { Text=initialIp??"", MaxLength = 15, Margin = new(0, 6, 0, 12) };
        System.Windows.Automation.AutomationProperties.SetName(input, "輸入對方 IP 位址");
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.Firebrick };
        var panel = new StackPanel { Margin = new(20) };
        panel.Children.Add(new Label { Content = "輸入對方 IP 位址", Target = input, Padding = new(0) });
        panel.Children.Add(input);
        var nickname=new TextBox{MaxLength=64,Margin=new(0,4,0,12)};
        if(!teacher){panel.Children.Add(new Label{Content="本機備註（可留白，只在這台電腦顯示）",Target=nickname});panel.Children.Add(nickname);}panel.Children.Add(error);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 12, 0, 0) };
        var cancel = new Button { Content = "取消", IsCancel = true, Margin = new(0, 0, 8, 0) };
        var save = new Button { Content = teacher ? "加入老師" : "新增對象", IsDefault = true };
        var open=new Button{Content="開啟既有對話",Visibility=Visibility.Collapsed,Margin=new(0,0,8,0)};
        buttons.Children.Add(open);buttons.Children.Add(cancel); buttons.Children.Add(save); panel.Children.Add(buttons);
        var dialog = new Window { Owner = this, Title = teacher ? "加入老師" : "新增對象", Width = 340,
            SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel };
        string? addedIp = null; bool busy = false; bool duplicate=false; string? duplicateIp=null;
        open.Click+=(_,_)=>{addedIp=duplicateIp;dialog.DialogResult=true;};
        dialog.Closing += (_, e) => { if (busy) e.Cancel = true; };
        dialog.Loaded += (_, _) => input.Focus();
        save.Click += async (_, _) => {
            if (busy) return;
            string ip = input.Text.Trim();
            if (!LanInvitation.PrivateIPv4(ip)) { error.Text = "請輸入有效的內網 IP 位址。"; input.Focus(); return; }
            busy = true; save.IsEnabled = cancel.IsEnabled = input.IsEnabled = false;
            try
            {
                await runtime.InvalidatePairingAsync("roster_edit");
                var state=await runtime.Worker.Run(store=>(Token:RosterImportPreview.ComputeSnapshotToken(store.GetRosterSnapshot()),Teacher:store.GetClassroomTeacherIp()));
                if(teacher&&state.Teacher is{} previous&&previous!=ip&&MessageBox.Show(dialog,$"將取代目前的老師 {previous}，原老師保留為一般聯絡人。","更換老師",MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No)!=MessageBoxResult.Yes)return;
                string alias=string.IsNullOrWhiteSpace(nickname.Text)?ip:nickname.Text.Trim();
                var result = await runtime.Worker.Run(store => teacher?store.SetClassroomTeacherByIp(ip,state.Token):store.SaveManualRosterEntry(null,new RosterEdit(alias,null,false,ip),state.Token));
                if (result.Changed || teacher && result.Reason == "unchanged") { addedIp = ip; busy = false; dialog.DialogResult = true; }
                else { error.Text=RosterFailure(result.Reason);duplicate=result.Reason=="duplicate-endpoint";duplicateIp=duplicate?ip:null;open.Visibility=duplicate?Visibility.Visible:Visibility.Collapsed; }
            }
            catch { error.Text = "無法加入好友，請稍後重試。"; }
            finally { busy = false; save.IsEnabled = cancel.IsEnabled = input.IsEnabled = true; }
        };
        if (dialog.ShowDialog() != true || addedIp is null) return;
        runtime.Presence?.Refresh(); await SyncTeacherProjectionAsync();ResetRecipientProtocolHints(); await RefreshInboxAsync();
        InboxFilter.SelectedIndex=_snapshot?.Entries.Any(e=>e.Endpoint==addedIp&&e.Blocked)==true?3:0;
        SelectEndpoint(addedIp); SetStatus(duplicate?"已在名單中，已開啟既有對話。":teacher ? "老師已加入。" : "好友已加入，可按右鍵編輯本機備註。");
    }

    private async void RenameNicknameClick(object sender, RoutedEventArgs e)
    {
        if (_runtime is not { } runtime || sender is not MenuItem { CommandParameter: InboxRowView view } || view.Entry.RosterId is not { } id) return;
        try
        {
            var rows = await runtime.Worker.Run(s => s.GetRosterSnapshot());
            var entry = rows.FirstOrDefault(r => r.Entry.Id == id)?.Entry;
            if (entry is null) { SetStatus("這位好友已移除。"); return; }
            var input = new TextBox { Text = entry.DisplayName, MaxLength = 64, Margin = new(0, 8, 0, 12) };
            var panel = new StackPanel { Margin = new(20) };
            System.Windows.Automation.AutomationProperties.SetName(input, "本機綽號");
            panel.Children.Add(new TextBlock { Text = "備註只保存在這台電腦", TextWrapping = TextWrapping.Wrap }); panel.Children.Add(input);
            var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
            var cancel = new Button { Content = "取消", IsCancel = true, Margin = new(0,0,8,0) };
            var save = new Button { Content = "儲存", IsDefault = true };
            buttons.Children.Add(cancel); buttons.Children.Add(save); panel.Children.Add(buttons);
            var dialog = new Window { Owner = this, Title = "編輯本機備註", Width = 340, SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel };
            var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.Firebrick };
            panel.Children.Insert(2, error);
            save.Click += (_, _) => {
                try { _ = new RosterEntryView(entry.Id, input.Text.Trim()); dialog.DialogResult = true; }
                catch (ArgumentException) { error.Text = "請輸入 1～64 字的綽號，不能含控制字元。"; input.Focus(); }
            };
            dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
            if (dialog.ShowDialog() != true) return;
            if (!ReferenceEquals(runtime, _runtime) || _closingRequested) return;
            string nickname = input.Text.Trim();
            var token = RosterImportPreview.ComputeSnapshotToken(rows);
            var result = await runtime.Worker.Run(s => s.SaveManualRosterEntry(id,
                new RosterEdit(nickname, entry.StudentNumber, entry.IsTeacher, entry.Endpoint), token));
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
            else SetStatus("班級名單尚未載入，請重新下載完整班級包，或按「加入好友」輸入 IP。");
        }
        catch { SetStatus("無法載入班級名單，請重新下載完整班級包，或按「加入好友」輸入 IP。"); }
    }
}
