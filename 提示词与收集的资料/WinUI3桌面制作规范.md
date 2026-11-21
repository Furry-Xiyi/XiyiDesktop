项目任务：开发跨 Windows 版本的 WinUI 3 自定义桌面 Shell

一、项目目标

开发一个基于 C#、WinUI 3、Windows App SDK 的 Windows 桌面程序，用于替换 Windows 原版桌面显示层，实现完整的自定义桌面体验。

项目不是普通桌面美化软件，而是一个真正接管桌面显示、图标管理和桌面交互的 Custom Desktop Shell。

核心要求：保留 Windows 原生 Explorer.exe 和任务栏，只替换桌面显示层。

用户登录 Windows 后自动启动本程序，使其成为实际使用的桌面界面，同时保留 Windows 原生任务栏、开始菜单、通知区域、系统托盘、UWP 激活机制和其他系统 Shell 功能。

二、最高优先级：Shell 架构及限制

1. 必须保留 explorer.exe 运行，不得结束、替换或禁用 Explorer 进程。
2. 不得修改 Winlogon\Shell、Winlogon\Userinit 等登录和 Shell 启动配置。
3. 不依赖 Shell Launcher、Custom User Interface 组策略或 Assigned Access 作为主要运行机制。
4. 不得自行重写或替换 Windows 原生任务栏、通知区域和开始菜单。
5. 仅接管桌面显示层，不得影响其他普通窗口的 Z-order、输入、Alt+Tab、UAC 安全桌面及系统快捷键。
6. 自定义桌面应位于实际桌面窗口层级，普通应用窗口正常覆盖它，原生任务栏正常显示在桌面前方。
7. 桌面区域必须能够正常接收鼠标、键盘、右键和拖放交互，不得做成无法交互的动态壁纸。
8. 不允许通过简单的 Topmost 全屏窗口模拟桌面，也不允许使用覆盖整个屏幕并拦截所有输入的方案。

桌面窗口挂接技术研究及实现

重点研究和验证以下 Windows 内部桌面窗口：

* Progman
* WorkerW
* SHELLDLL_DefView
* FolderView / SysListView32

参考 Lively Wallpaper 的 WinDesktopCore 实现：

https://github.com/lively-community/lively/blob/core-separation/src/Lively/Lively/Core/WinDesktopCore.cs

需要兼容：

* 传统 WorkerW 桌面结构。
* Windows 11 新式 Layered ShellView 结构。
* Progman 使用 WS_EX_NOREDIRECTIONBITMAP 的新式合成结构。
* WorkerW 创建时机及窗口层级变化。
* Explorer 重启后桌面窗口重建及重新挂接。

必须通过真实 HWND 窗口树检测结构，不得只根据 Windows 版本号判断窗口布局。

如果需要使用未经微软公开承诺的内部消息或 WorkerW 技术，必须独立封装，并明确标注为非公开接口，提供版本检测、失败恢复及回退方案。

实现独立接口：

* IDesktopLayerHost
* WindowsDesktopHost
* ClassicWorkerWHost
* ModernLayeredDesktopHost
* DesktopHostFallback

不得为了隐藏原版桌面图标而隐藏整个 Progman、结束 Explorer 或影响任务栏。

如果无法可靠隐藏原生桌面视图，必须优先保留原版桌面并进入兼容模式，不得破坏用户桌面。

三、操作系统兼容范围

目标系统包括：

Windows 客户端

* Windows 10 Home / Pro / Pro for Workstations
* Windows 10 Education / Enterprise / Enterprise LTSC
* Windows 11 Home / Pro / Pro for Workstations
* Windows 11 Education / Enterprise / Enterprise LTSC
* Windows 11 IoT Enterprise / IoT Enterprise LTSC

Windows Server

研究并支持具有 Desktop Experience 的：

* Windows Server 2016
* Windows Server 2019
* Windows Server 2022
* Windows Server 2025

Server Core、Nano Server、Windows PE、Windows RE 和 Windows IoT Core 不属于目标支持范围。

