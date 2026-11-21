using System.Collections.Generic;

namespace XiyiDesktop.Widgets.Models;

internal sealed record WidgetDefinition(
    string DefinitionId,
    string DisplayName,
    string Description,
    string? Clsid,
    string ActivationType,
    IReadOnlyList<string> SupportedSizes,
    IReadOnlyList<WidgetResourceInfo> Resources,
    string? AdditionalInfoUri,
    bool? AllowsMultiple,
    bool? IsCustomizable,
    string? LocalDefinitionId = null);
