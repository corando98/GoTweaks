using Microsoft.Win32;
using NLog;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Xml.Linq;
using Windows.System;
using Windows.UI.Input.Preview.Injection;
using XboxGamingBarHelper.Core;
using XboxGamingBarHelper.LosslessScaling.Properties;

namespace XboxGamingBarHelper.LosslessScaling
{
    internal class LosslessScalingManager : Manager
    {
        // P/Invoke for window management
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        private const int SW_RESTORE = 9;
        private const int SW_SHOW = 5;
        private const int SW_MINIMIZE = 6;
        private const int SW_SHOWMINNOACTIVE = 7;

        private const string PROCESS_NAME = "LosslessScaling";
        private const int STEAM_APP_ID = 993090;

        // Minimum length for a profile's window-title filter to be eligible for the
        // loose substring matching strategies. Without this a filter like "a"/"go"
        // would match almost any game and select the wrong profile.
        private const int MIN_FILTER_MATCH_LENGTH = 3;
        private static readonly string SETTINGS_PATH = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Lossless Scaling",
            "Settings.xml"
        );

        // Input injection for hotkeys
        private readonly InputInjector inputInjector;
        private InjectedInputKeyboardInfo[] toggleScalingKeyboardCombo;
        private string currentHotkeyDescription = "Ctrl+Alt+S";

        // Current game tracking
        private string currentGameExePath = "";
        private string currentProfileName = "Default";

        #region Properties

        private readonly LosslessScalingInstalledProperty losslessScalingInstalled;
        public LosslessScalingInstalledProperty LosslessScalingInstalled => losslessScalingInstalled;

        private readonly LosslessScalingRunningProperty losslessScalingRunning;
        public LosslessScalingRunningProperty LosslessScalingRunning => losslessScalingRunning;

        private readonly LosslessScalingEnabledProperty losslessScalingEnabled;
        public LosslessScalingEnabledProperty LosslessScalingEnabled => losslessScalingEnabled;

        private readonly LosslessScalingCurrentProfileProperty losslessScalingCurrentProfile;
        public LosslessScalingCurrentProfileProperty LosslessScalingCurrentProfile => losslessScalingCurrentProfile;

        private readonly LosslessScalingScalingTypeProperty losslessScalingScalingType;
        public LosslessScalingScalingTypeProperty LosslessScalingScalingType => losslessScalingScalingType;

        private readonly LosslessScalingSharpnessProperty losslessScalingSharpness;
        public LosslessScalingSharpnessProperty LosslessScalingSharpness => losslessScalingSharpness;

        private readonly LosslessScalingFSROptimizeProperty losslessScalingFSROptimize;
        public LosslessScalingFSROptimizeProperty LosslessScalingFSROptimize => losslessScalingFSROptimize;

        private readonly LosslessScalingAnime4KSizeProperty losslessScalingAnime4KSize;
        public LosslessScalingAnime4KSizeProperty LosslessScalingAnime4KSize => losslessScalingAnime4KSize;

        private readonly LosslessScalingAnime4KVRSProperty losslessScalingAnime4KVRS;
        public LosslessScalingAnime4KVRSProperty LosslessScalingAnime4KVRS => losslessScalingAnime4KVRS;

        private readonly LosslessScalingScaleModeProperty losslessScalingScaleMode;
        public LosslessScalingScaleModeProperty LosslessScalingScaleMode => losslessScalingScaleMode;

        private readonly LosslessScalingScaleFactorProperty losslessScalingScaleFactor;
        public LosslessScalingScaleFactorProperty LosslessScalingScaleFactor => losslessScalingScaleFactor;

        private readonly LosslessScalingAspectRatioProperty losslessScalingAspectRatio;
        public LosslessScalingAspectRatioProperty LosslessScalingAspectRatio => losslessScalingAspectRatio;

        private readonly LosslessScalingFrameGenTypeProperty losslessScalingFrameGenType;
        public LosslessScalingFrameGenTypeProperty LosslessScalingFrameGenType => losslessScalingFrameGenType;

        private readonly LosslessScalingLSFG3ModeProperty losslessScalingLSFG3Mode;
        public LosslessScalingLSFG3ModeProperty LosslessScalingLSFG3Mode => losslessScalingLSFG3Mode;

        private readonly LosslessScalingLSFG3MultiplierProperty losslessScalingLSFG3Multiplier;
        public LosslessScalingLSFG3MultiplierProperty LosslessScalingLSFG3Multiplier => losslessScalingLSFG3Multiplier;

        private readonly LosslessScalingLSFG3TargetProperty losslessScalingLSFG3Target;
        public LosslessScalingLSFG3TargetProperty LosslessScalingLSFG3Target => losslessScalingLSFG3Target;

        private readonly LosslessScalingLSFG2ModeProperty losslessScalingLSFG2Mode;
        public LosslessScalingLSFG2ModeProperty LosslessScalingLSFG2Mode => losslessScalingLSFG2Mode;

        private readonly LosslessScalingFlowScaleProperty losslessScalingFlowScale;
        public LosslessScalingFlowScaleProperty LosslessScalingFlowScale => losslessScalingFlowScale;

        private readonly LosslessScalingSizeProperty losslessScalingSize;
        public LosslessScalingSizeProperty LosslessScalingSize => losslessScalingSize;

        private readonly LosslessScalingAutoScaleProperty losslessScalingAutoScale;
        public LosslessScalingAutoScaleProperty LosslessScalingAutoScale => losslessScalingAutoScale;

        private readonly LosslessScalingAutoScaleDelayProperty losslessScalingAutoScaleDelay;
        public LosslessScalingAutoScaleDelayProperty LosslessScalingAutoScaleDelay => losslessScalingAutoScaleDelay;

        private readonly LosslessScalingSaveAndRestartProperty losslessScalingSaveAndRestart;
        public LosslessScalingSaveAndRestartProperty LosslessScalingSaveAndRestart => losslessScalingSaveAndRestart;

        private readonly LosslessScalingCreateProfileProperty losslessScalingCreateProfile;
        public LosslessScalingCreateProfileProperty LosslessScalingCreateProfile => losslessScalingCreateProfile;

        private readonly LosslessScalingBringToForegroundProperty losslessScalingBringToForeground;
        public LosslessScalingBringToForegroundProperty LosslessScalingBringToForeground => losslessScalingBringToForeground;

        private readonly LosslessScalingLaunchProperty losslessScalingLaunch;
        public LosslessScalingLaunchProperty LosslessScalingLaunch => losslessScalingLaunch;

