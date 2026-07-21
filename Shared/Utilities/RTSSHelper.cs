using Microsoft.Win32;
using System;
using System.Diagnostics;

namespace Shared.Utilities
{
    public static partial class RTSSHelper
    {
        // IsInstalled/IsRunning are called every tick from both RTSSManager.Update() (helper
        // main loop, ~1s) and SystemManager.GetRunningGame() - a full process-table scan plus
        // a registry read, twice a second, forever, regardless of whether RTSS is even
        // installed. Cache both so that's no longer the case; TTLs match the existing
        // LosslessScalingManager install/running-cache convention (10s / ~1s).
        private static readonly object _cacheLock = new object();
        private static bool _installedCache;
        private static DateTime _installedCacheUtc = DateTime.MinValue;
        private static readonly TimeSpan InstalledCacheTtl = TimeSpan.FromSeconds(10);

        private static bool _runningCache;
        private static DateTime _runningCacheUtc = DateTime.MinValue;
        private static readonly TimeSpan RunningCacheTtl = TimeSpan.FromMilliseconds(900);

        public static bool IsRunning()
        {
            lock (_cacheLock)
            {
                var now = DateTime.UtcNow;
                if (now - _runningCacheUtc < RunningCacheTtl)
                {
                    return _runningCache;
                }

                // Not installed -> can never be running. Skip the process-table scan (the
                // actually expensive part) entirely and lean on the also-cached install check.
                bool running = IsInstalledLocked(now) && IsRunningUncached();
                _runningCache = running;
                _runningCacheUtc = now;
                return running;
            }
        }

        private static bool IsRunningUncached()
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
            lock (_cacheLock)
            {
                return IsInstalledLocked(DateTime.UtcNow);
            }
        }

        // Caller must already hold _cacheLock.
        private static bool IsInstalledLocked(DateTime nowUtc)
        {
            if (nowUtc - _installedCacheUtc < InstalledCacheTtl)
            {
                return _installedCache;
            }

            _installedCache = IsInstalledUncached();
            _installedCacheUtc = nowUtc;
            return _installedCache;
        }

        private static bool IsInstalledUncached()
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
