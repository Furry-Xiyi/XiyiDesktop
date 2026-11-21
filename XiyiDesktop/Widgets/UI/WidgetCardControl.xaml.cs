using AdaptiveCards.ObjectModel.WinUI3;
using AdaptiveCards.Rendering.WinUI3;
using AdaptiveCards.Templating;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Documents;
using Microsoft.Windows.ApplicationModel.Resources;
using Microsoft.Windows.Widgets;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI.ViewManagement;
using XiyiDesktop.Widgets.Hosting;
using CommunityToolkit.WinUI;
using System.Diagnostics;

namespace XiyiDesktop.Widgets.UI;

/// <summary>A live Windows Widget surface with provider updates, size muxc, and Adaptive Cards rendering.</summary>
public sealed partial class WidgetCardControl : UserControl
{
    private static readonly HttpClient RemoteImageClient = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly AdaptiveElementParserRegistration _elementParser = new();
    private readonly AdaptiveActionParserRegistration _actionParser = new();
    private readonly SafeWidgetInstance _instance;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly SemaphoreSlim _renderGate = new(1, 1);
    private readonly UISettings _uiSettings = new();
    private readonly IReadOnlyList<WidgetSize> _supportedSizes;
    private readonly string _defaultTitle;
    private RenderedAdaptiveCard? _renderedCard;
    private bool _disposed;
    private int _widgetUpdatePending;
    private int _widgetUpdateScheduled;
    private WidgetSize _size;
    private string? _activeWebContentUrl;
    private CancellationTokenSource? _pollCancellation;
    private string? _lastRenderedTemplate;
    private string? _lastRenderedData;
    private bool _providerHeaderVisible = true;
    private bool _smtcCircularActionButtons;
    private bool _fullBleedBackgroundActive;
    private string? _providerIconPath;
    private double _textScaleFactor = 1.0;
    private double _desktopScaleX = 1.0;
    private double _desktopScaleY = 1.0;

    internal WidgetCardControl(
        SafeWidgetInstance instance,
        string title,
        WidgetSize size = WidgetSize.Medium,
        IReadOnlyList<WidgetSize>? supportedSizes = null)
    {
        InitializeComponent();
        UpdateCornerRadius(XiyiDesktop.AppSettingsStore.DesktopCornerRadius);
        UpdateBackgroundMaterial(AppThemeManager.CurrentMaterial);
        _instance = instance ?? throw new ArgumentNullException(nameof(instance));
        _size = size;
        _supportedSizes = supportedSizes is { Count: > 0 }
            ? supportedSizes
            : new[] { WidgetSize.Small, WidgetSize.Medium, WidgetSize.Large };
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _defaultTitle = string.IsNullOrWhiteSpace(title) ? instance.DefinitionId : title;
        _textScaleFactor = _uiSettings.TextScaleFactor;
        TitleText.Text = _defaultTitle;
        _instance.WidgetUpdated += Instance_WidgetUpdated;
        ActualThemeChanged += WidgetCardControl_ActualThemeChanged;
        _uiSettings.TextScaleFactorChanged += UiSettings_TextScaleFactorChanged;
        SetSizeLayout(size);
    }

    /// <summary>Header surface to which the desktop attaches its position drag behavior.</summary>
    public FrameworkElement DragHandle => HeaderGrid;

    internal void UpdateBackgroundMaterial(XiyiDesktop.BackgroundMaterial material)
    {
        CardBackdrop.SystemBackdrop = material switch
        {
            XiyiDesktop.BackgroundMaterial.MicaAlt => new MicaBackdrop { Kind = MicaKind.BaseAlt },
            XiyiDesktop.BackgroundMaterial.Acrylic => new DesktopAcrylicBackdrop(),
            _ => new MicaBackdrop { Kind = MicaKind.Base }
        };
    }

    internal void UpdateCornerRadius(double radius)
    {
        radius = double.IsFinite(radius) ? Math.Clamp(radius, 0, 32) : 8;
        var cornerRadius = new CornerRadius(radius);
        CardBackdrop.CornerRadius = cornerRadius;
        CardBorder.CornerRadius = cornerRadius;
    }

    internal Uri? ActiveWebContentUri => Uri.TryCreate(_activeWebContentUrl, UriKind.Absolute, out var uri) ? uri : null;
    internal SafeWidgetInstance Instance => _instance;
    public WidgetSize Size => _size;
    internal double ScaledWidth => GetLayoutWidth() * _desktopScaleX;
    internal double ScaledHeight => GetLayoutHeight() * _desktopScaleY;
    internal double DesktopScale => Math.Min(_desktopScaleX, _desktopScaleY);
    internal static int GetGridColumnSpan(WidgetSize size) => 3;
    internal static int GetGridRowSpan(WidgetSize size) => size switch
    {
        WidgetSize.Small => 2,
        WidgetSize.Medium => 3,
        WidgetSize.Large => 4,
        _ => 3
    };
    public event Action<WidgetSize>? SizeChangedByUser;
    public event EventHandler? DeleteRequested;
    public event Action<Uri?>? WebWidgetChanged;

    internal void SetDesktopScale(double scale)
        => SetDesktopScale(scale, scale);

    private void SetDesktopScale(double scaleX, double scaleY)
    {
        if (!double.IsFinite(scaleX) || scaleX <= 0 || !double.IsFinite(scaleY) || scaleY <= 0)
            return;

        _desktopScaleX = scaleX;
        _desktopScaleY = scaleY;
    }

    internal void FitDesktopScaleToGrid(double cellWidth, double cellHeight, Thickness contentInsets)
    {
        var occupiedWidth = GetGridColumnSpan(_size) * cellWidth;
        var occupiedHeight = GetGridRowSpan(_size) * cellHeight;
        var targetWidth = Math.Max(1, occupiedWidth - contentInsets.Left - contentInsets.Right);
        var targetHeight = Math.Max(1, occupiedHeight - contentInsets.Top - contentInsets.Bottom);
        var scale = Math.Min(targetWidth / GetLayoutWidth(), targetHeight / GetLayoutHeight());
        SetDesktopScale(scale);
    }

    internal static Windows.Foundation.Size GetGridOccupancy(WidgetSize size, double cellWidth, double cellHeight) =>
        new(GetGridColumnSpan(size) * cellWidth, GetGridRowSpan(size) * cellHeight);

    private double GetLayoutWidth() => double.IsFinite(Width) && Width > 0 ? Width : Math.Max(1, ActualWidth);
    private double GetLayoutHeight() => double.IsFinite(Height) && Height > 0 ? Height : Math.Max(1, ActualHeight);

    public async Task SetProviderIconAsync(string? path)
    {
        _providerIconPath = path;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            ProviderIcon.Source = null;
            ProviderIcon.Visibility = Visibility.Collapsed;
            ProviderGlyph.Visibility = Visibility.Visible;
            return;
        }

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenReadAsync();
            if (Path.GetExtension(path).Equals(".svg", StringComparison.OrdinalIgnoreCase))
            {
                var svg = new SvgImageSource { RasterizePixelWidth = 64, RasterizePixelHeight = 64 };
                if (await svg.SetSourceAsync(stream) != SvgImageSourceLoadStatus.Success)
                    return;
                ProviderIcon.Source = svg;
            }
            else
            {
                var bitmap = new BitmapImage { DecodePixelWidth = 64, DecodePixelHeight = 64 };
                await bitmap.SetSourceAsync(stream);
                ProviderIcon.Source = bitmap;
            }

