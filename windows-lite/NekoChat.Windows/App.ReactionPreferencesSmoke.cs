using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NekoChat.Core;
using NekoChat.Core.Classroom.Transport;

namespace NekoChat.Windows;

public partial class App
{
    // Runs inside the existing contacts smoke's deadline and artifact directory.
    // A separate fresh profile prevents reaction data/choices from affecting the contacts fixture.
    private async Task RunReactionPreferencesUiChecksAsync(string output, Action<bool, string> check,
        Action<string, FrameworkElement> shot, Func<Func<bool>, string, Task> wait, CancellationToken token)
    {
        string profile = Path.Combine(output, "reaction-profile-" + Guid.NewGuid().ToString("N"));
        UiTestOptions.AssertSafeDataDirectory(profile);
        var preferences = Settings.ForUiTest(profile) with { ClassRosterInitialized = true, TeacherIp = "127.0.0.1", WanderEnabled = false };
        check(preferences.Save() && preferences.ShowReactionPreviews && !preferences.ReactionPreviewExplicitChoice,
            "RX-01 fresh isolated profile enables images without an explicit choice");
        await using var runtime = await ChatRuntime.OpenAsync(new(preferences, SettingsSourceStatus.Loaded), true, 0, loopbackOnly: true);
        var view = new ChatWindow(preferences); var target = new PetWindow();
        target.SetWanderEnabled(false); target.ReactionDisplayDevice = "disconnected-test-screen";
        view.SettingsChanged += updated => { preferences = updated; target.ReactionDisplayDevice = updated.ReactionDisplayDevice; };
        int notifications = 0; Task? notification = null; LegacyDeliveryNotice? stored = null;
        var mapped = new ContactsSmokeSink(runtime) { SyntheticSource = ContactsIncomingIp };
        mapped.LegacyStored = notice => {
            // The actual receive transaction must emit this event. Blocked receives emit none.
            stored = notice;
            notification = Dispatcher.InvokeAsync(() => {
                notifications++;
                NotifyReactionOrText(preferences, target, ClassroomReactions.Find(notice.Reaction),
                    IsReactionTeacher(runtime, preferences), notice.Scope.ContextId, "合成反應同學");
            }).Task;
        };
        await using var listener = new ClassroomTcpListener(new ClassroomEndpointAdapter(mapped),
            new() { BindAddress = IPAddress.Loopback, Port = 0 });
        try
        {
            listener.Start(); view.Show(); target.Show(); view.Activate();
            view.SettingsPane.Visibility = Visibility.Visible;
            view.ReactionPreviewToggle.BringIntoView(); view.UpdateLayout();
            check(view.ReactionPreviewToggle.IsVisible && view.ReactionPreviewToggle.ActualWidth > 0 &&
                view.ReactionPreviewToggle.IsEnabled && view.ReactionPreviewToggle.IsChecked == true &&
                view.ReactionDisplay.IsVisible && !view.TeacherIpBox.IsVisible,
                "RX-02 visible default-ON reaction toggle and monitor picker; legacy teacher editor remains hidden");
            shot("reaction-settings-default-on", view);
            async Task Send(string id, bool expectNotification = true)
            {
                notification = null;
                var reaction = ClassroomReactions.Find(id)!;
                check(await DirectLanMessaging.SendDirectAsync("127.0.0.1", reaction.Text, listener.Port, token, reaction.Id),
                    "RX real loopback reaction receives normal legacy ACK: " + id);
                await runtime.Worker.Run(_ => true); // All store/event work precedes the ACK.
                if (expectNotification)
                {
                    check(notification is not null, "RX receive transaction emitted its real notification");
                    await notification!;
                }
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            }
            await Send("too-fast");
            await wait(() => target.ReactionActive && target.ReactionView.IsVisible && target.ReactionImage.Source is not null,
                "RX-01 default teacher reaction did not render its embedded cat image");
            check(!target.IsActive && target.ReactionCaption.Text == "講太快了", "RX-05 default teacher image does not take focus");
            shot("reaction-default-cat", target);
            await wait(() => !target.ReactionActive, "RX default image did not restore the pet");
            view.ReactionPreviewToggle.IsChecked = false;
            view.ReactionPreviewToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var reopened = Settings.LoadWithStatus(preferences.PersistencePath).Value;
            check(!reopened.ShowReactionPreviews && reopened.ReactionPreviewExplicitChoice,
                "RX-04 visible OFF persists explicit choice and survives settings reload");
            await Send("too-fast");
            check(!target.ReactionActive && target.ReactionImage.Source is null && target.Bubble.IsVisible &&
                target.BubbleText.Text.Contains("新訊息") && !target.BubbleText.Text.Contains("講太快"),
                "RX-02 OFF routes only to content-free text reminder, with no cat image");
            shot("reaction-off-text-only", target); shot("reaction-settings-explicit-off", view);
            view.ReactionPreviewToggle.IsChecked = true;
            view.ReactionPreviewToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            preferences = preferences with { TeacherIp = "10.251.232.99" }; // This fixture is now a student receiver.
            await Send("too-fast");
            check(!target.ReactionActive && target.ReactionImage.Source is null, "RX-05 TeacherOnly never renders on a student receiver");
            await Send("stop-scrolling");
            await wait(() => target.ReactionActive && target.ReactionImage.Source is not null &&
                target.ReactionCaption.Text == "你再給我滑！", "RX-05 peer reaction did not render on a student receiver");
            shot("reaction-peer-cat", target);
            await wait(() => !target.ReactionActive, "RX peer image did not restore the pet");
            var scope = stored!.Scope;
            await runtime.Worker.Run(s => s.SetSourceBlocked(ContactsIncomingIp, true));
            int before = notifications;
            int rows = await runtime.Worker.Run(s => s.ListLegacyMessages(scope).Items.Count);
            target.Bubble.Visibility = Visibility.Collapsed;
            await Send("stop-scrolling", expectNotification: false);
            check(notification is null && notifications == before && !target.ReactionActive &&
                target.ReactionImage.Source is null && target.Bubble.Visibility == Visibility.Collapsed &&
                await runtime.Worker.Run(s => s.ListLegacyMessages(scope).Items.Count) == rows,
                "RX-06 blocked reaction ACKs but emits no event, stores no body and displays neither image nor text");
            shot("reaction-blocked-no-notice", target);
        }
        finally
        {
            view.Hide(); target.Close();
        }
    }
}
