using System.IO;
using System.Text;
using System.Windows;

namespace FaceUnlock.Shell;

public partial class App : Application
{
    private static Mutex? _instanceMutex;
    private ShellEngine? _engine;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Global unhandled exception logging
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            LogCrash(args.ExceptionObject as Exception);
        };
        DispatcherUnhandledException += (s, args) =>
        {
            LogCrash(args.Exception);
            args.Handled = true;
        };

        // Determine Shell Mode: --test vs --shell (default is --shell)
        ShellMode mode = ShellMode.Shell;
        string? previewState = null;
        foreach (var arg in e.Args)
        {
            if (arg.Equals("--test", StringComparison.OrdinalIgnoreCase))
            {
                mode = ShellMode.Test;
            }
            else if (arg.Equals("--shell", StringComparison.OrdinalIgnoreCase))
            {
                mode = ShellMode.Shell;
            }
        }
        for (var i = 0; i + 1 < e.Args.Length; i++) if (e.Args[i].Equals("--test-state", StringComparison.OrdinalIgnoreCase)) previewState = e.Args[i + 1];

        // Single instance protection
        bool createdNew;
        _instanceMutex = new Mutex(true, $"Local\\FaceUnlockShell_{Environment.UserName}", out createdNew);
        if (!createdNew && mode == ShellMode.Shell)
        {
            // Already running in shell mode
            Shutdown();
            return;
        }

        _engine = new ShellEngine(mode);
        Exit += (_, _) => _engine?.Shutdown();
        var window = new MainWindow(_engine, previewState);
        window.Show();
    }

    private static void LogCrash(Exception? ex)
    {
        try
        {
            var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FaceUnlock", "logs");
            Directory.CreateDirectory(logDir);
            var logFile = Path.Combine(logDir, "shell.log");
            var content = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fffZ}] [CRASH] {ex?.ToString()}{Environment.NewLine}";
            File.AppendAllText(logFile, content, Encoding.UTF8);
        }
        catch { }
    }
}
