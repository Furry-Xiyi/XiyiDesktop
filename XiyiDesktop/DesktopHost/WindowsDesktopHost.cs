using System;
using System.Runtime.InteropServices;
using System.Text;

namespace XiyiDesktop.DesktopHost
{
    /// <summary>Finds Explorer's current desktop icon view and attaches above that view.</summary>
    public sealed class WindowsDesktopHost
    {
        private readonly IDesktopLayerHost[] _hosts =
        {
            new ExplorerShellViewHost()
        };

        public bool TryAttach(IntPtr windowHandle, out DesktopHostAttachment? attachment)
        {
            attachment = null;
            if (windowHandle == IntPtr.Zero || !NativeMethods.IsWindow(windowHandle))
                return false;

            foreach (var host in _hosts)
            {
                if (host.TryAttach(windowHandle, out attachment))
                    return true;
            }

            return false;
        }
    }

    internal sealed class ExplorerShellViewHost : IDesktopLayerHost
    {
        public string Name => "Explorer desktop icon layer";

        public bool TryAttach(IntPtr windowHandle, out DesktopHostAttachment? attachment)
        {
            attachment = null;
            var progman = NativeMethods.FindWindow("Progman", null);
            if (progman == IntPtr.Zero || !NativeMethods.IsWindow(progman))
                return false;

            // Explorer creates/rearranges its WorkerW desktop layers lazily. Ask it
            // to materialize the classic layout before taking a snapshot. This is an
            // Explorer implementation detail, so the call is bounded and optional.
            NativeMethods.RequestClassicDesktopLayout(progman);

            if (TryCreateAttachment(progman, out attachment))
                return true;

            // Use EnumWindows rather than assuming that every WorkerW is returned in
            // the same direct-child order on every Explorer build.
            foreach (var worker in NativeMethods.EnumerateTopLevelWindows())
            {
                if (!NativeMethods.GetClassName(worker).Equals("WorkerW", StringComparison.Ordinal))
                    continue;

                if (TryCreateAttachment(worker, out attachment))
                    return true;
            }

            // Keep a direct enumeration as a final fallback for shells that do not
            // expose the expected top-level callback order.
            for (var worker = NativeMethods.FindWindowEx(IntPtr.Zero, IntPtr.Zero, "WorkerW", null);
                 worker != IntPtr.Zero;
                 worker = NativeMethods.FindWindowEx(IntPtr.Zero, worker, "WorkerW", null))
            {
                if (TryCreateAttachment(worker, out attachment))
                    return true;
            }

            return false;
        }

        private bool TryCreateAttachment(IntPtr hostWindow, out DesktopHostAttachment? attachment)
        {
            attachment = null;
            var hostClass = NativeMethods.GetClassName(hostWindow);
            if (hostWindow == IntPtr.Zero ||
                (!hostClass.Equals("Progman", StringComparison.Ordinal) &&
                 !hostClass.Equals("WorkerW", StringComparison.Ordinal)))
                return false;

            var shellView = NativeMethods.FindWindowEx(hostWindow, IntPtr.Zero, "SHELLDLL_DefView", null);
            var listView = shellView == IntPtr.Zero
                ? IntPtr.Zero
                : NativeMethods.FindWindowEx(shellView, IntPtr.Zero, "SysListView32", null);
            var processId = NativeMethods.GetWindowProcessId(hostWindow);
            if (shellView == IntPtr.Zero || listView == IntPtr.Zero || processId == 0 ||
                NativeMethods.GetParentWindow(shellView) != hostWindow ||
                NativeMethods.GetParentWindow(listView) != shellView ||
                NativeMethods.GetWindowProcessId(shellView) != processId ||
                NativeMethods.GetWindowProcessId(listView) != processId)
                return false;

            attachment = new DesktopHostAttachment(
                hostWindow,
                Name,
                DesktopHostKind.ShellViewOverlay,
                shellView,
                usesUndocumentedShellBehavior: true);
            return true;
        }
    }

