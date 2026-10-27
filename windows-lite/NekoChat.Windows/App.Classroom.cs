using System.IO;
using System.Net.Http;
using System.Windows;
using NekoChat.Core;

namespace NekoChat.Windows;

public partial class App
{
    private ClassroomHost? classroomHost;
    private string? classroomUser;
    private string? classroomTeacherIP;
    private CancellationTokenSource? classroomCancel;
    private CancellationTokenSource? classroomApprovals;
    private bool classroomBusy;
    private readonly HttpClient roomHttp=new(new HttpClientHandler{AllowAutoRedirect=false,UseProxy=false}){Timeout=TimeSpan.FromSeconds(20)};
    private void InitializeClassroom()
    {
        // Credentials from a crashed old room are useless after its owning Relay exits.
        try{CredentialStore.Delete(CredentialStore.Classroom);}catch(Exception){ }
        chat.CreateRoomRequested+=async()=>await CreateClassroomAsync();
        chat.JoinRoomRequested+=async code=>await JoinClassroomAsync(code);
        chat.DirectHostRequested+=async()=>await CreateClassroomAsync(true);
        chat.DirectJoinRequested+=async ip=>await JoinDirectAsync(ip);
        chat.LeaveRoomRequested+=async()=> {
            if(classroomBusy){classroomCancel?.Cancel();chat.RoomStatus.Text="正在取消房間連線…";}
            else await LeaveClassroomAsync();
        };
    }
    private async Task CreateClassroomAsync(bool direct=false)
    {
        if(classroomBusy)return;
        classroomBusy=true;
        try {
            await LeaveClassroomAsync();
            classroomCancel=new CancellationTokenSource(TimeSpan.FromSeconds(30));
            chat.SetClassroomState(false,true,"正在建立房間…");
            classroomHost=await ClassroomHost.StartAsync(ClassroomHost.LocalAddresses(),classroomCancel.Token,direct);
            classroomUser=classroomHost.User;classroomTeacherIP=ClassroomInvitation.IPForUser(classroomUser);
            chat.RoomCode.Text=classroomHost.Invitation.Encode();
            chat.Recipient.Clear();
            chat.SetClassroomState(true,false,direct?"LAN IP 聊天已開啟，準備接受同學請求…":"教室房間已啟動，正在連線…",hosting:true,ownIP:classroomTeacherIP,teacherIP:classroomTeacherIP,direct:direct);
            if(!await ConnectAsync(settings with{Lan=true,LanEndpoint=classroomHost.LocalEndpoint.AbsoluteUri,User=classroomHost.User},classroomHost.Token,true))
                await LeaveClassroomAsync("建立連線失敗，請確認 Windows 網路權限後重試。");
            else if(direct) {
                classroomApprovals=new CancellationTokenSource();
                _=ApprovalLoopAsync(classroomHost,classroomApprovals.Token);
            }
        }catch(Exception ex){await LeaveClassroomAsync(RoomError(ex));}
        finally{classroomBusy=false;}
    }
    private async Task JoinClassroomAsync(string code)
    {
        if(classroomBusy)return;
        classroomBusy=true;
        try {
            var invite=ClassroomInvitation.Parse(code);
            await LeaveClassroomAsync();
            classroomCancel=new CancellationTokenSource(TimeSpan.FromSeconds(25));
            chat.SetClassroomState(false,true,"正在尋找同學的房間…");
            var endpoint=await invite.FindEndpointAsync(roomHttp,classroomCancel.Token);
            var session=await invite.JoinAsync(roomHttp,endpoint,classroomCancel.Token);
            classroomUser=session.User;classroomTeacherIP=session.TeacherIP;chat.Recipient.Text=session.TeacherIP;chat.JoinRoomCode.Clear();
            chat.SetClassroomState(true,false,"已找到教室，正在驗證連線…",ownIP:session.Ip,teacherIP:session.TeacherIP);
            if(await ConnectAsync(settings with{Lan=true,LanEndpoint=endpoint.AbsoluteUri,User=session.User},session.Token,true)){chat.Tabs.SelectedIndex=1;chat.ShowChat();chat.Draft.Focus();}
            else await LeaveClassroomAsync("加入失敗，請向房主取得新的邀請碼後重試。");
        }catch(Exception ex){await LeaveClassroomAsync(RoomError(ex));}
        finally{classroomBusy=false;}
    }
    private async Task JoinDirectAsync(string ip)
    {
        if(classroomBusy)return;
        classroomBusy=true;
        try {
            await LeaveClassroomAsync();
            classroomCancel=new CancellationTokenSource(TimeSpan.FromSeconds(90));
            chat.SetClassroomState(false,true,$"正在向 {ip} 發送 LAN 連線請求；請對方在貓咪視窗按「接受」。");
            var (endpoint,session)=await ClassroomDirect.JoinAsync(roomHttp,ip,classroomCancel.Token);
            classroomUser=session.User;classroomTeacherIP=session.TeacherIP;
            chat.Recipient.Text=session.TeacherIP;
            chat.SetClassroomState(true,false,"對方已接受，正在建立連線…",ownIP:session.Ip,teacherIP:session.TeacherIP);
            if(await ConnectAsync(settings with{Lan=true,LanEndpoint=endpoint.AbsoluteUri,User=session.User},session.Token,true)){
                chat.Tabs.SelectedIndex=1;chat.ShowChat();chat.Draft.Focus();
            }else await LeaveClassroomAsync("取得核准但連線失敗，請再試一次。");
        }catch(Exception ex){await LeaveClassroomAsync(RoomError(ex));}
        finally{classroomBusy=false;}
    }
    private async Task ApprovalLoopAsync(ClassroomHost host,CancellationToken cancellation)
    {
        try {
            while(!cancellation.IsCancellationRequested && classroomHost==host){
                try {
                    foreach(var (id,ip) in await host.PendingRequestsAsync(roomHttp,cancellation)){
                        if(cancellation.IsCancellationRequested || classroomHost!=host)break;
                        bool accept=MessageBox.Show(chat,
                            $"教室同學 {ip} 想連線到你的貓咪聊天。\\n\\n是否允許加入？\\n（只有接受後才會發放聊天憑證）",
                            "NekoChat · LAN 連線請求",MessageBoxButton.YesNo,MessageBoxImage.Question)==MessageBoxResult.Yes;
                        await host.DecideAsync(roomHttp,id,accept,cancellation);
                        chat.SetStatus(accept?$"已允許 {ip} 加入 LAN 聊天。":$"已拒絕 {ip} 的連線請求。");
                    }
                }catch(OperationCanceledException)when(cancellation.IsCancellationRequested){break;}
                catch(Exception){chat.SetStatus("暫時無法處理 LAN 連線請求，請確認房間服務仍正常運作。");}
                await Task.Delay(TimeSpan.FromSeconds(2),cancellation);
            }
        }catch(OperationCanceledException)when(cancellation.IsCancellationRequested){ }
    }

