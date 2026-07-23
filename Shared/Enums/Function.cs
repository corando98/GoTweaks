namespace Shared.Enums
{
    public enum Function
    {
        None = 0,
        OSD,
        TDP,
        CurrentTDP,
        RunningGame,
        PerGameProfile,
        CPUBoost,
        CPUEPP,
        MaxCPUState,             // RETIRED (#103) — do not reuse; enum values are positional wire IDs
        MinCPUState,             // RETIRED (#103) — do not reuse
        LimitGPUClock,
        GPUClockMin,
        GPUClockMax,
        RefreshRates,
        RefreshRate,
        Resolutions,        // string[] - list of available resolutions
        Resolution,         // string - current resolution (e.g., "1920x1080")
        DisplayOrientation, // int - display rotation (0=Landscape, 1=Portrait, 2=Landscape flipped, 3=Portrait flipped)
        HDRSupported,       // bool - whether HDR is supported
        HDREnabled,         // bool - HDR on/off
        TrackedGame,
        RTSSInstalled,
        AMDRadeonSuperResolutionSupported,
        AMDRadeonSuperResolutionEnabled,
        AMDRadeonSuperResolutionSharpness,
        AMDFluidMotionFrameSupported,
        AMDFluidMotionFrameEnabled,
        // AFMF 2.x extended controls (ADLX 1.5+, gated on V1Supported)
        AMDFluidMotionFrameV1Supported,         // bool — IADLX3DAMDFluidMotionFrames1 available on this driver
        AMDFluidMotionFrameAlgorithm,           // int — 0=Auto, 1=Enhanced, 2=Standard
        AMDFluidMotionFrameSearchMode,          // int — 0=Auto, 1=Standard, 2=High
        AMDFluidMotionFramePerformanceMode,     // int — 0=Auto, 1=Quality, 2=Performance
        AMDFluidMotionFrameFastMotionResponse,  // int — 0=RepeatFrames, 1=BlendedFrames
        AMDRadeonAntiLagSupported,
        AMDRadeonAntiLagEnabled,
        AMDRadeonBoostSupported,
        AMDRadeonBoostEnabled,
        AMDRadeonBoostResolution,
        AMDRadeonChillSupported,
        AMDRadeonChillEnabled,
        AMDRadeonChillMinFPS,
        AMDRadeonChillMaxFPS,
        AMDImageSharpeningSupported,
        AMDImageSharpeningEnabled,
        AMDImageSharpeningSharpness,
        AMDDisplayBrightnessSupported,
        AMDDisplayBrightness,
        AMDDisplayContrastSupported,
        AMDDisplayContrast,
        AMDDisplaySaturationSupported,
        AMDDisplaySaturation,
        AMDDisplayTemperatureSupported,
        AMDDisplayTemperature,
        Foreground,

        LosslessScalingInstalled,
        LosslessScalingRunning,
        LosslessScalingEnabled,
        LosslessScalingCurrentProfile,   // Name of active profile for current game
        LosslessScalingScalingType,      // Off, LS1, FSR, NIS, SGSR, BCAS, Anime4K, xBR, SharpBilinear, Integer, NearestNeighbor
        LosslessScalingSharpness,        // 0-100 (for FSR, NIS, SGSR, BCAS)
        LosslessScalingFSROptimize,      // bool - FSR optimize toggle
        LosslessScalingAnime4KSize,      // Small, Medium, Large, VeryLarge, UltraLarge
        LosslessScalingAnime4KVRS,       // bool - VRS toggle for Anime4K
        LosslessScalingScaleMode,        // Auto, Custom
        LosslessScalingScaleFactor,      // 1-5 (for Custom mode)
        LosslessScalingAspectRatio,      // AspectRatio, Fullscreen (for Auto mode)
        LosslessScalingFrameGenType,     // Off, LSFG1, LSFG2, LSFG3
        LosslessScalingLSFG3Mode,        // FIXED, ADAPTIVE
        LosslessScalingLSFG3Multiplier,  // 2, 3, 4
        LosslessScalingLSFG3Target,      // Target FPS (int)
        LosslessScalingLSFG2Mode,        // X2, X3, X4
        LosslessScalingFlowScale,        // 25-100
        LosslessScalingSize,             // PERFORMANCE, BALANCED
        LosslessScalingAutoScale,        // bool - auto-detect and scale
        LosslessScalingAutoScaleDelay,   // int - delay in ms before auto-scaling
        LosslessScalingSaveAndRestart,   // Action: save XML and restart LS
        LosslessScalingCreateProfile,    // Action: create profile for current game
        LosslessScalingBringToForeground, // Action: bring LS window to foreground
        LosslessScalingLaunch,           // Action: launch LS minimized (via helper)
        LosslessScalingResetProfile,     // Action: reset current profile to LS default values

        // Additional Settings.xml fields exposed in the widget Scaling tab.
        // String enums map to LS's verbatim values (so Settings.xml round-trips cleanly).
        LosslessScalingSyncMode,         // OFF, DEFAULT, VSYNC1..VSYNC4
        LosslessScalingCaptureApi,       // DXGI, WGC, GDI
        LosslessScalingDrawFps,          // bool — overlay shown by LS
        LosslessScalingHdrSupport,       // bool
        LosslessScalingGsyncSupport,     // bool
        LosslessScalingResizeBeforeScaling, // bool
        LosslessScalingLS1Type,          // BALANCED, PERFORMANCE (only meaningful when ScalingType=LS1)
        LosslessScalingMaxFrameLatency,  // int 0..4

        Settings_AutoStartRTSS,
        Settings_OnScreenDisplayProvider,
        Settings_UseManufacturerWMI,    // DEPRECATED: bool - use manufacturer WMI for TDP instead of RyzenAdj
        Settings_TdpMethod,             // int (TdpMethod enum) - TDP control method (ManufacturerWMI=0, PawnIO=1, WinRing0=2)
        TdpMethod_WinRing0Available,    // bool - whether WinRing0 files exist in C:\GoTweaks
        TdpMethod_PawnIOAvailable,      // bool - whether PawnIO/RyzenSMU is available for TDP control
        TdpMethod_PawnIOInstalled,      // bool - whether PawnIO driver is installed (driver present, may not work for TDP yet)
        TdpMethod_InstallPawnIO,        // string - trigger to install PawnIO (write "install" to trigger)

        // Device detection (agnostic, works for any device)
        DeviceType,                 // int (DeviceType enum) - detected device type (Generic=0, LegionGo=1, LegionGo2=2, LegionGoS=3)
        DeviceManufacturer,         // string - device manufacturer (e.g., "LENOVO", "ASUS", "Valve")
        DeviceModel,                // string - device model identifier (e.g., "83E1", "83N0")
        DeviceSupportsWmiTdp,       // bool - whether device supports WMI-based TDP control

        // Device capability flags (helper -> widget sync for UI visibility)
        DeviceDisplayName,              // string - "Legion Go", "Legion Go 2", "Legion Go S"
        DeviceSupportsControllerRemap,  // bool - whether device supports HID controller remapping
        DeviceSupportsRgbLighting,      // bool - whether device supports HID RGB lighting control
        DeviceSupportsGyro,             // bool - whether device supports HID gyro configuration
        DeviceHasScrollWheel,           // bool - whether device has a scroll wheel (Legion Go/Go2 yes, Go S no)
        DeviceHasDetachableControllers, // bool - whether device has detachable L/R controllers (Legion Go/Go2 yes, Go S no)
        DeviceHasTouchpad,              // bool - whether device has touchpad/vibration settings (uses HID)

        // Legion Go specific functions
        LegionGoDetected,           // bool - whether a Legion Go device is detected (kept for backwards compatibility)
        LegionTouchpadEnabled,      // bool - touchpad on/off
        LegionLightMode,            // int - RGB mode (Off=0, Solid=1, Pulse=2, Dynamic=3, Spiral=4)
        LegionLightColor,           // string - hex color "#RRGGBB"
        LegionLightBrightness,      // int - brightness (0-100)
        LegionLightSpeed,           // int - animation speed (0-100)
        LegionPerformanceMode,      // int - TDP mode (Quiet=1, Balanced=2, Performance=3, Custom=255)
        LegionCustomTDPSlow,        // int - Slow TDP (SPL) in watts
        LegionCustomTDPFast,        // int - Fast TDP (SPPL) in watts
        LegionCustomTDPPeak,        // int - Peak TDP (FPPT) in watts
        LegionFanFullSpeed,         // bool - fan full speed mode
        LegionFanCurveData,         // string - fan curve data "v0,v1,v2,...,v9" (10 values 0-100) — represents the *active* power mode's curve (drives EC/WMI)
        LegionUnlockFanCurve,       // bool - active power mode's EC-override unlock state (drives EC override loop)
        LegionFanCurvePerMode,      // string - "<mode>:v0,v1,...,v9" — read/write a specific mode's saved curve without changing power mode. Helper pushes 4 messages (one per mode) on connect; widget sends one per edit.
        LegionUnlockFanCurvePerMode,// string - "<mode>:0|1" — read/write a specific mode's unlock state. Same fan-out pattern as LegionFanCurvePerMode.
        LegionCPUCurrentTemp,       // int - current CPU temperature in Celsius (read-only from helper)
        LegionFanSensorTemp,        // int - fan control sensor temp (0x01 sensor, what EC uses for curve) (read-only from helper)
        LegionCPUFanRPM,            // int - current CPU fan speed in RPM (read-only from helper)
        LegionFanCurveVisible,      // bool - widget sets this when fan curve is expanded and visible
        LegionGyroEnabled,          // bool - gyroscope on/off (WIP)
        LegionVibration,            // int - vibration level (0=Off, 1=Weak, 2=Medium, 3=Strong)
        LegionPowerLight,           // bool - power button LED on/off
        LegionChargeLimit,          // bool - battery charge limit (80%) on/off

        // Legion Go Controller Remapping (supports Gamepad, Keyboard, Mouse mapping)
        LegionButtonY1,             // string - JSON ButtonMapping (type, gamepadAction, keyboardKeys[], mouseButton)
        LegionButtonY2,             // string - JSON ButtonMapping
        LegionButtonY3,             // string - JSON ButtonMapping
        LegionButtonM1,             // string - JSON ButtonMapping (new button)
        LegionButtonM2,             // string - JSON ButtonMapping
        LegionButtonM3,             // string - JSON ButtonMapping
        LegionButtonDesktop,        // string - JSON ButtonMapping (Desktop button - Win+G default)
        LegionButtonPage,           // string - JSON ButtonMapping (Page button - Win+Tab default)
        LegionNintendoLayout,       // bool - Nintendo-style face button swap (A↔B, X↔Y)
        LegionVibrationMode,        // int - vibration mode preset (FPS=1, Racing=2, AVG=3, SPG=4, RPG=5)
        LegionControllerProfileEnabled, // bool - per-game controller profile toggle

        // Legion Go Gyro Settings (per-game profile)
        LegionGyroTarget,               // int - 0=Disabled, 1=LeftStick, 2=RightStick, 3=Mouse
        LegionGyroSensitivityX,         // int - 1-100
        LegionGyroSensitivityY,         // int - 1-100
        LegionGyroInvertX,              // bool
        LegionGyroInvertY,              // bool
        LegionGyroMappingType,          // int - 0=Instant, 1=Continuous
        LegionGyroActivationMode,       // int - 0=Hold, 1=Toggle
        LegionGyroActivationButton,     // int - 0-8 (None, LB, LT, RB, RT, Y1, Y2, M2, M3)

        // Legion Go Advanced Gyro Settings (per-game profile)
        LegionGyroDeadzone,             // int - 1-100 (suppresses small motions near center)

        // Legion Go Stick Deadzones (per-game profile)
        LegionLeftStickDeadzone,        // int - 0-50 (percent)
        LegionRightStickDeadzone,       // int - 0-50 (percent)

        // Legion Go Trigger Travel (per-game profile)
        LegionLeftTriggerStart,         // int - 0-100 (start %)
        LegionLeftTriggerEnd,           // int - 0-100 (end % from full)
        LegionRightTriggerStart,        // int - 0-100 (start %)
        LegionRightTriggerEnd,          // int - 0-100 (end % from full)
        LegionHairTriggers,             // bool - hair triggers preset (0%/1%)

        // Legion Go Joystick as Mouse (per-game profile)
        LegionJoystickAsMouseMode,      // int - 0=Disabled, 1=Left Stick, 2=Right Stick
        LegionJoystickMouseSens,        // int - Mouse sensitivity (10-100)

        // Legion Go Gamepad Button Remapping (per-game profile)
        LegionGamepadButtonMapping,     // string - JSON mapping of gamepad buttons to actions

        // Legion Go Desktop Controls (preset: RS→Mouse, RT→LClick, LT→RClick, A→Enter, B→Esc)
        LegionDesktopControls,          // bool - desktop controls preset enabled

        // Legion Go Touchpad Vibration (GLOBAL setting)
        LegionTouchpadVibration,        // bool - on/off toggle for touchpad haptics

        // GPD specific functions
        GPDDetected,                    // bool - whether a GPD device is detected (Win Mini, Win 4, etc.)
        GPDWin5Connected,               // bool - whether GPD Win 5 HID controller is connected
        GPDRestoreDefaults,             // bool - trigger to restore default button mappings on Win 5
        GPDDeviceName,                  // string - device display name (e.g., "GPD Win 5")
        GPDSupportsFanControl,          // bool - whether device supports fan control (separate from HID)
        GPDFanSpeed,                    // int - fan speed percentage (0 = auto, 30-100 = manual)
        GPDFanRPM,                      // int - current fan RPM (read-only, helper to widget)
        GPDFanMode,                     // int - fan mode (0 = auto, 1 = manual)
        GPDFanCurveEnabled,             // bool - software fan curve on/off
        GPDFanCurveData,                // string - "v0,v1,...,v9" (10 fan speed % values)
        GPDFanCurveVisible,             // bool - graph is visible (triggers temp pushes)
        GPDCPUTemp,                     // int - CPU temp pushed to widget for graph

        // GPD Win 5 Button Remapping (ushort keycodes using GPDWin5Keycodes values)
        GPDButtonA,                     // ushort - A button keycode
        GPDButtonB,                     // ushort - B button keycode
        GPDButtonX,                     // ushort - X button keycode
        GPDButtonY,                     // ushort - Y button keycode
        GPDButtonDPadUp,                // ushort - D-Pad Up keycode
        GPDButtonDPadDown,              // ushort - D-Pad Down keycode
        GPDButtonDPadLeft,              // ushort - D-Pad Left keycode
        GPDButtonDPadRight,             // ushort - D-Pad Right keycode
        GPDButtonL3,                    // ushort - L3 (left stick click) keycode
        GPDButtonR3,                    // ushort - R3 (right stick click) keycode
        GPDButtonL4,                    // ushort - L4 back paddle keycode
        GPDButtonR4,                    // ushort - R4 back paddle keycode
        GPDButtonLSUp,                  // ushort - Left stick Up keycode
        GPDButtonLSDown,                // ushort - Left stick Down keycode
        GPDButtonLSLeft,                // ushort - Left stick Left keycode
        GPDButtonLSRight,               // ushort - Left stick Right keycode

        // Controller Battery (read-only, from HID input reports)
        ControllerBatteryLeft,          // int - left controller battery (1-100, or -1 if unavailable)
        ControllerBatteryRight,         // int - right controller battery (1-100, or -1 if unavailable)
        ControllerChargingLeft,         // bool - whether left controller is charging
        ControllerChargingRight,        // bool - whether right controller is charging
        ControllerConnectedLeft,        // bool - whether left controller is connected (attached/detached)
        ControllerConnectedRight,       // bool - whether right controller is connected
        ControllerVidPid,               // string - detected controller VID:PID (e.g., "17EF:6182")
        ControllerDeviceStatus,         // string - JSON snapshot of LegionGoStatus (FW, RGB, brightness, mode, speed, vibration, touchpad)

        // AutoTDP functions
        AutoTDPEnabled,             // bool - enable/disable AutoTDP
        AutoTDPTargetFPS,           // int - target FPS (30-144)
        AutoTDPCurrentFPS,          // int - current FPS reading (read-only)
        AutoTDPMinTDP,              // int - minimum TDP for AutoTDP range (4-85)
        AutoTDPMaxTDP,              // int - maximum TDP for AutoTDP range (4-85)
        AutoTDPUseMLMode,           // bool - DEPRECATED: use AutoTDPControllerType instead
        AutoTDPMLStatus,            // string - ML mode status (read-only: "Updates: N | Exploration: X%")
        AutoTDPResetML,             // bool - trigger to reset ML learning data (write true to trigger)
        AutoTDPPauseWhenUnfocused,  // bool - pause AutoTDP when game window is not focused (default: true)
        AutoTDPControllerType,      // int - controller type (0=PID, 1=Q-Learning, 2=SARSA)
        AutoTDPLearnedGameData,     // string - JSON bundle for learned TDP + heatmap for current game

        // OSD Customization
        OSDConfig,                  // string - OSD configuration per level (L1:items;L2:items;L3:items)

        // OLED Protection
        OLEDConfig,                 // string - OLED protection settings config

        // FPS Limiter (RTSS)
        FPSLimit,                   // int - FPS limit (0 = unlimited)

        // Device TDP Limits
        TDPLimits,                  // string - "min,max" format (e.g., "4,35")

        // TDP Boost (apply additional power to SPPT/FPPT above base TDP)
        TDPBoostEnabled,            // bool - enable/disable TDP boost (profile-synced)
        TDPBoostSPPT,               // int - additional watts for SPPT (0-10, default 1)
        TDPBoostFPPT,               // int - additional watts for FPPT (0-15, default 3)

        // CPU Core Configuration — all RETIRED (out of scope for a predictable app); do not reuse
        CPUCoreConfig,              // RETIRED — was "pCores,eCores,isHybrid" detection push
        CPUCoreActiveConfig,        // RETIRED — was the affinity selection
        CoreParkingPercent,         // RETIRED (#103) — do not reuse; was CPMAXCORES percentage
        ForceParkMode,              // RETIRED — was force-affinity-on-all-processes

        // OS Power Mode (Windows 11 power slider)
        OSPowerMode,                // int - 0=Best Power Efficiency, 1=Balanced, 2=Best Performance

        // System Actions
        RefreshDisplaySettings,     // Action: re-query display resolution, refresh rate, HDR status

        // Default Game Profile (Microsoft Gaming Services profiles)
        DefaultGameProfileAvailable,    // bool - whether current game has a default profile
        DefaultGameProfileData,         // string - serialized DefaultGameProfile XML
        DefaultGameProfileEnabled,      // bool - user's toggle state for current game
        ForceDefaultGameProfile,        // bool - master enable for Default Game Profiles (name kept for wire compat; OMNI fallback on undetected devices)

        // Profile Detection Settings
        ProfileMatchByExe,              // bool - match profiles by exe path instead of window title
        ProfileCustomGamePath,          // string - pipe-separated paths always treated as games
        ProfileGamesOnly,               // bool - only detect apps rendering frames (FPS > 0)
        ProfileBlacklistPaths,          // string - pipe-separated paths never treated as games
        ForegroundApp,                  // string - current foreground app path (for UI display)
        DeleteGameProfile,              // string - write game name to delete its profile (widget -> helper)

        // Labs Section (Experimental Features)
        Labs_DAServiceControl,          // int - 0=Stop, 1=Start DAService
        Labs_DAServiceStatus,           // int - 0=Stopped, 1=Running, 2=NotFound
        Labs_LegionLToXbox,             // DEPRECATED - replaced by Labs_LegionButtonRemap
        Labs_LegionButtonRemap,         // Button (0=Disabled, 1=Legion L, 2=Legion R), Action (0=Xbox Guide, 1=Shortcut), Shortcut (string)
        Labs_LegionScrollRemap,         // Direction (Up/Down/Click), Enabled, Action, Shortcut - back scroll wheel remap
        Labs_FocusWidget,               // Trigger: helper sends to widget to focus itself
        Debug_ExportDGPs,               // Trigger: widget requests helper to export DGPs to Desktop
        Debug_ExportProfiles,           // Trigger: widget requests helper to export per-game profiles to Desktop

        // ViGEmBus Driver
        ViGEmBusInstalled,              // bool - whether ViGEmBus driver is installed
        InstallViGEmBus,                // string - trigger to install ViGEmBus (write "install" to trigger)
        HidHideInstalled,               // bool - whether HidHide is installed (CLI available)
        InstallHidHide,                 // string - trigger to install HidHide (write "install" to trigger)

        // Controller Hotkey Settings (synced from widget to helper for XInput monitoring)
        ControllerHotkeyConfig,         // string - JSON config for controller button combos (Menu+DPad, View+ABXY)

        // Profile Save Flags (widget's Profiles-tab checkboxes). Helper routes per-setting
        // writes to GlobalProfile when the matching flag is false, CurrentProfile when true.
        ProfileSaveFlags,               // string - JSON map of flag name -> bool; sent on startup + on checkbox change

        PowerSourceProfileConfig,       // RETIRED (#103) — do not reuse; was the power-plan auto-switch config

        // Per-state TDP/boost values for the active profile, sent by the widget so the
        // helper can apply them on AC/DC transitions without depending on the widget being
        // awake. Sent whenever the active profile or its AC/DC sub-profile changes. Helper
        // caches both AC and DC values and picks the right set when SystemManager fires
        // PowerSourceChanged. JSON keys: AcTdp, DcTdp, AcTdpBoost, DcTdpBoost (all optional;
        // null/missing = no override for that field).
        PowerSourceProfileValues,       // string - JSON: AC/DC TDP and TDPBoost values

        // Debug/Development
        CheckLocalUpdate,               // Trigger: check for local AppPackages update (Debug)
        InstallUpdate,                  // Trigger: download and install update (Content = URL or local path)

        // System Restore (for clean uninstall)
        PrepareForUninstall,            // Trigger: restore original system values and remove scheduled task
        SystemRestoreStatus,            // string - status of saved original values (read-only)

        // Import/Export (comprehensive backup/restore)
        ExportAllData,                  // Trigger: export profiles, settings, Q-learning model to Desktop folder
        ImportAllData,                  // string - path to import folder; imports all data from it

        // Quick Metrics (compact stats row at top of Quick Tab)
        QuickMetrics,                   // string - JSON bundle pushed from helper (batteryDrain, cpuUsage, gpuUsage, timeRemaining, etc.)
        QuickMetricsEnabled,            // bool - toggle for metrics row visibility (widget setting synced to helper)

        // PawnIO Debug Tools (for testing RyzenSMU functions)
        PawnIOGetCpuInfo,               // Query: returns CPU codename and capabilities
        PawnIOApplySettings,            // Set: apply CO, GfxClk, Tctl settings (params: CoAll, CoGfx, GfxClk, TctlTemp)

        // Screen Saver (idle display off for gaming)
        ScreenSaverEnabled,             // bool - when true, helper monitors idle time and triggers Windows screen saver

        // Auto Hibernate — RETIRED, replaced by SystemHibernateTimeoutAC/DC (Power & Sleep
        // card); do not reuse, enum values are positional wire IDs. Stored settings are
        // migrated to the new AC/DC minutes at helper startup.
        AutoHibernateEnabled,           // RETIRED
        AutoHibernateIdleMinutes,       // RETIRED
        AutoHibernateMode,              // RETIRED

        // GPD Controller Emulation
        GPDGyroSource,                  // int - gyro source (0=Internal Handheld, 1=Controller Internal)
        GPDGyroSimulateMode,            // int - gyro simulation mode (0=Mouse, 1=XboxStick, 2=PS4Motion, 3=PS4Stick)
        GPDApplyMappings,               // bool - trigger to apply staged GPD Win 5 button mappings

        // Handheld-agnostic Controller Emulation
        ControllerEmulationAvailable,   // bool - helper supports controller emulation flow on current device
        ControllerEmulationEnabled,     // bool - global on/off switch for controller emulation runtime
        ControllerEmulationGyroSource,  // int - gyro source (0=Internal Handheld, 1=Controller Internal)
        ControllerEmulationMode,        // int - mode (0=Mouse, 1=XboxStick, 2=PS4Motion, 3=PS4Stick)
        ControllerEmulationDs4Orientation, // int - DS4 motion orientation (0=Parallel, 1=Orthogonal)
        ControllerEmulationMouseSensitivity,  // int - 1-400 (percent scaling)
        ControllerEmulationMouseThreshold,    // int - 0-20 (deg/s deadzone)
        ControllerEmulationMouseAxis,         // int - axis mapping (0=Yaw/Pitch, 1=Yaw/Roll, 2=Roll/Pitch)
        ControllerEmulationMouseInvertX,      // bool - invert horizontal
        ControllerEmulationMouseInvertY,      // bool - invert vertical
        ControllerEmulationMouseGainX,        // int - 25-400 (percent)
        ControllerEmulationMouseGainY,        // int - 25-400 (percent)
        ControllerEmulationStickSensitivity,  // int - 1-400 (percent scaling)
        ControllerEmulationStickThreshold,    // int - 0-20 (deg/s deadzone)
        ControllerEmulationStickAxis,         // int - axis mapping (0=XY(Yaw), 1=XZ(Roll), 2=Yaw+Pitch)
        ControllerEmulationStickInvertX,      // bool - invert horizontal
        ControllerEmulationStickInvertY,      // bool - invert vertical
        ControllerEmulationStickGainX,        // int - 25-400 (percent)
        ControllerEmulationStickGainY,        // int - 25-400 (percent)
        ControllerEmulationStickSelect,       // int - 0=Left, 1=Right
        ControllerEmulationStickExcessMove,   // bool - allow excess/overflow behavior
        ControllerEmulationStickRange,        // int - 0-200 (0.00-2.00x)
        ControllerEmulationStickOnlyJoystickData, // bool - only forward joystick data
        ControllerEmulationVirtualABXYLayout, // int - 0=Xbox, 1=Nintendo
        ControllerEmulationHideStockController, // bool - hide physical handheld controller while virtual controller is active
        ControllerEmulationHideTarget, // int - suppression target selector (0=Auto, 1=Native, 2=Xbox360Bridge, 3=NativeAndXbox360)
        ControllerEmulationPs4TouchpadEnabled, // bool - enable touchpad forwarding for PS4 (Motion/Stick) modes
        ControllerEmulationGyroActivationMode, // int - gyro activation behavior (0=AlwaysOn, 1=Hold, 2=Toggle)
        ControllerEmulationGyroActivationButton, // int - activation button mapping (0=None, 1=RT, 2=LT, ...)
        ControllerEmulationImprovedInput, // bool - Legion Go/Go2 HID gamepad-read path to avoid XInput blocking in Game Bar/FSE

        // GPD Win 5 HID diagnostics/configuration (appended to preserve prior enum values)
        GPDWin5HidDebug,              // bool - enable verbose Win 5 HID TX/RX debug logging
        GPDWin5HidDevices,            // string - JSON array of deterministic Win 5 HID candidate interfaces
        ControllerEmulationRumbleProfile, // int - rumble response profile (0=Balanced, 1=Sharp, 2=Soft, 3=Impact, 4=Boosted)
        ControllerEmulationLedForwardingEnabled, // bool - forward DS4 LED color requests from games to physical controller
        ControllerEmulationCalibrateGyro, // bool - trigger firmware gyro calibration (fire-and-forget action)
        ControllerEmulationStickMinGyroSpeed,      // int - min gyro input speed in deg/s (0-100, default 0)
        ControllerEmulationStickMaxGyroSpeed,      // int - max gyro speed for full deflection in deg/s (50-720, default 220)
        ControllerEmulationStickMinOutput,         // int - min joystick output percent (0-100, default 0) — anti-deadzone
        ControllerEmulationStickMaxOutput,         // int - max joystick output percent (1-100, default 100)
        ControllerEmulationStickPowerCurve,        // int - 10-400 = 0.1x-4.0x (default 100 = 1.0 linear)
        ControllerEmulationStickSensitivityV2,     // int - 1-400 = 0.01x-4.00x (default 100 = 1.00x)
        ControllerEmulationStickDeadzone,          // int - 0-50 deg/s deadzone with smooth recovery (default 2)
        ControllerEmulationStickPrecisionSpeed,    // int - 0-100 deg/s precision threshold (default 0 = off)
        ControllerEmulationStickOutputMix,         // int - -100 to +100 (default 0) positive reduces vertical, negative reduces horizontal
        ControllerEmulationStickOrientationV2,     // int - 0=Parallel, 1=Orthogonal (default 0) — for stick output
        ControllerEmulationStickConversion,        // int - 0=Yaw, 1=Roll, 2=Yaw+Roll (default 0) — 3DOF to 2D mapping
        SidebarMenuEnabled,                        // bool - widget sends to helper to enable/disable sidebar overlay

        // VIIPER (experimental new emulation backend)
        Settings_EmulationBackend,                 // int (EmulationBackend enum) - Legacy=0, Viiper=1 (global, persisted)
        Viiper_UsbipInstalled,                     // bool - whether usbip-win2 driver is installed
        Viiper_InstallUsbip,                       // string - write "install" to trigger silent usbip-win2 download + install (helper side runs the pinned InnoSetup installer elevated)
        Viiper_DeviceType,                         // string - virtual device type (xbox360, dualshock4, dualsenseedge, xboxelite2, steam-generic, switchpro, joycon-pair)
        Viiper_InputSource,                        // string - input source ("XInput" or "LegionHid")
        Viiper_GyroSource,                         // string - gyro source ("Left", "Right", "Mixed", "Handheld", "None")
        Viiper_SteamSubDevice,                     // string - Steam sub-device selector (generic, steam-deck, legion-go, legion-go-2, ..., gordon)
        Viiper_SonySubDevice,                      // string - Sony sub-device selector (dualsense, dualsense-edge, dualshock4) — used when Viiper_DeviceType == "sony"
        Viiper_NintendoSubDevice,                  // string - Nintendo sub-device selector (switchpro, switchpro2, joycon-left, joycon-right, joycon-pair) — used when Viiper_DeviceType == "nintendo". switchpro2 is a placeholder pending the libviiper ns2pro port (Cookiekira/VIIPER#ns2pro); selecting it currently falls back to switchpro at the helper.
        Viiper_GuideButtonMode,                    // string - "Native" (send device Guide/PS) or "GameBar" (send Win+G on Mode/Guide press)
        Viiper_SwapRumbleMotors,                   // bool  - swap large/small motor values before forwarding rumble feedback
        Viiper_RumbleIntensity,                    // int (0-200) - percentage multiplier applied to rumble motor values (100 = unity)
        Viiper_MirrorLightbarToStick,              // bool  - mirror emulated DS4/DSEdge lightbar color onto Legion Go stick lights (default true)
        Viiper_GyroAxisMapX,                       // string - which source axis feeds the emulated device's IMU X channel ("X","Y","Z","-X","-Y","-Z")
        Viiper_GyroAxisMapY,                       // string - IMU Y channel mapping (same options)
        Viiper_GyroAxisMapZ,                       // string - IMU Z channel mapping (same options)
        Viiper_StickGyroEnabled,                   // bool  - master enable for the Gyro → Right Stick processor on no-native-motion targets (default true)
        Viiper_JoyconGyroPerHalf,                  // bool  - joycon-pair only: when true each Joy-Con half is driven by its matching physical controller IMU (left half ← left controller, right half ← right controller); when false (default) both halves share the selected gyro source
        Viiper_AlternateGyroConvention,            // bool  - Alternate gyro convention. Gyro only (accel always pass-through). Per-target defaults are tuned for actual Steam usage, ON gives the alternate. DS4/DualSense/DSEdge default yaw=+gz/roll=-gy (verified native gyro aim); alt yaw=-gz/roll=+gy. Steam Deck / Switch Pro default is 90deg-about-X rotated gyro (yaw=-gz, roll=+gy on wire) — matches Steam's direct-HID reading; alt is plain pass-through (1:1 in SdlGyroTester via SDL3's internal driver remap, useful when downstream consumer goes through SDL_GetGamepadSensorData). Single toggle, target-aware behavior.
        Viiper_StickTriggerConfig,                 // string (JSON) - per-stick + per-trigger shaping config (deadzone shape, dead/anti-dead zones, sensitivity curves). Schema in StickTriggerConfigBundle.
        Viiper_StickTriggerPreviewEnabled,         // bool - widget sets true while the Sticks & Triggers panel is expanded; helper pumps live samples only when this is on.
        Viiper_StickTriggerLiveSample,             // string "LX,LY,RX,RY,LT,RT" - helper streams raw values at ~30 Hz while preview is enabled. Widget runs StickTriggerProcessor locally to compute shaped values for the canvas.

        // Adaptive Brightness backend selector. The existing Adaptive Brightness toggle is
        // the master on/off; this picks which loop runs underneath. Helper mode subscribes to
        // Windows.Devices.Sensors.LightSensor, log-scales lux into a brightness target with
        // EMA smoothing, asymmetric hysteresis, stepped output, and per-environment learning.
        AdaptiveBrightnessMode,                    // int (AdaptiveBrightnessMode enum) - 0=Windows native, 1=Helper

        // Stick-gyro anti-deadzone tuning. Stick output below the in-game
        // deadzone (typically 10-20%) gets silently killed; anti-deadzone
        // pumps any non-noise gyro motion into a guaranteed minimum stick
        // deflection so small precision aim adjustments register.
        ControllerEmulationStickGyroAntiDeadzone,           // int 0-30 — minimum stick deflection % (default 10 ≈ 3500 int16)
        ControllerEmulationStickGyroAntiDeadzoneThreshold,  // int 0-50 — gyro magnitude floor in 0.1°/s units (default 5 = 0.5°/s)

        // Helper-to-widget calibration progress. Pushed during the JSL
        // calibration run so the UI can show countdown + final bias offset.
        ControllerEmulationCalibrateGyroStatus,             // string JSON: { "phase": "running"|"done"|"error", "secondsLeft": N, "offset": [x,y,z], "weight": w }

        // Per-axis vertical sensitivity multiplier (% of master sensitivity).
        // Default 100 = vertical matches horizontal. Range 10–200.
        ControllerEmulationStickGyroVerticalRatio,          // int 10-200

        // Sensitivity curve preset (0=Linear, 1=Slow-and-precise, 2=Snap-aim).
        ControllerEmulationStickGyroCurvePreset,            // int

        // Tightening: above this gyro speed, output gain ramps up to the
        // configured "fast-zone" gain. Lets users do precise slow aim AND
        // quick whip-turns without changing the master slider.
        ControllerEmulationStickGyroTightenThreshold,       // int 0–500 (deg/s)
        ControllerEmulationStickGyroTightenGain,            // int 100–300 (% multiplier at full ramp; 100 = off)

        // Stick-touch deactivation. When the physical stick deflects past
        // the threshold, gyro output is suppressed; resumes after the
        // hold-off elapses with the stick at rest.
        ControllerEmulationStickGyroTouchDeactivateEnabled, // bool
        ControllerEmulationStickGyroTouchDeactivateThreshold, // int 0-50 (% of stick range)
        ControllerEmulationStickGyroTouchDeactivateHoldoff,   // int 0-1000 (ms)

        // Live gyro readings pushed at ~5 Hz when the widget is visible.
        ControllerEmulationStickGyroLiveReadings,           // string JSON: { "gyro":[x,y,z], "out":[stickX,stickY], "gate":bool }

        // EMA smoothing strength on the JSL-calibrated gyro stream before
        // stick conversion. 0 = no smoothing (raw per-sample output, prone to
        // visible jitter from BMI260 noise). 90 = heavy smoothing (90%
        // historical weight, adds noticeable lag). Default 30 = light smoother.
        ControllerEmulationStickGyroSmoothing,              // int 0-90

        // GoTweaks Legion lighting (helper-driven RGB on Legion Go controllers).
        // Config is a single delimited string to keep the property/pipe surface small:
        //   "<mode>|<baseHex>|<flashHex>|<decayMs>|<brightness0-100>|<speed0-100>"
        // mode: disabled|solid|pulse|rainbow|spiral|flash|cycle|perbutton. Colors are RRGGBB hex.
        GoTweaksLightingConfig,                             // string - see format above

        // GoTweaks Haptics (standalone physical-controller button haptics).
        // Config is a single delimited string:
        //   "<masterOn0/1>|face:<on>,<intensity0-100>|front:<on>,<i>|back:<on>,<i>|trigger:<on>,<i>"
        GoTweaksHapticsConfig,                              // string - see format above

        // Legion controller auto-sleep (idle power-off) timeout, in minutes. 0 = never.
        // Written via HID sub-command 0x09. Common values: 0, 5, 10, 15, 20, 30.
        LegionControllerSleepMinutes,                       // int - minutes, 0 = never

        // Software gyro bias capture (Steam-style one-shot calibration). Widget sends this
        // function to request a fresh capture (Content="capture") or to clear the stored bias
        // (Content="reset"). Helper samples raw gyro for ~500 ms, averages per axis, persists
        // the values in LocalSettings, and pushes back via GyroBiasOffset for the UI to show.
        // Subtraction is applied at LegionButtonMonitor.TryGetLatestGyroSample so every
        // downstream consumer (legacy CE stick-gyro, VIIPER stick-gyro, VIIPER native DS4 /
        // DualSense / Xbox forwarding) gets bias-corrected samples. Separate from the Legion
        // hardware CalibrateGyro path (which sends a firmware-level zero command to the
        // controller); the two coexist and address different drift sources.
        CalibrateGyroBias,                                  // string - "capture" or "reset"

        // Helper -> widget push of the current stored bias offset and timestamp, so the UI
        // can show "Calibrated 2 min ago, X +0.05, Y -0.03, Z +0.08 deg/s". Content is JSON:
        //   { "x":<deg/s>, "y":<deg/s>, "z":<deg/s>, "at":<UTC ticks>, "valid":<bool> }
        GyroBiasOffset,                                     // string JSON - see format above

        // Helper -> widget push of setup/environment health warnings (conflicting OEM
        // software running, missing drivers on hardware that needs them). Content is a
        // JSON array: [ { "id":"legionspace", "msg":"<user-facing text>", "action":"<optional: pawnio>" } ]
        // Empty array = all clear. Widget shows a dismissible warning banner; dismissal
        // is keyed on the array content so NEW warnings resurface it.
        SetupWarnings,                                      // string JSON - see format above

        // Built-in display (panel) brightness, 0-100 %. Helper seeds the real current
        // brightness on BatchGet via BrightnessManager (WMI); widget slider Set applies
        // it live. Optional Quick-tab slider (#50), hidden by default under Customize.
        PanelBrightness,                                    // int 0-100

        // Read-only: is the built-in panel brightness controllable right now? False when
        // docked to an external-only display (built-in panel not in active config). Widget
        // grays out + blocks the brightness slider so it never silently no-ops. (#50)
        PanelBrightnessSupported,                           // bool

        // Quick-tile controller combos. Widget -> helper: JSON array of tiles that have a
        // controller-combo binding [{ "id":..., "name":..., "mask":<uint> }]. Helper
        // registers each mask with ControllerHotkeyMonitor. Sent on save + on pipe connect.
        TileHotkeyConfig,               // string JSON - tile combo bindings (widget -> helper)

        // Helper -> widget push: a registered tile combo fired. Content = the tile id/tag.
        // Widget re-dispatches it through the normal tile-click handler (SimulateTileHotkeyFired).
        TileHotkeyFired,                // string - tile id/tag that the combo activated (helper -> widget)

        // VIIPER: which emulated-controller button the Legion front buttons act as while
        // controller emulation is active. String value: none|guide|touchpad|share|options|l3|r3.
        // Resolved per emulated type in the VIIPER forwarder (touchpad only exists on Sony/Deck).
        Viiper_DesktopButtonTarget,     // string - Legion "Desktop" front button -> emulated button
        Viiper_PageButtonTarget,        // string - Legion "Page" front button -> emulated button

        // Enable/disable the built-in touch screen digitizer via SetupAPI device disable
        // (HIDClass device matched by compatible ID UP:000D_U:0004 - never the localized
        // friendly name). Not Legion-specific hardware, but only surfaced on Legion Go for now.
        // (Ported from Rayekkk fork d790f307; appended at the END per the positional-wire-id rule.)
        TouchscreenEnabled,             // bool - true = touch input active, false = digitizer disabled

        // Read-only status: is the built-in panel the active display right now (vs. docked with
        // only an external monitor active)? Gates Resolution / Refresh Rate / Rotation, which only
        // make sense against the internal panel. Re-read on display-config changes (dock/undock).
        // (Ported from Rayekkk fork 18787c67; appended at the END per the positional-wire-id rule.)
        InternalPanelActive,        // bool

        // What the physical power button does, read directly from the active Windows
        // power plan's Power/Sleep buttons policy (Settings > Power & battery > Power
        // button behavior) rather than a GoTweaks-owned default. 0=Do nothing, 1=Sleep,
        // 2=Hibernate, 3=Shut down - matches the raw Windows PowerButtonAction values.
        // (Ported from Rayekkk fork bf80e735; appended at the END per the positional-wire-id rule.)
        SystemPowerButtonActionAC,  // int - action while plugged in (AC)
        SystemPowerButtonActionDC,  // int - action while on battery (DC)

        // Display (screen) off timeout, read directly from Windows' active power plan
        // (SUB_VIDEO/VIDEOIDLE), separately for AC/DC. Raw value is SECONDS (0 = never),
        // matching Windows exactly - same "pull from Windows" model as the Power Button.
        SystemDisplayTimeoutAC,     // int seconds, 0 = never
        SystemDisplayTimeoutDC,     // int seconds, 0 = never

        // Idle-to-hibernate timeout, in MINUTES (0 = disabled). Entirely GoTweaks-owned:
        // Windows only exposes a "sleep after X" idle timer via the standard power-plan
        // API (no reliable "hibernate after X" setting to piggyback on), so the helper
        // runs its own idle poll (Program.HibernateTimeout.cs) and calls SetSuspendState
        // directly. Persisted helper-side (LocalSettingsHelper), not read from Windows.
        // Replaces the old AutoHibernate feature (its enum slots below stay reserved).
        SystemHibernateTimeoutAC,   // int minutes, 0 = disabled
        SystemHibernateTimeoutDC,   // int minutes, 0 = disabled

        // Physical dock state per half, distinct from ControllerConnectedLeft/Right
        // (which since the linked/docked split means "linked" - true for a
        // detached-but-wirelessly-active half so its battery keeps showing).
        // Docked = conn code 0x02 only. The widget renders: docked = "Attached",
        // linked-but-undocked = "Detached", not linked = "Disconnected".
        ControllerDockedLeft,       // bool - left half physically docked
        ControllerDockedRight,      // bool - right half physically docked

        // Controller input mode, read from firmware (GET_FEATURE 0x0e gamepad mode +
        // 0x0b FPS switch, per the hid-lenovo-go.c protocol): 0=unknown, 1=XInput,
        // 2=DInput, 3=FPS (physical switch engaged - overrides the mode display).
        LegionControllerInputMode,  // int - 0 unknown / 1 xinput / 2 dinput / 3 fps

        // Receiver/MCU firmware version string (e.g. "0260422A"), read via
        // GET_VERSION_DATA(firmware, USB_MCU). Shown under the controller firmware
        // in the Controller Information card.
        LegionMcuFirmwareVersion,   // string
    }
}
