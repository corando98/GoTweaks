using NLog;
using Shared.Utilities;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Xml.Serialization;

namespace Shared.Data
{
    [XmlRoot("GameProfile")]
    public struct GameProfile
    {
        public const string GLOBAL_PROFILE_NAME = "global";

        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        /// <summary>
        /// Lock object for thread-safe cache and file operations.
        /// Prevents race conditions during profile switching.
        /// </summary>
        private static readonly object ProfileLock = new object();

        [XmlElement("GameId")]
        public GameId GameId;

        [XmlElement("Use")]
        private bool use;
        public bool Use
        {
            get
            {
                if (IsGlobalProfile)
                {
                    // Logger.Warn("Per-game profile is preferred over global profile.");
                    return false;
                }

                return use;
            }
            set
            {
                if (IsGlobalProfile)
                {
                    Logger.Warn("Can't change \"Use\" property of global profile.");
                    return;
                }

                if (use != value)
                {
                    use = value;
                    Save();
                }
            }
        }

        [XmlElement("TDP")]
        private int tdp;
        public int TDP
        {
            get { return tdp; }
            set
            {
                if (tdp != value)
                {
                    tdp = value;
                    Save();
                }
            }
        }

        [XmlElement("CPUBoost")]
        private bool cpuBoost;
        public bool CPUBoost
        {
            get { return cpuBoost; }
            set
            {
                if (cpuBoost != value)
                {
                    cpuBoost = value;
                    Save();
                }
            }
        }

        [XmlElement("CPUEPP")]
        private int cpuEPP;
        public int CPUEPP
        {
            get { return cpuEPP; }
            set
            {
                if (cpuEPP != value)
                {
                    cpuEPP = value;
                    Save();
                }
            }
        }

        [XmlElement("TDPBoostEnabled")]
        private bool tdpBoostEnabled;
        public bool TDPBoostEnabled
        {
            get { return tdpBoostEnabled; }
            set
            {
                if (tdpBoostEnabled != value)
                {
                    tdpBoostEnabled = value;
                    Save();
                }
            }
        }

        // TDP Boost deltas (per profile). When TDPBoostEnabled is on, the helper applies
        // SPPL = TDP + tdpBoostSPPT and FPPT = TDP + tdpBoostFPPT in PerformanceManager.SetTDP.
        // Replaces the previous global helper settings so each profile can configure how aggressive
        // its boost is — and removes the Legion-tab Custom SPPL/FPPT sliders that previously
        // fought with the boost path.
        //
        // GameProfile is a struct so field initializers aren't supported on C# 7.3; the getter
        // returns the legacy defaults (1 / 3) when the backing field is 0 (also the value old
        // profiles will deserialize to since they have no TDPBoostSPPT/FPPT XML element). A delta
        // of 0 is meaningless (no boost) so this overload is safe.
        [XmlElement("TDPBoostSPPT")]
        private int tdpBoostSPPT;
        public int TDPBoostSPPT
        {
            get { return tdpBoostSPPT > 0 ? tdpBoostSPPT : 1; }
            set
            {
                if (tdpBoostSPPT != value)
                {
                    tdpBoostSPPT = value;
                    Save();
                }
            }
        }

        [XmlElement("TDPBoostFPPT")]
        private int tdpBoostFPPT;
        public int TDPBoostFPPT
        {
            get { return tdpBoostFPPT > 0 ? tdpBoostFPPT : 3; }
            set
            {
                if (tdpBoostFPPT != value)
                {
                    tdpBoostFPPT = value;
                    Save();
                }
            }
        }

        // ========== DC (Battery) Overrides ==========
        // When null, the AC value (above) is used. When set, overrides for DC power.

        [XmlElement("TDP_DC")]
        private int? tdpDC;
        public int? TDP_DC
        {
            get { return tdpDC; }
            set
            {
                if (tdpDC != value)
                {
                    tdpDC = value;
                    Save();
                }
            }
        }

        [XmlElement("CPUBoost_DC")]
        private bool? cpuBoostDC;
        public bool? CPUBoost_DC
        {
            get { return cpuBoostDC; }
            set
            {
                if (cpuBoostDC != value)
                {
                    cpuBoostDC = value;
                    Save();
                }
            }
        }

