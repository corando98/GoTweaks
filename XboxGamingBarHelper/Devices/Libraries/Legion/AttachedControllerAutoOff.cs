using Microsoft.Win32.SafeHandles;
using Nefarius.Utilities.DeviceManagement.PnP;
using NLog;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using XboxGamingBarHelper.ControllerEmulation.Viiper;
using XboxGamingBarHelper.Labs;
using XboxGamingBarHelper.Settings;
using XboxGamingBarHelper.Windows;

namespace XboxGamingBarHelper.Devices.Libraries.Legion
{
    /// <summary>
    /// Attached-controller auto-off. When enabled for the current dock state, and
    ///   - the Legion Go halves are attached to the sides of the system (at least one attached,
    ///     and no half is connected wirelessly while detached), and
    ///   - another real gamepad (Xbox, PlayStation, or any HID game controller, over USB or
    ///     Bluetooth) is connected,
    /// the Legion pad is turned off as far as Windows and games are concerned:
    ///   1. firmware register 04/0f goes vendor-exclusive (the pad emits no XInput/HID gamepad
    ///      input to the OS; the vendor channel stays up, so dock/battery state keeps flowing
    ///      and we notice the moment a half is detached), and
    ///   2. the VIIPER virtual pad is unplugged (it would otherwise keep mirroring the Legion).
    /// As soon as any condition stops holding (a half is detached and used wirelessly, the other
    /// gamepad goes away, the option is turned off, or the dock state flips to a disabled one),
    /// both are restored to exactly what the user had before.
    ///
    /// "Docked" = an external display is active (User32.IsExternalDisplayActive) — the only
    /// dock signal Windows exposes reliably; a Legion Go dock is a USB-C hub driving a TV.
    /// </summary>
    internal sealed class AttachedControllerAutoOff : IDisposable
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private const int PollIntervalMs = 3000;
        // Engage only after the condition holds on this many consecutive evaluations, so a
        // controller that blips during pairing or a dock handshake doesn't flap the Legion pad.
        // Releasing is immediate.
        private const int EngageAfterConsecutiveHits = 2;

        private readonly LegionManager legionManager;
        private readonly SettingsManager settingsManager;
        private readonly Timer timer;
        private readonly object evalLock = new object();

        // device interface path -> "is a real external gamepad" (computed once per path)
        private readonly Dictionary<string, bool> gamepadCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        private bool active;
        private bool osReportingDisabledBeforeEngage;
        private int consecutiveHits;
        private string lastReason = "";
        private bool disposed;

        public AttachedControllerAutoOff(LegionManager legionManager, SettingsManager settingsManager)
        {
            this.legionManager = legionManager;
            this.settingsManager = settingsManager;

            // React immediately to attach/detach and to the toggles; the timer covers external
            // gamepads and displays coming and going.
            Subscribe(legionManager?.ControllerDockedLeft);
            Subscribe(legionManager?.ControllerDockedRight);
            Subscribe(legionManager?.ControllerConnectedLeft);
            Subscribe(legionManager?.ControllerConnectedRight);
            Subscribe(settingsManager?.LegionAutoOffWhenDocked);
            Subscribe(settingsManager?.LegionAutoOffWhenUndocked);

            timer = new Timer(_ => Evaluate("poll"), null, PollIntervalMs, PollIntervalMs);
            Logger.Info("AttachedControllerAutoOff initialized");
        }

        private void Subscribe(System.ComponentModel.INotifyPropertyChanged property)
        {
            if (property == null) return;
            property.PropertyChanged += (s, e) =>
                ThreadPool.QueueUserWorkItem(_ => Evaluate("state-change"));
        }

        private void Evaluate(string trigger)
        {
            if (!Monitor.TryEnter(evalLock)) return; // an evaluation is already running
            try
            {
                if (disposed) return;

                bool want = ShouldBeOff(out string reason);
                if (want)
                {
                    consecutiveHits++;
                    if (!active && consecutiveHits >= EngageAfterConsecutiveHits)
                    {
                        Engage(reason);
                    }
                }
                else
                {
                    consecutiveHits = 0;
                    if (active)
                    {
                        Release(reason);
                    }
                }

                if (reason != lastReason)
                {
                    Logger.Debug($"AttachedControllerAutoOff [{trigger}]: want={want} active={active} ({reason})");
                    lastReason = reason;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"AttachedControllerAutoOff evaluate failed: {ex.Message}");
            }
            finally
            {
                Monitor.Exit(evalLock);
            }
        }

