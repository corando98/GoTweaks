using Shared.Enums;
using Windows.UI.Xaml.Controls;

namespace XboxGamingBar.Data
{
    /// <summary>
    /// Property to trigger HidHide installation.
    /// Write "install" to trigger the download + install on the helper side; the
    /// helper responds with a fresh HidHideInstalled state when it finishes.
    /// </summary>
    internal class InstallHidHideProperty : WidgetProperty<string>
    {
        private readonly Page owner;

        public InstallHidHideProperty(Page inOwner) : base("", null, Function.InstallHidHide)
        {
            owner = inOwner;
        }

        public void TriggerInstall()
        {
            Logger.Info("Triggering HidHide installation...");
            SetValue("install");
        }
    }
}
