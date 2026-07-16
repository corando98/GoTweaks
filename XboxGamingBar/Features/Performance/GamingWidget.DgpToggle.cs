using Microsoft.Gaming.XboxGameBar;
using Microsoft.Gaming.XboxGameBar.Input;
using Microsoft.UI.Xaml.Controls;
using NLog;
using Shared.Data;
using Shared.Utilities;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Data.Json;
using Windows.Foundation;
using Windows.Foundation.Metadata;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;
using Windows.System.Power;
using Windows.Storage;
using Windows.System;
using Windows.UI.Xaml.Input;
using System.Runtime.InteropServices;
using Windows.UI;
using XboxGamingBar.Data;
using XboxGamingBar.Event;
using XboxGamingBar.IPC;
using XboxGamingBar.QuickSettings;
using Shared.Enums;

namespace XboxGamingBar
{
    public sealed partial class GamingWidget
    {
        // True while the toggle is being updated programmatically (helper sync) so
        // Toggled doesn't record a synced device default as an explicit user choice.
        private bool isSyncingDgpEnabledToggle;

        private void ForceDefaultGameProfileToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (ForceDefaultGameProfileToggle == null || isSyncingDgpEnabledToggle) return;

            bool enabled = ForceDefaultGameProfileToggle.IsOn;
            Logger.Info($"Enable Default Game Profiles toggled to: {enabled}");

            // Send to helper
            forceDefaultGameProfile?.SetValue(enabled);

            // Save to local settings
            var settings = ApplicationData.Current.LocalSettings;
            settings.Values["ForceDefaultGameProfile"] = enabled;
        }

        /// <summary>
        /// Reflects the helper's Default-Game-Profiles enabled state into the System-tab
        /// toggle. The helper default is device-dependent (on for auto-detected hardware),
        /// so the toggle can't just load a static local value.
        /// </summary>
        private void OnDgpEnabledSynced()
        {
            _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
            {
                if (ForceDefaultGameProfileToggle == null || forceDefaultGameProfile == null) return;
                bool enabled = forceDefaultGameProfile.Value;
                if (ForceDefaultGameProfileToggle.IsOn != enabled)
                {
                    isSyncingDgpEnabledToggle = true;
                    try { ForceDefaultGameProfileToggle.IsOn = enabled; }
                    finally { isSyncingDgpEnabledToggle = false; }
                }
            });
        }
    }
}
