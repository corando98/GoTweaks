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
        private int leftStickDeadzone = 4;
        private int rightStickDeadzone = 4;

        /// <summary>
        /// Sets the left stick deadzone (0-50%).
        /// </summary>
        public void SetLeftStickDeadzone(int percent)
        {
            DebounceFirmwareWrite("LeftStickDeadzone", () =>
            {
                try
                {
                    using var controller = new LegionGoController();
                    if (!controller.Connect())
                    {
                        Logger.Warn("Cannot set left stick deadzone: controller not connected");
                        return;
                    }

                    bool success = controller.SetStickDeadzone(Controller.Left, percent);
                    if (success)
                    {
                        leftStickDeadzone = percent;
                        Logger.Info($"Left stick deadzone set to {percent}%");
                    }
                    else
                    {
                        Logger.Error($"Failed to set left stick deadzone to {percent}%");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error setting left stick deadzone: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Sets the right stick deadzone (0-50%).
        /// </summary>
        public void SetRightStickDeadzone(int percent)
        {
            DebounceFirmwareWrite("RightStickDeadzone", () =>
            {
                try
                {
                    using var controller = new LegionGoController();
                    if (!controller.Connect())
                    {
                        Logger.Warn("Cannot set right stick deadzone: controller not connected");
                        return;
                    }

                    bool success = controller.SetStickDeadzone(Controller.Right, percent);
                    if (success)
                    {
                        rightStickDeadzone = percent;
                        Logger.Info($"Right stick deadzone set to {percent}%");
                    }
                    else
                    {
                        Logger.Error($"Failed to set right stick deadzone to {percent}%");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error setting right stick deadzone: {ex.Message}");
                }
            });
        }

    }
}
