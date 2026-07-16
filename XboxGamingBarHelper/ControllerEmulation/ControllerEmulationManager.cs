using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Shared.Data;
using Shared.Enums;
using XboxGamingBarHelper.Core;
using XboxGamingBarHelper.Devices;
using XboxGamingBarHelper.Devices.Libraries.GPD;
using XboxGamingBarHelper.Devices.Libraries.Legion;
using XboxGamingBarHelper.Labs;
using XboxGamingBarHelper.Settings;
using XboxGamingBarHelper.Windows;
using SharedDeviceType = Shared.Enums.DeviceType;

namespace XboxGamingBarHelper.ControllerEmulation
{
    /// <summary>
    /// Handheld-agnostic controller emulation settings manager.
    /// This manager owns the shared gyro source + emulation mode settings and
    /// forwards configuration to the active device backend.
    /// </summary>
    internal partial class ControllerEmulationManager : Manager
    {
        private static ControllerEmulationManager activeInstance;

        private readonly SharedDeviceType deviceType;
        private readonly bool isSupported;
        private readonly SettingsManager settingsManager;
        private readonly LegionManager legionManager;

        private bool enabled;
        // Runtime-only flag — when true, the VIIPER emulation backend has taken over and
        // this legacy manager must not forward input. Not persisted: the user's saved
        // `enabled` value is preserved for when VIIPER is toggled back off.
        private bool suppressedByViiper;
        private int gyroSource;
        private int mode;
        private int rumbleProfile;
        private int gyroActivationMode;
        private int gyroActivationButton;
        private int ds4Orientation;
        private int mouseSensitivity;
        private int mouseThreshold;
        private int mouseAxis;
        private bool mouseInvertX;
        private bool mouseInvertY;
        private int mouseGainX;
        private int mouseGainY;
        private int stickSensitivity;  // legacy, kept for LoadSettings compat
        private int stickThreshold;    // legacy
        private int stickAxis;         // legacy
        private bool stickInvertX;
        private bool stickInvertY;
        private int stickGainX;        // legacy
        private int stickGainY;        // legacy
        private int stickSelect;
        private bool stickExcessMove;  // legacy
        private int stickRange;        // legacy
        private bool stickOnlyJoystickData;
        // Stick v2 fields
        private int stickMinGyroSpeed;
        private int stickMaxGyroSpeed;
        private int stickMinOutput;
        private int stickMaxOutput;
        private int stickPowerCurve;
        private int stickSensitivityV2;
        private int stickDeadzone;
        private int stickPrecisionSpeed;
        private int stickOutputMix;
        private int stickOrientationV2;
        private int stickConversion;
        // Anti-deadzone settings — tunable from the widget. Defaults match
        // the previously-hardcoded constants (10% min stick deflection,
        // 0.5°/s gyro noise floor) so existing users see no behavior change.
        private int stickGyroAntiDeadzone = 10;          // percent of stick range (0-30)
        private int stickGyroAntiDeadzoneThreshold = 3;  // 0.1 deg/s units (0-50 → 0.0-5.0 deg/s) — 0.3°/s = post-calibration BMI260 noise floor
        private int stickGyroVerticalRatio = 100;        // % of master sensitivity (10-200, 100 = same as horizontal)
        private int stickGyroCurvePreset = 0;            // 0=Linear, 1=Slow-and-precise, 2=Snap-aim
        private int stickGyroTightenThreshold = 0;       // deg/s above which fast-zone gain ramps in (0 = off)
        private int stickGyroTightenGain = 100;          // % gain at full ramp (100 = no boost, 200 = 2×)
        private bool stickGyroTouchDeactivateEnabled = false;
        private int stickGyroTouchDeactivateThreshold = 15; // % of stick range
        private int stickGyroTouchDeactivateHoldoff = 250;  // ms hold-off after stick returns
        private int stickGyroSmoothing = 30;                // EMA alpha % (0=off, 90=heavy)
        private float smoothedGyroXState;
        private float smoothedGyroYState;
        private float smoothedGyroZState;
        private bool smoothedGyroPrimed;
        private int virtualAbxyLayout;
        private bool hideStockController;
        private int hideTarget;
        private bool improvedInputRead;
        private bool ps4TouchpadEnabled;
        private bool ledForwardingEnabled;
        private byte lastForwardedLedR;
        private byte lastForwardedLedG;
        private byte lastForwardedLedB;
        private bool hasForwardedLed;

