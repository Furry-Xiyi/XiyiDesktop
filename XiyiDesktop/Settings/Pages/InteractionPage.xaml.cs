using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace XiyiDesktop.Pages
{
    public sealed partial class InteractionPage : Page
    {
        private bool _isInitializing = true;

        public InteractionPage()
        {
            InitializeComponent();
            Loaded += (_, _) => LoadSettings();
        }

        private void LoadSettings()
        {
            bool sound = AppSettingsStore.EnableSound;
            SoundToggle.IsOn = sound;
            _isInitializing = false;
        }

        private void SoundToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            bool isOn = SoundToggle.IsOn;
            AppSettingsStore.EnableSound = isOn;
            ElementSoundPlayer.State = isOn ? ElementSoundPlayerState.On : ElementSoundPlayerState.Off;
        }
    }
}
