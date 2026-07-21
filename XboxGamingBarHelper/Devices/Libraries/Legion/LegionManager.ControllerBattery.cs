using NLog;
using Shared.Data;
using Shared.Enums;
using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using XboxGamingBarHelper.Core;
using XboxGamingBarHelper.Devices;
using XboxGamingBarHelper.Devices.Libraries.Legion;
using XboxGamingBarHelper.Performance;

namespace XboxGamingBarHelper.Devices.Libraries.Legion
{
    internal partial class LegionManager
    {
        // Periodic poll for b0:01 device status. Legion Space polls on demand when its
        // UI is open; we just refresh every few seconds so the Info card stays current
        // without burning HID bandwidth.
        private Timer _deviceStatusPollTimer;
        private const int DeviceStatusPollIntervalMs = 5000;

        /// <summary>
        /// Starts battery monitoring for the controllers using the existing controllerService connection.
        /// </summary>
        private void StartBatteryMonitoring()
        {
            try
            {
                if (controllerService != null && isControllerConnected)
                {
                    controllerService.BatteryUpdated += OnControllerBatteryUpdated;
                    controllerService.DeviceStatusUpdated += OnControllerDeviceStatusUpdated;
                    controllerService.StickLightDriftDetected += OnStickLightDriftDetected;
                    controllerService.StartBatteryMonitoring();
                    StartDeviceStatusPolling();
                    Logger.Info("Controller battery monitoring started");
                }
                else
                {
                    Logger.Info("Controller not connected, battery monitoring not started");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error starting battery monitoring: {ex.Message}");
            }
        }

        /// <summary>
        /// Stops battery monitoring.
        /// </summary>
        private void StopBatteryMonitoring()
        {
            if (controllerService != null)
            {
                try
                {
                    StopDeviceStatusPolling();
                    controllerService.BatteryUpdated -= OnControllerBatteryUpdated;
                    controllerService.DeviceStatusUpdated -= OnControllerDeviceStatusUpdated;
                    controllerService.StickLightDriftDetected -= OnStickLightDriftDetected;
                    controllerService.StopBatteryMonitoring();
                }
                catch { }
                Logger.Info("Controller battery monitoring stopped");
            }
        }

        private void StartDeviceStatusPolling()
        {
            try
            {
                _deviceStatusPollTimer?.Dispose();
                _deviceStatusPollTimer = new Timer(_ => PollDeviceStatus(), null, 250, DeviceStatusPollIntervalMs);
            }
            catch (Exception ex)
            {
                Logger.Warn($"Failed to start device status polling: {ex.Message}");
            }
        }

        private void StopDeviceStatusPolling()
        {
            try
            {
                _deviceStatusPollTimer?.Dispose();
                _deviceStatusPollTimer = null;
            }
            catch { }
        }

        private void PollDeviceStatus()
        {
            try
            {
                if (controllerService == null || !isControllerConnected) return;
                var result = controllerService.ReadDeviceStatus();
                if (result == null)
                {
                    Logger.Info("PollDeviceStatus: no b0:01 response (timeout or monitor inactive)");
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"PollDeviceStatus error: {ex.Message}");
            }
        }

        // One-shot timer for an out-of-band status re-read after a light/touchpad change, so the
        // Controller Info card reflects true hardware state without waiting up to 5s for the
        // periodic poll. Fires after a short settle delay (the firmware needs a moment to apply
        // the write before b0:01 reports the new mode).
        private Timer _statusRefreshTimer;
        private const int StatusRefreshDelayMs = 300;

        /// <summary>
        /// Schedule a single b0:01 re-read shortly after a controller state change (light mode,
        /// color, touchpad) so the Info card shows the real hardware state promptly.
        /// </summary>
        public void RequestDeviceStatusRefresh()
        {
            try
            {
                if (controllerService == null || !isControllerConnected) return;
                _statusRefreshTimer?.Dispose();
                _statusRefreshTimer = new Timer(_ => PollDeviceStatus(), null, StatusRefreshDelayMs, Timeout.Infinite);
            }
            catch (Exception ex)
            {
                Logger.Debug($"RequestDeviceStatusRefresh error: {ex.Message}");
            }
        }

        /// <summary>
        /// Handler for stick-light drift events. Logs WARN on a new mismatch and INFO
        /// on recovery. Source distinguishes the debounced post-write verifier from
        /// the passive 5s poll re-check.
        /// </summary>
        private void OnStickLightDriftDetected(object sender, StickLightDriftEventArgs e)
        {
            try
            {
                if (e.IsMismatch)
                    Logger.Warn($"Stick light drift ({e.Source}): {e.Description}");
                else
                    Logger.Info($"Stick light matches expectation ({e.Source})");
            }
            catch { }
        }

        /// <summary>
        /// Handler for b0:01 device status responses. Serializes the snapshot to JSON
        /// and ships it to the widget via the ControllerDeviceStatus property.
        /// </summary>
        /// <summary>
        /// Feed a device-status snapshot parsed elsewhere (e.g. by LegionButtonMonitor, which
        /// owns the HID handle on devices where LegionControllerService can't run its own b0:01
        /// read loop) into the same Info-card pipeline used by the controllerService path.
        /// </summary>
        public void IngestDeviceStatus(LegionGoStatus status)
        {
            _lastButtonMonitorStatusUtc = DateTime.UtcNow;
            OnControllerDeviceStatusUpdated(this, status);
        }

        // Last time the button monitor fed a b0:01 status (it owns the HID handle on
        // Legion Go 2, so its data is authoritative there).
        private DateTime _lastButtonMonitorStatusUtc = DateTime.MinValue;

        private void OnControllerDeviceStatusUpdated(object sender, LegionGoStatus status)
        {
            if (status == null) return;

            // While the button monitor is actively ingesting, drop status events from the
            // controllerService's own read loop: on Go 2 its reads return default values
            // (le=true, br=100, sp=50, no byte-6 flag knowledge), and the two sources
            // alternating made the widget's light state flap on/off (observed 2026-07-14).
            if (!ReferenceEquals(sender, this) &&
                (DateTime.UtcNow - _lastButtonMonitorStatusUtc).TotalSeconds < 30)
            {
                Logger.Debug("Ignoring controllerService device-status while button monitor is active");
                return;
            }

            try
            {
                // The firmware's "off" sentinel (LightModeRaw == 0xFF) isn't always reliably
                // reported after a successful SetRgbEnabled(false) - it sometimes still reports
                // the last active mode byte even though the light is genuinely off, which made
                // the Controller Information table flicker between "Off" and the previous
                // mode/color every few seconds as passive polls came in (confirmed via the
                // "Stick light drift" log). Prefer our own last successfully-written enabled
                // state over a readback that disagrees with it; fall back to the raw readback
                // when nothing has been written yet this session (e.g. right after a cold
                // helper start, where the block below deliberately wants the true hardware
                // state to avoid a default Solid/white flash - issue #81).
                bool? expectedEnabled = controllerService?.ExpectedLightEnabled;
                bool lightEnabled = expectedEnabled ?? status.LightEnabled;
                if (expectedEnabled.HasValue && expectedEnabled.Value != status.LightEnabled)
                {
                    Logger.Debug($"b0:01 readback disagrees with last-written state (expected={expectedEnabled.Value}, got={status.LightEnabled}) - trusting last-written state for display");
                }

                // Cache the true hardware light brightness/color/speed from the readback so the
                // reactive-lighting loop matches real state instead of the helper's stale defaults
                // (brightness defaults to 100 after a restart, which made reactive effects blast
                // full brightness until the widget reconnected — the "brightness peaked" bug).
                if (lightEnabled)
                {
                    _hwLightBrightness = status.Brightness;
                    _hwLightColorR = status.Red; _hwLightColorG = status.Green; _hwLightColorB = status.Blue;
                    _hasHwLightState = true;
                }

                // A b0:01 readback is a real source of truth for the light state even when the light
                // is off — record it so RestoreLightSettings can apply the true mode (including Off)
                // instead of the default Solid/white (#81 white stick flash on cold helper start).
                // Map the firmware animation byte (Solid=0,Pulse=1,Dynamic=2,Spiral=3) to our internal
                // convention (0=Off,1=Solid,2=Pulse,3=Dynamic,4=Spiral); off when the light is disabled.
                _hwLightMode = !lightEnabled ? 0
                    : status.LightModeRaw == 0 ? 1
                    : status.LightModeRaw == 1 ? 2
                    : status.LightModeRaw == 2 ? 3
                    : status.LightModeRaw == 3 ? 4
                    : 1;
                _lightStateKnown = true;

                // Stable; demoted to Debug. The b0:01 service emits one event per
                // data slot, which on Legion Go 2 fired ~5x per 5s polling cycle
                // and dominated user logs. Re-promote to Info during triage if
                // we need to compare hardware state vs. widget state again.
                Logger.Debug(
                    $"b0:01 readback: fw={status.FirmwareVersion} lightEnabled={lightEnabled} " +
                    $"mode={status.LightModeRaw} R={status.Red} G={status.Green} B={status.Blue} " +
                    $"brightness={status.Brightness} speed={status.Speed} " +
                    $"vibration={status.VibrationRaw} touchpad={status.TouchpadEnabled} " +
                    $"battery L={status.LeftBattery} R={status.RightBattery}");

                string json = JsonSerializer.Serialize(new
                {
                    fw = status.FirmwareVersion,
                    le = lightEnabled,
                    lm = status.LightModeRaw,
                    r = status.Red,
                    g = status.Green,
                    b = status.Blue,
                    br = status.Brightness,
                    sp = status.Speed,
                    vb = status.VibrationRaw,
                    tp = status.TouchpadEnabled,
                    bl = status.LeftBattery,
                    brt = status.RightBattery,
                });
                ControllerDeviceStatus.SetValueAndSync(json);

                // Reconcile the LegionTouchpadEnabled property with the hardware
                // state reported by the readback. Touchpad state is firmware-side
                // persistent — it survives helper restarts — so when the property
                // disagrees with hardware (typically after a fresh helper boot
                // before LocalSettings has caught up, or when LegionSpace / OS
                // gestures toggle it behind our back), trust the hardware. Use
                // SuppressHardwareApply so the resulting widget pipe sync doesn't
                // round-trip back to a redundant SetTouchpadEnabled call.
                if (LegionTouchpadEnabled != null && LegionTouchpadEnabled.Value != status.TouchpadEnabled)
                {
                    Logger.Info($"LegionTouchpadEnabled hardware reconcile: property={LegionTouchpadEnabled.Value} → readback={status.TouchpadEnabled}");
                    touchpadEnabled = status.TouchpadEnabled;
                    try { Settings.LocalSettingsHelper.SetValue("LegionTouchpadEnabled", status.TouchpadEnabled); }
                    catch (Exception persistEx) { Logger.Debug($"Failed to persist LegionTouchpadEnabled during reconcile: {persistEx.Message}"); }
                    LegionTouchpadEnabled.SuppressHardwareApply = true;
                    try { LegionTouchpadEnabled.SetValueAndSync(status.TouchpadEnabled); }
                    finally { LegionTouchpadEnabled.SuppressHardwareApply = false; }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Failed to sync device status to widget: {ex.Message}");
            }
        }

        /// <summary>
        /// Re-pushes the cached controller battery/charging/connection state to the widget.
        /// Called on every widget pipe connect: the button monitor only pushes on CHANGE, so
        /// values set while no widget was connected (typical at boot — helper starts first,
        /// battery pins at 100% docked and never changes again) would otherwise never reach
        /// a widget that connects later, leaving it on its defaults ("--" / Detached).
        /// ForceSetValue bypasses the equality-skip so the push always goes out.
        /// </summary>
        public void ResyncControllerStatusToWidget()
        {
            try
            {
                ControllerBatteryLeft.ForceSetValue(leftControllerBattery);
                ControllerBatteryRight.ForceSetValue(rightControllerBattery);
                ControllerChargingLeft.ForceSetValue(leftControllerCharging);
                ControllerChargingRight.ForceSetValue(rightControllerCharging);
                ControllerConnectedLeft.ForceSetValue(leftControllerConnected);
                ControllerConnectedRight.ForceSetValue(rightControllerConnected);
                ControllerDockedLeft.ForceSetValue(leftControllerDocked);
                ControllerDockedRight.ForceSetValue(rightControllerDocked);
                Logger.Info($"Controller status resynced to widget: L={leftControllerBattery}% (conn={leftControllerConnected}) R={rightControllerBattery}% (conn={rightControllerConnected})");
            }
            catch (Exception ex)
            {
                Logger.Warn($"ResyncControllerStatusToWidget failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Public method to start battery monitoring if controller is connected.
        /// Called from Program.cs after widget connection is established.
        /// </summary>
        public void StartBatteryMonitoringIfConnected()
        {
            if (isControllerConnected)
            {
                StartBatteryMonitoring();
            }
        }

        /// <summary>
        /// Handler for battery updates from the controller.
        /// </summary>
        private void OnControllerBatteryUpdated(object sender, ControllerServiceBatteryEventArgs e)
        {
            Logger.Debug($"Battery update received: L={e.LeftBattery}% ({(e.LeftCharging ? "charging" : "discharging")}), R={e.RightBattery}% ({(e.RightCharging ? "charging" : "discharging")})");

            // Update cached values
            leftControllerBattery = e.LeftBattery;
            rightControllerBattery = e.RightBattery;
            leftControllerCharging = e.LeftCharging;
            rightControllerCharging = e.RightCharging;

            // Update properties and sync to widget
            // Wrap in try/catch because SyncToRemote is async void and can crash the process
            // if the AppService connection is broken (e.g., widget closed or stale connection)
            try
            {
                ControllerBatteryLeft.SetValueAndSync(e.LeftBattery);
                ControllerBatteryRight.SetValueAndSync(e.RightBattery);
                ControllerChargingLeft.SetValueAndSync(e.LeftCharging);
                ControllerChargingRight.SetValueAndSync(e.RightCharging);
            }
            catch (Exception ex)
            {
                Logger.Warn($"Failed to sync battery status to widget: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets the left controller battery percentage (1-100), or -1 if unavailable.
        /// </summary>
        public int GetLeftControllerBattery() => leftControllerBattery;

        /// <summary>
        /// Gets the right controller battery percentage (1-100), or -1 if unavailable.
        /// </summary>
        public int GetRightControllerBattery() => rightControllerBattery;

        /// <summary>
        /// Gets whether the left controller is charging.
        /// </summary>
        public bool IsLeftControllerCharging() => leftControllerCharging;

        /// <summary>
        /// Gets whether the right controller is charging.
        /// </summary>
        public bool IsRightControllerCharging() => rightControllerCharging;

        /// <summary>
        /// Charge percentage (0-100) for the battery-indicator lighting effect. Uses the lower
        /// controller battery normally, but falls back to the SYSTEM battery when the controllers
        /// are charging/attached (in that state the controllers report ~100/charging, which isn't
        /// a useful indicator). Returns 100 if no battery info is available.
        /// </summary>
        /// <summary>
        /// True when either controller half is currently DETACHED (wireless link to the
        /// receiver instead of docked USB). Read by the HidHide suppression manager to
        /// skip its post-cloak-change device restart: port-cycling the receiver while a
        /// half is wireless drops the radio link and hard-disconnects (powers off) the
        /// pad — field report "enabling/disabling controller emu turns off the detached
        /// left controller". Based on the DOCKED flags (conn code 0x02), not the linked
        /// flags: a detached half is linked (streams input/battery, so the UI keeps its
        /// details) but not docked (port cycle would kill it). A half that is fully off
        /// is neither, and conservatively counts as detached here.
        /// </summary>
        public bool AnyControllerDetached => !leftControllerDocked || !rightControllerDocked;

        private bool leftControllerDocked = true;
        private bool rightControllerDocked = true;

        public int GetIndicatorBatteryPercent()
        {
            bool controllersCharging = leftControllerCharging || rightControllerCharging;
            if (!controllersCharging)
            {
                int l = leftControllerBattery, r = rightControllerBattery;
                int lo = Math.Min(l < 0 ? 100 : l, r < 0 ? 100 : r);
                if (l > 0 || r > 0) return Math.Max(0, Math.Min(100, lo));
            }

            // Controllers charging/attached, or no controller battery → system battery.
            try
            {
                int pct = global::Windows.System.Power.PowerManager.RemainingChargePercent;
                if (pct >= 0) return Math.Max(0, Math.Min(100, pct));
            }
            catch { }
            return 100;
        }

        /// <summary>
        /// Updates controller battery values from the LegionButtonMonitor.
        /// This is used when the button monitor is active and reading HID reports
        /// that contain battery data (same interface as button data).
        /// </summary>
        public void UpdateControllerBatteryFromButtonMonitor(int leftBattery, bool leftCharging, bool leftConnected,
                                                              int rightBattery, bool rightCharging, bool rightConnected,
                                                              bool leftDocked, bool rightDocked)
        {
            try
            {
                bool batteryChanged = leftControllerBattery != leftBattery ||
                                     rightControllerBattery != rightBattery ||
                                     leftControllerCharging != leftCharging ||
                                     rightControllerCharging != rightCharging;

                bool connectionChanged = leftControllerConnected != leftConnected ||
                                        rightControllerConnected != rightConnected;
                bool anyHalfReconnected = (!leftControllerConnected && leftConnected) ||
                                          (!rightControllerConnected && rightConnected);
                bool dockStateChanged = leftControllerDocked != leftDocked ||
                                        rightControllerDocked != rightDocked;

                leftControllerBattery = leftBattery;
                leftControllerCharging = leftCharging;
                leftControllerConnected = leftConnected;
                rightControllerBattery = rightBattery;
                rightControllerCharging = rightCharging;
                rightControllerConnected = rightConnected;
                leftControllerDocked = leftDocked;
                rightControllerDocked = rightDocked;

                if (batteryChanged)
                {
                    Logger.Info($"Controller battery from button monitor: L={leftBattery}% R={rightBattery}%");
                    try
                    {
                        ControllerBatteryLeft.SetValueAndSync(leftBattery);
                        ControllerBatteryRight.SetValueAndSync(rightBattery);
                        ControllerChargingLeft.SetValueAndSync(leftCharging);
                        ControllerChargingRight.SetValueAndSync(rightCharging);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"Failed to sync battery status from button monitor: {ex.Message}");
                    }
                }

                if (dockStateChanged)
                {
                    Logger.Info($"Controller dock state changed: L={(leftDocked ? "docked" : "detached")} R={(rightDocked ? "docked" : "detached")} (AnyControllerDetached={AnyControllerDetached})");
                    try
                    {
                        ControllerDockedLeft.SetValueAndSync(leftDocked);
                        ControllerDockedRight.SetValueAndSync(rightDocked);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"Failed to sync dock state from button monitor: {ex.Message}");
                    }
                }

                if (connectionChanged)
                {
                    Logger.Info($"Controller connection changed: L={leftConnected} R={rightConnected}");
                    try
                    {
                        ControllerConnectedLeft.SetValueAndSync(leftConnected);
                        ControllerConnectedRight.SetValueAndSync(rightConnected);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"Failed to sync connection status from button monitor: {ex.Message}");
                    }

                    if (anyHalfReconnected)
                    {
                        // A write sent while the half was offline never reached its
                        // firmware - replay joystick-as-mouse so the physical state
                        // matches the UI. Delayed so the pad finishes re-enumerating.
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(2000);
                            try { ReapplyJoystickAsMouseState(); }
                            catch (Exception ex) { Logger.Warn($"Joystick-as-mouse reapply after reconnect failed: {ex.Message}"); }
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"UpdateControllerBatteryFromButtonMonitor exception: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Updates the controller VID:PID from LegionButtonMonitor.
        /// Called from Program.cs when the button monitor successfully connects.
        /// </summary>
        public void UpdateControllerVidPid(string vidPid)
        {
            try
            {
                if (!string.IsNullOrEmpty(vidPid))
                {
                    // Always update the internal value
                    bool wasEmpty = string.IsNullOrEmpty(ControllerVidPid.Value);
                    bool changed = vidPid != ControllerVidPid.Value;

                    if (changed || wasEmpty)
                    {
                        Logger.Info($"Controller VID:PID set to {vidPid}");
                    }

                    // Always try to sync (the property will handle deduplication)
                    ControllerVidPid.SetValueAndSync(vidPid);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"UpdateControllerVidPid exception: {ex.Message}");
            }
        }

    }
}
