using System.IO;
using System.Text.Json;
using NekoChat.Core.Identity;
using NekoChat.Core.Storage;
using NekoChat.Core.V2;
using NekoChat.Windows.Identity;

namespace NekoChat.Windows;

public partial class App
{
    // Archived signed fixture only. The body limit follows the confirmed D-06 value; this does not activate signed identity in the classroom product.
    private const int MessagingFixtureLimit = V2Text.ProductMaxUtf8Bytes;
    private sealed record PublicFixtureIdentity(string Role, string PeerId, string PublicSpkiBase64);
    private string? messagingPeerId;
    private async Task<ChatWireConfiguration> PrepareMessagingFixtureAsync(MessagingSmokeOptions options)
    {
        Directory.CreateDirectory(options.Directory);
        string prefix = Path.Combine(options.Directory, options.Role);
        try
        {
            // Deliberately retained: every run needs a fresh output directory; old rendezvous files cannot produce a false PASS.
            using (new FileStream(prefix + ".fixture", FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            string root = ChatRuntime.RootDirectory(settings, isolated: true);
            var mine = await Task.Run(() => {
                using var store = ChatStore.Open(new(Path.Combine(root, "data")) { TextValidator = text => V2Text.Check(text, MessagingFixtureLimit) == V2TextProblem.None });
                var coordinator = new LocalIdentityCoordinator(store, WindowsIdentityVault.Create(Path.Combine(root, "identity")));
                Require(coordinator.Provision(store.GetLocalIdentitySnapshot(), () => true), "Synthetic identity was not freshly provisioned");
                var identity = store.GetLocalIdentitySnapshot();
                return new PublicFixtureIdentity(options.Role, identity.Id!, identity.PublicSpkiBase64!);
            });
            string publicPath = prefix + ".public.json", stage = publicPath + ".staging";
            await File.WriteAllTextAsync(stage, JsonSerializer.Serialize(mine)); File.Move(stage, publicPath);
            string other = options.Role == "alice" ? "bob" : "alice";
            string otherPath = Path.Combine(options.Directory, other + ".public.json");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await UntilAsync(() => File.Exists(otherPath), timeout.Token);
            // Public key only, bounded canonical SPKI. This is a fixture pin, never a real user's pairing consent.
            Require(new FileInfo(otherPath).Length <= 1024, "Oversized public fixture metadata");
            var peer = JsonSerializer.Deserialize<PublicFixtureIdentity>(await File.ReadAllTextAsync(otherPath)) ?? throw new InvalidDataException("Missing public fixture metadata.");
            byte[] spki = Convert.FromBase64String(peer.PublicSpkiBase64);
            using var canonical = V2Crypto.OpenCanonicalSigningKey(spki);
            Require(peer.Role == other && canonical is not null && V2Crypto.PeerId(spki) == peer.PeerId && peer.PeerId != mine.PeerId, "Invalid public fixture key");
            await Task.Run(() => {
                using var store = ChatStore.Open(new(Path.Combine(root, "data")));
                Require(store.RegisterPeer(peer.PeerId, PairingState.Paired, spki) is PeerRegistered, "Fixture key pin failed");
                store.TouchPeerSeen(peer.PeerId, "127.0.0.1"); // Route hint only, not a presence or LAN ownership assertion.
            });
            messagingPeerId = peer.PeerId;
            return new(MessagingFixtureLimit);
        }
        catch (Exception e)
        {
            File.WriteAllText(prefix + "-result.txt", "FAIL: isolated fixture setup: " + e.GetType().Name);
            throw;
        }
    }
}
