using NLog;
using Shared.Data;
using Shared.Enums;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using XboxGamingBarHelper.Core;

namespace XboxGamingBarHelper.Devices.Libraries.Legion
{
    /// <summary>
    /// Helper class to parse ButtonMapping JSON format
    /// Format: {"Type":0,"GamepadAction":5,"GamepadActions":[5,16],"GamepadMode":1,"Turbo":true,"KeyboardKeys":[4,5,6],"MouseButton":0}
    /// </summary>
    internal static class ButtonMappingParser
    {
        internal sealed class ParsedButtonMapping
        {
            public int Type { get; set; }
            public int GamepadAction { get; set; }
            public int[] GamepadActions { get; set; } = Array.Empty<int>();
            public int GamepadMode { get; set; }
            public bool Turbo { get; set; }
            public int[] KeyboardKeys { get; set; } = Array.Empty<int>();
            public int MouseButton { get; set; }
        }

        public static (int type, int gamepadAction, int[] keyboardKeys, int mouseButton) Parse(string json)
        {
            ParsedButtonMapping parsed = ParseExtended(json);
            return (parsed.Type, parsed.GamepadAction, parsed.KeyboardKeys, parsed.MouseButton);
        }

        public static ParsedButtonMapping ParseExtended(string json)
        {
            var parsed = new ParsedButtonMapping();
            if (string.IsNullOrEmpty(json))
            {
                return parsed;
            }

            try
            {
                parsed.Type = ExtractInt(json, "Type") ?? 0;
                parsed.GamepadAction = ExtractInt(json, "GamepadAction") ?? 0;
                parsed.GamepadActions = ExtractIntArray(json, "GamepadActions");
                parsed.GamepadMode = ExtractInt(json, "GamepadMode") ?? 0;
                parsed.Turbo = ExtractBool(json, "Turbo") ?? false;
                parsed.MouseButton = ExtractInt(json, "MouseButton") ?? 0;
                parsed.KeyboardKeys = ExtractIntArray(json, "KeyboardKeys");

                // Backward/forward compatibility:
                // if old/new payload has only one of the fields, hydrate the other.
                if (parsed.GamepadAction <= 0 && parsed.GamepadActions.Length > 0)
                {
                    parsed.GamepadAction = parsed.GamepadActions[0];
                }

                if (parsed.GamepadActions.Length == 0 && parsed.GamepadAction > 0)
                {
                    parsed.GamepadActions = new[] { parsed.GamepadAction };
                }

                return parsed;
            }
            catch
            {
                return parsed;
            }
        }

        private static int? ExtractInt(string json, string property)
        {
            var match = Regex.Match(json, $"\"{property}\"\\s*:\\s*(-?\\d+)");
            if (match.Success && int.TryParse(match.Groups[1].Value, out int value))
                return value;
            return null;
        }

        private static bool? ExtractBool(string json, string property)
        {
            var match = Regex.Match(json, $"\"{property}\"\\s*:\\s*(true|false|0|1)", RegexOptions.IgnoreCase);
            if (!match.Success)
                return null;

            string raw = match.Groups[1].Value;
            if (string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase))
                return true;
            if (string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase))
                return false;
            if (raw == "1")
                return true;
            if (raw == "0")
                return false;

