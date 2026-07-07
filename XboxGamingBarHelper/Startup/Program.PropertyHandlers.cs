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

        private static void CPUState_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            // Skip during profile application to prevent cross-contamination
            if (isApplyingProfile)
            {
                Logger.Debug($"Skipping CPUState_PropertyChanged - already applying profile");
                return;
            }

            // Skip stale widget messages during cooldown after profile switch
            if (IsInProfileSwitchCooldown())
            {
                Logger.Debug($"Skipping CPUState_PropertyChanged - in profile switch cooldown");
                return;
            }

            // TEST [ProfileSaveFlags-CPUState]: With ProfileSaveCPUState unchecked, change
            // Min/Max CPU State sliders in-game. Verify the change goes to GlobalProfile, not
            // the per-game profile. Pre-flag baseline: always wrote to CurrentProfile.
            RouteProfileSave(ProfileSaveFlagsState.CPUState, "CPUState",
                cur => { cur.MaxCPUState = powerManager.MaxCPUState.Value; cur.MinCPUState = powerManager.MinCPUState.Value; },
                glo => { glo.MaxCPUState = powerManager.MaxCPUState.Value; glo.MinCPUState = powerManager.MinCPUState.Value; });
        }

        private static void SystemManager_ResumeFromSleep(object sender)
        {
            Logger.Info("System resumed from sleep/hibernation, refreshing hardware sensors and re-applying profile.");

            // Reset RTSS OSD connection (can become stale after hibernation, causing frozen OSD values)
            rtssManager?.ResetRTSSConnection();

            // Force refresh hardware sensors (battery values can be stale after hibernation)
            performanceManager?.ForceRefreshHardware();

            // Rebuild the EC fan override path if the PawnIO handle died during
            // sleep — otherwise the custom fan curve silently stops applying
            // until three tick-level write failures trigger the self-heal.
            legionManager?.RecoverEcFanOverrideAfterResume();

            // Re-apply current profile settings (TDP, CPU boost, EPP, CPU state)
            CurrentProfile_PropertyChanged(sender, null);
        }

        // GPU Clock - DISABLED: Not supported by RyzenAdj on this hardware (returns error -1)
        //private static void GPUClock_PropertyChanged(object sender, PropertyChangedEventArgs e)
        //{
        //    // GPU Clock is saved per-profile
        //    // Note: Profiles would need GPUClockMin/Max properties added to support per-game GPU clocks
        //    Logger.Info($"GPU Clock settings changed: Enabled={powerManager.LimitGPUClock}, Min={powerManager.GPUClockMin}, Max={powerManager.GPUClockMax}");
        //}

        private static void CPUBoost_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            // Skip during profile application to prevent cross-contamination
            if (isApplyingProfile)
            {
                Logger.Debug($"Skipping CPUBoost_PropertyChanged - already applying profile");
                return;
            }

            // Skip stale widget messages during cooldown after profile switch
            if (IsInProfileSwitchCooldown())
            {
                Logger.Debug($"Skipping CPUBoost_PropertyChanged - in profile switch cooldown");
                return;
            }

            // TEST [ProfileSaveFlags-CPUBoost]: With ProfileSaveCPUBoost unchecked, toggle
            // CPU Boost in-game. Verify the change goes to GlobalProfile, not the per-game
            // profile. Pre-flag baseline: always wrote to CurrentProfile.
            RouteProfileSave(ProfileSaveFlagsState.CPUBoost, "CPUBoost",
                cur => cur.CPUBoost = powerManager.CPUBoost,
                glo => glo.CPUBoost = powerManager.CPUBoost);
        }

        private static void CPUEPP_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            // Skip during profile application to prevent cross-contamination
            if (isApplyingProfile)
            {
                Logger.Debug($"Skipping CPUEPP_PropertyChanged - already applying profile");
                return;
            }

            // Skip stale widget messages during cooldown after profile switch
            if (IsInProfileSwitchCooldown())
            {
                Logger.Debug($"Skipping CPUEPP_PropertyChanged - in profile switch cooldown");
                return;
            }

            // TEST [ProfileSaveFlags-CPUEPP]: With ProfileSaveCPUEPP unchecked, change CPU EPP
            // in-game. Verify the change goes to GlobalProfile, not the per-game profile.
            // Pre-flag baseline: always wrote to CurrentProfile.
            RouteProfileSave(ProfileSaveFlagsState.CPUEPP, "CPUEPP",
                cur => cur.CPUEPP = powerManager.CPUEPP,
                glo => glo.CPUEPP = powerManager.CPUEPP);
        }

        private static void AutoHibernateEnabled_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (settingsManager?.AutoHibernateEnabled == null) return;
            SetAutoHibernateEnabled(settingsManager.AutoHibernateEnabled.Value);
        }

        private static void AutoHibernateIdleMinutes_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (settingsManager?.AutoHibernateIdleMinutes == null) return;
            UpdateAutoHibernateIdleTimeout(settingsManager.AutoHibernateIdleMinutes.Value);
        }

    }
}
