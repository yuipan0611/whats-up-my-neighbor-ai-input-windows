using System.Text;
using System.Text.Json;

namespace NekoChat.Core.V2;

public enum V2JsonError { None, TooLarge, InvalidUtf8, Syntax, NotObject, DuplicateKey, TooDeep, TooManyItems, LoneSurrogate }

public enum V2JsonKind { String, Number, True, False, Null, Array, Object }

/// <summary>Immutable parsed value. Numbers keep their raw token text (never a double), because wire fields such as max_text_bytes are checked as literals.</summary>
public sealed class V2JsonValue
{
    internal V2JsonValue(V2JsonKind kind, string? text = null, IReadOnlyList<V2JsonValue>? items = null, V2JsonObject? obj = null)
    { Kind = kind; Text = text; Items = items; Object = obj; }

    public V2JsonKind Kind { get; }
    /// <summary>String value (Kind=String) or the raw number token (Kind=Number).</summary>
    public string? Text { get; }
    public IReadOnlyList<V2JsonValue>? Items { get; }
    public V2JsonObject? Object { get; }
}

public sealed class V2JsonObject
{
    private readonly Dictionary<string, V2JsonValue> _members;
    internal V2JsonObject(Dictionary<string, V2JsonValue> members) { _members = members; }

    public int Count => _members.Count;
    public IEnumerable<string> Keys => _members.Keys;
    public bool ContainsKey(string key) => _members.ContainsKey(key);
    public bool TryGet(string key, out V2JsonValue value) => _members.TryGetValue(key, out value!);

    /// <summary>True only if the key set equals <paramref name="keys"/> exactly: nothing missing, nothing extra.</summary>
    public bool HasExactKeys(params string[] keys)
    {
        if (_members.Count != keys.Length) return false;
        foreach (var k in keys) if (!_members.ContainsKey(k)) return false;
        return true;
    }

    public bool TryGetString(string key, out string value)
    {
        value = "";
        if (!_members.TryGetValue(key, out var v) || v.Kind != V2JsonKind.String) return false;
        value = v.Text!;
        return true;
    }

    public bool TryGetNumberToken(string key, out string token)
    {
        token = "";
        if (!_members.TryGetValue(key, out var v) || v.Kind != V2JsonKind.Number) return false;
        token = v.Text!;
        return true;
    }

    public bool IsNull(string key) => _members.TryGetValue(key, out var v) && v.Kind == V2JsonKind.Null;

    public bool TryGetStringArray(string key, out string[] values)
    {
        values = Array.Empty<string>();
        if (!_members.TryGetValue(key, out var v) || v.Kind != V2JsonKind.Array) return false;
        var list = new string[v.Items!.Count];
        for (int i = 0; i < list.Length; i++)
        {
            if (v.Items[i].Kind != V2JsonKind.String) return false;
            list[i] = v.Items[i].Text!;
        }
        values = list;
        return true;
    }
}

public readonly record struct V2JsonLimits(int MaxBytes, int MaxDepth, int MaxItems)
{
    /// <summary>Body allowance includes worst-case JSON escaping. The application must supply its agreed text policy.</summary>
    public static V2JsonLimits Message(int maxTextBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxTextBytes);
        return new(checked(6 * maxTextBytes + 4096), 8, 64);
    }
    /// <summary>Small control messages (hello, ack, receipt, pairing): 8 KiB.</summary>
    public static V2JsonLimits Control => new(8 * 1024, 8, 64);
}

