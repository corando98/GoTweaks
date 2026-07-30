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

        private async void GamingWidget_RequestedThemeChanged(XboxGameBarWidget sender, object args)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                SetBackgroundColor();
            });
        }

        private async void GamingWidget_SettingsClicked(XboxGameBarWidget sender, object args)
        {
            await widget.ActivateSettingsAsync();
        }

        private bool hasAppliedInitialSize = false;

        private async void GamingWidget_VisibleChanged(XboxGameBarWidget sender, object args)
        {
            try
            {
                bool isVisible = sender?.Visible ?? false;
                Logger.Info($"GamingWidget_VisibleChanged: Visible={isVisible}, DisplayMode={sender?.GameBarDisplayMode.ToString() ?? "Unknown"}");
                UpdateGameBarForegroundSignal("VisibleChanged");

                // Put gamepad focus on the active tab so the pad can navigate immediately on
                // open — but only if nothing is focused yet, so we never steal focus from a
                // user already interacting. Deferred so layout has settled first.
                if (isVisible)
                {
                    // Re-paint the Legion controller battery/connection/VID:PID display on every
                    // open. The underlying property values are usually already correct by now (the
                    // real battery reading typically arrives 180ms-1s after the widget's first
                    // BatchGet on a cold helper start), but the very first live-push repaint attempt
                    // can lose the race against the widget's own startup/dispatcher churn and throw
                    // RPC_E_WRONG_THREAD, which UpdateLegionControllerBatteryDisplay's catch block
                    // silently swallows - leaving the UI stuck on stale/blank values even though the
                    // data itself is fine. This gives the paint a guaranteed second chance on the
                    // next open instead of requiring the user to notice and manually reopen.
                    UpdateLegionControllerBatteryDisplay();
                    UpdateLegionControllerVidPidDisplay();

                    var ignoreFocus = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Low, () =>
                    {
                        // Anchor gamepad focus on open — UNCONDITIONALLY. Earlier
                        // heuristics ("only when focus is null / on a ScrollViewer")
                        // kept missing whatever non-visual element Game Bar actually
                        // parks focus on, so users still needed a touch tap or an A
                        // press before the nav bar showed focus (field feedback
                        // 2026-07-28 round 2). On the open transition there is no user
                        // focus state worth preserving — the nav strip is always the
                        // right starting point.
                        {
                            // Analog LT/RT poll only runs while the widget is visible.
                            StartAnalogTriggerPoll();
                            // Default-tab preference applies on every open, BEFORE the
                            // focus anchor so the anchor lands on the right pill.
                            ApplyDefaultTabOnOpen();
                            var focused = FocusManager.GetFocusedElement();
                            Logger.Info($"Open-anchor: focus was on {(focused == null ? "null" : focused.GetType().Name)}; anchoring to active nav item");
                            FocusActiveNavItem();
                        }
                    });
                }
                else
                {
                    // Hidden: stop the analog LT/RT poll so the widget doesn't touch
                    // Windows.Gaming.Input at all while a game has the controller.
                    var ignoreStop = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Low, () => StopAnalogTriggerPoll());
                }

                // Resize to full height on first activation.
                // Delay to let Game Bar finish restoring its cached layout first.
                if (isVisible && !hasAppliedInitialSize && sender != null)
                {
                    hasAppliedInitialSize = true;
                    await Task.Delay(500);
                    var success = await sender.TryResizeWindowAsync(new Windows.Foundation.Size(464, 1080));
                    Logger.Info($"Initial TryResizeWindowAsync(464x1080): success={success}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"GamingWidget_VisibleChanged failed: {ex.Message}");
            }
        }

        private void GamingWidget_GameBarDisplayModeChanged(XboxGameBarWidget sender, object args)
        {
            try
            {
                Logger.Info($"GamingWidget_GameBarDisplayModeChanged: DisplayMode={sender?.GameBarDisplayMode.ToString() ?? "Unknown"}, Visible={sender?.Visible ?? false}");
                UpdateGameBarForegroundSignal("GameBarDisplayModeChanged");
            }
            catch (Exception ex)
            {
                Logger.Error($"GamingWidget_GameBarDisplayModeChanged failed: {ex.Message}");
            }
        }

        private void UpdateGameBarForegroundSignal(string source)
        {
            try
            {
                bool widgetVisible = widget?.Visible ?? false;
                XboxGameBarDisplayMode displayMode = widget?.GameBarDisplayMode ?? XboxGameBarDisplayMode.Foreground;

                // Full Game Bar visibility signal:
                // - true when the overlay is foreground (even if this specific widget tab is not selected)
                // - false when app is backgrounded or Game Bar is not in foreground mode
                bool gameBarForeground = !appIsInBackground && displayMode == XboxGameBarDisplayMode.Foreground;
                isForeground.ForceSetValue(gameBarForeground);

                Logger.Info($"GameBar foreground signal update ({source}): value={gameBarForeground}, displayMode={displayMode}, widgetVisible={widgetVisible}, appBackground={appIsInBackground}");
            }
            catch (Exception ex)
            {
                Logger.Warn($"UpdateGameBarForegroundSignal failed ({source}): {ex.Message}");
            }
        }

    }
}