        [XmlElement("CPUEPP_DC")]
        private int? cpuEppDC;
        public int? CPUEPP_DC
        {
            get { return cpuEppDC; }
            set
            {
                if (cpuEppDC != value)
                {
                    cpuEppDC = value;
                    Save();
                }
            }
        }

        /// <summary>
        /// Whether DGP is enabled on AC power for this game.
        /// null = use default auto behavior (disabled on AC)
        /// true/false = user preference
        /// </summary>
        [XmlElement("DgpEnabledOnAC")]
        private bool? dgpEnabledOnAC;
        public bool? DgpEnabledOnAC
        {
            get { return dgpEnabledOnAC; }
            set
            {
                if (dgpEnabledOnAC != value)
                {
                    dgpEnabledOnAC = value;
                    Save();
                }
            }
        }

        /// <summary>
        /// Whether DGP is enabled on DC (battery) power for this game.
        /// null = use default auto behavior (enabled on DC)
        /// true/false = user preference
        /// </summary>
        [XmlElement("DgpEnabledOnDC")]
        private bool? dgpEnabledOnDC;
        public bool? DgpEnabledOnDC
        {
            get { return dgpEnabledOnDC; }
            set
            {
                if (dgpEnabledOnDC != value)
                {
                    dgpEnabledOnDC = value;
                    Save();
                }
            }
        }

        // ========== Additional Profile Settings ==========

        [XmlElement("FPSLimit")]
        private int fpsLimit;
        public int FPSLimit
        {
            get { return fpsLimit; }
            set
            {
                if (fpsLimit != value)
                {
                    fpsLimit = value;
                    Save();
                }
            }
        }

        [XmlElement("FPSLimit_DC")]
        private int? fpsLimitDC;
        public int? FPSLimit_DC
        {
            get { return fpsLimitDC; }
            set
            {
                if (fpsLimitDC != value)
                {
                    fpsLimitDC = value;
                    Save();
                }
            }
        }

        [XmlElement("AutoTDPEnabled")]
        private bool autoTDPEnabled;
        public bool AutoTDPEnabled
        {
            get { return autoTDPEnabled; }
            set
            {
                if (autoTDPEnabled != value)
                {
                    autoTDPEnabled = value;
                    Save();
                }
            }
        }

        [XmlElement("AutoTDPEnabled_DC")]
        private bool? autoTDPEnabledDC;
        public bool? AutoTDPEnabled_DC
        {
            get { return autoTDPEnabledDC; }
            set
            {
                if (autoTDPEnabledDC != value)
                {
                    autoTDPEnabledDC = value;
                    Save();
                }
            }
        }

        [XmlElement("AutoTDPTargetFPS")]
        private int autoTDPTargetFPS;
        public int AutoTDPTargetFPS
        {
            get { return autoTDPTargetFPS; }
            set
            {
                if (autoTDPTargetFPS != value)
                {
                    autoTDPTargetFPS = value;
                    Save();
                }
            }
        }

        [XmlElement("AutoTDPTargetFPS_DC")]
        private int? autoTDPTargetFPSDC;
        public int? AutoTDPTargetFPS_DC
        {
            get { return autoTDPTargetFPSDC; }
            set
            {
                if (autoTDPTargetFPSDC != value)
                {
                    autoTDPTargetFPSDC = value;
                    Save();
                }
            }
        }

        [XmlElement("AutoTDPMinTDP")]
        private int autoTDPMinTDP;
        public int AutoTDPMinTDP
        {
            get { return autoTDPMinTDP; }
            set
            {
                if (autoTDPMinTDP != value)
                {
                    autoTDPMinTDP = value;
                    Save();
                }
            }
        }

        [XmlElement("AutoTDPMaxTDP")]
        private int autoTDPMaxTDP;
        public int AutoTDPMaxTDP
        {
            get { return autoTDPMaxTDP; }
            set
            {
                if (autoTDPMaxTDP != value)
                {
                    autoTDPMaxTDP = value;
                    Save();
                }
            }
        }

        /// <summary>
        /// DEPRECATED: Use AutoTDPControllerType instead.
        /// Kept for backwards compatibility during migration.
        /// </summary>
        [XmlElement("AutoTDPUseMLMode")]
        private bool autoTDPUseMLMode;
        public bool AutoTDPUseMLMode
        {
            get { return autoTDPUseMLMode; }
            set
            {
                if (autoTDPUseMLMode != value)
                {
                    autoTDPUseMLMode = value;
                    Save();
                }
            }
        }

