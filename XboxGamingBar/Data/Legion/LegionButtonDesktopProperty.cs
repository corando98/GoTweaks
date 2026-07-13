using Shared.Enums;
using System;
using Windows.UI.Core;
using Windows.UI.Xaml.Controls;

namespace XboxGamingBar.Data
{
    /// <summary>
    /// Property for Legion Desktop button remap (JSON ButtonMapping format).
    /// Default: Win+G (Game Bar)
    /// Not auto-bound to UI - call SendMapping() explicitly to send to helper.
    /// </summary>
    internal class LegionButtonDesktopProperty : WidgetControlProperty<string, ComboBox>
    {
        public LegionButtonDesktopProperty(ComboBox inUI, Page inOwner) : base("", Function.LegionButtonDesktop, inUI, inOwner)
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
            // force bypasses the json==Value dedup. Needed for cold-start recovery: the
            // constructor-time send latches Value even though the pipe write is suppressed
            // (not yet connected), so a plain re-send would see json==Value and skip.
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
