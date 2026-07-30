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
        /// <summary>
        /// Writes one value into a settings container, absorbing the failure instead
        /// of aborting the caller mid-save. LocalSettings enforces a ~8 KB cap per
        /// value; an oversized string (large button-mapping JSON) throws, and without
        /// this guard the exception aborted SaveControllerProfileToStorage partway
        /// through — a torn profile where keys before the throw held new values and
        /// the rest stayed stale. Skipping the one oversized key keeps the previous
        /// stored value for it and lets every other key save normally.
        /// </summary>
        private static void SetSettingSafe(ApplicationDataContainer container, string key, object value)
        {
            try
            {
                container.Values[key] = value;
            }
            catch (Exception ex)
            {
                int len = (value as string)?.Length ?? -1;
                Logger.Error($"Failed to store setting '{key}'{(len >= 0 ? $" ({len} chars)" : "")}: {ex.Message} — skipped, previous stored value retained");
            }
        }

        private void SaveControllerProfileToStorage(string profileName, ControllerProfile profile)
        {
            // Never save to "No game detected" profile
            if (profileName.IndexOf("No game detected", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Logger.Warn($"Attempted to save controller profile with invalid name: {profileName}, skipping");
                return;
            }

            var settings = ApplicationData.Current.LocalSettings;
            var container = settings.CreateContainer($"ControllerProfile_{profileName}", ApplicationDataCreateDisposition.Always);

            // Button mappings (serialized as JSON)
            var y1Json = profile.ButtonY1.ToJson();
            var y2Json = profile.ButtonY2.ToJson();
            var desktopJson = profile.ButtonDesktop.ToJson();
            SetSettingSafe(container, "ButtonY1", y1Json);
            SetSettingSafe(container, "ButtonY2", y2Json);
            SetSettingSafe(container, "ButtonY3", profile.ButtonY3.ToJson());
            SetSettingSafe(container, "ButtonM1", profile.ButtonM1.ToJson());
            SetSettingSafe(container, "ButtonM2", profile.ButtonM2.ToJson());
            SetSettingSafe(container, "ButtonM3", profile.ButtonM3.ToJson());
            SetSettingSafe(container, "ButtonDesktop", desktopJson);
            SetSettingSafe(container, "ButtonPage", profile.ButtonPage.ToJson());
            Logger.Info($"SaveControllerProfile: {profileName} ButtonY1={y1Json}, ButtonY2={y2Json}, ButtonDesktop={desktopJson}");
            container.Values["NintendoLayout"] = profile.NintendoLayout;
            container.Values["VibrationLevel"] = profile.VibrationLevel;
            container.Values["VibrationMode"] = profile.VibrationMode;

            // Gyro settings
            container.Values["GyroTarget"] = profile.GyroTarget;
            container.Values["GyroSensitivityX"] = profile.GyroSensitivityX;
            container.Values["GyroSensitivityY"] = profile.GyroSensitivityY;
            container.Values["GyroInvertX"] = profile.GyroInvertX;
            container.Values["GyroInvertY"] = profile.GyroInvertY;
            container.Values["GyroMappingType"] = profile.GyroMappingType;
            container.Values["GyroActivationMode"] = profile.GyroActivationMode;
            container.Values["GyroActivationButton"] = profile.GyroActivationButton;

            // Advanced gyro settings
            container.Values["GyroDeadzone"] = profile.GyroDeadzone;

            // Stick deadzones
            container.Values["LeftStickDeadzone"] = profile.LeftStickDeadzone;
            container.Values["RightStickDeadzone"] = profile.RightStickDeadzone;

            // Trigger travel
            container.Values["LeftTriggerStart"] = profile.LeftTriggerStart;
            container.Values["LeftTriggerEnd"] = profile.LeftTriggerEnd;
            container.Values["RightTriggerStart"] = profile.RightTriggerStart;
            container.Values["RightTriggerEnd"] = profile.RightTriggerEnd;
            container.Values["HairTriggers"] = profile.HairTriggers;

            // Joystick as mouse
            container.Values["JoystickAsMouseMode"] = profile.JoystickAsMouseMode;
            container.Values["JoystickMouseSens"] = profile.JoystickMouseSens;

            // Gamepad button mappings (serialize dictionary as JSON)
            if (profile.GamepadButtonMappings != null && profile.GamepadButtonMappings.Count > 0)
            {
                var gamepadMappingsJson = SerializeGamepadButtonMappings(profile.GamepadButtonMappings);
                SetSettingSafe(container, "GamepadButtonMappings", gamepadMappingsJson);
            }
            else
            {
                container.Values["GamepadButtonMappings"] = "";
            }

            // Desktop Controls preset
            container.Values["DesktopControlsEnabled"] = profile.DesktopControlsEnabled;

            // Lighting
            container.Values["LightMode"] = profile.LightMode;
            container.Values["LightColorR"] = profile.LightColorR;
            container.Values["LightColorG"] = profile.LightColorG;
            container.Values["LightColorB"] = profile.LightColorB;
            container.Values["LightSpeed"] = profile.LightSpeed;
            container.Values["LightBrightness"] = profile.LightBrightness;
            container.Values["PowerLight"] = profile.PowerLight;

            // Store the game exe path for game profiles (used for loading icons)
            if (profileName.StartsWith("Game_") && !string.IsNullOrEmpty(currentGameExePath))
            {
                SetSettingSafe(container, "GameExePath", currentGameExePath);
            }

            Logger.Info($"Saved controller profile: {profileName}, LightMode={profile.LightMode}, Color=#{profile.LightColorR:X2}{profile.LightColorG:X2}{profile.LightColorB:X2}, Brightness={profile.LightBrightness}");
        }

        private ButtonMapping LoadButtonMapping(ApplicationDataContainer container, string key)
        {
            if (!container.Values.ContainsKey(key))
                return new ButtonMapping();

            var value = container.Values[key];

            // Handle backwards compatibility: old format stored int, new format stores JSON string
            if (value is int intValue)
            {
                // Old format: convert simple int to ButtonMapping with gamepad type
                return new ButtonMapping { Type = 0, GamepadAction = intValue };
            }
            else if (value is string jsonValue)
            {
                return ButtonMapping.FromJson(jsonValue);
            }

            return new ButtonMapping();
        }

        private void LoadControllerProfileFromStorage(string profileName, ControllerProfile profile)
        {
            var settings = ApplicationData.Current.LocalSettings;
            var containerKey = $"ControllerProfile_{profileName}";
            if (settings.Containers.ContainsKey(containerKey))
            {
                var container = settings.Containers[containerKey];

                // Log what's in the container for debugging
                var y1Raw = container.Values.ContainsKey("ButtonY1") ? container.Values["ButtonY1"]?.ToString() : "(not found)";
                var y2Raw = container.Values.ContainsKey("ButtonY2") ? container.Values["ButtonY2"]?.ToString() : "(not found)";
                var desktopRaw = container.Values.ContainsKey("ButtonDesktop") ? container.Values["ButtonDesktop"]?.ToString() : "(not found)";
                Logger.Info($"LoadControllerProfile: {profileName} raw values: ButtonY1={y1Raw}, ButtonY2={y2Raw}, ButtonDesktop={desktopRaw}");

                // Button mappings (with backwards compatibility for old int format)
                profile.ButtonY1 = LoadButtonMapping(container, "ButtonY1");
                profile.ButtonY2 = LoadButtonMapping(container, "ButtonY2");
                profile.ButtonY3 = LoadButtonMapping(container, "ButtonY3");
                profile.ButtonM1 = LoadButtonMapping(container, "ButtonM1");
                profile.ButtonM2 = LoadButtonMapping(container, "ButtonM2");
                profile.ButtonM3 = LoadButtonMapping(container, "ButtonM3");
                profile.ButtonDesktop = LoadButtonMapping(container, "ButtonDesktop");
                profile.ButtonPage = LoadButtonMapping(container, "ButtonPage");

                Logger.Info($"LoadControllerProfile: {profileName} parsed: Y1={FormatButtonMapping(profile.ButtonY1)}, Y2={FormatButtonMapping(profile.ButtonY2)}, Desktop={FormatButtonMapping(profile.ButtonDesktop)}");
                profile.NintendoLayout = container.Values.ContainsKey("NintendoLayout") ? (bool)container.Values["NintendoLayout"] : false;
                profile.VibrationLevel = container.Values.ContainsKey("VibrationLevel") ? (int)container.Values["VibrationLevel"] : 2;
                profile.VibrationMode = container.Values.ContainsKey("VibrationMode") ? (int)container.Values["VibrationMode"] : 1;

                // Gyro settings
                profile.GyroTarget = container.Values.ContainsKey("GyroTarget") ? (int)container.Values["GyroTarget"] : 0;
                profile.GyroSensitivityX = container.Values.ContainsKey("GyroSensitivityX") ? (int)container.Values["GyroSensitivityX"] : 50;
                profile.GyroSensitivityY = container.Values.ContainsKey("GyroSensitivityY") ? (int)container.Values["GyroSensitivityY"] : 50;
                profile.GyroInvertX = container.Values.ContainsKey("GyroInvertX") ? (bool)container.Values["GyroInvertX"] : false;
                profile.GyroInvertY = container.Values.ContainsKey("GyroInvertY") ? (bool)container.Values["GyroInvertY"] : false;
                profile.GyroMappingType = container.Values.ContainsKey("GyroMappingType") ? (int)container.Values["GyroMappingType"] : 0;
                profile.GyroActivationMode = container.Values.ContainsKey("GyroActivationMode") ? (int)container.Values["GyroActivationMode"] : 0;
                profile.GyroActivationButton = container.Values.ContainsKey("GyroActivationButton") ? (int)container.Values["GyroActivationButton"] : 0;

                // Advanced gyro settings
                profile.GyroDeadzone = container.Values.ContainsKey("GyroDeadzone") ? (int)container.Values["GyroDeadzone"] : 10;

                // Stick deadzones
                profile.LeftStickDeadzone = container.Values.ContainsKey("LeftStickDeadzone") ? (int)container.Values["LeftStickDeadzone"] : 4;
                profile.RightStickDeadzone = container.Values.ContainsKey("RightStickDeadzone") ? (int)container.Values["RightStickDeadzone"] : 4;

                // Trigger travel
                profile.LeftTriggerStart = container.Values.ContainsKey("LeftTriggerStart") ? (int)container.Values["LeftTriggerStart"] : 0;
                profile.LeftTriggerEnd = container.Values.ContainsKey("LeftTriggerEnd") ? (int)container.Values["LeftTriggerEnd"] : 0;
                profile.RightTriggerStart = container.Values.ContainsKey("RightTriggerStart") ? (int)container.Values["RightTriggerStart"] : 0;
                profile.RightTriggerEnd = container.Values.ContainsKey("RightTriggerEnd") ? (int)container.Values["RightTriggerEnd"] : 0;
                profile.HairTriggers = container.Values.ContainsKey("HairTriggers") ? (bool)container.Values["HairTriggers"] : false;

                // Joystick as mouse
                profile.JoystickAsMouseMode = container.Values.ContainsKey("JoystickAsMouseMode") ? (int)container.Values["JoystickAsMouseMode"] : 0;
                profile.JoystickMouseSens = container.Values.ContainsKey("JoystickMouseSens") ? (int)container.Values["JoystickMouseSens"] : 50;

                // Gamepad button mappings (deserialize from JSON)
                profile.GamepadButtonMappings = new Dictionary<string, ButtonMapping>();
                if (container.Values.ContainsKey("GamepadButtonMappings"))
                {
                    var gamepadMappingsJson = container.Values["GamepadButtonMappings"] as string;
                    if (!string.IsNullOrEmpty(gamepadMappingsJson))
                    {
                        try
                        {
                            profile.GamepadButtonMappings = DeserializeGamepadButtonMappings(gamepadMappingsJson);
                        }
                        catch (Exception ex)
                        {
                            Logger.Error($"Error loading gamepad button mappings: {ex.Message}");
                        }
                    }
                }

                // Desktop Controls preset
                profile.DesktopControlsEnabled = container.Values.ContainsKey("DesktopControlsEnabled")
                    ? (bool)container.Values["DesktopControlsEnabled"]
                    : false;

                // Lighting - only load if explicitly saved (to avoid defaulting to white for old profiles)
                profile.HasExplicitLighting = container.Values.ContainsKey("LightColorR");
                profile.LightMode = container.Values.ContainsKey("LightMode") ? (int)container.Values["LightMode"] : 1;
                profile.LightSpeed = container.Values.ContainsKey("LightSpeed") ? (int)container.Values["LightSpeed"] : 50;
                profile.LightBrightness = container.Values.ContainsKey("LightBrightness") ? (int)container.Values["LightBrightness"] : 50;
                profile.PowerLight = container.Values.ContainsKey("PowerLight") ? (bool)container.Values["PowerLight"] : true;

                if (profile.HasExplicitLighting)
                {
                    profile.LightColorR = (byte)container.Values["LightColorR"];
                    profile.LightColorG = container.Values.ContainsKey("LightColorG") ? (byte)container.Values["LightColorG"] : (byte)255;
                    profile.LightColorB = container.Values.ContainsKey("LightColorB") ? (byte)container.Values["LightColorB"] : (byte)255;

                    // Check if saved color is the default white (#FFFFFF) with high brightness
                    // This likely means the profile was saved before the user set their preferred color
                    // In this case, inherit from main lighting to prevent unexpected white lights
                    bool isDefaultWhite = profile.LightColorR == 255 && profile.LightColorG == 255 && profile.LightColorB == 255;
                    bool hasHighBrightness = profile.LightBrightness >= 90;  // 90% or higher suggests default
                    if (isDefaultWhite && hasHighBrightness)
                    {
                        Logger.Info($"Controller profile '{profileName}' has default white (#FFFFFF) with high brightness ({profile.LightBrightness}%) - inheriting main lighting instead");
                        InheritMainLightingSettings(profile);
                    }
                }
                else
                {
                    // No explicit lighting saved - inherit from current main lighting settings
                    // This prevents profiles from defaulting to white and ensures consistency
                    InheritMainLightingSettings(profile);
                }

                Logger.Info($"Loaded controller profile: {profileName} (HasExplicitLighting={profile.HasExplicitLighting}, LightMode={profile.LightMode}, Color=#{profile.LightColorR:X2}{profile.LightColorG:X2}{profile.LightColorB:X2}, Brightness={profile.LightBrightness})");
            }
            else
            {
                Logger.Warn($"Controller profile container not found: {containerKey} - using defaults");
                // Even for new profiles, inherit current main lighting settings
                InheritMainLightingSettings(profile);
            }
        }

        /// <summary>
        /// Copies current main lighting settings into a controller profile.
        /// Used when a profile has no explicit lighting saved to prevent defaulting to white.
        /// </summary>
        private void InheritMainLightingSettings(ControllerProfile profile)
        {
            try
            {
                // Get current light mode
                if (legionLightMode != null)
                {
                    profile.LightMode = legionLightMode.Value;
                }

                // Get current light color from hex string (e.g., "#RRGGBB" or "RRGGBB")
                if (legionLightColor != null && !string.IsNullOrEmpty(legionLightColor.Value))
                {
                    string hex = legionLightColor.Value.TrimStart('#');
                    if (hex.Length >= 6)
                    {
                        profile.LightColorR = Convert.ToByte(hex.Substring(0, 2), 16);
                        profile.LightColorG = Convert.ToByte(hex.Substring(2, 2), 16);
                        profile.LightColorB = Convert.ToByte(hex.Substring(4, 2), 16);
                    }
                }

                // Get current brightness
                if (legionLightBrightness != null)
                {
                    profile.LightBrightness = legionLightBrightness.Value;
                }

                // Get current speed
                if (legionLightSpeed != null)
                {
                    profile.LightSpeed = legionLightSpeed.Value;
                }

                // Get current power light state
                if (legionPowerLight != null)
                {
                    profile.PowerLight = legionPowerLight.Value;
                }

                Logger.Info($"Inherited main lighting settings: Mode={profile.LightMode}, Color=#{profile.LightColorR:X2}{profile.LightColorG:X2}{profile.LightColorB:X2}, Brightness={profile.LightBrightness}");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error inheriting main lighting settings: {ex.Message}");
            }
        }

        private void InitializeButtonMappingEvents(string buttonName)
        {
            var typeCombo = FindName($"LegionButton{buttonName}TypeComboBox") as ComboBox;
            var gamepadCombo = FindName($"LegionButton{buttonName}ComboBox") as ComboBox;
            var mouseCombo = FindName($"LegionButton{buttonName}MouseComboBox") as ComboBox;
            var keyCombo = FindName($"LegionButton{buttonName}KeyComboBox") as ComboBox;

            EnsureButtonGamepadComboControls(buttonName);

            if (typeCombo != null)
            {
                typeCombo.SelectionChanged += (s, e) => OnButtonTypeChanged(buttonName);
            }
            if (gamepadCombo != null)
            {
                gamepadCombo.SelectionChanged += (s, e) => OnButtonGamepadActionSelected(buttonName);
            }
            if (mouseCombo != null)
            {
                mouseCombo.SelectionChanged += ControllerSettingChanged;
            }
            if (keyCombo != null)
            {
                keyCombo.SelectionChanged += (s, e) => OnKeyboardKeySelected(buttonName);
            }
        }

        // System-action remap support (field request: invoke Touch Keyboard / Task
        // Manager / Screenshot etc. from a Legion button, like the System tab's
        // hotkeys). The action id shares the HotkeyAction enum used by
        // ExecuteControllerHotkeyAction on the helper (3=ToggleOSD, 4=Screenshot,
        // 5=AltTab, 6=AltF4, 7=TouchKeyboard, 8=CtrlAltDel, 9=TaskManager,
        // 10=FocusGoTweaks) and travels in the mapping JSON's MouseButton field
        // with Type=3 - no schema change. The combo is created programmatically
        // next to each button's mouse combo (same pattern as the gamepad combo
        // controls) to avoid 8 more hand-written XAML rows.
        private static readonly string[] SystemActionLabels =
            { "Screenshot", "Alt+Tab", "Alt+F4", "Touch Keyboard", "Ctrl+Alt+Del", "Task Manager", "Toggle OSD", "Focus GoTweaks" };
        private static readonly int[] SystemActionIds = { 4, 5, 6, 7, 8, 9, 3, 10 };
        private readonly Dictionary<string, ComboBox> _buttonSystemCombos = new Dictionary<string, ComboBox>();

        private ComboBox EnsureButtonSystemCombo(string buttonName)
        {
            if (_buttonSystemCombos.TryGetValue(buttonName, out var existing) && existing != null)
                return existing;

            var mouseCombo = FindName($"LegionButton{buttonName}MouseComboBox") as ComboBox;
            if (mouseCombo == null || !(mouseCombo.Parent is Panel parent))
                return null;

            var combo = new ComboBox
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Visibility = Visibility.Collapsed,
                SelectedIndex = 0,
            };
            if (Resources["ModernComboBoxStyle"] is Style style) combo.Style = style;
            foreach (var label in SystemActionLabels) combo.Items.Add(label);
            Grid.SetColumn(combo, Grid.GetColumn(mouseCombo));
            Grid.SetRow(combo, Grid.GetRow(mouseCombo));
            combo.SelectionChanged += ControllerSettingChanged;
            combo.SelectionChanged += (s, e) => RefreshLegionRemapSummary();

            int insertAt = parent.Children.IndexOf(mouseCombo) + 1;
            parent.Children.Insert(insertAt, combo);
            _buttonSystemCombos[buttonName] = combo;
            return combo;
        }

        private int SystemActionIdFromCombo(string buttonName)
        {
            if (_buttonSystemCombos.TryGetValue(buttonName, out var combo) && combo != null &&
                combo.SelectedIndex >= 0 && combo.SelectedIndex < SystemActionIds.Length)
            {
                return SystemActionIds[combo.SelectedIndex];
            }
            return SystemActionIds[0];
        }

        private void SetSystemComboFromActionId(string buttonName, int actionId)
        {
            var combo = EnsureButtonSystemCombo(buttonName);
            if (combo == null) return;
            int idx = Array.IndexOf(SystemActionIds, actionId);
            combo.SelectedIndex = idx >= 0 ? idx : 0;
        }

        private void OnButtonTypeChanged(string buttonName)
        {
            PreserveLegionScroll();
            var typeCombo = FindName($"LegionButton{buttonName}TypeComboBox") as ComboBox;
            var gamepadCombo = FindName($"LegionButton{buttonName}ComboBox") as ComboBox;
            var mouseCombo = FindName($"LegionButton{buttonName}MouseComboBox") as ComboBox;
            var keyboardPanel = FindName($"LegionButton{buttonName}KeyboardPanel") as StackPanel;

            if (typeCombo == null) return;
            int type = typeCombo.SelectedIndex;

            // Show/hide appropriate controls
            if (gamepadCombo != null)
                gamepadCombo.Visibility = type == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (mouseCombo != null)
                mouseCombo.Visibility = type == 2 ? Visibility.Visible : Visibility.Collapsed;
            if (keyboardPanel != null)
                keyboardPanel.Visibility = type == 1 ? Visibility.Visible : Visibility.Collapsed;
            var systemCombo = EnsureButtonSystemCombo(buttonName);
            if (systemCombo != null)
                systemCombo.Visibility = type == 3 ? Visibility.Visible : Visibility.Collapsed;

            UpdateButtonGamepadComboControls(buttonName);

            // Update the profile and send command
            if (!isLoadingControllerProfile && !isSwitchingControllerProfile)
            {
                ControllerSettingChanged(typeCombo, null);
            }
        }

        private bool IsImprovedButtonComboUiEnabled()
        {
            // The legacy "Improved Input" toggle that used to gate this (default off,
            // never turned on since VIIPER became the sole emulation backend) was
            // removed along with the rest of the dead legacy Controller Emulation
            // panel. No UI can set it to true anymore, so this preserves the
            // original default.
            return false;
        }

        private List<int> NormalizeGamepadActions(List<int> actions)
        {
            if (actions == null || actions.Count == 0)
            {
                return new List<int>();
            }

            var normalized = new List<int>();
            for (int i = 0; i < actions.Count; i++)
            {
                int action = actions[i];
                if (action <= 0)
                {
                    continue;
                }

                if (!normalized.Contains(action))
                {
                    normalized.Add(action);
                }
            }

            return normalized;
        }

        private List<int> GetStoredGamepadComboActions(string buttonName)
        {
            if (_buttonGamepadComboActions.TryGetValue(buttonName, out var actions))
            {
                return new List<int>(actions);
            }

            return new List<int>();
        }

        private void SetStoredGamepadComboActions(string buttonName, List<int> actions)
        {
            _buttonGamepadComboActions[buttonName] = NormalizeGamepadActions(actions);
        }

        private bool GetStoredButtonTurbo(string buttonName)
        {
            return _buttonGamepadTurbo.TryGetValue(buttonName, out var turbo) && turbo;
        }

        private void SetStoredButtonTurbo(string buttonName, bool turbo)
        {
            _buttonGamepadTurbo[buttonName] = turbo;
        }

        private int GetStoredButtonGamepadMode(string buttonName)
        {
            if (_buttonGamepadMode.TryGetValue(buttonName, out int mode))
            {
                return mode == 1 ? 1 : 0;
            }

            return 0;
        }

        private void SetStoredButtonGamepadMode(string buttonName, int mode)
        {
            _buttonGamepadMode[buttonName] = mode == 1 ? 1 : 0;
        }

        private void EnsureButtonGamepadComboControls(string buttonName)
        {
            if (_buttonGamepadComboRootPanels.ContainsKey(buttonName))
            {
                return;
            }

            var keyboardPanel = FindName($"LegionButton{buttonName}KeyboardPanel") as StackPanel;
            // The keyboard panel moved into the editor grid's Mapping column, so anchor the
            // injected combo-editor UI on the row's outer StackPanel instead of its parent.
            var container = FindName($"LegionButton{buttonName}Row") as StackPanel;
            if (keyboardPanel == null || container == null)
            {
                return;
            }

            var rootPanel = new StackPanel
            {
                Visibility = Visibility.Collapsed,
                Margin = new Thickness(keyboardPanel.Margin.Left, 8, 0, 0)
            };

            var modeRow = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            modeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
            modeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            modeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });

            var modeCombo = new ComboBox
            {
                Margin = new Thickness(0, 0, 8, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                SelectedIndex = 0
            };
            modeCombo.Items.Add("Single");
            modeCombo.Items.Add("Combo");

            Grid.SetColumn(modeCombo, 0);

            var addCombo = new ComboBox
            {
                Margin = new Thickness(0, 0, 8, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                SelectedIndex = 0,
                Visibility = Visibility.Collapsed
            };
            addCombo.Items.Add("+ Button");

            Grid.SetColumn(addCombo, 1);

            var turboCheck = new CheckBox
            {
                Content = "Turbo",
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 170, 170, 170))
            };
            Grid.SetColumn(turboCheck, 2);

            if (Resources.TryGetValue("ModernComboBoxStyle", out object comboStyleObj) && comboStyleObj is Style comboStyle)
            {
                modeCombo.Style = comboStyle;
            }

            modeRow.Children.Add(modeCombo);
            modeRow.Children.Add(addCombo);
            modeRow.Children.Add(turboCheck);

            var comboEditorRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 6, 0, 0),
                Visibility = Visibility.Collapsed
            };

            var comboTags = new StackPanel
            {
                Orientation = Orientation.Horizontal
            };

            // Indices align with RemapAction indices (1..N) by design.
            var sourceGamepadCombo = FindName($"LegionButton{buttonName}ComboBox") as ComboBox;
            if (sourceGamepadCombo != null)
            {
                for (int action = 1; action < sourceGamepadCombo.Items.Count; action++)
                {
                    addCombo.Items.Add(GetGamepadActionName(action));
                }
            }

            if (Resources.TryGetValue("ModernComboBoxStyle", out object addComboStyleObj) && addComboStyleObj is Style addComboStyle)
            {
                addCombo.Style = addComboStyle;
            }

            comboEditorRow.Children.Add(comboTags);

            rootPanel.Children.Add(modeRow);
            rootPanel.Children.Add(comboEditorRow);
            container.Children.Add(rootPanel);

            _buttonGamepadComboRootPanels[buttonName] = rootPanel;
            _buttonGamepadModeCombos[buttonName] = modeCombo;
            _buttonGamepadComboEditorRows[buttonName] = comboEditorRow;
            _buttonGamepadComboTags[buttonName] = comboTags;
            _buttonGamepadComboAddCombos[buttonName] = addCombo;
            _buttonGamepadTurboChecks[buttonName] = turboCheck;

            modeCombo.SelectionChanged += (s, e) => OnButtonGamepadModeChanged(buttonName);
            addCombo.SelectionChanged += (s, e) => OnButtonGamepadComboActionSelected(buttonName);
            turboCheck.Click += (s, e) => OnButtonGamepadTurboToggled(buttonName);
        }

        private void OnButtonGamepadActionSelected(string buttonName)
        {
            if (GetStoredButtonGamepadMode(buttonName) == 0)
            {
                var gamepadCombo = FindName($"LegionButton{buttonName}ComboBox") as ComboBox;
                int selectedAction = gamepadCombo?.SelectedIndex ?? 0;
                SetStoredGamepadComboActions(buttonName, selectedAction > 0
                    ? new List<int> { selectedAction }
                    : new List<int>());
                UpdateGamepadComboActionTags(buttonName, GetStoredGamepadComboActions(buttonName));
            }

            if (!isLoadingControllerProfile && !isSwitchingControllerProfile)
            {
                ControllerSettingChanged(null, null);
            }
        }

        private void OnButtonGamepadModeChanged(string buttonName)
        {
            if (isUpdatingButtonComboUi)
            {
                return;
            }

            if (!_buttonGamepadModeCombos.TryGetValue(buttonName, out ComboBox modeCombo))
            {
                return;
            }

            int mode = modeCombo.SelectedIndex == 1 ? 1 : 0;
            SetStoredButtonGamepadMode(buttonName, mode);

            if (mode == 0)
            {
                var gamepadCombo = FindName($"LegionButton{buttonName}ComboBox") as ComboBox;
                int selectedAction = gamepadCombo?.SelectedIndex ?? 0;
                SetStoredGamepadComboActions(buttonName, selectedAction > 0
                    ? new List<int> { selectedAction }
                    : new List<int>());
            }
            else
            {
                var actions = GetStoredGamepadComboActions(buttonName);
                if (actions.Count == 0)
                {
                    var gamepadCombo = FindName($"LegionButton{buttonName}ComboBox") as ComboBox;
                    int selectedAction = gamepadCombo?.SelectedIndex ?? 0;
                    if (selectedAction > 0)
                    {
                        SetStoredGamepadComboActions(buttonName, new List<int> { selectedAction });
                    }
                }
            }

            UpdateButtonGamepadComboControls(buttonName);
            if (!isLoadingControllerProfile && !isSwitchingControllerProfile)
            {
                ControllerSettingChanged(modeCombo, null);
            }
        }

        private void OnButtonGamepadComboActionSelected(string buttonName)
        {
            if (isUpdatingButtonComboUi)
            {
                return;
            }

            if (!_buttonGamepadComboAddCombos.TryGetValue(buttonName, out ComboBox addCombo))
            {
                return;
            }

            int action = addCombo.SelectedIndex;
            if (action <= 0)
            {
                return;
            }

            var actions = GetStoredGamepadComboActions(buttonName);
            if (!actions.Contains(action))
            {
                actions.Add(action);
                SetStoredGamepadComboActions(buttonName, actions);
                UpdateGamepadComboActionTags(buttonName, actions);

                if (!isLoadingControllerProfile && !isSwitchingControllerProfile)
                {
                    ControllerSettingChanged(addCombo, null);
                }
            }

            addCombo.SelectedIndex = 0;
        }

        private void OnButtonGamepadTurboToggled(string buttonName)
        {
            if (isUpdatingButtonComboUi)
            {
                return;
            }

            if (!_buttonGamepadTurboChecks.TryGetValue(buttonName, out CheckBox turboCheck))
            {
                return;
            }

            SetStoredButtonTurbo(buttonName, turboCheck.IsChecked == true);
            if (!isLoadingControllerProfile && !isSwitchingControllerProfile)
            {
                ControllerSettingChanged(turboCheck, null);
            }
        }

        private void RemoveGamepadComboActionFromButton(string buttonName, int action)
        {
            var actions = GetStoredGamepadComboActions(buttonName);
            actions.Remove(action);
            SetStoredGamepadComboActions(buttonName, actions);
            UpdateGamepadComboActionTags(buttonName, actions);

            if (!isLoadingControllerProfile && !isSwitchingControllerProfile)
            {
                ControllerSettingChanged(null, null);
            }
        }

        private void UpdateGamepadComboActionTags(string buttonName, List<int> actions)
        {
            if (!_buttonGamepadComboTags.TryGetValue(buttonName, out StackPanel tagPanel) || tagPanel == null)
            {
                return;
            }

            tagPanel.Children.Clear();
            if (actions == null)
            {
                return;
            }

            foreach (int action in NormalizeGamepadActions(actions))
            {
                var tagBorder = new Border
                {
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 60, 60, 60)),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 2, 6, 2),
                    Margin = new Thickness(0, 0, 4, 0)
                };

                var tagContent = new StackPanel { Orientation = Orientation.Horizontal };
                var text = new TextBlock
                {
                    Text = GetGamepadActionName(action),
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
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 180, 180, 180)),
                    VerticalAlignment = VerticalAlignment.Center,
                    MinWidth = 0,
                    MinHeight = 0
                };

                int actionToRemove = action;
                removeButton.Click += (s, e) => RemoveGamepadComboActionFromButton(buttonName, actionToRemove);

                tagContent.Children.Add(text);
                tagContent.Children.Add(removeButton);
                tagBorder.Child = tagContent;
                tagPanel.Children.Add(tagBorder);
            }
        }

        private void UpdateButtonGamepadComboControls(string buttonName)
        {
            EnsureButtonGamepadComboControls(buttonName);
            if (!_buttonGamepadComboRootPanels.TryGetValue(buttonName, out StackPanel rootPanel))
            {
                return;
            }

            var typeCombo = FindName($"LegionButton{buttonName}TypeComboBox") as ComboBox;
            var gamepadCombo = FindName($"LegionButton{buttonName}ComboBox") as ComboBox;
            bool isGamepadType = typeCombo?.SelectedIndex == 0;
            bool showComboUi = IsImprovedButtonComboUiEnabled() && isGamepadType;

            rootPanel.Visibility = showComboUi ? Visibility.Visible : Visibility.Collapsed;
            if (!showComboUi)
            {
                if (gamepadCombo != null && isGamepadType)
                {
                    gamepadCombo.Visibility = Visibility.Visible;
                }

                return;
            }

            int mode = GetStoredButtonGamepadMode(buttonName);
            bool comboMode = mode == 1;

            isUpdatingButtonComboUi = true;
            try
            {
                if (_buttonGamepadModeCombos.TryGetValue(buttonName, out ComboBox modeCombo) &&
                    modeCombo != null &&
                    modeCombo.SelectedIndex != mode)
                {
                    modeCombo.SelectedIndex = mode;
                }

                if (_buttonGamepadTurboChecks.TryGetValue(buttonName, out CheckBox turboCheck) &&
                    turboCheck != null &&
                    turboCheck.IsChecked != GetStoredButtonTurbo(buttonName))
                {
                    turboCheck.IsChecked = GetStoredButtonTurbo(buttonName);
                }
            }
            finally
            {
                isUpdatingButtonComboUi = false;
            }

            if (_buttonGamepadComboEditorRows.TryGetValue(buttonName, out StackPanel editorRow) && editorRow != null)
            {
                editorRow.Visibility = comboMode ? Visibility.Visible : Visibility.Collapsed;
            }

            if (_buttonGamepadComboAddCombos.TryGetValue(buttonName, out ComboBox addCombo) && addCombo != null)
            {
                addCombo.Visibility = comboMode ? Visibility.Visible : Visibility.Collapsed;
            }

            UpdateGamepadComboActionTags(buttonName, GetStoredGamepadComboActions(buttonName));

            if (gamepadCombo != null)
            {
                gamepadCombo.Visibility = comboMode ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        private void OnKeyboardKeySelected(string buttonName)
        {
            var keyCombo = FindName($"LegionButton{buttonName}KeyComboBox") as ComboBox;
            if (keyCombo == null || keyCombo.SelectedIndex <= 0) return;  // 0 is "+ Key"

            // Get the key code from the dropdown index
            int keyCode = GetKeyCodeFromDropdownIndex(keyCombo.SelectedIndex);
            if (keyCode == 0) return;

            // Get current keys and add the new one (max 5)
            var keys = GetStoredKeyboardKeys(buttonName);
            if (keys.Count >= 5)
            {
                keyCombo.SelectedIndex = 0;
                return;  // Max 5 keys
            }

            if (!keys.Contains(keyCode))
            {
                keys.Add(keyCode);
                SetStoredKeyboardKeys(buttonName, keys);
                UpdateKeyboardKeyTags(buttonName, keys);

                // Trigger profile save and command send
                if (!isLoadingControllerProfile && !isSwitchingControllerProfile)
                {
                    ControllerSettingChanged(keyCombo, null);
                }
            }

            // Reset dropdown
            keyCombo.SelectedIndex = 0;
        }

        private int GetKeyCodeFromDropdownIndex(int index)
        {
            // Map dropdown index to HID key code
            // Index 0 is "+ Key" placeholder
            // Index 1-26 are A-Z (0x04-0x1D)
            // Index 27-36 are 1-0 (0x1E-0x27)
            // Index 37-48 are F1-F12 (0x3A-0x45)
            // Index 49-53 are Enter, Esc, Space, Tab, Backspace (0x28-0x2C)
            // Index 54-57 are Up, Down, Left, Right (0x52, 0x51, 0x50, 0x4F)
            // Index 58-65 are modifier keys (0xE0-0xE7)
            // Index 66-76 are navigation/media keys
            // Index 77-78 are bracket keys

            if (index <= 0) return 0;
            if (index <= 26) return 0x04 + (index - 1);   // A-Z: indices 1-26 → 0x04-0x1D
            if (index <= 36) return 0x1E + (index - 27);  // 1-0: indices 27-36 → 0x1E-0x27
            if (index <= 48) return 0x3A + (index - 37);  // F1-F12: indices 37-48 → 0x3A-0x45
            if (index == 49) return 0x28;  // Enter
            if (index == 50) return 0x29;  // Esc
            if (index == 51) return 0x2C;  // Space
            if (index == 52) return 0x2B;  // Tab
            if (index == 53) return 0x2A;  // Backspace
            if (index == 54) return 0x52;  // Up
            if (index == 55) return 0x51;  // Down
            if (index == 56) return 0x50;  // Left
            if (index == 57) return 0x4F;  // Right
            // Modifier keys
            if (index == 58) return 0xE0;  // LCtrl
            if (index == 59) return 0xE1;  // LShift
            if (index == 60) return 0xE2;  // LAlt
            if (index == 61) return 0xE3;  // LWin (HID Left GUI)
            if (index == 62) return 0xE4;  // RCtrl
            if (index == 63) return 0xE5;  // RShift
            if (index == 64) return 0xE6;  // RAlt
            if (index == 65) return 0xE7;  // RWin (HID Right GUI)
            // Navigation keys
            if (index == 66) return 0x4A;  // Home
            if (index == 67) return 0x4D;  // End
            if (index == 68) return 0x4B;  // PgUp
            if (index == 69) return 0x4E;  // PgDn
            if (index == 70) return 0x49;  // Insert
            if (index == 71) return 0x4C;  // Delete
            if (index == 72) return 0x46;  // PrintScr
            if (index == 73) return 0x48;  // Pause
            // Media keys (HID Keyboard page)
            if (index == 74) return 0x80;  // VolUp
            if (index == 75) return 0x81;  // VolDown
            if (index == 76) return 0x7F;  // VolMute
            // Bracket keys
            if (index == 77) return 0x2F;  // [ LeftBracket
            if (index == 78) return 0x30;  // ] RightBracket

            return 0;
        }

        private void RemoveKeyFromButton(string buttonName, int keyCode)
        {
            var keys = GetStoredKeyboardKeys(buttonName);
            keys.Remove(keyCode);
            SetStoredKeyboardKeys(buttonName, keys);
            UpdateKeyboardKeyTags(buttonName, keys);

            // Trigger profile save and command send
            if (!isLoadingControllerProfile && !isSwitchingControllerProfile)
            {
                ControllerSettingChanged(null, null);
            }
        }

        private string FormatButtonMapping(ButtonMapping mapping)
        {
            if (mapping == null) return "none";
            switch (mapping.Type)
            {
                case 0:
                    if (mapping.GamepadMode == 1)
                    {
                        string actions = mapping.GamepadActions != null && mapping.GamepadActions.Count > 0
                            ? string.Join("+", mapping.GamepadActions)
                            : mapping.GamepadAction.ToString();
                        return $"GP:Combo[{actions}] {(mapping.Turbo ? "Turbo" : "")}".Trim();
                    }
                    return $"GP:{mapping.GamepadAction}{(mapping.Turbo ? " Turbo" : "")}";
                case 1: return $"KB:[{string.Join(",", mapping.KeyboardKeys)}]";
                case 2: return $"MS:{mapping.MouseButton}";
                default: return "?";
            }
        }

        private void ApplyButtonMappingToUI(string buttonName, ButtonMapping mapping)
        {
            // Find the controls by name using reflection-like approach
            var typeCombo = FindName($"LegionButton{buttonName}TypeComboBox") as ComboBox;
            var gamepadCombo = FindName($"LegionButton{buttonName}ComboBox") as ComboBox;
            var mouseCombo = FindName($"LegionButton{buttonName}MouseComboBox") as ComboBox;
            var keyboardPanel = FindName($"LegionButton{buttonName}KeyboardPanel") as StackPanel;
            EnsureButtonGamepadComboControls(buttonName);

            if (mapping == null) mapping = new ButtonMapping();

            if (mapping.GamepadActions == null || mapping.GamepadActions.Count == 0)
            {
                if (mapping.GamepadAction > 0)
                {
                    mapping.GamepadActions = new List<int> { mapping.GamepadAction };
                }
                else
                {
                    mapping.GamepadActions = new List<int>();
                }
            }

            SetStoredGamepadComboActions(buttonName, mapping.GamepadActions);
            SetStoredButtonTurbo(buttonName, mapping.Turbo);
            int effectiveMode = mapping.GamepadMode == 1 || (mapping.GamepadActions?.Count ?? 0) > 1 ? 1 : 0;
            SetStoredButtonGamepadMode(buttonName, effectiveMode);

            // Set type dropdown
            if (typeCombo != null)
                typeCombo.SelectedIndex = mapping.Type;

            // Show/hide appropriate controls
            if (gamepadCombo != null)
            {
                gamepadCombo.Visibility = mapping.Type == 0 ? Visibility.Visible : Visibility.Collapsed;
                if (mapping.Type == 0)
                    gamepadCombo.SelectedIndex = mapping.GamepadAction;
            }
            if (mouseCombo != null)
            {
                mouseCombo.Visibility = mapping.Type == 2 ? Visibility.Visible : Visibility.Collapsed;
                if (mapping.Type == 2)
                    mouseCombo.SelectedIndex = mapping.MouseButton;
            }
            if (keyboardPanel != null)
            {
                keyboardPanel.Visibility = mapping.Type == 1 ? Visibility.Visible : Visibility.Collapsed;
                if (mapping.Type == 1)
                    UpdateKeyboardKeyTags(buttonName, mapping.KeyboardKeys);
            }
            var sysCombo = EnsureButtonSystemCombo(buttonName);
            if (sysCombo != null)
            {
                sysCombo.Visibility = mapping.Type == 3 ? Visibility.Visible : Visibility.Collapsed;
                if (mapping.Type == 3)
                    SetSystemComboFromActionId(buttonName, mapping.MouseButton);
            }

            if (_buttonGamepadModeCombos.TryGetValue(buttonName, out ComboBox modeCombo) && modeCombo != null)
            {
                int mode = GetStoredButtonGamepadMode(buttonName);
                if (modeCombo.SelectedIndex != mode)
                {
                    modeCombo.SelectedIndex = mode;
                }
            }

            if (_buttonGamepadTurboChecks.TryGetValue(buttonName, out CheckBox turboCheck) && turboCheck != null)
            {
                bool turbo = GetStoredButtonTurbo(buttonName);
                if (turboCheck.IsChecked != turbo)
                {
                    turboCheck.IsChecked = turbo;
                }
            }

            UpdateGamepadComboActionTags(buttonName, GetStoredGamepadComboActions(buttonName));
            UpdateButtonGamepadComboControls(buttonName);
        }

        private void UpdateKeyboardKeyTags(string buttonName, List<int> keys)
        {
            var keyTags = FindName($"LegionButton{buttonName}KeyTags") as Panel;
            if (keyTags == null) return;

            keyTags.Children.Clear();
            if (keys == null) return;

            var tagElements = new List<FrameworkElement>();
            foreach (var key in keys)
            {
                // Create a tag with the key name and X button to remove
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
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 180, 180, 180)),
                    VerticalAlignment = VerticalAlignment.Center,
                    MinWidth = 0,
                    MinHeight = 0
                };

                // Capture the key code for the click handler
                int keyCode = key;
                string btnName = buttonName;
                removeButton.Click += (s, e) => RemoveKeyFromButton(btnName, keyCode);

                tagPanel.Children.Add(keyText);
                tagPanel.Children.Add(removeButton);
                tagBorder.Child = tagPanel;
                tagElements.Add(tagBorder);
            }

            FillWrapRows(keyTags, tagElements, GetKeyTagsWrapWidth(keyTags));
        }

        private string GetKeyDisplayName(int keyCode)
        {
            // Map key codes to display names
            var keyNames = new Dictionary<int, string>
            {
                { 0x04, "A" }, { 0x05, "B" }, { 0x06, "C" }, { 0x07, "D" }, { 0x08, "E" },
                { 0x09, "F" }, { 0x0A, "G" }, { 0x0B, "H" }, { 0x0C, "I" }, { 0x0D, "J" },
                { 0x0E, "K" }, { 0x0F, "L" }, { 0x10, "M" }, { 0x11, "N" }, { 0x12, "O" },
                { 0x13, "P" }, { 0x14, "Q" }, { 0x15, "R" }, { 0x16, "S" }, { 0x17, "T" },
                { 0x18, "U" }, { 0x19, "V" }, { 0x1A, "W" }, { 0x1B, "X" }, { 0x1C, "Y" },
                { 0x1D, "Z" }, { 0x1E, "1" }, { 0x1F, "2" }, { 0x20, "3" }, { 0x21, "4" },
                { 0x22, "5" }, { 0x23, "6" }, { 0x24, "7" }, { 0x25, "8" }, { 0x26, "9" },
                { 0x27, "0" }, { 0x28, "Enter" }, { 0x29, "Esc" }, { 0x2A, "Backspace" },
                { 0x2B, "Tab" }, { 0x2C, "Space" }, { 0x2D, "-" }, { 0x2E, "=" },
                { 0x2F, "[" }, { 0x30, "]" }, { 0x31, "\\" }, { 0x33, ";" }, { 0x34, "'" },
                { 0x35, "`" }, { 0x36, "," }, { 0x37, "." }, { 0x38, "/" }, { 0x39, "CapsLock" },
                { 0x3A, "F1" }, { 0x3B, "F2" }, { 0x3C, "F3" }, { 0x3D, "F4" }, { 0x3E, "F5" },
                { 0x3F, "F6" }, { 0x40, "F7" }, { 0x41, "F8" }, { 0x42, "F9" }, { 0x43, "F10" },
                { 0x44, "F11" }, { 0x45, "F12" }, { 0x46, "PrtSc" }, { 0x47, "ScrLk" },
                { 0x48, "Pause" }, { 0x49, "Ins" }, { 0x4A, "Home" }, { 0x4B, "PgUp" },
                { 0x4C, "Del" }, { 0x4D, "End" }, { 0x4E, "PgDn" }, { 0x4F, "Right" },
                { 0x50, "Left" }, { 0x51, "Down" }, { 0x52, "Up" },
                // Modifier keys
                { 0xE0, "LCtrl" }, { 0xE1, "LShift" }, { 0xE2, "LAlt" }, { 0xE3, "LWin" },
                { 0xE4, "RCtrl" }, { 0xE5, "RShift" }, { 0xE6, "RAlt" }, { 0xE7, "RWin" },
                // Media keys
                { 0x7F, "VolMute" }, { 0x80, "VolUp" }, { 0x81, "VolDown" }
            };
            return keyNames.TryGetValue(keyCode, out var name) ? name : $"0x{keyCode:X2}";
        }

        private ButtonMapping GetButtonMappingFromUI(string buttonName)
        {
            var mapping = new ButtonMapping();

            var typeCombo = FindName($"LegionButton{buttonName}TypeComboBox") as ComboBox;
            var gamepadCombo = FindName($"LegionButton{buttonName}ComboBox") as ComboBox;
            var mouseCombo = FindName($"LegionButton{buttonName}MouseComboBox") as ComboBox;

            mapping.Type = typeCombo?.SelectedIndex ?? 0;
            mapping.MouseButton = mouseCombo?.SelectedIndex ?? 0;
            // Type=3 (System): the MouseButton field carries the HotkeyAction id
            // instead of a mouse code (see SystemActionIds).
            if (mapping.Type == 3)
                mapping.MouseButton = SystemActionIdFromCombo(buttonName);
            mapping.GamepadMode = GetStoredButtonGamepadMode(buttonName);
            mapping.Turbo = GetStoredButtonTurbo(buttonName);

            int singleAction = gamepadCombo?.SelectedIndex ?? 0;
            if (mapping.Type == 0)
            {
                if (mapping.GamepadMode == 1)
                {
                    var comboActions = GetStoredGamepadComboActions(buttonName);
                    mapping.GamepadActions = comboActions;
                    mapping.GamepadAction = comboActions.Count > 0 ? comboActions[0] : singleAction;
                }
                else
                {
                    mapping.GamepadAction = singleAction;
                    mapping.GamepadActions = singleAction > 0 ? new List<int> { singleAction } : new List<int>();
                }
            }
            else
            {
                mapping.GamepadAction = singleAction;
                mapping.GamepadActions = new List<int>();
                mapping.GamepadMode = 0;
                mapping.Turbo = false;
            }

            // Get keyboard keys from the stored list (maintained separately)
            var keyList = GetStoredKeyboardKeys(buttonName);
            mapping.KeyboardKeys = keyList;

            return mapping;
        }

        private static readonly string[] LegionRemapButtonNames = new[] { "Y1", "Y2", "Y3", "M1", "M2", "M3", "Desktop", "Page" };
        private readonly Dictionary<string, List<int>> _buttonKeyboardKeys = new Dictionary<string, List<int>>();
        private readonly Dictionary<string, List<int>> _buttonGamepadComboActions = new Dictionary<string, List<int>>();
        private readonly Dictionary<string, bool> _buttonGamepadTurbo = new Dictionary<string, bool>();
        private readonly Dictionary<string, int> _buttonGamepadMode = new Dictionary<string, int>();
        private readonly Dictionary<string, StackPanel> _buttonGamepadComboRootPanels = new Dictionary<string, StackPanel>();
        private readonly Dictionary<string, ComboBox> _buttonGamepadModeCombos = new Dictionary<string, ComboBox>();
        private readonly Dictionary<string, StackPanel> _buttonGamepadComboEditorRows = new Dictionary<string, StackPanel>();
        private readonly Dictionary<string, StackPanel> _buttonGamepadComboTags = new Dictionary<string, StackPanel>();
        private readonly Dictionary<string, ComboBox> _buttonGamepadComboAddCombos = new Dictionary<string, ComboBox>();
        private readonly Dictionary<string, CheckBox> _buttonGamepadTurboChecks = new Dictionary<string, CheckBox>();
        private bool isUpdatingButtonComboUi = false;

        private List<int> GetStoredKeyboardKeys(string buttonName)
        {
            if (_buttonKeyboardKeys.TryGetValue(buttonName, out var keys))
                return new List<int>(keys);
            return new List<int>();
        }

        private void SetStoredKeyboardKeys(string buttonName, List<int> keys)
        {
            _buttonKeyboardKeys[buttonName] = new List<int>(keys ?? new List<int>());
        }

        private void ApplyControllerProfile(ControllerProfile profile)
        {
            isLoadingControllerProfile = true;

            try
            {
                // Store keyboard keys before applying UI
                SetStoredKeyboardKeys("Y1", profile.ButtonY1?.KeyboardKeys ?? new List<int>());
                SetStoredKeyboardKeys("Y2", profile.ButtonY2?.KeyboardKeys ?? new List<int>());
                SetStoredKeyboardKeys("Y3", profile.ButtonY3?.KeyboardKeys ?? new List<int>());
                SetStoredKeyboardKeys("M1", profile.ButtonM1?.KeyboardKeys ?? new List<int>());
                SetStoredKeyboardKeys("M2", profile.ButtonM2?.KeyboardKeys ?? new List<int>());
                SetStoredKeyboardKeys("M3", profile.ButtonM3?.KeyboardKeys ?? new List<int>());
                SetStoredKeyboardKeys("Desktop", profile.ButtonDesktop?.KeyboardKeys ?? new List<int>());
                SetStoredKeyboardKeys("Page", profile.ButtonPage?.KeyboardKeys ?? new List<int>());

                // Store gamepad combo/turbo metadata before applying UI
                SetStoredGamepadComboActions("Y1", profile.ButtonY1?.GamepadActions ?? new List<int>());
                SetStoredGamepadComboActions("Y2", profile.ButtonY2?.GamepadActions ?? new List<int>());
                SetStoredGamepadComboActions("Y3", profile.ButtonY3?.GamepadActions ?? new List<int>());
                SetStoredGamepadComboActions("M1", profile.ButtonM1?.GamepadActions ?? new List<int>());
                SetStoredGamepadComboActions("M2", profile.ButtonM2?.GamepadActions ?? new List<int>());
                SetStoredGamepadComboActions("M3", profile.ButtonM3?.GamepadActions ?? new List<int>());
                SetStoredGamepadComboActions("Desktop", profile.ButtonDesktop?.GamepadActions ?? new List<int>());
                SetStoredGamepadComboActions("Page", profile.ButtonPage?.GamepadActions ?? new List<int>());

                SetStoredButtonTurbo("Y1", profile.ButtonY1?.Turbo == true);
                SetStoredButtonTurbo("Y2", profile.ButtonY2?.Turbo == true);
                SetStoredButtonTurbo("Y3", profile.ButtonY3?.Turbo == true);
                SetStoredButtonTurbo("M1", profile.ButtonM1?.Turbo == true);
                SetStoredButtonTurbo("M2", profile.ButtonM2?.Turbo == true);
                SetStoredButtonTurbo("M3", profile.ButtonM3?.Turbo == true);
                SetStoredButtonTurbo("Desktop", profile.ButtonDesktop?.Turbo == true);
                SetStoredButtonTurbo("Page", profile.ButtonPage?.Turbo == true);

                SetStoredButtonGamepadMode("Y1", (profile.ButtonY1?.GamepadMode == 1 || (profile.ButtonY1?.GamepadActions?.Count ?? 0) > 1) ? 1 : 0);
                SetStoredButtonGamepadMode("Y2", (profile.ButtonY2?.GamepadMode == 1 || (profile.ButtonY2?.GamepadActions?.Count ?? 0) > 1) ? 1 : 0);
                SetStoredButtonGamepadMode("Y3", (profile.ButtonY3?.GamepadMode == 1 || (profile.ButtonY3?.GamepadActions?.Count ?? 0) > 1) ? 1 : 0);
                SetStoredButtonGamepadMode("M1", (profile.ButtonM1?.GamepadMode == 1 || (profile.ButtonM1?.GamepadActions?.Count ?? 0) > 1) ? 1 : 0);
                SetStoredButtonGamepadMode("M2", (profile.ButtonM2?.GamepadMode == 1 || (profile.ButtonM2?.GamepadActions?.Count ?? 0) > 1) ? 1 : 0);
                SetStoredButtonGamepadMode("M3", (profile.ButtonM3?.GamepadMode == 1 || (profile.ButtonM3?.GamepadActions?.Count ?? 0) > 1) ? 1 : 0);
                SetStoredButtonGamepadMode("Desktop", (profile.ButtonDesktop?.GamepadMode == 1 || (profile.ButtonDesktop?.GamepadActions?.Count ?? 0) > 1) ? 1 : 0);
                SetStoredButtonGamepadMode("Page", (profile.ButtonPage?.GamepadMode == 1 || (profile.ButtonPage?.GamepadActions?.Count ?? 0) > 1) ? 1 : 0);

                // Apply button mappings (with full type support)
                ApplyButtonMappingToUI("Y1", profile.ButtonY1);
                ApplyButtonMappingToUI("Y2", profile.ButtonY2);
                ApplyButtonMappingToUI("Y3", profile.ButtonY3);
                ApplyButtonMappingToUI("M1", profile.ButtonM1);
                ApplyButtonMappingToUI("M2", profile.ButtonM2);
                ApplyButtonMappingToUI("M3", profile.ButtonM3);
                ApplyButtonMappingToUI("Desktop", profile.ButtonDesktop);
                ApplyButtonMappingToUI("Page", profile.ButtonPage);

                // Apply Nintendo layout (with event unsubscription to prevent handler firing)
                if (LegionNintendoLayoutToggle != null)
                {
                    LegionNintendoLayoutToggle.Toggled -= LegionNintendoLayout_Toggled;
                    try
                    {
                        LegionNintendoLayoutToggle.IsOn = profile.NintendoLayout;
                        // Apply or clear Nintendo layout mappings to match toggle state
                        if (profile.NintendoLayout)
                        {
                            ApplyNintendoLayoutMappings();
                        }
                        else
                        {
                            ClearNintendoLayoutMappings();
                        }
                    }
                    finally
                    {
                        LegionNintendoLayoutToggle.Toggled += LegionNintendoLayout_Toggled;
                    }
                }

                // Apply vibration settings
                if (LegionVibrationComboBox != null)
                    LegionVibrationComboBox.SelectedIndex = profile.VibrationLevel;
                if (LegionVibrationModeComboBox != null)
                    LegionVibrationModeComboBox.SelectedIndex = profile.VibrationMode - 1; // Mode is 1-based, index is 0-based

                // Apply gyro settings
                if (LegionGyroTargetComboBox != null)
                    LegionGyroTargetComboBox.SelectedIndex = profile.GyroTarget;
                if (LegionGyroSensitivityXSlider != null)
                {
                    LegionGyroSensitivityXSlider.Value = profile.GyroSensitivityX;
                    if (LegionGyroSensitivityXValue != null)
                        LegionGyroSensitivityXValue.Text = profile.GyroSensitivityX.ToString();
                }
                if (LegionGyroSensitivityYSlider != null)
                {
                    LegionGyroSensitivityYSlider.Value = profile.GyroSensitivityY;
                    if (LegionGyroSensitivityYValue != null)
                        LegionGyroSensitivityYValue.Text = profile.GyroSensitivityY.ToString();
                }
                if (LegionGyroInvertXToggle != null)
                    LegionGyroInvertXToggle.IsOn = profile.GyroInvertX;
                if (LegionGyroInvertYToggle != null)
                    LegionGyroInvertYToggle.IsOn = profile.GyroInvertY;
                if (LegionGyroMappingTypeComboBox != null)
                    LegionGyroMappingTypeComboBox.SelectedIndex = profile.GyroMappingType;
                if (LegionGyroActivationModeComboBox != null)
                    LegionGyroActivationModeComboBox.SelectedIndex = profile.GyroActivationMode;
                if (LegionGyroActivationButtonComboBox != null)
                    LegionGyroActivationButtonComboBox.SelectedIndex = profile.GyroActivationButton;

                // Apply advanced gyro settings
                if (LegionGyroDeadzoneSlider != null)
                {
                    LegionGyroDeadzoneSlider.Value = profile.GyroDeadzone;
                    if (LegionGyroDeadzoneValue != null)
                        LegionGyroDeadzoneValue.Text = profile.GyroDeadzone.ToString();
                }

                // Apply stick deadzones
                if (LegionLeftStickDeadzoneSlider != null)
                {
                    LegionLeftStickDeadzoneSlider.Value = profile.LeftStickDeadzone;
                    if (LegionLeftStickDeadzoneValue != null)
                        LegionLeftStickDeadzoneValue.Text = $"{profile.LeftStickDeadzone}%";
                }
                if (LegionRightStickDeadzoneSlider != null)
                {
                    LegionRightStickDeadzoneSlider.Value = profile.RightStickDeadzone;
                    if (LegionRightStickDeadzoneValue != null)
                        LegionRightStickDeadzoneValue.Text = $"{profile.RightStickDeadzone}%";
                }

                // Apply trigger travel settings
                if (LegionHairTriggersToggle != null)
                {
                    LegionHairTriggersToggle.Toggled -= LegionHairTriggers_Toggled;
                    try
                    {
                        LegionHairTriggersToggle.IsOn = profile.HairTriggers;
                        UpdateTriggerSlidersEnabled(!profile.HairTriggers);
                    }
                    finally
                    {
                        LegionHairTriggersToggle.Toggled += LegionHairTriggers_Toggled;
                    }
                }
                if (LegionLeftTriggerStartSlider != null)
                {
                    LegionLeftTriggerStartSlider.Value = profile.LeftTriggerStart;
                    if (LegionLeftTriggerStartValue != null)
                        LegionLeftTriggerStartValue.Text = $"{profile.LeftTriggerStart}%";
                }
                if (LegionLeftTriggerEndSlider != null)
                {
                    LegionLeftTriggerEndSlider.Value = profile.LeftTriggerEnd;
                    if (LegionLeftTriggerEndValue != null)
                        LegionLeftTriggerEndValue.Text = $"{profile.LeftTriggerEnd}%";
                }
                if (LegionRightTriggerStartSlider != null)
                {
                    LegionRightTriggerStartSlider.Value = profile.RightTriggerStart;
                    if (LegionRightTriggerStartValue != null)
                        LegionRightTriggerStartValue.Text = $"{profile.RightTriggerStart}%";
                }
                if (LegionRightTriggerEndSlider != null)
                {
                    LegionRightTriggerEndSlider.Value = profile.RightTriggerEnd;
                    if (LegionRightTriggerEndValue != null)
                        LegionRightTriggerEndValue.Text = $"{profile.RightTriggerEnd}%";
                }

                // Apply joystick as mouse settings.
                // Joystick-as-mouse (right stick -> mouse) is a DESKTOP-CONTROLS-only feature
                // and is a LATCHED firmware mode (persists across reboot until explicitly
                // disabled). It must never come from a non-Desktop profile: a leaked value in
                // the Global/per-game profile re-enabled it on every apply/startup with no way
                // to turn it off ("joy-to-mouse on permanently" - field report 0.3.2730). Force
                // 0 for any non-Desktop profile so a stale/leaked value is actively cleared.
                int effectiveJoyMouse = profile.DesktopControlsEnabled ? profile.JoystickAsMouseMode : 0;
                if (LegionJoystickAsMouseComboBox != null)
                {
                    // Set UI first
                    if (LegionJoystickAsMouseComboBox.Items.Count > effectiveJoyMouse)
                    {
                        LegionJoystickAsMouseComboBox.SelectedIndex = effectiveJoyMouse;
                    }
                    // Show/hide sensitivity grid based on mode
                    if (LegionJoystickMouseSensGrid != null)
                        LegionJoystickMouseSensGrid.Visibility = effectiveJoyMouse > 0
                            ? Windows.UI.Xaml.Visibility.Visible
                            : Windows.UI.Xaml.Visibility.Collapsed;
                    // Send value to helper (SetValue instead of SetValueSilent)
                    legionJoystickAsMouseMode?.SetValue(effectiveJoyMouse);
                }
                if (LegionJoystickMouseSensSlider != null)
                {
                    LegionJoystickMouseSensSlider.Value = profile.JoystickMouseSens;
                    if (LegionJoystickMouseSensValue != null)
                        LegionJoystickMouseSensValue.Text = profile.JoystickMouseSens.ToString();
                }
                // Send the sensitivity explicitly, like the mode above. Nothing else sends it
                // on a profile apply — the only sender used to be the bound slider property's
                // debounced echo of the programmatic assignment, which could latch a stale
                // intermediate value across rapid Desktop Mode toggles and stomp the profile
                // with it (helper at 50 while the slider showed the user's value).
                legionJoystickMouseSens?.SetValue(profile.JoystickMouseSens);

                // Apply gamepad button mappings
                gamepadButtonMappings = profile.GamepadButtonMappings?.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value.Clone()) ?? new Dictionary<string, ButtonMapping>();

                // Update UI to show current selected button's mapping
                if (LegionGamepadButtonSelectorComboBox != null && LegionGamepadButtonSelectorComboBox.SelectedIndex >= 0)
                {
                    LoadGamepadMappingToUI(GetGamepadButtonNameFromIndex(LegionGamepadButtonSelectorComboBox.SelectedIndex));
                }

                // Update the remapped buttons summary display
                UpdateGamepadMappingSummary();

                // Desktop Mode is now a global overlay (see DesktopControlsPreset.cs), not a
                // per-profile preset merge. Just reflect its live state on the toggle here; if
                // active, the overlay is re-applied on top of this profile at the end of the
                // method via ReapplyDesktopOverlayAfterProfileLoad().
                if (LegionDesktopControlsToggle != null)
                {
                    LegionDesktopControlsToggle.Toggled -= LegionDesktopControls_Toggled;
                    try { LegionDesktopControlsToggle.IsOn = isDesktopModeActive; }
                    finally { LegionDesktopControlsToggle.Toggled += LegionDesktopControls_Toggled; }
                }

                // Apply lighting settings
                if (LegionLightModeComboBox != null)
                {
                    LegionLightModeComboBox.SelectionChanged -= LegionLightModeComboBox_SelectionChanged;
                    try
                    {
                        LegionLightModeComboBox.SelectedIndex = profile.LightMode;
                    }
                    finally
                    {
                        LegionLightModeComboBox.SelectionChanged += LegionLightModeComboBox_SelectionChanged;
                    }
                }
                if (LegionColorPicker != null)
                {
                    LegionColorPicker.ColorChanged -= LegionColorPicker_ColorChanged;
                    try
                    {
                        LegionColorPicker.Color = Windows.UI.Color.FromArgb(255, profile.LightColorR, profile.LightColorG, profile.LightColorB);
                        if (LegionColorPreview != null)
                        {
                            LegionColorPreview.Background = new SolidColorBrush(LegionColorPicker.Color);
                        }
                    }
                    finally
                    {
                        LegionColorPicker.ColorChanged += LegionColorPicker_ColorChanged;
                    }
                }
                if (LegionSpeedSlider != null)
                {
                    LegionSpeedSlider.Value = profile.LightSpeed;
                }
                if (LegionBrightnessSlider != null)
                {
                    LegionBrightnessSlider.Value = profile.LightBrightness;
                    if (LegionBrightnessValue != null)
                        LegionBrightnessValue.Text = $"{profile.LightBrightness}%";
                }
                if (LegionPowerLightToggle != null)
                {
                    LegionPowerLightToggle.IsOn = profile.PowerLight;
                }

                Logger.Info($"Applied controller profile: Y1={FormatButtonMapping(profile.ButtonY1)}, Y2={FormatButtonMapping(profile.ButtonY2)}, Y3={FormatButtonMapping(profile.ButtonY3)}, M1={FormatButtonMapping(profile.ButtonM1)}, M2={FormatButtonMapping(profile.ButtonM2)}, M3={FormatButtonMapping(profile.ButtonM3)}, Nintendo={profile.NintendoLayout}, Vib={profile.VibrationLevel}, VibMode={profile.VibrationMode}, GyroTarget={profile.GyroTarget}, LDZ={profile.LeftStickDeadzone}, RDZ={profile.RightStickDeadzone}, GamepadMappings={profile.GamepadButtonMappings?.Count ?? 0}, DesktopControls={profile.DesktopControlsEnabled}, LightMode={profile.LightMode}");

                // Set timestamp BEFORE sending to prevent any queued events from causing duplicate sends
                // Use 2 second window since HID commands take ~1.5s to complete (50ms per button × ~30 buttons)
                lastProfileApplyTime = DateTime.Now;

                // Send button mappings to helper
                SendButtonMappingsToHelper(profile);

                // Send controller settings to helper (gyro, deadzone, vibration, triggers)
                SendControllerSettingsToHelper(profile);

                // Send lighting settings to helper
                SendLightingToHelper(profile);

                // Re-evaluate enhanced remap UI after profile/UI values settle.
                // This avoids startup ordering issues where improved input state arrives
                // after initial profile controls are populated.
                RefreshLegionEnhancedRemapUi();

                // Refresh the "Editing: Global/<Game>/Desktop Controls" header hint.
                UpdateButtonRemappingOverlayHint();
            }
            finally
            {
                isLoadingControllerProfile = false;
            }
        }

        private ControllerProfile GetCurrentControllerProfileFromUI()
        {
            // Get the current profile to preserve lighting if color picker isn't available
            // This prevents the color from resetting to white when saving from non-Legion tabs
            var currentProfile = (LegionControllerProfileToggle?.IsOn == true && HasValidGame(currentGameName))
                ? gameControllerProfile
                : globalControllerProfile;

            // Use color picker value if available, otherwise preserve existing color
            byte colorR = LegionColorPicker != null ? LegionColorPicker.Color.R : currentProfile.LightColorR;
            byte colorG = LegionColorPicker != null ? LegionColorPicker.Color.G : currentProfile.LightColorG;
            byte colorB = LegionColorPicker != null ? LegionColorPicker.Color.B : currentProfile.LightColorB;

            var builtProfile = new ControllerProfile
            {
                ButtonY1 = GetButtonMappingFromUI("Y1"),
                ButtonY2 = GetButtonMappingFromUI("Y2"),
                ButtonY3 = GetButtonMappingFromUI("Y3"),
                ButtonM1 = GetButtonMappingFromUI("M1"),
                ButtonM2 = GetButtonMappingFromUI("M2"),
                ButtonM3 = GetButtonMappingFromUI("M3"),
                ButtonDesktop = GetButtonMappingFromUI("Desktop"),
                ButtonPage = GetButtonMappingFromUI("Page"),
                NintendoLayout = LegionNintendoLayoutToggle?.IsOn ?? false,
                VibrationLevel = LegionVibrationComboBox?.SelectedIndex ?? 2,
                VibrationMode = (LegionVibrationModeComboBox?.SelectedIndex ?? 0) + 1, // Index is 0-based, mode is 1-based
                // Gyro settings
                GyroTarget = LegionGyroTargetComboBox?.SelectedIndex ?? 0,
                GyroSensitivityX = (int)(LegionGyroSensitivityXSlider?.Value ?? 50),
                GyroSensitivityY = (int)(LegionGyroSensitivityYSlider?.Value ?? 50),
                GyroInvertX = LegionGyroInvertXToggle?.IsOn ?? false,
                GyroInvertY = LegionGyroInvertYToggle?.IsOn ?? false,
                GyroMappingType = LegionGyroMappingTypeComboBox?.SelectedIndex ?? 0,
                GyroActivationMode = LegionGyroActivationModeComboBox?.SelectedIndex ?? 0,
                GyroActivationButton = LegionGyroActivationButtonComboBox?.SelectedIndex ?? 0,
                // Advanced gyro settings
                GyroDeadzone = (int)(LegionGyroDeadzoneSlider?.Value ?? 10),
                // Stick deadzones
                LeftStickDeadzone = (int)(LegionLeftStickDeadzoneSlider?.Value ?? 4),
                RightStickDeadzone = (int)(LegionRightStickDeadzoneSlider?.Value ?? 4),
                // Trigger travel
                LeftTriggerStart = (int)(LegionLeftTriggerStartSlider?.Value ?? 0),
                LeftTriggerEnd = (int)(LegionLeftTriggerEndSlider?.Value ?? 0),
                RightTriggerStart = (int)(LegionRightTriggerStartSlider?.Value ?? 0),
                RightTriggerEnd = (int)(LegionRightTriggerEndSlider?.Value ?? 0),
                HairTriggers = LegionHairTriggersToggle?.IsOn ?? false,
                // Joystick as mouse
                // Only capture joy-mouse into the profile while Desktop Mode is active; it is a
                // Desktop-Controls-only latched feature and must not leak into Global/per-game.
                JoystickAsMouseMode = isDesktopModeActive ? (LegionJoystickAsMouseComboBox?.SelectedIndex ?? 0) : 0,
                JoystickMouseSens = (int)(LegionJoystickMouseSensSlider?.Value ?? 50),
                // Gamepad button mappings
                GamepadButtonMappings = gamepadButtonMappings.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value.Clone()),
                // Desktop Controls preset
                DesktopControlsEnabled = LegionDesktopControlsToggle?.IsOn ?? false,
                // Lighting - preserve existing color if color picker not available
                LightMode = LegionLightModeComboBox?.SelectedIndex ?? currentProfile.LightMode,
                LightColorR = colorR,
                LightColorG = colorG,
                LightColorB = colorB,
                LightSpeed = (int)(LegionSpeedSlider?.Value ?? currentProfile.LightSpeed),
                LightBrightness = (int)(LegionBrightnessSlider?.Value ?? currentProfile.LightBrightness),
                PowerLight = LegionPowerLightToggle?.IsOn ?? currentProfile.PowerLight,
                HasExplicitLighting = true  // Mark as having explicit lighting since we're capturing from UI
            };

            return builtProfile;
        }

        private void LegionControllerProfileToggle_Toggled(object sender, RoutedEventArgs e)
        {
            // Protect entire toggle change sequence
            isSwitchingControllerProfile = true;

            try
            {
                var settings = ApplicationData.Current.LocalSettings;

                if (LegionControllerProfileToggle.IsOn)
                {
                    // Per-game controller profiles enabled - only proceed if we have a valid game
                    if (HasValidGame(currentGameName))
                    {
                        // Clear the disabled preference since user is enabling it
                        string disabledKey = $"ControllerProfileDisabled_{currentGameName}";
                        if (settings.Values.ContainsKey(disabledKey))
                        {
                            settings.Values.Remove(disabledKey);
                            Logger.Info($"Cleared controller profile disabled preference for {currentGameName}");
                        }

                        // Load or create game controller profile
                        string profileKey = $"ControllerProfile_Game_{currentGameName}";
                        if (!settings.Containers.ContainsKey(profileKey))
                        {
                            // Initialize new game controller profile from current UI state (global)
                            gameControllerProfile = GetCurrentControllerProfileFromUI();
                            SaveControllerProfileToStorage($"Game_{currentGameName}", gameControllerProfile);
                            Logger.Info($"Initialized game controller profile for {currentGameName} from current settings");

                            // Refresh saved profiles list if expanded
                            if (isSavedProfilesExpanded)
                            {
                                RefreshSavedProfilesList();
                            }
                        }
                        else
                        {
                            // Load existing game controller profile
                            LoadControllerProfileFromStorage($"Game_{currentGameName}", gameControllerProfile);
                            ApplyControllerProfile(gameControllerProfile);
                            Logger.Info($"Loaded existing controller profile for {currentGameName}");
                        }
                    }
                    else
                    {
                        // No valid game, turn toggle back off
                        Logger.Warn($"Cannot enable per-game controller profile without a valid game, forcing toggle OFF");
                        LegionControllerProfileToggle.IsOn = false;
                        return;
                    }
                }
                else
                {
                    // Toggle is being turned OFF
                    if (HasValidGame(currentGameName))
                    {
                        // Save user's preference to disable per-game controller profile for this game
                        string disabledKey = $"ControllerProfileDisabled_{currentGameName}";
                        settings.Values[disabledKey] = true;
                        Logger.Info($"Saved controller profile disabled preference for {currentGameName}");
                    }

                    // Switch back to global controller profile
                    LoadControllerProfileFromStorage("Global", globalControllerProfile);
                    ApplyControllerProfile(globalControllerProfile);
                    Logger.Info("Switched to global controller profile");
                }
            }
            finally
            {
                isSwitchingControllerProfile = false;
            }
        }

        private void ControllerSettingChanged(object sender, object e)
        {
            // Update slider value displays
            UpdateControllerSliderDisplays(sender);

            // Don't save during profile loading, switching, widget unloading, or helper sync
            if (isLoadingControllerProfile || isSwitchingControllerProfile || isUnloading || isApplyingHelperUpdate)
                return;

            // Skip if a profile was just applied (prevents duplicate sends from queued UI events)
            if ((DateTime.Now - lastProfileApplyTime).TotalMilliseconds < 2000)
                return;

            PerformControllerSettingSave(sender);
        }

        // Debounced variant for continuous-drag controls (gyro/deadzone/trigger/mouse-sens/
        // brightness/speed sliders + color picker). Updates the display immediately, then
        // coalesces the heavy save + IPC (storage write, SendButtonMappingsToHelper,
        // SendLightingToHelper) into a single call ~300ms after the drag settles. Dragging
        // one slider used to fire all of that dozens of times per second.
        private DispatcherTimer controllerSaveDebounceTimer;
        private object controllerSaveDebounceSender;

        private void ControllerSliderSettingChanged(object sender, object e)
        {
            // Update slider value displays immediately (keeps the UI responsive during drag)
            UpdateControllerSliderDisplays(sender);

            if (isLoadingControllerProfile || isSwitchingControllerProfile || isUnloading || isApplyingHelperUpdate)
                return;
            if ((DateTime.Now - lastProfileApplyTime).TotalMilliseconds < 2000)
                return;

            controllerSaveDebounceSender = sender;
            if (controllerSaveDebounceTimer == null)
            {
                controllerSaveDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                controllerSaveDebounceTimer.Tick += (s, ev) =>
                {
                    controllerSaveDebounceTimer.Stop();
                    // Re-check guards at fire time (state may have changed during the debounce window).
                    if (isLoadingControllerProfile || isSwitchingControllerProfile || isUnloading || isApplyingHelperUpdate)
                        return;
                    PerformControllerSettingSave(controllerSaveDebounceSender);
                };
            }
            controllerSaveDebounceTimer.Stop();
            controllerSaveDebounceTimer.Start();
        }

        // Shared save body: builds the current profile from the UI, persists it, and pushes
        // mappings/lighting to the helper. Called immediately for discrete controls and
        // debounced for continuous-drag controls.
        private void PerformControllerSettingSave(object sender)
        {
            // While Desktop Mode is active, ALL controller-setting saves route to the Desktop
            // profile and the underlying Global/Per-Game profile is left untouched. This is what
            // keeps paddle (Y/M/Desktop/Page) and other remaps from leaking into the profile
            // underneath while the user edits their Desktop layout.
            if (isDesktopModeActive)
            {
                SaveCurrentToDesktopProfile();
                SendButtonMappingsToHelper(desktopControllerProfile);
                SendControllerSettingsToHelper(desktopControllerProfile);
                bool powerLightOnly = sender == LegionPowerLightToggle;
                if (!powerLightOnly)
                    SendLightingToHelper(desktopControllerProfile);
                return;
            }

            // Get current profile from UI
            ControllerProfile profile;
            if (LegionControllerProfileToggle?.IsOn == true && HasValidGame(currentGameName))
            {
                // Save to game controller profile
                gameControllerProfile = GetCurrentControllerProfileFromUI();
                SaveControllerProfileToStorage($"Game_{currentGameName}", gameControllerProfile);
                profile = gameControllerProfile;
            }
            else
            {
                // Save to global controller profile
                globalControllerProfile = GetCurrentControllerProfileFromUI();
                SaveControllerProfileToStorage("Global", globalControllerProfile);
                profile = globalControllerProfile;
            }

            // Send button mappings to helper
            SendButtonMappingsToHelper(profile);

            // Send lighting settings to helper (so they get saved to helper's profile XML)
            // Skip if only the power light changed - it's already sent directly by the property
            // and resending all lighting would overwrite stick colors with UI color picker value
            bool isPowerLightToggleOnly = sender == LegionPowerLightToggle;
            if (!isPowerLightToggleOnly)
            {
                SendLightingToHelper(profile);
            }
        }

        /// <summary>
        /// Sends all button mappings to the helper via IPC.
        /// Always sends mappings, including "Disabled" (default) state to clear buttons.
        /// This method is only called from user-initiated changes (ControllerSettingChanged
        /// already has guards for profile loading), so we always want to send.
        /// </summary>
        private void SendButtonMappingsToHelper(ControllerProfile profile, bool force = false)
        {
            try
            {
                // Always send button mappings, including "Disabled" (Type=0, GamepadAction=0)
                // When user explicitly sets a button to Disabled, we need to send that to
                // the helper so it clears the button mapping on the controller.
                // force=true bypasses the per-property json==Value dedup so cold-start recovery
                // actually re-pushes (the early sends latched Value while disconnected).
                if (profile.ButtonY1 != null)
                    legionButtonY1?.SendMapping(profile.ButtonY1.ToJson(), force);
                if (profile.ButtonY2 != null)
                    legionButtonY2?.SendMapping(profile.ButtonY2.ToJson(), force);
                if (profile.ButtonY3 != null)
                    legionButtonY3?.SendMapping(profile.ButtonY3.ToJson(), force);
                if (profile.ButtonM1 != null)
                    legionButtonM1?.SendMapping(profile.ButtonM1.ToJson(), force);
                if (profile.ButtonM2 != null)
                    legionButtonM2?.SendMapping(profile.ButtonM2.ToJson(), force);
                if (profile.ButtonM3 != null)
                    legionButtonM3?.SendMapping(profile.ButtonM3.ToJson(), force);
                if (profile.ButtonDesktop != null)
                    legionButtonDesktop?.SendMapping(profile.ButtonDesktop.ToJson(), force);
                if (profile.ButtonPage != null)
                    legionButtonPage?.SendMapping(profile.ButtonPage.ToJson(), force);

                // Send gamepad button mappings as JSON dictionary
                // During profile loading, use gamepadButtonMappings (includes desktop control changes)
                // Otherwise use profile.GamepadButtonMappings
                // Always send the LIVE gamepad mappings, not the profile's. Normally they match
                // (GetCurrentControllerProfileFromUI copies live), but while Desktop Mode is active
                // the profile carries the substituted UNDERLYING mappings for storage while the
                // controller must reflect the live Desktop overlay.
                var mappingsToSend = gamepadButtonMappings;
                if (mappingsToSend != null && mappingsToSend.Count > 0)
                {
                    var gamepadMappingsJson = SerializeGamepadButtonMappings(mappingsToSend);
                    if (force) legionGamepadMapping?.ForceSetValue(gamepadMappingsJson);
                    else legionGamepadMapping?.SetValue(gamepadMappingsJson);
                }
                else
                {
                    if (force) legionGamepadMapping?.ForceSetValue("");
                    else legionGamepadMapping?.SetValue("");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error sending button mappings: {ex.Message}");
            }
        }

        /// <summary>
        /// Re-pushes the currently active controller profile (button mappings, controller
        /// settings, lighting) to the helper. Called from OnPipeConnectedAsync to recover
        /// from the cold-start race where ApplyControllerProfile fires during widget
        /// constructor before App.IsConnected becomes true — those early sends silently
        /// drop into a not-yet-connected pipe and the helper never learns the user's
        /// saved button remaps / gyro / vibration / triggers settings until the next
        /// manual interaction.
        /// </summary>
        internal void ResendActiveControllerProfileToHelper()
        {
            try
            {
                var profile = (LegionControllerProfileToggle?.IsOn == true && HasValidGame(currentGameName))
                    ? gameControllerProfile
                    : globalControllerProfile;
                if (profile == null) return;

                Logger.Info("Re-pushing active controller profile to helper after pipe connect (recovery from cold-start race)");
                SendButtonMappingsToHelper(profile, force: true);
                // force: the widget's cached values equal the profile's, so plain
                // SetValue would equality-skip and nothing would reach a restarted
                // helper (verified 2026-07-30 — recovery previously depended solely
                // on the helper's own global.xml restore).
                SendControllerSettingsToHelper(profile, force: true);
                SendLightingToHelper(profile, force: true);
            }
            catch (Exception ex)
            {
                Logger.Warn($"ResendActiveControllerProfileToHelper failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Sends lighting settings to the helper via IPC
        /// </summary>
        private void SendLightingToHelper(ControllerProfile profile, bool force = false)
        {
            try
            {
                // Only send lighting if the profile has explicit lighting settings saved
                // This prevents old profiles (created before per-game lighting) from resetting to white
                if (!profile.HasExplicitLighting)
                {
                    Logger.Info($"Skipping lighting update - profile has no explicit lighting settings");
                    return;
                }

                // Defensive: if the saved color is pure white with default mode/brightness/speed,
                // treat the profile as accidentally-flagged-explicit (we've seen the pre-fix race
                // poison Global with #FFFFFF + HasExplicitLighting=true) and skip the push.
                // A user genuinely picking white will have changed at least one other lighting
                // field too, so this only filters the corruption pattern, not real choices.
                bool isDefaultWhite = profile.LightColorR == 0xFF
                                   && profile.LightColorG == 0xFF
                                   && profile.LightColorB == 0xFF
                                   && profile.LightMode == 1
                                   && profile.LightSpeed == 50
                                   && profile.LightBrightness >= 9; // default brightness range
                if (isDefaultWhite)
                {
                    Logger.Warn($"Skipping lighting push - profile color is default white with default mode/speed (likely from a poisoned save). Adjust any lighting setting to re-enable.");
                    return;
                }

                // Send light mode (force bypasses the equality skip on reconnect
                // resends — see SendControllerSettingsToHelper)
                if (force) legionLightMode?.ForceSetValue(profile.LightMode);
                else legionLightMode?.SetValue(profile.LightMode);

                // Send light color as hex string (RRGGBB format)
                // Use SetFromProfile to mark as user-saved, preventing sync from overwriting with helper's default
                string colorHex = $"{profile.LightColorR:X2}{profile.LightColorG:X2}{profile.LightColorB:X2}";
                legionLightColor?.SetFromProfile(colorHex);

                // Send light speed
                if (force) legionLightSpeed?.ForceSetValue(profile.LightSpeed);
                else legionLightSpeed?.SetValue(profile.LightSpeed);

                // Send brightness
                if (force) legionLightBrightness?.ForceSetValue(profile.LightBrightness);
                else legionLightBrightness?.SetValue(profile.LightBrightness);

                // Send power light
                if (force) legionPowerLight?.ForceSetValue(profile.PowerLight);
                else legionPowerLight?.SetValue(profile.PowerLight);

                Logger.Info($"Sent lighting to helper: Mode={profile.LightMode}, Color=#{colorHex}, Speed={profile.LightSpeed}, Brightness={profile.LightBrightness}, PowerLight={profile.PowerLight}");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error sending lighting settings: {ex.Message}");
            }
        }

        /// <summary>
        /// Sends all controller settings (gyro, deadzone, vibration, triggers) to the helper via IPC.
        /// This ensures the helper has the full profile even when the widget is closed.
        /// </summary>
        private void SendControllerSettingsToHelper(ControllerProfile profile, bool force = false)
        {
            try
            {
                // force: on a RECONNECT resend the widget's cached values equal the
                // profile values, so plain SetValue hits GenericProperty's equality
                // skip and nothing goes over the wire — a restarted helper would only
                // have whatever its own persistence restored. ForceSetValue bypasses
                // the skip (same pattern as the button-mapping resend).
                void Send<T>(Shared.Data.GenericProperty<T> prop, T value)
                {
                    if (prop == null) return;
                    if (force) prop.ForceSetValue(value);
                    else prop.SetValue(value);
                }

                // Vibration settings
                Send(legionVibration, profile.VibrationLevel);
                Send(legionVibrationMode, profile.VibrationMode);

                // Gyro settings
                Send(legionGyroTarget, profile.GyroTarget);
                Send(legionGyroSensitivityX, profile.GyroSensitivityX);
                Send(legionGyroSensitivityY, profile.GyroSensitivityY);
                Send(legionGyroInvertX, profile.GyroInvertX);
                Send(legionGyroInvertY, profile.GyroInvertY);
                Send(legionGyroMappingType, profile.GyroMappingType);
                Send(legionGyroActivationMode, profile.GyroActivationMode);
                Send(legionGyroActivationButton, profile.GyroActivationButton);
                Send(legionGyroDeadzone, profile.GyroDeadzone);

                // Stick deadzone settings
                Send(legionLeftStickDeadzone, profile.LeftStickDeadzone);
                Send(legionRightStickDeadzone, profile.RightStickDeadzone);

                // Trigger travel settings
                Send(legionLeftTriggerStart, profile.LeftTriggerStart);
                Send(legionLeftTriggerEnd, profile.LeftTriggerEnd);
                Send(legionRightTriggerStart, profile.RightTriggerStart);
                Send(legionRightTriggerEnd, profile.RightTriggerEnd);
                Send(legionHairTriggers, profile.HairTriggers);

                Logger.Info($"Sent controller settings to helper (force={force}): Vib={profile.VibrationLevel}, VibMode={profile.VibrationMode}, GyroTarget={profile.GyroTarget}, LDZ={profile.LeftStickDeadzone}, RDZ={profile.RightStickDeadzone}");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error sending controller settings: {ex.Message}");
            }
        }

        /// <summary>
        /// Updates the display text for controller setting sliders
        /// </summary>
        private void UpdateControllerSliderDisplays(object sender)
        {
            try
            {
                // Gyro sensitivity sliders
                if (sender == LegionGyroSensitivityXSlider && LegionGyroSensitivityXValue != null)
                    LegionGyroSensitivityXValue.Text = ((int)LegionGyroSensitivityXSlider.Value).ToString();
                else if (sender == LegionGyroSensitivityYSlider && LegionGyroSensitivityYValue != null)
                    LegionGyroSensitivityYValue.Text = ((int)LegionGyroSensitivityYSlider.Value).ToString();
                // Advanced gyro sliders
                else if (sender == LegionGyroDeadzoneSlider && LegionGyroDeadzoneValue != null)
                    LegionGyroDeadzoneValue.Text = ((int)LegionGyroDeadzoneSlider.Value).ToString();
                // Stick deadzone sliders
                else if (sender == LegionLeftStickDeadzoneSlider && LegionLeftStickDeadzoneValue != null)
                    LegionLeftStickDeadzoneValue.Text = $"{(int)LegionLeftStickDeadzoneSlider.Value}%";
                else if (sender == LegionRightStickDeadzoneSlider && LegionRightStickDeadzoneValue != null)
                    LegionRightStickDeadzoneValue.Text = $"{(int)LegionRightStickDeadzoneSlider.Value}%";
                // Trigger travel sliders
                else if (sender == LegionLeftTriggerStartSlider && LegionLeftTriggerStartValue != null)
                    LegionLeftTriggerStartValue.Text = $"{(int)LegionLeftTriggerStartSlider.Value}%";
                else if (sender == LegionLeftTriggerEndSlider && LegionLeftTriggerEndValue != null)
                    LegionLeftTriggerEndValue.Text = $"{(int)LegionLeftTriggerEndSlider.Value}%";
                else if (sender == LegionRightTriggerStartSlider && LegionRightTriggerStartValue != null)
                    LegionRightTriggerStartValue.Text = $"{(int)LegionRightTriggerStartSlider.Value}%";
                else if (sender == LegionRightTriggerEndSlider && LegionRightTriggerEndValue != null)
                    LegionRightTriggerEndValue.Text = $"{(int)LegionRightTriggerEndSlider.Value}%";
                // Joystick as mouse sensitivity slider
                else if (sender == LegionJoystickMouseSensSlider && LegionJoystickMouseSensValue != null)
                    LegionJoystickMouseSensValue.Text = ((int)LegionJoystickMouseSensSlider.Value).ToString();
            }
            catch (Exception ex)
            {
                Logger.Debug($"Error updating controller slider display: {ex.Message}");
            }
        }
    }
}
