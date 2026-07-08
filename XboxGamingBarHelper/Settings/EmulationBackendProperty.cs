using Shared.Enums;
using XboxGamingBarHelper.Core;

namespace XboxGamingBarHelper.Settings
{
    /// <summary>
    /// Property to select controller emulation backend.
    /// Currently a binary toggle: false = Legacy ViGEm (deprecated), true = VIIPER (default).
    /// Global setting, persisted to LocalSettings.
    /// </summary>
    internal class EmulationBackendProperty : HelperProperty<bool, SettingsManager>
    {
        private const string SettingsKey = "EmulationBackend";

        public EmulationBackendProperty(SettingsManager inManager)
            : base(LoadFromSettings(), null, Function.Settings_EmulationBackend, inManager)
        {
            Logger.Info($"EmulationBackend loaded: {Backend}");
        }

        private static bool LoadFromSettings()
        {
            // ViGEm retirement FINAL: the legacy ViGEm backend is unreachable —
            // VIIPER is the only emulation backend. Stored legacy choices are
            // intentionally ignored (with a log so migrated users' reports are
            // explainable); ViiperEmulationManager.ApplyBackend(true) keeps the
            // legacy manager permanently suppressed via SetSuppressedByViiper.
            // Migration prerequisites shipped ahead of this: one-click usbip
            // installer + setup-warnings banner (phase 2 stage A).
            if (LocalSettingsHelper.TryGetValue<int>(SettingsKey, out var value)
                && value != (int)EmulationBackend.Viiper)
            {
                Logger.Warn("EmulationBackend: stored Legacy choice ignored — the ViGEm backend is retired, using VIIPER. Install usbip-win2 if controller emulation stays offline.");
            }
            return true;
        }

        private void SaveToSettings()
        {
            LocalSettingsHelper.SetValue(SettingsKey, Value ? (int)EmulationBackend.Viiper : (int)EmulationBackend.Legacy);
            Logger.Debug($"EmulationBackend saved: {Backend}");
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);
            Logger.Info($"Emulation backend changed to {Backend}");
            SaveToSettings();

            // Clamp: the legacy backend is retired. A stale widget (or old
            // LocalSettings echo) pushing Legacy is bounced straight back so
            // the mutual-exclusion suppression of the legacy manager never
            // lifts. One-hop recursion: the SetValue below re-enters this
            // method once with Value=true and stops.
            if (!Value)
            {
                Logger.Warn("EmulationBackend: Legacy requested but the ViGEm backend is retired — re-asserting VIIPER");
                SetValue(true);
            }
        }

        public EmulationBackend Backend => Value ? EmulationBackend.Viiper : EmulationBackend.Legacy;
    }
}
