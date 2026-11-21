using System.IO;
using System.Windows;
using System.Windows.Controls;
using NekoChat.Core;
using NekoChat.Core.Roster;

namespace NekoChat.Windows;

public partial class App
{
    private async Task RunDefaultRosterUiChecksAsync(string output, Action<bool, string> check,
        Action<string, FrameworkElement> shot, Func<Func<bool>, string, Task> wait)
    {
        foreach (string kind in new[] { "student", "teacher", "sidecar", "existing", "subnet-upgrade" })
        {
            string profile = Path.Combine(output, "default-roster-" + kind + "-" + Guid.NewGuid().ToString("N"));
            UiTestOptions.AssertSafeDataDirectory(profile);
            var preferences = Settings.ForUiTest(profile); check(preferences.Save(), "CT-16 fresh isolated settings saved: " + kind);
            string local = kind == "teacher" ? ClassroomReactions.DefaultTeacherIp : "192.168.21.5";
            string sidecar = Path.Combine(profile, "classroom-roster.csv");
            if (kind == "sidecar") await File.WriteAllTextAsync(sidecar,
                "student_number,name,ip,role\n,10.251.234.7,10.251.234.7,student\n");
            await using var runtime = await ChatRuntime.OpenAsync(new(preferences, SettingsSourceStatus.Loaded), true, 0, loopbackOnly: true);
            if (kind == "existing") await runtime.Worker.Run(store => {
                store.SaveManualRosterEntry(null, new("合成既有備註", null, false, "192.168.21.10"),
                    RosterImportPreview.ComputeSnapshotToken(store.GetRosterSnapshot()));
                string removed = store.SaveManualRosterEntry(null, new("合成先前移除", null, false, "192.168.21.15"),
                    RosterImportPreview.ComputeSnapshotToken(store.GetRosterSnapshot())).EntryId!;
                var deletion = store.PreviewRosterDelete(removed);
                return store.DeleteRosterEntry(removed, deletion.SnapshotToken, deletion.ToConfirmation());
            });
            if (kind == "subnet-upgrade") await runtime.Worker.Run(store => {
                store.SaveManualRosterEntry(null, new("合成保留備註", null, false, "192.168.24.10"),
                    RosterImportPreview.ComputeSnapshotToken(store.GetRosterSnapshot()));
                return store.SaveManualRosterEntry(null, new("合成老師", null, true, "192.168.24.240"),
                    RosterImportPreview.ComputeSnapshotToken(store.GetRosterSnapshot()));
            });
            // The real store also rejects this host's actual addresses. Keep the synthetic fixture consistent
            // when running on a classroom PC whose own seat is present in the new default CSV.
            var localAddresses = LanInvitation.LocalAddresses().Append(local).Distinct(StringComparer.Ordinal).ToArray();
            int excluded = RosterCsv.Parse(ClassroomRosterSeed.Load()).Rows.Count(row => localAddresses.Contains(row.Endpoint!));
            int peerCount = 38 - excluded;
            var hooks = new ContactsUiSmokeHooks { LocalAddresses = localAddresses, ClassroomRosterSidecarPath = sidecar };
            var view = new ChatWindow(preferences);
            view.SettingsChanged += updated => preferences = updated;
            view.InstallContactsUiSmokeHooks(profile, hooks, runtime);
            try
            {
                await view.AttachRuntimeAsync(runtime, 0);
                var rows = await runtime.Worker.Run(store => store.GetRosterSnapshot());
                if (kind is "student" or "teacher")
                {
                    bool teacher = localAddresses.Contains(ClassroomReactions.DefaultTeacherIp);
                    check(rows.Count == peerCount && rows.All(row => !localAddresses.Contains(row.Entry.Endpoint!)) &&
                        rows.Count(row => row.Entry.IsTeacher) == (teacher ? 0 : 1) &&
                        preferences.ThisDeviceIsTeacher == teacher && preferences.ClassRosterInitialized,
                        "CT-16 real fresh bootstrap excludes self and retains independent local teacher role: " + kind);
                    view.Show(); view.Activate(); view.UpdateLayout();
                    check(view.InboxList.Items.Count == peerCount - (teacher ? 0 : 1) && view.TeachersList.Items.Count == (teacher ? 0 : 1),
                        "CT-16 actual teacher pin/student list count: " + kind);
                    if (!teacher)
                    {
                        check(view.TeachersList.Items.OfType<InboxRowView>().Single().Entry.Endpoint == ClassroomReactions.DefaultTeacherIp,
                            "CT-16 actual pinned teacher is corrected .240");
                        shot("default-classroom-roster", view);
                    }
                }
                else if (kind == "sidecar")
                    check(rows.Count == 1 && rows.Single().Entry.Endpoint == "10.251.234.7",
                        "CT-16 real sidecar source takes priority over embedded class");
                else if (kind == "subnet-upgrade")
                {
                    check(rows.Count == 2 && rows.Single(row => !row.Entry.IsTeacher).Entry is
                        { Endpoint: "192.168.21.10", DisplayName: "合成保留備註" } &&
                        rows.Single(row => row.Entry.IsTeacher).Entry.Endpoint == "192.168.21.240" &&
                        preferences.TeacherIp == "192.168.21.240" && preferences.ClassRosterInitialized,
                        "subnet upgrade runs before inbox/bootstrap and projects the new teacher without adding contacts");
                    view.Show(); view.Activate(); view.UpdateLayout();
                    check(view.TeachersList.Items.Count == 1 && view.InboxList.Items.Count == 1 &&
                        view.TeachersList.Items.OfType<InboxRowView>().Single().Entry.Endpoint == "192.168.21.240",
                        "subnet upgrade appears in the actual native teacher pin and classmate list");
                    shot("classroom-subnet-upgrade", view);
                }
                else
                {
                    check(rows.Count == 1 && rows.Single().Entry.DisplayName == "合成既有備註" && preferences.ClassRosterInitialized,
                        "CT-16 existing user is not auto-merged and keeps local nickname");
                    view.Show(); view.Activate(); view.SettingsPane.Visibility = Visibility.Visible;
                    view.LoadClassRosterButton.BringIntoView(); view.UpdateLayout();
                    check(view.LoadClassRosterButton.IsVisible && view.LoadClassRosterButton.IsEnabled && view.LoadClassRosterButton.ActualWidth > 0,
                        "CT-16 manual class roster fill is visible and reachable");
                    async Task Fill()
                    {
                        hooks.ContactAction = null;
                        view.LoadClassRosterButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        var task = hooks.ContactAction ?? throw new InvalidOperationException("CT-16 manual action was not observed");
                        await wait(() => task.IsCompleted, "CT-16 manual fill did not complete"); task.GetAwaiter().GetResult();
                    }
                    string before = RosterImportPreview.ComputeSnapshotToken(rows);
                    hooks.NextPromptAnswer = ("將新增", MessageBoxResult.No); await Fill();
                    check(hooks.Prompts[^1].Text.Contains("已跳過 1 位你先前移除的聯絡人", StringComparison.Ordinal) &&
                        view.Status.Text.Contains("已跳過 1 位你先前移除的聯絡人", StringComparison.Ordinal),
                        "CT-16 confirmation reports previously removed contact skip");
                    check(hooks.NextPromptAnswer is null && await runtime.Worker.Run(store =>
                        RosterImportPreview.ComputeSnapshotToken(store.GetRosterSnapshot())) == before,
                        "CT-16 No confirmation changes no contacts");
                    await Fill(); rows = await runtime.Worker.Run(store => store.GetRosterSnapshot());
                    check(rows.Count == peerCount - 1 && rows.Single(row => row.Entry.Endpoint == "192.168.21.10").Entry.DisplayName == "合成既有備註" &&
                        rows.All(row => row.Entry.Endpoint != local && row.Entry.Endpoint != "192.168.21.15") && rows.Single(row => row.Entry.IsTeacher).Entry.Endpoint == ClassroomReactions.DefaultTeacherIp,
                        "CT-16 Yes adds only missing IPs, preserves nickname and excludes self");
                    check(view.Status.Text.Contains("已跳過 1 位你先前移除的聯絡人", StringComparison.Ordinal),
                        "CT-16 completed status reports removed contact skip");
                    shot("default-classroom-manual-fill", view);
                }
            }
            finally { await view.PrepareForShutdownAsync(); view.DetachRuntime(); view.Hide(); }
        }
    }
}
