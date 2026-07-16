using Shared.Enums;
using XboxGamingBarHelper.Core;

namespace XboxGamingBarHelper.Power
{
    /// <summary>
    /// Property for the "turn off display after" idle timeout, in seconds (0 = never).
    /// Two instances exist (AC/DC); each writes only its own side of the active Windows
    /// power plan's Display subgroup.
    /// </summary>
    internal class DisplayTimeoutProperty : HelperProperty<int, PowerManager>
    {
        private readonly bool isAC;

        public DisplayTimeoutProperty(int inValue, PowerManager inManager, bool isAC, Function inFunction)
            : base(inValue, null, inFunction, inManager)
        {
            this.isAC = isAC;
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);

            PowerManager.SetDisplayTimeoutSeconds(isAC, (uint)Value);
        }
    }
}
