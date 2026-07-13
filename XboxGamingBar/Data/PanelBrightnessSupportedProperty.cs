using Shared.Enums;
using System;
using Windows.UI.Xaml.Controls;

namespace XboxGamingBar.Data
{
    // Enables/disables the panel-brightness slider based on the helper's live "is the built-in
    // panel controllable" signal. When docked to an external-only display the built-in panel is
    // not in the active display config, so WMI brightness would silently no-op — this grays the
    // slider out instead of letting it look interactive (#50 docked display).
    internal class PanelBrightnessSupportedProperty : WidgetControlEnabledProperty<Slider>
    {
        private Action<bool> additionalCallback;

        public PanelBrightnessSupportedProperty(Slider inUI, Page inOwner)
            : base(Function.PanelBrightnessSupported, inUI, inOwner)
        {
        }

        public void SetAdditionalCallback(Action<bool> callback)
        {
            additionalCallback = callback;
        }

        protected override void SetControlEnabled(bool isEnabled)
        {
            base.SetControlEnabled(isEnabled);
            additionalCallback?.Invoke(isEnabled);
        }
    }
}
