using LibreHardwareMonitor.Hardware;
using NLog;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.System.Power;
using Shared.Enums;
using XboxGamingBarHelper.Core;
using XboxGamingBarHelper.Devices.Libraries.Legion;
using XboxGamingBarHelper.PawnIO;
using XboxGamingBarHelper.Performance.Sensors;
using XboxGamingBarHelper.Settings;

namespace XboxGamingBarHelper.Performance
{
    internal class HardwareSensors : IDictionary<string, HardwareSensor>
    {
        HardwareSensor IDictionary<string, HardwareSensor>.this[string key] { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

        ICollection<string> IDictionary<string, HardwareSensor>.Keys => throw new NotImplementedException();

        ICollection<HardwareSensor> IDictionary<string, HardwareSensor>.Values => throw new NotImplementedException();

        int ICollection<KeyValuePair<string, HardwareSensor>>.Count => throw new NotImplementedException();

        bool ICollection<KeyValuePair<string, HardwareSensor>>.IsReadOnly => throw new NotImplementedException();

        void IDictionary<string, HardwareSensor>.Add(string key, HardwareSensor value)
        {
            throw new NotImplementedException();
        }

        void ICollection<KeyValuePair<string, HardwareSensor>>.Add(KeyValuePair<string, HardwareSensor> item)
        {
            throw new NotImplementedException();
        }

        void ICollection<KeyValuePair<string, HardwareSensor>>.Clear()
        {
            throw new NotImplementedException();
        }

        bool ICollection<KeyValuePair<string, HardwareSensor>>.Contains(KeyValuePair<string, HardwareSensor> item)
        {
            throw new NotImplementedException();
        }

        bool IDictionary<string, HardwareSensor>.ContainsKey(string key)
        {
            throw new NotImplementedException();
        }

        void ICollection<KeyValuePair<string, HardwareSensor>>.CopyTo(KeyValuePair<string, HardwareSensor>[] array, int arrayIndex)
        {
            throw new NotImplementedException();
        }

        IEnumerator<KeyValuePair<string, HardwareSensor>> IEnumerable<KeyValuePair<string, HardwareSensor>>.GetEnumerator()
        {
            throw new NotImplementedException();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            throw new NotImplementedException();
        }

        bool IDictionary<string, HardwareSensor>.Remove(string key)
        {
            throw new NotImplementedException();
        }

        bool ICollection<KeyValuePair<string, HardwareSensor>>.Remove(KeyValuePair<string, HardwareSensor> item)
        {
            throw new NotImplementedException();
        }

        bool IDictionary<string, HardwareSensor>.TryGetValue(string key, out HardwareSensor value)
        {
            throw new NotImplementedException();
        }
    }