        // --- Additional Settings.xml fields (added 2026-05-01) ---
        private readonly LosslessScalingSyncModeProperty losslessScalingSyncMode;
        public LosslessScalingSyncModeProperty LosslessScalingSyncMode => losslessScalingSyncMode;

        private readonly LosslessScalingCaptureApiProperty losslessScalingCaptureApi;
        public LosslessScalingCaptureApiProperty LosslessScalingCaptureApi => losslessScalingCaptureApi;

        private readonly LosslessScalingDrawFpsProperty losslessScalingDrawFps;
        public LosslessScalingDrawFpsProperty LosslessScalingDrawFps => losslessScalingDrawFps;

        private readonly LosslessScalingHdrSupportProperty losslessScalingHdrSupport;
        public LosslessScalingHdrSupportProperty LosslessScalingHdrSupport => losslessScalingHdrSupport;

        private readonly LosslessScalingGsyncSupportProperty losslessScalingGsyncSupport;
        public LosslessScalingGsyncSupportProperty LosslessScalingGsyncSupport => losslessScalingGsyncSupport;

        private readonly LosslessScalingResizeBeforeScalingProperty losslessScalingResizeBeforeScaling;
        public LosslessScalingResizeBeforeScalingProperty LosslessScalingResizeBeforeScaling => losslessScalingResizeBeforeScaling;

        private readonly LosslessScalingLS1TypeProperty losslessScalingLS1Type;
        public LosslessScalingLS1TypeProperty LosslessScalingLS1Type => losslessScalingLS1Type;

        private readonly LosslessScalingMaxFrameLatencyProperty losslessScalingMaxFrameLatency;
        public LosslessScalingMaxFrameLatencyProperty LosslessScalingMaxFrameLatency => losslessScalingMaxFrameLatency;

        private readonly LosslessScalingResetProfileProperty losslessScalingResetProfile;
        public LosslessScalingResetProfileProperty LosslessScalingResetProfile => losslessScalingResetProfile;

        #endregion

        // State tracking
        private bool isScalingActive = false;
        private bool isRunningLastResult = false;

        // Guards the throttle/cache/current-game fields below, which are touched from
        // both the manager Update() timer thread and pipe/profile threads (SetCurrentGame,
        // property-change handlers). Kept for short synchronous sections only — never
        // held across an await.
        private readonly object stateLock = new object();

        // IsRunning() result cache. The poll enumerates processes on every Update()
        // tick and is also read by several action paths; a short TTL de-dupes those
        // reads. Paths that just launched or killed LS call IsRunning() directly so the
        // fresh result is observed at once (and the cache refreshed).
        private bool cachedIsRunning = false;
        private DateTime lastRunningCheck = DateTime.MinValue;
        private static readonly TimeSpan RunningCacheTtl = TimeSpan.FromMilliseconds(1000);

        // Installation re-detection throttle (so the Scale tab can appear/disappear
        // at runtime without a helper restart when LS is installed/uninstalled).
        private DateTime lastInstalledCheck = DateTime.MinValue;
        private static readonly TimeSpan InstalledRecheckInterval = TimeSpan.FromSeconds(10);

        public LosslessScalingManager() : base()
        {
            Logger.Info("Initializing Lossless Scaling Manager...");

            bool isInstalled = IsInstalled();
            bool isRunning = IsRunning();
            Logger.Info($"Lossless Scaling installed: {isInstalled}, running: {isRunning}");

            // Read Default profile settings
            var settings = ReadSettingsFromProfile("Default");

            // Initialize all properties
            losslessScalingInstalled = new LosslessScalingInstalledProperty(isInstalled, this);
            losslessScalingRunning = new LosslessScalingRunningProperty(isRunning, this);
            losslessScalingEnabled = new LosslessScalingEnabledProperty(false, this);
            losslessScalingCurrentProfile = new LosslessScalingCurrentProfileProperty("Default", this);
            losslessScalingScalingType = new LosslessScalingScalingTypeProperty(settings.ScalingType, this);
            losslessScalingSharpness = new LosslessScalingSharpnessProperty(settings.Sharpness, this);
            losslessScalingFSROptimize = new LosslessScalingFSROptimizeProperty(settings.FSROptimize, this);
            losslessScalingAnime4KSize = new LosslessScalingAnime4KSizeProperty(settings.Anime4KSize, this);
            losslessScalingAnime4KVRS = new LosslessScalingAnime4KVRSProperty(settings.Anime4KVRS, this);
            losslessScalingScaleMode = new LosslessScalingScaleModeProperty(settings.ScaleMode, this);
            losslessScalingScaleFactor = new LosslessScalingScaleFactorProperty(settings.ScaleFactor, this);
            losslessScalingAspectRatio = new LosslessScalingAspectRatioProperty(settings.AspectRatio, this);
            losslessScalingFrameGenType = new LosslessScalingFrameGenTypeProperty(settings.FrameGenType, this);
            losslessScalingLSFG3Mode = new LosslessScalingLSFG3ModeProperty(settings.LSFG3Mode, this);
            losslessScalingLSFG3Multiplier = new LosslessScalingLSFG3MultiplierProperty(settings.LSFG3Multiplier, this);
            losslessScalingLSFG3Target = new LosslessScalingLSFG3TargetProperty(settings.LSFG3Target, this);
            losslessScalingLSFG2Mode = new LosslessScalingLSFG2ModeProperty(settings.LSFG2Mode, this);
            losslessScalingFlowScale = new LosslessScalingFlowScaleProperty(settings.FlowScale, this);
            losslessScalingSize = new LosslessScalingSizeProperty(settings.Size, this);
            losslessScalingAutoScale = new LosslessScalingAutoScaleProperty(settings.AutoScale, this);
            losslessScalingAutoScaleDelay = new LosslessScalingAutoScaleDelayProperty(settings.AutoScaleDelay, this);
            losslessScalingSaveAndRestart = new LosslessScalingSaveAndRestartProperty(false, this);
            losslessScalingCreateProfile = new LosslessScalingCreateProfileProperty("", this);
            losslessScalingBringToForeground = new LosslessScalingBringToForegroundProperty(false, this);
            losslessScalingLaunch = new LosslessScalingLaunchProperty(false, this);

            // Additional Settings.xml-backed properties
            losslessScalingSyncMode = new LosslessScalingSyncModeProperty(settings.SyncMode, this);
            losslessScalingCaptureApi = new LosslessScalingCaptureApiProperty(settings.CaptureApi, this);
            losslessScalingDrawFps = new LosslessScalingDrawFpsProperty(settings.DrawFps, this);
            losslessScalingHdrSupport = new LosslessScalingHdrSupportProperty(settings.HdrSupport, this);
            losslessScalingGsyncSupport = new LosslessScalingGsyncSupportProperty(settings.GsyncSupport, this);
            losslessScalingResizeBeforeScaling = new LosslessScalingResizeBeforeScalingProperty(settings.ResizeBeforeScaling, this);
            losslessScalingLS1Type = new LosslessScalingLS1TypeProperty(settings.LS1Type, this);
            losslessScalingMaxFrameLatency = new LosslessScalingMaxFrameLatencyProperty(settings.MaxFrameLatency, this);
            losslessScalingResetProfile = new LosslessScalingResetProfileProperty(false, this);

            // Subscribe to action properties
            losslessScalingEnabled.PropertyChanged += LosslessScalingEnabled_PropertyChanged;
            losslessScalingSaveAndRestart.PropertyChanged += LosslessScalingSaveAndRestart_PropertyChanged;
            losslessScalingCreateProfile.PropertyChanged += LosslessScalingCreateProfile_PropertyChanged;
            losslessScalingBringToForeground.PropertyChanged += LosslessScalingBringToForeground_PropertyChanged;
            losslessScalingLaunch.PropertyChanged += LosslessScalingLaunch_PropertyChanged;
            losslessScalingResetProfile.PropertyChanged += LosslessScalingResetProfile_PropertyChanged;

            inputInjector = InputInjector.TryCreate();

            // Read hotkey from Settings.xml (user may have customized it)
            BuildToggleScalingHotkey();

            Logger.Info("Lossless Scaling Manager initialized successfully.");
        }

