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
                    var ignoreFocus = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Low, () =>
                    {
                        if (FocusManager.GetFocusedElement() == null)
                        {
                            FocusActiveNavItem();
                        }
                    });
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
