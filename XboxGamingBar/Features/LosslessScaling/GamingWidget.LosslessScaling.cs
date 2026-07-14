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

        // Lossless Scaling Helper Methods

        private async void UpdateLosslessScalingStatus()
        {
            try
            {
                bool isInstalled = losslessScalingInstalled?.Value ?? false;
                bool isRunning = losslessScalingRunning?.Value ?? false;

                Logger.Info($"UpdateLosslessScalingStatus called. Installed: {isInstalled}, Running: {isRunning}");

                // Marshal UI updates to the dispatcher thread
                await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                {
                    try
                    {
                        // Check if UI elements exist (may not be loaded yet)
                        if (LosslessScalingStatusText == null || LaunchLosslessScalingButton == null || ShowLosslessScalingWindowButton == null)
                        {
                            Logger.Warn("LosslessScaling UI elements not loaded yet, skipping status update");
                            return;
                        }

                        // Enable controls only when LS is installed
                        bool enableControls = isInstalled;
                        bool enableSaveButton = isInstalled && isRunning;

                        if (!isInstalled)
                        {
                            LosslessScalingStatusText.Text = "Not Installed";
                            LosslessScalingStatusText.Foreground = new SolidColorBrush(Windows.UI.Colors.Red);
                            LaunchLosslessScalingButton.Visibility = Visibility.Collapsed;
                            ShowLosslessScalingWindowButton.Visibility = Visibility.Collapsed;
                        }
                        else if (!isRunning)
                        {
                            LosslessScalingStatusText.Text = "Installed (Not Running)";
                            LosslessScalingStatusText.Foreground = new SolidColorBrush(Windows.UI.Colors.Orange);
                            LaunchLosslessScalingButton.Visibility = Visibility.Visible;
                            ShowLosslessScalingWindowButton.Visibility = Visibility.Collapsed;
                        }
                        else
                        {
                            LosslessScalingStatusText.Text = "Installed and Running";
                            LosslessScalingStatusText.Foreground = new SolidColorBrush(Windows.UI.Colors.Green);
                            LaunchLosslessScalingButton.Visibility = Visibility.Collapsed;
                            ShowLosslessScalingWindowButton.Visibility = Visibility.Visible;
                        }

                        // Enable/disable all Lossless Scaling controls
                        if (LosslessScalingEnabledToggle != null) LosslessScalingEnabledToggle.IsEnabled = enableControls;
                        if (LosslessScalingAutoScaleToggle != null) LosslessScalingAutoScaleToggle.IsEnabled = enableControls;
                        if (LosslessScalingAutoScaleDelaySlider != null) LosslessScalingAutoScaleDelaySlider.IsEnabled = enableControls;
                        if (LosslessScalingScalingTypeComboBox != null) LosslessScalingScalingTypeComboBox.IsEnabled = enableControls;
                        if (LosslessScalingFrameGenTypeComboBox != null) LosslessScalingFrameGenTypeComboBox.IsEnabled = enableControls;
                        if (LosslessScalingLSFG3ModeComboBox != null) LosslessScalingLSFG3ModeComboBox.IsEnabled = enableControls;
                        if (LosslessScalingLSFG3MultiplierComboBox != null) LosslessScalingLSFG3MultiplierComboBox.IsEnabled = enableControls;
                        if (LosslessScalingLSFG3TargetSlider != null) LosslessScalingLSFG3TargetSlider.IsEnabled = enableControls;
                        if (LosslessScalingLSFG2ModeComboBox != null) LosslessScalingLSFG2ModeComboBox.IsEnabled = enableControls;
                        if (LosslessScalingFlowScaleSlider != null) LosslessScalingFlowScaleSlider.IsEnabled = enableControls;
                        if (LosslessScalingSizeToggle != null) LosslessScalingSizeToggle.IsEnabled = enableControls;
                        // Additional Settings.xml-backed controls (added 2026-05-01)
                        if (LosslessScalingSyncModeComboBox != null) LosslessScalingSyncModeComboBox.IsEnabled = enableControls;
                        if (LosslessScalingCaptureApiComboBox != null) LosslessScalingCaptureApiComboBox.IsEnabled = enableControls;
                        if (LosslessScalingDrawFpsToggle != null) LosslessScalingDrawFpsToggle.IsEnabled = enableControls;
                        if (LosslessScalingHdrSupportToggle != null) LosslessScalingHdrSupportToggle.IsEnabled = enableControls;
                        if (LosslessScalingGsyncSupportToggle != null) LosslessScalingGsyncSupportToggle.IsEnabled = enableControls;
                        if (LosslessScalingResizeBeforeToggle != null) LosslessScalingResizeBeforeToggle.IsEnabled = enableControls;
                        if (LosslessScalingLS1TypeComboBox != null) LosslessScalingLS1TypeComboBox.IsEnabled = enableControls;
                        if (LosslessScalingMaxFrameLatencySlider != null) LosslessScalingMaxFrameLatencySlider.IsEnabled = enableControls;
                        if (LosslessScalingResetProfileButton != null) LosslessScalingResetProfileButton.IsEnabled = enableControls;
                        if (LosslessScalingSaveSettingsButton != null)
                        {
                            LosslessScalingSaveSettingsButton.IsEnabled = enableSaveButton;
                            // Update XY navigation to skip disabled Save button
                        }
                        if (LosslessScalingCreateProfileButton != null)
                        {
                            bool enableCreateProfile = enableControls && HasValidGame(currentGameName);
                            LosslessScalingCreateProfileButton.IsEnabled = enableCreateProfile;

                            // Update XY navigation for Scale toggle based on Create Profile button state
                            // When Create Profile is disabled, Scale should go up to Launch/ShowWindow button
                        }

                        // New Scaling Algorithm controls
                        if (LosslessScalingSharpnessSlider != null) LosslessScalingSharpnessSlider.IsEnabled = enableControls;
                        if (LosslessScalingFSROptimizeToggle != null) LosslessScalingFSROptimizeToggle.IsEnabled = enableControls;
                        if (LosslessScalingAnime4KSizeComboBox != null) LosslessScalingAnime4KSizeComboBox.IsEnabled = enableControls;
                        if (LosslessScalingAnime4KVRSToggle != null) LosslessScalingAnime4KVRSToggle.IsEnabled = enableControls;
                        if (LosslessScalingScaleModeComboBox != null) LosslessScalingScaleModeComboBox.IsEnabled = enableControls;
                        if (LosslessScalingScaleFactorSlider != null) LosslessScalingScaleFactorSlider.IsEnabled = enableControls;
                        if (LosslessScalingAspectRatioComboBox != null) LosslessScalingAspectRatioComboBox.IsEnabled = enableControls;

                        Logger.Info("LosslessScaling status UI updated successfully");
                    }
                    catch (Exception innerEx)
                    {
                        Logger.Error($"Error updating LosslessScaling status UI: {innerEx.Message}");
                        Logger.Error($"Stack trace: {innerEx.StackTrace}");
                    }
                });
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in UpdateLosslessScalingStatus: {ex.Message}");
                Logger.Error($"Stack trace: {ex.StackTrace}");
            }
        }

        private async void LaunchLosslessScalingButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Logger.Info("Launch Lossless Scaling button clicked");
                LaunchLosslessScalingButton.Content = "Launching...";
                LaunchLosslessScalingButton.IsEnabled = false;

                // Trigger launch via the helper service (which has permissions to launch exe directly)
                // Reset to false first, then set to true to ensure the change is detected
                losslessScalingLaunch.SetValue(false);
                losslessScalingLaunch.SetValue(true);
                Logger.Info("Sent launch request to helper");

                // Wait a bit and update status
                await Task.Delay(3000);
                UpdateLosslessScalingStatus();
                LaunchLosslessScalingButton.Content = "Launch";
                LaunchLosslessScalingButton.IsEnabled = true;
            }
            catch (Exception ex)
            {
                Logger.Error($"Error launching Lossless Scaling: {ex.Message}");
                LaunchLosslessScalingButton.Content = "Launch";
                LaunchLosslessScalingButton.IsEnabled = true;
            }
        }

        private void ShowLosslessScalingWindowButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Logger.Info("Show Lossless Scaling Window button clicked");
                // Reset to false first, then set to true to ensure the change is detected
                losslessScalingBringToForeground.SetValue(false);
                losslessScalingBringToForeground.SetValue(true);
                Logger.Info("Sent bring to foreground request to helper");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error showing Lossless Scaling window: {ex.Message}");
            }
        }

        private void LosslessScalingStatus_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            // Update status when installed/running state changes
            UpdateLosslessScalingStatus();
        }

        private void RunningGame_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            try
            {
                if (runningGame?.Value != null && runningGame.Value.IsValid())
                {
                    string exePath = runningGame.Value.GameId.Path;
                    string iconPath = runningGame.Value.GameId.IconPath;

                    if (!string.IsNullOrEmpty(exePath))
                    {
                        // Check if this is the same game (preserve cached icon path if new one is empty)
                        bool isSameGame = exePath.Equals(currentGameExePath, StringComparison.OrdinalIgnoreCase);

                        if (!string.IsNullOrEmpty(iconPath))
                        {
                            // New icon path provided - cache it
                            currentGameIconPath = iconPath;
                        }
                        else if (isSameGame && !string.IsNullOrEmpty(currentGameIconPath))
                        {
                            // Same game but no icon path in update - use cached path
                            iconPath = currentGameIconPath;
                            Logger.Info($"Using cached icon path for {exePath}");
                        }

                        currentGameExePath = exePath;
                        Logger.Info($"Updated currentGameExePath: {currentGameExePath}");

                        // Load the game icon for the Profiles tab
                        // Use helper-extracted icon if available, otherwise fall back to Steam lookup
                        LoadCurrentGameIcon(exePath, iconPath);

                        // Also update the Default Game Profile icon (may have been empty during initial sync)
                        UpdateDefaultProfileGameIcon();
                    }
                    else
                    {
                        currentGameExePath = "";
                        currentGameIconPath = "";
                        Logger.Info("Cleared currentGameExePath (no path in RunningGame)");

                        // Clear the game icon
                        LoadCurrentGameIcon(null, null);
                    }
                }
                else
                {
                    currentGameExePath = "";
                    currentGameIconPath = "";
                    Logger.Info("Cleared currentGameExePath (no running game)");

                    // Clear the game icon
                    LoadCurrentGameIcon(null, null);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in RunningGame_PropertyChanged: {ex.Message}");
            }
        }

        // Conflict resolution: Lossless Scaling Frame Gen vs AMD Fluid Motion Frames
        private bool isHandlingConflict = false; // Prevents infinite loop

        private void LosslessScalingFrameGenTypeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                string selectedType = LosslessScalingFrameGenTypeComboBox.SelectedItem as string ?? "Off";
                bool isFrameGenEnabled = selectedType != "Off";
                bool showLSFG3 = selectedType == "LSFG3";
                bool showLSFG2 = selectedType == "LSFG2";

                // Show/hide LSFG3 settings card
                if (LSFG3SettingsCard != null)
                {
                    LSFG3SettingsCard.Visibility = showLSFG3 ? Visibility.Visible : Visibility.Collapsed;
                }

                // Show/hide LSFG2 settings card
                if (LSFG2SettingsCard != null)
                {
                    LSFG2SettingsCard.Visibility = showLSFG2 ? Visibility.Visible : Visibility.Collapsed;
                }

                // Update XY navigation based on visible controls

                // Handle conflict with AMD Fluid Motion Frames
                if (isHandlingConflict) return;

                if (isFrameGenEnabled && AMDFluidMotionFrameToggle.IsOn)
                {
                    Logger.Info("Lossless Scaling Frame Gen enabled - auto-disabling AMD Fluid Motion Frames");
                    isHandlingConflict = true;
                    AMDFluidMotionFrameToggle.IsOn = false;
                    isHandlingConflict = false;

                    // Show conflict warning
                    if (LSConflictWarningBorder != null && LSConflictWarningText != null)
                    {
                        LSConflictWarningBorder.Visibility = Visibility.Visible;
                        LSConflictWarningText.Text = "AMD Fluid Motion Frames has been automatically disabled because it conflicts with Lossless Scaling Frame Generation.";
                    }
                }
                else if (!isFrameGenEnabled)
                {
                    // Hide warning when LS Frame Gen is disabled
                    if (LSConflictWarningBorder != null)
                    {
                        LSConflictWarningBorder.Visibility = Visibility.Collapsed;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in LosslessScalingFrameGenTypeComboBox_SelectionChanged: {ex.Message}");
            }
        }

        private void LosslessScalingScalingTypeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                string selectedType = LosslessScalingScalingTypeComboBox.SelectedItem as string ?? "Off";

                // Show/hide Sharpness panel (for FSR, NIS, SGSR, BCAS, LS1)
                bool showSharpness = selectedType == "FSR" || selectedType == "NIS" || selectedType == "SGSR" || selectedType == "BCAS" || selectedType == "LS1";
                bool showFSROptimize = selectedType == "FSR";
                bool showAnime4K = selectedType == "Anime4K";
                bool showLS1Type = selectedType == "LS1";
                bool showResizeBefore = selectedType != "Off";

                if (LosslessScalingSharpnessPanel != null)
                {
                    LosslessScalingSharpnessPanel.Visibility = showSharpness ? Visibility.Visible : Visibility.Collapsed;
                }

                // Show/hide FSR Optimize panel (FSR only)
                if (LosslessScalingFSROptimizePanel != null)
                {
                    LosslessScalingFSROptimizePanel.Visibility = showFSROptimize ? Visibility.Visible : Visibility.Collapsed;
                }

                // Show/hide Anime4K panel
                if (LosslessScalingAnime4KPanel != null)
                {
                    LosslessScalingAnime4KPanel.Visibility = showAnime4K ? Visibility.Visible : Visibility.Collapsed;
                }

                // Show/hide LS1 Type panel (LS1 only)
                if (LosslessScalingLS1TypePanel != null)
                {
                    LosslessScalingLS1TypePanel.Visibility = showLS1Type ? Visibility.Visible : Visibility.Collapsed;
                }

                // Show/hide Resize Before Scale panel (any type other than Off)
                if (LosslessScalingResizeBeforePanel != null)
                {
                    LosslessScalingResizeBeforePanel.Visibility = showResizeBefore ? Visibility.Visible : Visibility.Collapsed;
                }

                // Update XY navigation based on visible controls
                // ScalingTypeComboBox down: Sharpness -> FSROptimize -> Anime4K -> ScaleMode
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in LosslessScalingScalingTypeComboBox_SelectionChanged: {ex.Message}");
            }
        }

        private void LosslessScalingScaleModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                string selectedMode = LosslessScalingScaleModeComboBox.SelectedItem as string ?? "Auto";
                bool showAuto = selectedMode == "Auto";
                bool showCustom = selectedMode == "Custom";

                // Show/hide Auto mode panel
                if (LosslessScalingAutoModePanel != null)
                {
                    LosslessScalingAutoModePanel.Visibility = showAuto ? Visibility.Visible : Visibility.Collapsed;
                }

                // Show/hide Custom mode panel
                if (LosslessScalingCustomModePanel != null)
                {
                    LosslessScalingCustomModePanel.Visibility = showCustom ? Visibility.Visible : Visibility.Collapsed;
                }

                // Update XY navigation based on visible controls
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in LosslessScalingScaleModeComboBox_SelectionChanged: {ex.Message}");
            }
        }

        private void LosslessScalingLSFG3ModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                string selectedMode = LosslessScalingLSFG3ModeComboBox.SelectedItem as string ?? "FIXED";
                bool isAdaptive = selectedMode == "ADAPTIVE";

                // Hide multiplier when Adaptive mode is selected
                if (LosslessScalingLSFG3MultiplierPanel != null)
                {
                    LosslessScalingLSFG3MultiplierPanel.Visibility = isAdaptive ? Visibility.Collapsed : Visibility.Visible;
                }

                // Update XY navigation based on visible controls
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in LosslessScalingLSFG3ModeComboBox_SelectionChanged: {ex.Message}");
            }
        }

        private void AMDFluidMotionFrameToggle_Toggled(object sender, RoutedEventArgs e)
        {
            try
            {
                if (isHandlingConflict) return;

                string selectedType = LosslessScalingFrameGenTypeComboBox.SelectedItem as string ?? "Off";
                bool isLSFrameGenEnabled = selectedType != "Off";

                if (AMDFluidMotionFrameToggle.IsOn && isLSFrameGenEnabled)
                {
                    Logger.Info("AMD Fluid Motion Frames enabled - auto-disabling Lossless Scaling Frame Gen");
                    isHandlingConflict = true;
                    LosslessScalingFrameGenTypeComboBox.SelectedIndex = 0; // Set to "Off"
                    isHandlingConflict = false;

                    // Show conflict warning
                    if (LSConflictWarningBorder != null && LSConflictWarningText != null)
                    {
                        LSConflictWarningBorder.Visibility = Visibility.Visible;
                        LSConflictWarningText.Text = "Lossless Scaling Frame Generation has been automatically disabled because it conflicts with AMD Fluid Motion Frames.";
                    }
                }
                else if (!AMDFluidMotionFrameToggle.IsOn)
                {
                    // Hide warning if both are now off
                    if (LSConflictWarningBorder != null && !isLSFrameGenEnabled)
                    {
                        LSConflictWarningBorder.Visibility = Visibility.Collapsed;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in AMDFluidMotionFrameToggle_Toggled: {ex.Message}");
            }
        }

        private void LosslessScalingCurrentProfile_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            try
            {
                _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                {
                    if (LosslessScalingCurrentProfileText != null && losslessScalingCurrentProfile != null)
                    {
                        LosslessScalingCurrentProfileText.Text = losslessScalingCurrentProfile.Value ?? "Default";
                    }
                });
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in LosslessScalingCurrentProfile_PropertyChanged: {ex.Message}");
            }
        }

        private void LosslessScalingCreateProfileButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!string.IsNullOrEmpty(currentGameName))
                {
                    // Format: "GameName<||>WindowFilter" - use game name as window title filter for Lossless Scaling profile matching
                    string profileData = $"{currentGameName}<||>{currentGameName}";
                    losslessScalingCreateProfile.SetValue(profileData);
                    Logger.Info($"Creating Lossless Scaling profile for: {currentGameName}");
                }
                else
                {
                    Logger.Warn("Cannot create profile - no game detected");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in LosslessScalingCreateProfileButton_Click: {ex.Message}");
            }
        }

        private void LosslessScalingSaveSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Trigger save and restart
                losslessScalingSaveAndRestart.SetValue(true);
                Logger.Info("Saving Lossless Scaling settings and restarting");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in LosslessScalingSaveSettingsButton_Click: {ex.Message}");
            }
        }

        // Resets the active LS profile's properties to LS-default values. The
        // helper updates its in-memory state and pipes the new values back; the
        // user still needs Apply-and-Restart to persist the reset to Settings.xml.
        // That keeps Reset undoable until they explicitly commit.
        private void LosslessScalingResetProfileButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                losslessScalingResetProfile.SetValue(true);
                Logger.Info("Reset Lossless Scaling profile to defaults");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in LosslessScalingResetProfileButton_Click: {ex.Message}");
            }
        }

    }
}
