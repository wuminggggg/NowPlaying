using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using Forms=System.Windows.Forms;

[assembly:AssemblyTitle("NowPlaying")]
[assembly:AssemblyProduct("NowPlaying")]
[assembly:AssemblyVersion("1.0.1.0")]
[assembly:AssemblyFileVersion("1.0.1.0")]
[assembly:AssemblyCopyright("Copyright (C) 2026 wuminggggg")]

class NativePlayer {
 readonly bool testing;
 readonly string root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"NowPlaying");
 readonly Dictionary<string,string> bindings=new Dictionary<string,string>{
  {"Toggle","Ctrl+Alt+Space"},{"Previous","Ctrl+Alt+Left"},{"Next","Ctrl+Alt+Right"},{"Stop","Ctrl+Alt+Down"},{"Visibility","Ctrl+Alt+Up"}};
 readonly Dictionary<string,PlaybackState> history=new Dictionary<string,PlaybackState>();
 readonly Dictionary<string,TextBox> bindingLabels=new Dictionary<string,TextBox>();
 Window main,edge,settings;
 Application app;
 Forms.NotifyIcon tray;
 Forms.ToolStripMenuItem disabledMenu,startupMenu;
 GlobalShortcuts hooks;
 DispatcherTimer timer,collapse;
 GlobalSystemMediaTransportControlsSessionManager manager;
 GlobalSystemMediaTransportControlsSession session;
 string sessionId="",latest="",chosen="",dockSide="",recordAction="",seekId="";
 long sequence;
 bool disabled,hover,moving,seeking,transition,polling,closing;
 double opacity=.88,hoverOpacity=.95,dockTop,dockLeft,left=double.NaN,top=double.NaN;
 Rect dockArea;
 DateTime lastMetadata=DateTime.MinValue,recordUntil;
 TextBox recordLabel;
 Slider normalSlider,hoverSlider;
 Forms.ContextMenuStrip menu;
 StartupManager startup;
 Mutex mutex;
 EventWaitHandle wake;
 class PlaybackState {public string Status="";public long Order;}
 class Candidate {public string Id,Status;public DateTimeOffset Updated;public GlobalSystemMediaTransportControlsSession Session;}
 T UI<T>(string name) where T:class {return main.FindName(name) as T;}
 T Edge<T>(string name) where T:class {return edge.FindName(name) as T;}
 static Stream Resource(string name){return Assembly.GetExecutingAssembly().GetManifestResourceStream(name);}
 static string Executable {get{return StartupPaths.ResolveFile(Assembly.GetExecutingAssembly().Location);}}
 string Config {get{return Path.Combine(root,"settings.json");}}
 void Log(Exception error){try{Directory.CreateDirectory(root);File.AppendAllText(Path.Combine(root,"errors.log"),DateTime.Now.ToString("s")+" "+error+Environment.NewLine);}catch{}}
 [STAThread] static int Main(string[] args){
  if(args.Length==2&&args[0]=="--startup-probe"){
   try{
    var request=GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask();
    if(!request.Wait(10000))throw new Exception("媒体接口等待超时");
    File.WriteAllText(args[1],"PASS native task; media sessions="+request.Result.GetSessions().Count);
    return 0;
   }catch(Exception error){File.WriteAllText(args[1],"FAIL: "+error);return 1;}
  }
  bool test=args.Length>0&&args[0]=="--self-test";
  var player=new NativePlayer(test);
  try{return player.Run(test&&args.Length>1?args[1]:null);}
  catch(Exception e){player.Log(e);if(test&&args.Length>1)File.WriteAllText(args[1],"FAIL: "+e);return 1;}
 }
 NativePlayer(bool test){
 testing=test;
 var binaryFolder=new DirectoryInfo(Path.GetDirectoryName(Executable));
 if(binaryFolder.Parent!=null&&binaryFolder.Parent.Name=="app"&&binaryFolder.Parent.Parent!=null&&binaryFolder.Parent.Parent.Name=="NowPlaying")root=binaryFolder.Parent.Parent.FullName;
}
 int Run(string testLog){
  if(!testing){
   mutex=new Mutex(false,@"Local\CodexNowPlaying");
   bool owned=false;try{owned=mutex.WaitOne(0);}catch(AbandonedMutexException){owned=true;}
   if(!owned){try{using(var e=EventWaitHandle.OpenExisting(@"Local\CodexNowPlayingWake"))e.Set();}catch{}return 0;}
   wake=new EventWaitHandle(false,EventResetMode.AutoReset,@"Local\CodexNowPlayingWake");
  }
  Load();
  app=new Application();app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
  using(var stream=Resource("Player.xaml"))main=(Window)XamlReader.Load(stream);
  using(var stream=Resource("Edge.xaml"))edge=(Window)XamlReader.Load(stream);
  main.SourceInitialized+=(s,e)=>FloatingWindowChrome.Apply(new WindowInteropHelper(main).Handle);
  edge.SourceInitialized+=(s,e)=>FloatingWindowChrome.Apply(new WindowInteropHelper(edge).Handle);
  hooks=new GlobalShortcuts();SyncBindings();
  startup=new StartupManager(root,Executable);
  collapse=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(300)};
  collapse.Tick+=(s,e)=>{collapse.Stop();if(!main.IsMouseOver&&!edge.IsMouseOver)Collapse();};
  main.MouseEnter+=(s,e)=>{if(transition)return;if(dockSide!="")Expand();else{hover=true;ApplyOpacity();}};
  main.MouseLeave+=(s,e)=>{if(dockSide!="")ScheduleCollapse();else{hover=false;ApplyOpacity();}};
  edge.MouseEnter+=(s,e)=>Expand();
  edge.MouseLeave+=(s,e)=>ScheduleCollapse();
  edge.MouseLeftButtonDown+=(s,e)=>DragEdge();
  UI<Button>("Settings").Click+=(s,e)=>OpenSettings();
  UI<Button>("Close").Click+=(s,e)=>Hide();
  foreach(string action in new[]{"Previous","Toggle","Next","Stop"}){
   string captured=action;UI<Button>(action).Click+=async(s,e)=>await Control(captured);
  }
  UI<Grid>("Header").MouseLeftButtonDown+=DragMain;
  UI<Grid>("Metadata").MouseLeftButtonDown+=DragMain;
  var seek=UI<Slider>("Seek");
  seek.PreviewMouseLeftButtonDown+=(s,e)=>{seeking=true;seekId=sessionId;};
  seek.PreviewMouseLeftButtonUp+=async(s,e)=>{await Seek();seeking=false;ScheduleCollapse();};
  seek.LostMouseCapture+=(s,e)=>{seeking=false;ScheduleCollapse();};
  seek.PreviewKeyDown+=(s,e)=>{seeking=true;seekId=sessionId;};
  seek.KeyUp+=async(s,e)=>{await Seek();seeking=false;ScheduleCollapse();};
  seek.ValueChanged+=(s,e)=>UI<TextBlock>("Elapsed").Text=Clock(seek.Value);
  CreateTray();
  main.ContentRendered+=(s,e)=>{};
  main.Closed+=(s,e)=>{
   if(closing)return;closing=true;Save();timer.Stop();collapse.Stop();hooks.Dispose();
   tray.Visible=false;tray.Dispose();if(wake!=null)wake.Dispose();if(mutex!=null){mutex.ReleaseMutex();mutex.Dispose();}
   app.Shutdown();
  };
  timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(100)};
  int ticks=0;
  timer.Tick+=async(s,e)=>{
   try{
    if(wake!=null&&wake.WaitOne(0))Show();
    string action;
    while(hooks.Events.TryDequeue(out action)){
     if(action.StartsWith("capture:"))Capture(action.Substring(8));
     else if(action=="cancel")CancelRecording();
     else if(!disabled)await Control(action);
    }
    if(hooks.Recording&&DateTime.UtcNow>recordUntil){hooks.Recording=false;CancelRecording();}
    if(++ticks%5==0&&!polling){polling=true;try{await Poll();}finally{polling=false;}}
   }catch(Exception error){Log(error);}
  };
  ApplyOpacity();
  main.Show();main.UpdateLayout();
  Rect area=Area(double.IsNaN(left)?0:left+172,double.IsNaN(top)?0:top+100);
  if(double.IsNaN(left)){main.Left=area.Right-main.ActualWidth-20;main.Top=area.Bottom-main.ActualHeight-20;}
  else{main.Left=Clamp(left,area.Left,area.Right-main.ActualWidth);main.Top=Clamp(top,area.Top,area.Bottom-main.ActualHeight);}
  if(dockSide!="")Dock(dockSide,Area(dockLeft+10,dockTop+52),dockTop);
  int code=0;
  if(testing){
   app.Dispatcher.BeginInvoke(new Action(async()=>{
    try{await Tests();File.WriteAllText(testLog,"PASS: native WPF, media, hooks, opacity, docking, settings, login task");}
    catch(Exception error){code=1;File.WriteAllText(testLog,"FAIL: "+error);}
    main.Close();
   }));
  }else{
   try{startup.Migrate();startupMenu.Checked=startup.Enabled;}catch(Exception e){Log(e);}
   timer.Start();
  }
  app.Run();return code;
 }
 void Load(){
  try{
   var data=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(Config));
   object value;
   if(data.TryGetValue("bindings",out value))foreach(var pair in (Dictionary<string,object>)value)bindings[pair.Key]=Convert.ToString(pair.Value);
   opacity=Number(data,"opacity",.88);hoverOpacity=Number(data,"hoverOpacity",.95);
   left=Number(data,"left",double.NaN);top=Number(data,"top",double.NaN);
   dockLeft=Number(data,"dockLeft",0);dockTop=Number(data,"dockTop",100);
   if(data.TryGetValue("shortcutsDisabled",out value))disabled=Convert.ToBoolean(value);
   if(data.TryGetValue("dockSide",out value)&&new[]{"Left","Right"}.Contains(Convert.ToString(value)))dockSide=Convert.ToString(value);
  }catch{}
 }
 static double Number(Dictionary<string,object> data,string key,double fallback){
  object value;double result;
  return data.TryGetValue(key,out value)&&double.TryParse(Convert.ToString(value,CultureInfo.InvariantCulture),NumberStyles.Float,CultureInfo.InvariantCulture,out result)?result:fallback;
 }
 void Save(){
  if(testing)return;
  try{
   Directory.CreateDirectory(root);
   var data=new Dictionary<string,object>{{"bindings",bindings},{"opacity",opacity},{"hoverOpacity",hoverOpacity},{"shortcutsDisabled",disabled},{"left",main.Left},{"top",main.Top},{"dockSide",dockSide},{"dockLeft",double.IsNaN(edge.Left)?dockLeft:edge.Left},{"dockTop",double.IsNaN(edge.Top)?dockTop:edge.Top}};
   File.WriteAllText(Config,new JavaScriptSerializer().Serialize(data),new UTF8Encoding(false));
  }catch(Exception e){Log(e);}
 }
 void ApplyOpacity(){
  double value=Clamp(hover?hoverOpacity:opacity,.1,1);
  main.Opacity=edge.Opacity=value;
  UI<Border>("Card").Background=new SolidColorBrush(Color.FromRgb(245,245,248));
  UI<StackPanel>("Tools").Opacity=hover?1:0;
 }
 void SyncBindings(){hooks.Enabled=!disabled;hooks.Bindings.Clear();foreach(var pair in bindings)if(pair.Value!="")hooks.Bindings[pair.Value]=pair.Key;}
 static string Clock(double seconds){
  var time=TimeSpan.FromSeconds(Math.Max(0,seconds));
  return time.TotalHours>=1?string.Format("{0}:{1:00}:{2:00}",(int)time.TotalHours,time.Minutes,time.Seconds):string.Format("{0}:{1:00}",(int)time.TotalMinutes,time.Seconds);
 }
 static double Clamp(double value,double minimum,double maximum){return Math.Max(minimum,Math.Min(maximum,value));}
 Rect Area(double x,double y){
  var source=PresentationSource.FromVisual(main);
  var forward=source.CompositionTarget.TransformToDevice;
  var inverse=source.CompositionTarget.TransformFromDevice;
  var p=forward.Transform(new Point(x,y));
  var screen=Forms.Screen.FromPoint(new System.Drawing.Point((int)p.X,(int)p.Y)).WorkingArea;
  var origin=inverse.Transform(new Point(screen.Left,screen.Top));
  var size=inverse.Transform(new Vector(screen.Width,screen.Height));
  return new Rect(origin.X,origin.Y,size.X,size.Y);
 }
 string Snap(Rect rect,Rect area){return rect.Left<=area.Left+24?"Left":rect.Right>=area.Right-24?"Right":"";}
 void Dock(string side,Rect area,double y){
  transition=true;collapse.Stop();
  try{
   dockSide=side;dockArea=area;hover=false;
   edge.Left=side=="Left"?area.Left:area.Right-20;edge.Top=Clamp(y,area.Top,area.Bottom-104);
   edge.Topmost=main.Topmost;edge.Show();main.Hide();ApplyOpacity();
  }finally{transition=false;}Save();
 }
 void Expand(){
  if(dockSide==""||moving||transition)return;
  transition=true;collapse.Stop();
  try{
   hover=true;ApplyOpacity();main.Show();main.UpdateLayout();
   main.Left=dockSide=="Left"?edge.Left+6:edge.Left-main.ActualWidth+14;
   main.Top=Clamp(edge.Top+52-main.ActualHeight/2,dockArea.Top,dockArea.Bottom-main.ActualHeight);
   FloatingWindowChrome.Raise(new WindowInteropHelper(edge).Handle,main.Topmost);
  }finally{transition=false;}
 }
 void Collapse(){
  if(dockSide==""||moving||seeking||settings!=null)return;
  transition=true;
  try{hover=false;main.Hide();ApplyOpacity();}finally{transition=false;}
 }
 void ScheduleCollapse(){if(dockSide!=""&&!transition){collapse.Stop();collapse.Start();}}
 void Hide(){collapse.Stop();main.Hide();edge.Hide();hover=false;ApplyOpacity();}
 void Show(){if(dockSide!=""){edge.Show();main.Hide();}else main.Show();}
 void Undock(double x,double y){
  transition=true;collapse.Stop();
  try{dockSide="";edge.Hide();main.Left=x;main.Top=y;main.Show();hover=main.IsMouseOver;ApplyOpacity();}
  finally{transition=false;}Save();
 }
 void DragMain(object sender,MouseButtonEventArgs args){
  DependencyObject node=args.OriginalSource as DependencyObject;
  while(node!=null&&node!=main){if(node is Button)return;node=VisualTreeHelper.GetParent(node);}
  collapse.Stop();moving=true;
  try{dockSide="";edge.Hide();main.DragMove();}finally{moving=false;}
  var area=Area(main.Left+172,main.Top+main.ActualHeight/2);
  string side=Snap(new Rect(main.Left+14,main.Top+14,main.ActualWidth-28,main.ActualHeight-28),area);
  if(side!="")Dock(side,area,main.Top+(main.ActualHeight-104)/2);else Save();
 }
 void DragEdge(){
  collapse.Stop();moving=true;
  try{main.Hide();edge.DragMove();}finally{moving=false;}
  var area=Area(edge.Left+10,edge.Top+52);
  string side=Snap(new Rect(edge.Left,edge.Top,20,104),area);
  if(side!="")Dock(side,area,edge.Top);else Undock(edge.Left-14,edge.Top-14);
 }
 async Task Poll(){
  if(manager==null)manager=await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
  var players=new Dictionary<string,Candidate>();
  foreach(var candidate in manager.GetSessions()){
   try{
    var current=new Candidate{Id=candidate.SourceAppUserModelId,Status=candidate.GetPlaybackInfo().PlaybackStatus.ToString(),Updated=candidate.GetTimelineProperties().LastUpdatedTime,Session=candidate};
    Candidate previous;
    if(!players.TryGetValue(current.Id,out previous)||(current.Status=="Playing"&&previous.Status!="Playing")||((current.Status=="Playing")== (previous.Status=="Playing")&&current.Updated>previous.Updated))players[current.Id]=current;
   }catch{}
  }
  var started=new List<Candidate>();
  foreach(var player in players.Values){
   PlaybackState state;
   if(!history.TryGetValue(player.Id,out state)){state=new PlaybackState();history[player.Id]=state;}
   if(player.Status=="Playing"&&state.Status!="Playing")started.Add(player);
   state.Status=player.Status;
  }
  foreach(string id in history.Keys.ToArray())if(!players.ContainsKey(id))history.Remove(id);
  foreach(var player in started.OrderBy(p=>p.Updated).ThenBy(p=>p.Id)){history[player.Id].Order=++sequence;latest=player.Id;}
  if(!players.ContainsKey(latest))latest=players.Values.OrderByDescending(p=>history[p.Id].Order).ThenByDescending(p=>p.Updated).Select(p=>p.Id).FirstOrDefault()??"";
  string selected=chosen!=""?chosen:latest;
  Candidate selectedPlayer;
  session=players.TryGetValue(selected,out selectedPlayer)?selectedPlayer.Session:null;
  string idNow=session==null?"":session.SourceAppUserModelId;
  if(idNow!=sessionId){sessionId=idNow;lastMetadata=DateTime.MinValue;seeking=false;UI<TextBlock>("Title").Text="";UI<TextBlock>("Artist").Text="";ResetCover();}
  if(session==null){
   UI<TextBlock>("Title").Text="暂无播放";UI<TextBlock>("Artist").Text="";
   foreach(string name in new[]{"Previous","Toggle","Next","Stop"})UI<Button>(name).IsEnabled=false;
   UI<StackPanel>("Progress").Visibility=Visibility.Collapsed;Progress(0,0);return;
  }
  var currentSession=session;
  var info=session.GetPlaybackInfo();var controls=info.Controls;
  bool playing=info.PlaybackStatus==GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
  UI<Button>("Previous").IsEnabled=controls.IsPreviousEnabled;UI<Button>("Next").IsEnabled=controls.IsNextEnabled;
  UI<Button>("Stop").IsEnabled=controls.IsStopEnabled;UI<Button>("Toggle").IsEnabled=playing?controls.IsPauseEnabled:controls.IsPlayEnabled;
  UI<Button>("Toggle").Content=((char)(playing?0xE769:0xE768)).ToString();
  var time=session.GetTimelineProperties();
  double length=(time.EndTime-time.StartTime).TotalSeconds;
  double position=(time.Position-time.StartTime).TotalSeconds;
  if(playing)position+=Math.Max(0,(DateTimeOffset.UtcNow-time.LastUpdatedTime).TotalSeconds)*(info.PlaybackRate??1);
  Progress(position,length);
  bool seekable=controls.IsPlaybackPositionEnabled&&length>0&&time.MaxSeekTime>time.MinSeekTime;
  UI<StackPanel>("Progress").Visibility=seekable?Visibility.Visible:Visibility.Collapsed;
  if(seekable&&!seeking){UI<Slider>("Seek").Maximum=length;UI<Slider>("Seek").Value=Clamp(position,0,length);UI<TextBlock>("Elapsed").Text=Clock(position);UI<TextBlock>("Duration").Text=Clock(length);}
  if(DateTime.UtcNow-lastMetadata>TimeSpan.FromSeconds(2)){
   lastMetadata=DateTime.UtcNow;
   var properties=await currentSession.TryGetMediaPropertiesAsync();
   if(session!=currentSession)return;
   UI<TextBlock>("Title").Text=string.IsNullOrEmpty(properties.Title)?"未命名":properties.Title;
   UI<TextBlock>("Artist").Text=string.IsNullOrEmpty(properties.Artist)?properties.AlbumTitle:properties.Artist;
   if(properties.Thumbnail!=null){
    using(var random=await properties.Thumbnail.OpenReadAsync()){
     byte[] bytes=CoverStream.Read(random);
     using(var stream=new MemoryStream(bytes)){
      var image=new BitmapImage();image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;image.StreamSource=stream;image.EndInit();image.Freeze();
      if(session==currentSession){UI<Border>("Cover").Background=new ImageBrush(image){Stretch=Stretch.UniformToFill};UI<TextBlock>("CoverIcon").Visibility=Visibility.Collapsed;}
     }
    }
   }
  }
 }
 void ResetCover(){UI<Border>("Cover").Background=new SolidColorBrush(Color.FromArgb(21,21,21,40));UI<TextBlock>("CoverIcon").Visibility=Visibility.Visible;}
 void Progress(double position,double length){Edge<Border>("DockTrack").Visibility=length>0?Visibility.Visible:Visibility.Collapsed;Edge<Border>("DockFill").Height=length>0?84*Clamp(position/length,0,1):0;}
 async Task Control(string action){
  if(action=="Visibility"){if(main.IsVisible||edge.IsVisible)Hide();else Show();return;}
  try{
   await Poll();if(session==null)return;
   var info=session.GetPlaybackInfo();var c=info.Controls;
   bool success=true;
   switch(action){
    case "Previous":if(c.IsPreviousEnabled)success=await session.TrySkipPreviousAsync();break;
    case "Next":if(c.IsNextEnabled)success=await session.TrySkipNextAsync();break;
    case "Stop":if(c.IsStopEnabled)success=await session.TryStopAsync();break;
    case "Toggle":
     if(info.PlaybackStatus==GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing){if(c.IsPauseEnabled)success=await session.TryPauseAsync();}
     else if(c.IsPlayEnabled)success=await session.TryPlayAsync();
     break;
   }
  }catch(Exception error){Log(error);}
 }
 async Task Seek(){
  try{
   await Poll();if(session==null||seekId!=sessionId)return;
   var time=session.GetTimelineProperties();
   long ticks=time.StartTime.Ticks+(long)(UI<Slider>("Seek").Value*TimeSpan.TicksPerSecond);
   ticks=Math.Max(time.MinSeekTime.Ticks,Math.Min(time.MaxSeekTime.Ticks,ticks));
   await session.TryChangePlaybackPositionAsync(ticks);
  }catch(Exception error){Log(error);}
 }
 void CreateTray(){
  tray=new Forms.NotifyIcon();using(var stream=Resource("app.ico"))tray.Icon=new System.Drawing.Icon(stream);
  tray.Text="Now Playing";menu=new Forms.ContextMenuStrip();
  menu.Items.Add("显示 / 隐藏",null,(s,e)=>{if(main.IsVisible||edge.IsVisible)Hide();else Show();});
  menu.Items.Add("设置",null,(s,e)=>OpenSettings());
  var alpha=new Forms.ToolStripMenuItem("透明度");
  foreach(int value in new[]{10,25,50,70,85,100}){
   int captured=value;alpha.DropDownItems.Add(value+"%",null,(s,e)=>{opacity=captured/100.0;ApplyOpacity();if(normalSlider!=null)normalSlider.Value=captured;Save();});
  }
  menu.Items.Add(alpha);
  disabledMenu=new Forms.ToolStripMenuItem("禁用快捷键"){CheckOnClick=true,Checked=disabled};
  disabledMenu.CheckedChanged+=(s,e)=>{disabled=disabledMenu.Checked;hooks.Enabled=!disabled;string pending;while(hooks.Events.TryDequeue(out pending)){}Save();};
  menu.Items.Add(disabledMenu);
  startupMenu=new Forms.ToolStripMenuItem("开机自启"){Checked=startup.Enabled};
  startupMenu.Click+=(s,e)=>{try{startup.Set(!startupMenu.Checked);}catch(Exception error){Log(error);}startupMenu.Checked=startup.Enabled;};
  menu.Items.Add(startupMenu);menu.Opening+=(s,e)=>startupMenu.Checked=startup.Enabled;
  menu.Items.Add(new Forms.ToolStripSeparator());
  menu.Items.Add("退出",null,(s,e)=>main.Close());
  tray.ContextMenuStrip=menu;tray.MouseDoubleClick+=(s,e)=>{if(e.Button==Forms.MouseButtons.Left)OpenSettings();};tray.Visible=true;
 }
 void CancelRecording(){if(recordLabel!=null){recordLabel.Text=bindings[recordAction];recordLabel=null;}}
 void Capture(string chord){
  foreach(string key in bindings.Keys.ToArray())if(bindings[key]==chord){bindings[key]="";TextBox label;if(bindingLabels.TryGetValue(key,out label))label.Text="";}
  bindings[recordAction]=chord;if(recordLabel!=null)recordLabel.Text=chord;recordLabel=null;SyncBindings();Save();
 }
 void OpenSettings(){
  if(settings!=null){settings.Activate();return;}
  settings=new Window{Title="设置",ShowInTaskbar=false,Width=450,SizeToContent=SizeToContent.Height,ResizeMode=ResizeMode.NoResize,WindowStartupLocation=WindowStartupLocation.CenterScreen,Topmost=true,Background=new SolidColorBrush(Color.FromRgb(245,245,247)),FontFamily=new FontFamily("Microsoft YaHei UI")};
  using(var stream=Resource("app.ico")){var image=new BitmapImage();image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;image.StreamSource=stream;image.EndInit();settings.Icon=image;}
  var panel=new StackPanel{Margin=new Thickness(22)};settings.Content=panel;
  panel.Children.Add(new TextBlock{Text="快捷键",FontSize=20,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,14)});
  string[] actions={"Toggle","Previous","Next","Stop","Visibility"},names={"播放 / 暂停","上一首","下一首","停止","显示 / 隐藏"};
  for(int i=0;i<actions.Length;i++){
   string action=actions[i];var row=new Grid{Margin=new Thickness(0,5,0,5)};
   foreach(double width in new[]{105,-1,58,42})row.ColumnDefinitions.Add(new ColumnDefinition{Width=width<0?new GridLength(1,GridUnitType.Star):new GridLength(width)});
   row.Children.Add(new TextBlock{Text=names[i],VerticalAlignment=VerticalAlignment.Center});
   var label=new TextBox{Text=bindings[action],IsReadOnly=true,VerticalContentAlignment=VerticalAlignment.Center,FontSize=11};Grid.SetColumn(label,1);row.Children.Add(label);bindingLabels[action]=label;
   var record=new Button{Content="录入",Height=30,Margin=new Thickness(5,0,0,0)};
   record.Click+=(s,e)=>{CancelRecording();recordAction=action;recordLabel=label;label.Text="按下快捷键…";hooks.Recording=true;recordUntil=DateTime.UtcNow.AddSeconds(10);};
   Grid.SetColumn(record,2);row.Children.Add(record);
   var clear=new Button{Content="清除",Margin=new Thickness(5,0,0,0)};
   clear.Click+=(s,e)=>{hooks.Recording=false;CancelRecording();bindings[action]="";label.Text="";SyncBindings();Save();};
   Grid.SetColumn(clear,3);row.Children.Add(clear);panel.Children.Add(row);
  }
  panel.Children.Add(new TextBlock{Text="播放器",Margin=new Thickness(0,14,0,6)});
  var sources=new ComboBox();sources.Items.Add("最近播放");
  var ids=new List<string>{""};
  if(manager!=null)foreach(var item in manager.GetSessions()){if(!ids.Contains(item.SourceAppUserModelId)){ids.Add(item.SourceAppUserModelId);sources.Items.Add(item.SourceAppUserModelId);}}
  sources.SelectedIndex=Math.Max(0,ids.IndexOf(chosen));
  sources.SelectionChanged+=(s,e)=>{chosen=ids[sources.SelectedIndex];lastMetadata=DateTime.MinValue;};panel.Children.Add(sources);
  normalSlider=OpacityRow(panel,"常态不透明度（%）",opacity,v=>{opacity=v;ApplyOpacity();Save();});
  hoverSlider=OpacityRow(panel,"鼠标悬停不透明度（%）",hoverOpacity,v=>{hoverOpacity=v;ApplyOpacity();Save();});
  var pin=new CheckBox{Content="保持置顶",IsChecked=main.Topmost,Margin=new Thickness(0,14,0,0)};
  pin.Click+=(s,e)=>main.Topmost=edge.Topmost=pin.IsChecked==true;panel.Children.Add(pin);
  settings.Closed+=(s,e)=>{settings=null;normalSlider=hoverSlider=null;hooks.Recording=false;CancelRecording();Save();ScheduleCollapse();};
  settings.Show();
 }
 Slider OpacityRow(StackPanel panel,string name,double value,Action<double> change){
  panel.Children.Add(new TextBlock{Text=name,Margin=new Thickness(0,16,0,6)});
  var row=new Grid();row.ColumnDefinitions.Add(new ColumnDefinition());row.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(64)});
  var slider=new Slider{Minimum=10,Maximum=100,Value=value*100,TickFrequency=1,IsSnapToTickEnabled=true,VerticalAlignment=VerticalAlignment.Center};
  var number=new TextBox{Text=((int)slider.Value).ToString(),Margin=new Thickness(12,0,0,0),Height=28,VerticalContentAlignment=VerticalAlignment.Center};
  Action commit=()=>{int parsed;if(int.TryParse(number.Text,out parsed))slider.Value=Clamp(parsed,10,100);number.Text=((int)slider.Value).ToString();};
  slider.ValueChanged+=(s,e)=>{number.Text=((int)slider.Value).ToString();change(slider.Value/100.0);};
  number.LostKeyboardFocus+=(s,e)=>commit();number.KeyDown+=(s,e)=>{if(e.Key==Key.Return)commit();};
  row.Children.Add(slider);Grid.SetColumn(number,1);row.Children.Add(number);panel.Children.Add(row);return slider;
 }
 async Task Tests(){
  await Poll();
  if(manager==null)throw new Exception("媒体接口初始化失败");
  hover=false;opacity=.24;hoverOpacity=.63;ApplyOpacity();if(main.Opacity!=.24)throw new Exception("常态透明度错误");
  hover=true;ApplyOpacity();if(main.Opacity!=.63||edge.Opacity!=.63)throw new Exception("整窗悬停透明度错误");
  var area=Area(main.Left+100,main.Top+100);
  Dock("Left",area,area.Top+150);if(main.IsVisible||!edge.IsVisible)throw new Exception("吸附失败");
  Progress(25,100);if(Edge<Border>("DockFill").Height!=21)throw new Exception("竖向进度错误");
  Expand();if(!main.IsVisible)throw new Exception("展开失败");
  Collapse();if(main.IsVisible)throw new Exception("收回失败");
  Undock(area.Left+100,area.Top+100);if(!main.IsVisible||edge.IsVisible)throw new Exception("取消吸附失败");
  OpenSettings();if(settings.Icon==null)throw new Exception("设置图标缺失");settings.Close();
  var dispatch=typeof(GlobalShortcuts).GetMethod("Dispatch",BindingFlags.Instance|BindingFlags.NonPublic);
  hooks.Bindings["Ctrl+Alt+Space"]="Toggle";hooks.Enabled=false;
  if((bool)dispatch.Invoke(hooks,new object[]{"Ctrl+Alt+Space",false}))throw new Exception("快捷键禁用失败");
  hooks.Enabled=true;if(!(bool)dispatch.Invoke(hooks,new object[]{"Ctrl+Alt+Space",false}))throw new Exception("快捷键恢复失败");
  startup.Test();
 }
}

