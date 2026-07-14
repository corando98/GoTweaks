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
        private bool isLoadingAutoTDPSettings = false;
        private int previousFPSLimitBeforeSync = 0;  // Store FPS limit before sync was enabled
        private bool wasFPSLimitEnabledBeforeSync = false;  // Track if FPS limit toggle was on

        private void AutoTDPToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (AutoTDPToggle == null) return;
            if (isApplyingHelperUpdate) return;
            // Skip during mode changes - don't save forced-off state
            if (isUpdatingTDPMode) return;
            // Skip during AutoTDP settings load to prevent overwriting saved values
            if (isLoadingAutoTDPSettings) return;

            Logger.Info($"AutoTDP toggled to: {AutoTDPToggle.IsOn}");

            // If enabling AutoTDP on Legion Go and not in Custom mode, switch to Custom mode
            // AutoTDP requires Custom mode to control TDP values directly
            if (AutoTDPToggle.IsOn && legionGoDetected?.Value == true && legionPerformanceMode?.Value != 255)
            {
                Logger.Info($"AutoTDP enabled but Legion Go is in mode {legionPerformanceMode?.Value} (not Custom). Switching to Custom mode.");
                legionPerformanceMode?.SetValue(255);

                // Update BOTH UI dropdowns - this is critical for profile saving
                // Profile save reads from TDPModeComboBox via GetCurrentPresetLegionMode()
                int customIndex = GetCustomTdpModeIndex();
                if (TDPModeComboBox != null && TDPModeComboBox.SelectedIndex != customIndex)
                {
                    isUpdatingTDPMode = true;
                    TDPModeComboBox.SelectedIndex = customIndex;
                    lastTDPModeIndex = customIndex;
                    isUpdatingTDPMode = false;
                    UpdateTDPSliderEnabledState();
                }
                if (LegionPerformanceModeComboBox != null && LegionPerformanceModeComboBox.SelectedIndex != customIndex)
                {
                    LegionPerformanceModeComboBox.SelectedIndex = customIndex;
                }
            }

            // Update XY focus navigation based on toggle state
            UpdateAutoTDPFocusNavigation();

            // Update TDP limits display to show AutoTDP range or restore normal display
            UpdateAutoTDPLimitsDisplay();

            // Handle FPS limit sync
            if (AutoTDPToggle.IsOn)
            {
                // Apply FPS limit sync if enabled
                if (AutoTDPSyncFPSLimitCheckBox?.IsChecked == true)
                {
                    // Store current FPS limit state before syncing (if not already stored)
                    if (!wasFPSLimitEnabledBeforeSync && previousFPSLimitBeforeSync == 0)
                    {
                        wasFPSLimitEnabledBeforeSync = FPSLimitToggle?.IsOn ?? false;
                        previousFPSLimitBeforeSync = (int)(FPSLimitSlider?.Value ?? 60);
                    }
                    ApplyAutoTDPFPSLimit();
                }
            }
            else
            {
                // Restore FPS limit when AutoTDP is turned off (if sync was enabled)
                if (AutoTDPSyncFPSLimitCheckBox?.IsChecked == true)
                {
                    RestorePreviousFPSLimit();
                }
            }

            // Send to helper
            autoTDPEnabled?.SetValue(AutoTDPToggle.IsOn);

            // Save global setting
            var settings = ApplicationData.Current.LocalSettings;
            settings.Values["AutoTDPEnabled"] = AutoTDPToggle.IsOn;

            // Save to profile if AutoTDP saving is enabled
            if (SaveAutoTDP && !isLoadingProfile && !isSwitchingProfile)
            {
                SaveCurrentSettingsToProfile(currentProfileName);
            }
        }

        private void UpdateAutoTDPFocusNavigation()
        {
            if (AutoTDPToggle == null) return;

            // When AutoTDP is on, focus down goes to the slider
            // When AutoTDP is off, keep navigation inside Performance tab.
        }

        /// <summary>
        /// Updates the TDP display when AutoTDP is active - shows "AutoTDP" in main text
        /// and the min/max range in the limits area.
        /// </summary>
        private void UpdateAutoTDPLimitsDisplay()
        {
            if (AutoTDPToggle?.IsOn == true)
            {
                // Show "AutoTDP" in the main TDP value text
                if (TDPValueText != null)
                {
                    TDPValueText.Text = "AutoTDP";
                }

                // Show AutoTDP min/max range in limits area
                if (CurrentTDPValueText != null)
                {
                    int minTdp = (int)(AutoTDPMinSlider?.Value ?? 8);
                    int maxTdp = (int)(AutoTDPMaxSlider?.Value ?? 30);
                    CurrentTDPValueText.Text = $"{minTdp}W - {maxTdp}W";
                }
            }
            else
            {
                // When AutoTDP is off, restore normal display
                // Only update display if in Custom mode - don't overwrite preset mode names
                if (IsCustomTdpModeSelected())
                {
                    UpdateTDPDisplayText();
                }
                // Helper will update CurrentTDPValueText with actual hardware limits
            }
        }

        private void AutoTDPTargetFPSSlider_ValueChanged(object sender, Windows.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            // Guard against early XAML initialization calls
            if (AutoTDPTargetFPSSlider == null || AutoTDPToggle == null) return;
            if (isLoadingAutoTDPSettings) return; // Don't save during load
            if (isApplyingHelperUpdate) return;

            int targetFPS = (int)Math.Round(e.NewValue);

            // Update display immediately for visual feedback
            if (AutoTDPTargetFPSValue != null)
            {
                AutoTDPTargetFPSValue.Text = $"{targetFPS} FPS";
            }

            // Store pending value and debounce the send to helper
            pendingAutoTDPTargetFPS = targetFPS;

            // Initialize or restart debounce timer
            if (autoTDPTargetFPSDebounceTimer == null)
            {
                autoTDPTargetFPSDebounceTimer = new DispatcherTimer();
                autoTDPTargetFPSDebounceTimer.Interval = TimeSpan.FromMilliseconds(300);
                autoTDPTargetFPSDebounceTimer.Tick += AutoTDPTargetFPSDebounceTimer_Tick;
            }
            autoTDPTargetFPSDebounceTimer.Stop();
            autoTDPTargetFPSDebounceTimer.Start();
        }

        private void AutoTDPTargetFPSDebounceTimer_Tick(object sender, object e)
        {
            autoTDPTargetFPSDebounceTimer.Stop();

            int targetFPS = pendingAutoTDPTargetFPS;
            Logger.Info($"AutoTDP target FPS changed to: {targetFPS} (debounced)");

            // Sync FPS limit if enabled
            ApplyAutoTDPFPSLimit();

            // Send to helper
            autoTDPTargetFPS?.SetValue(targetFPS);

            // Save global setting
            var settings = ApplicationData.Current.LocalSettings;
            settings.Values["AutoTDPTargetFPS"] = targetFPS;

            // Save to profile if AutoTDP saving is enabled
            if (SaveAutoTDP && !isLoadingProfile && !isSwitchingProfile)
            {
                SaveCurrentSettingsToProfile(currentProfileName);
            }
        }

        private void AutoTDPMinSlider_ValueChanged(object sender, Windows.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            // Guard against early XAML initialization calls
            if (AutoTDPMinSlider == null || AutoTDPToggle == null) return;
            if (isLoadingAutoTDPSettings) return;
            if (isApplyingHelperUpdate) return;

            int minTDP = (int)Math.Round(e.NewValue);

            // Ensure min doesn't exceed max
            if (AutoTDPMaxSlider != null && minTDP > AutoTDPMaxSlider.Value)
            {
                minTDP = (int)AutoTDPMaxSlider.Value;
                AutoTDPMinSlider.Value = minTDP;
                return;
            }

            // Update display immediately for visual feedback
            if (AutoTDPMinValue != null)
            {
                AutoTDPMinValue.Text = $"{minTDP}W";
            }
            UpdateAutoTDPLimitsDisplay();

            // Store pending value and debounce the send to helper
            pendingAutoTDPMinTDP = minTDP;

            // Initialize or restart debounce timer
            if (autoTDPMinDebounceTimer == null)
            {
                autoTDPMinDebounceTimer = new DispatcherTimer();
                autoTDPMinDebounceTimer.Interval = TimeSpan.FromMilliseconds(300);
                autoTDPMinDebounceTimer.Tick += AutoTDPMinDebounceTimer_Tick;
            }
            autoTDPMinDebounceTimer.Stop();
            autoTDPMinDebounceTimer.Start();
        }

        private void AutoTDPMinDebounceTimer_Tick(object sender, object e)
        {
            autoTDPMinDebounceTimer.Stop();

            int minTDP = pendingAutoTDPMinTDP;
            Logger.Info($"AutoTDP min TDP changed to: {minTDP}W (debounced)");

            // Send to helper
            autoTDPMinTDP?.SetValue(minTDP);

            // Save global setting
            var settings = ApplicationData.Current.LocalSettings;
            settings.Values["AutoTDPMinTDP"] = minTDP;

            // Save to profile if AutoTDP saving is enabled
            if (SaveAutoTDP && !isLoadingProfile && !isSwitchingProfile)
            {
                SaveCurrentSettingsToProfile(currentProfileName);
            }
        }

        private void AutoTDPMaxSlider_ValueChanged(object sender, Windows.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            // Guard against early XAML initialization calls
            if (AutoTDPMaxSlider == null || AutoTDPToggle == null) return;
            if (isLoadingAutoTDPSettings) return;
            if (isApplyingHelperUpdate) return;

            int maxTDP = (int)Math.Round(e.NewValue);

            // Ensure max doesn't go below min
            if (AutoTDPMinSlider != null && maxTDP < AutoTDPMinSlider.Value)
            {
                maxTDP = (int)AutoTDPMinSlider.Value;
                AutoTDPMaxSlider.Value = maxTDP;
                return;
            }

            // Update display immediately for visual feedback
            if (AutoTDPMaxValue != null)
            {
                AutoTDPMaxValue.Text = $"{maxTDP}W";
            }
            UpdateAutoTDPLimitsDisplay();

            // Store pending value and debounce the send to helper
            pendingAutoTDPMaxTDP = maxTDP;

            // Initialize or restart debounce timer
            if (autoTDPMaxDebounceTimer == null)
            {
                autoTDPMaxDebounceTimer = new DispatcherTimer();
                autoTDPMaxDebounceTimer.Interval = TimeSpan.FromMilliseconds(300);
                autoTDPMaxDebounceTimer.Tick += AutoTDPMaxDebounceTimer_Tick;
            }
            autoTDPMaxDebounceTimer.Stop();
            autoTDPMaxDebounceTimer.Start();
        }

        private void AutoTDPMaxDebounceTimer_Tick(object sender, object e)
        {
            autoTDPMaxDebounceTimer.Stop();

            int maxTDP = pendingAutoTDPMaxTDP;
            Logger.Info($"AutoTDP max TDP changed to: {maxTDP}W (debounced)");

            // Send to helper
            autoTDPMaxTDP?.SetValue(maxTDP);

            // Save global setting
            var settings = ApplicationData.Current.LocalSettings;
            settings.Values["AutoTDPMaxTDP"] = maxTDP;

            // Save to profile if AutoTDP saving is enabled
            if (SaveAutoTDP && !isLoadingProfile && !isSwitchingProfile)
            {
                SaveCurrentSettingsToProfile(currentProfileName);
            }
        }

        private void LoadAutoTDPSettings()
        {
            isLoadingAutoTDPSettings = true;
            try
            {
                var settings = ApplicationData.Current.LocalSettings;

                // Load enabled state (default to OFF if not saved)
                if (settings.Values.TryGetValue("AutoTDPEnabled", out object enabledObj) && enabledObj is bool enabled)
                {
                    AutoTDPToggle.IsOn = enabled;
                }
                else
                {
                    AutoTDPToggle.IsOn = false;
                }

                // Load target FPS
                if (settings.Values.TryGetValue("AutoTDPTargetFPS", out object targetObj) && targetObj is int target)
                {
                    AutoTDPTargetFPSSlider.Value = target;
                }
                // Always sync text with slider value (handles both saved and default cases)
                if (AutoTDPTargetFPSValue != null && AutoTDPTargetFPSSlider != null)
                {
                    AutoTDPTargetFPSValue.Text = $"{(int)AutoTDPTargetFPSSlider.Value} FPS";
                }

                // Load min TDP
                if (settings.Values.TryGetValue("AutoTDPMinTDP", out object minObj) && minObj is int minTDP)
                {
                    AutoTDPMinSlider.Value = minTDP;
                    autoTDPMinTDP?.SetValue(minTDP);
                }
                // Always sync text with slider value
                if (AutoTDPMinValue != null && AutoTDPMinSlider != null)
                {
                    AutoTDPMinValue.Text = $"{(int)AutoTDPMinSlider.Value}W";
                }

                // Load max TDP
                if (settings.Values.TryGetValue("AutoTDPMaxTDP", out object maxObj) && maxObj is int maxTDP)
                {
                    AutoTDPMaxSlider.Value = maxTDP;
                    autoTDPMaxTDP?.SetValue(maxTDP);
                }
                // Always sync text with slider value
                if (AutoTDPMaxValue != null && AutoTDPMaxSlider != null)
                {
                    AutoTDPMaxValue.Text = $"{(int)AutoTDPMaxSlider.Value}W";
                }

                // Load controller type setting (0=PID, 1=Q-Learning, 2=SARSA)
                // Try new setting first, fall back to legacy UseMLMode
                int controllerType = 0;
                if (settings.Values.TryGetValue("AutoTDPControllerType", out object ctObj) && ctObj is int ct)
                {
                    controllerType = ct;
                }
                else if (settings.Values.TryGetValue("AutoTDPUseMLMode", out object mlModeObj) && mlModeObj is bool useMLMode)
                {
                    // Legacy migration: true -> Q-Learning (1), false -> PID (0)
                    controllerType = useMLMode ? 1 : 0;
                }

                if (AutoTDPControllerModeComboBox != null)
                {
                    AutoTDPControllerModeComboBox.SelectedIndex = Math.Max(0, Math.Min(2, controllerType));
                }
                autoTDPControllerType?.SetValue(controllerType);
                autoTDPUseMLMode?.SetValue(controllerType > 0);  // Legacy sync
                UpdateAutoTDPMLInfoPanelVisibility();

                // Load pause when unfocused setting (default: true)
                if (AutoTDPPauseWhenUnfocusedCheckBox != null)
                {
                    bool pauseWhenUnfocused = true; // Default to enabled
                    if (settings.Values.TryGetValue("AutoTDPPauseWhenUnfocused", out object pauseObj) && pauseObj is bool pauseVal)
                    {
                        pauseWhenUnfocused = pauseVal;
                    }
                    AutoTDPPauseWhenUnfocusedCheckBox.IsChecked = pauseWhenUnfocused;
                    autoTDPPauseWhenUnfocused?.SetValue(pauseWhenUnfocused);
                }

                // Load sync FPS limit setting (default: false)
                if (AutoTDPSyncFPSLimitCheckBox != null)
                {
                    bool syncFPSLimit = false; // Default to disabled
                    if (settings.Values.TryGetValue("AutoTDPSyncFPSLimit", out object syncObj) && syncObj is bool syncVal)
                    {
                        syncFPSLimit = syncVal;
                    }
                    AutoTDPSyncFPSLimitCheckBox.IsChecked = syncFPSLimit;

                    // If sync is enabled and AutoTDP is on, store current FPS limit and apply sync
                    if (syncFPSLimit && AutoTDPToggle?.IsOn == true)
                    {
                        wasFPSLimitEnabledBeforeSync = FPSLimitToggle?.IsOn ?? false;
                        previousFPSLimitBeforeSync = (int)(FPSLimitSlider?.Value ?? 60);
                        // Don't apply immediately during load - will be applied after FPS limit is loaded
                    }
                }

                // Update TDP limits display if AutoTDP is enabled
                UpdateAutoTDPLimitsDisplay();

                // Update focus navigation after loading settings
                UpdateAutoTDPFocusNavigation();
            }
            finally
            {
                isLoadingAutoTDPSettings = false;
            }
        }

        private void UpdateAutoTDPMLInfoPanelVisibility()
        {
            if (AutoTDPMLInfoPanel != null)
            {
                // Show ML panel for both Q-Learning (1) and SARSA (2)
                int controllerType = AutoTDPControllerModeComboBox?.SelectedIndex ?? 0;
                bool showMLPanel = controllerType > 0;
                AutoTDPMLInfoPanel.Visibility = showMLPanel ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void AutoTDPControllerModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (AutoTDPControllerModeComboBox == null) return;
            if (isLoadingAutoTDPSettings) return;
            if (isApplyingHelperUpdate) return;

            int controllerType = AutoTDPControllerModeComboBox.SelectedIndex;
            string[] controllerNames = { "PID Controller", "Q-Learning", "SARSA" };
            string name = controllerType >= 0 && controllerType < controllerNames.Length ? controllerNames[controllerType] : "Unknown";
            Logger.Info($"AutoTDP controller mode changed to: {name} ({controllerType})");

            // Update visibility of ML info panel
            UpdateAutoTDPMLInfoPanelVisibility();

            // Send to helper using new controller type property
            autoTDPControllerType?.SetValue(controllerType);

            // Also update legacy property for backwards compatibility
            autoTDPUseMLMode?.SetValue(controllerType > 0);

            // Save setting
            var settings = ApplicationData.Current.LocalSettings;
            settings.Values["AutoTDPControllerType"] = controllerType;
            // Keep legacy setting for backwards compatibility
            settings.Values["AutoTDPUseMLMode"] = controllerType > 0;

            // Save to profile if AutoTDP saving is enabled
            if (SaveAutoTDP && !isLoadingProfile && !isSwitchingProfile)
            {
                SaveCurrentSettingsToProfile(currentProfileName);
            }
        }

        private void AutoTDPPauseWhenUnfocusedCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (AutoTDPPauseWhenUnfocusedCheckBox == null) return;
            if (isLoadingAutoTDPSettings) return;
            if (isApplyingHelperUpdate) return;

            bool pauseWhenUnfocused = AutoTDPPauseWhenUnfocusedCheckBox.IsChecked == true;
            Logger.Info($"AutoTDP pause when unfocused changed to: {pauseWhenUnfocused}");

            // Send to helper
            autoTDPPauseWhenUnfocused?.SetValue(pauseWhenUnfocused);

            // Save setting
            var settings = ApplicationData.Current.LocalSettings;
            settings.Values["AutoTDPPauseWhenUnfocused"] = pauseWhenUnfocused;
        }

        private void AutoTDPSyncFPSLimitCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (AutoTDPSyncFPSLimitCheckBox == null) return;
            if (isLoadingAutoTDPSettings) return;
            if (isApplyingHelperUpdate) return;

            bool syncEnabled = AutoTDPSyncFPSLimitCheckBox.IsChecked == true;
            Logger.Info($"AutoTDP sync FPS limit changed to: {syncEnabled}");

            if (syncEnabled)
            {
                // Store current FPS limit state before syncing
                wasFPSLimitEnabledBeforeSync = FPSLimitToggle?.IsOn ?? false;
                previousFPSLimitBeforeSync = (int)(FPSLimitSlider?.Value ?? 60);
                Logger.Info($"Stored previous FPS limit: enabled={wasFPSLimitEnabledBeforeSync}, value={previousFPSLimitBeforeSync}");

                // Apply AutoTDP target as FPS limit
                ApplyAutoTDPFPSLimit();
            }
            else
            {
                // Restore previous FPS limit state
                RestorePreviousFPSLimit();
            }

            // Save setting
            var settings = ApplicationData.Current.LocalSettings;
            settings.Values["AutoTDPSyncFPSLimit"] = syncEnabled;
        }

        /// <summary>
        /// Applies the AutoTDP target FPS as the RTSS FPS limit.
        /// </summary>
        private void ApplyAutoTDPFPSLimit()
        {
            if (AutoTDPSyncFPSLimitCheckBox?.IsChecked != true) return;
            if (AutoTDPToggle?.IsOn != true) return;
            if (fpsLimit == null) return;

            int targetFPS = (int)(AutoTDPTargetFPSSlider?.Value ?? 60);

            // Enable FPS limit toggle if not already on
            if (FPSLimitToggle != null && !FPSLimitToggle.IsOn)
            {
                FPSLimitToggle.IsOn = true;
            }

            // Set FPS limit slider to target FPS
            if (FPSLimitSlider != null)
            {
                FPSLimitSlider.Value = targetFPS;
            }

            // Apply to RTSS
            fpsLimit.SetValue(targetFPS);
            Logger.Info($"AutoTDP: Synced FPS limit to target: {targetFPS} FPS");
        }

        /// <summary>
        /// Restores the FPS limit to its state before sync was enabled.
        /// </summary>
        private void RestorePreviousFPSLimit()
        {
            if (fpsLimit == null) return;

            if (wasFPSLimitEnabledBeforeSync)
            {
                // Restore previous FPS limit value
                if (FPSLimitSlider != null)
                {
                    FPSLimitSlider.Value = previousFPSLimitBeforeSync;
                }
                fpsLimit.SetValue(previousFPSLimitBeforeSync);
                Logger.Info($"AutoTDP: Restored previous FPS limit: {previousFPSLimitBeforeSync} FPS");
            }
            else
            {
                // FPS limit was off before sync, turn it off
                if (FPSLimitToggle != null)
                {
                    FPSLimitToggle.IsOn = false;
                }
                fpsLimit.SetValue(0);
                Logger.Info("AutoTDP: Restored FPS limit to off (was disabled before sync)");
            }
        }

        private async void AutoTDPResetMLButton_Click(object sender, RoutedEventArgs e)
        {
            // Show confirmation dialog
            var dialog = new ContentDialog
            {
                Title = "Reset ML Learning Data",
                Content = "This will erase all learned behavior and start from scratch. The ML controller will need time to re-learn optimal TDP values.\n\nAre you sure?",
                PrimaryButtonText = "Reset",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                Logger.Info("User confirmed ML reset");
                // Trigger reset via property
                autoTDPResetML?.SetValue(true);

                // Update status display
                if (AutoTDPMLStatusText != null)
                {
                    AutoTDPMLStatusText.Text = "Updates: 0 | Avg: 0.0 | Exploration: 30%";
                }
            }
        }

        private void AutoTDPMLStatus_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (AutoTDPMLStatusText != null && autoTDPMLStatus != null)
                {
                    AutoTDPMLStatusText.Text = autoTDPMLStatus.Value;
                }
            });
        }

        private void AutoTDPLearnedGameData_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                UpdateAutoTDPLearnedGameUI(autoTDPLearnedGameData?.Value);
            });
        }

        private void UpdateAutoTDPLearnedGameUI(string json)
        {
            if (AutoTDPLearnedSummaryText == null || AutoTDPLearnedHeatmapPanel == null)
                return;

            AutoTDPLearnedHeatmapPanel.Children.Clear();

            if (string.IsNullOrWhiteSpace(json))
            {
                AutoTDPLearnedSummaryText.Text = "No learned data yet";
                return;
            }

            if (!JsonObject.TryParse(json, out JsonObject obj))
            {
                AutoTDPLearnedSummaryText.Text = "Learned data unavailable";
                return;
            }

            string gameName = obj.GetNamedString("GameName", "");
            int targetFPS = (int)obj.GetNamedNumber("TargetFPS", 0);
            int learnedTDP = (int)obj.GetNamedNumber("LearnedTDP", 0);
            double confidence = obj.GetNamedNumber("Confidence", 0);
            int stableCount = (int)obj.GetNamedNumber("StableCount", 0);
            string lastUpdated = obj.GetNamedString("LastUpdatedUtc", "");
            bool hasLearned = false;
            if (obj.TryGetValue("HasLearned", out IJsonValue hasLearnedValue) && hasLearnedValue.ValueType == JsonValueType.Boolean)
            {
                hasLearned = hasLearnedValue.GetBoolean();
            }

            string titleName = string.IsNullOrWhiteSpace(gameName) ? "Current game" : gameName;
            if (hasLearned)
            {
                AutoTDPLearnedSummaryText.Text = $"{titleName} — {learnedTDP}W @ {targetFPS} FPS (conf {confidence:P0}, samples {stableCount})";
                if (!string.IsNullOrWhiteSpace(lastUpdated))
                {
                    AutoTDPLearnedSummaryText.Text += $" • {lastUpdated}";
                }
            }
            else
            {
                AutoTDPLearnedSummaryText.Text = string.IsNullOrWhiteSpace(gameName)
                    ? "No learned data yet"
                    : $"No learned data yet for {gameName}";
            }

            if (!obj.TryGetValue("Heatmap", out IJsonValue heatmapValue) || heatmapValue.ValueType != JsonValueType.Object)
            {
                AutoTDPLearnedHeatmapPanel.Children.Add(new TextBlock
                {
                    Text = "No heatmap data yet",
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromArgb(255, 136, 136, 136))
                });
                return;
            }

            var heatmapObj = heatmapValue.GetObject();
            var heatmap = new List<KeyValuePair<int, int>>();
            foreach (var key in heatmapObj.Keys)
            {
                if (!int.TryParse(key, out int tdp))
                    continue;

                int count = 0;
                var countVal = heatmapObj.GetNamedValue(key);
                if (countVal.ValueType == JsonValueType.Number)
                {
                    count = (int)countVal.GetNumber();
                }
                else if (countVal.ValueType == JsonValueType.String && int.TryParse(countVal.GetString(), out int parsed))
                {
                    count = parsed;
                }

                heatmap.Add(new KeyValuePair<int, int>(tdp, count));
            }

            if (heatmap.Count == 0)
            {
                AutoTDPLearnedHeatmapPanel.Children.Add(new TextBlock
                {
                    Text = "No heatmap data yet",
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromArgb(255, 136, 136, 136))
                });
                return;
            }

            heatmap = heatmap.OrderBy(kvp => kvp.Key).ToList();
            int maxCount = Math.Max(1, heatmap.Max(kvp => kvp.Value));

            foreach (var kvp in heatmap)
            {
                var row = new Grid { Margin = new Thickness(0, 2, 0, 0) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });

                var label = new TextBlock
                {
                    Text = $"{kvp.Key}W",
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromArgb(255, 170, 170, 170)),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(label, 0);

                var bar = new Windows.UI.Xaml.Controls.ProgressBar
                {
                    Minimum = 0,
                    Maximum = maxCount,
                    Value = kvp.Value,
                    Height = 6,
                    Foreground = new SolidColorBrush(Color.FromArgb(255, 139, 195, 74)),
                    Background = new SolidColorBrush(Color.FromArgb(255, 60, 60, 60)),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(bar, 1);

                var countText = new TextBlock
                {
                    Text = kvp.Value.ToString(),
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromArgb(255, 170, 170, 170)),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(countText, 2);

                row.Children.Add(label);
                row.Children.Add(bar);
                row.Children.Add(countText);
                AutoTDPLearnedHeatmapPanel.Children.Add(row);
            }
        }

        private void AutoTDPUseMLMode_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            // DEPRECATED: This handler is kept for backwards compatibility.
            // The new AutoTDPControllerType_PropertyChanged handler is preferred.
            // Only handle if autoTDPControllerType hasn't been updated yet (legacy profile)
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (AutoTDPControllerModeComboBox != null && autoTDPUseMLMode != null && autoTDPControllerType != null)
                {
                    // Only sync from legacy property if controller type is still at default (0)
                    // This ensures new profiles using ControllerType take precedence
                    if (autoTDPControllerType.Value == 0 && autoTDPUseMLMode.Value)
                    {
                        int expectedIndex = 1;  // Q-Learning for legacy ML mode
                        if (AutoTDPControllerModeComboBox.SelectedIndex != expectedIndex)
                        {
                            isApplyingHelperUpdate = true;
                            try
                            {
                                AutoTDPControllerModeComboBox.SelectedIndex = expectedIndex;
                                UpdateAutoTDPMLInfoPanelVisibility();
                                Logger.Info($"AutoTDP controller mode synced from legacy UseMLMode: Q-Learning");
                            }
                            finally
                            {
                                isApplyingHelperUpdate = false;
                            }
                        }
                    }
                }
            });
        }

        private void AutoTDPControllerType_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (AutoTDPControllerModeComboBox != null && autoTDPControllerType != null)
                {
                    // Sync combobox with helper's value (from profile)
                    int expectedIndex = autoTDPControllerType.Value;
                    if (expectedIndex < 0) expectedIndex = 0;
                    if (expectedIndex > 2) expectedIndex = 2;

                    if (AutoTDPControllerModeComboBox.SelectedIndex != expectedIndex)
                    {
                        isApplyingHelperUpdate = true;
                        try
                        {
                            AutoTDPControllerModeComboBox.SelectedIndex = expectedIndex;
                            UpdateAutoTDPMLInfoPanelVisibility();
                            string[] names = { "PID Controller", "Q-Learning", "SARSA" };
                            Logger.Info($"AutoTDP controller mode synced from helper: {names[expectedIndex]}");
                        }
                        finally
                        {
                            isApplyingHelperUpdate = false;
                        }
                    }
                }
            });
        }

        private void AutoTDPEnabled_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            // Capture epoch before queueing on UI thread. If a profile switch happens
            // between now and when the callback runs, the epoch will differ and we skip
            // the stale update (prevents helper's game-profile AutoTDP=true from overriding
            // Global profile's AutoTDP=false after a rapid profile switch).
            int epochSnapshot = profileSwitchEpoch;
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (epochSnapshot != profileSwitchEpoch)
                {
                    Logger.Info($"Skipping AutoTDP sync from helper (profile switched since queued, epoch {epochSnapshot} vs {profileSwitchEpoch})");
                    return;
                }
                if (AutoTDPToggle != null && autoTDPEnabled != null)
                {
                    // Sync toggle with helper's value (from profile)
                    if (AutoTDPToggle.IsOn != autoTDPEnabled.Value)
                    {
                        isApplyingHelperUpdate = true;
                        try
                        {
                            AutoTDPToggle.IsOn = autoTDPEnabled.Value;
                            Logger.Info($"AutoTDP enabled synced from helper: {autoTDPEnabled.Value}");
                        }
                        finally
                        {
                            isApplyingHelperUpdate = false;
                        }
                    }
                    // Always update the display when enabled state changes
                    UpdateAutoTDPLimitsDisplay();
                }
            });
        }

        private void AutoTDPTargetFPS_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (autoTDPTargetFPS != null)
                {
                    isApplyingHelperUpdate = true;
                    try
                    {
                        // Sync slider and text with helper's value (from profile)
                        if (AutoTDPTargetFPSSlider != null && (int)AutoTDPTargetFPSSlider.Value != autoTDPTargetFPS.Value)
                        {
                            AutoTDPTargetFPSSlider.Value = autoTDPTargetFPS.Value;
                        }
                        if (AutoTDPTargetFPSValue != null)
                        {
                            AutoTDPTargetFPSValue.Text = $"{autoTDPTargetFPS.Value} FPS";
                        }
                        Logger.Info($"AutoTDP target FPS synced from helper: {autoTDPTargetFPS.Value}");
                    }
                    finally
                    {
                        isApplyingHelperUpdate = false;
                    }
                }
            });
        }

        private void LoadStickyTDPSettings()
        {
            isLoadingStickyTDPSettings = true;
            try
            {
                var settings = ApplicationData.Current.LocalSettings;

                // Default to TRUE for new installs (key won't exist)
                bool enabled = true;
                if (settings.Values.TryGetValue("StickyTDPEnabled", out object enabledVal) && enabledVal is bool val)
                {
                    enabled = val;
                }
                StickyTDPToggle.IsOn = enabled;

                // Load interval setting (default 5 seconds)
                if (settings.Values.TryGetValue("StickyTDPInterval", out object intervalVal) && intervalVal is int interval)
                {
                    stickyTDPCheckIntervalSeconds = interval;
                    StickyTDPIntervalSlider.Value = interval;
                    if (StickyTDPIntervalValue != null)
                    {
                        StickyTDPIntervalValue.Text = $"{interval}s";
                    }
                }

                Logger.Info($"Loaded Sticky TDP settings: Enabled={enabled}, Interval={stickyTDPCheckIntervalSeconds}s");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error loading Sticky TDP settings: {ex.Message}");
            }
            finally
            {
                isLoadingStickyTDPSettings = false;
            }
        }

    }
}
