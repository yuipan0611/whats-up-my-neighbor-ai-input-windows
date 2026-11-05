﻿using System;
using System.IO;
using System.Text;
using System.Linq;
using System.Threading;
using System.Diagnostics;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Automation;
using System.Windows.Forms;

public static class NativeProbe
{
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd,out uint processId);
    static Process product;
    static AutomationElement window;
    static string output, profile, exe;
    static readonly List<string> checks = new List<string>();
    static readonly List<string> captures = new List<string>();
    static readonly List<string> notes = new List<string>();
    static readonly Dictionary<string, Action> tests = new Dictionary<string, Action>();
    static int failed;

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length != 3) { Console.Error.WriteLine("exe, isolated profile root, output root required"); return 2; }
        SetProcessDPIAware();
        exe = Path.GetFullPath(args[0]);
        string run = "run-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0,8);
        profile = Path.Combine(Path.GetFullPath(args[1]), run);
        string evidenceRoot = args[2] == "--shared-evidence" ? "\\\\Mac\\NekoChat-\u56de\u50b3\\UI3-native-20261105" : Path.GetFullPath(args[2]);
        output = Path.Combine(evidenceRoot, run);
        Directory.CreateDirectory(profile); Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output,"product-sha256.txt"),Sha(exe)+"  NekoChat.Windows.exe\n"+Sha(Path.Combine(Path.GetDirectoryName(exe),"NekoChat.Windows.dll"))+"  NekoChat.Windows.dll\n");
        File.WriteAllText(Path.Combine(output,"started.txt"), DateTime.UtcNow.ToString("o") + "\nUI3 source: 7e9f70c\nQA PID: " + Process.GetCurrentProcess().Id);
        object clipboard = null;
        try
        {
            clipboard = Clipboard.GetDataObject();
            Start();
            Check(window.Current.Name.Contains("classroom-ux.3.local"), "native UI-3 window loaded without connectionId failure");
            Wait(() => Rows("TeachersList").Count > 0 && Rows("InboxList").Count > 0, "private classroom roster bootstrap", 25000);
            Check(FindName(window, "匯入班級名單") == null, "no roster import button");
            Shot(window,"01-main.png");
            Select("TeachersList", 0);
            Wait(() => Visible(FindName(window,"講太快了")), "teacher reactions", 10000);
            Check(Visible(FindName(window,"跟不上")) && Visible(FindName(window,"聽不懂")) && Visible(FindName(window,"再講一次")), "four teacher reactions shown");
            Check(!Visible(Id("ScrollReaction")), "student reaction hidden in teacher chat");
            Shot(window,"02-teacher.png");
            Select("InboxList",0);
            Wait(() => Visible(Id("ScrollReaction")), "student conversation",10000);
            Check(!Visible(FindName(window,"講太快了")), "teacher reactions hidden in student chat");
            SetValue(Id("Draft"), "測試草稿\r\n    print('hello')");
            Check(GetValue(Id("Draft")).Contains("    print('hello')"), "Chinese text, newline and code indentation retained");
            Shot(window,"03-student-draft.png");

            Test("IP-only add dialog, validation and creation", delegate {
                Invoke(FindName(window,"＋ 加入好友"));
                var dialog = WaitWindow("加入好友");
                var edits = dialog.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Edit));
                Check(edits.Count == 1, "add friend dialog has only one IP field");
                Shot(dialog,"04-add-friend.png");
                SetValue(edits[0],"not-an-ip"); Invoke(FindName(dialog,"加入好友"));
                Wait(() => FindText(dialog,"有效的內網 IP") != null,"invalid IP feedback",5000);
                Check(Visible(dialog),"invalid IP keeps dialog open");
                SetValue(edits[0],"10.211.55.239"); Invoke(FindName(dialog,"加入好友"));
                Wait(() => !Visible(dialog),"save new friend",15000);
                Wait(() => FindText(window,"10.211.55.239") != null,"new friend shown",10000);
                Check(true,"valid IP-only friend persisted by UI");
            });

            Test("native nickname context menu and save", delegate {
                var row = Rows("InboxList")[0];
                ((SelectionItemPattern)row.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
                Foreground(window); row.SetFocus(); Keys("+{F10}");
                AutomationElement rename = null;
                Wait(() => (rename=FindName(AutomationElement.RootElement,"編輯綽號…")) != null,"nickname context menu",8000);
                Shot(window,"05-nickname-menu.png"); Invoke(rename);
                var dialog = WaitWindow("編輯綽號");
                Shot(dialog,"06-nickname-dialog.png");
                var edit = dialog.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Edit));
                SetValue(edit,"QA-隔壁同學"); Invoke(FindName(dialog,"儲存"));
                Wait(() => FindText(window,"QA-隔壁同學") != null,"nickname saved",10000);
                Check(true,"local nickname edited via real context menu");
            });

            Test("native text clipboard paste", delegate {
                Foreground(window); var draft=Id("Draft"); SetValue(draft,""); draft.SetFocus();
                Clipboard.SetText("剪貼簿測試\r\n    print('ok')"); Keys("^v");
                Wait(() => GetValue(Id("Draft")).Contains("    print('ok')"),"text paste",8000);
                Check(GetValue(Id("Draft")).Contains("剪貼簿測試"),"native text clipboard preserves Chinese and indentation");
            });
            Test("native screenshot clipboard paste and attachment-only draft", delegate {
                SetValue(Id("Draft"),""); Foreground(window); Id("Draft").SetFocus();
                using(var image=new Bitmap(96,64)) { using(var graphics=Graphics.FromImage(image)) graphics.Clear(Color.LightSkyBlue); Clipboard.SetImage(image); }
                Keys("^v");
                Wait(() => Visible(Id("RemoveAttachmentButton")),"image attachment staging",12000);
                Check(Id("SendButton").Current.IsEnabled,"attachment-only send button enabled");
                Shot(window,"07-pasted-image.png"); Invoke(Id("RemoveAttachmentButton"));
                Wait(() => !Visible(Id("RemoveAttachmentButton")),"remove image draft",10000);
                Check(true,"image draft removed without sending");
            });
            Test("paperclip file selection and draft", delegate {
                string file=Path.Combine(profile,"sample.py"); File.WriteAllText(file,"print('native UI-3 test')\r\n",new UTF8Encoding(false));
                Invoke(Id("AttachFileButton"));
                AutomationElement picker=null;
                Wait(() => (picker=Windows().FirstOrDefault(w => w.Current.Name.Contains("選擇") || w.Current.Name.Contains("開啟"))) != null,"file picker",10000);
                var edit=picker.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Edit)).Cast<AutomationElement>().FirstOrDefault(e=>e.Current.IsEnabled);
                SetValue(edit,file);
                var open=picker.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Button)).Cast<AutomationElement>().FirstOrDefault(e=>e.Current.Name.StartsWith("開啟") && e.Current.IsEnabled);
                Invoke(open);
                Wait(() => Visible(Id("RemoveAttachmentButton")),"file draft staging",12000);
                Check(FindText(window,"sample.py")!=null,"selected real file appears as draft");
                Shot(window,"08-selected-file.png"); Invoke(Id("RemoveAttachmentButton"));
            });

            Test("isolated restart preserves local nickname and draft", delegate {
                SetValue(Id("Draft"),"重開保留的測試草稿\r\n    four spaces");
                Thread.Sleep(1800); product.Kill(); product.WaitForExit(5000); Start();
                Wait(()=>FindText(window,"QA-隔壁同學")!=null,"nickname after restart",12000);
                Select("InboxList",0);
                Wait(()=>GetValue(Id("Draft")).Contains("重開保留的測試草稿"),"draft after restart",10000);
                Check(GetValue(Id("Draft")).Contains("    four spaces"),"nickname and draft survive isolated process restart");
                Shot(window,"09-restored.png");
            });
            Shot(window,"10-final.png");
        }
        catch(Exception e) { failed++; notes.Add("FATAL: "+e); TryScreenshot(); }
        finally
        {
            try { if(clipboard!=null) Clipboard.SetDataObject(clipboard,true); } catch(Exception e) { notes.Add("Clipboard restore: "+e.GetType().Name); }
            string result = (failed==0?"PASS":"FAIL")+"\n"+string.Join("\n",checks.ToArray())+"\n"+string.Join("\n",notes.ToArray())+
                "\nUNEXECUTED: drag-and-drop, physical IME, DPI changes, two Windows machines, actual message/attachment delivery.\n"+
                "Native automated UI checks; no messages were sent. Product left open on isolated profile.\n";
            File.WriteAllText(Path.Combine(output,"result.txt"),result,new UTF8Encoding(true));
            Console.WriteLine(result); Console.WriteLine("Evidence: "+output);
        }
        return failed==0?0:1;
    }
    static string Sha(string path) { using(var sha=System.Security.Cryptography.SHA256.Create()) using(var input=File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(input)).Replace("-","").ToLowerInvariant(); }
    static void Test(string name,Action action) { try { action(); } catch(Exception e) { failed++; notes.Add("FAIL "+name+": "+e); TryScreenshot(); CloseModal(); } }
    static void Check(bool value,string label) { if(!value)throw new Exception(label); checks.Add("PASS "+label); Console.WriteLine("PASS "+label); }
    static void Wait(Func<bool> check,string label,int ms) { var sw=Stopwatch.StartNew(); while(sw.ElapsedMilliseconds<ms) { try { if(check())return; }catch(ElementNotAvailableException){} Thread.Sleep(150); } throw new TimeoutException(label); }
    static void Start() {
        product=Process.Start(new ProcessStartInfo(exe,"--ui-test-data \""+profile+"\" --ui-test-port 18769") { UseShellExecute=false,WorkingDirectory=profile });
        Wait(delegate { window=Windows().FirstOrDefault(w=>w.Current.Name.StartsWith("NekoChat Lite")); return window!=null; },"UI-3 main window startup",25000);
        Foreground(window); Thread.Sleep(400);
    }
    static List<AutomationElement> Windows() { return AutomationElement.RootElement.FindAll(TreeScope.Children,new PropertyCondition(AutomationElement.ProcessIdProperty,product.Id)).Cast<AutomationElement>().ToList(); }
    static AutomationElement WaitWindow(string name) { AutomationElement found=null;Wait(()=> (found=Windows().FirstOrDefault(w=>w.Current.Name==name))!=null,name,10000);return found; }
    static AutomationElement Id(string id) { return window.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.AutomationIdProperty,id)); }
    static AutomationElement FindName(AutomationElement root,string name) {
        Condition condition = new PropertyCondition(AutomationElement.NameProperty,name);
        if(root.Current.ProcessId!=product.Id) condition=new AndCondition(condition,new PropertyCondition(AutomationElement.ProcessIdProperty,product.Id));
        return root.FindFirst(TreeScope.Descendants,condition);
    }
    static AutomationElement FindText(AutomationElement root,string text) { return root.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Text)).Cast<AutomationElement>().FirstOrDefault(e=>e.Current.Name.Contains(text)); }
    static bool Visible(AutomationElement e) { try{return e!=null&&!e.Current.IsOffscreen;}catch(ElementNotAvailableException){return false;} }
    static List<AutomationElement> Rows(string id) { var list=Id(id);if(list==null)return new List<AutomationElement>();return list.FindAll(TreeScope.Children,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.ListItem)).Cast<AutomationElement>().ToList(); }
    static void Select(string id,int index) { var row=Rows(id)[index]; ((SelectionItemPattern)row.GetCurrentPattern(SelectionItemPattern.Pattern)).Select(); Thread.Sleep(500); }
    static void SetValue(AutomationElement e,string value) { if(e==null)throw new Exception("Missing edit control"); ((ValuePattern)e.GetCurrentPattern(ValuePattern.Pattern)).SetValue(value); }
    static string GetValue(AutomationElement e) { return ((ValuePattern)e.GetCurrentPattern(ValuePattern.Pattern)).Current.Value; }
    static void Invoke(AutomationElement e) { if(e==null)throw new Exception("Missing button"); var invoke=(InvokePattern)e.GetCurrentPattern(InvokePattern.Pattern);ThreadPool.QueueUserWorkItem(delegate { try{invoke.Invoke();}catch(Exception ex){Console.WriteLine("Invoke: "+ex.Message);} });Thread.Sleep(200); }
    static void Keys(string keys) {
        uint foreground;GetWindowThreadProcessId(GetForegroundWindow(),out foreground);
        if(foreground!=product.Id)throw new Exception("QA window lost foreground; refusing to send keys to another application");
        SendKeys.SendWait(keys);
    }
    static void Foreground(AutomationElement e) { SetForegroundWindow(new IntPtr(e.Current.NativeWindowHandle));Thread.Sleep(200); }
    static void Shot(AutomationElement e,string name) {
        Foreground(e);Thread.Sleep(200);var r=e.Current.BoundingRectangle;
        using(var image=new Bitmap((int)r.Width,(int)r.Height)) { using(var g=Graphics.FromImage(image))g.CopyFromScreen((int)r.X,(int)r.Y,0,0,image.Size);image.Save(Path.Combine(output,name),ImageFormat.Png); }
        var map = new List<string>();
        map.Add("id\tname\ttype\tx\ty\twidth\theight");
        foreach(AutomationElement node in e.FindAll(TreeScope.Descendants,Condition.TrueCondition)) {
            try {
                if(node.Current.IsOffscreen)continue;
                var box=node.Current.BoundingRectangle;
                if(box.Width<=0||box.Height<=0)continue;
                map.Add(node.Current.AutomationId+"\t"+node.Current.Name.Replace("\r"," ").Replace("\n"," ").Replace("\t"," ")+"\t"+node.Current.ControlType.ProgrammaticName+"\t"+
                    (box.X-r.X)+"\t"+(box.Y-r.Y)+"\t"+box.Width+"\t"+box.Height);
            } catch(ElementNotAvailableException){}
        }
        File.WriteAllLines(Path.Combine(output,name+".map.tsv"),map.ToArray(),new UTF8Encoding(true));
        captures.Add(name);
    }
    static void TryScreenshot() { try{var target=window??Windows().FirstOrDefault();if(target!=null)Shot(target,"failure-"+failed+".png");}catch{} }
    static void CloseModal() { try { var modal=Windows().FirstOrDefault(w=>w.Current.Name=="加入好友"||w.Current.Name=="編輯綽號");if(modal!=null)Invoke(FindName(modal,"取消")); }catch{} }
}
