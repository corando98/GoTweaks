using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using NLog;
using Windows.Storage;
using XboxGamingBarHelper.ControllerEmulation;

namespace XboxGamingBarHelper.Labs
{
    /// <summary>
    /// Action type for Legion button remap.
    /// </summary>
    internal enum LegionButtonAction
    {
        XboxGuide = 0,
        KeyboardShortcut = 1,
        RunCommand = 2,
        FocusGoTweaks = 3
    }

    /// <summary>
    /// Unified monitor for Legion Go controller HID input.
    /// Monitors both Legion L and Legion R button presses and controller battery status.
    /// Single monitor handles all button/battery parsing from one HID device.
    /// </summary>
    internal class LegionButtonMonitor : IDisposable
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        // Lenovo tablet controller HID identifiers (Legion Go / Go 2 families).
        private const ushort LEGION_TABLET_VID = 0x17EF;
        private static readonly ushort[] LEGION_TABLET_PIDS = { 0x6182, 0x6183, 0x6184, 0x6185, 0x61EB, 0x61EC, 0x61ED, 0x61EE };
        // Legion Go 2 tablet controller PIDs (17EF:61Ex).
        // Used for front button parsing differences (Desktop/Page moved from button byte 0 -> byte #20 in 0xA1 report).
        private static readonly ushort[] LEGION_GO2_TABLET_PIDS = { 0x61EB, 0x61EC, 0x61ED, 0x61EE };
        // Legion Go S controller HID identifiers (xinput / dinput controller modes).
        // Source: HHD slim backend constants (GOS_VID + GOS_PIDS).
        private const ushort LEGION_GOS_VID = 0x1A86;
        private static readonly ushort[] LEGION_GOS_PIDS = { 0xE310, 0xE311 };

        // Legion button position in HID report
        // Attached mode (04:00:A1 header): buttons at byte 16
        // Detached mode (04:XX:XX header): buttons at byte 18
        private const int BUTTON_BYTE_ATTACHED = 16;
        private const int BUTTON_BYTE_DETACHED = 18;
        private const byte LEGION_L_BIT = 0x80;
        private const byte LEGION_R_BIT = 0x40;
        private const ushort XINPUT_GAMEPAD_DPAD_UP = 0x0001;
        private const ushort XINPUT_GAMEPAD_DPAD_DOWN = 0x0002;
        private const ushort XINPUT_GAMEPAD_DPAD_LEFT = 0x0004;
        private const ushort XINPUT_GAMEPAD_DPAD_RIGHT = 0x0008;
        private const ushort XINPUT_GAMEPAD_START = 0x0010;
        private const ushort XINPUT_GAMEPAD_BACK = 0x0020;
        private const ushort XINPUT_GAMEPAD_LEFT_THUMB = 0x0040;
        private const ushort XINPUT_GAMEPAD_RIGHT_THUMB = 0x0080;
        private const ushort XINPUT_GAMEPAD_LEFT_SHOULDER = 0x0100;
        private const ushort XINPUT_GAMEPAD_RIGHT_SHOULDER = 0x0200;
        private const ushort XINPUT_GAMEPAD_A = 0x1000;
        private const ushort XINPUT_GAMEPAD_B = 0x2000;
        private const ushort XINPUT_GAMEPAD_X = 0x4000;
        private const ushort XINPUT_GAMEPAD_Y = 0x8000;
        private const float GYRO_SCALE_DEG_PER_SECOND = 2000.0f / 32768.0f;
        // Legion Go 2 BMI260 ships configured for ±8g (Lenovo firmware
        // programs the range at boot — no explicit range-set command in any
        // userspace driver). The earlier ±4g constant was inherited from the
        // original-Legion-Go reference parser and read 0.49G at rest instead
        // of 1.0G. Verified against hhd's m/s² scale (0.00212 m/s²/LSB =
        // ±7.08g full-scale ≈ ±8g) and empirical at-rest reading of 0.98G
        // after this change.
        private const float ACCEL_SCALE_G = 8.0f / 32768.0f;

        // Diagnostic counter for actual HID report arrival rate.
        // Confirms (or refutes) the indirect 64 Hz measurement we inferred
        // from forwarder stats. Reset every second by the read loop.
        private static int _hidReportRateCount = 0;
        private static long _hidReportRateLastEmitTicks = 0;
        private static readonly System.Collections.Generic.Dictionary<int, int> _hidReportHeaderCounts = new System.Collections.Generic.Dictionary<int, int>();

        /// <summary>
        /// Auto-reset event signaled after each successful gamepad/gyro parse
        /// so downstream consumers (VIIPER forwarder, legacy CE forwarder) can
        /// block on a fresh sample instead of polling. Zero CPU when idle,
        /// catches every HID report at the moment it lands in the cache.
        /// </summary>
        private static readonly System.Threading.AutoResetEvent _newSampleEvent = new System.Threading.AutoResetEvent(false);

        /// <summary>
        /// Block until a fresh sample is available, or the timeout elapses.
        /// Returns true if signaled (fresh sample ready), false on timeout.
        /// Cheap fast path when samples arrive faster than the consumer can
        /// process them; never wastes CPU when samples are sparse.
        /// </summary>
        public static bool WaitForNewSample(int timeoutMs)
        {
            return _newSampleEvent.WaitOne(timeoutMs);
        }
        private const float LEGACY_M8_GYRO_SCALE_DEG_PER_SECOND = 2000.0f / 128.0f;
        private const byte LEGION_CONTROLLER_LEFT_ID = 0x03;
        private const byte LEGION_CONTROLLER_RIGHT_ID = 0x04;
        private const ushort LEGION_AUX_MODE = 0x0001;
        private const ushort LEGION_AUX_SHARE = 0x0002;
        private const ushort LEGION_AUX_EXTRA_L1 = 0x0004;
        private const ushort LEGION_AUX_EXTRA_L2 = 0x0008;
        private const ushort LEGION_AUX_EXTRA_R1 = 0x0010;
        private const ushort LEGION_AUX_EXTRA_RM1 = 0x0020;
        private const ushort LEGION_AUX_EXTRA_R2 = 0x0040;
        private const ushort LEGION_AUX_EXTRA_R3 = 0x0080;
        // Legion Go S back-button (Legion L/R) bits in byte 2.
        // Source: HHD slim const.py (extra_l1, extra_r1).
        private const int GOS_BUTTON_BYTE = 2;
        private const byte GOS_LEGION_L_BIT = 0x01;
        private const byte GOS_LEGION_R_BIT = 0x02;

        // Detected controller mode
        private bool isDetachedMode = false;
        private int currentButtonByte = BUTTON_BYTE_ATTACHED;

        // VID/PID combinations whose firmware ignores our init command (observed on
        // Legion Go 8ASP2 / 83N0). Skip the init write on subsequent probes for these
        // devices and go straight to the detached/uninitialized fallback to avoid
        // burning ~300ms per reconnect on the same failed init pattern.
        private static readonly HashSet<uint> _initFutileDevices = new HashSet<uint>();
        private static readonly object _initFutileLock = new object();

        private static bool IsInitFutile(ushort vid, ushort pid)
        {
            uint key = ((uint)vid << 16) | pid;
            lock (_initFutileLock) { return _initFutileDevices.Contains(key); }
        }

        private static void MarkInitFutile(ushort vid, ushort pid)
        {
            uint key = ((uint)vid << 16) | pid;
            lock (_initFutileLock) { _initFutileDevices.Add(key); }
        }

        // Configuration for Legion L button
        private bool legionLEnabled = false;
        private LegionButtonAction legionLActionType = LegionButtonAction.XboxGuide;
        private string legionLShortcutKeys = "";
        private string legionLCommandPath = "";

        // Configuration for Legion R button
        private bool legionREnabled = false;
        private LegionButtonAction legionRActionType = LegionButtonAction.XboxGuide;
        private string legionRShortcutKeys = "";
        private string legionRCommandPath = "";

        // Configuration for Scroll Wheel (unified scroll + click)
        // Note: Raw Input API can't distinguish scroll up/down, so we have unified "scroll" action
        private bool scrollEnabled = false;
        private LegionButtonAction scrollActionType = LegionButtonAction.XboxGuide;
        private string scrollShortcutKeys = "";
        private string scrollCommandPath = "";

        private bool scrollClickEnabled = false;
        private LegionButtonAction scrollClickActionType = LegionButtonAction.XboxGuide;
        private string scrollClickShortcutKeys = "";
        private string scrollClickCommandPath = "";

        // Legacy fields for backward compatibility (deprecated - use scrollEnabled instead)
        private bool scrollUpEnabled = false;
        private LegionButtonAction scrollUpActionType = LegionButtonAction.XboxGuide;
        private string scrollUpShortcutKeys = "";
        private string scrollUpCommandPath = "";
        private bool scrollDownEnabled = false;
        private LegionButtonAction scrollDownActionType = LegionButtonAction.XboxGuide;
        private string scrollDownShortcutKeys = "";
        private string scrollDownCommandPath = "";

        // Callbacks for actions
        private Action<string> onShortcutTriggered;
        private Action<string> onCommandTriggered;
        private Action onFocusGoTweaksTriggered;

        private SafeFileHandle hidHandle;
        private bool _hasWriteAccess = false;  // Track if we have write access for heartbeat
        private bool _highQualityGyroConfigured = false;
        private readonly object _hidLock = new object();  // Lock for HID operations to prevent race conditions
        private Thread monitorThread;
        private volatile bool isRunning = false;
        private volatile bool isDisposed = false;

        // Scroll wheel click state tracking
        private bool lastScrollClickState = false;
        private DateTime lastScrollClickActionTime = DateTime.MinValue;
        private DateTime scrollClickPressTime = DateTime.MinValue; // Track when scroll click was pressed for minimum hold time
        private const int SCROLL_CLICK_COOLDOWN_MS = 400; // Minimum time between scroll click actions for Game Bar toggle
        private const int SCROLL_CLICK_MIN_HOLD_MS = 150; // Minimum time to hold Xbox Guide button for Game Bar to register
        private const int LEGION_BUTTON_PRESS_DEBOUNCE_MS = 8; // Keep press responsive
        private const int LEGION_BUTTON_RELEASE_DEBOUNCE_MS = 35; // Filter release chatter while held

        // Scroll wheel Raw Input monitor thread
        // Uses Raw Input API to capture mouse events from Legion Go mi_01/col02 interface
        private Thread scrollWheelThread;
        private volatile bool scrollWheelRunning;

        // Track button states for both L and R
        private bool lastLegionLState = false;
        private bool lastLegionRState = false;
        private bool? pendingLegionLState = null;
        private bool? pendingLegionRState = null;
        private DateTime pendingLegionLStateSince = DateTime.MinValue;
        private DateTime pendingLegionRStateSince = DateTime.MinValue;

        private Action<bool> onButtonStateChanged;

        // Battery monitoring - parsed from the same HID reports
        private int _lastLeftBattery = -1;
        private int _lastRightBattery = -1;
        private bool _lastLeftCharging = false;
        private bool _lastRightCharging = false;
        private bool _lastLeftConnected = false;
        private bool _lastRightConnected = false;
        private byte _lastLeftChargingByte = 0;
        private byte _lastRightChargingByte = 0;
        private DateTime _lastBatteryUpdateTime = DateTime.MinValue;
        private const int BATTERY_UPDATE_THROTTLE_MS = 2000;  // Only update battery every 2 seconds
        // Light/device status (b0:01, header 04:00:B0) read via this monitor's HID handle so the
        // Info card reflects true hardware even when LegionControllerService doesn't own the device.
        private long _statusReqLastTicks;
        private static readonly long StatusReqIntervalTicks = TimeSpan.TicksPerSecond * 2;
        /// <summary>Raised when a b0:01 device-status report (header 04:00:B0) is parsed on this
        /// monitor's handle. Carries the full LegionGoStatus so LegionManager can drive the
        /// Controller Info card even when LegionControllerService doesn't own the device.</summary>
        public event EventHandler<Devices.Libraries.Legion.LegionGoStatus> DeviceStatusUpdated;

        // Track when output reports are sent to skip button detection (prevents false triggers)
        private DateTime _lastOutputReportTime = DateTime.MinValue;
        private const int OUTPUT_REPORT_IGNORE_MS = 100;  // Ignore button reads for 100ms after output
        private const int GUIDE_ROUTE_RECONCILE_INTERVAL_MS = 500;

        // Detected device info
        private ushort _detectedVid = 0;
        private ushort _detectedPid = 0;

        // Flag to prevent spamming "controller not found" log (only log once until found)
        private bool _loggedControllerNotFound = false;

        // Consecutive full scans where Legion-VID/PID HID devices were present but
        // EVERY one was rejected by ProbeDeviceFormat. Two very different causes:
        //   • Protocol-incompatible hardware (Legion Go S, issue #90) — permanent.
        //   • Transient no-stream states on Go 1/Go 2 — controllers powered off
        //     while docked, detached, or mid mode-switch. Recovers when the user
        //     powers them back on.
        // Both waste ~1s of probe I/O per candidate per scan, so after a few
        // all-rejected scans the reconnect loop backs off to 60s full probes.
        // During backoff a cheap path-set check (string enumeration, no device
        // opens) runs every 10s and aborts the backoff the moment the HID
        // device set changes, so controller power-on is picked up in ≤10s when
        // it surfaces new interfaces and ≤60s worst case when it doesn't.
        // Reset whenever a scan finds zero candidates (devices gone — normal
        // disconnect keeps the fast retry) or a device is accepted.
        private int _consecutiveAllRejectedScans = 0;
        private bool _loggedIncompatibleBackoff = false;
        private const int AllRejectedScansBeforeBackoff = 3;
        internal const int IncompatibleRescanDelayMs = 60 * 1000;      // 60s full-probe cadence
        private const int BackoffPathSetCheckIntervalMs = 10 * 1000;   // cheap set check cadence
        private DateTime _lastGuideRouteReconcileTimeUtc = DateTime.MinValue;

        // Cached device path for faster reconnection (persisted to settings)
        private static string _cachedDevicePath = null;
        private static readonly object _cacheLock = new object();

        /// <summary>
        /// Get or set the cached HID device path for faster startup.
        /// Call LoadCachedDevicePath() at startup and SaveCachedDevicePath() when a device is found.
        /// </summary>
        public static string CachedDevicePath
        {
            get { lock (_cacheLock) return _cachedDevicePath; }
            set
            {
                lock (_cacheLock)
                {
                    _cachedDevicePath = value;
                    SaveCachedDevicePathToSettings(value);
                }
            }
        }

        private const string CachedDevicePathSettingsKey = "LegionHIDDevicePath";

