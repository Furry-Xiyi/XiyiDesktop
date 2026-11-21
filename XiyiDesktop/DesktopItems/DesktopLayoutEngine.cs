using System;
using System.Collections.Generic;
using System.Linq;

namespace XiyiDesktop.DesktopItems;

/// <summary>
/// Shared, UI-independent geometry and occupancy rules for desktop icons and widgets.
/// The visual layer supplies stable item order and consumes the returned positions.
/// </summary>
internal static class DesktopLayoutEngine
{
    private const double MinimumDimension = 1;

    internal readonly record struct Point(double X, double Y);

    internal readonly record struct Size(double Width, double Height);

    internal readonly record struct Rect(double X, double Y, double Width, double Height)
    {
        public double Right => X + Width;
        public double Bottom => Y + Height;
    }

    internal readonly record struct Placement(string Identity, Point Position, Size Footprint);

    public static Point Snap(Point position, Size cell)
    {
        var width = NormalizeDimension(cell.Width);
        var height = NormalizeDimension(cell.Height);
        return new Point(
            Math.Round(NormalizeCoordinate(position.X) / width) * width,
            Math.Round(NormalizeCoordinate(position.Y) / height) * height);
    }

    public static Point Clamp(Point position, Size viewport, Size footprint, double padding = 0)
    {
        var width = NormalizeDimension(footprint.Width);
        var height = NormalizeDimension(footprint.Height);
        var viewportWidth = NormalizeDimension(viewport.Width, width);
        var viewportHeight = NormalizeDimension(viewport.Height, height);
        var inset = double.IsFinite(padding) ? Math.Clamp(padding, 0, 100000) : 0;
        return new Point(
            Math.Clamp(NormalizeCoordinate(position.X), inset, Math.Max(inset, viewportWidth - width - inset)),
            Math.Clamp(NormalizeCoordinate(position.Y), inset, Math.Max(inset, viewportHeight - height - inset)));
    }

    public static bool Intersects(Rect left, Rect right) =>
        left.X < right.Right && left.Right > right.X &&
        left.Y < right.Bottom && left.Bottom > right.Y;

    /// <summary>
    /// Resolves a stable ordered set against reserved placements, preserving existing
    /// coordinates when possible and moving collisions to the first available grid slot.
    /// </summary>
    public static IReadOnlyList<Placement> ResolveCollisions(
        IReadOnlyList<Rect> reserved,
        IReadOnlyList<Placement> orderedItems,
        Size viewport,
        Size cell,
        double padding = 0)
    {
        ArgumentNullException.ThrowIfNull(reserved);
        ArgumentNullException.ThrowIfNull(orderedItems);

        var occupied = new List<Rect>(reserved);
        var resolved = new List<Placement>(orderedItems.Count);
        foreach (var item in orderedItems)
        {
            var footprint = new Size(
                NormalizeDimension(item.Footprint.Width),
                NormalizeDimension(item.Footprint.Height));
            var position = Clamp(item.Position, viewport, footprint, padding);
            var bounds = new Rect(position.X, position.Y, footprint.Width, footprint.Height);
            if (occupied.Exists(existing => Intersects(bounds, existing)) &&
                TryFindAvailablePosition(position, viewport, cell, footprint, occupied, out var available, padding))
            {
                position = available;
                bounds = new Rect(position.X, position.Y, footprint.Width, footprint.Height);
            }

            resolved.Add(item with { Position = position, Footprint = footprint });
            occupied.Add(bounds);
        }

        return resolved;
    }

