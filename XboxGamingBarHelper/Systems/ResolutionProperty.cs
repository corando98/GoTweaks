using Shared.Data;
using Shared.Enums;
using XboxGamingBarHelper.Core;
using XboxGamingBarHelper.Windows;

namespace XboxGamingBarHelper.Systems
{
    internal class ResolutionProperty : HelperProperty<string, SystemManager>, IHardwareApplyResult
    {
        public bool LastApplySucceeded { get; private set; } = true;
        public string LastApplyFailureReason { get; private set; }

        public ResolutionProperty(string inValue, SystemManager inManager) : base(inValue, null, Function.Resolution, inManager)
        {
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);

            LastApplySucceeded = User32.SetResolutionTo(Value);
            LastApplyFailureReason = LastApplySucceeded ? null : $"Resolution {Value} could not be applied.";
        }
    }
}
