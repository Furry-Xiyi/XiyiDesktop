using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.ApplicationModel.Resources;
using System;
using System.Diagnostics;
using Windows.ApplicationModel;

namespace XiyiDesktop.Pages
{
    public sealed partial class BehaviorPage : Page
    {
        private readonly ResourceLoader _resources = new();
        private StartupTask? _startupTask;
        private bool _isInitializing = true;

        public BehaviorPage()
        {
            InitializeComponent();
            Loaded += async (_, _) => await LoadStartupStateAsync();
        }

        private async System.Threading.Tasks.Task LoadStartupStateAsync()
        {
            _isInitializing = true;
            StartupToggle.IsEnabled = false;
            try
            {
                _startupTask = await StartupTask.GetAsync(App.StartupTaskId);
                ApplyState(_startupTask.State);
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"Unable to read startup task: {exception.Message}");
                StartupStatusText.Text = _resources.GetString("Behavior_StartupUnavailable");
                StartupStatusText.Visibility = Visibility.Visible;
                StartupToggle.IsEnabled = false;
            }
            finally
            {
                _isInitializing = false;
            }
        }

        private async void StartupToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isInitializing || _startupTask is null)
                return;

            _isInitializing = true;
            StartupToggle.IsEnabled = false;
            try
            {
                if (StartupToggle.IsOn)
                    await _startupTask.RequestEnableAsync();
                else
                    _startupTask.Disable();

                ApplyState(_startupTask.State);
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"Unable to update startup task: {exception.Message}");
                StartupStatusText.Text = _resources.GetString("Behavior_StartupUpdateFailed");
                StartupStatusText.Visibility = Visibility.Visible;
                await LoadStartupStateAsync();
            }
            finally
            {
                _isInitializing = false;
            }
        }

        private void ApplyState(StartupTaskState state)
        {
            _isInitializing = true;
            StartupToggle.IsOn = state is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
            StartupToggle.IsEnabled = state is not (StartupTaskState.DisabledByUser or
                StartupTaskState.DisabledByPolicy or StartupTaskState.EnabledByPolicy);

            string messageKey = state switch
            {
                StartupTaskState.DisabledByUser => "Behavior_StartupDisabledByUser",
                StartupTaskState.DisabledByPolicy => "Behavior_StartupDisabledByPolicy",
                StartupTaskState.EnabledByPolicy => "Behavior_StartupEnabledByPolicy",
                _ => string.Empty
            };

            string message = string.IsNullOrEmpty(messageKey)
                ? string.Empty
                : _resources.GetString(messageKey);
            StartupStatusText.Text = message;
            StartupStatusText.Visibility = string.IsNullOrEmpty(message)
                ? Visibility.Collapsed
                : Visibility.Visible;
            _isInitializing = false;
        }
    }
}
