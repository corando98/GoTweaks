using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
using NLog;

namespace XboxGamingBarHelper.Services
{
    /// <summary>
    /// Manages the Windows scheduled task for auto-starting the helper with elevated privileges.
    /// The task points to the deployed location for stability across MSIX updates.
    /// </summary>
    public static class ScheduledTaskService
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        /// <summary>
        /// Task folder path in Task Scheduler
        /// </summary>
        private const string TaskFolder = "\\GoTweaks";

        /// <summary>
        /// Full task name including folder
        /// </summary>
        private const string FullTaskName = "\\GoTweaks\\GoTweaksHelper";

        /// <summary>
        /// Task name for schtasks.exe (uses backslash path)
        /// </summary>
        private const string TaskName = "GoTweaks\\GoTweaksHelper";

        /// <summary>
        /// Timeout for process operations
        /// </summary>
        private const int ProcessTimeoutMs = 10000;

        /// <summary>
        /// Set after CreateOrUpdateTask runs in this process. Used to break a potential
        /// recreate loop when ConfigureTaskSettings cannot flip the AC-only battery flags
        /// (e.g., Group Policy enforcing DisallowStartIfOnBatteries=true). Without this
        /// guard, IsTaskConfiguredCorrectly would keep returning false on the same boot
        /// after recreate, churning the task repeatedly.
        /// </summary>
        private static bool _hasAttemptedRecreateThisSession;

