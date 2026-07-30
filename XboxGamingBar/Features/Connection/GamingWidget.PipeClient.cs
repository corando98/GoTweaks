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
        // When the helper signals an intentional exit (HelperExiting), the very next pipe
        // disconnect is expected and must NOT trigger an auto-relaunch (issue #81). The
        // intentional initiator (Restart button / update flow) handles relaunch itself. A
        // disconnect within this window of the signal is treated as intentional.
        private DateTime _helperIntentionalExitUtc = DateTime.MinValue;
        private const int IntentionalExitSuppressMs = 8000;

        /// <summary>
        /// Handles messages received from helper via Named Pipe.
        /// </summary>
        private async void PipeClient_MessageReceived(object sender, IPC.PipeMessageEventArgs e)
        {
            try
            {
                // Only process messages if this is the active widget instance
                var activeWidget = App.GetActiveGamingWidget();
                if (activeWidget != null && activeWidget != this)
                {
                    Logger.Debug("Widget received pipe message but this is NOT the active instance. Ignoring.");
                    return;
                }

                // Parse the JSON message to ValueSet
                var message = ParsePipeMessageToValueSet(e.Message);
                if (message == null)
                {
                    Logger.Warn("Failed to parse pipe message");
                    return;
                }

                // TryGetValue, not indexer: string interpolation arguments are evaluated
                // even when Debug logging is off, so a message without a "Function" key
                // (e.g. HelperExiting notifications) would throw KeyNotFoundException
                // here and silently abort the whole handler before the checks below run.
                Logger.Debug($"Widget received pipe message: Function={(message.TryGetValue("Function", out object fnVal) ? fnVal : "(none)")}");

                // Helper told us it's exiting on purpose (kill / upgrade / update). Mark it so the
                // imminent pipe-disconnect doesn't auto-relaunch the dying helper and race it
                // (issue #81 — two helpers contending for the pipe). The intentional initiator
                // (Restart button / update flow) owns relaunch timing.
                if (message.TryGetValue("HelperExiting", out object exitReason))
                {
                    _helperIntentionalExitUtc = DateTime.UtcNow;
                    Logger.Info($"Helper signaled intentional exit ({exitReason}) — suppressing auto-relaunch on the imminent disconnect.");
                    return;
                }

                // Check for focus widget request from helper
                if (message.TryGetValue("Function", out object funcObj) &&
                    Convert.ToInt32(funcObj) == (int)Shared.Enums.Function.Labs_FocusWidget)
                {
                    Logger.Info("Focus widget request received from helper via pipe");
                    // This handler runs on the pipe read thread, not the UI thread (unlike the
                    // native Game Bar hotkey path, which already dispatches before calling
                    // FocusThisWidgetAsync). XboxGameBarWidgetControl.ActivateAsync needs the UI
                    // thread like any other widget-context call - dispatch explicitly here.
                    if (Dispatcher != null)
                    {
                        _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => { _ = FocusThisWidgetAsync(); });
                    }
                    return;
                }

                // A quick-tile controller combo fired on the helper — run the tile action.
                if (message.TryGetValue("Function", out object thFuncObj) &&
                    Convert.ToInt32(thFuncObj) == (int)Shared.Enums.Function.TileHotkeyFired)
                {
                    // Runs on the pipe read thread; SimulateTileHotkeyFired touches XAML, so
                    // dispatch to the UI thread (guard Dispatcher like the FocusWidget branch
                    // above - it can be null if the widget is being torn down).
                    if (Dispatcher != null &&
                        message.TryGetValue("Content", out object thContent) && thContent is string tileId
                        && !string.IsNullOrEmpty(tileId))
                    {
                        _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal,
                            () => SimulateTileHotkeyFired(tileId));
                    }
                    return;
                }

                // Check for Quick Metrics push from helper
                if (message.TryGetValue("Function", out object qmFuncObj) &&
                    Convert.ToInt32(qmFuncObj) == (int)Shared.Enums.Function.QuickMetrics)
                {
                    if (message.TryGetValue("Content", out object content) && content is string metricsJson)
                    {
                        UpdateQuickMetrics(metricsJson);
                    }
                    return;
                }

                // Calibration progress messages from helper. Pushed at start,
                // every ~250ms during the 5s capture, and once on completion
                // with the captured bias offset. Routed to the Viiper stick-
                // gyro section so the status text below the Calibrate button
                // updates live.
                if (message.TryGetValue("Function", out object calFuncObj) &&
                    Convert.ToInt32(calFuncObj) == (int)Shared.Enums.Function.ControllerEmulationCalibrateGyroStatus)
                {
                    if (message.TryGetValue("Content", out object calContent) && calContent is string calJson)
                    {
                        OnCalibrateGyroStatus(calJson);
                    }
                    return;
                }

                // Live gyro readings push for the visualizer.
                if (message.TryGetValue("Function", out object liveFuncObj) &&
                    Convert.ToInt32(liveFuncObj) == (int)Shared.Enums.Function.ControllerEmulationStickGyroLiveReadings)
                {
                    if (message.TryGetValue("Content", out object liveContent) && liveContent is string liveJson)
                    {
                        OnStickGyroLiveReadings(liveJson);
                    }
                    return;
                }

                // Helper push of the current software gyro bias offset (after a calibrate /
                // reset, and once on connect so the UI shows the persisted state). Routes to
                // the VIIPER UI handler to update the "Calibrated ... — bias X / Y / Z" line.
                if (message.TryGetValue("Function", out object gbFuncObj) &&
                    Convert.ToInt32(gbFuncObj) == (int)Shared.Enums.Function.GyroBiasOffset)
                {
                    if (message.TryGetValue("Content", out object gbContent) && gbContent is string gbJson)
                    {
                        OnGyroBiasOffsetReceived(gbJson);
                    }
                    return;
                }

                // Helper pushes DriverUpdatesAvailable as an unsolicited message
                // after its startup driver probe completes. Light up the Quick
                // tab tile; no other state needs updating yet.
                if (message.TryGetValue("DriverUpdatesAvailable", out object countObj))
                {
                    int count = 0;
                    try { count = Convert.ToInt32(countObj); } catch { }
                    UpdateDriverUpdatesTile(count);
                    return;
                }

                // Helper pushes GoTweaksUpdate (JSON blob) after startup
                // self-update probe. Also delivered as a response to an
                // explicit CheckGoTweaksUpdate request.
                if (message.TryGetValue("GoTweaksUpdate", out object goTweaksPayload)
                    && goTweaksPayload is string gtJson)
                {
                    HandleGoTweaksUpdatePush(gtJson);
                    return;
                }

                // Skip TDP and CurrentTDP updates during Sticky TDP reapply
                if (isStickyTDPReapplying && message.ContainsKey("Function"))
                {
                    var function = Convert.ToInt32(message["Function"]);
                    if (function == (int)Shared.Enums.Function.TDP || function == (int)Shared.Enums.Function.CurrentTDP)
                    {
                        Logger.Debug("Skipping TDP/CurrentTDP pipe update during Sticky TDP reapply");
                        return;
                    }
                }

                // Dispatch the whole property-update path to the UI thread. NamedPipeClient
                // delivers MessageReceived on a background reader thread; GenericProperty.SetValue
                // fires InvokePropertyChanged synchronously, and several subscribers (color picker
                // sync, visibility toggles, ComboBox.SelectedIndex=...) touch XAML. Those throw
                // RPC_E_WRONG_THREAD when invoked off the UI apartment (seen consistently in
                // live logs around TDP / LightBrightness debounce-cancel paths). Dispatching
                // here keeps the whole property tree on the UI thread.
                if (Dispatcher == null)
                {
                    // Fallback: no dispatcher available (widget torn down); skip.
                    return;
                }

                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    isApplyingHelperUpdate = true;
                    try
                    {
                        properties.HandlePipeMessage(message);
                    }
                    finally
                    {
                        isApplyingHelperUpdate = false;
                    }
                });
            }
            catch (Exception ex)
            {
                Logger.Error($"Error processing pipe message from helper: {ex.Message}");
            }
        }

        /// <summary>
        /// Parses a pipe JSON message into a ValueSet.
        /// </summary>
        private Windows.Foundation.Collections.ValueSet ParsePipeMessageToValueSet(string json)
        {
            try
            {
                var result = new Windows.Foundation.Collections.ValueSet();
                json = json.Trim();
                if (!json.StartsWith("{") || !json.EndsWith("}"))
                    return null;

                // Simple JSON parsing
                var matches = System.Text.RegularExpressions.Regex.Matches(json,
                    @"""(\w+)""\s*:\s*(""[^""\\]*(\\.[^""\\]*)*""|-?\d+\.?\d*|true|false|null|\{[^{}]*\}|\[[^\[\]]*\])");

                foreach (System.Text.RegularExpressions.Match match in matches)
                {
                    var key = match.Groups[1].Value;
                    var value = match.Groups[2].Value;

                    if (value.StartsWith("\"") && value.EndsWith("\""))
                    {
                        result[key] = UnescapeJsonString(value.Substring(1, value.Length - 2));
                    }
                    else if (value == "true")
                    {
                        result[key] = true;
                    }
                    else if (value == "false")
                    {
                        result[key] = false;
                    }
                    else if (value == "null")
                    {
                        result[key] = null;
                    }
                    else if (value.StartsWith("{") || value.StartsWith("["))
                    {
                        result[key] = value;
                    }
                    else if (value.Contains("."))
                    {
                        if (double.TryParse(value, out var d))
                            result[key] = d;
                    }
                    else
                    {
                        if (int.TryParse(value, out var i))
                            result[key] = i;
                        else if (long.TryParse(value, out var l))
                            result[key] = l;
                    }
                }

                return result;
            }
            catch (Exception ex)
            {
                Logger.Error($"Error parsing pipe message JSON: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Handles Named Pipe disconnection from the helper.
        /// Deliberately NOT async void with bare Dispatcher.RunAsync: this fires on
        /// the pipe read thread, and after Modern Standby the dispatcher may be dead
        /// (see GamingWidget.DispatcherHealth.cs) — an exception escaping an async
        /// void handler fail-fasts the whole Game Bar process. That crash-then-rehost
        /// is why "restart helper" used to appear to cure the blank-widget state.
        /// </summary>
        private void PipeClient_Disconnected(object sender, EventArgs e)
        {
            try
            {
                Logger.Info("Named pipe disconnected from helper");

                // Ignore disconnects from inactive/unloading widget instances.
                // A new active instance will own reconnection.
                if (isUnloading || App.GetActiveGamingWidget() != this)
                {
                    Logger.Info($"Skipping reconnect handling (isUnloading={isUnloading}, isActive={App.GetActiveGamingWidget() == this})");
                    return;
                }

                // Unregister handlers
                App.PipeMessageReceived -= PipeClient_MessageReceived;
                App.PipeDisconnected -= PipeClient_Disconnected;

                // If the active mode had EC override on, the helper was driving 0xC6C8 every
                // 3s. With the helper gone (crash or kill), 0xC6C8 holds whatever RPM we last
                // wrote — fan stuck at that value until the helper reconnects or reboot.
                // Surface a warning in the fan card so the user isn't left wondering why the
                // fan won't ramp. Cleared on next successful pipe-connect (OnPipeConnectedAsync).
                bool activeUnlockWasOn = legionUnlockFanCurve != null && legionUnlockFanCurve.Value;

                // Show reconnecting state and trigger guarded reconnect flow.
                TryRunOnDispatcher(() =>
                {
                    ShowConnectionBanner(BannerState.Reconnecting);
                    if (activeUnlockWasOn && FanCurveHelperDisconnectedWarning != null)
                    {
                        FanCurveHelperDisconnectedWarning.Visibility = Windows.UI.Xaml.Visibility.Visible;
                    }
                }, "PipeClient_Disconnected/banner");

                // Skip auto-relaunch if the helper just told us it's exiting on purpose. Relaunching
                // the dying helper here raced it and spawned two instances contending for the pipe
                // (issue #81). The intentional initiator owns relaunch.
                double sinceIntentionalMs = (DateTime.UtcNow - _helperIntentionalExitUtc).TotalMilliseconds;
                if (sinceIntentionalMs < IntentionalExitSuppressMs)
                {
                    Logger.Info($"Pipe disconnected after intentional helper exit ({sinceIntentionalMs:F0}ms ago) - skipping auto-relaunch.");
                    return;
                }

                Logger.Info("Pipe disconnected - starting automatic helper reconnection");
                TryRunOnDispatcher(() =>
                {
                    _ = LaunchHelperWithGuardsAsync("Pipe disconnected");
                }, "PipeClient_Disconnected/relaunch");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "PipeClient_Disconnected failed");
            }
        }

        // Single-pass JSON string unescape. Sequential Replace() calls corrupt data:
        // collapsing "\\" -> "\" before decoding "\t"/"\n"/"\r" lets a freshly-collapsed
        // backslash pair with an adjacent letter and be misread as an escape (e.g. a wire
        // "C:\\temp" -> "C:\temp" -> "C:<TAB>emp"). Decode each escape exactly once.
        private static string UnescapeJsonString(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            var sb = new System.Text.StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\\' && i + 1 < s.Length)
                {
                    char next = s[i + 1];
                    switch (next)
                    {
                        case '"': sb.Append('"'); i++; break;
                        case '\\': sb.Append('\\'); i++; break;
                        case 'n': sb.Append('\n'); i++; break;
                        case 'r': sb.Append('\r'); i++; break;
                        case 't': sb.Append('\t'); i++; break;
                        default: sb.Append(c); break;
                    }
                }
                else { sb.Append(c); }
            }
            return sb.ToString();
        }

    }
}
