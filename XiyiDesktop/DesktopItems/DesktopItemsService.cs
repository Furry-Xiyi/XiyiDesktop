using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using XiyiDesktop.ApplicationActivation;
using XiyiDesktop.Interop;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace XiyiDesktop.DesktopItems;

/// <summary>
/// Enumerates the shell-resolved user and public desktop folders, loads native Shell
/// thumbnails/icons for filesystem entries, and signals debounced filesystem changes.
/// </summary>
public sealed class DesktopItemsService : IDisposable
{
    private static readonly Guid UserDesktopFolderId = new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641");
    private static readonly Guid PublicDesktopFolderId = new("C4AA340D-F20F-4863-AFEF-F87EF2E6BA25");
    private static readonly TimeSpan ChangeDebounceDelay = TimeSpan.FromMilliseconds(350);
    private static readonly Guid ShellItemImageFactoryId = new("BCC18B79-BA16-442F-80C4-8A59C30C463B");
    private const uint ShgfiIcon = 0x00000100;
    private const uint ShgfiSmallIcon = 0x00000001;
    private const uint ShgfiOverlayIndex = 0x00000040;
    private const int IdoShgioiLink = 0x0FFFFFFE;
    private const uint ShellImageIconOnly = 0x00000004;
    private const uint DibRgb = 0;
    private static readonly Lazy<int> ShortcutOverlayIndex = new(() => SHGetIconOverlayIndex(null, IdoShgioiLink));

    private readonly object _sync = new();
    private readonly Timer _changeTimer;
    private readonly CancellationTokenSource _systemIconSettingsCancellation = new();
    private readonly Task _systemIconSettingsWatcher;
    private readonly List<FileSystemWatcher> _watchers = [];
    private bool _includeHiddenItems;
    private bool _includeSpecialItems = true;
    private bool _disposed;

    public DesktopItemsService()
    {
        _changeTimer = new Timer(OnChangeTimer, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _systemIconSettingsWatcher = Task.Run(() => WatchSystemIconSettings(_systemIconSettingsCancellation.Token));
    }

    /// <summary>Raised on a thread-pool thread after desktop filesystem changes settle.</summary>
    public event EventHandler? ItemsChanged;

    /// <summary>Whether hidden and system files should be included in enumeration.</summary>
    public bool IncludeHiddenItems
    {
        get
        {
            lock (_sync)
            {
                return _includeHiddenItems;
            }
        }
        set
        {
            lock (_sync)
            {
                ThrowIfDisposed();
                _includeHiddenItems = value;
            }
        }
    }

    /// <summary>Whether Windows-configured shell desktop entries should be included.</summary>
    public bool IncludeSpecialItems
    {
        get
        {
            lock (_sync)
            {
                return _includeSpecialItems;
            }
        }
        set
        {
            lock (_sync)
            {
                ThrowIfDisposed();
                _includeSpecialItems = value;
            }
        }
    }

    /// <summary>
    /// Returns the top-level entries visible on the desktop. Known-folder paths are resolved
    /// through the Shell, so folder redirection and OneDrive desktop backup are respected.
    /// </summary>
    public Task<IReadOnlyList<DesktopItem>> GetItemsAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var includeHidden = IncludeHiddenItems;
        var includeSpecial = IncludeSpecialItems;
        return Task.Run<IReadOnlyList<DesktopItem>>(
            () => EnumerateItems(includeHidden, includeSpecial, cancellationToken),
            cancellationToken);
    }

