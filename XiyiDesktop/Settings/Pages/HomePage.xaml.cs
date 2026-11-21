using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace XiyiDesktop.Pages
{
    public sealed partial class HomePage : Page
    {
        public HomePage()
        {
            this.InitializeComponent();
        }

        private void OpenDesktop_Click(object sender, RoutedEventArgs e)
        {
            App.ShowDesktopWindow();
        }
    }
}
