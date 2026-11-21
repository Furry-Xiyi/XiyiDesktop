using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.ApplicationModel.Resources;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Windows.UI;
using XiyiDesktop;
using XiyiDesktop.Widgets.Hosting;
using XiyiDesktop.Widgets.UI;

namespace XiyiDesktop
{
    public partial class App : Application
    {
        public static DesktopWindow? DesktopHostWindow { get; private set; }
        public static MainWindow? SettingsWindow { get; private set; }
        public static WidgetPickerWindow? WidgetPicker { get; private set; }
        internal static WidgetHostingService WidgetHostService { get; } = new();
        internal const string StartupTaskId = "XiyiDesktopStartupTask";

        private static DispatcherQueue? _dispatcherQueue;

        public App()
        {
            InitializeComponent();
        }

        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            var appUserModelId = Windows.ApplicationModel.AppInfo.Current.AppUserModelId;
            var instance = Microsoft.Windows.AppLifecycle.AppInstance.FindOrRegisterForKey("xiyi-desktop-shell");
            var activationArgs = Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs();
            if (!instance.IsCurrent)
            {
                string? requestPath = null;
                if (appUserModelId.EndsWith("!DesktopSettings", StringComparison.OrdinalIgnoreCase))
                    requestPath = QueueSettingsActivationRequest();

                try
                {
                    instance.RedirectActivationToAsync(activationArgs).GetAwaiter().GetResult();
                }
                catch
                {
                    if (requestPath is not null)
                        TryDeleteActivationRequest(requestPath);
                    throw;
                }

                Environment.Exit(0);
                return;
            }

            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
            // The desktop surface is hosted behind/over Explorer and should not be
            // activated as a regular foreground window.
            instance.Activated += (_, _) =>
                _dispatcherQueue?.TryEnqueue(HandleRedirectedActivation);

            AppThemeManager.LoadSettings();
            ShowDesktopWindow();
            if (appUserModelId.EndsWith("!DesktopSettings", StringComparison.OrdinalIgnoreCase) ||
                ConsumeSettingsActivationRequests())
            {
                ShowSettingsWindow();
            }
        }

        private static void HandleRedirectedActivation()
        {
            if (ConsumeSettingsActivationRequests())
                ShowSettingsWindow();
            else
                ShowDesktopWindow();
        }

