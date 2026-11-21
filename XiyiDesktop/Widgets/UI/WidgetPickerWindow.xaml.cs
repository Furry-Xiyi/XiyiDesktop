using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.ApplicationModel.Resources;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Storage;
using Windows.UI;
using XiyiDesktop.Widgets.Diagnostics;
using XiyiDesktop.Widgets.Discovery;
using XiyiDesktop.Widgets.Models;
using XiyiDesktop.Widgets.Services;
using XiyiDesktop.Widgets.Hosting;
using Microsoft.Windows.Widgets;

namespace XiyiDesktop.Widgets.UI;

public sealed partial class WidgetPickerWindow : Window
{
    private readonly WidgetExperimentLogger _logger = new();
    private readonly WindowsWidgetDiscoveryService _discovery;
    private readonly WidgetCatalogService _catalog;
    private readonly DispatcherQueue _dispatcherQueue;
    private bool _isRefreshing;
    private bool _isUpdatingSelection;
    private bool _desktopPlacementAvailable = true;
    private bool _isAddInProgress;
    private string? _selectedSize;

    internal event EventHandler<WidgetAddRequestedEventArgs>? AddRequested;
    internal event EventHandler<WidgetPlacementCheckRequestedEventArgs>? PlacementCheckRequested;
    internal void RefreshPlacementCheck() => RaisePlacementCheckRequested();

    public WidgetPickerWindow()
    {
        InitializeComponent();

        _discovery = new WindowsWidgetDiscoveryService(_logger);
        _catalog = new WidgetCatalogService(_discovery);
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _discovery.CatalogChanged += Discovery_CatalogChanged;
        Closed += Window_Closed;

        TitleBarArea.Window = this;
        TitleBarArea.Title = Package.Current.DisplayName;
        TitleBarArea.Subtitle = Get("WidgetPickerTitle");
        TitleBarArea.Icon = CreateWindowTitleIcon();
        WindowIconAssetResolver.AssignPackageApplicationIdentity(this, "AddWidget");
        AppWindow.Title = Get("WidgetPickerTitle");

        if (Content is FrameworkElement root)
        {
            root.RequestedTheme = AppThemeManager.CurrentTheme;
            root.ActualThemeChanged += Root_ActualThemeChanged;
            root.Loaded += Root_Loaded;
        }

        ConfigureTitleBar();
        _ = RefreshCatalogAsync();
    }

    private IconElement CreateWindowTitleIcon()
    {
        return WindowIconAssetResolver.CreateTitleBarIcon("AddWidget");
    }

    private void Root_Loaded(object sender, RoutedEventArgs e) => ConfigureTitleBar();

    private void Root_ActualThemeChanged(FrameworkElement sender, object args)
    {
        ConfigureTitleBar();
        foreach (var navigationEntry in WidgetNavigation.MenuItems.OfType<NavigationViewItem>())
        {
            if (navigationEntry.Tag is WidgetPickerEntry navigationWidget && navigationWidget.NavigationIcon is { } icon)
                navigationWidget.NavigationIconLoadTask = LoadWidgetIconAsync(navigationWidget, icon, sender.ActualTheme);
        }

        if (WidgetNavigation.SelectedItem is NavigationViewItem item && item.Tag is WidgetPickerEntry entry)
            _ = RenderSelectionAsync(entry);
    }

    private void ConfigureTitleBar()
    {
        try
        {
            if (!AppWindowTitleBar.IsCustomizationSupported())
                return;

            var titleBar = AppWindow.TitleBar;
            titleBar.ExtendsContentIntoTitleBar = true;
            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            bool dark = Content is FrameworkElement root && root.ActualTheme == ElementTheme.Dark;
            var foreground = dark ? Colors.White : Colors.Black;
            var inactive = dark ? Color.FromArgb(255, 128, 128, 128) : Color.FromArgb(255, 160, 160, 160);
            var hover = dark ? Color.FromArgb(20, 255, 255, 255) : Color.FromArgb(20, 0, 0, 0);
            titleBar.ButtonForegroundColor = foreground;
            titleBar.ButtonInactiveForegroundColor = inactive;
            titleBar.ButtonHoverBackgroundColor = hover;
            titleBar.ButtonHoverForegroundColor = foreground;
            titleBar.ButtonPressedBackgroundColor = Color.FromArgb(30, hover.R, hover.G, hover.B);
            titleBar.ButtonPressedForegroundColor = foreground;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Widget picker title bar setup failed: {exception.Message}");
        }
    }

