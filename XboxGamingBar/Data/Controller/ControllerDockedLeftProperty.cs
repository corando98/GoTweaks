using Shared.Enums;

namespace XboxGamingBar.Data
{
    // Physical dock state of the left half, synced from the helper. Distinct from
    // ControllerConnectedLeft (= linked): a detached-but-wirelessly-active half is
    // Connected=true, Docked=false, and the Controller Information card renders it
    // as "Detached" while still showing its battery.
    internal class ControllerDockedLeftProperty : WidgetProperty<bool>
    {
        public ControllerDockedLeftProperty() : base(false, null, Function.ControllerDockedLeft)
        {
        }
    }
}