    /// <summary>
    /// Loads the normal Shell image for a file or folder. The returned bitmap is created on
    /// the caller's UI context; call this from the UI thread before binding it to a WinUI view.
    /// Shell-only entries use their Shell namespace icon and never substitute a font glyph.
    /// </summary>
    public async Task<BitmapImage?> LoadIconAsync(
        DesktopItem item,
        uint pixelSize = 96,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ThrowIfDisposed();

        if (pixelSize == 0)
        {
            return null;
        }

        var shellParsingName = item.Path ?? GetSpecialShellParsingName(item.Kind);
        if (shellParsingName is null)
            return null;

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            if (item.Path is { } filePath)
                await LoadOneDriveOverlayAsync(item, filePath, cancellationToken);

            if (item.IsSpecial)
            {
                // A Shell namespace provider may not be ready for the first request during
                // desktop startup. Retry both native lookup paths before giving up.
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    if (attempt > 0)
                        await Task.Delay(TimeSpan.FromMilliseconds(150 * attempt), cancellationToken);

                    var specialShellIcon = await Task.Run(
                        () => TryGetShellIconPixels(shellParsingName, pixelSize, cancellationToken),
                        cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();

                    if (specialShellIcon is not null)
                    {
                        try
                        {
                            var shellBitmap = await CreateShellBitmapAsync(specialShellIcon);
                            cancellationToken.ThrowIfCancellationRequested();
                            item.Icon = shellBitmap;
                            return shellBitmap;
                        }
                        catch (Exception exception) when (IsExpectedItemAccessFailure(exception))
                        {
                            System.Diagnostics.Debug.WriteLine(
                                $"Unable to decode the Shell image for '{shellParsingName}': {exception.Message}");
                        }
                    }

                    var iconPath = await ShellIconService.ExtractShellItemIconPathAsync(shellParsingName);
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!string.IsNullOrWhiteSpace(iconPath))
                    {
                        try
                        {
                            var iconFile = await StorageFile.GetFileFromPathAsync(iconPath);
                            using var iconStream = await iconFile.OpenReadAsync();
                            var fallbackBitmap = new BitmapImage();
                            await fallbackBitmap.SetSourceAsync(iconStream);
                            cancellationToken.ThrowIfCancellationRequested();
                            item.Icon = fallbackBitmap;
                            return fallbackBitmap;
                        }
                        catch (Exception exception) when (IsExpectedItemAccessFailure(exception))
                        {
                            System.Diagnostics.Debug.WriteLine(
                                $"Unable to decode the Shell icon for '{shellParsingName}': {exception.Message}");
                        }
                    }
                }

                return null;
            }