    /// <summary>
    /// Resolves widget collisions while keeping all placements clear of reserved
    /// desktop items. Widget-to-widget conflicts are resolved along the overlap's
    /// dominant axis; vertical conflicts prefer the current column.
    /// </summary>
    public static IReadOnlyList<Placement> ResolveWidgetCollisions(
        IReadOnlyList<Rect> reserved,
        IReadOnlyList<Placement> orderedWidgets,
        Size viewport,
        Size cell,
        double padding = 0,
        string? allowReservedOverlapForIdentity = null)
    {
        ArgumentNullException.ThrowIfNull(reserved);
        ArgumentNullException.ThrowIfNull(orderedWidgets);

        var states = orderedWidgets.Select(widget =>
        {
            var footprint = new Size(
                NormalizeDimension(widget.Footprint.Width),
                NormalizeDimension(widget.Footprint.Height));
            var position = Clamp(widget.Position, viewport, footprint, padding);
            var bounds = new Rect(position.X, position.Y, footprint.Width, footprint.Height);
            var overlapsReserved = reserved.Any(existing => Intersects(bounds, existing));
            var allowReservedOverlap = string.Equals(
                widget.Identity,
                allowReservedOverlapForIdentity,
                StringComparison.OrdinalIgnoreCase);
            return (Widget: widget, Footprint: footprint, Position: position, Bounds: bounds,
                IsMoved: overlapsReserved && !allowReservedOverlap);
        }).ToArray();

        // Keep stationary widgets' original cells reserved before relocating anything.
        // Otherwise an earlier displaced widget can steal a later widget's valid slot.
        for (var left = 0; left < states.Length; left++)
        {
            for (var right = left + 1; right < states.Length; right++)
            {
                if (!Intersects(states[left].Bounds, states[right].Bounds))
                    continue;

                // If the earlier widget is already yielding to a desktop icon,
                // leave a later widget in place whenever that resolves the pair.
                if (states[left].IsMoved && !states[right].IsMoved)
                    continue;

                // The already prioritized widget keeps its requested position;
                // later widgets yield and find the nearest available grid cell.
                states[right].IsMoved = true;
            }
        }

        var occupied = new List<Rect>(reserved);
        foreach (var state in states.Where(state => !state.IsMoved))
            occupied.Add(state.Bounds);

        var resolved = new List<Placement>(states.Length);
        foreach (var state in states)
        {
            var position = state.Position;
            var bounds = state.Bounds;
            if (state.IsMoved)
            {
                var collision = states
                    .Where(other => !string.Equals(
                        other.Widget.Identity,
                        state.Widget.Identity,
                        StringComparison.OrdinalIgnoreCase))
                    .Select(other =>
                    {
                        var overlapWidth = Math.Min(bounds.Right, other.Bounds.Right) - Math.Max(bounds.X, other.Bounds.X);
                        var overlapHeight = Math.Min(bounds.Bottom, other.Bounds.Bottom) - Math.Max(bounds.Y, other.Bounds.Y);
                        return (other, overlapWidth, overlapHeight);
                    })
                    .Where(candidate => candidate.overlapWidth > 0 && candidate.overlapHeight > 0)
                    .OrderBy(candidate => candidate.overlapWidth * candidate.overlapHeight)
                    .FirstOrDefault();
                var overlapsWidget = collision.overlapWidth > 0 && collision.overlapHeight > 0;
                var preferHorizontal = overlapsWidget && collision.overlapWidth < collision.overlapHeight;
                Point available = default;
                var found = overlapsWidget
                    ? TryFindAvailableWidgetPosition(position, viewport, cell, state.Footprint, occupied, out available, padding, preferHorizontal)
                    : TryFindAvailablePosition(position, viewport, cell, state.Footprint, occupied, out available, padding);
                if (found)
                {
                    position = available;
                    bounds = new Rect(position.X, position.Y, state.Footprint.Width, state.Footprint.Height);
                }
                occupied.Add(bounds);
            }

            resolved.Add(state.Widget with { Position = position, Footprint = state.Footprint });
        }

        return resolved;
    }