        private static string? QueueSettingsActivationRequest()
        {
            try
            {
                // AppActivationArguments does not identify which manifest Application was launched.
                var directory = GetActivationRequestDirectory();
                Directory.CreateDirectory(directory);
                var requestPath = Path.Combine(directory, $"{DateTime.UtcNow.Ticks:D19}_{Guid.NewGuid():N}.settings");
                File.WriteAllText(requestPath, "OpenSettings");
                return requestPath;
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"Could not queue the settings window activation: {exception}");
                return null;
            }
        }

        private static bool ConsumeSettingsActivationRequests()
        {
            try
            {
                var directory = GetActivationRequestDirectory();
                if (!Directory.Exists(directory))
                    return false;

                var requestFiles = Directory.GetFiles(directory, "*.settings");
                var hasSettingsRequest = false;
                var expiredBefore = DateTime.UtcNow.AddMinutes(-1);
                foreach (var requestPath in requestFiles)
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(requestPath) >= expiredBefore)
                            hasSettingsRequest = true;
                        File.Delete(requestPath);
                    }
                    catch (IOException)
                    {
                        // Another activation may be consuming this request concurrently.
                    }
                }

                return hasSettingsRequest;
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"Could not read the pending settings window activation: {exception}");
                return false;
            }
        }

        private static string GetActivationRequestDirectory() => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XiyiDesktop",
            "ActivationRequests");

        private static void TryDeleteActivationRequest(string requestPath)
        {
            try { File.Delete(requestPath); } catch { }
        }

        internal static void ShowDesktopWindow()
        {
            var window = DesktopHostWindow;
            if (window is null)
            {
                window = new DesktopWindow();
                DesktopHostWindow = window;
                WindowIconAssetResolver.AssignPackageApplicationIdentity(window, "App");
                window.AppWindow.Title = new ResourceLoader().GetString("AppDisplayName");
                try { window.AppWindow.SetIcon("Assets/AppIcon.ico"); } catch { }
                window.Activate();
                return;
            }

            // If Explorer is still being discovered, do not expose the desktop
            // surface as an ordinary window. The host retries automatically.
            if (!window.IsDesktopAttached)
                window.RefreshHost();
        }

        internal static void ShowSettingsWindow() => ShowSettingsWindow(null);

        internal static void ShowSettingsWindow(string? pageTag)
        {
            var window = SettingsWindow;
            if (window is null)
            {
                window = new MainWindow();
                SettingsWindow = window;
                AppThemeManager.SetupTitleBar();
                AppThemeManager.ApplyMaterial();
                window.AppWindow.Title = new ResourceLoader().GetString("SettingsWindowTitle");
                WindowIconAssetResolver.AssignPackageApplicationIdentity(window, "DesktopSettings");
                window.StartLoadingContent(pageTag ?? "overview");
                window.Activate();
                return;
            }

            if (!string.IsNullOrWhiteSpace(pageTag))
                window.NavigateToPage(pageTag);
            BringWindowToFront(WinRT.Interop.WindowNative.GetWindowHandle(window));
        }

        internal static void ShowWidgetPickerWindow()
        {
            var window = WidgetPicker;
            if (window is null)
            {
                window = new WidgetPickerWindow();
                WidgetPicker = window;
                AppThemeManager.ApplyMaterial(window);
                window.AddRequested += async (_, request) =>
                {
                    var picker = WidgetPicker;
                    if (picker is null)
                        return;

                    picker.SetAddInProgress(true);
                    try
                    {
                        if (DesktopHostWindow is null ||
                            !await DesktopHostWindow.AddWidgetAsync(request.Provider, request.Definition, request.Size))
                        {
                            picker.ShowAddFailure();
                        }
                        else
                        {
                            picker.CloseAfterAdd();
                        }
                    }
                    catch (Exception exception)
                    {
                        Debug.WriteLine($"Could not add desktop widget: {exception}");
                        picker.ShowAddFailure();
                    }
                    finally
                    {
                        picker.SetAddInProgress(false);
                        picker.SetDesktopPlacementAvailability(
                            DesktopHostWindow?.CanAddWidget(request.Definition.DefinitionId, request.Size) == true,
                            DesktopHostWindow?.HasWidgetDefinition(request.Definition.DefinitionId) == true);
                    }
                };
                window.PlacementCheckRequested += (_, request) =>
                    window.SetDesktopPlacementAvailability(
                        DesktopHostWindow?.CanAddWidget(request.Definition.DefinitionId, request.Size) == true,
                        DesktopHostWindow?.HasWidgetDefinition(request.Definition.DefinitionId) == true);
                window.Closed += (_, _) =>
                {
                    if (ReferenceEquals(WidgetPicker, window))
                        WidgetPicker = null;
                };
            }

            window.RefreshPlacementCheck();
            window.Activate();
        }

        internal static void OnSettingsWindowClosed(MainWindow window)
        {
            if (ReferenceEquals(SettingsWindow, window))
                SettingsWindow = null;
        }

        internal static void RecreateDesktopWindow()
        {
            var previous = DesktopHostWindow;
            var window = new DesktopWindow();
            DesktopHostWindow = window;
            WindowIconAssetResolver.AssignPackageApplicationIdentity(window, "App");
            window.AppWindow.Title = new ResourceLoader().GetString("AppDisplayName");
            try { window.AppWindow.SetIcon("Assets/AppIcon.ico"); } catch { }
            window.Activate();
            try { previous?.Close(); } catch { }
        }

        internal static void RequestExit() => Current.Exit();

        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);

        private static void BringWindowToFront(IntPtr hwnd)
        {
            if (IsIconic(hwnd))
                ShowWindow(hwnd, 9);
            SetForegroundWindow(hwnd);
        }
    }
    public static class AppThemeManager
    {
        public static ElementTheme CurrentTheme = ElementTheme.Default;
        public static BackgroundMaterial CurrentMaterial = BackgroundMaterial.Mica;

        public static void LoadSettings()
        {
            CurrentTheme = AppSettingsStore.AppTheme switch
            {
                "Light" => ElementTheme.Light,
                "Dark" => ElementTheme.Dark,
                _ => ElementTheme.Default
            };

            CurrentMaterial = AppSettingsStore.AppMaterial switch
            {
                "MicaAlt" => BackgroundMaterial.MicaAlt,
                "Acrylic" => BackgroundMaterial.Acrylic,
                _ => BackgroundMaterial.Mica
            };

            ElementSoundPlayer.State = AppSettingsStore.EnableSound
                ? ElementSoundPlayerState.On
                : ElementSoundPlayerState.Off;
        }

        public static void ApplyMaterial()
        {
            if (App.SettingsWindow is { } settingsWindow)
                ApplyMaterial(settingsWindow);
            if (App.WidgetPicker is { } widgetPicker)
                ApplyMaterial(widgetPicker);
        }

        public static void ApplyMaterial(Window window)
        {
            try
            {
                if (window.SystemBackdrop is MicaBackdrop mica)
                {
                    if (CurrentMaterial == BackgroundMaterial.Mica && mica.Kind == MicaKind.Base) return;
                    if (CurrentMaterial == BackgroundMaterial.MicaAlt && mica.Kind == MicaKind.BaseAlt) return;
                }
                else if (window.SystemBackdrop is DesktopAcrylicBackdrop &&
                         CurrentMaterial == BackgroundMaterial.Acrylic)
                {
                    return;
                }

                window.SystemBackdrop = CurrentMaterial switch
                {
                    BackgroundMaterial.MicaAlt => new MicaBackdrop { Kind = MicaKind.BaseAlt },
                    BackgroundMaterial.Acrylic => new DesktopAcrylicBackdrop(),
                    _ => new MicaBackdrop { Kind = MicaKind.Base }
                };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ApplyMaterial failed: {ex.Message}");
                window.SystemBackdrop = null;
            }
        }

        public static void SetupTitleBar()
        {
            var window = App.SettingsWindow;
            if (window is null) return;
            try
            {
                if (!AppWindowTitleBar.IsCustomizationSupported()) return;
                var titleBar = window.AppWindow.TitleBar;
                titleBar.ExtendsContentIntoTitleBar = true;
                titleBar.ButtonBackgroundColor = Colors.Transparent;
                titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
                UpdateTitleBarColors();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SetupTitleBar failed: {ex.Message}");
            }
        }

        public static void UpdateTitleBarColors()
        {
            var window = App.SettingsWindow;
            if (window is null) return;
            try
            {
                if (!AppWindowTitleBar.IsCustomizationSupported()) return;
                var titleBar = window.AppWindow.TitleBar;
                bool isDark = GetIsDarkTheme();

                var fg = isDark ? Colors.White : Colors.Black;
                var inactiveFg = isDark
                    ? Color.FromArgb(255, 128, 128, 128)
                    : Color.FromArgb(255, 160, 160, 160);
                var hoverBg = isDark
                    ? Color.FromArgb(20, 255, 255, 255)
                    : Color.FromArgb(20, 0, 0, 0);

                titleBar.ButtonForegroundColor = fg;
                titleBar.ButtonInactiveForegroundColor = inactiveFg;
                titleBar.ButtonHoverBackgroundColor = hoverBg;
                titleBar.ButtonHoverForegroundColor = fg;
                titleBar.ButtonPressedBackgroundColor = Color.FromArgb(30, hoverBg.R, hoverBg.G, hoverBg.B);
                titleBar.ButtonPressedForegroundColor = fg;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"UpdateTitleBarColors failed: {ex.Message}");
            }
        }

        public static void OnActualThemeChanged(FrameworkElement sender, object args)
        {
            UpdateTitleBarColors();
        }

        public static bool GetIsDarkTheme()
        {
            if (App.SettingsWindow?.Content is FrameworkElement settingsRoot)
            {
                var actual = settingsRoot.ActualTheme;
                if (actual != ElementTheme.Default)
                    return actual == ElementTheme.Dark;
            }
            if (CurrentTheme == ElementTheme.Default)
                return Application.Current.RequestedTheme == ApplicationTheme.Dark;
            return CurrentTheme == ElementTheme.Dark;
        }
    }

    public enum BackgroundMaterial
    {
        Mica,
        MicaAlt,
        Acrylic
    }

    internal static class AppSettingsStore
    {
        private static readonly object SyncRoot = new();
        private static readonly string SettingsFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XiyiDesktop",
            "settings.json");
        private static readonly Dictionary<string, string> Values = Load();

        public static string AppTheme
        {
            get => Get("AppTheme", "System");
            set => Set("AppTheme", value);
        }

        public static string AppMaterial
        {
            get => Get("AppMaterial", "MicaAlt");
            set => Set("AppMaterial", value);
        }

        public static string PanePosition
        {
            get => Get("PanePosition", "Left");
            set => Set("PanePosition", value);
        }

        public static bool EnableSound
        {
            get => bool.TryParse(Get("EnableSound", "true"), out bool enabled) && enabled;
            set => Set("EnableSound", value.ToString());
        }

        public static string DesktopIconSize
        {
            get => Get("DesktopIconSize", "Medium");
            set => Set("DesktopIconSize", value);
        }

        public static double? DesktopIconCustomSize
        {
            get
            {
                var value = Get("DesktopIconCustomSize", string.Empty);
                return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var size) &&
                    double.IsFinite(size)
                    ? Math.Clamp(size, 24, 160)
                    : null;
            }
            set => Set("DesktopIconCustomSize", value?.ToString("0.###", CultureInfo.InvariantCulture) ?? string.Empty);
        }

        public static string DesktopSortMode
        {
            get
            {
                var value = Get("DesktopSortMode", "Name");
                return value is "Name" or "Size" or "Type" or "Modified" or "Manual" ? value : "Name";
            }
            set => Set("DesktopSortMode", value);
        }

        public static bool DesktopAutoArrange
        {
            get
            {
                lock (SyncRoot)
                {
                    if (Values.TryGetValue("DesktopAutoArrange", out var value) &&
                        bool.TryParse(value, out var enabled))
                    {
                        return enabled;
                    }

                    // Older settings stored "Manual" as a sort mode. Preserve that user's
                    // manual layout while migrating to the independent arrange setting.
                    return !Values.TryGetValue("DesktopSortMode", out var sortMode) ||
                        !string.Equals(sortMode, "Manual", StringComparison.Ordinal);
                }
            }
            set => Set("DesktopAutoArrange", value.ToString());
        }

        public static bool DesktopSortDescending
        {
            get => bool.TryParse(Get("DesktopSortDescending", "false"), out var descending) && descending;
            set => Set("DesktopSortDescending", value.ToString());
        }

        public static bool ShowDesktopIcons
        {
            get => bool.TryParse(Get("ShowDesktopIcons", "true"), out bool visible) && visible;
            set => Set("ShowDesktopIcons", value.ToString());
        }

        public static bool ShowShortcutArrows
        {
            get => bool.TryParse(Get("ShowShortcutArrows", "true"), out bool visible) && visible;
            set => Set("ShowShortcutArrows", value.ToString());
        }

        public static bool AlignIconsToGrid
        {
            get => bool.TryParse(Get("AlignIconsToGrid", "true"), out bool enabled) && enabled;
            set => Set("AlignIconsToGrid", value.ToString());
        }

        private static string Get(string key, string defaultValue)
        {
            lock (SyncRoot)
                return Values.TryGetValue(key, out string value) ? value : defaultValue;
        }

        private static void Set(string key, string value)
        {
            lock (SyncRoot)
            {
                Values[key] = value;
                try
                {
                    string directory = Path.GetDirectoryName(SettingsFilePath)!;
                    Directory.CreateDirectory(directory);
                    File.WriteAllText(SettingsFilePath, JsonSerializer.Serialize(
                        Values, AppJsonSerializerContext.Default.Settings));
                }
                catch (Exception exception)
                {
                    Debug.WriteLine($"Unable to save app settings: {exception.Message}");
                }
            }
        }

        private static Dictionary<string, string> Load()
        {
            try
            {
                if (File.Exists(SettingsFilePath))
                    return JsonSerializer.Deserialize(
                        File.ReadAllText(SettingsFilePath), AppJsonSerializerContext.Default.Settings)
                        ?? new Dictionary<string, string>();
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"Unable to load app settings: {exception.Message}");
            }

            return new Dictionary<string, string>();
        }
    }
}

