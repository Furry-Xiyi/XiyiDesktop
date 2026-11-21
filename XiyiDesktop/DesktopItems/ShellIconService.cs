using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Security.Cryptography;
using Windows.Storage.Streams;
using XiyiDesktop.Interop;

namespace XiyiDesktop.DesktopItems;

/// <summary>Loads Shell-associated and indirect resource icons into WinUI image icons.</summary>
internal static class ShellIconService
{
    private const uint ShgfiIcon = 0x00000100;
    private const uint ShgfiPidl = 0x00000008;
    private const uint ShgfiUseFileAttributes = 0x00000010;
    private const uint ShgfiTypeName = 0x00000400;
    private const int ShilSmall = 1;
    private const uint IldTransparent = 0x00000001;
    private static readonly Guid ImageListInterfaceId = new("46EB5926-582E-4017-9FDF-E8998DAA0950");
    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileAttributeDirectory = 0x00000010;
    private static readonly ConcurrentDictionary<string, string> FileTypeNameCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> ShellTypeNameCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Task<string?>> AlphaIconPathCache = new(StringComparer.OrdinalIgnoreCase);
    public static string GetFileTypeName(string? extension, bool isDirectory)
    {
        var normalizedExtension = string.IsNullOrWhiteSpace(extension) ? string.Empty : extension.Trim();
        var cacheKey = isDirectory ? "<directory>" : normalizedExtension;
        return FileTypeNameCache.GetOrAdd(cacheKey, _ => ReadFileTypeName(normalizedExtension, isDirectory));
    }

    public static string GetShellTypeName(string parsingName) =>
        ShellTypeNameCache.GetOrAdd(parsingName, ReadShellTypeName);

    private static string ReadShellTypeName(string parsingName)
    {
        IntPtr itemIdList = IntPtr.Zero;
        try
        {
            if (SHParseDisplayName(parsingName, IntPtr.Zero, out itemIdList, 0, out _) < 0 || itemIdList == IntPtr.Zero)
                return "System folder";

            var result = SHGetFileInfo(
                itemIdList,
                0,
                out var info,
                (uint)Marshal.SizeOf<ShellFileInfo>(),
                ShgfiPidl | ShgfiTypeName);
            return result != IntPtr.Zero && !string.IsNullOrWhiteSpace(info.TypeName)
                ? info.TypeName.Trim()
                : "System folder";
        }
        catch (Exception exception) when (exception is ArgumentException or ExternalException)
        {
            System.Diagnostics.Debug.WriteLine($"Unable to read the Shell type name for '{parsingName}': {exception.Message}");
            return "System folder";
        }
        finally
        {
            if (itemIdList != IntPtr.Zero)
                Marshal.FreeCoTaskMem(itemIdList);
        }
    }

    private static string ReadFileTypeName(string extension, bool isDirectory)
    {
        if (!isDirectory && (extension.Contains('/') || extension.Contains('\\') || extension.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            return extension;

        var probePath = isDirectory ? "XiyiDesktopTypeProbe" : $"XiyiDesktopTypeProbe{extension}";
        var attributes = isDirectory ? FileAttributeDirectory : FileAttributeNormal;
        try
        {
            var result = SHGetFileInfo(
                probePath,
                attributes,
                out var info,
                (uint)Marshal.SizeOf<ShellFileInfo>(),
                ShgfiUseFileAttributes | ShgfiTypeName);
            if (result != IntPtr.Zero && !string.IsNullOrWhiteSpace(info.TypeName))
                return info.TypeName.Trim();
        }
        catch (Exception exception) when (exception is ArgumentException or ExternalException)
        {
            System.Diagnostics.Debug.WriteLine($"Unable to read the Shell type name for '{extension}': {exception.Message}");
        }

        return isDirectory ? "Folder" : string.IsNullOrWhiteSpace(extension) ? "File" : extension;
    }

    public static IconElement? CreateFileTypeIcon(string extension)
        => CreateIconFromPath(ExtractFileTypeIconPath(extension));

    internal static async Task<byte[]> EncodeBgraPngAsync(
        byte[] pixels,
        int width,
        int height,
        BitmapAlphaMode alphaMode)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (width <= 0 || height <= 0 || pixels.Length != checked(width * height * 4))
            throw new ArgumentOutOfRangeException(nameof(width), "The BGRA pixel buffer dimensions are invalid.");

        var pixelBuffer = CryptographicBuffer.CreateFromByteArray(pixels);
        using var softwareBitmap = SoftwareBitmap.CreateCopyFromBuffer(
            pixelBuffer,
            BitmapPixelFormat.Bgra8,
            width,
            height,
            alphaMode);
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream).AsTask().ConfigureAwait(false);
        encoder.SetSoftwareBitmap(softwareBitmap);
        await encoder.FlushAsync().AsTask().ConfigureAwait(false);
        stream.Seek(0);

        var length = checked((uint)stream.Size);
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync(length).AsTask().ConfigureAwait(false);
        var bytes = new byte[checked((int)length)];
        reader.ReadBytes(bytes);
        return bytes;
    }

