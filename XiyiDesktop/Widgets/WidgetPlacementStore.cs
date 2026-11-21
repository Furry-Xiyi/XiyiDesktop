using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace XiyiDesktop.Widgets;

internal sealed record WidgetDesktopPlacement(
    string InstanceId,
    string DefinitionId,
    string DisplayName,
    string Size,
    double X,
    double Y,
    string[]? SupportedSizes = null,
    string? LightIconPath = null,
    string? DarkIconPath = null,
    string? DefaultIconPath = null,
    int CoordinateSpaceVersion = 0,
    double ViewportWidth = 0,
    double ViewportHeight = 0);

internal sealed class WidgetPlacementStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "XiyiDesktop",
        "desktop-widgets.json");

    private readonly object _sync = new();
    private List<WidgetDesktopPlacement> _placements = Load();

    public IReadOnlyList<WidgetDesktopPlacement> GetAll()
    {
        lock (_sync)
            return _placements.ToArray();
    }

    public void Upsert(WidgetDesktopPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(placement);
        UpsertRange([placement]);
    }

    public void UpsertRange(IEnumerable<WidgetDesktopPlacement> placements)
    {
        ArgumentNullException.ThrowIfNull(placements);
        var validPlacements = placements
            .Where(placement => !string.IsNullOrWhiteSpace(placement.InstanceId) &&
                !string.IsNullOrWhiteSpace(placement.DefinitionId) &&
                double.IsFinite(placement.X) && double.IsFinite(placement.Y))
            .GroupBy(placement => placement.InstanceId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToArray();
        if (validPlacements.Length == 0)
            return;

        lock (_sync)
        {
            var updatedIds = validPlacements
                .Select(placement => placement.InstanceId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            _placements.RemoveAll(existing => updatedIds.Contains(existing.InstanceId));
            _placements.AddRange(validPlacements);
            SaveLocked();
        }
    }

    public void Remove(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
            return;

        lock (_sync)
        {
            if (_placements.RemoveAll(existing => string.Equals(existing.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase)) > 0)
                SaveLocked();
        }
    }

    private void SaveLocked()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temporaryPath = FilePath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_placements, AppJsonSerializerContext.Default.DesktopWidgets));
            if (File.Exists(FilePath))
                File.Move(temporaryPath, FilePath, overwrite: true);
            else
                File.Move(temporaryPath, FilePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            Debug.WriteLine($"Could not save desktop widgets: {exception.Message}");
        }
    }

    private static List<WidgetDesktopPlacement> Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return [];

            return JsonSerializer.Deserialize(File.ReadAllText(FilePath), AppJsonSerializerContext.Default.DesktopWidgets)?
                .Where(item => !string.IsNullOrWhiteSpace(item.InstanceId) &&
                    !string.IsNullOrWhiteSpace(item.DefinitionId) &&
                    double.IsFinite(item.X) && double.IsFinite(item.Y))
                .GroupBy(item => item.InstanceId, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Last())
                .ToList() ?? [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            Debug.WriteLine($"Could not read desktop widgets: {exception.Message}");
            return [];
        }
    }
}
