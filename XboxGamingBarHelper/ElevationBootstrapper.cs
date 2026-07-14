using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using NLog;
using XboxGamingBarHelper.Services;

namespace XboxGamingBarHelper
{
    /// <summary>
    /// Handles self-elevation via scheduled task to avoid repeated UAC prompts.
    ///
    /// Architecture:
    /// - Helper files are deployed to %LocalAppData%\GoTweaks\Helper\ (stable location)
    /// - Scheduled task points to the deployed location (survives MSIX updates)
    /// - First run or update: UAC prompt once to deploy and create task
    /// - Subsequent launches: task runs without UAC
    /// </summary>
    public static class ElevationBootstrapper
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private const int ProcessStartTimeoutMs = 5000;

        /// <summary>
        /// Ensures the helper is running with admin privileges.
        /// Returns true if execution should continue, false if we're relaunching elevated.
        /// </summary>
        public static bool EnsureElevated(string[] args)
        {
            // Debug: Write immediately to see if we even get here
            try
            {
                var debugPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "elevation_debug.txt");
                File.AppendAllText(debugPath, $"{DateTime.Now}: EnsureElevated called, IsAdmin={IsRunningAsAdmin()}\n");
            }
            catch { }

            try
            {
                string exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(exePath))
                {
                    Logger.Error("Could not determine executable path");
                    return true; // Continue anyway, may fail later
                }

                // Already running as admin
                if (IsRunningAsAdmin())
                {
                    Logger.Info("Already running as administrator");

                    // If running from MSIX, trigger background deployment for future launches
                    if (HelperDeploymentService.IsRunningFromMsix())
                    {
                        EnsureDeploymentAsync();
                    }

                    return true; // Continue with normal execution immediately
                }

                // Not admin - check if setup is needed BEFORE mutex check
                // This ensures we can update even if an old instance is running
                Logger.Info($"Not running as administrator. ExePath={exePath}");
                Logger.Info($"HelperFolder: {HelperDeploymentService.HelperFolder}");
                Logger.Info($"DeployedExePath: {HelperDeploymentService.DeployedExePath}");
                Logger.Info($"Running from MSIX: {HelperDeploymentService.IsRunningFromMsix()}");
                Logger.Info($"Running from deployed location: {HelperDeploymentService.IsRunningFromDeployedLocation()}");
                LogManager.Flush(); // Ensure logs are written before checks

