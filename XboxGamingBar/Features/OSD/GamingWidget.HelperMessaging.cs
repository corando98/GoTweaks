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

        private async Task SendHelperMessageAsync(Windows.Foundation.Collections.ValueSet message)
        {
            if (App.IsConnected)
            {
                try
                {
                    await App.SendMessageAsync(message);
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error sending message to helper: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Send a keyboard shortcut via the helper process.
        /// This is required because UWP apps cannot use SendInput directly due to sandboxing.
        /// </summary>
        private async Task SendKeyboardShortcutViaHelper(string shortcut)
        {
            if (string.IsNullOrWhiteSpace(shortcut))
            {
                Logger.Warn("Empty shortcut string provided to SendKeyboardShortcutViaHelper");
                return;
            }

            try
            {
                var message = new Windows.Foundation.Collections.ValueSet();
                message.Add("SendKeyboardShortcut", shortcut);
                await SendHelperMessageAsync(message);
                Logger.Info($"Sent keyboard shortcut request to helper: {shortcut}");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error sending keyboard shortcut via helper: {ex.Message}");
            }
        }

        /// <summary>
        /// Request the helper to refresh display settings (resolution, refresh rate, HDR).
        /// Called when a game closes to ensure the resolution tile shows the correct value.
        /// </summary>
        private async Task RequestDisplaySettingsRefreshAsync()
        {
            try
            {
                Logger.Info("Requesting display settings refresh from helper");
                var message = new Windows.Foundation.Collections.ValueSet();
                message.Add("RefreshDisplaySettings", true);
                await SendHelperMessageAsync(message);
            }
            catch (Exception ex)
            {
                Logger.Error($"Error requesting display settings refresh: {ex.Message}");
            }
        }

        /// <summary>
        /// Send a custom shortcut by first closing Game Bar (if in widget mode), then sending the shortcut.
        /// Sequence: Win+G (close Game Bar) → Custom shortcut
        /// </summary>
        private async Task SendCustomShortcutAsync(string shortcut, string tileName)
        {
            try
            {
                Logger.Info($"Custom shortcut tile clicked: {tileName} -> {shortcut}");

                // Only close Game Bar if we're running as a widget
                if (widget != null)
                {
                    // First close Game Bar with Win+G
                    await SendKeyboardShortcutViaHelper("Win+G");
                    Logger.Debug("Win+G sent to close Game Bar");

                    // Wait for Game Bar to close
                    await Task.Delay(150);
                }

                // Now send the actual shortcut
                await SendKeyboardShortcutViaHelper(shortcut);
                Logger.Info($"Custom shortcut sent: {shortcut}");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error sending custom shortcut '{shortcut}': {ex.Message}");
            }
        }

    }
}
