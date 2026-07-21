using Shared.Data;
using Shared.Enums;
using System;
using XboxGamingBarHelper.Core;

namespace XboxGamingBarHelper.AMD.Properties
{
    internal class AMDRadeonSuperResolutionSupportedProperty : HelperProperty<bool, AMDManager>
    {
        public AMDRadeonSuperResolutionSupportedProperty(bool inValue, AMDManager inManager) : base(inValue, null, Function.AMDRadeonSuperResolutionSupported, inManager)
        {
        }
    }

    internal class AMDRadeonSuperResolutionEnabledProperty : HelperProperty<bool, AMDManager>, IHardwareApplyResult
    {
        public bool LastApplySucceeded { get; private set; } = true;
        public string LastApplyFailureReason { get; private set; }

        public AMDRadeonSuperResolutionEnabledProperty(bool inValue, AMDManager inManager) : base(inValue, null, Function.AMDRadeonSuperResolutionEnabled, inManager)
        {
        }

        public override bool SetValue(object newValue, long updatedTime = 0)
        {
            bool prev = Value;
            bool result = base.SetValue(newValue, updatedTime);
            // Display-tab cache-drift fix (same as AMDFluidMotionFrameEnabledProperty):
            // GenericProperty.SetValue's equality skip stops NotifyPropertyChanged — and thus
            // the SetEnabled call — from firing when the cached value already equals the
            // incoming one. The cache CAN drift from the real driver state (toggled in Adrenalin
            // directly, a prior SetEnabled silently failed), leaving the widget toggle changed
            // while the driver stayed put. Push to the driver whenever the write was accepted,
            // regardless of whether the cached value changed.
            if (result && prev == Value)
            {
                Manager.AMD3DSettingsChangedListener?.NotifyRSRChanged();
                ApplyToDriver();
            }
            return result;
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);

            // Notify the listener to start cooldown before we make the change
            // This prevents the listener from reading stale values when the driver callback fires
            Manager.AMD3DSettingsChangedListener?.NotifyRSRChanged();

            ApplyToDriver();
        }

        private void ApplyToDriver()
        {
            LastApplySucceeded = Manager.AMDRadeonSuperResolutionSetting.SetEnabled(Value);
            LastApplyFailureReason = LastApplySucceeded ? null : "Radeon Super Resolution could not be applied.";
        }
    }

    internal class AMDRadeonSuperResolutionSharpnessProperty : HelperProperty<int, AMDManager>, IHardwareApplyResult
    {
        public bool LastApplySucceeded { get; private set; } = true;
        public string LastApplyFailureReason { get; private set; }

        public AMDRadeonSuperResolutionSharpnessProperty(int inValue, AMDManager inManager) : base(inValue, null, Function.AMDRadeonSuperResolutionSharpness, inManager)
        {
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);

            (int min, int max) = Manager.AMDRadeonSuperResolutionSetting.GetSharpnessRange();
            LastApplySucceeded = (min == 0 && max == 100)
                ? Manager.AMDRadeonSuperResolutionSetting.SetSharpness(Value)
                : Manager.AMDRadeonSuperResolutionSetting.SetSharpness((int)Math.Round(min + Value / 100.0f * (max - min)));
            LastApplyFailureReason = LastApplySucceeded ? null : "Radeon Super Resolution sharpness could not be applied.";
        }
    }
}
