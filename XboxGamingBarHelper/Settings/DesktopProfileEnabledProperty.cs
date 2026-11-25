using Shared.Enums;
using XboxGamingBarHelper.Core;

namespace XboxGamingBarHelper.Settings
{
    /// <summary>
    /// "Desktop / not in-game" profile toggle (off by default). When on, the helper applies
    /// desktop.xml whenever no game is running, and settings changed while on the desktop are
    /// saved there instead of the global profile. See Program.SyncBaseProfile.
    /// </summary>
    internal class DesktopProfileEnabledProperty : HelperProperty<bool, SettingsManager>
    {
        private const string SettingsKey = "DesktopProfileEnabled";

        public DesktopProfileEnabledProperty(SettingsManager inManager)
            : base(LocalSettingsHelper.TryGetValue<bool>(SettingsKey, out var v) && v, null, Function.DesktopProfileEnabled, inManager)
        {
            Logger.Info($"DesktopProfileEnabled loaded: {Value}");
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);
            Logger.Info($"DesktopProfileEnabled changed: {Value}");
            LocalSettingsHelper.SetValue(SettingsKey, Value);
        }
    }
}