注意区分 Windows IoT Enterprise 与 Windows IoT Core。

实现原则

1. 不依赖需要 Enterprise 才能使用的 Shell Launcher。
2. 不依赖仅部分 SKU 支持的组策略。
3. 优先使用跨版本 Win32 API 和用户级功能。
4. 检测当前系统是否具有 Explorer、交互式桌面和所需 Shell 窗口结构。
5. 支持 x64 和 ARM64，评估 x86 架构的实现成本。
6. 不得把未经实测的系统版本标记为已验证兼容。
7. 对 Windows Insider 预览版本保留适配能力。

四、桌面核心功能

实现自己的完整桌面界面，包括：

4.1 桌面图标

* 枚举当前用户桌面文件和快捷方式。
* 枚举 Public Desktop 公共桌面内容。
* 支持桌面文件夹、快捷方式、可执行文件、文档及其他文件。
* 兼容桌面路径重定向、OneDrive 等已知文件夹重定向。
* 支持图标缓存和异步加载。
* 支持 Windows 原生文件图标和缩略图。
* 支持系统特殊 Shell 对象，例如回收站、此电脑、网络位置。
* 正确处理不同 DPI 和多显示器。

使用 SHGetKnownFolderPath、IShellItem、IShellFolder、SHGetFileInfo、IShellItemImageFactory 等适合的 Windows Shell API。

不得只通过硬编码 %USERPROFILE%\Desktop 实现桌面内容枚举。

4.2 图标排列

支持：

* 大图标、中图标、小图标。
* 自由排列。
* 自动排列。
* 对齐网格。
* 按名称、大小、类型、修改日期排序。
* 多选、框选、拖动。
* 文件重命名。
* 桌面刷新。
* 图标位置持久化。
* 分辨率和 DPI 改变后重新布局。
* 多显示器独立布局。

原版 Explorer 桌面图标排列状态与自定义桌面布局必须分离，不得直接破坏原版 Shell 的布局数据。

4.3 文件交互

实现：

* 双击打开。
* 单击选择。
* 多选。
* 右键菜单。
* 拖动文件。
* 拖放文件到其他窗口。
* 快捷方式创建。
* 复制、剪切、粘贴。
* 删除、重命名。
* 文件和文件夹操作。
* 回收站集成。

文件操作优先通过 Windows Shell 的 IFileOperation 等机制实现，保留进度窗口、权限处理及回收站行为。

4.4 应用启动

支持：

* 普通 Win32 应用。
* UWP 应用。
* MSIX 打包应用。
* Microsoft Store 应用。
* 文件关联。
* URI 协议激活。

研究 IApplicationActivationManager 等官方接口，确保不依赖 Explorer 直接启动 UWP 应用。

五、Windows 原生设置同步

建立完全独立的 SystemSettingsSyncService。

采用只读同步原则：只读取 Windows 设置，不主动修改用户原有设置。

分层实现：

* ISystemSettingsProvider
* RegistrySettingsProvider
* Win32SystemSettingsProvider
* ThemeSettingsProvider
* DisplaySettingsProvider
* ContextMenuSettingsProvider

5.1 系统桌面设置

研究并同步：

* 当前壁纸。
* 桌面图标是否显示。
* 图标大小。
* 图标文字及字体指标。
* 图标排列偏好。
* 系统主题和明暗模式。
* 系统强调色。
* 透明效果。
* 高对比度。
* 显示器分辨率。
* DPI 缩放。
* 工作区域。
* 任务栏占用的桌面区域。

优先使用官方 API：

* SystemParametersInfoW
* SPI_GETDESKWALLPAPER
* SPI_GETWORKAREA
* SPI_GETICONMETRICS
* GetSystemMetrics
* GetMonitorInfo
* EnumDisplayMonitors
* SHGetKnownFolderPath
* UISettings

注意 SPI_GETWORKAREA 主要提供主显示器工作区域，多显示器必须使用适当的显示器 API。

对于没有稳定公开 API 的设置，可以研究实际注册表实现，但必须区分公开接口与内部实现。

