using NLog;
using RTSSSharedMemoryNET;
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Shared.Data;
using XboxGamingBarHelper.Windows;
using XboxGamingBarHelper.Core;
using XboxGamingBarHelper.Settings;
using XboxGamingBarHelper.Icons;
using System.Collections.Generic;
using Shared.Utilities;
using Microsoft.Win32;

namespace XboxGamingBarHelper.Systems
{
    public delegate void ResumeFromSleepEventHandler(object sender);
    public delegate void PowerSourceChangedEventHandler(object sender, global::Windows.System.Power.PowerSupplyStatus newStatus);

    internal class SystemManager : Manager
    {
        public event ResumeFromSleepEventHandler ResumeFromSleep;

        /// <summary>
        /// Raised when the AC/DC line status transitions (Adequate ↔ NotPresent/Inadequate).
        /// Deduplicated against <see cref="lastPowerSupplyStatus"/> so battery-level ticks that
        /// also produce StatusChange callbacks don't fire this every few seconds.
        /// Needed because the widget is UWP and can't observe these events when suspended (issue #72).
        /// </summary>
        public event PowerSourceChangedEventHandler PowerSourceChanged;

        private global::Windows.System.Power.PowerSupplyStatus lastPowerSupplyStatus = global::Windows.System.Power.PowerSupplyStatus.NotPresent;
        private bool hasSeenInitialPowerSupplyStatus = false;

        // #94: SystemEvents.PowerModeChanged is unreliable under S0 Modern
        // Standby (LTSC 24H2 + Legion Go 2 logs show NO StatusChange/Resume
        // around standby cycles). Two independent backstops:
        //  - a slow poll of PowerManager.PowerSupplyStatus that converges the
        //    AC/DC state within one interval no matter which events fired;
        //  - PowerRegisterSuspendResumeNotification, the Modern-Standby-aware
        //    kernel callback, driving the same resume path as SystemEvents.
        private System.Timers.Timer powerStatusPollTimer;
        private const double PowerStatusPollIntervalMs = 10000;
        private PowrProf.DeviceNotifyCallbackRoutine suspendResumeCallback; // rooted for the registration lifetime
        private IntPtr suspendResumeNotificationHandle = IntPtr.Zero;
        private long lastResumeInvokeTicksUtc;
        private static readonly long ResumeDedupeWindowTicks = TimeSpan.FromSeconds(5).Ticks;
        private static readonly string[] IgnoredProcesses =
        {
            // Windows shell and system processes - never games
            "explorer.exe",
            "applicationframehost.exe",
            // Remote desktop tools
            "rustdesk.exe",
            "anydesk.exe",
            "parsecd.exe",
            // Game engines/editors
            "unity.exe",
            "unrealeditor.exe",
            "eacefsubprocess.exe",
            "rider64.exe",
            // Windows system apps that may render frames
            "appinstaller.exe",
            "winstore.app.exe",
            "systemsettings.exe",
            // Xbox Gaming Services - shows game info but is not a game
            "gamingservicesui.exe",
            // Monitoring/overlay tools
            "rtss.exe",
            "rivatuner.exe",
            "rivatuner statistics server.exe",
            "msiafterburner.exe",
            "hwinfo64.exe",
            "hwinfo32.exe",
            "hwinfo.exe",
            // UWP / Microsoft Store system apps. Before the #87 ApplicationFrameHost
            // resolution these all hid behind the host and were skipped by the
            // applicationframehost.exe entry above. Now that we surface the real packaged
            // exe so games like Forza Horizon 4 can be detected, these system apps need
            // explicit exclusion or they get picked up as "games" — especially when the
            // user has Games-only off.
            "windowsterminal.exe",
            "xboxpcapp.exe",            // Microsoft Store / Xbox app
            "startmenuexperiencehost.exe",
            "searchhost.exe",
            "searchapp.exe",
            "textinputhost.exe",
            "widgets.exe",
            "widgetservice.exe",
            "lockapp.exe",
            "logonui.exe",
            "cortana.exe",
            "shellexperiencehost.exe",
            "your phone.exe",
            "phoneexperiencehost.exe",
            "snippingtool.exe",
            "calculator.exe",
            "msteams.exe",
        };

        // Some games might not be detected by Xbox Game Bar, emulated games using RetroArch, MelonDS, Citra, etc.
        private static readonly string[] GameProcesses =
        {
            "azahar.exe",
            "cemu.exe",
            "citron.exe",
            "dolphin.exe",
            "duckstation-qt-x64-releaseltcg.exe",
            "duckstation.exe",
            "eden.exe",
            "melonds.exe",
            "pcsx2-qtx64.exe",
            "pcsx2-qt.exe",
            "pcsx2.exe",
            "ppssppwindows64.exe",
            "ppssppwindows.exe",
            "ppsspp.exe",
            "retroarch.exe",
            "rpcs3.exe",
            "ryujinx.exe",
            "scummvm.exe",
            "shadps4.exe",
            "vita3k.exe",
            "xemu.exe",
            "xenia_canary.exe",
        };

        private readonly RunningGameProperty runningGame;
        public RunningGameProperty RunningGame
        {
            get { return runningGame; }
        }

        private readonly RefreshRatesProperty refreshRates;
        public RefreshRatesProperty RefreshRates
        {
            get { return refreshRates; }
        }

        private readonly RefreshRateProperty refreshRate;
        public RefreshRateProperty RefreshRate
        {
            get { return refreshRate; }
        }

        private readonly ResolutionsProperty resolutions;
        public ResolutionsProperty Resolutions
        {
            get { return resolutions; }
        }

        private readonly ResolutionProperty resolution;
        public ResolutionProperty Resolution
        {
            get { return resolution; }
        }

        private readonly HDRSupportedProperty hdrSupported;
        public HDRSupportedProperty HDRSupported
        {
            get { return hdrSupported; }
        }

        private readonly HDREnabledProperty hdrEnabled;
        public HDREnabledProperty HDREnabled
        {
            get { return hdrEnabled; }
        }

        private readonly DisplayOrientationProperty displayOrientation;
        public DisplayOrientationProperty DisplayOrientation
        {
            get { return displayOrientation; }
        }

        private readonly AdaptiveBrightnessModeProperty adaptiveBrightnessMode;
        public AdaptiveBrightnessModeProperty AdaptiveBrightnessMode
        {
            get { return adaptiveBrightnessMode; }
        }

        private readonly AdaptiveBrightnessManager adaptiveBrightnessManager = new AdaptiveBrightnessManager();
        private const string AdaptiveBrightnessRequestedKey = "AdaptiveBrightnessRequested";
        private bool adaptiveBrightnessRequested;

        // CPU Core Configuration
        public int TotalPCores { get; private set; }
        public int TotalECores { get; private set; }
        public bool IsHybridCPU { get; private set; }

        private readonly CPUCoreConfigProperty cpuCoreConfig;
        public CPUCoreConfigProperty CPUCoreConfig
        {
            get { return cpuCoreConfig; }
        }

        private readonly CPUCoreActiveConfigProperty cpuCoreActiveConfig;
        public CPUCoreActiveConfigProperty CPUCoreActiveConfig
        {
            get { return cpuCoreActiveConfig; }
        }

        private readonly CoreParkingPercentProperty coreParkingPercent;
        public CoreParkingPercentProperty CoreParkingPercent
        {
            get { return coreParkingPercent; }
        }

        private readonly ForceParkModeProperty forceParkMode;
        public ForceParkModeProperty ForceParkMode
        {
            get { return forceParkMode; }
        }

        private bool isForceParkModeEnabled = false;

        private int lastCoreParkingPercent = 100;

        // Store CPU set IDs for core parking
        private List<uint> pCoreCpuSetIds = new List<uint>();
        private List<uint> eCoreCpuSetIds = new List<uint>();

        private readonly TrackedGameProperty trackedGame;
        public TrackedGameProperty TrackedGame
        {
            get { return trackedGame; }
        }

        private readonly ForegroundAppProperty foregroundApp;
        public ForegroundAppProperty ForegroundApp
        {
            get { return foregroundApp; }
        }

        // Track the last focused non-GameBar app for priority when multiple games detected
        private string lastFocusedAppPath = "";

        private IReadOnlyDictionary<GameId, GameProfile> Profiles { get; }

        // Keep track to current opening windows to determine currently running game.
        private Dictionary<int, ProcessWindow> ProcessWindows { get; }
        private Dictionary<int, AppEntry> AppEntries { get; }

