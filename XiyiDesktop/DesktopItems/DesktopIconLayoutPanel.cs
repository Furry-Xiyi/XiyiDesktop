using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;

namespace XiyiDesktop.DesktopItems;

/// <summary>Lays out desktop icon containers in automatic, snapped, or free-position mode.</summary>
public sealed partial class DesktopIconLayoutPanel : Canvas
{
    private const double MinimumSlotWidth = 48;
    private const double MinimumSlotHeight = 52;
    private Size _viewportSize;
    private readonly HashSet<XiyiDesktop.DesktopItemViewModel> _draggedItems = [];
    private readonly HashSet<XiyiDesktop.DesktopItemViewModel> _externalDragDisplacedIcons = [];
    private readonly HashSet<FrameworkElement> _externalLayoutItems = [];
    private readonly HashSet<FrameworkElement> _draggedExternalLayoutItems = [];
    private readonly Dictionary<XiyiDesktop.DesktopItemViewModel, Point> _preDragPositions = [];
    private readonly Dictionary<FrameworkElement, Point> _preDragExternalPositions = [];
    private readonly Dictionary<XiyiDesktop.DesktopItemViewModel, Point> _externalDragIconPositions = [];
    private readonly Dictionary<FrameworkElement, Storyboard> _positionAnimations = [];
    private readonly Compositor _compositor;
    private SpringVector3NaturalMotionAnimation? _springScaleAnimation;
    private bool _animateNextArrange;
    private DispatcherQueueTimer? _dragReflowTimer;
    private int _activeAutoArrangeDropIndex;
    private int _activeDragReflowKey;
    private int _pendingDragReflowKey;
    private long _pendingDragReflowStartedAt;
    private double _pendingDragReflowDelayMilliseconds;
    private bool _hasActiveAutoArrangeDropIndex;
    private bool _hasActiveDragReflowKey;
    private bool _dragReflowReady;
    private bool _dragOverRecycleBin;
    private DataTemplate? _itemTemplate;
    private Size _lastGridCellSize = new(MinimumSlotWidth, MinimumSlotHeight);
    private bool _desktopIconsVisible = true;

    public DesktopIconLayoutPanel()
    {
        _compositor = Microsoft.UI.Xaml.Media.CompositionTarget.GetCompositorForCurrentThread();
    }

    public DataTemplate? ItemTemplate
    {
        get => _itemTemplate;
        set => _itemTemplate = value;
    }

    public void SetItems(IEnumerable<XiyiDesktop.DesktopItemViewModel> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        foreach (var element in _positionAnimations.Keys.ToArray())
        {
            var externalOffset = _externalLayoutItems.Contains(element)
                ? element.RenderTransform as TranslateTransform
                : null;
            var externalLeft = _externalLayoutItems.Contains(element) ? Canvas.GetLeft(element) : 0;
            var externalTop = _externalLayoutItems.Contains(element) ? Canvas.GetTop(element) : 0;
            StopPositionAnimation(element);
            if (_externalLayoutItems.Contains(element))
            {
                if (externalOffset is not null && double.IsFinite(externalLeft) && double.IsFinite(externalTop))
                {
                    Canvas.SetLeft(element, externalLeft + externalOffset.X);
                    Canvas.SetTop(element, externalTop + externalOffset.Y);
                }
                element.RenderTransform = null;
            }
        }
        _animateNextArrange = false;
        StopDragReflowTimer();
        _draggedItems.Clear();
        _externalDragDisplacedIcons.Clear();
        _draggedExternalLayoutItems.Clear();
        _preDragPositions.Clear();
        _preDragExternalPositions.Clear();
        _externalDragIconPositions.Clear();
        _hasActiveAutoArrangeDropIndex = false;
        _hasActiveDragReflowKey = false;
        _dragReflowReady = false;
        _dragOverRecycleBin = false;
        var externalItems = Children.OfType<FrameworkElement>()
            .Where(_externalLayoutItems.Contains)
            .ToArray();
        Children.Clear();
        foreach (var item in items)
        {
            var presenter = new ContentPresenter
            {
                Content = item,
                ContentTemplate = _itemTemplate,
                DataContext = item,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Visibility = _desktopIconsVisible ? Visibility.Visible : Visibility.Collapsed
            };
            Children.Add(presenter);
        }
        foreach (var externalItem in externalItems)
            Children.Add(externalItem);

        InvalidateMeasure();
        InvalidateArrange();
    }

    public void ReorderItems(IReadOnlyList<XiyiDesktop.DesktopItemViewModel> orderedItems)
    {
        ArgumentNullException.ThrowIfNull(orderedItems);
        var externalItems = Children.OfType<FrameworkElement>()
            .Where(_externalLayoutItems.Contains)
            .ToArray();
        var presenters = Children.OfType<ContentPresenter>()
            .Where(presenter => presenter.Content is XiyiDesktop.DesktopItemViewModel)
            .ToDictionary(
                presenter => (XiyiDesktop.DesktopItemViewModel)presenter.Content!,
                presenter => presenter);
        var reorderedPresenters = orderedItems
            .Where(presenters.ContainsKey)
            .Select(item => presenters[item])
            .ToArray();

        Children.Clear();
        foreach (var presenter in reorderedPresenters)
            Children.Add(presenter);
        foreach (var externalItem in externalItems)
            Children.Add(externalItem);

        InvalidateMeasure();
        InvalidateArrange();
    }

    public void SetViewportSize(Size size)
    {
        if (Math.Abs(_viewportSize.Width - size.Width) < 0.1 &&
            Math.Abs(_viewportSize.Height - size.Height) < 0.1)
            return;

        _viewportSize = size;
        InvalidateArrange();
    }

    public void SetDesktopIconsVisible(bool visible)
    {
        if (_desktopIconsVisible == visible)
            return;

        _desktopIconsVisible = visible;
        foreach (var presenter in Children.OfType<ContentPresenter>())
            presenter.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        InvalidateMeasure();
        InvalidateArrange();
    }

    /// <summary>Returns the cell dimensions currently used to align desktop icons.</summary>
    public Size GetGridCellSize()
    {
        var slotWidth = MinimumSlotWidth;
        var slotHeight = MinimumSlotHeight;
        var hasVisibleIcons = false;
        for (var index = 0; index < Children.Count; index++)
        {
            var child = Children[index];
            var item = GetItemViewModel(child, index);
            if (child.Visibility == Visibility.Collapsed)
            {
                if (item is not null)
                {
                    slotWidth = Math.Max(slotWidth, item.TileWidth);
                    slotHeight = Math.Max(slotHeight, item.TileHeight);
                }
                continue;
            }
            if (child is FrameworkElement frameworkElement && _externalLayoutItems.Contains(frameworkElement))
                continue;

            hasVisibleIcons = true;
            slotWidth = Math.Max(slotWidth, NormalizeDimension(child.DesiredSize.Width, item?.TileWidth ?? MinimumSlotWidth));
            slotHeight = Math.Max(slotHeight, NormalizeDimension(child.DesiredSize.Height, item?.TileHeight ?? MinimumSlotHeight));
        }

        if (slotWidth > MinimumSlotWidth || slotHeight > MinimumSlotHeight || hasVisibleIcons)
            _lastGridCellSize = new Size(slotWidth, slotHeight);
        return _lastGridCellSize;
    }

    /// <summary>Returns the padding inside each icon cell around its visible content.</summary>
    public Thickness GetGridContentInsets()
    {
        var insets = new Thickness();
        for (var index = 0; index < Children.Count; index++)
        {
            if (GetItemViewModel(Children[index], index) is not { } item)
                continue;

            var padding = item.TilePadding;
            insets.Left = Math.Max(insets.Left, padding.Left);
            insets.Top = Math.Max(insets.Top, padding.Top);
            insets.Right = Math.Max(insets.Right, padding.Right);
            insets.Bottom = Math.Max(insets.Bottom, padding.Bottom);
        }

        return insets;
    }

