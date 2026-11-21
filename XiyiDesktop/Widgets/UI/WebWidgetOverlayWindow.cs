using Microsoft.Web.WebView2.Core;
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Foundation;

namespace XiyiDesktop.Widgets.UI;

/// <summary>
/// A regular native child host for WebView2, positioned beneath the layered
/// desktop surface so the original widget header remains the input/drag layer.
/// </summary>
internal sealed class WebWidgetOverlayWindow : IDisposable
{
    private const uint WsChild = 0x40000000;
    private const uint WsVisible = 0x10000000;
    private const uint WsClipSiblings = 0x04000000;
    private const uint WsClipChildren = 0x02000000;
    private const int GwChild = 5;
    private const int GwHwndNext = 2;
    private const int GwlStyle = -16;
    private const uint WsVisibleFlag = 0x10000000;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const int SwHide = 0;

    private readonly IntPtr _siblingAboveHwnd;
    private CoreWebView2Controller? _controller;
    private bool _isDisposed;
    private Rect _lastBounds;
    private Uri _uri;
    private int _lastRoundedWidth;
    private int _lastRoundedHeight;
    private int _lastCornerDiameter;
    private double _lastCornerRadiusPixels;
    private bool _hasCornerRadius;

    public event EventHandler<Exception>? InitializationFailed;
    public event EventHandler<CoreWebView2NavigationCompletedEventArgs>? NavigationCompleted;

    public IntPtr ParentHwnd { get; }
    public IntPtr HostHwnd { get; }
    public IntPtr SiblingAboveHwnd => _siblingAboveHwnd;

    public bool IsHostedBy(IntPtr parentHwnd, IntPtr siblingAboveHwnd) =>
        ParentHwnd == parentHwnd && _siblingAboveHwnd == siblingAboveHwnd;

    public WebWidgetOverlayWindow(IntPtr parentHwnd, IntPtr siblingAboveHwnd, Uri uri)
    {
        ValidateWindow(parentHwnd, nameof(parentHwnd));
        ValidateWindow(siblingAboveHwnd, nameof(siblingAboveHwnd));
        ArgumentNullException.ThrowIfNull(uri);
        if (uri.Scheme is not ("https" or "http"))
            throw new ArgumentException("Web widget URLs must use HTTP or HTTPS.", nameof(uri));

        ParentHwnd = parentHwnd;
        _siblingAboveHwnd = siblingAboveHwnd;
        _uri = uri;
        HostHwnd = CreateWindowEx(
            0,
            "STATIC",
            string.Empty,
            WsChild | WsVisible | WsClipSiblings | WsClipChildren,
            0,
            0,
            1,
            1,
            parentHwnd,
            IntPtr.Zero,
            GetModuleHandle(null),
            IntPtr.Zero);
        if (HostHwnd == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

        _ = InitializeWebViewAsync();
    }

    public void UpdateBounds(Rect screenBounds, double cornerRadiusPixels)
    {
        if (_isDisposed || !double.IsFinite(screenBounds.X) || !double.IsFinite(screenBounds.Y) ||
            !double.IsFinite(screenBounds.Width) || !double.IsFinite(screenBounds.Height))
            return;

        _lastBounds = screenBounds;
        var origin = new NativePoint
        {
            X = checked((int)Math.Round(screenBounds.X)),
            Y = checked((int)Math.Round(screenBounds.Y))
        };
        if (!ScreenToClient(ParentHwnd, ref origin))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

        var width = Math.Max(1, checked((int)Math.Round(screenBounds.Width)));
        var height = Math.Max(1, checked((int)Math.Round(screenBounds.Height)));
        _lastCornerRadiusPixels = cornerRadiusPixels;
        _hasCornerRadius = true;
        if (!SetWindowPos(HostHwnd, _siblingAboveHwnd, origin.X, origin.Y, width, height,
            SwpNoActivate | SwpShowWindow))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

        if (_controller is not null)
        {
            _controller.Bounds = new Rect(0, 0, width, height);
            ApplyRoundedWindowRegions(width, height, cornerRadiusPixels);
        }
    }

    public void SetUri(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (uri.Scheme is not ("https" or "http"))
            throw new ArgumentException("Web widget URLs must use HTTP or HTTPS.", nameof(uri));

        _uri = uri;
        if (_controller is not null)
            _controller.CoreWebView2.Navigate(uri.AbsoluteUri);
    }

    public void HideOverlay()
    {
        if (!_isDisposed)
            ShowWindow(HostHwnd, SwHide);
    }

    public void CloseOverlay()
    {
        if (_isDisposed)
            return;
        Dispose();
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync();
            if (_isDisposed)
                return;

            var hostReference = CoreWebView2ControllerWindowReference.CreateFromWindowHandle(
                unchecked((ulong)HostHwnd.ToInt64()));
            var controller = await environment.CreateCoreWebView2ControllerAsync(hostReference);
            if (_isDisposed)
            {
                controller.Close();
                return;
            }

            _controller = controller;
            var core = controller.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.NavigationCompleted += Core_NavigationCompleted;
            controller.IsVisible = true;

            var width = Math.Max(1, checked((int)Math.Round(_lastBounds.Width)));
            var height = Math.Max(1, checked((int)Math.Round(_lastBounds.Height)));
            controller.Bounds = new Rect(0, 0, width, height);
            var dpi = GetDpiForWindow(HostHwnd);
            var scale = dpi > 0 ? dpi / 96d : 1d;
            ApplyRoundedWindowRegions(width, height, _hasCornerRadius
                ? _lastCornerRadiusPixels
                : 8 * scale);
            core.Navigate(_uri.AbsoluteUri);
        }
        catch (Exception exception)
        {
            if (!_isDisposed)
            {
                System.Diagnostics.Debug.WriteLine($"WebView2 initialization failed in the native card host: {exception}");
                InitializationFailed?.Invoke(this, exception);
            }
        }
    }

