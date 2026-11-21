using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using XiyiDesktop.Interop;

namespace XiyiDesktop.ApplicationActivation;

/// <summary>Opens shell targets and activates packaged Windows applications.</summary>
public static class ShellActivationService
{
    private const int ShowNormal = 1;
    private const int ErrorCancelled = 1223;
    private const uint SeeMaskInvokeIdList = 0x0000000C;

    private static readonly Guid ApplicationActivationManagerClsid = new("45BA127D-10A8-46EA-8AB7-56EA9078943C");
    private static readonly Guid ApplicationActivationManagerIid = new("2E941141-7F97-4756-BA1D-9DECDE894A3D");

    /// <summary>Opens a file, folder, URL, or shell namespace target with its registered handler.</summary>
    public static void Open(string target, IntPtr ownerWindow = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ExecuteShellVerb(target, "open", ownerWindow);
    }

    /// <summary>Opens a Send To shortcut or destination handler with selected paths as arguments.</summary>
    public static void OpenWithArguments(string target, string arguments, IntPtr ownerWindow = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ArgumentNullException.ThrowIfNull(arguments);
        ExecuteShellVerb(target, "open", ownerWindow, arguments);
    }

    /// <summary>Opens the This PC namespace location.</summary>
    public static void OpenThisPc(IntPtr ownerWindow = default) =>
        ExecuteShellVerb(GetExplorerPath(), "open", ownerWindow, "shell:MyComputerFolder");

    /// <summary>Opens the Recycle Bin namespace location.</summary>
    public static void OpenRecycleBin(IntPtr ownerWindow = default) =>
        ExecuteShellVerb(GetExplorerPath(), "open", ownerWindow, "shell:RecycleBinFolder");

    /// <summary>Empties the Recycle Bin with the standard Shell confirmation and progress UI.</summary>
    /// <returns>False when the user cancels the confirmation dialog; otherwise true.</returns>
    public static Task<bool> EmptyRecycleBinAsync(IntPtr ownerWindow = default)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var apartment = NativeCom.InitializeApartment();
                // Zero flags preserve the Shell's normal confirmation, progress, and sound behavior.
                var result = SHEmptyRecycleBin(ownerWindow, null, flags: 0);
                if (result == 0)
                {
                    completion.TrySetResult(true);
                    return;
                }

                if (result == unchecked((int)0x80004004) || result == unchecked((int)0x800704C7))
                {
                    completion.TrySetResult(false);
                    return;
                }

                Marshal.ThrowExceptionForHR(result);
                completion.TrySetResult(true);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "XiyiDesktop Recycle Bin operation"
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    /// <summary>Opens Computer Management for the This PC context menu.</summary>
    public static void OpenComputerManagement(IntPtr ownerWindow = default) =>
        ExecuteShellVerb("compmgmt.msc", "open", ownerWindow);

