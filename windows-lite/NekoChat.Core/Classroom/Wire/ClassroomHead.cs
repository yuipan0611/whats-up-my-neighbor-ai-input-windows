using System.Globalization;
using System.Text;

namespace NekoChat.Core.Classroom.Wire;

public enum ClassroomRoute { Hello, Message, Receipt, LegacyV1, SignedV2Hello, SignedV2Message, SignedV2Receipt, SignedV2Pair }

public readonly record struct ClassroomHeadResult(string Code, ClassroomRoute? Route, int Length)
{
    public bool Ok => Code == ClassroomCodes.Ok;
    /// <summary>The vector label: <c>ok|route|length</c> or the error code.</summary>
    public string Label => Ok ? $"ok|{ClassroomHead.RouteName(Route!.Value)}|{Length}" : Code;
}

/// <summary>
/// Request head check (contract §1, §3). The path is the first discriminator and is decided before any body is read or parsed;
/// unknown paths win over every later header error. Classroom, legacy v1 and signed V2 paths stay separate routes: this type only
/// names them, it never routes classroom data into the signed API or the reverse.
/// </summary>
public static class ClassroomHead
{
    public static string RouteName(ClassroomRoute route) => route switch
    {
        ClassroomRoute.Hello => "classroom.hello", ClassroomRoute.Message => "classroom.message", ClassroomRoute.Receipt => "classroom.receipt",
        ClassroomRoute.LegacyV1 => "legacy.v1", ClassroomRoute.SignedV2Hello => "signed.v2.hello", ClassroomRoute.SignedV2Message => "signed.v2.message",
        ClassroomRoute.SignedV2Receipt => "signed.v2.receipt", _ => "signed.v2.pair"
    };

    private static bool TryRoute(string path, out ClassroomRoute route)
    {
        switch (path)
        {
            case "/nekochat/classroom/v1/hello": route = ClassroomRoute.Hello; return true;
            case "/nekochat/classroom/v1/message": route = ClassroomRoute.Message; return true;
            case "/nekochat/classroom/v1/receipt": route = ClassroomRoute.Receipt; return true;
            case "/nekochat/v1/direct": route = ClassroomRoute.LegacyV1; return true;
            case "/nekochat/v2/hello": route = ClassroomRoute.SignedV2Hello; return true;
            case "/nekochat/v2/message": route = ClassroomRoute.SignedV2Message; return true;
            case "/nekochat/v2/receipt": route = ClassroomRoute.SignedV2Receipt; return true;
            case "/nekochat/v2/pair": route = ClassroomRoute.SignedV2Pair; return true;
            default: route = default; return false;
        }
    }

    /// <summary><paramref name="head"/> is the whole block including the final CRLF CRLF.</summary>
    public static ClassroomHeadResult Check(ReadOnlySpan<byte> head)
    {
        ClassroomHeadResult Fail(string code) => new(code, null, 0);
        if (head.Length > ClassroomWire.HeadMaxBytes) return Fail(ClassroomCodes.InvalidPayload);
        foreach (byte b in head) if (b is not (9 or 10 or 13) && (b < 32 || b > 126)) return Fail(ClassroomCodes.InvalidPayload);
        if (head.Length < 4 || !head[^4..].SequenceEqual("\r\n\r\n"u8)) return Fail(ClassroomCodes.InvalidPayload);
        string[] lines = Encoding.ASCII.GetString(head[..^4]).Split("\r\n", StringSplitOptions.None);
        string[] request = lines[0].Split(' ', StringSplitOptions.None);
        if (request.Length != 3 || request[0] != "POST" || request[2] != "HTTP/1.1") return Fail(ClassroomCodes.InvalidPayload);
        if (!TryRoute(request[1], out var route)) return Fail(ClassroomCodes.UnsupportedVersion);
        if (lines.Length < 2 || lines.Length > ClassroomWire.MaxHeaderLines) return Fail(ClassroomCodes.InvalidPayload);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            int colon = lines[i].IndexOf(':'); if (colon <= 0) return Fail(ClassroomCodes.InvalidPayload);
            string name = lines[i][..colon], value = lines[i][(colon + 1)..].Trim(' ', '\t');
            if (name.Any(c => !char.IsAsciiLetterOrDigit(c) && !"!#$%&'*+-.^_`|~".Contains(c))) return Fail(ClassroomCodes.InvalidPayload);
            if (value.Any(c => c < 32 && c != '\t' || c == 127) || !headers.TryAdd(name, value)) return Fail(ClassroomCodes.InvalidPayload);
        }
        if (!headers.TryGetValue("Host", out var host) || host.Length == 0 || headers.ContainsKey("Transfer-Encoding") || headers.ContainsKey("Expect") ||
            !headers.TryGetValue("Content-Length", out var rawLength) || rawLength.Length is < 1 or > 10 || !rawLength.All(char.IsAsciiDigit) ||
            !long.TryParse(rawLength, NumberStyles.None, CultureInfo.InvariantCulture, out long declared) || declared <= 0 ||
            !headers.TryGetValue("Content-Type", out var media)) return Fail(ClassroomCodes.InvalidPayload);
        string[] parts = media.Split(';', StringSplitOptions.TrimEntries);
        bool jsonType = parts[0].Equals("application/json", StringComparison.OrdinalIgnoreCase) &&
            (parts.Length == 1 || parts.Length == 2 && (parts[1].Equals("charset=utf-8", StringComparison.OrdinalIgnoreCase) || parts[1].Equals("charset=\"utf-8\"", StringComparison.OrdinalIgnoreCase)));
        if (!jsonType) return Fail(ClassroomCodes.InvalidPayload);
        // Up to 10 digits is a legal Content-Length (contract §3.3); it can exceed int.MaxValue, so it is compared as a long and only
        // narrowed after the route limit has accepted it. A too-large value is payload_too_large, never a malformed head.
        long limit = route switch { ClassroomRoute.Message => ClassroomWire.MessageBodyMaxBytes, ClassroomRoute.Hello or ClassroomRoute.Receipt => ClassroomWire.ControlBodyMaxBytes, _ => int.MaxValue };
        if (declared > limit) return Fail(ClassroomCodes.PayloadTooLarge);
        return new(ClassroomCodes.Ok, route, (int)declared);
    }

    public static ClassroomHeadResult Check(string head) => Check(Encoding.UTF8.GetBytes(head));
}
