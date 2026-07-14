using RTSSSharedMemoryNET;
using Shared.Enums;
using Shared.Utilities;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using XboxGamingBarHelper.AutoTDP;
using XboxGamingBarHelper.Devices.Libraries.Legion;
using XboxGamingBarHelper.OnScreenDisplay;
using XboxGamingBarHelper.Performance;
using XboxGamingBarHelper.RTSS.OSDItems;
using XboxGamingBarHelper.Settings;
using XboxGamingBarHelper.Windows;

namespace XboxGamingBarHelper.RTSS
{
    internal class RTSSManager : OnScreenDisplayManager
    {
        private const string OSDSeparator = " <C=6E006A>|<C> ";
        private const string OSDBackground = "<P=0,0><L0><C=80000000><B=0,0>\b<C>";
        private const string OSDAppName = "GoTweaks OSD";

        private OSD rtssOSD;
        private readonly OSDItem[] osdItems;
        private readonly OSDItemFan osdItemFan;
        private readonly OSDItemAutoTDP osdItemAutoTDP;
        private readonly OSDItemTDPLimits osdItemTDPLimits;
        private readonly OSDItemCPU osdItemCPU;
        private readonly OSDItemTime osdItemTime = new OSDItemTime();
        private bool clock24Hour = false;
        private readonly OSDItemGPU osdItemGPU;
        private readonly OSDItemVRAM osdItemVRAM;
        private readonly OSDItemControllerBattery osdItemControllerBattery;

        private readonly RTSSInstalledProperty rtssInstalled;
        public RTSSInstalledProperty RTSSInstalled
        {
            get { return rtssInstalled; }
        }

        private readonly OSDConfigProperty osdConfig;
        public OSDConfigProperty OSDConfig
        {
            get { return osdConfig; }
        }

        private readonly FPSLimitProperty fpsLimit;
        public FPSLimitProperty FPSLimit
        {
            get { return fpsLimit; }
        }

        private DisplayOSDConfigProperty displayOSDConfig;
        public DisplayOSDConfigProperty DisplayOSDConfig
        {
            get { return displayOSDConfig; }
        }

        private RivatunerStatisticsServerState rtssState;

        // Tracks the RTSS process running-state across Update() ticks so we can act
        // exactly once on a not-running→running transition (init the FPS limiter +
        // re-apply the current limit). Evaluated regardless of OSD level so FPS
        // limiting works even with the OSD turned off.
        private bool rtssWasRunning = false;

        // Frametime graph settings - uses <G=<FT>> tag processed by RTSSSharedMemoryNET
        // Width/height are hardcoded in ProcessGraphTags (-32 chars wide, -2 lines tall)
        private float currentMinFt = 0f;
        private float currentAvgFt = 0f;
        private float currentMaxFt = 0f;

        // Public frametime stats for stability detection (used by AutoTDPManager)
        public float FrametimeMin => currentMinFt;
        public float FrametimeAvg => currentAvgFt;
        public float FrametimeMax => currentMaxFt;
        public float FrametimeVariance => currentMaxFt - currentMinFt;  // Max-Min variance in ms

        // OSD configuration per level - stores which items are enabled
        // Level 1 (Basic): Time, FPS, Battery - 3 columns
        // Level 2 (Detailed): Time, FPS, Battery, CPU, GPU, Fan, FrametimeGraph - 1 column
        // Level 3 (Full): All options - 1 column
        private Dictionary<int, HashSet<string>> osdLevelConfig = new Dictionary<int, HashSet<string>>
        {
            { 1, new HashSet<string> { "Time", "FPS", "Battery" } },
            { 2, new HashSet<string> { "Time", "FPS", "Battery", "CPU", "GPU", "FrameBudget", "Fan", "FrametimeGraph" } },
            { 3, new HashSet<string> { "AppName", "Time", "FPS", "Battery", "ControllerBattery", "Memory", "VRAM", "CPU", "CPUClock", "GPU", "GPUClock", "FrameBudget", "Fan", "AutoTDP", "FrametimeGraph" } }
        };
        private Dictionary<int, string> osdCustomTags = new Dictionary<int, string>
        {
            { 1, "" },
            { 2, "" },
            { 3, "" }
        };

        // Layout settings
        private int osdTextSize = 100;        // Percentage: 50=Small, 100=Medium, 150=Large, 200=X-Large
        private string osdTextColor = "FFFFFF";
        private string osdLabelColor = "DEFAULT";  // DEFAULT = use item-specific colors, or hex color code
        private string osdBackgroundColor = "80000000";
        private int osdOpacity = 100;         // Percentage: 10-100, darkens OSD colors for OLED protection

