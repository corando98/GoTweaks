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
        private void InitializeLabsSection()
        {
            // Create DAService status polling timer (only runs when Legion tab is visible)
            daServiceStatusTimer = new DispatcherTimer();
            daServiceStatusTimer.Interval = TimeSpan.FromSeconds(30);
            daServiceStatusTimer.Tick += (s, e) => UpdateDAServiceStatus();
            // Don't start timer here - it will be started when Legion tab becomes visible

            // Wire up Legion button remap event handlers (done in code to avoid XAML init issues)
            if (LegionLActionComboBox != null)
                LegionLActionComboBox.SelectionChanged += LegionLActionComboBox_SelectionChanged;
            if (LegionRActionComboBox != null)
                LegionRActionComboBox.SelectionChanged += LegionRActionComboBox_SelectionChanged;

            // Wire up Scroll wheel remap event handlers
            if (ScrollActionComboBox != null)
                ScrollActionComboBox.SelectionChanged += ScrollActionComboBox_SelectionChanged;
            if (ScrollClickActionComboBox != null)
                ScrollClickActionComboBox.SelectionChanged += ScrollClickActionComboBox_SelectionChanged;

            // Load saved Legion remap settings
            LoadLegionRemapSettings();

            // Load saved Scroll wheel remap settings
            LoadScrollRemapSettings();

            // Mark Labs section as initialized (enables event handlers)
            labsSectionInitialized = true;

            // Sync the "Legion L is disabled" hint in the Controller Emulation card with
            // the freshly-loaded Legion L action state.
            UpdateViiperLegionLDisabledHint();

            // Apply saved settings to helper (after connection is established)
            _ = Task.Run(async () =>
            {
                // Wait for helper connection (pipe or AppService)
                for (int i = 0; i < 30 && !App.IsConnected; i++)
                    await Task.Delay(200);

                if (App.IsConnected)
                {
                    await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                    {
                        ApplyLegionRemapSettingsToHelper();
                        ApplyScrollRemapSettingsToHelper();
                    });
                }
            });
        }

        // (RequestViGEmBusStatus removed — ViGEm backend retired. The Labs
        // Guide-remap prerequisite line reflects usbip-win2 via UpdateLabsUsbipUI,
        // driven by the UsbipInstalled property push.)

        private async void UpdateDAServiceStatus()
        {
            if (!App.IsConnected)
                return;

            // Request DAService status from helper
            try
            {
                var request = new Windows.Foundation.Collections.ValueSet();
                request.Add("Command", (int)Command.Get);
                request.Add("Function", (int)Function.Labs_DAServiceStatus);
                var response = await App.SendMessageAsync(request);

                // Handle response
                if (response != null)
                {
                    if (response.TryGetValue("Content", out object statusObj))
                    {
                        int status = Convert.ToInt32(statusObj);
                        OnDAServiceStatusReceived(status);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to request DAService status: {ex.Message}");
            }
        }

        private void OnDAServiceStatusReceived(int status)
        {
            // Status reflects the startup state: 0 = Disabled (no boot auto-start; Legion Space
            // still launchable on demand), 1 = Enabled (auto-starts at boot), 2 = Not Found.
            // (3/4 are legacy transient codes the helper no longer sends.)
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (DAServiceStatusText == null || ToggleDAServiceButton == null)
                    return;

                switch (status)
                {
                    case 0: // Disabled (no auto-start; still launchable on demand)
                        daServiceIsRunning = false;
                        DAServiceStatusText.Text = "Auto-start off - Legion Space won't run at boot (still launchable)";
                        DAServiceStatusText.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 200, 83)); // Green
                        ToggleDAServiceButton.Content = "Enable";
                        ToggleDAServiceButton.IsEnabled = true;
                        break;
                    case 1: // Enabled (auto-starts at boot)
                        daServiceIsRunning = true;
                        DAServiceStatusText.Text = "Auto-start on - Legion Space runs at boot";
                        DAServiceStatusText.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 170, 0)); // Orange
                        ToggleDAServiceButton.Content = "Disable";
                        ToggleDAServiceButton.IsEnabled = true;
                        break;
                    case 2: // Not Found
                        daServiceIsRunning = false;
                        DAServiceStatusText.Text = "DAService not found on this system";
                        DAServiceStatusText.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 128, 128, 128)); // Gray
                        ToggleDAServiceButton.IsEnabled = false;
                        break;
                    case 3: // Stopping
                        daServiceIsRunning = true; // Still technically running
                        DAServiceStatusText.Text = "Service stopping... please wait";
                        DAServiceStatusText.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 200, 0)); // Yellow
                        ToggleDAServiceButton.Content = "...";
                        ToggleDAServiceButton.IsEnabled = false;
                        break;
                    case 4: // Starting
                        daServiceIsRunning = false; // Still technically stopped
                        DAServiceStatusText.Text = "Service starting... please wait";
                        DAServiceStatusText.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 200, 0)); // Yellow
                        ToggleDAServiceButton.Content = "...";
                        ToggleDAServiceButton.IsEnabled = false;
                        break;
                }
            });
        }

        private async void ToggleDAServiceButton_Click(object sender, RoutedEventArgs e)
        {
            if (!App.IsConnected)
                return;

            try
            {
                // Update button text immediately for responsiveness
                ToggleDAServiceButton.Content = "...";
                DAServiceStatusText.Text = daServiceIsRunning ? "Disabling service..." : "Enabling service...";

                // Send start/stop command to helper
                // Content: 0 = Stop and Disable, 1 = Enable and Start
                int action = daServiceIsRunning ? 0 : 1;
                var request = new Windows.Foundation.Collections.ValueSet();
                request.Add("Command", (int)Command.Set);
                request.Add("Function", (int)Function.Labs_DAServiceControl);
                request.Add("Content", action);
                var response = await App.SendMessageAsync(request);

                // Handle response - helper sends back updated status in Content
                if (response != null)
                {
                    if (response.TryGetValue("Content", out object statusObj))
                    {
                        int status = Convert.ToInt32(statusObj);
                        OnDAServiceStatusReceived(status);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to control DAService: {ex.Message}");
                // Reset UI on error
                UpdateDAServiceStatus();
            }
        }

        // Legion L event handlers
        private void LegionLActionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!labsSectionInitialized) return;

            int selection = LegionLActionComboBox?.SelectedIndex ?? 0;
            // 0=Disabled, 1=Xbox Guide, 2=Keyboard Shortcut, 3=Run Command, 4=Focus GoTweaks
            bool isShortcut = selection == 2;
            bool isCommand = selection == 3;
            if (LegionLShortcutPanel != null)
                LegionLShortcutPanel.Visibility = isShortcut ? Visibility.Visible : Visibility.Collapsed;
            if (LegionLCommandGrid != null)
                LegionLCommandGrid.Visibility = isCommand ? Visibility.Visible : Visibility.Collapsed;

            // Always save settings immediately when selection changes
            SaveLegionRemapSettings();

            // Apply immediately for Disabled, Xbox Guide, or Focus GoTweaks
            if (selection != 2 && selection != 3)
                ApplyLegionButtonConfig(true);

            UpdateLegionRemapDescription();
            UpdateViiperLegionLDisabledHint();
        }

        /// <summary>
        /// Shows the "Legion L is disabled" warning in the Controller Emulation card's
        /// Guide/Mode section when the user has Guide mode set to Native but the Legion L
        /// Special Remapping action is set to Disabled — otherwise Native mode can't route
        /// the Xbox button through the emulated device.
        /// </summary>
        internal void UpdateViiperLegionLDisabledHint()
        {
            if (ViiperLegionLDisabledHint == null) return;

            int legionLAction = LegionLActionComboBox?.SelectedIndex ?? 0;
            string guideMode = (ViiperGuideButtonModeComboBox?.SelectedItem as ComboBoxItem)?.Tag as string;

            bool show = legionLAction == 0 && string.Equals(guideMode, "Native", StringComparison.Ordinal);
            ViiperLegionLDisabledHint.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        private void LegionLCommandApplyButton_Click(object sender, RoutedEventArgs e)
        {
            SaveLegionRemapSettings();
            ApplyLegionButtonConfig(true);
            UpdateLegionRemapDescription();
        }

        // Legion R event handlers
        private void LegionRActionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!labsSectionInitialized) return;

            int selection = LegionRActionComboBox?.SelectedIndex ?? 0;
            // 0=Disabled, 1=Xbox Guide, 2=Keyboard Shortcut, 3=Run Command, 4=Focus GoTweaks
            bool isShortcut = selection == 2;
            bool isCommand = selection == 3;
            if (LegionRShortcutPanel != null)
                LegionRShortcutPanel.Visibility = isShortcut ? Visibility.Visible : Visibility.Collapsed;
            if (LegionRCommandGrid != null)
                LegionRCommandGrid.Visibility = isCommand ? Visibility.Visible : Visibility.Collapsed;

            // Always save settings immediately when selection changes
            SaveLegionRemapSettings();

            // Apply immediately for Disabled, Xbox Guide, or Focus GoTweaks
            if (selection != 2 && selection != 3)
                ApplyLegionButtonConfig(false);

            UpdateLegionRemapDescription();
        }

        private void LegionRCommandApplyButton_Click(object sender, RoutedEventArgs e)
        {
            SaveLegionRemapSettings();
            ApplyLegionButtonConfig(false);
            UpdateLegionRemapDescription();
        }

        private void UpdateLegionRemapDescription()
        {
            // Description text removed in consolidated Special Remapping card
        }

        private string GetCommandDisplayName(string commandPath)
        {
            if (string.IsNullOrEmpty(commandPath))
                return null;
            // Show just the exe name if it's a path
            try
            {
                var fileName = System.IO.Path.GetFileName(commandPath.Split(' ')[0]);
                return !string.IsNullOrEmpty(fileName) ? fileName : commandPath;
            }
            catch
            {
                return commandPath;
            }
        }

        /// <summary>
        /// Saves settings to the JSON fallback file that the elevated helper can read.
        /// The elevated helper runs without package identity and can't access ApplicationData.Current.LocalSettings.
        ///
        /// Coordinates with the helper's writer via a named cross-process mutex and uses an
        /// atomic temp-file-rename write so a concurrent reader can never see a truncated
        /// file and then clobber the helper's persisted keys (e.g. EmulationBackend,
        /// Viiper_* settings). If the existing file can't be parsed, we DO NOT start from
        /// a blank slate — that would wipe every helper-owned key. We skip the save instead
        /// and let the next write retry.
        /// </summary>
        private void SaveToFallbackSettingsFile(Dictionary<string, object> settingsToSave)
        {
            var localCachePath = System.IO.Path.Combine(
                ApplicationData.Current.LocalCacheFolder.Path,
                "settings.json");
            var localStatePath = System.IO.Path.Combine(
                ApplicationData.Current.LocalFolder.Path,
                "settings.json");

            System.Threading.Mutex mutex = null;
            bool locked = false;
            try
            {
                mutex = new System.Threading.Mutex(false, @"Global\GoTweaksSettingsFileMutex");
                try { locked = mutex.WaitOne(2000); }
                catch (System.Threading.AbandonedMutexException) { locked = true; }

                // Prefer LocalState as the canonical source — the helper writes there.
                // Only fall back to LocalCache if LocalState doesn't exist yet.
                Windows.Data.Json.JsonObject allSettings = null;
                string sourcePath = System.IO.File.Exists(localStatePath) ? localStatePath
                                  : System.IO.File.Exists(localCachePath) ? localCachePath
                                  : null;
                if (sourcePath != null)
                {
                    try
                    {
                        var existingJson = System.IO.File.ReadAllText(sourcePath);
                        if (Windows.Data.Json.JsonObject.TryParse(existingJson, out var parsed))
                        {
                            allSettings = parsed;
                        }
                        else if (!string.IsNullOrWhiteSpace(existingJson))
                        {
                            // Parse failed on non-empty content — likely read a partial write.
                            // Do NOT overwrite with a blank dict; try once more after a brief delay.
                            System.Threading.Thread.Sleep(75);
                            existingJson = System.IO.File.ReadAllText(sourcePath);
                            if (Windows.Data.Json.JsonObject.TryParse(existingJson, out var retried))
                            {
                                allSettings = retried;
                            }
                            else
                            {
                                Logger.Warn($"Settings file at {sourcePath} failed to parse twice — skipping fallback save to avoid clobbering helper-owned keys");
                                return;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"Read of {sourcePath} failed ({ex.Message}) — skipping fallback save");
                        return;
                    }
                }
                if (allSettings == null)
                {
                    allSettings = new Windows.Data.Json.JsonObject();
                }

                // Merge in the widget's new settings.
                foreach (var kvp in settingsToSave)
                {
                    if (kvp.Value is int intVal)
                        allSettings[kvp.Key] = Windows.Data.Json.JsonValue.CreateNumberValue(intVal);
                    else if (kvp.Value is string strVal)
                        allSettings[kvp.Key] = Windows.Data.Json.JsonValue.CreateStringValue(strVal);
                    else if (kvp.Value is bool boolVal)
                        allSettings[kvp.Key] = Windows.Data.Json.JsonValue.CreateBooleanValue(boolVal);
                }

                var json = allSettings.Stringify();
                // Atomic write to both locations via temp+rename so concurrent readers never
                // see a truncated file.
                WriteJsonAtomically(localCachePath, json);
                WriteJsonAtomically(localStatePath, json);

                Logger.Info($"Saved {settingsToSave.Count} settings to fallback JSON file (preserved {allSettings.Count - settingsToSave.Count} existing keys)");
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to save to fallback settings file: {ex.Message}");
            }
            finally
            {
                if (mutex != null)
                {
                    try { if (locked) mutex.ReleaseMutex(); } catch { }
                    mutex.Dispose();
                }
            }
        }

        private static void WriteJsonAtomically(string path, string json)
        {
            try
            {
                var tempPath = path + ".tmp";
                System.IO.File.WriteAllText(tempPath, json);
                try
                {
                    if (System.IO.File.Exists(path))
                        System.IO.File.Replace(tempPath, path, null);
                    else
                        System.IO.File.Move(tempPath, path);
                }
                catch
                {
                    System.IO.File.Copy(tempPath, path, overwrite: true);
                    try { System.IO.File.Delete(tempPath); } catch { }
                }
            }
            catch { /* best-effort per target */ }
        }

        private void SaveLegionRemapSettings()
        {
            try
            {
                var settings = ApplicationData.Current.LocalSettings;
                int lAction = LegionLActionComboBox?.SelectedIndex ?? 0;
                string lShortcut = GetKeysAsString("LegionL");
                string lCommand = LegionLCommandTextBox?.Text ?? "";
                int rAction = LegionRActionComboBox?.SelectedIndex ?? 0;
                string rShortcut = GetKeysAsString("LegionR");
                string rCommand = LegionRCommandTextBox?.Text ?? "";

                settings.Values["LegionL_Action"] = lAction;
                settings.Values["LegionL_Shortcut"] = lShortcut;
                settings.Values["LegionL_Command"] = lCommand;
                settings.Values["LegionR_Action"] = rAction;
                settings.Values["LegionR_Shortcut"] = rShortcut;
                settings.Values["LegionR_Command"] = rCommand;

                // Also save to JSON fallback file for elevated helper
                SaveToFallbackSettingsFile(new Dictionary<string, object>
                {
                    { "LegionL_Action", lAction },
                    { "LegionL_Shortcut", lShortcut },
                    { "LegionL_Command", lCommand },
                    { "LegionR_Action", rAction },
                    { "LegionR_Shortcut", rShortcut },
                    { "LegionR_Command", rCommand }
                });

                Logger.Info("Legion remap settings saved");
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to save Legion remap settings: {ex.Message}");
            }
        }

        private void LoadLegionRemapSettings()
        {
            try
            {
                var settings = ApplicationData.Current.LocalSettings;

                // Load Legion L settings
                if (settings.Values.TryGetValue("LegionL_Action", out var lAction) && lAction is int lActionInt)
                {
                    if (LegionLActionComboBox != null && lActionInt >= 0 && lActionInt <= 4)
                        LegionLActionComboBox.SelectedIndex = lActionInt;
                }
                if (settings.Values.TryGetValue("LegionL_Shortcut", out var lShortcut) && lShortcut is string lShortcutStr)
                {
                    LoadKeysFromString("LegionL", lShortcutStr, LegionLKeyTags);
                }
                if (settings.Values.TryGetValue("LegionL_Command", out var lCommand) && lCommand is string lCommandStr)
                {
                    if (LegionLCommandTextBox != null)
                        LegionLCommandTextBox.Text = lCommandStr;
                }

                // Load Legion R settings
                if (settings.Values.TryGetValue("LegionR_Action", out var rAction) && rAction is int rActionInt)
                {
                    if (LegionRActionComboBox != null && rActionInt >= 0 && rActionInt <= 4)
                        LegionRActionComboBox.SelectedIndex = rActionInt;
                }
                if (settings.Values.TryGetValue("LegionR_Shortcut", out var rShortcut) && rShortcut is string rShortcutStr)
                {
                    LoadKeysFromString("LegionR", rShortcutStr, LegionRKeyTags);
                }
                if (settings.Values.TryGetValue("LegionR_Command", out var rCommand) && rCommand is string rCommandStr)
                {
                    if (LegionRCommandTextBox != null)
                        LegionRCommandTextBox.Text = rCommandStr;
                }

                // Update description and show/hide input grids based on loaded settings
                UpdateLegionRemapDescription();
                int lSelectionLoaded = LegionLActionComboBox?.SelectedIndex ?? 0;
                int rSelectionLoaded = LegionRActionComboBox?.SelectedIndex ?? 0;
                if (LegionLShortcutPanel != null)
                    LegionLShortcutPanel.Visibility = (lSelectionLoaded == 2) ? Visibility.Visible : Visibility.Collapsed;
                if (LegionLCommandGrid != null)
                    LegionLCommandGrid.Visibility = (lSelectionLoaded == 3) ? Visibility.Visible : Visibility.Collapsed;
                if (LegionRShortcutPanel != null)
                    LegionRShortcutPanel.Visibility = (rSelectionLoaded == 2) ? Visibility.Visible : Visibility.Collapsed;
                if (LegionRCommandGrid != null)
                    LegionRCommandGrid.Visibility = (rSelectionLoaded == 3) ? Visibility.Visible : Visibility.Collapsed;

                // Also sync to JSON fallback file for elevated helper
                SaveToFallbackSettingsFile(new Dictionary<string, object>
                {
                    { "LegionL_Action", LegionLActionComboBox?.SelectedIndex ?? 0 },
                    { "LegionL_Shortcut", GetKeysAsString("LegionL") },
                    { "LegionL_Command", LegionLCommandTextBox?.Text ?? "" },
                    { "LegionR_Action", LegionRActionComboBox?.SelectedIndex ?? 0 },
                    { "LegionR_Shortcut", GetKeysAsString("LegionR") },
                    { "LegionR_Command", LegionRCommandTextBox?.Text ?? "" }
                });

                Logger.Info("Legion remap settings loaded");
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to load Legion remap settings: {ex.Message}");
            }
        }

        private async void ApplyLegionRemapSettingsToHelper()
        {
            // Always send L/R config to helper (including disabled state) to clear any stale monitor config
            ApplyLegionButtonConfig(true);

            // Small delay between requests
            await Task.Delay(100);

            ApplyLegionButtonConfig(false);
        }

        private async void ApplyLegionButtonConfig(bool isLegionL)
        {
            // Validation/status feedback lands under the row it concerns, not the card bottom.
            var rowStatusText = FindName(isLegionL ? "LegionLRemapStatusText" : "LegionRRemapStatusText") as TextBlock;

            if (!App.IsConnected) return;

            try
            {
                ComboBox actionComboBox = isLegionL ? LegionLActionComboBox : LegionRActionComboBox;
                string shortcutKeyName = isLegionL ? "LegionL" : "LegionR";
                TextBox commandTextBox = isLegionL ? LegionLCommandTextBox : LegionRCommandTextBox;
                string buttonName = isLegionL ? "Legion L" : "Legion R";

                if (actionComboBox == null) return;

                int selection = actionComboBox.SelectedIndex; // 0=Disabled, 1=Xbox Guide, 2=Shortcut, 3=Command, 4=Focus GoTweaks
                bool enabled = selection != 0;
                // Convert UI selection to helper action type: 0=Xbox Guide, 1=Shortcut, 2=Command, 3=Focus GoTweaks
                int actionType = selection == 1 ? 0 : selection == 2 ? 1 : selection == 3 ? 2 : selection == 4 ? 3 : 0;

                string shortcutOrCommand = "";
                if (selection == 2)
                {
                    shortcutOrCommand = GetKeysAsString(shortcutKeyName);
                    if (string.IsNullOrEmpty(shortcutOrCommand))
                    {
                        if (LegionRemapStatusText != null)
                        {
                            LegionRemapStatusText.Text = $"{buttonName}: Please select keys";
                            LegionRemapStatusText.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 200, 100));
                        }
                        return;
                    }
                }
                else if (selection == 3 && commandTextBox != null)
                {
                    shortcutOrCommand = commandTextBox.Text?.Trim() ?? "";
                    if (string.IsNullOrEmpty(shortcutOrCommand))
                    {
                        if (LegionRemapStatusText != null)
                        {
                            LegionRemapStatusText.Text = $"{buttonName}: Please enter a command";
                            LegionRemapStatusText.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 200, 100));
                        }
                        return;
                    }
                }

                var request = new Windows.Foundation.Collections.ValueSet();
                request.Add("Function", (int)Function.Labs_LegionButtonRemap);
                request.Add("Button", isLegionL ? "L" : "R");
                request.Add("Enabled", enabled);
                request.Add("Action", actionType);
                request.Add("Shortcut", shortcutOrCommand); // Reuse "Shortcut" field for both shortcut and command

                var response = await App.SendMessageAsync(request);

                if (response != null)
                {
                    if (response.TryGetValue("Success", out object successObj))
                    {
                        bool success = Convert.ToBoolean(successObj);
                        if (LegionRemapStatusText != null)
                        {
                            if (!enabled)
                            {
                                LegionRemapStatusText.Text = "";
                            }
                            else if (success)
                            {
                                LegionRemapStatusText.Text = "";
                            }
                            else
                            {
                                string errorMsg = actionType == 0 ? "ViGEmBus not installed or controller not found" : "Controller not found";
                                LegionRemapStatusText.Text = $"{buttonName}: Failed - {errorMsg}";
                                LegionRemapStatusText.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 100, 100));
                                actionComboBox.SelectedIndex = 0; // Reset to Disabled
                            }
                        }

                        // Save settings on success
                        if (success || !enabled)
                            SaveLegionRemapSettings();

                        Logger.Info($"Legion Button Remap: {buttonName}, Enabled={enabled}, Action={actionType}, Value={shortcutOrCommand}, Success={success}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to apply Legion button config: {ex.Message}");
            }
        }

    }
}
