using System.Drawing;
using System.Drawing.Imaging;
using System.Windows;
using System.Windows.Threading;

namespace FaceUnlock.Agent;

public sealed class InteractiveBridge : IDisposable {
 private readonly DispatcherTimer _timer; private DateTime _lastClipboardIn=DateTime.MinValue;
 private static string Temp(string n)=>Path.Combine(Path.GetTempPath(),n);
 public InteractiveBridge(){_timer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(1)};_timer.Tick+=(_,_)=>Tick();_timer.Start();}
 private void Tick(){try{var input=Temp("faceunlock-clipboard-in.txt");if(File.Exists(input)){var t=File.GetLastWriteTimeUtc(input);if(t>_lastClipboardIn){Clipboard.SetText(File.ReadAllText(input));_lastClipboardIn=t;}}if(Clipboard.ContainsText())File.WriteAllText(Temp("faceunlock-clipboard-out.txt"),Clipboard.GetText());if(Clipboard.ContainsFileDropList()){var files=Clipboard.GetFileDropList();if(files.Count>0)File.WriteAllText(Temp("faceunlock-file-out.path"),files[0]!);}var req=Temp("faceunlock-screenshot.request");if(File.Exists(req)){Capture(Temp("faceunlock-screenshot.jpg"));File.Delete(req);}}catch{}}
 private static void Capture(string path){var bounds=System.Windows.Forms.SystemInformation.VirtualScreen;using var bmp=new Bitmap(bounds.Width,bounds.Height);using(var g=Graphics.FromImage(bmp))g.CopyFromScreen(bounds.Left,bounds.Top,0,0,bounds.Size);bmp.Save(path,ImageFormat.Jpeg);}
 public void Dispose()=>_timer.Stop();
}
