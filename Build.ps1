#requires -Version 5.1
param([string]$OutputDirectory=(Join-Path $PSScriptRoot 'dist'))
$ErrorActionPreference='Stop'
$fx=Join-Path ([Environment]::GetFolderPath('Windows')) 'Microsoft.NET\Framework64\v4.0.30319'
if(![IO.Directory]::Exists($fx)){$fx=$fx.Replace('Framework64','Framework')}
$metadata=Get-ChildItem (Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Windows Kits\10\UnionMetadata') -Filter Windows.winmd -Recurse | Where-Object {$_.FullName -notlike '*\Facade\*'} | Sort-Object FullName -Descending | Select-Object -First 1
if(!$metadata){throw '构建需要 Windows 10 SDK 元数据'}
$runtime=Get-ChildItem (Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_MSIL\System.Runtime') -Filter System.Runtime.dll -Recurse|Select-Object -First 1
$tasks=Get-ChildItem (Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_MSIL\System.Threading.Tasks') -Filter System.Threading.Tasks.dll -Recurse|Select-Object -First 1
[void][IO.Directory]::CreateDirectory($OutputDirectory)
$output=Join-Path $OutputDirectory 'NowPlaying.exe'
$buildArgs=@('/nologo','/target:winexe','/platform:anycpu',('/out:{0}' -f $output),('/win32icon:{0}' -f (Join-Path $PSScriptRoot 'app.ico')))
$references=@($metadata.FullName,$runtime.FullName,$tasks.FullName,
 (Join-Path $fx 'System.Runtime.WindowsRuntime.dll'),(Join-Path $fx 'System.Windows.Forms.dll'),
 (Join-Path $fx 'System.Drawing.dll'),(Join-Path $fx 'System.Web.Extensions.dll'),
 (Join-Path $fx 'Microsoft.CSharp.dll'),(Join-Path $fx 'WPF\WindowsBase.dll'),
 (Join-Path $fx 'WPF\PresentationCore.dll'),(Join-Path $fx 'WPF\PresentationFramework.dll'),
 (Join-Path $fx 'System.Xaml.dll'))
foreach($reference in $references){$buildArgs+=('/reference:{0}' -f $reference)}
foreach($pair in @(@('native\Player.xaml','Player.xaml'),@('native\Edge.xaml','Edge.xaml'),@('app.ico','app.ico'),@('LICENSE','LICENSE'),@('NOTICE','NOTICE'))){
 $buildArgs+=('/resource:{0},{1}' -f (Join-Path $PSScriptRoot $pair[0]),$pair[1])
}
$buildArgs+=(Join-Path $PSScriptRoot 'native\NativePlayer.cs')
$buildArgs+=(Join-Path $PSScriptRoot 'GlobalShortcuts.cs')
& (Join-Path $fx 'csc.exe') @buildArgs
if($LASTEXITCODE -ne 0){throw '原生程序构建失败'}
Get-Item -LiteralPath $output