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
using System.ComponentModel;
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
    private readonly Dictionary<WidgetPickerEntry, Task<ImageSource?>> _previewLoads = [];
    private bool _isRefreshing;
    private bool _isUpdatingSelection;
    private bool _desktopPlacementAvailable = true;
    private bool _alreadyAdded;
    private bool _isAddInProgress;
    private string? _addButtonMessage;
    private string? _selectedSize;
    private WidgetPickerEntry? _selectedEntry;
    private WidgetPickerGroup? _selectedGroup;
    private bool _isFlipViewSyncing;
    private int _selectionGeneration;

    internal event EventHandler<WidgetAddRequestedEventArgs>? AddRequested;
    internal event EventHandler<WidgetPlacementCheckRequestedEventArgs>? PlacementCheckRequested;
    internal void RefreshPlacementCheck() => RaisePlacementCheckRequested();

    public WidgetPickerWindow(int MinWidth, int MinHeight, int MaxWidth, int MaxHeight)
    {
        InitializeComponent();
        WindowIconAssetResolver.SetWindowIcon(this, "AddWidget");
        _discovery = new WindowsWidgetDiscoveryService(_logger);
        _catalog = new WidgetCatalogService(_discovery);
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _discovery.CatalogChanged += Discovery_CatalogChanged;
        OverlappedPresenter presenter = OverlappedPresenter.Create();
        presenter.PreferredMinimumWidth = MinWidth;
        presenter.PreferredMinimumHeight = MinHeight;
        presenter.PreferredMaximumWidth = MaxWidth;
        presenter.PreferredMaximumHeight = MaxHeight;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsResizable = false;
        AppWindow.SetPresenter(presenter);
        Closed += Window_Closed;

        TitleBarArea.Window = this;
        TitleBarArea.Title = Package.Current.DisplayName;
        TitleBarArea.Subtitle = Get("WidgetPickerTitle");
        TitleBarArea.Icon = CreateWindowTitleIcon();
        WindowIconAssetResolver.AssignPackageApplicationIdentity(this, "AddWidget");
        AppWindow.Title = Get("WidgetPickerTitle");
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1200, 1200));

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
        _previewLoads.Clear();
        foreach (var navigationEntry in WidgetNavigation.MenuItems.OfType<NavigationViewItem>())
        {
            if (navigationEntry.Tag is WidgetPickerGroup group && group.Entries[0].NavigationIcon is { } icon)
                group.Entries[0].NavigationIconLoadTask = LoadWidgetIconAsync(group.Entries[0], icon, sender.ActualTheme);
        }

        if (_selectedEntry is not null)
        {
            if (_selectedGroup is not null)
            {
                foreach (var entry in _selectedGroup.Entries)
                    entry.PreviewSource = null;
                _ = LoadGroupPreviewsAsync(_selectedGroup, sender.ActualTheme);
            }
            _ = RenderSelectionAsync(_selectedEntry, FindGroup(_selectedEntry));
        }
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
        _addButtonMessage = Get("WidgetPickerLoading/Content");
        UpdateAddButtonState();
        try
        {
            var snapshot = await _catalog.RefreshAsync();
            var discoveredEntries = snapshot.Providers
                .SelectMany(provider => provider.Definitions.Select(definition => new WidgetPickerEntry(provider, definition)))
                .ToArray();
            var registrationById = discoveredEntries
                .GroupBy(entry => entry.Definition.DefinitionId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            var hostDefinitions = await LoadHostDefinitionsAsync();
            var availableEntries = new List<WidgetPickerEntry>(Math.Max(discoveredEntries.Length, hostDefinitions.Length));
            if (hostDefinitions.Length > 0)
            {
                var seenDefinitionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var hostDefinition in hostDefinitions)
                {
                    string definitionId;
                    try
                    {
                        definitionId = await Task.Run(() => hostDefinition.Id);
                    }
                    catch (Exception exception)
                    {
                        _logger.Write("Read WidgetHost definition ID", "Skipped", null, exception);
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(definitionId)
                        || IsEmptyPeregrineDefinition(definitionId)
                        || !seenDefinitionIds.Add(definitionId))
                    {
                        continue;
                    }

                    var entry = registrationById.TryGetValue(definitionId, out var registeredEntry)
                        ? registeredEntry
                        : CreateHostEntry(definitionId);
                    if (await HydrateEntryAsync(entry))
                        availableEntries.Add(entry);
                }
            }
            else
            {
                // Match WidgetsVsProbe: only use AppExtension registration when
                // WidgetHost returned no definitions after its bounded retries.
                foreach (var entry in discoveredEntries)
                {
                    if (IsEmptyPeregrineDefinition(entry.Definition.DefinitionId))
                        continue;

                    if (await HydrateEntryAsync(entry))
                    {
                        availableEntries.Add(entry);
                    }
                    else if (entry.Definition.SupportedSizes.Count > 0)
                    {
                        // WidgetsVsProbe's registration fallback trusts the
                        // declared sizes when WidgetHost returns no definitions.
                        entry.SupportedSizes = entry.Definition.SupportedSizes.ToArray();
                        availableEntries.Add(entry);
                    }
                }
            }

            var manifestGroupKeys = new Dictionary<WidgetProviderInfo, string>(ReferenceEqualityComparer.Instance);
            var groups = availableEntries
                .GroupBy(entry =>
                {
                    var runtimeProviderId = entry.HostDefinition?.ProviderDefinitionId;
                    if (!string.IsNullOrWhiteSpace(runtimeProviderId))
                        return $"host|{entry.Provider.PackageFamilyName}|{runtimeProviderId}";

                    if (!manifestGroupKeys.TryGetValue(entry.Provider, out var registrationKey))
                    {
                        registrationKey = $"manifest|{manifestGroupKeys.Count}";
                        manifestGroupKeys.Add(entry.Provider, registrationKey);
                    }

                    return registrationKey;
                }, StringComparer.OrdinalIgnoreCase)
                .Select(group => new WidgetPickerGroup(
                    group.First().Provider,
                    group.OrderBy(entry => entry.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray()))
                .OrderBy(group => group.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();

            WidgetNavigation.MenuItems.Clear();
            foreach (var group in groups)
            {
                WidgetNavigation.MenuItems.Add(new NavigationViewItem
                {
                    Content = BuildNavigationContent(group),
                    Tag = group,
                    SelectsOnInvoked = true
                });
            }

            if (groups.Length == 0)
            {
                WidgetNavigation.SelectedItem = null;
                ClearSelection();
                _addButtonMessage = snapshot.Diagnostics.Count > 0
                    ? Get("WidgetPickerCatalogUnavailable")
                    : Get("WidgetPickerEmptyCatalog");
                UpdateAddButtonState();
                return;
            }

            _addButtonMessage = null;
            _isUpdatingSelection = true;
            try { WidgetNavigation.SelectedItem = WidgetNavigation.MenuItems[0]; }
            finally { _isUpdatingSelection = false; }
            ShowGroup(groups[0]);
            await RenderSelectionAsync(groups[0].Entries[0], groups[0]);
        }
        catch (Exception exception)
        {
            _logger.Write("Load widget picker catalog", "Failed", null, exception);
            WidgetNavigation.MenuItems.Clear();
            ClearSelection();
            _addButtonMessage = Get("WidgetPickerCatalogUnavailable");
            UpdateAddButtonState();
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private async Task<Microsoft.Windows.Widgets.Hosts.WidgetDefinition[]> LoadHostDefinitionsAsync()
    {
        Microsoft.Windows.Widgets.Hosts.WidgetDefinition[] definitions = [];
        for (var attempt = 1; attempt <= 3 && definitions.Length == 0; attempt++)
        {
            definitions = await App.WidgetHostService.GetWidgetDefinitionsAsync() ?? [];
            if (definitions.Length > 0 || attempt == 3)
                break;

            try
            {
                var catalog = await App.WidgetHostService.GetWidgetCatalogAsync();
                if (catalog is not null)
                    definitions = await Task.Run(() => catalog.GetWidgetDefinitions()) ?? [];
            }
            catch (Exception exception)
            {
                _logger.Write("Retry WidgetHost catalog definitions", "Failed", null, exception);
            }

            if (definitions.Length == 0)
                await Task.Delay(TimeSpan.FromMilliseconds(attempt * 500));
        }

        return definitions;
    }

    private static WidgetPickerEntry CreateHostEntry(string definitionId)
    {
        var packageFamilyName = definitionId.Split('!', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        var definition = new WidgetDefinition(
            definitionId,
            definitionId,
            string.Empty,
            null,
            string.Empty,
            Array.Empty<string>(),
            Array.Empty<WidgetResourceInfo>(),
            null,
            null,
            null);
        var provider = new WidgetProviderInfo(
            string.Empty,
            packageFamilyName,
            packageFamilyName,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            Array.Empty<WidgetResourceInfo>(),
            new[] { definition },
            Array.Empty<string>());
        return new WidgetPickerEntry(provider, definition);
    }

    private StackPanel BuildNavigationContent(WidgetPickerGroup group)
    {
        var entry = group.Entries[0];
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
                    Text = group.DisplayName,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center
                }
            }
        };
    }

    private async Task<bool> HydrateEntryAsync(WidgetPickerEntry entry)
    {
        if (IsEmptyPeregrineDefinition(entry.Definition.DefinitionId))
            return false;

        try
        {
            var safeDefinition = new SafeWidgetDefinition(App.WidgetHostService, entry.Definition.DefinitionId);
            if (!await safeDefinition.PopulateAsync())
                return false;

            entry.HostDefinition = safeDefinition;
            var runtimeSizes = await safeDefinition.GetSupportedSizesAsync();
            if (runtimeSizes.Count == 0)
                return false;

            entry.SupportedSizes = runtimeSizes.Select(size => size.ToString()).ToArray();

            // Match the host selector in WidgetsVsProbe: use DisplayTitle when
            // present and otherwise keep the registered definition ID.
            var title = !string.IsNullOrWhiteSpace(safeDefinition.DisplayTitle)
                ? safeDefinition.DisplayTitle
                : safeDefinition.Id;

            entry.DisplayName = title;
            return true;
        }
        catch (Exception exception)
        {
            _logger.Write("Load widget display metadata", "Failed", entry.Definition.DefinitionId, exception);
            return false;
        }
    }

    private void WidgetNavigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_isUpdatingSelection || args.SelectedItem is not NavigationViewItem item || item.Tag is not WidgetPickerGroup group)
            return;

        ShowGroup(group);
        _ = RenderSelectionAsync(group.Entries[0], group);
    }

    private async Task RenderSelectionAsync(WidgetPickerEntry entry, WidgetPickerGroup? group = null)
    {
        var generation = ++_selectionGeneration;
        _isUpdatingSelection = true;
        try
        {
            _selectedEntry = entry;
            _selectedGroup = group ?? FindGroup(entry);
            WidgetHeaderOverlay.Visibility = Visibility.Visible;
            WidgetActionOverlay.Visibility = Visibility.Visible;
            _alreadyAdded = false;
            _desktopPlacementAvailable = true;
            _addButtonMessage = null;
            UpdatePagePager(_selectedGroup, entry);
            EnsureFlipViewSelection(_selectedGroup, entry);
            _selectedSize = entry.SelectedSize ?? GetDefaultSize(entry.SupportedSizes);
            entry.SelectedSize = _selectedSize;
            SinglePreviewImage.Source = entry.PreviewSource;
            SinglePreviewFallback.Visibility = entry.PreviewSource is null ? Visibility.Visible : Visibility.Collapsed;
            WidgetNameText.Text = entry.DisplayName;
            WidgetIconImage.Source = entry.HeaderIconSource;
            BuildSizeOptions(entry);
            UpdateAddButtonState();

            var theme = Content is FrameworkElement root ? root.ActualTheme : ElementTheme.Default;
            if (entry.NavigationIcon is not null)
            {
                if (entry.NavigationIconLoadTask is not null)
                    await entry.NavigationIconLoadTask;
                entry.HeaderIconSource = entry.NavigationIcon.Source;
                WidgetIconImage.Source = entry.HeaderIconSource;
            }
            else
            {
                var iconImage = new Image();
                await LoadWidgetIconAsync(entry, iconImage, theme);
                entry.HeaderIconSource = iconImage.Source;
                WidgetIconImage.Source = entry.HeaderIconSource;
            }

            if (generation != _selectionGeneration)
                return;

            var previewLoad = GetPreviewLoad(entry, theme);
            var previewSource = await previewLoad;
            if (generation != _selectionGeneration)
                return;

            entry.PreviewSource = previewSource;
            if (_selectedGroup?.Entries.Count == 1 && ReferenceEquals(_selectedGroup.Entries[0], entry))
            {
                SinglePreviewImage.Source = previewSource;
                SinglePreviewFallback.Visibility = previewSource is null
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
            RaisePlacementCheckRequested();
        }
        finally
        {
            if (generation == _selectionGeneration)
                _isUpdatingSelection = false;
        }
    }

    private static string? GetDefaultSize(IReadOnlyList<string> sizes) => sizes
        .Where(size => !string.IsNullOrWhiteSpace(size))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(size => size.Trim().ToLowerInvariant() switch
        {
            "medium" => 0,
            "large" => 1,
            "small" => 2,
            _ => 3
        })
        .FirstOrDefault();

    private void BuildSizeOptions(WidgetPickerEntry entry)
    {
        var sizes = entry.SupportedSizes
            .Where(size => !string.IsNullOrWhiteSpace(size))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(size => size.Trim().ToLowerInvariant() switch
            {
                "medium" => 0,
                "large" => 1,
                "small" => 2,
                _ => 3
            })
            .ToArray();

        SizeComboBox.Items.Clear();
        foreach (var size in sizes)
        {
            SizeComboBox.Items.Add(new ComboBoxItem
            {
                Content = size.Trim().ToLowerInvariant() switch
                {
                    "small" => Get("WidgetPickerSizeSmall"),
                    "medium" => Get("WidgetPickerSizeMedium"),
                    "large" => Get("WidgetPickerSizeLarge"),
                    _ => size
                },
                Tag = size
            });
        }

        SizeSelectorPanel.Visibility = sizes.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
        var selectedSize = sizes.FirstOrDefault(size =>
            string.Equals(size, entry.SelectedSize, StringComparison.OrdinalIgnoreCase)) ?? sizes.FirstOrDefault();
        _selectedSize = selectedSize;
        entry.SelectedSize = selectedSize;
        SizeComboBox.SelectedIndex = Array.FindIndex(sizes, size =>
            string.Equals(size, selectedSize, StringComparison.OrdinalIgnoreCase));
    }

    private void SizeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingSelection || SizeComboBox.SelectedItem is not ComboBoxItem { Tag: string size }
            || _selectedEntry is null)
        {
            return;
        }

        _selectedSize = size;
        _selectedEntry.SelectedSize = size;
        _addButtonMessage = null;
        UpdateAddButtonState();
        RaisePlacementCheckRequested();
    }

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedEntry is null || _selectedSize is null)
            return;

        _addButtonMessage = null;
        UpdateAddButtonState();
        AddRequested?.Invoke(this,
            new WidgetAddRequestedEventArgs(_selectedEntry.Provider, _selectedEntry.Definition, _selectedSize));
    }

    private WidgetPickerGroup? FindGroup(WidgetPickerEntry entry) =>
        WidgetNavigation.MenuItems
            .OfType<NavigationViewItem>()
            .Select(item => item.Tag as WidgetPickerGroup)
            .FirstOrDefault(group => group?.Entries.Contains(entry) == true);

    private void ShowGroup(WidgetPickerGroup group)
    {
        _selectedGroup = group;
        _selectedEntry = group.Entries[0];
        _isFlipViewSyncing = true;
        try
        {
            if (group.Entries.Count > 1)
            {
                SinglePreviewCard.Visibility = Visibility.Collapsed;
                WidgetFlipView.Visibility = Visibility.Visible;
                WidgetFlipView.ItemsSource = group.Entries;
                WidgetFlipView.SelectedIndex = 0;
                WidgetPagePager.NumberOfPages = group.Entries.Count;
                WidgetPagePager.SelectedPageIndex = 0;
                WidgetPagePager.Visibility = Visibility.Visible;
            }
            else
            {
                WidgetFlipView.ItemsSource = null;
                WidgetFlipView.Visibility = Visibility.Collapsed;
                SinglePreviewCard.Visibility = Visibility.Visible;
                SinglePreviewImage.Source = group.Entries[0].PreviewSource;
                SinglePreviewFallback.Visibility = group.Entries[0].PreviewSource is null
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                WidgetPagePager.NumberOfPages = 1;
                WidgetPagePager.SelectedPageIndex = 0;
                WidgetPagePager.Visibility = Visibility.Collapsed;
            }
        }
        finally
        {
            _isFlipViewSyncing = false;
        }

        _ = LoadGroupPreviewsAsync(
            group,
            Content is FrameworkElement root ? root.ActualTheme : ElementTheme.Default);
    }

    private Task<ImageSource?> GetPreviewLoad(WidgetPickerEntry entry, ElementTheme theme)
    {
        if (_previewLoads.TryGetValue(entry, out var previewLoad))
            return previewLoad;

        var screenshot = SelectThemedResource(entry.Definition.Resources.Where(IsScreenshot), theme);
        previewLoad = LoadPreviewImageAsync(screenshot);
        _previewLoads[entry] = previewLoad;
        return previewLoad;
    }

    private async Task LoadGroupPreviewsAsync(WidgetPickerGroup group, ElementTheme theme)
    {
        try
        {
            var entries = group.Entries.ToArray();
            var sources = await Task.WhenAll(entries.Select(async entry =>
            {
                try
                {
                    return await GetPreviewLoad(entry, theme);
                }
                catch (Exception exception)
                {
                    _logger.Write("Load widget group preview", "Failed", entry.Definition.DefinitionId, exception);
                    return null;
                }
            }));

            if (!ReferenceEquals(group, _selectedGroup)
                || Content is FrameworkElement root && root.ActualTheme != theme)
            {
                return;
            }

            for (var index = 0; index < entries.Length; index++)
            {
                entries[index].PreviewSource = sources[index];
            }
        }
        catch (Exception exception)
        {
            _logger.Write("Preload widget group previews", "Failed", group.DisplayName, exception);
        }
    }

    private void EnsureFlipViewSelection(WidgetPickerGroup? group, WidgetPickerEntry entry)
    {
        if (group is null || group.Entries.Count <= 1)
            return;

        var index = GetEntryIndex(group, entry);
        if (index < 0 || WidgetFlipView.SelectedIndex == index)
            return;

        _isFlipViewSyncing = true;
        try { WidgetFlipView.SelectedIndex = index; }
        finally { _isFlipViewSyncing = false; }
    }

    private static int GetEntryIndex(WidgetPickerGroup group, WidgetPickerEntry entry)
    {
        for (var index = 0; index < group.Entries.Count; index++)
        {
            if (ReferenceEquals(group.Entries[index], entry))
                return index;
        }

        return -1;
    }

    private void WidgetFlipView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isFlipViewSyncing || _selectedGroup is null || WidgetFlipView.SelectedIndex < 0
            || WidgetFlipView.SelectedIndex >= _selectedGroup.Entries.Count)
            return;

        var entry = _selectedGroup.Entries[WidgetFlipView.SelectedIndex];
        _isFlipViewSyncing = true;
        try { WidgetPagePager.SelectedPageIndex = WidgetFlipView.SelectedIndex; }
        finally { _isFlipViewSyncing = false; }
        _ = RenderSelectionAsync(entry, _selectedGroup);
    }

    private void UpdatePagePager(WidgetPickerGroup? group, WidgetPickerEntry selectedEntry)
    {
        if (group is null || group.Entries.Count <= 1)
        {
            WidgetPagePager.NumberOfPages = 1;
            WidgetPagePager.SelectedPageIndex = 0;
            WidgetPagePager.Visibility = Visibility.Collapsed;
            return;
        }

        WidgetPagePager.NumberOfPages = group.Entries.Count;
        var selectedIndex = 0;
        for (var index = 0; index < group.Entries.Count; index++)
        {
            if (ReferenceEquals(group.Entries[index], selectedEntry))
            {
                selectedIndex = index;
                break;
            }
        }

        WidgetPagePager.SelectedPageIndex = selectedIndex;
        WidgetPagePager.Visibility = Visibility.Visible;
        EnsureFlipViewSelection(group, selectedEntry);
    }

    private void WidgetPagePager_SelectedIndexChanged(PipsPager sender, PipsPagerSelectedIndexChangedEventArgs args)
    {
        if (_isFlipViewSyncing || _selectedGroup is null)
            return;

        var index = sender.SelectedPageIndex;
        if ((uint)index < (uint)_selectedGroup.Entries.Count)
        {
            _isFlipViewSyncing = true;
            try { WidgetFlipView.SelectedIndex = index; }
            finally { _isFlipViewSyncing = false; }
            _ = RenderSelectionAsync(_selectedGroup.Entries[index], _selectedGroup);
        }
    }

    private async Task<ImageSource?> LoadPreviewImageAsync(WidgetResourceInfo? resource)
    {
        if (string.IsNullOrWhiteSpace(resource?.ResolvedPath) || !File.Exists(resource.ResolvedPath))
            return null;

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(resource.ResolvedPath);
            using var stream = await file.OpenReadAsync();
            if (Path.GetExtension(resource.ResolvedPath).Equals(".svg", StringComparison.OrdinalIgnoreCase))
            {
                var svg = new SvgImageSource
                {
                    RasterizePixelWidth = 300,
                    RasterizePixelHeight = 462
                };
                if (await svg.SetSourceAsync(stream) != SvgImageSourceLoadStatus.Success)
                    return null;
                return svg;
            }
            else
            {
                var bitmap = new BitmapImage();
                await bitmap.SetSourceAsync(stream);
                return bitmap;
            }
        }
        catch (Exception exception)
        {
            _logger.Write("Load widget image resource", "Failed", resource.ResolvedPath, exception);
            return null;
        }
    }

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
                var bitmap = new BitmapImage { DecodePixelWidth = pixelWidth, DecodePixelHeight = pixelHeight };
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

        // Prefer the icon parsed directly from the provider's package manifest.
        // The experimental host projection may throw FileNotFoundException while
        // resolving ProviderDefinition.Icon even when the packaged resource is valid.
        var manifestIcon = SelectThemedResource(
            entry.Definition.Resources.Where(IsIcon).Concat(entry.Provider.ProviderIcons.Where(IsIcon)),
            theme);
        if (manifestIcon is not null)
        {
            await SetImageAsync(image, manifestIcon, 96, 96);
            return;
        }

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

    private static bool IsEmptyPeregrineDefinition(string definitionId) =>
        definitionId.Split('!', StringSplitOptions.RemoveEmptyEntries)
            .Any(part => string.Equals(part, "PeregrineWidgets", StringComparison.OrdinalIgnoreCase));

    private void ClearSelection()
    {
        _selectedSize = null;
        _selectedEntry = null;
        _selectedGroup = null;
        WidgetFlipView.ItemsSource = null;
        WidgetFlipView.Visibility = Visibility.Collapsed;
        SinglePreviewCard.Visibility = Visibility.Collapsed;
        WidgetHeaderOverlay.Visibility = Visibility.Collapsed;
        WidgetActionOverlay.Visibility = Visibility.Collapsed;
        WidgetNameText.Text = string.Empty;
        WidgetIconImage.Source = null;
        SizeComboBox.Items.Clear();
        SizeSelectorPanel.Visibility = Visibility.Collapsed;
        WidgetPagePager.NumberOfPages = 1;
        WidgetPagePager.SelectedPageIndex = 0;
        WidgetPagePager.Visibility = Visibility.Collapsed;
        UpdateAddButtonState();
    }

    private void RaisePlacementCheckRequested()
    {
        if (_selectedEntry is not null && _selectedSize is not null)
        {
            PlacementCheckRequested?.Invoke(this,
                new WidgetPlacementCheckRequestedEventArgs(_selectedEntry.Provider, _selectedEntry.Definition, _selectedSize));
        }
    }

    private void UpdateAddButtonState()
    {
        if (_selectedEntry is null)
            return;

        _selectedEntry.AddButtonText = GetCurrentAddButtonText();
        _selectedEntry.AddButtonEnabled = IsAddButtonEnabled();
        AddButtonText.Text = _selectedEntry.AddButtonText;
        AddButton.IsEnabled = _selectedEntry.AddButtonEnabled;
    }

    private string GetCurrentAddButtonText() => _isAddInProgress
        ? Get("WidgetPickerAdding")
        : _addButtonMessage is not null
            ? _addButtonMessage
            : _alreadyAdded
                ? Get("WidgetPickerAlreadyAdded")
                : !_desktopPlacementAvailable
                    ? Get("WidgetPickerDesktopFullHint/Text")
                    : Get("WidgetPickerAddLabel");

    private bool IsAddButtonEnabled() => _addButtonMessage is null && _desktopPlacementAvailable
        && !_alreadyAdded && !_isAddInProgress && _selectedSize is not null;

    /// <summary>Updates whether the desktop currently has room for another widget.</summary>
    public void SetDesktopPlacementAvailability(bool available, bool alreadyAdded = false)
    {
        _desktopPlacementAvailable = available;
        _alreadyAdded = alreadyAdded;
        UpdateAddButtonState();
    }

    /// <summary>Shows a busy state while the selected widget is being created.</summary>
    public void SetAddInProgress(bool inProgress)
    {
        _isAddInProgress = inProgress;
        UpdateAddButtonState();
    }

    /// <summary>Closes the picker after the desktop has accepted a widget.</summary>
    public void CloseAfterAdd()
    {
        try { Close(); } catch { }
    }

    public void ShowAddFailure()
    {
        _addButtonMessage = Get("WidgetPickerAddFailed");
        UpdateAddButtonState();
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

}