    public void RegisterExternalLayoutItem(FrameworkElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        _externalLayoutItems.Add(element);
        InvalidateMeasure();
        InvalidateArrange();
    }

    public void UnregisterExternalLayoutItem(FrameworkElement element)
    {
        if (!_externalLayoutItems.Remove(element))
            return;

        _draggedExternalLayoutItems.Remove(element);
        if (_draggedExternalLayoutItems.Count == 0)
            _externalDragDisplacedIcons.Clear();
        StopPositionAnimation(element);
        element.RenderTransform = null;
        InvalidateMeasure();
        InvalidateArrange();
    }

    public void AnimateNextLayout()
    {
        _animateNextArrange = true;
        InvalidateArrange();
    }

    public Point FindAvailableGridPosition(Point requestedPosition, Size itemSize, IReadOnlyList<Rect> occupiedItems)
    {
        ArgumentNullException.ThrowIfNull(occupiedItems);
        var cell = GetGridCellSize();
        var layoutSize = new Size(
            ResolveLayoutDimension(ActualWidth, _viewportSize.Width, cell.Width),
            ResolveLayoutDimension(ActualHeight, _viewportSize.Height, cell.Height));
        var occupied = occupiedItems
            .Select(bounds => new DesktopLayoutEngine.Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height))
            .ToArray();
        if (!DesktopLayoutEngine.TryFindAvailablePosition(
                new DesktopLayoutEngine.Point(requestedPosition.X, requestedPosition.Y),
                new DesktopLayoutEngine.Size(layoutSize.Width, layoutSize.Height),
                new DesktopLayoutEngine.Size(cell.Width, cell.Height),
                new DesktopLayoutEngine.Size(itemSize.Width, itemSize.Height),
                occupied,
                out var available))
            return new Point(-1, -1);