                // Debug: Write to a simple file to diagnose non-admin path
                try
                {
                    var debugPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "elevation_debug.txt");
                    var debugInfo = $"{DateTime.Now}: ExePath={exePath}\n" +
                                   $"HelperFolder={HelperDeploymentService.HelperFolder}\n" +
                                   $"DeployedExePath={HelperDeploymentService.DeployedExePath}\n" +
                                   $"DeployedExeExists={File.Exists(HelperDeploymentService.DeployedExePath)}\n";
                    File.AppendAllText(debugPath, debugInfo);
                }
                catch { }

                bool deploymentValid = HelperDeploymentService.IsDeploymentValid();
                bool deploymentNeeded = HelperDeploymentService.IsDeploymentNeeded();
                bool taskConfigured = ScheduledTaskService.IsTaskConfiguredCorrectly();

                // Debug: Write check results
                try
                {
                    var debugPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "elevation_debug.txt");
                    var debugInfo = $"DeploymentValid={deploymentValid}, DeploymentNeeded={deploymentNeeded}, TaskConfigured={taskConfigured}\n\n";
                    File.AppendAllText(debugPath, debugInfo);
                }
                catch { }

                Logger.Info($"Deployment valid: {deploymentValid}, Deployment needed: {deploymentNeeded}, Task configured: {taskConfigured}");
                LogManager.Flush(); // Ensure logs are written before decision

                // If deployment is needed (missing or outdated), always trigger setup
                // This handles the case where an old instance is running from WindowsApps
                if (deploymentNeeded || !taskConfigured)
                {
                    Logger.Info("Setup needed - launching with --setup flag (will trigger UAC)...");
                    if (LaunchElevatedForSetup(exePath, args))
                    {
                        Logger.Info("Launched setup process via UAC, exiting current instance");
                        LogManager.Flush();
                        return false; // Exit this non-elevated instance
                    }

                    Logger.Error("Failed to launch setup - continuing without admin rights (some features may not work)");
                    LogManager.Flush();
                    return true; // Continue anyway, but warn user
                }

                // Deployment is valid and task is configured - check for existing instance
                const string mutexName = "Global\\XboxGamingBarHelper_SingleInstance";
                try
                {
                    using (var testMutex = Mutex.OpenExisting(mutexName))
                    {
                        // Mutex exists = another instance is running
                        Logger.Info("Another helper instance is running (mutex exists). Exiting gracefully without elevation.");
                        return false; // Don't continue, don't relaunch
                    }
                }
                catch (WaitHandleCannotBeOpenedException)
                {
                    // Mutex doesn't exist = no other instance running, fall through to
                    // process-enumeration as belt-and-suspenders before firing schtasks.
                    Logger.Debug("No existing helper instance detected (mutex not found)");
                }
                catch (Exception ex)
                {
                    // OpenExisting on a Global\ mutex created by an elevated (High-IL)
                    // helper fails with UnauthorizedAccessException from a Medium-IL
                    // launcher (mandatory integrity policy: NoReadUp). Don't treat that
                    // as "no helper exists" — fall through to process enumeration below.
                    Logger.Debug($"Mutex check inconclusive ({ex.GetType().Name}: {ex.Message}); falling back to process enumeration");
                }

                // Process-enumeration fallback. Same-session XboxGamingBarHelper.exe
                // peers indicate either an alive elevated helper (the common case for
                // a Medium-IL launcher unable to OpenExisting the High-IL mutex) or a
                // zombie helper that released its mutex but didn't fully exit. Either
                // way, do NOT fire schtasks /Run — that would spawn a duplicate.
                try
                {
                    var self = Process.GetCurrentProcess();
                    int selfPid = self.Id;
                    int selfSession = self.SessionId;
                    var peers = Process.GetProcessesByName("XboxGamingBarHelper");
                    var foundPids = new System.Collections.Generic.List<int>();
                    foreach (var p in peers)
                    {
                        try
                        {
                            if (p.Id != selfPid && !p.HasExited && p.SessionId == selfSession)
                            {
                                foundPids.Add(p.Id);
                            }
                        }
                        catch { /* race with process exit */ }
                        finally { try { p.Dispose(); } catch { } }
                    }
                    if (foundPids.Count > 0)
                    {
                        Logger.Info($"Another helper instance is running (PIDs={string.Join(",", foundPids)}). Exiting gracefully without elevation.");
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Debug($"Process-enumeration fallback failed: {ex.Message}");
                    // Continue — Main()'s own AnotherHelperIsAlive gate is the final guard.
                }

                // Try to launch via scheduled task (no UAC)
                Logger.Info("Deployment and task are ready, launching via scheduled task (no UAC)...");
                if (ScheduledTaskService.RunTaskNow())
                {
                    Logger.Info("Launched via scheduled task, exiting current instance");
                    LogManager.Flush();
                    return false; // Exit this non-elevated instance
                }

                Logger.Warn("Failed to launch via task, falling back to UAC elevation");
                if (LaunchElevatedForSetup(exePath, args))
                {
                    Logger.Info("Launched setup process via UAC, exiting current instance");
                    LogManager.Flush();
                    return false;
                }

                Logger.Error("Failed to elevate - continuing without admin rights (some features may not work)");
                LogManager.Flush();
                return true; // Continue anyway, but warn user
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error in elevation bootstrapper");
                return true; // Continue anyway on error
            }
        }

        /// <summary>
        /// Asynchronously ensures deployment is up to date.
        /// Runs in a background thread to avoid blocking startup.
        /// Called when running elevated from MSIX.
        /// </summary>
        private static void EnsureDeploymentAsync()
        {
            Thread deployThread = new Thread(() =>
            {
                try
                {
                    // Always remove legacy task first (cleanup from old implementation)
                    Logger.Info("Background: Removing legacy task if present...");
                    ScheduledTaskService.RemoveLegacyTaskIfExists();

                    Logger.Info("Background: Checking if deployment update is needed...");

                    if (HelperDeploymentService.IsDeploymentNeeded())
                    {
                        Logger.Info("Background: Deploying updated helper files...");
                        var timer = Stopwatch.StartNew();

                        if (HelperDeploymentService.DeployHelper())
                        {
                            timer.Stop();
                            Logger.Info($"Background: Helper deployed successfully in {timer.ElapsedMilliseconds}ms");

                            // Also ensure task is configured correctly
                            if (!ScheduledTaskService.IsTaskConfiguredCorrectly())
                            {
                                Logger.Info("Background: Creating/updating scheduled task...");
                                if (ScheduledTaskService.CreateOrUpdateTask())
                                {
                                    Logger.Info("Background: Scheduled task created successfully");
                                }
                                else
                                {
                                    Logger.Warn("Background: Failed to create scheduled task");
                                }
                            }
                        }
                        else
                        {
                            Logger.Warn("Background: Failed to deploy helper files");
                        }
                    }
                    else
                    {
                        Logger.Info("Background: Deployment is up to date");

                        // Still check if task needs update
                        if (!ScheduledTaskService.IsTaskConfiguredCorrectly())
                        {
                            Logger.Info("Background: Task needs update, creating...");
                            ScheduledTaskService.CreateOrUpdateTask();
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"Background: Error during deployment check: {ex.Message}");
                }
            })
            {
                IsBackground = true,
                Name = "DeploymentUpdater"
            };
            deployThread.Start();
        }

        /// <summary>
        /// Performs the setup process: deploys files and creates task.
        /// Called from Program.cs when --setup flag is present.
        /// Returns true if setup succeeded and task was launched.
        /// </summary>
        public static bool PerformSetup()
        {
            // Debug file for tracing setup issues (independent of NLog)
            var debugPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "setup_debug.txt");
            void DebugLog(string msg) { try { File.AppendAllText(debugPath, $"{DateTime.Now}: {msg}\n"); } catch { } }

            try
            {
                DebugLog($"PerformSetup started, IsAdmin={IsRunningAsAdmin()}");

                if (!IsRunningAsAdmin())
                {
                    Logger.Error("PerformSetup called without admin privileges");
                    DebugLog("ERROR: Not admin");
                    return false;
                }

                Logger.Info("=== Starting Setup ===");
                DebugLog($"Source: {HelperDeploymentService.GetSourceDirectory()}");
                DebugLog($"Target: {HelperDeploymentService.HelperFolder}");
                DebugLog($"Version: {HelperDeploymentService.GetCurrentPackageVersion()}");
                Logger.Info($"Source directory: {HelperDeploymentService.GetSourceDirectory()}");
                Logger.Info($"Target directory: {HelperDeploymentService.HelperFolder}");
                Logger.Info($"Package version: {HelperDeploymentService.GetCurrentPackageVersion()}");

                // Step 0: stop any running/zombie helper instances first (#97). A helper
                // still running from the deployed folder keeps its exe/DLLs locked, so
                // every copy attempt fails, .version is never stamped, and each launch
                // re-fires the UAC setup loop. We're elevated here, so we have the rights
                // to kill them regardless of who started them.
                Logger.Info("Step 0: Stopping running helper instances...");
                DebugLog("Step 0: Killing running helper instances...");
                KillRunningHelperInstances(DebugLog);

                // Step 1: Deploy helper files
                Logger.Info("Step 1: Deploying helper files...");
                DebugLog("Step 1: Deploying...");
                if (!HelperDeploymentService.DeployHelper())
                {
                    Logger.Error("Failed to deploy helper files");
                    DebugLog("ERROR: DeployHelper returned false");
                    return false;
                }
                Logger.Info("Helper files deployed successfully");
                DebugLog($"Deployed OK, DeployedExePath exists: {File.Exists(HelperDeploymentService.DeployedExePath)}");

                // Step 2: Create scheduled task
                Logger.Info("Step 2: Creating scheduled task...");
                DebugLog("Step 2: Creating task...");
                if (!ScheduledTaskService.CreateOrUpdateTask())
                {
                    Logger.Error("Failed to create scheduled task");
                    DebugLog("ERROR: CreateOrUpdateTask returned false");
                    return false;
                }
                Logger.Info("Scheduled task created successfully");
                DebugLog("Task created OK");

                // Note: After setup, we EXIT and let the widget relaunch the helper.
                // The relaunched helper will detect setup is complete and run via scheduled task.
                // This is cleaner than having the UAC-launched helper continue running.
                Logger.Info("Setup complete - will exit and let widget relaunch");
                Logger.Info("=== Setup Complete ===");
                DebugLog("=== Setup Complete ===");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error during setup");
                DebugLog($"EXCEPTION: {ex.Message}\n{ex.StackTrace}");
                return false;
            }
        }

        /// <summary>
        /// Kills every other XboxGamingBarHelper process so the deploy that follows
        /// doesn't fail on locked files (#97). All sessions, not just ours — a stale
        /// scheduled-task helper holds the same locks wherever it lives. The launcher
        /// instance that spawned this setup has already exited by the time UAC consent
        /// completes, so "everything except self" is safe.
        /// </summary>
        private static void KillRunningHelperInstances(Action<string> debugLog)
        {
            try
            {
                int selfPid = Process.GetCurrentProcess().Id;
                var peers = Process.GetProcessesByName("XboxGamingBarHelper");
                foreach (var p in peers)
                {
                    try
                    {
                        if (p.Id == selfPid || p.HasExited)
                        {
                            continue;
                        }

                        Logger.Info($"Killing running helper instance (PID={p.Id}) before deploy");
                        debugLog?.Invoke($"Killing helper PID={p.Id}");
                        p.Kill();
                        if (!p.WaitForExit(5000))
                        {
                            Logger.Warn($"Helper PID={p.Id} did not exit within 5s; deploy may hit locked files");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"Could not kill helper PID={p.Id}: {ex.Message}");
                    }
                    finally
                    {
                        try { p.Dispose(); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Could not enumerate helper instances: {ex.Message}");
            }
        }

        /// <summary>
        /// Checks if the current process is running with administrator privileges.
        /// </summary>
        public static bool IsRunningAsAdmin()
        {
            try
            {
                using (var identity = WindowsIdentity.GetCurrent())
                {
                    var principal = new WindowsPrincipal(identity);
                    return principal.IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error checking admin status");
                return false;
            }
        }

        /// <summary>
        /// Launches the helper with --setup flag and UAC elevation.
        /// </summary>
        private static bool LaunchElevatedForSetup(string exePath, string[] args)
        {
            try
            {
                // Build arguments: original args + --setup flag
                var argsList = args.Where(a => a != "--setup").ToList();
                argsList.Add("--setup");
                string arguments = string.Join(" ", argsList);

                var startInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = arguments,
                    UseShellExecute = true,
                    Verb = "runas" // This triggers UAC
                };

                Logger.Info($"Launching with UAC: {exePath} {arguments}");
                var process = Process.Start(startInfo);
                return process != null;
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                // User cancelled UAC prompt
                Logger.Warn("User cancelled UAC elevation prompt");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error launching elevated setup process");
                return false;
            }
        }

        /// <summary>
        /// Legacy method for direct UAC elevation (fallback).
        /// </summary>
        private static bool LaunchElevated(string exePath, string[] args)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = args.Length > 0 ? string.Join(" ", args) : "",
                    UseShellExecute = true,
                    Verb = "runas" // This triggers UAC
                };

                var process = Process.Start(startInfo);
                return process != null;
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                // User cancelled UAC prompt
                Logger.Warn("User cancelled UAC elevation prompt");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error launching elevated process");
                return false;
            }
        }

        /// <summary>
        /// Removes the scheduled task. Call this during uninstall.
        /// </summary>
        public static void UnregisterTask()
        {
            ScheduledTaskService.RemoveTask();
        }

        /// <summary>
        /// Full uninstall: removes task and deployed files.
        /// </summary>
        public static void Uninstall()
        {
            ScheduledTaskService.Uninstall();
        }
    }
}