        public override void Update()
        {
            base.Update();

            bool currentlyRunning = IsRunningCached();
            if (losslessScalingRunning.Value != currentlyRunning)
            {
                losslessScalingRunning.SetValue(currentlyRunning, DateTime.Now.Ticks);

                // LS exited → no scaling can be active. Reset the best-effort tracker so
                // the next enable starts from a known state. (Toggles made via LS's own
                // hotkey while it runs cannot be observed — that stays a blind spot.)
                if (!currentlyRunning)
                {
                    lock (stateLock) { isScalingActive = false; }
                }
            }

            // Periodically re-detect installation so the Scale tab (and Quick Settings
            // tile) appear/disappear without a helper restart when the user installs,
            // uninstalls, or first-launches LS while GoTweaks is already running.
            bool doInstalledCheck;
            lock (stateLock)
            {
                doInstalledCheck = DateTime.UtcNow - lastInstalledCheck >= InstalledRecheckInterval;
                if (doInstalledCheck)
                {
                    lastInstalledCheck = DateTime.UtcNow;
                }
            }
            if (doInstalledCheck)
            {
                bool currentlyInstalled = IsInstalled();
                if (losslessScalingInstalled.Value != currentlyInstalled)
                {
                    Logger.Info($"Lossless Scaling installation state changed → {currentlyInstalled}");
                    losslessScalingInstalled.SetValue(currentlyInstalled, DateTime.Now.Ticks);
                }
            }
        }

        /// <summary>
        /// Called when the running game changes. Updates the active profile.
        /// </summary>
        public void SetCurrentGame(string gameName, string exePath)
        {
            // Compound check-then-act on the shared field: guard so two threads
            // (Update timer vs. profile/pipe) can't both pass the dedup for the same game.
            lock (stateLock)
            {
                if (currentGameExePath == exePath)
                    return;
                currentGameExePath = exePath;
            }

            Logger.Info($"Current game changed to: {gameName} ({exePath})");

            // Find profile for this game - try multiple matching strategies
            string profileName = FindProfileForGame(gameName, exePath);
            if (string.IsNullOrEmpty(profileName))
            {
                profileName = "Default";
            }

            lock (stateLock) { currentProfileName = profileName; }
            losslessScalingCurrentProfile.SetValue(profileName, DateTime.Now.Ticks);

            // Load settings from the profile
            var settings = ReadSettingsFromProfile(profileName);
            UpdatePropertiesFromSettings(settings);

            Logger.Info($"Loaded profile '{profileName}' for game '{gameName}'");
        }

        #region Core Methods

        private bool IsInstalled()
        {
            // Presence of the LosslessScaling.exe (located via the Steam library) is
            // the source of truth. Settings.xml is created only on the FIRST LS launch,
            // so requiring it here would hide the Scale tab for a freshly-installed but
            // never-run LS. When Settings.xml is missing we simply fall back to the
            // built-in LosslessScalingSettings defaults.
            string exePath = FindLosslessScalingExePath();
            return !string.IsNullOrEmpty(exePath) && File.Exists(exePath);
        }

        /// <summary>
        /// Cached view of <see cref="IsRunning"/> for high-frequency callers. Returns the
        /// last result within <see cref="RunningCacheTtl"/>, otherwise re-polls. Paths that
        /// just launched or killed LS call <see cref="IsRunning"/> directly so the fresh
        /// result is observed at once (and the cache refreshed).
        /// </summary>
        private bool IsRunningCached()
        {
            lock (stateLock)
            {
                if (DateTime.UtcNow - lastRunningCheck < RunningCacheTtl)
                {
                    return cachedIsRunning;
                }
            }
            return IsRunning();
        }

        private bool IsRunning()
        {
            // Only log on a state transition (or a genuine failure) to keep the helper
            // log clean — this is polled frequently.
            // (vvalente30 issue #79: LS process name can be absent or the enumeration
            // can throw on some machines — the Warn below surfaces that case.)
            int procCount = -1;
            string error = null;
            try
            {
                var processes = Process.GetProcessesByName(PROCESS_NAME);
                procCount = processes.Length;
                foreach (var proc in processes)
                {
                    proc.Dispose();
                }
            }
            catch (Exception ex)
            {
                error = $"{ex.GetType().Name}: {ex.Message}";
            }

            bool isRunning = procCount > 0;

            lock (stateLock)
            {
                if (error != null)
                {
                    Logger.Warn($"Lossless Scaling IsRunning() poll threw — {error}");
                }
                else if (isRunning != isRunningLastResult)
                {
                    Logger.Info($"Lossless Scaling running state changed → {isRunning} (procs={procCount})");
                }

                isRunningLastResult = isRunning;
                cachedIsRunning = isRunning;
                lastRunningCheck = DateTime.UtcNow;
            }

            return isRunning;
        }