5.2 新版 Windows 右键菜单设置

重点研究 Windows 11 新版上下文菜单个性化功能。

候选注册表路径：

HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\ContextMenu\Preferences

重点验证：

* ShowCustomizeMenu
* ShowExtensions
* DemoteTopBar
* CloudStorageActions
* MoveProperties
* verbs\link
* verbs\print
* extensions 各应用子项及 Enabled 值

要求：

1. 实际验证注册表路径、数据类型、默认值和行为。
2. 不得直接把候选值认定为 Windows 永久公开 API。
3. 兼容注册表键不存在、数值缺失和未知扩展。
4. 研究应用扩展主菜单及子菜单的排列规则。
5. 研究 Cut、Copy、Paste、Rename、Delete、Share 的布局偏好。
6. 研究 Properties 和云存储分组的位置偏好。
7. 不同系统版本和 Insider Build 必须进行版本化适配。
8. 系统菜单设置修改后，自己的菜单应自动刷新，无须重启应用。

同步用户在 Windows 系统设置中配置的菜单布局，而不是自行维护一份完全独立、与系统设置冲突的偏好。

注意：菜单显示偏好不等于菜单命令本身。还必须研究 IExplorerCommand、IContextMenu 等 Shell 扩展接口，不能只显示菜单名称却不能实际执行命令。

5.3 设置变化监听

组合使用：

* RegNotifyChangeKeyValue
* WM_SETTINGCHANGE
* WM_THEMECHANGED
* WM_DISPLAYCHANGE
* WM_DPICHANGED
* UISettings.ColorValuesChanged
* SystemEvents.UserPreferenceChanged

要求：

* 启动时完整读取一次。
* 持续监听设置变化。
* 注册表通知触发后重新注册下一次监听。
* 对多个子项分别处理监听。
* 对密集通知进行防抖。
* 仅刷新受影响的 ViewModel。
* 防止注册表监听资源泄漏。
* 系统设置缺失或读取失败时使用安全默认值。

不得为了实现同步而写入或覆盖 Windows 原生设置。

六、桌面右键菜单

采用独立 ContextMenuService。

要求：

* WinUI 3 原生视觉风格。
* 同步 Windows 用户自定义上下文菜单的设置。
* 系统主菜单和扩展子菜单布局一致。
* 支持文件、文件夹和空白桌面不同菜单。
* 支持文件属性、复制路径、重命名、删除等功能。
* 正确处理多选情况下的菜单。
* 支持第三方 Shell 扩展。
* 支持系统图标及菜单命令。
* 对未知扩展采用安全回退。

研究并正确封装 IContextMenu、IExplorerCommand 及必要的 Shell COM 接口。

不得假设所有第三方 Shell 扩展都能直接作为 WinUI 菜单项执行。对于无法安全承载的原生扩展，提供兼容入口。

七、任务栏和系统 Shell 兼容

完全保留 Explorer 原生任务栏。

禁止：

* 自定义任务栏代替原生任务栏。
* 覆盖任务栏。
* 隐藏系统托盘。
* 结束 Explorer。
* 禁用系统通知。
* 修改系统开始菜单。
* 拦截所有系统鼠标和键盘事件。

必须测试：

* Win+D。
* Win+Tab。
* Alt+Tab。
* Win+R。
* Win+E。
* 开始菜单。
* 任务栏自动隐藏。
* 通知区域。
* 快速设置。
* 通知中心。
* 多显示器任务栏。
* 虚拟桌面。
* 锁屏和解锁。
* Explorer 重启。

在 Explorer 重启后重新检测桌面 HWND，并恢复自定义桌面挂接。

八、自动启动和故障恢复

实现用户级自动启动。

优先研究：

* HKCU\Software\Microsoft\Windows\CurrentVersion\Run
* 当前用户 Startup 文件夹。
* Task Scheduler 用户登录触发任务。

要求：

