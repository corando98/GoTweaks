using Shared.Enums;
using XboxGamingBarHelper.Core;

namespace XboxGamingBarHelper.Settings
{
    /// <summary>
    /// Gyro Tuning bundle for one IMU sensor (gyroscope or accelerometer), stored PER gyro
    /// source because the two Legion halves have physically mirrored IMUs and each source
    /// can need its own axis map. Format is "Left=csv|Right=csv|Mixed=csv|Handheld=csv"
    /// where each csv is "mapX,mapY,mapZ,invX,invY,invZ" (map X|Y|Z = which source axis feeds
    /// the emulated device's output channel, inv 0|1 = negate). A missing entry or empty
    /// bundle resolves to identity ("X,Y,Z,0,0,0"), a no-op over the hardcoded per-target
    /// frame. Selection of the active source's entry lives in ViiperEmulationManager.
    ///
    /// Two instances are wired in SettingsManager (gyro + accel), each persisted under its
    /// own LocalSettings key. We only round-trip the string here; the manager tolerates any
    /// malformed entry by falling back to identity, so validation is intentionally light.
    /// </summary>
    internal class ViiperTuningProperty : HelperProperty<string, SettingsManager>
    {
        public const string Identity = "";  // empty bundle -> identity for every source
        private readonly string settingsKey;

        public ViiperTuningProperty(SettingsManager inManager, Function function, string inSettingsKey)
            : base(LoadFromSettings(inSettingsKey), null, function, inManager)
        {
            settingsKey = inSettingsKey;
            Logger.Info($"{function} loaded: {Value}");
        }

        private static string LoadFromSettings(string key)
        {
            if (LocalSettingsHelper.TryGetValue<string>(key, out var value) && value != null)
            {
                return value;
            }
            return Identity;
        }

        private void SaveToSettings()
        {
            LocalSettingsHelper.SetValue(settingsKey, Value ?? Identity);
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);
            Logger.Info($"{Function} changed: {Value}");
            SaveToSettings();
        }
    }
}
