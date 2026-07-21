using NLog;
using Shared.Constants;
using Shared.Data;
using Shared.IPC;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.System;
using Windows.UI.Input.Preview.Injection;
using XboxGamingBarHelper.AMD;
using XboxGamingBarHelper.Core;
using XboxGamingBarHelper.ControllerEmulation;
using XboxGamingBarHelper.Devices.Libraries.GPD;
using XboxGamingBarHelper.Devices.Libraries.Legion;
using XboxGamingBarHelper.LosslessScaling;
using XboxGamingBarHelper.OnScreenDisplay;
using XboxGamingBarHelper.Performance;
using XboxGamingBarHelper.Power;
using XboxGamingBarHelper.Profile;
using XboxGamingBarHelper.RTSS;
using XboxGamingBarHelper.Settings;
using XboxGamingBarHelper.Systems;
using XboxGamingBarHelper.AutoTDP;
using XboxGamingBarHelper.DefaultGameProfiles;
using XboxGamingBarHelper.Labs;
using Shared.Enums;

namespace XboxGamingBarHelper
{
    internal partial class Program
    {
        // Snapshot of the widget's Profiles-tab save checkboxes. Defaults match the widget's
        // initial-field defaults so any handler that runs before the widget has pushed flags
        // falls back to the same behavior the UI shows to the user.
        // - true  => setting is captured per-game; mid-session writes land in CurrentProfile.
        // - false => setting is global; mid-session writes land in GlobalProfile regardless of
        //            whether a game is active, so reboots don't pull stale per-game values back.
        private static class ProfileSaveFlagsState
        {
            public static bool TDP = true;
            public static bool CPUBoost = true;
            public static bool CPUEPP = true;
            public static bool AMDFeatures = false;
            public static bool FPSLimit = true;
            public static bool AutoTDP = true;
            public static bool OSPowerMode = true;
            public static bool HDR = false;
            public static bool Resolution = false;
            public static bool RefreshRate = false;
            public static bool StickyTDP = false;
            public static bool OverlayLevel = false;
            public static bool NintendoLayout = false;
            public static bool Vibration = false;
            public static bool Lighting = false;
            public static bool ButtonMappings = false;
        }

        internal static void ApplyProfileSaveFlags(string configJson)
        {
            try
            {
                var cfg = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, bool>>(configJson);
                if (cfg == null) return;
                if (cfg.TryGetValue("TDP", out var v1)) ProfileSaveFlagsState.TDP = v1;
                if (cfg.TryGetValue("CPUBoost", out var v2)) ProfileSaveFlagsState.CPUBoost = v2;
                if (cfg.TryGetValue("CPUEPP", out var v3)) ProfileSaveFlagsState.CPUEPP = v3;
                if (cfg.TryGetValue("AMDFeatures", out var v5)) ProfileSaveFlagsState.AMDFeatures = v5;
                if (cfg.TryGetValue("FPSLimit", out var v6)) ProfileSaveFlagsState.FPSLimit = v6;
                if (cfg.TryGetValue("AutoTDP", out var v7)) ProfileSaveFlagsState.AutoTDP = v7;
                if (cfg.TryGetValue("OSPowerMode", out var v8)) ProfileSaveFlagsState.OSPowerMode = v8;
                if (cfg.TryGetValue("HDR", out var v9)) ProfileSaveFlagsState.HDR = v9;
                if (cfg.TryGetValue("Resolution", out var v10)) ProfileSaveFlagsState.Resolution = v10;
                if (cfg.TryGetValue("RefreshRate", out var v11)) ProfileSaveFlagsState.RefreshRate = v11;
                if (cfg.TryGetValue("StickyTDP", out var v12)) ProfileSaveFlagsState.StickyTDP = v12;
                if (cfg.TryGetValue("OverlayLevel", out var v13)) ProfileSaveFlagsState.OverlayLevel = v13;
                if (cfg.TryGetValue("NintendoLayout", out var v15)) ProfileSaveFlagsState.NintendoLayout = v15;
                if (cfg.TryGetValue("Vibration", out var v16)) ProfileSaveFlagsState.Vibration = v16;
                if (cfg.TryGetValue("Lighting", out var v17)) ProfileSaveFlagsState.Lighting = v17;
                if (cfg.TryGetValue("ButtonMappings", out var v18)) ProfileSaveFlagsState.ButtonMappings = v18;
                Logger.Info("Applied ProfileSaveFlags from widget "
                    + $"(TDP={ProfileSaveFlagsState.TDP}, CPUBoost={ProfileSaveFlagsState.CPUBoost}, "
                    + $"CPUEPP={ProfileSaveFlagsState.CPUEPP}, "
                    + $"AutoTDP={ProfileSaveFlagsState.AutoTDP}, NintendoLayout={ProfileSaveFlagsState.NintendoLayout}, "
                    + $"Vibration={ProfileSaveFlagsState.Vibration}, Lighting={ProfileSaveFlagsState.Lighting}, "
                    + $"ButtonMappings={ProfileSaveFlagsState.ButtonMappings})");
            }
            catch (Exception ex)
            {
                Logger.Error($"ApplyProfileSaveFlags: {ex.Message}");
            }
        }