    private void Window_Closed(object sender, WindowEventArgs args)
    {
        _discovery.CatalogChanged -= Discovery_CatalogChanged;
        _discovery.Dispose();
    }

    private void Discovery_CatalogChanged(object? sender, EventArgs e)
    {
        _dispatcherQueue.TryEnqueue(async () => await RefreshCatalogAsync());
    }

    private async Task RefreshCatalogAsync()
    {
        if (_isRefreshing)
            return;

        _isRefreshing = true;
        CatalogStatusText.Text = Get("WidgetPickerLoading/Content");
        try
        {
            var snapshot = await _catalog.RefreshAsync();
            var discoveredEntries = snapshot.Providers
                .SelectMany(provider => provider.Definitions.Select(definition => new WidgetPickerEntry(provider, definition)))
                .ToArray();

            foreach (var entry in discoveredEntries)
                await HydrateEntryAsync(entry);

            var entries = discoveredEntries
                .OrderBy(entry => entry.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(entry => entry.ProviderName, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();

            WidgetNavigation.MenuItems.Clear();
            foreach (var entry in entries)
            {
                WidgetNavigation.MenuItems.Add(new NavigationViewItem
                {
                    Content = BuildNavigationContent(entry),
                    Tag = entry,
                    SelectsOnInvoked = true
                });
            }

            if (entries.Length == 0)
            {
                WidgetNavigation.SelectedItem = null;
                ClearSelection();
                CatalogStatusText.Text = snapshot.Diagnostics.Count > 0
                    ? Get("WidgetPickerCatalogUnavailable")
                    : Get("WidgetPickerEmptyCatalog");
                return;
            }

            CatalogStatusText.Text = Format("WidgetPickerCatalogCount", entries.Length);
            WidgetNavigation.SelectedItem = WidgetNavigation.MenuItems[0];
            await RenderSelectionAsync(entries[0]);
        }
        catch (Exception exception)
        {
            _logger.Write("Load widget picker catalog", "Failed", null, exception);
            WidgetNavigation.MenuItems.Clear();
            ClearSelection();
            CatalogStatusText.Text = Get("WidgetPickerCatalogUnavailable");
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private StackPanel BuildNavigationContent(WidgetPickerEntry entry)
    {
        var icon = new Image
        {
            Width = 16,
            Height = 16,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center
        };
        entry.NavigationIcon = icon;
        entry.NavigationIconLoadTask = LoadWidgetIconAsync(
            entry,
            icon,
            Content is FrameworkElement root ? root.ActualTheme : ElementTheme.Default);

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                icon,
                new TextBlock
                {
                    Text = entry.DisplayName,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center
                }
            }
        };
    }

    private async Task HydrateEntryAsync(WidgetPickerEntry entry)
    {
        try
        {
            var safeDefinition = new SafeWidgetDefinition(App.WidgetHostService, entry.Definition.DefinitionId);
            if (!await safeDefinition.PopulateAsync())
                return;

            entry.HostDefinition = safeDefinition;
            var runtimeSizes = await safeDefinition.GetSupportedSizesAsync();
            if (runtimeSizes.Count > 0)
                entry.SupportedSizes = runtimeSizes.Select(size => size.ToString()).ToArray();

            // Match the host selector in WidgetsVsProbe: use DisplayTitle when
            // present and otherwise keep the registered definition ID.
            var title = !string.IsNullOrWhiteSpace(safeDefinition.DisplayTitle)
                ? safeDefinition.DisplayTitle
                : safeDefinition.Id;

            entry.DisplayName = title;
        }
        catch (Exception exception)
        {
            _logger.Write("Load widget display metadata", "Failed", entry.Definition.DefinitionId, exception);
        }
    }

    private void WidgetNavigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_isUpdatingSelection || args.SelectedItem is not NavigationViewItem item || item.Tag is not WidgetPickerEntry entry)
            return;

        _ = RenderSelectionAsync(entry);
    }

