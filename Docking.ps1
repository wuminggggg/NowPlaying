# Screen-edge docking. Coordinates are WPF device-independent pixels.
$script:dockSide=''
$script:dockExpanded=$false
$script:dockTransition=$false
$script:windowMoving=$false
$script:positionInitialized=$false
$script:dockArea=[Windows.Rect]::Empty
$script:dockRestore=$null
if(!$SelfTest -and !$Preview -and $saved -and $saved.dockSide -in @('Left','Right')){
 $script:dockRestore=$saved
}
[xml]$dockXaml=@"
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
 xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Title="Now Playing Edge"
 Width="20" Height="104" WindowStyle="None" ResizeMode="NoResize" AllowsTransparency="True"
 Background="Transparent" ShowInTaskbar="False" ShowActivated="False" Topmost="True"
 UseLayoutRounding="True" SnapsToDevicePixels="True">
 <Border CornerRadius="7" Background="#F5F5F8" BorderBrush="#40FFFFFF" BorderThickness="1" Padding="6,9" Cursor="SizeAll">
  <Grid><Border x:Name="DockTrack" Width="6" Height="84" Background="#20000000" CornerRadius="3">
   <Grid ClipToBounds="True"><Border x:Name="DockFill" Height="0" VerticalAlignment="Bottom" Background="#727A91" CornerRadius="3"/></Grid>
  </Border></Grid>
 </Border>
