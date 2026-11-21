using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;
using Microsoft.Windows.ApplicationModel.Resources;
using System;
using System.Runtime.InteropServices;
using Windows.ApplicationModel;
using Windows.System;
using XiyiDesktop.Dialogs;
using XiyiDesktop.Pages;

namespace XiyiDesktop
{
    public sealed partial class MainWindow : Window
    {
        private static SUBCLASSPROC? _subclassProc;
        public static MainWindow? Instance { get; private set; }

        public MainWindow()
        {
            InitializeComponent();
            Instance = this;
            WindowIconAssetResolver.SetWindowIcon(this, "DesktopSettings");
            Closed += (_, _) => XiyiDesktop.App.OnSettingsWindowClosed(this);
            AppWindow.Closing += AppWindow_Closing;
            TitleBarArea.Window = this;

            if (Content is FrameworkElement root)
                root.RequestedTheme = AppThemeManager.CurrentTheme;

            ContentFrame.Navigated += ContentFrame_Navigated;

            if (Content is FrameworkElement rootElement)
                rootElement.Loaded += Root_Loaded;

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            SetMinWindowSize(hwnd, minWidth: 800, minHeight: 520);
        }

        private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
        {
            args.Cancel = true;
            sender.Hide();
        }

        public async void OpenExternalLink(object sender, RoutedEventArgs e)
        {
            var root = (ContentFrame.Content as FrameworkElement)?.XamlRoot;
            if (root == null)
                return;

            var url = sender switch
            {
                Button button => button.Tag as string,
                HyperlinkButton hyperlink => hyperlink.Tag as string,
                _ => null
            };

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return;

            var dialog = new ExternalOpenDialog { XamlRoot = root };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                await Launcher.LaunchUriAsync(uri);
        }

        public void StartLoadingContent(string initialTag = "overview")
        {
            NavigateToPage(initialTag);
        }

        private static Type? PageTypeForTag(string? tag) => tag switch
        {
            "overview" => typeof(HomePage),
            "appearance" => typeof(AppearancePage),
            "interaction" => typeof(InteractionPage),
            "behavior" => typeof(BehaviorPage),
            "about" => typeof(AboutPage),
            _ => null
        };

        public void NavigateToPage(string tag) => NavigateByTag(tag);

        private void NavigateByTag(string tag)
        {
            var pageType = PageTypeForTag(tag);
            if (pageType != null && ContentFrame.CurrentSourcePageType != pageType)
                ContentFrame.Navigate(pageType);
        }

        private void NavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
        {
            if (args.InvokedItemContainer?.Tag is string tag)
                NavigateByTag(tag);
        }

        private void ContentFrame_Navigated(object sender, Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            foreach (var item in NavView.MenuItems)
            {
                if (item is NavigationViewItem navItem &&
                    PageTypeForTag(navItem.Tag?.ToString()) == e.SourcePageType)
                {
                    NavView.SelectedItem = navItem;
                    break;
                }
            }
        }

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            var resources = new ResourceLoader();
            TitleBarArea.Title = Package.Current.DisplayName;
            TitleBarArea.Subtitle = resources.GetString("SettingsTitle/Text");
            TitleBarArea.Icon = WindowIconAssetResolver.CreateTitleBarIcon("DesktopSettings");
            ApplySettings();

            if (Content is FrameworkElement root)
            {
                root.ActualThemeChanged -= AppThemeManager.OnActualThemeChanged;
                root.ActualThemeChanged += AppThemeManager.OnActualThemeChanged;
            }
        }

        public void ApplySettings()
        {
            try
            {
                string position = AppSettingsStore.PanePosition;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ApplySettings Error: {ex.Message}");
            }
        }

        private static int _minWidth, _minHeight;

        private static void SetMinWindowSize(IntPtr hwnd, int minWidth, int minHeight)
        {
            _minWidth = minWidth;
            _minHeight = minHeight;
            var callback = new SUBCLASSPROC(SubclassProc);
            _subclassProc = callback;
            SetWindowSubclass(hwnd, callback, 0, 0);
        }

        private static nuint SubclassProc(IntPtr hwnd, uint message, nuint wParam, nint lParam,
            nuint subclassId, nuint refData)
        {
            if (message == 0x0024)
            {
                double dpi = GetDpiForWindow(hwnd) / 96.0;
                var info = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                info.ptMinTrackSize.x = (int)(_minWidth * dpi);
                info.ptMinTrackSize.y = (int)(_minHeight * dpi);
                Marshal.StructureToPtr(info, lParam, true);
            }

            return DefSubclassProc(hwnd, message, wParam, lParam);
        }

        private delegate nuint SUBCLASSPROC(IntPtr hwnd, uint message, nuint wParam, nint lParam,
            nuint subclassId, nuint refData);

        [DllImport("comctl32.dll")]
        private static extern bool SetWindowSubclass(IntPtr hwnd, SUBCLASSPROC callback, nuint subclassId, nuint refData);
        [DllImport("comctl32.dll")]
        private static extern nuint DefSubclassProc(IntPtr hwnd, uint message, nuint wParam, nint lParam);
        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int x, y; }
    }
}