    private void Core_NavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (_lastRoundedWidth > 0 && _lastRoundedHeight > 0 && _hasCornerRadius)
            ApplyRoundedWindowRegions(_lastRoundedWidth, _lastRoundedHeight, _lastCornerRadiusPixels, force: true);
        NavigationCompleted?.Invoke(this, args);
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        if (_controller is not null)
        {
            _controller.CoreWebView2.NavigationCompleted -= Core_NavigationCompleted;
            try { _controller.CoreWebView2.Navigate("about:blank"); } catch { }
            _controller.Close();
            _controller = null;
        }

        if (IsWindow(HostHwnd))
            DestroyWindow(HostHwnd);
    }

    private static void ValidateWindow(IntPtr hwnd, string parameterName)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
            throw new ArgumentException("A valid desktop HWND is required.", parameterName);
    }

    private void ApplyRoundedWindowRegions(int width, int height, double cornerRadiusPixels, bool force = false)
    {
        _lastCornerRadiusPixels = cornerRadiusPixels;
        _hasCornerRadius = true;
        if (cornerRadiusPixels <= 0)
        {
            if (!force && _lastRoundedWidth == width && _lastRoundedHeight == height && _lastCornerDiameter == 0)
                return;

            SetWindowRgn(HostHwnd, IntPtr.Zero, redraw: true);
            for (var child = GetWindow(HostHwnd, GwChild); child != IntPtr.Zero; child = GetWindow(child, GwHwndNext))
            {
                var style = GetWindowLongPtr(child, GwlStyle).ToInt64();
                if ((style & WsVisibleFlag) == 0 || !GetClientRect(child, out var childClient))
                    continue;

                var childWidth = childClient.Right - childClient.Left;
                var childHeight = childClient.Bottom - childClient.Top;
                if (childWidth < width * 0.9 || childHeight < height * 0.9)
                    continue;

                SetWindowRgn(child, IntPtr.Zero, redraw: true);
            }

            _lastRoundedWidth = width;
            _lastRoundedHeight = height;
            _lastCornerDiameter = 0;
            return;
        }

        var diameter = Math.Clamp(
            checked((int)Math.Round(Math.Max(1, cornerRadiusPixels) * 2)),
            2,
            Math.Max(2, Math.Min(width, height)));
        if (!force && _lastRoundedWidth == width && _lastRoundedHeight == height && _lastCornerDiameter == diameter)
            return;

        try
        {
            ApplyRoundedRegion(HostHwnd, width, height, diameter);

            // CoreWebView2Controller creates its own child HWND. Apply the same
            // region to that HWND too, since child pixels may not inherit the
            // parent host's region on every WebView2 runtime version.
            for (var child = GetWindow(HostHwnd, GwChild); child != IntPtr.Zero; child = GetWindow(child, GwHwndNext))
            {
                var style = GetWindowLongPtr(child, GwlStyle).ToInt64();
                if ((style & WsVisibleFlag) == 0 || !GetClientRect(child, out var childClient))
                    continue;

                var childWidth = childClient.Right - childClient.Left;
                var childHeight = childClient.Bottom - childClient.Top;
                if (childWidth < width * 0.9 || childHeight < height * 0.9)
                    continue;

                ApplyRoundedRegion(child, childWidth, childHeight, diameter);
            }

            _lastRoundedWidth = width;
            _lastRoundedHeight = height;
            _lastCornerDiameter = diameter;
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Could not apply rounded corners to WebView2 child windows: {exception.Message}");
        }
    }

    private static void ApplyRoundedRegion(IntPtr hwnd, int width, int height, int diameter)
    {
        var region = CreateRoundRectRgn(0, 0, width + 1, height + 1, diameter, diameter);
        if (region == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

        if (SetWindowRgn(hwnd, region, redraw: true) == 0)
        {
            var error = Marshal.GetLastWin32Error();
            DeleteObject(region);
            throw new System.ComponentModel.Win32Exception(error);
        }
    }

    private static IntPtr GetWindowLongPtr(IntPtr hwnd, int index) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, index) : new IntPtr(GetWindowLong32(hwnd, index));

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        int extendedStyle,
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

    [DllImport("user32.dll", EntryPoint = "IsWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", EntryPoint = "DestroyWindow", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(IntPtr hwnd, ref NativePoint point);

    [DllImport("user32.dll", EntryPoint = "SetWindowPos", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", EntryPoint = "ShowWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll", EntryPoint = "GetWindow", SetLastError = true)]
    private static extern IntPtr GetWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll", EntryPoint = "GetClientRect", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hwnd, int index);

    [DllImport("gdi32.dll", EntryPoint = "CreateRoundRectRgn", SetLastError = true)]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);

    [DllImport("user32.dll", EntryPoint = "SetWindowRgn", SetLastError = true)]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [DllImport("gdi32.dll", EntryPoint = "DeleteObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("user32.dll", EntryPoint = "GetDpiForWindow", SetLastError = true)]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
