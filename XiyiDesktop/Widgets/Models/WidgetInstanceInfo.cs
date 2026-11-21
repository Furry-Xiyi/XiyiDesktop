using System;
using System.Collections.Generic;

namespace XiyiDesktop.Widgets.Models;

/// <summary>
/// A snapshot returned by the public WidgetManager API for this application's
/// provider scope. It does not represent a global list of installed widgets.
/// </summary>
internal sealed record WidgetInstanceInfo(
    string InstanceId,
    string DefinitionId,
    string Size,
    bool IsActive,
    DateTimeOffset? LastUpdateTime,
    string? Template,
    string? Data,
    string? CustomState,
    string Source);

internal enum WidgetInstanceDiscoveryStatus
{
    Available,
    Empty,
    PartiallyAvailable,
    Unsupported,
    Unavailable,
    Failed
}

internal sealed record WidgetApiCallDiagnostic(
    string Operation,
    string Status,
    string Details,
    string? ExceptionType = null,
    string? HResult = null);

internal enum WidgetKnownIdProbeStatus
{
    FoundInCallingProviderScope,
    NotFoundInCallingProviderScope,
    Unsupported,
    Failed
}

internal sealed record WidgetKnownIdProbeResult(
    WidgetKnownIdProbeStatus Status,
    string WidgetId,
    string ScopeDescription,
    string Summary,
    WidgetInstanceInfo? Instance,
    WidgetApiCallDiagnostic ApiCall);

internal sealed record WidgetInstanceDiscoveryResult(
    WidgetInstanceDiscoveryStatus Status,
    string ScopeDescription,
    string Summary,
    string EnvironmentDetails,
    IReadOnlyList<WidgetInstanceInfo> Instances,
    IReadOnlyList<WidgetApiCallDiagnostic> ApiCalls);