        [XmlElement("AutoTDPPauseWhenUnfocused")]
        private bool autoTDPPauseWhenUnfocused;
        public bool AutoTDPPauseWhenUnfocused
        {
            get { return autoTDPPauseWhenUnfocused; }
            set
            {
                if (autoTDPPauseWhenUnfocused != value)
                {
                    autoTDPPauseWhenUnfocused = value;
                    Save();
                }
            }
        }

        /// <summary>
        /// AutoTDP controller type: 0=PID, 1=Q-Learning, 2=SARSA
        /// </summary>
        [XmlElement("AutoTDPControllerType")]
        private int autoTDPControllerType;
        public int AutoTDPControllerType
        {
            get { return autoTDPControllerType; }
            set
            {
                if (autoTDPControllerType != value)
                {
                    autoTDPControllerType = value;
                    Save();
                }
            }
        }

        [XmlElement("OSPowerMode")]
        private string osPowerMode;
        public string OSPowerMode
        {
            get { return osPowerMode; }
            set
            {
                if (osPowerMode != value)
                {
                    osPowerMode = value;
                    Save();
                }
            }
        }

        [XmlElement("OSPowerMode_DC")]
        private string osPowerModeDC;
        public string OSPowerMode_DC
        {
            get { return osPowerModeDC; }
            set
            {
                if (osPowerModeDC != value)
                {
                    osPowerModeDC = value;
                    Save();
                }
            }
        }

        [XmlElement("HDREnabled")]
        private bool hdrEnabled;
        public bool HDREnabled
        {
            get { return hdrEnabled; }
            set
            {
                if (hdrEnabled != value)
                {
                    hdrEnabled = value;
                    Save();
                }
            }
        }

        [XmlElement("Resolution")]
        private string resolution;
        public string Resolution
        {
            get { return resolution; }
            set
            {
                if (resolution != value)
                {
                    resolution = value;
                    Save();
                }
            }
        }

        [XmlElement("RefreshRate")]
        private int? refreshRate;
        public int? RefreshRate
        {
            get { return refreshRate; }
            set
            {
                if (refreshRate != value)
                {
                    refreshRate = value;
                    Save();
                }
            }
        }

        [XmlElement("StickyTDP")]
        private bool stickyTDP;
        public bool StickyTDP
        {
            get { return stickyTDP; }
            set
            {
                if (stickyTDP != value)
                {
                    stickyTDP = value;
                    Save();
                }
            }
        }

        /// <summary>
        /// Performance overlay level (0=Off, 1=Basic, 2=Detailed, 3=Full for RTSS; 1-4 for AMD)
        /// </summary>
        [XmlElement("OverlayLevel")]
        private int? overlayLevel;
        public int? OverlayLevel
        {
            get { return overlayLevel; }
            set
            {
                if (overlayLevel != value)
                {
                    overlayLevel = value;
                    Save();
                }
            }
        }

        // ========== Legion Controller Remapping ==========

        [XmlElement("LegionButtonY1")]
        private string legionButtonY1;
        public string LegionButtonY1
        {
            get { return legionButtonY1; }
            set
            {
                if (legionButtonY1 != value)
                {
                    legionButtonY1 = value;
                    Save();
                }
            }
        }

        [XmlElement("LegionButtonY2")]
        private string legionButtonY2;
        public string LegionButtonY2
        {
            get { return legionButtonY2; }
            set
            {
                if (legionButtonY2 != value)
                {
                    legionButtonY2 = value;
                    Save();
                }
            }
        }

        [XmlElement("LegionButtonY3")]
        private string legionButtonY3;
        public string LegionButtonY3
        {
            get { return legionButtonY3; }
            set
            {
                if (legionButtonY3 != value)
                {
                    legionButtonY3 = value;
                    Save();
                }
            }
        }

        [XmlElement("LegionButtonM2")]
        private string legionButtonM2;
        public string LegionButtonM2
        {
            get { return legionButtonM2; }
            set
            {
                if (legionButtonM2 != value)
                {
                    legionButtonM2 = value;
                    Save();
                }
            }
        }

        [XmlElement("LegionButtonM3")]
        private string legionButtonM3;
        public string LegionButtonM3
        {
            get { return legionButtonM3; }
            set
            {
                if (legionButtonM3 != value)
                {
                    legionButtonM3 = value;
                    Save();
                }
            }
        }

