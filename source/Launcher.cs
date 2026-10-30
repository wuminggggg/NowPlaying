using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
class Launcher {
 [STAThread] static void Main(){
  try {
   string script=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"NowPlaying.ps1");
   if(!File.Exists(script)) throw new FileNotFoundException("缺少 NowPlaying.ps1，请完整解压工具文件夹。");
   string shell=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),@"WindowsPowerShell\v1.0\powershell.exe");
   var info=new ProcessStartInfo(shell,"-NoProfile -STA -ExecutionPolicy Bypass -File \""+script+"\"");
   info.UseShellExecute=false; info.CreateNoWindow=true; info.WindowStyle=ProcessWindowStyle.Hidden;
   info.RedirectStandardError=true;
   using(var process=Process.Start(info)){
    var error=process.StandardError.ReadToEndAsync();
    process.WaitForExit();
    if(process.ExitCode!=0) MessageBox.Show(error.Result,"Now Playing");
   }
  } catch(Exception e){MessageBox.Show(e.Message,"Now Playing");}
 }
}