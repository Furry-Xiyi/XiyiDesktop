using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace XiyiDesktop.DesktopItems;

/// <summary>Applies the current Windows app theme to native Shell popup menus.</summary>
internal static class ShellContextMenuTheme
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const int PreferredAppModeAllowDark = 1;
    private const int PreferredAppModeForceLight = 3;
    private const uint DwmwaUseImmersiveDarkMode = 20;
    private const uint DwmwaUseImmersiveDarkModeLegacy = 19;

    public static void Apply(IntPtr ownerWindow)
    {
        var isDark = ReadSystemDarkMode();
        ApplyUxThemeMenuMode(ownerWindow, isDark);

        if (ownerWindow == IntPtr.Zero)
            return;

        var darkMode = isDark ? 1 : 0;
        try
        {
            var result = DwmSetWindowAttribute(ownerWindow, DwmwaUseImmersiveDarkMode, ref darkMode, sizeof(int));
            if (result < 0)
                _ = DwmSetWindowAttribute(ownerWindow, DwmwaUseImmersiveDarkModeLegacy, ref darkMode, sizeof(int));
        }
        catch (EntryPointNotFoundException) { }
        catch (DllNotFoundException) { }

    }

    private static unsafe void ApplyUxThemeMenuMode(IntPtr ownerWindow, bool isDark)
    {
        var module = GetModuleHandle("uxtheme.dll");
        var ownsModuleReference = false;
        if (module == IntPtr.Zero)
        {
            module = LoadLibrary("uxtheme.dll");
            ownsModuleReference = module != IntPtr.Zero;
        }
        if (module == IntPtr.Zero)
            return;

        try
        {
            var refresh = GetProcAddress(module, new IntPtr(104));
            if (refresh != IntPtr.Zero)
                ((delegate* unmanaged[Stdcall]<void>)refresh)();

            var setAppMode = GetProcAddress(module, new IntPtr(135));
            if (setAppMode != IntPtr.Zero)
                _ = ((delegate* unmanaged[Stdcall]<int, int>)setAppMode)(
                    isDark ? PreferredAppModeAllowDark : PreferredAppModeForceLight);

            var setWindowMode = GetProcAddress(module, new IntPtr(133));
            if (setWindowMode != IntPtr.Zero && ownerWindow != IntPtr.Zero)
                _ = ((delegate* unmanaged[Stdcall]<IntPtr, int, int>)setWindowMode)(ownerWindow, isDark ? 1 : 0);

            var flush = GetProcAddress(module, new IntPtr(136));
            if (flush != IntPtr.Zero)
                ((delegate* unmanaged[Stdcall]<void>)flush)();
        }
        catch (Exception exception) when (exception is MarshalDirectiveException or ArgumentException)
        {
            Debug.WriteLine($"Unable to apply the native context-menu theme: {exception.Message}");
        }
        finally
        {
            if (ownsModuleReference)
                FreeLibrary(module);
        }
    }

    private static bool ReadSystemDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey, writable: false);
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException or IOException or ArgumentException)
        {
            Debug.WriteLine($"Unable to read the Windows app color preference: {exception.Message}");
            return false;
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr GetModuleHandle(string moduleName);

    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr LoadLibrary(string moduleName);

    [DllImport("kernel32.dll", EntryPoint = "GetProcAddress", ExactSpelling = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, IntPtr procedureName);

    [DllImport("kernel32.dll", EntryPoint = "FreeLibrary", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(IntPtr module);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr window, uint attribute, ref int value, int valueSize);
}
