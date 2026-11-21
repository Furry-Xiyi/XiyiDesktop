using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace XiyiDesktop.FileOperations;

/// <summary>
/// Tracks Recycle Bin entries created by this app and restores only those exact entries.
/// Windows has no public API for invoking Explorer's global undo transaction, so this
/// service pairs the Recycle Bin metadata/data files with the original paths it observed.
/// </summary>
internal static class DesktopUndoHistory
{
    private const int MaximumUndoBatches = 20;
    private const int MaximumItemsPerUndoBatch = 256;
    private const int MaximumRecycleEntriesToScan = 10000;
    private const int MaximumMetadataBytes = 1024 * 1024;
    private const int MaximumPathLength = 32760;
    private static readonly TimeSpan ValidationLifetime = TimeSpan.FromSeconds(5);
    private static readonly object Sync = new();
    private static readonly SemaphoreSlim RefreshGate = new(1, 1);
    private static readonly string HistoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "XiyiDesktop",
        "undo-history.json");
    private static readonly List<TrackedRecycleItem[]> History = Load();
    private static bool _needsRefresh = true;
    private static DateTime _verifiedAtUtc = DateTime.MinValue;

    public static bool CanUndo
    {
        get
        {
            lock (Sync)
                return IsValidationCurrentLocked() && History.Count > 0;
        }
    }

    public static bool NeedsRefresh
    {
        get
        {
            lock (Sync)
                return !IsValidationCurrentLocked();
        }
    }

    public static string UndoLabel
    {
        get
        {
            lock (Sync)
            {
                if (!IsValidationCurrentLocked() || History.Count == 0)
                    return string.Empty;
                var latest = History[^1];
                var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(latest[^1].OriginalPath));
                return latest.Length == 1 ? name : $"{name} (+{latest.Length - 1})";
            }
        }
    }

    /// <summary>Captures existing metadata identities before a Shell delete is queued.</summary>
    public static RecycleBinSnapshot CaptureSnapshot(IEnumerable<string> paths)
    {
        var entries = new Dictionary<string, SnapshotEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawPath in paths.Take(MaximumItemsPerUndoBatch + 1))
        {
            if (string.IsNullOrWhiteSpace(rawPath) || rawPath.Length > MaximumPathLength || !IsSupportedVolumePath(rawPath))
                continue;

            var originalPath = Path.GetFullPath(rawPath);
            var isDirectory = Directory.Exists(originalPath);
            var (items, complete) = FindRecycleItems(originalPath);
            entries[NormalizePath(originalPath)] = new SnapshotEntry(isDirectory, complete,
                items.Select(item => item.MetadataPath).ToHashSet(StringComparer.OrdinalIgnoreCase));
        }
        return new RecycleBinSnapshot(entries);
    }

    /// <summary>Records only newly-created matching entries whose source disappeared.</summary>
    public static void Record(IEnumerable<string> paths, RecycleBinSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var sourcePaths = paths.Take(MaximumItemsPerUndoBatch + 1)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (sourcePaths.Length == 0 || sourcePaths.Length > MaximumItemsPerUndoBatch)
            return;

        var batch = new List<TrackedRecycleItem>();
        foreach (var rawPath in sourcePaths)
        {
            if (string.IsNullOrWhiteSpace(rawPath) || rawPath.Length > MaximumPathLength ||
                File.Exists(rawPath) || Directory.Exists(rawPath) || !IsSupportedVolumePath(rawPath))
                continue;

            string originalPath;
            try { originalPath = Path.GetFullPath(rawPath); }
            catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException) { continue; }

            var normalized = NormalizePath(originalPath);
            if (!snapshot.Entries.TryGetValue(normalized, out var before) || !before.IsComplete)
                continue;

            var (afterItems, complete) = FindRecycleItems(originalPath);
            if (!complete)
                continue;

            var newItems = afterItems
                .Where(item => !before.MetadataPaths.Contains(item.MetadataPath))
                .OrderByDescending(item => SafeLastWriteTimeUtc(item.MetadataPath))
                .Take(1)
                .ToArray();
            if (newItems.Length == 0)
                continue;

            var item = newItems[0];
            batch.Add(new TrackedRecycleItem(
                originalPath,
                item.RecycledPath,
                item.MetadataPath,
                item.RecycleBinPath,
                before.IsDirectory));
        }

        if (batch.Count == 0)
            return;

        lock (Sync)
        {
            History.Add(batch.ToArray());
            if (History.Count > MaximumUndoBatches)
                History.RemoveAt(0);
            _needsRefresh = true;
            _verifiedAtUtc = DateTime.MinValue;
            Save();
        }
    }

    public static Task<int> UndoLastRecycleAsync(IntPtr ownerWindow = default) =>
        ShellRecycleBinRestore.RunOnStaAsync(() =>
        {
            try { return UndoLastRecycle(ownerWindow); }
            finally
            {
                try { RefreshUndoAvailability(ownerWindow); }
                catch (Exception exception)
                {
                    Debug.WriteLine($"Could not refresh desktop undo availability: {exception.Message}");
                }
            }
        });

    public static async Task RefreshUndoAvailabilityAsync(IntPtr ownerWindow = default)
    {
        await RefreshGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await ShellRecycleBinRestore.RunOnStaAsync(() =>
            {
                RefreshUndoAvailability(ownerWindow);
                return 0;
            }).ConfigureAwait(false);
        }
        finally
        {
            RefreshGate.Release();
        }
    }

    public static void Clear()
    {
        lock (Sync)
        {
            History.Clear();
            _needsRefresh = false;
            _verifiedAtUtc = DateTime.UtcNow;
            Save();
        }
    }

    private static int UndoLastRecycle(IntPtr ownerWindow)
    {
        lock (Sync)
        {
            if (History.Count == 0)
                return 0;

            var batch = History[^1];
            var available = batch
                .Where(IsExactTrackedEntryAvailable)
                .ToArray();

            var uniquePaths = ShellRecycleBinRestore.FindUniqueRestoreCandidates(available, ownerWindow, out var scanComplete);
            if (!scanComplete)
            {
                _needsRefresh = true;
                _verifiedAtUtc = DateTime.MinValue;
                return 0;
            }

            var plan = available.Where(item => uniquePaths.Contains(item.MetadataPath)).ToArray();
            if (plan.Length != batch.Length)
            {
                if (plan.Length == 0)
                    History.RemoveAt(History.Count - 1);
                else
                    History[^1] = plan;
                _needsRefresh = true;
                _verifiedAtUtc = DateTime.MinValue;
                Save();
            }

            if (plan.Length == 0)
                return 0;

            foreach (var item in plan)
            {
                if (File.Exists(item.OriginalPath) || Directory.Exists(item.OriginalPath))
                    throw new IOException($"An item already exists at '{item.OriginalPath}'. Nothing was restored.");
                var parent = Path.GetDirectoryName(item.OriginalPath);
                if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
                    throw new DirectoryNotFoundException(parent);
            }

            var restored = new List<TrackedRecycleItem>();
            var unavailable = new HashSet<TrackedRecycleItem>();
            var failures = new List<string>();
            foreach (var item in plan)
            {
                try
                {
                    switch (ShellRecycleBinRestore.RestoreExactItem(item, ownerWindow))
                    {
                        case ShellRecycleBinRestore.RestoreAttemptResult.Restored:
                            restored.Add(item);
                            break;
                        case ShellRecycleBinRestore.RestoreAttemptResult.NoUniqueCandidate:
                            unavailable.Add(item);
                            break;
                        case ShellRecycleBinRestore.RestoreAttemptResult.Failed:
                            failures.Add($"Could not restore '{item.OriginalPath}' from the Recycle Bin.");
                            break;
                    }
                }
                catch (Exception exception)
                {
                    failures.Add($"Could not restore '{item.OriginalPath}' from the Recycle Bin: {exception.Message}");
                }
            }

            if (restored.Count > 0 || unavailable.Count > 0 || plan.Length != batch.Length)
            {
                var restoredSet = restored.ToHashSet();
                var remaining = plan.Where(item => !restoredSet.Contains(item) && !unavailable.Contains(item)).ToArray();
                if (remaining.Length == 0)
                    History.RemoveAt(History.Count - 1);
                else
                    History[^1] = remaining;
                _needsRefresh = true;
                _verifiedAtUtc = DateTime.MinValue;
                Save();
            }

            if (failures.Count > 0)
            {
                var failureSummary = string.Join(Environment.NewLine, failures);
                if (restored.Count == 0 && unavailable.Count == 0)
                    throw new IOException(failureSummary);

                Debug.WriteLine($"Some Recycle Bin items could not be restored; {restored.Count} item(s) were restored successfully.{Environment.NewLine}{failureSummary}");
            }

            return restored.Count;
        }
    }

    private static void RefreshUndoAvailability(IntPtr ownerWindow)
    {
        TrackedRecycleItem[][] snapshot;
        lock (Sync)
        {
            snapshot = History.ToArray();
            if (snapshot.Length == 0)
            {
                _needsRefresh = false;
                _verifiedAtUtc = DateTime.UtcNow;
                return;
            }
        }

        var allItems = snapshot.SelectMany(batch => batch).Where(IsExactTrackedEntryAvailable).ToArray();
        HashSet<string> uniquePaths;
        bool scanComplete;
        if (allItems.Length == 0)
        {
            uniquePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            scanComplete = true;
        }
        else
        {
            uniquePaths = ShellRecycleBinRestore.FindUniqueRestoreCandidates(allItems, ownerWindow, out scanComplete);
        }
        lock (Sync)
        {
            if (History.Count != snapshot.Length ||
                History.Where((batch, index) => !ReferenceEquals(batch, snapshot[index])).Any())
            {
                _needsRefresh = true;
                _verifiedAtUtc = DateTime.MinValue;
                return;
            }

            if (!scanComplete)
            {
                _needsRefresh = true;
                _verifiedAtUtc = DateTime.MinValue;
                return;
            }

            var changed = false;
            for (var index = History.Count - 1; index >= 0; index--)
            {
                var surviving = History[index]
                    .Where(item => IsExactTrackedEntryAvailable(item) && uniquePaths.Contains(item.MetadataPath))
                    .ToArray();
                if (surviving.Length == 0)
                {
                    History.RemoveAt(index);
                    changed = true;
                }
                else if (surviving.Length != History[index].Length)
                {
                    History[index] = surviving;
                    changed = true;
                }
            }

            if (changed)
                Save();
            _needsRefresh = false;
            _verifiedAtUtc = DateTime.UtcNow;
        }
    }

    private static bool IsValidationCurrentLocked()
    {
        if (!_needsRefresh && DateTime.UtcNow - _verifiedAtUtc > ValidationLifetime)
        {
            _needsRefresh = true;
            _verifiedAtUtc = DateTime.MinValue;
        }
        return !_needsRefresh;
    }

    private static bool IsExactTrackedEntryAvailable(TrackedRecycleItem item)
    {
        if (!File.Exists(item.MetadataPath) || item.MetadataPath.Length > MaximumPathLength ||
            item.RecycledPath.Length > MaximumPathLength ||
            (!File.Exists(item.RecycledPath) && !Directory.Exists(item.RecycledPath)))
        {
            return false;
        }

        if (!TryReadOriginalPath(item.MetadataPath, out var recordedPath) ||
            !string.Equals(NormalizePath(recordedPath), NormalizePath(item.OriginalPath), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!TryGetRecycleBinPath(item.OriginalPath, out var expectedRecycleBin) ||
            !string.Equals(NormalizePath(expectedRecycleBin), NormalizePath(item.RecycleBinPath), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var relativeMetadataPath = Path.GetRelativePath(expectedRecycleBin, item.MetadataPath);
        if (Path.IsPathRooted(relativeMetadataPath) || relativeMetadataPath.StartsWith("..", StringComparison.Ordinal) ||
            relativeMetadataPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length != 2)
        {
            return false;
        }

        var name = Path.GetFileName(item.MetadataPath);
        if (name.Length < 3 || name[0] != '$' || char.ToUpperInvariant(name[1]) != 'I')
            return false;
        var expectedRecycledPath = Path.Combine(Path.GetDirectoryName(item.MetadataPath)!, "$R" + name[2..]);
        return string.Equals(NormalizePath(expectedRecycledPath), NormalizePath(item.RecycledPath), StringComparison.OrdinalIgnoreCase);
    }

    private static (IReadOnlyList<RecycleItem> Items, bool Complete) FindRecycleItems(string originalPath)
    {
        if (!TryGetRecycleBinPath(originalPath, out var recycleBinPath))
            return ([], false);
        if (!Directory.Exists(recycleBinPath))
            return ([], true);

        string userSid;
        try { userSid = WindowsIdentity.GetCurrent().User?.Value ?? string.Empty; }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return ([], false);
        }
        if (string.IsNullOrWhiteSpace(userSid) || userSid.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return ([], false);
        var userDirectories = new[] { Path.Combine(recycleBinPath, userSid) };
        if (!Directory.Exists(userDirectories[0]))
            return ([], true);

        var matches = new List<RecycleItem>();
        var complete = true;
        foreach (var userDirectory in userDirectories)
        {
            string[] metadataFiles;
            try
            {
                metadataFiles = Directory.EnumerateFiles(userDirectory, "$I*", SearchOption.TopDirectoryOnly)
                    .Take(MaximumRecycleEntriesToScan + 1)
                    .ToArray();
                if (metadataFiles.Length > MaximumRecycleEntriesToScan)
                {
                    complete = false;
                    continue;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                complete = false;
                continue;
            }

            foreach (var metadataPath in metadataFiles)
            {
                if (!TryReadOriginalPath(metadataPath, out var recordedPath) ||
                    !string.Equals(NormalizePath(recordedPath), NormalizePath(originalPath), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var name = Path.GetFileName(metadataPath);
                if (name.Length < 3 || name[0] != '$' || char.ToUpperInvariant(name[1]) != 'I')
                    continue;
                var recycledPath = Path.Combine(userDirectory, "$R" + name[2..]);
                if (File.Exists(recycledPath) || Directory.Exists(recycledPath))
                    matches.Add(new RecycleItem(originalPath, recycledPath, metadataPath, recycleBinPath));
            }
        }
        return (matches, complete);
    }

    private static bool TryReadOriginalPath(string metadataPath, out string originalPath)
    {
        originalPath = string.Empty;
        try
        {
            var info = new FileInfo(metadataPath);
            if (info.Length < 20 || info.Length > MaximumMetadataBytes)
                return false;

            var bytes = File.ReadAllBytes(metadataPath);
            var version = BitConverter.ToUInt64(bytes, 0);
            string candidate;
            if (version == 2 && bytes.Length >= 28)
            {
                var charCount = BitConverter.ToInt32(bytes, 24);
                if (charCount <= 0 || charCount > (bytes.Length - 28) / 2)
                    return false;
                candidate = Encoding.Unicode.GetString(bytes, 28, charCount * 2).TrimEnd('\0');
            }
            // Version 1 has three 64-bit fields (version, size, deletion FILETIME)
            // before the UTF-16 original path.
            else if (version == 1 && bytes.Length > 24)
            {
                candidate = Encoding.Unicode.GetString(bytes, 24, bytes.Length - 24).TrimEnd('\0');
            }
            else
            {
                return false;
            }

            if (candidate.Length == 0 || candidate.Length > MaximumPathLength || candidate.IndexOf('\0') >= 0 || !Path.IsPathRooted(candidate))
                return false;
            originalPath = candidate;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static bool TryGetRecycleBinPath(string originalPath, out string recycleBinPath)
    {
        recycleBinPath = string.Empty;
        try
        {
            var root = Path.GetPathRoot(originalPath);
            var fullPath = Path.GetFullPath(originalPath);
            if (string.IsNullOrWhiteSpace(root) || root.Length < 3 || root[1] != ':' ||
                fullPath.Length <= root.Length || fullPath.Length > MaximumPathLength)
                return false;
            recycleBinPath = Path.Combine(root, "$Recycle.Bin");
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsSupportedVolumePath(string path) =>
        !string.IsNullOrWhiteSpace(path) && TryGetRecycleBinPath(path, out _);

    private static string NormalizePath(string path)
    {
        if (path.Length > MaximumPathLength)
            throw new ArgumentException("Path exceeds the supported length.", nameof(path));
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (fullPath.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase))
            return "\\\\" + fullPath[8..];
        if (fullPath.StartsWith("\\\\?\\", StringComparison.OrdinalIgnoreCase))
            return fullPath[4..];
        return fullPath;
    }

    private static DateTime SafeLastWriteTimeUtc(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) { return DateTime.MinValue; }
    }

    private static List<TrackedRecycleItem[]> Load()
    {
        try
        {
            if (!File.Exists(HistoryPath))
                return [];
            var history = JsonSerializer.Deserialize(
                File.ReadAllText(HistoryPath), AppJsonSerializerContext.Default.UndoHistory) ?? [];
            return history
                .Where(batch => batch is { Length: > 0 and <= MaximumItemsPerUndoBatch } && batch.All(IsValidStoredEntry))
                .TakeLast(MaximumUndoBatches)
                .ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            Debug.WriteLine($"Could not load desktop undo history: {exception.Message}");
            return [];
        }
    }

    private static bool IsValidStoredEntry(TrackedRecycleItem item)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.OriginalPath) ||
            string.IsNullOrWhiteSpace(item.RecycledPath) || string.IsNullOrWhiteSpace(item.MetadataPath) ||
            string.IsNullOrWhiteSpace(item.RecycleBinPath) || item.OriginalPath.Length > MaximumPathLength ||
            item.RecycledPath.Length > MaximumPathLength || item.MetadataPath.Length > MaximumPathLength ||
            item.RecycleBinPath.Length > MaximumPathLength || !TryGetRecycleBinPath(item.OriginalPath, out var expectedBin))
        {
            return false;
        }

        try
        {
            return string.Equals(NormalizePath(expectedBin), NormalizePath(item.RecycleBinPath), StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Path.GetDirectoryName(item.MetadataPath), Path.GetDirectoryName(item.RecycledPath), StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(item.MetadataPath).StartsWith("$I", StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(item.RecycledPath).StartsWith("$R", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void Save()
    {
        var temporaryPath = HistoryPath + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(
                History, AppJsonSerializerContext.Default.UndoHistory));
            File.Move(temporaryPath, HistoryPath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            Debug.WriteLine($"Could not save desktop undo history: {exception.Message}");
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public sealed record RecycleBinSnapshot(IReadOnlyDictionary<string, SnapshotEntry> Entries);
    public sealed record SnapshotEntry(bool IsDirectory, bool IsComplete, IReadOnlySet<string> MetadataPaths);
    private sealed record RecycleItem(string OriginalPath, string RecycledPath, string MetadataPath, string RecycleBinPath);
    public sealed record TrackedRecycleItem(string OriginalPath, string RecycledPath, string MetadataPath, string RecycleBinPath, bool IsDirectory);
}
