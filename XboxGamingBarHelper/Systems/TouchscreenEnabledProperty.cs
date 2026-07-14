using Shared.Enums;
using XboxGamingBarHelper.Core;

namespace XboxGamingBarHelper.Systems
{
    // Enable/disable the built-in touch screen digitizer (SetupAPI device disable - see
    // XboxGamingBarHelper.Windows.SetupApi). Persisted across restarts - this is a deliberate
    // user preference (e.g. "always play with the controller, avoid accidental touches"),
    // not transient UI state.
    internal class TouchscreenEnabledProperty : HelperProperty<bool, SystemManager>
    {
        public TouchscreenEnabledProperty(bool initialValue, SystemManager inManager)
            : base(initialValue, null, Function.TouchscreenEnabled, inManager)
        {
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);
            Manager?.SetTouchscreenEnabled(Value);
        }
    }
}
