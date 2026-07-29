using NLog;
using Shared.Data;
using Shared.Enums;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage;
using XboxGamingBarHelper.Core;
using XboxGamingBarHelper.Devices;
using XboxGamingBarHelper.Devices.Libraries.Legion;
using XboxGamingBarHelper.Performance;

namespace XboxGamingBarHelper.Devices.Libraries.Legion
{
    /// <summary>
    /// Manager for Legion Go hardware features including controller RGB, touchpad, and performance modes.
    /// </summary>
    internal partial class LegionManager : Manager
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        // Legion Go services
        private LegionControllerService controllerService;
        private LegionGoSController goSController;  // Legion Go S uses different HID protocol
        private LenovoWMIService wmiService;

        // Device detection
        private DeviceInfo deviceInfo;
        private bool isLegionGoDetected = false;

        /// <summary>
        /// Fired when the controller service reconnects after a drop - including the
        /// Go 2 receiver re-presenting under a different USB PID (half sleep/wake or
        /// dock state change). Wired in Program.cs to re-assert HidHide suppression
        /// and replay joystick-as-mouse firmware state.
        /// </summary>
        public event Action ControllerReconnected;
        private bool isControllerConnected = false;
        private bool isGoSControllerConnected = false;  // Tracks Go S RGB controller connection

        // Current settings state (cached)
        private bool touchpadEnabled = true;
        private int lightMode = 1; // Solid
        private string lightColor = "#FFFFFF";
        private int lightBrightness = 100;
        private int lightSpeed = 50; // 0-100, default 50%
        private int performanceMode = 2; // Balanced

        // True hardware light state from the b0:01 readback (LegionManager.ControllerBattery.cs).
        // Preferred by the reactive-lighting getters over the stale lightBrightness/lightColor
        // defaults so reactive effects don't blast 100% before the widget reconnects after a restart.
        private bool _hasHwLightState;
        private int _hwLightBrightness;
        private byte _hwLightColorR, _hwLightColorG, _hwLightColorB;

        // True once the light state is known from a real source: a b0:01 hardware readback (even
        // when the light is off) or a widget/profile sync of color/brightness/speed. Until then the
        // lightMode/lightColor/lightBrightness fields hold uninitialized defaults (Solid/#FFFFFF/100)
        // that must NOT be pushed to hardware — doing so on a cold helper start (Kill/Update) flashed
        // a full-brightness white before the widget synced the user's real values (#81 white stick flash).
        private bool _lightStateKnown;
        // Internal-convention light mode (0=Off,1=Solid,2=Pulse,3=Dynamic,4=Spiral) captured from the
        // b0:01 readback so a restore reflects the real hardware mode (including Off) instead of default Solid.
        private int _hwLightMode = -1;

        /// <summary>
        /// Gets the current performance mode (Quiet=1, Balanced=2, Performance=3, Custom=255).
        /// </summary>
        public int CurrentPerformanceMode => performanceMode;

        /// <summary>
        /// Gets the display name for a performance mode value.
        /// </summary>
        public static string GetPerformanceModeName(int mode)
        {
            return mode switch
            {
                1 => "Quiet Mode",
                2 => "Balanced Mode",
                3 => "Performance Mode",
                255 => "Custom",
                _ => $"Mode {mode}"
            };
        }
        private int customTDPSlow = 15;
        private int customTDPFast = 25;
        private int customTDPPeak = 35;
        private bool fanFullSpeed = false;
        private ushort[] fanCurve = new ushort[10] { 44, 48, 55, 60, 71, 79, 87, 87, 100, 100 }; // active Legion Go fan curve — points to whichever per-mode slot is currently selected
        // Per-mode curve storage (Rodpad LeGo2-Fan-Control pattern). Each TdpMode value
        // (Quiet=1, Balanced=2, Performance=3, Custom=255) keeps its own 10-point fan
        // curve; mode change swaps the active curve. Persisted as separate LocalSettings
        // keys (LegionFanCurve_<mode>) with the legacy "LegionFanCurve" key kept in sync
        // with the active mode for backward compat.
        private readonly Dictionary<int, ushort[]> fanCurvesByMode = new Dictionary<int, ushort[]>();
        private static readonly int[] AllPerformanceModes = { 1, 2, 3, 255 };
        // EC override unlock — now per-power-mode. Each TdpMode (1=Quiet, 2=Balanced,
        // 3=Performance, 255=Custom) stores its own on/off state. Mode change → look up
        // the new mode's setting; start the EC loop if true, stop if false. Persisted as
        // LegionUnlockFanCurve_<mode>; legacy LegionUnlockFanCurve key kept in sync with
        // the active mode for backward compat.
        private bool fanCurveUnlocked = false;
        private readonly Dictionary<int, bool> ecOverrideByMode = new Dictionary<int, bool>();
        private LegionGo2EcAccess legionEcAccess; // lazily initialized when fanCurveUnlocked first toggled on
        private System.Threading.Timer ecFanCurveTimer; // 3s tick that re-applies the curve via direct EC write
        private const int EC_FAN_TICK_MS = 3000;
        private const int EC_FAN_MAX_RPM = 7000; // empirical Legion Go 2 fan ceiling — 100% on the curve maps to this
        private static readonly int[] LegionFanCurveTemps = { 10, 20, 30, 40, 50, 60, 70, 80, 90, 100 };

        // Anchor-point hysteresis state (Rodpad LeGo2-Fan-Control pattern). The fan target
        // only re-evaluates when temp has drifted ≥5°C from the anchor, preventing yo-yo
        // behaviour on small temp fluctuations. Forced re-write every 30s defeats firmware
        // overrides that may stomp our 0xC6C8 value.
        private int ecAnchorTempC = int.MinValue; // temp at last anchor recompute (sentinel: not yet set)
        private int ecCurrentTargetRpm = -1;      // RPM we're actively asserting on the EC
        private int ecLastWrittenRpm = -1;        // last value actually written via EC mailbox
        private DateTime ecLastForcedRewriteUtc = DateTime.MinValue;
        // Consecutive EC write failures. The PawnIO handle can silently die
        // across sleep/hibernate; after this many failed ticks the loop
        // rebuilds the whole access path via LegionGo2EcAccess.Reinitialize().
        private int ecConsecutiveWriteFailures = 0;
        private const int EC_WRITE_FAILURES_BEFORE_REINIT = 3;
        private const int EC_ANCHOR_TEMP_DELTA_C = 5;       // recompute target when |temp - anchor| ≥ this
        private const int EC_FORCED_REWRITE_INTERVAL_S = 30; // re-assert override even if target unchanged

        // Software thermal failsafe (Rodpad LeGo2-Fan-Control "panic mode"). When CPU
        // temperature meets/exceeds EC_FAN_PANIC_TEMP_C, bypass anchor + curve and force
        // the fan to max(EC_FAN_PANIC_MIN_RPM, curveMaxRpm). Belt-and-braces alongside
        // the EC firmware's own thermal failsafe (which engages at the same threshold);
        // ours fires from the helper-side temp source so misbehaving curves can't keep
        // the fan low even if the EC's own sensor briefly under-reads.
        private const int EC_FAN_PANIC_TEMP_C = 101;
        private const int EC_FAN_PANIC_MIN_RPM = 4800; // matches the EC firmware's own failsafe RPM
        private bool ecInPanicMode = false; // sticky log latch — log once when entering, once when leaving
        private bool ecInFullSpeed = false; // sticky log latch for the Fan Full Speed override
        private bool gyroEnabled = false;
        private int vibrationLevel = 2; // Medium
        private bool powerLightEnabled = true;
        private bool chargeLimitEnabled = false;

        // Controller remapping state (cached)
        private bool nintendoLayoutEnabled = false;
        private int vibrationMode = 1; // FPS

        // HID command timing - delay between commands for firmware to process
        private const int HID_COMMAND_DELAY_MS = 20; // Reduced from 50ms for faster profile switches

        // Controller battery monitoring (uses controllerService, not a separate connection)
        private int leftControllerBattery = -1;
        private int rightControllerBattery = -1;
        private bool leftControllerCharging = false;
        private bool rightControllerCharging = false;
        private bool leftControllerConnected = false;
        private bool rightControllerConnected = false;

        // Fan speed (RPM)
        private int cpuFanSpeed = 0;

        // Fan curve visibility (widget tells us when to push temp/RPM updates)
        private bool fanCurveVisible = false;

        /// <summary>
        /// True when the fan-curve UI panel is open in the widget. Read by
        /// PerformanceManager's consumer-check to keep CPU/VRM temps fresh while
        /// the user has the panel open.
        /// </summary>
        public bool IsFanCurveVisible => fanCurveVisible;

        // True only when the EC fan override timer is actually ticking. Used by
        // PerformanceManager's metrics-consumer check so the LHM sensor walk does not gate while
        // the loop is running — the cached CPUTemperature would otherwise sit at the last
        // reading (e.g. 68°C from gameplay) while the device is idle/screen-off and the loop
        // would keep writing the matching high-RPM target until something else wakes the walk
        // up. Gates strictly on ecFanCurveTimer (not fanCurveUnlocked + legionEcAccess) so a
        // toggled-on-but-EC-init-failed configuration (PawnIO missing) does not keep sensors
        // walking pointlessly. (kayti #88 fan stays loud after sleep, gap ~90 s in her capture.)
        public bool IsEcFanOverrideActive => ecFanCurveTimer != null;

        // TDP reapply timer (used when switching to Custom mode)
        private System.Timers.Timer tdpReapplyTimer;
        private int pendingTdpSlow;
        private int pendingTdpFast;
        private int pendingTdpPeak;

        // Cooldown to prevent RefreshTDPValuesFromDevice from overwriting recently-set values
        private DateTime lastTdpSetTime = DateTime.MinValue;
        private const int TDP_REFRESH_COOLDOWN_MS = 3000; // 3 seconds cooldown after setting TDP

        // Rate-limiting for fan speed refresh (WMI calls are CPU-expensive).
        // We refresh every FAN_SPEED_REFRESH_INTERVAL_MS when someone needs live fan
        // data (fan-curve UI open, OSD fan item visible, game running, sidebar open),
        // and back off to FAN_SPEED_IDLE_INTERVAL_MS when the helper is otherwise idle.
        private DateTime lastFanSpeedRefresh = DateTime.MinValue;
        private const int FAN_SPEED_REFRESH_INTERVAL_MS = 2000;
        private const int FAN_SPEED_IDLE_INTERVAL_MS = 10000;

        // Startup grace period to ignore LegionCustomTDP slider sync during widget startup
        // The main TDP slider calls SetCustomTDP which syncs all 3 values, so we don't want
        // the widget's stale cached values to override them
        private DateTime startupTime;
        private const int STARTUP_GRACE_PERIOD_MS = 5000; // 5 seconds after manager init

        // Performance mode debouncing to prevent queue buildup from rapid mode changes
        private System.Threading.Timer performanceModeDebounceTimer;
        private int pendingPerformanceMode = -1; // -1 means no pending mode
        private readonly object performanceModeDebounceLock = new object();
        private const int PERFORMANCE_MODE_DEBOUNCE_MS = 150;

        // Properties
        /// <summary>
        /// Gets the detected device information (manufacturer, model, features)
        /// </summary>
        public DeviceInfo DetectedDevice => deviceInfo ?? new DeviceInfo();

        public readonly LegionGoDetectedProperty LegionGoDetected;
        public readonly LegionTouchpadEnabledProperty LegionTouchpadEnabled;
        public readonly LegionLightModeProperty LegionLightMode;
        public readonly LegionLightColorProperty LegionLightColor;
        public readonly LegionLightBrightnessProperty LegionLightBrightness;
        public readonly LegionLightSpeedProperty LegionLightSpeed;
        public readonly LegionPerformanceModeProperty LegionPerformanceMode;
        public readonly LegionCustomTDPSlowProperty LegionCustomTDPSlow;
        public readonly LegionCustomTDPFastProperty LegionCustomTDPFast;
        public readonly LegionCustomTDPPeakProperty LegionCustomTDPPeak;
        public readonly LegionFanFullSpeedProperty LegionFanFullSpeed;
        public readonly LegionFanCurveDataProperty LegionFanCurveData;
        public readonly LegionUnlockFanCurveProperty LegionUnlockFanCurve;
        // Per-mode pipe channels — let the widget edit any mode's saved curve / unlock
        // state without changing the running power mode. Active-mode hardware writes
        // still go through LegionFanCurveData / LegionUnlockFanCurve.
        public readonly LegionFanCurvePerModeProperty LegionFanCurvePerMode;
        public readonly LegionUnlockFanCurvePerModeProperty LegionUnlockFanCurvePerMode;
        public readonly LegionCPUCurrentTempProperty LegionCPUCurrentTemp;
        public readonly LegionFanSensorTempProperty LegionFanSensorTemp;
        public readonly LegionCPUFanRPMProperty LegionCPUFanRPM;
        public readonly LegionFanCurveVisibleProperty LegionFanCurveVisible;
        public readonly LegionGyroEnabledProperty LegionGyroEnabled;
        public readonly LegionVibrationProperty LegionVibration;
        public readonly LegionPowerLightProperty LegionPowerLight;
        public readonly LegionChargeLimitProperty LegionChargeLimit;

        // Controller remapping properties
        public readonly LegionButtonY1Property LegionButtonY1;
        public readonly LegionButtonY2Property LegionButtonY2;
        public readonly LegionButtonY3Property LegionButtonY3;
        public readonly LegionButtonM1Property LegionButtonM1;
        public readonly LegionButtonM2Property LegionButtonM2;
        public readonly LegionButtonM3Property LegionButtonM3;
        public readonly LegionButtonDesktopProperty LegionButtonDesktop;
        public readonly LegionButtonPageProperty LegionButtonPage;
        public readonly LegionNintendoLayoutProperty LegionNintendoLayout;
        public readonly LegionVibrationModeProperty LegionVibrationMode;
        public readonly LegionControllerProfileEnabledProperty LegionControllerProfileEnabled;

        // Gyro settings properties (per-game profile)
        public readonly LegionGyroTargetProperty LegionGyroTarget;
        public readonly LegionGyroSensitivityXProperty LegionGyroSensitivityX;
        public readonly LegionGyroSensitivityYProperty LegionGyroSensitivityY;
        public readonly LegionGyroInvertXProperty LegionGyroInvertX;
        public readonly LegionGyroInvertYProperty LegionGyroInvertY;
        public readonly LegionGyroMappingTypeProperty LegionGyroMappingType;
        public readonly LegionGyroActivationModeProperty LegionGyroActivationMode;
        public readonly LegionGyroActivationButtonProperty LegionGyroActivationButton;

        // Advanced gyro properties (per-game profile)
        public readonly LegionGyroDeadzoneProperty LegionGyroDeadzone;

        // Stick deadzone properties (per-game profile)
        public readonly LegionLeftStickDeadzoneProperty LegionLeftStickDeadzone;
        public readonly LegionRightStickDeadzoneProperty LegionRightStickDeadzone;

        // Trigger travel properties (per-game profile)
        public readonly LegionLeftTriggerStartProperty LegionLeftTriggerStart;
        public readonly LegionLeftTriggerEndProperty LegionLeftTriggerEnd;
        public readonly LegionRightTriggerStartProperty LegionRightTriggerStart;
        public readonly LegionRightTriggerEndProperty LegionRightTriggerEnd;
        public readonly LegionHairTriggersProperty LegionHairTriggers;

        // Touchpad vibration property (GLOBAL setting)
        public readonly LegionTouchpadVibrationProperty LegionTouchpadVibration;

        // Joystick as mouse properties (per-game profile)
        public readonly LegionJoystickAsMouseModeProperty LegionJoystickAsMouseMode;
        public readonly LegionJoystickMouseSensProperty LegionJoystickMouseSens;

        // Gamepad button mapping (per-game profile)
        public readonly LegionGamepadMappingProperty LegionGamepadMapping;

        // Desktop controls preset (per-game profile state tracking)
        public readonly LegionDesktopControlsProperty LegionDesktopControls;

        // Controller battery properties
        public readonly ControllerBatteryLeftProperty ControllerBatteryLeft;
        public readonly ControllerBatteryRightProperty ControllerBatteryRight;
        public readonly ControllerChargingLeftProperty ControllerChargingLeft;
        public readonly ControllerChargingRightProperty ControllerChargingRight;
        public readonly ControllerDockedLeftProperty ControllerDockedLeft;
        public readonly ControllerDockedRightProperty ControllerDockedRight;
        public readonly LegionControllerInputModeProperty LegionControllerInputMode;
        public readonly LegionMcuFirmwareVersionProperty LegionMcuFirmwareVersion;
        public readonly LegionRgbActiveProfileProperty LegionRgbActiveProfile;
        public readonly LegionGamepadModeSelectProperty LegionGamepadModeSelect;
        public readonly LegionOsReportingDisabledProperty LegionOsReportingDisabled;
        public readonly ControllerConnectedLeftProperty ControllerConnectedLeft;
        public readonly ControllerConnectedRightProperty ControllerConnectedRight;
        public readonly ControllerVidPidProperty ControllerVidPid;
        public readonly ControllerDeviceStatusProperty ControllerDeviceStatus;

        // Device capability properties (for widget UI visibility)
        public readonly DeviceDisplayNameProperty DeviceDisplayName;
        public readonly DeviceSupportsControllerRemapProperty DeviceSupportsControllerRemap;
        public readonly DeviceSupportsRgbLightingProperty DeviceSupportsRgbLighting;
        public readonly DeviceSupportsGyroProperty DeviceSupportsGyro;
        public readonly DeviceHasScrollWheelProperty DeviceHasScrollWheel;
        public readonly DeviceHasDetachableControllersProperty DeviceHasDetachableControllers;
        public readonly DeviceHasTouchpadProperty DeviceHasTouchpad;

