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
        // Scroll (unified) event handlers - direction not available via Raw Input API
        private void ScrollActionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!labsSectionInitialized) return;

            int selection = ScrollActionComboBox?.SelectedIndex ?? 0;
            // 0=Disabled, 1=Xbox Guide, 2=Keyboard Shortcut, 3=Run Command, 4=Focus GoTweaks
            bool isShortcut = selection == 2;
            bool isCommand = selection == 3;
            if (ScrollShortcutPanel != null)
                ScrollShortcutPanel.Visibility = isShortcut ? Visibility.Visible : Visibility.Collapsed;
            if (ScrollCommandGrid != null)
                ScrollCommandGrid.Visibility = isCommand ? Visibility.Visible : Visibility.Collapsed;

            // Always save settings immediately when selection changes
            SaveScrollRemapSettings();

            // Apply immediately for Disabled, Xbox Guide, or Focus GoTweaks
            if (selection != 2 && selection != 3)
                ApplyScrollWheelConfig("Scroll");

            UpdateScrollRemapDescription();
        }

        private void ScrollCommandApplyButton_Click(object sender, RoutedEventArgs e)
        {
            SaveScrollRemapSettings();
            ApplyScrollWheelConfig("Scroll");
            UpdateScrollRemapDescription();
        }

        // Scroll Click event handlers
        private void ScrollClickActionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!labsSectionInitialized) return;

            int selection = ScrollClickActionComboBox?.SelectedIndex ?? 0;
            bool isShortcut = selection == 2;
            bool isCommand = selection == 3;
            if (ScrollClickShortcutPanel != null)
                ScrollClickShortcutPanel.Visibility = isShortcut ? Visibility.Visible : Visibility.Collapsed;
            if (ScrollClickCommandGrid != null)
                ScrollClickCommandGrid.Visibility = isCommand ? Visibility.Visible : Visibility.Collapsed;

            // Always save settings immediately when selection changes
            SaveScrollRemapSettings();

            if (selection != 2 && selection != 3)
                ApplyScrollWheelConfig("Click");

            UpdateScrollRemapDescription();
        }

        private void ScrollClickCommandApplyButton_Click(object sender, RoutedEventArgs e)
        {
            SaveScrollRemapSettings();
            ApplyScrollWheelConfig("Click");
            UpdateScrollRemapDescription();
        }

        private void UpdateScrollRemapDescription()
        {
            // Description text removed in consolidated Special Remapping card
        }

        private void SaveScrollRemapSettings()
        {
            try
            {
                var settings = ApplicationData.Current.LocalSettings;
                int scrollAction = ScrollActionComboBox?.SelectedIndex ?? 0;
                string scrollShortcut = GetKeysAsString("Scroll");
                string scrollCommand = ScrollCommandTextBox?.Text ?? "";
                int clickAction = ScrollClickActionComboBox?.SelectedIndex ?? 0;
                string clickShortcut = GetKeysAsString("ScrollClick");
                string clickCommand = ScrollClickCommandTextBox?.Text ?? "";

                settings.Values["Scroll_Action"] = scrollAction;
                settings.Values["Scroll_Shortcut"] = scrollShortcut;
                settings.Values["Scroll_Command"] = scrollCommand;
                settings.Values["ScrollClick_Action"] = clickAction;
                settings.Values["ScrollClick_Shortcut"] = clickShortcut;
                settings.Values["ScrollClick_Command"] = clickCommand;

                // Also save to JSON fallback file for elevated helper
                SaveToFallbackSettingsFile(new Dictionary<string, object>
                {
                    { "Scroll_Action", scrollAction },
                    { "Scroll_Shortcut", scrollShortcut },
                    { "Scroll_Command", scrollCommand },
                    { "ScrollClick_Action", clickAction },
                    { "ScrollClick_Shortcut", clickShortcut },
                    { "ScrollClick_Command", clickCommand }
                });

                Logger.Info("Scroll wheel remap settings saved");
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to save scroll wheel remap settings: {ex.Message}");
            }
        }

        private void LoadScrollRemapSettings()
        {
            try
            {
                var settings = ApplicationData.Current.LocalSettings;

                // Load Scroll (unified) settings
                if (settings.Values.TryGetValue("Scroll_Action", out var scrollAction) && scrollAction is int scrollActionInt)
                {
                    if (ScrollActionComboBox != null && scrollActionInt >= 0 && scrollActionInt <= 4)
                        ScrollActionComboBox.SelectedIndex = scrollActionInt;
                }
                if (settings.Values.TryGetValue("Scroll_Shortcut", out var scrollShortcut) && scrollShortcut is string scrollShortcutStr)
                {
                    LoadKeysFromString("Scroll", scrollShortcutStr, ScrollKeyTags);
                }
                if (settings.Values.TryGetValue("Scroll_Command", out var scrollCommand) && scrollCommand is string scrollCommandStr)
                {
                    if (ScrollCommandTextBox != null)
                        ScrollCommandTextBox.Text = scrollCommandStr;
                }

                // Load Scroll Click settings
                if (settings.Values.TryGetValue("ScrollClick_Action", out var clickAction) && clickAction is int clickActionInt)
                {
                    if (ScrollClickActionComboBox != null && clickActionInt >= 0 && clickActionInt <= 4)
                        ScrollClickActionComboBox.SelectedIndex = clickActionInt;
                }
                if (settings.Values.TryGetValue("ScrollClick_Shortcut", out var clickShortcut) && clickShortcut is string clickShortcutStr)
                {
                    LoadKeysFromString("ScrollClick", clickShortcutStr, ScrollClickKeyTags);
                }
                if (settings.Values.TryGetValue("ScrollClick_Command", out var clickCommand) && clickCommand is string clickCommandStr)
                {
                    if (ScrollClickCommandTextBox != null)
                        ScrollClickCommandTextBox.Text = clickCommandStr;
                }

                // Update visibility of shortcut/command grids based on loaded settings
                UpdateScrollGridVisibility();

                // Also sync to JSON fallback file for elevated helper
                int scrollActionLoaded = ScrollActionComboBox?.SelectedIndex ?? 0;
                string scrollShortcutLoaded = GetKeysAsString("Scroll");
                string scrollCommandLoaded = ScrollCommandTextBox?.Text ?? "";
                int clickActionLoaded = ScrollClickActionComboBox?.SelectedIndex ?? 0;
                string clickShortcutLoaded = GetKeysAsString("ScrollClick");
                string clickCommandLoaded = ScrollClickCommandTextBox?.Text ?? "";

                SaveToFallbackSettingsFile(new Dictionary<string, object>
                {
                    { "Scroll_Action", scrollActionLoaded },
                    { "Scroll_Shortcut", scrollShortcutLoaded },
                    { "Scroll_Command", scrollCommandLoaded },
                    { "ScrollClick_Action", clickActionLoaded },
                    { "ScrollClick_Shortcut", clickShortcutLoaded },
                    { "ScrollClick_Command", clickCommandLoaded }
                });

                Logger.Info("Scroll wheel remap settings loaded");
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to load scroll wheel remap settings: {ex.Message}");
            }
        }

        private void UpdateScrollGridVisibility()
        {
            int scrollSelection = ScrollActionComboBox?.SelectedIndex ?? 0;
            if (ScrollShortcutPanel != null)
                ScrollShortcutPanel.Visibility = scrollSelection == 2 ? Visibility.Visible : Visibility.Collapsed;
            if (ScrollCommandGrid != null)
                ScrollCommandGrid.Visibility = scrollSelection == 3 ? Visibility.Visible : Visibility.Collapsed;

            int clickSelection = ScrollClickActionComboBox?.SelectedIndex ?? 0;
            if (ScrollClickShortcutPanel != null)
                ScrollClickShortcutPanel.Visibility = clickSelection == 2 ? Visibility.Visible : Visibility.Collapsed;
            if (ScrollClickCommandGrid != null)
                ScrollClickCommandGrid.Visibility = clickSelection == 3 ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void ApplyScrollRemapSettingsToHelper()
        {
            // Always send scroll config to helper (including disabled state) to clear any stale monitor config
            ApplyScrollWheelConfig("Scroll");

            await Task.Delay(100);

            ApplyScrollWheelConfig("Click");
        }

        private async void ApplyScrollWheelConfig(string direction)
        {
            if (!App.IsConnected) return;

            try
            {
                ComboBox actionComboBox = direction == "Scroll" ? ScrollActionComboBox :
                                          direction == "Click" ? ScrollClickActionComboBox :
                                          ScrollClickActionComboBox;
                string shortcutKeyName = direction == "Scroll" ? "Scroll" :
                                         direction == "Click" ? "ScrollClick" :
                                         "ScrollClick";
                TextBox commandTextBox = direction == "Scroll" ? ScrollCommandTextBox :
                                         direction == "Click" ? ScrollClickCommandTextBox :
                                         ScrollClickCommandTextBox;
                string actionName = direction == "Scroll" ? "Scroll Wheel" : $"Scroll {direction}";

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
                        if (ScrollRemapStatusText != null)
                        {
                            ScrollRemapStatusText.Text = $"{actionName}: Please select keys";
                            ScrollRemapStatusText.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 200, 100));
                        }
                        return;
                    }
                }
                else if (selection == 3 && commandTextBox != null)
                {
                    shortcutOrCommand = commandTextBox.Text?.Trim() ?? "";
                    if (string.IsNullOrEmpty(shortcutOrCommand))
                    {
                        if (ScrollRemapStatusText != null)
                        {
                            ScrollRemapStatusText.Text = $"{actionName}: Please enter a command";
                            ScrollRemapStatusText.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 200, 100));
                        }
                        return;
                    }
                }

                var request = new Windows.Foundation.Collections.ValueSet();
                request.Add("Function", (int)Function.Labs_LegionScrollRemap);
                request.Add("Direction", direction);
                request.Add("Enabled", enabled);
                request.Add("Action", actionType);
                request.Add("Shortcut", shortcutOrCommand);

                var response = await App.SendMessageAsync(request);

                if (response != null)
                {
                    if (response.TryGetValue("Success", out object successObj))
                    {
                        bool success = Convert.ToBoolean(successObj);
                        if (ScrollRemapStatusText != null)
                        {
                            if (!enabled)
                            {
                                ScrollRemapStatusText.Text = "";
                            }
                            else if (success)
                            {
                                ScrollRemapStatusText.Text = "";
                            }
                            else
                            {
                                string errorMsg = actionType == 0 ? "ViGEmBus not installed or controller not found" : "Controller not found";
                                ScrollRemapStatusText.Text = $"{actionName}: Failed - {errorMsg}";
                                ScrollRemapStatusText.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 100, 100));
                                actionComboBox.SelectedIndex = 0; // Reset to Disabled
                            }
                        }

                        // Save settings on success
                        if (success || !enabled)
                            SaveScrollRemapSettings();

                        Logger.Info($"Scroll Wheel Remap: {direction}, Enabled={enabled}, Action={actionType}, Value={shortcutOrCommand}, Success={success}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to apply scroll wheel config: {ex.Message}");
            }
        }

    }
}
