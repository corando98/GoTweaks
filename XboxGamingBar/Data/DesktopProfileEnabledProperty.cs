using Shared.Enums;
using Windows.UI.Xaml.Controls;

namespace XboxGamingBar.Data
{
    /// <summary>
    /// "Desktop / not in-game" profile toggle. Off by default; the helper switches the base
    /// profile to desktop.xml while no game is running.
    /// </summary>
    internal class DesktopProfileEnabledProperty : WidgetToggleProperty
    {
        public DesktopProfileEnabledProperty(ToggleSwitch inUI, Page inOwner)
            : base(false, Function.DesktopProfileEnabled, inUI, inOwner)
        {
        }
    }
}