        /// <summary>
        /// Load the cached device path from LocalSettings at startup.
        /// Call this once when the helper starts.
        /// </summary>
        public static void LoadCachedDevicePathFromSettings()
        {
            try
            {
                var settings = ApplicationData.Current.LocalSettings;
                if (settings.Values.ContainsKey(CachedDevicePathSettingsKey))
                {
                    string path = settings.Values[CachedDevicePathSettingsKey] as string;
                    if (!string.IsNullOrEmpty(path))
                    {
                        lock (_cacheLock)
                        {
                            _cachedDevicePath = path;
                        }
                        Logger.Info($"LegionButtonMonitor: Loaded cached device path from settings");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"LegionButtonMonitor: Failed to load cached device path: {ex.Message}");
            }
        }

        private static void SaveCachedDevicePathToSettings(string path)
        {
            try
            {
                var settings = ApplicationData.Current.LocalSettings;
                if (string.IsNullOrEmpty(path))
                {
                    settings.Values.Remove(CachedDevicePathSettingsKey);
                }
                else
                {
                    settings.Values[CachedDevicePathSettingsKey] = path;
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"LegionButtonMonitor: Failed to save cached device path: {ex.Message}");
            }
        }

        // Static timestamp for external output reports (e.g., from LegionGoLibrary brightness commands)
        // This allows other code to notify us when they send HID output reports to the controller
        private static DateTime _lastExternalOutputReportTime = DateTime.MinValue;
        private static readonly object _externalOutputLock = new object();
        private static readonly object _gyroSampleLock = new object();
        private static LegionGyroSample _latestLeftGyroSample;
        private static LegionGyroSample _latestRightGyroSample;
        private static LegionTouchpadSample _latestRightTouchpadSample;
        private static LegionGamepadSample _latestGamepadSample;
        private static bool _hasLeftGyroSample = false;
        private static bool _hasRightGyroSample = false;
        private static bool _hasRightTouchpadSample = false;
        private static bool _hasGamepadSample = false;

        // Press-edge detection. Shared event stream consumed by reactive lighting and the
        // GoTweaks haptics feature. Raised on the HID poll thread immediately after each
        // gamepad sample is stored — keep handlers fast and non-blocking. Previous button
        // state is tracked so we emit one event per rising/falling edge, not per sample.
        public static event EventHandler<LegionButtonEdgeEventArgs> ButtonEdge;
        private static ushort _prevEdgeButtons;
        private static ushort _prevEdgeAux;
        private static bool _prevEdgeLeftTrigger;
        private static bool _prevEdgeRightTrigger;
        private static bool _hasPrevEdgeState;
        // Analog trigger digital threshold for edge purposes (matches XInput's ~30/255 deadzone feel).
        private const byte EdgeTriggerThreshold = 64;

        /// <summary>
        /// Notify that an external HID output report was sent to the Legion controller.
        /// This prevents false button triggers when other code (e.g., LegionGoLibrary) sends commands.
        /// Call this immediately before sending any HID output report to the Legion controller.
        /// </summary>
        public static void NotifyOutputReportSent()
        {
            lock (_externalOutputLock)
            {
                _lastExternalOutputReportTime = DateTime.Now;
            }
        }

        /// <summary>
        /// Returns the latest parsed controller gyro sample from HID input reports, with the
        /// stored software bias offset subtracted from each axis (Steam-style one-shot
        /// calibration; see <see cref="SetGyroBias"/>). Every downstream consumer (legacy CE
        /// stick-gyro, VIIPER stick-gyro, VIIPER native DS4 / DualSense / Xbox forwarding)
        /// gets the bias-corrected sample because they all go through this method. To get the
        /// raw uncorrected sample (e.g. the bias-capture routine), use
        /// <see cref="TryGetLatestRawGyroSample"/> instead.
        /// </summary>
        public static bool TryGetLatestGyroSample(bool useLeftController, out LegionGyroSample sample)
        {
            LegionGyroSample raw;
            bool ok;
            lock (_gyroSampleLock)
            {
                if (useLeftController)
                {
                    raw = _latestLeftGyroSample;
                    ok = _hasLeftGyroSample;
                }
                else
                {
                    raw = _latestRightGyroSample;
                    ok = _hasRightGyroSample;
                }
            }
            if (!ok) { sample = raw; return false; }
            if (!_hasGyroBias) { sample = raw; return true; }
            sample = new LegionGyroSample(
                raw.GyroXDegPerSecond - _gyroBiasX,
                raw.GyroYDegPerSecond - _gyroBiasY,
                raw.GyroZDegPerSecond - _gyroBiasZ,
                raw.AccelXG, raw.AccelYG, raw.AccelZG,
                raw.TimestampTicksUtc);
            return true;
        }

        /// <summary>
        /// Returns the latest parsed gyro sample without the software bias subtraction applied.
        /// Only the bias-capture path should call this — gameplay paths must go through
        /// <see cref="TryGetLatestGyroSample"/> so they see corrected values.
        /// </summary>
        public static bool TryGetLatestRawGyroSample(bool useLeftController, out LegionGyroSample sample)
        {
            lock (_gyroSampleLock)
            {
                if (useLeftController)
                {
                    sample = _latestLeftGyroSample;
                    return _hasLeftGyroSample;
                }
                sample = _latestRightGyroSample;
                return _hasRightGyroSample;
            }
        }

        // Software gyro bias state — captured one-shot from the bias-capture pipe handler,
        // persisted to LocalSettings, subtracted from every TryGetLatestGyroSample read.
        // Steam-style: the user certifies "I am holding it still right now" and we just
        // average the current gyro reading and store the negative of that as the offset.
        private static float _gyroBiasX;
        private static float _gyroBiasY;
        private static float _gyroBiasZ;
        private static long _gyroBiasCalibratedAtUtc;
        private static volatile bool _hasGyroBias;

        public static bool TryGetGyroBias(out float x, out float y, out float z, out long calibratedAtUtc)
        {
            x = _gyroBiasX; y = _gyroBiasY; z = _gyroBiasZ;
            calibratedAtUtc = _gyroBiasCalibratedAtUtc;
            return _hasGyroBias;
        }

        public static void SetGyroBias(float x, float y, float z, long calibratedAtUtc)
        {
            _gyroBiasX = x;
            _gyroBiasY = y;
            _gyroBiasZ = z;
            _gyroBiasCalibratedAtUtc = calibratedAtUtc;
            _hasGyroBias = true;
            try
            {
                Settings.LocalSettingsHelper.SetValue("GyroBiasX", (double)x);
                Settings.LocalSettingsHelper.SetValue("GyroBiasY", (double)y);
                Settings.LocalSettingsHelper.SetValue("GyroBiasZ", (double)z);
                // Persist ticks as string. As a JSON number (or UWP boxed long round-tripped
                // through the file-fallback), a large int64 can come back in scientific
                // notation (e.g. "6.39e+17") and JsonSerializer.Deserialize<long> rejects that.
                Settings.LocalSettingsHelper.SetValue("GyroBiasCalibratedAtUtc", calibratedAtUtc.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Logger.Info($"Stored gyro bias offset: X={x:F3} Y={y:F3} Z={z:F3} deg/s");
            }
            catch (Exception ex) { Logger.Warn($"Persist gyro bias failed: {ex.Message}"); }
        }

        public static void ClearGyroBias()
        {
            _gyroBiasX = 0;
            _gyroBiasY = 0;
            _gyroBiasZ = 0;
            _gyroBiasCalibratedAtUtc = 0;
            _hasGyroBias = false;
            try
            {
                Settings.LocalSettingsHelper.Remove("GyroBiasX");
                Settings.LocalSettingsHelper.Remove("GyroBiasY");
                Settings.LocalSettingsHelper.Remove("GyroBiasZ");
                Settings.LocalSettingsHelper.Remove("GyroBiasCalibratedAtUtc");
                Logger.Info("Cleared gyro bias offset.");
            }
            catch (Exception ex) { Logger.Warn($"Clear gyro bias failed: {ex.Message}"); }
        }

        public static void LoadGyroBiasFromSettings()
        {
            try
            {
                bool gotX = Settings.LocalSettingsHelper.TryGetValue<double>("GyroBiasX", out double bx);
                bool gotY = Settings.LocalSettingsHelper.TryGetValue<double>("GyroBiasY", out double by);
                bool gotZ = Settings.LocalSettingsHelper.TryGetValue<double>("GyroBiasZ", out double bz);
                long at = 0;
                bool gotAt = false;
                if (Settings.LocalSettingsHelper.TryGetValue<string>("GyroBiasCalibratedAtUtc", out string atStr) &&
                    long.TryParse(atStr, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out at))
                {
                    gotAt = true;
                }
                else if (Settings.LocalSettingsHelper.TryGetValue<long>("GyroBiasCalibratedAtUtc", out long atLong))
                {
                    at = atLong; gotAt = true;
                }
                else if (Settings.LocalSettingsHelper.TryGetValue<double>("GyroBiasCalibratedAtUtc", out double atDbl) && atDbl > 0)
                {
                    at = (long)atDbl; gotAt = true;
                }
                Logger.Info($"LoadGyroBiasFromSettings: gotX={gotX} gotY={gotY} gotZ={gotZ} gotAt={gotAt} at={at}");
                if (gotX && gotY && gotZ && gotAt && at > 0)
                {
                    _gyroBiasX = (float)bx;
                    _gyroBiasY = (float)by;
                    _gyroBiasZ = (float)bz;
                    _gyroBiasCalibratedAtUtc = at;
                    _hasGyroBias = true;
                    Logger.Info($"Loaded persisted gyro bias offset: X={_gyroBiasX:F3} Y={_gyroBiasY:F3} Z={_gyroBiasZ:F3} deg/s (calibrated {new DateTime(at, DateTimeKind.Utc):s}Z)");
                }
            }
            catch (Exception ex) { Logger.Warn($"Load gyro bias from settings failed: {ex.Message}"); }
        }

        /// <summary>
        /// Returns the latest parsed right touchpad sample from Legion controller HID input reports.
        /// </summary>
        public static bool TryGetLatestRightTouchpadSample(out LegionTouchpadSample sample)
        {
            lock (_gyroSampleLock)
            {
                sample = _latestRightTouchpadSample;
                return _hasRightTouchpadSample;
            }
        }

        /// <summary>
        /// Returns the latest parsed gamepad sample from Legion controller HID input reports.
        /// Sample is mapped into XInput-compatible button/trigger/stick fields.
        /// </summary>
        public static bool TryGetLatestGamepadSample(out LegionGamepadSample sample)
        {
            lock (_gyroSampleLock)
            {
                sample = _latestGamepadSample;
                return _hasGamepadSample;
            }
        }

        private static bool IsSupportedLegionControllerVidPid(ushort vid, ushort pid)
        {
            return IsLegionTabletControllerVidPid(vid, pid) || IsLegionGoSControllerVidPid(vid, pid);
        }

        private static bool IsLegionTabletControllerVidPid(ushort vid, ushort pid)
        {
            return vid == LEGION_TABLET_VID && LEGION_TABLET_PIDS.Contains(pid);
        }

        private static bool IsLegionGo2TabletControllerVidPid(ushort vid, ushort pid)
        {
            return vid == LEGION_TABLET_VID && LEGION_GO2_TABLET_PIDS.Contains(pid);
        }

        private static bool IsLegionGoSControllerVidPid(ushort vid, ushort pid)
        {
            return vid == LEGION_GOS_VID && LEGION_GOS_PIDS.Contains(pid);
        }

        private static bool DeviceNameMatchesVidPid(string deviceNameLower, ushort vid, ushort[] pids)
        {
            if (string.IsNullOrWhiteSpace(deviceNameLower))
            {
                return false;
            }

            if (!deviceNameLower.Contains($"vid_{vid:X4}".ToLowerInvariant()))
            {
                return false;
            }

            for (int i = 0; i < pids.Length; i++)
            {
                if (deviceNameLower.Contains($"pid_{pids[i]:X4}".ToLowerInvariant()))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Cheap signature of the currently-present Legion HID interface set:
        /// enumerates interface paths (SetupDi walk, string matching only — no
        /// CreateFile, no report probing) and concatenates the sorted paths
        /// whose VID/PID substring matches a supported Legion controller. Used
        /// by the all-rejected backoff to notice controller power-on/attach
        /// within seconds without paying full ProbeDeviceFormat costs: a new
        /// interface appearing (or one vanishing) changes the signature.
        /// </summary>
        private static string ComputeLegionHidPathSetSignature()
        {
            try
            {
                HidD_GetHidGuid(out Guid hidGuid);
                IntPtr deviceInfoSet = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
                if (deviceInfoSet == IntPtr.Zero || deviceInfoSet == new IntPtr(-1))
                {
                    return string.Empty;
                }
                try
                {
                    var paths = new List<string>();
                    var interfaceData = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                    uint memberIndex = 0;
                    while (SetupDiEnumDeviceInterfaces(deviceInfoSet, IntPtr.Zero, ref hidGuid, memberIndex, ref interfaceData))
                    {
                        SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref interfaceData, IntPtr.Zero, 0, out uint requiredSize, IntPtr.Zero);
                        IntPtr detailDataBuffer = Marshal.AllocHGlobal((int)requiredSize);
                        try
                        {
                            Marshal.WriteInt32(detailDataBuffer, IntPtr.Size == 8 ? 8 : 6);
                            if (SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref interfaceData, detailDataBuffer, requiredSize, out _, IntPtr.Zero))
                            {
                                string devicePath = Marshal.PtrToStringAuto(detailDataBuffer + 4);
                                string lower = devicePath?.ToLowerInvariant();
                                if (DeviceNameMatchesVidPid(lower, LEGION_TABLET_VID, LEGION_TABLET_PIDS) ||
                                    DeviceNameMatchesVidPid(lower, LEGION_GOS_VID, LEGION_GOS_PIDS))
                                {
                                    paths.Add(lower);
                                }
                            }
                        }
                        finally
                        {
                            Marshal.FreeHGlobal(detailDataBuffer);
                        }
                        memberIndex++;
                    }
                    paths.Sort(StringComparer.Ordinal);
                    return string.Join("|", paths);
                }
                finally
                {
                    SetupDiDestroyDeviceInfoList(deviceInfoSet);
                }
            }
            catch
            {
                return string.Empty;
            }
        }

        private bool IsGoSControllerDevice()
        {
            return IsLegionGoSControllerVidPid(_detectedVid, _detectedPid);
        }

        private bool IsTabletControllerDevice()
        {
            return IsLegionTabletControllerVidPid(_detectedVid, _detectedPid);
        }

        private bool IsGo2TabletControllerDevice()
        {
            return IsLegionGo2TabletControllerVidPid(_detectedVid, _detectedPid);
        }

        /// <summary>
        /// Event raised when controller battery status is updated.
        /// Battery data is parsed from the same HID reports used for button monitoring.
        /// </summary>
        public event EventHandler<LegionButtonBatteryEventArgs> BatteryUpdated;

        // P/Invoke declarations
        [DllImport("hid.dll", SetLastError = true)]
        private static extern void HidD_GetHidGuid(out Guid hidGuid);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(
            ref Guid classGuid,
            IntPtr enumerator,
            IntPtr hwndParent,
            uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(
            IntPtr deviceInfoSet,
            IntPtr deviceInfoData,
            ref Guid interfaceClassGuid,
            uint memberIndex,
            ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(
            IntPtr deviceInfoSet,
            ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData,
            IntPtr deviceInterfaceDetailData,
            uint deviceInterfaceDetailDataSize,
            out uint requiredSize,
            IntPtr deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern SafeFileHandle CreateFile(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadFile(
            SafeFileHandle hFile,
            byte[] lpBuffer,
            uint nNumberOfBytesToRead,
            out uint lpNumberOfBytesRead,
            IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadFile(
            SafeFileHandle hFile,
            byte[] lpBuffer,
            uint nNumberOfBytesToRead,
            out uint lpNumberOfBytesRead,
            ref NativeOverlapped lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetOverlappedResult(
            SafeFileHandle hFile,
            ref NativeOverlapped lpOverlapped,
            out uint lpNumberOfBytesTransferred,
            bool bWait);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateEvent(IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CancelIo(SafeFileHandle hFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CancelIoEx(SafeFileHandle hFile, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ResetEvent(IntPtr hEvent);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_GetAttributes(SafeFileHandle hidDeviceObject, ref HIDD_ATTRIBUTES attributes);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteFile(
            SafeFileHandle hFile,
            byte[] lpBuffer,
            uint nNumberOfBytesToWrite,
            out uint lpNumberOfBytesWritten,
            IntPtr lpOverlapped);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_SetOutputReport(
            SafeFileHandle hidDeviceObject,
            byte[] lpReportBuffer,
            uint reportBufferLength);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_GetPreparsedData(SafeFileHandle hidDeviceObject, out IntPtr preparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern int HidP_GetCaps(IntPtr preparsedData, out HIDP_CAPS capabilities);

        [StructLayout(LayoutKind.Sequential)]
        private struct HIDP_CAPS
        {
            public ushort Usage;
            public ushort UsagePage;
            public ushort InputReportByteLength;
            public ushort OutputReportByteLength;
            public ushort FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
            public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes;
            public ushort NumberInputButtonCaps;
            public ushort NumberInputValueCaps;
            public ushort NumberInputDataIndices;
            public ushort NumberOutputButtonCaps;
            public ushort NumberOutputValueCaps;
            public ushort NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps;
            public ushort NumberFeatureValueCaps;
            public ushort NumberFeatureDataIndices;
        }

        private const int HIDP_STATUS_SUCCESS = 0x00110000;

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HIDD_ATTRIBUTES
        {
            public int Size;
            public ushort VendorID;
            public ushort ProductID;
            public ushort VersionNumber;
        }

        private const uint DIGCF_PRESENT = 0x02;
        private const uint DIGCF_DEVICEINTERFACE = 0x10;
        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ = 0x01;
        private const uint FILE_SHARE_WRITE = 0x02;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_FLAG_OVERLAPPED = 0x40000000;
        private const uint WAIT_OBJECT_0 = 0x00000000;
        private const uint WAIT_TIMEOUT = 0x00000102;
        private const int ERROR_IO_PENDING = 997;

        // Raw Input API constants and structures for scroll wheel monitoring
        private const uint RIDEV_INPUTSINK = 0x00000100;
        private const uint RIM_TYPEMOUSE = 0;
        private const uint WM_INPUT = 0x00FF;
        private const uint RID_INPUT = 0x10000003;
        private const uint RIDI_DEVICENAME = 0x20000007;
        private const uint PM_REMOVE = 1;
        private const int HWND_MESSAGE = -3;

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUTDEVICE
        {
            public ushort usUsagePage;
            public ushort usUsage;
            public uint dwFlags;
            public IntPtr hwndTarget;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUTHEADER
        {
            public uint dwType;
            public uint dwSize;
            public IntPtr hDevice;
            public IntPtr wParam;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWMOUSE
        {
            public ushort usFlags;
            public ushort usButtonFlags;
            public ushort usButtonData;
            public uint ulRawButtons;
            public int lLastX;
            public int lLastY;
            public uint ulExtraInformation;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUT_MOUSE
        {
            public RAWINPUTHEADER header;
            public RAWMOUSE mouse;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int pt_x;
            public int pt_y;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASS
        {
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string lpszMenuName;
            public string lpszClassName;
        }

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);
        private static WndProcDelegate _scrollWndProcDelegate; // prevent GC

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, int cbSize);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern uint GetRawInputDeviceInfoW(IntPtr hDevice, uint uiCommand, IntPtr pData, ref uint pcbSize);

        [DllImport("user32.dll")]
        private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref MSG lpMsg);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern ushort RegisterClassW(ref WNDCLASS lpWndClass);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowExW(uint dwExStyle, string lpClassName, string lpWindowName,
            uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent,
            IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        // Scroll wheel Raw Input window handle
        private IntPtr scrollWheelWindowHandle = IntPtr.Zero;

        // Diagnostic tracking for Raw Input thread
        private DateTime lastScrollHeartbeat = DateTime.MinValue;
        private DateTime lastScrollInputReceived = DateTime.MinValue;
        private int scrollInputCount = 0;
        private bool hasReceivedScrollInput = false; // Track if we've ever received input
        private const int HEARTBEAT_INTERVAL_MS = 30000; // Log heartbeat every 30 seconds
        private const int NO_INPUT_REREGISTER_MS = 15000; // Re-register Raw Input if no input for 15 seconds (after previously receiving input)

        /// <summary>
        /// Initialize the unified monitor for both Legion L and R buttons.
        /// </summary>
        public LegionButtonMonitor(Action<bool> onStateChanged = null)
        {
            onButtonStateChanged = onStateChanged;
        }

        /// <summary>
        /// Configure a Legion button's action. Can be called multiple times to configure both L and R.
        /// </summary>
        /// <param name="button">"L" for Legion L, "R" for Legion R</param>
        /// <param name="enabled">Whether to enable the remap for this button</param>
        /// <param name="action">0 = Xbox Guide, 1 = Keyboard Shortcut, 2 = Run Command, 3 = Focus GoTweaks</param>
        /// <param name="shortcutOrCommand">Keyboard shortcut string (e.g., "Win+G") or command path</param>
        /// <param name="shortcutCallback">Callback to execute keyboard shortcut</param>
        /// <param name="commandCallback">Callback to execute command (optional)</param>
        /// <param name="focusGoTweaksCallback">Callback to focus GoTweaks widget (optional)</param>
        public void ConfigureButton(string button, bool enabled, int action, string shortcutOrCommand,
            Action<string> shortcutCallback, Action<string> commandCallback = null, Action focusGoTweaksCallback = null)
        {
            // Store callbacks (shared between both buttons)
            onShortcutTriggered = shortcutCallback;
            onCommandTriggered = commandCallback;
            onFocusGoTweaksTriggered = focusGoTweaksCallback;

            bool isLegionL = button == "L";
            var actionType = (LegionButtonAction)action;
            string shortcutKeys = actionType == LegionButtonAction.RunCommand ? "" : (shortcutOrCommand ?? "");
            string commandPath = actionType == LegionButtonAction.RunCommand ? (shortcutOrCommand ?? "") : "";

            if (isLegionL)
            {
                legionLEnabled = enabled;
                legionLActionType = actionType;
                legionLShortcutKeys = shortcutKeys;
                legionLCommandPath = commandPath;
            }
            else
            {
                legionREnabled = enabled;
                legionRActionType = actionType;
                legionRShortcutKeys = shortcutKeys;
                legionRCommandPath = commandPath;
            }

            string buttonName = isLegionL ? "Legion L" : "Legion R";
            string actionName = actionType == LegionButtonAction.XboxGuide ? "Xbox Guide" :
                               actionType == LegionButtonAction.KeyboardShortcut ? $"Shortcut: {shortcutKeys}" :
                               actionType == LegionButtonAction.RunCommand ? $"Command: {commandPath}" :
                               "Focus GoTweaks";
            Logger.Info($"LegionButtonMonitor: Configured {buttonName} - Enabled: {enabled}, Action: {actionName}");

            // Notify so VIIPER can spin up / tear down its guide-only pad when the
            // mapped Guide action changes (sole owner of the Guide route since the
            // ViGEm retirement).
            try { Program.NotifyGuideRouteChanged(); }
            catch (Exception ex) { Logger.Debug($"ConfigureButton: NotifyGuideRouteChanged threw: {ex.Message}"); }
        }

        /// <summary>
        /// Configure a scroll wheel action (Up, Down, or Click).
        /// </summary>
        /// <param name="direction">"Up", "Down", or "Click"</param>
        /// <param name="enabled">Whether to enable the remap for this action</param>
        /// <param name="action">0 = Xbox Guide, 1 = Keyboard Shortcut, 2 = Run Command, 3 = Focus GoTweaks</param>
        /// <param name="shortcutOrCommand">Keyboard shortcut string or command path</param>
        /// <param name="shortcutCallback">Callback to execute keyboard shortcut</param>
        /// <param name="commandCallback">Callback to execute command (optional)</param>
        /// <param name="focusGoTweaksCallback">Callback to focus GoTweaks widget (optional)</param>
        public void ConfigureScrollWheel(string direction, bool enabled, int action, string shortcutOrCommand,
            Action<string> shortcutCallback, Action<string> commandCallback = null, Action focusGoTweaksCallback = null)
        {
            // Store callbacks (shared between all actions)
            onShortcutTriggered = shortcutCallback;
            onCommandTriggered = commandCallback;
            onFocusGoTweaksTriggered = focusGoTweaksCallback;

            var actionType = (LegionButtonAction)action;
            string shortcutKeys = actionType == LegionButtonAction.RunCommand ? "" : (shortcutOrCommand ?? "");
            string commandPath = actionType == LegionButtonAction.RunCommand ? (shortcutOrCommand ?? "") : "";

            switch (direction.ToLower())
            {
                case "scroll":
                    // Unified scroll action (direction not available via Raw Input API)
                    scrollEnabled = enabled;
                    scrollActionType = actionType;
                    scrollShortcutKeys = shortcutKeys;
                    scrollCommandPath = commandPath;
                    break;
                case "up":
                    // Legacy - now handled by unified "scroll"
                    scrollUpEnabled = enabled;
                    scrollUpActionType = actionType;
                    scrollUpShortcutKeys = shortcutKeys;
                    scrollUpCommandPath = commandPath;
                    break;
                case "down":
                    // Legacy - now handled by unified "scroll"
                    scrollDownEnabled = enabled;
                    scrollDownActionType = actionType;
                    scrollDownShortcutKeys = shortcutKeys;
                    scrollDownCommandPath = commandPath;
                    break;
                case "click":
                    scrollClickEnabled = enabled;
                    scrollClickActionType = actionType;
                    scrollClickShortcutKeys = shortcutKeys;
                    scrollClickCommandPath = commandPath;
                    break;
                default:
                    Logger.Warn($"LegionButtonMonitor: Unknown scroll direction '{direction}'");
                    return;
            }

            string actionName = actionType == LegionButtonAction.XboxGuide ? "Xbox Guide" :
                               actionType == LegionButtonAction.KeyboardShortcut ? $"Shortcut: {shortcutKeys}" :
                               actionType == LegionButtonAction.RunCommand ? $"Command: {commandPath}" :
                               "Focus GoTweaks";
            Logger.Info($"LegionButtonMonitor: Configured Scroll {direction} - Enabled: {enabled}, Action: {actionName}");

            // Same notify pattern as ConfigureButton — give VIIPER a chance to
            // (de)activate its guide-only pad before Labs decides about ViGEm.
            try { Program.NotifyGuideRouteChanged(); }
            catch (Exception ex) { Logger.Debug($"ConfigureScrollWheel: NotifyGuideRouteChanged threw: {ex.Message}"); }

            // If monitor is already running and scroll is now configured, start the scroll wheel thread
            // This handles the case where scroll is configured after the monitor is already running for buttons/battery
            if (isRunning && HasAnyScrollConfigured && (scrollWheelThread == null || !scrollWheelThread.IsAlive))
            {
                scrollWheelRunning = true;
                scrollWheelThread = new Thread(ScrollWheelThreadProc)
                {
                    IsBackground = true,
                    Name = "LegionScrollWheel"
                };
                scrollWheelThread.Start();
                Logger.Info("LegionButtonMonitor: Scroll wheel Raw Input monitor thread started (hot-configured)");
            }
        }

        /// <summary>
        /// ViGEm retirement (phase 2): the dedicated Guide-only ViGEm pad is gone.
        /// The Guide route is now always served by VIIPER — either the full input
        /// forwarder or ViiperEmulationManager's guide-only pad, which since phase 2
        /// starts regardless of the backend selector whenever a Guide action is
        /// configured and no full pad owns the route. These methods remain as
        /// no-op shims because Program.NotifyGuideRouteChanged still calls them;
        /// VIIPER's own OnGuideRouteChanged does the real reconciliation.
        /// </summary>
        public void ForceReconcileGuideRoute()
        {
            // Intentionally empty — see summary.
        }

        private void ReconcileGuideRoute()
        {
            // Intentionally empty — see summary.
        }

        /// <summary>
        /// Get whether any user-mapped action is configured to fire the Xbox Guide button.
        /// Exposed so ViiperEmulationManager can decide whether to spin up its Guide-only
        /// virtual pad when the master Controller-Emulation toggle is off but backend=VIIPER.
        /// </summary>
        public bool HasGuideActionConfigured =>
            (legionLEnabled && legionLActionType == LegionButtonAction.XboxGuide) ||
            (legionREnabled && legionRActionType == LegionButtonAction.XboxGuide) ||
            (scrollUpEnabled && scrollUpActionType == LegionButtonAction.XboxGuide) ||
            (scrollDownEnabled && scrollDownActionType == LegionButtonAction.XboxGuide) ||
            (scrollClickEnabled && scrollClickActionType == LegionButtonAction.XboxGuide);

        /// <summary>
        /// ViGEm retirement (phase 2): constant false. The dedicated Guide-only
        /// ViGEm pad no longer exists — VIIPER guide-only serves the route on
        /// every backend. Kept so Program.Labs' "restart monitor when the pad
        /// requirement changes" logic stays inert without a rewrite; remove
        /// together with those call sites in the final ViGEm cleanup.
        /// </summary>
        public bool NeedsViGEm => false;

        /// <summary>
        /// Get whether any button is configured.
        /// </summary>
        public bool HasAnyButtonConfigured => legionLEnabled || legionREnabled;

        /// <summary>
        /// Get whether any scroll wheel action is configured.
        /// </summary>
        public bool HasAnyScrollConfigured => scrollEnabled || scrollClickEnabled || scrollUpEnabled || scrollDownEnabled;

        /// <summary>
        /// Get whether the monitor is currently running.
        /// </summary>
        public bool IsRunning => isRunning;

        /// <summary>
        /// Get the detected VID:PID as a formatted string (e.g., "17EF:6182").
        /// Returns empty string if no device detected.
        /// </summary>
        public string DetectedVidPid => _detectedVid != 0 ? $"{_detectedVid:X4}:{_detectedPid:X4}" : "";

        /// <summary>
        /// Get whether the controller is in detached/uninitialized mode.
        /// </summary>
        public bool IsDetachedMode => isDetachedMode;

        /// <summary>
        /// Check if ViGEm controller needs to be (re)initialized based on current configuration.
        /// Returns true if we need ViGEm but don't have a controller, or have one but don't need it.
        /// </summary>
        public bool NeedsViGEmRestart => false; // ViGEm retirement: pad no longer exists

        /// <summary>
        /// Start monitoring the configured Legion buttons (L and/or R).
        /// Guide-mapped actions are delivered through VIIPER (full forwarder or
        /// guide-only pad) — no dedicated ViGEm pad since the phase-2 retirement.
        /// </summary>
        public bool Start()
        {
            if (isRunning)
                return true;

            // Check if we have any button or scroll configured
            if (!HasAnyButtonConfigured && !HasAnyScrollConfigured)
            {
                Logger.Warn("LegionButtonMonitor: No buttons or scroll configured, not starting");
                return false;
            }

            // Try to find and open Legion controller HID device
            // Even if not found initially, start the monitor thread which will retry
            bool controllerFound = OpenLegionController();
            if (!controllerFound)
            {
                Logger.Warn("LegionButtonMonitor: Controller not found initially, will retry in background");
            }

            // Start monitoring thread - it will handle reconnection if controller not found
            isRunning = true;
            monitorThread = new Thread(MonitorLoop)
            {
                IsBackground = true,
                Name = "LegionButtonMonitor"
            };
            monitorThread.Start();

            // Start scroll wheel thread if any scroll action is configured
            // Uses Raw Input API to capture mouse events from Legion Go mi_01/col02 interface
            if (HasAnyScrollConfigured)
            {
                scrollWheelRunning = true;
                scrollWheelThread = new Thread(ScrollWheelThreadProc)
                {
                    IsBackground = true,
                    Name = "LegionScrollWheel"
                };
                scrollWheelThread.Start();
                Logger.Info("LegionButtonMonitor: Scroll wheel Raw Input monitor thread started");
            }

            string buttons = "";
            if (legionLEnabled) buttons += "L";
            if (legionREnabled) buttons += (buttons.Length > 0 ? " + R" : "R");
            if (controllerFound)
            {
                Logger.Info($"LegionButtonMonitor: Started monitoring Legion {buttons} button(s)");
            }
            else
            {
                Logger.Info($"LegionButtonMonitor: Started in background for Legion {buttons}, waiting for controller connection");
            }

            return true;
        }

        /// <summary>
        /// Stop monitoring.
        /// </summary>
        public void Stop()
        {
            if (!isRunning)
                return;

            isRunning = false;

            // Stop scroll wheel Raw Input thread
            scrollWheelRunning = false;
            if (scrollWheelThread != null && scrollWheelThread.IsAlive)
            {
                // Thread will exit its message loop and clean up its window
                scrollWheelThread.Join(2000);
                scrollWheelThread = null;
            }

            // Wait for main monitor thread to finish
            if (monitorThread != null && monitorThread.IsAlive)
            {
                monitorThread.Join(1000);
                monitorThread = null;
            }

            // Release Guide button if either was pressed (VIIPER guide-only pad
            // owns the route since the ViGEm retirement).
            if (lastLegionLState || lastLegionRState)
            {
                try { XboxGamingBarHelper.ControllerEmulation.Viiper.ViiperEmulationManager.TrySetGuideFromLabs(false); } catch { }
                lastLegionLState = false;
                lastLegionRState = false;
                pendingLegionLState = null;
                pendingLegionRState = null;
                pendingLegionLStateSince = DateTime.MinValue;
                pendingLegionRStateSince = DateTime.MinValue;
            }

            // Close HID handle
            if (hidHandle != null && !hidHandle.IsInvalid)
            {
                DisableHighQualityGyroReports(hidHandle);
                hidHandle.Close();
                hidHandle = null;
            }
            _hasWriteAccess = false;
            _highQualityGyroConfigured = false;

            Logger.Info("LegionButtonMonitor: Stopped");
        }

        /// <summary>
        /// Start the monitor for battery monitoring only (no button remapping).
        /// This allows battery data to be collected even when no buttons are configured.
        /// </summary>
        /// <returns>True if successfully started</returns>
        public bool StartForBatteryMonitoring()
        {
            if (isRunning)
                return true;

            // Try to find and open Legion controller HID device
            // Even if not found initially, start the monitor thread which will retry
            bool controllerFound = OpenLegionController();
            if (!controllerFound)
            {
                Logger.Warn("LegionButtonMonitor: Controller not found initially, will retry in background");
            }

            // Start monitoring thread - it will handle reconnection if controller not found
            isRunning = true;
            monitorThread = new Thread(MonitorLoop)
            {
                IsBackground = true,
                Name = "LegionButtonMonitor"
            };
            monitorThread.Start();

            if (controllerFound)
            {
                Logger.Info("LegionButtonMonitor: Started for battery monitoring only");
            }
            else
            {
                Logger.Info("LegionButtonMonitor: Started in background, waiting for controller connection");
            }
            return true;
        }

        private bool OpenLegionController()
        {
            try
            {
                // Try cached device path first for faster startup
                string cachedPath = CachedDevicePath;
                if (!string.IsNullOrEmpty(cachedPath))
                {
                    Logger.Info($"LegionButtonMonitor: Trying cached device path first: {cachedPath}");
                    if (TryOpenDeviceAtPath(cachedPath, out SafeFileHandle cachedHandle, out bool cachedWriteAccess))
                    {
                        if (ProbeDeviceFormat(cachedHandle, cachedWriteAccess, _detectedVid, _detectedPid))
                        {
                            hidHandle = cachedHandle;
                            _hasWriteAccess = cachedWriteAccess;
                            _highQualityGyroConfigured = false;
                            Logger.Info($"LegionButtonMonitor: Cached device path worked! VID:{_detectedVid:X4} PID:{_detectedPid:X4}");
                            return true;
                        }
                        else
                        {
                            Logger.Info("LegionButtonMonitor: Cached device path no longer valid, scanning all devices");
                            cachedHandle.Close();
                            CachedDevicePath = null; // Clear invalid cache
                        }
                    }
                    else
                    {
                        Logger.Info("LegionButtonMonitor: Cached device path not accessible, scanning all devices");
                        CachedDevicePath = null; // Clear invalid cache
                    }
                }

                HidD_GetHidGuid(out Guid hidGuid);

                IntPtr deviceInfoSet = SetupDiGetClassDevs(
                    ref hidGuid,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);

                if (deviceInfoSet == IntPtr.Zero || deviceInfoSet == new IntPtr(-1))
                {
                    Logger.Error("LegionButtonMonitor: Failed to get device info set");
                    return false;
                }

                try
                {
                    var interfaceData = new SP_DEVICE_INTERFACE_DATA
                    {
                        cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>()
                    };

                    int candidateCount = 0;
                    uint memberIndex = 0;
                    while (SetupDiEnumDeviceInterfaces(deviceInfoSet, IntPtr.Zero, ref hidGuid, memberIndex, ref interfaceData))
                    {
                        // Get required buffer size
                        SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref interfaceData, IntPtr.Zero, 0, out uint requiredSize, IntPtr.Zero);

                        // Allocate buffer for device path
                        IntPtr detailDataBuffer = Marshal.AllocHGlobal((int)requiredSize);
                        try
                        {
                            // Set cbSize for SP_DEVICE_INTERFACE_DETAIL_DATA
                            Marshal.WriteInt32(detailDataBuffer, IntPtr.Size == 8 ? 8 : 6);

                            if (SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref interfaceData, detailDataBuffer, requiredSize, out _, IntPtr.Zero))
                            {
                                // Get device path (starts at offset 4)
                                string devicePath = Marshal.PtrToStringAuto(detailDataBuffer + 4);

                                // Try to open device with overlapped I/O for timeout support
                                // First try with read + write access (needed for initialization command)
                                // Fall back to read-only if write access is denied
                                var handle = CreateFile(
                                    devicePath,
                                    GENERIC_READ | GENERIC_WRITE,
                                    FILE_SHARE_READ | FILE_SHARE_WRITE,
                                    IntPtr.Zero,
                                    OPEN_EXISTING,
                                    FILE_FLAG_OVERLAPPED,
                                    IntPtr.Zero);

                                bool hasWriteAccess = !handle.IsInvalid;
                                if (handle.IsInvalid)
                                {
                                    // Fallback to read-only access (initialization won't work but we can still monitor)
                                    handle = CreateFile(
                                        devicePath,
                                        GENERIC_READ,
                                        FILE_SHARE_READ | FILE_SHARE_WRITE,
                                        IntPtr.Zero,
                                        OPEN_EXISTING,
                                        FILE_FLAG_OVERLAPPED,
                                        IntPtr.Zero);
                                }

                                if (!handle.IsInvalid)
                                {
                                    // Check VID/PID
                                    var attrs = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
                                    if (HidD_GetAttributes(handle, ref attrs))
                                    {
                                        if (IsSupportedLegionControllerVidPid(attrs.VendorID, attrs.ProductID))
                                        {
                                            candidateCount++;
                                            Logger.Info($"LegionButtonMonitor: Found candidate #{candidateCount} at {devicePath} (write access: {hasWriteAccess})");

                                            // Probe the device by reading a report to verify it's the correct format
                                            // The correct device sends 64-byte reports with 04:00:A1 header
                                            if (ProbeDeviceFormat(handle, hasWriteAccess, attrs.VendorID, attrs.ProductID))
                                            {
                                                hidHandle = handle;
                                                _hasWriteAccess = hasWriteAccess;
                                                _highQualityGyroConfigured = false;
                                                _detectedVid = attrs.VendorID;
                                                _detectedPid = attrs.ProductID;
                                                Logger.Info($"LegionButtonMonitor: Selected device #{candidateCount} - VID:{_detectedVid:X4} PID:{_detectedPid:X4} (write: {hasWriteAccess})");

                                                // Cache the successful device path for faster startup next time
                                                CachedDevicePath = devicePath;
                                                Logger.Info($"LegionButtonMonitor: Cached device path for future use");

                                                // Reset the "not found" log flag so we'll log again if disconnected
                                                _loggedControllerNotFound = false;
                                                _consecutiveAllRejectedScans = 0;
                                                _loggedIncompatibleBackoff = false;
                                                return true;
                                            }
                                            else
                                            {
                                                Logger.Info($"LegionButtonMonitor: Device #{candidateCount} rejected - wrong report format");
                                            }
                                        }
                                    }
                                    handle.Close();
                                }
                            }
                        }
                        finally
                        {
                            Marshal.FreeHGlobal(detailDataBuffer);
                        }

                        memberIndex++;
                    }

                    if (candidateCount > 0)
                    {
                        _consecutiveAllRejectedScans++;
                        if (_consecutiveAllRejectedScans <= AllRejectedScansBeforeBackoff)
                        {
                            Logger.Warn($"LegionButtonMonitor: Found {candidateCount} Legion HID devices but none had correct 64-byte format (scan {_consecutiveAllRejectedScans}/{AllRejectedScansBeforeBackoff} before backoff)");
                        }
                    }
                    else
                    {
                        // Zero candidates = devices genuinely absent (disconnect /
                        // driver reset), not a protocol mismatch — keep fast retry.
                        _consecutiveAllRejectedScans = 0;
                        _loggedIncompatibleBackoff = false;
                    }
                }
                finally
                {
                    SetupDiDestroyDeviceInfoList(deviceInfoSet);
                }

                // Only log once to prevent spam - reset when controller is found
                if (!_loggedControllerNotFound)
                {
                    Logger.Warn("LegionButtonMonitor: Legion controller not found");
                    _loggedControllerNotFound = true;
                }
                return false;
            }
            catch (Exception ex)
            {
                Logger.Error($"LegionButtonMonitor: Exception opening controller: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Try to open a specific HID device at the given path.
        /// </summary>
        private bool TryOpenDeviceAtPath(string devicePath, out SafeFileHandle handle, out bool hasWriteAccess)
        {
            handle = null;
            hasWriteAccess = false;

            try
            {
                // Try with read + write access first
                handle = CreateFile(
                    devicePath,
                    GENERIC_READ | GENERIC_WRITE,
                    FILE_SHARE_READ | FILE_SHARE_WRITE,
                    IntPtr.Zero,
                    OPEN_EXISTING,
                    FILE_FLAG_OVERLAPPED,
                    IntPtr.Zero);

                hasWriteAccess = !handle.IsInvalid;
                if (handle.IsInvalid)
                {
                    // Fallback to read-only
                    handle = CreateFile(
                        devicePath,
                        GENERIC_READ,
                        FILE_SHARE_READ | FILE_SHARE_WRITE,
                        IntPtr.Zero,
                        OPEN_EXISTING,
                        FILE_FLAG_OVERLAPPED,
                        IntPtr.Zero);
                }

                if (handle.IsInvalid)
                {
                    handle = null;
                    return false;
                }

                // Verify VID/PID
                var attrs = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
                if (HidD_GetAttributes(handle, ref attrs))
                {
                    if (IsSupportedLegionControllerVidPid(attrs.VendorID, attrs.ProductID))
                    {
                        _detectedVid = attrs.VendorID;
                        _detectedPid = attrs.ProductID;
                        return true;
                    }
                }

                handle.Close();
                handle = null;
                return false;
            }
            catch (Exception ex)
            {
                Logger.Debug($"LegionButtonMonitor: TryOpenDeviceAtPath exception: {ex.Message}");
                if (handle != null && !handle.IsInvalid)
                {
                    handle.Close();
                }
                handle = null;
                return false;
            }
        }

        /// <summary>
        /// Process a scroll action (up/down) - these are instant actions, not press/release.
        /// </summary>
        private void ProcessScrollAction(string actionName, LegionButtonAction actionType, string shortcutKeys, string commandPath)
        {
            Logger.Info($"LegionButtonMonitor: {actionName} triggered - action={actionType}");

            switch (actionType)
            {
                case LegionButtonAction.XboxGuide:
                    // VIIPER backend intercept for scroll-triggered Guide (Native → injects
                    // a short Guide press into the emulated wire state; GameBar → Win+G).
                    if (XboxGamingBarHelper.ControllerEmulation.Viiper.ViiperInputForwarder.TryHandleGuidePressFromLabs())
                    {
                        Logger.Info($"LegionButtonMonitor: Routed scroll XboxGuide to VIIPER backend ({actionName})");
                        break;
                    }
                    if (XboxGamingBarHelper.ControllerEmulation.Viiper.ViiperEmulationManager.TrySetGuideFromLabs(true))
                    {
                        Thread.Sleep(50);
                        XboxGamingBarHelper.ControllerEmulation.Viiper.ViiperEmulationManager.TrySetGuideFromLabs(false);
                        Logger.Info($"LegionButtonMonitor: Routed scroll XboxGuide to VIIPER guide-only pad ({actionName})");
                        break;
                    }
                    // No further fallback — the dedicated ViGEm pad was retired
                    // (phase 2). If neither VIIPER tier took the press, the
                    // guide-only pad is offline (usbip missing) and the setup
                    // banner is already telling the user what to install.
                    Logger.Warn($"LegionButtonMonitor: No virtual pad available for scroll XboxGuide ({actionName}) — is usbip-win2 installed?");
                    break;

                case LegionButtonAction.KeyboardShortcut:
                    if (!string.IsNullOrEmpty(shortcutKeys))
                    {
                        Logger.Info($"LegionButtonMonitor: Executing shortcut '{shortcutKeys}', callback={(onShortcutTriggered != null ? "set" : "NULL")}");
                        try
                        {
                            if (onShortcutTriggered != null)
                            {
                                onShortcutTriggered.Invoke(shortcutKeys);
                            }
                            else
                            {
                                Logger.Warn("LegionButtonMonitor: Shortcut callback is null!");
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Error($"LegionButtonMonitor: Scroll shortcut exception: {ex.Message}");
                        }
                    }
                    else
                    {
                        Logger.Warn($"LegionButtonMonitor: Shortcut keys is empty!");
                    }
                    break;

                case LegionButtonAction.RunCommand:
                    if (!string.IsNullOrEmpty(commandPath))
                    {
                        try
                        {
                            onCommandTriggered?.Invoke(commandPath);
                        }
                        catch (Exception ex)
                        {
                            Logger.Error($"LegionButtonMonitor: Scroll command exception: {ex.Message}");
                        }
                    }
                    break;

                case LegionButtonAction.FocusGoTweaks:
                    try
                    {
                        onFocusGoTweaksTriggered?.Invoke();
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"LegionButtonMonitor: Scroll FocusGoTweaks exception: {ex.Message}");
                    }
                    break;
            }
        }

        /// <summary>
        /// WndProc callback for the Raw Input message window.
        /// Simply passes messages to DefWindowProc.
        /// </summary>
        private static IntPtr ScrollWheelWndProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam)
        {
            return DefWindowProcW(hWnd, uMsg, wParam, lParam);
        }

        /// <summary>
        /// Creates a message-only window to receive Raw Input events.
        /// </summary>
        private IntPtr CreateScrollWheelRawInputWindow()
        {
            try
            {
                // Only assign delegate once to prevent GC issues when window class is reused
                // (window class retains the original function pointer even after re-registration)
                if (_scrollWndProcDelegate == null)
                {
                    _scrollWndProcDelegate = ScrollWheelWndProc;
                }
                IntPtr hInstance = GetModuleHandle(null);

                var wc = new WNDCLASS
                {
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_scrollWndProcDelegate),
                    hInstance = hInstance,
                    lpszClassName = "LegionScrollWheelRawInput"
                };

                ushort atom = RegisterClassW(ref wc);
                // 1410 = ERROR_CLASS_ALREADY_EXISTS which is OK
                if (atom == 0 && Marshal.GetLastWin32Error() != 1410)
                {
                    Logger.Error($"LegionButtonMonitor: Failed to register window class (error={Marshal.GetLastWin32Error()})");
                    return IntPtr.Zero;
                }

                // Create message-only window (HWND_MESSAGE = -3)
                IntPtr hwnd = CreateWindowExW(0, "LegionScrollWheelRawInput", "LegionScrollWheel",
                    0, 0, 0, 0, 0, new IntPtr(HWND_MESSAGE), IntPtr.Zero, hInstance, IntPtr.Zero);

                if (hwnd == IntPtr.Zero)
                {
                    Logger.Error($"LegionButtonMonitor: Failed to create window (error={Marshal.GetLastWin32Error()})");
                }

                return hwnd;
            }
            catch (Exception ex)
            {
                Logger.Error($"LegionButtonMonitor: CreateScrollWheelRawInputWindow exception: {ex.Message}");
                return IntPtr.Zero;
            }
        }

        /// <summary>
        /// Initializes Raw Input registration for mouse events (to capture scroll wheel).
        /// </summary>
        private bool InitializeScrollWheelRawInput(IntPtr hwnd)
        {
            try
            {
                // Register for mouse input (scroll wheel reports as mouse)
                var rid = new RAWINPUTDEVICE[1];
                rid[0].usUsagePage = 0x01;  // Generic Desktop
                rid[0].usUsage = 0x02;       // Mouse
                rid[0].dwFlags = RIDEV_INPUTSINK;  // Receive input even when not focused
                rid[0].hwndTarget = hwnd;

                if (!RegisterRawInputDevices(rid, 1, Marshal.SizeOf<RAWINPUTDEVICE>()))
                {
                    Logger.Error($"LegionButtonMonitor: Failed to register for Raw Input (error={Marshal.GetLastWin32Error()})");
                    return false;
                }

                Logger.Info("LegionButtonMonitor: Registered for Raw Input mouse events");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"LegionButtonMonitor: InitializeScrollWheelRawInput exception: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Gets the device name from a Raw Input device handle.
        /// </summary>
        private string GetRawInputDeviceName(IntPtr hDevice)
        {
            try
            {
                uint size = 0;
                GetRawInputDeviceInfoW(hDevice, RIDI_DEVICENAME, IntPtr.Zero, ref size);
                if (size == 0) return null;

                IntPtr buffer = Marshal.AllocHGlobal((int)(size * 2));
                try
                {
                    if (GetRawInputDeviceInfoW(hDevice, RIDI_DEVICENAME, buffer, ref size) > 0)
                        return Marshal.PtrToStringUni(buffer);
                    return null;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Dedicated thread procedure for monitoring scroll wheel via Raw Input API.
        /// Uses Windows Raw Input to capture mouse events from the Legion Go scroll wheel
        /// interface (mi_01/col02). This works even though the device is "locked" by Windows.
        ///
        /// Raw Input button data:
        /// - 0x0010 = scroll click pressed
        /// - 0x0020 = scroll click released
        /// - 0x0400 = scroll event (check ulRawButtons for direction)
        /// </summary>
        private void ScrollWheelThreadProc()
        {
            Logger.Info("LegionButtonMonitor: Scroll wheel Raw Input thread started");

            try
            {
                // Create message-only window for Raw Input
                scrollWheelWindowHandle = CreateScrollWheelRawInputWindow();
                if (scrollWheelWindowHandle == IntPtr.Zero)
                {
                    Logger.Error("LegionButtonMonitor: Failed to create Raw Input window");
                    return;
                }

                // Register for Raw Input mouse events
                if (!InitializeScrollWheelRawInput(scrollWheelWindowHandle))
                {
                    Logger.Error("LegionButtonMonitor: Failed to initialize Raw Input");
                    DestroyWindow(scrollWheelWindowHandle);
                    scrollWheelWindowHandle = IntPtr.Zero;
                    return;
                }

                Logger.Info("LegionButtonMonitor: Scroll wheel Raw Input initialized, listening for Legion Go mi_01/col02 events");

                // Initialize diagnostic tracking
                lastScrollHeartbeat = DateTime.Now;
                lastScrollInputReceived = DateTime.Now;
                scrollInputCount = 0;
                hasReceivedScrollInput = false;

                // Message loop
                while (scrollWheelRunning)
                {
                    while (PeekMessage(out MSG msg, IntPtr.Zero, 0, 0, PM_REMOVE))
                    {
                        if (msg.message == WM_INPUT)
                        {
                            hasReceivedScrollInput = true;
                            lastScrollInputReceived = DateTime.Now;
                            scrollInputCount++;
                            ProcessScrollWheelRawInput(msg.lParam);
                        }
                        TranslateMessage(ref msg);
                        DispatchMessage(ref msg);
                    }

                    // Periodic heartbeat logging
                    var now = DateTime.Now;
                    if ((now - lastScrollHeartbeat).TotalMilliseconds >= HEARTBEAT_INTERVAL_MS)
                    {
                        var timeSinceLastInput = (now - lastScrollInputReceived).TotalSeconds;
                        bool windowValid = IsWindow(scrollWheelWindowHandle);
                        Logger.Info($"LegionButtonMonitor: Scroll thread heartbeat - inputCount={scrollInputCount}, lastInputAge={timeSinceLastInput:F1}s, windowValid={windowValid}, hasReceivedInput={hasReceivedScrollInput}");
                        lastScrollHeartbeat = now;

                        // Self-healing: Re-register Raw Input if we previously received input but stopped getting any
                        // This can help recover if Game Bar or another app disrupts Raw Input registration
                        if (hasReceivedScrollInput && timeSinceLastInput > (NO_INPUT_REREGISTER_MS / 1000.0) && windowValid)
                        {
                            Logger.Debug($"LegionButtonMonitor: Re-registering Raw Input (no scroll input for {timeSinceLastInput:F1}s)");
                            if (InitializeScrollWheelRawInput(scrollWheelWindowHandle))
                            {
                                Logger.Info("LegionButtonMonitor: Raw Input re-registration successful");
                                lastScrollInputReceived = now; // Reset timer after re-registration
                            }
                            else
                            {
                                Logger.Error("LegionButtonMonitor: Raw Input re-registration failed");
                            }
                        }
                    }

                    Thread.Sleep(10);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"LegionButtonMonitor: Scroll wheel thread exception: {ex.Message}\n{ex.StackTrace}");
            }
            finally
            {
                if (scrollWheelWindowHandle != IntPtr.Zero)
                {
                    DestroyWindow(scrollWheelWindowHandle);
                    scrollWheelWindowHandle = IntPtr.Zero;
                }
                Logger.Info("LegionButtonMonitor: Scroll wheel Raw Input thread exiting");
            }
        }

        /// <summary>
        /// Process a Raw Input message for scroll wheel events.
        /// Filters for Legion tablet-family devices and mi_01/col02 interface.
        /// </summary>
        private void ProcessScrollWheelRawInput(IntPtr hRawInput)
        {
            try
            {
                // Get size of raw input data
                uint size = 0;
                uint headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
                GetRawInputData(hRawInput, RID_INPUT, IntPtr.Zero, ref size, headerSize);
                if (size == 0) return;

                IntPtr buffer = Marshal.AllocHGlobal((int)size);
                try
                {
                    if (GetRawInputData(hRawInput, RID_INPUT, buffer, ref size, headerSize) == size)
                    {
                        var header = Marshal.PtrToStructure<RAWINPUTHEADER>(buffer);

                        // Filter: only process mouse input from Legion Go scroll wheel interface
                        string deviceName = GetRawInputDeviceName(header.hDevice);
                        if (deviceName == null) return;

                        string deviceLower = deviceName.ToLowerInvariant();

                        // Must be Legion tablet-family device (VID 17EF and known tablet PIDs).
                        if (!DeviceNameMatchesVidPid(deviceLower, LEGION_TABLET_VID, LEGION_TABLET_PIDS)) return;

                        // Must be scroll wheel interface: mi_01 and col02
                        if (!deviceLower.Contains("mi_01") || !deviceLower.Contains("col02")) return;

                        if (header.dwType == RIM_TYPEMOUSE)
                        {
                            var mouse = Marshal.PtrToStructure<RAWINPUT_MOUSE>(buffer);
                            ushort buttonFlags = mouse.mouse.usButtonFlags;
                            ushort buttonData = mouse.mouse.usButtonData;
                            uint rawButtons = mouse.mouse.ulRawButtons;

                            // Log ALL raw values to diagnose Legion Go 2 scroll wheel
                            // This helps identify what values LG2 sends (may differ from LG1)
                            if (buttonFlags != 0 || buttonData != 0 || rawButtons != 0)
                            {
                                Logger.Info($"LegionButtonMonitor: Scroll Raw Input - buttonFlags=0x{buttonFlags:X4}, buttonData=0x{buttonData:X4}, rawButtons=0x{rawButtons:X8}");
                            }

                            // Legion Go 1 sends scroll click in usButtonData (non-standard)
                            // 0x0010 = click pressed, 0x0020 = click released, 0x0400 = wheel scroll
                            // Legion Go 2 may use different values - check buttonFlags and rawButtons too

                            // First try buttonData (LG1 style)
                            switch (buttonData)
                            {
                                case 0x0010: // Scroll click pressed
                                    if (scrollClickEnabled && !lastScrollClickState)
                                    {
                                        // Apply cooldown for XboxGuide action to ensure Game Bar has time to process
                                        var timeSinceLastAction = (DateTime.Now - lastScrollClickActionTime).TotalMilliseconds;
                                        if (scrollClickActionType == LegionButtonAction.XboxGuide &&
                                            timeSinceLastAction < SCROLL_CLICK_COOLDOWN_MS)
                                        {
                                            Logger.Debug($"LegionButtonMonitor: Scroll Click PRESSED skipped (cooldown: {timeSinceLastAction:F0}ms < {SCROLL_CLICK_COOLDOWN_MS}ms)");
                                            // Don't set lastScrollClickState - ignore this press entirely
                                            break;
                                        }

                                        lastScrollClickState = true;
                                        lastScrollClickActionTime = DateTime.Now;
                                        scrollClickPressTime = DateTime.Now; // Track press time for minimum hold
                                        Logger.Info("LegionButtonMonitor: Scroll Click PRESSED (Raw Input)");
                                        ProcessButtonAction("Scroll Click", true, scrollClickActionType,
                                            scrollClickShortcutKeys, scrollClickCommandPath);
                                    }
                                    break;

                                case 0x0020: // Scroll click released
                                    if (scrollClickEnabled && lastScrollClickState)
                                    {
                                        lastScrollClickState = false;

                                        // Enforce minimum hold time for Xbox Guide action
                                        // Game Bar needs the button held for a minimum duration to register properly
                                        if (scrollClickActionType == LegionButtonAction.XboxGuide)
                                        {
                                            var holdDuration = (DateTime.Now - scrollClickPressTime).TotalMilliseconds;
                                            if (holdDuration < SCROLL_CLICK_MIN_HOLD_MS)
                                            {
                                                int waitTime = SCROLL_CLICK_MIN_HOLD_MS - (int)holdDuration;
                                                Logger.Debug($"LegionButtonMonitor: Scroll Click hold too short ({holdDuration:F0}ms), waiting {waitTime}ms before release");
                                                Thread.Sleep(waitTime);
                                            }
                                        }

                                        Logger.Info("LegionButtonMonitor: Scroll Click RELEASED (Raw Input)");
                                        ProcessButtonAction("Scroll Click", false, scrollClickActionType,
                                            scrollClickShortcutKeys, scrollClickCommandPath);
                                    }
                                    break;

                                case 0x0400: // Scroll wheel movement
                                    // Note: Raw Input API for Legion Go mi_01/col02 doesn't provide scroll direction
                                    // We can only detect that a scroll event occurred, not up vs down
                                    // Use unified scroll action for any scroll event
                                    if (scrollEnabled)
                                    {
                                        Logger.Debug("LegionButtonMonitor: Scroll Wheel (Raw Input)");
                                        ProcessScrollAction("Scroll", scrollActionType, scrollShortcutKeys, scrollCommandPath);
                                    }
                                    break;
                            }

                            // Also check buttonFlags for standard mouse wheel events (Legion Go 2 may use this)
                            // RI_MOUSE_WHEEL = 0x0400
                            const ushort RI_MOUSE_WHEEL = 0x0400;
                            const ushort RI_MOUSE_HWHEEL = 0x0800;
                            const ushort RI_MOUSE_MIDDLE_BUTTON_DOWN = 0x0010;
                            const ushort RI_MOUSE_MIDDLE_BUTTON_UP = 0x0020;

                            if ((buttonFlags & RI_MOUSE_WHEEL) != 0 || (buttonFlags & RI_MOUSE_HWHEEL) != 0)
                            {
                                // Standard mouse wheel event via buttonFlags
                                short wheelDelta = (short)buttonData; // Signed wheel delta
                                Logger.Info($"LegionButtonMonitor: Scroll Wheel via buttonFlags - delta={wheelDelta}");

                                if (wheelDelta > 0 && scrollUpEnabled)
                                {
                                    ProcessScrollAction("Scroll Up", scrollUpActionType, scrollUpShortcutKeys, scrollUpCommandPath);
                                }
                                else if (wheelDelta < 0 && scrollDownEnabled)
                                {
                                    ProcessScrollAction("Scroll Down", scrollDownActionType, scrollDownShortcutKeys, scrollDownCommandPath);
                                }
                                else if (scrollEnabled)
                                {
                                    ProcessScrollAction("Scroll", scrollActionType, scrollShortcutKeys, scrollCommandPath);
                                }
                            }

                            if ((buttonFlags & RI_MOUSE_MIDDLE_BUTTON_DOWN) != 0)
                            {
                                if (scrollClickEnabled && !lastScrollClickState)
                                {
                                    lastScrollClickState = true;
                                    lastScrollClickActionTime = DateTime.Now;
                                    scrollClickPressTime = DateTime.Now;
                                    Logger.Info("LegionButtonMonitor: Scroll Click PRESSED via buttonFlags");
                                    ProcessButtonAction("Scroll Click", true, scrollClickActionType,
                                        scrollClickShortcutKeys, scrollClickCommandPath);
                                }
                            }

                            if ((buttonFlags & RI_MOUSE_MIDDLE_BUTTON_UP) != 0)
                            {
                                if (scrollClickEnabled && lastScrollClickState)
                                {
                                    lastScrollClickState = false;
                                    Logger.Info("LegionButtonMonitor: Scroll Click RELEASED via buttonFlags");
                                    ProcessButtonAction("Scroll Click", false, scrollClickActionType,
                                        scrollClickShortcutKeys, scrollClickCommandPath);
                                }
                            }
                        }
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"LegionButtonMonitor: ProcessScrollWheelRawInput exception: {ex.Message}");
            }
        }

        /// <summary>
        /// Sends the initialization command to switch the controller to initialized mode.
        /// Legion Space sends this command: 05:00:01:04:00:00... (64 bytes)
        /// After initialization, the controller switches from 04:3C:74 to 04:00:A1 format.
        /// </summary>
        private bool InitializeController(SafeFileHandle handle)
        {
            try
            {
                // Legion Go S uses a separate HID command protocol; skip tablet init/heartbeat packets.
                if (IsGoSControllerDevice())
                {
                    return true;
                }

                byte[] initCommandPrefix = { 0x05, 0x00, 0x01, 0x04 };
                if (!SendOutputReport(handle, initCommandPrefix, "init command"))
                {
                    return false;
                }

                if (!_highQualityGyroConfigured)
                {
                    bool configured = ConfigureHighQualityGyroReports(handle);
                    _highQualityGyroConfigured = configured;
                    if (configured)
                    {
                        Logger.Info("LegionButtonMonitor: High-quality IMU mode enabled for both controllers");
                    }
                    else
                    {
                        Logger.Warn("LegionButtonMonitor: Failed to enable high-quality IMU mode");
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"LegionButtonMonitor: Exception sending initialization command: {ex.Message}");
                return false;
            }
        }

        private bool ConfigureHighQualityGyroReports(SafeFileHandle handle)
        {
            if (handle == null || handle.IsInvalid)
            {
                return false;
            }

            // The Lenovo controller firmware exposes high-quality IMU only when sub-command 0x6A/0x07 is set to 0x02.
            // We also send 0x6A/0x02 = 0x01 to ensure each controller gyro is powered on before enabling HQ reporting.
            byte[] leftEnable = { 0x05, 0x06, 0x6A, 0x02, LEGION_CONTROLLER_LEFT_ID, 0x01, 0x01 };
            byte[] rightEnable = { 0x05, 0x06, 0x6A, 0x02, LEGION_CONTROLLER_RIGHT_ID, 0x01, 0x01 };
            byte[] leftHighQuality = { 0x05, 0x06, 0x6A, 0x07, LEGION_CONTROLLER_LEFT_ID, 0x02, 0x01 };
            byte[] rightHighQuality = { 0x05, 0x06, 0x6A, 0x07, LEGION_CONTROLLER_RIGHT_ID, 0x02, 0x01 };

            bool leftPowered = SendOutputReport(handle, leftEnable, "left gyro enable");
            bool rightPowered = SendOutputReport(handle, rightEnable, "right gyro enable");
            bool leftHq = SendOutputReport(handle, leftHighQuality, "left gyro high-quality mode");
            bool rightHq = SendOutputReport(handle, rightHighQuality, "right gyro high-quality mode");

            return leftPowered && rightPowered && leftHq && rightHq;
        }

        /// <summary>
        /// Sends the gyro-calibration HID output report to the Legion Go controllers.
        /// Uses the working HID handle if one is open, otherwise returns false without
        /// throwing. The user must hold the controllers still during this window so the
        /// controller firmware can capture a fresh gyro bias.
        ///
        /// Wire format (from the VIIPER Controller reference):
        ///   [0]=0x05 (ReportID) [1]=0x00 [2]=0x0E (gyro command family)
        ///   [3]=0x06 (calibrate sub-command) [4]=0x03|0x04 (L/R) [5]=0x01
        /// </summary>
        public bool CalibrateGyro(bool left, bool right)
        {
            if (!left && !right)
            {
                Logger.Warn("LegionButtonMonitor.CalibrateGyro called with no target — skipping");
                return false;
            }
            var handle = hidHandle;
            if (handle == null || handle.IsInvalid)
            {
                Logger.Warn("LegionButtonMonitor.CalibrateGyro: no open HID handle");
                return false;
            }

            bool ok = true;
            if (left)
            {
                byte[] cmd = { 0x05, 0x00, 0x0E, 0x06, LEGION_CONTROLLER_LEFT_ID, 0x01 };
                ok &= SendOutputReport(handle, cmd, "gyro calibrate left");
            }
            if (right)
            {
                byte[] cmd = { 0x05, 0x00, 0x0E, 0x06, LEGION_CONTROLLER_RIGHT_ID, 0x01 };
                ok &= SendOutputReport(handle, cmd, "gyro calibrate right");
            }
            Logger.Info($"LegionButtonMonitor.CalibrateGyro left={left}, right={right}, ok={ok}");
            return ok;
        }

        /// <summary>
        /// Set the controllers' auto-sleep (idle power-off) time. minutes==0 requests "never".
        /// Protocol (RE doc legion_go_hid_protocol_complete): SET = 05 00 04 09 [min] 01, where
        /// [min] is the timeout as a raw byte (0x05=5, 0x0A=10, 0x0F=15, 0x14=20, 0x1E=30).
        /// Global command (not per-controller). Verified accepted (ok=True) on Legion Go 2.
        /// </summary>
        public bool SetAutoSleepTime(int minutes)
        {
            var handle = hidHandle;
            if (handle == null || handle.IsInvalid)
            {
                Logger.Warn("SetAutoSleepTime: no open HID handle");
                return false;
            }
            if (minutes < 0) minutes = 0;
            if (minutes > 255) minutes = 255;
            byte min = (byte)minutes;

            lock (_hidLock)
            {
                bool ok = SendOutputReport(handle, new byte[] { 0x05, 0x00, 0x04, 0x09, min, 0x01 }, $"auto-sleep set {minutes}min");
                Logger.Info($"LegionButtonMonitor.SetAutoSleepTime minutes={minutes} ok={ok}");
                return ok;
            }
        }

        private void DisableHighQualityGyroReports(SafeFileHandle handle)
        {
            if (handle == null || handle.IsInvalid || !_hasWriteAccess || !IsTabletControllerDevice())
            {
                return;
            }

            byte[] leftDisableHighQuality = { 0x05, 0x06, 0x6A, 0x07, LEGION_CONTROLLER_LEFT_ID, 0x01, 0x01 };
            byte[] rightDisableHighQuality = { 0x05, 0x06, 0x6A, 0x07, LEGION_CONTROLLER_RIGHT_ID, 0x01, 0x01 };

            SendOutputReport(handle, leftDisableHighQuality, "left gyro high-quality disable");
            SendOutputReport(handle, rightDisableHighQuality, "right gyro high-quality disable");
            _highQualityGyroConfigured = false;
        }

        /// <summary>
        /// Parse a b0:01 device-status report (header 04:00:B0) and raise DeviceStatusUpdated.
        /// Byte layout verified from live capture 2026-05-29 on the button-monitor stream — note
        /// this differs from LegionGoController.TryParseDeviceStatus (which reads mode at byte 12;
        /// on this stream byte 12 is always 0 and the real mode is byte 6):
        ///   [6]=mode (1=Solid,2=Pulse,3=Dynamic,4=Spiral; 0/0xFF=off),
        ///   [8..10]=R,G,B, [11]=brightness%, [13]=speed-raw, [15]=vibration, [18]=touchpad,
        ///   [21,22]=L/R battery, [28..31]=firmware.
        /// </summary>
        private void RaiseDeviceStatus(byte[] buffer)
        {
            var handler = DeviceStatusUpdated;
            if (handler == null) return;
            try
            {
                var copy = new byte[Math.Min(buffer.Length, 64)];
                Array.Copy(buffer, copy, copy.Length);

                var status = Devices.Libraries.Legion.LegionGoController.TryParseDeviceStatus(copy);
                if (status == null) return;

                // Byte 6 is the dedicated on/off flag (0x01=on, 0x02=off), independent of the
                // animation mode at byte 12. Verified via live capture 2026-05-29 — the canonical
                // parser's mode!=0xFF heuristic can't tell Off from Solid here (both leave byte 12
                // at 0x00), so set the explicit flag.
                if (copy.Length > 6) status.LightOnFlag = copy[6] == 0x01;
                handler(this, status);
            }
            catch (Exception ex)
            {
                Logger.Debug($"RaiseDeviceStatus threw: {ex.Message}");
            }
        }

        private bool SendOutputReport(SafeFileHandle handle, byte[] commandPrefix, string commandName)
        {
            if (handle == null || handle.IsInvalid || commandPrefix == null || commandPrefix.Length == 0)
            {
                return false;
            }

            byte[] outputReport = new byte[64];
            int copyCount = Math.Min(commandPrefix.Length, outputReport.Length);
            Buffer.BlockCopy(commandPrefix, 0, outputReport, 0, copyCount);

            _lastOutputReportTime = DateTime.Now;
            bool result = HidD_SetOutputReport(handle, outputReport, (uint)outputReport.Length);
            int error = Marshal.GetLastWin32Error();
            if (!result)
            {
                Logger.Warn($"LegionButtonMonitor: Failed to send {commandName} (error={error})");
                return false;
            }

            Logger.Debug($"LegionButtonMonitor: Sent {commandName} ({BitConverter.ToString(commandPrefix).Replace('-', ':')})");
            return true;
        }

        /// <summary>
        /// Probes a HID device to verify it sends the correct Legion format reports.
        /// If the controller is uninitialized (04:3C:74), sends initialization command.
        /// After initialization, controller should send 04:00:A1 format.
        /// </summary>
        /// <param name="handle">The HID device handle</param>
        /// <param name="hasWriteAccess">Whether the handle has write access for initialization</param>
        private bool ProbeDeviceFormat(SafeFileHandle handle, bool hasWriteAccess, ushort vendorId, ushort productId)
        {
            const uint READ_TIMEOUT_MS = 200; // Reduced timeout per read attempt (was 500ms)
            IntPtr eventHandle = IntPtr.Zero;
            bool initializationAttempted = false;
            bool isGoSController = IsLegionGoSControllerVidPid(vendorId, productId);

            try
            {
                // Create an event for overlapped I/O
                eventHandle = CreateEvent(IntPtr.Zero, true, false, null);
                if (eventHandle == IntPtr.Zero)
                {
                    Logger.Debug("LegionButtonMonitor: Failed to create event for probe");
                    return false;
                }

                byte[] buffer = new byte[64];
                int wrongFormatCount = 0;

                // Reduced from 10 to 5 attempts (still enough for initialization)
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    // Reset event before each overlapped operation
                    ResetEvent(eventHandle);
                    var overlapped = new NativeOverlapped { EventHandle = eventHandle };

                    bool readResult = ReadFile(handle, buffer, (uint)buffer.Length, out uint bytesRead, ref overlapped);
                    int lastError = Marshal.GetLastWin32Error();

                    if (!readResult && lastError == ERROR_IO_PENDING)
                    {
                        // Wait for read to complete with timeout
                        uint waitResult = WaitForSingleObject(eventHandle, READ_TIMEOUT_MS);

                        if (waitResult == WAIT_TIMEOUT)
                        {
                            // Timeout - cancel the pending I/O and try next attempt
                            CancelIo(handle);
                            Logger.Debug($"LegionButtonMonitor: Probe attempt {attempt + 1} timed out");
                            continue;
                        }
                        else if (waitResult == WAIT_OBJECT_0)
                        {
                            // Read completed - get the result
                            if (!GetOverlappedResult(handle, ref overlapped, out bytesRead, false))
                            {
                                Logger.Debug($"LegionButtonMonitor: Probe attempt {attempt + 1} GetOverlappedResult failed");
                                continue;
                            }
                            readResult = true;
                        }
                        else
                        {
                            Logger.Debug($"LegionButtonMonitor: Probe attempt {attempt + 1} WaitForSingleObject failed: {waitResult}");
                            continue;
                        }
                    }

                    if (readResult || bytesRead > 0)
                    {
                        if (isGoSController)
                        {
                            if (LooksLikeGoSGamepadReport(buffer, bytesRead))
                            {
                                isDetachedMode = false;
                                currentButtonByte = BUTTON_BYTE_ATTACHED;
                                Logger.Info($"LegionButtonMonitor: Probe success (Legion Go S mode) - {bytesRead} bytes");
                                return true;
                            }

                            wrongFormatCount++;
                            Logger.Debug($"LegionButtonMonitor: Go S probe attempt {attempt + 1} - unexpected report shape ({bytesRead} bytes)");
                            if (wrongFormatCount >= 3)
                            {
                                Logger.Debug("LegionButtonMonitor: Probe failed - consistently wrong Go S report format");
                                return false;
                            }

                            continue;
                        }

                        // Must be exactly 64 bytes with Legion format starting with 04
                        if (bytesRead == 64 && buffer[0] == 0x04)
                        {
                            // Check if initialized (04:00:A1) or uninitialized (04:3C:74)
                            bool isInitialized = buffer[1] == 0x00 && buffer[2] == 0xA1;
                            bool isUninitialized = buffer[1] == 0x3C && buffer[2] == 0x74;

                            if (isInitialized)
                            {
                                // Controller is in initialized mode - use standard format
                                isDetachedMode = false;
                                currentButtonByte = BUTTON_BYTE_ATTACHED;
                                Logger.Info($"LegionButtonMonitor: Probe success (initialized mode) - {bytesRead} bytes, header: 04:00:A1, btn byte: {currentButtonByte}");
                                return true;
                            }
                            else if (isUninitialized)
                            {
                                bool initFutile = IsInitFutile(vendorId, productId);
                                if (!initializationAttempted && hasWriteAccess && !initFutile)
                                {
                                    // Controller is uninitialized - send initialization command
                                    Logger.Info("LegionButtonMonitor: Controller is uninitialized (04:3C:74), sending init command...");
                                    initializationAttempted = true;
                                    bool initResult = InitializeController(handle);
                                    Logger.Info($"LegionButtonMonitor: Init command result: {initResult}");
                                    if (initResult)
                                    {
                                        Thread.Sleep(300); // Give controller time to switch modes
                                    }
                                    // Continue reading to check if initialization worked
                                    continue;
                                }
                                else
                                {
                                    // Either we have no write access, init was attempted and the
                                    // device didn't transition to 04:00:A1, or we already learned
                                    // this VID/PID's firmware ignores the init command. On some Go 2
                                    // variants (e.g. 83N0 / 8ASP2) the firmware keeps the tablet
                                    // layout regardless. The monitor loop handles both headers by
                                    // switching currentButtonByte, so accept 04:3C:74 as a valid
                                    // layout instead of rejecting.
                                    string reason;
                                    if (!hasWriteAccess) reason = "no write access to send init";
                                    else if (initFutile) reason = "previously learned init is ignored on this VID/PID";
                                    else
                                    {
                                        reason = "init attempted but device stayed in uninitialized mode";
                                        // Cache the result so subsequent reconnects skip the futile
                                        // ~300ms init+settle and go straight to the fallback.
                                        MarkInitFutile(vendorId, productId);
                                    }
                                    Logger.Warn($"LegionButtonMonitor: Using fallback (detached) layout — {reason}");
                                    isDetachedMode = true;
                                    currentButtonByte = BUTTON_BYTE_DETACHED;
                                    Logger.Info($"LegionButtonMonitor: Probe success (fallback/uninitialized mode) - {bytesRead} bytes, header: 04:3C:74, btn byte: {currentButtonByte}");
                                    return true;
                                }
                            }
                        }

                        wrongFormatCount++;
                        Logger.Info($"LegionButtonMonitor: Probe attempt {attempt + 1} - got {bytesRead} bytes, header: {buffer[0]:X2}:{buffer[1]:X2}:{buffer[2]:X2}");

                        // Reduced from 3 to 2 - fail faster on wrong devices
                        if (wrongFormatCount >= 2)
                        {
                            Logger.Debug("LegionButtonMonitor: Probe failed - consistently wrong format");
                            return false;
                        }
                    }
                }

                Logger.Debug("LegionButtonMonitor: Probe failed - could not read correct format reports");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Debug($"LegionButtonMonitor: Probe exception: {ex.Message}");
                return false;
            }
            finally
            {
                if (eventHandle != IntPtr.Zero)
                {
                    CloseHandle(eventHandle);
                }
            }
        }

        /// <summary>
        /// Attempts to reconnect to the Legion controller after disconnection.
        /// Also ensures ViGEm controller is created if Xbox Guide action is configured.
        /// </summary>
        private bool TryReconnect()
        {
            try
            {
                // Ensure old handle is closed
                if (hidHandle != null && !hidHandle.IsInvalid)
                {
                    DisableHighQualityGyroReports(hidHandle);
                    hidHandle.Close();
                    hidHandle = null;
                }
                _hasWriteAccess = false;
                _highQualityGyroConfigured = false;

                // Try to find and open the controller again
                if (!OpenLegionController())
                {
                    return false;
                }

                // Reset button states on reconnect
                lastLegionLState = false;
                lastLegionRState = false;
                pendingLegionLState = null;
                pendingLegionRState = null;
                pendingLegionLStateSince = DateTime.MinValue;
                pendingLegionRStateSince = DateTime.MinValue;

                // (ViGEm retirement: the dedicated Guide pad is gone — VIIPER's
                // guide-only pad reconciles itself via NotifyGuideRouteChanged.)

                return true;
            }
            catch (Exception ex)
            {
                Logger.Debug($"LegionButtonMonitor: Reconnect exception: {ex.Message}");
                return false;
            }
        }

        private void MonitorLoop()
        {
            Logger.Info("LegionButtonMonitor: Monitor thread started (unified L+R)");
            try
            {
                MonitorLoopInternal();
                Logger.Info("LegionButtonMonitor: Monitor thread exited normally");
            }
            catch (AccessViolationException ex)
            {
                Logger.Error($"LegionButtonMonitor: FATAL ACCESS VIOLATION: {ex.Message}\n{ex.StackTrace}");
            }
            catch (SEHException ex)
            {
                Logger.Error($"LegionButtonMonitor: FATAL SEH EXCEPTION: {ex.Message}\n{ex.StackTrace}");
            }
            catch (Exception ex)
            {
                Logger.Error($"LegionButtonMonitor: FATAL - Monitor loop crashed: {ex.Message}\n{ex.StackTrace}");
            }
            finally
            {
                Logger.Info($"LegionButtonMonitor: Monitor thread ending, isRunning={isRunning}");
            }
        }

        private void MonitorLoopInternal()
        {
            byte[] buffer = new byte[64];
            // Pin the buffer for the lifetime of the monitor loop to prevent GC from moving it
            // This is critical for overlapped I/O operations
            GCHandle bufferHandle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            int consecutiveFailures = 0;
            const int MAX_FAILURES_BEFORE_RECONNECT = 3;
            int reconnectDelayMs = 1000;
            const int MAX_RECONNECT_DELAY_MS = 10000;
            const uint READ_TIMEOUT_MS = 100; // Short timeout so we can check isRunning frequently

            // Controller heartbeat - send init command every 3 seconds to keep controller in initialized mode
            // Legion Space uses 5 second timeout, so 3 seconds gives us margin
            const int HEARTBEAT_INTERVAL_MS = 3000;
            DateTime lastHeartbeat = DateTime.Now;

            IntPtr eventHandle = CreateEvent(IntPtr.Zero, true, false, null);
            if (eventHandle == IntPtr.Zero)
            {
                Logger.Error("LegionButtonMonitor: Failed to create event for monitor loop");
                return;
            }

            try
            {
                int loopIteration = 0;
                while (isRunning)
                {
                    loopIteration++;
                    try
                    {
                        ReconcileGuideRoute();

                        // If no valid handle, try to reconnect
                        if (hidHandle == null || hidHandle.IsInvalid)
                        {
                            if (TryReconnect())
                            {
                                consecutiveFailures = 0;
                                reconnectDelayMs = 1000; // Reset delay on success
                                lastHeartbeat = DateTime.Now; // Reset heartbeat after reconnect
                                Logger.Info("LegionButtonMonitor: Reconnected successfully");
                            }
                            else
                            {
                                // All candidates present but rejected — either
                                // protocol-incompatible hardware (issue #90, Legion Go S,
                                // permanent) or controllers powered off / detached on a
                                // Go 1/Go 2 (transient). Full probing costs ~1s per
                                // candidate, so back off to a 60s full-probe cadence.
                                // A cheap path-set check every 10s aborts the backoff as
                                // soon as the Legion HID interface set changes (controller
                                // powered on / attached), keeping wake-up latency low.
                                if (_consecutiveAllRejectedScans >= AllRejectedScansBeforeBackoff)
                                {
                                    if (!_loggedIncompatibleBackoff)
                                    {
                                        Logger.Warn($"LegionButtonMonitor: {_consecutiveAllRejectedScans} consecutive scans rejected every Legion HID device — backing off to {IncompatibleRescanDelayMs / 1000}s full probes with {BackoffPathSetCheckIntervalMs / 1000}s device-set checks");
                                        _loggedIncompatibleBackoff = true;
                                    }
                                    string pathSetAtBackoff = ComputeLegionHidPathSetSignature();
                                    int sinceSetCheckMs = 0;
                                    // Sleep in 1s slices so Stop() stays responsive.
                                    for (int slept = 0; slept < IncompatibleRescanDelayMs && isRunning; slept += 1000)
                                    {
                                        Thread.Sleep(1000);
                                        sinceSetCheckMs += 1000;
                                        if (sinceSetCheckMs >= BackoffPathSetCheckIntervalMs)
                                        {
                                            sinceSetCheckMs = 0;
                                            string now = ComputeLegionHidPathSetSignature();
                                            if (!string.Equals(now, pathSetAtBackoff, StringComparison.Ordinal))
                                            {
                                                Logger.Info("LegionButtonMonitor: Legion HID device set changed during backoff — retrying full scan now");
                                                break;
                                            }
                                        }
                                    }
                                }
                                else
                                {
                                    // Exponential backoff for reconnect attempts
                                    Thread.Sleep(reconnectDelayMs);
                                    reconnectDelayMs = Math.Min(reconnectDelayMs * 2, MAX_RECONNECT_DELAY_MS);
                                }
                            }
                            continue;
                        }

                        // Send heartbeat to keep controller in initialized mode
                        // Legion Space times out after 5 seconds, so we send every 3 seconds
                        // We keep heartbeats active for both 0xA1 and 0x74 headers to avoid controller IMU timeout.
                        if (_hasWriteAccess && (DateTime.Now - lastHeartbeat).TotalMilliseconds >= HEARTBEAT_INTERVAL_MS)
                        {
                            if (InitializeController(hidHandle))
                            {
                                lastHeartbeat = DateTime.Now;
                            }
                        }

                        // Read HID report with overlapped I/O and timeout
                        // Reset the event before starting a new overlapped operation
                        ResetEvent(eventHandle);
                        var overlapped = new NativeOverlapped { EventHandle = eventHandle };
                        bool readResult = ReadFile(hidHandle, buffer, (uint)buffer.Length, out uint bytesRead, ref overlapped);
                        int lastError = Marshal.GetLastWin32Error();

                        if (!readResult && lastError == ERROR_IO_PENDING)
                        {
                            // Wait for read to complete with timeout
                            uint waitResult = WaitForSingleObject(eventHandle, READ_TIMEOUT_MS);

                            if (waitResult == WAIT_TIMEOUT)
                            {
                                // Timeout - cancel and check if we should continue
                                CancelIo(hidHandle);
                                continue;
                            }
                            else if (waitResult == WAIT_OBJECT_0)
                            {
                                // Read completed - get the result
                                if (GetOverlappedResult(hidHandle, ref overlapped, out bytesRead, false))
                                {
                                    readResult = true;
                                }
                                else
                                {
                                    consecutiveFailures++;
                                    continue;
                                }
                            }
                            else
                            {
                                consecutiveFailures++;
                                continue;
                            }
                        }
                        else if (!readResult)
                        {
                            // Read failed immediately
                            consecutiveFailures++;

                            if (consecutiveFailures >= MAX_FAILURES_BEFORE_RECONNECT)
                            {
                                Logger.Warn($"LegionButtonMonitor: {consecutiveFailures} consecutive read failures (error {lastError}), attempting reconnect...");

                                // Close invalid handle and trigger reconnect
                                if (hidHandle != null && !hidHandle.IsInvalid)
                                {
                                    DisableHighQualityGyroReports(hidHandle);
                                    hidHandle.Close();
                                }
                                hidHandle = null;
                                _hasWriteAccess = false;
                                _highQualityGyroConfigured = false;
                                consecutiveFailures = 0;
                            }
                            continue;
                        }

                        if (readResult && bytesRead >= 1)
                        {
                            consecutiveFailures = 0; // Reset on successful read

                            // Diagnostic: measure actual HID report arrival rate.
                            // Counts every successful ReadFile completion and emits
                            // a per-second log line with the observed rate so we
                            // can confirm whether Lenovo's firmware emits at the
                            // ~64 Hz we measured indirectly, or higher. Adds zero
                            // overhead in the hot path (just two interlocked
                            // counters + one tick check).
                            long _rateNow = DateTime.UtcNow.Ticks;
                            _hidReportRateCount++;
                            // Bucket by the first 3 header bytes (Lenovo report type).
                            // Forwarder sees ~64 Hz of usable samples at the same time
                            // that the read loop sees 125 Hz reports, so half are some
                            // other report type. This breaks it down so we can see
                            // which IDs Lenovo's firmware is interleaving.
                            if (bytesRead >= 3)
                            {
                                int hdr = (buffer[0] << 16) | (buffer[1] << 8) | buffer[2];
                                if (_hidReportHeaderCounts.TryGetValue(hdr, out int c)) _hidReportHeaderCounts[hdr] = c + 1;
                                else _hidReportHeaderCounts[hdr] = 1;
                            }
                            if (_rateNow - _hidReportRateLastEmitTicks >= TimeSpan.TicksPerSecond)
                            {
                                double elapsedSec = (_rateNow - _hidReportRateLastEmitTicks) / (double)TimeSpan.TicksPerSecond;
                                double rate = _hidReportRateCount / elapsedSec;
                                var sb = new System.Text.StringBuilder();
                                sb.Append($"LegionHID report rate: {rate:F1} Hz ({_hidReportRateCount} reports in {elapsedSec:F2}s) | headers: ");
                                foreach (var kv in _hidReportHeaderCounts)
                                {
                                    int h = kv.Key;
                                    sb.Append($"{(byte)(h >> 16):X2}:{(byte)(h >> 8):X2}:{(byte)h:X2}={kv.Value} ");
                                }
                                Logger.Info(sb.ToString());
                                _hidReportRateCount = 0;
                                _hidReportRateLastEmitTicks = _rateNow;
                                _hidReportHeaderCounts.Clear();
                            }

                            // Light/status report (b0:01 response, header 04:00:B0) arrives on
                            // this handle. Request it periodically and parse the light state so
                            // the Controller Info card reflects true hardware even when this
                            // monitor (not LegionControllerService) owns the device.
                            {
                                long _now = DateTime.UtcNow.Ticks;
                                if (_now - _statusReqLastTicks >= StatusReqIntervalTicks)
                                {
                                    _statusReqLastTicks = _now;
                                    var h = hidHandle;
                                    if (h != null && !h.IsInvalid)
                                    {
                                        SendOutputReport(h, new byte[] { 0x05, 0x00, 0xB0, 0x01, 0x00 }, "b0:01 status req");
                                    }
                                }
                                if (bytesRead >= 32 && buffer[0] == 0x04 && buffer[1] == 0x00 && buffer[2] == 0xB0)
                                {
                                    RaiseDeviceStatus(buffer);
                                }
                            }

                            // Note: Scroll wheel reports (0x07) are now handled by the dedicated ScrollWheelThreadProc
                            // which reads from the separate mi_01 HID interface. This ensures only Legion Go
                            // scroll wheel events are captured, not events from other mice.

                            // Validate report format before parsing battery/buttons.
                            // Tablet-family reports:
                            // - Attached/initialized mode: 04:00:A1 (battery at bytes 3-6, buttons at byte 16)
                            // - Detached/uninitialized mode: 04:3C:74 (battery at bytes 5-8, buttons at byte 18)
                            bool hasTabletReportHeader = false;
                            if (bytesRead >= currentButtonByte + 1 && bytesRead >= 14 && buffer[0] == 0x04)
                            {
                                bool isInitializedHeader = buffer[1] == 0x00 && buffer[2] == 0xA1;
                                bool isUninitializedHeader = buffer[1] == 0x3C && buffer[2] == 0x74;
                                if (isInitializedHeader)
                                {
                                    hasTabletReportHeader = true;  // Attached mode: 04:00:A1
                                    isDetachedMode = false;
                                    currentButtonByte = BUTTON_BYTE_ATTACHED;
                                }
                                else if (isUninitializedHeader)
                                {
                                    hasTabletReportHeader = true;  // Detached mode: 04:3C:74
                                    isDetachedMode = true;
                                    currentButtonByte = BUTTON_BYTE_DETACHED;
                                }
                            }

                            bool hasGoSReport = false;
                            if (hasTabletReportHeader)
                            {
                                TryParseAndStoreGyroSamples(buffer, bytesRead, IsGo2TabletControllerDevice());
                            }
                            else if (IsGoSControllerDevice())
                            {
                                hasGoSReport = TryParseAndStoreGoSGamepadSample(buffer, bytesRead);
                            }

                            // Parse battery data from valid reports
                            try
                            {
                                if (hasTabletReportHeader)
                                {
                                    // With heartbeat always active, we use 04:00:A1 header format
                                    // Battery at bytes 3-6, connection status at bytes 10-11
                                    int batteryOffset = 3;
                                    int connOffset = 10;

                                    // Connection status: 0x01=Off, 0x02=Attached, 0x03=Detached
                                    // Only 0x02 means the controller is actually connected
                                    bool leftConnected = buffer[connOffset] == 0x02;
                                    bool rightConnected = buffer[connOffset + 1] == 0x02;

                                    // Battery value (1-100), or -1 if not connected
                                    int leftBattery = leftConnected ? buffer[batteryOffset] : -1;
                                    int rightBattery = rightConnected ? buffer[batteryOffset + 2] : -1;

                                    // Charging status byte values (need to verify which means charging)
                                    byte leftChargingByte = buffer[batteryOffset + 1];
                                    byte rightChargingByte = buffer[batteryOffset + 3];

                                    // Log raw values when they change to help debug
                                    if (leftChargingByte != _lastLeftChargingByte || rightChargingByte != _lastRightChargingByte)
                                    {
                                        Logger.Info($"LegionButtonMonitor: Charging bytes L=0x{leftChargingByte:X2} R=0x{rightChargingByte:X2}");
                                        _lastLeftChargingByte = leftChargingByte;
                                        _lastRightChargingByte = rightChargingByte;
                                    }

                                    // Charging status: 0x02 = charging (USB power), 0x01 = discharging (battery)
                                    bool leftCharging = leftConnected && leftChargingByte == 0x02;
                                    bool rightCharging = rightConnected && rightChargingByte == 0x02;

                                    // Fire event if values changed AND throttle time has passed
                                    bool valuesChanged = leftBattery != _lastLeftBattery || rightBattery != _lastRightBattery ||
                                        leftCharging != _lastLeftCharging || rightCharging != _lastRightCharging ||
                                        leftConnected != _lastLeftConnected || rightConnected != _lastRightConnected;
                                    bool throttleExpired = (DateTime.Now - _lastBatteryUpdateTime).TotalMilliseconds >= BATTERY_UPDATE_THROTTLE_MS;

                                    if (valuesChanged && throttleExpired)
                                    {
                                        _lastLeftBattery = leftBattery;
                                        _lastRightBattery = rightBattery;
                                        _lastLeftCharging = leftCharging;
                                        _lastRightCharging = rightCharging;
                                        _lastLeftConnected = leftConnected;
                                        _lastRightConnected = rightConnected;
                                        _lastBatteryUpdateTime = DateTime.Now;

                                        Logger.Debug($"LegionButtonMonitor: Battery update L={leftBattery}% (conn={leftConnected}) R={rightBattery}% (conn={rightConnected})");
                                        try
                                        {
                                            BatteryUpdated?.Invoke(this, new LegionButtonBatteryEventArgs(
                                                leftBattery, leftCharging, leftConnected,
                                                rightBattery, rightCharging, rightConnected));
                                        }
                                        catch (Exception eventEx)
                                        {
                                            Logger.Error($"LegionButtonMonitor: BatteryUpdated event handler exception: {eventEx.Message}");
                                        }
                                    }
                                }
                            }
                            catch (Exception batteryEx)
                            {
                                Logger.Error($"LegionButtonMonitor: Battery parsing exception: {batteryEx.Message}");
                            }

                            bool canProcessButtons = hasTabletReportHeader || hasGoSReport;
                            if (canProcessButtons)
                            {
                                bool legionLRawPressed;
                                bool legionRRawPressed;
                                if (hasTabletReportHeader)
                                {
                                    byte currentBtnValue = buffer[currentButtonByte];
                                    legionLRawPressed = (currentBtnValue & LEGION_L_BIT) != 0;
                                    legionRRawPressed = (currentBtnValue & LEGION_R_BIT) != 0;
                                }
                                else
                                {
                                    byte gosButtonByte = buffer[GOS_BUTTON_BYTE];
                                    legionLRawPressed = (gosButtonByte & GOS_LEGION_L_BIT) != 0;
                                    legionRRawPressed = (gosButtonByte & GOS_LEGION_R_BIT) != 0;
                                }

                                // Process Legion L button if configured
                                if (legionLEnabled)
                                {
                                    if (TryCommitDebouncedButtonState(
                                        legionLRawPressed,
                                        ref lastLegionLState,
                                        ref pendingLegionLState,
                                        ref pendingLegionLStateSince,
                                        out bool legionLPressed))
                                    {
                                        try
                                        {
                                            ProcessButtonAction("Legion L", legionLPressed, legionLActionType,
                                                legionLShortcutKeys, legionLCommandPath);
                                        }
                                        catch (Exception btnEx)
                                        {
                                            Logger.Error($"LegionButtonMonitor: Legion L button action exception: {btnEx.Message}\n{btnEx.StackTrace}");
                                        }
                                    }
                                }

                                // Process Legion R button if configured
                                if (legionREnabled)
                                {
                                    if (TryCommitDebouncedButtonState(
                                        legionRRawPressed,
                                        ref lastLegionRState,
                                        ref pendingLegionRState,
                                        ref pendingLegionRStateSince,
                                        out bool legionRPressed))
                                    {
                                        try
                                        {
                                            ProcessButtonAction("Legion R", legionRPressed, legionRActionType,
                                                legionRShortcutKeys, legionRCommandPath);
                                        }
                                        catch (Exception btnEx)
                                        {
                                            Logger.Error($"LegionButtonMonitor: Legion R button action exception: {btnEx.Message}\n{btnEx.StackTrace}");
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch (ObjectDisposedException)
                    {
                        // HID handle torn down during a disconnect/reconnect
                        // or HidHide cycle-port (we lose the file handle when
                        // Windows re-enumerates the device). The reconnect
                        // path later in the loop rebuilds the stream, so this
                        // is normal transient noise — Warn, not Error.
                        Logger.Warn($"LegionButtonMonitor: HID handle disposed during monitor loop (iteration {loopIteration}); will reconnect");
                        consecutiveFailures++;
                        Thread.Sleep(500);
                    }
                    catch (NullReferenceException)
                    {
                        // Same transient class: a field (stream/device/reader)
                        // was nulled by the reconnect path between the null
                        // check and the use. Downgraded to Warn so reboots
                        // don't flood the log with iteration NREs.
                        Logger.Warn($"LegionButtonMonitor: transient null during monitor loop (iteration {loopIteration}); will reconnect");
                        consecutiveFailures++;
                        Thread.Sleep(500);
                    }
                    catch (ArgumentNullException ex) when (ex.ParamName == "pHandle" || ex.ParamName == "handle" || ex.ParamName == "SafeHandle" || (ex.StackTrace != null && ex.StackTrace.Contains("SafeHandleAddRef")))
                    {
                        // P/Invoke marshaller throws this when a torn-down
                        // SafeFileHandle gets passed to ReadFile during a
                        // disconnect/reconnect race. Functionally identical to
                        // ObjectDisposedException above — the reconnect path
                        // rebuilds the handle. Filter is scoped to the SafeHandle
                        // case so user-supplied callbacks (ProcessButtonAction,
                        // BatteryUpdated, parsers) that throw ArgumentNullException
                        // for their own reasons still surface in the generic catch
                        // below with full diagnostics.
                        Logger.Warn($"LegionButtonMonitor: transient null handle during monitor loop (iteration {loopIteration}); will reconnect");
                        consecutiveFailures++;
                        Thread.Sleep(500);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"LegionButtonMonitor: Exception in monitor loop (iteration {loopIteration}): {ex.Message}\n{ex.StackTrace}");
                        consecutiveFailures++;
                        Thread.Sleep(500);
                    }

                    // Log every 500 iterations to verify loop is running (Debug level to reduce log spam)
                    if (loopIteration % 500 == 0)
                    {
                        Logger.Debug($"LegionButtonMonitor: Monitor loop alive, iteration {loopIteration}");
                    }
                }
            }
            catch (Exception fatalEx)
            {
                Logger.Error($"LegionButtonMonitor: FATAL exception in monitor loop: {fatalEx.Message}\n{fatalEx.StackTrace}");
            }
            finally
            {
                Logger.Info("LegionButtonMonitor: Monitor loop exiting, closing event handle and freeing buffer");
                CloseHandle(eventHandle);
                if (bufferHandle.IsAllocated)
                {
                    bufferHandle.Free();
                }
            }
        }

        private static bool LooksLikeGoSGamepadReport(byte[] buffer, uint bytesRead)
        {
            if (buffer == null || bytesRead != 64)
            {
                return false;
            }

            // Reserved bits in byte2 are expected to stay clear for known Go S button map.
            if ((buffer[2] & 0x3C) != 0)
            {
                return false;
            }

            // Reject known tablet header signatures.
            if (buffer[0] == 0x04 &&
                ((buffer[1] == 0x00 && buffer[2] == 0xA1) ||
                 (buffer[1] == 0x3C && buffer[2] == 0x74)))
            {
                return false;
            }

            return true;
        }

        private static bool TryParseAndStoreGoSGamepadSample(byte[] buffer, uint bytesRead)
        {
            if (!LooksLikeGoSGamepadReport(buffer, bytesRead))
            {
                return false;
            }

            // Legion Go S HID layout (interface 6) from HHD:
            // - buttons: bytes 0..2 (BM mapping in slim/const.py)
            // - sticks: bytes 4..7 (m8)
            // - triggers: bytes 12/13 (HHD notes these are "confused"; on Windows raw captures
            //   LT/RT align as LT=12, RT=13)
            byte buttonByte0 = buffer[0];
            byte buttonByte1 = buffer[1];
            byte buttonByte2 = buffer[2];
            byte leftStickXRaw = buffer[4];
            byte leftStickYRaw = buffer[5];
            byte rightStickXRaw = buffer[6];
            byte rightStickYRaw = buffer[7];
            byte leftTrigger = buffer[12];
            byte rightTrigger = buffer[13];

            ushort buttons = 0;
            // byte0: mode/share/ls/rs/dpad (A/B/X/Y are on byte1)
            if ((buttonByte0 & 0x10) != 0) buttons |= XINPUT_GAMEPAD_DPAD_UP;
            if ((buttonByte0 & 0x20) != 0) buttons |= XINPUT_GAMEPAD_DPAD_DOWN;
            if ((buttonByte0 & 0x40) != 0) buttons |= XINPUT_GAMEPAD_DPAD_LEFT;
            if ((buttonByte0 & 0x80) != 0) buttons |= XINPUT_GAMEPAD_DPAD_RIGHT;
            if ((buttonByte0 & 0x04) != 0) buttons |= XINPUT_GAMEPAD_LEFT_THUMB;
            if ((buttonByte0 & 0x08) != 0) buttons |= XINPUT_GAMEPAD_RIGHT_THUMB;

            // byte1: ABXY + bumpers + digital trigger flags
            if ((buttonByte1 & 0x01) != 0) buttons |= XINPUT_GAMEPAD_A;
            if ((buttonByte1 & 0x02) != 0) buttons |= XINPUT_GAMEPAD_B;
            if ((buttonByte1 & 0x04) != 0) buttons |= XINPUT_GAMEPAD_X;
            if ((buttonByte1 & 0x08) != 0) buttons |= XINPUT_GAMEPAD_Y;
            if ((buttonByte1 & 0x10) != 0) buttons |= XINPUT_GAMEPAD_LEFT_SHOULDER;
            if ((buttonByte1 & 0x40) != 0) buttons |= XINPUT_GAMEPAD_RIGHT_SHOULDER;

            // byte2: extra buttons + start/select
            if ((buttonByte2 & 0x80) != 0) buttons |= XINPUT_GAMEPAD_START;
            if ((buttonByte2 & 0x40) != 0) buttons |= XINPUT_GAMEPAD_BACK;

            long sampleTimestampUtc = DateTime.UtcNow.Ticks;
            var sample = new LegionGamepadSample(
                buttons,
                leftTrigger,
                rightTrigger,
                ScaleStickByteToXInput(leftStickXRaw, false),
                ScaleStickByteToXInput(leftStickYRaw, true),
                ScaleStickByteToXInput(rightStickXRaw, false),
                ScaleStickByteToXInput(rightStickYRaw, true),
                0,
                sampleTimestampUtc);

            lock (_gyroSampleLock)
            {
                _latestGamepadSample = sample;
                _hasGamepadSample = true;
            }

            return true;
        }

        private static void TryParseAndStoreGyroSamples(byte[] buffer, uint bytesRead, bool isGo2TabletController)
        {
            if (buffer == null || bytesRead < 24)
            {
                return;
            }

            bool isInitializedHeader = buffer[0] == 0x04 && buffer[1] == 0x00 && buffer[2] == 0xA1;
            bool isUninitializedHeader = buffer[0] == 0x04 && buffer[1] == 0x3C && buffer[2] == 0x74;
            if (!isInitializedHeader && !isUninitializedHeader)
            {
                return;
            }

            // High-quality IMU payload:
            // - 04:00:A1 reports start at offset 32
            // - 04:3C:74 reports start at offset 34
            int imuBase = isInitializedHeader ? 32 : 34;
            int touchBase = isInitializedHeader ? 24 : 26;
            long sampleTimestampUtc = DateTime.UtcNow.Ticks;

            LegionGamepadSample gamepadSample = default;
            bool hasGamepadSample = TryParseGamepadSample(
                buffer,
                bytesRead,
                isInitializedHeader,
                sampleTimestampUtc,
                isGo2TabletController,
                out gamepadSample);

            LegionGyroSample leftSample = default;
            LegionGyroSample rightSample = default;
            LegionTouchpadSample rightTouchSample = default;
            bool hasLeftSample = false;
            bool hasRightSample = false;
            bool hasRightTouchSample = false;

            if (bytesRead > touchBase + 3)
            {
                // Legion touch bytes are split into range + area:
                // [X range][X area 0..3][Y range][Y area 0..3]
                // Rebuild 10-bit-like coordinates from area(high 2 bits) + range(low 8 bits).
                byte xRange = buffer[touchBase];
                byte xAreaRaw = buffer[touchBase + 1];
                byte yRange = buffer[touchBase + 2];
                byte yAreaRaw = buffer[touchBase + 3];

                int xArea = xAreaRaw & 0x03;
                int yArea = yAreaRaw & 0x03;

                ushort rawX = (ushort)((xArea << 8) | xRange);
                ushort rawY = (ushort)((yArea << 8) | yRange);

                bool areaLooksValid = (xAreaRaw & 0xFC) == 0 && (yAreaRaw & 0xFC) == 0;
                bool hasPosition = xRange != 0 || yRange != 0 || xArea != 0 || yArea != 0;
                bool touching = areaLooksValid && hasPosition;

                // Touchpad physical press: bit 0x80 at byte 19 (init) / 21 (uninit),
                // same byte that carries front face buttons.
                int touchPressIdx = isInitializedHeader ? 19 : 21;
                bool pressed = bytesRead > touchPressIdx && (buffer[touchPressIdx] & 0x80) != 0;

                rightTouchSample = new LegionTouchpadSample(touching, pressed, rawX, rawY, sampleTimestampUtc);
                hasRightTouchSample = true;
            }

            if (bytesRead > imuBase + 25)
            {
                int leftBase = imuBase;
                int rightBase = imuBase + 13;

                bool leftHighQualityActive = HasHighQualityImuSample(buffer, leftBase);
                bool rightHighQualityActive = HasHighQualityImuSample(buffer, rightBase);

                // Legion Go 2 IMU parser. Empirically-discovered offsets +
                // little-endian byte order that match LGO2's actual report
                // layout. The byte indices and scale factors are confirmed
                // against captured HID traffic on Legion Go 2 hardware.
                //
                // Per-side sign convention. Left and right joycons mount their
                // IMUs as 180°-around-Z mirrors of each other (Lenovo's
                // mechanical design), which means the raw X and Y readings
                // point in opposite directions on the two sides for the same
                // physical motion. Without per-side signs, switching gyro
                // source from Left to Right (or back) flips the apparent
                // camera direction, which is why Invert X/Y toggle needs
                // kept shifting across iterations. With these signs each
                // side reports identically in the device frame, the Invert
                // toggles default OFF, and Mixed mode merge averages two
                // matching-sign samples instead of cancelling.
                //
                // Left: X negated, Y negated.  Right: X positive, Y positive.
                // (Both produce horizontal = -device_Y, vertical = -device_X
                // when fed through Mode 0 with no Invert toggle — matches the
                // empirical "all sources needed Invert X" feedback by folding
                // the toggle in on both axes' yaw component.)
                // Accel passes through untouched — JSL's gravity tracker
                // uses BMI260's native convention directly.
                if (leftHighQualityActive)
                {
                    short lgX = ReadInt16LittleEndian(buffer, leftBase + 7);
                    short lgY = ReadInt16LittleEndian(buffer, leftBase + 11);
                    short lgZ = ReadInt16LittleEndian(buffer, leftBase + 9);
                    LogGyroSpikeIfAny("L", buffer, leftBase, lgX, lgY, lgZ); // log raw, before filter, so the audit log keeps showing torn reads even after we mask them
                    // See FilterTornGyroValue comment for torn-read background.
                    short flgX = FilterTornGyroValue(lgX, ref _leftPrevGX1, ref _leftPrevGX2, ref _leftConsecGX, ref _leftTornReadSubstitutions);
                    short flgY = FilterTornGyroValue(lgY, ref _leftPrevGY1, ref _leftPrevGY2, ref _leftConsecGY, ref _leftTornReadSubstitutions);
                    short flgZ = FilterTornGyroValue(lgZ, ref _leftPrevGZ1, ref _leftPrevGZ2, ref _leftConsecGZ, ref _leftTornReadSubstitutions);
                    leftSample = new LegionGyroSample(
                        -flgX * GYRO_SCALE_DEG_PER_SECOND,
                        -flgY * GYRO_SCALE_DEG_PER_SECOND,
                         flgZ * GYRO_SCALE_DEG_PER_SECOND,
                        ReadInt16LittleEndian(buffer, leftBase + 1) * ACCEL_SCALE_G,
                        ReadInt16LittleEndian(buffer, leftBase + 5) * ACCEL_SCALE_G,
                        ReadInt16LittleEndian(buffer, leftBase + 3) * ACCEL_SCALE_G,
                        sampleTimestampUtc);
                    hasLeftSample = true;
                }

                if (rightHighQualityActive)
                {
                    short rgX = ReadInt16LittleEndian(buffer, rightBase + 9);
                    short rgY = ReadInt16LittleEndian(buffer, rightBase + 11);
                    short rgZ = ReadInt16LittleEndian(buffer, rightBase + 7);
                    LogGyroSpikeIfAny("R", buffer, rightBase, rgX, rgY, rgZ);
                    short frgX = FilterTornGyroValue(rgX, ref _rightPrevGX1, ref _rightPrevGX2, ref _rightConsecGX, ref _rightTornReadSubstitutions);
                    short frgY = FilterTornGyroValue(rgY, ref _rightPrevGY1, ref _rightPrevGY2, ref _rightConsecGY, ref _rightTornReadSubstitutions);
                    short frgZ = FilterTornGyroValue(rgZ, ref _rightPrevGZ1, ref _rightPrevGZ2, ref _rightConsecGZ, ref _rightTornReadSubstitutions);
                    rightSample = new LegionGyroSample(
                        frgX * GYRO_SCALE_DEG_PER_SECOND,
                        frgY * GYRO_SCALE_DEG_PER_SECOND,
                        frgZ * GYRO_SCALE_DEG_PER_SECOND,
                        ReadInt16LittleEndian(buffer, rightBase + 3) * ACCEL_SCALE_G,
                        ReadInt16LittleEndian(buffer, rightBase + 5) * ACCEL_SCALE_G,
                        ReadInt16LittleEndian(buffer, rightBase + 1) * ACCEL_SCALE_G,
                        sampleTimestampUtc);
                    hasRightSample = true;
                }
            }

            // Fallback to legacy low-quality 8-bit gyro bytes when HQ stream is not active.
            // Legacy layout is present in both 0xA1 and 0x74 reports at bytes 28..31:
            // left=(28,29), right=(30,31), centered around 0x80.
            if (bytesRead > 31)
            {
                if (!hasLeftSample)
                {
                    leftSample = new LegionGyroSample(
                        ((int)buffer[29] - 128) * LEGACY_M8_GYRO_SCALE_DEG_PER_SECOND,
                        ((int)buffer[28] - 128) * LEGACY_M8_GYRO_SCALE_DEG_PER_SECOND,
                        0.0f,
                        0.0f,
                        0.0f,
                        0.0f,
                        sampleTimestampUtc);
                    hasLeftSample = true;
                }

                if (!hasRightSample)
                {
                    rightSample = new LegionGyroSample(
                        ((int)buffer[31] - 128) * LEGACY_M8_GYRO_SCALE_DEG_PER_SECOND,
                        ((int)buffer[30] - 128) * LEGACY_M8_GYRO_SCALE_DEG_PER_SECOND,
                        0.0f,
                        0.0f,
                        0.0f,
                        0.0f,
                        sampleTimestampUtc);
                    hasRightSample = true;
                }
            }

            if (!hasLeftSample && !hasRightSample)
            {
                if (!hasGamepadSample && !hasRightTouchSample)
                {
                    return;
                }
            }

            lock (_gyroSampleLock)
            {
                if (hasGamepadSample)
                {
                    _latestGamepadSample = gamepadSample;
                    _hasGamepadSample = true;
                }

                if (hasLeftSample)
                {
                    _latestLeftGyroSample = leftSample;
                    _hasLeftGyroSample = true;
                }

                if (hasRightSample)
                {
                    _latestRightGyroSample = rightSample;
                    _hasRightGyroSample = true;
                }

                if (hasRightTouchSample)
                {
                    _latestRightTouchpadSample = rightTouchSample;
                    _hasRightTouchpadSample = true;
                }
            }

            // Wake any consumer thread blocked on WaitForNewSample. AutoReset
            // releases exactly one waiter per Set, but if both VIIPER + legacy
            // CE forwarders are waiting only one wakes per HID report — fine,
            // they both share the cache and either can advance the pipeline.
            _newSampleEvent.Set();

            // Emit button press-edge events (outside the sample lock so handlers can't
            // stall the parse path). Only when a fresh gamepad sample was parsed.
            if (hasGamepadSample)
            {
                DetectButtonEdges(gamepadSample);
            }
        }

        /// <summary>
        /// Diff the current gamepad sample against the previous one and raise a
        /// <see cref="ButtonEdge"/> event for every button that changed state. Runs on the
        /// HID poll thread; handlers must be fast. Triggers are edge-detected against a
        /// digital threshold so a full pull fires exactly one press event.
        /// </summary>
        private static void DetectButtonEdges(LegionGamepadSample sample)
        {
            var handler = ButtonEdge;

            ushort buttons = sample.Buttons;
            ushort aux = sample.AuxButtons;
            bool lt = sample.LeftTrigger >= EdgeTriggerThreshold;
            bool rt = sample.RightTrigger >= EdgeTriggerThreshold;

            if (!_hasPrevEdgeState)
            {
                // First sample: seed state without emitting (avoid a burst of phantom
                // presses for whatever happens to be held at startup).
                _prevEdgeButtons = buttons;
                _prevEdgeAux = aux;
                _prevEdgeLeftTrigger = lt;
                _prevEdgeRightTrigger = rt;
                _hasPrevEdgeState = true;
                return;
            }

            if (handler != null)
            {
                ushort bChanged = (ushort)(buttons ^ _prevEdgeButtons);
                if (bChanged != 0)
                {
                    EmitEdge(handler, bChanged, buttons, XINPUT_GAMEPAD_A, LegionInputButton.A);
                    EmitEdge(handler, bChanged, buttons, XINPUT_GAMEPAD_B, LegionInputButton.B);
                    EmitEdge(handler, bChanged, buttons, XINPUT_GAMEPAD_X, LegionInputButton.X);
                    EmitEdge(handler, bChanged, buttons, XINPUT_GAMEPAD_Y, LegionInputButton.Y);
                    EmitEdge(handler, bChanged, buttons, XINPUT_GAMEPAD_DPAD_UP, LegionInputButton.DpadUp);
                    EmitEdge(handler, bChanged, buttons, XINPUT_GAMEPAD_DPAD_DOWN, LegionInputButton.DpadDown);
                    EmitEdge(handler, bChanged, buttons, XINPUT_GAMEPAD_DPAD_LEFT, LegionInputButton.DpadLeft);
                    EmitEdge(handler, bChanged, buttons, XINPUT_GAMEPAD_DPAD_RIGHT, LegionInputButton.DpadRight);
                    EmitEdge(handler, bChanged, buttons, XINPUT_GAMEPAD_START, LegionInputButton.Start);
                    EmitEdge(handler, bChanged, buttons, XINPUT_GAMEPAD_BACK, LegionInputButton.Back);
                    EmitEdge(handler, bChanged, buttons, XINPUT_GAMEPAD_LEFT_THUMB, LegionInputButton.LeftThumb);
                    EmitEdge(handler, bChanged, buttons, XINPUT_GAMEPAD_RIGHT_THUMB, LegionInputButton.RightThumb);
                    EmitEdge(handler, bChanged, buttons, XINPUT_GAMEPAD_LEFT_SHOULDER, LegionInputButton.LeftShoulder);
                    EmitEdge(handler, bChanged, buttons, XINPUT_GAMEPAD_RIGHT_SHOULDER, LegionInputButton.RightShoulder);
                }

                ushort aChanged = (ushort)(aux ^ _prevEdgeAux);
                if (aChanged != 0)
                {
                    EmitEdge(handler, aChanged, aux, LEGION_AUX_MODE, LegionInputButton.Mode);
                    EmitEdge(handler, aChanged, aux, LEGION_AUX_SHARE, LegionInputButton.Share);
                    EmitEdge(handler, aChanged, aux, LEGION_AUX_EXTRA_L1, LegionInputButton.ExtraL1);
                    EmitEdge(handler, aChanged, aux, LEGION_AUX_EXTRA_L2, LegionInputButton.ExtraL2);
                    EmitEdge(handler, aChanged, aux, LEGION_AUX_EXTRA_R1, LegionInputButton.ExtraR1);
                    EmitEdge(handler, aChanged, aux, LEGION_AUX_EXTRA_RM1, LegionInputButton.ExtraRM1);
                    EmitEdge(handler, aChanged, aux, LEGION_AUX_EXTRA_R2, LegionInputButton.ExtraR2);
                    EmitEdge(handler, aChanged, aux, LEGION_AUX_EXTRA_R3, LegionInputButton.ExtraR3);
                }

                if (lt != _prevEdgeLeftTrigger)
                {
                    handler(null, new LegionButtonEdgeEventArgs(LegionInputButton.LeftTrigger, LegionButtonGroup.Trigger, lt));
                }
                if (rt != _prevEdgeRightTrigger)
                {
                    handler(null, new LegionButtonEdgeEventArgs(LegionInputButton.RightTrigger, LegionButtonGroup.Trigger, rt));
                }
            }

            _prevEdgeButtons = buttons;
            _prevEdgeAux = aux;
            _prevEdgeLeftTrigger = lt;
            _prevEdgeRightTrigger = rt;
        }

        private static void EmitEdge(EventHandler<LegionButtonEdgeEventArgs> handler,
                                     ushort changedMask, ushort currentMask,
                                     ushort bit, LegionInputButton button)
        {
            if ((changedMask & bit) == 0) return;
            bool pressed = (currentMask & bit) != 0;
            handler(null, new LegionButtonEdgeEventArgs(button, LegionButtonClassifier.GroupOf(button), pressed));
        }

        private static bool TryParseGamepadSample(
            byte[] buffer,
            uint bytesRead,
            bool isInitializedHeader,
            long sampleTimestampUtc,
            bool isGo2TabletController,
            out LegionGamepadSample sample)
        {
            sample = default;

            int sticksBase = isInitializedHeader ? 12 : 14;
            int buttonsBase = isInitializedHeader ? 16 : 18;
            int leftTriggerIndex = isInitializedHeader ? 20 : 22;
            int rightTriggerIndex = isInitializedHeader ? 21 : 23;

            if (bytesRead <= sticksBase + 3 ||
                bytesRead <= buttonsBase + 2 ||
                bytesRead <= rightTriggerIndex ||
                bytesRead <= leftTriggerIndex)
            {
                return false;
            }

            byte leftStickXRaw = buffer[sticksBase];
            byte leftStickYRaw = buffer[sticksBase + 1];
            byte rightStickXRaw = buffer[sticksBase + 2];
            byte rightStickYRaw = buffer[sticksBase + 3];

            byte buttonByte0 = buffer[buttonsBase];
            byte buttonByte1 = buffer[buttonsBase + 1];
            byte buttonByte2 = buffer[buttonsBase + 2];

            ushort buttons = 0;

            // Byte 0: mode/share/ls/rs/dpad.
            // HHD tablet BM map: bit offsets 2/3/4/5/6/7 => masks 0x20/0x10/0x08/0x04/0x02/0x01.
            if ((buttonByte0 & 0x20) != 0) buttons |= XINPUT_GAMEPAD_LEFT_THUMB;
            if ((buttonByte0 & 0x10) != 0) buttons |= XINPUT_GAMEPAD_RIGHT_THUMB;
            if ((buttonByte0 & 0x08) != 0) buttons |= XINPUT_GAMEPAD_DPAD_UP;
            if ((buttonByte0 & 0x04) != 0) buttons |= XINPUT_GAMEPAD_DPAD_DOWN;
            if ((buttonByte0 & 0x02) != 0) buttons |= XINPUT_GAMEPAD_DPAD_LEFT;
            if ((buttonByte0 & 0x01) != 0) buttons |= XINPUT_GAMEPAD_DPAD_RIGHT;

            // Byte 1: ABXY + bumpers + digital trigger flags.
            // HHD tablet BM map uses MSB-first bit addressing.
            if ((buttonByte1 & 0x80) != 0) buttons |= XINPUT_GAMEPAD_A;
            if ((buttonByte1 & 0x40) != 0) buttons |= XINPUT_GAMEPAD_B;
            if ((buttonByte1 & 0x20) != 0) buttons |= XINPUT_GAMEPAD_X;
            if ((buttonByte1 & 0x10) != 0) buttons |= XINPUT_GAMEPAD_Y;
            if ((buttonByte1 & 0x08) != 0) buttons |= XINPUT_GAMEPAD_LEFT_SHOULDER;
            if ((buttonByte1 & 0x02) != 0) buttons |= XINPUT_GAMEPAD_RIGHT_SHOULDER;

            // Byte 2: extra buttons + start/select.
            if ((buttonByte2 & 0x02) != 0) buttons |= XINPUT_GAMEPAD_BACK;
            if ((buttonByte2 & 0x01) != 0) buttons |= XINPUT_GAMEPAD_START;

            ushort auxButtons = 0;
            if (isGo2TabletController)
            {
                // Legion Go 2 front buttons are reported on byte #20 (1-based) in 04:00:A1 reports.
                // In initialized mode this is absolute index 19. Detached mode keeps a +2 layout shift.
                int frontButtonsIndex = isInitializedHeader ? 19 : buttonsBase + 3;
                if (frontButtonsIndex >= 0 && bytesRead > frontButtonsIndex)
                {
                    byte frontButtons = buffer[frontButtonsIndex];
                    if ((frontButtons & 0x40) != 0) auxButtons |= LEGION_AUX_MODE;   // Desktop
                    if ((frontButtons & 0x20) != 0) auxButtons |= LEGION_AUX_SHARE;  // Page
                }
            }
            else
            {
                if ((buttonByte0 & 0x80) != 0) auxButtons |= LEGION_AUX_MODE;
                if ((buttonByte0 & 0x40) != 0) auxButtons |= LEGION_AUX_SHARE;
            }

            if ((buttonByte2 & 0x80) != 0) auxButtons |= LEGION_AUX_EXTRA_L1;
            if ((buttonByte2 & 0x40) != 0) auxButtons |= LEGION_AUX_EXTRA_L2;
            if ((buttonByte2 & 0x20) != 0) auxButtons |= LEGION_AUX_EXTRA_R1;
            // 0x10 is reserved in HHD maps; on Legion Go 1/2 this carries the sixth remappable extra key (M1).
            if ((buttonByte2 & 0x10) != 0) auxButtons |= LEGION_AUX_EXTRA_RM1;
            if ((buttonByte2 & 0x04) != 0) auxButtons |= LEGION_AUX_EXTRA_R2;
            if ((buttonByte2 & 0x08) != 0) auxButtons |= LEGION_AUX_EXTRA_R3;

            byte rightTrigger = buffer[rightTriggerIndex];
            byte leftTrigger = buffer[leftTriggerIndex];

            sample = new LegionGamepadSample(
                buttons,
                leftTrigger,
                rightTrigger,
                ScaleStickByteToXInput(leftStickXRaw, false),
                ScaleStickByteToXInput(leftStickYRaw, true),
                ScaleStickByteToXInput(rightStickXRaw, false),
                ScaleStickByteToXInput(rightStickYRaw, true),
                auxButtons,
                sampleTimestampUtc);

            return true;
        }

        private static short ScaleStickByteToXInput(byte raw, bool invert)
        {
            if (raw == 128)
            {
                return 0;
            }

            float normalized;
            if (raw > 128)
            {
                normalized = (raw - 128) / 127.0f;
            }
            else
            {
                normalized = -((128 - raw) / 128.0f);
            }

            if (invert)
            {
                normalized = -normalized;
            }

            int scaled = (int)Math.Round(normalized * short.MaxValue);
            if (scaled > short.MaxValue)
            {
                scaled = short.MaxValue;
            }
            else if (scaled < short.MinValue)
            {
                scaled = short.MinValue;
            }

            return (short)scaled;
        }

        // Diagnostic: when an HQ-parsed gyro reading is implausibly large (device should be
        // at rest most of the time, and even held-handheld motion rarely exceeds a couple
        // hundred deg/s), dump the raw int16 and the surrounding 16-byte HQ block so we can
        // see which byte is going wild. Rate-limited to once per 5 s per side so logs stay sane.
        private const double GYRO_SPIKE_THRESHOLD_RAW = 200.0; // raw int16 ~= 12 deg/s
        private static long _lastSpikeLogLeftTicks;
        private static long _lastSpikeLogRightTicks;

        // Torn-read filter state. The Legion firmware occasionally publishes a non-atomic gyro
        // register read in the HID report (low/high byte updated independently inside the
        // controller MCU before the report is assembled). Accel stays clean across these
        // frames so it is specifically the gyro path. The artifact manifests as an isolated
        // single-frame jump landing at exactly +/-255 or +/-256 raw LSB (the partial-update
        // bit patterns 0x00FF / 0xFF00 / 0xFF01 / 0x0100), surrounded by quiet noise-floor
        // samples. The 2026-05-30 capture audit confirmed this on Diego's Legion Go 2: every
        // spike was an isolated frame, accel was always intact, and the values were always
        // one of the four torn-read int16 patterns. We can't fix the firmware so we filter
        // here. Filter rules:
        //   * Substitute curr with mean(prev1, prev2) only when both neighbours are within
        //     STABLE_LSB (~1.8 deg/s) of each other AND curr differs from each by more than
        //     JUMP_LSB (~6 deg/s). Real motion would push prev1 vs prev2 apart and disarm.
        //   * Bail out after 3 consecutive substitutions on the same axis: that is the
        //     signature of an actual fast motion onset, not a torn read. Letting the raw
        //     value through resyncs the history within 2-3 frames.
        // Net cost on real motion: at most ~24 ms (3 frames at 125 Hz) of onset delay if the
        // user yanks the controller from a dead stop into > 6 deg/s in a single sample, which
        // is well below the noise floor of human-perceptible aim latency.
        private const int TORN_READ_JUMP_LSB = 100;
        private const int TORN_READ_STABLE_LSB = 30;
        private const int TORN_READ_MAX_CONSEC = 3;
        private static short _leftPrevGX1, _leftPrevGX2, _leftPrevGY1, _leftPrevGY2, _leftPrevGZ1, _leftPrevGZ2;
        private static short _rightPrevGX1, _rightPrevGX2, _rightPrevGY1, _rightPrevGY2, _rightPrevGZ1, _rightPrevGZ2;
        private static byte _leftConsecGX, _leftConsecGY, _leftConsecGZ;
        private static byte _rightConsecGX, _rightConsecGY, _rightConsecGZ;
        private static long _leftTornReadSubstitutions;
        private static long _rightTornReadSubstitutions;

        private static short FilterTornGyroValue(short curr, ref short prev1, ref short prev2, ref byte consec, ref long substCounter)
        {
            short result = curr;
            if (consec < TORN_READ_MAX_CONSEC
                && Math.Abs(curr - prev1) > TORN_READ_JUMP_LSB
                && Math.Abs(curr - prev2) > TORN_READ_JUMP_LSB
                && Math.Abs(prev1 - prev2) < TORN_READ_STABLE_LSB)
            {
                result = (short)((prev1 + prev2) / 2);
                substCounter++;
                consec++;
            }
            else
            {
                consec = 0;
            }
            prev2 = prev1;
            prev1 = result;
            return result;
        }
        private static void LogGyroSpikeIfAny(string sideTag, byte[] buffer, int baseOffset, short gX, short gY, short gZ)
        {
            int aX = Math.Abs(gX), aY = Math.Abs(gY), aZ = Math.Abs(gZ);
            int worst = aX > aY ? aX : aY;
            if (aZ > worst) worst = aZ;
            if (worst < GYRO_SPIKE_THRESHOLD_RAW) return;
            long now = DateTime.UtcNow.Ticks;
            ref long last = ref (sideTag == "L" ? ref _lastSpikeLogLeftTicks : ref _lastSpikeLogRightTicks);
            if (now - last < TimeSpan.TicksPerSecond * 5) return;
            last = now;
            int end = Math.Min(buffer.Length, baseOffset + 13);
            var sb = new System.Text.StringBuilder(96);
            for (int i = baseOffset; i < end; i++)
            {
                if (i > baseOffset) sb.Append(' ');
                sb.Append(buffer[i].ToString("X2"));
            }
            int pre = Math.Max(0, baseOffset - 4);
            var preSb = new System.Text.StringBuilder(16);
            for (int i = pre; i < baseOffset; i++)
            {
                if (i > pre) preSb.Append(' ');
                preSb.Append(buffer[i].ToString("X2"));
            }
            Logger.Info($"GyroSpike[{sideTag}] base={baseOffset} gX={gX} gY={gY} gZ={gZ} | pre[{pre}..{baseOffset - 1}]={preSb} block[{baseOffset}..{end - 1}]={sb}");
        }

        private static bool HasHighQualityImuSample(byte[] buffer, int baseOffset)
        {
            if (buffer == null || baseOffset < 0 || baseOffset + 12 >= buffer.Length)
            {
                return false;
            }

            // HQ sample is considered active if timestamp or any sample field is non-zero.
            for (int i = 0; i <= 12; i++)
            {
                if (buffer[baseOffset + i] != 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static short ReadInt16LittleEndian(byte[] buffer, int offset)
        {
            if (buffer == null || offset < 0 || offset + 1 >= buffer.Length)
            {
                return 0;
            }

            int low = buffer[offset];
            int high = buffer[offset + 1];
            return unchecked((short)(low | (high << 8)));
        }

        // Big-endian sibling — used by the IMU parser. The Legion controller
        // HID layout encodes IMU samples as (short)(data[i] << 8 | data[i+1]),
        // i.e. BE: high byte first.
        private static short ReadInt16BigEndian(byte[] buffer, int offset)
        {
            if (buffer == null || offset < 0 || offset + 1 >= buffer.Length)
            {
                return 0;
            }

            int high = buffer[offset];
            int low = buffer[offset + 1];
            return unchecked((short)((high << 8) | low));
        }

        private static bool TryCommitDebouncedButtonState(
            bool rawState,
            ref bool stableState,
            ref bool? pendingState,
            ref DateTime pendingSinceUtc,
            out bool committedState)
        {
            committedState = stableState;

            if (rawState == stableState)
            {
                pendingState = null;
                pendingSinceUtc = DateTime.MinValue;
                return false;
            }

            DateTime nowUtc = DateTime.UtcNow;
            if (!pendingState.HasValue || pendingState.Value != rawState)
            {
                pendingState = rawState;
                pendingSinceUtc = nowUtc;
                return false;
            }

            int debounceMs = rawState
                ? LEGION_BUTTON_PRESS_DEBOUNCE_MS
                : LEGION_BUTTON_RELEASE_DEBOUNCE_MS;

            if ((nowUtc - pendingSinceUtc).TotalMilliseconds < debounceMs)
            {
                return false;
            }

            stableState = rawState;
            committedState = rawState;
            pendingState = null;
            pendingSinceUtc = DateTime.MinValue;
            return true;
        }

        /// <summary>
        /// Process button press/release and execute the configured action.
        /// </summary>
        private void ProcessButtonAction(string buttonName, bool pressed, LegionButtonAction actionType,
            string shortcutKeys, string commandPath)
        {
            Logger.Info($"LegionButtonMonitor: {buttonName} {(pressed ? "PRESSED" : "RELEASED")} - action={actionType}");

            try
            {
                onButtonStateChanged?.Invoke(pressed);
            }
            catch (Exception ex)
            {
                Logger.Error($"LegionButtonMonitor: onButtonStateChanged exception: {ex.Message}");
            }

            if (pressed)
            {
                // Button pressed - perform the configured action
                switch (actionType)
                {
                    case LegionButtonAction.XboxGuide:
                        // VIIPER backend intercept: when the VIIPER forwarder is active, route
                        // the press through its Guide-button mode (Native → emulated Guide press
                        // held while physically held, GameBar → Win+G). Skip the legacy ViGEm
                        // path when VIIPER consumes it.
                        if (XboxGamingBarHelper.ControllerEmulation.Viiper.ViiperInputForwarder.TryHandleGuideButtonFromLabs(true))
                        {
                            Logger.Info($"LegionButtonMonitor: Routed XboxGuide press to VIIPER backend for {buttonName}");
                            break;
                        }

                        if (XboxGamingBarHelper.ControllerEmulation.Viiper.ViiperEmulationManager.TrySetGuideFromLabs(true))
                        {
                            Logger.Info($"LegionButtonMonitor: Routed XboxGuide press to VIIPER guide-only pad for {buttonName}");
                            break;
                        }

                        if (ControllerEmulationManager.TrySetGuideFromExternal(true))
                        {
                            Logger.Info($"LegionButtonMonitor: Routed SetGuide(true) to controller emulation virtual pad for {buttonName}");
                            break;
                        }

                        // No further fallback — the dedicated ViGEm pad was retired
                        // (phase 2). All three delivery tiers declined, which means
                        // the VIIPER guide-only pad is offline (usbip-win2 missing);
                        // the setup banner is already pointing the user at it.
                        Logger.Warn($"LegionButtonMonitor: No virtual pad available for Xbox Guide press ({buttonName}) — is usbip-win2 installed?");
                        break;

                    case LegionButtonAction.KeyboardShortcut:
                        if (!string.IsNullOrEmpty(shortcutKeys))
                        {
                            try
                            {
                                onShortcutTriggered?.Invoke(shortcutKeys);
                                Logger.Debug($"LegionButtonMonitor: {buttonName} pressed -> Shortcut '{shortcutKeys}' triggered");
                            }
                            catch (Exception ex)
                            {
                                Logger.Error($"LegionButtonMonitor: Shortcut trigger exception: {ex.Message}");
                            }
                        }
                        break;

                    case LegionButtonAction.RunCommand:
                        if (!string.IsNullOrEmpty(commandPath))
                        {
                            try
                            {
                                onCommandTriggered?.Invoke(commandPath);
                                Logger.Debug($"LegionButtonMonitor: {buttonName} pressed -> Command '{commandPath}' triggered");
                            }
                            catch (Exception ex)
                            {
                                Logger.Error($"LegionButtonMonitor: Command trigger exception: {ex.Message}");
                            }
                        }
                        break;

                    case LegionButtonAction.FocusGoTweaks:
                        try
                        {
                            onFocusGoTweaksTriggered?.Invoke();
                            Logger.Debug($"LegionButtonMonitor: {buttonName} pressed -> Focus GoTweaks triggered");
                        }
                        catch (Exception ex)
                        {
                            Logger.Error($"LegionButtonMonitor: FocusGoTweaks trigger exception: {ex.Message}");
                        }
                        break;
                }
            }
            else
            {
                // Button released - only release Xbox Guide if that's the action
                if (actionType == LegionButtonAction.XboxGuide)
                {
                    // VIIPER intercept for release (match the press path).
                    if (XboxGamingBarHelper.ControllerEmulation.Viiper.ViiperInputForwarder.TryHandleGuideButtonFromLabs(false))
                    {
                        Logger.Info($"LegionButtonMonitor: Routed XboxGuide release to VIIPER backend for {buttonName}");
                        return;
                    }

                    if (XboxGamingBarHelper.ControllerEmulation.Viiper.ViiperEmulationManager.TrySetGuideFromLabs(false))
                    {
                        Logger.Info($"LegionButtonMonitor: Routed XboxGuide release to VIIPER guide-only pad for {buttonName}");
                        return;
                    }

                    if (ControllerEmulationManager.TrySetGuideFromExternal(false))
                    {
                        Logger.Info($"LegionButtonMonitor: Routed SetGuide(false) to controller emulation virtual pad for {buttonName}");
                    }
                    // (No ViGEm fallback — dedicated Guide pad retired in phase 2.)
                }
            }

            Logger.Info($"LegionButtonMonitor: ProcessButtonAction completed for {buttonName}");
        }

        public void Dispose()
        {
            if (isDisposed)
                return;

            isDisposed = true;
            Stop();
            Logger.Info("LegionButtonMonitor: Disposed");
        }
    }

    /// <summary>
    /// Event arguments for battery status updates from LegionButtonMonitor.
    /// </summary>
    internal class LegionButtonBatteryEventArgs : EventArgs
    {
        public int LeftBattery { get; }
        public bool LeftCharging { get; }
        public bool LeftConnected { get; }
        public int RightBattery { get; }
        public bool RightCharging { get; }
        public bool RightConnected { get; }

        public LegionButtonBatteryEventArgs(int leftBattery, bool leftCharging, bool leftConnected,
                                            int rightBattery, bool rightCharging, bool rightConnected)
        {
            LeftBattery = leftBattery;
            LeftCharging = leftCharging;
            LeftConnected = leftConnected;
            RightBattery = rightBattery;
            RightCharging = rightCharging;
            RightConnected = rightConnected;
        }
    }


    /// <summary>
    /// Latest parsed Legion controller IMU sample from HID reports.
    /// Units:
    /// - Gyro: degrees per second
    /// - Accelerometer: g
    /// </summary>
    internal readonly struct LegionGyroSample
    {
        public readonly float GyroXDegPerSecond;
        public readonly float GyroYDegPerSecond;
        public readonly float GyroZDegPerSecond;
        public readonly float AccelXG;
        public readonly float AccelYG;
        public readonly float AccelZG;
        public readonly long TimestampTicksUtc;

        public LegionGyroSample(
            float gyroXDegPerSecond,
            float gyroYDegPerSecond,
            float gyroZDegPerSecond,
            float accelXG,
            float accelYG,
            float accelZG,
            long timestampTicksUtc)
        {
            GyroXDegPerSecond = gyroXDegPerSecond;
            GyroYDegPerSecond = gyroYDegPerSecond;
            GyroZDegPerSecond = gyroZDegPerSecond;
            AccelXG = accelXG;
            AccelYG = accelYG;
            AccelZG = accelZG;
            TimestampTicksUtc = timestampTicksUtc;
        }
    }

    /// <summary>
    /// Latest parsed Legion right-controller touchpad sample from HID reports.
    /// Raw coordinates are device-native (little-endian uint16 pair from report bytes).
    /// </summary>
    internal readonly struct LegionTouchpadSample
    {
        public readonly bool IsTouching;
        public readonly bool IsPressed;
        public readonly ushort RawX;
        public readonly ushort RawY;
        public readonly long TimestampTicksUtc;

        public LegionTouchpadSample(
            bool isTouching,
            bool isPressed,
            ushort rawX,
            ushort rawY,
            long timestampTicksUtc)
        {
            IsTouching = isTouching;
            IsPressed = isPressed;
            RawX = rawX;
            RawY = rawY;
            TimestampTicksUtc = timestampTicksUtc;
        }
    }

    /// <summary>
    /// Latest parsed Legion gamepad sample mapped to XInput-compatible fields.
    /// </summary>
    internal readonly struct LegionGamepadSample
    {
        public readonly ushort Buttons;
        public readonly byte LeftTrigger;
        public readonly byte RightTrigger;
        public readonly short LeftStickX;
        public readonly short LeftStickY;
        public readonly short RightStickX;
        public readonly short RightStickY;
        public readonly ushort AuxButtons;
        public readonly long TimestampTicksUtc;

        public LegionGamepadSample(
            ushort buttons,
            byte leftTrigger,
            byte rightTrigger,
            short leftStickX,
            short leftStickY,
            short rightStickX,
            short rightStickY,
            ushort auxButtons,
            long timestampTicksUtc)
        {
            Buttons = buttons;
            LeftTrigger = leftTrigger;
            RightTrigger = rightTrigger;
            LeftStickX = leftStickX;
            LeftStickY = leftStickY;
            RightStickX = rightStickX;
            RightStickY = rightStickY;
            AuxButtons = auxButtons;
            TimestampTicksUtc = timestampTicksUtc;
        }
    }
}
