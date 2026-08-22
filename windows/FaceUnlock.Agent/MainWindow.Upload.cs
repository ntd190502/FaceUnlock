using System.Diagnostics;
using System.Windows;

namespace FaceUnlock.Agent;

public partial class MainWindow
{
    void OpenUpload_Click(object sender, RoutedEventArgs e)
    {
        cfg = store.Load();
        if (string.IsNullOrWhiteSpace(cfg.PcToken)) { Status.Text = "Pair this PC before opening File Upload."; return; }
        var baseUrl = cfg.ServerUrl.TrimEnd('/');
        var url = $"{baseUrl}/transfer.php?mode=pc&pc={Uri.EscapeDataString(cfg.PcId)}&token={Uri.EscapeDataString(cfg.PcToken)}";
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        Status.Text = "File Upload opened in your default browser.";
    }
}