        // OSD position offset for OLED burn-in protection
        private int osdPositionOffsetX = 0;
        private int osdPositionOffsetY = 0;

        // OSD Position Shift (OLED burn-in protection)
        // Note: In RTSS, 1 vertical pixel = 2 horizontal pixels visually
        private Timer positionShiftTimer;
        private bool positionShiftEnabled = false;
        private const int MAX_OFFSET_X = 3;  // Horizontal pixels (±3)
        private const int MAX_OFFSET_Y = 2;  // Vertical pixels (±2) - appears similar to ±3 horizontal
        private readonly Random positionShiftRandom = new Random();

        // Frametime graph pinned mode - always on its own row at the bottom, left-aligned
        private bool frametimeGraphPinned = false;

        // Per-level columns (Basic=3, Detailed=1, Full=1)
        private Dictionary<int, int> osdLevelColumns = new Dictionary<int, int>
        {
            { 1, 3 },  // Basic: 3 columns
            { 2, 1 },  // Detailed: 1 column (vertical list)
            { 3, 1 }   // Full: 1 column (vertical list)
        };

        // Per-level item order
        private Dictionary<int, List<string>> osdLevelOrder = new Dictionary<int, List<string>>
        {
            { 1, new List<string> { "AppName", "Time", "FPS", "Battery", "ControllerBattery", "Memory", "VRAM", "CPU", "CPUClock", "GPU", "GPUClock", "FrameBudget", "Fan", "AutoTDP", "TDPLimits", "FrametimeGraph" } },
            { 2, new List<string> { "AppName", "Time", "FPS", "Battery", "ControllerBattery", "Memory", "VRAM", "CPU", "CPUClock", "GPU", "GPUClock", "FrameBudget", "Fan", "AutoTDP", "TDPLimits", "FrametimeGraph" } },
            { 3, new List<string> { "AppName", "Time", "FPS", "Battery", "ControllerBattery", "Memory", "VRAM", "CPU", "CPUClock", "GPU", "GPUClock", "FrameBudget", "Fan", "AutoTDP", "TDPLimits", "FrametimeGraph" } }
        };

        // Per-level, per-item label colors (e.g., osdItemLabelColors[1]["CPU"] = "FF0000")
        private Dictionary<int, Dictionary<string, string>> osdItemLabelColors = new Dictionary<int, Dictionary<string, string>>
        {
            { 1, new Dictionary<string, string>() },
            { 2, new Dictionary<string, string>() },
            { 3, new Dictionary<string, string>() }
        };

        public RTSSManager(PerformanceManager performanceManager) : base()
        {
            rtssInstalled = new RTSSInstalledProperty(this);
            osdConfig = new OSDConfigProperty(this);
            fpsLimit = new FPSLimitProperty(this);

            RTSSFPSLimiter.Initialize();
            osdItemFan = new OSDItemFan();
            osdItemAutoTDP = new OSDItemAutoTDP();
            osdItemTDPLimits = new OSDItemTDPLimits();
            osdItemTDPLimits.SetPerformanceManager(performanceManager);
            osdItemCPU = new OSDItemCPU(performanceManager.CPUUsage, performanceManager.CPUClock, performanceManager.CPUWattage, performanceManager.CPUTemperature);
            osdItemGPU = new OSDItemGPU(performanceManager.GPUUsage, performanceManager.GPUClock, performanceManager.GPUWattage, performanceManager.GPUTemperature);
            osdItemVRAM = new OSDItemVRAM(performanceManager.GPUMemoryUsed, performanceManager.GPUMemoryFree, performanceManager.GPUMemoryClock);
            osdItemControllerBattery = new OSDItemControllerBattery(null, null, null, null);
            osdItems = new OSDItem[]
            {
                osdItemTime,
                new OSDItemAppName(),
                new OSDItemFPS(),
                new OSDItemBattery(performanceManager.BatteryLevel, performanceManager.BatteryDischargeRate, performanceManager.BatteryChargeRate, performanceManager.BatteryRemainingTime, () => performanceManager.BatteryTimeToFull),
                osdItemControllerBattery,
                osdItemCPU,
                osdItemGPU,
                new OSDItemFrameBudget(),
                osdItemVRAM,
                new OSDItemMemory(performanceManager.MemoryUsage, performanceManager.MemoryUsed, performanceManager.MemoryAvailable),
                osdItemFan,
                osdItemAutoTDP,
                osdItemTDPLimits,
            };

            rtssState = RivatunerStatisticsServerState.NotInstalled;
        }

