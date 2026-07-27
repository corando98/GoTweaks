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
        private int leftTriggerStart = 0;
        private int leftTriggerEnd = 0;
        private int rightTriggerStart = 0;
        private int rightTriggerEnd = 0;

        /// <summary>
        /// Sets the left trigger travel range.
        /// </summary>
        /// <param name="start">Percentage where trigger starts registering (0-100).</param>
        /// <param name="end">Percentage from end where trigger reports full (0-100).</param>
        public void SetLeftTriggerTravel(int start, int end)
        {
            DebounceFirmwareWrite("LeftTriggerTravel", () =>
            {
                try
                {
                    using var controller = new LegionGoController();
                    if (!controller.Connect())
                    {
                        Logger.Warn("Cannot set left trigger travel: controller not connected");
                        return;
                    }

                    bool success = controller.SetTriggerTravel(Controller.Left, start, end);
                    if (success)
                    {
                        leftTriggerStart = start;
                        leftTriggerEnd = end;
                        Logger.Info($"Left trigger travel set to start={start}%, end={end}%");
                    }
                    else
                    {
                        Logger.Error($"Failed to set left trigger travel");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error setting left trigger travel: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Sets the right trigger travel range.
        /// </summary>
        /// <param name="start">Percentage where trigger starts registering (0-100).</param>
        /// <param name="end">Percentage from end where trigger reports full (0-100).</param>
        public void SetRightTriggerTravel(int start, int end)
        {
            DebounceFirmwareWrite("RightTriggerTravel", () =>
            {
                try
                {
                    using var controller = new LegionGoController();
                    if (!controller.Connect())
                    {
                        Logger.Warn("Cannot set right trigger travel: controller not connected");
                        return;
                    }

                    bool success = controller.SetTriggerTravel(Controller.Right, start, end);
                    if (success)
                    {
                        rightTriggerStart = start;
                        rightTriggerEnd = end;
                        Logger.Info($"Right trigger travel set to start={start}%, end={end}%");
                    }
                    else
                    {
                        Logger.Error($"Failed to set right trigger travel");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error setting right trigger travel: {ex.Message}");
                }
            });
        }

    }
}
