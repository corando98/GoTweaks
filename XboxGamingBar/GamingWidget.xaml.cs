using Microsoft.Gaming.XboxGameBar;
using Microsoft.Gaming.XboxGameBar.Input;
using Microsoft.UI.Xaml.Controls;
using NLog;
using Shared.Data;
using Shared.Utilities;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Data.Json;
using Windows.Foundation;
using Windows.Foundation.Metadata;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;
using Windows.System.Power;
using Windows.Storage;
using Windows.System;
using Windows.UI.Xaml.Input;
using System.Runtime.InteropServices;
using Windows.UI;
using XboxGamingBar.Data;
using XboxGamingBar.Event;
using XboxGamingBar.IPC;
using XboxGamingBar.QuickSettings;
using Shared.Enums;

// The Blank Page item template is documented at https://go.microsoft.com/fwlink/?LinkId=402352&clcid=0x409

namespace XboxGamingBar
{
    /// <summary>
    /// Profile settings storage
    /// </summary>
    public class PerformanceProfile
    {
        public double TDP { get; set; } = 15;
        public bool CPUBoost { get; set; } = false;
        public double CPUEPP { get; set; } = 0;
        public int MaxCPUState { get; set; } = 100;
        public int MinCPUState { get; set; } = 5;
        public bool FluidMotionFrames { get; set; } = false;
        public bool RadeonSuperResolution { get; set; } = false;
        public double RadeonSuperResolutionSharpness { get; set; } = 80;
        public bool ImageSharpening { get; set; } = false;
        public double ImageSharpeningSharpness { get; set; } = 80;
        public bool RadeonAntiLag { get; set; } = false;
        public bool RadeonBoost { get; set; } = false;
        public double RadeonBoostResolution { get; set; } = 0;
        public bool RadeonChill { get; set; } = false;
        public double RadeonChillMinFPS { get; set; } = 30;
        public double RadeonChillMaxFPS { get; set; } = 60;
        // FPS Limit settings
        public bool FPSLimitEnabled { get; set; } = false;
        public int FPSLimitValue { get; set; } = 60;
        // AutoTDP settings
        public bool AutoTDPEnabled { get; set; } = false;
        public int AutoTDPTargetFPS { get; set; } = 60;
        public int AutoTDPMinTDP { get; set; } = 8;
        public int AutoTDPMaxTDP { get; set; } = 30;
        public bool AutoTDPUseMLMode { get; set; } = false;
        public int AutoTDPControllerType { get; set; } = 0;  // 0=PID, 1=Q-Learning, 2=SARSA
        // OS Power Mode (0=Best Power Efficiency, 1=Balanced, 2=Best Performance)
        public int OSPowerMode { get; set; } = 1;
        // Legion Performance Mode (1=Quiet, 2=Balanced, 3=Performance, 255=Custom)
        public int LegionPerformanceMode { get; set; } = 2;
        // TDP Mode Index in the TDPModeComboBox (for user-made presets)
        // -1 means use LegionPerformanceMode to determine index (backwards compatibility)
        public int TDPModeIndex { get; set; } = -1;
        // TDP Boost toggle state (per-profile)
        public bool TDPBoostEnabled { get; set; } = false;
        // TDP Boost deltas (per-profile): SPPL = TDP + TDPBoostSPPT, FPPT = TDP + TDPBoostFPPT.
        // Replaces the previous global helper settings so each profile picks its own boost.
        public int TDPBoostSPPT { get; set; } = 1;
        public int TDPBoostFPPT { get; set; } = 3;
        // HDR and Resolution settings (per-profile)
        public bool HDREnabled { get; set; } = false;
        public string Resolution { get; set; } = "";
        public int? RefreshRate { get; set; } = null;
        // Sticky TDP settings (per-profile)
        public bool StickyTDPEnabled { get; set; } = true;
        public int StickyTDPInterval { get; set; } = 5;
        // Overlay Level (0=Off, 1-4 for RTSS/AMD)
        public int OverlayLevel { get; set; } = 0;
        // CPU Affinity as "pCores,eCores" string
        public string CPUAffinity { get; set; } = "";

        public PerformanceProfile Clone()
        {
            return new PerformanceProfile
            {
                TDP = this.TDP,
                CPUBoost = this.CPUBoost,
                CPUEPP = this.CPUEPP,
                MaxCPUState = this.MaxCPUState,
                MinCPUState = this.MinCPUState,
                FluidMotionFrames = this.FluidMotionFrames,
                RadeonSuperResolution = this.RadeonSuperResolution,
                RadeonSuperResolutionSharpness = this.RadeonSuperResolutionSharpness,
                ImageSharpening = this.ImageSharpening,
                ImageSharpeningSharpness = this.ImageSharpeningSharpness,
                RadeonAntiLag = this.RadeonAntiLag,
                RadeonBoost = this.RadeonBoost,
                RadeonBoostResolution = this.RadeonBoostResolution,
                RadeonChill = this.RadeonChill,
                RadeonChillMinFPS = this.RadeonChillMinFPS,
                RadeonChillMaxFPS = this.RadeonChillMaxFPS,
                FPSLimitEnabled = this.FPSLimitEnabled,
                FPSLimitValue = this.FPSLimitValue,
                AutoTDPEnabled = this.AutoTDPEnabled,
                AutoTDPTargetFPS = this.AutoTDPTargetFPS,
                AutoTDPMinTDP = this.AutoTDPMinTDP,
                AutoTDPMaxTDP = this.AutoTDPMaxTDP,
                AutoTDPUseMLMode = this.AutoTDPUseMLMode,
                AutoTDPControllerType = this.AutoTDPControllerType,
                OSPowerMode = this.OSPowerMode,
                LegionPerformanceMode = this.LegionPerformanceMode,
                TDPModeIndex = this.TDPModeIndex,
                TDPBoostEnabled = this.TDPBoostEnabled,
                TDPBoostSPPT = this.TDPBoostSPPT,
                TDPBoostFPPT = this.TDPBoostFPPT,
                HDREnabled = this.HDREnabled,
                Resolution = this.Resolution,
                RefreshRate = this.RefreshRate,
                StickyTDPEnabled = this.StickyTDPEnabled,
                StickyTDPInterval = this.StickyTDPInterval,
                OverlayLevel = this.OverlayLevel,
                CPUAffinity = this.CPUAffinity
            };
        }
    }

    /// <summary>
    /// Theme color palette definition
    /// </summary>
    public class ThemeColors
    {
        public string Name { get; set; }
        public Windows.UI.Color PageBackground { get; set; }
        public Windows.UI.Color CardBackground { get; set; }
        public Windows.UI.Color CardBorder { get; set; }
        public Windows.UI.Color AccentColor { get; set; }
        public Windows.UI.Color TextPrimary { get; set; }
        public Windows.UI.Color TextSecondary { get; set; }
        public Windows.UI.Color ButtonBackground { get; set; }
        public Windows.UI.Color ButtonBorder { get; set; }
        public Windows.UI.Color TileOff { get; set; }
        public Windows.UI.Color TileOn { get; set; }
    }

    /// <summary>
    /// View model for OSD item in the reorderable list
    /// </summary>
    public class OSDItemViewModel : INotifyPropertyChanged
    {
        public string Id { get; set; }
        public string DisplayName { get; set; }

        private bool _isEnabled;
        public bool IsEnabled
        {
            get => _isEnabled;
            set { _isEnabled = value; OnPropertyChanged(); }
        }

        private bool _canMoveUp;
        public bool CanMoveUp
        {
            get => _canMoveUp;
            set { _canMoveUp = value; OnPropertyChanged(); }
        }

        private bool _canMoveDown;
        public bool CanMoveDown
        {
            get => _canMoveDown;
            set { _canMoveDown = value; OnPropertyChanged(); }
        }

        private string _labelColor = "DEFAULT";
        public string LabelColor
        {
            get => _labelColor;
            set { _labelColor = value; OnPropertyChanged(); OnPropertyChanged(nameof(LabelColorBrush)); }
        }

