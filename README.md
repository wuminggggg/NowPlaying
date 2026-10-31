# NowPlaying

Windows 上的悬浮媒体控制器。贴到屏幕边缘会自动收成一条，鼠标划过展开。

全局快捷键随时切歌，不用去切窗口。

基于系统的媒体控制接口（SMTC），所以凡是接入系统媒体面板的播放器都能直接控制。没有第三方运行时依赖；本体为原生 C#/WPF 程序。

## 使用

从 [Releases](https://github.com/wuminggggg/NowPlaying/releases/latest) 下载 NowPlaying.exe，双击启动，无需安装。托盘图标左键双击打开设置，右键菜单可以显示/隐藏、禁用快捷键、开关自启或退出。

需要 Windows 10 1809 及以上版本或 Windows 11。

几个播放器同时在放时，默认跟随最近开始播放的那个，也可以在设置里钉死其中一个。

常态和悬停不透明度可分别调整。显示可拖动进度条。

## 默认快捷键

Ctrl+Alt+Space　播放 / 暂停

Ctrl+Alt+左　上一首

Ctrl+Alt+右　下一首

Ctrl+Alt+下　停止

Ctrl+Alt+上　显示 / 隐藏

## 打包成 exe

构建需要 Windows 10 SDK 元数据和系统 .NET Framework 编译器：

powershell -ExecutionPolicy Bypass -File Build.ps1

运行后生成 dist/NowPlaying.exe。

## 配置

设置存在 %LOCALAPPDATA%\NowPlaying\settings.json。

## 许可

GPL-3.0-only，见 LICENSE。