        private bool ShouldBeOff(out string reason)
        {
            if (legionManager == null || legionManager.LegionGoDetected?.Value != true)
            {
                reason = "no Legion Go controller";
                return false;
            }

            bool whenDocked = settingsManager?.LegionAutoOffWhenDocked?.Value ?? false;
            bool whenUndocked = settingsManager?.LegionAutoOffWhenUndocked?.Value ?? false;
            if (!whenDocked && !whenUndocked)
            {
                reason = "options off";
                return false;
            }

            bool docked = User32.IsExternalDisplayActive() ?? false;
            if (docked ? !whenDocked : !whenUndocked)
            {
                reason = docked ? "docked, docked option off" : "not docked, undocked option off";
                return false;
            }

            bool leftDocked = legionManager.ControllerDockedLeft?.Value ?? false;
            bool rightDocked = legionManager.ControllerDockedRight?.Value ?? false;
            bool leftConnected = legionManager.ControllerConnectedLeft?.Value ?? false;
            bool rightConnected = legionManager.ControllerConnectedRight?.Value ?? false;

            // Never touch a half that is detached and wirelessly connected — the user is
            // holding it. The firmware switch is shared by both halves, so this also blocks
            // turning off an attached half while the other one is in use detached.
            if ((leftConnected && !leftDocked) || (rightConnected && !rightDocked))
            {
                reason = $"a controller is detached and connected (L docked={leftDocked} conn={leftConnected}, R docked={rightDocked} conn={rightConnected})";
                return false;
            }
            if (!leftDocked && !rightDocked)
            {
                reason = "no controller attached to the sides";
                return false;
            }

            string external = FindExternalGamepad();
            if (external == null)
            {
                reason = $"{(docked ? "docked" : "not docked")}, controllers attached, no other gamepad";
                return false;
            }

            reason = $"{(docked ? "docked" : "not docked")}, controllers attached, other gamepad: {external}";
            return true;
        }

        private void Engage(string reason)
        {
            Logger.Info($"AttachedControllerAutoOff: turning attached Legion controllers OFF ({reason})");
            osReportingDisabledBeforeEngage = legionManager?.LegionOsReportingDisabled?.Value ?? false;
            active = true;
            ViiperEmulationManager.SetLegionAutoOff(true);
            bool ok = LegionButtonMonitor.Current?.SetPhysicalXInputEnabled(false) ?? false;
            Logger.Info($"AttachedControllerAutoOff: firmware pad output suppressed => {ok}");
        }

        private void Release(string reason)
        {
            Logger.Info($"AttachedControllerAutoOff: turning Legion controllers back ON ({reason})");
            active = false;
            // Restore the user's own "Disable OS Reporting" choice from before we engaged
            // (the property mirrors the firmware readback, so it reads true while we hold it).
            bool ok = LegionButtonMonitor.Current?.SetPhysicalXInputEnabled(!osReportingDisabledBeforeEngage) ?? false;
            Logger.Info($"AttachedControllerAutoOff: firmware pad output restored (OS reporting disabled={osReportingDisabledBeforeEngage}) => {ok}");
            ViiperEmulationManager.SetLegionAutoOff(false);
        }

        // ------------------------------------------------------------------ gamepad detection

        /// <summary>
        /// Returns a short description of the first real, non-Legion gamepad found, or null.
        /// Covers HID game controllers (PlayStation, Bluetooth Xbox, Switch, 8BitDo, generic)
        /// and XUSB devices (wired / wireless-adapter Xbox pads on xusb22/GIP).
        /// </summary>
        private string FindExternalGamepad()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string found = null;

            foreach (var iface in new[] { DeviceInterfaceIds.XUsbDevice, DeviceInterfaceIds.HidDevice })
            {
                bool isXusb = iface == DeviceInterfaceIds.XUsbDevice;
                for (int i = 0; i < 512; i++)
                {
                    string path, instanceId;
                    if (!Devcon.FindByInterfaceGuid(iface, out path, out instanceId, i, true)) break;
                    if (string.IsNullOrEmpty(path)) continue;
                    seen.Add(path);
                    if (found != null) continue; // keep enumerating so the cache prune below is accurate

                    if (!gamepadCache.TryGetValue(path, out bool isGamepad))
                    {
                        isGamepad = IsExternalGamepad(path, instanceId, isXusb);
                        gamepadCache[path] = isGamepad;
                        if (isGamepad) Logger.Info($"AttachedControllerAutoOff: external gamepad detected: {instanceId}");
                    }
                    if (isGamepad) found = instanceId;
                }
            }

