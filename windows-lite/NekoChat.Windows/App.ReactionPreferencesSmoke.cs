using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
        async Task RoleStartup(string name, string oldTeacherIp, string[] local, bool expected)
        {
            string root = Path.Combine(output, "reaction-role-" + name + "-" + Guid.NewGuid().ToString("N"));
            UiTestOptions.AssertSafeDataDirectory(root); Directory.CreateDirectory(root);
            string file = Path.Combine(root, "settings.json");
            await File.WriteAllTextAsync(file, System.Text.Json.JsonSerializer.Serialize(new {
                TeacherIp = oldTeacherIp, ClassRosterInitialized = true, AutoStartOnLogin = false
            }), token);
            var current = Settings.LoadWithStatus(file).Value;
            await using var roleRuntime = await ChatRuntime.OpenAsync(new(current, SettingsSourceStatus.Loaded), true, 0, loopbackOnly: true);
            var roleView = new ChatWindow(current);
            roleView.SettingsChanged += value => current = value;
            roleView.InstallContactsUiSmokeHooks(root, new() { LocalAddresses = local }, roleRuntime);
            try
            {
                check(roleRuntime.ClassroomFiles?.IsTeacherNode != true &&
                    await roleRuntime.Worker.Run(store => store.GetClassroomTeacherIp()) is null,
                    "RX-07 startup fixture has no CSV hint or roster teacher: " + name);
                await roleView.AttachRuntimeAsync(roleRuntime, 0);
                check(current.ThisDeviceIsTeacher == expected && current.TeacherIp == "" &&
                    roleView.ThisDeviceTeacherToggle.IsChecked == expected &&
                    Settings.LoadWithStatus(file).Value.ThisDeviceIsTeacher == expected,
                    "RX-07 real legacy-settings/Attach/empty projection/persistence path: " + name);
            }
            finally { await roleView.PrepareForShutdownAsync(); roleView.DetachRuntime(); roleView.Hide(); }
        }
        await RoleStartup("legacy-local", "10.251.232.240", ["10.251.232.240", "10.251.233.8"], true);
        await RoleStartup("student", ClassroomReactions.DefaultTeacherIp, ["10.251.232.8"], false);
        string profile = Path.Combine(output, "reaction-profile-" + Guid.NewGuid().ToString("N"));
        UiTestOptions.AssertSafeDataDirectory(profile);
        var preferences = Settings.ForUiTest(profile) with { ClassRosterInitialized = true, TeacherIp = "", ThisDeviceIsTeacher = false, WanderEnabled = false };
        check(preferences.Save() && preferences.ShowReactionPreviews && !preferences.ReactionPreviewExplicitChoice,
            "RX-01 fresh isolated profile enables images without an explicit choice");
        await using var runtime = await ChatRuntime.OpenAsync(new(preferences, SettingsSourceStatus.Loaded), true, 0, loopbackOnly: true);
        var hooks = new ContactsUiSmokeHooks { LocalAddresses = Enumerable.Range(1, 9).Select(index => "10.251.233." + index).Append(ClassroomReactions.DefaultTeacherIp).ToArray() };
        var view = new ChatWindow(preferences); var target = new PetWindow();
        var outcomes = new List<ReactionNotificationKind>();
        target.SetWanderEnabled(false); target.ReactionDisplayDevice = "disconnected-test-screen";
        view.SettingsChanged += updated => { preferences = updated; target.ReactionDisplayDevice = updated.ReactionDisplayDevice; };
        int notifications = 0; Task? notification = null; LegacyDeliveryNotice? stored = null;
        var rebuildObservations = new List<object>();
        Func<object>? currentRebuildState = null;
        string rebuildStage = "not-started";
        var mapped = new ContactsSmokeSink(runtime) { SyntheticSource = ContactsIncomingIp };
        mapped.LegacyStored = notice => {
            // The actual receive transaction must emit this event. Blocked receives emit none.
            stored = notice;
            notification = Dispatcher.InvokeAsync(() => {
                notifications++;
                view.DataChanged();
                outcomes.Add(NotifyReactionOrText(preferences, target, ClassroomReactions.Find(notice.Reaction),
                    IsReactionTeacher(runtime, preferences), notice.Scope.ContextId, "合成反應同學"));
            }).Task;
        };
        await using var listener = new ClassroomTcpListener(new ClassroomEndpointAdapter(mapped),
            new() { BindAddress = IPAddress.Loopback, Port = 0 });
        try
        {
            check(runtime.ClassroomFiles?.IsTeacherNode != true, "RX-07 fixed-IP fixture has no CSV teacher-node shortcut");
            view.InstallContactsUiSmokeHooks(profile, hooks, runtime);
            await view.AttachRuntimeAsync(runtime, 0);
            check(preferences.ThisDeviceIsTeacher == true && !preferences.ThisDeviceTeacherExplicitChoice &&
                preferences.TeacherIp == "" && Settings.LoadWithStatus(preferences.PersistencePath).Value.ThisDeviceIsTeacher == true,
                "RX-07 fixed default local IP promotes a previous false before empty roster projection, and persists");
            listener.Start(); view.SetReceiverState(true, listener.Port, null); view.Show(); target.Show(); view.Activate();
            view.SettingsPane.Visibility = Visibility.Visible;
            view.ReactionPreviewToggle.BringIntoView(); view.UpdateLayout();
            check(view.ReactionPreviewToggle.IsVisible && view.ReactionPreviewToggle.ActualWidth > 0 &&
                view.ReactionPreviewToggle.IsEnabled && view.ReactionPreviewToggle.IsChecked == true &&
                view.ReactionDisplay.IsVisible && view.ThisDeviceTeacherToggle.IsVisible && view.ThisDeviceTeacherToggle.IsChecked == true &&
                !view.TeacherIpBox.IsVisible,
                "RX-02 visible default-ON reaction toggle and monitor picker; legacy teacher editor remains hidden");
            shot("reaction-settings-default-on", view);
            async Task Send(string? id, bool expectNotification = true)
            {
                notification = null;
                var reaction = ClassroomReactions.Find(id);
                check(await DirectLanMessaging.SendDirectAsync("127.0.0.1", reaction?.Text ?? "講太快了", listener.Port, token, reaction?.Id),
                    "RX real loopback reaction receives normal legacy ACK: " + id);
                await runtime.Worker.Run(_ => true); // All store/event work precedes the ACK.
                if (expectNotification)
                {
                    check(notification is not null, "RX receive transaction emitted its real notification");
                    await notification!;
                }
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            }
            var capturedReactions = new List<string>();
            async Task CaptureReaction(string id, string text, string imageFile)
            {
                // Never overlap images or enqueue a second type behind the previous two-second display.
                await wait(() => !target.ReactionActive && target.ReactionImage.Source is null,
                    "RX previous image did not clear before " + id);
                await Send(id);
                await wait(() => target.ReactionActive && target.ReactionView.IsVisible && target.ReactionImage.IsVisible &&
                    target.ReactionImage.ActualWidth > 0 && target.ReactionImage.ActualHeight > 0 && target.ReactionImage.Source is not null,
                    "RX reaction image did not render: " + id);
                // Expectations are literal and independent of ClassroomReactions.All's current mapping.
                var definition = ClassroomReactions.Find(id)!;
                check(definition.Text == text && definition.ImageFile == imageFile,
                    $"RX exact caption/asset definition for {id}: {text} / {imageFile}");
                using var bytes = typeof(PetWindow).Assembly.GetManifestResourceStream("NekoChat.Windows.Reactions." + imageFile)
                    ?? throw new InvalidDataException("Missing expected embedded reaction image: " + imageFile);
                var expected = new PngBitmapDecoder(bytes, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                check(ContactsSameBitmap(target.ReactionImage.Source, expected),
                    $"RX actual PetWindow image pixels match {imageFile}: {id}");
                check(target.ReactionCaption.Text == text && !target.IsActive,
                    $"RX exact visible caption without merged count or focus change: {id} / {text}");
                shot((id == "stop-scrolling" ? "reaction-" : "reaction-teacher-") + id, target); capturedReactions.Add(id);
                await wait(() => !target.ReactionActive && target.ReactionImage.Source is null,
                    "RX image did not disappear after its normal display: " + id);
            }
            // Fresh default-ON profile: all four teacher-only captions each get their own real render.
            await CaptureReaction("too-fast", "講太快了", "raised-paw.png");
            await CaptureReaction("cant-keep-up", "跟不上", "close-up.png");
            await CaptureReaction("dont-understand", "聽不懂", "close-up.png");
            await CaptureReaction("say-again", "再講一次", "raised-paw.png");
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
            // Real visible role click, then reload Settings and run the actual Attach/projection path again.
            view.ThisDeviceTeacherToggle.IsChecked = false;
            view.ThisDeviceTeacherToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await wait(() => view.SmokeContactsViewIdle, "RX-07 role change history refresh did not settle");
            check(!IsReactionTeacher(runtime, preferences) && preferences.ThisDeviceTeacherExplicitChoice &&
                preferences.ResolveTeacherDeviceRole(hooks.LocalAddresses!, true).ThisDeviceIsTeacher == false,
                "RX-07 explicit OFF wins over fixed IP and CSV teacher-node hint");
            view.DetachRuntime(); view.Hide();
            preferences = Settings.LoadWithStatus(preferences.PersistencePath).Value;
            view = new ChatWindow(preferences);
            view.SettingsChanged += updated => { preferences = updated; target.ReactionDisplayDevice = updated.ReactionDisplayDevice; };
            view.InstallContactsUiSmokeHooks(profile, hooks, runtime);
            await view.AttachRuntimeAsync(runtime, 0);
            view.SetReceiverState(true, listener.Port, null); view.Show(); view.Activate();
            check(!IsReactionTeacher(runtime, preferences) && view.ThisDeviceTeacherToggle.IsChecked == false &&
                preferences.ThisDeviceTeacherExplicitChoice, "RX-07 second startup at fixed default IP respects explicit unchecked role");
            while (target.Bubble.Visibility == Visibility.Visible) target.AdvanceChatNotice();
            int beforeIgnored = notifications, beforeOutcomes = outcomes.Count;
            foreach (var reaction in ClassroomReactions.All.Where(r => r.TeacherOnly))
            {
                await Send(reaction.Id);
                check(outcomes[^1] == ReactionNotificationKind.None && !target.ReactionActive &&
                    target.ReactionImage.Source is null && !target.Bubble.IsVisible && target.PendingChatCount == 0,
                    "RX-08 student calls neither image nor text reminder: " + reaction.Id);
            }
            check(notifications == beforeIgnored + 4 && outcomes.Skip(beforeOutcomes).SequenceEqual(
                Enumerable.Repeat(ReactionNotificationKind.None, 4)), "RX-08 all four actual receive events are observed and deliberately suppressed");
            if (view.SettingsPane.Visibility == Visibility.Visible)
                SmokeVisuals<Button>(view.SettingsPane).Single(button => Equals(button.Content, "完成")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await view.SmokeOpenEntryAsync(entry => entry.Endpoint == ContactsIncomingIp);
            await wait(() => view.SettingsPane.Visibility == Visibility.Collapsed && view.History.IsVisible && view.SmokeContactsViewIdle,
                "RX-08 visible conversation/settings close/read refresh did not settle");
            check(view.SettingsPane.Visibility == Visibility.Collapsed && view.History.IsVisible,
                "RX-08 rebuild checks use the visible reaction conversation, not settings overlay");
            foreach (var reaction in ClassroomReactions.All.Where(r => r.TeacherOnly))
            {
                string label = "課堂反應：" + reaction.Text + "（只有老師電腦會跳出貓咪圖）";
                var message = view.History.Rows.Last(row => row.RawText == reaction.Text);
                check(message.ReactionHistoryLabel == label, "RX-08 actual history projects annotation without replacing raw text: " + reaction.Id);
                view.History.ScrollIntoView(message); view.UpdateLayout();
                await wait(() => view.SmokeContactsViewIdle && SmokeVisuals<MessageCard>(view.History)
                    .Any(card => ReferenceEquals(card.Message, message) && card.IsLoaded && card.IsVisible),
                    "RX-08 annotated message card/read refresh was not realized and idle");
                var card = SmokeVisuals<MessageCard>(view.History).Single(card => ReferenceEquals(card.Message, message));
                check(SmokeVisuals<TextBlock>(card).Any(text => text.Text == label && text.FontSize == 11) &&
                    !SmokeVisuals<TextBlock>(card).Any(text => text.Text == reaction.Text),
                    "RX-08 rendered small annotation replaces ordinary text body: " + reaction.Id);
                bool ordinaryFresh = false, annotatedFresh = false, reboundFresh = false;
                bool Connected(DependencyObject? child)
                {
                    for (var node = child; node is not null; node = VisualTreeHelper.GetParent(node))
                        if (ReferenceEquals(node, card)) return true;
                    return false;
                }
                object Describe() => new {
                    stage = rebuildStage, reaction = reaction.Id, ordinaryFresh, annotatedFresh, reboundFresh,
                    sameMessage = ReferenceEquals(card.Message, message), card.IsLoaded, card.IsVisible,
                    card.ActualWidth, card.ActualHeight, rootConnected = Connected(card.Content as DependencyObject),
                    binding = System.Windows.Data.BindingOperations.GetBindingExpressionBase(card, MessageCard.MessageProperty)?.Status.ToString(),
                    settings = view.SettingsPane.Visibility.ToString(), idle = view.SmokeContactsViewIdle,
                    currentRoot = card.Content?.GetType().Name,
                    labels = SmokeVisuals<TextBlock>(card).Where(text => text.FontSize == 11).Select(text => text.Text).ToArray()
                };
                currentRebuildState = Describe;
                bool Rendered(bool annotated)
                {
                    view.UpdateLayout();
                    if (!view.SmokeContactsViewIdle || view.SettingsPane.Visibility != Visibility.Collapsed ||
                        !card.IsLoaded || !card.IsVisible || !ReferenceEquals(card.Message, message) ||
                        card.Content is not DependencyObject root || !Connected(root)) return false;
                    return annotated
                        ? SmokeVisuals<TextBlock>(root).Any(text => text.Text == label && text.FontSize == 11) &&
                            !SmokeVisuals<TextBox>(root).Any(text => text.Text == message.RawText)
                        : SmokeVisuals<TextBox>(root).Any(text => text.Text == message.RawText) &&
                            !SmokeVisuals<TextBlock>(root).Any(text => text.Text == label);
                }
                rebuildStage = "ordinary-build";
                var beforeRebuild = card.Content;
                message.ReactionHistoryLabel = null; // Actual MessageUpdated; verify the synchronous Build happened.
                ordinaryFresh = !ReferenceEquals(beforeRebuild, card.Content);
                check(ordinaryFresh, "RX-08 ordinary projection Build allocates fresh Content on the same card: " + reaction.Id);
                await wait(() => Rendered(false), "RX-08 ordinary Build did not reach the current visible tree: " + reaction.Id);
                rebuildObservations.Add(Describe());

                rebuildStage = "annotated-build";
                var ordinaryContent = card.Content;
                message.ReactionHistoryLabel = label;
                annotatedFresh = !ReferenceEquals(ordinaryContent, card.Content);
                check(annotatedFresh, "RX-08 annotation projection Build allocates fresh Content on the same card: " + reaction.Id);
                await wait(() => Rendered(true), "RX-08 annotation Build did not reach the current visible tree: " + reaction.Id);
                rebuildObservations.Add(Describe());

                rebuildStage = "render-rebind";
                var beforeRender = card.Content;
                var originalBinding = System.Windows.Data.BindingOperations.GetBindingBase(card, MessageCard.MessageProperty)
                    ?? throw new InvalidOperationException("RX-08 real message card binding is missing");
                card.ClearValue(MessageCard.MessageProperty); card.Message = message;
                reboundFresh = !ReferenceEquals(beforeRender, card.Content);
                check(reboundFresh, "RX-08 Render rebind allocates fresh Content on the same card: " + reaction.Id);
                System.Windows.Data.BindingOperations.SetBinding(card, MessageCard.MessageProperty, originalBinding);
                await wait(() => System.Windows.Data.BindingOperations.GetBindingExpressionBase(card, MessageCard.MessageProperty)?.Status ==
                    System.Windows.Data.BindingStatus.Active && Rendered(true),
                    "RX-08 original binding/current rendered annotation did not become active: " + reaction.Id);
                check(ordinaryFresh && annotatedFresh && reboundFresh &&
                    System.Windows.Data.BindingOperations.IsDataBound(card, MessageCard.MessageProperty) && Rendered(true),
                    "RX-08 same card rebuilds both projections and rebinds with fresh UI parents: " + reaction.Id);
                rebuildObservations.Add(Describe());
                var bubble = SmokeVisuals<Border>(card).Single(border => border.Child is Grid overlay &&
                    overlay.Children.OfType<StackPanel>().Any(panel => panel.Children.OfType<TextBlock>().Any(text => text.Text == label)));
                var content = ((Grid)bubble.Child).Children.OfType<StackPanel>().Single();
                var overlay = (Grid)bubble.Child;
                check(ReferenceEquals(content.Parent, overlay) && ReferenceEquals(overlay.Parent, bubble) &&
                    overlay.Children.OfType<Button>().All(button => ReferenceEquals(button.Parent, overlay)),
                    "RX-08 current rendered annotation has one logical parent per element: " + reaction.Id);
                check(bubble.ActualHeight <= content.ActualHeight + bubble.Padding.Top + bubble.Padding.Bottom + 1 &&
                    !content.Children.OfType<Button>().Any(),
                    "RX-08 compact annotation card reserves no blank action row: " + reaction.Id);

            }
            shot("reaction-student-ignored", view);
            await Send(null);
            check(outcomes[^1] == ReactionNotificationKind.Text && target.Bubble.IsVisible && !target.ReactionActive && stored!.Reaction is null,
                "RX-08 manually typed caption without wire ID remains an ordinary notification");
            await view.SmokeRefreshContactsAsync();
            check(view.History.Rows.Single(row => row.Legacy && row.Sequence == stored!.LegacySeq).ReactionHistoryLabel is null,
                "RX-08 actual no-ID typed message history has no reaction annotation");
            await CaptureReaction("stop-scrolling", "你再給我滑！", "raised-paw.png");
            check(capturedReactions.SequenceEqual(new[] { "too-fast", "cant-keep-up", "dont-understand", "say-again", "stop-scrolling" }),
                "RX all five distinct reaction screenshots captured once with exact caption and image pixels");
            var scope = stored!.Scope;
            await runtime.Worker.Run(s => s.SetSourceBlocked(ContactsIncomingIp, true));
            int before = notifications;
            int rows = await runtime.Worker.Run(s => s.ListLegacyMessages(scope).Items.Count);
            while (target.Bubble.Visibility == Visibility.Visible) target.AdvanceChatNotice();
            await Send("stop-scrolling", expectNotification: false);
            check(notification is null && notifications == before && !target.ReactionActive &&
                target.ReactionImage.Source is null && target.Bubble.Visibility == Visibility.Collapsed &&
                await runtime.Worker.Run(s => s.ListLegacyMessages(scope).Items.Count) == rows,
                "RX-06 blocked reaction ACKs but emits no event, stores no body and displays neither image nor text");
            shot("reaction-blocked-no-notice", target);
        }
        catch
        {
            try
            {
                await File.WriteAllTextAsync(Path.Combine(output, "reaction-rebuild-observations.json"),
                    System.Text.Json.JsonSerializer.Serialize(new { stage = rebuildStage, observations = rebuildObservations, current = currentRebuildState?.Invoke() },
                        new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                shot("reaction-rebuild-failure", view);
            }
            catch (Exception evidenceError) when (evidenceError is IOException or InvalidOperationException) { Console.Error.WriteLine("Reaction rebuild evidence capture failed: " + evidenceError.GetType().Name); }
            throw;
        }
        finally
        {
            await view.PrepareForShutdownAsync(); view.DetachRuntime(); view.Hide(); target.Close();
        }
        await File.WriteAllTextAsync(Path.Combine(output, "reaction-rebuild-observations.json"),
            System.Text.Json.JsonSerializer.Serialize(new { passed = true, observations = rebuildObservations },
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), token);
    }
}
