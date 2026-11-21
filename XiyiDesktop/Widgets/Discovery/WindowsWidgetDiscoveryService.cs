using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Windows.ApplicationModel.AppExtensions;
using Windows.Foundation.Collections;
using XiyiDesktop.Widgets.Diagnostics;
using XiyiDesktop.Widgets.Models;

namespace XiyiDesktop.Widgets.Discovery;

/// <summary>Discovers installed Windows Widgets providers using the public AppExtension catalog.</summary>
internal sealed class WindowsWidgetDiscoveryService : IDisposable
{
    public const string ExtensionName = "com.microsoft.windows.widgets";

    private AppExtensionCatalog? _catalog;
    private readonly WidgetExperimentLogger _logger;
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private bool _disposed;

    public event EventHandler? CatalogChanged;

    public WindowsWidgetDiscoveryService(WidgetExperimentLogger logger)
    {
        _logger = logger;
    }

    public async Task<WidgetDiscoverySnapshot> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        await _scanGate.WaitAsync(cancellationToken);
        try
        {
            return await DiscoverCoreAsync(cancellationToken);
        }
        finally
        {
            _scanGate.Release();
        }
    }

    private async Task<WidgetDiscoverySnapshot> DiscoverCoreAsync(CancellationToken cancellationToken)
    {
        var providers = new List<WidgetProviderInfo>();
        var diagnostics = new List<WidgetDiscoveryDiagnostic>();
        var scannedAt = DateTimeOffset.Now;
        try
        {
            var catalog = GetCatalog();
            var extensions = await catalog.FindAllAsync();
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var extension in extensions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var packageFamilyName = TryGetPackageFamilyName(extension);
                try
                {
                    var package = extension.Package;
                    var packageId = package.Id;
                    var packageRoot = TryGetPackageRoot(package);
                    var publicFolderPath = await TryGetPublicFolderPathAsync(extension, diagnostics, packageFamilyName);
                    var extensionProperties = await TryGetPropertiesAsync(extension, diagnostics, packageFamilyName);

                    IReadOnlyList<ParsedWidgetExtension> parsed = Array.Empty<ParsedWidgetExtension>();
                    if (!string.IsNullOrWhiteSpace(packageRoot))
                    {
                        var manifestPath = Path.Combine(packageRoot, "AppxManifest.xml");
                        if (File.Exists(manifestPath))
                        {
                            try
                            {
                                var manifestXml = await File.ReadAllTextAsync(manifestPath, cancellationToken);
                                parsed = WidgetManifestParser.Parse(manifestXml, extension.Id);
                                _logger.Write("Parse package widget manifest", parsed.Count > 0 ? "Parsed" : "No matching AppExtension",
                                    $"PFN={packageFamilyName}; Package={packageId.FullName}");
                            }
                            catch (Exception exception) when (exception is not OperationCanceledException)
                            {
                                AddDiagnostic(diagnostics, "Read package manifest", exception, packageFamilyName);
                            }
                        }
                    }

                    if (parsed.Count == 0 && extensionProperties is not null)
                    {
                        var sourceXml = PropertySetToXml(extensionProperties);
                        try
                        {
                            parsed = WidgetManifestParser.Parse(sourceXml, extension.Id);
                        }
                        catch (Exception exception) when (IsReadException(exception))
                        {
                            AddDiagnostic(diagnostics, "Parse extension properties", exception, packageFamilyName);
                        }
                    }

                    if (parsed.Count == 0)
                    {
                        const string message = "The extension was enumerated, but no WidgetProvider definition could be parsed from its properties or package manifest.";
                        diagnostics.Add(new WidgetDiscoveryDiagnostic(
                            DateTimeOffset.Now,
                            "Parse widget provider",
                            message,
                            null,
                            packageFamilyName));
                        _logger.Write("Parse widget provider", "No definition parsed", $"PFN={packageFamilyName}; {message}");
                        continue;
                    }

                    var appName = TryGetLocalizedAppName(extension) ?? NormalizeDisplayName(package.DisplayName, packageId.Name);
                    foreach (var item in parsed)
                    {
                        var resolvedProviderIcons = ResolveResources(item.ProviderIconPaths, packageRoot, publicFolderPath);
                        providers.Add(new WidgetProviderInfo(
                            appName,
                            packageId.Name,
                            packageId.FamilyName,
                            packageId.FullName,
                            item.Id,
                            item.ApplicationId,
                            NormalizeDisplayName(item.DisplayName, extension.DisplayName),
                            publicFolderPath ?? string.Empty,
                            resolvedProviderIcons,
                            item.Definitions.Select(definition => definition with
                            {
                                DefinitionId = BuildWidgetDefinitionId(
                                    packageId.FamilyName,
                                    item.ApplicationId,
                                    item.Id,
                                    definition.LocalDefinitionId ?? definition.DefinitionId),
                                DisplayName = NormalizeDisplayName(
                                    definition.DisplayName,
                                    definition.LocalDefinitionId ?? definition.DefinitionId),
                                Description = NormalizeDisplayName(definition.Description, GetUnresolvedResourceName(definition.Description)),
                                Resources = ResolveResources(definition.Resources, packageRoot, publicFolderPath)
                            }).ToArray(),
                            extensionProperties?.Keys.ToArray() ?? Array.Empty<string>()));
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    AddDiagnostic(diagnostics, "Inspect widget extension", exception, packageFamilyName);
                }
            }

            _logger.Write("AppExtensionCatalog.FindAllAsync", "Success",
                $"ExtensionName={ExtensionName}; Extensions={extensions.Count}; Definitions={providers.Sum(provider => provider.Definitions.Count)}");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AddDiagnostic(diagnostics, "Enumerate widget extensions", exception, null);
        }

        return new WidgetDiscoverySnapshot(
            scannedAt,
            ExtensionName,
            providers.OrderBy(provider => provider.AppName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(provider => provider.Definitions.FirstOrDefault()?.DisplayName ?? provider.ExtensionId, StringComparer.CurrentCultureIgnoreCase)
                .ToArray(),
            diagnostics,
            Environment.OSVersion.Version.ToString());
    }

    private AppExtensionCatalog GetCatalog()
    {
        if (_catalog is not null)
            return _catalog;

        try
        {
            _catalog = AppExtensionCatalog.Open(ExtensionName);
            _catalog.PackageInstalled += Catalog_PackageChanged;
            _catalog.PackageUpdating += Catalog_PackageChanged;
            _catalog.PackageUpdated += Catalog_PackageChanged;
            _catalog.PackageUninstalling += Catalog_PackageChanged;
            _catalog.PackageStatusChanged += Catalog_PackageChanged;
            _logger.Write("AppExtensionCatalog.Open", "Success", $"ExtensionName={ExtensionName}");
            return _catalog;
        }
        catch (Exception exception)
        {
            _logger.Write("AppExtensionCatalog.Open", "Failed", $"ExtensionName={ExtensionName}", exception);
            throw;
        }
    }

    private void Catalog_PackageChanged(object sender, object args)
    {
        _logger.Write("AppExtensionCatalog package event", args.GetType().Name, ExtensionName);
        CatalogChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task<IPropertySet?> TryGetPropertiesAsync(
        AppExtension extension,
        List<WidgetDiscoveryDiagnostic> diagnostics,
        string? packageFamilyName)
    {
        try
        {
            var properties = await extension.GetExtensionPropertiesAsync();
            _logger.Write("AppExtension.GetExtensionPropertiesAsync", "Success",
                $"PFN={packageFamilyName}; PropertyKeys={string.Join(",", properties.Keys)}");
            return properties;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AddDiagnostic(diagnostics, "GetExtensionPropertiesAsync", exception, packageFamilyName);
            return null;
        }
    }

    private async Task<string?> TryGetPublicFolderPathAsync(
        AppExtension extension,
        List<WidgetDiscoveryDiagnostic> diagnostics,
        string? packageFamilyName)
    {
        try
        {
            var folder = await extension.GetPublicFolderAsync();
            var path = folder?.Path;
            _logger.Write("AppExtension.GetPublicFolderAsync", string.IsNullOrWhiteSpace(path) ? "No folder path" : "Success",
                $"PFN={packageFamilyName}; Path={path ?? "unavailable"}");
            return path;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AddDiagnostic(diagnostics, "GetPublicFolderAsync", exception, packageFamilyName);
            return null;
        }
    }

    private static string PropertySetToXml(IPropertySet properties)
    {
        var root = new XElement("Properties");
        foreach (var key in properties.Keys)
        {
            try
            {
                root.Add(ValueToElement(key, properties[key]));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                root.Add(new XElement("Property", new XAttribute("Name", key), new XAttribute("ReadError", exception.HResult)));
            }
        }

        return root.ToString(SaveOptions.DisableFormatting);
    }

    private static XElement ValueToElement(string name, object? value)
    {
        var element = new XElement(XmlSafeName(name));
        if (value is null)
            return element;

        if (value is IPropertySet propertySet)
        {
            foreach (var key in propertySet.Keys)
            {
                try { element.Add(ValueToElement(key, propertySet[key])); }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    element.Add(new XElement("Property", new XAttribute("Name", key), new XAttribute("ReadError", exception.HResult)));
                }
            }
        }
        else if (value is string text)
        {
            element.Value = text;
        }
        else if (value is Uri uri)
        {
            element.Value = uri.ToString();
        }
        else
        {
            element.Value = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        }

        return element;
    }

    private static string XmlSafeName(string name) =>
        System.Xml.XmlConvert.EncodeLocalName(string.IsNullOrWhiteSpace(name) ? "Property" : name);

    private static IReadOnlyList<WidgetResourceInfo> ResolveResources(IReadOnlyList<WidgetResourceInfo> resources, string? packageRoot, string? publicFolder) =>
        resources.Select(resource => resource with
            { ResolvedPath = WidgetResourceResolver.Resolve(packageRoot, publicFolder, resource.PackageRelativePath) })
            .ToArray();

    private static string? TryGetPackageRoot(Windows.ApplicationModel.Package package)
    {
        try { return package.InstalledLocation?.Path; }
        catch (Exception exception) when (IsReadException(exception)) { return null; }
    }

    private static string? TryGetPackageFamilyName(AppExtension extension)
    {
        try { return extension.Package.Id.FamilyName; }
        catch (Exception exception) when (IsReadException(exception)) { return null; }
    }

    private static string? TryGetLocalizedAppName(AppExtension extension)
    {
        try
        {
            var displayName = extension.AppInfo?.DisplayInfo?.DisplayName;
            return string.IsNullOrWhiteSpace(displayName) ? null : NormalizeDisplayName(displayName, string.Empty);
        }
        catch (Exception exception) when (IsReadException(exception)) { return null; }
    }

    private static string NormalizeDisplayName(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) || value.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)
            ? fallback
            : value.Trim();

    private static string BuildWidgetDefinitionId(string packageFamilyName, string applicationId, string extensionId, string localDefinitionId) =>
        $"{packageFamilyName}!{applicationId}!!{extensionId}!!{localDefinitionId}";

    private static string GetUnresolvedResourceName(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "" : value.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)
            ? $"Resource not resolved ({value})"
            : value.Trim();

    private void AddDiagnostic(
        List<WidgetDiscoveryDiagnostic> diagnostics,
        string operation,
        Exception exception,
        string? packageFamilyName)
    {
        var hresult = exception.HResult;
        diagnostics.Add(new WidgetDiscoveryDiagnostic(
            DateTimeOffset.Now,
            operation,
            $"{exception.GetType().Name}: {exception.Message} (0x{hresult:X8})",
            hresult,
            packageFamilyName));
        _logger.Write(operation, "Failed",
            $"PFN={packageFamilyName ?? "unknown"}; HRESULT=0x{hresult:X8}; OS={Environment.OSVersion.Version}", exception);
    }

    private static bool IsReadException(Exception exception) =>
        exception is UnauthorizedAccessException or IOException or ArgumentException or InvalidOperationException or
            NotSupportedException or System.Security.SecurityException or COMException or System.Xml.XmlException;

    public void Dispose()
    {
        if (_disposed)
            return;

        if (_catalog is not null)
        {
            _catalog.PackageInstalled -= Catalog_PackageChanged;
            _catalog.PackageUpdating -= Catalog_PackageChanged;
            _catalog.PackageUpdated -= Catalog_PackageChanged;
            _catalog.PackageUninstalling -= Catalog_PackageChanged;
            _catalog.PackageStatusChanged -= Catalog_PackageChanged;
        }
        _scanGate.Dispose();
        _disposed = true;
    }
}
