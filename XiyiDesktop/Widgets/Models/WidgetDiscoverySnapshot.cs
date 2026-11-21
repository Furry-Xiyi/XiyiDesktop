using System;
using System.Collections.Generic;

namespace XiyiDesktop.Widgets.Models;

internal sealed record WidgetDiscoveryDiagnostic(
    DateTimeOffset Timestamp,
    string Operation,
    string Message,
    int? HResult,
    string? PackageFamilyName);

internal sealed record WidgetDiscoverySnapshot(
    DateTimeOffset ScannedAt,
    string ExtensionName,
    IReadOnlyList<WidgetProviderInfo> Providers,
    IReadOnlyList<WidgetDiscoveryDiagnostic> Diagnostics,
    string OperatingSystemVersion);