class StartupManager {
 readonly string root,executable;
 string name="NowPlaying-"+System.Security.Principal.WindowsIdentity.GetCurrent().User.Value;
 string sid=System.Security.Principal.WindowsIdentity.GetCurrent().User.Value;
 public StartupManager(string dataRoot,string binary){root=dataRoot;executable=binary;}
 dynamic Service(){dynamic service=Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));service.Connect();return service;}
 dynamic TaskInfo(){try{return Service().GetFolder(@"\").GetTask(name);}catch{return null;}}
 public bool Enabled {get{try{dynamic task=TaskInfo();return task!=null&&task.Enabled&&File.Exists((string)task.Definition.Actions.Item(1).Path);}catch{return false;}}}
 string Deploy(){
  string version=Assembly.GetExecutingAssembly().GetName().Version.ToString();
  string folder=Path.Combine(root,"app",version);Directory.CreateDirectory(folder);
  string path=Path.Combine(folder,"NowPlaying.exe");
  string physical=File.Exists(path)?StartupPaths.ResolveFile(path):path;
  if(!string.Equals(executable,physical,StringComparison.OrdinalIgnoreCase))File.Copy(executable,path,true);
  return StartupPaths.ResolveFile(path);
 }
 public void Set(bool enabled){
  dynamic service=Service();dynamic folder=service.GetFolder(@"\");
  if(enabled){
   string path=Deploy();dynamic task=service.NewTask(0);
   task.RegistrationInfo.Description="NowPlaying";task.Principal.UserId=sid;task.Principal.LogonType=3;task.Principal.RunLevel=0;
   task.Settings.Enabled=true;task.Settings.StartWhenAvailable=true;task.Settings.DisallowStartIfOnBatteries=false;
   task.Settings.StopIfGoingOnBatteries=false;task.Settings.ExecutionTimeLimit="PT0S";task.Settings.MultipleInstances=2;
   dynamic trigger=task.Triggers.Create(9);trigger.UserId=sid;trigger.Enabled=true;trigger.Delay="PT15S";
   dynamic action=task.Actions.Create(0);action.Path=path;
   folder.RegisterTaskDefinition(name,task,6,sid,null,3);
  }else{if(TaskInfo()!=null)folder.DeleteTask(name,0);}
  using(var key=Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run",true)){if(key!=null)key.DeleteValue("NowPlaying",false);}
 }
 public void Migrate(){
  bool legacy=false;
  using(var key=Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")){legacy=key!=null&&!string.IsNullOrWhiteSpace((string)key.GetValue("NowPlaying"));}
  if(Enabled||legacy)Set(true);
 }
 public void Test(){
  string original=name;name="NowPlaying-NativeTest-"+Guid.NewGuid().ToString("N");
  string probe=Path.GetTempFileName();probe=StartupPaths.ResolveFile(probe);
  try{
   dynamic service=Service();dynamic task=service.NewTask(0);
   task.Principal.UserId=sid;task.Principal.LogonType=3;task.Principal.RunLevel=0;
   task.Settings.DisallowStartIfOnBatteries=false;task.Settings.StopIfGoingOnBatteries=false;
   dynamic trigger=task.Triggers.Create(9);trigger.UserId=sid;trigger.Delay="PT15S";
   dynamic action=task.Actions.Create(0);action.Path=executable;action.Arguments="--startup-probe \""+probe+"\"";
   dynamic registered=service.GetFolder(@"\").RegisterTaskDefinition(name,task,6,sid,null,3);
   if(!Enabled)throw new Exception("原生登录任务注册失败");
   registered.Run(null);
   DateTime deadline=DateTime.UtcNow.AddSeconds(20);
   while(DateTime.UtcNow<deadline&&new FileInfo(probe).Length==0)Thread.Sleep(150);
   string result=File.ReadAllText(probe);
   if(!result.StartsWith("PASS native task;"))throw new Exception("真实任务启动失败："+result);
  }finally{try{Service().GetFolder(@"\").DeleteTask(name,0);}catch{}File.Delete(probe);name=original;}
 }
}