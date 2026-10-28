using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NekoChat.Core;

namespace NekoChat.Windows;

internal static class SmokeTest
{
    internal static int Run(string directory)
    {
        Directory.CreateDirectory(directory);
        try
        {
            // 1. Settings & Contacts Roundtrip
            var settings = new Settings { TeacherIp = "192.168.24.9", ReactionDisplayDevice = "unplugged-screen", Coat = "calico" }
                .WithContact("127.0.0.1", "測試同學")
                .WithContact("192.168.1.50", "隔壁座位");
            if (settings.ResolveDisplayName("127.0.0.1") != "測試同學 (127.0.0.1)")
                throw new Exception("Contact resolution failed");
            settings.Save();
            var reloaded = Settings.Load();
            if (reloaded.Contacts.Count < 2)
                throw new Exception("Settings contacts persistence failed");
            if (reloaded.TeacherIp != settings.TeacherIp || reloaded.ReactionDisplayDevice != settings.ReactionDisplayDevice || reloaded.Coat != settings.Coat)
                throw new Exception("Reaction settings or original coat were lost");

            // 2. Direct LAN Listener Configuration & State Check
            var directListener = new DirectLanListener(18768);
            if (directListener.Port != 18768) throw new Exception("DirectLanListener port mismatch");
            directListener.Start();
            if (!directListener.Running) throw new Exception("DirectLanListener failed to start");
            _ = directListener.DisposeAsync();

            // 3. UI, Cat Artwork & Coats Rendering
            var chat = new ChatWindow(settings);
            Settings? latest = null;
            chat.SettingsChanged += value => latest = value;
            chat.RememberCoat("tuxedo");
            if (latest?.TeacherIp != settings.TeacherIp || latest.ReactionDisplayDevice != settings.ReactionDisplayDevice || latest.Contacts.Count != 2)
                throw new Exception("Changing coat overwrote current contacts or reaction settings");
            if (typeof(Protocol).Assembly.GetType("NekoChat.Core.GroqWhisperProvider") is not null ||
                typeof(App).Assembly.GetType("NekoChat.Windows.CredentialStore") is not null ||
                typeof(App).Assembly.GetReferencedAssemblies().Any(a => a.Name?.StartsWith("NAudio") == true) ||
                chat.FindName("VoiceButton") is not null)
                throw new Exception("Classroom edition must not include voice/key handling");
            var pet = new PetWindow();
            pet.VerifyAnimationIntegration();

            foreach (var id in Appearance.Presets)
            {
                pet.SetArt(CatArtwork.Preset(id));
                pet.Notify("下課見 🐈", "127.0.0.1", displayName: "測試同學");
                var visual = (FrameworkElement)pet.Content;
                visual.Measure(new Size(280, 310));
                visual.Arrange(new Rect(0, 0, 280, 310));
                visual.UpdateLayout();
                var bitmap = new RenderTargetBitmap(280, 310, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                Save(bitmap, Path.Combine(directory, id + "-window.png"));

                var native = CatArtwork.Preset(id);
                var custom = CatArtwork.Render(Appearance.Load(CatArtwork.PresetJson(id), CatArtwork.PresetJson));
                Save(custom, Path.Combine(directory, id + "-custom-render.png"));
                if (native.PixelWidth != custom.PixelWidth || native.PixelHeight != custom.PixelHeight)
                    throw new Exception("Appearance dimensions changed");
            }

            chat.AddHistory("測試同學 (127.0.0.1)：你好呀！");
            chat.RecipientIp.Text = "127.0.0.1";
            chat.Draft.Text = "下課一起走！";
            chat.SetStatus("Windows CI 視窗繪製驗證完成");

            var chatVisual = (FrameworkElement)chat.Content;
            chatVisual.Measure(new Size(480, 740));
            chatVisual.Arrange(new Rect(0, 0, 480, 740));
            chatVisual.UpdateLayout();
            var chatBitmap = new RenderTargetBitmap(480, 740, 96, 96, PixelFormats.Pbgra32);
            chatBitmap.Render(chatVisual);
            Save(chatBitmap, Path.Combine(directory, "chat-window.png"));

            File.WriteAllText(Path.Combine(directory, "result.txt"),
                "PASS: listener startup, contact resolution, WPF UI layout, 9 native cat coats render and synthetic desktop pet bubble. Real message delivery is verified separately by --messaging-smoke-test.");
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(directory, "result.txt"), "FAIL: " + ex.GetType().Name + " " + ex.Message + "\n" + ex.StackTrace);
            return 1;
        }
    }

    private static void Save(BitmapSource source, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
