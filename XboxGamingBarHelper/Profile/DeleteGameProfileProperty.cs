using NLog;
using Shared.Enums;
using XboxGamingBarHelper.Core;

namespace XboxGamingBarHelper.Profile
{
    /// <summary>
    /// Property that allows the widget to delete game profiles.
    /// When the widget sets a game name, the helper deletes that profile's XML file.
    /// </summary>
    internal class DeleteGameProfileProperty : HelperProperty<string, ProfileManager>
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public DeleteGameProfileProperty(ProfileManager inManager)
            : base("", null, Function.DeleteGameProfile, inManager)
        {
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);

            // When widget sets a game name, delete that profile
            if (!string.IsNullOrEmpty(Value))
            {
                Logger.Info($"DeleteGameProfile request received for: {Value}");
                bool deleted = Manager.DeleteProfile(Value);

                if (deleted)
                {
                    Logger.Info($"Successfully deleted profile for: {Value}");
                }
                else
                {
                    Logger.Warn($"Failed to delete profile for: {Value} (not found)");
                }

                // Reset the trigger to empty (silently, no echo) so deleting the SAME game
                // name again later in this helper session isn't dropped by the equal-value
                // dedupe in GenericProperty.SetValue.
                SetValueSilent("");
            }
        }
    }
}
