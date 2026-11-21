using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace XiyiDesktop.DesktopItems;

/// <summary>Reads the per-user desktop icon visibility choices made in Windows.</summary>
internal static class DesktopSystemIconSettings
{
    private const string HideDesktopIconsPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons";

    private static readonly DesktopSystemIconDefinition[] Definitions =
    [
        new(DesktopItemKind.ThisPc, "{20D04FE0-3AEA-1069-A2D8-08002B30309D}", false, "DesktopItem_ThisPc"),
        new(DesktopItemKind.RecycleBin, "{645FF040-5081-101B-9F08-00AA002F954E}", true, "DesktopItem_RecycleBin"),
        new(DesktopItemKind.ControlPanel, "{5399E694-6CE5-4D6C-8FCE-1D8870FDCBA0}", false, "DesktopItem_ControlPanel"),
        new(DesktopItemKind.UserFiles, "{59031A47-3F72-44A7-89C5-5595FE6B30EE}", false, "DesktopItem_UserFiles"),
        new(DesktopItemKind.Network, "{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}", false, "DesktopItem_Network")
    ];

    public static IReadOnlyList<DesktopItem> GetVisibleItems()
    {
        var result = new List<DesktopItem>(Definitions.Length);
        foreach (var definition in Definitions)
        {
            if (IsVisible(definition))
                result.Add(DesktopItem.CreateShellItem(definition.Kind, definition.ResourceKey));
        }

        return result;
    }

    private static bool IsVisible(DesktopSystemIconDefinition definition)
    {
        try
        {
            foreach (var subKeyName in new[] { "NewStartPanel", "ClassicStartMenu", "ClassicStartPanel" })
            {
                using var key = Registry.CurrentUser.OpenSubKey($"{HideDesktopIconsPath}\\{subKeyName}");
                if (key?.GetValue(definition.ClassId) is int hidden && hidden is 0 or 1)
                    return hidden == 0;
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
        {
            System.Diagnostics.Debug.WriteLine($"Unable to read the Windows desktop icon setting for '{definition.ClassId}': {exception.Message}");
        }

        return definition.DefaultVisible;
    }

    private sealed record DesktopSystemIconDefinition(
        DesktopItemKind Kind,
        string ClassId,
        bool DefaultVisible,
        string ResourceKey);
}
