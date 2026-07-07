using Shared.Enums;
using Windows.UI.Xaml.Controls;

namespace XboxGamingBar.Data
{
    /// <summary>
    /// Widget property for controller emulation backend selection.
    /// Backed by a ToggleSwitch: Off = Legacy ViGEm (deprecated), On = VIIPER.
    /// Default matches the helper's VIIPER-default (ViGEm retirement phase 1)
    /// so the toggle doesn't visibly flip during initial sync on fresh installs.
    /// </summary>
    internal class EmulationBackendProperty : WidgetToggleProperty
    {
        public EmulationBackendProperty(ToggleSwitch inUI, Page inOwner)
            : base(true, Function.Settings_EmulationBackend, inUI, inOwner)
        {
        }

        /// <summary>
        /// Convenience: translate the bool to/from the enum.
        /// </summary>
        public EmulationBackend Backend
        {
            get => Value ? EmulationBackend.Viiper : EmulationBackend.Legacy;
        }
    }
}
