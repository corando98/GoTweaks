using Shared.Enums;
using Windows.UI.Xaml.Controls;

namespace XboxGamingBar.Data
{
    /// <summary>
    /// Alternate gyro convention. Single toggle, target-aware behavior. Gyro only —
    /// accel stays pass-through. Per-target defaults are tuned for actual Steam
    /// usage; ON gives the alternate.
    ///
    /// off (default):
    ///   DS4 / DualSense / DualSenseEdge: yaw=+gz, roll=-gy.
    ///   Steam Deck / Switch Pro: pre-rotated 90deg-about-X gyro (frame-corrected,
    ///     matches Steam's direct-HID reading).
    ///
    /// on:
    ///   DS4 / DualSense / DualSenseEdge: yaw=-gz, roll=+gy.
    ///   Steam Deck / Switch Pro: plain pass-through (1:1 in SdlGyroTester via
    ///     SDL3's internal driver remap).
    /// </summary>
    internal class ViiperAlternateGyroConventionProperty : WidgetToggleProperty
    {
        public ViiperAlternateGyroConventionProperty(ToggleSwitch inUI, Page inOwner)
            : base(false, Function.Viiper_AlternateGyroConvention, inUI, inOwner)
        {
        }
    }
}
