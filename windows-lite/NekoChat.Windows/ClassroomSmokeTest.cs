using System.IO;
using System.Net.Http;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NekoChat.Core;

namespace NekoChat.Windows;

internal static class ClassroomSmokeTest
{
    internal static int Run(string directory)
    {
        Directory.CreateDirectory(directory);
        try {
            var chat=new ChatWindow(new Settings());
            bool? skipped=null;
            chat.VoiceSetupChanged+=value=>skipped=value;
            chat.SkipVoiceButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if(skipped!=true || chat.VoiceSetup.IsExpanded || chat.VoiceButton.IsEnabled ||
                !chat.CreateRoomButton.IsEnabled || !chat.JoinRoomButton.IsEnabled)
                throw new InvalidDataException("Text-only onboarding did not skip optional voice setup");
            Capture(chat,Path.Combine(directory,"classroom-text-only.png"));
            var restored=new ChatWindow(new Settings{VoiceSetupSkipped=true});
            if(restored.VoiceSetup.IsExpanded || restored.VoiceButton.IsEnabled || !restored.CreateRoomButton.IsEnabled)
                throw new InvalidDataException("Restored text-only preference blocked room actions");
            restored.VoiceSetup.IsExpanded=true;
            if(!restored.VoiceSetup.IsExpanded)throw new InvalidDataException("Optional voice settings cannot be reopened");
            Task.Run(RoundtripAsync).GetAwaiter().GetResult();
            Task.Run(DirectRoundtripAsync).GetAwaiter().GetResult();
            var pet=new PetWindow();string? replyTo=null;pet.OpenChat+=from=>replyTo=from;
            pet.NotifyChat("第一位同學發言","ip-192-168-1-21","192.168.1.21");
            pet.NotifyChat("第二位同學發言","ip-192-168-1-22","192.168.1.22");
            if(pet.PendingChatCount!=1 || !pet.BubbleText.Text.Contains("192.168.1.21"))throw new InvalidDataException("Incoming class messages overwrite the visible sender");
            pet.Bubble.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if(replyTo!="ip-192-168-1-21" || !pet.BubbleText.Text.Contains("192.168.1.22"))throw new InvalidDataException("Reply did not use displayed sender or advance queue");
            pet.ClearChatNotices();
            if(pet.Bubble.Visibility!=Visibility.Collapsed || pet.PendingChatCount!=0)throw new InvalidDataException("Room exit did not clear chat notices");
            chat.SetClassroomState(true,false,"學生已加入",ownIP:"192.168.1.21",teacherIP:"192.168.1.10");
            if(chat.TeacherButton.Visibility!=Visibility.Visible)throw new InvalidDataException("Student teacher shortcut missing");
            chat.SendButton.IsEnabled=true;
            int teacherSends=0;chat.SendRequested+=()=>teacherSends++;
            chat.TeacherButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if(teacherSends!=0)throw new InvalidDataException("Teacher shortcut sent an empty message");
            chat.Draft.Text="老師我有問題";chat.Recipient.Text="192.168.1.22";
            chat.TeacherButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if(teacherSends!=1 || chat.Recipient.Text!="192.168.1.10" || chat.Draft.Text!="老師我有問題")throw new InvalidDataException("Teacher shortcut did not address the teacher");
            chat.Tabs.SelectedIndex=1;Capture(chat,Path.Combine(directory,"classroom-student-chat.png"));
            chat.SetClassroomState(false,false,"已離開");chat.Tabs.SelectedIndex=0;
            int creates=0;string? joined=null;int leaves=0;
            chat.CreateRoomRequested+=()=>creates++;
            chat.JoinRoomRequested+=code=>joined=code;
            chat.LeaveRoomRequested+=()=>leaves++;
            chat.CreateRoomButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            chat.JoinRoomCode.Text="CI-only-invitation";chat.JoinRoomButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if(creates!=1 || joined!="CI-only-invitation")throw new InvalidDataException("Room UI actions were not connected");
            Capture(chat,Path.Combine(directory,"classroom-start.png"));
            chat.RoomCode.Text="CI 流程展示（非有效邀請碼）";
            chat.SetClassroomState(true,false,"房間已建立，請私下分享邀請碼給同學。",true);
            if(chat.CreateRoomButton.IsEnabled || chat.JoinRoomButton.IsEnabled || !chat.LeaveRoomButton.IsEnabled || chat.Recipient.Visibility!=Visibility.Visible || chat.Recipient.IsReadOnly)
                throw new InvalidDataException("Room UI did not protect active pairing");
            Capture(chat,Path.Combine(directory,"classroom-host.png"));
            chat.LeaveRoomButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            chat.SetClassroomState(false,false,"已離開房間。");
            if(leaves!=1 || chat.RoomCode.Text.Length!=0 || !chat.CreateRoomButton.IsEnabled)throw new InvalidDataException("Room UI cleanup failed");
            int hosts=0;string? directIP=null;
            chat.DirectHostRequested+=()=>hosts++;
            chat.DirectJoinRequested+=ip=>directIP=ip;
            chat.DirectHostButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            chat.DirectHostIP.Text="192.168.24.116";
            chat.DirectJoinButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if(hosts!=1 || directIP!="192.168.24.116")throw new InvalidDataException("LAN IP pairing actions missing");
            chat.SetClassroomState(true,false,"LAN 已開啟",hosting:true,direct:true,ownIP:"192.168.24.116",teacherIP:"192.168.24.116");
            if(chat.DirectHostButton.IsEnabled || chat.DirectJoinButton.IsEnabled || chat.ShareRoomPanel.Visibility!=Visibility.Collapsed)
                throw new InvalidDataException("LAN IP host state exposed invitation or allowed duplicate pairing");
            chat.SetClassroomState(false,false,"已退出 IP 聊天");
            File.WriteAllText(Path.Combine(directory,"classroom-result.txt"),"PASS: optional voice skip, legacy classroom rooms, new IP-only direct pairing with explicit local host approval and real WebSocket transport, authenticated host controls, WPF direct-host/direct-join buttons, speaker settings and queued bubble replies.");
            return 0;
        }catch(Exception ex){File.WriteAllText(Path.Combine(directory,"classroom-result.txt"),"FAIL: "+ex.GetType().Name+" "+ex.Message);return 1;}
    }
    private static async Task RoundtripAsync()
    {
        using var lifetime=new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var host=await ClassroomHost.StartAsync(["127.0.0.2"],lifetime.Token);
        var invite=ClassroomInvitation.Parse(host.Invitation.Encode());
        try {
            using var http=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false,UseProxy=false});
            var endpoint=await invite.FindEndpointAsync(http,lifetime.Token);
            var session=await invite.JoinAsync(http,endpoint,lifetime.Token);
            if(session.TeacherIP!="127.0.0.2")throw new InvalidDataException("Teacher IP mismatch");
            await using var a=new RelayConnection();await using var b=new RelayConnection();
            await a.ConnectAsync(host.LocalEndpoint,host.User,host.Token,lifetime.Token);
            await b.ConnectAsync(endpoint,session.User,session.Token,lifetime.Token);
            var aReady=new TaskCompletionSource();var bReady=new TaskCompletionSource();
            var toGuest=new TaskCompletionSource<ChatEvent>();var toHost=new TaskCompletionSource<ChatEvent>();
            var aAck=new TaskCompletionSource();var bAck=new TaskCompletionSource();
            var receivingA=a.ReceiveAsync(e=>{if(e.Kind=="ready")aReady.TrySetResult();if(e.Kind=="chat.message")toHost.TrySetResult(e);if(e.Kind=="chat.ack")aAck.TrySetResult();},lifetime.Token);
            var receivingB=b.ReceiveAsync(e=>{if(e.Kind=="ready")bReady.TrySetResult();if(e.Kind=="chat.message")toGuest.TrySetResult(e);if(e.Kind=="chat.ack")bAck.TrySetResult();},lifetime.Token);
            await Task.WhenAll(aReady.Task,bReady.Task).WaitAsync(lifetime.Token);
            await a.SendAsync(session.User,"一鍵建房 🐈","host_to_guest",lifetime.Token);
            var incoming=await toGuest.Task.WaitAsync(lifetime.Token);
            if(incoming.Text!="一鍵建房 🐈" || incoming.Sender!=host.User)throw new InvalidDataException("Guest delivery failed");
            await b.SendAsync(host.User,"邀請碼加入成功","guest_to_host",lifetime.Token);
            var reply=await toHost.Task.WaitAsync(lifetime.Token);
            if(reply.Text!="邀請碼加入成功" || reply.Sender!=session.User)throw new InvalidDataException("Host delivery failed");
            await Task.WhenAll(aAck.Task,bAck.Task).WaitAsync(lifetime.Token);
            lifetime.Cancel();
            try{await Task.WhenAll(receivingA,receivingB);}catch(OperationCanceledException){ }
        }finally{await host.DisposeAsync();}
        using var probe=new Socket(AddressFamily.InterNetwork,SocketType.Stream,ProtocolType.Tcp);
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(3));
        bool gone=false;
        try{await probe.ConnectAsync(IPAddress.Loopback,invite.Port,deadline.Token);}
        catch(SocketException error)when(error.SocketErrorCode==SocketError.ConnectionRefused){gone=true;}
        if(!gone)throw new InvalidDataException("Room port remained open after owner exit");
    }
    private static async Task DirectRoundtripAsync()
    {
        using var lifetime=new CancellationTokenSource(TimeSpan.FromSeconds(40));
        await using var host=await ClassroomHost.StartAsync(["127.0.0.2"],lifetime.Token,direct:true);
        if(!host.IsDirect || host.Invitation.Port!=ClassroomDirect.Port)
            throw new InvalidDataException("Direct host port not initialized");
        using var http=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false,UseProxy=false});
        var url=$"http://127.0.0.2:{ClassroomDirect.Port}/nekochat/v1/classroom/request";
        using var start=await http.PostAsync(url,null,lifetime.Token);
        if(start.StatusCode!=HttpStatusCode.OK)throw new InvalidDataException("Direct pairing request failed");
        using var requestJson=JsonDocument.Parse(await start.Content.ReadAsStringAsync(lifetime.Token));
        string id=requestJson.RootElement.GetProperty("request_id").GetString()??"";
        var pending=await host.PendingRequestsAsync(http,lifetime.Token);
        if(pending.Count!=1 || pending[0].Id!=id || pending[0].Ip!="127.0.0.1")
            throw new InvalidDataException("Direct request did not reach the real host");
        using(var wait=await http.GetAsync(url+"/"+id,lifetime.Token)){
            if(wait.StatusCode!=HttpStatusCode.Accepted)throw new InvalidDataException("Approval was bypassed");
        }
        await host.DecideAsync(http,id,true,lifetime.Token);
        string guestUser,guestToken;
        using(var result=await http.GetAsync(url+"/"+id,lifetime.Token)){
            if(result.StatusCode!=HttpStatusCode.OK)throw new InvalidDataException("Approved request was not redeemed");
            using var data=JsonDocument.Parse(await result.Content.ReadAsStringAsync(lifetime.Token));
            guestUser=data.RootElement.GetProperty("user").GetString()??"";
            guestToken=data.RootElement.GetProperty("token").GetString()??"";
            if(guestUser!="ip-127-0-0-1" || guestToken.Length!=43)
                throw new InvalidDataException("Approved identity was not bound to TCP source IP");
        }
        await using var a=new RelayConnection();await using var b=new RelayConnection();
        await a.ConnectAsync(host.LocalEndpoint,host.User,host.Token,lifetime.Token);
        var guestEndpoint=RelayConnection.ValidateEndpoint($"ws://127.0.0.2:{ClassroomDirect.Port}/nekochat/v1/ws",true);
        await b.ConnectAsync(guestEndpoint,guestUser,guestToken,lifetime.Token);
        var guestReady=new TaskCompletionSource();
        var hostReady=new TaskCompletionSource();
        var received=new TaskCompletionSource<ChatEvent>();
        var hostRecv=a.ReceiveAsync(e=>{
            if(e.Kind=="ready")hostReady.TrySetResult();
            if(e.Kind=="chat.message")received.TrySetResult(e);
        },lifetime.Token);
        var guestRecv=b.ReceiveAsync(e=>{if(e.Kind=="ready")guestReady.TrySetResult();},lifetime.Token);
        await Task.WhenAll(hostReady.Task,guestReady.Task).WaitAsync(lifetime.Token);
        await b.SendAsync(host.User,"LAN IP 核准後傳訊成功","direct_smoke",lifetime.Token);
        var message=await received.Task.WaitAsync(lifetime.Token);
        if(message.Sender!=guestUser || message.Text!="LAN IP 核准後傳訊成功")
            throw new InvalidDataException("Direct pairing message delivery failed");
        lifetime.Cancel();
        try{await Task.WhenAll(hostRecv,guestRecv);}catch(OperationCanceledException){ }
    }

    private static void Capture(ChatWindow chat,string path)
    {
        var visual=(FrameworkElement)chat.Content;visual.Measure(new Size(430,650));visual.Arrange(new Rect(0,0,430,650));visual.UpdateLayout();
        var bitmap=new RenderTargetBitmap(430,650,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(path);encoder.Save(file);
    }
}
