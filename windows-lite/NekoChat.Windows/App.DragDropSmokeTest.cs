using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using NekoChat.Core.Attachments;
using NekoChat.Core.Storage;

namespace NekoChat.Windows;

public partial class App
{
    // Explicit fresh-profile native test only. Explorer supplies the real OLE FileDrop;
    // this fixture never constructs DragEventArgs, invokes ComposerDrop, or clicks Send.
    private static void RetirePriorDndFixtures()
    {
        // Retire only our known QA windows; normal classroom candidates and daily profiles have none of these captions.
        var retired = new HashSet<int>();
        DndEnumWindows((hwnd, _) => {
            var title = new StringBuilder(512); DndGetWindowText(hwnd,title,title.Capacity);
            string text = title.ToString();
            if (text.StartsWith("NekoChat 原生拖曳 R",StringComparison.Ordinal) ||
                text.StartsWith("NekoChat Lite 0.1.0-beta.9-classroom-files.2.dnd",StringComparison.Ordinal))
            { DndGetWindowThreadProcessId(hwnd,out int pid); if(pid!=Environment.ProcessId)retired.Add(pid); }
            return true;
        },IntPtr.Zero);
        foreach(int pid in retired) try { using var prior=Process.GetProcessById(pid); if(prior.ProcessName=="NekoChat.Windows"||prior.ProcessName.StartsWith("NekoChat.DndVerifier",StringComparison.Ordinal))prior.Kill(); }
            catch(Exception e)when(e is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception){}
    }

