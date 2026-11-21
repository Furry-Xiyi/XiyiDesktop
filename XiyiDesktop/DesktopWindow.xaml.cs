using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Input;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.Windows.ApplicationModel.Resources;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using XiyiDesktop.ApplicationActivation;
using XiyiDesktop.DesktopHost;
using XiyiDesktop.DesktopItems;
using XiyiDesktop.Dialogs;
using XiyiDesktop.FileOperations;
using XiyiDesktop.Interop;
using XiyiDesktop.Widgets;
using XiyiDesktop.Widgets.Hosting;
using XiyiDesktop.Widgets.Models;
using XiyiDesktop.Widgets.UI;
using Microsoft.Windows.Widgets;

namespace XiyiDesktop
{
    public sealed partial class DesktopWindow : UserControl, IDisposable
    {
        private static readonly object SendToDestinationCacheLock = new();
        private static Task<IReadOnlyList<SendToDestination>>? _sendToDestinationCacheTask;
        private static DateTimeOffset _sendToDestinationCacheExpiresUtc = DateTimeOffset.MinValue;
        private static readonly TimeSpan SendToDestinationCacheLifetime = TimeSpan.FromMinutes(5);

        private readonly ResourceLoader _resources = new();
        private readonly Dictionary<string, string> _resourceTextCache = new(StringComparer.Ordinal);
        private readonly DesktopItemsService _itemsService = new();
        private readonly DesktopLayoutStore _layoutStore = new();
        private readonly WidgetPlacementStore _widgetPlacementStore = new();
        private readonly WidgetHostingService _widgetHostingService = App.WidgetHostService;
        private readonly Dictionary<string, WidgetDesktopSurface> _widgetSurfaces = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, WebWidgetOverlayWindow> _webWidgetOverlays = new(StringComparer.OrdinalIgnoreCase);
        private readonly TaskCompletionSource<bool> _initialDesktopItemsLayoutReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private WidgetCardControl? _widgetDragSurface;
        private DesktopLayoutTransaction? _widgetDragTransaction;
        private readonly Dictionary<string, DesktopLayoutEngine.Point> _widgetDragOriginalPositions = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<DesktopItemViewModel, Windows.Foundation.Point> _widgetDragIconPositions = [];
        private uint _widgetDragPointerId;
        private Windows.Foundation.Point _widgetDragStart;
        private Windows.Foundation.Point _widgetDragOrigin;
        private string? _lastWidgetGridSlotKey;
        private bool _widgetsRestored;
        private readonly ContextMenuPreferencesService _contextMenuPreferences = new();
        private readonly DispatcherQueue? _dispatcherQueue;
        private readonly HashSet<string> _cutDesktopItemPaths = new(StringComparer.OrdinalIgnoreCase);
        private readonly Grid _desktopRoot;
        private DesktopWindowXamlSource? _desktopXamlSource;
        private readonly TranslateTransform _workAreaTransitionTransform = new();
        private Storyboard? _workAreaTransitionStoryboard;
        private readonly MenuFlyout _desktopContextFlyout;
        private readonly List<RadioMenuFlyoutItem> _iconSizeMenuItems = [];
        private readonly List<RadioMenuFlyoutItem> _sortMenuItems = [];
        private readonly HashSet<DesktopItemViewModel> _selectedDesktopItems = [];
        private readonly HashSet<DesktopItemViewModel> _marqueeHoveredDesktopItems = [];
        private ToggleMenuFlyoutItem? _showIconsMenuItem;
        private ToggleMenuFlyoutItem? _autoArrangeMenuItem;
        private ToggleMenuFlyoutItem? _alignToGridMenuItem;
        private MenuFlyoutSubItem? _applicationExtensionsItem;
        private MenuFlyoutSubItem? _itemApplicationExtensionsItem;
        private MenuFlyoutItem? _customizeMenuItem;
        private MenuFlyoutItem? _itemCustomizeMenuItem;
        private MenuFlyoutItem? _itemShowMoreOptionsMenuItem;
        private MenuFlyoutItem? _itemOpenMenuItem;
        private MenuFlyoutItem? _emptyRecycleBinMenuItem;
        private MenuFlyoutItem? _manageThisPcMenuItem;
        private MenuFlyoutItem? _itemOpenFileLocationMenuItem;
        private MenuFlyoutItem? _itemRunAsAdminMenuItem;
        private MenuFlyoutItem? _itemShareMenuItem;
        private MenuFlyoutItem? _itemPasteMenuItem;
        private MenuFlyoutItem? _pasteMenuItem;
        private MenuFlyoutItem? _undoDeleteMenuItem;
        private AppBarButton? _legacyUndoDeleteButton;
        private AppBarButton? _legacyDesktopPasteButton;
        private AppBarButton? _legacyItemOpenButton;
        private AppBarButton? _legacyItemPasteButton;
        private AppBarButton? _legacyItemCopyButton;
        private AppBarButton? _legacyItemCutButton;
        private AppBarButton? _legacyItemDeleteButton;
        private AppBarButton? _legacyItemRenameButton;
        private AppBarButton? _legacyItemShareButton;
        private AppBarButton? _legacyItemOpenLocationButton;
        private AppBarButton? _legacyItemRunAsButton;
        private AppBarButton? _legacyItemPropertiesButton;
        private AppBarButton? _legacyEmptyRecycleBinButton;
        private AppBarButton? _legacyItemCustomizeMenuButton;
        private AppBarButton? _legacyItemShowMoreOptionsButton;
        private AppBarButton? _legacyManageThisPcButton;
        private MenuFlyoutSubItem? _sendToMenuItem;
        private MenuFlyoutItem? _printMenuItem;
        private MenuFlyoutItem? _createShortcutMenuItem;
        private MenuFlyoutItem? _copyAsPathMenuItem;
        private MenuFlyoutItem? _movePropertiesMenuItem;
        private MenuFlyoutSeparator? _movePropertiesSeparator;
        private MenuFlyoutSeparator? _desktopExtensionsSeparator;
        private MenuFlyoutSeparator? _itemExtensionsSeparator;
        private MenuFlyoutSubItem? _rotateImageMenuItem;
        private AppBarButton? _legacySendToButton;
        private AppBarButton? _legacyPrintButton;
        private AppBarButton? _legacyCreateShortcutButton;
        private AppBarButton? _legacyCopyAsPathButton;
        private AppBarButton? _legacyRotateImageButton;
        private AppBarButton? _legacyApplicationExtensionsButton;
        private AppBarButton? _legacyItemApplicationExtensionsButton;
        private AppBarSeparator? _legacyDesktopExtensionsSeparator;
        private AppBarSeparator? _legacyItemExtensionsSeparator;
        private CommandBarFlyout? _legacyDesktopCommandBarFlyout;
        private CommandBarFlyout? _legacyItemCommandBarFlyout;
        private AppBarButton? _legacyCustomizeMenuButton;
        private MenuFlyoutItem? _showMoreOptionsItem;
        private AppBarButton? _legacyShowMoreOptionsButton;
        private MenuFlyout? _legacyApplicationExtensionsFlyout;
        private MenuFlyout? _legacyItemApplicationExtensionsFlyout;
        private readonly List<MenuFlyoutItemBase> _dynamicExtensionMenuItems = [];
        private readonly List<MenuFlyoutItemBase> _dynamicItemExtensionMenuItems = [];
        private readonly List<AppBarButton> _dynamicLegacyExtensionButtons = [];
        private readonly List<AppBarButton> _dynamicLegacyItemExtensionButtons = [];
        private readonly List<AppBarSeparator> _dynamicLegacyExtensionSeparators = [];
        private readonly List<AppBarSeparator> _dynamicLegacyItemExtensionSeparators = [];
        private int _extensionRefreshGeneration;
        private int _itemExtensionRefreshGeneration;
        private int _sendToRefreshGeneration;
        private DesktopHostController? _hostController;
        private DesktopMenuHostWindow? _menuHostWindow;
        private FlyoutBase? _activeContextFlyout;
        private CancellationTokenSource? _refreshCancellation;
        private CancellationTokenSource? _iconRefreshCancellation;
        private XamlRoot? _subscribedXamlRoot;
        private double _observedRasterizationScale = double.NaN;
        private readonly WinEventDelegate _displayChangeCallback;
        private IntPtr _displayChangeHook;
        private readonly WinEventDelegate _taskbarBoundsChangeCallback;
        private IntPtr _taskbarBoundsChangeHook;
        private int _refreshGeneration;
        private bool _isApplyingItemOrder;
        private bool _sortDescending;
        private bool _disposed;
        private DesktopItemViewModel? _positionDragItem;
        private DesktopItemViewModel[] _positionDragItems = [];
        private readonly Dictionary<DesktopItemViewModel, Windows.Foundation.Point> _positionDragOrigins = [];
        private readonly Dictionary<DesktopItemViewModel, Windows.Foundation.Point> _positionDragSavedPositions = [];
        private readonly Dictionary<string, DesktopLayoutEngine.Point> _positionDragWidgetPositions = new(StringComparer.OrdinalIgnoreCase);
        private uint _positionDragPointerId;
        private Windows.Foundation.Point _positionDragStart;
        private Windows.Foundation.Point _positionDragLastPointerPosition;
        private long _positionDragLastMoveTimestamp;
        private double _positionDragSmoothedSpeed;
        private bool _positionDragMoved;
        private bool _positionDragOverRecycleBin;
        private bool _iconDragPreviewQueued;
        private double _pendingIconDragReflowDelayMilliseconds;
        private bool _desktopSurfacePointerMode;
        private bool _isSelectingDesktopArea;
        private int _clipboardStateRefreshGeneration;
        private Task? _undoAvailabilityRefreshTask;
        private uint _selectionPointerId;
        private Windows.Foundation.Point _selectionStart;
        private bool _selectionHapticSent;
        private PointerPoint? _lastPenPointerPoint;
        private readonly IntPtr _windowHandle;
        private DispatcherQueueTimer? _iconSizeRefreshTimer;
        private DispatcherQueueTimer? _taskbarBoundsChangeTimer;
        private DispatcherQueueTimer? _widgetOverlayLayoutTimer;
        private DispatcherQueueTimer? _iconDragPreviewTimer;
        private DateTimeOffset _widgetOverlayLayoutTrackingUntilUtc;
        private const double MinimumDesktopIconSize = DesktopIconMetrics.Minimum;
        private const double MaximumDesktopIconSize = DesktopIconMetrics.Maximum;
        private const double DesktopEdgePadding = 8;
        private bool _hasAppliedWorkAreaMargins;

        private static class DesktopClickBehavior
        {
            private const string ExplorerRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer";
            private const string ShellStateValueName = "ShellState";
            private const int ShellStateFlagsOffset = sizeof(uint);
            private const uint DoubleClickInWebViewStateBit = 1u << 5;

            public static bool OpensOnSingleClick
            {
                get
                {
                    try
                    {
                        using var key = Registry.CurrentUser.OpenSubKey(ExplorerRegistryPath, writable: false);
                        if (key?.GetValue(ShellStateValueName) is not byte[] shellState ||
                            shellState.Length < ShellStateFlagsOffset + sizeof(uint))
                            return false;

                        // The value starts with its size; the next DWORD contains the packed click flags.
                        var shellStateFlags = BitConverter.ToUInt32(shellState, ShellStateFlagsOffset);
                        return (shellStateFlags & DoubleClickInWebViewStateBit) == 0;
                    }
                    catch (Exception exception)
                    {
                        System.Diagnostics.Debug.WriteLine($"Could not read the Windows Explorer click setting: {exception}");
                        return false;
                    }
                }
            }
        }

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int StrCmpLogicalW(string left, string right);

