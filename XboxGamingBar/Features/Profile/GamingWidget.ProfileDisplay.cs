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
        private void UpdateProfileDisplay()
        {
            // Guard against calls during XAML initialization when controls aren't ready
            if (GlobalProfileTDPModeRow == null) return;

            // Determine visibility based on save settings
            var tdpModeVisibility = (legionGoDetected?.Value == true && SaveTDP) ? Visibility.Visible : Visibility.Collapsed;
            var tdpVisibility = SaveTDP ? Visibility.Visible : Visibility.Collapsed;
            var cpuBoostVisibility = SaveCPUBoost ? Visibility.Visible : Visibility.Collapsed;
            var cpuEPPVisibility = SaveCPUEPP ? Visibility.Visible : Visibility.Collapsed;
            var fpsLimitVisibility = SaveFPSLimit ? Visibility.Visible : Visibility.Collapsed;
            var powerModeVisibility = SaveOSPowerMode ? Visibility.Visible : Visibility.Collapsed;
            var amdVisibility = SaveAMDFeatures ? Visibility.Visible : Visibility.Collapsed;
            var hdrVisibility = SaveHDR ? Visibility.Visible : Visibility.Collapsed;
            var resolutionVisibility = SaveResolution ? Visibility.Visible : Visibility.Collapsed;
            var refreshRateVisibility = SaveRefreshRate ? Visibility.Visible : Visibility.Collapsed;
            var overlayLevelVisibility = SaveOverlayLevel ? Visibility.Visible : Visibility.Collapsed;

            // Update Global profile display (simple mode)
            GlobalProfileTDPModeRow.Visibility = tdpModeVisibility;
            GlobalProfileTDPModeText.Text = GetProfileTDPModeName(globalProfile);

            GlobalProfileTDPRow.Visibility = tdpVisibility;
            GlobalProfileTDPText.Text = $"{globalProfile.TDP}W";

            GlobalProfileCPUBoostRow.Visibility = cpuBoostVisibility;
            GlobalProfileCPUBoostText.Text = globalProfile.CPUBoost ? "On" : "Off";

            GlobalProfileCPUEPPRow.Visibility = cpuEPPVisibility;
            GlobalProfileCPUEPPText.Text = $"{globalProfile.CPUEPP}";


            GlobalProfileFPSLimitRow.Visibility = fpsLimitVisibility;
            GlobalProfileFPSLimitText.Text = globalProfile.FPSLimitEnabled ? $"{globalProfile.FPSLimitValue}" : "Off";

            GlobalProfilePowerModeRow.Visibility = powerModeVisibility;
            GlobalProfilePowerModeText.Text = GetPowerModeShortName(globalProfile.OSPowerMode);

            GlobalProfileAMDRow.Visibility = amdVisibility;
            var globalAmdFeatures = GetAMDFeaturesShortString(globalProfile);
            GlobalProfileAMDText.Text = string.IsNullOrEmpty(globalAmdFeatures) ? "Off" : globalAmdFeatures;

            GlobalProfileHDRRow.Visibility = hdrVisibility;
            GlobalProfileHDRText.Text = globalProfile.HDREnabled ? "On" : "Off";

            GlobalProfileResolutionRow.Visibility = resolutionVisibility;
            GlobalProfileResolutionText.Text = string.IsNullOrEmpty(globalProfile.Resolution) ? "Native" : globalProfile.Resolution;

            GlobalProfileRefreshRateRow.Visibility = refreshRateVisibility;
            GlobalProfileRefreshRateText.Text = GetRefreshRateShortString(globalProfile);

            GlobalProfileOverlayLevelRow.Visibility = overlayLevelVisibility;
            GlobalProfileOverlayLevelText.Text = GetOverlayLevelShortName(globalProfile.OverlayLevel);

            UpdateLastVisibleSeparator(
                (GlobalProfileTDPModeRow, GlobalProfileTDPModeSeparator),
                (GlobalProfileTDPRow, GlobalProfileTDPSeparator),
                (GlobalProfileCPUBoostRow, GlobalProfileCPUBoostSeparator),
                (GlobalProfileCPUEPPRow, GlobalProfileCPUEPPSeparator),
                (GlobalProfileFPSLimitRow, GlobalProfileFPSLimitSeparator),
                (GlobalProfilePowerModeRow, GlobalProfilePowerModeSeparator),
                (GlobalProfileAMDRow, GlobalProfileAMDSeparator),
                (GlobalProfileHDRRow, GlobalProfileHDRSeparator),
                (GlobalProfileResolutionRow, GlobalProfileResolutionSeparator),
                (GlobalProfileRefreshRateRow, GlobalProfileRefreshRateSeparator),
                (GlobalProfileOverlayLevelRow, GlobalProfileOverlayLevelSeparator));

            // Update AC/DC profile display
            ACDCProfileTDPModeRow.Visibility = tdpModeVisibility;
            ACProfileTDPModeText.Text = GetProfileTDPModeName(acProfile);
            DCProfileTDPModeText.Text = GetProfileTDPModeName(dcProfile);

            ACDCProfileTDPRow.Visibility = tdpVisibility;
            ACProfileTDPText.Text = $"{acProfile.TDP}W";
            DCProfileTDPText.Text = $"{dcProfile.TDP}W";

            ACDCProfileCPUBoostRow.Visibility = cpuBoostVisibility;
            ACProfileCPUBoostText.Text = acProfile.CPUBoost ? "On" : "Off";
            DCProfileCPUBoostText.Text = dcProfile.CPUBoost ? "On" : "Off";

            ACDCProfileCPUEPPRow.Visibility = cpuEPPVisibility;
            ACProfileCPUEPPText.Text = $"{acProfile.CPUEPP}";
            DCProfileCPUEPPText.Text = $"{dcProfile.CPUEPP}";


            ACDCProfileFPSLimitRow.Visibility = fpsLimitVisibility;
            ACProfileFPSLimitText.Text = acProfile.FPSLimitEnabled ? $"{acProfile.FPSLimitValue}" : "Off";
            DCProfileFPSLimitText.Text = dcProfile.FPSLimitEnabled ? $"{dcProfile.FPSLimitValue}" : "Off";

            ACDCProfilePowerModeRow.Visibility = powerModeVisibility;
            ACProfilePowerModeText.Text = GetPowerModeShortName(acProfile.OSPowerMode);
            DCProfilePowerModeText.Text = GetPowerModeShortName(dcProfile.OSPowerMode);

            ACDCProfileAMDRow.Visibility = amdVisibility;
            var acAmdFeatures = GetAMDFeaturesShortString(acProfile);
            var dcAmdFeatures = GetAMDFeaturesShortString(dcProfile);
            ACProfileAMDText.Text = string.IsNullOrEmpty(acAmdFeatures) ? "Off" : acAmdFeatures;
            DCProfileAMDText.Text = string.IsNullOrEmpty(dcAmdFeatures) ? "Off" : dcAmdFeatures;

            ACDCProfileHDRRow.Visibility = hdrVisibility;
            ACProfileHDRText.Text = acProfile.HDREnabled ? "On" : "Off";
            DCProfileHDRText.Text = dcProfile.HDREnabled ? "On" : "Off";

            ACDCProfileResolutionRow.Visibility = resolutionVisibility;
            ACProfileResolutionText.Text = string.IsNullOrEmpty(acProfile.Resolution) ? "Native" : acProfile.Resolution;
            DCProfileResolutionText.Text = string.IsNullOrEmpty(dcProfile.Resolution) ? "Native" : dcProfile.Resolution;

            ACDCProfileRefreshRateRow.Visibility = refreshRateVisibility;
            ACProfileRefreshRateText.Text = GetRefreshRateShortString(acProfile);
            DCProfileRefreshRateText.Text = GetRefreshRateShortString(dcProfile);

            ACDCProfileOverlayLevelRow.Visibility = overlayLevelVisibility;
            ACProfileOverlayLevelText.Text = GetOverlayLevelShortName(acProfile.OverlayLevel);
            DCProfileOverlayLevelText.Text = GetOverlayLevelShortName(dcProfile.OverlayLevel);

            UpdateLastVisibleSeparator(
                (ACDCProfileTDPModeRow, ACDCProfileTDPModeSeparator),
                (ACDCProfileTDPRow, ACDCProfileTDPSeparator),
                (ACDCProfileCPUBoostRow, ACDCProfileCPUBoostSeparator),
                (ACDCProfileCPUEPPRow, ACDCProfileCPUEPPSeparator),
                (ACDCProfileFPSLimitRow, ACDCProfileFPSLimitSeparator),
                (ACDCProfilePowerModeRow, ACDCProfilePowerModeSeparator),
                (ACDCProfileAMDRow, ACDCProfileAMDSeparator),
                (ACDCProfileHDRRow, ACDCProfileHDRSeparator),
                (ACDCProfileResolutionRow, ACDCProfileResolutionSeparator),
                (ACDCProfileRefreshRateRow, ACDCProfileRefreshRateSeparator),
                (ACDCProfileOverlayLevelRow, ACDCProfileOverlayLevelSeparator));

            // Update game profile display (if game is running)
            if (HasValidGame(currentGameName))
            {
                if (GetPerGamePowerSourceProfileEnabled(currentGameName))
                {
                    // Show AC/DC game profiles - TDP Mode (Legion only)
                    GameACDCProfileTDPModeRow.Visibility = tdpModeVisibility;
                    GameACProfileTDPModeText.Text = GetProfileTDPModeName(gameACProfile);
                    GameDCProfileTDPModeText.Text = GetProfileTDPModeName(gameDCProfile);

                    // TDP
                    GameACDCProfileTDPRow.Visibility = tdpVisibility;
                    GameACProfileTDPText.Text = $"{gameACProfile.TDP}W";
                    GameDCProfileTDPText.Text = $"{gameDCProfile.TDP}W";

                    // CPU Boost
                    GameACDCProfileCPUBoostRow.Visibility = cpuBoostVisibility;
                    GameACProfileCPUBoostText.Text = gameACProfile.CPUBoost ? "On" : "Off";
                    GameDCProfileCPUBoostText.Text = gameDCProfile.CPUBoost ? "On" : "Off";

                    // CPU EPP
                    GameACDCProfileCPUEPPRow.Visibility = cpuEPPVisibility;
                    GameACProfileCPUEPPText.Text = $"{gameACProfile.CPUEPP}";
                    GameDCProfileCPUEPPText.Text = $"{gameDCProfile.CPUEPP}";

                    // CPU State

                    // FPS Limit
                    GameACDCProfileFPSLimitRow.Visibility = fpsLimitVisibility;
                    GameACProfileFPSLimitText.Text = gameACProfile.FPSLimitEnabled ? $"{gameACProfile.FPSLimitValue}" : "Off";
                    GameDCProfileFPSLimitText.Text = gameDCProfile.FPSLimitEnabled ? $"{gameDCProfile.FPSLimitValue}" : "Off";

                    // Power Mode
                    GameACDCProfilePowerModeRow.Visibility = powerModeVisibility;
                    GameACProfilePowerModeText.Text = GetPowerModeShortName(gameACProfile.OSPowerMode);
                    GameDCProfilePowerModeText.Text = GetPowerModeShortName(gameDCProfile.OSPowerMode);

                    // AMD Features
                    GameACDCProfileAMDRow.Visibility = amdVisibility;
                    var gameACAmdFeatures = GetAMDFeaturesShortString(gameACProfile);
                    var gameDCAmdFeatures = GetAMDFeaturesShortString(gameDCProfile);
                    GameACProfileAMDText.Text = string.IsNullOrEmpty(gameACAmdFeatures) ? "Off" : gameACAmdFeatures;
                    GameDCProfileAMDText.Text = string.IsNullOrEmpty(gameDCAmdFeatures) ? "Off" : gameDCAmdFeatures;

                    // HDR
                    GameACDCProfileHDRRow.Visibility = hdrVisibility;
                    GameACProfileHDRText.Text = gameACProfile.HDREnabled ? "On" : "Off";
                    GameDCProfileHDRText.Text = gameDCProfile.HDREnabled ? "On" : "Off";

                    // Resolution
                    GameACDCProfileResolutionRow.Visibility = resolutionVisibility;
                    GameACProfileResolutionText.Text = string.IsNullOrEmpty(gameACProfile.Resolution) ? "Native" : gameACProfile.Resolution;
                    GameDCProfileResolutionText.Text = string.IsNullOrEmpty(gameDCProfile.Resolution) ? "Native" : gameDCProfile.Resolution;

                    // Refresh Rate
                    GameACDCProfileRefreshRateRow.Visibility = refreshRateVisibility;
                    GameACProfileRefreshRateText.Text = GetRefreshRateShortString(gameACProfile);
                    GameDCProfileRefreshRateText.Text = GetRefreshRateShortString(gameDCProfile);

                    // Overlay Level
                    GameACDCProfileOverlayLevelRow.Visibility = overlayLevelVisibility;
                    GameACProfileOverlayLevelText.Text = GetOverlayLevelShortName(gameACProfile.OverlayLevel);
                    GameDCProfileOverlayLevelText.Text = GetOverlayLevelShortName(gameDCProfile.OverlayLevel);

                    UpdateLastVisibleSeparator(
                        (GameACDCProfileTDPModeRow, GameACDCProfileTDPModeSeparator),
                        (GameACDCProfileTDPRow, GameACDCProfileTDPSeparator),
                        (GameACDCProfileCPUBoostRow, GameACDCProfileCPUBoostSeparator),
                        (GameACDCProfileCPUEPPRow, GameACDCProfileCPUEPPSeparator),
                        (GameACDCProfileFPSLimitRow, GameACDCProfileFPSLimitSeparator),
                        (GameACDCProfilePowerModeRow, GameACDCProfilePowerModeSeparator),
                        (GameACDCProfileAMDRow, GameACDCProfileAMDSeparator),
                        (GameACDCProfileHDRRow, GameACDCProfileHDRSeparator),
                        (GameACDCProfileResolutionRow, GameACDCProfileResolutionSeparator),
                        (GameACDCProfileRefreshRateRow, GameACDCProfileRefreshRateSeparator),
                        (GameACDCProfileOverlayLevelRow, GameACDCProfileOverlayLevelSeparator));
                }
                else
                {
                    // Show single game profile - TDP Mode (Legion only)
                    GameProfileTDPModeRow.Visibility = tdpModeVisibility;
                    GameProfileTDPModeText.Text = GetProfileTDPModeName(gameProfile);

                    // TDP
                    GameProfileTDPRow.Visibility = tdpVisibility;
                    GameProfileTDPText.Text = $"{gameProfile.TDP}W";

                    // CPU Boost
                    GameProfileCPUBoostRow.Visibility = cpuBoostVisibility;
                    GameProfileCPUBoostText.Text = gameProfile.CPUBoost ? "On" : "Off";

                    // CPU EPP
                    GameProfileCPUEPPRow.Visibility = cpuEPPVisibility;
                    GameProfileCPUEPPText.Text = $"{gameProfile.CPUEPP}";

                    // CPU State

                    // FPS Limit
                    GameProfileFPSLimitRow.Visibility = fpsLimitVisibility;
                    GameProfileFPSLimitText.Text = gameProfile.FPSLimitEnabled ? $"{gameProfile.FPSLimitValue}" : "Off";

                    // Power Mode
                    GameProfilePowerModeRow.Visibility = powerModeVisibility;
                    GameProfilePowerModeText.Text = GetPowerModeShortName(gameProfile.OSPowerMode);

                    // AMD Features
                    GameProfileAMDRow.Visibility = amdVisibility;
                    var gameAmdFeatures = GetAMDFeaturesShortString(gameProfile);
                    GameProfileAMDText.Text = string.IsNullOrEmpty(gameAmdFeatures) ? "Off" : gameAmdFeatures;

                    // HDR
                    GameProfileHDRRow.Visibility = hdrVisibility;
                    GameProfileHDRText.Text = gameProfile.HDREnabled ? "On" : "Off";

                    // Resolution
                    GameProfileResolutionRow.Visibility = resolutionVisibility;
                    GameProfileResolutionText.Text = string.IsNullOrEmpty(gameProfile.Resolution) ? "Native" : gameProfile.Resolution;

                    // Refresh Rate
                    GameProfileRefreshRateRow.Visibility = refreshRateVisibility;
                    GameProfileRefreshRateText.Text = GetRefreshRateShortString(gameProfile);

                    // Overlay Level
                    GameProfileOverlayLevelRow.Visibility = overlayLevelVisibility;
                    GameProfileOverlayLevelText.Text = GetOverlayLevelShortName(gameProfile.OverlayLevel);

                    UpdateLastVisibleSeparator(
                        (GameProfileTDPModeRow, GameProfileTDPModeSeparator),
                        (GameProfileTDPRow, GameProfileTDPSeparator),
                        (GameProfileCPUBoostRow, GameProfileCPUBoostSeparator),
                        (GameProfileCPUEPPRow, GameProfileCPUEPPSeparator),
                        (GameProfileFPSLimitRow, GameProfileFPSLimitSeparator),
                        (GameProfilePowerModeRow, GameProfilePowerModeSeparator),
                        (GameProfileAMDRow, GameProfileAMDSeparator),
                        (GameProfileHDRRow, GameProfileHDRSeparator),
                        (GameProfileResolutionRow, GameProfileResolutionSeparator),
                        (GameProfileRefreshRateRow, GameProfileRefreshRateSeparator),
                        (GameProfileOverlayLevelRow, GameProfileOverlayLevelSeparator));
                }
            }

            // Update all saved game profiles display
            UpdateAllGameProfilesDisplay();
        }

        /// <summary>
        /// Given an ordered list of (row, separator) pairs, hides the separator belonging to
        /// whichever row is actually the LAST VISIBLE one (not necessarily the last in the fixed
        /// design order, since users can enable/disable individual "Save X" categories per
        /// profile) and ensures every other visible row's separator stays shown. Each row's
        /// separator lives inside that row's own collapsible StackPanel, so setting Row.Visibility
        /// already hides/shows content+separator together — this only needs to correct the single
        /// trailing separator so the box doesn't end with a stray line flush against the padding.
        /// </summary>
        private static void UpdateLastVisibleSeparator(params (FrameworkElement Row, Border Separator)[] rows)
        {
            Border lastVisibleSeparator = null;
            foreach (var (row, separator) in rows)
            {
                if (row == null || separator == null) continue;
                if (row.Visibility != Visibility.Visible) continue;

                if (lastVisibleSeparator != null) lastVisibleSeparator.Visibility = Visibility.Visible;
                separator.Visibility = Visibility.Collapsed;
                lastVisibleSeparator = separator;
            }
        }

        private static string GetPowerModeShortName(int mode)
        {
            switch (mode)
            {
                case 0: return "Efficiency";
                case 1: return "Balanced";
                case 2: return "Performance";
                default: return "Balanced";
            }
        }

        private static string GetRefreshRateShortString(PerformanceProfile profile)
        {
            return profile.RefreshRate.HasValue ? $"{profile.RefreshRate.Value} Hz" : "Auto";
        }

        private static string GetOverlayLevelShortName(int level)
        {
            switch (level)
            {
                case 0: return "Off";
                case 1: return "Basic";
                case 2: return "Detailed";
                case 3: return "Full";
                default: return "Off";
            }
        }

        private static string GetLegionModeShortName(int mode)
        {
            switch (mode)
            {
                case 1: return "Quiet";
                case 2: return "Balanced";
                case 3: return "Performance";
                case 255: return "Custom";
                default: return "Balanced";
            }
        }

        /// <summary>
        /// Gets the TDP mode display name from a profile, accounting for custom presets.
        /// </summary>
        private string GetProfileTDPModeName(PerformanceProfile profile)
        {
            return GetLegionModeShortName(profile.LegionPerformanceMode);
        }

        /// <summary>
        /// Gets the TDPModeComboBox index from a profile, accounting for custom presets.
        /// Returns the index to use for TDPModeComboBox.SelectedIndex.
        /// </summary>
        private int GetProfileTDPModeIndex(PerformanceProfile profile)
        {
            // If TDPModeIndex is set, use it directly
            if (profile.TDPModeIndex >= 0 && profile.TDPModeIndex <= 3)
            {
                return profile.TDPModeIndex;
            }
            // Fall back to legacy: convert LegionPerformanceMode to index
            int[] modeValues = { 1, 2, 3, 255 }; // Quiet, Balanced, Performance, Custom
            int index = Array.IndexOf(modeValues, profile.LegionPerformanceMode);
            return index >= 0 ? index : 1; // Default to Balanced if not found
        }


        /// <summary>
        /// Gets a short string representation of enabled AMD features for display in profile cards
        /// </summary>
        private static string GetAMDFeaturesShortString(PerformanceProfile profile)
        {
            var features = new List<string>();

            if (profile.FluidMotionFrames) features.Add("AFMF");
            if (profile.RadeonSuperResolution) features.Add("RSR");
            if (profile.ImageSharpening) features.Add("RIS");
            if (profile.RadeonAntiLag) features.Add("AL");
            if (profile.RadeonBoost) features.Add("Boost");
            if (profile.RadeonChill) features.Add("Chill");

            return string.Join(",", features);
        }
    }
}
