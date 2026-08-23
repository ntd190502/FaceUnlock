using System.Windows;

namespace FaceUnlock.AlertUI;

public partial class AlertWindow : Window
{
    public string AlertKey { get; private set; }

    public AlertWindow(string key, string title, string message)
    {
        InitializeComponent();
        AlertKey = key;
        AlertTitle.Text = title;
        AlertMessage.Text = message;
        Activated += (_, _) => Topmost = true;
    }

    public void Update(string key, string title, string message)
    {
        AlertKey = key;
        AlertTitle.Text = title;
        AlertMessage.Text = message;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();
        Topmost = true;
    }

    void Ok_Click(object sender, RoutedEventArgs e) => Close();
}
