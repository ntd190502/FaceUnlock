using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace FaceUnlock.AlertUI;

public partial class App : Application
{
    readonly string _alertPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FaceUnlock", "Bridge", "alert.json");
    Mutex? _mutex;
    DispatcherTimer? _timer;
    AlertWindow? _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _mutex = new Mutex(true, @"Local\FaceUnlock.AlertUI", out var ownsMutex);
        if (!ownsMutex)
        {
            Shutdown();
            return;
        }

        if (!TryConsumeAlert(out var alert))
        {
            Shutdown();
            return;
        }

        _window = new AlertWindow(alert.key, alert.title, alert.message);
        _window.Closed += (_, _) => Shutdown();
        _window.Show();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _timer.Tick += (_, _) =>
        {
            if (_window == null || !_window.IsVisible) return;
            if (TryConsumeAlert(out var next)) _window.Update(next.key, next.title, next.message);
        };
        _timer.Start();
    }

    bool TryConsumeAlert(out AlertPayload alert)
    {
        alert = new AlertPayload("alert", "FaceUnlock warning", "");
        if (!File.Exists(_alertPath)) return false;
        try
        {
            var json = File.ReadAllText(_alertPath);
            File.Delete(_alertPath);
            var parsed = JsonSerializer.Deserialize<AlertPayload>(json);
            if (parsed == null) return false;
            alert = parsed;
            return true;
        }
        catch { return false; }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _timer?.Stop();
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
        base.OnExit(e);
    }

    sealed record AlertPayload(string key, string title, string message);
}
