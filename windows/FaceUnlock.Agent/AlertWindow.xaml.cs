using System.Windows;
namespace FaceUnlock.Agent;
public partial class AlertWindow:Window{
 public string AlertKey{get;}
 public AlertWindow(string key,string title,string message){InitializeComponent();AlertKey=key;AlertTitle.Text=title;AlertMessage.Text=message;}
 public void Update(string title,string message){AlertTitle.Text=title;AlertMessage.Text=message;Activate();Topmost=true;}
 void Ok_Click(object sender,RoutedEventArgs e)=>Close();
}
