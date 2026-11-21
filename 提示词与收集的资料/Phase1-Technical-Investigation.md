# 阶段一：技术调查与兼容性基线

本文根据项目需求规范整理自定义桌面 Host 的技术边界、可采用的公开接口、需要专项验证的 Shell 行为和测试矩阵。它是实现前的调查基线，不代表任何 Windows 版本已经通过运行验证。

## 目标与安全边界

本项目保留 `explorer.exe`、原生任务栏、开始菜单、通知区域和系统 Shell，仅承载自定义桌面显示层。不得结束 Explorer、修改 `Winlogon\Shell` / `Winlogon\Userinit`、替换任务栏、设置全屏 Topmost 窗口或拦截系统级鼠标键盘输入。未确认挂接成功时，隐藏自定义桌面窗口并保留 Explorer 原生桌面；设置窗口仍可单独打开。

阶段二应先证明窗口层级、输入、普通窗口遮盖和 Explorer 重启恢复，再进入桌面图标和文件操作阶段。当前阶段不设置登录启动项，也不修改本机系统配置。

当前解决方案采用一个普通 WinUI 3 可执行项目 `XiyiDesktop.Desktop`，不使用独立打包项目。设置窗口与桌面窗口同进程运行；当前开发部署不注册 MSIX 包身份或自定义 URI 协议。

## 桌面窗口结构与挂接路线

传统桌面常可观察到 `Progman`、`WorkerW`、`SHELLDLL_DefView`、`SysListView32`（FolderView）等 HWND。它们是 Explorer 的实现细节，不是承诺稳定的应用扩展点；不能仅凭操作系统版本号假定窗口树结构。探测必须通过窗口类名、父子/相邻关系、所属进程和有效 HWND 实时完成，并记录检测结果。

Windows 11 的桌面合成结构可能不同，包含 Layered ShellView 或带 `WS_EX_NOREDIRECTIONBITMAP` 的窗口形态。应将传统 WorkerW 与现代结构分开探测。面向现代结构的 host 在未有真实测试和明确兼容路径前，必须返回“未识别/不支持”，不能强行调用传统挂接流程。

建议阶段二模块边界：

| 模块 | 职责 | 失败行为 |
| --- | --- | --- |
| `IDesktopLayerHost` | 启动、挂接、布局更新、停止和状态报告 | 返回明确失败状态 |
| `WindowsDesktopHost` | 真实 HWND 树探测，选择适配器，协调重试 | 不隐藏 Explorer 窗口 |
| `ClassicWorkerWHost` | 隔离传统 WorkerW 探测及可选挂接流程 | 恢复原窗口样式/父级，转兼容模式 |
| `ModernLayeredDesktopHost` | 独立识别现代 ShellView；逐版本验证 | 未识别时不尝试修改层级 |
| `DesktopHostFallback` | 保持原生桌面并提供普通设置/预览入口 | Explorer 桌面持续可用 |

经典路线可能会使用 `SendMessageTimeout` 请求 Explorer 建立 WorkerW、`SetParent` 变更父窗口以及 `SetWindowPos` 更新尺寸。`SetParent` 本身是公开 Win32 API，但把 WinUI 3 HWND 嵌入 Explorer 私有窗口树这一用法，以及 WorkerW 的发现/创建约定，都不是 Microsoft 对第三方桌面 Shell 承诺的稳定契约。内部消息通常被称作 `0x052C`，不应扩散到业务代码，也不能作为唯一实现路径。

挂接前保存 HWND 原父级与相关样式；设置失败、目标 HWND 消失、窗口布局异常或 Explorer 重启时，撤销部分修改并让应用 HWND 回到安全状态。不得为了隐藏原生桌面图标而隐藏整个 Progman / WorkerW，也不得直接改写 Explorer 自身图标布局数据。

## Explorer、任务栏及生命周期

Explorer 负责任务栏、通知区域、开始菜单激活等多项系统体验。Host 只接触被识别为桌面显示层的 HWND，不修改任务栏 HWND，不设置 Topmost，也不接管 Alt+Tab、UAC 安全桌面或系统快捷键。

Explorer 更新或重启后，缓存的桌面 HWND 可能失效。收到任务栏重建消息或窗口事件后，Host 应在 UI Dispatcher 上合并重复通知，丢弃旧句柄、重新枚举 HWND 树，再尝试挂接；检测不到目标时应保持自定义桌面窗口隐藏并保留 Explorer 原生桌面。所有重试必须有上限/退避，并提供清楚的诊断日志，避免与 Explorer 的启动竞态形成 UI 线程阻塞。

