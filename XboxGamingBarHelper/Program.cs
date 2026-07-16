using NLog;
using Shared.Constants;
using Shared.Data;
using Shared.IPC;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.System;
using Windows.UI.Input.Preview.Injection;
using XboxGamingBarHelper.AMD;
using XboxGamingBarHelper.Core;
using XboxGamingBarHelper.ControllerEmulation;
using XboxGamingBarHelper.Devices.Libraries.GPD;
using XboxGamingBarHelper.Devices.Libraries.Legion;
using XboxGamingBarHelper.LosslessScaling;
using XboxGamingBarHelper.OnScreenDisplay;
using XboxGamingBarHelper.Performance;
using XboxGamingBarHelper.Power;
using XboxGamingBarHelper.Profile;
using XboxGamingBarHelper.RTSS;
using XboxGamingBarHelper.Settings;
using XboxGamingBarHelper.Systems;
using XboxGamingBarHelper.AutoTDP;
using XboxGamingBarHelper.DefaultGameProfiles;
using XboxGamingBarHelper.Labs;
using Shared.Enums;

namespace XboxGamingBarHelper
{
    internal partial class Program
    {
        // IMPORTANT: This field initializer MUST appear BEFORE the Logger field!
        // It ensures LogDirectory GDC is set before NLog creates the Logger.
        // NLog resolves ${gdc:item=LogDirectory} when the logger/target is first used,
        // so we need LogDirectory set before any logging happens.
        private static readonly bool _logDirConfigured = InitLogDirectory();

        private static bool InitLogDirectory()
        {
            ConfigureLogDirectory();
            return true;
        }

        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private static Mutex singleInstanceMutex;
        // Held by the --setup helper while it deploys files + creates the scheduled
        // task + waits for the new task-launched helper to acquire the main mutex.
        // The new helper's AnotherHelperIsAlive() check skips peers while this kernel
        // object is alive so the setup helper isn't mistaken for a duplicate during
        // the handoff. Auto-releases when the setup process exits.
        private const string SetupInProgressMutexName = "Global\\GoTweaks_SetupInProgress";
        private static Mutex setupInProgressMutex;
        private static CancellationToken _serviceCancellationToken;
        private static bool _isRunningAsService = false;
        private static volatile bool _isShuttingDown = false;
        private static volatile bool _restartInProgress = false;
        private static HelperTrayIndicator _trayIndicator;

        // P/Invoke for SetDllDirectory - must be called BEFORE any native DLLs are loaded
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetDllDirectory(string lpPathName);

