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

        private readonly MaxCPUStateProperty maxCPUState;
        public MaxCPUStateProperty MaxCPUState
        {
            get { return maxCPUState; }
        }

        private readonly MinCPUStateProperty minCPUState;
        public MinCPUStateProperty MinCPUState
        {
            get { return minCPUState; }
        }

        private readonly OSPowerModeProperty osPowerMode;
        public OSPowerModeProperty OSPowerMode
        {
            get { return osPowerMode; }
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

            // CPU State limits (percentage)
            var initialMaxCPUState = GetMaxCPUState(false);
            var initialMinCPUState = GetMinCPUState(false);
            Logger.Info($"Initial CPU State: Min={initialMinCPUState}%, Max={initialMaxCPUState}%");
            maxCPUState = new MaxCPUStateProperty((int)initialMaxCPUState, this);
            minCPUState = new MinCPUStateProperty((int)initialMinCPUState, this);

            // OS Power Mode
            var initialPowerMode = GetOSPowerMode();
            Logger.Info($"Initial OS Power Mode: {initialPowerMode} (0=Efficiency, 1=Balanced, 2=Performance)");
            osPowerMode = new OSPowerModeProperty(initialPowerMode >= 0 ? initialPowerMode : 1, this);

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

        /// <summary>
        /// Reads the CPU frequency limit (in MHz) for AC or DC mode.
        /// </summary>
        public static uint GetCpuFreqLimit(bool isAC, bool isSecondary = false)
        {
            Guid scheme = GetActiveScheme();
            Guid subgroup = PowerGuids.GUID_PROCESSOR_SETTINGS_SUBGROUP;
            Guid setting = isSecondary
                ? PowerGuids.GUID_PROCESSOR_FREQUENCY_LIMIT1
                : PowerGuids.GUID_PROCESSOR_FREQUENCY_LIMIT;

            uint result;
            uint status = isAC
                ? PowrProf.PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out result)
                : PowrProf.PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out result);

            if (status != 0)
            {
                Logger.Error("Can't read CPU Clock limit.");
                return 1000;
            }

            return result;
        }

        /// <summary>
        /// Sets the CPU frequency limit (in MHz) for AC or DC mode.
        /// </summary>
        public static void SetCpuFreqLimit(bool isAC, uint mhzValue, bool isSecondary = false)
        {
            Guid scheme = GetActiveScheme();
            Guid subgroup = PowerGuids.GUID_PROCESSOR_SETTINGS_SUBGROUP;
            Guid setting = isSecondary
                ? PowerGuids.GUID_PROCESSOR_FREQUENCY_LIMIT1
                : PowerGuids.GUID_PROCESSOR_FREQUENCY_LIMIT;

            uint status = isAC
                ? PowrProf.PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, mhzValue)
                : PowrProf.PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, mhzValue);

            if (status != 0)
            {
                Logger.Error("Can't set CPU Clock limit.");
                return;
            }

            Logger.Info($"Set CPU Clock limit {(isAC ? "AC" : "DC")} {(isSecondary ? "secondary" : "primary")} to {mhzValue}MHz");
            PowrProf.PowerSetActiveScheme(IntPtr.Zero, ref scheme);
        }

        /// <summary>
        /// Gets the maximum processor state (percentage 0-100).
        /// </summary>
        public static uint GetMaxCPUState(bool isAC)
        {
            Guid scheme = GetActiveScheme();
            Guid subgroup = PowerGuids.GUID_PROCESSOR_SETTINGS_SUBGROUP;
            Guid setting = PowerGuids.GUID_PROCESSOR_THROTTLE_MAX;

            uint result;
            uint status = isAC
                ? PowrProf.PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out result)
                : PowrProf.PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out result);

            if (status != 0)
            {
                Logger.Error("Can't read Maximum CPU State.");
                return 100;
            }

            return result;
        }

        /// <summary>
        /// Sets the maximum processor state (percentage 0-100).
        /// Also sets the E-core (efficiency core) max state on hybrid processors.
        /// </summary>
        public static void SetMaxCPUState(bool isAC, uint percentage)
        {
            // Save original values before first modification (for clean uninstall)
            try
            {
                uint currentAC = GetMaxCPUState(true);
                uint currentDC = GetMaxCPUState(false);
                SystemRestoreService.SaveOriginalMaxCpuState(currentAC, currentDC);
            }
            catch (Exception ex)
            {
                Logger.Warn($"Failed to save original Max CPU State values: {ex.Message}");
            }

            Guid scheme = GetActiveScheme();
            Guid subgroup = PowerGuids.GUID_PROCESSOR_SETTINGS_SUBGROUP;
            Guid setting = PowerGuids.GUID_PROCESSOR_THROTTLE_MAX;

            uint status = isAC
                ? PowrProf.PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, percentage)
                : PowrProf.PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, percentage);

            if (status != 0)
            {
                Logger.Error("Can't set Maximum CPU State.");
                return;
            }

            Logger.Info($"Set Maximum CPU State {(isAC ? "AC" : "DC")} to {percentage}%");

            // Also set E-core (efficiency core) max state on hybrid processors
            Guid settingECore = PowerGuids.GUID_PROCESSOR_THROTTLE_MAX1;
            uint statusECore = isAC
                ? PowrProf.PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref settingECore, percentage)
                : PowrProf.PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref settingECore, percentage);

            if (statusECore == 0)
            {
                Logger.Info($"Set Maximum CPU State (E-cores) {(isAC ? "AC" : "DC")} to {percentage}%");
            }
            else
            {
                Logger.Warn($"Failed to set Maximum CPU State (E-cores) {(isAC ? "AC" : "DC")} - status: {statusECore} (may not have E-cores)");
            }

            PowrProf.PowerSetActiveScheme(IntPtr.Zero, ref scheme);
        }

        /// <summary>
        /// Gets the minimum processor state (percentage 0-100).
        /// </summary>
        public static uint GetMinCPUState(bool isAC)
        {
            Guid scheme = GetActiveScheme();
            Guid subgroup = PowerGuids.GUID_PROCESSOR_SETTINGS_SUBGROUP;
            Guid setting = PowerGuids.GUID_PROCESSOR_THROTTLE_MIN;

            uint result;
            uint status = isAC
                ? PowrProf.PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out result)
                : PowrProf.PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out result);

            if (status != 0)
            {
                Logger.Error("Can't read Minimum CPU State.");
                return 5;
            }

            return result;
        }

        /// <summary>
        /// Sets the minimum processor state (percentage 0-100).
        /// Also sets the E-core (efficiency core) min state on hybrid processors.
        /// </summary>
        public static void SetMinCPUState(bool isAC, uint percentage)
        {
            // Save original values before first modification (for clean uninstall)
            try
            {
                uint currentAC = GetMinCPUState(true);
                uint currentDC = GetMinCPUState(false);
                SystemRestoreService.SaveOriginalMinCpuState(currentAC, currentDC);
            }
            catch (Exception ex)
            {
                Logger.Warn($"Failed to save original Min CPU State values: {ex.Message}");
            }

            Guid scheme = GetActiveScheme();
            Guid subgroup = PowerGuids.GUID_PROCESSOR_SETTINGS_SUBGROUP;
            Guid setting = PowerGuids.GUID_PROCESSOR_THROTTLE_MIN;

            uint status = isAC
                ? PowrProf.PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, percentage)
                : PowrProf.PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, percentage);

            if (status != 0)
            {
                Logger.Error("Can't set Minimum CPU State.");
                return;
            }

            Logger.Info($"Set Minimum CPU State {(isAC ? "AC" : "DC")} to {percentage}%");

            // Also set E-core (efficiency core) min state on hybrid processors
            Guid settingECore = PowerGuids.GUID_PROCESSOR_THROTTLE_MIN1;
            uint statusECore = isAC
                ? PowrProf.PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref settingECore, percentage)
                : PowrProf.PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref settingECore, percentage);

            if (statusECore == 0)
            {
                Logger.Info($"Set Minimum CPU State (E-cores) {(isAC ? "AC" : "DC")} to {percentage}%");
            }
            else
            {
                Logger.Warn($"Failed to set Minimum CPU State (E-cores) {(isAC ? "AC" : "DC")} - status: {statusECore} (may not have E-cores)");
            }

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

        #region Energy Saver

        /// <summary>
        /// Gets whether Energy Saver is currently enabled using powercfg.
        /// </summary>
        public static bool GetEnergySaverEnabled()
        {
            try
            {
                // Use powercfg to query Energy Saver threshold
                var process = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "powercfg",
                        Arguments = "/query SCHEME_CURRENT de830923-a562-41af-a086-e3a2c6bad2da e69653ca-cf7f-4f05-aa73-cb833fa90ad4",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    }
                };
                process.Start();
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                // Parse output to find current setting value
                // Look for "Current DC Power Setting Index:" or "Current AC Power Setting Index:"
                // Value of 0x00000064 (100) means always on, 0x00000000 means never
                bool isEnabled = false;
                foreach (var line in output.Split('\n'))
                {
                    if (line.Contains("Current") && line.Contains("Power Setting Index"))
                    {
                        // Extract hex value
                        var match = System.Text.RegularExpressions.Regex.Match(line, @"0x([0-9a-fA-F]+)");
                        if (match.Success)
                        {
                            uint value = Convert.ToUInt32(match.Groups[1].Value, 16);
                            if (value >= 100)
                            {
                                isEnabled = true;
                                break;
                            }
                        }
                    }
                }

                Logger.Debug($"Energy Saver enabled: {isEnabled}");
                return isEnabled;
            }
            catch (Exception ex)
            {
                Logger.Error($"Error getting Energy Saver status: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Toggles Energy Saver on or off using powercfg.
        /// Sets both AC and DC values for the Energy Saver battery threshold.
        /// </summary>
        public static bool SetEnergySaverEnabled(bool enabled)
        {
            try
            {
                // 100 = always on (0x64), 0 = never
                string value = enabled ? "100" : "0";
                string subgroup = "de830923-a562-41af-a086-e3a2c6bad2da"; // Energy Saver subgroup
                string setting = "e69653ca-cf7f-4f05-aa73-cb833fa90ad4";  // Battery threshold setting

                // Set DC value (battery mode)
                var processDC = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "powercfg",
                        Arguments = $"/setdcvalueindex SCHEME_CURRENT {subgroup} {setting} {value}",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                processDC.Start();
                processDC.WaitForExit();

                // Set AC value (plugged in mode)
                var processAC = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "powercfg",
                        Arguments = $"/setacvalueindex SCHEME_CURRENT {subgroup} {setting} {value}",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                processAC.Start();
                processAC.WaitForExit();

                // Apply the change
                var processApply = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "powercfg",
                        Arguments = "/setactive SCHEME_CURRENT",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                processApply.Start();
                processApply.WaitForExit();

                Logger.Info($"Energy Saver {(enabled ? "enabled" : "disabled")} (threshold: {value}% for both AC and DC)");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"Error setting Energy Saver: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Toggles Energy Saver state.
        /// </summary>
        public static bool ToggleEnergySaver()
        {
            bool currentState = GetEnergySaverEnabled();
            return SetEnergySaverEnabled(!currentState);
        }

        #endregion

        #region Power Plan Management

        /// <summary>
        /// Represents a Windows power plan
        /// </summary>
        public class PowerPlan
        {
            public Guid Guid { get; set; }
            public string Name { get; set; }

            public override string ToString() => Name;
        }

        /// <summary>
        /// Enumerates all available power plans on the system.
        /// </summary>
        public static System.Collections.Generic.List<PowerPlan> GetPowerPlans()
        {
            var plans = new System.Collections.Generic.List<PowerPlan>();

            try
            {
                uint index = 0;
                uint bufferSize = 16; // GUID size
                IntPtr buffer = Marshal.AllocHGlobal((int)bufferSize);

                try
                {
                    while (true)
                    {
                        bufferSize = 16;
                        uint result = PowrProf.PowerEnumerate(
                            IntPtr.Zero,
                            IntPtr.Zero,
                            IntPtr.Zero,
                            PowrProf.ACCESS_SCHEME,
                            index,
                            buffer,
                            ref bufferSize);

                        if (result != 0)
                            break; // No more power plans

                        Guid schemeGuid = (Guid)Marshal.PtrToStructure(buffer, typeof(Guid));
                        string friendlyName = GetPowerPlanName(schemeGuid);

                        plans.Add(new PowerPlan { Guid = schemeGuid, Name = friendlyName });
                        Logger.Debug($"Found power plan: {friendlyName} ({schemeGuid})");

                        index++;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error enumerating power plans: {ex.Message}");
            }

            return plans;
        }

        /// <summary>
        /// Gets the friendly name of a power plan by its GUID.
        /// </summary>
        public static string GetPowerPlanName(Guid schemeGuid)
        {
            try
            {
                uint bufferSize = 0;

                // First call to get required buffer size
                PowrProf.PowerReadFriendlyName(
                    IntPtr.Zero,
                    ref schemeGuid,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    ref bufferSize);

                if (bufferSize == 0)
                    return schemeGuid.ToString();

                IntPtr buffer = Marshal.AllocHGlobal((int)bufferSize);
                try
                {
                    uint result = PowrProf.PowerReadFriendlyName(
                        IntPtr.Zero,
                        ref schemeGuid,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        buffer,
                        ref bufferSize);

                    if (result == 0)
                    {
                        return Marshal.PtrToStringUni(buffer);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error getting power plan name: {ex.Message}");
            }

            return schemeGuid.ToString();
        }

        /// <summary>
        /// Gets the currently active power plan GUID.
        /// </summary>
        public static Guid GetActivePowerPlan()
        {
            return GetActiveScheme();
        }

        /// <summary>
        /// Sets the active power plan by GUID.
        /// </summary>
        public static bool SetActivePowerPlan(Guid planGuid)
        {
            try
            {
                uint result = PowrProf.PowerSetActiveScheme(IntPtr.Zero, ref planGuid);
                if (result == 0)
                {
                    Logger.Info($"Set active power plan to {planGuid}");
                    return true;
                }
                else
                {
                    Logger.Error($"Failed to set power plan, error code: {result}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error setting power plan: {ex.Message}");
                return false;
            }
        }

        #endregion
    }
}