        [XmlElement("LegionButtonDesktop")]
        private string legionButtonDesktop;
        public string LegionButtonDesktop
        {
            get { return legionButtonDesktop; }
            set
            {
                if (legionButtonDesktop != value)
                {
                    legionButtonDesktop = value;
                    Save();
                }
            }
        }

        [XmlElement("LegionButtonPage")]
        private string legionButtonPage;
        public string LegionButtonPage
        {
            get { return legionButtonPage; }
            set
            {
                if (legionButtonPage != value)
                {
                    legionButtonPage = value;
                    Save();
                }
            }
        }

        [XmlElement("LegionGyroButton")]
        private int? legionGyroButton;
        public int? LegionGyroButton
        {
            get { return legionGyroButton; }
            set
            {
                if (legionGyroButton != value)
                {
                    legionGyroButton = value;
                    Save();
                }
            }
        }

        // ========== Additional Legion Controller Settings ==========

        [XmlElement("LegionControllerProfileEnabled")]
        private bool? legionControllerProfileEnabled;
        public bool? LegionControllerProfileEnabled
        {
            get { return legionControllerProfileEnabled; }
            set
            {
                if (legionControllerProfileEnabled != value)
                {
                    legionControllerProfileEnabled = value;
                    Save();
                }
            }
        }

        [XmlElement("LegionButtonM1")]
        private string legionButtonM1;
        public string LegionButtonM1
        {
            get { return legionButtonM1; }
            set
            {
                if (legionButtonM1 != value)
                {
                    legionButtonM1 = value;
                    Save();
                }
            }
        }

        [XmlElement("LegionGyroTarget")]
        private int? legionGyroTarget;
        public int? LegionGyroTarget
        {
            get { return legionGyroTarget; }
            set
            {
                if (legionGyroTarget != value)
                {
                    legionGyroTarget = value;
                    Save();
                }
            }
        }

        [XmlElement("LegionGyroSensitivityX")]
        private int? legionGyroSensitivityX;
        public int? LegionGyroSensitivityX
        {
            get { return legionGyroSensitivityX; }
            set
            {
                if (legionGyroSensitivityX != value)
                {
                    legionGyroSensitivityX = value;
                    Save();
                }
            }
        }

        [XmlElement("LegionGyroSensitivityY")]
        private int? legionGyroSensitivityY;
        public int? LegionGyroSensitivityY
        {
            get { return legionGyroSensitivityY; }
            set
            {
                if (legionGyroSensitivityY != value)
                {
                    legionGyroSensitivityY = value;
                    Save();
                }
            }
        }

        [XmlElement("LegionGyroInvertX")]
        private bool? legionGyroInvertX;
        public bool? LegionGyroInvertX
        {
            get { return legionGyroInvertX; }
            set
            {
                if (legionGyroInvertX != value)
                {
                    legionGyroInvertX = value;
                    Save();
                }
            }
        }

        [XmlElement("LegionGyroInvertY")]
        private bool? legionGyroInvertY;
        public bool? LegionGyroInvertY
        {
            get { return legionGyroInvertY; }
            set
            {
                if (legionGyroInvertY != value)
                {
                    legionGyroInvertY = value;
                    Save();
                }
            }
        }

        [XmlElement("LegionGyroMappingType")]
        private int? legionGyroMappingType;
        public int? LegionGyroMappingType
        {
            get { return legionGyroMappingType; }
            set
            {
                if (legionGyroMappingType != value)
                {
                    legionGyroMappingType = value;
                    Save();
                }
            }
        }

        [XmlElement("LegionGyroActivationMode")]
        private int? legionGyroActivationMode;
        public int? LegionGyroActivationMode
        {
            get { return legionGyroActivationMode; }
            set
            {
                if (legionGyroActivationMode != value)
                {
                    legionGyroActivationMode = value;
                    Save();
                }
            }
        }

        [XmlElement("LegionGyroDeadzone")]
        private int? legionGyroDeadzone;
        public int? LegionGyroDeadzone
        {
            get { return legionGyroDeadzone; }
            set
            {
                if (legionGyroDeadzone != value)
                {
                    legionGyroDeadzone = value;
                    Save();
                }
            }
        }

        [XmlElement("LegionLeftStickDeadzone")]
        private int? legionLeftStickDeadzone;
        public int? LegionLeftStickDeadzone
        {
            get { return legionLeftStickDeadzone; }
            set
            {
                if (legionLeftStickDeadzone != value)
                {
                    legionLeftStickDeadzone = value;
                    Save();
                }
            }
        }

