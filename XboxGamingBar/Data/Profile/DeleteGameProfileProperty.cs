using Shared.Enums;

namespace XboxGamingBar.Data
{
    // Action trigger property. ForceSetValue(gameName) tells the helper to delete that
    // game's per-game profile XML (ProfileManager.DeleteProfile) so a deleted profile
    // doesn't leave an orphaned file the helper would re-apply when the game reappears.
    internal class DeleteGameProfileProperty : WidgetProperty<string>
    {
        public DeleteGameProfileProperty() : base("", null, Function.DeleteGameProfile)
        {
        }
    }
}
