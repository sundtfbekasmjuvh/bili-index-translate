using System.Windows;
using System.Windows.Interop;

namespace BiliIndexTranslate.App.Onboarding;

public partial class WelcomeWindow : Window
{
    public WelcomeWindow()
    {
        InitializeComponent();
    }

    public event Action? SettingsRequested;
    public event Action<bool>? Finished;

    public IntPtr Hwnd => new WindowInteropHelper(this).Handle;

    public void SetWarning(string? text)
    {
        HookWarning.Text = text ?? string.Empty;
        HookWarning.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnSettings(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();

    private void OnClose(object sender, RoutedEventArgs e)
    {
        Finished?.Invoke(DontShowBox.IsChecked == true);
        Close();
    }
}