        public DesktopWindow()
        {
            InitializeComponent();

            _desktopRoot = (Grid)Content;
            UpdateCornerRadiusSetting(AppSettingsStore.DesktopCornerRadius);
            if (MarqueeFadeOut.Children.Count > 0)
                Storyboard.SetTarget(MarqueeFadeOut.Children[0], SelectionRectangle);
            DesktopSurface.RenderTransform = _workAreaTransitionTransform;
            DesktopItemsView.Margin = new Thickness(0);
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
            _desktopRoot.ActualThemeChanged += DesktopRoot_ActualThemeChanged;
            MarqueeFadeOut.Completed += (_, _) =>
            {
                SelectionRectangle.Visibility = Visibility.Collapsed;
                SelectionRectangle.Opacity = 0.25;
            };
            _desktopContextFlyout = CreateDesktopContextFlyout();
            _desktopContextFlyout.Opening += (_, _) => UpdateUndoDeleteVisibility();
            _sortDescending = XiyiDesktop.AppSettingsStore.DesktopSortDescending;
            DesktopItemsView.SetDesktopIconsVisible(XiyiDesktop.AppSettingsStore.ShowDesktopIcons);
            DesktopItemsView.DragReflowed += DesktopItemsView_DragReflowed;
            UpdateReorderAvailability();

            _windowHandle = DesktopCarrierWindow.Create();
            UpdateUndoDeleteVisibility();
            _itemsService.ItemsChanged += ItemsService_ItemsChanged;
            Clipboard.ContentChanged += Clipboard_ContentChanged;
            Items.CollectionChanged += Items_CollectionChanged;
            _displayChangeCallback = DisplayChange_WinEvent;
            _displayChangeHook = SetWinEventHook(
                EventSystemDisplayChange,
                EventSystemDisplayChange,
                IntPtr.Zero,
                _displayChangeCallback,
                0,
                0,
                WineventOutOfContext);
            _taskbarBoundsChangeCallback = TaskbarBoundsChanged_WinEvent;
            _taskbarBoundsChangeHook = SetWinEventHook(
                EventObjectLocationChange,
                EventObjectLocationChange,
                IntPtr.Zero,
                _taskbarBoundsChangeCallback,
                0,
                0,
                WineventOutOfContext);
            _desktopRoot.Loaded += DesktopRoot_Loaded;
            DesktopItemsView.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(WidgetCard_PointerPressed), handledEventsToo: true);
            DesktopItemsView.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(WidgetCard_PointerMoved), handledEventsToo: true);
            DesktopItemsView.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(WidgetCard_PointerReleased), handledEventsToo: true);
            DesktopItemsView.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(WidgetCard_PointerCanceled), handledEventsToo: true);
            AttachDesktopRootToXamlIsland();
        }

        public ObservableCollection<DesktopItemViewModel> Items { get; } = new();

        public IntPtr WindowHandle => _windowHandle;

        internal bool IsDesktopAttached => _hostController?.IsAttached == true;

        internal void UpdateWidgetMaterial(BackgroundMaterial material)
        {
            foreach (var surface in _widgetSurfaces.Values)
                surface.Card.UpdateBackgroundMaterial(material);
        }

        internal void UpdateCornerRadiusSetting(double cornerRadius)
        {
            cornerRadius = double.IsFinite(cornerRadius) ? Math.Clamp(cornerRadius, 0, 32) : 8;
            var radius = new CornerRadius(cornerRadius);
            SelectionRectangle.CornerRadius = radius;

            foreach (var item in Items)
                item.SetCornerRadius(cornerRadius);

            foreach (var surface in _widgetSurfaces.Values)
            {
                surface.Card.UpdateCornerRadius(cornerRadius);
                UpdateWebWidgetOverlay(surface);
            }
        }

        private void AttachDesktopRootToXamlIsland()
        {
            DesktopWindowXamlSource? island = null;
            try
            {
                // Host the desktop tree directly in an island under a borderless
                // native HWND. There is no outer WinUI Window to paint an opaque fill.
                island = new DesktopWindowXamlSource();
                island.Initialize(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_windowHandle));
                if (island.SiteBridge is Microsoft.UI.Content.DesktopChildSiteBridge childSiteBridge)
                {
                    childSiteBridge.ResizePolicy =
                        Microsoft.UI.Content.ContentSizePolicy.ResizeContentToParentWindow;
                }
                island.Content = this;
                _desktopRoot.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
                _desktopXamlSource = island;
            }
            catch (Exception exception)
            {
                try { island?.Dispose(); } catch { }
                Debug.WriteLine($"Could not host the desktop root in a WinUI XAML Island: {exception}");
            }
        }

        private void DesktopRoot_ActualThemeChanged(FrameworkElement sender, object args)
        {
            foreach (var surface in _widgetSurfaces.Values)
                _ = surface.Card.SetProviderIconAsync(GetWidgetIconPath(surface, _desktopRoot.ActualTheme));
        }

        private const uint EventSystemDisplayChange = 0x001F;
        private const uint EventObjectLocationChange = 0x800B;
        private const int ObjectIdWindow = 0;
        private const uint WineventOutOfContext = 0x0000;

        private void DisplayChange_WinEvent(
            IntPtr hook,
            uint eventType,
            IntPtr windowHandle,
            int objectId,
            int childId,
            uint eventThread,
            uint eventTime) => QueueDisplayRefresh();

        private void TaskbarBoundsChanged_WinEvent(
            IntPtr hook,
            uint eventType,
            IntPtr windowHandle,
            int objectId,
            int childId,
            uint eventThread,
            uint eventTime)
        {
            if (windowHandle == IntPtr.Zero || objectId != ObjectIdWindow)
                return;

            var className = NativeMethods.GetClassName(windowHandle);
            if (className.Equals("Shell_TrayWnd", StringComparison.Ordinal) ||
                className.Equals("Shell_SecondaryTrayWnd", StringComparison.Ordinal))
                QueueTaskbarBoundsRefresh();
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWinEventHook(
            uint eventMinimum,
            uint eventMaximum,
            IntPtr eventModule,
            WinEventDelegate callback,
            uint processId,
            uint threadId,
            uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWinEvent(IntPtr hook);

        private delegate void WinEventDelegate(
            IntPtr hook,
            uint eventType,
            IntPtr windowHandle,
            int objectId,
            int childId,
            uint eventThread,
            uint eventTime);

        internal void RefreshHost() => _hostController?.Refresh();

        private void QueueDisplayRefresh()
        {
            if (_disposed || _dispatcherQueue is null)
                return;

            try
            {
                _dispatcherQueue.TryEnqueue(() =>
                {
                    if (_disposed)
                        return;

                    _hostController?.RefreshLayout();
                    UpdateDesktopWorkAreaMargins();
                });
            }
            catch (Exception exception) when (exception is COMException or ObjectDisposedException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine($"Unable to refresh the desktop after a display change: {exception.Message}");
            }
        }

        private void QueueTaskbarBoundsRefresh()
        {
            if (_disposed || _dispatcherQueue is null)
                return;

            try
            {
                _dispatcherQueue.TryEnqueue(() =>
                {
                    if (_disposed)
                        return;

                    _taskbarBoundsChangeTimer ??= _dispatcherQueue.CreateTimer();
                    _taskbarBoundsChangeTimer.Stop();
                    _taskbarBoundsChangeTimer.Interval = TimeSpan.FromMilliseconds(500);
                    _taskbarBoundsChangeTimer.IsRepeating = false;
                    _taskbarBoundsChangeTimer.Tick -= TaskbarBoundsChangeTimer_Tick;
                    _taskbarBoundsChangeTimer.Tick += TaskbarBoundsChangeTimer_Tick;
                    _taskbarBoundsChangeTimer.Start();
                });
            }
            catch (Exception exception) when (exception is COMException or ObjectDisposedException)
            {
                Debug.WriteLine($"Unable to queue a taskbar bounds refresh: {exception.Message}");
            }
        }

        private void TaskbarBoundsChangeTimer_Tick(DispatcherQueueTimer sender, object args)
        {
            sender.Stop();
            if (!_disposed)
                UpdateDesktopWorkAreaMargins();
        }

        private void DesktopRoot_Loaded(object sender, RoutedEventArgs e)
        {
            SubscribeToXamlRootChanges();

            // This HWND is only a desktop surface. Keep it hidden until the Explorer
            // desktop layer has been found, attached, and validated.
            NativeMethods.HideWindow(_windowHandle);

            // A DispatcherQueue is created by the WinUI application bootstrapper. It can
            // be unavailable while a window is being torn down or when startup is being
            // interrupted by activation redirection. Do not start shell work in that state.
            if (_dispatcherQueue is null)
            {
                return;
            }

            if (_hostController is null)
            {
                _hostController = new DesktopHostController(_windowHandle, _dispatcherQueue);
                _hostController.StateChanged += HostController_StateChanged;
                _hostController.WindowHandleLost += HostController_WindowHandleLost;
                _hostController.Start();
            }

            DesktopItemsView.UpdateLayout();
            DesktopItemsView.SetViewportSize(new Windows.Foundation.Size(DesktopItemsView.ActualWidth, DesktopItemsView.ActualHeight));
            UpdateDesktopWorkAreaMargins();

            UpdateUndoDeleteVisibility();
            _ = RefreshDesktopItemsAsync(showErrorDialog: false);
            _ = RestoreDesktopWidgetsAsync();
        }

        internal bool CanFitWidget(string sizeName)
        {
            var size = ParseWidgetSize(sizeName);
            return TryFindWidgetPlacement(size, out _);
        }

        internal bool HasWidgetDefinition(string definitionId) =>
            _widgetSurfaces.Values.Any(surface => string.Equals(surface.DefinitionId, definitionId, StringComparison.OrdinalIgnoreCase)) ||
            _widgetPlacementStore.GetAll().Any(placement => string.Equals(placement.DefinitionId, definitionId, StringComparison.OrdinalIgnoreCase));

        internal bool CanAddWidget(string definitionId, string sizeName) =>
            !HasWidgetDefinition(definitionId) && CanFitWidget(sizeName);

        internal async Task<bool> AddWidgetAsync(WidgetProviderInfo provider, WidgetDefinition definition, string sizeName)
        {
            if (_disposed || HasWidgetDefinition(definition.DefinitionId))
                return false;

            var size = ParseWidgetSize(sizeName);
            var safeDefinition = new SafeWidgetDefinition(_widgetHostingService, definition.DefinitionId);
            if (!await safeDefinition.PopulateAsync())
                return false;
            var supportedSizes = (await safeDefinition.GetSupportedSizesAsync()).ToArray();
            if (supportedSizes.Length == 0 || !supportedSizes.Contains(size))
                return false;

            if (!TryFindWidgetPlacement(size, out var placement))
                return false;

            if (!await _widgetHostingService.EnsureCatalogReadyForCreationAsync())
                return false;

            var widget = await _widgetHostingService.CreateWidgetAsync(definition.DefinitionId, size);
            if (widget is null || string.IsNullOrWhiteSpace(widget.Id))
                return false;

            var instance = new SafeWidgetInstance(_widgetHostingService, widget.Id);
            if (!await instance.PopulateAsync())
            {
                await instance.DeleteAsync();
                return false;
            }

            var actualWidgetSize = await instance.GetSizeAsync();
            if (actualWidgetSize != size &&
                (!await instance.SetSizeAsync(size) || await instance.GetSizeAsync() != size))
            {
                await instance.DeleteAsync();
                return false;
            }

            var title = !string.IsNullOrWhiteSpace(safeDefinition.DisplayTitle)
                ? safeDefinition.DisplayTitle
                : !string.IsNullOrWhiteSpace(safeDefinition.ProviderDefinitionDisplayName)
                    ? safeDefinition.ProviderDefinitionDisplayName
                    : safeDefinition.Id;
            var card = new WidgetCardControl(instance, title, size, supportedSizes);
            FitWidgetCardToDesktopGrid(card);
            var layoutElement = CreateWidgetLayoutElement(card);
            AttachWidgetCardHandlers(card);
            DesktopItemsView.Children.Add(layoutElement);
            DesktopItemsView.RegisterExternalLayoutItem(layoutElement);
            Canvas.SetLeft(layoutElement, placement.X);
            Canvas.SetTop(layoutElement, placement.Y);
            var surface = new WidgetDesktopSurface(widget.Id, definition.DefinitionId, title, card, layoutElement, provider, placement.X, placement.Y);
            _widgetSurfaces[widget.Id] = surface;
            surface.SupportedSizes = supportedSizes.Select(value => value.ToString()).ToArray();
            surface.LightIconPath = SelectWidgetIconResource(provider, definition, "LightMode")?.ResolvedPath;
            surface.DarkIconPath = SelectWidgetIconResource(provider, definition, "DarkMode")?.ResolvedPath;
            surface.DefaultIconPath = SelectWidgetIconResource(provider, definition, "Provider")?.ResolvedPath
                ?? SelectWidgetIconResource(provider, definition, "Default")?.ResolvedPath;
            surface.WebUri = card.ActiveWebContentUri;
            SaveWidgetPlacements();
            RefreshSharedDesktopLayout();
            SaveManualDesktopOrder();
            await card.SetProviderIconAsync(GetWidgetIconPath(surface, _desktopRoot.ActualTheme));
            await card.LoadAsync();
            return true;
        }

        private async Task RestoreDesktopWidgetsAsync()
        {
            if (_widgetsRestored || _disposed)
                return;

            _widgetsRestored = true;
            await _initialDesktopItemsLayoutReady.Task;
            if (_disposed)
                return;

            var storedPlacements = _widgetPlacementStore.GetAll();
            var restoredDefinitions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var restoredCards = new List<WidgetCardControl>(storedPlacements.Count);
            foreach (var placement in storedPlacements)
            {
                try
                {
                    if (restoredDefinitions.Contains(placement.DefinitionId))
                    {
                        var duplicateWidget = await _widgetHostingService.GetWidgetAsync(placement.InstanceId);
                        if (duplicateWidget is not null)
                            await duplicateWidget.DeleteAsync();
                        _widgetPlacementStore.Remove(placement.InstanceId);
                        continue;
                    }

                    var size = ParseWidgetSize(placement.Size);
                    var widget = await _widgetHostingService.GetWidgetAsync(placement.InstanceId);
                    if (widget is null)
                    {
                        _widgetPlacementStore.Remove(placement.InstanceId);
                        continue;
                    }

                    var instance = new SafeWidgetInstance(_widgetHostingService, widget.Id);
                    var displayName = string.IsNullOrWhiteSpace(placement.DisplayName)
                        ? placement.DefinitionId
                        : placement.DisplayName;
                    var card = new WidgetCardControl(instance, displayName, size, ParseSupportedWidgetSizes(placement.SupportedSizes));
                    FitWidgetCardToDesktopGrid(card);
                    var layoutElement = CreateWidgetLayoutElement(card);
                    AttachWidgetCardHandlers(card);
                    DesktopItemsView.Children.Add(layoutElement);
                    DesktopItemsView.RegisterExternalLayoutItem(layoutElement);
                    card.ShowLoadingStatus();
                    var viewportWidth = Math.Max(1, DesktopItemsView.ActualWidth);
                    var viewportHeight = Math.Max(1, DesktopItemsView.ActualHeight);
                    var x = ScaleCoordinate(placement.X, placement.ViewportWidth, viewportWidth);
                    var y = ScaleCoordinate(placement.Y, placement.ViewportHeight, viewportHeight);
                    if (placement.CoordinateSpaceVersion < 1)
                    {
                        x -= DesktopSurface.Margin.Left;
                        y -= DesktopSurface.Margin.Top;
                        var normalizedPlacement = placement with
                        {
                            X = x,
                            Y = y,
                            CoordinateSpaceVersion = 1,
                            ViewportWidth = viewportWidth,
                            ViewportHeight = viewportHeight
                        };
                        _widgetPlacementStore.Upsert(normalizedPlacement);
                    }
                    else if (placement.ViewportWidth <= 0 || placement.ViewportHeight <= 0)
                    {
                        var normalizedPlacement = placement with
                        {
                            X = x,
                            Y = y,
                            ViewportWidth = viewportWidth,
                            ViewportHeight = viewportHeight
                        };
                        _widgetPlacementStore.Upsert(normalizedPlacement);
                    }
                    ClampWidgetPosition(card, ref x, ref y);
                    Canvas.SetLeft(layoutElement, x);
                    Canvas.SetTop(layoutElement, y);
                    var surface = new WidgetDesktopSurface(widget.Id, placement.DefinitionId, displayName, card, layoutElement, null, x, y)
                    {
                        SupportedSizes = placement.SupportedSizes,
                        LightIconPath = placement.LightIconPath,
                        DarkIconPath = placement.DarkIconPath,
                        DefaultIconPath = placement.DefaultIconPath
                    };
                    _widgetSurfaces[widget.Id] = surface;
                    surface.WebUri = card.ActiveWebContentUri;
                    restoredDefinitions.Add(placement.DefinitionId);
                    restoredCards.Add(card);
                    RefreshSharedDesktopLayout();
                    await card.SetProviderIconAsync(GetWidgetIconPath(surface, _desktopRoot.ActualTheme));
                }
                catch (Exception exception)
                {
                    Debug.WriteLine($"Could not restore desktop widget {placement.InstanceId}: {exception}");
                    _widgetPlacementStore.Remove(placement.InstanceId);
                }
            }

            ResolveWidgetCollisions(persistChanges: false);
            RefreshSharedDesktopLayout();

            // WidgetHost and WebView2 providers expose apartment-bound native
            // objects, so initialize their controllers serially after all cards
            // and their saved positions are already on the desktop.
            foreach (var card in restoredCards)
            {
                if (_disposed)
                    return;
                await card.LoadAsync();
            }
        }

        private void OpenWidgetPicker_Click(object sender, RoutedEventArgs e) => App.ShowWidgetPickerWindow();

        private static double ScaleCoordinate(double coordinate, double sourceExtent, double targetExtent)
        {
            if (!double.IsFinite(coordinate) || !double.IsFinite(sourceExtent) || sourceExtent <= 0 ||
                !double.IsFinite(targetExtent) || targetExtent <= 0)
                return coordinate;

            return coordinate * targetExtent / sourceExtent;
        }

        private static WidgetSize ParseWidgetSize(string? sizeName) => sizeName?.Trim().ToLowerInvariant() switch
        {
            "small" => WidgetSize.Small,
            "large" => WidgetSize.Large,
            _ => WidgetSize.Medium
        };

        private static Microsoft.Windows.Widgets.WidgetSize[] ParseSupportedWidgetSizes(IEnumerable<string>? sizes) =>
            sizes?.Select(size => Enum.TryParse<WidgetSize>(size, ignoreCase: true, out var parsed) ? (WidgetSize?)parsed : null)
                .Where(size => size is not null)
                .Select(size => size!.Value)
                .Distinct()
                .ToArray() ?? [];

        private bool TryFindWidgetPlacement(WidgetSize size, out Windows.Foundation.Point placement)
        {
            var cell = DesktopItemsView.GetGridCellSize();
            var occupancy = WidgetCardControl.GetGridOccupancy(size, cell.Width, cell.Height);
            var insets = GetWidgetVisualInsets();
            var width = Math.Max(1, DesktopItemsView.ActualWidth);
            var height = Math.Max(1, DesktopItemsView.ActualHeight);
            var desiredX = Math.Floor(Math.Max(0, width - occupancy.Width) / cell.Width) * cell.Width;
            var found = DesktopLayoutEngine.TryFindAvailablePosition(
                new DesktopLayoutEngine.Point(desiredX + insets.Left, insets.Top),
                new DesktopLayoutEngine.Size(width, height),
                new DesktopLayoutEngine.Size(cell.Width, cell.Height),
                new DesktopLayoutEngine.Size(
                    Math.Max(1, occupancy.Width - insets.Left - insets.Right),
                    Math.Max(1, occupancy.Height - insets.Top - insets.Bottom)),
                GetWidgetBlockedBounds(),
                out var candidate,
                insets.Left);
            if (!found)
            {
                placement = default;
                return false;
            }

            placement = new Windows.Foundation.Point(candidate.X - insets.Left, candidate.Y - insets.Top);
            return true;
        }

        private List<DesktopLayoutEngine.Rect> GetWidgetBlockedBounds()
        {
            var blocked = GetDesktopIconBounds();
            foreach (var surface in _widgetSurfaces.Values.OrderBy(value => value.InstanceId, StringComparer.OrdinalIgnoreCase))
            {
                var x = Canvas.GetLeft(surface.LayoutElement);
                var y = Canvas.GetTop(surface.LayoutElement);
                if (double.IsFinite(x) && double.IsFinite(y))
                {
                    var bounds = GetWidgetCollisionBounds(surface.Card, x, y);
                    blocked.Add(new DesktopLayoutEngine.Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height));
                }
            }

            return blocked;
        }

        private List<DesktopLayoutEngine.Rect> GetDesktopIconBounds()
        {
            var bounds = new List<DesktopLayoutEngine.Rect>(Items.Count);
            if (!XiyiDesktop.AppSettingsStore.ShowDesktopIcons)
                return bounds;

            foreach (var item in Items)
            {
                if (!double.IsFinite(item.PositionX) || !double.IsFinite(item.PositionY))
                    continue;

                var padding = item.TilePadding;
                // TileWidth/TileHeight include the transparent hit-test padding used
                // for pointer comfort. Collision geometry follows the visible icon
                // and label so a nearby item is not displaced before the visuals meet.
                var itemBounds = new DesktopLayoutEngine.Rect(
                    item.PositionX + padding.Left,
                    item.PositionY + padding.Top,
                    Math.Max(1, item.TileWidth - padding.Left - padding.Right),
                    Math.Max(1, item.TileHeight - padding.Top - padding.Bottom));
                bounds.Add(itemBounds);
            }

            return bounds;
        }

        private void ClampWidgetPosition(WidgetCardControl card, ref double x, ref double y)
        {
            var width = Math.Max(1, DesktopItemsView.ActualWidth > 0 ? DesktopItemsView.ActualWidth : _desktopRoot.ActualWidth);
            var height = Math.Max(1, DesktopItemsView.ActualHeight > 0 ? DesktopItemsView.ActualHeight : _desktopRoot.ActualHeight);
            var occupancy = GetWidgetOccupancyBounds(card, 0, 0);
            const double edgePadding = 8;
            var position = DesktopLayoutEngine.Clamp(
                new DesktopLayoutEngine.Point(x, y),
                new DesktopLayoutEngine.Size(width, height),
                new DesktopLayoutEngine.Size(occupancy.Width, occupancy.Height),
                edgePadding);
            x = position.X;
            y = position.Y;
        }

        private void ApplyDesktopWidgetScale(bool persistPositions = true)
        {
            foreach (var surface in _widgetSurfaces.Values)
            {
                FitWidgetCardToDesktopGrid(surface.Card);
                UpdateWidgetLayoutElementSize(surface);
                var x = Canvas.GetLeft(surface.LayoutElement);
                var y = Canvas.GetTop(surface.LayoutElement);
                var previousX = x;
                var previousY = y;
                ClampWidgetPosition(surface.Card, ref x, ref y);
                SetWidgetPosition(surface, x, y, animate: x != previousX || y != previousY);
                UpdateWebWidgetOverlay(surface);
            }

            ResolveWidgetCollisions(persistChanges: false);
            if (persistPositions)
                SaveWidgetPlacements();
            RefreshSharedDesktopLayout();
            if (persistPositions)
                SaveManualDesktopOrder();
            App.WidgetPicker?.RefreshPlacementCheck();
        }

        private void FitWidgetCardToDesktopGrid(WidgetCardControl card)
        {
            var cell = DesktopItemsView.GetGridCellSize();
            card.FitDesktopScaleToGrid(cell.Width, cell.Height, GetWidgetVisualInsets());
        }

        private FrameworkElement CreateWidgetLayoutElement(WidgetCardControl card)
        {
            var cell = DesktopItemsView.GetGridCellSize();
            var occupancy = WidgetCardControl.GetGridOccupancy(card.Size, cell.Width, cell.Height);
            var host = new Grid
            {
                Width = occupancy.Width,
                Height = occupancy.Height
            };
            var content = new Viewbox
            {
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Child = card
            };
            host.Children.Add(new Border
            {
                Padding = GetWidgetVisualInsets(),
                Child = content
            });
            return host;
        }

        private void UpdateWidgetLayoutElementSize(WidgetDesktopSurface surface)
        {
            var occupancy = GetWidgetOccupancyBounds(surface.Card, 0, 0);
            surface.LayoutElement.Width = occupancy.Width;
            surface.LayoutElement.Height = occupancy.Height;
        }

        private void SetWidgetPosition(WidgetDesktopSurface surface, double x, double y, bool animate)
        {
            DesktopItemsView.SetExternalChildPosition(surface.LayoutElement, x, y, animate);
            surface.X = x;
            surface.Y = y;
            if (animate)
                TrackWidgetOverlayLayout(TimeSpan.FromMilliseconds(220));
        }

        private Dictionary<string, DesktopLayoutEngine.Point> GetWidgetPositionSnapshot()
        {
            var positions = new Dictionary<string, DesktopLayoutEngine.Point>(StringComparer.OrdinalIgnoreCase);
            foreach (var surface in _widgetSurfaces.Values)
            {
                var x = Canvas.GetLeft(surface.LayoutElement);
                var y = Canvas.GetTop(surface.LayoutElement);
                if (double.IsFinite(x) && double.IsFinite(y))
                    positions[surface.InstanceId] = new DesktopLayoutEngine.Point(x, y);
            }

            return positions;
        }

        private void ApplyWidgetPositionSnapshot(IReadOnlyDictionary<string, DesktopLayoutEngine.Point>? positions)
        {
            if (positions is null)
                return;

            foreach (var surface in _widgetSurfaces.Values)
            {
                if (!positions.TryGetValue(surface.InstanceId, out var position))
                    continue;
                SetWidgetPosition(surface, position.X, position.Y, animate: true);
                UpdateWebWidgetOverlay(surface);
            }
        }

        private void SaveWidgetPlacements() =>
            _widgetPlacementStore.UpsertRange(_widgetSurfaces.Values
                .OrderBy(surface => surface.InstanceId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(surface => surface.InstanceId, StringComparer.Ordinal)
                .Select(CreateWidgetPlacement));

        private WidgetDesktopPlacement CreateWidgetPlacement(WidgetDesktopSurface surface) =>
            surface.ToPlacement(
                surface.Card.Size.ToString(),
                Math.Max(1, DesktopItemsView.ActualWidth),
                Math.Max(1, DesktopItemsView.ActualHeight));

        private void TrackWidgetOverlayLayout(TimeSpan duration)
        {
            _widgetOverlayLayoutTrackingUntilUtc = DateTimeOffset.UtcNow + duration;
            if (_dispatcherQueue is null)
                return;

            _widgetOverlayLayoutTimer ??= _dispatcherQueue.CreateTimer();
            _widgetOverlayLayoutTimer.Interval = TimeSpan.FromMilliseconds(32);
            _widgetOverlayLayoutTimer.IsRepeating = true;
            _widgetOverlayLayoutTimer.Tick -= WidgetOverlayLayoutTimer_Tick;
            _widgetOverlayLayoutTimer.Tick += WidgetOverlayLayoutTimer_Tick;
            _widgetOverlayLayoutTimer.Start();
        }

        private void WidgetOverlayLayoutTimer_Tick(DispatcherQueueTimer sender, object args)
        {
            if (DateTimeOffset.UtcNow >= _widgetOverlayLayoutTrackingUntilUtc)
            {
                sender.Stop();
                foreach (var surface in _widgetSurfaces.Values)
                    UpdateWebWidgetOverlay(surface);
                return;
            }

            foreach (var surface in _widgetSurfaces.Values)
                UpdateWebWidgetOverlay(surface);
        }

        private Windows.Foundation.Rect GetWidgetOccupancyBounds(WidgetCardControl card, double x, double y)
        {
            var cell = DesktopItemsView.GetGridCellSize();
            var occupancy = WidgetCardControl.GetGridOccupancy(card.Size, cell.Width, cell.Height);
            return new Windows.Foundation.Rect(x, y, occupancy.Width, occupancy.Height);
        }

        private Windows.Foundation.Rect GetWidgetCollisionBounds(WidgetCardControl card, double x, double y)
        {
            var occupancy = GetWidgetOccupancyBounds(card, x, y);
            var insets = GetWidgetVisualInsets();
            return new Windows.Foundation.Rect(
                occupancy.X + insets.Left,
                occupancy.Y + insets.Top,
                Math.Max(1, occupancy.Width - insets.Left - insets.Right),
                Math.Max(1, occupancy.Height - insets.Top - insets.Bottom));
        }

        private Thickness GetWidgetVisualInsets()
        {
            const double widgetEdgeInset = 10;
            return new Thickness(widgetEdgeInset);
        }

        private bool ResolveWidgetCollisions(
            WidgetCardControl? priorityCard = null,
            bool persistChanges = true,
            IReadOnlyDictionary<string, DesktopLayoutEngine.Point>? requestedPositions = null,
            bool animateChanges = true,
            string? allowDesktopIconOverlapForInstanceId = null)
        {
            var surfaces = _widgetSurfaces.Values
                .OrderBy(surface => surface.InstanceId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(surface => surface.InstanceId, StringComparer.Ordinal)
                .ToList();
            if (priorityCard is not null)
                surfaces = surfaces.OrderBy(surface => ReferenceEquals(surface.Card, priorityCard) ? 0 : 1).ToList();

            var cell = DesktopItemsView.GetGridCellSize();
            var viewport = new DesktopLayoutEngine.Size(
                Math.Max(1, DesktopItemsView.ActualWidth > 0 ? DesktopItemsView.ActualWidth : _desktopRoot.ActualWidth),
                Math.Max(1, DesktopItemsView.ActualHeight > 0 ? DesktopItemsView.ActualHeight : _desktopRoot.ActualHeight));
            var contentInsets = GetWidgetVisualInsets();
            var requested = surfaces.Select(surface =>
            {
                var x = Canvas.GetLeft(surface.LayoutElement);
                var y = Canvas.GetTop(surface.LayoutElement);
                if (requestedPositions is not null && requestedPositions.TryGetValue(surface.InstanceId, out var requestedPosition))
                {
                    x = requestedPosition.X;
                    y = requestedPosition.Y;
                }
                var occupancy = GetWidgetCollisionBounds(surface.Card, 0, 0);
                return new DesktopLayoutEngine.Placement(
                    surface.InstanceId,
                    // Placement coordinates are host coordinates. The solver uses
                    // the visible card origin so transparent host padding cannot
                    // trigger a collision before the cards actually meet.
                    new DesktopLayoutEngine.Point(x + contentInsets.Left, y + contentInsets.Top),
                    new DesktopLayoutEngine.Size(occupancy.Width, occupancy.Height));
            }).ToArray();
            var resolved = DesktopLayoutEngine.ResolveWidgetCollisions(
                GetDesktopIconBounds(),
                requested,
                viewport,
                new DesktopLayoutEngine.Size(cell.Width, cell.Height),
                contentInsets.Left,
                allowDesktopIconOverlapForInstanceId);
            var resolvedById = resolved.ToDictionary(item => item.Identity, StringComparer.OrdinalIgnoreCase);
            var moved = new List<WidgetDesktopSurface>();
            foreach (var surface in surfaces)
            {
                if (!resolvedById.TryGetValue(surface.InstanceId, out var item))
                    continue;
                var currentX = Canvas.GetLeft(surface.LayoutElement);
                var currentY = Canvas.GetTop(surface.LayoutElement);
                if (Math.Abs(currentX + contentInsets.Left - item.Position.X) < 0.1 &&
                    Math.Abs(currentY + contentInsets.Top - item.Position.Y) < 0.1)
                    continue;
                var hostX = item.Position.X - contentInsets.Left;
                var hostY = item.Position.Y - contentInsets.Top;
                SetWidgetPosition(
                    surface,
                    hostX,
                    hostY,
                    animate: animateChanges && !ReferenceEquals(surface.Card, priorityCard));
                if (animateChanges)
                    UpdateWebWidgetOverlay(surface);
                moved.Add(surface);
            }

            if (persistChanges && moved.Count > 0)
                _widgetPlacementStore.UpsertRange(moved.Select(CreateWidgetPlacement));
            else if (!animateChanges && moved.Count > 0)
                TrackWidgetOverlayLayout(TimeSpan.FromMilliseconds(48));

            return moved.Count > 0;
        }

        private bool RefreshSharedDesktopLayout(bool isDragging = false)
        {
            if (isDragging)
            {
                DesktopItemsView.InvalidateArrange();
                TrackWidgetOverlayLayout(TimeSpan.FromMilliseconds(48));
                return false;
            }

            var cell = DesktopItemsView.GetGridCellSize();
            var key = string.Join(";", _widgetSurfaces.Values
                .OrderBy(surface => surface.InstanceId, StringComparer.OrdinalIgnoreCase)
                .Select(surface =>
                {
                    var x = Canvas.GetLeft(surface.LayoutElement);
                    var y = Canvas.GetTop(surface.LayoutElement);
                    return $"{surface.InstanceId}:{Math.Floor(x / cell.Width)}:{Math.Floor(y / cell.Height)}";
                }));
            _lastWidgetGridSlotKey = key;
            DesktopItemsView.AnimateNextLayout();
            DesktopItemsView.UpdateLayout();
            App.WidgetPicker?.RefreshPlacementCheck();
            return true;
        }

        private static WidgetResourceInfo? SelectWidgetIconResource(WidgetProviderInfo provider, WidgetDefinition definition, string theme)
        {
            var resources = definition.Resources.Concat(provider.ProviderIcons)
                .Where(resource => resource.Kind.Equals("Icon", StringComparison.OrdinalIgnoreCase));
            return resources.FirstOrDefault(resource => resource.Theme.Equals(theme, StringComparison.OrdinalIgnoreCase))
                ?? resources.FirstOrDefault(resource => resource.Theme.Equals("Provider", StringComparison.OrdinalIgnoreCase))
                ?? resources.FirstOrDefault(resource => resource.Theme.Equals("Default", StringComparison.OrdinalIgnoreCase))
                ?? resources.FirstOrDefault();
        }

        private static string? GetWidgetIconPath(WidgetDesktopSurface surface, ElementTheme theme) =>
            (theme == ElementTheme.Dark ? surface.DarkIconPath : surface.LightIconPath)
            ?? surface.DefaultIconPath
            ?? (theme == ElementTheme.Dark ? surface.LightIconPath : surface.DarkIconPath);

        private void AttachWidgetCardHandlers(WidgetCardControl card)
        {
            card.DeleteRequested += WidgetCard_DeleteRequested;
            card.SizeChangedByUser += widgetSize => WidgetCard_SizeChanged(card, widgetSize);
            card.SizeChanged += (_, _) =>
            {
                if (_dispatcherQueue is not null)
                    _dispatcherQueue.TryEnqueue(() =>
                    {
                        if (_widgetSurfaces.Values.FirstOrDefault(value => ReferenceEquals(value.Card, card)) is { } surface)
                        {
                            FitWidgetCardToDesktopGrid(card);
                            UpdateWidgetLayoutElementSize(surface);
                            var x = Canvas.GetLeft(surface.LayoutElement);
                            var y = Canvas.GetTop(surface.LayoutElement);
                            var previousX = x;
                            var previousY = y;
                            ClampWidgetPosition(card, ref x, ref y);
                            SetWidgetPosition(surface, x, y, animate: x != previousX || y != previousY);
                            UpdateWebWidgetOverlay(surface);
                            RefreshSharedDesktopLayout();
                        }
                    });
            };
            card.WebWidgetChanged += uri =>
            {
                var surface = _widgetSurfaces.Values.FirstOrDefault(value => ReferenceEquals(value.Card, card));
                if (surface is null)
                    return;

                surface.WebUri = uri;
                UpdateWebWidgetOverlay(surface);
            };
        }

        private void UpdateWebWidgetOverlay(WidgetDesktopSurface surface)
        {
            if (_disposed || surface.WebUri is null)
            {
                CloseWebWidgetOverlay(surface.InstanceId);
                return;
            }

            var parent = _hostController?.AttachedParentHandle ?? IntPtr.Zero;
            var siblingAbove = _hostController?.DesktopSurfaceHandle ?? IntPtr.Zero;
            if (parent == IntPtr.Zero || siblingAbove == IntPtr.Zero)
            {
                CloseWebWidgetOverlay(surface.InstanceId);
                return;
            }

            if (!TryGetWebWidgetScreenBounds(surface.Card, out var bounds))
            {
                return;
            }

            try
            {
                if (_webWidgetOverlays.TryGetValue(surface.InstanceId, out var existing))
                {
                    if (existing.IsHostedBy(parent, siblingAbove))
                    {
                        existing.SetUri(surface.WebUri);
                        existing.UpdateBounds(bounds, GetWidgetCornerRadiusPixels(surface.Card.DesktopScale));
                        return;
                    }

                    existing.CloseOverlay();
                    _webWidgetOverlays.Remove(surface.InstanceId);
                }

                var overlay = new WebWidgetOverlayWindow(parent, siblingAbove, surface.WebUri);
                overlay.InitializationFailed += (_, exception) => System.Diagnostics.Debug.WriteLine(
                    $"WebView2 overlay initialization failed: {exception}");
                overlay.NavigationCompleted += (_, args) =>
                {
                    if (!args.IsSuccess)
                        System.Diagnostics.Debug.WriteLine(
                            $"WebView2 overlay navigation failed: {args.WebErrorStatus}");
                };
                _webWidgetOverlays[surface.InstanceId] = overlay;
                overlay.UpdateBounds(bounds, GetWidgetCornerRadiusPixels(surface.Card.DesktopScale));
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"Could not create WebView2 overlay: {exception}");
            }
        }

        private bool TryGetWebWidgetScreenBounds(WidgetCardControl card, out Windows.Foundation.Rect bounds)
        {
            bounds = default;
            if (card.ActualWidth <= 0 || card.ActualHeight <= 0 ||
                !GetClientOriginOnScreen(_windowHandle, out var clientOrigin))
                return false;

            try
            {
                var transformedBounds = card.TransformToVisual(_desktopRoot).TransformBounds(
                    new Windows.Foundation.Rect(0, 0, card.ActualWidth, card.ActualHeight));
                var scale = _desktopRoot.XamlRoot?.RasterizationScale ?? 0;
                if (!double.IsFinite(scale) || scale <= 0)
                {
                    var dpi = GetDpiForWindow(_windowHandle);
                    scale = dpi > 0 ? dpi / 96d : 1d;
                }

                var x = clientOrigin.X + transformedBounds.X * scale;
                var y = clientOrigin.Y + transformedBounds.Y * scale;
                var width = transformedBounds.Width * scale;
                var height = transformedBounds.Height * scale;
                if (!double.IsFinite(x) || !double.IsFinite(y) || width <= 0 || height <= 0)
                    return false;

                bounds = new Windows.Foundation.Rect(x, y, width, height);
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private static bool GetClientOriginOnScreen(IntPtr hwnd, out NativePoint point)
        {
            point = default;
            return ClientToScreen(hwnd, ref point);
        }

        private double GetWidgetCornerRadiusPixels(double widgetScale)
        {
            var scale = _desktopRoot.XamlRoot?.RasterizationScale ?? 0;
            if (!double.IsFinite(scale) || scale <= 0)
            {
                var dpi = GetDpiForWindow(_windowHandle);
                scale = dpi > 0 ? dpi / 96d : 1d;
            }

            return AppSettingsStore.DesktopCornerRadius * scale * widgetScale;
        }

        private void CloseWebWidgetOverlay(string instanceId)
        {
            if (!_webWidgetOverlays.Remove(instanceId, out var overlay))
                return;

            overlay.CloseOverlay();
        }

        private void WidgetCard_SizeChanged(WidgetCardControl card, WidgetSize size)
        {
            if (_widgetSurfaces.Values.FirstOrDefault(surface => ReferenceEquals(surface.Card, card)) is not { } surface)
                return;

            FitWidgetCardToDesktopGrid(card);
            UpdateWidgetLayoutElementSize(surface);
            var x = Canvas.GetLeft(surface.LayoutElement);
            var y = Canvas.GetTop(surface.LayoutElement);
            ClampWidgetPosition(card, ref x, ref y);
            SetWidgetPosition(surface, x, y, animate: true);
            ResolveWidgetCollisions(card, persistChanges: false);
            UpdateWebWidgetOverlay(surface);
            SaveWidgetPlacements();
            RefreshSharedDesktopLayout();
            SaveManualDesktopOrder();
            App.WidgetPicker?.RefreshPlacementCheck();
        }

        private async void WidgetCard_DeleteRequested(object? sender, EventArgs e)
        {
            if (sender is not WidgetCardControl card)
                return;

            var surface = _widgetSurfaces.Values.FirstOrDefault(value => ReferenceEquals(value.Card, card));
            if (surface is null)
                return;

            _widgetSurfaces.Remove(surface.InstanceId);
            CloseWebWidgetOverlay(surface.InstanceId);
            DesktopItemsView.UnregisterExternalLayoutItem(surface.LayoutElement);
            DesktopItemsView.Children.Remove(surface.LayoutElement);
            _widgetPlacementStore.Remove(surface.InstanceId);
            RefreshSharedDesktopLayout();
            SaveManualDesktopOrder();
            await card.DeleteAsync();
            App.WidgetPicker?.RefreshPlacementCheck();
        }

        private void WidgetCard_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (e.OriginalSource is not DependencyObject source)
                return;

            var card = FindWidgetCard(source);
            if (card is null || !IsWidgetDragHandle(source, card))
                return;

            var point = e.GetCurrentPoint(DesktopItemsView);
            if (!point.Properties.IsLeftButtonPressed)
                return;

            _widgetDragSurface = card;
            _widgetDragPointerId = point.PointerId;
            _widgetDragStart = point.Position;
            _widgetDragOriginalPositions.Clear();
            foreach (var (instanceId, position) in GetWidgetPositionSnapshot())
                _widgetDragOriginalPositions[instanceId] = position;
            _widgetDragTransaction = new DesktopLayoutTransaction(_widgetDragOriginalPositions);
            _widgetDragIconPositions.Clear();
            foreach (var item in Items.Where(item => item.HasPosition))
                _widgetDragIconPositions[item] = new Windows.Foundation.Point(item.PositionX, item.PositionY);
            var dragSurface = _widgetSurfaces.Values.First(value => ReferenceEquals(value.Card, card));
            DesktopItemsView.BeginExternalItemDrag(dragSurface.LayoutElement);
            _lastWidgetGridSlotKey = null;
            RefreshSharedDesktopLayout();
            _widgetDragOrigin = new Windows.Foundation.Point(
                Canvas.GetLeft(dragSurface.LayoutElement),
                Canvas.GetTop(dragSurface.LayoutElement));
            card.CapturePointer(e.Pointer);
            e.Handled = true;
        }

        private void WidgetCard_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_widgetDragSurface is null || e.Pointer.PointerId != _widgetDragPointerId)
                return;

            var card = _widgetDragSurface;
            var point = e.GetCurrentPoint(DesktopItemsView).Position;
            var x = _widgetDragOrigin.X + point.X - _widgetDragStart.X;
            var y = _widgetDragOrigin.Y + point.Y - _widgetDragStart.Y;
            ClampWidgetPosition(card, ref x, ref y);
            if (_widgetSurfaces.Values.FirstOrDefault(value => ReferenceEquals(value.Card, card)) is { } surface)
            {
                SetWidgetPosition(surface, x, y, animate: false);
                RefreshSharedDesktopLayout(isDragging: true);

                // Reflow icons against the moving widget first, then resolve the
                // widget preview from the original snapshot so each pointer update
                // starts from the same stable layout.
                DesktopItemsView.UpdateLayout();
                var requestedPositions = new Dictionary<string, DesktopLayoutEngine.Point>(
                    _widgetDragOriginalPositions,
                    StringComparer.OrdinalIgnoreCase)
                {
                    [surface.InstanceId] = new DesktopLayoutEngine.Point(x, y)
                };
                ResolveWidgetCollisions(
                    card,
                    persistChanges: false,
                    requestedPositions: requestedPositions,
                    animateChanges: true,
                    allowDesktopIconOverlapForInstanceId: surface.InstanceId);
                _widgetDragTransaction?.UpdatePreview(GetWidgetPositionSnapshot());
                DesktopItemsView.InvalidateArrange();
                RefreshSharedDesktopLayout(isDragging: true);
                TrackWidgetOverlayLayout(TimeSpan.FromMilliseconds(48));
            }
            e.Handled = true;
        }

        private void WidgetCard_PointerReleased(object sender, PointerRoutedEventArgs e) => EndWidgetDrag(e, canceled: false);
        private void WidgetCard_PointerCanceled(object sender, PointerRoutedEventArgs e) => EndWidgetDrag(e, canceled: true);

        private void EndWidgetDrag(PointerRoutedEventArgs e, bool canceled)
        {
            if (_widgetDragSurface is null || e.Pointer.PointerId != _widgetDragPointerId)
                return;

            var card = _widgetDragSurface;
            card.ReleasePointerCapture(e.Pointer);
            var surface = _widgetSurfaces.Values.FirstOrDefault(value => ReferenceEquals(value.Card, card));
            if (surface is not null)
            {
                DesktopItemsView.EndExternalItemDrag(surface.LayoutElement, resetIconPositions: canceled);
                if (canceled)
                {
                    ApplyWidgetPositionSnapshot(_widgetDragTransaction?.Cancel());
                    foreach (var (item, originalPosition) in _widgetDragIconPositions)
                        item.SetPosition(originalPosition.X, originalPosition.Y);
                    DesktopItemsView.UpdateLayout();
                }
                else
                {
                    var x = Canvas.GetLeft(surface.LayoutElement);
                    var y = Canvas.GetTop(surface.LayoutElement);
                    if (XiyiDesktop.AppSettingsStore.AlignIconsToGrid)
                    {
                        var aligned = DesktopItemsView.ClampPositionToGrid(
                            new Windows.Foundation.Point(x, y),
                            new Windows.Foundation.Size(surface.LayoutElement.Width, surface.LayoutElement.Height));
                        x = aligned.X;
                        y = aligned.Y;
                    }
                    else
                    {
                        ClampWidgetPosition(card, ref x, ref y);
                    }
                    SetWidgetPosition(surface, x, y, animate: true);
                    ResolveWidgetCollisions(card, persistChanges: false);
                    _widgetDragTransaction?.UpdatePreview(GetWidgetPositionSnapshot());
                    ApplyWidgetPositionSnapshot(_widgetDragTransaction?.Commit());
                    SaveWidgetPlacements();
                }
                RefreshSharedDesktopLayout();
            }

            _widgetDragSurface = null;
            _widgetDragPointerId = 0;
            _widgetDragTransaction = null;
            _widgetDragOriginalPositions.Clear();
            _widgetDragIconPositions.Clear();
            if (!canceled)
                SaveManualDesktopOrder();
            App.WidgetPicker?.RefreshPlacementCheck();
            e.Handled = true;
        }

        private static WidgetCardControl? FindWidgetCard(DependencyObject source)
        {
            while (source is not null)
            {
                if (source is WidgetCardControl card)
                    return card;
                source = VisualTreeHelper.GetParent(source);
            }

            return null;
        }

        private bool IsInsideWidgetLayout(DependencyObject? source)
        {
            while (source is not null)
            {
                if (_widgetSurfaces.Values.Any(surface => ReferenceEquals(surface.LayoutElement, source)))
                    return true;
                source = VisualTreeHelper.GetParent(source);
            }

            return false;
        }

        private static bool IsWidgetDragHandle(DependencyObject source, WidgetCardControl card)
        {
            while (source is not null)
            {
                if (ReferenceEquals(source, card.DragHandle))
                    return true;
                if (source is Button)
                    return false;
                source = VisualTreeHelper.GetParent(source);
            }

            return false;
        }

        private sealed class WidgetDesktopSurface(
            string instanceId,
            string definitionId,
            string displayName,
            WidgetCardControl card,
            FrameworkElement layoutElement,
            WidgetProviderInfo? provider,
            double x,
            double y)
        {
            public string InstanceId { get; } = instanceId;
            public string DefinitionId { get; } = definitionId;
            public string DisplayName { get; } = displayName;
            public WidgetCardControl Card { get; } = card;
            public FrameworkElement LayoutElement { get; } = layoutElement;
            public WidgetProviderInfo? Provider { get; } = provider;
            public double X { get; set; } = x;
            public double Y { get; set; } = y;
            public string[]? SupportedSizes { get; set; }
            public string? LightIconPath { get; set; }
            public string? DarkIconPath { get; set; }
            public string? DefaultIconPath { get; set; }
            public Uri? WebUri { get; set; }

            public WidgetDesktopPlacement ToPlacement(string size, double viewportWidth, double viewportHeight) =>
                new(InstanceId, DefinitionId, DisplayName, size, X, Y, SupportedSizes, LightIconPath, DarkIconPath,
                    DefaultIconPath, CoordinateSpaceVersion: 1, ViewportWidth: viewportWidth, ViewportHeight: viewportHeight);
        }

        private void HostController_StateChanged(object? sender, DesktopHostStateChangedEventArgs e)
        {
            if (_disposed)
                return;

            if (e.State == DesktopHostState.Attached)
            {
                foreach (var surface in _widgetSurfaces.Values)
                    UpdateWebWidgetOverlay(surface);
            }
            else
            {
                foreach (var overlay in _webWidgetOverlays.Values)
                    overlay.HideOverlay();
            }
        }

        private void HostController_WindowHandleLost(object? sender, EventArgs e) => XiyiDesktop.App.RecreateDesktopWindow();

        private void ItemsService_ItemsChanged(object? sender, EventArgs e)
        {
            if (_disposed || _dispatcherQueue is null)
                return;

            try
            {
                _dispatcherQueue.TryEnqueue(() => _ = RefreshDesktopItemsAsync(showErrorDialog: false));
            }
            catch (Exception exception) when (exception is COMException || exception is ObjectDisposedException)
            {
                // The file watcher can report a change after the window's dispatcher
                // has started shutting down. That notification is no longer actionable.
            }
        }

        private void Clipboard_ContentChanged(object? sender, object e)
        {
            if (_disposed || _dispatcherQueue is null)
                return;

            var generation = Interlocked.Increment(ref _clipboardStateRefreshGeneration);
            _dispatcherQueue.TryEnqueue(() => _ = RefreshCutDesktopItemsAsync(generation));
        }

        private Task RefreshCutDesktopItemsAsync(int generation = 0)
        {
            if (_disposed)
                return Task.CompletedTask;

            if (generation == 0)
                generation = Interlocked.Increment(ref _clipboardStateRefreshGeneration);

            var nextCutPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var clipboard = DesktopFileOperationsService.GetClipboardState(out var clipboardPaths);
                if (clipboard.Action == DesktopClipboardAction.Move)
                {
                    foreach (var path in clipboardPaths)
                    {
                        if (string.IsNullOrWhiteSpace(path))
                            continue;

                        try
                        {
                            nextCutPaths.Add(Path.GetFullPath(path));
                        }
                        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
                        {
                            Debug.WriteLine($"Could not normalize clipboard cut path '{path}': {exception.Message}");
                        }
                    }
                }
            }
            catch (Exception exception) when (exception is COMException or InvalidOperationException or UnauthorizedAccessException or IOException)
            {
                Debug.WriteLine($"Could not read cut state from the clipboard: {exception.Message}");
            }

            if (_disposed || generation != Volatile.Read(ref _clipboardStateRefreshGeneration))
                return Task.CompletedTask;

            _cutDesktopItemPaths.Clear();
            foreach (var path in nextCutPaths)
                _cutDesktopItemPaths.Add(path);

            foreach (var item in Items)
                item.IsCut = item.Item.Path is { } path && _cutDesktopItemPaths.Contains(path);

            return Task.CompletedTask;
        }

        private async Task RefreshDesktopItemsAsync(bool showErrorDialog = true)
        {
            if (_disposed)
                return;

            _refreshCancellation?.Cancel();
            _refreshCancellation?.Dispose();
            _refreshCancellation = new CancellationTokenSource();
            var token = _refreshCancellation.Token;
            var generation = Interlocked.Increment(ref _refreshGeneration);

            try
            {
                var desktopItems = await _itemsService.GetItemsAsync(token);
                if (_disposed || token.IsCancellationRequested || generation != _refreshGeneration)
                    return;

                ClearDesktopSelection();
                Items.Clear();
                IReadOnlyList<DesktopItem> orderedItems;
                if (!_layoutStore.HasSavedOrder && !XiyiDesktop.AppSettingsStore.DesktopAutoArrange)
                {
                    orderedItems = desktopItems
                        .OrderBy(GetDesktopItemDisplayName, Comparer<string>.Create(CompareShellNames))
                        .ToArray();
                    _layoutStore.SaveOrder(orderedItems.Select(item => item.Identity));
                }
                else
                {
                    orderedItems = _layoutStore.ApplyOrder(desktopItems);
                }

                var viewModels = orderedItems.Select(item => new DesktopItemViewModel(item, _resources, GetDesktopIconSize())).ToArray();
                foreach (var viewModel in viewModels)
                {
                    if (_layoutStore.TryGetPosition(viewModel.Item.Identity, out DesktopLayoutStore.DesktopItemPosition position))
                    {
                        viewModel.SetPosition(
                            ScaleCoordinate(position.X, position.ViewportWidth, DesktopItemsView.ActualWidth),
                            ScaleCoordinate(position.Y, position.ViewportHeight, DesktopItemsView.ActualHeight));
                    }
                    viewModel.IsCut = viewModel.Item.Path is { } itemPath && _cutDesktopItemPaths.Contains(itemPath);
                }
                foreach (var item in viewModels)
                    Items.Add(item);

                DesktopItemsView.SetItems(viewModels);
                DesktopItemsView.SetViewportSize(new Windows.Foundation.Size(DesktopItemsView.ActualWidth, DesktopItemsView.ActualHeight));

                if (XiyiDesktop.AppSettingsStore.DesktopAutoArrange)
                    ReorderDesktopItems(XiyiDesktop.AppSettingsStore.DesktopSortMode, persistLayout: false);
                else
                {
                    var viewportWidth = Math.Max(1, DesktopItemsView.ActualWidth);
                    var viewportHeight = Math.Max(1, DesktopItemsView.ActualHeight);
                    var positionsWithoutViewport = viewModels
                        .Where(item => item.HasPosition &&
                            _layoutStore.TryGetPosition(item.Item.Identity, out DesktopLayoutStore.DesktopItemPosition saved) &&
                            (saved.ViewportWidth <= 0 || saved.ViewportHeight <= 0))
                        .Select(item => new DesktopLayoutStore.DesktopItemPosition(
                            item.Item.Identity,
                            item.PositionX,
                            item.PositionY,
                            viewportWidth,
                            viewportHeight))
                        .ToArray();
                    if (positionsWithoutViewport.Length > 0)
                        _layoutStore.SaveLayout(viewModels.Select(item => item.Item.Identity), positionsWithoutViewport);
                    else
                        _layoutStore.SaveOrder(viewModels.Select(item => item.Item.Identity));
                }

                DesktopItemsView.UpdateLayout();
                if (!XiyiDesktop.AppSettingsStore.DesktopAutoArrange)
                {
                    var untrackedPositions = viewModels
                        .Where(item => item.HasPosition &&
                            !_layoutStore.TryGetPosition(item.Item.Identity, out DesktopLayoutStore.DesktopItemPosition _))
                        .Select(item => new DesktopLayoutStore.DesktopItemPosition(
                            item.Item.Identity,
                            item.PositionX,
                            item.PositionY,
                            Math.Max(1, DesktopItemsView.ActualWidth),
                            Math.Max(1, DesktopItemsView.ActualHeight)))
                        .ToArray();
                    if (untrackedPositions.Length > 0)
                        _layoutStore.SaveLayout(viewModels.Select(item => item.Item.Identity), untrackedPositions);
                }
                ApplyDesktopWidgetScale(persistPositions: false);
                _initialDesktopItemsLayoutReady.TrySetResult(true);

                await LoadDesktopItemIconsAsync(viewModels, GetDesktopIconPixelSize(), token);
                App.WidgetPicker?.RefreshPlacementCheck();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"Could not enumerate desktop items: {exception}");
                if (showErrorDialog)
                    await ShowOperationErrorAsync();
            }
            finally
            {
                if (!_disposed && generation == Volatile.Read(ref _refreshGeneration))
                    _initialDesktopItemsLayoutReady.TrySetResult(true);
            }
        }

        private void SubscribeToXamlRootChanges()
        {
            var xamlRoot = _desktopRoot.XamlRoot;
            if (xamlRoot is null || ReferenceEquals(xamlRoot, _subscribedXamlRoot))
                return;

            if (_subscribedXamlRoot is not null)
                _subscribedXamlRoot.Changed -= DesktopXamlRoot_Changed;

            _subscribedXamlRoot = xamlRoot;
            _observedRasterizationScale = xamlRoot.RasterizationScale;
            xamlRoot.Changed += DesktopXamlRoot_Changed;
            LogDesktopDpiState("XamlRoot attached");
        }

        private void DesktopXamlRoot_Changed(XamlRoot sender, XamlRootChangedEventArgs args)
        {
            var currentScale = sender.RasterizationScale;
            _hostController?.RefreshLayout();
            if (!double.IsFinite(currentScale) || currentScale <= 0)
            {
                return;
            }

            if (Math.Abs(currentScale - _observedRasterizationScale) < 0.01)
                return;

            var previousScale = _observedRasterizationScale;
            _observedRasterizationScale = currentScale;
            LogDesktopDpiState($"XamlRoot scale changed {previousScale:F3} -> {currentScale:F3}");
            UpdateDesktopWorkAreaMargins();
            foreach (var surface in _widgetSurfaces.Values)
                UpdateWebWidgetOverlay(surface);

            _iconRefreshCancellation?.Cancel();
            _iconRefreshCancellation?.Dispose();
            _iconRefreshCancellation = new CancellationTokenSource();
            _ = LoadDesktopItemIconsAsync(Items.ToArray(), GetDesktopIconPixelSize(), _iconRefreshCancellation.Token);
        }

        private void DesktopItemsView_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            DesktopItemsView.SetViewportSize(e.NewSize);
            if (!XiyiDesktop.AppSettingsStore.DesktopAutoArrange)
            {
                foreach (var item in Items)
                {
                    if (_layoutStore.TryGetPosition(item.Item.Identity, out DesktopLayoutStore.DesktopItemPosition position))
                    {
                        item.SetPosition(
                            ScaleCoordinate(position.X, position.ViewportWidth, e.NewSize.Width),
                            ScaleCoordinate(position.Y, position.ViewportHeight, e.NewSize.Height));
                    }
                    if (!XiyiDesktop.AppSettingsStore.AlignIconsToGrid && item.HasPosition)
                        DesktopItemsView.ClampItemPosition(item);
                }
            }

            var savedWidgetPositions = _widgetPlacementStore.GetAll()
                .ToDictionary(placement => placement.InstanceId, StringComparer.OrdinalIgnoreCase);
            foreach (var surface in _widgetSurfaces.Values)
            {
                if (savedWidgetPositions.TryGetValue(surface.InstanceId, out var placement))
                {
                    var viewportWidth = Math.Max(1, e.NewSize.Width);
                    var viewportHeight = Math.Max(1, e.NewSize.Height);
                    var x = ScaleCoordinate(placement.X, placement.ViewportWidth, viewportWidth);
                    var y = ScaleCoordinate(placement.Y, placement.ViewportHeight, viewportHeight);
                    if (placement.CoordinateSpaceVersion < 1)
                    {
                        x -= DesktopSurface.Margin.Left;
                        y -= DesktopSurface.Margin.Top;
                        placement = placement with
                        {
                            X = x,
                            Y = y,
                            CoordinateSpaceVersion = 1,
                            ViewportWidth = viewportWidth,
                            ViewportHeight = viewportHeight
                        };
                        _widgetPlacementStore.Upsert(placement);
                    }
                    SetWidgetPosition(surface, x, y, animate: false);
                }
            }

            // Resizing the desktop surface is only a presentation adjustment. Keep
            // stored positions intact so returning to the previous viewport restores them.
            ApplyDesktopWidgetScale(persistPositions: false);
        }

        private void DesktopRoot_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            if ((e.KeyModifiers & Windows.System.VirtualKeyModifiers.Control) == 0)
                return;

            var wheelDelta = e.GetCurrentPoint(_desktopRoot).Properties.MouseWheelDelta;
            if (wheelDelta == 0)
                return;

            var currentSize = GetDesktopIconSize();
            var nextSize = Math.Clamp(
                currentSize + wheelDelta / 120.0 * DesktopIconMetrics.WheelStep,
                MinimumDesktopIconSize,
                MaximumDesktopIconSize);
            if (Math.Abs(nextSize - currentSize) < 0.01)
                return;

            XiyiDesktop.AppSettingsStore.DesktopIconCustomSize = nextSize;
            ApplyDesktopIconSizeToItems(nextSize);
            UpdateViewMenuItems();
            e.Handled = true;
        }

        private uint GetDesktopIconPixelSize()
        {
            var scale = _desktopRoot.XamlRoot?.RasterizationScale ?? 1.0;
            var pixelSize = Math.Ceiling(GetDesktopIconSize() * scale);
            return (uint)Math.Clamp(pixelSize, MinimumDesktopIconSize, 512);
        }

        private static double GetDesktopIconSize() => XiyiDesktop.AppSettingsStore.DesktopIconCustomSize ??
            (XiyiDesktop.AppSettingsStore.DesktopIconSize switch
            {
                "Small" => DesktopIconMetrics.Small,
                "Large" => DesktopIconMetrics.Large,
                _ => DesktopIconMetrics.Medium
            });

        private void ScheduleDesktopIconImageRefresh()
        {
            if (_dispatcherQueue is null || _disposed)
                return;

            _iconSizeRefreshTimer ??= CreateIconSizeRefreshTimer();
            _iconSizeRefreshTimer.Stop();
            _iconSizeRefreshTimer.Start();
        }

        private DispatcherQueueTimer CreateIconSizeRefreshTimer()
        {
            var timer = _dispatcherQueue!.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(180);
            timer.IsRepeating = false;
            timer.Tick += (_, _) =>
            {
                if (_disposed)
                    return;

                _iconRefreshCancellation?.Cancel();
                _iconRefreshCancellation?.Dispose();
                _iconRefreshCancellation = new CancellationTokenSource();
                _ = LoadDesktopItemIconsAsync(Items.ToArray(), GetDesktopIconPixelSize(), _iconRefreshCancellation.Token);
            };
            return timer;
        }

        private async Task LoadDesktopItemIconsAsync(
            IEnumerable<DesktopItemViewModel> viewModels,
            uint pixelSize,
            CancellationToken cancellationToken)
        {
            await Task.WhenAll(viewModels.Select(async viewModel =>
            {
                try
                {
                    await _itemsService.LoadIconAsync(viewModel.Item, pixelSize, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                }
            }));
        }

        internal void UpdateShortcutArrowSetting(bool showShortcutArrows)
        {
            foreach (var item in Items)
                item.SetShortcutArrowVisibility(showShortcutArrows);
        }

        private void LogDesktopDpiState(string reason)
        {
            var scale = _desktopRoot.XamlRoot?.RasterizationScale ?? 0;
            var windowDpi = _windowHandle == IntPtr.Zero ? 0 : GetDpiForWindow(_windowHandle);
            System.Diagnostics.Debug.WriteLine(
                $"Desktop DPI ({reason}): XamlRoot={scale:F3}, HWND={windowDpi / 96.0:F3}.");
        }

        private async void DesktopItem_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (DesktopClickBehavior.OpensOnSingleClick ||
                sender is not FrameworkElement { DataContext: DesktopItemViewModel viewModel })
                return;

            await OpenItemAsync(viewModel.Item);
            e.Handled = true;
        }

        private void DesktopItem_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            RememberPenPointerPoint(e);
            if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Pen)
                PenHapticsFeedback.Send(e.GetCurrentPoint(null), PenHapticAction.Hover);
            if (sender is FrameworkElement element)
                SetDesktopItemPointerFeedback(element, 0.12);
        }

        private void DesktopItem_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (sender is FrameworkElement element &&
                (element.DataContext is not DesktopItemViewModel item || _positionDragItem != item))
                SetDesktopItemPointerFeedback(element, 0);
        }

        private void DesktopItem_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement element || element.DataContext is not DesktopItemViewModel item)
                return;

            SendLongContextHaptic(e.PointerDeviceType);

            SelectForContextMenu(item);
            // Use a common coordinate space for both the desktop and item flyouts;
            // the item template is transformed inside the items surface and can otherwise
            // shift the popup away from the actual pointer location.
            var point = GetDpiAwareFlyoutPosition(e);
            var preferences = _contextMenuPreferences.Read();
            e.Handled = true;
            if (preferences.SupportsCustomContextMenus && preferences.IsEnabled("DemoteTopBar\\Enabled") != false)
            {
                var menu = CreateItemContextFlyout();
                _activeContextFlyout = menu;
                ShowContextFlyout(menu, point);
                _ = RefreshItemApplicationExtensionsAsync(menu, GetSelectedItems());
            }
            else
            {
                if (HasLegacyItemCommandBarContent())
                {
                    var flyout = CreateLegacyItemCommandBarFlyout();
                    UpdateLegacyItemCommandVisibility(preferences);
                    _activeContextFlyout = flyout;
                    ShowContextFlyout(flyout, point);
                    _ = RefreshLegacyItemApplicationExtensionsAsync(flyout, _legacyItemApplicationExtensionsFlyout);
                }
                else
                {
                    var menu = CreateItemContextFlyout();
                    _activeContextFlyout = menu;
                    ShowContextFlyout(menu, point);
                    _ = RefreshItemApplicationExtensionsAsync(menu, GetSelectedItems());
                }
            }
        }

        private void DesktopItemsView_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            var item = FindItemViewModel(e.OriginalSource as DependencyObject);
            if (item is not null)
                SelectForContextMenu(item);
        }

        private void DesktopItem_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            RememberPenPointerPoint(e);
            if (sender is not FrameworkElement { DataContext: DesktopItemViewModel item } element)
                return;

            var point = e.GetCurrentPoint(GetDesktopItemsCoordinateTarget());
            if (!point.Properties.IsLeftButtonPressed)
                return;

            if (!_selectedDesktopItems.Contains(item))
            {
                ClearDesktopSelection();
                SetDesktopItemSelected(item, true);
            }

            SetDesktopItemPointerFeedback(element, 0.24);

            _positionDragItem = item;
            _positionDragItems = _selectedDesktopItems.Contains(item)
                ? Items.Where(_selectedDesktopItems.Contains).ToArray()
                : [item];
            if (_positionDragItems.Length == 0)
                _positionDragItems = [item];
            _positionDragOrigins.Clear();
            _positionDragSavedPositions.Clear();
            _positionDragWidgetPositions.Clear();
            foreach (var surface in _widgetSurfaces.Values)
            {
                var widgetX = Canvas.GetLeft(surface.LayoutElement);
                var widgetY = Canvas.GetTop(surface.LayoutElement);
                if (double.IsFinite(widgetX) && double.IsFinite(widgetY))
                    _positionDragWidgetPositions[surface.InstanceId] = new DesktopLayoutEngine.Point(widgetX, widgetY);
            }
            foreach (var draggedItem in _positionDragItems)
            {
                _positionDragSavedPositions[draggedItem] = new Windows.Foundation.Point(
                    draggedItem.PositionX,
                    draggedItem.PositionY);
                var originX = draggedItem.PositionX;
                var originY = draggedItem.PositionY;
                if (DesktopItemsView.TryGetItemBounds(draggedItem, DesktopItemsView, out var bounds))
                {
                    originX = bounds.X;
                    originY = bounds.Y;
                }
                if (!double.IsFinite(originX))
                    originX = 0;
                if (!double.IsFinite(originY))
                    originY = 0;
                _positionDragOrigins[draggedItem] = new Windows.Foundation.Point(originX, originY);
            }
            _positionDragPointerId = point.PointerId;
            _positionDragStart = point.Position;
            _positionDragLastPointerPosition = point.Position;
            _positionDragLastMoveTimestamp = Stopwatch.GetTimestamp();
            _positionDragSmoothedSpeed = 0;
            _positionDragMoved = false;
            _desktopSurfacePointerMode = XiyiDesktop.AppSettingsStore.DesktopAutoArrange;
            element.CapturePointer(e.Pointer);
        }

        private void DesktopItem_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            RememberPenPointerPoint(e);
            if (_positionDragItem is null || e.Pointer.PointerId != _positionDragPointerId ||
                sender is not FrameworkElement element)
                return;

            var point = e.GetCurrentPoint(GetDesktopItemsCoordinateTarget()).Position;
            var deltaX = point.X - _positionDragStart.X;
            var deltaY = point.Y - _positionDragStart.Y;
            if (!_positionDragMoved && Math.Sqrt(deltaX * deltaX + deltaY * deltaY) < 4)
                return;

            var now = Stopwatch.GetTimestamp();
            var elapsedMilliseconds = Stopwatch.GetElapsedTime(_positionDragLastMoveTimestamp, now).TotalMilliseconds;
            if (elapsedMilliseconds > 0)
            {
                var moveX = point.X - _positionDragLastPointerPosition.X;
                var moveY = point.Y - _positionDragLastPointerPosition.Y;
                var instantaneousSpeed = Math.Sqrt(moveX * moveX + moveY * moveY) / elapsedMilliseconds;
                _positionDragSmoothedSpeed = _positionDragSmoothedSpeed <= 0
                    ? instantaneousSpeed
                    : _positionDragSmoothedSpeed * 0.65 + instantaneousSpeed * 0.35;
            }
            _positionDragLastPointerPosition = point;
            _positionDragLastMoveTimestamp = now;
            var reflowDelayMilliseconds = Math.Clamp(220 - _positionDragSmoothedSpeed * 100, 70, 220);

            var firstDragMove = !_positionDragMoved;
            if (firstDragMove)
            {
                DesktopItemsView.BeginItemDrag(_positionDragItems);
                if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Pen)
                    PenHapticsFeedback.Send(e.GetCurrentPoint(null), PenHapticAction.Press);
            }
            _positionDragMoved = true;
            foreach (var draggedItem in _positionDragItems)
            {
                if (_positionDragOrigins.TryGetValue(draggedItem, out var origin))
                    draggedItem.SetPosition(origin.X + deltaX, origin.Y + deltaY);
            }
            var overRecycleBin = IsPointOverRecycleBin(point);
            _positionDragOverRecycleBin = overRecycleBin;
            _pendingIconDragReflowDelayMilliseconds = reflowDelayMilliseconds;
            var copyModifier = (e.KeyModifiers & Windows.System.VirtualKeyModifiers.Shift) != 0 &&
                _positionDragItems.All(item => item.Item.Path is not null);
            var operationGlyph = overRecycleBin
                ? "\uE74D"
                : copyModifier
                    ? "\uE8C8"
                    : "\uE8AD";
            var operationText = GetResourceText(overRecycleBin
                ? "DesktopDrag_Delete"
                : copyModifier
                    ? "DesktopDrag_Copy"
                    : "DesktopDrag_Move");
            foreach (var draggedItem in _positionDragItems)
                draggedItem.SetDragOperation(operationGlyph, operationText);
            QueueIconDragPreview();
            e.Handled = true;
        }

        private void QueueIconDragPreview()
        {
            if (_iconDragPreviewQueued)
                return;

            if (_dispatcherQueue is null)
            {
                ProcessIconDragPreview();
                return;
            }

            _iconDragPreviewTimer ??= _dispatcherQueue.CreateTimer();
            _iconDragPreviewTimer.Interval = TimeSpan.FromMilliseconds(16);
            _iconDragPreviewTimer.IsRepeating = false;
            _iconDragPreviewTimer.Tick -= IconDragPreviewTimer_Tick;
            _iconDragPreviewTimer.Tick += IconDragPreviewTimer_Tick;
            _iconDragPreviewQueued = true;
            _iconDragPreviewTimer.Start();
        }

        private void IconDragPreviewTimer_Tick(DispatcherQueueTimer sender, object args)
        {
            sender.Stop();
            ProcessIconDragPreview();
        }

        private void ProcessIconDragPreview()
        {
            _iconDragPreviewTimer?.Stop();
            _iconDragPreviewQueued = false;
            if (_positionDragItem is null || !_positionDragMoved || _positionDragItems.Length == 0)
                return;

            try
            {
                ResolveWidgetCollisions(
                    persistChanges: false,
                    requestedPositions: _positionDragWidgetPositions,
                    animateChanges: false);
                DesktopItemsView.UpdateDragReflow(
                    _positionDragOverRecycleBin,
                    _pendingIconDragReflowDelayMilliseconds);
                DesktopItemsView.InvalidateArrange();
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"Could not update the desktop drag preview: {exception}");
            }
        }

        private async void DesktopItem_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_positionDragItem is null || e.Pointer.PointerId != _positionDragPointerId)
                return;

            var releasedItem = _positionDragItem;
            var releaseElement = sender as FrameworkElement;
            var releasePosition = releaseElement is null ? default : e.GetCurrentPoint(releaseElement).Position;
            var pointerRemainsOverItem = releaseElement is not null &&
                releasePosition.X >= 0 && releasePosition.X <= releaseElement.ActualWidth &&
                releasePosition.Y >= 0 && releasePosition.Y <= releaseElement.ActualHeight;

            if (sender is UIElement element)
                element.ReleasePointerCapture(e.Pointer);

            if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Pen)
                PenHapticsFeedback.Send(e.GetCurrentPoint(null), PenHapticAction.Release);

            ProcessIconDragPreview();

            if (releaseElement is not null)
                SetDesktopItemPointerFeedback(releaseElement, pointerRemainsOverItem ? 0.12 : 0);

            if (_positionDragMoved)
            {
                if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Pen)
                    PenHapticsFeedback.Send(e.GetCurrentPoint(null), PenHapticAction.Success);
                var releasePoint = e.GetCurrentPoint(GetDesktopItemsCoordinateTarget()).Position;
                var recycleBin = FindRecycleBinAt(releasePoint);
                var draggedPaths = GetSelectedItems()
                    .Select(item => item.Path)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Cast<string>()
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (draggedPaths.Length == 0 && _positionDragItem.Item.Path is { } singlePath)
                    draggedPaths = [singlePath];

                var wasAutoArranged = _desktopSurfacePointerMode;
                var copyInsteadOfMove = recycleBin is null &&
                    (e.KeyModifiers & Windows.System.VirtualKeyModifiers.Shift) != 0 &&
                    draggedPaths.Length > 0 && _positionDragItems.All(item => item.Item.Path is not null);
                if (copyInsteadOfMove)
                {
                    foreach (var draggedItem in _positionDragItems)
                    {
                        if (_positionDragSavedPositions.TryGetValue(draggedItem, out var origin) &&
                            double.IsFinite(origin.X) && double.IsFinite(origin.Y))
                            draggedItem.SetPosition(origin.X, origin.Y);
                    }
                    RestoreIconDragWidgetPositions();
                    DesktopItemsView.EndItemDrag(_positionDragItems);
                    if (!wasAutoArranged)
                        SaveManualDesktopOrder();
                    e.Handled = true;
                    ResetItemDragState();
                    try
                    {
                        var copyResult = await DesktopFileOperationsService.CopyToAsync(
                            draggedPaths,
                            GetDesktopPath(),
                            cut: false,
                            _windowHandle);
                        if (copyResult.Status == DesktopFileOperationStatus.Completed)
                        {
                            SendPenHaptic(PenHapticAction.Success);
                            await RefreshDesktopItemsAsync();
                        }
                    }
                    catch (Exception exception)
                    {
                        System.Diagnostics.Debug.WriteLine($"Could not copy the dragged desktop items: {exception}");
                        await ShowOperationErrorAsync();
                    }
                    return;
                }

                if (recycleBin is null && wasAutoArranged)
                {
                    var targetOrder = CreateAutoArrangeDropOrder(
                        _positionDragItems,
                        DesktopItemsView.GetAutoArrangeDropIndexForDrop());
                    XiyiDesktop.AppSettingsStore.DesktopAutoArrange = true;
                    XiyiDesktop.AppSettingsStore.DesktopSortMode = "Manual";
                    XiyiDesktop.AppSettingsStore.DesktopSortDescending = false;
                    _sortDescending = false;
                    ReplaceDesktopItemOrder(targetOrder);
                    _layoutStore.SaveOrder(targetOrder.Select(item => item.Item.Identity));
                    UpdateSortMenuItems();
                    UpdateViewMenuItems();
                }
                else if (!wasAutoArranged)
                {
                    if (XiyiDesktop.AppSettingsStore.AlignIconsToGrid)
                        DesktopItemsView.SnapItemsToGrid(_positionDragItems, _positionDragItem);
                    DesktopItemsView.ClampItemsToViewport(_positionDragItems);
                    if (recycleBin is null)
                    {
                        DesktopItemsView.CommitDragReflowNow();
                        DesktopItemsView.UpdateLayout();
                        ResolveWidgetCollisions(requestedPositions: _positionDragWidgetPositions);
                        RefreshSharedDesktopLayout();
                    }
                }

                DesktopItemsView.EndItemDrag(_positionDragItems);
                if (recycleBin is null)
                {
                    DesktopItemsView.UpdateLayout();
                    ResolveWidgetCollisions(requestedPositions: _positionDragWidgetPositions);
                    RefreshSharedDesktopLayout();
                    SaveWidgetPlacements();
                }
                else
                {
                    RestoreIconDragWidgetPositions();
                }
                if (recycleBin is null)
                    SendPenHaptic(PenHapticAction.Success);
                if (recycleBin is null)
                    SaveManualDesktopOrder();
                e.Handled = true;

                ResetItemDragState();

                if (recycleBin is not null && draggedPaths.Length > 0)
                {
                    try
                    {
                        await DesktopFileOperationsService.RecycleAsync(draggedPaths, _windowHandle);
                        SendPenHaptic(PenHapticAction.Success);
                        UpdateUndoDeleteVisibility();
                        await RefreshDesktopItemsAsync();
                    }
                    catch (Exception exception)
                    {
                        System.Diagnostics.Debug.WriteLine($"Could not recycle the dragged desktop item: {exception}");
                        await ShowOperationErrorAsync();
                    }
                }
                return;
            }

            ResetItemDragState();

            if (DesktopClickBehavior.OpensOnSingleClick)
            {
                e.Handled = true;
                await OpenItemAsync(releasedItem.Item);
            }
        }

        private void DesktopItem_PointerCanceled(object sender, PointerRoutedEventArgs e)
        {
            if (_positionDragItem is null || e.Pointer.PointerId != _positionDragPointerId)
                return;

            if (sender is UIElement element)
                element.ReleasePointerCapture(e.Pointer);
            if (sender is FrameworkElement itemElement)
                SetDesktopItemPointerFeedback(itemElement, 0);
            if (_positionDragMoved)
            {
                foreach (var item in _positionDragItems)
                {
                    if (_positionDragSavedPositions.TryGetValue(item, out var originalPosition))
                        item.SetPosition(originalPosition.X, originalPosition.Y);
                }
                RestoreIconDragWidgetPositions();
                DesktopItemsView.EndItemDrag(_positionDragItems);
                DesktopItemsView.UpdateLayout();
            }
            ResetItemDragState();
        }

        private DesktopItemViewModel[] CreateAutoArrangeDropOrder(
            IReadOnlyList<DesktopItemViewModel> draggedItems,
            int insertionIndex)
        {
            var draggedSet = draggedItems.ToHashSet();
            var ordered = Items.Where(item => !draggedSet.Contains(item)).ToList();
            var orderedDraggedItems = Items.Where(draggedSet.Contains).ToArray();
            ordered.InsertRange(Math.Clamp(insertionIndex, 0, ordered.Count), orderedDraggedItems);
            return ordered.ToArray();
        }

        private void ReplaceDesktopItemOrder(IReadOnlyList<DesktopItemViewModel> orderedItems)
        {
            _isApplyingItemOrder = true;
            try
            {
                Items.Clear();
                foreach (var item in orderedItems)
                    Items.Add(item);
            }
            finally
            {
                _isApplyingItemOrder = false;
            }

            DesktopItemsView.ReorderItems(orderedItems);
        }

        private void ResetItemDragState()
        {
            _iconDragPreviewTimer?.Stop();
            if (_iconDragPreviewTimer is not null)
                _iconDragPreviewTimer.Tick -= IconDragPreviewTimer_Tick;
            _iconDragPreviewQueued = false;
            _positionDragOverRecycleBin = false;
            HideExternalDragOperationCue();
            foreach (var item in _positionDragItems)
                item.SetDragOperation(string.Empty, string.Empty);
            _positionDragItem = null;
            _positionDragItems = [];
            _positionDragOrigins.Clear();
            _positionDragSavedPositions.Clear();
            _positionDragWidgetPositions.Clear();
            _positionDragMoved = false;
            _desktopSurfacePointerMode = false;
        }

        private void RestoreIconDragWidgetPositions()
        {
            if (_positionDragWidgetPositions.Count == 0)
                return;

            foreach (var surface in _widgetSurfaces.Values)
            {
                if (!_positionDragWidgetPositions.TryGetValue(surface.InstanceId, out var position))
                    continue;
                SetWidgetPosition(surface, position.X, position.Y, animate: false);
                UpdateWebWidgetOverlay(surface);
            }

            DesktopItemsView.UpdateLayout();
        }

        private bool IsPointOverRecycleBin(Windows.Foundation.Point point) =>
            DesktopItemsView.GetTopmostItemAtPoint(point, _positionDragItems)?.Item.Kind == DesktopItemKind.RecycleBin;

        private DesktopItemViewModel? FindRecycleBinAt(Windows.Foundation.Point point) =>
            DesktopItemsView.GetTopmostItemAtPoint(point, _positionDragItems) is { Item.Kind: DesktopItemKind.RecycleBin } recycleBin
                ? recycleBin
                : null;

        private void DesktopRoot_DragOver(object sender, DragEventArgs e)
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                e.AcceptedOperation = DataPackageOperation.None;
                HideExternalDragOperationCue();
                return;
            }

            var operation = GetExternalDropOperation(e);
            if (operation == DataPackageOperation.None)
            {
                e.AcceptedOperation = DataPackageOperation.None;
                HideExternalDragOperationCue();
                return;
            }
            e.AcceptedOperation = operation;
            var text = GetResourceText(operation == DataPackageOperation.Move ? "DesktopDrag_Move" : "DesktopDrag_Copy");
            var point = e.GetPosition(_desktopRoot);
            ShowDragOperationCue(
                operation == DataPackageOperation.Move ? "\uE8AD" : "\uE8C8",
                text,
                pointerPosition: point);
        }

        private void DesktopRoot_DragLeave(object sender, DragEventArgs e) => HideExternalDragOperationCue();

        private async void DesktopRoot_Drop(object sender, DragEventArgs e)
        {
            HideExternalDragOperationCue();
            if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                e.AcceptedOperation = DataPackageOperation.None;
                return;
            }

            try
            {
                var storageItems = await e.DataView.GetStorageItemsAsync();
                var paths = storageItems
                    .Select(item => item.Path)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (paths.Length == 0)
                {
                    e.AcceptedOperation = DataPackageOperation.None;
                    return;
                }

                var operation = GetExternalDropOperation(e);
                e.AcceptedOperation = operation;
                var result = await DesktopFileOperationsService.CopyToAsync(
                    paths,
                    GetDesktopPath(),
                    cut: operation == DataPackageOperation.Move,
                    _windowHandle);
                if (result.Status == DesktopFileOperationStatus.Completed)
                    await RefreshDesktopItemsAsync();
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"Could not add dropped files to the desktop: {exception}");
                await ShowOperationErrorAsync();
            }
            finally
            {
                HideExternalDragOperationCue();
            }
        }

        private static DataPackageOperation GetExternalDropOperation(DragEventArgs e)
        {
            if (IsCopyModifierPressed(e))
                return DataPackageOperation.Copy;
            if ((e.DataView.RequestedOperation & DataPackageOperation.Move) != 0)
                return DataPackageOperation.Move;
            return DataPackageOperation.Copy;
        }

        private static bool IsCopyModifierPressed(DragEventArgs e)
        {
            var modifiers = e.Modifiers.ToString();
            return modifiers.Contains("Shift", StringComparison.OrdinalIgnoreCase) ||
                modifiers.Contains("Control", StringComparison.OrdinalIgnoreCase);
        }

        private void HideExternalDragOperationCue()
        {
            ExternalDragOperationCue.Visibility = Visibility.Collapsed;
        }

        private void ShowDragOperationCue(
            string glyph,
            string text,
            IEnumerable<DesktopItemViewModel>? items = null,
            Windows.Foundation.Point? pointerPosition = null)
        {
            ExternalDragOperationGlyph.Glyph = glyph;
            ExternalDragOperationText.Text = text;
            ExternalDragOperationCue.Visibility = Visibility.Visible;
            ExternalDragOperationCue.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));

            double left;
            double top;
            if (pointerPosition is { } point)
            {
                left = point.X + 16;
                top = point.Y + 16;
            }
            else
            {
                var bounds = items?
                    .Select(item => DesktopItemsView.TryGetItemBounds(item, _desktopRoot, out var itemBounds)
                        ? itemBounds
                        : default)
                    .Where(itemBounds => itemBounds.Width > 0 && itemBounds.Height > 0)
                    .ToArray() ?? [];
                if (bounds.Length == 0)
                {
                    left = 8;
                    top = 8;
                }
                else
                {
                    var right = bounds.Max(itemBounds => itemBounds.Right);
                    var bottom = bounds.Max(itemBounds => itemBounds.Bottom);
                    left = right - ExternalDragOperationCue.DesiredSize.Width;
                    top = bottom - ExternalDragOperationCue.DesiredSize.Height;
                }
            }

            left = Math.Clamp(left, 0, Math.Max(0, _desktopRoot.ActualWidth - ExternalDragOperationCue.DesiredSize.Width));
            top = Math.Clamp(top, 0, Math.Max(0, _desktopRoot.ActualHeight - ExternalDragOperationCue.DesiredSize.Height));
            Canvas.SetLeft(ExternalDragOperationCue, left);
            Canvas.SetTop(ExternalDragOperationCue, top);
        }

        private static void SetDesktopItemPointerFeedback(FrameworkElement element, double opacity)
        {
            if (element.FindName("ItemPointerFeedback") is Border feedback)
                feedback.Opacity = opacity;
        }

        private UIElement GetDesktopItemsCoordinateTarget() =>
            DesktopItemsView;

        private void DesktopRoot_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            RememberPenPointerPoint(e);
            if (FindItemViewModel(e.OriginalSource as DependencyObject) is not null ||
                FindWidgetCard(e.OriginalSource as DependencyObject ?? _desktopRoot) is not null ||
                IsInsideWidgetLayout(e.OriginalSource as DependencyObject))
                return;

            var point = e.GetCurrentPoint(_desktopRoot);
            if (!point.Properties.IsLeftButtonPressed)
                return;

            ClearMarqueeHover();
            _isSelectingDesktopArea = true;
            _selectionHapticSent = false;
            _selectionPointerId = point.PointerId;
            _selectionStart = point.Position;
            _desktopRoot.CapturePointer(e.Pointer);
            MarqueeFadeOut.Stop();
            SelectionRectangle.Opacity = 0.25;
            SelectionRectangle.Visibility = Visibility.Collapsed;
            e.Handled = true;
        }

        private void DesktopRoot_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            RememberPenPointerPoint(e);
            if (!_isSelectingDesktopArea || e.Pointer.PointerId != _selectionPointerId)
                return;

            var current = e.GetCurrentPoint(_desktopRoot).Position;
            var left = Math.Min(_selectionStart.X, current.X);
            var top = Math.Min(_selectionStart.Y, current.Y);
            var width = Math.Abs(current.X - _selectionStart.X);
            var height = Math.Abs(current.Y - _selectionStart.Y);
            if ((width >= 4 || height >= 4) && !_selectionHapticSent &&
                e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Pen)
            {
                PenHapticsFeedback.Send(e.GetCurrentPoint(null), PenHapticAction.Press);
                _selectionHapticSent = true;
            }
            if (UpdateMarqueeHover(new Windows.Foundation.Rect(left, top, width, height)) &&
                e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Pen)
                PenHapticsFeedback.Send(e.GetCurrentPoint(null), PenHapticAction.Success);
            if (width < 4 && height < 4)
            {
                SelectionRectangle.Visibility = Visibility.Collapsed;
                return;
            }

            Canvas.SetLeft(SelectionRectangle, left);
            Canvas.SetTop(SelectionRectangle, top);
            SelectionRectangle.Width = width;
            SelectionRectangle.Height = height;
            SelectionRectangle.Visibility = Visibility.Visible;
            e.Handled = true;
        }

        private void DesktopRoot_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_isSelectingDesktopArea || e.Pointer.PointerId != _selectionPointerId)
                return;

            _desktopRoot.ReleasePointerCapture(e.Pointer);
            if (_selectionHapticSent && e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Pen)
            {
                PenHapticsFeedback.Send(e.GetCurrentPoint(null), PenHapticAction.Release);
                PenHapticsFeedback.Send(e.GetCurrentPoint(null), PenHapticAction.Success);
            }

            var current = e.GetCurrentPoint(_desktopRoot).Position;
            var left = Math.Min(_selectionStart.X, current.X);
            var top = Math.Min(_selectionStart.Y, current.Y);
            var width = Math.Abs(current.X - _selectionStart.X);
            var height = Math.Abs(current.Y - _selectionStart.Y);
            if (width < 4 && height < 4)
            {
                ClearDesktopSelection();
            }
            else
            {
                var selectionBounds = new Windows.Foundation.Rect(left, top, width, height);
                ClearDesktopSelection();
                foreach (var item in Items)
                {
                    var padding = item.TilePadding;
                    var bounds = DesktopItemsView.TryGetVisibleItemBounds(item, _desktopRoot, out var renderedBounds)
                        ? renderedBounds
                        : new Windows.Foundation.Rect(
                            item.PositionX + padding.Left,
                            item.PositionY + padding.Top,
                            Math.Max(1, item.TileWidth - padding.Left - padding.Right),
                            Math.Max(1, item.TileHeight - padding.Top - padding.Bottom));

                    if (selectionBounds.Left < bounds.Right && selectionBounds.Right > bounds.Left &&
                        selectionBounds.Top < bounds.Bottom && selectionBounds.Bottom > bounds.Top)
                        SetDesktopItemSelected(item, true);
                }
            }

            ClearMarqueeHover();
            FadeOutMarqueeSelection();
            _isSelectingDesktopArea = false;
            _selectionHapticSent = false;
            e.Handled = true;
        }

        private void DesktopRoot_PointerCanceled(object sender, PointerRoutedEventArgs e)
        {
            if (!_isSelectingDesktopArea || e.Pointer.PointerId != _selectionPointerId)
                return;

            _desktopRoot.ReleasePointerCapture(e.Pointer);
            ClearMarqueeHover();
            FadeOutMarqueeSelection();
            _isSelectingDesktopArea = false;
            _selectionHapticSent = false;
        }

        private void RememberPenPointerPoint(PointerRoutedEventArgs e)
        {
            _lastPenPointerPoint = e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Pen
                ? e.GetCurrentPoint(null)
                : null;
        }

        private void SendLongContextHaptic(Microsoft.UI.Input.PointerDeviceType pointerDeviceType)
        {
            if (pointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Pen && _lastPenPointerPoint is { } point)
                PenHapticsFeedback.Send(point, PenHapticAction.LongInteraction);
        }

        private void SendPenHaptic(PenHapticAction action)
        {
            if (_lastPenPointerPoint is { } point)
                PenHapticsFeedback.Send(point, action);
        }

        private void DesktopItemsView_DragReflowed(object? sender, EventArgs e)
        {
            QueueIconDragPreview();
            SendPenHaptic(PenHapticAction.Success);
        }

        private void FadeOutMarqueeSelection()
        {
            if (SelectionRectangle.Visibility != Visibility.Visible)
            {
                MarqueeFadeOut.Stop();
                SelectionRectangle.Opacity = 0.25;
                SelectionRectangle.Visibility = Visibility.Collapsed;
                return;
            }

            MarqueeFadeOut.Stop();
            SelectionRectangle.Opacity = 0.25;
            MarqueeFadeOut.Begin();
        }

        private bool UpdateMarqueeHover(Windows.Foundation.Rect selectionBounds)
        {
            var currentHoverItems = new HashSet<DesktopItemViewModel>();
            foreach (var item in Items)
            {
                var padding = item.TilePadding;
                var bounds = DesktopItemsView.TryGetVisibleItemBounds(item, _desktopRoot, out var renderedBounds)
                    ? renderedBounds
                    : new Windows.Foundation.Rect(
                        item.PositionX + padding.Left,
                        item.PositionY + padding.Top,
                        Math.Max(1, item.TileWidth - padding.Left - padding.Right),
                        Math.Max(1, item.TileHeight - padding.Top - padding.Bottom));
                if (selectionBounds.Left < bounds.Right && selectionBounds.Right > bounds.Left &&
                    selectionBounds.Top < bounds.Bottom && selectionBounds.Bottom > bounds.Top)
                    currentHoverItems.Add(item);
            }

            foreach (var item in _marqueeHoveredDesktopItems.Where(item => !currentHoverItems.Contains(item)).ToArray())
                item.IsMarqueeHovered = false;
            var newlyHoveredItems = currentHoverItems.Where(item => !_marqueeHoveredDesktopItems.Contains(item)).ToArray();
            foreach (var item in newlyHoveredItems)
                item.IsMarqueeHovered = true;

            _marqueeHoveredDesktopItems.Clear();
            _marqueeHoveredDesktopItems.UnionWith(currentHoverItems);
            return newlyHoveredItems.Length > 0;
        }

        private void ClearMarqueeHover()
        {
            foreach (var item in _marqueeHoveredDesktopItems)
                item.IsMarqueeHovered = false;
            _marqueeHoveredDesktopItems.Clear();
        }

        private void DesktopRoot_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (FindItemViewModel(e.OriginalSource as DependencyObject) is not null ||
                FindWidgetCard(e.OriginalSource as DependencyObject ?? _desktopRoot) is not null)
                return;

            SendLongContextHaptic(e.PointerDeviceType);
            e.Handled = true;
            ClearDesktopSelection();
            var point = GetDpiAwareFlyoutPosition(e);
            var preferences = _contextMenuPreferences.Read();
            if (preferences.SupportsCustomContextMenus && preferences.IsEnabled("DemoteTopBar\\Enabled") != false)
            {
                _activeContextFlyout = _desktopContextFlyout;
                ShowContextFlyout(_desktopContextFlyout, point);
                _ = RefreshApplicationExtensionsAsync(_desktopContextFlyout, preferences);
            }
            else
            {
                if (CanPasteToDesktop())
                {
                    var flyout = CreateLegacyDesktopCommandBarFlyout();
                    UpdateLegacyDesktopCommandVisibility(preferences);
                    _activeContextFlyout = flyout;
                    ShowContextFlyout(flyout, point);
                    _ = RefreshLegacyApplicationExtensionsAsync(flyout, _legacyApplicationExtensionsFlyout);
                }
                else
                {
                    _activeContextFlyout = _desktopContextFlyout;
                    ShowContextFlyout(_desktopContextFlyout, point);
                    _ = RefreshApplicationExtensionsAsync(_desktopContextFlyout, preferences);
                }
            }
        }

        private CommandBarFlyout CreateLegacyDesktopCommandBarFlyout()
        {
            var flyout = new CommandBarFlyout
            {
                AlwaysExpanded = true,
                SystemBackdrop = new DesktopAcrylicBackdrop()
            };
            _legacyDesktopCommandBarFlyout = flyout;
            var paste = CreateAppBarButton("DesktopContext_Paste.Text", "\uE77F", Paste_Click);
            _legacyDesktopPasteButton = paste;
            flyout.PrimaryCommands.Add(_legacyDesktopPasteButton);

            var view = CreateAppBarButton("DesktopContext_View.Text", "\uE8A9", (_, _) => { });
            view.Flyout = CreateLegacyViewMenuFlyout();
            flyout.SecondaryCommands.Add(view);

            var sort = CreateAppBarButton("DesktopContext_SortBy.Text", "\uE8CB", (_, _) => { });
            sort.Flyout = CreateLegacySortMenuFlyout();
            flyout.SecondaryCommands.Add(sort);

            flyout.SecondaryCommands.Add(CreateAppBarButton("DesktopContext_Refresh.Text", "\uE72C", RefreshDesktop_Click));
            flyout.SecondaryCommands.Add(new AppBarSeparator());

            _legacyUndoDeleteButton = CreateAppBarButton("DesktopContext_UndoDelete.Text", "\uE7A7", UndoDelete_Click);
            flyout.SecondaryCommands.Add(_legacyUndoDeleteButton);
            flyout.Opening += (_, _) =>
            {
                UpdateLegacyDesktopCommandVisibility(_contextMenuPreferences.Read());
                NormalizeCommandBarSeparators(flyout);
            };

            var newMenu = new MenuFlyout();
            newMenu.MenuFlyoutPresenterStyle = MenuFlyoutVisuals.CreateAcrylicPresenterStyle();
            var legacyAddWidgetItem = new MenuFlyoutItem
            {
                Name = "LegacyNewWidgetMenuItem",
                Text = GetResourceText("DesktopContext_NewWidget.Text"),
                Icon = new ImageIcon { Source = new BitmapImage(new Uri("ms-appx:///Assets/Icon/windows.widget.png")) }
            };
            legacyAddWidgetItem.Click += OpenWidgetPicker_Click;
            newMenu.Items.Add(legacyAddWidgetItem);
            newMenu.Items.Add(new MenuFlyoutSeparator());
            var newFolderItem = CreateMenuItem("LegacyNewFolderMenuItem", "DesktopContext_NewFolder.Text", "\uE8B7", NewFolder_Click);
            newFolderItem.Icon = null;
            SetDirectoryIconWhenReady(icon => newFolderItem.Icon = icon);
            newMenu.Items.Add(newFolderItem);
            foreach (var template in GetShellOrderedNewTemplates())
            {
                var item = CreateMenuItem($"LegacyNew_{template.Extension.TrimStart('.')}", template.ResourceKey, template.IconGlyph, CreateNewTemplateItem_Click);
                item.Tag = template.Extension;
                item.Icon = null;
                if (template.ShellMenuText is { } shellMenuText)
                    item.Text = shellMenuText;
                SetFileTypeIconWhenReady(icon => item.Icon = icon, template.Extension);
                newMenu.Items.Add(item);
                if (template.ContentKind == ShellNewContentKind.Shortcut)
                    newMenu.Items.Add(new MenuFlyoutSeparator());
            }
            var newButton = CreateAppBarButton("DesktopContext_New.Text", "\uE710", (_, _) => { });
            newButton.Flyout = newMenu;
            flyout.SecondaryCommands.Add(newButton);
            flyout.SecondaryCommands.Add(new AppBarSeparator());

            flyout.SecondaryCommands.Add(CreateAppBarButton("DesktopContext_DisplaySettings.Text", "\uE7F4", DisplaySettings_Click));
            flyout.SecondaryCommands.Add(CreateAppBarButton("DesktopContext_Personalize.Text", "\uE771", Personalize_Click));
            _legacyApplicationExtensionsButton = CreateAppBarButton("DesktopContext_ApplicationExtensions.Text", "\uE74C", (_, _) => { });
            _legacyApplicationExtensionsFlyout = new MenuFlyout();
            _legacyApplicationExtensionsFlyout.MenuFlyoutPresenterStyle = MenuFlyoutVisuals.CreateAcrylicPresenterStyle();
            _legacyApplicationExtensionsButton.Flyout = _legacyApplicationExtensionsFlyout;
            flyout.SecondaryCommands.Add(_legacyApplicationExtensionsButton);
            _legacyDesktopExtensionsSeparator = new AppBarSeparator();
            flyout.SecondaryCommands.Add(_legacyDesktopExtensionsSeparator);
            _legacyCustomizeMenuButton = CreateAppBarButton("DesktopContext_CustomizeMenu.Text", "\uE713", CustomizeMenu_Click);
            flyout.SecondaryCommands.Add(_legacyCustomizeMenuButton);
            _legacyShowMoreOptionsButton = CreateAppBarButton("DesktopContext_ShowMoreOptions.Text", "\uE8A7", OpenNativeDesktopContextMenu_Click);
            flyout.SecondaryCommands.Add(_legacyShowMoreOptionsButton);
            return flyout;
        }

        private void UpdateLegacyDesktopCommandVisibility(ContextMenuPreferencesSnapshot snapshot)
        {
            if (_legacyDesktopPasteButton is not null)
                SetAppBarCommandAvailability(_legacyDesktopPasteButton, CanPasteToDesktop());
            UpdateUndoDeleteVisibility();
            SetMenuItemVisibility(_legacyApplicationExtensionsButton,
                snapshot.SupportsCustomContextMenus && snapshot.ShowExtensions != false);
            SetMenuItemVisibility(_legacyDesktopExtensionsSeparator,
                snapshot.SupportsCustomContextMenus && snapshot.ShowExtensions != false);
            SetMenuItemVisibility(_legacyShowMoreOptionsButton,
                snapshot.SupportsCustomContextMenus && snapshot.IsEnabled("ShowMore\\Enabled") == true);
            SetMenuItemVisibility(_legacyCustomizeMenuButton,
                snapshot.SupportsCustomContextMenus && snapshot.ShowCustomizeMenu != false);
            if (_legacyDesktopCommandBarFlyout is not null)
                NormalizeCommandBarSeparators(_legacyDesktopCommandBarFlyout);
        }

        private MenuFlyout CreateLegacyViewMenuFlyout()
        {
            var menu = new MenuFlyout();
            menu.MenuFlyoutPresenterStyle = MenuFlyoutVisuals.CreateAcrylicPresenterStyle();
            var sizes = new List<RadioMenuFlyoutItem>();
            foreach (var option in new[]
                     {
                         (Value: "Large", Key: "DesktopView_Large.Text", Shortcut: Windows.System.VirtualKey.Number2),
                         (Value: "Medium", Key: "DesktopView_Medium.Text", Shortcut: Windows.System.VirtualKey.Number3),
                         (Value: "Small", Key: "DesktopView_Small.Text", Shortcut: Windows.System.VirtualKey.Number4)
                     })
            {
                var size = CreateOptionMenuItem(option.Value, option.Key, "LegacyIconSize", ApplyDesktopIconSize);
                AddViewShortcut(size, option.Shortcut);
                sizes.Add(size);
                menu.Items.Add(size);
            }
            menu.Items.Add(new MenuFlyoutSeparator());
            var autoArrange = new ToggleMenuFlyoutItem
            {
                Text = GetResourceText("DesktopContext_AutoArrange.Text"),
                Icon = ContextMenuIconProvider.CreateForCommand("DesktopContext_AutoArrange.Text", ElementTheme.Default)
            };
            autoArrange.Click += (_, _) => SetDesktopAutoArrange(autoArrange.IsChecked);
            autoArrange.IsChecked = XiyiDesktop.AppSettingsStore.DesktopAutoArrange;
            menu.Items.Add(autoArrange);
            var alignToGrid = new ToggleMenuFlyoutItem
            {
                Text = GetResourceText("DesktopContext_AlignToGrid.Text"),
                Icon = ContextMenuIconProvider.CreateForCommand("DesktopContext_AlignToGrid.Text", ElementTheme.Default)
            };
            alignToGrid.Click += (_, _) => SetDesktopGridAlignment(alignToGrid.IsChecked);
            alignToGrid.IsChecked = XiyiDesktop.AppSettingsStore.AlignIconsToGrid;
            menu.Items.Add(alignToGrid);
            menu.Items.Add(new MenuFlyoutSeparator());
            var showIcons = new ToggleMenuFlyoutItem
            {
                Text = GetResourceText("DesktopContext_ShowIcons.Text"),
                IsChecked = XiyiDesktop.AppSettingsStore.ShowDesktopIcons,
                Icon = ContextMenuIconProvider.CreateForCommand("DesktopContext_ShowIcons.Text", ElementTheme.Default)
            };
            showIcons.Click += (_, _) =>
            {
                XiyiDesktop.AppSettingsStore.ShowDesktopIcons = showIcons.IsChecked;
                DesktopItemsView.SetDesktopIconsVisible(showIcons.IsChecked);
                UpdateArrangeOptionsAvailability();
            };
            menu.Items.Add(showIcons);
            menu.Opening += (_, _) =>
            {
                var hasCustomSize = XiyiDesktop.AppSettingsStore.DesktopIconCustomSize is not null;
                foreach (var size in sizes)
                    size.IsChecked = !hasCustomSize &&
                        string.Equals(size.Tag as string, XiyiDesktop.AppSettingsStore.DesktopIconSize, StringComparison.Ordinal);
                autoArrange.IsChecked = XiyiDesktop.AppSettingsStore.DesktopAutoArrange;
                alignToGrid.IsChecked = XiyiDesktop.AppSettingsStore.AlignIconsToGrid;
                showIcons.IsChecked = XiyiDesktop.AppSettingsStore.ShowDesktopIcons;
                UpdateArrangeOptionsAvailability();
            };

            void UpdateArrangeOptionsAvailability()
            {
                var iconsVisible = XiyiDesktop.AppSettingsStore.ShowDesktopIcons;
                autoArrange.IsEnabled = iconsVisible;
                alignToGrid.IsEnabled = iconsVisible;
            }

            return menu;
        }

        private MenuFlyout CreateLegacySortMenuFlyout()
        {
            var menu = new MenuFlyout();
            menu.MenuFlyoutPresenterStyle = MenuFlyoutVisuals.CreateAcrylicPresenterStyle();
            var options = new List<RadioMenuFlyoutItem>();
            foreach (var option in new[]
                     {
                         (Value: "Name", Key: "DesktopSort_Name.Text"),
                         (Value: "Size", Key: "DesktopSort_Size.Text"),
                         (Value: "Type", Key: "DesktopSort_Type.Text"),
                         (Value: "Modified", Key: "DesktopSort_Modified.Text")
                     })
            {
                var item = CreateOptionMenuItem(option.Value, option.Key, "LegacySortMode", ApplyDesktopSortMode);
                options.Add(item);
                menu.Items.Add(item);
            }
            menu.Opening += (_, _) =>
            {
                foreach (var item in options)
                    item.IsChecked = string.Equals(item.Tag as string, XiyiDesktop.AppSettingsStore.DesktopSortMode, StringComparison.Ordinal);
            };
            return menu;
        }

        private Windows.Foundation.Point GetDpiAwareFlyoutPosition(RightTappedRoutedEventArgs args)
        {
            return args.GetPosition(_desktopRoot);
        }

        private void ShowContextFlyout(FlyoutBase flyout, Windows.Foundation.Point desktopPoint)
        {
            var host = _menuHostWindow;
            if (host is null)
            {
                host = new DesktopMenuHostWindow();
                _menuHostWindow = host;
                host.EnableTransparentBackdrop();
            }

            var scale = _desktopRoot.XamlRoot?.RasterizationScale ?? 1.0;
            if (!double.IsFinite(scale) || scale <= 0)
                scale = 1.0;

            var screenX = (int)Math.Round(desktopPoint.X * scale);
            var screenY = (int)Math.Round(desktopPoint.Y * scale);
            if (GetWindowRect(_windowHandle, out var desktopRect))
            {
                screenX += desktopRect.Left;
                screenY += desktopRect.Top;
            }

            var hasShownFlyout = false;
            void ShowFromHost()
            {
                if (hasShownFlyout)
                    return;

                host.Root.Loaded -= HostRoot_Loaded;
                if (_disposed || host.Root.XamlRoot is null)
                {
                    if (ReferenceEquals(_menuHostWindow, host))
                        _menuHostWindow = null;
                    host.CloseHost();
                    return;
                }

                hasShownFlyout = true;
                EventHandler<object>? closed = null;
                closed = (_, _) =>
                {
                    flyout.Closed -= closed;
                    if (ReferenceEquals(_activeContextFlyout, flyout))
                        _activeContextFlyout = null;
                    host.HideHost();
                };
                try
                {
                    if (flyout.XamlRoot is null)
                        flyout.XamlRoot = host.Root.XamlRoot;
                    flyout.Closed += closed;
                    flyout.ShowAt(host.Root, new FlyoutShowOptions
                    {
                        Position = new Windows.Foundation.Point(0, 0)
                    });
                }
                catch (Exception exception)
                {
                    flyout.Closed -= closed;
                    host.HideHost();
                    Debug.WriteLine($"Could not show the context flyout from the acrylic host window: {exception}");
                }
            }

            void HostRoot_Loaded(object sender, RoutedEventArgs args) => ShowFromHost();

            host.Root.Loaded += HostRoot_Loaded;
            host.ShowAtScreenPoint(screenX, screenY);
            if (host.Root.XamlRoot is not null)
                ShowFromHost();
        }

        private void CloseContextMenuHost()
        {
            _menuHostWindow?.CloseHost();
            _menuHostWindow = null;
        }

        private void HideContextMenuHost() => _menuHostWindow?.HideHost();

        [DllImport("user32.dll", EntryPoint = "GetWindowRect", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ClientToScreen(IntPtr hwnd, ref NativePoint point);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        private const uint MonitorDefaultToNearest = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NativeMonitorInfo
        {
            public uint Size;
            public NativeRect Monitor;
            public NativeRect Work;
            public uint Flags;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref NativeMonitorInfo monitorInfo);

        [DllImport("user32.dll", EntryPoint = "EnumWindows", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

        private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);

        private static NativeRect? FindTaskbarBoundsForMonitor(IntPtr monitor)
        {
            NativeRect? taskbarBounds = null;
            EnumWindows((window, _) =>
            {
                var className = NativeMethods.GetClassName(window);
                if ((!className.Equals("Shell_TrayWnd", StringComparison.Ordinal) &&
                     !className.Equals("Shell_SecondaryTrayWnd", StringComparison.Ordinal)) ||
                    MonitorFromWindow(window, MonitorDefaultToNearest) != monitor ||
                    !GetWindowRect(window, out var bounds))
                {
                    return true;
                }

                taskbarBounds = bounds;
                return false;
            }, IntPtr.Zero);
            return taskbarBounds;
        }

        private void UpdateDesktopWorkAreaMargins()
        {
            if (_disposed || DesktopItemsView is null)
                return;

            var scale = _desktopRoot.XamlRoot?.RasterizationScale ?? 1.0;
            if (!double.IsFinite(scale) || scale <= 0)
                scale = 1.0;

            var left = DesktopEdgePadding;
            var top = DesktopEdgePadding;
            var right = DesktopEdgePadding;
            var bottom = DesktopEdgePadding;
            var monitor = MonitorFromWindow(_windowHandle, MonitorDefaultToNearest);
            if (monitor != IntPtr.Zero)
            {
                var info = new NativeMonitorInfo
                {
                    Size = (uint)Marshal.SizeOf<NativeMonitorInfo>()
                };
                if (GetMonitorInfo(monitor, ref info))
                {
                    var monitorWidth = info.Monitor.Right - info.Monitor.Left;
                    var monitorHeight = info.Monitor.Bottom - info.Monitor.Top;
                    var workWidth = info.Work.Right - info.Work.Left;
                    var workHeight = info.Work.Bottom - info.Work.Top;
                    var workLeft = Math.Max(0, info.Work.Left - info.Monitor.Left);
                    var workTop = Math.Max(0, info.Work.Top - info.Monitor.Top);
                    var workRight = Math.Max(0, monitorWidth - workWidth - workLeft);
                    var workBottom = Math.Max(0, monitorHeight - workHeight - workTop);

                    // The work area normally reserves the taskbar already. Use the
                    // larger of that reservation and the current taskbar HWND bounds,
                    // never add the taskbar height on top of the work-area inset.
                    if (FindTaskbarBoundsForMonitor(monitor) is { } taskbar)
                    {
                        var taskbarWidth = taskbar.Right - taskbar.Left;
                        var taskbarHeight = taskbar.Bottom - taskbar.Top;
                        if (taskbarWidth >= taskbarHeight)
                        {
                            var topGap = Math.Abs(taskbar.Top - info.Monitor.Top);
                            var bottomGap = Math.Abs(info.Monitor.Bottom - taskbar.Bottom);
                            if (topGap <= bottomGap)
                                workTop = Math.Max(workTop, Math.Clamp(taskbar.Bottom - info.Monitor.Top, 0, monitorHeight));
                            else
                                workBottom = Math.Max(workBottom, Math.Clamp(info.Monitor.Bottom - taskbar.Top, 0, monitorHeight));
                        }
                        else
                        {
                            var leftGap = Math.Abs(taskbar.Left - info.Monitor.Left);
                            var rightGap = Math.Abs(info.Monitor.Right - taskbar.Right);
                            if (leftGap <= rightGap)
                                workLeft = Math.Max(workLeft, Math.Clamp(taskbar.Right - info.Monitor.Left, 0, monitorWidth));
                            else
                                workRight = Math.Max(workRight, Math.Clamp(info.Monitor.Right - taskbar.Left, 0, monitorWidth));
                        }
                    }

                    left = DesktopEdgePadding + workLeft / scale;
                    top = DesktopEdgePadding + workTop / scale;
                    right = DesktopEdgePadding + workRight / scale;
                    bottom = DesktopEdgePadding + workBottom / scale;
                }
            }

            var margins = new Thickness(left, top, right, bottom);
            if (DesktopSurface.Margin.Equals(margins))
                return;

            var previousMargins = DesktopSurface.Margin;
            DesktopSurface.Margin = margins;
            DesktopItemsView.Margin = new Thickness(0);
            DesktopItemsView.UpdateLayout();
            DesktopItemsView.SetViewportSize(new Windows.Foundation.Size(
                DesktopItemsView.ActualWidth,
                DesktopItemsView.ActualHeight));
            DesktopItemsView.InvalidateArrange();
            if (_hasAppliedWorkAreaMargins)
                AnimateWorkAreaMarginChange(previousMargins, margins);
            else
                _hasAppliedWorkAreaMargins = true;

            if (!XiyiDesktop.AppSettingsStore.DesktopAutoArrange && !XiyiDesktop.AppSettingsStore.AlignIconsToGrid)
            {
                foreach (var item in Items)
                {
                    if (item.HasPosition)
                        DesktopItemsView.ClampItemPosition(item);
                }
            }
        }

        private void AnimateWorkAreaMarginChange(Thickness previousMargins, Thickness newMargins)
        {
            var currentX = _workAreaTransitionTransform.X;
            var currentY = _workAreaTransitionTransform.Y;
            _workAreaTransitionStoryboard?.Stop();

            _workAreaTransitionTransform.X = currentX + previousMargins.Left - newMargins.Left;
            _workAreaTransitionTransform.Y = currentY + previousMargins.Top - newMargins.Top;

            var storyboard = new Storyboard();
            var easing = new CircleEase { EasingMode = EasingMode.EaseInOut };
            var horizontal = new DoubleAnimation
            {
                To = 0,
                Duration = TimeSpan.FromMilliseconds(500),
                EasingFunction = easing,
                EnableDependentAnimation = true
            };
            Storyboard.SetTarget(horizontal, _workAreaTransitionTransform);
            Storyboard.SetTargetProperty(horizontal, "X");
            storyboard.Children.Add(horizontal);

            var vertical = new DoubleAnimation
            {
                To = 0,
                Duration = TimeSpan.FromMilliseconds(500),
                EasingFunction = easing,
                EnableDependentAnimation = true
            };
            Storyboard.SetTarget(vertical, _workAreaTransitionTransform);
            Storyboard.SetTargetProperty(vertical, "Y");
            storyboard.Children.Add(vertical);

            _workAreaTransitionStoryboard = storyboard;
            storyboard.Begin();
            TrackWidgetOverlayLayout(TimeSpan.FromMilliseconds(520));
        }

        private void SelectForContextMenu(DesktopItemViewModel item)
        {
            if (!_selectedDesktopItems.Contains(item))
            {
                ClearDesktopSelection();
                SetDesktopItemSelected(item, true);
            }
        }

        private void ClearDesktopSelection()
        {
            foreach (var item in _selectedDesktopItems.ToArray())
                SetDesktopItemSelected(item, false);
            _selectedDesktopItems.Clear();
        }

        private void SetDesktopItemSelected(DesktopItemViewModel item, bool selected)
        {
            if (selected)
                _selectedDesktopItems.Add(item);
            else
                _selectedDesktopItems.Remove(item);
            item.IsSelected = selected;
        }

        private void ItemContextFlyout_Opening(object? sender, object e)
        {
            SetMenuItemText(sender, "OpenMenuItem", "DesktopItemContext_Open/Text");
            SetMenuItemText(sender, "CopyMenuItem", "DesktopItemContext_Copy/Text");
            SetMenuItemText(sender, "CutMenuItem", "DesktopItemContext_Cut/Text");
            SetMenuItemText(sender, "ItemPasteMenuItem", "DesktopItemContext_Paste/Text");
            SetMenuItemText(sender, "RenameMenuItem", "DesktopItemContext_Rename/Text");
            SetMenuItemText(sender, "DeleteMenuItem", "DesktopItemContext_Delete/Text");
            SetMenuItemText(sender, "PropertiesMenuItem", "DesktopItemContext_Properties/Text");
            SetMenuItemText(sender, "ManageThisPcMenuItem", "DesktopItemContext_ManageThisPc.Text");
            SetMenuItemText(sender, "EmptyRecycleBinMenuItem", "DesktopItemContext_EmptyRecycleBin.Text");

            var selected = GetSelectedItems();
            var hasSingle = selected.Length == 1;
            var fileSystemItems = selected.Where(item => item.Path is not null).ToArray();
            var hasOnlyFileSystemItems = selected.Length > 0 && fileSystemItems.Length == selected.Length;
            var preferences = _contextMenuPreferences.Read();

            if (sender is MenuFlyout flyout)
            {
                SetMenuItemText(sender, "SendToMenuItem", "DesktopItemContext_SendTo.Text");
                SetMenuItemText(sender, "PrintMenuItem", "DesktopItemContext_Print.Text");
                SetMenuItemText(sender, "CreateShortcutMenuItem", "DesktopItemContext_CreateShortcut.Text");
                SetMenuItemText(sender, "CopyAsPathMenuItem", "DesktopItemContext_CopyAsPath.Text");
                if (_rotateImageMenuItem is not null)
                    _rotateImageMenuItem.Text = GetResourceText("DesktopItemContext_RotateImage.Text");

                if (_movePropertiesMenuItem is not null)
                {
                    if (_movePropertiesSeparator is not null && flyout.Items.Contains(_movePropertiesSeparator))
                        flyout.Items.Remove(_movePropertiesSeparator);
                    if (flyout.Items.Contains(_movePropertiesMenuItem))
                        flyout.Items.Remove(_movePropertiesMenuItem);

                    var deleteItem = flyout.Items.OfType<MenuFlyoutItem>().FirstOrDefault(item => item.Name == "DeleteMenuItem");
                    var deleteIndex = deleteItem is null ? flyout.Items.Count : flyout.Items.IndexOf(deleteItem);
                    var placeAtBottom = preferences.IsEnabled("MoveProperties\\Enabled") != false;
                    if (placeAtBottom)
                    {
                        if (_movePropertiesSeparator is not null)
                            flyout.Items.Add(_movePropertiesSeparator);
                        flyout.Items.Add(_movePropertiesMenuItem);
                    }
                    else
                        flyout.Items.Insert(Math.Max(0, deleteIndex), _movePropertiesMenuItem);
                }
            }

            SetMenuItemEnabled(sender, "OpenMenuItem", hasSingle && selected[0].CanOpen);
            SetMenuItemEnabled(sender, "CopyMenuItem", selected.Length > 0 && fileSystemItems.Length == selected.Length);
            SetMenuItemEnabled(sender, "CutMenuItem", selected.Length > 0 && fileSystemItems.Length == selected.Length);
            SetMenuItemEnabled(sender, "RenameMenuItem", hasSingle && CanRenameDesktopItem(selected[0]));
            SetMenuItemEnabled(sender, "DeleteMenuItem", fileSystemItems.Length > 0);
            SetMenuItemEnabled(sender, "PropertiesMenuItem", hasSingle && CanShowProperties(selected[0]));
            SetMenuItemVisibility(sender, "PropertiesMenuItem", hasSingle && CanShowProperties(selected[0]));
            SetMenuItemVisibility(_itemOpenFileLocationMenuItem, hasSingle && selected[0].IsShortcut);
            SetMenuItemVisibility(_itemRunAsAdminMenuItem, hasSingle && IsRunnableAsAdministrator(selected[0]));
            SetMenuItemVisibility(_itemShareMenuItem, hasSingle && selected[0].Path is not null);
            SetMenuItemVisibility(_itemPasteMenuItem,
                hasSingle && selected[0].IsDirectory && CanPasteToCurrentTarget());
            SetMenuItemVisibility(_manageThisPcMenuItem, hasSingle && selected[0].Kind == DesktopItemKind.ThisPc);
            SetMenuItemVisibility(_emptyRecycleBinMenuItem,
                hasSingle && selected[0].Kind == DesktopItemKind.RecycleBin && RecycleBinContainsItems());
            SetMenuItemVisibility(_itemCustomizeMenuItem,
                preferences.SupportsCustomContextMenus && preferences.ShowCustomizeMenu != false);
            SetMenuItemVisibility(_itemShowMoreOptionsMenuItem,
                hasSingle && preferences.SupportsCustomContextMenus && preferences.IsEnabled("ShowMore\\Enabled") == true);
            SetMenuItemVisibility(_itemApplicationExtensionsItem,
                hasSingle && selected[0].Path is not null && preferences.SupportsCustomContextMenus && preferences.ShowExtensions != false);
            SetMenuItemVisibility(_itemExtensionsSeparator,
                hasSingle && selected[0].Path is not null && preferences.SupportsCustomContextMenus && preferences.ShowExtensions != false);

            SetMenuItemVisibility(_sendToMenuItem, hasOnlyFileSystemItems && preferences.IsEnabled("sendto") != false);
            if (hasOnlyFileSystemItems && preferences.IsEnabled("sendto") != false && _sendToMenuItem is not null)
                _ = RefreshSendToMenuAsync(_sendToMenuItem, fileSystemItems.Select(item => item.Path!).ToArray());
            SetMenuItemVisibility(_printMenuItem, hasSingle && selected[0].Path is not null && preferences.IsEnabled("print") != false);
            SetMenuItemVisibility(_createShortcutMenuItem, hasOnlyFileSystemItems && preferences.IsEnabled("link") != false);
            SetMenuItemVisibility(_copyAsPathMenuItem, hasOnlyFileSystemItems && preferences.IsEnabled("copyaspath") != false);
            SetMenuItemVisibility(_rotateImageMenuItem,
                hasSingle && selected[0].Path is { } imagePath && IsRotatableImage(imagePath) && preferences.IsEnabled("rotate90") != false);

            if (_itemOpenMenuItem is not null)
            {
                var openMenuItem = _itemOpenMenuItem;
                if (hasSingle && selected[0].Path is not null && !selected[0].IsDirectory && !selected[0].IsShortcut && selected[0].FileExtension is { } extension)
                    SetFileTypeIconWhenReady(icon => openMenuItem.Icon = icon, extension);
                else if (hasSingle && selected[0].Icon is { } shellIcon)
                    openMenuItem.Icon = new ImageIcon { Source = shellIcon };
            }

            if (sender is MenuFlyout itemFlyout)
                NormalizeMenuFlyoutSeparators(itemFlyout);
        }

        private async Task RefreshItemApplicationExtensionsAsync(
            MenuFlyout ownerFlyout,
            IReadOnlyList<DesktopItem> selected)
        {
            var generation = ++_itemExtensionRefreshGeneration;
            foreach (var item in _dynamicItemExtensionMenuItems)
                ownerFlyout.Items.Remove(item);
            _dynamicItemExtensionMenuItems.Clear();
            _itemApplicationExtensionsItem?.Items.Clear();
            var preferences = _contextMenuPreferences.Read();
            if (!preferences.SupportsCustomContextMenus || preferences.ShowExtensions == false ||
                selected.Count != 1 || selected[0].Path is not { } targetPath)
                return;

            IReadOnlyList<ExplorerCommandPackage> packages;
            try
            {
                packages = await ExplorerCommandAdapter.DiscoverItemCommandsAsync(targetPath);
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"Could not discover item application extensions for '{targetPath}': {exception}");
                packages = [];
            }

            if (_disposed || generation != _itemExtensionRefreshGeneration)
                return;

            if (_itemApplicationExtensionsItem is null)
                return;

            var directCommands = packages
                .Where(IsDirectMainMenuExtension)
                .SelectMany(package => package.Commands)
                .ToArray();
            var insertionIndex = ownerFlyout.Items.IndexOf(_itemApplicationExtensionsItem);
            if (insertionIndex >= 0)
            {
                foreach (var command in directCommands)
                {
                    var commandItem = CreateExplorerCommandMenuItem(command, targetPath);
                    ownerFlyout.Items.Insert(insertionIndex++, commandItem);
                    _dynamicItemExtensionMenuItems.Add(commandItem);
                }

                if (directCommands.Length > 0)
                {
                    var separator = new MenuFlyoutSeparator();
                    ownerFlyout.Items.Insert(insertionIndex, separator);
                    _dynamicItemExtensionMenuItems.Add(separator);
                }
            }

            foreach (var package in packages.Where(package => !IsDirectMainMenuExtension(package)))
            {
                foreach (var command in package.Commands)
                {
                    var menuItem = CreateExplorerCommandMenuItem(command, targetPath);
                    ToolTipService.SetToolTip(menuItem, $"{package.DisplayName} ({package.PackageFamilyName})");
                    _itemApplicationExtensionsItem.Items.Add(menuItem);
                }
            }

            if (_itemApplicationExtensionsItem.Items.Count > 0)
                _itemApplicationExtensionsItem.Items.Add(new MenuFlyoutSeparator());
            _itemApplicationExtensionsItem.Items.Add(CreateMenuItem(
                "ItemManageExtensionsMenuItem",
                "DesktopContext_ManageExtensions.Text",
                "\uE713",
                CustomizeMenu_Click,
                includeIcon: false));
            NormalizeMenuFlyoutSeparators(ownerFlyout);
        }

        private void DesktopContextFlyout_Opening(object? sender, object e)
        {
            var snapshot = _contextMenuPreferences.Read();
            UpdateViewMenuItems();
            UpdateSortMenuItems();
            if (_applicationExtensionsItem is not null)
                _applicationExtensionsItem.Visibility = snapshot.SupportsCustomContextMenus && snapshot.ShowExtensions != false
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            SetMenuItemVisibility(_desktopExtensionsSeparator,
                snapshot.SupportsCustomContextMenus && snapshot.ShowExtensions != false);

            if (_showMoreOptionsItem is not null)
                _showMoreOptionsItem.Visibility = snapshot.SupportsCustomContextMenus && snapshot.IsEnabled("ShowMore\\Enabled") == true
                    ? Visibility.Visible
                    : Visibility.Collapsed;

            if (_customizeMenuItem is not null)
                _customizeMenuItem.Visibility = snapshot.SupportsCustomContextMenus && snapshot.ShowCustomizeMenu != false
                    ? Visibility.Visible
                    : Visibility.Collapsed;

            if (_pasteMenuItem is not null)
                _pasteMenuItem.Visibility = CanPasteToDesktop() ? Visibility.Visible : Visibility.Collapsed;

            UpdateUndoDeleteVisibility();
            NormalizeMenuFlyoutSeparators(_desktopContextFlyout);
        }

        private MenuFlyout CreateDesktopContextFlyout()
        {
            var flyout = new MenuFlyout();
            flyout.MenuFlyoutPresenterStyle = MenuFlyoutVisuals.CreateAcrylicPresenterStyle();

            var viewMenu = CreateSubMenu("RootViewMenu", "DesktopContext_View.Text", "\uE8A9");
            RadioMenuFlyoutItem.SetAreCheckStatesEnabled(viewMenu, true);
            _iconSizeMenuItems.Add(CreateOptionMenuItem("Large", "DesktopView_Large.Text", "DesktopIconSize", ApplyDesktopIconSize));
            _iconSizeMenuItems.Add(CreateOptionMenuItem("Medium", "DesktopView_Medium.Text", "DesktopIconSize", ApplyDesktopIconSize));
            _iconSizeMenuItems.Add(CreateOptionMenuItem("Small", "DesktopView_Small.Text", "DesktopIconSize", ApplyDesktopIconSize));
            AddViewShortcut(_iconSizeMenuItems[0], Windows.System.VirtualKey.Number2);
            AddViewShortcut(_iconSizeMenuItems[1], Windows.System.VirtualKey.Number3);
            AddViewShortcut(_iconSizeMenuItems[2], Windows.System.VirtualKey.Number4);
            foreach (var item in _iconSizeMenuItems)
                viewMenu.Items.Add(item);
            viewMenu.Items.Add(new MenuFlyoutSeparator());
            _autoArrangeMenuItem = new ToggleMenuFlyoutItem
            {
                Name = "RootAutoArrangeMenuItem",
                Text = GetResourceText("DesktopContext_AutoArrange.Text"),
                Icon = ContextMenuIconProvider.CreateForCommand("DesktopContext_AutoArrange.Text", ElementTheme.Default)
            };
            _autoArrangeMenuItem.Click += AutoArrangeMenuItem_Click;
            viewMenu.Items.Add(_autoArrangeMenuItem);
            _alignToGridMenuItem = new ToggleMenuFlyoutItem
            {
                Name = "RootAlignToGridMenuItem",
                Text = GetResourceText("DesktopContext_AlignToGrid.Text"),
                Icon = ContextMenuIconProvider.CreateForCommand("DesktopContext_AlignToGrid.Text", ElementTheme.Default)
            };
            _alignToGridMenuItem.Click += AlignToGridMenuItem_Click;
            viewMenu.Items.Add(_alignToGridMenuItem);
            viewMenu.Items.Add(new MenuFlyoutSeparator());
            _showIconsMenuItem = new ToggleMenuFlyoutItem
            {
                Name = "RootShowIconsMenuItem",
                Text = GetResourceText("DesktopContext_ShowIcons.Text"),
                Icon = ContextMenuIconProvider.CreateForCommand("DesktopContext_ShowIcons.Text", ElementTheme.Default)
            };
            _showIconsMenuItem.Click += ToggleDesktopIcons_Click;
            viewMenu.Items.Add(_showIconsMenuItem);
            flyout.Items.Add(viewMenu);

            var sortMenu = CreateSubMenu("RootSortMenu", "DesktopContext_SortBy.Text", "\uE8CB");
            RadioMenuFlyoutItem.SetAreCheckStatesEnabled(sortMenu, true);
            _sortMenuItems.Add(CreateOptionMenuItem("Name", "DesktopSort_Name.Text", "DesktopSortMode", ApplyDesktopSortMode));
            _sortMenuItems.Add(CreateOptionMenuItem("Size", "DesktopSort_Size.Text", "DesktopSortMode", ApplyDesktopSortMode));
            _sortMenuItems.Add(CreateOptionMenuItem("Type", "DesktopSort_Type.Text", "DesktopSortMode", ApplyDesktopSortMode));
            _sortMenuItems.Add(CreateOptionMenuItem("Modified", "DesktopSort_Modified.Text", "DesktopSortMode", ApplyDesktopSortMode));
            foreach (var item in _sortMenuItems)
                sortMenu.Items.Add(item);
            flyout.Items.Add(sortMenu);

            flyout.Items.Add(CreateMenuItem("RootRefreshMenuItem", "DesktopContext_Refresh.Text", "\uE72C", RefreshDesktop_Click));
            _pasteMenuItem = CreateMenuItem("RootPasteMenuItem", "DesktopContext_Paste.Text", "\uE77F", Paste_Click);
            flyout.Items.Add(_pasteMenuItem);
            _undoDeleteMenuItem = CreateMenuItem("RootUndoDeleteMenuItem", "DesktopContext_UndoDelete.Text", "\uE7A7", UndoDelete_Click);
            _undoDeleteMenuItem.KeyboardAccelerators.Add(new KeyboardAccelerator { Key = Windows.System.VirtualKey.Z, Modifiers = Windows.System.VirtualKeyModifiers.Control });
            flyout.Items.Add(_undoDeleteMenuItem);
            flyout.Items.Add(new MenuFlyoutSeparator());

            var newMenu = CreateSubMenu("RootNewMenu", "DesktopContext_New.Text", "\uE710");
            var addWidgetItem = new MenuFlyoutItem
            {
                Name = "NewWidgetMenuItem",
                Text = GetResourceText("DesktopContext_NewWidget.Text"),
                Icon = new ImageIcon { Source = new BitmapImage(new Uri("ms-appx:///Assets/Icon/windows.widget.png")) }
            };
            addWidgetItem.Click += OpenWidgetPicker_Click;
            newMenu.Items.Add(addWidgetItem);
            newMenu.Items.Add(new MenuFlyoutSeparator());
            var newFolderItem = CreateMenuItem("NewFolderMenuItem", "DesktopContext_NewFolder.Text", "\uE8B7", NewFolder_Click);
            newFolderItem.Icon = null;
            SetDirectoryIconWhenReady(icon => newFolderItem.Icon = icon);
            newMenu.Items.Add(newFolderItem);
            foreach (var template in GetShellOrderedNewTemplates())
            {
                var item = CreateMenuItem(
                    $"NewItem_{template.Extension.TrimStart('.')}",
                    template.ResourceKey,
                    template.IconGlyph,
                    CreateNewTemplateItem_Click);
                item.Tag = template.Extension;
                item.Icon = null;
                if (template.ShellMenuText is { } shellMenuText)
                    item.Text = shellMenuText;
                SetFileTypeIconWhenReady(icon => item.Icon = icon, template.Extension);
                newMenu.Items.Add(item);
                if (template.ContentKind == ShellNewContentKind.Shortcut)
                    newMenu.Items.Add(new MenuFlyoutSeparator());
            }
            flyout.Items.Add(newMenu);
            flyout.Items.Add(new MenuFlyoutSeparator());

            flyout.Items.Add(CreateMenuItem("DisplaySettingsMenuItem", "DesktopContext_DisplaySettings.Text", "\uE7F4", DisplaySettings_Click));
            flyout.Items.Add(CreateMenuItem("PersonalizeMenuItem", "DesktopContext_Personalize.Text", "\uE771", Personalize_Click));
            _applicationExtensionsItem = CreateSubMenu("ApplicationExtensionsMenuItem", "DesktopContext_ApplicationExtensions.Text", "\uE74C");
            flyout.Items.Add(_applicationExtensionsItem);
            _desktopExtensionsSeparator = new MenuFlyoutSeparator();
            flyout.Items.Add(_desktopExtensionsSeparator);

            _customizeMenuItem = CreateMenuItem("CustomizeMenuItem", "DesktopContext_CustomizeMenu.Text", "\uE713", CustomizeMenu_Click);
            flyout.Items.Add(_customizeMenuItem);
            _showMoreOptionsItem = CreateMenuItem("ShowMoreOptionsMenuItem", "DesktopContext_ShowMoreOptions.Text", "\uE8A7", OpenNativeDesktopContextMenu_Click);
            flyout.Items.Add(_showMoreOptionsItem);
            flyout.Opening += DesktopContextFlyout_Opening;
            return flyout;
        }

        private IReadOnlyList<ShellNewTemplate> GetShellOrderedNewTemplates()
        {
            var templates = ShellNewTemplateCatalog.GetSupportedTemplates();
            var nativeOrder = ShellContextMenuService.GetDesktopNewMenuOrder(GetResourceText("DesktopContext_New.Text"));
            if (nativeOrder.Count == 0)
                return templates;

            var nativeIndices = new Dictionary<string, int>(StringComparer.CurrentCultureIgnoreCase);
            for (var index = 0; index < nativeOrder.Count; index++)
            {
                var label = NormalizeNewMenuLabel(nativeOrder[index]);
                if (!string.IsNullOrWhiteSpace(label) && !nativeIndices.ContainsKey(label))
                    nativeIndices.Add(label, index);
            }

            var orderedTemplates = templates
                .Select((template, originalIndex) =>
                {
                    var resourceLabel = NormalizeNewMenuLabel(GetResourceText(template.ResourceKey));
                    var shellTypeLabel = NormalizeNewMenuLabel(ShellIconService.GetFileTypeName(template.Extension, isDirectory: false));
                    var matchedLabel = nativeIndices.ContainsKey(resourceLabel)
                        ? resourceLabel
                        : nativeIndices.ContainsKey(shellTypeLabel)
                            ? shellTypeLabel
                            : null;
                    return new
                    {
                        Template = template,
                        OriginalIndex = originalIndex,
                        MatchedLabel = matchedLabel
                    };
                })
                .ToArray();

            if (!orderedTemplates.Any(entry => entry.MatchedLabel is not null))
                return templates;

            return orderedTemplates
                .OrderBy(entry => entry.MatchedLabel is { } label && nativeIndices.TryGetValue(label, out var index)
                    ? index
                    : int.MaxValue)
                .ThenBy(entry => entry.OriginalIndex)
                .Select(entry => entry.Template with
                {
                    ShellMenuText = entry.MatchedLabel is { } label
                        ? nativeOrder[nativeIndices[label]]
                        : null
                })
                .ToArray();
        }

        private static string NormalizeNewMenuLabel(string value)
        {
            var acceleratorStart = value.IndexOf("(&", StringComparison.Ordinal);
            if (acceleratorStart >= 0)
            {
                var acceleratorEnd = value.IndexOf(')', acceleratorStart);
                if (acceleratorEnd > acceleratorStart)
                    value = value.Remove(acceleratorStart, acceleratorEnd - acceleratorStart + 1);
            }

            return value.Replace("&&", "\0", StringComparison.Ordinal)
                .Replace("&", string.Empty, StringComparison.Ordinal)
                .Replace("\0", "&", StringComparison.Ordinal)
                .Trim()
                .TrimEnd('.', '…');
        }

        private MenuFlyoutSubItem CreateSubMenu(string name, string resourceKey, string glyph) => new()
        {
            Name = name,
            Text = GetResourceText(resourceKey),
            Icon = ContextMenuIconProvider.CreateForCommand(resourceKey, ElementTheme.Default)
        };

        private async Task RefreshApplicationExtensionsAsync(
            MenuFlyout desktopFlyout,
            ContextMenuPreferencesSnapshot preferences)
        {
            var generation = ++_extensionRefreshGeneration;
            foreach (var item in _dynamicExtensionMenuItems)
                desktopFlyout.Items.Remove(item);
            _dynamicExtensionMenuItems.Clear();
            _applicationExtensionsItem?.Items.Clear();

            if (_applicationExtensionsItem is null ||
                !preferences.SupportsCustomContextMenus ||
                preferences.ShowExtensions == false)
            {
                return;
            }

            IReadOnlyList<ExplorerCommandPackage> packages;
            try
            {
                packages = await ExplorerCommandAdapter.DiscoverDesktopBackgroundCommandsAsync(GetDesktopPath());
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"Could not discover desktop application extensions: {exception}");
                packages = [];
            }

            if (_disposed || generation != _extensionRefreshGeneration || _applicationExtensionsItem is null)
                return;

            _applicationExtensionsItem.Items.Clear();
            var mainMenuCommands = packages
                .Where(IsDirectMainMenuExtension)
                .SelectMany(package => package.Commands)
                .Select(command => CreateExplorerCommandMenuItem(command, GetDesktopPath()))
                .Cast<MenuFlyoutItemBase>()
                .ToArray();

            if (mainMenuCommands.Length > 0)
            {
                var insertionIndex = desktopFlyout.Items.IndexOf(_applicationExtensionsItem);
                if (insertionIndex >= 0)
                {
                    foreach (var commandItem in mainMenuCommands)
                    {
                        desktopFlyout.Items.Insert(insertionIndex++, commandItem);
                        _dynamicExtensionMenuItems.Add(commandItem);
                    }
                    var separatorAfter = new MenuFlyoutSeparator();
                    desktopFlyout.Items.Insert(insertionIndex, separatorAfter);
                    _dynamicExtensionMenuItems.Add(separatorAfter);
                }
            }

            var submenuPackages = packages
                .Where(package => !IsDirectMainMenuExtension(package))
                .ToArray();
            foreach (var package in submenuPackages)
            {
                foreach (var command in package.Commands)
                {
                    var menuItem = CreateExplorerCommandMenuItem(command, GetDesktopPath());
                    ToolTipService.SetToolTip(menuItem, $"{package.DisplayName} ({package.PackageFamilyName})");
                    _applicationExtensionsItem.Items.Add(menuItem);
                }
            }

            if (_applicationExtensionsItem.Items.Count > 0)
                _applicationExtensionsItem.Items.Add(new MenuFlyoutSeparator());
            _applicationExtensionsItem.Items.Add(CreateMenuItem(
                "ManageExtensionsMenuItem",
                "DesktopContext_ManageExtensions.Text",
                "\uE713",
                CustomizeMenu_Click,
                includeIcon: false));
            NormalizeMenuFlyoutSeparators(desktopFlyout);
        }

        private async Task RefreshLegacyApplicationExtensionsAsync(CommandBarFlyout ownerFlyout, MenuFlyout? extensionFlyout)
        {
            if (extensionFlyout is null)
                return;

            var generation = ++_extensionRefreshGeneration;
            foreach (var separator in _dynamicLegacyExtensionSeparators)
                ownerFlyout.SecondaryCommands.Remove(separator);
            _dynamicLegacyExtensionSeparators.Clear();
            foreach (var button in _dynamicLegacyExtensionButtons)
                ownerFlyout.SecondaryCommands.Remove(button);
            _dynamicLegacyExtensionButtons.Clear();
            extensionFlyout.Items.Clear();
            var preferences = _contextMenuPreferences.Read();
            if (!preferences.SupportsCustomContextMenus || preferences.ShowExtensions == false)
                return;

            IReadOnlyList<ExplorerCommandPackage> packages;
            try
            {
                packages = await ExplorerCommandAdapter.DiscoverDesktopBackgroundCommandsAsync(GetDesktopPath());
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"Could not discover desktop application extensions: {exception}");
                packages = [];
            }

            if (_disposed || generation != _extensionRefreshGeneration)
                return;

            var directCommands = packages
                .Where(IsDirectMainMenuExtension)
                .SelectMany(package => package.Commands)
                .ToArray();
            var insertIndex = _legacyApplicationExtensionsButton is null
                ? ownerFlyout.SecondaryCommands.Count
                : ownerFlyout.SecondaryCommands.IndexOf(_legacyApplicationExtensionsButton);
            if (insertIndex < 0)
                insertIndex = ownerFlyout.SecondaryCommands.Count;
            if (directCommands.Length > 0)
            {
                var separatorBefore = new AppBarSeparator();
                ownerFlyout.SecondaryCommands.Insert(insertIndex++, separatorBefore);
                _dynamicLegacyExtensionSeparators.Add(separatorBefore);
            }
            foreach (var command in directCommands)
            {
                var button = CreateExplorerCommandAppBarButton(command, GetDesktopPath());
                ownerFlyout.SecondaryCommands.Insert(insertIndex++, button);
                _dynamicLegacyExtensionButtons.Add(button);
            }
            if (directCommands.Length > 0)
            {
                var separatorAfter = new AppBarSeparator();
                ownerFlyout.SecondaryCommands.Insert(insertIndex, separatorAfter);
                _dynamicLegacyExtensionSeparators.Add(separatorAfter);
            }

            foreach (var package in packages.Where(package => !IsDirectMainMenuExtension(package)))
            {
                foreach (var command in package.Commands)
                {
                    var item = CreateExplorerCommandMenuItem(command, GetDesktopPath());
                    ToolTipService.SetToolTip(item, $"{package.DisplayName} ({package.PackageFamilyName})");
                    extensionFlyout.Items.Add(item);
                }
            }

            if (extensionFlyout.Items.Count > 0)
            {
                extensionFlyout.Items.Add(new MenuFlyoutSeparator());
            }
            extensionFlyout.Items.Add(CreateMenuItem(
                "LegacyManageExtensionsMenuItem",
                "DesktopContext_ManageExtensions.Text",
                "\uE713",
                CustomizeMenu_Click,
                includeIcon: false));
            NormalizeCommandBarSeparators(ownerFlyout);
        }

        private MenuFlyoutItemBase CreateExplorerCommandMenuItem(ExplorerCommandNode command, string? invocationTarget = null)
        {
            if (command.Children.Count > 0)
            {
                var submenu = new MenuFlyoutSubItem
                {
                    Text = command.Title,
                    Icon = CreateExplorerCommandIcon(),
                    IsEnabled = !command.IsDisabled
                };
                SetExplorerCommandIconWhenReady(icon => submenu.Icon = icon, command.IconLocation);
                if (!string.IsNullOrWhiteSpace(command.ToolTip))
                    ToolTipService.SetToolTip(submenu, command.ToolTip);
                foreach (var child in command.Children)
                    submenu.Items.Add(CreateExplorerCommandMenuItem(child, invocationTarget));
                return submenu;
            }

            var item = new MenuFlyoutItem
            {
                Text = command.Title,
                Icon = CreateExplorerCommandIcon(),
                IsEnabled = !command.IsDisabled
            };
            SetExplorerCommandIconWhenReady(icon => item.Icon = icon, command.IconLocation);
            if (!string.IsNullOrWhiteSpace(command.ToolTip))
                ToolTipService.SetToolTip(item, command.ToolTip);
            item.Click += async (_, _) => await InvokeExplorerCommandAsync(command, invocationTarget);
            return item;
        }

        private AppBarButton CreateExplorerCommandAppBarButton(ExplorerCommandNode command, string? invocationTarget = null)
        {
            var button = new AppBarButton
            {
                Label = command.Title,
                Icon = CreateExplorerCommandIcon(),
                IsEnabled = !command.IsDisabled
            };
            SetExplorerCommandIconWhenReady(icon => button.Icon = icon, command.IconLocation);

            if (command.Children.Count > 0)
            {
                var submenu = new MenuFlyout();
                submenu.MenuFlyoutPresenterStyle = MenuFlyoutVisuals.CreateAcrylicPresenterStyle();
                foreach (var child in command.Children)
                    submenu.Items.Add(CreateExplorerCommandMenuItem(child, invocationTarget));
                button.Flyout = submenu;
            }
            else
            {
                button.Click += async (_, _) => await InvokeExplorerCommandAsync(command, invocationTarget);
            }

            return button;
        }

        private async Task InvokeExplorerCommandAsync(ExplorerCommandNode command, string? invocationTarget)
        {
            try
            {
                if (invocationTarget is null)
                    await ExplorerCommandAdapter.InvokeAsync(command.ClassId, command.SubCommandPath);
                else
                    await ExplorerCommandAdapter.InvokeAsync(command.ClassId, command.SubCommandPath, invocationTarget);
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"Could not invoke Explorer command '{command.Title}': {exception}");
                if (invocationTarget is null)
                    _dispatcherQueue?.TryEnqueue(() => _hostController?.OpenNativeDesktopContextMenu());
                else
                    ShellContextMenuService.ShowForItems([invocationTarget], _windowHandle);
            }
        }

        private static IconElement CreateExplorerCommandIcon() =>
            ContextMenuIconProvider.CreateForCommand("DesktopContext_ApplicationExtensions.Text", ElementTheme.Default);

        private void SetExplorerCommandIconWhenReady(Action<IconElement> setIcon, string? iconLocation)
        {
            if (string.IsNullOrWhiteSpace(iconLocation))
                return;

            _ = LoadExplorerCommandIconAsync(setIcon, iconLocation);
        }

        private void SetFileTypeIconWhenReady(Action<IconElement> setIcon, string extension)
        {
            _ = LoadFileTypeIconAsync(setIcon, extension);
        }

        private void SetDirectoryIconWhenReady(Action<IconElement> setIcon)
        {
            _ = LoadDirectoryIconAsync(setIcon);
        }

        private async Task LoadFileTypeIconAsync(Action<IconElement> setIcon, string extension)
        {
            try
            {
                var iconPath = await ShellIconService.ExtractFileTypeIconPathAsync(extension).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(iconPath) || _dispatcherQueue is null)
                    return;

                _dispatcherQueue.TryEnqueue(() =>
                {
                    if (_disposed)
                        return;

                    var icon = ShellIconService.CreateIconFromPath(iconPath);
                    if (icon is not null)
                        setIcon(icon);
                });
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"Could not load the Shell icon for '{extension}': {exception.Message}");
            }
        }

        private async Task LoadDirectoryIconAsync(Action<IconElement> setIcon)
        {
            try
            {
                var iconPath = await ShellIconService.ExtractDirectoryIconPathAsync().ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(iconPath) || _dispatcherQueue is null)
                    return;

                _dispatcherQueue.TryEnqueue(() =>
                {
                    if (_disposed)
                        return;

                    var icon = ShellIconService.CreateIconFromPath(iconPath);
                    if (icon is not null)
                        setIcon(icon);
                });
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"Could not load the Shell folder icon: {exception.Message}");
            }
        }

        private static void NormalizeMenuFlyoutSeparators(MenuFlyout flyout)
        {
            NormalizeSeparators(flyout.Items.Cast<object>().ToArray(), item => item is MenuFlyoutSeparator,
                item => item is FrameworkElement element && element.Visibility != Visibility.Collapsed,
                (item, visibility) => ((MenuFlyoutSeparator)item).Visibility = visibility);
        }

        private static void NormalizeCommandBarSeparators(CommandBarFlyout flyout)
        {
            NormalizeSeparators(flyout.PrimaryCommands.Cast<object>().ToArray(),
                item => item is AppBarSeparator,
                item => item is FrameworkElement element && element.Visibility != Visibility.Collapsed &&
                    item is AppBarButton button && button.IsEnabled,
                (item, visibility) => ((AppBarSeparator)item).Visibility = visibility);
            NormalizeSeparators(flyout.SecondaryCommands.Cast<object>().ToArray(),
                item => item is AppBarSeparator,
                item => item is FrameworkElement element && element.Visibility != Visibility.Collapsed &&
                    item is AppBarButton button && button.IsEnabled,
                (item, visibility) => ((AppBarSeparator)item).Visibility = visibility);
        }

        private static void NormalizeSeparators(
            IReadOnlyList<object> items,
            Func<object, bool> isSeparator,
            Func<object, bool> isVisibleCommand,
            Action<object, Visibility> setVisibility)
        {
            var index = 0;
            var hasVisibleCommandInGroup = false;
            while (index < items.Count)
            {
                if (!isSeparator(items[index]))
                {
                    if (isVisibleCommand(items[index]))
                        hasVisibleCommandInGroup = true;
                    index++;
                    continue;
                }

                var separatorStart = index;
                while (index < items.Count && isSeparator(items[index]))
                    index++;

                var hasVisibleCommandAfter = false;
                for (var next = index; next < items.Count && !isSeparator(items[next]); next++)
                {
                    if (isVisibleCommand(items[next]))
                    {
                        hasVisibleCommandAfter = true;
                        break;
                    }
                }

                var keepSeparator = hasVisibleCommandInGroup && hasVisibleCommandAfter;
                for (var separatorIndex = separatorStart; separatorIndex < index; separatorIndex++)
                    setVisibility(items[separatorIndex], keepSeparator && separatorIndex == separatorStart ? Visibility.Visible : Visibility.Collapsed);

                hasVisibleCommandInGroup = false;
            }
        }

        private async Task LoadExplorerCommandIconAsync(Action<IconElement> setIcon, string iconLocation)
        {
            try
            {
                var iconPath = await ShellIconService.ExtractResourceIconPathAsync(iconLocation).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(iconPath) || _dispatcherQueue is null)
                    return;

                _dispatcherQueue.TryEnqueue(() =>
                {
                    if (_disposed)
                        return;

                    var icon = ShellIconService.CreateIconFromPath(iconPath);
                    if (icon is not null)
                        setIcon(icon);
                });
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"Could not load an Explorer command icon: {exception.Message}");
            }
        }

        private static bool IsDirectMainMenuExtension(ExplorerCommandPackage package) =>
            package.Placement != DesktopContextMenuExtensionPlacement.AppExtensionsSubmenu;

        private RadioMenuFlyoutItem CreateOptionMenuItem(string value, string resourceKey, string groupName, Action<string> apply)
        {
            var item = new RadioMenuFlyoutItem
            {
                Name = $"Option_{value}",
                Text = GetResourceText(resourceKey),
                Icon = ContextMenuIconProvider.CreateForCommand(resourceKey, ElementTheme.Default),
                GroupName = groupName,
                Tag = value,
                DataContext = resourceKey
            };
            item.Click += (_, _) => apply(value);
            return item;
        }

        private static void AddViewShortcut(MenuFlyoutItem item, Windows.System.VirtualKey key)
        {
            item.KeyboardAccelerators.Add(new KeyboardAccelerator
            {
                Key = key,
                Modifiers = Windows.System.VirtualKeyModifiers.Control | Windows.System.VirtualKeyModifiers.Shift
            });
        }

        private void UpdateViewMenuItems()
        {
            var current = XiyiDesktop.AppSettingsStore.DesktopIconSize;
            var hasCustomIconSize = XiyiDesktop.AppSettingsStore.DesktopIconCustomSize is not null;
            foreach (var item in _iconSizeMenuItems)
                item.IsChecked = !hasCustomIconSize &&
                    string.Equals(item.Tag as string, current, StringComparison.Ordinal);

            if (_showIconsMenuItem is not null)
                _showIconsMenuItem.IsChecked = XiyiDesktop.AppSettingsStore.ShowDesktopIcons;
            if (_autoArrangeMenuItem is not null)
            {
                _autoArrangeMenuItem.IsChecked = XiyiDesktop.AppSettingsStore.DesktopAutoArrange;
                _autoArrangeMenuItem.IsEnabled = XiyiDesktop.AppSettingsStore.ShowDesktopIcons;
            }
            if (_alignToGridMenuItem is not null)
            {
                _alignToGridMenuItem.IsChecked = XiyiDesktop.AppSettingsStore.AlignIconsToGrid;
                _alignToGridMenuItem.IsEnabled = XiyiDesktop.AppSettingsStore.ShowDesktopIcons;
            }
        }

        private void UpdateSortMenuItems()
        {
            var current = XiyiDesktop.AppSettingsStore.DesktopSortMode;
            foreach (var item in _sortMenuItems)
                item.IsChecked = !string.Equals(current, "Manual", StringComparison.Ordinal) &&
                    string.Equals(item.Tag as string, current, StringComparison.Ordinal);
        }

        private void ToggleDesktopIcons_Click(object sender, RoutedEventArgs e)
        {
            var isVisible = _showIconsMenuItem?.IsChecked ?? !XiyiDesktop.AppSettingsStore.ShowDesktopIcons;
            XiyiDesktop.AppSettingsStore.ShowDesktopIcons = isVisible;
            DesktopItemsView.SetDesktopIconsVisible(XiyiDesktop.AppSettingsStore.ShowDesktopIcons);
            UpdateViewMenuItems();
        }

        private void AutoArrangeMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var enabled = _autoArrangeMenuItem?.IsChecked == true;
            SetDesktopAutoArrange(enabled);
        }

        private void AlignToGridMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var enabled = _alignToGridMenuItem?.IsChecked == true;
            SetDesktopGridAlignment(enabled);
        }

        private void SetDesktopAutoArrange(bool enabled)
        {
            var wasEnabled = XiyiDesktop.AppSettingsStore.DesktopAutoArrange;
            if (wasEnabled == enabled)
                return;

            XiyiDesktop.AppSettingsStore.DesktopAutoArrange = enabled;
            if (enabled)
            {
                if (string.Equals(XiyiDesktop.AppSettingsStore.DesktopSortMode, "Manual", StringComparison.Ordinal))
                    XiyiDesktop.AppSettingsStore.DesktopSortMode = "Name";
                ReorderDesktopItems(XiyiDesktop.AppSettingsStore.DesktopSortMode, persistLayout: false);
            }
            else
                SaveManualDesktopOrder();

            UpdateReorderAvailability();
            UpdateViewMenuItems();
        }

        private void SetDesktopGridAlignment(bool enabled)
        {
            XiyiDesktop.AppSettingsStore.AlignIconsToGrid = enabled;

            // Alignment changes should not implicitly sort icons; the custom panel switches
            // between snapped cells and the coordinates already saved for free placement.
            UpdateReorderAvailability();
            DesktopItemsView.InvalidateArrange();
            if (!XiyiDesktop.AppSettingsStore.DesktopAutoArrange)
            {
                DesktopItemsView.UpdateLayout();
                SaveManualDesktopOrder(updateLayout: false);
            }

            ResolveWidgetCollisions(persistChanges: false);
            SaveWidgetPlacements();
            RefreshSharedDesktopLayout();
            SaveManualDesktopOrder();
            UpdateViewMenuItems();
        }

        private void ApplyDesktopIconSize(string size)
        {
            XiyiDesktop.AppSettingsStore.DesktopIconSize = size;
            XiyiDesktop.AppSettingsStore.DesktopIconCustomSize = null;
            ApplyDesktopIconSizeToItems(GetDesktopIconSize());
            UpdateViewMenuItems();
        }

        private void ApplyDesktopIconSizeToItems(double iconSize)
        {
            foreach (var item in Items)
                item.SetIconSize(iconSize);

            DesktopItemsView.InvalidateMeasure();
            DesktopItemsView.UpdateLayout();
            DesktopItemsView.InvalidateArrange();
            if (!XiyiDesktop.AppSettingsStore.DesktopAutoArrange)
            {
                foreach (var item in Items)
                {
                    if (item.HasPosition)
                        DesktopItemsView.ClampItemPosition(item);
                }
                SaveManualDesktopOrder(updateLayout: false);
            }

            ScheduleDesktopIconImageRefresh();
            ApplyDesktopWidgetScale();
        }

        private void ApplyDesktopSortMode(string sortMode)
        {
            var currentSortMode = XiyiDesktop.AppSettingsStore.DesktopSortMode;
            if (string.Equals(currentSortMode, sortMode, StringComparison.Ordinal))
                _sortDescending = !_sortDescending;
            else
                _sortDescending = false;

            XiyiDesktop.AppSettingsStore.DesktopSortDescending = _sortDescending;
            XiyiDesktop.AppSettingsStore.DesktopSortMode = sortMode;
            XiyiDesktop.AppSettingsStore.DesktopAutoArrange = true;
            UpdateReorderAvailability();
            UpdateSortMenuItems();
            UpdateViewMenuItems();
            ReorderDesktopItems(sortMode, persistLayout: sortMode == "Manual");
        }

        private void UpdateReorderAvailability()
        {
            DesktopItemsView.InvalidateArrange();
        }

        private void ReorderDesktopItems(string sortMode, bool persistLayout)
        {
            using var shellComparer = DesktopShellItemComparer.TryCreate(Items.Select(item => item.Item), sortMode);
            IEnumerable<DesktopItemViewModel> ordered;
            if (!string.Equals(sortMode, "Manual", StringComparison.Ordinal))
            {
                var allItems = Items.AsEnumerable();
                if (shellComparer is not null)
                {
                    var shellOrdered = allItems.OrderBy(item => item.Item, shellComparer);
                    if (string.Equals(sortMode, "Type", StringComparison.Ordinal))
                        ordered = shellOrdered;
                    else
                        ordered = shellOrdered.ThenBy(item => item.DisplayName, Comparer<string>.Create(CompareShellNames));
                }
                else
                {
                    ordered = sortMode switch
                    {
                        "Name" => allItems.OrderBy(item => item.DisplayName, Comparer<string>.Create(CompareShellNames)),
                        "Size" => allItems.OrderBy(item => item.Item.IsDirectory ? 0 : 1)
                            .ThenBy(item => item.Item.SizeBytes ?? -1)
                            .ThenBy(item => item.DisplayName, Comparer<string>.Create(CompareShellNames)),
                        "Type" => allItems
                            .OrderBy(item => item.Item.IsSpecial ? 0 : 1)
                            .ThenBy(item => item.Item.IsSpecial
                                ? DesktopShellItemComparer.GetSpecialItemTypeOrder(item.Item.Kind)
                                : 0)
                            .ThenBy(GetDesktopItemTypeName, Comparer<string>.Create(CompareShellNames))
                            .ThenBy(item => item.DisplayName, Comparer<string>.Create(CompareShellNames)),
                        "Modified" => allItems.OrderBy(item => item.Item.ModifiedTimeUtc is null ? 1 : 0)
                            .ThenBy(item => item.Item.ModifiedTimeUtc ?? DateTimeOffset.MaxValue)
                            .ThenBy(item => item.DisplayName, Comparer<string>.Create(CompareShellNames)),
                        _ => _layoutStore.ApplyOrder(allItems.Select(item => item.Item))
                            .Select(item => allItems.First(viewModel => string.Equals(
                                viewModel.Item.Identity,
                                item.Identity,
                                StringComparison.OrdinalIgnoreCase)))
                    };
                }
            }
            else
            {
                ordered = _layoutStore.ApplyOrder(Items.Select(item => item.Item))
                    .Select(item => Items.First(viewModel => string.Equals(
                        viewModel.Item.Identity,
                        item.Identity,
                        StringComparison.OrdinalIgnoreCase)));
            }

            var targetOrder = ordered.ToArray();
            if (_sortDescending && !string.Equals(sortMode, "Manual", StringComparison.Ordinal))
            {
                if (string.Equals(sortMode, "Modified", StringComparison.Ordinal))
                {
                    var knownDates = targetOrder.Where(item => item.Item.ModifiedTimeUtc is not null).Reverse();
                    var unknownDates = targetOrder.Where(item => item.Item.ModifiedTimeUtc is null);
                    targetOrder = knownDates.Concat(unknownDates).ToArray();
                }
                else
                {
                    Array.Reverse(targetOrder);
                }
            }
            _isApplyingItemOrder = true;
            try
            {
                // Replacing the collection contents forces a full items-surface refresh before the
                // custom panel assigns the new shell order to its grid or free-position slots.
                Items.Clear();
                foreach (var item in targetOrder)
                    Items.Add(item);
            }
            finally
            {
                _isApplyingItemOrder = false;
            }

            DesktopItemsView.SetItems(targetOrder);

            if (persistLayout)
                SaveManualDesktopOrder();
        }

        private static string GetDesktopItemTypeName(DesktopItemViewModel item) => item.Item.IsSpecial
            ? ShellIconService.GetShellTypeName(DesktopItem.GetShellParsingName(item.Item.Kind))
            : ShellIconService.GetFileTypeName(item.Item.FileExtension, item.Item.IsDirectory);

        private string GetDesktopItemDisplayName(DesktopItem item) => item.IsSpecial && !string.IsNullOrWhiteSpace(item.Name)
            ? item.Name
            : item.DisplayNameResourceKey is { } resourceKey
                ? _resources.GetString(resourceKey)
                : item.Name;

        private static int CompareShellNames(string left, string right)
        {
            var result = StrCmpLogicalW(left, right);
            return result != 0 ? result : StringComparer.OrdinalIgnoreCase.Compare(left, right);
        }

        private void SaveManualDesktopOrder(bool updateLayout = true)
        {
            if (!XiyiDesktop.AppSettingsStore.DesktopAutoArrange)
            {
                DesktopItemsView.InvalidateArrange();
                if (updateLayout)
                    DesktopItemsView.UpdateLayout();
                _layoutStore.SaveLayout(
                    Items.Select(item => item.Item.Identity),
                    Items.Where(item => item.HasPosition).Select(item =>
                        new DesktopLayoutStore.DesktopItemPosition(
                            item.Item.Identity,
                            item.PositionX,
                            item.PositionY,
                            Math.Max(1, DesktopItemsView.ActualWidth),
                            Math.Max(1, DesktopItemsView.ActualHeight))));
            }
        }

        private async void UndoDelete_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var restoredCount = await DesktopFileOperationsService.UndoLastRecycleAsync(_windowHandle);
                if (restoredCount > 0)
                    await RefreshDesktopItemsAsync();
                UpdateUndoDeleteVisibility();
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"Could not restore the most recently deleted desktop items: {exception}");
                await ShowOperationErrorAsync();
                UpdateUndoDeleteVisibility();
            }
        }

        private async void EmptyRecycleBin_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (await ShellActivationService.EmptyRecycleBinAsync(_windowHandle))
                {
                    DesktopFileOperationsService.ClearUndoHistory();
                    UpdateUndoDeleteVisibility();
                    await RefreshDesktopItemsAsync();
                }
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"Could not empty the Recycle Bin: {exception}");
                await ShowOperationErrorAsync();
            }
        }

        private void UpdateUndoDeleteVisibility()
        {
            if (DesktopFileOperationsService.NeedsRefresh)
                _ = RefreshUndoAvailabilityAsync();
            ApplyUndoDeleteVisibility();
        }

        private async Task RefreshUndoAvailabilityAsync()
        {
            if (_disposed)
                return;
            if (_undoAvailabilityRefreshTask is { IsCompleted: false })
                return;

            var refreshTask = RefreshUndoAvailabilityCoreAsync();
            _undoAvailabilityRefreshTask = refreshTask;
            await refreshTask;
        }

        private async Task RefreshUndoAvailabilityCoreAsync()
        {
            try
            {
                await DesktopFileOperationsService.RefreshUndoAvailabilityAsync(_windowHandle);
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"Could not refresh desktop undo availability: {exception}");
            }
            finally
            {
                if (!_disposed)
                    ApplyUndoDeleteVisibility();
            }
        }

        private void ApplyUndoDeleteVisibility()
        {
            var visibility = DesktopFileOperationsService.CanUndoDelete ? Visibility.Visible : Visibility.Collapsed;
            if (_undoDeleteMenuItem is not null)
                _undoDeleteMenuItem.Visibility = visibility;
            if (_legacyUndoDeleteButton is not null)
            {
                _legacyUndoDeleteButton.Visibility = visibility;
                _legacyUndoDeleteButton.Label = GetResourceText("DesktopContext_UndoDelete.Text");
            }
        }

        private static bool RecycleBinContainsItems()
        {
            var info = new ShellQueryRecycleBinInfo { Size = Marshal.SizeOf<ShellQueryRecycleBinInfo>() };
            try
            {
                // If Windows cannot report the state, keep the command available.
                // A failed query must not make a non-empty Recycle Bin inaccessible.
                return SHQueryRecycleBin(null, ref info) < 0 || info.NumberOfItems > 0;
            }
            catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
            {
                System.Diagnostics.Debug.WriteLine($"Unable to query the Recycle Bin: {exception.Message}");
                return true;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ShellQueryRecycleBinInfo
        {
            public int Size;
            public long TotalSize;
            public long NumberOfItems;
        }

        [DllImport("shell32.dll", EntryPoint = "SHQueryRecycleBinW", CharSet = CharSet.Unicode)]
        private static extern int SHQueryRecycleBin(string? rootPath, ref ShellQueryRecycleBinInfo recycleBinInfo);

        private async void NewFolder_Click(object sender, RoutedEventArgs e) =>
            await CreateNewDesktopItemAsync(_resources.GetString("DesktopContext_NewFolderName"), isDirectory: true, promptToRename: true);

        private async void CreateNewTemplateItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuFlyoutItem menuItem || menuItem.Tag is not string extension)
                return;

            var template = ShellNewTemplateCatalog.GetSupportedTemplates().FirstOrDefault(value =>
                string.Equals(value.Extension, extension, StringComparison.OrdinalIgnoreCase));
            if (template is null)
                return;

            await CreateNewDesktopItemAsync(
                _resources.GetString(template.DefaultNameResourceKey),
                isDirectory: false,
                promptToRename: true,
                template: template);
        }

        private async Task CreateNewDesktopItemAsync(
            string name,
            bool isDirectory,
            bool promptToRename,
            ShellNewTemplate? template = null)
        {
            try
            {
                var created = false;
                var createdPath = string.Empty;
                if (template is not null)
                {
                    if (template.ContentKind == ShellNewContentKind.Shortcut)
                    {
                        var targetDialog = new DesktopActionDialog(
                            GetResourceText("DesktopContext_NewShortcut.Text"),
                            _resources.GetString("DesktopContext_NewShortcutTarget"),
                            _resources.GetString("DesktopAction_Confirm"),
                            _resources.GetString("DesktopAction_Skip"),
                            inputPlaceholder: _resources.GetString("DesktopRename_Placeholder"),
                            showInput: true)
                        {
                            XamlRoot = _desktopRoot.XamlRoot
                        };

                        if (await targetDialog.ShowAsync() != ContentDialogResult.Primary ||
                            string.IsNullOrWhiteSpace(targetDialog.InputValue))
                            return;

                        var targetPath = targetDialog.InputValue.Trim().Trim('"');
                        if (!File.Exists(targetPath) && !Directory.Exists(targetPath))
                        {
                            await ShowOperationErrorAsync();
                            return;
                        }
                        targetPath = Path.GetFullPath(targetPath);

                        var requestedShortcutName = name;
                        for (var attempt = 0; attempt < 100 && !created; attempt++)
                        {
                            name = GetAvailableNewItemName(requestedShortcutName, isDirectory: false);
                            createdPath = Path.Combine(GetDesktopPath(), name);
                            if (File.Exists(createdPath) || Directory.Exists(createdPath))
                                continue;

                            try
                            {
                                await DesktopShortcutService.CreateAsync(createdPath, targetPath);
                                created = true;
                            }
                            catch (IOException) when (File.Exists(createdPath) || Directory.Exists(createdPath))
                            {
                                if (attempt == 99)
                                    throw;
                            }
                        }
                    }

                    var requestedName = name;
                    for (var attempt = 0; template.ContentKind != ShellNewContentKind.Shortcut && attempt < 100 && !created; attempt++)
                    {
                        name = GetAvailableNewItemName(requestedName, isDirectory: false);
                        createdPath = Path.Combine(GetDesktopPath(), name);
                        try
                        {
                            await Task.Run(() => ShellNewTemplateCatalog.Create(template, createdPath));
                            created = true;
                        }
                        catch (ShellNewDestinationConflictException)
                        {
                            // Another process created the same name after our availability check.
                            // Retry with the next available shell-style suffix.
                            if (attempt == 99)
                                throw;
                        }
                    }
                }
                else
                {
                    name = GetAvailableNewItemName(name, isDirectory);
                    createdPath = Path.Combine(GetDesktopPath(), name);
                    var result = await DesktopFileOperationsService.CreateNewItemAsync(GetDesktopPath(), name, isDirectory, _windowHandle);
                    created = result.Status == DesktopFileOperationStatus.Completed;
                }

                if (created)
                {
                    if (promptToRename)
                        await PromptToRenameCreatedItemAsync(createdPath);
                    await RefreshDesktopItemsAsync();
                }
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"Could not create a desktop item: {exception}");
                await ShowOperationErrorAsync();
            }
        }

        private static string GetAvailableNewItemName(string requestedName, bool isDirectory)
        {
            var desktopPath = GetDesktopPath();
            var extension = isDirectory ? string.Empty : Path.GetExtension(requestedName);
            var stem = isDirectory ? requestedName : Path.GetFileNameWithoutExtension(requestedName);
            var candidate = requestedName;
            var suffix = 2;

            while (File.Exists(Path.Combine(desktopPath, candidate)) || Directory.Exists(Path.Combine(desktopPath, candidate)))
            {
                candidate = $"{stem} ({suffix++}){extension}";
            }

            return candidate;
        }

        private async Task PromptToRenameCreatedItemAsync(string createdPath)
        {
            var dialog = new DesktopActionDialog(
                _resources.GetString("DesktopRename_Title"),
                _resources.GetString("DesktopRename_Message"),
                _resources.GetString("DesktopAction_Confirm"),
                _resources.GetString("DesktopAction_Skip"),
                inputText: Path.GetFileNameWithoutExtension(createdPath),
                inputPlaceholder: _resources.GetString("DesktopRename_Placeholder"),
                showInput: true)
            {
                XamlRoot = _desktopRoot.XamlRoot
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary ||
                string.IsNullOrWhiteSpace(dialog.InputValue))
                return;

            var extension = Path.GetExtension(createdPath);
            var newName = dialog.InputValue.Trim();
            if (!string.IsNullOrEmpty(extension) && !newName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                newName += extension;

            var currentName = Path.GetFileName(createdPath);
            if (string.Equals(currentName, newName, StringComparison.OrdinalIgnoreCase))
                return;

            try
            {
                await DesktopFileOperationsService.RenameAsync(createdPath, newName, _windowHandle);
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"Could not rename new desktop item: {exception}");
                await ShowOperationErrorAsync();
            }
        }

        private async void DisplaySettings_Click(object sender, RoutedEventArgs e) => await OpenSettingsUriAsync("ms-settings:display");

        private async void Personalize_Click(object sender, RoutedEventArgs e) => await OpenSettingsUriAsync("ms-settings:personalization");

        private async Task OpenSettingsUriAsync(string uri)
        {
            try
            {
                ShellActivationService.Open(uri, _windowHandle);
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"Could not open Windows Settings ({uri}): {exception}");
                await ShowOperationErrorAsync();
            }
        }

        private void OpenNativeDesktopContextMenu_Click(object sender, RoutedEventArgs e)
        {
            DismissActiveContextFlyout(() =>
            {
                if (_hostController?.ShowClassicDesktopBackgroundContextMenu() != true)
                    System.Diagnostics.Debug.WriteLine("Could not show the classic desktop background context menu.");
            });
        }

        private async void CustomizeMenu_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:personalization-contextmenu")))
                    return;
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"The context-menu settings page is unavailable: {exception.Message}");
            }

            await OpenSettingsUriAsync("ms-settings:personalization");
        }

        private void ShowNativeItemContextMenu_Click(object sender, RoutedEventArgs e)
        {
            var selected = GetSelectedItems();
            if (selected.Length != 1)
                return;

            var item = selected[0];
            var target = item.Path ?? DesktopItem.GetShellParsingName(item.Kind);
            DismissActiveContextFlyout(() =>
            {
                if (!ShellContextMenuService.ShowForItems([target], _windowHandle))
                    System.Diagnostics.Debug.WriteLine($"Could not show the native context menu for '{target}'.");
            });
        }

        private void DismissActiveContextFlyout(Action action)
        {
            var flyout = _activeContextFlyout;
            _activeContextFlyout = null;
            flyout?.Hide();
            HideContextMenuHost();
            if (_dispatcherQueue?.TryEnqueue(() => action()) != true)
                action();
        }

        private MenuFlyout CreateItemContextFlyout()
        {
            var flyout = new MenuFlyout();
            flyout.MenuFlyoutPresenterStyle = MenuFlyoutVisuals.CreateAcrylicPresenterStyle();
            flyout.Items.Add(CreateMenuItem("CutMenuItem", "DesktopItemContext_Cut/Text", "\uE8C6", CutItem_Click));
            flyout.Items.Add(CreateMenuItem("CopyMenuItem", "DesktopItemContext_Copy/Text", "\uE8C8", CopyItem_Click));
            flyout.Items.Add(CreateMenuItem("RenameMenuItem", "DesktopItemContext_Rename/Text", "\uE8AC", RenameItem_Click));
            _itemShareMenuItem = CreateMenuItem("ShareMenuItem", "DesktopItemContext_Share.Text", "\uE72D", ShareItem_Click);
            flyout.Items.Add(_itemShareMenuItem);
            flyout.Items.Add(CreateMenuItem("DeleteMenuItem", "DesktopItemContext_Delete/Text", "\uE74D", DeleteItem_Click));
            flyout.Items.Add(new MenuFlyoutSeparator());
            _itemOpenMenuItem = CreateMenuItem("OpenMenuItem", "DesktopItemContext_Open/Text", "\uE8E5", OpenItem_Click);
            flyout.Items.Add(_itemOpenMenuItem);
            _itemOpenFileLocationMenuItem = CreateMenuItem("OpenFileLocationMenuItem", "DesktopItemContext_OpenFileLocation.Text", "\uE8B7", OpenFileLocation_Click);
            flyout.Items.Add(_itemOpenFileLocationMenuItem);
            _itemRunAsAdminMenuItem = CreateMenuItem("RunAsAdministratorMenuItem", "DesktopItemContext_RunAsAdministrator.Text", "\uE7EF", RunAsAdministrator_Click);
            flyout.Items.Add(_itemRunAsAdminMenuItem);
            _itemPasteMenuItem = CreateMenuItem("ItemPasteMenuItem", "DesktopItemContext_Paste/Text", "\uE77F", Paste_Click);
            flyout.Items.Add(_itemPasteMenuItem);
            _manageThisPcMenuItem = CreateMenuItem("ManageThisPcMenuItem", "DesktopItemContext_ManageThisPc.Text", "\uE90F", ManageThisPc_Click);
            flyout.Items.Add(_manageThisPcMenuItem);
            _emptyRecycleBinMenuItem = CreateMenuItem("EmptyRecycleBinMenuItem", "DesktopItemContext_EmptyRecycleBin.Text", "\uE74D", EmptyRecycleBin_Click);
            flyout.Items.Add(_emptyRecycleBinMenuItem);
            flyout.Items.Add(new MenuFlyoutSeparator());
            _sendToMenuItem = CreateSubMenu("SendToMenuItem", "DesktopItemContext_SendTo.Text", "\uE72A");
            flyout.Items.Add(_sendToMenuItem);
            _printMenuItem = CreateMenuItem("PrintMenuItem", "DesktopItemContext_Print.Text", "\uE749", PrintItem_Click);
            flyout.Items.Add(_printMenuItem);
            _createShortcutMenuItem = CreateMenuItem("CreateShortcutMenuItem", "DesktopItemContext_CreateShortcut.Text", "\uE8A7", CreateShortcutItem_Click);
            flyout.Items.Add(_createShortcutMenuItem);
            _copyAsPathMenuItem = CreateMenuItem("CopyAsPathMenuItem", "DesktopItemContext_CopyAsPath.Text", "\uE71B", CopyAsPathItem_Click);
            flyout.Items.Add(_copyAsPathMenuItem);
            _rotateImageMenuItem = new MenuFlyoutSubItem
            {
                Name = "RotateImageMenuItem",
                Text = GetResourceText("DesktopItemContext_RotateImage.Text"),
                Icon = ContextMenuIconProvider.CreateForCommand("DesktopItemContext_RotateImage.Text", ElementTheme.Default)
            };
            _rotateImageMenuItem.Items.Add(CreateMenuItem("RotateImageLeftMenuItem", "DesktopItemContext_RotateLeft.Text", "\uE7AD", RotateImageLeft_Click));
            _rotateImageMenuItem.Items.Add(CreateMenuItem("RotateImageRightMenuItem", "DesktopItemContext_RotateRight.Text", "\uE7AE", RotateImageRight_Click));
            flyout.Items.Add(_rotateImageMenuItem);
            flyout.Items.Add(new MenuFlyoutSeparator());
            _itemApplicationExtensionsItem = CreateSubMenu("ItemApplicationExtensionsMenuItem", "DesktopContext_ApplicationExtensions.Text", "\uE74C");
            flyout.Items.Add(_itemApplicationExtensionsItem);
            _itemExtensionsSeparator = new MenuFlyoutSeparator();
            flyout.Items.Add(_itemExtensionsSeparator);
            _itemCustomizeMenuItem = CreateMenuItem("ItemCustomizeMenuItem", "DesktopContext_CustomizeMenu.Text", "\uE713", CustomizeMenu_Click);
            flyout.Items.Add(_itemCustomizeMenuItem);
            _itemShowMoreOptionsMenuItem = CreateMenuItem("ItemShowMoreOptionsMenuItem", "DesktopContext_ShowMoreOptions.Text", "\uE8A7", ShowNativeItemContextMenu_Click);
            flyout.Items.Add(_itemShowMoreOptionsMenuItem);
            _movePropertiesSeparator = new MenuFlyoutSeparator();
            flyout.Items.Add(_movePropertiesSeparator);
            _movePropertiesMenuItem = CreateMenuItem("PropertiesMenuItem", "DesktopItemContext_Properties/Text", "\uE946", PropertiesItem_Click);
            flyout.Items.Add(_movePropertiesMenuItem);
            flyout.Opening += ItemContextFlyout_Opening;
            return flyout;
        }

        private Task RefreshSendToMenuAsync(MenuFlyoutSubItem menu, IReadOnlyList<string> sourcePaths) =>
            RefreshSendToMenuCoreAsync(menu.Items.Clear, item => menu.Items.Add(item), value =>
            {
                menu.Visibility = value;
            }, sourcePaths);

        private Task RefreshSendToMenuAsync(MenuFlyout menu, IReadOnlyList<string> sourcePaths) =>
            RefreshSendToMenuCoreAsync(menu.Items.Clear, item => menu.Items.Add(item), value =>
            {
                SetMenuItemVisibility(_legacySendToButton, value == Visibility.Visible);
                if (_legacyItemCommandBarFlyout is not null)
                    NormalizeCommandBarSeparators(_legacyItemCommandBarFlyout);
            }, sourcePaths);

        private async Task RefreshSendToMenuCoreAsync(
            Action clearItems,
            Action<MenuFlyoutItemBase> addItem,
            Action<Visibility> setMenuVisibility,
            IReadOnlyList<string> sourcePaths)
        {
            var generation = ++_sendToRefreshGeneration;
            IReadOnlyList<SendToDestination> destinations;
            try
            {
                destinations = await GetSendToDestinationsAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"Could not read Windows Send To destinations: {exception.Message}");
                destinations = [];
            }

            if (_disposed || generation != _sendToRefreshGeneration || _dispatcherQueue is null)
                return;

            _dispatcherQueue.TryEnqueue(() =>
            {
                if (_disposed || generation != _sendToRefreshGeneration)
                    return;

                clearItems();
                for (var index = 0; index < destinations.Count; index++)
                {
                    var destination = destinations[index];
                    var item = CreateMenuItem(
                        $"SendToDestination{index}",
                        "DesktopItemContext_SendTo.Text",
                        "\uE72A",
                        async (_, _) => await SendToDestinationAsync(destination, sourcePaths));
                    item.Text = destination.Name;
                    item.Icon = ShellIconService.CreateIconFromPath(destination.IconPath) ??
                        ContextMenuIconProvider.Create("link.svg", ElementTheme.Default);
                    addItem(item);
                }
                setMenuVisibility(destinations.Count > 0 ? Visibility.Visible : Visibility.Collapsed);
                if (_activeContextFlyout is MenuFlyout parentMenu)
                    NormalizeMenuFlyoutSeparators(parentMenu);
                else if (_activeContextFlyout is CommandBarFlyout parentCommandBar)
                    NormalizeCommandBarSeparators(parentCommandBar);
            });
        }

        private static Task<IReadOnlyList<SendToDestination>> GetSendToDestinationsAsync()
        {
            lock (SendToDestinationCacheLock)
            {
                if (_sendToDestinationCacheTask is not null &&
                    DateTimeOffset.UtcNow < _sendToDestinationCacheExpiresUtc)
                {
                    return _sendToDestinationCacheTask;
                }

                _sendToDestinationCacheExpiresUtc = DateTimeOffset.UtcNow + SendToDestinationCacheLifetime;
                return _sendToDestinationCacheTask = EnumerateSendToDestinationsAsync();
            }
        }

        private static async Task<IReadOnlyList<SendToDestination>> EnumerateSendToDestinationsAsync()
        {
            var sendToPath = Environment.GetFolderPath(Environment.SpecialFolder.SendTo);
            if (string.IsNullOrWhiteSpace(sendToPath) || !Directory.Exists(sendToPath))
                return [];

            var paths = await Task.Run(() => Directory.EnumerateFileSystemEntries(sendToPath).ToArray())
                .ConfigureAwait(false);
            var result = new List<SendToDestination>();
            foreach (var path in paths)
            {
                try
                {
                    if (string.Equals(Path.GetFileName(path), "desktop.ini", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var attributes = File.GetAttributes(path);
                    if ((attributes & (System.IO.FileAttributes.Hidden | System.IO.FileAttributes.System)) != 0)
                        continue;

                    var extension = Path.GetExtension(path);
                    var targetPath = extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase)
                        ? await DesktopShortcutService.GetTargetPathAsync(path).ConfigureAwait(false)
                        : extension.Equals(".mydocs", StringComparison.OrdinalIgnoreCase)
                            ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                            : path;
                    if (string.IsNullOrWhiteSpace(targetPath))
                        targetPath = path;

                    var displayName = ShellActivationService.GetShellDisplayName(path);
                    var iconPath = await ShellIconService.ExtractShellItemIconPathAsync(path).ConfigureAwait(false);

                    result.Add(new SendToDestination(
                        string.IsNullOrWhiteSpace(displayName) ? Path.GetFileNameWithoutExtension(path) : displayName,
                        path,
                        targetPath,
                        Directory.Exists(targetPath),
                        iconPath));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or COMException or ArgumentException)
                {
                    System.Diagnostics.Debug.WriteLine($"Skipping Send To entry '{path}': {exception.Message}");
                }
            }

            return result.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }

        private async Task SendToDestinationAsync(SendToDestination destination, IReadOnlyList<string> sourcePaths)
        {
            if (sourcePaths.Count == 0)
                return;

            try
            {
                if (destination.IsDirectory)
                {
                    await DesktopFileOperationsService.CopyToAsync(sourcePaths, destination.TargetPath, ownerWindow: _windowHandle);
                }
                else if (Path.GetExtension(destination.ShortcutPath).Equals(".DeskLink", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var sourcePath in sourcePaths)
                    {
                        var targetName = Path.GetFileNameWithoutExtension(sourcePath);
                        var shortcutPath = GetAvailableNewItemPath(Path.Combine(GetDesktopPath(), $"{targetName}.lnk"));
                        await DesktopShortcutService.CreateAsync(shortcutPath, sourcePath);
                    }
                }
                else if (Path.GetExtension(destination.ShortcutPath).Equals(".ZFSendToTarget", StringComparison.OrdinalIgnoreCase))
                {
                    await CreateCompressedArchiveAsync(sourcePaths);
                }
                else
                {
                    var arguments = string.Join(" ", sourcePaths.Select(path => $"\"{path}\""));
                    ShellActivationService.OpenWithArguments(destination.ShortcutPath, arguments, _windowHandle);
                }

                await RefreshDesktopItemsAsync();
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"Could not send selected items to '{destination.Name}': {exception.Message}");
                if (exception is not System.ComponentModel.Win32Exception { NativeErrorCode: 1223 })
                    await ShowOperationErrorAsync();
            }
        }

        private static Task CreateCompressedArchiveAsync(IReadOnlyList<string> sourcePaths) => Task.Run(() =>
        {
            var baseName = sourcePaths.Count == 1
                ? Path.GetFileNameWithoutExtension(Path.TrimEndingDirectorySeparator(sourcePaths[0]))
                : "Compressed Folder";
            if (string.IsNullOrWhiteSpace(baseName))
                baseName = "Compressed Folder";
            var archivePath = GetAvailableNewItemPath(Path.Combine(GetDesktopPath(), $"{baseName}.zip"));
            using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
            foreach (var sourcePath in sourcePaths)
            {
                var entryRoot = Path.GetFileName(Path.TrimEndingDirectorySeparator(sourcePath));
                if (File.Exists(sourcePath))
                {
                    archive.CreateEntryFromFile(sourcePath, entryRoot, CompressionLevel.Optimal);
                    continue;
                }

                foreach (var file in Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories))
                {
                    var relativePath = Path.GetRelativePath(Path.GetDirectoryName(sourcePath)!, file)
                        .Replace(Path.DirectorySeparatorChar, '/');
                    archive.CreateEntryFromFile(file, relativePath, CompressionLevel.Optimal);
                }
            }
        });

        private static string GetAvailableNewItemPath(string path)
        {
            if (!File.Exists(path) && !Directory.Exists(path))
                return path;

            var directory = Path.GetDirectoryName(path)!;
            var extension = Path.GetExtension(path);
            var name = Path.GetFileNameWithoutExtension(path);
            for (var index = 2; ; index++)
            {
                var candidate = Path.Combine(directory, $"{name} ({index}){extension}");
                if (!File.Exists(candidate) && !Directory.Exists(candidate))
                    return candidate;
            }
        }

        private sealed record SendToDestination(string Name, string ShortcutPath, string TargetPath, bool IsDirectory, string? IconPath);

        private CommandBarFlyout CreateLegacyItemCommandBarFlyout()
        {
            var flyout = new CommandBarFlyout
            {
                AlwaysExpanded = true,
                SystemBackdrop = new DesktopAcrylicBackdrop()
            };
            _legacyItemCommandBarFlyout = flyout;

            _legacyItemOpenButton = CreateAppBarButton("DesktopItemContext_Open/Text", "\uE8E5", OpenItem_Click);
            flyout.SecondaryCommands.Add(_legacyItemOpenButton);
            _legacyItemOpenLocationButton = CreateAppBarButton("DesktopItemContext_OpenFileLocation.Text", "\uE8B7", OpenFileLocation_Click);
            flyout.SecondaryCommands.Add(_legacyItemOpenLocationButton);
            _legacyItemRunAsButton = CreateAppBarButton("DesktopItemContext_RunAsAdministrator.Text", "\uE7EF", RunAsAdministrator_Click);
            flyout.SecondaryCommands.Add(_legacyItemRunAsButton);
            _legacyManageThisPcButton = CreateAppBarButton("DesktopItemContext_ManageThisPc.Text", "\uE90F", ManageThisPc_Click);
            flyout.SecondaryCommands.Add(_legacyManageThisPcButton);
            _legacyEmptyRecycleBinButton = CreateAppBarButton("DesktopItemContext_EmptyRecycleBin.Text", "\uE74D", EmptyRecycleBin_Click);
            flyout.SecondaryCommands.Add(_legacyEmptyRecycleBinButton);

            _legacyItemCutButton = CreateAppBarButton("DesktopItemContext_Cut/Text", "\uE8C6", CutItem_Click);
            flyout.PrimaryCommands.Add(_legacyItemCutButton);
            _legacyItemCopyButton = CreateAppBarButton("DesktopItemContext_Copy/Text", "\uE8C8", CopyItem_Click);
            flyout.PrimaryCommands.Add(_legacyItemCopyButton);
            _legacyItemPasteButton = CreateAppBarButton("DesktopItemContext_Paste/Text", "\uE77F", Paste_Click);
            flyout.PrimaryCommands.Add(_legacyItemPasteButton);
            _legacyItemRenameButton = CreateAppBarButton("DesktopItemContext_Rename/Text", "\uE8AC", RenameItem_Click);
            flyout.PrimaryCommands.Add(_legacyItemRenameButton);
            _legacyItemShareButton = CreateAppBarButton("DesktopItemContext_Share.Text", "\uE72D", ShareItem_Click);
            flyout.PrimaryCommands.Add(_legacyItemShareButton);
            _legacyItemDeleteButton = CreateAppBarButton("DesktopItemContext_Delete/Text", "\uE74D", DeleteItem_Click);
            flyout.PrimaryCommands.Add(_legacyItemDeleteButton);
            _legacyItemApplicationExtensionsFlyout = new MenuFlyout();
            _legacyItemApplicationExtensionsFlyout.MenuFlyoutPresenterStyle = MenuFlyoutVisuals.CreateAcrylicPresenterStyle();
            _legacyItemApplicationExtensionsButton = CreateAppBarButton("DesktopContext_ApplicationExtensions.Text", "\uE74C", (_, _) => { });
            _legacyItemApplicationExtensionsButton.Flyout = _legacyItemApplicationExtensionsFlyout;
            _legacyItemPropertiesButton = CreateAppBarButton("DesktopItemContext_Properties/Text", "\uE946", PropertiesItem_Click);
            var propertiesAtBottom = _contextMenuPreferences.Read().IsEnabled("MoveProperties\\Enabled") != false;

            _legacySendToButton = CreateAppBarButton("DesktopItemContext_SendTo.Text", "\uE72A", (_, _) => { });
            var sendToMenuFlyout = new MenuFlyout();
            sendToMenuFlyout.MenuFlyoutPresenterStyle = MenuFlyoutVisuals.CreateAcrylicPresenterStyle();
            _legacySendToButton.Flyout = sendToMenuFlyout;
            sendToMenuFlyout.Opening += (_, _) =>
            {
                var paths = GetSelectedItems().Where(item => item.Path is not null).Select(item => item.Path!).ToArray();
                _ = RefreshSendToMenuAsync(sendToMenuFlyout, paths);
            };
            flyout.SecondaryCommands.Add(_legacySendToButton);
            _legacyPrintButton = CreateAppBarButton("DesktopItemContext_Print.Text", "\uE749", PrintItem_Click);
            flyout.SecondaryCommands.Add(_legacyPrintButton);
            _legacyCreateShortcutButton = CreateAppBarButton("DesktopItemContext_CreateShortcut.Text", "\uE8A7", CreateShortcutItem_Click);
            flyout.SecondaryCommands.Add(_legacyCreateShortcutButton);
            _legacyCopyAsPathButton = CreateAppBarButton("DesktopItemContext_CopyAsPath.Text", "\uE71B", CopyAsPathItem_Click);
            flyout.SecondaryCommands.Add(_legacyCopyAsPathButton);
            _legacyRotateImageButton = CreateAppBarButton("DesktopItemContext_RotateImage.Text", "\uE7AD", (_, _) => { });
            var rotateMenu = new MenuFlyout();
            rotateMenu.MenuFlyoutPresenterStyle = MenuFlyoutVisuals.CreateAcrylicPresenterStyle();
            rotateMenu.Items.Add(CreateMenuItem("LegacyRotateImageLeftMenuItem", "DesktopItemContext_RotateLeft.Text", "\uE7AD", RotateImageLeft_Click));
            rotateMenu.Items.Add(CreateMenuItem("LegacyRotateImageRightMenuItem", "DesktopItemContext_RotateRight.Text", "\uE7AE", RotateImageRight_Click));
            _legacyRotateImageButton.Flyout = rotateMenu;
            flyout.SecondaryCommands.Add(_legacyRotateImageButton);
            flyout.SecondaryCommands.Add(_legacyItemApplicationExtensionsButton);
            _legacyItemExtensionsSeparator = new AppBarSeparator();
            flyout.SecondaryCommands.Add(_legacyItemExtensionsSeparator);
            if (!propertiesAtBottom)
                flyout.SecondaryCommands.Add(_legacyItemPropertiesButton);
            _legacyItemCustomizeMenuButton = CreateAppBarButton("DesktopContext_CustomizeMenu.Text", "\uE713", CustomizeMenu_Click);
            flyout.SecondaryCommands.Add(_legacyItemCustomizeMenuButton);
            _legacyItemShowMoreOptionsButton = CreateAppBarButton("DesktopContext_ShowMoreOptions.Text", "\uE8A7", ShowNativeItemContextMenu_Click);
            flyout.SecondaryCommands.Add(_legacyItemShowMoreOptionsButton);
            if (propertiesAtBottom)
            {
                flyout.SecondaryCommands.Add(new AppBarSeparator());
                flyout.SecondaryCommands.Add(_legacyItemPropertiesButton);
            }
            flyout.Opened += (_, _) =>
            {
                UpdateLegacyItemCommandVisibility(_contextMenuPreferences.Read());
                NormalizeCommandBarSeparators(flyout);
            };
            return flyout;
        }

        private async Task RefreshLegacyItemApplicationExtensionsAsync(
            CommandBarFlyout ownerFlyout,
            MenuFlyout? extensionFlyout)
        {
            if (extensionFlyout is null)
                return;

            var selected = GetSelectedItems();
            var generation = ++_itemExtensionRefreshGeneration;
            foreach (var separator in _dynamicLegacyItemExtensionSeparators)
                ownerFlyout.SecondaryCommands.Remove(separator);
            _dynamicLegacyItemExtensionSeparators.Clear();
            foreach (var button in _dynamicLegacyItemExtensionButtons)
                ownerFlyout.SecondaryCommands.Remove(button);
            _dynamicLegacyItemExtensionButtons.Clear();
            extensionFlyout.Items.Clear();
            var preferences = _contextMenuPreferences.Read();
            if (!preferences.SupportsCustomContextMenus || preferences.ShowExtensions == false ||
                selected.Length != 1 || selected[0].Path is not { } targetPath)
                return;

            IReadOnlyList<ExplorerCommandPackage> packages;
            try
            {
                packages = await ExplorerCommandAdapter.DiscoverItemCommandsAsync(targetPath);
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"Could not discover item application extensions for '{targetPath}': {exception}");
                packages = [];
            }

            if (_disposed || generation != _itemExtensionRefreshGeneration)
                return;

            var directCommands = packages
                .Where(IsDirectMainMenuExtension)
                .SelectMany(package => package.Commands)
                .ToArray();
            var insertIndex = _legacyItemApplicationExtensionsButton is null
                ? ownerFlyout.SecondaryCommands.Count
                : ownerFlyout.SecondaryCommands.IndexOf(_legacyItemApplicationExtensionsButton);
            if (insertIndex < 0)
                insertIndex = ownerFlyout.SecondaryCommands.Count;
            if (directCommands.Length > 0)
            {
                var separatorBefore = new AppBarSeparator();
                ownerFlyout.SecondaryCommands.Insert(insertIndex++, separatorBefore);
                _dynamicLegacyItemExtensionSeparators.Add(separatorBefore);
            }
            foreach (var command in directCommands)
            {
                var button = CreateExplorerCommandAppBarButton(command, targetPath);
                ownerFlyout.SecondaryCommands.Insert(insertIndex++, button);
                _dynamicLegacyItemExtensionButtons.Add(button);
            }
            if (directCommands.Length > 0)
            {
                var separatorAfter = new AppBarSeparator();
                ownerFlyout.SecondaryCommands.Insert(insertIndex, separatorAfter);
                _dynamicLegacyItemExtensionSeparators.Add(separatorAfter);
            }

            foreach (var package in packages.Where(package => !IsDirectMainMenuExtension(package)))
            {
                foreach (var command in package.Commands)
                {
                    var item = CreateExplorerCommandMenuItem(command, targetPath);
                    ToolTipService.SetToolTip(item, $"{package.DisplayName} ({package.PackageFamilyName})");
                    extensionFlyout.Items.Add(item);
                }
            }

            if (extensionFlyout.Items.Count > 0)
                extensionFlyout.Items.Add(new MenuFlyoutSeparator());
            extensionFlyout.Items.Add(CreateMenuItem(
                "LegacyItemManageExtensionsMenuItem",
                "DesktopContext_ManageExtensions.Text",
                "\uE713",
                CustomizeMenu_Click,
                includeIcon: false));
            NormalizeCommandBarSeparators(ownerFlyout);
        }

        private void UpdateLegacyItemCommandVisibility(ContextMenuPreferencesSnapshot preferences)
        {
            var selected = GetSelectedItems();
            var hasSinglePath = selected.Length == 1 && selected[0].Path is not null;
            var allHavePaths = selected.Length > 0 && selected.All(item => item.Path is not null);
            SetAppBarCommandAvailability(_legacyItemOpenButton, selected.Length == 1 && selected[0].CanOpen);
            if (_legacyItemOpenButton is { } openButton)
            {
                if (selected.Length == 1 && selected[0].Path is not null && !selected[0].IsDirectory && !selected[0].IsShortcut && selected[0].FileExtension is { } openExtension)
                    SetFileTypeIconWhenReady(icon => openButton.Icon = icon, openExtension);
                else if (selected.Length == 1 && selected[0].Icon is { } openIcon)
                    openButton.Icon = new ImageIcon { Source = openIcon };
            }
            SetAppBarCommandAvailability(
                _legacyItemPasteButton,
                selected.Length == 1 && selected[0].IsDirectory && CanPasteToCurrentTarget());
            SetAppBarCommandAvailability(_legacyItemCopyButton, allHavePaths);
            SetAppBarCommandAvailability(_legacyItemCutButton, allHavePaths);
            SetAppBarCommandAvailability(_legacyItemDeleteButton, allHavePaths);
            SetAppBarCommandAvailability(_legacyItemRenameButton, selected.Length == 1 && CanRenameDesktopItem(selected[0]));
            SetAppBarCommandAvailability(_legacyItemShareButton, selected.Length == 1 && selected[0].Path is not null);
            SetAppBarCommandAvailability(_legacyItemOpenLocationButton, selected.Length == 1 && selected[0].IsShortcut);
            SetAppBarCommandAvailability(_legacyItemRunAsButton, selected.Length == 1 && IsRunnableAsAdministrator(selected[0]));
            SetAppBarCommandAvailability(_legacyItemApplicationExtensionsButton,
                selected.Length == 1 && selected[0].Path is not null &&
                preferences.SupportsCustomContextMenus && preferences.ShowExtensions != false);
            SetMenuItemVisibility(_legacyItemExtensionsSeparator,
                selected.Length == 1 && selected[0].Path is not null &&
                preferences.SupportsCustomContextMenus && preferences.ShowExtensions != false);
            SetAppBarCommandAvailability(_legacyItemPropertiesButton,
                selected.Length == 1 && CanShowProperties(selected[0]));
            SetAppBarCommandAvailability(_legacyManageThisPcButton, selected.Length == 1 && selected[0].Kind == DesktopItemKind.ThisPc);
            SetAppBarCommandAvailability(_legacyEmptyRecycleBinButton, selected.Length == 1 &&
                selected[0].Kind == DesktopItemKind.RecycleBin && RecycleBinContainsItems());
            SetMenuItemVisibility(_legacyItemCustomizeMenuButton,
                preferences.SupportsCustomContextMenus && preferences.ShowCustomizeMenu != false);
            SetMenuItemVisibility(_legacyItemShowMoreOptionsButton,
                selected.Length == 1 && preferences.SupportsCustomContextMenus && preferences.IsEnabled("ShowMore\\Enabled") == true);
            SetMenuItemVisibility(_legacySendToButton, allHavePaths && preferences.IsEnabled("sendto") != false);
            SetMenuItemVisibility(_legacyPrintButton, hasSinglePath && preferences.IsEnabled("print") != false);
            SetMenuItemVisibility(_legacyCreateShortcutButton, allHavePaths && preferences.IsEnabled("link") != false);
            SetMenuItemVisibility(_legacyCopyAsPathButton, allHavePaths && preferences.IsEnabled("copyaspath") != false);
            SetMenuItemVisibility(_legacyRotateImageButton,
                hasSinglePath && IsRotatableImage(selected[0].Path!) && preferences.IsEnabled("rotate90") != false);
            if (_legacyItemCommandBarFlyout is not null)
                NormalizeCommandBarSeparators(_legacyItemCommandBarFlyout);
        }

        private bool HasLegacyItemCommandBarContent()
        {
            var selected = GetSelectedItems();
            var hasSingle = selected.Length == 1;
            var allHavePaths = selected.Length > 0 && selected.All(item => item.Path is not null);

            // The command bar's primary commands are the content shown immediately.
            // Secondary commands do not make an otherwise empty command bar useful here.
            return allHavePaths ||
                   (hasSingle && selected[0].IsDirectory && CanPasteToCurrentTarget()) ||
                   (hasSingle && CanRenameDesktopItem(selected[0]));
        }

        private static void SetAppBarCommandAvailability(AppBarButton? button, bool available)
        {
            if (button is null)
                return;

            button.IsEnabled = available;
            button.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        }

        private AppBarButton CreateAppBarButton(string resourceKey, string glyph, RoutedEventHandler clickHandler)
        {
            var button = new AppBarButton
            {
                Label = GetResourceText(resourceKey),
                Icon = ContextMenuIconProvider.CreateForCommand(resourceKey, ElementTheme.Default)
            };
            button.Click += clickHandler;
            return button;
        }

        private MenuFlyoutItem CreateMenuItem(
            string name,
            string resourceKey,
            string iconGlyph,
            RoutedEventHandler clickHandler,
            bool includeIcon = true)
        {
            var item = new MenuFlyoutItem
            {
                Name = name,
                Text = GetResourceText(resourceKey),
                Icon = includeIcon ? ContextMenuIconProvider.CreateForCommand(resourceKey, ElementTheme.Default) : null
            };
            item.Click += clickHandler;
            return item;
        }

        private string GetResourceText(string resourceKey)
        {
            if (_resourceTextCache.TryGetValue(resourceKey, out var cachedText))
                return cachedText;

            var path = resourceKey.EndsWith(".Text", StringComparison.Ordinal)
                ? resourceKey[..^5] + "/Text"
                : resourceKey;
            var text = _resources.GetString(path);
            _resourceTextCache[resourceKey] = text;
            return text;
        }

        private void SetMenuItemText(object? sender, string name, string resourceKey)
        {
            if (sender is MenuFlyout flyout && flyout.Items.OfType<MenuFlyoutItem>().FirstOrDefault(item => item.Name == name) is { } item)
                item.Text = GetResourceText(resourceKey);
        }

        private static void SetMenuItemEnabled(object? sender, string name, bool enabled)
        {
            if (sender is MenuFlyout flyout && flyout.Items.OfType<MenuFlyoutItem>().FirstOrDefault(item => item.Name == name) is { } item)
                item.IsEnabled = enabled;
        }

        private static void SetMenuItemVisibility(FrameworkElement? item, bool visible)
        {
            if (item is not null)
                item.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        private static void SetMenuItemVisibility(object? sender, string name, bool visible)
        {
            if (sender is MenuFlyout flyout &&
                flyout.Items.OfType<MenuFlyoutItem>().FirstOrDefault(item => item.Name == name) is { } item)
            {
                item.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private async void OpenItem_Click(object sender, RoutedEventArgs e)
        {
            var selected = GetSelectedItems();
            if (selected.Length == 1)
                await OpenItemAsync(selected[0]);
        }

        private async Task OpenItemAsync(DesktopItem item)
        {
            try
            {
                if (!await _itemsService.OpenAsync(item))
                    await ShowOperationErrorAsync();
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"Could not open desktop item: {exception}");
                await ShowOperationErrorAsync();
            }
        }

        private async void CopyItem_Click(object sender, RoutedEventArgs e) => await CopyOrCutSelectedAsync(cut: false);

        private async void CutItem_Click(object sender, RoutedEventArgs e) => await CopyOrCutSelectedAsync(cut: true);

        private async Task CopyOrCutSelectedAsync(bool cut)
        {
            var paths = GetSelectedItems().Where(item => item.Path is not null).Select(item => item.Path!).ToArray();
            if (paths.Length == 0)
                return;

            try
            {
                var result = await DesktopFileOperationsService.CopyAsync(paths, cut, _windowHandle);
                if (result.ItemCount > 0 && result.Action == DesktopClipboardAction.Move)
                {
                    _cutDesktopItemPaths.Clear();
                    foreach (var path in paths)
                    {
                        try
                        {
                            _cutDesktopItemPaths.Add(Path.GetFullPath(path));
                        }
                        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
                        {
                            Debug.WriteLine($"Could not normalize cut path '{path}': {exception.Message}");
                        }
                    }
                }
                else
                {
                    _cutDesktopItemPaths.Clear();
                }

                foreach (var item in Items)
                    item.IsCut = item.Item.Path is { } path && _cutDesktopItemPaths.Contains(path);
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"Could not update clipboard: {exception}");
                await ShowOperationErrorAsync();
            }
        }

        private async void PrintItem_Click(object sender, RoutedEventArgs e)
        {
            var item = GetSelectedItems().SingleOrDefault();
            if (item?.Path is not { } path)
                return;

            try
            {
                ShellActivationService.Print(path, _windowHandle);
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"Could not print '{path}': {exception}");
                await ShowOperationErrorAsync();
            }
        }

        private async void ShareItem_Click(object sender, RoutedEventArgs e)
        {
            var item = GetSelectedItems().SingleOrDefault();
            if (item?.Path is not { } path)
                return;

            try
            {
                ShellActivationService.Share(path, _windowHandle);
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"Could not share '{path}': {exception}");
                await ShowOperationErrorAsync();
            }
        }

        private async void OpenFileLocation_Click(object sender, RoutedEventArgs e)
        {
            var item = GetSelectedItems().SingleOrDefault();
            if (item?.Path is not { } path || !item.IsShortcut)
                return;

            try
            {
                ShellActivationService.OpenContainingFolder(path, _windowHandle);
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"Could not open the containing folder for '{path}': {exception}");
                await ShowOperationErrorAsync();
            }
        }

        private async void RunAsAdministrator_Click(object sender, RoutedEventArgs e)
        {
            var item = GetSelectedItems().SingleOrDefault();
            if (item?.Path is not { } path || !IsRunnableAsAdministrator(item))
                return;

            try
            {
                ShellActivationService.RunAsAdministrator(path, _windowHandle);
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"Could not run '{path}' as administrator: {exception}");
                await ShowOperationErrorAsync();
            }
        }

        private static bool IsRunnableAsAdministrator(DesktopItem item) =>
            item.Path is not null &&
            (item.IsShortcut || Path.GetExtension(item.Path).ToLowerInvariant() is ".exe" or ".msi" or ".bat" or ".cmd");

        private static bool CanShowProperties(DesktopItem item) =>
            item.Path is not null ||
            (item.IsSpecial && item.Kind is not (DesktopItemKind.ControlPanel or DesktopItemKind.UserFiles));

        private async void CreateShortcutItem_Click(object sender, RoutedEventArgs e)
        {
            var paths = GetSelectedItems().Where(item => item.Path is not null).Select(item => item.Path!).ToArray();
            foreach (var path in paths)
            {
                var targetName = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
                if (string.IsNullOrWhiteSpace(targetName))
                    targetName = Path.GetPathRoot(path)?.TrimEnd(Path.DirectorySeparatorChar) ?? "Shortcut";
                var shortcutNameTemplate = _resources.GetString("DesktopItemContext_ShortcutName");
                var shortcutName = GetAvailableNewItemName(string.Format(shortcutNameTemplate, targetName), isDirectory: false);
                var shortcutPath = Path.Combine(GetDesktopPath(), shortcutName);
                try
                {
                    await DesktopShortcutService.CreateAsync(shortcutPath, path);
                }
                catch (Exception exception)
                {
                    System.Diagnostics.Debug.WriteLine($"Could not create a shortcut to '{path}': {exception}");
                    await ShowOperationErrorAsync();
                    return;
                }
            }

            await RefreshDesktopItemsAsync();
        }

        private void CopyAsPathItem_Click(object sender, RoutedEventArgs e)
        {
            var paths = GetSelectedItems()
                .Where(item => item.Path is not null)
                .Select(item => $"\"{item.Path}\"")
                .ToArray();
            if (paths.Length == 0)
                return;

            var data = new DataPackage();
            data.SetText(string.Join(Environment.NewLine, paths));
            Clipboard.SetContent(data);
        }

        private async void RotateImageLeft_Click(object sender, RoutedEventArgs e) => await RotateSelectedImageAsync(clockwise: false);

        private async void RotateImageRight_Click(object sender, RoutedEventArgs e) => await RotateSelectedImageAsync(clockwise: true);

        private async Task RotateSelectedImageAsync(bool clockwise)
        {
            var item = GetSelectedItems().SingleOrDefault();
            if (item?.Path is not { } path || !IsRotatableImage(path))
                return;

            try
            {
                ShellActivationService.RotateImage(path, clockwise, _windowHandle);
                await RefreshDesktopItemsAsync();
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"Could not rotate image '{path}': {exception}");
                await ShowOperationErrorAsync();
            }
        }

        private static bool IsRotatableImage(string path) =>
            Path.GetExtension(path).ToLowerInvariant() is ".bmp" or ".gif" or ".jpg" or ".jpeg" or ".png" or ".tif" or ".tiff";

        private async void Paste_Click(object sender, RoutedEventArgs e)
        {
            var target = GetPasteTarget();
            if (string.IsNullOrWhiteSpace(target))
                return;

            try
            {
                var result = await DesktopFileOperationsService.PasteAsync(target, _windowHandle);
                if (result.Kind == DesktopFileOperationKind.Move && result.Status == DesktopFileOperationStatus.Completed)
                    await RefreshCutDesktopItemsAsync();
                if (result.Status != DesktopFileOperationStatus.NoClipboardItems)
                    await RefreshDesktopItemsAsync();
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"Could not paste desktop items: {exception}");
                await ShowOperationErrorAsync();
            }
        }

        private async void PropertiesItem_Click(object sender, RoutedEventArgs e)
        {
            var selected = GetSelectedItems();
            if (selected.Length != 1)
                return;

            try
            {
                if (selected[0].Kind == DesktopItemKind.ThisPc)
                {
                    ShellActivationService.Open("ms-settings:about", _windowHandle);
                    return;
                }
                if (selected[0].Kind == DesktopItemKind.RecycleBin)
                {
                    ShellActivationService.ShowShellItemProperties(
                        DesktopItem.GetShellParsingName(DesktopItemKind.RecycleBin),
                        _windowHandle);
                    return;
                }
                if (selected[0].IsSpecial)
                {
                    ShellActivationService.ShowShellItemProperties(
                        DesktopItem.GetShellParsingName(selected[0].Kind),
                        _windowHandle);
                    return;
                }
                if (selected[0].Path is not { } path)
                    return;
                ShellActivationService.ShowProperties(path, _windowHandle);
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"Could not show desktop item properties: {exception}");
                await ShowOperationErrorAsync();
            }
        }

        private async void RenameItem_Click(object sender, RoutedEventArgs e)
        {
            var selected = GetSelectedItems();
            if (selected.Length != 1 || !CanRenameDesktopItem(selected[0]))
                return;

            var item = selected[0];
            var path = item.Path;
            var initialName = path is not null
                ? item.IsShortcut ? Path.GetFileNameWithoutExtension(path) : Path.GetFileName(path)
                : item.Name;
            if (string.IsNullOrWhiteSpace(initialName) && item.DisplayNameResourceKey is { } resourceKey)
                initialName = _resources.GetString(resourceKey);

            var dialog = new DesktopActionDialog(
                _resources.GetString("DesktopRename_Title"),
                _resources.GetString("DesktopRename_Message"),
                _resources.GetString("DesktopAction_Confirm"),
                _resources.GetString("DesktopAction_Cancel"),
                inputText: initialName,
                inputPlaceholder: _resources.GetString("DesktopRename_Placeholder"),
                showInput: true)
            {
                XamlRoot = _desktopRoot.XamlRoot
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(dialog.InputValue))
                return;

            try
            {
                var newName = dialog.InputValue.Trim();
                if (path is not null)
                {
                    if (item.IsShortcut)
                    {
                        var extension = Path.GetExtension(path);
                        if (!newName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                            newName += extension;
                    }
                    await DesktopFileOperationsService.RenameAsync(path, newName, _windowHandle);
                }
                else
                {
                    await DesktopFileOperationsService.RenameShellItemAsync(
                        DesktopItem.GetShellParsingName(item.Kind),
                        newName,
                        _windowHandle);
                }
                await RefreshDesktopItemsAsync();
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"Could not rename desktop item: {exception}");
                await ShowOperationErrorAsync();
            }
        }

        private static bool CanRenameDesktopItem(DesktopItem item) =>
            item.Path is not null || item.Kind is DesktopItemKind.ThisPc or DesktopItemKind.RecycleBin;

        private async void ManageThisPc_Click(object sender, RoutedEventArgs e)
        {
            if (GetSelectedItems().SingleOrDefault()?.Kind != DesktopItemKind.ThisPc)
                return;

            try
            {
                ShellActivationService.OpenComputerManagement(_windowHandle);
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"Could not open Computer Management: {exception}");
                await ShowOperationErrorAsync();
            }
        }

        private async void DeleteItem_Click(object sender, RoutedEventArgs e)
        {
            var paths = GetSelectedItems().Where(item => item.Path is not null).Select(item => item.Path!).ToArray();
            if (paths.Length == 0)
                return;

            var dialog = new DesktopActionDialog(
                _resources.GetString("DesktopDelete_Title"),
                string.Format(_resources.GetString("DesktopDelete_Message"), paths.Length),
                _resources.GetString("DesktopAction_Delete"),
                _resources.GetString("DesktopAction_Cancel"))
            {
                XamlRoot = _desktopRoot.XamlRoot
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return;

            try
            {
                await DesktopFileOperationsService.RecycleAsync(paths, _windowHandle);
                UpdateUndoDeleteVisibility();
                await RefreshDesktopItemsAsync();
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"Could not recycle desktop items: {exception}");
                await ShowOperationErrorAsync();
            }
        }

        private async Task ShowOperationErrorAsync()
        {
            SendPenHaptic(PenHapticAction.Error);
            var dialog = new DesktopActionDialog(
                _resources.GetString("DesktopAction_ErrorTitle"),
                _resources.GetString("DesktopAction_ErrorMessage"),
                _resources.GetString("DesktopAction_Close"),
                string.Empty)
            {
                XamlRoot = _desktopRoot.XamlRoot
            };
            await dialog.ShowAsync();
        }

        private bool CanPasteToCurrentTarget()
        {
            try
            {
                var clipboard = DesktopFileOperationsService.GetClipboardState();
                var target = GetPasteTarget();
                return clipboard.HasStorageItems && !string.IsNullOrWhiteSpace(target) && Directory.Exists(target);
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"Could not inspect clipboard for paste: {exception}");
                return false;
            }
        }

        private bool CanPasteToDesktop()
        {
            try
            {
                var clipboard = DesktopFileOperationsService.GetClipboardState();
                return clipboard.HasStorageItems && Directory.Exists(GetDesktopPath());
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"Could not inspect clipboard for paste: {exception}");
                return false;
            }
        }

        private string? GetPasteTarget()
        {
            var selected = GetSelectedItems();
            if (selected.Length == 1 && selected[0].IsDirectory && selected[0].Path is { } selectedPath)
                return selectedPath;
            return GetDesktopPath();
        }

        private static string GetDesktopPath() => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

        private DesktopItem[] GetSelectedItems() =>
            Items.Where(_selectedDesktopItems.Contains).Select(item => item.Item).ToArray();

        private static DesktopItemViewModel? FindItemViewModel(DependencyObject? element)
        {
            while (element is not null)
            {
                if (element is FrameworkElement frameworkElement && frameworkElement.DataContext is DesktopItemViewModel item)
                    return item;
                element = VisualTreeHelper.GetParent(element);
            }
            return null;
        }

        private async void OpenSettings_Click(object sender, RoutedEventArgs e) => await ShowSettingsAsync();

        public Task ShowSettingsAsync()
        {
            XiyiDesktop.App.ShowSettingsWindow();
            return Task.CompletedTask;
        }

        private async void RefreshDesktop_Click(object sender, RoutedEventArgs e)
        {
            _hostController?.RefreshLayout();
            await RefreshDesktopItemsAsync();
        }

        private void Exit_Click(object sender, RoutedEventArgs e) => XiyiDesktop.App.RequestExit();

        private void Items_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (_disposed || _isApplyingItemOrder || e.Action != NotifyCollectionChangedAction.Move ||
                XiyiDesktop.AppSettingsStore.DesktopAutoArrange)
            {
                return;
            }

            SaveManualDesktopOrder();
            UpdateViewMenuItems();
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _initialDesktopItemsLayoutReady.TrySetResult(true);
            foreach (var instanceId in _webWidgetOverlays.Keys.ToArray())
                CloseWebWidgetOverlay(instanceId);
            foreach (var surface in _widgetSurfaces.Values)
                surface.Card.DetachWithoutDelete();
            _widgetSurfaces.Clear();
            CloseContextMenuHost();
            _workAreaTransitionStoryboard?.Stop();
            _workAreaTransitionStoryboard = null;
            _taskbarBoundsChangeTimer?.Stop();
            _taskbarBoundsChangeTimer = null;
            _iconDragPreviewTimer?.Stop();
            if (_iconDragPreviewTimer is not null)
                _iconDragPreviewTimer.Tick -= IconDragPreviewTimer_Tick;
            _iconDragPreviewTimer = null;
            _desktopRoot.ActualThemeChanged -= DesktopRoot_ActualThemeChanged;
            Clipboard.ContentChanged -= Clipboard_ContentChanged;
            try
            {
                if (_desktopXamlSource is not null)
                {
                    _desktopXamlSource.Content = null;
                    _desktopXamlSource.Dispose();
                    _desktopXamlSource = null;
                }
            }
            catch (Exception exception) when (exception is COMException or InvalidOperationException)
            {
                Debug.WriteLine($"Could not close the desktop XAML Island: {exception.Message}");
            }
            _itemsService.ItemsChanged -= ItemsService_ItemsChanged;
            if (_taskbarBoundsChangeHook != IntPtr.Zero)
            {
                UnhookWinEvent(_taskbarBoundsChangeHook);
                _taskbarBoundsChangeHook = IntPtr.Zero;
            }
            if (_displayChangeHook != IntPtr.Zero)
            {
                UnhookWinEvent(_displayChangeHook);
                _displayChangeHook = IntPtr.Zero;
            }
            _itemsService.Dispose();
            _refreshCancellation?.Cancel();
            _refreshCancellation?.Dispose();
            _iconSizeRefreshTimer?.Stop();
            _iconSizeRefreshTimer = null;
            _widgetOverlayLayoutTimer?.Stop();
            _widgetOverlayLayoutTimer = null;
            _iconRefreshCancellation?.Cancel();
            _iconRefreshCancellation?.Dispose();
            if (_subscribedXamlRoot is not null)
            {
                _subscribedXamlRoot.Changed -= DesktopXamlRoot_Changed;
                _subscribedXamlRoot = null;
            }

            if (_hostController is not null)
            {
                _hostController.StateChanged -= HostController_StateChanged;
                _hostController.WindowHandleLost -= HostController_WindowHandleLost;
                _hostController.Dispose();
                _hostController = null;
            }

            DesktopCarrierWindow.Destroy(_windowHandle);
        }
    }

    public sealed class DesktopItemViewModel : INotifyPropertyChanged
    {
        private static readonly Lazy<BitmapImage?> ShortcutArrowImage = new(CreateShortcutArrowImage);
        private readonly ResourceLoader _resources;
        private readonly DesktopItem _item;

        internal DesktopItemViewModel(DesktopItem item, ResourceLoader resources, double iconSize)
        {
            _item = item;
            _resources = resources;
            _iconSize = iconSize;
            _showShortcutArrow = AppSettingsStore.ShowShortcutArrows && _item.IsShortcut;
            _item.PropertyChanged += Item_PropertyChanged;
        }

        private double _iconSize;
        private double _cornerRadius = AppSettingsStore.DesktopCornerRadius;
        private double _positionX = double.NaN;
        private double _positionY = double.NaN;
        private bool _isSelected;
        private bool _isCut;
        private bool _isMarqueeHovered;
        private bool _showShortcutArrow;
        private string _dragOperationGlyph = string.Empty;
        private string _dragOperationText = string.Empty;

        public DesktopItem Item => _item;

        public string DisplayName => _item.IsSpecial && !string.IsNullOrWhiteSpace(_item.Name)
            ? _item.Name
            : _item.DisplayNameResourceKey is { } key
                ? _resources.GetString(key)
                : _item.Name;

        public double IconSize => _iconSize;

        public CornerRadius ItemCornerRadius => new(_cornerRadius);

        public double ScaleFactor => _iconSize / DesktopIconMetrics.Medium;

        public double TileWidth => Math.Max(_iconSize, TextWidth) + TilePadding.Left + TilePadding.Right;

        public double TileHeight => _iconSize + PanelSpacing + TextMaxHeight + TilePadding.Top + TilePadding.Bottom;

        public double GlyphSize => _iconSize * 0.75;

        public double TextWidth => Math.Max(72, (DesktopIconMetrics.Medium + 40) * ScaleFactor);

        public double TextMaxHeight => Math.Max(32, 40 * ScaleFactor);

        public double TextFontSize => Math.Clamp(12 * ScaleFactor, 10, 14);

        public double PanelSpacing => Math.Max(4, 5 * ScaleFactor);

        public Thickness TilePadding => new(Math.Max(2, 4 * ScaleFactor));

        public BitmapImage? ShortcutArrowIcon => ShortcutArrowImage.Value;

        public double ShortcutArrowSize => 22 * ScaleFactor;

        public double ShortcutArrowOffsetX => -2 * ScaleFactor;

        public double ShortcutArrowOffsetY => 2 * ScaleFactor;

        public Visibility ShortcutArrowVisibility => _showShortcutArrow && ShortcutArrowIcon is not null
            ? Visibility.Visible
            : Visibility.Collapsed;

        public double ShellStatusOverlaySize => 18 * ScaleFactor;

        public double PositionX => _positionX;

        public double PositionY => _positionY;

        public bool HasPosition => double.IsFinite(_positionX) && double.IsFinite(_positionY);

        public bool IsSelected
        {
            get => _isSelected;
            internal set
            {
                if (_isSelected == value)
                    return;

                _isSelected = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SelectionVisibility));
            }
        }

        public Visibility SelectionVisibility => IsSelected ? Visibility.Visible : Visibility.Collapsed;

        public bool IsCut
        {
            get => _isCut;
            internal set
            {
                if (_isCut == value)
                    return;

                _isCut = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CutVisibility));
            }
        }

        public Visibility CutVisibility => IsCut ? Visibility.Visible : Visibility.Collapsed;

        public string DragOperationGlyph => _dragOperationGlyph;

        public string DragOperationText => _dragOperationText;

        public Visibility DragOperationVisibility => string.IsNullOrEmpty(_dragOperationGlyph)
            ? Visibility.Collapsed
            : Visibility.Visible;

        internal void SetDragOperation(string glyph, string text)
        {
            if (string.Equals(_dragOperationGlyph, glyph, StringComparison.Ordinal) &&
                string.Equals(_dragOperationText, text, StringComparison.Ordinal))
                return;

            _dragOperationGlyph = glyph;
            _dragOperationText = text;
            OnPropertyChanged(nameof(DragOperationGlyph));
            OnPropertyChanged(nameof(DragOperationText));
            OnPropertyChanged(nameof(DragOperationVisibility));
        }

        public bool IsMarqueeHovered
        {
            get => _isMarqueeHovered;
            internal set
            {
                if (_isMarqueeHovered == value)
                    return;

                _isMarqueeHovered = value;
                OnPropertyChanged(nameof(MarqueeHoverOpacity));
            }
        }

        public double MarqueeHoverOpacity => IsMarqueeHovered ? 0.18 : 0;

        internal void SetCornerRadius(double cornerRadius)
        {
            cornerRadius = double.IsFinite(cornerRadius) ? Math.Clamp(cornerRadius, 0, 32) : 8;
            if (Math.Abs(_cornerRadius - cornerRadius) < 0.01)
                return;

            _cornerRadius = cornerRadius;
            OnPropertyChanged(nameof(ItemCornerRadius));
        }

        internal void SetShortcutArrowVisibility(bool isVisible)
        {
            var shouldShow = isVisible && _item.IsShortcut;
            if (_showShortcutArrow == shouldShow)
                return;

            _showShortcutArrow = shouldShow;
            OnPropertyChanged(nameof(ShortcutArrowVisibility));
        }

        private static BitmapImage? CreateShortcutArrowImage()
        {
            try
            {
                return new BitmapImage(new Uri("ms-appx:///Assets/ShortcutArrow.png", UriKind.Absolute));
            }
            catch (Exception exception) when (exception is ArgumentException or UriFormatException or IOException or COMException)
            {
                System.Diagnostics.Debug.WriteLine($"Unable to create the shortcut arrow image: {exception.Message}");
                return null;
            }
        }

        internal void SetPosition(double x, double y)
        {
            if (!double.IsFinite(x) || !double.IsFinite(y))
                return;

            if (!double.IsFinite(_positionX) || Math.Abs(_positionX - x) > 0.1)
            {
                _positionX = x;
                OnPropertyChanged(nameof(PositionX));
            }

            if (!double.IsFinite(_positionY) || Math.Abs(_positionY - y) > 0.1)
            {
                _positionY = y;
                OnPropertyChanged(nameof(PositionY));
            }
        }

        public Microsoft.UI.Xaml.Media.Imaging.BitmapImage? Icon => _item.Icon;

        public Microsoft.UI.Xaml.Media.Imaging.BitmapImage? ShellStatusOverlay => _item.ShellStatusOverlay;

        public HorizontalAlignment ShellStatusOverlayHorizontalAlignment => _item.IsShortcut
            ? HorizontalAlignment.Right
            : HorizontalAlignment.Left;

        public string IconGlyph => _item.Kind switch
        {
            _ when _item.Icon is not null => string.Empty,
            _ when _item.IsSpecial => string.Empty,
            _ when _item.IsDirectory => "\uE8B7",
            _ => "\uE7C3"
        };

        internal void SetIconSize(double iconSize)
        {
            if (Math.Abs(_iconSize - iconSize) < 0.1)
                return;

            _iconSize = iconSize;
            OnPropertyChanged(nameof(IconSize));
            OnPropertyChanged(nameof(ScaleFactor));
            OnPropertyChanged(nameof(TileWidth));
            OnPropertyChanged(nameof(TileHeight));
            OnPropertyChanged(nameof(GlyphSize));
            OnPropertyChanged(nameof(TextWidth));
            OnPropertyChanged(nameof(TextMaxHeight));
            OnPropertyChanged(nameof(TextFontSize));
            OnPropertyChanged(nameof(PanelSpacing));
            OnPropertyChanged(nameof(TilePadding));
            OnPropertyChanged(nameof(ShortcutArrowSize));
            OnPropertyChanged(nameof(ShortcutArrowOffsetX));
            OnPropertyChanged(nameof(ShortcutArrowOffsetY));
            OnPropertyChanged(nameof(ShellStatusOverlaySize));
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Item_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(DesktopItem.Icon))
            {
                OnPropertyChanged(nameof(Icon));
                OnPropertyChanged(nameof(IconGlyph));
            }
            else if (e.PropertyName == nameof(DesktopItem.ShellStatusOverlay))
            {
                OnPropertyChanged(nameof(ShellStatusOverlay));
            }
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
