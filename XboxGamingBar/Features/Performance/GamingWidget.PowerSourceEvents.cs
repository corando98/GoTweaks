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
        private async void PowerManager_PowerSourceChanged(object sender, object e)
        {
            if (isUnloading) return;

            // Small delay to allow system to update power status
            await System.Threading.Tasks.Task.Delay(100);

            if (isUnloading) return;

            // Update the active profile indicator when power source changes
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (isUnloading) return;

                var batteryStatus = PowerManager.BatteryStatus;
                var powerSupplyStatus = PowerManager.PowerSupplyStatus;

                Logger.Info($"Power source event - Battery: {batteryStatus}, PowerSupply: {powerSupplyStatus}");

                UpdateActiveProfileIndicator();

                // Only reapply TDP after power source change if:
                // 1. On Legion Go in Custom mode (255) - system changes TDP, need to restore
                // 2. Power Source Profile toggle is enabled - user wants different profiles per power state
                // For Legion preset modes (Quiet=1, Balanced=2, Performance=3), let the system handle TDP
                // Skip TDP reapply when DGP is active - DGP controls TDP regardless of power source
                bool isLegionCustomMode = legionGoDetected?.Value == true && legionPerformanceMode?.Value == 255;
                bool powerSourceProfileEnabled = GetPowerSourceProfileEnabledForCurrentContext();
                bool dgpActive = defaultGameProfileEnabled?.Value == true;

                if ((isLegionCustomMode || powerSourceProfileEnabled) && !dgpActive)
                {
                    SchedulePowerSourceTdpReapply();
                }
                else if (dgpActive)
                {
                    Logger.Info("Power source change: Skipping TDP reapply - Default Game Profile is active");
                }
            });
        }

        /// <summary>
        /// Schedules a TDP reapply 5 seconds after power source changes.
        /// This ensures the TDP is properly applied after the system settles.
        /// </summary>
        private void SchedulePowerSourceTdpReapply()
        {
            try
            {
                // Cancel existing timer if any
                if (powerSourceTdpReapplyTimer != null)
                {
                    powerSourceTdpReapplyTimer.Stop();
                }

                // Create and start timer
                powerSourceTdpReapplyTimer = new DispatcherTimer();
                powerSourceTdpReapplyTimer.Interval = TimeSpan.FromSeconds(5);
                powerSourceTdpReapplyTimer.Tick += async (s, args) =>
                {
                    powerSourceTdpReapplyTimer.Stop();

                    // Skip TDP reapply if DGP is active - DGP controls TDP regardless of power source
                    if (defaultGameProfileEnabled?.Value == true)
                    {
                        Logger.Info("Power source change: Skipping TDP reapply - Default Game Profile is active");
                        return;
                    }

                    // Skip TDP reapply if not in Custom mode - preset modes manage TDP automatically
                    if (legionGoDetected?.Value == true && legionPerformanceMode?.Value != 255)
                    {
                        Logger.Info($"Power source change: Skipping TDP reapply - using {GetLegionModeShortName(legionPerformanceMode?.Value ?? 0)} preset mode");
                        return;
                    }

                    // Read TDP value NOW (at timer fire time), not when scheduled
                    // This ensures we use the new profile's TDP after profile switch completes
                    int currentTdpValue = (int)TDPSlider.Value;

                    // Ask the helper to re-push current TDP to hardware via a dedicated pipe
                    // message. The previous N-1/N trick corrupted global.xml by briefly
                    // writing TDP-1 (profile persistence rounded to that transient value
                    // whenever widget re-init / reboot landed during the 100 ms gap).
                    try
                    {
                        if (App.IsConnected)
                        {
                            var request = new Windows.Foundation.Collections.ValueSet();
                            request.Add("ReapplyTDP", true);
                            await App.SendMessageAsync(request);
                            Logger.Info($"Power source change: Asked helper to reapply current TDP ({currentTdpValue}W)");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"Power source change: ReapplyTDP send failed: {ex.Message}");
                    }
                };
                powerSourceTdpReapplyTimer.Start();
                Logger.Info($"Power source change: Scheduled TDP reapply in 5 seconds");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error scheduling power source TDP reapply: {ex.Message}");
            }
        }

    }
}
