using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using XiyiDesktop.Interop;

namespace XiyiDesktop.DesktopItems;

/// <summary>Adapts packaged Explorer commands into safe, display-only menu metadata.</summary>
internal static class ExplorerCommandAdapter
{
    private const uint CommandStateDisabled = 0x00000001;
    private const uint CommandStateHidden = 0x00000002;
    private const uint CommandFlagHasSubCommands = 0x00000001;
    private const uint ExplorerCommandClassContext = 0x00000007;
    private const uint CoinitApartmentThreaded = 0x00000002;
    private const int RpcChangedMode = unchecked((int)0x80010106);
    private const int MaximumSubCommands = 64;
    private const int MaximumMenuDepth = 8;
    private static readonly Guid ExplorerCommandInterfaceId = new("A08CE4D0-FA25-44AB-B57C-C7B1C323E0B9");
    private static readonly Guid ShellItemInterfaceId = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");
    private static readonly Guid ShellItemArrayInterfaceId = new("B63EA76D-1F85-456F-A19C-48159EFA858B");
    private static readonly object BackgroundCacheLock = new();
    private static Task<IReadOnlyList<ExplorerCommandPackage>>? _backgroundCacheTask;
    private static string? _backgroundCacheKey;
    private static readonly ConcurrentDictionary<string, Task<IReadOnlyList<ExplorerCommandPackage>>> ItemCache = new(StringComparer.OrdinalIgnoreCase);
    private static DateTimeOffset _cacheCreatedUtc = DateTimeOffset.MinValue;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);

    public static Task WarmDesktopBackgroundCacheAsync(string desktopPath) => GetBackgroundCacheTask(desktopPath);

    public static async Task<IReadOnlyList<ExplorerCommandPackage>> DiscoverDesktopBackgroundCommandsAsync(string desktopPath) =>
        RefreshPlacements(await GetBackgroundCacheTask(desktopPath).ConfigureAwait(false));

    public static async Task<IReadOnlyList<ExplorerCommandPackage>> DiscoverItemCommandsAsync(string targetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        EnsureCacheIsCurrent();
        var cacheKey = GetItemCacheKey(targetPath);
        var cachedTask = ItemCache.GetOrAdd(cacheKey, _ => Task.Run<IReadOnlyList<ExplorerCommandPackage>>(
            () => RunOnSta(() => DiscoverItemCommands(targetPath))));
        return RefreshPlacements(await cachedTask.ConfigureAwait(false));
    }

    public static Task InvokeAsync(Guid classId, IReadOnlyList<int> subCommandPath) =>
        Task.Run(() => RunOnSta(() => InvokeOnSta(classId, subCommandPath, null)));

    public static Task InvokeAsync(Guid classId, IReadOnlyList<int> subCommandPath, string targetPath) =>
        Task.Run(() => RunOnSta(() => InvokeOnSta(classId, subCommandPath, targetPath)));

    private static Task<IReadOnlyList<ExplorerCommandPackage>> GetBackgroundCacheTask(string desktopPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(desktopPath);
        EnsureCacheIsCurrent();
        var cacheKey = Path.GetFullPath(desktopPath);
        lock (BackgroundCacheLock)
        {
            if (_backgroundCacheTask is null ||
                !string.Equals(_backgroundCacheKey, cacheKey, StringComparison.OrdinalIgnoreCase))
            {
                _backgroundCacheKey = cacheKey;
                _backgroundCacheTask = Task.Run<IReadOnlyList<ExplorerCommandPackage>>(
                    () => RunOnSta(() => DiscoverDesktopBackgroundCommands(cacheKey)));
            }

            return _backgroundCacheTask;
        }
    }

    private static void EnsureCacheIsCurrent()
    {
        lock (BackgroundCacheLock)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _cacheCreatedUtc < CacheLifetime)
                return;

            _backgroundCacheTask = null;
            _backgroundCacheKey = null;
            ItemCache.Clear();
            _cacheCreatedUtc = now;
        }
    }

    private static IReadOnlyList<ExplorerCommandPackage> RefreshPlacements(IReadOnlyList<ExplorerCommandPackage> packages) =>
        packages.Select(package => package with
        {
            Placement = ContextMenuExtensionCatalog.ReadPlacement(package.PackageFamilyName)
        }).ToArray();

    private static string GetItemCacheKey(string targetPath)
    {
        var normalizedPath = Path.GetFullPath(targetPath);
        return $"{(Directory.Exists(normalizedPath) ? "directory" : "file")}:{normalizedPath}";
    }

    private static IReadOnlyList<ExplorerCommandPackage> DiscoverDesktopBackgroundCommands(string desktopPath)
    {
        var catalog = new ContextMenuExtensionCatalog().DiscoverDesktopBackgroundExtensions();
        if (!TryCreateShellItemArray(desktopPath, out var itemArray))
            return [];

        var packages = new List<ExplorerCommandPackage>();
        try
        {
            foreach (var extension in catalog.Extensions)
            {
                var commands = new List<ExplorerCommandNode>();
                foreach (var registration in extension.Commands)
                {
                    var command = ReadRegisteredCommand(registration.ClassId, extension.DisplayName, itemArray);
                    if (command is not null)
                        commands.Add(command);
                }

                if (commands.Count > 0)
                {
                    packages.Add(new ExplorerCommandPackage(
                        extension.PackageFamilyName,
                        extension.DisplayName,
                        extension.Placement,
                        commands));
                }
            }
        }
        finally { NativeCom.Release(itemArray); }

        return packages;
    }

    private static IReadOnlyList<ExplorerCommandPackage> DiscoverItemCommands(string targetPath)
    {
        var catalog = new ContextMenuExtensionCatalog().DiscoverItemExtensions(targetPath);
        if (!TryCreateShellItemArray(targetPath, out var itemArray))
            return [];

        var packages = new List<ExplorerCommandPackage>();
        try
        {
            foreach (var extension in catalog.Extensions)
            {
                var commands = new List<ExplorerCommandNode>();
                foreach (var registration in extension.Commands)
                {
                    var command = ReadRegisteredCommand(registration.ClassId, extension.DisplayName, itemArray);
                    if (command is not null)
                        commands.Add(command);
                }

                if (commands.Count > 0)
                {
                    packages.Add(new ExplorerCommandPackage(
                        extension.PackageFamilyName,
                        extension.DisplayName,
                        extension.Placement,
                        commands));
                }
            }
        }
        finally { NativeCom.Release(itemArray); }

        return packages;
    }

    private static ExplorerCommandNode? ReadRegisteredCommand(Guid classId, string fallbackRootTitle, IntPtr items = default)
    {
        IntPtr command = IntPtr.Zero;
        try
        {
            command = TryActivateExplorerCommand(classId);
            if (command == IntPtr.Zero)
                return null;

            return ReadCommand(command, classId, [], fallbackRootTitle, items, depth: 0);
        }
        catch (Exception exception) when (IsExpectedCommandFailure(exception))
        {
            Debug.WriteLine($"Could not read desktop context-menu command {classId}: {exception.Message}");
            return null;
        }
        finally { NativeCom.Release(command); }
    }

    private static IntPtr TryActivateExplorerCommand(Guid classId)
    {
        try
        {
            return NativeCom.CreateInstance(classId, ExplorerCommandInterfaceId, ExplorerCommandClassContext);
        }
        catch (Exception exception) when (IsExpectedCommandFailure(exception))
        {
            Debug.WriteLine($"Explorer command {classId} does not expose IExplorerCommand: {exception.Message}");
            return IntPtr.Zero;
        }
    }

    private static unsafe ExplorerCommandNode? ReadCommand(
        IntPtr command,
        Guid classId,
        IReadOnlyList<int> path,
        string? fallbackRootTitle,
        IntPtr items,
        int depth)
    {
        IntPtr titlePointer = IntPtr.Zero;
        IntPtr iconPointer = IntPtr.Zero;
        IntPtr toolTipPointer = IntPtr.Zero;
        try
        {
            var titleResult = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)NativeCom.GetMethod(command, 3))(
                command, items, &titlePointer);
            var title = titleResult >= 0 ? Marshal.PtrToStringUni(titlePointer)?.Trim() : null;

            var iconLocation = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)NativeCom.GetMethod(command, 4))(
                command, items, &iconPointer) >= 0
                ? Marshal.PtrToStringUni(iconPointer)?.Trim()
                : null;
            var toolTip = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)NativeCom.GetMethod(command, 5))(
                command, items, &toolTipPointer) >= 0
                ? Marshal.PtrToStringUni(toolTipPointer)?.Trim()
                : null;

            uint state = 0;
            var stateResult = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, uint*, int>)NativeCom.GetMethod(command, 7))(
                command, items, 1, &state);
            if (stateResult >= 0 && (state & CommandStateHidden) != 0)
                return null;

            var children = new List<ExplorerCommandNode>();
            if (depth < MaximumMenuDepth &&
                GetCommandFlags(command, out var flags) >= 0 &&
                (flags & CommandFlagHasSubCommands) != 0 &&
                TryGetSubCommands(command, out var enumerator) >= 0 && enumerator != IntPtr.Zero)
            {
                try
                {
                    for (var index = 0; index < MaximumSubCommands; index++)
                    {
                        if (!TryGetNextCommand(enumerator, out var childCommand))
                            break;

                        try
                        {
                            var childPath = new int[path.Count + 1];
                            for (var pathIndex = 0; pathIndex < path.Count; pathIndex++)
                                childPath[pathIndex] = path[pathIndex];
                            childPath[^1] = index;

                            var child = ReadCommand(childCommand, classId, childPath, null, items, depth + 1);
                            if (child is not null)
                                children.Add(child);
                        }
                        finally { NativeCom.Release(childCommand); }
                    }
                }
                finally
                {
                    NativeCom.Release(enumerator);
                }
            }

            if (!IsDisplayableCommandTitle(title))
            {
                // Some packaged commands expose only an internal Verb Id as their
                // title. Never show those identifiers. A root with real children can
                // still be represented by its package's display name.
                title = path.Count == 0 && children.Count > 0 && IsDisplayableCommandTitle(fallbackRootTitle)
                    ? fallbackRootTitle!.Trim()
                    : null;
            }

            if (title is null)
                return null;

            return new ExplorerCommandNode(
                classId,
                path,
                title,
                iconLocation,
                toolTip,
                stateResult >= 0 && (state & CommandStateDisabled) != 0,
                children);
        }
        catch (Exception exception) when (IsExpectedCommandFailure(exception))
        {
            Debug.WriteLine($"Could not read an Explorer command: {exception.Message}");
            return null;
        }
        finally
        {
            FreeCoTaskMem(titlePointer);
            FreeCoTaskMem(iconPointer);
            FreeCoTaskMem(toolTipPointer);
        }
    }

    private static bool IsDisplayableCommandTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return false;

        var candidate = title.Trim();
        if (candidate == "*" || candidate.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
            return false;

        var hasNumericPrefix = candidate.Length > 0 && char.IsDigit(candidate[0]);
        return !hasNumericPrefix || !candidate.EndsWith("ShellExtension", StringComparison.OrdinalIgnoreCase);
    }

    private static unsafe void InvokeOnSta(Guid classId, IReadOnlyList<int> subCommandPath, string? targetPath)
    {
        IntPtr command = IntPtr.Zero;
        IntPtr items = IntPtr.Zero;
        try
        {
            if (!string.IsNullOrWhiteSpace(targetPath) && !TryCreateShellItemArray(targetPath, out items))
                throw new InvalidOperationException($"The Shell item could not be created for '{targetPath}'.");

            command = TryActivateExplorerCommand(classId);
            if (command == IntPtr.Zero)
                throw new InvalidOperationException($"Explorer command {classId} does not expose IExplorerCommand.");

            foreach (var childIndex in subCommandPath)
            {
                if (childIndex < 0 || childIndex >= MaximumSubCommands)
                    throw new ArgumentOutOfRangeException(nameof(subCommandPath));

                if (TryGetSubCommands(command, out var enumerator) < 0 || enumerator == IntPtr.Zero)
                    throw new InvalidOperationException("The Explorer command does not expose the requested submenu.");

                IntPtr selectedChild = IntPtr.Zero;
                try
                {
                    for (var index = 0; index <= childIndex; index++)
                    {
                        if (!TryGetNextCommand(enumerator, out var child))
                            throw new InvalidOperationException("The Explorer command submenu changed before it was invoked.");

                        if (index == childIndex)
                            selectedChild = child;
                        else
                            NativeCom.Release(child);
                    }
                }
                finally
                {
                    NativeCom.Release(enumerator);
                }

                NativeCom.Release(command);
                command = selectedChild;
            }

            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, int>)NativeCom.GetMethod(command, 8))(
                command, items, IntPtr.Zero));
        }
        finally
        {
            NativeCom.Release(command);
            NativeCom.Release(items);
        }
    }

    private static bool TryCreateShellItemArray(string path, out IntPtr itemArray)
    {
        itemArray = IntPtr.Zero;
        IntPtr shellItem = IntPtr.Zero;
        var created = false;
        try
        {
            var shellItemId = ShellItemInterfaceId;
            if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref shellItemId, out shellItem) < 0 || shellItem == IntPtr.Zero)
                return false;

            var itemArrayId = ShellItemArrayInterfaceId;
            created = SHCreateShellItemArrayFromShellItem(shellItem, ref itemArrayId, out itemArray) >= 0 && itemArray != IntPtr.Zero;
            return created;
        }
        catch (Exception exception) when (IsExpectedCommandFailure(exception))
        {
            Debug.WriteLine($"Could not bind Shell item '{path}' to an item array: {exception.Message}");
            return false;
        }
        finally
        {
            NativeCom.Release(shellItem);
            if (!created)
            {
                NativeCom.Release(itemArray);
                itemArray = IntPtr.Zero;
            }
        }
    }

    private static unsafe int GetCommandFlags(IntPtr command, out uint flags)
    {
        // IExplorerCommand follows IUnknown; GetFlags and EnumSubCommands are slots 9 and 10.
        uint nativeFlags = 0;
        var result = ((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)NativeCom.GetMethod(command, 9))(
            command, &nativeFlags);
        flags = nativeFlags;
        return result;
    }

    private static unsafe int TryGetSubCommands(IntPtr command, out IntPtr enumerator)
    {
        IntPtr nativeEnumerator = IntPtr.Zero;
        var result = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)NativeCom.GetMethod(command, 10))(
            command, &nativeEnumerator);
        if (result < 0)
        {
            NativeCom.Release(nativeEnumerator);
            nativeEnumerator = IntPtr.Zero;
        }
        enumerator = nativeEnumerator;
        return result;
    }

    private static unsafe bool TryGetNextCommand(IntPtr enumerator, out IntPtr command)
    {
        IntPtr nativeCommand = IntPtr.Zero;
        uint fetched = 0;
        var result = ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, uint*, int>)NativeCom.GetMethod(enumerator, 3))(
            enumerator, 1, &nativeCommand, &fetched);
        if (result < 0 || fetched != 1 || nativeCommand == IntPtr.Zero)
        {
            NativeCom.Release(nativeCommand);
            command = IntPtr.Zero;
            return false;
        }
        command = nativeCommand;
        return true;
    }

    private static T RunOnSta<T>(Func<T> callback)
    {
        T result = default!;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var initialized = false;
            try
            {
                var initializationResult = CoInitializeEx(IntPtr.Zero, CoinitApartmentThreaded);
                initialized = initializationResult >= 0;
                if (initializationResult == RpcChangedMode)
                    Debug.WriteLine("Explorer command thread already has a different COM apartment; using the existing apartment.");
                else
                    Marshal.ThrowExceptionForHR(initializationResult);
                result = callback();
            }
            catch (Exception exception) { failure = exception; }
            finally
            {
                if (initialized)
                    CoUninitialize();
            }
        })
        {
            IsBackground = true,
            Name = "XiyiDesktop Explorer command adapter"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            throw failure;
        return result;
    }

    private static void RunOnSta(Action callback) => RunOnSta(() =>
    {
        callback();
        return true;
    });

    private static bool IsExpectedCommandFailure(Exception exception) =>
        exception is COMException or InvalidCastException or InvalidOperationException or
            ArgumentException or UnauthorizedAccessException or TypeLoadException or
            NotSupportedException or System.Security.SecurityException;

    private static void FreeCoTaskMem(IntPtr value)
    {
        if (value != IntPtr.Zero)
            Marshal.FreeCoTaskMem(value);
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(IntPtr reserved, uint flags);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        string path,
        IntPtr bindContext,
        ref Guid interfaceId,
        out IntPtr shellItem);

    [DllImport("shell32.dll", PreserveSig = true)]
    private static extern int SHCreateShellItemArrayFromShellItem(
        IntPtr shellItem,
        ref Guid interfaceId,
        out IntPtr itemArray);
}

internal sealed record ExplorerCommandPackage(
    string PackageFamilyName,
    string DisplayName,
    DesktopContextMenuExtensionPlacement Placement,
    IReadOnlyList<ExplorerCommandNode> Commands);

internal sealed record ExplorerCommandNode(
    Guid ClassId,
    IReadOnlyList<int> SubCommandPath,
    string Title,
    string? IconLocation,
    string? ToolTip,
    bool IsDisabled,
    IReadOnlyList<ExplorerCommandNode> Children);
