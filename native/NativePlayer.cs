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
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Windows.Media.Control;
using Forms=System.Windows.Forms;

[assembly:AssemblyTitle("NowPlaying")]
[assembly:AssemblyProduct("NowPlaying")]
[assembly:AssemblyVersion("1.1.0.0")]
[assembly:AssemblyFileVersion("1.1.0.0")]
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
 string backgroundColor="#F2EFFA";
 bool backgroundImage,wasPlaying,motionTesting;
 bool Animations {get{return (!testing||motionTesting)&&SystemParameters.ClientAreaAnimation;}}
 BitmapSource backgroundBitmap;
 int motion;
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
    File.WriteAllText(args[1],"PASS native startup; media sessions="+request.Result.GetSessions().Count);
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
 if(test)root=Path.Combine(Path.GetTempPath(),"NowPlaying UI test "+Guid.NewGuid().ToString("N"));
}
 int Run(string testLog){
  if(!testing){
   mutex=new Mutex(false,@"Local\CodexNowPlaying");
   bool owned=false;try{owned=mutex.WaitOne(0);}catch(AbandonedMutexException){owned=true;}
   if(!owned){try{using(var e=EventWaitHandle.OpenExisting(@"Local\CodexNowPlayingWake"))e.Set();}catch{}return 0;}
   wake=new EventWaitHandle(false,EventResetMode.AutoReset,@"Local\CodexNowPlayingWake");
  }
  Load();
  if(backgroundImage){try{backgroundBitmap=ReadBackground(Path.Combine(root,"background.png"));}catch{backgroundImage=false;}}
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
  main.ContentRendered+=(s,e)=>{if(!testing){var slide=(TranslateTransform)UI<Border>("Card").RenderTransform;slide.Y=8;Animate(slide,TranslateTransform.YProperty,0,260);}};
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
  ApplyBackground();ApplyOpacity();
  main.Show();main.UpdateLayout();
  Rect area=Area(double.IsNaN(left)?0:left+172,double.IsNaN(top)?0:top+100);
  if(double.IsNaN(left)){main.Left=area.Right-main.ActualWidth-20;main.Top=area.Bottom-main.ActualHeight-20;}
  else{main.Left=Clamp(left,area.Left,area.Right-main.ActualWidth);main.Top=Clamp(top,area.Top,area.Bottom-main.ActualHeight);}
  if(dockSide!="")Dock(dockSide,Area(dockLeft+10,dockTop+52),dockTop);
  int code=0;
  if(testing){
   app.Dispatcher.BeginInvoke(new Action(async()=>{
    try{await Tests();await MotionTests();await VisualTests(Path.GetDirectoryName(testLog));File.WriteAllText(testLog,"PASS: native WPF, media, hooks, opacity, docking, settings, registry startup, startup path repair, background presets, image import, background reload, hover and docking animations");}
    catch(Exception error){code=1;File.WriteAllText(testLog,"FAIL: "+error);}
    main.Close();if(Directory.Exists(root))Directory.Delete(root,true);
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
   if(data.TryGetValue("backgroundColor",out value)){try{var color=(Color)ColorConverter.ConvertFromString(Convert.ToString(value));backgroundColor=color.ToString();}catch{}}
   if(data.TryGetValue("backgroundImage",out value))backgroundImage=Convert.ToBoolean(value);
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
   var data=new Dictionary<string,object>{{"backgroundColor",backgroundColor},{"backgroundImage",backgroundImage},{"bindings",bindings},{"opacity",opacity},{"hoverOpacity",hoverOpacity},{"shortcutsDisabled",disabled},{"left",main.Left},{"top",main.Top},{"dockSide",dockSide},{"dockLeft",double.IsNaN(edge.Left)?dockLeft:edge.Left},{"dockTop",double.IsNaN(edge.Top)?dockTop:edge.Top}};
   File.WriteAllText(Config,new JavaScriptSerializer().Serialize(data),new UTF8Encoding(false));
  }catch(Exception e){Log(e);}
 }
 void Animate(System.Windows.Media.Animation.Animatable target,DependencyProperty property,double value,double duration=180){
  double from=(double)target.GetValue(property);target.BeginAnimation(property,null);target.SetValue(property,value);
  if(Animations)target.BeginAnimation(property,new DoubleAnimation(from,value,TimeSpan.FromMilliseconds(duration)){EasingFunction=new CubicEase{EasingMode=EasingMode.EaseOut},FillBehavior=FillBehavior.Stop});
 }
 void Fade(UIElement target,double value){
  double from=target.Opacity;target.BeginAnimation(UIElement.OpacityProperty,null);target.Opacity=value;
  if(Animations)target.BeginAnimation(UIElement.OpacityProperty,new DoubleAnimation(from,value,TimeSpan.FromMilliseconds(180)){FillBehavior=FillBehavior.Stop});
 }
 void ApplyOpacity(){
  double value=Clamp(hover?hoverOpacity:opacity,.1,1);
  Fade(main,value);Fade(edge,value);Fade(UI<StackPanel>("Tools"),hover?1:0);
  UI<StackPanel>("Tools").IsHitTestVisible=hover;
  Animate((TranslateTransform)UI<Border>("Card").RenderTransform,TranslateTransform.YProperty,hover?-2:0);
 }
 static BitmapSource ReadBackground(string path){
  using(var stream=File.OpenRead(path)){
   var bitmap=new BitmapImage();bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;bitmap.DecodePixelWidth=1200;bitmap.StreamSource=stream;bitmap.EndInit();bitmap.Freeze();return bitmap;
  }
 }
 void ImportBackground(string path){
  var bitmap=ReadBackground(path);
  Directory.CreateDirectory(root);
  var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
  string destination=Path.Combine(root,"background.png"),temporary=Path.Combine(root,"background.tmp");
  try{using(var file=File.Create(temporary))encoder.Save(file);File.Copy(temporary,destination,true);}
  finally{if(File.Exists(temporary))File.Delete(temporary);}
  backgroundBitmap=bitmap;backgroundImage=true;ApplyBackground();Save();
 }
 void ApplyBackground(){
  Color color=(Color)ColorConverter.ConvertFromString(backgroundColor);color.A=255;
  bool dark=(color.R*.299+color.G*.587+color.B*.114)<135;
  Color end=Color.FromRgb((byte)Math.Min(255,color.R+12),(byte)Math.Min(255,color.G+12),(byte)Math.Min(255,color.B+12));
  Brush surface=new LinearGradientBrush(end,color,65);
  if(backgroundImage&&backgroundBitmap!=null){
   var drawings=new DrawingGroup();var rect=new RectangleGeometry(new Rect(0,0,300,240));
   drawings.Children.Add(new GeometryDrawing(new ImageBrush(backgroundBitmap){Stretch=Stretch.UniformToFill},null,rect));
   drawings.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromArgb(190,color.R,color.G,color.B)),null,rect));
   surface=new DrawingBrush(drawings){Stretch=Stretch.Fill};
  }
  UI<Border>("Card").Background=surface;Edge<Border>("EdgeCard").Background=new SolidColorBrush(color);
  Brush foreground=new SolidColorBrush(dark?Color.FromRgb(248,246,255):Color.FromRgb(43,36,62));
  Brush muted=new SolidColorBrush(dark?Color.FromRgb(202,195,218):Color.FromRgb(115,104,136));
  main.Foreground=foreground;
  foreach(string name in new[]{"Title","Artist","Elapsed","Duration","PlaybackLabel"})UI<TextBlock>(name).Foreground=name=="Title"?foreground:muted;
  foreach(string name in new[]{"Previous","Toggle","Next","Stop","Settings","Close"})UI<Button>(name).Foreground=foreground;
 }
 void PlaybackMotion(bool playing){
  UI<TextBlock>("PlaybackLabel").Text=playing?"正在播放":session==null?"音乐待命":"已暂停";
  if(wasPlaying==playing)return;wasPlaying=playing;
  for(int i=0;i<3;i++){
   var bar=UI<Border>("Beat"+i);bar.BeginAnimation(FrameworkElement.HeightProperty,null);bar.Height=5+i*2;
   if(playing&&Animations)bar.BeginAnimation(FrameworkElement.HeightProperty,new DoubleAnimation(4,13-i,TimeSpan.FromMilliseconds(320+i*95)){AutoReverse=true,RepeatBehavior=RepeatBehavior.Forever,BeginTime=TimeSpan.FromMilliseconds(i*80),EasingFunction=new SineEase()});
  }
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
  motion++;main.BeginAnimation(UIElement.OpacityProperty,null);
  transition=true;collapse.Stop();
  try{
   dockSide=side;dockArea=area;hover=false;
   edge.Left=side=="Left"?area.Left:area.Right-20;edge.Top=Clamp(y,area.Top,area.Bottom-104);
   edge.Topmost=main.Topmost;edge.Show();main.Hide();ApplyOpacity();
  }finally{transition=false;}Save();
 }
 void Expand(){
  if(dockSide==""||moving||transition)return;
  motion++;main.BeginAnimation(UIElement.OpacityProperty,null);
  transition=true;collapse.Stop();
  try{
   bool opening=!main.IsVisible;hover=true;ApplyOpacity();main.Show();main.UpdateLayout();
   if(opening){var slide=(TranslateTransform)UI<Border>("Card").RenderTransform;slide.X=dockSide=="Left"?-16:16;Animate(slide,TranslateTransform.XProperty,0,240);}
   main.Left=dockSide=="Left"?edge.Left+6:edge.Left-main.ActualWidth+14;
   main.Top=Clamp(edge.Top+52-main.ActualHeight/2,dockArea.Top,dockArea.Bottom-main.ActualHeight);
   FloatingWindowChrome.Raise(new WindowInteropHelper(edge).Handle,main.Topmost);
  }finally{transition=false;}
 }
 void Collapse(){
  if(dockSide==""||moving||seeking||settings!=null)return;
  int token=++motion;hover=false;ApplyOpacity();
  if(!Animations){main.Hide();return;}
  var fade=new DoubleAnimation(main.Opacity,0,TimeSpan.FromMilliseconds(130)){FillBehavior=FillBehavior.Stop};
  fade.Completed+=(s,e)=>{if(token==motion&&!main.IsMouseOver&&!edge.IsMouseOver)main.Hide();};
  main.BeginAnimation(UIElement.OpacityProperty,fade);
 }
 void ScheduleCollapse(){if(dockSide!=""&&!transition){collapse.Stop();collapse.Start();}}
 void Hide(){motion++;main.BeginAnimation(UIElement.OpacityProperty,null);collapse.Stop();main.Hide();edge.Hide();hover=false;ApplyOpacity();}
 void Show(){motion++;main.BeginAnimation(UIElement.OpacityProperty,null);if(dockSide!=""){edge.Show();main.Hide();}else main.Show();}
 void Undock(double x,double y){
  motion++;main.BeginAnimation(UIElement.OpacityProperty,null);
  transition=true;collapse.Stop();
  try{dockSide="";edge.Hide();main.Left=x;main.Top=y;main.Show();hover=main.IsMouseOver;ApplyOpacity();}
  finally{transition=false;}Save();
 }
 void DragMain(object sender,MouseButtonEventArgs args){
  DependencyObject node=args.OriginalSource as DependencyObject;
  while(node!=null&&node!=main){if(node is Button)return;node=VisualTreeHelper.GetParent(node);}
  motion++;main.BeginAnimation(UIElement.OpacityProperty,null);collapse.Stop();moving=true;
  try{dockSide="";edge.Hide();main.DragMove();}finally{moving=false;}
  var area=Area(main.Left+172,main.Top+main.ActualHeight/2);
  string side=Snap(new Rect(main.Left+14,main.Top+14,main.ActualWidth-28,main.ActualHeight-28),area);
  if(side!="")Dock(side,area,main.Top+(main.ActualHeight-104)/2);else Save();
 }
 void DragEdge(){
  motion++;main.BeginAnimation(UIElement.OpacityProperty,null);
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
   PlaybackMotion(false);
   UI<TextBlock>("Title").Text="暂无播放";UI<TextBlock>("Artist").Text="";
   foreach(string name in new[]{"Previous","Toggle","Next","Stop"})UI<Button>(name).IsEnabled=false;
   UI<StackPanel>("Progress").Visibility=Visibility.Collapsed;Progress(0,0);return;
  }
  var currentSession=session;
  var info=session.GetPlaybackInfo();var controls=info.Controls;
  bool playing=info.PlaybackStatus==GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
  PlaybackMotion(playing);
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
  startupMenu.Click+=(s,e)=>{try{startup.Set(!startupMenu.Checked);}catch(Exception error){Log(error);Forms.MessageBox.Show("无法更新开机自启："+error.Message,"NowPlaying");}startupMenu.Checked=startup.Enabled;};
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
  settings.Resources=(ResourceDictionary)XamlReader.Parse(@"<ResourceDictionary xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"" xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml"">
   <Style TargetType=""Button""><Setter Property=""Background"" Value=""#ECE8F4""/><Setter Property=""Foreground"" Value=""#423654""/><Setter Property=""BorderThickness"" Value=""0""/><Setter Property=""Padding"" Value=""6,3""/><Setter Property=""Cursor"" Value=""Hand""/><Setter Property=""Template""><Setter.Value><ControlTemplate TargetType=""Button""><Border CornerRadius=""7"" Background=""{TemplateBinding Background}"" Padding=""{TemplateBinding Padding}""><ContentPresenter HorizontalAlignment=""Center"" VerticalAlignment=""Center""/></Border><ControlTemplate.Triggers><Trigger Property=""IsMouseOver"" Value=""True""><Setter Property=""Opacity"" Value=""0.8""/></Trigger><Trigger Property=""IsPressed"" Value=""True""><Setter Property=""Opacity"" Value=""0.6""/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>
   <Style TargetType=""TextBox""><Setter Property=""Background"" Value=""White""/><Setter Property=""BorderBrush"" Value=""#E0DBE9""/><Setter Property=""Padding"" Value=""6,2""/></Style>
  </ResourceDictionary>");
  var panel=new StackPanel{Margin=new Thickness(22)};settings.MaxHeight=Math.Max(320,SystemParameters.WorkArea.Height-60);settings.Content=new ScrollViewer{Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
  BackgroundSettings(panel);
  panel.Children.Add(new TextBlock{Text="快捷键",FontSize=16,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,20,0,10)});
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
 sealed class DialogOwner:Forms.IWin32Window {
  public IntPtr Handle {get;private set;}
  public DialogOwner(IntPtr handle){Handle=handle;}
 }
 void BackgroundSettings(StackPanel panel){
  panel.Children.Add(new TextBlock{Text="背景",FontSize=16,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,18,0,10)});
  var choices=new StackPanel{Orientation=Orientation.Horizontal};panel.Children.Add(choices);
  string[] colors={"#F2EFFA","#E6F3ED","#FAEDE1","#252535"},names={"雾紫","薄荷","奶杏","夜色"};
  for(int i=0;i<colors.Length;i++){
   string color=colors[i];var button=new Button{Content=names[i],Width=88,Height=32,Margin=new Thickness(0,0,6,0),Background=new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)),Foreground=i==3?Brushes.White:Brushes.Black};
   button.Click+=(s,e)=>{backgroundColor=color;backgroundImage=false;ApplyBackground();Save();};choices.Children.Add(button);
  }
  var actions=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,8,0,0)};panel.Children.Add(actions);
  var custom=new Button{Content="自选颜色",Width=110,Height=30,Margin=new Thickness(0,0,8,0)};
  custom.Click+=(s,e)=>{
   using(var dialog=new Forms.ColorDialog()){
    var color=(Color)ColorConverter.ConvertFromString(backgroundColor);dialog.Color=System.Drawing.Color.FromArgb(color.R,color.G,color.B);dialog.FullOpen=true;
    if(dialog.ShowDialog(new DialogOwner(new WindowInteropHelper(settings).Handle))==Forms.DialogResult.OK){backgroundColor=Color.FromRgb(dialog.Color.R,dialog.Color.G,dialog.Color.B).ToString();backgroundImage=false;ApplyBackground();Save();}
   }
  };
  actions.Children.Add(custom);
  var image=new Button{Content="选择图片",Width=110,Height=30,Margin=new Thickness(0,0,8,0)};
  image.Click+=(s,e)=>{
   var dialog=new Microsoft.Win32.OpenFileDialog{Filter="图片|*.png;*.jpg;*.jpeg;*.bmp",CheckFileExists=true};
   if(dialog.ShowDialog(settings)==true){try{ImportBackground(dialog.FileName);}catch(Exception error){Forms.MessageBox.Show("无法读取图片："+error.Message,"NowPlaying");}}
  };actions.Children.Add(image);
  var reset=new Button{Content="恢复默认",Width=110,Height=30};reset.Click+=(s,e)=>{backgroundColor="#F2EFFA";backgroundImage=false;ApplyBackground();Save();};actions.Children.Add(reset);
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
 void Snapshot(Window window,string path){
  window.UpdateLayout();var image=new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),(int)Math.Ceiling(window.ActualHeight),96,96,PixelFormats.Pbgra32);image.Render(window);
  var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using(var file=File.Create(path))encoder.Save(file);
 }
 async Task MotionTests(){
  motionTesting=true;
  try{
   hover=false;ApplyOpacity();await Task.Delay(230);
   hover=true;ApplyOpacity();await Task.Delay(230);
   if(Math.Abs(main.Opacity-hoverOpacity)>.01)throw new Exception("悬停动效未到达目标透明度");
   var area=Area(main.Left+100,main.Top+100);Dock("Left",area,area.Top+150);Expand();await Task.Delay(280);
   if(Math.Abs(((TranslateTransform)UI<Border>("Card").RenderTransform).X)>.01)throw new Exception("展开动效未完成");
   Collapse();Expand();await Task.Delay(200);if(!main.IsVisible)throw new Exception("收回后重新展开被旧动效隐藏");
   Hide();await Task.Delay(200);if(main.IsVisible||edge.IsVisible)throw new Exception("动效期间隐藏失败");
   Undock(area.Left+100,area.Top+100);
  }finally{motionTesting=false;ApplyOpacity();}
 }
 async Task VisualTests(string folder){
  opacity=hoverOpacity=1;hover=true;ApplyOpacity();backgroundImage=false;backgroundColor="#F2EFFA";ApplyBackground();
  await Task.Delay(150);Snapshot(main,Path.Combine(folder,"player-light.png"));
  backgroundColor="#252535";ApplyBackground();await Task.Delay(50);Snapshot(main,Path.Combine(folder,"player-dark.png"));
  backgroundColor="#F2EFFA";backgroundImage=true;ApplyBackground();await Task.Delay(50);Snapshot(main,Path.Combine(folder,"player-image.png"));
  backgroundImage=false;ApplyBackground();OpenSettings();await Task.Delay(150);Snapshot(settings,Path.Combine(folder,"settings.png"));settings.Close();
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
  Directory.CreateDirectory(root);
  var sample=new DrawingVisual();using(var dc=sample.RenderOpen()){dc.DrawRectangle(new LinearGradientBrush(Colors.Coral,Colors.SlateBlue,35),null,new Rect(0,0,400,300));}
  var bitmap=new RenderTargetBitmap(400,300,96,96,PixelFormats.Pbgra32);bitmap.Render(sample);
  var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));string original=Path.Combine(root,"sample.png");using(var file=File.Create(original))encoder.Save(file);
  ImportBackground(original);File.Delete(original);
  if(!(UI<Border>("Card").Background is DrawingBrush)||!File.Exists(Path.Combine(root,"background.png")))throw new Exception("背景图片导入失败");
  File.WriteAllText(Config,new JavaScriptSerializer().Serialize(new Dictionary<string,object>{{"backgroundColor","#252535"},{"backgroundImage",true}}));
  backgroundImage=false;backgroundColor="#F2EFFA";Load();
  if(!backgroundImage||backgroundColor!="#FF252535")throw new Exception("背景设置恢复失败");
  backgroundBitmap=ReadBackground(Path.Combine(root,"background.png"));ApplyBackground();
  backgroundImage=false;ApplyBackground();if(!(UI<Border>("Card").Background is LinearGradientBrush))throw new Exception("背景预设切换失败");
 }
}

