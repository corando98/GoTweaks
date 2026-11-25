using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using NLog;

namespace XboxGamingBarHelper.ControllerEmulation.Viiper
{
    /// <summary>
    /// Removes ghost PnP entries left over from VIIPER hot-swaps. When the user
    /// switches from one virtual controller target to another, libviiper's
    /// RemoveDevice detaches the old USB device, but Windows keeps an
    /// "Present=False" entry in the PnP database for every (VID, PID, serial)
    /// it has ever seen. Steam's controller manager enumerates these ghosts
    /// and shows them as inactive controllers ("Switch Pro / PS4 separate
    /// from the one I'm emulating") — and worse, Steam Input can pin a game's
    /// rumble routing to the ghost's PnP path, so live rumble events stop
    /// reaching the active device.
    ///
    /// Strategy: maintain a static VID/PID map of every libviiper target we
    /// can produce. On helper start, sweep all known VID/PID combos for
    /// non-Present entries and remove them (cleans up ghosts that survived
    /// a previous helper session). On every hot-swap, after RemoveDevice
    /// succeeds, run the same sweep for the OLD target only (the one we
    /// just detached). This keeps the ghost list permanently empty going
    /// forward without affecting any currently-Present device.
    /// </summary>
    internal static class ViiperPnpCleanup
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        /// <summary>
        /// VID/PID combinations libviiper can present, indexed by the target
        /// alias the helper sends to AddDevice. Steam sub-device VIDs/PIDs
        /// (Valve 0x28DE / 0x12Fx range) are included so Steam-handheld
        /// hot-swaps clean their cached PIDs too.
        /// </summary>
        private static readonly Dictionary<string, (ushort vid, ushort pid)[]> TargetVidPids = new Dictionary<string, (ushort, ushort)[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["xbox360"]              = new[] { ((ushort)0x045E, (ushort)0x028E) },
            ["xboxelite2"]           = new[] { ((ushort)0x045E, (ushort)0x0B13) },
            ["xbox-one"]             = new[] { ((ushort)0x045E, (ushort)0x02D1) },
            ["xbox-elite"]           = new[] { ((ushort)0x045E, (ushort)0x0B00) },
            // xboxgip target removed from widget after 2026-05-19 RE
            // (see [[project_gip_definitive_walls_2026-05-19]]). Cleanup
            // still covers all PIDs the experimental builds used, so any
            // ghost PnP entries from those builds get swept on next start.
            ["xboxgip"]              = new[]
            {
                ((ushort)0x045E, (ushort)0x02D1),
                ((ushort)0x045E, (ushort)0x02D2),
                ((ushort)0x045E, (ushort)0x0B00),
            },
            ["dualshock4"]           = new[] { ((ushort)0x054C, (ushort)0x05C4) },
            ["dualsense"]            = new[] { ((ushort)0x054C, (ushort)0x0CE6) },
            ["ds5"]                  = new[] { ((ushort)0x054C, (ushort)0x0CE6) },
            ["dualsenseedge"]        = new[] { ((ushort)0x054C, (ushort)0x0DF2) },
            ["dualsense-edge"]       = new[] { ((ushort)0x054C, (ushort)0x0DF2) },
            ["switchpro"]            = new[] { ((ushort)0x057E, (ushort)0x2009) },
            ["joycon-left"]          = new[] { ((ushort)0x057E, (ushort)0x2006) },
            ["joycon-right"]         = new[] { ((ushort)0x057E, (ushort)0x2007) },
            ["ns2pro"]               = new[] { ((ushort)0x057E, (ushort)0x2069) },
            ["gordon"]               = new[] { ((ushort)0x28DE, (ushort)0x1102) },
            ["steam-controller"]     = new[] { ((ushort)0x28DE, (ushort)0x1102) },
            ["steam-controller-v1"]  = new[] { ((ushort)0x28DE, (ushort)0x1102) },
            ["steamcontroller-v1"]   = new[] { ((ushort)0x28DE, (ushort)0x1102) },
            // Steam-handheld family — all use Valve VID with handheld-specific PIDs.
            // steam-generic is the umbrella target; the sub-device PID overrides
            // narrow it down. We list every PID in the 0x12Fx range we ever
            // assign so a handheld hot-swap that changes only the PID also
            // cleans the previous handheld's ghost.
            ["steam-generic"]        = new[]
            {
                ((ushort)0x28DE, (ushort)0x12F0),   // generic
                ((ushort)0x28DE, (ushort)0x1205),   // steam-deck
                ((ushort)0x28DE, (ushort)0x12FA),   // msi-claw
                ((ushort)0x28DE, (ushort)0x12FB),   // legion-go-2
                ((ushort)0x28DE, (ushort)0x12FC),   // zotac-zone
                ((ushort)0x28DE, (ushort)0x12FD),   // rog-ally
                ((ushort)0x28DE, (ushort)0x12FE),   // legion-go
                ((ushort)0x28DE, (ushort)0x12FF),   // legion-go-s
            },
        };

