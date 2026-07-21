using Shared.Data;
using Shared.Enums;
using XboxGamingBarHelper.Core;

namespace XboxGamingBarHelper.RTSS
{
    internal class FPSLimitProperty : HelperProperty<int, RTSSManager>, IHardwareApplyResult
    {
        public bool LastApplySucceeded { get; private set; } = true;
        public string LastApplyFailureReason { get; private set; }

        public FPSLimitProperty(RTSSManager inManager) : base(0, null, Function.FPSLimit, inManager)
        {
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);

            LastApplySucceeded = RTSSFPSLimiter.SetFPSLimit(Value);
            LastApplyFailureReason = LastApplySucceeded ? null : "FPS limit could not be applied (is RTSS installed and running?).";
        }
    }
}
