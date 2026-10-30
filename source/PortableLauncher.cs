using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
[assembly: AssemblyTitle("NowPlaying")]
[assembly: AssemblyProduct("NowPlaying")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: AssemblyCopyright("Copyright (C) 2026 wuminggggg")]
class PortableLauncher {
 [STAThread] static int Main(string[] args) {
  bool test=args.Length>0 && args[0]=="--self-test";
  string testLog=test && args.Length>1 ? args[1] : null;
  try {
   var assembly=Assembly.GetExecutingAssembly();
   string root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"NowPlaying");
   string runtime=Path.Combine(root,"runtime",assembly.ManifestModule.ModuleVersionId.ToString("N"));
   Directory.CreateDirectory(runtime);
   using(var extraction=new Mutex(false,@"Local\CodexNowPlayingExtract")){
    bool acquired=false;
    try{
     try{acquired=extraction.WaitOne(15000);}catch(AbandonedMutexException){acquired=true;}
     if(!acquired) throw new IOException("等待启动超时，请重试。");
     foreach(string file in new[]{"NowPlaying.ps1","Docking.ps1","GlobalShortcuts.cs","app.ico","LICENSE","NOTICE"}){
      string destination=Path.Combine(runtime,file);
      if(!File.Exists(destination)){
       using(var resource=assembly.GetManifestResourceStream(file))
       using(var output=File.Create(destination)){resource.CopyTo(output);}
      }
     }
     string settings=Path.Combine(root,"settings.json");
     if(!test && !File.Exists(settings)){
      string previous=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"NowPlaying","settings.json");
      if(File.Exists(previous))File.Copy(previous,settings,false);
     }
    }finally{if(acquired)extraction.ReleaseMutex();}
   }
   string shell=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),@"WindowsPowerShell\v1.0\powershell.exe");
   string arguments="-NoProfile -STA -ExecutionPolicy Bypass -File \""+Path.Combine(runtime,"NowPlaying.ps1")+"\" -SettingsPath \""+Path.Combine(root,"settings.json")+"\"";
   if(test)arguments+=" -SelfTest";
   var info=new ProcessStartInfo(shell,arguments);
   info.UseShellExecute=false;info.CreateNoWindow=true;info.WindowStyle=ProcessWindowStyle.Hidden;
   info.RedirectStandardError=true;info.RedirectStandardOutput=true;
   info.EnvironmentVariables["NOWPLAYING_LAUNCHER"]=assembly.Location;
   using(var process=Process.Start(info)){
    var errors=process.StandardError.ReadToEndAsync();
    var output=process.StandardOutput.ReadToEndAsync();
    process.WaitForExit();
    string result=output.Result+errors.Result;
    if(test && testLog!=null)File.WriteAllText(testLog,result);
    if(process.ExitCode!=0 && !test)MessageBox.Show(result,"Now Playing");
    return process.ExitCode;
   }
  }catch(Exception e){
   if(test && testLog!=null)File.WriteAllText(testLog,"FAIL: "+e);
   else MessageBox.Show(e.Message,"Now Playing");
   return 1;
  }
 }
}