        public Windows.UI.Xaml.Media.SolidColorBrush LabelColorBrush
        {
            get
            {
                if (string.IsNullOrEmpty(_labelColor) || _labelColor == "DEFAULT")
                    return new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Colors.Gray);
                try
                {
                    byte r = Convert.ToByte(_labelColor.Substring(0, 2), 16);
                    byte g = Convert.ToByte(_labelColor.Substring(2, 2), 16);
                    byte b = Convert.ToByte(_labelColor.Substring(4, 2), 16);
                    return new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, r, g, b));
                }
                catch { return new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Colors.Gray); }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// Hotkey action types for Xbox controller button combos
    /// </summary>
    public enum HotkeyAction
    {
        Disabled = 0,
        KeyboardKey = 1,
        KeyboardShortcut = 2,
        ToggleOSD = 3,
        Screenshot = 4,
        AltTab = 5,
        AltF4 = 6,
        OpenKeyboard = 7,
        CtrlAltDel = 8,
        TaskManager = 9,
        FocusGoTweaks = 10
    }

    /// <summary>
    /// View model for saved controller profile display
    /// </summary>
    public class SavedProfileInfo : System.ComponentModel.INotifyPropertyChanged
    {
        public string ProfileKey { get; set; }
        public string GameName { get; set; }
        public string SettingsSummary { get; set; }
        public bool IsGlobal { get; set; }
        public Windows.UI.Xaml.Visibility CanDelete => IsGlobal ? Windows.UI.Xaml.Visibility.Collapsed : Windows.UI.Xaml.Visibility.Visible;

        // Game exe path for icon loading
        public string GameExePath { get; set; }

        // Game icon support
        private Windows.UI.Xaml.Media.ImageSource _iconSource;
        public Windows.UI.Xaml.Media.ImageSource IconSource
        {
            get => _iconSource;
            set
            {
                _iconSource = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IconSource)));
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IconVisibility)));
            }
        }
        public Windows.UI.Xaml.Visibility IconVisibility => IconSource != null ? Windows.UI.Xaml.Visibility.Visible : Windows.UI.Xaml.Visibility.Collapsed;

        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
    }

    /// <summary>
    /// Button mapping with type support (Gamepad, Keyboard, Mouse)
    /// </summary>
    public class ButtonMapping
    {
        /// <summary>Mapping type: 0=Gamepad, 1=Keyboard, 2=Mouse</summary>
        public int Type { get; set; } = 0;
        /// <summary>Gamepad action index (0-24, into RemapAction)</summary>
        public int GamepadAction { get; set; } = 0;
        /// <summary>Gamepad remap mode: 0=Single, 1=Combo</summary>
        public int GamepadMode { get; set; } = 0;
        /// <summary>Gamepad combo action indices (into RemapAction)</summary>
        public List<int> GamepadActions { get; set; } = new List<int>();
        /// <summary>Turbo on/off while held (improved emulation path)</summary>
        public bool Turbo { get; set; } = false;
        /// <summary>Keyboard key codes (up to 5 keys)</summary>
        public List<int> KeyboardKeys { get; set; } = new List<int>();
        /// <summary>Mouse button code (1-7)</summary>
        public int MouseButton { get; set; } = 0;

        /// <summary>
        /// Returns true if this mapping represents "no mapping" / default state.
        /// A mapping is default if Type=0 (Gamepad) with GamepadAction=0 (Disabled).
        /// </summary>
        public bool IsDefault => Type == 0 && GamepadAction == 0 && (GamepadActions == null || GamepadActions.Count == 0);

        public ButtonMapping Clone() => new ButtonMapping
        {
            Type = this.Type,
            GamepadAction = this.GamepadAction,
            GamepadMode = this.GamepadMode,
            GamepadActions = new List<int>(this.GamepadActions),
            Turbo = this.Turbo,
            KeyboardKeys = new List<int>(this.KeyboardKeys),
            MouseButton = this.MouseButton
        };

        /// <summary>
        /// Serializes to JSON string format for IPC/storage.
        /// Format: {"Type":0,"GamepadAction":5,"KeyboardKeys":[4,5],"MouseButton":0}
        /// </summary>
        public string ToJson()
        {
            var gamepadActions = GamepadActions.Count > 0 ? string.Join(",", GamepadActions) : "";
            var keys = KeyboardKeys.Count > 0 ? string.Join(",", KeyboardKeys) : "";
            string turboJson = Turbo ? "true" : "false";
            return $"{{\"Type\":{Type},\"GamepadAction\":{GamepadAction},\"GamepadMode\":{GamepadMode},\"GamepadActions\":[{gamepadActions}],\"Turbo\":{turboJson},\"KeyboardKeys\":[{keys}],\"MouseButton\":{MouseButton}}}";
        }

        /// <summary>
        /// Deserializes from JSON string. Returns new ButtonMapping if parsing fails.
        /// </summary>
        public static ButtonMapping FromJson(string json)
        {
            var result = new ButtonMapping();
            if (string.IsNullOrEmpty(json)) return result;

            try
            {
                // Parse Type
                var typeMatch = System.Text.RegularExpressions.Regex.Match(json, "\"Type\"\\s*:\\s*(-?\\d+)");
                if (typeMatch.Success && int.TryParse(typeMatch.Groups[1].Value, out int type))
                    result.Type = type;

                // Parse GamepadAction
                var gamepadMatch = System.Text.RegularExpressions.Regex.Match(json, "\"GamepadAction\"\\s*:\\s*(-?\\d+)");
                if (gamepadMatch.Success && int.TryParse(gamepadMatch.Groups[1].Value, out int gamepadAction))
                    result.GamepadAction = gamepadAction;

                // Parse GamepadMode
                var gamepadModeMatch = System.Text.RegularExpressions.Regex.Match(json, "\"GamepadMode\"\\s*:\\s*(-?\\d+)");
                if (gamepadModeMatch.Success && int.TryParse(gamepadModeMatch.Groups[1].Value, out int gamepadMode))
                    result.GamepadMode = gamepadMode;

                // Parse Turbo
                var turboMatch = System.Text.RegularExpressions.Regex.Match(json, "\"Turbo\"\\s*:\\s*(true|false|0|1)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (turboMatch.Success)
                {
                    string rawTurbo = turboMatch.Groups[1].Value;
                    result.Turbo = string.Equals(rawTurbo, "true", StringComparison.OrdinalIgnoreCase) || rawTurbo == "1";
                }

                // Parse MouseButton
                var mouseMatch = System.Text.RegularExpressions.Regex.Match(json, "\"MouseButton\"\\s*:\\s*(-?\\d+)");
                if (mouseMatch.Success && int.TryParse(mouseMatch.Groups[1].Value, out int mouseButton))
                    result.MouseButton = mouseButton;

                // Parse GamepadActions array
                var gamepadActionsMatch = System.Text.RegularExpressions.Regex.Match(json, "\"GamepadActions\"\\s*:\\s*\\[([^\\]]*)\\]");
                if (gamepadActionsMatch.Success)
                {
                    var actionsStr = gamepadActionsMatch.Groups[1].Value;
                    if (!string.IsNullOrWhiteSpace(actionsStr))
                    {
                        foreach (var part in actionsStr.Split(','))
                        {
                            if (int.TryParse(part.Trim(), out int action))
                                result.GamepadActions.Add(action);
                        }
                    }
                }

                // Parse KeyboardKeys array
                var keysMatch = System.Text.RegularExpressions.Regex.Match(json, "\"KeyboardKeys\"\\s*:\\s*\\[([^\\]]*)\\]");
                if (keysMatch.Success)
                {
                    var keysStr = keysMatch.Groups[1].Value;
                    if (!string.IsNullOrWhiteSpace(keysStr))
                    {
                        foreach (var part in keysStr.Split(','))
                        {
                            if (int.TryParse(part.Trim(), out int key))
                                result.KeyboardKeys.Add(key);
                        }
                    }
                }
            }
            catch
            {
                // Return default on any parse error
            }

            if (result.GamepadAction <= 0 && result.GamepadActions.Count > 0)
            {
                result.GamepadAction = result.GamepadActions[0];
            }
            else if (result.GamepadActions.Count == 0 && result.GamepadAction > 0)
            {
                result.GamepadActions.Add(result.GamepadAction);
            }

            return result;
        }
    }

    /// <summary>
    /// Controller profile settings storage for per-game button remapping
    /// </summary>
    public class ControllerProfile
    {
        public ButtonMapping ButtonY1 { get; set; } = new ButtonMapping();
        public ButtonMapping ButtonY2 { get; set; } = new ButtonMapping();
        public ButtonMapping ButtonY3 { get; set; } = new ButtonMapping();
        public ButtonMapping ButtonM1 { get; set; } = new ButtonMapping();
        public ButtonMapping ButtonM2 { get; set; } = new ButtonMapping();
        public ButtonMapping ButtonM3 { get; set; } = new ButtonMapping();
        public ButtonMapping ButtonDesktop { get; set; } = new ButtonMapping();
        public ButtonMapping ButtonPage { get; set; } = new ButtonMapping();
        public bool NintendoLayout { get; set; } = false;
        public int VibrationLevel { get; set; } = 2;  // Medium
        public int VibrationMode { get; set; } = 1;   // FPS

        // Gyro settings (per-game profile)
        public int GyroTarget { get; set; } = 0;           // Disabled
        public int GyroSensitivityX { get; set; } = 50;
        public int GyroSensitivityY { get; set; } = 50;
        public bool GyroInvertX { get; set; } = false;
        public bool GyroInvertY { get; set; } = false;
        public int GyroMappingType { get; set; } = 0;      // Instant
        public int GyroActivationMode { get; set; } = 0;   // Hold
        public int GyroActivationButton { get; set; } = 0; // None

        // Advanced gyro settings (per-game profile)
        public int GyroDeadzone { get; set; } = 10;         // 1-100

        // Stick deadzones (per-game profile)
        public int LeftStickDeadzone { get; set; } = 4;    // Default 4%
        public int RightStickDeadzone { get; set; } = 4;

        // Trigger travel (per-game profile)
        public int LeftTriggerStart { get; set; } = 0;     // Start % (0-100)
        public int LeftTriggerEnd { get; set; } = 0;       // End % from full (0-100)
        public int RightTriggerStart { get; set; } = 0;
        public int RightTriggerEnd { get; set; } = 0;
        public bool HairTriggers { get; set; } = false;    // Hair triggers preset (0%/1%)

        // Joystick as mouse (per-game profile)
        public int JoystickAsMouseMode { get; set; } = 0;  // 0=Disabled, 1=Left Stick, 2=Right Stick
        public int JoystickMouseSens { get; set; } = 50;   // 10-100

        // Gamepad button remapping (per-game profile)
        public Dictionary<string, ButtonMapping> GamepadButtonMappings { get; set; } = new Dictionary<string, ButtonMapping>();

        // Desktop Controls preset (per-game profile)
        public bool DesktopControlsEnabled { get; set; } = false;

        // Lighting (per-game profile)
        public int LightMode { get; set; } = 1;          // 0=Off, 1=Solid, 2=Pulse, 3=Dynamic, 4=Spiral
        public byte LightColorR { get; set; } = 255;     // RGB color for Solid/Pulse modes
        public byte LightColorG { get; set; } = 255;
        public byte LightColorB { get; set; } = 255;
        public int LightSpeed { get; set; } = 50;        // Animation speed for dynamic modes
        public int LightBrightness { get; set; } = 50;   // Brightness level 0-100
        public bool PowerLight { get; set; } = true;     // Power button light on/off
        public bool HasExplicitLighting { get; set; } = false;  // True if lighting was explicitly saved in this profile

        public ControllerProfile Clone()
        {
            return new ControllerProfile
            {
                ButtonY1 = this.ButtonY1.Clone(),
                ButtonY2 = this.ButtonY2.Clone(),
                ButtonY3 = this.ButtonY3.Clone(),
                ButtonM1 = this.ButtonM1.Clone(),
                ButtonM2 = this.ButtonM2.Clone(),
                ButtonM3 = this.ButtonM3.Clone(),
                ButtonDesktop = this.ButtonDesktop.Clone(),
                ButtonPage = this.ButtonPage.Clone(),
                NintendoLayout = this.NintendoLayout,
                VibrationLevel = this.VibrationLevel,
                VibrationMode = this.VibrationMode,
                // Gyro settings
                GyroTarget = this.GyroTarget,
                GyroSensitivityX = this.GyroSensitivityX,
                GyroSensitivityY = this.GyroSensitivityY,
                GyroInvertX = this.GyroInvertX,
                GyroInvertY = this.GyroInvertY,
                GyroMappingType = this.GyroMappingType,
                GyroActivationMode = this.GyroActivationMode,
                GyroActivationButton = this.GyroActivationButton,
                // Advanced gyro settings
                GyroDeadzone = this.GyroDeadzone,
                // Stick deadzones
                LeftStickDeadzone = this.LeftStickDeadzone,
                RightStickDeadzone = this.RightStickDeadzone,
                // Trigger travel
                LeftTriggerStart = this.LeftTriggerStart,
                LeftTriggerEnd = this.LeftTriggerEnd,
                RightTriggerStart = this.RightTriggerStart,
                RightTriggerEnd = this.RightTriggerEnd,
                HairTriggers = this.HairTriggers,
                // Joystick as mouse
                JoystickAsMouseMode = this.JoystickAsMouseMode,
                JoystickMouseSens = this.JoystickMouseSens,
                // Gamepad button mappings
                GamepadButtonMappings = this.GamepadButtonMappings.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value.Clone()),
                // Desktop Controls preset
                DesktopControlsEnabled = this.DesktopControlsEnabled,
                // Lighting
                LightMode = this.LightMode,
                LightColorR = this.LightColorR,
                LightColorG = this.LightColorG,
                LightColorB = this.LightColorB,
                LightSpeed = this.LightSpeed,
                LightBrightness = this.LightBrightness,
                PowerLight = this.PowerLight,
                HasExplicitLighting = this.HasExplicitLighting
            };
        }
    }

    /// <summary>
    /// Represents a power plan for UI binding
    /// </summary>
    public class PowerPlanItem
    {
        public Guid Guid { get; set; }
        public string Name { get; set; }
        public override string ToString() => Name;
    }

    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class GamingWidget : Page, INotifyPropertyChanged
    {
        [DllImport("user32.dll")]
        private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        [StructLayout(LayoutKind.Sequential)]
        private struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private static readonly List<string> BlackListAppTrackerNames = new List<string>()
        {
            "App Installer", //Somehow App Installer shows up as a game sometimes
        };

        // Theme definitions
        private static readonly Dictionary<string, ThemeColors> WidgetThemes = new Dictionary<string, ThemeColors>
        {
            { "Default", new ThemeColors {
                Name = "Default",
                PageBackground = Windows.UI.Color.FromArgb(255, 37, 40, 44),      // #25282C
                CardBackground = Windows.UI.Color.FromArgb(255, 48, 52, 58),      // #30343A
                CardBorder = Windows.UI.Color.FromArgb(255, 80, 85, 92),          // #50555C
                AccentColor = Windows.UI.Color.FromArgb(255, 0, 200, 255),        // #00C8FF
                TextPrimary = Windows.UI.Color.FromArgb(255, 255, 255, 255),      // #FFFFFF
                TextSecondary = Windows.UI.Color.FromArgb(255, 160, 160, 160),    // #A0A0A0
                ButtonBackground = Windows.UI.Color.FromArgb(255, 62, 67, 75),    // #3E434B
                ButtonBorder = Windows.UI.Color.FromArgb(255, 107, 117, 132),     // #6B7584
                TileOff = Windows.UI.Color.FromArgb(255, 26, 28, 30),             // #1A1C1E
                TileOn = Windows.UI.Color.FromArgb(255, 26, 46, 31)               // #1A2E1F
            }},
            { "Dark", new ThemeColors {
                Name = "Dark",
                PageBackground = Windows.UI.Color.FromArgb(255, 26, 26, 26),      // #1A1A1A
                CardBackground = Windows.UI.Color.FromArgb(255, 37, 37, 37),      // #252525
                CardBorder = Windows.UI.Color.FromArgb(255, 58, 58, 58),          // #3A3A3A
                AccentColor = Windows.UI.Color.FromArgb(255, 0, 200, 255),        // #00C8FF
                TextPrimary = Windows.UI.Color.FromArgb(255, 255, 255, 255),      // #FFFFFF
                TextSecondary = Windows.UI.Color.FromArgb(255, 144, 144, 144),    // #909090
                ButtonBackground = Windows.UI.Color.FromArgb(255, 50, 50, 50),    // #323232
                ButtonBorder = Windows.UI.Color.FromArgb(255, 80, 80, 80),        // #505050
                TileOff = Windows.UI.Color.FromArgb(255, 20, 20, 20),             // #141414
                TileOn = Windows.UI.Color.FromArgb(255, 20, 40, 25)               // #142819
            }},
            { "OLED", new ThemeColors {
                Name = "OLED",
                PageBackground = Windows.UI.Color.FromArgb(255, 0, 0, 0),         // #000000
                CardBackground = Windows.UI.Color.FromArgb(255, 10, 10, 10),      // #0A0A0A
                CardBorder = Windows.UI.Color.FromArgb(255, 26, 26, 26),          // #1A1A1A
                AccentColor = Windows.UI.Color.FromArgb(255, 0, 200, 255),        // #00C8FF
                TextPrimary = Windows.UI.Color.FromArgb(255, 255, 255, 255),      // #FFFFFF
                TextSecondary = Windows.UI.Color.FromArgb(255, 128, 128, 128),    // #808080
                ButtonBackground = Windows.UI.Color.FromArgb(255, 20, 20, 20),    // #141414
                ButtonBorder = Windows.UI.Color.FromArgb(255, 40, 40, 40),        // #282828
                TileOff = Windows.UI.Color.FromArgb(255, 5, 5, 5),                // #050505
                TileOn = Windows.UI.Color.FromArgb(255, 10, 30, 15)               // #0A1E0F
            }},
            { "Dracula", new ThemeColors {
                Name = "Dracula",
                PageBackground = Windows.UI.Color.FromArgb(255, 40, 42, 54),      // #282A36
                CardBackground = Windows.UI.Color.FromArgb(255, 68, 71, 90),      // #44475A
                CardBorder = Windows.UI.Color.FromArgb(255, 98, 114, 164),        // #6272A4
                AccentColor = Windows.UI.Color.FromArgb(255, 189, 147, 249),      // #BD93F9
                TextPrimary = Windows.UI.Color.FromArgb(255, 248, 248, 242),      // #F8F8F2
                TextSecondary = Windows.UI.Color.FromArgb(255, 98, 114, 164),     // #6272A4
                ButtonBackground = Windows.UI.Color.FromArgb(255, 68, 71, 90),    // #44475A
                ButtonBorder = Windows.UI.Color.FromArgb(255, 98, 114, 164),      // #6272A4
                TileOff = Windows.UI.Color.FromArgb(255, 33, 34, 44),             // #21222C
                TileOn = Windows.UI.Color.FromArgb(255, 80, 250, 123)             // #50FA7B (green)
            }},
            { "Nord", new ThemeColors {
                Name = "Nord",
                PageBackground = Windows.UI.Color.FromArgb(255, 46, 52, 64),      // #2E3440
                CardBackground = Windows.UI.Color.FromArgb(255, 59, 66, 82),      // #3B4252
                CardBorder = Windows.UI.Color.FromArgb(255, 76, 86, 106),         // #4C566A
                AccentColor = Windows.UI.Color.FromArgb(255, 136, 192, 208),      // #88C0D0
                TextPrimary = Windows.UI.Color.FromArgb(255, 236, 239, 244),      // #ECEFF4
                TextSecondary = Windows.UI.Color.FromArgb(255, 216, 222, 233),    // #D8DEE9
                ButtonBackground = Windows.UI.Color.FromArgb(255, 67, 76, 94),    // #434C5E
                ButtonBorder = Windows.UI.Color.FromArgb(255, 76, 86, 106),       // #4C566A
                TileOff = Windows.UI.Color.FromArgb(255, 36, 40, 50),             // #242832
                TileOn = Windows.UI.Color.FromArgb(255, 163, 190, 140)            // #A3BE8C (green)
            }},
            { "Catppuccin", new ThemeColors {
                Name = "Catppuccin",
                PageBackground = Windows.UI.Color.FromArgb(255, 30, 30, 46),      // #1E1E2E
                CardBackground = Windows.UI.Color.FromArgb(255, 49, 50, 68),      // #313244
                CardBorder = Windows.UI.Color.FromArgb(255, 69, 71, 90),          // #45475A
                AccentColor = Windows.UI.Color.FromArgb(255, 203, 166, 247),      // #CBA6F7
                TextPrimary = Windows.UI.Color.FromArgb(255, 205, 214, 244),      // #CDD6F4
                TextSecondary = Windows.UI.Color.FromArgb(255, 166, 173, 200),    // #A6ADC8
                ButtonBackground = Windows.UI.Color.FromArgb(255, 49, 50, 68),    // #313244
                ButtonBorder = Windows.UI.Color.FromArgb(255, 88, 91, 112),       // #585B70
                TileOff = Windows.UI.Color.FromArgb(255, 24, 24, 37),             // #181825
                TileOn = Windows.UI.Color.FromArgb(255, 166, 227, 161)            // #A6E3A1 (green)
            }}
        };

        private string currentThemeName = "Default";

        // Xbox Game Bar logic
        private XboxGameBarWidget widget = null;
        private XboxGameBarWidgetActivity widgetActivity = null;
        private XboxGameBarWidgetNotificationManager notificationManager = null;
        public XboxGameBarWidgetActivity WidgetActivity { get { return widgetActivity; } }
        private XboxGameBarAppTargetTracker appTargetTracker = null;
        private bool appIsInBackground = false;

        private SolidColorBrush widgetDarkThemeBrush = null;
        private SolidColorBrush widgetLightThemeBrush = null;

        // Compact mode detection (based on window width)
        private bool isCompactMode = false;
        private const double CompactModeWidthThreshold = 400;

        // Widget unloading flag - prevents UI updates during shutdown
        private bool isUnloading = false;

        // Sticky TDP monitoring
        private DispatcherTimer stickyTDPTimer = null;
        private double targetTDPLimit = 15; // Stores the TDP limit we want to maintain
        private int stickyTDPCheckIntervalSeconds = 5;
        private bool isStickyTDPReapplying = false; // Prevents slider flicker during reapply

        // Power source change TDP reapply timer
        private DispatcherTimer powerSourceTdpReapplyTimer = null;

        // Labs section - DAService status timer
        private DispatcherTimer daServiceStatusTimer = null;
        private bool daServiceIsRunning = false;
        private bool labsSectionInitialized = false;

        // Update check
        private string _pendingUpdateZipUrl = null;
        private string _pendingUpdateVersion = null;

        // Helper launch guard - prevents duplicate launches and UAC prompts
        private static bool isLaunchingHelper = false;
        private static DateTime lastLaunchAttempt = DateTime.MinValue;
        private const int MinLaunchIntervalMs = 5000; // 5 seconds between launch attempts
        private const int HeartbeatStaleThresholdSeconds = 5;
        private const int ReconnectionTimeoutSeconds = 5;
        private DispatcherTimer reconnectionTimeoutTimer = null;

        // Post-give-up heartbeat watcher. After TryConnectPipeAsync exhausts its retry
        // budget (typically because a UAC elevation prompt sat unattended during a
        // helper upgrade), this timer polls helper_heartbeat.json every ~3 s and fires
        // a fresh connect attempt when the file mtime or pid changes, so the widget
        // auto-reconnects when the helper finally comes up without the user having to
        // close/reopen it.
        private DispatcherTimer heartbeatWatcherTimer = null;
        private long heartbeatWatcherLastMtimeTicks = 0;
        private int heartbeatWatcherLastPid = 0;
        private volatile bool heartbeatWatcherReconnectInFlight = false;

        // Hotkey watchers for Xbox controller button combos
        private XboxGameBarHotkeyWatcher hotkeyMenuA = null;
        private XboxGameBarHotkeyWatcher hotkeyMenuB = null;
        private XboxGameBarHotkeyWatcher hotkeyMenuX = null;
        private XboxGameBarHotkeyWatcher hotkeyMenuY = null;
        private XboxGameBarHotkeyWatcher hotkeyMenuDpadUp = null;
        private XboxGameBarHotkeyWatcher hotkeyMenuDpadDown = null;
        private XboxGameBarHotkeyWatcher hotkeyMenuDpadLeft = null;
        private XboxGameBarHotkeyWatcher hotkeyMenuDpadRight = null;
        private bool isLoadingHotkeys = false;
        private bool isHotkeysExpanded = false;
        private readonly Dictionary<string, DateTime> hotkeyLastExecuted = new Dictionary<string, DateTime>();
        private const int HotkeyDebounceMs = 300; // Minimum ms between hotkey executions
        private int lastNonZeroOsdLevel = 1; // Track last OSD level for toggle (default to Basic)

        // Properties
        private readonly OSDProperty osd;
        private readonly TDPProperty tdp;
        private readonly CurrentTDPProperty currentTdp;
        private readonly RunningGameProperty runningGame;
        private readonly PerGameProfileProperty perGameProfile;
        private readonly CPUBoostProperty cpuBoost;
        private readonly CPUEPPProperty cpuEPP;
        private readonly MaxCPUStateProperty maxCPUState;
        private readonly MinCPUStateProperty minCPUState;
        // GPU Clock - DISABLED: Not supported by RyzenAdj on this hardware (returns error -1)
        //private readonly LimitGPUClockProperty limitGPUClock;
        //private readonly GPUClockMinProperty gpuClockMin;
        //private readonly GPUClockMaxProperty gpuClockMax;
        private readonly RefreshRatesProperty refreshRates;
        private readonly RefreshRateProperty refreshRate;
        private readonly ResolutionsProperty resolutions;
        private readonly ResolutionProperty resolution;
        private readonly DisplayOrientationProperty displayOrientation;
        private readonly HDRSupportedProperty hdrSupported;
        private readonly HDREnabledProperty hdrEnabled;
        private readonly AdaptiveBrightnessModeProperty adaptiveBrightnessMode;
        private readonly TrackedGameProperty trackedGame;
        private readonly RTSSInstalledProperty rtssInstalled;
        private readonly IsForegroundProperty isForeground;

        // AMD properties
        private readonly AMDRadeonSuperResolutionEnabledProperty amdRadeonSuperResolutionEnabled;
        private readonly AMDRadeonSuperResolutionSupportedProperty amdRadeonSuperResolutionSupported;
        private readonly AMDRadeonSuperResolutionSharpnessProperty amdRadeonSuperResolutionSharpness;
        private readonly AMDFluidMotionFrameEnabledProperty amdFluidMotionFrameEnabled;
        private readonly AMDFluidMotionFrameSupportedProperty amdFluidMotionFrameSupported;
        private readonly AMDFluidMotionFrameComboProperty amdFluidMotionFrameAlgorithm;
        private readonly AMDFluidMotionFrameComboProperty amdFluidMotionFrameSearchMode;
        private readonly AMDFluidMotionFrameComboProperty amdFluidMotionFramePerformanceMode;
        private readonly AMDFluidMotionFrameComboProperty amdFluidMotionFrameFastMotionResponse;
        private readonly AMDFluidMotionFrameV1SupportedProperty amdFluidMotionFrameV1Supported;
        private readonly AMDRadeonAntiLagEnabledProperty amdRadeonAntiLagEnabled;
        private readonly AMDRadeonAntiLagSupportedProperty amdRadeonAntiLagSupported;
        private readonly AMDRadeonBoostEnabledProperty amdRadeonBoostEnabled;
        private readonly AMDRadeonBoostSupportedProperty amdRadeonBoostSupported;
        private readonly AMDRadeonBoostResolutionProperty amdRadeonBoostResolution;
        private readonly AMDRadeonChillEnabledProperty amdRadeonChillEnabled;
        private readonly AMDRadeonChillSupportedProperty amdRadeonChillSupported;
        private readonly AMDRadeonChillMinFPSProperty amdRadeonChillMinFPSProperty;
        private readonly AMDRadeonChillMaxFPSProperty amdRadeonChillMaxFPSProperty;
        private readonly AMDImageSharpeningEnabledProperty amdImageSharpeningEnabled;
        private readonly AMDImageSharpeningSupportedProperty amdImageSharpeningSupported;
        private readonly AMDImageSharpeningSharpnessProperty amdImageSharpeningSharpness;
        private readonly AMDDisplayBrightnessSupportedProperty amdDisplayBrightnessSupported;
        private readonly AMDDisplayBrightnessProperty amdDisplayBrightness;
        private readonly AMDDisplayContrastSupportedProperty amdDisplayContrastSupported;
        private readonly AMDDisplayContrastProperty amdDisplayContrast;
        private readonly AMDDisplaySaturationSupportedProperty amdDisplaySaturationSupported;
        private readonly AMDDisplaySaturationProperty amdDisplaySaturation;
        private readonly AMDDisplayTemperatureSupportedProperty amdDisplayTemperatureSupported;
        private readonly AMDDisplayTemperatureProperty amdDisplayTemperature;

        // Lossless Scaling properties
        private readonly LosslessScalingInstalledProperty losslessScalingInstalled;
        private readonly LosslessScalingRunningProperty losslessScalingRunning;
        private readonly LosslessScalingEnabledProperty losslessScalingEnabled;
        private readonly LosslessScalingCurrentProfileProperty losslessScalingCurrentProfile;
        private readonly LosslessScalingScalingTypeProperty losslessScalingScalingType;
        private readonly LosslessScalingSharpnessProperty losslessScalingSharpness;
        private readonly LosslessScalingFSROptimizeProperty losslessScalingFSROptimize;
        private readonly LosslessScalingAnime4KSizeProperty losslessScalingAnime4KSize;
        private readonly LosslessScalingAnime4KVRSProperty losslessScalingAnime4KVRS;
        private readonly LosslessScalingScaleModeProperty losslessScalingScaleMode;
        private readonly LosslessScalingScaleFactorProperty losslessScalingScaleFactor;
        private readonly LosslessScalingAspectRatioProperty losslessScalingAspectRatio;
        private readonly LosslessScalingFrameGenTypeProperty losslessScalingFrameGenType;
        private readonly LosslessScalingLSFG3ModeProperty losslessScalingLSFG3Mode;
        private readonly LosslessScalingLSFG3MultiplierProperty losslessScalingLSFG3Multiplier;
        private readonly LosslessScalingLSFG3TargetProperty losslessScalingLSFG3Target;
        private readonly LosslessScalingLSFG2ModeProperty losslessScalingLSFG2Mode;
        private readonly LosslessScalingFlowScaleProperty losslessScalingFlowScale;
        private readonly LosslessScalingSizeProperty losslessScalingSize;
        private readonly LosslessScalingAutoScaleProperty losslessScalingAutoScale;
        private readonly LosslessScalingAutoScaleDelayProperty losslessScalingAutoScaleDelay;
        private readonly LosslessScalingSaveAndRestartProperty losslessScalingSaveAndRestart;
        private readonly LosslessScalingCreateProfileProperty losslessScalingCreateProfile;
        private readonly LosslessScalingBringToForegroundProperty losslessScalingBringToForeground;
        private readonly LosslessScalingLaunchProperty losslessScalingLaunch;

        // Additional Settings.xml-backed LS properties (added 2026-05-01)
        private readonly LosslessScalingSyncModeProperty losslessScalingSyncMode;
        private readonly LosslessScalingCaptureApiProperty losslessScalingCaptureApi;
        private readonly LosslessScalingDrawFpsProperty losslessScalingDrawFps;
        private readonly LosslessScalingHdrSupportProperty losslessScalingHdrSupport;
        private readonly LosslessScalingGsyncSupportProperty losslessScalingGsyncSupport;
        private readonly LosslessScalingResizeBeforeScalingProperty losslessScalingResizeBeforeScaling;
        private readonly LosslessScalingLS1TypeProperty losslessScalingLS1Type;
        private readonly LosslessScalingMaxFrameLatencyProperty losslessScalingMaxFrameLatency;
        private readonly LosslessScalingResetProfileProperty losslessScalingResetProfile;

        // Legion Go properties
        private readonly LegionGoDetectedProperty legionGoDetected;
        private readonly LegionTouchpadEnabledProperty legionTouchpadEnabled;
        private readonly LegionLightModeProperty legionLightMode;
        private readonly LegionLightColorProperty legionLightColor;
        private readonly LegionLightBrightnessProperty legionLightBrightness;
        private readonly LegionLightSpeedProperty legionLightSpeed;
        private readonly LegionPerformanceModeProperty legionPerformanceMode;
        // Legion Custom SPL/SPPL/FPPT slider properties removed — TDP is now governed by the
        // Performance-tab master TDP slider + per-profile TDP Boost deltas. Helper still tracks
        // the hardware values internally for SetCustomTDP / WMI roundtrips.
        private readonly LegionFanFullSpeedProperty legionFanFullSpeed;
        private readonly LegionUnlockFanCurveProperty legionUnlockFanCurve;
        private readonly LegionFanCurveGraphProperty legionFanCurveGraph;
        private readonly LegionFanCurvePerModeProperty legionFanCurvePerMode;
        private readonly LegionUnlockFanCurvePerModeProperty legionUnlockFanCurvePerMode;
        private readonly LegionCPUTempProperty legionCPUTemp;
        private readonly LegionFanSensorTempProperty legionFanSensorTemp;
        private readonly LegionCPUFanRPMProperty legionCPUFanRPM;
        private readonly LegionFanCurveVisibleProperty legionFanCurveVisible;
        private readonly LegionGyroEnabledProperty legionGyroEnabled;
        private readonly LegionVibrationProperty legionVibration;
        private readonly LegionPowerLightProperty legionPowerLight;
        private readonly LegionChargeLimitProperty legionChargeLimit;

        // Legion controller remapping properties
        private readonly LegionButtonY1Property legionButtonY1;
        private readonly LegionButtonY2Property legionButtonY2;
        private readonly LegionButtonY3Property legionButtonY3;
        private readonly LegionButtonM1Property legionButtonM1;
        private readonly LegionButtonM2Property legionButtonM2;
        private readonly LegionButtonM3Property legionButtonM3;
        private readonly LegionButtonDesktopProperty legionButtonDesktop;
        private readonly LegionButtonPageProperty legionButtonPage;
        private readonly LegionNintendoLayoutProperty legionNintendoLayout;
        private readonly LegionVibrationModeProperty legionVibrationMode;
        private readonly LegionControllerProfileProperty legionControllerProfile;

        // Gyro properties
        private readonly LegionGyroTargetProperty legionGyroTarget;
        private readonly LegionGyroSensitivityXProperty legionGyroSensitivityX;
        private readonly LegionGyroSensitivityYProperty legionGyroSensitivityY;
        private readonly LegionGyroInvertXProperty legionGyroInvertX;
        private readonly LegionGyroInvertYProperty legionGyroInvertY;
        private readonly LegionGyroMappingTypeProperty legionGyroMappingType;
        private readonly LegionGyroActivationModeProperty legionGyroActivationMode;
        private readonly LegionGyroActivationButtonProperty legionGyroActivationButton;

        // Advanced gyro properties
        private readonly LegionGyroDeadzoneProperty legionGyroDeadzone;

        // Stick deadzone properties
        private readonly LegionLeftStickDeadzoneProperty legionLeftStickDeadzone;
        private readonly LegionRightStickDeadzoneProperty legionRightStickDeadzone;

        // Trigger travel properties
        private readonly LegionLeftTriggerStartProperty legionLeftTriggerStart;
        private readonly LegionLeftTriggerEndProperty legionLeftTriggerEnd;
        private readonly LegionRightTriggerStartProperty legionRightTriggerStart;
        private readonly LegionRightTriggerEndProperty legionRightTriggerEnd;
        private readonly LegionHairTriggersProperty legionHairTriggers;

        // Touchpad vibration property (GLOBAL setting)
        private readonly LegionTouchpadVibrationProperty legionTouchpadVibration;

        // Joystick as mouse properties
        private readonly LegionJoystickAsMouseModeProperty legionJoystickAsMouseMode;
        private readonly LegionJoystickMouseSensProperty legionJoystickMouseSens;

        // Gamepad button mapping property
        private readonly LegionGamepadMappingProperty legionGamepadMapping;
        private Dictionary<string, ButtonMapping> gamepadButtonMappings = new Dictionary<string, ButtonMapping>();

        // Desktop controls preset (synced from helper for hotkey support)
        private readonly LegionDesktopControlsProperty legionDesktopControls;

        // Controller battery properties (from HID input reports)
        private readonly ControllerBatteryLeftProperty controllerBatteryLeft;
        private readonly ControllerBatteryRightProperty controllerBatteryRight;
        private readonly ControllerChargingLeftProperty controllerChargingLeft;
        private readonly ControllerChargingRightProperty controllerChargingRight;
        private readonly ControllerConnectedLeftProperty controllerConnectedLeft;
        private readonly ControllerConnectedRightProperty controllerConnectedRight;
        private readonly ControllerVidPidProperty controllerVidPid;
        private readonly ControllerDeviceStatusProperty controllerDeviceStatus;

        // Device capability properties (for UI visibility based on device features)
        private readonly DeviceDisplayNameProperty deviceDisplayName;
        private readonly DeviceSupportsControllerRemapProperty deviceSupportsControllerRemap;
        private readonly DeviceSupportsRgbLightingProperty deviceSupportsRgbLighting;
        private readonly DeviceSupportsGyroProperty deviceSupportsGyro;
        private readonly DeviceHasScrollWheelProperty deviceHasScrollWheel;
        private readonly DeviceHasDetachableControllersProperty deviceHasDetachableControllers;
        private readonly DeviceHasTouchpadProperty deviceHasTouchpad;

        // GPD properties
        private readonly GPDDetectedProperty gpdDetected;
        private readonly GPDWin5ConnectedProperty gpdWin5Connected;
        private readonly GPDWin5HidDebugProperty gpdWin5HidDebug;
        private readonly GPDWin5HidDevicesProperty gpdWin5HidDevices;
        private readonly GPDDeviceNameProperty gpdDeviceName;
        private readonly GPDSupportsFanControlProperty gpdSupportsFanControl;
        private readonly GPDFanSpeedProperty gpdFanSpeed;
        private readonly GPDFanRPMProperty gpdFanRPM;
        private readonly GPDFanModeProperty gpdFanMode;
        private readonly GPDRestoreDefaultsProperty gpdRestoreDefaults;
        private readonly GPDApplyMappingsProperty gpdApplyMappings;
        private readonly GPDButtonL4Property gpdButtonL4;
        private readonly GPDButtonR4Property gpdButtonR4;
        private readonly GPDButtonProperty gpdButtonA;
        private readonly GPDButtonProperty gpdButtonB;
        private readonly GPDButtonProperty gpdButtonX;
        private readonly GPDButtonProperty gpdButtonY;
        private readonly GPDButtonProperty gpdButtonDPadUp;
        private readonly GPDButtonProperty gpdButtonDPadDown;
        private readonly GPDButtonProperty gpdButtonDPadLeft;
        private readonly GPDButtonProperty gpdButtonDPadRight;
        private readonly GPDButtonProperty gpdButtonL3;
        private readonly GPDButtonProperty gpdButtonR3;
        private readonly GPDButtonProperty gpdButtonLSLeft;
        private readonly GPDButtonProperty gpdButtonLSRight;
        private readonly ControllerEmulationAvailableProperty controllerEmulationAvailable;
        private readonly ControllerEmulationEnabledProperty controllerEmulationEnabled;
        private readonly ControllerEmulationHideStockControllerProperty controllerEmulationHideStockController;
        private readonly ControllerEmulationImprovedInputProperty controllerEmulationImprovedInput;
        private readonly ControllerEmulationHideTargetProperty controllerEmulationHideTarget;
        private readonly ControllerEmulationGyroSourceProperty controllerEmulationGyroSource;
        private readonly ControllerEmulationModeProperty controllerEmulationMode;
        private readonly ControllerEmulationRumbleProfileProperty controllerEmulationRumbleProfile;
        private readonly ControllerEmulationGyroActivationModeProperty controllerEmulationGyroActivationMode;
        private readonly ControllerEmulationGyroActivationButtonProperty controllerEmulationGyroActivationButton;
        private readonly ControllerEmulationDs4OrientationProperty controllerEmulationDs4Orientation;
        private readonly ControllerEmulationPs4TouchpadEnabledProperty controllerEmulationPs4TouchpadEnabled;
        private readonly ControllerEmulationLedForwardingEnabledProperty controllerEmulationLedForwardingEnabled;
        private readonly ControllerEmulationMouseSensitivityProperty controllerEmulationMouseSensitivity;
        private readonly ControllerEmulationMouseThresholdProperty controllerEmulationMouseThreshold;
        private readonly ControllerEmulationMouseAxisProperty controllerEmulationMouseAxis;
        private readonly ControllerEmulationMouseInvertXProperty controllerEmulationMouseInvertX;
        private readonly ControllerEmulationMouseInvertYProperty controllerEmulationMouseInvertY;
        private readonly ControllerEmulationMouseGainXProperty controllerEmulationMouseGainX;
        private readonly ControllerEmulationMouseGainYProperty controllerEmulationMouseGainY;
        private readonly ControllerEmulationStickInvertXProperty controllerEmulationStickInvertX;
        private readonly ControllerEmulationStickInvertYProperty controllerEmulationStickInvertY;
        private readonly ControllerEmulationStickSelectProperty controllerEmulationStickSelect;
        private readonly ControllerEmulationStickOnlyJoystickDataProperty controllerEmulationStickOnlyJoystickData;
        private readonly ControllerEmulationVirtualABXYLayoutProperty controllerEmulationVirtualAbxyLayout;
        private readonly ControllerEmulationStickSensitivityV2Property controllerEmulationStickSensitivityV2;
        private readonly ControllerEmulationStickOrientationV2Property controllerEmulationStickOrientationV2;
        private readonly ControllerEmulationStickConversionProperty controllerEmulationStickConversion;
        private bool isGyroActivationExpanded;
        private bool isFeaturesExpanded;
        private bool isJoystickOutputExpanded;
        private bool controllerEmulationSupported = false;
        private bool isApplyingGpdRestoreDefaults = false;
        private readonly GPDFanCurveGraphProperty gpdFanCurveGraph;
        private readonly GPDCPUTempProperty gpdCPUTemp;
        private readonly GPDFanCurveVisibleProperty gpdFanCurveVisible;
        private readonly GPDFanCurveEnabledProperty gpdFanCurveEnabled;

        // Default Game Profile properties (Microsoft Gaming Services profiles)
        private readonly DefaultGameProfileAvailableProperty defaultGameProfileAvailable;
        private readonly DefaultGameProfileDataProperty defaultGameProfileData;
        private readonly DefaultGameProfileEnabledProperty defaultGameProfileEnabled;

        // Settings properties
        private readonly TdpMethodProperty tdpMethod;
        private readonly EmulationBackendProperty emulationBackend;
        private readonly UsbipInstalledProperty usbipInstalled;
        private readonly InstallUsbipProperty installUsbip;
        private readonly ViiperStringComboProperty viiperDeviceType;
        private readonly ViiperStringComboProperty viiperInputSource;
        private readonly ViiperStringComboProperty viiperGyroSource;
        private readonly ViiperStringComboProperty viiperSteamSubDevice;
        private readonly ViiperStringComboProperty viiperSonySubDevice;
        private readonly ViiperStringComboProperty viiperNintendoSubDevice;
        private readonly ViiperStringComboProperty viiperGuideButtonMode;
        private readonly ViiperSwapRumbleMotorsProperty viiperSwapRumbleMotors;
        private readonly ViiperMirrorLightbarToStickProperty viiperMirrorLightbarToStick;
        private readonly ViiperStickGyroEnabledProperty viiperStickGyroEnabled;
        private readonly ViiperJoyconGyroPerHalfProperty viiperJoyconGyroPerHalf;
        private readonly ViiperAlternateGyroConventionProperty viiperAlternateGyroConvention;
        private readonly ViiperRumbleIntensityProperty viiperRumbleIntensity;
        private readonly ViiperStringComboProperty viiperGyroAxisMapX;
        private readonly ViiperStringComboProperty viiperGyroAxisMapY;
        private readonly ViiperStringComboProperty viiperGyroAxisMapZ;
        private readonly ViiperStickTriggerConfigProperty viiperStickTriggerConfig;
        private readonly ViiperStickTriggerPreviewEnabledProperty viiperStickTriggerPreviewEnabled;
        private readonly ViiperStickTriggerLiveSampleProperty viiperStickTriggerLiveSample;
        private readonly WinRing0AvailableProperty winRing0Available;
        private readonly PawnIOAvailableProperty pawnIOAvailable;
        private readonly PawnIOInstalledProperty pawnIOInstalled;
        private readonly InstallPawnIOProperty installPawnIO;
        private readonly SetupWarningsProperty setupWarnings;
        private readonly HidHideInstalledProperty hidHideInstalled;
        private readonly InstallHidHideProperty installHidHide;
        private readonly AutoHibernateEnabledProperty autoHibernateEnabled;
        private readonly AutoHibernateIdleMinutesProperty autoHibernateIdleMinutes;

        // AutoTDP properties
        private readonly AutoTDPEnabledProperty autoTDPEnabled;
        private readonly AutoTDPTargetFPSProperty autoTDPTargetFPS;
        private readonly AutoTDPCurrentFPSProperty autoTDPCurrentFPS;
        private readonly AutoTDPMinTDPProperty autoTDPMinTDP;
        private readonly AutoTDPMaxTDPProperty autoTDPMaxTDP;
        private readonly AutoTDPUseMLModeProperty autoTDPUseMLMode;  // DEPRECATED: use autoTDPControllerType
        private readonly AutoTDPControllerTypeProperty autoTDPControllerType;  // 0=PID, 1=Q-Learning, 2=SARSA
        private readonly AutoTDPMLStatusProperty autoTDPMLStatus;
        private readonly AutoTDPLearnedGameDataProperty autoTDPLearnedGameData;
        private readonly AutoTDPResetMLProperty autoTDPResetML;
        private readonly AutoTDPPauseWhenUnfocusedProperty autoTDPPauseWhenUnfocused;
        private readonly TDPLimitsProperty tdpLimits;

        // AutoTDP slider debounce timers (delay sending to helper until user stops sliding)
        private DispatcherTimer autoTDPTargetFPSDebounceTimer;
        private DispatcherTimer autoTDPMinDebounceTimer;
        private DispatcherTimer autoTDPMaxDebounceTimer;
        private int pendingAutoTDPTargetFPS;
        private int pendingAutoTDPMinTDP;
        private int pendingAutoTDPMaxTDP;

        private readonly CPUCoreConfigProperty cpuCoreConfig;
        private readonly CPUCoreActiveConfigProperty cpuCoreActiveConfig;
        private readonly CoreParkingPercentProperty coreParkingPercent;
        private readonly ForceParkModeProperty forceParkMode;
        private readonly ForceDefaultGameProfileProperty forceDefaultGameProfile;

        // TDP Boost properties
        private readonly TDPBoostEnabledProperty tdpBoostEnabled;
        private readonly TDPBoostSPPTProperty tdpBoostSPPT;
        private readonly TDPBoostFPPTProperty tdpBoostFPPT;
        private bool isTDPBoostExpanded = false;
        private bool isLoadingTDPBoostSettings = false;
        private bool isLoadingStickyTDPSettings = false;
        private bool isUpdatingTDPMode = false; // Prevents saving toggle states during mode changes

        // OS Power Mode
        private readonly OSPowerModeProperty osPowerMode;
        private bool isLoadingOSPowerMode = false;

        // Profile Detection Settings
        private readonly ProfileMatchByExeProperty profileMatchByExe;
        private readonly ProfileGamesOnlyProperty profileGamesOnly;
        // DISABLED: Custom games, blacklist, and current apps features - caused user confusion
        // private readonly ProfileCustomGamePathProperty profileCustomGamePath;
        // private readonly ProfileBlacklistPathsProperty profileBlacklistPaths;
        // private readonly ForegroundAppProperty foregroundApp;

        // FPS Limit (RTSS)
        private readonly FPSLimitProperty fpsLimit;
        private DispatcherTimer fpsLimitDebounceTimer;
        private int fpsLimitPendingValue;
        private const int FPS_LIMIT_DEBOUNCE_MS = 300;

        // Profile management
        private PerformanceProfile globalProfile = new PerformanceProfile();
        private PerformanceProfile acProfile = new PerformanceProfile();
        private PerformanceProfile dcProfile = new PerformanceProfile();
        private PerformanceProfile gameProfile = new PerformanceProfile();
        private PerformanceProfile gameACProfile = new PerformanceProfile();
        private PerformanceProfile gameDCProfile = new PerformanceProfile();
        private string currentProfileName = ""; // Empty so first SwitchProfile() loads settings
        private string currentGameName = "";
        private string currentGameExePath = "";
        private string currentGameIconPath = ""; // Cache icon path to preserve it across foreground changes
        private bool isLoadingProfile = false;
        private bool isSwitchingProfile = false;
        private bool isApplyingHelperUpdate = false; // Prevents saves when helper echoes values back
        private bool isInitialSync = true; // Prevents saves during initial app startup sync
        private bool isCleanInstall = false; // True if no saved Global profile existed on startup
        private bool isInternalToggleDisable = false; // Indicates toggle is being disabled internally (game close)
        private bool isUserInitiatedProfileToggle = false; // Indicates user clicked Profile tile to toggle
        private bool isUserInitiatedTDPModeChange = false; // Indicates user clicked TDP Mode tile in Quick Tab
        private int savedLegionPerformanceMode = -1; // Stores Legion mode before per-game profile (-1 = not saved)
        private bool isUpdatingPowerSourceProfileToggle = false; // Prevents programmatic toggle sync from triggering profile writes
        private const string GlobalPowerSourceProfileSettingKey = "PowerSourceProfileEnabled";
        private const string PerGamePowerSourceProfileSettingPrefix = "PerGamePowerSourceProfileEnabled_";

        // Controller profile state
        private ControllerProfile globalControllerProfile = new ControllerProfile();
        private ControllerProfile gameControllerProfile = new ControllerProfile();
        private bool isLoadingControllerProfile = false;
        private bool isSwitchingControllerProfile = false;
        private DateTime lastProfileApplyTime = DateTime.MinValue; // Prevents duplicate sends from queued UI events
        private int profileSwitchEpoch = 0; // Incremented on each LoadProfileSettings; used to skip stale deferred callbacks
        private string lastSentGamepadMappingsJson = null; // Tracks last sent mappings to avoid duplicates

        // Helper to check if we have a valid game (not null, not empty, not "No game detected")
        private bool HasValidGame(string gameName)
        {
            if (string.IsNullOrWhiteSpace(gameName))
                return false;

            string normalized = gameName.Trim();

            // Case-insensitive check for "No game detected" (handles any capitalization)
            return !normalized.Equals("No game detected", StringComparison.OrdinalIgnoreCase);
        }

        // Sanitize game name for consistent storage
        private string SanitizeGameName(string gameName)
        {
            if (string.IsNullOrWhiteSpace(gameName))
                return "";

            // Trim whitespace, normalize spaces
            return gameName.Trim();
        }

        private string GetPerGamePowerSourceProfileSettingKey(string gameName)
        {
            return $"{PerGamePowerSourceProfileSettingPrefix}{gameName}";
        }

        private bool HasGameSingleProfile(string gameName)
        {
            if (!HasValidGame(gameName))
                return false;

            var settings = ApplicationData.Current.LocalSettings;
            return settings.Containers.ContainsKey($"Profile_Game_{gameName}");
        }

        private bool HasGamePowerSplitProfiles(string gameName)
        {
            if (!HasValidGame(gameName))
                return false;

            var settings = ApplicationData.Current.LocalSettings;
            return settings.Containers.ContainsKey($"Profile_Game_{gameName}_AC")
                || settings.Containers.ContainsKey($"Profile_Game_{gameName}_DC");
        }

        private bool HasAnyGameProfile(string gameName)
        {
            return HasGameSingleProfile(gameName) || HasGamePowerSplitProfiles(gameName);
        }

        private bool GetGlobalPowerSourceProfileEnabled()
        {
            var settings = ApplicationData.Current.LocalSettings;
            if (settings.Values.TryGetValue(GlobalPowerSourceProfileSettingKey, out object val) && val is bool enabled)
            {
                return enabled;
            }

            return false;
        }

        private bool GetPerGamePowerSourceProfileEnabled(string gameName)
        {
            if (!HasValidGame(gameName))
            {
                return GetGlobalPowerSourceProfileEnabled();
            }

            var settings = ApplicationData.Current.LocalSettings;
            string settingKey = GetPerGamePowerSourceProfileSettingKey(gameName);
            if (settings.Values.TryGetValue(settingKey, out object val) && val is bool enabled)
            {
                return enabled;
            }

            bool hasSplitProfiles = HasGamePowerSplitProfiles(gameName);
            bool hasSingleProfile = HasGameSingleProfile(gameName);

            // Prefer single-profile mode when both profile shapes exist and no explicit
            // per-game split setting has been saved yet.
            if (hasSingleProfile)
            {
                return false;
            }

            if (hasSplitProfiles)
            {
                return true;
            }

            return GetGlobalPowerSourceProfileEnabled();
        }

        private void SavePerGamePowerSourceProfileSetting(string gameName, bool enabled)
        {
            if (!HasValidGame(gameName))
            {
                return;
            }

            var settings = ApplicationData.Current.LocalSettings;
            settings.Values[GetPerGamePowerSourceProfileSettingKey(gameName)] = enabled;
        }

        private bool GetPowerSourceProfileEnabledForCurrentContext()
        {
            bool perGameEnabled = PerGameProfileToggle?.IsOn == true;
            if (perGameEnabled && HasValidGame(currentGameName))
            {
                return GetPerGamePowerSourceProfileEnabled(currentGameName);
            }

            return GetGlobalPowerSourceProfileEnabled();
        }

        private void UpdateGlobalProfileDisplayMode()
        {
            bool globalPowerSourceSplit = GetGlobalPowerSourceProfileEnabled();

            if (GlobalProfileSimple != null)
            {
                GlobalProfileSimple.Visibility = globalPowerSourceSplit ? Visibility.Collapsed : Visibility.Visible;
            }

            if (GlobalProfileACDC != null)
            {
                GlobalProfileACDC.Visibility = globalPowerSourceSplit ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void UpdatePowerSourceProfileScopeText()
        {
            if (PowerSourceProfileScopeText == null)
            {
                return;
            }

            bool hasGame = HasValidGame(currentGameName);
            bool perGameContext = PerGameProfileToggle?.IsOn == true && hasGame;

            if (perGameContext)
            {
                bool splitEnabled = GetPerGamePowerSourceProfileEnabled(currentGameName);
                PowerSourceProfileScopeText.Text = $"Scope: {currentGameName} (per-game). AC/DC split: {(splitEnabled ? "On" : "Off")} (auto-saved).";
            }
            else
            {
                bool splitEnabled = GetGlobalPowerSourceProfileEnabled();
                PowerSourceProfileScopeText.Text = $"Scope: Global profiles. AC/DC split: {(splitEnabled ? "On" : "Off")}. Enable Per-Game Profile to set this per game.";
            }
        }

        private void SyncPowerSourceProfileToggleForCurrentContext()
        {
            if (PowerSourceProfileToggle == null)
            {
                return;
            }

            bool targetValue = GetPowerSourceProfileEnabledForCurrentContext();
            if (PowerSourceProfileToggle.IsOn != targetValue)
            {
                isUpdatingPowerSourceProfileToggle = true;
                try
                {
                    PowerSourceProfileToggle.IsOn = targetValue;
                }
                finally
                {
                    isUpdatingPowerSourceProfileToggle = false;
                }
            }

            UpdateGlobalProfileDisplayMode();
            UpdatePowerSourceProfileScopeText();
        }

        // Profile save settings - backed by fields to avoid UI thread access issues
        // These are updated in LoadProfileCustomizationSettings and ProfileSettingCheckBox_Changed
        private bool _saveTDP = true;
        private bool _saveCPUBoost = true;
        private bool _saveCPUEPP = true;
        private bool _saveCPUState = true;
        private bool _saveAMDFeatures = false;
        private bool _saveFPSLimit = true;
        private bool _saveAutoTDP = true;
        private bool _saveOSPowerMode = true;
        private bool _saveHDR = false;
        private bool _saveResolution = false;
        private bool _saveRefreshRate = false;
        private bool _saveStickyTDP = false;
        private bool _saveOverlayLevel = false;
        private bool _saveCPUAffinity = false;
        // Legion device-wide settings default to false so they behave as global device
        // settings rather than per-game; the helper routes writes to GlobalProfile when these
        // are false and still applies from the game profile only when the game stored a value.
        private bool _saveNintendoLayout = false;
        private bool _saveVibration = false;
        private bool _saveLighting = false;
        private bool _saveButtonMappings = false;

        private bool SaveTDP => _saveTDP;
        private bool SaveCPUBoost => _saveCPUBoost;
        private bool SaveCPUEPP => _saveCPUEPP;
        private bool SaveCPUState => _saveCPUState;
        private bool SaveAMDFeatures => _saveAMDFeatures;
        private bool SaveFPSLimit => _saveFPSLimit;
        private bool SaveAutoTDP => _saveAutoTDP;
        private bool SaveOSPowerMode => _saveOSPowerMode;
        private bool SaveHDR => _saveHDR;
        private bool SaveResolution => _saveResolution;
        private bool SaveRefreshRate => _saveRefreshRate;
        private bool SaveStickyTDP => _saveStickyTDP;
        private bool SaveOverlayLevel => _saveOverlayLevel;
        private bool SaveCPUAffinity => _saveCPUAffinity;
        private bool SaveNintendoLayout => _saveNintendoLayout;
        private bool SaveVibration => _saveVibration;
        private bool SaveLighting => _saveLighting;
        private bool SaveButtonMappings => _saveButtonMappings;

        private bool isLoadingProfileSettings = false;

        private string RadeonChillOnText
        {
            get
            {
                try
                {
                    // Safety check: ensure both properties are initialized before accessing values
                    if (amdRadeonChillMinFPSProperty == null || amdRadeonChillMaxFPSProperty == null)
                        return "Enabled";

                    return string.Format("Idle FPS: {0} - Max FPS: {1}",
                        amdRadeonChillMinFPSProperty.Value,
                        amdRadeonChillMaxFPSProperty.Value);
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error in RadeonChillOnText getter: {ex.Message}");
                    return "Enabled";
                }
            }
        }

        private readonly WidgetProperties properties;

        public event PropertyChangedEventHandler PropertyChanged;

        public GamingWidget()
        {
            var constructorTimer = Stopwatch.StartNew();
            Logger.Info($"=== GamingWidget constructor START === Instance hash: {this.GetHashCode()}");

            // Prevent OSD checkbox events from saving during XAML initialization
            isLoadingOSDConfig = true;
            // Prevent profile settings checkbox events from saving during XAML initialization
            isLoadingProfileSettings = true;
            // Prevent TDP limits slider events from saving during XAML initialization
            isLoadingTDPLimits = true;
            // Prevent controller profile events from saving during XAML initialization
            // This MUST be set before InitializeComponent() to prevent button ComboBox events
            // from overwriting stored button mappings with defaults
            isLoadingControllerProfile = true;

            var xamlTimer = Stopwatch.StartNew();
            InitializeComponent();
            xamlTimer.Stop();
            Logger.Info($"[TIMING] InitializeComponent: {xamlTimer.ElapsedMilliseconds}ms");

            // Restore the GoTweaks self-update + Lenovo driver-update checkboxes from
            // LocalSettings now, independent of any helper push. The push-driven sync
            // paths only fire when the helper actually returns a result (update available,
            // driver list parsed) — without this call, the XAML defaults stay visible on
            // every cold start where no push arrives, making toggles look unsaved.
            SyncUpdatePreferenceCheckboxesFromLocalSettings();

            // Register for lifecycle events
            this.Loaded += GamingWidget_Loaded;
            this.Unloaded += GamingWidget_Unloaded;
            Logger.Info("Registered Loaded and Unloaded event handlers.");

            // Register for LT/RT tab navigation (PreviewKeyDown to intercept before scrolling).
            // PreviewKeyUp is used to clear the press-edge state so the next press advances
            // exactly one tab — without it, holding a trigger would cycle tabs continuously.
            this.PreviewKeyDown += GamingWidget_PreviewKeyDown;
            this.PreviewKeyUp += GamingWidget_PreviewKeyUp;
            // Clear any latched LT/RT "held" state whenever the widget regains focus.
            // HidHide CyclePort during emulation setup can hide the physical pad while
            // a trigger was physically pressed; the KeyUp never arrives so the widget
            // thinks the trigger is still down. Refreshing on focus is a clean escape.
            this.GotFocus += (s, args) => ResetTriggerTabNavState();

            var propertiesTimer = Stopwatch.StartNew();
            tdp = new TDPProperty(4, TDPSlider, this);
            currentTdp = new CurrentTDPProperty(CurrentTDPValueText, this);
            osd = new OSDProperty(0, PerformanceOverlaySlider, this);
            runningGame = new RunningGameProperty(RunningGameText, PerGameProfileToggle, DetectedGameText, this);
            // Callback fires after RunningGameProperty updates DetectedGameText.Text on the
            // UI thread. Re-evaluate the marquee scroll so long window titles (e.g. Windows
            // Terminal showing the current task) scroll instead of truncating. Also keeps
            // the existing XY-navigation refresh for game-detection state changes.
            runningGame.SetGameDetectionCallback(() =>
            {
                UpdatePerformanceTabXYNavigation();
                UpdateDetectedGameScrollAnimation();
            });
            perGameProfile = new PerGameProfileProperty(PerGameProfileToggle, this);
            cpuBoost = new CPUBoostProperty(CPUBoostToggle, this);
            cpuEPP = new CPUEPPProperty(80, CPUEPPSlider, this);
            maxCPUState = new MaxCPUStateProperty();
            minCPUState = new MinCPUStateProperty();
            // GPU Clock - DISABLED: Not supported by RyzenAdj on this hardware (returns error -1)
            //limitGPUClock = new LimitGPUClockProperty(LimitGPUClockToggle, this);
            //gpuClockMin = new GPUClockMinProperty(GPUClockMinSlider, this);
            //gpuClockMax = new GPUClockMaxProperty(GPUClockMaxSlider, this);
            refreshRates = new RefreshRatesProperty(RefreshRatesComboBox, this);
            refreshRate = new RefreshRateProperty(RefreshRatesComboBox, this);
            refreshRates.SetRefreshRateProperty(refreshRate); // Wire up for selection restoration on dock/undock
            resolutions = new ResolutionsProperty(ResolutionComboBox, this);
            resolution = new ResolutionProperty(ResolutionComboBox, this);
            resolutions.SetResolutionProperty(resolution); // Wire up for selection restoration on dock/undock
            displayOrientation = new DisplayOrientationProperty();
            hdrSupported = new HDRSupportedProperty(HDRToggle, this);
            hdrEnabled = new HDREnabledProperty(HDRToggle, this);
            adaptiveBrightnessMode = new AdaptiveBrightnessModeProperty(AdaptiveBrightnessModeComboBox, this);
            trackedGame = new TrackedGameProperty(new TrackedGame());
            rtssInstalled = new RTSSInstalledProperty(PerformanceOverlaySlider, this);
            rtssInstalled.SetAdditionalCallback(UpdateFPSLimitControls);
            isForeground = new IsForegroundProperty();
            amdRadeonSuperResolutionEnabled = new AMDRadeonSuperResolutionEnabledProperty(AMDRadeonSuperResolutionToggle, this);
            amdRadeonSuperResolutionSupported = new AMDRadeonSuperResolutionSupportedProperty(AMDRadeonSuperResolutionToggle, this);
            amdRadeonSuperResolutionSharpness = new AMDRadeonSuperResolutionSharpnessProperty(AMDRadeonSuperResolutionSharpnessSlider, this);
            amdFluidMotionFrameEnabled = new AMDFluidMotionFrameEnabledProperty(AMDFluidMotionFrameToggle, this);
            amdFluidMotionFrameSupported = new AMDFluidMotionFrameSupportedProperty(AMDFluidMotionFrameToggle, this);
            amdFluidMotionFrameAlgorithm = new AMDFluidMotionFrameComboProperty(Function.AMDFluidMotionFrameAlgorithm, AMDFluidMotionFrameAlgorithmComboBox, this);
            amdFluidMotionFrameSearchMode = new AMDFluidMotionFrameComboProperty(Function.AMDFluidMotionFrameSearchMode, AMDFluidMotionFrameSearchModeComboBox, this);
            amdFluidMotionFramePerformanceMode = new AMDFluidMotionFrameComboProperty(Function.AMDFluidMotionFramePerformanceMode, AMDFluidMotionFramePerformanceModeComboBox, this);
            amdFluidMotionFrameFastMotionResponse = new AMDFluidMotionFrameComboProperty(Function.AMDFluidMotionFrameFastMotionResponse, AMDFluidMotionFrameFastMotionResponseComboBox, this);
            amdFluidMotionFrameV1Supported = new AMDFluidMotionFrameV1SupportedProperty(this,
                AMDFluidMotionFrameAlgorithmComboBox,
                AMDFluidMotionFrameSearchModeComboBox,
                AMDFluidMotionFramePerformanceModeComboBox,
                AMDFluidMotionFrameFastMotionResponseComboBox);
            amdRadeonAntiLagEnabled = new AMDRadeonAntiLagEnabledProperty(AMDRadeonAntiLagToggle, this);
            amdRadeonAntiLagSupported = new AMDRadeonAntiLagSupportedProperty(AMDRadeonAntiLagToggle, this);
            amdRadeonBoostEnabled = new AMDRadeonBoostEnabledProperty(AMDRadeonBoostToggle, this);
            amdRadeonBoostSupported = new AMDRadeonBoostSupportedProperty(AMDRadeonBoostToggle, this);
            amdRadeonBoostResolution = new AMDRadeonBoostResolutionProperty(AMDRadeonBoostResolutionSlider, this);
            amdRadeonChillEnabled = new AMDRadeonChillEnabledProperty(AMDRadeonChillToggle, this);
            amdRadeonChillSupported = new AMDRadeonChillSupportedProperty(AMDRadeonChillToggle, this);
            amdRadeonChillMinFPSProperty = new AMDRadeonChillMinFPSProperty(AMDRadeonChillMinFPSSlider, this);
            amdRadeonChillMaxFPSProperty = new AMDRadeonChillMaxFPSProperty(AMDRadeonChillMaxFPSSlider, this);
            amdImageSharpeningEnabled = new AMDImageSharpeningEnabledProperty(AMDImageSharpeningToggle, this);
            amdImageSharpeningSupported = new AMDImageSharpeningSupportedProperty(AMDImageSharpeningToggle, this);
            amdImageSharpeningSharpness = new AMDImageSharpeningSharpnessProperty(AMDImageSharpeningSlider, this);
            amdDisplayBrightnessSupported = new AMDDisplayBrightnessSupportedProperty(AMDDisplayBrightnessSlider, this);
            amdDisplayBrightness = new AMDDisplayBrightnessProperty(AMDDisplayBrightnessSlider, this);
            amdDisplayContrastSupported = new AMDDisplayContrastSupportedProperty(AMDDisplayContrastSlider, this);
            amdDisplayContrast = new AMDDisplayContrastProperty(AMDDisplayContrastSlider, this);
            amdDisplaySaturationSupported = new AMDDisplaySaturationSupportedProperty(AMDDisplaySaturationSlider, this);
            amdDisplaySaturation = new AMDDisplaySaturationProperty(AMDDisplaySaturationSlider, this);
            amdDisplayTemperatureSupported = new AMDDisplayTemperatureSupportedProperty(AMDDisplayTemperatureSlider, this);
            amdDisplayTemperature = new AMDDisplayTemperatureProperty(AMDDisplayTemperatureSlider, this);

            // Lossless Scaling properties
            losslessScalingInstalled = new LosslessScalingInstalledProperty(this);
            losslessScalingRunning = new LosslessScalingRunningProperty();
            losslessScalingEnabled = new LosslessScalingEnabledProperty(LosslessScalingEnabledToggle, this);
            losslessScalingCurrentProfile = new LosslessScalingCurrentProfileProperty();
            losslessScalingScalingType = new LosslessScalingScalingTypeProperty(LosslessScalingScalingTypeComboBox, this);
            losslessScalingSharpness = new LosslessScalingSharpnessProperty(50, LosslessScalingSharpnessSlider, this);
            losslessScalingFSROptimize = new LosslessScalingFSROptimizeProperty(LosslessScalingFSROptimizeToggle, this);
            losslessScalingAnime4KSize = new LosslessScalingAnime4KSizeProperty(LosslessScalingAnime4KSizeComboBox, this);
            losslessScalingAnime4KVRS = new LosslessScalingAnime4KVRSProperty(LosslessScalingAnime4KVRSToggle, this);
            losslessScalingScaleMode = new LosslessScalingScaleModeProperty(LosslessScalingScaleModeComboBox, this);
            losslessScalingScaleFactor = new LosslessScalingScaleFactorProperty(2, LosslessScalingScaleFactorSlider, this);
            losslessScalingAspectRatio = new LosslessScalingAspectRatioProperty(LosslessScalingAspectRatioComboBox, this);
            losslessScalingFrameGenType = new LosslessScalingFrameGenTypeProperty(LosslessScalingFrameGenTypeComboBox, this);
            losslessScalingLSFG3Mode = new LosslessScalingLSFG3ModeProperty(LosslessScalingLSFG3ModeComboBox, this);
            losslessScalingLSFG3Multiplier = new LosslessScalingLSFG3MultiplierProperty(LosslessScalingLSFG3MultiplierComboBox, this);
            losslessScalingLSFG3Target = new LosslessScalingLSFG3TargetProperty(120, LosslessScalingLSFG3TargetSlider, this);
            losslessScalingLSFG2Mode = new LosslessScalingLSFG2ModeProperty(LosslessScalingLSFG2ModeComboBox, this);
            losslessScalingFlowScale = new LosslessScalingFlowScaleProperty(50, LosslessScalingFlowScaleSlider, this);
            losslessScalingSize = new LosslessScalingSizeProperty(LosslessScalingSizeToggle, this);
            losslessScalingAutoScale = new LosslessScalingAutoScaleProperty(LosslessScalingAutoScaleToggle, this);
            losslessScalingAutoScaleDelay = new LosslessScalingAutoScaleDelayProperty(0, LosslessScalingAutoScaleDelaySlider, this);
            losslessScalingSaveAndRestart = new LosslessScalingSaveAndRestartProperty();
            losslessScalingCreateProfile = new LosslessScalingCreateProfileProperty();
            losslessScalingBringToForeground = new LosslessScalingBringToForegroundProperty();
            losslessScalingLaunch = new LosslessScalingLaunchProperty();

            // Additional Settings.xml-backed LS properties (added 2026-05-01)
            losslessScalingSyncMode = new LosslessScalingSyncModeProperty(LosslessScalingSyncModeComboBox, this);
            losslessScalingCaptureApi = new LosslessScalingCaptureApiProperty(LosslessScalingCaptureApiComboBox, this);
            losslessScalingDrawFps = new LosslessScalingDrawFpsProperty(LosslessScalingDrawFpsToggle, this);
            losslessScalingHdrSupport = new LosslessScalingHdrSupportProperty(LosslessScalingHdrSupportToggle, this);
            losslessScalingGsyncSupport = new LosslessScalingGsyncSupportProperty(LosslessScalingGsyncSupportToggle, this);
            losslessScalingResizeBeforeScaling = new LosslessScalingResizeBeforeScalingProperty(LosslessScalingResizeBeforeToggle, this);
            losslessScalingLS1Type = new LosslessScalingLS1TypeProperty(LosslessScalingLS1TypeComboBox, this);
            losslessScalingMaxFrameLatency = new LosslessScalingMaxFrameLatencyProperty(1, LosslessScalingMaxFrameLatencySlider, this);
            losslessScalingResetProfile = new LosslessScalingResetProfileProperty();

            // Legion Go properties
            legionGoDetected = new LegionGoDetectedProperty(this);
            legionTouchpadEnabled = new LegionTouchpadEnabledProperty(LegionTouchpadToggle, this);
            legionLightMode = new LegionLightModeProperty(LegionLightModeComboBox, this);
            legionLightColor = new LegionLightColorProperty(LegionColorPicker, this);
            legionLightBrightness = new LegionLightBrightnessProperty(LegionBrightnessSlider, this);
            legionLightSpeed = new LegionLightSpeedProperty(LegionSpeedSlider, this);
            legionPerformanceMode = new LegionPerformanceModeProperty(LegionPerformanceModeComboBox, this);
            legionFanFullSpeed = new LegionFanFullSpeedProperty(LegionFanFullSpeedToggle, this);
            legionUnlockFanCurve = new LegionUnlockFanCurveProperty(LegionUnlockFanCurveToggle, this);

            // Fan curve graph properties
            legionFanCurveGraph = new LegionFanCurveGraphProperty(this);
            legionFanCurveGraph.SetGraphUpdateCallback(OnFanCurveUpdated);
            // Per-mode channel: helper pushes one message per TdpMode (1=Quiet, 2=Balanced,
            // 3=Performance, 255=Custom) on connect so the widget can let the user edit any
            // mode's curve without changing power mode. Outbound edits also go through this
            // channel keyed by the dropdown-selected mode (not the active mode).
            legionFanCurvePerMode = new LegionFanCurvePerModeProperty(this);
            legionFanCurvePerMode.SetCallback(OnFanCurvePerModeReceived);
            legionUnlockFanCurvePerMode = new LegionUnlockFanCurvePerModeProperty(this);
            legionUnlockFanCurvePerMode.SetCallback(OnUnlockFanCurvePerModeReceived);
            legionCPUTemp = new LegionCPUTempProperty(this);
            legionCPUTemp.SetTempUpdateCallback(OnCPUTempUpdated);
            legionFanSensorTemp = new LegionFanSensorTempProperty(this);
            legionFanSensorTemp.SetTempUpdateCallback(OnFanSensorTempUpdated);
            legionCPUFanRPM = new LegionCPUFanRPMProperty(this);
            legionCPUFanRPM.SetRPMUpdateCallback(OnFanRPMUpdated);
            legionFanCurveVisible = new LegionFanCurveVisibleProperty();

            legionGyroEnabled = new LegionGyroEnabledProperty(null, this); // Gyro removed from UI, kept for backwards compatibility
            legionVibration = new LegionVibrationProperty(LegionVibrationComboBox, this);
            legionPowerLight = new LegionPowerLightProperty(LegionPowerLightToggle, this);
            legionChargeLimit = new LegionChargeLimitProperty(LegionChargeLimitToggle, this);

            // Controller remapping properties (not auto-bound, use SendMapping())
            legionButtonY1 = new LegionButtonY1Property(LegionButtonY1ComboBox, this);
            legionButtonY2 = new LegionButtonY2Property(LegionButtonY2ComboBox, this);
            legionButtonY3 = new LegionButtonY3Property(LegionButtonY3ComboBox, this);
            legionButtonM1 = new LegionButtonM1Property(LegionButtonM1ComboBox, this);
            legionButtonM2 = new LegionButtonM2Property(LegionButtonM2ComboBox, this);
            legionButtonM3 = new LegionButtonM3Property(LegionButtonM3ComboBox, this);
            legionButtonDesktop = new LegionButtonDesktopProperty(LegionButtonDesktopComboBox, this);
            legionButtonPage = new LegionButtonPageProperty(LegionButtonPageComboBox, this);
            legionNintendoLayout = new LegionNintendoLayoutProperty(LegionNintendoLayoutToggle, this);
            legionVibrationMode = new LegionVibrationModeProperty(LegionVibrationModeComboBox, this);
            legionControllerProfile = new LegionControllerProfileProperty(LegionControllerProfileToggle, this);

            // Gyro properties
            legionGyroTarget = new LegionGyroTargetProperty(LegionGyroTargetComboBox, this);
            legionGyroSensitivityX = new LegionGyroSensitivityXProperty(LegionGyroSensitivityXSlider, this);
            legionGyroSensitivityY = new LegionGyroSensitivityYProperty(LegionGyroSensitivityYSlider, this);
            legionGyroInvertX = new LegionGyroInvertXProperty(LegionGyroInvertXToggle, this);
            legionGyroInvertY = new LegionGyroInvertYProperty(LegionGyroInvertYToggle, this);
            legionGyroMappingType = new LegionGyroMappingTypeProperty(LegionGyroMappingTypeComboBox, this);
            legionGyroActivationMode = new LegionGyroActivationModeProperty(LegionGyroActivationModeComboBox, this);
            legionGyroActivationButton = new LegionGyroActivationButtonProperty(LegionGyroActivationButtonComboBox, this);

            // Advanced gyro properties
            legionGyroDeadzone = new LegionGyroDeadzoneProperty(LegionGyroDeadzoneSlider, this);

            // Stick deadzone properties
            legionLeftStickDeadzone = new LegionLeftStickDeadzoneProperty(LegionLeftStickDeadzoneSlider, this);
            legionRightStickDeadzone = new LegionRightStickDeadzoneProperty(LegionRightStickDeadzoneSlider, this);

            // Trigger travel properties
            legionLeftTriggerStart = new LegionLeftTriggerStartProperty(LegionLeftTriggerStartSlider, this);
            legionLeftTriggerEnd = new LegionLeftTriggerEndProperty(LegionLeftTriggerEndSlider, this);
            legionRightTriggerStart = new LegionRightTriggerStartProperty(LegionRightTriggerStartSlider, this);
            legionRightTriggerEnd = new LegionRightTriggerEndProperty(LegionRightTriggerEndSlider, this);
            legionHairTriggers = new LegionHairTriggersProperty(LegionHairTriggersToggle, this);

            // Touchpad vibration property (GLOBAL setting)
            legionTouchpadVibration = new LegionTouchpadVibrationProperty(LegionTouchpadVibrationComboBox, this);

            // Joystick as mouse properties
            legionJoystickAsMouseMode = new LegionJoystickAsMouseModeProperty(LegionJoystickAsMouseComboBox, LegionJoystickMouseSensGrid, this);
            legionJoystickMouseSens = new LegionJoystickMouseSensProperty(LegionJoystickMouseSensSlider, this);

            // Gamepad button mapping property
            legionGamepadMapping = new LegionGamepadMappingProperty();

            // Desktop controls preset (synced from helper for hotkey)
            legionDesktopControls = new LegionDesktopControlsProperty(LegionDesktopControlsToggle, this);

            // Controller battery properties (read-only)
            controllerBatteryLeft = new ControllerBatteryLeftProperty();
            controllerBatteryRight = new ControllerBatteryRightProperty();
            controllerChargingLeft = new ControllerChargingLeftProperty();
            controllerChargingRight = new ControllerChargingRightProperty();
            controllerConnectedLeft = new ControllerConnectedLeftProperty();
            controllerConnectedRight = new ControllerConnectedRightProperty();
            controllerVidPid = new ControllerVidPidProperty();
            controllerDeviceStatus = new ControllerDeviceStatusProperty();

            // Device capability properties (for UI visibility)
            deviceDisplayName = new DeviceDisplayNameProperty(this);
            deviceSupportsControllerRemap = new DeviceSupportsControllerRemapProperty(this);
            deviceSupportsRgbLighting = new DeviceSupportsRgbLightingProperty(this);
            deviceSupportsGyro = new DeviceSupportsGyroProperty(this);
            deviceHasScrollWheel = new DeviceHasScrollWheelProperty(this);
            deviceHasDetachableControllers = new DeviceHasDetachableControllersProperty(this);
            deviceHasTouchpad = new DeviceHasTouchpadProperty(this);

            // GPD properties
            gpdDetected = new GPDDetectedProperty(this);
            gpdWin5Connected = new GPDWin5ConnectedProperty(this);
            gpdWin5HidDebug = new GPDWin5HidDebugProperty();
            gpdWin5HidDevices = new GPDWin5HidDevicesProperty(this);
            gpdDeviceName = new GPDDeviceNameProperty(this);
            gpdSupportsFanControl = new GPDSupportsFanControlProperty(this);
            gpdFanSpeed = new GPDFanSpeedProperty(this);
            gpdFanRPM = new GPDFanRPMProperty(this);
            gpdFanMode = new GPDFanModeProperty(this);
            gpdRestoreDefaults = new GPDRestoreDefaultsProperty();
            gpdApplyMappings = new GPDApplyMappingsProperty();
            gpdButtonL4 = new GPDButtonL4Property(this);
            gpdButtonR4 = new GPDButtonR4Property(this);
            gpdButtonA = new GPDButtonProperty(this, Function.GPDButtonA);
            gpdButtonB = new GPDButtonProperty(this, Function.GPDButtonB);
            gpdButtonX = new GPDButtonProperty(this, Function.GPDButtonX);
            gpdButtonY = new GPDButtonProperty(this, Function.GPDButtonY);
            gpdButtonDPadUp = new GPDButtonProperty(this, Function.GPDButtonDPadUp);
            gpdButtonDPadDown = new GPDButtonProperty(this, Function.GPDButtonDPadDown);
            gpdButtonDPadLeft = new GPDButtonProperty(this, Function.GPDButtonDPadLeft);
            gpdButtonDPadRight = new GPDButtonProperty(this, Function.GPDButtonDPadRight);
            gpdButtonL3 = new GPDButtonProperty(this, Function.GPDButtonL3);
            gpdButtonR3 = new GPDButtonProperty(this, Function.GPDButtonR3);
            gpdButtonLSLeft = new GPDButtonProperty(this, Function.GPDButtonLSLeft);
            gpdButtonLSRight = new GPDButtonProperty(this, Function.GPDButtonLSRight);
            controllerEmulationAvailable = new ControllerEmulationAvailableProperty(this);
            controllerEmulationEnabled = new ControllerEmulationEnabledProperty(ControllerEmulationEnabledToggle, this);
            controllerEmulationHideStockController = new ControllerEmulationHideStockControllerProperty(ControllerEmulationHideStockControllerToggle, this);
            controllerEmulationImprovedInput = new ControllerEmulationImprovedInputProperty(ControllerEmulationImprovedInputToggle, this);
            controllerEmulationImprovedInput.PropertyChanged += ControllerEmulationImprovedInput_PropertyChanged;
            controllerEmulationHideTarget = new ControllerEmulationHideTargetProperty(ControllerEmulationHideTargetComboBox, this);
            controllerEmulationGyroSource = new ControllerEmulationGyroSourceProperty(ControllerEmulationGyroSourceComboBox, this);
            controllerEmulationMode = new ControllerEmulationModeProperty(ControllerEmulationModeComboBox, this);
            controllerEmulationRumbleProfile = new ControllerEmulationRumbleProfileProperty(ControllerEmulationRumbleProfileComboBox, this);
            controllerEmulationGyroActivationMode = new ControllerEmulationGyroActivationModeProperty(ControllerEmulationGyroActivationModeComboBox, this);
            controllerEmulationGyroActivationButton = new ControllerEmulationGyroActivationButtonProperty(ControllerEmulationGyroActivationButtonComboBox, this);
            controllerEmulationDs4Orientation = new ControllerEmulationDs4OrientationProperty(ControllerEmulationDs4OrientationComboBox, this);
            controllerEmulationPs4TouchpadEnabled = new ControllerEmulationPs4TouchpadEnabledProperty(ControllerEmulationPs4TouchpadToggle, this);
            controllerEmulationLedForwardingEnabled = new ControllerEmulationLedForwardingEnabledProperty(ControllerEmulationLedForwardingToggle, this);
            controllerEmulationMouseSensitivity = new ControllerEmulationMouseSensitivityProperty(ControllerEmulationMouseSensitivitySlider, this);
            controllerEmulationMouseThreshold = new ControllerEmulationMouseThresholdProperty(ControllerEmulationMouseThresholdSlider, this);
            controllerEmulationMouseAxis = new ControllerEmulationMouseAxisProperty(ControllerEmulationMouseAxisComboBox, this);
            controllerEmulationMouseInvertX = new ControllerEmulationMouseInvertXProperty(ControllerEmulationMouseInvertXToggle, this);
            controllerEmulationMouseInvertY = new ControllerEmulationMouseInvertYProperty(ControllerEmulationMouseInvertYToggle, this);
            controllerEmulationMouseGainX = new ControllerEmulationMouseGainXProperty(ControllerEmulationMouseGainXSlider, this);
            controllerEmulationMouseGainY = new ControllerEmulationMouseGainYProperty(ControllerEmulationMouseGainYSlider, this);
            controllerEmulationStickInvertX = new ControllerEmulationStickInvertXProperty(ControllerEmulationStickInvertXToggle, this);
            controllerEmulationStickInvertY = new ControllerEmulationStickInvertYProperty(ControllerEmulationStickInvertYToggle, this);
            controllerEmulationStickSelect = new ControllerEmulationStickSelectProperty(ControllerEmulationStickSelectComboBox, this);
            controllerEmulationStickOnlyJoystickData = new ControllerEmulationStickOnlyJoystickDataProperty(ControllerEmulationStickOnlyJoystickToggle, this);
            // Min/Max gyro speed, Min/Max output, Power curve, Deadzone, Precision speed,
            // Output mix — pipeline removed in #79 round 5 (matches HC). Sensitivity stays.
            controllerEmulationStickSensitivityV2 = new ControllerEmulationStickSensitivityV2Property(StickSensitivityV2Slider, this);
            controllerEmulationStickOrientationV2 = new ControllerEmulationStickOrientationV2Property(StickOrientationV2ComboBox, this);
            controllerEmulationStickConversion = new ControllerEmulationStickConversionProperty(StickConversionComboBox, this);
            controllerEmulationVirtualAbxyLayout = new ControllerEmulationVirtualABXYLayoutProperty(ControllerEmulationVirtualAbxyLayoutComboBox, this);
            gpdFanCurveGraph = new GPDFanCurveGraphProperty(this);
            gpdFanCurveGraph.SetGraphUpdateCallback(OnGPDFanCurveUpdated);
            gpdCPUTemp = new GPDCPUTempProperty(this);
            gpdCPUTemp.SetTempUpdateCallback(OnGPDCPUTempUpdated);
            gpdFanCurveVisible = new GPDFanCurveVisibleProperty();
            gpdFanCurveEnabled = new GPDFanCurveEnabledProperty(this);

            // Default Game Profile properties
            defaultGameProfileAvailable = new DefaultGameProfileAvailableProperty(this);
            defaultGameProfileData = new DefaultGameProfileDataProperty(this);
            defaultGameProfileEnabled = new DefaultGameProfileEnabledProperty(this);

            // Set up Default Game Profile callbacks
            defaultGameProfileAvailable.SetVisibilityCallback(SetDefaultProfileCardVisibility);
            defaultGameProfileData.SetDataCallback(UpdateDefaultProfileDisplay);
            defaultGameProfileEnabled.BindToggle(DefaultProfileToggle);
            defaultGameProfileEnabled.SetEnabledCallback(OnDefaultProfileEnabledChanged);

            // Settings properties
            tdpMethod = new TdpMethodProperty(TdpMethodComboBox, this);
            emulationBackend = new EmulationBackendProperty(ViiperEmulationToggle, this);
            usbipInstalled = new UsbipInstalledProperty();
            installUsbip = new InstallUsbipProperty(this);
            viiperDeviceType = new ViiperStringComboProperty("xbox360", Shared.Enums.Function.Viiper_DeviceType, ViiperDeviceTypeComboBox, this);
            viiperInputSource = new ViiperStringComboProperty("XInput", Shared.Enums.Function.Viiper_InputSource, ViiperInputSourceComboBox, this);
            viiperGyroSource = new ViiperStringComboProperty("Left", Shared.Enums.Function.Viiper_GyroSource, ViiperGyroSourceComboBox, this);
            viiperSteamSubDevice = new ViiperStringComboProperty("steam-deck", Shared.Enums.Function.Viiper_SteamSubDevice, ViiperSteamSubDeviceComboBox, this);
            viiperSonySubDevice = new ViiperStringComboProperty("dualsense-edge", Shared.Enums.Function.Viiper_SonySubDevice, ViiperSonySubDeviceComboBox, this);
            viiperNintendoSubDevice = new ViiperStringComboProperty("switchpro", Shared.Enums.Function.Viiper_NintendoSubDevice, ViiperNintendoSubDeviceComboBox, this);
            viiperGuideButtonMode = new ViiperStringComboProperty("Native", Shared.Enums.Function.Viiper_GuideButtonMode, ViiperGuideButtonModeComboBox, this);
            viiperSwapRumbleMotors = new ViiperSwapRumbleMotorsProperty(ViiperSwapRumbleMotorsToggle, this);
            viiperMirrorLightbarToStick = new ViiperMirrorLightbarToStickProperty(ViiperMirrorLightbarToStickToggle, this);
            viiperStickGyroEnabled = new ViiperStickGyroEnabledProperty(ViiperStickGyroEnabledToggle, this);
            viiperJoyconGyroPerHalf = new ViiperJoyconGyroPerHalfProperty(ViiperJoyconGyroPerHalfToggle, this);
            viiperAlternateGyroConvention = new ViiperAlternateGyroConventionProperty(ViiperAlternateGyroConventionToggle, this);
            viiperRumbleIntensity = new ViiperRumbleIntensityProperty(100, ViiperRumbleIntensitySlider, this);
            viiperGyroAxisMapX = new ViiperStringComboProperty("X", Shared.Enums.Function.Viiper_GyroAxisMapX, ViiperGyroAxisMapXComboBox, this);
            viiperGyroAxisMapY = new ViiperStringComboProperty("Y", Shared.Enums.Function.Viiper_GyroAxisMapY, ViiperGyroAxisMapYComboBox, this);
            viiperGyroAxisMapZ = new ViiperStringComboProperty("Z", Shared.Enums.Function.Viiper_GyroAxisMapZ, ViiperGyroAxisMapZComboBox, this);
            viiperStickTriggerConfig = new ViiperStickTriggerConfigProperty(string.Empty);
            viiperStickTriggerPreviewEnabled = new ViiperStickTriggerPreviewEnabledProperty();
            viiperStickTriggerLiveSample = new ViiperStickTriggerLiveSampleProperty();
            InitStickTriggerControls();
            InitStickTriggerPreview();
            // Keep the "nn%" label in sync as the user drags.
            if (ViiperRumbleIntensitySlider != null)
            {
                ViiperRumbleIntensitySlider.ValueChanged += (s, e) =>
                {
                    if (ViiperRumbleIntensityValue != null)
                        ViiperRumbleIntensityValue.Text = ((int)ViiperRumbleIntensitySlider.Value) + "%";
                };
            }
            // Refresh the "Legion L disabled" warning when either the Guide mode changes or
            // the VIIPER backend toggles on.
            if (ViiperGuideButtonModeComboBox != null)
                ViiperGuideButtonModeComboBox.SelectionChanged += (s, e) => UpdateViiperLegionLDisabledHint();
            // Show USBIP install card only when VIIPER toggle is on AND driver is missing
            emulationBackend.PropertyChanged += (s, e) => { UpdateUsbipCardVisibility(); UpdateViiperConfigVisibility(); UpdateViiperLegionLDisabledHint(); UpdateQuickSettingsTileStates(); UpdateViiperStickGyroSectionVisibility(); };
            usbipInstalled.PropertyChanged += (s, e) => { UpdateUsbipCardVisibility(); UpdateLabsUsbipUI(usbipInstalled.Value); };
            // Show Steam sub-device picker only when a Steam device type is selected
            viiperDeviceType.PropertyChanged += (s, e) => { UpdateViiperConfigVisibility(); UpdateQuickSettingsTileStates(); UpdateViiperStickGyroSectionVisibility(); };
            // Re-evaluate sub-device-dependent panels (e.g. Joy-Con Pair per-half gyro) when the Nintendo sub-device changes.
            viiperNintendoSubDevice.PropertyChanged += (s, e) => UpdateViiperConfigVisibility();
            controllerEmulationMode.PropertyChanged += (s, e) => UpdateQuickSettingsTileStates();
            winRing0Available = new WinRing0AvailableProperty(this);
            pawnIOAvailable = new PawnIOAvailableProperty();
            pawnIOInstalled = new PawnIOInstalledProperty(this);
            installPawnIO = new InstallPawnIOProperty(this);
            setupWarnings = new SetupWarningsProperty(this) { OnWarningsChanged = OnSetupWarningsChanged };
            hidHideInstalled = new HidHideInstalledProperty(this);
            installHidHide = new InstallHidHideProperty(this);
            autoHibernateEnabled = new AutoHibernateEnabledProperty(AutoHibernateToggle, this);
            autoHibernateIdleMinutes = new AutoHibernateIdleMinutesProperty(15, AutoHibernateTimeoutSlider, this);

            // Set up callbacks for TDP method availability
            winRing0Available.SetAvailabilityCallback(UpdateWinRing0Visibility);
            pawnIOInstalled.SetInstalledCallback(UpdatePawnIOInstalledUI);
            hidHideInstalled.SetInstalledCallback(UpdateHidHideInstalledUI);

            // AutoTDP properties
            autoTDPEnabled = new AutoTDPEnabledProperty(false);
            autoTDPTargetFPS = new AutoTDPTargetFPSProperty(60);
            autoTDPCurrentFPS = new AutoTDPCurrentFPSProperty(0);
            autoTDPMinTDP = new AutoTDPMinTDPProperty(8);
            autoTDPMaxTDP = new AutoTDPMaxTDPProperty(30);
            autoTDPUseMLMode = new AutoTDPUseMLModeProperty(false);
            autoTDPControllerType = new AutoTDPControllerTypeProperty(0);  // 0=PID
            autoTDPMLStatus = new AutoTDPMLStatusProperty("");
            autoTDPLearnedGameData = new AutoTDPLearnedGameDataProperty("");
            autoTDPResetML = new AutoTDPResetMLProperty(false);
            autoTDPPauseWhenUnfocused = new AutoTDPPauseWhenUnfocusedProperty(true); // Default: enabled
            tdpLimits = new TDPLimitsProperty("4,35");
            cpuCoreConfig = new CPUCoreConfigProperty("");
            cpuCoreActiveConfig = new CPUCoreActiveConfigProperty("");
            coreParkingPercent = new CoreParkingPercentProperty(100); // 100% = all cores active
            forceParkMode = new ForceParkModeProperty(false);
            forceDefaultGameProfile = new ForceDefaultGameProfileProperty(false);

            // TDP Boost properties (defaults: enabled=false, SPPT=1W, FPPT=3W)
            tdpBoostEnabled = new TDPBoostEnabledProperty(false);
            tdpBoostSPPT = new TDPBoostSPPTProperty(1);
            tdpBoostFPPT = new TDPBoostFPPTProperty(3);

            // OS Power Mode property
            osPowerMode = new OSPowerModeProperty();

            // FPS Limit property
            fpsLimit = new FPSLimitProperty();

            // Profile Detection Settings
            profileMatchByExe = new ProfileMatchByExeProperty(ProfileMatchByExeToggle, this);
            profileGamesOnly = new ProfileGamesOnlyProperty(ProfileGamesOnlyToggle, this);
            // DISABLED: Custom games, blacklist, and current apps features - caused user confusion
            // profileCustomGamePath = new ProfileCustomGamePathProperty(CustomGamesList, CustomGamesEmptyText, this);
            // profileBlacklistPaths = new ProfileBlacklistPathsProperty(BlacklistList, BlacklistEmptyText, this);
            // foregroundApp = new ForegroundAppProperty(ForegroundAppsContainer, this);
            // foregroundApp.OnAppsChanged = UpdateForegroundAppsList;

            // Set up Legion tab visibility callback
            legionGoDetected.SetVisibilityCallback(SetLegionTabVisibility);

            // Set up Scale tab visibility callback (hide when Lossless Scaling not installed)
            losslessScalingInstalled.SetVisibilityCallback(SetScaleTabVisibility);

            // Set up custom TDP visibility callback
            legionPerformanceMode.SetCustomTDPVisibilityCallback(SetCustomTDPVisibility);

            // Set up device capability callbacks for Legion tab section visibility
            deviceDisplayName.SetDisplayNameCallback(SetLegionDeviceName);
            deviceSupportsControllerRemap.SetVisibilityCallback(SetControllerRemappingSectionVisibility);
            deviceSupportsRgbLighting.SetVisibilityCallback(SetLightingSectionVisibility);
            deviceSupportsGyro.SetVisibilityCallback(SetGyroSectionVisibility);
            deviceHasScrollWheel.SetVisibilityCallback(SetScrollWheelSectionVisibility);
            deviceHasDetachableControllers.SetVisibilityCallback(SetControllerBatterySectionVisibility);
            deviceHasTouchpad.SetVisibilityCallback(SetTouchpadVibrationSectionVisibility);

            // GPD tab callbacks
            gpdDetected.SetVisibilityCallback(SetGPDTabVisibility);
            gpdDeviceName.SetNameCallback(SetGPDDeviceName);
            gpdSupportsFanControl.SetVisibilityCallback(SetGPDFanControlVisibility);
            gpdWin5Connected.SetConnectionCallback(SetGPDButtonRemapVisibility);
            controllerEmulationAvailable.SetAvailabilityCallback(SetControllerEmulationAvailability);
            gpdFanRPM.SetRPMCallback(UpdateGPDFanRPM);
            gpdFanMode.SetModeCallback(UpdateGPDFanMode);

            // NOTE: Event handlers for Chill FPS will be registered AFTER first sync
            // to avoid crash when binding evaluates RadeonChillOnText before both values are ready
            // See RegisterChillFPSHandlers() called after sync completes

            propertiesTimer.Stop();
            Logger.Info($"[TIMING] Property creation: {propertiesTimer.ElapsedMilliseconds}ms");

            var widgetPropsTimer = Stopwatch.StartNew();
            properties = new WidgetProperties(
                osd,
                tdp,
                runningGame,
                perGameProfile,
                cpuBoost,
                cpuEPP,
                maxCPUState,
                minCPUState,
                // GPU Clock - DISABLED: Not supported by RyzenAdj on this hardware (returns error -1)
                //limitGPUClock,
                //gpuClockMin,
                //gpuClockMax,
                refreshRates,
                refreshRate,
                resolutions,
                resolution,
                displayOrientation,
                hdrSupported,
                hdrEnabled,
                adaptiveBrightnessMode,
                trackedGame,
                rtssInstalled,
                isForeground,
                amdRadeonSuperResolutionEnabled,
                amdRadeonSuperResolutionSupported,
                amdRadeonSuperResolutionSharpness,
                amdFluidMotionFrameEnabled,
                amdFluidMotionFrameSupported,
                amdFluidMotionFrameAlgorithm,
                amdFluidMotionFrameSearchMode,
                amdFluidMotionFramePerformanceMode,
                amdFluidMotionFrameFastMotionResponse,
                amdFluidMotionFrameV1Supported,
                amdRadeonAntiLagEnabled,
                amdRadeonAntiLagSupported,
                amdRadeonBoostEnabled,
                amdRadeonBoostSupported,
                amdRadeonBoostResolution,
                amdRadeonChillEnabled,
                amdRadeonChillSupported,
                amdRadeonChillMinFPSProperty,
                amdRadeonChillMaxFPSProperty,
                amdImageSharpeningEnabled,
                amdImageSharpeningSupported,
                amdImageSharpeningSharpness,
                amdDisplayBrightnessSupported,
                amdDisplayBrightness,
                amdDisplayContrastSupported,
                amdDisplayContrast,
                amdDisplaySaturationSupported,
                amdDisplaySaturation,
                amdDisplayTemperatureSupported,
                amdDisplayTemperature,
                losslessScalingInstalled,
                losslessScalingRunning,
                losslessScalingEnabled,
                losslessScalingCurrentProfile,
                losslessScalingScalingType,
                losslessScalingSharpness,
                losslessScalingFSROptimize,
                losslessScalingAnime4KSize,
                losslessScalingAnime4KVRS,
                losslessScalingScaleMode,
                losslessScalingScaleFactor,
                losslessScalingAspectRatio,
                losslessScalingFrameGenType,
                losslessScalingLSFG3Mode,
                losslessScalingLSFG3Multiplier,
                losslessScalingLSFG3Target,
                losslessScalingLSFG2Mode,
                losslessScalingFlowScale,
                losslessScalingSize,
                losslessScalingAutoScale,
                losslessScalingAutoScaleDelay,
                losslessScalingSaveAndRestart,
                losslessScalingCreateProfile,
                // Additional Settings.xml-backed LS properties (added 2026-05-01)
                losslessScalingSyncMode,
                losslessScalingCaptureApi,
                losslessScalingDrawFps,
                losslessScalingHdrSupport,
                losslessScalingGsyncSupport,
                losslessScalingResizeBeforeScaling,
                losslessScalingLS1Type,
                losslessScalingMaxFrameLatency,
                losslessScalingResetProfile,
                currentTdp,
                legionGoDetected,
                legionTouchpadEnabled,
                legionLightMode,
                legionLightColor,
                legionLightBrightness,
                legionLightSpeed,
                legionPerformanceMode,
                legionFanFullSpeed,
                legionUnlockFanCurve,
                legionFanCurveGraph,
                legionFanCurvePerMode,
                legionUnlockFanCurvePerMode,
                legionCPUTemp,
                legionFanSensorTemp,
                legionCPUFanRPM,
                legionFanCurveVisible,
                legionGyroEnabled,
                legionVibration,
                legionVibrationMode,
                legionPowerLight,
                legionChargeLimit,
                legionTouchpadVibration,
                legionNintendoLayout,
                legionControllerProfile,
                legionGamepadMapping,
                legionButtonY1,
                legionButtonY2,
                legionButtonY3,
                legionButtonM1,
                legionButtonM2,
                legionButtonM3,
                legionButtonDesktop,
                legionButtonPage,
                legionGyroTarget,
                legionGyroSensitivityX,
                legionGyroSensitivityY,
                legionGyroInvertX,
                legionGyroInvertY,
                legionGyroMappingType,
                legionGyroActivationMode,
                legionGyroActivationButton,
                legionGyroDeadzone,
                legionLeftStickDeadzone,
                legionRightStickDeadzone,
                legionLeftTriggerStart,
                legionLeftTriggerEnd,
                legionRightTriggerStart,
                legionRightTriggerEnd,
                legionHairTriggers,
                tdpMethod,
                emulationBackend,
                usbipInstalled,
                installUsbip,
                viiperDeviceType,
                viiperInputSource,
                viiperGyroSource,
                viiperSteamSubDevice,
                viiperSonySubDevice,
                viiperNintendoSubDevice,
                viiperGuideButtonMode,
                viiperSwapRumbleMotors,
                viiperRumbleIntensity,
                viiperMirrorLightbarToStick,
                viiperStickGyroEnabled,
                viiperJoyconGyroPerHalf,
                viiperAlternateGyroConvention,
                viiperGyroAxisMapX,
                viiperGyroAxisMapY,
                viiperGyroAxisMapZ,
                viiperStickTriggerConfig,
                viiperStickTriggerPreviewEnabled,
                viiperStickTriggerLiveSample,
                winRing0Available,
                pawnIOAvailable,
                pawnIOInstalled,
                installPawnIO,
                setupWarnings,
                hidHideInstalled,
                installHidHide,
                autoHibernateEnabled,
                autoHibernateIdleMinutes,
                autoTDPEnabled,
                autoTDPTargetFPS,
                autoTDPCurrentFPS,
                autoTDPMinTDP,
                autoTDPMaxTDP,
                autoTDPUseMLMode,
                autoTDPControllerType,
                autoTDPPauseWhenUnfocused,
                autoTDPMLStatus,
                autoTDPLearnedGameData,
                autoTDPResetML,
                fpsLimit,
                osPowerMode,
                tdpLimits,
                cpuCoreConfig,
                cpuCoreActiveConfig,
                coreParkingPercent,
                forceParkMode,
                tdpBoostEnabled,
                tdpBoostSPPT,
                tdpBoostFPPT,
                legionDesktopControls,
                legionJoystickAsMouseMode,
                legionJoystickMouseSens,
                controllerBatteryLeft,
                controllerBatteryRight,
                controllerChargingLeft,
                controllerChargingRight,
                controllerConnectedLeft,
                controllerConnectedRight,
                controllerVidPid,
                controllerDeviceStatus,
                // Device capability properties (for UI visibility)
                deviceDisplayName,
                deviceSupportsControllerRemap,
                deviceSupportsRgbLighting,
                deviceSupportsGyro,
                deviceHasScrollWheel,
                deviceHasDetachableControllers,
                deviceHasTouchpad,
                // GPD properties
                gpdDetected,
                gpdWin5Connected,
                gpdWin5HidDebug,
                gpdWin5HidDevices,
                gpdDeviceName,
                gpdSupportsFanControl,
                gpdFanSpeed,
                gpdFanRPM,
                gpdFanMode,
                gpdRestoreDefaults,
                gpdApplyMappings,
                gpdButtonL4,
                gpdButtonR4,
                gpdButtonA,
                gpdButtonB,
                gpdButtonX,
                gpdButtonY,
                gpdButtonDPadUp,
                gpdButtonDPadDown,
                gpdButtonDPadLeft,
                gpdButtonDPadRight,
                gpdButtonL3,
                gpdButtonR3,
                gpdButtonLSLeft,
                gpdButtonLSRight,
                controllerEmulationAvailable,
                controllerEmulationEnabled,
                controllerEmulationHideStockController,
                controllerEmulationImprovedInput,
                controllerEmulationHideTarget,
                controllerEmulationGyroSource,
                controllerEmulationMode,
                controllerEmulationRumbleProfile,
                controllerEmulationGyroActivationMode,
                controllerEmulationGyroActivationButton,
                controllerEmulationDs4Orientation,
                controllerEmulationPs4TouchpadEnabled,
                controllerEmulationLedForwardingEnabled,
                controllerEmulationMouseSensitivity,
                controllerEmulationMouseThreshold,
                controllerEmulationMouseAxis,
                controllerEmulationMouseInvertX,
                controllerEmulationMouseInvertY,
                controllerEmulationMouseGainX,
                controllerEmulationMouseGainY,
                controllerEmulationStickInvertX,
                controllerEmulationStickInvertY,
                controllerEmulationStickSelect,
                controllerEmulationStickOnlyJoystickData,
                controllerEmulationStickSensitivityV2,
                controllerEmulationStickOrientationV2,
                controllerEmulationStickConversion,
                controllerEmulationVirtualAbxyLayout,
                gpdFanCurveGraph,
                gpdCPUTemp,
                gpdFanCurveVisible,
                gpdFanCurveEnabled,
                defaultGameProfileAvailable,
                defaultGameProfileData,
                defaultGameProfileEnabled,
                forceDefaultGameProfile,
                // Profile Detection Settings
                profileMatchByExe,
                profileGamesOnly
                // DISABLED: Custom games, blacklist, and current apps features
                // profileCustomGamePath,
                // profileBlacklistPaths,
                // foregroundApp
            );
            widgetPropsTimer.Stop();
            Logger.Info($"[TIMING] WidgetProperties creation: {widgetPropsTimer.ElapsedMilliseconds}ms");

            // Register card focus handlers for all interactive controls
            RegisterCardFocusHandlers();

            constructorTimer.Stop();
            Logger.Info($"=== GamingWidget constructor END === Total: {constructorTimer.ElapsedMilliseconds}ms, Instance hash: {this.GetHashCode()}");
        }

        // Track the currently focused card
        private Border currentFocusedCard = null;
        private SolidColorBrush cardDefaultBorderBrush;
        private SolidColorBrush cardFocusBorderBrush;

        private void GamingWidget_Loaded(object sender, RoutedEventArgs e)
        {
            Logger.Info($"GamingWidget_Loaded called. Widget is null: {widget == null}, WidgetActivity is null: {widgetActivity == null}, Pipe connected: {App.IsConnected}");

            // Set initial navigation selection (first RadioButton - Quick tab).
            // Pre-arm the destination tag so the nav drift guard treats the initial
            // Loaded selection as user-driven, not as focus drift.
            ArmUserNavIntent("Quick");
            QuickNavItem.IsChecked = true;

            // Attach Pointer/Tap/KeyDown listeners on every nav RadioButton via
            // AddHandler(handledEventsToo: true) so the drift guard gets pre-armed
            // even when UWP's RadioButton marks the input event Handled internally.
            AttachNavInteractionTracking();

            // Load profile customization settings
            LoadProfileCustomizationSettings();

            // Load TDP preset customization settings
            LoadTdpPresetsSettings();

            // Initialize CPU State comboboxes with percentage values
            InitializeCPUStateComboBoxes();

            // Check if this is a clean install (no saved Global profile)
            var settings = ApplicationData.Current.LocalSettings;
            isCleanInstall = !settings.Containers.ContainsKey("Profile_Global");
            if (isCleanInstall)
            {
                Logger.Info("Clean install detected - will initialize profile with current system values after helper sync");
            }

            // Load profiles from storage
            LoadProfileFromStorage("Global", globalProfile);
            LoadProfileFromStorage("AC", acProfile);
            LoadProfileFromStorage("DC", dcProfile);

            // Load global controller profile from storage
            LoadControllerProfileFromStorage("Global", globalControllerProfile);

            // Clean up any invalid "No game detected" profiles
            CleanupInvalidProfiles();

            // Load saved Power Source Profile toggle state BEFORE attaching event handler
            LoadPowerSourceProfileSetting();

            // Initialize power source profile
            PowerSourceProfileToggle.Toggled += PowerSourceProfileToggle_Toggled;
            SyncPowerSourceProfileToggleForCurrentContext();

            UpdateActiveProfileIndicator();

            // Subscribe to power source changes
            PowerManager.PowerSupplyStatusChanged += PowerManager_PowerSourceChanged;

            // Subscribe to checkbox changes to save settings
            // Event handlers are now in XAML (ProfileSettingsCheckBox_Changed)

            // IMPORTANT: Set loading flag BEFORE subscribing to events to prevent
            // UI initialization events from triggering saves that overwrite stored values
            isLoadingControllerProfile = true;

            // Subscribe to settings changes for auto-save
            SubscribeToSettingsChanges();

            // Apply global controller profile to UI controls
            // (ApplyControllerProfile will clear isLoadingControllerProfile when done)
            ApplyControllerProfile(globalControllerProfile);

            // Subscribe to power source profile toggle changes to update game profile card
            PowerSourceProfileToggle.Toggled += PowerSourceToggle_Changed;

            // Subscribe to per-game profile toggle changes
            PerGameProfileToggle.Toggled += PerGameProfileToggle_Changed;

            // Subscribe to Lossless Scaling FrameGenType ComboBox for showing/hiding LSFG settings
            LosslessScalingFrameGenTypeComboBox.SelectionChanged += LosslessScalingFrameGenTypeComboBox_SelectionChanged;
            AMDFluidMotionFrameToggle.Toggled += AMDFluidMotionFrameToggle_Toggled;

            // Subscribe to Lossless Scaling property changes for status updates
            if (losslessScalingInstalled != null)
                losslessScalingInstalled.PropertyChanged += LosslessScalingStatus_PropertyChanged;
            if (losslessScalingRunning != null)
                losslessScalingRunning.PropertyChanged += LosslessScalingStatus_PropertyChanged;
            if (losslessScalingCurrentProfile != null)
                losslessScalingCurrentProfile.PropertyChanged += LosslessScalingCurrentProfile_PropertyChanged;

            // Subscribe to running game changes to get exe path for Lossless Scaling profiles
            if (runningGame != null)
                runningGame.PropertyChanged += RunningGame_PropertyChanged;

            // Subscribe to game text changes
            RunningGameText.RegisterPropertyChangedCallback(TextBlock.TextProperty, OnGameTextChanged);

            // Subscribe to window size changes for compact mode detection
            this.SizeChanged += GamingWidget_SizeChanged;

            // Initialize compact mode based on current size
            UpdateCompactMode(this.ActualWidth);

            // Update profile display
            UpdateProfileDisplay();
            UpdateGameProfileCardVisibility();

            // Load Device TDP limits (must be before AutoTDP settings)
            LoadTDPLimitsFromStorage();

            // Load AutoTDP settings and subscribe to current FPS updates
            LoadAutoTDPSettings();
            if (autoTDPCurrentFPS != null)
                autoTDPCurrentFPS.PropertyChanged += AutoTDPCurrentFPS_PropertyChanged;
            if (autoTDPMLStatus != null)
                autoTDPMLStatus.PropertyChanged += AutoTDPMLStatus_PropertyChanged;
            if (autoTDPLearnedGameData != null)
                autoTDPLearnedGameData.PropertyChanged += AutoTDPLearnedGameData_PropertyChanged;
            if (autoTDPUseMLMode != null)
                autoTDPUseMLMode.PropertyChanged += AutoTDPUseMLMode_PropertyChanged;
            if (autoTDPControllerType != null)
                autoTDPControllerType.PropertyChanged += AutoTDPControllerType_PropertyChanged;
            if (autoTDPEnabled != null)
                autoTDPEnabled.PropertyChanged += AutoTDPEnabled_PropertyChanged;
            if (autoTDPTargetFPS != null)
                autoTDPTargetFPS.PropertyChanged += AutoTDPTargetFPS_PropertyChanged;

            // Load TDP Boost settings (SPPT/FPPT from LocalSettings)
            LoadTDPBoostSettings();

            // Load Sticky TDP settings (defaults to enabled on new installs)
            LoadStickyTDPSettings();

            // Subscribe to TDP Boost property changes from helper (profile sync)
            if (tdpBoostEnabled != null)
                tdpBoostEnabled.PropertyChanged += TDPBoostEnabled_PropertyChanged;

            // Subscribe to property changes that affect Quick Settings tiles
            SubscribeToQuickSettingsPropertyChanges();

            // Initialize Quick Settings tiles (loads custom shortcuts into qsTileMap)
            InitializeQuickSettings();

            // Load OSD customization settings
            LoadOSDConfigFromStorage();
            LoadOSDOptionsForLevel(1); // Load Basic level options by default

            // Load Display and OSD settings
            LoadDisplayOSDSettingsFromStorage();

            // Load Performance Overlay setting
            LoadPerformanceOverlaySetting();

            // Load Power Plan settings
            LoadPowerPlanSettings();

            // Load Force Default Game Profile setting
            LoadForceDefaultGameProfileSetting();

            // Restore Auto Hibernate AC/DC power-source choice (issue #88 bug #3)
            LoadAutoHibernateModeSetting();

            // Send OSD config to helper on startup
            SendOSDConfigToHelper();
            _ = SendDisplayOSDConfigToHelper();

            // Initialize Labs section (DAService status polling)
            InitializeLabsSection();
        }

        private void AutoTDPCurrentFPS_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (AutoTDPCurrentFPSValue != null && autoTDPCurrentFPS != null)
                {
                    int fps = autoTDPCurrentFPS.Value;
                    AutoTDPCurrentFPSValue.Text = fps > 0 ? $"{fps} FPS" : "-- FPS";
                }
            });
        }

        private void SubscribeToQuickSettingsPropertyChanges()
        {
            // Subscribe to properties that affect Quick Settings tile states
            if (legionPerformanceMode != null)
                legionPerformanceMode.PropertyChanged += QuickSettingsProperty_Changed;
            if (tdp != null)
                tdp.PropertyChanged += QuickSettingsProperty_Changed;
            if (osd != null)
                osd.PropertyChanged += QuickSettingsProperty_Changed;
            if (perGameProfile != null)
                perGameProfile.PropertyChanged += QuickSettingsProperty_Changed;
            if (runningGame != null)
                runningGame.PropertyChanged += QuickSettingsProperty_Changed;
            if (fpsLimit != null)
                fpsLimit.PropertyChanged += QuickSettingsProperty_Changed;
            if (osPowerMode != null)
                osPowerMode.PropertyChanged += OSPowerMode_PropertyChanged;
            if (resolution != null)
            {
                resolution.PropertyChanged += QuickSettingsProperty_Changed;
                resolution.PropertyChanged += Resolution_PropertyChanged_OSD;
            }
            if (hdrEnabled != null)
                hdrEnabled.PropertyChanged += QuickSettingsProperty_Changed;
            if (hdrSupported != null)
                hdrSupported.PropertyChanged += QuickSettingsProperty_Changed;
            if (displayOrientation != null)
                displayOrientation.PropertyChanged += QuickSettingsProperty_Changed;
            if (losslessScalingEnabled != null)
                losslessScalingEnabled.PropertyChanged += QuickSettingsProperty_Changed;
            if (amdFluidMotionFrameEnabled != null)
                amdFluidMotionFrameEnabled.PropertyChanged += QuickSettingsProperty_Changed;
            if (amdRadeonSuperResolutionEnabled != null)
                amdRadeonSuperResolutionEnabled.PropertyChanged += QuickSettingsProperty_Changed;
            if (amdRadeonAntiLagEnabled != null)
                amdRadeonAntiLagEnabled.PropertyChanged += QuickSettingsProperty_Changed;
            if (amdRadeonChillEnabled != null)
                amdRadeonChillEnabled.PropertyChanged += QuickSettingsProperty_Changed;
            if (amdRadeonBoostEnabled != null)
                amdRadeonBoostEnabled.PropertyChanged += QuickSettingsProperty_Changed;
            if (autoTDPEnabled != null)
                autoTDPEnabled.PropertyChanged += QuickSettingsProperty_Changed;

            // Controller battery properties - update tile when battery status changes
            if (controllerBatteryLeft != null)
            {
                controllerBatteryLeft.PropertyChanged += QuickSettingsProperty_Changed;
                controllerBatteryLeft.PropertyChanged += LegionControllerBattery_PropertyChanged;
            }
            if (controllerBatteryRight != null)
            {
                controllerBatteryRight.PropertyChanged += QuickSettingsProperty_Changed;
                controllerBatteryRight.PropertyChanged += LegionControllerBattery_PropertyChanged;
            }
            if (controllerChargingLeft != null)
            {
                controllerChargingLeft.PropertyChanged += QuickSettingsProperty_Changed;
                controllerChargingLeft.PropertyChanged += LegionControllerBattery_PropertyChanged;
            }
            if (controllerChargingRight != null)
            {
                controllerChargingRight.PropertyChanged += QuickSettingsProperty_Changed;
                controllerChargingRight.PropertyChanged += LegionControllerBattery_PropertyChanged;
            }
            if (controllerConnectedLeft != null)
            {
                controllerConnectedLeft.PropertyChanged += LegionControllerBattery_PropertyChanged;
            }
            if (controllerConnectedRight != null)
            {
                controllerConnectedRight.PropertyChanged += LegionControllerBattery_PropertyChanged;
            }
            if (controllerVidPid != null)
            {
                controllerVidPid.PropertyChanged += LegionControllerVidPid_PropertyChanged;
            }
            if (controllerDeviceStatus != null)
            {
                controllerDeviceStatus.PropertyChanged += LegionControllerDeviceStatus_PropertyChanged;
            }

            // Subscribe to CPU core config changes
            if (cpuCoreConfig != null)
                cpuCoreConfig.PropertyChanged += CPUCoreConfig_PropertyChanged;

            Logger.Info("Subscribed to Quick Settings property changes");
        }

        private void CPUCoreConfig_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (cpuCoreConfig != null && !string.IsNullOrEmpty(cpuCoreConfig.Value))
                {
                    // Parse "pCores,eCores,isHybrid" format
                    var parts = cpuCoreConfig.Value.Split(',');
                    if (parts.Length >= 3 &&
                        int.TryParse(parts[0], out int pCores) &&
                        int.TryParse(parts[1], out int eCores) &&
                        bool.TryParse(parts[2], out bool isHybrid))
                    {
                        Logger.Info($"Received CPU core config from helper: {pCores}P + {eCores}E cores, hybrid={isHybrid}");
                        SetupCPUCoreConfigUI(pCores, eCores);
                    }
                }
            });
        }

        private void QuickSettingsProperty_Changed(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                UpdateQuickSettingsTileStates();
            });
        }

        private void LegionControllerBattery_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                UpdateLegionControllerBatteryDisplay();
                // Also refresh VID:PID in case it was missed during initial sync
                UpdateLegionControllerVidPidDisplay();
            });
        }

        private void UpdateLegionControllerBatteryDisplay()
        {
            try
            {
                // Update left controller battery
                int leftBattery = controllerBatteryLeft?.Value ?? -1;
                bool leftCharging = controllerChargingLeft?.Value ?? false;
                bool leftConnected = controllerConnectedLeft?.Value ?? false;

                if (leftBattery >= 0)
                {
                    LeftControllerBatteryText.Text = $"{leftBattery}%";
                    LeftControllerBatteryText.Foreground = GetBatteryBrush(leftBattery);
                }
                else
                {
                    LeftControllerBatteryText.Text = "N/A";
                    LeftControllerBatteryText.Foreground = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x88, 0x88, 0x88));
                }
                LeftControllerChargingIcon.Visibility = leftCharging ? Visibility.Visible : Visibility.Collapsed;
                LeftControllerConnectionText.Text = leftConnected ? "Attached" : "Detached";

                // Update right controller battery
                int rightBattery = controllerBatteryRight?.Value ?? -1;
                bool rightCharging = controllerChargingRight?.Value ?? false;
                bool rightConnected = controllerConnectedRight?.Value ?? false;

                if (rightBattery >= 0)
                {
                    RightControllerBatteryText.Text = $"{rightBattery}%";
                    RightControllerBatteryText.Foreground = GetBatteryBrush(rightBattery);
                }
                else
                {
                    RightControllerBatteryText.Text = "N/A";
                    RightControllerBatteryText.Foreground = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x88, 0x88, 0x88));
                }
                RightControllerChargingIcon.Visibility = rightCharging ? Visibility.Visible : Visibility.Collapsed;
                RightControllerConnectionText.Text = rightConnected ? "Attached" : "Detached";
            }
            catch (Exception ex)
            {
                Logger.Error($"Error updating Legion controller battery display: {ex.Message}");
            }
        }

        private Windows.UI.Xaml.Media.SolidColorBrush GetBatteryBrush(int percentage)
        {
            if (percentage <= 20)
                return new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xF4, 0x43, 0x36)); // Red
            else if (percentage <= 40)
                return new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xFF, 0x98, 0x00)); // Orange
            else
                return new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x4C, 0xAF, 0x50)); // Green
        }

        private void LegionControllerVidPid_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                UpdateLegionControllerVidPidDisplay();
            });
        }

        private void UpdateLegionControllerVidPidDisplay()
        {
            try
            {
                string vidPid = controllerVidPid?.Value ?? "";
                Logger.Info($"UpdateLegionControllerVidPidDisplay: vidPid='{vidPid}'");
                if (!string.IsNullOrEmpty(vidPid))
                {
                    LegionControllerPidVidText.Text = $"VID:PID {vidPid}";
                }
                else
                {
                    LegionControllerPidVidText.Text = "VID:PID --";
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error updating Legion controller VID:PID display: {ex.Message}");
            }
        }

        private void LegionControllerDeviceStatus_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            // Defensive: anything thrown by RunAsync (or from the lambda below escaping)
            // bubbles to the UWP runtime as 0xc000027b and terminates the process. Wrap
            // both layers and log every transition so we can see which step is failing.
            try
            {
                Logger.Info("LegionControllerDeviceStatus_PropertyChanged: enqueuing render");
                var dispatcher = Dispatcher;
                if (dispatcher == null)
                {
                    Logger.Warn("LegionControllerDeviceStatus_PropertyChanged: Dispatcher is null, skipping");
                    return;
                }
                _ = dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    try
                    {
                        UpdateLegionControllerDeviceStatusDisplay();
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"UpdateLegionControllerDeviceStatusDisplay (dispatcher lambda) crashed: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                    }
                });
            }
            catch (Exception ex)
            {
                Logger.Error($"LegionControllerDeviceStatus_PropertyChanged outer: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private void UpdateLegionControllerDeviceStatusDisplay()
        {
            try
            {
                Logger.Info("UpdateLegionControllerDeviceStatusDisplay: ENTER");
                string json = controllerDeviceStatus?.Value ?? "";
                if (string.IsNullOrEmpty(json))
                {
                    if (LegionControllerInfoSection != null)
                        LegionControllerInfoSection.Visibility = Visibility.Collapsed;
                    return;
                }

                if (!Windows.Data.Json.JsonObject.TryParse(json, out var root))
                {
                    Logger.Warn("ControllerDeviceStatus JSON failed to parse: " + json);
                    return;
                }

                string GetStr(string n) =>
                    (root.TryGetValue(n, out var v) && v.ValueType == Windows.Data.Json.JsonValueType.String)
                        ? v.GetString() ?? "" : "";
                int GetInt(string n) =>
                    (root.TryGetValue(n, out var v) && v.ValueType == Windows.Data.Json.JsonValueType.Number)
                        ? (int)v.GetNumber() : 0;
                bool GetBool(string n) =>
                    root.TryGetValue(n, out var v) && v.ValueType == Windows.Data.Json.JsonValueType.Boolean && v.GetBoolean();

                string fw = GetStr("fw");
                bool lightEnabled = GetBool("le");
                int lightMode = GetInt("lm");
                int r = GetInt("r"), g = GetInt("g"), b = GetInt("b");
                int brightness = GetInt("br");
                int speed = GetInt("sp");
                int vibration = GetInt("vb");
                bool touchpad = GetBool("tp");

                Logger.Info($"UpdateLegionControllerDeviceStatusDisplay: parsed fw={fw} le={lightEnabled} lm={lightMode} br={brightness} sp={speed} vb={vibration} tp={touchpad}");

                if (LegionControllerInfoSection != null)
                    LegionControllerInfoSection.Visibility = Visibility.Visible;
                if (LegionControllerFirmwareText != null)
                    LegionControllerFirmwareText.Text = string.IsNullOrEmpty(fw) ? "—" : fw;

                if (LegionControllerLightText != null)
                {
                    if (!lightEnabled)
                    {
                        LegionControllerLightText.Text = "Off";
                    }
                    else
                    {
                        LegionControllerLightText.Text = $"{LightModeLabel(lightMode)} · {brightness}% · speed {speed}%";
                    }
                }
                if (LegionControllerLightSwatch != null)
                {
                    LegionControllerLightSwatch.Visibility = lightEnabled ? Visibility.Visible : Visibility.Collapsed;
                    if (lightEnabled)
                    {
                        LegionControllerLightSwatch.Background = new Windows.UI.Xaml.Media.SolidColorBrush(
                            Windows.UI.Color.FromArgb(255, (byte)r, (byte)g, (byte)b));
                    }
                }

                if (LegionControllerVibrationText != null)
                    LegionControllerVibrationText.Text = VibrationLabel(vibration);
                if (LegionControllerTouchpadText != null)
                    LegionControllerTouchpadText.Text = touchpad ? "On" : "Off";

                Logger.Info("UpdateLegionControllerDeviceStatusDisplay: EXIT ok");
            }
            catch (Exception ex)
            {
                Logger.Error($"UpdateLegionControllerDeviceStatusDisplay error: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }
        }

        // Map raw firmware light-mode byte (b0:01 byte 12) to a label for the Info card.
        // Readback enum is 1-less than the setter's StickLightMode enum:
        // 0=Solid, 1=Pulse, 2=Dynamic, 3=Spiral. Off is signalled separately (see "le").
        private static string LightModeLabel(int raw)
        {
            switch (raw)
            {
                case 0: return "Solid";
                case 1: return "Pulse";
                case 2: return "Dynamic";
                case 3: return "Spiral";
                case 0xFF: return "Off";
                default: return $"Mode {raw}";
            }
        }

        // Per the device's b0:01 readback: 1=Off, 2=Weak, 3=Medium, 4=Strong.
        private static string VibrationLabel(int raw)
        {
            switch (raw)
            {
                case 1: return "Off";
                case 2: return "Weak";
                case 3: return "Medium";
                case 4: return "Strong";
                default: return $"Level {raw}";
            }
        }

        private void Resolution_PropertyChanged_OSD(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            // When resolution changes, reload OSD config for the new resolution and send to helper
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                Logger.Info($"Resolution changed to {resolution?.Value}, reloading OSD config");
                LoadOSDConfigFromStorage();
                SendOSDConfigToHelper();
            });
        }

        private void GamingWidget_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateCompactMode(e.NewSize.Width);
        }

        private void PowerSourceToggle_Changed(object sender, RoutedEventArgs e)
        {
            UpdateGameProfileCardVisibility();
        }

        private void PerGameProfileToggle_Changed(object sender, RoutedEventArgs e)
        {
            // Skip when toggle is being updated by helper pipe sync (not user interaction).
            // This prevents creating profiles for the wrong game when currentGameName
            // hasn't been updated yet (RunningGame pipe message may still be queued).
            // The helper already manages profile switching; OnGameTextChanged will update the UI.
            if (perGameProfile?.IsUpdatingUI == true)
            {
                Logger.Info($"Skipping PerGameProfileToggle_Changed - toggle set by helper sync (currentGameName='{currentGameName}')");
                SyncPowerSourceProfileToggleForCurrentContext();
                UpdateGameProfileCardVisibility();
                UpdateProfileDisplay();
                return;
            }

            // Protect entire toggle change sequence from auto-saves
            isSwitchingProfile = true;

            try
            {
                var settings = ApplicationData.Current.LocalSettings;

                if (PerGameProfileToggle.IsOn)
                {
                    // Per-game profiles enabled - only proceed if we have a valid game
                    if (HasValidGame(currentGameName))
                    {
                        // Clear the disabled preference since user is enabling it
                        string disabledKey = $"PerGameProfileDisabled_{currentGameName}";
                        if (settings.Values.ContainsKey(disabledKey))
                        {
                            settings.Values.Remove(disabledKey);
                            Logger.Info($"Cleared per-game profile disabled preference for {currentGameName}");
                        }
                        LoadOrCreateGameProfiles();

                        // Show notification for per-game profile
                        _ = ShowProfileNotificationAsync(currentGameName, isPerGameProfile: true);
                    }
                    else
                    {
                        // No valid game, turn toggle back off
                        Logger.Warn($"Cannot enable per-game profile without a valid game (currentGameName='{currentGameName}'), forcing toggle OFF");
                        PerGameProfileToggle.IsOn = false;
                        return;
                    }
                }
                else
                {
                    // Toggle is being turned OFF - check if we should reject this
                    // (Prevents helper messages from disabling auto-enabled toggle)
                    // But ALLOW internal disables (when game closes) and user-initiated toggles
                    if (HasValidGame(currentGameName) && isApplyingHelperUpdate && !isInternalToggleDisable && !isUserInitiatedProfileToggle)
                    {
                        bool hasExistingProfile = HasAnyGameProfile(currentGameName);

                        if (hasExistingProfile)
                        {
                            Logger.Info($"Ignoring helper request to disable toggle - game '{currentGameName}' has saved profile");
                            PerGameProfileToggle.IsOn = true; // Keep it on
                            return;
                        }
                    }

                    // Save user's preference to disable per-game profile for this game
                    // (only for valid games and user-initiated toggles, not internal/game-close disables)
                    if (HasValidGame(currentGameName) && !isInternalToggleDisable)
                    {
                        string disabledKey = $"PerGameProfileDisabled_{currentGameName}";
                        settings.Values[disabledKey] = true;
                        Logger.Info($"Saved per-game profile disabled preference for {currentGameName}");
                    }
                }

                SyncPowerSourceProfileToggleForCurrentContext();
                UpdateActiveProfileIndicator();
                // Refresh the active profile card so it hides immediately when the
                // user flips the toggle off — otherwise the card lingers showing
                // per-game default values that aren't actually being applied.
                UpdateGameProfileCardVisibility();
                UpdateProfileDisplay();
            }
            finally
            {
                isSwitchingProfile = false;
            }
        }

        private void OnGameTextChanged(DependencyObject sender, DependencyProperty dp)
        {
            string rawGameName = RunningGameText.Text;
            string sanitizedName = SanitizeGameName(rawGameName);

            // Validate the game name - if invalid, use empty string instead
            string newGameName = HasValidGame(sanitizedName) ? sanitizedName : "";

            if (newGameName != currentGameName)
            {
                Logger.Info($"Game changed from '{currentGameName}' to '{newGameName}' (raw: '{rawGameName}')");

                // IMPORTANT: Disable toggle BEFORE changing currentGameName
                // This prevents race condition where profile switching happens with invalid state
                if (!HasValidGame(newGameName) && PerGameProfileToggle?.IsOn == true)
                {
                    Logger.Info($"No valid game detected (was: '{rawGameName}'), disabling per-game toggle BEFORE updating game name");
                    isInternalToggleDisable = true; // Flag this as internal disable
                    PerGameProfileToggle.IsOn = false;  // This triggers UpdateActiveProfileIndicator which switches to Global
                    isInternalToggleDisable = false;

                    // Show notification for global profile revert
                    _ = ShowProfileNotificationAsync("", isPerGameProfile: false);
                }

                // When game closes (going from valid game to no game), refresh display settings
                // This ensures the resolution tile updates after a game that changed resolution exits
                if (HasValidGame(currentGameName) && !HasValidGame(newGameName))
                {
                    _ = RequestDisplaySettingsRefreshAsync();
                }

                // Now safe to update currentGameName
                currentGameName = newGameName;

                // Check if we have a valid game
                if (HasValidGame(currentGameName))
                {
                    // Valid game detected
                    var settings = ApplicationData.Current.LocalSettings;
                    bool hasExistingProfile = HasAnyGameProfile(currentGameName);

                    // Check if user explicitly disabled per-game profile for this game
                    string disabledKey = $"PerGameProfileDisabled_{currentGameName}";
                    bool userDisabledProfile = settings.Values.ContainsKey(disabledKey) && (bool)settings.Values[disabledKey];

                    // Auto-enable per-game toggle ONLY if profile already exists for this game
                    // Do NOT carry over toggle state from previous game - that causes unwanted profile creation
                    // Respect user's preference if they explicitly disabled it
                    if (hasExistingProfile && !userDisabledProfile)
                    {
                        if (!PerGameProfileToggle.IsOn)
                        {
                            Logger.Info($"Auto-enabling per-game profile for {currentGameName} (profile exists)");
                            PerGameProfileToggle.IsOn = true;  // This will trigger PerGameProfileToggle_Changed
                        }
                        else
                        {
                            // Already on, need to switch to new game's profile
                            // Protect this sequence from auto-saves
                            isSwitchingProfile = true;
                            try
                            {
                                LoadOrCreateGameProfiles();
                                UpdateActiveProfileIndicator();  // Critical: switch to the new game's profile!

                                // Show notification for per-game profile
                                _ = ShowProfileNotificationAsync(currentGameName, isPerGameProfile: true);
                            }
                            finally
                            {
                                isSwitchingProfile = false;
                            }
                        }
                    }
                    else if (PerGameProfileToggle?.IsOn == true)
                    {
                        // New game doesn't have a profile - turn off toggle to prevent auto-creation
                        Logger.Info($"Disabling per-game toggle for {currentGameName} (no existing profile)");
                        isInternalToggleDisable = true;
                        PerGameProfileToggle.IsOn = false;
                        isInternalToggleDisable = false;
                    }
                }

                SyncPowerSourceProfileToggleForCurrentContext();

                // Update game profile card visibility and display
                UpdateGameProfileCardVisibility();
                UpdateProfileDisplay();

                // Update Lossless Scaling Create Profile button state
                if (LosslessScalingCreateProfileButton != null)
                {
                    bool isInstalled = losslessScalingInstalled?.Value ?? false;
                    LosslessScalingCreateProfileButton.IsEnabled = isInstalled && HasValidGame(currentGameName);
                }

                // Update controller profile toggle and game name display
                UpdateControllerProfileForGameChange(newGameName);
            }
        }

        /// <summary>
        /// Shows a Game Bar notification when a profile is applied.
        /// </summary>
        private async Task ShowProfileNotificationAsync(string gameName, bool isPerGameProfile)
        {
            if (notificationManager == null || widget == null)
                return;

            try
            {
                // Check if notifications are enabled
                if (notificationManager.Setting == XboxGameBarWidgetNotificationSetting.DisabledByUser)
                {
                    Logger.Debug("Profile notifications disabled by user");
                    return;
                }

                string title = isPerGameProfile ? "Profile Applied" : "Global Profile";
                string content = isPerGameProfile
                    ? $"Loaded settings for {gameName}"
                    : "Reverted to global settings";

                var builder = new XboxGameBarWidgetNotificationBuilder(title)
                    .Content(content);

                var notification = builder.BuildNotification();
                var result = await notificationManager.TryShowAsync(notification);

                Logger.Info($"Profile notification shown: {title} - {content} (result: {result})");
            }
            catch (Exception ex)
            {
                Logger.Debug($"Failed to show profile notification: {ex.Message}");
            }
        }

        private void UpdateControllerProfileForGameChange(string newGameName)
        {
            // Update the game name display in controller profile card
            if (LegionControllerProfileGameText != null)
            {
                LegionControllerProfileGameText.Text = HasValidGame(newGameName) ? newGameName : "No game detected";
            }

            // Enable/disable the toggle based on game detection
            if (LegionControllerProfileToggle != null)
            {
                LegionControllerProfileToggle.IsEnabled = HasValidGame(newGameName);

                if (!HasValidGame(newGameName))
                {
                    // No valid game, turn off and disable toggle, switch to global profile
                    if (LegionControllerProfileToggle.IsOn)
                    {
                        isSwitchingControllerProfile = true;
                        try
                        {
                            LegionControllerProfileToggle.IsOn = false;
                            LoadControllerProfileFromStorage("Global", globalControllerProfile);
                            ApplyControllerProfile(globalControllerProfile);
                            Logger.Info("Game closed - switched to global controller profile");
                        }
                        finally
                        {
                            isSwitchingControllerProfile = false;
                        }
                    }
                }
                else
                {
                    // Valid game detected - check for existing controller profile
                    var settings = ApplicationData.Current.LocalSettings;
                    bool hasExistingControllerProfile = settings.Containers.ContainsKey($"ControllerProfile_Game_{newGameName}");

                    // Check if user explicitly disabled controller profile for this game
                    string disabledKey = $"ControllerProfileDisabled_{newGameName}";
                    bool userDisabledProfile = settings.Values.ContainsKey(disabledKey) && (bool)settings.Values[disabledKey];

                    // Auto-enable if profile exists and user hasn't disabled it
                    if (hasExistingControllerProfile && !userDisabledProfile)
                    {
                        if (!LegionControllerProfileToggle.IsOn)
                        {
                            // Setting toggle to true triggers LegionControllerProfileToggle_Toggled
                            // which handles loading and applying the profile
                            LegionControllerProfileToggle.IsOn = true;
                            Logger.Info($"Auto-enabled controller profile for {newGameName}");
                        }
                        else
                        {
                            // Toggle already on, need to switch to new game's profile
                            isSwitchingControllerProfile = true;
                            try
                            {
                                LoadControllerProfileFromStorage($"Game_{newGameName}", gameControllerProfile);
                                ApplyControllerProfile(gameControllerProfile);
                                Logger.Info($"Switched to controller profile for {newGameName}");
                            }
                            finally
                            {
                                isSwitchingControllerProfile = false;
                            }
                        }
                    }
                    else if (LegionControllerProfileToggle.IsOn)
                    {
                        // Toggle was on for previous game - only keep it on if new game has existing profile
                        if (!hasExistingControllerProfile)
                        {
                            // No profile for new game - turn off toggle instead of auto-creating
                            // Setting toggle to false triggers LegionControllerProfileToggle_Toggled
                            // which handles switching to global profile
                            LegionControllerProfileToggle.IsOn = false;
                            Logger.Info($"Disabled controller profile toggle for {newGameName} (no existing profile)");
                        }
                        else
                        {
                            // Profile exists, switch to it
                            isSwitchingControllerProfile = true;
                            try
                            {
                                LoadControllerProfileFromStorage($"Game_{newGameName}", gameControllerProfile);
                                ApplyControllerProfile(gameControllerProfile);
                                Logger.Info($"Switched to controller profile for {newGameName}");
                            }
                            finally
                            {
                                isSwitchingControllerProfile = false;
                            }
                        }
                    }
                }
            }
        }

        private void UpdateActiveProfileIndicator()
        {
            bool hasGame = HasValidGame(currentGameName);
            bool perGameEnabled = PerGameProfileToggle?.IsOn ?? false;

            // Check if we should use per-game profiles
            if (perGameEnabled && hasGame)
            {
                // Per-game profile is active
                if (GetPerGamePowerSourceProfileEnabled(currentGameName))
                {
                    // Check power status for AC/DC
                    // Only consider DC (battery) when power supply is NotPresent (actually unplugged)
                    // Inadequate means charger is connected but can't keep up - still treat as AC
                    var powerSupplyStatus = PowerManager.PowerSupplyStatus;
                    bool isOnAC = powerSupplyStatus != PowerSupplyStatus.NotPresent;

                    if (isOnAC)
                    {
                        ActiveProfileText.Text = $"{currentGameName} (AC)";
                    }
                    else
                    {
                        ActiveProfileText.Text = $"{currentGameName} (DC)";
                    }
                }
                else
                {
                    // Game profile without power source split
                    ActiveProfileText.Text = currentGameName;
                }
            }
            else
            {
                // Global profiles
                if (!GetGlobalPowerSourceProfileEnabled())
                {
                    // Power source profiles disabled, show global
                    ActiveProfileText.Text = "Global Settings";
                }
                else
                {
                    // Check power status
                    // Only consider DC (battery) when power supply is NotPresent (actually unplugged)
                    // Inadequate means charger is connected but can't keep up - still treat as AC
                    var powerSupplyStatus = PowerManager.PowerSupplyStatus;
                    var remainingCharge = PowerManager.RemainingChargePercent;

                    Logger.Info($"Power status - PowerSupply: {powerSupplyStatus}, Charge: {remainingCharge}%");

                    bool isOnAC = powerSupplyStatus != PowerSupplyStatus.NotPresent;

                    if (isOnAC)
                    {
                        ActiveProfileText.Text = "AC Profile (Plugged In)";
                    }
                    else
                    {
                        ActiveProfileText.Text = "DC Profile (Battery)";
                    }
                }
            }

            Logger.Info($"Active profile updated to: {ActiveProfileText.Text}");

            // Start scrolling animation if text is too long
            UpdateActiveProfileScrollAnimation();

            // Switch profile if needed
            SwitchProfile();
        }

        // Marquee storyboards for header text fields. Cached so successive calls can
        // stop the previous animation cleanly before starting a new one (text changed /
        // canvas resized). Keyed by Canvas so multiple call sites (active profile name,
        // detected-game name) can share the helper without trampling each other.
        private readonly Dictionary<Canvas, Storyboard> activeMarqueeStoryboards = new Dictionary<Canvas, Storyboard>();
        // Tracks which canvases we've already wired SizeChanged on so we don't
        // double-subscribe across repeated Update calls.
        private readonly HashSet<Canvas> marqueeSizeChangedHooked = new HashSet<Canvas>();

        /// <summary>
        /// Marquee-scrolls a TextBlock inside a Canvas when the rendered text width
        /// exceeds the canvas slot width. Mirrors the Quick Settings tile scroll
        /// behavior at GamingWidget.QuickSettings.TileStates.cs:206 — same keyframe
        /// shape, NaN guards, and 30 px/sec speed. When the text fits, just
        /// left-aligns it. Idempotent: safe to call repeatedly when the text or
        /// canvas size changes.
        /// </summary>
        private void UpdateMarqueeScrollAnimation(TextBlock text, Canvas canvas, TranslateTransform transform)
        {
            if (text == null || canvas == null || transform == null) return;

            // First call wires SizeChanged so the clip rect tracks the laid-out
            // canvas width and the marquee re-evaluates when the column changes.
            if (!marqueeSizeChangedHooked.Contains(canvas))
            {
                marqueeSizeChangedHooked.Add(canvas);
                canvas.SizeChanged += (s, e) =>
                {
                    if (e.NewSize.Width > 0 && canvas != null)
                    {
                        canvas.Clip = new Windows.UI.Xaml.Media.RectangleGeometry
                        {
                            Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, canvas.Height)
                        };
                        UpdateMarqueeScrollAnimation(text, canvas, transform);
                    }
                };
            }

            // Stop any existing animation for this canvas before recomputing.
            if (activeMarqueeStoryboards.TryGetValue(canvas, out var existing) && existing != null)
            {
                existing.Stop();
                activeMarqueeStoryboards.Remove(canvas);
            }
            transform.X = 0;

            // Measure text at its natural size.
            text.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            double textWidth = text.DesiredSize.Width;

            // Prefer ActualWidth, fall back to declared Width. Both can be NaN
            // (XAML default) before first layout, so use `!(x > 0)` to reject NaN
            // — the same guard pattern from build 2061 that fixed the TimeSpan-NaN
            // throw in UpdateTileScrollAnimation.
            double canvasWidth = canvas.ActualWidth;
            if (!(canvasWidth > 0)) canvasWidth = canvas.Width;
            if (!(canvasWidth > 0)) return;
            if (!(textWidth >= 0) || double.IsInfinity(textWidth)) return;

            // Fits: just left-align (matches the original TextBlock layout).
            if (textWidth <= canvasWidth)
            {
                Canvas.SetLeft(text, 0);
                return;
            }

            // Doesn't fit: scroll left → pause → scroll right → pause → repeat.
            Canvas.SetLeft(text, 0);
            double scrollDistance = textWidth - canvasWidth + 10;
            const double scrollSpeed = 30; // pixels per second
            double scrollDuration = scrollDistance / scrollSpeed;

            var storyboard = new Storyboard();
            var animation = new DoubleAnimationUsingKeyFrames
            {
                RepeatBehavior = RepeatBehavior.Forever
            };
            animation.KeyFrames.Add(new DiscreteDoubleKeyFrame
            {
                KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero),
                Value = 0
            });
            animation.KeyFrames.Add(new DiscreteDoubleKeyFrame
            {
                KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.5)),
                Value = 0
            });
            animation.KeyFrames.Add(new LinearDoubleKeyFrame
            {
                KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.5 + scrollDuration)),
                Value = -scrollDistance
            });
            animation.KeyFrames.Add(new DiscreteDoubleKeyFrame
            {
                KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(3 + scrollDuration)),
                Value = -scrollDistance
            });
            animation.KeyFrames.Add(new LinearDoubleKeyFrame
            {
                KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(3 + scrollDuration * 2)),
                Value = 0
            });
            animation.KeyFrames.Add(new DiscreteDoubleKeyFrame
            {
                KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(4.5 + scrollDuration * 2)),
                Value = 0
            });
            Storyboard.SetTarget(animation, transform);
            Storyboard.SetTargetProperty(animation, "X");
            storyboard.Children.Add(animation);

            activeMarqueeStoryboards[canvas] = storyboard;
            storyboard.Begin();
        }

        /// <summary>
        /// Marquees the active profile name. Called from UpdateActiveProfileIndicator
        /// every time the displayed profile / game name changes.
        /// </summary>
        private void UpdateActiveProfileScrollAnimation()
        {
            UpdateMarqueeScrollAnimation(ActiveProfileText, ActiveProfileTextCanvas, ActiveProfileTextTransform);
        }

        /// <summary>
        /// Marquees the detected-game name shown next to the per-game-profile toggle on
        /// the Performance tab. Called from the RunningGameProperty's game-detection
        /// callback so the scroll evaluates whenever the displayed game name changes.
        /// </summary>
        private void UpdateDetectedGameScrollAnimation()
        {
            UpdateMarqueeScrollAnimation(DetectedGameText, DetectedGameTextCanvas, DetectedGameTextTransform);
        }

        private void AddTextBlock(Grid grid, int row, int column, string text, int fontSize, string colorHex,
            Thickness? margin = null, HorizontalAlignment horizontalAlignment = HorizontalAlignment.Left)
        {
            var textBlock = new TextBlock
            {
                Text = text,
                FontSize = fontSize,
                Foreground = new SolidColorBrush(ParseColor(colorHex)),
                Margin = margin ?? new Thickness(0),
                HorizontalAlignment = horizontalAlignment
            };
            Grid.SetRow(textBlock, row);
            Grid.SetColumn(textBlock, column);
            grid.Children.Add(textBlock);
        }

        private Windows.UI.Color ParseColor(string hex)
        {
            hex = hex.TrimStart('#');
            if (hex.Length == 6)
            {
                return Windows.UI.Color.FromArgb(
                    255,
                    Convert.ToByte(hex.Substring(0, 2), 16),
                    Convert.ToByte(hex.Substring(2, 2), 16),
                    Convert.ToByte(hex.Substring(4, 2), 16)
                );
            }
            return Windows.UI.Colors.White;
        }

        protected async override void OnNavigatedTo(NavigationEventArgs e)
        {
            Logger.Info("=== OnNavigatedTo START ===");
            Logger.Info($"Parameter type: {e.Parameter?.GetType().FullName ?? "null"}");
            Logger.Info($"Current state - Widget is null: {widget == null}, WidgetActivity is null: {widgetActivity == null}, Pipe connected: {App.IsConnected}");

            base.OnNavigatedTo(e);

            // Show loading banner immediately to give user feedback during initialization
            ShowConnectionBanner(BannerState.Loading);

            // Yield to UI thread to allow banner to render before async operations block
            await Task.Yield();

            // Register this instance as the active widget to handle AppService messages
            Logger.Info("Registering this GamingWidget instance as the active widget.");
            App.RegisterActiveGamingWidget(this);
            Logger.Info("GamingWidget instance registered as active.");

            //while (!System.Diagnostics.Debugger.IsAttached)
            //{
            //    await Task.Delay(500);
            //}

            Logger.Info("Creating theme brushes...");
            widgetDarkThemeBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 37, 40, 44));
            widgetLightThemeBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255));

            // Load saved theme preference
            LoadThemeSetting();

            // Only update widget if new parameter is an XboxGameBarWidget, preserve existing otherwise
            if (e.Parameter is XboxGameBarWidget newWidget)
            {
                widget = newWidget;
            }

            if (widget != null)
            {
                Logger.Info($"Running as a Xbox Game Bar widget. Widget type: {widget.GetType().FullName}");

                // Update max window size at runtime to override any cached manifest values
                widget.MaxWindowSize = new Windows.Foundation.Size(900, 1080);

                Logger.Info("Calling widget.CenterWindowAsync()...");
                await widget.CenterWindowAsync();
                Logger.Info("widget.CenterWindowAsync() completed.");

                Logger.Info("Registering widget event handlers (RequestedThemeChanged, SettingsClicked, VisibleChanged, GameBarDisplayModeChanged)...");
                widget.RequestedThemeChanged += GamingWidget_RequestedThemeChanged;
                widget.SettingsClicked += GamingWidget_SettingsClicked;
                widget.VisibleChanged += GamingWidget_VisibleChanged;
                widget.GameBarDisplayModeChanged += GamingWidget_GameBarDisplayModeChanged;
                Logger.Info("Widget event handlers registered.");

                UpdateGameBarForegroundSignal("OnNavigatedTo");

                // Initialize notification manager for profile notifications
                notificationManager = new XboxGameBarWidgetNotificationManager(widget);
                Logger.Info("Widget notification manager initialized.");

                // Create widget activity if we have a widget but no activity yet
                if (widgetActivity == null)
                {
                    Logger.Info("Widget is available but activity not created yet. Creating now.");
                    await CreateWidgetActivity();
                }
                else
                {
                    Logger.Info($"WidgetActivity already exists, skipping creation.");
                }

                // Create app target tracker if not already created
                if (appTargetTracker == null)
                {
                    Logger.Info("AppTargetTracker is null, creating now.");
                    await CreateAppTargetTracker();
                }
                else
                {
                    Logger.Info("AppTargetTracker already exists, skipping creation.");
                }

                // Initialize hotkey watchers for controller button combos
                InitializeHotkeyWatchers();
            }
            else
            {
                Logger.Info("XboxGameBarWidget not available, probably running as an app instead of widget.");
            }

            if (!App.IsConnected && ApiInformation.IsApiContractPresent("Windows.ApplicationModel.FullTrustAppContract", 1, 0))
            {
                Logger.Info("Not connected to helper. Launching full trust process.");

                // Launch helper with guards (checks heartbeat, enforces rate limiting)
                await LaunchHelperWithGuardsAsync("OnNavigatedTo - initial connection");
            }
            else
            {
                Logger.Info($"Not launching full trust process. App.IsConnected: {App.IsConnected}, FullTrustAppContract present: {ApiInformation.IsApiContractPresent("Windows.ApplicationModel.FullTrustAppContract", 1, 0)}");

                // If connection already exists, sync properties
                if (App.IsConnected)
                {
                    Logger.Info("Already connected to helper via pipe.");

                    // Hide connection status banner since we're connected
                    HideConnectionBanner();
                    Logger.Info("Connection status banner hidden - already connected.");

                    // Sync properties since we're already connected
                    // The helper is the source of truth for hardware state (TDP mode, etc.)
                    Logger.Info("Syncing properties with helper since connection already exists...");
                    try
                    {
                        isApplyingHelperUpdate = true;

                        // Accept helper's LegionPerformanceMode as-is - helper is source of truth

                        // Skip OS Power Mode sync if profile has it saved
                        // This prevents sync from overwriting profile-loaded OS Power Mode with hardware state
                        if (SaveOSPowerMode && osPowerMode != null)
                        {
                            osPowerMode.SkipSync = true;
                            Logger.Info("OSPowerMode sync will be skipped - profile has OS Power Mode saved");
                        }

                        await properties.Sync();
                        Logger.Info("Property sync completed.");

                        // Update Legion Controller battery display after sync (values come from helper)
                        UpdateLegionControllerBatteryDisplay();
                        UpdateLegionControllerVidPidDisplay();

                        // Update FPS Limit controls based on RTSS installed status
                        UpdateFPSLimitControls();

                        // Register Chill FPS handlers after first sync to prevent crash
                        RegisterChillFPSHandlers();
                    }
                    finally
                    {
                        isApplyingHelperUpdate = false;
                    }

                    // Stop any pending slider updates from the sync
                    properties.StopPendingUpdates();

                    // Re-enable OS Power Mode sync for future syncs
                    if (osPowerMode != null)
                    {
                        osPowerMode.SkipSync = false;
                    }

                    // Don't apply profile TDP mode to helper - helper is source of truth
                    // Helper already has correct mode from its own profile

                    // Sync TDPModeComboBox with helper's LegionPerformanceMode
                    if (legionGoDetected?.Value == true && LegionPerformanceModeComboBox != null && TDPModeComboBox != null)
                    {
                        if (TDPModeComboBox.SelectedIndex != LegionPerformanceModeComboBox.SelectedIndex)
                        {
                            Logger.Info($"Syncing TDPModeComboBox to match helper's mode: index {LegionPerformanceModeComboBox.SelectedIndex}");
                            lastTDPModeIndex = LegionPerformanceModeComboBox.SelectedIndex;
                            TDPModeComboBox.SelectedIndex = LegionPerformanceModeComboBox.SelectedIndex;
                        }

                        // Initialize savedCustomTDP from helper's synced TDP value
                        if (IsCustomTdpModeIndex(LegionPerformanceModeComboBox.SelectedIndex) && tdp != null)
                        {
                            savedCustomTDP = tdp.Value;
                            Logger.Info($"Initialized savedCustomTDP from helper's TDP: {savedCustomTDP}W");
                        }
                    }

                    // Update TDP slider to show helper's actual TDP value
                    if (tdp != null && TDPSlider != null)
                    {
                        double helperTDP = tdp.Value;
                        if (Math.Abs(TDPSlider.Value - helperTDP) > 0.5)
                        {
                            Logger.Info($"Updating TDP slider from stale {TDPSlider.Value}W to helper's {helperTDP}W");
                            TDPSlider.Value = helperTDP;
                        }
                    }

                    // Update TDP display text and enabled state based on current mode
                    UpdateTDPSliderEnabledState();
                    Logger.Info($"Updated TDP slider enabled state after sync");

                    // Update profile display now that legionGoDetected has been synced from helper
                    // This ensures TDP Mode shows in Profiles tab on fresh start
                    UpdateProfileDisplay();
                    RefreshLegionEnhancedRemapUi();
                    Logger.Info("Profile display updated after sync - legionGoDetected=" + (legionGoDetected?.Value.ToString() ?? "null"));

                    // Clear initial sync flag - profile is loaded and applied, user changes should now save
                    // Add a small delay to let any pending ValueChanged events settle first
                    await Task.Delay(200);
                    isInitialSync = false;
                    Logger.Info("Initial sync complete - profile saves are now enabled");
                }
            }

            // Auto-check for updates on startup (if enabled)
            _ = CheckForUpdatesOnStartupAsync();

            Logger.Info("=== OnNavigatedTo END ===");
        }
        private void AppTargetTracker_TargetChanged(XboxGameBarAppTargetTracker sender, object args)
        {
            var settingEnabled = appTargetTracker.Setting == XboxGameBarAppTargetSetting.Enabled;

            XboxGameBarAppTarget target = null;
            if (settingEnabled)
            {
                target = appTargetTracker.GetTarget();
            }

            if (target == null)
            {
                Logger.Info("Found no target.");
                trackedGame.SetValue(new TrackedGame());
            }
            else
            {
                if (target.IsGame && !BlackListAppTrackerNames.Contains(target.DisplayName))
                {
                    Logger.Info($"Tracked game DisplayName={target.DisplayName} AumId={target.AumId} TitleId={target.TitleId} IsFullscreen={target.IsFullscreen}");
                    trackedGame.SetValue(new TrackedGame(target.AumId, target.DisplayName, StringHelper.CleanStringForSerialization(target.TitleId), target.IsFullscreen));
                }
                else
                {
                    Logger.Info($"Tracked non-game DisplayName={target.DisplayName} AumId={target.AumId} TitleId={target.TitleId} IsFullscreen={target.IsFullscreen}");
                    trackedGame.SetValue(new TrackedGame());
                }
            }
        }

        /// <summary>
        /// Banner state types for different connection status messages.
        /// </summary>
        private enum BannerState
        {
            /// <summary>Red banner - Not connected to helper (error state)</summary>
            Disconnected,
            /// <summary>Blue banner - Syncing properties (info state)</summary>
            Syncing,
            /// <summary>Orange banner - Reconnecting to helper (warning state)</summary>
            Reconnecting,
            /// <summary>Blue banner - Launching helper process (info state)</summary>
            Launching,
            /// <summary>Blue banner - Loading widget (initial state)</summary>
            Loading,
            /// <summary>Purple banner - Initial setup in progress (requires UAC)</summary>
            InitialSetup,
            /// <summary>Blue banner - Upgrading helper (UAC-free upgrade in progress)</summary>
            Upgrading
        }

        private void UpdateTextBlockStyles(DependencyObject parent)
        {
            if (parent == null) return;

            int childCount = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < childCount; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);

                if (child is TextBlock textBlock)
                {
                    // Update font size based on current style and compact mode
                    if (textBlock.Style != null)
                    {
                        string styleKey = textBlock.Style.TargetType == typeof(TextBlock) ? GetStyleKey(textBlock) : null;
                        if (styleKey != null)
                        {
                            string compactStyleKey = isCompactMode ? styleKey + "Compact" : styleKey;
                            if (Resources.ContainsKey(compactStyleKey))
                            {
                                textBlock.Style = Resources[compactStyleKey] as Style;
                            }
                        }
                    }
                }

                // Recurse through visual tree
                UpdateTextBlockStyles(child);
            }
        }

        private string GetStyleKey(TextBlock textBlock)
        {
            // Determine which style is currently applied
            if (textBlock.Style == Resources["CardTitleStyle"] as Style ||
                textBlock.Style == Resources["CardTitleStyleCompact"] as Style)
                return "CardTitleStyle";
            if (textBlock.Style == Resources["CardCaptionStyle"] as Style ||
                textBlock.Style == Resources["CardCaptionStyleCompact"] as Style)
                return "CardCaptionStyle";
            if (textBlock.Style == Resources["CardValueStyle"] as Style ||
                textBlock.Style == Resources["CardValueStyleCompact"] as Style)
                return "CardValueStyle";
            return null;
        }

        /// <summary>
        /// Focus this widget using XboxGameBarWidgetControl API.
        /// Called when helper sends Labs_FocusWidget command.
        /// </summary>
        private async Task FocusThisWidgetAsync()
        {
            try
            {
                if (widget != null)
                {
                    // Create widget control and activate this widget
                    var widgetControl = new XboxGameBarWidgetControl(widget);
                    await widgetControl.ActivateAsync("GamingWidget");
                    Logger.Info("Widget focused successfully via XboxGameBarWidgetControl");
                }
                else
                {
                    Logger.Warn("Cannot focus widget - widget object is null");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to focus widget: {ex.Message}");
            }
        }

        /// <summary>
        /// Called when pipe connection is established.
        /// </summary>
        private async Task OnPipeConnectedAsync()
        {
            Logger.Info("=== OnPipeConnectedAsync START ===");
            Logger.Info($"[PIPE] OnPipeConnectedAsync called. Widget hash: {this.GetHashCode()}, App.IsConnected: {App.IsConnected}");

            // Stop reconnection timeout timer - connection established
            StopReconnectionTimeoutTimer();
            // Stop the post-give-up heartbeat watcher too; we're connected now.
            StopHeartbeatWatcher();

            // Clear the "helper disconnected with EC override active" warning now that
            // the pipe is back. Helper restart releases 0xC6C8 via ProcessExit hook + the
            // fresh EC tick on reconnect, so the fan will resume curve-following shortly.
            if (FanCurveHelperDisconnectedWarning != null)
            {
                FanCurveHelperDisconnectedWarning.Visibility = Windows.UI.Xaml.Visibility.Collapsed;
            }

            // Verify we connected to the correct helper version by reading heartbeat
            // This prevents issues during upgrades where we connect to the old dying helper
            try
            {
                var localFolder = Windows.Storage.ApplicationData.Current.LocalFolder;
                var heartbeatFile = await localFolder.TryGetItemAsync("helper_heartbeat.json");

                if (heartbeatFile != null)
                {
                    string content = await Windows.Storage.FileIO.ReadTextAsync((Windows.Storage.StorageFile)heartbeatFile);
                    var versionMatch = System.Text.RegularExpressions.Regex.Match(content, @"""version"":""([^""]+)""");

                    if (versionMatch.Success)
                    {
                        string helperVersion = versionMatch.Groups[1].Value;
                        string widgetVersion = GetWidgetVersion();

                        if (helperVersion != widgetVersion)
                        {
                            Logger.Warn($"Connected to wrong helper version: helper={helperVersion}, widget={widgetVersion} - disconnecting and retrying");

                            // Disconnect and trigger reconnection
                            App.PipeClient?.Dispose();

                            // Wait a bit for the old helper to finish exiting
                            await Task.Delay(3000);

                            // Re-check state after the delay: overlapping invocations of this
                            // branch (one per OnPipeConnectedAsync hit on the dying helper) each
                            // schedule a deferred Upgrading banner. If a later attempt has since
                            // reached a correct-version helper, bail out — otherwise we'd re-show
                            // "Upgrading" and then TryConnectPipeAsync returns "Pipe already
                            // connected" without firing OnPipeConnectedAsync again, leaving the
                            // banner stuck until restart.
                            if (App.PipeClient?.IsConnected == true)
                            {
                                bool currentHelperMatches = await IsConnectedHelperVersionCurrentAsync();
                                if (currentHelperMatches)
                                {
                                    Logger.Info("Version-mismatch retry aborted: already connected to correct-version helper");
                                    await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                                    {
                                        HideConnectionBanner();
                                    });
                                    return;
                                }
                            }

                            // Show reconnecting banner and retry
                            await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                            {
                                ShowConnectionBanner(BannerState.Upgrading);
                            });

                            // Retry connection
                            _ = TryConnectPipeAsync();
                            return;
                        }

                        Logger.Info($"Connected to correct helper version: {helperVersion}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"Version check failed (may be expected during startup): {ex.Message}");
                // Continue anyway - version check is optional safety measure
            }

            // Register as active widget
            App.RegisterActiveGamingWidget(this);
            Logger.Info("Registered as active widget for pipe communication");

            // Hide the connection banner early - the pipe is connected and UI is already visible.
            // Property sync happens in the background; no need to show "Reconnecting" during it.
            await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
            {
                HideConnectionBanner();
            });

            // Create widget activity and app target tracker if widget is available
            if (widget != null)
            {
                await CreateWidgetActivity();
                await CreateAppTargetTracker();
            }

            // Sync properties now that pipe is connected
            // The helper is the source of truth for hardware state (TDP mode, etc.)
            // Always accept the helper's current LegionPerformanceMode during sync
            // Using App.HasEverConnectedToHelper (static) so it persists across widget instance recreations
            bool isReconnection = App.HasEverConnectedToHelper;
            Logger.Info($"Starting property sync via pipe... (isReconnection={isReconnection})");
            try
            {
                isApplyingHelperUpdate = true;

                // Never suppress LegionPerformanceMode - always accept helper's current TDP mode
                // The helper loads the correct mode from its profile on startup

                await properties.Sync();
                Logger.Info("Property sync via pipe completed successfully.");

                RegisterChillFPSHandlers();
            }
            catch (Exception ex)
            {
                Logger.Error($"Error during property sync via pipe: {ex}");
            }
            finally
            {
                isApplyingHelperUpdate = false;
            }

            try
            {
                Logger.Info("[PIPE] Starting post-sync initialization...");
                await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                {
                    properties.StopPendingUpdates();
                });

                Logger.Info("[PIPE] Sending OSD config to helper...");
                SendOSDConfigToHelper();

                // Don't apply profile TDP mode to helper - the helper is the source of truth
                // It already has the correct mode from its own profile (global.xml)
                Logger.Info("[PIPE] Skipping profile TDP mode apply - helper is source of truth");

                // Sync TDPModeComboBox with helper's LegionPerformanceMode
                // This is needed because LegionPerformanceModeComboBox_SelectionChanged skips
                // TDPModeComboBox sync during isInitialSync
                await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                {
                    if (LegionPerformanceModeComboBox != null && TDPModeComboBox != null)
                    {
                        if (legionGoDetected?.Value == true)
                        {
                            if (TDPModeComboBox.SelectedIndex != LegionPerformanceModeComboBox.SelectedIndex)
                            {
                                Logger.Info($"[PIPE] Syncing TDPModeComboBox to match helper's mode: index {LegionPerformanceModeComboBox.SelectedIndex}");
                                lastTDPModeIndex = LegionPerformanceModeComboBox.SelectedIndex;
                                TDPModeComboBox.SelectedIndex = LegionPerformanceModeComboBox.SelectedIndex;
                            }
                            else
                            {
                                // Always sync lastTDPModeIndex even when indices already match
                                // to prevent stale values from a previous session
                                lastTDPModeIndex = TDPModeComboBox.SelectedIndex;
                            }

                            // Initialize savedCustomTDP from helper's synced TDP value
                            // This prevents stale savedCustomTDP (default 15W) from overriding
                            // the helper's actual TDP when TDPModeComboBox_SelectionChanged fires
                            if (IsCustomTdpModeIndex(LegionPerformanceModeComboBox.SelectedIndex) && tdp != null)
                            {
                                savedCustomTDP = tdp.Value;
                                Logger.Info($"[PIPE] Initialized savedCustomTDP from helper's TDP: {savedCustomTDP}W");
                            }
                        }
                        else
                        {
                            // Generic device: sync lastTDPModeIndex from current ComboBox state
                            // to prevent stale values from causing the first mode change to be skipped
                            lastTDPModeIndex = TDPModeComboBox.SelectedIndex;
                            Logger.Info($"[PIPE] Synced lastTDPModeIndex for generic device: {lastTDPModeIndex}");
                        }
                    }

                    // Update the TDP slider to show helper's actual TDP value
                    // The slider may have been set to a stale profile value during LoadProfileSettings
                    if (tdp != null && TDPSlider != null)
                    {
                        double helperTDP = tdp.Value;
                        if (Math.Abs(TDPSlider.Value - helperTDP) > 0.5)
                        {
                            Logger.Info($"[PIPE] Updating TDP slider from stale {TDPSlider.Value}W to helper's {helperTDP}W");
                            TDPSlider.Value = helperTDP;
                        }
                    }

                    // Update TDP display text and enabled state based on current mode
                    // Without this, TDPValueText/CurrentTDPValueText show stale "Balanced mode" from XAML defaults
                    UpdateTDPSliderEnabledState();
                    Logger.Info($"[PIPE] Updated TDP slider enabled state after sync");
                });

                // Send Quick Metrics and Screen Saver enabled states to helper (fire-and-forget)
                Logger.Info("[PIPE] Sending Quick Metrics and Screen Saver enabled states to helper...");
                SendQuickMetricsEnabledToHelper();
                SendScreenSaverEnabledToHelper();
                SendProfileSaveFlagsToHelper();
                SendPowerSourceProfileConfigToHelper();
                SendPowerSourceProfileValuesToHelper();
                // Without this, the helper's ControllerHotkeyMonitor only learns the
                // widget's View+ABXY / Menu+DPad bindings on the next dropdown change.
                // Before that, helper-side detection sits at the default-all-disabled
                // state — which is fine in desktop mode (the widget's own watcher
                // fires) but breaks every hotkey in FSE where the widget is suspended
                // and only the helper is polling XInput. Issue #79 (kingvall).
                SendControllerHotkeyConfigToHelper();

                // Re-send the persisted Auto Hibernate AC/DC mode now that the pipe is up. At
                // Loaded time (when LoadAutoHibernateModeSetting first runs) the pipe isn't
                // connected, so that send is dropped; this guarantees the helper agrees with the
                // widget's saved choice. (Issue #88 bug #3)
                SendAutoHibernateModeToHelper();

                await Task.Delay(200);
                isInitialSync = false;
                App.HasEverConnectedToHelper = true;
                Logger.Info("Initial sync via pipe complete - profile saves are now enabled");

                // Populate the GoTweaks lighting + haptics controls from the helper's persisted
                // config (fire-and-forget; guards its own sends during the programmatic fill).
                SyncGoTweaksFeaturesFromHelper();

                Logger.Info("[PIPE] About to hide connection banner and update profile display...");
                await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                {
                    Logger.Info("[PIPE] Inside dispatcher - calling HideConnectionBanner()");
                    HideConnectionBanner();

                    // Update current profile's TDP and mode from helper's synced values
                    // This prevents stale profile values from being loaded in subsequent LoadProfileSettings calls
                    if (tdp != null && !string.IsNullOrEmpty(currentProfileName))
                    {
                        var profile = GetProfile(currentProfileName);
                        if (profile != null && legionPerformanceMode != null)
                        {
                            int helperMode = legionPerformanceMode.Value;
                            double helperTDP = tdp.Value;
                            if (profile.LegionPerformanceMode != helperMode || Math.Abs(profile.TDP - helperTDP) > 0.5)
                            {
                                Logger.Info($"[PIPE] Syncing profile '{currentProfileName}' with helper: TDP {profile.TDP}→{helperTDP}W, Mode {profile.LegionPerformanceMode}→{helperMode}");
                                profile.LegionPerformanceMode = helperMode;
                                profile.TDP = helperTDP;
                                SaveProfileToStorage(currentProfileName, profile);
                            }
                        }
                    }

                    Logger.Info("[PIPE] Inside dispatcher - calling UpdateProfileDisplay()");
                    UpdateProfileDisplay();
                    RefreshLegionEnhancedRemapUi();

                    // Recover from the cold-start race: ApplyControllerProfile fires during
                    // widget constructor before App.IsConnected, so its Send* calls drop
                    // into a not-yet-connected pipe. Re-push now that the pipe is up.
                    ResendActiveControllerProfileToHelper();

                    // Refresh USBIP prereq status line. PropertyChanged-driven refresh misses
                    // the case where the helper's value matches the widget's default (false),
                    // since GenericProperty.SetValue skips NotifyPropertyChanged on equality
                    // (see MEMORY.md GenericProperty.SetValue Equality Check Gotcha). Without
                    // this explicit call, users on a fresh install where USBIP isn't installed
                    // would see "checking..." forever.
                    UpdateUsbipCardVisibility();

                    // Same equality-skip gotcha applies: if helper's viiperDeviceType matches
                    // the widget's default ("xbox360"), no PropertyChanged fires and the Gyro →
                    // Right Stick section never appears. Call once explicitly so the section
                    // shows on first connect when applicable.
                    UpdateViiperStickGyroSectionVisibility();

                    Logger.Info("[PIPE] Inside dispatcher - post-sync UI updates complete");
                });
                Logger.Info("[PIPE] Post-sync initialization complete");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in post-sync initialization via pipe: {ex}");
                Logger.Error($"[PIPE] Stack trace: {ex.StackTrace}");
                // Show error banner so user knows something went wrong
                await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                {
                    ShowConnectionBanner(BannerState.Disconnected);
                });
            }

            Logger.Info("=== OnPipeConnectedAsync END ===");
        }
    }
}
