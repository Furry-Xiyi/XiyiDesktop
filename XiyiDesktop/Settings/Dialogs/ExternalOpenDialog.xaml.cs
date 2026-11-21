using Microsoft.UI.Xaml.Controls;

namespace XiyiDesktop.Dialogs
{
    public sealed partial class ExternalOpenDialog : ContentDialog
    {
        public bool UserConfirmed { get; private set; }

        public ExternalOpenDialog()
        {
            this.InitializeComponent();
        }

        private void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            UserConfirmed = true;
        }

        private void ContentDialog_SecondaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            UserConfirmed = false;
        }
    }
}