    /// <summary>Extracts a one-based Shell image-list overlay as a transparent PNG.</summary>
    internal static Task<string?> ExtractSystemOverlayIconPathAsync(int overlayIndex, string cacheKey)
    {
        if (overlayIndex <= 0 || string.IsNullOrWhiteSpace(cacheKey))
            return Task.FromResult<string?>(null);

        return CacheAlphaHIconPathAsync(
            $"system-overlay:{cacheKey}:{overlayIndex}",
            () => ExtractSystemOverlayIcon(overlayIndex));
    }

    private static unsafe IntPtr ExtractSystemOverlayIcon(int overlaySlot)
    {

        var interfaceId = ImageListInterfaceId;
        var result = SHGetImageList(ShilSmall, ref interfaceId, out var imageList);
        if (result < 0 || imageList == IntPtr.Zero)
        {
            NativeCom.Release(imageList);
            return IntPtr.Zero;
        }

        try
        {
            var getOverlayImage = (delegate* unmanaged[Stdcall]<IntPtr, int, int*, int>)
                NativeCom.GetMethod(imageList, 30);
            var imageIndex = -1;
            result = getOverlayImage(imageList, overlaySlot, &imageIndex);
            if (result < 0 || imageIndex < 0)
                return IntPtr.Zero;

            var getIcon = (delegate* unmanaged[Stdcall]<IntPtr, int, uint, IntPtr*, int>)
                NativeCom.GetMethod(imageList, 10);
            IntPtr icon = IntPtr.Zero;
            result = getIcon(imageList, imageIndex, IldTransparent, &icon);
            if (result < 0 || icon == IntPtr.Zero)
            {
                if (icon != IntPtr.Zero)
                    DestroyIcon(icon);
                return IntPtr.Zero;
            }

            return icon;
        }
        catch (Exception exception) when (exception is ArgumentException or COMException or InvalidOperationException)
        {
            System.Diagnostics.Debug.WriteLine($"Unable to extract Shell overlay {overlaySlot}: {exception.Message}");
            return IntPtr.Zero;
        }
        finally
        {
            NativeCom.Release(imageList);
        }
    }

    /// <summary>Extracts the system folder icon and stores it in the shared icon cache.</summary>
    public static string? ExtractDirectoryIconPath() =>
        ExtractDirectoryIconPathAsync().GetAwaiter().GetResult();

    public static Task<string?> ExtractDirectoryIconPathAsync() => CacheAlphaHIconPathAsync("filetype:<directory>", () =>
    {
        var result = SHGetFileInfo(
            "XiyiDesktopFolderIconProbe",
            FileAttributeDirectory,
            out var info,
            (uint)Marshal.SizeOf<ShellFileInfo>(),
            ShgfiIcon | ShgfiUseFileAttributes);
        return result == IntPtr.Zero ? IntPtr.Zero : info.Icon;
    });

    /// <summary>Extracts a Shell item's icon through its alpha-preserving image factory.</summary>
    public static Task<string?> ExtractShellItemIconPathAsync(string? parsingName)
    {
        if (string.IsNullOrWhiteSpace(parsingName))
            return Task.FromResult<string?>(null);

        var normalizedName = parsingName.Trim();
        var cacheKey = $"shellitem-alpha-v2:{normalizedName.ToUpperInvariant()}";
        return AlphaIconPathCache.GetOrAdd(
            cacheKey,
            _ => CacheAlphaPngIconAsync(cacheKey, () => DesktopItemsService.ExtractShellItemIconPngAsync(normalizedName)));
    }

    /// <summary>Extracts and caches a Shell file-type icon without creating WinUI objects.</summary>
    public static string? ExtractFileTypeIconPath(string extension) =>
        ExtractFileTypeIconPathAsync(extension).GetAwaiter().GetResult();

