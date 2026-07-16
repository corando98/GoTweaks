using NLog;
using Shared.Constants;
using System;
using System.Runtime.InteropServices;
using XboxGamingBarHelper.Core;
using XboxGamingBarHelper.Services;
using XboxGamingBarHelper.Windows;

namespace XboxGamingBarHelper.Power
{
    internal class PowerManager : Manager
    {
        private IntPtr ryzenAdjHandle;
        public IntPtr RyzenAdjHandle
        {
            get { return ryzenAdjHandle; }
        }

        private readonly CPUBoostProperty cpuBoost;
        public CPUBoostProperty CPUBoost
        {
            get { return cpuBoost; }
        }

        private readonly CPUEPPProperty cpuEPP;
        public CPUEPPProperty CPUEPP
        {
            get { return cpuEPP; }
        }

        private readonly OSPowerModeProperty osPowerMode;
        public OSPowerModeProperty OSPowerMode
        {
            get { return osPowerMode; }
        }

        private readonly PowerButtonActionProperty powerButtonActionAC;
        public PowerButtonActionProperty PowerButtonActionAC
        {
            get { return powerButtonActionAC; }
        }

        private readonly PowerButtonActionProperty powerButtonActionDC;
        public PowerButtonActionProperty PowerButtonActionDC
        {
            get { return powerButtonActionDC; }
        }

        private readonly DisplayTimeoutProperty displayTimeoutAC;
        public DisplayTimeoutProperty DisplayTimeoutAC
        {
            get { return displayTimeoutAC; }
        }

        private readonly DisplayTimeoutProperty displayTimeoutDC;
        public DisplayTimeoutProperty DisplayTimeoutDC
        {
            get { return displayTimeoutDC; }
        }

        private readonly HibernateTimeoutProperty hibernateTimeoutAC;
        public HibernateTimeoutProperty HibernateTimeoutAC
        {
            get { return hibernateTimeoutAC; }
        }

        private readonly HibernateTimeoutProperty hibernateTimeoutDC;
        public HibernateTimeoutProperty HibernateTimeoutDC
        {
            get { return hibernateTimeoutDC; }
        }

        // GPU Clock - DISABLED: Not supported by RyzenAdj on this hardware (returns error -1)
        //private readonly LimitGPUClockProperty limitGPUClock;
        //public LimitGPUClockProperty LimitGPUClock
        //{
        //    get { return limitGPUClock; }
        //}

        //private readonly GPUClockMinProperty gpuClockMin;
        //public GPUClockMinProperty GPUClockMin
        //{
        //    get { return gpuClockMin; }
        //}

        //private readonly GPUClockMaxProperty gpuClockMax;
        //public GPUClockMaxProperty GPUClockMax
        //{
        //    get { return gpuClockMax; }
        //}