        // Routes a setting save to CurrentProfile (per-game capture) when saveToProfile is true,
        // else to GlobalProfile (treat as device-wide). Caller supplies a setter action for each
        // target; the target's own setter handles the equality check and debounced Save().
        private static void RouteProfileSave(bool saveToProfile, string settingName,
            Action<Profile.GameProfileProperty> onCurrent, Action<Shared.Data.GameProfile> onGlobal)
        {
            if (saveToProfile)
            {
                Logger.Info($"Saving {settingName} to profile {profileManager.CurrentProfile.GameId.Name}");
                onCurrent(profileManager.CurrentProfile);
            }
            else
            {
                Logger.Info($"Saving {settingName} to global (per-game capture disabled)");
                // [2.0 fix] The old code passed GlobalProfile (a mutable STRUCT) by value to the
                // onGlobal lambda, so `glo => glo.X = value` only mutated a throwaway copy - the
                // save was silently lost (proven via global.xml: LegionVibration/Light/gyro stayed
                // xsi:nil while deadzones, which route through CurrentProfile, persisted). This is a
                // pre-existing §29 bug, masked until the widget stopped owning these settings.
                // When no game is active, CurrentProfile IS the global profile (a reference-type
                // GameProfileProperty that persists correctly) - route through it. When a game IS
                // active, mutate the GlobalProfile struct field and write it back.
                if (profileManager.CurrentProfile.IsGlobalProfile)
                {
                    onCurrent(profileManager.CurrentProfile);
                }
                else
                {
                    var glob = profileManager.GlobalProfile;
                    onGlobal(glob);
                    profileManager.GlobalProfile = glob;
                }
            }
        }

        private static void AutoTDPSetting_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            // Skip during profile application to prevent cross-contamination
            if (isApplyingProfile)
            {
                Logger.Debug("Skipping AutoTDPSetting_PropertyChanged - already applying profile");
                return;
            }

            // Skip stale widget messages during cooldown after profile switch
            if (IsInProfileSwitchCooldown())
            {
                Logger.Debug("Skipping AutoTDPSetting_PropertyChanged - in profile switch cooldown");
                return;
            }

            if (profileManager?.CurrentProfile == null || autoTDPManager == null)
                return;

            // All AutoTDP* settings share a single flag (ProfileSaveAutoTDP). When it's false the
            // writes land in GlobalProfile so a disabled toggle in-game doesn't sit on top of a
            // stale enabled-True in GlobalProfile that then resurfaces on reboot.
            bool saveToProfile = ProfileSaveFlagsState.AutoTDP;

            if (sender == autoTDPManager.Enabled)
            {
                RouteProfileSave(saveToProfile, "AutoTDPEnabled",
                    cur => cur.AutoTDPEnabled = autoTDPManager.Enabled.Value,
                    glo => glo.AutoTDPEnabled = autoTDPManager.Enabled.Value);
            }
            else if (sender == autoTDPManager.TargetFPS)
            {
                RouteProfileSave(saveToProfile, "AutoTDPTargetFPS",
                    cur => cur.AutoTDPTargetFPS = autoTDPManager.TargetFPS.Value,
                    glo => glo.AutoTDPTargetFPS = autoTDPManager.TargetFPS.Value);
            }
            else if (sender == autoTDPManager.MinTDP)
            {
                RouteProfileSave(saveToProfile, "AutoTDPMinTDP",
                    cur => cur.AutoTDPMinTDP = autoTDPManager.MinTDP.Value,
                    glo => glo.AutoTDPMinTDP = autoTDPManager.MinTDP.Value);
            }
            else if (sender == autoTDPManager.MaxTDP)
            {
                RouteProfileSave(saveToProfile, "AutoTDPMaxTDP",
                    cur => cur.AutoTDPMaxTDP = autoTDPManager.MaxTDP.Value,
                    glo => glo.AutoTDPMaxTDP = autoTDPManager.MaxTDP.Value);
            }
            else if (sender == autoTDPManager.UseMLMode)
            {
                // Legacy: sync UseMLMode to profile for backwards compatibility
                RouteProfileSave(saveToProfile, "AutoTDPUseMLMode",
                    cur => cur.AutoTDPUseMLMode = autoTDPManager.UseMLMode.Value,
                    glo => glo.AutoTDPUseMLMode = autoTDPManager.UseMLMode.Value);
            }
            else if (sender == autoTDPManager.ControllerType)
            {
                RouteProfileSave(saveToProfile, "AutoTDPControllerType",
                    cur => { cur.AutoTDPControllerType = autoTDPManager.ControllerType.Value;
                             cur.AutoTDPUseMLMode = autoTDPManager.ControllerType.Value > 0; },
                    glo => { glo.AutoTDPControllerType = autoTDPManager.ControllerType.Value;
                             glo.AutoTDPUseMLMode = autoTDPManager.ControllerType.Value > 0; });
            }
            else if (sender == autoTDPManager.PauseWhenUnfocused)
            {
                RouteProfileSave(saveToProfile, "AutoTDPPauseWhenUnfocused",
                    cur => cur.AutoTDPPauseWhenUnfocused = autoTDPManager.PauseWhenUnfocused.Value,
                    glo => glo.AutoTDPPauseWhenUnfocused = autoTDPManager.PauseWhenUnfocused.Value);
            }
        }

