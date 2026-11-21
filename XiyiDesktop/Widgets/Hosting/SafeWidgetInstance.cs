using Microsoft.Windows.Widgets;
using Microsoft.Windows.Widgets.Hosts;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;

namespace XiyiDesktop.Widgets.Hosting;

/// <summary>Reacquires an out-of-process Widget object after the host RPC server restarts.</summary>
internal sealed class SafeWidgetInstance(WidgetHostingService hosting, string widgetId)
{
    private const int MaxAttempts = 3;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private Widget? _widget;
    private bool _propertiesLoaded;

    public string Id { get; private set; } = widgetId;
    public string DefinitionId { get; private set; } = string.Empty;

    public event TypedEventHandler<SafeWidgetInstance, WidgetUpdatedEventArgs>? WidgetUpdated;

    public async Task<bool> PopulateAsync()
    {
        await LoadWidgetAsync();
        return _propertiesLoaded;
    }

    public Task<WidgetSize> GetSizeAsync() => InvokeAsync(widget => Task.Run(async () => await widget.GetSizeAsync()), WidgetSize.Medium);

    public Task<bool> SetSizeAsync(WidgetSize size) => InvokeAsync(
        async widget => { await Task.Run(async () => await widget.SetSizeAsync(size)); return true; }, false);

    public Task<string> GetCardTemplateAsync() => InvokeAsync(
        async widget => await Task.Run(async () => await widget.GetCardTemplateAsync()) ?? "{}", "{}");

    public Task<string> GetCardDataAsync() => InvokeAsync(
        async widget => await Task.Run(async () => await widget.GetCardDataAsync()) ?? "{}", "{}");

    public Task<string> GetCustomStateAsync() => InvokeAsync(
        async widget => await Task.Run(async () => await widget.GetCustomStateAsync()) ?? string.Empty, string.Empty);

    public Task<bool> SetCustomStateAsync(string state) => InvokeAsync(
        async widget => { await Task.Run(async () => await widget.SetCustomStateAsync(state)); return true; }, false);

    public Task<bool> NotifyActionInvokedAsync(string verb, string data) => InvokeAsync(
        async widget =>
        {
            try
            {
                var nativeObject = ((WinRT.IWinRTObject)widget).NativeObject;
                CoAllowSetForegroundWindow(nativeObject.ThisPtr, IntPtr.Zero);
            }
            catch (Exception exception)
            {
                // Keep forwarding even if Windows declines the foreground grant.
                Debug.WriteLine($"Could not grant foreground permission to widget {Id}: {exception}");
            }

            await Task.Run(async () => await widget.NotifyActionInvokedAsync(verb, data));
            return true;
        }, false);

    public Task<bool> NotifyCustomizationRequestedAsync() => InvokeAsync(
        async widget => { await Task.Run(async () => await widget.NotifyCustomizationRequestedAsync()); return true; }, false);

    public async Task<bool> DeleteAsync()
    {
        var success = await InvokeAsync(
            async widget => { await Task.Run(async () => await widget.DeleteAsync()); return true; }, false);
        if (success)
        {
            _widget = null;
            _propertiesLoaded = false;
        }

        return success;
    }

    public Widget? GetUnsafeWidgetObject() => _widget;

    private async Task<T> InvokeAsync<T>(Func<Widget, Task<T>> operation, T fallback)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            await LoadWidgetAsync();
            var widget = _widget;
            if (widget is null)
                return fallback;

            try
            {
                return await operation(widget);
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"Widget instance {Id} operation failed (attempt {attempt + 1}/{MaxAttempts}): {exception}");
                if (!WidgetHostingService.IsRpcFailure(exception))
                    return fallback;

                _widget = null;
                _propertiesLoaded = false;
            }
        }

        return fallback;
    }

    private async Task LoadWidgetAsync()
    {
        await _loadLock.WaitAsync();
        try
        {
            for (var attempt = 0; attempt < MaxAttempts && (_widget is null || !_propertiesLoaded); attempt++)
            {
                try
                {
                    _widget ??= await hosting.GetWidgetAsync(Id);
                    if (_widget is null)
                        return;

                    await Task.Run(() =>
                    {
                        DefinitionId = _widget.DefinitionId;
                        Id = _widget.Id;
                        _widget.WidgetUpdated -= OopWidgetUpdated;
                        _widget.WidgetUpdated += OopWidgetUpdated;
                    });
                    _propertiesLoaded = true;
                }
                catch (Exception exception)
                {
                    Debug.WriteLine($"Could not load widget instance {Id}: {exception}");
                    _widget = null;
                }
            }
        }
        finally
        {
            _loadLock.Release();
        }
    }

    [DllImport("ole32.dll", ExactSpelling = true, PreserveSig = false, SetLastError = true)]
    private static extern void CoAllowSetForegroundWindow(IntPtr unknown, IntPtr reserved);

    private void OopWidgetUpdated(Widget sender, WidgetUpdatedEventArgs args) => WidgetUpdated?.Invoke(this, args);
}
