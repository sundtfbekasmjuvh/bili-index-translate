using BiliIndexTranslate.Core.Diagnostics;
using BiliIndexTranslate.Core.Selection;
using BiliIndexTranslate.Core.Settings;
using Xunit;

namespace BiliIndexTranslate.Core.Tests;

internal sealed class ReversibleProtector : ISecretProtector
{
    public string Scheme => "test";
    public string Protect(string plain) => "enc:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plain));
    public string? Unprotect(string protectedValue)
        => protectedValue.StartsWith("enc:") ? System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue[4..])) : null;
}

public class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bit-tests-" + Guid.NewGuid().ToString("N"));
    private string File_ => Path.Combine(_dir, "settings.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void MissingFile_YieldsDefaults()
    {
        var store = new SettingsStore(File_, new ReversibleProtector());
        var r = store.Load();
        Assert.False(r.WasMalformed);
        Assert.True(r.Settings.AutoTranslate);
        Assert.Equal("zh-Hans", r.Settings.TargetLanguage);
        Assert.Equal(AppSettings.DefaultModel, r.Settings.Model);
        Assert.Equal(AppSettings.DefaultApiKey, r.Settings.ApiKey);
        Assert.False(r.Settings.UseRelay);
    }

    [Fact]
    public void RoundTrip_ProtectsKey_OnDisk()
    {
        var store = new SettingsStore(File_, new ReversibleProtector());
        var s = new AppSettings { ApiKey = "sk-secret", Model = "Index-Translate-2B", UseRelay = true, TextScale = 1.2 };
        s.ExcludedProcesses.Add("KeePass");
        store.Save(s);

        var raw = File.ReadAllText(File_);
        Assert.DoesNotContain("sk-secret", raw);
        Assert.Contains("apiKeyProtected", raw);

        var r = store.Load();
        Assert.Equal("sk-secret", r.Settings.ApiKey);
        Assert.Equal("Index-Translate-2B", r.Settings.Model);
        Assert.True(r.Settings.UseRelay);
        Assert.Equal(1.2, r.Settings.TextScale);
        Assert.True(r.Settings.IsProcessExcluded("keepass"));
        Assert.False(r.WasMigrated);
    }

    [Fact]
    public void MalformedFile_IsBackedUp_AndDefaultsRestored()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(File_, "{ this is not json");
        var store = new SettingsStore(File_, new ReversibleProtector());
        var r = store.Load();
        Assert.True(r.WasMalformed);
        Assert.True(File.Exists(File_ + ".bad"));
        Assert.Equal(AppSettings.DefaultBaseUrl, r.Settings.BaseUrl);
    }

    [Fact]
    public void V1PlainKey_IsMigrated_AndUnknownFieldsIgnored_AndRangesClamped()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(File_, """
            {
              "schemaVersion": 1,
              "apiKey": "old-plain-key",
              "textScale": 9,
              "stabilizationDelayMs": 5,
              "targetLanguage": "klingon",
              "somethingNew": { "a": 1 },
              "excludedProcesses": [" notepad ", "", "NOTEPAD"]
            }
            """);
        var store = new SettingsStore(File_, new ReversibleProtector());
        var r = store.Load();
        Assert.True(r.WasMigrated);
        Assert.Equal("old-plain-key", r.Settings.ApiKey);
        Assert.Equal(1.6, r.Settings.TextScale);
        Assert.Equal(120, r.Settings.StabilizationDelayMs);
        Assert.Equal("zh-Hans", r.Settings.TargetLanguage);
        Assert.Single(r.Settings.ExcludedProcesses);

        store.Save(r.Settings);
        var raw = File.ReadAllText(File_);
        Assert.DoesNotContain("old-plain-key", raw);
        Assert.DoesNotContain("\"apiKey\"", raw);
        Assert.Contains("\"schemaVersion\": 2", raw);
    }

    [Fact]
    public void UndecryptableKey_FallsBackToDefault_AndReportsIt()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(File_, """{ "schemaVersion": 2, "apiKeyProtected": "garbage", "apiKeyScheme": "dpapi-user" }""");
        var r = new SettingsStore(File_, new ReversibleProtector()).Load();
        Assert.True(r.SecretUnavailable);
        Assert.Equal(AppSettings.DefaultApiKey, r.Settings.ApiKey);
        Assert.False(r.WasMalformed);
    }

    [Fact]
    public void InvalidEnumValue_IsTreatedAsMalformed_NotCrash()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(File_, """{ "schemaVersion": 2, "theme": "Neon" }""");
        var r = new SettingsStore(File_, new ReversibleProtector()).Load();
        Assert.True(r.WasMalformed);
        Assert.Equal(ThemePreference.System, r.Settings.Theme);
    }
}

