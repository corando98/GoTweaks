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
        private async void AdaptiveBrightnessToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (isLoadingOLEDSettings) return;
            adaptiveBrightnessEnabled = AdaptiveBrightnessToggle.IsOn;
            SaveDisplayOSDSettingsToStorage();
            await SendDisplayOSDConfigToHelper();
        }

        // Chevron expander for the Adaptive Brightness card — keeps the card compact
        // by default; the mode combo + description live inside.
        // Chevron glyph: E70D = down (collapsed), E70E = up (expanded).
        private void AdaptiveBrightnessExpanderToggle_Click(object sender, RoutedEventArgs e)
        {
            if (AdaptiveBrightnessPanel == null || AdaptiveBrightnessExpanderToggle == null) return;
            bool open = AdaptiveBrightnessExpanderToggle.IsChecked == true;
            AdaptiveBrightnessPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            if (AdaptiveBrightnessExpanderIcon != null)
            {
                AdaptiveBrightnessExpanderIcon.Glyph = open ? "\uE70E" : "\uE70D";
            }
        }

        private async void OSDPositionShiftToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (isLoadingOLEDSettings) return;
            osdPositionShiftEnabled = OSDPositionShiftToggle.IsOn;
            SaveDisplayOSDSettingsToStorage();
            await SendDisplayOSDConfigToHelper();
        }

        private void FrametimeGraphPinnedToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (isLoadingOSDConfig) return;
            frametimeGraphPinned = FrametimeGraphPinnedToggle.IsOn;
            SaveOSDConfigToStorage();
            SendOSDConfigToHelper();
        }

        private void Osd24HourClockToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (isLoadingOSDConfig) return;
            osd24HourClock = Osd24HourClockToggle.IsOn;
            SaveOSDConfigToStorage();
            SendOSDConfigToHelper();
        }

        private void SaveDisplayOSDSettingsToStorage()
        {
            try
            {
                var settings = ApplicationData.Current.LocalSettings;
                settings.Values["OLED_AdaptiveBrightness"] = adaptiveBrightnessEnabled;
                settings.Values["OLED_PositionShift"] = osdPositionShiftEnabled;
            }
            catch (Exception ex)
            {
                Logger.Error($"Error saving display/OSD settings: {ex.Message}");
            }
        }

        private void LoadDisplayOSDSettingsFromStorage()
        {
            isLoadingOLEDSettings = true;
            try
            {
                var settings = ApplicationData.Current.LocalSettings;

                if (settings.Values.TryGetValue("OLED_AdaptiveBrightness", out object adaptiveBrightness) && adaptiveBrightness is bool ab)
                    adaptiveBrightnessEnabled = ab;
                if (settings.Values.TryGetValue("OLED_PositionShift", out object posShift) && posShift is bool ps)
                    osdPositionShiftEnabled = ps;
                if (settings.Values.TryGetValue("OSD_Opacity", out object opacity) && opacity is int op)
                    osdOpacity = op;

                // Update UI
                if (AdaptiveBrightnessToggle != null) AdaptiveBrightnessToggle.IsOn = adaptiveBrightnessEnabled;
                if (OSDPositionShiftToggle != null) OSDPositionShiftToggle.IsOn = osdPositionShiftEnabled;
                if (OSDOpacitySlider != null) OSDOpacitySlider.Value = osdOpacity;
                if (OSDOpacityValue != null) OSDOpacityValue.Text = $"{osdOpacity}%";
            }
            catch (Exception ex)
            {
                Logger.Error($"Error loading display/OSD settings: {ex.Message}");
            }
            finally
            {
                isLoadingOLEDSettings = false;
            }
        }

        private async Task SendDisplayOSDConfigToHelper()
        {
            try
            {
                if (!App.IsConnected) return;

                var configString = $"AdaptiveBrightness:{(adaptiveBrightnessEnabled ? 1 : 0)};" +
                                   $"PositionShift:{(osdPositionShiftEnabled ? 1 : 0)}";

                var request = new Windows.Foundation.Collections.ValueSet
                {
                    { "Command", (int)Shared.Enums.Command.Set },
                    { "Function", (int)Shared.Enums.Function.OLEDConfig },
                    { "Content", configString },
                    { "UpdatedTime", DateTimeOffset.Now.Ticks }
                };
                await App.SendMessageAsync(request);

                Logger.Info($"Display/OSD config sent to helper: {configString}");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error sending display/OSD config to helper: {ex.Message}");
            }
        }

    }
}