    internal static class NativeMethods
    {
        private const int GwlStyle = -16;
        private const int GwlExStyle = -20;
        private const long WsChild = 0x40000000L;
        private const long WsPopup = unchecked((long)0x80000000L);
        private const long WsCaption = 0x00C00000L;
        private const long WsThickFrame = 0x00040000L;
        private const long WsSystemMenu = 0x00080000L;
        private const long WsMinimizeBox = 0x00020000L;
        private const long WsMaximizeBox = 0x00010000L;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoZOrder = 0x0004;
        private const uint SwpFrameChanged = 0x0020;
        private const uint SwpShowWindow = 0x0040;
        private const uint SwpHideWindow = 0x0080;
        private const uint SmtoBlock = 0x0001;
        private const uint SmtoAbortIfHung = 0x0002;
        private const uint ClassicDesktopLayoutMessage = 0x052C;
        private const uint WmContextMenu = 0x007B;
        private const uint LwaAlpha = 0x00000002;
        private const byte FullyOpaqueAlpha = 255;
        private const int DwmwaUseHostBackdropBrush = 17;
        private const int DwmwaRedirectionBitmapAlpha = 39;
        private const int SwHide = 0;
        private const int SwShow = 5;
        private const uint GwHwndNext = 2;
        internal const long WsExLayered = 0x00080000L;
        internal const long WsExNoRedirectionBitmap = 0x00200000L;
        private static readonly IntPtr HwndTop = IntPtr.Zero;

        internal static bool IsWindow(IntPtr hwnd) => IsWindowNative(hwnd);

        internal static void HideWindow(IntPtr hwnd)
        {
            if (IsWindow(hwnd))
                ShowWindow(hwnd, SwHide);
        }

        internal static IntPtr FindWindow(string className, string windowName) =>
            FindWindowNative(className, windowName);

        internal static IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string windowName) =>
            FindWindowExNative(parent, childAfter, className, windowName);

        internal static void RequestClassicDesktopLayout(IntPtr progman)
        {
            if (!IsWindow(progman))
                return;

            SendMessageTimeout(
                progman,
                ClassicDesktopLayoutMessage,
                new IntPtr(0xD),
                new IntPtr(1),
                SmtoBlock | SmtoAbortIfHung,
                1000,
                out _);
        }

        internal static bool PostNativeDesktopContextMenu(IntPtr shellViewHandle)
        {
            if (!IsWindow(shellViewHandle) || !GetCursorPos(out var point))
                return false;

            // WM_CONTEXTMENU expects signed 16-bit screen coordinates in LPARAM.
            var coordinates = unchecked((int)(((ushort)point.Y << 16) | (ushort)point.X));
            return PostMessage(shellViewHandle, WmContextMenu, shellViewHandle, new IntPtr(coordinates));
        }

        internal static System.Collections.Generic.IReadOnlyList<IntPtr> EnumerateTopLevelWindows()
        {
            var windows = new System.Collections.Generic.List<IntPtr>();
            EnumWindows((window, _) =>
            {
                if (window != IntPtr.Zero && IsWindow(window))
                    windows.Add(window);
                return true;
            }, IntPtr.Zero);
            return windows;
        }

        internal static uint GetWindowProcessId(IntPtr hwnd)
        {
            GetWindowThreadProcessId(hwnd, out var processId);
            return processId;
        }

        internal static IntPtr GetParentWindow(IntPtr hwnd) => GetParent(hwnd);

        internal static void HideNativeIconView(IntPtr hwnd)
        {
            if (IsWindow(hwnd))
                ShowWindow(hwnd, SwHide);
        }

        internal static bool IsWindowVisible(IntPtr hwnd) =>
            IsWindow(hwnd) && IsWindowVisibleNative(hwnd);

        internal static void ShowNativeIconView(IntPtr hwnd)
        {
            if (IsWindow(hwnd))
                ShowWindow(hwnd, SwShow);
        }

        internal static bool HasNoExtendedStyle(IntPtr hwnd, long styleMask) =>
            TryGetExtendedStyle(hwnd, out var style) && (style & styleMask) == 0;

        private static bool TryGetExtendedStyle(IntPtr hwnd, out long style)
        {
            SetLastError(0);
            style = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
            return style != 0 || Marshal.GetLastWin32Error() == 0;
        }

        internal static string GetClassName(IntPtr hwnd)
        {
            var buffer = new StringBuilder(256);
            return GetClassNameNative(hwnd, buffer, buffer.Capacity) > 0 ? buffer.ToString() : string.Empty;
        }

