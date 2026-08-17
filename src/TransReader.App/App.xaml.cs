using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;
using TransReader.App.Services;

namespace TransReader.App;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        // The merged SxS manifest cannot carry a dpiAwareness element (mt.exe
        // places it after the WinRT class entries, which the loader rejects),
        // so set Per-Monitor V2 awareness before any window is created instead.
        SetProcessDpiAwarenessContext(new IntPtr(-4));

        // Catch every silent exit path: background threads and unobserved tasks
        // bypass Application.UnhandledException entirely.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
            {
                WriteCrashLog(exception, "AppDomain.UnhandledException");
            }
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteCrashLog(args.Exception, "TaskScheduler.UnobservedTaskException");
            args.SetObserved();
        };

        InitializeComponent();
        UnhandledException += (_, args) =>
        {
            System.Diagnostics.Debug.WriteLine(args.Exception);
            WriteCrashLog(args.Exception, "Application.UnhandledException");
            if (_window is MainWindow mainWindow)
            {
                args.Handled = true;
                mainWindow.ShowUnhandledError(args.Exception);
            }
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _window = new MainWindow();
            _window.Activate();
        }
        catch (Exception ex)
        {
            WriteCrashLog(ex, "App.OnLaunched");
            throw;
        }

        // Unpackaged apps receive "open with" paths as plain process arguments
        // (LaunchActivatedEventArgs.Arguments is empty for unpackaged apps).
        var path = Environment.GetCommandLineArgs() is { Length: > 1 } commandLine
            ? commandLine[1].Trim().Trim('"')
            : null;
        if (!string.IsNullOrEmpty(path) &&
            path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) &&
            System.IO.File.Exists(path) &&
            _window is MainWindow mainWindow)
        {
            _ = mainWindow.OpenPdfFromCommandLineAsync(path);
        }
    }

    private static void WriteCrashLog(Exception exception, string context)
    {
        try
        {
            // 走与 app.log 一致的轮转机制（独立 crashes.log，2MB 截断），
            // 避免旧的单文件 %TEMP%\transreader-crash.txt 被覆盖而丢失上次崩溃现场。
            AppLog.Crash(exception, context);
        }
        catch
        {
            // 崩溃记录本身绝不能再抛。
        }
    }

    // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 == (HANDLE)-4
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);
}
