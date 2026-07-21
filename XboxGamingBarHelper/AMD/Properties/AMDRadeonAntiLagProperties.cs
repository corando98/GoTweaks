using Shared.Enums;
using XboxGamingBarHelper.Core;

namespace XboxGamingBarHelper.AMD.Properties
{
    internal class AMDRadeonAntiLagSupportedProperty : HelperProperty<bool, AMDManager>
    {
        public AMDRadeonAntiLagSupportedProperty(bool inValue, AMDManager inManager) : base(inValue, null, Function.AMDRadeonAntiLagSupported, inManager)
        {
        }
    }

    internal class AMDRadeonAntiLagEnabledProperty : HelperProperty<bool, AMDManager>
    {
        public AMDRadeonAntiLagEnabledProperty(bool inValue, AMDManager inManager) : base(inValue, null, Function.AMDRadeonAntiLagEnabled, inManager)
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
                Manager.AMD3DSettingsChangedListener?.NotifyAntiLagChanged();
                Manager.AMDRadeonAntiLagSetting.SetEnabled(Value);
            }
            return result;
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);

            // Start cooldown before writing, same as AFMF/RSR/RIS - without this the native ADLX
            // callback (AMD3DSettingsChangedListener) can read back a stale pre-commit value
            // immediately after our own write and undo it.
            Manager.AMD3DSettingsChangedListener?.NotifyAntiLagChanged();
            Manager.AMDRadeonAntiLagSetting.SetEnabled(Value);
        }
    }
}