        public async Task<bool> LaunchLosslessScalingAsync()
        {
            if (IsRunningCached())
            {
                Logger.Info("Lossless Scaling is already running.");
                return true;
            }

            // A freshly launched LS always starts with scaling OFF, so reset the
            // best-effort tracker here. The enable handler then toggles it on if needed.
            lock (stateLock) { isScalingActive = false; }

            try
            {
                // Try to find and launch via direct exe path first
                string exePath = FindLosslessScalingExePath();
                if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                {
                    Logger.Info($"Launching Lossless Scaling directly from: {exePath}");
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = exePath,
                        WorkingDirectory = Path.GetDirectoryName(exePath),
                        WindowStyle = ProcessWindowStyle.Minimized,
                        UseShellExecute = true
                    };
                    Process.Start(startInfo);
                    await Task.Delay(2000);

                    // Ensure window is minimized after launch
                    var processes = Process.GetProcessesByName(PROCESS_NAME);
                    try
                    {
                        foreach (var proc in processes)
                        {
                            if (proc.MainWindowHandle != IntPtr.Zero)
                            {
                                ShowWindow(proc.MainWindowHandle, SW_MINIMIZE);
                            }
                        }
                    }
                    finally
                    {
                        foreach (var proc in processes)
                        {
                            proc.Dispose();
                        }
                    }

                    bool success = IsRunning();
                    Logger.Info($"Lossless Scaling launch {(success ? "successful" : "failed")}");
                    return success;
                }
                else
                {
                    // Fallback to Steam URI if exe not found
                    Logger.Info("Exe not found, falling back to Steam URI launch...");
                    var uri = new Uri($"steam://rungameid/{STEAM_APP_ID}");
                    await global::Windows.System.Launcher.LaunchUriAsync(uri);
                    await Task.Delay(3000);

                    bool success = IsRunning();
                    Logger.Info($"Lossless Scaling launch via Steam {(success ? "successful" : "failed")}");
                    return success;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to launch Lossless Scaling: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Finds the Lossless Scaling executable path from Steam installation.
        /// </summary>
        private string FindLosslessScalingExePath()
        {
            try
            {
                // Try to find Steam installation path from registry
                string steamPath = null;

                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    steamPath = key?.GetValue("SteamPath") as string;
                }

                if (string.IsNullOrEmpty(steamPath))
                {
                    using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam"))
                    {
                        steamPath = key?.GetValue("InstallPath") as string;
                    }
                }

                if (string.IsNullOrEmpty(steamPath))
                {
                    Logger.Debug("Could not find Steam installation path in registry");
                    return null;
                }

                Logger.Debug($"Found Steam path: {steamPath}");

                // Check default steamapps location first
                string defaultLibrary = Path.Combine(steamPath, "steamapps", "common", "Lossless Scaling", "LosslessScaling.exe");
                if (File.Exists(defaultLibrary))
                {
                    Logger.Debug($"Found Lossless Scaling at default location: {defaultLibrary}");
                    return defaultLibrary;
                }

                // Parse libraryfolders.vdf to find additional Steam library folders
                string libraryFoldersPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
                if (File.Exists(libraryFoldersPath))
                {
                    var libraryContent = File.ReadAllText(libraryFoldersPath);
                    var pathMatches = System.Text.RegularExpressions.Regex.Matches(libraryContent, "\"path\"\\s+\"([^\"]+)\"");

                    foreach (System.Text.RegularExpressions.Match match in pathMatches)
                    {
                        string libraryPath = match.Groups[1].Value.Replace("\\\\", "\\");
                        string lsPath = Path.Combine(libraryPath, "steamapps", "common", "Lossless Scaling", "LosslessScaling.exe");

                        if (File.Exists(lsPath))
                        {
                            Logger.Debug($"Found Lossless Scaling in library: {lsPath}");
                            return lsPath;
                        }
                    }
                }

                Logger.Debug("Could not find Lossless Scaling executable in any Steam library");
                return null;
            }
            catch (Exception ex)
            {
                Logger.Error($"Error finding Lossless Scaling exe path: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Brings Lossless Scaling window to the foreground.
        /// </summary>
        public void BringToForeground()
        {
            Process[] processes = null;
            try
            {
                processes = Process.GetProcessesByName(PROCESS_NAME);
                if (processes.Length == 0)
                {
                    Logger.Warn("Cannot bring to foreground: Lossless Scaling is not running");
                    return;
                }

                foreach (var proc in processes)
                {
                    IntPtr hWnd = proc.MainWindowHandle;
                    if (hWnd != IntPtr.Zero)
                    {
                        // If window is minimized, restore it first
                        if (IsIconic(hWnd))
                        {
                            ShowWindow(hWnd, SW_RESTORE);
                        }
                        else
                        {
                            ShowWindow(hWnd, SW_SHOW);
                        }
                        SetForegroundWindow(hWnd);
                        Logger.Info("Lossless Scaling brought to foreground");
                        return;
                    }
                }

                Logger.Warn("Could not find Lossless Scaling window handle");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error bringing Lossless Scaling to foreground: {ex.Message}");
            }
            finally
            {
                if (processes != null)
                {
                    foreach (var proc in processes)
                    {
                        proc.Dispose();
                    }
                }
            }
        }

        private void ToggleScaling()
        {
            if (!IsRunningCached())
            {
                Logger.Warn("Cannot toggle scaling: Lossless Scaling is not running");
                return;
            }

            if (inputInjector == null)
            {
                Logger.Error("Input injector not available");
                return;
            }

            try
            {
                Logger.Info($"Sending {currentHotkeyDescription} hotkey to toggle Lossless Scaling...");
                inputInjector.InjectKeyboardInput(toggleScalingKeyboardCombo);
                bool nowActive;
                lock (stateLock)
                {
                    isScalingActive = !isScalingActive;
                    nowActive = isScalingActive;
                }
                Logger.Info($"Scaling toggled to: {nowActive}");
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to send hotkey: {ex.Message}");
            }
        }

        /// <summary>
        /// Reads the hotkey configuration from Settings.xml and builds the keyboard combo.
        /// Falls back to Ctrl+Alt+S if Settings.xml cannot be read.
        /// </summary>
        private void BuildToggleScalingHotkey()
        {
            var modifiers = new System.Collections.Generic.List<VirtualKey>();
            VirtualKey mainKey = VirtualKey.S;
            var descParts = new System.Collections.Generic.List<string>();

            try
            {
                if (File.Exists(SETTINGS_PATH))
                {
                    var doc = XDocument.Load(SETTINGS_PATH);

                    // Read Hotkey element (the main key, e.g., "S", "F")
                    string hotkeyValue = doc.Root?.Element("Hotkey")?.Value;
                    if (!string.IsNullOrEmpty(hotkeyValue))
                    {
                        mainKey = ParseKeyString(hotkeyValue);
                        Logger.Info($"Read main hotkey from Settings.xml: {hotkeyValue}");
                    }

                    // Read HotkeyModifierKeys element (e.g., "Alt Control")
                    string modifierKeysValue = doc.Root?.Element("HotkeyModifierKeys")?.Value;
                    if (!string.IsNullOrEmpty(modifierKeysValue))
                    {
                        Logger.Info($"Read modifier keys from Settings.xml: {modifierKeysValue}");
                        var modifierParts = modifierKeysValue.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);

                        foreach (var mod in modifierParts)
                        {
                            switch (mod.ToLower())
                            {
                                case "control":
                                case "ctrl":
                                    modifiers.Add(VirtualKey.LeftControl);
                                    descParts.Add("Ctrl");
                                    break;
                                case "alt":
                                case "menu":
                                    modifiers.Add(VirtualKey.LeftMenu);
                                    descParts.Add("Alt");
                                    break;
                                case "shift":
                                    modifiers.Add(VirtualKey.LeftShift);
                                    descParts.Add("Shift");
                                    break;
                                case "windows":
                                case "win":
                                    modifiers.Add(VirtualKey.LeftWindows);
                                    descParts.Add("Win");
                                    break;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Failed to read hotkey from Settings.xml, using default: {ex.Message}");
            }

            // If no modifiers were read, use default Ctrl+Alt
            if (modifiers.Count == 0)
            {
                modifiers.Add(VirtualKey.LeftControl);
                modifiers.Add(VirtualKey.LeftMenu);
                descParts.Add("Ctrl");
                descParts.Add("Alt");
            }

            // Build description
            descParts.Add(GetKeyDescription(mainKey));
            currentHotkeyDescription = string.Join("+", descParts);

            // Build the keyboard combo array: press modifiers, press main key, release all
            var comboList = new System.Collections.Generic.List<InjectedInputKeyboardInfo>();

            // Key down for modifiers
            foreach (var mod in modifiers)
            {
                comboList.Add(new InjectedInputKeyboardInfo { VirtualKey = (ushort)mod, KeyOptions = InjectedInputKeyOptions.None });
            }

            // Key down for main key
            comboList.Add(new InjectedInputKeyboardInfo { VirtualKey = (ushort)mainKey, KeyOptions = InjectedInputKeyOptions.None });

            // Key up for modifiers (reverse order)
            for (int i = modifiers.Count - 1; i >= 0; i--)
            {
                comboList.Add(new InjectedInputKeyboardInfo { VirtualKey = (ushort)modifiers[i], KeyOptions = InjectedInputKeyOptions.KeyUp });
            }

            // Key up for main key
            comboList.Add(new InjectedInputKeyboardInfo { VirtualKey = (ushort)mainKey, KeyOptions = InjectedInputKeyOptions.KeyUp });

            toggleScalingKeyboardCombo = comboList.ToArray();
            Logger.Info($"Lossless Scaling hotkey configured: {currentHotkeyDescription}");
        }

        /// <summary>
        /// Parses a key string (e.g., "S", "F", "F5") to a VirtualKey.
        /// </summary>
        private VirtualKey ParseKeyString(string keyStr)
        {
            if (string.IsNullOrEmpty(keyStr))
                return VirtualKey.S;

            keyStr = keyStr.Trim().ToUpper();

            // Single letter keys A-Z
            if (keyStr.Length == 1 && char.IsLetter(keyStr[0]))
            {
                return (VirtualKey)(65 + (keyStr[0] - 'A'));
            }

            // Number keys 0-9
            if (keyStr.Length == 1 && char.IsDigit(keyStr[0]))
            {
                return (VirtualKey)(48 + (keyStr[0] - '0'));
            }

            // Function keys F1-F24
            if (keyStr.StartsWith("F") && keyStr.Length >= 2 && int.TryParse(keyStr.Substring(1), out int fNum) && fNum >= 1 && fNum <= 24)
            {
                return (VirtualKey)(111 + fNum); // F1 = 112
            }

            // Try direct VirtualKey enum parse
            if (Enum.TryParse<VirtualKey>(keyStr, true, out var vk))
            {
                return vk;
            }

            Logger.Warn($"Could not parse key '{keyStr}', defaulting to S");
            return VirtualKey.S;
        }

        /// <summary>
        /// Gets a friendly description for a VirtualKey.
        /// </summary>
        private string GetKeyDescription(VirtualKey key)
        {
            int keyVal = (int)key;

            // Letters A-Z (65-90)
            if (keyVal >= 65 && keyVal <= 90)
                return ((char)keyVal).ToString();

            // Numbers 0-9 (48-57)
            if (keyVal >= 48 && keyVal <= 57)
                return ((char)keyVal).ToString();

            // Function keys F1-F24 (112-135)
            if (keyVal >= 112 && keyVal <= 135)
                return $"F{keyVal - 111}";

            return key.ToString();
        }

        #endregion

        #region XML Operations

        private class LosslessScalingSettings
        {
            public string ScalingType { get; set; } = "Off";
            public int Sharpness { get; set; } = 50;
            public bool FSROptimize { get; set; } = false;
            public string Anime4KSize { get; set; } = "Medium";
            public bool Anime4KVRS { get; set; } = false;
            public string ScaleMode { get; set; } = "Auto";
            public int ScaleFactor { get; set; } = 2;
            public string AspectRatio { get; set; } = "Aspect Ratio";
            public string FrameGenType { get; set; } = "Off";
            public string LSFG3Mode { get; set; } = "FIXED";
            public int LSFG3Multiplier { get; set; } = 2;
            public int LSFG3Target { get; set; } = 120;
            public string LSFG2Mode { get; set; } = "X2";
            public int FlowScale { get; set; } = 50;
            public string Size { get; set; } = "BALANCED";
            public bool AutoScale { get; set; } = false;
            public int AutoScaleDelay { get; set; } = 0;

            // Added 2026-05-01 — additional Settings.xml fields exposed in widget
            public string SyncMode { get; set; } = "OFF";       // OFF, DEFAULT, VSYNC1..VSYNC4
            public string CaptureApi { get; set; } = "WGC";     // DXGI, WGC, GDI
            public bool DrawFps { get; set; } = false;
            public bool HdrSupport { get; set; } = false;
            public bool GsyncSupport { get; set; } = false;
            public bool ResizeBeforeScaling { get; set; } = false;
            public string LS1Type { get; set; } = "BALANCED";   // BALANCED, PERFORMANCE
            public int MaxFrameLatency { get; set; } = 1;       // 0..4
        }

        /// <summary>
        /// Finds a profile for the given game using multiple matching strategies.
        /// Lossless Scaling uses the Path field as a window title filter (substring match).
        /// </summary>
        private string FindProfileForGame(string gameName, string exePath)
        {
            try
            {
                if (!File.Exists(SETTINGS_PATH))
                    return null;

                var doc = XDocument.Load(SETTINGS_PATH);
                var profiles = doc.Descendants("Profile")
                    .Where(p => p.Element("Title")?.Value != "Default")
                    .ToList();

                // Strategy 1: Exact match on Path (legacy exe path matching)
                if (!string.IsNullOrEmpty(exePath))
                {
                    var exactPathMatch = profiles.FirstOrDefault(p =>
                        string.Equals(p.Element("Path")?.Value, exePath, StringComparison.OrdinalIgnoreCase));
                    if (exactPathMatch != null)
                    {
                        Logger.Debug($"Found profile by exact path match: {exactPathMatch.Element("Title")?.Value}");
                        return exactPathMatch.Element("Title")?.Value;
                    }
                }

                // Strategy 2: Profile Title matches game name (case-insensitive)
                if (!string.IsNullOrEmpty(gameName))
                {
                    var titleMatch = profiles.FirstOrDefault(p =>
                        string.Equals(p.Element("Title")?.Value, gameName, StringComparison.OrdinalIgnoreCase));
                    if (titleMatch != null)
                    {
                        Logger.Debug($"Found profile by title match: {titleMatch.Element("Title")?.Value}");
                        return titleMatch.Element("Title")?.Value;
                    }
                }

                // Strategy 3: Path filter contains game name or game name contains Path filter (window title matching)
                if (!string.IsNullOrEmpty(gameName))
                {
                    foreach (var profile in profiles)
                    {
                        var pathFilter = profile.Element("Path")?.Value;
                        if (string.IsNullOrEmpty(pathFilter) || pathFilter.Length < MIN_FILTER_MATCH_LENGTH)
                            continue;

                        // Check if game name contains the path filter (Lossless Scaling style matching)
                        if (gameName.IndexOf(pathFilter, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            Logger.Debug($"Found profile by window filter match (game contains filter): {profile.Element("Title")?.Value}");
                            return profile.Element("Title")?.Value;
                        }

                        // Check if path filter contains game name
                        if (pathFilter.IndexOf(gameName, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            Logger.Debug($"Found profile by window filter match (filter contains game): {profile.Element("Title")?.Value}");
                            return profile.Element("Title")?.Value;
                        }
                    }
                }

                // Strategy 4: Extract exe name from path and match against Path filter
                if (!string.IsNullOrEmpty(exePath))
                {
                    string exeName = System.IO.Path.GetFileNameWithoutExtension(exePath);
                    if (!string.IsNullOrEmpty(exeName) && exeName.Length >= MIN_FILTER_MATCH_LENGTH)
                    {
                        foreach (var profile in profiles)
                        {
                            var pathFilter = profile.Element("Path")?.Value;
                            if (string.IsNullOrEmpty(pathFilter) || pathFilter.Length < MIN_FILTER_MATCH_LENGTH)
                                continue;

                            if (exeName.IndexOf(pathFilter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                pathFilter.IndexOf(exeName, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                Logger.Debug($"Found profile by exe name match: {profile.Element("Title")?.Value}");
                                return profile.Element("Title")?.Value;
                            }
                        }
                    }
                }

                Logger.Debug($"No matching profile found for game '{gameName}'");
                return null;
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to find profile for game: {ex.Message}");
                return null;
            }
        }

        private string FindProfileByExePath(string exePath)
        {
            try
            {
                if (!File.Exists(SETTINGS_PATH) || string.IsNullOrEmpty(exePath))
                    return null;

                var doc = XDocument.Load(SETTINGS_PATH);
                var profile = doc.Descendants("Profile")
                    .FirstOrDefault(p => string.Equals(p.Element("Path")?.Value, exePath, StringComparison.OrdinalIgnoreCase));

                return profile?.Element("Title")?.Value;
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to find profile by exe path: {ex.Message}");
                return null;
            }
        }

        private LosslessScalingSettings ReadSettingsFromProfile(string profileName)
        {
            var settings = new LosslessScalingSettings();

            try
            {
                if (!File.Exists(SETTINGS_PATH))
                {
                    Logger.Warn("Settings.xml not found, using defaults");
                    return settings;
                }

                var doc = XDocument.Load(SETTINGS_PATH);
                var profile = doc.Descendants("Profile")
                    .FirstOrDefault(p => p.Element("Title")?.Value == profileName);

                if (profile == null)
                {
                    Logger.Warn($"Profile '{profileName}' not found, using defaults");
                    return settings;
                }

                settings.ScalingType = profile.Element("ScalingType")?.Value ?? "Off";
                settings.Sharpness = int.TryParse(profile.Element("Sharpness")?.Value, out int sharpness) ? sharpness : 50;
                settings.FSROptimize = bool.TryParse(profile.Element("FSROptimize")?.Value?.ToLower(), out bool fsrOpt) && fsrOpt;
                settings.Anime4KSize = profile.Element("Anime4KSize")?.Value ?? "Medium";
                settings.Anime4KVRS = bool.TryParse(profile.Element("Anime4KVRS")?.Value?.ToLower(), out bool vrs) && vrs;
                settings.ScaleMode = profile.Element("ScaleMode")?.Value ?? "Auto";
                settings.ScaleFactor = int.TryParse(profile.Element("ScaleFactor")?.Value, out int factor) ? factor : 2;
                settings.AspectRatio = profile.Element("AspectRatio")?.Value ?? "Aspect Ratio";
                settings.FrameGenType = profile.Element("FrameGeneration")?.Value ?? "Off";
                settings.LSFG3Mode = profile.Element("LSFG3Mode1")?.Value ?? "FIXED";
                settings.LSFG3Multiplier = int.TryParse(profile.Element("LSFG3Multiplier")?.Value, out int mult) ? mult : 2;
                settings.LSFG3Target = int.TryParse(profile.Element("LSFG3Target")?.Value, out int target) ? target : 120;
                settings.LSFG2Mode = profile.Element("LSFG2Mode")?.Value ?? "X2";
                settings.FlowScale = int.TryParse(profile.Element("LSFGFlowScale")?.Value, out int flow) ? flow : 50;
                settings.Size = profile.Element("LSFGSize")?.Value ?? "BALANCED";
                settings.AutoScale = bool.TryParse(profile.Element("AutoScale")?.Value?.ToLower(), out bool auto) && auto;
                settings.AutoScaleDelay = int.TryParse(profile.Element("AutoScaleDelay")?.Value, out int delay) ? delay : 0;

                // Additional Settings.xml fields
                settings.SyncMode = profile.Element("SyncMode")?.Value ?? "OFF";
                settings.CaptureApi = profile.Element("CaptureApi")?.Value ?? "WGC";
                settings.DrawFps = bool.TryParse(profile.Element("DrawFps")?.Value?.ToLower(), out bool drawFps) && drawFps;
                settings.HdrSupport = bool.TryParse(profile.Element("HdrSupport")?.Value?.ToLower(), out bool hdr) && hdr;
                settings.GsyncSupport = bool.TryParse(profile.Element("GsyncSupport")?.Value?.ToLower(), out bool gsync) && gsync;
                settings.ResizeBeforeScaling = bool.TryParse(profile.Element("ResizeBeforeScaling")?.Value?.ToLower(), out bool rbs) && rbs;
                settings.LS1Type = profile.Element("LS1Type")?.Value ?? "BALANCED";
                settings.MaxFrameLatency = int.TryParse(profile.Element("MaxFrameLatency")?.Value, out int latency) ? latency : 1;

                Logger.Info($"Read settings from profile '{profileName}'");
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to read Settings.xml: {ex.Message}");
            }

            return settings;
        }

        private void WriteSettingsToProfile(string profileName)
        {
            try
            {
                if (!File.Exists(SETTINGS_PATH))
                {
                    Logger.Warn("Settings.xml not found, cannot write settings");
                    return;
                }

                var doc = XDocument.Load(SETTINGS_PATH);
                var profile = doc.Descendants("Profile")
                    .FirstOrDefault(p => p.Element("Title")?.Value == profileName);

                if (profile == null)
                {
                    Logger.Warn($"Profile '{profileName}' not found");
                    return;
                }

                // Update values from current properties
                SetElementValue(profile, "ScalingType", losslessScalingScalingType.Value);
                SetElementValue(profile, "Sharpness", losslessScalingSharpness.Value.ToString());
                SetElementValue(profile, "FSROptimize", losslessScalingFSROptimize.Value.ToString().ToLower());
                SetElementValue(profile, "Anime4KSize", losslessScalingAnime4KSize.Value);
                SetElementValue(profile, "Anime4KVRS", losslessScalingAnime4KVRS.Value.ToString().ToLower());
                SetElementValue(profile, "ScaleMode", losslessScalingScaleMode.Value);
                SetElementValue(profile, "ScaleFactor", losslessScalingScaleFactor.Value.ToString());
                SetElementValue(profile, "AspectRatio", losslessScalingAspectRatio.Value);
                SetElementValue(profile, "FrameGeneration", losslessScalingFrameGenType.Value);
                SetElementValue(profile, "LSFG3Mode1", losslessScalingLSFG3Mode.Value);
                SetElementValue(profile, "LSFG3Multiplier", losslessScalingLSFG3Multiplier.Value.ToString());
                SetElementValue(profile, "LSFG3Target", losslessScalingLSFG3Target.Value.ToString());
                SetElementValue(profile, "LSFG2Mode", losslessScalingLSFG2Mode.Value);
                SetElementValue(profile, "LSFGFlowScale", losslessScalingFlowScale.Value.ToString());
                SetElementValue(profile, "LSFGSize", losslessScalingSize.Value);
                SetElementValue(profile, "AutoScale", losslessScalingAutoScale.Value.ToString().ToLower());
                SetElementValue(profile, "AutoScaleDelay", losslessScalingAutoScaleDelay.Value.ToString());

                // Additional Settings.xml fields
                SetElementValue(profile, "SyncMode", losslessScalingSyncMode.Value);
                SetElementValue(profile, "CaptureApi", losslessScalingCaptureApi.Value);
                SetElementValue(profile, "DrawFps", losslessScalingDrawFps.Value.ToString().ToLower());
                SetElementValue(profile, "HdrSupport", losslessScalingHdrSupport.Value.ToString().ToLower());
                SetElementValue(profile, "GsyncSupport", losslessScalingGsyncSupport.Value.ToString().ToLower());
                SetElementValue(profile, "ResizeBeforeScaling", losslessScalingResizeBeforeScaling.Value.ToString().ToLower());
                SetElementValue(profile, "LS1Type", losslessScalingLS1Type.Value);
                SetElementValue(profile, "MaxFrameLatency", losslessScalingMaxFrameLatency.Value.ToString());

                doc.Save(SETTINGS_PATH);
                Logger.Info($"Settings written to profile '{profileName}'");
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to write Settings.xml: {ex.Message}");
            }
        }

        private void CreateProfile(string gameName, string exePath)
        {
            try
            {
                if (!File.Exists(SETTINGS_PATH))
                {
                    Logger.Warn("Settings.xml not found, cannot create profile");
                    return;
                }

                // Check if profile already exists
                if (!string.IsNullOrEmpty(FindProfileByExePath(exePath)))
                {
                    Logger.Warn($"Profile for '{exePath}' already exists");
                    return;
                }

                var doc = XDocument.Load(SETTINGS_PATH);
                var profilesElement = doc.Descendants("GameProfiles").FirstOrDefault();
                var defaultProfile = doc.Descendants("Profile")
                    .FirstOrDefault(p => p.Element("Title")?.Value == "Default");

                if (profilesElement == null || defaultProfile == null)
                {
                    Logger.Error("Cannot find GameProfiles or Default profile");
                    return;
                }

                // Clone Default profile
                var newProfile = new XElement(defaultProfile);
                newProfile.Element("Title").Value = gameName;

                // Add or update Path element
                var pathElement = newProfile.Element("Path");
                if (pathElement != null)
                {
                    pathElement.Value = exePath;
                }
                else
                {
                    newProfile.Add(new XElement("Path", exePath));
                }

                profilesElement.Add(newProfile);
                doc.Save(SETTINGS_PATH);

                Logger.Info($"Created profile '{gameName}' for '{exePath}'");

                // Update current profile
                lock (stateLock) { currentProfileName = gameName; }
                losslessScalingCurrentProfile.SetValue(gameName, DateTime.Now.Ticks);
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to create profile: {ex.Message}");
            }
        }

        private void SetElementValue(XElement parent, string name, string value)
        {
            var element = parent.Element(name);
            if (element != null)
            {
                element.Value = value;
            }
            else
            {
                parent.Add(new XElement(name, value));
            }
        }

        private void UpdatePropertiesFromSettings(LosslessScalingSettings settings)
        {
            long now = DateTime.Now.Ticks;
            losslessScalingScalingType.SetValue(settings.ScalingType, now);
            losslessScalingSharpness.SetValue(settings.Sharpness, now);
            losslessScalingFSROptimize.SetValue(settings.FSROptimize, now);
            losslessScalingAnime4KSize.SetValue(settings.Anime4KSize, now);
            losslessScalingAnime4KVRS.SetValue(settings.Anime4KVRS, now);
            losslessScalingScaleMode.SetValue(settings.ScaleMode, now);
            losslessScalingScaleFactor.SetValue(settings.ScaleFactor, now);
            losslessScalingAspectRatio.SetValue(settings.AspectRatio, now);
            losslessScalingFrameGenType.SetValue(settings.FrameGenType, now);
            losslessScalingLSFG3Mode.SetValue(settings.LSFG3Mode, now);
            losslessScalingLSFG3Multiplier.SetValue(settings.LSFG3Multiplier, now);
            losslessScalingLSFG3Target.SetValue(settings.LSFG3Target, now);
            losslessScalingLSFG2Mode.SetValue(settings.LSFG2Mode, now);
            losslessScalingFlowScale.SetValue(settings.FlowScale, now);
            losslessScalingSize.SetValue(settings.Size, now);
            losslessScalingAutoScale.SetValue(settings.AutoScale, now);
            losslessScalingAutoScaleDelay.SetValue(settings.AutoScaleDelay, now);

            // Additional Settings.xml fields
            losslessScalingSyncMode.SetValue(settings.SyncMode, now);
            losslessScalingCaptureApi.SetValue(settings.CaptureApi, now);
            losslessScalingDrawFps.SetValue(settings.DrawFps, now);
            losslessScalingHdrSupport.SetValue(settings.HdrSupport, now);
            losslessScalingGsyncSupport.SetValue(settings.GsyncSupport, now);
            losslessScalingResizeBeforeScaling.SetValue(settings.ResizeBeforeScaling, now);
            losslessScalingLS1Type.SetValue(settings.LS1Type, now);
            losslessScalingMaxFrameLatency.SetValue(settings.MaxFrameLatency, now);
        }

        #endregion

        #region Property Change Handlers

        private async void LosslessScalingEnabled_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            try
            {
                bool shouldBeEnabled = losslessScalingEnabled.Value;
                Logger.Info($"Lossless Scaling enabled changed to: {shouldBeEnabled}");

                if (!IsRunning())
                {
                    if (shouldBeEnabled)
                    {
                        Logger.Info("Lossless Scaling not running, launching...");
                        await LaunchLosslessScalingAsync();
                        await Task.Delay(1000);
                    }
                    else
                    {
                        Logger.Info("Lossless Scaling not running and should be disabled, nothing to do");
                        return;
                    }
                }

                bool active;
                lock (stateLock) { active = isScalingActive; }
                if (active != shouldBeEnabled)
                {
                    ToggleScaling();
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in LosslessScalingEnabled_PropertyChanged: {ex.Message}");
            }
        }

        private async void LosslessScalingSaveAndRestart_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            try
            {
                if (!losslessScalingSaveAndRestart.Value)
                    return;

                Logger.Info("Save and Restart triggered");

                // Write settings to the current profile
                string profileToWrite;
                lock (stateLock) { profileToWrite = currentProfileName; }
                WriteSettingsToProfile(profileToWrite);

                // Kill Lossless Scaling if running
                if (IsRunning())
                {
                    Logger.Info("Closing Lossless Scaling...");
                    var processes = Process.GetProcessesByName(PROCESS_NAME);
                    foreach (var proc in processes)
                    {
                        try
                        {
                            proc.Kill();
                            proc.WaitForExit(5000);
                        }
                        finally
                        {
                            proc.Dispose();
                        }
                    }
                    await Task.Delay(1000);
                }

                // Restart Lossless Scaling
                Logger.Info("Restarting Lossless Scaling...");
                await LaunchLosslessScalingAsync();

                // Reset the trigger
                losslessScalingSaveAndRestart.SetValue(false, DateTime.Now.Ticks);
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in LosslessScalingSaveAndRestart_PropertyChanged: {ex.Message}");
            }
        }

        private void LosslessScalingCreateProfile_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            string value = losslessScalingCreateProfile.Value;
            if (string.IsNullOrEmpty(value))
                return;

            // Value format: "GameName<||>ExePath" - using <||> as delimiter to avoid conflicts with | in window titles
            var parts = value.Split(new[] { "<||>" }, StringSplitOptions.None);
            if (parts.Length >= 2)
            {
                string gameName = parts[0];
                string exePath = parts[1];
                CreateProfile(gameName, exePath);
            }

            // Reset the trigger
            losslessScalingCreateProfile.SetValue("", DateTime.Now.Ticks);
        }

        private void LosslessScalingBringToForeground_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (!losslessScalingBringToForeground.Value)
                return;

            Logger.Info("Bring to foreground triggered");
            BringToForeground();

            // Reset the trigger
            losslessScalingBringToForeground.SetValue(false, DateTime.Now.Ticks);
        }

        private async void LosslessScalingLaunch_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            try
            {
                if (!losslessScalingLaunch.Value)
                    return;

                Logger.Info("Launch triggered from widget");
                await LaunchLosslessScalingAsync();

                // Reset the trigger
                losslessScalingLaunch.SetValue(false, DateTime.Now.Ticks);
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in LosslessScalingLaunch_PropertyChanged: {ex.Message}");
            }
        }

        // Reset the active LS profile back to the LS-default values, then push
        // the new state up to the widget so its UI reflects the reset without
        // needing the user to switch profiles.
        private void LosslessScalingResetProfile_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            try
            {
                if (!losslessScalingResetProfile.Value)
                    return;

                Logger.Info("Reset profile triggered from widget");
                var defaults = new LosslessScalingSettings(); // class defaults double as LS defaults
                UpdatePropertiesFromSettings(defaults);
                // Don't write to XML here — the widget will Apply-and-Restart if the
                // user wants the change persisted to LS. That keeps Reset reversible
                // until the user explicitly commits.

                losslessScalingResetProfile.SetValue(false, DateTime.Now.Ticks);
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in LosslessScalingResetProfile_PropertyChanged: {ex.Message}");
            }
        }

        #endregion
    }
}
