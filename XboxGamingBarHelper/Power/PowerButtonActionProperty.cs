using Shared.Enums;
using XboxGamingBarHelper.Core;

namespace XboxGamingBarHelper.Power
{
    /// <summary>
    /// Property for what the physical power button does: 0=Do nothing, 1=Sleep,
    /// 2=Hibernate, 3=Shut down. Two instances exist (AC/DC); each writes only its
    /// own side of the active Windows power plan's Power/Sleep buttons policy.
    /// </summary>
    internal class PowerButtonActionProperty : HelperProperty<int, PowerManager>
    {
        private readonly bool isAC;

        public PowerButtonActionProperty(int inValue, PowerManager inManager, bool isAC, Function inFunction)
            : base(inValue, null, inFunction, inManager)
        {
            this.isAC = isAC;
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);

            PowerManager.SetPowerButtonAction(isAC, (uint)Value);
        }
    }
}
