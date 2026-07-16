using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Shared.Data;
using Shared.Enums;
using XboxGamingBarHelper.Core;
using XboxGamingBarHelper.Devices;
using XboxGamingBarHelper.Devices.Libraries.GPD;
using XboxGamingBarHelper.Devices.Libraries.Legion;
using XboxGamingBarHelper.Labs;
using XboxGamingBarHelper.Settings;
using XboxGamingBarHelper.Windows;
using SharedDeviceType = Shared.Enums.DeviceType;

namespace XboxGamingBarHelper.ControllerEmulation
{
    internal partial class ControllerEmulationManager
    {

        private void StopGyroSourceAdapter()
        {
            if (gyroSourceAdapter == null)
            {
                return;
            }

            try
            {
                gyroSourceAdapter.Stop();
                gyroSourceAdapter.Dispose();
            }
            catch
            {
                // Ignore shutdown exceptions.
            }

            gyroSourceAdapter = null;
        }

        private bool EnableSuppression(string reason)
        {
            if (suppressionManager == null)
            {
                return false;
            }

            // VIIPER owns the HidHide hide list whenever it's the active backend.
            // The legacy deferred startup apply can land AFTER VIIPER's own
            // Enable(hideMode=1) but BEFORE SetSuppressedByViiper(true) flips the
            // suppressedByViiper flag, leaving a window where legacy would call
            // Enable(hideTarget=user_setting) — which is 0 ("hide ViGEm only") for
            // anyone who configured it that way, wiping VIIPER's hideMode=1 and
            // re-exposing the native Legion HID to Windows.Gaming.Input. Gate on
            // the persisted backend setting itself, which is authoritative the
            // moment the manager is constructed, regardless of dispatcher ordering.
            if (settingsManager?.EmulationBackend?.Value == true)
            {
                Logger.Info($"Legacy EnableSuppression skipped ({reason}); VIIPER backend owns HidHide");
                return suppressionActive;
            }

            suppressionPausedForGameBar = false;
            suppressionPauseUntilTicksUtc = 0;
            bool wasActive = suppressionActive;
            IReadOnlyCollection<string> excludedIds = virtualXboxBridgeDeviceIds.Count > 0
                ? virtualXboxBridgeDeviceIds
                : null;
            suppressionActive = suppressionManager.Enable(deviceType, hideTarget, excludedIds);
            if (suppressionActive)
            {
                Logger.Info($"Controller suppression {(wasActive ? "updated" : "enabled")} ({reason}, target={hideTarget})");
            }
            else if (wasActive)
            {
                Logger.Info($"Controller suppression cleared ({reason}, target={hideTarget})");
            }

            return suppressionActive;
        }

        private bool ShouldManageSuppression()
        {
            // Improved Legion HID input keeps physical input flowing even while Game Bar/FSE
            // blocks XInput reads, so we should keep stock controller cloaked continuously.
            if (ShouldUseLegionHidInputPath())
            {
                return false;
            }

            return isSupported &&
                enabled &&
                hideStockController &&
                RequiresSoftwareForwarding(mode) &&
                RequiresVirtualGamepad(mode);
        }