        [XmlElement("LegionRightStickDeadzone")]
        private int? legionRightStickDeadzone;
        public int? LegionRightStickDeadzone
        {
            get { return legionRightStickDeadzone; }
            set
            {
                if (legionRightStickDeadzone != value)
                {
                    legionRightStickDeadzone = value;
                    Save();
                }
            }
        }

        [XmlElement("LegionLeftTriggerStart")]
        private int? legionLeftTriggerStart;
        public int? LegionLeftTriggerStart
        {
            get { return legionLeftTriggerStart; }
            set
            {
                if (legionLeftTriggerStart != value)
                {
                    legionLeftTriggerStart = value;
                    Save();
                }
            }
        }

        [XmlElement("LegionLeftTriggerEnd")]
        private int? legionLeftTriggerEnd;
        public int? LegionLeftTriggerEnd
        {
            get { return legionLeftTriggerEnd; }
            set
            {
                if (legionLeftTriggerEnd != value)
                {
                    legionLeftTriggerEnd = value;
                    Save();
                }
            }
        }

        [XmlElement("LegionRightTriggerStart")]
        private int? legionRightTriggerStart;
        public int? LegionRightTriggerStart
        {
            get { return legionRightTriggerStart; }
            set
            {
                if (legionRightTriggerStart != value)
                {
                    legionRightTriggerStart = value;
                    Save();
                }
            }
        }

        [XmlElement("LegionRightTriggerEnd")]
        private int? legionRightTriggerEnd;
        public int? LegionRightTriggerEnd
        {
            get { return legionRightTriggerEnd; }
            set
            {
                if (legionRightTriggerEnd != value)
                {
                    legionRightTriggerEnd = value;
                    Save();
                }
            }
        }

        [XmlElement("LegionHairTriggers")]
        private bool? legionHairTriggers;
        public bool? LegionHairTriggers
        {
            get { return legionHairTriggers; }
            set
            {
                if (legionHairTriggers != value)
                {
                    legionHairTriggers = value;
                    Save();
                }
            }
        }

        [XmlElement("LegionJoystickAsMouseMode")]
        private int? legionJoystickAsMouseMode;
        public int? LegionJoystickAsMouseMode
        {
            get { return legionJoystickAsMouseMode; }
            set
            {
                if (legionJoystickAsMouseMode != value)
                {
                    legionJoystickAsMouseMode = value;
                    Save();
                }
            }
        }

        [XmlElement("LegionJoystickMouseSens")]
        private int? legionJoystickMouseSens;
        public int? LegionJoystickMouseSens
        {
            get { return legionJoystickMouseSens; }
            set
            {
                if (legionJoystickMouseSens != value)
                {
                    legionJoystickMouseSens = value;
                    Save();
                }
            }
        }

        [XmlElement("LegionGamepadMapping")]
        private string legionGamepadMapping;
        public string LegionGamepadMapping
        {
            get { return legionGamepadMapping; }
            set
            {
                if (legionGamepadMapping != value)
                {
                    legionGamepadMapping = value;
                    Save();
                }
            }
        }

        [XmlElement("LegionNintendoLayout")]
        private bool? legionNintendoLayout;
        public bool? LegionNintendoLayout
        {
            get { return legionNintendoLayout; }
            set
            {
                if (legionNintendoLayout != value)
                {
                    legionNintendoLayout = value;
                    Save();
                }
            }
        }

        [XmlElement("LegionVibration")]
        private int? legionVibration;
        public int? LegionVibration
        {
            get { return legionVibration; }
            set
            {
                if (legionVibration != value)
                {
                    legionVibration = value;
                    Save();
                }
            }
        }

        [XmlElement("LegionVibrationMode")]
        private int? legionVibrationMode;
        public int? LegionVibrationMode
        {
            get { return legionVibrationMode; }
            set
            {
                if (legionVibrationMode != value)
                {
                    legionVibrationMode = value;
                    Save();
                }
            }
        }