        public SystemManager(IReadOnlyDictionary<GameId, GameProfile> profiles) : base()
        {
            Logger.Info("Create process windows.");
            ProcessWindows = new Dictionary<int, ProcessWindow>();
            Logger.Info("Create app entries.");
            AppEntries = new Dictionary<int, AppEntry>();
            Logger.Info("Save profiles for detecting games.");
            Profiles = profiles;

            trackedGame = new TrackedGameProperty(this);
            foregroundApp = new ForegroundAppProperty(this);
            Logger.Info("Check current running game.");
            runningGame = new RunningGameProperty(this);
            Logger.Info("Check supported refresh rates.");
            refreshRates = new RefreshRatesProperty(User32.GetSupportedRefreshRates(), this);
            Logger.Info("Check current refresh rate.");
            refreshRate = new RefreshRateProperty(User32.GetCurrentRefreshRateFromDisplayConfig(), this);
            Logger.Info("Check supported resolutions.");
            resolutions = new ResolutionsProperty(User32.GetSupportedResolutions(), this);
            Logger.Info("Check current resolution.");
            resolution = new ResolutionProperty(User32.GetCurrentResolution(), this);
            Logger.Info("Check HDR status.");
            var hdrStatus = User32.GetHDRStatus();
            hdrSupported = new HDRSupportedProperty(hdrStatus.Supported, this);
            hdrEnabled = new HDREnabledProperty(hdrStatus.Enabled, this);
            Logger.Info("Check display orientation.");
            displayOrientation = new DisplayOrientationProperty(User32.GetCurrentOrientation(), this);

            adaptiveBrightnessMode = new AdaptiveBrightnessModeProperty(this);
            // Master AB toggle lives inside the OSD/Display bundle and is only re-sent on
            // widget change — so a helper restart loses the in-memory "requested" flag.
            // Persist it locally and re-apply on startup so mode flips work after restarts.
            if (XboxGamingBarHelper.Settings.LocalSettingsHelper.TryGetValue<bool>(AdaptiveBrightnessRequestedKey, out var savedRequested))
            {
                adaptiveBrightnessRequested = savedRequested;
                if (savedRequested)
                {
                    Logger.Info($"AdaptiveBrightness: restoring requested=true on startup, mode={adaptiveBrightnessMode.Mode}");
                    ApplyAdaptiveBrightnessBackend(true, adaptiveBrightnessMode.Mode);
                }
            }

            Logger.Info("Detecting CPU core configuration.");
            DetectCPUCoreConfiguration();
            // Initialize cpuCoreConfig after detection
            string configString = $"{TotalPCores},{TotalECores},{IsHybridCPU.ToString().ToLower()}";
            cpuCoreConfig = new CPUCoreConfigProperty(configString, this);
            cpuCoreActiveConfig = new CPUCoreActiveConfigProperty(this);
            coreParkingPercent = new CoreParkingPercentProperty(this);
            forceParkMode = new ForceParkModeProperty(this);

            // Seed the AC/DC dedupe baseline from the live PowerManager status now,
            // before subscribing. Otherwise the lazy-on-first-event capture below loses
            // the *real* first AC↔DC transition: PowerModes.StatusChange fires
            // continuously for battery-percent updates, so the first one we see is
            // almost always a battery-level tick rather than the actual transition,
            // and the lazy capture would silently absorb the new state without raising
            // PowerSourceChanged.
            try
            {
                lastPowerSupplyStatus = global::Windows.System.Power.PowerManager.PowerSupplyStatus;
                hasSeenInitialPowerSupplyStatus = true;
                Logger.Debug($"SystemManager seeded initial power supply status: {lastPowerSupplyStatus}");
            }
            catch (Exception ex)
            {
                // Fall back to lazy capture on first StatusChange.
                Logger.Warn($"SystemManager: failed to seed initial PowerSupplyStatus, will capture on first StatusChange: {ex.Message}");
            }

            // Subscribe to system power events for sleep/wake detection
            SystemEvents.PowerModeChanged += SystemEvents_PowerModeChanged;
            // Subscribe to display change events for dock/undock detection
            SystemEvents.DisplaySettingsChanged += SystemEvents_DisplaySettingsChanged;

            // #94 backstop 1: poll the power supply status so AC/DC state
            // converges even when no StatusChange event is delivered (plug or
            // unplug during Modern Standby, LTSC event delivery gaps).
            powerStatusPollTimer = new System.Timers.Timer(PowerStatusPollIntervalMs);
            powerStatusPollTimer.Elapsed += (s, args) => CheckPowerSupplyStatusChange("poll");
            powerStatusPollTimer.AutoReset = true;
            powerStatusPollTimer.Start();

            // #94 backstop 2: Modern-Standby-aware suspend/resume callback.
            RegisterSuspendResumeNotification();
        }