    private async Task RenderSelectionAsync(WidgetPickerEntry entry)
    {
        _isUpdatingSelection = true;
        try
        {
            WidgetNameText.Text = entry.DisplayName;
            BuildSizeOptions(entry.SupportedSizes.Length > 0 ? entry.SupportedSizes : entry.Definition.SupportedSizes);
            UpdateAddButtonState();

            var theme = Content is FrameworkElement root ? root.ActualTheme : ElementTheme.Default;
            if (entry.NavigationIcon is not null)
            {
                if (entry.NavigationIconLoadTask is not null)
                    await entry.NavigationIconLoadTask;
                WidgetIconImage.Source = entry.NavigationIcon.Source;
            }
            else
            {
                await LoadWidgetIconAsync(entry, WidgetIconImage, theme);
            }

            var screenshot = SelectThemedResource(entry.Definition.Resources.Where(IsScreenshot), theme);
            bool loaded = await SetImageAsync(WidgetPreviewImage, screenshot, 880, 560);
            WidgetPreviewFallbackText.Visibility = loaded ? Visibility.Collapsed : Visibility.Visible;
            RaisePlacementCheckRequested();
        }
        finally
        {
            _isUpdatingSelection = false;
        }
    }

    private void BuildSizeOptions(IReadOnlyList<string> sizes)
    {
        _selectedSize = sizes.FirstOrDefault();
        SizeButton.Content = _selectedSize is null ? Get("WidgetPickerSizeLabel") : LocalizeSize(_selectedSize);
    }

    private string LocalizeSize(string size) => size.Trim().ToLowerInvariant() switch
    {
        "small" => Get("WidgetPickerSizeSmall"),
        "medium" => Get("WidgetPickerSizeMedium"),
        "large" => Get("WidgetPickerSizeLarge"),
        _ => size
    };

    private async Task<bool> SetImageAsync(Image image, WidgetResourceInfo? resource, int pixelWidth, int pixelHeight)
    {
        image.Source = null;
        if (string.IsNullOrWhiteSpace(resource?.ResolvedPath) || !File.Exists(resource.ResolvedPath))
            return false;

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(resource.ResolvedPath);
            using var stream = await file.OpenReadAsync();
            if (Path.GetExtension(resource.ResolvedPath).Equals(".svg", StringComparison.OrdinalIgnoreCase))
            {
                var svg = new SvgImageSource
                {
                    RasterizePixelWidth = pixelWidth,
                    RasterizePixelHeight = pixelHeight
                };
                if (await svg.SetSourceAsync(stream) != SvgImageSourceLoadStatus.Success)
                    return false;
                image.Source = svg;
            }
            else
            {
                var bitmap = new BitmapImage { DecodePixelWidth = pixelWidth };
                await bitmap.SetSourceAsync(stream);
                image.Source = bitmap;
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            _logger.Write("Load widget image resource", "Failed", resource.ResolvedPath, exception);
            return false;
        }
    }

