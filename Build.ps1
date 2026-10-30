#requires -Version 5.1
param([string]$OutputDirectory=(Join-Path $PSScriptRoot 'dist'))
$ErrorActionPreference='Stop'
$compiler=Join-Path ([Environment]::GetFolderPath('Windows')) 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if(![IO.File]::Exists($compiler)){
 $compiler=Join-Path ([Environment]::GetFolderPath('Windows')) 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if(![IO.File]::Exists($compiler)){throw '找不到 Windows .NET Framework 编译器'}
[void][IO.Directory]::CreateDirectory($OutputDirectory)
$output=Join-Path $OutputDirectory 'NowPlaying.exe'
$buildArgs=@('/nologo','/target:winexe','/platform:anycpu','/reference:System.Windows.Forms.dll',('/win32icon:{0}' -f (Join-Path $PSScriptRoot 'app.ico')))
foreach($file in @('NowPlaying.ps1','Docking.ps1','GlobalShortcuts.cs','app.ico','LICENSE','NOTICE')){
 $buildArgs+=('/resource:{0},{1}' -f (Join-Path $PSScriptRoot $file),$file)
}
$buildArgs+=('/out:{0}' -f $output)
$buildArgs+=(Join-Path $PSScriptRoot 'source\PortableLauncher.cs')
& $compiler @buildArgs
if($LASTEXITCODE -ne 0){throw '构建失败'}
Get-Item -LiteralPath $output