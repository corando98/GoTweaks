using Shared.Enums;
using System;
using Windows.UI.Core;
using Windows.UI.Xaml.Controls;

namespace XboxGamingBar.Data
{
    /// <summary>
    /// Property for Legion M2 button remap (JSON ButtonMapping format).
    /// Not auto-bound to UI - call SendMapping() explicitly to send to helper.
    /// </summary>
    internal class LegionButtonM2Property : WidgetControlProperty<string, ComboBox>
    {
        public LegionButtonM2Property(ComboBox inUI, Page inOwner) : base("", Function.LegionButtonM2, inUI, inOwner)
        {
            // Don't auto-bind to ComboBox - we'll manually send via SendMapping()
        }

        /// <summary>
        /// Sends the button mapping JSON to the helper.
        /// Sends even for "Disabled" state to clear the button mapping.
        /// </summary>
        public void SendMapping(string json, bool force = false)
        {
            if (json == null) return;
            // force bypasses the json==Value dedup for cold-start recovery (see Desktop property).
            if (force)
            {
                Logger.Info($"{Function} force-sending mapping: {json}");
                ForceSetValue(json);
            }
            else if (json != Value)
            {
                Logger.Info($"{Function} sending mapping: {json}");
                SetValue(json);
            }
        }
    }
}
