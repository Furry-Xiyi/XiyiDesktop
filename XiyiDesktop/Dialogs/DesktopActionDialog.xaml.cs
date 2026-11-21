using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace XiyiDesktop.Dialogs;

/// <summary>A localized, app-owned confirmation, rename, or error dialog.</summary>
public sealed partial class DesktopActionDialog : ContentDialog
{
    public DesktopActionDialog(
        string title,
        string message,
        string primaryButtonText,
        string closeButtonText,
        string? inputText = null,
        string? inputPlaceholder = null,
        bool showInput = false)
    {
        InitializeComponent();

        Title = title;
        MessageText.Text = message;
        PrimaryButtonText = primaryButtonText;
        CloseButtonText = closeButtonText;
        InputTextBox.Visibility = showInput ? Visibility.Visible : Visibility.Collapsed;
        InputTextBox.Text = inputText ?? string.Empty;
        InputTextBox.PlaceholderText = inputPlaceholder ?? string.Empty;
    }

    public string InputValue => InputTextBox.Text;
}
