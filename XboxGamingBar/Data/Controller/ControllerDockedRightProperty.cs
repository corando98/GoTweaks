using Shared.Enums;

namespace XboxGamingBar.Data
{
    // Physical dock state of the right half - see ControllerDockedLeftProperty.
    internal class ControllerDockedRightProperty : WidgetProperty<bool>
    {
        public ControllerDockedRightProperty() : base(false, null, Function.ControllerDockedRight)
        {
        }
    }
}