        private readonly ControllerSuppressionManager suppressionManager;

        /// <summary>
        /// Exposes the internal HidHide suppression manager so other emulation backends
        /// (VIIPER) can share the same underlying hide/unhide infrastructure and avoid
        /// fighting each other over the same physical device IDs.
        /// </summary>
        internal ControllerSuppressionManager SuppressionManager => suppressionManager;

        /// <summary>The detected handheld device type (Legion Go, GPD, etc.).</summary>
        internal SharedDeviceType HandheldDeviceType => deviceType;

        /// <summary>Current HideTarget setting (0=all gamepads, 1=matching VID:PID only, etc.).</summary>
        internal int HideTarget => hideTarget;

        /// <summary>
        /// Master "Enable Controller Emulation" switch from the widget's Controller Emulation
        /// card. The VIIPER backend observes this so flipping the backend-selector toggle in
        /// Debug doesn't auto-start VIIPER — the user still has to enable emulation explicitly.
        /// </summary>
        internal bool EmulationEnabled => enabled;

        /// <summary>
        /// Fires when <see cref="EmulationEnabled"/> changes. Subscribed by the VIIPER backend.
        /// </summary>
        internal event Action<bool> EmulationEnabledChanged;

        private void RaiseEmulationEnabledChanged()
        {
            try { EmulationEnabledChanged?.Invoke(enabled); }
            catch (Exception ex) { Logger.Warn($"EmulationEnabledChanged handler threw: {ex.Message}"); }

            // Tell Labs/LegionButtonMonitor to reconsider whether a dedicated Guide-only
            // ViGEm pad is still needed (the legacy backend may now own the Guide route).
            try { Program.NotifyGuideRouteChanged(); }
            catch (Exception ex) { Logger.Debug($"Program.NotifyGuideRouteChanged threw: {ex.Message}"); }
        }
        private IGyroSourceAdapter gyroSourceAdapter;
        private Thread forwardingThread;
        private volatile bool forwardingRunning;
        private int? virtualXboxUserIndex;
        private int? physicalXboxUserIndex;
        private bool suppressionActive;
        private bool suppressionPausedForGameBar;
        private long suppressionPauseUntilTicksUtc;
        private bool hasWidgetForegroundSignal;
        private bool widgetForegroundSignal;
        private int gameBarForegroundConsecutiveTicks;
        private int nonGameBarForegroundConsecutiveTicks;
        private readonly HashSet<string> virtualXboxBridgeDeviceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly object rumbleSync = new object();
        private readonly uint[] lastPacketByController = new uint[4];
        private long lastLegionHidSampleTimestampTicksUtc;
        private uint legionHidPacketNumber;
        private byte lastRumbleLargeMotor;
        private byte lastRumbleSmallMotor;
        private long lastRumbleDispatchTicksUtc;
        private int lastLegionRumbleLevel = -1;
        private long lastLegionRumbleSetTicksUtc;
        private float mouseCarryX;
        private float mouseCarryY;
        private float mouseFilteredHorizontal;
        private float mouseFilteredVertical;
        private float mouseFilteredDerivativeHorizontal;
        private float mouseFilteredDerivativeVertical;
        private bool mouseFilterInitialized;
        private long mouseLastSampleTicksUtc;
        private float stickFilteredHorizontal;
        private float stickFilteredVertical;
        private float stickFilteredDerivativeHorizontal;
        private float stickFilteredDerivativeVertical;
        private bool stickFilterInitialized;
        private long stickLastSampleTicksUtc;
        private long stickLastDiagLogTicksUtc;
        private short lastGyroStickX;
        private short lastGyroStickY;
        private bool hasLastGyroStick;
        private long lastGyroStickTicksUtc;
        private bool gyroToggleActive;
        private bool lastGyroActivationButtonPressed;
        private readonly INPUT[] mouseMoveInputBuffer = new INPUT[1];
        private readonly INPUT[] keyboardInputBuffer = new INPUT[1];
        private readonly INPUT[] mouseButtonInputBuffer = new INPUT[1];
        private readonly LegionUserspaceRemapEntry[] legionUserspaceRemaps = new LegionUserspaceRemapEntry[8];
        private readonly bool[] legionUserspaceRemapPressed = new bool[8];
        private readonly bool[] legionUserspaceTurboOutputActive = new bool[8];
        private readonly long[] legionUserspaceTurboNextToggleTicksUtc = new long[8];
        private long legionUserspaceRemapCacheTicksUtc;
        private int touchDiagLogCounter;
        private int gyroDiagLogCounter;

