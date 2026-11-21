using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Windows.Widgets.Providers;
using XiyiDesktop.Widgets.Diagnostics;
using XiyiDesktop.Widgets.Models;

namespace XiyiDesktop.Widgets.Interop;

/// <summary>Reads only the public WidgetManager API for the calling provider app.</summary>
internal sealed class WidgetManagerInterop(WidgetExperimentLogger logger)
{
    public WidgetInstanceDiscoveryResult ReadCurrentProviderInstances()
    {
        const string scope = "Public WidgetManager results are associated with the calling provider app.";
        var diagnostics = new List<WidgetApiCallDiagnostic>();
        var instancesById = new Dictionary<string, WidgetInstanceInfo>(StringComparer.Ordinal);
        var infoIds = new List<string>();
        var environment = logger.GetEnvironmentDetails();
        WidgetManager manager;

        try
        {
            manager = WidgetManager.GetDefault();
            Record(diagnostics, "WidgetManager.GetDefault", "Success", "A manager instance was returned.");
        }
        catch (Exception exception)
        {
            var status = IsUnsupportedException(exception)
                ? WidgetInstanceDiscoveryStatus.Unsupported
                : WidgetInstanceDiscoveryStatus.Unavailable;
            RecordException(diagnostics, "WidgetManager.GetDefault", exception);
            return new WidgetInstanceDiscoveryResult(
                status,
                scope,
                status == WidgetInstanceDiscoveryStatus.Unsupported
                    ? "The WidgetManager API is unsupported in this runtime environment."
                    : "WidgetManager could not be initialized in this runtime environment.",
                environment,
                Array.Empty<WidgetInstanceInfo>(),
                diagnostics);
        }

        string[]? widgetIds = null;
        try
        {
            widgetIds = manager.GetWidgetIds()?.ToArray() ?? Array.Empty<string>();
            Record(diagnostics, "WidgetManager.GetWidgetIds", "Success",
                $"Count={widgetIds.Length}; IDs=[{string.Join(", ", widgetIds)}]");
        }
        catch (Exception exception)
        {
            RecordException(diagnostics, "WidgetManager.GetWidgetIds", exception);
        }

        try
        {
            var widgetInfos = manager.GetWidgetInfos();
            if (widgetInfos is null)
            {
                Record(diagnostics, "WidgetManager.GetWidgetInfos", "ReturnedNull", "The API returned null.");
            }
            else
            {
                var count = 0;
                foreach (var info in widgetInfos)
                {
                    count++;
                    if (info is null)
                        continue;

                    try
                    {
                        var instance = Convert(info);
                        instancesById[instance.InstanceId] = instance;
                        if (!string.IsNullOrWhiteSpace(instance.InstanceId))
                            infoIds.Add(instance.InstanceId);
                    }
                    catch (Exception exception)
                    {
                        RecordException(diagnostics, "WidgetManager.GetWidgetInfos.ReadEntry", exception,
                            "An entry could not be copied into the diagnostic snapshot.");
                    }
                }

                Record(diagnostics, "WidgetManager.GetWidgetInfos", "Success",
                    $"Count={count}; usable entries={instancesById.Count}.");
            }
        }
        catch (Exception exception)
        {
            RecordException(diagnostics, "WidgetManager.GetWidgetInfos", exception);
        }

        if (widgetIds is not null && infoIds.Count > 0)
        {
            var listedIds = new HashSet<string>(widgetIds, StringComparer.Ordinal);
            var infoIdSet = new HashSet<string>(infoIds, StringComparer.Ordinal);
            if (!listedIds.SetEquals(infoIdSet))
            {
                Record(diagnostics, "WidgetManager ID consistency", "Failed",
                    $"GetWidgetIds count={listedIds.Count}; GetWidgetInfos count={infoIdSet.Count}; " +
                    $"IDs only in GetWidgetIds=[{string.Join(", ", listedIds.Except(infoIdSet, StringComparer.Ordinal))}]; " +
                    $"IDs only in GetWidgetInfos=[{string.Join(", ", infoIdSet.Except(listedIds, StringComparer.Ordinal))}].");
            }
        }

        var idsToQuery = new HashSet<string>(StringComparer.Ordinal);
        if (widgetIds is not null)
            idsToQuery.UnionWith(widgetIds);
        idsToQuery.UnionWith(infoIds);

        foreach (var widgetId in idsToQuery.Where(id => !string.IsNullOrWhiteSpace(id)))
        {
            try
            {
                var info = manager.GetWidgetInfo(widgetId);
                if (info is null)
                {
                    Record(diagnostics, "WidgetManager.GetWidgetInfo", "ReturnedNull", $"ID={widgetId}");
                    continue;
                }

                var instance = Convert(info);
                var wasInList = instancesById.ContainsKey(instance.InstanceId);
                instancesById[instance.InstanceId] = instance;
                Record(diagnostics, "WidgetManager.GetWidgetInfo", "Success",
                    $"ID={widgetId}; DefinitionId={instance.DefinitionId}; Size={instance.Size}; " +
                    $"IsActive={instance.IsActive}; LastUpdateTime={FormatTime(instance.LastUpdateTime)}; " +
                    $"TemplateLength={instance.Template?.Length ?? 0}; DataLength={instance.Data?.Length ?? 0}; " +
                    $"CustomStateLength={instance.CustomState?.Length ?? 0}; ListedByGetWidgetInfos={wasInList}.");
            }
            catch (Exception exception)
            {
                RecordException(diagnostics, "WidgetManager.GetWidgetInfo", exception, $"ID={widgetId}");
            }
        }

        var hasFailure = diagnostics.Any(item => item.Status is "Failed" or "ReturnedNull");
        var instances = instancesById.Values
            .OrderBy(item => item.InstanceId, StringComparer.Ordinal)
            .ToArray();
        var resultStatus = instances.Length == 0
            ? hasFailure ? WidgetInstanceDiscoveryStatus.Unavailable : WidgetInstanceDiscoveryStatus.Empty
            : hasFailure ? WidgetInstanceDiscoveryStatus.PartiallyAvailable : WidgetInstanceDiscoveryStatus.Available;
        var summary = resultStatus switch
        {
            WidgetInstanceDiscoveryStatus.Empty => "No instances were returned for the calling provider app.",
            WidgetInstanceDiscoveryStatus.Available => $"Read {instances.Length} instance(s) for the calling provider app.",
            WidgetInstanceDiscoveryStatus.PartiallyAvailable =>
                $"Read {instances.Length} instance(s); one or more public API calls or entries failed.",
            _ => "No instance details could be read; see the API diagnostics for failures."
        };

        logger.Write("WidgetManager.ReadCallingProviderInstances", resultStatus.ToString(),
            $"{summary} Scope={scope}");
        return new WidgetInstanceDiscoveryResult(resultStatus, scope, summary, environment, instances, diagnostics);
    }

