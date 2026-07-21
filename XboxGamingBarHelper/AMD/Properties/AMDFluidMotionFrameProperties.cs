using Shared.Data;
using Shared.Enums;
using XboxGamingBarHelper.Core;

namespace XboxGamingBarHelper.AMD.Properties
{
    internal class AMDFluidMotionFrameSupportedProperty : HelperProperty<bool, AMDManager>
    {
        public AMDFluidMotionFrameSupportedProperty(bool inValue, AMDManager inManager) : base(inValue, null, Function.AMDFluidMotionFrameSupported, inManager)
        {
        }
    }

    internal class AMDFluidMotionFrameEnabledProperty : HelperProperty<bool, AMDManager>, IHardwareApplyResult
    {
        public bool LastApplySucceeded { get; private set; } = true;
        public string LastApplyFailureReason { get; private set; }

        public AMDFluidMotionFrameEnabledProperty(bool inValue, AMDManager inManager) : base(inValue, null, Function.AMDFluidMotionFrameEnabled, inManager)
        {
        }

        public override bool SetValue(object newValue, long updatedTime = 0)
        {
            bool prev = Value;
            bool result = base.SetValue(newValue, updatedTime);
            // Display-tab fix: GenericProperty.SetValue's equality skip prevents
            // NotifyPropertyChanged from firing when our cached value matches
            // the incoming value — and therefore the SetEnabled call below in
            // NotifyPropertyChanged never reaches ADLX. The cache CAN drift
            // from the actual driver state (user toggled AFMF in Adrenalin
            // directly, a prior SetEnabled silently failed, etc.), so the
            // Display-tab toggle would persist the new value in the widget UI
            // but the driver would stay in its previous state. Per-game
            // profiles bypass this via ForceSetValue but the Display-tab path
            // hits SetValue. Push to driver here whenever the result was
            // accepted, regardless of whether the value changed.
            if (result && prev == Value)
            {
                Manager.AMD3DSettingsChangedListener?.NotifyAFMFChanged();
                ApplyToDriver();
            }
            return result;
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);

            // Notify the listener to start cooldown before we make the change
            // This prevents the listener from reading stale values when the driver callback fires
            Manager.AMD3DSettingsChangedListener?.NotifyAFMFChanged();

            ApplyToDriver();
        }

        private void ApplyToDriver()
        {
            LastApplySucceeded = Manager.AMDFluidMotionFrameSetting.SetEnabled(Value);
            LastApplyFailureReason = LastApplySucceeded ? null : "AFMF could not be applied.";
        }
    }

    internal class AMDFluidMotionFrameV1SupportedProperty : HelperProperty<bool, AMDManager>
    {
        public AMDFluidMotionFrameV1SupportedProperty(bool inValue, AMDManager inManager) : base(inValue, null, Function.AMDFluidMotionFrameV1Supported, inManager)
        {
        }
    }

    internal class AMDFluidMotionFrameAlgorithmProperty : HelperProperty<int, AMDManager>, IHardwareApplyResult
    {
        public bool LastApplySucceeded { get; private set; } = true;
        public string LastApplyFailureReason { get; private set; }

        public AMDFluidMotionFrameAlgorithmProperty(int inValue, AMDManager inManager) : base(inValue, null, Function.AMDFluidMotionFrameAlgorithm, inManager)
        {
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);
            Manager.AMD3DSettingsChangedListener?.NotifyAFMFChanged();
            ApplyToDriver();
        }

        private void ApplyToDriver()
        {
            LastApplySucceeded = Manager.AMDFluidMotionFrameSettingV1 != null
                && Manager.AMDFluidMotionFrameSettingV1.SetAlgorithm((ADLX_AFMF_ALGORITHM)Value);
            LastApplyFailureReason = LastApplySucceeded ? null : "AFMF algorithm could not be applied.";
        }
    }

    internal class AMDFluidMotionFrameSearchModeProperty : HelperProperty<int, AMDManager>, IHardwareApplyResult
    {
        public bool LastApplySucceeded { get; private set; } = true;
        public string LastApplyFailureReason { get; private set; }

        public AMDFluidMotionFrameSearchModeProperty(int inValue, AMDManager inManager) : base(inValue, null, Function.AMDFluidMotionFrameSearchMode, inManager)
        {
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);
            Manager.AMD3DSettingsChangedListener?.NotifyAFMFChanged();
            ApplyToDriver();
        }

        private void ApplyToDriver()
        {
            LastApplySucceeded = Manager.AMDFluidMotionFrameSettingV1 != null
                && Manager.AMDFluidMotionFrameSettingV1.SetSearchMode((ADLX_AFMF_SEARCH_MODE_TYPE)Value);
            LastApplyFailureReason = LastApplySucceeded ? null : "AFMF search mode could not be applied.";
        }
    }

    internal class AMDFluidMotionFramePerformanceModeProperty : HelperProperty<int, AMDManager>, IHardwareApplyResult
    {
        public bool LastApplySucceeded { get; private set; } = true;
        public string LastApplyFailureReason { get; private set; }

        public AMDFluidMotionFramePerformanceModeProperty(int inValue, AMDManager inManager) : base(inValue, null, Function.AMDFluidMotionFramePerformanceMode, inManager)
        {
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);
            Manager.AMD3DSettingsChangedListener?.NotifyAFMFChanged();
            ApplyToDriver();
        }

        private void ApplyToDriver()
        {
            LastApplySucceeded = Manager.AMDFluidMotionFrameSettingV1 != null
                && Manager.AMDFluidMotionFrameSettingV1.SetPerformanceMode((ADLX_AFMF_PERFORMANCE_MODE_TYPE)Value);
            LastApplyFailureReason = LastApplySucceeded ? null : "AFMF performance mode could not be applied.";
        }
    }

    internal class AMDFluidMotionFrameFastMotionResponseProperty : HelperProperty<int, AMDManager>, IHardwareApplyResult
    {
        public bool LastApplySucceeded { get; private set; } = true;
        public string LastApplyFailureReason { get; private set; }

        public AMDFluidMotionFrameFastMotionResponseProperty(int inValue, AMDManager inManager) : base(inValue, null, Function.AMDFluidMotionFrameFastMotionResponse, inManager)
        {
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);
            Manager.AMD3DSettingsChangedListener?.NotifyAFMFChanged();
            ApplyToDriver();
        }

        private void ApplyToDriver()
        {
            LastApplySucceeded = Manager.AMDFluidMotionFrameSettingV1 != null
                && Manager.AMDFluidMotionFrameSettingV1.SetFastMotionResponse((ADLX_AFMF_FAST_MOTION_RESP)Value);
            LastApplyFailureReason = LastApplySucceeded ? null : "AFMF fast motion response could not be applied.";
        }
    }
}
