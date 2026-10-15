namespace BiliIndexTranslate.Core.Translation;

public enum TranslationErrorKind
{
    Configuration,
    Network,
    Timeout,
    Http,
    InvalidResponse,
    EmptyResult,
    Cancelled,
}

/// <summary>A concise, user-facing error plus the route it happened on (for honest diagnostics).</summary>
public sealed record TranslationError(TranslationErrorKind Kind, string Message, RequestRoute Route, int? StatusCode = null)
{
    public bool IsRetryable => Kind is TranslationErrorKind.Network or TranslationErrorKind.Timeout
        || (Kind == TranslationErrorKind.Http && StatusCode is null or >= 500 or 429);

    /// <summary>Short suggestion rendered under the message; null when there is nothing actionable.</summary>
    public string? Hint => Kind switch
    {
        TranslationErrorKind.Http when StatusCode == 412 && Route == RequestRoute.Direct =>
            "The service rejected the direct request (412). You can enable the relay in Settings → Service; nothing is switched automatically.",
        TranslationErrorKind.Http when StatusCode == 412 && Route == RequestRoute.Relay =>
            "The relay was also rejected (412). Check the endpoint and key in Settings.",
        TranslationErrorKind.Http when StatusCode is 401 or 403 => "Check the API key in Settings → Service.",
        TranslationErrorKind.Http when StatusCode == 404 => "Check the endpoint URL (base or full /chat/completions URL).",
        TranslationErrorKind.Http when StatusCode == 429 => "The service is rate limiting; wait a moment and retry.",
        TranslationErrorKind.Timeout => "The service did not answer in time. Retry, or try a smaller model.",
        TranslationErrorKind.Network => "Check the network connection or the endpoint URL.",
        TranslationErrorKind.InvalidResponse => "The endpoint did not return a chat completion. Check the URL and relay settings.",
        TranslationErrorKind.Configuration => "Open Settings → Service to fix the configuration.",
        _ => null,
    };

    public override string ToString() => StatusCode is { } c ? $"{Kind} {c} via {Route}: {Message}" : $"{Kind} via {Route}: {Message}";
}

public sealed record TranslationRequest(
    string Text,
    string TargetLanguageCode,
    string TargetLanguagePromptName,
    string SourceLanguage,
    string Model,
    string BaseUrl,
    bool UseRelay,
    string RelayPrefix,
    string ApiKey);

public sealed record TranslationResult(string Translation, RequestRoute Route, double ElapsedMs, string Model);

/// <summary>Raw transport reply; separated so the translator can be tested with a fake transport.</summary>
public sealed record TransportResponse(int StatusCode, string Body, string? ContentType);

public interface ITranslationTransport
{
    Task<TransportResponse> PostJsonAsync(Uri url, string apiKey, string jsonBody, TimeSpan timeout, CancellationToken ct);
}

public sealed class TranslationException : Exception
{
    public TranslationException(TranslationError error) : base(error.Message) { Error = error; }
    public TranslationError Error { get; }
}