        /// <summary>
        /// Parses the OSD configuration string from the widget.
        /// Format: "Position:0;Columns:3;TextSize:100;TextColor:FFFFFF;BackgroundColor:80000000;L1:FPS,Battery;L2:...;L1_Custom:tags"
        /// </summary>
        public void ParseOSDConfig(string configString)
        {
            if (string.IsNullOrEmpty(configString))
            {
                Logger.Warn("Empty OSD config string received");
                return;
            }

            Logger.Info($"Parsing OSD config: {configString}");

            try
            {
                var parts = configString.Split(';');
                foreach (var part in parts)
                {
                    if (string.IsNullOrWhiteSpace(part)) continue;

                    var colonIndex = part.IndexOf(':');
                    if (colonIndex <= 0) continue;

                    var key = part.Substring(0, colonIndex);
                    var value = colonIndex < part.Length - 1 ? part.Substring(colonIndex + 1) : "";

                    // Layout settings
                    if (key == "TextSize")
                    {
                        if (int.TryParse(value, out int size))
                        {
                            osdTextSize = size;
                            Logger.Debug($"OSD TextSize: {size}");
                        }
                    }
                    else if (key == "TextColor")
                    {
                        osdTextColor = value;
                        Logger.Debug($"OSD TextColor: {value}");
                    }
                    else if (key == "LabelColor")
                    {
                        osdLabelColor = value;
                        Logger.Debug($"OSD LabelColor: {value}");
                    }
                    else if (key == "BackgroundColor")
                    {
                        osdBackgroundColor = value;
                        Logger.Debug($"OSD BackgroundColor: {value}");
                    }
                    else if (key == "Opacity")
                    {
                        if (int.TryParse(value, out int opacity))
                        {
                            osdOpacity = Math.Max(10, Math.Min(100, opacity));
                            Logger.Debug($"OSD Opacity: {osdOpacity}");
                        }
                    }
                    else if (key == "FrametimeGraphPinned")
                    {
                        frametimeGraphPinned = value == "1" || value.ToLower() == "true";
                        Logger.Debug($"OSD FrametimeGraphPinned: {frametimeGraphPinned}");
                    }
                    else if (key == "Clock24Hour")
                    {
                        clock24Hour = value == "1" || value.ToLower() == "true";
                        Logger.Debug($"OSD Clock24Hour: {clock24Hour}");
                    }
                    else if (key.StartsWith("L") && key.EndsWith("_Columns"))
                    {
                        // Per-level columns: L1_Columns, L2_Columns, L3_Columns
                        var levelStr = key.Substring(1, key.Length - 9); // "L1_Columns" -> "1"
                        if (int.TryParse(levelStr, out int level) && int.TryParse(value, out int cols))
                        {
                            osdLevelColumns[level] = cols;
                            Logger.Debug($"OSD Level {level} columns: {cols}");
                        }
                    }
                    else if (key.StartsWith("L") && key.EndsWith("_Custom"))
                    {
                        // Custom tags: L1_Custom, L2_Custom, L3_Custom
                        var levelStr = key.Substring(1, key.Length - 8);
                        if (int.TryParse(levelStr, out int level))
                        {
                            osdCustomTags[level] = value;
                            Logger.Debug($"OSD Level {level} custom tags: {value}");
                        }
                    }
                    else if (key.StartsWith("L") && key.EndsWith("_Order"))
                    {
                        // Order: L1_Order, L2_Order, L3_Order
                        var levelStr = key.Substring(1, key.Length - 7); // "L1_Order" -> "1"
                        if (int.TryParse(levelStr, out int level))
                        {
                            var orderList = value.Split(',').Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
                            if (orderList.Count > 0)
                            {
                                osdLevelOrder[level] = orderList;
                                Logger.Debug($"OSD Level {level} order: {string.Join(", ", orderList)}");
                            }
                        }
                    }
                    else if (key.StartsWith("L") && key.Contains("_") && key.EndsWith("_Color"))
                    {
                        // Item label color: L1_CPU_Color, L2_FPS_Color, etc.
                        // Format: L{level}_{itemId}_Color
                        var underscoreIdx = key.IndexOf('_');
                        var lastUnderscoreIdx = key.LastIndexOf('_');
                        if (underscoreIdx > 1 && lastUnderscoreIdx > underscoreIdx)
                        {
                            var levelStr = key.Substring(1, underscoreIdx - 1);
                            var itemId = key.Substring(underscoreIdx + 1, lastUnderscoreIdx - underscoreIdx - 1);
                            if (int.TryParse(levelStr, out int level) && !string.IsNullOrEmpty(itemId))
                            {
                                if (!osdItemLabelColors.ContainsKey(level))
                                {
                                    osdItemLabelColors[level] = new Dictionary<string, string>();
                                }
                                osdItemLabelColors[level][itemId] = value;
                                Logger.Debug($"OSD Level {level} item '{itemId}' label color: {value}");
                            }
                        }
                    }
                    else if (key.StartsWith("L"))
                    {
                        // Level config: L1, L2, L3
                        var levelStr = key.Substring(1);
                        if (int.TryParse(levelStr, out int level))
                        {
                            var items = new HashSet<string>();
                            if (!string.IsNullOrEmpty(value))
                            {
                                foreach (var item in value.Split(','))
                                {
                                    if (!string.IsNullOrWhiteSpace(item))
                                    {
                                        items.Add(item.Trim());
                                    }
                                }
                            }
                            osdLevelConfig[level] = items;
                            Logger.Debug($"OSD Level {level} items: {string.Join(", ", items)}");
                        }
                    }
                }

                Logger.Info("OSD configuration updated successfully");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error parsing OSD config: {ex.Message}");
            }
        }

