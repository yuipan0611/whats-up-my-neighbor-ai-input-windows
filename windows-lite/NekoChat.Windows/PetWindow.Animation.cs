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
    private readonly DesktopPetActivity activity = new(Environment.TickCount);
    private bool automaticMotion;
    private bool wanderEnabled = true;
    internal bool WanderEnabled => wanderEnabled;
    internal void SetWanderEnabled(bool enabled)
    {
        wanderEnabled = enabled;
        if (wanderMenuItem is not null) wanderMenuItem.IsChecked = enabled;
        if (!animation.Pressed) { animation.Reset(AnimationNow); automaticMotion = false; }
        activity.Reset(AnimationNow, 0);
        nextAction = AnimationNow + 2;
    }
    private double AnimationNow=>animationClock.Elapsed.TotalSeconds;
    private void InitializeAnimation()
    {
        animation=new(animationAssets.Manifest);
        frameTimer.Tick+=(_,_)=>{
            try{AdvanceAnimation();}
            catch(Exception ex) when(ex is IOException or NotSupportedException or FormatException) {
                var last=Cat.Source;frameTimer.Stop();
                if(last is not null)UseStaticArtwork(last);
                Notify("貓咪動畫暫停，請重新開啟 App；聊天仍可使用。");
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
        var now=AnimationNow;var gap=now-previousFrame;previousFrame=now;
        if(animatedCoat is null)return;
        if(gap>2 && !animation.Pressed){animation.Reset(now);automaticMotion=false;activity.Reset(now,0);nextAction=now+2;}
        animation.Tick(now);
        bool blocked=persistentNotice || Bubble.Visibility==Visibility.Visible || Cat.ContextMenu?.IsOpen==true || !wanderEnabled;
        if(blocked || now<nextAction || animation.Pressed || animation.Mode is "lifting" or "landing" or "carried")
        {
            activity.Reset(now,0);
            if(blocked)
            {
                if(automaticMotion){animation.Reset(now);automaticMotion=false;}
                nextAction=now+2;
            }
        }
        else
        {
            double minimum=SystemParameters.VirtualScreenLeft;
            double maximum=minimum+Math.Max(0,SystemParameters.VirtualScreenWidth-Width);
            var step=activity.Step(now,Left,minimum,maximum);
            if(step.Changed)
            {
                if(step.Pose=="sitting")animation.Reset(now);
                else animation.Play(step.Pose,now);
                automaticMotion=true;
            }
            if(step.Pose is "walk-left" or "walk-right")
            {
                Left=step.X;
                animation.SetWalkDistance(step.WalkDistance);
            }
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
