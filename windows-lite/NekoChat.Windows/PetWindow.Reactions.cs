using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NekoChat.Core;

namespace NekoChat.Windows;

public partial class PetWindow
{
    private readonly ReactionInbox reactions = new();
    private readonly DispatcherTimer reactionTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly Dictionary<string, BitmapSource> reactionImages = new();
    private PetPlacement? reactionOrigin;
    private double originalWidth, originalHeight;
    private bool resumeAnimation;
    internal string ReactionDisplayDevice { get; set; } = "";
    internal bool ReactionActive => reactionOrigin is not null;
    internal PetPlacement? ReactionReturnPlacement => reactionOrigin;
    internal event Action<string>? ReactionFailed;

    private void InitializeReactions()
    {
        reactionTimer.Tick += (_, _) => AdvanceReactions();
        Closed += (_, _) => reactionTimer.Stop();
    }

    internal void NotifyReaction(ClassroomReaction reaction, string from)
    {
        reactions.Enqueue(reaction, from, AnimationNow);
        AdvanceReactions();
        reactionTimer.Start();
    }

    private void AdvanceReactions()
    {
        try
        {
            if (ReactionActive)
            {
                if (AnimationNow >= reactions.Deadline) EndReaction();
                else { UpdateReactionCaption(); return; }
            }
            if (Cat.IsMouseCaptured || Cat.ContextMenu?.IsOpen == true || animation.Pressed ||
                animation.Mode is "lifting" or "landing" or "carried") return;
            if (reactions.TryStart(AnimationNow) is { } notice) StartReaction(notice);
            else reactionTimer.Stop();
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or FormatException or Win32Exception or InvalidOperationException)
        {
            try { EndReaction(); }
            catch (Exception restoreError) when (restoreError is Win32Exception or InvalidOperationException) { }
            reactions.Complete();
            ReactionFailed?.Invoke("貼圖提醒無法顯示；訊息仍保留在聊天室。");
            if (reactions.PendingCount == 0) reactionTimer.Stop();
        }
    }

    private void StartReaction(ReactionNotice notice)
    {
        if (!reactionImages.TryGetValue(notice.Reaction.ImageFile, out var image))
        {
            using var source = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Assets", "Reactions", notice.Reaction.ImageFile));
            image = new PngBitmapDecoder(source, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            image.Freeze(); reactionImages.Add(notice.Reaction.ImageFile, image);
        }
        var handle = new WindowInteropHelper(this).Handle;
        var origin = DisplayScreens.Capture(handle);
        originalWidth = Width; originalHeight = Height;
        resumeAnimation = frameTimer.IsEnabled;
        frameTimer.Stop(); bubbleTimer.Stop();
        animation.Pause(true, AnimationNow);
        reactionOrigin = origin;
        NormalPet.Visibility = Visibility.Collapsed;
        ReactionImage.Source = image;
        UpdateReactionCaption();
        ReactionView.Visibility = Visibility.Visible;
        Width = 312; Height = 288;
        UpdateLayout();
        DisplayScreens.Center(handle, ReactionDisplayDevice, Width, Height);
    }

    private void UpdateReactionCaption()
    {
        if (reactions.Current is not { } notice) return;
        ReactionCaption.Text = notice.Reaction.Text + (notice.Count > 1 ? $" ×{notice.Count}" : "");
    }

    private void EndReaction()
    {
        var origin = reactionOrigin;
        reactionOrigin = null;
        reactions.Complete();
        if (origin is null) return;
        try
        {
            ReactionView.Visibility = Visibility.Collapsed;
            ReactionImage.Source = null;
            NormalPet.Visibility = Visibility.Visible;
            Width = originalWidth; Height = originalHeight;
            DisplayScreens.Restore(new WindowInteropHelper(this).Handle, origin);
        }
        finally
        {
            animation.Pause(false, AnimationNow);
            animation.Reset(AnimationNow);
            automaticMotion = false;
            previousFrame = AnimationNow;
            nextAction = AnimationNow + 2;
            if (resumeAnimation) frameTimer.Start();
            if (Bubble.Visibility == Visibility.Visible && !persistentNotice) bubbleTimer.Start();
        }
    }

    private void ReactionClick(object sender, RoutedEventArgs e)
    {
        var from = reactions.Current?.SenderIp;
        EndReaction();
        if (from is not null) OpenChat?.Invoke(from);
    }
}
