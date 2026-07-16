using NLog;
using System;
using System.Diagnostics;
using System.IO;
using System.ServiceProcess;
using System.Text.Json;
using XboxGamingBarHelper.Devices.Libraries.Legion;
using XboxGamingBarHelper.Power;
using XboxGamingBarHelper.Services;
using XboxGamingBarHelper.Systems;

namespace XboxGamingBarHelper.Services
{
    /// <summary>
    /// Service to save original system values before modification and restore them on uninstall.
    /// This ensures users can cleanly uninstall the app without leftover system changes.
    /// </summary>
    internal static class SystemRestoreService
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private static readonly string RestoreFilePath;
        private static SystemRestoreData _restoreData;
        private static bool _isLoaded = false;

        static SystemRestoreService()
        {
            // Store in LocalState folder
            string localStatePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Packages",
                "PlayandBuildCustom.10365195AA1EC_8edemd50ez3gg",
                "LocalState"
            );
            RestoreFilePath = Path.Combine(localStatePath, "system_restore_data.json");
        }

        /// <summary>
        /// Data class for storing original system values
        /// </summary>
        public class SystemRestoreData
        {
            public int Version { get; set; } = 1;
            public DateTime FirstRunDate { get; set; }

            // CPU Boost original values
            public bool? OriginalCpuBoostAC { get; set; }
            public bool? OriginalCpuBoostDC { get; set; }
            public bool CpuBoostSaved { get; set; } = false;

            // EPP original values
            public int? OriginalEppAC { get; set; }
            public int? OriginalEppDC { get; set; }
            public bool EppSaved { get; set; } = false;

            // DAService original state
            public bool? OriginalDAServiceEnabled { get; set; }
            public bool DAServiceSaved { get; set; } = false;

            // Max CPU State original values
            public uint? OriginalMaxCpuStateAC { get; set; }
            public uint? OriginalMaxCpuStateDC { get; set; }
            public bool MaxCpuStateSaved { get; set; } = false;

            // Min CPU State original values
            public uint? OriginalMinCpuStateAC { get; set; }
            public uint? OriginalMinCpuStateDC { get; set; }
            public bool MinCpuStateSaved { get; set; } = false;

            // OS Power Mode (power slider) original value
            public int? OriginalOsPowerMode { get; set; }
            public bool OsPowerModeSaved { get; set; } = false;

            // Power Button action original values
            public uint? OriginalPowerButtonActionAC { get; set; }
            public uint? OriginalPowerButtonActionDC { get; set; }
            public bool PowerButtonActionSaved { get; set; } = false;

            // Display Timeout original values (seconds)
            public uint? OriginalDisplayTimeoutAC { get; set; }
            public uint? OriginalDisplayTimeoutDC { get; set; }
            public bool DisplayTimeoutSaved { get; set; } = false;

            // Sleep Timeout original values (seconds) - only touched by the "Disable Sleep
            // Timer (AC+DC)" button, not by any synced property.
            public uint? OriginalSleepTimeoutAC { get; set; }
            public uint? OriginalSleepTimeoutDC { get; set; }
            public bool SleepTimeoutSaved { get; set; } = false;

            // Scheduled task was created
            public bool ScheduledTaskCreated { get; set; } = false;
        }

        /// <summary>
        /// Loads the restore data from disk, or creates new if doesn't exist.
        /// </summary>
        public static void Initialize()
        {
            if (_isLoaded) return;

            try
            {
                if (File.Exists(RestoreFilePath))
                {
                    string json = File.ReadAllText(RestoreFilePath);
                    _restoreData = JsonSerializer.Deserialize<SystemRestoreData>(json);
                    Logger.Info($"Loaded system restore data from {RestoreFilePath}");
                }
                else
                {
                    _restoreData = new SystemRestoreData
                    {
                        FirstRunDate = DateTime.Now
                    };
                    Logger.Info("Created new system restore data (first run)");
                }
                _isLoaded = true;
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to load system restore data: {ex.Message}");
                _restoreData = new SystemRestoreData
                {
                    FirstRunDate = DateTime.Now
                };
                _isLoaded = true;
            }
        }

        /// <summary>
        /// Saves the restore data to disk.
        /// </summary>
        private static void Save()
        {
            try
            {
                string directory = Path.GetDirectoryName(RestoreFilePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(_restoreData, options);
                File.WriteAllText(RestoreFilePath, json);
                Logger.Debug("Saved system restore data");
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to save system restore data: {ex.Message}");
            }
        }

        /// <summary>
        /// Saves the original CPU Boost values before first modification.
        /// Call this BEFORE changing CPU Boost for the first time.
        /// </summary>
        public static void SaveOriginalCpuBoost(bool currentAC, bool currentDC)
        {
            Initialize();

            if (_restoreData.CpuBoostSaved)
            {
                Logger.Debug("CPU Boost original values already saved, skipping");
                return;
            }

            _restoreData.OriginalCpuBoostAC = currentAC;
            _restoreData.OriginalCpuBoostDC = currentDC;
            _restoreData.CpuBoostSaved = true;
            Save();

            Logger.Info($"Saved original CPU Boost values: AC={currentAC}, DC={currentDC}");
        }

        /// <summary>
        /// Saves the original EPP values before first modification.
        /// Call this BEFORE changing EPP for the first time.
        /// </summary>
        public static void SaveOriginalEpp(int currentAC, int currentDC)
        {
            Initialize();

            if (_restoreData.EppSaved)
            {
                Logger.Debug("EPP original values already saved, skipping");
                return;
            }

            _restoreData.OriginalEppAC = currentAC;
            _restoreData.OriginalEppDC = currentDC;
            _restoreData.EppSaved = true;
            Save();

            Logger.Info($"Saved original EPP values: AC={currentAC}, DC={currentDC}");
        }

        /// <summary>
        /// Saves the original DAService state before first modification.
        /// </summary>
        public static void SaveOriginalDAServiceState(bool wasEnabled)
        {
            Initialize();

            if (_restoreData.DAServiceSaved)
            {
                Logger.Debug("DAService original state already saved, skipping");
                return;
            }

            _restoreData.OriginalDAServiceEnabled = wasEnabled;
            _restoreData.DAServiceSaved = true;
            Save();

            Logger.Info($"Saved original DAService state: Enabled={wasEnabled}");
        }

        /// <summary>
        /// One-time cleanup for the power-plan features retired in issue #103 ("don't touch
        /// the user's power plan"). Older builds wrote Max/Min processor state into the
        /// active scheme and could leave aggressive core-parking powercfg values behind.
        /// Runs at every startup, but only acts when a pre-#103 build left something to
        /// undo, then clears its markers so it never fires again.
        /// </summary>
        public static void RestoreRetiredSettings(SystemManager systemManager)
        {
            Initialize();

            // Max/Min processor state: write the snapshotted originals back. Old builds
            // wrote the E-core class-1 setting alongside the primary with the same value,
            // so the restore mirrors that.
            if (_restoreData.MaxCpuStateSaved || _restoreData.MinCpuStateSaved)
            {
                try
                {
                    if (_restoreData.MaxCpuStateSaved && _restoreData.OriginalMaxCpuStateAC.HasValue)
                    {
                        WriteProcessorStateValue(Windows.PowerGuids.GUID_PROCESSOR_THROTTLE_MAX, Windows.PowerGuids.GUID_PROCESSOR_THROTTLE_MAX1, true, _restoreData.OriginalMaxCpuStateAC.Value);
                        if (_restoreData.OriginalMaxCpuStateDC.HasValue)
                        {
                            WriteProcessorStateValue(Windows.PowerGuids.GUID_PROCESSOR_THROTTLE_MAX, Windows.PowerGuids.GUID_PROCESSOR_THROTTLE_MAX1, false, _restoreData.OriginalMaxCpuStateDC.Value);
                        }
                        Logger.Info($"Restored Max CPU State to original values (AC={_restoreData.OriginalMaxCpuStateAC}, DC={_restoreData.OriginalMaxCpuStateDC}) — feature retired");
                    }

                    if (_restoreData.MinCpuStateSaved && _restoreData.OriginalMinCpuStateAC.HasValue)
                    {
                        WriteProcessorStateValue(Windows.PowerGuids.GUID_PROCESSOR_THROTTLE_MIN, Windows.PowerGuids.GUID_PROCESSOR_THROTTLE_MIN1, true, _restoreData.OriginalMinCpuStateAC.Value);
                        if (_restoreData.OriginalMinCpuStateDC.HasValue)
                        {
                            WriteProcessorStateValue(Windows.PowerGuids.GUID_PROCESSOR_THROTTLE_MIN, Windows.PowerGuids.GUID_PROCESSOR_THROTTLE_MIN1, false, _restoreData.OriginalMinCpuStateDC.Value);
                        }
                        Logger.Info($"Restored Min CPU State to original values (AC={_restoreData.OriginalMinCpuStateAC}, DC={_restoreData.OriginalMinCpuStateDC}) — feature retired");
                    }

                    var scheme = PowerManager.GetActiveScheme();
                    Windows.PowrProf.PowerSetActiveScheme(IntPtr.Zero, ref scheme);

                    _restoreData.MaxCpuStateSaved = false;
                    _restoreData.MinCpuStateSaved = false;
                    _restoreData.OriginalMaxCpuStateAC = null;
                    _restoreData.OriginalMaxCpuStateDC = null;
                    _restoreData.OriginalMinCpuStateAC = null;
                    _restoreData.OriginalMinCpuStateDC = null;
                    Save();
                }
                catch (Exception ex)
                {
                    Logger.Error($"Failed to restore retired Max/Min CPU State values: {ex.Message}");
                }
            }

            // Core parking: the retired slider left aggressive CP* powercfg values in the
            // active scheme whenever the persisted selection implied parked cores. There
            // was never a snapshot for these, so the best we can do is a one-time reset to
            // the Windows defaults the old percent>=100 path used.
            try
            {
                if (!Settings.LocalSettingsHelper.TryGetValue("CoreParkingResetDone", out bool resetDone) || !resetDone)
                {
                    bool hadParking = false;
                    if (Settings.LocalSettingsHelper.TryGetValue("CoreParkingActiveCores", out int activeCores)
                        && activeCores < Environment.ProcessorCount)
                    {
                        hadParking = true;
                    }
                    // Hybrid CPUs derived the parking percent from the affinity selection
                    // (P-cores × 2 threads + E-cores, matching the widget's calculation).
                    if (Settings.LocalSettingsHelper.TryGetValue("ActivePCores", out int pCores)
                        && Settings.LocalSettingsHelper.TryGetValue("ActiveECores", out int eCores)
                        && (pCores * 2) + eCores < Environment.ProcessorCount)
                    {
                        hadParking = true;
                    }

                    if (hadParking && systemManager != null)
                    {
                        Logger.Info("Resetting core-parking powercfg values left by the retired Core Parking feature");
                        systemManager.ResetCoreParkingToDefaults();
                    }

                    Settings.LocalSettingsHelper.Remove("CoreParkingActiveCores");
                    Settings.LocalSettingsHelper.Remove("ActivePCores");
                    Settings.LocalSettingsHelper.Remove("ActiveECores");
                    Settings.LocalSettingsHelper.Remove("ForceParkMode");
                    Settings.LocalSettingsHelper.SetValue("CoreParkingResetDone", true);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to reset retired core-parking values: {ex.Message}");
            }
        }

        /// <summary>
        /// Writes a processor-state percentage into the active scheme for the given
        /// primary + efficiency-class-1 setting pair. Restore-path only.
        /// </summary>
        private static void WriteProcessorStateValue(Guid setting, Guid settingECore, bool isAC, uint percentage)
        {
            var scheme = PowerManager.GetActiveScheme();
            var subgroup = Windows.PowerGuids.GUID_PROCESSOR_SETTINGS_SUBGROUP;

            uint status = isAC
                ? Windows.PowrProf.PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, percentage)
                : Windows.PowrProf.PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, percentage);
            if (status != 0)
            {
                Logger.Warn($"Failed to write processor state {(isAC ? "AC" : "DC")} value {percentage}% (status {status})");
            }

            uint statusECore = isAC
                ? Windows.PowrProf.PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref settingECore, percentage)
                : Windows.PowrProf.PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref settingECore, percentage);
            if (statusECore != 0)
            {
                Logger.Debug($"Processor state E-core write skipped (status {statusECore}, may not have E-cores)");
            }
        }

        /// <summary>
        /// Saves the original OS Power Mode (power slider) value before first modification.
        /// Call this BEFORE changing the OS Power Mode for the first time.
        /// </summary>
        public static void SaveOriginalOsPowerMode(int currentMode)
        {
            Initialize();

            if (_restoreData.OsPowerModeSaved)
            {
                Logger.Debug("OS Power Mode original value already saved, skipping");
                return;
            }

            _restoreData.OriginalOsPowerMode = currentMode;
            _restoreData.OsPowerModeSaved = true;
            Save();

            Logger.Info($"Saved original OS Power Mode value: {currentMode}");
        }

        /// <summary>
        /// Saves the original Power Button action values before first modification.
        /// Call this BEFORE changing the Power Button action for the first time.
        /// </summary>
        public static void SaveOriginalPowerButtonAction(uint currentAC, uint currentDC)
        {
            Initialize();

            if (_restoreData.PowerButtonActionSaved)
            {
                Logger.Debug("Power Button action original values already saved, skipping");
                return;
            }

            _restoreData.OriginalPowerButtonActionAC = currentAC;
            _restoreData.OriginalPowerButtonActionDC = currentDC;
            _restoreData.PowerButtonActionSaved = true;
            Save();

            Logger.Info($"Saved original Power Button action values: AC={currentAC}, DC={currentDC}");
        }

        /// <summary>
        /// Saves the original Display Timeout values before first modification.
        /// Call this BEFORE changing the Display Timeout for the first time.
        /// </summary>
        public static void SaveOriginalDisplayTimeout(uint currentAC, uint currentDC)
        {
            Initialize();

            if (_restoreData.DisplayTimeoutSaved)
            {
                Logger.Debug("Display Timeout original values already saved, skipping");
                return;
            }

            _restoreData.OriginalDisplayTimeoutAC = currentAC;
            _restoreData.OriginalDisplayTimeoutDC = currentDC;
            _restoreData.DisplayTimeoutSaved = true;
            Save();

            Logger.Info($"Saved original Display Timeout values: AC={currentAC}s, DC={currentDC}s");
        }

        /// <summary>
        /// Saves the original Sleep Timeout values before first modification.
        /// Call this BEFORE changing the Sleep Timeout for the first time.
        /// </summary>
        public static void SaveOriginalSleepTimeout(uint currentAC, uint currentDC)
        {
            Initialize();

            if (_restoreData.SleepTimeoutSaved)
            {
                Logger.Debug("Sleep Timeout original values already saved, skipping");
                return;
            }

            _restoreData.OriginalSleepTimeoutAC = currentAC;
            _restoreData.OriginalSleepTimeoutDC = currentDC;
            _restoreData.SleepTimeoutSaved = true;
            Save();

            Logger.Info($"Saved original Sleep Timeout values: AC={currentAC}s, DC={currentDC}s");
        }
        /// <summary>
        /// Marks that a scheduled task was created.
        /// </summary>
        public static void MarkScheduledTaskCreated()
        {
            Initialize();
            _restoreData.ScheduledTaskCreated = true;
            Save();
        }

        /// <summary>
        /// Prepares the system for uninstall by reverting all changes.
        /// </summary>
        /// <param name="legionManager">Optional - releases the EC fan override if a custom fan curve is active.</param>
        /// <param name="systemManager">Optional - re-enables the touchscreen if GoTweaks disabled it.</param>
        /// <param name="viiperManager">Optional - stops any live VIIPER emulation session (usbip detach, HidHide, virtual pad teardown).</param>
        /// <returns>A summary of actions taken</returns>
        public static string PrepareForUninstall(
            LegionManager legionManager = null,
            SystemManager systemManager = null,
            XboxGamingBarHelper.ControllerEmulation.Viiper.ViiperEmulationManager viiperManager = null)
        {
            Initialize();

            var results = new System.Text.StringBuilder();
            results.AppendLine("=== Prepare for Uninstall ===");
            results.AppendLine();

            // 1. Remove scheduled task
            try
            {
                Logger.Info("Uninstall: Removing scheduled task...");
                ScheduledTaskService.RemoveTask();
                ScheduledTaskService.RemoveLegacyTaskIfExists();
                results.AppendLine("✓ Scheduled task removed");
            }
            catch (Exception ex)
            {
                Logger.Error($"Uninstall: Failed to remove scheduled task: {ex.Message}");
                results.AppendLine($"✗ Failed to remove scheduled task: {ex.Message}");
            }

            // 2. Restore CPU Boost
            if (_restoreData.CpuBoostSaved && _restoreData.OriginalCpuBoostAC.HasValue)
            {
                try
                {
                    Logger.Info($"Uninstall: Restoring CPU Boost to AC={_restoreData.OriginalCpuBoostAC}, DC={_restoreData.OriginalCpuBoostDC}");
                    PowerManager.SetCpuBoostMode(true, _restoreData.OriginalCpuBoostAC.Value);
                    if (_restoreData.OriginalCpuBoostDC.HasValue)
                    {
                        PowerManager.SetCpuBoostMode(false, _restoreData.OriginalCpuBoostDC.Value);
                    }
                    results.AppendLine($"✓ CPU Boost restored to: AC={_restoreData.OriginalCpuBoostAC}, DC={_restoreData.OriginalCpuBoostDC}");
                }
                catch (Exception ex)
                {
                    Logger.Error($"Uninstall: Failed to restore CPU Boost: {ex.Message}");
                    results.AppendLine($"✗ Failed to restore CPU Boost: {ex.Message}");
                }
            }
            else
            {
                results.AppendLine("- CPU Boost: No original value saved (was not modified)");
            }

            // 3. Restore EPP
            if (_restoreData.EppSaved && _restoreData.OriginalEppAC.HasValue)
            {
                try
                {
                    Logger.Info($"Uninstall: Restoring EPP to AC={_restoreData.OriginalEppAC}, DC={_restoreData.OriginalEppDC}");
                    PowerManager.SetEppValue(true, (uint)_restoreData.OriginalEppAC.Value);
                    if (_restoreData.OriginalEppDC.HasValue)
                    {
                        PowerManager.SetEppValue(false, (uint)_restoreData.OriginalEppDC.Value);
                    }
                    results.AppendLine($"✓ EPP restored to: AC={_restoreData.OriginalEppAC}, DC={_restoreData.OriginalEppDC}");
                }
                catch (Exception ex)
                {
                    Logger.Error($"Uninstall: Failed to restore EPP: {ex.Message}");
                    results.AppendLine($"✗ Failed to restore EPP: {ex.Message}");
                }
            }
            else
            {
                results.AppendLine("- EPP: No original value saved (was not modified)");
            }

            // 4. Re-enable DAService if it was originally enabled
            if (_restoreData.DAServiceSaved)
            {
                if (_restoreData.OriginalDAServiceEnabled == true)
                {
                    try
                    {
                        Logger.Info("Uninstall: Re-enabling DAService (Legion Space)...");

                        // First enable the service startup type using sc.exe
                        var enableProcess = new Process
                        {
                            StartInfo = new ProcessStartInfo
                            {
                                FileName = "sc.exe",
                                Arguments = "config DAService start= auto",
                                UseShellExecute = false,
                                CreateNoWindow = true,
                                RedirectStandardOutput = true
                            }
                        };
                        enableProcess.Start();
                        enableProcess.WaitForExit(5000);
                        Logger.Info($"Uninstall: DAService startup enabled (exit code: {enableProcess.ExitCode})");

                        // Then start the service
                        using (var sc = new ServiceController("DAService"))
                        {
                            if (sc.Status == ServiceControllerStatus.Stopped)
                            {
                                Logger.Info("Uninstall: Starting DAService...");
                                sc.Start();
                                sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(10));
                                Logger.Info("Uninstall: DAService started");
                            }
                        }

                        results.AppendLine("✓ DAService (Legion Space) re-enabled and started");
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Uninstall: Failed to re-enable DAService: {ex.Message}");
                        results.AppendLine($"✗ Failed to re-enable DAService: {ex.Message}");
                    }
                }
                else
                {
                    results.AppendLine("- DAService: Was already disabled before app install");
                }
            }
            else
            {
                results.AppendLine("- DAService: No original state saved (was not modified)");
            }

            // Max/Min CPU State and core parking are retired features (#103); any values an
            // older build wrote were already restored by RestoreRetiredSettings at startup.

            // 5. Restore OS Power Mode (power slider)
            if (_restoreData.OsPowerModeSaved && _restoreData.OriginalOsPowerMode.HasValue)
            {
                try
                {
                    Logger.Info($"Uninstall: Restoring OS Power Mode to {_restoreData.OriginalOsPowerMode}");
                    PowerManager.SetOSPowerMode(_restoreData.OriginalOsPowerMode.Value);
                    results.AppendLine($"✓ OS Power Mode restored to: {_restoreData.OriginalOsPowerMode}");
                }
                catch (Exception ex)
                {
                    Logger.Error($"Uninstall: Failed to restore OS Power Mode: {ex.Message}");
                    results.AppendLine($"✗ Failed to restore OS Power Mode: {ex.Message}");
                }
            }
            else
            {
                results.AppendLine("- OS Power Mode: No original value saved (was not modified)");
            }

            // 6. Restore Power Button action
            if (_restoreData.PowerButtonActionSaved && _restoreData.OriginalPowerButtonActionAC.HasValue)
            {
                try
                {
                    Logger.Info($"Uninstall: Restoring Power Button action to AC={_restoreData.OriginalPowerButtonActionAC}, DC={_restoreData.OriginalPowerButtonActionDC}");
                    PowerManager.SetPowerButtonAction(true, _restoreData.OriginalPowerButtonActionAC.Value);
                    if (_restoreData.OriginalPowerButtonActionDC.HasValue)
                    {
                        PowerManager.SetPowerButtonAction(false, _restoreData.OriginalPowerButtonActionDC.Value);
                    }
                    results.AppendLine($"✓ Power Button action restored to: AC={_restoreData.OriginalPowerButtonActionAC}, DC={_restoreData.OriginalPowerButtonActionDC}");
                }
                catch (Exception ex)
                {
                    Logger.Error($"Uninstall: Failed to restore Power Button action: {ex.Message}");
                    results.AppendLine($"✗ Failed to restore Power Button action: {ex.Message}");
                }
            }
            else
            {
                results.AppendLine("- Power Button action: No original value saved (was not modified)");
            }

            // 7. Restore Display Timeout
            if (_restoreData.DisplayTimeoutSaved && _restoreData.OriginalDisplayTimeoutAC.HasValue)
            {
                try
                {
                    Logger.Info($"Uninstall: Restoring Display Timeout to AC={_restoreData.OriginalDisplayTimeoutAC}s, DC={_restoreData.OriginalDisplayTimeoutDC}s");
                    PowerManager.SetDisplayTimeoutSeconds(true, _restoreData.OriginalDisplayTimeoutAC.Value);
                    if (_restoreData.OriginalDisplayTimeoutDC.HasValue)
                    {
                        PowerManager.SetDisplayTimeoutSeconds(false, _restoreData.OriginalDisplayTimeoutDC.Value);
                    }
                    results.AppendLine($"✓ Display Timeout restored to: AC={_restoreData.OriginalDisplayTimeoutAC}s, DC={_restoreData.OriginalDisplayTimeoutDC}s");
                }
                catch (Exception ex)
                {
                    Logger.Error($"Uninstall: Failed to restore Display Timeout: {ex.Message}");
                    results.AppendLine($"✗ Failed to restore Display Timeout: {ex.Message}");
                }
            }
            else
            {
                results.AppendLine("- Display Timeout: No original value saved (was not modified)");
            }

            // 8. Restore Sleep Timeout
            if (_restoreData.SleepTimeoutSaved && _restoreData.OriginalSleepTimeoutAC.HasValue)
            {
                try
                {
                    Logger.Info($"Uninstall: Restoring Sleep Timeout to AC={_restoreData.OriginalSleepTimeoutAC}s, DC={_restoreData.OriginalSleepTimeoutDC}s");
                    PowerManager.SetSleepTimeoutSeconds(true, _restoreData.OriginalSleepTimeoutAC.Value);
                    if (_restoreData.OriginalSleepTimeoutDC.HasValue)
                    {
                        PowerManager.SetSleepTimeoutSeconds(false, _restoreData.OriginalSleepTimeoutDC.Value);
                    }
                    results.AppendLine($"✓ Sleep Timeout restored to: AC={_restoreData.OriginalSleepTimeoutAC}s, DC={_restoreData.OriginalSleepTimeoutDC}s");
                }
                catch (Exception ex)
                {
                    Logger.Error($"Uninstall: Failed to restore Sleep Timeout: {ex.Message}");
                    results.AppendLine($"✗ Failed to restore Sleep Timeout: {ex.Message}");
                }
            }
            else
            {
                results.AppendLine("- Sleep Timeout: No original value saved (was not modified)");
            }

            // 9. Re-enable the touchscreen if GoTweaks disabled it (SetupAPI device state
            // persists across reboots and survives an uninstall until manually reverted).
            if (systemManager != null)
            {
                try
                {
                    if (systemManager.TouchscreenEnabled != null && systemManager.TouchscreenEnabled.Value == false)
                    {
                        Logger.Info("Uninstall: Re-enabling touchscreen...");
                        systemManager.SetTouchscreenEnabled(true);
                        results.AppendLine("✓ Touchscreen re-enabled");
                    }
                    else
                    {
                        results.AppendLine("- Touchscreen: Already enabled (was not modified)");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"Uninstall: Failed to re-enable touchscreen: {ex.Message}");
                    results.AppendLine($"✗ Failed to re-enable touchscreen: {ex.Message}");
                }
            }

            // 10. Release the EC fan override (register 0xC6C8) if a custom fan curve is
            // active, handing fan control back to Lenovo firmware. Otherwise a stuck RPM
            // survives helper shutdown - the crash-safety hooks only cover process exit,
            // not this in-app "prepare for uninstall" flow.
            if (legionManager != null)
            {
                try
                {
                    Logger.Info("Uninstall: Releasing EC fan override...");
                    legionManager.StopEcFanCurveLoop();
                    results.AppendLine("✓ EC fan override released (firmware fan control restored)");
                }
                catch (Exception ex)
                {
                    Logger.Error($"Uninstall: Failed to release EC fan override: {ex.Message}");
                    results.AppendLine($"✗ Failed to release EC fan override: {ex.Message}");
                }
            }

            // 11. Stop any live VIIPER emulation session (usbip detach, HidHide suppression,
            // virtual pad teardown) before the controller-related cleanup below.
            if (viiperManager != null)
            {
                try
                {
                    Logger.Info("Uninstall: Stopping VIIPER emulation...");
                    viiperManager.Stop();
                    results.AppendLine("✓ VIIPER emulation stopped");
                }
                catch (Exception ex)
                {
                    Logger.Error($"Uninstall: Failed to stop VIIPER emulation: {ex.Message}");
                    results.AppendLine($"✗ Failed to stop VIIPER emulation: {ex.Message}");
                }
            }

            // 12. Clear our HidHide cloaking rules (blocked device IDs + registered app
            // paths) so no controller stays hidden from other apps after uninstall.
            try
            {
                Logger.Info("Uninstall: Restoring HidHide state...");
                UninstallService.RestoreHidHide();
                results.AppendLine("✓ HidHide cloaking rules cleared");
            }
            catch (Exception ex)
            {
                Logger.Error($"Uninstall: Failed to restore HidHide state: {ex.Message}");
                results.AppendLine($"✗ Failed to restore HidHide state: {ex.Message}");
            }

            // 13. Sweep any orphaned VIIPER/ViGEm phantom virtual pads left behind by a
            // prior crash or ungraceful shutdown.
            try
            {
                Logger.Info("Uninstall: Sweeping phantom virtual pads...");
                ControllerEmulation.Viiper.ViiperPnpCleanup.CleanupPresentViiperPhantomsBlocking();
                ControllerEmulation.Viiper.ViiperPnpCleanup.CleanupPresentVigemPhantomsBlocking();
                ControllerEmulation.Viiper.ViiperPnpCleanup.CleanupAllKnownGhosts();
                results.AppendLine("✓ Phantom virtual pads swept");
            }
            catch (Exception ex)
            {
                Logger.Error($"Uninstall: Failed to sweep phantom virtual pads: {ex.Message}");
                results.AppendLine($"✗ Failed to sweep phantom virtual pads: {ex.Message}");
            }

            results.AppendLine();
            results.AppendLine("Uninstall preparation complete.");
            results.AppendLine("You can now uninstall the app from Windows Settings.");

            string resultText = results.ToString();
            Logger.Info(resultText);

            return resultText;
        }

        /// <summary>
        /// Gets a summary of what original values are saved.
        /// </summary>
        public static string GetSavedValuesStatus()
        {
            Initialize();

            var status = new System.Text.StringBuilder();
            status.AppendLine("Saved Original Values:");

            if (_restoreData.CpuBoostSaved)
                status.AppendLine($"  CPU Boost: AC={_restoreData.OriginalCpuBoostAC}, DC={_restoreData.OriginalCpuBoostDC}");
            else
                status.AppendLine("  CPU Boost: Not saved");

            if (_restoreData.EppSaved)
                status.AppendLine($"  EPP: AC={_restoreData.OriginalEppAC}, DC={_restoreData.OriginalEppDC}");
            else
                status.AppendLine("  EPP: Not saved");

            if (_restoreData.DAServiceSaved)
                status.AppendLine($"  DAService: Originally enabled={_restoreData.OriginalDAServiceEnabled}");
            else
                status.AppendLine("  DAService: Not saved");

            status.AppendLine($"  Scheduled Task Created: {_restoreData.ScheduledTaskCreated}");
            status.AppendLine($"  First Run: {_restoreData.FirstRunDate}");

            return status.ToString();
        }

        /// <summary>
        /// Checks if any original values have been saved.
        /// </summary>
        public static bool HasSavedValues()
        {
            Initialize();
            return _restoreData.CpuBoostSaved || _restoreData.EppSaved || _restoreData.DAServiceSaved || _restoreData.PowerButtonActionSaved || _restoreData.DisplayTimeoutSaved || _restoreData.SleepTimeoutSaved;
        }
    }
}
