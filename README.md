# NowPlaying

Windows 上的悬浮媒体控制器。半透明小卡片浮在桌面上，贴到屏幕边缘会自动收成一条，鼠标划过展开；全局快捷键随时切歌，不用去切窗口。

基于系统的媒体控制接口（SMTC），所以凡是接入系统媒体面板的播放器都能直接控制，Spotify、网易云、浏览器里的网页播放都算。没有第三方运行时依赖；从 v1.0.1 起，本体为原生 C#/WPF 程序，直接调用 Windows 媒体接口，不再内嵌执行 PowerShell 脚本或调用 WScript。

## 使用

从 [Releases](https://github.com/wuminggggg/NowPlaying/releases/latest) 下载 NowPlaying.exe，双击启动，无需安装。托盘图标左键双击打开设置，右键菜单可以显示/隐藏、禁用快捷键、开关自启或退出。需要 Windows 10 1809 及以上版本或 Windows 11。

几个播放器同时在放时，默认跟随最近开始播放的那个，也可以在设置里钉死其中一个。

常态和悬停不透明度可分别调整，文字、封面、按钮与背景一起变化。播放器提供跳转能力时显示可拖动进度条；贴边收起后，进度从下往上填充。

勾选托盘菜单“开机自启”后，程序会复制到当前用户的本地应用数据目录，并创建登录后延迟 15 秒的任务，直接启动 EXE；取消勾选会移除任务。升级前先从托盘退出旧版本，再启动新 EXE；已开启的自启会更新到新版本。

## 默认快捷键

Ctrl+Alt+Space　播放 / 暂停

Ctrl+Alt+左　上一首

Ctrl+Alt+右　下一首

Ctrl+Alt+下　停止

Ctrl+Alt+上　显示 / 隐藏

都能在设置里改，也可以整组关掉。

## 打包成 exe

构建需要 Windows 10 SDK 元数据和系统 .NET Framework 编译器。PowerShell 仅用于运行构建脚本：

powershell -ExecutionPolicy Bypass -File Build.ps1

生成 dist/NowPlaying.exe，界面、图标与许可证嵌入 EXE，运行时不释放或启动脚本。

## 配置

设置存在 %LOCALAPPDATA%\NowPlaying\settings.json，删掉就回到默认值。

## 许可

GPL-3.0-only，见 LICENSE。
