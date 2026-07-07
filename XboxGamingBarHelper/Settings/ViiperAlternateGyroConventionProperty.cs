using Shared.Enums;
using XboxGamingBarHelper.Core;

namespace XboxGamingBarHelper.Settings
{
    /// <summary>
    /// Alternate gyro convention. Persisted globally. Affects gyro only — accel is
    /// always pass-through so gravity stays in the SDL-correct direction for
    /// sensor fusion regardless of the chosen gyro mode. Per-target defaults are
    /// tuned for actual Steam usage; ON gives the alternate.
    ///
    /// false (default):
    ///   DS4 / DualSense / DualSenseEdge: wire yaw=+gz, roll=-gy (verified native gyro
    ///     aim 2026-05-31 on LeGo2 Mixed source).
    ///   Steam Deck / Switch Pro: pre-rotated 90deg-about-X gyro (wire yaw=-gz,
    ///     roll=+gy). Matches the older "frame-corrected" layout that real Steam
    ///     reads when it bypasses SDL_GetGamepadSensorData; verified working in
    ///     Steam 2026-06-01 after pass-through default broke gyro in-game.
    ///
    /// true:
    ///   DS4 / DualSense / DualSenseEdge: wire yaw=-gz, roll=+gy. Upstream / legacy
    ///     polarity some Steam Input gyro-to-mouse mappings prefer.
    ///   Steam Deck / Switch Pro: plain pass-through. SDL3's HIDAPI drivers apply
    ///     their own (X,Z,-Y) axis remap, so this exposes a 1:1 frame in
    ///     SdlGyroTester [M] mode. Useful when the downstream consumer is the
    ///     SDL3 sensor pipeline rather than direct HID.
    /// </summary>
    internal class ViiperAlternateGyroConventionProperty : HelperProperty<bool, SettingsManager>
    {
        public const bool Default = false;

        // SettingsKey preserved from the original DS4-only flag name so existing
        // users keep their chosen value across the rename.
        private const string SettingsKey = "ViiperDs4LegacyGyroSigns";

        public ViiperAlternateGyroConventionProperty(SettingsManager inManager)
            : base(LoadFromSettings(), null, Function.Viiper_AlternateGyroConvention, inManager)
        {
            Logger.Info($"ViiperAlternateGyroConvention loaded: {Value}");
        }

        private static bool LoadFromSettings()
        {
            if (LocalSettingsHelper.TryGetValue<bool>(SettingsKey, out var value))
            {
                return value;
            }
            return Default;
        }

        private void SaveToSettings()
        {
            LocalSettingsHelper.SetValue(SettingsKey, Value);
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);
            Logger.Info($"ViiperAlternateGyroConvention changed: {Value}");
            SaveToSettings();
        }
    }
}