    internal class PerformanceManager : Manager
    {
        // Native methods for fast PawnIO driver detection
        private const string PAWNIO_DEVICE_PATH = @"\\.\PawnIO";
        private const uint GENERIC_READ = 0x80000000;
        private const uint FILE_SHARE_READ = 0x00000001;
        private const uint OPEN_EXISTING = 3;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFile(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        private Computer computer;
        private IVisitor updateVisitor;
        private IntPtr ryzenAdjHandle;
        public IntPtr RyzenAdjHandle
        {
            get { return ryzenAdjHandle; }
        }

        public CPUUsageSensor CPUUsage { get; }
        public CPUClockSensor CPUClock { get; }
        // Wattage typed as base HardwareSensor so we can swap CPU<->GPU
        // sources on devices where LibreHardwareMonitor reports them swapped
        // (e.g. LeGo2 / Ryzen Z2 Extreme — see PerformanceManager constructor).
        public HardwareSensor CPUWattage { get; private set; }
        public CPUTemperatureSensor CPUTemperature { get; }
        public VRMTemperatureSensor VRMTemperature { get; }

        public GPUUsageSensor GPUUsage { get; }
        public GPUClockSensor GPUClock { get; }
        public HardwareSensor GPUWattage { get; private set; }
        public GPUTemperatureSensor GPUTemperature { get; }

        // The sensor object that always queries GPU-hardware power sensors, regardless of
        // which public property (CPUWattage/GPUWattage) currently exposes it after the
        // device-specific swap in the constructor. Used by the ADLX GPU fallback — see the
        // swap comment for why the GPUWattage property alone isn't a safe target.
        private HardwareSensor gpuHardwareWattageSensor;

        public MemoryUsageSensor MemoryUsage { get; }
        public MemoryUsedSensor MemoryUsed { get; }
        public MemoryAvailableSensor MemoryAvailable { get; }

        public GPUMemoryUsedSensor GPUMemoryUsed { get; }
        public GPUMemoryFreeSensor GPUMemoryFree { get; }
        public GPUMemoryClockSensor GPUMemoryClock { get; }

        public BatteryLevelSensor BatteryLevel { get; }
        public BatteryRemainingTimeSensor BatteryRemainingTime { get; }
        public BatteryDischargeRateSensor BatteryDischargeRate { get; }
        public BatteryChargeRateSensor BatteryChargeRate { get; }
        public BatteryRemainingCapacitySensor BatteryRemainingCapacity { get; }
        public BatteryFullChargeCapacitySensor BatteryFullChargeCapacity { get; }

        /// <summary>
        /// Calculated time to full charge in seconds. Returns -1 if not charging or cannot calculate.
        /// </summary>
        public float BatteryTimeToFull
        {
            get
            {
                // Only calculate when charging (charge rate > 0)
                if (BatteryChargeRate.Value <= 0)
                    return -1;

                // Need remaining and full capacity to calculate
                if (BatteryRemainingCapacity.Value <= 0 || BatteryFullChargeCapacity.Value <= 0)
                {
                    Logger.Debug($"BatteryTimeToFull: Missing capacity values - Remaining={BatteryRemainingCapacity.Value}, Full={BatteryFullChargeCapacity.Value}");
                    return -1;
                }

                // Already full
                if (BatteryRemainingCapacity.Value >= BatteryFullChargeCapacity.Value)
                    return 0;

                // Time to Full (hours) = (Full Capacity - Remaining Capacity) mWh / 1000 / Charge Rate W
                // Capacity sensors report in mWh, charge rate is in W
                float remainingToChargeWh = (BatteryFullChargeCapacity.Value - BatteryRemainingCapacity.Value) / 1000f;
                float timeHours = remainingToChargeWh / BatteryChargeRate.Value;

                // Return in seconds for consistency with BatteryRemainingTime
                return timeHours * 3600;
            }
        }

        /// <summary>
        /// Calculated time remaining on battery in seconds. Returns -1 if not discharging or cannot calculate.
        /// Uses Windows API for battery percentage (more reliable after sleep/hibernate) combined with
        /// full charge capacity to get accurate remaining capacity.
        /// </summary>
        public float BatteryTimeRemaining
        {
            get
            {
                // Only calculate when discharging (discharge rate > 0)
                if (BatteryDischargeRate.Value <= 0)
                    return -1;

                // Sanity check: discharge rate should be reasonable (under 100W for a laptop)
                // Stale values after hibernate can be very high from gaming sessions
                if (BatteryDischargeRate.Value > 100)
                {
                    Logger.Debug($"BatteryTimeRemaining: Discharge rate too high ({BatteryDischargeRate.Value}W), likely stale");
                    return -1;
                }

                // Try to use Windows API percentage combined with full charge capacity
                // This is more reliable than LibreHardwareMonitor's remaining capacity after sleep/hibernate
                float remainingCapacityWh = -1;
                try
                {
                    int windowsBatteryPercent = PowerManager.RemainingChargePercent;
                    if (windowsBatteryPercent >= 0 && windowsBatteryPercent <= 100 && BatteryFullChargeCapacity.Value > 0)
                    {
                        // Calculate remaining from Windows % and full capacity
                        // Full capacity is in mWh, convert to Wh
                        float fullCapacityWh = BatteryFullChargeCapacity.Value / 1000f;
                        remainingCapacityWh = fullCapacityWh * windowsBatteryPercent / 100f;
                    }
                }
                catch
                {
                    // Windows API not available, fall through to LibreHardwareMonitor
                }

                // Fallback to LibreHardwareMonitor remaining capacity if Windows API failed
                if (remainingCapacityWh < 0)
                {
                    if (BatteryRemainingCapacity.Value <= 0)
                    {
                        Logger.Debug($"BatteryTimeRemaining: Missing capacity value - Remaining={BatteryRemainingCapacity.Value}");
                        return -1;
                    }
                    remainingCapacityWh = BatteryRemainingCapacity.Value / 1000f;
                }

                // Time Remaining (hours) = Remaining Capacity Wh / Discharge Rate W
                float timeHours = remainingCapacityWh / BatteryDischargeRate.Value;

                // Sanity check: time should be reasonable (under 24 hours)
                if (timeHours > 24)
                {
                    Logger.Debug($"BatteryTimeRemaining: Calculated time too high ({timeHours:F1}h), likely stale values");
                    return -1;
                }

                // Return in seconds
                return timeHours * 3600;
            }
        }

        public NetworkDownloadSensor NetworkDownload { get; }
        public NetworkUploadSensor NetworkUpload { get; }

        private List<HardwareSensor> hardwareSensors;

        private TDPProperty tdp;
        public TDPProperty TDP
        {
            get { return tdp; }
        }

        private CurrentTDPProperty currentTdp;
        public CurrentTDPProperty CurrentTDP
        {
            get { return currentTdp; }
        }

        // TDP Boost properties
        private TDPBoostEnabledProperty tdpBoostEnabled;
        public TDPBoostEnabledProperty TDPBoostEnabled
        {
            get { return tdpBoostEnabled; }
        }

        private TDPBoostSPPTProperty tdpBoostSPPT;
        public TDPBoostSPPTProperty TDPBoostSPPT
        {
            get { return tdpBoostSPPT; }
        }

        private TDPBoostFPPTProperty tdpBoostFPPT;
        public TDPBoostFPPTProperty TDPBoostFPPT
        {
            get { return tdpBoostFPPT; }
        }

        // Current TDP limits (for OSD display)
        public int CurrentSPL { get; private set; }
        public int CurrentSPPT { get; private set; }
        public int CurrentFPPT { get; private set; }

        // Flag to indicate AutoTDP is managing TDP - when true, widget TDP updates are ignored
        public bool IsAutoTDPActive { get; set; }

        /// <summary>
        /// Returns true if the device is in Custom TDP mode (Legion mode 255).
        /// AutoTDP should only manage TDP when this is true.
        /// </summary>
        public bool IsInCustomMode
        {
            get
            {
                // If no Legion manager, assume custom mode (legacy devices)
                if (legionManager == null)
                    return true;

                // Check if Legion Go is detected
                if (!(legionManager.LegionGoDetected?.Value ?? false))
                    return true;

                // Check if in Custom mode (255)
                return legionManager.CurrentPerformanceMode == 255;
            }
        }

        // Quick Metrics push (bundled sensor data sent to widget when enabled).
        // Previously driven by a dedicated 1Hz timer which duplicated the main-loop
        // sensor refresh — now piggy-backs on the main loop's Update() tick so we only
        // walk LibreHardwareMonitor once per second.
        private bool quickMetricsEnabled;

        /// <summary>
        /// Gets or sets whether Quick Metrics push is enabled.
        /// When enabled, the main-loop Update() tick pushes bundled sensor data
        /// (battery, CPU, GPU usage) to the widget every second.
        /// </summary>
        public bool QuickMetricsEnabled
        {
            get => quickMetricsEnabled;
            set
            {
                if (quickMetricsEnabled == value) return;
                quickMetricsEnabled = value;
                Logger.Info($"Quick Metrics push {(value ? "enabled" : "disabled")}");
            }
        }

        private System.Timers.Timer currentTdpTimer;
        private string lastTdpString = "";
        private int consecutiveReadFailures = 0;

        // Legion Go support for manufacturer WMI TDP
        private LegionManager legionManager;

        // AMD ADLX fallback for GPU sensors that LibreHardwareMonitor doesn't expose
        // on certain APUs (Z2 series). Filled in via SetAMDManager after AMDManager
        // initializes; null when ADLX isn't available, which leaves the OSD on
        // LHM-only behavior.
        private XboxGamingBarHelper.AMD.AMDManager amdManager;
        // Throttle ADLX-fallback diagnostic logging to once per minute so a sustained
        // OSD session doesn't fill the log with the same "filled GPUWattage from ADLX"
        // line every tick.
        private long lastAdlxFallbackLogTicksUtc;

        // PawnIO/RyzenSMU support for anti-cheat compatible TDP control
        private RyzenSmuService ryzenSmuService;
        private bool pawnIOAvailable;

        // WinRing0 removed - deprecated TDP method, no longer bundled
        // private bool winRing0Available;
        // private const string WinRing0BackupFolder = @"C:\GoTweaks";

        // PawnIO driver installation status
        private bool pawnIOInstalled;

        // RyzenAdj lazy loading - only load when user disables Manufacturer WMI
        private bool ryzenAdjInitialized = false;
        private bool ryzenAdjInitAttempted = false;

        // Thread synchronization locks to prevent race conditions
        private readonly object tdpLock = new object();
        private readonly object ryzenAdjInitLock = new object();

        // Properties for TDP method availability
        // private TdpMethodAvailableProperty winRing0AvailableProperty; // WinRing0 removed
        private TdpMethodAvailableProperty pawnIOAvailableProperty;
        private TdpMethodAvailableProperty pawnIOInstalledProperty;
        private InstallPawnIOProperty installPawnIOProperty;

        /// <summary>
        /// Gets whether PawnIO is available for TDP control.
        /// </summary>
        public bool IsPawnIOAvailable => pawnIOAvailable;

        // WinRing0 removed - deprecated TDP method
        // /// <summary>
        // /// Gets whether WinRing0 files are available in C:\GoTweaks.
        // /// </summary>
        // public bool IsWinRing0Available => winRing0Available;

        // /// <summary>
        // /// Property for WinRing0 availability (exposed to widget).
        // /// </summary>
        // public TdpMethodAvailableProperty WinRing0AvailableProperty => winRing0AvailableProperty;

        /// <summary>
        /// Property for PawnIO availability (exposed to widget).
        /// </summary>
        public TdpMethodAvailableProperty PawnIOAvailableProperty => pawnIOAvailableProperty;

        /// <summary>
        /// Gets whether PawnIO driver is installed (may not work for TDP yet).
        /// </summary>
        public bool IsPawnIOInstalled => pawnIOInstalled;

        /// <summary>
        /// Property for PawnIO driver installed status (exposed to widget).
        /// </summary>
        public TdpMethodAvailableProperty PawnIOInstalledProperty => pawnIOInstalledProperty;

        /// <summary>
        /// Property to trigger PawnIO installation (exposed to widget).
        /// </summary>
        public InstallPawnIOProperty InstallPawnIOProperty => installPawnIOProperty;

        #region PawnIO Debug Tools

        /// <summary>
        /// Gets CPU info string for PawnIO debug display.
        /// </summary>
        public string GetPawnIOCpuInfo()
        {
            if (ryzenSmuService == null || !ryzenSmuService.IsInitialized)
            {
                return "PawnIO not initialized";
            }

            var cpuName = ryzenSmuService.CpuCodeName.ToString();
            var smuVer = $"0x{ryzenSmuService.SmuVersion:X8}";
            var capabilities = new System.Collections.Generic.List<string>();

            if (ryzenSmuService.CanSetCurveOptimizerAll()) capabilities.Add("CO");
            if (ryzenSmuService.CanSetCurveOptimizerGfx()) capabilities.Add("CO-GFX");
            if (ryzenSmuService.CanSetGfxClock()) capabilities.Add("GfxClk");
            if (ryzenSmuService.CanSetTctlTemp()) capabilities.Add("Tctl");
            if (ryzenSmuService.CanSetStapmTime()) capabilities.Add("StapmTime");

            return $"{cpuName} (SMU: {smuVer}) | {string.Join(", ", capabilities)}";
        }

        /// <summary>
        /// Applies PawnIO debug settings (Curve Optimizer, GfxClk, Tctl).
        /// </summary>
        public string ApplyPawnIODebugSettings(int coAll, int coGfx, int gfxClk, int tctlTemp)
        {
            if (ryzenSmuService == null || !ryzenSmuService.IsInitialized)
            {
                return "Error: PawnIO not initialized";
            }

            var results = new System.Collections.Generic.List<string>();

            try
            {
                // Apply Curve Optimizer All
                if (coAll != 0 && ryzenSmuService.CanSetCurveOptimizerAll())
                {
                    bool success = ryzenSmuService.SetCurveOptimizerAll(coAll);
                    results.Add($"CO All ({coAll}): {(success ? "OK" : "FAIL")}");
                }

                // Apply Curve Optimizer iGPU
                if (coGfx != 0 && ryzenSmuService.CanSetCurveOptimizerGfx())
                {
                    bool success = ryzenSmuService.SetCurveOptimizerGfx(coGfx);
                    results.Add($"CO GFX ({coGfx}): {(success ? "OK" : "FAIL")}");
                }

                // Apply iGPU Clock
                if (gfxClk > 0 && ryzenSmuService.CanSetGfxClock())
                {
                    bool success = ryzenSmuService.SetGfxClock((uint)gfxClk);
                    results.Add($"GfxClk ({gfxClk} MHz): {(success ? "OK" : "FAIL")}");
                }

                // Apply Tctl Temperature
                if (tctlTemp > 0 && ryzenSmuService.CanSetTctlTemp())
                {
                    bool success = ryzenSmuService.SetTctlTemp((uint)tctlTemp);
                    results.Add($"Tctl ({tctlTemp}°C): {(success ? "OK" : "FAIL")}");
                }

                if (results.Count == 0)
                {
                    return "No settings applied (all values at default or unsupported)";
                }

                return string.Join(" | ", results);
            }
            catch (Exception ex)
            {
                Logger.Error($"PawnIO debug apply failed: {ex.Message}");
                return $"Error: {ex.Message}";
            }
        }

        #endregion

        /// <summary>
        /// Sets the Legion Manager reference for WMI TDP support.
        /// Must be called after LegionManager is initialized.
        /// </summary>
        public void SetLegionManager(LegionManager manager)
        {
            legionManager = manager;
            Logger.Info($"LegionManager reference set. Legion detected: {manager?.LegionGoDetected?.Value ?? false}");
        }

        /// <summary>
        /// Sets the AMD Manager reference so the sensor update loop can fall back to
        /// ADLX for GPU metrics that LibreHardwareMonitor doesn't expose on a given
        /// APU. No-op when AMDManager is null (non-AMD systems / ADLX init failed).
        /// </summary>
        public void SetAMDManager(XboxGamingBarHelper.AMD.AMDManager manager)
        {
            amdManager = manager;
            Logger.Info($"AMDManager reference set on PerformanceManager. ADLX GPU-metric fallback {(manager != null ? "enabled" : "disabled")}.");
        }

        // Returns true when any in-process consumer wants fresh sensors (sidebar visible,
        // OSD/QuickMetrics on, game running, AutoTDP active, fan-curve UI open, etc.).
        // Set from Program.cs once all managers are wired. When null we behave as before
        // (always refresh) so init-order can't accidentally freeze the OSD.
        private Func<bool> metricsConsumerCheck;

        /// <summary>
        /// Registers a predicate the Update() loop uses to decide whether anyone needs
        /// fresh sensor data this tick. When the predicate returns false the expensive
        /// LibreHardwareMonitor walk and ADLX fallback are skipped — sensors retain
        /// their previous value until the next tick where a consumer is active.
        /// </summary>
        public void SetMetricsConsumerCheck(Func<bool> check)
        {
            metricsConsumerCheck = check;
            Logger.Info($"PerformanceManager metrics consumer-check {(check != null ? "registered" : "cleared")}");
        }

        private bool AnyMetricsConsumerActive()
        {
            if (quickMetricsEnabled) return true;
            var check = metricsConsumerCheck;
            if (check == null) return true; // fail-open until wired
            try { return check(); }
            catch (Exception ex)
            {
                Logger.Warn($"metricsConsumerCheck threw — refreshing anyway: {ex.Message}");
                return true;
            }
        }

        /// <summary>
        /// Public view of the consumer check — used by other managers (e.g. LegionManager
        /// fan-speed WMI throttle) to align their refresh cadence with whether anyone
        /// in the helper actually needs the data.
        /// </summary>
        public bool IsAnyMetricsConsumerActive => AnyMetricsConsumerActive();

        /// <summary>
        /// Initializes PawnIO/RyzenSMU for anti-cheat compatible TDP control.
        /// Call this after the helper is initialized.
        /// </summary>
        public void InitializePawnIO()
        {
            try
            {
                Logger.Info("Attempting to initialize PawnIO/RyzenSMU...");
                ryzenSmuService = new RyzenSmuService();

                if (ryzenSmuService.Initialize())
                {
                    pawnIOAvailable = true;
                    Logger.Info($"PawnIO/RyzenSMU initialized successfully. CPU: {ryzenSmuService.CpuCodeName}, SMU: 0x{ryzenSmuService.SmuVersion:X8}");
                }
                else
                {
                    pawnIOAvailable = false;
                    Logger.Warn("PawnIO/RyzenSMU initialization failed. PawnIO driver may not be installed.");
                    ryzenSmuService?.Dispose();
                    ryzenSmuService = null;
                }
            }
            catch (Exception ex)
            {
                pawnIOAvailable = false;
                Logger.Error($"Exception initializing PawnIO: {ex.Message}");
                ryzenSmuService?.Dispose();
                ryzenSmuService = null;
            }

            // Update the availability property if already initialized
            pawnIOAvailableProperty?.SetAvailable(pawnIOAvailable);
            Logger.Info($"PawnIO availability updated: {pawnIOAvailable}");
        }
        private const int MaxConsecutiveFailuresBeforeReinit = 5;
        private const double NormalTimerInterval = 2000; // 2 seconds
        private const double BackoffTimerInterval = 10000; // 10 seconds during failures
        private const int VerificationDelayMs = 500; // Delay before verification read after SetTDP
        private const int TDPDebounceDelayMs = 150; // Debounce delay for rapid TDP changes

        // TDP debouncing to prevent queue buildup from rapid changes
        private System.Threading.Timer tdpDebounceTimer;
        private int pendingTDP = -1; // -1 means no pending TDP
        private readonly object debounceLock = new object();

        // Flag to indicate if hardware detection is complete
        private volatile bool hardwareInitialized = false;

        internal PerformanceManager() : base()
        {
            // Initialize the computer sensors
            // Only enable hardware types actually used - others can cause hangs:
            // - IsMotherboardEnabled: SuperIO chip probing can hang
            // - IsStorageEnabled: SMART queries can timeout on some drives
            // - IsControllerEnabled: Fan controller probing (Legion has its own)
            // - IsNetworkEnabled: Not used in OSD
            Logger.Info("PerformanceManager: Creating Computer object...");
            computer = new Computer
            {
                IsCpuEnabled = true,        // CPU usage, clock, temp, wattage
                IsGpuEnabled = true,        // GPU usage, clock, temp, wattage, VRAM
                IsMemoryEnabled = true,     // RAM usage
                IsMotherboardEnabled = false, // DISABLED - not used, can hang on SuperIO
                IsControllerEnabled = false,  // DISABLED - not used, Legion has own fan control
                IsNetworkEnabled = false,     // DISABLED - not used in OSD
                IsStorageEnabled = false,     // DISABLED - not used, SMART can hang
                IsBatteryEnabled = true,      // Battery level, time, charge/discharge rate
            };
            Logger.Info("PerformanceManager: Creating UpdateVisitor...");
            updateVisitor = new UpdateVisitor();

            // Start hardware detection in background - don't block constructor
            // Sensors will return default values until hardware is initialized
            Logger.Info("PerformanceManager: Starting hardware detection in background...");
            Task.Run(() => InitializeHardwareAsync());

            // Initialize hardware sensors immediately (they'll work once hardware is detected)
            Logger.Info("Initializing hardware sensors...");
            CPUClock = new CPUClockSensor();
            CPUUsage = new CPUUsageSensor();
            CPUWattage = new CPUWattageSensor();
            CPUTemperature = new CPUTemperatureSensor();
            VRMTemperature = new VRMTemperatureSensor();
            GPUUsage = new GPUUsageSensor();
            GPUClock = new GPUClockSensor();
            GPUTemperature = new GPUTemperatureSensor();
            GPUWattage = new GPUWattageSensor();

            // Remember the sensor object that actually queries GPU hardware ("GPU Core" /
            // "GPU Package" / "GPU Power") BEFORE the LeGo2 swap below can reassign the
            // GPUWattage property to something else. FillGpuSensorsFromAdlxFallback needs this
            // fixed reference — it fills in ADLX data specifically when the GPU-hardware LHM
            // sensor is absent (a known LeGo2/Z2 gap), and must keep targeting that same
            // sensor object regardless of which public property currently exposes it.
            gpuHardwareWattageSensor = GPUWattage;

            // LeGo2 (Ryzen Z2 Extreme): LibreHardwareMonitor's CPU "Package" sensor
            // reports what AMD Adrenalin's overlay labels GPU Power, and the GPU
            // "GPU Core" sensor reports CPU package power — they're swapped vs.
            // their labels. Confirmed live by Diego on 2026-05-31 (GoTweaks CPU
            // showed 25 W at the same time AMD Adrenalin showed 25 W on GPU).
            // Swap the two sensor references so OSD labels match what the user
            // expects (and what AMD's overlay shows).
            try
            {
                var detected = Devices.DeviceDetector.DetectDevice();
                if (detected != null && detected.DeviceType == Shared.Enums.DeviceType.LegionGo2)
                {
                    var tmpW = CPUWattage;
                    CPUWattage = GPUWattage;
                    GPUWattage = tmpW;
                    Logger.Info("PerformanceManager: LeGo2 detected — swapped CPU/GPU wattage sensors (Adrenalin agrees)");
                }
            }
            catch (System.Exception swapEx)
            {
                Logger.Warn($"PerformanceManager: LeGo2 wattage swap check failed: {swapEx.Message}");
            }
            MemoryUsage = new MemoryUsageSensor();
            MemoryUsed = new MemoryUsedSensor();
            MemoryAvailable = new MemoryAvailableSensor();
            GPUMemoryUsed = new GPUMemoryUsedSensor();
            GPUMemoryFree = new GPUMemoryFreeSensor();
            GPUMemoryClock = new GPUMemoryClockSensor();
            BatteryLevel = new BatteryLevelSensor();
            BatteryRemainingTime = new BatteryRemainingTimeSensor();
            BatteryDischargeRate = new BatteryDischargeRateSensor();
            BatteryChargeRate = new BatteryChargeRateSensor();
            BatteryRemainingCapacity = new BatteryRemainingCapacitySensor();
            BatteryFullChargeCapacity = new BatteryFullChargeCapacitySensor();
            NetworkDownload = new NetworkDownloadSensor();
            NetworkUpload = new NetworkUploadSensor();

            hardwareSensors = new List<HardwareSensor>()
            {
                CPUClock,
                CPUUsage,
                CPUWattage,
                CPUTemperature,
                VRMTemperature,
                GPUUsage,
                GPUClock,
                GPUTemperature,
                GPUWattage,
                MemoryUsage,
                MemoryUsed,
                MemoryAvailable,
                GPUMemoryUsed,
                GPUMemoryFree,
                GPUMemoryClock,
                BatteryLevel,
                BatteryRemainingTime,
                BatteryDischargeRate,
                BatteryChargeRate,
                BatteryRemainingCapacity,
                BatteryFullChargeCapacity,
                NetworkDownload,
                NetworkUpload,
            };

            // RyzenAdj initialization deferred - WinRing0 no longer bundled
            // Use PawnIO for TDP control instead (anti-cheat compatible)
            Logger.Info("RyzenAdj initialization deferred (deprecated - PawnIO preferred for TDP control)");
            var initialTDP = 25;
            var initialCurrentTDP = "-- W";

            Logger.Info("Creating TDP properties...");
            tdp = new TDPProperty(initialTDP, null, this);
            currentTdp = new CurrentTDPProperty(initialCurrentTDP, null, this);
            lastTdpString = initialCurrentTDP;

            // Initialize TDP Boost properties (defaults: enabled=false, SPPT=1W, FPPT=3W)
            tdpBoostEnabled = new TDPBoostEnabledProperty(false, this);
            tdpBoostSPPT = new TDPBoostSPPTProperty(1, this);
            tdpBoostFPPT = new TDPBoostFPPTProperty(3, this);
            Logger.Info("TDP Boost properties initialized (defaults: enabled=false, SPPT=1W, FPPT=3W)");

            // Set up timer to update current TDP every 3 seconds
            currentTdpTimer = new System.Timers.Timer(NormalTimerInterval);
            currentTdpTimer.Elapsed += UpdateCurrentTDP;
            currentTdpTimer.AutoReset = true;
            currentTdpTimer.Start();
            Logger.Info("CurrentTDP timer started, updating every 3 seconds");

            // WinRing0 removed - deprecated TDP method, no longer bundled
            // CheckWinRing0Availability();

            // Check PawnIO driver installation status
            CheckPawnIODriverInstalled();

            // Initialize TDP method availability properties
            // winRing0AvailableProperty = new TdpMethodAvailableProperty(winRing0Available, Function.TdpMethod_WinRing0Available, this); // WinRing0 removed
            pawnIOAvailableProperty = new TdpMethodAvailableProperty(pawnIOAvailable, Function.TdpMethod_PawnIOAvailable, this);
            pawnIOInstalledProperty = new TdpMethodAvailableProperty(pawnIOInstalled, Function.TdpMethod_PawnIOInstalled, this);
            installPawnIOProperty = new InstallPawnIOProperty(this);
            // pawnIOAvailable is always false here — InitializePawnIO() runs
            // after manager init (Program.cs) and logs "PawnIO availability
            // updated" with the real value. Label this line accordingly so a
            // "PawnIO=False" at startup isn't misread as a broken install.
            Logger.Info($"TDP method availability (pre-init snapshot): PawnIO={pawnIOAvailable}, PawnIOInstalled={pawnIOInstalled} — final availability logged after InitializePawnIO");
        }

        /// <summary>
        /// Initialize hardware detection in background. This is slow (2+ seconds) so we don't block the constructor.
        /// </summary>
        private void InitializeHardwareAsync()
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                Logger.Info("PerformanceManager: Background hardware detection starting...");

                // Call computer.Open() - this is the slow part
                try
                {
                    computer.Open();
                    Logger.Info("PerformanceManager: computer.Open() completed successfully.");
                }
                catch (AggregateException ae)
                {
                    Logger.Warn($"PerformanceManager: computer.Open() had {ae.InnerExceptions.Count} errors (some hardware may not be available):");
                    foreach (var innerEx in ae.InnerExceptions)
                    {
                        Logger.Warn($"  - {innerEx.GetType().Name}: {innerEx.Message}");
                    }
                    // Continue anyway - use whatever hardware was initialized
                }
                catch (Exception ex)
                {
                    Logger.Error($"PerformanceManager: computer.Open() failed: {ex.GetType().Name}: {ex.Message}");
                    if (ex.InnerException != null)
                    {
                        Logger.Error($"  Inner exception: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
                    }
                }

                // Accept the visitor for whatever hardware was found
                try
                {
                    computer.Accept(updateVisitor);
                    Logger.Info("PerformanceManager: computer.Accept() completed.");
                }
                catch (Exception ex)
                {
                    Logger.Warn($"PerformanceManager: computer.Accept() failed: {ex.Message}");
                }

                // Log discovered hardware
                foreach (IHardware hardware in computer.Hardware)
                {
                    var properties = string.Empty;
                    if (hardware.Properties.Count > 0)
                    {
                        foreach (var property in hardware.Properties)
                        {
                            properties = properties.Length == 0 ? $"{property.Key}:{property.Value}" : $"{properties}, {property.Key}:{property.Value}";
                        }
                    }
                    Logger.Info($"Found hardware {hardware.HardwareType}: Name={hardware.Name}, Type={hardware.HardwareType}, Id={hardware.Identifier}, Properties={properties}");

                    // Log sensors for key hardware types
                    if (hardware.HardwareType == HardwareType.Cpu ||
                        hardware.HardwareType == HardwareType.Battery ||
                        hardware.HardwareType == HardwareType.GpuNvidia ||
                        hardware.HardwareType == HardwareType.GpuIntel ||
                        hardware.HardwareType == HardwareType.GpuAmd)
                    {
                        hardware.Update();
                        foreach (ISensor sensor in hardware.Sensors)
                        {
                            Logger.Info($"  {hardware.HardwareType} Sensor: Name='{sensor.Name}', Type={sensor.SensorType}, Value={sensor.Value}");
                        }
                    }
                }

                hardwareInitialized = true;
                timer.Stop();
                Logger.Info($"[TIMING] Background hardware detection completed: {timer.ElapsedMilliseconds}ms");
            }
            catch (Exception ex)
            {
                Logger.Error($"PerformanceManager: Background hardware detection failed: {ex.Message}");
                timer.Stop();
            }
        }

