namespace BiliIndexTranslate.Core.Translation;

public enum RequestRoute { Direct, Relay }

public sealed record ResolvedEndpoint(Uri ChatCompletionsUrl, string NormalizedBase, RequestRoute Route)
{
    /// <summary>Host shown in diagnostics (never the full URL with query strings).</summary>
    public string DisplayHost => ChatCompletionsUrl.Host;
}

/// <summary>
/// Turns the user's endpoint input into the exact URL to POST. Accepts a base URL
/// (".../v1") or a full ".../chat/completions" URL, trims whitespace and slashes, never duplicates
/// the suffix, and only applies the relay prefix when relay use is explicitly enabled.
/// </summary>
public static class EndpointResolver
{
    public const string ChatCompletionsSuffix = "/chat/completions";

    public static bool TryResolve(string? baseUrl, bool useRelay, string? relayPrefix, out ResolvedEndpoint? endpoint, out string? error)
    {
        endpoint = null;
        error = null;

        var normalized = NormalizeBase(baseUrl);
        if (normalized is null) { error = "Endpoint is empty."; return false; }

        var full = normalized + ChatCompletionsSuffix;
        var route = RequestRoute.Direct;

        if (useRelay)
        {
            var relay = NormalizeBase(relayPrefix);
            if (relay is null) { error = "Relay is enabled but the relay URL is empty."; return false; }
            full = relay + ChatCompletionsSuffix;
            route = RequestRoute.Relay;
        }

        if (!Uri.TryCreate(full, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            error = "Endpoint must be an absolute http(s) URL.";
            return false;
        }

        endpoint = new ResolvedEndpoint(uri, route == RequestRoute.Relay ? NormalizeBase(relayPrefix)! : normalized, route);
        return true;
    }

    /// <summary>Returns the base without trailing slash and without a trailing /chat/completions suffix, or null if blank.</summary>
    public static string? NormalizeBase(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var s = input.Trim();
        // Keep query-less: drop fragments/queries silently (they would break the suffix logic).
        var q = s.IndexOfAny(['?', '#']);
        if (q >= 0) s = s[..q];
        s = s.TrimEnd('/');
        if (s.EndsWith(ChatCompletionsSuffix, StringComparison.OrdinalIgnoreCase))
            s = s[..^ChatCompletionsSuffix.Length].TrimEnd('/');
        return s.Length == 0 ? null : s;
    }
}
