using Shared.Enums;
using XboxGamingBarHelper.Core;

namespace XboxGamingBarHelper.Systems
{
    // Read-only status: is the built-in panel brightness controllable right now? False when the
    // internal panel is not in the active display config (e.g. docked to an external-only display)
    // — WMI brightness only controls the built-in panel, so the widget grays out + blocks the
    // slider rather than letting it silently no-op. Re-pushed on display config changes (#50).
    internal class PanelBrightnessSupportedProperty : HelperProperty<bool, SystemManager>
    {
        public PanelBrightnessSupportedProperty(SystemManager inManager)
            : base(BrightnessManager.IsSupported(), null, Function.PanelBrightnessSupported, inManager)
        {
            Logger.Info($"PanelBrightnessSupported seeded: {Value}");
        }
    }
}