        /// <summary>
        /// Legion Performance Mode (1=Quiet, 2=Balanced, 3=Performance, 255=Custom)
        /// null = use current system mode (don't change on profile switch)
        /// </summary>
        [XmlElement("LegionPerformanceMode")]
        private int? legionPerformanceMode;
        public int? LegionPerformanceMode
        {
            get { return legionPerformanceMode; }
            set
            {
                if (legionPerformanceMode != value)
                {
                    legionPerformanceMode = value;
                    Save();
                }
            }
        }

        // ========== Legion Controller Lighting ==========

        /// <summary>
        /// Legion Light Mode (0=Off, 1=Solid, 2=Pulse, 3=Dynamic, 4=Spiral)
        /// </summary>
        [XmlElement("LegionLightMode")]
        private int? legionLightMode;
        public int? LegionLightMode
        {
            get { return legionLightMode; }
            set
            {
                if (legionLightMode != value)
                {
                    legionLightMode = value;
                    Save();
                }
            }
        }

        /// <summary>
        /// Legion Light Color as hex string (RRGGBB format)
        /// </summary>
        [XmlElement("LegionLightColor")]
        private string legionLightColor;
        public string LegionLightColor
        {
            get { return legionLightColor; }
            set
            {
                if (legionLightColor != value)
                {
                    legionLightColor = value;
                    Save();
                }
            }
        }

        /// <summary>
        /// Legion Light Brightness (0-100%)
        /// </summary>
        [XmlElement("LegionLightBrightness")]
        private int? legionLightBrightness;
        public int? LegionLightBrightness
        {
            get { return legionLightBrightness; }
            set
            {
                if (legionLightBrightness != value)
                {
                    legionLightBrightness = value;
                    Save();
                }
            }
        }

        /// <summary>
        /// Legion Light Speed (0-100%)
        /// </summary>
        [XmlElement("LegionLightSpeed")]
        private int? legionLightSpeed;
        public int? LegionLightSpeed
        {
            get { return legionLightSpeed; }
            set
            {
                if (legionLightSpeed != value)
                {
                    legionLightSpeed = value;
                    Save();
                }
            }
        }

        /// <summary>
        /// Legion Power Light (controller's power indicator LED)
        /// </summary>
        [XmlElement("LegionPowerLight")]
        private bool? legionPowerLight;
        public bool? LegionPowerLight
        {
            get { return legionPowerLight; }
            set
            {
                if (legionPowerLight != value)
                {
                    legionPowerLight = value;
                    Save();
                }
            }
        }

        [XmlIgnore]
        public string Path;

        public bool IsGlobalProfile { get { return string.Compare(GameId.Name, GLOBAL_PROFILE_NAME) == 0; } }

        [XmlIgnore]
        private IDictionary<GameId, GameProfile> cache;
        [XmlIgnore]
        public IDictionary<GameId, GameProfile> Cache
        {
            get { return cache; }
            set { cache = value; }
        }

        public GameProfile(string gameName, string gamePath, bool inUse, int inTDP, bool inCPUBoost, int inCPUEPP, bool inTDPBoostEnabled, string inPath, IDictionary<GameId, GameProfile> inCache)
        {
            GameId = new GameId(gameName, gamePath);
            use = inUse;
            // AC values (main settings)
            tdp = inTDP;
            cpuBoost = inCPUBoost;
            cpuEPP = inCPUEPP;
            tdpBoostEnabled = inTDPBoostEnabled;
            tdpBoostSPPT = 0; // 0 = property getter returns legacy default 1
            tdpBoostFPPT = 0; // 0 = property getter returns legacy default 3
            // DC overrides (null = use AC value)
            tdpDC = null;
            cpuBoostDC = null;
            cpuEppDC = null;
            // DGP preferences
            dgpEnabledOnAC = null;
            dgpEnabledOnDC = null;
            // Additional profile settings (AC)
            fpsLimit = 0;
            autoTDPEnabled = false;
            autoTDPTargetFPS = 60;
            autoTDPMinTDP = 8;
            autoTDPMaxTDP = 30;
            autoTDPUseMLMode = false;
            autoTDPPauseWhenUnfocused = true; // Default: pause when game not focused
            autoTDPControllerType = 0; // PID by default
            osPowerMode = null;
            // Additional profile settings (DC overrides)
            fpsLimitDC = null;
            autoTDPEnabledDC = null;
            autoTDPTargetFPSDC = null;
            osPowerModeDC = null;
            // Display settings (shared AC/DC)
            hdrEnabled = false;
            resolution = null;
            refreshRate = null;
            stickyTDP = false;
            overlayLevel = null;
            // Legion controller remapping (shared AC/DC)
            legionButtonY1 = null;
            legionButtonY2 = null;
            legionButtonY3 = null;
            legionButtonM2 = null;
            legionButtonM3 = null;
            legionButtonDesktop = null;
            legionButtonPage = null;
            legionGyroButton = null;
            // Additional Legion controller settings
            legionControllerProfileEnabled = null;
            legionButtonM1 = null;
            legionGyroTarget = null;
            legionGyroSensitivityX = null;
            legionGyroSensitivityY = null;
            legionGyroInvertX = null;
            legionGyroInvertY = null;
            legionGyroMappingType = null;
            legionGyroActivationMode = null;
            legionGyroDeadzone = null;
            legionLeftStickDeadzone = null;
            legionRightStickDeadzone = null;
            legionLeftTriggerStart = null;
            legionLeftTriggerEnd = null;
            legionRightTriggerStart = null;
            legionRightTriggerEnd = null;
            legionHairTriggers = null;
            legionJoystickAsMouseMode = null;
            legionJoystickMouseSens = null;
            legionGamepadMapping = null;
            legionNintendoLayout = null;
            legionVibration = null;
            legionVibrationMode = null;
            legionPerformanceMode = null;
            // Lighting settings
            legionLightMode = null;
            legionLightColor = null;
            legionLightBrightness = null;
            legionLightSpeed = null;
            legionPowerLight = null;
            Path = inPath;
            cache = inCache;
        }