        // WinRing0 removed - deprecated TDP method, no longer bundled
        // /// <summary>
        // /// Checks if WinRing0 files exist in C:\GoTweaks folder.
        // /// </summary>
        // private void CheckWinRing0Availability()
        // {
        //     try
        //     {
        //         // Check for bundled WinRing0 files in helper's directory
        //         string helperDir = AppDomain.CurrentDomain.BaseDirectory;
        //         string dllPath = Path.Combine(helperDir, "WinRing0x64.dll");
        //         string sysPath = Path.Combine(helperDir, "WinRing0x64.sys");
        //         string libRyzenAdjPath = Path.Combine(helperDir, "libryzenadj.dll");
        //
        //         winRing0Available = File.Exists(dllPath) && File.Exists(sysPath) && File.Exists(libRyzenAdjPath);
        //
        //         if (winRing0Available)
        //         {
        //             Logger.Info($"WinRing0 files found (bundled) in {helperDir}");
        //
        //             // Also copy to backup folder for external access if needed
        //             try
        //             {
        //                 if (!Directory.Exists(WinRing0BackupFolder))
        //                     Directory.CreateDirectory(WinRing0BackupFolder);
        //
        //                 CopyFileIfNewer(dllPath, Path.Combine(WinRing0BackupFolder, "WinRing0x64.dll"));
        //                 CopyFileIfNewer(sysPath, Path.Combine(WinRing0BackupFolder, "WinRing0x64.sys"));
        //                 CopyFileIfNewer(libRyzenAdjPath, Path.Combine(WinRing0BackupFolder, "libryzenadj.dll"));
        //             }
        //             catch (Exception ex)
        //             {
        //                 Logger.Warn($"Could not copy WinRing0 files to backup folder: {ex.Message}");
        //             }
        //         }
        //         else
        //         {
        //             Logger.Info($"WinRing0 files not bundled in {helperDir} (WinRing0 TDP method will be hidden)");
        //         }
        //     }
        //     catch (Exception ex)
        //     {
        //         winRing0Available = false;
        //         Logger.Warn($"Error checking WinRing0 availability: {ex.Message}");
        //     }
        // }