        private static void ApplyLegionControllerSettingsFromProfile()
        {
            var profile = profileManager.CurrentProfile;
            var profileName = profile.GameId.Name;

            Logger.Info($"Applying Legion controller settings from profile: {profileName}");

            // Button mappings - skip empty/null fields (not configured in profile).
            // An explicit disabled mapping like {"Type":0,"GamepadAction":0,...} MUST be
            // applied so the hardware clear command is sent; otherwise buttons like Desktop
            // keep their hardware default (Xbox) even though the UI shows "Disabled".
            if (!string.IsNullOrEmpty(profile.LegionButtonY1))
            {
                Logger.Debug($"Applying LegionButtonY1: {profile.LegionButtonY1}");
                legionManager.LegionButtonY1.SetValue(profile.LegionButtonY1);
            }
            if (!string.IsNullOrEmpty(profile.LegionButtonY2))
            {
                Logger.Debug($"Applying LegionButtonY2: {profile.LegionButtonY2}");
                legionManager.LegionButtonY2.SetValue(profile.LegionButtonY2);
            }
            if (!string.IsNullOrEmpty(profile.LegionButtonY3))
            {
                Logger.Debug($"Applying LegionButtonY3: {profile.LegionButtonY3}");
                legionManager.LegionButtonY3.SetValue(profile.LegionButtonY3);
            }
            if (!string.IsNullOrEmpty(profile.LegionButtonM1))
            {
                Logger.Debug($"Applying LegionButtonM1: {profile.LegionButtonM1}");
                legionManager.LegionButtonM1.SetValue(profile.LegionButtonM1);
            }
            if (!string.IsNullOrEmpty(profile.LegionButtonM2))
            {
                Logger.Debug($"Applying LegionButtonM2: {profile.LegionButtonM2}");
                legionManager.LegionButtonM2.SetValue(profile.LegionButtonM2);
            }
            if (!string.IsNullOrEmpty(profile.LegionButtonM3))
            {
                Logger.Debug($"Applying LegionButtonM3: {profile.LegionButtonM3}");
                legionManager.LegionButtonM3.SetValue(profile.LegionButtonM3);
            }
            if (!string.IsNullOrEmpty(profile.LegionButtonDesktop))
            {
                Logger.Debug($"Applying LegionButtonDesktop: {profile.LegionButtonDesktop}");
                legionManager.LegionButtonDesktop.SetValue(profile.LegionButtonDesktop);
            }
            if (!string.IsNullOrEmpty(profile.LegionButtonPage))
            {
                Logger.Debug($"Applying LegionButtonPage: {profile.LegionButtonPage}");
                legionManager.LegionButtonPage.SetValue(profile.LegionButtonPage);
            }

            // Gyro settings
            // Apply explicit safe defaults when profile entries are missing so gyro never
            // inherits prior game/global values by accident.
            int legionGyroButton = profile.LegionGyroButton ?? 0;
            int legionGyroTarget = profile.LegionGyroTarget ?? 0;
            int legionGyroSensitivityX = profile.LegionGyroSensitivityX ?? 50;
            int legionGyroSensitivityY = profile.LegionGyroSensitivityY ?? 50;
            bool legionGyroInvertX = profile.LegionGyroInvertX ?? false;
            bool legionGyroInvertY = profile.LegionGyroInvertY ?? false;
            int legionGyroMappingType = profile.LegionGyroMappingType ?? 0;
            int legionGyroActivationMode = profile.LegionGyroActivationMode ?? 0;
            int legionGyroDeadzone = profile.LegionGyroDeadzone ?? 10;

            Logger.Debug($"Applying LegionGyroButton: {legionGyroButton}{(profile.LegionGyroButton.HasValue ? string.Empty : " (default)")}");
            legionManager.LegionGyroActivationButton.SetValue(legionGyroButton);

            Logger.Debug($"Applying LegionGyroTarget: {legionGyroTarget}{(profile.LegionGyroTarget.HasValue ? string.Empty : " (default)")}");
            legionManager.LegionGyroTarget.SetValue(legionGyroTarget);

            Logger.Debug($"Applying LegionGyroSensitivityX: {legionGyroSensitivityX}{(profile.LegionGyroSensitivityX.HasValue ? string.Empty : " (default)")}");
            legionManager.LegionGyroSensitivityX.SetValue(legionGyroSensitivityX);

            Logger.Debug($"Applying LegionGyroSensitivityY: {legionGyroSensitivityY}{(profile.LegionGyroSensitivityY.HasValue ? string.Empty : " (default)")}");
            legionManager.LegionGyroSensitivityY.SetValue(legionGyroSensitivityY);

            Logger.Debug($"Applying LegionGyroInvertX: {legionGyroInvertX}{(profile.LegionGyroInvertX.HasValue ? string.Empty : " (default)")}");
            legionManager.LegionGyroInvertX.SetValue(legionGyroInvertX);

            Logger.Debug($"Applying LegionGyroInvertY: {legionGyroInvertY}{(profile.LegionGyroInvertY.HasValue ? string.Empty : " (default)")}");
            legionManager.LegionGyroInvertY.SetValue(legionGyroInvertY);

            Logger.Debug($"Applying LegionGyroMappingType: {legionGyroMappingType}{(profile.LegionGyroMappingType.HasValue ? string.Empty : " (default)")}");
            legionManager.LegionGyroMappingType.SetValue(legionGyroMappingType);

            Logger.Debug($"Applying LegionGyroActivationMode: {legionGyroActivationMode}{(profile.LegionGyroActivationMode.HasValue ? string.Empty : " (default)")}");
            legionManager.LegionGyroActivationMode.SetValue(legionGyroActivationMode);

            Logger.Debug($"Applying LegionGyroDeadzone: {legionGyroDeadzone}{(profile.LegionGyroDeadzone.HasValue ? string.Empty : " (default)")}");
            legionManager.LegionGyroDeadzone.SetValue(legionGyroDeadzone);

            // Stick deadzones
            if (profile.LegionLeftStickDeadzone.HasValue)
            {
                Logger.Debug($"Applying LegionLeftStickDeadzone: {profile.LegionLeftStickDeadzone.Value}");
                legionManager.LegionLeftStickDeadzone.SetValue(profile.LegionLeftStickDeadzone.Value);
            }
            if (profile.LegionRightStickDeadzone.HasValue)
            {
                Logger.Debug($"Applying LegionRightStickDeadzone: {profile.LegionRightStickDeadzone.Value}");
                legionManager.LegionRightStickDeadzone.SetValue(profile.LegionRightStickDeadzone.Value);
            }

            // Trigger travel
            if (profile.LegionLeftTriggerStart.HasValue)
            {
                Logger.Debug($"Applying LegionLeftTriggerStart: {profile.LegionLeftTriggerStart.Value}");
                legionManager.LegionLeftTriggerStart.SetValue(profile.LegionLeftTriggerStart.Value);
            }
            if (profile.LegionLeftTriggerEnd.HasValue)
            {
                Logger.Debug($"Applying LegionLeftTriggerEnd: {profile.LegionLeftTriggerEnd.Value}");
                legionManager.LegionLeftTriggerEnd.SetValue(profile.LegionLeftTriggerEnd.Value);
            }
            if (profile.LegionRightTriggerStart.HasValue)
            {
                Logger.Debug($"Applying LegionRightTriggerStart: {profile.LegionRightTriggerStart.Value}");
                legionManager.LegionRightTriggerStart.SetValue(profile.LegionRightTriggerStart.Value);
            }
            if (profile.LegionRightTriggerEnd.HasValue)
            {
                Logger.Debug($"Applying LegionRightTriggerEnd: {profile.LegionRightTriggerEnd.Value}");
                legionManager.LegionRightTriggerEnd.SetValue(profile.LegionRightTriggerEnd.Value);
            }
            if (profile.LegionHairTriggers.HasValue)
            {
                Logger.Debug($"Applying LegionHairTriggers: {profile.LegionHairTriggers.Value}");
                legionManager.LegionHairTriggers.SetValue(profile.LegionHairTriggers.Value);
            }

            // Joystick as mouse
            if (profile.LegionJoystickAsMouseMode.HasValue)
            {
                Logger.Debug($"Applying LegionJoystickAsMouseMode: {profile.LegionJoystickAsMouseMode.Value}");
                legionManager.LegionJoystickAsMouseMode.SetValue(profile.LegionJoystickAsMouseMode.Value);
            }
            if (profile.LegionJoystickMouseSens.HasValue)
            {
                Logger.Debug($"Applying LegionJoystickMouseSens: {profile.LegionJoystickMouseSens.Value}");
                legionManager.LegionJoystickMouseSens.SetValue(profile.LegionJoystickMouseSens.Value);
            }

            // Gamepad mapping
            if (!string.IsNullOrEmpty(profile.LegionGamepadMapping))
            {
                Logger.Debug($"Applying LegionGamepadMapping from profile");
                legionManager.LegionGamepadMapping.SetValue(profile.LegionGamepadMapping);
            }

            // Other controller settings
            if (profile.LegionNintendoLayout.HasValue)
            {
                Logger.Debug($"Applying LegionNintendoLayout: {profile.LegionNintendoLayout.Value}");
                legionManager.LegionNintendoLayout.SetValue(profile.LegionNintendoLayout.Value);
            }
            if (profile.LegionVibration.HasValue)
            {
                Logger.Debug($"Applying LegionVibration: {profile.LegionVibration.Value}");
                legionManager.LegionVibration.SetValue(profile.LegionVibration.Value);
            }
            if (profile.LegionVibrationMode.HasValue)
            {
                Logger.Debug($"Applying LegionVibrationMode: {profile.LegionVibrationMode.Value}");
                legionManager.LegionVibrationMode.SetValue(profile.LegionVibrationMode.Value);
            }

            // Lighting settings - apply color, brightness, and speed BEFORE mode
            // to prevent flash to white when mode is applied with old/default color
            if (!string.IsNullOrEmpty(profile.LegionLightColor))
            {
                Logger.Debug($"Applying LegionLightColor: {profile.LegionLightColor}");
                legionManager.LegionLightColor.SetValue(profile.LegionLightColor);
            }
            if (profile.LegionLightBrightness.HasValue)
            {
                Logger.Debug($"Applying LegionLightBrightness: {profile.LegionLightBrightness.Value}");
                legionManager.LegionLightBrightness.SetValue(profile.LegionLightBrightness.Value);
            }
            if (profile.LegionLightSpeed.HasValue)
            {
                Logger.Debug($"Applying LegionLightSpeed: {profile.LegionLightSpeed.Value}");
                legionManager.LegionLightSpeed.SetValue(profile.LegionLightSpeed.Value);
            }
            // Apply mode last so it uses the updated color/brightness/speed
            if (profile.LegionLightMode.HasValue)
            {
                Logger.Debug($"Applying LegionLightMode: {profile.LegionLightMode.Value}");
                legionManager.LegionLightMode.SetValue(profile.LegionLightMode.Value);
            }
            if (profile.LegionPowerLight.HasValue)
            {
                Logger.Debug($"Applying LegionPowerLight: {profile.LegionPowerLight.Value}");
                legionManager.LegionPowerLight.SetValue(profile.LegionPowerLight.Value);
            }
        }

