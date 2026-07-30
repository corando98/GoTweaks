using Shared.Enums;

namespace XboxGamingBar.Data
{
    /// <summary>
    /// Read-only widget property mirroring whether HidHide is installed on the host.
    /// Synced from the helper; the Setup tab uses this for its status row.
    /// </summary>
    internal class HidHideInstalledProperty : WidgetProperty<bool>
    {
        public HidHideInstalledProperty() : base(false, null, Function.HidHideInstalled)
        {
        }
    }
}