        // P/Invoke for LoadLibrary - used to explicitly preload native DLLs with full path
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        // P/Invoke for screen saver idle detection and monitor power control
        [DllImport("user32.dll")]
        private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostThreadMessage(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        private static readonly IntPtr HWND_BROADCAST = new IntPtr(0xFFFF);
        private const uint WM_SYSCOMMAND = 0x0112;
        private const uint WM_QUIT = 0x0012;
        private static readonly IntPtr SC_MONITORPOWER = new IntPtr(0xF170);
        private static readonly IntPtr MONITOR_OFF = new IntPtr(2);

        // Managers
        private static PerformanceManager performanceManager;
        private static RTSSManager rtssManager;
        private static ProfileManager profileManager;
        private static SystemManager systemManager;
        private static PowerManager powerManager;
        private static AMDManager amdManager;
        private static LosslessScalingManager losslessScalingManager;
        private static SettingsManager settingsManager;
        private static LegionManager legionManager;
        private static GPDManager gpdManager;
        private static ControllerEmulationManager controllerEmulationManager;
        private static XboxGamingBarHelper.ControllerEmulation.Viiper.ViiperEmulationManager viiperEmulationManager;
        private static AutoTDPManager autoTDPManager;
        private static DefaultGameProfileManager defaultGameProfileManager;
        private static List<IManager> Managers;

        public static OnScreenDisplayProperty onScreenDisplay;
        public static List<OnScreenDisplayManager> onScreenDisplayProviders;

        // Properties
        private static HelperProperties properties;

        /// <summary>
        /// Guard flag to prevent reentrant profile change handling.
        /// Prevents race conditions during rapid game switches.
        /// Also used by TDPBoostProperties to skip redundant TDP re-apply during profile application.
        /// </summary>
        internal static bool isApplyingProfile = false;

        /// <summary>
        /// Timestamp when the last profile switch completed.
        /// Used to implement a cooldown period to reject stale widget messages.
        /// </summary>
        private static DateTime profileSwitchTime = DateTime.MinValue;

        /// <summary>
        /// Cooldown period in milliseconds after a profile switch.
        /// Stale widget messages arriving during this period are rejected to prevent profile corruption.
        /// </summary>
        private const int PROFILE_SWITCH_COOLDOWN_MS = 500;

        /// <summary>
        /// Helper method to check if we're in the cooldown period after a profile switch.
        /// </summary>
        private static bool IsInProfileSwitchCooldown()
        {
            return (DateTime.UtcNow - profileSwitchTime).TotalMilliseconds < PROFILE_SWITCH_COOLDOWN_MS;
        }

        /// <summary>
        /// Lock object to ensure atomic profile application.
        /// Prevents race conditions when rapid game switches cause interleaved settings.
        /// </summary>
        private static readonly object profileApplicationLock = new object();

        /// <summary>
        /// Input injector for sending keyboard shortcuts (works in widget context unlike SendInput)
        /// </summary>
        private static InputInjector inputInjector;

        /// <summary>
        /// Labs: Unified Legion button monitor (handles both L and R buttons + battery)
        /// </summary>
        // internal so siblings (ControllerEmulationManager.CalibrateGyro) can route
        // through the shared HID handle instead of opening a parallel one.
        internal static LegionButtonMonitor legionButtonMonitor;
        private static readonly object legionButtonMonitorLock = new object();
        private static bool legionButtonMonitorBatteryHooked;

        // GoTweaks lighting + standalone haptics. Both consume the shared LegionButtonMonitor
        // press-edge stream; lighting also needs the monitor's HID handle to write RGB.
        internal static XboxGamingBarHelper.Labs.LegionLightingManager legionLightingManager;
        internal static XboxGamingBarHelper.Labs.GoTweaksHapticManager goTweaksHapticManager;
        private static bool goTweaksFeaturesHooked;

        /// <summary>
        /// Hotkey manager for global keyboard shortcuts (Ctrl+Shift+D for Desktop Controls)
        /// </summary>
        private static HotkeyManager hotkeyManager;

        /// <summary>
        /// Controller hotkey monitor for gamepad button combos (Menu+DPad, View+ABXY)
        /// Uses XInput to detect combos system-wide, including in games
        /// </summary>
        private static ControllerHotkeyMonitor controllerHotkeyMonitor;

        /// <summary>
        /// Named Pipe server for IPC with the widget (works when elevated via scheduled task)
        /// </summary>
        private static IPC.NamedPipeServer pipeServer;

        /// <summary>
        /// Flag indicating whether all managers are initialized and ready to handle requests.
        /// When false, BatchGet requests will return a "NotReady" response.
        /// </summary>
        private static volatile bool _managersReady = false;

        /// <summary>
        /// Heartbeat file path for widget to detect if helper is running
        /// </summary>
        private static string heartbeatFilePath;
        private static DateTime lastHeartbeatWrite = DateTime.MinValue;
        private const int HeartbeatIntervalMs = 2000;

        /// <summary>
        /// Helper version string for widget to detect version mismatch after updates
        /// </summary>
        private static string helperVersion = "0.0.0.0";

        /// <summary>
        /// Debounce for Focus GoTweaks to prevent rapid button presses from flooding the system
        /// </summary>
        private static DateTime lastFocusWidgetTime = DateTime.MinValue;
        private const int FocusWidgetDebounceMs = 200;

        /// <summary>
        /// Package uninstall detection - path to the package's data folder
        /// </summary>
        private static string packageDataFolder;
        private static DateTime lastUninstallCheck = DateTime.MinValue;
        private const int UninstallCheckIntervalMs = 60000; // Check every 60 seconds

        /// <summary>
        /// Screen saver idle monitoring - triggers Windows screen saver after idle timeout
        /// </summary>
        private static volatile bool screenSaverEnabled = false;
        private static volatile bool screenSaverTriggered = false;
        private static System.Threading.Timer screenSaverTimer;
        private const int ScreenSaverIdleTimeoutMs = 60000; // 60 seconds idle before triggering
        private const int ScreenSaverCheckIntervalMs = 5000; // Check every 5 seconds

        /// <summary>
        /// Setup/environment health re-check (conflicting OEM software can be
        /// launched at any time, PawnIO can be installed mid-session). Push is
        /// change-gated inside SendSetupWarningsToWidget so a 2-min cadence
        /// costs one process-list walk and usually no pipe traffic.
        /// </summary>
        private static System.Threading.Timer setupHealthTimer;
        private const int SetupHealthCheckIntervalMs = 2 * 60 * 1000;

        /// <summary>
        /// Auto hibernate idle monitoring - hibernates after inactivity timeout
        /// </summary>

        /// <summary>
        /// Configures NLog to write logs to the package's LocalCache/Local folder.
        /// Must be called BEFORE any logging happens.
        /// </summary>
        private static void ConfigureLogDirectory()
        {
            string logDir = null;

            try
            {
                // Try to get the package's LocalCache path
                try
                {
                    var localCache = global::Windows.Storage.ApplicationData.Current.LocalCacheFolder;
                    logDir = Path.Combine(localCache.Path, "Local");
                }
                catch
                {
                    // Not running in package context - try to extract from exe path
                    var exePath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
                    if (exePath.Contains("LocalCache"))
                    {
                        int idx = exePath.IndexOf("LocalCache", StringComparison.OrdinalIgnoreCase);
                        if (idx > 0)
                        {
                            logDir = Path.Combine(exePath.Substring(0, idx + "LocalCache".Length), "Local");
                        }
                    }
                }

                // Fallback to user's LocalAppData if we couldn't determine package path
                if (string.IsNullOrEmpty(logDir))
                {
                    logDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                }

                // Ensure the log directory exists
                Directory.CreateDirectory(logDir);

                // Set the NLog GDC variable
                NLog.GlobalDiagnosticsContext.Set("LogDirectory", logDir);

                // Force NLog to reconfigure to pick up the new LogDirectory
                LogManager.ReconfigExistingLoggers();
            }
            catch
            {
                // If all else fails, use LocalApplicationData
                var fallback = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                NLog.GlobalDiagnosticsContext.Set("LogDirectory", fallback);
            }
        }

        static async Task Main(string[] args)
        {
            // Set log directory BEFORE any logging happens
            // This ensures logs go to the package's LocalCache/Local folder even when running elevated
            ConfigureLogDirectory();

            // Log startup info
            Logger.Info($"=== Helper starting, PID={Process.GetCurrentProcess().Id} ===");
            LogManager.Flush();

            // Early process-uniqueness gate. Catches the case where a previous helper
            // crashed/zombied (released or disposed its single-instance mutex but didn't
            // fully exit), or where the bootstrapper fired schtasks /Run twice during a
            // redeploy. Without this gate, two elevated helpers can race past the mutex
            // check in Main()/RunAsService(), each creating its own ViGEm Guide pad and
            // leaving stale PnP entries that survive reboot.
            //
            // Skip the gate for one-shot CLI modes (they're expected to coexist with a
            // running helper for the duration of their work and exit immediately after).
            bool isOneShotMode = args != null && (
                args.Contains("--export-profiles") ||
                args.Contains("--setup") ||
                args.Contains("--uninstall"));
            if (!isOneShotMode && AnotherHelperIsAlive())
            {
                // A peer is alive. It may be a healthy running helper (we should exit) OR an old
                // instance mid-shutdown after a kill/update/restart (we should wait for it to go,
                // then take over). The widget relaunches on pipe-disconnect, so this path is hit
                // during the dying helper's shutdown window — exiting immediately there left the
                // user with no helper; racing it spawned two. Wait up to ~5s for the peer to exit,
                // re-checking, before giving up. (issue #81 — two helpers racing for the pipe.)
                bool peerGone = false;
                for (int i = 0; i < 10; i++)
                {
                    System.Threading.Thread.Sleep(500);
                    if (!AnotherHelperIsAlive()) { peerGone = true; break; }
                }
                if (!peerGone)
                {
                    Logger.Warn("Another XboxGamingBarHelper.exe is still running after wait. Exiting to prevent duplicate.");
                    LogManager.Flush();
                    return;
                }
                Logger.Info("Peer helper exited during wait — proceeding with startup (takeover after kill/restart).");
            }

            // Process-exit + unhandled-exception cleanup. Two shared concerns:
            //
            //   1. EC fan override: 0xC6C8 keeps the last RPM we wrote until
            //      reboot. Release it so the fan returns to firmware control.
            //
            //   2. HidHide suppression: BlockedInstanceIds live in HidHide's
            //      driver registry and persist across helper death. If we
            //      leave the Legion hidden, games can't see it until the
            //      next helper boot reapplies state — and if the helper
            //      stays dead, the user has no controller. Clear it here so
            //      the user's pad is always visible when the helper isn't.
            //      The next graceful start re-applies the right state.
            //
            // ProcessExit covers normal shutdowns and most crashes that
            // unwind through the runtime; UnhandledException covers the rest.
            // Neither fires on TerminateProcess (hard kill) — the only
            // remaining gap, which the helper recovers from on next start
            // (ApplySuppressionInner diffs against current state).
            AppDomain.CurrentDomain.ProcessExit += (s, e) =>
            {
                try
                {
                    Logger.Warn("ProcessExit fired — releasing EC fan + HidHide suppression + VIIPER bus before shutdown");
                    // Flush any pending debounced GameProfile writes (global.xml etc, 250ms debounce
                    // in Shared/Data/GameProfile.cs) - without this a TDP change made just before an
                    // update/exit-triggered Environment.Exit(0) is silently lost and the profile
                    // reverts to whatever was last flushed.
                    try { Shared.Data.GameProfile.FlushAllPendingWrites(); }
                    catch (Exception ex) { Logger.Warn($"ProcessExit FlushAllPendingWrites threw: {ex.Message}"); }
                    legionManager?.EmergencyReleaseFanOverride();
                    try { controllerEmulationManager?.SuppressionManager?.Disable(); }
                    catch (Exception ex) { Logger.Warn($"ProcessExit HidHide.Disable threw: {ex.Message}"); }
                    try { viiperEmulationManager?.Dispose(); }
                    catch (Exception ex) { Logger.Warn($"ProcessExit VIIPER.Dispose threw: {ex.Message}"); }
                    LogManager.Flush();
                }
                catch { }
            };
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                try
                {
                    Logger.Error($"UnhandledException — releasing EC fan + HidHide suppression + VIIPER bus. Exception: {e.ExceptionObject}");
                    try { Shared.Data.GameProfile.FlushAllPendingWrites(); }
                    catch (Exception ex) { Logger.Warn($"UnhandledException FlushAllPendingWrites threw: {ex.Message}"); }
                    legionManager?.EmergencyReleaseFanOverride();
                    try { controllerEmulationManager?.SuppressionManager?.Disable(); }
                    catch (Exception ex) { Logger.Warn($"UnhandledException HidHide.Disable threw: {ex.Message}"); }
                    try { viiperEmulationManager?.Dispose(); }
                    catch (Exception ex) { Logger.Warn($"UnhandledException VIIPER.Dispose threw: {ex.Message}"); }
                    LogManager.Flush();
                }
                catch { }
            };

            // Check for export-profiles mode - exports registry game profiles to bundled JSON
            // Run this on a system with Xbox FSE enabled to generate bundled_profiles.json
            if (args.Contains("--export-profiles"))
            {
                Logger.Info("=== Export Profiles Mode ===");
                var outputPath = args.SkipWhile(a => a != "--export-profiles").Skip(1).FirstOrDefault()
                    ?? Path.Combine(Environment.CurrentDirectory, "bundled_profiles.json");

                Logger.Info($"Exporting profiles to: {outputPath}");
                var count = BundledProfileExporter.ExportToJson(outputPath);
                Logger.Info($"Exported {count} game profiles");
                Console.WriteLine($"Exported {count} game profiles to: {outputPath}");
                LogManager.Flush();
                return;
            }

            // Uninstall restoration mode: stop peers, clear our HidHide rules,
            // sweep phantom pads, remove the scheduled task + deployed copy,
            // optionally uninstall the driver stack (--remove-drivers), then EXIT.
            // Runnable from the deployed helper even after the MSIX package is
            // gone — this is what Uninstall-GoTweaks.ps1 invokes.
            if (args.Contains("--uninstall"))
            {
                Logger.Info("=== Uninstall Mode ===");
                Services.UninstallService.Run(removeDrivers: args.Contains("--remove-drivers"));
                LogManager.Flush();
                return;
            }

            // Check for setup mode FIRST (before anything else)
            // Setup mode: deploy files, create scheduled task, run task, then EXIT
            // The task launches the elevated helper which will connect to the widget.
            if (args.Contains("--setup"))
            {
                // Signal that setup is in progress via a Global\ mutex. The newly
                // task-launched helper checks this in AnotherHelperIsAlive() so that
                // the setup process (still alive while polling for the new helper's
                // main mutex) is not mistaken for a duplicate. Auto-releases on exit.
                try
                {
                    setupInProgressMutex = new Mutex(true, SetupInProgressMutexName, out _);
                    Logger.Info("Setup-in-progress mutex acquired");
                }
                catch (Exception ex)
                {
                    Logger.Warn($"Could not acquire setup-in-progress mutex: {ex.Message} — continuing anyway");
                }

                // Debug file for tracing setup issues (independent of NLog)
                var setupDebugPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "setup_debug.txt");
                void SetupDebugLog(string msg) { try { File.AppendAllText(setupDebugPath, $"{DateTime.Now}: [Main] {msg}\n"); } catch { } }

                SetupDebugLog($"Setup mode entered, args={string.Join(" ", args)}");
                Logger.Info("=== Setup Mode ===");
                try
                {
                    SetupDebugLog("Calling PerformSetup...");
                    bool success = ElevationBootstrapper.PerformSetup();
                    SetupDebugLog($"PerformSetup returned: {success}");
                    Logger.Info($"Setup completed with result: {success}");

                    if (success)
                    {
                        // Run the scheduled task to start the elevated helper
                        // This helper will connect to the widget
                        Logger.Info("Running scheduled task to start elevated helper...");
                        SetupDebugLog("Running scheduled task...");

                        // CRITICAL: Shutdown NLog to release the log file BEFORE starting the elevated helper
                        // Otherwise the elevated helper's Wave1 initialization logs (including AMDManager/ADLX)
                        // will be blocked because this process still has the log file open
                        LogManager.Shutdown();

                        if (Services.ScheduledTaskService.RunTaskNow())
                        {
                            SetupDebugLog("Task started OK");
                        }
                        else
                        {
                            SetupDebugLog("Task failed to start");
                        }
                    }

                    SetupDebugLog("Exiting setup mode (terminating process)");
                    // Terminate hard: a plain return left the setup instance
                    // alive indefinitely, and Environment.Exit(0) DEADLOCKED in
                    // shutdown plumbing (verified 2026-07-09: setup_debug.txt
                    // reached the line before Exit and the process was still
                    // alive 6+ minutes later — the Task Scheduler COM apartment
                    // wedges finalization). Kill() is TerminateProcess: no
                    // shutdown ceremony, nothing to deadlock on. Deploy is
                    // complete and logs are flushed at this point.
                    System.Diagnostics.Process.GetCurrentProcess().Kill();
                    return;
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Setup failed with exception");
                    SetupDebugLog($"EXCEPTION in setup: {ex.Message}\n{ex.StackTrace}");
                    LogManager.Flush();
                    return; // Exit on setup failure
                }
            }

            // Check if running as a Windows Service (MSIX Desktop Service)
            // Services are started by SCM and have no console/interactive session
            bool isService = !Environment.UserInteractive;

            if (isService)
            {
                // Running as Windows Service - let SCM handle the lifecycle
                Logger.Info("Starting as Windows Service");
                _isRunningAsService = true;
                ServiceBase.Run(new GoTweaksService());
                return;
            }

            // Running interactively (console/debug mode or via FullTrustProcessLauncher)
            Logger.Info("Starting in interactive mode");

            // Self-elevation bootstrap - only needed in interactive mode
            // Service runs as LocalSystem which is already elevated
            if (!ElevationBootstrapper.EnsureElevated(args))
            {
                return; // Relaunching elevated via scheduled task, exit this instance
            }

            // Ensure only one instance of the helper runs at a time
            const string mutexName = "Global\\XboxGamingBarHelper_SingleInstance";
            bool createdNew;

            try
            {
                singleInstanceMutex = new Mutex(true, mutexName, out createdNew);
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to create mutex: {ex.Message}");
                return;
            }

            if (!createdNew)
            {
                Logger.Warn("Another instance of XboxGamingBarHelper is already running. Exiting.");
                return;
            }

