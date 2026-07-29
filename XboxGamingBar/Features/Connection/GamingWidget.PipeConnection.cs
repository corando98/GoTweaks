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
        /// Attempts to connect to the helper via Named Pipe.
        /// Runs in background and triggers PipeConnected event on success.
        /// Uses longer retry duration to handle elevation scenario where helper
        /// goes through setup mode (UAC prompt, task creation) before pipe server starts.
        /// </summary>
        private async Task TryConnectPipeAsync()
        {
            // Retry duration must cover the elevation scenario:
            // - MSIX helper launches, checks elevation, launches --setup (UAC prompt)
            // - User approves UAC
            // - Setup helper deploys, creates task, runs task, exits
            // - Elevated helper starts from deployed location
            // - Pipe server starts
            // This can take 15-30 seconds total
            //
            // Use fast retries (500ms timeout + 250ms delay) for the first 10 attempts (~7.5s)
            // to minimize reconnection latency when helper is already running.
            // Then slow down for the remaining attempts to cover UAC/setup scenarios.
            const int maxAttempts = 80;
            const int fastTimeoutMs = 500;
            const int slowTimeoutMs = 1500;
            const int fastDelayMs = 250;
            const int slowDelayMs = 1000;
            const int fastAttempts = 10; // ~7.5s of fast retries
            var startTime = DateTime.Now;
            bool shownInitialSetupBanner = false;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                if (App.PipeClient?.IsConnected == true)
                {
                    Logger.Info("Pipe already connected");
                    return;
                }

                bool isFastPhase = attempt <= fastAttempts;
                int timeoutMs = isFastPhase ? fastTimeoutMs : slowTimeoutMs;
                int delayMs = isFastPhase ? fastDelayMs : slowDelayMs;

                // Only log every 10 attempts to reduce noise
                if (attempt == 1 || attempt % 10 == 0)
                {
                    Logger.Info($"Attempting pipe connection ({attempt}/{maxAttempts}, timeout={timeoutMs}ms)...");
                }

                // After 8 seconds, show InitialSetup banner (likely UAC/setup in progress)
                if (!shownInitialSetupBanner && (DateTime.Now - startTime).TotalSeconds >= 8)
                {
                    shownInitialSetupBanner = true;
                    await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                    {
                        ShowConnectionBanner(BannerState.InitialSetup);
                    });
                }

                bool connected = await App.ConnectPipeAsync(timeoutMs);

                if (connected)
                {
                    Logger.Info($"Connected to helper via Named Pipe! (attempt {attempt}, {(DateTime.Now - startTime).TotalMilliseconds:F0}ms elapsed)");

                    // Register for pipe messages
                    App.PipeMessageReceived -= PipeClient_MessageReceived;
                    App.PipeMessageReceived += PipeClient_MessageReceived;
                    App.PipeDisconnected -= PipeClient_Disconnected;
                    App.PipeDisconnected += PipeClient_Disconnected;

                    // Trigger connection success flow
                    await OnPipeConnectedAsync();
                    return;
                }

                // Wait before next attempt
                if (attempt < maxAttempts)
                {
                    await Task.Delay(delayMs);
                }
            }

            Logger.Warn($"Failed to connect via pipe after {maxAttempts} attempts ({(DateTime.Now - startTime).TotalSeconds:F1}s)");

            // Show disconnected banner so user knows connection failed
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                ShowConnectionBanner(BannerState.Disconnected);
                // Kick on the heartbeat watcher so we auto-reconnect if the helper
                // appears later (e.g. long UAC prompt during update-debug completes
                // just after we gave up).
                StartHeartbeatWatcher();
            });
        }

        /// <summary>
        /// Polls helper_heartbeat.json every few seconds while we're in the
        /// "Disconnected" state. When the heartbeat's mtime or pid changes we
        /// assume a fresh helper has come up and fire a new pipe-connect attempt,
        /// so the user doesn't have to close/reopen the widget when UAC approval
        /// or slow setup pushed the helper past our retry budget.
        /// </summary>
        private void StartHeartbeatWatcher()
        {
            if (heartbeatWatcherTimer != null) return; // already running

            // Capture the CURRENT heartbeat snapshot so we only react to FRESHER
            // heartbeats, not the stale one that was there when we gave up.
            (heartbeatWatcherLastMtimeTicks, heartbeatWatcherLastPid) = ReadHeartbeatSnapshot();
            heartbeatWatcherFailedRetries = 0;
            heartbeatWatcherStaleTicks = 0;

            heartbeatWatcherTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            heartbeatWatcherTimer.Tick += HeartbeatWatcher_Tick;
            heartbeatWatcherTimer.Start();
            Logger.Info($"Heartbeat watcher started (baseline mtime={heartbeatWatcherLastMtimeTicks}, pid={heartbeatWatcherLastPid})");
        }

        /// <summary>Stops the heartbeat watcher; called once we're connected again.</summary>
        private void StopHeartbeatWatcher()
        {
            if (heartbeatWatcherTimer == null) return;
            heartbeatWatcherTimer.Stop();
            heartbeatWatcherTimer.Tick -= HeartbeatWatcher_Tick;
            heartbeatWatcherTimer = null;
            Logger.Info("Heartbeat watcher stopped");
        }

        private void HeartbeatWatcher_Tick(object sender, object e)
        {
            // If we got connected via some other path, shut down.
            if (App.IsConnected)
            {
                heartbeatWatcherFailedRetries = 0;
                StopHeartbeatWatcher();
                return;
            }
            if (heartbeatWatcherReconnectInFlight) return;

            var (mtime, pid) = ReadHeartbeatSnapshot();
            // Nothing there yet — keep polling quietly.
            if (pid == 0 || mtime == 0) return;
            // Same file we've already seen — the helper side isn't writing heartbeats.
            // Either the process is gone (clean exits delete the file, so a leftover file
            // means it didn't exit cleanly) or it's a zombie whose main loop died while
            // other threads hold the mutex. Neither heals by waiting: after ~15 s of a
            // frozen heartbeat, force-launch a replacement — its startup takeover kills a
            // lingering zombie, and a genuinely dead helper just starts normally.
            if (mtime == heartbeatWatcherLastMtimeTicks && pid == heartbeatWatcherLastPid)
            {
                if (++heartbeatWatcherStaleTicks >= 5)
                {
                    heartbeatWatcherStaleTicks = 0;
                    Logger.Warn("Helper heartbeat frozen for ~15s while disconnected — force-launching replacement helper");
                    _ = LaunchHelperWithGuardsAsync("Heartbeat frozen while disconnected", forceLaunch: true);
                }
                return;
            }
            heartbeatWatcherStaleTicks = 0;

            Logger.Info($"Heartbeat watcher detected helper activity (pid={pid}, mtime={mtime}); retrying pipe connect");
            heartbeatWatcherLastMtimeTicks = mtime;
            heartbeatWatcherLastPid = pid;
            heartbeatWatcherReconnectInFlight = true;

            // Hide the "Disconnected" banner and switch to the Reconnecting state
            // for the duration of the retry so the user sees progress.
            ShowConnectionBanner(BannerState.Reconnecting);

            // Run the reconnect on the UI dispatcher — TryConnectPipeAsync
            // eventually fires OnPipeConnectedAsync which touches Xaml
            // properties and Windows.Data.Json parsers, both of which are
            // WinRT single-apartment and reject calls from threadpool
            // threads (RPC_E_WRONG_THREAD, HRESULT 0x8001010E). Observed
            // after a long UAC gap during helper updates: the connect
            // itself succeeded but the post-connect sync exploded.
            var _ignored = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, async () =>
            {
                try
                {
                    await TryConnectPipeAsync();
                }
                catch (Exception ex)
                {
                    Logger.Warn($"Heartbeat-watcher reconnect attempt threw: {ex.Message}");
                }
                finally
                {
                    heartbeatWatcherReconnectInFlight = false;
                }

                // Escalation: the heartbeat is FRESH (that's what triggered this retry)
                // yet the pipe still won't connect — the helper process is alive but its
                // pipe server is wedged. Retrying the pipe forever can't fix that; after
                // three strikes force-launch a new helper, whose startup takeover kills
                // the unreachable incumbent and replaces it (2026-07-27 field case).
                if (App.IsConnected)
                {
                    heartbeatWatcherFailedRetries = 0;
                }
                else if (++heartbeatWatcherFailedRetries >= 3)
                {
                    heartbeatWatcherFailedRetries = 0;
                    Logger.Warn("Helper heartbeat fresh but pipe unreachable after 3 retries — force-launching replacement helper");
                    try
                    {
                        await LaunchHelperWithGuardsAsync("Heartbeat fresh but pipe wedged", forceLaunch: true);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"Force-launch after wedge detection threw: {ex.Message}");
                    }
                }
            });
        }

        private static (long mtimeTicks, int pid) ReadHeartbeatSnapshot()
        {
            try
            {
                var path = Path.Combine(ApplicationData.Current.LocalFolder.Path, "helper_heartbeat.json");
                if (!File.Exists(path)) return (0, 0);
                var info = new FileInfo(path);
                long mtime = info.LastWriteTimeUtc.Ticks;
                int pid = 0;
                try
                {
                    var json = File.ReadAllText(path);
                    var m = Regex.Match(json, @"""pid""\s*:\s*(\d+)");
                    if (m.Success && int.TryParse(m.Groups[1].Value, out int parsedPid)) pid = parsedPid;
                }
                catch { /* mid-write is fine — mtime alone flags a change */ }
                return (mtime, pid);
            }
            catch { return (0, 0); }
        }

        /// <summary>
        /// Starts a timer that will force-launch the helper if connection isn't established within timeout.
        /// Call this when helper is detected as alive but not connected.
        /// </summary>
        private void StartReconnectionTimeoutTimer()
        {
            // Stop any existing timer
            StopReconnectionTimeoutTimer();

            // Don't start timer if already connected (via AppService or pipe)
            if (App.IsConnected)
            {
                Logger.Info("Reconnection timeout timer not started - already connected");
                return;
            }

            Logger.Info($"Starting reconnection timeout timer ({ReconnectionTimeoutSeconds}s)");

            reconnectionTimeoutTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(ReconnectionTimeoutSeconds)
            };
            reconnectionTimeoutTimer.Tick += ReconnectionTimeoutTimer_Tick;
            reconnectionTimeoutTimer.Start();
        }

        /// <summary>
        /// Stops the reconnection timeout timer if running.
        /// </summary>
        private void StopReconnectionTimeoutTimer()
        {
            if (reconnectionTimeoutTimer != null)
            {
                Logger.Info("Stopping reconnection timeout timer");
                reconnectionTimeoutTimer.Stop();
                reconnectionTimeoutTimer.Tick -= ReconnectionTimeoutTimer_Tick;
                reconnectionTimeoutTimer = null;
            }
        }

        /// <summary>
        /// Called when reconnection timeout fires - force launch the helper.
        /// </summary>
        private async void ReconnectionTimeoutTimer_Tick(object sender, object e)
        {
            // Stop the timer first (it's a one-shot)
            StopReconnectionTimeoutTimer();

            // Check if we're now connected (race condition check)
            if (App.IsConnected)
            {
                Logger.Info("Reconnection timeout fired but already connected - skipping force launch");
                HideConnectionBanner();
                return;
            }

            Logger.Info("Reconnection timeout fired - force launching helper");
            await LaunchHelperWithGuardsAsync("Reconnection timeout", forceLaunch: true);
        }

    }
}
