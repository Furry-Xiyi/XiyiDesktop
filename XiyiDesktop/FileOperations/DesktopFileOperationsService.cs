using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using XiyiDesktop.Interop;
using Windows.ApplicationModel.DataTransfer;

namespace XiyiDesktop.FileOperations;

public enum DesktopClipboardAction
{
    None,
    Copy,
    Move
}

public enum DesktopFileOperationKind
{
    Copy,
    Move,
    Recycle,
    Rename,
    Create
}

public enum DesktopFileOperationStatus
{
    Completed,
    Canceled,
    NoClipboardItems
}

public readonly record struct DesktopClipboardState(bool HasStorageItems, DesktopClipboardAction Action);

public readonly record struct DesktopClipboardResult(DesktopClipboardAction Action, int ItemCount);

public readonly record struct DesktopFileOperationResult(
    DesktopFileOperationKind Kind,
    DesktopFileOperationStatus Status,
    int ItemCount);

/// <summary>
/// Performs desktop file operations through Windows Shell so its progress, conflict,
/// permission, undo, and recycle-bin behavior remains available to the user.
/// Clipboard calls are intended to be made from the UI thread.
/// </summary>
public static class DesktopFileOperationsService
{
    private const uint FofAllowUndo = 0x00000040;
    private const uint FofxRecycleOnDelete = 0x00080000;
    private const uint CfHDrop = 15;
    private const uint DropEffectCopy = 1;
    private const uint DropEffectMove = 2;
    private const uint GlobalMoveable = 0x0002;
    private const uint GlobalZeroInit = 0x0040;
    private const int ClipboardOpenRetryCount = 5;
    private const int ClipboardOpenRetryDelayMilliseconds = 10;
    private static readonly Guid FileOperationClassId = new("3AD05575-8857-4850-9277-11B85BDB8E09");
    private static readonly Guid FileOperationInterfaceId = new("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8");
    private static readonly Guid ShellItemInterfaceId = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");

    /// <summary>Whether the most recent app-managed delete can be restored.</summary>
    public static bool CanUndoDelete => DesktopUndoHistory.CanUndo;

    /// <summary>Short label used by the desktop context menu for the pending operation.</summary>
    public static string UndoDeleteLabel => DesktopUndoHistory.UndoLabel;

    /// <summary>Whether the cached desktop undo state needs a Shell refresh.</summary>
    public static bool NeedsRefresh => DesktopUndoHistory.NeedsRefresh;

    /// <summary>Refreshes the undo state against the current Windows Recycle Bin.</summary>
    public static Task RefreshUndoAvailabilityAsync(IntPtr ownerWindow = default) =>
        DesktopUndoHistory.RefreshUndoAvailabilityAsync(ownerWindow);

    /// <summary>Restores the most recently deleted app-managed desktop items.</summary>
    public static Task<int> UndoLastRecycleAsync(IntPtr ownerWindow = default) =>
        DesktopUndoHistory.UndoLastRecycleAsync(ownerWindow);

    /// <summary>Forgets tracked deletes after the user empties the Recycle Bin.</summary>
    public static void ClearUndoHistory() => DesktopUndoHistory.Clear();

    /// <summary>Returns the current Windows clipboard's file availability and requested action.</summary>
    public static DesktopClipboardState GetClipboardState() => GetClipboardState(out _);

    /// <summary>Returns clipboard file availability and paths from the same CF_HDROP snapshot.</summary>
    public static DesktopClipboardState GetClipboardState(out IReadOnlyList<string> filePaths)
    {
        var clipboard = ReadClipboardSnapshot(IntPtr.Zero);
        filePaths = clipboard.Paths;
        if (clipboard.Paths.Length != 0)
            return new DesktopClipboardState(true, clipboard.Action);

        try
        {
            var view = Clipboard.GetContent();
            if (!view.Contains(StandardDataFormats.StorageItems))
                return new DesktopClipboardState(false, DesktopClipboardAction.None);

            var action = view.RequestedOperation switch
            {
                DataPackageOperation.Move => DesktopClipboardAction.Move,
                DataPackageOperation.Copy => DesktopClipboardAction.Copy,
                _ => DesktopClipboardAction.None
            };
            return new DesktopClipboardState(true, action);
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            return new DesktopClipboardState(false, DesktopClipboardAction.None);
        }
    }

