using System.IO;
using System.Net.WebSockets;
using System.Windows;
using Microsoft.Win32;
using NekoChat.Core;

namespace NekoChat.Windows;

public partial class App : Application
{
    private Settings settings=new();
    private PetWindow pet=null!;
    private ChatWindow chat=null!;
    private RelayConnection? relay;
    private DirectLanListener? directListener;
    private CancellationTokenSource? connectionCancel;
    private bool ready;
    private (string Id,string To,string Text)? pending;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if(e.Args.Length==2 && e.Args[0]=="--room-smoke-test") {Shutdown(ClassroomSmokeTest.Run(e.Args[1]));return;}
        if(e.Args.Length==2 && e.Args[0]=="--smoke-test") {Shutdown(SmokeTest.Run(e.Args[1]));return;}
        try {
            settings=Settings.Load();
            bool textOnlySmoke=e.Args.Length==2 && e.Args[0]=="--text-only-startup-smoke-test";
            if(textOnlySmoke)settings=settings with{VoiceSetupSkipped=true};
            pet=new PetWindow();chat=new ChatWindow(settings);
            pet.OpenChat+=sender=>chat.ShowChat(sender is not null && classroomUser is not null?ClassroomInvitation.IPForUser(sender):sender);
            pet.SelectCoat+=id=> {
                try{pet.SetPreset(id);settings=settings with{Coat=id};settings.Save();}
                catch(Exception ex){chat.SetStatus(SafeError(ex));}
            };
            pet.ImportCoat+=()=> {
                var picker=new OpenFileDialog{Filter="貓咪設定 JSON|*.json",Title="選擇 cat.config.json"};
                if(picker.ShowDialog(chat)!=true)return;
                try{pet.SetArt(CatArtwork.FromConfig(picker.FileName));chat.SetStatus("已載入自訂外觀；此次啟動有效。內建花色會自動記住。");}
                catch(Exception ex){chat.SetStatus(SafeError(ex));}
            };
            pet.CheckUpdates+=()=>_=CheckForUpdatesAsync(true);
            chat.ConnectRequested+=async(s,t)=>{await LeaveClassroomAsync();await ConnectAsync(s,t);};
            chat.DisconnectRequested+=async()=>{if(classroomHost is not null || classroomUser is not null)await LeaveClassroomAsync();else await DisconnectAsync();};
            chat.SendRequested+=async()=>await SendAsync();
            directListener=new DirectLanListener();
            directListener.MessageReceived+=(senderIp,text)=>Dispatcher.Invoke(()=>{
                chat.AddHistory($"{senderIp}：{text}");
                pet.NotifyChat(text,senderIp,senderIp);
            });
            directListener.Start();
            if(Appearance.Presets.Contains(settings.Coat))pet.SetPreset(settings.Coat);
            pet.Show();InitializeVoice();InitializeClassroom();chat.Show();
            if(e.Args.Length==0)Task.Delay(3000).ContinueWith(_=>Dispatcher.Invoke(()=>_=CheckForUpdatesAsync(false)));
            if(e.Args.Length==2 && e.Args[0] is "--startup-smoke-test" or "--text-only-startup-smoke-test")
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle,new Action(()=> {
                    Directory.CreateDirectory(e.Args[1]);
                    bool visible=pet.IsVisible && chat.IsVisible;
                    bool optionalVoice=!textOnlySmoke || settings.VoiceSetupSkipped && hotkeys is null && recorder is null &&
                        !chat.VoiceButton.IsEnabled && chat.CreateRoomButton.IsEnabled && chat.JoinRoomButton.IsEnabled;
                    File.WriteAllText(Path.Combine(e.Args[1],"startup-result.txt"),visible&&optionalVoice?
                        (textOnlySmoke?"PASS: text-only startup without voice hotkeys/microphone or Groq-key access; create/join remain available.":"PASS: actual App startup, visible desktop cat and onboarding, hotkey initialization and clean shutdown."):
                        "FAIL: initial windows or optional voice state were incorrect.");
                    Shutdown(visible&&optionalVoice?0:1);
                }));
        }catch(Exception){MessageBox.Show("NekoChat 無法啟動，請確認所有解壓縮檔案都在同一資料夾。","NekoChat");Shutdown(1);}
    }
    private async Task<bool> ConnectAsync(Settings next,string suppliedToken,bool classroom=false)
    {
        try {
            var endpoint=RelayConnection.ValidateEndpoint(next.Endpoint,next.Lan);
            if(!Protocol.ValidUser(next.User))throw new ArgumentException("請填有效的我的帳號 ID。");
            var target=classroom?CredentialStore.Classroom:CredentialStore.Relay(endpoint.AbsoluteUri,next.User);
            if(!string.IsNullOrWhiteSpace(suppliedToken))CredentialStore.Save(target,suppliedToken.Trim());
            var token=CredentialStore.Load(target)??throw new ArgumentException("請先輸入此帳號的個人 Relay 憑證。");
            await DisconnectAsync();
            if(!classroom){settings=next with{Coat=settings.Coat};settings.Save();}
            var local=new RelayConnection();relay=local;connectionCancel=new();
            chat.ConnectButton.IsEnabled=false;chat.SetStatus("正在連線與驗證…");
            _=RunConnectionAsync(local,endpoint,next.User,token,connectionCancel.Token,next.Lan,classroom);
            return true;
        }catch(Exception ex){chat.SetStatus(SafeError(ex));return false;}
    }
    private async Task RunConnectionAsync(RelayConnection local,Uri endpoint,string user,string token,CancellationToken cancel,bool lan,bool classroom)
    {
        try {
            await local.ConnectAsync(endpoint,user,token,cancel);
            await local.ReceiveAsync(message=>Dispatcher.Invoke(()=> {
                if(relay!=local)return;
                if(message.Kind=="ready") {
                    if(ready || message.User!=user)throw new InvalidDataException("Relay 身分確認不符。");
                    ready=true;chat.SendButton.IsEnabled=true;if(classroom)ClassroomReady();chat.SetStatus("已連線 · "+(lan?"LAN":"VPS WSS"));return;
                }
                if(!ready)throw new InvalidDataException("Relay 尚未完成身分確認。");
                switch(message.Kind) {
                    case "chat.message":
                        // Incoming text only enters display controls. Never call dictation or input APIs.
                        var senderName=classroom?ClassroomInvitation.IPForUser(message.Sender!):message.Sender!;
                        chat.AddHistory($"{senderName}：{message.Text}");
                        pet.NotifyChat(message.Text!,message.Sender!,senderName);break;
                    case "chat.ack" when pending is { } sent && sent.Id==message.Id:
                        chat.AddHistory($"我 → {(classroom?ClassroomInvitation.IPForUser(sent.To):sent.To)}：{sent.Text}");
                        if(chat.Draft.Text==sent.Text && chat.Recipient.Text.Trim()==(classroom?ClassroomInvitation.IPForUser(sent.To):sent.To))chat.Draft.Clear();
                        pending=null;chat.SendButton.IsEnabled=true;chat.SetStatus("已送達對方連線（非已讀）。");break;
                    case "error":
                        if(pending?.Id==message.Id){pending=null;chat.SendButton.IsEnabled=true;}
                        chat.SetStatus(message.Code switch{"offline"=>"朋友目前離線；草稿已保留。","forbidden"=>classroom?"這個 IP 尚未加入同一教室，或選到了自己；請確認對方 IP 與房間。":"尚未取得雙向好友授權。","rate_limited"=>"傳送過於頻繁，請稍後重新連線。",_=>"Relay 拒絕訊息格式。"});break;
                }
            }),cancel);
        }catch(Exception ex){if(relay==local)chat.SetStatus(SafeError(ex));}
        finally {
            if(relay==local) {
                relay=null;ready=false;chat.SendButton.IsEnabled=true;chat.ConnectButton.IsEnabled=true;
                if(pending is not null){pending=null;chat.SetStatus("連線中斷，上一則送達狀態不確定；草稿保留，請先向朋友確認。");}
                else if(!cancel.IsCancellationRequested)chat.SetStatus(chat.Status.Text+" 已中斷，請重新連線。");
                if(classroom)_=LeaveClassroomAsync("房間連線已中斷，請重新建立或加入房間。");
            }
            await local.DisposeAsync();
        }
    }
    private async Task DisconnectAsync()
    {
        connectionCancel?.Cancel();connectionCancel?.Dispose();connectionCancel=null;
        var old=relay;relay=null;ready=false;chat.SendButton.IsEnabled=true;chat.ConnectButton.IsEnabled=true;
        chat.SetStatus(pending is null?"已中斷連線。":"已中斷；上一則送達狀態不確定，草稿保留。");pending=null;
        if(old is not null)await old.DisposeAsync();
    }
    private async Task SendAsync()
    {
        var recipient=chat.Recipient.Text.Trim();
        var text=chat.Draft.Text;
        if(string.IsNullOrWhiteSpace(text)){chat.SetStatus("請先輸入訊息文字。");return;}
        if(string.IsNullOrWhiteSpace(recipient)){chat.SetStatus("請輸入收件者的內網 IP（例如 192.168.1.25）或好友 ID。");return;}

        bool isDirectIP=LanInvitation.PrivateIPv4(recipient);
        if(relay is null) {
            if(!isDirectIP) {
                chat.SetStatus("未加入房間。請輸入對方的內網 IP（例如 192.168.1.25）即可直連傳訊。");
                return;
            }
            var ownIps=LanInvitation.LocalAddresses();
            if(ownIps.Contains(recipient)) {
                chat.SetStatus("這是自己的 IP，請填對方的內網 IP。");
                return;
            }
            chat.SendButton.IsEnabled=false;chat.SetStatus($"正在直連傳送至 {recipient}…");
            try {
                await DirectLanMessaging.SendDirectAsync(recipient,text);
                chat.AddHistory($"我 → {recipient}：{text}");
                chat.Draft.Clear();
                chat.SetStatus($"已直連送達 {recipient}！");
            } catch(Exception ex) {
                chat.SetStatus(SafeError(ex)+"（草稿保留）");
            } finally {
                chat.SendButton.IsEnabled=true;
            }
            return;
        }

        if(!ready || pending is not null)return;
        try {
            var to=classroomUser is not null?ClassroomInvitation.UserForIP(recipient):recipient;
            if(classroomUser==to)throw new ArgumentException("這是自己的 IP，請填要傳訊的同學 IP。");
            var id=Guid.NewGuid().ToString("N");
            _=Protocol.Send(to,text,id);
            pending=(id,to,text);chat.SendButton.IsEnabled=false;chat.SetStatus("等待 Relay 送達回條…");
            await relay.SendAsync(to,text,id,connectionCancel!.Token);
            _=ExpirePendingAsync(id);
        }catch(Exception ex){pending=null;chat.SendButton.IsEnabled=ready;chat.SetStatus(SafeError(ex)+" 草稿保留；送達狀態可能不確定。");}
    }
    private async Task ExpirePendingAsync(string id)
    {
        await Task.Delay(TimeSpan.FromSeconds(15));
        if(pending?.Id==id){pending=null;chat.SendButton.IsEnabled=ready;chat.SetStatus("未收到送達回條；草稿保留，重送前請先向朋友確認。");}
    }
    internal static string SafeError(Exception ex)=>ex switch {
        ArgumentException=>ex.Message, InvalidDataException=>ex.Message,
        OperationCanceledException=>"操作取消或逾時。",
        WebSocketException=>"Relay 連線失敗；請檢查位址、網路、憑證、帳號重複登入與 WSS 設定。",
        System.Net.Http.HttpRequestException=>"語音轉錄失敗；請檢查 Groq Key、額度與網路。",
        _=>"操作失敗，請檢查檔案、裝置、權限或 Windows 認證管理員。"
    };
    private async Task CheckForUpdatesAsync(bool manual)
    {
        try {
            string currentVer="0.1.0-beta.2";
            try {
                var infoVer=(System.Reflection.AssemblyInformationalVersionAttribute?)
                    Attribute.GetCustomAttribute(typeof(App).Assembly, typeof(System.Reflection.AssemblyInformationalVersionAttribute));
                if(infoVer is not null && !string.IsNullOrWhiteSpace(infoVer.InformationalVersion))
                    currentVer=infoVer.InformationalVersion.Split('+')[0].Trim();
            }catch{}

            using var checker=new UpdateChecker();
            var result=await checker.CheckForUpdatesAsync(currentVer,"nekochat-lite-v");
            if(result.HasUpdate) {
                string url=!string.IsNullOrWhiteSpace(result.HtmlUrl)
                    ? result.HtmlUrl
                    : "https://github.com/yuipan0611/whats-up-my-neighbor-ai-input-windows/releases";
                string prompt=$"發現新版本 NekoChat Lite v{result.LatestVersion}！（目前版本：v{result.CurrentVersion}）\n\n{result.ReleaseTitle ?? ""}\n是否開啟瀏覽器前往下載頁面？";
                if(manual) {
                    if(MessageBox.Show(chat,prompt,"NekoChat — 檢查更新",MessageBoxButton.YesNo,MessageBoxImage.Information)==MessageBoxResult.Yes) {
                        try{System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url){UseShellExecute=true});}
                        catch{chat.SetStatus("無法開啟瀏覽器，請手動前往："+url);}
                    }
                } else {
                    pet.Notify($"發現新版本 v{result.LatestVersion}！右鍵選單「檢查更新」可前往下載。");
                }
            } else if(manual) {
                if(result.Success) {
                    MessageBox.Show(chat,$"目前已是最新版本（v{result.CurrentVersion}）。","NekoChat — 檢查更新",MessageBoxButton.OK,MessageBoxImage.Information);
                } else {
                    MessageBox.Show(chat,"無法檢查更新："+result.ErrorMessage+"\n\n請確認網路連線或稍後再試。","NekoChat — 檢查更新",MessageBoxButton.OK,MessageBoxImage.Warning);
                }
            }
        }catch(Exception ex){if(manual)MessageBox.Show(chat,"檢查更新時發生錯誤："+ex.Message,"NekoChat — 檢查更新",MessageBoxButton.OK,MessageBoxImage.Warning);}
    }
    protected override void OnExit(ExitEventArgs e)
    {
        _ = directListener?.DisposeAsync();
        DisposeVoice();DisposeClassroom();connectionCancel?.Cancel();relay?.Abort();
        base.OnExit(e);
    }
}
