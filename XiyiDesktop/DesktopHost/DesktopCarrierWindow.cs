using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace XiyiDesktop.DesktopHost;

/// <summary>Creates a borderless, non-painting HWND used only as a parent for the desktop XAML Island.</summary>
internal static unsafe class DesktopCarrierWindow
{
    private const uint WsPopup = 0x80000000;
    private const uint WsClipChildren = 0x02000000;
    private const uint WsClipSiblings = 0x04000000;
    private const uint WmEraseBackground = 0x0014;
    private const uint WmPaint = 0x000F;
    private const uint ErrorClassAlreadyExists = 1410;
    private const string ClassName = "XiyiDesktop.NativeIslandHost";
    private static readonly object RegistrationLock = new();
    private static bool _classRegistered;

    public static IntPtr Create()
    {
        RegisterWindowClass();
        var instance = GetModuleHandle(null);
        var hwnd = CreateWindowEx(
            0,
            ClassName,
            string.Empty,
            WsPopup | WsClipChildren | WsClipSiblings,
            0,
            0,
            1,
            1,
            IntPtr.Zero,
            IntPtr.Zero,
            instance,
            IntPtr.Zero);
        if (hwnd == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the desktop XAML Island host HWND.");
        return hwnd;
    }

    public static void Destroy(IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero && IsWindow(hwnd))
            _ = DestroyWindow(hwnd);
    }

    private static void RegisterWindowClass()
    {
        if (_classRegistered)
            return;

        lock (RegistrationLock)
        {
            if (_classRegistered)
                return;

            var windowClass = new WindowClassEx
            {
                Size = (uint)Marshal.SizeOf<WindowClassEx>(),
                WindowProcedure = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, UIntPtr, IntPtr, IntPtr>)&WindowProcedure,
                Instance = GetModuleHandle(null),
                ClassName = ClassName
            };

            if (RegisterClassEx(ref windowClass) == 0 && Marshal.GetLastWin32Error() != ErrorClassAlreadyExists)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not register the desktop XAML Island host window class.");

            _classRegistered = true;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static IntPtr WindowProcedure(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam)
    {
        if (message == WmEraseBackground)
            return new IntPtr(1);

        if (message == WmPaint)
        {
            _ = BeginPaint(hwnd, out var paint);
            _ = EndPaint(hwnd, ref paint);
            return IntPtr.Zero;
        }

        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClassEx
    {
        public uint Size;
        public uint Style;
        public IntPtr WindowProcedure;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr BackgroundBrush;
        public string? MenuName;
        public string ClassName;
        public IntPtr SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct PaintStruct
    {
        public IntPtr DeviceContext;
        public int Erase;
        public NativeRect Paint;
        public int Restore;
        public int IncrementalUpdate;
        public fixed byte Reserved[32];
    }

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WindowClassEx windowClass);

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr BeginPaint(IntPtr hwnd, out PaintStruct paint);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EndPaint(IntPtr hwnd, ref PaintStruct paint);

}
