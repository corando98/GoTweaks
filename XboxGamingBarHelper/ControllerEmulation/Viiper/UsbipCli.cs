using NLog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace XboxGamingBarHelper.ControllerEmulation.Viiper
{
    /// <summary>
    /// Thin wrapper over usbip-win2's usbip.exe, used to explicitly attach libviiper's
    /// exported devices to the local UDE bus (and detach them on teardown).
    ///
    /// WHY THIS EXISTS: libviiper's viiper_device_add is *documented* to auto-attach the
    /// device to the local UDE driver, but on some usbip-win2 UDE installs that internal
    /// attach silently no-ops — viiper_device_add returns success and the USBIP server
    /// exports the device on 127.0.0.1:3241, yet no child ever appears under the UDE host
    /// controller and "usbip.exe port" stays empty, so no gamepad reaches Windows.
    /// Driving the standard usbip client against libviiper's own loopback server lands it.
    ///
    /// Verified on-device 2026-07-08 (LeGo2, usbip-win2 0.9.7.8): the internal auto-attach
    /// produced nothing (empty UDE host-controller child list, empty "usbip.exe port"), but
    ///   usbip.exe -t 3241 attach -r 127.0.0.1 -b 1-1
    /// plugged in the emulated DualSense and gamepads worked. This class reproduces that
    /// call after every device add. It is idempotent (devices already attached are skipped),
    /// so on machines where the internal auto-attach *does* work it is a harmless no-op
    /// rather than a duplicate attachment.
    /// </summary>
    internal static class UsbipCli
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        // Must match ViiperService.Initialize's listen address.
        private const string Host = "127.0.0.1";
        private const int Port = 3241;

        private static readonly string[] ExePaths =
        {
            @"C:\Program Files\USBip\usbip.exe",
            @"C:\Program Files (x86)\USBip\usbip.exe",
        };

        private static string ResolveExe()
        {
            foreach (var p in ExePaths)
            {
                if (File.Exists(p)) return p;
            }
            return null;
        }

        /// <summary>
        /// Attaches every device libviiper is exporting on its loopback USBIP server that
        /// isn't already imported into the local UDE bus. Best-effort: logs and returns
        /// quietly if usbip.exe is missing or the CLI misbehaves.
        /// </summary>
        public static void AttachExportedDevices()
        {
            string exe = ResolveExe();
            if (exe == null)
            {
                Logger.Warn("usbip.exe not found; cannot attach VIIPER device to the UDE bus.");
                return;
            }

            // The server registers the device synchronously inside viiper_device_add, but
            // allow a couple of short retries in case the export list lags right after add.
            for (int attempt = 0; attempt < 3; attempt++)
            {
                var exported = ListExportedBusIds(exe);
                if (exported.Count == 0)
                {
                    System.Threading.Thread.Sleep(200);
                    continue;
                }

                var attached = ListAttachedBusIds(exe);
                int attachedNow = 0;
                foreach (var busId in exported)
                {
                    if (attached.Contains(busId))
                    {
                        Logger.Debug($"usbip: {busId} already attached, skipping.");
                        continue;
                    }
                    if (Attach(exe, busId)) attachedNow++;
                }
                Logger.Info($"usbip: exported={exported.Count}, newly attached={attachedNow}.");
                return;
            }
            Logger.Warn("usbip: libviiper exported no devices after add (attach skipped).");
        }

        /// <summary>Detaches every UDE port imported from libviiper's loopback server. Best-effort.</summary>
        public static void DetachAll()
        {
            string exe = ResolveExe();
            if (exe == null) return;

            string output = Run(exe, $"-t {Port} port");
            if (string.IsNullOrEmpty(output)) return;

            // Walk "Port NN:" blocks, collect the numbers of blocks referencing our loopback
            // server so we never detach an unrelated usbip import the user set up themselves.
            var ports = new List<string>();
            string currentPort = null;
            bool currentIsOurs = false;
            foreach (var raw in output.Split('\n'))
            {
                var pm = Regex.Match(raw, @"Port\s+(\d+)\s*:");
                if (pm.Success)
                {
                    if (currentPort != null && currentIsOurs) ports.Add(currentPort);
                    currentPort = pm.Groups[1].Value;
                    currentIsOurs = false;
                }
                if (raw.IndexOf(Host, StringComparison.OrdinalIgnoreCase) >= 0) currentIsOurs = true;
            }
            if (currentPort != null && currentIsOurs) ports.Add(currentPort);

            foreach (var p in ports)
            {
                string o = Run(exe, $"detach -p {p}");
                Logger.Info($"usbip detach -p {p} -> {(o ?? string.Empty).Trim()}");
            }
        }

        private static List<string> ListExportedBusIds(string exe)
        {
            var result = new List<string>();
            string output = Run(exe, $"-t {Port} list -r {Host}");
            if (string.IsNullOrEmpty(output)) return result;
            // Exported lines look like: "    1-1    : Sony Corp. : DualSense ... (054c:0df2)"
            foreach (var line in output.Split('\n'))
            {
                var m = Regex.Match(line, @"^\s*(\d+-\d+)\s*:");
                if (m.Success) result.Add(m.Groups[1].Value);
            }
            return result;
        }

        private static HashSet<string> ListAttachedBusIds(string exe)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string output = Run(exe, $"-t {Port} port");
            if (string.IsNullOrEmpty(output)) return set;
            // usbip-win2 port output references the remote busid, e.g.
            //   "1-1 -> usbip://127.0.0.1:3241/1-1"
            // Only trust busids on lines that mention our loopback server, so an unrelated
            // usbip import is never mistaken for one of ours (which would skip a real attach).
            foreach (var line in output.Split('\n'))
            {
                if (line.IndexOf(Host, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var m = Regex.Match(line, @"(\d+-\d+)");
                if (m.Success) set.Add(m.Groups[1].Value);
            }
            return set;
        }

        private static bool Attach(string exe, string busId)
        {
            string output = Run(exe, $"-t {Port} attach -r {Host} -b {busId}");
            // usbip attach prints nothing meaningful on success; an "error" line signals failure.
            bool failed = output != null && output.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0;
            if (failed)
            {
                Logger.Warn($"usbip attach -b {busId} failed: {output.Trim()}");
                return false;
            }
            Logger.Info($"usbip attach -b {busId} succeeded.");
            return true;
        }

        private static string Run(string exe, string args)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                using (var proc = Process.Start(psi))
                {
                    if (proc == null) return string.Empty;
                    string stdout = proc.StandardOutput.ReadToEnd();
                    string stderr = proc.StandardError.ReadToEnd();
                    proc.WaitForExit(15000);
                    return (stdout ?? string.Empty) + "\n" + (stderr ?? string.Empty);
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"usbip exec failed ({args}): {ex.Message}");
                return string.Empty;
            }
        }
    }
}