        private static void ApplyAutoTDPSettingsFromProfile()
        {
            if (profileManager?.CurrentProfile == null || autoTDPManager == null)
                return;

            var profile = profileManager.CurrentProfile;
            var profileName = profile.GameId.Name;

            Logger.Info($"Applying AutoTDP settings from profile: {profileName}");

            // Apply AutoTDP settings from profile
            // Use ForceSetValue for Enabled to ensure the pipe message is ALWAYS sent to the widget.
            // If the global profile was corrupted (AutoTDPEnabled=true from a previous bug),
            // SetValue would skip NotifyPropertyChanged when the value hasn't changed (e.g., game
            // profile had AutoTDP=true and corrupted global also has true), leaving the widget
            // with the wrong toggle state.
            Logger.Debug($"Applying AutoTDPEnabled: {profile.AutoTDPEnabled}");
            autoTDPManager.Enabled.ForceSetValue(profile.AutoTDPEnabled);

            Logger.Debug($"Applying AutoTDPTargetFPS: {profile.AutoTDPTargetFPS}");
            autoTDPManager.TargetFPS.SetValue(profile.AutoTDPTargetFPS);

            Logger.Debug($"Applying AutoTDPMinTDP: {profile.AutoTDPMinTDP}");
            autoTDPManager.MinTDP.SetValue(profile.AutoTDPMinTDP);

            Logger.Debug($"Applying AutoTDPMaxTDP: {profile.AutoTDPMaxTDP}");
            autoTDPManager.MaxTDP.SetValue(profile.AutoTDPMaxTDP);

            Logger.Debug($"Applying AutoTDPPauseWhenUnfocused: {profile.AutoTDPPauseWhenUnfocused}");
            autoTDPManager.PauseWhenUnfocused.SetValue(profile.AutoTDPPauseWhenUnfocused);

            // Apply controller type (0=PID, 1=Q-Learning, 2=SARSA)
            // Try new property first, fall back to legacy UseMLMode for migration
            int controllerType = profile.AutoTDPControllerType;
            if (controllerType == 0 && profile.AutoTDPUseMLMode)
            {
                // Legacy migration: UseMLMode=true -> Q-Learning (1)
                controllerType = 1;
            }
            Logger.Debug($"Applying AutoTDPControllerType: {controllerType} (PID=0, Q-Learning=1, SARSA=2)");
            autoTDPManager.ControllerType.SetValue(controllerType);
            autoTDPManager.UseMLMode.SetValue(controllerType > 0);  // Legacy sync
        }

