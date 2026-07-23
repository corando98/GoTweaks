using Shared.Enums;

namespace XboxGamingBar.Data
{
    // Firmware-backed Legion Go 2 control, synced bidirectionally with the helper
    // (values read back from the controller; sets apply to hardware).
    internal class LegionRgbActiveProfileProperty : WidgetProperty<int>
    {
        public LegionRgbActiveProfileProperty() : base(0, null, Function.LegionRgbActiveProfile)
        {
        }
    }
}
