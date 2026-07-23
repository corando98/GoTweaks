using Shared.Enums;

namespace XboxGamingBar.Data
{
    // Controller input mode synced from the helper's firmware reads:
    // 0 = unknown, 1 = XInput, 2 = DInput, 3 = FPS (physical switch engaged).
    // Rendered as a pill on the Legion tab header.
    internal class LegionControllerInputModeProperty : WidgetProperty<int>
    {
        public LegionControllerInputModeProperty() : base(0, null, Function.LegionControllerInputMode)
        {
        }
    }
}