    private async Task RunNativeDndAsync(string directory)
    {
        string evidence = Path.Combine(directory, "Evidence"); Directory.CreateDirectory(evidence);
        var checks = new List<string>(); var events = new List<object>();
        DndAllowedProcesses.Clear(); DndAllowedProcesses.Add(Environment.ProcessId);
        GetCursorPos(out var originalCursor); IntPtr explorerHandle = IntPtr.Zero;
        string? failure = null; using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); checks.Add(name); }
        int overCount = 0, dropCount = 0;
        Task Progress(string stage)
        {
            chat.Title = "NekoChat 原生拖曳 R11 · " + stage;
            chat.SetStatus("原生拖曳驗收：" + stage);
            string local = Path.Combine(evidence,"progress.json");
            File.WriteAllText(local,JsonSerializer.Serialize(new{stage,processId=Environment.ProcessId,utc=DateTimeOffset.UtcNow,events,overCount,dropCount}));
            try {string export=Path.Combine(@"\\Mac\NekoChat-回傳\DND-QA-20261105",Path.GetFileName(directory));Directory.CreateDirectory(export);File.Copy(local,Path.Combine(export,"progress.json"),true);}catch(IOException){}
            return Task.CompletedTask;
        }
        await Progress("starting");
        chat.AddHandler(DragDrop.PreviewDragOverEvent, new DragEventHandler((_, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) overCount++; }), true);
        chat.AddHandler(DragDrop.PreviewDropEvent, new DragEventHandler((_, e) => {
            dropCount++; events.Add(new { utc = DateTimeOffset.UtcNow, kind = "real_preview_drop", files = (e.Data.GetData(DataFormats.FileDrop) as string[])?.Select(Path.GetFileName).ToArray(), formats = e.Data.GetFormats() });
        }), true);
        try
        {
            var runtime = chatRuntime ?? throw new InvalidOperationException("No isolated runtime");
            await Progress("runtime-ready");
            Check(runtime.IsolatedData && !classroomShareNetwork, "fresh isolated runtime; public replication disabled");
            await runtime.Worker.Run(s => s.SetReadReceiptPolicy(false));
            string fixtures = Path.Combine(directory, "NekoChat-DND-Fixtures-" + Guid.NewGuid().ToString("N")[..6]); Directory.CreateDirectory(fixtures);
            await File.WriteAllTextAsync(Path.Combine(fixtures,"01-chat-drop.txt"),"Native Explorer drag\n聊天室：中文與換行\n",new UTF8Encoding(false),deadline.Token);
            await File.WriteAllTextAsync(Path.Combine(fixtures,"03-classroom-drop.py"),"def hello():\n    print('課堂檔案拖曳')\n",new UTF8Encoding(false),deadline.Token);
            File.Copy(Path.Combine(AppContext.BaseDirectory,"..","Fixtures","02-image.png"),Path.Combine(fixtures,"02-image.png"));
            Directory.CreateDirectory(Path.Combine(fixtures,"Folder"));
            await using(var tooLarge = new FileStream(Path.Combine(fixtures,"04-over-limit.bin"),FileMode.CreateNew)) tooLarge.SetLength(AttachmentLimits.MaximumFileBytes+1);
            chat.WindowState=WindowState.Normal; chat.ShowChat("192.168.255.250");
            await Progress("waiting-draft");await UntilAsync(()=>chat.Draft.IsEnabled,deadline.Token);await Progress("draft-ready");
            var inbox = await runtime.Worker.Run(s=>s.GetClassroomInboxSnapshot().ToInboxSnapshot());
            var entry = inbox.Entries.Single(x=>x.Endpoint=="192.168.255.250");
            Check(ClassroomPrincipalKey.TryParse(entry.ConversationId,out var principal),"synthetic recipient only");
            chat.Left=SystemParameters.WorkArea.Left+8;chat.Top=SystemParameters.WorkArea.Top+8;
            chat.Width=Math.Max(chat.MinWidth,Math.Min(600,SystemParameters.WorkArea.Width*.52));
            chat.Height=Math.Min(700,SystemParameters.WorkArea.Height-24);chat.Topmost=true;chat.Activate();await Task.Delay(400,deadline.Token);
            Process.Start(new ProcessStartInfo("explorer.exe",$"/n,/select,\"{Path.Combine(fixtures,"01-chat-drop.txt")}\""){UseShellExecute=true});
            AutomationElement? explorer=null;
            await UntilAsync(()=> {
                explorer=AutomationElement.RootElement.FindAll(TreeScope.Children,new PropertyCondition(AutomationElement.ClassNameProperty,"CabinetWClass"))
                    .Cast<AutomationElement>().FirstOrDefault(x=>x.Current.Name.Contains(Path.GetFileName(fixtures),StringComparison.Ordinal));
                return explorer is not null;
            },deadline.Token);
            await Progress("explorer-found");
            explorerHandle=new IntPtr(explorer!.Current.NativeWindowHandle);
            DndAllowedProcesses.Add(explorer.Current.ProcessId);
            var dpi=System.Windows.Media.VisualTreeHelper.GetDpi(chat);
            var edge=chat.PointToScreen(new Point(chat.ActualWidth+6,0));
            int right=(int)((SystemParameters.WorkArea.Right)*dpi.DpiScaleX);
            DndSetWindowPos(explorerHandle,IntPtr.Zero,(int)edge.X,(int)edge.Y,Math.Max(650,right-(int)edge.X-8),(int)(chat.ActualHeight*dpi.DpiScaleY),0x0040);
            await Task.Delay(350,deadline.Token);
            AutomationElement Source(string name)
            {
                var items=explorer.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.ListItem)).Cast<AutomationElement>();
                var item=items.FirstOrDefault(x=>x.Current.Name==name||x.Current.Name==Path.GetFileNameWithoutExtension(name))??throw new InvalidOperationException("Explorer item not visible: "+name);
                if(item.TryGetCurrentPattern(ScrollItemPattern.Pattern,out var scroll))((ScrollItemPattern)scroll).ScrollIntoView();return item;
            }
            async Task Drag(string[] names, FrameworkElement target)
            {
                await Progress("drag:"+string.Join("+",names));
                deadline.Token.ThrowIfCancellationRequested();
                if((DndGetAsyncKeyState(0x1B)&0x8000)!=0)throw new OperationCanceledException("Escape interrupted native test");
                DndSetForegroundWindow(explorerHandle);await Task.Delay(150,deadline.Token);
                for(int i=0;i<names.Length;i++)
                {
                    var selection=(SelectionItemPattern)Source(names[i]).GetCurrentPattern(SelectionItemPattern.Pattern);
                    if(i==0)selection.Select();else selection.AddToSelection();
                }
                await Task.Delay((int)GetDoubleClickTime()+150,deadline.Token);
                var sourceItem=Source(names[^1]);
                if(!sourceItem.TryGetClickablePoint(out Point from))throw new InvalidOperationException("Explorer item has no clickable point: "+names[^1]);
                Check(((SelectionItemPattern)sourceItem.GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected,"Explorer selected source: "+names[^1]);
                Point to=target.PointToScreen(new Point(Math.Min(70,target.ActualWidth/2),Math.Min(35,target.ActualHeight/2)));
                Check(DndProcessAt(to)==Environment.ProcessId,"drag target belongs to isolated candidate: "+names[^1]);
                events.Add(new{utc=DateTimeOffset.UtcNow,kind="explorer_mouse_drag",names,fromX=from.X,fromY=from.Y,toX=to.X,toY=to.Y});
                SetCursorPos((int)from.X,(int)from.Y);MouseEvent(0x0002,0,0,0,UIntPtr.Zero);await Task.Delay(140,deadline.Token);
                try
                {
                    for(int n=1;n<=32;n++){SetCursorPos((int)(from.X+(to.X-from.X)*n/32),(int)(from.Y+(to.Y-from.Y)*n/32));await Task.Delay(25,deadline.Token);}
                    await Task.Delay(250,deadline.Token);
                    if(DndProcessAt(to)!=Environment.ProcessId)
                    {
                        DndKeyEvent(0x1B,0,0,UIntPtr.Zero);DndKeyEvent(0x1B,0,2,UIntPtr.Zero);
                        throw new InvalidOperationException("Drag target was occluded; cancelled rather than dropping into another program.");
                    }
                }
                finally{MouseEvent(0x0004,0,0,0,UIntPtr.Zero);}
                await Task.Delay(500,deadline.Token);await Progress("after-drag:"+string.Join("+",names));
            }
            void Click(FrameworkElement control)
            {
                Check(control.IsVisible && control.IsEnabled && control.ActualWidth>0 && control.ActualHeight>0,"visible native click target: "+control.Name);
                DndSetForegroundWindow(new WindowInteropHelper(chat).Handle);
                DndClick(control.PointToScreen(new Point(control.ActualWidth/2,control.ActualHeight/2)));
            }
            async Task VerifyDraft(string name)
            {
                AttachmentDraft? row=null;
                await UntilAsync(()=> { row=runtime.Attachments!.DraftAsync(principal).GetAwaiter().GetResult();return row?.File.Name==name;},deadline.Token);
                byte[] actual=await File.ReadAllBytesAsync(runtime.Attachments!.Vault.PathFor(row!.File.BlobId),deadline.Token);
                byte[] expected=await File.ReadAllBytesAsync(Path.Combine(fixtures,name),deadline.Token);
                Check(actual.SequenceEqual(expected)&&row.File.Size==expected.Length&&row.File.Sha256==Convert.ToHexStringLower(SHA256.HashData(expected)),"chat draft bytes/size/SHA-256: "+name);
                await UntilAsync(()=>chat.AttachmentDraftPanel.IsVisible && chat.AttachmentDraftText.Text.StartsWith(name,StringComparison.Ordinal),deadline.Token);
            }
            await Drag(["01-chat-drop.txt"],chat.Draft);await VerifyDraft("01-chat-drop.txt");await Progress("chat-text-verified");
            Check(overCount>0&&dropCount>0,"real Explorer OLE drag reached chat preview handlers");
            Capture(chat,Path.Combine(evidence,"01-chat-file-drop.png"));
            Click(chat.RemoveAttachmentButton);await UntilAsync(()=>chat.AttachmentDraftPanel.Visibility==Visibility.Collapsed,deadline.Token);
            await Drag(["02-image.png"],chat.Draft);await VerifyDraft("02-image.png");await Progress("chat-image-verified");
            Check(chat.AttachmentDraftImage.Source is not null,"image drop decoded preview");Capture(chat,Path.Combine(evidence,"02-chat-image-drop.png"));
            Click(chat.RemoveAttachmentButton);await UntilAsync(()=>chat.AttachmentDraftPanel.Visibility==Visibility.Collapsed,deadline.Token);
            // Narrow layout uses the same visible return/list route; only drag events are under test here.
            if(chat.TeacherFilesButton.IsVisible==false){Click(chat.BackToInbox);await UntilAsync(()=>chat.TeacherFilesButton.IsVisible,deadline.Token);}
            Click(chat.TeacherFilesButton);await UntilAsync(()=>chat.TeacherFilesPane.IsVisible,deadline.Token);
            int priorDrops=dropCount;
            await Drag(["03-classroom-drop.py"],chat.TeacherFilesPane);
            await UntilAsync(()=>runtime.ClassroomFiles!.List().Any(x=>x.File.Name=="03-classroom-drop.py"),deadline.Token);
            await Progress("classroom-share-added");
            var shared=runtime.ClassroomFiles!.List().Single(x=>x.File.Name=="03-classroom-drop.py");
            byte[] sharedBytes=await File.ReadAllBytesAsync(runtime.ClassroomFiles.LocalPath(shared)!,deadline.Token),sourceBytes=await File.ReadAllBytesAsync(Path.Combine(fixtures,"03-classroom-drop.py"),deadline.Token);
            Check(sharedBytes.SequenceEqual(sourceBytes)&&shared.File.Sha256==Convert.ToHexStringLower(SHA256.HashData(sourceBytes)),"classroom drop stored real bytes and SHA-256");
            Check(dropCount>priorDrops,"real Explorer OLE drop reached classroom handler");
            Check(await runtime.Attachments!.DraftAsync(principal) is null,"classroom drop did not create private chat draft");
            await UntilAsync(()=>chat.TeacherFilesList.Items.OfType<ClassroomFileView>().Any(x=>x.Row.File.Id==shared.File.Id),deadline.Token);
            Capture(chat,Path.Combine(evidence,"03-classroom-file-drop.png"));
            // Invalid inputs must retain the existing share and show an item-specific problem.
            await Drag(["Folder"],chat.TeacherFilesPane);await UntilAsync(()=>chat.ShareUploadState.Text.Contains("資料夾"),deadline.Token);
            Check(runtime.ClassroomFiles.List().Count==1,"folder drop rejected without sharing");Capture(chat,Path.Combine(evidence,"04-folder-rejected.png"));
            Button CancelUpload()=>((System.Windows.Controls.DockPanel)chat.ShareUploadPanel.Child).Children.OfType<Button>().Single(x=>Equals(x.Content,"取消"));
            Click(CancelUpload());await UntilAsync(()=>chat.ShareUploadPanel.Visibility==Visibility.Collapsed,deadline.Token);chat.UpdateLayout();
            await Drag(["04-over-limit.bin"],chat.TeacherFilesPane);await UntilAsync(()=>chat.ShareUploadState.Text.Contains("25 MiB"),deadline.Token);
            Check(runtime.ClassroomFiles.List().Count==1,"oversize drop rejected without sharing");Click(CancelUpload());await UntilAsync(()=>chat.ShareUploadPanel.Visibility==Visibility.Collapsed,deadline.Token);chat.UpdateLayout();
            await Drag(["01-chat-drop.txt","03-classroom-drop.py"],chat.TeacherFilesPane);await UntilAsync(()=>chat.ShareUploadState.Text.Contains("一次"),deadline.Token);
            Check(runtime.ClassroomFiles.List().Count==1,"batch drop rejected without sharing");Click(CancelUpload());
            await UntilAsync(()=>chat.ShareUploadPanel.Visibility==Visibility.Collapsed,deadline.Token);chat.UpdateLayout();await Task.Delay(250,deadline.Token);
            await Progress("withdraw-click");
            // Click the actual WPF button and native confirmation, not the share service.
            // Native MessageBox blocks WPF dispatcher continuations; its real mouse confirmation runs off the UI thread.
            var confirmation = Task.Run(async () => {
                IntPtr dialog=IntPtr.Zero;
                while((dialog=DndFindWindow("#32770","撤下分享"))==IntPtr.Zero)await Task.Delay(50,deadline.Token).ConfigureAwait(false);
                DndGetWindowThreadProcessId(dialog,out int dialogProcess);
                if(dialogProcess!=Environment.ProcessId)throw new InvalidOperationException("Withdrawal dialog belongs to another process");
                IntPtr ok=DndGetDlgItem(dialog,1);if(ok==IntPtr.Zero)throw new InvalidOperationException("No native confirmation button");
                await Task.Delay(200,deadline.Token).ConfigureAwait(false);
                DndGetWindowRect(ok,out var rectangle);DndClick(new((rectangle.Left+rectangle.Right)/2d,(rectangle.Top+rectangle.Bottom)/2d));
                return true;
            },deadline.Token);
            var withdraw=FindDndVisuals<Button>(chat.TeacherFilesList).First(x=>Equals(x.Content,"撤下…"));Click(withdraw);
            Check(await confirmation,"native withdrawal confirmation displayed and clicked");
            await UntilAsync(()=>runtime.ClassroomFiles.List().Count==0,deadline.Token);
            Check(runtime.ClassroomFiles.List().Count==0,"actual withdrawal button removed own share");
            await UntilAsync(()=>!chat.TeacherFilesList.Items.OfType<ClassroomFileView>().Any(x=>x.Row.File.Id==shared.File.Id),deadline.Token);
            chat.UpdateLayout();await Task.Delay(150,deadline.Token);Check(chat.TeacherFilesState.IsVisible,"withdrawal refreshed actual file list empty state");Capture(chat,Path.Combine(evidence,"05-after-withdraw.png"));
            var history=await runtime.Worker.Run(s=>s.ListClassroomMessages(principal));Check(history.Items.Count==0,"no private messages sent by test");
        }
        catch(Exception error){failure=error.ToString();try{Capture(chat,Path.Combine(evidence,"failure.png"));}catch{} }
        finally
        {
            MouseEvent(0x0004,0,0,0,UIntPtr.Zero);DndKeyEvent(0x11,0,2,UIntPtr.Zero);SetCursorPos(originalCursor.X,originalCursor.Y);
            var result=new{result=failure is null?"pass":"failed",sourceVersion=System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(App).Assembly)?.InformationalVersion,
                processId=Environment.ProcessId,input="native Windows mouse events; real Explorer OLE FileDrop",checks,events,overCount,dropCount,error=failure,profile=directory,schoolRecipientsConfigured=false,sharedReplicationStarted=false,
                unexecuted=new[]{"Two Windows delivery","Other DPI settings","Classroom network acceptance"}};
            await File.WriteAllTextAsync(Path.Combine(evidence,"result.json"),JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));
            try
            {
                string export=Path.Combine(@"\\Mac\NekoChat-回傳\DND-QA-20261105",Path.GetFileName(directory));Directory.CreateDirectory(export);
                foreach(string file in Directory.EnumerateFiles(evidence))File.Copy(file,Path.Combine(export,Path.GetFileName(file)),true);
            }
            catch(IOException){}
            if(explorerHandle!=IntPtr.Zero)DndPostMessage(explorerHandle,0x0010,IntPtr.Zero,IntPtr.Zero);
            // Fixtures wrote all verified results and durable rows already. Do not enter interactive draft-close dialogs.
            if (chatRuntime is { } testRuntime) { await testRuntime.DisposeAsync(); chatRuntime = null; }
            Environment.Exit(failure is null ? 0 : 1);
        }
    }
    private static IEnumerable<T> FindDndVisuals<T>(DependencyObject root) where T:DependencyObject
    {
        for(int i=0;i<System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);i++)
        {var child=System.Windows.Media.VisualTreeHelper.GetChild(root,i);if(child is T typed)yield return typed;foreach(var found in FindDndVisuals<T>(child))yield return found;}
    }
    private static readonly HashSet<int> DndAllowedProcesses=[];
    private static int DndProcessAt(Point point)
    {DndGetWindowThreadProcessId(DndWindowFromPoint(new DndPoint{X=(int)point.X,Y=(int)point.Y}),out int process);return process;}
    private static void DndClick(Point point)
    {if(!DndAllowedProcesses.Contains(DndProcessAt(point)))throw new InvalidOperationException($"Native input target belongs to PID {DndProcessAt(point)}, expected {string.Join(",",DndAllowedProcesses)} at {point}; test stopped.");SetCursorPos((int)point.X,(int)point.Y);MouseEvent(0x0002,0,0,0,UIntPtr.Zero);MouseEvent(0x0004,0,0,0,UIntPtr.Zero);}
    private delegate bool DndWindowCallback(IntPtr hwnd,IntPtr parameter);
    [DllImport("user32.dll",EntryPoint="EnumWindows")]private static extern bool DndEnumWindows(DndWindowCallback callback,IntPtr parameter);
    [DllImport("user32.dll",EntryPoint="GetWindowTextW",CharSet=CharSet.Unicode)]private static extern int DndGetWindowText(IntPtr hwnd,StringBuilder text,int maximum);
    [StructLayout(LayoutKind.Sequential)]private struct DndPoint{public int X,Y;}
    [DllImport("user32.dll",EntryPoint="WindowFromPoint")]private static extern IntPtr DndWindowFromPoint(DndPoint point);
    [DllImport("user32.dll",EntryPoint="GetWindowThreadProcessId")]private static extern uint DndGetWindowThreadProcessId(IntPtr hwnd,out int process);
    [StructLayout(LayoutKind.Sequential)]private struct DndRect{public int Left,Top,Right,Bottom;}
    [DllImport("user32.dll",EntryPoint="SetForegroundWindow")]private static extern bool DndSetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll",EntryPoint="SetWindowPos")]private static extern bool DndSetWindowPos(IntPtr hwnd,IntPtr after,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll",EntryPoint="keybd_event")]private static extern void DndKeyEvent(byte key,byte scan,uint flags,UIntPtr extra);
    [DllImport("user32.dll",EntryPoint="GetAsyncKeyState")]private static extern short DndGetAsyncKeyState(int key);
    [DllImport("user32.dll",EntryPoint="FindWindowW",CharSet=CharSet.Unicode)]private static extern IntPtr DndFindWindow(string cls,string name);
    [DllImport("user32.dll",EntryPoint="GetDlgItem")]private static extern IntPtr DndGetDlgItem(IntPtr dialog,int id);
    [DllImport("user32.dll",EntryPoint="GetWindowRect")]private static extern bool DndGetWindowRect(IntPtr hwnd,out DndRect rect);
    [DllImport("user32.dll",EntryPoint="PostMessageW")]private static extern bool DndPostMessage(IntPtr hwnd,uint msg,IntPtr wp,IntPtr lp);
}
