using System.IO;
using System.Text.Json;
using System.Windows.Threading;
namespace FaceUnlock.Agent;
public partial class App : System.Windows.Application {
 private InteractiveBridge? _bridge; private DispatcherTimer? _alertTimer; private AlertWindow? _alert;
 static readonly string AlertPath=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"FaceUnlock","Bridge","alert.json");
 protected override void OnStartup(System.Windows.StartupEventArgs e){base.OnStartup(e);_bridge=new InteractiveBridge();_alertTimer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(500)};_alertTimer.Tick+=(_,_)=>CheckAlert();_alertTimer.Start();}
 void CheckAlert(){if(!File.Exists(AlertPath))return;try{var text=File.ReadAllText(AlertPath);File.Delete(AlertPath);using var d=JsonDocument.Parse(text);var root=d.RootElement;var key=root.GetProperty("key").GetString()??"alert";var title=root.GetProperty("title").GetString()??"FaceUnlock warning";var message=root.GetProperty("message").GetString()??"";if(_alert!=null&&_alert.IsVisible&&_alert.AlertKey==key)_alert.Update(title,message);else{_alert?.Close();_alert=new AlertWindow(key,title,message);_alert.Closed+=(_,_)=>_alert=null;_alert.Show();}}catch{}}
 protected override void OnExit(System.Windows.ExitEventArgs e){_alertTimer?.Stop();_bridge?.Dispose();base.OnExit(e);}
}