            if (item.IsShortcut)
            {
                var shellIcon = await Task.Run(
                    () => TryGetShellIconPixels(shellParsingName, pixelSize, cancellationToken),
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                if (shellIcon is not null)
                {
                    var bitmapSource = await CreateShellBitmapAsync(shellIcon);
                    cancellationToken.ThrowIfCancellationRequested();
                    item.Icon = bitmapSource;
                    return bitmapSource;
                }
            }

            if (item.Path is null)
                return null;

            using var thumbnail = item.IsDirectory
                ? await (await StorageFolder.GetFolderFromPathAsync(item.Path)).GetThumbnailAsync(
                    ThumbnailMode.SingleItem,
                    pixelSize,
                    ThumbnailOptions.UseCurrentScale)
                : await (await StorageFile.GetFileFromPathAsync(item.Path)).GetThumbnailAsync(
                    ThumbnailMode.SingleItem,
                    pixelSize,
                    ThumbnailOptions.UseCurrentScale);

            cancellationToken.ThrowIfCancellationRequested();
            if (thumbnail is null || thumbnail.Size == 0)
            {
                return null;
            }

            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(thumbnail);
            cancellationToken.ThrowIfCancellationRequested();
            item.Icon = bitmap;
            return bitmap;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedItemAccessFailure(exception))
        {
            // Items can disappear or become inaccessible while their thumbnail is loading.
            return null;
        }
    }

    private static async Task<BitmapImage> CreateShellBitmapAsync(ShellIconPixels shellIcon)
    {
        var pngBytes = await ShellIconService.EncodeBgraPngAsync(
            shellIcon.Pixels,
            shellIcon.Width,
            shellIcon.Height,
            BitmapAlphaMode.Premultiplied);
        return await CreateBitmapImageFromPngAsync(pngBytes);
    }

    private static async Task LoadOneDriveOverlayAsync(
        DesktopItem item,
        string path,
        CancellationToken cancellationToken)
    {
        item.ShellStatusOverlay = null;
        try
        {
            var overlayIndex = await Task.Run(() =>
            {
                using var apartment = NativeCom.InitializeApartment(0, allowDifferentApartment: true);
                return GetShellOverlayIndex(path);
            }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            // Desktop shortcuts already get the separate shortcut arrow from the appearance setting.
            // Avoid drawing the exact same Shell overlay a second time.
            if (overlayIndex <= 0 ||
                (item.IsShortcut && overlayIndex == ShortcutOverlayIndex.Value))
                return;

            var overlayPath = await ShellIconService.ExtractSystemOverlayIconPathAsync(
                overlayIndex,
                $"filesystem-overlay:{overlayIndex}");
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(overlayPath))
                return;

            var overlayFile = await StorageFile.GetFileFromPathAsync(overlayPath);
            using var overlayStream = await overlayFile.OpenReadAsync();
            var overlayBitmap = new BitmapImage();
            await overlayBitmap.SetSourceAsync(overlayStream);
            cancellationToken.ThrowIfCancellationRequested();
            item.ShellStatusOverlay = overlayBitmap;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedItemAccessFailure(exception) || exception is COMException)
        {
            Debug.WriteLine($"Unable to load the Shell status overlay for '{path}': {exception.Message}");
        }
    }

    private static int GetShellOverlayIndex(string path)
    {
        var result = SHGetFileInfo(
            path,
            0,
            out var info,
            (uint)Marshal.SizeOf<ShellFileInfo>(),
            ShgfiIcon | ShgfiSmallIcon | ShgfiOverlayIndex);
        try
        {
            return result == IntPtr.Zero
                ? 0
                : (int)(((uint)info.IconIndex >> 24) & 0xFF);
        }
        finally
        {
            if (info.Icon != IntPtr.Zero)
                DestroyIcon(info.Icon);
        }
    }

    private static async Task<BitmapImage> CreateBitmapImageFromPngAsync(byte[] pngBytes)
    {
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(pngBytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }

        stream.Seek(0);
        var bitmapSource = new BitmapImage();
        await bitmapSource.SetSourceAsync(stream);
        return bitmapSource;
    }

    internal static async Task<byte[]?> ExtractShellItemIconPngAsync(string parsingName)
    {
        if (string.IsNullOrWhiteSpace(parsingName))
            return null;

        try
        {
            var shellIcon = await Task.Run(
                () => TryGetShellIconPixels(parsingName, 32, CancellationToken.None)).ConfigureAwait(false);
            if (shellIcon is null)
                return null;

            return await ShellIconService.EncodeBgraPngAsync(
                shellIcon.Pixels,
                shellIcon.Width,
                shellIcon.Height,
                BitmapAlphaMode.Premultiplied).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsExpectedItemAccessFailure(exception) || exception is COMException)
        {
            Debug.WriteLine($"Unable to encode the Shell icon for '{parsingName}': {exception.Message}");
            return null;
        }
    }

    /// <summary>Opens a file/folder with its registered shell handler, or a special shell view.</summary>
    public Task<bool> OpenAsync(DesktopItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        ThrowIfDisposed();

        if (item.Kind == DesktopItemKind.ThisPc)
        {
            try
            {
                ShellActivationService.OpenThisPc();
                return Task.FromResult(true);
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or COMException)
            {
                return Task.FromResult(false);
            }
        }

        if (item.Kind == DesktopItemKind.RecycleBin)
        {
            try
            {
                ShellActivationService.OpenRecycleBin();
                return Task.FromResult(true);
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or COMException)
            {
                return Task.FromResult(false);
            }
        }

        if (item.IsSpecial)
        {
            try
            {
                ShellActivationService.Open(DesktopItem.GetShellParsingName(item.Kind));
                return Task.FromResult(true);
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or COMException)
            {
                return Task.FromResult(false);
            }
        }

        if (item.Path is null)
        {
            return Task.FromResult(false);
        }

        try
        {
            ShellActivationService.Open(item.Path);
            return Task.FromResult(true);
        }
        catch (Exception exception) when (IsExpectedItemAccessFailure(exception))
        {
            return Task.FromResult(false);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var watcher in _watchers)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }

            _watchers.Clear();
            _changeTimer.Dispose();
            _systemIconSettingsCancellation.Cancel();
            ItemsChanged = null;
        }
    }

    private IReadOnlyList<DesktopItem> EnumerateItems(
        bool includeHidden,
        bool includeSpecial,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var folders = ResolveDesktopFolders();
        ConfigureWatchers(folders);

        var items = new List<DesktopItem>();
        if (includeSpecial)
            items.AddRange(DesktopSystemIconSettings.GetVisibleItems());

        var pathsSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var folder in folders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(folder.Path))
            {
                continue;
            }

            try
            {
                foreach (var path in Directory.EnumerateFileSystemEntries(folder.Path))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!pathsSeen.Add(path))
                    {
                        continue;
                    }

                    var item = TryCreateItem(path, folder.Source);
                    if (item is not null && (includeHidden || !item.IsHidden))
                    {
                        items.Add(item);
                    }
                }
            }
            catch (Exception exception) when (IsExpectedFolderAccessFailure(exception))
            {
                // A redirected or removable folder can become unavailable during enumeration.
            }
        }

        return items;
    }

    private IReadOnlyList<DesktopFolderLocation> ResolveDesktopFolders()
    {
        var locations = new List<DesktopFolderLocation>(capacity: 2);

        var userPath = TryGetKnownFolderPath(UserDesktopFolderId);
        if (string.IsNullOrWhiteSpace(userPath))
        {
            userPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        }

        if (!string.IsNullOrWhiteSpace(userPath))
        {
            locations.Add(new DesktopFolderLocation(Path.GetFullPath(userPath), DesktopItemSource.UserDesktop));
        }

        var publicPath = TryGetKnownFolderPath(PublicDesktopFolderId);
        if (!string.IsNullOrWhiteSpace(publicPath))
        {
            var normalizedPath = Path.GetFullPath(publicPath);
            if (!locations.Any(location => string.Equals(location.Path, normalizedPath, StringComparison.OrdinalIgnoreCase)))
            {
                locations.Add(new DesktopFolderLocation(normalizedPath, DesktopItemSource.PublicDesktop));
            }
        }

        return locations;
    }

    private void ConfigureWatchers(IReadOnlyList<DesktopFolderLocation> folders)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            var desiredPaths = folders
                .Select(folder => folder.Path)
                .Where(Directory.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var existingPaths = _watchers
                .Select(watcher => watcher.Path)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (desiredPaths.SetEquals(existingPaths))
            {
                return;
            }

            foreach (var watcher in _watchers)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }

            _watchers.Clear();
            foreach (var path in desiredPaths)
            {
                try
                {
                    var watcher = new FileSystemWatcher(path)
                    {
                        IncludeSubdirectories = false,
                        NotifyFilter = NotifyFilters.FileName |
                            NotifyFilters.DirectoryName |
                            NotifyFilters.LastWrite |
                            NotifyFilters.Attributes,
                        InternalBufferSize = 16 * 1024,
                        EnableRaisingEvents = false
                    };

                    watcher.Created += OnFileSystemChanged;
                    watcher.Deleted += OnFileSystemChanged;
                    watcher.Changed += OnFileSystemChanged;
                    watcher.Renamed += OnFileSystemRenamed;
                    watcher.Error += OnFileSystemError;
                    _watchers.Add(watcher);
                    watcher.EnableRaisingEvents = true;
                }
                catch (Exception exception) when (IsExpectedFolderAccessFailure(exception))
                {
                    // Keep other desktop-folder watchers even when one location cannot be watched.
                }
            }
        }
    }

    private void WatchSystemIconSettings(CancellationToken cancellationToken)
    {
        const uint notifyName = 0x00000001;
        const uint notifyLastSet = 0x00000004;
        const string explorerPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer";
        const string hideDesktopIconsPath = explorerPath + @"\HideDesktopIcons";

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var iconSettingsKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(hideDesktopIconsPath);
                using var parentKey = iconSettingsKey is null
                    ? Microsoft.Win32.Registry.CurrentUser.OpenSubKey(explorerPath)
                    : null;
                var key = iconSettingsKey ?? parentKey;
                if (key is null)
                {
                    cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(2));
                    continue;
                }

                using var changedEvent = new EventWaitHandle(false, EventResetMode.AutoReset);
                var result = RegNotifyChangeKeyValue(
                    key.Handle,
                    watchSubtree: iconSettingsKey is not null,
                    notifyFilter: iconSettingsKey is not null ? notifyName | notifyLastSet : notifyName,
                    changedEvent.SafeWaitHandle,
                    asynchronous: true);
                if (result != 0)
                {
                    Debug.WriteLine($"Unable to watch Windows desktop icon settings: registry error {result}.");
                    cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(2));
                    continue;
                }

                var signaled = WaitHandle.WaitAny([changedEvent, cancellationToken.WaitHandle]);
                if (signaled == 0 && !cancellationToken.IsCancellationRequested)
                    ScheduleChangeNotification();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or ObjectDisposedException)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    Debug.WriteLine($"Unable to watch Windows desktop icon settings: {exception.Message}");
                    cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(2));
                }
            }
        }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegNotifyChangeKeyValue(
        SafeRegistryHandle key,
        [MarshalAs(UnmanagedType.Bool)] bool watchSubtree,
        uint notifyFilter,
        SafeWaitHandle eventHandle,
        [MarshalAs(UnmanagedType.Bool)] bool asynchronous);

    private static DesktopItem? TryCreateItem(string path, DesktopItemSource source)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            var isDirectory = (attributes & System.IO.FileAttributes.Directory) != 0;
            var name = Path.GetFileName(path);
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }

            var extension = Path.GetExtension(name);
            if (!isDirectory && IsShortcutExtension(extension))
                name = Path.GetFileNameWithoutExtension(name);

            long? size = null;
            DateTimeOffset? created = null;
            DateTimeOffset? modified = null;

            if (isDirectory)
            {
                var directory = new DirectoryInfo(path);
                created = ToUtc(directory.CreationTimeUtc);
                modified = ToUtc(directory.LastWriteTimeUtc);
            }
            else
            {
                var file = new FileInfo(path);
                size = file.Length;
                created = ToUtc(file.CreationTimeUtc);
                modified = ToUtc(file.LastWriteTimeUtc);
            }

            return DesktopItem.CreateFileSystemItem(
                path: Path.GetFullPath(path),
                name,
                isDirectory ? DesktopItemKind.Folder : DesktopItemKind.File,
                source,
                isDirectory,
                size,
                created,
                modified,
                isDirectory ? null : extension,
                attributes);
        }
        catch (Exception exception) when (IsExpectedFolderAccessFailure(exception))
        {
            // Filesystem state can change between enumeration and metadata access.
            return null;
        }
    }

    private static DateTimeOffset ToUtc(DateTime utcTime) =>
        new(DateTime.SpecifyKind(utcTime, DateTimeKind.Utc));

    private static string? GetSpecialShellParsingName(DesktopItemKind kind) => kind switch
    {
        DesktopItemKind.ThisPc or
        DesktopItemKind.UserFiles or
        DesktopItemKind.Network or
        DesktopItemKind.RecycleBin or
        DesktopItemKind.ControlPanel => DesktopItem.GetShellParsingName(kind),
        _ => null
    };

    private static bool IsShortcutExtension(string extension) =>
        string.Equals(extension, ".lnk", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(extension, ".url", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(extension, ".appref-ms", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Asks the Shell to resolve a shortcut's configured/target icon instead of treating
    /// the .lnk file itself as a generic document thumbnail. The returned native bitmap
    /// is copied to managed pixels and released before the call returns.
    /// </summary>
    private static unsafe ShellIconPixels? TryGetShellIconPixels(string path, uint requestedSize, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // This method runs on a pool thread; raw COM calls do not initialize its apartment.
        try
        {
            using var apartment = NativeCom.InitializeApartment(0, allowDifferentApartment: true);
            return TryGetShellIconPixelsInInitializedApartment(path, requestedSize, cancellationToken);
        }
        catch (COMException)
        {
            return null;
        }
    }

    private static unsafe ShellIconPixels? TryGetShellIconPixelsInInitializedApartment(
        string path, uint requestedSize, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IntPtr imageFactory = IntPtr.Zero;
        IntPtr bitmapHandle = IntPtr.Zero;
        try
        {
            var interfaceId = ShellItemImageFactoryId;
            var hr = SHCreateItemFromParsingName(path, IntPtr.Zero, ref interfaceId, out imageFactory);
            if (hr < 0 || imageFactory == IntPtr.Zero)
                return null;

            cancellationToken.ThrowIfCancellationRequested();
            var size = new NativeSize
            {
                Width = checked((int)Math.Min(requestedSize, 512)),
                Height = checked((int)Math.Min(requestedSize, 512))
            };
            // IShellItemImageFactory::GetImage follows IUnknown's three methods.
            var getImage = (delegate* unmanaged[Stdcall]<IntPtr, NativeSize, uint, IntPtr*, int>)
                NativeCom.GetMethod(imageFactory, 3);
            hr = getImage(imageFactory, size, ShellImageIconOnly, &bitmapHandle);
            if (hr < 0 || bitmapHandle == IntPtr.Zero)
            {
                return null;
            }

            var bitmap = new NativeBitmap();
            if (GetObject(bitmapHandle, Marshal.SizeOf<NativeBitmap>(), out bitmap) == 0 ||
                bitmap.Width <= 0 || bitmap.Height <= 0 || bitmap.Width > 512 || bitmap.Height > 512)
            {
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var pixels = new byte[checked(bitmap.Width * bitmap.Height * 4)];
            var info = new NativeBitmapInfo
            {
                Header = new NativeBitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<NativeBitmapInfoHeader>(),
                    Width = bitmap.Width,
                    Height = -bitmap.Height,
                    Planes = 1,
                    BitCount = 32,
                    Compression = DibRgb,
                    SizeImage = (uint)pixels.Length
                }
            };

            var deviceContext = GetDC(IntPtr.Zero);
            if (deviceContext == IntPtr.Zero)
            {
                return null;
            }

            int copiedScanlines;
            try
            {
                copiedScanlines = GetDIBits(
                    deviceContext,
                    bitmapHandle,
                    0,
                    (uint)bitmap.Height,
                    pixels,
                    ref info,
                    DibRgb);
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, deviceContext);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return copiedScanlines == bitmap.Height
                ? new ShellIconPixels(bitmap.Width, bitmap.Height, pixels)
                : null;
        }
        finally
        {
            if (bitmapHandle != IntPtr.Zero)
            {
                DeleteObject(bitmapHandle);
            }

            NativeCom.Release(imageFactory);
        }
    }

    private static string? TryGetKnownFolderPath(Guid folderId)
    {
        var result = SHGetKnownFolderPath(ref folderId, 0, IntPtr.Zero, out var pathPointer);
        try
        {
            if (result < 0 || pathPointer == IntPtr.Zero)
            {
                return null;
            }

            return Marshal.PtrToStringUni(pathPointer);
        }
        finally
        {
            if (pathPointer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(pathPointer);
            }
        }
    }

    private void OnFileSystemChanged(object sender, FileSystemEventArgs args) => ScheduleChangeNotification();

    private void OnFileSystemRenamed(object sender, RenamedEventArgs args) => ScheduleChangeNotification();

    private void OnFileSystemError(object sender, ErrorEventArgs args) => ScheduleChangeNotification();

    private void ScheduleChangeNotification()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _changeTimer.Change(ChangeDebounceDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnChangeTimer(object? state)
    {
        EventHandler? handler;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            handler = ItemsChanged;
        }

        if (handler is null)
        {
            return;
        }

        foreach (EventHandler subscriber in handler.GetInvocationList())
        {
            try
            {
                subscriber(this, EventArgs.Empty);
            }
            catch (Exception exception)
            {
                // A client callback must not escape a thread-pool timer and terminate the app.
                Debug.WriteLine($"Desktop item change callback failed: {exception}");
            }
        }
    }

    private static bool IsExpectedItemAccessFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException or
            ArgumentException or COMException or InvalidOperationException;

    private static bool IsExpectedFolderAccessFailure(Exception exception) =>
        exception is UnauthorizedAccessException or SecurityException or IOException or
            ArgumentException or NotSupportedException;

    private void ThrowIfDisposed()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
    }

    private readonly record struct DesktopFolderLocation(string Path, DesktopItemSource Source);

    private sealed record ShellIconPixels(int Width, int Height, byte[] Pixels);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int Width;
        public int Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBitmap
    {
        public int Type;
        public int Width;
        public int Height;
        public int WidthBytes;
        public ushort Planes;
        public ushort BitsPixel;
        public IntPtr Bits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XpelsPerMeter;
        public int YpelsPerMeter;
        public uint ColorsUsed;
        public uint ColorsImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBitmapInfo
    {
        public NativeBitmapInfoHeader Header;
        public uint Color;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    [DllImport("shell32.dll", EntryPoint = "SHGetFileInfoW", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string path,
        uint fileAttributes,
        out ShellFileInfo fileInfo,
        uint fileInfoSize,
        uint flags);

    [DllImport("shell32.dll", EntryPoint = "SHGetIconOverlayIndexW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHGetIconOverlayIndex(string? iconPath, int iconIndex);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string path,
        IntPtr bindContext,
        ref Guid interfaceId,
        out IntPtr imageFactory);

    [DllImport("gdi32.dll", EntryPoint = "GetObjectW", SetLastError = true)]
    private static extern int GetObject(IntPtr graphicsObject, int size, out NativeBitmap bitmap);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int GetDIBits(
        IntPtr deviceContext,
        IntPtr bitmap,
        uint startScanline,
        uint scanlineCount,
        [Out] byte[] pixels,
        ref NativeBitmapInfo bitmapInfo,
        uint usage);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr graphicsObject);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr windowHandle, IntPtr deviceContext);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHGetKnownFolderPath(
        ref Guid rfid,
        uint dwFlags,
        IntPtr hToken,
        out IntPtr ppszPath);
}
