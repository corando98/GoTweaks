using Shared.Enums;
using XboxGamingBarHelper.Core;

namespace XboxGamingBarHelper.Power
{
    internal class CPUBoostProperty : HelperProperty<bool, PowerManager>
    {
        // Track whether the user has explicitly changed this value
        // On fresh install, we read from system but don't write back unless user changes it
        private bool _hasUserModified = false;
        private bool _initialValue;

        public CPUBoostProperty(bool inValue, PowerManager inManager) : base(inValue, null, Function.CPUBoost, inManager)
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
                // Apply when the value diverged from the initial system read, OR when it arrives at
                // runtime with the widget connected (a genuine user/profile change). The incoming value
                // can legitimately EQUAL our stale initial when the real hardware state drifted in the
                // meantime (another control/profile/power-plan changed it), in which case the old
                // equality-only skip left boost in the wrong state. Only skip the pre-connect
                // fresh-startup echo of an unchanged value.
                if (Value != _initialValue || Program.IsPipeConnected)
                {
                    _hasUserModified = true;
                }
                else
                {
                    // Value is same as initial and no widget yet - fresh-startup sync, don't write to system
                    Logger.Debug($"CPU Boost: Skipping system write - value unchanged from initial ({Value})");
                    return;
                }
            }

            PowerManager.SetCpuBoostMode(false, Value);
            PowerManager.SetCpuBoostMode(true, Value);
        }
    }
}
