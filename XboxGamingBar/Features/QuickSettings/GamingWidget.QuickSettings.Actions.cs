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
        /// Re-dispatch a tile activation that came from a controller combo (helper push).
        /// Runs the exact same path as a physical tile tap so behavior is identical.
        /// </summary>
        private void SimulateTileHotkeyFired(string tileId)
        {
            try
            {
                if (string.IsNullOrEmpty(tileId)) return;
                Logger.Info($"Controller combo fired tile: {tileId}");
                var fake = new Windows.UI.Xaml.Controls.Button { Tag = tileId };
                QuickSettingsTile_Click(fake, null);
            }
            catch (Exception ex)
            {
                Logger.Error($"SimulateTileHotkeyFired error: {ex.Message}");
            }
        }

        /// <summary>
        /// Handle Quick Settings tile clicks
        /// </summary>
        private void QuickSettingsTile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string tileTag)
            {
                try
                {
                    // First check if it's in our local qsTileMap (includes custom shortcuts with GUID IDs)
                    if (qsTileMap.TryGetValue(tileTag, out var mappedTile) && !string.IsNullOrEmpty(mappedTile.CustomShortcut))
                    {
                        _ = SendCustomShortcutAsync(mappedTile.CustomShortcut, mappedTile.Name);
                    }
                    // Fallback: Check QuickSettingsConfig by ID (tile IDs are now GUIDs)
                    else if (QuickSettings.QuickSettingsConfig.Instance.GetTile(tileTag) is QuickSettings.QuickSettingsTile configTile
                             && configTile.Type == QuickSettings.TileType.CustomShortcut
                             && !string.IsNullOrEmpty(configTile.CustomShortcut))
                    {
                        _ = SendCustomShortcutAsync(configTile.CustomShortcut, configTile.Name);
                    }
                    else
                    {
                        switch (tileTag)
                        {
                            case "TDPMode":
                                CycleTDPMode();
                                break;
                            case "AutoTDP":
                                ToggleAutoTDPTile();
                                break;
                            case "Profile":
                                TogglePerGameProfile();
                                break;
                            case "Overlay":
                                CyclePerformanceOverlay();
                                break;
                            case "PowerMode":
                                CyclePowerMode();
                                break;
                            case "FPSLimit":
                                CycleFPSLimit();
                                break;
                            case "LegionVibration":
                                CycleLegionVibration();
                                break;
                            case "LegionVibrationMode":
                                CycleLegionVibrationMode();
                                break;
                            case "Resolution":
                                CycleResolution();
                                break;
                            case "Rotation":
                                CycleRotation();
                                break;
                            case "RefreshRate":
                                CycleRefreshRate();
                                break;
                            case "HDR":
                                ToggleHDR();
                                break;
                            case "LosslessScaling":
                                ToggleLosslessScaling();
                                break;
                            case "RIS":
                                ToggleRIS();
                                break;
                            case "AFMF":
                                ToggleAFMF();
                                break;
                            case "RSR":
                                ToggleRSR();
                                break;
                            case "AntiLag":
                                ToggleAntiLag();
                                break;
                            case "RadeonChill":
                                ToggleRadeonChill();
                                break;
                            case "CPUBoost":
                                ToggleCPUBoost();
                                break;
                            case "EPP":
                                CycleEPP();
                                break;
                            case "ScreenSaver":
                                ToggleScreenSaver();
                                break;
                            case "Keyboard":
                                TriggerOnScreenKeyboard();
                                break;
                            case "LegionTouchpad":
                                ToggleLegionTouchpad();
                                break;
                            case "Touchscreen":
                                ToggleTouchscreen();
                                break;
                            case "LegionLightMode":
                                CycleLegionLightMode();
                                break;
                            case "LegionDesktopControls":
                                ToggleLegionDesktopControls();
                                break;
                            case "LegionRemapControls":
                                ToggleRemapControlsProfile();
                                break;
                            case "LegionChargeLimit":
                                ToggleLegionChargeLimit();
                                break;
                            // Action tiles
                            case "ActionTaskManager":
                                LaunchTaskManager();
                                break;
                            case "ActionExplorer":
                                LaunchExplorer();
                                break;
                            case "ActionEndTask":
                                SendAltF4();
                                break;
                            case "Fullscreen":
                                ToggleFullscreen();
                                break;
                            case "ActionHibernate":
                                ExecuteHibernate();
                                break;
                            case "LegionPowerLight":
                                ToggleLegionPowerLight();
                                break;
                            case "LegionFanFullSpeed":
                                ToggleLegionFanFullSpeed();
                                break;
                            case "ControllerEmulation":
                                ToggleControllerEmulation();
                                break;
                        }
                    }

                    // Update tile states after action
                    UpdateQuickSettingsTileStates();
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error handling Quick Settings tile click: {ex.Message}");
                }
            }
        }

        private void CycleTDPMode()
        {
            // If default game profile is active, turn it off when user manually changes TDP mode
            if (defaultGameProfileEnabled?.Value == true && DefaultProfileToggle != null)
            {
                Logger.Info("TDP Mode tile clicked - turning off Default Game Profile");
                DefaultProfileToggle.IsOn = false;
                // The toggle change will trigger OnDefaultProfileEnabledChanged which re-enables controls
            }

            bool isLegion = legionGoDetected?.Value == true;
            int currentIndex = TDPModeComboBox?.SelectedIndex ?? 0;

            // Use custom presets if enabled
            if (useCustomTDPPresets && tdpPresets != null && tdpPresets.Count > 0)
            {
                // Total items = presets + Custom mode
                int totalItems = tdpPresets.Count + 1;
                int nextIndex = (currentIndex + 1) % totalItems;

                // Update combobox
                if (TDPModeComboBox != null)
                {
                    isUserInitiatedTDPModeChange = true;
                    TDPModeComboBox.SelectedIndex = nextIndex;
                    isUserInitiatedTDPModeChange = false;
                }

                // Determine the Legion mode and TDP to apply
                int nextLegionMode;
                int? nextTdp = null;
                string presetName;

                if (nextIndex < tdpPresets.Count)
                {
                    var preset = tdpPresets[nextIndex];
                    nextLegionMode = preset.LegionModeValue ?? 255;
                    nextTdp = preset.TdpWatts;
                    presetName = preset.Name;
                }
                else
                {
                    // Custom mode (last item)
                    nextLegionMode = 255;
                    presetName = "Custom";
                }

                // For Legion devices, set the hardware mode
                if (isLegion && legionPerformanceMode != null)
                {
                    legionPerformanceMode.SetValue(nextLegionMode);
                }

                // Apply TDP for software-controlled presets (no LegionModeValue or Custom mode)
                if (nextLegionMode == 255 && nextTdp.HasValue)
                {
                    // Apply the preset's TDP via the TDP slider/property
                    if (TDPSlider != null)
                    {
                        TDPSlider.Value = nextTdp.Value;
                    }
                    ScheduleQsTdpReapply();
                }
                else if (nextLegionMode == 255)
                {
                    // Pure Custom mode - schedule reapply for current slider value
                    ScheduleQsTdpReapply();
                }

                Logger.Info($"TDP Mode cycled to preset '{presetName}' (index={nextIndex}, legionMode={nextLegionMode}, tdp={nextTdp})");
            }
            else
            {
                // Default hardcoded mode cycling: Quiet(1) -> Balanced(2) -> Performance(3) -> Custom(255)
                int[] modeValues = { 1, 2, 3, 255 };
                int currentMode;
                if (isLegion && legionPerformanceMode != null)
                {
                    currentMode = legionPerformanceMode.Value;
                }
                else
                {
                    currentMode = (currentIndex >= 0 && currentIndex < modeValues.Length) ? modeValues[currentIndex] : 2;
                }

                // Calculate next mode
                int nextMode;
                switch (currentMode)
                {
                    case 1: nextMode = 2; break;     // Quiet -> Balanced
                    case 2: nextMode = 3; break;     // Balanced -> Performance
                    case 3: nextMode = 255; break;   // Performance -> Custom
                    case 255: nextMode = 1; break;   // Custom -> Quiet
                    default: nextMode = 2; break;
                }

                // For Legion devices, update the Legion property
                if (isLegion && legionPerformanceMode != null)
                {
                    legionPerformanceMode.SetValue(nextMode);
                }

                // Update TDPModeComboBox
                int nextIndex = Array.IndexOf(modeValues, nextMode);
                if (nextIndex >= 0 && TDPModeComboBox != null)
                {
                    isUserInitiatedTDPModeChange = true;
                    TDPModeComboBox.SelectedIndex = nextIndex;
                    isUserInitiatedTDPModeChange = false;
                }

                // If switching to Custom mode on Legion, schedule TDP reapply
                if (isLegion && nextMode == 255)
                {
                    ScheduleQsTdpReapply();
                }

                Logger.Info($"TDP Mode cycled from {currentMode} to {nextMode} (isLegion={isLegion})");
            }
        }

        private void ToggleAutoTDPTile()
        {
            if (AutoTDPToggle != null)
            {
                AutoTDPToggle.IsOn = !AutoTDPToggle.IsOn;
                Logger.Info($"AutoTDP tile toggled to: {AutoTDPToggle.IsOn}");
            }
        }

        private void ScheduleQsTdpReapply()
        {
            try
            {
                // Cancel existing timer
                if (qsTdpReapplyTimer != null)
                {
                    qsTdpReapplyTimer.Stop();
                }

                // Create new timer
                qsTdpReapplyTimer = new Windows.UI.Xaml.DispatcherTimer();
                qsTdpReapplyTimer.Interval = TimeSpan.FromSeconds(5);
                qsTdpReapplyTimer.Tick += async (s, e) =>
                {
                    qsTdpReapplyTimer.Stop();
                    // Reapply TDP - still in Custom mode?
                    bool isCustomMode = TDPModeComboBox?.SelectedIndex == 3;
                    if (isCustomMode)
                    {
                        // Read TDP value NOW (at timer fire time), not when scheduled
                        // This ensures we use the current profile's TDP if profile switched
                        int currentTdpValue = (int)(TDPSlider?.Value ?? 15);

                        // Ask helper to re-push current TDP to hardware. The previous
                        // N-1/N trick corrupted global.xml by briefly writing TDP-1.
                        try
                        {
                            if (App.IsConnected)
                            {
                                var request = new Windows.Foundation.Collections.ValueSet();
                                request.Add("ReapplyTDP", true);
                                await App.SendMessageAsync(request);
                                Logger.Info($"Quick Settings: Asked helper to reapply current TDP ({currentTdpValue}W)");
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn($"Quick Settings: ReapplyTDP send failed: {ex.Message}");
                        }
                    }
                };
                qsTdpReapplyTimer.Start();
                Logger.Info($"Quick Settings: Scheduled TDP reapply in 5 seconds");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error scheduling TDP reapply: {ex.Message}");
            }
        }

        private void TogglePerGameProfile()
        {
            // If Default Game Profile is active, toggle it off instead
            if (defaultGameProfileEnabled?.Value == true && DefaultProfileToggle != null)
            {
                Logger.Info("Profile tile clicked - turning off Default Game Profile");
                DefaultProfileToggle.IsOn = false;
                return;
            }

            // Only allow toggling when a game is detected
            if (perGameProfile != null && runningGame != null && runningGame.Value.IsValid())
            {
                bool newValue = !perGameProfile.Value;
                isUserInitiatedProfileToggle = true; // Flag this as user-initiated
                perGameProfile.SetValue(newValue);
                isUserInitiatedProfileToggle = false;
                Logger.Info($"Per-game profile toggled to {newValue}");
            }
            else
            {
                Logger.Info("Per-game profile toggle ignored - no game detected");
            }
        }

        private async void TriggerOnScreenKeyboard()
        {
            await ToggleTouchKeyboard();
        }

        /// <summary>
        /// Toggle the Windows touch keyboard using COM interop
        /// </summary>
        private async Task ToggleTouchKeyboard()
        {
            try
            {
                // Use helper to toggle touch keyboard via COM interop
                if (App.IsConnected)
                {
                    var message = new Windows.Foundation.Collections.ValueSet { { "ToggleTouchKeyboard", true } };
                    await App.SendMessageAsync(message);
                    Logger.Info("Touch keyboard toggle requested via helper");
                }
                else
                {
                    // Fallback to Win+Ctrl+O (accessibility keyboard shortcut)
                    QuickSettings.KeyboardShortcutHelper.SendShortcut("Win+Ctrl+O");
                    Logger.Info("On-screen keyboard triggered via Win+Ctrl+O (fallback)");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error toggling touch keyboard: {ex.Message}");
            }
        }

        /// <summary>
        /// Toggle RTSS OSD between off and last used level
        /// </summary>
        private void ToggleRTSSOsd()
        {
            try
            {
                if (osd == null)
                {
                    Logger.Warn("ToggleRTSSOsd: osd property is null");
                    return;
                }

                int currentLevel = (int)osd.Value;

                if (currentLevel > 0)
                {
                    // Currently on - save level and turn off
                    lastNonZeroOsdLevel = currentLevel;
                    osd.SetValue(0);
                    Logger.Info($"RTSS OSD toggled OFF (was level {currentLevel})");
                }
                else
                {
                    // Currently off - restore to last level
                    osd.SetValue(lastNonZeroOsdLevel);
                    Logger.Info($"RTSS OSD toggled ON to level {lastNonZeroOsdLevel}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error toggling RTSS OSD: {ex.Message}");
            }
        }

        /// <summary>
        /// Launch Task Manager via helper
        /// </summary>
        private void LaunchTaskManager()
        {
            try
            {
                _ = SendKeyboardShortcutViaHelper("Ctrl+Shift+Escape");
                Logger.Info("Task Manager launched via Ctrl+Shift+Escape");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error launching Task Manager: {ex.Message}");
            }
        }

        /// <summary>
        /// Launch File Explorer via helper
        /// </summary>
        private void LaunchExplorer()
        {
            try
            {
                _ = SendKeyboardShortcutViaHelper("Win+E");
                Logger.Info("Explorer launched via Win+E");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error launching Explorer: {ex.Message}");
            }
        }

        /// <summary>
        /// Close the foreground game window
        /// Uses Alt+Tab to switch to game, then Alt+F4 to close it
        /// </summary>
        private async void SendAltF4()
        {
            try
            {
                // Alt+Tab to switch focus to the game (away from Game Bar)
                _ = SendKeyboardShortcutViaHelper("Alt+Tab");
                Logger.Info("Alt+Tab sent to focus game");

                // Wait for focus switch
                await Task.Delay(200);

                // Now send Alt+F4 to close the focused game
                _ = SendKeyboardShortcutViaHelper("Alt+F4");
                Logger.Info("Alt+F4 sent to close game");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error closing game: {ex.Message}");
            }
        }

        /// <summary>
        /// Toggle fullscreen via F11
        /// Uses Alt+Tab first to focus the game
        /// </summary>
        private async void ToggleFullscreen()
        {
            try
            {
                // Alt+Tab to switch focus to the game (away from Game Bar)
                _ = SendKeyboardShortcutViaHelper("Alt+Tab");
                Logger.Info("Alt+Tab sent to focus game");

                // Wait for focus switch
                await Task.Delay(200);

                // F11 is the most universal fullscreen toggle
                _ = SendKeyboardShortcutViaHelper("F11");
                Logger.Info("Fullscreen toggled via F11");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error toggling fullscreen: {ex.Message}");
            }
        }

        // Resolutions to exclude from quick cycling (odd resolutions that don't scale well)
        private static readonly HashSet<string> excludedQuickResolutions = new HashSet<string>
        {
            "1680x1050"  // Odd 16:10 resolution that doesn't scale cleanly
        };

        private void CycleResolution()
        {
            // Resolution override only applies to the built-in panel; no-op while docked with
            // only an external monitor active (the tile itself is dimmed + disabled too).
            if (!IsInternalPanelActive) return;
            if (resolution != null && resolutions?.Value != null && resolutions.Value.Count > 0)
            {
                // Filter out excluded resolutions for quick cycling
                var quickResolutions = resolutions.Value
                    .Where(r => !excludedQuickResolutions.Contains(r))
                    .ToList();

                if (quickResolutions.Count == 0)
                {
                    quickResolutions = resolutions.Value; // Fallback to all if filter removes everything
                }

                string currentRes = resolution.Value;
                int currentIndex = quickResolutions.IndexOf(currentRes);

                // If current resolution is not in quick list, start from first
                if (currentIndex < 0) currentIndex = -1;

                int nextIndex = (currentIndex + 1) % quickResolutions.Count;
                string nextRes = quickResolutions[nextIndex];
                resolution.SetValue(nextRes);
                Logger.Info($"Resolution cycled from {currentRes} to {nextRes}");
            }
        }

        /// <summary>
        /// Cycles display orientation between Landscape (0) and Portrait (1).
        /// </summary>
        /// <summary>
        /// Cycles the display refresh rate between 60Hz and 120Hz. Only offers a rate if the
        /// display actually reports it as supported (refreshRates.Value); otherwise no-ops rather
        /// than requesting a mode the driver would reject or silently ignore.
        /// </summary>
        private void CycleRefreshRate()
        {
            if (refreshRate == null || !IsInternalPanelActive) return;

            int current = refreshRate.Value;
            int next = current >= 120 ? 60 : 120;

            if (refreshRates?.Value != null && refreshRates.Value.Count > 0 && !refreshRates.Value.Contains(next))
            {
                Logger.Info($"Refresh rate {next}Hz not in supported list, skipping cycle");
                return;
            }

            refreshRate.SetValue(next);
            Logger.Info($"Refresh rate cycled from {current}Hz to {next}Hz");
        }

        private void CycleRotation()
        {
            // Display rotation only applies to the built-in panel; no-op while docked with
            // only an external monitor active (the tile itself is dimmed + disabled too).
            if (!IsInternalPanelActive) return;
            if (displayOrientation != null)
            {
                int currentOrientation = displayOrientation.Value;
                // Cycle between Landscape (0) and Portrait (1)
                // Skip flipped modes (2, 3) for simple toggle behavior
                int nextOrientation = (currentOrientation == 0) ? 1 : 0;
                displayOrientation.SetValue(nextOrientation);
                Logger.Info($"Display orientation cycled from {currentOrientation} to {nextOrientation}");
            }
        }

        private void ToggleHDR()
        {
            if (hdrEnabled != null && (hdrSupported?.Value ?? false))
            {
                bool newValue = !hdrEnabled.Value;
                hdrEnabled.SetValue(newValue);
                // Update the toggle so SettingChanged fires and saves to profile
                if (HDRToggle != null)
                    HDRToggle.IsOn = newValue;
                Logger.Info($"HDR toggled to {newValue}");
            }
        }

        private void ToggleLosslessScaling()
        {
            if (losslessScalingEnabled != null)
            {
                bool newValue = !losslessScalingEnabled.Value;
                losslessScalingEnabled.SetValue(newValue);
                Logger.Info($"Lossless Scaling toggled to {newValue}");
            }
        }

        private void ToggleAFMF()
        {
            if (amdFluidMotionFrameEnabled != null && (amdFluidMotionFrameSupported?.Value ?? false))
            {
                bool newValue = !amdFluidMotionFrameEnabled.Value;
                amdFluidMotionFrameEnabled.SetValue(newValue);
                // Update the toggle so SettingChanged fires and saves to profile
                if (AMDFluidMotionFrameToggle != null)
                    AMDFluidMotionFrameToggle.IsOn = newValue;
                Logger.Info($"AFMF toggled to {newValue}");
            }
        }

        private void ToggleRSR()
        {
            if (amdRadeonSuperResolutionEnabled != null && (amdRadeonSuperResolutionSupported?.Value ?? false))
            {
                bool newValue = !amdRadeonSuperResolutionEnabled.Value;
                amdRadeonSuperResolutionEnabled.SetValue(newValue);
                // Update the toggle so SettingChanged fires and saves to profile
                if (AMDRadeonSuperResolutionToggle != null)
                    AMDRadeonSuperResolutionToggle.IsOn = newValue;
                Logger.Info($"RSR toggled to {newValue}");
            }
        }

        private void ToggleRIS()
        {
            if (amdImageSharpeningEnabled != null && (amdImageSharpeningSupported?.Value ?? false))
            {
                bool newValue = !amdImageSharpeningEnabled.Value;
                amdImageSharpeningEnabled.SetValue(newValue);
                AMDImageSharpeningToggle.IsOn = newValue;
                Logger.Info($"RIS toggled to {newValue}");
            }
        }

        private void ToggleAntiLag()
        {
            if (amdRadeonAntiLagEnabled != null && (amdRadeonAntiLagSupported?.Value ?? false))
            {
                bool newValue = !amdRadeonAntiLagEnabled.Value;
                amdRadeonAntiLagEnabled.SetValue(newValue);
                // Update the toggle so SettingChanged fires and saves to profile
                if (AMDRadeonAntiLagToggle != null)
                    AMDRadeonAntiLagToggle.IsOn = newValue;
                Logger.Info($"Anti-Lag toggled to {newValue}");
            }
        }

        private void ToggleRadeonChill()
        {
            if (amdRadeonChillEnabled != null && (amdRadeonChillSupported?.Value ?? false))
            {
                bool newValue = !amdRadeonChillEnabled.Value;
                amdRadeonChillEnabled.SetValue(newValue);
                // Update the toggle so SettingChanged fires and saves to profile
                if (AMDRadeonChillToggle != null)
                    AMDRadeonChillToggle.IsOn = newValue;
                Logger.Info($"Radeon Chill toggled to {newValue}");
            }
        }

        private void ToggleCPUBoost()
        {
            if (cpuBoost != null)
            {
                bool newValue = !cpuBoost.Value;
                cpuBoost.SetValue(newValue);
                // Update the toggle so SettingChanged fires and saves to profile
                if (CPUBoostToggle != null)
                    CPUBoostToggle.IsOn = newValue;
                Logger.Info($"CPU Boost toggled to {newValue}");
            }
        }

        private void CyclePowerMode()
        {
            if (osPowerMode != null)
            {
                // Cycle: Efficiency (0) -> Balanced (1) -> Performance (2) -> Efficiency (0)
                int currentMode = osPowerMode.Value;
                int nextMode = (currentMode + 1) % 3;
                osPowerMode.SetValue(nextMode);

                // Update the combobox and value text in Performance tab
                isLoadingOSPowerMode = true;
                try
                {
                    OSPowerModeComboBox.SelectedIndex = nextMode;
                    OSPowerModeValue.Text = OSPowerModeNames[nextMode];
                }
                finally
                {
                    isLoadingOSPowerMode = false;
                }

                Logger.Info($"Power Mode cycled to {OSPowerModeNames[nextMode]}");

                // Save the change to profile
                if (!isInitialSync && !isApplyingHelperUpdate && !isLoadingProfile && SaveOSPowerMode)
                {
                    Logger.Info($"Saving OS Power Mode change to profile: {currentProfileName}");
                    SaveCurrentSettingsToProfile(currentProfileName);
                }
            }
        }

        private void CycleEPP()
        {
            if (cpuEPP != null)
            {
                int currentValue = (int)cpuEPP.Value;
                int nextValue;
                switch (currentValue)
                {
                    case 0: nextValue = 30; break;
                    case 30: nextValue = 80; break;
                    case 80: nextValue = 100; break;
                    case 100: nextValue = 0; break;
                    default: nextValue = 0; break;
                }
                cpuEPP.SetValue(nextValue);

                // Update slider to match (SaveCurrentSettingsToProfile reads from it)
                if (CPUEPPSlider != null)
                {
                    CPUEPPSlider.Value = nextValue;
                }

                Logger.Info($"EPP cycled from {currentValue} to {nextValue}");

                // Save the change to profile
                // Use direct save to bypass isApplyingHelperUpdate check - this is a user-initiated action
                if (!isInitialSync && !isLoadingProfile && SaveCPUEPP && !string.IsNullOrEmpty(currentProfileName))
                {
                    try
                    {
                        var profile = GetProfile(currentProfileName);
                        profile.CPUEPP = nextValue;
                        SaveProfileToStorage(currentProfileName, profile);
                        Logger.Info($"Saved EPP {nextValue} to profile: {currentProfileName}");
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Failed to save EPP to profile: {ex.Message}");
                    }
                }
            }
        }

        private void CyclePerformanceOverlay()
        {
            // (AMD Adrenalin overlay pass-through removed - RTSS only.)
            {
                if (osd != null)
                {
                    int currentLevel = (int)osd.Value;
                    int nextLevel = (currentLevel + 1) % 4;
                    osd.SetValue(nextLevel);
                    Logger.Info($"RTSS Performance Overlay cycled from {currentLevel} to {nextLevel}");
                }
            }
        }

        /// <summary>
        /// Cycle FPS limit through: Off -> MaxRefresh -> MaxRefresh/2 -> MaxRefresh/3 -> Off
        /// </summary>
        private void CycleFPSLimit()
        {
            if (fpsLimit == null) return;

            // Get max refresh rate from current display
            int maxRefresh = 60; // Default
            if (refreshRates?.Value != null && refreshRates.Value.Count > 0)
            {
                maxRefresh = refreshRates.Value.Max();
            }

            // Calculate FPS limit values: Max, Max/2, Max/3
            int[] fpsValues = new int[]
            {
                0,                          // Off (unlimited)
                maxRefresh,                 // e.g., 144
                maxRefresh / 2,             // e.g., 72
                maxRefresh / 3              // e.g., 48
            };

            // Find current index and cycle to next
            int currentLimit = fpsLimit.Value;
            int currentIndex = 0;
            for (int i = 0; i < fpsValues.Length; i++)
            {
                if (fpsValues[i] == currentLimit)
                {
                    currentIndex = i;
                    break;
                }
            }

            int nextIndex = (currentIndex + 1) % fpsValues.Length;
            int nextLimit = fpsValues[nextIndex];

            fpsLimit.SetValue(nextLimit);
            Logger.Info($"FPS Limit cycled from {currentLimit} to {nextLimit} (max refresh: {maxRefresh})");

            // Sync the Performance tab FPS Limit controls
            isApplyingHelperUpdate = true;
            try
            {
                // Update slider maximum to current refresh rate
                FPSLimitSlider.Maximum = maxRefresh;

                if (nextLimit > 0)
                {
                    FPSLimitToggle.IsOn = true;
                    FPSLimitSlider.Value = nextLimit;
                }
                else
                {
                    FPSLimitToggle.IsOn = false;
                }
            }
            finally
            {
                isApplyingHelperUpdate = false;
            }

            // Save to profile if FPS Limit saving is enabled
            if (SaveFPSLimit && !isLoadingProfile && !isSwitchingProfile)
            {
                SaveCurrentSettingsToProfile(currentProfileName);
            }
        }

        private void CycleLegionVibration()
        {
            if (legionGoDetected?.Value == true && legionVibration != null)
            {
                int currentLevel = legionVibration.Value;
                int nextLevel = (currentLevel + 1) % 4; // 0-3: Off, Weak, Medium, Strong
                legionVibration.SetValue(nextLevel);
                UpdateQuickSettingsTileStates();
                Logger.Info($"Legion Vibration intensity cycled from {currentLevel} to {nextLevel}");
            }
        }

        private void CycleLegionVibrationMode()
        {
            if (legionGoDetected?.Value == true && legionVibrationMode != null)
            {
                int currentMode = legionVibrationMode.Value;
                // Modes are 1-based (FPS=1, Racing=2, AVG=3, SPG=4, RPG=5 — see VibrationMode
                // enum in LegionGoController.cs), not 0-based. The old "% 5" cycled 0-4, which
                // could land on the invalid value 0 and could never reach RPG (5).
                int nextMode = (currentMode % 5) + 1; // 1-5: FPS, Racing, AVG, SPG, RPG
                legionVibrationMode.SetValue(nextMode);
                UpdateQuickSettingsTileStates();
                Logger.Info($"Legion Vibration mode cycled from {currentMode} to {nextMode}");
            }
        }

        /// <summary>
        /// FPS Limit toggle changed - set FPS limit to slider value or 0 (off)
        /// </summary>
        private void FPSLimitToggle_Toggled(object sender, RoutedEventArgs e)
        {
            // Update display text when toggle is enabled
            if (FPSLimitToggle.IsOn && FPSLimitValue != null)
            {
                FPSLimitValue.Text = $"{(int)FPSLimitSlider.Value} FPS";
            }

            if (fpsLimit == null || isApplyingHelperUpdate) return;

            if (FPSLimitToggle.IsOn)
            {
                // Get max refresh rate and update slider
                int maxRefresh = 60;
                if (refreshRates?.Value != null && refreshRates.Value.Count > 0)
                {
                    maxRefresh = refreshRates.Value.Max();
                }
                FPSLimitSlider.Maximum = maxRefresh;

                // If slider is at minimum (15) or below, set to max refresh as default
                int limit = (int)FPSLimitSlider.Value;
                if (limit <= 15)
                {
                    limit = maxRefresh;
                    FPSLimitSlider.Value = limit;
                }

                // Update display text with the final value
                if (FPSLimitValue != null)
                {
                    FPSLimitValue.Text = $"{limit} FPS";
                }

                fpsLimit.SetValue(limit);
                Logger.Info($"FPS Limit enabled: {limit}");
            }
            else
            {
                // Disable FPS limit (0 = unlimited)
                fpsLimit.SetValue(0);
                Logger.Info("FPS Limit disabled");
            }

            // Save to profile if FPS Limit saving is enabled
            // Don't save during DGP restoration - values being restored to original state
            if (SaveFPSLimit && !isLoadingProfile && !isSwitchingProfile && !isRestoringFromDefaultProfile)
            {
                SaveCurrentSettingsToProfile(currentProfileName);
            }
        }

        /// <summary>
        /// RSR toggle changed - disable RIS if RSR is enabled (mutually exclusive)
        /// </summary>
        private void AMDRadeonSuperResolutionToggle_Toggled(object sender, RoutedEventArgs e)
        {
            // RSR and RIS are mutually exclusive - enabling one disables the other
            if (AMDRadeonSuperResolutionToggle.IsOn && AMDImageSharpeningToggle.IsOn && !isLoadingProfile && !isSwitchingProfile && !isApplyingHelperUpdate)
            {
                Logger.Info("RSR enabled - disabling RIS (mutually exclusive)");
                AMDImageSharpeningToggle.IsOn = false;
            }

            // Call the generic setting changed handler
            SettingChanged(sender, e);
        }

        /// <summary>
        /// RIS toggle changed - disable RSR if RIS is enabled (mutually exclusive)
        /// </summary>
        private void AMDImageSharpeningToggle_Toggled(object sender, RoutedEventArgs e)
        {
            // RSR and RIS are mutually exclusive - enabling one disables the other
            if (AMDImageSharpeningToggle.IsOn && AMDRadeonSuperResolutionToggle.IsOn && !isLoadingProfile && !isSwitchingProfile && !isApplyingHelperUpdate)
            {
                Logger.Info("RIS enabled - disabling RSR (mutually exclusive)");
                AMDRadeonSuperResolutionToggle.IsOn = false;
            }

            // Call the generic setting changed handler
            SettingChanged(sender, e);
        }

        /// <summary>
        /// Radeon Anti-Lag toggle changed - disable Chill if Anti-Lag is enabled (mutually exclusive)
        /// </summary>
        private void AMDRadeonAntiLagToggle_Toggled(object sender, RoutedEventArgs e)
        {
            // Anti-Lag and Chill are mutually exclusive
            if (AMDRadeonAntiLagToggle.IsOn && AMDRadeonChillToggle.IsOn && !isLoadingProfile && !isSwitchingProfile && !isApplyingHelperUpdate)
            {
                Logger.Info("Anti-Lag enabled - disabling Chill (mutually exclusive)");
                AMDRadeonChillToggle.IsOn = false;
            }

            // Call the generic setting changed handler
            SettingChanged(sender, e);
        }

        /// <summary>
        /// Radeon Boost toggle changed - disable Chill if Boost is enabled (mutually exclusive)
        /// </summary>
        private void AMDRadeonBoostToggle_Toggled(object sender, RoutedEventArgs e)
        {
            // Boost and Chill are mutually exclusive
            if (AMDRadeonBoostToggle.IsOn && AMDRadeonChillToggle.IsOn && !isLoadingProfile && !isSwitchingProfile && !isApplyingHelperUpdate)
            {
                Logger.Info("Boost enabled - disabling Chill (mutually exclusive)");
                AMDRadeonChillToggle.IsOn = false;
            }

            // Call the generic setting changed handler
            SettingChanged(sender, e);
        }

        /// <summary>
        /// Radeon Chill toggle changed - disable Anti-Lag and Boost if Chill is enabled (mutually exclusive)
        /// </summary>
        private void AMDRadeonChillToggle_Toggled(object sender, RoutedEventArgs e)
        {
            // Chill is mutually exclusive with Anti-Lag and Boost
            if (AMDRadeonChillToggle.IsOn && !isLoadingProfile && !isSwitchingProfile && !isApplyingHelperUpdate)
            {
                if (AMDRadeonAntiLagToggle.IsOn)
                {
                    Logger.Info("Chill enabled - disabling Anti-Lag (mutually exclusive)");
                    AMDRadeonAntiLagToggle.IsOn = false;
                }
                if (AMDRadeonBoostToggle.IsOn)
                {
                    Logger.Info("Chill enabled - disabling Boost (mutually exclusive)");
                    AMDRadeonBoostToggle.IsOn = false;
                }
            }

            // Call the generic setting changed handler
            SettingChanged(sender, e);
        }

        /// <summary>
        /// FPS Limit slider changed - update FPS limit if toggle is on (with debouncing)
        /// </summary>
        private void FPSLimitSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            // Always update the display text
            if (FPSLimitValue != null)
            {
                FPSLimitValue.Text = $"{(int)e.NewValue} FPS";
            }

            if (fpsLimit == null || isApplyingHelperUpdate) return;

            if (FPSLimitToggle.IsOn)
            {
                int limit = (int)e.NewValue;
                fpsLimitPendingValue = limit;

                // Initialize debounce timer if needed
                if (fpsLimitDebounceTimer == null)
                {
                    fpsLimitDebounceTimer = new DispatcherTimer();
                    fpsLimitDebounceTimer.Interval = TimeSpan.FromMilliseconds(FPS_LIMIT_DEBOUNCE_MS);
                    fpsLimitDebounceTimer.Tick += FPSLimitDebounceTimer_Tick;
                }

                // Restart the debounce timer
                fpsLimitDebounceTimer.Stop();
                fpsLimitDebounceTimer.Start();
            }
        }

        /// <summary>
        /// Debounce timer tick - apply the pending FPS limit value
        /// </summary>
        private void FPSLimitDebounceTimer_Tick(object sender, object e)
        {
            fpsLimitDebounceTimer?.Stop();

            if (fpsLimit != null && FPSLimitToggle.IsOn)
            {
                fpsLimit.SetValue(fpsLimitPendingValue);
                Logger.Info($"FPS Limit changed (debounced): {fpsLimitPendingValue}");

                // Save to profile if FPS Limit saving is enabled
                if (SaveFPSLimit && !isLoadingProfile && !isSwitchingProfile)
                {
                    SaveCurrentSettingsToProfile(currentProfileName);
                }
            }
        }

        /// <summary>
        /// Update FPS Limit controls based on RTSS installed status and current fpsLimit value
        /// </summary>
        private void UpdateFPSLimitControls()
        {
            UpdateFPSLimitControls(rtssInstalled?.Value == true);
        }

        /// <summary>
        /// Update FPS Limit controls based on RTSS installed status
        /// </summary>
        private void UpdateFPSLimitControls(bool rtssAvailable)
        {
            // Dispatch to UI thread since this may be called from property callback on non-UI thread
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                try
                {
                    if (isUnloading) return;

                    // Guard against null controls during initialization or shutdown
                    if (FPSLimitToggle == null || FPSLimitSlider == null) return;

                    FPSLimitToggle.IsEnabled = rtssAvailable;

                    // Update slider maximum to current refresh rate
                    int maxRefresh = 60; // Default
                    if (refreshRates?.Value != null && refreshRates.Value.Count > 0)
                    {
                        maxRefresh = refreshRates.Value.Max();
                    }
                    FPSLimitSlider.Maximum = maxRefresh;

                    // Set tick frequency based on max refresh rate (show ~5-8 ticks)
                    int tickFreq;
                    if (maxRefresh >= 144)
                        tickFreq = 24;
                    else if (maxRefresh >= 120)
                        tickFreq = 20;
                    else if (maxRefresh >= 90)
                        tickFreq = 15;
                    else
                        tickFreq = 10;
                    FPSLimitSlider.TickFrequency = tickFreq;

                    // Sync toggle/slider with fpsLimit value
                    if (fpsLimit != null)
                    {
                        isApplyingHelperUpdate = true;
                        try
                        {
                            int limit = fpsLimit.Value;
                            if (limit > 0)
                            {
                                FPSLimitToggle.IsOn = true;
                                // Clamp value to slider range
                                FPSLimitSlider.Value = Math.Min(limit, maxRefresh);
                            }
                            else
                            {
                                FPSLimitToggle.IsOn = false;
                            }
                        }
                        finally
                        {
                            isApplyingHelperUpdate = false;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error in UpdateFPSLimitControls: {ex.Message}");
                }
            });
        }
        private void ToggleLegionTouchpad()
        {
            if (legionGoDetected?.Value == true && legionTouchpadEnabled != null)
            {
                bool newValue = !legionTouchpadEnabled.Value;
                legionTouchpadEnabled.SetValue(newValue);
                Logger.Info($"Legion Touchpad toggled to {newValue}");
            }
        }

        private void ToggleTouchscreen()
        {
            if (legionGoDetected?.Value == true && touchscreenEnabled != null)
            {
                bool newValue = !touchscreenEnabled.Value;
                touchscreenEnabled.SetValue(newValue);
                Logger.Info($"Touchscreen toggled to {newValue}");
            }
        }

        private void CycleLegionLightMode()
        {
            if (legionGoDetected?.Value == true && legionLightMode != null)
            {
                int currentMode = legionLightMode.Value;
                int nextMode = (currentMode + 1) % 5; // 0-4: Off, Static, Breathing, Rainbow, Spiral
                legionLightMode.SetValue(nextMode);
                Logger.Info($"Legion Light Mode cycled from {currentMode} to {nextMode}");
            }
        }

        private void ToggleLegionDesktopControls()
        {
            if (legionGoDetected?.Value == true && LegionDesktopControlsToggle != null)
            {
                bool newValue = !LegionDesktopControlsToggle.IsOn;
                LegionDesktopControlsToggle.IsOn = newValue;
                // The Toggled event handler will apply the mappings
                Logger.Info($"Legion Desktop Controls toggled to {newValue}");
            }
        }

        private void ToggleLegionChargeLimit()
        {
            if (legionGoDetected?.Value == true && legionChargeLimit != null)
            {
                bool newValue = !legionChargeLimit.Value;
                legionChargeLimit.SetValue(newValue);
                // Also update the toggle in Legion tab if it exists
                if (LegionChargeLimitToggle != null)
                {
                    LegionChargeLimitToggle.IsOn = newValue;
                }
                Logger.Info($"Legion Charge Limit toggled to {(newValue ? "80%" : "Off")}");
            }
        }

        // Cycle order for the Quick-tab Controller tile: every supported virtual-device
        // tag from ViiperDeviceTypeComboBox. VIIPER is the only emulation backend (the
        // helper clamps any persisted legacy backend selection forward - CLAUDE.md SS21),
        // so this is the only cycle left. Ends at "Off" so the user can always get the
        // tile back to disabled.
        private static readonly string[] ControllerEmulationViiperCycle = new[]
        {
            // xboxelite2 dropped from the cycle alongside the UI option (the
            // helper coerces persisted values forward). switchpro is reachable
            // through the new "nintendo" family entry — switching backend to
            // nintendo + picking a sub-device from the Debug-tab dropdown.
            "xbox360", "dualshock4", "dualsenseedge", "steam-generic", "sony", "nintendo"
        };

        private void ToggleControllerEmulation()
        {
            if (controllerEmulationEnabled == null)
            {
                return;
            }
            if (controllerEmulationAvailable?.Value != true)
            {
                Logger.Info("Controller Emulation tile click ignored — emulation not available on this device.");
                return;
            }

            CycleControllerEmulationViiper(controllerEmulationEnabled.Value);
        }

        private void CycleControllerEmulationViiper(bool currentlyEnabled)
        {
            string[] cycle = ControllerEmulationViiperCycle;

            if (!currentlyEnabled)
            {
                string firstDevice = cycle[0];
                viiperDeviceType?.SetValue(firstDevice);
                controllerEmulationEnabled.SetValue(true);
                if (ControllerEmulationEnabledToggle != null)
                {
                    ControllerEmulationEnabledToggle.IsOn = true;
                }
                Logger.Info($"Controller Emulation (VIIPER) cycled: Off -> {firstDevice}");
                return;
            }

            string current = viiperDeviceType?.Value ?? cycle[0];
            int currentIndex = Array.IndexOf(cycle, current);
            int nextIndex = currentIndex + 1;

            if (currentIndex < 0 || nextIndex >= cycle.Length)
            {
                controllerEmulationEnabled.SetValue(false);
                if (ControllerEmulationEnabledToggle != null)
                {
                    ControllerEmulationEnabledToggle.IsOn = false;
                }
                Logger.Info("Controller Emulation (VIIPER) cycled: -> Off");
            }
            else
            {
                string nextDevice = cycle[nextIndex];
                viiperDeviceType?.SetValue(nextDevice);
                Logger.Info($"Controller Emulation (VIIPER) cycled: {current} -> {nextDevice}");
            }
        }

        private void ToggleRemapControlsProfile()
        {
            if (legionGoDetected?.Value != true)
                return;

            if (LegionControllerProfileToggle == null)
                return;

            // Toggle the per-game controller profile
            LegionControllerProfileToggle.IsOn = !LegionControllerProfileToggle.IsOn;
            Logger.Info($"Toggled per-game controller profile to: {LegionControllerProfileToggle.IsOn}");

            // Update Quick Settings tiles
            UpdateQuickSettingsTileStates();
        }

        /// <summary>
        /// Show/hide customization panel
        /// </summary>
        private void QuickSettingsCustomize_Click(object sender, RoutedEventArgs e)
        {
            if (QuickSettingsCustomizePanel != null)
            {
                // Enter edit mode
                qsEditMode = true;
                qsSelectedTileForMove = null;

                QuickSettingsCustomizePanel.Visibility = Visibility.Visible;
                QuickSettingsCustomizeButton.Visibility = Visibility.Collapsed;

                // Register keyboard handler for B/Escape to deselect
                QuickSettingsCustomizePanel.KeyDown -= QuickSettingsCustomizePanel_KeyDown;
                QuickSettingsCustomizePanel.KeyDown += QuickSettingsCustomizePanel_KeyDown;

                // Update column button visuals
                UpdateColumnButtonVisuals();

                // Rebuild UIs with edit mode enabled
                BuildSortableGrid();
                RebuildQuickSettingsTiles();  // Shows hidden tiles with overlay in edit mode
            }
        }

        /// <summary>
        /// Handle keyboard input in customize panel (B/Escape to deselect)
        /// </summary>
        private void QuickSettingsCustomizePanel_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Escape ||
                e.Key == Windows.System.VirtualKey.GamepadB)
            {
                if (qsSelectedTileForMove != null)
                {
                    qsSelectedTileForMove = null;
                    UpdateSelectedTileIndicator(null);
                    BuildSortableGrid();
                    e.Handled = true;
                }
            }
        }

        /// <summary>
        /// Close customization panel
        /// </summary>
        private void QuickSettingsCustomizeDone_Click(object sender, RoutedEventArgs e)
        {
            if (QuickSettingsCustomizePanel != null)
            {
                // Exit edit mode
                qsEditMode = false;
                qsSelectedTileForMove = null;
                UpdateSelectedTileIndicator(null);

                QuickSettingsCustomizePanel.Visibility = Visibility.Collapsed;
                QuickSettingsCustomizeButton.Visibility = Visibility.Visible;

                // Save config and rebuild tiles without edit overlays
                SaveQuickSettingsConfig();
                RebuildQuickSettingsTiles();
                UpdateQuickSettingsTileStates();
            }
        }

        /// <summary>
        /// Set column count to 3
        /// </summary>
        private void ColumnCount3_Click(object sender, RoutedEventArgs e)
        {
            if (qsColumnCount != 3)
            {
                qsColumnCount = 3;
                UpdateColumnButtonVisuals();
                BuildSortableGrid();
                RebuildQuickSettingsTiles();
            }
        }

        /// <summary>
        /// Set column count to 4
        /// </summary>
        private void ColumnCount4_Click(object sender, RoutedEventArgs e)
        {
            if (qsColumnCount != 4)
            {
                qsColumnCount = 4;
                UpdateColumnButtonVisuals();
                BuildSortableGrid();
                RebuildQuickSettingsTiles();
            }
        }

        /// <summary>
        /// Set column count to 5
        /// </summary>
        private void ColumnCount5_Click(object sender, RoutedEventArgs e)
        {
            if (qsColumnCount != 5)
            {
                qsColumnCount = 5;
                UpdateColumnButtonVisuals();
                BuildSortableGrid();
                RebuildQuickSettingsTiles();
            }
        }

        /// <summary>
        /// Update column button visuals to show current selection
        /// </summary>
        private void UpdateColumnButtonVisuals()
        {
            if (Column3Button == null || Column4Button == null || Column5Button == null) return;

            var selectedBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 120, 180));
            var normalBrush = tileOffBrush ?? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 26, 28, 30));

            Column3Button.Background = qsColumnCount == 3 ? selectedBrush : normalBrush;
            Column4Button.Background = qsColumnCount == 4 ? selectedBrush : normalBrush;
            Column5Button.Background = qsColumnCount == 5 ? selectedBrush : normalBrush;
        }

        /// <summary>
        /// Add a custom shortcut tile
        /// </summary>
        private void AddCustomShortcut_Click(object sender, RoutedEventArgs e)
        {
            string name = CustomShortcutNameBox?.Text?.Trim();
            string shortcut = GetCustomShortcutKeysString();

            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(shortcut))
            {
                Logger.Warn("Custom shortcut name or shortcut is empty");
                return;
            }

            AddCustomShortcutTile(name, shortcut);

            // Clear inputs
            if (CustomShortcutNameBox != null) CustomShortcutNameBox.Text = "";
            _customShortcutKeys.Clear();
            UpdateCustomShortcutKeyTags();

            UpdateQuickSettingsTileStates();
        }

    }
}
