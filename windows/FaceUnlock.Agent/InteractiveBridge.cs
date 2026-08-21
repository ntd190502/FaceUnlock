using System;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Threading;

namespace FaceUnlock.Agent;

public sealed class InteractiveBridge : IDisposable
{
    readonly DispatcherTimer _timer;
    DateTime _lastClipboardIn = DateTime.MinValue;
    DateTime _lastIncoming = DateTime.MinValue;
    string? _lastAppsRequest;
    string? _lastCloseRequest;

    static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "FaceUnlock", "Bridge");

    static string F(string name)
    {
        Directory.CreateDirectory(Dir);
        return Path.Combine(Dir, name);
    }

    public InteractiveBridge()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    void Tick()
    {
        try
        {
            HandleClipboard();
            HandleIncomingFile();
            PublishClipboard();
            HandleScreenshot();
            HandleAppsRequest();
            HandleCloseAppRequest();
        }
        catch
        {
            // The interactive helper must never crash the desktop agent.
        }
    }

    void HandleClipboard()
    {
        var input = F("clipboard-in.txt");
        if (!File.Exists(input)) return;
        var changed = File.GetLastWriteTimeUtc(input);
        if (changed <= _lastClipboardIn) return;
        System.Windows.Clipboard.SetText(File.ReadAllText(input));
        _lastClipboardIn = changed;
    }

    void HandleIncomingFile()
    {
        var incoming = F("incoming-file.path");
        if (!File.Exists(incoming)) return;
        var changed = File.GetLastWriteTimeUtc(incoming);
        if (changed <= _lastIncoming) return;
        var path = File.ReadAllText(incoming).Trim();
        if (File.Exists(path))
        {
            var files = new StringCollection();
            files.Add(path);
            System.Windows.Clipboard.SetFileDropList(files);
        }
        _lastIncoming = changed;
    }

    static void PublishClipboard()
    {
        var textMarker = F("clipboard-out.txt");
        if (System.Windows.Clipboard.ContainsText())
            File.WriteAllText(textMarker, System.Windows.Clipboard.GetText());
        else
            TryDelete(textMarker);

        var fileMarker = F("clipboard-file.path");
        if (System.Windows.Clipboard.ContainsFileDropList())
        {
            var files = System.Windows.Clipboard.GetFileDropList();
            if (files.Count > 0 && File.Exists(files[0]!))
            {
                File.WriteAllText(fileMarker, files[0]!);
                return;
            }
        }
        TryDelete(fileMarker);
    }

    static void HandleScreenshot()
    {
        var request = F("screenshot.request");
        if (!File.Exists(request)) return;
        Capture(F("screenshot.jpg"));
        File.Delete(request);
    }

    void HandleAppsRequest()
    {
        var requestFile = F("apps.request");
        if (!File.Exists(requestFile)) return;
        var requestId = File.ReadAllText(requestFile).Trim();
        if (string.IsNullOrWhiteSpace(requestId) || requestId == _lastAppsRequest) return;
        _lastAppsRequest = requestId;

        var apps = Process.GetProcesses()
            .Select(p =>
            {
                try
                {
                    var title = p.MainWindowTitle;
                    return string.IsNullOrWhiteSpace(title)
                        ? null
                        : new { id = p.Id, name = p.ProcessName, title };
                }
                catch { return null; }
            })
            .Where(x => x != null)
            .OrderBy(x => x!.name, StringComparer.OrdinalIgnoreCase)
            .Take(200)
            .ToArray();

        WriteJsonAtomic(F("apps.result.json"), new { request_id = requestId, apps });
    }

    void HandleCloseAppRequest()
    {
        var requestFile = F("close-app.request.json");
        if (!File.Exists(requestFile)) return;

        using var doc = JsonDocument.Parse(File.ReadAllText(requestFile));
        var root = doc.RootElement;
        var requestId = root.TryGetProperty("request_id", out var rid) ? rid.GetString() : null;
        if (string.IsNullOrWhiteSpace(requestId) || requestId == _lastCloseRequest) return;
        _lastCloseRequest = requestId;

        if (!root.TryGetProperty("pid", out var pidElement) || !pidElement.TryGetInt32(out var pid))
        {
            WriteJsonAtomic(F("close-app.result.json"), new { request_id = requestId, ok = false, error = "pid required" });
            return;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            var graceful = process.CloseMainWindow();
            if (!graceful)
                process.Kill(true);
            WriteJsonAtomic(F("close-app.result.json"), new { request_id = requestId, ok = true, pid });
        }
        catch (Exception ex)
        {
            WriteJsonAtomic(F("close-app.result.json"), new { request_id = requestId, ok = false, error = ex.Message, pid });
        }
    }

    static void WriteJsonAtomic(string path, object value)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value));
        File.Move(temp, path, true);
    }

    static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }

    static void Capture(string path)
    {
        var bounds = System.Windows.Forms.SystemInformation.VirtualScreen;
        using var bitmap = new Bitmap(bounds.Width, bounds.Height);
        using (var graphics = Graphics.FromImage(bitmap))
            graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size);
        bitmap.Save(path, ImageFormat.Jpeg);
    }

    public void Dispose() => _timer.Stop();
}
