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

        private void GamingWidget_Unloaded(object sender, RoutedEventArgs e)
        {
            // Set flag immediately to prevent any pending async operations from updating UI
            isUnloading = true;

            Logger.Info($"GamingWidget_Unloaded called. Widget is null: {widget == null}, WidgetActivity is null: {widgetActivity == null}, Pipe connected: {App.IsConnected}");

            // Unsubscribe from power source changes
            PowerManager.PowerSupplyStatusChanged -= PowerManager_PowerSourceChanged;
            if (PowerSourceProfileToggle != null)
            {
                PowerSourceProfileToggle.Toggled -= PowerSourceProfileToggle_Toggled;
            }

            if (widget != null)
            {
                widget.RequestedThemeChanged -= GamingWidget_RequestedThemeChanged;
                widget.SettingsClicked -= GamingWidget_SettingsClicked;
                widget.VisibleChanged -= GamingWidget_VisibleChanged;
                widget.GameBarDisplayModeChanged -= GamingWidget_GameBarDisplayModeChanged;
            }

            // Stop Sticky TDP timer
            StopStickyTDPTimer();
            if (stickyTDPTimer != null)
            {
                stickyTDPTimer.Tick -= StickyTDPTimer_Tick;
                stickyTDPTimer = null;
            }

            // Stop power source TDP reapply timer
            if (powerSourceTdpReapplyTimer != null)
            {
                powerSourceTdpReapplyTimer.Stop();
                powerSourceTdpReapplyTimer = null;
            }

            // Stop reconnection timeout timer
            StopReconnectionTimeoutTimer();

            // Unregister this instance as the active widget
            Logger.Info("Unregistering this GamingWidget instance as the active widget.");
            App.UnregisterActiveGamingWidget(this);
            Logger.Info("GamingWidget instance unregistered.");

            // Detach this instance's pipe handlers. The active-instance guards inside those
            // handlers already make a stale instance's callbacks a no-op, but leaving them
            // subscribed across repeated Game-Bar-driven instance recreations lets dead
            // GamingWidget instances pile up on the static App.PipeMessageReceived event.
            App.PipeMessageReceived -= PipeClient_MessageReceived;
            App.PipeDisconnected -= PipeClient_Disconnected;

            // Unsubscribe from Lossless Scaling property changes
            if (losslessScalingInstalled != null)
            {
                losslessScalingInstalled.PropertyChanged -= LosslessScalingStatus_PropertyChanged;
            }
            if (losslessScalingRunning != null)
            {
                losslessScalingRunning.PropertyChanged -= LosslessScalingStatus_PropertyChanged;
            }
            Logger.Info("Event handlers unregistered.");

            // Clean up properties (stop debounce timers, unregister slider events)
            Logger.Info("Cleaning up properties...");
            properties.Cleanup();
            Logger.Info("Properties cleaned up.");

            // Clean up widget activity - capture to local var to avoid race condition
            var activity = widgetActivity;
            if (activity != null)
            {
                Logger.Info("Completing widget activity during page unload.");
                try
                {
                    activity.Complete();
                    Logger.Info("Widget activity completed and disposed.");
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error completing widget activity during unload: {ex.Message}");
                }
                finally
                {
                    widgetActivity = null;
                }
            }
            else
            {
                Logger.Info("No widget activity to clean up during unload.");
            }

            // Reset Quick Settings initialized flag so next instance starts fresh
            quickSettingsInitialized = false;

            Logger.Info("GamingWidget_Unloaded completed.");
        }

        public void OnDeactivated()
        {
            Logger.Info($"=== GamingWidget.OnDeactivated START === Instance hash: {this.GetHashCode()}");
            try
            {
                // Must run on UI thread since DispatcherTimer is UI-bound
                _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                {
                    try
                    {
                        properties.StopPendingUpdates();
                        Logger.Info("Pending updates stopped.");
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Error stopping pending updates: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                // Expected when Game Bar has already torn down the old instance — accessing
                // Dispatcher on a detached Page throws RCW separated. Nothing to clean up
                // anyway; the new active instance owns the timers now.
                Logger.Debug($"Deactivation cleanup skipped (instance already detached): {ex.Message}");
            }
        }

        private bool chillFPSHandlersRegistered = false;

    }
}