        public PowerManager(IntPtr ryzenAdjHandle) : base()
        {
            this.ryzenAdjHandle = ryzenAdjHandle;

            Logger.Info($"Check CPU Boost Mode and EPP.");
            cpuBoost = new CPUBoostProperty(GetCpuBoostMode(false), this);
            cpuEPP = new CPUEPPProperty((int)GetEppValue(false), this);

            // OS Power Mode
            var initialPowerMode = GetOSPowerMode();
            Logger.Info($"Initial OS Power Mode: {initialPowerMode} (0=Efficiency, 1=Balanced, 2=Performance)");
            osPowerMode = new OSPowerModeProperty(initialPowerMode >= 0 ? initialPowerMode : 1, this);

            // Power Button action - read directly from the active Windows power plan
            // (not a GoTweaks-owned default; Windows' own out-of-box default is Sleep=1).
            var initialPowerButtonAC = (int)GetPowerButtonAction(true);
            var initialPowerButtonDC = (int)GetPowerButtonAction(false);
            Logger.Info($"Initial Power Button action: AC={initialPowerButtonAC}, DC={initialPowerButtonDC} (0=Nothing, 1=Sleep, 2=Hibernate, 3=Shutdown)");
            powerButtonActionAC = new PowerButtonActionProperty(initialPowerButtonAC, this, true, Shared.Enums.Function.SystemPowerButtonActionAC);
            powerButtonActionDC = new PowerButtonActionProperty(initialPowerButtonDC, this, false, Shared.Enums.Function.SystemPowerButtonActionDC);

            // Display Timeout - read directly from the active Windows power plan, same
            // "pull from Windows" model as the Power Button action above.
            var initialDisplayTimeoutAC = (int)GetDisplayTimeoutSeconds(true);
            var initialDisplayTimeoutDC = (int)GetDisplayTimeoutSeconds(false);
            Logger.Info($"Initial Display Timeout: AC={initialDisplayTimeoutAC}s, DC={initialDisplayTimeoutDC}s");
            displayTimeoutAC = new DisplayTimeoutProperty(initialDisplayTimeoutAC, this, true, Shared.Enums.Function.SystemDisplayTimeoutAC);
            displayTimeoutDC = new DisplayTimeoutProperty(initialDisplayTimeoutDC, this, false, Shared.Enums.Function.SystemDisplayTimeoutDC);

            // Hibernate Timeout - GoTweaks-owned (no Windows API to read from), loads
            // its own persisted value from LocalSettings.
            hibernateTimeoutAC = new HibernateTimeoutProperty(this, true, Shared.Enums.Function.SystemHibernateTimeoutAC);
            hibernateTimeoutDC = new HibernateTimeoutProperty(this, false, Shared.Enums.Function.SystemHibernateTimeoutDC);
            Logger.Info($"Initial Hibernate Timeout: AC={hibernateTimeoutAC.Value}min, DC={hibernateTimeoutDC.Value}min");

            // GPU Clock - DISABLED: Not supported by RyzenAdj on this hardware (returns error -1)
            //// Initialize GPU Clock properties
            //limitGPUClock = new LimitGPUClockProperty(false, this);
            //gpuClockMin = new GPUClockMinProperty(200, this);
            //gpuClockMax = new GPUClockMaxProperty(3000, this);
            //Logger.Info($"GPU Clock limiter initialized (disabled by default).");
        }

        public static Guid GetActiveScheme()
        {
            var res = PowrProf.PowerGetActiveScheme(IntPtr.Zero, out IntPtr pGuid);
            if (res != 0)
            {
                Logger.Error("Can't get active power scheme?");
                return Guid.Empty;
            }

            var active = (Guid)Marshal.PtrToStructure(pGuid, typeof(Guid));
            Marshal.FreeHGlobal(pGuid);
            return active;
        }

        public static bool GetCpuBoostMode(bool isAC)
        {
            var scheme = GetActiveScheme();
            var subgroup = PowerGuids.GUID_PROCESSOR_SETTINGS_SUBGROUP;
            var setting = PowerGuids.GUID_PROCESSOR_PERFBOOST_MODE;

            var status = isAC
                ? PowrProf.PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out uint result)
                : PowrProf.PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out result);

            if (status != 0)
            {
                Logger.Error("Can't get CPU Boost Mode?");
                return false;
            }

