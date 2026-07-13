using Shared.Enums;
using XboxGamingBarHelper.Core;

namespace XboxGamingBarHelper.Settings
{
    /// <summary>
    /// Which emulated-controller button the Legion "Desktop" front button acts as while
    /// controller emulation is active. String value: none|guide|touchpad|share|options|l3|r3.
    /// Resolved per emulated type in the VIIPER forwarder. Default "guide" matches the prior
    /// hardcoded behavior (Desktop -> PS/Guide).
    /// </summary>
    internal class ViiperDesktopButtonTargetProperty : HelperProperty<string, SettingsManager>
    {
        public const string Default = "guide";
        private const string SettingsKey = "ViiperDesktopButtonTarget";

        public ViiperDesktopButtonTargetProperty(SettingsManager inManager)
            : base(LoadFromSettings(), null, Function.Viiper_DesktopButtonTarget, inManager)
        {
            Logger.Info($"ViiperDesktopButtonTarget loaded: {Value}");
        }

        private static string LoadFromSettings()
        {
            if (LocalSettingsHelper.TryGetValue<string>(SettingsKey, out var value) && !string.IsNullOrEmpty(value))
                return value;
            return Default;
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);
            Logger.Info($"ViiperDesktopButtonTarget changed: {Value}");
            LocalSettingsHelper.SetValue(SettingsKey, Value ?? Default);
        }
    }

    /// <summary>
    /// Which emulated-controller button the Legion "Page" front button acts as while controller
    /// emulation is active. String value: none|guide|touchpad|share|options|l3|r3. Default
    /// "touchpad" matches the prior hardcoded behavior (Page -> Touchpad click).
    /// </summary>
    internal class ViiperPageButtonTargetProperty : HelperProperty<string, SettingsManager>
    {
        public const string Default = "touchpad";
        private const string SettingsKey = "ViiperPageButtonTarget";

        public ViiperPageButtonTargetProperty(SettingsManager inManager)
            : base(LoadFromSettings(), null, Function.Viiper_PageButtonTarget, inManager)
        {
            Logger.Info($"ViiperPageButtonTarget loaded: {Value}");
        }

        private static string LoadFromSettings()
        {
            if (LocalSettingsHelper.TryGetValue<string>(SettingsKey, out var value) && !string.IsNullOrEmpty(value))
                return value;
            return Default;
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);
            Logger.Info($"ViiperPageButtonTarget changed: {Value}");
            LocalSettingsHelper.SetValue(SettingsKey, Value ?? Default);
        }
    }
}