        /// <summary>
        /// Checks if the scheduled task exists
        /// </summary>
        public static bool IsTaskInstalled()
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = $"/Query /TN \"{TaskName}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(startInfo))
                {
                    if (process == null) return false;
                    process.WaitForExit(ProcessTimeoutMs);
                    return process.ExitCode == 0;
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"Error checking if task exists: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Checks if the task is configured correctly (points to deployed path)
        /// </summary>
        public static bool IsTaskConfiguredCorrectly()
        {
            try
            {
                // Use XML format to avoid line-wrapping issues with long paths
                var startInfo = new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = $"/Query /TN \"{TaskName}\" /XML",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(startInfo))
                {
                    if (process == null) return false;

                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit(ProcessTimeoutMs);

                    if (process.ExitCode != 0)
                    {
                        Logger.Debug("Task query failed - task doesn't exist");
                        return false;
                    }

                    // Parse <Command> element from XML output
                    // Format: <Command>"path\to\exe"</Command> or <Command>path\to\exe</Command>
                    var match = Regex.Match(output, @"<Command>""?([^""<]+)""?</Command>", RegexOptions.IgnoreCase);
                    if (!match.Success)
                    {
                        Logger.Debug("Could not parse task path from XML output");
                        return false;
                    }

                    string taskPath = match.Groups[1].Value.Trim().Trim('"');
                    // Compare against deployed path (not MSIX path)
                    // The deployed helper uses Named Pipes to communicate, which doesn't require package context
                    string expectedPath = HelperDeploymentService.DeployedExePath;

                    bool pathOk = string.Equals(taskPath, expectedPath, StringComparison.OrdinalIgnoreCase);
                    if (!pathOk)
                    {
                        Logger.Info($"Task path mismatch: Task={taskPath}, Expected={expectedPath}");
                        return false;
                    }

                    // Also verify the battery conditions are disabled. schtasks /Create defaults
                    // DisallowStartIfOnBatteries=true and StopIfGoingOnBatteries=true. We flip
                    // them off in ConfigureTaskSettings() via a follow-up PowerShell call, but
                    // that flip can fail silently (timing race with Get-ScheduledTask, or PS
                    // exit non-zero just logs a warning). When it fails, the helper won't run
                    // on battery — which has bitten at least one user in the wild (issue #79
                    // vvalente30). Treat any leftover AC-only condition as a misconfigured
                    // task so the caller recreates it.
                    bool acOnlyDisallowed = Regex.IsMatch(output,
                        @"<DisallowStartIfOnBatteries>\s*true\s*</DisallowStartIfOnBatteries>",
                        RegexOptions.IgnoreCase);
                    bool acOnlyStops = Regex.IsMatch(output,
                        @"<StopIfGoingOnBatteries>\s*true\s*</StopIfGoingOnBatteries>",
                        RegexOptions.IgnoreCase);

                    if (acOnlyDisallowed || acOnlyStops)
                    {
                        if (_hasAttemptedRecreateThisSession)
                        {
                            // We already recreated the task this process lifetime but the AC-only
                            // flags came back, which means ConfigureTaskSettings's PowerShell flip
                            // is being blocked (most likely cause: Group Policy enforcement).
                            // Don't churn the task on every helper restart — accept the current
                            // state and warn the user. Helper may not run on battery in this case.
                            Logger.Warn($"Task still has AC-only battery conditions after recreate attempt this session (DisallowStart={acOnlyDisallowed}, StopOnBattery={acOnlyStops}) — likely Group Policy enforced. Accepting current state to avoid recreate loop. Helper may not run on battery.");
                            return true;
                        }
                        Logger.Info($"Task has AC-only battery conditions (DisallowStart={acOnlyDisallowed}, StopOnBattery={acOnlyStops}) — will recreate to clear them.");
                        return false;
                    }

                    Logger.Debug($"Task configured correctly: {taskPath}");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"Error checking task configuration: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Creates or updates the scheduled task to point to the deployed helper location.
        /// Must be called from an elevated process.
        /// Note: We use the deployed path (not MSIX path) because:
        /// 1. MSIX executables can't run directly from scheduled tasks (ACCESS_DENIED)
        /// 2. The deployed helper uses Named Pipes for IPC, which doesn't require package context
        /// 3. The deployed path stays stable across MSIX updates (just need to re-deploy files)
        /// </summary>
        public static bool CreateOrUpdateTask()
        {
            try
            {
                // Mark before any work so the recreate-loop guard in IsTaskConfiguredCorrectly
                // trips on the next call this session, even if we throw partway through.
                _hasAttemptedRecreateThisSession = true;

                // Use deployed path - MSIX executables can't run from scheduled tasks
                // The deployed helper uses Named Pipes to communicate with the widget
                string exePath = HelperDeploymentService.DeployedExePath;
                Logger.Info($"Creating scheduled task pointing to: {exePath}");

                // First, delete any existing task (both new and legacy)
                RemoveTask();
                RemoveLegacyTaskIfExists();

                // Create task with schtasks.exe
                // /SC ONLOGON - run at user logon
                // /RL HIGHEST - run with highest privileges
                // /F - force create (overwrite if exists)
                // /DELAY 0000:30 - delay 30 seconds after logon
                var createInfo = new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = $"/Create /TN \"{TaskName}\" /TR \"\\\"{exePath}\\\"\" /SC ONLOGON /RL HIGHEST /F /DELAY 0000:05",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(createInfo))
                {
                    if (process == null)
                    {
                        Logger.Error("Failed to start schtasks process");
                        return false;
                    }

                    string output = process.StandardOutput.ReadToEnd();
                    string error = process.StandardError.ReadToEnd();
                    process.WaitForExit(ProcessTimeoutMs);

                    if (process.ExitCode != 0)
                    {
                        Logger.Error($"schtasks /Create failed: {error}");
                        return false;
                    }

                    Logger.Debug($"schtasks /Create output: {output}");
                }

                // Configure additional settings via PowerShell
                ConfigureTaskSettings();

                // Add wake from sleep trigger
                AddWakeFromSleepTrigger();

                Logger.Info("Scheduled task created successfully");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error creating scheduled task");
                return false;
            }
        }

        /// <summary>
        /// Configures additional task settings (battery, time limit, multiple instances).
        ///
        /// Why the script does more than mutate $task.Settings:
        /// - The original mutate-and-Set-ScheduledTask formulation could silently leave
        ///   DisallowStartIfOnBatteries=true if Set-ScheduledTask returned successfully but
        ///   didn't actually persist the property change (seen in the wild — issue #79
        ///   vvalente30 reported the AC-only flag being re-enabled after an MSIX upgrade,
        ///   which removes the task on package events and recreates it via this code path).
        /// - We now build a fresh settings object via New-ScheduledTaskSettingsSet and pass
        ///   it explicitly via -Settings, then re-query and verify the battery flags are
        ///   actually $false. PS exits non-zero on verify failure so the C# side can log it
        ///   loudly instead of swallowing the failure.
        /// - A short retry loop on Get-ScheduledTask handles the small window after schtasks
        ///   /Create where the cmdlet's view of the task can lag.
        /// </summary>
        private static void ConfigureTaskSettings()
        {
            try
            {
                var psScript = @"
$ErrorActionPreference = 'Stop'
$task = $null
for ($i = 0; $i -lt 10; $i++) {
    $task = Get-ScheduledTask -TaskName 'GoTweaksHelper' -TaskPath '\GoTweaks\' -ErrorAction SilentlyContinue
    if ($task) { break }
    Start-Sleep -Milliseconds 200
}
if (-not $task) {
    Write-Error 'Get-ScheduledTask returned null after schtasks /Create'
    exit 2
}
$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit (New-TimeSpan -Seconds 0) `
    -MultipleInstances IgnoreNew `
    -StartWhenAvailable
Set-ScheduledTask -TaskName 'GoTweaksHelper' -TaskPath '\GoTweaks\' -Settings $settings | Out-Null
$verify = Get-ScheduledTask -TaskName 'GoTweaksHelper' -TaskPath '\GoTweaks\'
if ($verify.Settings.DisallowStartIfOnBatteries -or $verify.Settings.StopIfGoingOnBatteries) {
    Write-Error (""Verify failed: DisallowStart="" + $verify.Settings.DisallowStartIfOnBatteries + "" StopOnBattery="" + $verify.Settings.StopIfGoingOnBatteries)
    exit 3
}
Write-Host 'Task settings configured and verified'
";

                var psInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{psScript.Replace("\"", "\\\"")}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(psInfo))
                {
                    if (process == null)
                    {
                        Logger.Warn("Could not start PowerShell to configure task settings");
                        return;
                    }

                    string output = process.StandardOutput.ReadToEnd();
                    string error = process.StandardError.ReadToEnd();
                    process.WaitForExit(ProcessTimeoutMs);

                    if (process.ExitCode != 0)
                    {
                        // Bubble up at Error level. The task will likely be AC-only and the
                        // bootstrapper's IsTaskConfiguredCorrectly check will recreate it on
                        // the next helper start — but if this keeps failing across recreates
                        // we want it visible in the log, not buried as a warning.
                        Logger.Error($"PowerShell task settings failed (ExitCode={process.ExitCode}): stderr={error.Trim()} stdout={output.Trim()}");
                    }
                    else
                    {
                        Logger.Info($"Task settings configured successfully: {output.Trim()}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Error configuring task settings: {ex.Message}");
            }
        }

        /// <summary>
        /// Adds wake from sleep event trigger to the task
        /// </summary>
        private static void AddWakeFromSleepTrigger()
        {
            try
            {
                var wakeEventXml = "<QueryList><Query Id='0' Path='System'><Select Path='System'>*[System[Provider[@Name='Microsoft-Windows-Power-Troubleshooter'] and EventID=1]]</Select></Query></QueryList>";

                var psScript = $@"
$xml = @'
{wakeEventXml}
'@
$CIMClass = Get-CimClass -ClassName MSFT_TaskEventTrigger -Namespace Root/Microsoft/Windows/TaskScheduler
$trigger = New-CimInstance -CimClass $CIMClass -ClientOnly
$trigger.Subscription = $xml
$trigger.Enabled = $true
$trigger.Delay = 'PT10S'
$task = Get-ScheduledTask -TaskName 'GoTweaksHelper' -TaskPath '\GoTweaks\'
$allTriggers = @($task.Triggers) + $trigger
Set-ScheduledTask -TaskName 'GoTweaksHelper' -TaskPath '\GoTweaks\' -Trigger $allTriggers | Out-Null
Write-Host 'Wake trigger added'
";

                var psInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{psScript.Replace("\"", "\\\"")}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(psInfo))
                {
                    if (process != null)
                    {
                        process.WaitForExit(ProcessTimeoutMs);

                        if (process.ExitCode != 0)
                        {
                            string error = process.StandardError.ReadToEnd();
                            Logger.Warn($"PowerShell wake trigger warning: {error}");
                        }
                        else
                        {
                            Logger.Info("Wake from sleep trigger added successfully");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Error adding wake trigger: {ex.Message}");
            }
        }

        /// <summary>
        /// Reads whether the task's logon trigger is enabled ("start on boot",
        /// tray toggle). The task itself must stay registered and runnable
        /// either way — on-demand elevation relaunch depends on RunTaskNow.
        /// Defaults to true (the created state) when the query fails.
        /// </summary>
        public static bool IsLogonTriggerEnabled()
        {
            try
            {
                var psScript = @"$t = Get-ScheduledTask -TaskName 'GoTweaksHelper' -TaskPath '\GoTweaks\' -ErrorAction Stop; $lt = @($t.Triggers) | Where-Object { $_.CimClass.CimClassName -eq 'MSFT_TaskLogonTrigger' } | Select-Object -First 1; if ($null -eq $lt) { 'none' } elseif ($lt.Enabled -eq $false) { 'disabled' } else { 'enabled' }";
                var psInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{psScript.Replace("\"", "\\\"")}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using (var process = Process.Start(psInfo))
                {
                    if (process == null) return true;
                    string output = process.StandardOutput.ReadToEnd().Trim();
                    process.WaitForExit(ProcessTimeoutMs);
                    Logger.Info($"Logon trigger state query: '{output}'");
                    return output != "disabled";
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"IsLogonTriggerEnabled failed: {ex.Message}");
                return true;
            }
        }

        /// <summary>
        /// Enables/disables the task's logon trigger (tray "Start on boot").
        /// Follows the AddWakeFromSleepTrigger pattern: modify the trigger
        /// list via the ScheduledTasks CIM cmdlets and write it back.
        /// </summary>
        public static bool SetLogonTriggerEnabled(bool enabled)
        {
            try
            {
                string psBool = enabled ? "$true" : "$false";
                var psScript = $@"$t = Get-ScheduledTask -TaskName 'GoTweaksHelper' -TaskPath '\GoTweaks\' -ErrorAction Stop; $triggers = @($t.Triggers); foreach ($tr in $triggers) {{ if ($tr.CimClass.CimClassName -eq 'MSFT_TaskLogonTrigger') {{ $tr.Enabled = {psBool} }} }}; Set-ScheduledTask -TaskName 'GoTweaksHelper' -TaskPath '\GoTweaks\' -Trigger $triggers | Out-Null; 'ok'";
                var psInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{psScript.Replace("\"", "\\\"")}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using (var process = Process.Start(psInfo))
                {
                    if (process == null) return false;
                    string output = process.StandardOutput.ReadToEnd().Trim();
                    string error = process.StandardError.ReadToEnd();
                    process.WaitForExit(ProcessTimeoutMs);
                    bool ok = process.ExitCode == 0 && output.EndsWith("ok");
                    if (ok)
                    {
                        Logger.Info($"Logon trigger (start on boot) set to {enabled}");
                    }
                    else
                    {
                        Logger.Warn($"SetLogonTriggerEnabled({enabled}) failed: exit={process.ExitCode}, stderr={error.Trim()}");
                    }
                    return ok;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"SetLogonTriggerEnabled failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Removes the scheduled task
        /// </summary>
        public static void RemoveTask()
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = $"/Delete /TN \"{TaskName}\" /F",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(startInfo))
                {
                    if (process == null) return;

                    process.WaitForExit(ProcessTimeoutMs);

                    if (process.ExitCode == 0)
                    {
                        Logger.Info("Scheduled task removed successfully");
                    }
                    else
                    {
                        // Task probably didn't exist
                        Logger.Debug("Task removal - task may not have existed");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"Error removing task: {ex.Message}");
            }
        }

        /// <summary>
        /// Removes the legacy task from previous implementation (without folder) if it exists.
        /// Safe to call multiple times.
        /// </summary>
        public static void RemoveLegacyTaskIfExists()
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = "/Delete /TN \"GoTweaksHelper\" /F",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(startInfo))
                {
                    if (process == null) return;

                    process.WaitForExit(ProcessTimeoutMs);

                    if (process.ExitCode == 0)
                    {
                        Logger.Info("Legacy scheduled task removed successfully");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"Error removing legacy task: {ex.Message}");
            }
        }

        /// <summary>
        /// Runs the scheduled task immediately (no UAC prompt).
        /// Waits for the scheduled task helper to be fully ready before returning.
        /// </summary>
        /// <returns>True if task started successfully and helper is ready</returns>
        public static bool RunTaskNow()
        {
            try
            {
                Logger.Info("Running scheduled task...");

                var startInfo = new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = $"/Run /TN \"{TaskName}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(startInfo))
                {
                    if (process == null)
                    {
                        Logger.Error("Failed to start schtasks /Run");
                        return false;
                    }

                    string output = process.StandardOutput.ReadToEnd();
                    string error = process.StandardError.ReadToEnd();
                    process.WaitForExit(ProcessTimeoutMs);

                    Logger.Info($"schtasks /Run: ExitCode={process.ExitCode}, Output={output.Trim()}, Error={error.Trim()}");

                    if (process.ExitCode != 0)
                    {
                        Logger.Error($"schtasks /Run failed: {error}");
                        return false;
                    }

                    // Wait for the scheduled task helper to acquire the mutex (proving it's running)
                    // This prevents a race where widget connects before the new helper is ready
                    const string mutexName = "Global\\XboxGamingBarHelper_SingleInstance";
                    const int maxWaitMs = 5000;
                    const int pollIntervalMs = 100;
                    int elapsed = 0;

                    Logger.Info("Waiting for scheduled task helper to start...");
                    while (elapsed < maxWaitMs)
                    {
                        try
                        {
                            // Try to open the mutex - if it exists, the new helper is running
                            using (var testMutex = Mutex.OpenExisting(mutexName))
                            {
                                Logger.Info($"Scheduled task helper acquired mutex after {elapsed}ms");
                                // Give extra time for manager initialization to complete
                                // Mutex is acquired early in Main(), but managers (LegionManager,
                                // PerformanceManager, etc.) take ~500-1000ms to initialize.
                                // BatchGet will fail with NullReferenceException if sent before ready.
                                Thread.Sleep(2000);
                                Logger.Info("Scheduled task helper should be ready now");
                                return true;
                            }
                        }
                        catch (WaitHandleCannotBeOpenedException)
                        {
                            // Mutex doesn't exist yet - helper hasn't started
                            Thread.Sleep(pollIntervalMs);
                            elapsed += pollIntervalMs;
                        }
                        catch (Exception ex)
                        {
                            Logger.Debug($"Mutex check error: {ex.Message}");
                            Thread.Sleep(pollIntervalMs);
                            elapsed += pollIntervalMs;
                        }
                    }

                    Logger.Warn($"Scheduled task helper did not acquire mutex within {maxWaitMs}ms - proceeding anyway");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error running scheduled task");
                return false;
            }
        }

        /// <summary>
        /// Full uninstall: removes task and deployed files
        /// </summary>
        public static void Uninstall()
        {
            Logger.Info("Uninstalling scheduled task and deployment...");
            RemoveTask();
            HelperDeploymentService.RemoveDeployment();
            Logger.Info("Uninstall complete");
        }
    }
}