        private void CopyFileIfNewer(string source, string dest)
        {
            if (!File.Exists(source)) return;

            bool needsCopy = !File.Exists(dest);
            if (!needsCopy)
            {
                var sourceInfo = new FileInfo(source);
                var destInfo = new FileInfo(dest);
                needsCopy = sourceInfo.Length != destInfo.Length || sourceInfo.LastWriteTimeUtc > destInfo.LastWriteTimeUtc;
            }

            if (needsCopy)
            {
                File.Copy(source, dest, true);
            }
        }

        /// <summary>
        /// Checks if PawnIO driver is installed on the system.
        /// Uses fast CreateFile check instead of slow WMI query.
        /// </summary>
        private void CheckPawnIODriverInstalled()
        {
            Logger.Info("Checking PawnIO driver installation status...");

            try
            {
                // Try to open the PawnIO device - this is much faster than WMI
                IntPtr handle = CreateFile(
                    PAWNIO_DEVICE_PATH,
                    GENERIC_READ,
                    FILE_SHARE_READ,
                    IntPtr.Zero,
                    OPEN_EXISTING,
                    0,
                    IntPtr.Zero);

                if (handle != IntPtr.Zero && handle.ToInt64() != -1)
                {
                    // Successfully opened - driver is installed
                    CloseHandle(handle);
                    pawnIOInstalled = true;
                    Logger.Info("PawnIO driver is installed (device opened successfully)");
                }
                else
                {
                    // Failed to open - driver not installed or not running
                    pawnIOInstalled = false;
                    int error = Marshal.GetLastWin32Error();
                    Logger.Info($"PawnIO driver is not installed (error code: {error})");
                }
            }
            catch (Exception ex)
            {
                pawnIOInstalled = false;
                Logger.Warn($"Error checking PawnIO driver: {ex.Message}");
            }
        }

        /// <summary>
        /// Refreshes the PawnIO driver installation status.
        /// Called after installation attempt to update the UI.
        /// </summary>
        public void RefreshPawnIOInstalledStatus()
        {
            CheckPawnIODriverInstalled();
            pawnIOInstalledProperty?.SetAvailable(pawnIOInstalled);
            Logger.Info($"PawnIO driver installation status refreshed: {pawnIOInstalled}");
        }

