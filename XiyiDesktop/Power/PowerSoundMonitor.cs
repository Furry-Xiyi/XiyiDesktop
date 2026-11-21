using Microsoft.Win32;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;

namespace XiyiDesktop.Power;

internal static class PowerSoundMonitor
{
    private const uint WmPowerBroadcast = 0x0218;
    private const uint WmSettingChange = 0x001A;
    private const uint WmThemeChanged = 0x031A;
    private const uint WmNcDestroy = 0x0082;
    private const uint WsPopup = 0x80000000;
    private const uint WsExToolWindow = 0x00000080;
    private const int ErrorClassAlreadyExists = 1410;
    private const uint SoundAsync = 0x00000001;
    private const uint SoundNoDefault = 0x00000002;
    private const uint SoundAlias = 0x00010000;
    private const uint SoundApplication = 0x00000080;
    private const int ApplicationDisplayNameResource = 50100;
    private const int ConnectedDisplayNameResource = 50101;
    private const int DisconnectedDisplayNameResource = 50102;
    private const string WindowClassName = "XiyiDesktop.PowerSoundMonitor";
    private const string ConnectedEventName = "XiyiDesktop.PowerConnected";
    private const string DisconnectedEventName = "XiyiDesktop.PowerDisconnected";
    private const string AppEventsPath = @"AppEvents\Schemes\Apps";
    private const string EventLabelsPath = @"AppEvents\EventLabels";
    private const string CompatibilityEventLabelsPath = @"Software\Classes\AppEvents\EventLabels";
    private const string LightThemeRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private static IntPtr _windowHandle;
    private static uint _windowThreadId;
    private static bool _windowClassRegistered;
    private static string? _applicationName;
    private static string? _modulePath;
    private static bool _soundsEnabled = true;
    private static bool _customSoundsEnabled;
    private static string? _connectedCustomSoundPath;
    private static string? _disconnectedCustomSoundPath;
    private static bool _connectedSoundEnabled = true;
    private static bool _disconnectedSoundEnabled = true;
    private static MediaPlayer? _customAudioPlayer;
    private static MediaSource? _customAudioSource;
    private static int _customAudioPlaybackGeneration;
    private static bool? _isLightTheme;
    private static bool? _lastAcOnline;

    public static void Start()
    {
        if (_windowHandle != IntPtr.Zero)
            return;

        try
        {
            _modulePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(_modulePath))
                throw new InvalidOperationException("The executable path could not be determined.");

            _applicationName = Path.GetFileNameWithoutExtension(_modulePath);
            _soundsEnabled = AppSettingsStore.PowerSoundsEnabled;
            _customSoundsEnabled = AppSettingsStore.UseCustomPowerSounds;
            _connectedCustomSoundPath = AppSettingsStore.PowerConnectedSoundPath;
            _disconnectedCustomSoundPath = AppSettingsStore.PowerDisconnectedSoundPath;
            _connectedSoundEnabled = AppSettingsStore.PowerConnectedSoundEnabled;
            _disconnectedSoundEnabled = AppSettingsStore.PowerDisconnectedSoundEnabled;

            MigrateExecutableNameSoundSettings();
            RegisterSoundEvents();
            UpdateThemeDefaults(force: true);
            ApplyStoredCustomSounds();

            RegisterWindowClass();
            _windowThreadId = GetCurrentThreadId();
            _windowHandle = CreateWindowEx(
                WsExToolWindow,
                WindowClassName,
                string.Empty,
                WsPopup,
                0,
                0,
                1,
                1,
                IntPtr.Zero,
                IntPtr.Zero,
                GetModuleHandle(null),
                IntPtr.Zero);

            if (_windowHandle == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The power sound listener window could not be created.");

            _lastAcOnline = TryGetAcOnline(out var acOnline) ? acOnline : null;
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Power sound initialization failed: {exception}");
            Stop();
        }
    }

