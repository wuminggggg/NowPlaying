#requires -Version 5.1
param([switch]$SelfTest,[string]$Preview,[string]$SettingsPath)
$ErrorActionPreference='Stop'
$script:singleMutex=$null
$script:wakeEvent=$null
if(!$SelfTest -and !$Preview){
 $script:singleMutex=[Threading.Mutex]::new($false,'Local\CodexNowPlaying')
 $owned=$false
 try{$owned=$script:singleMutex.WaitOne(0)}catch [Threading.AbandonedMutexException]{$owned=$true}
 if(!$owned){
  try{$wake=[Threading.EventWaitHandle]::OpenExisting('Local\CodexNowPlayingWake');[void]$wake.Set();$wake.Dispose()}catch{}
  $script:singleMutex.Dispose()
  exit 0
 }
 $script:wakeEvent=[Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::AutoReset,'Local\CodexNowPlayingWake')
}
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase,System.Windows.Forms,System.Runtime.WindowsRuntime
Add-Type -Path (Join-Path $PSScriptRoot 'GlobalShortcuts.cs') -ReferencedAssemblies System.Windows.Forms
$script:configPath=if($SettingsPath){$SettingsPath}else{Join-Path $PSScriptRoot 'settings.json'}
$script:bindings=@{Toggle='Ctrl+Alt+Space';Previous='Ctrl+Alt+Left';Next='Ctrl+Alt+Right';Stop='Ctrl+Alt+Down';Visibility='Ctrl+Alt+Up'}
$script:shortcutsDisabled=$false;
$script:opacity=0.88;$script:hoverOpacity=0.95;$script:isHovered=$false;$script:position=$null
if(Test-Path $configPath){try{
 $saved=Get-Content $configPath -Raw|ConvertFrom-Json
 if($null -ne $saved.shortcutsDisabled){$script:shortcutsDisabled=[bool]$saved.shortcutsDisabled}
 foreach($p in $saved.bindings.PSObject.Properties){$script:bindings[$p.Name]=[string]$p.Value}
 if($saved.opacity -ge 0.1 -and $saved.opacity -le 1){$script:opacity=[double]$saved.opacity}
 if($saved.hoverOpacity -ge 0.1 -and $saved.hoverOpacity -le 1){$script:hoverOpacity=[double]$saved.hoverOpacity}
 if($null -ne $saved.left -and $null -ne $saved.top){$script:position=$saved}
}catch{}}
function Save-Settings{
 if($SelfTest -or $Preview){return}
 @{bindings=$script:bindings;shortcutsDisabled=$script:shortcutsDisabled;opacity=$script:opacity;hoverOpacity=$script:hoverOpacity;dockSide=$script:dockSide;dockLeft=$script:dockWindow.Left;dockTop=$script:dockWindow.Top;left=$window.Left;top=$window.Top}|ConvertTo-Json|Set-Content -LiteralPath $configPath -Encoding UTF8
}
$script:taskMethod=[System.WindowsRuntimeSystemExtensions].GetMethods()|Where-Object{
 $_.Name -eq 'AsTask' -and $_.IsGenericMethod -and $_.GetGenericArguments().Count -eq 1 -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq ('IAsyncOperation'+[char]96+'1')
}|Select-Object -First 1
function Media-Task($op,[type]$type){return $script:taskMethod.MakeGenericMethod($type).Invoke($null,@($op))}
$managerType=[Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager,Windows.Media.Control,ContentType=WindowsRuntime]
$script:propertiesType=[Windows.Media.Control.GlobalSystemMediaTransportControlsSessionMediaProperties,Windows.Media.Control,ContentType=WindowsRuntime]
$script:managerTask=Media-Task ($managerType::RequestAsync()) $managerType
$script:manager=$null;$script:session=$null;$script:sessionId='';$script:chosen=''
$script:info=$null;$script:timeline=$null;$script:mediaTask=$null;$script:mediaTaskId='';$script:coverTask=$null;$script:coverTaskId=''
$script:lastMetadata=[datetime]::MinValue
$script:pending=[Collections.Generic.List[object]]::new()
$script:dragging=$false;$script:seekId='';$script:polls=0
$script:settingsWindow=$null;$script:recordAction='';$script:recordUntil=[datetime]::MinValue
$script:recordLabel=$null;$script:bindingLabels=@{};$script:hooks=$null;$script:lastError=''
[xml]$xaml=@"
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
 Width="344" ShowActivated="False" SizeToContent="Height" Title="Now Playing" WindowStyle="None" ResizeMode="NoResize" AllowsTransparency="True"
 Background="Transparent" Topmost="True" ShowInTaskbar="False" UseLayoutRounding="True" SnapsToDevicePixels="True" FontFamily="Segoe UI" Foreground="#25252B">
 <Window.Resources><Style TargetType="Button">
 <Setter Property="Background" Value="Transparent"/><Setter Property="Foreground" Value="#303039"/>
 <Setter Property="BorderThickness" Value="0"/><Setter Property="Cursor" Value="Hand"/><Setter Property="Padding" Value="9,7"/>
 <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button">
 <Border x:Name="B" CornerRadius="12" Background="{TemplateBinding Background}" Padding="{TemplateBinding Padding}">
 <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/></Border>
 <ControlTemplate.Triggers>
 <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="B" Property="Background" Value="#14000000"/></Trigger>
 <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.25"/></Trigger>
 </ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style> <Style TargetType="Slider">
 <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Slider">
 <Grid Background="Transparent" Height="18">
 <Track x:Name="PART_Track" Height="18">
 <Track.DecreaseRepeatButton><RepeatButton Command="Slider.DecreaseLarge">
 <RepeatButton.Template><ControlTemplate TargetType="RepeatButton"><Border Height="3" CornerRadius="2" Background="#7D7D87"/></ControlTemplate></RepeatButton.Template>
 </RepeatButton></Track.DecreaseRepeatButton>
 <Track.IncreaseRepeatButton><RepeatButton Command="Slider.IncreaseLarge">
 <RepeatButton.Template><ControlTemplate TargetType="RepeatButton"><Border Height="3" CornerRadius="2" Background="#25000000"/></ControlTemplate></RepeatButton.Template>
 </RepeatButton></Track.IncreaseRepeatButton>
 <Track.Thumb><Thumb Width="8" Height="18"><Thumb.Template><ControlTemplate TargetType="Thumb">
 <Ellipse Width="8" Height="8" Fill="#7D7D87"/></ControlTemplate></Thumb.Template></Thumb></Track.Thumb>
 </Track></Grid></ControlTemplate></Setter.Value></Setter></Style></Window.Resources>
 <Border x:Name="Card" CornerRadius="22" Background="#E0F5F5F8" BorderThickness="1" BorderBrush="#CFFFFFFF" Margin="14" Padding="20,12,20,15">
 <Border.Effect><DropShadowEffect BlurRadius="22" ShadowDepth="5" Opacity="0.17" Color="#353545"/></Border.Effect>
 <StackPanel>
 <Grid x:Name="Header" Height="28" Background="Transparent">
 <StackPanel x:Name="Tools" Orientation="Horizontal" HorizontalAlignment="Right" Opacity="0">
 <Button x:Name="Settings" FontFamily="Segoe MDL2 Assets" Content="&#xE713;" FontSize="14" Width="28" Height="28" Padding="0" ToolTip="设置"/>
 <Button x:Name="Close" FontFamily="Segoe MDL2 Assets" Content="&#xE8BB;" FontSize="13" Width="28" Height="28" Padding="0" ToolTip="收起"/>
 </StackPanel></Grid>
 <Grid x:Name="Metadata" Background="Transparent" Margin="0,4,0,15">
 <Grid.ColumnDefinitions><ColumnDefinition Width="60"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
 <Border x:Name="Cover" CornerRadius="12" Width="48" Height="48" HorizontalAlignment="Left" Background="#15151528">
 <TextBlock x:Name="CoverIcon" Text="&#xE8D6;" FontFamily="Segoe MDL2 Assets" FontSize="23" Foreground="#85858F" VerticalAlignment="Center" HorizontalAlignment="Center"/>
 </Border>
 <StackPanel Grid.Column="1" VerticalAlignment="Center">
 <TextBlock x:Name="Title" Text="暂无播放" FontSize="16" FontWeight="SemiBold" TextTrimming="CharacterEllipsis"/>
 <TextBlock x:Name="Artist" FontSize="12" Foreground="#797982" Margin="0,4,0,0" TextTrimming="CharacterEllipsis"/>
 </StackPanel></Grid>
 <StackPanel x:Name="Progress" Visibility="Collapsed" Margin="0,0,0,7">
 <Slider x:Name="Seek" Minimum="0" Maximum="1" IsMoveToPointEnabled="True" SmallChange="5" LargeChange="10" Height="18"/>
 <Grid><TextBlock x:Name="Elapsed" FontSize="10" Foreground="#8B8B95"/><TextBlock x:Name="Duration" FontSize="10" Foreground="#8B8B95" HorizontalAlignment="Right"/></Grid>
 </StackPanel>
 <Grid Height="42"><StackPanel Orientation="Horizontal" HorizontalAlignment="Center">
 <Button x:Name="Previous" Content="&#xE892;" FontFamily="Segoe MDL2 Assets" FontSize="19" Width="52" IsEnabled="False" ToolTip="上一首"/>
 <Button x:Name="Toggle" Content="&#xE768;" FontFamily="Segoe MDL2 Assets" FontSize="24" Width="58" Background="#11000000" IsEnabled="False" ToolTip="播放 / 暂停"/>
 <Button x:Name="Next" Content="&#xE893;" FontFamily="Segoe MDL2 Assets" FontSize="19" Width="52" IsEnabled="False" ToolTip="下一首"/>
 </StackPanel><Button x:Name="Stop" Content="&#xE71A;" FontFamily="Segoe MDL2 Assets" FontSize="12" Width="31" HorizontalAlignment="Right" IsEnabled="False" ToolTip="停止"/></Grid>
 </StackPanel></Border></Window>
