using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using Windows.ApplicationModel;

namespace XiyiDesktop.Widgets.Providers;

/// <summary>Resolves an SMTC session AUMID to a user-facing application name.</summary>
internal static class SmtcSourceNameResolver
{
    private const uint ShgfiDisplayName = 0x000000200;
    private const int MaxCachedNames = 128;
    private static readonly ConcurrentDictionary<string, string> NameCache = new(StringComparer.OrdinalIgnoreCase);

    public static string? Resolve(string? appUserModelId)
    {
        if (string.IsNullOrWhiteSpace(appUserModelId))
            return null;

        var key = appUserModelId.Trim();
        if (NameCache.TryGetValue(key, out var cachedName))
            return cachedName.Length == 0 ? null : cachedName;

        var displayName = ResolveUncached(key);
        if (NameCache.Count < MaxCachedNames)
            NameCache.TryAdd(key, displayName ?? string.Empty);

        return displayName;
    }

    private static string? ResolveUncached(string appUserModelId)
    {
        var packagedName = TryGetPackagedDisplayName(appUserModelId);
        if (!string.IsNullOrWhiteSpace(packagedName))
            return packagedName;

        var shellName = TryGetStartMenuDisplayName(appUserModelId);
        if (!string.IsNullOrWhiteSpace(shellName))
            return shellName;

        return TryGetExecutableDisplayName(appUserModelId);
    }

    private static string? TryGetPackagedDisplayName(string appUserModelId)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            return null;

        try
        {
            var appInfo = AppInfo.GetFromAppUserModelId(appUserModelId);
            var displayName = appInfo?.DisplayInfo?.DisplayName?.Trim();
            return IsUsableDisplayName(displayName, appUserModelId) ? displayName : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? TryGetStartMenuDisplayName(string appUserModelId)
    {
        try
        {
            var parsingName = $"shell:AppsFolder\\{appUserModelId}";
            if (SHGetFileInfo(
                    parsingName,
                    0,
                    out var fileInfo,
                    (uint)Marshal.SizeOf<ShellFileInfo>(),
                    ShgfiDisplayName) == IntPtr.Zero)
            {
                return null;
            }

            var displayName = fileInfo.DisplayName?.Trim();
            return IsUsableDisplayName(displayName, appUserModelId) ? displayName : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? TryGetExecutableDisplayName(string appUserModelId)
    {
        // Some unpackaged media apps report a simple executable AUMID. Showing
        // its file stem is readable; package-family IDs and other opaque IDs are
        // deliberately withheld when Windows cannot resolve a display name.
        if (appUserModelId.Contains('!') || appUserModelId.Contains('\\') || appUserModelId.Contains('/'))
            return null;

        var extension = Path.GetExtension(appUserModelId);
        if (!extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
            return null;

        var name = Path.GetFileNameWithoutExtension(appUserModelId).Trim();
        return IsUsableDisplayName(name, appUserModelId) ? name : null;
    }

    private static bool IsUsableDisplayName(string? displayName, string appUserModelId)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            return false;

        return !string.Equals(displayName, appUserModelId, StringComparison.OrdinalIgnoreCase) &&
            !displayName.Contains('!');
    }

    [DllImport("shell32.dll", EntryPoint = "SHGetFileInfoW", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string path,
        uint fileAttributes,
        out ShellFileInfo fileInfo,
        uint fileInfoSize,
        uint flags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr IconHandle;
        public int IconIndex;
        public uint Attributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string DisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string TypeName;
    }
}