        return new Point(available.X, available.Y);
    }

    public void SetExternalChildPosition(FrameworkElement element, double left, double top, bool animate)
    {
        ArgumentNullException.ThrowIfNull(element);
        SetChildPosition(element, left, top, animate);
    }

    public void BeginExternalItemDrag(FrameworkElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (!_externalLayoutItems.Contains(element))
            throw new InvalidOperationException("The element is not registered with the desktop layout.");

        if (_draggedExternalLayoutItems.Count == 0)
        {
            _externalDragIconPositions.Clear();
            _externalDragDisplacedIcons.Clear();
            foreach (var presenter in Children.OfType<ContentPresenter>())
            {
                if (presenter.Content is XiyiDesktop.DesktopItemViewModel item)
                    _externalDragIconPositions[item] = new Point(item.PositionX, item.PositionY);
            }
        }

        var offset = element.RenderTransform as TranslateTransform;
        var left = Canvas.GetLeft(element);
        var top = Canvas.GetTop(element);
        StopPositionAnimation(element);
        if (offset is not null && double.IsFinite(left) && double.IsFinite(top))
        {
            Canvas.SetLeft(element, left + offset.X);
            Canvas.SetTop(element, top + offset.Y);
        }
        element.RenderTransform = null;
        _draggedExternalLayoutItems.Add(element);
        AnimateScale(element, 1.04f);
        Canvas.SetZIndex(element, Children.Count + 1);
        InvalidateArrange();
    }

    public void EndExternalItemDrag(FrameworkElement element, bool resetIconPositions = false)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (!_externalLayoutItems.Contains(element))
            return;

        _draggedExternalLayoutItems.Remove(element);
        if (resetIconPositions)
        {
            foreach (var (item, position) in _externalDragIconPositions)
                item.SetPosition(position.X, position.Y);
        }
        if (_draggedExternalLayoutItems.Count == 0)
            _externalDragIconPositions.Clear();
        Canvas.SetZIndex(element, 0);
        AnimateScale(element, 1.0f);
        InvalidateArrange();
    }

    private static (double X, double Y, double Width, double Height) GetVisibleExternalBounds(FrameworkElement element) =>
        GetVisibleExternalBounds(element, Canvas.GetLeft(element), Canvas.GetTop(element));

    private static (double X, double Y, double Width, double Height) GetVisibleExternalBounds(
        FrameworkElement element,
        double x,
        double y)
    {
        var insets = element is Grid grid
            ? grid.Children.OfType<Border>().FirstOrDefault()?.Padding ?? new Thickness()
            : new Thickness();
        x = double.IsFinite(x) ? x : 0;
        y = double.IsFinite(y) ? y : 0;
        var width = NormalizeDimension(element.Width, element.DesiredSize.Width);
        var height = NormalizeDimension(element.Height, element.DesiredSize.Height);
        return (
            x + insets.Left,
            y + insets.Top,
            Math.Max(1, width - insets.Left - insets.Right),
            Math.Max(1, height - insets.Top - insets.Bottom));
    }

    private List<(double X, double Y, double Width, double Height)> GetVisibleExternalOccupiedItems() =>
        _externalLayoutItems
            .Where(Children.Contains)
            .Select(GetVisibleExternalBounds)
            .ToList();

    private static (double X, double Y, double Width, double Height) GetVisibleItemBounds(
        XiyiDesktop.DesktopItemViewModel item,
        double x,
        double y)
    {
        var padding = item.TilePadding;
        return (
            x + padding.Left,
            y + padding.Top,
            Math.Max(1, item.TileWidth - padding.Left - padding.Right),
            Math.Max(1, item.TileHeight - padding.Top - padding.Bottom));
    }

    public void BeginItemDrag(IEnumerable<XiyiDesktop.DesktopItemViewModel> items)
    {
        if (_draggedItems.Count == 0)
        {
            _preDragPositions.Clear();
            _preDragExternalPositions.Clear();
            foreach (var presenter in Children.OfType<ContentPresenter>())
            {
                if (presenter.Content is XiyiDesktop.DesktopItemViewModel existingItem)
                    _preDragPositions[existingItem] = new Point(existingItem.PositionX, existingItem.PositionY);
            }
            foreach (var widget in _externalLayoutItems.Where(Children.Contains))
                _preDragExternalPositions[widget] = new Point(Canvas.GetLeft(widget), Canvas.GetTop(widget));
            _dragReflowReady = true;
        }

        foreach (var item in items)
        {
            if (!_draggedItems.Add(item))
                continue;

            if (FindPresenter(item) is { } presenter)
            {
                StopPositionAnimation(presenter);
                AnimateScale(presenter, 1.06f);
                Canvas.SetZIndex(presenter, 1);
            }
        }
        if (_draggedItems.Count > 0 && !_hasActiveDragReflowKey)
        {
            var autoArrange = XiyiDesktop.AppSettingsStore.DesktopAutoArrange;
            _activeDragReflowKey = CalculateDragReflowKey(autoArrange);
            _hasActiveDragReflowKey = true;
            _activeAutoArrangeDropIndex = autoArrange ? CalculateAutoArrangeDropIndex() : 0;
            _hasActiveAutoArrangeDropIndex = autoArrange;
        }
        InvalidateArrange();
    }

    public event EventHandler? DragReflowed;

    public void EndItemDrag(IEnumerable<XiyiDesktop.DesktopItemViewModel> items)
    {
        foreach (var item in items)
        {
            if (FindPresenter(item) is { } presenter)
            {
                Canvas.SetZIndex(presenter, 0);
                AnimateScale(presenter, 1.0f);
            }
        }
        _animateNextArrange = true;
        _draggedItems.Clear();
        _preDragPositions.Clear();
        _preDragExternalPositions.Clear();
        _hasActiveAutoArrangeDropIndex = false;
        _hasActiveDragReflowKey = false;
        _dragReflowReady = false;
        _dragOverRecycleBin = false;
        StopDragReflowTimer();
        InvalidateArrange();
    }

    public void PlayDropAnimation(IEnumerable<XiyiDesktop.DesktopItemViewModel> items)
    {
        foreach (var item in items)
        {
            if (FindPresenter(item) is not { } presenter)
                continue;

            ElementCompositionPreview.GetElementVisual(presenter).Scale = new Vector3(1.06f);
            AnimateScale(presenter, 1.0f);
        }
    }

    public void UpdateDragReflow(bool overRecycleBin, double delayMilliseconds)
    {
        _dragOverRecycleBin = overRecycleBin;
        if (_draggedItems.Count == 0)
            return;

        if (overRecycleBin)
        {
            _dragReflowReady = false;
            StopDragReflowTimer();
            return;
        }

        var autoArrange = XiyiDesktop.AppSettingsStore.DesktopAutoArrange;
        var candidate = CalculateDragReflowKey(autoArrange);
        if (!_hasActiveDragReflowKey)
        {
            SetActiveDragReflowKey(candidate, autoArrange);
            _dragReflowReady = true;
            InvalidateArrange();
            return;
        }

        if (candidate == _activeDragReflowKey)
        {
            _dragReflowReady = true;
            StopDragReflowTimer();
            return;
        }

        _dragReflowReady = false;
        if (_dragReflowTimer is null || _pendingDragReflowKey != candidate)
        {
            StopDragReflowTimer();
            _pendingDragReflowKey = candidate;
            _pendingDragReflowStartedAt = Stopwatch.GetTimestamp();
            _pendingDragReflowDelayMilliseconds = Math.Clamp(delayMilliseconds, 60, 220);
            _dragReflowTimer = DispatcherQueue.GetForCurrentThread()?.CreateTimer();
            if (_dragReflowTimer is null)
            {
                SetActiveDragReflowKey(candidate, autoArrange);
                _dragReflowReady = true;
                InvalidateArrange();
                DragReflowed?.Invoke(this, EventArgs.Empty);
                return;
            }

            _dragReflowTimer.IsRepeating = false;
            _dragReflowTimer.Tick += DragReflowTimer_Tick;
        }

        _pendingDragReflowDelayMilliseconds = Math.Clamp(delayMilliseconds, 60, 220);
        var elapsed = Stopwatch.GetElapsedTime(_pendingDragReflowStartedAt).TotalMilliseconds;
        if (elapsed >= _pendingDragReflowDelayMilliseconds)
        {
            CompletePendingDragReflow(autoArrange);
            return;
        }

        _dragReflowTimer!.Stop();
        _dragReflowTimer.Interval = TimeSpan.FromMilliseconds(
            Math.Max(1, _pendingDragReflowDelayMilliseconds - elapsed));
        _dragReflowTimer.Start();
    }

    private void DragReflowTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        sender.Tick -= DragReflowTimer_Tick;
        _dragReflowTimer = null;
        if (_draggedItems.Count == 0 || _dragOverRecycleBin)
            return;

        var autoArrange = XiyiDesktop.AppSettingsStore.DesktopAutoArrange;
        if (CalculateDragReflowKey(autoArrange) == _pendingDragReflowKey)
        {
            CompletePendingDragReflow(autoArrange);
        }
    }

    private void CompletePendingDragReflow(bool autoArrange)
    {
        var candidate = CalculateDragReflowKey(autoArrange);
        if (candidate != _pendingDragReflowKey || _dragOverRecycleBin)
            return;

        var didReflow = !_hasActiveDragReflowKey || candidate != _activeDragReflowKey;
        SetActiveDragReflowKey(candidate, autoArrange);
        _dragReflowReady = true;
        StopDragReflowTimer();
        InvalidateArrange();
        if (didReflow)
            DragReflowed?.Invoke(this, EventArgs.Empty);
    }

    private void SetActiveDragReflowKey(int key, bool autoArrange)
    {
        _activeDragReflowKey = key;
        _hasActiveDragReflowKey = true;
        _activeAutoArrangeDropIndex = autoArrange ? CalculateAutoArrangeDropIndex() : 0;
        _hasActiveAutoArrangeDropIndex = autoArrange;
    }

    private int CalculateDragReflowKey(bool autoArrange)
    {
        // HashCode seeds each instance randomly. Recreating it for every pointer move
        // made an unchanged overlap set look different on every sample, so the dwell
        // timer never reached a stable candidate and icons never yielded their slots.
        const uint offsetBasis = 2166136261;
        const uint prime = 16777619;
        var hash = offsetBasis;
        if (autoArrange)
            hash = unchecked((hash ^ (uint)CalculateAutoArrangeDropIndex()) * prime);
        var displaced = GetDragPreviewDisplacedItems();
        var orderedItems = Children.OfType<ContentPresenter>()
            .Select(presenter => presenter.Content as XiyiDesktop.DesktopItemViewModel)
            .Where(item => item is not null)
            .Cast<XiyiDesktop.DesktopItemViewModel>()
            .ToArray();
        foreach (var dragged in orderedItems.Where(displaced.ContainsKey))
        {
            foreach (var character in dragged.Item.Identity.ToUpperInvariant())
                hash = unchecked((hash ^ character) * prime);
            foreach (var character in displaced[dragged].Item.Identity.ToUpperInvariant())
                hash = unchecked((hash ^ character) * prime);
            hash = unchecked((hash ^ 0xFFFFu) * prime);
        }

        var gridCell = GetGridCellSize();
        var widgetIndex = 0;
        foreach (var widget in _externalLayoutItems.OrderBy(Children.IndexOf))
        {
            if (!Children.Contains(widget))
                continue;

            var widgetBounds = _preDragExternalPositions.TryGetValue(widget, out var widgetPosition)
                ? GetVisibleExternalBounds(widget, widgetPosition.X, widgetPosition.Y)
                : GetVisibleExternalBounds(widget);
            foreach (var item in _draggedItems)
            {
                var draggedBounds = GetVisibleItemBounds(item, item.PositionX, item.PositionY);
                if (!(draggedBounds.X < widgetBounds.X + widgetBounds.Width &&
                    draggedBounds.X + draggedBounds.Width > widgetBounds.X &&
                    draggedBounds.Y < widgetBounds.Y + widgetBounds.Height &&
                    draggedBounds.Y + draggedBounds.Height > widgetBounds.Y))
                    continue;

                var centerX = draggedBounds.X + draggedBounds.Width / 2;
                var centerY = draggedBounds.Y + draggedBounds.Height / 2;
                var column = Math.Max(0, (int)Math.Floor(centerX / Math.Max(MinimumSlotWidth, gridCell.Width)));
                var row = Math.Max(0, (int)Math.Floor(centerY / Math.Max(MinimumSlotHeight, gridCell.Height)));
                foreach (var character in $"WIDGET:{widgetIndex}:{item.Item.Identity.ToUpperInvariant()}:{column}:{row}")
                    hash = unchecked((hash ^ character) * prime);
                hash = unchecked((hash ^ 0xFFFEu) * prime);
            }
            widgetIndex++;
        }

        return unchecked((int)hash);
    }

    private Dictionary<XiyiDesktop.DesktopItemViewModel, XiyiDesktop.DesktopItemViewModel> GetDragPreviewDisplacedItems()
    {
        var draggedItems = Children.OfType<ContentPresenter>()
            .Select(presenter => presenter.Content as XiyiDesktop.DesktopItemViewModel)
            .Where(item => item is not null && _draggedItems.Contains(item))
            .Cast<XiyiDesktop.DesktopItemViewModel>()
            .ToArray();
        var stationaryItems = Children.OfType<ContentPresenter>()
            .Select(presenter => presenter.Content as XiyiDesktop.DesktopItemViewModel)
            .Where(item => item is not null && !_draggedItems.Contains(item))
            .Cast<XiyiDesktop.DesktopItemViewModel>()
            .ToArray();
        var overlaps = new List<(
            XiyiDesktop.DesktopItemViewModel Dragged,
            XiyiDesktop.DesktopItemViewModel Stationary,
            double Area,
            double Distance)>();

        foreach (var dragged in draggedItems)
        {
            var draggedBounds = GetVisibleItemBounds(dragged, dragged.PositionX, dragged.PositionY);
            foreach (var stationary in stationaryItems)
            {
                var originalPosition = _preDragPositions.TryGetValue(stationary, out var storedPosition)
                    ? storedPosition
                    : new Point(stationary.PositionX, stationary.PositionY);
                var stationaryBounds = GetVisibleItemBounds(stationary, originalPosition.X, originalPosition.Y);
                var overlapWidth = Math.Min(draggedBounds.X + draggedBounds.Width, stationaryBounds.X + stationaryBounds.Width) -
                    Math.Max(draggedBounds.X, stationaryBounds.X);
                var overlapHeight = Math.Min(draggedBounds.Y + draggedBounds.Height, stationaryBounds.Y + stationaryBounds.Height) -
                    Math.Max(draggedBounds.Y, stationaryBounds.Y);
                if (overlapWidth <= 0 || overlapHeight <= 0)
                    continue;

                var draggedCenterX = draggedBounds.X + draggedBounds.Width / 2;
                var draggedCenterY = draggedBounds.Y + draggedBounds.Height / 2;
                var stationaryCenterX = stationaryBounds.X + stationaryBounds.Width / 2;
                var stationaryCenterY = stationaryBounds.Y + stationaryBounds.Height / 2;
                var deltaX = draggedCenterX - stationaryCenterX;
                var deltaY = draggedCenterY - stationaryCenterY;
                overlaps.Add((dragged, stationary, overlapWidth * overlapHeight, deltaX * deltaX + deltaY * deltaY));
            }
        }

        var displaced = new Dictionary<XiyiDesktop.DesktopItemViewModel, XiyiDesktop.DesktopItemViewModel>();
        var matchedDragged = new HashSet<XiyiDesktop.DesktopItemViewModel>();
        foreach (var overlap in overlaps
                     .OrderByDescending(candidate => candidate.Area)
                     .ThenBy(candidate => candidate.Distance))
        {
            if (matchedDragged.Contains(overlap.Dragged) || displaced.Values.Contains(overlap.Stationary))
                continue;
            matchedDragged.Add(overlap.Dragged);
            displaced.Add(overlap.Dragged, overlap.Stationary);
            if (displaced.Count >= draggedItems.Length)
                break;
        }

        return displaced;
    }

    private void StopDragReflowTimer()
    {
        if (_dragReflowTimer is null)
            return;

        _dragReflowTimer.Stop();
        _dragReflowTimer.Tick -= DragReflowTimer_Tick;
        _dragReflowTimer = null;
    }

    public int GetAutoArrangeDropIndex()
    {
        return _hasActiveAutoArrangeDropIndex
            ? _activeAutoArrangeDropIndex
            : CalculateAutoArrangeDropIndex();
    }

    public int GetAutoArrangeDropIndexForDrop() => CalculateAutoArrangeDropIndex();

    public void CommitDragReflowNow()
    {
        if (_draggedItems.Count == 0 || XiyiDesktop.AppSettingsStore.DesktopAutoArrange)
            return;

        _dragOverRecycleBin = false;
        _dragReflowReady = true;
        StopDragReflowTimer();
        InvalidateArrange();
    }

    private int CalculateAutoArrangeDropIndex()
    {
        var orderedItems = Children.OfType<ContentPresenter>()
            .Select(presenter => presenter.Content as XiyiDesktop.DesktopItemViewModel)
            .Where(item => item is not null)
            .Cast<XiyiDesktop.DesktopItemViewModel>()
            .ToArray();
        var draggedCount = orderedItems.Count(_draggedItems.Contains);
        var remainingCount = orderedItems.Length - draggedCount;
        if (draggedCount == 0 || remainingCount == 0)
            return 0;

        var slotWidth = Math.Max(MinimumSlotWidth, orderedItems.Max(item => item.TileWidth));
        var slotHeight = Math.Max(MinimumSlotHeight, orderedItems.Max(item => item.TileHeight));
        var rowCount = Math.Max(1, (int)Math.Floor(NormalizeDimension(_viewportSize.Height, ActualHeight) / slotHeight));
        var centerX = _draggedItems.Average(item => item.PositionX + item.TileWidth / 2);
        var centerY = _draggedItems.Average(item => item.PositionY + item.TileHeight / 2);
        var column = Math.Max(0, (int)Math.Floor(centerX / slotWidth));
        var row = Math.Clamp((int)Math.Floor(centerY / slotHeight), 0, rowCount - 1);
        return Math.Clamp(column * rowCount + row, 0, remainingCount);
    }

    public void CommitGridPositions(IReadOnlyList<XiyiDesktop.DesktopItemViewModel> orderedItems)
    {
        if (orderedItems.Count == 0)
            return;

        var slotWidth = Math.Max(MinimumSlotWidth, orderedItems.Max(item => item.TileWidth));
        var slotHeight = Math.Max(MinimumSlotHeight, orderedItems.Max(item => item.TileHeight));
        var viewportHeight = NormalizeDimension(_viewportSize.Height, ActualHeight);
        var rowCount = Math.Max(1, (int)Math.Floor(viewportHeight / slotHeight));
        for (var index = 0; index < orderedItems.Count; index++)
            orderedItems[index].SetPosition((index / rowCount) * slotWidth, (index % rowCount) * slotHeight);
    }

    public void SnapItemsToGrid(
        IReadOnlyList<XiyiDesktop.DesktopItemViewModel> items,
        XiyiDesktop.DesktopItemViewModel anchorItem)
    {
        if (items.Count == 0)
            return;

        var slotWidth = Math.Max(MinimumSlotWidth, anchorItem.TileWidth);
        var slotHeight = Math.Max(MinimumSlotHeight, anchorItem.TileHeight);
        var snapped = DesktopLayoutEngine.Snap(
            new DesktopLayoutEngine.Point(anchorItem.PositionX, anchorItem.PositionY),
            new DesktopLayoutEngine.Size(slotWidth, slotHeight));
        var deltaX = snapped.X - anchorItem.PositionX;
        var deltaY = snapped.Y - anchorItem.PositionY;
        foreach (var item in items)
            item.SetPosition(item.PositionX + deltaX, item.PositionY + deltaY);
        InvalidateArrange();
    }

    public void ClampItemsToViewport(IReadOnlyList<XiyiDesktop.DesktopItemViewModel> items)
    {
        if (items.Count == 0)
            return;

        var minX = items.Min(item => item.PositionX);
        var minY = items.Min(item => item.PositionY);
        var maxX = items.Max(item => item.PositionX + item.TileWidth);
        var maxY = items.Max(item => item.PositionY + item.TileHeight);
        var viewportWidth = NormalizeDimension(_viewportSize.Width, ActualWidth);
        var viewportHeight = NormalizeDimension(_viewportSize.Height, ActualHeight);
        var deltaX = GetGroupClampDelta(minX, maxX, viewportWidth);
        var deltaY = GetGroupClampDelta(minY, maxY, viewportHeight);
        foreach (var item in items)
            item.SetPosition(item.PositionX + deltaX, item.PositionY + deltaY);
        InvalidateArrange();
    }

    private static double GetGroupClampDelta(double minimum, double maximum, double viewportLength)
    {
        if (viewportLength <= 0 || maximum - minimum > viewportLength)
            return -minimum;
        if (minimum < 0)
            return -minimum;
        if (maximum > viewportLength)
            return viewportLength - maximum;
        return 0;
    }

    public bool TryGetItemBounds(
        XiyiDesktop.DesktopItemViewModel item,
        UIElement coordinateTarget,
        out Rect bounds)
    {
        var presenter = FindPresenter(item);
        if (presenter is null || presenter.ActualWidth <= 0 || presenter.ActualHeight <= 0)
        {
            bounds = default;
            return false;
        }

        try
        {
            bounds = presenter.TransformToVisual(coordinateTarget).TransformBounds(
                new Rect(0, 0, presenter.ActualWidth, presenter.ActualHeight));
            return double.IsFinite(bounds.X) && double.IsFinite(bounds.Y) &&
                double.IsFinite(bounds.Width) && double.IsFinite(bounds.Height);
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException)
        {
            bounds = default;
            return false;
        }
    }

    public bool TryGetVisibleItemBounds(
        XiyiDesktop.DesktopItemViewModel item,
        UIElement coordinateTarget,
        out Rect bounds)
    {
        var presenter = FindPresenter(item);
        if (presenter is null || presenter.ActualWidth <= 0 || presenter.ActualHeight <= 0)
        {
            bounds = default;
            return false;
        }

        var padding = item.TilePadding;
        var left = Math.Clamp(padding.Left, 0, presenter.ActualWidth);
        var top = Math.Clamp(padding.Top, 0, presenter.ActualHeight);
        var width = Math.Max(0, presenter.ActualWidth - left - Math.Clamp(padding.Right, 0, presenter.ActualWidth - left));
        var height = Math.Max(0, presenter.ActualHeight - top - Math.Clamp(padding.Bottom, 0, presenter.ActualHeight - top));
        try
        {
            bounds = presenter.TransformToVisual(coordinateTarget).TransformBounds(
                new Rect(left, top, width, height));
            return double.IsFinite(bounds.X) && double.IsFinite(bounds.Y) &&
                double.IsFinite(bounds.Width) && double.IsFinite(bounds.Height);
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException)
        {
            bounds = default;
            return false;
        }
    }

    private ContentPresenter? FindPresenter(XiyiDesktop.DesktopItemViewModel item) =>
        Children.OfType<ContentPresenter>().FirstOrDefault(candidate => ReferenceEquals(candidate.Content, item));

    private void AnimateScale(UIElement element, float finalScale)
    {
        _springScaleAnimation ??= _compositor.CreateSpringVector3Animation();
        _springScaleAnimation.Target = "Scale";
        _springScaleAnimation.DampingRatio = 0.6f;
        _springScaleAnimation.Period = TimeSpan.FromMilliseconds(50);
        _springScaleAnimation.FinalValue = new Vector3(finalScale);
        var visual = ElementCompositionPreview.GetElementVisual(element);
        if (element is FrameworkElement frameworkElement)
            visual.CenterPoint = new Vector3(
                (float)(frameworkElement.ActualWidth / 2),
                (float)(frameworkElement.ActualHeight / 2),
                0);
        visual.StartAnimation("Scale", _springScaleAnimation);
    }

    public XiyiDesktop.DesktopItemViewModel? GetTopmostItemAtPoint(
        Point point,
        IEnumerable<XiyiDesktop.DesktopItemViewModel>? excludedItems = null)
    {
        var excluded = excludedItems?.ToHashSet() ?? [];
        foreach (var presenter in Children.OfType<ContentPresenter>()
                     .OrderByDescending(Canvas.GetZIndex)
                     .ThenByDescending(Children.IndexOf))
        {
            if (presenter.Visibility == Visibility.Collapsed)
                continue;
            if (presenter.Content is not XiyiDesktop.DesktopItemViewModel item || excluded.Contains(item))
                continue;

            var visual = ElementCompositionPreview.GetElementVisual(presenter);
            double left = visual.Offset.X;
            double top = visual.Offset.Y;
            left = double.IsFinite(left) ? left : item.PositionX;
            top = double.IsFinite(top) ? top : item.PositionY;

            var bounds = GetVisibleItemBounds(item, left, top);
            if (new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height).Contains(point))
                return item;
        }

        return null;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = MinimumSlotWidth;
        var height = MinimumSlotHeight;

        var iconCount = 0;
        for (var index = 0; index < Children.Count; index++)
        {
            var child = Children[index];
            child.Measure(availableSize);
            if (child.Visibility == Visibility.Collapsed)
                continue;
            if (child is FrameworkElement frameworkElement && _externalLayoutItems.Contains(frameworkElement))
                continue;
            iconCount++;
            var item = GetItemViewModel(child, index);
            width = Math.Max(width, NormalizeDimension(child.DesiredSize.Width, item?.TileWidth ?? MinimumSlotWidth));
            height = Math.Max(height, NormalizeDimension(child.DesiredSize.Height, item?.TileHeight ?? MinimumSlotHeight));
        }

        var viewportWidth = double.IsFinite(availableSize.Width) ? Math.Max(width, availableSize.Width) : 0;
        var viewportHeight = double.IsFinite(availableSize.Height) ? Math.Max(height, availableSize.Height) : 0;
        if (double.IsFinite(availableSize.Width) && availableSize.Width > 0 &&
            double.IsFinite(availableSize.Height) && availableSize.Height > 0)
        {
            // This is a fixed desktop surface. Report the viewport, not a scrollable
            // content extent, so marquee gestures cannot pan the icon layer.
            return new Size(availableSize.Width, availableSize.Height);
        }

        if (iconCount == 0)
            return new Size(viewportWidth, viewportHeight);

        if (viewportHeight > 0)
        {
            var rows = Math.Max(1, (int)Math.Floor(viewportHeight / height));
            var columns = (int)Math.Ceiling(iconCount / (double)rows);
            return new Size(Math.Max(viewportWidth, columns * width), viewportHeight);
        }

        if (viewportWidth > 0)
        {
            var columns = Math.Max(1, (int)Math.Floor(viewportWidth / width));
            var rows = (int)Math.Ceiling(iconCount / (double)columns);
            return new Size(viewportWidth, Math.Max(height, rows * height));
        }

        var autoArrangeColumns = (int)Math.Ceiling(Math.Sqrt(iconCount));
        var autoArrangeRows = (int)Math.Ceiling(iconCount / (double)autoArrangeColumns);
        return new Size(autoArrangeColumns * width, autoArrangeRows * height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var animateLayout = _animateNextArrange;
        _animateNextArrange = false;
        var autoArrange = XiyiDesktop.AppSettingsStore.DesktopAutoArrange;
        var alignToGrid = XiyiDesktop.AppSettingsStore.AlignIconsToGrid;
        var slotWidth = MinimumSlotWidth;
        var slotHeight = MinimumSlotHeight;
        var hasIconEntries = false;
        for (var index = 0; index < Children.Count; index++)
        {
            var child = Children[index];
            if (child is FrameworkElement frameworkElement && _externalLayoutItems.Contains(frameworkElement))
                continue;

            var item = GetItemViewModel(child, index);
            hasIconEntries |= item is not null;
            slotWidth = Math.Max(slotWidth, NormalizeDimension(child.DesiredSize.Width, item?.TileWidth ?? MinimumSlotWidth));
            slotHeight = Math.Max(slotHeight, NormalizeDimension(child.DesiredSize.Height, item?.TileHeight ?? MinimumSlotHeight));
        }

        if (hasIconEntries)
            _lastGridCellSize = new Size(slotWidth, slotHeight);
        else
        {
            slotWidth = _lastGridCellSize.Width;
            slotHeight = _lastGridCellSize.Height;
        }

        var layoutWidth = ResolveLayoutDimension(finalSize.Width, _viewportSize.Width, slotWidth);
        var layoutHeight = ResolveLayoutDimension(finalSize.Height, _viewportSize.Height, slotHeight);
        var rowCount = layoutHeight > 0
            ? Math.Max(1, (int)Math.Min(int.MaxValue, Math.Floor(layoutHeight / slotHeight)))
            : 1;
        var hasUsableFreeLayoutSize = layoutWidth > 0 && layoutHeight > 0;
        var collisionItems = GetVisibleExternalOccupiedItems();

        if (_draggedExternalLayoutItems.Count > 0 && _externalDragIconPositions.Count > 0 && hasUsableFreeLayoutSize)
        {
            ArrangeExternalDragPreview(
                autoArrange,
                alignToGrid,
                slotWidth,
                slotHeight,
                new Size(layoutWidth, layoutHeight));
            return base.ArrangeOverride(new Size(layoutWidth, layoutHeight));
        }

        if (_draggedItems.Count > 0 && hasUsableFreeLayoutSize)
        {
            ArrangeDraggedItems(
                alignToGrid,
                slotWidth,
                slotHeight,
                rowCount,
                new Size(layoutWidth, layoutHeight));
            return base.ArrangeOverride(new Size(layoutWidth, layoutHeight));
        }

        for (var index = 0; index < Children.Count; index++)
        {
            var child = Children[index];
            if (child.Visibility == Visibility.Collapsed)
                continue;

            var viewModel = GetItemViewModel(child, index);
            var width = NormalizeDimension(child.DesiredSize.Width, viewModel?.TileWidth ?? MinimumSlotWidth);
            var height = NormalizeDimension(child.DesiredSize.Height, viewModel?.TileHeight ?? MinimumSlotHeight);
            if (viewModel is not { } item)
            {
                if (child is FrameworkElement externalItem && _externalLayoutItems.Contains(externalItem))
                {
                    var externalX = Canvas.GetLeft(externalItem);
                    var externalY = Canvas.GetTop(externalItem);
                    if (!double.IsFinite(externalX))
                        externalX = 0;
                    if (!double.IsFinite(externalY))
                        externalY = 0;
                    SetChildPosition(externalItem, externalX, externalY, animate: animateLayout);
                    continue;
                }

                // If a container cannot yet be mapped to its source item, keep it in
                // a deterministic provisional slot rather than stacking at the origin.
                var provisionalX = (index / rowCount) * slotWidth;
                var provisionalY = (index % rowCount) * slotHeight;
                SetChildPosition(child, provisionalX, provisionalY);
                continue;
            }

            if (autoArrange)
            {
                var x = (index / rowCount) * slotWidth;
                var y = (index % rowCount) * slotHeight;
                (x, y) = ClampPosition(x, y, new Size(layoutWidth, layoutHeight), width, height, clampToViewport: true);
                var visibleBounds = GetVisibleItemBounds(item, x, y);
                if (OverlapsPlacedItem(visibleBounds.X, visibleBounds.Y, visibleBounds.Width, visibleBounds.Height, collisionItems))
                {
                    (x, y) = FindAvailableIconPosition(
                        x,
                        y,
                        slotWidth,
                        slotHeight,
                        new Size(layoutWidth, layoutHeight),
                        item,
                        collisionItems);
                }
                item.SetPosition(x, y);
            }
            else if (!hasUsableFreeLayoutSize)
            {
                // The layout system can ask the panel to arrange once before it has a
                // real viewport. Use temporary cells for this pass, but do not persist them.
                var x = (index / rowCount) * slotWidth;
                var y = (index % rowCount) * slotHeight;
                SetChildPosition(child, x, y);
                continue;
            }

            double left;
            double top;
            if (autoArrange)
            {
                // Keep a dragged item under the pointer temporarily. Other icons stay in
                // the sorted automatic layout until the drop is committed or canceled.
                left = NormalizeCoordinate(item.PositionX);
                top = NormalizeCoordinate(item.PositionY);
                var visibleBounds = GetVisibleItemBounds(item, left, top);
                if (OverlapsPlacedItem(
                        visibleBounds.X,
                        visibleBounds.Y,
                        visibleBounds.Width,
                        visibleBounds.Height,
                        collisionItems))
                {
                    (left, top) = FindAvailableIconPosition(
                        left,
                        top,
                        slotWidth,
                        slotHeight,
                        new Size(layoutWidth, layoutHeight),
                        item,
                        collisionItems);
                    item.SetPosition(left, top);
                }
                collisionItems.Add(GetVisibleItemBounds(item, left, top));
            }
            else
            {
                var desiredX = item.HasPosition ? item.PositionX : (index / rowCount) * slotWidth;
                var desiredY = item.HasPosition ? item.PositionY : (index % rowCount) * slotHeight;
                if (alignToGrid)
                {
                    desiredX = Math.Round(desiredX / slotWidth) * slotWidth;
                    desiredY = Math.Round(desiredY / slotHeight) * slotHeight;
                }
                (left, top) = ClampPosition(
                    desiredX,
                    desiredY,
                    new Size(layoutWidth, layoutHeight),
                    width,
                    height,
                    clampToViewport: !autoArrange);

                var visibleBounds = GetVisibleItemBounds(item, left, top);
                if (OverlapsPlacedItem(
                        visibleBounds.X,
                        visibleBounds.Y,
                        visibleBounds.Width,
                        visibleBounds.Height,
                        collisionItems))
                {
                    (left, top) = FindAvailableIconPosition(
                        left,
                        top,
                        slotWidth,
                        slotHeight,
                        new Size(layoutWidth, layoutHeight),
                        item,
                        collisionItems);
                }

                collisionItems.Add(GetVisibleItemBounds(item, left, top));
            }

            item.SetPosition(left, top);

            SetChildPosition(child, left, top, animate: animateLayout);
        }

        // Let Canvas arrange its children from Canvas.Left/Top and DesiredSize.
        // This avoids directly arranging presenter children while their content
        // templates are being materialized or refreshed.
        return base.ArrangeOverride(new Size(layoutWidth, layoutHeight));
    }

    private void ArrangeDraggedItems(
        bool alignToGrid,
        double slotWidth,
        double slotHeight,
        int rowCount,
        Size layoutSize)
    {
        var orderedItems = Children.OfType<ContentPresenter>()
            .Select(presenter => presenter.Content as XiyiDesktop.DesktopItemViewModel)
            .Where(item => item is not null)
            .Cast<XiyiDesktop.DesktopItemViewModel>()
            .ToArray();
        if (orderedItems.Length == 0)
            return;

        if (_dragOverRecycleBin)
        {
            foreach (var item in orderedItems)
            {
                if (FindPresenter(item) is { } presenter)
                {
                    if (_draggedItems.Contains(item))
                    {
                        SetChildPosition(presenter, item.PositionX, item.PositionY, animate: false);
                    }
                    else if (_preDragPositions.TryGetValue(item, out var originalPosition))
                    {
                        item.SetPosition(originalPosition.X, originalPosition.Y);
                        SetChildPosition(presenter, originalPosition.X, originalPosition.Y, animate: true);
                    }
                }
            }
            return;
        }

        if (!_dragReflowReady)
        {
            foreach (var item in orderedItems.Where(_draggedItems.Contains))
            {
                if (FindPresenter(item) is { } presenter)
                    SetChildPosition(presenter, item.PositionX, item.PositionY, animate: false);
            }
            return;
        }

        var freeArrangeCollisionItems = GetVisibleExternalOccupiedItems();
        var displacedItemsByDragged = GetDragPreviewDisplacedItems();
        var displacedItemSet = displacedItemsByDragged.Values.ToHashSet();
        foreach (var item in orderedItems.Where(_draggedItems.Contains))
        {
            var left = NormalizeCoordinate(item.PositionX);
            var top = NormalizeCoordinate(item.PositionY);
            freeArrangeCollisionItems.Add(GetVisibleItemBounds(item, left, top));
            if (FindPresenter(item) is { } presenter)
                SetChildPosition(presenter, left, top, animate: false);
        }

        var displacedItems = new List<(
            int Index,
            XiyiDesktop.DesktopItemViewModel Item,
            ContentPresenter Presenter,
            double RequestedX,
            double RequestedY)>();
        for (var index = 0; index < orderedItems.Length; index++)
        {
            var item = orderedItems[index];
            if (_draggedItems.Contains(item) || FindPresenter(item) is not { } presenter)
                continue;

            var fallbackX = item.HasPosition ? item.PositionX : (index / rowCount) * slotWidth;
            var fallbackY = item.HasPosition ? item.PositionY : (index % rowCount) * slotHeight;
            var desiredX = _preDragPositions.TryGetValue(item, out var originalPosition) ? originalPosition.X : fallbackX;
            var desiredY = _preDragPositions.TryGetValue(item, out originalPosition) ? originalPosition.Y : fallbackY;
            if (alignToGrid)
            {
                desiredX = Math.Round(desiredX / slotWidth) * slotWidth;
                desiredY = Math.Round(desiredY / slotHeight) * slotHeight;
            }

            var (left, top) = ClampPosition(
                desiredX,
                desiredY,
                layoutSize,
                item.TileWidth,
                item.TileHeight,
                clampToViewport: true);
            if (displacedItemSet.Contains(item))
            {
                displacedItems.Add((index, item, presenter, left, top));
                continue;
            }

            item.SetPosition(left, top);
            var visibleBounds = GetVisibleItemBounds(item, left, top);
            freeArrangeCollisionItems.Add(visibleBounds);
            SetChildPosition(presenter, left, top, animate: true);
        }

        // Keep every unaffected icon's original slot reserved before assigning new
        // slots to colliding icons. Otherwise the first displaced icon can steal a
        // later icon's old slot, cascading a single drag into a full desktop shuffle.
        var displacedByItem = displacedItems.ToDictionary(entry => entry.Item);
        foreach (var pair in displacedItemsByDragged)
        {
            if (!displacedByItem.TryGetValue(pair.Value, out var displaced))
                continue;

            var sourcePosition = _preDragPositions.TryGetValue(pair.Key, out var originalPosition)
                ? originalPosition
                : new Point(pair.Key.PositionX, pair.Key.PositionY);
            var (sourceX, sourceY) = ClampPosition(
                sourcePosition.X,
                sourcePosition.Y,
                layoutSize,
                displaced.Item.TileWidth,
                displaced.Item.TileHeight,
                clampToViewport: true);
            var sourceBounds = GetVisibleItemBounds(displaced.Item, sourceX, sourceY);
            var (left, top) = OverlapsPlacedItem(
                sourceBounds.X,
                sourceBounds.Y,
                sourceBounds.Width,
                sourceBounds.Height,
                freeArrangeCollisionItems)
                ? FindAvailableIconPosition(
                    sourcePosition.X,
                    sourcePosition.Y,
                    slotWidth,
                    slotHeight,
                    layoutSize,
                    displaced.Item,
                    freeArrangeCollisionItems)
                : (sourceX, sourceY);
            displaced.Item.SetPosition(left, top);
            freeArrangeCollisionItems.Add(GetVisibleItemBounds(displaced.Item, left, top));
            SetChildPosition(displaced.Presenter, left, top, animate: true);
        }
    }

    private void ArrangeExternalDragPreview(
        bool autoArrange,
        bool alignToGrid,
        double slotWidth,
        double slotHeight,
        Size layoutSize)
    {
        var orderedItems = Children.OfType<ContentPresenter>()
            .Select(presenter => presenter.Content as XiyiDesktop.DesktopItemViewModel)
            .Where(item => item is not null)
            .Cast<XiyiDesktop.DesktopItemViewModel>()
            .ToArray();
        var collisionItems = GetVisibleExternalOccupiedItems();
        var movingWidgetBounds = _draggedExternalLayoutItems
            .Where(Children.Contains)
            .Select(GetVisibleExternalBounds)
            .ToArray();
        var displacedByMovingWidget = new HashSet<XiyiDesktop.DesktopItemViewModel>();
        var displacedItems = new List<(
            int Index,
            XiyiDesktop.DesktopItemViewModel Item,
            ContentPresenter Presenter,
            double RequestedX,
            double RequestedY)>();

        foreach (var (index, item) in orderedItems.Select((item, index) => (index, item)))
        {
            if (FindPresenter(item) is not { } presenter)
                continue;

            double left;
            double top;
            if (_externalDragIconPositions.TryGetValue(item, out var originalPosition))
            {
                left = originalPosition.X;
                top = originalPosition.Y;
            }
            else
            {
                left = item.PositionX;
                top = item.PositionY;
            }

            if ((autoArrange || alignToGrid))
            {
                left = Math.Round(left / slotWidth) * slotWidth;
                top = Math.Round(top / slotHeight) * slotHeight;
            }

            (left, top) = ClampPosition(left, top, layoutSize, item.TileWidth, item.TileHeight, clampToViewport: true);
            var visibleBounds = GetVisibleItemBounds(item, left, top);
            var overlapsMovingWidget = movingWidgetBounds.Any(widgetBounds =>
                visibleBounds.X < widgetBounds.X + widgetBounds.Width &&
                visibleBounds.X + visibleBounds.Width > widgetBounds.X &&
                visibleBounds.Y < widgetBounds.Y + widgetBounds.Height &&
                visibleBounds.Y + visibleBounds.Height > widgetBounds.Y);
            if (overlapsMovingWidget)
            {
                displacedItems.Add((index, item, presenter, left, top));
                displacedByMovingWidget.Add(item);
                continue;
            }

            item.SetPosition(left, top);
            collisionItems.Add(visibleBounds);
            SetChildPosition(presenter, left, top, animate: _externalDragDisplacedIcons.Contains(item));
        }

        foreach (var displaced in displacedItems)
        {
            var (left, top) = FindAvailableIconPosition(
                displaced.RequestedX,
                displaced.RequestedY,
                slotWidth,
                slotHeight,
                layoutSize,
                displaced.Item,
                collisionItems);
            displaced.Item.SetPosition(left, top);
            collisionItems.Add(GetVisibleItemBounds(displaced.Item, left, top));
            SetChildPosition(displaced.Presenter, left, top, animate: true);
        }

        _externalDragDisplacedIcons.Clear();
        _externalDragDisplacedIcons.UnionWith(displacedByMovingWidget);
    }

    private (double X, double Y) FindAvailableIconPosition(
        double requestedX,
        double requestedY,
        double slotWidth,
        double slotHeight,
        Size layoutSize,
        XiyiDesktop.DesktopItemViewModel item,
        IReadOnlyList<(double X, double Y, double Width, double Height)> occupiedItems)
    {
        var columns = Math.Max(1, (int)Math.Floor(
            Math.Max(0, layoutSize.Width - item.TileWidth + 0.001) / slotWidth) + 1);
        var rows = Math.Max(1, (int)Math.Floor(
            Math.Max(0, layoutSize.Height - item.TileHeight + 0.001) / slotHeight) + 1);
        var requestedBounds = GetVisibleItemBounds(item, requestedX, requestedY);
        var requestedCenterX = requestedBounds.X + requestedBounds.Width / 2;
        var requestedCenterY = requestedBounds.Y + requestedBounds.Height / 2;
        var bestDistance = double.PositiveInfinity;
        var bestPosition = default((double X, double Y));
        var foundPosition = false;

        // Search the whole grid and pick the closest free cell. A linear wrap
        // makes an adjacent vacancy lose to a much later column-major slot.
        for (var column = 0; column < columns; column++)
        {
            for (var row = 0; row < rows; row++)
            {
                var left = column * slotWidth;
                var top = row * slotHeight;
                var visible = GetVisibleItemBounds(item, left, top);
                if (OverlapsPlacedItem(visible.X, visible.Y, visible.Width, visible.Height, occupiedItems))
                    continue;

                var centerX = visible.X + visible.Width / 2;
                var centerY = visible.Y + visible.Height / 2;
                var deltaX = centerX - requestedCenterX;
                var deltaY = centerY - requestedCenterY;
                var distance = deltaX * deltaX + deltaY * deltaY;
                if (distance >= bestDistance)
                    continue;

                bestDistance = distance;
                bestPosition = (left, top);
                foundPosition = true;
            }
        }

        if (foundPosition)
            return bestPosition;

        return ClampPosition(requestedX, requestedY, layoutSize, item.TileWidth, item.TileHeight, clampToViewport: true);
    }

    private static bool OverlapsPlacedItem(
        double x,
        double y,
        double width,
        double height,
        IReadOnlyList<(double X, double Y, double Width, double Height)> placedItems)
    {
        var bounds = new DesktopLayoutEngine.Rect(x, y, width, height);
        return placedItems.Any(placed => DesktopLayoutEngine.Intersects(
            bounds,
            new DesktopLayoutEngine.Rect(placed.X, placed.Y, placed.Width, placed.Height)));
    }

    private XiyiDesktop.DesktopItemViewModel? GetItemViewModel(UIElement element, int index)
    {
        if (element is ContentPresenter { Content: XiyiDesktop.DesktopItemViewModel presenterItem })
            return presenterItem;

        if (element is FrameworkElement { DataContext: XiyiDesktop.DesktopItemViewModel dataContextItem })
            return dataContextItem;
        return index >= 0 && index < Children.Count && Children[index] is ContentPresenter { Content: XiyiDesktop.DesktopItemViewModel indexedItem }
            ? indexedItem
            : null;
    }

    public void ClampItemPosition(XiyiDesktop.DesktopItemViewModel item)
    {
        var (left, top) = ClampPosition(
            item.PositionX,
            item.PositionY,
            new Size(NormalizeDimension(ActualWidth, _viewportSize.Width), NormalizeDimension(ActualHeight, _viewportSize.Height)),
            item.TileWidth,
            item.TileHeight,
            clampToViewport: true);
        item.SetPosition(left, top);
        InvalidateArrange();
    }

    public void SnapItemPosition(XiyiDesktop.DesktopItemViewModel item)
    {
        var slotWidth = Math.Max(MinimumSlotWidth, item.TileWidth);
        var slotHeight = Math.Max(MinimumSlotHeight, item.TileHeight);
        var snapped = DesktopLayoutEngine.Snap(
            new DesktopLayoutEngine.Point(item.PositionX, item.PositionY),
            new DesktopLayoutEngine.Size(slotWidth, slotHeight));
        item.SetPosition(snapped.X, snapped.Y);
        InvalidateArrange();
    }

    private (double X, double Y) ClampPosition(
        double x,
        double y,
        Size panelSize,
        double itemWidth,
        double itemHeight,
        bool clampToViewport = false)
    {
        itemWidth = NormalizeDimension(itemWidth, MinimumSlotWidth);
        itemHeight = NormalizeDimension(itemHeight, MinimumSlotHeight);
        var width = NormalizeDimension(panelSize.Width, itemWidth);
        var height = NormalizeDimension(panelSize.Height, itemHeight);
        if (clampToViewport)
        {
            if (double.IsFinite(_viewportSize.Width) && _viewportSize.Width > 0)
                width = Math.Min(width, _viewportSize.Width);
            if (double.IsFinite(_viewportSize.Height) && _viewportSize.Height > 0)
                height = Math.Min(height, _viewportSize.Height);
        }
        var clamped = DesktopLayoutEngine.Clamp(
            new DesktopLayoutEngine.Point(x, y),
            new DesktopLayoutEngine.Size(width, height),
            new DesktopLayoutEngine.Size(itemWidth, itemHeight));
        return (clamped.X, clamped.Y);
    }

    private static double NormalizeDimension(double value, double fallback)
    {
        if (!double.IsFinite(fallback) || fallback < 0)
            fallback = 0;
        if (!double.IsFinite(value) || value < 0)
            return fallback;
        return Math.Min(value, 100000);
    }

    private static double ResolveLayoutDimension(double finalSize, double viewportSize, double minimum)
    {
        var resolved = double.IsFinite(finalSize) && finalSize > 0
            ? finalSize
            : double.IsFinite(viewportSize) && viewportSize > 0
                ? viewportSize
                : 0;
        return Math.Min(resolved, 100000);
    }

    private static double NormalizeCoordinate(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, -100000, 100000) : 0;

    private void SetChildPosition(UIElement child, double left, double top, bool animate = false)
    {
        var x = NormalizeCoordinate(left);
        var y = NormalizeCoordinate(top);
        var currentX = Canvas.GetLeft(child);
        var currentY = Canvas.GetTop(child);
        currentX = double.IsFinite(currentX) ? currentX : 0;
        currentY = double.IsFinite(currentY) ? currentY : 0;
        var element = child as FrameworkElement;
        // Layout is invalidated on every pointer move. If this icon already has the
        // requested Canvas slot, let its current visual animation finish instead of
        // restarting it on every arrange pass.
        if (Math.Abs(currentX - x) < 0.1 && Math.Abs(currentY - y) < 0.1)
            return;

        var currentTransform = element?.RenderTransform as TranslateTransform;
        var offsetX = currentTransform?.X ?? 0;
        var offsetY = currentTransform?.Y ?? 0;

        if (element is not null)
            StopPositionAnimation(element);

        if (animate && element is not null)
        {
            element.RenderTransform = new TranslateTransform
            {
                X = currentX + offsetX - x,
                Y = currentY + offsetY - y
            };
        }
        else if (element is not null)
        {
            element.RenderTransform = null;
        }

        Canvas.SetLeft(child, x);
        Canvas.SetTop(child, y);
        if (animate && element?.RenderTransform is TranslateTransform transform)
            AnimatePositionChange(element, transform);
    }

    private void AnimatePositionChange(FrameworkElement element, TranslateTransform transform)
    {
        var storyboard = new Storyboard();
        var easing = new CircleEase { EasingMode = EasingMode.EaseInOut };
        var horizontal = new DoubleAnimation
        {
            To = 0,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = easing,
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(horizontal, transform);
        Storyboard.SetTargetProperty(horizontal, "X");
        storyboard.Children.Add(horizontal);

        var vertical = new DoubleAnimation
        {
            To = 0,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = easing,
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(vertical, transform);
        Storyboard.SetTargetProperty(vertical, "Y");
        storyboard.Children.Add(vertical);
        storyboard.Completed += (_, _) =>
        {
            if (_positionAnimations.TryGetValue(element, out var activeStoryboard) &&
                ReferenceEquals(activeStoryboard, storyboard))
            {
                _positionAnimations.Remove(element);
                element.RenderTransform = null;
            }
        };

        _positionAnimations[element] = storyboard;
        storyboard.Begin();
    }

    private void StopPositionAnimation(FrameworkElement element)
    {
        if (_positionAnimations.Remove(element, out var storyboard))
            storyboard.Stop();
    }
}
