using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NekoChat.Windows;

internal sealed record MessagingSmokeOptions(string Directory, string Role, int ListenPort, int PeerPort)
{
    internal static MessagingSmokeOptions? Parse(string[] args)
    {
        if (args.Length == 0 || args[0] != "--messaging-smoke-test") return null;
        if (args.Length != 5 || args[2] is not ("alice" or "bob") ||
            !int.TryParse(args[3], out int listen) || !int.TryParse(args[4], out int peer) ||
            listen is < 1024 or > 65535 || peer is < 1024 or > 65535 || listen == peer)
            throw new ArgumentException("Invalid messaging smoke-test arguments.");
        return new(Path.GetFullPath(args[1]), args[2], listen, peer);
    }
}

public partial class App
{
    private async Task RunMessagingSmokeTestAsync(MessagingSmokeOptions options)
    {
        Directory.CreateDirectory(options.Directory);
        string prefix = Path.Combine(options.Directory, options.Role);
        string other = options.Role == "alice" ? "bob" : "alice";
        string first = "訊息驗收 A → B：下課見 🐈\n第二行中文與 emoji";
        string reply = "訊息驗收 B → A：收到，等你一起走 🐾";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            Require(directListener?.Running == true, "App listener failed to start");
            Require(chat.IsVisible && pet.IsVisible, "Actual app windows are not visible");
            File.WriteAllText(prefix + ".ready", "ready");
            await UntilAsync(() => File.Exists(Path.Combine(options.Directory, other + ".ready")), timeout.Token);

            if (options.Role == "alice")
            {
                SendFromButton(first);
                await UntilAsync(() => chat.SendButton.IsEnabled, timeout.Token);
                VerifySent(first);
                await UntilAsync(() => chat.History.Text.Contains("127.0.0.1：" + reply), timeout.Token);
                VerifyIncoming(reply);
            }
            else
            {
                await UntilAsync(() => chat.History.Text.Contains("127.0.0.1：" + first), timeout.Token);
                VerifyIncoming(first);
                SendFromButton(reply);
                await UntilAsync(() => chat.SendButton.IsEnabled, timeout.Token);
                VerifySent(reply);
            }

            Capture(chat, prefix + "-chat.png");
            File.WriteAllText(prefix + ".received", "received");
            await UntilAsync(() => File.Exists(Path.Combine(options.Directory, other + ".received")), timeout.Token);
            await RunReactionMessagingChecksAsync(options, timeout.Token);

            // Use an unbound peer port while keeping this app's receiver healthy.
            using (var reservation = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0))
            {
                reservation.Start();
                directSendPort = ((System.Net.IPEndPoint)reservation.LocalEndpoint).Port;
                reservation.Stop();
            }
            const string failed = "未送達的訊息必須保留在草稿";
            SendFromButton(failed);
            await UntilAsync(() => chat.SendButton.IsEnabled, timeout.Token);
            Require(chat.Draft.Text == failed, "Failed send cleared the draft");
            Require(!chat.History.Text.Contains(failed), "Failed send appeared as delivered history");
            Require(chat.Status.Text.Contains("草稿保留") && !chat.Status.Text.Contains("已送達"),
                "Failed send did not show an error");
            chat.UpdateLayout();
            var status = chat.Status;
            var fullText = new FormattedText(status.Text, System.Globalization.CultureInfo.CurrentCulture,
                status.FlowDirection, new Typeface(status.FontFamily, status.FontStyle, status.FontWeight, status.FontStretch),
                status.FontSize, status.Foreground, VisualTreeHelper.GetDpi(status).PixelsPerDip)
                { MaxTextWidth = status.ActualWidth };
            Require(status.ActualHeight + 1 >= fullText.Height, "Failure explanation is clipped by the window layout");
            Capture(chat, prefix + "-failure.png");
            File.WriteAllText(prefix + "-result.txt",
                "PASS: two independent app processes; real WPF send button; bidirectional TCP messages; Unicode/multiline text; " +
                "received history; visible cat bubble; bubble reply target; successful receipt and draft clearing; " +
                "unreachable peer error, retained draft and restored send button. Same-host loopback only; classroom LAN remains unverified.");
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            File.WriteAllText(prefix + "-result.txt", "FAIL: " + ex);
            Environment.Exit(1);
        }

        void SendFromButton(string text)
        {
            chat.RecipientIp.Text = "127.0.0.1";
            chat.Draft.Text = text;
            chat.SendButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        void VerifySent(string text)
        {
            Require(chat.History.Text.Contains("我 → 127.0.0.1：" + text), "Sender history missing real delivery");
            Require(chat.Draft.Text.Length == 0 && chat.Status.Text.Contains("已送達"), "Receipt did not update the sender UI");
        }
        void VerifyIncoming(string text)
        {
            Require(pet.Bubble.Visibility == Visibility.Visible && pet.BubbleText.Text.Contains(text),
                "Network message did not appear in the cat bubble");
            Require(!pet.BubbleText.Text.Contains("127.0.0.1"), "Source IP leaked into the pet bubble");
            Capture(pet, prefix + "-bubble.png");
            chat.RecipientIp.Text = "192.168.1.254";
            pet.Bubble.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(chat.IsVisible && chat.RecipientIp.Text == "127.0.0.1", "Bubble click did not select the real sender");
        }
    }

    private static async Task UntilAsync(Func<bool> predicate, CancellationToken cancellation)
    {
        while (!predicate()) await Task.Delay(50, cancellation);
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static void Capture(Window window, string path)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),
            (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
