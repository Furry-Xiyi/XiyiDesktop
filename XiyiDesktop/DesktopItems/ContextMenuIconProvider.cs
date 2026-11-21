using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;

namespace XiyiDesktop.DesktopItems;

/// <summary>Creates context-menu icons from the matching Windows light or dark SVG set.</summary>
internal static class ContextMenuIconProvider
{
    private const string FallbackIcon = "windows.newitem.svg";

    private static readonly IReadOnlyDictionary<string, string> CommandIcons =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["DesktopContext_AlignToGrid.Text"] = "windows.aligngrid.svg",
            ["DesktopContext_ApplicationExtensions.Text"] = "windows.extensionsflyout.svg",
            ["DesktopContext_AutoArrange.Text"] = "windows.autoarrange.svg",
            ["DesktopContext_CustomizeMenu.Text"] = "windows.customizemenu.svg",
            ["DesktopContext_DisplaySettings.Text"] = "DisplaySettings.svg",
            ["DesktopContext_ManageExtensions.Text"] = "windows.extensionsflyout.svg",
            ["DesktopContext_NewBitmap.Text"] = "windows.searchfilterkind.pictures.svg",
            ["DesktopContext_NewCompressedFolder.Text"] = "Windows.Zip.Action.svg",
            ["DesktopContext_NewExcel.Text"] = "windows.searchfilterkind.documents.svg",
            ["DesktopContext_New.Text"] = FallbackIcon,
            ["DesktopContext_NewFolder.Text"] = "newfolder.svg",
            ["DesktopContext_NewPowerPoint.Text"] = "windows.searchfilterkind.documents.svg",
            ["DesktopContext_NewShortcut.Text"] = "link.svg",
            ["DesktopContext_NewTextDocument.Text"] = "windows.searchfilterkind.documents.svg",
            ["DesktopContext_NewWord.Text"] = "windows.searchfilterkind.documents.svg",
            ["DesktopContext_OpenInTerminal.Text"] = "windows.powershell.run.svg",
            ["DesktopContext_Paste.Text"] = "windows.paste.svg",
            ["DesktopContext_Personalize.Text"] = "Personalize.svg",
            ["DesktopContext_Refresh.Text"] = "windows.refresh.svg",
            ["DesktopContext_ShowIcons.Text"] = "windows.showdesktop.svg",
            ["DesktopContext_ShowMoreOptions.Text"] = "expandtoclassic.svg",
            ["DesktopContext_SortBy.Text"] = "windows.sortbycolumn.svg",
            ["DesktopContext_UndoDelete.Text"] = "windows.undo.svg",
            ["DesktopContext_View.Text"] = "View.svg",
            ["DesktopItemContext_Copy/Text"] = "windows.copy.svg",
            ["DesktopItemContext_CopyAsPath.Text"] = "windows.copyaspath.svg",
            ["DesktopItemContext_CreateShortcut.Text"] = "link.svg",
            ["DesktopItemContext_Cut/Text"] = "windows.cut.svg",
            ["DesktopItemContext_Delete/Text"] = "windows.ribbondelete.svg",
            ["DesktopItemContext_EmptyRecycleBin.Text"] = "windows.recyclebin.empty.svg",
            ["DesktopItemContext_ManageThisPc.Text"] = "windows.folderoptions.svg",
            ["DesktopItemContext_Open/Text"] = "windows.openfolder.svg",
            ["DesktopItemContext_OpenFileLocation.Text"] = "windows.shortcut.opencontaining.svg",
            ["DesktopItemContext_Paste/Text"] = "windows.paste.svg",
            ["DesktopItemContext_Print.Text"] = "print.svg",
            ["DesktopItemContext_Properties/Text"] = "windows.properties.svg",
            ["DesktopItemContext_Rename/Text"] = "windows.rename.svg",
            ["DesktopItemContext_RunAsAdministrator.Text"] = "RunAsAdmin.svg",
            ["DesktopItemContext_RotateImage.Text"] = "windows.rotate90.svg",
            ["DesktopItemContext_RotateLeft.Text"] = "windows.rotate270.svg",
            ["DesktopItemContext_RotateRight.Text"] = "windows.rotate90.svg",
            ["DesktopItemContext_SendTo.Text"] = "windows.sendto.svg",
            ["DesktopItemContext_Share.Text"] = "Windows.ModernShare.svg",
            ["DesktopSort_Name.Text"] = "windows.showfileextensions.svg",
            ["DesktopSort_Size.Text"] = "windows.searchfiltersize.svg",
            ["DesktopSort_Type.Text"] = "windows.searchfilterkind.svg",
            ["DesktopSort_Modified.Text"] = "windows.searchfilterdate.svg",
            ["DesktopView_Large.Text"] = "windows.iconsize.large.svg",
            ["DesktopView_Medium.Text"] = "windows.iconsize.medium.svg",
            ["DesktopView_Small.Text"] = "windows.iconsize.smallicon.svg"
        };

    public static IconElement Create(string fileName, ElementTheme theme)
    {
        var safeFileName = GetSafeFileName(fileName);
        var icon = new ImageIcon();
        icon.Source = CreateSource(safeFileName, theme, icon.ActualTheme);

        if (theme == ElementTheme.Default)
        {
            void RefreshSource() => icon.Source = CreateSource(safeFileName, ElementTheme.Default, icon.ActualTheme);
            icon.ActualThemeChanged += (_, _) => RefreshSource();
            icon.Loaded += (_, _) => RefreshSource();
        }

        return icon;
    }

    public static IconElement CreateForCommand(string resourceKey, ElementTheme theme)
    {
        var normalizedKey = resourceKey?.Replace('\\', '/').Trim() ?? string.Empty;
        return Create(CommandIcons.TryGetValue(normalizedKey, out var fileName) ? fileName : FallbackIcon, theme);
    }

    private static SvgImageSource CreateSource(string fileName, ElementTheme requestedTheme, ElementTheme actualTheme = ElementTheme.Default)
    {
        var resolvedTheme = requestedTheme == ElementTheme.Default
            ? (actualTheme is ElementTheme.Light or ElementTheme.Dark ? actualTheme : ElementTheme.Light)
            : requestedTheme;
        var themeFolder = resolvedTheme == ElementTheme.Dark ? "theme-dark" : "theme-light";
        return new SvgImageSource
        {
            UriSource = new Uri($"ms-appx:///Assets/Icon/{themeFolder}/{fileName}")
        };
    }

    private static string GetSafeFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return FallbackIcon;

        var separatorIndex = Math.Max(fileName.LastIndexOf('/'), fileName.LastIndexOf('\\'));
        var leafName = separatorIndex >= 0 ? fileName[(separatorIndex + 1)..] : fileName;
        return leafName.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ? leafName : FallbackIcon;
    }
}
