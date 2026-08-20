using System.Collections.Specialized;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows;
using System.Windows.Threading;
namespace FaceUnlock.Agent;
public sealed class InteractiveBridge:IDisposable{
 readonly DispatcherTimer _timer;DateTime _lastClipboardIn=DateTime.MinValue,_lastIncoming=DateTime.MinValue;static readonly string Dir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"FaceUnlock","Bridge");static string F(string n){Directory.CreateDirectory(Dir);return Path.Combine(Dir,n);}
 public InteractiveBridge(){_timer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(1)};_timer.Tick+=(_,_)=>Tick();_timer.Start();}
 void Tick(){try{var input=F("clipboard-in.txt");if(File.Exists(input)){var t=File.GetLastWriteTimeUtc(input);if(t>_lastClipboardIn){Clipboard.SetText(File.ReadAllText(input));_lastClipboardIn=t;}}var incoming=F("incoming-file.path");if(File.Exists(incoming)){var t=File.GetLastWriteTimeUtc(incoming);if(t>_lastIncoming){var p=File.ReadAllText(incoming).Trim();if(File.Exists(p)){var c=new StringCollection();c.Add(p);Clipboard.SetFileDropList(c);}_lastIncoming=t;}}if(Clipboard.ContainsText())File.WriteAllText(F("clipboard-out.txt"),Clipboard.GetText());if(Clipboard.ContainsFileDropList()){var files=Clipboard.GetFileDropList();if(files.Count>0)File.WriteAllText(F("clipboard-file.path"),files[0]!);}var req=F("screenshot.request");if(File.Exists(req)){Capture(F("screenshot.jpg"));File.Delete(req);}}catch{}}
 static void Capture(string path){var b=System.Windows.Forms.SystemInformation.VirtualScreen;using var bmp=new Bitmap(b.Width,b.Height);using(var g=Graphics.FromImage(bmp))g.CopyFromScreen(b.Left,b.Top,0,0,b.Size);bmp.Save(path,ImageFormat.Jpeg);}
 public void Dispose()=>_timer.Stop();
}
