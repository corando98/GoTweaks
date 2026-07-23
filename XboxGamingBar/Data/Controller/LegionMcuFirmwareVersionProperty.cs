using Shared.Enums;

namespace XboxGamingBar.Data
{
    // Receiver/MCU firmware version string (e.g. "0260422A"), synced from the helper's
    // GET_VERSION_DATA read. Shown under the controller firmware in the info card.
    internal class LegionMcuFirmwareVersionProperty : WidgetProperty<string>
    {
        public LegionMcuFirmwareVersionProperty() : base("", null, Function.LegionMcuFirmwareVersion)
        {
        }
    }
}