            // Forget devices that went away so a re-plugged one is re-examined.
            if (gamepadCache.Count > seen.Count)
            {
                var stale = new List<string>();
                foreach (var key in gamepadCache.Keys) if (!seen.Contains(key)) stale.Add(key);
                foreach (var key in stale) gamepadCache.Remove(key);
            }
            return found;
        }

        private static bool IsExternalGamepad(string path, string instanceId, bool isXusb)
        {
            if (!IsRealNonLegionDevice(path, instanceId)) return false;
            if (isXusb) return true;
            return IsHidGameController(path);
        }

        /// <summary>
        /// Walks the PnP parent chain. Rejects the Legion's own pads (any Lenovo VID_17EF node)
        /// and virtual pads (VIIPER/usbip under ROOT\USB, ViGEm/Steam under ROOT\SYSTEM, any
        /// software-root node). Real USB and Bluetooth devices hang off PCI under ROOT\ACPI_HAL.
        /// </summary>
        private static bool IsRealNonLegionDevice(string path, string instanceId)
        {
            try
            {
                IPnPDevice device = PnPDevice.GetDeviceByInterfaceId(path);
                for (int depth = 0; device != null && depth < 16; depth++)
                {
                    string id = device.InstanceId ?? "";
                    if (id.IndexOf("VID_17EF", StringComparison.OrdinalIgnoreCase) >= 0) return false;
                    if (id.StartsWith("SWD\\", StringComparison.OrdinalIgnoreCase)) return false;
                    if (id.StartsWith("ROOT\\", StringComparison.OrdinalIgnoreCase) &&
                        !id.StartsWith("ROOT\\ACPI_HAL", StringComparison.OrdinalIgnoreCase)) return false;
                    if (id.StartsWith("HTREE\\", StringComparison.OrdinalIgnoreCase)) break;
                    device = device.Parent;
                }
                return true;
            }
            catch (Exception ex)
            {
                Logger.Debug($"AttachedControllerAutoOff: parent walk failed for {instanceId}: {ex.Message}");
                return false;
            }
        }

        private const ushort HID_USAGE_PAGE_GENERIC = 0x01;
        private const ushort HID_USAGE_JOYSTICK = 0x04;
        private const ushort HID_USAGE_GAMEPAD = 0x05;
        private const ushort HID_USAGE_MULTI_AXIS = 0x08;

        private static bool IsHidGameController(string path)
        {
            // Zero access rights: enough for the preparsed data, works on devices other
            // processes hold open exclusively.
            using (SafeFileHandle handle = CreateFile(path, 0, 3 /*FILE_SHARE_READ|WRITE*/, IntPtr.Zero, 3 /*OPEN_EXISTING*/, 0, IntPtr.Zero))
            {
                if (handle.IsInvalid) return false;
                if (!HidD_GetPreparsedData(handle, out IntPtr preparsed)) return false;
                try
                {
                    if (HidP_GetCaps(preparsed, out HIDP_CAPS caps) != HIDP_STATUS_SUCCESS) return false;
                    return caps.UsagePage == HID_USAGE_PAGE_GENERIC &&
                           (caps.Usage == HID_USAGE_JOYSTICK || caps.Usage == HID_USAGE_GAMEPAD || caps.Usage == HID_USAGE_MULTI_AXIS);
                }
                finally
                {
                    HidD_FreePreparsedData(preparsed);
                }
            }
        }

        private const int HIDP_STATUS_SUCCESS = 0x00110000;

        [StructLayout(LayoutKind.Sequential)]
        private struct HIDP_CAPS
        {
            public ushort Usage;
            public ushort UsagePage;
            public ushort InputReportByteLength;
            public ushort OutputReportByteLength;
            public ushort FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
            public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes;
            public ushort NumberInputButtonCaps;
            public ushort NumberInputValueCaps;
            public ushort NumberInputDataIndices;
            public ushort NumberOutputButtonCaps;
            public ushort NumberOutputValueCaps;
            public ushort NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps;
            public ushort NumberFeatureValueCaps;
            public ushort NumberFeatureDataIndices;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode,
            IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_GetPreparsedData(SafeFileHandle hidDeviceObject, out IntPtr preparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern int HidP_GetCaps(IntPtr preparsedData, out HIDP_CAPS capabilities);

        // ------------------------------------------------------------------ lifetime

        public void Dispose()
        {
            lock (evalLock)
            {
                if (disposed) return;
                disposed = true;
                timer.Dispose();
                if (active)
                {
                    try { Release("helper shutting down"); }
                    catch (Exception ex) { Logger.Warn($"AttachedControllerAutoOff release on dispose failed: {ex.Message}"); }
                }
            }
        }
    }
}
