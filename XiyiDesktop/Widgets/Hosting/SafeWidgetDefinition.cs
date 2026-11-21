using Microsoft.Windows.Widgets;
using Microsoft.Windows.Widgets.Hosts;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace XiyiDesktop.Widgets.Hosting;

/// <summary>Reads definition metadata and capabilities while tolerating host RPC restarts.</summary>
internal sealed class SafeWidgetDefinition(WidgetHostingService hosting, string definitionId)
{
    private const int MaxAttempts = 3;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private WidgetDefinition? _definition;
    private bool _propertiesLoaded;

    public bool AllowMultiple { get; private set; }
    public string Description { get; private set; } = string.Empty;
    public string DisplayTitle { get; private set; } = string.Empty;
    public string Id { get; private set; } = definitionId;
    public WidgetProviderDefinition? ProviderDefinition { get; private set; }
    public string ProviderDefinitionDisplayName { get; private set; } = string.Empty;
    public string ProviderDefinitionId { get; private set; } = string.Empty;
    public string AdditionalInfoUri { get; private set; } = string.Empty;
    public bool IsCustomizable { get; private set; }
    public WidgetType Type { get; private set; }

    public async Task<bool> PopulateAsync()
    {
        await LoadDefinitionAsync();
        return _propertiesLoaded;
    }

    public Task<WidgetThemeResources?> GetThemeResourcesAsync(WidgetTheme theme) => InvokeAsync(
        definition => Task.Run(() => definition.GetThemeResource(theme)), null);

    public async Task<IReadOnlyList<WidgetSize>> GetSupportedSizesAsync()
    {
        var capabilities = await InvokeAsync(
            definition => Task.Run(() => definition.GetWidgetCapabilities()),
            Array.Empty<WidgetCapability>());
        return capabilities
            .Select(capability => capability.Size)
            .Distinct()
            .OrderBy(size => (int)size)
            .ToArray();
    }

    private async Task<T> InvokeAsync<T>(Func<WidgetDefinition, Task<T>> operation, T fallback)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            await LoadDefinitionAsync();
            var definition = _definition;
            if (definition is null)
                return fallback;

            try
            {
                return await operation(definition);
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"Widget definition {Id} operation failed (attempt {attempt + 1}/{MaxAttempts}): {exception}");
                if (!WidgetHostingService.IsRpcFailure(exception))
                    return fallback;

                _definition = null;
                _propertiesLoaded = false;
            }
        }

        return fallback;
    }

    private async Task LoadDefinitionAsync()
    {
        await _loadLock.WaitAsync();
        try
        {
            for (var attempt = 0; attempt < MaxAttempts && (_definition is null || !_propertiesLoaded); attempt++)
            {
                try
                {
                    _definition ??= await hosting.GetWidgetDefinitionAsync(Id);
                    if (_definition is null)
                        return;

                    await Task.Run(() =>
                    {
                        AllowMultiple = _definition.AllowMultiple;
                        Description = _definition.Description;
                        DisplayTitle = _definition.DisplayTitle;
                        Id = _definition.Id;
                        ProviderDefinition = _definition.ProviderDefinition;
                        ProviderDefinitionDisplayName = _definition.ProviderDefinition.DisplayName;
                        ProviderDefinitionId = _definition.ProviderDefinition.Id;
                        AdditionalInfoUri = _definition.AdditionalInfoUri;
                        IsCustomizable = _definition.IsCustomizable;
                        Type = _definition.Type;
                    });
                    _propertiesLoaded = true;
                }
                catch (Exception exception)
                {
                    Debug.WriteLine($"Could not load widget definition {Id}: {exception}");
                    _definition = null;
                }
            }
        }
        finally
        {
            _loadLock.Release();
        }
    }
}