            ProviderIcon.Visibility = Visibility.Visible;
            ProviderGlyph.Visibility = Visibility.Collapsed;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Could not load widget provider icon: {exception}");
        }
    }

    public async Task LoadAsync()
    {
        if (_disposed)
            return;

        ShowLoadingStatus();
        try
        {
            if (!await _instance.PopulateAsync())
                throw new InvalidOperationException(Get("WidgetsCardUnavailable"));
            await RenderWithReadinessRetriesAsync();
            StartPeriodicRefresh();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Widget card loading failed: {exception}");
            ShowStatus(false, Get("WidgetsCardUnavailable"));
        }
    }

    internal void ShowLoadingStatus()
    {
        if (!_disposed)
            ShowStatus(true, Get("WidgetsCardLoading"));
    }

    public async Task<bool> ChangeSizeAsync(WidgetSize size)
    {
        if (_disposed || size == _size)
            return true;

        if (!await _instance.SetSizeAsync(size))
            return false;

        _size = size;
        SetSizeLayout(size);
        SizeChangedByUser?.Invoke(size);
        await RenderWithReadinessRetriesAsync(force: true);
        return true;
    }

    internal void DetachWithoutDelete()
    {
        if (_disposed)
            return;
        _disposed = true;
        _pollCancellation?.Cancel();
        _pollCancellation?.Dispose();
        _pollCancellation = null;
        _instance.WidgetUpdated -= Instance_WidgetUpdated;
        ActualThemeChanged -= WidgetCardControl_ActualThemeChanged;
        _uiSettings.TextScaleFactorChanged -= UiSettings_TextScaleFactorChanged;
        ReleaseRenderedCard();
        WebWidgetChanged?.Invoke(null);
    }
    public async Task DeleteAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        _pollCancellation?.Cancel();
        _pollCancellation?.Dispose();
        _pollCancellation = null;
        _instance.WidgetUpdated -= Instance_WidgetUpdated;
        ActualThemeChanged -= WidgetCardControl_ActualThemeChanged;
        _uiSettings.TextScaleFactorChanged -= UiSettings_TextScaleFactorChanged;
        ReleaseRenderedCard();
        WebWidgetChanged?.Invoke(null);
        await _instance.DeleteAsync();
    }

    private void ReleaseRenderedCard()
    {
        if (_renderedCard is not null)
        {
            _renderedCard.Action -= Rendered_Action;
            _renderedCard = null;
        }

        CardContent.Content = null;
    }

    private void Instance_WidgetUpdated(SafeWidgetInstance sender, Microsoft.Windows.Widgets.Hosts.WidgetUpdatedEventArgs args)
    {
        if (_disposed)
            return;

        Interlocked.Exchange(ref _widgetUpdatePending, 1);
        ScheduleWidgetUpdateRender();
    }

    private void ScheduleWidgetUpdateRender()
    {
        if (_disposed || Interlocked.CompareExchange(ref _widgetUpdateScheduled, 1, 0) != 0)
            return;

        if (!_dispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                Interlocked.Exchange(ref _widgetUpdatePending, 0);
                if (!_disposed)
                    await RenderWithReadinessRetriesAsync();
            }
            finally
            {
                Interlocked.Exchange(ref _widgetUpdateScheduled, 0);
                if (!_disposed && Volatile.Read(ref _widgetUpdatePending) != 0)
                    ScheduleWidgetUpdateRender();
            }
        }))
        {
            Interlocked.Exchange(ref _widgetUpdateScheduled, 0);
        }
    }

    private async void WidgetCardControl_ActualThemeChanged(FrameworkElement sender, object args)
    {
        if (!_disposed)
            await RenderWithReadinessRetriesAsync(force: true);
    }

    private void UiSettings_TextScaleFactorChanged(UISettings sender, object args)
    {
        if (!_dispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed)
                return;

            _textScaleFactor = sender.TextScaleFactor;
            SetSizeLayout(_size);
            _ = ReflowLastRenderAsync();
        }))
        {
            System.Diagnostics.Debug.WriteLine("Could not apply the updated system text scale to the widget card.");
        }
    }

    private async Task ReflowLastRenderAsync()
    {
        await _renderGate.WaitAsync();
        try
        {
            if (!_disposed && _lastRenderedTemplate is not null && _lastRenderedData is not null)
                await RenderPayloadAsync(_lastRenderedTemplate, _lastRenderedData);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Could not reflow the widget after a system text-scale change: {exception}");
        }
        finally
        {
            _renderGate.Release();
        }
    }

    private void MenuButton_Click(object sender, RoutedEventArgs e)
    {
        var flyout = new MenuFlyout();
        if (_supportedSizes.Contains(WidgetSize.Small))
            AddSizeItem(flyout, WidgetSize.Small, Get("WidgetsSizeSmall"));
        if (_supportedSizes.Contains(WidgetSize.Medium))
            AddSizeItem(flyout, WidgetSize.Medium, Get("WidgetsSizeMedium"));
        if (_supportedSizes.Contains(WidgetSize.Large))
            AddSizeItem(flyout, WidgetSize.Large, Get("WidgetsSizeLarge"));
        flyout.Items.Add(new MenuFlyoutSeparator());
        var customize = new MenuFlyoutItem { Text = Get("WidgetsCustomize") };
        customize.Click += async (_, _) => await _instance.NotifyCustomizationRequestedAsync();
        flyout.Items.Add(customize);
        var delete = new MenuFlyoutItem { Text = Get("WidgetsDelete") };
        delete.Click += (_, _) => DeleteRequested?.Invoke(this, EventArgs.Empty);
        flyout.Items.Add(delete);
        flyout.ShowAt(MenuButton);
    }

    private void AddSizeItem(MenuFlyout flyout, WidgetSize size, string label)
    {
        var item = new RadioMenuFlyoutItem
        {
            Text = label,
            Tag = size,
            GroupName = "WidgetCardSize",
            IsChecked = size == _size
        };
        item.Click += async (_, _) => await ChangeSizeAsync(size);
        flyout.Items.Add(item);
    }

    private async Task RenderWithReadinessRetriesAsync(bool force = false)
    {
        if (_disposed)
            return;
        await _renderGate.WaitAsync();

        if (force || _lastRenderedTemplate is null)
            ShowStatus(true, Get("WidgetsCardLoading"));
        try
        {
            string? previousTemplate = null;
            string? previousData = null;
            for (var attempt = 1; attempt <= 5; attempt++)
            {
                var template = await _instance.GetCardTemplateAsync();
                var data = await _instance.GetCardDataAsync();
                var templateReady = !string.IsNullOrWhiteSpace(template) && template != "{}";
                var dataReady = IsJsonObject(data);
                if (!force && dataReady && string.Equals(template, _lastRenderedTemplate, StringComparison.Ordinal) &&
                    string.Equals(data, _lastRenderedData, StringComparison.Ordinal))
                {
                    ShowStatus(false, string.Empty);
                    return;
                }
                var stableEmptyData = attempt > 1 &&
                    string.Equals(template, previousTemplate, StringComparison.Ordinal) &&
                    string.Equals(data, previousData, StringComparison.Ordinal);

                if (templateReady && dataReady && (!IsEmptyObject(data) || stableEmptyData))
                {
                    await RenderPayloadAsync(template, data);
                    return;
                }

                previousTemplate = template;
                previousData = data;
                if (attempt < 5)
                    await Task.Delay(TimeSpan.FromSeconds(attempt switch { 1 => 2, 2 => 4, 3 => 8, _ => 12 }));
            }

            ShowStatus(false, Get("WidgetsCardUnavailable"));
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Widget card update failed: {exception}");
            ShowStatus(false, Get("WidgetsCardUnavailable"));
        }
        finally
        {
            _renderGate.Release();
        }
    }

    private async Task RenderPayloadAsync(string template, string data, bool allowWebContent = true)
    {
        UpdateLayout();
        var hostData = CreateWidgetHostContext(_size, ActualTheme == ElementTheme.Dark);
        var expanded = ExpandTemplateSafely(template, data, hostData);
        if (!IsExpandedAdaptiveCardPayload(expanded))
        {
            System.Diagnostics.Debug.WriteLine("Widget template expansion did not produce an AdaptiveCard payload; keeping the previous card.");
            ShowStatus(false, _lastRenderedTemplate is null ? Get("WidgetsCardUnavailable") : string.Empty);
            return;
        }

        await ApplyProviderHeaderAsync(expanded, data);
        expanded = RemovePromotedTrackInfoFromBody(expanded);
        // Header visibility and custom text affect its measured overlay height.
        // Measure after applying the expanded Provider header, as the probe does.
        HeaderGrid.UpdateLayout();
        ContentGrid.UpdateLayout();

        var webUrl = allowWebContent ? TryGetWebContentUrl(expanded) ?? TryGetWebContentUrl(data) : null;
        if (webUrl is not null)
        {
            try
            {
                await ShowWebContentAsync(webUrl);
                _lastRenderedTemplate = template;
                _lastRenderedData = data;
                ShowStatus(false, string.Empty);
                return;
            }
            catch (Exception exception)
            {
                // WebView2 can fail to initialize in a hosted desktop surface.
                // Match the probe's fallback: strip webUrl and render the card body.
                System.Diagnostics.Debug.WriteLine($"Web widget surface failed; rendering its Adaptive Card fallback: {exception}");
                HideWebContent();
                expanded = RemoveWebWidgetUrl(expanded);
            }
        }
        HideWebContent();

        var hostConfigName = ActualTheme == ElementTheme.Dark ? "HostConfigDark.json" : "HostConfigLight.json";
        var hostConfigPath = Path.Combine(AppContext.BaseDirectory, "Widgets", hostConfigName);
        var hostConfigJson = ApplySystemAccent(await File.ReadAllTextAsync(hostConfigPath));
        var bodyWidth = Math.Max(1, ContentGrid.ActualWidth > 0 ? ContentGrid.ActualWidth : Width);
        var fullCardHeight = Math.Max(1, ContentGrid.ActualHeight > 0 ? Math.Ceiling(ContentGrid.ActualHeight) : Height - 2);
        var measuredHeaderHeight = Math.Max(HeaderGrid.ActualHeight, HeaderGrid.DesiredSize.Height);
        var headerAvoidanceHeight = _providerHeaderVisible
            ? Math.Ceiling(measuredHeaderHeight > 0 ? measuredHeaderHeight : 38)
            : 0;
        var availableBodyHeight = Math.Max(1, fullCardHeight - headerAvoidanceHeight);
        _fullBleedBackgroundActive = await ApplyRootBackgroundAsync(expanded, hostConfigJson);
        var rendererSource = _fullBleedBackgroundActive ? RemovePromotedSurfaceBackground(expanded) : expanded;

        var geometryScale = 1.0;
        var typographyScale = 1.0;
        double naturalHeight = double.NaN;
        if (!_smtcCircularActionButtons)
        {
            for (var attempt = 0; attempt < 9; attempt++)
            {
                var candidateCard = ScaleAdaptiveCardDensity(rendererSource, geometryScale, typographyScale);
                var candidateHost = ScaleAdaptiveHostConfig(hostConfigJson, geometryScale, typographyScale);
                naturalHeight = MeasureAdaptiveCardNaturalHeight(candidateCard, candidateHost, bodyWidth);
                if (!double.IsFinite(naturalHeight) || naturalHeight <= 0 || naturalHeight <= availableBodyHeight * 0.98)
                    break;

                var shrink = Math.Clamp((availableBodyHeight / naturalHeight) * 0.94, 0.65, 0.9);
                if (geometryScale > 0.5001)
                    geometryScale = Math.Max(0.5, geometryScale * shrink);
                else if (typographyScale > 0.7001)
                    typographyScale = Math.Max(0.7, typographyScale * shrink);
                else
                    break;
            }
        }

        var finalCard = _smtcCircularActionButtons
            ? rendererSource
            : ScaleAdaptiveCardDensity(rendererSource, geometryScale, typographyScale);
        var finalHost = _smtcCircularActionButtons
            ? hostConfigJson
            : ScaleAdaptiveHostConfig(hostConfigJson, geometryScale, typographyScale);
        if (!_smtcCircularActionButtons && double.IsFinite(naturalHeight))
            naturalHeight = MeasureAdaptiveCardNaturalHeight(finalCard, finalHost, bodyWidth);
        var cardTopPadding = GetAdaptiveCardTopPadding(finalHost);
        finalCard = ApplyFloatingHeaderAvoidance(finalCard, headerAvoidanceHeight, cardTopPadding);
        var parsed = AdaptiveCard.FromJsonString(finalCard, _elementParser, _actionParser);
        if (parsed?.AdaptiveCard is null)
        {
            System.Diagnostics.Debug.WriteLine("Adaptive Card parsing failed; showing the unavailable state instead of throwing.");
            ShowStatus(false, Get("WidgetsCardUnavailable"));
            return;
        }

        var renderer = CreateAdaptiveCardRenderer(finalHost);
        renderer.SetFixedDimensions(checked((uint)Math.Ceiling(bodyWidth)), checked((uint)Math.Ceiling(fullCardHeight)));
        var rendered = renderer.RenderAdaptiveCard(parsed.AdaptiveCard);
        if (rendered?.FrameworkElement is null)
        {
            System.Diagnostics.Debug.WriteLine("Adaptive Card rendering produced no visual element.");
            ShowStatus(false, Get("WidgetsCardUnavailable"));
            return;
        }
        if (_renderedCard is not null)
            _renderedCard.Action -= Rendered_Action;
        _renderedCard = rendered;
        rendered.Action += Rendered_Action;
        CardContent.Margin = new Thickness(0);
        CardContent.Content = rendered.FrameworkElement;
        CardContent.UpdateLayout();
        ContentGrid.UpdateLayout();
        rendered.FrameworkElement.Width = double.NaN;
        rendered.FrameworkElement.Height = double.NaN;
        rendered.FrameworkElement.HorizontalAlignment = HorizontalAlignment.Stretch;
        rendered.FrameworkElement.UpdateLayout();
        ContentGrid.UpdateLayout();
        // Apply accent after attaching and measuring: provider-supplied native
        // WinUI muxc may resolve their theme brushes only in the live tree.
        ApplySystemAccentTextBrush(rendered.FrameworkElement);
        UpdateRendererTruncationState(
            rendered,
            naturalHeight + headerAvoidanceHeight,
            fullCardHeight);
        _lastRenderedTemplate = template;
        _lastRenderedData = data;
        ShowStatus(false, string.Empty);
    }

    private async Task ShowWebContentAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
            throw new InvalidDataException("Widget webUrl must use HTTP or HTTPS.");

        CardBackdrop.Visibility = Visibility.Collapsed;

        if (string.Equals(_activeWebContentUrl, uri.AbsoluteUri, StringComparison.Ordinal))
        {
            CardContent.Visibility = Visibility.Collapsed;
            WebWidgetHost.Visibility = Visibility.Visible;
            return;
        }

        // This is a layered Explorer child HWND. WebView2 uses its own native
        // composition controller, so DesktopWindow hosts this URL in a separate
        // regular top-level WebView2 window aligned with the widget card.
        WebWidgetHost.Visibility = Visibility.Visible;
        CardContent.Visibility = Visibility.Collapsed;
        _activeWebContentUrl = uri.AbsoluteUri;
        WebWidgetChanged?.Invoke(uri);
        await Task.CompletedTask;
    }

    private void HideWebContent()
    {
        CardBackdrop.Visibility = Visibility.Visible;
        if (_activeWebContentUrl is null)
            return;
        _activeWebContentUrl = null;
        WebWidgetChanged?.Invoke(null);
        CardContent.Visibility = Visibility.Visible;
    }

    private static string RemoveWebWidgetUrl(string cardJson)
    {
        try
        {
            if (JsonNode.Parse(cardJson) is JsonObject card && card["metadata"] is JsonObject metadata)
            {
                metadata.Remove("webUrl");
                return card.ToJsonString();
            }
        }
        catch (JsonException)
        {
        }

        return cardJson;
    }
    private async void Rendered_Action(RenderedAdaptiveCard sender, AdaptiveActionEventArgs args)
    {
        try
        {
            var actionType = args.Action.ActionTypeString;
            System.Diagnostics.Debug.WriteLine($"Widget action clicked: type={actionType}, instance={_instance.Id}.");
            var actionJson = args.Action.ToJson().Stringify();
            using var actionDocument = JsonDocument.Parse(actionJson);
            if (string.Equals(actionType, "Action.OpenUrl", StringComparison.OrdinalIgnoreCase) &&
                actionDocument.RootElement.TryGetProperty("url", out var urlElement) &&
                Uri.TryCreate(urlElement.GetString(), UriKind.Absolute, out var url))
            {
                var launched = await Windows.System.Launcher.LaunchUriAsync(url);
                System.Diagnostics.Debug.WriteLine(launched
                    ? $"Widget OpenUrl launched: {url}."
                    : $"Widget OpenUrl was not handled: {url}.");
                return;
            }

            if (string.Equals(actionType, "Action.Execute", StringComparison.OrdinalIgnoreCase))
            {
                var verb = actionDocument.RootElement.TryGetProperty("verb", out var verbElement)
                    ? verbElement.GetString() ?? string.Empty
                    : string.Empty;
                var notified = await _instance.NotifyActionInvokedAsync(verb,
                    MergeActionData(GetPropertyText(actionDocument.RootElement, "data"), args.Inputs.AsJson().Stringify()));
                System.Diagnostics.Debug.WriteLine(notified
                    ? $"Widget Execute action sent to provider: verb={verb}."
                    : $"Widget Execute action could not reach provider: verb={verb}.");
                return;
            }

            if (string.Equals(actionType, "Action.Submit", StringComparison.OrdinalIgnoreCase))
            {
                var notified = await _instance.NotifyActionInvokedAsync(string.Empty,
                    MergeActionData(GetPropertyText(actionDocument.RootElement, "data"), args.Inputs.AsJson().Stringify()));
                System.Diagnostics.Debug.WriteLine(notified
                    ? "Widget Submit action sent to provider."
                    : "Widget Submit action could not reach provider.");
                return;
            }

            System.Diagnostics.Debug.WriteLine($"Widget action was not forwarded: type={actionType}.");
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Widget action forwarding failed: {exception}");
        }
    }

    private void StartPeriodicRefresh()
    {
        _pollCancellation?.Cancel();
        _pollCancellation?.Dispose();
        _pollCancellation = new CancellationTokenSource();
        _ = PollWidgetAsync(_pollCancellation.Token);
    }

    private async Task PollWidgetAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (_disposed || !await _renderGate.WaitAsync(0, cancellationToken))
                    continue;

                try
                {
                    var template = await _instance.GetCardTemplateAsync();
                    var data = await _instance.GetCardDataAsync();
                    if (string.IsNullOrWhiteSpace(template) || template == "{}" || !IsJsonObject(data))
                        continue;

                    if (string.Equals(template, _lastRenderedTemplate, StringComparison.Ordinal) &&
                        string.Equals(data, _lastRenderedData, StringComparison.Ordinal))
                        continue;

                    await EnqueueOnUiAsync(async () =>
                    {
                        if (_disposed)
                            return;
                        await RenderPayloadAsync(template, data);
                    });
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    System.Diagnostics.Debug.WriteLine($"Widget periodic refresh failed: {exception}");
                }
                finally
                {
                    _renderGate.Release();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private Task EnqueueOnUiAsync(Func<Task> action)
    {
        if (_dispatcherQueue.HasThreadAccess)
            return action();

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                await action();
                completion.SetResult();
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        }))
        {
            completion.SetException(new InvalidOperationException("The widget UI dispatcher is unavailable."));
        }
        return completion.Task;
    }

    private async Task ApplyProviderHeaderAsync(string expandedJson, string dataJson)
    {
        _smtcCircularActionButtons = false;
        try
        {
            using var document = JsonDocument.Parse(expandedJson);
            _providerHeaderVisible = !IsProviderHeaderHidden(document.RootElement);
            if (!_providerHeaderVisible)
            {
                HeaderGrid.Visibility = Visibility.Visible;
                HeaderGrid.Height = double.NaN;
                HeaderRow.Height = GridLength.Auto;
                ProviderIconContainer.Visibility = Visibility.Collapsed;
                TitleText.Visibility = Visibility.Collapsed;
                MenuButton.Visibility = Visibility.Visible;
                return;
            }

            if (!document.RootElement.TryGetProperty("header", out var header))
            {
                HeaderGrid.Visibility = Visibility.Visible;
                HeaderGrid.Height = double.NaN;
                HeaderRow.Height = GridLength.Auto;
                TitleText.Text = _defaultTitle;
                ProviderIconContainer.Visibility = Visibility.Visible;
                TitleText.Visibility = Visibility.Visible;
                MenuButton.Visibility = Visibility.Visible;
                await SetProviderIconAsync(_providerIconPath);
                return;
            }

            if (header.ValueKind == JsonValueKind.Null)
            {
                HeaderGrid.Visibility = Visibility.Visible;
                HeaderGrid.Height = double.NaN;
                HeaderRow.Height = GridLength.Auto;
                ProviderIconContainer.Visibility = Visibility.Collapsed;
                TitleText.Visibility = Visibility.Collapsed;
                MenuButton.Visibility = Visibility.Visible;
                return;
            }

            HeaderGrid.Visibility = Visibility.Visible;
            HeaderGrid.Height = double.NaN;
            HeaderRow.Height = GridLength.Auto;
            ProviderIconContainer.Visibility = Visibility.Visible;
            TitleText.Visibility = Visibility.Visible;
            MenuButton.Visibility = Visibility.Visible;
            TitleText.Text = _defaultTitle;
            if (header.ValueKind == JsonValueKind.String)
                TitleText.Text = header.GetString() ?? TitleText.Text;
            else if (header.ValueKind == JsonValueKind.Object)
            {
                if (header.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                    TitleText.Text = text.GetString() ?? TitleText.Text;
                _smtcCircularActionButtons = header.TryGetProperty("smtcCircularActions", out var circularActions) &&
                    circularActions.ValueKind == JsonValueKind.True;
            }

            var iconUrl = TryGetHeaderProperty(expandedJson, "iconUrl") ?? TryGetStringProperty(dataJson, "imageUrl");
            if (string.IsNullOrWhiteSpace(iconUrl) || !await SetProviderIconFromUriAsync(iconUrl))
                await SetProviderIconAsync(_providerIconPath);
        }
        catch (JsonException)
        {
            HeaderGrid.Visibility = Visibility.Visible;
            HeaderGrid.Height = double.NaN;
            HeaderRow.Height = GridLength.Auto;
            ProviderIconContainer.Visibility = Visibility.Visible;
            TitleText.Visibility = Visibility.Visible;
            MenuButton.Visibility = Visibility.Visible;
            TitleText.Text = _defaultTitle;
        }
    }

    private static string RemovePromotedTrackInfoFromBody(string cardJson)
    {
        try
        {
            if (JsonNode.Parse(cardJson) is not JsonObject card ||
                card["header"] is not JsonObject header ||
                header["moveTitleToHeader"]?.GetValueKind() != JsonValueKind.True ||
                header["promotedBodyTextBlocks"]?.GetValueKind() != JsonValueKind.Number ||
                card["body"] is not JsonArray body ||
                body.FirstOrDefault() is not JsonObject container ||
                container["items"] is not JsonArray items)
            {
                return cardJson;
            }

            var promotedTextBlockCount = header["promotedBodyTextBlocks"]!.GetValue<int>();
            for (var index = 0; index < promotedTextBlockCount && items.FirstOrDefault() is JsonObject textBlock &&
                 string.Equals(textBlock["type"]?.GetValue<string>(), "TextBlock", StringComparison.Ordinal); index++)
            {
                items.RemoveAt(0);
            }
            return card.ToJsonString();
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return cardJson;
        }
    }

    private static bool IsProviderHeaderHidden(JsonElement root)
    {
        if (root.TryGetProperty("header", out var header) && header.ValueKind == JsonValueKind.Null)
            return true;
        if (root.TryGetProperty("hideHeader", out var hideHeader) && hideHeader.ValueKind == JsonValueKind.True)
            return true;
        if (root.TryGetProperty("provider", out var provider) && provider.ValueKind == JsonValueKind.Object)
        {
            if (provider.TryGetProperty("hideHeader", out hideHeader) && hideHeader.ValueKind == JsonValueKind.True)
                return true;
            if (provider.TryGetProperty("displayMode", out var displayMode) && displayMode.ValueKind == JsonValueKind.String &&
                string.Equals(displayMode.GetString(), "hidden", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string? TryGetHeaderProperty(string json, string propertyName)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("header", out var header) &&
                header.ValueKind == JsonValueKind.Object &&
                header.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? TryGetStringProperty(string json, string propertyName)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<bool> SetProviderIconFromUriAsync(string uriText)
    {
        if (!Uri.TryCreate(uriText, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return false;
        try
        {
            var bytes = await RemoteImageClient.GetByteArrayAsync(uri);
            using var stream = new InMemoryRandomAccessStream();
            using (var output = stream.GetOutputStreamAt(0))
            using (var writer = new DataWriter(output))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }
            stream.Seek(0);
            var bitmap = new BitmapImage { DecodePixelWidth = 64, DecodePixelHeight = 64 };
            await bitmap.SetSourceAsync(stream);
            ProviderIcon.Source = bitmap;
            ProviderIcon.Visibility = Visibility.Visible;
            ProviderGlyph.Visibility = Visibility.Collapsed;
            return true;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Could not load widget header image: {exception}");
            return false;
        }
    }

    private static string CreateWidgetHostContext(WidgetSize size, bool dark) => new JsonObject
    {
        ["widgetSize"] = size.ToString().ToLowerInvariant(),
        ["hostTheme"] = dark ? "dark" : "light",
        ["isSettingsPayload"] = false,
        ["isHeaderSupported"] = true,
        ["isHeader"] = true,
        ["isWebSupported"] = true,
        ["isUserContextAuthenticated"] = true
    }.ToJsonString();

    private static string ExpandTemplateSafely(string template, string dataJson, string hostJson)
        => new AdaptiveCardTemplate(template).Expand(new EvaluationContext(dataJson, hostJson));

    private AdaptiveCardRenderer CreateAdaptiveCardRenderer(string hostConfigJson)
    {
        var overrides = new ResourceDictionary();
        if (Application.Current.Resources.TryGetValue("DefaultButtonStyle", out var defaultStyle) && defaultStyle is Style)
        {
            overrides["Adaptive.Action"] = defaultStyle;
            overrides["Adaptive.Action.Default"] = defaultStyle;
            overrides["Adaptive.Action.Negative"] = defaultStyle;
            overrides["Adaptive.Action.Destructive"] = defaultStyle;
        }
        if (_smtcCircularActionButtons &&
            Application.Current.Resources.TryGetValue("SmtcCircularWidgetActionButtonStyle", out var circularStyle) &&
            circularStyle is Style)
        {
            overrides["Adaptive.Action"] = circularStyle;
            overrides["Adaptive.Action.Default"] = circularStyle;
        }
        if (Application.Current.Resources.TryGetValue("SubtleButtonStyle", out var subtleStyle) && subtleStyle is Style)
            overrides["Adaptive.Action.Subtle"] = subtleStyle;
        if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out var accentStyle) && accentStyle is Style)
            overrides["Adaptive.Action.Positive"] = accentStyle;

        return new AdaptiveCardRenderer
        {
            HostConfig = AdaptiveHostConfig.FromJsonString(hostConfigJson).HostConfig,
            OverrideStyles = overrides,
            AddSelectActionMargin = false
        };
    }

    private string ApplySystemAccent(string hostConfigJson)
    {
        var accent = GetSystemAccentColor();
        var rgb = $"#{accent.R:X2}{accent.G:X2}{accent.B:X2}";
        var subtle = $"#88{accent.R:X2}{accent.G:X2}{accent.B:X2}";
        return hostConfigJson
            .Replace("#0063B1", rgb, StringComparison.OrdinalIgnoreCase)
            .Replace("#880063B1", subtle, StringComparison.OrdinalIgnoreCase)
            .Replace("#2E89FC", rgb, StringComparison.OrdinalIgnoreCase)
            .Replace("#882E89FC", subtle, StringComparison.OrdinalIgnoreCase);
    }

    private void ApplySystemAccentTextBrush(FrameworkElement root)
    {
        try
        {
            var accentColor = GetSystemAccentColor();
            var accentBrush = AccentBrushProbe.Foreground as SolidColorBrush
                ?? new SolidColorBrush(accentColor);
            var visited = 0;
            ApplySystemAccentTextBrushCore(root, accentBrush, accentColor, ref visited);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Could not apply the system widget accent: {exception}");
        }
    }

    private Windows.UI.Color GetSystemAccentColor()
    {
        if (AccentBrushProbe.Foreground is SolidColorBrush brush)
            return brush.Color;

        try
        {
            return new UISettings().GetColorValue(UIColorType.Accent);
        }
        catch
        {
            return Windows.UI.Color.FromArgb(255, 0, 99, 177);
        }
    }

    private static void ApplySystemAccentTextBrushCore(DependencyObject current, Brush accentBrush, Windows.UI.Color accentColor, ref int visited)
    {
        if (current is TextBlock textBlock && IsAccentColor(textBlock.Foreground, accentColor))
        {
            textBlock.Foreground = accentBrush;
            visited++;
        }
        else if (current is Control control && IsAccentColor(control.Foreground, accentColor))
        {
            control.Foreground = accentBrush;
            visited++;
        }

        if (current is TextBlock inlineText)
            foreach (var inline in inlineText.Inlines)
                ApplyInlineAccentBrush(inline, accentBrush, accentColor, ref visited);

        if (current is RichTextBlock richText)
        {
            foreach (var block in richText.Blocks)
                if (block is Paragraph paragraph)
                    foreach (var inline in paragraph.Inlines)
                        ApplyInlineAccentBrush(inline, accentBrush, accentColor, ref visited);
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(current); index++)
            ApplySystemAccentTextBrushCore(VisualTreeHelper.GetChild(current, index), accentBrush, accentColor, ref visited);
    }

    private static void ApplyInlineAccentBrush(Inline inline, Brush accentBrush, Windows.UI.Color accentColor, ref int visited)
    {
        if (inline is Run run && IsAccentColor(run.Foreground, accentColor))
        {
            run.Foreground = accentBrush;
            visited++;
        }
        else if (inline is Hyperlink hyperlink && IsAccentColor(hyperlink.Foreground, accentColor))
        {
            hyperlink.Foreground = accentBrush;
            visited++;
        }
    }

    private static bool IsAccentColor(Brush? brush, Windows.UI.Color accentColor) =>
        brush is SolidColorBrush solid && solid.Color.R == accentColor.R && solid.Color.G == accentColor.G && solid.Color.B == accentColor.B;

    private async Task<bool> ApplyRootBackgroundAsync(string expandedJson, string hostConfigJson)
    {
        CardBorder.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
        if (!TryGetFullCardSurfaceElement(expandedJson, out var surface))
            return false;

        var promoted = false;

        string? color = null;
        if (surface.TryGetProperty("backgroundColor", out var explicitColor) && explicitColor.ValueKind == JsonValueKind.String)
            color = explicitColor.GetString();
        else if (surface.TryGetProperty("style", out var styleElement) && styleElement.ValueKind == JsonValueKind.String)
        {
            try
            {
                using var config = JsonDocument.Parse(hostConfigJson);
                var styleName = styleElement.GetString();
                if (!string.IsNullOrWhiteSpace(styleName) &&
                    config.RootElement.TryGetProperty("containerStyles", out var styles) &&
                    styles.TryGetProperty(styleName, out var style) &&
                    style.TryGetProperty("backgroundColor", out var styleColor))
                    color = styleColor.GetString();
            }
            catch (JsonException) { }
        }

        if (TryParseAdaptiveColor(color, out var parsedColor))
        {
            CardBorder.Background = new SolidColorBrush(parsedColor);
            promoted = true;
        }

        var backgroundImage = TryGetBackgroundImageUrl(expandedJson);
        if (backgroundImage is not null && await TrySetRemoteImageBrushAsync(expandedJson, backgroundImage))
            promoted = true;
        return promoted;
    }

    private async Task<bool> TrySetRemoteImageBrushAsync(string cardJson, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return false;
        try
        {
            var bytes = await RemoteImageClient.GetByteArrayAsync(uri);
            using var stream = new InMemoryRandomAccessStream();
            using (var output = stream.GetOutputStreamAt(0))
            using (var writer = new DataWriter(output))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }
            stream.Seek(0);
            var bitmap = new BitmapImage { DecodePixelWidth = checked((int)Math.Ceiling(Width * 2)) };
            await bitmap.SetSourceAsync(stream);
            var stretch = string.Equals(TryGetRootBackgroundFillMode(cardJson), "contain", StringComparison.OrdinalIgnoreCase)
                ? Stretch.Uniform : Stretch.UniformToFill;
            CardBorder.Background = new ImageBrush
            {
                ImageSource = bitmap,
                Stretch = stretch,
                AlignmentX = AlignmentX.Center,
                AlignmentY = AlignmentY.Center
            };
            return true;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Could not load widget background image: {exception}");
            return false;
        }
    }

    private static bool TryGetFullCardSurfaceElement(string cardJson, out JsonElement surface)
    {
        surface = default;
        try
        {
            using var document = JsonDocument.Parse(cardJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;
            if (root.TryGetProperty("backgroundColor", out _) || root.TryGetProperty("backgroundImage", out _) ||
                (root.TryGetProperty("style", out var rootStyle) && rootStyle.ValueKind == JsonValueKind.String &&
                 !string.Equals(rootStyle.GetString(), "default", StringComparison.OrdinalIgnoreCase)))
            {
                surface = root.Clone();
                return true;
            }
            if (root.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.Array && body.GetArrayLength() == 1)
            {
                var only = body[0];
                if (only.ValueKind == JsonValueKind.Object &&
                    (only.TryGetProperty("backgroundColor", out _) || only.TryGetProperty("backgroundImage", out _) ||
                     (only.TryGetProperty("style", out var bodyStyle) && bodyStyle.ValueKind == JsonValueKind.String &&
                      !string.Equals(bodyStyle.GetString(), "default", StringComparison.OrdinalIgnoreCase))))
                {
                    surface = only.Clone();
                    return true;
                }
            }
        }
        catch (JsonException) { }
        return false;
    }

    private static string RemovePromotedSurfaceBackground(string cardJson)
    {
        try
        {
            if (JsonNode.Parse(cardJson) is not JsonObject card)
                return cardJson;
            JsonObject? surface = card;
            if (!card.ContainsKey("backgroundImage") && !card.ContainsKey("backgroundColor") && card["body"] is JsonArray body && body.Count == 1)
                surface = body[0] as JsonObject;
            if (surface is null)
                return cardJson;

            surface.Remove("backgroundImage");
            surface.Remove("backgroundColor");
            if (surface["style"] is JsonValue style && TryGetString(style, out var styleName) &&
                !string.Equals(styleName, "default", StringComparison.OrdinalIgnoreCase))
                surface.Remove("style");
            return card.ToJsonString();
        }
        catch (JsonException)
        {
            return cardJson;
        }
    }

    private static string? TryGetBackgroundImageUrl(string cardJson)
    {
        if (!TryGetFullCardSurfaceElement(cardJson, out var surface) || !surface.TryGetProperty("backgroundImage", out var background))
            return null;
        if (background.ValueKind == JsonValueKind.String)
            return background.GetString();
        return background.ValueKind == JsonValueKind.Object && background.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String
            ? url.GetString() : null;
    }

    private static string? TryGetRootBackgroundFillMode(string cardJson)
    {
        if (!TryGetFullCardSurfaceElement(cardJson, out var surface) ||
            !surface.TryGetProperty("backgroundImage", out var background) || background.ValueKind != JsonValueKind.Object ||
            !background.TryGetProperty("fillMode", out var fillMode) || fillMode.ValueKind != JsonValueKind.String)
            return null;
        return fillMode.GetString()?.Trim().ToLowerInvariant();
    }

    private static bool TryParseAdaptiveColor(string? value, out Windows.UI.Color color)
    {
        color = Windows.UI.Color.FromArgb(0, 0, 0, 0);
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith('#') ||
            !uint.TryParse(value.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var argb))
            return false;
        if (value.Length == 7)
        {
            color = Windows.UI.Color.FromArgb(255, (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
            return true;
        }
        if (value.Length == 9)
        {
            color = Windows.UI.Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
            return true;
        }
        return false;
    }

    private double MeasureAdaptiveCardNaturalHeight(string cardJson, string hostConfigJson, double availableWidth)
    {
        var parsed = AdaptiveCard.FromJsonString(cardJson, _elementParser, _actionParser);
        if (parsed?.AdaptiveCard is null)
        {
            System.Diagnostics.Debug.WriteLine("Adaptive Card natural-height measurement skipped because parsing failed.");
            return double.NaN;
        }

        var rendered = CreateAdaptiveCardRenderer(hostConfigJson).RenderAdaptiveCard(parsed.AdaptiveCard);
        if (rendered?.FrameworkElement is not FrameworkElement root)
        {
            System.Diagnostics.Debug.WriteLine("Adaptive Card natural-height measurement skipped because rendering returned no element.");
            return double.NaN;
        }

        root.Width = availableWidth;
        root.Height = double.NaN;
        try
        {
            root.Measure(new Windows.Foundation.Size(availableWidth, double.PositiveInfinity));
        }
        catch (System.Runtime.InteropServices.COMException exception)
        {
            // This probe renders a temporary control tree that is not attached to
            // the widget's XamlRoot. Some native Adaptive Card controls fail during
            // an unconstrained Measure; continue with the fixed-size live render.
            Debug.WriteLine(
                $"Adaptive Card natural-height measurement failed (0x{exception.HResult:X8}); skipping density fitting for this payload.");
            return double.NaN;
        }

        return Math.Max(root.DesiredSize.Height, root.ActualHeight);
    }

    private static string ScaleAdaptiveCardDensity(string cardJson, double geometryScale, double typographyScale)
    {
        try
        {
            if (JsonNode.Parse(cardJson) is not JsonObject card)
                return cardJson;
            ScaleAdaptiveCardNode(card, geometryScale, typographyScale);
            return card.ToJsonString();
        }
        catch (JsonException)
        {
            return cardJson;
        }
    }

    private static void ScaleAdaptiveCardNode(JsonNode? node, double geometryScale, double typographyScale)
    {
        if (node is JsonArray array)
        {
            foreach (var child in array)
                ScaleAdaptiveCardNode(child, geometryScale, typographyScale);
            return;
        }

        if (node is not JsonObject obj)
            return;

        foreach (var property in obj.ToList())
        {
            var key = property.Key;
            var value = property.Value;
            if (value is JsonValue dimension && (key is "height" or "minHeight" or "maxHeight" or "padding") &&
                TryScaleDimensionValue(dimension, geometryScale, out var scaledDimension))
            {
                obj[key] = JsonValue.Create(scaledDimension);
                continue;
            }
            if (value is JsonValue fontSize && key == "fontSize" && TryScaleDimensionValue(fontSize, typographyScale, out var scaledFontSize))
            {
                obj[key] = JsonValue.Create(scaledFontSize);
                continue;
            }
            if (value is JsonValue namedSpacing && key == "spacing" && TryGetString(namedSpacing, out var spacing) &&
                ScaleNamedSpacing(spacing, geometryScale) is { } smallerSpacing)
            {
                obj[key] = JsonValue.Create(smallerSpacing);
                continue;
            }
            if (value is JsonValue namedFontSize && key == "size" && TryGetString(namedFontSize, out var fontSizeName) &&
                ScaleNamedFontSize(fontSizeName, typographyScale) is { } smallerFontSize)
            {
                obj[key] = JsonValue.Create(smallerFontSize);
                continue;
            }

            ScaleAdaptiveCardNode(value, geometryScale, typographyScale);
        }
    }

    private static bool TryGetString(JsonValue value, out string text)
    {
        if (value.GetValueKind() == JsonValueKind.String)
        {
            text = value.GetValue<string>();
            return true;
        }

        text = string.Empty;
        return false;
    }

    private static string? ScaleNamedSpacing(string spacing, double scale)
    {
        var normalized = spacing.Trim().ToLowerInvariant();
        if (scale <= 0.55 && normalized == "small") return "none";
        if (scale <= 0.65 && normalized == "default") return "small";
        if (scale <= 0.75 && normalized == "medium") return "default";
        if (scale <= 0.85 && normalized == "large") return "medium";
        if (scale <= 0.95 && normalized == "extralarge") return "large";
        return null;
    }

    private static string? ScaleNamedFontSize(string fontSize, double scale)
    {
        var normalized = fontSize.Trim().ToLowerInvariant();
        if (scale <= 0.6 && normalized == "default") return "small";
        if (scale <= 0.7 && normalized == "medium") return "small";
        if (scale <= 0.8 && normalized == "large") return "medium";
        if (scale <= 0.9 && normalized == "extralarge") return "large";
        return null;
    }

    private static bool TryScaleDimensionValue(JsonValue value, double scale, out string scaledValue)
    {
        scaledValue = string.Empty;
        if (!TryGetString(value, out var text))
        {
            if (!TryGetJsonNumber(value, out var numericPixels) || numericPixels <= 0)
                return false;
            scaledValue = Math.Max(1, numericPixels * scale).ToString("0.##", CultureInfo.InvariantCulture);
            return true;
        }

        if (!text.EndsWith("px", StringComparison.OrdinalIgnoreCase) ||
            !double.TryParse(text[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var pixels) ||
            !double.IsFinite(pixels) || pixels <= 0)
            return false;

        scaledValue = $"{Math.Max(1, pixels * scale).ToString("0.##", CultureInfo.InvariantCulture)}px";
        return true;
    }

    private static string ScaleAdaptiveHostConfig(string hostConfigJson, double geometryScale, double typographyScale)
    {
        try
        {
            if (JsonNode.Parse(hostConfigJson) is not JsonObject hostConfig)
                return hostConfigJson;
            ScaleAllNumericValues(hostConfig["spacing"], geometryScale);
            ScaleAllNumericValues(hostConfig["imageSizes"], geometryScale, 8);
            ScaleAllNumericValues(hostConfig["fontTypes"]?["default"]?["fontSizes"], typographyScale, 8);
            ScaleAllNumericValues(hostConfig["fontTypes"]?["monospace"]?["fontSizes"], typographyScale, 8);
            ScaleNumericProperty(hostConfig["imageSet"] as JsonObject, "maxImageHeight", geometryScale);
            ScaleNumericProperty(hostConfig["actions"] as JsonObject, "buttonSpacing", geometryScale);
            ScaleNumericProperty(hostConfig["actions"]?["showCard"] as JsonObject, "inlineTopMargin", geometryScale);
            ScaleNumericProperty(hostConfig["factSet"] as JsonObject, "spacing", geometryScale);
            return hostConfig.ToJsonString();
        }
        catch (JsonException)
        {
            return hostConfigJson;
        }
    }

    private static void ScaleAllNumericValues(JsonNode? node, double scale, double minimum = 0)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj.ToList())
            {
                if (!ScaleNumericProperty(obj, property.Key, scale, minimum))
                    ScaleAllNumericValues(property.Value, scale, minimum);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array)
                ScaleAllNumericValues(child, scale, minimum);
        }
    }

    private static bool ScaleNumericProperty(JsonObject? obj, string key, double scale, double minimum = 0)
    {
        if (obj is null || obj[key] is not JsonValue value || !TryGetJsonNumber(value, out var number))
            return false;
        obj[key] = JsonValue.Create(number == 0 ? 0 : (int)Math.Max(minimum, Math.Round(number * scale)));
        return true;
    }

    private static bool TryGetJsonNumber(JsonValue value, out double number)
    {
        if (value.GetValueKind() == JsonValueKind.Number &&
            double.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number) &&
            double.IsFinite(number))
            return true;

        number = 0;
        return false;
    }

    private static string ApplyFloatingHeaderAvoidance(string cardJson, double topInset, double existingTopPadding)
    {
        var spacerHeight = double.IsFinite(topInset) && double.IsFinite(existingTopPadding)
            ? Math.Max(0, topInset - existingTopPadding)
            : 0;
        if (spacerHeight <= 0)
            return cardJson;

        try
        {
            if (JsonNode.Parse(cardJson) is not JsonObject card || card["body"] is not JsonArray body)
                return cardJson;

            // Add title clearance after the density pass: the spacer remains
            // fixed while the card content has already been compressed to fit
            // below it. The Adaptive Card root stays full size so any promoted
            // provider background still paints behind the floating header.
            body.Insert(0, new JsonObject
            {
                ["type"] = "Container",
                ["spacing"] = "none",
                ["minHeight"] = $"{spacerHeight.ToString("0.##", CultureInfo.InvariantCulture)}px",
                ["items"] = new JsonArray()
            });
            return card.ToJsonString();
        }
        catch (JsonException)
        {
            return cardJson;
        }
    }

    private static double GetAdaptiveCardTopPadding(string hostConfigJson)
    {
        try
        {
            using var document = JsonDocument.Parse(hostConfigJson);
            return document.RootElement.TryGetProperty("spacing", out var spacing) &&
                spacing.ValueKind == JsonValueKind.Object &&
                spacing.TryGetProperty("padding", out var padding) &&
                padding.ValueKind == JsonValueKind.Number &&
                padding.TryGetDouble(out var value) && double.IsFinite(value) && value > 0
                    ? value
                    : 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    private void UpdateRendererTruncationState(RenderedAdaptiveCard rendered, double naturalHeight, double viewportHeight)
    {
        var panels = EnumerateVisualTree(rendered.FrameworkElement).OfType<WholeItemsPanel>().ToArray();
        var truncatedPanels = panels.Where(panel => panel.IsTruncated()).ToArray();
        var allContentClippedOut = panels.Any(panel => panel.IsAllContentClippedOut());
        var measuredOverflow = double.IsFinite(naturalHeight) && naturalHeight > viewportHeight + 1;
        var isTruncated = truncatedPanels.Length > 0 || allContentClippedOut || measuredOverflow;

        if (isTruncated)
        {
            var details = truncatedPanels.Select(panel => panel.GetAltText())
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var detailText = details.Length == 0
                ? "Some Adaptive Card content does not fit the available widget body viewport."
                : string.Join(" ", details);
            if (measuredOverflow)
                detailText += $" Content height after adaptive fitting is {naturalHeight:0.##} DIP for a {viewportHeight:0.##} DIP body viewport.";

            CardRenderStateText.Text = $"{Get("WidgetsCardContentTruncated")} {detailText}";
            CardRenderStateText.Visibility = Visibility.Visible;
            System.Diagnostics.Debug.WriteLine(
                $"Widget renderer truncated: panels={panels.Length}, truncated={truncatedPanels.Length}, allContentClippedOut={allContentClippedOut}, measuredOverflow={measuredOverflow}; {detailText}");
        }
        else
        {
            CardRenderStateText.Text = string.Empty;
            CardRenderStateText.Visibility = Visibility.Collapsed;
        }
    }

    private static IEnumerable<DependencyObject> EnumerateVisualTree(DependencyObject root)
    {
        yield return root;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            foreach (var child in EnumerateVisualTree(VisualTreeHelper.GetChild(root, index)))
                yield return child;
        }
    }

    private static string? TryGetWebContentUrl(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("metadata", out var metadata) &&
                metadata.ValueKind == JsonValueKind.Object &&
                metadata.TryGetProperty("webUrl", out var webUrl) &&
                webUrl.ValueKind == JsonValueKind.String &&
                Uri.TryCreate(webUrl.GetString()?.Trim(), UriKind.Absolute, out var uri) &&
                uri.Scheme is "https" or "http")
                return uri.AbsoluteUri;
        }
        catch (JsonException)
        {
        }
        return null;
    }

    private static string? GetPropertyText(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) ? value.GetRawText() : null;

    private static string MergeActionData(string? actionData, string? inputData)
    {
        if (string.IsNullOrWhiteSpace(actionData) || actionData == "null")
            return string.IsNullOrWhiteSpace(inputData) || inputData == "null" ? "{}" : inputData;
        if (string.IsNullOrWhiteSpace(inputData) || inputData == "null")
            return actionData;
        try
        {
            if (JsonNode.Parse(actionData) is JsonObject actionObject && JsonNode.Parse(inputData) is JsonObject inputObject)
            {
                foreach (var pair in inputObject)
                    actionObject[pair.Key] = pair.Value?.DeepClone();
                return actionObject.ToJsonString();
            }
        }
        catch (JsonException)
        {
        }
        return actionData;
    }

    private void SetSizeLayout(WidgetSize size)
    {
        var scale = GetTextScaleFactor();
        Height = GetHeight(size) * scale;
        MinHeight = Height;
        MaxHeight = Height;
        Width = 300 * scale;
        HeaderGrid.Padding = new Thickness(10 * scale, 3 * scale, 10 * scale, 0);
        HeaderIdentityStack.Spacing = 0;
        TitleText.MaxWidth = 180 * scale;
        ProviderIcon.Width = 14 * scale;
        ProviderIcon.Height = 14 * scale;
        ProviderIconContainer.Width = 14 * scale;
        ProviderIconContainer.Height = 14 * scale;
        ProviderGlyph.FontSize = 9 * scale;
        MenuButton.Width = 28 * scale;
        MenuButton.Height = 28 * scale;
        MenuButton.MinWidth = 28 * scale;
        MenuButton.MinHeight = 28 * scale;
        if (MenuButton.Content is FontIcon menuIcon)
            menuIcon.FontSize = 14 * scale;
        CardRenderStateText.Margin = new Thickness(8 * scale, 0, 8 * scale, 4 * scale);
    }

    private double GetTextScaleFactor() => double.IsFinite(_textScaleFactor) && _textScaleFactor > 0
        ? _textScaleFactor
        : 1.0;

    private static double GetHeight(WidgetSize size) => size switch
    {
        WidgetSize.Small => 146,
        WidgetSize.Large => 462,
        _ => 304
    };

    private void ShowStatus(bool loading, string message)
    {
        var hasMessage = loading || !string.IsNullOrWhiteSpace(message);
        LoadingRing.IsActive = loading;
        StatusPanel.Visibility = hasMessage ? Visibility.Visible : Visibility.Collapsed;
        CardContent.Visibility = hasMessage || _activeWebContentUrl is not null ? Visibility.Collapsed : Visibility.Visible;
    }
    private static bool IsJsonObject(string? value)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(value) && JsonDocument.Parse(value).RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsExpandedAdaptiveCardPayload(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        try
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("type", out var type) &&
                type.ValueKind == JsonValueKind.String &&
                string.Equals(type.GetString(), "AdaptiveCard", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsEmptyObject(string? value)
    {
        try
        {
            using var document = JsonDocument.Parse(value ?? string.Empty);
            return document.RootElement.ValueKind == JsonValueKind.Object && !document.RootElement.EnumerateObject().Any();
        }
        catch (JsonException)
        {
            return true;
        }
    }

    private static string Get(string key) => new ResourceLoader().GetString(key);
}