    /// <summary>
    /// Finds the first legal, unoccupied grid slot in stable column-major order.
    /// Every viewport slot is visited at most once; a full desktop is reported as failure.
    /// </summary>
    public static bool TryFindAvailablePosition(
        Point requestedPosition,
        Size viewport,
        Size cell,
        Size footprint,
        IReadOnlyList<Rect> occupied,
        out Point position,
        double padding = 0)
    {
        ArgumentNullException.ThrowIfNull(occupied);
        position = default;

        var cellWidth = NormalizeDimension(cell.Width);
        var cellHeight = NormalizeDimension(cell.Height);
        var itemWidth = NormalizeDimension(footprint.Width);
        var itemHeight = NormalizeDimension(footprint.Height);
        var viewportWidth = NormalizeDimension(viewport.Width);
        var viewportHeight = NormalizeDimension(viewport.Height);
        var inset = double.IsFinite(padding) ? Math.Clamp(padding, 0, 100000) : 0;
        var usableWidth = Math.Max(0, viewportWidth - inset * 2);
        var usableHeight = Math.Max(0, viewportHeight - inset * 2);
        if (usableWidth + 0.001 < itemWidth || usableHeight + 0.001 < itemHeight)
            return false;

        var columns = Math.Max(1, (int)Math.Floor((usableWidth - itemWidth + 0.001) / cellWidth) + 1);
        var rows = Math.Max(1, (int)Math.Floor((usableHeight - itemHeight + 0.001) / cellHeight) + 1);
        var slotCount = (long)columns * rows;
        if (slotCount <= 0)
            return false;

        var clampedStart = Clamp(requestedPosition, viewport, new Size(itemWidth, itemHeight), inset);
        var startColumn = Math.Clamp((int)Math.Floor(Math.Max(0, clampedStart.X - inset) / cellWidth), 0, columns - 1);
        var startRow = Math.Clamp((int)Math.Floor(Math.Max(0, clampedStart.Y - inset) / cellHeight), 0, rows - 1);
        var startIndex = (long)startColumn * rows + startRow;

        for (long offset = 0; offset < slotCount; offset++)
        {
            var index = (startIndex + offset) % slotCount;
            var column = (int)(index / rows);
            var row = (int)(index % rows);
            var candidate = new Point(inset + column * cellWidth, inset + row * cellHeight);
            var bounds = new Rect(candidate.X, candidate.Y, itemWidth, itemHeight);
            var hasCollision = false;
            for (var occupiedIndex = 0; occupiedIndex < occupied.Count; occupiedIndex++)
            {
                if (!Intersects(bounds, occupied[occupiedIndex]))
                    continue;
                hasCollision = true;
                break;
            }

            if (hasCollision)
                continue;

            position = candidate;
            return true;
        }

        return false;
    }