        /// <summary>
        /// Restores global profile settings (TDP, AutoTDP, Legion mode, etc.)
        /// Called when transitioning away from a per-game profile:
        /// - Game stops (RunningGame becomes invalid)
        /// - Game changes from per-game profile game to non-per-game-profile game
        /// - Per-game profile is explicitly disabled by widget
        /// Must be called within isApplyingProfile = true context.
        /// </summary>
        private static void RestoreGlobalProfileSettings()
        {
            // Refresh GlobalProfile from cache — property change handlers (TDP_PropertyChanged, etc.)
            // update CurrentProfile (a struct copy), which saves to cache/disk, but the
            // GlobalProfile field stays stale since GameProfile is a struct.
            profileManager.RefreshGlobalProfile();

            profileManager.CurrentProfile.SetValue(profileManager.GlobalProfile);

            Logger.Info($"Applying global profile settings: TDP={profileManager.GlobalProfile.TDP}, CPUBoost={profileManager.GlobalProfile.CPUBoost}, EPP={profileManager.GlobalProfile.CPUEPP}");

            // IMPORTANT: Disable AutoTDP FIRST, before setting TDP.
            // TDPProperty.NotifyPropertyChanged() skips hardware apply when IsAutoTDPActive is true.
            // If we set TDP while AutoTDP is still active, the TDP value never gets applied to hardware.
            ApplyAutoTDPSettingsFromProfile();
            // Clear the AutoTDP active flag immediately so the TDP set below applies to hardware.
            // The AutoTDP tick will also clear this on its next iteration, but we can't wait for that.
            performanceManager.IsAutoTDPActive = false;

            // Restore LegionPerformanceMode from global profile if set
            if (legionManager != null)
            {
                int? savedMode = profileManager.GlobalProfile.LegionPerformanceMode;
                if (savedMode.HasValue)
                {
                    int currentMode = legionManager.LegionPerformanceMode.Value;
                    if (currentMode != savedMode.Value)
                    {
                        Logger.Info($"Restoring global profile performance mode ({savedMode.Value}) (was {currentMode})");
                        legionManager.LegionPerformanceMode.SetValue(savedMode.Value);
                    }
                }
            }

            performanceManager.TDP.SetProfileValue(profileManager.GlobalProfile.TDP);
            performanceManager.TDPBoostEnabled.SetValue(profileManager.GlobalProfile.TDPBoostEnabled);
            powerManager.CPUBoost.SetValue(profileManager.GlobalProfile.CPUBoost);
            powerManager.CPUEPP.SetValue(profileManager.GlobalProfile.CPUEPP);
            profileManager.PerGameProfile.SetValue(false);

            // Apply Legion controller settings from global profile
            if (legionManager != null)
            {
                ApplyLegionControllerSettingsFromProfile();
            }
        }