        /// <summary>
        /// Forces a refresh of all hardware sensors, particularly useful after resume from hibernation.
        /// LibreHardwareMonitor caches values that can become stale after hibernation.
        /// </summary>
        public void ForceRefreshHardware()
        {
            if (computer == null)
                return;

            Logger.Info("ForceRefreshHardware: Forcing refresh of all hardware sensors after resume");

            try
            {
                // Force update all hardware
                foreach (IHardware hardware in computer.Hardware)
                {
                    hardware.Update();

                    // Also update sub-hardware (some sensors are nested)
                    foreach (IHardware subHardware in hardware.SubHardware)
                    {
                        subHardware.Update();
                    }

                    // Log battery values for debugging
                    if (hardware.HardwareType == HardwareType.Battery)
                    {
                        foreach (ISensor sensor in hardware.Sensors)
                        {
                            Logger.Info($"ForceRefreshHardware: Battery sensor '{sensor.Name}' = {sensor.Value}");
                        }
                    }

                    // Log GPU sensor inventory so we can diagnose OSD "GPU 71% N/A N/A N/A"
                    // patterns (LibreHardwareMonitor not exposing temp/power/clock on
                    // certain AMD APUs). This is the only diagnostic visible after a
                    // hibernate resume — the original startup enumeration may have rolled
                    // off the log file by the time the user reports the issue.
                    if (hardware.HardwareType == HardwareType.GpuAmd ||
                        hardware.HardwareType == HardwareType.GpuNvidia ||
                        hardware.HardwareType == HardwareType.GpuIntel)
                    {
                        Logger.Info($"ForceRefreshHardware: {hardware.HardwareType} '{hardware.Name}' sensor inventory:");
                        foreach (ISensor sensor in hardware.Sensors)
                        {
                            Logger.Info($"  {hardware.HardwareType} Sensor: Name='{sensor.Name}', Type={sensor.SensorType}, Value={sensor.Value}");
                        }
                    }
                }

                // Accept visitor to ensure all values are propagated
                computer.Accept(updateVisitor);

                // Log Windows API battery value for comparison
                try
                {
                    int windowsBatteryPercent = PowerManager.RemainingChargePercent;
                    Logger.Info($"ForceRefreshHardware: Windows API Battery = {windowsBatteryPercent}%");
                }
                catch (Exception apiEx)
                {
                    Logger.Warn($"ForceRefreshHardware: Failed to get Windows API battery: {apiEx.Message}");
                }

                // After accept-visitor has populated hardwareSensors, log the registered
                // GPU sensor objects' current values. -1 means LibreHardwareMonitor never
                // matched the expected name+type for that slot, which is exactly the
                // "OSD shows N/A" condition. Cross-reference with the sensor inventory
                // above to find what name LHM uses on this hardware.
                Logger.Info("ForceRefreshHardware: Registered GPU/VRAM sensor values after refresh:");
                Logger.Info($"  GPUUsage         = {GPUUsage.Value} (looking for SensorType=Load on GpuAmd/GpuNvidia/GpuIntel, names: GPU Core / D3D 3D / GPU)");
                Logger.Info($"  GPUClock         = {GPUClock.Value} (looking for SensorType=Clock, names: GPU Core)");
                Logger.Info($"  GPUWattage       = {GPUWattage.Value} (looking for SensorType=Power, names: GPU Core / GPU Package / GPU Power)");
                Logger.Info($"  GPUTemperature   = {GPUTemperature.Value} (looking for SensorType=Temperature, names: GPU VR SoC / GPU Core)");
                Logger.Info($"  GPUMemoryUsed    = {GPUMemoryUsed.Value}");
                Logger.Info($"  GPUMemoryFree    = {GPUMemoryFree.Value}");
                Logger.Info($"  GPUMemoryClock   = {GPUMemoryClock.Value} (looking for SensorType=Clock)");
                Logger.Info("ForceRefreshHardware: Hardware refresh complete");
            }
            catch (Exception ex)
            {
                Logger.Error($"ForceRefreshHardware: Error refreshing hardware: {ex.Message}");
            }
        }

        // Tick counter for fast/slow tier split. The main helper loop ticks Update()
        // every 1 s, but a full per-second refresh of CPU% / RAM / Battery / temperature
        // is overkill for what consumes the data downstream (sidebar / OSD redraw, EC
        // fan loop at 3 s, AutoTDP at its own cadence). FastTickEvery throttles the
        // entire walk to every 2 s; SlowTickEvery within that lands the GPU + ADLX
        // refresh on every 4 s (every 4th raw tick = every 2nd walk), keeping OSD GPU
        // temp / clock readouts current enough for users running the overlay live.
        private long sensorTickCounter;
        private const int FastTickEvery = 2;
        private const int SlowTickEvery = 4;

        public override void Update()
        {
            base.Update();

            /*if (ryzenAdjHandle != IntPtr.Zero)
            {
                Logger.Info($"get_core_clk={RyzenAdj.get_core_clk(ryzenAdjHandle, 0)} get_core_power={RyzenAdj.get_core_power(ryzenAdjHandle, 0)} get_fclk={RyzenAdj.get_fclk(ryzenAdjHandle)} get_gfx_clk={RyzenAdj.get_gfx_clk(ryzenAdjHandle)} get_soc_power={RyzenAdj.get_soc_power(ryzenAdjHandle)} get_socket_power={RyzenAdj.get_socket_power(ryzenAdjHandle)}");
                var setMaxResult = RyzenAdj.set_max_gfxclk_freq(ryzenAdjHandle, 2000);
                var setMinResult = RyzenAdj.set_min_gfxclk_freq(ryzenAdjHandle, 1000);
                //var nan2 = float.NaN;
                //var setResult = RyzenAdj.set_gfx_clk(ryzenAdjHandle, (uint)nan2);

                Logger.Info($"set_max={setMaxResult} set_min={setMinResult} set={"123"}");
            }*/

            if (computer == null)
                return;

            // When nobody is consuming metrics (sidebar closed, OSD off, no game, AutoTDP
            // off, fan-curve UI closed), skip the LibreHardwareMonitor walk + ADLX fallback
            // entirely. Sensors keep their last value; the next consumer-active tick
            // repopulates them. This is the dominant idle-CPU cost in the helper.
            if (!AnyMetricsConsumerActive())
                return;

            sensorTickCounter++;

            // Throttle the entire walk to every FastTickEvery-th active Update so the
            // cheap CPU/Memory/Battery refresh lands at ~2 s rather than 1 s. The very
            // first active tick still walks so the sidebar/OSD don't sit on "—" while
            // they wait for fresh data. (kayti #88 #1/#2 follow-up: idle helper cost
            // was the dominant CPU draw; this trims it ~50 % without affecting the EC
            // fan loop, which reads CPUTemperature on its own 3 s cadence.)
            if (sensorTickCounter != 1 && sensorTickCounter % FastTickEvery != 0)
                return;

            // First active tick always does a full refresh so the OSD/widget aren't
            // stuck showing "—" for GPU sensors during the first 1–2 seconds.
            bool slowTick = (sensorTickCounter == 1) || (sensorTickCounter % SlowTickEvery == 0);

            // Pre-load with current sensor values. Hardware we refresh this tick will
            // overwrite via ProcessHardwareSensors; hardware we skip this tick keeps
            // its last reading rather than reverting to -1.
            var pendingValues = new Dictionary<HardwareSensor, float>();
            foreach (var hardwareSensor in hardwareSensors)
            {
                pendingValues[hardwareSensor] = hardwareSensor.Value;
            }

            foreach (IHardware hardware in computer.Hardware)
            {
                if (!ShouldRefreshHardwareThisTick(hardware.HardwareType, slowTick))
                    continue;

                // Reset just the sensors we're about to refresh to -1, so a sensor
                // that has *now* gone missing reports as no-data instead of stale.
                // (Matches the original semantics for hardware we're walking.)
                ResetSensorsForHardwareType(pendingValues, hardware.HardwareType);

                hardware.Update();
                foreach (IHardware subHardware in hardware.SubHardware)
                    subHardware.Update();

                ProcessHardwareSensors(hardware, pendingValues);
                foreach (IHardware subHardware in hardware.SubHardware)
                    ProcessHardwareSensors(subHardware, pendingValues);
            }

            // ADLX fallback is itself a GPU query (IADLXGPUMetrics), so it pairs with
            // the slow tier. On fast ticks the cached GPU sensor values stand.
            if (slowTick)
            {
                FillGpuSensorsFromAdlxFallback(pendingValues);
            }

            // Apply all values at once so PushQuickMetrics never sees partially-reset state
            foreach (var kvp in pendingValues)
            {
                kvp.Key.Value = kvp.Value;
            }

            // Override battery level with Windows API value
            // LibreHardwareMonitor calculates battery % from capacity values which can be stale after sleep
            // Windows API returns the battery controller's actual reported percentage
            try
            {
                int windowsBatteryPercent = PowerManager.RemainingChargePercent;
                if (windowsBatteryPercent >= 0 && windowsBatteryPercent <= 100)
                {
                    BatteryLevel.Value = windowsBatteryPercent;
                }
            }
            catch
            {
                // Fallback to LibreHardwareMonitor value if Windows API fails
            }

            // Calculate battery remaining time from capacity and discharge rate
            // LibreHardwareMonitor doesn't always report the "Remaining Time (Estimated)" sensor
            float calculatedTimeRemaining = BatteryTimeRemaining;
            if (calculatedTimeRemaining >= 0)
            {
                BatteryRemainingTime.Value = calculatedTimeRemaining;
            }

            // Piggy-back the widget metrics push on this tick. Previously a separate
            // 1Hz Timer fired ~halfway between Update() calls and read the same fields;
            // bundling here gives the widget the freshest values and removes a thread.
            if (quickMetricsEnabled)
            {
                PushQuickMetrics();
            }
        }

        // Map hardware type → refresh tier. Cheap hardware refreshes every tick; GPU
        // hardware (and anything we don't explicitly classify) defers to the slow tier
        // because ADL/NVML round-trips dominate the per-tick budget.
        private static bool ShouldRefreshHardwareThisTick(HardwareType type, bool slowTick)
        {
            switch (type)
            {
                case HardwareType.Cpu:
                case HardwareType.Memory:
                case HardwareType.Battery:
                    return true;
                case HardwareType.GpuAmd:
                case HardwareType.GpuNvidia:
                case HardwareType.GpuIntel:
                    return slowTick;
                default:
                    return slowTick;
            }
        }

        private void ResetSensorsForHardwareType(Dictionary<HardwareSensor, float> pendingValues, HardwareType type)
        {
            foreach (var hardwareSensor in hardwareSensors)
            {
                if (hardwareSensor.MatchesHardwareType(type))
                    pendingValues[hardwareSensor] = -1.0f;
            }
        }