    public static Task<string?> ExtractFileTypeIconPathAsync(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension) || extension[0] != '.' ||
            extension.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            extension.Contains('/') || extension.Contains('\\'))
        {
            return Task.FromResult<string?>(null);
        }

        return CacheAlphaHIconPathAsync($"filetype:{extension.ToLowerInvariant()}", () =>
        {
            var fileName = $"XiyiDesktopIconProbe{extension}";
            var info = new ShellFileInfo();
            var result = SHGetFileInfo(
                fileName,
                FileAttributeNormal,
                out info,
                (uint)Marshal.SizeOf<ShellFileInfo>(),
                ShgfiIcon | ShgfiUseFileAttributes);
            if (result == IntPtr.Zero || info.Icon == IntPtr.Zero)
                return IntPtr.Zero;

            return info.Icon;
        });
    }

    public static IconElement? CreateResourceIcon(string? iconLocation)
    {
        return CreateIconFromPath(ExtractResourceIconPath(iconLocation));
    }

    /// <summary>Extracts a Shell namespace item's real icon into the shared icon cache.</summary>
    public static string? ExtractShellItemIconPath(string? parsingName) =>
        ExtractShellItemIconPathAsync(parsingName).GetAwaiter().GetResult();

    public static IconElement? CreateIconFromPath(string? iconPath) => CreateImageIcon(iconPath);

    /// <summary>Resolves and extracts a Shell icon without creating any WinUI objects.</summary>
    public static string? ExtractResourceIconPath(string? iconLocation) =>
        ExtractResourceIconPathAsync(iconLocation).GetAwaiter().GetResult();

    public static Task<string?> ExtractResourceIconPathAsync(string? iconLocation)
    {
        if (string.IsNullOrWhiteSpace(iconLocation))
            return Task.FromResult<string?>(null);

        var location = ResolveIndirectLocation(iconLocation.Trim());
        if (!TrySplitIconLocation(location, out var filePath, out var iconIndex))
            return Task.FromResult<string?>(null);

        filePath = Environment.ExpandEnvironmentVariables(filePath);
        if (!Path.IsPathRooted(filePath) || !File.Exists(filePath))
            return Task.FromResult<string?>(null);

        var extension = Path.GetExtension(filePath);
        if (extension.Equals(".ico", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult<string?>(filePath);
        }

        return CacheAlphaHIconPathAsync($"resource:{filePath.ToUpperInvariant()}:{iconIndex}", () =>
        {
            var hr = SHDefExtractIcon(filePath, iconIndex, 0, out var largeIcon, out var smallIcon, 32);
            var icon = hr >= 0 && largeIcon != IntPtr.Zero ? largeIcon : smallIcon;
            if (icon != largeIcon)
                DestroyIcon(largeIcon);
            if (icon != smallIcon)
                DestroyIcon(smallIcon);
            return icon;
        });
    }

    private static ImageIcon? CreateImageIcon(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            return new ImageIcon { Source = new BitmapImage(new Uri(path, UriKind.Absolute)) };
        }
        catch (Exception exception) when (exception is ArgumentException or UriFormatException or IOException)
        {
            return null;
        }
    }

    private static Task<string?> CacheAlphaHIconPathAsync(string cacheKey, Func<IntPtr> extractIcon)
    {
        var versionedKey = $"hicon-alpha-v2:{cacheKey}";
        return AlphaIconPathCache.GetOrAdd(versionedKey, _ => CacheAlphaPngIconAsync(versionedKey, async () =>
        {
            var iconPixels = await Task.Run<(int Width, int Height, byte[]? Pixels)>(() =>
            {
                using var apartment = NativeCom.InitializeApartment(0, allowDifferentApartment: true);
                var icon = extractIcon();
                if (icon == IntPtr.Zero)
                    return (Width: 0, Height: 0, Pixels: (byte[]?)null);

                try
                {
                    return TryGetIconPixels(icon, out var width, out var height, out var pixels)
                        ? (width, height, (byte[]?)pixels)
                        : (0, 0, (byte[]?)null);
                }
                finally
                {
                    DestroyIcon(icon);
                }
            }).ConfigureAwait(false);

            return iconPixels.Pixels is null
                ? null
                : await EncodeBgraPngAsync(
                    iconPixels.Pixels,
                    iconPixels.Width,
                    iconPixels.Height,
                    BitmapAlphaMode.Straight).ConfigureAwait(false);
        }));
    }

    private static async Task<string?> CacheAlphaPngIconAsync(string cacheKey, Func<Task<byte[]?>> extractPng)
    {
        try
        {
            var cacheDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "XiyiDesktop",
                "IconCache");
            Directory.CreateDirectory(cacheDirectory);
            var fileName = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cacheKey))) + ".png";
            var imagePath = Path.Combine(cacheDirectory, fileName);
            if (IsCompletePng(imagePath))
                return imagePath;
            if (File.Exists(imagePath))
                File.Delete(imagePath);

            var pngBytes = await extractPng().ConfigureAwait(false);
            if (pngBytes is null || pngBytes.Length == 0)
                return null;

            var temporaryPath = Path.Combine(cacheDirectory, $".{fileName}.{Guid.NewGuid():N}.tmp");
            try
            {
                await File.WriteAllBytesAsync(temporaryPath, pngBytes).ConfigureAwait(false);
                if (!IsCompletePng(temporaryPath))
                    return null;

                try
                {
                    File.Move(temporaryPath, imagePath);
                }
                catch (IOException) when (IsCompletePng(imagePath))
                {
                    // Another request already published the same icon.
                }

                return IsCompletePng(imagePath) ? imagePath : null;
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or ExternalException)
        {
            System.Diagnostics.Debug.WriteLine($"Unable to cache a transparent Shell icon for '{cacheKey}': {exception.Message}");
            return null;
        }
    }

    private static bool TryGetIconPixels(IntPtr icon, out int width, out int height, out byte[] pixels)
    {
        width = 0;
        height = 0;
        pixels = [];
        if (!GetIconInfo(icon, out var iconInfo))
            return false;

        try
        {
            if (iconInfo.ColorBitmap == IntPtr.Zero || iconInfo.MaskBitmap == IntPtr.Zero ||
                GetObject(iconInfo.ColorBitmap, Marshal.SizeOf<NativeBitmap>(), out var bitmap) == 0 ||
                bitmap.Width <= 0 || bitmap.Height <= 0 || bitmap.Width > 512 || bitmap.Height > 512)
                return false;

            width = bitmap.Width;
            height = bitmap.Height;
            pixels = new byte[checked(width * height * 4)];
            var bitmapInfo = new NativeBitmapInfo
            {
                Header = new NativeBitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<NativeBitmapInfoHeader>(),
                    Width = width,
                    Height = -height,
                    Planes = 1,
                    BitCount = 32,
                    Compression = 0,
                    SizeImage = (uint)pixels.Length
                }
            };
            var screenDc = GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero)
                return false;

            int copiedScanlines;
            try
            {
                copiedScanlines = GetDIBits(screenDc, iconInfo.ColorBitmap, 0, (uint)height, pixels, ref bitmapInfo, 0);
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screenDc);
            }

            if (copiedScanlines != height)
                return false;

            byte maxAlpha = 0;
            for (var index = 3; index < pixels.Length; index += 4)
            {
                maxAlpha = Math.Max(maxAlpha, pixels[index]);
            }

            // Some legacy Shell HICONs have an undefined low-valued alpha channel.
            // Treat it as absent and use the AND mask instead of making the icon nearly invisible.
            var hasAlpha = maxAlpha >= 128;
            ApplyIconMaskAlpha(iconInfo.MaskBitmap, width, height, pixels, hasAlpha);
            if (hasAlpha)
                UnpremultiplyAlpha(pixels);

            return true;
        }
        finally
        {
            if (iconInfo.ColorBitmap != IntPtr.Zero)
                DeleteObject(iconInfo.ColorBitmap);
            if (iconInfo.MaskBitmap != IntPtr.Zero)
                DeleteObject(iconInfo.MaskBitmap);
        }
    }

    private static void ApplyIconMaskAlpha(IntPtr maskBitmap, int width, int height, byte[] pixels, bool hasAlpha)
    {
        var deviceContext = CreateCompatibleDC(IntPtr.Zero);
        if (deviceContext == IntPtr.Zero)
            return;

        var oldBitmap = SelectObject(deviceContext, maskBitmap);
        try
        {
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var maskColor = GetPixel(deviceContext, x, y);
                    var offset = (y * width + x) * 4;
                    if (maskColor == 0x00FFFFFF)
                        pixels[offset + 3] = 0;
                    else if (!hasAlpha || pixels[offset + 3] == 0)
                        pixels[offset + 3] = 255;
                }
            }
        }
        finally
        {
            SelectObject(deviceContext, oldBitmap);
            DeleteDC(deviceContext);
        }
    }

    private static void UnpremultiplyAlpha(byte[] pixels)
    {
        for (var index = 0; index < pixels.Length; index += 4)
        {
            var alpha = pixels[index + 3];
            if (alpha == 0 || alpha == 255)
                continue;

            for (var channel = 0; channel < 3; channel++)
                pixels[index + channel] = (byte)Math.Min(255, (pixels[index + channel] * 255 + alpha / 2) / alpha);
        }
    }

    private static bool IsCompletePng(string path)
    {
        // A missing cache entry is the normal first-run case; do not probe it by opening
        // a FileStream, which raises a first-chance FileNotFoundException in the debugger.
        if (!File.Exists(path))
            return false;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length < 45)
                return false;

            Span<byte> header = stackalloc byte[8];
            stream.ReadExactly(header);
            if (!header.SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                return false;

            Span<byte> trailer = stackalloc byte[12];
            stream.Position = stream.Length - trailer.Length;
            stream.ReadExactly(trailer);
            return trailer.SequenceEqual(new byte[] { 0, 0, 0, 0, 73, 69, 78, 68, 174, 66, 96, 130 });
        }
        catch (FileNotFoundException)
        {
            // Another process may remove a stale cache entry between the existence check
            // and opening it. Treat that race the same as a cache miss.
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static string ResolveIndirectLocation(string location)
    {
        if (!location.StartsWith('@'))
            return location;

        var buffer = new StringBuilder(32768);
        return SHLoadIndirectString(location, buffer, (uint)buffer.Capacity, IntPtr.Zero) >= 0
            ? buffer.ToString()
            : location;
    }

    private static bool TrySplitIconLocation(string value, out string filePath, out int iconIndex)
    {
        value = value.Trim();
        filePath = value;
        iconIndex = 0;

        if (value.StartsWith('"'))
        {
            var closingQuote = value.IndexOf('"', 1);
            if (closingQuote < 0)
                return false;

            filePath = value[1..closingQuote];
            var suffix = value[(closingQuote + 1)..].Trim();
            if (suffix.Length == 0)
                return true;
            if (suffix[0] != ',' || !int.TryParse(suffix.AsSpan(1).Trim(), out iconIndex))
                return false;
            return true;
        }

        var comma = value.LastIndexOf(',');
        if (comma > 0 && int.TryParse(value.AsSpan(comma + 1).Trim(), out var parsedIndex))
        {
            filePath = value[..comma].Trim();
            iconIndex = parsedIndex;
        }

        return filePath.Length > 0;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeIconInfo
    {
        [MarshalAs(UnmanagedType.Bool)] public bool IsIcon;
        public uint HotspotX;
        public uint HotspotY;
        public IntPtr MaskBitmap;
        public IntPtr ColorBitmap;
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
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ColorsUsed;
        public uint ColorsImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBitmapInfo
    {
        public NativeBitmapInfoHeader Header;
        public uint Color;
    }

    [DllImport("shell32.dll", EntryPoint = "SHGetFileInfoW", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string path,
        uint fileAttributes,
        out ShellFileInfo fileInfo,
        uint fileInfoSize,
        uint flags);

    [DllImport("shell32.dll", EntryPoint = "SHGetFileInfoW", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        IntPtr itemIdList,
        uint fileAttributes,
        out ShellFileInfo fileInfo,
        uint fileInfoSize,
        uint flags);

    [DllImport("shell32.dll", EntryPoint = "SHGetImageList", ExactSpelling = true)]
    private static extern int SHGetImageList(int imageList, ref Guid interfaceId, out IntPtr imageListInterface);

    [DllImport("shell32.dll", EntryPoint = "SHParseDisplayName", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(
        string name,
        IntPtr bindingContext,
        out IntPtr itemIdList,
        uint attributesIn,
        out uint attributesOut);

    [DllImport("shell32.dll", EntryPoint = "SHDefExtractIconW", CharSet = CharSet.Unicode)]
    private static extern int SHDefExtractIcon(
        string iconFile,
        int iconIndex,
        uint flags,
        out IntPtr largeIcon,
        out IntPtr smallIcon,
        uint iconSize);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHLoadIndirectString(string source, StringBuilder output, uint outputChars, IntPtr reserved);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIconInfo(IntPtr icon, out NativeIconInfo iconInfo);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr windowHandle, IntPtr deviceContext);

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

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr deviceContext);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr deviceContext, IntPtr graphicsObject);

    [DllImport("gdi32.dll")]
    private static extern uint GetPixel(IntPtr deviceContext, int x, int y);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr deviceContext);

}