    private async Task LoadWidgetIconAsync(WidgetPickerEntry entry, Image image, ElementTheme theme)
    {
        image.Source = null;
        try
        {
            var safeDefinition = entry.HostDefinition;
            if (safeDefinition is null)
            {
                safeDefinition = new SafeWidgetDefinition(App.WidgetHostService, entry.Definition.DefinitionId);
                if (await safeDefinition.PopulateAsync())
                    entry.HostDefinition = safeDefinition;
            }

            if (safeDefinition is not null)
            {
                var iconReference = safeDefinition.ProviderDefinition?.Icon;
                if (iconReference is null)
                {
                    var widgetTheme = theme == ElementTheme.Dark
                        ? Microsoft.Windows.Widgets.Hosts.WidgetTheme.Dark
                        : Microsoft.Windows.Widgets.Hosts.WidgetTheme.Light;
                    iconReference = (await safeDefinition.GetThemeResourcesAsync(widgetTheme))?.Icon;
                }

                if (iconReference is not null)
                {
                    using var stream = await iconReference.OpenReadAsync();
                    ImageSource? source = null;
                    if (string.Equals(stream.ContentType, "image/svg+xml", StringComparison.OrdinalIgnoreCase))
                    {
                        var svg = new SvgImageSource { RasterizePixelWidth = 96, RasterizePixelHeight = 96 };
                        if (await svg.SetSourceAsync(stream) == SvgImageSourceLoadStatus.Success)
                            source = svg;
                    }
                    else
                    {
                        var bitmap = new BitmapImage { DecodePixelWidth = 96, DecodePixelHeight = 96 };
                        await bitmap.SetSourceAsync(stream);
                        source = bitmap;
                    }

                    if (source is not null)
                    {
                        image.Source = source;
                        return;
                    }
                }
            }
        }
        catch (Exception exception)
        {
            _logger.Write("Load runtime widget icon", "Fallback", entry.Definition.DefinitionId, exception);
        }

        var manifestIcon = SelectThemedResource(
            entry.Definition.Resources.Where(IsIcon).Concat(entry.Provider.ProviderIcons.Where(IsIcon)),
            theme);
        await SetImageAsync(image, manifestIcon, 96, 96);
    }

    private static WidgetResourceInfo? SelectThemedResource(IEnumerable<WidgetResourceInfo> resources, ElementTheme theme)
    {
        var candidates = resources.Where(resource => !string.IsNullOrWhiteSpace(resource.ResolvedPath)).ToArray();
        var preferredTheme = theme switch
        {
            ElementTheme.Dark => "DarkMode",
            ElementTheme.Light => "LightMode",
            _ => null
        };

        if (preferredTheme is not null)
        {
            var themed = candidates.FirstOrDefault(resource => resource.Theme.Equals(preferredTheme, StringComparison.OrdinalIgnoreCase));
            if (themed is not null)
                return themed;
        }

        return candidates.FirstOrDefault(resource => resource.Theme.Equals("Default", StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault(resource => resource.Theme.Equals("Provider", StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault();
    }

    private static bool IsIcon(WidgetResourceInfo resource) => resource.Kind.Equals("Icon", StringComparison.OrdinalIgnoreCase);
    private static bool IsScreenshot(WidgetResourceInfo resource) => resource.Kind.Equals("Screenshot", StringComparison.OrdinalIgnoreCase);

    private void ClearSelection()
    {
        WidgetNameText.Text = string.Empty;
        WidgetIconImage.Source = null;
        WidgetPreviewImage.Source = null;
        WidgetPreviewFallbackText.Visibility = Visibility.Visible;
        _selectedSize = null;
        SizeButton.Content = Get("WidgetPickerSizeLabel");
        AddButton.IsEnabled = false;
    }

    private void SizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (WidgetNavigation.SelectedItem is not NavigationViewItem item || item.Tag is not WidgetPickerEntry entry)
            return;

        var flyout = new MenuFlyout();
        foreach (var size in entry.Definition.SupportedSizes)
        {
            var menuItem = new MenuFlyoutItem { Text = LocalizeSize(size), Tag = size };
            menuItem.Click += SizeMenuItem_Click;
            flyout.Items.Add(menuItem);
        }

        if (flyout.Items.Count > 0)
            flyout.ShowAt(SizeButton);
    }

    private void SizeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem item || item.Tag is not string size)
            return;

        _selectedSize = size;
        SizeButton.Content = LocalizeSize(size);
        UpdateAddButtonState();
        RaisePlacementCheckRequested();
    }

    private void RaisePlacementCheckRequested()
    {
        if (WidgetNavigation.SelectedItem is NavigationViewItem item && item.Tag is WidgetPickerEntry entry && _selectedSize is not null)
        {
            PlacementCheckRequested?.Invoke(this,
                new WidgetPlacementCheckRequestedEventArgs(entry.Provider, entry.Definition, _selectedSize));
        }
    }

    private void UpdateAddButtonState() =>
        AddButton.IsEnabled = _desktopPlacementAvailable && !_isAddInProgress && _selectedSize is not null;

    /// <summary>Updates whether the desktop currently has room for another widget.</summary>
    public void SetDesktopPlacementAvailability(bool available, bool alreadyAdded = false)
    {
        _desktopPlacementAvailable = available;
        DesktopFullHint.Visibility = available ? Visibility.Collapsed : Visibility.Visible;
        DesktopFullHint.Text = alreadyAdded
            ? Get("WidgetPickerAlreadyAdded")
            : Get("WidgetPickerDesktopFullHint/Text");
        UpdateAddButtonState();
    }

    /// <summary>Shows a busy state while the selected widget is being created.</summary>
    public void SetAddInProgress(bool inProgress)
    {
        _isAddInProgress = inProgress;
        AddButton.Content = inProgress ? Get("WidgetPickerAdding") : Get("WidgetPickerAddLabel");
        UpdateAddButtonState();
    }

    /// <summary>Closes the picker after the desktop has accepted a widget.</summary>
    public void CloseAfterAdd()
    {
        try { Close(); } catch { }
    }

    public void ShowAddFailure() => CatalogStatusText.Text = Get("WidgetPickerAddFailed");

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        if (WidgetNavigation.SelectedItem is not NavigationViewItem item || item.Tag is not WidgetPickerEntry entry
            || _selectedSize is null)
        {
            return;
        }

        AddRequested?.Invoke(this, new WidgetAddRequestedEventArgs(entry.Provider, entry.Definition, _selectedSize));
    }