class StartupManager {
 const string RunKey=@"Software\Microsoft\Windows\CurrentVersion\Run";
 readonly string root,executable;
 string name="NowPlaying";
 string LegacyName {get{return "NowPlaying-"+System.Security.Principal.WindowsIdentity.GetCurrent().User.Value;}}
 public StartupManager(string dataRoot,string binary){root=dataRoot;executable=binary;}
 string Command {get{using(var key=Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey)){return key==null?null:key.GetValue(name) as string;}}}
 public bool Enabled {get{
  string command=Command;
  return !string.IsNullOrEmpty(command)&&command.Length>2&&command[0]=='"'&&command[command.Length-1]=='"'&&File.Exists(command.Substring(1,command.Length-2));
 }}
 string InstalledPath {get{return Path.Combine(root,"app",Assembly.GetExecutingAssembly().GetName().Version.ToString(),"NowPlaying.exe");}}
 string Deploy(){
  string path=InstalledPath;Directory.CreateDirectory(Path.GetDirectoryName(path));
  string physical=File.Exists(path)?StartupPaths.ResolveFile(path):path;
  if(!string.Equals(executable,physical,StringComparison.OrdinalIgnoreCase))File.Copy(executable,path,true);
  return StartupPaths.ResolveFile(path);
 }
 dynamic LegacyTask(){
  dynamic service=Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));service.Connect();
  try{return service.GetFolder(@"\").GetTask(LegacyName);}
  catch(System.Runtime.InteropServices.COMException error){if(error.ErrorCode==unchecked((int)0x80070002))return null;throw;}
 }
 void LogMigration(Exception error){try{Directory.CreateDirectory(root);File.AppendAllText(Path.Combine(root,"errors.log"),DateTime.Now.ToString("s")+" Legacy startup migration: "+error+Environment.NewLine);}catch{}}
 void RemoveLegacy(){
  if(name!="NowPlaying")return;
  try{
   dynamic task=LegacyTask();if(task==null)return;
   task.Enabled=false;
   dynamic service=Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));service.Connect();
   service.GetFolder(@"\").DeleteTask(LegacyName,0);
  }catch(Exception error){LogMigration(error);}
 }
 public void Set(bool enabled){
  if(enabled){
   string command="\""+Deploy()+"\"";
   if(command.Length>260)throw new IOException("自启路径过长，请缩短用户数据目录路径。");
   using(var key=Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey)){key.SetValue(name,command,Microsoft.Win32.RegistryValueKind.String);}
  }else{
   using(var key=Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey,true)){if(key!=null)key.DeleteValue(name,false);}
  }
  RemoveLegacy();
 }
 public void Migrate(){
  bool registered;
  using(var key=Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey)){registered=key!=null&&key.GetValueNames().Contains(name,StringComparer.OrdinalIgnoreCase);}
  if(registered){
   string path=InstalledPath;
   string expected=File.Exists(path)?"\""+StartupPaths.ResolveFile(path)+"\"":null;
   if(expected==null||!string.Equals(Command,expected,StringComparison.OrdinalIgnoreCase))Set(true);
   else RemoveLegacy();
   return;
  }
  if(name!="NowPlaying")return;
  bool enabled=false;
  try{dynamic task=LegacyTask();enabled=task!=null&&task.Enabled;}catch(Exception error){LogMigration(error);}
  if(enabled)Set(true);
 }
 public void Test(){
  string temp=Path.Combine(Path.GetTempPath(),"NowPlaying registry test "+Guid.NewGuid().ToString("N"));
  var test=new StartupManager(temp,executable);test.name="NowPlaying-Test-"+Guid.NewGuid().ToString("N");
  string probe=Path.Combine(temp,"probe.txt");
  try{
   test.Set(true);
   string command=test.Command;
   if(!test.Enabled||command!="\""+test.Deploy()+"\"")throw new Exception("注册表启动项或路径引号错误");
   var info=new ProcessStartInfo(command.Substring(1,command.Length-2),"--startup-probe \""+probe+"\""){UseShellExecute=false,CreateNoWindow=true};
   using(var process=Process.Start(info)){
    if(!process.WaitForExit(20000)){process.Kill();process.WaitForExit();throw new Exception("启动项命令运行超时");}
    if(process.ExitCode!=0||!File.ReadAllText(probe).StartsWith("PASS native startup;"))throw new Exception("启动项命令运行失败");
   }
   foreach(object invalid in new object[]{"", "\""+Path.Combine(temp,"missing.exe")+"\"", 123}){
    using(var key=Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey,true)){key.SetValue(test.name,invalid);}
    test.Migrate();
    if(test.Command!=command||!test.Enabled)throw new Exception("无效启动路径未修复");
   }
   string installed=test.InstalledPath;
   DateTime written=File.GetLastWriteTimeUtc(installed);test.Migrate();
   if(test.Command!=command||File.GetLastWriteTimeUtc(installed)!=written)throw new Exception("有效启动项被重复部署");
   File.Delete(installed);test.Migrate();
   if(!test.Enabled||test.Command!=command)throw new Exception("丢失的自启文件未恢复");
   test.Set(false);test.Migrate();if(test.Command!=null)throw new Exception("取消自启后不应重新注册");
  }finally{test.Set(false);if(Directory.Exists(temp))Directory.Delete(temp,true);}
 }
}