        internal static bool TryAttachWindow(
            IntPtr windowHandle,
            IntPtr parentHandle,
            IntPtr shellViewHandle,
            out WindowRestoreState originalState)
        {
            var originalParent = GetParent(windowHandle);
            var originalStyle = GetWindowLongPtr(windowHandle, GwlStyle).ToInt64();
            var listView = IsWindow(shellViewHandle)
                ? FindWindowEx(shellViewHandle, IntPtr.Zero, "SysListView32", null)
                : IntPtr.Zero;
            originalState = default;

            if (originalParent != IntPtr.Zero || !IsWindow(parentHandle) || !IsWindow(shellViewHandle) ||
                (!GetClassName(parentHandle).Equals("Progman", StringComparison.Ordinal) &&
                 !GetClassName(parentHandle).Equals("WorkerW", StringComparison.Ordinal)) ||
                GetParent(shellViewHandle) != parentHandle ||
                !GetClassName(shellViewHandle).Equals("SHELLDLL_DefView", StringComparison.Ordinal) ||
                listView == IntPtr.Zero ||
                GetParent(listView) != shellViewHandle ||
                GetWindowProcessId(parentHandle) == 0 ||
                GetWindowProcessId(shellViewHandle) != GetWindowProcessId(parentHandle) ||
                GetWindowProcessId(listView) != GetWindowProcessId(parentHandle))
                return false;

            if (!GetWindowRect(windowHandle, out var originalRect))
                return false;

            originalState = new WindowRestoreState(
                originalParent,
                originalStyle,
                GetWindowLongPtr(windowHandle, GwlExStyle).ToInt64(),
                originalRect.Left,
                originalRect.Top,
                originalRect.Right - originalRect.Left,
                originalRect.Bottom - originalRect.Top);

            var topLevelStyle = WsPopup | WsCaption | WsThickFrame | WsSystemMenu | WsMinimizeBox | WsMaximizeBox;
            var childStyle = (originalStyle & ~topLevelStyle) | WsChild;
            SetLastError(0);
            if (SetWindowLongPtr(windowHandle, GwlStyle, new IntPtr(childStyle)) == IntPtr.Zero && Marshal.GetLastWin32Error() != 0)
            {
                RestoreTopLevelWindow(windowHandle, originalState, showWindow: false);
                return false;
            }

            if ((GetWindowLongPtr(windowHandle, GwlStyle).ToInt64() & WsChild) == 0)
            {
                RestoreTopLevelWindow(windowHandle, originalState, showWindow: false);
                return false;
            }

            SetLastError(0);
            var previousParent = SetParent(windowHandle, parentHandle);
            var error = Marshal.GetLastWin32Error();

            if (previousParent == IntPtr.Zero && error != 0)
            {
                RestoreTopLevelWindow(windowHandle, originalState, showWindow: false);
                return false;
            }

            if (GetParent(windowHandle) != parentHandle)
            {
                RestoreTopLevelWindow(windowHandle, originalState, showWindow: false);
                return false;
            }

            // Try per-pixel alpha while the child is still hidden. A failure falls
            // back to the original window style so desktop attachment still works.
            EnablePerPixelAlpha(windowHandle, originalState.ExtendedStyle);

            if (!PositionAboveShellView(windowHandle, parentHandle, shellViewHandle))
            {
                RestoreTopLevelWindow(windowHandle, originalState, showWindow: false);
                return false;
            }

            if (IsAboveSibling(windowHandle, shellViewHandle, parentHandle))
                return true;

            RestoreTopLevelWindow(windowHandle, originalState, showWindow: false);
            return false;
        }

        private static bool EnablePerPixelAlpha(IntPtr windowHandle, long originalExtendedStyle)
        {
            var currentExtendedStyle = GetWindowLongPtr(windowHandle, GwlExStyle).ToInt64();
            if ((currentExtendedStyle & WsExNoRedirectionBitmap) != 0)
            {
                System.Diagnostics.Debug.WriteLine(
                    "The WinUI desktop HWND has WS_EX_NOREDIRECTIONBITMAP; per-pixel DWM alpha may have no redirection surface to affect.");
            }

            SetLastError(0);
            if (SetWindowLongPtr(windowHandle, GwlExStyle, new IntPtr(currentExtendedStyle | WsExLayered)) == IntPtr.Zero &&
                Marshal.GetLastWin32Error() != 0)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Could not add WS_EX_LAYERED to the desktop HWND: {Marshal.GetLastWin32Error()}.");
                return false;
            }

