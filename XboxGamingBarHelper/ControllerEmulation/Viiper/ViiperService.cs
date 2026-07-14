using NLog;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace XboxGamingBarHelper.ControllerEmulation.Viiper
{
    /// <summary>
    /// Manages the libviiper lifecycle: initialization, bus/device management, and shutdown.
    /// Ported from ViiperController reference implementation.
    /// </summary>
    internal sealed class ViiperService : IDisposable
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private bool _initialized;
        private readonly object _lock = new object();

        // Keep a reference to each delegate to prevent GC from collecting it while the
        // native side still holds the function pointer.
        private readonly Dictionary<Tuple<uint, uint>, LibViiper.FeedbackCallback> _feedbackDelegates
            = new Dictionary<Tuple<uint, uint>, LibViiper.FeedbackCallback>();

        /// <summary>
        /// Fired when the emulated device receives an output report (rumble, LEDs, etc.)
        /// from the consuming application. Raised on a native thread — handlers should
        /// not block.
        /// </summary>
        public event Action<uint, uint, byte[]> FeedbackReceived;

        public bool IsInitialized
        {
            get { lock (_lock) return _initialized; }
        }

        /// <summary>
        /// Initializes the USBIP server on the given address.
        /// Default address is localhost:3241 — we don't expose beyond loopback.
        /// </summary>
        public bool Initialize(string listenAddr = "127.0.0.1:3241")
        {
            lock (_lock)
            {
                if (_initialized) return true;
                int result;
                try
                {
                    result = LibViiper.viiper_init(listenAddr);
                }
                catch (DllNotFoundException ex)
                {
                    Logger.Error($"libviiper.dll not found: {ex.Message}");
                    return false;
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "viiper_init threw unexpectedly");
                    return false;
                }
                if (result != 0)
                {
                    Logger.Error($"viiper_init failed: {LibViiper.GetLastError()}");
                    return false;
                }
                _initialized = true;
                Logger.Info($"VIIPER USBIP server started on {listenAddr}");
                return true;
            }
        }

        public bool CreateBus(uint busId)
        {
            var result = LibViiper.viiper_bus_create(busId);
            if (result != 0)
            {
                string err = LibViiper.GetLastError() ?? string.Empty;
                // "bus number N already allocated" means a previous CreateBus
                // (this session) already established it — treat as success so
                // re-entrant Start() calls don't think the service is broken
                // and tear it down. The bus is genuinely usable; libviiper just
                // rejects the duplicate creation. See helper_2026-05-20_23.log
                // around 23:13:14 for the race that motivated this.
                if (err.IndexOf("already allocated", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Logger.Info($"VIIPER bus {busId} already created (idempotent), continuing");
                    return true;
                }
                Logger.Error($"viiper_bus_create({busId}) failed: {err}");
                return false;
            }
            Logger.Info($"VIIPER bus {busId} created");
            return true;
        }

        public bool RemoveBus(uint busId)
        {
            var result = LibViiper.viiper_bus_remove(busId);
            if (result != 0)
            {
                Logger.Error($"viiper_bus_remove({busId}) failed: {LibViiper.GetLastError()}");
                return false;
            }
            Logger.Info($"VIIPER bus {busId} removed");
            return true;
        }

        /// <summary>
        /// Adds a device of the specified type to a bus. Optionally override VID/PID
        /// (used for Steam sub-device selection). Returns (success, assigned device id).
        /// </summary>
        public ViiperAddDeviceResult AddDevice(uint busId, string typeName, ushort vid = 0, ushort pid = 0)
        {
            int result;
            uint deviceId;
            if (vid != 0 || pid != 0)
            {
                try
                {
                    result = LibViiper.viiper_device_add_ex(busId, typeName, vid, pid, out deviceId);
                }
                catch (EntryPointNotFoundException)
                {
                    Logger.Warn("viiper_device_add_ex not available, falling back (VID/PID override ignored)");
                    result = LibViiper.viiper_device_add(busId, typeName, out deviceId);
                    vid = 0; pid = 0;
                }
            }
            else
            {
                result = LibViiper.viiper_device_add(busId, typeName, out deviceId);
            }
            if (result != 0)
            {
                Logger.Error($"viiper_device_add({busId}, {typeName}, vid=0x{vid:X4}, pid=0x{pid:X4}) failed: {LibViiper.GetLastError()}");
                return new ViiperAddDeviceResult(false, 0);
            }
            Logger.Info($"VIIPER device added: {typeName} (bus={busId}, dev={deviceId}, vid=0x{vid:X4}, pid=0x{pid:X4})");

            // libviiper's viiper_device_add is *supposed* to auto-attach the device to the
            // local UDE bus, but on some usbip-win2 UDE installs that internal attach silently
            // no-ops: add returns success and the device is exported on 127.0.0.1:3241, yet no
            // child ever appears under the UDE host controller and no gamepad reaches Windows
            // (verified on-device 2026-07-08, LeGo2 + usbip-win2 0.9.7.8). Drive the standard
            // usbip client explicitly to land it. UsbipCli is idempotent — a device already
            // attached (e.g. the internal auto-attach DID fire) is skipped, so this never
            // produces the duplicate USBIP attachment the old auto-attach-only path warned of.
            UsbipCli.AttachExportedDevices();

            RegisterFeedbackCallback(busId, deviceId);
            return new ViiperAddDeviceResult(true, deviceId);
        }

        public bool RemoveDevice(uint busId, uint deviceId)
        {
            _feedbackDelegates.Remove(Tuple.Create(busId, deviceId));
            var result = LibViiper.viiper_device_remove(busId, deviceId);
            if (result != 0)
            {
                Logger.Error($"viiper_device_remove({busId}, {deviceId}) failed: {LibViiper.GetLastError()}");
                return false;
            }
            Logger.Info($"VIIPER device removed (bus={busId}, dev={deviceId})");
            return true;
        }

        /// <summary>Sends a raw input state report to the emulated device.</summary>
        public bool SetInput(uint busId, uint deviceId, byte[] data)
        {
            var ok = LibViiper.viiper_device_set_input(busId, deviceId, data, data.Length) == 0;
            if (!ok)
            {
                Logger.Warn($"viiper_device_set_input failed (bus={busId}, dev={deviceId}, len={data.Length}): {LibViiper.GetLastError()}");
            }
            return ok;
        }

        public string[] GetDeviceTypes()
        {
            return LibViiper.GetDeviceTypes();
        }

        /// <summary>Hot-swap a device type without tearing down the bus.</summary>
        public ViiperAddDeviceResult SwitchDeviceType(uint busId, uint oldDeviceId, string newTypeName, ushort vid = 0, ushort pid = 0)
        {
            Logger.Info($"VIIPER switching device type: bus={busId}, dev={oldDeviceId} -> {newTypeName}");
            if (!RemoveDevice(busId, oldDeviceId))
            {
                return new ViiperAddDeviceResult(false, 0);
            }
            return AddDevice(busId, newTypeName, vid, pid);
        }

        private void RegisterFeedbackCallback(uint busId, uint deviceId)
        {
            LibViiper.FeedbackCallback cb = OnFeedback;
            _feedbackDelegates[Tuple.Create(busId, deviceId)] = cb;

            var result = LibViiper.viiper_device_set_feedback_callback(busId, deviceId, cb, IntPtr.Zero);
            if (result != 0)
            {
                Logger.Warn($"Failed to register feedback callback: {LibViiper.GetLastError()}");
            }
        }

        private void OnFeedback(uint busId, uint deviceId, IntPtr data, int len, IntPtr userData)
        {
            if (len <= 0 || data == IntPtr.Zero) return;
            var bytes = new byte[len];
            Marshal.Copy(data, bytes, 0, len);
            var handler = FeedbackReceived;
            if (handler != null)
            {
                try { handler(busId, deviceId, bytes); }
                catch (Exception ex) { Logger.Error(ex, "VIIPER FeedbackReceived handler threw"); }
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (!_initialized) return;
                // Shutdown first so the Go side stops invoking callbacks,
                // then clear delegate references so they can be GC'd.
                try { LibViiper.viiper_shutdown(); }
                catch (Exception ex) { Logger.Warn($"viiper_shutdown threw: {ex.Message}"); }
                _feedbackDelegates.Clear();
                _initialized = false;
                Logger.Info("VIIPER shut down");
            }
        }
    }

    /// <summary>
    /// Result of an AddDevice / SwitchDeviceType call.
    /// </summary>
    internal readonly struct ViiperAddDeviceResult
    {
        public readonly bool Success;
        public readonly uint DeviceId;
        public ViiperAddDeviceResult(bool success, uint deviceId)
        {
            Success = success;
            DeviceId = deviceId;
        }
    }
}
