using System.Windows;
using BiliIndexTranslate.Core.Settings;
using Microsoft.Win32;

namespace BiliIndexTranslate.App.Themes;

/// <summary>Swaps the palette dictionary; every brush in the UI is a DynamicResource so windows update live.</summary>
internal sealed class ThemeManager : IDisposable
{
    private static readonly Uri LightUri = new("pack://application:,,,/BiliIndexTranslate;component/Themes/Light.xaml");
    private static readonly Uri DarkUri = new("pack://application:,,,/BiliIndexTranslate;component/Themes/Dark.xaml");

    private readonly Application _app;
    private ResourceDictionary? _palette;
    private ThemePreference _preference = ThemePreference.System;
    private bool _listening;

    public ThemeManager(Application app)
    {
        _app = app;
    }

    public bool IsDark { get; private set; }

    public void Apply(ThemePreference preference)
    {
        _preference = preference;
        var dark = preference switch
        {
            ThemePreference.Dark => true,
            ThemePreference.Light => false,
            _ => SystemPrefersDark(),
        };
        if (_palette is not null && dark == IsDark) return;

        var next = new ResourceDictionary { Source = dark ? DarkUri : LightUri };
        if (_palette is not null) _app.Resources.MergedDictionaries.Remove(_palette);
        _app.Resources.MergedDictionaries.Add(next);
        _palette = next;
        IsDark = dark;

        if (!_listening)
        {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            _listening = true;
        }
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (_preference != ThemePreference.System) return;
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
            _app.Dispatcher.BeginInvoke(() => Apply(ThemePreference.System));
    }

    public static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch (Exception) { return false; }
    }

    public void Dispose()
    {
        if (_listening) SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _listening = false;
    }
}