    private static bool TryFindAvailableWidgetPosition(
        Point requestedPosition,
        Size viewport,
        Size cell,
        Size footprint,
        IReadOnlyList<Rect> occupied,
        out Point position,
        double padding = 0,
        bool preferHorizontal = false)
    {
        ArgumentNullException.ThrowIfNull(occupied);
        position = default;

        var cellWidth = NormalizeDimension(cell.Width);
        var cellHeight = NormalizeDimension(cell.Height);
        var itemWidth = NormalizeDimension(footprint.Width);
        var itemHeight = NormalizeDimension(footprint.Height);
        var viewportWidth = NormalizeDimension(viewport.Width);
        var viewportHeight = NormalizeDimension(viewport.Height);
        var inset = double.IsFinite(padding) ? Math.Clamp(padding, 0, 100000) : 0;
        var usableWidth = Math.Max(0, viewportWidth - inset * 2);
        var usableHeight = Math.Max(0, viewportHeight - inset * 2);
        if (usableWidth + 0.001 < itemWidth || usableHeight + 0.001 < itemHeight)
            return false;

        var columns = Math.Max(1, (int)Math.Floor((usableWidth - itemWidth + 0.001) / cellWidth) + 1);
        var rows = Math.Max(1, (int)Math.Floor((usableHeight - itemHeight + 0.001) / cellHeight) + 1);
        var clampedStart = Clamp(requestedPosition, viewport, new Size(itemWidth, itemHeight), inset);
        var startColumn = Math.Clamp((int)Math.Round(Math.Max(0, clampedStart.X - inset) / cellWidth), 0, columns - 1);
        var startRow = Math.Clamp((int)Math.Round(Math.Max(0, clampedStart.Y - inset) / cellHeight), 0, rows - 1);

        bool TryPosition(int column, int row, out Point candidate)
        {
            candidate = new Point(inset + column * cellWidth, inset + row * cellHeight);
            var bounds = new Rect(candidate.X, candidate.Y, itemWidth, itemHeight);
            return !occupied.Any(existing => Intersects(bounds, existing));
        }

        if (preferHorizontal)
        {
            // A left/right collision should first look for a neighboring column
            // in the same row so existing widgets can sit side by side.
            for (var distance = 1; distance < columns; distance++)
            {
                var columnLeft = startColumn - distance;
                if (columnLeft >= 0 && TryPosition(columnLeft, startRow, out position))
                    return true;

                var columnRight = startColumn + distance;
                if (columnRight < columns && TryPosition(columnRight, startRow, out position))
                    return true;
            }
        }

        // Search up and down in the current column before moving sideways.
        for (var distance = 0; distance < rows; distance++)
        {
            var rowAbove = startRow - distance;
            if (rowAbove >= 0 && TryPosition(startColumn, rowAbove, out position))
                return true;

            var rowBelow = startRow + distance;
            if (distance > 0 && rowBelow < rows && TryPosition(startColumn, rowBelow, out position))
                return true;
        }

        // Check each adjacent column from its top row down before moving farther away.
        for (var distance = 1; distance < columns; distance++)
        {
            var columnLeft = startColumn - distance;
            if (columnLeft >= 0)
            {
                for (var row = 0; row < rows; row++)
                {
                    if (preferHorizontal && row == startRow)
                        continue;
                    if (TryPosition(columnLeft, row, out position))
                        return true;
                }
            }

            var columnRight = startColumn + distance;
            if (columnRight < columns)
            {
                for (var row = 0; row < rows; row++)
                {
                    if (preferHorizontal && row == startRow)
                        continue;
                    if (TryPosition(columnRight, row, out position))
                        return true;
                }
            }
        }

        return false;
    }

    private static double NormalizeDimension(double value, double fallback = MinimumDimension)
    {
        if (!double.IsFinite(value) || value <= 0)
            value = fallback;
        return Math.Clamp(value, MinimumDimension, 100000);
    }

    private static double NormalizeCoordinate(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, -100000, 100000) : 0;
}

/// <summary>Captures one layout edit so drag previews can be committed or rolled back as a unit.</summary>
internal sealed class DesktopLayoutTransaction
{
    private readonly Dictionary<string, DesktopLayoutEngine.Point> _originalPositions;
    private Dictionary<string, DesktopLayoutEngine.Point> _previewPositions;
    private bool _isActive = true;

    public DesktopLayoutTransaction(IReadOnlyDictionary<string, DesktopLayoutEngine.Point> initialPositions)
    {
        ArgumentNullException.ThrowIfNull(initialPositions);
        _originalPositions = initialPositions.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.OrdinalIgnoreCase);
        _previewPositions = new Dictionary<string, DesktopLayoutEngine.Point>(_originalPositions, StringComparer.OrdinalIgnoreCase);
    }

    public void UpdatePreview(IReadOnlyDictionary<string, DesktopLayoutEngine.Point> positions)
    {
        ArgumentNullException.ThrowIfNull(positions);
        EnsureActive();
        _previewPositions = positions.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyDictionary<string, DesktopLayoutEngine.Point> Commit()
    {
        EnsureActive();
        _isActive = false;
        return new Dictionary<string, DesktopLayoutEngine.Point>(_previewPositions, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyDictionary<string, DesktopLayoutEngine.Point> Cancel()
    {
        EnsureActive();
        _isActive = false;
        return new Dictionary<string, DesktopLayoutEngine.Point>(_originalPositions, StringComparer.OrdinalIgnoreCase);
    }

    private void EnsureActive()
    {
        if (!_isActive)
            throw new InvalidOperationException("The desktop layout transaction is already complete.");
    }
}
