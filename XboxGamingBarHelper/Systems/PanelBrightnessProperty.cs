using Shared.Enums;
using XboxGamingBarHelper.Core;

namespace XboxGamingBarHelper.Systems
{
    // Built-in display (panel) brightness, 0-100 %. Applies live to the OS via the WMI brightness
    // path (BrightnessManager -> WmiSetBrightness). Seeded from the real current brightness at
    // startup so the widget slider reflects reality on BatchGet. Not persisted here — panel
    // brightness is OS-owned state, we just read/write it. Optional Quick-tab slider (#50),
    // hidden by default and revealed under the Quick tab's Customize.
    internal class PanelBrightnessProperty : HelperProperty<int, SystemManager>
    {
        public PanelBrightnessProperty(SystemManager inManager)
            : base(SeedBrightness(), null, Function.PanelBrightness, inManager)
        {
            Logger.Info($"PanelBrightness seeded from hardware: {Value}%");
        }

        private static int SeedBrightness()
        {
            int b = BrightnessManager.GetBrightness();
            return b < 0 ? 0 : (b > 100 ? 100 : b);
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);
            // Apply to the built-in panel. When the change came from the widget slider,
            // SuppressRemoteSync stops the echo but the hardware apply still runs.
            BrightnessManager.SetBrightness(Value);
        }
    }
}