    public static void ApplyConfiguration(
        bool soundsEnabled,
        bool useCustomSounds,
        string? connectedSoundPath,
        string? disconnectedSoundPath,
        bool connectedSoundEnabled,
        bool disconnectedSoundEnabled,
        bool customModeChanged = false,
        bool connectedSoundChanged = false,
        bool disconnectedSoundChanged = false)
    {
        _soundsEnabled = soundsEnabled;
        _connectedSoundEnabled = connectedSoundEnabled;
        _disconnectedSoundEnabled = disconnectedSoundEnabled;

        if (customModeChanged && useCustomSounds)
        {
            ApplyCustomSound(ConnectedEventName, connectedSoundPath);
            ApplyCustomSound(DisconnectedEventName, disconnectedSoundPath);
        }
        else if (customModeChanged)
        {
            RestoreDefaultWhenCurrentIsCustom(ConnectedEventName, _connectedCustomSoundPath);
            RestoreDefaultWhenCurrentIsCustom(DisconnectedEventName, _disconnectedCustomSoundPath);
        }

        if (useCustomSounds && connectedSoundChanged)
            ApplyCustomSound(ConnectedEventName, connectedSoundPath);

        if (useCustomSounds && disconnectedSoundChanged)
            ApplyCustomSound(DisconnectedEventName, disconnectedSoundPath);

        _customSoundsEnabled = useCustomSounds;
        _connectedCustomSoundPath = connectedSoundPath;
        _disconnectedCustomSoundPath = disconnectedSoundPath;
    }

    public static void Stop()
    {
        StopCustomAudioPlayback();

        if (_windowHandle != IntPtr.Zero)
        {
            if (GetCurrentThreadId() != _windowThreadId)
            {
                Debug.WriteLine("The power sound listener must be stopped on its window thread.");
                return;
            }

            _ = DestroyWindow(_windowHandle);
            _windowHandle = IntPtr.Zero;
            _windowThreadId = 0;
        }

        if (_windowClassRegistered)
        {
            _ = UnregisterClass(WindowClassName, GetModuleHandle(null));
            _windowClassRegistered = false;
        }

        _lastAcOnline = null;
        _isLightTheme = null;
    }

    private static void RegisterSoundEvents()
    {
        if (_applicationName is null || _modulePath is null)
            return;

        RegisterApplicationDisplayName();
        RegisterSoundEvent(ConnectedEventName, ConnectedDisplayNameResource, "Power connected");
        RegisterSoundEvent(DisconnectedEventName, DisconnectedDisplayNameResource, "Power disconnected");
    }