1. 登录后启动，而不是在 Windows 登录界面或安全桌面启动。
2. 必须等待 Explorer 桌面窗口初始化完成。
3. 不需要管理员权限。
4. 不修改 Winlogon Shell。
5. 不依赖 Shell Launcher。
6. 不使用 Windows 服务代替用户桌面程序。
7. Explorer 重启后重新挂接。
8. 自定义桌面进程崩溃时，系统原生 Explorer 桌面必须能够恢复。
9. 提供安全模式和原版桌面恢复入口。
10. 提供独立卸载及关闭自动启动的功能。

未经用户确认，不得实际修改当前电脑的启动设置。

九、代码架构

使用 C# + WinUI 3 + Windows App SDK。

采用 MVVM 和模块化设计，避免把所有逻辑写入 MainWindow.xaml.cs。

建议模块：

* AppLifecycle
* DesktopHost
* DesktopView
* DesktopItems
* DesktopLayout
* ShellInterop
* ApplicationActivation
* FileOperations
* ContextMenu
* SystemSettingsSync
* RegistryMonitoring
* DisplayManagement
* StartupManagement
* RecoveryManager

要求：

* Win32 P/Invoke 独立封装。
* COM 互操作独立封装。
* 使用安全句柄和明确的资源释放。
* 所有 HWND 操作必须考虑 Explorer 重启。
* 异步加载图标和文件。
* 避免阻塞 UI 线程。
* 多显示器使用独立桌面视图。
* DPI 感知。
* 支持浅色、深色及系统主题。
* 支持高对比度。
* 统一错误处理和日志。

Windows App SDK、.NET 和第三方依赖选择当前稳定版本，并通过官方兼容性文档验证目标 Windows 版本，禁止盲目采用预览版本或强制最低版本过高的依赖。

评估 MSIX、Unpackaged 和自包含部署方案，选择能够最大化覆盖目标 SKU 的方案，并说明运行时依赖。

十、开发执行顺序

阶段 1：技术调查

先调查：

* Windows 桌面窗口结构。
* Windows 11 新旧桌面层级差异。
* WorkerW 兼容性。
* Explorer 与任务栏的关系。
* UWP 激活方式。
* Windows 各 SKU 支持范围。
* 注册表设置同步。
* 多显示器与 DPI。

输出技术调查文档、官方文档链接、兼容性矩阵及风险清单。

阶段 2：桌面 Host 原型

优先完成最小原型：

* Explorer 始终运行。
* 正常显示原生任务栏。
* WinUI 3 窗口真正进入桌面层级。
* 正常显示自己的桌面内容。
* 能接收鼠标和右键操作。
* 普通窗口可以正常覆盖桌面。
* Explorer 重启后能够恢复挂接。

优先完成这一阶段的真实运行验证，不要先开发大量 UI 再发现窗口层级方案不可行。

阶段 3：桌面基础功能

实现桌面文件枚举、图标显示、布局、选择、文件操作及应用启动。

阶段 4：系统设置同步

实现 SystemSettingsSyncService、注册表监听、显示器变化监听、主题同步和右键菜单配置同步。

阶段 5：兼容性与恢复

完成不同 Windows 桌面版本、不同 SKU、多显示器、Explorer 崩溃及用户登录退出的测试。

阶段 6：安装和发布

制作安装程序、用户级自动启动配置、卸载程序和原版桌面恢复工具。

十一、验收标准

以下项目必须逐项验证：

测试项目	通过标准
原生任务栏	始终正常运行
原生开始菜单	正常打开
UWP 应用	可以启动
Win32 应用	可以启动
桌面交互	正常工作
桌面右键菜单	正常工作
系统菜单偏好	修改后自动同步
系统主题	自动同步
壁纸	正确同步
图标排列	正常工作
多显示器	正确显示
DPI 改变	自动调整
Explorer 重启	自动恢复
桌面程序崩溃	原生桌面可以恢复
自动启动	登录后正常启动
关闭自动启动	恢复正常原生桌面
Windows Home	不依赖专业版功能
Windows Server Desktop Experience	单独验证

任何未实际测试的平台都必须标注为待验证，不得虚构兼容性结果。

十二、最终交付物

交付：