"@
$window=[Windows.Markup.XamlReader]::Load([Xml.XmlNodeReader]::new($xaml))
$window.Add_SourceInitialized({
 $handle=[Windows.Interop.WindowInteropHelper]::new($window).Handle
 [FloatingWindowChrome]::Apply($handle)
})
$ui=@{}
foreach($n in @('Card','Header','Tools','Settings','Close','Metadata','Cover','CoverIcon','Title','Artist','Progress','Seek','Elapsed','Duration','Previous','Toggle','Next','Stop')){$ui[$n]=$window.FindName($n)}
. (Join-Path $PSScriptRoot 'Docking.ps1')
function Apply-Opacity{
 $effective=if($script:isHovered){$script:hoverOpacity}else{$script:opacity}
 $ui.Card.Background=[Windows.Media.SolidColorBrush]::new([Windows.Media.Color]::FromRgb(245,245,248))
 $window.Opacity=$effective
 $script:dockWindow.Opacity=$effective
}
Apply-Opacity
function Time-Text([double]$seconds){
 $t=[timespan]::FromSeconds([math]::Max(0,$seconds))
 if($t.TotalHours -ge 1){return '{0}:{1:00}:{2:00}' -f [int][math]::Floor($t.TotalHours),$t.Minutes,$t.Seconds}
 return '{0}:{1:00}' -f [int][math]::Floor($t.TotalMinutes),$t.Seconds
}
function Control-Media([string]$action){
 if($action -eq 'Visibility'){Toggle-Widget;return}
 try{Update-Media}catch{return}
 if(!$script:session -or !$script:info){return}
 try{
 $c=$script:info.Controls
 $op=switch($action){
 'Previous'{if($c.IsPreviousEnabled){$script:session.TrySkipPreviousAsync()}}
 'Next'{if($c.IsNextEnabled){$script:session.TrySkipNextAsync()}}
 'Stop'{if($c.IsStopEnabled){$script:session.TryStopAsync()}}
 'Toggle'{
 if($script:info.PlaybackStatus.ToString() -eq 'Playing'){if($c.IsPauseEnabled){$script:session.TryPauseAsync()}}
 elseif($c.IsPlayEnabled){$script:session.TryPlayAsync()}
 }}
 if($op){$script:pending.Add((Media-Task $op ([bool])))}
 }catch{$script:lastError=$_.Exception.Message}
}
function Commit-Seek{
 try{Update-Media}catch{return}
 if(!$script:session -or !$script:timeline -or $script:seekId -ne $script:sessionId){return}
 try{
 $t=$script:timeline;$ticks=$t.StartTime.Ticks+[long]($ui.Seek.Value*[timespan]::TicksPerSecond)
 $ticks=[math]::Max($t.MinSeekTime.Ticks,[math]::Min($t.MaxSeekTime.Ticks,$ticks))
 $script:pending.Add((Media-Task ($script:session.TryChangePlaybackPositionAsync($ticks)) ([bool])))
 }catch{$script:lastError=$_.Exception.Message}
}
$script:playerStates=@{}
$script:playerSequence=0L
$script:latestPlayerId=''
function Select-LatestPlayerId([object[]]$Players){
 $live=@{}
 $started=@()
 foreach($player in $Players){
  $live[$player.Id]=$player
  if(!$script:playerStates.ContainsKey($player.Id)){
   $script:playerStates[$player.Id]=[pscustomobject]@{State='';Order=0L}
  }
  $previous=$script:playerStates[$player.Id]
  if($player.State -eq 'Playing' -and $previous.State -ne 'Playing'){$started+=$player}
  $previous.State=$player.State
 }
 foreach($id in @($script:playerStates.Keys)){
  if(!$live.ContainsKey($id)){$script:playerStates.Remove($id)}
 }
 foreach($player in @($started | Sort-Object Updated,Id)){
  $script:playerSequence++
  $script:playerStates[$player.Id].Order=$script:playerSequence
  $script:latestPlayerId=$player.Id
 }
 if(!$live.ContainsKey($script:latestPlayerId)){
  $fallback=$Players | Sort-Object @{Expression={$script:playerStates[$_.Id].Order};Descending=$true},@{Expression={$_.Updated};Descending=$true} | Select-Object -First 1
  $script:latestPlayerId=if($fallback){$fallback.Id}else{''}
 }
 return $script:latestPlayerId
}
function Select-MediaSession([object[]]$Sessions){
 $players=@{}
 foreach($candidate in $Sessions){
  try{
   $id=$candidate.SourceAppUserModelId
   $state=$candidate.GetPlaybackInfo().PlaybackStatus.ToString()
   $updated=$candidate.GetTimelineProperties().LastUpdatedTime
   $entry=[pscustomobject]@{Id=$id;State=$state;Updated=$updated;Session=$candidate}
   $current=$players[$id]
   if(!$current -or ($state -eq 'Playing' -and $current.State -ne 'Playing') -or
    (($state -eq 'Playing') -eq ($current.State -eq 'Playing') -and $updated -gt $current.Updated)){
    $players[$id]=$entry
   }
  }catch{}
 }
 $latest=Select-LatestPlayerId @($players.Values)
 if($script:chosen){$latest=$script:chosen}
 if($players.ContainsKey($latest)){return $players[$latest].Session}
 return $null
}
function Update-Media{
 if(!$script:manager){
 if(!$script:managerTask.IsCompleted){return}
 $script:manager=$script:managerTask.GetAwaiter().GetResult()
 }
 for($i=$script:pending.Count-1;$i -ge 0;$i--){
 if($script:pending[$i].IsCompleted){
 try{if(!$script:pending[$i].GetAwaiter().GetResult()){$script:lastError='播放器未接受此操作'}}catch{$script:lastError=$_.Exception.Message}
 $script:pending.RemoveAt($i)
 }}
 $sessions=@($script:manager.GetSessions())
 $s=Select-MediaSession $sessions

 $id=if($s){$s.SourceAppUserModelId}else{''}
 if($id -ne $script:sessionId){$script:lastMetadata=[datetime]::MinValue;$ui.Title.Text='';$ui.Artist.Text='';$script:dragging=$false;$ui.Cover.Background='#15151528';$ui.CoverIcon.Visibility='Visible'}
 $script:session=$s;$script:sessionId=$id
 if(!$s){
 $ui.Title.Text='暂无播放';$ui.Artist.Text='';$ui.Cover.Background='#15151528';$ui.CoverIcon.Visibility='Visible';$ui.Progress.Visibility='Collapsed'
 foreach($n in @('Previous','Toggle','Next','Stop')){$ui[$n].IsEnabled=$false}
 return
 }
 $script:info=$s.GetPlaybackInfo();$script:timeline=$s.GetTimelineProperties()
 $c=$script:info.Controls;$playing=$script:info.PlaybackStatus.ToString() -eq 'Playing'
 $ui.Previous.IsEnabled=$c.IsPreviousEnabled;$ui.Next.IsEnabled=$c.IsNextEnabled;$ui.Stop.IsEnabled=$c.IsStopEnabled
 $ui.Toggle.IsEnabled=if($playing){$c.IsPauseEnabled}else{$c.IsPlayEnabled}
 $ui.Toggle.Content=if($playing){[string][char]0xE769}else{[string][char]0xE768}
 if($script:mediaTask -and $script:mediaTask.IsCompleted){
 try{$p=$script:mediaTask.GetAwaiter().GetResult()
 if($script:mediaTaskId -eq $id){
 $ui.Title.Text=if($p.Title){$p.Title}else{'未命名'};$ui.Title.ToolTip=$ui.Title.Text
 $ui.Artist.Text=if($p.Artist){$p.Artist}else{$p.AlbumTitle};$ui.Artist.ToolTip=$ui.Artist.Text
 if($p.Thumbnail -and !$script:coverTask){
 $script:coverTask=Media-Task ($p.Thumbnail.OpenReadAsync()) ([Windows.Storage.Streams.IRandomAccessStreamWithContentType,Windows.Storage.Streams,ContentType=WindowsRuntime])
 $script:coverTaskId=$id
 }
 }}catch{$script:lastError=$_.Exception.Message}
 $script:mediaTask=$null
 }
 if(!$script:mediaTask -and ([datetime]::UtcNow-$script:lastMetadata).TotalSeconds -ge 2){
 $script:mediaTaskId=$id;$script:mediaTask=Media-Task ($s.TryGetMediaPropertiesAsync()) $script:propertiesType
 $script:lastMetadata=[datetime]::UtcNow
 }
 if($script:coverTask -and $script:coverTask.IsCompleted){
 try{
  $randomStream=$script:coverTask.GetAwaiter().GetResult()
  try{
   if($script:coverTaskId -eq $id){
    $stream=[IO.MemoryStream]::new([CoverStream]::Read($randomStream))
    try{
     $image=[Windows.Media.Imaging.BitmapImage]::new();$image.BeginInit()
     $image.CacheOption='OnLoad';$image.StreamSource=$stream;$image.EndInit();$image.Freeze()
     $brush=[Windows.Media.ImageBrush]::new($image);$brush.Stretch='UniformToFill'
     $ui.Cover.Background=$brush;$ui.CoverIcon.Visibility='Collapsed'
    }finally{$stream.Dispose()}
   }
  }finally{$randomStream=$null}
 }catch{$script:lastError=$_.Exception.Message}
 $script:coverTask=$null
 }
 $t=$script:timeline;$length=($t.EndTime-$t.StartTime).TotalSeconds
 $pos=($t.Position-$t.StartTime).TotalSeconds
 if($playing -and $null -ne $script:info.PlaybackRate){
 $age=([datetimeoffset]::UtcNow-$t.LastUpdatedTime).TotalSeconds;$pos += [math]::Max(0,$age)*$script:info.PlaybackRate
 }
 Update-DockProgress $pos $length
 $canSeek=$c.IsPlaybackPositionEnabled -and $length -gt 0 -and $t.MaxSeekTime -gt $t.MinSeekTime
 $ui.Progress.Visibility=if($canSeek){'Visible'}else{'Collapsed'}
 if($canSeek -and !$script:dragging){
 $ui.Seek.Maximum=$length;$ui.Seek.Value=[math]::Max(0,[math]::Min($length,$pos))
 $ui.Elapsed.Text=Time-Text $ui.Seek.Value;$ui.Duration.Text=Time-Text $length
 }
}
function Sync-Shortcuts{
 $script:hooks.Enabled=!$script:shortcutsDisabled
 $script:hooks.Bindings.Clear()
 foreach($action in $script:bindings.Keys){if($script:bindings[$action]){$script:hooks.Bindings[$script:bindings[$action]]=$action}}
}
function Open-Settings{
 if($script:settingsWindow){[void]$script:settingsWindow.Activate();return}
 $sw=[Windows.Window]::new();$script:settingsWindow=$sw
 $sw.Title='设置';$sw.ShowInTaskbar=$false;$sw.Width=450;$sw.SizeToContent='Height';$sw.ResizeMode='NoResize';$sw.WindowStartupLocation='CenterScreen';$sw.Topmost=$true;$sw.Background='#F5F5F7';$sw.FontFamily='Microsoft YaHei UI'
 $sw.Icon=[Windows.Media.Imaging.BitmapFrame]::Create([Uri]::new((Join-Path $PSScriptRoot 'app.ico'),[UriKind]::Absolute))
 $panel=[Windows.Controls.StackPanel]::new();$panel.Margin='22';$sw.Content=$panel
 $heading=[Windows.Controls.TextBlock]::new();$heading.Text='快捷键';$heading.FontSize=20;$heading.FontWeight='SemiBold';$heading.Margin='0,0,0,14';[void]$panel.Children.Add($heading)
 $names=@{Toggle='播放 / 暂停';Previous='上一首';Next='下一首';Stop='停止';Visibility='显示 / 隐藏'}
 foreach($action in @('Toggle','Previous','Next','Stop','Visibility')){
 $row=[Windows.Controls.Grid]::new();$row.Margin='0,5'
 foreach($width in @('105','*','58','42')){$col=[Windows.Controls.ColumnDefinition]::new();$col.Width=$width;[void]$row.ColumnDefinitions.Add($col)}
 $name=[Windows.Controls.TextBlock]::new();$name.Text=$names[$action];$name.VerticalAlignment='Center';[void]$row.Children.Add($name)
 $label=[Windows.Controls.TextBox]::new();$label.Text=$script:bindings[$action];$label.IsReadOnly=$true;$label.VerticalContentAlignment='Center';$label.FontSize=11
 [Windows.Controls.Grid]::SetColumn($label,1);[void]$row.Children.Add($label);$script:bindingLabels[$action]=$label
 $record=[Windows.Controls.Button]::new();$record.Content='录入';$record.Margin='5,0,0,0';$record.Height=30;$record.Tag=$action
 $record.Add_Click({
 if($script:recordLabel){$script:recordLabel.Text=$script:bindings[$script:recordAction]}
 $script:recordAction=$this.Tag;$script:recordLabel=$script:bindingLabels[$this.Tag];$script:recordLabel.Text='按下快捷键…'
 $script:hooks.Recording=$true;$script:recordUntil=[datetime]::UtcNow.AddSeconds(10)
 })
 [Windows.Controls.Grid]::SetColumn($record,2);[void]$row.Children.Add($record)
 $clear=[Windows.Controls.Button]::new();$clear.Content='清除';$clear.Tag=$action;$clear.Margin='5,0,0,0'
 $clear.Add_Click({
 $script:hooks.Recording=$false
 if($script:recordLabel){$script:recordLabel.Text=$script:bindings[$script:recordAction];$script:recordLabel=$null}
 $script:bindings[$this.Tag]='';$script:bindingLabels[$this.Tag].Text='';Sync-Shortcuts;Save-Settings
 })
 [Windows.Controls.Grid]::SetColumn($clear,3);[void]$row.Children.Add($clear);[void]$panel.Children.Add($row)
 }
 $sl=[Windows.Controls.TextBlock]::new();$sl.Text='播放器';$sl.Margin='0,14,0,6';[void]$panel.Children.Add($sl)
 $sources=[Windows.Controls.ComboBox]::new();$sources.DisplayMemberPath='Label'
 [void]$sources.Items.Add([pscustomobject]@{Label='最近播放';Id=''})
 if($script:manager){foreach($s in $script:manager.GetSessions()){[void]$sources.Items.Add([pscustomobject]@{Label=$s.SourceAppUserModelId;Id=$s.SourceAppUserModelId})}}
 $sources.SelectedIndex=0
 for($i=0;$i -lt $sources.Items.Count;$i++){if($sources.Items[$i].Id -eq $script:chosen){$sources.SelectedIndex=$i}}
 $sources.Add_SelectionChanged({$script:chosen=$this.SelectedItem.Id;$script:lastMetadata=[datetime]::MinValue});[void]$panel.Children.Add($sources)
 $ol=[Windows.Controls.TextBlock]::new();$ol.Text='常态不透明度（%）';$ol.Margin='0,18,0,6';[void]$panel.Children.Add($ol)
 $orow=[Windows.Controls.Grid]::new()
 $col=[Windows.Controls.ColumnDefinition]::new();$col.Width='*';[void]$orow.ColumnDefinitions.Add($col)
 $col=[Windows.Controls.ColumnDefinition]::new();$col.Width='64';[void]$orow.ColumnDefinitions.Add($col)
 $os=[Windows.Controls.Slider]::new();$script:opacitySlider=$os
 $os.Minimum=10;$os.Maximum=100;$os.TickFrequency=1;$os.IsSnapToTickEnabled=$true;$os.Value=100*$script:opacity;$os.VerticalAlignment='Center'
 $percent=[Windows.Controls.TextBox]::new();$script:opacityPercent=$percent;$percent.Text=[int]$os.Value;$percent.Margin='12,0,0,0';$percent.Height=28;$percent.VerticalContentAlignment='Center'
 $percent.ToolTip='10–100（百分比）'
 $os.Add_ValueChanged({
 $script:opacity=$this.Value/100
 $script:opacityPercent.Text=[string][int]$this.Value
 Apply-Opacity;Save-Settings
 })
 $percent.Add_LostKeyboardFocus({
 $value=0
 if([int]::TryParse($this.Text,[ref]$value)){
 $script:opacitySlider.Value=[math]::Max(10,[math]::Min(100,$value))
 }
 $this.Text=[string][int]$script:opacitySlider.Value
 })
 $percent.Add_KeyDown({
 if($_.Key -eq 'Return'){
 $value=0
 if([int]::TryParse($this.Text,[ref]$value)){$script:opacitySlider.Value=[math]::Max(10,[math]::Min(100,$value))}
 $this.Text=[string][int]$script:opacitySlider.Value
 }
 })
 [void]$orow.Children.Add($os);[Windows.Controls.Grid]::SetColumn($percent,1);[void]$orow.Children.Add($percent)
 [void]$panel.Children.Add($orow)
 $hl=[Windows.Controls.TextBlock]::new();$hl.Text='鼠标悬停不透明度（%）';$hl.Margin='0,16,0,6';[void]$panel.Children.Add($hl)
 $hrow=[Windows.Controls.Grid]::new()
 $hcol=[Windows.Controls.ColumnDefinition]::new();$hcol.Width='*';[void]$hrow.ColumnDefinitions.Add($hcol)
 $hcol=[Windows.Controls.ColumnDefinition]::new();$hcol.Width='64';[void]$hrow.ColumnDefinitions.Add($hcol)
 $hs=[Windows.Controls.Slider]::new();$script:hoverSlider=$hs
 $hs.Minimum=10;$hs.Maximum=100;$hs.TickFrequency=1;$hs.IsSnapToTickEnabled=$true;$hs.Value=100*$script:hoverOpacity;$hs.VerticalAlignment='Center'
 $hp=[Windows.Controls.TextBox]::new();$script:hoverPercent=$hp;$hp.Text=[string][int]$hs.Value;$hp.Margin='12,0,0,0';$hp.Height=28;$hp.VerticalContentAlignment='Center'
 $hs.Add_ValueChanged({$script:hoverOpacity=$this.Value/100;$script:hoverPercent.Text=[string][int]$this.Value;Apply-Opacity;Save-Settings})
 $hp.Add_LostKeyboardFocus({
  $value=0
  if([int]::TryParse($this.Text,[ref]$value)){$script:hoverSlider.Value=[math]::Max(10,[math]::Min(100,$value))}
  $this.Text=[string][int]$script:hoverSlider.Value
 })
 $hp.Add_KeyDown({
  if($_.Key -eq 'Return'){
   $value=0
   if([int]::TryParse($this.Text,[ref]$value)){$script:hoverSlider.Value=[math]::Max(10,[math]::Min(100,$value))}
   $this.Text=[string][int]$script:hoverSlider.Value
  }
 })
 [void]$hrow.Children.Add($hs);[Windows.Controls.Grid]::SetColumn($hp,1);[void]$hrow.Children.Add($hp);[void]$panel.Children.Add($hrow)
 $pin=[Windows.Controls.CheckBox]::new();$pin.Content='保持置顶';$pin.IsChecked=$window.Topmost;$pin.Margin='0,14,0,0'
 $pin.Add_Click({$window.Topmost=[bool]$this.IsChecked;$script:dockWindow.Topmost=$window.Topmost});[void]$panel.Children.Add($pin)
 $sw.Add_Closed({Schedule-DockCollapse;$script:hooks.Recording=$false;$script:settingsWindow=$null;$script:recordLabel=$null;Save-Settings})
 $sw.Show()
}
$script:tray=$null
$script:startupRegistryPath='Software\Microsoft\Windows\CurrentVersion\Run'
function Get-AutoStartCommand{
 $launcher=[Environment]::GetEnvironmentVariable('NOWPLAYING_LAUNCHER')
 if($launcher -and [IO.File]::Exists($launcher)){return '"'+$launcher+'"'}
 $entry=Join-Path $PSScriptRoot 'Start.vbs'
 if(![IO.File]::Exists($entry)){throw '找不到启动入口'}
 $hostPath=Join-Path ([Environment]::GetFolderPath('System')) 'wscript.exe'
 return '"'+$hostPath+'" //B //Nologo "'+$entry+'"'
}
function Test-AutoStart{
 $key=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($script:startupRegistryPath)
 if(!$key){return $false}
 try{return ![string]::IsNullOrWhiteSpace([string]$key.GetValue('NowPlaying'))}finally{$key.Dispose()}
}
function Set-AutoStart([bool]$Enabled){
 if($Enabled){
  $command=Get-AutoStartCommand
  $key=[Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($script:startupRegistryPath)
  try{$key.SetValue('NowPlaying',$command,[Microsoft.Win32.RegistryValueKind]::String)}finally{$key.Dispose()}
 }else{
  $key=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($script:startupRegistryPath,$true)
  if($key){try{$key.DeleteValue('NowPlaying',$false)}finally{$key.Dispose()}}
 }
}
function Create-Tray {
 $script:tray=[Windows.Forms.NotifyIcon]::new()
 $script:tray.Icon=[Drawing.Icon]::new((Join-Path $PSScriptRoot 'app.ico'))
 $script:tray.Text='Now Playing'
 $menu=[Windows.Forms.ContextMenuStrip]::new()
 $show=$menu.Items.Add('显示 / 隐藏');$show.Add_Click({Control-Media 'Visibility'})
 $settings=$menu.Items.Add('设置');$settings.Add_Click({Open-Settings})
 $opacityMenu=[Windows.Forms.ToolStripMenuItem]::new('透明度')
 foreach($v in @(10,25,50,70,85,100)){
  $item=[Windows.Forms.ToolStripMenuItem]::new(('{0}%' -f $v));$item.Tag=$v
  $item.Add_Click({
   $script:opacity=[int]$this.Tag/100;Apply-Opacity
   if($script:settingsWindow){$script:opacitySlider.Value=[int]$this.Tag}
   Save-Settings
  })
  [void]$opacityMenu.DropDownItems.Add($item)
 }
 [void]$menu.Items.Add($opacityMenu)
 $disable=[Windows.Forms.ToolStripMenuItem]::new('禁用快捷键')
 $script:disableShortcutsItem=$disable
 $disable.CheckOnClick=$true
 $disable.Checked=$script:shortcutsDisabled
 $disable.Add_CheckedChanged({
  $script:shortcutsDisabled=$this.Checked
  $script:hooks.Enabled=!$script:shortcutsDisabled
  $discard=''
  while($script:hooks.Events.TryDequeue([ref]$discard)){}
  Save-Settings
 })
 [void]$menu.Items.Add($disable)
 $startup=[Windows.Forms.ToolStripMenuItem]::new('开机自启')
 $script:startupItem=$startup
 $startup.Checked=Test-AutoStart
 $startup.Add_Click({
  try{Set-AutoStart (!$this.Checked)}catch{$script:lastError=$_.Exception.Message}
  $this.Checked=Test-AutoStart
 })
 [void]$menu.Items.Add($startup)
 $menu.Add_Opening({$script:startupItem.Checked=Test-AutoStart})
 [void]$menu.Items.Add([Windows.Forms.ToolStripSeparator]::new())
 $exit=$menu.Items.Add('退出');$exit.Add_Click({$window.Close()})
 $script:tray.ContextMenuStrip=$menu
 $script:tray.Add_MouseDoubleClick({
 if($_.Button -eq [Windows.Forms.MouseButtons]::Left){Open-Settings}
})
 $script:tray.Visible=$true
}
$window.Add_MouseEnter({
 if($script:dockTransition){return}
 if($script:dockSide){Expand-Docked}else{$script:isHovered=$true;$ui.Tools.Opacity=1;Apply-Opacity}
})
$window.Add_MouseLeave({
 if($script:dockSide){Schedule-DockCollapse}else{$script:isHovered=$false;$ui.Tools.Opacity=0;Apply-Opacity}
})
$ui.Close.ToolTip='收起';$ui.Close.Add_Click({Hide-Widget});$ui.Settings.Add_Click({Open-Settings})
$dragHandler={
 $node=$_.OriginalSource
 while($node -and $node -ne $window){
 if($node -is [Windows.Controls.Button]){return}
 $node=[Windows.Media.VisualTreeHelper]::GetParent($node)
 }
 Move-FloatingCard
}
$ui.Header.Add_MouseLeftButtonDown($dragHandler);$ui.Metadata.Add_MouseLeftButtonDown($dragHandler)
$ui.Previous.Add_Click({Control-Media 'Previous'});$ui.Toggle.Add_Click({Control-Media 'Toggle'})
$ui.Next.Add_Click({Control-Media 'Next'});$ui.Stop.Add_Click({Control-Media 'Stop'})
$ui.Seek.Add_PreviewMouseLeftButtonDown({$script:dragging=$true;$script:seekId=$script:sessionId})
$ui.Seek.Add_PreviewMouseLeftButtonUp({Commit-Seek;$script:dragging=$false;Schedule-DockCollapse})
$ui.Seek.Add_LostMouseCapture({$script:dragging=$false;Schedule-DockCollapse})
$ui.Seek.Add_PreviewKeyDown({$script:dragging=$true;$script:seekId=$script:sessionId})
$ui.Seek.Add_KeyUp({Commit-Seek;$script:dragging=$false})
$ui.Seek.Add_ValueChanged({$ui.Elapsed.Text=Time-Text $ui.Seek.Value})
$window.Add_ContentRendered({
 if($script:positionInitialized){return};$script:positionInitialized=$true
 $area=[Windows.SystemParameters]::WorkArea
 if($script:position){
  $area=Get-ScreenArea ([double]$script:position.left+$window.ActualWidth/2) ([double]$script:position.top+$window.ActualHeight/2)
  $window.Left=[math]::Max($area.Left,[math]::Min($area.Right-$window.ActualWidth,[double]$script:position.left))
  $window.Top=[math]::Max($area.Top,[math]::Min($area.Bottom-$window.ActualHeight,[double]$script:position.top))
 }else{$window.Left=$area.Right-$window.ActualWidth-20;$window.Top=$area.Bottom-$window.ActualHeight-20}
 if($script:dockRestore){
  $restore=$script:dockRestore
  $work=Get-ScreenArea ([double]$restore.dockLeft+10) ([double]$restore.dockTop+52)
  Set-Docked $restore.dockSide $work ([double]$restore.dockTop)
  $script:dockRestore=$null
 }
})
$timer=[Windows.Threading.DispatcherTimer]::new();$timer.Interval=[timespan]::FromMilliseconds(100)
$timer.Add_Tick({
 try{
 if($script:wakeEvent -and $script:wakeEvent.WaitOne(0)){Show-Widget}
 $eventName=''
 while($script:hooks.Events.TryDequeue([ref]$eventName)){
 if($eventName.StartsWith('capture:')){
 $chord=$eventName.Substring(8)
 foreach($a in @($script:bindings.Keys)){
 if($script:bindings[$a] -eq $chord){$script:bindings[$a]='';if($script:bindingLabels[$a]){$script:bindingLabels[$a].Text=''}}
 }
 $script:bindings[$script:recordAction]=$chord;$script:recordLabel.Text=$chord;$script:recordLabel=$null
 Sync-Shortcuts;Save-Settings
 }elseif($eventName -eq 'cancel'){
 if($script:recordLabel){$script:recordLabel.Text=$script:bindings[$script:recordAction];$script:recordLabel=$null}
 }elseif(!$script:shortcutsDisabled){Control-Media $eventName}
 }
 if($script:hooks.Recording -and [datetime]::UtcNow -gt $script:recordUntil){
 $script:hooks.Recording=$false
 if($script:recordLabel){$script:recordLabel.Text=$script:bindings[$script:recordAction];$script:recordLabel=$null}
 }
 $script:polls++;if($script:polls % 5 -eq 0){Update-Media}
 }catch{$script:lastError=$_.Exception.Message}
})
$window.Add_Closed({
 $timer.Stop();$script:dockCollapse.Stop();if(!$script:application){$script:dockWindow.Close()};if($script:application){$script:application.Shutdown()};if($script:tray){$script:tray.Visible=$false;$script:tray.Icon.Dispose();$script:tray.Dispose()};if($script:settingsWindow){$script:settingsWindow.Close()}
 if($script:hooks){$script:hooks.Dispose()};Save-Settings
 if($script:wakeEvent){$script:wakeEvent.Dispose()};if($script:singleMutex){$script:singleMutex.ReleaseMutex();$script:singleMutex.Dispose()}
})
try{
 if($SelfTest){
 if(!$script:managerTask.Wait(10000)){throw '系统媒体服务响应超时'}
 $script:manager=$script:managerTask.GetAwaiter().GetResult();Update-Media
 if($script:mediaTask){[void]$script:mediaTask.Wait(5000);Update-Media}
  $script:hooks=[GlobalShortcuts]::new();Sync-Shortcuts
 $script:hooks.Bindings['Ctrl+Alt+Space']='Toggle'
 $dispatch=[GlobalShortcuts].GetMethod('Dispatch',[Reflection.BindingFlags]'Instance,NonPublic')
 $result=$dispatch.Invoke($script:hooks,@('Ctrl+Alt+Space',$false))
 $eventName=''
 if(!$result -or !$script:hooks.Events.TryDequeue([ref]$eventName) -or $eventName -ne 'Toggle'){throw '键盘快捷键匹配失败'}
 $script:hooks.Recording=$true
 [void]$dispatch.Invoke($script:hooks,@('Ctrl+MouseX1',$true))
 if(!$script:hooks.Events.TryDequeue([ref]$eventName) -or $eventName -ne 'capture:Ctrl+MouseX1'){throw '鼠标快捷键录入失败'}
 $window.ShowActivated=$false;$window.Show();$window.UpdateLayout()
 $handle=[Windows.Interop.WindowInteropHelper]::new($window).EnsureHandle()
 if(([FloatingWindowChrome]::Style($handle) -band 0x00CF0000) -ne 0){throw '仍存在原生标题栏或系统菜单样式'}
 $window.Measure([Windows.Size]::new(344,[double]::PositiveInfinity))
 $window.Arrange([Windows.Rect]::new([Windows.Point]::new(0,0),$window.DesiredSize))
 $window.UpdateLayout()
 foreach($name in @('Settings','Close')){
  $button=$ui[$name]
  if($button.ActualHeight -lt 28 -or $button.ActualWidth -lt 28 -or $ui.Header.ActualHeight -lt $button.ActualHeight){throw '顶部按钮尺寸不足'}
  $presenter=[Windows.Media.VisualTreeHelper]::GetChild($button,0)
  $presenter.Measure([Windows.Size]::new([double]::PositiveInfinity,[double]::PositiveInfinity))
  if($presenter.DesiredSize.Height -gt $button.ActualHeight){throw '顶部按钮内容被裁切'}
 }
 $script:isHovered=$false;Apply-Opacity
 $originalOpacity=$script:opacity
 Open-Settings
 $script:opacitySlider.Value=17
 if($window.Opacity -ne 0.17){throw '透明度未即时生效'}
 $script:isHovered=$true;Apply-Opacity
 if($window.Opacity -ne $script:hoverOpacity){throw '鼠标移入透明度切换失败'}
 $script:hoverSlider.Value=63
 if($window.Opacity -ne 0.63){throw '悬停固定值调整失败'}
 $script:opacitySlider.Value=24
 if($window.Opacity -ne 0.63){throw '悬停时常态值不应覆盖悬停值'}
 $script:isHovered=$false;Apply-Opacity
 if($window.Opacity -ne 0.24){throw '鼠标移出未恢复设定值'}
 $script:opacity=$originalOpacity;Apply-Opacity
 $script:settingsWindow.Close()
 $area=Get-ScreenArea ($window.Left+100) ($window.Top+100)
 $negativeArea=[Windows.Rect]::new(-1600,0,1600,900)
 if((Get-SnapSide ([Windows.Rect]::new(-1600,80,300,200)) $negativeArea) -ne 'Left'){throw '左侧屏幕吸附判定失败'}
 if((Get-SnapSide ([Windows.Rect]::new(-300,80,300,200)) $negativeArea) -ne 'Right'){throw '右侧屏幕吸附判定失败'}
 if(Get-SnapSide ([Windows.Rect]::new(-900,80,300,200)) $negativeArea){throw '中央区域不应吸附'}
 Set-Docked 'Left' $area ($area.Top+150)
 if($window.IsVisible -or !$script:dockWindow.IsVisible -or $script:dockWindow.Left -ne $area.Left){throw '左侧收起状态错误'}
 Update-DockProgress 25 100
 if($ui.DockFill.Height -ne 21 -or $ui.DockFill.VerticalAlignment -ne 'Bottom'){throw ('竖向进度方向错误: height='+$ui.DockFill.Height+' align='+$ui.DockFill.VerticalAlignment)}
 Update-DockProgress 200 100
 if($ui.DockFill.Height -ne 84){throw '竖向进度上界错误'}
 Update-DockProgress 0 0
 if($ui.DockTrack.Visibility -ne 'Collapsed'){throw '无时间信息时应隐藏进度'}
 Expand-Docked
 if(!$window.IsVisible -or !$script:dockExpanded -or $window.Opacity -ne $script:hoverOpacity){throw '悬停展开失败'}
 Collapse-Docked
 if($window.IsVisible -or !$script:dockWindow.IsVisible -or $script:dockWindow.Opacity -ne $script:opacity){throw '移开收回失败'}
 Set-Docked 'Right' $area ($area.Top+150)
 if($script:dockWindow.Left+20 -ne $area.Right){throw '右侧吸附位置错误'}
 Hide-Widget
 if($window.IsVisible -or $script:dockWindow.IsVisible){throw '托盘收起未隐藏全部窗口'}
 Show-Widget
 if($window.IsVisible -or !$script:dockWindow.IsVisible){throw '吸附模式恢复错误'}
 Undock-Widget ($area.Left+120) ($area.Top+120)
 if($script:dockSide -or $script:dockWindow.IsVisible -or !$window.IsVisible){throw '拖离取消吸附失败'}
 Create-Tray
 $originalDisabled=$script:shortcutsDisabled
 $script:disableShortcutsItem.Checked=$false
 if(!$script:hooks.Enabled){throw '未勾选时应启用快捷键'}
 $script:disableShortcutsItem.PerformClick()
 if(!$script:disableShortcutsItem.Checked -or $script:hooks.Enabled -or !$script:shortcutsDisabled){throw '勾选禁用未生效'}
 $script:hooks.Bindings['Ctrl+MouseX1']='Next'
 if($dispatch.Invoke($script:hooks,@('Ctrl+Alt+Space',$false))){throw '禁用时键盘快捷键被拦截'}
 if($dispatch.Invoke($script:hooks,@('Ctrl+MouseX1',$true))){throw '禁用时鼠标快捷键被拦截'}
 $eventName=''
 if($script:hooks.Events.TryDequeue([ref]$eventName)){throw '禁用时不应产生播放控制事件'}
 $script:disableShortcutsItem.PerformClick()
 if($script:disableShortcutsItem.Checked -or !$script:hooks.Enabled){throw '取消勾选未恢复快捷键'}
 if(!$dispatch.Invoke($script:hooks,@('Ctrl+Alt+Space',$false))){throw '重新启用后键盘匹配失败'}
 if(!$script:hooks.Events.TryDequeue([ref]$eventName) -or $eventName -ne 'Toggle'){throw '重新启用后操作事件错误'}
 $script:disableShortcutsItem.Checked=$originalDisabled
 if(!$script:tray.Visible -or $window.ShowInTaskbar){throw '系统托盘检查失败'}
 $mouseDoubleClick=[Windows.Forms.NotifyIcon].GetMethod('OnMouseDoubleClick',[Reflection.BindingFlags]'Instance,NonPublic')
 [void]$mouseDoubleClick.Invoke($script:tray,@([Windows.Forms.MouseEventArgs]::new([Windows.Forms.MouseButtons]::Right,2,0,0,0)))
 if($script:settingsWindow){throw '右键双击不应打开设置'}
 [void]$mouseDoubleClick.Invoke($script:tray,@([Windows.Forms.MouseEventArgs]::new([Windows.Forms.MouseButtons]::Left,2,0,0,0)))
 if(!$script:settingsWindow -or !$script:settingsWindow.IsVisible){throw '左键双击未打开设置'}
 if(!$script:settingsWindow.Icon -or $script:settingsWindow.Icon.Width -le 0){throw '设置窗口未加载工具图标'}
 if(@($script:settingsWindow.Content.Children | Where-Object {$_ -is [Windows.Controls.TextBlock] -and $_.Text -like '*支持组合键*'}).Count){throw '提示行未删除'}
 $script:settingsWindow.Close()
 $realStartupPath=$script:startupRegistryPath
 $testStartupPath='Software\NowPlaying\Tests\'+[guid]::NewGuid().ToString('N')
 try{
  $script:startupRegistryPath=$testStartupPath
  $script:startupItem.Checked=Test-AutoStart
  if($script:startupItem.Checked){throw '空测试位置不应启用自启'}
  $script:startupItem.PerformClick()
  if(!$script:startupItem.Checked -or !(Test-AutoStart)){throw '勾选开机自启未生效'}
  $key=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($testStartupPath)
  try{
   if($key.GetValue('NowPlaying') -ne (Get-AutoStartCommand)){throw '开机自启命令错误'}
  }finally{$key.Dispose()}
  $script:startupItem.PerformClick()
  if($script:startupItem.Checked -or (Test-AutoStart)){throw '取消勾选未移除自启'}
 }finally{
  [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($testStartupPath,$false)
  $script:startupRegistryPath=$realStartupPath
  $script:startupItem.Checked=Test-AutoStart
 }
 $script:tray.Dispose();$script:hooks.Dispose()
 if($script:coverTask){[void]$script:coverTask.Wait(5000);Update-Media}
 Write-Output ('PASS: XAML, WinRT, global hooks; sessions='+@($script:manager.GetSessions()).Count+'; title='+$ui.Title.Text)
 if($script:lastError){throw $script:lastError}
 }elseif($Preview){
 $ui.Title.Text='Midnight City';$ui.Artist.Text='M83';$ui.Progress.Visibility='Visible'
 $ui.Seek.Maximum=243;$ui.Seek.Value=86;$ui.Elapsed.Text='1:26';$ui.Duration.Text='4:03'
 foreach($n in @('Previous','Toggle','Next','Stop')){$ui[$n].IsEnabled=$true}
 $ui.Toggle.Content=[string][char]0xE769;$window.Show();$window.UpdateLayout();$script:isHovered=$true;$ui.Tools.Opacity=1;Apply-Opacity
 $bitmap=[Windows.Media.Imaging.RenderTargetBitmap]::new([int]$window.ActualWidth,[int]$window.ActualHeight,96,96,[Windows.Media.PixelFormats]::Pbgra32)
 $bitmap.Render($window)
 $encoder=[Windows.Media.Imaging.PngBitmapEncoder]::new();$encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
 $stream=[IO.File]::Create($Preview);$encoder.Save($stream);$stream.Dispose();$window.Close()
 Write-Output 'PASS: preview rendered'
 }else{
 $script:application=[Windows.Application]::new();$script:application.ShutdownMode='OnExplicitShutdown';
 $script:hooks=[GlobalShortcuts]::new();Sync-Shortcuts;Create-Tray;$timer.Start();[void]$script:application.Run($window)
 }
}catch{
 if($script:hooks){$script:hooks.Dispose()}
 if($SelfTest -or $Preview){throw}
 [Windows.MessageBox]::Show($_.Exception.Message,'Now Playing')|Out-Null;exit 1
}