        private static void CurrentProfile_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            // Use lock to ensure atomic profile application and prevent interleaved settings
            // from rapid game switches (Game A → Game B → Game A)
            lock (profileApplicationLock)
            {
                // Prevent reentrant profile handling that can cause race conditions
                if (isApplyingProfile)
                {
                    Logger.Debug("Skipping CurrentProfile_PropertyChanged - already applying profile");
                    return;
                }

                if (profileManager.CurrentProfile.Use || profileManager.CurrentProfile.IsGlobalProfile)
                {
                    try
                    {
                        isApplyingProfile = true;
                        Logger.Info($"Profile changed to {profileManager.CurrentProfile.GameId.Name}, apply it.");

                        // For per-game profiles, apply the saved LegionPerformanceMode if set
                        // This ensures the correct TDP mode is applied when the game is detected
                        if (profileManager.CurrentProfile.Use && legionManager != null)
                        {
                            int? savedMode = profileManager.CurrentProfile.LegionPerformanceMode;
                            if (savedMode.HasValue)
                            {
                                int currentMode = legionManager.LegionPerformanceMode.Value;
                                if (currentMode != savedMode.Value)
                                {
                                    Logger.Info($"Switching to saved performance mode ({savedMode.Value}) for per-game profile (was {currentMode})");
                                    legionManager.LegionPerformanceMode.SetValue(savedMode.Value);
                                }
                            }
                            else
                            {
                                // Profile has no saved LegionPerformanceMode - auto-switch to Custom mode (255)
                                // if not already in Custom mode, so that custom TDP values can be applied
                                int currentMode = legionManager.LegionPerformanceMode.Value;
                                if (currentMode != 255)
                                {
                                    Logger.Info($"Per-game profile has no saved LegionPerformanceMode, auto-switching to Custom mode (was {currentMode}) to enable TDP control");
                                    legionManager.LegionPerformanceMode.SetValue(255);
                                }
                                else
                                {
                                    Logger.Debug($"Per-game profile has no saved LegionPerformanceMode, already in Custom mode");
                                }
                            }
                        }

                        // Apply AutoTDP settings FIRST, before TDP.
                        // TDPProperty.NotifyPropertyChanged() skips hardware apply when IsAutoTDPActive is true.
                        // Applying AutoTDP first ensures IsAutoTDPActive is cleared when disabling AutoTDP,
                        // so the subsequent TDP.SetProfileValue applies to hardware.
                        ApplyAutoTDPSettingsFromProfile();
                        performanceManager.IsAutoTDPActive = false;

                        // Use SetProfileValue to ensure profile TDP takes precedence over in-flight widget messages
                        // All settings applied atomically under lock to prevent cross-contamination
                        performanceManager.TDP.SetProfileValue(profileManager.CurrentProfile.TDP);
                        performanceManager.TDPBoostEnabled.SetValue(profileManager.CurrentProfile.TDPBoostEnabled);
                        powerManager.CPUBoost.SetValue(profileManager.CurrentProfile.CPUBoost);
                        powerManager.CPUEPP.SetValue(profileManager.CurrentProfile.CPUEPP);
                        profileManager.PerGameProfile.SetValue(profileManager.CurrentProfile.Use);

                        // Apply Legion controller settings from profile (both global and per-game)
                        if (legionManager != null)
                        {
                            ApplyLegionControllerSettingsFromProfile();
                        }
                    }
                    finally
                    {
                        profileSwitchTime = DateTime.UtcNow;
                        isApplyingProfile = false;
                    }
                }
                else
                {
                    Logger.Info($"Profile changed to {profileManager.CurrentProfile.GameId.Name} is not used.");
                }
            }
        }

        private static void PerGameProfile_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            // Prevent reentrant profile handling
            if (isApplyingProfile)
            {
                Logger.Debug("Skipping PerGameProfile_PropertyChanged - already applying profile");
                return;
            }

            try
            {
                isApplyingProfile = true;
                GameProfile gameProfile;
                if (profileManager.PerGameProfile)
                {
                    // Don't enable per-game profile if there's no valid game running
                    // This prevents race conditions when game closes and stale PerGameProfile=true arrives
                    if (!systemManager.RunningGame.Value.IsValid())
                    {
                        Logger.Info("Ignoring PerGameProfile=true - no valid game running (stale message)");
                        return;
                    }

                    if (!profileManager.TryGetProfile(systemManager.RunningGame.Value.GameId, out gameProfile))
                    {
                        gameProfile = profileManager.AddNewProfile(systemManager.RunningGame.Value.GameId);
                    }
                    Logger.Info($"Enable per-game profile for {systemManager.RunningGame.Value.GameId}");
                    gameProfile.Use = true;

                    // Disable DefaultGameProfile when per-game profile is enabled
                    if (defaultGameProfileManager != null && defaultGameProfileManager.ProfileEnabled.Value)
                    {
                        Logger.Info("Disabling DefaultGameProfile since per-game profile is now enabled");
                        defaultGameProfileManager.ProfileEnabled.SetValue(false);
                    }

                    // Apply saved LegionPerformanceMode from game profile, or default to Custom (255) for new profiles.
                    // Previously this always switched to Custom, which overrode user-saved preset modes.
                    if (legionManager != null)
                    {
                        int? savedMode = gameProfile.LegionPerformanceMode;
                        if (savedMode.HasValue && savedMode.Value > 0)
                        {
                            if (legionManager.LegionPerformanceMode.Value != savedMode.Value)
                            {
                                Logger.Info($"Applying saved performance mode ({savedMode.Value}) for per-game profile '{systemManager.RunningGame.Value.GameId.Name}'");
                                legionManager.LegionPerformanceMode.SetValue(savedMode.Value);
                            }
                            else
                            {
                                Logger.Debug($"Per-game profile already in saved mode ({savedMode.Value})");
                            }
                        }
                        else if (legionManager.LegionPerformanceMode.Value != 255)
                        {
                            Logger.Info("Switching to Custom TDP mode for new per-game profile (no saved mode)");
                            legionManager.LegionPerformanceMode.SetValue(255);
                        }
                    }

                    // Set current profile and apply settings from per-game profile.
                    // CurrentProfile_PropertyChanged is blocked by isApplyingProfile, so we
                    // must apply settings explicitly here (same pattern as RestoreGlobalProfileSettings).
                    profileManager.CurrentProfile.SetValue(gameProfile);

                    ApplyAutoTDPSettingsFromProfile();
                    performanceManager.IsAutoTDPActive = false;

                    performanceManager.TDP.SetProfileValue(gameProfile.TDP);
                    performanceManager.TDPBoostEnabled.SetValue(gameProfile.TDPBoostEnabled);
                    powerManager.CPUBoost.SetValue(gameProfile.CPUBoost);
                    powerManager.CPUEPP.SetValue(gameProfile.CPUEPP);
                }
                else
                {
                    // Don't disable per-game profile if a game with an active profile is still running
                    // This prevents race conditions when widget sends stale PerGameProfile=false
                    if (systemManager.RunningGame.Value.IsValid())
                    {
                        // Check if the current profile matches the running game (or a similar name variant)
                        var currentProfile = profileManager.CurrentProfile;
                        if (currentProfile != null && currentProfile != profileManager.GlobalProfile && currentProfile.Use)
                        {
                            var runningGameName = systemManager.RunningGame.Value.GameId.Name ?? "";
                            var profileName = currentProfile.GameId.Name ?? "";

                            // Check for exact match or name variants (e.g., "Game: Title" vs "Game Title")
                            bool isSameGame = string.Equals(runningGameName, profileName, StringComparison.OrdinalIgnoreCase) ||
                                              runningGameName.Replace(":", "").Replace("  ", " ").Trim().Equals(
                                                  profileName.Replace(":", "").Replace("  ", " ").Trim(),
                                                  StringComparison.OrdinalIgnoreCase);

                            if (isSameGame)
                            {
                                // Only ignore if this is likely a stale message from a recent game
                                // transition (within 2 seconds). Otherwise, honor the user's explicit
                                // toggle and restore global settings.
                                double secondsSinceSwitch = (DateTime.UtcNow - profileSwitchTime).TotalSeconds;
                                if (secondsSinceSwitch < 2)
                                {
                                    Logger.Info($"Ignoring stale PerGameProfile=false - game '{runningGameName}' still running, recent switch {secondsSinceSwitch:F1}s ago");
                                    return;
                                }
                                Logger.Info($"Honoring PerGameProfile=false while game '{runningGameName}' is running (user toggle, {secondsSinceSwitch:F1}s since last switch)");
                                // Fall through to disable per-game profile and restore global settings
                            }
                        }
                    }

                    if (profileManager.TryGetProfile(systemManager.RunningGame.Value.GameId, out gameProfile))
                    {
                        gameProfile.Use = false;
                    }
                    // Restore global profile and apply all its settings (TDP, AutoTDP, etc.)
                    // CurrentProfile_PropertyChanged is blocked by isApplyingProfile, so we
                    // must apply settings explicitly here
                    RestoreGlobalProfileSettings();
                }
            }
            finally
            {
                profileSwitchTime = DateTime.UtcNow;
                isApplyingProfile = false;
            }
        }

        private static void TDP_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            // Skip during profile application to prevent cross-contamination
            // (e.g., writing game profile TDP to global profile during switch)
            if (isApplyingProfile)
            {
                Logger.Debug($"Skipping TDP_PropertyChanged - already applying profile (TDP={performanceManager.TDP})");
                return;
            }

            // Skip stale widget messages during cooldown after profile switch
            if (IsInProfileSwitchCooldown())
            {
                Logger.Debug($"Skipping TDP_PropertyChanged - in profile switch cooldown (TDP={performanceManager.TDP})");
                return;
            }

            // Skip when default game profile is active - don't overwrite user's saved profile
            if (defaultGameProfileManager != null && defaultGameProfileManager.ProfileEnabled.Value)
            {
                Logger.Debug($"Skipping TDP_PropertyChanged - Default Game Profile is active (TDP={performanceManager.TDP})");
                return;
            }

            // TEST [ProfileSaveFlags-TDP]: With ProfileSaveTDP unchecked in the widget, change
            // TDP while a per-game profile is active. Expect the change to land in GlobalProfile
            // (and the per-game TDP to remain whatever was saved before). Pre-flag baseline:
            // always wrote to CurrentProfile regardless of flag.
            RouteProfileSave(ProfileSaveFlagsState.TDP, "TDP",
                cur => cur.TDP = performanceManager.TDP,
                glo => glo.TDP = performanceManager.TDP);
        }

        private static void TDPBoostEnabled_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            // Skip during profile application to prevent cross-contamination
            if (isApplyingProfile)
            {
                Logger.Debug($"Skipping TDPBoostEnabled_PropertyChanged - already applying profile");
                return;
            }

            // Skip stale widget messages during cooldown after profile switch
            if (IsInProfileSwitchCooldown())
            {
                Logger.Debug($"Skipping TDPBoostEnabled_PropertyChanged - in profile switch cooldown");
                return;
            }

            // Skip when default game profile is active - don't overwrite user's saved profile
            if (defaultGameProfileManager != null && defaultGameProfileManager.ProfileEnabled.Value)
            {
                Logger.Debug($"Skipping TDPBoostEnabled_PropertyChanged - Default Game Profile is active");
                return;
            }

            // TEST [ProfileSaveFlags-TDP]: TDPBoost (CPU long/short-term boost FPPT/SPPT) is
            // grouped under the TDP flag — there's no separate ProfileSaveTDPBoost checkbox,
            // and StickyTDP in the widget refers to a different feature (auto-restore TDP after
            // mode change). With ProfileSaveTDP unchecked, toggle TDP Boost in-game and verify
            // the change goes to GlobalProfile, not the per-game profile.
            RouteProfileSave(ProfileSaveFlagsState.TDP, "TDPBoostEnabled",
                cur => cur.TDPBoostEnabled = performanceManager.TDPBoostEnabled.Value,
                glo => glo.TDPBoostEnabled = performanceManager.TDPBoostEnabled.Value);
        }

        private static void RunningGame_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            // #66: drive PresentMon subprocess lifecycle from the same RunningGame signal.
            // Wrap in try/catch in the caller so PresentMon faults never break profile flow.
            OnRunningGameChangedForPresentMon();

            // Prevent reentrant profile handling
            if (isApplyingProfile)
            {
                Logger.Debug("Skipping RunningGame_PropertyChanged - already applying profile");
                return;
            }

            try
            {
                isApplyingProfile = true;
                if (systemManager.RunningGame.Value.IsValid())
                {
                    bool gameHasActiveProfile = false;
                    if (profileManager.TryGetProfile(systemManager.RunningGame.Value.GameId, out var runningGameProfile))
                    {
                        if (runningGameProfile.Use)
                        {
                            Logger.Info($"Game {systemManager.RunningGame.GameId} has per-game profile in use.");
                            profileManager.CurrentProfile.SetValue(runningGameProfile);
                            gameHasActiveProfile = true;

                            // Notify widget that per-game profile is active.
                            // The widget may not auto-enable (e.g., disabled preference stored locally),
                            // so the helper must assert this to keep both sides in sync.
                            profileManager.PerGameProfile.ForceSetValue(true);

                            // Apply all settings explicitly. CurrentProfile_PropertyChanged is blocked
                            // by isApplyingProfile, so we must apply here (same as PerGameProfile_PropertyChanged).
                            if (legionManager != null)
                            {
                                int? savedMode = runningGameProfile.LegionPerformanceMode;
                                if (savedMode.HasValue && savedMode.Value > 0)
                                {
                                    if (legionManager.LegionPerformanceMode.Value != savedMode.Value)
                                    {
                                        Logger.Info($"Applying saved performance mode ({savedMode.Value}) for game '{systemManager.RunningGame.Value.GameId.Name}'");
                                        legionManager.LegionPerformanceMode.SetValue(savedMode.Value);
                                    }
                                }
                                else if (legionManager.LegionPerformanceMode.Value != 255)
                                {
                                    Logger.Info("Switching to Custom TDP mode for game profile (no saved mode)");
                                    legionManager.LegionPerformanceMode.SetValue(255);
                                }
                            }

                            ApplyAutoTDPSettingsFromProfile();
                            performanceManager.IsAutoTDPActive = false;

                            performanceManager.TDP.SetProfileValue(runningGameProfile.TDP);
                            performanceManager.TDPBoostEnabled.SetValue(runningGameProfile.TDPBoostEnabled);
                            powerManager.CPUBoost.SetValue(runningGameProfile.CPUBoost);
                            powerManager.CPUEPP.SetValue(runningGameProfile.CPUEPP);

                            if (legionManager != null)
                            {
                                ApplyLegionControllerSettingsFromProfile();
                            }

                            Logger.Info($"Applied per-game profile settings for {systemManager.RunningGame.Value.GameId.Name}: TDP={runningGameProfile.TDP}, AutoTDP={runningGameProfile.AutoTDPEnabled}");
                        }
                        else
                        {
                            Logger.Info($"Game {systemManager.RunningGame.GameId} has per-game profile but not in use.");
                        }
                    }
                    else
                    {
                        Logger.Info($"Game {systemManager.RunningGame.GameId} doesn't have per-game profile.");
                    }

                    // Only restore global if the current game doesn't have an active profile
                    // AND we're currently on a per-game profile from a previous game.
                    // Without the gameHasActiveProfile check, re-firing for the SAME game
                    // (e.g., foreground change) would incorrectly restore global and cause
                    // per-game AutoTDP settings to bleed into the global profile.
                    if (!gameHasActiveProfile && !profileManager.CurrentProfile.IsGlobalProfile)
                    {
                        Logger.Info($"Previous game had per-game profile active, restoring global profile for {systemManager.RunningGame.GameId}");
                        RestoreGlobalProfileSettings();
                    }

                    // Switch Lossless Scaling profile for the detected game
                    if (losslessScalingManager.LosslessScalingInstalled.Value)
                    {
                        var gameName = systemManager.RunningGame.Value.GameId.Name;
                        var gamePath = systemManager.RunningGame.Value.GameId.Path;
                        losslessScalingManager.SetCurrentGame(gameName, gamePath);
                    }
                }
                else
                {
                    Logger.Info($"Stopped playing game, use global profile instead.");
                    RestoreGlobalProfileSettings();

                    // Reset Lossless Scaling to Default profile when game stops
                    if (losslessScalingManager.LosslessScalingInstalled.Value)
                    {
                        losslessScalingManager.SetCurrentGame("Default", "");
                    }
                }
            }
            finally
            {
                profileSwitchTime = DateTime.UtcNow;
                isApplyingProfile = false;
            }
        }

    }
}
