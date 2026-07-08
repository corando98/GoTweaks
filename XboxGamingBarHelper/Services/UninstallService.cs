using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.Win32;
using Nefarius.Drivers.HidHide;
using NLog;

namespace XboxGamingBarHelper.Services
{
    /// <summary>
    /// One-shot "--uninstall" mode: restores everything GoTweaks changed on the
    /// system so an uninstall actually leaves the machine the way we found it
    /// (inspired by Handheld Companion 0.31's OEM/driver-stack restoration).
    ///
    /// Always: stop peer helpers, clear our HidHide rules (the #1 "GoTweaks
    /// broke my controller after uninstall" leftover), sweep phantom virtual
    /// pads, remove the scheduled task + deployed helper copy.
    ///
    /// With "--remove-drivers": additionally uninstall PawnIO, ViGEmBus and
    /// usbip-win2 via their registered uninstallers. Opt-in because those
    /// drivers are shared infrastructure — RTSS/FanControl use PawnIO,
    /// DS4Windows uses ViGEmBus — and ripping them out under another tool is
    /// worse than leaving them.
    /// </summary>
    internal static class UninstallService
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public static void Run(bool removeDrivers)
        {
            Logger.Info($"=== GoTweaks uninstall restoration (removeDrivers={removeDrivers}) ===");

            Step("stop peer helper processes", StopPeerHelpers);
            Step("restore HidHide state", RestoreHidHide);
            Step("sweep phantom virtual pads", () =>
            {
                ControllerEmulation.Viiper.ViiperPnpCleanup.CleanupPresentViiperPhantomsBlocking();
                ControllerEmulation.Viiper.ViiperPnpCleanup.CleanupPresentVigemPhantomsBlocking();
                ControllerEmulation.Viiper.ViiperPnpCleanup.CleanupAllKnownGhosts();
            });
            Step("remove scheduled task + deployed helper", ElevationBootstrapper.Uninstall);

            if (removeDrivers)
            {
                Step("uninstall PawnIO", () => UninstallByDisplayName("PawnIO"));
                Step("uninstall ViGEmBus", () => UninstallByDisplayName("ViGEm Bus Driver"));
                Step("uninstall usbip-win2", () => UninstallByDisplayName("USBip"));
            }

            Logger.Info("=== Uninstall restoration complete ===");
        }

        private static void Step(string name, Action action)
        {
            try
            {
                Logger.Info($"Uninstall step: {name}");
                action();
            }
            catch (Exception ex)
            {
                Logger.Warn($"Uninstall step '{name}' failed (continuing): {ex.Message}");
            }
        }

