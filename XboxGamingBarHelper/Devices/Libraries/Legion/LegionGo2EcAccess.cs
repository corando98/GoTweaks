using System;
using System.Reflection;
using System.Threading;
using NLog;
using XboxGamingBarHelper.PawnIO;

namespace XboxGamingBarHelper.Devices.Libraries.Legion
{
    /// <summary>
    /// Direct EC RAM read/write for the Legion Go 2 via the signed PawnIO LpcIO module.
    ///
    /// Uses the EC mailbox protocol (ports 0x4E/0x4F) translated from
    /// Rodpad/LeGo2-Fan-Control's Linux Python implementation, verified against
    /// Undervoltologist/legiongo2_ec_reverse_engineer's decompiled EC firmware.
    ///
    /// Mailbox sequence to address 16-bit EC RAM at <addr> for read/write:
    ///   superio_outb(0x2E, 0x11)      // select address-high register pointer
    ///   superio_outb(0x2F, addrHi)    // write addr high byte
    ///   superio_outb(0x2E, 0x10)      // select address-low register pointer
    ///   superio_outb(0x2F, addrLo)    // write addr low byte
    ///   superio_outb(0x2E, 0x12)      // select data register pointer
    ///   superio_outb(0x2F, value)     // write byte (or superio_inb to read)
    ///
    /// Each <c>superio_outb(reg, val)</c> call is one IOCTL into LpcIO.bin which
    /// performs <c>out_byte(0x4E, reg); out_byte(0x4F, val);</c> on the host.
    ///
    /// Key EC RAM addresses (from Undervoltologist's reverse-engineering):
    ///   0xC6C0 — current fan RPM, 16-bit (read-only)
    ///   0xC6C8 — fan target RPM override, 16-bit (write to bypass firmware curve)
    ///   0xC683 — current power mode, 8-bit
    ///
    /// Threading: the EC mailbox is a multi-step indexed protocol — only one caller
    /// can be in the middle of a transaction at a time. This class holds an
    /// internal lock so concurrent EC access from multiple helper subsystems is
    /// serialised. Cross-process access (e.g. AIDA64, HWiNFO) needs the system
    /// mutex <c>\BaseNamedObjects\Access_ISABUS.HTP.Method</c>; we don't acquire
    /// that yet because there's no other GoTweaks subsystem touching the EC and
    /// holding it during a 3s control loop tick would block external monitors.
    /// </summary>
    public sealed class LegionGo2EcAccess : IDisposable
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private const ushort EC_REG_CURRENT_RPM = 0xC6C0;
        private const ushort EC_REG_TARGET_RPM_OVERRIDE = 0xC6C8;

        // Mailbox register pointer values — written through SuperIO index 0x2E to
        // select which logical register subsequent 0x2F writes/reads will target.
        private const byte MAILBOX_INDEX_REG = 0x2E;
        private const byte MAILBOX_DATA_REG = 0x2F;
        private const byte MAILBOX_PTR_ADDR_LOW = 0x10;
        private const byte MAILBOX_PTR_ADDR_HIGH = 0x11;
        private const byte MAILBOX_PTR_DATA = 0x12;

        private const int LPCIO_SLOT_4E_4F = 1;

        private readonly object _txLock = new object();
        private PawnIOWrapper _pawnIO = new PawnIOWrapper();
        private bool _slotSelected = false;
        private bool _disposed = false;

        public bool IsAvailable { get; private set; }

        /// <summary>
        /// Tears down the PawnIO handle and rebuilds the whole access path
        /// (connect → load LpcIO.bin → select slot). The driver handle can go
        /// stale across sleep/hibernate — Rodpad's Windows tool shipped the
        /// same fix ("custom fan speeds stop working after waking from
        /// sleep", 2026-06-04 changelog). Call on resume or after repeated
        /// EC I/O failures. Returns true when the rebuilt path is usable.
        /// </summary>
        public bool Reinitialize()
        {
            if (_disposed) return false;
            lock (_txLock)
            {
                Logger.Info("LegionGo2EcAccess: reinitializing PawnIO EC access path");
                try { _pawnIO.Dispose(); }
                catch (Exception ex) { Logger.Debug($"LegionGo2EcAccess reinit dispose: {ex.Message}"); }
                _pawnIO = new PawnIOWrapper();
                _slotSelected = false;
                IsAvailable = false;
            }
            return Initialize();
        }