阶段二人工验收至少包括普通窗口覆盖桌面、任务栏和开始菜单正常、鼠标单击/右键、Win+D、Win+Tab、Alt+Tab、Win+R、Win+E、任务栏自动隐藏、Explorer 正常重启以及自定义 Host 关闭后的原生桌面恢复。未运行验证前均为待验证。

## 显示器、工作区及 DPI

使用 `EnumDisplayMonitors` 遍历显示器，使用 `GetMonitorInfo` 获取每台显示器的屏幕区域和工作区；`SPI_GETWORKAREA` 主要描述主显示器工作区，不能代替多显示器枚举。窗口坐标涉及负坐标、显示器排列、任务栏自动隐藏与 DPI 变更；后续应监视显示器拓扑、`WM_DISPLAYCHANGE`、`WM_DPICHANGED` 和窗口布局变化。

跨进程 `SetParent` 可能遇到 DPI awareness context 不一致，尤其应验证 Per-Monitor DPI 变化和 WinUI 3 HWND 的限制。挂接失败时不应显示成普通应用窗口，也不以重设进程 DPI 模式或全局覆盖输入来规避。阶段二先验证主显示器路径；多显示器各自布局、热插拔和混合缩放留作明确测试项。

## UWP / MSIX 激活与部署

Win32 文件或 URI 启动可先使用系统关联和 `Launcher` API；UWP / MSIX 应用启动要研究 `IApplicationActivationManager` 及包激活 API，不得假设由 Explorer 的私有行为转发。确认调用身份、包清单、打包/非打包部署、激活参数和失败提示后再纳入桌面交互。

WinUI 可执行项目目标为 `net8.0-windows10.0.19041.0`，最小平台配置为 `10.0.17763.0`，启用了 x64 与 ARM64，引用 Windows App SDK 1.8、Windows SDK BuildTools 10.0.28000.2957 和 CommunityToolkit SettingsControls。应用使用 Windows App SDK 自包含运行时，不生成 MSIX。项目暂未声明 x86 目标。

当前唯一 WinUI 可执行项目采用 `net8.0-windows10.0.19041.0` 与 Windows App SDK 1.8，直接以普通桌面项目方式启动。设置窗口由进程内调用打开；设置概览直接切回桌面窗口。

## Windows 设置读取及注册表风险

系统桌面信息优先采用公开只读 Win32 / WinRT API：`SystemParametersInfoW`（如壁纸、工作区、图标指标）、`EnumDisplayMonitors`、`GetMonitorInfo`、`SHGetKnownFolderPath`、`UISettings` 等。需区分 API 是否覆盖某个设置以及其适用范围；设置不可读时显示安全默认值，不写回或“修复”用户原生设置。

Windows 11 上下文菜单候选设置位置 `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\ContextMenu\Preferences` 及其值名属于待调查对象，并非本文认定的公开契约。应逐系统构建验证键是否存在、类型、默认值、变更通知和实际菜单行为；未知值忽略，值缺失使用默认值。`RegNotifyChangeKeyValue`、`WM_SETTINGCHANGE`、`WM_THEMECHANGED`、`WM_DISPLAYCHANGE`、`WM_DPICHANGED`、`UISettings.ColorValuesChanged` 和 `SystemEvents.UserPreferenceChanged` 可用于构建只读变化监听；监听句柄、订阅和重复通知须正确释放/防抖。

菜单偏好与菜单命令是两件事。`IContextMenu`、`IExplorerCommand` 等 Shell 扩展需要独立的 COM 封装和失败隔离；不要把扩展显示名称冒充为可执行命令，也不要假设第三方扩展都能安全在 WinUI 菜单中运行。

## 兼容性矩阵（全部待验证）

| 系统/架构 | 目标范围 | 当前状态 | 特别验证项 |
| --- | --- | --- | --- |
| Windows 10 客户端 x64 | Home、Pro、Pro for Workstations、Education、Enterprise、Enterprise LTSC | 待验证 | 传统 WorkerW 结构、任务栏遮挡、WinUI SDK 最低系统要求 |
| Windows 10 客户端 ARM64 | 同上（发行版实际可用性需逐项确认） | 待验证 | ARM64 进程、Shell COM、DPI、依赖包 |
| Windows 11 客户端 x64 | Home、Pro、Pro for Workstations、Education、Enterprise、Enterprise LTSC | 待验证 | 传统/Layered ShellView 实际 HWND 树及更新变更 |
| Windows 11 客户端 ARM64 | Home、Pro、Education、Enterprise 等实际可用版本 | 待验证 | ARM64 COM、包激活、多屏与混合 DPI |
| Windows 11 IoT Enterprise / LTSC | 明确区分 IoT Enterprise 与 IoT Core | 待验证 | SKU 组件差异、桌面体验和系统更新 |
| Windows Server 2016/2019/2022/2025 Desktop Experience | 仅 Desktop Experience | 待验证 | Explorer/Shell 组件、激活、生命周期、每个版本单独测试 |
| Windows Insider 预览版 | 不承诺支持 | 待验证 | 保持结构探测适配，不依据 build 号硬编码成功 |
| Server Core、Nano Server、Windows PE/RE、IoT Core | 不在目标范围 | 不支持目标 | 不尝试伪装桌面可用 |