internal sealed class WidgetPickerEntry(WidgetProviderInfo provider, WidgetDefinition definition) : INotifyPropertyChanged
{
    public WidgetProviderInfo Provider { get; } = provider;
    public WidgetDefinition Definition { get; } = definition;
    public string DisplayName { get; set; } = string.IsNullOrWhiteSpace(definition.DisplayName) ? definition.DefinitionId : definition.DisplayName;
    public string ProviderName => string.IsNullOrWhiteSpace(Provider.AppName) ? Provider.PackageName : Provider.AppName;
    public SafeWidgetDefinition? HostDefinition { get; set; }
    public string[] SupportedSizes { get; set; } = [];
    private ImageSource? _previewSource;
    private ImageSource? _headerIconSource;
    private string? _selectedSize;
    private string _addButtonText = string.Empty;
    private bool _addButtonEnabled;

    public ImageSource? PreviewSource
    {
        get => _previewSource;
        set => SetProperty(ref _previewSource, value, nameof(PreviewSource));
    }

    public ImageSource? HeaderIconSource
    {
        get => _headerIconSource;
        set => SetProperty(ref _headerIconSource, value, nameof(HeaderIconSource));
    }

    public string? SelectedSize
    {
        get => _selectedSize;
        set => SetProperty(ref _selectedSize, value, nameof(SelectedSize));
    }

