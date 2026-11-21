using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.ApplicationModel.Resources;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using XiyiDesktop.Power;

namespace XiyiDesktop.Pages
{
    public sealed partial class AppearancePage : Page
    {
        private bool _isInitializing = true;
        private readonly ResourceLoader _resources = new();

        public AppearancePage()
        {
            InitializeComponent();
            Loaded += (_, _) =>
            {
                LoadSettings();
                UpdateShortcutArrowIcon();
            };
            ActualThemeChanged += (_, _) => UpdateShortcutArrowIcon();
        }

        private void UpdateShortcutArrowIcon()
        {
            string themeFolder = ActualTheme == ElementTheme.Dark ? "theme-dark" : "theme-light";
            ShortcutArrowIcon.Source = new SvgImageSource
            {
                UriSource = new Uri($"ms-appx:///Assets/Icon/{themeFolder}/link.svg")
            };
        }

        private void LoadSettings()
        {
            _isInitializing = true;
            string theme = AppSettingsStore.AppTheme;
            ThemeOptions.SelectedIndex = theme switch { "Light" => 1, "Dark" => 2, _ => 0 };

            string material = AppSettingsStore.AppMaterial;
            MaterialOptions.SelectedIndex = material switch { "MicaAlt" => 1, "Acrylic" => 2, _ => 0 };
            ShortcutArrowToggle.IsOn = AppSettingsStore.ShowShortcutArrows;
            CornerRadiusSlider.Value = AppSettingsStore.DesktopCornerRadius;
            UpdateCornerRadiusValueText(CornerRadiusSlider.Value);
            PowerSoundsToggle.IsOn = AppSettingsStore.PowerSoundsEnabled;
            UseCustomPowerSoundsCheckBox.IsChecked = AppSettingsStore.UseCustomPowerSounds;
            PowerConnectedSoundToggle.IsOn = AppSettingsStore.PowerConnectedSoundEnabled;
            PowerDisconnectedSoundToggle.IsOn = AppSettingsStore.PowerDisconnectedSoundEnabled;
            ToolTipService.SetToolTip(
                PowerConnectedSoundResetButton,
                _resources.GetString("PowerSoundResetTooltip"));
            ToolTipService.SetToolTip(
                PowerDisconnectedSoundResetButton,
                _resources.GetString("PowerSoundResetTooltip"));
            UpdatePowerSoundDescriptions();
            UpdatePowerSoundControlAvailability();
            _isInitializing = false;
        }

        private void PowerSoundsToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isInitializing)
                return;