    /// <summary>Reads a Shell namespace item's localized display name.</summary>
    public static string? GetShellDisplayName(string parsingName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parsingName);
        string? displayName = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            IntPtr itemIdList = IntPtr.Zero;
            IntPtr namePointer = IntPtr.Zero;
            NativeCom.ApartmentScope? apartment = null;
            try
            {
                apartment = NativeCom.InitializeApartment();
                Marshal.ThrowExceptionForHR(SHParseDisplayName(parsingName, IntPtr.Zero, out itemIdList, 0, out _));
                Marshal.ThrowExceptionForHR(SHGetNameFromIDList(itemIdList, 0, out namePointer));
                displayName = Marshal.PtrToStringUni(namePointer);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                if (namePointer != IntPtr.Zero)
                    Marshal.FreeCoTaskMem(namePointer);
                if (itemIdList != IntPtr.Zero)
                    Marshal.FreeCoTaskMem(itemIdList);
                apartment?.Dispose();
            }
        })
        {
            IsBackground = true,
            Name = "XiyiDesktop Shell display-name lookup"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            System.Diagnostics.Debug.WriteLine($"Could not read Shell display name for '{parsingName}': {failure.Message}");
        return displayName;
    }

    /// <summary>Opens the Windows Shell properties dialog for a filesystem or namespace target.</summary>
    public static void ShowProperties(string target, IntPtr ownerWindow = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ExecuteShellItemVerb(target, "properties", ownerWindow);
    }

    /// <summary>Shows properties for a Shell namespace parsing name such as the Recycle Bin.</summary>
    public static void ShowShellItemProperties(string parsingName, IntPtr ownerWindow = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parsingName);
        ExecuteShellItemVerb(parsingName, "properties", ownerWindow);
    }

    /// <summary>Invokes a registered Windows Shell verb for a filesystem target.</summary>
    public static void InvokeVerb(string target, string verb, IntPtr ownerWindow = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(verb);
        ExecuteShellVerb(target, verb, ownerWindow, mask: SeeMaskInvokeIdList);
    }

    public static void SendTo(string target, IntPtr ownerWindow = default) => InvokeVerb(target, "sendto", ownerWindow);

    public static void Print(string target, IntPtr ownerWindow = default) => InvokeVerb(target, "print", ownerWindow);

    public static void Share(string target, IntPtr ownerWindow = default) => InvokeVerb(target, "share", ownerWindow);

    public static void RunAsAdministrator(string target, IntPtr ownerWindow = default) => InvokeVerb(target, "runas", ownerWindow);

    public static void OpenContainingFolder(string target, IntPtr ownerWindow = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ExecuteShellVerb("explorer.exe", "open", ownerWindow, $"/select,\"{target}\"");
    }

    public static void RotateImage(string target, bool clockwise, IntPtr ownerWindow = default) =>
        InvokeVerb(target, clockwise ? "rotateright" : "rotateleft", ownerWindow);

    /// <summary>Activates an installed packaged app by its Application User Model ID.</summary>
    /// <returns>The process identifier reported by Windows.</returns>
    public static unsafe uint ActivatePackagedApplication(string applicationUserModelId, string? arguments = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationUserModelId);
        using var apartment = NativeCom.InitializeApartment(allowDifferentApartment: true);
        var manager = NativeCom.CreateInstance(ApplicationActivationManagerClsid, ApplicationActivationManagerIid);
        var applicationId = IntPtr.Zero;
        var argumentString = IntPtr.Zero;

        try
        {
            applicationId = Marshal.StringToCoTaskMemUni(applicationUserModelId);
            argumentString = arguments is null ? IntPtr.Zero : Marshal.StringToCoTaskMemUni(arguments);
            uint processId = 0;
            var activate = (delegate* unmanaged[Stdcall]<IntPtr, char*, char*, uint, uint*, int>)NativeCom.GetMethod(manager, 3);
            var result = activate(manager, (char*)applicationId, (char*)argumentString, 0, &processId);
            Marshal.ThrowExceptionForHR(result);
            return processId;
        }
        finally
        {
            if (argumentString != IntPtr.Zero)
                Marshal.FreeCoTaskMem(argumentString);
            if (applicationId != IntPtr.Zero)
                Marshal.FreeCoTaskMem(applicationId);
            NativeCom.Release(manager);
        }
    }

    private static string GetExplorerPath() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHParseDisplayName(
        [MarshalAs(UnmanagedType.LPWStr)] string name,
        IntPtr bindContext,
        out IntPtr itemIdList,
        uint attributesIn,
        out uint attributesOut);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHGetNameFromIDList(IntPtr itemIdList, int nameKind, out IntPtr name);

    [DllImport("shell32.dll", EntryPoint = "SHEmptyRecycleBinW", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHEmptyRecycleBin(IntPtr ownerWindow, string? rootPath, uint flags);

    private static void ExecuteShellVerb(
        string target,
        string verb,
        IntPtr ownerWindow,
        string? parameters = null,
        uint mask = 0)
    {
        using var apartment = NativeCom.InitializeApartment(allowDifferentApartment: true);
        var executeInfo = new ShellExecuteInfo
        {
            Size = Marshal.SizeOf<ShellExecuteInfo>(),
            Mask = mask,
            Owner = ownerWindow,
            Verb = verb,
            File = target,
            Parameters = parameters,
            Show = ShowNormal
        };

        if (!ShellExecuteEx(ref executeInfo))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 0 && executeInfo.InstanceHandle != IntPtr.Zero)
                error = checked((int)executeInfo.InstanceHandle.ToInt64());
            if (error == ErrorCancelled)
                return;
            throw new Win32Exception(error);
        }

        // ShellExecuteEx may return a legacy Shell error code in hInstApp even when the
        // BOOL return value is nonzero. Values above 32 indicate success.
        if (executeInfo.InstanceHandle != IntPtr.Zero && executeInfo.InstanceHandle.ToInt64() <= 32)
        {
            var error = checked((int)executeInfo.InstanceHandle.ToInt64());
            if (error != ErrorCancelled)
                throw new Win32Exception(error);
        }
    }

    private static void ExecuteShellItemVerb(string parsingName, string verb, IntPtr ownerWindow)
    {
        using var apartment = NativeCom.InitializeApartment(allowDifferentApartment: true);
        IntPtr itemIdList = IntPtr.Zero;
        try
        {
            Marshal.ThrowExceptionForHR(SHParseDisplayName(parsingName, IntPtr.Zero, out itemIdList, 0, out _));
            if (itemIdList == IntPtr.Zero)
                throw new FileNotFoundException(null, parsingName);

            var executeInfo = new ShellExecuteInfo
            {
                Size = Marshal.SizeOf<ShellExecuteInfo>(),
                Mask = SeeMaskInvokeIdList,
                Owner = ownerWindow,
                Verb = verb,
                IdList = itemIdList,
                Show = ShowNormal
            };

            if (!ShellExecuteEx(ref executeInfo))
            {
                var error = Marshal.GetLastWin32Error();
                if (error == 0 && executeInfo.InstanceHandle != IntPtr.Zero)
                    error = checked((int)executeInfo.InstanceHandle.ToInt64());
                if (error != ErrorCancelled)
                    throw new Win32Exception(error);
                return;
            }

            if (executeInfo.InstanceHandle != IntPtr.Zero && executeInfo.InstanceHandle.ToInt64() <= 32)
            {
                var error = checked((int)executeInfo.InstanceHandle.ToInt64());
                if (error != ErrorCancelled)
                    throw new Win32Exception(error);
            }
        }
        finally
        {
            if (itemIdList != IntPtr.Zero)
                Marshal.FreeCoTaskMem(itemIdList);
        }
    }

    [DllImport("shell32.dll", EntryPoint = "ShellExecuteExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellExecuteEx(ref ShellExecuteInfo executeInfo);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellExecuteInfo
    {
        public int Size;
        public uint Mask;
        public IntPtr Owner;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Verb;
        [MarshalAs(UnmanagedType.LPWStr)] public string? File;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Parameters;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Directory;
        public int Show;
        public IntPtr InstanceHandle;
        public IntPtr IdList;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Class;
        public IntPtr ClassKey;
        public uint HotKey;
        public IntPtr IconOrMonitor;
        public IntPtr Process;
    }
}
