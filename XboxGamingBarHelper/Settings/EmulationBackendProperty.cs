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
            // Stored as int so future enum expansion stays compatible.
            if (LocalSettingsHelper.TryGetValue<int>(SettingsKey, out var value))
            {
                return value == (int)EmulationBackend.Viiper;
            }

            // No stored choice. The backend key is only written when the user
            // touches the toggle, so "no key" covers BOTH fresh installs AND
            // upgraders who used legacy CE under the old legacy-default. The
            // latter must NOT be silently flipped: they likely lack usbip-win2
            // and VIIPER would leave their working CE offline. An active CE
            // toggle (ControllerEmulationEnabled=true) is the tell.
            if (LocalSettingsHelper.TryGetValue<bool>("ControllerEmulationEnabled", out var ceEnabled) && ceEnabled)
            {
                Logger.Info("EmulationBackend: no stored choice but legacy CE is actively enabled — keeping Legacy (upgrade compat)");
                return false;
            }

            // Default to VIIPER (ViGEm retirement phase 1, mirroring Handheld
            // Companion 0.30's move). VIIPER eliminates the ViGEm suspend/resume
            // input-loss and XInput slot-0 recovery bug classes; without
            // usbip-win2 it stays offline gracefully and both the CE-tab install
            // card and the setup-warnings banner point the user at the driver.
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
        }

        public EmulationBackend Backend => Value ? EmulationBackend.Viiper : EmulationBackend.Legacy;
    }
}
