using System.Collections;
using System.IO;
using System.Reflection;
using NekoChat.Core.Storage;

namespace NekoChat.TestFixtures;

// Shared by the native smoke and a host-side contract check. Never an ordinary startup path.
internal static class ContactsUiSmokeSeed
{
    internal const string OldIp = "10.251.232.10";
    internal static void CreateV13(string profile, Action<string> assertSafeDirectory)
    {
        assertSafeDirectory(profile);
        if (!Path.GetFileName(Path.GetFullPath(profile)).StartsWith("contacts-profile-", StringComparison.Ordinal) || Directory.Exists(Path.Combine(profile, "data")))
            throw new InvalidOperationException("Contacts seed requires a new, explicitly isolated profile.");
        // Historical migrations stay internal to Core. Confine reflection to fixture setup,
        // rather than adding a product friend/API or duplicating the frozen schema.
        Type migration = typeof(ChatStore).Assembly.GetType("NekoChat.Core.Storage.Migration", throwOnError: true)!;
        var defaults = (IEnumerable)migration.GetProperty("Default", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        Array legacy = Array.CreateInstance(migration, 13); int index = 0;
        foreach (object item in defaults) { if (index == 13) break; legacy.SetValue(item, index++); }
        if (index != 13) throw new InvalidOperationException("Historical v13 fixture unavailable.");
        var options = new ChatStoreOptions(Path.Combine(profile, "data"));
        typeof(ChatStoreOptions).GetProperty("Migrations", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(options, legacy);
        using var store = ChatStore.Open(options);
        var received = (ClassroomStored)store.ReceiveClassroomMessage(OldIp, new(Guid.NewGuid().ToString("D"), "合成舊版歷史：保留這則訊息"), () => true);
        store.SaveClassroomDraft(received.Principal, "合成升級前草稿", 0, 1);
        if (received.Principal.IsRoster || store.GetReadReceiptPolicy() is not { Enabled: false, Generation: 0 })
            throw new InvalidOperationException("Fixture must start with a real v13 unknown history and unset receipts.");
    }
}