    private void ClassroomReady()
    {
        string message=classroomHost is null?"已連線！可以 Ping 房主，也可以填同學 IP 私訊。":
            classroomHost.IsDirect?$"IP 聊天已開啟！請同學輸入 {classroomTeacherIP} 並等待你接受。":
            "教室已建立。分享邀請碼給全班，學生的訊息會顯示在聊天頁及貓咪氣泡。";
        chat.SetClassroomState(true,false,message,classroomHost is not null,ClassroomInvitation.IPForUser(classroomUser!),classroomTeacherIP,
            direct:classroomHost?.IsDirect??false);
    }
    private async Task LeaveClassroomAsync(string message="已離開房間，可以重新建立或加入房間。")
    {
        classroomApprovals?.Cancel();classroomApprovals?.Dispose();classroomApprovals=null;
        classroomCancel?.Cancel();classroomCancel?.Dispose();classroomCancel=null;
        await DisconnectAsync();
        var host=classroomHost;classroomHost=null;classroomUser=null;classroomTeacherIP=null;pet.ClearChatNotices();
        if(host is not null)await host.DisposeAsync();
        try{CredentialStore.Delete(CredentialStore.Classroom);}catch(Exception){ }
        chat.SetClassroomState(false,false,message);
    }
    private static string RoomError(Exception ex)=>ex switch {
        OperationCanceledException=>"已取消或連線逾時，請確認同學仍開著房間後再試一次。",
        InvalidDataException=>ex.Message,
        _=>"房間無法啟動或連線。請確認使用完整安裝包、教室網路可互連，並依學校規範允許 Windows 網路存取。"
    };
    private void DisposeClassroom()
    {
        classroomApprovals?.Cancel();classroomCancel?.Cancel();classroomHost?.Stop();
        try{CredentialStore.Delete(CredentialStore.Classroom);}catch(Exception){ }
        roomHttp.Dispose();
    }
}
