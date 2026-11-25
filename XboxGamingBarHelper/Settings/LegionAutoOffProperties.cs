using Shared.Enums;
using XboxGamingBarHelper.Core;

namespace XboxGamingBarHelper.Settings
{
    /// <summary>
    /// Persisted bool toggle for the attached-controller auto-off feature. See
    /// Devices/Libraries/Legion/AttachedControllerAutoOff.cs for the behavior.
    /// </summary>
    internal abstract class LegionAutoOffToggleProperty : HelperProperty<bool, SettingsManager>
    {
        private readonly string settingsKey;

        protected LegionAutoOffToggleProperty(string inSettingsKey, Function function, SettingsManager inManager)
            : base(Load(inSettingsKey), null, function, inManager)
        {
            settingsKey = inSettingsKey;
            Logger.Info($"{settingsKey} loaded: {Value}");
        }

        private static bool Load(string key)
        {
            // Off by default.
            return LocalSettingsHelper.TryGetValue<bool>(key, out var value) && value;
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);
            Logger.Info($"{settingsKey} changed: {Value}");
            LocalSettingsHelper.SetValue(settingsKey, Value);
        }
    }

    /// <summary>Auto-off while docked (an external display is active).</summary>
    internal class LegionAutoOffWhenDockedProperty : LegionAutoOffToggleProperty
    {
        public LegionAutoOffWhenDockedProperty(SettingsManager inManager)
            : base("LegionAutoOffWhenDocked", Function.LegionAutoOffWhenDocked, inManager)
        {
        }
    }

    /// <summary>Auto-off while NOT docked (no external display active).</summary>
    internal class LegionAutoOffWhenUndockedProperty : LegionAutoOffToggleProperty
    {
        public LegionAutoOffWhenUndockedProperty(SettingsManager inManager)
            : base("LegionAutoOffWhenUndocked", Function.LegionAutoOffWhenUndocked, inManager)
        {
        }
    }
}
