using Shared.Data;
using Shared.Enums;
using XboxGamingBarHelper.Core;

namespace XboxGamingBarHelper.Power
{
    internal class CPUEPPProperty : HelperProperty<int, PowerManager>, IHardwareApplyResult
    {
        public bool LastApplySucceeded { get; private set; } = true;
        public string LastApplyFailureReason { get; private set; }

        // Track whether the user has explicitly changed this value
        // On fresh install, we read from system but don't write back unless user changes it
        private bool _hasUserModified = false;
        private int _initialValue;

        public CPUEPPProperty(int inValue, PowerManager inManager) : base(inValue, null, Function.CPUEPP, inManager)
        {
            _initialValue = inValue;
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);

            // Only apply to system if user has explicitly changed the value
            // This prevents overwriting system settings on first startup/sync
            if (!_hasUserModified)
            {
                // Check if the value actually changed from the initial system value
                if (Value != _initialValue)
                {
                    _hasUserModified = true;
                }
                else
                {
                    // Value is same as initial - this is just a sync, don't write to system.
                    // Nothing was attempted, so there is nothing to report as failed.
                    Logger.Debug($"CPU EPP: Skipping system write - value unchanged from initial ({Value})");
                    LastApplySucceeded = true;
                    LastApplyFailureReason = null;
                    return;
                }
            }

            bool dcOk = PowerManager.SetEppValue(false, (uint)Value);
            bool acOk = PowerManager.SetEppValue(true, (uint)Value);
            LastApplySucceeded = dcOk && acOk;
            LastApplyFailureReason = LastApplySucceeded ? null : "CPU EPP could not be applied.";
        }
    }
}
