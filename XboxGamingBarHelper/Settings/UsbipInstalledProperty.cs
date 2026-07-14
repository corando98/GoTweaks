using Microsoft.Win32;
using Shared.Enums;
using System;
using System.IO;
using System.ServiceProcess;
using System.Threading.Tasks;
using XboxGamingBarHelper.Core;

namespace XboxGamingBarHelper.Settings
{
    /// <summary>
    /// Detects whether usbip-win2 is installed on the system.
    /// Required prerequisite for the VIIPER emulation backend.
    /// </summary>
    internal class UsbipInstalledProperty : HelperProperty<bool, SettingsManager>
    {
        // Service names installed ONLY by usbip-win2. Every entry must be specific to
        // usbip-win2 — see the false-positive note below.
        private static readonly string[] ServiceKeyNames = new[]
        {
            "usbip2_ude",      // primary — USB Device Emulation driver (usbip-win2 UDE releases)
            "usbip2_filter",   // upper filter driver
            "usbip2vhci",      // older usbip-win2 vhci-based releases
            // DO NOT add generic names like "mausbip" or bare "VHCI". They are NOT
            // usbip-win2 — they exist on stock machines from unrelated components, and
            // matching them false-positived Detect()=>true on a system with NO usbip-win2.
            // That let the VIIPER backend run with no real driver: HidHide hid the physical
            // pad, the virtual pad never mounted (no vhci to surface it), and the controller
            // went dead. usbip.exe (below) + the usbip2_* drivers are the only valid signals.
        };

        private static readonly string[] BinaryPaths = new[]
        {
            @"C:\Program Files\USBip\usbip.exe",
            @"C:\Program Files (x86)\USBip\usbip.exe",
        };

        public UsbipInstalledProperty(SettingsManager inManager)
            : base(Detect(), null, Function.Viiper_UsbipInstalled, inManager)
        {
            Logger.Info($"usbip-win2 installed: {Value}");
        }

        /// <summary>
        /// Re-runs detection and pushes the new state. Call after a user-initiated install.
        /// </summary>
        public void Refresh()
        {
            var detected = Detect();
            if (detected != Value)
            {
                SetValue(detected);
                Logger.Info($"usbip-win2 detection refreshed: {detected}");
            }
        }

        private static bool Detect()
        {
            // Any one of these signals is enough. We're intentionally permissive so partial
            // installs or newer release builds (which might rename one service) still pass.
            //
            // Diagnostic note: registry-key existence proves the service is *registered*, not
            // *running*. Walk every signal (don't short-circuit) and log each at Info level so
            // production logs show which detection paths fired and the runtime state of any
            // services we find. A service registered but Stopped/Disabled is a strong hint
            // that VIIPER's USBIP client side isn't actually wired up — see issue #79
            // vvalente30, where "no input from VIIPER" coincided with no visibility into
            // whether the USBIP driver was loaded.
            bool found = false;
            try
            {
                foreach (var name in ServiceKeyNames)
                {
                    using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name))
                    {
                        if (key != null)
                        {
                            string state = QueryServiceState(name);
                            Logger.Info($"usbip-win2 service registered: '{name}' state={state}");
                            found = true;
                        }
                    }
                }
                foreach (var path in BinaryPaths)
                {
                    if (File.Exists(path))
                    {
                        Logger.Info($"usbip-win2 binary present: {path}");
                        found = true;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"UsbipInstalled detection failed: {ex.Message}");
            }
            return found;
        }

        /// <summary>
        /// Returns "Status/StartType" (e.g., "Running/Manual", "Stopped/Disabled") for the
        /// given driver service. "Stopped/Disabled" is the case we most want to surface: a
        /// service registered in the SCM but configured to never start, which silently
        /// breaks USBIP without any error from libviiper.
        ///
        /// SCM queries can block if the service control manager is unresponsive (rare,
        /// but observed during heavy boot contention). Run on a worker task with a
        /// short timeout so a hung SCM never delays helper startup — diagnostic logs
        /// are valuable, but never at the cost of pushing back the helper's main work.
        /// </summary>
        private static string QueryServiceState(string serviceName)
        {
            const int TimeoutMs = 1500;
            try
            {
                var task = Task.Run(() =>
                {
                    using (var sc = new ServiceController(serviceName))
                    {
                        return $"{sc.Status}/{sc.StartType}";
                    }
                });
                if (!task.Wait(TimeoutMs))
                {
                    return "scm-timeout";
                }
                return task.Result;
            }
            catch (AggregateException ae) when (ae.InnerException is InvalidOperationException)
            {
                // SCM doesn't know the service even though the registry key exists —
                // partial install or rename in flight.
                return "not-in-scm";
            }
            catch (Exception ex)
            {
                return $"query-error:{ex.GetType().Name}";
            }
        }
    }
}
