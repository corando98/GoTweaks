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

        private void SwitchProfile()
        {
            string targetProfile = GetTargetProfileName();

            if (targetProfile != currentProfileName)
            {
                Logger.Info($"Switching from '{currentProfileName}' to '{targetProfile}' profile");

                // Set flag to prevent auto-saves during transition
                isSwitchingProfile = true;

                try
                {
                    // Save current profile before switching, but SKIP for game-related transitions.
                    // 1. FROM a game profile (game close): helper already pushed global values to the
                    //    widget UI (AutoTDP=false, Mode=Quiet, etc.) BEFORE sending PerGameProfile=false.
                    //    Saving now would capture global values and corrupt the game profile.
                    // 2. TO a game profile (game open): helper sends game values (Mode=Custom, AutoTDP=true)
                    //    BEFORE the profile switch. Saving now would capture game values and corrupt Global.
                    // Individual toggle/slider handlers already save user changes immediately,
                    // so skipping here is safe — the profile is always up-to-date.
                    if (!currentProfileName.StartsWith("Game_") && !targetProfile.StartsWith("Game_"))
                    {
                        SaveCurrentSettingsToProfile(currentProfileName);
                    }

                    // Switch to new profile
                    currentProfileName = targetProfile;

                    // Load settings from new profile (explicit switch - apply HDR/Resolution)
                    LoadProfileSettings(currentProfileName, isExplicitSwitch: true);
                }
                finally
                {
                    // Always clear the flag
                    isSwitchingProfile = false;
                }
            }
        }

        private string GetTargetProfileName()
        {
            bool hasGame = HasValidGame(currentGameName);
            bool perGameEnabled = PerGameProfileToggle?.IsOn ?? false;

            // Only consider DC (battery) when power supply is NotPresent (actually unplugged)
            // Inadequate means charger is connected but can't keep up - still treat as AC
            var powerSupplyStatus = PowerManager.PowerSupplyStatus;
            bool isOnAC = powerSupplyStatus != PowerSupplyStatus.NotPresent;

            // IMPORTANT: Never create profile names for invalid games
            // If per-game is enabled but no valid game, fall back to global profiles
            if (perGameEnabled && hasGame)
            {
                // Per-game profile - only if we have a VALID game name AND the profile
                // storage container already exists. This prevents switching to ghost profiles
                // for fuzzy-matched launcher names that were never explicitly created by the user
                // or LoadOrCreateGameProfiles(). Without this check, deferred events after
                // SwitchProfile can auto-save to a non-existent profile, creating it accidentally.
                var settings = ApplicationData.Current.LocalSettings;
                string candidateProfile;
                bool perGamePowerSourceSplit = GetPerGamePowerSourceProfileEnabled(currentGameName);
                if (perGamePowerSourceSplit)
                {
                    candidateProfile = isOnAC ? $"Game_{currentGameName}_AC" : $"Game_{currentGameName}_DC";
                }
                else
                {
                    candidateProfile = $"Game_{currentGameName}";
                }

                if (settings.Containers.ContainsKey($"Profile_{candidateProfile}"))
                {
                    Logger.Info($"Using per-game profile for: {currentGameName}");
                    return candidateProfile;
                }

                Logger.Warn($"Per-game toggle is ON but no saved profile exists for '{candidateProfile}', using global profile instead");
                // Fall through to global profile below
            }
            else if (perGameEnabled && !hasGame)
            {
                Logger.Warn($"Per-game toggle is ON but no valid game detected, using global profile instead");
            }

            // Global profiles (used when: no valid game, per-game disabled, or game profile doesn't exist yet)
            if (!GetGlobalPowerSourceProfileEnabled())
            {
                return "Global";
            }
            else
            {
                return isOnAC ? "AC" : "DC";
            }
        }

        private void SaveCurrentSettingsToProfile(string profileName)
        {
            // Guard against null profile name during XAML initialization
            if (string.IsNullOrEmpty(profileName))
            {
                return;
            }

            // Don't save during helper updates - prevents race conditions
            if (isApplyingHelperUpdate)
            {
                Logger.Debug($"Skipping profile save for {profileName} - isApplyingHelperUpdate is true");
                return;
            }

            // Don't save during initial sync - prevents stale widget values from overwriting
            // the helper's actual hardware state in the profile
            if (isInitialSync)
            {
                Logger.Debug($"Skipping profile save for {profileName} - isInitialSync is true");
                return;
            }

            // Don't save when Default Game Profile is active - prevents overwriting user's profile
            if (defaultGameProfileEnabled?.Value == true)
            {
                Logger.Debug($"Skipping profile save for {profileName} - Default Game Profile is active");
                return;
            }

            // Don't save during DGP restoration - toggle handlers would save wrong values during state restore
            if (isRestoringFromDefaultProfile)
            {
                Logger.Debug($"Skipping profile save for {profileName} - restoring from Default Game Profile");
                return;
            }

            // Never save to "No game detected" profile (case-insensitive check)
            if (profileName.IndexOf("No game detected", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Logger.Warn($"Attempted to save to invalid profile name: {profileName}, skipping");
                return;
            }

            // Don't auto-save to game profiles that haven't been explicitly created.
            // Only LoadOrCreateGameProfiles() should create new game profile storage containers.
            // Without this guard, deferred UI events after SwitchProfile can accidentally create
            // ghost profiles for fuzzy-matched launcher names (e.g., "Game_Hollow Knight: Silksong").
            if (profileName.StartsWith("Game_"))
            {
                var settings2 = ApplicationData.Current.LocalSettings;
                if (!settings2.Containers.ContainsKey($"Profile_{profileName}"))
                {
                    Logger.Warn($"Skipping auto-save to non-existent game profile '{profileName}' (profile must be created via LoadOrCreateGameProfiles first)");
                    return;
                }
            }

            var profile = GetProfile(profileName);

            // Save only enabled settings
            if (SaveTDP && TDPSlider != null && TDPModeComboBox != null)
            {
                int selectedIndex = TDPModeComboBox.SelectedIndex;
                if (selectedIndex >= 0)
                {
                    // Always save the TDP mode index for proper restoration with custom presets
                    profile.TDPModeIndex = selectedIndex;

                    // Get the Legion mode value from the current selection
                    int legionModeValue = GetCurrentPresetLegionMode();
                    profile.LegionPerformanceMode = legionModeValue;

                    // Only save TDP slider value if in actual Custom mode (slider-controlled)
                    // Not for user-made presets (which also have legionModeValue=255 but aren't Custom)
                    if (IsCustomTdpModeSelected())
                    {
                        profile.TDP = TDPSlider.Value;
                        // Also update savedCustomTDP for consistency
                        savedCustomTDP = TDPSlider.Value;
                    }
                    // For preset modes (including user-made), keep the profile's existing TDP value
                }
            }
            if (SaveCPUBoost && CPUBoostToggle != null)
            {
                profile.CPUBoost = CPUBoostToggle.IsOn;
            }
            if (SaveCPUEPP && CPUEPPSlider != null)
            {
                profile.CPUEPP = CPUEPPSlider.Value;
            }
            if (SaveAMDFeatures && AMDFluidMotionFrameToggle != null)
            {
                profile.FluidMotionFrames = AMDFluidMotionFrameToggle.IsOn;
                profile.RadeonSuperResolution = AMDRadeonSuperResolutionToggle.IsOn;
                profile.RadeonSuperResolutionSharpness = AMDRadeonSuperResolutionSharpnessSlider.Value;
                profile.ImageSharpening = AMDImageSharpeningToggle.IsOn;
                profile.ImageSharpeningSharpness = AMDImageSharpeningSlider.Value;
                profile.RadeonAntiLag = AMDRadeonAntiLagToggle.IsOn;
                profile.RadeonBoost = AMDRadeonBoostToggle.IsOn;
                profile.RadeonBoostResolution = AMDRadeonBoostResolutionSlider.Value;
                profile.RadeonChill = AMDRadeonChillToggle.IsOn;
                profile.RadeonChillMinFPS = AMDRadeonChillMinFPSSlider.Value;
                profile.RadeonChillMaxFPS = AMDRadeonChillMaxFPSSlider.Value;
            }
            if (SaveFPSLimit && FPSLimitToggle != null && FPSLimitSlider != null)
            {
                profile.FPSLimitEnabled = FPSLimitToggle.IsOn;
                profile.FPSLimitValue = (int)FPSLimitSlider.Value;
            }
            if (SaveAutoTDP && AutoTDPToggle != null && AutoTDPTargetFPSSlider != null && AutoTDPMinSlider != null && AutoTDPMaxSlider != null)
            {
                profile.AutoTDPEnabled = AutoTDPToggle.IsOn;
                profile.AutoTDPTargetFPS = (int)AutoTDPTargetFPSSlider.Value;
                profile.AutoTDPMinTDP = (int)AutoTDPMinSlider.Value;
                profile.AutoTDPMaxTDP = (int)AutoTDPMaxSlider.Value;
                // Save the controller type (0=PID, 1=Q-Learning, 2=SARSA)
                profile.AutoTDPControllerType = AutoTDPControllerModeComboBox?.SelectedIndex ?? 0;
                // Also update deprecated field for backwards compatibility
                profile.AutoTDPUseMLMode = AutoTDPControllerModeComboBox?.SelectedIndex > 0;
            }
            if (SaveOSPowerMode && OSPowerModeComboBox != null)
            {
                profile.OSPowerMode = OSPowerModeComboBox.SelectedIndex;
            }
            // TDP Boost is always saved with TDP (they go together). Per-profile boost deltas
            // (SPPT / FPPT additions) replace the previous global helper settings — they live on
            // the same profile object so each game's boost aggressiveness is independent.
            if (SaveTDP && TDPBoostToggle != null)
            {
                profile.TDPBoostEnabled = TDPBoostToggle.IsOn;
                if (TDPBoostSPPTSlider != null)
                {
                    profile.TDPBoostSPPT = (int)Math.Round(TDPBoostSPPTSlider.Value);
                }
                if (TDPBoostFPPTSlider != null)
                {
                    profile.TDPBoostFPPT = (int)Math.Round(TDPBoostFPPTSlider.Value);
                }
            }
            // HDR
            if (SaveHDR)
            {
                profile.HDREnabled = HDRToggle?.IsOn ?? false;
            }
            // Resolution
            if (SaveResolution)
            {
                profile.Resolution = ResolutionComboBox?.SelectedItem?.ToString() ?? "";
            }
            // Refresh Rate
            if (SaveRefreshRate)
            {
                profile.RefreshRate = refreshRate?.Value;
            }
            // Sticky TDP
            if (SaveStickyTDP && StickyTDPToggle != null)
            {
                profile.StickyTDPEnabled = StickyTDPToggle.IsOn;
                profile.StickyTDPInterval = (int)(StickyTDPIntervalSlider?.Value ?? 5);
            }
            // Overlay Level
            if (SaveOverlayLevel && PerformanceOverlayComboBox != null)
            {
                profile.OverlayLevel = PerformanceOverlayComboBox.SelectedIndex;
            }
            // Persist to storage
            Logger.Info($"Saving profile {profileName}: TDP={profile.TDP}W");
            SaveProfileToStorage(profileName, profile);

            // Per-state profile edited → refresh the helper's AC/DC cache so it
            // applies the new values on transitions without the widget awake.
            // GetProfile returned the same instance SendPowerSourceProfileValues
            // reads, so the in-memory pair is already up to date; only the pipe
            // sync was missing (#94 item 3: AutoTDP edits in a game AC/DC
            // profile never reached the helper until the next reconnect).
            if (profileName == "AC" || profileName == "DC"
                || profileName.EndsWith("_AC") || profileName.EndsWith("_DC"))
            {
                SendPowerSourceProfileValuesToHelper();
            }

            // Update profile display
            UpdateProfileDisplay();
        }

        /// <summary>
        /// Fired whenever an AMD feature property's value changes, from EITHER direction: a
        /// genuine widget-side toggle click (already captured via SettingChanged ->
        /// SaveCurrentSettingsToProfile through the control's own Toggled event) or a helper-
        /// pushed update reflecting the real driver state (e.g. Anti-Lag/Boost/Chill changed
        /// externally via AMD Software: Adrenalin Edition, or a hardware read-back on connect).
        /// The helper-driven direction is deliberately skipped by SettingChanged's
        /// WidgetSliderProperty.HelperSyncCount guard, to avoid a startup BatchGet flood
        /// clobbering a freshly-loaded profile - but that also means the profile table's AMD row
        /// never learns about a driver-side change, and shows stale data (e.g. "Off") forever.
        /// Deferring one dispatcher tick lets the isApplyingHelperUpdate window close before
        /// SaveCurrentSettingsToProfile's own guards (isLoadingProfile/isSwitchingProfile/
        /// isInitialSync) decide whether it's safe to resync the active profile + redraw the table.
        /// </summary>
        private void AMDFeatureProperty_ChangedResyncProfile(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                SaveCurrentSettingsToProfile(currentProfileName);
            });
        }

        private void LoadProfileSettings(string profileName, bool isExplicitSwitch = false)
        {
            if (isLoadingProfile) return;
            isLoadingProfile = true;
            profileSwitchEpoch++; // Invalidate any deferred PropertyChanged callbacks queued before this switch

            try
            {
                var profile = GetProfile(profileName);

                // For Legion devices: check if we need to switch to Custom mode BEFORE sending any TDP-related settings
                // This prevents TDP/TDPBoost/EPP from being ignored when helper is still in preset mode
                bool legionNeedsModeChange = false;
                bool legionSwitchingToCustom = false;
                if (legionGoDetected?.Value == true && defaultGameProfileEnabled?.Value != true && !isInitialSync)
                {
                    int profileMode = profile.LegionPerformanceMode;
                    int modeIndex = GetProfileTDPModeIndex(profile);
                    if (modeIndex >= 0 && legionPerformanceMode?.Value != profileMode)
                    {
                        legionNeedsModeChange = true;
                        legionSwitchingToCustom = profileMode == 255;
                    }
                }

                // Apply only enabled settings to UI controls
                // Skip TDP loading when DGP is active - DGP controls TDP
                if (SaveTDP && defaultGameProfileEnabled?.Value != true)
                {
                    // Set IsUpdatingUI to prevent the slider's ValueChanged from starting debounce timer.
                    // Without this, the slider fires ValueChanged → debounce timer → sends stale LocalSettings
                    // value back to helper, corrupting the per-game profile.
                    if (tdp != null) tdp.IsUpdatingUI = true;
                    try
                    {
                        TDPSlider.Value = profile.TDP;
                    }
                    finally
                    {
                        if (tdp != null) tdp.IsUpdatingUI = false;
                    }
                    // Only initialize savedCustomTDP from profile's TDP value if the profile was saved in Custom mode
                    // Otherwise we'd be saving a preset's TDP value as the custom TDP value
                    if (profile.LegionPerformanceMode == 255)
                    {
                        savedCustomTDP = profile.TDP;
                        Logger.Debug($"Initialized savedCustomTDP from profile (Custom mode): {savedCustomTDP}W");
                    }

                    // For Legion devices: TDP value will be sent AFTER TDP mode is applied (see Legion-specific handling below)
                    // This prevents TDP from being ignored when switching from preset mode to Custom mode
                    // For non-Legion devices: send TDP value immediately
                    // Skip sending when helper triggered the profile switch (isApplyingHelperUpdate) —
                    // the helper already sent the correct TDP via pipe, don't overwrite with stale LocalSettings value.
                    // Also skip on isInitialSync (first LoadProfileSettings at widget construction time):
                    // helper is the source of truth for TDP across restarts (global.xml), and the widget
                    // may have a stale or default value from LocalSettings here — pushing it would clobber
                    // the helper's just-restored value. Issues #74 and #79 symptom: TDP resets to the
                    // hardcoded 15 W default after every reboot because widget-owned TDP is skipped in
                    // batch sync, then this push overwrites helper's global.xml value before the user
                    // even touches the slider.
                    if (legionGoDetected?.Value != true && !isApplyingHelperUpdate && !isInitialSync)
                    {
                        tdp?.ForceSetValue((int)profile.TDP);
                    }
                    // Update Sticky TDP target when loading profile
                    if (StickyTDPToggle?.IsOn == true)
                    {
                        targetTDPLimit = profile.TDP;
                        Logger.Info($"Sticky TDP target updated to: {targetTDPLimit}W (profile load)");
                    }
                    // Load TDP Boost toggle state from profile
                    // For Legion devices switching to Custom mode: defer sending to helper until mode change is applied
                    if (TDPBoostToggle != null)
                    {
                        TDPBoostToggle.IsOn = profile.TDPBoostEnabled;
                        if (!legionSwitchingToCustom && !isApplyingHelperUpdate)
                        {
                            tdpBoostEnabled?.SetValue(profile.TDPBoostEnabled);
                        }
                        Logger.Info($"TDP Boost loaded from profile: {profile.TDPBoostEnabled} (deferred={legionSwitchingToCustom})");
                    }
                    // Load per-profile TDP Boost deltas (SPPT / FPPT additions). Replaces the
                    // previous global helper settings, so each profile can pick its own boost
                    // aggressiveness. isLoadingTDPBoostSettings guards the slider's
                    // ValueChanged handler from re-saving the same value back to the profile.
                    isLoadingTDPBoostSettings = true;
                    try
                    {
                        if (TDPBoostSPPTSlider != null) TDPBoostSPPTSlider.Value = profile.TDPBoostSPPT;
                        if (TDPBoostFPPTSlider != null) TDPBoostFPPTSlider.Value = profile.TDPBoostFPPT;
                        if (TDPBoostSPPTValue != null) TDPBoostSPPTValue.Text = $"+{profile.TDPBoostSPPT}W";
                        if (TDPBoostFPPTValue != null) TDPBoostFPPTValue.Text = $"+{profile.TDPBoostFPPT}W";
                        UpdateTDPBoostCardActiveText();
                        if (!legionSwitchingToCustom && !isApplyingHelperUpdate)
                        {
                            tdpBoostSPPT?.SetValue(profile.TDPBoostSPPT);
                            tdpBoostFPPT?.SetValue(profile.TDPBoostFPPT);
                        }
                    }
                    finally { isLoadingTDPBoostSettings = false; }
                }
                if (SaveCPUBoost)
                {
                    CPUBoostToggle.IsOn = profile.CPUBoost;
                    // Send to helper explicitly — skip when helper triggered the switch
                    if (!isApplyingHelperUpdate)
                    {
                        cpuBoost?.SetValue(profile.CPUBoost);
                    }
                }
                if (SaveCPUEPP)
                {
                    // Set IsUpdatingUI to prevent EPP slider debounce timer
                    if (cpuEPP != null) cpuEPP.IsUpdatingUI = true;
                    try
                    {
                        CPUEPPSlider.Value = profile.CPUEPP;
                    }
                    finally
                    {
                        if (cpuEPP != null) cpuEPP.IsUpdatingUI = false;
                    }
                    // Send to helper explicitly (cast to int for property type)
                    // For Legion devices switching to Custom mode: defer sending to helper until mode change is applied
                    // Skip when helper triggered the switch
                    if (!legionSwitchingToCustom && !isApplyingHelperUpdate)
                    {
                        cpuEPP?.SetValue((int)profile.CPUEPP);
                    }
                }
                if (SaveAMDFeatures)
                {
                    // RSR and RIS are mutually exclusive - if both are enabled in profile, prefer RSR
                    bool rsrEnabled = profile.RadeonSuperResolution;
                    bool risEnabled = profile.ImageSharpening;
                    if (rsrEnabled && risEnabled)
                    {
                        Logger.Warn("Profile has both RSR and RIS enabled - disabling RIS (mutually exclusive)");
                        risEnabled = false;
                    }

                    // Chill is mutually exclusive with Anti-Lag and Boost - if Chill is enabled, disable the others
                    bool antiLagEnabled = profile.RadeonAntiLag;
                    bool boostEnabled = profile.RadeonBoost;
                    bool chillEnabled = profile.RadeonChill;
                    if (chillEnabled && (antiLagEnabled || boostEnabled))
                    {
                        Logger.Warn("Profile has Chill with Anti-Lag/Boost enabled - disabling Anti-Lag and Boost (mutually exclusive)");
                        antiLagEnabled = false;
                        boostEnabled = false;
                    }

                    AMDFluidMotionFrameToggle.IsOn = profile.FluidMotionFrames;
                    AMDRadeonSuperResolutionToggle.IsOn = rsrEnabled;
                    AMDRadeonSuperResolutionSharpnessSlider.Value = profile.RadeonSuperResolutionSharpness;
                    AMDImageSharpeningToggle.IsOn = risEnabled;
                    AMDImageSharpeningSlider.Value = profile.ImageSharpeningSharpness;
                    AMDRadeonAntiLagToggle.IsOn = antiLagEnabled;
                    AMDRadeonBoostToggle.IsOn = boostEnabled;
                    AMDRadeonBoostResolutionSlider.Value = profile.RadeonBoostResolution;
                    AMDRadeonChillToggle.IsOn = chillEnabled;
                    AMDRadeonChillMinFPSSlider.Value = profile.RadeonChillMinFPS;
                    AMDRadeonChillMaxFPSSlider.Value = profile.RadeonChillMaxFPS;
                    // Send to helper explicitly using ForceSetValue to ensure AMD driver state is synchronized
                    // even if the cached value appears unchanged (driver state may differ from cache)
                    // Send RIS first (to disable it if needed), then RSR
                    // Send Anti-Lag and Boost first (to disable them if needed), then Chill
                    amdFluidMotionFrameEnabled?.ForceSetValue(profile.FluidMotionFrames);
                    amdImageSharpeningEnabled?.ForceSetValue(risEnabled);
                    amdImageSharpeningSharpness?.ForceSetValue((int)profile.ImageSharpeningSharpness);
                    amdRadeonSuperResolutionEnabled?.ForceSetValue(rsrEnabled);
                    amdRadeonSuperResolutionSharpness?.ForceSetValue((int)profile.RadeonSuperResolutionSharpness);
                    amdRadeonAntiLagEnabled?.ForceSetValue(antiLagEnabled);
                    amdRadeonBoostEnabled?.ForceSetValue(boostEnabled);
                    amdRadeonBoostResolution?.ForceSetValue((int)profile.RadeonBoostResolution);
                    amdRadeonChillEnabled?.ForceSetValue(chillEnabled);
                    amdRadeonChillMinFPSProperty?.ForceSetValue((int)profile.RadeonChillMinFPS);
                    amdRadeonChillMaxFPSProperty?.ForceSetValue((int)profile.RadeonChillMaxFPS);
                }
                if (SaveFPSLimit)
                {
                    FPSLimitToggle.IsOn = profile.FPSLimitEnabled;
                    FPSLimitSlider.Value = profile.FPSLimitValue;
                    // Send to helper explicitly (toggle/slider handlers may be blocked by flags)
                    int fpsLimitValue = profile.FPSLimitEnabled ? profile.FPSLimitValue : 0;
                    fpsLimit?.SetValue(fpsLimitValue);
                }
                if (SaveAutoTDP)
                {
                    // Set loading flag to prevent toggled event from sending to helper
                    isLoadingAutoTDPSettings = true;
                    try
                    {
                        // For game profiles, use the helper's synced property value.
                        // The widget's profile may be stale (e.g., AutoTDP=false saved during game close
                        // when helper restored global values). The helper is the source of truth.
                        bool autoTDPState = profileName.StartsWith("Game_") && autoTDPEnabled != null
                            ? autoTDPEnabled.Value
                            : profile.AutoTDPEnabled;
                        AutoTDPToggle.IsOn = autoTDPState;
                        AutoTDPTargetFPSSlider.Value = profile.AutoTDPTargetFPS;
                        AutoTDPMinSlider.Value = profile.AutoTDPMinTDP;
                        AutoTDPMaxSlider.Value = profile.AutoTDPMaxTDP;
                        // Update text displays explicitly
                        if (AutoTDPTargetFPSValue != null)
                        {
                            AutoTDPTargetFPSValue.Text = $"{profile.AutoTDPTargetFPS} FPS";
                        }
                        if (AutoTDPMinValue != null)
                        {
                            AutoTDPMinValue.Text = $"{profile.AutoTDPMinTDP}W";
                        }
                        if (AutoTDPMaxValue != null)
                        {
                            AutoTDPMaxValue.Text = $"{profile.AutoTDPMaxTDP}W";
                        }
                        // Update controller type selection (0=PID, 1=Q-Learning, 2=SARSA)
                        if (AutoTDPControllerModeComboBox != null)
                        {
                            AutoTDPControllerModeComboBox.SelectedIndex = profile.AutoTDPControllerType;
                            UpdateAutoTDPMLInfoPanelVisibility();
                        }
                        // NOTE: Do NOT send to helper here - helper is source of truth for profile values
                        // Helper will apply profile values and sync back to widget
                    }
                    finally
                    {
                        isLoadingAutoTDPSettings = false;
                    }
                }
                if (SaveOSPowerMode)
                {
                    isLoadingOSPowerMode = true;
                    try
                    {
                        OSPowerModeComboBox.SelectedIndex = profile.OSPowerMode;
                        if (profile.OSPowerMode >= 0 && profile.OSPowerMode < OSPowerModeNames.Length)
                        {
                            OSPowerModeValue.Text = OSPowerModeNames[profile.OSPowerMode];
                        }
                        // Send to helper explicitly
                        osPowerMode?.SetValue(profile.OSPowerMode);
                    }
                    finally
                    {
                        isLoadingOSPowerMode = false;
                    }
                }
                // Legion Performance Mode handling
                // Skip TDP mode loading when:
                // - Default Game Profile is active (DGP controls TDP)
                // - Initial sync is in progress (let helper's value take precedence - DGP state not yet known)
                Logger.Info($"LoadProfileSettings Legion check: legionGoDetected={legionGoDetected?.Value}, LegionPerformanceModeComboBox={LegionPerformanceModeComboBox != null}, TDPModeComboBox={TDPModeComboBox != null}, defaultGameProfileEnabled={defaultGameProfileEnabled?.Value}, isInitialSync={isInitialSync}");
                if (legionGoDetected?.Value == true && LegionPerformanceModeComboBox != null && TDPModeComboBox != null && defaultGameProfileEnabled?.Value != true && !isInitialSync)
                {
                    int[] modeValues = { 1, 2, 3, 255 }; // Quiet, Balanced, Performance, Custom

                    if (profileName.StartsWith("Game_"))
                    {
                        // Loading a game profile: save the source profile's TDP mode (not the current UI state)
                        // This ensures we restore to the intended profile mode when the game closes
                        if (savedLegionPerformanceMode < 0)
                        {
                            // Save from the correct source profile based on Power Source Profile toggle
                            if (GetGlobalPowerSourceProfileEnabled())
                            {
                                var powerSupplyStatus = PowerManager.PowerSupplyStatus;
                                bool isOnAC = powerSupplyStatus != PowerSupplyStatus.NotPresent;
                                savedLegionPerformanceMode = isOnAC ? acProfile.LegionPerformanceMode : dcProfile.LegionPerformanceMode;
                                Logger.Info($"Saved Legion Performance Mode from {(isOnAC ? "AC" : "DC")} profile: {GetLegionModeShortName(savedLegionPerformanceMode)} ({savedLegionPerformanceMode}) before game profile");
                            }
                            else
                            {
                                savedLegionPerformanceMode = globalProfile.LegionPerformanceMode;
                                Logger.Info($"Saved Legion Performance Mode from global profile: {GetLegionModeShortName(savedLegionPerformanceMode)} ({savedLegionPerformanceMode}) before game profile");
                            }
                        }

                        // Apply game profile's TDP Mode if SaveTDP is enabled
                        if (SaveTDP)
                        {
                            int profileMode = profile.LegionPerformanceMode;
                            int modeIndex = GetProfileTDPModeIndex(profile);

                            // For game profiles, the helper manages LegionPerformanceMode in PerGameProfile_PropertyChanged:
                            // it applies the saved mode from the helper's profile (or Custom for new profiles).
                            // Don't send mode to helper here — that would override the helper's mode and cause
                            // "switches to Custom then immediately back" when profiles have stale/corrupted modes.
                            // Just update lastTDPModeIndex so the handler doesn't treat the helper's mode update as a "change".
                            if (modeIndex >= 0)
                                lastTDPModeIndex = modeIndex;
                            Logger.Info($"Game profile: LegionPerformanceMode deferred to helper. Widget profile has: {GetLegionModeShortName(profileMode)} ({profileMode}) for {profileName}");
                        }
                        else
                        {
                            // SaveTDP disabled: let helper manage mode (it defaults to Custom for new profiles)
                            lastTDPModeIndex = 3; // Custom mode index
                            Logger.Info($"SaveTDP disabled - deferring mode to helper for game profile: {profileName}");
                        }
                    }
                    else if (savedLegionPerformanceMode >= 0)
                    {
                        // Loading Global/AC/DC profile and we have a saved mode to restore
                        int index = Array.IndexOf(modeValues, savedLegionPerformanceMode);
                        bool modeChanged = false;
                        if (index >= 0 && (legionPerformanceMode.Value != savedLegionPerformanceMode || TDPModeComboBox.SelectedIndex != index))
                        {
                            if (LegionPerformanceModeComboBox.SelectedIndex != index)
                                LegionPerformanceModeComboBox.SelectedIndex = index;
                            if (TDPModeComboBox.SelectedIndex != index)
                            {
                                lastTDPModeIndex = index;
                                TDPModeComboBox.SelectedIndex = index;
                            }
                            legionPerformanceMode?.ForceSetValue(savedLegionPerformanceMode);
                            modeChanged = true;
                            Logger.Info($"Restored Legion Performance Mode: {GetLegionModeShortName(savedLegionPerformanceMode)} ({savedLegionPerformanceMode}) after game closed");
                        }
                        // Also restore the TDP slider to the profile's TDP value
                        // This is needed because the slider may still show the game profile's TDP
                        if (SaveTDP && TDPSlider.Value != profile.TDP)
                        {
                            TDPSlider.Value = profile.TDP;
                            Logger.Info($"Restored TDP slider to {profile.TDP}W after game closed");
                        }
                        // If restoring to Custom mode (255), send deferred settings after mode change
                        if (SaveTDP && savedLegionPerformanceMode == 255)
                        {
                            if (modeChanged)
                            {
                                _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, async () =>
                                {
                                    await Task.Delay(500); // Allow mode change to propagate to helper
                                    // Send deferred TDP-related settings
                                    if (TDPBoostToggle != null)
                                    {
                                        tdpBoostEnabled?.ForceSetValue(profile.TDPBoostEnabled);
                                    }
                                    if (SaveCPUEPP)
                                    {
                                        cpuEPP?.ForceSetValue((int)profile.CPUEPP);
                                    }
                                    tdp?.ForceSetValue((int)profile.TDP);
                                    Logger.Info($"Restored TDP settings after mode change: TDP={profile.TDP}W, Boost={profile.TDPBoostEnabled}, EPP={profile.CPUEPP}");
                                });
                            }
                            else
                            {
                                tdp?.ForceSetValue((int)profile.TDP);
                                Logger.Info($"Restored TDP value (already in Custom mode): {profile.TDP}W");
                            }
                        }
                        savedLegionPerformanceMode = -1; // Clear saved mode
                    }
                    else if (SaveTDP)
                    {
                        // Loading Global profile directly (not returning from game) - apply profile's TDP Mode
                        int profileMode = profile.LegionPerformanceMode;
                        int modeIndex = GetProfileTDPModeIndex(profile);
                        Logger.Info($"LoadProfileSettings: profileMode={profileMode}, modeIndex={modeIndex}, legionPerformanceMode.Value={legionPerformanceMode?.Value}, TDPModeComboBox.SelectedIndex={TDPModeComboBox?.SelectedIndex}");

                        // Always update UI to match profile when loading Global profile
                        // The internal value may already match (set by helper) but UI may be stale
                        bool modeChanged = false;
                        if (modeIndex >= 0)
                        {
                            // Update lastTDPModeIndex FIRST to prevent TDPModeComboBox_SelectionChanged
                            // from treating the profile load as a user-initiated change
                            lastTDPModeIndex = modeIndex;

                            modeChanged = legionPerformanceMode.Value != profileMode;
                            if (LegionPerformanceModeComboBox.SelectedIndex != modeIndex)
                                LegionPerformanceModeComboBox.SelectedIndex = modeIndex;
                            if (TDPModeComboBox.SelectedIndex != modeIndex)
                                TDPModeComboBox.SelectedIndex = modeIndex;
                            legionPerformanceMode?.ForceSetValue(profileMode);
                            Logger.Info($"Applied profile TDP Mode: {GetLegionModeShortName(profileMode)} ({profileMode}) for {profileName}");

                            // If Custom mode (255), send deferred settings after mode change
                            if (profileMode == 255)
                            {
                                if (modeChanged)
                                {
                                    _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, async () =>
                                    {
                                        await Task.Delay(500); // Allow mode change to propagate to helper
                                        // Send deferred TDP-related settings
                                        if (TDPBoostToggle != null)
                                        {
                                            tdpBoostEnabled?.ForceSetValue(profile.TDPBoostEnabled);
                                        }
                                        if (SaveCPUEPP)
                                        {
                                            cpuEPP?.ForceSetValue((int)profile.CPUEPP);
                                        }
                                        tdp?.ForceSetValue((int)profile.TDP);
                                        Logger.Info($"Applied profile TDP settings after mode change: TDP={profile.TDP}W, Boost={profile.TDPBoostEnabled}, EPP={profile.CPUEPP} for {profileName}");
                                    });
                                }
                                else
                                {
                                    tdp?.ForceSetValue((int)profile.TDP);
                                    Logger.Info($"Applied profile TDP value (already in Custom mode): {profile.TDP}W for {profileName}");
                                }
                            }
                        }
                    }

                    // Update TDP slider enabled state based on mode
                    // Skip for game profiles: helper manages TDP mode, and the ComboBox hasn't been
                    // updated yet (it still shows the old global mode). Running UpdateTDPSliderEnabledState
                    // now would see the wrong mode and send incorrect values to the helper (e.g.,
                    // AutoTDP=false when the game profile has AutoTDP=true, because the old mode is
                    // non-Custom). UpdateTDPSliderEnabledState runs naturally when the helper sends its
                    // mode via pipe → ComboBox updates → TDPModeComboBox_SelectionChanged.
                    if (!profileName.StartsWith("Game_"))
                    {
                        UpdateTDPSliderEnabledState();
                    }
                }
                // Generic device TDP Mode handling
                else if (legionGoDetected?.Value != true && TDPModeComboBox != null && defaultGameProfileEnabled?.Value != true && !isInitialSync)
                {
                    // Load TDP Mode from profile for generic devices
                    int profileMode = profile.LegionPerformanceMode;
                    int modeIndex = GetProfileTDPModeIndex(profile); // Already defaults to Balanced if not found

                    // Always sync lastTDPModeIndex to match the profile's mode.
                    // Without this, lastTDPModeIndex retains a stale value from the previous
                    // user session, causing TDPModeComboBox_SelectionChanged to skip the first
                    // mode change (selectedIndex == lastTDPModeIndex early return).
                    if (SaveTDP)
                    {
                        lastTDPModeIndex = modeIndex;

                        if (TDPModeComboBox.SelectedIndex != modeIndex)
                        {
                            TDPModeComboBox.SelectedIndex = modeIndex;
                            Logger.Info($"Applied generic device TDP Mode: index {modeIndex} (mode {profileMode}) for {profileName}");

                            // For Custom mode, the TDP slider value was already set above
                            // For preset modes, apply the preset TDP value
                            if (profileMode != 255)
                            {
                                int[] genericTDPValues = { 8, 15, 25 }; // Quiet, Balanced, Performance TDP values
                                if (modeIndex >= 0 && modeIndex < genericTDPValues.Length)
                                {
                                    int presetTDP = genericTDPValues[modeIndex];
                                    TDPSlider.Value = presetTDP;
                                    tdp?.ForceSetValue(presetTDP);
                                    Logger.Info($"Applied generic device preset TDP: {presetTDP}W");
                                }
                            }
                        }
                    }

                    // Update TDP slider enabled state based on mode
                    UpdateTDPSliderEnabledState();
                }

                // HDR
                if (SaveHDR)
                {
                    // Only apply HDR if:
                    // 1. This is an explicit profile switch (game detected/closed), OR
                    // 2. No game is currently running (returning to desktop)
                    bool shouldApplyHDR = isExplicitSwitch || !HasValidGame(currentGameName);

                    if (shouldApplyHDR)
                    {
                        if (HDRToggle != null && hdrSupported?.Value == true)
                        {
                            HDRToggle.IsOn = profile.HDREnabled;
                            hdrEnabled?.SetValue(profile.HDREnabled);
                        }
                    }
                    else
                    {
                        Logger.Info($"Skipping HDR application - game is running and not an explicit switch");
                    }
                }

                // Resolution
                if (SaveResolution)
                {
                    // Only apply Resolution if:
                    // 1. This is an explicit profile switch (game detected/closed), OR
                    // 2. No game is currently running (returning to desktop)
                    bool shouldApplyResolution = isExplicitSwitch || !HasValidGame(currentGameName);

                    if (shouldApplyResolution)
                    {
                        if (ResolutionComboBox != null && !string.IsNullOrEmpty(profile.Resolution))
                        {
                            // Find and select matching resolution
                            for (int i = 0; i < ResolutionComboBox.Items.Count; i++)
                            {
                                if (ResolutionComboBox.Items[i]?.ToString() == profile.Resolution)
                                {
                                    ResolutionComboBox.SelectedIndex = i;
                                    resolution?.SetValue(profile.Resolution);
                                    break;
                                }
                            }
                        }
                    }
                    else
                    {
                        Logger.Info($"Skipping Resolution application - game is running and not an explicit switch");
                    }
                }

                // Refresh Rate
                if (SaveRefreshRate)
                {
                    // Only apply Refresh Rate if:
                    // 1. This is an explicit profile switch (game detected/closed), OR
                    // 2. No game is currently running (returning to desktop)
                    bool shouldApplyRefreshRate = isExplicitSwitch || !HasValidGame(currentGameName);

                    if (shouldApplyRefreshRate)
                    {
                        if (RefreshRatesComboBox != null && profile.RefreshRate.HasValue)
                        {
                            // Find and select matching refresh rate
                            for (int i = 0; i < RefreshRatesComboBox.Items.Count; i++)
                            {
                                if (RefreshRatesComboBox.Items[i] is int rate && rate == profile.RefreshRate.Value)
                                {
                                    RefreshRatesComboBox.SelectedIndex = i;
                                    refreshRate?.SetValue(profile.RefreshRate.Value);
                                    break;
                                }
                            }
                        }
                    }
                    else
                    {
                        Logger.Info($"Skipping RefreshRate application - game is running and not an explicit switch");
                    }
                }

                // Sticky TDP
                if (SaveStickyTDP && StickyTDPToggle != null)
                {
                    isLoadingStickyTDPSettings = true;
                    try
                    {
                        StickyTDPToggle.IsOn = profile.StickyTDPEnabled;
                        if (StickyTDPIntervalSlider != null)
                        {
                            StickyTDPIntervalSlider.Value = profile.StickyTDPInterval;
                            stickyTDPCheckIntervalSeconds = profile.StickyTDPInterval;
                        }
                        if (StickyTDPIntervalValue != null)
                        {
                            StickyTDPIntervalValue.Text = $"{profile.StickyTDPInterval}s";
                        }
                        // Update timer state based on profile
                        if (profile.StickyTDPEnabled)
                        {
                            targetTDPLimit = profile.TDP;
                            StartStickyTDPTimer();
                        }
                        else
                        {
                            StopStickyTDPTimer();
                        }
                    }
                    finally
                    {
                        isLoadingStickyTDPSettings = false;
                    }
                }

                // Overlay Level
                // [bug fix] Unlike TDP/CPUBoost/CPUEPP/CPUState above, this block had no
                // isApplyingHelperUpdate/isInitialSync guard - PerformanceOverlayComboBox is bound
                // to PerformanceOverlaySlider, which the "osd" property sends to the helper on
                // change (OSDProperty ctor: new OSDProperty(0, PerformanceOverlaySlider, this)).
                // So a helper-driven switch or cold start could push this profile's stale
                // LocalSettings OverlayLevel over the helper's authoritative (global, per slice 1)
                // OSD level. OverlayLevel itself is out of scope for a full RouteProfileSave
                // migration (see win32-2.0-migration.md - no clean per-game helper schema for the
                // AMD-overlay-cycling half of this feature), so just add the same guard TDP uses.
                if (SaveOverlayLevel && PerformanceOverlayComboBox != null && !isApplyingHelperUpdate && !isInitialSync)
                {
                    int level = profile.OverlayLevel;
                    if (level >= 0 && level < PerformanceOverlayComboBox.Items.Count)
                    {
                        PerformanceOverlayComboBox.SelectedIndex = level;
                        // The SelectionChanged handler will update PerformanceOverlaySlider and send to system
                    }
                }

                // Update profile display to show correct TDP mode in Profiles tab
                UpdateProfileDisplay();

                // Safety check: If AutoTDP is enabled but we're not in Custom mode, switch to Custom mode
                // This handles profiles that were saved with incorrect mode values before the fix.
                // Skip for game profiles: helper manages TDP mode for game profiles, and the toggle
                // may show true from autoTDPEnabled.Value which belongs to a DIFFERENT game's profile.
                if (SaveAutoTDP && AutoTDPToggle?.IsOn == true && legionGoDetected?.Value == true && !profileName.StartsWith("Game_"))
                {
                    int customIndex = GetCustomTdpModeIndex();
                    if (TDPModeComboBox != null && !IsCustomTdpModeSelected())
                    {
                        Logger.Info($"AutoTDP enabled but not in Custom mode - fixing mode to Custom");
                        isUpdatingTDPMode = true;
                        try
                        {
                            lastTDPModeIndex = customIndex;
                            TDPModeComboBox.SelectedIndex = customIndex;
                            if (LegionPerformanceModeComboBox != null)
                                LegionPerformanceModeComboBox.SelectedIndex = customIndex;
                            legionPerformanceMode?.SetValue(255);
                            UpdateTDPSliderEnabledState();
                        }
                        finally
                        {
                            isUpdatingTDPMode = false;
                        }
                    }
                }
            }
            finally
            {
                isLoadingProfile = false;
            }
        }

        private PerformanceProfile GetProfile(string profileName)
        {
            // Never return a game profile for invalid game names (case-insensitive check)
            if (profileName.IndexOf("No game detected", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Logger.Warn($"Attempted to get invalid profile: {profileName}, returning global profile");
                return globalProfile;
            }

            // Handle game profiles
            if (profileName.StartsWith("Game_"))
            {
                if (profileName.EndsWith("_AC"))
                    return gameACProfile;
                else if (profileName.EndsWith("_DC"))
                    return gameDCProfile;
                else
                    return gameProfile;
            }

            // Handle global profiles
            switch (profileName)
            {
                case "AC": return acProfile;
                case "DC": return dcProfile;
                default: return globalProfile;
            }
        }

    }
}
