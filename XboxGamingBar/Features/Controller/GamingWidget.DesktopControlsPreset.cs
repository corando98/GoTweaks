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
        // ---- Desktop Mode (controller overlay) ----
        // Desktop Mode is a separate, user-editable controller REMAP profile stored under the
        // name "Desktop". While it's on, EVERY button-remap edit (gamepad face/dpad/stick AND the
        // Y/M/Desktop/Page paddles) plus joystick-as-mouse routes to the Desktop profile, not the
        // underlying Global/Per-Game profile. Toggling off re-applies the underlying profile, so
        // the underlying bindings are never touched. State is a global flag, remembered across
        // profile switches and app restarts.

        private const string DesktopProfileName = "Desktop";
        private bool isDesktopModeActive;
        private string _desktopUnderlyingProfileName = "Global";
        private ControllerProfile desktopControllerProfile = new ControllerProfile();

        private const string DesktopModeActiveKey = "DesktopMode_Active";
        private const string DesktopModeSeededKey = "DesktopMode_Seeded";

        private void LegionDesktopControls_Toggled(object sender, RoutedEventArgs e)
        {
            if (isLoadingControllerProfile || isSwitchingControllerProfile)
                return;

            // No time-based guard here. The old "skip if a profile was just applied"
            // 2-second window silently dropped REAL toggles while leaving the ToggleSwitch
            // visually flipped, desyncing isDesktopModeActive from the UI — after which
            // every toggle applied the OPPOSITE state (Desktop Mode / Joystick-as-Mouse
            // "synced but alternating", field report on 0.3.2637). Duplicate queued events
            // are already harmless: ApplyDesktopModeState no-ops when the requested state
            // matches isDesktopModeActive.
            bool enabled = LegionDesktopControlsToggle?.IsOn ?? false;
            ApplyDesktopModeState(enabled, persistActive: true);
        }

        /// <summary>
        /// Enable or disable the Desktop Mode overlay. Enable loads (or seeds) the Desktop profile
        /// and applies it; disable saves the current edits to Desktop and re-applies the underlying
        /// profile so all its bindings are restored.
        /// </summary>
        private void ApplyDesktopModeState(bool enabled, bool persistActive)
        {
            try
            {
                var settings = Windows.Storage.ApplicationData.Current.LocalSettings;

                // Heal the legacy-storage state before deciding whether to seed.
                MigrateDesktopModeStorageIfNeeded();

                if (enabled && !isDesktopModeActive)
                {
                    // Remember which profile is underneath so we can restore it on disable.
                    _desktopUnderlyingProfileName =
                        (LegionControllerProfileToggle?.IsOn == true && HasValidGame(currentGameName))
                            ? $"Game_{currentGameName}" : "Global";

                    // Load the user's Desktop profile, or seed it (current remaps + desktop preset).
                    if (DesktopModeSeeded())
                    {
                        desktopControllerProfile = new ControllerProfile();
                        LoadControllerProfileFromStorage(DesktopProfileName, desktopControllerProfile);
                    }
                    else
                    {
                        desktopControllerProfile = GetCurrentControllerProfileFromUI(); // underlying snapshot
                        desktopControllerProfile.GamepadButtonMappings = BuildDesktopPresetMappings();
                        desktopControllerProfile.JoystickAsMouseMode = 2; // Right Stick as mouse
                        SaveControllerProfileToStorage(DesktopProfileName, desktopControllerProfile);
                        settings.Values[DesktopModeSeededKey] = true;
                    }
                    desktopControllerProfile.DesktopControlsEnabled = true;

                    isDesktopModeActive = true;
                    InjectGamepadResetsForRemovedButtons(desktopControllerProfile);
                    ApplyControllerProfile(desktopControllerProfile);

                    if (persistActive) settings.Values[DesktopModeActiveKey] = true;
                    UpdateButtonRemappingOverlayHint();
                    Logger.Info($"Desktop Mode enabled (overlay applied, underlying={_desktopUnderlyingProfileName})");
                }
                else if (!enabled && isDesktopModeActive)
                {
                    // Capture the final Desktop edits, then re-apply the underlying profile.
                    SaveCurrentToDesktopProfile();
                    isDesktopModeActive = false;

                    var underlying = new ControllerProfile();
                    LoadControllerProfileFromStorage(_desktopUnderlyingProfileName, underlying);
                    if (_desktopUnderlyingProfileName == "Global") globalControllerProfile = underlying;
                    else gameControllerProfile = underlying;
                    InjectGamepadResetsForRemovedButtons(underlying);
                    ApplyControllerProfile(underlying);

                    if (persistActive) settings.Values[DesktopModeActiveKey] = false;
                    UpdateButtonRemappingOverlayHint();
                    Logger.Info($"Desktop Mode disabled (restored {_desktopUnderlyingProfileName})");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"ApplyDesktopModeState error: {ex.Message}");
            }
        }

        /// <summary>
        /// Persist the current UI remaps to the Desktop profile. Called from the controller-save
        /// choke points (SaveAndSendGamepadMappings / PerformControllerSettingSave) while Desktop
        /// Mode is active, so edits never touch the underlying profile.
        /// </summary>
        private void SaveCurrentToDesktopProfile()
        {
            desktopControllerProfile = GetCurrentControllerProfileFromUI();
            desktopControllerProfile.DesktopControlsEnabled = true;
            SaveControllerProfileToStorage(DesktopProfileName, desktopControllerProfile);
            var settings = Windows.Storage.ApplicationData.Current.LocalSettings;
            settings.Values[DesktopModeSeededKey] = true;
        }

        private bool DesktopModeSeeded()
        {
            // The Desktop profile is "seeded" iff its storage container actually exists. Do NOT
            // rely on the old DesktopMode_Seeded flag — builds 0.3.2607-2609 set that flag without
            // ever creating the ControllerProfile_Desktop container, which left Desktop Mode blank.
            var settings = Windows.Storage.ApplicationData.Current.LocalSettings;
            return settings.Containers.ContainsKey($"ControllerProfile_{DesktopProfileName}");
        }

        /// <summary>
        /// One-time migration off the legacy Desktop Mode storage (builds 0.3.2607-2609 stored
        /// bindings under DesktopMode_* LocalSettings keys and set DesktopMode_Seeded WITHOUT ever
        /// writing the ControllerProfile_Desktop container). Clears those artifacts and any blank
        /// container so Desktop Mode re-seeds from the built-in preset. Self-clearing (won't re-run).
        /// </summary>
        private void MigrateDesktopModeStorageIfNeeded()
        {
            try
            {
                var settings = Windows.Storage.ApplicationData.Current.LocalSettings;
                bool legacy = settings.Values.ContainsKey("DesktopMode_GamepadMappings")
                           || settings.Values.ContainsKey("DesktopMode_JoystickMouseIndex");
                if (!legacy) return;

                if (settings.Containers.ContainsKey($"ControllerProfile_{DesktopProfileName}"))
                    settings.DeleteContainer($"ControllerProfile_{DesktopProfileName}");
                settings.Values.Remove("DesktopMode_GamepadMappings");
                settings.Values.Remove("DesktopMode_JoystickMouseIndex");
                settings.Values.Remove(DesktopModeSeededKey);
                Logger.Info("Migrated Desktop Mode off legacy storage - Desktop profile will re-seed from preset");
            }
            catch (Exception ex)
            {
                Logger.Warn($"MigrateDesktopModeStorageIfNeeded failed: {ex.Message}");
            }
        }

        /// <summary>
        /// The helper only resets a gamepad button that's explicitly present with Type=0; buttons
        /// absent from the new set stay mapped. So when switching to <paramref name="target"/>,
        /// inject Type=0 resets for any button that's currently mapped (live) but not in the target,
        /// so removed Desktop/underlying remaps actually clear on the controller.
        /// </summary>
        private void InjectGamepadResetsForRemovedButtons(ControllerProfile target)
        {
            if (target == null) return;
            if (target.GamepadButtonMappings == null)
                target.GamepadButtonMappings = new Dictionary<string, ButtonMapping>();
            if (gamepadButtonMappings == null) return;
            foreach (var key in gamepadButtonMappings.Keys)
            {
                if (!target.GamepadButtonMappings.ContainsKey(key))
                    target.GamepadButtonMappings[key] = new ButtonMapping { Type = 0, GamepadAction = 0 };
            }
        }

        /// <summary>
        /// Update the Button Remapping header hint to show which overlay is being edited:
        /// "Desktop Controls" when Desktop Mode is on, otherwise the active per-game profile
        /// name or "Global".
        /// </summary>
        private void UpdateButtonRemappingOverlayHint()
        {
            try
            {
                if (ButtonRemappingOverlayHint == null) return;
                string label;
                if (isDesktopModeActive)
                    label = "Desktop Controls";
                else if (LegionControllerProfileToggle?.IsOn == true && HasValidGame(currentGameName))
                    label = currentGameName;
                else
                    label = "Global";
                ButtonRemappingOverlayHint.Text = $"Editing: {label}";
            }
            catch (Exception ex)
            {
                Logger.Debug($"UpdateButtonRemappingOverlayHint error: {ex.Message}");
            }
        }

        /// <summary>The built-in Desktop Mode default: DPAD/LS→arrows, LSClick→Win, A→Enter, B→Esc, LB→LClick, LT→RClick.</summary>
        private Dictionary<string, ButtonMapping> BuildDesktopPresetMappings()
        {
            // HID key codes: Up=0x52, Down=0x51, Left=0x50, Right=0x4F, Enter=0x28, Escape=0x29, LeftGUI(Win)=0xE3.
            // LB/LT used for clicks to avoid the firmware drag-drop bug with triggers.
            // MouseButton: 0=Left, 1=Right, 2=Middle, 3=ScrollUp, 4=ScrollDown.
            return new Dictionary<string, ButtonMapping>
            {
                ["DPadUp"] = new ButtonMapping { Type = 1, KeyboardKeys = new List<int> { 0x52 } },
                ["DPadDown"] = new ButtonMapping { Type = 1, KeyboardKeys = new List<int> { 0x51 } },
                ["DPadLeft"] = new ButtonMapping { Type = 1, KeyboardKeys = new List<int> { 0x50 } },
                ["DPadRight"] = new ButtonMapping { Type = 1, KeyboardKeys = new List<int> { 0x4F } },
                ["LSUp"] = new ButtonMapping { Type = 1, KeyboardKeys = new List<int> { 0x52 } },
                ["LSDown"] = new ButtonMapping { Type = 1, KeyboardKeys = new List<int> { 0x51 } },
                ["LSClick"] = new ButtonMapping { Type = 1, KeyboardKeys = new List<int> { 0xE3 } },
                ["A"] = new ButtonMapping { Type = 1, KeyboardKeys = new List<int> { 0x28 } },
                ["B"] = new ButtonMapping { Type = 1, KeyboardKeys = new List<int> { 0x29 } },
                ["LB"] = new ButtonMapping { Type = 2, MouseButton = 0 },
                ["LT"] = new ButtonMapping { Type = 2, MouseButton = 1 },
            };
        }

        /// <summary>Restore Desktop Mode active state on startup (global flag), if it was left on.</summary>
        private void RestoreDesktopModeIfActive()
        {
            try
            {
                var settings = Windows.Storage.ApplicationData.Current.LocalSettings;
                bool wasActive = settings.Values.TryGetValue(DesktopModeActiveKey, out var v) && v is bool b && b;
                if (!wasActive || isDesktopModeActive) return;

                if (LegionDesktopControlsToggle != null)
                {
                    LegionDesktopControlsToggle.Toggled -= LegionDesktopControls_Toggled;
                    try { LegionDesktopControlsToggle.IsOn = true; }
                    finally { LegionDesktopControlsToggle.Toggled += LegionDesktopControls_Toggled; }
                }
                ApplyDesktopModeState(true, persistActive: false);
                Logger.Info("Desktop Mode restored on startup (was left active)");
            }
            catch (Exception ex)
            {
                Logger.Error($"RestoreDesktopModeIfActive error: {ex.Message}");
            }
        }

    }
}
