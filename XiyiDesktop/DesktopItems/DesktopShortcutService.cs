using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using XiyiDesktop.Interop;

namespace XiyiDesktop.DesktopItems;

/// <summary>Reads and creates Windows shortcuts through native Shell interfaces on an STA thread.</summary>
internal static class DesktopShortcutService
{
    private static readonly Guid ShellLinkClassId = new("00021401-0000-0000-C000-000000000046");
    private static readonly Guid ShellLinkInterfaceId = new("000214F9-0000-0000-C000-000000000046");
    private static readonly Guid PersistFileInterfaceId = new("0000010B-0000-0000-C000-000000000046");

    public static Task<string?> GetTargetPathAsync(string shortcutPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shortcutPath);
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.TrySetResult(GetTargetPath(shortcutPath));
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "XiyiDesktop Shortcut Target Lookup"
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    public static Task CreateAsync(string shortcutPath, string targetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shortcutPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            string? temporaryPath = null;
            try
            {
                var finalPath = Path.GetFullPath(shortcutPath);
                temporaryPath = Path.Combine(
                    Path.GetDirectoryName(finalPath)!,
                    $".{Path.GetFileNameWithoutExtension(finalPath)}.{Guid.NewGuid():N}.tmp.lnk");
                SaveShortcut(temporaryPath, targetPath, Path.GetFileNameWithoutExtension(shortcutPath));
                File.Move(temporaryPath, finalPath);
                completion.TrySetResult(true);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
            finally
            {
                try
                {
                    if (temporaryPath is not null && File.Exists(temporaryPath))
                        File.Delete(temporaryPath);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        })
        {
            IsBackground = true,
            Name = "XiyiDesktop Shortcut Creation"
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static unsafe string? GetTargetPath(string shortcutPath)
    {
        using var apartment = NativeCom.InitializeApartment();
        var link = NativeCom.CreateInstance(ShellLinkClassId, ShellLinkInterfaceId, classContext: 1);
        IntPtr persistFile = IntPtr.Zero;
        try
        {
            persistFile = NativeCom.QueryInterface(link, PersistFileInterfaceId);
            var fullPath = Path.GetFullPath(shortcutPath);
            fixed (char* fileName = fullPath)
            {
                var load = (delegate* unmanaged[Stdcall]<IntPtr, char*, uint, int>)NativeCom.GetMethod(persistFile, 5);
                Marshal.ThrowExceptionForHR(load(persistFile, fileName, 0));
            }

            var buffer = new char[32768];
            fixed (char* path = buffer)
            {
                var getPath = (delegate* unmanaged[Stdcall]<IntPtr, char*, int, IntPtr, uint, int>)NativeCom.GetMethod(link, 3);
                Marshal.ThrowExceptionForHR(getPath(link, path, buffer.Length, IntPtr.Zero, 0));
            }

            var length = Array.IndexOf(buffer, '\0');
            return length == 0 ? null : new string(buffer, 0, length < 0 ? buffer.Length : length);
        }
        finally
        {
            NativeCom.Release(persistFile);
            NativeCom.Release(link);
        }
    }

    private static unsafe void SaveShortcut(string shortcutPath, string targetPath, string description)
    {
        using var apartment = NativeCom.InitializeApartment();
        var link = NativeCom.CreateInstance(ShellLinkClassId, ShellLinkInterfaceId, classContext: 1);
        IntPtr persistFile = IntPtr.Zero;
        try
        {
            // IShellLinkW slots: SetDescription 7, SetWorkingDirectory 9, SetPath 20.
            var workingDirectory = Path.GetDirectoryName(targetPath) ?? string.Empty;
            fixed (char* target = targetPath)
            fixed (char* text = description)
            fixed (char* directory = workingDirectory)
            {
                var setPath = (delegate* unmanaged[Stdcall]<IntPtr, char*, int>)NativeCom.GetMethod(link, 20);
                Marshal.ThrowExceptionForHR(setPath(link, target));
                var setDescription = (delegate* unmanaged[Stdcall]<IntPtr, char*, int>)NativeCom.GetMethod(link, 7);
                Marshal.ThrowExceptionForHR(setDescription(link, text));
                var setDirectory = (delegate* unmanaged[Stdcall]<IntPtr, char*, int>)NativeCom.GetMethod(link, 9);
                Marshal.ThrowExceptionForHR(setDirectory(link, directory));
            }

            persistFile = NativeCom.QueryInterface(link, PersistFileInterfaceId);
            fixed (char* fileName = shortcutPath)
            {
                var save = (delegate* unmanaged[Stdcall]<IntPtr, char*, int, int>)NativeCom.GetMethod(persistFile, 6);
                Marshal.ThrowExceptionForHR(save(persistFile, fileName, 1));
            }
        }
        finally
        {
            NativeCom.Release(persistFile);
            NativeCom.Release(link);
        }
    }
}