    /// <summary>Reads filesystem paths from CF_HDROP on the Windows clipboard.</summary>
    public static IReadOnlyList<string> GetClipboardFilePaths() => ReadClipboardSnapshot(IntPtr.Zero).Paths;

    /// <summary>Places files or folders on the Windows clipboard for copy or cut.</summary>
    public static Task<DesktopClipboardResult> CopyAsync(
        IEnumerable<string> sourcePaths,
        bool cut,
        IntPtr ownerWindow = default)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);
        var paths = ValidateClipboardPaths(sourcePaths);
        var preferredDropEffectFormat = GetPreferredDropEffectFormat();
        var dropFiles = IntPtr.Zero;
        var dropEffect = IntPtr.Zero;
        var clipboardOpened = false;
        var dropFilesTransferred = false;
        var dropEffectTransferred = false;

        try
        {
            dropFiles = CreateDropFilesHandle(paths);
            dropEffect = CreateDropEffectHandle(cut ? DropEffectMove : DropEffectCopy);
            clipboardOpened = TryOpenClipboard(ownerWindow != IntPtr.Zero ? ownerWindow : GetDesktopWindow());
            if (!clipboardOpened)
                throw new IOException("The Windows clipboard is in use by another application.");

            if (!EmptyClipboard())
                throw new IOException("Could not clear the Windows clipboard.");

            if (SetClipboardData(preferredDropEffectFormat, dropEffect) == IntPtr.Zero)
                throw new IOException("Could not set the clipboard operation type.");
            dropEffectTransferred = true;
            dropEffect = IntPtr.Zero;

            if (SetClipboardData(CfHDrop, dropFiles) == IntPtr.Zero)
                throw new IOException("Could not set the clipboard file list.");
            dropFilesTransferred = true;
            dropFiles = IntPtr.Zero;
        }
        catch
        {
            // If only the effect was published, avoid leaving a clipboard that looks like a file operation.
            if (clipboardOpened && dropEffectTransferred && !dropFilesTransferred)
                _ = EmptyClipboard();
            throw;
        }
        finally
        {
            if (clipboardOpened)
                _ = CloseClipboard();
            if (!dropFilesTransferred && dropFiles != IntPtr.Zero)
                _ = GlobalFree(dropFiles);
            if (!dropEffectTransferred && dropEffect != IntPtr.Zero)
                _ = GlobalFree(dropEffect);
        }

        return Task.FromResult(new DesktopClipboardResult(cut ? DesktopClipboardAction.Move : DesktopClipboardAction.Copy, paths.Length));
    }

    /// <summary>
    /// Pastes clipboard files or folders into a filesystem destination using Windows Shell.
    /// Returns NoClipboardItems when the clipboard has no compatible storage items.
    /// </summary>
    public static async Task<DesktopFileOperationResult> PasteAsync(string destinationFolder, IntPtr ownerWindow = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationFolder);
        var destination = Path.GetFullPath(destinationFolder);
        if (!Directory.Exists(destination))
            throw new DirectoryNotFoundException(destination);

        var clipboard = ReadClipboardSnapshot(ownerWindow);
        var paths = clipboard.Paths;
        if (paths.Length == 0)
        {
            var clipboardSequence = GetClipboardSequenceNumber();
            var view = Clipboard.GetContent();
            if (!view.Contains(StandardDataFormats.StorageItems))
                return new DesktopFileOperationResult(DesktopFileOperationKind.Copy, DesktopFileOperationStatus.NoClipboardItems, 0);

            var storageItems = await view.GetStorageItemsAsync();
            if (storageItems.Count == 0 || GetClipboardSequenceNumber() != clipboardSequence)
                return new DesktopFileOperationResult(DesktopFileOperationKind.Copy, DesktopFileOperationStatus.NoClipboardItems, 0);

            paths = storageItems.Select(item => item.Path).Where(path => !string.IsNullOrWhiteSpace(path)).ToArray();
            if (paths.Length != storageItems.Count)
                throw new NotSupportedException();

            var action = view.RequestedOperation == DataPackageOperation.Move
                ? DesktopClipboardAction.Move
                : DesktopClipboardAction.Copy;
            clipboard = new ClipboardSnapshot(paths, action, clipboardSequence);
        }

        var isMove = clipboard.Action == DesktopClipboardAction.Move;
        var kind = isMove ? DesktopFileOperationKind.Move : DesktopFileOperationKind.Copy;
        var status = await RunOnShellStaAsync(() =>
        {
            using var operation = new FileOperationScope(ownerWindow);
            var destinationItem = CreateShellItem(destination);
            try
            {
                foreach (var path in paths)
                {
                    var sourceItem = CreateShellItem(path);
                    try
                    {
                        if (isMove)
                            operation.MoveItem(sourceItem, destinationItem, null, IntPtr.Zero);
                        else
                            operation.CopyItem(sourceItem, destinationItem, null, IntPtr.Zero);
                    }
                    finally
                    {
                        NativeCom.Release(sourceItem);
                    }
                }

                return operation.Perform();
            }
            finally
            {
                NativeCom.Release(destinationItem);
            }
        });

        if (isMove && status == DesktopFileOperationStatus.Completed)
            ClearClipboardIfUnchanged(clipboard.SequenceNumber, ownerWindow != IntPtr.Zero ? ownerWindow : GetDesktopWindow());

        return new DesktopFileOperationResult(kind, status, paths.Length);
    }

    /// <summary>Copies or moves filesystem entries directly to a Send To folder destination.</summary>
    public static async Task<DesktopFileOperationResult> CopyToAsync(
        IEnumerable<string> sourcePaths,
        string destinationFolder,
        bool cut = false,
        IntPtr ownerWindow = default)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationFolder);
        var paths = ValidateFileSystemPaths(sourcePaths);
        var destination = Path.GetFullPath(destinationFolder);
        if (!Directory.Exists(destination))
            throw new DirectoryNotFoundException(destination);

        var status = await RunOnShellStaAsync(() =>
        {
            using var operation = new FileOperationScope(ownerWindow, FofAllowUndo);
            var destinationItem = CreateShellItem(destination);
            try
            {
                foreach (var path in paths)
                {
                    var sourceItem = CreateShellItem(path);
                    try
                    {
                        if (cut)
                            operation.MoveItem(sourceItem, destinationItem, null, IntPtr.Zero);
                        else
                            operation.CopyItem(sourceItem, destinationItem, null, IntPtr.Zero);
                    }
                    finally
                    {
                        NativeCom.Release(sourceItem);
                    }
                }

                return operation.Perform();
            }
            finally
            {
                NativeCom.Release(destinationItem);
            }
        });

        return new DesktopFileOperationResult(
            cut ? DesktopFileOperationKind.Move : DesktopFileOperationKind.Copy,
            status,
            paths.Length);
    }

    /// <summary>Moves filesystem items to the Windows Recycle Bin with the standard Shell progress UI.</summary>
    public static async Task<DesktopFileOperationResult> RecycleAsync(IEnumerable<string> sourcePaths, IntPtr ownerWindow = default)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);
        var paths = ValidateFileSystemPaths(sourcePaths);
        var recycleSnapshot = DesktopUndoHistory.CaptureSnapshot(paths);

        var status = await RunOnShellStaAsync(() =>
        {
            using var operation = new FileOperationScope(ownerWindow, FofAllowUndo | FofxRecycleOnDelete);
            foreach (var path in paths)
            {
                var item = CreateShellItem(path);
                try
                {
                    operation.DeleteItem(item, IntPtr.Zero);
                }
                finally
                {
                    NativeCom.Release(item);
                }
            }

            return operation.Perform();
        });
        DesktopUndoHistory.Record(paths, recycleSnapshot);
        return new DesktopFileOperationResult(DesktopFileOperationKind.Recycle, status, paths.Length);
    }

    /// <summary>Renames one file or folder through Windows Shell.</summary>
    public static async Task<DesktopFileOperationResult> RenameAsync(string sourcePath, string newName, IntPtr ownerWindow = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        if (!string.Equals(newName, Path.GetFileName(newName), StringComparison.Ordinal) ||
            newName is "." or ".." || newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException(null, nameof(newName));
        }

        var path = Path.GetFullPath(sourcePath);
        if (!File.Exists(path) && !Directory.Exists(path))
            throw new FileNotFoundException(null, path);

        var status = await RunOnShellStaAsync(() =>
        {
            using var operation = new FileOperationScope(ownerWindow);
            var item = CreateShellItem(path);
            try
            {
                operation.RenameItem(item, newName, IntPtr.Zero);
                return operation.Perform();
            }
            finally
            {
                NativeCom.Release(item);
            }
        });
        return new DesktopFileOperationResult(DesktopFileOperationKind.Rename, status, 1);
    }

    /// <summary>Renames a well-known Shell namespace item such as This PC or Recycle Bin.</summary>
    public static async Task<DesktopFileOperationResult> RenameShellItemAsync(string parsingName, string newName, IntPtr ownerWindow = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parsingName);
        ValidateNewName(newName);

        var status = await RunOnShellStaAsync(() =>
        {
            using var operation = new FileOperationScope(ownerWindow);
            var item = CreateShellItem(parsingName);
            try
            {
                operation.RenameItem(item, newName, IntPtr.Zero);
                return operation.Perform();
            }
            finally
            {
                NativeCom.Release(item);
            }
        });
        return new DesktopFileOperationResult(DesktopFileOperationKind.Rename, status, 1);
    }

    private static void ValidateNewName(string newName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        if (!string.Equals(newName, Path.GetFileName(newName), StringComparison.Ordinal) ||
            newName is "." or ".." || newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException(null, nameof(newName));
        }
    }

    /// <summary>Creates a new item in a folder through the Windows Shell file-operation service.</summary>
    public static async Task<DesktopFileOperationResult> CreateNewItemAsync(
        string destinationFolder,
        string name,
        bool isDirectory,
        IntPtr ownerWindow = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!string.Equals(name, Path.GetFileName(name), StringComparison.Ordinal) ||
            name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException(null, nameof(name));
        }

        var destinationPath = Path.GetFullPath(destinationFolder);
        if (!Directory.Exists(destinationPath))
            throw new DirectoryNotFoundException(destinationPath);

        const uint FileAttributeDirectory = 0x00000010;
        const uint FileAttributeNormal = 0x00000080;
        var attributes = isDirectory ? FileAttributeDirectory : FileAttributeNormal;
        var status = await RunOnShellStaAsync(() =>
        {
            using var operation = new FileOperationScope(ownerWindow);
            var destination = CreateShellItem(destinationPath);
            try
            {
                operation.NewItem(destination, attributes, name, null, IntPtr.Zero);
                return operation.Perform();
            }
            finally
            {
                NativeCom.Release(destination);
            }
        });

        return new DesktopFileOperationResult(DesktopFileOperationKind.Create, status, 1);
    }

    private static string[] ValidateFileSystemPaths(IEnumerable<string> sourcePaths)
    {
        var paths = sourcePaths
            .Select(path => string.IsNullOrWhiteSpace(path) ? throw new ArgumentException(null, nameof(sourcePaths)) : Path.GetFullPath(path))
            .ToArray();

        if (paths.Length == 0)
            throw new ArgumentException(null, nameof(sourcePaths));

        foreach (var path in paths)
        {
            if (!File.Exists(path) && !Directory.Exists(path))
                throw new FileNotFoundException(null, path);
        }

        return paths;
    }

    private static string[] ValidateClipboardPaths(IEnumerable<string> sourcePaths)
    {
        var paths = sourcePaths.Select(NormalizeClipboardPath).ToArray();

        if (paths.Length == 0)
            throw new ArgumentException(null, nameof(sourcePaths));

        return paths;
    }

    private static string NormalizeClipboardPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.GetFullPath(path);
    }

    private static Task<DesktopFileOperationStatus> RunOnShellStaAsync(Func<DesktopFileOperationStatus> action)
    {
        var completion = new TaskCompletionSource<DesktopFileOperationStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var apartment = NativeCom.InitializeApartment();
                completion.SetResult(action());
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "XiyiDesktop Shell file operation"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static ClipboardSnapshot ReadClipboardSnapshot(IntPtr ownerWindow)
    {
        if (!TryOpenClipboard(ownerWindow))
            return ClipboardSnapshot.Empty;

        try
        {
            var sequenceNumber = GetClipboardSequenceNumber();
            var dropFiles = GetClipboardData(CfHDrop);
            if (dropFiles == IntPtr.Zero)
                return ClipboardSnapshot.Empty with { SequenceNumber = sequenceNumber };

            var paths = ReadDropFiles(dropFiles);
            if (paths.Length == 0)
                return ClipboardSnapshot.Empty with { SequenceNumber = sequenceNumber };

            var effect = ReadDropEffect(GetClipboardData(GetPreferredDropEffectFormat()));
            var action = (effect & DropEffectMove) != 0
                ? DesktopClipboardAction.Move
                : DesktopClipboardAction.Copy;
            return new ClipboardSnapshot(paths, action, sequenceNumber);
        }
        finally
        {
            _ = CloseClipboard();
        }
    }

    private static string[] ReadDropFiles(IntPtr dropFiles)
    {
        var fileCount = DragQueryFile(dropFiles, uint.MaxValue, null, 0);
        if (fileCount == 0)
            return [];

        var paths = new List<string>(checked((int)fileCount));
        for (uint index = 0; index < fileCount; index++)
        {
            var length = DragQueryFile(dropFiles, index, null, 0);
            if (length == 0)
                continue;

            var buffer = new StringBuilder(checked((int)length + 1));
            if (DragQueryFile(dropFiles, index, buffer, checked(length + 1)) == 0)
                continue;

            var path = buffer.ToString();
            if (path.Length != 0)
                paths.Add(path);
        }

        return paths.ToArray();
    }

    private static uint ReadDropEffect(IntPtr dropEffect)
    {
        if (dropEffect == IntPtr.Zero || GlobalSize(dropEffect) < (nuint)sizeof(uint))
            return DropEffectCopy;

        var data = GlobalLock(dropEffect);
        if (data == IntPtr.Zero)
            return DropEffectCopy;

        try
        {
            return unchecked((uint)Marshal.ReadInt32(data));
        }
        finally
        {
            _ = GlobalUnlock(dropEffect);
        }
    }

    private static IntPtr CreateDropFilesHandle(string[] paths)
    {
        var pathBytes = Encoding.Unicode.GetBytes(string.Join('\0', paths) + "\0\0");
        var headerSize = Marshal.SizeOf<DropFiles>();
        var handle = GlobalAlloc(
            GlobalMoveable | GlobalZeroInit,
            checked((nuint)headerSize + (nuint)pathBytes.Length));
        if (handle == IntPtr.Zero)
            throw new OutOfMemoryException("Could not allocate clipboard file data.");

        var data = GlobalLock(handle);
        if (data == IntPtr.Zero)
        {
            _ = GlobalFree(handle);
            throw new IOException("Could not lock clipboard file data.");
        }

        try
        {
            Marshal.StructureToPtr(new DropFiles
            {
                FileOffset = checked((uint)headerSize),
                Wide = 1
            }, data, fDeleteOld: false);
            Marshal.Copy(pathBytes, 0, IntPtr.Add(data, headerSize), pathBytes.Length);
        }
        catch
        {
            _ = GlobalUnlock(handle);
            _ = GlobalFree(handle);
            throw;
        }

        _ = GlobalUnlock(handle);
        return handle;
    }

    private static IntPtr CreateDropEffectHandle(uint effect)
    {
        var handle = GlobalAlloc(GlobalMoveable | GlobalZeroInit, sizeof(uint));
        if (handle == IntPtr.Zero)
            throw new OutOfMemoryException("Could not allocate clipboard operation data.");

        var data = GlobalLock(handle);
        if (data == IntPtr.Zero)
        {
            _ = GlobalFree(handle);
            throw new IOException("Could not lock clipboard operation data.");
        }

        try
        {
            Marshal.WriteInt32(data, unchecked((int)effect));
        }
        catch
        {
            _ = GlobalUnlock(handle);
            _ = GlobalFree(handle);
            throw;
        }

        _ = GlobalUnlock(handle);
        return handle;
    }

    private static void ClearClipboardIfUnchanged(uint expectedSequenceNumber, IntPtr ownerWindow)
    {
        if (GetClipboardSequenceNumber() != expectedSequenceNumber || !TryOpenClipboard(ownerWindow))
            return;

        try
        {
            if (GetClipboardSequenceNumber() == expectedSequenceNumber)
                _ = EmptyClipboard();
        }
        finally
        {
            _ = CloseClipboard();
        }
    }

    private static bool TryOpenClipboard(IntPtr ownerWindow)
    {
        for (var attempt = 0; attempt < ClipboardOpenRetryCount; attempt++)
        {
            if (OpenClipboard(ownerWindow))
                return true;

            if (attempt + 1 < ClipboardOpenRetryCount)
                Thread.Sleep(ClipboardOpenRetryDelayMilliseconds);
        }

        return false;
    }

    private static uint GetPreferredDropEffectFormat()
    {
        var format = RegisterClipboardFormat("Preferred DropEffect");
        if (format == 0)
            throw new IOException("Could not register the clipboard operation format.");
        return format;
    }

    private static IntPtr CreateShellItem(string path)
    {
        var iid = ShellItemInterfaceId;
        var hresult = SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var item);
        if (hresult < 0)
        {
            NativeCom.Release(item);
            Marshal.ThrowExceptionForHR(hresult);
        }
        if (item == IntPtr.Zero)
            throw new COMException("The Shell returned an empty item interface.");
        return item;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DropFiles
    {
        public uint FileOffset;
        public int X;
        public int Y;
        public int NonClient;
        public int Wide;
    }

    private readonly record struct ClipboardSnapshot(string[] Paths, DesktopClipboardAction Action, uint SequenceNumber)
    {
        public static ClipboardSnapshot Empty { get; } = new([], DesktopClipboardAction.None, 0);
    }

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr ownerWindow);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr memory);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterClipboardFormatW", ExactSpelling = true, SetLastError = true)]
    private static extern uint RegisterClipboardFormat(string format);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr GetDesktopWindow();

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, nuint bytes);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr memory);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr memory);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern nuint GlobalSize(IntPtr memory);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "DragQueryFileW", ExactSpelling = true)]
    private static extern uint DragQueryFile(IntPtr drop, uint file, StringBuilder? fileName, uint characterCount);

    private sealed class FileOperationScope : IDisposable
    {
        public FileOperationScope(IntPtr ownerWindow, uint flags = FofAllowUndo)
        {
            Instance = NativeCom.CreateInstance(FileOperationClassId, FileOperationInterfaceId);
            try
            {
                FileOperationInterop.SetOwnerWindow(Instance, ownerWindow);
                FileOperationInterop.SetOperationFlags(Instance, flags);
            }
            catch
            {
                NativeCom.Release(Instance);
                Instance = IntPtr.Zero;
                throw;
            }
        }

        private IntPtr Instance { get; set; }

        public void MoveItem(IntPtr item, IntPtr destinationFolder, string? newName, IntPtr progressSink) =>
            FileOperationInterop.MoveItem(Instance, item, destinationFolder, newName, progressSink);

        public void CopyItem(IntPtr item, IntPtr destinationFolder, string? newName, IntPtr progressSink) =>
            FileOperationInterop.CopyItem(Instance, item, destinationFolder, newName, progressSink);

        public void DeleteItem(IntPtr item, IntPtr progressSink) =>
            FileOperationInterop.DeleteItem(Instance, item, progressSink);

        public void RenameItem(IntPtr item, string newName, IntPtr progressSink) =>
            FileOperationInterop.RenameItem(Instance, item, newName, progressSink);

        public void NewItem(IntPtr destinationFolder, uint attributes, string name, string? templateName, IntPtr progressSink) =>
            FileOperationInterop.NewItem(Instance, destinationFolder, attributes, name, templateName, progressSink);

        public DesktopFileOperationStatus Perform()
        {
            FileOperationInterop.PerformOperations(Instance);
            var aborted = FileOperationInterop.GetAnyOperationsAborted(Instance);
            return aborted ? DesktopFileOperationStatus.Canceled : DesktopFileOperationStatus.Completed;
        }

        public void Dispose()
        {
            NativeCom.Release(Instance);
            Instance = IntPtr.Zero;
        }
    }

    // IFileOperation vtable slots follow ShObjIdl_core.h, including the IUnknown prefix.
    private static class FileOperationInterop
    {
        private static void ThrowIfFailed(int hresult) => Marshal.ThrowExceptionForHR(hresult);

        public static unsafe void SetOperationFlags(IntPtr instance, uint flags)
        {
            var method = (delegate* unmanaged[Stdcall]<IntPtr, uint, int>)NativeCom.GetMethod(instance, 5);
            ThrowIfFailed(method(instance, flags));
        }

        public static unsafe void SetOwnerWindow(IntPtr instance, IntPtr ownerWindow)
        {
            var method = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)NativeCom.GetMethod(instance, 9);
            ThrowIfFailed(method(instance, ownerWindow));
        }

        public static unsafe void RenameItem(IntPtr instance, IntPtr item, string newName, IntPtr progressSink)
        {
            fixed (char* name = newName)
            {
                var method = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, char*, IntPtr, int>)NativeCom.GetMethod(instance, 12);
                ThrowIfFailed(method(instance, item, name, progressSink));
            }
        }

        public static unsafe void MoveItem(IntPtr instance, IntPtr item, IntPtr destinationFolder, string? newName, IntPtr progressSink)
        {
            fixed (char* name = newName)
            {
                var method = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, char*, IntPtr, int>)NativeCom.GetMethod(instance, 14);
                ThrowIfFailed(method(instance, item, destinationFolder, name, progressSink));
            }
        }

        public static unsafe void CopyItem(IntPtr instance, IntPtr item, IntPtr destinationFolder, string? newName, IntPtr progressSink)
        {
            fixed (char* name = newName)
            {
                var method = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, char*, IntPtr, int>)NativeCom.GetMethod(instance, 16);
                ThrowIfFailed(method(instance, item, destinationFolder, name, progressSink));
            }
        }

        public static unsafe void DeleteItem(IntPtr instance, IntPtr item, IntPtr progressSink)
        {
            var method = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, int>)NativeCom.GetMethod(instance, 18);
            ThrowIfFailed(method(instance, item, progressSink));
        }

        public static unsafe void NewItem(IntPtr instance, IntPtr destinationFolder, uint attributes, string name, string? templateName, IntPtr progressSink)
        {
            fixed (char* itemName = name)
            fixed (char* template = templateName)
            {
                var method = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, char*, char*, IntPtr, int>)NativeCom.GetMethod(instance, 20);
                ThrowIfFailed(method(instance, destinationFolder, attributes, itemName, template, progressSink));
            }
        }

        public static unsafe void PerformOperations(IntPtr instance)
        {
            var method = (delegate* unmanaged[Stdcall]<IntPtr, int>)NativeCom.GetMethod(instance, 21);
            ThrowIfFailed(method(instance));
        }

        public static unsafe bool GetAnyOperationsAborted(IntPtr instance)
        {
            int aborted = 0;
            var method = (delegate* unmanaged[Stdcall]<IntPtr, int*, int>)NativeCom.GetMethod(instance, 22);
            ThrowIfFailed(method(instance, &aborted));
            return aborted != 0;
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string path,
        IntPtr bindContext,
        ref Guid interfaceId,
        out IntPtr shellItem);
}
