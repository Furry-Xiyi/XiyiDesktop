using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace XiyiDesktop.DesktopItems;

/// <summary>
/// Discovers packaged File Explorer context-menu registrations that apply to a folder
/// background. It reads package manifests and the current user's placement preferences;
/// it does not activate or invoke third-party COM code.
/// </summary>
internal sealed class ContextMenuExtensionCatalog
{
    private const string PreferencesPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\ContextMenu\Preferences";
    private const string ExtensionPreferencesPath = PreferencesPath + @"\extensions";
    private const string ExplorerContextMenusCategory = "windows.fileExplorerContextMenus";
    private const string ComServerCategory = "windows.comServer";
    private static readonly object PackageManifestCacheLock = new();
    private static IReadOnlyList<InstalledPackageManifest>? _packageManifestCache;
    private static DateTimeOffset _packageManifestCacheTimeUtc = DateTimeOffset.MinValue;
    private static readonly TimeSpan PackageManifestCacheLifetime = TimeSpan.FromMinutes(5);
    private static readonly XmlReaderSettings ManifestReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true
    };

    /// <summary>
    /// Finds installed package registrations applicable to a desktop background.
    /// An unreadable package or preference is skipped or reported as unknown; it does
    /// not prevent other packages from being returned.
    /// </summary>
    public ContextMenuExtensionCatalogSnapshot DiscoverDesktopBackgroundExtensions()
        => DiscoverExtensions(ReadBackgroundCommands);

    /// <summary>Finds packaged commands registered for a selected file or directory.</summary>
    public ContextMenuExtensionCatalogSnapshot DiscoverItemExtensions(string itemPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemPath);
        var isDirectory = Directory.Exists(itemPath);
        var extension = isDirectory ? null : Path.GetExtension(itemPath);
        return DiscoverExtensions((manifest, packageFullName) =>
            ReadItemCommands(manifest, packageFullName, isDirectory, extension));
    }

    private static ContextMenuExtensionCatalogSnapshot DiscoverExtensions(
        Func<XDocument, string, IReadOnlyList<DesktopContextMenuCommandRegistration>> readCommands)
    {
        var extensions = new List<DesktopContextMenuExtension>();
        foreach (var package in GetInstalledPackageManifests())
        {
            try
            {
                var commands = readCommands(package.Manifest, package.PackageFullName);
                if (commands.Count == 0)
                    continue;

                extensions.Add(new DesktopContextMenuExtension(
                    package.PackageFamilyName,
                    package.PackageName,
                    package.DisplayName,
                    ReadPlacement(package.PackageFamilyName),
                    commands));
            }
            catch (Exception exception) when (IsPackageReadException(exception))
            {
                Debug.WriteLine($"Unable to inspect package context-menu manifest for '{package.PackageName}': {exception.Message}");
            }
        }

        return new ContextMenuExtensionCatalogSnapshot(
            ReadShowExtensionsPreference(),
            extensions
                .OrderBy(extension => extension.Placement)
                .ThenBy(extension => extension.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToArray());
    }

    private static IReadOnlyList<InstalledPackageManifest> GetInstalledPackageManifests()
    {
        lock (PackageManifestCacheLock)
        {
            if (_packageManifestCache is not null && DateTimeOffset.UtcNow - _packageManifestCacheTimeUtc < PackageManifestCacheLifetime)
                return _packageManifestCache;

            var manifests = new List<InstalledPackageManifest>();
            var seenPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var package in GetInstalledPackages())
            {
                try
                {
                    var packageId = package.Id;
                    var packageFamilyName = packageId.FamilyName;
                    var packageFullName = packageId.FullName;
                    if (string.IsNullOrWhiteSpace(packageFamilyName) ||
                        string.IsNullOrWhiteSpace(packageFullName) ||
                        !seenPackages.Add(packageFullName))
                    {
                        continue;
                    }

                    var installPath = TryGetInstalledLocationPath(packageFullName);
                    if (string.IsNullOrWhiteSpace(installPath))
                        continue;

                    var manifestPath = Path.Combine(installPath, "AppxManifest.xml");
                    if (!File.Exists(manifestPath))
                        continue;

                    var manifest = ReadManifest(manifestPath);
                    if (manifest is null)
                        continue;

                    var packageName = packageId.Name;
                    manifests.Add(new InstalledPackageManifest(
                        packageFullName,
                        packageFamilyName,
                        packageName,
                        ResolvePackageDisplayName(package, package.DisplayName, packageName),
                        manifest));
                }
                catch (Exception exception) when (IsPackageReadException(exception))
                {
                    Debug.WriteLine($"Unable to inspect an installed package context-menu manifest: {exception.Message}");
                }
            }

            _packageManifestCache = manifests.ToArray();
            _packageManifestCacheTimeUtc = DateTimeOffset.UtcNow;
            return _packageManifestCache;
        }
    }

    private static string ResolvePackageDisplayName(Package package, string? displayName, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(displayName) && !displayName.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
            return displayName.Trim();

        try
        {
            // Package.DisplayName can expose the manifest token rather than the
            // localized application label. AppListEntry.DisplayInfo is resolved by
            // Windows' package resource manager for the current user and language.
            var entries = Task.Run(async () => await package.GetAppListEntriesAsync()).GetAwaiter().GetResult();
            var localizedName = entries
                .Select(entry => entry.DisplayInfo?.DisplayName)
                .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name) && !name.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(localizedName))
                return localizedName.Trim();
        }
        catch (Exception exception) when (IsPackageReadException(exception))
        {
            Debug.WriteLine($"Unable to resolve localized app display name for package '{fallback}': {exception.Message}");
        }

        return NormalizePackageDisplayName(displayName, fallback);
    }

    private static string? TryGetInstalledLocationPath(string packageFullName)
    {
        // Package.InstalledLocation throws a managed FileNotFoundException for stale
        // package registrations. Query the package path API directly so an absent
        // package is reported as an error code and can be skipped without an exception.
        uint pathLength = 0;
        var result = GetPackagePathByFullName(packageFullName, ref pathLength, null);
        if (result != ErrorInsufficientBuffer || pathLength == 0 || pathLength > int.MaxValue)
            return null;

        var path = new StringBuilder((int)pathLength);
        result = GetPackagePathByFullName(packageFullName, ref pathLength, path);
        return result == ErrorSuccess && path.Length > 0 ? path.ToString() : null;
    }

    private static IReadOnlyList<Package> GetInstalledPackages()
    {
        var packages = new List<Package>();
        try
        {
            var packageManager = new PackageManager();
            // The desktop shell only needs packages registered to this user.
            // FindPackages() also tries machine-wide registrations and commonly
            // throws UnauthorizedAccessException for an unpackaged desktop process.
            try
            {
                packages.AddRange(packageManager.FindPackagesForUser(string.Empty));
            }
            catch (Exception exception) when (IsPackageReadException(exception))
            {
                Debug.WriteLine($"Unable to enumerate current-user packages: {exception.Message}");
            }
        }
        catch (Exception exception) when (IsPackageReadException(exception))
        {
            Debug.WriteLine($"Unable to create the Windows package manager: {exception.Message}");
        }

        return packages;
    }

    private static XDocument? ReadManifest(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = XmlReader.Create(stream, ManifestReaderSettings);
            return XDocument.Load(reader, LoadOptions.None);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException or XmlException or ArgumentException)
        {
            Debug.WriteLine($"Unable to read package manifest '{path}': {exception.Message}");
            return null;
        }
    }

    private static IReadOnlyList<DesktopContextMenuCommandRegistration> ReadBackgroundCommands(
        XDocument manifest,
        string packageFullName)
    {
        var registeredComClasses = ReadRegisteredComClasses(manifest);
        var commands = new Dictionary<CommandKey, HashSet<string>>();

        foreach (var extension in manifest.Descendants().Where(element =>
                     element.Name.LocalName == "Extension" &&
                     string.Equals((string?)element.Attribute("Category"), ExplorerContextMenusCategory, StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var itemType in extension.Descendants().Where(element => element.Name.LocalName == "ItemType"))
            {
                var itemTypeName = ((string?)itemType.Attribute("Type"))?.Trim();
                if (!AppliesToDesktopBackground(itemTypeName))
                    continue;

                foreach (var verb in itemType.Elements().Where(element => element.Name.LocalName == "Verb"))
                {
                    var verbId = ((string?)verb.Attribute("Id"))?.Trim();
                    var classIdText = ((string?)verb.Attribute("Clsid"))?.Trim();
                    if (string.IsNullOrWhiteSpace(verbId) ||
                        !Guid.TryParse(classIdText, out var classId) ||
                        classId == Guid.Empty)
                    {
                        continue;
                    }

                    var key = new CommandKey(verbId, classId, registeredComClasses.Contains(classId));
                    if (!commands.TryGetValue(key, out var applicableTypes))
                    {
                        applicableTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        commands.Add(key, applicableTypes);
                    }

                    applicableTypes.Add(itemTypeName!);
                }
            }
        }

        return commands
            .Select(pair => new DesktopContextMenuCommandRegistration(
                pair.Key.VerbId,
                pair.Key.ClassId,
                pair.Key.HasComServerRegistration,
                packageFullName,
                pair.Value.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray()))
            .OrderBy(command => command.VerbId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<DesktopContextMenuCommandRegistration> ReadItemCommands(
        XDocument manifest,
        string packageFullName,
        bool isDirectory,
        string? extension)
    {
        var registeredComClasses = ReadRegisteredComClasses(manifest);
        var commands = new Dictionary<CommandKey, HashSet<string>>();
        var extensionType = string.IsNullOrWhiteSpace(extension) ? null : extension;

        foreach (var manifestExtension in manifest.Descendants().Where(element =>
                     element.Name.LocalName == "Extension" &&
                     string.Equals((string?)element.Attribute("Category"), ExplorerContextMenusCategory, StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var itemType in manifestExtension.Descendants().Where(element => element.Name.LocalName == "ItemType"))
            {
                var itemTypeName = ((string?)itemType.Attribute("Type"))?.Trim();
                var applies = (!isDirectory && string.Equals(itemTypeName, "*", StringComparison.OrdinalIgnoreCase)) ||
                    (isDirectory && string.Equals(itemTypeName, "Directory", StringComparison.OrdinalIgnoreCase)) ||
                    (!isDirectory && extensionType is not null && string.Equals(itemTypeName, extensionType, StringComparison.OrdinalIgnoreCase));
                if (!applies)
                    continue;

                foreach (var verb in itemType.Elements().Where(element => element.Name.LocalName == "Verb"))
                {
                    var verbId = ((string?)verb.Attribute("Id"))?.Trim();
                    var classIdText = ((string?)verb.Attribute("Clsid"))?.Trim();
                    if (string.IsNullOrWhiteSpace(verbId) || !Guid.TryParse(classIdText, out var classId) || classId == Guid.Empty)
                        continue;

                    var key = new CommandKey(verbId, classId, registeredComClasses.Contains(classId));
                    if (!commands.TryGetValue(key, out var applicableTypes))
                    {
                        applicableTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        commands.Add(key, applicableTypes);
                    }
                    applicableTypes.Add(itemTypeName!);
                }
            }
        }

        return commands.Select(pair => new DesktopContextMenuCommandRegistration(
                pair.Key.VerbId,
                pair.Key.ClassId,
                pair.Key.HasComServerRegistration,
                packageFullName,
                pair.Value.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray()))
            .OrderBy(command => command.VerbId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static HashSet<Guid> ReadRegisteredComClasses(XDocument manifest)
    {
        var classes = new HashSet<Guid>();
        foreach (var extension in manifest.Descendants().Where(element =>
                     element.Name.LocalName == "Extension" &&
                     string.Equals((string?)element.Attribute("Category"), ComServerCategory, StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var classElement in extension.Descendants().Where(element => element.Name.LocalName == "Class"))
            {
                if (Guid.TryParse((string?)classElement.Attribute("Id"), out var classId) && classId != Guid.Empty)
                    classes.Add(classId);
            }
        }

        return classes;
    }

    private static bool AppliesToDesktopBackground(string? itemType) =>
        string.Equals(itemType, @"Directory\Background", StringComparison.OrdinalIgnoreCase);

    private static string NormalizePackageDisplayName(string? displayName, string fallback) =>
        string.IsNullOrWhiteSpace(displayName) || displayName.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)
            ? fallback
            : displayName.Trim();

    private static bool? ReadShowExtensionsPreference()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PreferencesPath + @"\ShowExtensions", writable: false);
            return ReadEnabledDword(key);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or SecurityException or ArgumentException or IOException)
        {
            Debug.WriteLine($"Unable to read Explorer's app-extension submenu preference: {exception.Message}");
            return null;
        }
    }

    internal static DesktopContextMenuExtensionPlacement ReadPlacement(string packageFamilyName)
    {
        if (string.IsNullOrWhiteSpace(packageFamilyName) ||
            packageFamilyName.Contains('\\') ||
            packageFamilyName.Contains('/'))
        {
            return DesktopContextMenuExtensionPlacement.Unknown;
        }

        try
        {
            using var extensionsKey = Registry.CurrentUser.OpenSubKey(ExtensionPreferencesPath, writable: false);
            if (extensionsKey is null)
                return DesktopContextMenuExtensionPlacement.Unknown;

            var matchingName = extensionsKey.GetSubKeyNames().FirstOrDefault(name =>
                string.Equals(name, packageFamilyName, StringComparison.OrdinalIgnoreCase));
            if (matchingName is null)
                return DesktopContextMenuExtensionPlacement.Unknown;

            using var packagePreferenceKey = extensionsKey.OpenSubKey(matchingName, writable: false);
            var enabled = ReadEnabledDword(packagePreferenceKey);
            return enabled switch
            {
                true => DesktopContextMenuExtensionPlacement.MainMenu,
                false => DesktopContextMenuExtensionPlacement.AppExtensionsSubmenu,
                _ => DesktopContextMenuExtensionPlacement.Unknown
            };
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or SecurityException or ArgumentException or IOException)
        {
            Debug.WriteLine($"Unable to read placement preference for package '{packageFamilyName}': {exception.Message}");
            return DesktopContextMenuExtensionPlacement.Unknown;
        }
    }

    private static bool? ReadEnabledDword(RegistryKey? key)
    {
        try
        {
            return key?.GetValue("Enabled", null, RegistryValueOptions.DoNotExpandEnvironmentNames) switch
            {
                int value when value == 1 => true,
                int value when value == 0 => false,
                _ => null
            };
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or SecurityException or ArgumentException or IOException)
        {
            return null;
        }
    }

    private static bool IsPackageReadException(Exception exception) =>
        exception is UnauthorizedAccessException or SecurityException or ArgumentException or IOException or
            System.Runtime.InteropServices.COMException or InvalidOperationException or NotSupportedException;

    private readonly record struct CommandKey(string VerbId, Guid ClassId, bool HasComServerRegistration);

    private const int ErrorSuccess = 0;
    private const int ErrorInsufficientBuffer = 122;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetPackagePathByFullName", ExactSpelling = true)]
    private static extern int GetPackagePathByFullName(
        string packageFullName,
        ref uint pathLength,
        StringBuilder? path);
}

internal sealed record ContextMenuExtensionCatalogSnapshot(
    bool? ShowAppExtensionsSubmenu,
    IReadOnlyList<DesktopContextMenuExtension> Extensions);

internal sealed record InstalledPackageManifest(
    string PackageFullName,
    string PackageFamilyName,
    string PackageName,
    string DisplayName,
    XDocument Manifest);

internal sealed record DesktopContextMenuExtension(
    string PackageFamilyName,
    string PackageName,
    string DisplayName,
    DesktopContextMenuExtensionPlacement Placement,
    IReadOnlyList<DesktopContextMenuCommandRegistration> Commands);

internal sealed record DesktopContextMenuCommandRegistration(
    string VerbId,
    Guid ClassId,
    bool HasComServerRegistration,
    string PackageFullName,
    IReadOnlyList<string> ItemTypes);

internal enum DesktopContextMenuExtensionPlacement
{
    MainMenu,
    AppExtensionsSubmenu,
    Unknown
}
