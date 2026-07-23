using System;
using System.Linq;
using System.Threading;
using HidSharp;

namespace XboxGamingBarHelper.Devices.Libraries.Legion
{
    /// <summary>
    /// Legion Go S Controller HID Library
    ///
    /// Provides RGB lighting control for Lenovo Legion Go S devices.
    /// The Go S uses a different chipset (VID 0x1A86) and HID protocol than the original Legion Go.
    ///
    /// Protocol reference: https://github.com/honjow/HueSync (legion_go_s.py)
    /// Based on: https://github.com/hhd-dev/hhd (slim/hid.py)
    ///
    /// HID Protocol Reference:
    /// - Vendor ID: 0x1A86 (WinChipHead / QinHeng Electronics)
    /// - Product IDs: 0xE310 (XInput mode), 0xE311 (DInput mode)
    /// - Usage Page: 0xFFA0 (Vendor Defined)
    /// - Usage: 0x0001
    /// - Interface: 3
    ///
    /// Supported models: 83L3, 83N6, 83Q2, 83Q3
    /// </summary>
    public class LegionGoSController : IDisposable
    {
        #region Constants

        private const int VendorId = 0x1A86;
        private static readonly int[] ProductIds = { 0xE310, 0xE311 };
        private const int UsagePage = 0xFFA0;
        private const int Usage = 0x0001;
        private const int Interface = 3;
        private const int CommandDelayMs = 50;

        #endregion

        #region RGB Mode Constants

        /// <summary>
        /// RGB lighting modes supported by Legion Go S
        /// </summary>
        public enum RgbMode : byte
        {
            Solid = 0,
            Pulse = 1,
            Dynamic = 2,  // Rainbow
            Spiral = 3
        }

        #endregion

        #region Private Fields

        private HidDevice _device;
        private HidStream _stream;
        private bool _disposed;
        private RgbMode? _currentMode;
        private readonly object _deviceLock = new object();

        #endregion

        #region Events

        /// <summary>
        /// Raised when connection status changes.
        /// </summary>
        public event EventHandler<bool> ConnectionChanged;

        /// <summary>
        /// Raised when a HID command is sent (for debugging).
        /// </summary>
        public event EventHandler<byte[]> CommandSent;

        #endregion

        #region Properties

        /// <summary>
        /// Gets whether a Legion Go S controller is currently connected.
        /// </summary>
        public bool IsConnected => _device != null && _stream != null;

        /// <summary>
        /// Gets information about the connected device.
        /// </summary>
        public string DeviceInfo => _device?.ToString();

        #endregion

        #region Helper Methods

        private static int Clamp(int value, int min, int max)
        {
            return Math.Max(min, Math.Min(max, value));
        }

        #endregion

        #region Connection Methods

        /// <summary>
        /// Attempts to connect to a Legion Go S controller.
        /// Searches for devices matching the Legion Go S VID/PIDs with the correct usage page.
        /// </summary>
        /// <returns>True if connection successful, false otherwise.</returns>
        public bool Connect()
        {
            lock (_deviceLock)
            {
                try
                {
                    Disconnect();

                    var devices = DeviceList.Local.GetHidDevices()
                        .Where(d => d.VendorID == VendorId &&
                                    ProductIds.Contains(d.ProductID));

                    foreach (var device in devices)
                    {
                        try
                        {
                            // Check usage page and usage
                            var reportDescriptor = device.GetReportDescriptor();
                            foreach (var deviceItem in reportDescriptor.DeviceItems)
                            {
                                foreach (var usage in deviceItem.Usages.GetAllValues())
                                {
                                    var usagePage = (usage >> 16) & 0xFFFF;
                                    var usageId = usage & 0xFFFF;

                                    if (usagePage == UsagePage && usageId == Usage)
                                    {
                                        // Found matching device
                                        _device = device;
                                        _stream = device.Open();
                                        _currentMode = null;

                                        ConnectionChanged?.Invoke(this, true);
                                        return true;
                                    }
                                }
                            }
                        }
                        catch
                        {
                            // Try next device
                            continue;
                        }
                    }

                    return false;
                }
                catch
                {
                    Disconnect();
                    return false;
                }
            }
        }

