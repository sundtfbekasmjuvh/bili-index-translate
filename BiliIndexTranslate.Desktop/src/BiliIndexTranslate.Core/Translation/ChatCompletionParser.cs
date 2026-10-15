using System.Text;
using System.Text.Json;

namespace BiliIndexTranslate.Core.Translation;

/// <summary>
/// Extracts the translation from an OpenAI-compatible chat completion body. Defensive by design:
/// HTML or empty bodies are reported as invalid responses, never rendered as a translation.
/// </summary>
public static class ChatCompletionParser
{
    public static bool TryParse(string? body, out string translation, out string? error)
    {
        translation = string.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(body)) { error = "Empty response body."; return false; }

        var trimmed = body.AsSpan().TrimStart();
        if (LooksLikeHtml(trimmed)) { error = "The endpoint returned an HTML page instead of JSON."; return false; }

        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch (JsonException) { error = "Response is not valid JSON."; return false; }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { error = "Response JSON is not an object."; return false; }

            if (root.TryGetProperty("error", out var errEl))
            {
                error = DescribeError(errEl);
                return false;
            }

            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            {
                error = "Response has no choices.";
                return false;
            }

            var first = choices[0];
            string? content = null;
            if (first.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object)
                content = ReadContent(message);
            if (content is null && first.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
                content = ReadContent(delta);
            if (content is null && first.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
                content = textEl.GetString();

            if (content is null) { error = "Response has no message content."; return false; }
            if (content.Trim().Length == 0) { error = "The service returned an empty translation."; return false; }

            translation = content.Trim();
            return true;
        }
    }

    private static string? ReadContent(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var c)) return null;
        switch (c.ValueKind)
        {
            case JsonValueKind.String:
                return c.GetString();
            case JsonValueKind.Array:
            {
                var sb = new StringBuilder();
                foreach (var part in c.EnumerateArray())
                {
                    if (part.ValueKind == JsonValueKind.String) sb.Append(part.GetString());
                    else if (part.ValueKind == JsonValueKind.Object && part.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String) sb.Append(t.GetString());
                }
                return sb.ToString();
            }
            default:
                return null;
        }
    }

    private static string DescribeError(JsonElement errEl)
    {
        if (errEl.ValueKind == JsonValueKind.String) return Shorten(errEl.GetString() ?? "Service error.");
        if (errEl.ValueKind == JsonValueKind.Object && errEl.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
            return Shorten(m.GetString() ?? "Service error.");
        return "The service reported an error.";
    }

    private static bool LooksLikeHtml(ReadOnlySpan<char> s)
        => s.StartsWith("<!doctype", StringComparison.OrdinalIgnoreCase)
           || s.StartsWith("<html", StringComparison.OrdinalIgnoreCase)
           || s.StartsWith("<head", StringComparison.OrdinalIgnoreCase)
           || s.StartsWith("<body", StringComparison.OrdinalIgnoreCase)
           || (s.Length > 0 && s[0] == '<' && !s.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase));

    private static string Shorten(string s)
    {
        s = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return s.Length > 160 ? s[..160] + "…" : s;
    }
}
