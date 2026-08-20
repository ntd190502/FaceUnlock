namespace FaceUnlock.Agent;
public partial class App : System.Windows.Application {
 private InteractiveBridge? _bridge;
 protected override void OnStartup(System.Windows.StartupEventArgs e){base.OnStartup(e);_bridge=new InteractiveBridge();}
 protected override void OnExit(System.Windows.ExitEventArgs e){_bridge?.Dispose();base.OnExit(e);}
}
