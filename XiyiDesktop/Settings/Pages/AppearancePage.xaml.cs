using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System;

namespace XiyiDesktop.Pages
{
    public sealed partial class AppearancePage : Page
    {
        private bool _isInitializing = true;

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
            string theme = AppSettingsStore.AppTheme;
            ThemeOptions.SelectedIndex = theme switch { "Light" => 1, "Dark" => 2, _ => 0 };

            string material = AppSettingsStore.AppMaterial;
            MaterialOptions.SelectedIndex = material switch { "MicaAlt" => 1, "Acrylic" => 2, _ => 0 };
            ShortcutArrowToggle.IsOn = AppSettingsStore.ShowShortcutArrows;
            _isInitializing = false;
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
            App.DesktopHostWindow?.RefreshTransparentCompositionBackdrop();
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