任何测试结果应附带系统版本/build、SKU、架构、Windows App SDK 运行时、显示器/DPI 布局、HWND 树记录和复现步骤；未执行的测试保留“待验证”。

## 阶段一风险清单

| 风险 | 影响 | 缓解措施 |
| --- | --- | --- |
| WorkerW / 0x052C 非公开且可能变化 | 系统更新后挂接失效或行为异常 | 隔离适配器；实时探测；失败即回退；Windows build 分组测试 |
| WinUI HWND 跨进程重设 parent / style | XAML 输入、渲染或关闭生命周期异常 | 小型原型验证；保存并恢复状态；不支持时放弃挂接 |
| Explorer 重启和启动竞态 | 自定义层脱离、空白/重复桌面层 | 事件触发重新枚举，有限退避，永不缓存失效 HWND |
| 原生视图与自定义视图叠放 | 图标重复、无法点击、桌面闪烁 | 不隐藏整个 Explorer 窗口；按实际结构做独立兼容模式 |
| DPI / 多显示器 | 错位、裁切、输入坐标不一致 | 逐显示器边界布局、DPI 消息处理、混合 DPI 实机验证 |
| Shell COM 扩展不稳定 | 菜单崩溃或第三方代码问题 | 独立进程/异常边界评估、超时和安全兼容入口 |
| 注册表候选值语义不稳定 | 菜单与用户设置不一致 | 只读探测；版本化映射；未验证值不生效 |
| 包激活与部署差异 | UWP / Store 应用无法启动 | 分别覆盖 MSIX 与 unpackaged，记录依赖和失败原因 |

## 官方文档与参考

以下 Microsoft Learn 页面用于公开 API 和平台行为调查。它们不构成对 WorkerW 或自定义桌面 Shell 的兼容承诺。

- [WinUI 3 与 Windows App SDK 概览](https://learn.microsoft.com/windows/apps/winui/winui3/)
- [Windows App SDK 发布说明与已知问题](https://learn.microsoft.com/windows/apps/windows-app-sdk/release-notes/)
- [Windows App SDK 支持的操作系统版本](https://learn.microsoft.com/windows/apps/windows-app-sdk/system-requirements)
- [Windows 应用窗口概览](https://learn.microsoft.com/windows/apps/develop/ui-input/windowing-overview)
- [`EnumWindows`](https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-enumwindows)
- [`FindWindowEx`](https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-findwindowexw)
- [`SetParent`](https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-setparent)
- [`SetWindowPos`](https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-setwindowpos)
- [`RegisterWindowMessage`](https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-registerwindowmessagew)
- [High DPI Desktop Application Development](https://learn.microsoft.com/windows/win32/hidpi/high-dpi-desktop-application-development-on-windows)
- [`EnumDisplayMonitors`](https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-enumdisplaymonitors)
- [`GetMonitorInfo`](https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-getmonitorinfow)
- [`SystemParametersInfoW`](https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-systemparametersinfow)
- [`SHGetKnownFolderPath`](https://learn.microsoft.com/windows/win32/api/shlobj_core/nf-shlobj_core-shgetknownfolderpath)
- [`UISettings`](https://learn.microsoft.com/uwp/api/windows.ui.viewmanagement.uisettings)
- [`RegNotifyChangeKeyValue`](https://learn.microsoft.com/windows/win32/api/winreg/nf-winreg-regnotifychangekeyvalue)
- [`IApplicationActivationManager`](https://learn.microsoft.com/windows/win32/api/shobjidl_core/nn-shobjidl_core-iapplicationactivationmanager)
- [Activate Windows Store apps](https://learn.microsoft.com/windows/uwp/launch-resume/launch-activate-application)
- [`IContextMenu`](https://learn.microsoft.com/windows/win32/api/shobjidl_core/nn-shobjidl_core-icontextmenu)
- [`IExplorerCommand`](https://learn.microsoft.com/windows/win32/api/shobjidl_core/nn-shobjidl_core-iexplorercommand)
- [Lively Wallpaper `WinDesktopCore` implementation (community reference)](https://github.com/lively-community/lively/blob/core-separation/src/Lively/Lively/Core/WinDesktopCore.cs)

