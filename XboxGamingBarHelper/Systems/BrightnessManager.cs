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

        internal static bool SetBrightness(int level)
        {
            try
            {
                level = Math.Max(0, Math.Min(100, level));
                bool appliedToAny = false;
                using (var searcher = new ManagementObjectSearcher("root\\WMI", "SELECT * FROM WmiMonitorBrightnessMethods"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        obj.InvokeMethod("WmiSetBrightness", new object[] { 1, level });
                        appliedToAny = true;
                    }
                }
                if (!appliedToAny)
                {
                    Logger.Warn("BrightnessManager: SetBrightness found no WmiMonitorBrightnessMethods instance");
                }
                return appliedToAny;
            }
            catch (Exception ex)
            {
                Logger.Error($"BrightnessManager: SetBrightness failed: {ex.Message}");
                return false;
            }
        }

        internal static bool IsSupported()
        {
            // Authoritative gate: is the built-in panel in the ACTIVE display config?
            // WmiMonitorBrightness keeps enumerating the internal panel's (inactive)
            // instance even when docked to an external-only display, so instance
            // presence alone is NOT enough — the WMI Set would silently no-op or
            // control nothing. QueryDisplayConfig is the same signal Windows' own
            // brightness slider uses (it grays out with external-only). (#50 docked.)
            bool? internalActive = XboxGamingBarHelper.Windows.User32.IsInternalPanelActive();
            if (internalActive == false)
            {
                return false; // built-in panel not in active config -> not controllable
            }

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