        /// <summary>
        /// Disconnects from the controller.
        /// </summary>
        public void Disconnect()
        {
            lock (_deviceLock)
            {
                if (_stream != null)
                {
                    try { _stream.Close(); } catch { }
                    _stream = null;
                }

                if (_device != null)
                {
                    _device = null;
                    _currentMode = null;
                    ConnectionChanged?.Invoke(this, false);
                }
            }
        }

        #endregion

        #region Gamepad Config Methods (per hid-lenovo-go-s.c: SET_GAMEPAD_CFG=0x04, features 0-based)

        /// <summary>
        /// Sets the auto-sleep (hibernation) timeout. 0 = never.
        ///
        /// HID Command: [0x04, 0x04, minutes] (SET_GAMEPAD_CFG, FEATURE_AUTO_SLEEP_TIME)
        /// </summary>
        public bool SetAutoSleepTime(int minutes)
        {
            byte min = (byte)Clamp(minutes, 0, 255);
            return SendCommand(new byte[] { 0x04, 0x04, min });
        }

        /// <summary>
        /// Sets the gamepad mode. NOTE: Go S values are 0-based (0=XInput, 1=DInput),
        /// unlike the Go 2 protocol (1=XInput, 2=DInput).
        ///
        /// HID Command: [0x04, 0x01, mode] (SET_GAMEPAD_CFG, FEATURE_GAMEPAD_MODE)
        /// </summary>
        public bool SetGamepadMode(bool xinput)
        {
            return SendCommand(new byte[] { 0x04, 0x01, (byte)(xinput ? 0x00 : 0x01) });
        }

        #endregion

        #region RGB Control Methods

        /// <summary>
        /// Enables or disables RGB lighting.
        ///
        /// HID Command: [0x04, 0x06, enable (0 or 1)]
        /// </summary>
        /// <param name="enable">True to enable, false to disable.</param>
        /// <returns>True if command sent successfully.</returns>
        public bool SetRgbEnabled(bool enable)
        {
            var command = new byte[] { 0x04, 0x06, (byte)(enable ? 0x01 : 0x00) };
            return SendCommand(command);
        }

        /// <summary>
        /// Loads (activates) a saved RGB profile.
        ///
        /// HID Command: [0x10, 0x02, profile (1-3)]
        /// </summary>
        /// <param name="profile">Profile number (1, 2, or 3).</param>
        /// <returns>True if command sent successfully.</returns>
        public bool LoadRgbProfile(byte profile = 3)
        {
            profile = (byte)Clamp(profile, 1, 3);
            var command = new byte[] { 0x10, 0x02, profile };
            return SendCommand(command);
        }

        /// <summary>
        /// Sets the RGB profile settings.
        ///
        /// HID Command: [0x10, profile+2, mode, R, G, B, brightness (0-63), speed (0-63)]
        /// </summary>
        /// <param name="profile">Profile number (1, 2, or 3).</param>
        /// <param name="mode">RGB mode.</param>
        /// <param name="red">Red color component (0-255).</param>
        /// <param name="green">Green color component (0-255).</param>
        /// <param name="blue">Blue color component (0-255).</param>
        /// <param name="brightness">Brightness (0.0-1.0).</param>
        /// <param name="speed">Animation speed (0.0-1.0).</param>
        /// <returns>True if command sent successfully.</returns>
        public bool SetRgbProfile(
            byte profile,
            RgbMode mode,
            byte red, byte green, byte blue,
            double brightness = 1.0,
            double speed = 0.5)
        {
            profile = (byte)Clamp(profile, 1, 3);

            // Convert brightness and speed from 0.0-1.0 to 0-63
            byte brightnessValue = (byte)Clamp((int)(64 * brightness), 0, 63);
            byte speedValue = (byte)Clamp((int)(64 * speed), 0, 63);

            var command = new byte[]
            {
                0x10,
                (byte)(profile + 2),  // Profile 1=3, 2=4, 3=5
                (byte)mode,
                red,
                green,
                blue,
                brightnessValue,
                speedValue
            };

            return SendCommand(command);
        }