        /// <summary>
        /// For GPU/VRAM sensors that LibreHardwareMonitor didn't fill (still -1 after
        /// the per-hardware processing loop), query ADLX's IADLXGPUMetrics and use its
        /// reading instead. This makes the OSD GPU line work on AMD APUs where LHM
        /// exposes Load + memory but not Power/Temperature/Clock — see Mute's Legion
        /// Go 2 Z2-series report 2026-05-04. ADLX-only metrics also stay at -1 on
        /// systems where ADLX itself can't provide them, so the OSD still falls back
        /// to "N/A" when neither source has the value.
        /// </summary>
        private void FillGpuSensorsFromAdlxFallback(Dictionary<HardwareSensor, float> pendingValues)
        {
            if (amdManager == null)
            {
                return;
            }

            // Cheap fast-path: if every GPU/VRAM slot already has a valid value from
            // LHM, don't bother spinning up ADLX metrics this tick.
            bool needAny =
                IsMissing(pendingValues, GPUUsage) ||
                IsMissing(pendingValues, GPUClock) ||
                IsMissing(pendingValues, gpuHardwareWattageSensor) ||
                IsMissing(pendingValues, GPUTemperature) ||
                IsMissing(pendingValues, GPUMemoryClock);
            if (!needAny)
            {
                return;
            }

            if (!amdManager.TryGetCurrentGpuMetrics(out var snap))
            {
                return;
            }

            int filled = 0;

            if (IsMissing(pendingValues, GPUUsage) && snap.HasUsage)
            {
                pendingValues[GPUUsage] = (float)snap.UsagePercent;
                filled++;
            }
            if (IsMissing(pendingValues, GPUClock) && snap.HasClockMHz)
            {
                pendingValues[GPUClock] = snap.GpuClockMHz;
                filled++;
            }
            if (IsMissing(pendingValues, gpuHardwareWattageSensor) && snap.HasPowerW)
            {
                pendingValues[gpuHardwareWattageSensor] = (float)snap.GpuPowerW;
                filled++;
            }
            if (IsMissing(pendingValues, GPUTemperature) && snap.HasTemperatureC)
            {
                pendingValues[GPUTemperature] = (float)snap.GpuTemperatureC;
                filled++;
            }
            if (IsMissing(pendingValues, GPUMemoryClock) && snap.HasVramClockMHz)
            {
                pendingValues[GPUMemoryClock] = snap.VramClockMHz;
                filled++;
            }

            if (filled > 0)
            {
                long nowUtc = DateTime.UtcNow.Ticks;
                if (nowUtc - lastAdlxFallbackLogTicksUtc >= TimeSpan.TicksPerMinute)
                {
                    lastAdlxFallbackLogTicksUtc = nowUtc;
                    Logger.Info($"GPU sensor ADLX fallback filled {filled} value(s) this tick. usage={(snap.HasUsage ? snap.UsagePercent.ToString("F0") : "-")} clock={(snap.HasClockMHz ? snap.GpuClockMHz.ToString() : "-")}MHz temp={(snap.HasTemperatureC ? snap.GpuTemperatureC.ToString("F0") : "-")}C power={(snap.HasPowerW ? snap.GpuPowerW.ToString("F1") : "-")}W vramClock={(snap.HasVramClockMHz ? snap.VramClockMHz.ToString() : "-")}MHz");
                }
            }
        }

        private static bool IsMissing(Dictionary<HardwareSensor, float> pending, HardwareSensor key)
        {
            return pending.TryGetValue(key, out float v) && v < 0;
        }

        /// <summary>
        /// Processes sensors for a given hardware device and updates pending values dictionary.
        /// Values are written to sensors atomically after all processing completes.
        /// </summary>
        private void ProcessHardwareSensors(IHardware hardware, Dictionary<HardwareSensor, float> pendingValues)
        {
            foreach (ISensor sensor in hardware.Sensors)
            {
                // Match ALL sensors that match this hardware sensor (don't break on first)
                // This allows sensors like GPUTemperature and VRMTemperature to share the same reading
                float newValue = sensor.Value ?? -1;
                foreach (var hardwareSensor in hardwareSensors)
                {
                    if (hardwareSensor.MatchesHardwareType(hardware.HardwareType) &&
                        hardwareSensor.SensorType == sensor.SensorType &&
                        hardwareSensor.MatchesSensorName(sensor.Name))
                    {
                        // Prefer non-zero valid values over zero or invalid values
                        // This handles dual-GPU scenarios (iGPU + dGPU) where iGPU may report 0W
                        // while dGPU has the actual power reading
                        float currentValue = pendingValues[hardwareSensor];
                        bool currentIsValid = currentValue >= 0;
                        bool currentIsNonZero = currentValue > 0;
                        bool newIsValid = newValue >= 0;
                        bool newIsNonZero = newValue > 0;

                        // Update if:
                        // 1. Current value is invalid (-1), OR
                        // 2. New value is non-zero (prefer actual readings over 0)
                        // Don't overwrite a non-zero valid value with zero
                        if (!currentIsValid || newIsNonZero || (!currentIsNonZero && newIsValid))
                        {
                            pendingValues[hardwareSensor] = newValue;
                        }
                    }
                }
            }
        }

        public int GetTDP()
        {
            if (ryzenAdjHandle == IntPtr.Zero)
            {
                Logger.Info("RyzenAdj not initialized");
                return 10;
            }

            RyzenAdj.refresh_table(ryzenAdjHandle);
            return (int)RyzenAdj.get_fast_limit(ryzenAdjHandle);
        }

        public void SetTDP(int tdp)
        {
            // Debounce rapid TDP changes to prevent queue buildup
            // Only the final value will be applied after the debounce delay
            lock (debounceLock)
            {
                pendingTDP = tdp;

                // Cancel existing timer and start a new one
                tdpDebounceTimer?.Dispose();
                tdpDebounceTimer = new System.Threading.Timer(
                    _ => ApplyPendingTDP(),
                    null,
                    TDPDebounceDelayMs,
                    System.Threading.Timeout.Infinite // Don't repeat
                );

                Logger.Debug($"SetTDP: Debouncing TDP change to {tdp}W (will apply in {TDPDebounceDelayMs}ms if no new changes)");
            }
        }

        /// <summary>
        /// Called by the debounce timer to apply the pending TDP value.
        /// </summary>
        private void ApplyPendingTDP()
        {
            int tdpToApply;
            lock (debounceLock)
            {
                if (pendingTDP < 0)
                {
                    return; // No pending TDP
                }
                tdpToApply = pendingTDP;
                pendingTDP = -1; // Clear pending
            }

            ApplyTDPInternal(tdpToApply);
        }

        /// <summary>
        /// Internal method that actually applies the TDP to hardware.
        /// Called after debouncing completes.
        /// </summary>
        private void ApplyTDPInternal(int tdp)
        {
            // Lock to prevent race conditions from multiple sources calling SetTDP simultaneously
            // (widget slider, AutoTDP, TDP Boost changes, profile switches)
            lock (tdpLock)
            {
                var settingsManager = SettingsManager.GetInstance();
                TdpMethod tdpMethod = settingsManager?.TdpMethod?.Method ?? TdpMethod.ManufacturerWMI;
                bool legionDetected = legionManager?.LegionGoDetected?.Value ?? false;

                // Calculate actual TDP values based on TDP Boost settings
                // When boost is enabled: SPPT = TDP + boost_sppt, FPPT = TDP + boost_fppt
                // SPL/STAPM stays at base TDP value
                int spl = tdp;
                int sppt = tdp;
                int fppt = tdp;

                if (tdpBoostEnabled?.Value == true)
                {
                    int spptBoost = tdpBoostSPPT?.Value ?? 1;
                    int fpptBoost = tdpBoostFPPT?.Value ?? 3;
                    sppt = tdp + spptBoost;
                    fppt = tdp + fpptBoost;
                    Logger.Info($"TDP Boost enabled: SPL={spl}W, SPPT={sppt}W (+{spptBoost}), FPPT={fppt}W (+{fpptBoost})");
                }

                // Note: CurrentSPL/SPPT/FPPT are now updated from actual hardware values
                // in UpdateCurrentTDP, which is called via ScheduleVerificationRead after SetTDP

                Logger.Info($"SetTDP: method={tdpMethod}, legionDetected={legionDetected}, pawnIOAvailable={pawnIOAvailable}");

                // Use the selected TDP method
                switch (tdpMethod)
                {
                    case TdpMethod.ManufacturerWMI:
                        if (legionDetected && legionManager != null)
                        {
                            // Note: SetCustomTDP will automatically switch to Custom mode (255) if needed
                            Logger.Info($"Using Legion WMI to set TDP (SPL={spl}W, SPPL={sppt}W, FPPT={fppt}W)");
                            legionManager.SetCustomTDP(spl, sppt, fppt);
                            ScheduleVerificationRead();
                            return;
                        }
                        // Legion not detected - fall through to PawnIO
                        Logger.Warn("ManufacturerWMI selected but Legion not detected, trying PawnIO");
                        goto case TdpMethod.PawnIO;

                    case TdpMethod.PawnIO:
                        if (pawnIOAvailable && ryzenSmuService != null && ryzenSmuService.IsInitialized)
                        {
                            if (legionDetected)
                            {
                                // #94 (Marctraider): SMU writes on Legion devices get
                                // re-asserted by the embedded controller within seconds —
                                // his log shows SetAllLimits(35W) "succeeding" while the
                                // EC kept SPL at 25W. The write is accepted by the SMU
                                // but the platform owns the power table.
                                Logger.Warn("PawnIO TDP method selected on a Legion device: the Lenovo EC typically re-asserts its own power limits over SMU writes within seconds. Lenovo WMI is the supported method on this hardware.");
                            }
                            Logger.Info($"Using PawnIO/RyzenSMU to set TDP (SPL={spl}W, SPPT={sppt}W, FPPT={fppt}W)");
                            if (ryzenSmuService.SetAllLimits(spl, sppt, fppt))
                            {
                                Logger.Info($"PawnIO: TDP set commands accepted");
                                if (legionDetected && legionManager != null)
                                {
                                    // On Legion hardware the WMI read shows what the EC
                                    // actually enforces — let the verification read fill
                                    // Current* so the OSD/widget report the truth instead
                                    // of echoing the requested values.
                                    ScheduleLegionPawnIOVerificationRead();
                                    return;
                                }
                                // No independent read-back path on non-Legion hardware —
                                // assume the requested values took effect.
                                CurrentSPL = spl;
                                CurrentSPPT = sppt;
                                CurrentFPPT = fppt;
                                // Update the currentTdp property for widget display
                                var newTdpString = $"SPL:{spl}W SPPT:{sppt}W FPPT:{fppt}W";
                                if (newTdpString != lastTdpString)
                                {
                                    currentTdp.SetValue(newTdpString);
                                    lastTdpString = newTdpString;
                                }
                                return;
                            }
                            Logger.Warn("PawnIO: Failed to set TDP");
                        }
                        else
                        {
                            Logger.Warn("PawnIO not available");
                        }
                        Logger.Warn("SetTDP: No TDP control method available");
                        return;

                    // WinRing0 removed - deprecated TDP method, no longer bundled
                    // case TdpMethod.WinRing0:
                    //     // RyzenAdj (deprecated - WinRing0 no longer bundled)
                    //     if (!EnsureRyzenAdjInitialized())
                    //     {
                    //         Logger.Warn("SetTDP: WinRing0/RyzenAdj unavailable");
                    //         return;
                    //     }
                    //     Logger.Info($"Using RyzenAdj to set TDP (STAPM={spl}W, SLOW={sppt}W, FAST={fppt}W) (deprecated)");
                    //     RyzenAdj.set_fast_limit(ryzenAdjHandle, (uint)(fppt * 1000));
                    //     RyzenAdj.set_slow_limit(ryzenAdjHandle, (uint)(sppt * 1000));
                    //     RyzenAdj.set_stapm_limit(ryzenAdjHandle, (uint)(spl * 1000));
                    // #if DEBUG
                    //     RyzenAdj.refresh_table(ryzenAdjHandle);
                    //     Logger.Info($"Set TDP, current fast limit is {RyzenAdj.get_fast_limit(ryzenAdjHandle)}");
                    // #endif
                    //     break;
                }
            }

            // Schedule a verification read after a short delay to confirm the TDP was applied
            ScheduleVerificationRead();
        }

