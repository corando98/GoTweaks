using Shared.Enums;
using XboxGamingBarHelper.Core;

namespace XboxGamingBarHelper.Settings
{
    /// <summary>
    /// Reports whether HidHide is installed. Registry-backed sibling of the ad-hoc
    /// Get/Install handlers in Program.PipeHandlers: those answer single requests,
    /// but only registry properties are included in the widget's initial BatchGet —
    /// without this, the Setup tab's HidHide row would read "not installed" until
    /// something issued an explicit Get. Post-install refresh is pushed by the
    /// InstallHidHide handler itself.
    /// </summary>
    internal class HidHideInstalledProperty : HelperProperty<bool, SettingsManager>
    {
        public HidHideInstalledProperty(SettingsManager inManager)
            : base(Detect(), null, Function.HidHideInstalled, inManager)
        {
            Logger.Info($"HidHide installed: {Value}");
        }

        private static bool Detect()
        {
            try
            {
                return XboxGamingBarHelper.Labs.HidHideHelper.IsInstalled();
            }
            catch
            {
                return false;
            }
        }
    }
}
