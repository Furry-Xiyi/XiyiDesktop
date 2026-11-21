using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace XiyiDesktop.DesktopItems;

/// <summary>Creates per-flyout presenter styles so the acrylic backdrop is not shared across popup windows.</summary>
internal static class MenuFlyoutVisuals
{
    public static Style CreateAcrylicPresenterStyle()
    {
        var style = new Style(typeof(MenuFlyoutPresenter))
        {
            BasedOn = (Style)Application.Current.Resources["AcrylicMenuFlyoutPresenterBaseStyle"]
        };
        style.Setters.Add(new Setter(
            MenuFlyoutPresenter.SystemBackdropProperty,
            new DesktopAcrylicBackdrop()));
        return style;
    }
}