            if ((GetWindowLongPtr(windowHandle, GwlExStyle).ToInt64() & WsExLayered) == 0 ||
                !SetLayeredWindowAttributes(windowHandle, 0, FullyOpaqueAlpha, LwaAlpha))
            {
                var error = Marshal.GetLastWin32Error();
                System.Diagnostics.Debug.WriteLine($"Could not initialize layered-window composition: {error}.");
                SetWindowLongPtr(windowHandle, GwlExStyle, new IntPtr(originalExtendedStyle));
                SetWindowPos(windowHandle, IntPtr.Zero, 0, 0, 0, 0,
                    SwpNoActivate | SwpNoZOrder | SwpFrameChanged | SwpNoMove | SwpNoSize);
                return false;
            }

            var alphaEnabled = 1;
            var result = DwmSetWindowAttribute(
                windowHandle,
                DwmwaRedirectionBitmapAlpha,
                ref alphaEnabled,
                sizeof(int));
            if (result < 0)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"DWM per-pixel alpha setup failed with HRESULT 0x{result:X8}.");
                SetWindowLongPtr(windowHandle, GwlExStyle, new IntPtr(originalExtendedStyle));
                SetWindowPos(windowHandle, IntPtr.Zero, 0, 0, 0, 0,
                    SwpNoActivate | SwpNoZOrder | SwpFrameChanged | SwpNoMove | SwpNoSize);
                return false;
            }

            // The desktop HWND becomes a child of Explorer. Ask DWM to expose
            // the host backdrop to system-backdrop visuals inside this surface.
            var hostBackdropEnabled = 1;
            var hostBackdropResult = DwmSetWindowAttribute(
                windowHandle,
                DwmwaUseHostBackdropBrush,
                ref hostBackdropEnabled,
                sizeof(int));
            if (hostBackdropResult < 0)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"DWM host backdrop setup failed with HRESULT 0x{hostBackdropResult:X8}.");
            }

            System.Diagnostics.Debug.WriteLine(
                "DWM per-pixel alpha enabled on the desktop HWND. WS_EX_NOREDIRECTIONBITMAP=" +
                (((currentExtendedStyle & WsExNoRedirectionBitmap) != 0) ? "true" : "false"));
            return true;
        }

        internal static bool IsAboveSibling(IntPtr windowHandle, IntPtr siblingHandle, IntPtr parentHandle)
        {
            if (!IsWindow(windowHandle) || !IsWindow(siblingHandle) ||
                GetParent(windowHandle) != parentHandle || GetParent(siblingHandle) != parentHandle)
            {
                return false;
            }

            var current = GetTopWindow(parentHandle);
            while (current != IntPtr.Zero)
            {
                if (current == windowHandle)
                    return true;
                if (current == siblingHandle)
                    return false;
                current = GetWindow(current, GwHwndNext);
            }

            return false;
        }

        internal static bool PositionAboveShellView(IntPtr windowHandle, IntPtr parentHandle, IntPtr shellViewHandle)
        {
            // This is a child HWND, so size it to the Explorer host's client area.
            // Using a primary-monitor rectangle here goes stale or maps incorrectly
            // when the display rotates, DPI changes, or Explorer adjusts its host.
            if (!GetClientRect(parentHandle, out var rect) ||
                rect.Right <= rect.Left || rect.Bottom <= rect.Top)
                return false;

            var positioned = SetWindowPos(
                windowHandle,
                HwndTop,
                0,
                0,
                rect.Right - rect.Left,
                rect.Bottom - rect.Top,
                SwpNoActivate | SwpFrameChanged | SwpShowWindow);

            return positioned && IsAboveSibling(windowHandle, shellViewHandle, parentHandle);
        }

        internal static void RestoreTopLevelWindow(IntPtr windowHandle, WindowRestoreState state, bool showWindow = true)
        {
            if (!IsWindow(windowHandle))
                return;

            var alphaDisabled = 0;
            _ = DwmSetWindowAttribute(
                windowHandle,
                DwmwaRedirectionBitmapAlpha,
                ref alphaDisabled,
                sizeof(int));
            _ = DwmSetWindowAttribute(
                windowHandle,
                DwmwaUseHostBackdropBrush,
                ref alphaDisabled,
                sizeof(int));

            var restoreParent = state.Parent;
            if (restoreParent != IntPtr.Zero && !IsWindow(restoreParent))
                restoreParent = IntPtr.Zero;

            SetLastError(0);
            var previousParent = SetParent(windowHandle, restoreParent);
            if (previousParent == IntPtr.Zero && Marshal.GetLastWin32Error() != 0 && restoreParent != IntPtr.Zero)
            {
                SetLastError(0);
                SetParent(windowHandle, IntPtr.Zero);
            }

            SetWindowLongPtr(windowHandle, GwlStyle, new IntPtr(state.Style));
            SetWindowLongPtr(windowHandle, GwlExStyle, new IntPtr(state.ExtendedStyle));
            var visibilityFlag = showWindow ? SwpShowWindow : SwpHideWindow;
            SetWindowPos(windowHandle, IntPtr.Zero, state.X, state.Y, state.Width, state.Height,
                SwpNoActivate | SwpNoZOrder | SwpFrameChanged | visibilityFlag);
        }

        private static IntPtr GetWindowLongPtr(IntPtr hwnd, int index) =>
            IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, index) : new IntPtr(GetWindowLong32(hwnd, index));

        private static IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value) =>
            IntPtr.Size == 8 ? SetWindowLongPtr64(hwnd, index, value) : new IntPtr(SetWindowLong32(hwnd, index, value.ToInt32()));

        private static IntPtr GetTopWindow(IntPtr hwnd) => GetTopWindowNative(hwnd);

        private static IntPtr GetWindow(IntPtr hwnd, uint command) => GetWindowNative(hwnd, command);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll", EntryPoint = "IsWindow")]
        private static extern bool IsWindowNative(IntPtr hwnd);
        [DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindWindowNative(string className, string windowName);
        [DllImport("user32.dll", EntryPoint = "FindWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindWindowExNative(IntPtr parent, IntPtr childAfter, string className, string windowName);
        [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(
            IntPtr window,
            uint message,
            IntPtr wParam,
            IntPtr lParam,
            uint flags,
            uint timeout,
            out IntPtr result);
        [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out NativePoint point);
        [DllImport("user32.dll", EntryPoint = "EnumWindows", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr lParam);
        private delegate bool EnumWindowsCallback(IntPtr window, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
        [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetClassNameNative(IntPtr hwnd, StringBuilder className, int maxCount);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetParent(IntPtr child, IntPtr newParent);
        [DllImport("user32.dll", EntryPoint = "GetParent", SetLastError = true)]
        private static extern IntPtr GetParent(IntPtr hwnd);
        [DllImport("user32.dll", EntryPoint = "ShowWindow")]
        private static extern bool ShowWindow(IntPtr hwnd, int command);
        [DllImport("user32.dll", EntryPoint = "IsWindowVisible")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisibleNative(IntPtr hwnd);
        [DllImport("user32.dll", EntryPoint = "GetTopWindow", SetLastError = true)]
        private static extern IntPtr GetTopWindowNative(IntPtr hwnd);
        [DllImport("user32.dll", EntryPoint = "GetWindow", SetLastError = true)]
        private static extern IntPtr GetWindowNative(IntPtr hwnd, uint command);
        [DllImport("user32.dll", EntryPoint = "GetWindowRect", SetLastError = true)]
        private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
        [DllImport("user32.dll", EntryPoint = "GetClientRect", SetLastError = true)]
        private static extern bool GetClientRect(IntPtr hwnd, out NativeRect rect);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
        private static extern int GetWindowLong32(IntPtr hwnd, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr value);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
        private static extern int SetWindowLong32(IntPtr hwnd, int index, int value);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint colorKey, byte alpha, uint flags);
        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);
        [DllImport("kernel32.dll", EntryPoint = "SetLastError")]
        private static extern void SetLastError(uint errorCode);
    }

    internal readonly record struct WindowRestoreState(
        IntPtr Parent,
        long Style,
        long ExtendedStyle,
        int X,
        int Y,
        int Width,
        int Height);
}

