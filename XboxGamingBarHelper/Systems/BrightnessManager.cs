using System;
using System.Management;
using NLog;

namespace XboxGamingBarHelper.Systems
{
    internal static class BrightnessManager
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        internal static int GetBrightness()
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher("root\\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness"))
                {
                    foreach (var obj in searcher.Get())
                    {
                        return Convert.ToInt32(obj["CurrentBrightness"]);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"BrightnessManager: GetBrightness failed: {ex.Message}");
            }
            return 50;
        }

        internal static void SetBrightness(int level)
        {
            try
            {
                level = Math.Max(0, Math.Min(100, level));
                using (var searcher = new ManagementObjectSearcher("root\\WMI", "SELECT * FROM WmiMonitorBrightnessMethods"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        obj.InvokeMethod("WmiSetBrightness", new object[] { 1, level });
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"BrightnessManager: SetBrightness failed: {ex.Message}");
            }
        }

        internal static bool IsSupported()
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher("root\\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness"))
                {
                    foreach (var obj in searcher.Get())
                    {
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }
    }
}
