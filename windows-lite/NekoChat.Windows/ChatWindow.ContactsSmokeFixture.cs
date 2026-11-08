using System.IO;
using System.Windows;

namespace NekoChat.Windows;

// Explicit, per-window hooks. Installation requires the same isolated --ui-test-data profile
// as the attached runtime and Settings; there is no global switch or everyday-profile fallback.
internal sealed class ContactsUiSmokeHooks
{
    internal Func<Window, Task>? DriveDialog;
    internal readonly List<(string Title, string Text)> Prompts = [];
    internal Exception? DialogFailure;
    internal Task? ContactAction;
}

public partial class ChatWindow
{
    private ContactsUiSmokeHooks? _contactsUiSmoke;
    internal void InstallContactsUiSmokeHooks(string profile, ContactsUiSmokeHooks hooks)
    {
        UiTestOptions.AssertSafeDataDirectory(profile);
        string root = Path.GetFullPath(profile);
        if (_runtime is not { IsolatedData: true } runtime ||
            !string.Equals(Path.GetFullPath(runtime.DataDirectory), Path.Combine(root, "data"), StringComparison.OrdinalIgnoreCase) ||
            settings.PersistencePath is null || !string.Equals(Path.GetFullPath(settings.PersistencePath), Path.Combine(root, "settings.json"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Contacts UI hooks require their exact isolated profile.");
        _contactsUiSmoke = hooks;
    }
    private MessageBoxResult ContactPrompt(string text, string title, MessageBoxButton buttons,
        MessageBoxImage image, MessageBoxResult defaultResult = MessageBoxResult.None, Window? owner = null)
    {
        if (_contactsUiSmoke is { } hook)
        {
            hook.Prompts.Add((title, text));
            return buttons == MessageBoxButton.OK ? MessageBoxResult.OK : MessageBoxResult.Yes;
        }
        return MessageBox.Show(owner ?? this, text, title, buttons, image, defaultResult);
    }
    private bool? ContactDialog(Window dialog)
    {
        if (_contactsUiSmoke is { } hook)
        {
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
    internal Task SmokeStageContactAttachmentAsync(string path) => AddAttachmentPathsAsync([path]);
}