/// <summary>
/// Strict reader for v2 wire JSON (N002A-v2.3 §5.3, §5.4 invalid_payload). It rejects, before any field is trusted:
/// oversize input (checked first, nothing is allocated for it), invalid UTF-8 and a BOM, malformed JSON, comments, trailing commas,
/// content after the root value, a non-object root, duplicate member names (after unescaping), excess nesting depth,
/// excess array/object members and unpaired surrogates (including ones written as \uD800 escapes).
/// </summary>
public static class V2Json
{
    public static bool TryParseObject(ReadOnlySpan<byte> utf8, V2JsonLimits limits, out V2JsonObject? result, out V2JsonError error)
    {
        result = null;
        if (limits.MaxBytes <= 0 || limits.MaxDepth is < 1 or > 64 || limits.MaxItems <= 0) throw new ArgumentOutOfRangeException(nameof(limits));
        if (utf8.Length > limits.MaxBytes) { error = V2JsonError.TooLarge; return false; }
        try { _ = new UTF8Encoding(false, true).GetCharCount(utf8); }
        catch (ArgumentException) { error = V2JsonError.InvalidUtf8; return false; }
        if (utf8.Length >= 3 && utf8[0] == 0xEF && utf8[1] == 0xBB && utf8[2] == 0xBF) { error = V2JsonError.Syntax; return false; }

        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false, MaxDepth = limits.MaxDepth + 8 });
        try
        {
            if (!reader.Read()) { error = V2JsonError.Syntax; return false; }
            if (reader.TokenType != JsonTokenType.StartObject) { error = V2JsonError.NotObject; return false; }
            var err = V2JsonError.None;
            var obj = ReadObject(ref reader, limits, 1, ref err);
            if (obj is null) { error = err; return false; }
            if (reader.Read()) { error = V2JsonError.Syntax; return false; }
            result = obj; error = V2JsonError.None; return true;
        }
        catch (JsonException) { error = V2JsonError.Syntax; return false; }
        catch (InvalidOperationException) { error = V2JsonError.LoneSurrogate; return false; }
    }

    private static bool HasLoneSurrogate(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (char.IsHighSurrogate(c)) { if (i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) i++; else return true; }
            else if (char.IsLowSurrogate(c)) return true;
        }
        return false;
    }

    private static V2JsonObject? ReadObject(ref Utf8JsonReader reader, V2JsonLimits limits, int depth, ref V2JsonError error)
    {
        if (depth > limits.MaxDepth) { error = V2JsonError.TooDeep; return null; }
        var members = new Dictionary<string, V2JsonValue>(StringComparer.Ordinal);
        while (true)
        {
            if (!reader.Read()) { error = V2JsonError.Syntax; return null; }
            if (reader.TokenType == JsonTokenType.EndObject) return new V2JsonObject(members);
            if (reader.TokenType != JsonTokenType.PropertyName) { error = V2JsonError.Syntax; return null; }
            if (!ValidStringEscapes(reader.ValueSpan)) { error = V2JsonError.LoneSurrogate; return null; }
            string name = reader.GetString()!;
            if (HasLoneSurrogate(name)) { error = V2JsonError.LoneSurrogate; return null; }
            if (members.ContainsKey(name)) { error = V2JsonError.DuplicateKey; return null; }
            if (members.Count >= limits.MaxItems) { error = V2JsonError.TooManyItems; return null; }
            if (!reader.Read()) { error = V2JsonError.Syntax; return null; }
            var value = ReadValue(ref reader, limits, depth, ref error);
            if (value is null) return null;
            members[name] = value;
        }
    }

    private static V2JsonValue? ReadValue(ref Utf8JsonReader reader, V2JsonLimits limits, int depth, ref V2JsonError error)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
            {
                if (!ValidStringEscapes(reader.ValueSpan)) { error = V2JsonError.LoneSurrogate; return null; }
                string s = reader.GetString()!;
                if (HasLoneSurrogate(s)) { error = V2JsonError.LoneSurrogate; return null; }
                return new V2JsonValue(V2JsonKind.String, s);
            }
            case JsonTokenType.Number: return new V2JsonValue(V2JsonKind.Number, Encoding.ASCII.GetString(reader.ValueSpan));
            case JsonTokenType.True: return new V2JsonValue(V2JsonKind.True);
            case JsonTokenType.False: return new V2JsonValue(V2JsonKind.False);
            case JsonTokenType.Null: return new V2JsonValue(V2JsonKind.Null);
            case JsonTokenType.StartObject:
            {
                var o = ReadObject(ref reader, limits, depth + 1, ref error);
                return o is null ? null : new V2JsonValue(V2JsonKind.Object, obj: o);
            }
            case JsonTokenType.StartArray:
            {
                if (depth + 1 > limits.MaxDepth) { error = V2JsonError.TooDeep; return null; }
                var items = new List<V2JsonValue>();
                while (true)
                {
                    if (!reader.Read()) { error = V2JsonError.Syntax; return null; }
                    if (reader.TokenType == JsonTokenType.EndArray) return new V2JsonValue(V2JsonKind.Array, items: items.AsReadOnly());
                    if (items.Count >= limits.MaxItems) { error = V2JsonError.TooManyItems; return null; }
                    var item = ReadValue(ref reader, limits, depth + 1, ref error);
                    if (item is null) return null;
                    items.Add(item);
                }
            }
            default: error = V2JsonError.Syntax; return null;
        }
    }

    // Validate raw \u sequences before GetString can reject or normalize an unpaired surrogate.
    private static bool ValidStringEscapes(ReadOnlySpan<byte> raw)
    {
        static int Hex(byte b) => b is >= (byte)'0' and <= (byte)'9' ? b - '0' : b is >= (byte)'a' and <= (byte)'f' ? b - 'a' + 10 : b is >= (byte)'A' and <= (byte)'F' ? b - 'A' + 10 : -1;
        static int Code(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length < 4) return -1; int n = 0;
            for (int j = 0; j < 4; j++) { int digit = Hex(bytes[j]); if (digit < 0) return -1; n = n * 16 + digit; }
            return n;
        }
        for (int i = 0; i < raw.Length; i++)
        {
            if (raw[i] != '\\') continue;
            if (++i >= raw.Length) return false;
            if (raw[i] != 'u') continue;
            int unit = Code(raw[(i + 1)..]); if (unit < 0) return false; i += 4;
            if (unit is >= 0xdc00 and <= 0xdfff) return false;
            if (unit is < 0xd800 or > 0xdbff) continue;
            if (i + 6 >= raw.Length || raw[i + 1] != '\\' || raw[i + 2] != 'u') return false;
            int low = Code(raw[(i + 3)..]); if (low is < 0xdc00 or > 0xdfff) return false; i += 6;
        }
        return true;
    }
}
