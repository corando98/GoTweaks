using Shared.Enums;
using Windows.UI.Xaml.Controls;

namespace XboxGamingBar.Data
{
    /// <summary>
    /// Turn the side-attached Legion controllers off while docked (external display active)
    /// and another controller is connected. Off by default; the helper owns the logic.
    /// </summary>
    internal class LegionAutoOffWhenDockedProperty : WidgetToggleProperty
    {
        public LegionAutoOffWhenDockedProperty(ToggleSwitch inUI, Page inOwner)
            : base(false, Function.LegionAutoOffWhenDocked, inUI, inOwner)
        {
        }
    }

    /// <summary>Same as <see cref="LegionAutoOffWhenDockedProperty"/>, for when not docked.</summary>
    internal class LegionAutoOffWhenUndockedProperty : WidgetToggleProperty
    {
        public LegionAutoOffWhenUndockedProperty(ToggleSwitch inUI, Page inOwner)
            : base(false, Function.LegionAutoOffWhenUndocked, inUI, inOwner)
        {
        }
    }
}
