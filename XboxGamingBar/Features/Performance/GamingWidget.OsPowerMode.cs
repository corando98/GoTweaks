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
        private static readonly string[] OSPowerModeNames = { "Best Power Efficiency", "Balanced", "Best Performance" };

        /// <summary>
        /// Called when the OS Power Mode property changes (synced from helper)
        /// </summary>
        private void OSPowerMode_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (isUnloading) return;

                isLoadingOSPowerMode = true;
                try
                {
                    int mode = osPowerMode?.Value ?? 1;
                    if (mode >= 0 && mode < OSPowerModeNames.Length)
                    {
                        OSPowerModeComboBox.SelectedIndex = mode;
                        OSPowerModeValue.Text = OSPowerModeNames[mode];
                    }

                    // Update Quick Settings tile
                    UpdateQuickSettingsTileStates();
                }
                finally
                {
                    isLoadingOSPowerMode = false;
                }
            });
        }

        /// <summary>
        /// Called when user changes the OS Power Mode combo box
        /// </summary>
        private void OSPowerModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (isLoadingOSPowerMode || osPowerMode == null) return;

            int selectedIndex = OSPowerModeComboBox.SelectedIndex;
            if (selectedIndex >= 0 && selectedIndex < OSPowerModeNames.Length)
            {
                osPowerMode.SetValue(selectedIndex);
                OSPowerModeValue.Text = OSPowerModeNames[selectedIndex];
                Logger.Info($"OS Power Mode changed to: {OSPowerModeNames[selectedIndex]}");

                // Save the change to profile
                if (!isInitialSync && !isApplyingHelperUpdate && !isLoadingProfile && SaveOSPowerMode)
                {
                    Logger.Info($"Saving OS Power Mode change to profile: {currentProfileName}");
                    SaveCurrentSettingsToProfile(currentProfileName);
                }
            }
        }

        // --- Hide Advanced Options (System > TDP Settings). ON = hide the Performance-tab
        // Advanced card and the OS Power Mode card, so new users see fewer, more predictable
        // settings. Default ON. ---
        private const string HideAdvancedOptionsKey = "HideAdvancedOptions";
        private bool hideAdvancedOptions = true;
        private bool _hideAdvancedLoaded = false;

        private void HideAdvancedOptionsToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (HideAdvancedOptionsToggle == null) return;
            hideAdvancedOptions = HideAdvancedOptionsToggle.IsOn;
            ApplyHideAdvancedOptions();
            if (_hideAdvancedLoaded)
            {
                ApplicationData.Current.LocalSettings.Values[HideAdvancedOptionsKey] = hideAdvancedOptions;
                Logger.Info($"Hide Advanced Options set to {hideAdvancedOptions}");
            }
        }

        private void ApplyHideAdvancedOptions()
        {
            var vis = hideAdvancedOptions ? Visibility.Collapsed : Visibility.Visible;
            if (AdvancedCard != null) AdvancedCard.Visibility = vis;
            if (OSPowerModeCard != null) OSPowerModeCard.Visibility = vis;
        }

        // Called during startup settings load. Applies the stored preference (default ON)
        // without the XAML IsOn="True" init-fire stomping a stored value.
        private void LoadHideAdvancedOptions()
        {
            try
            {
                var settings = ApplicationData.Current.LocalSettings;
                if (settings.Values.TryGetValue(HideAdvancedOptionsKey, out object v) && v is bool b)
                {
                    hideAdvancedOptions = b;
                }
                if (HideAdvancedOptionsToggle != null) HideAdvancedOptionsToggle.IsOn = hideAdvancedOptions;
                ApplyHideAdvancedOptions();
            }
            catch (Exception ex)
            {
                Logger.Warn($"LoadHideAdvancedOptions failed: {ex.Message}");
            }
            finally
            {
                _hideAdvancedLoaded = true;
            }
        }

    }
}