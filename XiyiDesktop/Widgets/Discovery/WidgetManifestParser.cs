using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using XiyiDesktop.Widgets.Models;

namespace XiyiDesktop.Widgets.Discovery;

/// <summary>Reads the documented widget-provider XML while tolerating namespace and layout differences.</summary>
internal static class WidgetManifestParser
{
    private const string WidgetExtensionName = "com.microsoft.windows.widgets";

    public static IReadOnlyList<ParsedWidgetExtension> Parse(string manifestXml, string? requestedExtensionId = null)
    {
        using var textReader = new StringReader(manifestXml);
        using var xmlReader = System.Xml.XmlReader.Create(textReader, new System.Xml.XmlReaderSettings
        {
            DtdProcessing = System.Xml.DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true
        });

        var document = XDocument.Load(xmlReader, LoadOptions.None);
        var results = new List<ParsedWidgetExtension>();

        // AppExtensionCatalog properties contain the WidgetProvider subtree rather
        // than the surrounding package manifest. Accept both forms.
        var root = document.Root;
        if (root is not null && IsElement(root, "WidgetProvider"))
            return new[] { ParseProvider(root, requestedExtensionId ?? string.Empty, string.Empty, string.Empty, "App") };

        if (root is not null && IsElement(root, "Properties"))
        {
            var propertyProviders = root.Elements().Where(element => IsElement(element, "WidgetProvider")).ToArray();
            if (propertyProviders.Length > 0)
                return propertyProviders.Select(provider => ParseProvider(provider, requestedExtensionId ?? string.Empty, string.Empty, string.Empty, "App")).ToArray();
        }

        foreach (var appExtension in document.Descendants().Where(element =>
                     IsElement(element, "AppExtension") &&
                     string.Equals(Attribute(element, "Name"), WidgetExtensionName, StringComparison.OrdinalIgnoreCase)))
        {
            var id = Attribute(appExtension, "Id") ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(requestedExtensionId) &&
                !string.Equals(requestedExtensionId, id, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var properties = appExtension.Elements().FirstOrDefault(element => IsElement(element, "Properties"));
            var providers = properties?.Elements().Where(element => IsElement(element, "WidgetProvider")).ToArray()
                ?? Array.Empty<XElement>();
            if (providers.Length == 0 && properties is not null)
                providers = properties.Descendants().Where(element => IsElement(element, "WidgetProvider")).ToArray();

            var application = appExtension.Ancestors().FirstOrDefault(element => IsElement(element, "Application"));
            var applicationId = Attribute(application, "Id") ?? "App";
            foreach (var provider in providers)
                results.Add(ParseProvider(provider, id, Attribute(appExtension, "DisplayName") ?? string.Empty,
                    Attribute(appExtension, "PublicFolder") ?? string.Empty, applicationId));
        }

        return results;
    }

    private static ParsedWidgetExtension ParseProvider(XElement provider, string id, string displayName, string publicFolder, string applicationId)
    {
        var icons = ParseResources(provider.Elements().FirstOrDefault(element => IsElement(element, "ProviderIcons")), "Provider");
        var definitions = ParseDefinitions(provider.Elements().FirstOrDefault(element => IsElement(element, "Definitions")));
        return new ParsedWidgetExtension(id, displayName, publicFolder, applicationId, icons, definitions, true);
    }

    private static IReadOnlyList<WidgetDefinition> ParseDefinitions(XElement? definitions)
    {
        if (definitions is null)
            return Array.Empty<WidgetDefinition>();

        return definitions.Elements()
            .Where(element => IsElement(element, "Definition"))
            .Select(definition =>
            {
                var activation = definition.Parent?.Parent?.Elements().FirstOrDefault(element => IsElement(element, "Activation"));
                var createInstance = activation?.Elements().FirstOrDefault(element => IsElement(element, "CreateInstance"));
                var activateApplication = activation?.Elements().FirstOrDefault(element => IsElement(element, "ActivateApplication"));
                var declaredSizes = definition.Descendants()
                    .Where(element => IsElement(element, "Size"))
                    .Select(element => Attribute(element, "Name"))
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var sizes = declaredSizes.Length == 0 ? new[] { "large" } : declaredSizes;
                var resources = ParseResources(definition.Elements().FirstOrDefault(element => IsElement(element, "ThemeResources")), "Default");

                return new WidgetDefinition(
                    Attribute(definition, "Id") ?? string.Empty,
                    Attribute(definition, "DisplayName") ?? string.Empty,
                    Attribute(definition, "Description") ?? string.Empty,
                    Attribute(createInstance, "ClassId"),
                    createInstance is not null ? "CreateInstance" : activateApplication is not null ? "ActivateApplication" : "未声明",
                    sizes,
                    resources,
                    Attribute(definition, "AdditionalInfoUri"),
                    ParseNullableBoolean(Attribute(definition, "AllowMultiple")),
                    ParseNullableBoolean(Attribute(definition, "IsCustomizable")),
                    Attribute(definition, "Id"));
            })
            .ToArray();
    }

    private static IReadOnlyList<WidgetResourceInfo> ParseResources(XElement? container, string defaultTheme)
    {
        if (container is null)
            return Array.Empty<WidgetResourceInfo>();

        return container.DescendantsAndSelf()
            .Where(element => IsElement(element, "Icon") || IsElement(element, "Screenshot"))
            .Select(element =>
            {
                var theme = element.Ancestors().FirstOrDefault(ancestor =>
                    IsElement(ancestor, "DarkMode") || IsElement(ancestor, "LightMode"))?.Name.LocalName ?? defaultTheme;
                return new WidgetResourceInfo(
                    element.Name.LocalName,
                    theme,
                    Attribute(element, "Path") ?? string.Empty,
                    null,
                    Attribute(element, "DisplayAltText"));
            })
            .Where(resource => !string.IsNullOrWhiteSpace(resource.PackageRelativePath))
            .DistinctBy(resource => $"{resource.Theme}|{resource.Kind}|{resource.PackageRelativePath}", StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? Attribute(XElement? element, string name) =>
        element?.Attributes().FirstOrDefault(attribute => string.Equals(attribute.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))?.Value.Trim();

    private static bool IsElement(XElement element, string localName) =>
        string.Equals(element.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase);

    private static bool? ParseNullableBoolean(string? value) => bool.TryParse(value, out var parsed) ? parsed : null;
}

internal sealed record ParsedWidgetExtension(
    string Id,
    string DisplayName,
    string PublicFolder,
    string ApplicationId,
    IReadOnlyList<WidgetResourceInfo> ProviderIconPaths,
    IReadOnlyList<WidgetDefinition> Definitions,
    bool HasWidgetProvider);
