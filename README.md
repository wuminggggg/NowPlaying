# NowPlaying

Windows 上的悬浮媒体控制器。半透明小卡片浮在桌面上，贴到屏幕边缘会自动收成一条，鼠标划过展开；全局快捷键随时切歌，不用去切窗口。

基于系统的媒体控制接口（SMTC），所以凡是接入系统媒体面板的播放器都能直接控制，Spotify、网易云、浏览器里的网页播放都算。没有第三方依赖，本体就是一个 PowerShell 脚本。

## 使用

双击 Start.vbs 启动，托盘图标右键进设置、退出。需要 Windows 10 或 11，系统自带的 PowerShell 5.1 足够。

几个播放器同时在放时，默认跟随最近开始播放的那个，也可以在设置里钉死其中一个。

## 默认快捷键

Ctrl+Alt+Space　播放 / 暂停

Ctrl+Alt+左　上一首

Ctrl+Alt+右　下一首

Ctrl+Alt+下　停止

Ctrl+Alt+上　显示 / 隐藏

都能在设置里改，也可以整组关掉。

## 打包成 exe

powershell -ExecutionPolicy Bypass -File Build.ps1

生成 dist/NowPlaying.exe，脚本和资源内嵌在里面，运行时释放到 %LOCALAPPDATA%\NowPlaying。

## 配置

设置存在 %LOCALAPPDATA%\NowPlaying\settings.json，删掉就回到默认值。

## 许可

GPL-3.0-only，见 LICENSE。