    public WidgetKnownIdProbeResult ProbeKnownId(string widgetId)
    {
        const string scope = "GetWidgetInfo can only return information associated with the calling provider app.";
        if (string.IsNullOrWhiteSpace(widgetId))
        {
            var invalid = new WidgetApiCallDiagnostic("WidgetManager.GetWidgetInfo", "InvalidInput", "A widget ID is required.");
            return new WidgetKnownIdProbeResult(
                WidgetKnownIdProbeStatus.Failed, widgetId, scope, "A widget ID is required.", null, invalid);
        }

        try
        {
            var manager = WidgetManager.GetDefault();
            var info = manager.GetWidgetInfo(widgetId);
            if (info is null)
            {
                const string summary = "The ID returned no WidgetInfo for the calling provider app. This does not indicate whether the ID belongs to another provider.";
                var call = new WidgetApiCallDiagnostic("WidgetManager.GetWidgetInfo", "ReturnedNull", summary);
                logger.Write("WidgetManager.GetWidgetInfo known-ID probe", "ReturnedNull", $"ID={widgetId}; {summary}");
                return new WidgetKnownIdProbeResult(
                    WidgetKnownIdProbeStatus.NotFoundInCallingProviderScope, widgetId, scope, summary, null, call);
            }

            var instance = Convert(info);
            var details = $"ID={widgetId}; DefinitionId={instance.DefinitionId}; Size={instance.Size}; " +
                $"IsActive={instance.IsActive}; TemplateLength={instance.Template?.Length ?? 0}; " +
                $"DataLength={instance.Data?.Length ?? 0}; CustomStateLength={instance.CustomState?.Length ?? 0}.";
            var success = new WidgetApiCallDiagnostic("WidgetManager.GetWidgetInfo", "Success", details);
            logger.Write("WidgetManager.GetWidgetInfo known-ID probe", "Success", details);
            return new WidgetKnownIdProbeResult(
                WidgetKnownIdProbeStatus.FoundInCallingProviderScope, widgetId, scope,
                "WidgetInfo was returned in the calling provider app's scope.", instance, success);
        }
        catch (Exception exception)
        {
            var exceptionType = exception.GetType().FullName ?? exception.GetType().Name;
            var hResult = $"0x{exception.HResult.ToString("X8", CultureInfo.InvariantCulture)}";
            var details = $"ID={widgetId}; {exceptionType}: {exception.Message}";
            var failed = new WidgetApiCallDiagnostic(
                "WidgetManager.GetWidgetInfo", "Failed", details, exceptionType, hResult);
            logger.Write("WidgetManager.GetWidgetInfo known-ID probe", "Failed", details, exception);
            return new WidgetKnownIdProbeResult(
                WidgetKnownIdProbeStatus.Failed, widgetId, scope,
                "The known-ID probe failed; see the API diagnostic for the exception and HRESULT.", null, failed);
        }
    }

    private static WidgetInstanceInfo Convert(WidgetInfo info)
    {
        var context = info.WidgetContext;
        return new WidgetInstanceInfo(
            context.Id ?? string.Empty,
            context.DefinitionId ?? string.Empty,
            context.Size.ToString(),
            context.IsActive,
            info.LastUpdateTime,
            info.Template,
            info.Data,
            info.CustomState,
            "WidgetManager / calling provider app scope");
    }

    private void Record(
        ICollection<WidgetApiCallDiagnostic> diagnostics,
        string operation,
        string status,
        string details)
    {
        diagnostics.Add(new WidgetApiCallDiagnostic(operation, status, details));
        logger.Write(operation, status, details);
    }

    private void RecordException(
        ICollection<WidgetApiCallDiagnostic> diagnostics,
        string operation,
        Exception exception,
        string? details = null)
    {
        var exceptionType = exception.GetType().FullName ?? exception.GetType().Name;
        var hResult = $"0x{exception.HResult.ToString("X8", CultureInfo.InvariantCulture)}";
        var message = $"{details ?? string.Empty} {exceptionType}: {exception.Message}".Trim();
        diagnostics.Add(new WidgetApiCallDiagnostic(operation, "Failed", message, exceptionType, hResult));
        logger.Write(operation, "Failed", details, exception);
    }

    private static bool IsUnsupportedException(Exception exception) =>
        exception is NotSupportedException or TypeLoadException or DllNotFoundException or
            EntryPointNotFoundException or MissingMethodException;

    private static string FormatTime(DateTimeOffset? value) =>
        value?.ToString("O", CultureInfo.InvariantCulture) ?? "Unavailable";
}
