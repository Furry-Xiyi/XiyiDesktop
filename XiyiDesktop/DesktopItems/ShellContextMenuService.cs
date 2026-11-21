using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using XiyiDesktop.Interop;

namespace XiyiDesktop.DesktopItems;

/// <summary>Displays the real Shell context menu for a single filesystem or namespace item.</summary>
internal static class ShellContextMenuService
{
    private const uint TrackPopupReturnCommand = 0x0100;
    private const uint TrackPopupRightButton = 0x0002;
    private const uint ContextMenuFirstCommand = 1;
    private const uint ContextMenuLastCommand = 0x7FFF;
    private const uint ContextMenuUnicode = 0x00004000;
    private const uint WmInitMenuPopup = 0x0117;
    private const uint WmDrawItem = 0x002B;
    private const uint WmMeasureItem = 0x002C;
    private const uint WmNcDestroy = 0x0082;
    private const uint MenuByPosition = 0x0400;
    private const int ShowNormal = 1;
    private static readonly object NewMenuOrderCacheLock = new();
    private static readonly Dictionary<string, (DateTimeOffset ExpiresUtc, IReadOnlyList<string> Items)> NewMenuOrderCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan NewMenuOrderCacheLifetime = TimeSpan.FromMinutes(5);

    private static readonly Guid ShellFolderInterfaceId = new("000214E6-0000-0000-C000-000000000046");
    private static readonly Guid ContextMenuInterfaceId = new("000214E4-0000-0000-C000-000000000046");
    private static readonly Guid ContextMenu2InterfaceId = new("000214F4-0000-0000-C000-000000000046");
    private static readonly Guid ContextMenu3InterfaceId = new("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719");
    private static readonly nuint ContextMenuSubclassId = (nuint)0x58445949;

    /// <summary>
    /// Shows the native Windows context menu for one file, folder, or Shell parsing name and invokes the
    /// selected Shell command. Multi-selection is intentionally left to the Shell's
    /// native desktop surface until a shared-parent selection adapter is available.
    /// </summary>
    public static unsafe bool ShowForItems(IReadOnlyList<string> paths, IntPtr ownerWindow)
    {
        if (paths is null || paths.Count != 1 || string.IsNullOrWhiteSpace(paths[0]))
            return false;

        ShellContextMenuTheme.Apply(ownerWindow);

        IntPtr absoluteItemIdList = IntPtr.Zero;
        IntPtr relativeIdListArray = IntPtr.Zero;
        IntPtr menu = IntPtr.Zero;
        IntPtr parentFolder = IntPtr.Zero;
        IntPtr contextMenu = IntPtr.Zero;
        MenuMessageHandler? menuMessages = null;
        NativeCom.ApartmentScope? apartment = null;
        try
        {
            apartment = NativeCom.InitializeApartment(2, allowDifferentApartment: true);
            var result = SHParseDisplayName(paths[0], IntPtr.Zero, out absoluteItemIdList, 0, IntPtr.Zero);
            if (result < 0 || absoluteItemIdList == IntPtr.Zero)
                return false;

            var shellFolderInterfaceId = ShellFolderInterfaceId;
            result = SHBindToParent(absoluteItemIdList, ref shellFolderInterfaceId, out parentFolder, out var childIdList);
            if (result < 0 || parentFolder == IntPtr.Zero || childIdList == IntPtr.Zero)
                return false;

            relativeIdListArray = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(relativeIdListArray, childIdList);
            var contextMenuInterfaceId = ContextMenuInterfaceId;
            // IShellFolder::GetUIObjectOf is slot 10, including IUnknown's methods.
            var getUiObject = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, IntPtr, Guid*, IntPtr, IntPtr*, int>)
                NativeCom.GetMethod(parentFolder, 10);
            result = getUiObject(
                parentFolder,
                ownerWindow,
                1,
                relativeIdListArray,
                &contextMenuInterfaceId,
                IntPtr.Zero,
                &contextMenu);
            if (result < 0 || contextMenu == IntPtr.Zero)
                return false;

            menuMessages = new MenuMessageHandler(contextMenu);
            menuMessages.Attach(ownerWindow);

            menu = CreatePopupMenu();
            if (menu == IntPtr.Zero)
                return false;

            var queryContextMenu = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, uint, int>)
                NativeCom.GetMethod(contextMenu, 3);
            result = queryContextMenu(
                contextMenu,
                menu,
                0,
                ContextMenuFirstCommand,
                ContextMenuLastCommand,
                0);
            if (result < 0)
                return false;