        /// <summary>
        /// Connects to PawnIO, loads the signed LpcIO.bin module, and selects the
        /// 0x4E/0x4F slot. Must succeed before any EC access call.
        /// </summary>
        public bool Initialize()
        {
            if (IsAvailable) return true;

            if (!_pawnIO.Connect())
            {
                Logger.Warn("LegionGo2EcAccess: PawnIO connection failed (driver not installed?)");
                return false;
            }

            if (!_pawnIO.LoadModuleFromResource(Assembly.GetExecutingAssembly(),
                    "XboxGamingBarHelper.Resources.PawnIO.LpcIO.bin"))
            {
                Logger.Warn("LegionGo2EcAccess: failed to load LpcIO.bin module");
                return false;
            }

            // Select slot 1 = 0x4E/0x4F. This is what Legion Go 2's Super-IO uses.
            var inArgs = new ulong[] { LPCIO_SLOT_4E_4F };
            if (!_pawnIO.ExecuteFunction("ioctl_select_slot", inArgs, null))
            {
                Logger.Warn("LegionGo2EcAccess: ioctl_select_slot(1) failed");
                return false;
            }

            _slotSelected = true;
            IsAvailable = true;
            Logger.Info("LegionGo2EcAccess: ready (LpcIO module loaded, slot 0x4e/0x4f selected)");
            return true;
        }

        /// <summary>
        /// Reads one byte from EC RAM at <paramref name="address"/>. Returns -1 on failure.
        /// </summary>
        public int ReadByte(ushort address)
        {
            if (!IsAvailable) return -1;
            lock (_txLock)
            {
                if (!SelectAddress(address)) return -1;
                if (!SuperIoOutB(MAILBOX_INDEX_REG, MAILBOX_PTR_DATA)) return -1;
                return SuperIoInB(MAILBOX_DATA_REG);
            }
        }

        /// <summary>
        /// Writes one byte to EC RAM at <paramref name="address"/>. Returns true on success.
        /// </summary>
        public bool WriteByte(ushort address, byte value)
        {
            if (!IsAvailable) return false;
            lock (_txLock)
            {
                if (!SelectAddress(address)) return false;
                if (!SuperIoOutB(MAILBOX_INDEX_REG, MAILBOX_PTR_DATA)) return false;
                return SuperIoOutB(MAILBOX_DATA_REG, value);
            }
        }

        /// <summary>
        /// Reads a 16-bit word stored as little-endian (low byte at addr, high at addr+1).
        /// Returns -1 on failure.
        /// </summary>
        public int ReadWord(ushort address)
        {
            int lo = ReadByte(address);
            if (lo < 0) return -1;
            int hi = ReadByte((ushort)(address + 1));
            if (hi < 0) return -1;
            return ((hi & 0xFF) << 8) | (lo & 0xFF);
        }

        /// <summary>
        /// Writes a 16-bit word as little-endian (low byte at addr, high at addr+1).
        /// </summary>
        public bool WriteWord(ushort address, ushort value)
        {
            return WriteByte(address, (byte)(value & 0xFF))
                && WriteByte((ushort)(address + 1), (byte)((value >> 8) & 0xFF));
        }

        /// <summary>
        /// Reads the current fan RPM from EC register 0xC6C0.
        /// </summary>
        public int GetCurrentFanRpm() => ReadWord(EC_REG_CURRENT_RPM);

        /// <summary>
        /// Sets the fan target RPM via direct EC override (register 0xC6C8).
        /// Bypasses Lenovo's firmware curve. The EC firmware's thermal failsafe
        /// at ~4800 RPM (0x12C0) still engages above 101°C regardless.
        /// </summary>
        public bool SetTargetFanRpm(ushort rpm) => WriteWord(EC_REG_TARGET_RPM_OVERRIDE, rpm);

        // --- Internal mailbox plumbing ---

        private bool SelectAddress(ushort addr)
        {
            return SuperIoOutB(MAILBOX_INDEX_REG, MAILBOX_PTR_ADDR_HIGH)
                && SuperIoOutB(MAILBOX_DATA_REG, (byte)((addr >> 8) & 0xFF))
                && SuperIoOutB(MAILBOX_INDEX_REG, MAILBOX_PTR_ADDR_LOW)
                && SuperIoOutB(MAILBOX_DATA_REG, (byte)(addr & 0xFF));
        }

        // ioctl_superio_outb(reg, val) — the LpcIO module performs out_byte(0x4E, reg); out_byte(0x4F, val).
        private bool SuperIoOutB(byte reg, byte value)
        {
            var inArgs = new ulong[] { reg, value };
            return _pawnIO.ExecuteFunction("ioctl_superio_outb", inArgs, null);
        }

        // ioctl_superio_inb(reg) — out_byte(0x4E, reg); return in_byte(0x4F).
        private int SuperIoInB(byte reg)
        {
            var inArgs = new ulong[] { reg };
            var outArgs = new ulong[1];
            if (!_pawnIO.ExecuteFunction("ioctl_superio_inb", inArgs, outArgs))
                return -1;
            return (int)(outArgs[0] & 0xFF);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                _pawnIO.Dispose();
            }
            catch (Exception ex)
            {
                Logger.Debug($"LegionGo2EcAccess dispose: {ex.Message}");
            }
        }
    }
}