        private void RegisterSuspendResumeNotification()
        {
            try
            {
                suspendResumeCallback = OnSuspendResumeNotification;
                var subscribeParams = new PowrProf.DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS
                {
                    Callback = suspendResumeCallback,
                    Context = IntPtr.Zero,
                };
                uint result = PowrProf.PowerRegisterSuspendResumeNotification(
                    PowrProf.DEVICE_NOTIFY_CALLBACK,
                    ref subscribeParams,
                    out suspendResumeNotificationHandle);
                if (result == 0)
                {
                    Logger.Info("Suspend/resume notification registered (Modern Standby aware)");
                }
                else
                {
                    Logger.Warn($"PowerRegisterSuspendResumeNotification failed with {result}; relying on SystemEvents + polling only");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Suspend/resume notification registration threw: {ex.Message}");
            }
        }

        private int OnSuspendResumeNotification(IntPtr context, int type, IntPtr setting)
        {
            // Kernel callback thread — keep the work identical to the
            // SystemEvents path and let HandleResume dedupe the two sources.
            try
            {
                switch (type)
                {
                    case PowrProf.PBT_APMSUSPEND:
                        Logger.Info($"System is going to sleep (kernel notification) at: {DateTime.Now}");
                        break;
                    case PowrProf.PBT_APMRESUMEAUTOMATIC:
                    case PowrProf.PBT_APMRESUMESUSPEND:
                        HandleResume("kernel notification");
                        break;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Suspend/resume kernel callback threw: {ex.Message}");
            }
            return 0;
        }

        private void HandleResume(string source)
        {
            // Both SystemEvents.Resume and the kernel callback can fire for the
            // same wake (classic S3 machines deliver both) — run the resume
            // work once per wake.
            long now = DateTime.UtcNow.Ticks;
            long last = System.Threading.Interlocked.Read(ref lastResumeInvokeTicksUtc);
            if (now - last < ResumeDedupeWindowTicks)
            {
                Logger.Debug($"Resume ({source}) within dedupe window; already handled");
                return;
            }
            System.Threading.Interlocked.Exchange(ref lastResumeInvokeTicksUtc, now);

            Logger.Info($"System resumed from sleep/hibernate ({source}) at: {DateTime.Now}");
            ResumeFromSleep?.Invoke(this);
            // Refresh display settings in case display changed during sleep
            RefreshDisplaySettings();
            // AC/DC may have changed while asleep with no StatusChange event
            // (issue #94: unplug during Modern Standby left the helper on the
            // stale power source until the next real transition).
            CheckPowerSupplyStatusChange("resume");
        }

        /// <summary>
        /// Compares the live PowerSupplyStatus against the last observed value
        /// and raises PowerSourceChanged on a real AC↔DC transition. Shared by
        /// the StatusChange event, the resume path, and the poll timer, so the
        /// dedupe baseline stays consistent regardless of which source sees the
        /// change first.
        /// </summary>
        private void CheckPowerSupplyStatusChange(string source)
        {
            try
            {
                var currentStatus = global::Windows.System.Power.PowerManager.PowerSupplyStatus;
                lock (this)
                {
                    if (!hasSeenInitialPowerSupplyStatus)
                    {
                        lastPowerSupplyStatus = currentStatus;
                        hasSeenInitialPowerSupplyStatus = true;
                        Logger.Debug($"Initial power supply status captured ({source}): {currentStatus}");
                        return;
                    }

                    if (currentStatus == lastPowerSupplyStatus)
                    {
                        return;
                    }

                    Logger.Info($"AC/DC transition ({source}): {lastPowerSupplyStatus} -> {currentStatus}");
                    lastPowerSupplyStatus = currentStatus;
                }
                PowerSourceChanged?.Invoke(this, currentStatus);
            }
            catch (Exception ex)
            {
                Logger.Warn($"Power supply status check ({source}) threw: {ex.Message}");
            }
        }

        private void SystemEvents_PowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            switch (e.Mode)
            {
                case PowerModes.Resume:
                    HandleResume("SystemEvents");
                    break;
                case PowerModes.Suspend:
                    Logger.Info($"System is going to sleep/hibernate at: {DateTime.Now}");
                    break;
                case PowerModes.StatusChange:
                    // StatusChange fires on AC/DC line transitions AND on battery percentage
                    // changes. Dedupe against the last observed PowerSupplyStatus so we only
                    // raise PowerSourceChanged on actual AC↔DC transitions.
                    CheckPowerSupplyStatusChange("event");
                    break;
            }
        }

        private void SystemEvents_DisplaySettingsChanged(object sender, EventArgs e)
        {
            Logger.Info("Display settings changed (dock/undock detected)");
            // Delay refresh to allow Windows to fully update display configuration
            // Without delay, we may query stale values (e.g., 60Hz instead of 144Hz)
            System.Threading.Tasks.Task.Delay(1000).ContinueWith(_ =>
            {
                Logger.Info("Executing delayed display refresh");
                RefreshDisplaySettings();
            });
        }

        /// <summary>
        /// Re-queries and updates display resolutions, refresh rates, and HDR status.
        /// Called when displays change (dock/undock) or on system wake.
        /// </summary>
        public void RefreshDisplaySettings()
        {
            try
            {
                // Refresh supported refresh rates
                var newRefreshRates = User32.GetSupportedRefreshRates();
                if (newRefreshRates != null && newRefreshRates.Count > 0)
                {
                    Logger.Info($"Refreshing refresh rates: {string.Join(", ", newRefreshRates)}Hz");
                    refreshRates.SetValue(newRefreshRates);
                }

                // Refresh current refresh rate (use QueryDisplayConfig for accurate value)
                var currentRate = User32.GetCurrentRefreshRateFromDisplayConfig();
                if (currentRate > 0)
                {
                    Logger.Info($"Current refresh rate: {currentRate}Hz");
                    refreshRate.SetValue(currentRate);
                }

                // Refresh supported resolutions
                var newResolutions = User32.GetSupportedResolutions();
                if (newResolutions != null && newResolutions.Count > 0)
                {
                    Logger.Info($"Refreshing resolutions: {string.Join(", ", newResolutions)}");
                    resolutions.SetValue(newResolutions);
                }

                // Refresh current resolution
                var currentRes = User32.GetCurrentResolution();
                if (!string.IsNullOrEmpty(currentRes))
                {
                    Logger.Info($"Current resolution: {currentRes}");
                    resolution.SetValue(currentRes);
                }

                // Refresh HDR status
                var hdrStatus = User32.GetHDRStatus();
                Logger.Info($"HDR status: Supported={hdrStatus.Supported}, Enabled={hdrStatus.Enabled}");
                hdrSupported.SetValue(hdrStatus.Supported);
                hdrEnabled.SetValue(hdrStatus.Enabled);

                // Refresh display orientation
                var currentOrientation = User32.GetCurrentOrientation();
                Logger.Info($"Display orientation: {currentOrientation}");
                displayOrientation.SetValue(currentOrientation);
            }
            catch (Exception ex)
            {
                Logger.Error($"Error refreshing display settings: {ex.Message}");
            }
        }

        // Diagnostic logging throttle - log summary every 30 seconds
        private DateTime _lastDiagnosticLogTime = DateTime.MinValue;
        private const int DIAGNOSTIC_LOG_INTERVAL_SECONDS = 30;
        private int _gameDetectionCallCount = 0;

        private RunningGame GetRunningGame()
        {
            _gameDetectionCallCount++;

            // Get profile detection settings
            var settings = SettingsManager.GetInstance();
            bool preferExe = settings?.ProfileMatchByExe?.Value ?? false;
            var customGamePathProperty = settings?.ProfileCustomGamePath;
            bool gamesOnly = settings?.ProfileGamesOnly?.Value ?? true;

            // Periodic diagnostic logging to avoid spam but ensure visibility
            bool shouldLogDiagnostics = (DateTime.Now - _lastDiagnosticLogTime).TotalSeconds >= DIAGNOSTIC_LOG_INTERVAL_SECONDS;
            if (shouldLogDiagnostics)
            {
                _lastDiagnosticLogTime = DateTime.Now;
                Logger.Info($"[GameDetection] Diagnostic: calls={_gameDetectionCallCount}, gamesOnly={gamesOnly}, preferExe={preferExe}");
            }

            // Helper: Get game name based on preferExe setting
            // When preferExe is true: use exe name if available, fall back to window title
            // When preferExe is false: use window title, fall back to exe name
            string GetGameName(string path, string windowTitle)
            {
                if (preferExe)
                {
                    // Prefer executable name, fall back to window title if path is empty
                    if (!string.IsNullOrEmpty(path))
                    {
                        var exeName = Path.GetFileNameWithoutExtension(path);
                        if (!string.IsNullOrEmpty(exeName))
                            return exeName;
                    }
                    // Fall back to window title
                    if (!string.IsNullOrEmpty(windowTitle))
                        return windowTitle;
                    // Last resort: try exe name again
                    return Path.GetFileNameWithoutExtension(path) ?? "";
                }
                else
                {
                    // Use window title, fall back to exe name
                    if (!string.IsNullOrEmpty(windowTitle))
                        return windowTitle;
                    return Path.GetFileNameWithoutExtension(path) ?? "";
                }
            }

            try
            {
                User32.GetOpenWindows(ProcessWindows);
            }
            catch (Exception e)
            {
                Logger.Error($"Can't get open windows: {e}");
                return new RunningGame();
            }

            if (shouldLogDiagnostics)
            {
                Logger.Info($"[GameDetection] ProcessWindows count: {ProcessWindows.Count}, TrackedGame valid: {trackedGame.IsValid()}, TrackedGame: {(trackedGame.IsValid() ? trackedGame.DisplayName : "none")}");
            }

            if (ProcessWindows.Count == 0)
            {
                if (shouldLogDiagnostics)
                {
                    Logger.Info("[GameDetection] No open windows found - returning empty");
                }
                Logger.Debug("There is not any opening window, so no game detected");
                return new RunningGame();
            }

            // Track last focused non-GameBar app for priority when multiple games detected
            foreach (var pw in ProcessWindows.Values)
            {
                if (string.IsNullOrEmpty(pw.Path)) continue;
                if (!pw.IsForeground) continue;

                // Skip Game Bar
                bool isGameBar = (pw.ProcessName ?? "").IndexOf("GameBar", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 pw.Path.IndexOf("GameBar", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!isGameBar)
                {
                    lastFocusedAppPath = pw.Path;
                    Logger.Debug($"Updated lastFocusedAppPath: {Path.GetFileName(lastFocusedAppPath)}");
                    break;
                }
            }

            // Check for custom game paths override first (blacklist doesn't apply to custom games)
            if (customGamePathProperty != null)
            {
                foreach (var processWindow in ProcessWindows)
                {
                    if (customGamePathProperty.ContainsPath(processWindow.Value.Path))
                    {
                        var gameName = GetGameName(processWindow.Value.Path, processWindow.Value.Title);
                        Logger.Debug($"Custom game match: {processWindow.Value.Path}");
                        return new RunningGame(processWindow.Value.ProcessId, gameName, processWindow.Value.Path, 0, processWindow.Value.IsForeground);
                    }
                }
            }

            AppEntries.Clear();
            AppEntry[] appEntries = Array.Empty<AppEntry>();
            if (RTSSHelper.IsRunning())
            {
                try
                {
                    appEntries = OSD.GetAppEntries(AppFlags.MASK);
                    Logger.Debug($"RTSS returned {appEntries.Length} app entries");
                    foreach (var entry in appEntries)
                    {
                        Logger.Debug($"RTSS AppEntry: ProcessId={entry.ProcessId}, Name={entry.Name}, InstantaneousFrames={entry.InstantaneousFrames}");
                    }
                }
                catch (System.IO.FileNotFoundException)
                {
                    // RTSS.exe is running but hasn't mapped its shared-memory
                    // segment yet (startup race) OR it was just killed between
                    // IsRunning() and GetAppEntries. Transient — we retry on
                    // the next tick. Downgraded from Error to Debug so the
                    // helper log isn't noisy on every reboot while the OSD
                    // attaches.
                    Logger.Debug("RTSS shared-memory segment not ready — game detection will retry next tick");
                }
                catch (Exception e)
                {
                    Logger.Error($"Can't connect to Rivatuner Statistics Server: {e}");
                }
            }
            else
            {
                Logger.Debug("Rivatuner Statistics Server is not running, can't determine current game.");
            }

            foreach (var appEntry in appEntries)
            {
                AppEntries[appEntry.ProcessId] = appEntry;
            }

            Logger.Debug($"ProcessWindows count: {ProcessWindows.Count}, AppEntries count: {AppEntries.Count}");

            // Xbox Game Bar TrackedGame: Trust it directly without window matching
            // This handles UWP/Store games that run inside ApplicationFrameHost.exe
            // Game Bar already identified this as a game, so we don't need to validate via window title/process name
            if (trackedGame.IsValid())
            {
                // Try to find the actual game process, not just any foreground window
                ProcessWindow? matchedWindow = null;

                // Extract package family prefix from AumId for UWP apps (e.g., "Microsoft.WindowsNotepad" from "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App")
                string packagePrefix = null;
                if (!string.IsNullOrEmpty(trackedGame.AumId) && trackedGame.AumId.Contains("_"))
                {
                    packagePrefix = trackedGame.AumId.Split('_')[0];
                }

                // First pass: Look for a window that matches the TrackedGame
                foreach (var pw in ProcessWindows.Values)
                {
                    if (string.IsNullOrEmpty(pw.Path)) continue;

                    // Skip Game Bar itself
                    bool isGameBar = (pw.ProcessName ?? "").IndexOf("GameBar", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     pw.Path.IndexOf("GameBar", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (isGameBar) continue;

                    // Skip ignored processes (e.g., GamingServicesUI which may show game info but is not a game)
                    var processExecutable = Path.GetFileName(pw.Path).ToLower();
                    if (IgnoredProcesses.Contains(processExecutable)) continue;

                    bool isMatch = false;

                    // For UWP apps: First try matching by package family name in path
                    if (!string.IsNullOrEmpty(packagePrefix))
                    {
                        // UWP apps from WindowsApps folder have package name in path
                        if (pw.Path.IndexOf(packagePrefix, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            isMatch = true;
                        }
                        // Note: UWP apps via ApplicationFrameHost.exe won't match by path,
                        // so we fall through to DisplayName matching below
                    }

                    // For all apps (including UWP when path didn't match): Try DisplayName matching
                    // This handles UWP apps running via ApplicationFrameHost.exe which don't expose package path
                    if (!isMatch && !string.IsNullOrEmpty(trackedGame.DisplayName))
                    {
                        // Check if window title contains the game name (common for game windows)
                        if (!string.IsNullOrEmpty(pw.Title) &&
                            pw.Title.IndexOf(trackedGame.DisplayName, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            isMatch = true;
                        }
                        // Check if process name matches (e.g., "citron" in "citron.exe")
                        else if (!string.IsNullOrEmpty(pw.ProcessName))
                        {
                            // Extract first word from DisplayName for matching (e.g., "citron" from "citron Nightly | 2028150eb")
                            var firstWord = trackedGame.DisplayName.Split(' ')[0];
                            if (pw.ProcessName.IndexOf(firstWord, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                isMatch = true;
                            }
                        }
                    }

                    if (isMatch)
                    {
                        // Prefer foreground window, but accept background if it's the only match
                        if (pw.IsForeground)
                        {
                            matchedWindow = pw;
                            break; // Foreground match is best, stop searching
                        }
                        else if (!matchedWindow.HasValue)
                        {
                            matchedWindow = pw; // Keep looking for foreground
                        }
                    }
                }

                if (matchedWindow.HasValue)
                {
                    var mw = matchedWindow.Value;
                    // Get FPS from RTSS if available
                    uint fps = 0;
                    if (AppEntries.TryGetValue(mw.ProcessId, out var appEntry))
                    {
                        fps = appEntry.InstantaneousFrames;
                    }

                    // If TrackedGame matched a non-rendering window (FPS=0), check if an actual
                    // game is still running with FPS > 0. This prevents switching away from a real
                    // game (e.g., Hollow Knight) to a non-game app (e.g., Notepad) just because
                    // Game Bar tracked a focus change. Fall through to normal FPS-based detection.
                    if (fps == 0)
                    {
                        bool hasGameWithFPS = false;
                        foreach (var pw in ProcessWindows.Values)
                        {
                            if (AppEntries.TryGetValue(pw.ProcessId, out var otherEntry) && otherEntry.InstantaneousFrames > 0)
                            {
                                hasGameWithFPS = true;
                                break;
                            }
                        }
                        if (hasGameWithFPS)
                        {
                            Logger.Info($"TrackedGame \"{trackedGame.DisplayName}\" matched but has FPS=0, another game has FPS > 0 - falling through to normal detection");
                            // Fall through to normal game detection below
                        }
                        else
                        {
                            // Use actual window title (or exe name) for the game name, NOT trackedGame.DisplayName.
                            // Xbox Game Bar's DisplayName comes from MSIX metadata and may contain punctuation
                            // (e.g., "Hollow Knight: Silksong") that differs from the window title ("Hollow Knight Silksong").
                            // Using DisplayName causes profile name mismatches between helper and widget.
                            var gameName = GetGameName(mw.Path, mw.Title);
                            Logger.Info($"TrackedGame \"{trackedGame.DisplayName}\" matched to ProcessId={mw.ProcessId} Path={mw.Path} FPS={fps} Foreground={mw.IsForeground} -> GameName={gameName}");
                            return new RunningGame(mw.ProcessId, gameName, mw.Path, fps, mw.IsForeground);
                        }
                    }
                    else
                    {
                        // TrackedGame has FPS > 0, it's actively rendering - use it
                        // Use actual window title for naming consistency (see comment above)
                        var gameName = GetGameName(mw.Path, mw.Title);
                        Logger.Info($"TrackedGame \"{trackedGame.DisplayName}\" matched to ProcessId={mw.ProcessId} Path={mw.Path} FPS={fps} Foreground={mw.IsForeground} -> GameName={gameName}");
                        return new RunningGame(mw.ProcessId, gameName, mw.Path, fps, mw.IsForeground);
                    }
                }
                else
                {
                    // No matching window found - the game might be minimized or has actually closed
                    if (shouldLogDiagnostics)
                    {
                        Logger.Info($"[GameDetection] TrackedGame \"{trackedGame.DisplayName}\" valid but no window match (AumId={trackedGame.AumId})");
                    }
                    Logger.Debug($"TrackedGame \"{trackedGame.DisplayName}\" is valid but no matching window found (AumId={trackedGame.AumId})");
                }
            }

            var possibleGames = new List<RunningGame>();

            // Check if Game Bar is the current foreground app
            bool gameBarIsForeground = false;
            foreach (var pw in ProcessWindows.Values)
            {
                if (!pw.IsForeground) continue;
                bool isGameBar = (pw.ProcessName ?? "").IndexOf("GameBar", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 (pw.Path ?? "").IndexOf("GameBar", StringComparison.OrdinalIgnoreCase) >= 0;
                if (isGameBar)
                {
                    gameBarIsForeground = true;
                    Logger.Debug("Game Bar is currently foreground");
                    break;
                }
            }

            if (ProcessWindows.Count > 0)
            {
                foreach (var processWindow in ProcessWindows)
                {
                    var processPath = processWindow.Value.Path;
                    var processExecutable = Path.GetFileName(processPath).ToLower();

                    // Skip Game Bar itself - it shouldn't be detected as a game
                    bool isGameBar = (processWindow.Value.ProcessName ?? "").IndexOf("GameBar", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     processPath.IndexOf("GameBar", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (isGameBar)
                    {
                        continue;
                    }

                    if (IgnoredProcesses.Contains(processExecutable))
                    {
                        Logger.Debug($"Window {processWindow.Value.Path} is ignored");
                        continue;
                    }

                    // Get FPS from RTSS if available
                    uint fps = 0;
                    if (AppEntries.TryGetValue(processWindow.Value.ProcessId, out var appEntry))
                    {
                        fps = appEntry.InstantaneousFrames;
                    }

                    // GamesOnly mode: User profiles are always trusted.
                    // For other apps, gamesOnly ON requires FPS > 0 to be considered a game.
                    bool hasFPS = fps > 0;

                    // Check for existing profile - try both exe name and window title based on preferExe setting
                    // If user created a profile for this app, trust it as a game regardless of gamesOnly setting
                    var profileGameName = GetGameName(processWindow.Value.Path, processWindow.Value.Title);
                    if (Profiles.ContainsKey(new GameId(profileGameName, processWindow.Value.Path)))
                    {
                        // User-created profile is always trusted as a game - no FPS check needed
                        Logger.Debug($"Found window \"{processWindow.Value.Title}\" running {(processWindow.Value.IsForeground ? "foreground" : "background")} process id {processWindow.Key} at path \"{processWindow.Value.Path}\" named \"{processWindow.Value.ProcessName}\" has profile, use it (FPS={fps}).");
                        possibleGames.Add(new RunningGame(processWindow.Value.ProcessId, profileGameName, processWindow.Value.Path, fps, processWindow.Value.IsForeground));
                        continue;
                    }

                    // Fallback TrackedGame matching (if early return didn't find a match)
                    // This uses the OLD matching logic which removes spaces and compares full display name
                    // TrackedGame from Xbox Game Bar is always trusted as a game - no FPS check needed
                    if (trackedGame.IsValid())
                    {
                        bool matchesByTitle = !string.IsNullOrEmpty(processWindow.Value.Title) &&
                            processWindow.Value.Title.Equals(trackedGame.DisplayName, StringComparison.OrdinalIgnoreCase);
                        bool matchesByProcessName = !string.IsNullOrEmpty(trackedGame.DisplayName) &&
                            processWindow.Value.ProcessName.Replace(" ", "").IndexOf(
                                trackedGame.DisplayName.Replace(" ", ""), StringComparison.OrdinalIgnoreCase) >= 0;

                        if (matchesByTitle || matchesByProcessName)
                        {
                            // Use actual window title for naming consistency (not trackedGame.DisplayName)
                            var gameName = GetGameName(processWindow.Value.Path, processWindow.Value.Title);
                            Logger.Debug($"Found window \"{processWindow.Value.Title}\" running {(processWindow.Value.IsForeground ? "foreground" : "background")} process id {processWindow.Key} at path \"{processWindow.Value.Path}\" named \"{processWindow.Value.ProcessName}\" matches TrackedGame \"{gameName}\" (byTitle={matchesByTitle}, byProcess={matchesByProcessName}, FPS={fps}).");
                            possibleGames.Add(new RunningGame(processWindow.Value.ProcessId, gameName, processWindow.Value.Path, fps, processWindow.Value.IsForeground));
                            continue;
                        }
                    }

                    // Check RTSS entry for FPS-based detection
                    if (hasFPS)
                    {
                        // App has FPS > 0, it's a game
                        var gameName = GetGameName(processWindow.Value.Path, processWindow.Value.Title);
                        Logger.Debug($"Found window \"{processWindow.Value.Title}\" running {(processWindow.Value.IsForeground ? "foreground" : "background")} process id {processWindow.Key} at path \"{processWindow.Value.Path}\" named \"{processWindow.Value.ProcessName}\" has {fps} FPS, use it.");
                        possibleGames.Add(new RunningGame(processWindow.Value.ProcessId, gameName, processWindow.Value.Path, fps, processWindow.Value.IsForeground));
                        continue;
                    }

                    // When gamesOnly is OFF, any foreground app qualifies as a game
                    // Also include the last focused app if:
                    // 1. Game Bar is currently foreground, OR
                    // 2. No window has focus detected (Game Bar overlay may not show in ProcessWindows)
                    bool isLastFocusedApp = !string.IsNullOrEmpty(lastFocusedAppPath) &&
                                            processPath.Equals(lastFocusedAppPath, StringComparison.OrdinalIgnoreCase);
                    bool useLastFocused = isLastFocusedApp && (gameBarIsForeground || !ProcessWindows.Values.Any(pw => pw.IsForeground));

                    if (!gamesOnly && (processWindow.Value.IsForeground || useLastFocused))
                    {
                        var gameName = GetGameName(processWindow.Value.Path, processWindow.Value.Title);
                        if (useLastFocused && !processWindow.Value.IsForeground)
                        {
                            Logger.Info($"GamesOnly OFF: Last focused app \"{processWindow.Value.Title}\" at path \"{processWindow.Value.Path}\" treated as game (no foreground window detected).");
                        }
                        else
                        {
                            Logger.Info($"GamesOnly OFF: Foreground window \"{processWindow.Value.Title}\" at path \"{processWindow.Value.Path}\" treated as game.");
                        }
                        possibleGames.Add(new RunningGame(processWindow.Value.ProcessId, gameName, processWindow.Value.Path, 0, processWindow.Value.IsForeground || useLastFocused));
                        continue;
                    }

                    // GameProcesses list (emulators) - always detect as games since they're explicitly whitelisted
                    // These are known gaming applications that Xbox Game Bar doesn't recognize
                    if (GameProcesses.Contains(processExecutable))
                    {
                        var gameName = GetGameName(processWindow.Value.Path, processWindow.Value.Title);
                        Logger.Debug($"Found window \"{processWindow.Value.Title}\" running {(processWindow.Value.IsForeground ? "foreground" : "background")} process id {processWindow.Key} at path \"{processPath}\" named \"{processWindow.Value.ProcessName}\" in pre-defined list.");
                        possibleGames.Add(new RunningGame(processWindow.Value.ProcessId, gameName, processPath, 0, processWindow.Value.IsForeground));
                        continue;
                    }

                    Logger.Debug($"Window \"{processWindow.Value.Title}\" at path {processWindow.Value.Path} doesn't have profile nor FPS.");
                }
            }

            if (possibleGames.Count == 0)
            {
                if (shouldLogDiagnostics)
                {
                    Logger.Info($"[GameDetection] No games found - returning empty (windows={ProcessWindows.Count}, gamesOnly={gamesOnly})");
                }
                Logger.Debug("Not found any game running.");
                return new RunningGame();
            }
            else if (possibleGames.Count == 1)
            {
                if (shouldLogDiagnostics)
                {
                    Logger.Info($"[GameDetection] Single game found: {possibleGames[0].GameId.Name} at {possibleGames[0].GameId.Path}");
                }
                Logger.Debug($"Found single running game {possibleGames[0].GameId.Name}.");
                return possibleGames[0];
            }
            else
            {
                // Log all possible games for debugging
                Logger.Info($"Multiple possible games detected ({possibleGames.Count}), lastFocused={Path.GetFileName(lastFocusedAppPath)}:");
                foreach (var pg in possibleGames)
                {
                    bool isLastFocused = !string.IsNullOrEmpty(lastFocusedAppPath) &&
                                         pg.GameId.Path.Equals(lastFocusedAppPath, StringComparison.OrdinalIgnoreCase);
                    Logger.Info($"  - {pg.GameId.Name} (FPS={pg.FPS}, Foreground={pg.IsForeground}, LastFocused={isLastFocused})");
                }

                // First priority: games with FPS > 0 (actually rendering frames)
                var gamesWithFPS = possibleGames.Where(g => g.FPS > 0).ToList();

                if (gamesWithFPS.Count == 1)
                {
                    Logger.Info($"Selected only game with FPS: {gamesWithFPS[0].GameId.Name} (FPS={gamesWithFPS[0].FPS})");
                    return gamesWithFPS[0];
                }
                else if (gamesWithFPS.Count > 1)
                {
                    // Multiple games with FPS - prefer last focused
                    if (!string.IsNullOrEmpty(lastFocusedAppPath))
                    {
                        foreach (var game in gamesWithFPS)
                        {
                            if (game.GameId.Path.Equals(lastFocusedAppPath, StringComparison.OrdinalIgnoreCase))
                            {
                                Logger.Info($"Selected last focused game with FPS: {game.GameId.Name} (FPS={game.FPS})");
                                return game;
                            }
                        }
                    }

                    // No last focused match, return first game with FPS
                    Logger.Info($"Selected first game with FPS: {gamesWithFPS[0].GameId.Name} (FPS={gamesWithFPS[0].FPS})");
                    return gamesWithFPS[0];
                }

                // No games with FPS - fall back to last focused or first game
                if (!string.IsNullOrEmpty(lastFocusedAppPath))
                {
                    foreach (var possibleGame in possibleGames)
                    {
                        if (possibleGame.GameId.Path.Equals(lastFocusedAppPath, StringComparison.OrdinalIgnoreCase))
                        {
                            Logger.Info($"Selected last focused game (no FPS): {possibleGame.GameId.Name}");
                            return possibleGame;
                        }
                    }
                }

                Logger.Info($"Selected first game (no FPS match): {possibleGames[0].GameId.Name}");
                return possibleGames[0];
            }
        }

        // Idle-throttling state for game detection. EnumWindows + per-window
        // Process.MainModule lookups are the second-largest contributor to helper
        // idle CPU. When nothing is running and Xbox Game Bar doesn't have a
        // TrackedGame either, only sample every IdleThrottleTicks (5s) instead of
        // every tick (1s). Resumes 1Hz immediately on TrackedGame becoming valid.
        private int idleTickCount;
        private const int IdleEnterThreshold = 5;   // after 5s of no-game, start skipping
        private const int IdleThrottleTicks = 5;    // sample once every 5 ticks while idle

        public override void Update()
        {
            base.Update();

            bool isIdleCandidate = !RunningGame.Value.GameId.IsValid() && !trackedGame.IsValid();
            if (isIdleCandidate)
            {
                idleTickCount++;
                if (idleTickCount > IdleEnterThreshold && (idleTickCount % IdleThrottleTicks) != 0)
                {
                    // Throttled tick — skip the expensive EnumWindows walk. State is
                    // already "no game"; if a game appears we'll catch it on the next
                    // un-skipped tick (≤5s lag), or instantly when TrackedGame fires.
                    return;
                }
            }
            else
            {
                idleTickCount = 0;
            }

            var currentRunningGame = GetRunningGame();
            var previousRunningGame = RunningGame.Value;

            // RunningGame equality now compares both GameId and IsForeground
            if (previousRunningGame != currentRunningGame)
            {
                Logger.Info($"[GameDetection] State change: prev={previousRunningGame.GameId.Name ?? "none"} -> curr={currentRunningGame.GameId.Name ?? "none"}");
                bool gameChanged = previousRunningGame.GameId != currentRunningGame.GameId;
                bool foregroundChanged = previousRunningGame.IsForeground != currentRunningGame.IsForeground;

                if (gameChanged)
                {
                    if (currentRunningGame.GameId.IsValid())
                    {
                        Logger.Info($"Detect new running game {currentRunningGame.GameId.Name}.");

                        // Try to get cached icon first (synchronous, fast)
                        var exePath = currentRunningGame.GameId.Path;
                        var cachedIconPath = GameIconHelper.GetCachedIconPath(exePath);

                        if (!string.IsNullOrEmpty(cachedIconPath))
                        {
                            // Icon already cached - include it in the RunningGame
                            currentRunningGame.GameId = new GameId(
                                currentRunningGame.GameId.Name,
                                currentRunningGame.GameId.Path,
                                cachedIconPath);
                            Logger.Info($"Using cached icon: {cachedIconPath}");
                        }
                        else
                        {
                            // Extract icon asynchronously for future use
                            Task.Run(() =>
                            {
                                try
                                {
                                    var iconPath = GameIconHelper.ExtractAndCacheIcon(exePath);
                                    if (!string.IsNullOrEmpty(iconPath))
                                    {
                                        Logger.Info($"Game icon extracted: {iconPath}");

                                        // Only send update if the game is still running
                                        if (RunningGame.Value.GameId.Path == exePath)
                                        {
                                            var updatedGame = RunningGame.Value;
                                            updatedGame.GameId = new GameId(
                                                updatedGame.GameId.Name,
                                                updatedGame.GameId.Path,
                                                iconPath);
                                            RunningGame.ForceSetValue(updatedGame);
                                            Logger.Info($"Sent icon update to widget");
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Logger.Debug($"Failed to extract icon for {exePath}: {ex.Message}");
                                }
                            });
                        }
                    }
                    else
                    {
                        Logger.Info($"Running game {previousRunningGame.GameId.Name} stopped.");
                    }
                }
                else if (foregroundChanged)
                {
                    Logger.Info($"Game {currentRunningGame.GameId.Name} foreground status changed to {currentRunningGame.IsForeground}.");

                    // Preserve the IconPath from the previous RunningGame since GetRunningGame() doesn't include it
                    if (!string.IsNullOrEmpty(previousRunningGame.GameId.IconPath))
                    {
                        currentRunningGame.GameId = new GameId(
                            currentRunningGame.GameId.Name,
                            currentRunningGame.GameId.Path,
                            previousRunningGame.GameId.IconPath);
                    }
                }
                RunningGame.SetValue(currentRunningGame);
            }
        }

        #region CPU Core Detection and Configuration

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemCpuSetInformation(
            IntPtr Information,
            uint BufferLength,
            out uint ReturnedLength,
            IntPtr Process,
            uint Flags);

        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_CPU_SET_INFORMATION
        {
            public uint Size;
            public uint Type;  // CpuSetInformation = 0
            public uint Id;
            public ushort Group;
            public byte LogicalProcessorIndex;
            public byte CoreIndex;
            public byte LastLevelCacheIndex;
            public byte NumaNodeIndex;
            public byte EfficiencyClass;  // 0 = E-Core, 1 = P-Core (on Intel/AMD hybrid)
            public byte AllFlags;
            public uint Reserved;
            public ulong AllocationTag;
        }

        // Store logical processor indices for each core type
        private List<int> pCoreLogicalProcessors = new List<int>();
        private List<int> eCoreLogicalProcessors = new List<int>();

        private void DetectCPUCoreConfiguration()
        {
            try
            {
                // Clear previous data
                pCoreCpuSetIds.Clear();
                eCoreCpuSetIds.Clear();
                pCoreLogicalProcessors.Clear();
                eCoreLogicalProcessors.Clear();

                // First call to get required buffer size
                uint bufferSize = 0;
                GetSystemCpuSetInformation(IntPtr.Zero, 0, out bufferSize, IntPtr.Zero, 0);

                if (bufferSize == 0)
                {
                    Logger.Warn("GetSystemCpuSetInformation returned 0 buffer size");
                    TotalPCores = Environment.ProcessorCount;
                    TotalECores = 0;
                    IsHybridCPU = false;
                    return;
                }

                IntPtr buffer = Marshal.AllocHGlobal((int)bufferSize);
                try
                {
                    if (!GetSystemCpuSetInformation(buffer, bufferSize, out uint returnedLength, IntPtr.Zero, 0))
                    {
                        int error = Marshal.GetLastWin32Error();
                        Logger.Error($"GetSystemCpuSetInformation failed with error: {error}");
                        TotalPCores = Environment.ProcessorCount;
                        TotalECores = 0;
                        IsHybridCPU = false;
                        return;
                    }

                    // Parse the results
                    int structSize = Marshal.SizeOf<SYSTEM_CPU_SET_INFORMATION>();
                    int numEntries = (int)(returnedLength / structSize);

                    int pCoreCount = 0;
                    int eCoreCount = 0;
                    var coreEfficiencies = new HashSet<byte>();
                    var processedCores = new HashSet<int>(); // Track unique cores by CoreIndex

                    // Temporary storage for CPU set IDs per core type
                    var pCoreIds = new List<uint>();
                    var eCoreIds = new List<uint>();
                    var pCoreProcs = new List<int>();
                    var eCoreProcs = new List<int>();

                    IntPtr current = buffer;
                    for (int i = 0; i < numEntries; i++)
                    {
                        var info = Marshal.PtrToStructure<SYSTEM_CPU_SET_INFORMATION>(current);

                        // Track CPU set IDs and logical processor indices by core type
                        if (info.EfficiencyClass == 0)
                        {
                            eCoreIds.Add(info.Id);
                            eCoreProcs.Add(info.LogicalProcessorIndex);
                        }
                        else
                        {
                            pCoreIds.Add(info.Id);
                            pCoreProcs.Add(info.LogicalProcessorIndex);
                        }

                        // Count unique physical cores
                        int coreKey = (info.Group << 8) | info.CoreIndex;
                        if (!processedCores.Contains(coreKey))
                        {
                            processedCores.Add(coreKey);
                            coreEfficiencies.Add(info.EfficiencyClass);

                            if (info.EfficiencyClass == 0)
                            {
                                eCoreCount++;
                            }
                            else
                            {
                                pCoreCount++;
                            }
                        }

                        current = IntPtr.Add(current, (int)info.Size > 0 ? (int)info.Size : structSize);
                    }

                    // Store the CPU set IDs and logical processor indices
                    pCoreCpuSetIds = pCoreIds;
                    eCoreCpuSetIds = eCoreIds;
                    pCoreLogicalProcessors = pCoreProcs;
                    eCoreLogicalProcessors = eCoreProcs;

                    // Determine if this is a hybrid CPU (has both efficiency classes)
                    IsHybridCPU = coreEfficiencies.Count > 1;

                    if (IsHybridCPU)
                    {
                        TotalPCores = pCoreCount;
                        TotalECores = eCoreCount;
                        Logger.Info($"Hybrid CPU detected: {TotalPCores} P-Cores ({pCoreCpuSetIds.Count} threads), {TotalECores} E-Cores ({eCoreCpuSetIds.Count} threads)");
                        Logger.Info($"P-Core logical processors: {string.Join(",", pCoreLogicalProcessors)}");
                        Logger.Info($"E-Core logical processors: {string.Join(",", eCoreLogicalProcessors)}");
                    }
                    else
                    {
                        // Non-hybrid CPU - all cores are the same type
                        TotalPCores = processedCores.Count;
                        TotalECores = 0;
                        Logger.Info($"Non-hybrid CPU detected: {TotalPCores} cores total");
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to detect CPU core configuration: {ex.Message}");
                TotalPCores = Environment.ProcessorCount;
                TotalECores = 0;
                IsHybridCPU = false;
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessAffinityMask(IntPtr hProcess, UIntPtr dwProcessAffinityMask);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        private const uint PROCESS_SET_INFORMATION = 0x0200;
        private const uint PROCESS_QUERY_INFORMATION = 0x0400;

        // Store the current active core configuration
        private int currentActivePCores = 0;
        private int currentActiveECores = 0;

        /// <summary>
        /// Applies core configuration by setting process affinity on the running game.
        /// This restricts which cores the game can use.
        /// </summary>
        /// <param name="activePCores">Number of P-Cores to enable (0 = disable P-Cores)</param>
        /// <param name="activeECores">Number of E-Cores to enable (0 = disable E-Cores)</param>
        public void ApplyCoreConfiguration(int activePCores, int activeECores)
        {
            if (!IsHybridCPU)
            {
                Logger.Warn("Cannot apply core configuration on non-hybrid CPU");
                return;
            }

            // Validate: can't have both be 0 (need at least one core type)
            if (activePCores <= 0 && activeECores <= 0)
            {
                Logger.Error("Invalid configuration: cannot disable both P-Cores and E-Cores");
                return;
            }

            // Store the configuration for applying to games
            currentActivePCores = activePCores;
            currentActiveECores = activeECores;

            // 0 = disable those cores, max value or above = use all
            int pCoresToUse = activePCores <= 0 ? 0 : Math.Min(activePCores, TotalPCores);
            int eCoresToUse = activeECores <= 0 ? 0 : Math.Min(activeECores, TotalECores);

            Logger.Info($"Core configuration set: {pCoresToUse}/{TotalPCores} P-Cores, {eCoresToUse}/{TotalECores} E-Cores");

            // Check if all cores are enabled - skip process manipulation to avoid anticheat triggers
            bool allCoresEnabled = (pCoresToUse >= TotalPCores && eCoresToUse >= TotalECores);

            // If Force Park Mode is enabled, re-apply affinity to ALL processes with new settings
            if (isForceParkModeEnabled)
            {
                if (allCoresEnabled)
                {
                    Logger.Info("All cores enabled with Force Park Mode - resetting all process affinities");
                    ResetAffinityForAllProcesses();
                }
                else
                {
                    Logger.Info("Force Park Mode is enabled, re-applying affinity to all processes with new config");
                    ApplyAffinityToAllProcesses();
                }
            }
            // Apply to currently running game if any (only if cores are restricted)
            else if (!allCoresEnabled && RunningGame.Value.IsValid())
            {
                ApplyAffinityToProcess(RunningGame.Value.ProcessId, pCoresToUse, eCoresToUse);
            }
            else if (allCoresEnabled)
            {
                Logger.Info("All cores enabled, no affinity change needed (anticheat safe)");
            }
            else
            {
                Logger.Info("No game running, configuration will be applied when a game starts");
            }
        }

        /// <summary>
        /// Applies the stored core configuration to a specific process.
        /// Called when a new game is detected.
        /// </summary>
        public void ApplyAffinityToRunningGame()
        {
            // Only apply if we have a valid config (at least one core type must be active)
            if (!IsHybridCPU || (currentActivePCores <= 0 && currentActiveECores <= 0))
            {
                return;
            }

            // Skip if all cores are enabled - no need to touch the game process
            // This avoids potential anticheat triggers when no restriction is needed
            if (currentActivePCores >= TotalPCores && currentActiveECores >= TotalECores)
            {
                Logger.Info("All cores enabled, skipping affinity change (anticheat safe)");
                return;
            }

            if (RunningGame.Value.IsValid())
            {
                // 0 = disable those cores, max value or above = use all
                int pCoresToUse = currentActivePCores <= 0 ? 0 : Math.Min(currentActivePCores, TotalPCores);
                int eCoresToUse = currentActiveECores <= 0 ? 0 : Math.Min(currentActiveECores, TotalECores);

                ApplyAffinityToProcess(RunningGame.Value.ProcessId, pCoresToUse, eCoresToUse);
            }
        }

        private void ApplyAffinityToProcess(int processId, int pCoresToUse, int eCoresToUse)
        {
            try
            {
                // Calculate how many threads per P-Core (typically 2 for SMT)
                int threadsPerPCore = pCoreLogicalProcessors.Count > 0 && TotalPCores > 0
                    ? pCoreLogicalProcessors.Count / TotalPCores
                    : 1;

                // Calculate how many threads per E-Core
                int threadsPerECore = eCoreLogicalProcessors.Count > 0 && TotalECores > 0
                    ? eCoreLogicalProcessors.Count / TotalECores
                    : 1;

                // Build affinity mask from logical processor indices
                ulong affinityMask = 0;

                // Add P-Core threads
                int pThreadsToAdd = pCoresToUse * threadsPerPCore;
                for (int i = 0; i < pThreadsToAdd && i < pCoreLogicalProcessors.Count; i++)
                {
                    affinityMask |= (1UL << pCoreLogicalProcessors[i]);
                }

                // Add E-Core threads
                int eThreadsToAdd = eCoresToUse * threadsPerECore;
                for (int i = 0; i < eThreadsToAdd && i < eCoreLogicalProcessors.Count; i++)
                {
                    affinityMask |= (1UL << eCoreLogicalProcessors[i]);
                }

                Logger.Info($"Applying affinity to process {processId}: {pCoresToUse}P ({pThreadsToAdd} threads) + {eCoresToUse}E ({eThreadsToAdd} threads), mask=0x{affinityMask:X}");

                if (affinityMask == 0)
                {
                    Logger.Error("Affinity mask is 0, aborting");
                    return;
                }

                // Open the process with required permissions
                IntPtr hProcess = OpenProcess(PROCESS_SET_INFORMATION | PROCESS_QUERY_INFORMATION, false, processId);
                if (hProcess == IntPtr.Zero)
                {
                    int error = Marshal.GetLastWin32Error();
                    Logger.Error($"Failed to open process {processId}, error: {error}");
                    return;
                }

                try
                {
                    // Set the process affinity mask
                    bool result = SetProcessAffinityMask(hProcess, new UIntPtr(affinityMask));
                    if (result)
                    {
                        Logger.Info($"Successfully set affinity for process {processId}: {pCoresToUse}P + {eCoresToUse}E cores");
                    }
                    else
                    {
                        int error = Marshal.GetLastWin32Error();
                        Logger.Error($"Failed to set affinity for process {processId}, error: {error}");
                    }
                }
                finally
                {
                    CloseHandle(hProcess);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error applying affinity to process {processId}: {ex.Message}");
            }
        }

        #endregion

        #region Core Parking

        /// <summary>
        /// Applies core parking using powercfg CPMAXCORES and related aggressive settings.
        /// This tells Windows the maximum percentage of cores that can be unparked
        /// and uses additional settings to encourage actual parking.
        /// When percent is 100, resets to default Windows behavior.
        /// </summary>
        /// <param name="percent">Percentage of cores to keep active (1-100)</param>
        public void ApplyCoreParkingPercent(int percent)
        {
            // Clamp to valid range
            percent = Math.Max(1, Math.Min(100, percent));

            // Skip if unchanged
            if (percent == lastCoreParkingPercent)
            {
                return;
            }

            lastCoreParkingPercent = percent;

            try
            {
                if (percent >= 100)
                {
                    // Reset to Windows defaults - all cores active, normal parking behavior
                    RunPowerCfgCommand("/setacvalueindex scheme_current sub_processor CPMAXCORES 100");
                    RunPowerCfgCommand("/setdcvalueindex scheme_current sub_processor CPMAXCORES 100");
                    RunPowerCfgCommand("/setacvalueindex scheme_current sub_processor CPMINCORES 100");
                    RunPowerCfgCommand("/setdcvalueindex scheme_current sub_processor CPMINCORES 100");
                    RunPowerCfgCommand("/setacvalueindex scheme_current sub_processor CPHEADROOM 10");
                    RunPowerCfgCommand("/setdcvalueindex scheme_current sub_processor CPHEADROOM 10");
                    RunPowerCfgCommand("/setacvalueindex scheme_current sub_processor CPCONCURRENCY 50");
                    RunPowerCfgCommand("/setdcvalueindex scheme_current sub_processor CPCONCURRENCY 50");
                    RunPowerCfgCommand("/setacvalueindex scheme_current sub_processor CPDISTRIBUTION 1");
                    RunPowerCfgCommand("/setdcvalueindex scheme_current sub_processor CPDISTRIBUTION 1");
                    RunPowerCfgCommand("/setactive scheme_current");
                    Logger.Info("Core parking reset to Windows defaults (all cores active)");
                }
                else
                {
                    // Apply aggressive parking settings
                    // CPMAXCORES: Maximum percentage of cores that can be unparked (our target)
                    RunPowerCfgCommand($"/setacvalueindex scheme_current sub_processor CPMAXCORES {percent}");
                    RunPowerCfgCommand($"/setdcvalueindex scheme_current sub_processor CPMAXCORES {percent}");

                    // CPMINCORES: Minimum percentage of cores that must stay unparked
                    // Set to same as max to force the exact number of cores we want
                    RunPowerCfgCommand($"/setacvalueindex scheme_current sub_processor CPMINCORES {percent}");
                    RunPowerCfgCommand($"/setdcvalueindex scheme_current sub_processor CPMINCORES {percent}");

                    // CPHEADROOM: Performance headroom threshold before unparking additional cores (0-100)
                    // Higher value = more reluctant to unpark cores (default is usually 10)
                    // Set to 100 to make Windows very reluctant to unpark beyond our limit
                    RunPowerCfgCommand("/setacvalueindex scheme_current sub_processor CPHEADROOM 100");
                    RunPowerCfgCommand("/setdcvalueindex scheme_current sub_processor CPHEADROOM 100");

                    // CPCONCURRENCY: Concurrency threshold before unparking (0-100)
                    // Higher value = requires more concurrent threads before unparking
                    // Set to 100 to discourage unparking
                    RunPowerCfgCommand("/setacvalueindex scheme_current sub_processor CPCONCURRENCY 100");
                    RunPowerCfgCommand("/setdcvalueindex scheme_current sub_processor CPCONCURRENCY 100");

                    // CPDISTRIBUTION: Whether to distribute utility across parked cores (0=disabled, 1=enabled)
                    // Disable to keep parked cores truly parked
                    RunPowerCfgCommand("/setacvalueindex scheme_current sub_processor CPDISTRIBUTION 0");
                    RunPowerCfgCommand("/setdcvalueindex scheme_current sub_processor CPDISTRIBUTION 0");

                    // Apply the changes
                    RunPowerCfgCommand("/setactive scheme_current");

                    Logger.Info($"Core parking set to {percent}% with aggressive settings (CPMAXCORES={percent}, CPMINCORES={percent}, CPHEADROOM=100, CPCONCURRENCY=100, CPDISTRIBUTION=0)");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to apply core parking: {ex.Message}");
            }
        }

        private void RunPowerCfgCommand(string arguments)
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powercfg",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using (var process = System.Diagnostics.Process.Start(startInfo))
            {
                process.WaitForExit(5000); // 5 second timeout
                if (process.ExitCode != 0)
                {
                    string error = process.StandardError.ReadToEnd();
                    Logger.Warn($"powercfg {arguments} returned {process.ExitCode}: {error}");
                }
            }
        }

        /// <summary>
        /// Sets Windows adaptive brightness (auto-adjust based on ambient light sensor).
        /// Note: This setting only has effect if the device has an ambient light sensor.
        /// </summary>
        public void SetAdaptiveBrightness(bool enabled)
        {
            adaptiveBrightnessRequested = enabled;
            try { XboxGamingBarHelper.Settings.LocalSettingsHelper.SetValue(AdaptiveBrightnessRequestedKey, enabled); } catch { }
            var mode = adaptiveBrightnessMode != null ? adaptiveBrightnessMode.Mode : Shared.Enums.AdaptiveBrightnessMode.Windows;
            Logger.Info($"SetAdaptiveBrightness requested={enabled} mode={mode}");
            ApplyAdaptiveBrightnessBackend(enabled, mode);
        }

        public void OnAdaptiveBrightnessModeChanged(Shared.Enums.AdaptiveBrightnessMode mode)
        {
            // Re-apply the current requested state so the right backend ends up running.
            ApplyAdaptiveBrightnessBackend(adaptiveBrightnessRequested, mode);
        }

        private void ApplyAdaptiveBrightnessBackend(bool requested, Shared.Enums.AdaptiveBrightnessMode mode)
        {
            if (!requested)
            {
                adaptiveBrightnessManager.Stop();
                ApplyWindowsAdaptiveBrightness(false);
                return;
            }
            if (mode == Shared.Enums.AdaptiveBrightnessMode.Helper)
            {
                ApplyWindowsAdaptiveBrightness(false);
                adaptiveBrightnessManager.Start();
            }
            else
            {
                adaptiveBrightnessManager.Stop();
                ApplyWindowsAdaptiveBrightness(true);
            }
        }

        private void ApplyWindowsAdaptiveBrightness(bool enabled)
        {
            try
            {
                RunPowerCfgCommand($"/setacvalueindex scheme_current sub_video ADAPTBRIGHT {(enabled ? 1 : 0)}");
                RunPowerCfgCommand($"/setdcvalueindex scheme_current sub_video ADAPTBRIGHT {(enabled ? 1 : 0)}");
                RunPowerCfgCommand("/setactive scheme_current");
                Logger.Info($"Windows ADAPTBRIGHT set to {enabled}");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error setting Windows ADAPTBRIGHT: {ex.Message}");
            }
        }

        /// <summary>
        /// Enables or disables Force Park Mode, which applies affinity to ALL running processes.
        /// This is aggressive and may cause system instability.
        /// </summary>
        public void SetForceParkMode(bool enabled)
        {
            if (isForceParkModeEnabled == enabled)
            {
                return;
            }

            isForceParkModeEnabled = enabled;
            Logger.Info($"Force Park Mode {(enabled ? "ENABLED" : "DISABLED")}");

            if (enabled)
            {
                // Apply affinity to all running processes
                ApplyAffinityToAllProcesses();
            }
            else
            {
                // Reset affinity for all processes to default (all cores)
                ResetAffinityForAllProcesses();
            }
        }

        /// <summary>
        /// Calculates the affinity mask based on current P-Core and E-Core settings.
        /// Returns IntPtr.Zero if invalid.
        /// </summary>
        private IntPtr CalculateAffinityMask()
        {
            // Calculate how many threads per P-Core (typically 2 for SMT)
            int threadsPerPCore = pCoreLogicalProcessors.Count > 0 && TotalPCores > 0
                ? pCoreLogicalProcessors.Count / TotalPCores
                : 1;

            // Calculate how many threads per E-Core
            int threadsPerECore = eCoreLogicalProcessors.Count > 0 && TotalECores > 0
                ? eCoreLogicalProcessors.Count / TotalECores
                : 1;

            // Use current active core settings
            int pCoresToUse = currentActivePCores <= 0 ? 0 : Math.Min(currentActivePCores, TotalPCores);
            int eCoresToUse = currentActiveECores <= 0 ? 0 : Math.Min(currentActiveECores, TotalECores);

            // Build affinity mask from logical processor indices
            ulong affinityMask = 0;

            // Add P-Core threads
            int pThreadsToAdd = pCoresToUse * threadsPerPCore;
            for (int i = 0; i < pThreadsToAdd && i < pCoreLogicalProcessors.Count; i++)
            {
                affinityMask |= (1UL << pCoreLogicalProcessors[i]);
            }

            // Add E-Core threads
            int eThreadsToAdd = eCoresToUse * threadsPerECore;
            for (int i = 0; i < eThreadsToAdd && i < eCoreLogicalProcessors.Count; i++)
            {
                affinityMask |= (1UL << eCoreLogicalProcessors[i]);
            }

            Logger.Info($"Calculated affinity mask: {pCoresToUse}P + {eCoresToUse}E = 0x{affinityMask:X}");

            return (IntPtr)affinityMask;
        }

        /// <summary>
        /// Applies the current core affinity mask to ALL running processes.
        /// WARNING: This is aggressive and may cause system instability.
        /// </summary>
        private void ApplyAffinityToAllProcesses()
        {
            if (!IsHybridCPU)
            {
                Logger.Warn("Force Park Mode only supported on hybrid CPUs");
                return;
            }

            IntPtr affinityMask = CalculateAffinityMask();
            if (affinityMask == IntPtr.Zero)
            {
                Logger.Error("Cannot apply affinity: invalid mask");
                return;
            }

            int successCount = 0;
            int failCount = 0;
            var processes = System.Diagnostics.Process.GetProcesses();

            foreach (var process in processes)
            {
                try
                {
                    // Skip system-critical processes
                    string name = process.ProcessName.ToLower();
                    if (name == "system" || name == "idle" || name == "registry" ||
                        name == "smss" || name == "csrss" || name == "wininit" ||
                        name == "services" || name == "lsass" || name == "svchost" ||
                        name == "dwm" || name == "explorer" || name == "audiodg" ||
                        name.Contains("antimalware") || name.Contains("defender"))
                    {
                        continue;
                    }

                    // Skip our own processes
                    if (name.Contains("xboxgamingbar"))
                    {
                        continue;
                    }

                    process.ProcessorAffinity = affinityMask;
                    successCount++;
                }
                catch
                {
                    // Access denied or process exited - ignore
                    failCount++;
                }
                finally
                {
                    process.Dispose();
                }
            }

            Logger.Info($"Force Park Mode: Applied affinity to {successCount} processes ({failCount} skipped/failed)");
        }

        /// <summary>
        /// Resets affinity for all processes back to default (all cores).
        /// </summary>
        private void ResetAffinityForAllProcesses()
        {
            // Calculate full affinity mask (all cores)
            int totalLogicalProcessors = Environment.ProcessorCount;
            IntPtr fullMask = (IntPtr)((1L << totalLogicalProcessors) - 1);

            int successCount = 0;
            int failCount = 0;
            var processes = System.Diagnostics.Process.GetProcesses();

            foreach (var process in processes)
            {
                try
                {
                    // Skip system processes we didn't modify anyway
                    string name = process.ProcessName.ToLower();
                    if (name == "system" || name == "idle" || name == "registry" ||
                        name == "smss" || name == "csrss" || name == "wininit" ||
                        name == "services" || name == "lsass")
                    {
                        continue;
                    }

                    process.ProcessorAffinity = fullMask;
                    successCount++;
                }
                catch
                {
                    // Access denied or process exited - ignore
                    failCount++;
                }
                finally
                {
                    process.Dispose();
                }
            }

            Logger.Info($"Force Park Mode disabled: Reset affinity for {successCount} processes ({failCount} skipped/failed)");
        }

        #endregion
    }
}
