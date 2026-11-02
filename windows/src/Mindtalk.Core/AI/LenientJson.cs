using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mindtalk.Core.AI;

/// <summary>
/// JSON as a language model writes it: wrapped in ```json fences or a sentence, with trailing
/// commas, “curly” quotes, comments, or cut off before the end. Finds the object and mends
/// what can be mended; null when nothing usable is there.
/// </summary>
public static class LenientJson
{
    private static readonly JsonDocumentOptions Loose = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    public static JsonObject? ParseObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var start = text.IndexOf('{');
        if (start < 0) return null;
        var body = text[start..];

        if (TryParse(Cut(body), out var node)) return node;
        var repaired = Repair(body);
        return TryParse(repaired, out node) ? node : null;
    }

    private static bool TryParse(string text, out JsonObject? node)
    {
        try
        {
            node = JsonNode.Parse(text, documentOptions: Loose) as JsonObject;
            return node is not null;
        }
        catch (JsonException) { node = null; return false; }
    }

    /// The first balanced object (anything the model wrote after it is dropped).
    private static string Cut(string text)
    {
        var depth = 0;
        var inString = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (c == '\\') i++;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') inString = true;
            else if (c is '{' or '[') depth++;
            else if (c is '}' or ']' && --depth == 0) return text[..(i + 1)];
        }
        return text;
    }

    /// <summary>
    /// Mends the usual mistakes: curly quotes, single-quoted strings, raw line breaks inside
    /// strings, a missing comma between items, trailing commas, and an answer cut off midway
    /// (the open string, array and object are closed — the last, unfinished item is dropped).
    /// </summary>
    public static string Repair(string text)
    {
        text = text.Replace('“', '"').Replace('”', '"').Replace('„', '"');
        text = Cut(text);
        var sb = new StringBuilder(text.Length + 16);
        var stack = new Stack<char>();
        var inString = false;
        var quote = '"';
        // Where the output last stood at a complete value, to cut back to if it ends mid-item.
        var lastGood = 0;
        var lastGoodStack = Array.Empty<char>();

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (c == '\\' && i + 1 < text.Length) { sb.Append(c).Append(text[++i]); continue; }
                if (c == quote) { sb.Append('"'); inString = false; continue; }
                if (c == '"') { sb.Append("\\\""); continue; }   // a " inside a '…' string
                if (c == '\n') { sb.Append("\\n"); continue; }
                if (c == '\r') continue;
                if (c == '\t') { sb.Append("\\t"); continue; }
                sb.Append(c);
                continue;
            }
            switch (c)
            {
                case '"' or '\'':
                    // Two values with nothing between them: a comma was left out.
                    if (NeedsComma(sb)) sb.Append(',');
                    inString = true; quote = c; sb.Append('"');
                    break;
                case '{' or '[':
                    if (NeedsComma(sb)) sb.Append(',');
                    stack.Push(c == '{' ? '}' : ']');
                    sb.Append(c);
                    break;
                case '}' or ']':
                    TrimTrailingComma(sb);
                    if (stack.Count > 0) sb.Append(stack.Pop());
                    lastGood = sb.Length; lastGoodStack = stack.ToArray();
                    break;
                case ',':
                    TrimTrailingComma(sb);
                    lastGood = sb.Length; lastGoodStack = stack.ToArray();
                    sb.Append(c);
                    break;
                default:
                    // At the start of a bare value (not in the middle of one: 12, true).
                    if ((char.IsLetterOrDigit(c) || c == '-') && !(sb.Length > 0 && (char.IsLetterOrDigit(sb[^1]) || sb[^1] is '.' or '-')))
                    {
                        if (NeedsComma(sb)) sb.Append(',');
                    }
                    sb.Append(c);
                    break;
            }
        }

        if (inString || stack.Count > 0)
        {
            // Cut off: go back to the last complete item and close what's open there.
            if (inString || !EndsCleanly(sb))
            {
                sb.Length = lastGood;
                stack = new Stack<char>(lastGoodStack.Reverse());
            }
            TrimTrailingComma(sb);
            while (stack.Count > 0) { TrimTrailingComma(sb); sb.Append(stack.Pop()); }
        }
        return sb.ToString();
    }

    private static bool EndsCleanly(StringBuilder sb)
    {
        for (var i = sb.Length - 1; i >= 0; i--)
        {
            if (char.IsWhiteSpace(sb[i])) continue;
            return sb[i] is '}' or ']' or '"' or '{' or '[' or ',' || char.IsLetterOrDigit(sb[i]);
        }
        return true;
    }

    // After a finished value ("…", }, ], a number or true/false/null), the next value needs a comma.
    private static bool NeedsComma(StringBuilder sb)
    {
        for (var i = sb.Length - 1; i >= 0; i--)
        {
            var c = sb[i];
            if (char.IsWhiteSpace(c)) continue;
            if (c is '"' or '}' or ']')
            {
                // (A key's closing quote is always followed by ':' before anything else comes.)
                return true;
            }
            if (char.IsLetterOrDigit(c))
            {
                // the tail of a bare literal: true, false, null, 12
                var j = i;
                while (j >= 0 && (char.IsLetterOrDigit(sb[j]) || sb[j] is '.' or '-')) j--;
                var word = sb.ToString(j + 1, i - j);
                return word is "true" or "false" or "null" || double.TryParse(word, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _);
            }
            return false;
        }
        return false;
    }

    private static void TrimTrailingComma(StringBuilder sb)
    {
        var i = sb.Length - 1;
        while (i >= 0 && char.IsWhiteSpace(sb[i])) i--;
        if (i >= 0 && sb[i] == ',') sb.Length = i;
    }

    // ---- Reading values leniently ----------------------------------------------------

    public static string? Str(JsonNode? node, params string[] keys)
    {
        if (node is not JsonObject obj) return node is JsonValue v ? AsString(v) : null;
        foreach (var key in keys)
            foreach (var (k, value) in obj)
                if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase) && value is JsonValue jv && AsString(jv) is { Length: > 0 } s)
                    return s.Trim();
        return null;
    }

    private static string? AsString(JsonValue value) => value.GetValueKind() switch
    {
        JsonValueKind.String => value.GetValue<string>(),
        JsonValueKind.Number => value.ToJsonString(),
        _ => null,
    };

    public static double? Num(JsonNode? node, params string[] keys)
    {
        if (node is not JsonObject obj) return null;
        foreach (var key in keys)
            foreach (var (k, value) in obj)
            {
                if (!string.Equals(k, key, StringComparison.OrdinalIgnoreCase) || value is not JsonValue jv) continue;
                if (jv.GetValueKind() == JsonValueKind.Number) return jv.GetValue<double>();
                if (jv.GetValueKind() == JsonValueKind.String)
                {
                    var s = jv.GetValue<string>().Trim().TrimStart('L', 'l', '#', '[').TrimEnd(']');
                    if (double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)) return d;
                }
            }
        return null;
    }

    public static JsonArray Arr(JsonNode? node, params string[] keys)
    {
        if (node is JsonObject obj)
            foreach (var key in keys)
                foreach (var (k, value) in obj)
                    if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase) && value is JsonArray a) return a;
        return [];
    }
}
