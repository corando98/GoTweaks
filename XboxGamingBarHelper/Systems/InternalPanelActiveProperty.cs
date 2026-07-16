using Shared.Enums;
using XboxGamingBarHelper.Core;
using XboxGamingBarHelper.Windows;

namespace XboxGamingBarHelper.Systems
{
    // Read-only status: is the built-in panel the active display right now? False when docked
    // with only an external monitor active - the widget grays out Resolution / Refresh Rate /
    // Rotation, which only make sense against the internal panel. Re-read on every
    // RefreshDisplaySettings call (dock/undock, wake). Fails open (true) if the underlying
    // QueryDisplayConfig call can't determine the state, so a query failure never blocks
    // functionality that worked fine before this property existed.
    internal class InternalPanelActiveProperty : HelperProperty<bool, SystemManager>
    {
        public InternalPanelActiveProperty(SystemManager inManager)
            : base(User32.IsInternalPanelActive() ?? true, null, Function.InternalPanelActive, inManager)
        {
            Logger.Info($"InternalPanelActive seeded: {Value}");
        }
    }
}