            return result != 0;
        }

        public static void SetCpuBoostMode(bool isAC, bool enabled)
        {
            // Save original values before first modification (for clean uninstall)
            try
            {
                bool currentAC = GetCpuBoostMode(true);
                bool currentDC = GetCpuBoostMode(false);
                SystemRestoreService.SaveOriginalCpuBoost(currentAC, currentDC);
            }
            catch (Exception ex)
            {
                Logger.Warn($"Failed to save original CPU Boost values: {ex.Message}");
            }

            var scheme = GetActiveScheme();
            var subgroup = PowerGuids.GUID_PROCESSOR_SETTINGS_SUBGROUP;
            var setting = PowerGuids.GUID_PROCESSOR_PERFBOOST_MODE;
            uint value = (uint)(enabled ? 2 : 0);
            Logger.Info($"Set CPU Boost to {(enabled ? "Aggressive" : "Disabled")}.");

            var status = isAC ? PowrProf.PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, value)
                : PowrProf.PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, value);

            if (status != 0)
            {
                Logger.Error("Can't set CPU Boost Mode??");
                return;
            }

            Logger.Info($"Set CPU Boost {(isAC ? "AC" : "DC")} to {value}.");
            // Apply the updated plan
            PowrProf.PowerSetActiveScheme(IntPtr.Zero, ref scheme);
        }

        public static uint GetEppValue(bool isAC)
        {
            Guid scheme = GetActiveScheme();
            Guid subgroup = PowerGuids.GUID_PROCESSOR_SETTINGS_SUBGROUP;
            Guid setting = PowerGuids.GUID_PROCESSOR_EPP;

            uint result;
            uint status = isAC ? 
                PowrProf.PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out result)
                : PowrProf.PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out result);

            if (status != 0)
            {
                Logger.Error("Can't get EPP value.");
                return 90;
            }

            return result;
        }

        public static void SetEppValue(bool isAC, uint value)
        {
            if (value > 100) value = 100; // clamp to valid range

            // Save original values before first modification (for clean uninstall)
            try
            {
                int currentAC = (int)GetEppValue(true);
                int currentDC = (int)GetEppValue(false);
                SystemRestoreService.SaveOriginalEpp(currentAC, currentDC);
            }
            catch (Exception ex)
            {
                Logger.Warn($"Failed to save original EPP values: {ex.Message}");
            }

            Guid scheme = GetActiveScheme();
            Guid subgroup = PowerGuids.GUID_PROCESSOR_SETTINGS_SUBGROUP;
            Guid setting = PowerGuids.GUID_PROCESSOR_EPP;

            uint status = isAC
                ? PowrProf.PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, value)
                : PowrProf.PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, value);

            if (status != 0)
            {
                Logger.Error("Can't set EPP value.");
                return;
            }

            Logger.Info($"Set CPU EPP {(isAC ? "AC" : "DC")} to {value}.");
            // Apply changes to the currently active power plan
            PowrProf.PowerSetActiveScheme(IntPtr.Zero, ref scheme);
        }

        #region OS Power Mode (Windows 11 Power Slider)

        // Power mode overlay GUIDs
        private static readonly Guid GUID_POWER_SAVER = new Guid("961cc777-2547-4f9d-8174-7d86181b8a7a");
        private static readonly Guid GUID_BALANCED = Guid.Empty; // 00000000-0000-0000-0000-000000000000
        private static readonly Guid GUID_HIGH_PERFORMANCE = new Guid("ded574b5-45a0-4f42-8737-46345c09c238");

        /// <summary>
        /// Gets the current OS power mode.
        /// </summary>
        /// <returns>0 = Best Power Efficiency, 1 = Balanced, 2 = Best Performance, -1 = Unknown/Error</returns>
        public static int GetOSPowerMode()
        {
            try
            {
                uint status = PowrProf.PowerGetEffectiveOverlayScheme(out Guid overlayGuid);
                if (status != 0)
                {
                    Logger.Warn($"PowerGetEffectiveOverlayScheme failed with status {status}");
                    return -1;
                }

                if (overlayGuid == GUID_POWER_SAVER)
                    return 0; // Best Power Efficiency
                else if (overlayGuid == GUID_BALANCED || overlayGuid == Guid.Empty)
                    return 1; // Balanced
                else if (overlayGuid == GUID_HIGH_PERFORMANCE)
                    return 2; // Best Performance
                else
                {
                    Logger.Info($"Unknown power overlay GUID: {overlayGuid}");
                    return 1; // Default to Balanced for unknown
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error getting OS power mode: {ex.Message}");
                return -1;
            }
        }

        /// <summary>
        /// Sets the OS power mode.
        /// </summary>
        /// <param name="mode">0 = Best Power Efficiency, 1 = Balanced, 2 = Best Performance</param>
        /// <returns>True if successful</returns>
        public static bool SetOSPowerMode(int mode)
        {
            // Save original value before first modification (for clean uninstall)
            try
            {
                int currentMode = GetOSPowerMode();
                if (currentMode >= 0)
                {
                    SystemRestoreService.SaveOriginalOsPowerMode(currentMode);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Failed to save original OS Power Mode value: {ex.Message}");
            }

            try
            {
                Guid targetGuid;
                string modeName;

                switch (mode)
                {
                    case 0:
                        targetGuid = GUID_POWER_SAVER;
                        modeName = "Best Power Efficiency";
                        break;
                    case 1:
                        targetGuid = GUID_BALANCED;
                        modeName = "Balanced";
                        break;
                    case 2:
                        targetGuid = GUID_HIGH_PERFORMANCE;
                        modeName = "Best Performance";
                        break;
                    default:
                        Logger.Warn($"Invalid power mode: {mode}");
                        return false;
                }

                uint status = PowrProf.PowerSetActiveOverlayScheme(targetGuid);
                if (status != 0)
                {
                    Logger.Error($"PowerSetActiveOverlayScheme failed with status {status}");
                    return false;
                }

                Logger.Info($"Set OS Power Mode to {modeName} ({targetGuid})");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"Error setting OS power mode: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region Power Button Action

        /// <summary>
        /// Gets what the physical power button does. 0=Do nothing, 1=Sleep, 2=Hibernate, 3=Shut down
        /// - these match the raw Windows PowerButtonAction setting values exactly.
        /// </summary>
        public static uint GetPowerButtonAction(bool isAC)
        {
            Guid scheme = GetActiveScheme();
            Guid subgroup = PowerGuids.GUID_BUTTONS_SUBGROUP;
            Guid setting = PowerGuids.GUID_POWERBUTTON_ACTION;

            uint result;
            uint status = isAC
                ? PowrProf.PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out result)
                : PowrProf.PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out result);

            if (status != 0)
            {
                Logger.Error("Can't read Power Button action.");
                return 1; // Sleep - Windows' own out-of-box default
            }

            return result;
        }

        /// <summary>
        /// Sets what the physical power button does. 0=Do nothing, 1=Sleep, 2=Hibernate, 3=Shut down.
        /// </summary>
        public static void SetPowerButtonAction(bool isAC, uint value)
        {
            // Save original values before first modification (for clean uninstall)
            try
            {
                uint currentAC = GetPowerButtonAction(true);
                uint currentDC = GetPowerButtonAction(false);
                SystemRestoreService.SaveOriginalPowerButtonAction(currentAC, currentDC);
            }
            catch (Exception ex)
            {
                Logger.Warn($"Failed to save original Power Button action values: {ex.Message}");
            }

            Guid scheme = GetActiveScheme();
            Guid subgroup = PowerGuids.GUID_BUTTONS_SUBGROUP;
            Guid setting = PowerGuids.GUID_POWERBUTTON_ACTION;

            uint status = isAC
                ? PowrProf.PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, value)
                : PowrProf.PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, value);

            if (status != 0)
            {
                Logger.Error("Can't set Power Button action.");
                return;
            }

            Logger.Info($"Set Power Button action {(isAC ? "AC" : "DC")} to {value}.");
            PowrProf.PowerSetActiveScheme(IntPtr.Zero, ref scheme);
        }

        #endregion

        #region Display Timeout

        /// <summary>
        /// Gets the "turn off display after" idle timeout, in seconds (0 = never).
        /// </summary>
        public static uint GetDisplayTimeoutSeconds(bool isAC)
        {
            Guid scheme = GetActiveScheme();
            Guid subgroup = PowerGuids.GUID_VIDEO_SUBGROUP;
            Guid setting = PowerGuids.GUID_VIDEO_IDLE;

            uint result;
            uint status = isAC
                ? PowrProf.PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out result)
                : PowrProf.PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out result);

            if (status != 0)
            {
                Logger.Error("Can't read Display Timeout.");
                return 600; // 10 minutes - Windows' own out-of-box default
            }

            return result;
        }

        /// <summary>
        /// Sets the "turn off display after" idle timeout, in seconds (0 = never).
        /// </summary>
        public static void SetDisplayTimeoutSeconds(bool isAC, uint seconds)
        {
            // Save original values before first modification (for clean uninstall)
            try
            {
                uint currentAC = GetDisplayTimeoutSeconds(true);
                uint currentDC = GetDisplayTimeoutSeconds(false);
                SystemRestoreService.SaveOriginalDisplayTimeout(currentAC, currentDC);
            }
            catch (Exception ex)
            {
                Logger.Warn($"Failed to save original Display Timeout values: {ex.Message}");
            }

            Guid scheme = GetActiveScheme();
            Guid subgroup = PowerGuids.GUID_VIDEO_SUBGROUP;
            Guid setting = PowerGuids.GUID_VIDEO_IDLE;

            uint status = isAC
                ? PowrProf.PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, seconds)
                : PowrProf.PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, seconds);

            if (status != 0)
            {
                Logger.Error("Can't set Display Timeout.");
                return;
            }

            Logger.Info($"Set Display Timeout {(isAC ? "AC" : "DC")} to {seconds}s.");
            PowrProf.PowerSetActiveScheme(IntPtr.Zero, ref scheme);
        }

        #endregion

        #region Sleep Timeout

        /// <summary>
        /// Gets the "put the device to sleep after" idle timeout, in seconds (0 = never).
        /// </summary>
        public static uint GetSleepTimeoutSeconds(bool isAC)
        {
            Guid scheme = GetActiveScheme();
            Guid subgroup = PowerGuids.GUID_SLEEP_SUBGROUP;
            Guid setting = PowerGuids.GUID_SLEEP_IDLE;

            uint result;
            uint status = isAC
                ? PowrProf.PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out result)
                : PowrProf.PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out result);

            if (status != 0)
            {
                Logger.Error("Can't read Sleep Timeout.");
                return 1800; // 30 minutes - Windows' own out-of-box default
            }

            return result;
        }

        /// <summary>
        /// Sets the "put the device to sleep after" idle timeout, in seconds (0 = never).
        /// </summary>
        public static void SetSleepTimeoutSeconds(bool isAC, uint seconds)
        {
            // Save original values before first modification (for clean uninstall)
            try
            {
                uint currentAC = GetSleepTimeoutSeconds(true);
                uint currentDC = GetSleepTimeoutSeconds(false);
                SystemRestoreService.SaveOriginalSleepTimeout(currentAC, currentDC);
            }
            catch (Exception ex)
            {
                Logger.Warn($"Failed to save original Sleep Timeout values: {ex.Message}");
            }

            Guid scheme = GetActiveScheme();
            Guid subgroup = PowerGuids.GUID_SLEEP_SUBGROUP;
            Guid setting = PowerGuids.GUID_SLEEP_IDLE;

            uint status = isAC
                ? PowrProf.PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, seconds)
                : PowrProf.PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, seconds);

            if (status != 0)
            {
                Logger.Error("Can't set Sleep Timeout.");
                return;
            }

            Logger.Info($"Set Sleep Timeout {(isAC ? "AC" : "DC")} to {seconds}s.");
            PowrProf.PowerSetActiveScheme(IntPtr.Zero, ref scheme);
        }

        /// <summary>
        /// Disables Windows' own idle-to-sleep timer for both AC and DC in one call - used
        /// by the System tab's "Disable Sleep Timer (AC+DC)" button so the GoTweaks
        /// Hibernate Timeout (see HibernateTimeoutProperty) is the only idle-power action
        /// left, instead of racing against Windows' Sleep timer.
        /// </summary>
        public static void DisableSleepTimers()
        {
            SetSleepTimeoutSeconds(true, 0);
            SetSleepTimeoutSeconds(false, 0);
        }

        #endregion
    }
}
