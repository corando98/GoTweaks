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

namespace XboxGamingBar
{
    public sealed partial class GamingWidget
    {

        /// <summary>
        /// Applies the current profile's TDP value and TDP Mode to the helper.
        /// Called after connection is established since profile loads before connection.
        /// </summary>
        private async Task ApplyProfileTDPToHelper()
        {
            try
            {
                // Skip when Default Game Profile is active - DGP controls TDP, not the profile
                if (defaultGameProfileEnabled?.Value == true)
                {
                    Logger.Info("Skipping ApplyProfileTDPToHelper - Default Game Profile is active");
                    return;
                }

                var profile = GetProfile(currentProfileName);
                if (profile == null)
                {
                    Logger.Warn($"Cannot apply profile TDP - profile '{currentProfileName}' not found");
                    return;
                }

                // Run on UI thread since we're touching UI controls (including SaveTDP which accesses checkbox)
                bool needsDelayForModeChange = false;
                await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                {
                    if (!SaveTDP) return;

                    // Apply Legion TDP Mode first (for Legion devices)
                    if (legionGoDetected?.Value == true && legionPerformanceMode != null)
                    {
                        int profileMode = profile.LegionPerformanceMode;
                        int modeIndex = GetProfileTDPModeIndex(profile);

                        if (modeIndex >= 0)
                        {
                            // During initial sync, always apply the profile's TDP mode to ensure
                            // hardware matches profile (hardware may have been set to Custom by TDP sync)
                            bool needsUIUpdate = (TDPModeComboBox != null && TDPModeComboBox.SelectedIndex != modeIndex) ||
                                                 (LegionPerformanceModeComboBox != null && LegionPerformanceModeComboBox.SelectedIndex != modeIndex);

                            Logger.Info($"Applying profile TDP Mode to helper: {GetLegionModeShortName(profileMode)} ({profileMode}) (profile: {currentProfileName})");

                            // Update lastTDPModeIndex FIRST before any ComboBox changes to prevent
                            // TDPModeComboBox_SelectionChanged from treating this as a user-initiated change
                            lastTDPModeIndex = modeIndex;

                            // Update UI combo boxes if needed
                            if (LegionPerformanceModeComboBox != null && LegionPerformanceModeComboBox.SelectedIndex != modeIndex)
                                LegionPerformanceModeComboBox.SelectedIndex = modeIndex;
                            if (TDPModeComboBox != null && TDPModeComboBox.SelectedIndex != modeIndex)
                                TDPModeComboBox.SelectedIndex = modeIndex;

                            // Always force send to helper during startup to ensure hardware matches profile
                            // This is necessary because TDP sync may have triggered Custom mode on hardware
                            legionPerformanceMode.ForceSetValue(profileMode);

                            // Update TDP slider enabled state
                            UpdateTDPSliderEnabledState();

                            // If switching to Custom mode, we need a delay before applying TDP value
                            // to allow the mode change to propagate to the helper first
                            if (profileMode == 255)
                            {
                                needsDelayForModeChange = true;
                            }
                        }
                    }
                });

                // If we just changed to Custom mode, wait for mode change to propagate to helper
                // before sending TDP value (mode change involves WMI calls that take time)
                if (needsDelayForModeChange)
                {
                    Logger.Info("Waiting for Custom mode to propagate to helper before applying TDP...");
                    await Task.Delay(300);
                }

                // Second dispatcher call for TDP value application
                await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                {
                    if (!SaveTDP) return;

                    // Apply TDP value ONLY in Custom mode (255)
                    // Quiet/Balanced/Performance modes use hardware presets and don't accept TDP values
                    bool isCustomMode = legionGoDetected?.Value != true || profile.LegionPerformanceMode == 255;

                    if (tdp != null && isCustomMode)
                    {
                        int targetTDP = (int)profile.TDP;
                        Logger.Info($"Applying profile TDP to helper: {targetTDP}W (profile: {currentProfileName})");

                        // Invalidate cached value first to force send even if values match
                        // This is needed because profile was loaded before connection, so the cached
                        // value matches but was never sent to hardware
                        tdp.SetValueSilent(-1);
                        tdp.SetValue(targetTDP, DateTime.Now.Ticks);

                        // Update Sticky TDP target if enabled
                        if (StickyTDPToggle?.IsOn == true)
                        {
                            targetTDPLimit = profile.TDP;
                            Logger.Info($"Sticky TDP target set to: {targetTDPLimit}W");
                        }
                    }
                    else if (tdp != null && !isCustomMode)
                    {
                        Logger.Info($"Skipping TDP value application - using {GetLegionModeShortName(profile.LegionPerformanceMode)} preset (profile: {currentProfileName})");
                    }
                });
            }
            catch (Exception ex)
            {
                Logger.Error($"Error applying profile TDP to helper: {ex.Message}");
            }
        }

        /// <summary>
        /// Check if helper is alive by reading its heartbeat file.
        /// Returns true if heartbeat file exists and is recent (less than HeartbeatStaleThresholdSeconds old).
        /// Also checks version match - if helper version doesn't match widget version, requests helper exit.
        /// </summary>
        private async Task<bool> IsHelperAliveAsync()
        {
            try
            {
                var localFolder = Windows.Storage.ApplicationData.Current.LocalFolder;
                var heartbeatFile = await localFolder.TryGetItemAsync("helper_heartbeat.json");

                if (heartbeatFile == null)
                {
                    Logger.Info("Heartbeat file not found - helper not running");
                    return false;
                }

                string content = await Windows.Storage.FileIO.ReadTextAsync((Windows.Storage.StorageFile)heartbeatFile);

                // Simple JSON parsing without external dependency
                // Format: {"pid":1234,"timestamp":1234567890,"connected":true,"elevated":true,"version":"0.3.1430.0"}
                var timestampMatch = System.Text.RegularExpressions.Regex.Match(content, @"""timestamp"":(\d+)");
                if (!timestampMatch.Success)
                {
                    Logger.Warn("Could not parse heartbeat timestamp");
                    return false;
                }

                long timestamp = long.Parse(timestampMatch.Groups[1].Value);
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                long age = now - timestamp;

                if (age > HeartbeatStaleThresholdSeconds)
                {
                    // Heartbeat stale, but check if process is still running (e.g., after sleep/hibernation)
                    var pidMatch = System.Text.RegularExpressions.Regex.Match(content, @"""pid"":(\d+)");
                    if (pidMatch.Success)
                    {
                        int pid = int.Parse(pidMatch.Groups[1].Value);
                        try
                        {
                            var process = System.Diagnostics.Process.GetProcessById(pid);
                            if (process != null && !process.HasExited)
                            {
                                Logger.Info($"Heartbeat stale ({age}s old) but process {pid} still running - helper likely resuming from sleep");
                                return true; // Helper is alive, just needs time to resume
                            }
                        }
                        catch (ArgumentException)
                        {
                            // Process not found - it has exited
                            Logger.Info($"Heartbeat stale ({age}s old) and process {pid} not found - helper is dead");
                        }
                        catch (Exception ex)
                        {
                            Logger.Debug($"Error checking process {pid}: {ex.Message}");
                        }
                    }

                    Logger.Info($"Heartbeat is stale ({age}s old) - helper may be hung or dead");
                    return false;
                }

                // Check version match - if helper is outdated, request it to exit
                var versionMatch = System.Text.RegularExpressions.Regex.Match(content, @"""version"":""([^""]+)""");
                if (versionMatch.Success)
                {
                    string helperVersion = versionMatch.Groups[1].Value;
                    string widgetVersion = GetWidgetVersion();

                    if (helperVersion != widgetVersion)
                    {
                        Logger.Info($"Helper version mismatch: helper={helperVersion}, widget={widgetVersion} - requesting helper restart");

                        // Show upgrading banner before requesting exit
                        await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                        {
                            ShowConnectionBanner(BannerState.Upgrading);
                        });

                        await RequestHelperExitAsync();

                        // Wait for the old helper to fully exit
                        // The helper has a 7 second force-exit timeout after receiving ExitHelper.
                        // We can't reliably check process exit from UWP (sandbox restrictions on elevated processes).
                        // Instead, wait for the force-exit timeout plus buffer to guarantee the old helper is dead.
                        const int forceExitTimeoutMs = 7000; // Helper's force-exit timeout
                        const int bufferMs = 1500; // Extra buffer for pipe cleanup
                        const int totalWaitMs = forceExitTimeoutMs + bufferMs;

                        Logger.Info($"Waiting {totalWaitMs}ms for old helper force-exit timeout...");
                        await Task.Delay(totalWaitMs);
                        Logger.Info("Old helper should now be fully exited");

                        // Force the next package-helper launch to redeploy the bundled (updated)
                        // helper. Without this, relaunching just reruns the stale deployed helper —
                        // which self-reports as "up to date" (its .version == its own assembly) — so
                        // the mismatch loops forever and the widget talks to an old-protocol helper.
                        InvalidateDeployedHelperVersion();

                        return false; // Return false so a new helper will be launched
                    }
                }

                Logger.Info($"Helper is alive (heartbeat {age}s old)");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Debug($"Error reading heartbeat: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Delete the deployed helper's .version file so the next package-context helper launch
        /// (via FullTrustProcessLauncher, which runs the deployment bootstrapper) sees
        /// IsDeploymentNeeded()==true and redeploys the bundled binaries. This breaks the update
        /// loop where a stale deployed helper keeps relaunching itself as current. Path mirrors
        /// HelperDeploymentService: {LocalCache}\GoTweaks\Helper\.version.
        /// </summary>
        private void InvalidateDeployedHelperVersion()
        {
            try
            {
                var localCache = Windows.Storage.ApplicationData.Current.LocalCacheFolder.Path;
                var versionFile = System.IO.Path.Combine(localCache, "GoTweaks", "Helper", ".version");
                if (System.IO.File.Exists(versionFile))
                {
                    System.IO.File.Delete(versionFile);
                    Logger.Info($"Invalidated deployed helper .version to force redeploy on next launch: {versionFile}");
                }
                else
                {
                    Logger.Info($"Deployed helper .version not found ({versionFile}) - redeploy will trigger on next launch");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"InvalidateDeployedHelperVersion failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Get the current widget/package version as a string.
        /// </summary>
        private string GetWidgetVersion()
        {
            try
            {
                var packageVersion = Windows.ApplicationModel.Package.Current.Id.Version;
                return $"{packageVersion.Major}.{packageVersion.Minor}.{packageVersion.Build}.{packageVersion.Revision}";
            }
            catch
            {
                return "0.0.0.0";
            }
        }

        /// <summary>
        /// Reads the helper heartbeat and returns true if the reported version matches this
        /// widget. Used to abort stale deferred retry tasks in the version-mismatch branch
        /// of OnPipeConnectedAsync when a parallel invocation has already landed on the
        /// correct-version helper.
        /// </summary>
        private async Task<bool> IsConnectedHelperVersionCurrentAsync()
        {
            try
            {
                var localFolder = Windows.Storage.ApplicationData.Current.LocalFolder;
                var heartbeatFile = await localFolder.TryGetItemAsync("helper_heartbeat.json");
                if (heartbeatFile == null) return false;

                string content = await Windows.Storage.FileIO.ReadTextAsync((Windows.Storage.StorageFile)heartbeatFile);
                var versionMatch = System.Text.RegularExpressions.Regex.Match(content, @"""version"":""([^""]+)""");
                if (!versionMatch.Success) return false;

                return versionMatch.Groups[1].Value == GetWidgetVersion();
            }
            catch (Exception ex)
            {
                Logger.Debug($"IsConnectedHelperVersionCurrentAsync check failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Request the helper to exit gracefully. Used when version mismatch is detected.
        /// </summary>
        private async Task RequestHelperExitAsync()
        {
            try
            {
                bool exitSent = false;

                // Try via Named Pipe
                if (App.PipeClient?.IsConnected == true)
                {
                    var request = new Windows.Foundation.Collections.ValueSet();
                    request.Add("ExitHelper", true);
                    await App.SendMessageAsync(request);
                    Logger.Info("Sent ExitHelper via Named Pipe");
                    exitSent = true;
                }
                // Not connected - try to establish a temporary pipe connection just to send ExitHelper
                else
                {
                    Logger.Info("Not connected to helper - attempting temporary pipe connection to send ExitHelper");
                    try
                    {
                        // Create a temporary pipe client just for this purpose
                        using (var tempPipe = new System.IO.Pipes.NamedPipeClientStream(".", "GoTweaksHelper", System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous))
                        {
                            // Try to connect with short timeout
                            var connectTask = tempPipe.ConnectAsync(2000);
                            if (await Task.WhenAny(connectTask, Task.Delay(2500)) == connectTask)
                            {
                                // Connected - send ExitHelper as simple JSON
                                using (var writer = new System.IO.StreamWriter(tempPipe, System.Text.Encoding.UTF8, 4096, leaveOpen: true))
                                {
                                    writer.AutoFlush = true;
                                    await writer.WriteLineAsync("{\"RequestId\":0,\"ExitHelper\":true}");
                                }
                                Logger.Info("Sent ExitHelper via temporary pipe connection");
                                exitSent = true;
                            }
                            else
                            {
                                Logger.Warn("Timed out connecting to helper pipe for ExitHelper");
                            }
                        }
                    }
                    catch (Exception pipeEx)
                    {
                        Logger.Warn($"Failed to establish temporary pipe connection: {pipeEx.Message}");
                    }
                }

                if (!exitSent)
                {
                    Logger.Warn("Could not send ExitHelper to helper - no connection available");
                }

                // Give helper time to exit
                await Task.Delay(1000);

                // Delete stale heartbeat file
                try
                {
                    var localFolder = Windows.Storage.ApplicationData.Current.LocalFolder;
                    var heartbeatFile = await localFolder.TryGetItemAsync("helper_heartbeat.json");
                    if (heartbeatFile != null)
                    {
                        await heartbeatFile.DeleteAsync();
                        Logger.Info("Deleted stale heartbeat file after requesting helper exit");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Debug($"Could not delete heartbeat file: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Failed to request helper exit: {ex.Message}");
            }
        }

        /// <summary>
        /// Request the helper to perform a UAC-free upgrade.
        /// Sends the MSIX source path so the elevated helper can copy new files and restart.
        /// This avoids UAC prompts during upgrades since the running helper is already elevated.
        /// </summary>
        /// <returns>True if upgrade succeeded and new helper is running, false otherwise</returns>
        private async Task<bool> RequestHelperUpgradeAsync()
        {
            try
            {
                // Show upgrading banner
                ShowConnectionBanner(BannerState.Upgrading);

                // Get the MSIX helper source path
                string msixSourcePath = GetMsixHelperSourcePath();
                if (string.IsNullOrEmpty(msixSourcePath))
                {
                    Logger.Warn("Could not determine MSIX helper source path - falling back to ExitHelper");
                    await RequestHelperExitAsync();
                    return false;
                }

                Logger.Info($"Requesting UAC-free upgrade with source: {msixSourcePath}");
                bool upgradeSent = false;

                // Try via Named Pipe (primary method)
                if (App.PipeClient?.IsConnected == true)
                {
                    var request = new Windows.Foundation.Collections.ValueSet();
                    request.Add("UpgradeHelper", msixSourcePath);
                    await App.SendMessageAsync(request);
                    Logger.Info("Sent UpgradeHelper via Named Pipe (already connected)");
                    upgradeSent = true;
                }
                // Not connected - try to establish a temporary pipe connection
                else
                {
                    Logger.Info("Not connected to helper - attempting temporary pipe connection for UpgradeHelper");
                    try
                    {
                        using (var tempPipe = new System.IO.Pipes.NamedPipeClientStream(".", "GoTweaksHelper", System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous))
                        {
                            var connectTask = tempPipe.ConnectAsync(2000);
                            if (await Task.WhenAny(connectTask, Task.Delay(2500)) == connectTask)
                            {
                                using (var writer = new System.IO.StreamWriter(tempPipe, System.Text.Encoding.UTF8, 4096, leaveOpen: true))
                                {
                                    writer.AutoFlush = true;
                                    // Send as JSON with UpgradeHelper in Extra
                                    string json = $"{{\"RequestId\":0,\"Extra\":{{\"UpgradeHelper\":\"{msixSourcePath.Replace("\\", "\\\\")}\"}}}}";
                                    await writer.WriteLineAsync(json);
                                }
                                Logger.Info("Sent UpgradeHelper via temporary pipe connection");
                                upgradeSent = true;
                            }
                            else
                            {
                                Logger.Warn("Timed out connecting to helper pipe for UpgradeHelper");
                            }
                        }
                    }
                    catch (Exception pipeEx)
                    {
                        Logger.Warn($"Failed to establish temporary pipe connection: {pipeEx.Message}");
                    }
                }

                if (!upgradeSent)
                {
                    Logger.Warn("Could not send UpgradeHelper - falling back to ExitHelper");
                    await RequestHelperExitAsync();
                    return false;
                }

                // Delete stale heartbeat file first
                try
                {
                    var localFolder = Windows.Storage.ApplicationData.Current.LocalFolder;
                    var heartbeatFile = await localFolder.TryGetItemAsync("helper_heartbeat.json");
                    if (heartbeatFile != null)
                    {
                        await heartbeatFile.DeleteAsync();
                        Logger.Info("Deleted stale heartbeat file before waiting for upgrade");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Debug($"Could not delete heartbeat file: {ex.Message}");
                }

                // Wait for the upgrade to complete and new helper to start
                // The batch script needs time to: wait for old helper to exit, copy files, run task
                // Poll for new heartbeat file to confirm new helper is running
                Logger.Info("Waiting for new helper to start after upgrade...");
                bool newHelperStarted = false;
                for (int i = 0; i < 15; i++) // Wait up to 15 seconds
                {
                    await Task.Delay(1000);

                    try
                    {
                        var localFolder = Windows.Storage.ApplicationData.Current.LocalFolder;
                        var heartbeatFile = await localFolder.TryGetItemAsync("helper_heartbeat.json");
                        if (heartbeatFile != null)
                        {
                            // Heartbeat file exists - check if it's from new helper
                            var file = heartbeatFile as Windows.Storage.StorageFile;
                            if (file != null)
                            {
                                string content = await Windows.Storage.FileIO.ReadTextAsync(file);
                                // Check if version matches new version
                                var versionMatch = System.Text.RegularExpressions.Regex.Match(content, @"""version"":""([^""]+)""");
                                if (versionMatch.Success)
                                {
                                    string helperVersion = versionMatch.Groups[1].Value;
                                    string widgetVersion = GetWidgetVersion();
                                    if (helperVersion == widgetVersion)
                                    {
                                        Logger.Info($"New helper v{helperVersion} started successfully after upgrade");
                                        newHelperStarted = true;
                                        break;
                                    }
                                    else
                                    {
                                        Logger.Debug($"Heartbeat version {helperVersion} doesn't match widget {widgetVersion} yet");
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Debug($"Error checking heartbeat during upgrade wait: {ex.Message}");
                    }
                }

                if (newHelperStarted)
                {
                    Logger.Info("UAC-free upgrade completed successfully");
                    HideConnectionBanner();
                    return true;
                }
                else
                {
                    Logger.Warn("New helper did not start within timeout - may need manual intervention");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Failed to request helper upgrade: {ex.Message} - falling back to ExitHelper");
                await RequestHelperExitAsync();
                return false;
            }
        }

        /// <summary>
        /// Gets the MSIX helper source path (in WindowsApps folder).
        /// </summary>
        private string GetMsixHelperSourcePath()
        {
            try
            {
                var package = Windows.ApplicationModel.Package.Current;
                var installPath = package.InstalledLocation.Path;
                return System.IO.Path.Combine(installPath, "XboxGamingBarHelper");
            }
            catch (Exception ex)
            {
                Logger.Warn($"Could not get MSIX helper path: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Launch helper with guards to prevent duplicate launches and unnecessary UAC prompts.
        /// Checks if helper is already alive (via heartbeat) and enforces minimum interval between launches.
        /// </summary>
        /// <param name="reason">Description of why we're launching (for logging)</param>
        /// <param name="forceLaunch">If true, ignore heartbeat check (use when sync explicitly failed)</param>
        /// <returns>True if launch was attempted, false if skipped</returns>
        private async Task<bool> LaunchHelperWithGuardsAsync(string reason, bool forceLaunch = false)
        {
            // Check if already launching
            if (isLaunchingHelper)
            {
                Logger.Info($"Skipping launch ({reason}) - already launching");
                // Keep existing Launching banner visible
                return false;
            }

            // Check minimum interval (skip if force launching)
            var timeSinceLastLaunch = (DateTime.Now - lastLaunchAttempt).TotalMilliseconds;
            if (!forceLaunch && timeSinceLastLaunch < MinLaunchIntervalMs)
            {
                Logger.Info($"Skipping launch ({reason}) - too soon since last attempt ({timeSinceLastLaunch:F0}ms)");
                // Show reconnecting banner since we're rate-limited but trying to connect
                ShowConnectionBanner(BannerState.Reconnecting);
                return false;
            }

            // Check if helper is already alive (skip if force launching - we know connection is broken)
            if (!forceLaunch)
            {
                bool helperAlive = await IsHelperAliveAsync();
                if (helperAlive)
                {
                    Logger.Info($"Skipping launch ({reason}) - helper is already alive, trying pipe connection");
                    // Show reconnecting banner since we're waiting for helper to reconnect
                    ShowConnectionBanner(BannerState.Reconnecting);

                    // Immediately try to connect via Named Pipe (don't wait for timeout)
                    _ = TryConnectPipeAsync();

                    // Start timeout timer as backup - if pipe doesn't connect within timeout, force launch
                    StartReconnectionTimeoutTimer();
                    return false;
                }
            }
            else
            {
                Logger.Info($"Force launching helper ({reason}) - ignoring heartbeat check");
            }

            try
            {
                isLaunchingHelper = true;
                lastLaunchAttempt = DateTime.Now;

                Logger.Info($"Launching helper ({reason})...");
                ShowConnectionBanner(BannerState.Launching);
                await FullTrustProcessLauncher.LaunchFullTrustProcessForCurrentAppAsync();
                Logger.Info("Helper launch completed");

                // Try to connect via Named Pipe (works even when helper is elevated)
                // Brief delay for helper to start, then fast retry loop handles the rest
                await Task.Delay(200);
                _ = TryConnectPipeAsync();

                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"Error launching helper: {ex.Message}");
                return false;
            }
            finally
            {
                isLaunchingHelper = false;
            }
        }

    }
}
