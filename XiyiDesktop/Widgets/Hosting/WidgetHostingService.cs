using Microsoft.Windows.Widgets;
using Microsoft.Windows.Widgets.Hosts;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace XiyiDesktop.Widgets.Hosting;

/// <summary>Owns the Windows Widgets host and catalog objects for this application.</summary>
internal sealed class WidgetHostingService
{
    // This stable host context lets Windows associate instances created by XiyiDesktop with
    // the same host when the desktop shell is restarted.
    private const string HostContextId = "BAA93438-9B07-4554-AD09-7ACCD7D4F031";
    private const int RpcServerUnavailable = unchecked((int)0x800706BA);
    private const int RpcCallFailed = unchecked((int)0x800706BE);
    private const int MaxAttempts = 3;

    private readonly SemaphoreSlim _serviceLock = new(1, 1);
    private WidgetHost? _host;
    private WidgetCatalog? _catalog;

    public Task<Widget[]> GetWidgetsAsync() => RetryHostCallAsync(
        async host => await Task.Run(() => host.GetWidgets()) ?? [],
        []);

    public Task<Widget?> GetWidgetAsync(string widgetId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(widgetId);
        return RetryHostCallAsync<Widget?>(host => Task.Run(() => host.GetWidget(widgetId)), null);
    }

    public Task<Widget?> CreateWidgetAsync(string definitionId, WidgetSize size)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionId);
        return RetryHostCallAsync<Widget?>(host => Task.Run(async () => await host.CreateWidgetAsync(definitionId, size)), null);
    }

    public Task<WidgetCatalog?> GetWidgetCatalogAsync() => RetryCatalogCallAsync<WidgetCatalog?>(catalog => Task.FromResult(catalog), null);

    /// <summary>Runs the same lightweight catalog liveness check used by the working widget probe.</summary>
    public async Task<bool> EnsureCatalogReadyForCreationAsync()
    {
        var catalog = await GetWidgetCatalogAsync();
        if (catalog is null)
            return false;

        catalog.WidgetDefinitionUpdated += (_, _) => { };
        catalog.WidgetDefinitionDeleted += (_, _) => { };
        try
        {
            await Task.Run(() => catalog.GetWidgetDefinition("fakeWidgetDefinitionId"));
            return true;
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Widget catalog liveness check failed: {exception}");
            if (IsRpcFailure(exception))
                await ResetServiceObjectsAsync();
            return false;
        }
    }

    public Task<WidgetProviderDefinition[]> GetProviderDefinitionsAsync() => RetryCatalogCallAsync(
        async catalog => await Task.Run(() => catalog.GetProviderDefinitions()) ?? [],
        []);

    public Task<WidgetDefinition[]> GetWidgetDefinitionsAsync() => RetryCatalogCallAsync(
        async catalog => await Task.Run(() => catalog.GetWidgetDefinitions()) ?? [],
        []);

    public Task<WidgetDefinition?> GetWidgetDefinitionAsync(string definitionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionId);
        return RetryCatalogCallAsync<WidgetDefinition?>(catalog => Task.Run(() => catalog.GetWidgetDefinition(definitionId)), null);
    }

    private async Task<T> RetryHostCallAsync<T>(Func<WidgetHost, Task<T>> operation, T fallback)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            try
            {
                var host = await GetHostAsync();
                return await operation(host);
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"Widget host operation failed (attempt {attempt + 1}/{MaxAttempts}): {exception}");
                if (IsRpcFailure(exception))
                    await ResetServiceObjectsAsync();
                else if (attempt + 1 >= MaxAttempts)
                    break;
            }
        }

        return fallback;
    }

    private async Task<T> RetryCatalogCallAsync<T>(Func<WidgetCatalog, Task<T>> operation, T fallback)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            try
            {
                var catalog = await GetCatalogAsync();
                return await operation(catalog);
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"Widget catalog operation failed (attempt {attempt + 1}/{MaxAttempts}): {exception}");
                await ResetServiceObjectsAsync();
            }
        }

        return fallback;
    }

    private async Task<WidgetHost> GetHostAsync()
    {
        await _serviceLock.WaitAsync();
        try
        {
            _host ??= await Task.Run(() => WidgetHost.Register(new WidgetHostContext(HostContextId)));
            return _host;
        }
        finally
        {
            _serviceLock.Release();
        }
    }

    private async Task<WidgetCatalog> GetCatalogAsync()
    {
        await _serviceLock.WaitAsync();
        try
        {
            _catalog ??= await Task.Run(() => WidgetCatalog.GetDefault());
            return _catalog;
        }
        finally
        {
            _serviceLock.Release();
        }
    }

    private async Task ResetServiceObjectsAsync()
    {
        await _serviceLock.WaitAsync();
        try
        {
            _host = null;
            _catalog = null;
        }
        finally
        {
            _serviceLock.Release();
        }
    }

    internal static bool IsRpcFailure(Exception exception) =>
        exception is COMException comException &&
        (comException.HResult == RpcServerUnavailable || comException.HResult == RpcCallFailed);
}