            Logger.Info("Single instance mutex acquired. Starting helper.");
            LogManager.Flush(); // Ensure mutex acquisition log is written before Initialize

            try
            {
                TryStartTrayIndicator();
                await Initialize();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "FATAL: Unhandled exception in Initialize()");
                LogManager.Flush();
                throw;
            }
            finally
            {
                DisposeTrayIndicator();
                // Intentionally NOT releasing/disposing singleInstanceMutex here.
                // The kernel releases named mutexes automatically on process exit.
                // Releasing in finally caused a duplicate-helper race: a slow shutdown
                // would free the mutex while threads were still draining, letting a
                // new schtasks /Run-spawned helper acquire it and run concurrently
                // (each creating its own ViGEm Guide pad → 2 phantom Xbox 360
                // controllers surviving reboot).
            }

        }

        /// <summary>
        /// Belt-and-suspenders single-instance check that does not depend on the named
        /// mutex. Returns true when any other XboxGamingBarHelper.exe is alive in this
        /// session. The named mutex is the primary guard; this catches edge cases:
        ///   - a previous helper zombied (released the mutex but is still alive),
        ///   - a non-elevated launcher cannot open the elevated helper's Global\ mutex
        ///     (mandatory integrity policy denies Medium-IL OpenExisting against
        ///     High-IL kernel objects).
        /// </summary>
        private static bool AnotherHelperIsAlive()
        {
            try
            {
                var self = Process.GetCurrentProcess();
                int selfPid = self.Id;
                int selfSession = self.SessionId;
                Process[] peers = Process.GetProcessesByName("XboxGamingBarHelper");
                bool found = false;
                var foundPids = new List<int>();
                foreach (var p in peers)
                {
                    try
                    {
                        if (p.Id == selfPid) continue;
                        if (p.HasExited) continue;
                        // Same-session peers only — different sessions would belong to
                        // a different user and the named pipe would not collide.
                        if (p.SessionId != selfSession) continue;
                        found = true;
                        foundPids.Add(p.Id);
                    }
                    catch { /* process may have exited between enum and access */ }
                    finally
                    {
                        try { p.Dispose(); } catch { }
                    }
                }
                if (!found) return false;

                // If --setup is in progress, the only alive peer is almost certainly
                // the setup-mode helper (waiting on us via RunTaskNow polling). Don't
                // treat it as a duplicate — proceed with normal startup so we can
                // acquire the main mutex, which is what setup's RunTaskNow is waiting
                // to see. The setup helper exits as soon as we take that mutex, so the
                // brief overlap is intentional and harmless.
                if (IsSetupInProgress())
                {
                    Logger.Info($"Peer(s) found ({string.Join(",", foundPids)}) but setup-in-progress mutex is held — proceeding with normal startup (setup-side handoff)");
                    return false;
                }

                Logger.Info($"Existing helper PIDs in session {selfSession}: {string.Join(", ", foundPids)}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Debug($"AnotherHelperIsAlive check failed: {ex.Message}");
                return false; // fail-open: don't block startup on probe failures
            }
        }

        private static bool IsSetupInProgress()
        {
            try
            {
                using (Mutex.OpenExisting(SetupInProgressMutexName))
                {
                    return true;
                }
            }
            catch (WaitHandleCannotBeOpenedException) { return false; }
            catch (Exception ex)
            {
                Logger.Debug($"IsSetupInProgress probe failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Entry point when running as a Windows Service.
        /// Called by GoTweaksService.OnStart().
        /// </summary>
        public static async Task RunAsService(CancellationToken cancellationToken)
        {
            Logger.Info("RunAsService starting...");
            _serviceCancellationToken = cancellationToken;
            _isRunningAsService = true;

            // Ensure only one instance of the helper runs at a time
            const string mutexName = "Global\\XboxGamingBarHelper_SingleInstance";
            bool createdNew;

            try
            {
                singleInstanceMutex = new Mutex(true, mutexName, out createdNew);
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to create mutex: {ex.Message}");
                return;
            }

            if (!createdNew)
            {
                Logger.Warn("Another instance of XboxGamingBarHelper is already running. Service will wait.");
                // In service mode, we might want to wait for the other instance to exit
                // For now, just return - the service will be marked as started but won't do anything
                return;
            }

            Logger.Info("Single instance mutex acquired. Starting service helper.");

            try
            {
                await Initialize();
            }
            finally
            {
                // Intentionally not releasing the mutex here — see Main() for rationale.
                // The kernel releases the named mutex on process exit; releasing it on
                // shutdown opens a window where a concurrently-starting helper can
                // acquire it while this process is still draining threads.
            }
        }

        /// <summary>
        /// Cleanup when service is stopping.
        /// Called by GoTweaksService.OnStop().
        /// </summary>
        public static void Shutdown()
        {
            Logger.Info("Shutdown called");
            _isShuttingDown = true;
            DisposeTrayIndicator();

            // Flush any pending debounced profile writes so we don't lose the last tick of changes.
            try { Shared.Data.GameProfile.FlushAllPendingWrites(); }
            catch (Exception ex) { Logger.Error(ex, "Error flushing pending profile writes"); }

            try
            {
                // Dispose managers
                if (Managers != null)
                {
                    foreach (var manager in Managers)
                    {
                        try
                        {
                            (manager as IDisposable)?.Dispose();
                        }
                        catch (Exception ex)
                        {
                            Logger.Error(ex, $"Error disposing manager {manager.GetType().Name}");
                        }
                    }
                }

                // Dispose hotkey manager
                hotkeyManager?.Dispose();
                hotkeyManager = null;

                // Dispose Legion button monitor
                DisposeLegionButtonMonitor();

                // Delete heartbeat file on shutdown
                DeleteHeartbeatFile();

                Logger.Info("Shutdown complete");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error during shutdown");
            }
        }

        /// <summary>
        /// Write heartbeat file so widget can detect if helper is running.
        /// Called every HeartbeatIntervalMs in main loop.
        /// </summary>
        private static void WriteHeartbeat()
        {
            if ((DateTime.Now - lastHeartbeatWrite).TotalMilliseconds < HeartbeatIntervalMs)
                return;

            try
            {
                if (string.IsNullOrEmpty(heartbeatFilePath))
                {
                    // Initialize heartbeat file path on first write
                    string localStateFolder;
                    try
                    {
                        // Try to use Package.Current (works when running in package context)
                        localStateFolder = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                            "Packages",
                            Package.Current.Id.FamilyName,
                            "LocalState"
                        );
                    }
                    catch
                    {
                        // Fallback for elevated mode (no package identity)
                        // Use hardcoded package family name (same as LocalSettingsHelper)
                        localStateFolder = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                            "Packages",
                            "PlayandBuildCustom.10365195AA1EC_8edemd50ez3gg",
                            "LocalState"
                        );
                    }
                    heartbeatFilePath = Path.Combine(localStateFolder, "helper_heartbeat.json");
                }

                var heartbeat = new
                {
                    pid = Process.GetCurrentProcess().Id,
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    connected = pipeServer?.IsConnected ?? false,
                    elevated = ElevationBootstrapper.IsRunningAsAdmin(),
                    version = helperVersion
                };

                string json = $"{{\"pid\":{heartbeat.pid},\"timestamp\":{heartbeat.timestamp},\"connected\":{heartbeat.connected.ToString().ToLower()},\"elevated\":{heartbeat.elevated.ToString().ToLower()},\"version\":\"{heartbeat.version}\"}}";
                File.WriteAllText(heartbeatFilePath, json);
                lastHeartbeatWrite = DateTime.Now;
            }
            catch (Exception ex)
            {
                Logger.Debug($"Failed to write heartbeat: {ex.Message}");
            }
        }

        /// <summary>
        /// Delete heartbeat file on shutdown so widget knows helper is not running.
        /// </summary>
        private static void DeleteHeartbeatFile()
        {
            try
            {
                if (!string.IsNullOrEmpty(heartbeatFilePath) && File.Exists(heartbeatFilePath))
                {
                    File.Delete(heartbeatFilePath);
                    Logger.Info("Heartbeat file deleted");
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"Failed to delete heartbeat file: {ex.Message}");
            }
        }

        /// <summary>
        /// Initialize the app service connection and named pipe server
        /// </summary>
        private static void InitializeConnection()
        {
            Logger.Info("Initialize connection...");

            // Start Named Pipe server (primary communication method - works when elevated)
            try
            {
                pipeServer = new IPC.NamedPipeServer();
                pipeServer.MessageReceived += PipeServer_MessageReceived;
                pipeServer.Connected += (s, e) =>
                {
                    Logger.Info("Widget connected via Named Pipe");
                    // Snapshot every saved per-mode fan curve + unlock state so the
                    // widget can populate its cache and let the user edit any mode
                    // without changing the running power mode.
                    try { legionManager?.PushAllPerModeStateToWidget(); }
                    catch (Exception ex) { Logger.Warn($"Failed to push per-mode fan curve state on connect: {ex.Message}"); }
                    // Push the persisted software gyro bias offset so the Calibrate Gyro Bias
                    // status text in the widget reflects the saved state immediately on connect.
                    try { SendGyroBiasOffsetToWidget(); }
                    catch (Exception ex) { Logger.Warn($"Failed to push gyro bias offset on connect: {ex.Message}"); }
                    // Setup/environment health (conflicting OEM software, missing PawnIO) —
                    // force so a freshly-connected widget always gets current state.
                    try { SendSetupWarningsToWidget(force: true); }
                    catch (Exception ex) { Logger.Warn($"Failed to push setup warnings on connect: {ex.Message}"); }
                    // Controller battery/connection state is change-gated at the source, so a
                    // widget connecting after the last change (boot: battery pinned at 100%)
                    // would otherwise show "--" / Detached until something changes. The widget
                    // can connect while the helper is still constructing its managers (observed:
                    // connect at helper start +7s with legionManager still null), so wait for the
                    // manager rather than silently no-op'ing.
                    _ = System.Threading.Tasks.Task.Run(async () =>
                    {
                        try
                        {
                            for (int i = 0; i < 30 && legionManager == null; i++)
                            {
                                await System.Threading.Tasks.Task.Delay(1000);
                            }
                            legionManager?.ResyncControllerStatusToWidget();
                        }
                        catch (Exception ex) { Logger.Warn($"Failed to resync controller status on connect: {ex.Message}"); }
                    });
                };
                pipeServer.Disconnected += (s, e) => Logger.Info("Widget disconnected from Named Pipe");
                pipeServer.Start();
                Logger.Info($"Named Pipe server started: {IPC.NamedPipeServer.FullPipePath}");

                // Periodic setup-health re-check; push is change-gated so this is quiet
                // unless something actually changes (Legion Space launched, PawnIO installed).
                setupHealthTimer = new System.Threading.Timer(
                    _ => SendSetupWarningsToWidget(),
                    null, SetupHealthCheckIntervalMs, SetupHealthCheckIntervalMs);
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to start Named Pipe server: {ex.Message}");
            }
        }

        /// <summary>
        /// Initialize DefaultGameProfileManager in background.
        /// Deferred because it's not needed for initial UI and takes ~600ms.
        /// DGP only activates when a game starts running.
        /// </summary>
        private static async void InitializeDefaultGameProfileManagerAsync()
        {
            await Task.Run(() =>
            {
                var dgpTimer = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    defaultGameProfileManager = new DefaultGameProfileManager(performanceManager, rtssManager, systemManager, profileManager, legionManager);

                    if (defaultGameProfileManager != null)
                    {
                        // Wait for Managers list to be initialized before trying to lock it
                        while (Managers == null)
                        {
                            Thread.Sleep(10);
                        }

                        // Add to Managers list for cleanup
                        lock (Managers)
                        {
                            Managers.Add(defaultGameProfileManager);
                        }

                        // Wait for properties to be initialized before adding DGP properties
                        // The properties object is created in Initialize() which runs before this
                        while (properties == null)
                        {
                            Thread.Sleep(10);
                        }

                        // Add properties dynamically using thread-safe Add method
                        properties.Add(defaultGameProfileManager.ProfileAvailable);
                        properties.Add(defaultGameProfileManager.ProfileData);
                        properties.Add(defaultGameProfileManager.ProfileEnabled);
                        properties.Add(defaultGameProfileManager.ForceProfile);

                        Logger.Info("DefaultGameProfileManager properties added to helper");
                    }

                    dgpTimer.Stop();
                    Logger.Info($"[TIMING] DefaultGameProfileManager (background): {dgpTimer.ElapsedMilliseconds}ms");
                }
                catch (Exception ex)
                {
                    Logger.Error($"Failed to initialize DefaultGameProfileManager: {ex.Message}");
                    Logger.Error($"Stack trace: {ex.StackTrace}");
                    defaultGameProfileManager = null;
                }
            });
        }

        /// <summary>
        /// Wait for the widget to connect via Named Pipe
        /// </summary>
        private static Task WaitForWidgetConnection(bool blocking)
        {
            if (blocking && pipeServer != null)
            {
                Logger.Info("Waiting for widget to connect via Named Pipe...");
                // Don't block forever - the main loop will handle reconnection
                // The pipe server is already listening for connections
            }
            return Task.CompletedTask;
        }

        /// <summary>
        /// Open connection to UWP app service
        /// </summary>
        private static async Task Initialize()
        {
            var initTimer = System.Diagnostics.Stopwatch.StartNew();

            // Initialize package data folder path for uninstall detection
            InitializePackageDataFolder();

            //while (!System.Diagnostics.Debugger.IsAttached)
            //{
            //    await Task.Delay(500);
            //}

            // Set DLL directory to exe location BEFORE loading any native DLLs (ADLX, etc.)
            // This is critical for elevated helper running from deployed location
            try
            {
                var exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exePath))
                {
                    var exeDir = Path.GetDirectoryName(exePath);
                    if (!string.IsNullOrEmpty(exeDir))
                    {
                        SetDllDirectory(exeDir);
                        Logger.Info($"SetDllDirectory to: {exeDir}");

                        // Preload ADLXCSharpBind.dll with full path to ensure it's found
                        // This bypasses DLL search path issues when running from deployed location
                        var adlxDllPath = Path.Combine(exeDir, "ADLXCSharpBind.dll");
                        if (File.Exists(adlxDllPath))
                        {
                            var handle = LoadLibrary(adlxDllPath);
                            if (handle == IntPtr.Zero)
                            {
                                var error = Marshal.GetLastWin32Error();
                                Logger.Error($"Failed to preload ADLXCSharpBind.dll: Win32 error {error} (0x{error:X})");
                            }
                            else
                            {
                                Logger.Info($"Preloaded ADLXCSharpBind.dll from: {adlxDllPath}");
                            }
                        }
                        else
                        {
                            Logger.Warn($"ADLXCSharpBind.dll not found at: {adlxDllPath}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Could not set DLL directory: {ex.Message}");
            }

            // RESOLVE OUR VERSION AND WRITE FRESH HEARTBEAT BEFORE OPENING THE PIPE.
            // The widget's update-debug flow has an old helper v(N) in place, launches our
            // new helper v(N+1) which replaces it. The widget reads the "version" field of
            // helper_heartbeat.json to decide whether it has connected to the correct helper.
            // If we open the pipe server before our new version has been written to the
            // heartbeat, the widget connects, reads the OLD helper's stale version from
            // the file, flags it as "wrong helper version", disconnects, and retries — and
            // keeps retrying for the whole managers-initialization window (~15 s), showing
            // the "Initial setup in progress" banner forever. Writing the heartbeat with
            // our version first guarantees the widget's very first version check sees the
            // correct value.
            try
            {
                var packageVersion = Package.Current.Id.Version;
                helperVersion = $"{packageVersion.Major}.{packageVersion.Minor}.{packageVersion.Build}.{packageVersion.Revision}";
            }
            catch
            {
                try
                {
                    var deployedVersion = Services.HelperDeploymentService.GetDeployedVersion();
                    if (!string.IsNullOrEmpty(deployedVersion)) helperVersion = deployedVersion;
                }
                catch { /* fall back to "0.0.0.0" default */ }
            }
            Logger.Info($"GoTweaks Helper starting v{helperVersion} — writing initial heartbeat");
            WriteHeartbeat();

            // One-time migration: clear the short-lived LegionCustomTDPFast/Peak keys shipped in
            // 0.3.2426. They were replaced by per-profile TDP Boost deltas (TDPBoostSPPT /
            // TDPBoostFPPT on each GameProfile), and the Legion-tab Custom SPL/SPPL/FPPT sliders
            // were removed entirely. The Remove call is a no-op when the keys aren't present.
            try
            {
                Settings.LocalSettingsHelper.Remove("LegionCustomTDPFast");
                Settings.LocalSettingsHelper.Remove("LegionCustomTDPPeak");
            }
            catch (Exception migEx) { Logger.Debug($"LegionCustomTDP cleanup migration threw: {migEx.Message}"); }

            // START PIPE SERVER EARLY - Widget can connect while managers initialize
            // BatchGet requests will return "NotReady" until _managersReady is true
            var pipeTimer = System.Diagnostics.Stopwatch.StartNew();
            InitializeConnection();
            pipeTimer.Stop();
            Logger.Info($"[TIMING] Pipe server started early: {pipeTimer.ElapsedMilliseconds}ms");

            // PRE-POPULATE DeviceDetector cache BEFORE parallel initialization
            // This avoids duplicate WMI queries when LegionManager and GPDManager both call DetectDevice()
            var deviceTimer = System.Diagnostics.Stopwatch.StartNew();
            var deviceInfo = Devices.DeviceDetector.DetectDevice();
            deviceTimer.Stop();
            Logger.Info($"[TIMING] DeviceDetector pre-cached: {deviceTimer.ElapsedMilliseconds}ms (Device: {deviceInfo.Manufacturer} {deviceInfo.Model})");

            // Sweep Present=True ViGEm Xbox 360 phantoms from PRIOR helper
            // sessions BEFORE any manager constructs its own ViGEm pad. ViGEmBus
            // is supposed to release virtual pads when the owning process exits,
            // but during MSIX upgrade cycles or abnormal exits, Labs ViGEm Xbox
            // 360 pads can linger — accumulating one phantom per cycle. Each
            // phantom is a live virtual controller delivering input to apps, so
            // games see double (or triple) presses from a single physical button.
            // Runs on a thread pool task — non-blocking.
            // Three-phase cleanup. The two Present=True phantom sweeps (VIIPER
            // and ViGEm) MUST complete before any backend creates a virtual
            // pad, otherwise the active pad is visible alongside the phantom
            // for ~5s until pnputil catches up (seen in
            // helper_2026-05-21_01.log around 01:18:08-14). Run both
            // synchronously here — blocks ~1-3s total — then fire the
            // disconnected-ghost sweep async since it's safe to overlap with
            // any backend's startup.
            try
            {
                XboxGamingBarHelper.ControllerEmulation.Viiper.ViiperPnpCleanup.CleanupPresentViiperPhantomsBlocking();
            }
            catch (Exception ex)
            {
                Logger.Debug($"ViiperPnpCleanup Present=True VIIPER sweep threw: {ex.Message}");
            }

            try
            {
                XboxGamingBarHelper.ControllerEmulation.Viiper.ViiperPnpCleanup.CleanupPresentVigemPhantomsBlocking();
            }
            catch (Exception ex)
            {
                Logger.Debug($"ViiperPnpCleanup Present=True ViGEm sweep threw: {ex.Message}");
            }

            try
            {
                XboxGamingBarHelper.ControllerEmulation.Viiper.ViiperPnpCleanup.CleanupAllKnownGhosts();
            }
            catch (Exception ex)
            {
                Logger.Debug($"ViiperPnpCleanup early sweep threw: {ex.Message}");
            }

            // PARALLEL MANAGER INITIALIZATION - Wave-based to respect dependencies
            var totalTimer = System.Diagnostics.Stopwatch.StartNew();
            Logger.Info("Initialize managers (parallel waves)...");
            LogManager.Flush(); // Ensure this log appears before parallel tasks start

            // Wave 1: Independent managers (no dependencies) - run in parallel
            var wave1Timer = System.Diagnostics.Stopwatch.StartNew();
            Logger.Info("Wave 1: PerformanceManager, ProfileManager, AMDManager, LosslessScalingManager, SettingsManager, LegionManager, GPDManager");
            LogManager.Flush();

            PerformanceManager tempPerfMgr = null;
            ProfileManager tempProfileMgr = null;
            AMDManager tempAmdMgr = null;
            LosslessScalingManager tempLosslessMgr = null;
            SettingsManager tempSettingsMgr = null;
            LegionManager tempLegionMgr = null;
            GPDManager tempGpdMgr = null;

            var wave1Tasks = new[]
            {
                Task.Run(() => {
                    try { tempPerfMgr = new PerformanceManager(); Logger.Info("Wave1: PerformanceManager DONE"); }
                    catch (Exception ex) { Logger.Error(ex, "Wave1: PerformanceManager FAILED"); throw; }
                }),
                Task.Run(() => {
                    try { tempProfileMgr = new ProfileManager(); Logger.Info("Wave1: ProfileManager DONE"); }
                    catch (Exception ex) { Logger.Error(ex, "Wave1: ProfileManager FAILED"); throw; }
                }),
                Task.Run(() => {
                    try { tempAmdMgr = new AMDManager(); Logger.Info("Wave1: AMDManager DONE"); }
                    catch (Exception ex) { Logger.Error(ex, "Wave1: AMDManager FAILED"); throw; }
                }),
                Task.Run(() => {
                    try { tempLosslessMgr = new LosslessScalingManager(); Logger.Info("Wave1: LosslessScalingManager DONE"); }
                    catch (Exception ex) { Logger.Error(ex, "Wave1: LosslessScalingManager FAILED"); throw; }
                }),
                Task.Run(() => {
                    try { tempSettingsMgr = SettingsManager.CreateInstance(); Logger.Info("Wave1: SettingsManager DONE"); }
                    catch (Exception ex) { Logger.Error(ex, "Wave1: SettingsManager FAILED"); throw; }
                }),
                Task.Run(() => {
                    try { tempLegionMgr = new LegionManager(); Logger.Info("Wave1: LegionManager DONE"); }
                    catch (Exception ex) { Logger.Error(ex, "Wave1: LegionManager FAILED"); throw; }
                }),
                Task.Run(() => {
                    try { tempGpdMgr = new GPDManager(); Logger.Info("Wave1: GPDManager DONE"); }
                    catch (Exception ex) { Logger.Error(ex, "Wave1: GPDManager FAILED"); throw; }
                })
            };

            try
            {
                Task.WaitAll(wave1Tasks);
            }
            catch (AggregateException ae)
            {
                Logger.Error("Wave1 Task.WaitAll failed with AggregateException:");
                foreach (var ex in ae.InnerExceptions)
                {
                    Logger.Error(ex, $"  Inner exception: {ex.GetType().Name}");
                }
                throw;
            }

            // Flush logs from parallel tasks to ensure they appear in order
            LogManager.Flush();

            performanceManager = tempPerfMgr;
            profileManager = tempProfileMgr;
            amdManager = tempAmdMgr;
            losslessScalingManager = tempLosslessMgr;
            settingsManager = tempSettingsMgr;
            legionManager = tempLegionMgr;
            gpdManager = tempGpdMgr;

            wave1Timer.Stop();
            Logger.Info($"[TIMING] Wave 1 (parallel): {wave1Timer.ElapsedMilliseconds}ms");

            // Wave 2: Managers that depend on Wave 1 - run in parallel
            var wave2Timer = System.Diagnostics.Stopwatch.StartNew();
            Logger.Info("Wave 2: RTSSManager, SystemManager, PowerManager");

            RTSSManager tempRtssMgr = null;
            SystemManager tempSystemMgr = null;
            PowerManager tempPowerMgr = null;

            var wave2Tasks = new[]
            {
                Task.Run(() => { tempRtssMgr = new RTSSManager(performanceManager); }),
                Task.Run(() => { tempSystemMgr = new SystemManager(profileManager.GameProfiles); }),
                Task.Run(() => { tempPowerMgr = new PowerManager(performanceManager.RyzenAdjHandle); })
            };
            Task.WaitAll(wave2Tasks);
            LogManager.Flush();

            rtssManager = tempRtssMgr;
            systemManager = tempSystemMgr;
            powerManager = tempPowerMgr;

            // One-time cleanup for the power-plan features retired in #103: restore the
            // user's original Max/Min processor state if an older build modified it, and
            // reset leftover core-parking powercfg values. No-op once the markers clear.
            Task.Run(() => Services.SystemRestoreService.RestoreRetiredSettings(systemManager));

            wave2Timer.Stop();
            Logger.Info($"[TIMING] Wave 2 (parallel): {wave2Timer.ElapsedMilliseconds}ms");

            // Wave 3: Managers that depend on Wave 2
            var wave3Timer = System.Diagnostics.Stopwatch.StartNew();
            Logger.Info("Wave 3: AutoTDPManager");
            autoTDPManager = new AutoTDPManager(performanceManager, systemManager);
            wave3Timer.Stop();
            Logger.Info($"[TIMING] Wave 3: {wave3Timer.ElapsedMilliseconds}ms");

            totalTimer.Stop();
            Logger.Info($"[TIMING] All managers total (parallel): {totalTimer.ElapsedMilliseconds}ms");

            // Initialize DefaultGameProfileManager in background - not needed for initial UI
            // Deferred to avoid blocking startup - DGP only kicks in when a game is running
            Logger.Info("Initialize Default Game Profile Manager (deferred to background).");
            InitializeDefaultGameProfileManagerAsync();

            // Initialize input injector for keyboard shortcuts (works in widget context unlike SendInput)
            inputInjector = InputInjector.TryCreate();
            if (inputInjector == null)
            {
                Logger.Warn("Failed to create InputInjector - keyboard shortcuts may not work in widget");
            }

            // Set LegionManager reference in PerformanceManager for WMI TDP support
            performanceManager.SetLegionManager(legionManager);

            // Wire ADLX-backed GPU metrics fallback so the OSD GPU line still shows
            // Power/Temperature/Clock on hardware where LibreHardwareMonitor doesn't
            // expose those sensors (e.g. Mute's Legion Go 2 / AMD Z2-series APU).
            performanceManager.SetAMDManager(amdManager);

            // Set PerformanceManager reference in LegionManager for CPU temperature sensor access
            legionManager.SetPerformanceManager(performanceManager);

            // Set PerformanceManager reference in GPDManager for software fan curve CPU temperature access
            gpdManager?.SetPerformanceManager(performanceManager);

            // Initialize handheld-agnostic controller emulation manager.
            controllerEmulationManager = new ControllerEmulationManager(legionManager, gpdManager, settingsManager);

            // Initialize VIIPER emulation manager (toggle-driven; mutually exclusive with legacy).
            // legionManager is passed so VIIPER can forward LED color reports to the Legion stick lights.
            viiperEmulationManager = new XboxGamingBarHelper.ControllerEmulation.Viiper.ViiperEmulationManager(settingsManager, controllerEmulationManager, legionManager);

            // PawnIO/RyzenSMU initialization for anti-cheat compatible TDP control
            // Priority: Legion WMI > PawnIO/RyzenSMU > RyzenAdj (deprecated, WinRing0 not bundled)
            // Uses official signed module from release 0.2.1
            // Supported CPUs: StrixHalo (Ryzen AI Max 385/395), etc.
            performanceManager.InitializePawnIO();

            // Set LegionManager reference in RTSSManager for fan speed OSD support
            rtssManager.SetLegionManager(legionManager);

            // Set controller battery callbacks in RTSSManager for Controller Battery OSD item
            rtssManager.SetControllerBatteryCallbacks(
                () => legionManager.GetLeftControllerBattery(),
                () => legionManager.GetRightControllerBattery(),
                () => legionManager.IsLeftControllerCharging(),
                () => legionManager.IsRightControllerCharging()
            );

            // Start Legion button monitor for battery monitoring (even when button remap is disabled)
            // This allows controller battery to be monitored without requiring button remapping.
            //
            // Deferred to the thread pool — the monitor iterates up to 4
            // candidate HID paths and each firmware "init" command that gets
            // rejected costs ~1s, for a combined ~2.2s on Legion Go 2. None
            // of the values it produces (VID:PID, controller battery) are
            // referenced by the property list at construction time; they
            // sync into legionManager's properties as soon as the real HID
            // device is found, and a zero-filled initial state is correct
            // in the meantime.
            // Gate on the device config's HID capabilities, not just "is a Legion".
            // LegionButtonMonitor speaks the 64-byte 04:00:A1 protocol of the
            // Go 1 / Go 2 controllers (VID 0x17EF / 0x1A86-with-64-byte-reports).
            // The Legion Go S (issue #90) exposes 8 HID devices on the same VID but
            // NONE use that report format, so the monitor's reconnect loop rescanned
            // all 8 candidates (~1s ProbeDeviceFormat each) every 10s forever —
            // pure CPU waste plus log spam. Go S's DeviceConfig already declares
            // SupportsControllerRemap=false AND SupportsGyro=false; the monitor
            // has no job to do on such devices (buttons, gyro, battery all ride
            // the same protocol), so don't start it at all.
            bool deviceSupportsButtonMonitor =
                legionManager.DetectedDevice.SupportsControllerRemap ||
                legionManager.DetectedDevice.SupportsGyro;
            if (legionManager.LegionGoDetected.Value && !deviceSupportsButtonMonitor)
            {
                Logger.Info($"Legion button monitor skipped: device '{legionManager.DetectedDevice.Model}' " +
                            "declares no controller-remap or gyro support (HID protocol incompatible)");
            }
            if (legionManager.LegionGoDetected.Value && deviceSupportsButtonMonitor)
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        LegionButtonMonitor.LoadCachedDevicePathFromSettings();
                        LegionButtonMonitor.LoadGyroBiasFromSettings();
                        // Widget typically connects to the pipe seconds before this load runs,
                        // so the Connected-event push happens with _hasGyroBias=false. Push
                        // again here so the widget UI reflects the persisted offset.
                        try { SendGyroBiasOffsetToWidget(); } catch { }
                        LegionButtonMonitor monitor = EnsureLegionButtonMonitor();

                        if (monitor.StartForBatteryMonitoring())
                        {
                            Logger.Info("Legion button monitor started for battery monitoring");
                            var vidPid = monitor.DetectedVidPid;
                            Logger.Info($"Legion button monitor VID:PID after start: '{vidPid}'");
                            if (!string.IsNullOrEmpty(vidPid))
                            {
                                legionManager.UpdateControllerVidPid(vidPid);
                            }
                            else
                            {
                                Logger.Warn("Legion button monitor VID:PID is empty after start");
                            }

                            // GoTweaks lighting + standalone haptics — both ride the monitor's
                            // press-edge stream, so wire them up once the monitor is live.
                            InitGoTweaksLightingAndHaptics(monitor);
                        }
                        else
                        {
                            Logger.Warn("Failed to start Legion button monitor for battery monitoring");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Error initializing Legion button monitor for battery: {ex.Message}");
                    }
                });
            }

            // Set AutoTDPManager reference in RTSSManager for AutoTDP OSD support
            rtssManager.SetAutoTDPManager(autoTDPManager);

            // Set RTSSManager reference in AutoTDPManager for frametime stability detection
            autoTDPManager.SetRTSSManager(rtssManager);

            // Initialize Display/OSD config (position shift handled by RTSSManager, adaptive brightness by SystemManager)
            Logger.Info("Initialize DisplayOSD Config.");
            rtssManager.InitializeDisplayOSDConfig(systemManager.SetAdaptiveBrightness);

            // Initialize global hotkey manager (Ctrl+Shift+D to toggle Desktop Controls)
            InitializeHotkeyManager();

            Managers = new List<IManager>
            {
                performanceManager,
                rtssManager,
                profileManager,
                systemManager,
                powerManager,
                amdManager,
                losslessScalingManager,
                settingsManager,
                legionManager,
                gpdManager,
                controllerEmulationManager,
                autoTDPManager
            };
            // Note: defaultGameProfileManager is added in background task when ready

            // Wire sidebar manager to live managers (WPF thread already running from TryStartSidebar)

            // Gate the per-tick LibreHardwareMonitor walk on whether anyone actually wants
            // fresh sensor data. PerformanceManager already short-circuits on QuickMetrics
            // enabled; this predicate covers the other consumers. When idle (no game,
            // no UI, no AutoTDP, no fan-curve panel) the sensor walk is skipped, which is
            // the dominant idle-CPU cost in the helper.
            performanceManager.SetMetricsConsumerCheck(() =>
            {
                try
                {
                    if (autoTDPManager?.Enabled?.Value == true) return true;
                    if (legionManager?.IsFanCurveVisible == true) return true;
                    // EC fan override loop reads CPUTemperature every tick to drive 0xC6C8. If
                    // the sensor walk is gated off when no one else needs sensors, the cached
                    // temperature stays at the last reading (e.g. 68°C from gameplay) while the
                    // device idles or sleeps, and the loop keeps writing the matching high-RPM
                    // target until something else wakes the sensor walk up. Treating the EC
                    // override as a metrics consumer keeps the temperature fresh. (#88 kayti.)
                    if (legionManager?.IsEcFanOverrideActive == true) return true;
                    var runningGameProp = systemManager?.RunningGame;
                    if (runningGameProp != null && runningGameProp.Value.GameId.IsValid()) return true;
                }
                catch { /* fail-open below */ return true; }
                return false;
            });

            Logger.Info("Initialize properties.");
            onScreenDisplay = new OnScreenDisplayProperty(0, null, rtssManager);
            onScreenDisplayProviders = new List<OnScreenDisplayManager>() { rtssManager, amdManager };
            //onScreenDisplay = new OnScreenDisplayProperty(0, null, amdManager);

            // Build properties list (conditionally add DefaultGameProfile if available)
            var propertyList = new List<FunctionalProperty>
            {
                systemManager.RunningGame,
                onScreenDisplay,
                performanceManager.TDP,
                performanceManager.CurrentTDP,
                profileManager.PerGameProfile,
                profileManager.DeleteGameProfile,
                powerManager.CPUBoost,
                powerManager.CPUEPP,
                powerManager.OSPowerMode,
                powerManager.PowerButtonActionAC,
                powerManager.PowerButtonActionDC,
                powerManager.DisplayTimeoutAC,
                powerManager.DisplayTimeoutDC,
                powerManager.HibernateTimeoutAC,
                powerManager.HibernateTimeoutDC,
                // GPU Clock - DISABLED: Not supported by RyzenAdj on this hardware (returns error -1)
                //powerManager.LimitGPUClock,
                //powerManager.GPUClockMin,
                //powerManager.GPUClockMax,
                systemManager.RefreshRates,
                systemManager.RefreshRate,
                systemManager.Resolutions,
                systemManager.Resolution,
                systemManager.DisplayOrientation,
                systemManager.HDRSupported,
                systemManager.HDREnabled,
                systemManager.AdaptiveBrightnessMode,
                systemManager.PanelBrightness,
                systemManager.PanelBrightnessSupported,
                systemManager.InternalPanelActive,
                systemManager.TouchscreenEnabled,
                systemManager.TrackedGame,
                rtssManager.RTSSInstalled,
                rtssManager.OSDConfig,
                rtssManager.FPSLimit,
                rtssManager.DisplayOSDConfig,
                settingsManager.IsForeground,
                amdManager.AMDRadeonSuperResolutionEnabled,
                amdManager.AMDRadeonSuperResolutionSupported,
                amdManager.AMDRadeonSuperResolutionSharpness,
                amdManager.AMDFluidMotionFrameEnabled,
                amdManager.AMDFluidMotionFrameSupported,
                amdManager.AMDFluidMotionFrameV1Supported,
                amdManager.AMDFluidMotionFrameAlgorithm,
                amdManager.AMDFluidMotionFrameSearchMode,
                amdManager.AMDFluidMotionFramePerformanceMode,
                amdManager.AMDFluidMotionFrameFastMotionResponse,
                amdManager.AMDRadeonAntiLagEnabled,
                amdManager.AMDRadeonAntiLagSupported,
                amdManager.AMDRadeonBoostEnabled,
                amdManager.AMDRadeonBoostSupported,
                amdManager.AMDRadeonBoostResolution,
                amdManager.AMDRadeonChillEnabled,
                amdManager.AMDRadeonChillSupported,
                amdManager.AMDRadeonChillMinFPS,
                amdManager.AMDRadeonChillMaxFPS,
                amdManager.AMDImageSharpeningEnabled,
                amdManager.AMDImageSharpeningSupported,
                amdManager.AMDImageSharpeningSharpness,
                amdManager.AMDDisplayBrightnessSupported,
                amdManager.AMDDisplayBrightness,
                amdManager.AMDDisplayContrastSupported,
                amdManager.AMDDisplayContrast,
                amdManager.AMDDisplaySaturationSupported,
                amdManager.AMDDisplaySaturation,
                amdManager.AMDDisplayTemperatureSupported,
                amdManager.AMDDisplayTemperature,
                losslessScalingManager.LosslessScalingInstalled,
                losslessScalingManager.LosslessScalingRunning,
                losslessScalingManager.LosslessScalingEnabled,
                losslessScalingManager.LosslessScalingCurrentProfile,
                losslessScalingManager.LosslessScalingScalingType,
                losslessScalingManager.LosslessScalingFrameGenType,
                losslessScalingManager.LosslessScalingLSFG3Mode,
                losslessScalingManager.LosslessScalingLSFG3Multiplier,
                losslessScalingManager.LosslessScalingLSFG3Target,
                losslessScalingManager.LosslessScalingLSFG2Mode,
                losslessScalingManager.LosslessScalingFlowScale,
                losslessScalingManager.LosslessScalingSize,
                losslessScalingManager.LosslessScalingAutoScale,
                losslessScalingManager.LosslessScalingAutoScaleDelay,
                losslessScalingManager.LosslessScalingSaveAndRestart,
                losslessScalingManager.LosslessScalingCreateProfile,
                losslessScalingManager.LosslessScalingBringToForeground,
                losslessScalingManager.LosslessScalingLaunch,
                settingsManager.AutoStartRTSS,
                settingsManager.OnScreenDisplayProvider,
                settingsManager.UseManufacturerWMI,
                settingsManager.TdpMethod,
                settingsManager.EmulationBackend,
                settingsManager.UsbipInstalled,
                settingsManager.InstallUsbip,
                settingsManager.ViiperDeviceType,
                settingsManager.ViiperInputSource,
                settingsManager.ViiperGyroSource,
                settingsManager.ViiperSteamSubDevice,
                settingsManager.ViiperSonySubDevice,
                settingsManager.ViiperNintendoSubDevice,
                settingsManager.ViiperGuideButtonMode,
                settingsManager.ViiperDesktopButtonTarget,
                settingsManager.ViiperPageButtonTarget,
                settingsManager.ViiperSwapRumbleMotors,
                settingsManager.ViiperRumbleIntensity,
                settingsManager.GoTweaksLightingConfig,
                settingsManager.GoTweaksHapticsConfig,
                settingsManager.LegionControllerSleepMinutes,
                settingsManager.ViiperMirrorLightbarToStick,
                settingsManager.ViiperStickGyroEnabled,
                settingsManager.ViiperJoyconGyroPerHalf,
                settingsManager.ViiperGyroAxisMapX,
                settingsManager.ViiperGyroAxisMapY,
                settingsManager.ViiperGyroAxisMapZ,
                settingsManager.ViiperStickTriggerConfig,
                settingsManager.ViiperStickTriggerPreviewEnabled,
                viiperEmulationManager.StickTriggerLiveSample,
                // Profile Detection Settings
                settingsManager.ProfileMatchByExe,
                settingsManager.ProfileCustomGamePath,
                settingsManager.ProfileGamesOnly,
                settingsManager.ProfileBlacklistPaths,
                systemManager.ForegroundApp,
                // GPD specific properties
                gpdManager.GPDDetected,
                gpdManager.Win5Connected,
                gpdManager.Win5HidDebug,
                gpdManager.Win5HidDevices,
                gpdManager.DeviceName,
                gpdManager.SupportsFanControlProp,
                gpdManager.RestoreDefaults,
                gpdManager.ApplyMappings,
                // GPD Win 5 button remapping properties
                gpdManager.ButtonA,
                gpdManager.ButtonB,
                gpdManager.ButtonX,
                gpdManager.ButtonY,
                gpdManager.ButtonDPadUp,
                gpdManager.ButtonDPadDown,
                gpdManager.ButtonDPadLeft,
                gpdManager.ButtonDPadRight,
                gpdManager.ButtonL3,
                gpdManager.ButtonR3,
                gpdManager.ButtonL4,
                gpdManager.ButtonR4,
                gpdManager.ButtonLSUp,
                gpdManager.ButtonLSDown,
                gpdManager.ButtonLSLeft,
                gpdManager.ButtonLSRight,
                // GPD fan control properties
                gpdManager.FanSpeed,
                gpdManager.FanRPM,
                gpdManager.FanMode,
                // GPD software fan curve properties
                gpdManager.FanCurveEnabled,
                gpdManager.FanCurveData,
                gpdManager.FanCurveVisibleProp,
                gpdManager.CPUTemp,
                gpdManager.GyroSource,
                gpdManager.GyroSimulateMode,
                // Handheld-agnostic controller emulation properties
                controllerEmulationManager.ControllerEmulationAvailable,
                controllerEmulationManager.ControllerEmulationEnabled,
                controllerEmulationManager.ControllerEmulationGyroActivationMode,
                controllerEmulationManager.ControllerEmulationGyroActivationButton,
                controllerEmulationManager.ControllerEmulationStickInvertX,
                controllerEmulationManager.ControllerEmulationStickInvertY,
                controllerEmulationManager.ControllerEmulationStickSelect,
                controllerEmulationManager.ControllerEmulationCalibrateGyro,
                controllerEmulationManager.ControllerEmulationStickSensitivityV2,
                controllerEmulationManager.ControllerEmulationStickOrientationV2,
                controllerEmulationManager.ControllerEmulationStickConversion,
                // Legion Go specific properties
                legionManager.LegionGoDetected,
                legionManager.LegionTouchpadEnabled,
                legionManager.LegionLightMode,
                legionManager.LegionLightColor,
                legionManager.LegionLightBrightness,
                legionManager.LegionLightSpeed,
                legionManager.LegionPerformanceMode,
                legionManager.LegionCustomTDPSlow,
                legionManager.LegionCustomTDPFast,
                legionManager.LegionCustomTDPPeak,
                legionManager.LegionFanFullSpeed,
                legionManager.LegionFanCurveData,
                legionManager.LegionUnlockFanCurve,
                legionManager.LegionFanCurvePerMode,
                legionManager.LegionUnlockFanCurvePerMode,
                legionManager.LegionCPUCurrentTemp,
                legionManager.LegionFanSensorTemp,
                legionManager.LegionCPUFanRPM,
                legionManager.LegionFanCurveVisible,
                legionManager.LegionGyroEnabled,
                legionManager.LegionVibration,
                legionManager.LegionPowerLight,
                legionManager.LegionChargeLimit,
                legionManager.LegionButtonY1,
                legionManager.LegionButtonY2,
                legionManager.LegionButtonY3,
                legionManager.LegionButtonM1,
                legionManager.LegionButtonM2,
                legionManager.LegionButtonM3,
                legionManager.LegionButtonDesktop,
                legionManager.LegionButtonPage,
                legionManager.LegionNintendoLayout,
                legionManager.LegionVibrationMode,
                legionManager.LegionControllerProfileEnabled,
                // Gyro properties
                legionManager.LegionGyroTarget,
                legionManager.LegionGyroSensitivityX,
                legionManager.LegionGyroSensitivityY,
                legionManager.LegionGyroInvertX,
                legionManager.LegionGyroInvertY,
                legionManager.LegionGyroMappingType,
                legionManager.LegionGyroActivationMode,
                legionManager.LegionGyroActivationButton,
                // Gyro deadzone property
                legionManager.LegionGyroDeadzone,
                // Stick deadzone properties
                legionManager.LegionLeftStickDeadzone,
                legionManager.LegionRightStickDeadzone,
                // Trigger travel properties
                legionManager.LegionLeftTriggerStart,
                legionManager.LegionLeftTriggerEnd,
                legionManager.LegionRightTriggerStart,
                legionManager.LegionRightTriggerEnd,
                legionManager.LegionHairTriggers,
                // Touchpad vibration (GLOBAL setting)
                legionManager.LegionTouchpadVibration,
                // Joystick as mouse properties
                legionManager.LegionJoystickAsMouseMode,
                legionManager.LegionJoystickMouseSens,
                // Gamepad button mapping
                legionManager.LegionGamepadMapping,
                // Desktop controls preset (state tracking for UI sync)
                legionManager.LegionDesktopControls,
                // Controller battery properties (read-only, from HID)
                legionManager.ControllerBatteryLeft,
                legionManager.ControllerBatteryRight,
                legionManager.ControllerChargingLeft,
                legionManager.ControllerChargingRight,
                legionManager.ControllerConnectedLeft,
                legionManager.ControllerConnectedRight,
                legionManager.ControllerVidPid,
                legionManager.ControllerDeviceStatus,
                // Device capability properties (for UI visibility based on device features)
                legionManager.DeviceDisplayName,
                legionManager.DeviceSupportsControllerRemap,
                legionManager.DeviceSupportsRgbLighting,
                legionManager.DeviceSupportsGyro,
                legionManager.DeviceHasScrollWheel,
                legionManager.DeviceHasDetachableControllers,
                legionManager.DeviceHasTouchpad,
                autoTDPManager.Enabled,
                autoTDPManager.TargetFPS,
                autoTDPManager.CurrentFPS,
                autoTDPManager.MinTDP,
                autoTDPManager.MaxTDP,
                autoTDPManager.TDPLimits,
                autoTDPManager.UseMLMode,
                autoTDPManager.ControllerType,  // 0=PID, 1=Q-Learning, 2=SARSA
                autoTDPManager.MLStatus,
                autoTDPManager.LearnedGameData,
                autoTDPManager.ResetML,
                autoTDPManager.PauseWhenUnfocused,
                performanceManager.TDPBoostEnabled,
                performanceManager.TDPBoostSPPT,
                performanceManager.TDPBoostFPPT,
                // performanceManager.WinRing0AvailableProperty, // WinRing0 removed - deprecated
                performanceManager.PawnIOAvailableProperty,
                performanceManager.PawnIOInstalledProperty,
                performanceManager.InstallPawnIOProperty
            };

            // Note: DefaultGameProfileManager properties are added dynamically in background task

            // Initialize properties
            properties = new HelperProperties(propertyList.ToArray());

            Logger.Info("Initialize callbacks.");
            systemManager.RunningGame.PropertyChanged += RunningGame_PropertyChanged;
            systemManager.ResumeFromSleep += SystemManager_ResumeFromSleep;
            systemManager.PowerSourceChanged += SystemManager_PowerSourceChanged;
            profileManager.PerGameProfile.PropertyChanged += PerGameProfile_PropertyChanged;
            performanceManager.TDP.PropertyChanged += TDP_PropertyChanged;
            performanceManager.TDPBoostEnabled.PropertyChanged += TDPBoostEnabled_PropertyChanged;
            powerManager.CPUBoost.PropertyChanged += CPUBoost_PropertyChanged;
            powerManager.CPUEPP.PropertyChanged += CPUEPP_PropertyChanged;
            powerManager.HibernateTimeoutAC.PropertyChanged += UpdateHibernateTimeoutMonitorState;
            powerManager.HibernateTimeoutDC.PropertyChanged += UpdateHibernateTimeoutMonitorState;
            // One-time migration: the old AutoHibernate feature (single idle-minutes value
            // + Always/AC-Only/DC-Only mode) is replaced by the Power & Sleep card's
            // per-AC/DC Hibernate Timeout. Seed the new values from the stored old ones so
            // an existing user's idle-hibernate keeps working after the update.
            MigrateAutoHibernateSettings();

            // GoTweaks-owned idle-to-hibernate monitor (Power & Sleep card). Only runs the
            // polling timer while at least one of AC/DC is configured - see
            // UpdateHibernateTimeoutMonitorState / the PropertyChanged subscriptions above.
            UpdateHibernateTimeoutMonitorState();
            // GPU Clock - DISABLED: Not supported by RyzenAdj on this hardware (returns error -1)
            //powerManager.LimitGPUClock.PropertyChanged += GPUClock_PropertyChanged;
            //powerManager.GPUClockMin.PropertyChanged += GPUClock_PropertyChanged;
            //powerManager.GPUClockMax.PropertyChanged += GPUClock_PropertyChanged;
            profileManager.CurrentProfile.PropertyChanged += CurrentProfile_PropertyChanged;

            // Subscribe to Legion controller property changes to save to profile
            if (legionManager != null)
            {
                // Button mappings
                legionManager.LegionButtonY1.PropertyChanged += LegionControllerSetting_PropertyChanged;
                legionManager.LegionButtonY2.PropertyChanged += LegionControllerSetting_PropertyChanged;
                legionManager.LegionButtonY3.PropertyChanged += LegionControllerSetting_PropertyChanged;
                legionManager.LegionButtonM1.PropertyChanged += LegionControllerSetting_PropertyChanged;
                legionManager.LegionButtonM2.PropertyChanged += LegionControllerSetting_PropertyChanged;
                legionManager.LegionButtonM3.PropertyChanged += LegionControllerSetting_PropertyChanged;
                legionManager.LegionButtonDesktop.PropertyChanged += LegionControllerSetting_PropertyChanged;
                legionManager.LegionButtonPage.PropertyChanged += LegionControllerSetting_PropertyChanged;
                // Gyro settings
                legionManager.LegionGyroActivationButton.PropertyChanged += LegionControllerSetting_PropertyChanged;
                legionManager.LegionGyroTarget.PropertyChanged += LegionControllerSetting_PropertyChanged;
                legionManager.LegionGyroSensitivityX.PropertyChanged += LegionControllerSetting_PropertyChanged;
                legionManager.LegionGyroSensitivityY.PropertyChanged += LegionControllerSetting_PropertyChanged;
                legionManager.LegionGyroInvertX.PropertyChanged += LegionControllerSetting_PropertyChanged;
                legionManager.LegionGyroInvertY.PropertyChanged += LegionControllerSetting_PropertyChanged;
                legionManager.LegionGyroMappingType.PropertyChanged += LegionControllerSetting_PropertyChanged;
                legionManager.LegionGyroActivationMode.PropertyChanged += LegionControllerSetting_PropertyChanged;
                legionManager.LegionGyroDeadzone.PropertyChanged += LegionControllerSetting_PropertyChanged;
                // Stick deadzones
                legionManager.LegionLeftStickDeadzone.PropertyChanged += LegionControllerSetting_PropertyChanged;
                legionManager.LegionRightStickDeadzone.PropertyChanged += LegionControllerSetting_PropertyChanged;
                // Trigger travel
                legionManager.LegionLeftTriggerStart.PropertyChanged += LegionControllerSetting_PropertyChanged;
                legionManager.LegionLeftTriggerEnd.PropertyChanged += LegionControllerSetting_PropertyChanged;
                legionManager.LegionRightTriggerStart.PropertyChanged += LegionControllerSetting_PropertyChanged;
                legionManager.LegionRightTriggerEnd.PropertyChanged += LegionControllerSetting_PropertyChanged;
                legionManager.LegionHairTriggers.PropertyChanged += LegionControllerSetting_PropertyChanged;
                // Joystick as mouse
                legionManager.LegionJoystickAsMouseMode.PropertyChanged += LegionControllerSetting_PropertyChanged;
                legionManager.LegionJoystickMouseSens.PropertyChanged += LegionControllerSetting_PropertyChanged;
                // Gamepad mapping (24 buttons JSON)
                legionManager.LegionGamepadMapping.PropertyChanged += LegionControllerSetting_PropertyChanged;
                // Other controller settings
                legionManager.LegionNintendoLayout.PropertyChanged += LegionControllerSetting_PropertyChanged;
                legionManager.LegionVibration.PropertyChanged += LegionControllerSetting_PropertyChanged;
                legionManager.LegionVibrationMode.PropertyChanged += LegionControllerSetting_PropertyChanged;
                legionManager.LegionControllerProfileEnabled.PropertyChanged += LegionControllerSetting_PropertyChanged;
                // Performance mode (for per-game TDP mode)
                legionManager.LegionPerformanceMode.PropertyChanged += LegionControllerSetting_PropertyChanged;
                // Lighting settings (per-game lighting profiles)
                legionManager.LegionLightMode.PropertyChanged += LegionControllerSetting_PropertyChanged;
                legionManager.LegionLightColor.PropertyChanged += LegionControllerSetting_PropertyChanged;
                legionManager.LegionLightBrightness.PropertyChanged += LegionControllerSetting_PropertyChanged;
                legionManager.LegionLightSpeed.PropertyChanged += LegionControllerSetting_PropertyChanged;
                legionManager.LegionPowerLight.PropertyChanged += LegionControllerSetting_PropertyChanged;
            }

            // AutoTDP settings (per-game AutoTDP profiles)
            if (autoTDPManager != null)
            {
                autoTDPManager.Enabled.PropertyChanged += AutoTDPSetting_PropertyChanged;
                autoTDPManager.TargetFPS.PropertyChanged += AutoTDPSetting_PropertyChanged;
                autoTDPManager.MinTDP.PropertyChanged += AutoTDPSetting_PropertyChanged;
                autoTDPManager.MaxTDP.PropertyChanged += AutoTDPSetting_PropertyChanged;
                autoTDPManager.UseMLMode.PropertyChanged += AutoTDPSetting_PropertyChanged;  // Legacy
                autoTDPManager.ControllerType.PropertyChanged += AutoTDPSetting_PropertyChanged;
                autoTDPManager.PauseWhenUnfocused.PropertyChanged += AutoTDPSetting_PropertyChanged;
            }

            initTimer.Stop();
            Logger.Info($"[TIMING] Helper initialization (managers + properties): {initTimer.ElapsedMilliseconds}ms");

            // Mark managers as ready - BatchGet requests will now be processed
            // Pipe server was started earlier, widget may already be connected and waiting
            _managersReady = true;
            Logger.Info("Managers ready - BatchGet requests will now be processed");

            // #66: bring up PresentMon integration after managers are ready. Lifecycle is
            // driven from RunningGame_PropertyChanged → OnRunningGameChangedForPresentMon.
            InitializePresentMon();

            // Kick off a background Lenovo driver-update probe so the widget
            // can show an "N updates available" tile the moment it opens.
            // Only for Legion devices — non-Lenovo machines return IsLenovo=false
            // from the WMI check anyway, so CheckAsync is a no-op, but we guard
            // on the LegionManager flag so we don't even burn the HTTP request
            // on unrelated handhelds. Fire-and-forget: failure just means no
            // tile, which is the same as the pre-feature behaviour.
            if (legionManager != null && legionManager.LegionGoDetected != null && legionManager.LegionGoDetected.Value)
            {
                // Per-user opt-out for the Lenovo startup probe. Widget's
                // "Check for driver updates on start" checkbox writes this
                // via pipe → Settings.LocalSettingsHelper → settings.json,
                // and we read it synchronously here before spawning the task.
                // Default true keeps the tile-on-launch UX for untouched
                // installs.
                bool checkOnStart = true;
                try
                {
                    if (Settings.LocalSettingsHelper.TryGetValue<bool>("DriverCheckOnStart", out var persisted))
                        checkOnStart = persisted;
                }
                catch { }

                if (checkOnStart)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var result = await Services.LenovoDriverCheckService.CheckAsync();
                            int updateCount = result?.Drivers?.Count(d => d.UpdateStatus == Services.DriverUpdateStatus.UpdateAvailable) ?? 0;
                            Logger.Info($"Startup driver probe complete — {updateCount} update(s) available out of {result?.Drivers?.Count ?? 0} total");
                            // Mirror the per-update diagnostic from the CheckDriverUpdates pipe
                            // handler so users don't have to click "Check for updates" in the
                            // widget to get matchedDevice/matchedProvider/matchScore data into
                            // the helper log. The startup probe runs on every helper boot, so
                            // any wrong-driver-row match shows up automatically next restart.
                            if (result?.Drivers != null)
                            {
                                foreach (var d in result.Drivers)
                                {
                                    if (d.UpdateStatus == Services.DriverUpdateStatus.UpdateAvailable)
                                    {
                                        Logger.Info($"  Driver flagged update: name='{d.Name}', category='{d.Category}', installed='{d.InstalledVersion}', catalog='{d.Version}', matchedDevice='{d.MatchedDeviceName}', matchedProvider='{d.MatchedProvider}', matchScore={d.MatchScore}");
                                    }
                                }
                            }
                            PushDriverUpdatesAvailable(updateCount);
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn($"Startup driver probe failed: {ex.Message}");
                        }
                    });
                }
                else
                {
                    Logger.Info("Startup driver probe skipped (user disabled 'Check for driver updates on start')");
                }
            }

            // GoTweaks self-update check runs on every device type — the app
            // itself can be updated regardless of which handheld it's on.
            // Opt-out via the System tab's "Check for updates on start"
            // checkbox — widget writes GoTweaksCheckOnStart via pipe →
            // LocalSettingsHelper → settings.json, we honour it here.
            bool goTweaksCheckOnStart = true;
            try
            {
                if (Settings.LocalSettingsHelper.TryGetValue<bool>("GoTweaksCheckOnStart", out var gtPersisted))
                    goTweaksCheckOnStart = gtPersisted;
            }
            catch { }

            if (goTweaksCheckOnStart)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var result = await Services.GoTweaksUpdateService.CheckAsync(helperVersion);
                        PushGoTweaksUpdate(result);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"Startup GoTweaks update probe failed: {ex.Message}");
                    }
                });
            }
            else
            {
                Logger.Info("Startup GoTweaks update probe skipped (user disabled 'Check for updates on start')");
            }

            // Wait for widget connection (non-blocking if already connected)
            var connectTimer = System.Diagnostics.Stopwatch.StartNew();
            await WaitForWidgetConnection(true);
            connectTimer.Stop();
            Logger.Info($"[TIMING] Widget connection: {connectTimer.ElapsedMilliseconds}ms");

            // Start battery monitoring after pipe server is ready
            if (legionManager != null)
            {
                legionManager.StartBatteryMonitoringIfConnected();
            }

            // Load and apply Legion button remap settings from LocalSettings
            LoadLegionButtonRemapSettings();

            // Load and apply Legion scroll wheel remap settings from LocalSettings
            LoadLegionScrollRemapSettings();

            // Restore all global profile settings (TDP, AutoTDP, CPUBoost, EPP, Legion mode, etc.)
            // on startup so saved values are applied to hardware after device restart.
            if (profileManager?.CurrentProfile != null)
            {
                Logger.Info($"Restoring global profile settings on startup: {profileManager.CurrentProfile.GameId.Name}");
                isApplyingProfile = true;
                try
                {
                    RestoreGlobalProfileSettings();
                }
                finally
                {
                    isApplyingProfile = false;
                }
            }

            Logger.Info($"[TIMING] Helper fully initialized and ready");

            // Log version number for easier debugging and set helperVersion for heartbeat
            try
            {
                var packageVersion = Package.Current.Id.Version;
                helperVersion = $"{packageVersion.Major}.{packageVersion.Minor}.{packageVersion.Build}.{packageVersion.Revision}";
                Logger.Info($"GoTweaks Helper v{helperVersion}");
            }
            catch (Exception ex)
            {
                // When running elevated (no package identity), try to get version from deployed .version file
                try
                {
                    var deployedVersion = Services.HelperDeploymentService.GetDeployedVersion();
                    if (!string.IsNullOrEmpty(deployedVersion))
                    {
                        helperVersion = deployedVersion;
                        Logger.Info($"GoTweaks Helper v{helperVersion} (from deployed .version file)");
                    }
                    else
                    {
                        Logger.Debug($"Could not get package version: {ex.Message}");
                    }
                }
                catch
                {
                    Logger.Debug($"Could not get deployed version: {ex.Message}");
                }
            }

            // Main loop - helper runs until cancelled (service stop) or shutdown
            while (!_isShuttingDown)
            {
                // Check for service cancellation
                if (_isRunningAsService && _serviceCancellationToken.IsCancellationRequested)
                {
                    Logger.Info("Service cancellation requested, exiting main loop");
                    break;
                }

                // Check for shutdown (from ExitHelper request)
                if (_isShuttingDown)
                {
                    Logger.Info("Shutdown flag set, exiting main loop for version update");
                    break;
                }

                await Task.Delay(1000);

                // Write heartbeat file so widget can detect if helper is running
                WriteHeartbeat();

                // Check if MSIX package has been uninstalled - if so, clean up and exit
                CheckForPackageUninstall();

                foreach (var manager in Managers)
                {
                    // One manager's exception must not take down the helper —
                    // that used to happen when RTSS wasn't fully initialised
                    // on startup and OSD.ctor threw FileNotFoundException.
                    // Log and continue so sibling managers keep ticking.
                    try
                    {
                        manager.Update();
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"Manager {manager?.GetType().Name} Update() threw: {ex.Message}");
                    }
                }
            }

            // Clean up heartbeat file before exiting
            DeleteHeartbeatFile();

            Logger.Info("Main loop exited");
        }

        /// <summary>
        /// Disposes all managers to free resources
        /// </summary>
        private static void DisposeManagers()
        {
            Logger.Info("Disposing all managers...");
            if (Managers != null)
            {
                // Create a copy of the list to avoid "collection was modified" exception
                var managersCopy = Managers.ToList();
                Managers.Clear();
                Managers = null;

                foreach (var manager in managersCopy)
                {
                    try
                    {
                        manager?.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Error disposing manager: {ex.Message}");
                    }
                }
            }

            // Dispose hotkey manager
            try
            {
                hotkeyManager?.Dispose();
                hotkeyManager = null;
            }
            catch (Exception ex)
            {
                Logger.Error($"Error disposing hotkey manager: {ex.Message}");
            }

            // Dispose Legion button monitor
            try
            {
                DisposeLegionButtonMonitor();
            }
            catch (Exception ex)
            {
                Logger.Error($"Error disposing Legion button monitor: {ex.Message}");
            }

            // Clear references
            performanceManager = null;
            rtssManager = null;
            profileManager = null;
            systemManager = null;
            powerManager = null;
            amdManager = null;
            losslessScalingManager = null;
            settingsManager = null;
            legionManager = null;
            controllerEmulationManager = null;

            try { viiperEmulationManager?.Dispose(); }
            catch (Exception ex) { Logger.Warn($"viiperEmulationManager.Dispose threw: {ex.Message}"); }
            viiperEmulationManager = null;

            Logger.Info("All managers disposed.");
        }
    }
}