        /// <summary>
        /// Schedules a delayed verification read of TDP values after SetTDP.
        /// This allows the hardware time to apply the new values before reading back.
        /// </summary>
        private void ScheduleVerificationRead()
        {
            System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    await System.Threading.Tasks.Task.Delay(VerificationDelayMs);
                    UpdateCurrentTDP(null, null);
                    Logger.Debug("ScheduleVerificationRead: Verification read completed");
                }
                catch (Exception ex)
                {
                    Logger.Debug($"ScheduleVerificationRead: Error during verification read: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Delayed Legion-WMI read used specifically after a PawnIO/RyzenSMU write on Legion
        /// hardware, where the Lenovo EC silently re-asserts its own power table over the SMU
        /// write within seconds. Deliberately does NOT go through UpdateCurrentTDP — that method
        /// only reads Legion WMI when TdpMethod==ManufacturerWMI (its "Priority 1" gate), so
        /// calling it here (TdpMethod==PawnIO) would hit the dead WinRing0-removed fallback and
        /// silently do nothing, leaving the OSD stuck on the last value instead of showing what
        /// the EC actually enforces.
        /// </summary>
        private void ScheduleLegionPawnIOVerificationRead()
        {
            System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    await System.Threading.Tasks.Task.Delay(VerificationDelayMs);
                    if (legionManager == null) return;

                    var (slow, fast, peak) = legionManager.GetCurrentTDPValues();
                    if (!slow.HasValue || !fast.HasValue || !peak.HasValue)
                    {
                        Logger.Debug("ScheduleLegionPawnIOVerificationRead: Could not read all TDP values from Legion WMI");
                        return;
                    }

                    CurrentSPL = slow.Value;
                    CurrentSPPT = fast.Value;
                    CurrentFPPT = peak.Value;

                    var newTdpString = $"SPL:{slow}W SPPT:{fast}W FPPT:{peak}W";
                    if (newTdpString != lastTdpString)
                    {
                        Logger.Info($"ScheduleLegionPawnIOVerificationRead: EC-enforced TDP is '{newTdpString}' (PawnIO write may not have stuck)");
                        currentTdp.SetValue(newTdpString);
                        lastTdpString = newTdpString;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Debug($"ScheduleLegionPawnIOVerificationRead: Error during verification read: {ex.Message}");
                }
            });
        }

        // WinRing0 removed - deprecated TDP method, no longer bundled
        // /// <summary>
        // /// Lazy-loads RyzenAdj when needed. RyzenAdj is deprecated as WinRing0 is no longer bundled.
        // /// This will only succeed if the user has WinRing0 files in C:\GoTweaks.
        // /// Copies libryzenadj.dll from app bundle to C:\GoTweaks so all files are together.
        // /// </summary>
        // /// <returns>True if RyzenAdj is available</returns>
        // private bool EnsureRyzenAdjInitialized()
        // {
        //     // Lock to prevent double-initialization race condition when multiple threads
        //     // try to initialize RyzenAdj simultaneously
        //     lock (ryzenAdjInitLock)
        //     {
        //         if (ryzenAdjInitialized)
        //             return ryzenAdjHandle != IntPtr.Zero;
        //
        //         if (ryzenAdjInitAttempted)
        //             return false; // Already tried and failed
        //
        //         ryzenAdjInitAttempted = true;
        //
        //         // Check if WinRing0 files are bundled
        //         if (!winRing0Available)
        //         {
        //             Logger.Warn("EnsureRyzenAdjInitialized: WinRing0 files not bundled");
        //             return false;
        //         }
        //
        //         try
        //         {
        //             // Load from helper's bundled directory (all files are together)
        //             string helperDir = AppDomain.CurrentDomain.BaseDirectory;
        //             RyzenAdj.LoadFromFolder(helperDir);
        //             ryzenAdjHandle = RyzenAdj.init_ryzenadj();
        //
        //             if (ryzenAdjHandle == IntPtr.Zero)
        //             {
        //                 // Get WinRing0 status for diagnostics
        //                 uint finalStatus = RyzenAdj.GetLastWinRing0Status();
        //                 string statusDesc = RyzenAdj.GetWinRing0StatusDescription(finalStatus);
        //                 Logger.Warn($"RyzenAdj initialization failed - WinRing0 status: {finalStatus} ({statusDesc})");
        //                 return false;
        //             }
        //
        //             RyzenAdj.refresh_table(ryzenAdjHandle);
        //             var stapm = (int)RyzenAdj.get_stapm_limit(ryzenAdjHandle);
        //             var fast = (int)RyzenAdj.get_fast_limit(ryzenAdjHandle);
        //             var slow = (int)RyzenAdj.get_slow_limit(ryzenAdjHandle);
        //
        //             if (stapm > 0 && fast > 0 && slow > 0 &&
        //                 stapm != int.MinValue && fast != int.MinValue && slow != int.MinValue)
        //             {
        //                 Logger.Info($"RyzenAdj initialized successfully - STAPM:{stapm}W FAST:{fast}W SLOW:{slow}W");
        //                 ryzenAdjInitialized = true;
        //                 return true;
        //             }
        //             else
        //             {
        //                 Logger.Warn($"RyzenAdj returned invalid values - STAPM:{stapm}W FAST:{fast}W SLOW:{slow}W");
        //                 return false;
        //             }
        //         }
        //         catch (Exception ex)
        //         {
        //             Logger.Error($"RyzenAdj initialization failed: {ex.Message}");
        //             ryzenAdjHandle = IntPtr.Zero;
        //             return false;
        //         }
        //     }
        // }

        // WinRing0 removed - deprecated TDP method, no longer bundled
        // private void ReinitializeRyzenAdj()
        // {
        //     // Lock to ensure thread-safe reinitialization
        //     lock (ryzenAdjInitLock)
        //     {
        //         Logger.Info("ReinitializeRyzenAdj: Attempting to reinitialize RyzenAdj handle");
        //
        //         // Clean up old handle if it exists
        //         if (ryzenAdjHandle != IntPtr.Zero)
        //         {
        //             try
        //             {
        //                 RyzenAdj.cleanup_ryzenadj(ryzenAdjHandle);
        //                 Logger.Info("ReinitializeRyzenAdj: Old handle cleaned up");
        //             }
        //             catch (Exception ex)
        //             {
        //                 Logger.Warn($"ReinitializeRyzenAdj: Error cleaning up old handle: {ex.Message}");
        //             }
        //         }
        //
        //         // Reset flags and try again
        //         ryzenAdjInitialized = false;
        //         ryzenAdjInitAttempted = false;
        //     }
        //
        //     // Call outside lock since EnsureRyzenAdjInitialized acquires the same lock
        //     if (EnsureRyzenAdjInitialized())
        //     {
        //         consecutiveReadFailures = 0;
        //     }
        // }
        //
        // private (int stapm, int fast, int slow) TryReadTdpValues()
        // {
        //     RyzenAdj.refresh_table(ryzenAdjHandle);
        //     var stapm = (int)RyzenAdj.get_stapm_limit(ryzenAdjHandle);
        //     var fast = (int)RyzenAdj.get_fast_limit(ryzenAdjHandle);
        //     var slow = (int)RyzenAdj.get_slow_limit(ryzenAdjHandle);
        //     return (stapm, fast, slow);
        // }
        //
        // private bool AreValuesValid(int stapm, int fast, int slow)
        // {
        //     return stapm > 0 && fast > 0 && slow > 0 &&
        //            stapm != int.MinValue && fast != int.MinValue && slow != int.MinValue;
        // }

