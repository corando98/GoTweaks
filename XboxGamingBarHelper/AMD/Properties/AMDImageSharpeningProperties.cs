using Shared.Enums;
using System;
using XboxGamingBarHelper.Core;

namespace XboxGamingBarHelper.AMD.Properties
{
    internal class AMDImageSharpeningSupportedProperty : HelperProperty<bool, AMDManager>
    {
        public AMDImageSharpeningSupportedProperty(bool inValue, AMDManager inManager) : base(inValue, null, Function.AMDImageSharpeningSupported, inManager)
        {
        }
    }

    internal class AMDImageSharpeningEnabledProperty : HelperProperty<bool, AMDManager>
    {
        public AMDImageSharpeningEnabledProperty(bool inValue, AMDManager inManager) : base(inValue, null, Function.AMDImageSharpeningEnabled, inManager)
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
                Manager.AMD3DSettingsChangedListener?.NotifyRISChanged();
                Manager.AMDImageSharpeningSetting.SetEnabled(Value);
            }
            return result;
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);

            // Notify the listener to start cooldown before we make the change
            // This prevents the listener from reading stale values when the driver callback fires
            Manager.AMD3DSettingsChangedListener?.NotifyRISChanged();

            Manager.AMDImageSharpeningSetting.SetEnabled(Value);
        }
    }

    internal class AMDImageSharpeningSharpnessProperty : HelperProperty<int, AMDManager>
    {
        public AMDImageSharpeningSharpnessProperty(int inValue, AMDManager inManager) : base(inValue, null, Function.AMDImageSharpeningSharpness, inManager)
        {
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);

            (int min, int max) = Manager.AMDImageSharpeningSetting.GetSharpnessRange();
            if (min == 0 && max == 100)
            {
                Manager.AMDImageSharpeningSetting.SetSharpness(Value);
            }
            else
            {
                Manager.AMDImageSharpeningSetting.SetSharpness((int)Math.Round(min + Value / 100.0f * (max - min)));
            }
        }
    }
}
