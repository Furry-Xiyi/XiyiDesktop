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
