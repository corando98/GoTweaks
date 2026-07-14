using Shared.Enums;
using XboxGamingBarHelper.Core;

namespace XboxGamingBarHelper.DefaultGameProfiles
{
    /// <summary>
    /// Property indicating whether a default game profile is available for the current game.
    /// </summary>
    internal class DefaultGameProfileAvailableProperty : HelperProperty<bool, DefaultGameProfileManager>
    {
        public DefaultGameProfileAvailableProperty(DefaultGameProfileManager manager)
            : base(false, null, Function.DefaultGameProfileAvailable, manager)
        {
        }
    }

    /// <summary>
    /// Property containing the serialized default game profile data.
    /// Format: XML serialized DefaultGameProfile struct.
    /// </summary>
    internal class DefaultGameProfileDataProperty : HelperProperty<string, DefaultGameProfileManager>
    {
        public DefaultGameProfileDataProperty(DefaultGameProfileManager manager)
            : base("", null, Function.DefaultGameProfileData, manager)
        {
        }
    }

    /// <summary>
    /// Property indicating whether the default profile is currently enabled for this game.
    /// Widget can toggle this to enable/disable applying the default profile.
    /// </summary>
    internal class DefaultGameProfileEnabledProperty : HelperProperty<bool, DefaultGameProfileManager>
    {
        public DefaultGameProfileEnabledProperty(DefaultGameProfileManager manager)
            : base(false, null, Function.DefaultGameProfileEnabled, manager)
        {
        }
    }

    /// <summary>
    /// Master enable for the Default Game Profile feature. When off, profiles are never
    /// looked up or applied. On undetected hardware, enabling falls back to Z1 Extreme
    /// (OMNI) profiles. (Function/property names kept for wire compatibility.)
    /// </summary>
    internal class ForceDefaultGameProfileProperty : HelperProperty<bool, DefaultGameProfileManager>
    {
        public ForceDefaultGameProfileProperty(DefaultGameProfileManager manager)
            : base(false, null, Function.ForceDefaultGameProfile, manager)
        {
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);

            // Notify manager when force setting changes
            Manager?.OnForceSettingChanged(Value);
        }
    }
}
