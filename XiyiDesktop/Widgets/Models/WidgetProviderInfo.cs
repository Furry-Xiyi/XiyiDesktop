using System.Collections.Generic;

namespace XiyiDesktop.Widgets.Models;

internal sealed record WidgetProviderInfo(
    string AppName,
    string PackageName,
    string PackageFamilyName,
    string PackageFullName,
    string ExtensionId,
    string ApplicationId,
    string ExtensionDisplayName,
    string PublicFolderPath,
    IReadOnlyList<WidgetResourceInfo> ProviderIcons,
    IReadOnlyList<WidgetDefinition> Definitions,
    IReadOnlyList<string> ExtensionPropertyKeys);