    public string AddButtonText
    {
        get => _addButtonText;
        set => SetProperty(ref _addButtonText, value, nameof(AddButtonText));
    }

    public bool AddButtonEnabled
    {
        get => _addButtonEnabled;
        set => SetProperty(ref _addButtonEnabled, value, nameof(AddButtonEnabled));
    }

    public Image? NavigationIcon { get; set; }
    public Task? NavigationIconLoadTask { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetProperty<T>(ref T field, T value, string propertyName)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

internal sealed class WidgetPickerGroup(WidgetProviderInfo provider, IReadOnlyList<WidgetPickerEntry> entries)
{
    public WidgetProviderInfo Provider { get; } = provider;
    public IReadOnlyList<WidgetPickerEntry> Entries { get; } = entries;
    public string DisplayName
    {
        get
        {
            if (Entries.Count == 1)
                return Entries[0].DisplayName;

            var runtimeProviderName = Entries.FirstOrDefault()?.HostDefinition?.ProviderDefinitionDisplayName;
            if (!string.IsNullOrWhiteSpace(runtimeProviderName))
                return runtimeProviderName;
            if (!string.IsNullOrWhiteSpace(Provider.ExtensionDisplayName))
                return Provider.ExtensionDisplayName;
            if (!string.IsNullOrWhiteSpace(Provider.AppName))
                return Provider.AppName;

            return Provider.PackageName;
        }
    }
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

