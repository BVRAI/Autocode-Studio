using System.IO;
using System.Windows;
using System.Windows.Threading;

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
