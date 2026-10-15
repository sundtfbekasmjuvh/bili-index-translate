using System.Text.Json.Serialization;

namespace BiliIndexTranslate.Core.Settings;

public enum ThemePreference { System, Light, Dark }

public enum EditableFieldPolicy
{
    /// <summary>Default: translate in documents and multi-line editors, skip address bars / search boxes / chat lines.</summary>
    SkipSingleLineInputs,
    SkipAllEditable,
    TranslateEverywhere,
}

public sealed record TargetLanguage(string Code, string DisplayName, string PromptName)
{
    public static readonly IReadOnlyList<TargetLanguage> All =
    [
        new("zh-Hans", "简体中文", "Simplified Chinese"),
        new("zh-Hant", "繁體中文", "Traditional Chinese"),
        new("en", "English", "English"),
        new("ja", "日本語", "Japanese"),
        new("ko", "한국어", "Korean"),
    ];

    public static TargetLanguage Resolve(string? code)
        => All.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase)) ?? All[0];
}

/// <summary>
/// User configuration. Plain data; validation/clamping lives in <see cref="Normalize"/> so that
/// malformed or hand-edited files never put the app into an impossible state.
/// </summary>
public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 2;

    public const string DefaultBaseUrl = "https://index-translate.bilibili.com/v1";
    public const string DefaultRelayPrefix = "https://seep.eu.org/https://index-translate.bilibili.com/v1";
    public const string DefaultApiKey = "bili";
    public const string DefaultModel = "Index-Translate-9B";
    public static readonly IReadOnlyList<string> KnownModels = ["Index-Translate-2B", "Index-Translate-9B", "Index-Translate-35B-A3B"];

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    // Reading
    public bool AutoTranslate { get; set; } = true;
    public string TargetLanguage { get; set; } = "zh-Hans";
    public string SourceLanguage { get; set; } = "auto";
    public double TextScale { get; set; } = 1.0;
    public ThemePreference Theme { get; set; } = ThemePreference.System;
    public bool ShowOriginalByDefault { get; set; }
    public EditableFieldPolicy EditableFieldPolicy { get; set; } = EditableFieldPolicy.SkipSingleLineInputs;
    public List<string> ExcludedProcesses { get; set; } = new();

    // Service
    public string BaseUrl { get; set; } = DefaultBaseUrl;
    public string Model { get; set; } = DefaultModel;
    public bool UseRelay { get; set; }
    public string RelayPrefix { get; set; } = DefaultRelayPrefix;

    /// <summary>In-memory only. Persisted through <see cref="SettingsStore"/> as a protected blob.</summary>
    [JsonIgnore]
    public string ApiKey { get; set; } = DefaultApiKey;

    // Fallback shortcut
    public bool HotkeyEnabled { get; set; } = true;
    public string Hotkey { get; set; } = "Alt+Q";

    // Timing / capture
    public int StabilizationDelayMs { get; set; } = 250;
    public int MinChars { get; set; } = 2;
    public int MaxChars { get; set; } = 3000;
    public bool UseUiaSelectionEvents { get; set; } = true;

    // Diagnostics / lifecycle
    public bool DiagnosticsFileLogging { get; set; }
    public bool OnboardingCompleted { get; set; }

    public AppSettings Clone()
    {
        var c = (AppSettings)MemberwiseClone();
        c.ExcludedProcesses = new List<string>(ExcludedProcesses);
        return c;
    }

    public bool IsProcessExcluded(string processName)
        => ExcludedProcesses.Any(p => string.Equals(p, processName, StringComparison.OrdinalIgnoreCase));

    public void SetProcessExcluded(string processName, bool excluded)
    {
        ExcludedProcesses.RemoveAll(p => string.Equals(p, processName, StringComparison.OrdinalIgnoreCase));
        if (excluded) ExcludedProcesses.Add(processName);
    }

    /// <summary>Clamps every value into a safe range and fills blanks with defaults. Idempotent.</summary>
    public AppSettings Normalize()
    {
        TargetLanguage = Settings.TargetLanguage.Resolve(TargetLanguage).Code;
        if (string.IsNullOrWhiteSpace(SourceLanguage)) SourceLanguage = "auto";
        TextScale = double.IsFinite(TextScale) ? Math.Clamp(Math.Round(TextScale, 2), 0.8, 1.6) : 1.0;
        if (!Enum.IsDefined(Theme)) Theme = ThemePreference.System;
        if (!Enum.IsDefined(EditableFieldPolicy)) EditableFieldPolicy = EditableFieldPolicy.SkipSingleLineInputs;
        ExcludedProcesses = (ExcludedProcesses ?? new List<string>())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        BaseUrl = string.IsNullOrWhiteSpace(BaseUrl) ? DefaultBaseUrl : BaseUrl.Trim();
        Model = string.IsNullOrWhiteSpace(Model) ? DefaultModel : Model.Trim();
        RelayPrefix = string.IsNullOrWhiteSpace(RelayPrefix) ? DefaultRelayPrefix : RelayPrefix.Trim();
        if (string.IsNullOrWhiteSpace(ApiKey)) ApiKey = DefaultApiKey;
        Hotkey = string.IsNullOrWhiteSpace(Hotkey) ? "Alt+Q" : Hotkey.Trim();

        StabilizationDelayMs = Math.Clamp(StabilizationDelayMs, 120, 800);
        MinChars = Math.Clamp(MinChars, 1, 20);
        MaxChars = Math.Clamp(MaxChars, 200, 20000);
        if (MinChars > MaxChars) MinChars = 1;
        SchemaVersion = CurrentSchemaVersion;
        return this;
    }

    public TargetLanguage ResolvedTargetLanguage => Settings.TargetLanguage.Resolve(TargetLanguage);
}
