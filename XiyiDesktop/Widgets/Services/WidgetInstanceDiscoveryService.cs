using System;
using System.Collections.Generic;
using System.Globalization;
using XiyiDesktop.Widgets.Diagnostics;
using XiyiDesktop.Widgets.Interop;
using XiyiDesktop.Widgets.Models;

namespace XiyiDesktop.Widgets.Services;

/// <summary>Provides a safe, read-only boundary around WidgetManager diagnostics.</summary>
internal sealed class WidgetInstanceDiscoveryService(
    WidgetManagerInterop widgetManager,
    WidgetExperimentLogger logger)
{
    private const string ScopeDescription =
        "Public WidgetManager results are associated with the calling provider app; this is not a board-wide instance query.";

    public WidgetInstanceDiscoveryResult ReadCallingProviderInstances()
    {
        var environment = logger.GetEnvironmentDetails();
        if (!OperatingSystem.IsWindows())
        {
            const string summary = "WidgetManager is available only on Windows.";
            logger.Write("WidgetManager availability", "Unsupported", summary);
            return Create(WidgetInstanceDiscoveryStatus.Unsupported, summary, environment);
        }

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
        {
            const string summary = "Windows App SDK Widgets require Windows 11 version 22H2 (build 22621) or later.";
            logger.Write("WidgetManager availability", "Unsupported", summary);
            return Create(WidgetInstanceDiscoveryStatus.Unsupported, summary, environment);
        }

        try
        {
            return widgetManager.ReadCurrentProviderInstances();
        }
        catch (Exception exception)
        {
            var exceptionType = exception.GetType().FullName ?? exception.GetType().Name;
            var hResult = $"0x{exception.HResult.ToString("X8", CultureInfo.InvariantCulture)}";
            const string summary = "The read-only WidgetManager diagnostic could not complete.";
            logger.Write("WidgetManager availability boundary", "Failed", summary, exception);
            return new WidgetInstanceDiscoveryResult(
                WidgetInstanceDiscoveryStatus.Failed,
                ScopeDescription,
                summary,
                environment,
                Array.Empty<WidgetInstanceInfo>(),
                new[]
                {
                    new WidgetApiCallDiagnostic(
                        "WidgetManager.ReadCallingProviderInstances",
                        "Failed",
                        $"{exceptionType}: {exception.Message}",
                        exceptionType,
                        hResult)
                });
        }
    }

    public WidgetKnownIdProbeResult ProbeKnownId(string widgetId)
    {
        if (!OperatingSystem.IsWindows() || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
        {
            const string summary = "WidgetManager is unsupported in this Windows environment.";
            logger.Write("WidgetManager known-ID probe", "Unsupported", summary);
            return new WidgetKnownIdProbeResult(
                WidgetKnownIdProbeStatus.Unsupported,
                widgetId,
                ScopeDescription,
                summary,
                null,
                new WidgetApiCallDiagnostic("WidgetManager.GetWidgetInfo", "Unsupported", summary));
        }

        try
        {
            return widgetManager.ProbeKnownId(widgetId);
        }
        catch (Exception exception)
        {
            var exceptionType = exception.GetType().FullName ?? exception.GetType().Name;
            var hResult = $"0x{exception.HResult.ToString("X8", CultureInfo.InvariantCulture)}";
            const string summary = "The known-ID probe could not complete.";
            logger.Write("WidgetManager known-ID probe boundary", "Failed", summary, exception);
            return new WidgetKnownIdProbeResult(
                WidgetKnownIdProbeStatus.Failed,
                widgetId,
                ScopeDescription,
                summary,
                null,
                new WidgetApiCallDiagnostic(
                    "WidgetManager.GetWidgetInfo",
                    "Failed",
                    $"{exceptionType}: {exception.Message}",
                    exceptionType,
                    hResult));
        }
    }

    private static WidgetInstanceDiscoveryResult Create(
        WidgetInstanceDiscoveryStatus status,
        string summary,
        string environment) =>
        new(status, ScopeDescription, summary, environment,
            Array.Empty<WidgetInstanceInfo>(), Array.Empty<WidgetApiCallDiagnostic>());
}
