using Microsoft.Win32;
using System;
using System.Diagnostics;

namespace Shared.Utilities
{
    public static partial class RTSSHelper
    {
        public static bool IsRunning()
        {
            var rtssProcessses = Process.GetProcessesByName("RTSS");
            if (rtssProcessses.Length == 0)
            {
                return false;
            }

            try
            {
                // Check if the first process has been running for at least 2 seconds
                return (DateTime.Now - rtssProcessses[0].StartTime).TotalSeconds >= 2.0f;
            }
            finally
            {
                // Dispose all processes
                foreach (var proc in rtssProcessses)
                {
                    proc.Dispose();
                }
            }
        }

        public static bool IsInstalled()
        {
            using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"Software\WOW6432Node\Unwinder\RTSS"))
            {
                return key != null;
            }
        }

        public static string InstalledLocation()
        {
            using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"Software\WOW6432Node\Unwinder\RTSS"))
            {
                if (key == null)
                {
                    return string.Empty;
                }

                return (string)key.GetValue("InstallDir");
            }
        }

        public static string ExecutablePath()
        {
            var installLocation = InstalledLocation();
            if (string.IsNullOrEmpty(installLocation))
            {
                return string.Empty;
            }
            return System.IO.Path.Combine(installLocation, "RTSS.exe");
        }
    }
}
