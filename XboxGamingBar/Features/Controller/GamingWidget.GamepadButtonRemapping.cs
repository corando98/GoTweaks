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
        // Map of button index to button name (matches dropdown order in XAML)
        private static readonly string[] GamepadButtonNames = new[]
        {
            "LSClick", "LSUp", "LSDown", "LSLeft", "LSRight",
            "RSClick", "RSUp", "RSDown", "RSLeft", "RSRight",
            "DPadUp", "DPadDown", "DPadLeft", "DPadRight",
            "A", "B", "X", "Y",
            "LB", "LT", "RB", "RT",
            "Start", "Select"
        };

        /// <summary>
        /// Serializes gamepad button mappings dictionary to JSON string.
        /// Format: {"ButtonName":{Type:0,GamepadAction:5,...},...}
        /// </summary>
        private string SerializeGamepadButtonMappings(Dictionary<string, ButtonMapping> mappings)
        {
            if (mappings == null || mappings.Count == 0)
                return "{}";

            // Output nested JSON objects (not escaped strings)
            var entries = mappings.Select(kvp =>
                $"\"{kvp.Key}\":{kvp.Value.ToJson()}");
            return "{" + string.Join(",", entries) + "}";
        }

        /// <summary>
        /// Deserializes JSON string to gamepad button mappings dictionary.
        /// Format: {"ButtonName":{Type:0,...},...}
        /// </summary>
        private Dictionary<string, ButtonMapping> DeserializeGamepadButtonMappings(string json)
        {
            var result = new Dictionary<string, ButtonMapping>();
            if (string.IsNullOrEmpty(json) || json == "{}")
                return result;

            // Match patterns like "ButtonName":{...}
            var regex = new System.Text.RegularExpressions.Regex("\"(\\w+)\"\\s*:\\s*(\\{[^}]+\\})");
            var matches = regex.Matches(json);

            foreach (System.Text.RegularExpressions.Match match in matches)
            {
                if (match.Groups.Count >= 3)
                {
                    var buttonName = match.Groups[1].Value;
                    var mappingJson = match.Groups[2].Value;
                    result[buttonName] = ButtonMapping.FromJson(mappingJson);
                }
            }

            return result;
        }

        private string GetGamepadButtonNameFromIndex(int index)
        {
            if (index >= 0 && index < GamepadButtonNames.Length)
                return GamepadButtonNames[index];
            return "LSClick"; // Default
        }

        private void LoadGamepadMappingToUI(string buttonName)
        {
            if (LegionGamepadTypeComboBox == null || LegionGamepadActionComboBox == null)
                return;

            ButtonMapping mapping;
            if (gamepadButtonMappings.TryGetValue(buttonName, out mapping))
            {
                // Set type dropdown
                LegionGamepadTypeComboBox.SelectedIndex = mapping.Type;

                // Show appropriate action dropdown based on type
                UpdateGamepadMappingUI(mapping.Type);

                // Set action value
                if (mapping.Type == 0 && LegionGamepadActionComboBox != null)
                    LegionGamepadActionComboBox.SelectedIndex = mapping.GamepadAction;
                else if (mapping.Type == 2 && LegionGamepadMouseComboBox != null)
                    LegionGamepadMouseComboBox.SelectedIndex = mapping.MouseButton;
                else if (mapping.Type == 1)
                    UpdateGamepadKeyboardKeyTags(mapping.KeyboardKeys);
            }
            else
            {
                // No mapping exists - set to default (Gamepad, Disabled)
                LegionGamepadTypeComboBox.SelectedIndex = 0;
                UpdateGamepadMappingUI(0);
                if (LegionGamepadActionComboBox != null)
                    LegionGamepadActionComboBox.SelectedIndex = 0;
            }
        }

        private void UpdateGamepadMappingUI(int type)
        {
            // Type: 0=Gamepad, 1=Keyboard, 2=Mouse
            if (LegionGamepadActionComboBox != null)
                LegionGamepadActionComboBox.Visibility = type == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (LegionGamepadMouseComboBox != null)
                LegionGamepadMouseComboBox.Visibility = type == 2 ? Visibility.Visible : Visibility.Collapsed;
            if (LegionGamepadKeyboardPanel != null)
                LegionGamepadKeyboardPanel.Visibility = type == 1 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateGamepadKeyboardKeyTags(List<int> keys)
        {
            if (LegionGamepadKeyTags == null) return;

            LegionGamepadKeyTags.Children.Clear();
            if (keys == null) return;

            foreach (var key in keys)
            {
                var tagBorder = new Border
                {
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 60, 60, 60)),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 2, 6, 2),
                    Margin = new Thickness(0, 0, 4, 0)
                };

                var tagPanel = new StackPanel { Orientation = Orientation.Horizontal };

                var keyText = new TextBlock
                {
                    Text = GetKeyDisplayName(key),
                    Foreground = new SolidColorBrush(Windows.UI.Colors.White),
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center
                };

                var removeButton = new Button
                {
                    Content = "×",
                    FontSize = 10,
                    Padding = new Thickness(4, 0, 0, 0),
                    Background = new SolidColorBrush(Windows.UI.Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    Foreground = new SolidColorBrush(Windows.UI.Colors.Gray),
                    VerticalAlignment = VerticalAlignment.Center,
                    MinWidth = 0,
                    MinHeight = 0
                };
                removeButton.Click += (s, e) => RemoveGamepadKeyboardKey(key);

                tagPanel.Children.Add(keyText);
                tagPanel.Children.Add(removeButton);
                tagBorder.Child = tagPanel;
                LegionGamepadKeyTags.Children.Add(tagBorder);
            }
        }

        private void RemoveGamepadKeyboardKey(int key)
        {
            if (LegionGamepadButtonSelectorComboBox == null || LegionGamepadButtonSelectorComboBox.SelectedIndex < 0)
                return;

            var buttonName = GetGamepadButtonNameFromIndex(LegionGamepadButtonSelectorComboBox.SelectedIndex);
            if (gamepadButtonMappings.TryGetValue(buttonName, out var mapping))
            {
                mapping.KeyboardKeys.Remove(key);
                UpdateGamepadKeyboardKeyTags(mapping.KeyboardKeys);
                SaveAndSendGamepadMappings();
            }
        }

        private void LegionGamepadButtonSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (isLoadingControllerProfile || isSwitchingControllerProfile)
                return;

            if (LegionGamepadButtonSelectorComboBox == null || LegionGamepadButtonSelectorComboBox.SelectedIndex < 0)
                return;

            var buttonName = GetGamepadButtonNameFromIndex(LegionGamepadButtonSelectorComboBox.SelectedIndex);
            LoadGamepadMappingToUI(buttonName);
        }

        private void LegionGamepadMapping_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (isLoadingControllerProfile || isSwitchingControllerProfile)
                return;

            // Skip if a profile was just applied (prevents duplicate sends from queued UI events)
            if ((DateTime.Now - lastProfileApplyTime).TotalMilliseconds < 2000)
                return;

            if (LegionGamepadButtonSelectorComboBox == null || LegionGamepadButtonSelectorComboBox.SelectedIndex < 0)
                return;

            var buttonName = GetGamepadButtonNameFromIndex(LegionGamepadButtonSelectorComboBox.SelectedIndex);
            var type = LegionGamepadTypeComboBox?.SelectedIndex ?? 0;

            // Update UI visibility if type changed
            if (sender == LegionGamepadTypeComboBox)
            {
                UpdateGamepadMappingUI(type);
            }

            // Build new mapping from UI
            var mapping = new ButtonMapping { Type = type };
            if (type == 0 && LegionGamepadActionComboBox != null)
                mapping.GamepadAction = LegionGamepadActionComboBox.SelectedIndex;
            else if (type == 2 && LegionGamepadMouseComboBox != null)
                mapping.MouseButton = LegionGamepadMouseComboBox.SelectedIndex;
            else if (type == 1)
            {
                // Keep existing keyboard keys if we have them
                if (gamepadButtonMappings.TryGetValue(buttonName, out var existingMapping))
                    mapping.KeyboardKeys = new List<int>(existingMapping.KeyboardKeys ?? new List<int>());
            }

            gamepadButtonMappings[buttonName] = mapping;
            SaveAndSendGamepadMappings();
        }

        private void LegionGamepadKey_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (isLoadingControllerProfile || isSwitchingControllerProfile)
                return;

            // Skip if a profile was just applied (prevents duplicate sends from queued UI events)
            if ((DateTime.Now - lastProfileApplyTime).TotalMilliseconds < 2000)
                return;

            if (LegionGamepadKeyComboBox == null || LegionGamepadKeyComboBox.SelectedIndex <= 0)
                return; // Index 0 is "+ Key" placeholder

            if (LegionGamepadButtonSelectorComboBox == null || LegionGamepadButtonSelectorComboBox.SelectedIndex < 0)
                return;

            var buttonName = GetGamepadButtonNameFromIndex(LegionGamepadButtonSelectorComboBox.SelectedIndex);

            // Get or create mapping
            if (!gamepadButtonMappings.TryGetValue(buttonName, out var mapping))
            {
                mapping = new ButtonMapping { Type = 1, KeyboardKeys = new List<int>() };
                gamepadButtonMappings[buttonName] = mapping;
            }

            // Add the key (LegionGamepadKeyComboBox is 1-indexed since 0 is "+ Key")
            // The key code is based on the combo box item order
            var keyCode = GetKeyCodeFromDropdownIndex(LegionGamepadKeyComboBox.SelectedIndex);
            if (!mapping.KeyboardKeys.Contains(keyCode))
            {
                mapping.KeyboardKeys.Add(keyCode);
                UpdateGamepadKeyboardKeyTags(mapping.KeyboardKeys);
            }

            // Reset dropdown to "+ Key"
            LegionGamepadKeyComboBox.SelectedIndex = 0;

            SaveAndSendGamepadMappings();
        }

        private void SaveAndSendGamepadMappings()
        {
            // Don't save during profile loading - we're just applying the profile, not modifying it
            // The profile will be fully applied and any saves will happen after isLoadingControllerProfile is cleared
            if (isLoadingControllerProfile)
            {
                // Skip sending if this is a duplicate call during profile loading
                // (the main send will happen via SendButtonMappingsToHelper at the end of ApplyControllerProfile)
                return;
            }

            // Skip if a profile was just applied (prevents duplicate sends from queued UI events)
            // HID commands take ~1.5s to complete, so use 2 second window
            if ((DateTime.Now - lastProfileApplyTime).TotalMilliseconds < 2000)
            {
                Logger.Info("SaveAndSendGamepadMappings skipped - profile was just applied");
                return;
            }

            // While Desktop Mode is active, all remaps belong to the Desktop profile, not the
            // underlying Global/Per-Game profile. Save to Desktop and send live to the helper.
            if (isDesktopModeActive)
            {
                SaveCurrentToDesktopProfile();
                SendButtonMappingsToHelper(desktopControllerProfile);
                UpdateGamepadMappingSummary();
                return;
            }

            // Get current profile
            ControllerProfile currentProfile;
            if (LegionControllerProfileToggle?.IsOn == true && HasValidGame(currentGameName))
            {
                gameControllerProfile = GetCurrentControllerProfileFromUI();
                SaveControllerProfileToStorage($"Game_{currentGameName}", gameControllerProfile);
                currentProfile = gameControllerProfile;
            }
            else
            {
                globalControllerProfile = GetCurrentControllerProfileFromUI();
                SaveControllerProfileToStorage("Global", globalControllerProfile);
                currentProfile = globalControllerProfile;
            }

            // Send to helper
            SendButtonMappingsToHelper(currentProfile);

            // Update the summary display
            UpdateGamepadMappingSummary();
        }
        private void LegionGamepadResetAll_Click(object sender, RoutedEventArgs e)
        {
            // Reset ALL gamepad buttons (including LS and RS stick directions) to their defaults
            // This ensures any button that might have been remapped is reset, not just those in the dictionary
            foreach (var buttonName in GamepadButtonNames)
            {
                gamepadButtonMappings[buttonName] = new ButtonMapping { Type = 0, GamepadAction = 0 };
            }

            // Send reset commands for all buttons - helper will clear then remap to self
            SaveAndSendGamepadMappings();

            Logger.Info($"Sent reset HID commands for all {GamepadButtonNames.Length} gamepad buttons");

            // Now clear the dictionary (buttons are now at default, no need to track them)
            gamepadButtonMappings.Clear();

            // Reset UI to defaults
            if (LegionGamepadTypeComboBox != null)
                LegionGamepadTypeComboBox.SelectedIndex = 0;
            if (LegionGamepadActionComboBox != null)
                LegionGamepadActionComboBox.SelectedIndex = 0;
            UpdateGamepadMappingUI(0);
            if (LegionGamepadKeyTags != null)
                LegionGamepadKeyTags.Children.Clear();

            // Update summary display (now empty)
            UpdateGamepadMappingSummary();

            Logger.Info("Reset all gamepad button mappings");
        }

        /// <summary>
        /// Updates the summary display showing which gamepad buttons are remapped.
        /// </summary>
        private void UpdateGamepadMappingSummary()
        {
            if (LegionGamepadRemappedTags == null || LegionGamepadRemappedLabel == null || LegionGamepadNoRemapsLabel == null)
                return;

            // Get list of remapped buttons (those with non-default mappings)
            var remappedButtons = gamepadButtonMappings
                .Where(kvp => IsButtonRemapped(kvp.Value))
                .Select(kvp => kvp.Key)
                .ToList();

            // Clear existing tags
            LegionGamepadRemappedTags.Items.Clear();

            if (remappedButtons.Count > 0)
            {
                LegionGamepadRemappedLabel.Visibility = Visibility.Visible;
                LegionGamepadNoRemapsLabel.Visibility = Visibility.Collapsed;

                foreach (var buttonName in remappedButtons)
                {
                    var mapping = gamepadButtonMappings[buttonName];
                    var tag = CreateRemappedButtonTag(buttonName, mapping);
                    LegionGamepadRemappedTags.Items.Add(tag);
                }
            }
            else
            {
                LegionGamepadRemappedLabel.Visibility = Visibility.Collapsed;
                LegionGamepadNoRemapsLabel.Visibility = Visibility.Visible;
            }
        }

        private bool IsButtonRemapped(ButtonMapping mapping)
        {
            if (mapping == null) return false;
            bool hasGamepadCombo = mapping.GamepadActions != null && mapping.GamepadActions.Count > 0;
            // Type 0 (Gamepad) with action 0 (Disabled) means default/cleared
            // Keyboard or Mouse type means remapped
            // Gamepad type with action > 0 means remapped
            return mapping.Type != 0 || mapping.GamepadAction > 0 || hasGamepadCombo ||
                   (mapping.KeyboardKeys != null && mapping.KeyboardKeys.Count > 0);
        }

        private Border CreateRemappedButtonTag(string buttonName, ButtonMapping mapping)
        {
            var displayName = GetGamepadButtonDisplayName(buttonName);
            var mappingDesc = GetMappingDescription(mapping);

            var tagBorder = new Border
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 50, 70, 90)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2, 6, 2),
                Margin = new Thickness(0, 0, 4, 0)
            };

            var tagPanel = new StackPanel { Orientation = Orientation.Horizontal };

            var buttonText = new TextBlock
            {
                Text = $"{displayName} → {mappingDesc}",
                Foreground = new SolidColorBrush(Windows.UI.Colors.White),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            };

            var clearButton = new Button
            {
                Content = "×",
                FontSize = 10,
                Padding = new Thickness(4, 0, 0, 0),
                Background = new SolidColorBrush(Windows.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                Foreground = new SolidColorBrush(Windows.UI.Colors.Gray),
                VerticalAlignment = VerticalAlignment.Center,
                MinWidth = 0,
                MinHeight = 0,
                Tag = buttonName
            };
            clearButton.Click += (s, e) => ClearSingleGamepadMapping((string)((Button)s).Tag);

            tagPanel.Children.Add(buttonText);
            tagPanel.Children.Add(clearButton);
            tagBorder.Child = tagPanel;

            // Click on tag to select that button
            tagBorder.Tapped += (s, e) =>
            {
                var index = Array.IndexOf(GamepadButtonNames, buttonName);
                if (index >= 0 && LegionGamepadButtonSelectorComboBox != null)
                    LegionGamepadButtonSelectorComboBox.SelectedIndex = index;
            };

            return tagBorder;
        }

        private void ClearSingleGamepadMapping(string buttonName)
        {
            if (gamepadButtonMappings.ContainsKey(buttonName))
            {
                // Set to reset state (Type=0, GamepadAction=0) to trigger HID command
                // This maps the button back to itself (default behavior)
                gamepadButtonMappings[buttonName] = new ButtonMapping { Type = 0, GamepadAction = 0 };
                SaveAndSendGamepadMappings();

                // Now remove from dictionary (button is at default, no need to track)
                gamepadButtonMappings.Remove(buttonName);

                // Update the summary display
                UpdateGamepadMappingSummary();

                // If this was the currently selected button, reload UI
                if (LegionGamepadButtonSelectorComboBox != null)
                {
                    var currentButtonName = GetGamepadButtonNameFromIndex(LegionGamepadButtonSelectorComboBox.SelectedIndex);
                    if (currentButtonName == buttonName)
                        LoadGamepadMappingToUI(buttonName);
                }

                Logger.Info($"Cleared gamepad button mapping for {buttonName} (sent HID reset command)");
            }
        }

        private string GetGamepadButtonDisplayName(string buttonName)
        {
            // Convert internal names to display names
            switch (buttonName)
            {
                case "LSClick": return "LS Click";
                case "LSUp": return "LS Up";
                case "LSDown": return "LS Down";
                case "LSLeft": return "LS Left";
                case "LSRight": return "LS Right";
                case "RSClick": return "RS Click";
                case "RSUp": return "RS Up";
                case "RSDown": return "RS Down";
                case "RSLeft": return "RS Left";
                case "RSRight": return "RS Right";
                case "DPadUp": return "D-Up";
                case "DPadDown": return "D-Down";
                case "DPadLeft": return "D-Left";
                case "DPadRight": return "D-Right";
                default: return buttonName;
            }
        }

        private string GetMappingDescription(ButtonMapping mapping)
        {
            if (mapping == null) return "Default";

            switch (mapping.Type)
            {
                case 0: // Gamepad
                    if (mapping.GamepadMode == 1)
                    {
                        var comboActions = mapping.GamepadActions ?? new List<int>();
                        if (comboActions.Count == 0 && mapping.GamepadAction > 0)
                        {
                            comboActions = new List<int> { mapping.GamepadAction };
                        }

                        if (comboActions.Count == 0)
                        {
                            return "Disabled";
                        }

                        string comboText = string.Join("+", comboActions.Select(GetGamepadActionName));
                        return mapping.Turbo ? $"{comboText} (Turbo)" : comboText;
                    }

                    if (mapping.GamepadAction == 0) return "Disabled";
                    string single = GetGamepadActionName(mapping.GamepadAction);
                    return mapping.Turbo ? $"{single} (Turbo)" : single;
                case 1: // Keyboard
                    if (mapping.KeyboardKeys == null || mapping.KeyboardKeys.Count == 0)
                        return "Keys";
                    return string.Join("+", mapping.KeyboardKeys.Select(k => GetKeyDisplayName(k)));
                case 2: // Mouse
                    return GetMouseActionName(mapping.MouseButton);
                default:
                    return "Unknown";
            }
        }

        private string GetGamepadActionName(int action)
        {
            string[] names = { "Disabled", "LS Click", "LS Up", "LS Down", "LS Left", "LS Right",
                              "RS Click", "RS Up", "RS Down", "RS Left", "RS Right",
                              "D-Up", "D-Down", "D-Left", "D-Right",
                              "A", "B", "X", "Y", "LB", "LT", "RB", "RT", "View", "Menu" };
            return action >= 0 && action < names.Length ? names[action] : $"Action{action}";
        }

        private string GetMouseActionName(int action)
        {
            string[] names = { "Left Click", "Right Click", "Middle Click",
                              "Scroll Up", "Scroll Down", "Scroll Left", "Scroll Right" };
            return action >= 0 && action < names.Length ? names[action] : $"Mouse{action}";
        }

    }
}
