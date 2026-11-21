using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace XiyiDesktop.DesktopItems;

/// <summary>
/// Stores the user's manual desktop arrangement as stable identities and surface-relative coordinates.
/// </summary>
internal sealed class DesktopLayoutStore
{
    private const int CurrentVersion = 2;
    private static readonly AppJsonSerializerContext JsonContext = new(
        new JsonSerializerOptions { WriteIndented = true });
    private static readonly string LayoutFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "XiyiDesktop",
        "desktop-layout.json");

    private readonly object _sync = new();
    private DesktopLayoutData _layout = Load();

    public bool HasSavedOrder
    {
        get
        {
            lock (_sync)
                return _layout.OrderedIdentities.Count > 0;
        }
    }

    public bool TryGetPosition(string identity, out double x, out double y)
    {
        if (TryGetPosition(identity, out DesktopItemPosition position))
        {
            x = position.X;
            y = position.Y;
            return true;
        }

        x = 0;
        y = 0;
        return false;
    }

    public bool TryGetPosition(string identity, out DesktopItemPosition position)
    {
        lock (_sync)
        {
            var savedPosition = (_layout.Positions ?? []).FirstOrDefault(item =>
                string.Equals(item.Identity, identity, StringComparison.OrdinalIgnoreCase));
            if (savedPosition is not null)
            {
                position = savedPosition;
                return true;
            }
        }

        position = default!;
        return false;
    }

    public IReadOnlyList<DesktopItem> ApplyOrder(IEnumerable<DesktopItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var itemList = items.ToList();
        lock (_sync)
        {
            if (_layout.OrderedIdentities.Count == 0)
                return itemList;

            var byIdentity = itemList.ToDictionary(item => item.Identity, StringComparer.OrdinalIgnoreCase);
            var ordered = new List<DesktopItem>(itemList.Count);
            var included = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var identity in _layout.OrderedIdentities)
            {
                if (byIdentity.TryGetValue(identity, out var item) && included.Add(identity))
                    ordered.Add(item);
            }

            foreach (var item in itemList)
            {
                if (included.Add(item.Identity))
                    ordered.Add(item);
            }

            return ordered;
        }
    }

    public void SaveOrder(IEnumerable<string> identities)
    {
        ArgumentNullException.ThrowIfNull(identities);

        var nextOrder = identities
            .Where(identity => !string.IsNullOrWhiteSpace(identity))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        lock (_sync)
        {
            _layout = _layout with { OrderedIdentities = nextOrder };
            SaveLocked();
        }
    }

    public void SaveLayout(IEnumerable<string> identities, IEnumerable<DesktopItemPosition> positions)
    {
        ArgumentNullException.ThrowIfNull(identities);
        ArgumentNullException.ThrowIfNull(positions);

        var orderedIdentities = identities
            .Where(identity => !string.IsNullOrWhiteSpace(identity))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var positionList = positions
            .Where(position => !string.IsNullOrWhiteSpace(position.Identity) &&
                double.IsFinite(position.X) && double.IsFinite(position.Y))
            .GroupBy(position => position.Identity, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToList();
        lock (_sync)
        {
            var mergedPositions = (_layout.Positions ?? [])
                .ToDictionary(position => position.Identity, StringComparer.OrdinalIgnoreCase);
            foreach (var position in positionList)
                mergedPositions[position.Identity] = position;

            _layout = new DesktopLayoutData(
                CurrentVersion,
                orderedIdentities,
                mergedPositions.Values.ToList());
            SaveLocked();
        }
    }

    private void SaveLocked()
    {
        try
        {
            var directory = Path.GetDirectoryName(LayoutFilePath)!;
            Directory.CreateDirectory(directory);

            var temporaryPath = LayoutFilePath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_layout, JsonContext.DesktopLayout));

            if (File.Exists(LayoutFilePath))
                File.Move(temporaryPath, LayoutFilePath, overwrite: true);
            else
                File.Move(temporaryPath, LayoutFilePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            Debug.WriteLine($"Unable to save desktop layout: {exception.Message}");
        }
    }

    private static DesktopLayoutData Load()
    {
        try
        {
            if (!File.Exists(LayoutFilePath))
                return new DesktopLayoutData(CurrentVersion, [], []);

            var data = JsonSerializer.Deserialize(File.ReadAllText(LayoutFilePath), JsonContext.DesktopLayout);
            if (data is null || data.Version is < 1 or > CurrentVersion || data.OrderedIdentities is null)
                return new DesktopLayoutData(CurrentVersion, [], []);

            return new DesktopLayoutData(
                CurrentVersion,
                data.OrderedIdentities
                    .Where(identity => !string.IsNullOrWhiteSpace(identity))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                (data.Positions ?? [])
                    .Where(position => !string.IsNullOrWhiteSpace(position.Identity) &&
                        double.IsFinite(position.X) && double.IsFinite(position.Y))
                    .GroupBy(position => position.Identity, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.Last())
                    .ToList());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            Debug.WriteLine($"Unable to load desktop layout: {exception.Message}");
            return new DesktopLayoutData(CurrentVersion, [], []);
        }
    }

    internal sealed record DesktopLayoutData(int Version, List<string> OrderedIdentities, List<DesktopItemPosition>? Positions);

    internal sealed record DesktopItemPosition(
        string Identity,
        double X,
        double Y,
        double ViewportWidth = 0,
        double ViewportHeight = 0);
}
