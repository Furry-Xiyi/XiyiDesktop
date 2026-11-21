# 本地开发与启动

应用由一个普通 WinUI 3 桌面项目提供，不生成 MSIX。桌面窗口和设置窗口由同一个进程提供；桌面上的设置入口直接打开设置窗口，设置概览可切回桌面窗口。

在 Visual Studio 中以 `XiyiDesktop.Desktop` 项目启动应用。图片和中英文 `.resw` 资源由该 WinUI 项目编译并复制到应用输出目录。应用偏好保存在当前用户的本地应用数据目录。

桌面 Host 会查找 Explorer 桌面图标层，挂接透明 WinUI 窗口以透出当前 Windows 壁纸，并显示桌面目录图标；窗口按 Explorer 宿主客户区调整大小以适应显示器旋转。Explorer 窗口树挂接、DPI、渲染、输入、多显示器与 Explorer 重启恢复仍需实机验证。
