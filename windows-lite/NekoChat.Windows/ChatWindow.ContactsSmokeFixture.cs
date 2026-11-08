using System.IO;
using System.Windows;

namespace NekoChat.Windows;

// Explicit, per-window hooks. Installation requires the same isolated --ui-test-data profile
// as the attached runtime and Settings; there is no global switch or everyday-profile fallback.
internal sealed class ContactsUiSmokeHooks
{
    internal Func<Window, Task>? DriveDialog;
    internal readonly List<(string Title, string Text, MessageBoxResult Answer)> Prompts = [];
    internal (string TextFragment, MessageBoxResult Answer)? NextPromptAnswer;
    internal bool FailRemovalRefreshOnce, FailNextInboxRead;
    internal Exception? DialogFailure;
    internal Task? ContactAction;
}

public partial class ChatWindow
{
    private ContactsUiSmokeHooks? _contactsUiSmoke;
    private ChatRuntime? _contactsUiSmokeRuntime;
    internal void InstallContactsUiSmokeHooks(string profile, ContactsUiSmokeHooks hooks, ChatRuntime runtime)
    {
        UiTestOptions.AssertSafeDataDirectory(profile);
        string root = Path.GetFullPath(profile);
        if (!runtime.IsolatedData || _runtime is not null && !ReferenceEquals(_runtime, runtime) ||
            !string.Equals(Path.GetFullPath(runtime.DataDirectory), Path.Combine(root, "data"), StringComparison.OrdinalIgnoreCase) ||
            settings.PersistencePath is null || !string.Equals(Path.GetFullPath(settings.PersistencePath), Path.Combine(root, "settings.json"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Contacts UI hooks require their exact isolated profile.");
        _contactsUiSmoke = hooks;
        _contactsUiSmokeRuntime = runtime;
    }
    private void AssertContactsSmokeRuntime()
    {
        if (_runtime is not { IsolatedData: true } || !ReferenceEquals(_runtime, _contactsUiSmokeRuntime))
            throw new InvalidOperationException("Contacts hook cannot operate on a different runtime.");
    }
    private void ContactsSmokeBeforeRemovalRefresh()
    {
        if (_contactsUiSmoke is not { FailRemovalRefreshOnce: true } hook) return;
        AssertContactsSmokeRuntime(); hook.FailRemovalRefreshOnce = false; hook.FailNextInboxRead = true;
    }
    private void ContactsSmokeBeforeInboxRead()
    {
        if (_contactsUiSmoke is not { FailNextInboxRead: true } hook) return;
        AssertContactsSmokeRuntime(); hook.FailNextInboxRead = false;
        throw new IOException("Synthetic isolated inbox refresh failure.");
    }
    private MessageBoxResult ContactPrompt(string text, string title, MessageBoxButton buttons,
        MessageBoxImage image, MessageBoxResult defaultResult = MessageBoxResult.None, Window? owner = null)
    {
        if (_contactsUiSmoke is { } hook)
        {
            AssertContactsSmokeRuntime();
            var answer = buttons == MessageBoxButton.OK ? MessageBoxResult.OK : MessageBoxResult.Yes;
            if (hook.NextPromptAnswer is { } next)
            {
                if (buttons != MessageBoxButton.YesNo || !text.Contains(next.TextFragment, StringComparison.Ordinal))
                    throw new InvalidOperationException("Unexpected prompt while waiting for a synthetic cancellation: " + title);
                answer = next.Answer; hook.NextPromptAnswer = null;
            }
            hook.Prompts.Add((title, text, answer)); return answer;
        }
        return MessageBox.Show(owner ?? this, text, title, buttons, image, defaultResult);
    }
    private bool? ContactDialog(Window dialog)
    {
        if (_contactsUiSmoke is { } hook)
        {
            AssertContactsSmokeRuntime();
            var drive = hook.DriveDialog ?? throw new InvalidOperationException("Unexpected synthetic contact dialog.");
            hook.DriveDialog = null;
            dialog.Loaded += async (_, _) => {
                try { await drive(dialog); }
                catch (Exception error) { hook.DialogFailure = error; dialog.DialogResult = false; }
            };
        }
        bool? result = dialog.ShowDialog();
        if (_contactsUiSmoke?.DialogFailure is { } failure) throw new InvalidOperationException("Contact dialog automation failed.", failure);
        return result;
    }
    internal bool SmokeContactsViewIdle => !_refreshing && !_readBusy && !_readTimer.IsEnabled && !_saveTimer.IsEnabled && !_attachmentDraftLoading && (_selected is null || _historyLoaded);
    internal bool SmokeReadNoticeIdle => !_readNoticeShowing;
    internal Task SmokeFlushContactDraftAsync() => FlushDraftsAsync();
    internal string? SmokeContactDraft(string conversation) => _drafts.TryGetValue(conversation, out var buffer) ? buffer.Text : null;
    internal Task SmokeRefreshContactsAsync() => RefreshInboxAsync(true, waitForCompletion: true);
    internal string SmokeCancellationState() => System.Text.Json.JsonSerializer.Serialize(new {
        selected = _selected?.Id, filter = InboxFilter.SelectedIndex, search = InboxSearch.Text, teacher = settings.TeacherIp,
        students = _inbox.Select(row => row.Entry).ToArray(), teachers = TeachersList.Items.OfType<InboxRowView>().Select(row => row.Entry).ToArray(),
        drafts = _drafts.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new { pair.Key, ticket = pair.Value.Capture(), pair.Value.Dirty, pair.Value.Clearing }).ToArray()
    });
    internal Task SmokeStageContactAttachmentAsync(string path) => AddAttachmentPathsAsync([path]);
}
