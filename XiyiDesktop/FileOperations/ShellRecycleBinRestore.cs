using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using XiyiDesktop.Interop;

namespace XiyiDesktop.FileOperations;

/// <summary>Restores one uniquely identified Recycle Bin child through its native Shell command.</summary>
internal static unsafe class ShellRecycleBinRestore
{
    public enum RestoreAttemptResult
    {
        Restored,
        NoUniqueCandidate,
        Failed
    }

    private const uint ShellFolders = 0x00000020;
    private const uint ShellNonFolders = 0x00000040;
    private const uint ShellIncludeHidden = 0x00000080;
    private const uint ShellAttributeFolder = 0x20000000;
    private const uint ContextMenuFirstCommand = 1;
    private const uint ContextMenuLastCommand = 0x7FFF;
    private const uint ContextMenuUnicode = 0x00004000;
    private const uint ContextMenuVerbUnicode = 4;
    private const int MaximumRecycleEntriesToScan = 10000;
    private const int ShowNormal = 1;
    private const int VariantTypeDate = 7;
    private const int VariantTypeFileTime = 64;
    private static readonly Guid ShellFolderInterfaceId = new("000214E6-0000-0000-C000-000000000046");
    private static readonly Guid ShellItem2InterfaceId = new("7E9FB0D3-919F-4307-AB2E-9B1860310C93");
    private static readonly Guid ContextMenuInterfaceId = new("000214E4-0000-0000-C000-000000000046");
    private static readonly PropertyKey DeletedFromProperty = new(
        new Guid("9B174B34-40FF-11D2-A27E-00C04F79FBC9"), 2);
    private static readonly PropertyKey DateDeletedProperty = new(
        new Guid("9B174B34-40FF-11D2-A27E-00C04F79FBC9"), 3);
    private static readonly PropertyKey FileNameProperty = new(
        new Guid("B725F130-47EF-101A-A5F1-02608C9EEBAC"), 100);

    public static Task<int> RunOnStaAsync(Func<int> operation)
    {
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var apartment = NativeCom.InitializeApartment();
                completion.SetResult(operation());
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "XiyiDesktop Recycle Bin restore"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    public static RestoreAttemptResult RestoreExactItem(DesktopUndoHistory.TrackedRecycleItem tracked, IntPtr ownerWindow)
    {
        if (!TryReadDeletionTime(tracked.MetadataPath, out var deletionTime))
            return RestoreAttemptResult.NoUniqueCandidate;

        IntPtr absoluteRecycleBinIdList = IntPtr.Zero;
        IntPtr parentFolder = IntPtr.Zero;
        IntPtr recycleBinFolder = IntPtr.Zero;
        IntPtr enumerator = IntPtr.Zero;
        IntPtr selectedChild = IntPtr.Zero;
        try
        {
            var parseResult = SHParseDisplayName(
                "shell:RecycleBinFolder", IntPtr.Zero, out absoluteRecycleBinIdList, 0, out _);
            if (parseResult < 0)
                Marshal.ThrowExceptionForHR(parseResult);
            if (absoluteRecycleBinIdList == IntPtr.Zero)
                return RestoreAttemptResult.Failed;

            var shellFolderId = ShellFolderInterfaceId;
            Marshal.ThrowExceptionForHR(SHBindToParent(
                absoluteRecycleBinIdList, ref shellFolderId, out parentFolder, out var childIdList));
            if (parentFolder == IntPtr.Zero || childIdList == IntPtr.Zero)
                return RestoreAttemptResult.Failed;

            var bindToObject = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, Guid*, IntPtr*, int>)
                NativeCom.GetMethod(parentFolder, 5);
            var recycleFolderInterfaceId = ShellFolderInterfaceId;
            Marshal.ThrowExceptionForHR(bindToObject(
                parentFolder,
                childIdList,
                IntPtr.Zero,
                &recycleFolderInterfaceId,
                &recycleBinFolder));

            var enumObjects = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, IntPtr*, int>)
                NativeCom.GetMethod(recycleBinFolder, 4);
            Marshal.ThrowExceptionForHR(enumObjects(
                recycleBinFolder,
                ownerWindow,
                ShellFolders | ShellNonFolders | ShellIncludeHidden,
                &enumerator));

            if (enumerator == IntPtr.Zero)
                return RestoreAttemptResult.Failed;

            var matches = 0;
            var scanned = 0;
            var next = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, uint*, int>)
                NativeCom.GetMethod(enumerator, 3);
            while (true)
            {
                IntPtr child = IntPtr.Zero;
                uint fetched = 0;
                var nextResult = next(enumerator, 1, &child, &fetched);
                if (nextResult < 0)
                {
                    FreePidl(child);
                    return RestoreAttemptResult.Failed;
                }
                if (fetched == 0)
                {
                    FreePidl(child);
                    break;
                }
                if (++scanned > MaximumRecycleEntriesToScan)
                {
                    FreePidl(child);
                    return RestoreAttemptResult.Failed;
                }

                try
                {
                    if (!TryCreateShellItem2(absoluteRecycleBinIdList, recycleBinFolder, child, out var shellItem2))
                        return RestoreAttemptResult.Failed;
                    bool isMatch;
                    try
                    {
                        if (!TryClassifyChild(shellItem2, tracked, deletionTime, out isMatch))
                            return RestoreAttemptResult.Failed;
                    }
                    finally
                    {
                        NativeCom.Release(shellItem2);
                    }

                    if (!isMatch)
                        continue;

                    matches++;
                    if (matches == 1)
                    {
                        selectedChild = child;
                        child = IntPtr.Zero;
                    }
                }
                finally
                {
                    FreePidl(child);
                }

                if (matches > 1)
                    break;
            }

