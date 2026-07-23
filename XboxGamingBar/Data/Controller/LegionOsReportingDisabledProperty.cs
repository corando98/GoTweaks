using Shared.Enums;

namespace XboxGamingBar.Data
{
    // Firmware-backed Legion Go 2 control, synced bidirectionally with the helper
    // (values read back from the controller; sets apply to hardware).
    internal class LegionOsReportingDisabledProperty : WidgetProperty<bool>
    {
        public LegionOsReportingDisabledProperty() : base(false, null, Function.LegionOsReportingDisabled)
        {
        }
    }
}