        /// <summary>
        /// Parses display/OSD config from the widget.
        /// Format: "PositionShift:1;PositionShiftInterval:5;AdaptiveBrightness:1"
        /// AdaptiveBrightness is handled by SystemManager via callback.
        /// </summary>
        public void ParseDisplayOSDConfig(string configString, Action<bool> setAdaptiveBrightness)
        {
            if (string.IsNullOrEmpty(configString))
                return;

            Logger.Info($"Parsing Display/OSD config: {configString}");

            try
            {
                var parts = configString.Split(';');
                foreach (var part in parts)
                {
                    if (string.IsNullOrWhiteSpace(part)) continue;

                    // Match ParseOSDConfig: split on the FIRST colon only, so a value
                    // that itself contains ':' isn't dropped by an exact-2-parts check.
                    var colonIndex = part.IndexOf(':');
                    if (colonIndex <= 0) continue;

                    var key = part.Substring(0, colonIndex);
                    var value = colonIndex < part.Length - 1 ? part.Substring(colonIndex + 1) : "";

                    switch (key)
                    {
                        case "PositionShift":
                            bool newPosShiftEnabled = value == "1";
                            if (newPosShiftEnabled != positionShiftEnabled)
                            {
                                positionShiftEnabled = newPosShiftEnabled;
                                UpdatePositionShiftTimer();
                            }
                            break;
                        case "AdaptiveBrightness":
                            setAdaptiveBrightness?.Invoke(value == "1");
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error parsing Display/OSD config: {ex.Message}");
            }
        }

        private void UpdatePositionShiftTimer()
        {
            positionShiftTimer?.Dispose();
            positionShiftTimer = null;

            if (positionShiftEnabled)
            {
                // Fixed 1-minute interval for OLED burn-in prevention
                const int intervalMs = 60 * 1000;
                positionShiftTimer = new Timer(PositionShiftTick, null, intervalMs, intervalMs);
                Logger.Info("OSD Position shift enabled, interval: 1 minute");
            }
            else
            {
                // Reset to origin
                osdPositionOffsetX = 0;
                osdPositionOffsetY = 0;
                Logger.Info("OSD Position shift disabled");
            }
        }

        private void PositionShiftTick(object state)
        {
            // Generate random offset within bounds (X and Y scaled for visual uniformity)
            osdPositionOffsetX = positionShiftRandom.Next(-MAX_OFFSET_X, MAX_OFFSET_X + 1);
            osdPositionOffsetY = positionShiftRandom.Next(-MAX_OFFSET_Y, MAX_OFFSET_Y + 1);
            Logger.Debug($"OSD Position shifted to ({osdPositionOffsetX}, {osdPositionOffsetY})");
        }

        /// <summary>
        /// Checks if the given OSD item should be shown for the current level.
        /// </summary>
        private bool IsItemEnabled(string itemId)
        {
            if (osdLevelConfig.TryGetValue(onScreenDisplayLevel, out var enabledItems))
            {
                return enabledItems.Contains(itemId);
            }
            return false;
        }

        /// <summary>
        /// Sets the Legion Manager reference for fan speed OSD support.
        /// Must be called after LegionManager is initialized.
        /// </summary>
        public void SetLegionManager(LegionManager legionManager)
        {
            osdItemFan.SetLegionManager(legionManager);
            osdItemTDPLimits.SetLegionManager(legionManager);
            Logger.Info("LegionManager reference set for RTSS OSD fan speed and TDP limits");
        }

        /// <summary>
        /// Sets the AutoTDP Manager reference for AutoTDP OSD support.
        /// Must be called after AutoTDPManager is initialized.
        /// </summary>
        public void SetAutoTDPManager(AutoTDPManager autoTDPManager)
        {
            osdItemAutoTDP.SetAutoTDPManager(autoTDPManager);
            Logger.Info("AutoTDPManager reference set for RTSS OSD AutoTDP status");
        }

        /// <summary>
        /// Sets the controller battery callbacks for the Controller Battery OSD item.
        /// Must be called after LegionManager is initialized.
        /// </summary>
        public void SetControllerBatteryCallbacks(Func<int> getLeftBattery, Func<int> getRightBattery,
            Func<bool> getLeftCharging, Func<bool> getRightCharging)
        {
            osdItemControllerBattery.SetCallbacks(getLeftBattery, getRightBattery, getLeftCharging, getRightCharging);
            Logger.Info("Controller battery callbacks set for RTSS OSD");
        }

        /// <summary>
        /// Initializes the DisplayOSDConfig property with the adaptive brightness callback.
        /// Must be called after SystemManager is initialized.
        /// </summary>
        public void InitializeDisplayOSDConfig(Action<bool> setAdaptiveBrightness)
        {
            displayOSDConfig = new DisplayOSDConfigProperty(this, setAdaptiveBrightness);
            Logger.Info("DisplayOSDConfig property initialized");
        }

        /// <summary>
        /// Resets the RTSS connection after hibernate/suspend resume.
        /// The OSD connection can become stale after hibernation, causing stale values.
        /// This forces the OSD to be recreated on the next Update() cycle.
        /// </summary>
        public void ResetRTSSConnection()
        {
            Logger.Info("ResetRTSSConnection: Resetting RTSS OSD connection after hibernate resume");

            // Dispose existing OSD connection
            if (rtssOSD != null)
            {
                try
                {
                    rtssOSD.Update(string.Empty); // Clear OSD content first
                    rtssOSD.Dispose();
                    Logger.Info("ResetRTSSConnection: Disposed stale RTSS OSD");
                }
                catch (Exception ex)
                {
                    Logger.Warn($"ResetRTSSConnection: Error disposing RTSS OSD: {ex.Message}");
                }
                rtssOSD = null;
            }

            // Reset state so OSD will be recreated
            rtssState = RivatunerStatisticsServerState.NotRunning;

            Logger.Info("ResetRTSSConnection: RTSS connection reset complete, OSD will be recreated on next update");
        }

        public override void Update()
        {
            base.Update();

            var isRTSSInstalled = RTSSHelper.IsInstalled();
            if (rtssInstalled.Value != isRTSSInstalled)
                rtssInstalled.SetValue(isRTSSInstalled);

            if (!isRTSSInstalled)
            {
                Logger.Debug("Rivatuner Statistics Server is not installed.");
                rtssState = RivatunerStatisticsServerState.NotInstalled;
                rtssWasRunning = false;
                return;
            }

            // Detect the RTSS not-running→running transition once (independent of OSD
            // level). On that edge, (re)initialize the FPS limiter — this recovers the
            // case where RTSS was installed/started AFTER the helper (the ctor's one-shot
            // Initialize would otherwise leave the limiter disabled until a helper
            // restart) — and re-apply the current FPS limit, which SetFPSLimit silently
            // drops while RTSS is closed.
            bool isRTSSRunning = RTSSHelper.IsRunning();
            if (isRTSSRunning && !rtssWasRunning)
            {
                Logger.Info("RTSS became available — (re)initializing FPS limiter and re-applying FPS limit.");
                RTSSFPSLimiter.Initialize();
                if (fpsLimit.Value > 0)
                {
                    RTSSFPSLimiter.SetFPSLimit(fpsLimit.Value);
                }
            }
            rtssWasRunning = isRTSSRunning;

            if (onScreenDisplayLevel == 0)
            {
                if (rtssOSD != null)
                {
                    try
                    {
                        rtssOSD.Update(string.Empty);
                        rtssOSD.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Logger.Debug($"Error clearing OSD: {ex.Message}");
                    }
                    rtssOSD = null;
                }

                return;
            }

            if (!isRTSSRunning)
            {
                if (SettingsManager.GetInstance().AutoStartRTSS)
                {
                    if (rtssState == RivatunerStatisticsServerState.Starting)
                    {
                        Logger.Info("Starting Rivatuner Statistics Server..");
                    }
                    else
                    {
                        rtssState = RivatunerStatisticsServerState.Starting;
                        try
                        {
                            Logger.Info("Start Rivatuner Statistics Server.");
                            Process.Start(RTSSHelper.ExecutablePath());
                        }
                        catch (Exception ex)
                        {
                            Logger.Error(ex, "Failed to start Rivatuner Statistics Server.");
                            rtssState = RivatunerStatisticsServerState.NotRunning;
                        }
                    }
                }
                return;
            }

            rtssState = RivatunerStatisticsServerState.Running;

            if (rtssOSD == null)
            {
                // Guard: RTSSHelper.IsRunning() can return true before RTSS has
                // mapped its shared-memory segment (startup race), or the
                // segment can vanish if RTSS is killed mid-update. The OSD
                // ctor calls openSharedMemory() which throws
                // FileNotFoundException (HRESULT 0x80070002) in that window.
                // That exception previously bubbled all the way to Main and
                // killed the helper on launch when RTSS wasn't fully up —
                // bail out of this tick instead and retry next time.
                try
                {
                    rtssOSD = new OSD(OSDAppName);
                }
                catch (Exception ex)
                {
                    Logger.Debug($"RTSS OSD unavailable (shared memory not ready): {ex.Message}");
                    rtssState = RivatunerStatisticsServerState.NotRunning;
                    return;
                }
            }

            // Update clock display settings based on config
            osdItemCPU.SetShowClock(IsItemEnabled("CPUClock"));
            osdItemGPU.SetShowClock(IsItemEnabled("GPUClock"));
            osdItemTime.Use24Hour = clock24Hour;

            // Set text color and opacity on all items
            // Apply opacity for OLED protection
            var textColorWithOpacity = osdTextColor == "DYNAMIC" ? "DYNAMIC" : ApplyOpacityToColor(osdTextColor);
            foreach (var item in osdItems)
            {
                item.SetTextColor(textColorWithOpacity);
                item.SetOpacity(osdOpacity);
            }

            // Pre-build frametime graph string if enabled (so it can be placed in order)
            // Uses RTSS native <G=<FT>> tag - RTSS handles graph rendering internally
            string frametimeGraphString = null;
            if (IsItemEnabled("FrametimeGraph"))
            {
                try
                {
                    // Get frametime statistics from RTSS AppEntry
                    UpdateFrametimeStats();

                    // Build graph using <G=<FT>> tag - processed by RTSSSharedMemoryNET.ProcessGraphTags
                    // Width/height are hardcoded in ProcessGraphTags (-32 chars, -2 lines)
                    string graphColor = (osdTextColor == "DYNAMIC" || string.IsNullOrEmpty(osdTextColor)) ? "00FFFF" : osdTextColor;
                    graphColor = ApplyOpacityToColor(graphColor);

                    // Build stats label if we have valid data
                    string statsLabel = "";
                    if (currentMinFt > 0 && currentMaxFt > 0)
                    {
                        string labelColor = ApplyOpacityToColor("808080");
                        string minColor = ApplyOpacityToColor("00FF00");
                        string avgColor = ApplyOpacityToColor("FFFF00");
                        string maxColor = ApplyOpacityToColor("FF6600");
                        // Reset to osdTextSize after the small stats label (not just <S> which resets to 100%)
                        string sizeReset = osdTextSize != 100 ? $"<S={osdTextSize}>" : "<S>";
                        statsLabel = $"\n<S=50><C={labelColor}>min:<C={minColor}>{currentMinFt:F1}ms <C={labelColor}>avg:<C={avgColor}>{currentAvgFt:F1}ms <C={labelColor}>max:<C={maxColor}>{currentMaxFt:F1}ms<C>{sizeReset}";
                    }

                    // <G=<FT>> - RTSSSharedMemoryNET.ProcessGraphTags converts this to embedded graph object
                    frametimeGraphString = $"<C={graphColor}><G=<FT>><C>{statsLabel}";
                }
                catch (Exception ex)
                {
                    Logger.Debug($"Failed to build frametime graph: {ex.Message}");
                }
            }

            // Build OSD header
            string osdString = BuildOSDHeader();

            // Apply text size if not default
            if (osdTextSize != 100)
            {
                osdString += $"<S={osdTextSize}>";
            }

            // Apply default text color (use white as base for dynamic mode)
            var baseTextColor = osdTextColor == "DYNAMIC" ? "FFFFFF" : osdTextColor;
            // Apply opacity for OLED protection
            baseTextColor = ApplyOpacityToColor(baseTextColor);
            osdString += $"<C={baseTextColor}>";

            // Collect all enabled items in custom order
            var enabledItems = new List<string>();

            // Get the order for current level (fall back to level 1 if not found)
            var order = osdLevelOrder.TryGetValue(onScreenDisplayLevel, out var levelOrder) ? levelOrder : osdLevelOrder[1];

            foreach (var itemId in order)
            {
                // Check if this item is enabled for the current level
                if (!IsItemEnabled(itemId))
                    continue;

                // Handle FrametimeGraph specially (it's not in osdItems array)
                if (itemId == "FrametimeGraph")
                {
                    // If pinned mode is enabled, skip here - we'll add it at the end
                    if (frametimeGraphPinned)
                        continue;

                    if (!string.IsNullOrEmpty(frametimeGraphString))
                    {
                        enabledItems.Add(frametimeGraphString);
                    }
                    continue;
                }

                // Find the OSD item by ID
                var item = osdItems.FirstOrDefault(i => i.Id == itemId);
                if (item == null)
                    continue;

                // Apply global label color if set (not DEFAULT)
                if (!string.IsNullOrEmpty(osdLabelColor) && osdLabelColor != "DEFAULT")
                {
                    item.SetLabelColor(osdLabelColor);
                }
                else
                {
                    item.SetLabelColor(null);  // Reset to item's default color
                }

                var osdItemString = item.GetOSDString(onScreenDisplayLevel);
                if (string.IsNullOrEmpty(osdItemString))
                    continue;

                enabledItems.Add(osdItemString);
            }

            // Add custom tags if configured for this level
            if (osdCustomTags.TryGetValue(onScreenDisplayLevel, out var customTags) && !string.IsNullOrWhiteSpace(customTags))
            {
                enabledItems.Add(customTags);
            }

            // Build output with columns - use per-level setting
            int itemsPerRow = 3; // Fallback default
            if (osdLevelColumns.TryGetValue(onScreenDisplayLevel, out int levelColumns) && levelColumns > 0)
            {
                itemsPerRow = levelColumns;
            }
            for (int i = 0; i < enabledItems.Count; i++)
            {
                if (i > 0)
                {
                    // Check if we need a newline (new row)
                    if (i % itemsPerRow == 0)
                    {
                        osdString += "\n";
                    }
                    else
                    {
                        osdString += OSDSeparator;
                    }
                }
                osdString += enabledItems[i];
            }

            // Close text size tag if used
            if (osdTextSize != 100)
            {
                osdString += "<S>";
            }

            // Add pinned frametime graph at the end on its own line
            if (frametimeGraphPinned && !string.IsNullOrEmpty(frametimeGraphString) && IsItemEnabled("FrametimeGraph"))
            {
                osdString += "\n" + frametimeGraphString;
            }

            try
            {
                rtssOSD.Update(osdString);
            }
            catch (Exception ex)
            {
                Logger.Debug($"Error updating OSD: {ex.Message}");
            }
        }

        /// <summary>
        /// Builds the OSD header string.
        /// Note: Position and background are controlled by RTSS application settings.
        /// RTSS background requires complex <B=x,y> bar drawing with \b backspace which
        /// doesn't work well with dynamic content.
        /// </summary>
        private string BuildOSDHeader()
        {
            // Apply position offset for OLED burn-in protection
            if (osdPositionOffsetX != 0 || osdPositionOffsetY != 0)
            {
                return $"<P={osdPositionOffsetX},{osdPositionOffsetY}>";
            }
            // Background not supported via simple tags - must be configured in RTSS app
            return "";
        }

        /// <summary>
        /// Sets the OSD position offset for OLED burn-in protection.
        /// Called by OLEDProtectionManager when the position shift timer fires.
        /// </summary>
        public void SetPositionOffset(int x, int y)
        {
            osdPositionOffsetX = x;
            osdPositionOffsetY = y;
            Logger.Debug($"OSD position offset set to ({x}, {y})");
        }

        /// <summary>
        /// Applies opacity to a hex color by reducing RGB values proportionally.
        /// Used for OLED protection to darken OSD colors.
        /// </summary>
        private string ApplyOpacityToColor(string hexColor)
        {
            if (osdOpacity >= 100 || string.IsNullOrEmpty(hexColor) || hexColor.Length < 6)
                return hexColor;

            try
            {
                float factor = osdOpacity / 100f;
                byte r = (byte)(Convert.ToByte(hexColor.Substring(0, 2), 16) * factor);
                byte g = (byte)(Convert.ToByte(hexColor.Substring(2, 2), 16) * factor);
                byte b = (byte)(Convert.ToByte(hexColor.Substring(4, 2), 16) * factor);
                return $"{r:X2}{g:X2}{b:X2}";
            }
            catch
            {
                return hexColor;
            }
        }

        /// <summary>
        /// Updates frametime statistics from RTSS AppEntry frametime buffer.
        /// Calculates min/avg/max from recent frames in the circular buffer.
        /// Values are in microseconds, converted to milliseconds for display.
        /// </summary>
        private void UpdateFrametimeStats()
        {
            try
            {
                var appEntries = OSD.GetAppEntries(AppFlags.MASK);
                if (appEntries == null || appEntries.Length == 0)
                {
                    currentMinFt = 0f;
                    currentAvgFt = 0f;
                    currentMaxFt = 0f;
                    return;
                }

                // Get the foreground window's process ID to prioritize that app's data
                int foregroundPid = User32.GetForegroundProcessId();
                AppEntry targetEntry = null;
                AppEntry fallbackEntry = null;

                // Find the matching app entry - prioritize foreground window
                foreach (var entry in appEntries)
                {
                    if (entry.StatFrameTimeBuf != null && entry.StatFrameTimeBuf.Length > 0)
                    {
                        if (entry.ProcessId == foregroundPid)
                        {
                            targetEntry = entry;
                            break; // Found foreground app, use it
                        }
                        // Keep track of first valid entry as fallback
                        if (fallbackEntry == null)
                        {
                            fallbackEntry = entry;
                        }
                    }
                }

                // Use foreground app if found, otherwise fallback to first valid app
                var selectedEntry = targetEntry ?? fallbackEntry;
                if (selectedEntry == null || selectedEntry.StatFrameTimeBuf == null)
                {
                    currentMinFt = 0f;
                    currentAvgFt = 0f;
                    currentMaxFt = 0f;
                    return;
                }

                // Calculate stats from recent frames in the circular buffer
                // Buffer is 1024 samples, we analyze the last 256 for recent stats
                const int sampleCount = 256;
                uint bufSize = (uint)selectedEntry.StatFrameTimeBuf.Length;
                uint bufPos = selectedEntry.StatFrameTimeBufPos;

                float minFt = float.MaxValue;
                float maxFt = 0f;
                float sumFt = 0f;
                int validSamples = 0;

                for (int i = 0; i < sampleCount; i++)
                {
                    uint srcIndex = (bufPos - (uint)sampleCount + (uint)i + bufSize) % bufSize;
                    float frametimeMs = selectedEntry.StatFrameTimeBuf[srcIndex] / 1000.0f;

                    // Only count valid samples (non-zero, reasonable range)
                    if (frametimeMs > 0.1f && frametimeMs < 1000f)
                    {
                        validSamples++;
                        sumFt += frametimeMs;
                        if (frametimeMs < minFt) minFt = frametimeMs;
                        if (frametimeMs > maxFt) maxFt = frametimeMs;
                    }
                }

                if (validSamples > 0)
                {
                    currentMinFt = minFt;
                    currentAvgFt = sumFt / validSamples;
                    currentMaxFt = maxFt;
                }
                else
                {
                    currentMinFt = 0f;
                    currentAvgFt = 0f;
                    currentMaxFt = 0f;
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"Error updating frametime stats: {ex.Message}");
                currentMinFt = 0f;
                currentAvgFt = 0f;
                currentMaxFt = 0f;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Logger.Info("RTSSManager: Disposing resources");

                // Stop position shift timer
                if (positionShiftTimer != null)
                {
                    positionShiftTimer.Dispose();
                    positionShiftTimer = null;
                    Logger.Info("RTSSManager: Position shift timer disposed");
                }

                // Shutdown FPS limiter
                RTSSFPSLimiter.Shutdown();

                if (rtssOSD != null)
                {
                    try
                    {
                        rtssOSD.Update(string.Empty);
                        rtssOSD.Dispose();
                        rtssOSD = null;
                        Logger.Info("RTSSManager: RTSS OSD disposed");
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"RTSSManager: Error disposing RTSS OSD: {ex.Message}");
                    }
                }
            }
            base.Dispose(disposing);
        }
    }
}
