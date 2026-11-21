using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Media.Imaging;
using XiyiDesktop.ApplicationActivation;

namespace XiyiDesktop.DesktopItems;

public enum DesktopItemKind
{
    File,
    Folder,
    ThisPc,
    RecycleBin,
    UserFiles,
    Network,
    ControlPanel
}

public enum DesktopItemSource
{
    UserDesktop,
    PublicDesktop,
    ShellNamespace
}

/// <summary>
/// A desktop entry backed by a filesystem item or a well-known Shell namespace item.
/// For shell-only entries, use <see cref="DisplayNameResourceKey"/> to obtain a localized
/// name from the desktop application's resource loader.
/// </summary>
public sealed class DesktopItem : INotifyPropertyChanged
{
    private BitmapImage? _icon;
    private BitmapImage? _shellStatusOverlay;

    internal DesktopItem(
        string identity,
        string? path,
        string name,
        DesktopItemKind kind,
        DesktopItemSource source,
        bool isDirectory,
        string? displayNameResourceKey = null,
        long? sizeBytes = null,
        DateTimeOffset? createdTimeUtc = null,
        DateTimeOffset? modifiedTimeUtc = null,
        string? fileExtension = null,
        FileAttributes? attributes = null)
    {
        Identity = identity;
        Path = path;
        Name = name;
        Kind = kind;
        Source = source;
        IsDirectory = isDirectory;
        DisplayNameResourceKey = displayNameResourceKey;
        SizeBytes = sizeBytes;
        CreatedTimeUtc = createdTimeUtc;
        ModifiedTimeUtc = modifiedTimeUtc;
        FileExtension = fileExtension;
        Attributes = attributes;
    }

    /// <summary>A stable key suitable for a UI list's key or selection restoration.</summary>
    public string Identity { get; }

    /// <summary>Filesystem path, or null for a Shell namespace item.</summary>
    public string? Path { get; }

    /// <summary>Filesystem name. Special Shell entries should use their resource key.</summary>
    public string Name { get; }

    public string? DisplayNameResourceKey { get; }

    public DesktopItemKind Kind { get; }

    public DesktopItemSource Source { get; }

    public bool IsDirectory { get; }

    public bool IsSpecial => Source == DesktopItemSource.ShellNamespace;

    public bool CanOpen => IsSpecial || Path is not null;

    public long? SizeBytes { get; }

    public DateTimeOffset? CreatedTimeUtc { get; }

    public DateTimeOffset? ModifiedTimeUtc { get; }

    public string? FileExtension { get; }

    public bool IsShortcut =>
        string.Equals(FileExtension, ".lnk", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(FileExtension, ".url", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(FileExtension, ".appref-ms", StringComparison.OrdinalIgnoreCase);

    public FileAttributes? Attributes { get; }

    public bool IsHidden => Attributes is { } attributes &&
        (attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0;

    public BitmapImage? Icon
    {
        get => _icon;
        internal set
        {
            if (ReferenceEquals(_icon, value))
            {
                return;
            }

            _icon = value;
            OnPropertyChanged();
        }
    }

    /// <summary>The Shell status overlay for a file, such as a cloud sync state.</summary>
    public BitmapImage? ShellStatusOverlay
    {
        get => _shellStatusOverlay;
        internal set
        {
            if (ReferenceEquals(_shellStatusOverlay, value))
                return;

            _shellStatusOverlay = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal static DesktopItem CreateShellItem(DesktopItemKind kind, string resourceKey)
    {
        var parsingName = GetShellParsingName(kind);
        var identity = kind switch
        {
            DesktopItemKind.ThisPc => "shell:{20D04FE0-3AEA-1069-A2D8-08002B30309D}",
            DesktopItemKind.RecycleBin => "shell:{645FF040-5081-101B-9F08-00AA002F954E}",
            DesktopItemKind.UserFiles => "shell:{59031A47-3F72-44A7-89C5-5595FE6B30EE}",
            DesktopItemKind.Network => "shell:{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}",
            DesktopItemKind.ControlPanel => "shell:{5399E694-6CE5-4D6C-8FCE-1D8870FDCBA0}",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a Shell namespace item.")
        };

        return new DesktopItem(
            identity: identity,
            path: null,
            name: ShellActivationService.GetShellDisplayName(parsingName) ?? string.Empty,
            kind: kind,
            source: DesktopItemSource.ShellNamespace,
            isDirectory: true,
            displayNameResourceKey: resourceKey);
    }

    internal static string GetShellParsingName(DesktopItemKind kind) => kind switch
    {
        DesktopItemKind.ThisPc => "shell:MyComputerFolder",
        DesktopItemKind.RecycleBin => "shell:RecycleBinFolder",
        DesktopItemKind.UserFiles => "::{59031A47-3F72-44A7-89C5-5595FE6B30EE}",
        DesktopItemKind.Network => "::{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}",
        DesktopItemKind.ControlPanel => "::{5399E694-6CE5-4D6C-8FCE-1D8870FDCBA0}",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a Shell namespace item.")
    };

    internal static DesktopItem CreateFileSystemItem(
        string path,
        string name,
        DesktopItemKind kind,
        DesktopItemSource source,
        bool isDirectory,
        long? sizeBytes,
        DateTimeOffset? createdTimeUtc,
        DateTimeOffset? modifiedTimeUtc,
        string? fileExtension,
        FileAttributes attributes)
    {
        return new DesktopItem(
            identity: path,
            path: path,
            name: name,
            kind: kind,
            source: source,
            isDirectory: isDirectory,
            sizeBytes: sizeBytes,
            createdTimeUtc: createdTimeUtc,
            modifiedTimeUtc: modifiedTimeUtc,
            fileExtension: fileExtension,
            attributes: attributes);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