        // Issue #79: vvalente30 reports legacy CE produces no input on his LGO2
        // even though ViGEm connects + forwarding loop starts. These counters
        // emit a 5s stats line so the next log shows whether reads succeeded
        // (and from which source) or always returned no-data.
        private long forwardingStatsLastEmitTicksUtc;
        private int forwardingIterations;
        private int forwardingReadOkXInput;
        private int forwardingReadOkLegionHid;
        private int forwardingReadFail;

        // Issue #79 round 4: vvalente30 reports legacy CE gyro doesn't reach
        // games after the #5 source swap, but his test windows were too short
        // to confirm whether ApplyStickFromGyro produced non-zero output. Mirror
        // the VIIPER processor's stats shape so a single grep covers both
        // backends and the next capture tells us which step is failing.
        // gyroMerged increments when the gyro→stick output is non-zero AND
        // gets merged into the forwarded stick; gyroNoSample when activation
        // is on but TryReadGyroSample returned false; gyroGateOff when the
        // activation mode/button gate kept gyro disabled.
        private int forwardingGyroMerged;
        private int forwardingGyroNoSample;
        private int forwardingGyroGateOff;

        // FR-1 (issue #79): allow M1/M2/M3/Y1/Y2/Y3 to be picked as the gyro
        // activation button. The XInput gamepad struct has no fields for these,
        // so cache the last LegionHid sample's AuxButtons here and consult it
        // from IsGyroActivationButtonPressed for the new indices.
        private ushort lastLegionAuxButtons;

        // Issue #79 round 4: residual IMU bias produces "camera drifts on its own"
        // in Always-On gyro→stick. The estimator subtracts the rest-state bias
        // before the deadzone so a 5°/s sensor offset doesn't survive as a 1.4%
        // persistent stick deflection. Shared with the VIIPER stick-gyro
        // processor — each pipeline owns its own instance because filter state
        // shouldn't cross backend swaps.
        private readonly GyroBiasEstimator stickGyroBiasEstimator = new GyroBiasEstimator();

        // Sensor-fusion state for the Player Space conversion mode (#79 round 5).
        // Wraps Jibb Smart's GamepadMotionHelpers via P/Invoke. The native
        // library maintains a fused gravity/orientation quaternion using gyro
        // and accel together — much more stable than a low-pass on accel alone.
        // Updated every stick-gyro tick so flipping to Player Space mid-session
        // is instant.
        private readonly GamepadMotion stickGamepadMotion = new GamepadMotion();
        private long stickGamepadMotionLastTicksUtc;