            AppSettingsStore.PowerSoundsEnabled = PowerSoundsToggle.IsOn;
            UpdatePowerSoundControlAvailability();
            ApplyPowerSoundConfiguration();
        }

        private void UseCustomPowerSoundsCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_isInitializing)
                return;

            AppSettingsStore.UseCustomPowerSounds = UseCustomPowerSoundsCheckBox.IsChecked == true;
            UpdatePowerSoundControlAvailability();
            ApplyPowerSoundConfiguration(customModeChanged: true);
        }

        private void UpdatePowerSoundControlAvailability()
        {
            bool customSoundsAvailable = PowerSoundsToggle.IsOn;
            UseCustomPowerSoundsCheckBox.IsEnabled = customSoundsAvailable;

            bool canChooseCustomSounds = customSoundsAvailable && UseCustomPowerSoundsCheckBox.IsChecked == true;
            PowerConnectedSoundChooseButton.IsEnabled = canChooseCustomSounds;
            PowerDisconnectedSoundChooseButton.IsEnabled = canChooseCustomSounds;
            PowerConnectedSoundResetButton.IsEnabled = customSoundsAvailable;
            PowerDisconnectedSoundResetButton.IsEnabled = customSoundsAvailable;
            PowerConnectedSoundToggle.IsEnabled = customSoundsAvailable;
            PowerDisconnectedSoundToggle.IsEnabled = customSoundsAvailable;
            PowerConnectedSoundResetButton.Visibility = string.IsNullOrWhiteSpace(AppSettingsStore.PowerConnectedSoundPath)
                ? Visibility.Collapsed
                : Visibility.Visible;
            PowerDisconnectedSoundResetButton.Visibility = string.IsNullOrWhiteSpace(AppSettingsStore.PowerDisconnectedSoundPath)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private void UpdatePowerSoundDescriptions()
        {
            PowerConnectedSoundCard.Description = GetPowerSoundDescription(AppSettingsStore.PowerConnectedSoundPath);
            PowerDisconnectedSoundCard.Description = GetPowerSoundDescription(AppSettingsStore.PowerDisconnectedSoundPath);
        }

        private string GetPowerSoundDescription(string path) =>
            string.IsNullOrWhiteSpace(path)
                ? _resources.GetString("PowerSoundDefaultDescription")
                : path;

        private async void PowerConnectedSoundChooseButton_Click(object sender, RoutedEventArgs e)
        {
            string? path = await PickAudioFileAsync();
            if (path is null)
                return;

            AppSettingsStore.PowerConnectedSoundPath = path;
            UpdatePowerSoundDescriptions();
            UpdatePowerSoundControlAvailability();
            ApplyPowerSoundConfiguration(connectedSoundChanged: true);
        }

        private async void PowerDisconnectedSoundChooseButton_Click(object sender, RoutedEventArgs e)
        {
            string? path = await PickAudioFileAsync();
            if (path is null)
                return;

            AppSettingsStore.PowerDisconnectedSoundPath = path;
            UpdatePowerSoundDescriptions();
            UpdatePowerSoundControlAvailability();
            ApplyPowerSoundConfiguration(disconnectedSoundChanged: true);
        }

        private void PowerConnectedSoundResetButton_Click(object sender, RoutedEventArgs e)
        {
            AppSettingsStore.PowerConnectedSoundPath = string.Empty;
            PowerSoundMonitor.ResetCustomSound(connected: true);
            UpdatePowerSoundDescriptions();
            UpdatePowerSoundControlAvailability();
        }

        private void PowerDisconnectedSoundResetButton_Click(object sender, RoutedEventArgs e)
        {
            AppSettingsStore.PowerDisconnectedSoundPath = string.Empty;
            PowerSoundMonitor.ResetCustomSound(connected: false);
            UpdatePowerSoundDescriptions();
            UpdatePowerSoundControlAvailability();
        }

        private void PowerConnectedSoundToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isInitializing)
                return;

            AppSettingsStore.PowerConnectedSoundEnabled = PowerConnectedSoundToggle.IsOn;
            ApplyPowerSoundConfiguration();
        }

        private void PowerDisconnectedSoundToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isInitializing)
                return;

            AppSettingsStore.PowerDisconnectedSoundEnabled = PowerDisconnectedSoundToggle.IsOn;
            ApplyPowerSoundConfiguration();
        }

        private async Task<string?> PickAudioFileAsync()
        {
            if (App.SettingsWindow is not { } settingsWindow)
                return null;

            try
            {
                var picker = new FileOpenPicker();
                picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(
                    picker,
                    WinRT.Interop.WindowNative.GetWindowHandle(settingsWindow));

                var file = await picker.PickSingleFileAsync();
                return file?.Path;
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"Could not select a power sound file: {exception.Message}");
                return null;
            }
        }

        private void ApplyPowerSoundConfiguration(
            bool customModeChanged = false,
            bool connectedSoundChanged = false,
            bool disconnectedSoundChanged = false)
        {
            PowerSoundMonitor.ApplyConfiguration(
                AppSettingsStore.PowerSoundsEnabled,
                AppSettingsStore.UseCustomPowerSounds,
                AppSettingsStore.PowerConnectedSoundPath,
                AppSettingsStore.PowerDisconnectedSoundPath,
                AppSettingsStore.PowerConnectedSoundEnabled,
                AppSettingsStore.PowerDisconnectedSoundEnabled,
                customModeChanged,
                connectedSoundChanged,
                disconnectedSoundChanged);
        }

        private void UpdateCornerRadiusValueText(double value) =>
            CornerRadiusValueText.Text = $"{Math.Round(value):0}";

        private void CornerRadiusSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            UpdateCornerRadiusValueText(e.NewValue);
            if (_isInitializing)
                return;

            var value = Math.Clamp(e.NewValue, 0, 32);
            AppSettingsStore.DesktopCornerRadius = value;
            App.DesktopHostWindow?.UpdateCornerRadiusSetting(value);
        }

        private void ShortcutArrowToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isInitializing)
                return;

            var showShortcutArrows = ShortcutArrowToggle.IsOn;
            AppSettingsStore.ShowShortcutArrows = showShortcutArrows;
            App.DesktopHostWindow?.UpdateShortcutArrowSetting(showShortcutArrows);
        }

        private void ThemeOptions_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializing) return;

            string value = ThemeOptions.SelectedIndex switch { 1 => "Light", 2 => "Dark", _ => "System" };
            ElementTheme theme = ThemeOptions.SelectedIndex switch
            {
                1 => ElementTheme.Light,
                2 => ElementTheme.Dark,
                _ => ElementTheme.Default
            };

            AppSettingsStore.AppTheme = value;
            AppThemeManager.CurrentTheme = theme;
            if (XiyiDesktop.App.SettingsWindow?.Content is FrameworkElement root)
                root.RequestedTheme = theme;
            AppThemeManager.UpdateTitleBarColors();
        }

        private void MaterialOptions_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializing) return;

            string value = MaterialOptions.SelectedIndex switch { 1 => "MicaAlt", 2 => "Acrylic", _ => "Mica" };
            AppSettingsStore.AppMaterial = value;
            AppThemeManager.CurrentMaterial = value switch
            {
                "MicaAlt" => BackgroundMaterial.MicaAlt,
                "Acrylic" => BackgroundMaterial.Acrylic,
                _ => BackgroundMaterial.Mica
            };
            AppThemeManager.ApplyMaterial();
        }
    }
}
