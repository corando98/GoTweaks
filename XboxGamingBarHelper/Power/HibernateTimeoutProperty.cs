using Shared.Enums;
using XboxGamingBarHelper.Core;
using XboxGamingBarHelper.Settings;

namespace XboxGamingBarHelper.Power
{
    /// <summary>
    /// Idle-to-hibernate timeout, in minutes (0 = disabled). Entirely GoTweaks-owned -
    /// unlike Power Button action and Display Timeout, Windows does not expose a
    /// reliable "hibernate after X" idle timer via the standard power-plan API, so this
    /// is driven by the helper's own idle poll (see Program.HibernateTimeout.cs) rather
    /// than PowrProf. Persisted to LocalSettings; two instances exist (AC/DC).
    /// </summary>
    internal class HibernateTimeoutProperty : HelperProperty<int, PowerManager>
    {
        private readonly string settingsKey;

        public HibernateTimeoutProperty(PowerManager inManager, bool isAC, Function inFunction)
            : base(LoadFromSettings(isAC), null, inFunction, inManager)
        {
            settingsKey = SettingsKeyFor(isAC);
        }

        private static string SettingsKeyFor(bool isAC) => isAC ? "HibernateTimeoutMinutesAC" : "HibernateTimeoutMinutesDC";

        private static int LoadFromSettings(bool isAC)
        {
            if (LocalSettingsHelper.TryGetValue<int>(SettingsKeyFor(isAC), out var value))
            {
                return value;
            }
            return 0; // Never - opt-in only, no surprise hibernation on a fresh install
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);

            LocalSettingsHelper.SetValue(settingsKey, Value);
            Logger.Info($"Hibernate Timeout ({settingsKey}) set to {Value} minute(s).");
        }
    }
}