        private const int ForwardingIntervalMs = 4;
        private const uint ERROR_SUCCESS = 0;
        private const float GyroDs4MaxDegPerSecond = 2000.0f;
        private const float AccelDs4MaxG = 4.0f;
        // DS4 IMU scale factors:
        // Gyro: 16 counts per °/s → 1°/s = 16 raw, max ±2048°/s = ±32768 raw
        // Accel: 8192 counts per G (matches ViiperController's ScaleAccel = raw * 2 from BMI323 4096 LSB/G)
        private const float Ds4GyroCountsPerDps = 16.0f;
        private const float Ds4AccelCountsPerG = 8192.0f;
        // Default accel Z for a controller lying flat = -1G = -8192
        private const short Ds4DefaultAccelZRaw = -8192;
        private const float MousePixelsPerDegree = 24.0f;
        private const float MouseSensitivityPower = 1.35f;
        private const float OneEuroMinCutoff = 1.2f;
        private const float OneEuroBeta = 0.25f;
        private const float OneEuroDerivativeCutoff = 1.5f;
        private const float MouseResidualCutoffDegPerSecond = 0.12f;
        private const float MouseOutlierMaxDeltaDegPerSecond = 420.0f;
        private const float MouseMaxDegPerSecond = 720.0f;
        private const int MouseMaxPixelsPerFrame = 220;
        private const float DefaultDeltaSeconds = 1.0f / 250.0f;
        private const float MinDeltaSeconds = 0.002f;
        private const float MaxDeltaSeconds = 0.05f;
        private const float StickDegreesPerSecondAtFullDeflection = 220.0f;
        private const long StickOutputStaleTicks = TimeSpan.TicksPerSecond / 4; // 250ms
        private const float LegionTouchMaxX = 1023.0f;
        private const float LegionTouchMaxY = 1023.0f;
        private const float Ds4TouchMaxX = 1919.0f;
        private const float Ds4TouchMaxY = 942.0f;
        private const long LegionHidSampleMaxAgeTicks = TimeSpan.TicksPerSecond / 2; // 500ms
        private const long RumbleDispatchMinTicks = TimeSpan.TicksPerMillisecond * 4; // 250Hz max
        private const long LegionRumbleFallbackMinTicks = TimeSpan.TicksPerMillisecond * 350; // Coarse firmware fallback; avoid frequent EC rumble bursts
        private const int GameBarForegroundStableTicks = 1;
        private const int GameBarBackgroundStableTicks = 2;
        private const int GuideSuppressionPauseSeconds = 25;
        // Temporary diagnostic switch: keep suppression control tied to foreground detection only.
        private static readonly bool GuideTriggeredSuppressionPauseEnabled = false;
        private const uint INPUT_MOUSE = 0;
        private const uint INPUT_KEYBOARD = 1;
        private const uint MOUSEEVENTF_MOVE = 0x0001;
        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
        private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
        private const uint MOUSEEVENTF_WHEEL = 0x0800;
        private const uint MOUSEEVENTF_HWHEEL = 0x01000;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
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
        private const byte XINPUT_TRIGGER_THRESHOLD = 30;
        private const ushort LEGION_AUX_MODE = 0x0001;
        private const ushort LEGION_AUX_SHARE = 0x0002;
        private const ushort LEGION_AUX_EXTRA_L1 = 0x0004;
        private const ushort LEGION_AUX_EXTRA_L2 = 0x0008;
        private const ushort LEGION_AUX_EXTRA_R1 = 0x0010;
        private const ushort LEGION_AUX_EXTRA_RM1 = 0x0020;
        private const ushort LEGION_AUX_EXTRA_R2 = 0x0040;
        private const ushort LEGION_AUX_EXTRA_R3 = 0x0080;
        private const long LegionUserspaceRemapRefreshTicks = TimeSpan.TicksPerSecond / 2;
        private const long LegionUserspaceTurboIntervalTicks = TimeSpan.TicksPerMillisecond * 45;
        private const int MouseWheelDelta = 120;
        private static readonly int MouseInputStructSize = Marshal.SizeOf(typeof(INPUT));
        private static readonly HashSet<string> GameBarForegroundProcessNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "GameBar",
                "GameBarFTServer",
                "GameBarElevatedFT",
                "XboxGameBarWidgets",
                // App-mode widget host process (ms-gamebarwidget app package entry point).
                "XboxGamingBar",
                // Legacy/alternate naming seen on some builds.
                "XboxGameBar",
            };