public class SelectionPolicyTests
{
    private static SelectionSnapshot Snap(string text, EditableKind editable = EditableKind.NotEditable, string process = "notepad")
        => new(text, null, 1, 1, process, editable, SelectionOrigin.UiAutomation, 1);

    [Fact]
    public void DefaultPolicy()
    {
        var s = new AppSettings().Normalize();
        Assert.Null(SelectionPolicy.ReasonToSkip(Snap("hello"), s, false));
        Assert.NotNull(SelectionPolicy.ReasonToSkip(Snap("h"), s, false));
        Assert.NotNull(SelectionPolicy.ReasonToSkip(Snap("12345 67"), s, false));
        Assert.NotNull(SelectionPolicy.ReasonToSkip(Snap(new string('a', 3001)), s, false));
        Assert.NotNull(SelectionPolicy.ReasonToSkip(Snap("hello", EditableKind.SingleLineInput), s, false));
        Assert.Null(SelectionPolicy.ReasonToSkip(Snap("hello", EditableKind.MultiLineEditor), s, false));
    }

    [Fact]
    public void ExplicitRequest_BypassesAutoPolicies_ButNotLength()
    {
        var s = new AppSettings().Normalize();
        s.SetProcessExcluded("notepad", true);
        Assert.NotNull(SelectionPolicy.ReasonToSkip(Snap("hello"), s, false));
        Assert.Null(SelectionPolicy.ReasonToSkip(Snap("hello"), s, true));
        Assert.Null(SelectionPolicy.ReasonToSkip(Snap("hello", EditableKind.SingleLineInput), s, true));
        Assert.NotNull(SelectionPolicy.ReasonToSkip(Snap(new string('a', 3001)), s, true));
    }

    [Fact]
    public void EditableClassification()
    {
        Assert.Equal(EditableKind.NotEditable, SelectionPolicy.ClassifyEditable(false, 20, 16));
        Assert.Equal(EditableKind.SingleLineInput, SelectionPolicy.ClassifyEditable(true, 30, 16));
        Assert.Equal(EditableKind.MultiLineEditor, SelectionPolicy.ClassifyEditable(true, 400, 16));
    }
}

public class DiagnosticsTests
{
    [Fact]
    public void Log_IsBounded_AndOrdered()
    {
        var log = new DiagnosticsLog(5);
        for (var i = 0; i < 12; i++) log.Info("t", "m" + i);
        var snap = log.Snapshot();
        Assert.Equal(5, snap.Count);
        Assert.Equal("m7", snap[0].Message);
        Assert.Equal("m11", snap[4].Message);
        Assert.Equal(DiagnosticsLog.Fingerprint("abc"), DiagnosticsLog.Fingerprint("abc"));
        Assert.NotEqual(DiagnosticsLog.Fingerprint("abc"), DiagnosticsLog.Fingerprint("abd"));
    }

    [Fact]
    public void TimingStats_Percentiles()
    {
        var stats = new TimingStats(16);
        for (var i = 1; i <= 10; i++) stats.Record(TimingCategory.Probe, i * 10);
        var s = stats.Summary(TimingCategory.Probe)!;
        Assert.Equal(10, s.Count);
        Assert.Equal(55, s.P50, 1);
        Assert.Equal(95.5, s.P95, 1);
        Assert.Equal(100, s.Max);
        Assert.Null(stats.Summary(TimingCategory.Http));
        stats.Record(TimingCategory.Http, double.NaN);
        Assert.Null(stats.Summary(TimingCategory.Http));
    }

    [Fact]
    public void SupportRegistry_Classifies()
    {
        var reg = new AppSupportRegistry();
        reg.RecordSuccess("chrome", true);
        reg.RecordSuccess("acrobat", false);
        reg.RecordUnsupported("game");
        reg.RecordSuccess("mixed", true);
        reg.RecordFailure("mixed");
        var map = reg.Snapshot().ToDictionary(e => e.ProcessName);
        Assert.Equal(SupportLevel.Works, map["chrome"].Level);
        Assert.Equal(SupportLevel.Partial, map["acrobat"].Level);
        Assert.Equal(SupportLevel.Unsupported, map["game"].Level);
        Assert.Equal(SupportLevel.Partial, map["mixed"].Level);
    }
}
