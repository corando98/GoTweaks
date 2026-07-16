using Shared.Enums;

namespace XboxGamingBar.Data
{
    internal class AutoTDPEnabledProperty : WidgetProperty<bool>
    {
        public AutoTDPEnabledProperty(bool inValue) : base(inValue, null, Function.AutoTDPEnabled)
        {
        }
    }

    internal class AutoTDPTargetFPSProperty : WidgetProperty<int>
    {
        public AutoTDPTargetFPSProperty(int inValue) : base(inValue, null, Function.AutoTDPTargetFPS)
        {
        }
    }

    internal class AutoTDPCurrentFPSProperty : WidgetProperty<int>
    {
        public AutoTDPCurrentFPSProperty(int inValue) : base(inValue, null, Function.AutoTDPCurrentFPS)
        {
        }
    }

    internal class AutoTDPMinTDPProperty : WidgetProperty<int>
    {
        public AutoTDPMinTDPProperty(int inValue) : base(inValue, null, Function.AutoTDPMinTDP)
        {
        }
    }

    internal class AutoTDPMaxTDPProperty : WidgetProperty<int>
    {
        public AutoTDPMaxTDPProperty(int inValue) : base(inValue, null, Function.AutoTDPMaxTDP)
        {
        }
    }

    internal class TDPLimitsProperty : WidgetProperty<string>
    {
        public TDPLimitsProperty(string inValue) : base(inValue, null, Function.TDPLimits)
        {
        }
    }

    internal class ForceDefaultGameProfileProperty : WidgetProperty<bool>
    {
        public ForceDefaultGameProfileProperty(bool inValue) : base(inValue, null, Function.ForceDefaultGameProfile)
        {
        }
    }

    internal class TDPBoostEnabledProperty : WidgetProperty<bool>
    {
        public TDPBoostEnabledProperty(bool inValue) : base(inValue, null, Function.TDPBoostEnabled)
        {
        }
    }

    internal class TDPBoostSPPTProperty : WidgetProperty<int>
    {
        public TDPBoostSPPTProperty(int inValue) : base(inValue, null, Function.TDPBoostSPPT)
        {
        }
    }

    internal class TDPBoostFPPTProperty : WidgetProperty<int>
    {
        public TDPBoostFPPTProperty(int inValue) : base(inValue, null, Function.TDPBoostFPPT)
        {
        }
    }

    // ML Mode properties for AutoTDP
    internal class AutoTDPUseMLModeProperty : WidgetProperty<bool>
    {
        public AutoTDPUseMLModeProperty(bool inValue) : base(inValue, null, Function.AutoTDPUseMLMode)
        {
        }
    }

    internal class AutoTDPMLStatusProperty : WidgetProperty<string>
    {
        public AutoTDPMLStatusProperty(string inValue) : base(inValue, null, Function.AutoTDPMLStatus)
        {
        }
    }

    internal class AutoTDPLearnedGameDataProperty : WidgetProperty<string>
    {
        public AutoTDPLearnedGameDataProperty(string inValue) : base(inValue, null, Function.AutoTDPLearnedGameData)
        {
        }
    }

    internal class AutoTDPResetMLProperty : WidgetProperty<bool>
    {
        public AutoTDPResetMLProperty(bool inValue) : base(inValue, null, Function.AutoTDPResetML)
        {
        }
    }

    internal class AutoTDPPauseWhenUnfocusedProperty : WidgetProperty<bool>
    {
        public AutoTDPPauseWhenUnfocusedProperty(bool inValue) : base(inValue, null, Function.AutoTDPPauseWhenUnfocused)
        {
        }
    }

    internal class AutoTDPControllerTypeProperty : WidgetProperty<int>
    {
        public AutoTDPControllerTypeProperty(int inValue) : base(inValue, null, Function.AutoTDPControllerType)
        {
        }
    }
}
