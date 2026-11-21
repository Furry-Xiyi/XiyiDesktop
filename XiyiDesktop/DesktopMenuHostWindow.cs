using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Runtime.InteropServices;
using WinRT;

namespace XiyiDesktop;

/// <summary>Provides a normal top-level HWND/XamlRoot for transient desktop menus.</summary>
internal sealed class DesktopMenuHostWindow : Window
{
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const long WsPopup = unchecked((long)0x80000000L);
    private const long WsCaption = 0x00C00000L;
    private const long WsThickFrame = 0x00040000L;
    private const long WsSystemMenu = 0x00080000L;
    private const long WsMinimizeBox = 0x00020000L;
    private const long WsMaximizeBox = 0x00010000L;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExLayered = 0x00080000L;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpShowWindow = 0x0040;
    private const int SwHide = 0;
    private const uint LwaAlpha = 0x00000002;
    private const byte FullyTransparentAlpha = 0;
    private const int DwmwaUseHostBackdropBrush = 17;
    private const int DwmwaRedirectionBitmapAlpha = 39;
    private static readonly IntPtr HwndTop = IntPtr.Zero;

    private readonly IntPtr _windowHandle;
    private readonly Grid _root;
    private Windows.UI.Composition.CompositionColorBrush? _transparentBrush;
    private bool _closed;

    public DesktopMenuHostWindow()
    {
        _root = new Grid { Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        Content = _root;
        _windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        ConfigureWindow();
    }

    public Grid Root => _root;

    public IntPtr WindowHandle => _windowHandle;

    public void ShowAtScreenPoint(int x, int y)
    {
        if (_closed || _windowHandle == IntPtr.Zero)
            return;

        SetWindowPos(
            _windowHandle,
            HwndTop,
            x,
            y,
            1,
            1,
            SwpNoActivate | SwpFrameChanged | SwpShowWindow);
        Activate();
        SetWindowPos(
            _windowHandle,
            HwndTop,
            x,
            y,
            1,
            1,
            SwpNoActivate | SwpFrameChanged | SwpShowWindow);
    }

    public void HideHost()
    {
        if (!_closed && _windowHandle != IntPtr.Zero)
            ShowWindow(_windowHandle, SwHide);
    }

    public void EnableTransparentBackdrop()
    {
        if (_closed || _transparentBrush is not null)
            return;

        try
        {
            DispatcherQueue.GetForCurrentThread()?.EnsureSystemDispatcherQueue();
            var target = this.As<Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop>();
            _transparentBrush = new Windows.UI.Composition.Compositor().CreateColorBrush(
                Windows.UI.Color.FromArgb(0, 0, 0, 0));
            target.SystemBackdrop = _transparentBrush;
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"Could not make the menu host transparent: {exception.Message}");
        }
    }

    public void CloseHost()
    {
        if (_closed)
            return;

        _closed = true;
        try
        {
            this.As<Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop>().SystemBackdrop = null;
            _transparentBrush?.Dispose();
            _transparentBrush = null;
        }
        catch (Exception exception) when (exception is COMException or ObjectDisposedException)
        {
            System.Diagnostics.Debug.WriteLine($"Could not release the menu host backdrop: {exception.Message}");
        }

        try { Close(); } catch (Exception exception) when (exception is COMException or InvalidOperationException) { }
    }

    private void ConfigureWindow()
    {
        var style = GetWindowLongPtr(_windowHandle, GwlStyle).ToInt64();
        var overlappedStyle = WsPopup | WsCaption | WsThickFrame | WsSystemMenu | WsMinimizeBox | WsMaximizeBox;
        SetWindowLongPtr(_windowHandle, GwlStyle, new IntPtr((style & ~overlappedStyle) | WsPopup));

        var extendedStyle = GetWindowLongPtr(_windowHandle, GwlExStyle).ToInt64();
        extendedStyle |= WsExToolWindow | WsExLayered;
        SetWindowLongPtr(_windowHandle, GwlExStyle, new IntPtr(extendedStyle));
        // This HWND only provides the top-level XamlRoot/backdrop context. Its
        // own 1x1 surface must not leave a visible pixel at the invocation point.
        _ = SetLayeredWindowAttributes(_windowHandle, 0, FullyTransparentAlpha, LwaAlpha);

        var enabled = 1;
        LogDwmAttributeResult(
            DwmwaUseHostBackdropBrush,
            DwmSetWindowAttribute(_windowHandle, DwmwaUseHostBackdropBrush, ref enabled, sizeof(int)));
        LogDwmAttributeResult(
            DwmwaRedirectionBitmapAlpha,
            DwmSetWindowAttribute(_windowHandle, DwmwaRedirectionBitmapAlpha, ref enabled, sizeof(int)));
    }

    private static void LogDwmAttributeResult(int attribute, int result)
    {
        if (result < 0)
            System.Diagnostics.Debug.WriteLine($"Could not set DWM window attribute {attribute}: HRESULT 0x{result:X8}.");
    }

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

    [DllImport("user32.dll", EntryPoint = "ShowWindow")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint colorKey, byte alpha, uint flags);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);

    private static IntPtr GetWindowLongPtr(IntPtr hwnd, int index) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, index) : new IntPtr(GetWindowLong32(hwnd, index));

    private static IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value) =>
        IntPtr.Size == 8 ? SetWindowLongPtr64(hwnd, index, value) : new IntPtr(SetWindowLong32(hwnd, index, value.ToInt32()));
}
