using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace BiliIndexTranslate.App;

/// <summary>
/// Entry point. No App.xaml: the Application object is created by hand so that the single-instance
/// check runs before any WPF window or tray icon exists.
/// </summary>
public static class Program
{
    public const string MutexName = @"Local\BiliIndexTranslate.SingleInstance";
    public const string ActivateEventName = @"Local\BiliIndexTranslate.Activate";

    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BiliIndexTranslate");

    [STAThread]
    public static int Main(string[] args)
    {
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            // Second instance: wake the running one (it opens Settings) and leave quietly.
            try
            {
                using var evt = EventWaitHandle.OpenExisting(ActivateEventName);
                evt.Set();
            }
            catch (WaitHandleCannotBeOpenedException) { }
            return 0;
        }

        Directory.CreateDirectory(DataDirectory);
        System.Windows.Forms.Application.EnableVisualStyles(); // tray menu only

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/BiliIndexTranslate;component/Themes/Base.xaml"),
        });

        AppHost? host = null;
        var crashCount = 0;
        app.DispatcherUnhandledException += (_, e) =>
        {
            WriteCrash(e.Exception);
            host?.ReportCrash(e.Exception);
            // Keep running after an isolated UI exception; give up on a storm.
            e.Handled = ++crashCount < 5;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteCrash(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { WriteCrash(e.Exception); e.SetObserved(); };

        host = new AppHost(app, DataDirectory, args.Contains("--welcome", StringComparer.OrdinalIgnoreCase));
        app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, host.Start);
        var code = app.Run();
        host.Dispose();
        mutex.ReleaseMutex();
        return code;
    }

    private static void WriteCrash(Exception? ex)
    {
        if (ex is null) return;
        try
        {
            File.AppendAllText(Path.Combine(DataDirectory, "crash.log"),
                $"{DateTimeOffset.Now:O} {ex.GetType().FullName}: {ex.Message}{Environment.NewLine}{ex.StackTrace}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception) { }
    }
}
