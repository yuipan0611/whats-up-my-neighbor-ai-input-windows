using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NekoChat.Animation;

namespace NekoChat.Windows;

public partial class PetWindow
{
    private readonly AnimationAssets animationAssets=new();
    private readonly Stopwatch animationClock=Stopwatch.StartNew();
    private readonly DispatcherTimer frameTimer=new(){Interval=TimeSpan.FromMilliseconds(16)};
    private AnimationPlayer animation=null!;
    private readonly Dictionary<string,BitmapSource> frames=[];
    private readonly Queue<string> frameOrder=[];
    private string? animatedCoat="original";
    private string currentFrame="";
    private double previousFrame,nextAction=2;
    private int actionIndex;
    private bool automaticMotion;
    private double AnimationNow=>animationClock.Elapsed.TotalSeconds;
    private void InitializeAnimation()
    {
        animation=new(animationAssets.Manifest);
        frameTimer.Tick+=(_,_)=>{
            try{AdvanceAnimation();}
            catch(Exception ex) when(ex is IOException or NotSupportedException or FormatException) {
                var last=Cat.Source;frameTimer.Stop();
                if(last is not null)UseStaticArtwork(last);
                Notify("貓咪動畫暫停，請重新開啟 App；聊天與語音仍可使用。");
            }
        };
        Loaded+=(_,_)=>{previousFrame=AnimationNow;nextAction=AnimationNow+2;if(animatedCoat is not null)frameTimer.Start();};
        Closed+=(_,_)=>{frameTimer.Stop();bubbleTimer.Stop();animationAssets.Dispose();};
        Cat.LostMouseCapture+=(_,_)=>{
            if(animation.Pressed){animation.Cancel(AnimationNow);dragging=false;nextAction=AnimationNow+5;}
        };
        SetPreset("original");
    }
    internal void SetPreset(string id)
    {
        if(!animationAssets.Manifest.Coats.Contains(id))throw new ArgumentException("未知花色。");
        if(animatedCoat is null){animation.Reset(AnimationNow);automaticMotion=false;nextAction=AnimationNow+2;}
        animatedCoat=id;frames.Clear();frameOrder.Clear();currentFrame="";
        Cat.Width=208;Cat.Height=192;RenderAnimation();
        UpdateCoatChecked(id);
        if(IsLoaded)frameTimer.Start();
    }
    private void UseStaticArtwork(System.Windows.Media.ImageSource image)
    {
        animatedCoat=null;frameTimer.Stop();frames.Clear();frameOrder.Clear();currentFrame="";
        animation.Reset(AnimationNow);automaticMotion=false;
        Cat.Width=190;Cat.Height=190;Cat.Source=image;
        UpdateCoatChecked(null);
    }
    private void RenderAnimation()
    {
        if(animatedCoat is null)return;
        var key=animatedCoat+"/"+animation.Frame;
        if(key==currentFrame)return;
        if(!frames.TryGetValue(key,out var image)) {
            using var stream=animationAssets.OpenFrame(animatedCoat,animation.Frame);
            image=new PngBitmapDecoder(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad).Frames[0];image.Freeze();
            if(frames.Count>=64)frames.Remove(frameOrder.Dequeue());
            frames[key]=image;frameOrder.Enqueue(key);
        }
        Cat.Source=image;currentFrame=key;
    }
    private void AdvanceAnimation()
    {
        var now=AnimationNow;var gap=now-previousFrame;var dt=Math.Clamp(gap,0,.05);previousFrame=now;
        if(animatedCoat is null)return;
        if(gap>2 && !animation.Pressed){animation.Reset(now);automaticMotion=false;nextAction=now+2;}
        animation.Tick(now);
        bool blocked=persistentNotice || Bubble.Visibility==Visibility.Visible || Cat.ContextMenu?.IsOpen==true || !SystemParameters.ClientAreaAnimation;
        if(blocked){if(automaticMotion){animation.Reset(now);automaticMotion=false;}nextAction=now+2;}
        else if(!animation.Pressed && animation.Mode is not ("lifting" or "landing" or "carried") && now>=nextAction) {
            (string Name,double Seconds)[] actions=[("standing",1.8),("walk-left",4),("grooming",3.2),("yawning",3.2),
                ("loafing",5),("sleeping",20),("curious",2.2),("walk-right",4)];
            var action=actions[actionIndex++%actions.Length];animation.Play(action.Name,now);automaticMotion=true;nextAction=now+action.Seconds;
        }
        if(automaticMotion && animation.Mode=="clip" && animation.ClipName is "walk-left" or "walk-right") {
            var target=Left+(animation.ClipName=="walk-left"?-1:1)*70*dt;
            Left=Math.Clamp(target,SystemParameters.VirtualScreenLeft,SystemParameters.VirtualScreenLeft+Math.Max(0,SystemParameters.VirtualScreenWidth-Width));
            if(Math.Abs(Left-target)>.01){animation.Reset(now);automaticMotion=false;nextAction=now+2;}
        }
        RenderAnimation();
    }
    internal void VerifyAnimationIntegration()
    {
        frameTimer.Stop();animation.Reset(0);animation.Press(0);animation.Drag(0);animation.Tick(.85);
        foreach(var id in animationAssets.Manifest.Coats){
            SetPreset(id);
            if(animation.Mode!="carried")throw new InvalidDataException("切換花色重設了動作。");
            if(!CoatMenuItems[id].IsChecked || CoatMenuItems.Values.Count(m=>m.IsChecked)!=1)throw new InvalidDataException("花色勾選狀態不符。");
            RenderAnimation();
        }
        animation.Release(1);animation.Tick(1.4);RenderAnimation();
        if(animation.Frame!="lift-35.png")throw new InvalidDataException("落地影格不符。");
        var custom=Cat.Source!;SetArt(custom);if(animatedCoat is not null || frameTimer.IsEnabled)throw new InvalidDataException("自訂圖片被動畫取代。");
        if(CoatMenuItems.Values.Any(m=>m.IsChecked))throw new InvalidDataException("自訂圖片未清除內建花色勾選。");
        SetPreset("original");
        if(!CoatMenuItems["original"].IsChecked || CoatMenuItems.Values.Count(m=>m.IsChecked)!=1)throw new InvalidDataException("原版黑貓勾選狀態未還原。");
        animation.Reset(AnimationNow);nextAction=AnimationNow+2;RenderAnimation();
    }
}
