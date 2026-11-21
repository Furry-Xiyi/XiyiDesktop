using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;

namespace XiyiDesktop.DesktopItems;

/// <summary>
/// Reads Explorer's version-dependent context-menu preferences without changing them.
/// These registry values are implementation details, not a documented Windows contract.
/// </summary>
internal sealed class ContextMenuPreferencesService
{
    public const string KeyPath = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\ContextMenu\Preferences";
    private const string PreferencesSubKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\ContextMenu\Preferences";
    private const int MinimumReportedBuild = 26340;
    private const int MinimumReportedRevision = 9212;

    public ContextMenuPreferencesSnapshot Read()
    {
        var build = ReadWindowsBuild();
        var reportedBuildSupport = IsSupportedClientBuild(build);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PreferencesSubKey, writable: false);
            if (key is null)
                return new ContextMenuPreferencesSnapshot(
                    build,
                    false,
                    null,
                    null,
                    values);

            values["ShowCustomizeMenu"] = ReadEnabledFlag(key, "ShowCustomizeMenu");
            values["ShowExtensions\\Enabled"] = ReadEnabledFlag(key, "ShowExtensions");
            values["CloudStorageActions"] = ReadEnabledFlag(key, "CloudStorageActions");

            foreach (var subKeyName in new[] { "DemoteTopBar", "MoveProperties", "ShowMore" })
                values[$"{subKeyName}\\Enabled"] = ReadEnabledFlag(key, subKeyName);

            ReadNamedChildren(key, "verbs", values);
            ReadNamedChildren(key, "extensions", values);

            var featureDetected =
                !string.Equals(values.GetValueOrDefault("ShowExtensions\\Enabled", "unknown"), "unknown", StringComparison.Ordinal) ||
                !string.Equals(values.GetValueOrDefault("ShowCustomizeMenu", "unknown"), "unknown", StringComparison.Ordinal);
            var supported = reportedBuildSupport && featureDetected;

            return new ContextMenuPreferencesSnapshot(
                build,
                supported,
                ParseBoolean(values.GetValueOrDefault("ShowCustomizeMenu", "unknown")),
                ParseBoolean(values.GetValueOrDefault("ShowExtensions\\Enabled", "unknown")),
                values);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or System.IO.IOException)
        {
            Debug.WriteLine($"Unable to read Explorer context-menu preferences: {exception.Message}");
            return new ContextMenuPreferencesSnapshot(
                build,
                false,
                ParseBoolean(values.GetValueOrDefault("ShowCustomizeMenu", "unknown")),
                ParseBoolean(values.GetValueOrDefault("ShowExtensions\\Enabled", "unknown")),
                values);
        }
    }

    private static string ReadEnabledFlag(RegistryKey root, string name)
    {
        using var child = root.OpenSubKey(name, writable: false);
        if (child is not null)
        {
            var nestedValue = ReadDword(child, "Enabled");
            if (!string.Equals(nestedValue, "unknown", StringComparison.Ordinal))
                return nestedValue;
        }

        // Some builds may use a flat DWORD. Accept the shape only when the value is a DWORD.
        return ReadDword(root, name);
    }

    private static void ReadNamedChildren(RegistryKey root, string groupName, IDictionary<string, string> values)
    {
        using var group = root.OpenSubKey(groupName, writable: false);
        if (group is null)
        {
            values[groupName] = "unknown";
            return;
        }

        var names = group.GetSubKeyNames().OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
        if (names.Length == 0)
        {
            values[groupName] = "none";
            return;
        }

        foreach (var name in names)
        {
            using var child = group.OpenSubKey(name, writable: false);
            values[$"{groupName}\\{name}\\Enabled"] = child is null ? "unknown" : ReadDword(child, "Enabled");
        }
    }

    private static string ReadDword(RegistryKey key, string name)
    {
        try
        {
            var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            return value is int number
                ? number.ToString(CultureInfo.InvariantCulture)
                : "unknown";
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or System.IO.IOException)
        {
            return "unknown";
        }
    }

    private static bool? ParseBoolean(string value) => value switch
    {
        "0" => false,
        "1" => true,
        _ => null
    };

    private static Version? ReadWindowsBuild()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", writable: false);
            var installationType = key?.GetValue("InstallationType") as string;
            var buildText = key?.GetValue("CurrentBuildNumber") as string;
            var revision = key?.GetValue("UBR");
            if (!string.Equals(installationType, "Client", StringComparison.OrdinalIgnoreCase) ||
                !int.TryParse(buildText, NumberStyles.None, CultureInfo.InvariantCulture, out var build))
                return null;

            var ubr = revision is int number ? number : 0;
            return new Version(10, 0, build, ubr);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or System.IO.IOException)
        {
            Debug.WriteLine($"Unable to read the Windows client build: {exception.Message}");
            return null;
        }
    }

    private static bool IsSupportedClientBuild(Version? build) => build is not null &&
        (build.Build > MinimumReportedBuild ||
         (build.Build == MinimumReportedBuild && build.Revision >= MinimumReportedRevision));
}

internal sealed record ContextMenuPreferencesSnapshot(
    Version? WindowsBuild,
    bool SupportsCustomContextMenus,
    bool? ShowCustomizeMenu,
    bool? ShowExtensions,
    IReadOnlyDictionary<string, string> Values)
{
    public bool? IsEnabled(string valuePath)
    {
        var registryPath = valuePath.EndsWith("\\Enabled", StringComparison.OrdinalIgnoreCase)
            ? valuePath
            : $"verbs\\{valuePath}\\Enabled";
        return Values.TryGetValue(registryPath, out var rawValue)
            ? rawValue switch
            {
                "1" => true,
                "0" => false,
                _ => null
            }
            : null;
    }

    public string ToDisplayText()
    {
        if (Values.Count == 0)
            return $"{ContextMenuPreferencesService.KeyPath}{Environment.NewLine}{Environment.NewLine}Unknown or unavailable";

        var lines = new[]
        {
            ContextMenuPreferencesService.KeyPath,
            "",
            "Read-only values (Windows-version dependent):"
        }.Concat(Values.Select(pair => $"{pair.Key}: {pair.Value}"));
        return string.Join(Environment.NewLine, lines);
    }
}
