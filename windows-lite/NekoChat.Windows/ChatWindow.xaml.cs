using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace NekoChat.Windows;

public partial class ChatWindow : Window
{
    private Settings settings;
    private bool loaded;
    private string? teacherAddress;
    internal event Action? SendRequested;
    internal event Action? VoiceRequested;
    internal event Action? CancelRequested;
    internal event Action<Settings,string>? ConnectRequested;
    internal event Action? DisconnectRequested;
    internal event Action? CreateRoomRequested;
    internal event Action? DirectHostRequested;
    internal event Action<string>? DirectJoinRequested;
    internal event Action<string>? JoinRoomRequested;
    internal event Action? LeaveRoomRequested;
    internal event Action<bool>? VoiceSetupChanged;
    internal ChatWindow(Settings initial)
    {
        settings=initial;InitializeComponent();Mode.SelectedIndex=initial.Lan?0:1;
        Endpoint.Text=initial.Endpoint;UserId.Text=initial.User;loaded=true;
        Closing+=OnClosing;
        if(initial.VoiceSetupSkipped)SetVoiceSetupSkipped(true);
        else {
            try{GroqState.Text=string.IsNullOrWhiteSpace(CredentialStore.Load(CredentialStore.Groq))?"尚未設定語音。可以直接建立或加入房間，互傳文字訊息。":"語音金鑰已設定，可以在記事本按 Ctrl + Alt + Space 測試。";}
            catch(Exception){GroqState.Text="目前無法讀取語音金鑰；可以先跳過，使用文字聊天。";}
        }
    }
    private void OnClosing(object? sender,CancelEventArgs e){e.Cancel=true;Hide();}
    internal void ShowChat(string? sender=null)
    {
        if(sender is not null){Recipient.Text=sender;Tabs.SelectedIndex=1;}
        Show();if(WindowState==WindowState.Minimized)WindowState=WindowState.Normal;Activate();
    }
    internal void SetStatus(string status)=>Status.Text=status;
    internal void AddHistory(string text)
    {
        History.AppendText(text+Environment.NewLine);
        if(History.Text.Length>24000)History.Text=History.Text[^20000..];
        History.ScrollToEnd();
    }
    private void SendClick(object sender,RoutedEventArgs e)=>SendRequested?.Invoke();
    private void VoiceClick(object sender,RoutedEventArgs e)=>VoiceRequested?.Invoke();
    private void CancelClick(object sender,RoutedEventArgs e)=>CancelRequested?.Invoke();
    private void DisconnectClick(object sender,RoutedEventArgs e)=>DisconnectRequested?.Invoke();
    private void ModeChanged(object sender,SelectionChangedEventArgs e)
    {
        if(!loaded)return;
        // Keep independent LAN/VPS endpoints while switching the selector.
        settings=settings.Lan?settings with{LanEndpoint=Endpoint.Text}:settings with{VpsEndpoint=Endpoint.Text};
        settings=settings with{Lan=Mode.SelectedIndex==0};Endpoint.Text=settings.Endpoint;
    }
    private void ConnectClick(object sender,RoutedEventArgs e)
    {
        settings=settings with{Lan=Mode.SelectedIndex==0,User=UserId.Text.Trim()};
        settings=settings.Lan?settings with{LanEndpoint=Endpoint.Text.Trim()}:settings with{VpsEndpoint=Endpoint.Text.Trim()};
        var token=RelayToken.Password;RelayToken.Clear();ConnectRequested?.Invoke(settings,token);
    }
    private void SaveGroqClick(object sender,RoutedEventArgs e)
    {
        try {
            if(string.IsNullOrWhiteSpace(GroqKey.Password))throw new ArgumentException("請先輸入自己的 Groq Key。");
            CredentialStore.Save(CredentialStore.Groq,GroqKey.Password.Trim());SetVoiceSetupSkipped(false);VoiceSetupChanged?.Invoke(false);SetStatus("Groq Key 已存入 Windows 認證管理員。");GroqState.Text="語音金鑰已設定，可以在記事本按 Ctrl + Alt + Space 測試。";
        }catch(Exception ex){SetStatus(App.SafeError(ex));}finally{GroqKey.Clear();}
    }
    private void DeleteGroqClick(object sender,RoutedEventArgs e)
    {
        try{CredentialStore.Delete(CredentialStore.Groq);GroqKey.Clear();SetStatus("Groq Key 已刪除。");GroqState.Text="尚未設定語音金鑰。文字聊天仍可使用。";}catch(Exception ex){SetStatus(App.SafeError(ex));}
    }
    private void DeleteRelayClick(object sender,RoutedEventArgs e)
    {
        try{CredentialStore.Delete(CredentialStore.Relay(NekoChat.Core.RelayConnection.ValidateEndpoint(Endpoint.Text.Trim(),Mode.SelectedIndex==0).AbsoluteUri,UserId.Text.Trim()));RelayToken.Clear();SetStatus("已刪除此連線的憑證；現有連線請另按中斷。");}catch(Exception ex){SetStatus(App.SafeError(ex));}
    }
    private void CopyClick(object sender,RoutedEventArgs e)
    {
        if(Transcript.Text.Length>0)SetStatus(NativeInput.Copy(Transcript.Text));
    }
    internal void SetClassroomState(bool active,bool busy,string text,bool hosting=false,string? ownIP=null,string? teacherIP=null,bool direct=false)
    {
        CreateRoomButton.IsEnabled=!active&&!busy;JoinRoomButton.IsEnabled=!active&&!busy;
        DirectHostButton.IsEnabled=!active&&!busy;DirectJoinButton.IsEnabled=!active&&!busy;
        DirectHostIP.IsEnabled=!active&&!busy;
        JoinRoomCode.IsEnabled=!active&&!busy;LeaveRoomButton.IsEnabled=active||busy;
        ShareRoomPanel.Visibility=hosting&&!direct?Visibility.Visible:Visibility.Collapsed;
        ConnectButton.IsEnabled=!active&&!busy;RoomStatus.Text=text;
        Recipient.IsReadOnly=false;Recipient.Visibility=Visibility.Visible;
        RecipientLabel.Text=active?"對方內網 IP（例如 192.168.1.23）":"好友 ID";
        if(ownIP is not null){MyRoomAddress.Text="我的教室 IP："+ownIP+(teacherIP is not null?"　老師："+teacherIP:"");MyRoomAddress.Visibility=Visibility.Visible;}
        else if(!active&&!busy)MyRoomAddress.Visibility=Visibility.Collapsed;
        teacherAddress=active?teacherIP:null;
        TeacherButton.Visibility=active && teacherIP is not null && teacherIP!=ownIP?Visibility.Visible:Visibility.Collapsed;
        if(!active&&!busy){RoomCode.Clear();JoinRoomCode.Clear();}
    }
    private void PingTeacherClick(object sender,RoutedEventArgs e)
    {
        if(teacherAddress is null)return;
        Recipient.Text=teacherAddress;
        if(string.IsNullOrWhiteSpace(Draft.Text)){SetStatus("請先輸入想對老師說的話，再按 Ping 老師。");Draft.Focus();return;}
        SendRequested?.Invoke();
    }
    private void CreateRoomClick(object sender,RoutedEventArgs e)=>CreateRoomRequested?.Invoke();
    private void DirectHostClick(object sender,RoutedEventArgs e)=>DirectHostRequested?.Invoke();
    private void DirectJoinClick(object sender,RoutedEventArgs e)=>DirectJoinRequested?.Invoke(DirectHostIP.Text.Trim());
    private void JoinRoomClick(object sender,RoutedEventArgs e)=>JoinRoomRequested?.Invoke(JoinRoomCode.Text);
    private void LeaveRoomClick(object sender,RoutedEventArgs e)=>LeaveRoomRequested?.Invoke();
    private void CopyRoomCodeClick(object sender,RoutedEventArgs e)
    {
        if(RoomCode.Text.Length>0)SetStatus(NativeInput.Copy(RoomCode.Text,"已複製房間邀請碼，請私下交給同學"));
    }

    internal void SetVoiceSetupSkipped(bool skipped)
    {
        settings=settings with{VoiceSetupSkipped=skipped};
        VoiceSetup.IsExpanded=!skipped;VoiceButton.IsEnabled=!skipped;
        SkipVoiceButton.IsEnabled=!skipped;
        if(skipped){GroqKey.Clear();GroqState.Text="已跳過語音設定。可以直接建立／加入房間，互傳文字；之後有麥克風再展開設定即可。";}
    }
    private void SkipVoiceClick(object sender,RoutedEventArgs e)
    {
        SetVoiceSetupSkipped(true);VoiceSetupChanged?.Invoke(true);
        SetStatus("已跳過語音，不需要麥克風或 Groq Key。請直接建立或加入房間。");
        RoomHeading.BringIntoView();
    }

}