    private static void MigrateExecutableNameSoundSettings()
    {
        if (_applicationName is null || _modulePath is null)
            return;

        string executableFileName = Path.GetFileName(_modulePath);
        if (string.Equals(executableFileName, _applicationName, StringComparison.OrdinalIgnoreCase))
            return;

        string previousApplicationPath = $"{AppEventsPath}\\{executableFileName}";
        try
        {
            using (var previousApplication = Registry.CurrentUser.OpenSubKey(previousApplicationPath))
            {
                if (previousApplication is null)
                    return;

                using var application = Registry.CurrentUser.CreateSubKey($"{AppEventsPath}\\{_applicationName}");
                if (application is null)
                    return;

                CopyRegistryValues(previousApplication, application);
            }

            Registry.CurrentUser.DeleteSubKeyTree(previousApplicationPath, throwOnMissingSubKey: false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Debug.WriteLine($"Could not migrate the power sound settings: {exception.Message}");
        }
    }

    private static void CopyRegistryValues(RegistryKey source, RegistryKey destination)
    {
        foreach (string valueName in source.GetValueNames())
        {
            if (destination.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not null)
                continue;

            object? value = source.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (value is not null)
                destination.SetValue(valueName, value, source.GetValueKind(valueName));
        }

        foreach (string subKeyName in source.GetSubKeyNames())
        {
            using var sourceChild = source.OpenSubKey(subKeyName);
            using var destinationChild = destination.CreateSubKey(subKeyName);
            if (sourceChild is not null && destinationChild is not null)
                CopyRegistryValues(sourceChild, destinationChild);
        }
    }

    private static void RegisterApplicationDisplayName()
    {
        using var application = Registry.CurrentUser.CreateSubKey($"{AppEventsPath}\\{_applicationName}");
        application?.SetValue(string.Empty, "Xiyi Desktop", RegistryValueKind.String);
        application?.SetValue(
            "DispFileName",
            $"@{_modulePath},-{ApplicationDisplayNameResource}",
            RegistryValueKind.String);
    }

    private static void RegisterSoundEvent(string eventName, int displayNameResource, string fallbackDisplayName)
    {
        string displayNameReference = $"@{_modulePath},-{displayNameResource}";
        WriteEventDisplayName(EventLabelsPath, eventName, displayNameReference, fallbackDisplayName);
        WriteEventDisplayName(CompatibilityEventLabelsPath, eventName, displayNameReference, fallbackDisplayName);

        using var appEvent = Registry.CurrentUser.CreateSubKey(GetEventRegistryPath(eventName));
        appEvent?.CreateSubKey(".Default")?.Dispose();
        appEvent?.CreateSubKey(".Current")?.Dispose();
    }

    private static void WriteEventDisplayName(
        string eventLabelsPath,
        string eventName,
        string displayNameReference,
        string fallbackDisplayName)
    {
        using var label = Registry.CurrentUser.CreateSubKey($"{eventLabelsPath}\\{eventName}");
        label?.SetValue(string.Empty, fallbackDisplayName, RegistryValueKind.String);
        label?.SetValue("DispFileName", displayNameReference, RegistryValueKind.String);
    }

    private static unsafe void RegisterWindowClass()
    {
        if (_windowClassRegistered)
            return;

        var windowClass = new WindowClassEx
        {
            Size = (uint)Marshal.SizeOf<WindowClassEx>(),
            WindowProcedure = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, UIntPtr, IntPtr, IntPtr>)&WindowProcedure,
            Instance = GetModuleHandle(null),
            ClassName = WindowClassName
        };

        if (RegisterClassEx(ref windowClass) == 0 && Marshal.GetLastWin32Error() != ErrorClassAlreadyExists)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The power sound listener window class could not be registered.");

        _windowClassRegistered = true;
    }

    private static void UpdateThemeDefaults(bool force)
    {
        bool isLightTheme = ReadSystemUsesLightTheme();
        if (!force && _isLightTheme == isLightTheme)
            return;

        UpdateDefaultSound(ConnectedEventName, isLightTheme, connected: true);
        UpdateDefaultSound(DisconnectedEventName, isLightTheme, connected: false);
        _isLightTheme = isLightTheme;
    }

    private static void UpdateDefaultSound(string eventName, bool isLightTheme, bool connected)
    {
        string? oldDefaultPath = ReadEventSoundPath(eventName, ".Default");
        var currentSetting = ReadEventSoundSetting(eventName, ".Current");
        string? newDefaultPath = ResolveDefaultSoundPath(connected, isLightTheme);

        WriteEventSoundPath(eventName, ".Default", newDefaultPath);

        if (currentSetting.ReadSucceeded &&
            (!currentSetting.HasValue || PathsEqual(currentSetting.Path, oldDefaultPath)))
        {
            WriteEventSoundPath(eventName, ".Current", newDefaultPath);
        }
    }

    private static string? ResolveDefaultSoundPath(bool connected, bool isLightTheme)
    {
        string? lightPath = ReadLightThemeSoundPath(connected);
        string? preferredPath = isLightTheme ? lightPath : GetDarkThemeSoundPath(connected);

        if (FileExists(preferredPath))
            return preferredPath;

        return FileExists(lightPath) ? lightPath : null;
    }