</Window>
"@
$script:dockWindow=[Windows.Markup.XamlReader]::Load([Xml.XmlNodeReader]::new($dockXaml))
$ui.DockFill=$script:dockWindow.FindName('DockFill')
$ui.DockTrack=$script:dockWindow.FindName('DockTrack')
$script:dockWindow.Add_SourceInitialized({
 [FloatingWindowChrome]::Apply([Windows.Interop.WindowInteropHelper]::new($script:dockWindow).Handle)
})
function Get-ScreenArea([double]$Left,[double]$Top){
 $source=[Windows.PresentationSource]::FromVisual($window)
 $toDevice=if($source){$source.CompositionTarget.TransformToDevice}else{[Windows.Media.Matrix]::Identity}
 $fromDevice=if($source){$source.CompositionTarget.TransformFromDevice}else{[Windows.Media.Matrix]::Identity}
 $point=$toDevice.Transform([Windows.Point]::new($Left,$Top))
 $screen=[Windows.Forms.Screen]::FromPoint([Drawing.Point]::new([int]$point.X,[int]$point.Y))
 $work=$screen.WorkingArea
 $origin=$fromDevice.Transform([Windows.Point]::new($work.Left,$work.Top))
 $size=$fromDevice.Transform([Windows.Vector]::new($work.Width,$work.Height))
 return [Windows.Rect]::new($origin.X,$origin.Y,$size.X,$size.Y)
}
function Get-SnapSide([Windows.Rect]$Content,[Windows.Rect]$Area){
 if($Content.Left -le $Area.Left+24){return 'Left'}
 if($Content.Right -ge $Area.Right-24){return 'Right'}
 return ''
}
function Update-DockProgress([double]$Position,[double]$Length){
 if($Length -gt 0){
  $ui.DockTrack.Visibility='Visible'
  $ui.DockFill.Height=84*[math]::Max(0.0,[math]::Min(1.0,$Position/$Length))
 }else{
  $ui.DockTrack.Visibility='Collapsed'
  $ui.DockFill.Height=0
 }
}
function Set-Docked([string]$Side,[Windows.Rect]$Area,[double]$Top){
 $script:dockCollapse.Stop()
 $script:dockTransition=$true
 try{
  $script:dockSide=$Side;$script:dockArea=$Area;$script:dockExpanded=$false
  $script:dockWindow.Left=if($Side -eq 'Left'){$Area.Left}else{$Area.Right-20}
  $script:dockWindow.Top=[math]::Max($Area.Top,[math]::Min($Area.Bottom-104,$Top))
  $script:dockWindow.Topmost=$window.Topmost
  $script:dockWindow.Show();$window.Hide()
  $script:isHovered=$false;$ui.Tools.Opacity=0;Apply-Opacity
 }finally{$script:dockTransition=$false}
 Save-Settings
}
function Expand-Docked{
 if(!$script:dockSide -or $script:dockTransition -or $script:windowMoving){return}
 $script:dockCollapse.Stop()
 $script:dockTransition=$true
 try{
  $script:dockExpanded=$true;$script:isHovered=$true;$ui.Tools.Opacity=1;Apply-Opacity
  $window.Show();$window.UpdateLayout()
  $window.Left=if($script:dockSide -eq 'Left'){
   $script:dockWindow.Left+20-14
  }else{
   $script:dockWindow.Left-$window.ActualWidth+14
  }
  $window.Top=[math]::Max($script:dockArea.Top,[math]::Min(
   $script:dockArea.Bottom-$window.ActualHeight,
   $script:dockWindow.Top+52-$window.ActualHeight/2))
  [FloatingWindowChrome]::Raise([Windows.Interop.WindowInteropHelper]::new($script:dockWindow).Handle,$window.Topmost)
 }finally{$script:dockTransition=$false}
}
function Collapse-Docked{
 if(!$script:dockSide -or $script:windowMoving -or $script:settingsWindow -or $script:dragging){return}
 $script:dockTransition=$true
 try{
  $script:dockExpanded=$false;$script:isHovered=$false;$ui.Tools.Opacity=0
  $window.Hide();Apply-Opacity
 }finally{$script:dockTransition=$false}
}
function Test-DockPointer{
 if($window.IsMouseOver -or $script:dockWindow.IsMouseOver){return $true}
 $cursor=[Windows.Forms.Cursor]::Position
 $point=[Windows.Point]::new($cursor.X,$cursor.Y)
 foreach($surface in @($window,$script:dockWindow)){
  if($surface.IsVisible){
   $local=$surface.PointFromScreen($point)
   if($local.X -ge 0 -and $local.Y -ge 0 -and $local.X -lt $surface.ActualWidth -and $local.Y -lt $surface.ActualHeight){return $true}
  }
 }
 return $false
}
$script:dockCollapse=[Windows.Threading.DispatcherTimer]::new()
$script:dockCollapse.Interval=[timespan]::FromMilliseconds(300)
$script:dockCollapse.Add_Tick({
 $script:dockCollapse.Stop()
 if($script:dockSide -and !$script:dockTransition -and !$script:windowMoving -and !(Test-DockPointer)){Collapse-Docked}
})
function Schedule-DockCollapse{
 if($script:dockSide -and !$script:dockTransition){$script:dockCollapse.Stop();$script:dockCollapse.Start()}
}
function Hide-Widget{
 $script:dockCollapse.Stop();$window.Hide();$script:dockWindow.Hide()
 $script:dockExpanded=$false;$script:isHovered=$false;Apply-Opacity
}
function Show-Widget{
 if($script:dockSide){
  $script:dockWindow.Show();$window.Hide();$script:dockExpanded=$false
 }else{$window.Show()}
}
function Toggle-Widget{
 if($window.IsVisible -or $script:dockWindow.IsVisible){Hide-Widget}else{Show-Widget}
}
function Undock-Widget([double]$Left,[double]$Top){
 $script:dockCollapse.Stop();$script:dockTransition=$true
 try{
  $script:dockSide='';$script:dockExpanded=$false;$script:dockWindow.Hide()
  $window.Left=$Left;$window.Top=$Top;$window.Show()
  $script:isHovered=$window.IsMouseOver;$ui.Tools.Opacity=if($script:isHovered){1}else{0}
  Apply-Opacity
 }finally{$script:dockTransition=$false}
 Save-Settings
}
function Move-FloatingCard{
 $script:dockCollapse.Stop();$script:windowMoving=$true
 try{
  $script:dockSide='';$script:dockExpanded=$false;$script:dockWindow.Hide()
  $window.DragMove()
 }finally{$script:windowMoving=$false}
 $area=Get-ScreenArea ($window.Left+$window.ActualWidth/2) ($window.Top+$window.ActualHeight/2)
 $content=[Windows.Rect]::new($window.Left+14,$window.Top+14,$window.ActualWidth-28,$window.ActualHeight-28)
 $side=Get-SnapSide $content $area
 if($side){Set-Docked $side $area ($window.Top+($window.ActualHeight-104)/2)}else{Save-Settings}
}
$script:dockWindow.Add_MouseEnter({
 if(!$script:dockTransition){Expand-Docked}
})
$script:dockWindow.Add_MouseLeave({Schedule-DockCollapse})
$script:dockWindow.Add_MouseLeftButtonDown({
 $script:dockCollapse.Stop();$script:windowMoving=$true
 try{
  $window.Hide()
  $script:dockWindow.DragMove()
 }finally{$script:windowMoving=$false}
 $left=$script:dockWindow.Left;$top=$script:dockWindow.Top
 $area=Get-ScreenArea ($left+10) ($top+52)
 $side=Get-SnapSide ([Windows.Rect]::new($left,$top,20,104)) $area
 if($side){Set-Docked $side $area $top}else{Undock-Widget ($left-14) ($top-14)}
})