        private static void StopPeerHelpers()
        {
            int self = Process.GetCurrentProcess().Id;
            foreach (var p in Process.GetProcessesByName("XboxGamingBarHelper"))
            {
                using (p)
                {
                    if (p.Id == self) continue;
                    try
                    {
                        Logger.Info($"Stopping helper process PID {p.Id}");
                        p.Kill();
                        p.WaitForExit(5000);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"Could not stop PID {p.Id}: {ex.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// Removes the HidHide entries GoTweaks created: blocked Legion
        /// controller instance IDs (VID 17EF tablets, VID 1A86 Go S) and any
        /// application-path registrations pointing at our install. If the
        /// blocked list ends up empty, deactivate cloaking so a hidden
        /// controller can never survive us. HidHide itself stays installed
        /// (shared with DS4Windows etc.) unless --remove-drivers.
        /// </summary>
        private static void RestoreHidHide()
        {
            HidHideControlService service;
            try
            {
                service = new HidHideControlService();
                if (!service.IsInstalled)
                {
                    Logger.Info("HidHide not installed - nothing to restore");
                    return;
                }
            }
            catch (Exception ex)
            {
                Logger.Info($"HidHide API unavailable ({ex.Message}) - nothing to restore");
                return;
            }

            var blocked = (service.BlockedInstanceIds ?? Enumerable.Empty<string>()).ToList();
            foreach (var id in blocked)
            {
                string lower = id?.ToLowerInvariant() ?? "";
                if (lower.Contains("vid_17ef") || lower.Contains("vid_1a86"))
                {
                    try
                    {
                        service.RemoveBlockedInstanceId(id);
                        Logger.Info($"HidHide: unblocked {id}");
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"HidHide: failed to unblock {id}: {ex.Message}");
                    }
                }
            }

            var apps = (service.ApplicationPaths ?? Enumerable.Empty<string>()).ToList();
            foreach (var path in apps)
            {
                string lower = path?.ToLowerInvariant() ?? "";
                if (lower.Contains("gotweaks") || lower.Contains("xboxgamingbar") || lower.Contains("playandbuildcustom"))
                {
                    try
                    {
                        service.RemoveApplicationPath(path);
                        Logger.Info($"HidHide: deregistered app {path}");
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"HidHide: failed to deregister {path}: {ex.Message}");
                    }
                }
            }

            try
            {
                var remaining = (service.BlockedInstanceIds ?? Enumerable.Empty<string>()).ToList();
                if (remaining.Count == 0 && service.IsActive)
                {
                    service.IsActive = false;
                    Logger.Info("HidHide: cloaking deactivated (blocked list empty)");
                }
                else if (remaining.Count > 0)
                {
                    Logger.Info($"HidHide: left active - {remaining.Count} non-GoTweaks blocked entr{(remaining.Count == 1 ? "y" : "ies")} remain");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"HidHide: final state check failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Runs the registered uninstaller for an installed product whose
        /// DisplayName starts with <paramref name="displayNamePrefix"/>.
        /// Prefers QuietUninstallString; falls back to UninstallString with a
        /// best-effort silent flag (msiexec → /qn, InnoSetup/other → /VERYSILENT).
        /// </summary>
        private static void UninstallByDisplayName(string displayNamePrefix)
        {
            var uninstallRoots = new[]
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
            };

            foreach (var root in uninstallRoots)
            {
                using (var key = Registry.LocalMachine.OpenSubKey(root))
                {
                    if (key == null) continue;
                    foreach (var subName in key.GetSubKeyNames())
                    {
                        using (var sub = key.OpenSubKey(subName))
                        {
                            string displayName = sub?.GetValue("DisplayName") as string;
                            if (string.IsNullOrEmpty(displayName) ||
                                !displayName.StartsWith(displayNamePrefix, StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            string quiet = sub.GetValue("QuietUninstallString") as string;
                            string loud = sub.GetValue("UninstallString") as string;
                            string command = !string.IsNullOrEmpty(quiet) ? quiet : AppendSilentFlag(loud);
                            if (string.IsNullOrEmpty(command))
                            {
                                Logger.Warn($"'{displayName}' found but has no uninstall string");
                                return;
                            }

                            Logger.Info($"Uninstalling '{displayName}': {command}");
                            RunCommand(command);
                            return;
                        }
                    }
                }
            }

            Logger.Info($"'{displayNamePrefix}' not found in the uninstall registry - skipping");
        }

        private static string AppendSilentFlag(string uninstallString)
        {
            if (string.IsNullOrEmpty(uninstallString)) return null;
            if (uninstallString.IndexOf("msiexec", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // /I in registered strings means "modify"; force uninstall + quiet.
                return uninstallString.Replace("/I", "/X").Replace("/i", "/X") + " /qn /norestart";
            }
            return uninstallString + " /VERYSILENT /SUPPRESSMSGBOXES /NORESTART";
        }

        private static void RunCommand(string command)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c {command}",
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden,
                };
                using (var p = Process.Start(psi))
                {
                    if (p != null && !p.WaitForExit(180000))
                    {
                        Logger.Warn("Uninstaller timed out after 3 minutes");
                        try { p.Kill(); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Uninstaller run failed: {ex.Message}");
            }
        }
    }
}
