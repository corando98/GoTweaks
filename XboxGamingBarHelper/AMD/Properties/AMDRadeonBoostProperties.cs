using Shared.Enums;
using System;
using XboxGamingBarHelper.Core;

namespace XboxGamingBarHelper.AMD.Properties
{
    internal class AMDRadeonBoostSupportedProperty : HelperProperty<bool, AMDManager>
    {
        public AMDRadeonBoostSupportedProperty(bool inValue, AMDManager inManager) : base(inValue, null, Function.AMDRadeonBoostSupported, inManager)
        {
        }
    }

    internal class AMDRadeonBoostEnabledProperty : HelperProperty<bool, AMDManager>
    {
        public AMDRadeonBoostEnabledProperty(bool inValue, AMDManager inManager) : base(inValue, null, Function.AMDRadeonBoostEnabled, inManager)
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
                Manager.AMDRadeonBoostSetting.SetEnabled(Value);
            }
            return result;
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);

            Manager.AMDRadeonBoostSetting.SetEnabled(Value);
        }
    }

    internal class AMDRadeonBoostResolutionProperty : HelperProperty<int, AMDManager>
    {
        public AMDRadeonBoostResolutionProperty(int inValue, AMDManager inManager) : base(inValue, null, Function.AMDRadeonBoostResolution, inManager)
        {
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);

            (int min, int max) = Manager.AMDRadeonBoostSetting.GetResolutionRange();
            Manager.AMDRadeonBoostSetting.SetResolution(Value == 0 ? min : max);
        }
    }
}
