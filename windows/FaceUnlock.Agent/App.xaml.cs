using System.Windows;
namespace FaceUnlock.Agent;
public partial class App : Application {
 private InteractiveBridge? _bridge;
 protected override void OnStartup(StartupEventArgs e){base.OnStartup(e);_bridge=new InteractiveBridge();}
 protected override void OnExit(ExitEventArgs e){_bridge?.Dispose();base.OnExit(e);}
}
