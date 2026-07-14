using Shared.Enums;

namespace XboxGamingBar.Data
{
    /// <summary>
    /// Headless send-channel for the built-in touch screen on/off state (Quick Settings tile
    /// only - no dedicated settings-tab control). The helper is authoritative for the persisted
    /// value and applies it via SetupAPI device enable/disable; this just mirrors/sends the bool.
    /// </summary>
    internal class TouchscreenEnabledProperty : WidgetProperty<bool>
    {
        public TouchscreenEnabledProperty() : base(true, null, Function.TouchscreenEnabled)
        {
        }
    }
}