1. 完整 WinUI 3 项目源代码。
2. 完整解决方案及项目配置。
3. Desktop Host 技术架构文档。
4. Windows 注册表设置映射表。
5. Windows SKU 兼容性矩阵。
6. 系统 API 和 COM 接口说明。
7. 自动启动及卸载实现。
8. 原版桌面恢复工具。
9. 日志和故障诊断机制。
10. 可编译的 Release 版本。

十三、执行纪律

* 直接检查当前工作区和已有项目，尽量在现有项目基础上开发。
* 不要覆盖已有源代码或用户数据。
* 优先研究官方 Microsoft Learn 文档，再参考成熟开源项目。
* 涉及非公开 Windows Shell 接口时必须说明风险和替代方案。
* 不要把注册表内部值当成永久稳定的公开接口。
* 不要为了兼容性而削减核心功能。
* 不要将普通窗口假装成真正的桌面 Host。
* 不得擅自结束 Explorer、修改 Winlogon 或修改系统级启动配置。
* 先实现真实的桌面窗口接管原型，再继续完整功能。
* 遇到技术限制时，继续研究其他实现路径，而不是直接放弃。
* 不需要反复询问已经明确的需求。
* 完成后生成完整项目及文档，并执行适当的构建和测试。

最终目标：实现一个可在广泛 Windows 桌面 SKU 上运行、保留原生 Windows 任务栏与系统 Shell 功能、由 WinUI 3 完整管理桌面显示和交互的自定义桌面环境。

## 十四、当前项目进度记录（2026-11-11）

以下内容是当前工程的进度记录，用于和上面的目标、要求及验收标准对照；它不降低前文的兼容性要求，也不把单机验证结果当作跨版本承诺。

### 已完成或已接入

* 项目已拆分为桌面 Host 与设置页面的代码边界，但采用一个普通 `XiyiDesktop.Desktop` WinUI 3 可执行项目，不单独打包设置项目。
* 已移除桌面项目的 `Package.appxmanifest`/独立 Package 部署路线；`Assets`、中英文 `Strings`、设置页面和桌面对话框均由桌面项目维护。
* 设置窗口已作为应用窗口运行，设置页面按功能分组；对话框放在 `Dialogs` 文件夹。
* 桌面 Host 已接入 Explorer 桌面层探测、窗口挂接、原生图标视图隐藏/恢复、壁纸显示和桌面项目枚举。
* 当前用户桌面和公共桌面内容、此电脑、回收站、快捷方式、文件图标/缩略图、打开和基础文件操作已经接入；快捷方式显示名称不带 `.lnk` 等后缀。
* 桌面右键菜单已提供本地化文本和图标，并支持空白桌面与项目菜单的基础操作。
* 当前开发机主显示器已经运行验证桌面挂接、壁纸、桌面图标和基础右键交互；此前的 DPI 缩放错位问题已由用户修复。

### 尚未完成或仍需验证

* 网格顺序持久化已接入代码但尚未构建或运行验证；自定义自由坐标位置、自由排列、自动排列、对齐网格、框选和拖放仍待实现。
* 多显示器独立桌面视图、负坐标排列、混合 DPI、显示器热插拔和 DPI 动态改变。
* 触摸与笔输入、普通窗口覆盖、任务栏自动隐藏、虚拟桌面、锁屏/解锁和输入法交互。
* Explorer 重启、桌面窗口句柄变化、应用关闭后的原生桌面恢复以及异常退出恢复。
* `IContextMenu`/`IExplorerCommand` 第三方 Shell 扩展、完整菜单偏好同步和 UWP/MSIX 应用激活。
* Windows 10/11 各版本、Windows Server Desktop Experience、x64/ARM64 及不同 Shell 窗口树的兼容性矩阵。
* 用户级自动启动、安装/卸载、恢复工具和最终 Release 交付。

当前进度记录只说明开发机上的已验证路径。每项跨系统或跨硬件的验收仍必须附带系统版本/build、SKU、架构、Windows App SDK 运行时、显示器/DPI 布局、HWND 树记录和复现步骤。