        // Reference to PerformanceManager for LibreHardwareMonitor sensor access
        private PerformanceManager performanceManager;

        /// <summary>
        /// Sets the PerformanceManager reference for CPU temperature sensor access.
        /// Called from Program.cs after both managers are initialized.
        /// </summary>
        public void SetPerformanceManager(PerformanceManager manager)
        {
            performanceManager = manager;
            Logger.Info($"PerformanceManager reference set, CPUTemperature sensor available: {manager?.CPUTemperature != null}");
        }

        public LegionManager() : base()
        {
            Logger.Info("Initializing Legion Manager...");
            var constructorTimer = System.Diagnostics.Stopwatch.StartNew();

            // Record startup time for grace period
            startupTime = DateTime.Now;

            // Try to detect Legion Go device
            var detectTimer = System.Diagnostics.Stopwatch.StartNew();
            DetectLegionGo();
            detectTimer.Stop();
            Logger.Info($"[TIMING] LegionManager.DetectLegionGo: {detectTimer.ElapsedMilliseconds}ms");

            // Touchpad: persist user's last choice across helper restarts. Prior
            // behavior used the hardcoded `true` default at LegionManager.cs:35
            // every boot, so users who turned the touchpad OFF would see the
            // widget toggle re-show as ON after restart even though hardware
            // stayed off (touchpad state is firmware-side persistent). The first
            // b0:01 readback further reconciles when something else (LegionSpace,
            // OS gesture toggle) changed the hardware state behind our back.
            if (Settings.LocalSettingsHelper.TryGetValue<bool>("LegionTouchpadEnabled", out bool savedTouchpadEnabled))
            {
                touchpadEnabled = savedTouchpadEnabled;
                Logger.Info($"LegionTouchpadEnabled loaded from settings: {touchpadEnabled}");
            }

            // Initialize properties (pass this as manager)
            LegionGoDetected = new LegionGoDetectedProperty(isLegionGoDetected, this);
            LegionTouchpadEnabled = new LegionTouchpadEnabledProperty(touchpadEnabled, this);
            LegionLightMode = new LegionLightModeProperty(lightMode, this);
            LegionLightColor = new LegionLightColorProperty(lightColor, this);
            LegionLightBrightness = new LegionLightBrightnessProperty(lightBrightness, this);
            LegionLightSpeed = new LegionLightSpeedProperty(lightSpeed, this);
            LegionPerformanceMode = new LegionPerformanceModeProperty(performanceMode, this);
            LegionCustomTDPSlow = new LegionCustomTDPSlowProperty(customTDPSlow, this);
            LegionCustomTDPFast = new LegionCustomTDPFastProperty(customTDPFast, this);
            LegionCustomTDPPeak = new LegionCustomTDPPeakProperty(customTDPPeak, this);
            LegionFanFullSpeed = new LegionFanFullSpeedProperty(fanFullSpeed, this);

            // Restore per-mode fan-curve unlock prefs from LocalSettings. Each mode has its
            // own on/off slot (LegionUnlockFanCurve_<mode>); migrate the legacy single-key
            // value into all four slots on first run so users keep their setting after
            // upgrade. The "active" fanCurveUnlocked tracks whichever mode is currently
            // selected — re-loaded after the WMI mode read settles.
            LoadAllPerModeEcOverride();
            // performanceMode is still the field default at this point (Balanced=2). The
            // real value loads later via ReadCurrentPerformanceMode; we re-sync there.
            fanCurveUnlocked = ecOverrideByMode.TryGetValue(performanceMode, out bool initialUnlock) && initialUnlock;
            LegionUnlockFanCurve = new LegionUnlockFanCurveProperty(fanCurveUnlocked, this);

            // Initialize fan curve - first try LocalSettings, then device
            if (isLegionGoDetected)
            {
                var fanCurveTimer = System.Diagnostics.Stopwatch.StartNew();

                // Load all per-mode curves into the dict; backfill missing modes from the
                // legacy single-curve key (so users who upgrade from a build that only had
                // one fan curve get all four mode slots seeded with their existing curve
                // instead of reverting to defaults).
                bool loadedAnyPerModeCurve = LoadAllPerModeFanCurves();
                bool loadedFromSettings = false;

                if (loadedAnyPerModeCurve || fanCurvesByMode.ContainsKey(performanceMode))
                {
                    if (fanCurvesByMode.TryGetValue(performanceMode, out var modeCurve)
                        && modeCurve != null && modeCurve.Length == 10)
                    {
                        Array.Copy(modeCurve, fanCurve, 10);
                        loadedFromSettings = true;
                        Logger.Info($"Fan curve loaded for mode {performanceMode}: {string.Join(",", fanCurve)}");
                    }
                }

                // If no saved curve, read from device
                if (!loadedFromSettings)
                {
                    Logger.Info("Reading fan curve from device...");
                    const int fanCurveTimeoutMs = 5000;
                    var fanCurveTask = Task.Run(() =>
                    {
                        try
                        {
                            return wmiService?.GetFanCurve();
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn($"GetFanCurve exception: {ex.Message}");
                            return null;
                        }
                    });

                    if (fanCurveTask.Wait(fanCurveTimeoutMs))
                    {
                        var curveResult = fanCurveTask.Result;
                        if (curveResult.HasValue && curveResult.Value.Success && curveResult.Value.FanSpeeds?.Length == 10)
                        {
                            fanCurve = curveResult.Value.FanSpeeds;
                            Logger.Info($"Fan curve loaded from device: {string.Join(",", fanCurve)}");
                        }
                        else
                        {
                            Logger.Warn($"Failed to load fan curve from device, using defaults: {string.Join(",", fanCurve)}");
                        }
                    }
                    else
                    {
                        Logger.Warn($"GetFanCurve timed out after {fanCurveTimeoutMs}ms, using defaults: {string.Join(",", fanCurve)}");
                    }
                }
                fanCurveTimer.Stop();
                Logger.Info($"[TIMING] LegionManager.FanCurve read: {fanCurveTimer.ElapsedMilliseconds}ms");

                // Write the loaded fan curve to hardware now. Without this the saved curve
                // only lived in memory + LocalSettings — it got pushed to the widget via
                // LegionFanCurveData.EnableDeviceWrites() but never reached the controller.
                // Users reported "fan curve not working at all anymore" because the device
                // kept running its firmware default; the user's curve only applied if they
                // nudged the slider manually after the ~5 s startup grace period.
                //
                // Skip if we're reading in a performance mode that isn't Custom (255) —
                // preset modes (Quiet=1, Balanced=2, Performance=3) have their own
                // hardware-managed curves and writing here would be ignored anyway, but
                // logging the skip is useful for triage.
                try
                {
                    var result = wmiService?.SetFanCurve(fanCurve);
                    if (result.HasValue && result.Value.Success)
                    {
                        Logger.Info($"Fan curve applied to hardware on startup: [{string.Join(",", fanCurve)}]%");
                    }
                    else if (result.HasValue)
                    {
                        Logger.Warn($"Fan curve startup apply failed: {result.Value.Message}");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn($"Fan curve startup apply threw: {ex.Message}");
                }
            }
            string fanCurveString = string.Join(",", fanCurve.Select(v => (int)v));
            LegionFanCurveData = new LegionFanCurveDataProperty(fanCurveString, this);
            LegionFanCurvePerMode = new LegionFanCurvePerModeProperty(this);
            LegionUnlockFanCurvePerMode = new LegionUnlockFanCurvePerModeProperty(this);
            LegionCPUCurrentTemp = new LegionCPUCurrentTempProperty(0, this);
            LegionFanSensorTemp = new LegionFanSensorTempProperty(0, this);
            LegionCPUFanRPM = new LegionCPUFanRPMProperty(0, this);
            LegionFanCurveVisible = new LegionFanCurveVisibleProperty(false, this);

            LegionGyroEnabled = new LegionGyroEnabledProperty(gyroEnabled, this);
            LegionVibration = new LegionVibrationProperty(vibrationLevel, this);
            LegionPowerLight = new LegionPowerLightProperty(powerLightEnabled, this);

            // Battery charge limit: read the current EC/WMI state at startup. On some Legion
            // hardware the EC retains it across reboot; on the Legion Go 2 (Z2E) it does NOT —
            // the EC reads back "off" every boot, so the toggle appears to never save (issue
            // #88 bug #5). We therefore reconcile the EC state against the user's persisted
            // preference below and re-apply if the firmware dropped it.
            try
            {
                const int chargeLimitTimeoutMs = 2000;
                var chargeLimitTask = Task.Run(() =>
                {
                    try { return wmiService?.GetBatteryChargeLimit(); }
                    catch (Exception ex)
                    {
                        Logger.Warn($"GetBatteryChargeLimit exception: {ex.Message}");
                        return null;
                    }
                });

                if (chargeLimitTask.Wait(chargeLimitTimeoutMs))
                {
                    var r = chargeLimitTask.Result;
                    if (r.HasValue && r.Value.Success && r.Value.Result.HasValue)
                    {
                        chargeLimitEnabled = r.Value.Result.Value == 1;
                        Logger.Info($"Battery charge limit (80%) read from EC: {(chargeLimitEnabled ? "Enabled" : "Disabled")}");
                    }
                    else
                    {
                        Logger.Warn($"GetBatteryChargeLimit failed, defaulting to Disabled: {r?.Message}");
                    }
                }
                else
                {
                    Logger.Warn($"GetBatteryChargeLimit timed out after {chargeLimitTimeoutMs}ms, defaulting to Disabled");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Charge limit startup read threw: {ex.Message}");
            }

            // Reconcile against the user's persisted preference. If they previously turned the
            // limit ON but the firmware dropped it across reboot (EC now reads off), re-apply it
            // so the setting actually sticks instead of silently reverting (issue #88 bug #5).
            try
            {
                if (Settings.LocalSettingsHelper.TryGetValue("LegionChargeLimit", out bool desiredChargeLimit)
                    && desiredChargeLimit && !chargeLimitEnabled && wmiService != null)
                {
                    Logger.Info("Battery charge limit: persisted preference is ON but EC reads off — re-applying.");
                    var reapply = wmiService.SetBatteryChargeLimit(true);
                    if (reapply.Success)
                    {
                        chargeLimitEnabled = true;
                        Logger.Info("Battery charge limit (80%) re-applied from saved preference on startup.");
                    }
                    else
                    {
                        Logger.Warn($"Battery charge limit re-apply failed: {reapply.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Charge limit preference reconcile threw: {ex.Message}");
            }

            LegionChargeLimit = new LegionChargeLimitProperty(chargeLimitEnabled, this);

            // Initialize controller remapping properties (JSON ButtonMapping format)
            LegionButtonY1 = new LegionButtonY1Property("", this);
            LegionButtonY2 = new LegionButtonY2Property("", this);
            LegionButtonY3 = new LegionButtonY3Property("", this);
            LegionButtonM1 = new LegionButtonM1Property("", this);
            LegionButtonM2 = new LegionButtonM2Property("", this);
            LegionButtonM3 = new LegionButtonM3Property("", this);
            LegionButtonDesktop = new LegionButtonDesktopProperty("", this);
            LegionButtonPage = new LegionButtonPageProperty("", this);
            LegionNintendoLayout = new LegionNintendoLayoutProperty(nintendoLayoutEnabled, this);
            LegionVibrationMode = new LegionVibrationModeProperty(vibrationMode, this);
            LegionControllerProfileEnabled = new LegionControllerProfileEnabledProperty(false, this);

            // Initialize gyro settings properties
            LegionGyroTarget = new LegionGyroTargetProperty(gyroTarget, this);
            LegionGyroSensitivityX = new LegionGyroSensitivityXProperty(gyroSensitivityX, this);
            LegionGyroSensitivityY = new LegionGyroSensitivityYProperty(gyroSensitivityY, this);
            LegionGyroInvertX = new LegionGyroInvertXProperty(gyroInvertX, this);
            LegionGyroInvertY = new LegionGyroInvertYProperty(gyroInvertY, this);
            LegionGyroMappingType = new LegionGyroMappingTypeProperty(gyroMappingType, this);
            LegionGyroActivationMode = new LegionGyroActivationModeProperty(gyroActivationMode, this);
            LegionGyroActivationButton = new LegionGyroActivationButtonProperty(gyroActivationButton, this);

            // Initialize advanced gyro properties
            LegionGyroDeadzone = new LegionGyroDeadzoneProperty(gyroDeadzone, this);

            // Initialize stick deadzone properties
            LegionLeftStickDeadzone = new LegionLeftStickDeadzoneProperty(leftStickDeadzone, this);
            LegionRightStickDeadzone = new LegionRightStickDeadzoneProperty(rightStickDeadzone, this);

            // Initialize trigger travel properties
            LegionLeftTriggerStart = new LegionLeftTriggerStartProperty(0, this);
            LegionLeftTriggerEnd = new LegionLeftTriggerEndProperty(0, this);
            LegionRightTriggerStart = new LegionRightTriggerStartProperty(0, this);
            LegionRightTriggerEnd = new LegionRightTriggerEndProperty(0, this);
            LegionHairTriggers = new LegionHairTriggersProperty(false, this);

            // Initialize touchpad vibration property (GLOBAL setting)
            LegionTouchpadVibration = new LegionTouchpadVibrationProperty(touchpadVibrationLevel, this);

            // Initialize joystick as mouse properties (per-game profile)
            LegionJoystickAsMouseMode = new LegionJoystickAsMouseModeProperty(joystickAsMouseMode, this);
            LegionJoystickMouseSens = new LegionJoystickMouseSensProperty(joystickMouseSens, this);

            // Initialize gamepad button mapping property (per-game profile)
            LegionGamepadMapping = new LegionGamepadMappingProperty("", this);

            // Initialize desktop controls property (per-game profile state tracking)
            LegionDesktopControls = new LegionDesktopControlsProperty(false, this);

            // Initialize controller battery properties
            ControllerBatteryLeft = new ControllerBatteryLeftProperty(-1, this);
            ControllerBatteryRight = new ControllerBatteryRightProperty(-1, this);
            ControllerChargingLeft = new ControllerChargingLeftProperty(false, this);
            ControllerChargingRight = new ControllerChargingRightProperty(false, this);
            ControllerDockedLeft = new ControllerDockedLeftProperty(false, this);
            ControllerDockedRight = new ControllerDockedRightProperty(false, this);
            LegionControllerInputMode = new LegionControllerInputModeProperty(0, this);
            LegionMcuFirmwareVersion = new LegionMcuFirmwareVersionProperty("", this);
            LegionRgbActiveProfile = new LegionRgbActiveProfileProperty(0, this);
            LegionGamepadModeSelect = new LegionGamepadModeSelectProperty(0, this);
            LegionOsReportingDisabled = new LegionOsReportingDisabledProperty(false, this);
            ControllerConnectedLeft = new ControllerConnectedLeftProperty(false, this);
            ControllerConnectedRight = new ControllerConnectedRightProperty(false, this);
            ControllerVidPid = new ControllerVidPidProperty("", this);
            ControllerDeviceStatus = new ControllerDeviceStatusProperty("", this);

            // Initialize device capability properties (for widget UI visibility)
            // Get display name and capabilities from the detected device config
            var deviceConfig = DeviceRegistry.GetByType(deviceInfo?.DeviceType ?? Shared.Enums.DeviceType.Generic);
            string displayName = deviceConfig?.DisplayName ?? "Legion Go Controller";
            bool supportsControllerRemap = deviceInfo?.SupportsControllerRemap ?? true;
            bool supportsRgbLighting = deviceInfo?.SupportsRgbLighting ?? true;
            bool supportsGyro = deviceInfo?.SupportsGyro ?? true;
            bool hasScrollWheel = deviceInfo?.HasScrollWheel ?? true;
            bool hasDetachableControllers = deviceInfo?.HasDetachableControllers ?? true;
            bool hasTouchpad = deviceInfo?.HasTouchpad ?? true;

            DeviceDisplayName = new DeviceDisplayNameProperty(displayName, this);
            DeviceSupportsControllerRemap = new DeviceSupportsControllerRemapProperty(supportsControllerRemap, this);
            DeviceSupportsRgbLighting = new DeviceSupportsRgbLightingProperty(supportsRgbLighting, this);
            DeviceSupportsGyro = new DeviceSupportsGyroProperty(supportsGyro, this);
            DeviceHasScrollWheel = new DeviceHasScrollWheelProperty(hasScrollWheel, this);
            DeviceHasDetachableControllers = new DeviceHasDetachableControllersProperty(hasDetachableControllers, this);
            DeviceHasTouchpad = new DeviceHasTouchpadProperty(hasTouchpad, this);

            Logger.Info($"Device capabilities - Name: {displayName}, ControllerRemap: {supportsControllerRemap}, RGB: {supportsRgbLighting}, Gyro: {supportsGyro}, ScrollWheel: {hasScrollWheel}, DetachableControllers: {hasDetachableControllers}, Touchpad: {hasTouchpad}");

            // NOTE: Battery monitoring is started from Program.cs AFTER widget connection
            // is established. Starting it here blocks the AppService connection.

            if (isLegionGoDetected)
            {
                // Read current performance mode and TDP values IN PARALLEL (with timeouts to prevent hangs)
                const int wmiReadTimeoutMs = 5000;
                var parallelTimer = System.Diagnostics.Stopwatch.StartNew();

                Logger.Info("Reading performance mode and TDP values in parallel...");
                var perfModeTask = Task.Run(() =>
                {
                    try { ReadCurrentPerformanceMode(); }
                    catch (Exception ex) { Logger.Warn($"ReadCurrentPerformanceMode exception: {ex.Message}"); }
                });
                var tdpTask = Task.Run(() =>
                {
                    try { ReadCurrentTDPValues(); }
                    catch (Exception ex) { Logger.Warn($"ReadCurrentTDPValues exception: {ex.Message}"); }
                });

                // Wait for both tasks to complete (or timeout)
                if (!Task.WaitAll(new[] { perfModeTask, tdpTask }, wmiReadTimeoutMs))
                {
                    Logger.Warn($"WMI reads timed out after {wmiReadTimeoutMs}ms");
                }
                parallelTimer.Stop();
                Logger.Info($"[TIMING] LegionManager.ReadPerformanceMode+TDP (parallel): {parallelTimer.ElapsedMilliseconds}ms");

                // Update properties with the values read from device
                // Use silent update to avoid triggering WMI calls back
                LegionPerformanceMode.SetValueSilent(performanceMode);
                LegionCustomTDPSlow.SetValueSilent(customTDPSlow);
                LegionCustomTDPFast.SetValueSilent(customTDPFast);
                LegionCustomTDPPeak.SetValueSilent(customTDPPeak);
                LegionFanFullSpeed.SetValueSilent(fanFullSpeed);

                // Reload curve + unlock state from the per-mode slots now that we know
                // the actual mode WMI returned. The earlier load at line ~312 used the
                // performanceMode field default (Balanced=2), so if the user was in a
                // different mode at last shutdown the wrong slots were active until this
                // moment — which surfaced as "fan curve reset after update".
                try
                {
                    if (fanCurvesByMode.TryGetValue(performanceMode, out var modeCurve)
                        && modeCurve != null && modeCurve.Length == 10)
                    {
                        bool same = true;
                        for (int i = 0; i < 10; i++) { if (fanCurve[i] != modeCurve[i]) { same = false; break; } }
                        if (!same)
                        {
                            Array.Copy(modeCurve, fanCurve, 10);
                            Logger.Info($"Fan curve re-loaded for actual mode {performanceMode} after WMI read: [{string.Join(",", fanCurve)}]%");
                        }
                        // Align legacy LegionFanCurveData with the actual mode's curve
                        // so EnableDeviceWrites (5s grace timer) doesn't push the stale
                        // default-mode-2 curve. AlignSilent updates Value + _lastSentValue.
                        try
                        {
                            string actualCurveStr = string.Join(",", fanCurve.Select(v => (int)v));
                            LegionFanCurveData?.AlignSilent(actualCurveStr);
                        }
                        catch (Exception ex) { Logger.Debug($"Failed to align legacy LegionFanCurveData after WMI read: {ex.Message}"); }
                    }

                    bool actualUnlock = ecOverrideByMode.TryGetValue(performanceMode, out bool v) && v;
                    if (actualUnlock != fanCurveUnlocked)
                    {
                        fanCurveUnlocked = actualUnlock;
                        Logger.Info($"EC override state re-loaded for actual mode {performanceMode}: {(actualUnlock ? "ON" : "off")}");
                        try { Settings.LocalSettingsHelper.SetValue("LegionUnlockFanCurve", actualUnlock); } catch { }
                        try { LegionUnlockFanCurve?.SetValueSilent(actualUnlock); } catch { }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn($"Failed to reload fan curve / unlock state for actual mode {performanceMode}: {ex.Message}");
                }
            }

            // Enable fan curve device writes after startup grace period (only for Legion Go)
            // This prevents the widget's sync from overwriting the device's fan curve on startup
            if (isLegionGoDetected)
            {
                var fanCurveEnableTimer = new System.Timers.Timer(STARTUP_GRACE_PERIOD_MS);
                fanCurveEnableTimer.Elapsed += (s, e) =>
                {
                    fanCurveEnableTimer.Stop();
                    fanCurveEnableTimer.Dispose();
                    LegionFanCurveData?.EnableDeviceWrites();
                };
                fanCurveEnableTimer.AutoReset = false;
                fanCurveEnableTimer.Start();
            }

            // If the user previously toggled fan-curve unlock on and the helper is
            // restarting (e.g. after upgrade), bootstrap the EC override loop now —
            // SetFanCurveUnlocked won't fire from a property change here because the
            // initial property value already matches the persisted state.
            if (fanCurveUnlocked && isLegionGoDetected)
            {
                Logger.Info("LegionUnlockFanCurve persisted as on — bootstrapping EC override path post-construction.");
                EnsureEcOverrideRunning();
            }

            constructorTimer.Stop();
            Logger.Info($"[TIMING] LegionManager constructor total: {constructorTimer.ElapsedMilliseconds}ms");
            Logger.Info($"Legion Manager initialized. Legion Go detected: {isLegionGoDetected}");
        }

        private void DetectLegionGo()
        {
            try
            {
                var stepTimer = System.Diagnostics.Stopwatch.StartNew();

                // Step 1: Use DeviceDetector to get device info from WMI (Manufacturer, Model)
                deviceInfo = DeviceDetector.DetectDevice();
                Logger.Info($"[TIMING] DeviceDetector.DetectDevice: {stepTimer.ElapsedMilliseconds}ms");
                stepTimer.Restart();

                // Check if device is a known Legion model
                if (deviceInfo.IsLegionDevice)
                {
                    isLegionGoDetected = true;
                    Logger.Info($"Legion device detected via DeviceDetector: {deviceInfo.DeviceType} ({deviceInfo.Model})");
                }

                // If debug mode is active and device is NOT Legion, skip hardware fallback checks
                // This allows testing non-Legion devices on actual Legion hardware
                bool skipHardwareFallbacks = DeviceDetector.IsDebugModeActive && !deviceInfo.IsLegionDevice;
                if (skipHardwareFallbacks)
                {
                    Logger.Warn("DEBUG MODE: Skipping Legion hardware fallback checks (non-Legion device override active)");
                    deviceInfo.SupportsWmiTdp = false;
                    deviceInfo.SupportsControllerRemap = false;
                }
                else
                {
                    // Step 2: Validate WMI capability (even if model matches, verify GAMEZONE WMI works)
                    wmiService = new LenovoWMIService();
                    Logger.Info($"[TIMING] LenovoWMIService created: {stepTimer.ElapsedMilliseconds}ms");
                    stepTimer.Restart();

                    // Try GetSmartFanMode - if it works, this device supports Legion WMI TDP control
                    var fanModeResult = wmiService.GetSmartFanMode();
                    Logger.Info($"[TIMING] GetSmartFanMode (WMI check): {stepTimer.ElapsedMilliseconds}ms");
                    stepTimer.Restart();

                    if (fanModeResult.Success)
                    {
                        isLegionGoDetected = true;
                        deviceInfo.SupportsWmiTdp = true;
                        Logger.Info($"Legion WMI TDP control confirmed (SmartFanMode={fanModeResult.Result})");
                    }
                    else if (deviceInfo.IsLegionDevice)
                    {
                        // Model matches but WMI failed - log warning but keep detected
                        Logger.Warn($"Legion device model detected but WMI TDP control unavailable");
                        deviceInfo.SupportsWmiTdp = false;
                    }

                    // Step 3: Try to connect to controller service (for button remapping, RGB, etc.)
                    controllerService = new LegionControllerService();
                    Logger.Info($"[TIMING] LegionControllerService created: {stepTimer.ElapsedMilliseconds}ms");
                    stepTimer.Restart();

                    var connectResult = controllerService.Connect();
                    Logger.Info($"[TIMING] Controller Connect: {stepTimer.ElapsedMilliseconds}ms");

                    if (connectResult.Success)
                    {
                        isControllerConnected = true;
                        isLegionGoDetected = true;
                        // Don't override SupportsControllerRemap here - respect the DeviceConfig value
                        // Legion Go S has different HID structure that doesn't support remapping even if controller connects
                        if (deviceInfo.SupportsControllerRemap)
                        {
                            Logger.Info($"Legion Go controller connected: {connectResult.Message}");
                        }
                        else
                        {
                            Logger.Info($"Legion Go controller connected but remapping not supported for this device variant: {connectResult.Message}");
                        }
                    }
                    else
                    {
                        // Only disable if it was expected to be supported
                        if (deviceInfo.SupportsControllerRemap)
                        {
                            deviceInfo.SupportsControllerRemap = false;
                            Logger.Info($"Legion Go controller not connected: {connectResult.Message}");
                        }
                        else
                        {
                            Logger.Info($"Legion Go controller not connected (not supported for this device): {connectResult.Message}");
                        }
                    }

                    // Step 4: For Legion Go S, try to connect to the RGB controller (uses different HID protocol)
                    if (deviceInfo.DeviceType == Shared.Enums.DeviceType.LegionGoS && deviceInfo.SupportsRgbLighting)
                    {
                        try
                        {
                            Logger.Info("Attempting to connect Legion Go S RGB controller...");
                            goSController = new LegionGoSController();
                            if (goSController.Connect())
                            {
                                isGoSControllerConnected = true;
                                isLegionGoDetected = true;
                                Logger.Info("Legion Go S RGB controller connected successfully");
                                // Route the Controller Sleep card to the Go S config protocol
                                // (SET_GAMEPAD_CFG auto-sleep) - the button monitor's Go 2
                                // frames don't apply on this device.
                                Labs.LegionButtonMonitor.GoSAutoSleepWriter =
                                    (min) => goSController?.SetAutoSleepTime(min) ?? false;
                            }
                            else
                            {
                                // In debug mode, don't disable RGB capability - the UI should still show
                                // RGB controls even if we can't connect to actual hardware
                                if (DeviceDetector.IsDebugModeActive)
                                {
                                    Logger.Info("Legion Go S RGB controller not found (debug mode - keeping RGB UI enabled)");
                                    isLegionGoDetected = true;  // Mark as detected so UI shows
                                }
                                else
                                {
                                    Logger.Warn("Legion Go S RGB controller not found - RGB lighting disabled");
                                    deviceInfo.SupportsRgbLighting = false;
                                }
                            }
                        }
                        catch (Exception goSEx)
                        {
                            Logger.Error($"Error connecting Legion Go S RGB controller: {goSEx.Message}");
                            if (!DeviceDetector.IsDebugModeActive)
                            {
                                deviceInfo.SupportsRgbLighting = false;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error detecting Legion Go: {ex.Message}");
                isLegionGoDetected = false;
                if (deviceInfo == null)
                {
                    deviceInfo = new DeviceInfo();
                }
            }
        }

        private void ReadCurrentPerformanceMode()
        {
            try
            {
                if (wmiService != null)
                {
                    var result = wmiService.GetSmartFanMode();
                    if (result.Success && result.Result.HasValue)
                    {
                        performanceMode = (int)result.Result.Value;
                        Logger.Info($"Current performance mode: {result.Result.Value}");
                    }

                    var fanFullResult = wmiService.GetFanFullSpeed();
                    if (fanFullResult.Success && fanFullResult.Result.HasValue)
                    {
                        fanFullSpeed = fanFullResult.Result.Value == 1;
                        Logger.Info($"Fan full speed: {fanFullSpeed}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error reading performance mode: {ex.Message}");
            }
        }

        private void ReadCurrentTDPValues()
        {
            try
            {
                if (wmiService != null)
                {
                    var slowResult = wmiService.GetCPUShortTermPowerLimit();
                    if (slowResult.Success && slowResult.Result.HasValue)
                    {
                        customTDPSlow = slowResult.Result.Value;
                        Logger.Info($"Current Slow TDP (SPL): {customTDPSlow}W");
                    }

                    var fastResult = wmiService.GetCPULongTermPowerLimit();
                    if (fastResult.Success && fastResult.Result.HasValue)
                    {
                        customTDPFast = fastResult.Result.Value;
                        Logger.Info($"Current Fast TDP (SPPL): {customTDPFast}W");
                    }

                    var peakResult = wmiService.GetCPUPeakPowerLimit();
                    if (peakResult.Success && peakResult.Result.HasValue)
                    {
                        customTDPPeak = peakResult.Result.Value;
                        Logger.Info($"Current Peak TDP (FPPT): {customTDPPeak}W");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error reading TDP values: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets the current TDP values.
        /// When in Custom mode (255), returns our cached values since WMI returns preset values.
        /// Returns (slow/SPL, fast/SPPL, peak/FPPT) or null values if not available.
        /// </summary>
        public (int? slow, int? fast, int? peak) GetCurrentTDPValues()
        {
            // In Custom mode, return our cached values since WMI GetFeatureValue
            // returns the preset TDP limits, not the custom values we've applied
            if (performanceMode == 255)
            {
                return (customTDPSlow, customTDPFast, customTDPPeak);
            }

            int? slow = null, fast = null, peak = null;

            try
            {
                if (wmiService != null)
                {
                    var slowResult = wmiService.GetCPUShortTermPowerLimit();
                    if (slowResult.Success && slowResult.Result.HasValue)
                    {
                        slow = slowResult.Result.Value;
                    }

                    var fastResult = wmiService.GetCPULongTermPowerLimit();
                    if (fastResult.Success && fastResult.Result.HasValue)
                    {
                        fast = fastResult.Result.Value;
                    }

                    var peakResult = wmiService.GetCPUPeakPowerLimit();
                    if (peakResult.Success && peakResult.Result.HasValue)
                    {
                        peak = peakResult.Result.Value;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error getting TDP values: {ex.Message}");
            }

            return (slow, fast, peak);
        }

        public bool SetTouchpadEnabled(bool enabled, out string failureReason)
        {
            failureReason = null;
            if (!isControllerConnected || controllerService == null)
            {
                failureReason = "controller not connected";
                Logger.Warn("Cannot set touchpad: controller not connected");
                return false;
            }

            try
            {
                var result = controllerService.SetTouchpadEnabled(enabled);
                if (result.Success)
                {
                    touchpadEnabled = enabled;
                    try { Settings.LocalSettingsHelper.SetValue("LegionTouchpadEnabled", enabled); }
                    catch (Exception persistEx) { Logger.Debug($"Failed to persist LegionTouchpadEnabled: {persistEx.Message}"); }
                    Logger.Info($"Touchpad {(enabled ? "enabled" : "disabled")}");
                    return true;
                }

                failureReason = result.Message;
                Logger.Error($"Failed to set touchpad: {result.Message}");
                return false;
            }
            catch (Exception ex)
            {
                failureReason = ex.Message;
                Logger.Error($"Error setting touchpad: {ex.Message}");
                return false;
            }
        }

        public bool SetLightMode(int mode, out string failureReason)
        {
            failureReason = null;
            // Check if we have a valid controller for RGB
            bool hasGoSController = isGoSControllerConnected && goSController != null;
            bool hasStandardController = isControllerConnected && controllerService != null;

            if (!hasGoSController && !hasStandardController)
            {
                failureReason = "no RGB controller connected";
                Logger.Warn("Cannot set light mode: no RGB controller connected");
                return false;
            }

            bool success = false;
            try
            {
                // Parse current color
                ParseHexColor(lightColor, out byte r, out byte g, out byte b);

                // Use Go S controller if available (Legion Go S uses different HID protocol)
                if (hasGoSController)
                {
                    switch (mode)
                    {
                        case 0: // Disabled/Off
                            success = goSController.DisableRgb();
                            break;
                        case 1: // Solid
                            success = goSController.SetSolidColor(r, g, b, lightBrightness);
                            break;
                        case 2: // Pulse
                            success = goSController.SetPulseMode(r, g, b, lightBrightness, lightSpeed);
                            break;
                        case 3: // Dynamic/Rainbow
                            success = goSController.SetRainbowMode(lightBrightness, lightSpeed);
                            break;
                        case 4: // Spiral
                            success = goSController.SetSpiralMode(lightBrightness, lightSpeed);
                            break;
                        default:
                            success = goSController.SetSolidColor(r, g, b, lightBrightness);
                            break;
                    }

                    if (success)
                    {
                        lightMode = mode;
                        Logger.Info($"Light mode set to {mode} (Go S controller)");
                    }
                    else
                    {
                        failureReason = "Go S controller rejected the light mode write";
                        Logger.Error($"Failed to set light mode on Go S controller");
                    }
                }
                else
                {
                    // Standard Legion Go controller. Widget sends mode=0 to mean "Off" but
                    // RgbMode enum starts at Solid=1, so casting (RgbMode)0 produces an
                    // invalid byte that the firmware silently rejects (and leaves the light
                    // in whatever state it was previously). Route mode=0 through SetRgbEnabled
                    // for both controllers, mirroring the Go S branch above.
                    if (mode == 0)
                    {
                        var leftOff = controllerService.SetRgbEnabled(Controller.Left, false);
                        var rightOff = controllerService.SetRgbEnabled(Controller.Right, false);
                        success = leftOff.Success && rightOff.Success;
                        if (success)
                        {
                            lightMode = mode;
                            Logger.Info("Light mode set to Off (SetRgbEnabled false on both controllers)");
                        }
                        else
                        {
                            failureReason = $"L={leftOff.Message}, R={rightOff.Message}";
                            Logger.Error($"Failed to disable stick lights: L={leftOff.Message}, R={rightOff.Message}");
                        }
                    }
                    else
                    {
                        // Re-enable the light first in case it was previously off — writing
                        // a profile alone doesn't re-enable a disabled light per the firmware.
                        controllerService.SetRgbEnabled(Controller.Left, true);
                        controllerService.SetRgbEnabled(Controller.Right, true);

                        RgbMode rgbMode = (RgbMode)mode;
                        var result = controllerService.SetStickLightMode(rgbMode, r, g, b, lightBrightness / 100f, lightSpeed / 100f);
                        success = result.Success;
                        if (success)
                        {
                            lightMode = mode;

                            // Make the slot we just wrote (3) the firmware's ACTIVE profile.
                            // SetRgbProfile applies the values live but does not change which
                            // profile slot the firmware restores after sleep/power-cycle — if
                            // Legion Space (or the factory default) left a different slot
                            // active, every resume "reverted" the pads to that slot's stored
                            // settings (typically the pale-blue default). Field report:
                            // "gamepads were set to profile 3" while colors kept resetting.
                            // The Go S path already loads the profile as part of its flow.
                            controllerService.LoadRgbProfile(Controller.Left);
                            controllerService.LoadRgbProfile(Controller.Right);

                            Logger.Info($"Light mode set to {rgbMode} (profile slot activated)");
                        }
                        else
                        {
                            failureReason = result.Message;
                            Logger.Error($"Failed to set light mode: {result.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                success = false;
                failureReason = ex.Message;
                Logger.Error($"Error setting light mode: {ex.Message}");
            }
            // Re-read hardware state so the Info card reflects the new mode promptly.
            RequestDeviceStatusRefresh();
            return success;
        }

        public void RestoreLightSettings()
        {
            // Don't push light to hardware until we actually know the light state from a real source
            // (b0:01 hardware readback or a widget/profile sync). On a cold helper start (Kill/Update)
            // the light fields still hold uninitialized defaults (mode=Solid, #FFFFFF, 100%); applying
            // those blasted a full-brightness white flash before the widget synced the user's real
            // values ~9s later (#81 white stick flash). Reactive-disabled init and gyro LED revert both
            // route through here, so this gate covers every restore caller.
            if (!_lightStateKnown)
            {
                Logger.Info("RestoreLightSettings skipped: light state not yet known (avoiding default white flash).");
                return;
            }

            // Sync lightColor from the property value (widget is source of truth).
            // The cached lightColor field may still be the default #FFFFFF if SetLightColor
            // was never called (e.g., _hasUserModified check skipped it, or HasExplicitLighting was false).
            string propertyColor = LegionLightColor?.Value;
            if (!string.IsNullOrEmpty(propertyColor) && propertyColor != lightColor)
            {
                Logger.Info($"RestoreLightSettings: updating cached color from {lightColor} to {propertyColor} (from property)");
                lightColor = propertyColor;
            }

            // If the widget hasn't synced yet but the b0:01 readback knows the true hardware
            // brightness/color, prefer those over the stale defaults (mode=Solid/100/#FFFFFF) so a
            // restore can't flash full-brightness white before sync (#81 brightness flash).
            if (_hasHwLightState)
            {
                if (lightBrightness == 100 && _hwLightBrightness != 100) lightBrightness = _hwLightBrightness;
                if (lightColor == "#FFFFFF")
                {
                    lightColor = $"#{_hwLightColorR:X2}{_hwLightColorG:X2}{_hwLightColorB:X2}";
                }
            }

            // Don't sync lightMode from the b0:01 readback here: during a reactive effect the
            // helper writes Solid+color frames to hardware, so b0:01 reports mode=Solid even when
            // the user's saved static mode is Off. Overriding lightMode with hw mode at release
            // time would re-enable the light to Solid instead of returning to Off. The widget's
            // sync of LegionLightMode is the authoritative source for the user's static mode.

            Logger.Info($"Restoring light settings: mode={lightMode}, color={lightColor}, brightness={lightBrightness}");
            SetLightMode(lightMode, out _);
        }

        /// <summary>
        /// Set both controllers to a SOLID color immediately, used by the reactive-lighting
        /// effect loop (flash on press, etc). Routes through the same LegionControllerService /
        /// Go S path as the static lighting so there's a single RGB writer. Does NOT touch the
        /// persisted lightMode/lightColor — call <see cref="ReleaseReactiveLighting"/> to return
        /// to the user's static setting. Keep callers throttled (~60Hz); the service serializes
        /// HID writes internally.
        /// </summary>
        public void SetReactiveStickColor(byte r, byte g, byte b, int brightnessPercent)
        {
            bool hasGoSController = isGoSControllerConnected && goSController != null;
            bool hasStandardController = isControllerConnected && controllerService != null;
            if (!hasGoSController && !hasStandardController) return;

            int bright = Math.Max(0, Math.Min(100, brightnessPercent));
            // Preserve the user's configured speed in the profile. For a Solid reactive frame
            // speed has no visible effect (no animation), but it's still written to the profile
            // and read back by b0:01 — hardcoding 0.5 made the Info card report speed 50%.
            float speed = Math.Max(0, Math.Min(100, lightSpeed)) / 100f;
            try
            {
                if (hasGoSController)
                {
                    // SetSolidColor re-enables the Go S RGB as part of the write.
                    goSController.SetSolidColor(r, g, b, bright);
                }
                else
                {
                    // The static light may be Off (RGB disabled at firmware level); writing a
                    // profile alone won't re-enable it. Enable first so reactive flashes show
                    // even when the user's static mode is Off. ReleaseReactiveLighting ->
                    // RestoreLightSettings will disable it again when the effect ends if Off.
                    if (lightMode == 0)
                    {
                        controllerService.SetRgbEnabled(Controller.Left, true);
                        controllerService.SetRgbEnabled(Controller.Right, true);
                    }
                    controllerService.SetStickLightMode(RgbMode.Solid, r, g, b, bright / 100f, speed);
                }
            }
            catch (Exception ex) { Logger.Debug($"SetReactiveStickColor failed: {ex.Message}"); }
        }

        /// <summary>Return the controllers to the user's persisted static lighting after a
        /// reactive effect ends (or when reactive lighting is disabled).</summary>
        public void ReleaseReactiveLighting()
        {
            try { RestoreLightSettings(); }
            catch (Exception ex) { Logger.Debug($"ReleaseReactiveLighting failed: {ex.Message}"); }
        }

        /// <summary>Brightness (0-100) for the reactive loop. Prefers the true hardware value from
        /// the b0:01 readback over the helper's stale default (which is 100 after a restart and
        /// would make reactive effects peak at full brightness before the widget reconnects).</summary>
        public int CurrentLightBrightness => _hasHwLightState ? _hwLightBrightness : lightBrightness;

        /// <summary>Current static color (parsed RGB), so the reactive fade can blend toward it
        /// instead of toward black — avoids a visible snap back at the end of a flash. Prefers the
        /// hardware readback color; when static mode is Off, returns black so flashes fade fully out.</summary>
        public (byte r, byte g, byte b) CurrentLightColorRgb
        {
            get
            {
                if (lightMode == 0) return (0, 0, 0);
                if (_hasHwLightState) return (_hwLightColorR, _hwLightColorG, _hwLightColorB);
                try { ParseHexColor(lightColor, out byte r, out byte g, out byte b); return (r, g, b); }
                catch { return (0, 0, 0); }
            }
        }


        public bool SetLightColor(string hexColor, out string failureReason)
        {
            failureReason = null;
            // An external color set is a real source of truth for the light state.
            _lightStateKnown = true;

            bool hasGoSController = isGoSControllerConnected && goSController != null;
            bool hasStandardController = isControllerConnected && controllerService != null;

            if (!hasGoSController && !hasStandardController)
            {
                failureReason = "no RGB controller connected";
                Logger.Warn("Cannot set light color: no RGB controller connected");
                return false;
            }

            bool success = false;
            try
            {
                ParseHexColor(hexColor, out byte r, out byte g, out byte b);

                if (hasGoSController)
                {
                    // Map light mode to Go S RGB mode
                    var goSMode = lightMode switch
                    {
                        0 => LegionGoSController.RgbMode.Solid,
                        1 => LegionGoSController.RgbMode.Solid,
                        2 => LegionGoSController.RgbMode.Pulse,
                        3 => LegionGoSController.RgbMode.Dynamic,
                        4 => LegionGoSController.RgbMode.Spiral,
                        _ => LegionGoSController.RgbMode.Solid
                    };

                    success = goSController.SetRgbMode(goSMode, r, g, b, lightBrightness, lightSpeed);
                    if (success)
                    {
                        lightColor = hexColor;
                        Logger.Info($"Light color set to {hexColor} (Go S controller)");
                    }
                    else
                    {
                        failureReason = "Go S controller rejected the light color write";
                        Logger.Error($"Failed to set light color on Go S controller");
                    }
                }
                else
                {
                    // When the user has the light mode set to Off (lightMode=0), don't
                    // push a profile — that would write an invalid (RgbMode)0 byte and
                    // leak through to the firmware as garbage. Just remember the color
                    // so the next mode change (back to Solid/Pulse/etc.) can apply it.
                    if (lightMode == 0)
                    {
                        lightColor = hexColor;
                        success = true;
                        Logger.Info($"Light color cached as {hexColor} (mode is Off, not pushing profile)");
                    }
                    else
                    {
                        RgbMode rgbMode = (RgbMode)lightMode;
                        var result = controllerService.SetStickLightMode(rgbMode, r, g, b, lightBrightness / 100f, lightSpeed / 100f);
                        success = result.Success;
                        if (success)
                        {
                            lightColor = hexColor;
                            Logger.Info($"Light color set to {hexColor}");
                        }
                        else
                        {
                            failureReason = result.Message;
                            Logger.Error($"Failed to set light color: {result.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                success = false;
                failureReason = ex.Message;
                Logger.Error($"Error setting light color: {ex.Message}");
            }
            // Re-read hardware state so the Info card reflects the new color/mode promptly.
            RequestDeviceStatusRefresh();
            return success;
        }

        private void ParseHexColor(string hexColor, out byte r, out byte g, out byte b)
        {
            // Default to white
            r = 255; g = 255; b = 255;

            if (string.IsNullOrEmpty(hexColor)) return;

            // Remove # if present
            string hex = hexColor.TrimStart('#');

            if (hex.Length == 6)
            {
                r = Convert.ToByte(hex.Substring(0, 2), 16);
                g = Convert.ToByte(hex.Substring(2, 2), 16);
                b = Convert.ToByte(hex.Substring(4, 2), 16);
            }
        }

        public bool SetLightBrightness(int brightness, out string failureReason)
        {
            failureReason = null;
            // An external brightness set is a real source of truth for the light state.
            _lightStateKnown = true;

            bool hasGoSController = isGoSControllerConnected && goSController != null;
            bool hasStandardController = isControllerConnected && controllerService != null;

            if (!hasGoSController && !hasStandardController)
            {
                failureReason = "no RGB controller connected";
                Logger.Warn("Cannot set light brightness: no RGB controller connected");
                return false;
            }

            bool success = false;
            try
            {
                brightness = Math.Max(0, Math.Min(100, brightness));
                ParseHexColor(lightColor, out byte r, out byte g, out byte b);

                if (hasGoSController)
                {
                    var goSMode = lightMode switch
                    {
                        0 => LegionGoSController.RgbMode.Solid,
                        1 => LegionGoSController.RgbMode.Solid,
                        2 => LegionGoSController.RgbMode.Pulse,
                        3 => LegionGoSController.RgbMode.Dynamic,
                        4 => LegionGoSController.RgbMode.Spiral,
                        _ => LegionGoSController.RgbMode.Solid
                    };

                    success = goSController.SetRgbMode(goSMode, r, g, b, brightness, lightSpeed);
                    if (success)
                    {
                        lightBrightness = brightness;
                        Logger.Info($"Light brightness set to {brightness}% (Go S controller)");
                    }
                    else
                    {
                        failureReason = "Go S controller rejected the light brightness write";
                        Logger.Error($"Failed to set light brightness on Go S controller");
                    }
                }
                else
                {
                    // Skip the wire push when the light is in Off mode — writing
                    // (RgbMode)0 is invalid and the firmware will reject or
                    // misinterpret it. Just cache for the next mode change.
                    if (lightMode == 0)
                    {
                        lightBrightness = brightness;
                        success = true;
                        Logger.Info($"Light brightness cached as {brightness}% (mode is Off, not pushing profile)");
                    }
                    else
                    {
                        RgbMode rgbMode = (RgbMode)lightMode;
                        var result = controllerService.SetStickLightMode(rgbMode, r, g, b, brightness / 100f, lightSpeed / 100f);
                        success = result.Success;
                        if (success)
                        {
                            lightBrightness = brightness;
                            Logger.Info($"Light brightness set to {brightness}%");
                        }
                        else
                        {
                            failureReason = result.Message;
                            Logger.Error($"Failed to set light brightness: {result.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                success = false;
                failureReason = ex.Message;
                Logger.Error($"Error setting light brightness: {ex.Message}");
            }
            return success;
        }

        public bool SetLightSpeed(int speed, out string failureReason)
        {
            failureReason = null;
            // An external speed set is a real source of truth for the light state.
            _lightStateKnown = true;

            bool hasGoSController = isGoSControllerConnected && goSController != null;
            bool hasStandardController = isControllerConnected && controllerService != null;

            if (!hasGoSController && !hasStandardController)
            {
                failureReason = "no RGB controller connected";
                Logger.Warn("Cannot set light speed: no RGB controller connected");
                return false;
            }

            bool success = false;
            try
            {
                speed = Math.Max(0, Math.Min(100, speed));
                ParseHexColor(lightColor, out byte r, out byte g, out byte b);

                if (hasGoSController)
                {
                    var goSMode = lightMode switch
                    {
                        0 => LegionGoSController.RgbMode.Solid,
                        1 => LegionGoSController.RgbMode.Solid,
                        2 => LegionGoSController.RgbMode.Pulse,
                        3 => LegionGoSController.RgbMode.Dynamic,
                        4 => LegionGoSController.RgbMode.Spiral,
                        _ => LegionGoSController.RgbMode.Solid
                    };

                    success = goSController.SetRgbMode(goSMode, r, g, b, lightBrightness, speed);
                    if (success)
                    {
                        lightSpeed = speed;
                        Logger.Info($"Light speed set to {speed}% (Go S controller)");
                    }
                    else
                    {
                        failureReason = "Go S controller rejected the light speed write";
                        Logger.Error($"Failed to set light speed on Go S controller");
                    }
                }
                else
                {
                    if (lightMode == 0)
                    {
                        lightSpeed = speed;
                        success = true;
                        Logger.Info($"Light speed cached as {speed}% (mode is Off, not pushing profile)");
                    }
                    else
                    {
                        RgbMode rgbMode = (RgbMode)lightMode;
                        var result = controllerService.SetStickLightMode(rgbMode, r, g, b, lightBrightness / 100f, speed / 100f);
                        success = result.Success;
                        if (success)
                        {
                            lightSpeed = speed;
                            Logger.Info($"Light speed set to {speed}%");
                        }
                        else
                        {
                            failureReason = result.Message;
                            Logger.Error($"Failed to set light speed: {result.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                success = false;
                failureReason = ex.Message;
                Logger.Error($"Error setting light speed: {ex.Message}");
            }
            return success;
        }

        public void SetPerformanceMode(int mode)
        {
            // Debounce rapid mode changes to prevent queue buildup
            int oldMode;
            bool modeActuallyChanged;
            lock (performanceModeDebounceLock)
            {
                oldMode = performanceMode;
                modeActuallyChanged = oldMode != mode;
                pendingPerformanceMode = mode;

                // Update cached mode immediately so SetCustomTDP knows our intended mode
                // This prevents race conditions where TDP is skipped because mode hasn't changed yet
                performanceMode = mode;

                // Cancel existing timer and start a new one
                performanceModeDebounceTimer?.Dispose();
                performanceModeDebounceTimer = new System.Threading.Timer(
                    _ => ApplyPendingPerformanceMode(),
                    null,
                    PERFORMANCE_MODE_DEBOUNCE_MS,
                    System.Threading.Timeout.Infinite // Don't repeat
                );

                Logger.Debug($"SetPerformanceMode: Debouncing mode change to {mode} (will apply in {PERFORMANCE_MODE_DEBOUNCE_MS}ms if no new changes)");
            }

            // Per-mode fan curve swap fires on every actual mode transition (not on no-op
            // "set to current" calls). Independent from the WMI debounce that gates
            // SetSmartFanMode rapid-fire suppression — we want the active curve to swap
            // immediately so the EC override loop's next tick uses the right one.
            if (modeActuallyChanged)
            {
                OnPerformanceModeChanged(oldMode, mode);
            }
        }

        /// <summary>
        /// Called by the debounce timer to apply the pending performance mode.
        /// </summary>
        private void ApplyPendingPerformanceMode()
        {
            int modeToApply;
            lock (performanceModeDebounceLock)
            {
                if (pendingPerformanceMode < 0)
                {
                    return; // No pending mode
                }
                modeToApply = pendingPerformanceMode;
                pendingPerformanceMode = -1; // Clear pending
            }

            ApplyPerformanceModeInternal(modeToApply);
        }

        /// <summary>
        /// Internal method that actually applies the performance mode via WMI.
        /// Called after debouncing completes.
        /// Note: performanceMode is already set optimistically in SetPerformanceMode.
        /// We only need to revert it here if the WMI call fails.
        /// </summary>
        private void ApplyPerformanceModeInternal(int mode)
        {
            if (wmiService == null)
            {
                Logger.Warn("Cannot set performance mode: WMI service not available");
                return;
            }

            try
            {
                TdpMode tdpMode = (TdpMode)mode;
                var result = wmiService.SetSmartFanMode(tdpMode);
                if (result.Success)
                {
                    // Mode was already set optimistically, just log success
                    Logger.Info($"Performance mode set to {tdpMode}");

                    // NOTE: We do NOT reset the fan curve when switching to preset modes.
                    // The hardware's preset modes (Quiet, Balanced, Performance) have their own
                    // built-in fan curves that automatically take effect when SetSmartFanMode is called.
                    // Setting any fan curve here would override the hardware's preset behavior.
                    // The custom fan curve only applies when in Custom mode (255).

                }
                else
                {
                    Logger.Error($"Failed to set performance mode: {result.Message}");
                    // Note: We don't revert performanceMode here because rapid toggling
                    // means the cached value might be for a different pending change
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error setting performance mode: {ex.Message}");
            }
        }

        public void SetCustomTDP(int slow, int fast, int peak)
        {
            if (wmiService == null)
            {
                Logger.Warn("Cannot set custom TDP: WMI service not available");
                return;
            }

            try
            {
                // Only apply TDP if in Custom mode (255)
                // Don't automatically switch modes - preset modes manage TDP via hardware
                // The widget should explicitly switch to Custom mode first if needed
                if (performanceMode != 255)
                {
                    Logger.Info($"Skipping SetCustomTDP({slow}, {fast}, {peak}) - not in Custom mode (current mode: {performanceMode})");
                    return;
                }

                // If there's a pending Custom mode change waiting on debounce, apply it immediately
                // This ensures the hardware is in Custom mode before we try to set TDP values
                bool flushedModeChange = false;
                lock (performanceModeDebounceLock)
                {
                    if (pendingPerformanceMode == 255)
                    {
                        Logger.Info("Flushing pending Custom mode change before applying TDP");
                        performanceModeDebounceTimer?.Dispose();
                        performanceModeDebounceTimer = null;
                        pendingPerformanceMode = -1;

                        // Apply mode change synchronously
                        TdpMode tdpMode = (TdpMode)255;
                        var modeResult = wmiService.SetSmartFanMode(tdpMode);
                        if (modeResult.Success)
                        {
                            Logger.Info($"Performance mode set to {tdpMode}");
                            flushedModeChange = true;
                        }
                        else
                        {
                            Logger.Error($"Failed to set performance mode: {modeResult.Message}");
                            return; // Don't try to set TDP if mode change failed
                        }
                    }
                }

                // If we just flushed a mode change, wait for hardware to confirm it's in Custom mode
                // The WMI call returns quickly but hardware takes ~400ms to actually switch modes
                if (flushedModeChange)
                {
                    const int maxAttempts = 10; // ~500ms max wait
                    const int delayMs = 50;
                    bool hardwareInCustomMode = false;

                    for (int i = 0; i < maxAttempts; i++)
                    {
                        var result = wmiService.GetSmartFanMode();
                        if (result.Success && result.Result == TdpMode.Custom)
                        {
                            hardwareInCustomMode = true;
                            Logger.Info($"Hardware confirmed Custom mode after {(i + 1) * delayMs}ms");
                            break;
                        }
                        System.Threading.Thread.Sleep(delayMs);
                    }

                    if (!hardwareInCustomMode)
                    {
                        Logger.Warn("Hardware did not confirm Custom mode within 500ms, applying TDP anyway and scheduling reapply");
                        // Apply TDP values now and schedule a reapply to ensure they stick
                        ApplyTDPValues(slow, fast, peak);
                        ScheduleTDPReapply(slow, fast, peak);
                        return;
                    }
                }

                // Apply TDP values
                ApplyTDPValues(slow, fast, peak);

                // Set cooldown to prevent RefreshTDPValuesFromDevice from overwriting these values
                lastTdpSetTime = DateTime.Now;

                // Keep the helper's internal cache in sync with the applied values, but no longer
                // SyncToRemote — the Legion-tab Slow/Fast/Peak sliders were removed in the per-
                // profile TDP Boost refactor, so the widget has no handler for these property
                // pushes and just logs "Property LegionCustomTDP... not found for pipe message"
                // 60+ times per minute. The helper still uses the cached fields internally for
                // WMI bookkeeping / OSD current-TDP display.
                LegionCustomTDPSlow.SetValueSilent(slow);
                LegionCustomTDPFast.SetValueSilent(fast);
                LegionCustomTDPPeak.SetValueSilent(peak);
                Logger.Info($"Synced Legion Custom TDP cache (internal): Slow={slow}W, Fast={fast}W, Peak={peak}W");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error setting custom TDP: {ex.Message}");
            }
        }

        /// <summary>
        /// Applies TDP values via WMI with rollback on partial failure.
        /// All three values are applied atomically - if any fails, previous values are restored.
        /// </summary>
        private void ApplyTDPValues(int slow, int fast, int peak)
        {
            // Store previous values for rollback on partial failure
            int previousSlow = customTDPSlow;
            int previousFast = customTDPFast;
            int previousPeak = customTDPPeak;

            // Step 1: Set Slow TDP (SPL)
            var slowResult = wmiService.SetCPUShortTermPowerLimit(slow);
            if (!slowResult.Success)
            {
                Logger.Error($"Failed to set Slow TDP: {slowResult.Message}");
                return; // Nothing to rollback yet
            }
            Logger.Info($"Slow TDP (SPL) set to {slow}W");

            // Step 2: Set Fast TDP (SPPL)
            var fastResult = wmiService.SetCPULongTermPowerLimit(fast);
            if (!fastResult.Success)
            {
                Logger.Error($"Failed to set Fast TDP: {fastResult.Message}");
                // Rollback Slow TDP
                Logger.Warn($"Rolling back Slow TDP to {previousSlow}W due to Fast TDP failure");
                wmiService.SetCPUShortTermPowerLimit(previousSlow);
                return;
            }
            Logger.Info($"Fast TDP (SPPL) set to {fast}W");

            // Step 3: Set Peak TDP (FPPT)
            var peakResult = wmiService.SetCPUPeakPowerLimit(peak);
            if (!peakResult.Success)
            {
                Logger.Error($"Failed to set Peak TDP: {peakResult.Message}");
                // Rollback both Slow and Fast TDP
                Logger.Warn($"Rolling back Slow TDP to {previousSlow}W and Fast TDP to {previousFast}W due to Peak TDP failure");
                wmiService.SetCPUShortTermPowerLimit(previousSlow);
                wmiService.SetCPULongTermPowerLimit(previousFast);
                return;
            }
            Logger.Info($"Peak TDP (FPPT) set to {peak}W");

            // All succeeded - update cached values
            customTDPSlow = slow;
            customTDPFast = fast;
            customTDPPeak = peak;
            Logger.Info($"All TDP values applied successfully: SPL={slow}W, SPPL={fast}W, FPPT={peak}W");
        }

        /// <summary>
        /// Schedules a TDP reapply after 5 seconds (used when switching to Custom mode)
        /// </summary>
        private void ScheduleTDPReapply(int slow, int fast, int peak)
        {
            // Store pending values
            pendingTdpSlow = slow;
            pendingTdpFast = fast;
            pendingTdpPeak = peak;

            // Cancel any existing timer
            if (tdpReapplyTimer != null)
            {
                tdpReapplyTimer.Stop();
                tdpReapplyTimer.Dispose();
            }

            // Create new timer for 5 seconds
            tdpReapplyTimer = new System.Timers.Timer(5000);
            tdpReapplyTimer.AutoReset = false;
            tdpReapplyTimer.Elapsed += (sender, e) =>
            {
                try
                {
                    Logger.Info($"Reapplying TDP after mode change: SPL={pendingTdpSlow}W, SPPL={pendingTdpFast}W, FPPT={pendingTdpPeak}W");
                    ApplyTDPValues(pendingTdpSlow, pendingTdpFast, pendingTdpPeak);
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error reapplying TDP: {ex.Message}");
                }
                finally
                {
                    tdpReapplyTimer?.Dispose();
                    tdpReapplyTimer = null;
                }
            };
            tdpReapplyTimer.Start();
            Logger.Info("Scheduled TDP reapply in 5 seconds");
        }

        /// <summary>
        /// Apply individual Slow TDP (SPL) value
        /// </summary>
        public bool ApplyCustomTDPSlow(int slow, out string failureReason)
        {
            failureReason = null;
            // Skip during startup grace period to prevent widget sync from overriding main TDP slider
            if ((DateTime.Now - startupTime).TotalMilliseconds < STARTUP_GRACE_PERIOD_MS)
            {
                Logger.Info($"Skipping ApplyCustomTDPSlow({slow}W) - still in startup grace period");
                return true;
            }

            if (wmiService == null)
            {
                failureReason = "WMI service not available";
                Logger.Warn("Cannot set custom TDP Slow: WMI service not available");
                return false;
            }

            try
            {
                var result = wmiService.SetCPUShortTermPowerLimit(slow);
                if (result.Success)
                {
                    customTDPSlow = slow;
                    Logger.Info($"Slow TDP (SPL) set to {slow}W");
                    return true;
                }

                failureReason = result.Message;
                Logger.Error($"Failed to set Slow TDP: {result.Message}");
                return false;
            }
            catch (Exception ex)
            {
                failureReason = ex.Message;
                Logger.Error($"Error setting Slow TDP: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Apply individual Fast TDP (SPPL) value
        /// </summary>
        public bool ApplyCustomTDPFast(int fast, out string failureReason)
        {
            failureReason = null;
            // Skip during startup grace period to prevent widget sync from overriding main TDP slider
            if ((DateTime.Now - startupTime).TotalMilliseconds < STARTUP_GRACE_PERIOD_MS)
            {
                Logger.Info($"Skipping ApplyCustomTDPFast({fast}W) - still in startup grace period");
                return true;
            }

            if (wmiService == null)
            {
                failureReason = "WMI service not available";
                Logger.Warn("Cannot set custom TDP Fast: WMI service not available");
                return false;
            }

            try
            {
                var result = wmiService.SetCPULongTermPowerLimit(fast);
                if (result.Success)
                {
                    customTDPFast = fast;
                    Logger.Info($"Fast TDP (SPPL) set to {fast}W");
                    return true;
                }

                failureReason = result.Message;
                Logger.Error($"Failed to set Fast TDP: {result.Message}");
                return false;
            }
            catch (Exception ex)
            {
                failureReason = ex.Message;
                Logger.Error($"Error setting Fast TDP: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Apply individual Peak TDP (FPPT) value
        /// </summary>
        public bool ApplyCustomTDPPeak(int peak, out string failureReason)
        {
            failureReason = null;
            // Skip during startup grace period to prevent widget sync from overriding main TDP slider
            if ((DateTime.Now - startupTime).TotalMilliseconds < STARTUP_GRACE_PERIOD_MS)
            {
                Logger.Info($"Skipping ApplyCustomTDPPeak({peak}W) - still in startup grace period");
                return true;
            }

            if (wmiService == null)
            {
                failureReason = "WMI service not available";
                Logger.Warn("Cannot set custom TDP Peak: WMI service not available");
                return false;
            }

            try
            {
                var result = wmiService.SetCPUPeakPowerLimit(peak);
                if (result.Success)
                {
                    customTDPPeak = peak;
                    Logger.Info($"Peak TDP (FPPT) set to {peak}W");
                    return true;
                }

                failureReason = result.Message;
                Logger.Error($"Failed to set Peak TDP: {result.Message}");
                return false;
            }
            catch (Exception ex)
            {
                failureReason = ex.Message;
                Logger.Error($"Error setting Peak TDP: {ex.Message}");
                return false;
            }
        }

        // Toggles the fan-curve override path. When false: Lenovo WMI Fan_Set_Table only
        // (subject to firmware minimums). When true: helper will additionally drive the EC
        // _EC_TargetRPM register at 0xC6C8 directly via PawnIO (Rodpad-style; bypasses
        // Lenovo's fan controller). The .pawn module that exposes EC port-IO functions
        // isn't bundled yet — see legion_go2_ec.pawn TODO + Resources/PawnIO/. For now we
        // persist the pref + log clearly so the next round-trip with the user shows the
        // unlock state in helper logs.
        public void SetFanCurveUnlocked(bool unlocked)
        {
            if (fanCurveUnlocked == unlocked) return;
            fanCurveUnlocked = unlocked;

            // Persist to the CURRENT mode's slot. Other modes keep their own setting —
            // user can have EC override on for Performance/Custom but off for Quiet, etc.
            SaveOverrideForMode(performanceMode, unlocked);

            // Keep the legacy LegionUnlockFanCurve property aligned with the active-mode
            // unlock state. SetValueSilent skips NotifyPropertyChanged so we don't
            // re-enter SetFanCurveUnlocked or echo back to the widget — just sync the
            // in-memory Value so any caller that reads it sees current state.
            try { LegionUnlockFanCurve?.SetValueSilent(unlocked); }
            catch (Exception ex) { Logger.Debug($"Failed to sync legacy LegionUnlockFanCurve: {ex.Message}"); }

            if (unlocked)
            {
                EnsureEcOverrideRunning();
            }
            else
            {
                Logger.Info("Fan curve override locked — stopping EC override loop, returning fan to firmware control.");
                StopEcFanCurveLoop();
            }

            // Re-apply current curve so behavior reflects the new lock state immediately.
            // Lenovo's Fan_Set_Table is idempotent; firmware re-evaluates on each call.
            try
            {
                if (wmiService != null && fanCurve != null && fanCurve.Length == 10)
                {
                    var result = wmiService.SetFanCurve(fanCurve);
                    Logger.Info($"Fan curve re-applied after unlock toggle: {result.Message}");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Failed to re-apply fan curve after unlock toggle: {ex.Message}");
            }
        }

        // === Per-mode fan curve storage (Rodpad pattern) ============================

        private static string FanCurveKeyForMode(int mode) => $"LegionFanCurve_{mode}";

        // Tries to read a 10-point fan curve from a LocalSettings key. Returns null if
        // missing or malformed.
        private static ushort[] TryLoadCurveFromKey(string key)
        {
            try
            {
                if (!Settings.LocalSettingsHelper.TryGetValue<string>(key, out string s) || string.IsNullOrEmpty(s))
                    return null;
                var parts = s.Split(',');
                if (parts.Length != 10) return null;
                var arr = new ushort[10];
                for (int i = 0; i < 10; i++)
                {
                    if (!int.TryParse(parts[i].Trim(), out int v)) return null;
                    arr[i] = (ushort)Math.Max(0, Math.Min(100, v));
                }
                return arr;
            }
            catch
            {
                return null;
            }
        }

        // Loads all four mode curves into the dict. Migration: if no per-mode keys exist
        // yet but the legacy "LegionFanCurve" key does, copy that single curve into all
        // four slots so users keep their saved curve after upgrade. Missing slots are
        // filled with the Lenovo default. Returns true if at least one per-mode key was
        // found on disk (vs all coming from migration / defaults).
        private bool LoadAllPerModeFanCurves()
        {
            bool foundAny = false;
            foreach (int mode in AllPerformanceModes)
            {
                var loaded = TryLoadCurveFromKey(FanCurveKeyForMode(mode));
                if (loaded != null)
                {
                    fanCurvesByMode[mode] = loaded;
                    foundAny = true;
                }
            }

            if (!foundAny)
            {
                var legacy = TryLoadCurveFromKey("LegionFanCurve");
                if (legacy != null)
                {
                    foreach (int mode in AllPerformanceModes)
                        fanCurvesByMode[mode] = (ushort[])legacy.Clone();
                    Logger.Info($"Migrated legacy LegionFanCurve into per-mode slots: [{string.Join(",", legacy)}]%");
                }
            }

            // Backfill any modes that didn't load (or had no legacy migration source) with
            // Lenovo's stock curve so we never have a missing slot.
            foreach (int mode in AllPerformanceModes)
            {
                if (!fanCurvesByMode.ContainsKey(mode))
                    fanCurvesByMode[mode] = (ushort[])LenovoWMIService.DefaultFanCurve.Clone();
            }

            return foundAny;
        }

        // Loads each mode's EC-override unlock state from LegionUnlockFanCurve_<mode>.
        // Migrates the legacy single key into all four slots if no per-mode keys exist
        // yet. Missing slots default to false (firmware controls fan unless user opts
        // in per-mode).
        private static string EcOverrideKeyForMode(int mode) => $"LegionUnlockFanCurve_{mode}";

        private void LoadAllPerModeEcOverride()
        {
            bool foundAny = false;
            foreach (int mode in AllPerformanceModes)
            {
                try
                {
                    if (Settings.LocalSettingsHelper.TryGetValue<bool>(EcOverrideKeyForMode(mode), out bool v))
                    {
                        ecOverrideByMode[mode] = v;
                        foundAny = true;
                    }
                }
                catch { }
            }

            if (!foundAny)
            {
                bool legacy = false;
                try { Settings.LocalSettingsHelper.TryGetValue<bool>("LegionUnlockFanCurve", out legacy); }
                catch { }
                foreach (int mode in AllPerformanceModes)
                    ecOverrideByMode[mode] = legacy;
                if (legacy) Logger.Info($"Migrated legacy LegionUnlockFanCurve=true into all per-mode slots");
            }

            foreach (int mode in AllPerformanceModes)
                if (!ecOverrideByMode.ContainsKey(mode)) ecOverrideByMode[mode] = false;
        }

        private void SaveOverrideForMode(int mode, bool enabled)
        {
            ecOverrideByMode[mode] = enabled;
            try
            {
                Settings.LocalSettingsHelper.SetValue(EcOverrideKeyForMode(mode), enabled);
                if (mode == performanceMode)
                    Settings.LocalSettingsHelper.SetValue("LegionUnlockFanCurve", enabled);
            }
            catch (Exception ex)
            {
                Logger.Warn($"SaveOverrideForMode({mode}) failed: {ex.Message}");
            }
        }

        // Persists the given curve to the per-mode LocalSettings key. Also updates the
        // legacy single-curve key when the saved mode is the one currently active, so
        // older code paths (and downgrade scenarios) stay consistent.
        private void SaveCurveForMode(int mode, ushort[] curve)
        {
            if (curve == null || curve.Length != 10) return;
            fanCurvesByMode[mode] = (ushort[])curve.Clone();
            try
            {
                string s = string.Join(",", curve.Select(v => (int)v));
                Settings.LocalSettingsHelper.SetValue(FanCurveKeyForMode(mode), s);
                if (mode == performanceMode)
                    Settings.LocalSettingsHelper.SetValue("LegionFanCurve", s);
            }
            catch (Exception ex)
            {
                Logger.Warn($"SaveCurveForMode({mode}) failed: {ex.Message}");
            }
        }

        // === Per-mode pipe channels (LegionFanCurvePerMode / LegionUnlockFanCurvePerMode) ====

        // Parse "<mode>:v0,v1,…,v9" coming from the widget. Updates that mode's slot in
        // fanCurvesByMode + LocalSettings. If the named mode is the active power mode,
        // also reroutes through SetFanCurveFromString so the EC override anchor / WMI
        // table reflect the edit immediately.
        public void OnFanCurvePerModePayload(string payload)
        {
            if (string.IsNullOrEmpty(payload)) return;
            try
            {
                int colon = payload.IndexOf(':');
                if (colon <= 0) { Logger.Warn($"OnFanCurvePerModePayload: malformed payload '{payload}'"); return; }
                if (!int.TryParse(payload.Substring(0, colon), out int mode)) return;
                string csv = payload.Substring(colon + 1);
                var parts = csv.Split(',');
                if (parts.Length != 10) { Logger.Warn($"OnFanCurvePerModePayload: expected 10 values, got {parts.Length} for mode {mode}"); return; }
                var curve = new ushort[10];
                for (int i = 0; i < 10; i++)
                {
                    if (!ushort.TryParse(parts[i], out ushort v)) return;
                    curve[i] = (ushort)Math.Max(0, Math.Min(100, (int)v));
                }

                // For the active mode, route through SetFanCurveFromString so the EC
                // override anchor + active-curve state stay in sync. For non-active
                // modes, just persist to the slot — no hardware write.
                if (mode == performanceMode)
                {
                    SetFanCurveFromString(csv);
                }
                else
                {
                    SaveCurveForMode(mode, curve);
                    Logger.Info($"Per-mode fan curve saved for mode {mode} (not active): [{string.Join(",", curve)}]%");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"OnFanCurvePerModePayload failed for '{payload}': {ex.Message}");
            }
        }

        // Parse "<mode>:0|1" coming from the widget. Updates that mode's unlock slot.
        // If the named mode is active, also flips fanCurveUnlocked so the EC loop
        // starts/stops; otherwise just persists.
        public void OnUnlockFanCurvePerModePayload(string payload)
        {
            if (string.IsNullOrEmpty(payload)) return;
            try
            {
                int colon = payload.IndexOf(':');
                if (colon <= 0) return;
                if (!int.TryParse(payload.Substring(0, colon), out int mode)) return;
                string val = payload.Substring(colon + 1).Trim();
                bool unlocked = val == "1" || string.Equals(val, "true", StringComparison.OrdinalIgnoreCase);

                SaveOverrideForMode(mode, unlocked);

                if (mode == performanceMode)
                {
                    // Active mode: flip the live state via the same code path as the
                    // legacy single-toggle property so the EC loop bootstraps/teardown
                    // logic runs.
                    SetFanCurveUnlocked(unlocked);
                }
                else
                {
                    Logger.Info($"Per-mode unlock saved for mode {mode}: {(unlocked ? "ON" : "off")} (not active — no EC change)");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"OnUnlockFanCurvePerModePayload failed for '{payload}': {ex.Message}");
            }
        }

        // Push every mode's saved curve and unlock state to the widget. Called once on
        // pipe connect so the widget can cache all four modes locally and let the user
        // edit any of them. Each SetValue call carries a unique "<mode>:..." prefix, so
        // GenericProperty's equality check never collapses the four messages.
        public void PushAllPerModeStateToWidget()
        {
            if (!isLegionGoDetected) return;
            try
            {
                foreach (var mode in AllPerformanceModes)
                {
                    if (fanCurvesByMode.TryGetValue(mode, out var curve)
                        && curve != null && curve.Length == 10)
                    {
                        string csv = string.Join(",", curve.Select(v => (int)v));
                        // PushOutbound (not SetValue) so our own push doesn't trigger
                        // the helper-side parser → no redundant SaveCurveForMode +
                        // potential WMI re-write for the active-mode push.
                        LegionFanCurvePerMode?.PushOutbound($"{mode}:{csv}");
                    }
                }
                foreach (var mode in AllPerformanceModes)
                {
                    bool u = ecOverrideByMode.TryGetValue(mode, out bool v) && v;
                    LegionUnlockFanCurvePerMode?.PushOutbound($"{mode}:{(u ? 1 : 0)}");
                }
                Logger.Info("Pushed all per-mode fan curves + unlock states to widget on pipe connect.");
            }
            catch (Exception ex)
            {
                Logger.Warn($"PushAllPerModeStateToWidget failed: {ex.Message}");
            }
        }

        // Send a single mode's curve to the widget — used after internal mode-driven
        // edits (e.g. the active curve was reshaped via SetFanCurveFromString and we
        // need the widget cache to stay current for that mode).
        private void PushPerModeCurveToWidget(int mode)
        {
            try
            {
                if (fanCurvesByMode.TryGetValue(mode, out var curve)
                    && curve != null && curve.Length == 10)
                {
                    string csv = string.Join(",", curve.Select(v => (int)v));
                    LegionFanCurvePerMode?.PushOutbound($"{mode}:{csv}");
                }
            }
            catch (Exception ex) { Logger.Debug($"PushPerModeCurveToWidget({mode}) failed: {ex.Message}"); }
        }

        private void PushPerModeUnlockToWidget(int mode)
        {
            try
            {
                bool u = ecOverrideByMode.TryGetValue(mode, out bool v) && v;
                LegionUnlockFanCurvePerMode?.PushOutbound($"{mode}:{(u ? 1 : 0)}");
            }
            catch (Exception ex) { Logger.Debug($"PushPerModeUnlockToWidget({mode}) failed: {ex.Message}"); }
        }

        // Called from SetPerformanceMode whenever the active power mode actually changes
        // (after debounce). Saves the active curve to the OUTGOING mode's slot in case
        // the user edited it while in that mode, then loads the INCOMING mode's curve
        // into the active state, pushes it to the widget so the displayed curve matches,
        // and resets the EC override anchor so the next tick re-evaluates.
        private void OnPerformanceModeChanged(int oldMode, int newMode)
        {
            if (oldMode == newMode) return;
            try
            {
                // 1. Save outgoing mode's curve snapshot (in case of in-flight edits)
                if (fanCurve != null && fanCurve.Length == 10)
                    SaveCurveForMode(oldMode, fanCurve);

                // 2. Save outgoing mode's unlock state (already current in-memory; this
                //    keeps the dict + legacy key consistent for backward compat).
                ecOverrideByMode[oldMode] = fanCurveUnlocked;

                // 3. Load incoming mode's curve into the active state.
                if (fanCurvesByMode.TryGetValue(newMode, out var newCurve)
                    && newCurve != null && newCurve.Length == 10)
                {
                    Array.Copy(newCurve, fanCurve, 10);
                    Logger.Info($"Fan curve swapped for mode change {oldMode}→{newMode}: [{string.Join(",", fanCurve)}]%");
                }

                // 4. Look up the incoming mode's persisted EC-override state.
                bool newUnlock = ecOverrideByMode.TryGetValue(newMode, out bool v) && v;
                bool unlockChanged = newUnlock != fanCurveUnlocked;
                fanCurveUnlocked = newUnlock;

                // Update the legacy single key so it tracks the active mode (downgrade compat).
                try { Settings.LocalSettingsHelper.SetValue("LegionUnlockFanCurve", newUnlock); }
                catch { }

                Logger.Info($"EC override state for mode {newMode}: {(newUnlock ? "ON" : "off")}{(unlockChanged ? " (changed from previous mode)" : "")}");

                // 5. Reset EC anchor so the next tick recomputes target with the new curve.
                ecAnchorTempC = int.MinValue;
                ecCurrentTargetRpm = -1;
                ecLastWrittenRpm = -1;
                ecLastForcedRewriteUtc = DateTime.MinValue;

                // 6. Start or stop the EC loop based on the new mode's unlock state.
                if (newUnlock)
                {
                    EnsureEcOverrideRunning();
                }
                else if (ecFanCurveTimer != null)
                {
                    Logger.Info($"Mode {newMode} has EC override off — stopping loop, restoring firmware fan control.");
                    StopEcFanCurveLoop();
                }

                // 7. Push the new curve + unlock state to widget so its UI reflects the active
                //    mode. ForceSetValue bypasses the equality check (string may match byte-
                //    for-byte if both modes had the same shape; bool would never refresh).
                try
                {
                    string s = string.Join(",", fanCurve.Select(v => (int)v));
                    LegionFanCurveData?.ForceSetValue(s);
                    LegionUnlockFanCurve?.ForceSetValue(newUnlock);
                }
                catch (Exception ex) { Logger.Debug($"Failed to push state to widget after mode change: {ex.Message}"); }
            }
            catch (Exception ex)
            {
                Logger.Warn($"OnPerformanceModeChanged({oldMode}→{newMode}) failed: {ex.Message}");
            }
        }

        // === EC override path =======================================================

        // Idempotent bootstrap for the EC override path. Loads the signed LpcIO module
        // (lazily, once), runs a sanity-check read, and starts the 3s curve-driven loop.
        // Safe to call from both the unlock-toggle handler and the post-construction
        // bootstrap when fanCurveUnlocked was persisted as true.
        private void EnsureEcOverrideRunning()
        {
            Logger.Warn("Fan curve override UNLOCKED — initialising direct EC access path via signed PawnIO LpcIO module.");

            if (legionEcAccess == null)
                legionEcAccess = new LegionGo2EcAccess();

            if (!legionEcAccess.IsAvailable && !legionEcAccess.Initialize())
            {
                Logger.Warn("EC access initialisation failed — falling through to WMI Fan_Set_Table. Check PawnIO driver is installed.");
                return;
            }

            int currentRpm = legionEcAccess.GetCurrentFanRpm();
            if (currentRpm < 0)
            {
                Logger.Warn("EC access loaded but ReadWord(0xC6C0) returned error — mailbox protocol may not match this firmware revision. Will fall through to WMI for now.");
                return;
            }

            Logger.Info($"EC access verified — current fan RPM via 0xC6C0 = {currentRpm}. Starting curve-driven EC override loop ({EC_FAN_TICK_MS}ms tick).");
            StartEcFanCurveLoop();
        }

        // 3s tick — re-applies the user's fan curve via direct EC RPM override. Beats the
        // firmware's own write to _EC_TargetRPM (which would otherwise overwrite our value
        // on its next curve evaluation).
        private void StartEcFanCurveLoop()
        {
            if (ecFanCurveTimer != null) return; // already running
            ecLastWrittenRpm = -1;
            ecCurrentTargetRpm = -1;
            ecAnchorTempC = int.MinValue;
            ecLastForcedRewriteUtc = DateTime.MinValue;
            ecInPanicMode = false;
            ecFanCurveTimer = new System.Threading.Timer(
                EcFanCurveTick,
                state: null,
                dueTime: 0,        // first tick immediately for responsiveness
                period: EC_FAN_TICK_MS);
        }

        internal void StopEcFanCurveLoop()
        {
            if (ecFanCurveTimer == null) return;
            ecFanCurveTimer.Dispose();
            ecFanCurveTimer = null;
            ecLastWrittenRpm = -1;
            ecCurrentTargetRpm = -1;
            ecAnchorTempC = int.MinValue;
            ecLastForcedRewriteUtc = DateTime.MinValue;
            ecInPanicMode = false;

            // Explicitly clear the EC's _EC_TargetRPM override register (0xC6C8 = 0
            // means "no override, fall back to firmware curve"). Without this, the
            // EC keeps the last RPM we wrote — leaving the fan stuck at that value
            // when the override loop stops, especially on a process crash where
            // this method may not be reached at all but other shutdown hooks call
            // it. Belt-and-suspenders: also re-apply Lenovo's WMI curve so firmware
            // re-evaluates immediately rather than waiting for its next tick.
            try
            {
                legionEcAccess?.SetTargetFanRpm(0);
                Logger.Info("EC override loop stopped — wrote 0xC6C8=0 to clear target RPM override");
            }
            catch (Exception ex) { Logger.Debug($"EC clear on stop failed: {ex.Message}"); }

            try
            {
                if (wmiService != null && fanCurve != null && fanCurve.Length == 10)
                {
                    wmiService.SetFanCurve(fanCurve);
                    Logger.Info("EC override loop stopped — restored Lenovo WMI fan curve");
                }
            }
            catch (Exception ex) { Logger.Debug($"WMI fan curve restore on unlock-off failed: {ex.Message}"); }
        }

        /// <summary>
        /// Best-effort cleanup for process exit (graceful or unhandled exception).
        /// Writes 0xC6C8=0 to release the EC fan override and re-applies the WMI
        /// curve so firmware retakes control. Safe to call even if the EC loop was
        /// never running. Idempotent.
        /// </summary>
        public void EmergencyReleaseFanOverride()
        {
            if (!isLegionGoDetected) return;
            try
            {
                if (legionEcAccess != null)
                {
                    legionEcAccess.SetTargetFanRpm(0);
                    Logger.Warn("EmergencyReleaseFanOverride: wrote 0xC6C8=0 to release fan override");
                }
                if (wmiService != null && fanCurve != null && fanCurve.Length == 10)
                {
                    wmiService.SetFanCurve(fanCurve);
                    Logger.Warn("EmergencyReleaseFanOverride: re-applied WMI fan curve");
                }
            }
            catch (Exception ex)
            {
                // Last-ditch: log to the system event log too, since we may be exiting
                // and the file logger could be torn down.
                try { Logger.Error($"EmergencyReleaseFanOverride failed: {ex}"); } catch { }
            }
        }

        /// <summary>
        /// Called on system resume. The PawnIO handle backing the EC fan
        /// override can die across sleep/hibernate (Rodpad's Windows tool
        /// shipped the same fix on 2026-06-04). Probe the EC path and rebuild
        /// it proactively so the user's fan curve reasserts within one tick of
        /// waking instead of waiting for three failed writes. No-op when the
        /// override loop isn't running.
        /// </summary>
        public void RecoverEcFanOverrideAfterResume()
        {
            try
            {
                if (!fanCurveUnlocked || legionEcAccess == null || ecFanCurveTimer == null)
                    return;

                bool healthy = legionEcAccess.IsAvailable && legionEcAccess.GetCurrentFanRpm() >= 0;
                if (!healthy)
                {
                    Logger.Warn("Resume: EC fan access unhealthy after wake — reinitializing PawnIO path");
                    if (!legionEcAccess.Reinitialize())
                    {
                        Logger.Warn("Resume: EC access reinit failed; tick-level self-heal will keep retrying");
                        return;
                    }
                }

                // Firmware may have cleared 0xC6C8 during sleep. Force the next
                // tick to recompute from a fresh anchor and write unconditionally.
                ecAnchorTempC = int.MinValue;
                ecLastWrittenRpm = -1;
                ecLastForcedRewriteUtc = DateTime.MinValue;
                ecConsecutiveWriteFailures = 0;
                Logger.Info("Resume: EC fan override state reset — curve reasserts on next tick");
            }
            catch (Exception ex)
            {
                Logger.Warn($"RecoverEcFanOverrideAfterResume failed: {ex.Message}");
            }
        }

        private void EcFanCurveTick(object _)
        {
            try
            {
                if (!fanCurveUnlocked || legionEcAccess == null || !legionEcAccess.IsAvailable)
                    return;

                int tempC = ReadFanControlTempForEcLoop();
                if (tempC < 0) return; // sensor read failed; skip this tick

                // --- Software thermal failsafe (panic mode). ---
                // Fires BEFORE anchor + curve logic. Bypasses smoothing entirely so the
                // fan ramps to the failsafe RPM on the very next tick if temp crosses
                // the panic threshold (matches Rodpad's behaviour). Force a re-write
                // every tick while panic is active to defeat any firmware path that
                // might try to walk our value back down.
                if (tempC >= EC_FAN_PANIC_TEMP_C)
                {
                    int curveMaxPercent = fanCurve.Max();
                    int curveMaxRpm = (int)Math.Round(curveMaxPercent * EC_FAN_MAX_RPM / 100.0);
                    int panicRpm = Math.Max(EC_FAN_PANIC_MIN_RPM, curveMaxRpm);
                    if (panicRpm > 65535) panicRpm = 65535;

                    if (!ecInPanicMode)
                    {
                        Logger.Warn($"EC fan PANIC: temp={tempC}°C ≥ {EC_FAN_PANIC_TEMP_C}°C — forcing fan to {panicRpm} RPM (max of {EC_FAN_PANIC_MIN_RPM} firmware-failsafe and {curveMaxRpm} curve-max). Curve and anchor logic bypassed until temp drops.");
                        ecInPanicMode = true;
                    }

                    if (legionEcAccess.SetTargetFanRpm((ushort)panicRpm))
                    {
                        ecCurrentTargetRpm = panicRpm;
                        ecLastWrittenRpm = panicRpm;
                        ecLastForcedRewriteUtc = DateTime.UtcNow;
                    }
                    return; // skip anchor/curve evaluation while in panic
                }

                if (ecInPanicMode)
                {
                    Logger.Info($"EC fan PANIC cleared: temp={tempC}°C dropped below {EC_FAN_PANIC_TEMP_C}°C — resuming curve-driven control.");
                    ecInPanicMode = false;
                    // Force a fresh anchor evaluation so we don't keep panic RPM after panic ends.
                    ecAnchorTempC = int.MinValue;
                    ecCurrentTargetRpm = -1;
                }

                // --- Fan Full Speed override. ---
                // When the user has Full Speed on, the Lenovo WMI full-speed command and this
                // loop's curve write to 0xC6C8 otherwise fight, and the curve wins on the next
                // tick — so Full Speed silently reverts to the curve. Command EC_FAN_MAX_RPM
                // here so both paths agree, re-writing every tick to defeat any firmware
                // walk-back. Panic (thermal safety) still takes precedence above this.
                if (fanFullSpeed)
                {
                    int fullRpm = Math.Min(EC_FAN_MAX_RPM, 65535);
                    if (!ecInFullSpeed)
                    {
                        Logger.Info($"EC fan FULL SPEED engaged — commanding {fullRpm} RPM via 0xC6C8 (curve/anchor bypassed until Full Speed is turned off).");
                        ecInFullSpeed = true;
                    }
                    if (legionEcAccess.SetTargetFanRpm((ushort)fullRpm))
                    {
                        ecCurrentTargetRpm = fullRpm;
                        ecLastWrittenRpm = fullRpm;
                        ecLastForcedRewriteUtc = DateTime.UtcNow;
                    }
                    return; // skip anchor/curve while Full Speed is on
                }

                if (ecInFullSpeed)
                {
                    Logger.Info("EC fan FULL SPEED cleared — resuming curve-driven control.");
                    ecInFullSpeed = false;
                    // Fresh anchor so we don't hold max RPM after Full Speed ends.
                    ecAnchorTempC = int.MinValue;
                    ecCurrentTargetRpm = -1;
                }

                // --- Anchor-point hysteresis (Rodpad pattern). ---
                // Only recompute the target RPM when temperature has drifted ≥5°C from the
                // last anchor. Between anchors the fan target stays constant — prevents the
                // fan yo-yoing on idle ±1°C jitter without sacrificing curve responsiveness
                // for real temperature changes.
                bool anchorMoved = (ecAnchorTempC == int.MinValue)
                                || Math.Abs(tempC - ecAnchorTempC) >= EC_ANCHOR_TEMP_DELTA_C;

                if (anchorMoved)
                {
                    int percent = InterpolateFanCurvePercent(tempC);
                    int targetRpm = (int)Math.Round(percent * EC_FAN_MAX_RPM / 100.0);

                    // EC reg 0xC6C8 treats value 0 as "no override — fall back to firmware
                    // curve". To honor a user-configured 0% point we have to write 1
                    // (Rodpad's pattern); the EC then drives fan to its hardware minimum.
                    if (targetRpm < 1) targetRpm = 1;
                    if (targetRpm > 65535) targetRpm = 65535;

                    if (targetRpm != ecCurrentTargetRpm)
                    {
                        Logger.Info($"EC fan anchor moved: temp {ecAnchorTempC}→{tempC}°C → curve {percent}% → target {ecCurrentTargetRpm}→{targetRpm} RPM");
                        ecCurrentTargetRpm = targetRpm;
                    }
                    ecAnchorTempC = tempC;
                }

                if (ecCurrentTargetRpm < 0) return; // first tick before anchor settled

                // --- Forced re-write every 30s (defeats firmware overrides that may stomp 0xC6C8). ---
                bool forcedRewriteDue = (DateTime.UtcNow - ecLastForcedRewriteUtc).TotalSeconds >= EC_FORCED_REWRITE_INTERVAL_S;
                bool valueChanged = ecCurrentTargetRpm != ecLastWrittenRpm;

                if (!valueChanged && !forcedRewriteDue) return; // skip — no reason to write

                if (legionEcAccess.SetTargetFanRpm((ushort)ecCurrentTargetRpm))
                {
                    int actualRpm = legionEcAccess.GetCurrentFanRpm();
                    string reason = valueChanged ? "target changed" : "30s forced refresh";
                    Logger.Info($"EC fan tick: temp={tempC}°C → wrote {ecCurrentTargetRpm} RPM to 0xC6C8 ({reason}, current={actualRpm})");
                    ecLastWrittenRpm = ecCurrentTargetRpm;
                    ecLastForcedRewriteUtc = DateTime.UtcNow;
                    ecConsecutiveWriteFailures = 0;
                }
                else
                {
                    ecConsecutiveWriteFailures++;
                    if (ecConsecutiveWriteFailures >= EC_WRITE_FAILURES_BEFORE_REINIT)
                    {
                        // Stale PawnIO handle (typically after sleep/hibernate) —
                        // rebuild the access path instead of failing forever.
                        Logger.Warn($"EC fan tick: {ecConsecutiveWriteFailures} consecutive write failures — reinitializing EC access path");
                        ecConsecutiveWriteFailures = 0;
                        if (legionEcAccess.Reinitialize())
                        {
                            Logger.Info("EC fan tick: EC access path rebuilt — forcing immediate rewrite next tick");
                            ecLastWrittenRpm = -1; // guarantee the next tick writes
                        }
                        else
                        {
                            Logger.Warn("EC fan tick: EC access reinit failed; will retry after further failures");
                        }
                    }
                    else
                    {
                        Logger.Warn($"EC fan tick: SetTargetFanRpm({ecCurrentTargetRpm}) failed ({ecConsecutiveWriteFailures}/{EC_WRITE_FAILURES_BEFORE_REINIT} before reinit); will retry next tick");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"EC fan tick exception: {ex.Message}");
            }
        }

        // Source temperature for the EC override loop. We use the AMD CPU temperature
        // (k10temp/Tctl exposed via LibreHardwareMonitor / RyzenSMU) — same sensor source
        // Rodpad's Linux tool reads from sysfs (k10temp/amdgpu/zenpower). Choosing this
        // sensor means our curve responds to the same temp signal a user would see in
        // HWiNFO or RyzenAdj, and avoids the EC-side filtering on Lenovo's 0x01 sensor
        // which can lag actual workload changes by several seconds.
        //
        // Lenovo WMI's GetFanControlSensorTemp is kept as a fallback for the case where
        // PerformanceManager hasn't initialized yet (e.g. early boot ticks before LHM
        // hardware enumeration completes).
        private int ReadFanControlTempForEcLoop()
        {
            try
            {
                if (performanceManager?.CPUTemperature != null && performanceManager.CPUTemperature.Value > 0)
                    return (int)performanceManager.CPUTemperature.Value;
            }
            catch { /* fall through */ }

            try
            {
                if (wmiService != null)
                {
                    var r = wmiService.GetFanControlSensorTemp();
                    if (r.Success && r.Result.HasValue && r.Result.Value > 0)
                        return r.Result.Value;
                }
            }
            catch { /* fall through */ }

            return -1;
        }

        // Linear interpolation against the user's fanCurve[10] using LegionFanCurveTemps[10] as the temp axis.
        // Returns percentage 0–100; values can stay at user-configured 0 (which then maps to 0 RPM = fan off).
        private int InterpolateFanCurvePercent(int tempC)
        {
            if (tempC <= LegionFanCurveTemps[0]) return fanCurve[0];
            // Clamp to the user's top curve point, not a hardcoded 100. Forcing 100% here
            // made the fan spike to max ("jet engine", issue #88) any time temp crossed the
            // top threshold, ignoring the configured top point. True over-temp is handled
            // separately by panic mode (EC_FAN_PANIC_TEMP_C = 101°C), so respecting the curve
            // here is safe.
            if (tempC >= LegionFanCurveTemps[9]) return fanCurve[9];
            for (int i = 0; i < 9; i++)
            {
                if (tempC >= LegionFanCurveTemps[i] && tempC <= LegionFanCurveTemps[i + 1])
                {
                    float t = (tempC - LegionFanCurveTemps[i])
                            / (float)(LegionFanCurveTemps[i + 1] - LegionFanCurveTemps[i]);
                    int speed = (int)Math.Round(fanCurve[i] + t * (fanCurve[i + 1] - fanCurve[i]));
                    return Math.Max(0, Math.Min(100, speed));
                }
            }
            return 100;
        }

        public bool SetFanFullSpeed(bool enabled, out string failureReason)
        {
            failureReason = null;
            if (wmiService == null)
            {
                failureReason = "WMI service not available";
                Logger.Warn("Cannot set fan full speed: WMI service not available");
                return false;
            }

            try
            {
                var result = wmiService.SetFanFullSpeed(enabled);
                if (result.Success)
                {
                    fanFullSpeed = enabled;
                    Logger.Info($"Fan full speed {(enabled ? "enabled" : "disabled")}");
                    return true;
                }

                failureReason = result.Message;
                Logger.Error($"Failed to set fan full speed: {result.Message}");
                return false;
            }
            catch (Exception ex)
            {
                failureReason = ex.Message;
                Logger.Error($"Error setting fan full speed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Sets the fan curve from a comma-separated string of 10 values.
        /// Only applies when in Custom TDP mode (255).
        /// </summary>
        public void SetFanCurveFromString(string curveData)
        {
            if (wmiService == null)
            {
                Logger.Warn("Cannot set fan curve: WMI service not available");
                return;
            }

            if (string.IsNullOrEmpty(curveData))
            {
                Logger.Warn("Empty fan curve data received");
                return;
            }

            try
            {
                var parts = curveData.Split(',');
                if (parts.Length != 10)
                {
                    Logger.Warn($"Invalid fan curve data: expected 10 values, got {parts.Length}");
                    return;
                }

                for (int i = 0; i < 10; i++)
                {
                    if (int.TryParse(parts[i].Trim(), out int value))
                    {
                        fanCurve[i] = (ushort)Math.Max(0, Math.Min(100, value));
                    }
                    else
                    {
                        Logger.Warn($"Invalid fan curve value at index {i}: {parts[i]}");
                        return;
                    }
                }

                Logger.Info($"Fan curve parsed: [{string.Join(", ", fanCurve)}]%");

                // Curve changed → invalidate the EC anchor so the next tick recomputes the
                // target against the new curve. Without this, anchor hysteresis (which only
                // recomputes when |temp - anchorTemp| ≥ 5°C) holds the old target indefinitely
                // when the user edits the curve at a stable temperature, and edits don't
                // visibly take effect until the temp drifts ≥5°C.
                ecAnchorTempC = int.MinValue;
                ecCurrentTargetRpm = -1;

                // Keep the legacy LegionFanCurveData property in sync with the active
                // curve. SetValueSilent skips NotifyPropertyChanged so we don't echo back
                // to the widget (the per-mode channel already handled the inbound).
                try
                {
                    string normalized = string.Join(",", fanCurve.Select(v => (int)v));
                    LegionFanCurveData?.SetValueSilent(normalized);
                }
                catch (Exception ex) { Logger.Debug($"Failed to sync legacy LegionFanCurveData: {ex.Message}"); }

                // Apply the fan curve (it's saved on the controller)
                ApplyFanCurve();
            }
            catch (Exception ex)
            {
                Logger.Error($"Error parsing fan curve data: {ex.Message}");
            }
        }

        private void ApplyFanCurve()
        {
            // Persist always — per-mode storage means edits in Quiet/Balanced/Performance
            // are kept in their own slot and used when the EC override loop runs (or when
            // the user later switches to Custom). The WMI Fan_Set_Table call below stays
            // gated on Custom mode because preset modes have firmware-controlled curves
            // that ignore Fan_Set_Table writes.
            SaveFanCurveToSettings();

            if (performanceMode != 255)
            {
                Logger.Info($"Fan curve persisted for mode {performanceMode} but not pushed to Lenovo WMI (preset modes use firmware curves; EC override path applies the saved curve when unlocked).");
                return;
            }

            if (wmiService == null)
            {
                Logger.Warn("Cannot apply fan curve via WMI: service not available");
                return;
            }

            try
            {
                var result = wmiService.SetFanCurve(fanCurve);
                if (result.Success)
                    Logger.Info($"Fan curve applied (Custom mode): [{string.Join(", ", fanCurve)}]%");
                else
                    Logger.Error($"Failed to apply fan curve: {result.Message}");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error applying fan curve: {ex.Message}");
            }
        }

        /// <summary>
        /// Saves the current fan curve to settings for persistence. Per-mode aware:
        /// writes to LegionFanCurve_&lt;currentMode&gt; AND the legacy LegionFanCurve key
        /// (kept synced with the active mode for backward compat).
        /// </summary>
        private void SaveFanCurveToSettings()
        {
            try
            {
                SaveCurveForMode(performanceMode, fanCurve);
                Logger.Info($"Fan curve saved for mode {performanceMode}: [{string.Join(",", fanCurve)}]%");
            }
            catch (Exception ex)
            {
                Logger.Warn($"Failed to save fan curve: {ex.Message}");
            }
        }

        public void SetGyroEnabled(bool enabled)
        {
            // WIP - Gyro control not working currently
            Logger.Warn("Gyro control is WIP - not implemented");
            gyroEnabled = enabled;
        }

        public bool TrySetVibration(int level)
        {
            if (!isControllerConnected || controllerService == null)
            {
                Logger.Warn("Cannot set vibration: controller not connected");
                return false;
            }

            if (level < 0 || level > 3)
            {
                Logger.Warn($"Invalid vibration level: {level}");
                return false;
            }

            try
            {
                var vibLevel = (ControllerVibrationLevel)level;
                var result = controllerService.SetBothControllersVibration(vibLevel);
                if (result.Success)
                {
                    vibrationLevel = level;
                    Logger.Info($"Vibration set to level {level} ({result.Message})");
                    return true;
                }

                Logger.Error($"Failed to set vibration: {result.Message}");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Error($"Error setting vibration: {ex.Message}");
                return false;
            }
        }

        public bool SetPowerLight(bool enabled, out string failureReason)
        {
            failureReason = null;
            if (wmiService == null)
            {
                failureReason = "WMI service not available";
                Logger.Warn("Cannot set power light: WMI service not available");
                return false;
            }

            try
            {
                var result = wmiService.SetPowerLight(enabled);
                if (result.Success)
                {
                    powerLightEnabled = enabled;
                    Logger.Info($"Power light {(enabled ? "enabled" : "disabled")}");
                    return true;
                }

                failureReason = result.Message;
                Logger.Error($"Failed to set power light: {result.Message}");
                return false;
            }
            catch (Exception ex)
            {
                failureReason = ex.Message;
                Logger.Error($"Error setting power light: {ex.Message}");
                return false;
            }
        }

        public bool SetChargeLimit(bool enabled, out string failureReason)
        {
            failureReason = null;
            if (wmiService == null)
            {
                failureReason = "WMI service not available";
                Logger.Warn("Cannot set charge limit: WMI service not available");
                return false;
            }

            try
            {
                var result = wmiService.SetBatteryChargeLimit(enabled);
                if (result.Success)
                {
                    chargeLimitEnabled = enabled;
                    // Persist the user's preference so we can re-apply it on boot. On the Legion Go 2
                    // (Z2E) the EC does NOT retain the 80% limit across reboot, so reading the EC at
                    // startup yields "off" and the toggle appears to never save (issue #88 bug #5).
                    try { Settings.LocalSettingsHelper.SetValue("LegionChargeLimit", enabled); }
                    catch (Exception persistEx) { Logger.Debug($"Failed to persist LegionChargeLimit: {persistEx.Message}"); }
                    Logger.Info($"Battery charge limit (80%) {(enabled ? "enabled" : "disabled")}");
                    return true;
                }

                failureReason = result.Message;
                Logger.Error($"Failed to set charge limit: {result.Message}");
                return false;
            }
            catch (Exception ex)
            {
                failureReason = ex.Message;
                Logger.Error($"Error setting charge limit: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Sets the button mapping for a remappable button (legacy gamepad-only method).
        /// </summary>
        /// <param name="buttonIndex">Button index: 0=Y1, 1=Y2, 2=Y3, 3=M1, 4=M2, 5=M3</param>
        /// <param name="actionIndex">Action index matching RemapAction enum (0-28)</param>
        public void SetButtonMapping(int buttonIndex, int actionIndex)
        {
            // Forward to advanced method with gamepad type
            SetButtonMappingAdvanced(buttonIndex, 0, new int[] { actionIndex }, out _);
        }

        /// <summary>
        /// Sets the button mapping with type support (Gamepad, Keyboard, Mouse).
        /// </summary>
        /// <param name="buttonIndex">Button index: 0=Y1, 1=Y2, 2=Y3, 3=M1, 4=M2, 5=M3</param>
        /// <param name="mappingType">0=Gamepad, 1=Keyboard, 2=Mouse</param>
        /// <param name="values">Mapping values (gamepad action, keyboard keys[], or mouse button)</param>
        public bool SetButtonMappingAdvanced(int buttonIndex, int mappingType, int[] values, out string failureReason)
        {
            failureReason = null;
            try
            {
                // Helper-side kinds (System actions; scroll directions repeat while held)
                // register/deregister BEFORE the firmware ops so the dispatch table stays
                // correct even if the controller is briefly unreachable.
                var edgeButton = EdgeButtonForSlot(buttonIndex);
                bool isSystem = mappingType == 3;
                bool isScrollRepeat = mappingType == 2 && values != null && values.Length > 0 && values[0] >= 4 && values[0] <= 7;
                if (edgeButton != Labs.LegionInputButton.None)
                {
                    SetHelperSideButtonMapping(edgeButton,
                        isSystem && values != null && values.Length > 0 ? values[0] : (int?)null,
                        isScrollRepeat ? values[0] : (int?)null);
                }

                using var controller = new LegionGoController();
                if (!controller.Connect())
                {
                    failureReason = "controller not connected";
                    Logger.Warn("Cannot set button mapping: controller not connected");
                    return false;
                }

                // Map button index to RemappableButton enum
                RemappableButton remapButton = buttonIndex switch
                {
                    0 => RemappableButton.Y1,
                    1 => RemappableButton.Y2,
                    2 => RemappableButton.Y3,
                    3 => RemappableButton.M1,
                    4 => RemappableButton.M2,
                    5 => RemappableButton.M3,
                    _ => throw new ArgumentException($"Invalid button index: {buttonIndex}")
                };

                // System / scroll-repeat: the helper drives the behavior; clear the
                // firmware mapping so the button emits nothing OS-visible on its own.
                if (isSystem || isScrollRepeat)
                {
                    bool cleared = controller.ClearButtonMapping(remapButton);
                    Logger.Info($"Button {remapButton} firmware mapping cleared for helper-side {(isSystem ? "system action" : "scroll repeat")} (cleared={cleared})");
                    // Report the actual clear result: if the firmware rejected it, the stock
                    // mapping keeps firing UNDER the helper-side action — that's a failed
                    // apply, not a success (matches the disabled-path in SetLegionButtonMapping).
                    return cleared;
                }

                // Log the button details for debugging
                var ctrl = LegionGoController.GetControllerForButton(remapButton);
                Logger.Info($"SetButtonMappingAdvanced: buttonIndex={buttonIndex}, button={remapButton}(0x{(byte)remapButton:X2}), controller={ctrl}(0x{(byte)ctrl:X2}), mappingType={mappingType}, values=[{string.Join(",", values ?? Array.Empty<int>())}]");

                bool success;
                if (mappingType == 0 && (values == null || values.Length == 0 || values[0] == 0))
                {
                    // Clear mapping (disabled)
                    success = controller.ClearButtonMapping(remapButton);
                    Logger.Info($"Button {remapButton} mapping cleared (HID: 05 00 12 0A {(byte)ctrl:X2} 01 11 01 {(byte)remapButton:X2} 01)");
                }
                else
                {
                    // Set mapping with type
                    var type = (MappingType)(mappingType + 1);  // 0→1, 1→2, 2→3
                    byte[] mappings;

                    if (mappingType == 0)
                    {
                        // Gamepad: use RemapAction
                        var action = RemapActionHelper.GetByIndex(values[0]);
                        mappings = new byte[] { (byte)action };
                    }
                    else
                    {
                        // Keyboard or Mouse: use raw values
                        mappings = values.Select(v => (byte)v).ToArray();
                    }

                    success = controller.SetButtonMappingAdvanced(remapButton, type, mappings);
                    var hidBytes = $"05 00 12 0A {(byte)ctrl:X2} 01 11 01 {(byte)remapButton:X2} {(byte)type:X2} {string.Join(" ", mappings.Select(b => b.ToString("X2")))}";
                    Logger.Info($"Button {remapButton} mapped to {type} with mappings=[{string.Join(",", mappings.Select(b => $"0x{b:X2}"))}] (HID: {hidBytes})");
                }

                if (!success)
                {
                    failureReason = $"controller rejected the mapping write for {remapButton}";
                    Logger.Error($"Failed to set button mapping for {remapButton}");
                }

                // Delay to allow controller firmware to process command before next one
                System.Threading.Thread.Sleep(HID_COMMAND_DELAY_MS);
                return success;
            }
            catch (Exception ex)
            {
                failureReason = ex.Message;
                Logger.Error($"Error setting button mapping: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Sets mapping for Legion-specific buttons (Desktop, Page) which use GamepadButton codes.
        /// </summary>
        /// <param name="button">The GamepadButton to map (DesktopButton=0x25, PageButton=0x26)</param>
        /// <param name="mappingType">0=Gamepad, 1=Keyboard, 2=Mouse</param>
        /// <param name="values">Mapping values (key codes, button codes, etc.)</param>
        public bool SetLegionButtonMapping(GamepadButton button, int mappingType, int[] values, out string failureReason)
        {
            failureReason = null;
            try
            {
                // Desktop/Page front buttons arrive on the edge stream as Mode/Share.
                var edgeButton = button == GamepadButton.DesktopButton ? Labs.LegionInputButton.Mode
                               : button == GamepadButton.PageButton ? Labs.LegionInputButton.Share
                               : Labs.LegionInputButton.None;
                bool isSystem = mappingType == 3;
                bool isScrollRepeat = mappingType == 2 && values != null && values.Length > 0 && values[0] >= 4 && values[0] <= 7;
                if (edgeButton != Labs.LegionInputButton.None)
                {
                    SetHelperSideButtonMapping(edgeButton,
                        isSystem && values != null && values.Length > 0 ? values[0] : (int?)null,
                        isScrollRepeat ? values[0] : (int?)null);
                }

                using var controller = new LegionGoController();
                if (!controller.Connect())
                {
                    failureReason = "controller not connected";
                    Logger.Warn($"Cannot set {button} mapping: controller not connected");
                    return false;
                }

                if (isSystem || isScrollRepeat)
                {
                    controller.ClearGamepadButtonMapping(button);
                    Logger.Info($"{button} firmware mapping cleared for helper-side {(isSystem ? "system action" : "scroll repeat")}");
                    return true;
                }

                var type = (MappingType)(mappingType + 1); // 0->Gamepad(1), 1->Keyboard(2), 2->Mouse(3)
                byte[] mappings;

                if (mappingType == 0)
                {
                    // Gamepad: use RemapActionHelper to convert to HID button code
                    if (values.Length == 0 || values[0] == 0)
                    {
                        // Disabled - clear the mapping
                        bool cleared = controller.ClearGamepadButtonMapping(button);
                        Logger.Info($"{button} mapping cleared (disabled)");
                        if (!cleared) failureReason = $"controller rejected clearing {button} mapping";
                        return cleared;
                    }
                    var action = RemapActionHelper.GetByIndex(values[0]);
                    mappings = new byte[] { (byte)action };
                }
                else
                {
                    // Keyboard or Mouse: use raw values
                    mappings = values.Select(v => (byte)v).ToArray();
                }

                bool success = controller.SetGamepadButtonMappingAdvanced(button, type, mappings);
                var hidBytes = $"05 00 12 0A 03 01 11 01 {(byte)button:X2} {(byte)type:X2} {string.Join(" ", mappings.Select(b => b.ToString("X2")))}";
                Logger.Info($"{button} mapped to {type} with mappings=[{string.Join(",", mappings.Select(b => $"0x{b:X2}"))}] (HID: {hidBytes})");

                if (!success)
                {
                    failureReason = $"controller rejected the mapping write for {button}";
                    Logger.Error($"Failed to set {button} mapping");
                }

                System.Threading.Thread.Sleep(HID_COMMAND_DELAY_MS);
                return success;
            }
            catch (Exception ex)
            {
                failureReason = ex.Message;
                Logger.Error($"Error setting {button} mapping: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Sets the Nintendo layout mode (swaps A↔B and X↔Y face buttons).
        /// </summary>
        public bool SetNintendoLayout(bool enabled, out string failureReason)
        {
            failureReason = null;
            try
            {
                using var controller = new LegionGoController();
                if (!controller.Connect())
                {
                    failureReason = "controller not connected";
                    Logger.Warn("Cannot set Nintendo layout: controller not connected");
                    return false;
                }

                bool success = controller.SetNintendoLayout(enabled);
                if (success)
                {
                    nintendoLayoutEnabled = enabled;
                    Logger.Info($"Nintendo layout {(enabled ? "enabled" : "disabled")}");
                }
                else
                {
                    failureReason = "controller rejected the Nintendo layout write";
                    Logger.Error("Failed to set Nintendo layout");
                }
                return success;
            }
            catch (Exception ex)
            {
                failureReason = ex.Message;
                Logger.Error($"Error setting Nintendo layout: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Sets the vibration mode preset (game-specific vibration patterns).
        /// </summary>
        /// <param name="mode">Vibration mode: 1=FPS, 2=Racing, 3=AVG, 4=SPG, 5=RPG</param>
        public void SetVibrationMode(int mode)
        {
            try
            {
                using var controller = new LegionGoController();
                if (!controller.Connect())
                {
                    Logger.Warn("Cannot set vibration mode: controller not connected");
                    return;
                }

                VibrationMode vibMode = (VibrationMode)mode;
                bool success = controller.SetVibrationMode(vibMode);
                if (success)
                {
                    vibrationMode = mode;
                    Logger.Info($"Vibration mode set to {vibMode}");
                }
                else
                {
                    Logger.Error($"Failed to set vibration mode to {vibMode}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error setting vibration mode: {ex.Message}");
            }
        }
        public override void Update()
        {
            // Detect if controller service has disconnected (e.g., controller removed)
            if (isControllerConnected && controllerService != null && !controllerService.IsConnected)
            {
                isControllerConnected = false;
                Logger.Info("Controller disconnected - will attempt reconnection");
                // Battery monitoring will stop automatically when connection is lost
            }

            // Periodically check for controller connection if not connected
            if (!isControllerConnected && controllerService != null)
            {
                try
                {
                    var connectResult = controllerService.Connect();
                    if (connectResult.Success)
                    {
                        isControllerConnected = true;
                        isLegionGoDetected = true;
                        LegionGoDetected.SetDetected(true);
                        Logger.Info($"Legion Go controller reconnected: {connectResult.Message}");

                        // Restart battery monitoring on reconnection
                        StartBatteryMonitoring();

                        // The Go 2 receiver re-presents under a different USB PID when a
                        // half sleeps/wakes or docks (61EB xinput <-> 61ED dual-dinput,
                        // 61EE seen too) - every flip creates fresh devnodes, orphaning
                        // any HidHide cloak applied earlier. Let listeners re-assert.
                        try { ControllerReconnected?.Invoke(); }
                        catch (Exception ex) { Logger.Warn($"ControllerReconnected handler threw: {ex.Message}"); }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Debug($"Controller reconnection attempt failed: {ex.Message}");
                }
            }

            // Rate-limit fan speed refresh to reduce WMI call overhead. The active
            // cadence is 2s when someone needs the data (fan-curve UI open, sidebar/OSD
            // showing, game running); otherwise back off to 10s so an idle helper isn't
            // spending ~30ms of WMI every two seconds for nobody.
            // Note: TDP refresh is disabled to prevent conflicts with user-set values
            if (isLegionGoDetected && wmiService != null)
            {
                bool active = fanCurveVisible || (performanceManager?.IsAnyMetricsConsumerActive ?? true);
                int intervalMs = active ? FAN_SPEED_REFRESH_INTERVAL_MS : FAN_SPEED_IDLE_INTERVAL_MS;
                var now = DateTime.Now;
                if ((now - lastFanSpeedRefresh).TotalMilliseconds >= intervalMs)
                {
                    RefreshFanSpeed();
                    lastFanSpeedRefresh = now;
                }
            }
        }

        /// <summary>
        /// Reads current CPU fan speed from device for internal use (OSD display).
        /// Also pushes temp/RPM updates to widget when fan curve is visible.
        /// </summary>
        private void RefreshFanSpeed()
        {
            try
            {
                // Read fan RPM for internal use (OSD)
                var fanResult = wmiService.GetCpuFanSpeed();
                if (fanResult.Success && fanResult.Result.HasValue)
                {
                    cpuFanSpeed = fanResult.Result.Value;
                }

                // Always push RPM so the OSD + quick-settings fan readout shows a value
                // instead of "--" even when the fan-curve panel hasn't been opened (issue
                // #83/#88). The extra sensor-temp WMI reads below stay gated on the panel
                // being visible since they're only consumed by the fan-curve UI.
                LegionCPUFanRPM.UpdateRPM(cpuFanSpeed);

                if (fanCurveVisible)
                {
                    // Read the fan control sensor temp (0x01 sensor) - this is what the EC uses for fan curve lookup
                    var fanSensorResult = wmiService.GetFanControlSensorTemp();
                    if (fanSensorResult.Success && fanSensorResult.Result.HasValue)
                    {
                        LegionFanSensorTemp.UpdateTemp(fanSensorResult.Result.Value);
                    }

                    // Also send CPU temp for reference (VRM temp with fallback to CPU temp)
                    if (performanceManager?.VRMTemperature != null && performanceManager.VRMTemperature.Value > 0)
                    {
                        int vrmTemp = (int)performanceManager.VRMTemperature.Value;
                        LegionCPUCurrentTemp.UpdateTemp(vrmTemp);
                    }
                    else if (performanceManager?.CPUTemperature != null)
                    {
                        int cpuTemp = (int)performanceManager.CPUTemperature.Value;
                        LegionCPUCurrentTemp.UpdateTemp(cpuTemp);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"Error reading fan speed: {ex.Message}");
            }
        }

        /// <summary>
        /// Called by LegionFanCurveVisibleProperty when widget sets visibility.
        /// </summary>
        public void SetFanCurveVisible(bool visible)
        {
            fanCurveVisible = visible;
            Logger.Debug($"Fan curve visibility set to: {visible}");

            // Immediately push current values when fan curve becomes visible
            if (visible)
            {
                LegionCPUFanRPM.UpdateRPM(cpuFanSpeed);

                // Read the fan control sensor temp (0x01 sensor) - this is what the EC uses for fan curve lookup
                var fanSensorResult = wmiService?.GetFanControlSensorTemp();
                if (fanSensorResult?.Success == true && fanSensorResult.Value.Result.HasValue)
                {
                    LegionFanSensorTemp.UpdateTemp(fanSensorResult.Value.Result.Value);
                }

                // Also push CPU temp for reference (VRM temp with fallback to CPU temp)
                if (performanceManager?.VRMTemperature != null && performanceManager.VRMTemperature.Value > 0)
                {
                    int vrmTemp = (int)performanceManager.VRMTemperature.Value;
                    LegionCPUCurrentTemp.UpdateTemp(vrmTemp);
                }
                else if (performanceManager?.CPUTemperature != null)
                {
                    int cpuTemp = (int)performanceManager.CPUTemperature.Value;
                    LegionCPUCurrentTemp.UpdateTemp(cpuTemp);
                }
            }
        }

        /// <summary>
        /// Gets the current CPU fan speed in RPM. Returns 0 if Legion Go is not detected.
        /// </summary>
        public int GetCpuFanSpeed()
        {
            return isLegionGoDetected ? cpuFanSpeed : 0;
        }

        /// <summary>
        /// Reads current TDP values from device and updates properties silently (without triggering WMI set calls)
        /// </summary>
        private void RefreshTDPValuesFromDevice()
        {
            // Skip refresh during cooldown period after TDP was set
            // This prevents stale WMI reads from overwriting recently-set values
            if ((DateTime.Now - lastTdpSetTime).TotalMilliseconds < TDP_REFRESH_COOLDOWN_MS)
            {
                return;
            }

            try
            {
                // Skip SyncToRemote — the Legion-tab SPL/SPPL/FPPT sliders were removed in the
                // per-profile TDP Boost refactor, so pushing these properties produces only
                // "Property LegionCustomTDP... not found for pipe message" widget warns.
                // The helper cache (customTDPSlow / Fast / Peak fields) is still updated for
                // internal bookkeeping and OSD display via CurrentTDP.
                var slowResult = wmiService.GetCPUShortTermPowerLimit();
                if (slowResult.Success && slowResult.Result.HasValue && slowResult.Result.Value != customTDPSlow)
                {
                    customTDPSlow = slowResult.Result.Value;
                    LegionCustomTDPSlow.SetValueSilent(customTDPSlow);
                    Logger.Debug($"Refreshed Slow TDP (SPL): {customTDPSlow}W");
                }

                var fastResult = wmiService.GetCPULongTermPowerLimit();
                if (fastResult.Success && fastResult.Result.HasValue && fastResult.Result.Value != customTDPFast)
                {
                    customTDPFast = fastResult.Result.Value;
                    LegionCustomTDPFast.SetValueSilent(customTDPFast);
                    Logger.Debug($"Refreshed Fast TDP (SPPL): {customTDPFast}W");
                }

                var peakResult = wmiService.GetCPUPeakPowerLimit();
                if (peakResult.Success && peakResult.Result.HasValue && peakResult.Result.Value != customTDPPeak)
                {
                    customTDPPeak = peakResult.Result.Value;
                    LegionCustomTDPPeak.SetValueSilent(customTDPPeak);
                    Logger.Debug($"Refreshed Peak TDP (FPPT): {customTDPPeak}W");
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"Error refreshing TDP values: {ex.Message}");
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Logger.Info("Disposing Legion Manager...");

                try
                {
                    // Cancel any pending debounced firmware writes so a 300ms-deferred
                    // slider write can't open a fresh controller handle mid-teardown.
                    DisposeFirmwareDebounceTimers();
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error disposing firmware debounce timers: {ex.Message}");
                }

                try
                {
                    // Stop battery monitoring
                    StopBatteryMonitoring();
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error stopping battery monitoring: {ex.Message}");
                }

                try
                {
                    // Dispose TDP reapply timer
                    if (tdpReapplyTimer != null)
                    {
                        tdpReapplyTimer.Stop();
                        tdpReapplyTimer.Dispose();
                        tdpReapplyTimer = null;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error disposing TDP reapply timer: {ex.Message}");
                }

                try
                {
                    controllerService?.Dispose();
                    controllerService = null;
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error disposing controller service: {ex.Message}");
                }

                wmiService = null;
                Logger.Info("Legion Manager disposed.");
            }

            base.Dispose(disposing);
        }
    }
}
