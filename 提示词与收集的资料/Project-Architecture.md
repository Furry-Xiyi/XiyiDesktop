# 项目结构

解决方案由一个普通 WinUI 3 桌面应用组成，不生成 MSIX：

| 项目 | 职责 | 入口 |
| --- | --- | --- |
| `XiyiDesktop.Desktop` | 桌面 Host、桌面图标浏览、文件操作、设置窗口及应用资源/图片 | `DesktopWindow`、`MainWindow` |

桌面窗口通过进程内 `ShowSettingsWindow()` 打开设置窗口；设置概览调用 `ShowDesktopWindow()` 切回桌面。中英文运行时资源和应用图像位于 `XiyiDesktop.Desktop/Strings` 与 `XiyiDesktop.Desktop/Assets`。

桌面 Host 会枚举顶层 `Progman` 和 `WorkerW`，查找包含 `SHELLDLL_DefView` 与 `SysListView32` 的 Explorer 图标层，并把透明 WinUI 窗口放在图标层上方，使 Explorer 自己绘制的系统壁纸直接透出。挂接后临时隐藏 Explorer 原生图标列表，退出或检测到句柄变化时恢复；图标由桌面目录服务显示。窗口尺寸按实际 Explorer 宿主客户区更新，显示器旋转或 DPI 变化后重新铺满宿主区域。桌面窗口在挂接成功前保持隐藏，不会以普通应用窗口充当桌面。此为实验性的非公开 Explorer 窗口树用法，并涉及跨进程 `SetParent` DPI 上下文变更；渲染、输入、多显示器、普通窗口遮挡及 Explorer 重启恢复仍未实机验证。未找到可识别图标层时，Host 会继续重试，应用再次启动入口可打开设置窗口。

桌面窗口枚举当前用户和公共桌面目录，并加入“此电脑”和回收站 Shell 入口；文件/快捷方式通过 Windows Shell 打开。右键菜单和 Shell 文件操作提供选择、复制、剪切、粘贴、重命名、回收站删除及属性等基础交互。文件图标与缩略图异步加载，桌面目录变更后重新枚举。此阶段尚未实现自定义位置持久化、自动/自由排列、多显示器独立布局、拖放到其他窗口及第三方 `IContextMenu` 扩展。

Host 以短间隔重新检查 Explorer 桌面窗口树；检测到布局变化后恢复原生图标视图并重新查找挂接目标。跨进程挂接、主显示器桌面层布局、多显示器、DPI 变化、普通窗口覆盖、任务栏行为、Explorer 重启和关闭后的原生恢复仍须在目标 Windows 实机验证。

## 当前验证状态

项目按要求尚未构建或运行，因此 Explorer 桌面挂接、壁纸加载、桌面图标交互、Shell 文件操作、图标加载、WinUI HWND 跨进程 reparent、鼠标输入、普通窗口覆盖、Explorer 重启恢复和各系统版本兼容性均标记为待验证。不得把“代码已接入”视为阶段二或阶段三验收通过。