        private void UpdateCurrentTDP(object sender, System.Timers.ElapsedEventArgs e)
        {
            try
            {
                // Check selected TDP method
                var settingsManager = SettingsManager.GetInstance();
                TdpMethod tdpMethod = settingsManager?.TdpMethod?.Method ?? TdpMethod.ManufacturerWMI;
                bool legionDetected = legionManager?.LegionGoDetected?.Value ?? false;

                // Priority 1: Legion WMI (when ManufacturerWMI selected and Legion is detected)
                if (tdpMethod == TdpMethod.ManufacturerWMI && legionDetected && legionManager != null)
                {
                    // Check current performance mode - if not Custom, show mode name instead of TDP values
                    int performanceMode = legionManager.CurrentPerformanceMode;
                    if (performanceMode != 255) // Not Custom mode
                    {
                        string modeName = LegionManager.GetPerformanceModeName(performanceMode);
                        if (modeName != lastTdpString)
                        {
                            Logger.Info($"UpdateCurrentTDP: Mode changed to '{modeName}', sending update");
                            currentTdp.SetValue(modeName);
                            lastTdpString = modeName;
                        }
                        return;
                    }

                    // Custom mode - use Legion WMI to get TDP values
                    var (slow, fast, peak) = legionManager.GetCurrentTDPValues();

                    if (slow.HasValue && fast.HasValue && peak.HasValue)
                    {
                        // Update actual hardware limits for OSD display
                        CurrentSPL = slow.Value;
                        CurrentSPPT = fast.Value;
                        CurrentFPPT = peak.Value;

                        var newTdpString = $"SPL:{slow}W SPPL:{fast}W FPPT:{peak}W";
                        Logger.Debug($"UpdateCurrentTDP (Legion WMI): Read values - {newTdpString}");

                        if (newTdpString != lastTdpString)
                        {
                            Logger.Info($"UpdateCurrentTDP: Value changed from '{lastTdpString}' to '{newTdpString}', sending update");
                            currentTdp.SetValue(newTdpString);
                            lastTdpString = newTdpString;
                        }
                    }
                    else
                    {
                        Logger.Debug("UpdateCurrentTDP (Legion WMI): Could not read all TDP values");
                    }
                    return;
                }

                // WinRing0 removed - deprecated TDP method, no longer bundled
                // RyzenAdj/WinRing0 TDP reading is no longer available
                return;

                // // Only use RyzenAdj when WinRing0 method is explicitly selected
                // if (tdpMethod != TdpMethod.WinRing0)
                // {
                //     Logger.Debug("UpdateCurrentTDP: TDP method is not WinRing0, skipping RyzenAdj");
                //     return;
                // }
                //
                // // Initialize RyzenAdj (lazy-load, copies WinRing0 files from C:\GoTweaks)
                // if (!EnsureRyzenAdjInitialized())
                // {
                //     Logger.Debug("UpdateCurrentTDP: RyzenAdj not available");
                //     return;
                // }
                //
                // Logger.Debug("UpdateCurrentTDP: Reading TDP limits from hardware");
                //
                // // Try reading values with retry
                // var (stapm, fastVal, slowVal) = TryReadTdpValues();
                //
                // // If first read fails, retry up to 2 more times with small delay
                // int retryCount = 0;
                // while (!AreValuesValid(stapm, fastVal, slowVal) && retryCount < 2)
                // {
                //     retryCount++;
                //     Logger.Debug($"UpdateCurrentTDP: Read attempt {retryCount + 1} failed, retrying...");
                //     System.Threading.Thread.Sleep(100); // Brief delay before retry
                //     (stapm, fastVal, slowVal) = TryReadTdpValues();
                // }
                //
                // // Check for invalid values (int.MinValue indicates read failure)
                // if (!AreValuesValid(stapm, fastVal, slowVal))
                // {
                //     consecutiveReadFailures++;
                //     Logger.Debug($"UpdateCurrentTDP: Invalid values read (STAPM:{stapm}, FAST:{fastVal}, SLOW:{slowVal}), failure count: {consecutiveReadFailures}");
                //
                //     // Backoff timer to reduce CPU usage during failures
                //     if (currentTdpTimer != null && currentTdpTimer.Interval != BackoffTimerInterval)
                //     {
                //         currentTdpTimer.Interval = BackoffTimerInterval;
                //         Logger.Info($"UpdateCurrentTDP: Backing off timer to {BackoffTimerInterval}ms due to failures");
                //     }
                //
                //     // If we've had too many consecutive failures, try reinitializing
                //     if (consecutiveReadFailures >= MaxConsecutiveFailuresBeforeReinit)
                //     {
                //         Logger.Warn($"UpdateCurrentTDP: {consecutiveReadFailures} consecutive read failures, reinitializing RyzenAdj");
                //         ReinitializeRyzenAdj();
                //     }
                //     return;
                // }
                //
                // // Reset failure counter on successful read
                // if (consecutiveReadFailures > 0)
                // {
                //     Logger.Info($"UpdateCurrentTDP: Read succeeded after {consecutiveReadFailures} failures");
                //     consecutiveReadFailures = 0;
                //
                //     // Restore normal timer interval
                //     if (currentTdpTimer != null && currentTdpTimer.Interval != NormalTimerInterval)
                //     {
                //         currentTdpTimer.Interval = NormalTimerInterval;
                //         Logger.Info($"UpdateCurrentTDP: Restored timer to {NormalTimerInterval}ms");
                //     }
                // }
                //
                // // Update actual hardware limits for OSD display
                // // RyzenAdj: STAPM=SPL (base), SLOW=SPPT, FAST=FPPT
                // CurrentSPL = stapm;
                // CurrentSPPT = slowVal;
                // CurrentFPPT = fastVal;
                //
                // // Only show limits (power consumption methods not working on this hardware)
                // var newTdpStringRyzen = $"STAPM:{stapm}W FAST:{fastVal}W SLOW:{slowVal}W";
                // Logger.Debug($"UpdateCurrentTDP: Read values - {newTdpStringRyzen}");
                //
                // // Only update if value has changed to reduce IPC traffic
                // if (newTdpStringRyzen != lastTdpString)
                // {
                //     Logger.Info($"UpdateCurrentTDP: Value changed from '{lastTdpString}' to '{newTdpStringRyzen}', sending update");
                //     currentTdp.SetValue(newTdpStringRyzen);
                //     lastTdpString = newTdpStringRyzen;
                // }
                // else
                // {
                //     Logger.Debug($"UpdateCurrentTDP: Value unchanged ({newTdpStringRyzen}), skipping update");
                // }
            }
            catch (Exception ex)
            {
                consecutiveReadFailures++;
                Logger.Error($"Error updating current TDP: {ex.Message}");
                Logger.Error($"Stack trace: {ex.StackTrace}");

                // WinRing0 removed - RyzenAdj no longer used
                // if (consecutiveReadFailures >= MaxConsecutiveFailuresBeforeReinit)
                // {
                //     Logger.Warn($"UpdateCurrentTDP: {consecutiveReadFailures} consecutive failures with exceptions, reinitializing RyzenAdj");
                //     ReinitializeRyzenAdj();
                // }
            }
        }

        /// <summary>
        /// Pushes bundled metrics data to the widget. Called from Update() each tick
        /// when QuickMetricsEnabled is true so we reuse the same sensor refresh that
        /// fed local state instead of doing a second pass on a separate timer.
        /// </summary>
        private void PushQuickMetrics()
        {
            try
            {
                // Build JSON with all sensor values for flexible display
                float batteryDrain = BatteryDischargeRate.Value > 0 ? BatteryDischargeRate.Value : -BatteryChargeRate.Value;
                float cpuUsage = CPUUsage.Value;
                float gpuUsage = GPUUsage.Value;
                float cpuTemp = CPUTemperature.Value;
                float gpuTemp = GPUTemperature.Value;
                float cpuWattage = CPUWattage.Value;
                float gpuWattage = GPUWattage.Value;
                float memoryUsage = MemoryUsage.Value;
                float batteryLevel = BatteryLevel.Value;
                float timeRemaining = BatteryTimeRemaining;
                float timeToFull = BatteryTimeToFull;
                bool isCharging = BatteryChargeRate.Value > 0;

                // Format as JSON with all metrics
                string json = $"{{" +
                    $"\"batteryDrain\":{batteryDrain:F1}," +
                    $"\"cpuUsage\":{cpuUsage:F0}," +
                    $"\"gpuUsage\":{gpuUsage:F0}," +
                    $"\"cpuTemp\":{cpuTemp:F0}," +
                    $"\"gpuTemp\":{gpuTemp:F0}," +
                    $"\"cpuWattage\":{cpuWattage:F1}," +
                    $"\"gpuWattage\":{gpuWattage:F1}," +
                    $"\"memoryUsage\":{memoryUsage:F0}," +
                    $"\"batteryLevel\":{batteryLevel:F0}," +
                    $"\"timeRemaining\":{timeRemaining:F0}," +
                    $"\"timeToFull\":{timeToFull:F0}," +
                    $"\"isCharging\":{(isCharging ? "true" : "false")}}}";

                // Send via named pipe
                var message = new Shared.IPC.PipeMessage
                {
                    Command = Shared.Enums.Command.Response,
                    Function = Shared.Enums.Function.QuickMetrics,
                    Content = json
                };

                Program.SendPipeMessage(message);
            }
            catch (Exception ex)
            {
                Logger.Error($"Error pushing Quick Metrics: {ex.Message}");
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Logger.Info("PerformanceManager: Disposing resources");

                // Stop and dispose the timer
                if (currentTdpTimer != null)
                {
                    currentTdpTimer.Stop();
                    currentTdpTimer.Elapsed -= UpdateCurrentTDP;
                    currentTdpTimer.Dispose();
                    currentTdpTimer = null;
                    Logger.Info("PerformanceManager: Timer disposed");
                }

                // Clean up PawnIO/RyzenSMU
                if (ryzenSmuService != null)
                {
                    try
                    {
                        ryzenSmuService.Dispose();
                        Logger.Info("PerformanceManager: PawnIO/RyzenSMU disposed");
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"PerformanceManager: Error disposing PawnIO: {ex.Message}");
                    }
                    ryzenSmuService = null;
                    pawnIOAvailable = false;
                }

                // Clean up RyzenAdj handle
                if (ryzenAdjHandle != IntPtr.Zero)
                {
                    try
                    {
                        RyzenAdj.cleanup_ryzenadj(ryzenAdjHandle);
                        Logger.Info("PerformanceManager: RyzenAdj handle cleaned up");
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"PerformanceManager: Error cleaning up RyzenAdj: {ex.Message}");
                    }
                    ryzenAdjHandle = IntPtr.Zero;
                }

                // Dispose LibreHardwareMonitor computer
                if (computer != null)
                {
                    try
                    {
                        computer.Close();
                        Logger.Info("PerformanceManager: LibreHardwareMonitor closed");
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"PerformanceManager: Error closing LibreHardwareMonitor: {ex.Message}");
                    }
                    computer = null;
                }
            }

            base.Dispose(disposing);
        }
    }
}