        [StructLayout(LayoutKind.Sequential)]
        private struct XINPUT_GAMEPAD
        {
            public ushort wButtons;
            public byte bLeftTrigger;
            public byte bRightTrigger;
            public short sThumbLX;
            public short sThumbLY;
            public short sThumbRX;
            public short sThumbRY;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct XINPUT_STATE
        {
            public uint dwPacketNumber;
            public XINPUT_GAMEPAD Gamepad;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct XINPUT_VIBRATION
        {
            public ushort wLeftMotorSpeed;
            public ushort wRightMotorSpeed;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public INPUTUNION U;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct INPUTUNION
        {
            [FieldOffset(0)]
            public MOUSEINPUT mi;

            [FieldOffset(0)]
            public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        private enum LegionUserspaceSource
        {
            Y1 = 0,
            Y2 = 1,
            Y3 = 2,
            M1 = 3,
            M2 = 4,
            M3 = 5,
            Desktop = 6,
            Page = 7,
        }

        private struct LegionUserspaceRemapEntry
        {
            public int Type;
            public RemapAction GamepadAction;
            public RemapAction[] GamepadActions;
            public bool TurboEnabled;
            public int[] KeyboardKeys;
            public int MouseButton;
            public bool Enabled;
        }

        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
        private static extern uint XInputGetState14(uint dwUserIndex, ref XINPUT_STATE pState);

        [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")]
        private static extern uint XInputGetState910(uint dwUserIndex, ref XINPUT_STATE pState);

        [DllImport("xinput1_4.dll", EntryPoint = "XInputSetState")]
        private static extern uint XInputSetState14(uint dwUserIndex, ref XINPUT_VIBRATION pVibration);

        [DllImport("xinput9_1_0.dll", EntryPoint = "XInputSetState")]
        private static extern uint XInputSetState910(uint dwUserIndex, ref XINPUT_VIBRATION pVibration);

        [DllImport("user32.dll", SetLastError = false)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        private delegate uint XInputGetStateDelegate(uint dwUserIndex, ref XINPUT_STATE pState);
        private delegate uint XInputSetStateDelegate(uint dwUserIndex, ref XINPUT_VIBRATION pVibration);
        private static XInputGetStateDelegate xInputGetState;
        private static XInputSetStateDelegate xInputSetState;

        public readonly ControllerEmulationAvailableProperty ControllerEmulationAvailable;
        public readonly ControllerEmulationEnabledProperty ControllerEmulationEnabled;
        public readonly ControllerEmulationGyroActivationModeProperty ControllerEmulationGyroActivationMode;
        public readonly ControllerEmulationGyroActivationButtonProperty ControllerEmulationGyroActivationButton;
        public readonly ControllerEmulationStickInvertXProperty ControllerEmulationStickInvertX;
        public readonly ControllerEmulationStickInvertYProperty ControllerEmulationStickInvertY;
        public readonly ControllerEmulationStickSelectProperty ControllerEmulationStickSelect;
        public readonly ControllerEmulationCalibrateGyroProperty ControllerEmulationCalibrateGyro;
        public readonly ControllerEmulationStickSensitivityV2Property ControllerEmulationStickSensitivityV2;
        public readonly ControllerEmulationStickOrientationV2Property ControllerEmulationStickOrientationV2;
        public readonly ControllerEmulationStickConversionProperty ControllerEmulationStickConversion;
        public readonly ControllerEmulationStickGyroAntiDeadzoneProperty ControllerEmulationStickGyroAntiDeadzone;
        public readonly ControllerEmulationStickGyroAntiDeadzoneThresholdProperty ControllerEmulationStickGyroAntiDeadzoneThreshold;
        public readonly ControllerEmulationCalibrateGyroStatusProperty ControllerEmulationCalibrateGyroStatus;
        public readonly ControllerEmulationStickGyroVerticalRatioProperty ControllerEmulationStickGyroVerticalRatio;
        public readonly ControllerEmulationStickGyroCurvePresetProperty ControllerEmulationStickGyroCurvePreset;
        public readonly ControllerEmulationStickGyroTightenThresholdProperty ControllerEmulationStickGyroTightenThreshold;
        public readonly ControllerEmulationStickGyroTightenGainProperty ControllerEmulationStickGyroTightenGain;
        public readonly ControllerEmulationStickGyroTouchDeactivateEnabledProperty ControllerEmulationStickGyroTouchDeactivateEnabled;
        public readonly ControllerEmulationStickGyroTouchDeactivateThresholdProperty ControllerEmulationStickGyroTouchDeactivateThreshold;
        public readonly ControllerEmulationStickGyroTouchDeactivateHoldoffProperty ControllerEmulationStickGyroTouchDeactivateHoldoff;
        public readonly ControllerEmulationStickGyroLiveReadingsProperty ControllerEmulationStickGyroLiveReadings;
        public readonly ControllerEmulationStickGyroSmoothingProperty ControllerEmulationStickGyroSmoothing;

        public IEnumerable<IProperty> Properties
        {
            get
            {
                yield return ControllerEmulationAvailable;
                yield return ControllerEmulationEnabled;
                yield return ControllerEmulationGyroActivationMode;
                yield return ControllerEmulationGyroActivationButton;
                yield return ControllerEmulationStickInvertX;
                yield return ControllerEmulationStickInvertY;
                yield return ControllerEmulationStickSelect;
                yield return ControllerEmulationCalibrateGyro;
                yield return ControllerEmulationStickSensitivityV2;
                yield return ControllerEmulationStickOrientationV2;
                yield return ControllerEmulationStickConversion;
                yield return ControllerEmulationStickGyroAntiDeadzone;
                yield return ControllerEmulationStickGyroAntiDeadzoneThreshold;
                yield return ControllerEmulationCalibrateGyroStatus;
                yield return ControllerEmulationStickGyroVerticalRatio;
                yield return ControllerEmulationStickGyroCurvePreset;
                yield return ControllerEmulationStickGyroTightenThreshold;
                yield return ControllerEmulationStickGyroTightenGain;
                yield return ControllerEmulationStickGyroTouchDeactivateEnabled;
                yield return ControllerEmulationStickGyroTouchDeactivateThreshold;
                yield return ControllerEmulationStickGyroTouchDeactivateHoldoff;
                yield return ControllerEmulationStickGyroLiveReadings;
                yield return ControllerEmulationStickGyroSmoothing;
            }
        }

        public ControllerEmulationManager(LegionManager inLegionManager, GPDManager inGpdManager, SettingsManager inSettingsManager)
        {
            activeInstance = this;
            suppressionManager = new ControllerSuppressionManager();
            settingsManager = inSettingsManager;
            legionManager = inLegionManager;

            var deviceInfo = DeviceDetector.DetectDevice();
            deviceType = deviceInfo?.DeviceType ?? SharedDeviceType.Generic;
            isSupported = IsSupportedDevice(deviceType);

            LoadSettings();

            ControllerEmulationAvailable = new ControllerEmulationAvailableProperty(isSupported, this);
            ControllerEmulationEnabled = new ControllerEmulationEnabledProperty(enabled, this);
            ControllerEmulationGyroActivationMode = new ControllerEmulationGyroActivationModeProperty(gyroActivationMode, this);
            ControllerEmulationGyroActivationButton = new ControllerEmulationGyroActivationButtonProperty(gyroActivationButton, this);
            ControllerEmulationStickInvertX = new ControllerEmulationStickInvertXProperty(stickInvertX, this);
            ControllerEmulationStickInvertY = new ControllerEmulationStickInvertYProperty(stickInvertY, this);
            ControllerEmulationStickSelect = new ControllerEmulationStickSelectProperty(stickSelect, this);
            ControllerEmulationCalibrateGyro = new ControllerEmulationCalibrateGyroProperty(this);
            ControllerEmulationStickSensitivityV2 = new ControllerEmulationStickSensitivityV2Property(stickSensitivityV2, this);
            ControllerEmulationStickOrientationV2 = new ControllerEmulationStickOrientationV2Property(stickOrientationV2, this);
            ControllerEmulationStickConversion = new ControllerEmulationStickConversionProperty(stickConversion, this);
            ControllerEmulationStickGyroAntiDeadzone = new ControllerEmulationStickGyroAntiDeadzoneProperty(stickGyroAntiDeadzone, this);
            ControllerEmulationStickGyroAntiDeadzoneThreshold = new ControllerEmulationStickGyroAntiDeadzoneThresholdProperty(stickGyroAntiDeadzoneThreshold, this);
            ControllerEmulationCalibrateGyroStatus = new ControllerEmulationCalibrateGyroStatusProperty(this);
            ControllerEmulationStickGyroVerticalRatio = new ControllerEmulationStickGyroVerticalRatioProperty(stickGyroVerticalRatio, this);
            ControllerEmulationStickGyroCurvePreset = new ControllerEmulationStickGyroCurvePresetProperty(stickGyroCurvePreset, this);
            ControllerEmulationStickGyroTightenThreshold = new ControllerEmulationStickGyroTightenThresholdProperty(stickGyroTightenThreshold, this);
            ControllerEmulationStickGyroTightenGain = new ControllerEmulationStickGyroTightenGainProperty(stickGyroTightenGain, this);
            ControllerEmulationStickGyroTouchDeactivateEnabled = new ControllerEmulationStickGyroTouchDeactivateEnabledProperty(stickGyroTouchDeactivateEnabled, this);
            ControllerEmulationStickGyroTouchDeactivateThreshold = new ControllerEmulationStickGyroTouchDeactivateThresholdProperty(stickGyroTouchDeactivateThreshold, this);
            ControllerEmulationStickGyroTouchDeactivateHoldoff = new ControllerEmulationStickGyroTouchDeactivateHoldoffProperty(stickGyroTouchDeactivateHoldoff, this);
            ControllerEmulationStickGyroLiveReadings = new ControllerEmulationStickGyroLiveReadingsProperty(this);
            ControllerEmulationStickGyroSmoothing = new ControllerEmulationStickGyroSmoothingProperty(stickGyroSmoothing, this);

            SubscribeForegroundSignal();

            // Forward live gyro readings from the Viiper processor (when it's
            // the active backend) into the manager's throttled publisher so
            // the widget visualizer sees data regardless of which backend the
            // user has selected.
            Viiper.ViiperStickGyroProcessor.LiveReadingsSink = (gx, gy, gz, sx, sy, gate) =>
                PublishStickGyroLiveReadings(gx, gy, gz, sx, sy, gate);

            // Apply persisted JSL gyro bias offset (from a previous Calibrate
            // Gyro click). The legacy CE side gets it immediately. The Viiper
            // side picks it up via the same call path once its forwarder
            // becomes active (the ActiveInstance singleton is null until
            // Viiper starts), and on subsequent calibrations the offset is
            // re-applied to both sides via SetCalibrationOffset.
            LoadJslCalibrationOffset();

            Logger.Info($"ControllerEmulationManager initialized. DeviceType={deviceType}, Supported={isSupported}, Enabled={enabled}, HideStockController={hideStockController}, HideTarget={hideTarget}, ImprovedInput={improvedInputRead}, GyroSource={gyroSource}, Mode={mode}, RumbleProfile={rumbleProfile}, GyroActivationMode={gyroActivationMode}, GyroActivationButton={gyroActivationButton}, Ds4Orientation={ds4Orientation}, Ps4TouchpadEnabled={ps4TouchpadEnabled}");

            // Apply persisted settings on startup when supported — deferred.
            //
            // ApplyCurrentConfiguration drives HidHide suppression + VIIPER
            // USBIP bus bring-up, which does a PnP cycle-port on the Legion
            // controller HID stack and takes ~6–8 s of real time. Running it
            // synchronously here blocked the helper's main Initialize() path
            // for that whole window, keeping _managersReady=false and making
            // the widget display "Not Connected" / spinners on launch for
            // ~14 s total (managers core was only 400 ms). Move the apply to
            // the thread pool so the property list is complete immediately —
            // the side effects settle in the background and UI is free to
            // bind and render while HidHide churns.
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    ApplyCurrentConfiguration("startup");
                }
                catch (Exception ex)
                {
                    Logger.Warn($"ControllerEmulationManager: deferred startup apply failed — {ex.Message}");
                }
                finally
                {
                    // Signal ViiperEmulationManager's startup apply that the
                    // legacy settle is complete, so VIIPER can assert its
                    // HidHide state without getting overwritten mid-cycle-port.
                    ControllerEmulationStartupLock.MarkLegacyApplyComplete();
                }
            });
        }

        public override void Update()
        {
            base.Update();

            try
            {
                MonitorGameBarSuppressionState();
            }
            catch (Exception ex)
            {
                Logger.Debug($"Controller emulation suppression foreground monitor failed: {ex.Message}");
            }
        }

        internal static bool TrySetGuideFromExternal(bool pressed)
        {
            var instance = activeInstance;
            return instance?.TrySetGuideFromExternalInternal(pressed) ?? false;
        }

        internal static bool CanHandleExternalGuide()
        {
            var instance = activeInstance;
            return instance?.CanHandleExternalGuideInternal() ?? false;
        }

        // ViGEm retirement: the legacy manager never owns a virtual pad
        // anymore, so it can never carry the Guide route. Constant-false
        // keeps the delivery-tier call sites (LegionButtonMonitor) and
        // ViiperEmulationManager.ReapplyMode's exclusion working unchanged.
        private bool CanHandleExternalGuideInternal()
        {
            return false;
        }

        private bool TrySetGuideFromExternalInternal(bool pressed)
        {
            return false;
        }

        private void ResetGyroActivationRuntimeState()
        {
            gyroToggleActive = false;
            lastGyroActivationButtonPressed = false;
        }

        private void ResetLegionUserspaceRemapRuntime()
        {
            // No release path needed while improved Legion userspace remaps are gamepad-only.

            Array.Clear(legionUserspaceRemapPressed, 0, legionUserspaceRemapPressed.Length);
            Array.Clear(legionUserspaceTurboOutputActive, 0, legionUserspaceTurboOutputActive.Length);
            Array.Clear(legionUserspaceTurboNextToggleTicksUtc, 0, legionUserspaceTurboNextToggleTicksUtc.Length);
            Array.Clear(legionUserspaceRemaps, 0, legionUserspaceRemaps.Length);
            legionUserspaceRemapCacheTicksUtc = 0;
        }
    }
}