    private static string Get(string key)
    {
        try
        {
            return new ResourceLoader().GetString(key);
        }
        catch (COMException exception) when (exception.HResult == unchecked((int)0x80073B17))
        {
            System.Diagnostics.Debug.WriteLine($"Widget picker resource '{key}' was not found.");
            return string.Empty;
        }
    }

    private static string Format(string key, params object[] args) =>
        string.Format(System.Globalization.CultureInfo.CurrentCulture, Get(key), args);
}

internal sealed class WidgetPickerEntry(WidgetProviderInfo provider, WidgetDefinition definition)
{
    public WidgetProviderInfo Provider { get; } = provider;
    public WidgetDefinition Definition { get; } = definition;
    public string DisplayName { get; set; } = string.IsNullOrWhiteSpace(definition.DisplayName) ? definition.DefinitionId : definition.DisplayName;
    public string ProviderName => string.IsNullOrWhiteSpace(Provider.AppName) ? Provider.PackageName : Provider.AppName;
    public SafeWidgetDefinition? HostDefinition { get; set; }
    public string[] SupportedSizes { get; set; } = [];
    public Image? NavigationIcon { get; set; }
    public Task? NavigationIconLoadTask { get; set; }
}

internal sealed class WidgetAddRequestedEventArgs(WidgetProviderInfo provider, WidgetDefinition definition, string size) : EventArgs
{
    public WidgetProviderInfo Provider { get; } = provider;
    public WidgetDefinition Definition { get; } = definition;
    public string Size { get; } = size;
}

internal sealed class WidgetPlacementCheckRequestedEventArgs(WidgetProviderInfo provider, WidgetDefinition definition, string size) : EventArgs
{
    public WidgetProviderInfo Provider { get; } = provider;
    public WidgetDefinition Definition { get; } = definition;
    public string Size { get; } = size;
}