            if (!GetCursorPos(out var cursor))
                return false;

            SetForegroundWindow(ownerWindow);
            var commandId = TrackPopupMenuEx(
                menu,
                TrackPopupReturnCommand | TrackPopupRightButton,
                cursor.X,
                cursor.Y,
                ownerWindow,
                IntPtr.Zero);
            if (commandId == 0)
                return true;

            var commandInfo = new CommandInfoEx
            {
                Size = (uint)sizeof(CommandInfoEx),
                Mask = ContextMenuUnicode,
                Window = ownerWindow,
                Verb = new IntPtr(unchecked((int)(commandId - ContextMenuFirstCommand))),
                VerbUnicode = new IntPtr(unchecked((int)(commandId - ContextMenuFirstCommand))),
                Show = ShowNormal,
                InvokePoint = cursor
            };
            var invokeCommand = (delegate* unmanaged[Stdcall]<IntPtr, CommandInfoEx*, int>)
                NativeCom.GetMethod(contextMenu, 4);
            Marshal.ThrowExceptionForHR(invokeCommand(contextMenu, &commandInfo));
            return true;
        }
        catch (Exception exception) when (exception is COMException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Could not show the native Shell context menu for '{paths[0]}': {exception.Message}");
            return false;
        }
        finally
        {
            menuMessages?.Dispose();
            if (ownerWindow != IntPtr.Zero)
                PostMessage(ownerWindow, WmNull, IntPtr.Zero, IntPtr.Zero);
            if (menu != IntPtr.Zero)
                DestroyMenu(menu);
            if (relativeIdListArray != IntPtr.Zero)
                Marshal.FreeHGlobal(relativeIdListArray);
            if (absoluteItemIdList != IntPtr.Zero)
                Marshal.FreeCoTaskMem(absoluteItemIdList);
            NativeCom.Release(contextMenu);
            NativeCom.Release(parentFolder);
            apartment?.Dispose();
        }
    }

    /// <summary>Shows the classic Shell context menu for the desktop folder background.</summary>
    public static unsafe bool ShowForDesktopBackground(IntPtr ownerWindow)
    {
        if (ownerWindow == IntPtr.Zero)
            return false;

        ShellContextMenuTheme.Apply(ownerWindow);

        IntPtr contextMenu = IntPtr.Zero;
        IntPtr desktopFolder = IntPtr.Zero;
        MenuMessageHandler? menuMessages = null;
        IntPtr menu = IntPtr.Zero;
        NativeCom.ApartmentScope? apartment = null;
        try
        {
            apartment = NativeCom.InitializeApartment(2, allowDifferentApartment: true);
            var result = SHGetDesktopFolder(out desktopFolder);
            if (result < 0 || desktopFolder == IntPtr.Zero)
                return false;

            var contextMenuInterfaceId = ContextMenuInterfaceId;
            // IShellFolder::CreateViewObject is slot 8. Asking for IContextMenu
            // gives the desktop folder's classic background menu directly.
            var createViewObject = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)
                NativeCom.GetMethod(desktopFolder, 8);
            result = createViewObject(desktopFolder, ownerWindow, &contextMenuInterfaceId, &contextMenu);
            if (result < 0 || contextMenu == IntPtr.Zero)
                return false;

            menuMessages = new MenuMessageHandler(contextMenu);
            menuMessages.Attach(ownerWindow);
            menu = CreatePopupMenu();
            if (menu == IntPtr.Zero)
                return false;

            var queryContextMenu = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, uint, int>)
                NativeCom.GetMethod(contextMenu, 3);
            result = queryContextMenu(contextMenu, menu, 0, ContextMenuFirstCommand, ContextMenuLastCommand, 0);
            if (result < 0 || !GetCursorPos(out var cursor))
                return false;

            SetForegroundWindow(ownerWindow);
            var commandId = TrackPopupMenuEx(
                menu,
                TrackPopupReturnCommand | TrackPopupRightButton,
                cursor.X,
                cursor.Y,
                ownerWindow,
                IntPtr.Zero);
            if (commandId == 0)
                return true;

            var commandInfo = new CommandInfoEx
            {
                Size = (uint)sizeof(CommandInfoEx),
                Mask = ContextMenuUnicode,
                Window = ownerWindow,
                Verb = new IntPtr(unchecked((int)(commandId - ContextMenuFirstCommand))),
                VerbUnicode = new IntPtr(unchecked((int)(commandId - ContextMenuFirstCommand))),
                Show = ShowNormal,
                InvokePoint = cursor
            };
            var invokeCommand = (delegate* unmanaged[Stdcall]<IntPtr, CommandInfoEx*, int>)
                NativeCom.GetMethod(contextMenu, 4);
            Marshal.ThrowExceptionForHR(invokeCommand(contextMenu, &commandInfo));
            return true;
        }
        catch (Exception exception) when (exception is COMException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Could not show the classic desktop background context menu: {exception.Message}");
            return false;
        }
        finally
        {
            menuMessages?.Dispose();
            PostMessage(ownerWindow, WmNull, IntPtr.Zero, IntPtr.Zero);
            if (menu != IntPtr.Zero)
                DestroyMenu(menu);
            NativeCom.Release(contextMenu);
            NativeCom.Release(desktopFolder);
            apartment?.Dispose();
        }
    }

    /// <summary>Reads the current Shell-generated desktop New submenu order.</summary>
    public static unsafe IReadOnlyList<string> GetDesktopNewMenuOrder(string newMenuText)
    {
        if (string.IsNullOrWhiteSpace(newMenuText))
            return [];

        var cacheKey = newMenuText.Trim();
        lock (NewMenuOrderCacheLock)
        {
            if (NewMenuOrderCache.TryGetValue(cacheKey, out var cached) && DateTimeOffset.UtcNow < cached.ExpiresUtc)
                return cached.Items;
        }

        IReadOnlyList<string> items;
        try
        {
            items = ReadDesktopNewMenuOrder(cacheKey);
        }
        catch (Exception exception) when (exception is COMException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Could not read the Shell New submenu order: {exception.Message}");
            items = [];
        }

        lock (NewMenuOrderCacheLock)
            NewMenuOrderCache[cacheKey] = (DateTimeOffset.UtcNow + NewMenuOrderCacheLifetime, items);
        return items;
    }

    private static unsafe IReadOnlyList<string> ReadDesktopNewMenuOrder(string newMenuText)
    {
        using var apartment = NativeCom.InitializeApartment(2, allowDifferentApartment: true);
        IntPtr desktopFolder = IntPtr.Zero;
        IntPtr contextMenu = IntPtr.Zero;
        IntPtr menu = IntPtr.Zero;
        IntPtr contextMenu2 = IntPtr.Zero;
        IntPtr contextMenu3 = IntPtr.Zero;
        try
        {
            var result = SHGetDesktopFolder(out desktopFolder);
            if (result < 0 || desktopFolder == IntPtr.Zero)
                return [];

            var contextMenuInterfaceId = ContextMenuInterfaceId;
            var createViewObject = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)
                NativeCom.GetMethod(desktopFolder, 8);
            result = createViewObject(desktopFolder, IntPtr.Zero, &contextMenuInterfaceId, &contextMenu);
            if (result < 0 || contextMenu == IntPtr.Zero)
                return [];

            menu = CreatePopupMenu();
            if (menu == IntPtr.Zero)
                return [];

            var queryContextMenu = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, uint, int>)
                NativeCom.GetMethod(contextMenu, 3);
            result = queryContextMenu(contextMenu, menu, 0, ContextMenuFirstCommand, ContextMenuLastCommand, 0);
            if (result < 0)
                return [];

            contextMenu3 = NativeCom.TryQueryInterface(contextMenu, ContextMenu3InterfaceId);
            if (contextMenu3 == IntPtr.Zero)
                contextMenu2 = NativeCom.TryQueryInterface(contextMenu, ContextMenu2InterfaceId);

            var newMenuIndex = -1;
            var newMenuHandle = IntPtr.Zero;
            var topLevelCount = GetMenuItemCount(menu);
            for (var index = 0; index < topLevelCount; index++)
            {
                var text = GetMenuItemText(menu, index);
                if (!string.Equals(NormalizeMenuText(text), NormalizeMenuText(newMenuText), StringComparison.CurrentCultureIgnoreCase))
                    continue;

                newMenuIndex = index;
                newMenuHandle = GetSubMenu(menu, index);
                break;
            }

            if (newMenuIndex < 0 || newMenuHandle == IntPtr.Zero)
                return [];

            var menuMessageInterface = contextMenu3 != IntPtr.Zero ? contextMenu3 : contextMenu2;
            if (menuMessageInterface != IntPtr.Zero)
            {
                var handleMenuMessage = (delegate* unmanaged[Stdcall]<IntPtr, uint, UIntPtr, IntPtr, int>)
                    NativeCom.GetMethod(menuMessageInterface, 6);
                _ = handleMenuMessage(
                    menuMessageInterface,
                    WmInitMenuPopup,
                    (UIntPtr)newMenuHandle,
                    new IntPtr(newMenuIndex));
            }

            var itemCount = GetMenuItemCount(newMenuHandle);
            var items = new List<string>(Math.Max(0, itemCount));
            for (var index = 0; index < itemCount; index++)
            {
                var text = NormalizeMenuText(GetMenuItemText(newMenuHandle, index));
                if (!string.IsNullOrWhiteSpace(text))
                    items.Add(text);
            }
            return items;
        }
        finally
        {
            if (menu != IntPtr.Zero)
                DestroyMenu(menu);
            NativeCom.Release(contextMenu3);
            NativeCom.Release(contextMenu2);
            NativeCom.Release(contextMenu);
            NativeCom.Release(desktopFolder);
        }
    }

    private static string NormalizeMenuText(string text)
    {
        var acceleratorStart = text.IndexOf("(&", StringComparison.Ordinal);
        if (acceleratorStart >= 0)
        {
            var acceleratorEnd = text.IndexOf(')', acceleratorStart);
            if (acceleratorEnd > acceleratorStart)
                text = text.Remove(acceleratorStart, acceleratorEnd - acceleratorStart + 1);
        }

        return text.Replace("&&", "\0", StringComparison.Ordinal)
            .Replace("&", string.Empty, StringComparison.Ordinal)
            .Replace("\0", "&", StringComparison.Ordinal)
            .Trim()
            .TrimEnd('.', '…');
    }

    private static string GetMenuItemText(IntPtr menu, int position)
    {
        var buffer = new System.Text.StringBuilder(512);
        var length = GetMenuString(menu, (uint)position, buffer, buffer.Capacity, MenuByPosition);
        return length > 0 ? buffer.ToString(0, length) : string.Empty;
    }

    private static unsafe IntPtr SubclassCallback =>
        (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, UIntPtr, IntPtr, UIntPtr, UIntPtr, IntPtr>)&MenuSubclassProcedure;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static IntPtr MenuSubclassProcedure(
        IntPtr window, uint message, UIntPtr wParam, IntPtr lParam, UIntPtr subclassId, UIntPtr referenceData)
    {
        try
        {
            var handle = GCHandle.FromIntPtr((IntPtr)referenceData);
            if (handle.Target is MenuMessageHandler handler)
            {
                if (message == WmNcDestroy)
                {
                    var result = DefSubclassProc(window, message, wParam, lParam);
                    handler.OnWindowDestroyed();
                    return result;
                }
                if (handler.TryHandleMessage(message, wParam, lParam, out var handledResult))
                    return handledResult;
            }
        }
        catch (Exception exception)
        {
            // Managed exceptions must never cross the native window-procedure boundary.
            Debug.WriteLine($"Could not process a Shell menu message: {exception.Message}");
        }
        return DefSubclassProc(window, message, wParam, lParam);
    }

    private sealed class MenuMessageHandler : IDisposable
    {
        private IntPtr _contextMenu2;
        private IntPtr _contextMenu3;
        private IntPtr _window;
        private GCHandle _handle;

        public MenuMessageHandler(IntPtr contextMenu)
        {
            _contextMenu3 = NativeCom.TryQueryInterface(contextMenu, ContextMenu3InterfaceId);
            if (_contextMenu3 == IntPtr.Zero)
                _contextMenu2 = NativeCom.TryQueryInterface(contextMenu, ContextMenu2InterfaceId);
        }

        public void Attach(IntPtr window)
        {
            if (window == IntPtr.Zero || (_contextMenu2 == IntPtr.Zero && _contextMenu3 == IntPtr.Zero))
                return;

            _handle = GCHandle.Alloc(this);
            if (SetWindowSubclass(window, SubclassCallback, ContextMenuSubclassId, (UIntPtr)GCHandle.ToIntPtr(_handle)))
                _window = window;
            else
                _handle.Free();
        }

        public unsafe bool TryHandleMessage(uint message, UIntPtr wParam, IntPtr lParam, out IntPtr result)
        {
            result = IntPtr.Zero;
            if (message is not (WmInitMenuPopup or WmDrawItem or WmMeasureItem))
                return false;

            if (_contextMenu3 != IntPtr.Zero)
            {
                IntPtr nativeResult = IntPtr.Zero;
                var handleMessage2 = (delegate* unmanaged[Stdcall]<IntPtr, uint, UIntPtr, IntPtr, IntPtr*, int>)
                    NativeCom.GetMethod(_contextMenu3, 7);
                if (handleMessage2(_contextMenu3, message, wParam, lParam, &nativeResult) >= 0)
                {
                    result = nativeResult;
                    return true;
                }
            }

            // IContextMenu3 inherits IContextMenu2::HandleMenuMsg at slot 6.
            var messageHandler = _contextMenu3 != IntPtr.Zero ? _contextMenu3 : _contextMenu2;
            if (messageHandler == IntPtr.Zero)
                return false;
            var handleMessage = (delegate* unmanaged[Stdcall]<IntPtr, uint, UIntPtr, IntPtr, int>)
                NativeCom.GetMethod(messageHandler, 6);
            return handleMessage(messageHandler, message, wParam, lParam) >= 0;
        }

        public void OnWindowDestroyed()
        {
            _window = IntPtr.Zero;
            Dispose();
        }

        public void Dispose()
        {
            if (_window != IntPtr.Zero &&
                !RemoveWindowSubclass(_window, SubclassCallback, ContextMenuSubclassId) && IsWindow(_window))
            {
                // Keep the callback state alive until WM_NCDESTROY if the subclass still exists.
                return;
            }
            _window = IntPtr.Zero;
            NativeCom.Release(_contextMenu2);
            NativeCom.Release(_contextMenu3);
            _contextMenu2 = IntPtr.Zero;
            _contextMenu3 = IntPtr.Zero;
            if (_handle.IsAllocated)
                _handle.Free();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CommandInfoEx
    {
        public uint Size;
        public uint Mask;
        public IntPtr Window;
        public IntPtr Verb;
        public IntPtr Parameters;
        public IntPtr Directory;
        public int Show;
        public uint HotKey;
        public IntPtr Icon;
        public IntPtr Title;
        public IntPtr VerbUnicode;
        public IntPtr ParametersUnicode;
        public IntPtr DirectoryUnicode;
        public IntPtr TitleUnicode;
        public Point InvokePoint;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHParseDisplayName(string name, IntPtr bindContext, out IntPtr itemIdList, uint attributesIn, IntPtr attributesOut);

    [DllImport("shell32.dll", PreserveSig = true)]
    private static extern int SHGetDesktopFolder(out IntPtr desktopFolder);

    [DllImport("shell32.dll", PreserveSig = true)]
    private static extern int SHBindToParent(IntPtr itemIdList, ref Guid interfaceId, out IntPtr parentFolder, out IntPtr childIdList);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetMenuItemCount(IntPtr menu);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetSubMenu(IntPtr menu, int position);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetMenuString(IntPtr menu, uint item, System.Text.StringBuilder text, int maxCount, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr owner, IntPtr parameters);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    private const uint WmNull = 0x0000;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(
        IntPtr window,
        IntPtr procedure,
        nuint subclassId,
        UIntPtr referenceData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(IntPtr window, IntPtr procedure, nuint subclassId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);
}
