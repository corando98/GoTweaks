using Shared.Enums;

namespace XboxGamingBar.Data
{
    // Firmware-backed Legion Go 2 control, synced bidirectionally with the helper
    // (values read back from the controller; sets apply to hardware).
    internal class LegionGamepadModeSelectProperty : WidgetProperty<int>
    {
        public LegionGamepadModeSelectProperty() : base(0, null, Function.LegionGamepadModeSelect)
        {
        }
    }
}