        private static bool IsForegroundXboxGameBarProcess()
        {
            int foregroundProcessId = User32.GetForegroundProcessId();
            if (foregroundProcessId <= 0)
            {
                return false;
            }

            try
            {
                using Process process = Process.GetProcessById(foregroundProcessId);
                string processName = process.ProcessName;
                if (GameBarForegroundProcessNames.Contains(processName))
                {
                    return true;
                }

                if (processName.IndexOf("XboxGamingBar", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    processName.IndexOf("XboxGameBar", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }

                try
                {
                    string processPath = process.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(processPath) &&
                        (processPath.IndexOf("XboxGamingOverlay", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         processPath.IndexOf("XboxGamingBar", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         processPath.IndexOf("XboxGameBar", StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        return true;
                    }
                }
                catch
                {
                    // Access to MainModule may fail for protected processes; process name check is sufficient.
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        private bool IsWidgetForegroundSignalActive()
        {
            return hasWidgetForegroundSignal && widgetForegroundSignal;
        }

        private bool TryPauseSuppressionForForegroundGameBar(string reason)
        {
            if (!ShouldManageSuppression())
            {
                return false;
            }

            bool isForegroundGameBar = IsWidgetForegroundSignalActive() || IsForegroundXboxGameBarProcess();
            bool isGuidePauseActive = suppressionPauseUntilTicksUtc > DateTime.UtcNow.Ticks;
            if (!isForegroundGameBar && !isGuidePauseActive)
            {
                return false;
            }

            bool wasPaused = suppressionPausedForGameBar;
            suppressionPausedForGameBar = true;
            DisableSuppression();

            if (!wasPaused)
            {
                if (isForegroundGameBar)
                {
                    Logger.Info($"Controller suppression temporarily disabled while Xbox Game Bar is foreground ({reason})");
                }
                else
                {
                    Logger.Info($"Controller suppression temporarily disabled after guide press ({reason})");
                }
            }

            return true;
        }

        private void MonitorGameBarSuppressionState()
        {
            if (!ShouldManageSuppression())
            {
                gameBarForegroundConsecutiveTicks = 0;
                nonGameBarForegroundConsecutiveTicks = 0;
                suppressionPausedForGameBar = false;
                suppressionPauseUntilTicksUtc = 0;
                return;
            }

            if (IsWidgetForegroundSignalActive() || IsForegroundXboxGameBarProcess())
            {
                gameBarForegroundConsecutiveTicks++;
                nonGameBarForegroundConsecutiveTicks = 0;

                if (!suppressionPausedForGameBar &&
                    gameBarForegroundConsecutiveTicks >= GameBarForegroundStableTicks)
                {
                    suppressionPausedForGameBar = true;
                    DisableSuppression();
                    Logger.Info("Controller suppression temporarily disabled while Xbox Game Bar is foreground");
                }

                return;
            }

            if (suppressionPauseUntilTicksUtc > DateTime.UtcNow.Ticks)
            {
                gameBarForegroundConsecutiveTicks = 0;
                nonGameBarForegroundConsecutiveTicks = 0;

                if (!suppressionPausedForGameBar)
                {
                    suppressionPausedForGameBar = true;
                    DisableSuppression();
                    Logger.Info("Controller suppression temporarily disabled due to active guide pause");
                }

                return;
            }

            nonGameBarForegroundConsecutiveTicks++;
            gameBarForegroundConsecutiveTicks = 0;

            if (suppressionPausedForGameBar &&
                nonGameBarForegroundConsecutiveTicks >= GameBarBackgroundStableTicks)
            {
                suppressionPausedForGameBar = false;
                Logger.Info("Xbox Game Bar no longer foreground; restoring controller suppression");
                ApplySuppressionConfiguration("game bar no longer foreground");
            }
        }

        private void SubscribeForegroundSignal()
        {
            try
            {
                if (settingsManager != null && settingsManager.IsForeground != null)
                {
                    settingsManager.IsForeground.PropertyChanged += OnWidgetForegroundPropertyChanged;
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"Controller emulation failed to subscribe to widget foreground signal: {ex.Message}");
            }
        }

        private void UnsubscribeForegroundSignal()
        {
            try
            {
                if (settingsManager != null && settingsManager.IsForeground != null)
                {
                    settingsManager.IsForeground.PropertyChanged -= OnWidgetForegroundPropertyChanged;
                }
            }
            catch
            {
                // Best effort only.
            }
        }

        private void OnWidgetForegroundPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (!string.IsNullOrEmpty(e?.PropertyName) &&
                !string.Equals(e.PropertyName, "value", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!(sender is Settings.IsForegroundProperty foregroundProperty))
            {
                return;
            }

            hasWidgetForegroundSignal = true;
            widgetForegroundSignal = foregroundProperty.Value;
            Logger.Info($"Controller emulation Game Bar foreground signal: {widgetForegroundSignal}");

            if (!ShouldManageSuppression())
            {
                return;
            }

            if (widgetForegroundSignal)
            {
                TryPauseSuppressionForForegroundGameBar("widget foreground signal");
                return;
            }

            if (suppressionPausedForGameBar && suppressionPauseUntilTicksUtc <= DateTime.UtcNow.Ticks)
            {
                ApplySuppressionConfiguration("widget background signal");
            }
        }

        private void UpdateVirtualXboxBridgeDeviceIds(IReadOnlyCollection<string> bridgeIdsBeforeVirtualConnect)
        {
            virtualXboxBridgeDeviceIds.Clear();

            var before = bridgeIdsBeforeVirtualConnect == null
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(bridgeIdsBeforeVirtualConnect, StringComparer.OrdinalIgnoreCase);

            IReadOnlyCollection<string> after = ControllerSuppressionManager.QueryXboxBridgeDeviceIds();
            foreach (string id in after)
            {
                if (!before.Contains(id))
                {
                    virtualXboxBridgeDeviceIds.Add(id);
                }
            }

            Logger.Info($"Controller emulation tracked virtual Xbox bridge HID instance count: {virtualXboxBridgeDeviceIds.Count}");
        }

        private void DisableSuppression()
        {
            if (suppressionManager == null)
            {
                return;
            }

            bool shouldForceCleanup =
                !hideStockController ||
                !enabled ||
                !RequiresSoftwareForwarding(mode) ||
                !RequiresVirtualGamepad(mode);
            if (!suppressionActive && !shouldForceCleanup)
            {
                return;
            }

            bool wasActive = suppressionActive;
            suppressionManager.Disable();
            suppressionActive = false;
            if (wasActive)
            {
                Logger.Info("Controller suppression disabled");
            }
        }

        // ViGEm retirement: no virtual pad exists on the legacy manager. The
        // callers of these helpers are themselves unreachable (forwarding loop
        // deleted), but they still compile — constant null/false keeps them
        // harmless until the remaining dead callers are swept.
        private int? TryGetVirtualXboxUserIndexSafe()
        {
            return null;
        }


        private void RevertLegionLed()
        {
            hasForwardedLed = false;
            if (deviceType != SharedDeviceType.LegionGo && deviceType != SharedDeviceType.LegionGo2)
            {
                return;
            }

            try
            {
                // Restore the user's configured LED settings via the Legion manager.
                legionManager?.RestoreLightSettings();
            }
            catch (Exception ex)
            {
                Logger.Debug($"Controller emulation LED revert failed: {ex.Message}");
            }
        }

        private bool TryForwardPhysicalXInputRumble(byte largeMotor, byte smallMotor)
        {
            if (!EnsureXInputLoaded() || xInputSetState == null)
            {
                return false;
            }

            int? targetIndex = physicalXboxUserIndex;
            if (!targetIndex.HasValue)
            {
                if (mode == 1 && !virtualXboxUserIndex.HasValue)
                {
                    // Try lazy resolution — ViGEm may not have reported the index at startup.
                    virtualXboxUserIndex = TryGetVirtualXboxUserIndexSafe();

                    if (!virtualXboxUserIndex.HasValue && !ShouldUseLegionHidInputPath())
                    {
                        // XInput input path: avoid sending to virtual slot while unresolved.
                        return false;
                    }
                    // Legion HID input path: no XInput feedback loop risk, safe to proceed.
                }

                targetIndex = DiscoverPreferredPhysicalXboxIndex(virtualXboxUserIndex);
                if (targetIndex.HasValue)
                {
                    physicalXboxUserIndex = targetIndex;
                }
            }

            if (!targetIndex.HasValue)
            {
                return false;
            }

            if (virtualXboxUserIndex.HasValue && targetIndex.Value == virtualXboxUserIndex.Value)
            {
                return false;
            }

            var vibration = new XINPUT_VIBRATION
            {
                wLeftMotorSpeed = (ushort)(largeMotor * 257),
                wRightMotorSpeed = (ushort)(smallMotor * 257),
            };

            uint result;
            try
            {
                result = xInputSetState((uint)targetIndex.Value, ref vibration);
            }
            catch
            {
                xInputSetState = null;
                return false;
            }

            if (result == ERROR_SUCCESS)
            {
                return true;
            }

            physicalXboxUserIndex = null;
            return false;
        }

        private void StopForwardedRumble()
        {
            lock (rumbleSync)
            {
                lastRumbleLargeMotor = 0;
                lastRumbleSmallMotor = 0;
                lastRumbleDispatchTicksUtc = 0;
            }

            lastLegionRumbleLevel = -1;
            lastLegionRumbleSetTicksUtc = 0;

            TryForwardPhysicalXInputRumble(0, 0);
        }

        private int? DiscoverPreferredPhysicalXboxIndex(int? excludedIndex)
        {
            if (xInputGetState == null)
            {
                return null;
            }

            for (uint index = 0; index < 4; index++)
            {
                if (excludedIndex.HasValue && index == (uint)excludedIndex.Value)
                {
                    continue;
                }

                XINPUT_STATE state = default;
                if (xInputGetState(index, ref state) == ERROR_SUCCESS)
                {
                    lastPacketByController[index] = state.dwPacketNumber;
                    return (int)index;
                }
            }

            return null;
        }

        private bool ShouldUseLegionHidInputPath()
        {
            if (!improvedInputRead)
            {
                return false;
            }

            return deviceType == SharedDeviceType.LegionGo ||
                   deviceType == SharedDeviceType.LegionGo2 ||
                   deviceType == SharedDeviceType.LegionGoS;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                StopForwarding();
                suppressionManager?.Dispose();
                UnsubscribeForegroundSignal();
                if (ReferenceEquals(activeInstance, this))
                {
                    activeInstance = null;
                }
            }

            base.Dispose(disposing);
        }

    }
}
