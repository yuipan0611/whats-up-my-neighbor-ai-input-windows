using System.Windows;
using System.Windows.Controls;
using NekoChat.Core.Storage;
using NekoChat.Core.Roster;

namespace NekoChat.Windows;
public partial class ChatWindow
{
    private bool _readNoticeShowing;
    private bool _teacherProjectionPending;
    private async Task SyncTeacherProjectionAsync()
    {
        if(_runtime is not {ArchivedIdentityFixture:false} runtime)return;
        string ip=await runtime.Worker.Run(s=>s.GetClassroomTeacherIp())??"";
        if(!ReferenceEquals(runtime,_runtime)||_closingRequested)return;
        if(settings.TeacherIp!=ip||_teacherProjectionPending) _teacherProjectionPending=!UpdateSettings(settings with{TeacherIp=ip});
        TeacherIpBox.Text=ip; RefreshReactionTargets();
        if (_teacherProjectionPending) ReloadData.Visibility = Visibility.Visible;
    }
    private async Task ShowReadReceiptNoticeAsync()
    {
        if(_readNoticeShowing||!IsVisible||_closingRequested||_runtime is not{ArchivedIdentityFixture:false} runtime)return;
        _readNoticeShowing=true;
        try{
            var preference=await runtime.Worker.Run(s=>s.GetReadReceiptPreferences());
            if(preference.NoticeShown||!IsVisible||!ReferenceEquals(runtime,_runtime)||_closingRequested)return;
            ContactPrompt("已讀回條現在預設開啟，可在設定關閉。已送出的回條無法撤回。","已讀回條",MessageBoxButton.OK,MessageBoxImage.Information);
            if(ReferenceEquals(runtime,_runtime))await runtime.Worker.Run(s=>{s.AcknowledgeReadReceiptNotice();return true;});
        }catch(Exception error){ShowStorageError(error);}finally{_readNoticeShowing=false;}
    }

    private ContextMenu ContactMenu(InboxEntry entry)
    {
        var menu=new ContextMenu();
        void Item(string label,bool enabled,Func<Task> action){var item=new MenuItem{Header=label,IsEnabled=enabled};item.Click+=async(_,_)=>{try{await action();}catch(Exception error){ShowStorageError(error);}};menu.Items.Add(item);}
        bool active=_runtime is{ArchivedIdentityFixture:false}&&!_closingRequested;
        Item("編輯本機備註…",active&&entry.RosterId is not null&&!entry.Removed,()=>{RenameNicknameClick(new MenuItem{CommandParameter=new InboxRowView(entry,StateOf(entry))},new RoutedEventArgs());return Task.CompletedTask;});
        Item("編輯 IP…",active&&entry.RosterId is not null&&!entry.Removed&&!entry.Blocked,async()=>{await SelectEntryAsync(entry,true);SettingsPane.Visibility=Visibility.Visible;ContactSettingsPanel.Visibility=Visibility.Visible;ViewChanged();await LoadManualFormAsync(entry.RosterId);});
        if(entry.RosterId is null)Item("加入聯絡人…",active&&!entry.Blocked&&entry.Endpoint is not null,()=>AddContactDialogAsync(false,entry.Endpoint));
        else Item("確認為一般聯絡人…",active&&!entry.Removed&&!entry.Blocked,()=>ManageContactAsync(entry,"confirm"));
        Item(entry.IsTeacher?"取消老師身分…":"設為老師…",active&&!entry.Removed&&!entry.Blocked&&entry.Endpoint is not null,()=>ManageContactAsync(entry,"teacher"));
        Item("移除聯絡人…",active&&entry.RosterId is not null&&!entry.Removed&&!entry.Blocked,()=>ManageContactAsync(entry,"remove"));
        menu.Items.Add(new Separator());
        Item(entry.Blocked?"解除封鎖…":"封鎖…",active&&entry.Endpoint is not null,()=>ManageContactAsync(entry,"block"));
        menu.Items.Add(new Separator());
        Item("清除此對話…",active,async()=>{await SelectEntryAsync(entry,true);if(_selected?.Id==entry.Id)ClearConversationClick(this,new RoutedEventArgs());});
        return menu;
    }
    private async Task ManageContactAsync(InboxEntry entry,string action)
    {
        if(_runtime is not{ArchivedIdentityFixture:false} runtime||_closingRequested)return;
        var state=await runtime.Worker.Run(s=>(Rows:s.GetRosterSnapshot(),Teacher:s.GetClassroomTeacherIp()));
        string token=RosterImportPreview.ComputeSnapshotToken(state.Rows);
        if(action=="confirm"&&state.Rows.FirstOrDefault(r=>r.Entry.Id==entry.RosterId)?.Source!="auto"){SetStatus("這位對象已是一般聯絡人。");return;}
        var preview=action=="remove"&&entry.RosterId is{} id?await runtime.Worker.Run(s=>s.PreviewRosterDelete(id)):null;
        string text=action switch{
            "teacher"=>entry.IsTeacher?"取消老師身分後，對話與歷史保留為一般聯絡人。":state.Teacher is{} ip&&ip!=entry.Endpoint?$"將取代目前的老師 {ip}；原老師保留為一般聯絡人。":"將此聯絡人設為老師並置頂。",
            "remove"=>"只從這台電腦的名單移除，歷史會保留在封存區，不會封鎖對方。",
            "confirm"=>"確認為一般聯絡人後，可自動接收小附件並參與課堂檔案備援。名稱與 IP 不代表身分驗證。",
            _=>entry.Blocked?"解除封鎖後可再次收訊；已停止的待送不會自動重送，可手動重送。":(entry.IsTeacher?"這會取消此對象的老師身分。\n":"")+"封鎖後不再接收此 IP 的訊息與檔案；對方不會收到封鎖通知，可隨時解除。"
        };
        if(ContactPrompt(text,entry.DisplayName,MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No)!=MessageBoxResult.Yes)return;
        if(!ReferenceEquals(runtime,_runtime)||_closingRequested)return;
        await FlushDraftsAsync();
        var result=await runtime.Worker.Run(s=>action switch{
            "teacher"=>entry.IsTeacher?s.ClearClassroomTeacher(entry.RosterId!,token):s.SetClassroomTeacherByIp(entry.Endpoint!,token),
            "remove"=>preview is{CanDelete:true}?s.DeleteRosterEntry(entry.RosterId!,preview.SnapshotToken,preview.ToConfirmation()):new RosterMutationResult(false,"not-found",entry.RosterId,token),
            "confirm"=>s.ConfirmAutomaticContact(entry.RosterId!,token),
            _=>s.SetSourceBlocked(entry.Endpoint!,!entry.Blocked,token)
        });
        if(!result.Changed&&result.Reason!="unchanged"){SetStatus(RosterFailure(result.Reason));return;}
        await runtime.InvalidatePairingAsync("contact_management");
        await SyncTeacherProjectionAsync();ResetRecipientProtocolHints();await RefreshInboxAsync(true);
        InboxFilter.SelectedIndex=action=="remove"?2:action=="block"&&!entry.Blocked?3:0;
        var updated=_snapshot?.Entries.FirstOrDefault(e=>e.Id==entry.Id||entry.Endpoint is not null&&e.Endpoint==entry.Endpoint);
        if(updated is not null)await SelectEntryAsync(updated,true);
        SetStatus(action=="remove"?"聯絡人已移到封存，歷史保留。":action=="block"?entry.Blocked?"已解除封鎖；待送需手動重送。":"已封鎖此 IP，歷史保留；待送已停止。":"聯絡人已更新。");
    }
}