        public bool IsValid()
        {
            return GameId.IsValid();
        }

        public static bool operator ==(GameProfile g1, GameProfile g2)
        {
            return g1.GameId == g2.GameId;
        }

        public static bool operator !=(GameProfile p1, GameProfile p2)
        {
            return !(p1 == p2);
        }

        public override bool Equals(object obj)
        {
            if (obj is GameProfile other)
            {
                return this == other;
            }
            return false;
        }

        public override int GetHashCode()
        {
            return GameId.GetHashCode();
        }

        // Export to xml string.
        public override string ToString()
        {
            return XmlHelper.ToXMLString(this, true);
        }

        /// <summary>
        /// Save debounce state keyed by file path. A GameProfile setter typically changes one
        /// field at a time, and UI sliders can fire dozens of setters per second. Writing the
        /// full XML to disk each time is wasteful. We instead update the cache synchronously
        /// (so reads are always consistent) and schedule the disk write after a short delay,
        /// collapsing bursts of changes into a single write.
        /// </summary>
        private const int SaveDebounceMs = 250;
        private static readonly ConcurrentDictionary<string, GameProfile> PendingWrites
            = new ConcurrentDictionary<string, GameProfile>(System.StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, Timer> PendingTimers
            = new ConcurrentDictionary<string, Timer>(System.StringComparer.OrdinalIgnoreCase);

        public void Save()
        {
            // Update cache synchronously so other code sees the latest state immediately.
            lock (ProfileLock)
            {
                if (cache != null)
                {
                    cache[GameId] = this;
                }
            }

            if (string.IsNullOrEmpty(Path))
            {
                return;
            }

            // Queue the disk write; coalesce bursts of changes into a single debounced write.
            PendingWrites[Path] = this;

            var newTimer = new Timer(FlushPendingWrite, Path, SaveDebounceMs, Timeout.Infinite);
            if (PendingTimers.TryGetValue(Path, out var existing))
            {
                // Cancel and dispose the previous timer to reset the debounce window.
                existing.Dispose();
            }
            PendingTimers[Path] = newTimer;
        }

        /// <summary>
        /// Timer callback: flushes the pending profile snapshot for <paramref name="state"/> (a file path) to disk.
        /// </summary>
        private static void FlushPendingWrite(object state)
        {
            var path = (string)state;
            if (!PendingWrites.TryRemove(path, out var profile))
            {
                return;
            }
            if (PendingTimers.TryRemove(path, out var timer))
            {
                timer.Dispose();
            }

            lock (ProfileLock)
            {
                XmlHelper.ToXMLFile(profile, path);
            }
        }

        /// <summary>
        /// Forces any pending debounced saves to flush immediately. Useful on shutdown.
        /// </summary>
        public static void FlushAllPendingWrites()
        {
            foreach (var path in PendingWrites.Keys)
            {
                FlushPendingWrite(path);
            }
        }
    }
}