        /// <summary>
        /// Removes any Present=False PnP entries matching the VID/PID set
        /// associated with the given target alias. Runs on a background
        /// thread; safe to call from hot-swap or startup code paths. No-ops
        /// if the helper isn't elevated (pnputil exits with access denied)
        /// or if there's nothing to clean.
        /// </summary>
        public static void CleanupGhostsForTarget(string targetAlias)
        {
            if (string.IsNullOrEmpty(targetAlias)) return;
            if (!TargetVidPids.TryGetValue(targetAlias, out var pairs)) return;

            System.Threading.Tasks.Task.Run(() =>
            {
                try { RunCleanup(pairs); }
                catch (Exception ex) { Logger.Debug($"ViiperPnpCleanup({targetAlias}) threw: {ex.Message}"); }
            });
        }

        /// <summary>
        /// Full sweep across every known VID/PID combination — used at helper
        /// startup to clear any ghost left over from a previous session.
        /// Also sweeps Present=True ViGEm-bus phantoms (Labs Xbox 360 pads
        /// from a prior helper instance that didn't release the ViGEmBus
        /// handle on exit — those show up alive in joy.cpl as duplicate
        /// "Xbox 360 Controller for Windows" entries delivering double input).
        /// </summary>
        /// <summary>
        /// Synchronous version of <see cref="CleanupAllKnownGhosts"/> for the VIIPER start
        /// paths: returns only after every removal is done, so nothing can race the
        /// AddDevice + usbip attach that follows. Listing takes ~0.2 s; removals are rare.
        /// </summary>
        public static void CleanupAllKnownGhostsBlocking()
        {
            try { RunCleanup(AllKnownVidPids()); }
            catch (Exception ex) { Logger.Debug($"ViiperPnpCleanup blocking sweep threw: {ex.Message}"); }
        }

        private static HashSet<(ushort vid, ushort pid)> AllKnownVidPids()
        {
            var all = new HashSet<(ushort vid, ushort pid)>();
            foreach (var kvp in TargetVidPids)
            {
                foreach (var pair in kvp.Value) all.Add(pair);
            }
            return all;
        }

        public static void CleanupAllKnownGhosts()
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                var all = AllKnownVidPids();
                try { RunCleanup(all); }
                catch (Exception ex) { Logger.Debug($"ViiperPnpCleanup startup sweep threw: {ex.Message}"); }

