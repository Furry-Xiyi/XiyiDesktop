using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using XiyiDesktop.Interop;

namespace XiyiDesktop.DesktopItems;

/// <summary>Uses the desktop Shell namespace's column comparison for virtual and file items.</summary>
internal sealed unsafe class DesktopShellItemComparer : IComparer<DesktopItem>, IDisposable
{
    private const uint DesktopShellItemCompareColumnMask = 0x0000FFFF;
    private const int DesktopNameColumn = 0;
    private const int DesktopSizeColumn = 1;
    private const int DesktopTypeColumn = 2;
    private const int DesktopModifiedColumn = 3;

    private readonly IntPtr _desktopFolder;
    private readonly Dictionary<string, IntPtr> _itemIdLists = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _column;

    private DesktopShellItemComparer(IntPtr desktopFolder, int column)
    {
        _desktopFolder = desktopFolder;
        _column = column;
    }

    public static DesktopShellItemComparer? TryCreate(IEnumerable<DesktopItem> items, string sortMode)
    {
        var column = sortMode switch
        {
            "Name" => DesktopNameColumn,
            "Size" => DesktopSizeColumn,
            "Type" => DesktopTypeColumn,
            "Modified" => DesktopModifiedColumn,
            _ => -1
        };
        if (column < 0)
            return null;

        IntPtr desktopFolder = IntPtr.Zero;
        DesktopShellItemComparer? comparer = null;
        try
        {
            var result = SHGetDesktopFolder(out desktopFolder);
            if (result < 0 || desktopFolder == IntPtr.Zero)
            {
                NativeCom.Release(desktopFolder);
                return null;
            }

            comparer = new DesktopShellItemComparer(desktopFolder, column);
            foreach (var item in items)
            {
                var parsingName = item.Path ?? DesktopItem.GetShellParsingName(item.Kind);
                var parseResult = SHParseDisplayName(parsingName, IntPtr.Zero, out var itemIdList, 0, out _);
                if (parseResult < 0 || itemIdList == IntPtr.Zero)
                {
                    if (itemIdList != IntPtr.Zero)
                        Marshal.FreeCoTaskMem(itemIdList);
                    continue;
                }
                comparer._itemIdLists[item.Identity] = itemIdList;
            }

            return comparer;
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or ArgumentException)
        {
            if (comparer is not null)
                comparer.Dispose();
            else
                NativeCom.Release(desktopFolder);
            Debug.WriteLine($"Unable to use Shell ordering for desktop icons: {exception.Message}");
            return null;
        }
    }

    public int Compare(DesktopItem? left, DesktopItem? right)
    {
        if (ReferenceEquals(left, right))
            return 0;
        if (left is null)
            return -1;
        if (right is null)
            return 1;

        if (_column == DesktopTypeColumn)
        {
            // Keep the well-known desktop folders in the same order as Windows' desktop
            // context menu, independently of the localized names or Shell column values.
            if (left.IsSpecial != right.IsSpecial)
                return left.IsSpecial ? -1 : 1;
            if (left.IsSpecial)
                return GetSpecialItemTypeOrder(left.Kind).CompareTo(GetSpecialItemTypeOrder(right.Kind));
        }

        if (_itemIdLists.TryGetValue(left.Identity, out var leftIdList) &&
            _itemIdLists.TryGetValue(right.Identity, out var rightIdList))
        {
            try
            {
                var compare = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, IntPtr, int>)
                    NativeCom.GetMethod(_desktopFolder, 7);
                var result = compare(
                    _desktopFolder,
                    new IntPtr(_column & DesktopShellItemCompareColumnMask),
                    leftIdList,
                    rightIdList);
                if ((result & 0xFFFF0000) == 0)
                {
                    var shellOrder = unchecked((short)(result & 0xFFFF));
                    if (shellOrder != 0)
                        return shellOrder;
                    if (_column == DesktopTypeColumn && left.IsSpecial && right.IsSpecial)
                        return 0;
                    if (_column == DesktopTypeColumn)
                        return CompareFallback(left, right);
                }
            }
            catch (Exception exception) when (exception is COMException or InvalidOperationException)
            {
                Debug.WriteLine($"Shell desktop item comparison failed: {exception.Message}");
            }
        }

        return CompareFallback(left, right);
    }

    private int CompareFallback(DesktopItem left, DesktopItem right) => _column switch
    {
        DesktopNameColumn => CompareNames(left.Name, right.Name),
        DesktopSizeColumn => CompareSize(left, right),
        DesktopTypeColumn => CompareType(left, right),
        DesktopModifiedColumn => CompareModified(left, right),
        _ => CompareNames(left.Name, right.Name)
    };

    private static int CompareSize(DesktopItem left, DesktopItem right)
    {
        var result = (left.IsDirectory ? 0 : 1).CompareTo(right.IsDirectory ? 0 : 1);
        if (result != 0)
            return result;

        result = (left.SizeBytes ?? -1).CompareTo(right.SizeBytes ?? -1);
        return result != 0 ? result : CompareNames(left.Name, right.Name);
    }

    private static int CompareType(DesktopItem left, DesktopItem right)
    {
        if (left.IsSpecial != right.IsSpecial)
            return left.IsSpecial ? -1 : 1;
        if (left.IsSpecial)
            return GetSpecialItemTypeOrder(left.Kind).CompareTo(GetSpecialItemTypeOrder(right.Kind));

        var leftType = ShellIconService.GetFileTypeName(left.FileExtension, left.IsDirectory);
        var rightType = ShellIconService.GetFileTypeName(right.FileExtension, right.IsDirectory);
        var result = CompareNames(leftType, rightType);
        return result != 0 ? result : CompareNames(left.Name, right.Name);
    }

    private static int CompareModified(DesktopItem left, DesktopItem right)
    {
        var result = (left.ModifiedTimeUtc is null ? 1 : 0).CompareTo(right.ModifiedTimeUtc is null ? 1 : 0);
        if (result != 0)
            return result;

        result = (left.ModifiedTimeUtc ?? DateTimeOffset.MaxValue).CompareTo(
            right.ModifiedTimeUtc ?? DateTimeOffset.MaxValue);
        return result != 0 ? result : CompareNames(left.Name, right.Name);
    }

    private static int CompareNames(string left, string right)
    {
        var result = StrCmpLogicalW(left, right);
        return result != 0 ? result : StringComparer.OrdinalIgnoreCase.Compare(left, right);
    }

    internal static int GetSpecialItemTypeOrder(DesktopItemKind kind) => kind switch
    {
        DesktopItemKind.ThisPc => 0,
        DesktopItemKind.RecycleBin => 1,
        DesktopItemKind.Network => 2,
        DesktopItemKind.UserFiles => 3,
        DesktopItemKind.ControlPanel => 4,
        _ => int.MaxValue
    };

    public void Dispose()
    {
        foreach (var itemIdList in _itemIdLists.Values)
        {
            if (itemIdList != IntPtr.Zero)
                Marshal.FreeCoTaskMem(itemIdList);
        }

        _itemIdLists.Clear();
        NativeCom.Release(_desktopFolder);
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetDesktopFolder(out IntPtr desktopFolder);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(
        string name,
        IntPtr bindingContext,
        out IntPtr itemIdList,
        uint attributesIn,
        out uint attributesOut);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string left, string right);

}
