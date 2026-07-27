using NLog;
using Shared.Data;
using Shared.Enums;
using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage;
using XboxGamingBarHelper.Core;
using XboxGamingBarHelper.Devices;
using XboxGamingBarHelper.Performance;

namespace XboxGamingBarHelper.Devices.Libraries.Legion
{
    internal partial class LegionManager
    {
        private int gyroTarget = 0;
        private int gyroSensitivityX = 50;
        private int gyroSensitivityY = 50;
        private bool gyroInvertX = false;
        private bool gyroInvertY = false;
        private int gyroMappingType = 0;
        private int gyroActivationMode = 0;
        private int gyroActivationButton = 0;

        // Advanced gyro settings
        private int gyroDeadzone = 10;         // 1-100

        /// <summary>
        /// Sets the gyro target output (0=Disabled, 1=LeftStick, 2=RightStick, 3=Mouse).
        /// </summary>
        public void SetGyroTarget(int target)
        {
            try
            {
                using var controller = new LegionGoController();
                if (!controller.Connect())
                {
                    Logger.Warn("Cannot set gyro target: controller not connected");
                    return;
                }

                // Map index to GyroTarget enum (0=Disabled maps to 0x01, etc.)
                GyroTarget gyroTargetValue = target switch
                {
                    0 => GyroTarget.Disabled,
                    1 => GyroTarget.LeftStick,
                    2 => GyroTarget.RightStick,
                    3 => GyroTarget.Mouse,
                    _ => GyroTarget.Disabled
                };

                bool success = controller.SetGyroTarget(gyroTargetValue);
                if (success)
                {
                    gyroTarget = target;
                    Logger.Info($"Gyro target set to {gyroTargetValue}");
                }
                else
                {
                    Logger.Error($"Failed to set gyro target to {gyroTargetValue}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error setting gyro target: {ex.Message}");
            }
        }

        /// <summary>
        /// Sets the gyro X-axis sensitivity (1-100).
        /// </summary>
        public void SetGyroSensitivityX(int sensitivity)
        {
            gyroSensitivityX = sensitivity;
            ApplyGyroSettings();
        }

        /// <summary>
        /// Sets the gyro Y-axis sensitivity (1-100).
        /// </summary>
        public void SetGyroSensitivityY(int sensitivity)
        {
            gyroSensitivityY = sensitivity;
            ApplyGyroSettings();
        }

        /// <summary>
        /// Sets the gyro X-axis inversion.
        /// </summary>
        public void SetGyroInvertX(bool invert)
        {
            gyroInvertX = invert;
            ApplyGyroSettings();
        }

        /// <summary>
        /// Sets the gyro Y-axis inversion.
        /// </summary>
        public void SetGyroInvertY(bool invert)
        {
            gyroInvertY = invert;
            ApplyGyroSettings();
        }

        /// <summary>
        /// Sets the gyro mapping type (0=Instant, 1=Continuous).
        /// </summary>
        public void SetGyroMappingType(int mappingType)
        {
            gyroMappingType = mappingType;
            ApplyGyroSettings();
        }

        /// <summary>
        /// Applies all gyro settings at once.
        /// </summary>
        private void ApplyGyroSettings()
        {
            try
            {
                using var controller = new LegionGoController();
                if (!controller.Connect())
                {
                    Logger.Warn("Cannot apply gyro settings: controller not connected");
                    return;
                }

                GyroMappingType mappingTypeValue = gyroMappingType switch
                {
                    0 => GyroMappingType.Instant,
                    1 => GyroMappingType.Continuous,
                    _ => GyroMappingType.Instant
                };

                bool success = controller.SetGyroSettings(mappingTypeValue, gyroSensitivityX, gyroSensitivityY, gyroInvertX, gyroInvertY);
                if (success)
                {
                    Logger.Info($"Gyro settings applied: MappingType={mappingTypeValue}, SensX={gyroSensitivityX}, SensY={gyroSensitivityY}, InvX={gyroInvertX}, InvY={gyroInvertY}");
                }
                else
                {
                    Logger.Error("Failed to apply gyro settings");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error applying gyro settings: {ex.Message}");
            }
        }

        /// <summary>
        /// Sets the gyro activation mode (0=Hold, 1=Toggle).
        /// </summary>
        public void SetGyroActivationMode(int mode)
        {
            gyroActivationMode = mode;
            ApplyGyroActivation();
        }

        /// <summary>
        /// Sets the gyro activation button (0-8: None, LB, LT, RB, RT, Y1, Y2, M2, M3).
        /// </summary>
        public void SetGyroActivationButton(int button)
        {
            gyroActivationButton = button;
            ApplyGyroActivation();
        }

        /// <summary>
        /// Applies gyro activation settings.
        /// </summary>
        private void ApplyGyroActivation()
        {
            try
            {
                using var controller = new LegionGoController();
                if (!controller.Connect())
                {
                    Logger.Warn("Cannot apply gyro activation: controller not connected");
                    return;
                }

                // If button is 0 (None), reset to always-on mode
                if (gyroActivationButton == 0)
                {
                    bool resetSuccess = controller.ResetGyroActivation();
                    if (resetSuccess)
                    {
                        Logger.Info("Gyro activation reset to always-on mode");
                    }
                    else
                    {
                        Logger.Error("Failed to reset gyro activation");
                    }
                    return;
                }

                // Map index to GyroActivationMode
                GyroActivationMode modeValue = gyroActivationMode switch
                {
                    0 => GyroActivationMode.Hold,
                    1 => GyroActivationMode.Toggle,
                    _ => GyroActivationMode.Hold
                };

                // Map index to GyroActivationButton (0=None is handled above)
                // 1=LB, 2=LT, 3=RB, 4=RT, 5=Y1, 6=Y2, 7=M2, 8=M3
                GyroActivationButton buttonValue = gyroActivationButton switch
                {
                    1 => GyroActivationButton.LB,
                    2 => GyroActivationButton.LT,
                    3 => GyroActivationButton.RB,
                    4 => GyroActivationButton.RT,
                    5 => GyroActivationButton.Y1,
                    6 => GyroActivationButton.Y2,
                    7 => GyroActivationButton.M2,
                    8 => GyroActivationButton.M3,
                    _ => GyroActivationButton.None
                };

                bool success = controller.SetGyroActivationButtons(modeValue, buttonValue);
                if (success)
                {
                    Logger.Info($"Gyro activation set: Mode={modeValue}, Button={buttonValue}");
                }
                else
                {
                    Logger.Error($"Failed to set gyro activation");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error applying gyro activation: {ex.Message}");
            }
        }

        /// <summary>
        /// Sets the gyro deadzone (1-100, suppresses small motions near center).
        /// </summary>
        public void SetGyroDeadzone(int deadzone)
        {
            gyroDeadzone = deadzone;
            ApplyAdvancedGyroSetting("Deadzone", deadzone, controller => controller.SetGyroDeadzone(deadzone));
        }

        /// <summary>
        /// Helper to apply advanced gyro settings with common logging/error handling.
        /// </summary>
        private void ApplyAdvancedGyroSetting(string settingName, int value, Func<LegionGoController, bool> applyAction)
        {
            // Debounced (keyed per setting) so dragging a gyro deadzone/sensitivity slider
            // doesn't open a fresh HID connection + MCU write on every tick — see
            // DebounceFirmwareWrite in LegionManager.FirmwareWriteDebounce.cs.
            DebounceFirmwareWrite($"Gyro:{settingName}", () =>
            {
                try
                {
                    using var controller = new LegionGoController();
                    if (!controller.Connect())
                    {
                        Logger.Warn($"Cannot apply gyro {settingName}: controller not connected");
                        return;
                    }

                    bool success = applyAction(controller);
                    if (success)
                    {
                        Logger.Info($"Gyro {settingName} set to {value}");
                    }
                    else
                    {
                        Logger.Error($"Failed to set gyro {settingName} to {value}");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error applying gyro {settingName}: {ex.Message}");
                }
            });
        }

    }
}
