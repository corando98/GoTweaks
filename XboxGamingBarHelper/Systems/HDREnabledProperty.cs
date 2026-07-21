using Shared.Data;
using Shared.Enums;
using XboxGamingBarHelper.Core;
using XboxGamingBarHelper.Windows;

namespace XboxGamingBarHelper.Systems
{
    internal class HDREnabledProperty : HelperProperty<bool, SystemManager>, IHardwareApplyResult
    {
        public bool LastApplySucceeded { get; private set; } = true;
        public string LastApplyFailureReason { get; private set; }

        public HDREnabledProperty(bool inValue, SystemManager inManager) : base(inValue, null, Function.HDREnabled, inManager)
        {
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);

            LastApplySucceeded = User32.SetHDREnabled(Value);
            LastApplyFailureReason = LastApplySucceeded ? null : $"HDR {(Value ? "on" : "off")} could not be applied.";
        }
    }
}
