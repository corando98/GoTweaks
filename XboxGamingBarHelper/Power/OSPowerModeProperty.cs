using Shared.Data;
using Shared.Enums;
using XboxGamingBarHelper.Core;

namespace XboxGamingBarHelper.Power
{
    /// <summary>
    /// Property for Windows 11 OS Power Mode (power slider).
    /// 0 = Best Power Efficiency, 1 = Balanced, 2 = Best Performance
    /// </summary>
    internal class OSPowerModeProperty : HelperProperty<int, PowerManager>, IHardwareApplyResult
    {
        public bool LastApplySucceeded { get; private set; } = true;
        public string LastApplyFailureReason { get; private set; }

        public OSPowerModeProperty(int inValue, PowerManager inManager) : base(inValue, null, Function.OSPowerMode, inManager)
        {
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);

            LastApplySucceeded = PowerManager.SetOSPowerMode(Value);
            LastApplyFailureReason = LastApplySucceeded ? null : $"OS Power Mode {Value} could not be applied.";
        }
    }
}