            if (matches != 1 || selectedChild == IntPtr.Zero)
                return RestoreAttemptResult.NoUniqueCandidate;

            if (!InvokeRestoreVerb(recycleBinFolder, selectedChild, ownerWindow))
                return RestoreAttemptResult.Failed;

            // Require both the original destination to reappear and this exact Shell
            // item to disappear. A target created concurrently by another process alone
            // must not make the tracked Recycle Bin entry look successfully restored.
            var remainingMatches = CountMatchingChildren(
                absoluteRecycleBinIdList,
                recycleBinFolder,
                tracked,
                deletionTime,
                ownerWindow);
            if (!IsTrackedRecycleEntryPresent(tracked) &&
                (File.Exists(tracked.OriginalPath) || Directory.Exists(tracked.OriginalPath)))
            {
                if (remainingMatches > 0)
                    Debug.WriteLine($"The Recycle Bin view has not yet removed the restored item '{tracked.OriginalPath}'.");
                return RestoreAttemptResult.Restored;
            }
            return RestoreAttemptResult.Failed;
        }
        finally
        {
            FreePidl(selectedChild);
            NativeCom.Release(enumerator);
            NativeCom.Release(recycleBinFolder);
            NativeCom.Release(parentFolder);
            FreePidl(absoluteRecycleBinIdList);
        }
    }

    private static bool IsTrackedRecycleEntryPresent(DesktopUndoHistory.TrackedRecycleItem tracked) =>
        File.Exists(tracked.MetadataPath) || File.Exists(tracked.RecycledPath) || Directory.Exists(tracked.RecycledPath);

    public static HashSet<string> FindUniqueRestoreCandidates(
        IReadOnlyList<DesktopUndoHistory.TrackedRecycleItem> trackedItems,
        IntPtr ownerWindow,
        out bool complete)
    {
        var uniqueMetadataPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        complete = false;
        if (trackedItems.Count == 0)
        {
            complete = true;
            return uniqueMetadataPaths;
        }

        var expected = new List<ExpectedRestoreCandidate>();
        foreach (var item in trackedItems)
        {
            if (!TryReadDeletionTime(item.MetadataPath, out var deletionTime))
                return uniqueMetadataPaths;
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(item.OriginalPath));
            var parent = Path.GetDirectoryName(item.OriginalPath);
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(parent))
                return uniqueMetadataPaths;
            expected.Add(new ExpectedRestoreCandidate(item, name, parent, deletionTime.Ticks / TimeSpan.TicksPerSecond));
        }

        IntPtr absoluteRecycleBinIdList = IntPtr.Zero;
        IntPtr parentFolder = IntPtr.Zero;
        IntPtr recycleBinFolder = IntPtr.Zero;
        IntPtr enumerator = IntPtr.Zero;
        try
        {
            var parseResult = SHParseDisplayName(
                "shell:RecycleBinFolder", IntPtr.Zero, out absoluteRecycleBinIdList, 0, out _);
            if (parseResult < 0)
                return uniqueMetadataPaths;
            if (absoluteRecycleBinIdList == IntPtr.Zero)
                return uniqueMetadataPaths;

            var shellFolderId = ShellFolderInterfaceId;
            if (SHBindToParent(absoluteRecycleBinIdList, ref shellFolderId, out parentFolder, out var childIdList) < 0 ||
                parentFolder == IntPtr.Zero || childIdList == IntPtr.Zero)
            {
                return uniqueMetadataPaths;
            }

            var bindToObject = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, Guid*, IntPtr*, int>)
                NativeCom.GetMethod(parentFolder, 5);
            var recycleFolderInterfaceId = ShellFolderInterfaceId;
            if (bindToObject(parentFolder, childIdList, IntPtr.Zero, &recycleFolderInterfaceId, &recycleBinFolder) < 0 ||
                recycleBinFolder == IntPtr.Zero)
            {
                return uniqueMetadataPaths;
            }

            var enumObjects = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, IntPtr*, int>)
                NativeCom.GetMethod(recycleBinFolder, 4);
            if (enumObjects(
                    recycleBinFolder,
                    ownerWindow,
                    ShellFolders | ShellNonFolders | ShellIncludeHidden,
                    &enumerator) < 0 || enumerator == IntPtr.Zero)
            {
                return uniqueMetadataPaths;
            }

            var matchCounts = new int[expected.Count];
            var ambiguous = new bool[expected.Count];
            var scanned = 0;
            var next = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, uint*, int>)
                NativeCom.GetMethod(enumerator, 3);
            while (true)
            {
                IntPtr child = IntPtr.Zero;
                uint fetched = 0;
                var result = next(enumerator, 1, &child, &fetched);
                if (result < 0)
                    return uniqueMetadataPaths;
                if (fetched == 0)
                    break;
                if (++scanned > MaximumRecycleEntriesToScan)
                    return uniqueMetadataPaths;

                try
                {
                    if (!TryCreateShellItem2(absoluteRecycleBinIdList, recycleBinFolder, child, out var shellItem2))
                    {
                        continue;
                    }

                    try
                    {
                        if (!TryGetStringProperty(shellItem2, FileNameProperty, out var shellName))
                        {
                            if (TryGetDisplayName(shellItem2, out var fallbackName))
                            {
                                foreach (var (candidate, index) in expected.Select((candidate, index) => (candidate, index)))
                                {
                                    if (string.Equals(candidate.Name, fallbackName, StringComparison.OrdinalIgnoreCase) ||
                                        string.Equals(Path.GetFileNameWithoutExtension(candidate.Name), fallbackName, StringComparison.OrdinalIgnoreCase))
                                    {
                                        ambiguous[index] = true;
                                    }
                                }
                            }
                            continue;
                        }
                        var potentialMatches = expected
                            .Select((candidate, index) => (candidate, index))
                            .Where(pair => string.Equals(pair.candidate.Name, shellName, StringComparison.OrdinalIgnoreCase))
                            .ToArray();
                        if (potentialMatches.Length == 0)
                            continue;

                        var getAttributes = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint*, int>)
                            NativeCom.GetMethod(shellItem2, 6);
                        uint attributes = 0;
                        if (getAttributes(shellItem2, ShellAttributeFolder, &attributes) < 0 ||
                            !TryGetStringProperty(shellItem2, DeletedFromProperty, out var deletedFrom) ||
                            !TryGetDateProperty(shellItem2, DateDeletedProperty, out var shellDeletionTime))
                        {
                            foreach (var (_, index) in potentialMatches)
                                ambiguous[index] = true;
                            continue;
                        }

                        var isDirectory = (attributes & ShellAttributeFolder) != 0;
                        var deletionSecond = shellDeletionTime.Ticks / TimeSpan.TicksPerSecond;
                        var childMatches = new List<int>();
                        foreach (var (candidate, index) in potentialMatches)
                        {
                            if (candidate.Item.IsDirectory == isDirectory &&
                                (PathMatches(deletedFrom, candidate.ParentPath) || PathMatches(deletedFrom, candidate.Item.OriginalPath)) &&
                                candidate.DeletionSecond == deletionSecond)
                            {
                                childMatches.Add(index);
                            }
                        }

                        if (childMatches.Count > 1)
                        {
                            foreach (var index in childMatches)
                                ambiguous[index] = true;
                        }
                        else if (childMatches.Count == 1)
                        {
                            matchCounts[childMatches[0]]++;
                        }
                    }
                    finally
                    {
                        NativeCom.Release(shellItem2);
                    }
                }
                finally
                {
                    FreePidl(child);
                }
            }

            for (var index = 0; index < expected.Count; index++)
            {
                if (matchCounts[index] == 1 && !ambiguous[index])
                    uniqueMetadataPaths.Add(expected[index].Item.MetadataPath);
            }
            complete = true;
            return uniqueMetadataPaths;
        }
        catch (Exception exception) when (exception is COMException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Could not verify Recycle Bin undo entries: {exception.Message}");
            return uniqueMetadataPaths;
        }
        finally
        {
            NativeCom.Release(enumerator);
            NativeCom.Release(recycleBinFolder);
            NativeCom.Release(parentFolder);
            FreePidl(absoluteRecycleBinIdList);
        }
    }

    private static int CountMatchingChildren(
        IntPtr absoluteRecycleBinIdList,
        IntPtr recycleBinFolder,
        DesktopUndoHistory.TrackedRecycleItem tracked,
        DateTime deletionTime,
        IntPtr ownerWindow)
    {
        IntPtr enumerator = IntPtr.Zero;
        try
        {
            var enumObjects = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, IntPtr*, int>)
                NativeCom.GetMethod(recycleBinFolder, 4);
            if (enumObjects(recycleBinFolder, ownerWindow, ShellFolders | ShellNonFolders | ShellIncludeHidden, &enumerator) < 0 ||
                enumerator == IntPtr.Zero)
            {
                return -1;
            }

            var matches = 0;
            var scanned = 0;
            var next = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, uint*, int>)
                NativeCom.GetMethod(enumerator, 3);
            while (true)
            {
                IntPtr child = IntPtr.Zero;
                uint fetched = 0;
                var result = next(enumerator, 1, &child, &fetched);
                if (result < 0)
                {
                    FreePidl(child);
                    return -1;
                }
                if (fetched == 0)
                {
                    FreePidl(child);
                    break;
                }
                if (++scanned > MaximumRecycleEntriesToScan)
                {
                    FreePidl(child);
                    return -1;
                }

                try
                {
                    if (!TryCreateShellItem2(absoluteRecycleBinIdList, recycleBinFolder, child, out var shellItem2))
                        return -1;
                    bool isMatch;
                    try
                    {
                        if (!TryClassifyChild(shellItem2, tracked, deletionTime, out isMatch))
                            return -1;
                    }
                    finally
                    {
                        NativeCom.Release(shellItem2);
                    }

                    if (isMatch)
                        matches++;
                }
                finally
                {
                    FreePidl(child);
                }
            }

            return matches;
        }
        catch (Exception exception) when (exception is COMException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Could not verify the restored Recycle Bin entry: {exception.Message}");
            return -1;
        }
        finally
        {
            NativeCom.Release(enumerator);
        }
    }

    private static bool TryCreateShellItem2(
        IntPtr absoluteRecycleBinIdList,
        IntPtr recycleBinFolder,
        IntPtr childIdList,
        out IntPtr shellItem2)
    {
        shellItem2 = IntPtr.Zero;
        var itemInterfaceId = ShellItem2InterfaceId;
        var result = SHCreateItemWithParent(
            absoluteRecycleBinIdList,
            recycleBinFolder,
            childIdList,
            ref itemInterfaceId,
            out shellItem2);
        if (result >= 0 && shellItem2 != IntPtr.Zero)
            return true;

        NativeCom.Release(shellItem2);
        shellItem2 = IntPtr.Zero;
        return false;
    }

    private static bool TryGetDisplayName(IntPtr shellItem2, out string value)
    {
        value = string.Empty;
        var getDisplayName = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)
            NativeCom.GetMethod(shellItem2, 5);
        IntPtr displayName = IntPtr.Zero;
        var result = getDisplayName(shellItem2, 0, &displayName);
        if (result < 0 || displayName == IntPtr.Zero)
            return false;

        try
        {
            value = Marshal.PtrToStringUni(displayName) ?? string.Empty;
            return value.Length > 0;
        }
        finally
        {
            Marshal.FreeCoTaskMem(displayName);
        }
    }

    private static bool TryClassifyChild(
        IntPtr shellItem2,
        DesktopUndoHistory.TrackedRecycleItem tracked,
        DateTime deletionTime,
        out bool isMatch)
    {
        isMatch = false;
        if (!TryGetStringProperty(shellItem2, FileNameProperty, out var childName))
            return false;

        if (!string.Equals(childName, Path.GetFileName(Path.TrimEndingDirectorySeparator(tracked.OriginalPath)), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var getAttributes = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint*, int>)
            NativeCom.GetMethod(shellItem2, 6);
        uint attributes = 0;
        if (getAttributes(shellItem2, ShellAttributeFolder, &attributes) < 0)
        {
            return false;
        }
        if (((attributes & ShellAttributeFolder) != 0) != tracked.IsDirectory)
            return true;

        if (!TryGetStringProperty(shellItem2, DeletedFromProperty, out var deletedFrom))
            return false;

        var originalParent = Path.GetDirectoryName(tracked.OriginalPath);
        if (string.IsNullOrWhiteSpace(originalParent) || !PathMatches(deletedFrom, originalParent) &&
            !PathMatches(deletedFrom, tracked.OriginalPath))
        {
            return true;
        }

        if (!TryGetDateProperty(shellItem2, DateDeletedProperty, out var shellDeletionTime))
            return false;

        // Compare at the timestamp's reported one-second precision. If another item
        // has the same source path, name, and second, enumeration will find it too and
        // the caller will refuse to pick either one.
        isMatch = shellDeletionTime.Ticks / TimeSpan.TicksPerSecond ==
            deletionTime.Ticks / TimeSpan.TicksPerSecond;
        return true;
    }

    private static bool PathMatches(string candidate, string expected)
    {
        try
        {
            return string.Equals(NormalizePath(candidate), NormalizePath(expected), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string NormalizePath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool TryGetDateProperty(
        IntPtr shellItem2,
        PropertyKey property,
        out DateTime value)
    {
        value = default;
        var variant = default(VariantValue);
        var getProperty = (delegate* unmanaged[Stdcall]<IntPtr, PropertyKey*, VariantValue*, int>)
            NativeCom.GetMethod(shellItem2, 13);
        var result = getProperty(shellItem2, &property, &variant);
        if (result < 0)
            return false;

        try
        {
            if (variant.Type == VariantTypeDate)
            {
                value = DateTime.FromOADate(variant.DateValue);
                return true;
            }
            if (variant.Type == VariantTypeFileTime)
            {
                value = DateTime.FromFileTime(variant.FileTimeValue);
                return true;
            }
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
        finally
        {
            _ = PropVariantClear(&variant);
        }
    }

    private static bool TryGetStringProperty(IntPtr shellItem2, PropertyKey property, out string value)
    {
        value = string.Empty;
        var getString = (delegate* unmanaged[Stdcall]<IntPtr, PropertyKey*, IntPtr*, int>)
            NativeCom.GetMethod(shellItem2, 17);
        IntPtr nativeValue = IntPtr.Zero;
        var result = getString(shellItem2, &property, &nativeValue);
        if (result < 0 || nativeValue == IntPtr.Zero)
            return false;

        try
        {
            value = Marshal.PtrToStringUni(nativeValue) ?? string.Empty;
            return value.Length > 0;
        }
        finally
        {
            Marshal.FreeCoTaskMem(nativeValue);
        }
    }

    private static bool TryReadDeletionTime(string metadataPath, out DateTime deletionTime)
    {
        deletionTime = default;
        try
        {
            var bytes = File.ReadAllBytes(metadataPath);
            if (bytes.Length < 24)
                return false;
            var fileTime = BitConverter.ToInt64(bytes, 16);
            if (fileTime <= 0)
                return false;
            deletionTime = DateTime.FromFileTime(fileTime);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Debug.WriteLine($"Could not read Recycle Bin deletion time '{metadataPath}': {exception.Message}");
            return false;
        }
    }

    private static bool InvokeRestoreVerb(IntPtr recycleBinFolder, IntPtr childIdList, IntPtr ownerWindow)
    {
        IntPtr childArray = IntPtr.Zero;
        IntPtr contextMenu = IntPtr.Zero;
        IntPtr verbBuffer = IntPtr.Zero;
        try
        {
            childArray = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(childArray, childIdList);
            var contextMenuId = ContextMenuInterfaceId;
            var getUiObjectOf = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, IntPtr, Guid*, IntPtr, IntPtr*, int>)
                NativeCom.GetMethod(recycleBinFolder, 10);
            Marshal.ThrowExceptionForHR(getUiObjectOf(
                recycleBinFolder,
                ownerWindow,
                1,
                childArray,
                &contextMenuId,
                IntPtr.Zero,
                &contextMenu));

            var menu = CreatePopupMenu();
            if (menu == IntPtr.Zero)
                return false;
            try
            {
                var queryContextMenu = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, uint, int>)
                    NativeCom.GetMethod(contextMenu, 3);
                var queryResult = queryContextMenu(
                    contextMenu, menu, 0, ContextMenuFirstCommand, ContextMenuLastCommand, 0);
                Marshal.ThrowExceptionForHR(queryResult);
                var commandCount = unchecked((uint)queryResult) & 0xFFFF;
                if (commandCount == 0)
                    return false;

                var getCommandString = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, IntPtr, IntPtr, uint, int>)
                    NativeCom.GetMethod(contextMenu, 5);
                var restoreOffsets = new System.Collections.Generic.List<uint>();
                verbBuffer = Marshal.AllocHGlobal(512);
                for (uint offset = 0; offset < commandCount; offset++)
                {
                    for (var charIndex = 0; charIndex < 256; charIndex++)
                        Marshal.WriteInt16(verbBuffer, charIndex * sizeof(char), 0);
                    if (getCommandString(
                        contextMenu,
                        new IntPtr(unchecked((int)offset)),
                        ContextMenuVerbUnicode,
                        IntPtr.Zero,
                        verbBuffer,
                        256) < 0)
                    {
                        continue;
                    }

                    var verb = Marshal.PtrToStringUni(verbBuffer);
                    if (string.Equals(verb, "undelete", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(verb, "restore", StringComparison.OrdinalIgnoreCase))
                    {
                        restoreOffsets.Add(offset);
                    }
                }

                if (restoreOffsets.Count != 1)
                    return false;

                var commandInfo = new CommandInfoEx
                {
                    Size = (uint)sizeof(CommandInfoEx),
                    Mask = ContextMenuUnicode,
                    Window = ownerWindow,
                    Verb = new IntPtr(unchecked((int)restoreOffsets[0])),
                    VerbUnicode = new IntPtr(unchecked((int)restoreOffsets[0])),
                    Show = ShowNormal
                };
                var invokeCommand = (delegate* unmanaged[Stdcall]<IntPtr, CommandInfoEx*, int>)
                    NativeCom.GetMethod(contextMenu, 4);
                Marshal.ThrowExceptionForHR(invokeCommand(contextMenu, &commandInfo));
                return true;
            }
            finally
            {
                DestroyMenu(menu);
            }
        }
        finally
        {
            if (verbBuffer != IntPtr.Zero)
                Marshal.FreeHGlobal(verbBuffer);
            if (childArray != IntPtr.Zero)
                Marshal.FreeHGlobal(childArray);
            NativeCom.Release(contextMenu);
        }
    }

    private static void FreePidl(IntPtr itemIdList)
    {
        if (itemIdList != IntPtr.Zero)
            Marshal.FreeCoTaskMem(itemIdList);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;

        public PropertyKey(Guid formatId, uint propertyId)
        {
            FormatId = formatId;
            PropertyId = propertyId;
        }
    }

    private sealed record ExpectedRestoreCandidate(
        DesktopUndoHistory.TrackedRecycleItem Item,
        string Name,
        string ParentPath,
        long DeletionSecond);

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct VariantValue
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr PointerValue;
        [FieldOffset(8)] public double DateValue;
        [FieldOffset(8)] public long FileTimeValue;
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
        public int InvokePointX;
        public int InvokePointY;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHParseDisplayName(
        string name, IntPtr bindContext, out IntPtr itemIdList, uint attributesIn, out uint attributesOut);

    [DllImport("shell32.dll", PreserveSig = true)]
    private static extern int SHBindToParent(
        IntPtr itemIdList, ref Guid interfaceId, out IntPtr parentFolder, out IntPtr childIdList);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int PropVariantClear(VariantValue* variant);

    [DllImport("shell32.dll", PreserveSig = true)]
    private static extern int SHCreateItemWithParent(
        IntPtr parentItemIdList,
        IntPtr parentShellFolder,
        IntPtr childItemIdList,
        ref Guid interfaceId,
        out IntPtr shellItem);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr menu);
}
