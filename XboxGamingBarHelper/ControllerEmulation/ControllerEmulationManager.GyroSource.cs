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

        private bool ConfigureGyroSourceAdapter(string reason)
        {
            if (mode < 0 || mode > 3)
            {
                StopGyroSourceAdapter();
                return true;
            }

            StopGyroSourceAdapter();
            gyroSourceAdapter = BuildGyroSourceAdapter();
            if (gyroSourceAdapter == null)
            {
                Logger.Warn("Controller emulation gyro mode requested but no gyro source adapter is available");
                return false;
            }

            if (!gyroSourceAdapter.Start())
            {
                Logger.Warn($"Gyro adapter '{gyroSourceAdapter.Name}' failed to start");
                gyroSourceAdapter.Dispose();
                gyroSourceAdapter = null;

                // Fallback path for Legion: if internal sensor is unavailable, fall back to controller IMU.
                if ((deviceType == SharedDeviceType.LegionGo || deviceType == SharedDeviceType.LegionGo2) && gyroSource == 0)
                {
                    gyroSourceAdapter = new LegionControllerGyroSourceAdapter(false);
                    if (gyroSourceAdapter.Start())
                    {
                        Logger.Info($"Controller emulation motion fallback active ({reason}): {gyroSourceAdapter.Name}");
                        return true;
                    }

                    gyroSourceAdapter.Dispose();
                    gyroSourceAdapter = null;
                }

                return false;
            }

            Logger.Info($"Controller emulation motion source active ({reason}): {gyroSourceAdapter.Name}");
            return true;
        }

        private IGyroSourceAdapter BuildGyroSourceAdapter()
        {
            switch (deviceType)
            {
                case SharedDeviceType.LegionGo:
                case SharedDeviceType.LegionGo2:
                    switch (gyroSource)
                    {
                        case 1: return new LegionControllerGyroSourceAdapter(false);   // Right
                        case 2: return new LegionControllerGyroSourceAdapter(true);    // Left
                        case 3: return new LegionControllerMixedGyroSourceAdapter();   // Mixed
                        // Default ("Internal") reads via LegionButtonMonitor's cached HID
                        // handle, which keeps producing samples even when HidHide hides the
                        // native HID from the rest of the OS. The previous Windows Sensor
                        // adapter went dark whenever HidHide hid the native device, forcing
                        // users to leave it visible (and get double input in Steam/Game Bar)
                        // just to keep gyro working in games.
                        default: return new LegionControllerMixedGyroSourceAdapter();
                    }

                case SharedDeviceType.LegionGoS:
                    // Go S currently uses a different controller HID path; keep gyro source on
                    // Windows sensor stack for stability regardless of dropdown source value.
                    return new WindowsSensorGyroSourceAdapter("Legion Go S Internal Gyro");

                case SharedDeviceType.GPDWin5:
                    // Win5 firmware gyro packet path is still being finalized in this codebase.
                    // Internal Windows sensor path keeps motion mode functional until a native adapter is added.
                    return new WindowsSensorGyroSourceAdapter("GPD Internal Gyro");

                default:
                    return null;
            }
        }

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

        private bool TryReadGyroSample(out GyroSample sample)
        {
            sample = default;
            var adapter = gyroSourceAdapter;
            if (adapter == null)
            {
                return false;
            }

            try
            {
                return adapter.TryGetLatestSample(out sample);
            }
            catch
            {
                return false;
            }
        }

        private bool TryReadDs4TouchSample(out bool isTouching, out bool isPressed, out ushort x, out ushort y)
        {
            isTouching = false;
            isPressed = false;
            x = 0;
            y = 0;

            if (deviceType != SharedDeviceType.LegionGo && deviceType != SharedDeviceType.LegionGo2)
            {
                return false;
            }

            if (!LegionButtonMonitor.TryGetLatestRightTouchpadSample(out LegionTouchpadSample touchSample))
            {
                return false;
            }

            // Legion touch parser normalizes raw touch coordinates to 0..1023 on both axes.
            float normalizedX = Math.Max(0.0f, Math.Min(LegionTouchMaxX, touchSample.RawX)) / LegionTouchMaxX;
            float normalizedY = Math.Max(0.0f, Math.Min(LegionTouchMaxY, touchSample.RawY)) / LegionTouchMaxY;
            x = (ushort)Math.Round(normalizedX * Ds4TouchMaxX);
            y = (ushort)Math.Round(normalizedY * Ds4TouchMaxY);
            isTouching = touchSample.IsTouching;
            isPressed = touchSample.IsPressed;

            if (Logger.IsDebugEnabled && isTouching && touchDiagLogCounter++ % 250 == 0)
            {
                Logger.Debug($"DS4 touch: raw=({touchSample.RawX},{touchSample.RawY}) ds4=({x},{y}) pressed={isPressed}");
            }

            return true;
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

        private static short ConvertSignedRangeToInt16(float value, float maxMagnitude)
        {
            if (maxMagnitude <= 0.0f)
            {
                return 0;
            }

            float clamped = Math.Max(-maxMagnitude, Math.Min(maxMagnitude, value));
            float normalized = clamped / maxMagnitude;
            return (short)Math.Round(normalized * short.MaxValue);
        }

        /// <summary>
        /// Converts gyro value in °/s to DS4 raw format (16 counts per °/s).
        /// </summary>
        private static short ConvertToDs4Gyro(float degPerSecond)
        {
            float raw = degPerSecond * Ds4GyroCountsPerDps;
            return (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, Math.Round(raw)));
        }

        /// <summary>
        /// Converts accelerometer value in G to DS4 raw format (8192 counts per G).
        /// Matches ViiperController's ScaleAccel approach (BMI323 raw × 2).
        /// </summary>
        private static short ConvertToDs4Accel(float valueG)
        {
            float raw = valueG * Ds4AccelCountsPerG;
            return (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, Math.Round(raw)));
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

        private void ApplyRumbleProfile(ref byte largeMotor, ref byte smallMotor)
        {
            switch (rumbleProfile)
            {
                case 1:
                    // Sharp: suppress tiny noise and bias toward distinct punchier pulses.
                    largeMotor = ApplySharpRumbleCurve(largeMotor);
                    smallMotor = ApplySharpRumbleCurve(smallMotor);
                    break;
                case 2:
                    // Soft: keep low/mid detail but cap peak harshness.
                    largeMotor = ApplySoftRumbleCurve(largeMotor);
                    smallMotor = ApplySoftRumbleCurve(smallMotor);
                    break;
                case 3:
                    // Impact: heavily suppress background buzz and preserve strong hit peaks.
                    largeMotor = ApplyImpactRumbleCurve(largeMotor);
                    smallMotor = ApplyImpactRumbleCurve(smallMotor);
                    break;
                case 4:
                    // Boosted: raise low/mid output for weaker motors while keeping a top-end cap.
                    largeMotor = ApplyBoostedRumbleCurve(largeMotor);
                    smallMotor = ApplyBoostedRumbleCurve(smallMotor);
                    break;
                default:
                    break;
            }
        }

        private static byte ApplySharpRumbleCurve(byte value)
        {
            if (value < 18)
            {
                return 0;
            }

            double normalized = (value - 18.0) / (255.0 - 18.0);
            double curved = Math.Pow(normalized, 1.45);
            int mapped = (int)Math.Round(curved * 255.0);
            if (mapped < 0)
            {
                mapped = 0;
            }
            else if (mapped > 255)
            {
                mapped = 255;
            }

            return (byte)mapped;
        }

        private static byte ApplySoftRumbleCurve(byte value)
        {
            if (value == 0)
            {
                return 0;
            }

            double normalized = value / 255.0;
            double curved = Math.Pow(normalized, 0.85) * 0.82;
            int mapped = (int)Math.Round(curved * 255.0);
            if (mapped < 0)
            {
                mapped = 0;
            }
            else if (mapped > 255)
            {
                mapped = 255;
            }

            return (byte)mapped;
        }

        private static byte ApplyImpactRumbleCurve(byte value)
        {
            if (value < 20)
            {
                return 0;
            }

            double normalized = (value - 20.0) / (255.0 - 20.0);
            double curved = Math.Pow(normalized, 1.60);
            int mapped = (int)Math.Round(curved * 255.0);
            if (mapped < 0)
            {
                mapped = 0;
            }
            else if (mapped > 255)
            {
                mapped = 255;
            }

            return (byte)mapped;
        }

        private static byte ApplyBoostedRumbleCurve(byte value)
        {
            if (value == 0)
            {
                return 0;
            }

            double normalized = value / 255.0;
            double curved = Math.Pow(normalized, 0.62) * 0.95;
            int mapped = (int)Math.Round(curved * 255.0);
            if (mapped < 0)
            {
                mapped = 0;
            }
            else if (mapped > 255)
            {
                mapped = 255;
            }

            return (byte)mapped;
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

        private bool TryForwardLegionRumble(byte largeMotor, byte smallMotor, long nowTicksUtc)
        {
            if (legionManager == null)
            {
                return false;
            }

            if (deviceType != SharedDeviceType.LegionGo &&
                deviceType != SharedDeviceType.LegionGo2 &&
                deviceType != SharedDeviceType.LegionGoS)
            {
                return false;
            }

            int level = MapRumbleToLegionLevel(Math.Max(largeMotor, smallMotor));
            if (level == lastLegionRumbleLevel)
            {
                return true;
            }

            // Legion EC vibration level changes can trigger a noticeable pulse.
            // In fallback mode, avoid rapid non-zero level churn.
            if (level != 0 &&
                lastLegionRumbleLevel > 0 &&
                (nowTicksUtc - lastLegionRumbleSetTicksUtc) < LegionRumbleFallbackMinTicks)
            {
                return true;
            }

            if (!legionManager.TrySetVibration(level))
            {
                return false;
            }
            lastLegionRumbleLevel = level;
            lastLegionRumbleSetTicksUtc = nowTicksUtc;
            return true;
        }

        private static int MapRumbleToLegionLevel(byte magnitude)
        {
            if (magnitude == 0)
            {
                return 0;
            }

            if (magnitude < 86)
            {
                return 1;
            }

            if (magnitude < 171)
            {
                return 2;
            }

            return 3;
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

        private bool TryReadPhysicalControllerState(out XINPUT_STATE selectedState)
        {
            selectedState = default;

            if (ShouldUseLegionHidInputPath() && TryReadLegionHidControllerState(out selectedState))
            {
                forwardingReadOkLegionHid++;
                return true;
            }

            if (xInputGetState == null)
            {
                return false;
            }

            // (ViGEm retirement: no legacy virtual pad exists, so the old
            // virtual-vs-physical XInput index disambiguation is gone.)

            bool hasLockedState = false;
            XINPUT_STATE lockedState = default;
            uint lockedIndex = 0;
            if (physicalXboxUserIndex.HasValue)
            {
                lockedIndex = (uint)physicalXboxUserIndex.Value;
                if (!virtualXboxUserIndex.HasValue || lockedIndex != (uint)virtualXboxUserIndex.Value)
                {
                    if (xInputGetState(lockedIndex, ref lockedState) == ERROR_SUCCESS)
                    {
                        hasLockedState = true;
                    }
                }

                if (!hasLockedState)
                {
                    physicalXboxUserIndex = null;
                }
            }

            bool foundAnyConnected = false;
            XINPUT_STATE firstConnectedState = default;
            uint firstConnectedIndex = 0;
            bool hasFirstConnected = false;
            XINPUT_STATE changedState = default;
            uint changedIndex = 0;
            bool hasChangedState = false;

            for (uint index = 0; index < 4; index++)
            {
                if (virtualXboxUserIndex.HasValue && index == (uint)virtualXboxUserIndex.Value)
                {
                    continue;
                }

                XINPUT_STATE state = default;
                uint result = xInputGetState(index, ref state);
                if (result != ERROR_SUCCESS)
                {
                    continue;
                }

                foundAnyConnected = true;
                if (!hasFirstConnected)
                {
                    firstConnectedState = state;
                    firstConnectedIndex = index;
                    hasFirstConnected = true;
                }

                bool packetChanged = state.dwPacketNumber != lastPacketByController[index];
                if (!packetChanged)
                {
                    continue;
                }

                // Prefer states that changed since last sample.
                // If a locked index is active, keep it when it changes.
                if (hasLockedState && index == lockedIndex)
                {
                    changedState = state;
                    changedIndex = index;
                    hasChangedState = true;
                    break;
                }

                // Otherwise keep the first changed candidate and continue scanning in case
                // the locked index also changed in this cycle.
                if (!hasChangedState)
                {
                    changedState = state;
                    changedIndex = index;
                    hasChangedState = true;
                }
            }

            if (hasChangedState)
            {
                lastPacketByController[changedIndex] = changedState.dwPacketNumber;
                if (!physicalXboxUserIndex.HasValue || physicalXboxUserIndex.Value != (int)changedIndex)
                {
                    Logger.Debug($"Controller emulation physical source switched to XInput index {changedIndex}");
                }

                physicalXboxUserIndex = (int)changedIndex;
                selectedState = changedState;
                forwardingReadOkXInput++;
                return true;
            }

            if (hasLockedState)
            {
                // Keep using the locked index when nothing else changed this poll.
                lastPacketByController[lockedIndex] = lockedState.dwPacketNumber;
                physicalXboxUserIndex = (int)lockedIndex;
                selectedState = lockedState;
                forwardingReadOkXInput++;
                return true;
            }

            if (foundAnyConnected && hasFirstConnected)
            {
                lastPacketByController[firstConnectedIndex] = firstConnectedState.dwPacketNumber;
                physicalXboxUserIndex = (int)firstConnectedIndex;
                selectedState = firstConnectedState;
                forwardingReadOkXInput++;
                return true;
            }

            return false;
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

        private bool TryReadLegionHidControllerState(out XINPUT_STATE state)
        {
            state = default;
            if (!LegionButtonMonitor.TryGetLatestGamepadSample(out LegionGamepadSample sample))
            {
                return false;
            }

            long sampleTimestamp = sample.TimestampTicksUtc;
            if (sampleTimestamp <= 0)
            {
                return false;
            }

            long nowUtc = DateTime.UtcNow.Ticks;
            if (nowUtc - sampleTimestamp > LegionHidSampleMaxAgeTicks)
            {
                return false;
            }

            if (sampleTimestamp != lastLegionHidSampleTimestampTicksUtc)
            {
                legionHidPacketNumber++;
                lastLegionHidSampleTimestampTicksUtc = sampleTimestamp;
            }

            state.dwPacketNumber = legionHidPacketNumber;
            state.Gamepad.wButtons = sample.Buttons;
            state.Gamepad.bLeftTrigger = sample.LeftTrigger;
            state.Gamepad.bRightTrigger = sample.RightTrigger;
            state.Gamepad.sThumbLX = sample.LeftStickX;
            state.Gamepad.sThumbLY = sample.LeftStickY;
            state.Gamepad.sThumbRX = sample.RightStickX;
            state.Gamepad.sThumbRY = sample.RightStickY;

            // FR-1: cache aux buttons so IsGyroActivationButtonPressed can resolve
            // the M1/M2/M3/Y1/Y2/Y3 selector indices (these aren't in XINPUT_GAMEPAD).
            lastLegionAuxButtons = sample.AuxButtons;

            ApplyLegionUserspaceRemaps(sample, ref state);
            return true;
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