                // Both Present=True phantom sweeps (ViGEm and VIIPER) are
                // intentionally NOT in this async path — see the *Blocking
                // methods called synchronously from Program.cs at startup,
                // BEFORE VIIPER's deferred ApplyBackend kicks in. The async
                // path's slow pnputil calls (5s+ tail latency observed in
                // helper_2026-05-21_01.log) finish AFTER VIIPER adds its
                // active pad, leaving the phantom visible alongside the
                // active pad until the sweep finally catches up.
            });
        }

        /// <summary>
        /// Synchronous Present=True VIIPER phantom sweep. Call ONCE at helper
        /// startup, BEFORE any VIIPER manager is constructed / before any
        /// libviiper AddDevice call. Blocks for ~1-3 seconds depending on how
        /// many phantoms need removal (one pnputil call per phantom plus
        /// enumeration overhead). Returning before VIIPER's first AddDevice
        /// guarantees no race: any matching Present=True entry we find is a
        /// stale phantom from a prior helper session, never our active pad.
        /// </summary>
        public static void CleanupPresentViiperPhantomsBlocking()
        {
            try { CleanupPresentViiperPhantoms(); }
            catch (Exception ex) { Logger.Debug($"ViiperPnpCleanup VIIPER phantom sweep (blocking) threw: {ex.Message}"); }
        }

        /// <summary>
        /// Synchronous Present=True ViGEm phantom sweep. Same safety profile as
        /// <see cref="CleanupPresentViiperPhantomsBlocking"/>: call ONCE at
        /// helper startup BEFORE any backend creates virtual pads. Removes
        /// ViGEm-bus phantoms from prior helper sessions (e.g. Labs Xbox 360
        /// pads from before VIIPER was selected as the backend). The async
        /// equivalent in CleanupAllKnownGhosts had 5s+ tail latency on pnputil,
        /// completing AFTER VIIPER created its new pad — leaving the phantom
        /// visible alongside the active pad in the user-observed window.
        /// </summary>
        public static void CleanupPresentVigemPhantomsBlocking()
        {
            try { CleanupPresentVigemPhantoms(); }
            catch (Exception ex) { Logger.Debug($"ViiperPnpCleanup ViGEm phantom sweep (blocking) threw: {ex.Message}"); }
        }

        /// <summary>
        /// Removes Present=True VIIPER virtual phantoms that survived a previous
        /// crashed helper session. usbip-win2 leaves the PnP node Present=True
        /// even after the original libviiper attachment dies — Windows doesn't
        /// know the device is unreachable until something else tries to talk to
        /// it. Steam Input picks these up as live controllers and routes rumble
        /// to them, draining game feedback from the real active VIIPER pad.
        ///
        /// <para>Identification: every VIIPER virtual we publish is parented by
        /// usbip-win2's software USB bus, which Windows enumerates as a child
        /// of <c>ROOT\USB\&lt;n&gt;</c>. Real Xbox / DualSense / Switch Pro pads
        /// plugged into the user's USB ports are PCI-rooted (parent chain ends
        /// at <c>PCI\VEN_...</c>). Walking the parent chain via cfgmgr32 lets us
        /// tell the two apart unambiguously.</para>
        ///
        /// <para>Called at helper startup BEFORE VIIPER's own <c>service.AddDevice</c>
        /// runs, so any Present=True 045E:028E / 054C:* / etc. we find is by
        /// definition a stale phantom — not our active pad.</para>
        /// </summary>
        private static void CleanupPresentViiperPhantoms()
        {
            string raw = RunPnputil("/enum-devices /connected");
            if (string.IsNullOrEmpty(raw)) return;

            // Build the set of every VID:PID combination libviiper can produce.
            // Same source-of-truth as RunCleanup's ghost sweep, so adding a new
            // libviiper target to TargetVidPids auto-extends both passes.
            var targetVidPids = new HashSet<string>();
            foreach (var kvp in TargetVidPids)
            {
                foreach (var pair in kvp.Value)
                {
                    targetVidPids.Add($"VID_{pair.vid:X4}&PID_{pair.pid:X4}".ToUpperInvariant());
                }
            }

            var blocks = raw.Split(new[] { "\r\n\r\n", "\n\n" }, StringSplitOptions.RemoveEmptyEntries);
            var instanceIdRx = new Regex(@"Instance ID:\s+(\S+)", RegexOptions.IgnoreCase);

            var candidates = new List<string>();
            foreach (var block in blocks)
            {
                var idMatch = instanceIdRx.Match(block);
                if (!idMatch.Success) continue;
                string id = idMatch.Groups[1].Value.Trim();
                string upperId = id.ToUpperInvariant();

                // Only consider USB-level entries (the actual device root).
                // Removing the USB root cascades the IG_xx HID children, so we
                // don't need to enumerate them separately.
                if (!upperId.StartsWith("USB\\", StringComparison.Ordinal)) continue;

                bool matchesViiperTarget = false;
                foreach (var prefix in targetVidPids)
                {
                    if (upperId.Contains(prefix)) { matchesViiperTarget = true; break; }
                }
                if (!matchesViiperTarget) continue;

                // Critical safety check: only remove if the parent chain is
                // software-rooted (usbip-win2 bus, not real PCI hardware). A
                // real Xbox 360 controller plugged into the user's USB port
                // would match VID:045E&PID:028E but be PCI-rooted, and we must
                // not remove the user's actual hardware.
                if (!IsSoftwareBusRooted(id)) continue;

                candidates.Add(id);
            }

            if (candidates.Count == 0) return;
            Logger.Info($"ViiperPnpCleanup: removing {candidates.Count} Present=True VIIPER phantom pad(s) from a prior helper session");
            foreach (var id in candidates)
            {
                string output = RunPnputil($"/remove-device \"{id}\"");
                Logger.Info($"  pnputil /remove-device {id} → {(output ?? string.Empty).TrimEnd()}");
            }
        }

        /// <summary>
        /// Walks the PnP parent chain of <paramref name="deviceInstanceId"/> up
        /// to 16 levels looking for the <c>ROOT\USB\</c> software-bus enumerator
        /// that usbip-win2 publishes. Returns true if found — meaning this
        /// device sits on a virtual USB bus rather than real hardware. Returns
        /// false if we find <c>PCI\</c> first (real hardware) or hit an error.
        /// Conservative on lookup failures: a hardware-rooted device returns
        /// false, so we never accidentally remove a real controller.
        /// </summary>
        private static bool IsSoftwareBusRooted(string deviceInstanceId)
        {
            if (string.IsNullOrWhiteSpace(deviceInstanceId)) return false;
            try
            {
                if (CM_Locate_DevNodeW(out uint devInst, deviceInstanceId, 0) != 0) return false;

                var idBuf = new StringBuilder(512);
                for (int depth = 0; depth < 16; depth++)
                {
                    idBuf.Clear();
                    idBuf.EnsureCapacity(512);
                    if (CM_Get_Device_IDW(devInst, idBuf, idBuf.Capacity, 0) != 0) return false;
                    string nodeId = idBuf.ToString();
                    if (!string.IsNullOrEmpty(nodeId))
                    {
                        if (nodeId.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase)) return false;
                        if (nodeId.StartsWith("ROOT\\USB\\", StringComparison.OrdinalIgnoreCase)) return true;
                    }
                    if (CM_Get_Parent(out uint parent, devInst, 0) != 0) return false;
                    if (parent == 0 || parent == devInst) return false;
                    devInst = parent;
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"IsSoftwareBusRooted({deviceInstanceId}) threw: {ex.Message}");
            }
            return false;
        }

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceInstanceId, int flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_Device_IDW(uint devInst, StringBuilder buffer, int len, int flags);

        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Get_Parent(out uint parent, uint devInst, int flags);

        /// <summary>
        /// Removes Present=True ViGEm-bus phantom pads that survived from a
        /// prior helper session. We identify ViGEm pads (vs real Microsoft
        /// hardware) via DEVPKEY_Device_LocationInfo == "Virtual Gamepad
        /// Emulation Bus" — a string set by ViGEmBus itself, so it never
        /// matches a real Xbox 360 controller plugged into a real USB hub.
        ///
        /// This used to be safe to skip because ViGEmBus tracks per-process
        /// handles and is supposed to clean up its virtual pads when the
        /// owning process exits. In practice — especially during MSIX
        /// reinstall cycles where the helper exits abnormally — pads can
        /// linger, accumulating one phantom per upgrade cycle. Each phantom
        /// is a live virtual controller delivering input to apps, so the
        /// game/Xbox Game Bar sees double (or triple) input from a single
        /// physical press.
        ///
        /// Safe to run before any helper-owned ViGEm pad is created. Any
        /// helper-managed pad live at this point would only exist if a peer
        /// helper instance is also alive — which the single-instance mutex
        /// already prevents.
        /// </summary>
        private static void CleanupPresentVigemPhantoms()
        {
            // Enumerate ALL connected devices with location info; the ViGEm
            // pad's parent USB device (under which IG_XX interface groups
            // live) reports LocationInfo = "Virtual Gamepad Emulation Bus"
            // — a string ViGEmBus itself sets, so real Xbox 360 hardware
            // plugged into a real USB hub never matches.
            //
            // pnputil's /deviceid filter only accepts exact hardware IDs
            // (not wildcards), and /class HIDClass would miss the USB
            // parent entries, so we just enum the full connected list and
            // filter in-process. One-time call at helper startup; the
            // listing is on a fast in-memory PnP enumerator so wall-clock
            // cost is well under 1 s on Legion Go 2.
            string raw = RunPnputil("/enum-devices /connected /location");
            if (string.IsNullOrEmpty(raw)) return;

            // Each device record is a block of lines; blocks are separated
            // by blank lines. Parse Instance ID + Location Info per block.
            var blocks = raw.Split(new[] { "\r\n\r\n", "\n\n" }, StringSplitOptions.RemoveEmptyEntries);
            var instanceIdRx = new Regex(@"Instance ID:\s+(\S+)", RegexOptions.IgnoreCase);
            var locationRx = new Regex(@"Location Info:\s+(.+)", RegexOptions.IgnoreCase);

            var toRemove = new List<string>();
            foreach (var block in blocks)
            {
                var locMatch = locationRx.Match(block);
                if (!locMatch.Success) continue;
                string loc = locMatch.Groups[1].Value.Trim();
                if (loc.IndexOf("Virtual Gamepad Emulation Bus", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                var idMatch = instanceIdRx.Match(block);
                if (!idMatch.Success) continue;
                string id = idMatch.Groups[1].Value.Trim();

                // Belt-and-suspenders: only target the USB ROOT (parent) entries
                // for VID_045E&PID_028E (Xbox 360) or VID_054C&PID_05C4 (DS4).
                // Removing the USB root cascades the IG_XX children. Avoids
                // touching anything else even if some unrelated device ever
                // labels itself "Virtual Gamepad Emulation Bus".
                string upperId = id.ToUpperInvariant();
                bool isVigemXboxRoot =
                    upperId.StartsWith("USB\\VID_045E&PID_028E\\", StringComparison.Ordinal) ||
                    upperId.StartsWith("USB\\VID_054C&PID_05C4\\", StringComparison.Ordinal);
                if (!isVigemXboxRoot) continue;

                toRemove.Add(id);
            }

            if (toRemove.Count == 0) return;
            Logger.Info($"ViiperPnpCleanup: removing {toRemove.Count} Present=True ViGEm phantom pad(s) from a prior helper session");
            foreach (var id in toRemove)
            {
                string output = RunPnputil($"/remove-device \"{id}\"");
                Logger.Info($"  pnputil /remove-device {id} → {(output ?? string.Empty).TrimEnd()}");
            }
        }

        private static void RunCleanup(IEnumerable<(ushort vid, ushort pid)> pairs)
        {
            // /disconnected restricts the listing to Present=False entries —
            // exactly the ghosts we want to clean. Avoids the cost of parsing
            // every device on the system and avoids the risk of removing a
            // Present device by mistake.
            //
            // No /class filter: a single virtual Steam-handheld attachment
            // spawns PnP children in multiple device classes — the gamepad
            // HID is HIDClass, but the Steam-style keyboard/mouse companion
            // interfaces (VID_28DE PID_12Fx with &MI_00 / &MI_01 suffixes)
            // land in classes Keyboard and Mouse, the USB roots
            // (USB\VID_28DE&PID_12Fx\KEYBOARD) are class USB, and the Xbox
            // 360 / Xbox One Elite virtual devices are XnaComposite and
            // XboxComposite respectively. Scoping by /class HIDClass missed
            // every non-HID class, leaving Steam's controller list cluttered
            // with companion-device ghosts the helper had supposedly cleaned.
            string raw = RunPnputil("/enum-devices /disconnected");
            if (string.IsNullOrEmpty(raw)) return;

            var targetVidPids = new HashSet<string>();
            foreach (var (vid, pid) in pairs)
            {
                targetVidPids.Add($"VID_{vid:X4}&PID_{pid:X4}".ToUpperInvariant());
            }
            var instanceIdRegex = new Regex(@"Instance ID:\s+(\S+)", RegexOptions.IgnoreCase);

            var toRemove = new List<string>();
            foreach (Match m in instanceIdRegex.Matches(raw))
            {
                string instanceId = m.Groups[1].Value.Trim();
                string upperId = instanceId.ToUpperInvariant();
                foreach (var prefix in targetVidPids)
                {
                    if (!upperId.Contains(prefix)) continue;
                    // The VID/PIDs we emulate are the same ones real Xbox / PlayStation /
                    // Switch pads use. Never delete the PnP entry of the user's real,
                    // currently-unplugged controller — only ghosts of our virtual pads.
                    if (IsGhostOfRealHardware(instanceId))
                    {
                        Logger.Debug($"ViiperPnpCleanup: keeping {instanceId} (real hardware, not a VIIPER ghost)");
                        break;
                    }
                    toRemove.Add(instanceId);
                    break;
                }
            }

            if (toRemove.Count == 0) return;
            Logger.Info($"ViiperPnpCleanup: removing {toRemove.Count} ghost PnP entr{(toRemove.Count == 1 ? "y" : "ies")}");
            foreach (var id in toRemove)
            {
                // Serialize with usbip attach/detach, and re-check right before removing: the
                // list above is a snapshot, and a virtual pad re-attached since then reuses the
                // same instance ID. Removing a LIVE usbip2_ude device crashes the kernel.
                lock (UsbipCli.AttachSync)
                {
                    if (IsPresentNow(id))
                    {
                        Logger.Info($"  skipping {id}: present again (live device), not a ghost");
                        continue;
                    }
                    string output = RunPnputil($"/remove-device \"{id}\"");
                    Logger.Info($"  pnputil /remove-device {id} → {(output ?? string.Empty).TrimEnd()}");
                }
            }
        }

        /// <summary>True if the device node is currently in the live PnP tree (not a phantom).</summary>
        private static bool IsPresentNow(string instanceId)
        {
            try { return CM_Locate_DevNodeW(out _, instanceId, 0 /*CM_LOCATE_DEVNODE_NORMAL*/) == 0; }
            catch { return true; } // unsure -> don't remove
        }

        /// <summary>
        /// For a disconnected (Present=False) entry: follows its recorded parent chain
        /// (DEVPKEY_Device_Parent works on phantom nodes too) and returns true only when it
        /// was attached through real hardware (reaches PCI\ or ACPI\) rather than the
        /// usbip-win2 software bus (ROOT\USB\). Unresolvable chains return false, keeping the
        /// old behavior of sweeping them, since VIIPER ghosts are the common case.
        /// </summary>
        private static bool IsGhostOfRealHardware(string instanceId)
        {
            try
            {
                string id = instanceId;
                for (int depth = 0; depth < 16 && !string.IsNullOrEmpty(id); depth++)
                {
                    if (id.StartsWith("ROOT\\USB\\", StringComparison.OrdinalIgnoreCase)) return false;
                    if (id.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase) ||
                        id.StartsWith("ACPI\\", StringComparison.OrdinalIgnoreCase) ||
                        id.StartsWith("BTH", StringComparison.OrdinalIgnoreCase)) return true;

                    var node = Nefarius.Utilities.DeviceManagement.PnP.PnPDevice.GetDeviceByInstanceId(
                        id, Nefarius.Utilities.DeviceManagement.PnP.DeviceLocationFlags.Phantom);
                    id = node?.GetProperty<string>(Nefarius.Utilities.DeviceManagement.PnP.DevicePropertyKey.Device_Parent);
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"IsGhostOfRealHardware({instanceId}) lookup failed: {ex.Message}");
            }
            return false;
        }

        private static string RunPnputil(string args)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "pnputil.exe",
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
                    if (proc.ExitCode != 0 && !string.IsNullOrEmpty(stderr))
                    {
                        Logger.Debug($"pnputil exit={proc.ExitCode}: {stderr.TrimEnd()}");
                    }
                    return stdout;
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"pnputil exec failed: {ex.Message}");
                return string.Empty;
            }
        }
    }
}
