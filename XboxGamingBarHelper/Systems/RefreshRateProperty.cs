using Shared.Data;
using Shared.Enums;
using XboxGamingBarHelper.Core;
using XboxGamingBarHelper.Windows;

namespace XboxGamingBarHelper.Systems
{
    internal class RefreshRateProperty : HelperProperty<int, SystemManager>, IHardwareApplyResult
    {
        public bool LastApplySucceeded { get; private set; } = true;
        public string LastApplyFailureReason { get; private set; }

        public RefreshRateProperty(int inValue, SystemManager inManager) : base(inValue, null, Function.RefreshRate, inManager)
        {
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);

            LastApplySucceeded = User32.SetRefreshRateTo(Value);
            LastApplyFailureReason = LastApplySucceeded ? null : $"Refresh rate {Value}Hz could not be applied.";
        }
    }
}