    private static string? ReadLightThemeSoundPath(bool connected)
    {
        string nativeEvent = connected ? "DeviceConnect" : "DeviceDisconnect";
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($"{AppEventsPath}\\.Default\\{nativeEvent}\\.Default");
            var path = key?.GetValue(string.Empty, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            return ExpandSoundPath(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Debug.WriteLine($"Could not read the Windows {nativeEvent} sound: {exception.Message}");
            return null;
        }
    }

    private static string? GetDarkThemeSoundPath(bool connected)
    {
        string mediaDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "Media",
            "dm");
        return Path.Combine(mediaDirectory, connected ? "Windows Hardware Insert.wav" : "Windows Hardware Remove.wav");
    }

    private static bool ReadSystemUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(LightThemeRegistryPath);
            return key?.GetValue("SystemUsesLightTheme") is not int themeValue || themeValue != 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Debug.WriteLine($"Could not read the Windows appearance setting: {exception.Message}");
            return true;
        }
    }

    private static void ApplyStoredCustomSounds()
    {
        if (!_customSoundsEnabled)
        {
            RestoreDefaultWhenCurrentIsCustom(ConnectedEventName, _connectedCustomSoundPath);
            RestoreDefaultWhenCurrentIsCustom(DisconnectedEventName, _disconnectedCustomSoundPath);
            return;
        }

        ApplyCustomSound(ConnectedEventName, _connectedCustomSoundPath);
        ApplyCustomSound(DisconnectedEventName, _disconnectedCustomSoundPath);
    }

    private static void ApplyCustomSound(string eventName, string? path)
    {
        string? normalizedPath = NormalizeExistingAudioFile(path);
        if (normalizedPath is not null)
            WriteEventSoundPath(eventName, ".Current", normalizedPath);
    }

    public static void ResetCustomSound(bool connected)
    {
        string eventName = connected ? ConnectedEventName : DisconnectedEventName;

        WriteEventSoundPath(eventName, ".Current", ReadEventSoundPath(eventName, ".Default"));

        if (connected)
            _connectedCustomSoundPath = null;
        else
            _disconnectedCustomSoundPath = null;
    }

    private static void RestoreDefaultWhenCurrentIsCustom(string eventName, string? customPath)
    {
        string? currentPath = ReadEventSoundPath(eventName, ".Current");
        if (PathsEqual(currentPath, customPath))
            WriteEventSoundPath(eventName, ".Current", ReadEventSoundPath(eventName, ".Default"));
    }

    private static void PlayPowerSound(bool connected)
    {
        if (!_soundsEnabled || (connected ? !_connectedSoundEnabled : !_disconnectedSoundEnabled))
            return;

        string eventName = connected ? ConnectedEventName : DisconnectedEventName;
        string? currentPath = ReadEventSoundPath(eventName, ".Current");
        if (!IsWaveFile(currentPath))
        {
            if (!string.IsNullOrWhiteSpace(currentPath))
                _ = PlayCustomAudioAsync(currentPath);
            return;
        }

        if (!PlaySound($"{eventName}", IntPtr.Zero, SoundApplication | SoundAlias | SoundAsync | SoundNoDefault))
            Debug.WriteLine($"The {eventName} sound alias could not be played.");
    }

    private static async Task PlayCustomAudioAsync(string path)
    {
        int generation = Interlocked.Increment(ref _customAudioPlaybackGeneration);
        string? normalizedPath = NormalizeExistingAudioFile(path);
        if (normalizedPath is null)
            return;

        try
        {
            StorageFile file = await StorageFile.GetFileFromPathAsync(normalizedPath);
            if (generation != Volatile.Read(ref _customAudioPlaybackGeneration))
                return;

            MediaSource source = MediaSource.CreateFromStorageFile(file);
            if (generation != Volatile.Read(ref _customAudioPlaybackGeneration))
            {
                source.Dispose();
                return;
            }

            StopCustomAudioPlayback(incrementGeneration: false);
            var player = new MediaPlayer
            {
                AudioCategory = MediaPlayerAudioCategory.SoundEffects,
                AutoPlay = false,
                IsLoopingEnabled = false,
                Source = source
            };

            _customAudioSource = source;
            _customAudioPlayer = player;
            player.Play();
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Could not play the custom power sound '{normalizedPath}': {exception.Message}");
        }
    }

    private static void StopCustomAudioPlayback(bool incrementGeneration = true)
    {
        if (incrementGeneration)
            Interlocked.Increment(ref _customAudioPlaybackGeneration);

        var player = _customAudioPlayer;
        var source = _customAudioSource;
        _customAudioPlayer = null;
        _customAudioSource = null;

        try
        {
            player?.Dispose();
            source?.Dispose();
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Could not stop the custom power sound: {exception.Message}");
        }
    }

    private static void HandlePowerStatusChange()
    {
        if (!TryGetAcOnline(out bool acOnline))
            return;

        if (_lastAcOnline is null)
        {
            _lastAcOnline = acOnline;
            return;
        }

        if (_lastAcOnline.Value == acOnline)
            return;

        _lastAcOnline = acOnline;
        PlayPowerSound(acOnline);
    }

    private static bool TryGetAcOnline(out bool acOnline)
    {
        if (GetSystemPowerStatus(out var status) && status.AcLineStatus <= 1)
        {
            acOnline = status.AcLineStatus == 1;
            return true;
        }

        acOnline = false;
        return false;
    }

    private static string GetEventRegistryPath(string eventName) => $"{AppEventsPath}\\{_applicationName}\\{eventName}";

    private static string? ReadEventSoundPath(string eventName, string scheme)
    {
        return ReadEventSoundSetting(eventName, scheme).Path;
    }

    private static (bool ReadSucceeded, bool HasValue, string? Path) ReadEventSoundSetting(string eventName, string scheme)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($"{GetEventRegistryPath(eventName)}\\{scheme}");
            if (key is null)
                return (true, false, null);

            bool hasValue = Array.Exists(key.GetValueNames(), name => name.Length == 0);
            var path = key.GetValue(string.Empty, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            return (true, hasValue, ExpandSoundPath(path));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Debug.WriteLine($"Could not read the {eventName} sound setting: {exception.Message}");
            return (false, false, null);
        }
    }

    private static void WriteEventSoundPath(string eventName, string scheme, string? path)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey($"{GetEventRegistryPath(eventName)}\\{scheme}");
            key?.SetValue(string.Empty, path ?? string.Empty, RegistryValueKind.String);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Debug.WriteLine($"Could not update the {eventName} sound setting: {exception.Message}");
        }
    }

    private static string? NormalizeExistingAudioFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            string normalizedPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
            return File.Exists(normalizedPath) ? normalizedPath : null;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    private static string? ExpandSoundPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        string value = path.Trim().Trim('"');
        return Environment.ExpandEnvironmentVariables(value);
    }

    private static bool FileExists(string? path) => !string.IsNullOrWhiteSpace(path) && File.Exists(path);

    private static bool IsWaveFile(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        string.Equals(Path.GetExtension(path), ".wav", StringComparison.OrdinalIgnoreCase);

    private static bool PathsEqual(string? first, string? second) =>
        !string.IsNullOrWhiteSpace(first) &&
        !string.IsNullOrWhiteSpace(second) &&
        string.Equals(first, second, StringComparison.OrdinalIgnoreCase);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static IntPtr WindowProcedure(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam)
    {
        if (message == WmPowerBroadcast)
        {
            HandlePowerStatusChange();
            return new IntPtr(1);
        }

        if (message == WmSettingChange || message == WmThemeChanged)
        {
            UpdateThemeDefaults(force: false);
        }

        IntPtr result = DefWindowProc(hwnd, message, wParam, lParam);
        if (message == WmNcDestroy && hwnd == _windowHandle)
            _windowHandle = IntPtr.Zero;

        return result;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClassEx
    {
        public uint Size;
        public uint Style;
        public IntPtr WindowProcedure;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr BackgroundBrush;
        public string? MenuName;
        public string ClassName;
        public IntPtr SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", EntryPoint = "GetSystemPowerStatus", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WindowClassEx windowClass);

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr parameter);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "DestroyWindow", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "UnregisterClassW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterClass(string className, IntPtr instance);

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PlaySound(string soundName, IntPtr module, uint flags);
}
