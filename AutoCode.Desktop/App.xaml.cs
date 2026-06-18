using System.IO;
using System.Windows;
using System.Windows.Threading;
using AutoCode.Engine.Auth;

namespace AutoCode.Desktop;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            WriteCrash("AppDomain", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteCrash("TaskScheduler", e.Exception);
            e.SetObserved();
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // Initialize i18n before the main window is built so {DynamicResource L_*} keys
        // resolve on first paint. Language comes from saved config, else OS culture, else English.
        try
        {
            LocalizationService.Initialize(new ConfigStore().Load().Language);
        }
        catch
        {
            LocalizationService.Initialize(null);
        }

        base.OnStartup(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrash("Dispatcher", e.Exception);
        e.Handled = false;
    }

    internal static void WriteCrash(string source, Exception? exception)
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "autocode-gui");
        Directory.CreateDirectory(directory);

        var logPath = Path.Combine(directory, "crash.log");
        File.AppendAllText(
            logPath,
            $"[{DateTimeOffset.Now:O}] {source}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
    }
}