            return null;
        }

        private static int[] ExtractIntArray(string json, string property)
        {
            var match = Regex.Match(json, $"\"{property}\"\\s*:\\s*\\[([^\\]]*)\\]");
            if (!match.Success)
                return Array.Empty<int>();

            var values = new List<int>();
            var numbers = match.Groups[1].Value.Split(',');
            foreach (var num in numbers)
            {
                if (int.TryParse(num.Trim(), out int value))
                    values.Add(value);
            }
            return values.ToArray();
        }

        /// <summary>
        /// Returns true if the mapping JSON represents a default/empty mapping (Type=0, GamepadAction=0).
        /// Default mappings should not be applied as they would clear existing button mappings.
        /// </summary>
        public static bool IsDefaultMapping(string json)
        {
            if (string.IsNullOrEmpty(json))
                return true;

            ParsedButtonMapping parsed = ParseExtended(json);
            bool hasGamepadActions = parsed.GamepadActions != null && Array.Exists(parsed.GamepadActions, action => action > 0);
            // A mapping is default if Type=0 (Gamepad) and no gamepad action is set.
            return parsed.Type == 0 && parsed.GamepadAction == 0 && !hasGamepadActions;
        }

        /// <summary>
        /// Gets the mapping values to send to the HID command based on mapping type
        /// </summary>
        public static int[] GetMappingValues(int type, int gamepadAction, int[] keyboardKeys, int mouseButton, int[] gamepadActions = null)
        {
            switch (type)
            {
                case 0: // Gamepad
                    if (gamepadAction > 0)
                        return new[] { gamepadAction };

                    if (gamepadActions != null)
                    {
                        for (int i = 0; i < gamepadActions.Length; i++)
                        {
                            if (gamepadActions[i] > 0)
                                return new[] { gamepadActions[i] };
                        }
                    }

                    return Array.Empty<int>();
                case 1: // Keyboard
                    return keyboardKeys;
                case 2: // Mouse
                    // The widget sends a 0-based mouse-combo index (0=Left, 1=Right, 2=Middle,
                    // 3=ScrollUp, 4=ScrollDown, 5=ScrollLeft, 6=ScrollRight); the controller's
                    // MouseButton enum is 1-based (LeftClick=0x01 .. ScrollRight=0x07), so shift
                    // by one. Matches the joystick-as-mouse path (LegionManager.JoystickAsMouse.cs),
                    // which already does mouseButton + 1. The old "mouseButton > 0 ? [mouseButton]"
                    // was off-by-one for every entry AND silently dropped Left Click (index 0).
                    return mouseButton >= 0 ? new[] { mouseButton + 1 } : Array.Empty<int>();
                case 3: // System action - MouseButton field carries the HotkeyAction id verbatim
                    return new[] { mouseButton };
                default:
                    return Array.Empty<int>();
            }
        }
    }

    // Legion Go detection (read-only)
    internal class LegionGoDetectedProperty : HelperProperty<bool, LegionManager>
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public LegionGoDetectedProperty(bool initialValue, LegionManager inManager) : base(initialValue, null, Function.LegionGoDetected, inManager)
        {
        }

        public void SetDetected(bool detected)
        {
            SetValue((object)detected);
        }
    }

    // Touchpad control
    internal class LegionTouchpadEnabledProperty : HelperProperty<bool, LegionManager>, IHardwareApplyResult
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        // When true, NotifyPropertyChanged skips the hardware re-apply step.
        // Used by LegionManager.SyncTouchpadFromHardware to update widget UI
        // (via the base pipe sync) without bouncing the value back to the
        // controller — the readback is itself proof of the hardware state, so
        // sending another SetTouchpadEnabled would just be a redundant HID
        // round-trip and could cause an oscillation if it fails partway.
        internal bool SuppressHardwareApply { get; set; }

        public bool LastApplySucceeded { get; private set; } = true;
        public string LastApplyFailureReason { get; private set; }

        public LegionTouchpadEnabledProperty(bool initialValue, LegionManager inManager) : base(initialValue, null, Function.LegionTouchpadEnabled, inManager)
        {
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);
            Logger.Info($"LegionTouchpadEnabled changed to {Value}");
            if (!SuppressHardwareApply)
            {
                string reason = "Legion manager is not available.";
                LastApplySucceeded = Manager != null && Manager.SetTouchpadEnabled(Value, out reason);
                LastApplyFailureReason = LastApplySucceeded ? null : (reason ?? "Touchpad could not be applied.");
            }
            else
            {
                // A hardware readback, not a user request - nothing was attempted, so there is
                // nothing to report as failed.
                LastApplySucceeded = true;
                LastApplyFailureReason = null;
            }
        }

        /// <summary>
        /// Push the value to the widget AND set our local Value. Used by the
        /// hardware-readback reconcile path. Wrap with <see cref="SuppressHardwareApply"/>
        /// = true if you don't want the change to round-trip back to the
        /// controller — that's the typical case when the readback IS the source
        /// of truth.
        /// </summary>
        public void SetValueAndSync(bool value)
        {
            SetValue((object)value);
            SyncToRemote();
        }
    }

    // Light mode (0=Off, 1=Solid, 2=Pulse, 3=Dynamic, 4=Spiral)
    internal class LegionLightModeProperty : HelperProperty<int, LegionManager>, IHardwareApplyResult
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private bool _hasUserModified = false;
        private int _initialValue;

        public bool LastApplySucceeded { get; private set; } = true;
        public string LastApplyFailureReason { get; private set; }

        public LegionLightModeProperty(int initialValue, LegionManager inManager) : base(initialValue, null, Function.LegionLightMode, inManager)
        {
            _initialValue = initialValue;
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);

            // Only apply to device if user has explicitly changed the value
            if (!_hasUserModified)
            {
                if (Value != _initialValue)
                {
                    _hasUserModified = true;
                }
                else
                {
                    // Nothing was attempted, so there is nothing to report as failed.
                    Logger.Debug($"LegionLightMode: Skipping device write - value unchanged from initial ({Value})");
                    LastApplySucceeded = true;
                    LastApplyFailureReason = null;
                    return;
                }
            }

            Logger.Info($"LegionLightMode changed to {Value}");
            string reason = "Legion manager is not available.";
            LastApplySucceeded = Manager != null && Manager.SetLightMode(Value, out reason);
            LastApplyFailureReason = LastApplySucceeded ? null : (reason ?? "Light mode could not be applied.");
        }
    }

    // Light color (hex string "#RRGGBB")
    internal class LegionLightColorProperty : HelperProperty<string, LegionManager>, IHardwareApplyResult
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private bool _hasUserModified = false;
        private string _initialValue;

        public bool LastApplySucceeded { get; private set; } = true;
        public string LastApplyFailureReason { get; private set; }

        public LegionLightColorProperty(string initialValue, LegionManager inManager) : base(initialValue, null, Function.LegionLightColor, inManager)
        {
            _initialValue = initialValue;
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);

            // Only apply to device if user has explicitly changed the value
            if (!_hasUserModified)
            {
                if (Value != _initialValue)
                {
                    _hasUserModified = true;
                }
                else
                {
                    Logger.Debug($"LegionLightColor: Skipping device write - value unchanged from initial ({Value})");
                    LastApplySucceeded = true;
                    LastApplyFailureReason = null;
                    return;
                }
            }

            Logger.Info($"LegionLightColor changed to {Value}");
            string reason = "Legion manager is not available.";
            LastApplySucceeded = Manager != null && Manager.SetLightColor(Value, out reason);
            LastApplyFailureReason = LastApplySucceeded ? null : (reason ?? "Light color could not be applied.");
        }
    }

    // Light brightness (0-100)
    internal class LegionLightBrightnessProperty : HelperProperty<int, LegionManager>, IHardwareApplyResult
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private bool _hasUserModified = false;
        private int _initialValue;

        public bool LastApplySucceeded { get; private set; } = true;
        public string LastApplyFailureReason { get; private set; }

        public LegionLightBrightnessProperty(int initialValue, LegionManager inManager) : base(initialValue, null, Function.LegionLightBrightness, inManager)
        {
            _initialValue = initialValue;
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);

            // Only apply to device if user has explicitly changed the value
            if (!_hasUserModified)
            {
                if (Value != _initialValue)
                {
                    _hasUserModified = true;
                }
                else
                {
                    Logger.Debug($"LegionLightBrightness: Skipping device write - value unchanged from initial ({Value})");
                    LastApplySucceeded = true;
                    LastApplyFailureReason = null;
                    return;
                }
            }

            Logger.Info($"LegionLightBrightness changed to {Value}");
            string reason = "Legion manager is not available.";
            LastApplySucceeded = Manager != null && Manager.SetLightBrightness(Value, out reason);
            LastApplyFailureReason = LastApplySucceeded ? null : (reason ?? "Light brightness could not be applied.");
        }
    }

    // Light speed (0-100, for animated modes)
    internal class LegionLightSpeedProperty : HelperProperty<int, LegionManager>, IHardwareApplyResult
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private bool _hasUserModified = false;
        private int _initialValue;

        public bool LastApplySucceeded { get; private set; } = true;
        public string LastApplyFailureReason { get; private set; }

        public LegionLightSpeedProperty(int initialValue, LegionManager inManager) : base(initialValue, null, Function.LegionLightSpeed, inManager)
        {
            _initialValue = initialValue;
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);

            // Only apply to device if user has explicitly changed the value
            if (!_hasUserModified)
            {
                if (Value != _initialValue)
                {
                    _hasUserModified = true;
                }
                else
                {
                    Logger.Debug($"LegionLightSpeed: Skipping device write - value unchanged from initial ({Value})");
                    LastApplySucceeded = true;
                    LastApplyFailureReason = null;
                    return;
                }
            }

            Logger.Info($"LegionLightSpeed changed to {Value}");
            string reason = "Legion manager is not available.";
            LastApplySucceeded = Manager != null && Manager.SetLightSpeed(Value, out reason);
            LastApplyFailureReason = LastApplySucceeded ? null : (reason ?? "Light speed could not be applied.");
        }
    }

    // Performance mode (1=Quiet, 2=Balanced, 3=Performance, 255=Custom)
    internal class LegionPerformanceModeProperty : HelperProperty<int, LegionManager>
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public LegionPerformanceModeProperty(int initialValue, LegionManager inManager) : base(initialValue, null, Function.LegionPerformanceMode, inManager)
        {
        }

        public override bool SetValue(object newValue, long updatedTime = 0)
        {
            int incoming;
            if (newValue is int intValue)
            {
                incoming = intValue;
            }
            else if (newValue is string stringValue && int.TryParse(stringValue, out var parsed))
            {
                incoming = parsed;
            }
            else
            {
                return base.SetValue(newValue, updatedTime);
            }

            bool isSame = incoming == value;
            var result = base.SetValue(incoming, updatedTime);

            // Force apply even when the cached value is already the same.
            // This fixes startup cases where hardware mode differs from cached value.
            if (isSame)
            {
                Logger.Info($"LegionPerformanceMode received same value {incoming}, forcing apply");
                Manager?.SetPerformanceMode(incoming);
            }

            return result;
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);
            Logger.Info($"LegionPerformanceMode changed to {Value}");
            Manager?.SetPerformanceMode(Value);
        }
    }

    // Custom TDP Slow (SPL) in watts
    internal class LegionCustomTDPSlowProperty : HelperProperty<int, LegionManager>, IHardwareApplyResult
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public bool LastApplySucceeded { get; private set; } = true;
        public string LastApplyFailureReason { get; private set; }

        public LegionCustomTDPSlowProperty(int initialValue, LegionManager inManager) : base(initialValue, null, Function.LegionCustomTDPSlow, inManager)
        {
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);
            Logger.Info($"LegionCustomTDPSlow changed to {Value}W");
            string reason = "Legion manager is not available.";
            LastApplySucceeded = Manager != null && Manager.ApplyCustomTDPSlow(Value, out reason);
            LastApplyFailureReason = LastApplySucceeded ? null : (reason ?? "TDP (SPL) could not be applied.");
        }
    }

    // Custom TDP Fast (SPPL) in watts
    internal class LegionCustomTDPFastProperty : HelperProperty<int, LegionManager>, IHardwareApplyResult
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public bool LastApplySucceeded { get; private set; } = true;
        public string LastApplyFailureReason { get; private set; }

        public LegionCustomTDPFastProperty(int initialValue, LegionManager inManager) : base(initialValue, null, Function.LegionCustomTDPFast, inManager)
        {
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);
            Logger.Info($"LegionCustomTDPFast changed to {Value}W");
            string reason = "Legion manager is not available.";
            LastApplySucceeded = Manager != null && Manager.ApplyCustomTDPFast(Value, out reason);
            LastApplyFailureReason = LastApplySucceeded ? null : (reason ?? "TDP (SPPT) could not be applied.");
        }
    }

    // Custom TDP Peak (FPPT) in watts
    internal class LegionCustomTDPPeakProperty : HelperProperty<int, LegionManager>, IHardwareApplyResult
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public bool LastApplySucceeded { get; private set; } = true;
        public string LastApplyFailureReason { get; private set; }

        public LegionCustomTDPPeakProperty(int initialValue, LegionManager inManager) : base(initialValue, null, Function.LegionCustomTDPPeak, inManager)
        {
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);
            Logger.Info($"LegionCustomTDPPeak changed to {Value}W");
            string reason = "Legion manager is not available.";
            LastApplySucceeded = Manager != null && Manager.ApplyCustomTDPPeak(Value, out reason);
            LastApplyFailureReason = LastApplySucceeded ? null : (reason ?? "TDP (FPPT) could not be applied.");
        }
    }

    // Fan full speed toggle
    internal class LegionFanFullSpeedProperty : HelperProperty<bool, LegionManager>, IHardwareApplyResult
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public bool LastApplySucceeded { get; private set; } = true;
        public string LastApplyFailureReason { get; private set; }

        public LegionFanFullSpeedProperty(bool initialValue, LegionManager inManager) : base(initialValue, null, Function.LegionFanFullSpeed, inManager)
        {
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);
            Logger.Info($"LegionFanFullSpeed changed to {Value}");
            string reason = "Legion manager is not available.";
            LastApplySucceeded = Manager != null && Manager.SetFanFullSpeed(Value, out reason);
            LastApplyFailureReason = LastApplySucceeded ? null : (reason ?? "Fan full speed could not be applied.");
        }
    }

    // Unlock fan curve override — when true, helper applies the curve via direct EC
    // RPM write (Rodpad LeGo2-Fan-Control style) instead of Lenovo's WMI Fan_Set_Table.
    // Phase 1: just plumbed through; helper logs the unlock state and falls back to WMI
    // since the PawnIO EC module isn't built yet. Phase 2 (separate work) will load a
    // dedicated .pawn module via PawnIOWrapper to write to EC reg 0xC6C8 directly.
    internal class LegionUnlockFanCurveProperty : HelperProperty<bool, LegionManager>
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public LegionUnlockFanCurveProperty(bool initialValue, LegionManager inManager)
            : base(initialValue, null, Function.LegionUnlockFanCurve, inManager)
        {
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);
            Logger.Info($"LegionUnlockFanCurve changed to {Value} ({(Value ? "EC override path enabled (PawnIO EC module — phase 2)" : "WMI Fan_Set_Table only")})");
            Manager?.SetFanCurveUnlocked(Value);
        }
    }

    // Per-mode fan curve channel — payload format "<mode>:v0,v1,…,v9".
    // Helper pushes 4 messages on connect (one per TdpMode: 1=Quiet, 2=Balanced,
    // 3=Performance, 255=Custom) so the widget can cache every mode's saved curve and
    // let the user edit a non-active mode without changing the running power mode.
    // Widget→helper: same payload — helper updates the named mode's slot, persists,
    // and only writes hardware if the named mode happens to be active.
    internal class LegionFanCurvePerModeProperty : HelperProperty<string, LegionManager>
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        // Tracks the last payload we *originated* (helper-side push to widget), so
        // we don't fire the parser on our own outbound. Pre-set in PushOutbound
        // BEFORE SetValue so the equality guard in NotifyPropertyChanged trips.
        private string _lastOutboundValue = "";

        public LegionFanCurvePerModeProperty(LegionManager inManager)
            : base("", null, Function.LegionFanCurvePerMode, inManager) { }

        // Outbound-only push from helper to widget. Marks the payload as "ours"
        // before driving SetValue → NotifyPropertyChanged sees the match and
        // skips the parser. The base call still pushes to the widget.
        public void PushOutbound(string payload)
        {
            _lastOutboundValue = payload;
            SetValue(payload);
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            // base.NotifyPropertyChanged sends the value over the pipe to the widget
            // regardless of source — that's correct for both helper-originated push
            // and widget→helper echo. The parser only runs for genuine widget edits.
            base.NotifyPropertyChanged(propertyName);
            if (Value == _lastOutboundValue) return;
            Manager?.OnFanCurvePerModePayload(Value);
        }
    }

    // Per-mode unlock channel — payload format "<mode>:0|1".
    internal class LegionUnlockFanCurvePerModeProperty : HelperProperty<string, LegionManager>
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private string _lastOutboundValue = "";

        public LegionUnlockFanCurvePerModeProperty(LegionManager inManager)
            : base("", null, Function.LegionUnlockFanCurvePerMode, inManager) { }

        public void PushOutbound(string payload)
        {
            _lastOutboundValue = payload;
            SetValue(payload);
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);
            if (Value == _lastOutboundValue) return;
            Manager?.OnUnlockFanCurvePerModePayload(Value);
        }
    }

    // Fan curve data property - manages all 10 fan speed values as a comma-separated string
    internal class LegionFanCurveDataProperty : HelperProperty<string, LegionManager>
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private bool _initialized = false;
        private string _lastSentValue = null;

        public LegionFanCurveDataProperty(string initialValue, LegionManager inManager)
            : base(initialValue, null, Function.LegionFanCurveData, inManager)
        {
            _lastSentValue = initialValue; // Track initial value to avoid re-sending same curve
        }

        // Default fan curve string for comparison
        private const string DEFAULT_FAN_CURVE = "44,48,55,60,71,79,87,87,100,100";

        /// <summary>
        /// Aligns Value AND _lastSentValue to <paramref name="curveStr"/> without
        /// triggering NotifyPropertyChanged. Used after the actual-mode WMI read
        /// settles so EnableDeviceWrites doesn't push the stale default-mode curve
        /// the constructor was initialized with.
        /// </summary>
        public void AlignSilent(string curveStr)
        {
            if (string.IsNullOrEmpty(curveStr)) return;
            SetValueSilent(curveStr);
            _lastSentValue = curveStr;
        }

        /// <summary>
        /// Call this after initial sync is complete to enable device writes.
        /// Only pushes the device-read value to widget if it's NOT the default curve.
        /// The WMI Fan_Get_Table typically returns defaults; actual custom curves may be stored elsewhere.
        /// </summary>
        public void EnableDeviceWrites()
        {
            // Only push to widget if we got a NON-default curve from the device
            // If WMI returned defaults, let the widget keep its own state
            bool isDefaultCurve = _lastSentValue == DEFAULT_FAN_CURVE;

            if (!isDefaultCurve && !string.IsNullOrEmpty(_lastSentValue))
            {
                Logger.Info($"Fan curve device writes enabled, pushing non-default curve to widget: {_lastSentValue}");
                SetValue(_lastSentValue);
            }
            else
            {
                Logger.Info($"Fan curve device writes enabled, NOT pushing default curve to widget (device returned defaults)");
            }

            // Now enable device writes for future changes
            _initialized = true;
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);
            Logger.Info($"LegionFanCurveData changed to {Value}, initialized={_initialized}, lastSent={_lastSentValue}");

            // Always apply: SetFanCurveFromString updates the active curve in the helper,
            // persists to the per-mode LocalSettings slot, and (if in Custom mode) writes
            // through to the WMI Fan_Set_Table. The Custom-mode gate moved into
            // ApplyFanCurve so the per-mode persistence + EC-override active curve update
            // run regardless of which mode is active.
            if (_initialized && Value != _lastSentValue)
            {
                _lastSentValue = Value;
                Manager?.SetFanCurveFromString(Value);
            }
        }
    }

    // CPU temperature property - read-only, updated by LegionManager periodically
    internal class LegionCPUCurrentTempProperty : HelperProperty<int, LegionManager>
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public LegionCPUCurrentTempProperty(int initialValue, LegionManager inManager)
            : base(initialValue, null, Function.LegionCPUCurrentTemp, inManager)
        {
        }

        /// <summary>
        /// Called by LegionManager to update the temperature and sync to widget
        /// </summary>
        public void UpdateTemp(int tempC)
        {
            if (tempC != Value)
            {
                SetValue(tempC);
            }
        }
    }

    // Fan control sensor temperature (0x01 sensor) - what EC uses for fan curve lookup
    // This is typically 10-17°C lower than CPU temperature
    internal class LegionFanSensorTempProperty : HelperProperty<int, LegionManager>
    {
        public LegionFanSensorTempProperty(int initialValue, LegionManager inManager)
            : base(initialValue, null, Function.LegionFanSensorTemp, inManager)
        {
        }

        /// <summary>
        /// Called by LegionManager to update the fan sensor temp and sync to widget
        /// </summary>
        public void UpdateTemp(int tempC)
        {
            if (tempC != Value)
            {
                SetValue(tempC);
            }
        }
    }

    // CPU fan RPM property - read-only, updated by LegionManager periodically
    internal class LegionCPUFanRPMProperty : HelperProperty<int, LegionManager>
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public LegionCPUFanRPMProperty(int initialValue, LegionManager inManager)
            : base(initialValue, null, Function.LegionCPUFanRPM, inManager)
        {
        }

        /// <summary>
        /// Called by LegionManager to update the RPM and sync to widget
        /// </summary>
        public void UpdateRPM(int rpm)
        {
            if (rpm != Value)
            {
                SetValue(rpm);
            }
        }
    }

    /// <summary>
    /// Widget sets this property to indicate when the fan curve graph is visible.
    /// The helper uses this to know when to push CPU temp and fan RPM updates.
    /// </summary>
    internal class LegionFanCurveVisibleProperty : HelperProperty<bool, LegionManager>
    {
        public LegionFanCurveVisibleProperty(bool initialValue, LegionManager inManager)
            : base(initialValue, null, Function.LegionFanCurveVisible, inManager)
        {
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);
            Manager?.SetFanCurveVisible(Value);
        }
    }

    // Gyro control (WIP)
    internal class LegionGyroEnabledProperty : HelperProperty<bool, LegionManager>
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public LegionGyroEnabledProperty(bool initialValue, LegionManager inManager) : base(initialValue, null, Function.LegionGyroEnabled, inManager)
        {
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);
            Logger.Info($"LegionGyroEnabled changed to {Value} (WIP - not functional)");
            Manager?.SetGyroEnabled(Value);
        }
    }

    // Vibration level (0=Off, 1=Weak, 2=Medium, 3=Strong)
    internal class LegionVibrationProperty : HelperProperty<int, LegionManager>, IHardwareApplyResult
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public bool LastApplySucceeded { get; private set; } = true;
        public string LastApplyFailureReason { get; private set; }

        public LegionVibrationProperty(int initialValue, LegionManager inManager) : base(initialValue, null, Function.LegionVibration, inManager)
        {
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);
            Logger.Info($"LegionVibration changed to {Value}");
            LastApplySucceeded = Manager != null && Manager.TrySetVibration(Value);
            LastApplyFailureReason = LastApplySucceeded ? null : "Vibration intensity could not be applied.";
        }
    }

    // Power Light toggle
    internal class LegionPowerLightProperty : HelperProperty<bool, LegionManager>, IHardwareApplyResult
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public bool LastApplySucceeded { get; private set; } = true;
        public string LastApplyFailureReason { get; private set; }

        public LegionPowerLightProperty(bool initialValue, LegionManager inManager) : base(initialValue, null, Function.LegionPowerLight, inManager)
        {
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);
            Logger.Info($"LegionPowerLight changed to {Value}");
            string reason = "Legion manager is not available.";
            LastApplySucceeded = Manager != null && Manager.SetPowerLight(Value, out reason);
            LastApplyFailureReason = LastApplySucceeded ? null : (reason ?? "Power light could not be applied.");
        }
    }

    // Battery Charge Limit (80%)
    internal class LegionChargeLimitProperty : HelperProperty<bool, LegionManager>, IHardwareApplyResult
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public bool LastApplySucceeded { get; private set; } = true;
        public string LastApplyFailureReason { get; private set; }

        public LegionChargeLimitProperty(bool initialValue, LegionManager inManager) : base(initialValue, null, Function.LegionChargeLimit, inManager)
        {
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);
            Logger.Info($"LegionChargeLimit changed to {Value}");
            string reason = "Legion manager is not available.";
            LastApplySucceeded = Manager != null && Manager.SetChargeLimit(Value, out reason);
            LastApplyFailureReason = LastApplySucceeded ? null : (reason ?? "Charge limit could not be applied.");
        }
    }

    // Button Y1 remap (JSON ButtonMapping: Type, GamepadAction, KeyboardKeys[], MouseButton)
    internal class LegionButtonY1Property : HelperProperty<string, LegionManager>, IHardwareApplyResult
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public bool LastApplySucceeded { get; private set; } = true;
        public string LastApplyFailureReason { get; private set; }

        public LegionButtonY1Property(string initialValue, LegionManager inManager) : base(initialValue ?? "", null, Function.LegionButtonY1, inManager)
        {
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);
            Logger.Info($"LegionButtonY1 applying mapping: {Value}");
            var (type, gamepadAction, keyboardKeys, mouseButton) = ButtonMappingParser.Parse(Value);
            string reason = "Legion manager is not available.";
            LastApplySucceeded = Manager != null && Manager.SetButtonMappingAdvanced(0, type, ButtonMappingParser.GetMappingValues(type, gamepadAction, keyboardKeys, mouseButton), out reason);
            LastApplyFailureReason = LastApplySucceeded ? null : (reason ?? "Y1 button mapping could not be applied.");
        }
    }

    // Button Y2 remap (JSON ButtonMapping: Type, GamepadAction, KeyboardKeys[], MouseButton)
    internal class LegionButtonY2Property : HelperProperty<string, LegionManager>, IHardwareApplyResult
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public bool LastApplySucceeded { get; private set; } = true;
        public string LastApplyFailureReason { get; private set; }

        public LegionButtonY2Property(string initialValue, LegionManager inManager) : base(initialValue ?? "", null, Function.LegionButtonY2, inManager)
        {
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);
            Logger.Info($"LegionButtonY2 applying mapping: {Value}");
            var (type, gamepadAction, keyboardKeys, mouseButton) = ButtonMappingParser.Parse(Value);
            string reason = "Legion manager is not available.";
            LastApplySucceeded = Manager != null && Manager.SetButtonMappingAdvanced(1, type, ButtonMappingParser.GetMappingValues(type, gamepadAction, keyboardKeys, mouseButton), out reason);
            LastApplyFailureReason = LastApplySucceeded ? null : (reason ?? "Y2 button mapping could not be applied.");
        }
    }

    // Button Y3 remap (JSON ButtonMapping: Type, GamepadAction, KeyboardKeys[], MouseButton)
    internal class LegionButtonY3Property : HelperProperty<string, LegionManager>, IHardwareApplyResult
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public bool LastApplySucceeded { get; private set; } = true;
        public string LastApplyFailureReason { get; private set; }

        public LegionButtonY3Property(string initialValue, LegionManager inManager) : base(initialValue ?? "", null, Function.LegionButtonY3, inManager)
        {
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);
            Logger.Info($"LegionButtonY3 applying mapping: {Value}");
            var (type, gamepadAction, keyboardKeys, mouseButton) = ButtonMappingParser.Parse(Value);
            string reason = "Legion manager is not available.";
            LastApplySucceeded = Manager != null && Manager.SetButtonMappingAdvanced(2, type, ButtonMappingParser.GetMappingValues(type, gamepadAction, keyboardKeys, mouseButton), out reason);
            LastApplyFailureReason = LastApplySucceeded ? null : (reason ?? "Y3 button mapping could not be applied.");
        }
    }

    // Button M1 remap (JSON ButtonMapping: Type, GamepadAction, KeyboardKeys[], MouseButton)
    internal class LegionButtonM1Property : HelperProperty<string, LegionManager>, IHardwareApplyResult
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public bool LastApplySucceeded { get; private set; } = true;
        public string LastApplyFailureReason { get; private set; }

        public LegionButtonM1Property(string initialValue, LegionManager inManager) : base(initialValue ?? "", null, Function.LegionButtonM1, inManager)
        {
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);
            Logger.Info($"LegionButtonM1 applying mapping: {Value}");
            var (type, gamepadAction, keyboardKeys, mouseButton) = ButtonMappingParser.Parse(Value);
            string reason = "Legion manager is not available.";
            LastApplySucceeded = Manager != null && Manager.SetButtonMappingAdvanced(3, type, ButtonMappingParser.GetMappingValues(type, gamepadAction, keyboardKeys, mouseButton), out reason);
            LastApplyFailureReason = LastApplySucceeded ? null : (reason ?? "M1 button mapping could not be applied.");
        }
    }

    // Button M2 remap (JSON ButtonMapping: Type, GamepadAction, KeyboardKeys[], MouseButton)
    internal class LegionButtonM2Property : HelperProperty<string, LegionManager>, IHardwareApplyResult
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public bool LastApplySucceeded { get; private set; } = true;
        public string LastApplyFailureReason { get; private set; }

        public LegionButtonM2Property(string initialValue, LegionManager inManager) : base(initialValue ?? "", null, Function.LegionButtonM2, inManager)
        {
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);
            Logger.Info($"LegionButtonM2 applying mapping: {Value}");
            var (type, gamepadAction, keyboardKeys, mouseButton) = ButtonMappingParser.Parse(Value);
            string reason = "Legion manager is not available.";
            LastApplySucceeded = Manager != null && Manager.SetButtonMappingAdvanced(4, type, ButtonMappingParser.GetMappingValues(type, gamepadAction, keyboardKeys, mouseButton), out reason);
            LastApplyFailureReason = LastApplySucceeded ? null : (reason ?? "M2 button mapping could not be applied.");
        }
    }

    // Button M3 remap (JSON ButtonMapping: Type, GamepadAction, KeyboardKeys[], MouseButton)
    internal class LegionButtonM3Property : HelperProperty<string, LegionManager>, IHardwareApplyResult
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public bool LastApplySucceeded { get; private set; } = true;
        public string LastApplyFailureReason { get; private set; }

        public LegionButtonM3Property(string initialValue, LegionManager inManager) : base(initialValue ?? "", null, Function.LegionButtonM3, inManager)
        {
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);
            Logger.Info($"LegionButtonM3 applying mapping: {Value}");
            var (type, gamepadAction, keyboardKeys, mouseButton) = ButtonMappingParser.Parse(Value);
            string reason = "Legion manager is not available.";
            LastApplySucceeded = Manager != null && Manager.SetButtonMappingAdvanced(5, type, ButtonMappingParser.GetMappingValues(type, gamepadAction, keyboardKeys, mouseButton), out reason);
            LastApplyFailureReason = LastApplySucceeded ? null : (reason ?? "M3 button mapping could not be applied.");
        }
    }

    // Button Desktop remap (JSON ButtonMapping: Type, GamepadAction, KeyboardKeys[], MouseButton)
    // Default: Win+G (Game Bar)
    internal class LegionButtonDesktopProperty : HelperProperty<string, LegionManager>, IHardwareApplyResult
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public bool LastApplySucceeded { get; private set; } = true;
        public string LastApplyFailureReason { get; private set; }

        public LegionButtonDesktopProperty(string initialValue, LegionManager inManager) : base(initialValue ?? "", null, Function.LegionButtonDesktop, inManager)
        {
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);
            Logger.Info($"LegionButtonDesktop applying mapping: {Value}");
            var (type, gamepadAction, keyboardKeys, mouseButton) = ButtonMappingParser.Parse(Value);
            string reason = "Legion manager is not available.";
            LastApplySucceeded = Manager != null && Manager.SetLegionButtonMapping(GamepadButton.DesktopButton, type, ButtonMappingParser.GetMappingValues(type, gamepadAction, keyboardKeys, mouseButton), out reason);
            LastApplyFailureReason = LastApplySucceeded ? null : (reason ?? "Desktop button mapping could not be applied.");
        }
    }

    // Button Page remap (JSON ButtonMapping: Type, GamepadAction, KeyboardKeys[], MouseButton)
    // Default: Win+Tab (Task View)
    internal class LegionButtonPageProperty : HelperProperty<string, LegionManager>, IHardwareApplyResult
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public bool LastApplySucceeded { get; private set; } = true;
        public string LastApplyFailureReason { get; private set; }

        public LegionButtonPageProperty(string initialValue, LegionManager inManager) : base(initialValue ?? "", null, Function.LegionButtonPage, inManager)
        {
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);
            Logger.Info($"LegionButtonPage applying mapping: {Value}");
            var (type, gamepadAction, keyboardKeys, mouseButton) = ButtonMappingParser.Parse(Value);
            string reason = "Legion manager is not available.";
            LastApplySucceeded = Manager != null && Manager.SetLegionButtonMapping(GamepadButton.PageButton, type, ButtonMappingParser.GetMappingValues(type, gamepadAction, keyboardKeys, mouseButton), out reason);
            LastApplyFailureReason = LastApplySucceeded ? null : (reason ?? "Page button mapping could not be applied.");
        }
    }

    // Nintendo layout toggle (A↔B, X↔Y swap)
    internal class LegionNintendoLayoutProperty : HelperProperty<bool, LegionManager>, IHardwareApplyResult
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public bool LastApplySucceeded { get; private set; } = true;
        public string LastApplyFailureReason { get; private set; }

        public LegionNintendoLayoutProperty(bool initialValue, LegionManager inManager) : base(initialValue, null, Function.LegionNintendoLayout, inManager)
        {
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);
            Logger.Info($"LegionNintendoLayout changed to {Value}");
            string reason = "Legion manager is not available.";
            LastApplySucceeded = Manager != null && Manager.SetNintendoLayout(Value, out reason);
            LastApplyFailureReason = LastApplySucceeded ? null : (reason ?? "Nintendo layout could not be applied.");
        }
    }

    // Vibration mode preset (FPS=1, Racing=2, AVG=3, SPG=4, RPG=5)
    internal class LegionVibrationModeProperty : HelperProperty<int, LegionManager>
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public LegionVibrationModeProperty(int initialValue, LegionManager inManager) : base(initialValue, null, Function.LegionVibrationMode, inManager)
        {
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);
            Logger.Info($"LegionVibrationMode changed to {Value}");
            Manager?.SetVibrationMode(Value);
        }
    }

    // Controller profile enabled (per-game toggle) - notification only, storage in widget
    internal class LegionControllerProfileEnabledProperty : HelperProperty<bool, LegionManager>
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public LegionControllerProfileEnabledProperty(bool initialValue, LegionManager inManager) : base(initialValue, null, Function.LegionControllerProfileEnabled, inManager)
        {
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);
            Logger.Info($"LegionControllerProfileEnabled changed to {Value}");
            // No action needed - profile storage is handled by the widget
        }
    }

    // Gyro Target (0=Disabled, 1=LeftStick, 2=RightStick, 3=Mouse)
    internal class LegionGyroTargetProperty : HelperProperty<int, LegionManager>
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public LegionGyroTargetProperty(int initialValue, LegionManager inManager) : base(initialValue, null, Function.LegionGyroTarget, inManager)
        {
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);
            Logger.Info($"LegionGyroTarget changed to {Value}");
            Manager?.SetGyroTarget(Value);
        }
    }

    // Gyro Sensitivity X (1-100)
    internal class LegionGyroSensitivityXProperty : HelperProperty<int, LegionManager>
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public LegionGyroSensitivityXProperty(int initialValue, LegionManager inManager) : base(initialValue, null, Function.LegionGyroSensitivityX, inManager)
        {
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);
            Logger.Info($"LegionGyroSensitivityX changed to {Value}");
            Manager?.SetGyroSensitivityX(Value);
        }
    }

    // Gyro Sensitivity Y (1-100)
    internal class LegionGyroSensitivityYProperty : HelperProperty<int, LegionManager>
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public LegionGyroSensitivityYProperty(int initialValue, LegionManager inManager) : base(initialValue, null, Function.LegionGyroSensitivityY, inManager)
        {
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);
            Logger.Info($"LegionGyroSensitivityY changed to {Value}");
            Manager?.SetGyroSensitivityY(Value);
        }
    }

    // Gyro Invert X
    internal class LegionGyroInvertXProperty : HelperProperty<bool, LegionManager>
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public LegionGyroInvertXProperty(bool initialValue, LegionManager inManager) : base(initialValue, null, Function.LegionGyroInvertX, inManager)
        {
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);
            Logger.Info($"LegionGyroInvertX changed to {Value}");
            Manager?.SetGyroInvertX(Value);
        }
    }

    // Gyro Invert Y
    internal class LegionGyroInvertYProperty : HelperProperty<bool, LegionManager>
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public LegionGyroInvertYProperty(bool initialValue, LegionManager inManager) : base(initialValue, null, Function.LegionGyroInvertY, inManager)
        {
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);
            Logger.Info($"LegionGyroInvertY changed to {Value}");
            Manager?.SetGyroInvertY(Value);
        }
    }

    // Gyro Mapping Type (0=Instant, 1=Continuous)
    internal class LegionGyroMappingTypeProperty : HelperProperty<int, LegionManager>
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public LegionGyroMappingTypeProperty(int initialValue, LegionManager inManager) : base(initialValue, null, Function.LegionGyroMappingType, inManager)
        {
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);
            Logger.Info($"LegionGyroMappingType changed to {Value}");
            Manager?.SetGyroMappingType(Value);
        }
    }

    // Gyro Activation Mode (0=Hold, 1=Toggle)
    internal class LegionGyroActivationModeProperty : HelperProperty<int, LegionManager>
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public LegionGyroActivationModeProperty(int initialValue, LegionManager inManager) : base(initialValue, null, Function.LegionGyroActivationMode, inManager)
        {
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);
            Logger.Info($"LegionGyroActivationMode changed to {Value}");
            Manager?.SetGyroActivationMode(Value);
        }
    }

    // Gyro Activation Button (0-8: None, LB, LT, RB, RT, Y1, Y2, M2, M3)
    internal class LegionGyroActivationButtonProperty : HelperProperty<int, LegionManager>
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public LegionGyroActivationButtonProperty(int initialValue, LegionManager inManager) : base(initialValue, null, Function.LegionGyroActivationButton, inManager)
        {
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);
            Logger.Info($"LegionGyroActivationButton changed to {Value}");
            Manager?.SetGyroActivationButton(Value);
        }
    }

    // Gyro Deadzone (1-100)
    internal class LegionGyroDeadzoneProperty : HelperProperty<int, LegionManager>
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public LegionGyroDeadzoneProperty(int initialValue, LegionManager inManager) : base(initialValue, null, Function.LegionGyroDeadzone, inManager)
        {
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);
            Logger.Info($"LegionGyroDeadzone changed to {Value}");
            Manager?.SetGyroDeadzone(Value);
        }
    }

    // Left Stick Deadzone (0-50%)
    internal class LegionLeftStickDeadzoneProperty : HelperProperty<int, LegionManager>
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public LegionLeftStickDeadzoneProperty(int initialValue, LegionManager inManager) : base(initialValue, null, Function.LegionLeftStickDeadzone, inManager)
        {
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);
            Logger.Info($"LegionLeftStickDeadzone changed to {Value}");
            Manager?.SetLeftStickDeadzone(Value);
        }
    }

    // Right Stick Deadzone (0-50%)
    internal class LegionRightStickDeadzoneProperty : HelperProperty<int, LegionManager>
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public LegionRightStickDeadzoneProperty(int initialValue, LegionManager inManager) : base(initialValue, null, Function.LegionRightStickDeadzone, inManager)
        {
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);
            Logger.Info($"LegionRightStickDeadzone changed to {Value}");
            Manager?.SetRightStickDeadzone(Value);
        }
    }

    // Trigger Travel - Left Start (0-100%)
    internal class LegionLeftTriggerStartProperty : HelperProperty<int, LegionManager>
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public LegionLeftTriggerStartProperty(int initialValue, LegionManager inManager) : base(initialValue, null, Function.LegionLeftTriggerStart, inManager)
        {
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);
            Logger.Info($"LegionLeftTriggerStart changed to {Value}");
            // Send HID command with current Start and End values
            Manager?.SetLeftTriggerTravel(Value, Manager.LegionLeftTriggerEnd.Value);
        }
    }

    // Trigger Travel - Left End (0-100%)
    internal class LegionLeftTriggerEndProperty : HelperProperty<int, LegionManager>
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public LegionLeftTriggerEndProperty(int initialValue, LegionManager inManager) : base(initialValue, null, Function.LegionLeftTriggerEnd, inManager)
        {
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);
            Logger.Info($"LegionLeftTriggerEnd changed to {Value}");
            // Send the HID command when end value changes (start should already be set)
            Manager?.SetLeftTriggerTravel(Manager.LegionLeftTriggerStart.Value, Value);
        }
    }

    // Trigger Travel - Right Start (0-100%)
    internal class LegionRightTriggerStartProperty : HelperProperty<int, LegionManager>
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public LegionRightTriggerStartProperty(int initialValue, LegionManager inManager) : base(initialValue, null, Function.LegionRightTriggerStart, inManager)
        {
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);
            Logger.Info($"LegionRightTriggerStart changed to {Value}");
            // Send HID command with current Start and End values
            Manager?.SetRightTriggerTravel(Value, Manager.LegionRightTriggerEnd.Value);
        }
    }

    // Trigger Travel - Right End (0-100%)
    internal class LegionRightTriggerEndProperty : HelperProperty<int, LegionManager>
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public LegionRightTriggerEndProperty(int initialValue, LegionManager inManager) : base(initialValue, null, Function.LegionRightTriggerEnd, inManager)
        {
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);
            Logger.Info($"LegionRightTriggerEnd changed to {Value}");
            // Send the HID command when end value changes (start should already be set)
            Manager?.SetRightTriggerTravel(Manager.LegionRightTriggerStart.Value, Value);
        }
    }

    // Hair Triggers toggle (sets both triggers to 0%/1% for instant response)
    internal class LegionHairTriggersProperty : HelperProperty<bool, LegionManager>
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public LegionHairTriggersProperty(bool initialValue, LegionManager inManager) : base(initialValue, null, Function.LegionHairTriggers, inManager)
        {
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);
            Logger.Info($"LegionHairTriggers changed to {Value}");
            if (Value)
            {
                // Hair triggers: start at 0%, full at 1% (instant response)
                Manager?.SetLeftTriggerTravel(0, 1);
                Manager?.SetRightTriggerTravel(0, 1);
            }
            // When turned off, the individual slider values will be re-applied by the widget
        }
    }

    // Touchpad Vibration level (1=Off, 2=Low, 3=Medium, 4=High - GLOBAL setting)
    internal class LegionTouchpadVibrationProperty : HelperProperty<int, LegionManager>
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public LegionTouchpadVibrationProperty(int initialValue, LegionManager inManager) : base(initialValue, null, Function.LegionTouchpadVibration, inManager)
        {
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);
            string levelName = Value switch
            {
                1 => "Off",
                2 => "Low",
                3 => "Medium",
                4 => "High",
                _ => "Unknown"
            };
            Logger.Info($"LegionTouchpadVibration changed to {levelName} ({Value})");
            Manager?.SetTouchpadVibration(Value);
        }
    }

    // Joystick as Mouse Mode (0=Disabled, 1=Left Stick, 2=Right Stick)
    internal class LegionJoystickAsMouseModeProperty : HelperProperty<int, LegionManager>
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public LegionJoystickAsMouseModeProperty(int initialValue, LegionManager inManager) : base(initialValue, null, Function.LegionJoystickAsMouseMode, inManager)
        {
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);
            Logger.Info($"LegionJoystickAsMouseMode changed to {Value}");
            Manager?.SetJoystickAsMouseMode(Value);
        }
    }

    // Joystick Mouse Sensitivity (10-100)
    internal class LegionJoystickMouseSensProperty : HelperProperty<int, LegionManager>
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public LegionJoystickMouseSensProperty(int initialValue, LegionManager inManager) : base(initialValue, null, Function.LegionJoystickMouseSens, inManager)
        {
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);
            Logger.Info($"LegionJoystickMouseSens changed to {Value}");
            Manager?.SetJoystickMouseSens(Value);
        }
    }

    // Gamepad Button Mapping (JSON string for all 24 buttons)
    internal class LegionGamepadMappingProperty : HelperProperty<string, LegionManager>
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public LegionGamepadMappingProperty(string initialValue, LegionManager inManager) : base(initialValue ?? "", null, Function.LegionGamepadButtonMapping, inManager)
        {
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);
            Logger.Info($"LegionGamepadMapping changed to {Value}");
            Manager?.ApplyGamepadButtonMappings(Value);
        }
    }

    // Desktop Controls Preset (state tracking - actual application via JoystickAsMouseMode + GamepadButtonMapping)
    internal class LegionDesktopControlsProperty : HelperProperty<bool, LegionManager>
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public LegionDesktopControlsProperty(bool initialValue, LegionManager inManager) : base(initialValue, null, Function.LegionDesktopControls, inManager)
        {
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);
            Logger.Info($"LegionDesktopControls changed to {Value}");
            // Note: Actual application happens through JoystickAsMouseMode and GamepadButtonMapping
            // properties, so this property mainly serves as state tracking for the UI toggle
        }
    }

    // Controller Battery Left (read-only, 1-100 or -1 if unavailable)
    internal class ControllerBatteryLeftProperty : HelperProperty<int, LegionManager>
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public ControllerBatteryLeftProperty(int initialValue, LegionManager inManager) : base(initialValue, null, Function.ControllerBatteryLeft, inManager)
        {
        }

        public void SetValueAndSync(int value)
        {
            SetValue((object)value);
            SyncToRemote();
        }
    }

    // Controller Battery Right (read-only, 1-100 or -1 if unavailable)
    internal class ControllerBatteryRightProperty : HelperProperty<int, LegionManager>
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public ControllerBatteryRightProperty(int initialValue, LegionManager inManager) : base(initialValue, null, Function.ControllerBatteryRight, inManager)
        {
        }

        public void SetValueAndSync(int value)
        {
            SetValue((object)value);
            SyncToRemote();
        }
    }

    // Controller Charging Left (read-only, true if charging)
    internal class ControllerChargingLeftProperty : HelperProperty<bool, LegionManager>
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public ControllerChargingLeftProperty(bool initialValue, LegionManager inManager) : base(initialValue, null, Function.ControllerChargingLeft, inManager)
        {
        }

        public void SetValueAndSync(bool value)
        {
            SetValue((object)value);
            SyncToRemote();
        }
    }

    // Controller Charging Right (read-only, true if charging)
    internal class ControllerChargingRightProperty : HelperProperty<bool, LegionManager>
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public ControllerChargingRightProperty(bool initialValue, LegionManager inManager) : base(initialValue, null, Function.ControllerChargingRight, inManager)
        {
        }

        public void SetValueAndSync(bool value)
        {
            SetValue((object)value);
            SyncToRemote();
        }
    }

    // Controller Docked Left/Right (read-only, true when the half is physically docked;
    // a detached-but-wirelessly-linked half is Connected=true, Docked=false).
    internal class ControllerDockedLeftProperty : HelperProperty<bool, LegionManager>
    {
        public ControllerDockedLeftProperty(bool initialValue, LegionManager inManager) : base(initialValue, null, Function.ControllerDockedLeft, inManager)
        {
        }

        public void SetValueAndSync(bool value)
        {
            SetValue((object)value);
            SyncToRemote();
        }
    }

    internal class ControllerDockedRightProperty : HelperProperty<bool, LegionManager>
    {
        public ControllerDockedRightProperty(bool initialValue, LegionManager inManager) : base(initialValue, null, Function.ControllerDockedRight, inManager)
        {
        }

        public void SetValueAndSync(bool value)
        {
            SetValue((object)value);
            SyncToRemote();
        }
    }

    // Receiver/MCU firmware version string, read once per connect via GET_VERSION_DATA.
    internal class LegionMcuFirmwareVersionProperty : HelperProperty<string, LegionManager>
    {
        public LegionMcuFirmwareVersionProperty(string initialValue, LegionManager inManager) : base(initialValue, null, Function.LegionMcuFirmwareVersion, inManager)
        {
        }

        public void SetValueAndSync(string value)
        {
            SetValue((object)value);
            SyncToRemote();
        }
    }

    // Controller input mode read from firmware (0=unknown, 1=XInput, 2=DInput, 3=FPS).
    // Fed by LegionButtonMonitor's GET_FEATURE(gamepad mode / FPS switch) responses;
    // rendered as a pill on the widget's Legion tab.
    internal class LegionControllerInputModeProperty : HelperProperty<int, LegionManager>
    {
        public LegionControllerInputModeProperty(int initialValue, LegionManager inManager) : base(initialValue, null, Function.LegionControllerInputMode, inManager)
        {
        }

        public void SetValueAndSync(int value)
        {
            SetValue((object)value);
            SyncToRemote();
        }
    }

    // Controller Connected Left (read-only, true if controller is connected/attached)
    internal class ControllerConnectedLeftProperty : HelperProperty<bool, LegionManager>
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public ControllerConnectedLeftProperty(bool initialValue, LegionManager inManager) : base(initialValue, null, Function.ControllerConnectedLeft, inManager)
        {
        }

        public void SetValueAndSync(bool value)
        {
            SetValue((object)value);
            SyncToRemote();
        }
    }

    // Controller Connected Right (read-only, true if controller is connected/attached)
    internal class ControllerConnectedRightProperty : HelperProperty<bool, LegionManager>
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public ControllerConnectedRightProperty(bool initialValue, LegionManager inManager) : base(initialValue, null, Function.ControllerConnectedRight, inManager)
        {
        }

        public void SetValueAndSync(bool value)
        {
            SetValue((object)value);
            SyncToRemote();
        }
    }

    // Controller VID:PID (read-only, e.g., "17EF:6182")
    internal class ControllerVidPidProperty : HelperProperty<string, LegionManager>
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public ControllerVidPidProperty(string initialValue, LegionManager inManager) : base(initialValue, null, Function.ControllerVidPid, inManager)
        {
        }

        public void SetValueAndSync(string value)
        {
            SetValue((object)value);
            SyncToRemote();
        }
    }

    // Controller Device Status (read-only, JSON LegionGoStatus snapshot from b0:01)
    internal class ControllerDeviceStatusProperty : HelperProperty<string, LegionManager>
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public ControllerDeviceStatusProperty(string initialValue, LegionManager inManager) : base(initialValue, null, Function.ControllerDeviceStatus, inManager)
        {
        }

        public void SetValueAndSync(string value)
        {
            SetValue((object)value);
            SyncToRemote();
        }
    }

    // Device Display Name (read-only, e.g., "Legion Go", "Legion Go 2", "Legion Go S")
    internal class DeviceDisplayNameProperty : HelperProperty<string, LegionManager>
    {
        public DeviceDisplayNameProperty(string initialValue, LegionManager inManager) : base(initialValue, null, Function.DeviceDisplayName, inManager)
        {
        }

        public void SetValueAndSync(string value)
        {
            SetValue((object)value);
            SyncToRemote();
        }
    }

    // Device Supports Controller Remap (read-only, based on device capabilities)
    internal class DeviceSupportsControllerRemapProperty : HelperProperty<bool, LegionManager>
    {
        public DeviceSupportsControllerRemapProperty(bool initialValue, LegionManager inManager) : base(initialValue, null, Function.DeviceSupportsControllerRemap, inManager)
        {
        }

        public void SetValueAndSync(bool value)
        {
            SetValue((object)value);
            SyncToRemote();
        }
    }

    // Device Supports RGB Lighting (read-only, based on device capabilities)
    internal class DeviceSupportsRgbLightingProperty : HelperProperty<bool, LegionManager>
    {
        public DeviceSupportsRgbLightingProperty(bool initialValue, LegionManager inManager) : base(initialValue, null, Function.DeviceSupportsRgbLighting, inManager)
        {
        }

        public void SetValueAndSync(bool value)
        {
            SetValue((object)value);
            SyncToRemote();
        }
    }

    // Device Supports Gyro (read-only, based on device capabilities)
    internal class DeviceSupportsGyroProperty : HelperProperty<bool, LegionManager>
    {
        public DeviceSupportsGyroProperty(bool initialValue, LegionManager inManager) : base(initialValue, null, Function.DeviceSupportsGyro, inManager)
        {
        }

        public void SetValueAndSync(bool value)
        {
            SetValue((object)value);
            SyncToRemote();
        }
    }

    // Device Has Scroll Wheel (read-only, based on device capabilities)
    internal class DeviceHasScrollWheelProperty : HelperProperty<bool, LegionManager>
    {
        public DeviceHasScrollWheelProperty(bool initialValue, LegionManager inManager) : base(initialValue, null, Function.DeviceHasScrollWheel, inManager)
        {
        }

        public void SetValueAndSync(bool value)
        {
            SetValue((object)value);
            SyncToRemote();
        }
    }

    // Device Has Detachable Controllers (read-only, based on device capabilities)
    internal class DeviceHasDetachableControllersProperty : HelperProperty<bool, LegionManager>
    {
        public DeviceHasDetachableControllersProperty(bool initialValue, LegionManager inManager) : base(initialValue, null, Function.DeviceHasDetachableControllers, inManager)
        {
        }

        public void SetValueAndSync(bool value)
        {
            SetValue((object)value);
            SyncToRemote();
        }
    }

    // Device Has Touchpad (read-only, based on device capabilities - uses HID)
    internal class DeviceHasTouchpadProperty : HelperProperty<bool, LegionManager>
    {
        public DeviceHasTouchpadProperty(bool initialValue, LegionManager inManager) : base(initialValue, null, Function.DeviceHasTouchpad, inManager)
        {
        }

        public void SetValueAndSync(bool value)
        {
            SetValue((object)value);
            SyncToRemote();
        }
    }
}
