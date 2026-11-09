namespace NekoChat.Core.Presentation;

public static class MessageMetadataPolicy
{
    public static string Format(string? utc, string? delivery)
    {
        string clock = DateTimeOffset.TryParse(utc, out var time) ? time.ToLocalTime().ToString("HH:mm") : "";
        if (string.IsNullOrWhiteSpace(delivery)) return clock;
        return clock.Length == 0 ? delivery : clock + " · " + delivery;
    }
}
