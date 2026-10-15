using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BiliIndexTranslate.App.Input;
using BiliIndexTranslate.Core.Settings;

namespace BiliIndexTranslate.App.Settings;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _original;
    private readonly ObservableCollection<string> _excluded;
    private readonly Func<AppSettings, CancellationToken, Task<(bool Ok, string Message)>> _tester;
    private readonly Action<AppSettings> _save;
    private readonly string _settingsPath;
    private CancellationTokenSource? _testCts;
    private bool _keyRevealed;

    public SettingsWindow(AppSettings current, string settingsPath,
        Func<AppSettings, CancellationToken, Task<(bool Ok, string Message)>> tester,
        Action<AppSettings> save)
    {
        InitializeComponent();
        _original = current.Clone();
        _excluded = new ObservableCollection<string>(current.ExcludedProcesses);
        _tester = tester;
        _save = save;
        _settingsPath = settingsPath;
        Populate(current);
        Closed += (_, _) => _testCts?.Cancel();
    }

    /// <summary>Shows a transient hotkey registration status from the host.</summary>
    public void SetHotkeyStatus(string? error)
    {
        HotkeyStatus.Text = error ?? "Used when UI Automation cannot read a selection: the app sends one Ctrl+C and translates the copied text. Automatic translation never uses the clipboard.";
        HotkeyStatus.Foreground = error is null ? (Brush)FindResource("Ui.TextSecondary") : (Brush)FindResource("Ui.Error");
    }

    private void Populate(AppSettings s)
    {
        AutoTranslateBox.IsChecked = s.AutoTranslate;
        foreach (var lang in TargetLanguage.All)
            TargetLanguageBox.Items.Add(new ComboBoxItem { Content = $"{lang.DisplayName}  ·  {lang.PromptName}", Tag = lang.Code });
        TargetLanguageBox.SelectedIndex = Math.Max(0, TargetLanguage.All.ToList().FindIndex(l => l.Code == s.TargetLanguage));
        TextScaleSlider.Value = s.TextScale;
        TextScaleLabel.Text = $"{s.TextScale * 100:0}%";
        ThemeBox.SelectedIndex = (int)s.Theme;
        EditablePolicyBox.SelectedIndex = (int)s.EditableFieldPolicy;
        ShowOriginalBox.IsChecked = s.ShowOriginalByDefault;

        EndpointBox.Text = s.BaseUrl;
        ApiKeyBox.Password = s.ApiKey;
        foreach (var m in AppSettings.KnownModels) ModelBox.Items.Add(m);
        ModelBox.Text = s.Model;
        RelayBox.IsChecked = s.UseRelay;
        RelayBox2.Text = s.RelayPrefix;
        RelayBox2.IsEnabled = s.UseRelay;

        ExcludedList.ItemsSource = _excluded;

        HotkeyEnabledBox.IsChecked = s.HotkeyEnabled;
        HotkeyBox.Text = s.Hotkey;

        DelaySlider.Value = s.StabilizationDelayMs;
        DelayLabel.Text = $"{s.StabilizationDelayMs} ms";
        UiaEventsBox.IsChecked = s.UseUiaSelectionEvents;
        FileLoggingBox.IsChecked = s.DiagnosticsFileLogging;
        SettingsPathRun.Text = _settingsPath;
    }

    /// <summary>Builds a settings object from what is currently on screen (not what was saved).</summary>
    private AppSettings? ReadForm(out string? error)
    {
        error = null;
        var s = _original.Clone();
        s.AutoTranslate = AutoTranslateBox.IsChecked == true;
        s.TargetLanguage = (TargetLanguageBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "zh-Hans";
        s.TextScale = TextScaleSlider.Value;
        s.Theme = (ThemePreference)Math.Max(0, ThemeBox.SelectedIndex);
        s.EditableFieldPolicy = (EditableFieldPolicy)Math.Max(0, EditablePolicyBox.SelectedIndex);
        s.ShowOriginalByDefault = ShowOriginalBox.IsChecked == true;

        s.BaseUrl = EndpointBox.Text.Trim();
        s.ApiKey = _keyRevealed ? ApiKeyPlainBox.Text : ApiKeyBox.Password;
        s.Model = ModelBox.Text.Trim();
        s.UseRelay = RelayBox.IsChecked == true;
        s.RelayPrefix = RelayBox2.Text.Trim();
        s.ExcludedProcesses = _excluded.ToList();

        s.HotkeyEnabled = HotkeyEnabledBox.IsChecked == true;
        s.Hotkey = HotkeyBox.Text.Trim();
        s.StabilizationDelayMs = (int)DelaySlider.Value;
        s.UseUiaSelectionEvents = UiaEventsBox.IsChecked == true;
        s.DiagnosticsFileLogging = FileLoggingBox.IsChecked == true;

        if (!Core.Translation.EndpointResolver.TryResolve(s.BaseUrl, s.UseRelay, s.RelayPrefix, out _, out var epError))
        {
            error = epError;
            return null;
        }
        if (string.IsNullOrWhiteSpace(s.Model)) { error = "Choose a model."; return null; }
        if (s.HotkeyEnabled && !HotkeyParser.TryParse(s.Hotkey, out _)) { error = "Shortcut must be like Alt+Q, Ctrl+Shift+T or Win+F8."; return null; }
        s.Normalize();
        return s;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var s = ReadForm(out var error);
        if (s is null) { ShowStatus(error!, isError: true); return; }
        _save(s);
        Close();
    }

    private async void OnSaveAndTest(object sender, RoutedEventArgs e)
    {
        var s = ReadForm(out var error);
        if (s is null) { ShowStatus(error!, isError: true); return; }
        _save(s);

        _testCts?.Cancel();
        _testCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        SaveTestButton.IsEnabled = false;
        ShowStatus("Saved. Testing the connection…", isError: false);
        try
        {
            var (ok, message) = await _tester(s, _testCts.Token);
            ShowStatus(message, isError: !ok);
        }
        catch (OperationCanceledException) { ShowStatus("Test cancelled.", isError: true); }
        catch (Exception ex) { ShowStatus($"Test failed: {ex.GetType().Name}", isError: true); }
        finally { SaveTestButton.IsEnabled = true; }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void ShowStatus(string text, bool isError)
    {
        StatusText.Text = text;
        StatusText.Foreground = isError ? (Brush)FindResource("Ui.Error") : (Brush)FindResource("Ui.Success");
    }

    private void OnTextScaleChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TextScaleLabel is not null) TextScaleLabel.Text = $"{e.NewValue * 100:0}%";
    }

    private void OnResetTextScale(object sender, RoutedEventArgs e) => TextScaleSlider.Value = 1.0;

    private void OnDelayChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (DelayLabel is not null) DelayLabel.Text = $"{(int)e.NewValue} ms";
    }

    private void OnRelayToggled(object sender, RoutedEventArgs e)
    {
        if (RelayBox2 is not null) RelayBox2.IsEnabled = RelayBox.IsChecked == true;
    }

    private void OnRevealKey(object sender, RoutedEventArgs e)
    {
        _keyRevealed = !_keyRevealed;
        if (_keyRevealed)
        {
            ApiKeyPlainBox.Text = ApiKeyBox.Password;
            ApiKeyPlainBox.Visibility = Visibility.Visible;
            ApiKeyBox.Visibility = Visibility.Collapsed;
            RevealKeyButton.Content = "Hide";
        }
        else
        {
            ApiKeyBox.Password = ApiKeyPlainBox.Text;
            ApiKeyBox.Visibility = Visibility.Visible;
            ApiKeyPlainBox.Visibility = Visibility.Collapsed;
            RevealKeyButton.Content = "Show";
        }
    }

    private void OnAddExcluded(object sender, RoutedEventArgs e)
    {
        var name = NewExcludedBox.Text.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        if (name.Length == 0) return;
        if (!_excluded.Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase))) _excluded.Add(name);
        NewExcludedBox.Clear();
    }

    private void OnRemoveExcluded(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string name) _excluded.Remove(name);
    }
}
