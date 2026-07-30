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
        /// Raised once per real suspend (deduped across the SystemEvents + kernel-callback
        /// sources, same as <see cref="ResumeFromSleep"/>). Lets subsystems quiesce before
        /// sleep — notably the VIIPER emulation, whose continuous virtual-gyro reports would
        /// otherwise flood the emulated USB device and wake the machine back up (HC #541).
        /// </summary>
        public event ResumeFromSleepEventHandler SuspendingToSleep;

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
        private long lastSuspendInvokeTicksUtc;
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

        private readonly PanelBrightnessProperty panelBrightness;
        public PanelBrightnessProperty PanelBrightness
        {
            get { return panelBrightness; }
        }

        private readonly PanelBrightnessSupportedProperty panelBrightnessSupported;
        public PanelBrightnessSupportedProperty PanelBrightnessSupported
        {
            get { return panelBrightnessSupported; }
        }

        private readonly InternalPanelActiveProperty internalPanelActive;
        public InternalPanelActiveProperty InternalPanelActive
        {
            get { return internalPanelActive; }
        }

        private const string TouchscreenEnabledKey = "TouchscreenEnabled";
        private readonly TouchscreenEnabledProperty touchscreenEnabled;
        public TouchscreenEnabledProperty TouchscreenEnabled
        {
            get { return touchscreenEnabled; }
        }

        private readonly AdaptiveBrightnessModeProperty adaptiveBrightnessMode;
        public AdaptiveBrightnessModeProperty AdaptiveBrightnessMode
        {
            get { return adaptiveBrightnessMode; }
        }

        private readonly AdaptiveBrightnessManager adaptiveBrightnessManager = new AdaptiveBrightnessManager();
        private const string AdaptiveBrightnessRequestedKey = "AdaptiveBrightnessRequested";
        private bool adaptiveBrightnessRequested;

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
            panelBrightness = new PanelBrightnessProperty(this);
            panelBrightnessSupported = new PanelBrightnessSupportedProperty(this);
            internalPanelActive = new InternalPanelActiveProperty(this);

            // Touch screen: persist the user's last choice across helper restarts. Default true
            // (touch active) if never set. No need to re-apply on startup - Device Manager's
            // disabled state is itself persistent at the OS level (survives reboots/helper
            // restarts on its own), we're just mirroring it in the UI. Default true also means
            // a fresh install never starts with touch off.
            bool touchscreenInitial = true;
            if (XboxGamingBarHelper.Settings.LocalSettingsHelper.TryGetValue<bool>(TouchscreenEnabledKey, out var savedTouchscreenEnabled))
            {
                touchscreenInitial = savedTouchscreenEnabled;
            }
            touchscreenEnabled = new TouchscreenEnabledProperty(touchscreenInitial, this);

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
                        HandleSuspend("kernel notification");
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
            // Mirror of HandleSuspend: after a real resume, the next suspend is a NEW
            // sleep — clear the suspend dedupe so a quick re-sleep isn't swallowed
            // (which would leave the VIIPER forwarder emitting through the sleep, the
            // exact wake-flood SetSystemSuspended exists to prevent).
            System.Threading.Interlocked.Exchange(ref lastSuspendInvokeTicksUtc, 0);

            Logger.Info($"System resumed from sleep/hibernate ({source}) at: {DateTime.Now}");
            // Deferred: the kernel writes the 507 wake-reason record asynchronously after
            // wake, so an immediate read races it and reports the PREVIOUS wake — and the
            // event-log query mustn't delay the resume re-apply work below either.
            Task.Run(async () => { await Task.Delay(2000); LogLastWakeReason(); });
            ResumeFromSleep?.Invoke(this);
            // Refresh display settings in case display changed during sleep
            RefreshDisplaySettings();
            // AC/DC may have changed while asleep with no StatusChange event
            // (issue #94: unplug during Modern Standby left the helper on the
            // stale power source until the next real transition).
            CheckPowerSupplyStatusChange("resume");
        }

        /// <summary>
        /// Fires <see cref="SuspendingToSleep"/> once per real suspend, deduped across the
        /// SystemEvents Suspend and kernel PBT_APMSUSPEND sources (both fire on classic S3).
        /// </summary>
        private void HandleSuspend(string source)
        {
            long now = DateTime.UtcNow.Ticks;
            long last = System.Threading.Interlocked.Read(ref lastSuspendInvokeTicksUtc);
            if (now - last < ResumeDedupeWindowTicks)
            {
                Logger.Debug($"Suspend ({source}) within dedupe window; already handled");
                return;
            }
            System.Threading.Interlocked.Exchange(ref lastSuspendInvokeTicksUtc, now);
            // A real suspend means the next resume is a NEW wake, however soon it lands —
            // clear the resume dedupe so a rapid sleep/wake cycle (Modern Standby bounce,
            // auto-resleep) can't swallow it and leave subscribers wrongly suspended.
            System.Threading.Interlocked.Exchange(ref lastResumeInvokeTicksUtc, 0);
            Logger.Info($"System suspending to sleep/hibernate ({source}) at: {DateTime.Now}");
            try { SuspendingToSleep?.Invoke(this); }
            catch (Exception ex) { Logger.Warn($"SuspendingToSleep handler threw: {ex.Message}"); }
        }

        /// <summary>
        /// Best-effort: reads the most recent Kernel-Power wake event (id 507) from the
        /// System event log and logs why the device woke. We DON'T act on it — our idle
        /// hibernate already re-arms from a fresh baseline on resume so it can't ping-pong
        /// (HC #544's over-aggressive-resleep failure can't occur here) — but the reason is
        /// valuable triage for sleep/resume field reports.
        /// </summary>
        private void LogLastWakeReason()
        {
            try
            {
                var query = new System.Diagnostics.Eventing.Reader.EventLogQuery(
                    "System",
                    System.Diagnostics.Eventing.Reader.PathType.LogName,
                    "*[System[Provider[@Name='Microsoft-Windows-Kernel-Power'] and (EventID=507)]]")
                {
                    ReverseDirection = true, // newest first
                };
                using (var reader = new System.Diagnostics.Eventing.Reader.EventLogReader(query))
                {
                    var rec = reader.ReadEvent();
                    if (rec == null)
                    {
                        Logger.Debug("Wake reason: no Kernel-Power 507 event found");
                        return;
                    }
                    string desc = null;
                    try { desc = rec.FormatDescription(); } catch { }
                    if (!string.IsNullOrWhiteSpace(desc))
                    {
                        // Collapse to a single line for the log.
                        Logger.Info($"Wake reason (Kernel-Power 507): {desc.Replace("\r", " ").Replace("\n", " ").Trim()}");
                    }
                    else
                    {
                        Logger.Info("Wake reason (Kernel-Power 507): present but no description");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"Wake reason lookup failed: {ex.Message}");
            }
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
                    HandleSuspend("SystemEvents");
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
                // Refresh built-in panel brightness availability + value. Dock/undock changes
                // whether the internal panel is in the active display config, so the optional
                // brightness slider must enable/disable to match without reopening GoTweaks.
                // ForceSetValue guarantees the widget re-asserts its enabled/grayed state (#50).
                if (panelBrightnessSupported != null)
                {
                    bool brightnessSupported = BrightnessManager.IsSupported();
                    Logger.Info($"Panel brightness supported after display change: {brightnessSupported}");
                    panelBrightnessSupported.ForceSetValue(brightnessSupported);
                    if (brightnessSupported && panelBrightness != null)
                    {
                        panelBrightness.ForceSetValue(BrightnessManager.GetBrightness());
                    }
                }

                // Refresh internal-panel-active status - gates Resolution / Refresh Rate /
                // Rotation, which only make sense against the built-in panel. Same dock/undock
                // trigger as the panel brightness check above; ForceSetValue re-asserts even if
                // unchanged so the widget re-evaluates its enabled/grayed state without a reopen.
                if (internalPanelActive != null)
                {
                    bool panelActive = User32.IsInternalPanelActive() ?? true;
                    Logger.Info($"Internal panel active after display change: {panelActive}");
                    internalPanelActive.ForceSetValue(panelActive);
                }

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
                // Window titles go through CleanGameName: some titles embed
                // zero-width/format code points that vary per launch, so a
                // name-keyed profile silently stops matching (e.g. ARC Raiders).
                windowTitle = Shared.Utilities.StringHelper.CleanGameName(windowTitle);
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

        #region powercfg

        /// <summary>
        /// Resets the core-parking powercfg values (CPMAXCORES et al.) that the retired
        /// Core Parking slider wrote into the user's active scheme. Called once at startup
        /// when a pre-#103 build left a sub-100% value behind; the feature itself is gone.
        /// </summary>
        public void ResetCoreParkingToDefaults()
        {
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

        // Disables/enables the built-in touch screen digitizer via SetupAPI (Device Manager's
        // own "Disable device" mechanism). Matches by HID compatible ID (UP:000D_U:0004 =
        // Digitizer usage page + Touch Screen usage), not the friendly name/VID/PID - the
        // friendly name is localized by Windows ("HID-compliant touch screen" becomes e.g.
        // "Ekran dotykowy zgodny z HID" on Polish Windows) so name matching silently found
        // nothing on non-English installs; the compatible ID is generic and never localized.
        public void SetTouchscreenEnabled(bool enabled)
        {
            try { XboxGamingBarHelper.Settings.LocalSettingsHelper.SetValue(TouchscreenEnabledKey, enabled); } catch { }
            try
            {
                int touched = SetupApi.SetHidDeviceEnabled("UP:000D_U:0004", enabled);
                Logger.Info($"SetTouchscreenEnabled({enabled}): {touched} matching HID device(s) toggled");
            }
            catch (Exception ex)
            {
                Logger.Warn($"SetTouchscreenEnabled({enabled}) failed: {ex.Message}");
            }
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


        #endregion
    }
}
