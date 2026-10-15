using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace BiliIndexTranslate.Core.Settings;

/// <summary>
/// Protects secrets at rest. The Windows implementation uses DPAPI (CurrentUser scope);
/// tests use a reversible fake. A protector may refuse to unprotect (e.g. a file copied from another
/// user account), in which case the store falls back to the default key and reports it.
/// </summary>
public interface ISecretProtector
{
    string Protect(string plain);
    string? Unprotect(string protectedValue);
    string Scheme { get; }
}

/// <summary>No-op protector for non-Windows test environments. Never used in the shipped app.</summary>
public sealed class PlainTextSecretProtector : ISecretProtector
{
    public string Scheme => "plain";
    public string Protect(string plain) => plain;
    public string? Unprotect(string protectedValue) => protectedValue;
}

public sealed record SettingsLoadResult(AppSettings Settings, bool WasMalformed, bool WasMigrated, bool SecretUnavailable, string? Note);

/// <summary>
/// JSON file store with: tolerant parsing (unknown fields ignored, malformed file backed up as
/// *.bad and replaced by defaults), schema migration (v1 stored the key in plain text), and
/// protected secret storage. Writes are atomic (temp file + replace).
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private readonly ISecretProtector _protector;

    public SettingsStore(string filePath, ISecretProtector protector)
    {
        FilePath = filePath;
        _protector = protector;
    }

    public string FilePath { get; }

    public SettingsLoadResult Load()
    {
        if (!File.Exists(FilePath))
            return new SettingsLoadResult(new AppSettings().Normalize(), false, false, false, "no settings file; defaults");

        string raw;
        try { raw = File.ReadAllText(FilePath); }
        catch (Exception ex) { return new SettingsLoadResult(new AppSettings().Normalize(), true, false, false, $"unreadable settings file: {ex.GetType().Name}"); }

        JsonObject root;
        try
        {
            root = JsonNode.Parse(raw, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
                   ?? throw new JsonException("root is not an object");
        }
        catch (JsonException ex)
        {
            BackupMalformed();
            return new SettingsLoadResult(new AppSettings().Normalize(), true, false, false, $"malformed settings file ({ex.Message}); defaults restored, original kept as .bad");
        }

        var migrated = false;
        var secretUnavailable = false;
        string? note = null;

        var version = root["schemaVersion"]?.GetValue<int>() ?? 1;
        string? plainKey = null;
        if (root["apiKey"] is JsonValue v && v.TryGetValue<string>(out var legacyKey)) { plainKey = legacyKey; migrated = true; }
        root.Remove("apiKey");

        var protectedKey = root["apiKeyProtected"] is JsonValue pv && pv.TryGetValue<string>(out var pk) ? pk : null;
        var scheme = root["apiKeyScheme"] is JsonValue sv && sv.TryGetValue<string>(out var sc) ? sc : null;
        root.Remove("apiKeyProtected");
        root.Remove("apiKeyScheme");

        AppSettings settings;
        try
        {
            settings = root.Deserialize<AppSettings>(JsonOptions) ?? new AppSettings();
        }
        catch (JsonException ex)
        {
            BackupMalformed();
            return new SettingsLoadResult(new AppSettings().Normalize(), true, false, false, $"settings file has invalid values ({ex.Message}); defaults restored");
        }

        if (plainKey is not null)
        {
            settings.ApiKey = plainKey;
        }
        else if (protectedKey is not null)
        {
            var un = scheme == _protector.Scheme ? SafeUnprotect(protectedKey) : null;
            if (un is null)
            {
                secretUnavailable = true;
                note = "stored API key could not be decrypted on this account; default key in use";
                settings.ApiKey = AppSettings.DefaultApiKey;
            }
            else settings.ApiKey = un;
        }

        if (version < AppSettings.CurrentSchemaVersion) migrated = true;
        settings.Normalize();
        return new SettingsLoadResult(settings, false, migrated, secretUnavailable, note);
    }

    public void Save(AppSettings settings)
    {
        settings.Normalize();
        var node = JsonSerializer.SerializeToNode(settings, JsonOptions) as JsonObject ?? new JsonObject();
        node["apiKeyProtected"] = _protector.Protect(settings.ApiKey);
        node["apiKeyScheme"] = _protector.Scheme;

        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, node.ToJsonString(JsonOptions));
        if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
        else File.Move(tmp, FilePath);
    }

    private string? SafeUnprotect(string value)
    {
        try { return _protector.Unprotect(value); }
        catch { return null; }
    }

    private void BackupMalformed()
    {
        try
        {
            var bad = FilePath + ".bad";
            File.Copy(FilePath, bad, overwrite: true);
        }
        catch { /* best effort */ }
    }
}