        /// <summary>
        /// Disables RGB lighting (turns off LEDs).
        /// </summary>
        /// <returns>True if command sent successfully.</returns>
        public bool DisableRgb()
        {
            _currentMode = null;
            return SetRgbEnabled(false);
        }

        /// <summary>
        /// Sets solid color mode with the specified color.
        /// </summary>
        /// <param name="red">Red component (0-255).</param>
        /// <param name="green">Green component (0-255).</param>
        /// <param name="blue">Blue component (0-255).</param>
        /// <param name="brightness">Brightness (0-100).</param>
        /// <returns>True if all commands sent successfully.</returns>
        public bool SetSolidColor(byte red, byte green, byte blue, int brightness = 100)
        {
            // If color is black, disable RGB
            if (red == 0 && green == 0 && blue == 0)
            {
                return DisableRgb();
            }

            return SetRgbMode(RgbMode.Solid, red, green, blue, brightness);
        }

        /// <summary>
        /// Sets RGB mode with full parameters.
        /// </summary>
        /// <param name="mode">RGB mode.</param>
        /// <param name="red">Red component (0-255).</param>
        /// <param name="green">Green component (0-255).</param>
        /// <param name="blue">Blue component (0-255).</param>
        /// <param name="brightness">Brightness (0-100).</param>
        /// <param name="speed">Animation speed (0-100).</param>
        /// <param name="profile">Profile slot (1-3).</param>
        /// <returns>True if all commands sent successfully.</returns>
        public bool SetRgbMode(
            RgbMode mode,
            byte red, byte green, byte blue,
            int brightness = 100,
            int speed = 50,
            byte profile = 3)
        {
            lock (_deviceLock)
            {
                bool isInit = _currentMode != mode;
                double brightnessNormalized = brightness / 100.0;
                double speedNormalized = speed / 100.0;

                // If mode changed, send init sequence
                if (isInit)
                {
                    // Step 1: Enable RGB
                    if (!SetRgbEnabled(true))
                        return false;
                    Thread.Sleep(CommandDelayMs);

                    // Step 2: Load profile
                    if (!LoadRgbProfile(profile))
                        return false;
                    Thread.Sleep(CommandDelayMs);
                }

                // Step 3: Set profile with color and mode
                if (!SetRgbProfile(profile, mode, red, green, blue, brightnessNormalized, speedNormalized))
                    return false;

                _currentMode = mode;
                return true;
            }
        }

        /// <summary>
        /// Sets pulse/breathing mode with the specified color.
        /// </summary>
        public bool SetPulseMode(byte red, byte green, byte blue, int brightness = 100, int speed = 50)
        {
            return SetRgbMode(RgbMode.Pulse, red, green, blue, brightness, speed);
        }

        /// <summary>
        /// Sets rainbow/dynamic mode.
        /// </summary>
        public bool SetRainbowMode(int brightness = 100, int speed = 50)
        {
            return SetRgbMode(RgbMode.Dynamic, 255, 0, 0, brightness, speed);
        }

        /// <summary>
        /// Sets spiral mode.
        /// </summary>
        public bool SetSpiralMode(int brightness = 100, int speed = 50)
        {
            return SetRgbMode(RgbMode.Spiral, 255, 0, 0, brightness, speed);
        }

        #endregion

        #region HID Communication

        private bool SendCommand(byte[] command)
        {
            lock (_deviceLock)
            {
                if (!IsConnected || _stream == null)
                    return false;

                try
                {
                    _stream.Write(command);
                    CommandSent?.Invoke(this, command);
                    return true;
                }
                catch
                {
                    Disconnect();
                    return false;
                }
            }
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    Disconnect();
                }
                _disposed = true;
            }
        }

        ~LegionGoSController()
        {
            Dispose(false);
        }

        #endregion
    }
}
