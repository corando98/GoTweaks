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

        private static bool IsSupportedDevice(SharedDeviceType inDeviceType)
        {
            switch (inDeviceType)
            {
                case SharedDeviceType.LegionGo:
                case SharedDeviceType.LegionGo2:
                case SharedDeviceType.LegionGoS:
                case SharedDeviceType.GPDWin5:
                    return true;
                default:
                    return false;
            }
        }

        private static int NormalizeGyroSource(int source)
        {
            return (source >= 0 && source <= 3) ? source : 0;
        }

        private static int NormalizeMode(int inMode)
        {
            if (inMode < 0)
            {
                return 0;
            }

            if (inMode > 3)
            {
                return 3;
            }

            return inMode;
        }

        private static int NormalizeRumbleProfile(int value)
        {
            if (value < 0)
            {
                return 0;
            }

            if (value > 4)
            {
                return 4;
            }

            return value;
        }

        private static int NormalizeHideTarget(int value)
        {
            if (value < 0)
            {
                return 0;
            }

            if (value > 3)
            {
                return 3;
            }

            return value;
        }

        private static int NormalizeGyroActivationMode(int value)
        {
            if (value < 0)
            {
                return 0;
            }

            if (value > 2)
            {
                return 2;
            }

            return value;
        }

        private static int NormalizeGyroActivationButton(int value)
        {
            // Upper bound matches the case range in IsGyroActivationButtonPressed
            // (1..16 = standard XInput buttons, 17..22 = Legion aux paddles M3/M1/M2/Y1/Y2/Y3).
            // Previously clamped at 16, which silently routed every paddle pick to Back (16).
            if (value < 0)
            {
                return 0;
            }

            if (value > 22)
            {
                return 22;
            }

            return value;
        }

        private static int NormalizeDs4Orientation(int value)
        {
            return value == 1 ? 1 : 0;
        }

        private static int NormalizeMouseSensitivity(int value)
        {
            if (value < 1)
            {
                return 1;
            }

            if (value > 400)
            {
                return 400;
            }

            return value;
        }

        private static int NormalizeMouseThreshold(int value)
        {
            if (value < 0)
            {
                return 0;
            }

            if (value > 20)
            {
                return 20;
            }

            return value;
        }

        private static int NormalizeMouseAxis(int value)
        {
            if (value < 0)
            {
                return 0;
            }

            if (value > 2)
            {
                return 2;
            }

            return value;
        }

        private static int NormalizeMouseGain(int value)
        {
            if (value < 25)
            {
                return 25;
            }

            if (value > 400)
            {
                return 400;
            }

            return value;
        }

        private static int NormalizeStickSensitivity(int value)
        {
            if (value < 1)
            {
                return 1;
            }

            if (value > 400)
            {
                return 400;
            }

            return value;
        }

        private static int NormalizeStickThreshold(int value)
        {
            if (value < 0)
            {
                return 0;
            }

            if (value > 20)
            {
                return 20;
            }

            return value;
        }

        private static int NormalizeStickAxis(int value)
        {
            if (value < 0)
            {
                return 0;
            }

            if (value > 2)
            {
                return 2;
            }

            return value;
        }

        private static int NormalizeStickGain(int value)
        {
            if (value < 25)
            {
                return 25;
            }

            if (value > 400)
            {
                return 400;
            }

            return value;
        }

        private static int NormalizeStickSelect(int value)
        {
            return value == 0 ? 0 : 1;
        }

        private static int NormalizeStickRange(int value)
        {
            if (value < 0)
            {
                return 0;
            }

            if (value > 200)
            {
                return 200;
            }

            return value;
        }

        private static int NormalizeVirtualAbxyLayout(int value)
        {
            return value == 1 ? 1 : 0;
        }

        private void LoadSettings()
        {
            try
            {
                if (LocalSettingsHelper.TryGetValue("ControllerEmulationEnabled", out bool savedEnabled))
                {
                    enabled = savedEnabled;
                }
                else
                {
                    // Safety default: emulation stays off until explicitly enabled by the user.
                    enabled = false;
                }

                if (LocalSettingsHelper.TryGetValue("ControllerEmulationHideStockController", out bool savedHideStockController))
                {
                    hideStockController = savedHideStockController;
                }
                else
                {
                    // Preserve current behavior for existing installs where suppression was always attempted.
                    hideStockController = true;
                }

                if (LocalSettingsHelper.TryGetValue("ControllerEmulationImprovedInput", out bool savedImprovedInput))
                {
                    improvedInputRead = savedImprovedInput;
                }
                else
                {
                    improvedInputRead = false;
                }

                if (LocalSettingsHelper.TryGetValue("ControllerEmulationHideTarget", out int savedHideTarget))
                {
                    hideTarget = NormalizeHideTarget(savedHideTarget);
                }
                else if (deviceType == SharedDeviceType.LegionGo || deviceType == SharedDeviceType.LegionGo2)
                {
                    // Fresh-install default for Legion Go / Go 2: hide both the native
                    // handheld HID and the Xbox 360 bridge. Now that the default gyro
                    // adapter reads via LegionButtonMonitor (cached HID handle), gyro
                    // keeps flowing while the OS-visible devices are suppressed, so
                    // games see only the emulated pad — no double input in Steam/Game Bar.
                    hideTarget = 3;
                }
                else
                {
                    hideTarget = 0;
                }

                // Prefer new handheld-agnostic keys, fall back to legacy GPD keys.
                if (LocalSettingsHelper.TryGetValue("ControllerEmulationGyroSource", out int savedGyroSource))
                {
                    gyroSource = NormalizeGyroSource(savedGyroSource);
                }
                else if (LocalSettingsHelper.TryGetValue("GPDControllerEmulationGyroSource", out int legacyGyroSource))
                {
                    gyroSource = NormalizeGyroSource(legacyGyroSource);
                }
                else
                {
                    gyroSource = 0;
                }

                if (LocalSettingsHelper.TryGetValue("ControllerEmulationMode", out int savedMode))
                {
                    mode = NormalizeMode(savedMode);
                }
                else if (LocalSettingsHelper.TryGetValue("GPDControllerEmulationMode", out int legacyMode))
                {
                    mode = NormalizeMode(legacyMode);
                }
                else
                {
                    mode = 0;
                }

                if (LocalSettingsHelper.TryGetValue("ControllerEmulationRumbleProfile", out int savedRumbleProfile))
                {
                    rumbleProfile = NormalizeRumbleProfile(savedRumbleProfile);
                }
                else
                {
                    rumbleProfile = 0;
                }

                if (LocalSettingsHelper.TryGetValue("ControllerEmulationGyroActivationMode", out int savedGyroActivationMode))
                {
                    gyroActivationMode = NormalizeGyroActivationMode(savedGyroActivationMode);
                }
                else
                {
                    gyroActivationMode = 0;
                }

                if (LocalSettingsHelper.TryGetValue("ControllerEmulationGyroActivationButton", out int savedGyroActivationButton))
                {
                    gyroActivationButton = NormalizeGyroActivationButton(savedGyroActivationButton);
                }
                else
                {
                    gyroActivationButton = 1;
                }

                if (LocalSettingsHelper.TryGetValue("ControllerEmulationDs4Orientation", out int savedDs4Orientation))
                {
                    ds4Orientation = NormalizeDs4Orientation(savedDs4Orientation);
                }
                else
                {
                    ds4Orientation = 0;
                }

                if (LocalSettingsHelper.TryGetValue("ControllerEmulationPs4TouchpadEnabled", out bool savedPs4TouchpadEnabled))
                {
                    ps4TouchpadEnabled = savedPs4TouchpadEnabled;
                }
                else
                {
                    ps4TouchpadEnabled = true;
                }

                if (LocalSettingsHelper.TryGetValue("ControllerEmulationMouseSensitivity", out int savedSensitivity))
                {
                    mouseSensitivity = NormalizeMouseSensitivity(savedSensitivity);
                }
                else
                {
                    mouseSensitivity = 100;
                }

                if (LocalSettingsHelper.TryGetValue("ControllerEmulationMouseThreshold", out int savedThreshold))
                {
                    mouseThreshold = NormalizeMouseThreshold(savedThreshold);
                }
                else
                {
                    mouseThreshold = 2;
                }

                if (LocalSettingsHelper.TryGetValue("ControllerEmulationMouseAxis", out int savedAxis))
                {
                    mouseAxis = NormalizeMouseAxis(savedAxis);
                }
                else
                {
                    mouseAxis = 0;
                }

                if (LocalSettingsHelper.TryGetValue("ControllerEmulationMouseInvertX", out bool savedInvertX))
                {
                    mouseInvertX = savedInvertX;
                }
                else
                {
                    mouseInvertX = false;
                }

                if (LocalSettingsHelper.TryGetValue("ControllerEmulationMouseInvertY", out bool savedInvertY))
                {
                    mouseInvertY = savedInvertY;
                }
                else
                {
                    mouseInvertY = false;
                }

                if (LocalSettingsHelper.TryGetValue("ControllerEmulationMouseGainX", out int savedGainX))
                {
                    mouseGainX = NormalizeMouseGain(savedGainX);
                }
                else
                {
                    mouseGainX = 100;
                }

                if (LocalSettingsHelper.TryGetValue("ControllerEmulationMouseGainY", out int savedGainY))
                {
                    mouseGainY = NormalizeMouseGain(savedGainY);
                }
                else
                {
                    mouseGainY = 100;
                }

                if (LocalSettingsHelper.TryGetValue("ControllerEmulationStickSensitivity", out int savedStickSensitivity))
                {
                    stickSensitivity = NormalizeStickSensitivity(savedStickSensitivity);
                }
                else
                {
                    stickSensitivity = 100;
                }

                if (LocalSettingsHelper.TryGetValue("ControllerEmulationStickThreshold", out int savedStickThreshold))
                {
                    stickThreshold = NormalizeStickThreshold(savedStickThreshold);
                }
                else
                {
                    stickThreshold = 2;
                }

                if (LocalSettingsHelper.TryGetValue("ControllerEmulationStickAxis", out int savedStickAxis))
                {
                    stickAxis = NormalizeStickAxis(savedStickAxis);
                }
                else
                {
                    stickAxis = 0;
                }

                if (LocalSettingsHelper.TryGetValue("ControllerEmulationStickInvertX", out bool savedStickInvertX))
                {
                    stickInvertX = savedStickInvertX;
                }
                else
                {
                    stickInvertX = false;
                }

                if (LocalSettingsHelper.TryGetValue("ControllerEmulationStickInvertY", out bool savedStickInvertY))
                {
                    stickInvertY = savedStickInvertY;
                }
                else
                {
                    stickInvertY = false;
                }

                if (LocalSettingsHelper.TryGetValue("ControllerEmulationStickGainX", out int savedStickGainX))
                {
                    stickGainX = NormalizeStickGain(savedStickGainX);
                }
                else
                {
                    stickGainX = 100;
                }

                if (LocalSettingsHelper.TryGetValue("ControllerEmulationStickGainY", out int savedStickGainY))
                {
                    stickGainY = NormalizeStickGain(savedStickGainY);
                }
                else
                {
                    stickGainY = 100;
                }

                if (LocalSettingsHelper.TryGetValue("ControllerEmulationStickSelect", out int savedStickSelect))
                {
                    stickSelect = NormalizeStickSelect(savedStickSelect);
                }
                else
                {
                    stickSelect = 1;
                }

                if (LocalSettingsHelper.TryGetValue("ControllerEmulationStickExcessMove", out bool savedStickExcessMove))
                {
                    stickExcessMove = savedStickExcessMove;
                }
                else
                {
                    stickExcessMove = false;
                }

                if (LocalSettingsHelper.TryGetValue("ControllerEmulationStickRange", out int savedStickRange))
                {
                    stickRange = NormalizeStickRange(savedStickRange);
                }
                else
                {
                    stickRange = 100;
                }

                if (LocalSettingsHelper.TryGetValue("ControllerEmulationStickOnlyJoystickData", out bool savedStickOnlyJoystickData))
                {
                    stickOnlyJoystickData = savedStickOnlyJoystickData;
                }
                else
                {
                    stickOnlyJoystickData = false;
                }

                // Stick v2 settings
                stickMinGyroSpeed = LocalSettingsHelper.TryGetValue("ControllerEmulationStickMinGyroSpeed", out int savedMinGyroSpeed)
                    ? Math.Max(0, Math.Min(100, savedMinGyroSpeed)) : 0;
                stickMaxGyroSpeed = LocalSettingsHelper.TryGetValue("ControllerEmulationStickMaxGyroSpeed", out int savedMaxGyroSpeed)
                    ? Math.Max(50, Math.Min(720, savedMaxGyroSpeed)) : 220;
                stickMinOutput = LocalSettingsHelper.TryGetValue("ControllerEmulationStickMinOutput", out int savedMinOutput)
                    ? Math.Max(0, Math.Min(100, savedMinOutput)) : 0;
                stickMaxOutput = LocalSettingsHelper.TryGetValue("ControllerEmulationStickMaxOutput", out int savedMaxOutput)
                    ? Math.Max(1, Math.Min(100, savedMaxOutput)) : 100;
                stickPowerCurve = LocalSettingsHelper.TryGetValue("ControllerEmulationStickPowerCurve", out int savedPowerCurve)
                    ? Math.Max(10, Math.Min(400, savedPowerCurve)) : 100;
                stickSensitivityV2 = LocalSettingsHelper.TryGetValue("ControllerEmulationStickSensitivityV2", out int savedSensV2)
                    ? Math.Max(1, Math.Min(400, savedSensV2)) : 100;
                stickDeadzone = LocalSettingsHelper.TryGetValue("ControllerEmulationStickDeadzone", out int savedDeadzone)
                    ? Math.Max(0, Math.Min(50, savedDeadzone)) : 2;
                stickPrecisionSpeed = LocalSettingsHelper.TryGetValue("ControllerEmulationStickPrecisionSpeed", out int savedPrecision)
                    ? Math.Max(0, Math.Min(100, savedPrecision)) : 0;
                stickOutputMix = LocalSettingsHelper.TryGetValue("ControllerEmulationStickOutputMix", out int savedOutputMix)
                    ? Math.Max(-100, Math.Min(100, savedOutputMix)) : 0;
                // 0 = Flat (no Y/Z swap), 1 = Handheld (Y/Z swap). Default Flat
                // because the new Mode 0 (Yaw) default uses gyroY directly,
                // which works for both flat and handheld holds without needing
                // the swap. Saved value wins.
                stickOrientationV2 = LocalSettingsHelper.TryGetValue("ControllerEmulationStickOrientationV2", out int savedOrientV2)
                    ? ((savedOrientV2 == 1) ? 1 : 0) : 0;
                // 0=Yaw, 1=Roll, 2=Yaw+Roll, 3=Player Space, 4=World Space.
                // Default 0 (Yaw) — "laser pointer from back of device" model:
                // gyroY directly drives horizontal, gyroX drives vertical, roll
                // doesn't move the camera. Player/World Space use gravity as
                // the yaw axis, which feels like the laser is pointing at the
                // sky on a tilted handheld. Saved value wins.
                stickConversion = LocalSettingsHelper.TryGetValue("ControllerEmulationStickConversion", out int savedConversion)
                    ? Math.Max(0, Math.Min(4, savedConversion)) : 0;

                stickGyroAntiDeadzone = LocalSettingsHelper.TryGetValue("ControllerEmulationStickGyroAntiDeadzone", out int savedAdz)
                    ? Math.Max(0, Math.Min(30, savedAdz)) : 10;
                stickGyroAntiDeadzoneThreshold = LocalSettingsHelper.TryGetValue("ControllerEmulationStickGyroAntiDeadzoneThreshold", out int savedAdzThr)
                    ? Math.Max(0, Math.Min(50, savedAdzThr)) : 3;

                stickGyroVerticalRatio = LocalSettingsHelper.TryGetValue("ControllerEmulationStickGyroVerticalRatio", out int savedVerticalRatio)
                    ? Math.Max(10, Math.Min(200, savedVerticalRatio)) : 100;
                stickGyroCurvePreset = LocalSettingsHelper.TryGetValue("ControllerEmulationStickGyroCurvePreset", out int savedCurvePreset)
                    ? Math.Max(0, Math.Min(2, savedCurvePreset)) : 0;
                stickGyroTightenThreshold = LocalSettingsHelper.TryGetValue("ControllerEmulationStickGyroTightenThreshold", out int savedTightenThr)
                    ? Math.Max(0, Math.Min(500, savedTightenThr)) : 0;
                stickGyroTightenGain = LocalSettingsHelper.TryGetValue("ControllerEmulationStickGyroTightenGain", out int savedTightenGain)
                    ? Math.Max(100, Math.Min(300, savedTightenGain)) : 100;
                stickGyroTouchDeactivateEnabled = LocalSettingsHelper.TryGetValue("ControllerEmulationStickGyroTouchDeactivateEnabled", out bool savedTouchEn) && savedTouchEn;
                stickGyroTouchDeactivateThreshold = LocalSettingsHelper.TryGetValue("ControllerEmulationStickGyroTouchDeactivateThreshold", out int savedTouchThr)
                    ? Math.Max(0, Math.Min(50, savedTouchThr)) : 15;
                stickGyroTouchDeactivateHoldoff = LocalSettingsHelper.TryGetValue("ControllerEmulationStickGyroTouchDeactivateHoldoff", out int savedTouchHo)
                    ? Math.Max(0, Math.Min(1000, savedTouchHo)) : 250;
                stickGyroSmoothing = LocalSettingsHelper.TryGetValue("ControllerEmulationStickGyroSmoothing", out int savedSmoothing)
                    ? Math.Max(0, Math.Min(90, savedSmoothing)) : 30;

                if (LocalSettingsHelper.TryGetValue("ControllerEmulationVirtualABXYLayout", out int savedVirtualAbxyLayout))
                {
                    virtualAbxyLayout = NormalizeVirtualAbxyLayout(savedVirtualAbxyLayout);
                }
                else
                {
                    virtualAbxyLayout = 0;
                }

                if (LocalSettingsHelper.TryGetValue("ControllerEmulationLedForwardingEnabled", out bool savedLedForwarding))
                {
                    ledForwardingEnabled = savedLedForwarding;
                }
                else
                {
                    ledForwardingEnabled = false;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Controller emulation settings load failed: {ex.Message}");
                enabled = false;
                hideStockController = true;
                improvedInputRead = false;
                hideTarget = 0;
                gyroSource = 0;
                mode = 0;
                rumbleProfile = 0;
                gyroActivationMode = 0;
                gyroActivationButton = 1;
                ds4Orientation = 0;
                ps4TouchpadEnabled = true;
                mouseSensitivity = 100;
                mouseThreshold = 2;
                mouseAxis = 0;
                mouseInvertX = false;
                mouseInvertY = false;
                mouseGainX = 100;
                mouseGainY = 100;
                stickSensitivity = 100;
                stickThreshold = 2;
                stickAxis = 0;
                stickInvertX = false;
                stickInvertY = false;
                stickGainX = 100;
                stickGainY = 100;
                stickSelect = 1;
                stickExcessMove = false;
                stickRange = 100;
                stickOnlyJoystickData = false;
                virtualAbxyLayout = 0;
            }
        }

        private void SaveSettings()
        {
            try
            {
                LocalSettingsHelper.SetValue("ControllerEmulationEnabled", enabled);
                LocalSettingsHelper.SetValue("ControllerEmulationHideStockController", hideStockController);
                LocalSettingsHelper.SetValue("ControllerEmulationImprovedInput", improvedInputRead);
                LocalSettingsHelper.SetValue("ControllerEmulationHideTarget", hideTarget);
                LocalSettingsHelper.SetValue("ControllerEmulationGyroSource", gyroSource);
                LocalSettingsHelper.SetValue("ControllerEmulationMode", mode);
                LocalSettingsHelper.SetValue("ControllerEmulationRumbleProfile", rumbleProfile);
                LocalSettingsHelper.SetValue("ControllerEmulationGyroActivationMode", gyroActivationMode);
                LocalSettingsHelper.SetValue("ControllerEmulationGyroActivationButton", gyroActivationButton);
                LocalSettingsHelper.SetValue("ControllerEmulationDs4Orientation", ds4Orientation);
                LocalSettingsHelper.SetValue("ControllerEmulationPs4TouchpadEnabled", ps4TouchpadEnabled);
                LocalSettingsHelper.SetValue("ControllerEmulationMouseSensitivity", mouseSensitivity);
                LocalSettingsHelper.SetValue("ControllerEmulationMouseThreshold", mouseThreshold);
                LocalSettingsHelper.SetValue("ControllerEmulationMouseAxis", mouseAxis);
                LocalSettingsHelper.SetValue("ControllerEmulationMouseInvertX", mouseInvertX);
                LocalSettingsHelper.SetValue("ControllerEmulationMouseInvertY", mouseInvertY);
                LocalSettingsHelper.SetValue("ControllerEmulationMouseGainX", mouseGainX);
                LocalSettingsHelper.SetValue("ControllerEmulationMouseGainY", mouseGainY);
                LocalSettingsHelper.SetValue("ControllerEmulationStickSensitivity", stickSensitivity);
                LocalSettingsHelper.SetValue("ControllerEmulationStickThreshold", stickThreshold);
                LocalSettingsHelper.SetValue("ControllerEmulationStickAxis", stickAxis);
                LocalSettingsHelper.SetValue("ControllerEmulationStickInvertX", stickInvertX);
                LocalSettingsHelper.SetValue("ControllerEmulationStickInvertY", stickInvertY);
                LocalSettingsHelper.SetValue("ControllerEmulationStickGainX", stickGainX);
                LocalSettingsHelper.SetValue("ControllerEmulationStickGainY", stickGainY);
                LocalSettingsHelper.SetValue("ControllerEmulationStickSelect", stickSelect);
                LocalSettingsHelper.SetValue("ControllerEmulationStickExcessMove", stickExcessMove);
                LocalSettingsHelper.SetValue("ControllerEmulationStickRange", stickRange);
                LocalSettingsHelper.SetValue("ControllerEmulationStickOnlyJoystickData", stickOnlyJoystickData);
                LocalSettingsHelper.SetValue("ControllerEmulationVirtualABXYLayout", virtualAbxyLayout);
                LocalSettingsHelper.SetValue("ControllerEmulationLedForwardingEnabled", ledForwardingEnabled);
                LocalSettingsHelper.SetValue("ControllerEmulationStickMinGyroSpeed", stickMinGyroSpeed);
                LocalSettingsHelper.SetValue("ControllerEmulationStickMaxGyroSpeed", stickMaxGyroSpeed);
                LocalSettingsHelper.SetValue("ControllerEmulationStickMinOutput", stickMinOutput);
                LocalSettingsHelper.SetValue("ControllerEmulationStickMaxOutput", stickMaxOutput);
                LocalSettingsHelper.SetValue("ControllerEmulationStickPowerCurve", stickPowerCurve);
                LocalSettingsHelper.SetValue("ControllerEmulationStickSensitivityV2", stickSensitivityV2);
                LocalSettingsHelper.SetValue("ControllerEmulationStickDeadzone", stickDeadzone);
                LocalSettingsHelper.SetValue("ControllerEmulationStickPrecisionSpeed", stickPrecisionSpeed);
                LocalSettingsHelper.SetValue("ControllerEmulationStickOutputMix", stickOutputMix);
                LocalSettingsHelper.SetValue("ControllerEmulationStickOrientationV2", stickOrientationV2);
                LocalSettingsHelper.SetValue("ControllerEmulationStickConversion", stickConversion);
                LocalSettingsHelper.SetValue("ControllerEmulationStickGyroAntiDeadzone", stickGyroAntiDeadzone);
                LocalSettingsHelper.SetValue("ControllerEmulationStickGyroAntiDeadzoneThreshold", stickGyroAntiDeadzoneThreshold);
                LocalSettingsHelper.SetValue("ControllerEmulationStickGyroVerticalRatio", stickGyroVerticalRatio);
                LocalSettingsHelper.SetValue("ControllerEmulationStickGyroCurvePreset", stickGyroCurvePreset);
                LocalSettingsHelper.SetValue("ControllerEmulationStickGyroTightenThreshold", stickGyroTightenThreshold);
                LocalSettingsHelper.SetValue("ControllerEmulationStickGyroTightenGain", stickGyroTightenGain);
                LocalSettingsHelper.SetValue("ControllerEmulationStickGyroTouchDeactivateEnabled", stickGyroTouchDeactivateEnabled);
                LocalSettingsHelper.SetValue("ControllerEmulationStickGyroTouchDeactivateThreshold", stickGyroTouchDeactivateThreshold);
                LocalSettingsHelper.SetValue("ControllerEmulationStickGyroTouchDeactivateHoldoff", stickGyroTouchDeactivateHoldoff);
                LocalSettingsHelper.SetValue("ControllerEmulationStickGyroSmoothing", stickGyroSmoothing);

                // Keep legacy keys in sync for compatibility with older builds.
                LocalSettingsHelper.SetValue("GPDControllerEmulationGyroSource", gyroSource);
                LocalSettingsHelper.SetValue("GPDControllerEmulationMode", mode);
            }
            catch (Exception ex)
            {
                Logger.Warn($"Controller emulation settings save failed: {ex.Message}");
            }
        }

        private void ApplyCurrentConfiguration(string reason)
        {
            if (!isSupported)
            {
                StopForwarding();
                suppressionPausedForGameBar = false;
                suppressionPauseUntilTicksUtc = 0;
                Logger.Debug($"Skipping controller emulation apply ({reason}): unsupported device type {deviceType}");
                return;
            }

            if (!enabled)
            {
                StopForwarding();
                suppressionPausedForGameBar = false;
                suppressionPauseUntilTicksUtc = 0;
                Logger.Info($"Controller emulation disabled ({reason}); forwarding stopped");
                return;
            }

            if (suppressedByViiper)
            {
                // CRITICAL: preserveSuppression=true so legacy's StopForwarding
                // does NOT call DisableSuppression. VIIPER owns the HidHide
                // hide list while suppressedByViiper is in effect. Without
                // this, legacy's deferred startup apply (which runs AFTER
                // VIIPER's HidHide enable per helper_2026-05-21_00.log at
                // 00:41:51-53) wipes the Legion hides VIIPER just established,
                // and the user has to toggle CE off/on for VIIPER's HidHide
                // state to stick.
                StopForwarding(preserveSuppression: true);
                suppressionPausedForGameBar = false;
                suppressionPauseUntilTicksUtc = 0;
                Logger.Info($"Controller emulation suppressed by VIIPER backend ({reason}); legacy forwarding stopped (HidHide preserved — VIIPER owns it)");
                return;
            }

            // Controller emulation is intentionally software-only and independent from
            // per-device firmware gyro settings exposed in the Legion tab.
            bool backendApplied = true;
            Logger.Debug($"Controller emulation using software path only ({reason}); skipping device firmware gyro mapping");

            // Rebuild forwarding/runtime state for every apply-triggering setting change.
            // This avoids races between live submit loop and virtual device reconnect/dispose.
            // Keep suppression active across virtual-mode switches to avoid unhide/rehide port churn.
            bool preserveSuppressionAcrossRestart =
                suppressionActive &&
                hideStockController &&
                RequiresSoftwareForwarding(mode) &&
                RequiresVirtualGamepad(mode);
            // When switching between modes that use the same virtual gamepad type (e.g. DS4 Stick ↔ DS4 Motion),
            // preserve the virtual controller to avoid Windows losing track of the device during re-enumeration.
            var newTargetType = mode == 1
                ? ViGEmController.VirtualGamepadType.Xbox360
                : ViGEmController.VirtualGamepadType.DualShock4;
            bool preserveVirtualController =
                virtualController != null &&
                virtualController.IsPluggedIn &&
                RequiresVirtualGamepad(mode) &&
                virtualController.CurrentType == newTargetType;
            StopForwarding(preserveSuppressionAcrossRestart, preserveVirtualController);
            bool forwardingApplied = ConfigureForwarding(reason);

            if (backendApplied || forwardingApplied)
            {
                Logger.Info($"Controller emulation applied ({reason}): source={gyroSource}, mode={mode}, rumbleProfile={rumbleProfile}, gyroActivationMode={gyroActivationMode}, gyroActivationButton={gyroActivationButton}, ds4Orientation={ds4Orientation}, ps4TouchpadEnabled={ps4TouchpadEnabled}, hideStockController={hideStockController}, hideTarget={hideTarget}, improvedInput={improvedInputRead}, device={deviceType}, backend={backendApplied}, forwarding={forwardingApplied}");
            }
            else
            {
                Logger.Warn($"Controller emulation apply not completed ({reason}): source={gyroSource}, mode={mode}, rumbleProfile={rumbleProfile}, gyroActivationMode={gyroActivationMode}, gyroActivationButton={gyroActivationButton}, ds4Orientation={ds4Orientation}, ps4TouchpadEnabled={ps4TouchpadEnabled}, hideStockController={hideStockController}, hideTarget={hideTarget}, improvedInput={improvedInputRead}, device={deviceType}, backend={backendApplied}, forwarding={forwardingApplied}");
            }
        }

        private bool ConfigureForwarding(string reason)
        {
            if (!RequiresSoftwareForwarding(mode))
            {
                Logger.Info($"Controller emulation forwarding disabled ({reason}) for mode {mode}");
                return true;
            }

            bool gyroReady = ConfigureGyroSourceAdapter(reason);
            bool needsVirtualGamepad = RequiresVirtualGamepad(mode);

            if (!needsVirtualGamepad)
            {
                if (gyroActivationMode != 0 && !EnsureXInputLoaded())
                {
                    Logger.Warn("Controller emulation gyro activation is set to Hold/Toggle but XInput is unavailable");
                }

                DisableSuppression();
                StartForwarding();
                return gyroReady;
            }

            if (!EnsureXInputLoaded())
            {
                Logger.Warn("Controller emulation forwarding unavailable: XInput not loaded");
                return false;
            }

            var targetType = mode == 1
                ? ViGEmController.VirtualGamepadType.Xbox360
                : ViGEmController.VirtualGamepadType.DualShock4;

            int? preferredPhysicalIndex = null;
            IReadOnlyCollection<string> xboxBridgeIdsBeforeVirtualConnect = null;
            if (targetType == ViGEmController.VirtualGamepadType.Xbox360)
            {
                preferredPhysicalIndex = DiscoverPreferredPhysicalXboxIndex(null);
                xboxBridgeIdsBeforeVirtualConnect = ControllerSuppressionManager.QueryXboxBridgeDeviceIds();
            }

            if (virtualController == null)
            {
                virtualController = new ViGEmController();
            }

            virtualController.RumbleReceived -= OnVirtualControllerRumbleReceived;
            virtualController.RumbleReceived += OnVirtualControllerRumbleReceived;
            virtualController.LedReceived -= OnVirtualControllerLedReceived;
            virtualController.LedReceived += OnVirtualControllerLedReceived;

            if (!virtualController.EnsureConnected(targetType))
            {
                Logger.Warn($"Controller emulation forwarding: failed to connect virtual {targetType} controller");
                return false;
            }

            if (targetType == ViGEmController.VirtualGamepadType.Xbox360)
            {
                UpdateVirtualXboxBridgeDeviceIds(xboxBridgeIdsBeforeVirtualConnect);

                virtualXboxUserIndex = TryGetVirtualXboxUserIndexSafe();
                if (!virtualXboxUserIndex.HasValue)
                {
                    Logger.Warn("Controller emulation virtual Xbox user index not reported yet; forwarding will continue with fallback physical source selection");
                }

                if (preferredPhysicalIndex.HasValue &&
                    (!virtualXboxUserIndex.HasValue || preferredPhysicalIndex.Value != virtualXboxUserIndex.Value))
                {
                    physicalXboxUserIndex = preferredPhysicalIndex;
                }
                else
                {
                    physicalXboxUserIndex = null;
                }
            }
            else
            {
                virtualXboxUserIndex = null;
                physicalXboxUserIndex = null;
                virtualXboxBridgeDeviceIds.Clear();
            }

            StartForwarding();

            // The pad target just (re)connected — possibly changing whether this
            // legacy pad can carry the Xbox Guide route (Xbox360 target: yes;
            // DS4: no). Re-run the VIIPER guide-only reconciliation so the Guide
            // route hands over without waiting for another trigger. Replaces the
            // retired 500ms ViGEm reconcile poll in LegionButtonMonitor.
            try { Program.NotifyGuideRouteChanged(); }
            catch (Exception ex) { Logger.Debug($"StartForwarding NotifyGuideRouteChanged threw: {ex.Message}"); }

            if (!hideStockController)
            {
                suppressionPausedForGameBar = false;
                suppressionPauseUntilTicksUtc = 0;
                DisableSuppression();
                Logger.Info($"Controller emulation suppression disabled by setting ({reason})");
                return gyroReady;
            }

            if (TryPauseSuppressionForForegroundGameBar(reason))
            {
                return gyroReady;
            }

            bool suppressionReady = EnableSuppression(reason);
            if (!suppressionReady)
            {
                Logger.Warn($"Controller emulation suppression unavailable ({reason}); forwarding continues without HidHide cloaking");
            }

            // Post-Start re-pin (ported from ViiperEmulationManager). The initial
            // DiscoverPreferredPhysicalXboxIndex at the top of ConfigureForwarding fires
            // BEFORE EnableSuppression engages and BEFORE LegionButtonMonitor's Labs
            // ViGEm pad is disposed. Either of those events can shuffle XInput slots,
            // leaving physicalXboxUserIndex pinned to the wrong slot. The forwarder's
            // own per-loop slot recovery covers most cases, but doing an explicit
            // re-pick here shaves recovery latency and makes the slot transition
            // visible in logs for "no input after toggle" triage.
            if (targetType == ViGEmController.VirtualGamepadType.Xbox360)
            {
                try
                {
                    System.Threading.Thread.Sleep(150); // let HidHide cycle-port + Labs pad disposal settle
                    int? repinned = DiscoverPreferredPhysicalXboxIndex(virtualXboxUserIndex);
                    if (repinned.HasValue && repinned.Value != physicalXboxUserIndex)
                    {
                        Logger.Info($"Controller emulation post-Start XInput re-pin: physicalIndex {physicalXboxUserIndex} -> {repinned} (post-suppression slot reshuffle)");
                        physicalXboxUserIndex = repinned;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Debug($"Controller emulation post-Start re-pin skipped: {ex.Message}");
                }
            }

            return gyroReady;
        }

        private void ApplySuppressionConfiguration(string reason)
        {
            if (!isSupported || !enabled || !RequiresSoftwareForwarding(mode) || !RequiresVirtualGamepad(mode))
            {
                suppressionPausedForGameBar = false;
                suppressionPauseUntilTicksUtc = 0;
                DisableSuppression();
                Logger.Info($"Controller emulation suppression skipped ({reason})");
                return;
            }

            if (!hideStockController)
            {
                suppressionPausedForGameBar = false;
                suppressionPauseUntilTicksUtc = 0;
                DisableSuppression();
                Logger.Info($"Controller emulation suppression disabled by setting ({reason})");
                return;
            }

            if (TryPauseSuppressionForForegroundGameBar(reason))
            {
                return;
            }

            bool suppressionReady = EnableSuppression(reason);
            if (!suppressionReady)
            {
                Logger.Warn($"Controller emulation suppression unavailable ({reason}); forwarding continues without HidHide cloaking");
            }
        }

        private static bool RequiresSoftwareForwarding(int selectedMode)
        {
            return selectedMode >= 0 && selectedMode <= 3;
        }

        private static bool RequiresVirtualGamepad(int selectedMode)
        {
            return selectedMode == 1 || selectedMode == 2 || selectedMode == 3;
        }

        private bool EnsureXInputLoaded()
        {
            if (xInputGetState != null && xInputSetState != null)
            {
                return true;
            }

            try
            {
                var state = new XINPUT_STATE();
                XInputGetState14(0, ref state);
                xInputGetState = XInputGetState14;
                xInputSetState = XInputSetState14;
                Logger.Info("Controller emulation using xinput1_4.dll");
                return true;
            }
            catch
            {
                try
                {
                    var state = new XINPUT_STATE();
                    XInputGetState910(0, ref state);
                    xInputGetState = XInputGetState910;
                    xInputSetState = XInputSetState910;
                    Logger.Info("Controller emulation using xinput9_1_0.dll");
                    return true;
                }
                catch (Exception ex)
                {
                    Logger.Error($"Controller emulation failed to load XInput: {ex.Message}");
                    xInputGetState = null;
                    xInputSetState = null;
                    return false;
                }
            }
        }

        private void StartForwarding()
        {
            if (forwardingRunning)
            {
                return;
            }

            ResetMouseRuntimeState();
            ResetStickRuntimeState();
            ResetGyroActivationRuntimeState();
            ResetLegionUserspaceRemapRuntime();
            physicalXboxUserIndex = null;
            forwardingRunning = true;
            forwardingThread = new Thread(ForwardingThreadProc)
            {
                IsBackground = true,
                Name = "ControllerEmulationForwarder",
                Priority = ThreadPriority.AboveNormal,
            };
            forwardingThread.Start();
            Logger.Info("Controller emulation forwarding thread started");
        }

        private void StopForwarding(bool preserveSuppression = false, bool preserveVirtualController = false)
        {
            forwardingRunning = false;
            if (forwardingThread != null && forwardingThread.IsAlive)
            {
                forwardingThread.Join(500);
            }

            forwardingThread = null;
            virtualXboxUserIndex = null;
            physicalXboxUserIndex = null;
            virtualXboxBridgeDeviceIds.Clear();
            lastLegionHidSampleTimestampTicksUtc = 0;
            legionHidPacketNumber = 0;
            ResetMouseRuntimeState();
            ResetStickRuntimeState();
            ResetGyroActivationRuntimeState();
            ResetLegionUserspaceRemapRuntime();

            if (virtualController != null && !preserveVirtualController)
            {
                try
                {
                    virtualController.RumbleReceived -= OnVirtualControllerRumbleReceived;
                    virtualController.LedReceived -= OnVirtualControllerLedReceived;
                    virtualController.Dispose();
                }
                catch
                {
                    // Ignore disposal failures on shutdown.
                }
                virtualController = null;

                // Legacy pad gone — if a Legion button maps to Xbox Guide, the
                // VIIPER guide-only pad must take the route over. Mirrors the
                // notify in the connect path.
                try { Program.NotifyGuideRouteChanged(); }
                catch (Exception ex) { Logger.Debug($"StopForwarding NotifyGuideRouteChanged threw: {ex.Message}"); }
            }

            if (hasForwardedLed && !preserveVirtualController)
            {
                RevertLegionLed();
            }

            if (!preserveSuppression)
            {
                DisableSuppression();
            }

            StopForwardedRumble();
            StopGyroSourceAdapter();
        }

        private void ForwardingThreadProc()
        {
            Logger.Info("Controller emulation forwarding loop running");
            forwardingStatsLastEmitTicksUtc = DateTime.UtcNow.Ticks;
            forwardingIterations = 0;
            forwardingReadOkXInput = 0;
            forwardingReadOkLegionHid = 0;
            forwardingReadFail = 0;
            forwardingGyroMerged = 0;
            forwardingGyroNoSample = 0;
            forwardingGyroGateOff = 0;
            while (forwardingRunning)
            {
                try
                {
                    forwardingIterations++;
                    EmitForwardingStatsIfDue();
                    if (mode == 0)
                    {
                        XINPUT_STATE activationState = default;
                        bool hasActivationState = false;
                        if (gyroActivationMode != 0)
                        {
                            if (xInputGetState == null)
                            {
                                EnsureXInputLoaded();
                            }

                            if (xInputGetState != null)
                            {
                                hasActivationState = TryReadPhysicalControllerState(out activationState);
                                if (!hasActivationState) forwardingReadFail++;
                            }
                        }

                        bool mouseGyroActive = IsGyroActivationEnabled(hasActivationState ? activationState.Gamepad : (XINPUT_GAMEPAD?)null);
                        if (mouseGyroActive && TryReadGyroSample(out GyroSample mouseSample))
                        {
                            ApplyMouseFromGyro(mouseSample);
                        }

                        Thread.Sleep(ForwardingIntervalMs);
                        continue;
                    }

                    if (!TryReadPhysicalControllerState(out XINPUT_STATE state))
                    {
                        forwardingReadFail++;
                        Thread.Sleep(ForwardingIntervalMs);
                        continue;
                    }

                    ushort forwardedButtons = ApplyVirtualAbxyLayout(state.Gamepad.wButtons);
                    byte forwardedLeftTrigger = state.Gamepad.bLeftTrigger;
                    byte forwardedRightTrigger = state.Gamepad.bRightTrigger;
                    short forwardedLeftX = state.Gamepad.sThumbLX;
                    short forwardedLeftY = state.Gamepad.sThumbLY;
                    short forwardedRightX = state.Gamepad.sThumbRX;
                    short forwardedRightY = state.Gamepad.sThumbRY;
                    bool ds4TouchActive = false;
                    bool ds4TouchPressed = false;
                    ushort ds4TouchX = 0;
                    ushort ds4TouchY = 0;
                    bool gyroActive = IsGyroActivationEnabled(state.Gamepad);

                    if (mode == 1 || mode == 3)
                    {
                        if (gyroActive && TryReadGyroSample(out GyroSample stickSample))
                        {
                            // Pass the user-selected physical stick values so stick-touch
                            // deactivation can suppress gyro when the player is moving
                            // the stick by hand. stickSelect 0 = Left, 1 = Right.
                            short physStickX = stickSelect == 1 ? forwardedRightX : forwardedLeftX;
                            short physStickY = stickSelect == 1 ? forwardedRightY : forwardedLeftY;
                            ApplyStickFromGyro(stickSample, physStickX, physStickY, out lastGyroStickX, out lastGyroStickY);
                            hasLastGyroStick = true;
                            lastGyroStickTicksUtc = stickSample.TimestampTicksUtc > 0
                                ? stickSample.TimestampTicksUtc
                                : DateTime.UtcNow.Ticks;
                            // Diagnostic: count only when the math produced a non-zero
                            // contribution. Zero outputs at this step usually mean
                            // bias-corrected sample fell entirely inside the deadzone.
                            if (lastGyroStickX != 0 || lastGyroStickY != 0)
                            {
                                forwardingGyroMerged++;
                            }
                        }
                        else if (!gyroActive)
                        {
                            lastGyroStickX = 0;
                            lastGyroStickY = 0;
                            hasLastGyroStick = false;
                            lastGyroStickTicksUtc = 0;
                            forwardingGyroGateOff++;
                            // Feed the bias estimator while the gate is closed so
                            // calibration is already converged when the user presses
                            // the activation button. Otherwise Hold-mode users would
                            // see full pre-correction drift on engage.
                            if (TryReadGyroSample(out GyroSample idleSample))
                            {
                                _ = stickGyroBiasEstimator.Correct(idleSample);
                            }
                        }
                        else
                        {
                            // gyroActive but TryReadGyroSample returned false — the
                            // adapter isn't producing samples (LegionButtonMonitor
                            // dead, or Windows Sensor stack timed out). Keep
                            // hasLastGyroStick alive so the existing stale guard
                            // can age the previous output out.
                            forwardingGyroNoSample++;
                        }

                        if (hasLastGyroStick)
                        {
                            long nowTicksUtc = DateTime.UtcNow.Ticks;
                            if (lastGyroStickTicksUtc > 0 && (nowTicksUtc - lastGyroStickTicksUtc) > StickOutputStaleTicks)
                            {
                                lastGyroStickX = 0;
                                lastGyroStickY = 0;
                                hasLastGyroStick = false;
                            }
                        }

                        if (hasLastGyroStick)
                        {
                            if (stickSelect == 0)
                            {
                                MergeStickVectors(
                                    forwardedLeftX,
                                    forwardedLeftY,
                                    lastGyroStickX,
                                    lastGyroStickY,
                                    out forwardedLeftX,
                                    out forwardedLeftY);
                            }
                            else
                            {
                                MergeStickVectors(
                                    forwardedRightX,
                                    forwardedRightY,
                                    lastGyroStickX,
                                    lastGyroStickY,
                                    out forwardedRightX,
                                    out forwardedRightY);
                            }
                        }
                    }

                    if ((mode == 1 || mode == 3) && stickOnlyJoystickData)
                    {
                        forwardedButtons = 0;
                        forwardedLeftTrigger = 0;
                        forwardedRightTrigger = 0;
                    }

                    if ((mode == 2 || mode == 3) && ps4TouchpadEnabled)
                    {
                        TryReadDs4TouchSample(out ds4TouchActive, out ds4TouchPressed, out ds4TouchX, out ds4TouchY);
                    }

                    bool ok = false;
                    if (mode == 1)
                    {
                        ok = SubmitXboxState(
                            forwardedButtons,
                            forwardedLeftTrigger,
                            forwardedRightTrigger,
                            forwardedLeftX,
                            forwardedLeftY,
                            forwardedRightX,
                            forwardedRightY);
                    }
                    else if (mode == 2)
                    {
                        short gyroX = 0;
                        short gyroY = 0;
                        short gyroZ = 0;
                        // Default accel = controller lying flat (1G downward on Z axis)
                        short accelX = 0;
                        short accelY = 0;
                        short accelZ = Ds4DefaultAccelZRaw;

                        if (gyroActive && TryReadGyroSample(out GyroSample motionSample))
                        {
                            float gyroXValue = motionSample.GyroXDegPerSecond;
                            float gyroYValue = motionSample.GyroYDegPerSecond;
                            float gyroZValue = motionSample.GyroZDegPerSecond;
                            float accelXValue = motionSample.AccelXG;
                            float accelYValue = motionSample.AccelYG;
                            float accelZValue = motionSample.AccelZG;

                            ApplyDs4Orientation(
                                ref gyroXValue,
                                ref gyroYValue,
                                ref gyroZValue,
                                ref accelXValue,
                                ref accelYValue,
                                ref accelZValue);

                            // DS4 gyro/accel conversion.
                            // Legion BMI323 controller needs Y↔Z swap; Windows sensor API already uses standard axes.
                            bool isLegionController = gyroSourceAdapter is ILegionControllerGyroSource;
                            gyroX = ConvertToDs4Gyro(gyroXValue);
                            gyroY = ConvertToDs4Gyro(isLegionController ? gyroZValue : gyroYValue);
                            gyroZ = ConvertToDs4Gyro(isLegionController ? gyroYValue : gyroZValue);
                            accelX = ConvertToDs4Accel(accelXValue);
                            accelY = ConvertToDs4Accel(isLegionController ? accelZValue : accelYValue);
                            accelZ = ConvertToDs4Accel(isLegionController ? accelYValue : accelZValue);

                            if (Logger.IsDebugEnabled && gyroDiagLogCounter++ % 500 == 0)
                            {
                                Logger.Debug($"DS4 motion: gyro=({gyroXValue:F1},{gyroYValue:F1},{gyroZValue:F1})°/s accel=({accelXValue:F2},{accelYValue:F2},{accelZValue:F2})G ds4=({gyroX},{gyroY},{gyroZ},{accelX},{accelY},{accelZ})");
                            }
                        }

                        ok = SubmitDualShock4StateRaw(
                            forwardedButtons,
                            forwardedLeftTrigger,
                            forwardedRightTrigger,
                            forwardedLeftX,
                            forwardedLeftY,
                            forwardedRightX,
                            forwardedRightY,
                            gyroX,
                            gyroY,
                            gyroZ,
                            accelX,
                            accelY,
                            accelZ,
                            ds4TouchActive,
                            ds4TouchX,
                            ds4TouchY,
                            ds4TouchPressed);
                    }
                    else if (mode == 3)
                    {
                        ok = SubmitDualShock4StateRaw(
                            forwardedButtons,
                            forwardedLeftTrigger,
                            forwardedRightTrigger,
                            forwardedLeftX,
                            forwardedLeftY,
                            forwardedRightX,
                            forwardedRightY,
                            0,
                            0,
                            0,
                            0,
                            0,
                            Ds4DefaultAccelZRaw,
                            ds4TouchActive,
                            ds4TouchX,
                            ds4TouchY,
                            ds4TouchPressed);
                    }

                    if (!ok)
                    {
                        Logger.Debug("Controller emulation forwarding submit failed");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn($"Controller emulation forwarding loop error: {ex.Message}");
                }

                // Event-driven wait — same fix as the VIIPER forwarder. Blocks
                // until a fresh HID report arrives from Lenovo (zero CPU until
                // signaled), or falls through after the timeout for non-Legion
                // sources / XInput-only paths.
                if (deviceType == SharedDeviceType.LegionGo || deviceType == SharedDeviceType.LegionGo2)
                {
                    Labs.LegionButtonMonitor.WaitForNewSample(16);
                }
                else
                {
                    Thread.Sleep(ForwardingIntervalMs);
                }
            }

            Logger.Info("Controller emulation forwarding loop stopped");
        }

        private void ResetMouseRuntimeState()
        {
            mouseCarryX = 0.0f;
            mouseCarryY = 0.0f;
            mouseFilteredHorizontal = 0.0f;
            mouseFilteredVertical = 0.0f;
            mouseFilteredDerivativeHorizontal = 0.0f;
            mouseFilteredDerivativeVertical = 0.0f;
            mouseFilterInitialized = false;
            mouseLastSampleTicksUtc = 0;
        }

        private void ResetStickRuntimeState()
        {
            lastGyroStickX = 0;
            lastGyroStickY = 0;
            hasLastGyroStick = false;
            lastGyroStickTicksUtc = 0;
            stickFilteredHorizontal = 0.0f;
            stickFilteredVertical = 0.0f;
            stickFilteredDerivativeHorizontal = 0.0f;
            stickFilteredDerivativeVertical = 0.0f;
            stickFilterInitialized = false;
            stickLastSampleTicksUtc = 0;
        }

    }
}
