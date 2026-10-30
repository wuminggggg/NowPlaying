Option Explicit
Dim shell, fso, script, settings
Set shell = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")
script = fso.BuildPath(fso.GetParentFolderName(WScript.ScriptFullName), "NowPlaying.ps1")
settings = shell.ExpandEnvironmentStrings("%LOCALAPPDATA%\NowPlaying\settings.json")
shell.Run "powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File """ & script & """ -SettingsPath """ & settings & """", 0, False