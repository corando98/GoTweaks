using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using HidSharp;

namespace XboxGamingBarHelper.Devices.Libraries.Legion
{

/// <summary>
/// Legion Go Controller HID Library
///
/// Provides a clean API for configuring Lenovo Legion Go controller settings via HID.
/// This library allows you to remap buttons, configure vibration, gyro, touchpad,
/// stick deadzones, and sleep timers on the Legion Go detachable controllers.
///
/// Usage:
/// <code>
/// using var controller = new LegionGoController();
/// if (controller.Connect())
/// {
///     controller.SetButtonMapping(RemappableButton.Y1, RemapAction.Screenshot);
///     controller.SetVibrationLevel(Controller.Left, VibrationLevel.Medium);
///     controller.SetGyroTarget(GyroTarget.RightStick);
///     controller.SetGyroSensitivity(75, 75);
///     controller.SetGyroActivationButtons(GyroActivationMode.Hold, GyroActivationButton.LT);
///
///     // Easter egg: Nintendo-style face button layout (A↔B, X↔Y)
///     controller.SetNintendoLayout(true);
/// }
/// </code>
///
/// HID Protocol Reference:
/// - Vendor ID: 0x17EF (Lenovo)
/// - Product IDs: 0x6180, 0x61E0
/// - Usage Page: 0xFFA0 (Vendor Defined)
/// - Report ID: 0x05
/// - Command Length: 64 bytes (padded with 0x00)
/// </summary>
public class LegionGoController : IDisposable
{
    #region Constants

    private const int VendorId = 0x17EF;
    private static readonly int[] ProductIds = { 0x6180, 0x61E0 };
    private const int UsagePage = 0xFFA0;
    private const byte ReportId = 0x05;
    private const byte PaddingByte = 0x00;
    private const int CommandLength = 64;
    private const int CommandDelayMs = 50;

    #endregion

    #region Helper Methods

    // Clamp is not available in .NET Framework 4.8
    private static int Clamp(int value, int min, int max)
    {
        return Math.Max(min, Math.Min(max, value));
    }

    private static double Clamp(double value, double min, double max)
    {
        return Math.Max(min, Math.Min(max, value));
    }

    #endregion

    #region Private Fields

    private HidDevice? _device;
    private HidStream? _stream;
    private bool _disposed;

    // Battery monitoring
    private Thread? _batteryMonitorThread;
    private volatile bool _monitoringBattery;
    private int _leftControllerBattery = -1;
    private int _rightControllerBattery = -1;
    private bool _leftControllerCharging;
    private bool _rightControllerCharging;

    // Device status (b0:01) read state
    private readonly object _statusLock = new object();
    private LegionGoStatus? _latestStatus;
    private ManualResetEventSlim? _statusWaiter;

    #endregion

    #region Events

    /// <summary>
    /// Raised when connection status changes.
    /// </summary>
    public event EventHandler<bool>? ConnectionChanged;

    /// <summary>
    /// Raised when controller battery status is updated.
    /// </summary>
    public event EventHandler<ControllerBatteryEventArgs>? BatteryUpdated;

    /// <summary>
    /// Raised whenever a b0:01 device status report is received (either solicited
    /// via <see cref="ReadDeviceStatus"/> or observed by the battery monitor).
    /// </summary>
    public event EventHandler<LegionGoStatus>? DeviceStatusUpdated;

    /// <summary>
    /// Raised when a HID command is sent or received (for debugging).
    /// </summary>
    public event EventHandler<HidCommandEventArgs>? CommandExecuted;

    #endregion

    #region Properties

    /// <summary>
    /// Gets whether a Legion Go controller is currently connected.
    /// </summary>
    public bool IsConnected => _device != null && _stream != null;

    /// <summary>
    /// Gets information about the connected device.
    /// </summary>
    public string? DeviceInfo => _device?.ToString();

    /// <summary>
    /// Gets the left controller battery percentage (1-100), or -1 if unavailable.
    /// </summary>
    public int LeftControllerBattery => _leftControllerBattery;

    /// <summary>
    /// Gets the right controller battery percentage (1-100), or -1 if unavailable.
    /// </summary>
    public int RightControllerBattery => _rightControllerBattery;

    /// <summary>
    /// Gets whether the left controller is charging.
    /// </summary>
    public bool LeftControllerCharging => _leftControllerCharging;

    /// <summary>
    /// Gets whether the right controller is charging.
    /// </summary>
    public bool RightControllerCharging => _rightControllerCharging;

    #endregion

    #region Connection Methods

    /// <summary>
    /// Attempts to connect to a Legion Go controller.
    /// Searches for devices matching Lenovo VID and Legion Go PIDs with the correct usage page.
    /// </summary>
    /// <returns>True if connection successful, false otherwise.</returns>
    public bool Connect()
    {
        try
        {
            Disconnect();

            var devices = DeviceList.Local.GetHidDevices();

            foreach (var device in devices)
            {
                if (device.VendorID != VendorId)
                    continue;

                bool productMatch = ProductIds.Any(pid => (device.ProductID & 0xFFF0) == pid);
                if (!productMatch)
                    continue;

                try
                {
                    var reportDescriptor = device.GetReportDescriptor();
                    bool hasUsagePage = reportDescriptor.DeviceItems
                        .SelectMany(item => item.Usages.GetAllValues())
                        .Any(usage => (usage >> 16) == UsagePage);

                    if (!hasUsagePage)
                        continue;

                    _device = device;
                    _stream = device.Open();
                    _stream.ReadTimeout = 1000;
                    _stream.WriteTimeout = 1000;

                    ConnectionChanged?.Invoke(this, true);
                    return true;
                }
                catch
                {
                    continue;
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Disconnects from the current controller.
    /// </summary>
    public void Disconnect()
    {
        StopBatteryMonitoring();

        if (_stream != null)
        {
            try
            {
                _stream.Close();
                _stream.Dispose();
            }
            catch { }
            _stream = null;
        }

        if (_device != null)
        {
            _device = null;
            ConnectionChanged?.Invoke(this, false);
        }
    }

    /// <summary>
    /// Checks if any Legion Go controller is available without connecting.
    /// </summary>
    /// <returns>True if a compatible device is found.</returns>
    public static bool IsDeviceAvailable()
    {
        try
        {
            var devices = DeviceList.Local.GetHidDevices();
            return devices.Any(d =>
                d.VendorID == VendorId &&
                ProductIds.Any(pid => (d.ProductID & 0xFFF0) == pid));
        }
        catch
        {
            return false;
        }
    }

    #endregion

    #region Button Remapping

    /// <summary>
    /// Sets the action for a remappable button.
    ///
    /// HID Command: 05 07 6C 02 [controller] [button] [action] 01
    /// </summary>
    /// <param name="button">The button to remap (Y1, Y2, Y3, M2, M3).</param>
    /// <param name="action">The action to assign.</param>
    /// <returns>True if command sent successfully.</returns>
    public bool SetButtonMapping(RemappableButton button, RemapAction action)
    {
        var controller = GetControllerForButton(button);
        var command = CreateCommand(
            0x07, 0x6C, 0x02,
            (byte)controller,
            (byte)button,
            (byte)action,
            0x01
        );
        return SendCommand(command);
    }

    /// <summary>
    /// Gets the controller (Left/Right) that owns a specific button.
    /// </summary>
    public static Controller GetControllerForButton(RemappableButton button)
    {
        switch (button)
        {
            case RemappableButton.Y1:
            case RemappableButton.Y2:
                return Controller.Left;
            case RemappableButton.Y3:
            case RemappableButton.M1:
            case RemappableButton.M2:
            case RemappableButton.M3:
                return Controller.Right;
            default:
                return Controller.Right;
        }
    }

    /// <summary>
    /// Sets button mapping with type support (gamepad/keyboard/mouse).
    ///
    /// HID Command: 05 00 12 0a [ctrl] 01 11 01 [btn] [mode] [map(s)]
    /// </summary>
    /// <param name="button">The button to remap.</param>
    /// <param name="type">Mapping type (Gamepad, Keyboard, or Mouse).</param>
    /// <param name="mappings">The mapping value(s). For keyboard, can be up to 5 key codes.</param>
    /// <returns>True if command sent successfully.</returns>
    public bool SetButtonMappingAdvanced(RemappableButton button, MappingType type, params byte[] mappings)
    {
        var controller = GetControllerForButton(button);
        var bytes = new List<byte> { 0x00, 0x12, 0x0A, (byte)controller, 0x01, 0x11, 0x01, (byte)button, (byte)type };
        bytes.AddRange(mappings);
        return SendCommand(CreateCommand(bytes.ToArray()));
    }

    /// <summary>
    /// Clears a button mapping (sets to disabled).
    ///
    /// HID Command: 05 00 12 0a [ctrl] 01 11 01 [btn] 01
    /// </summary>
    /// <param name="button">The button to clear.</param>
    /// <returns>True if command sent successfully.</returns>
    public bool ClearButtonMapping(RemappableButton button)
    {
        var controller = GetControllerForButton(button);
        return SendCommand(CreateCommand(0x00, 0x12, 0x0A, (byte)controller, 0x01, 0x11, 0x01, (byte)button, 0x01));
    }

    /// <summary>
    /// Gets the controller (Left/Right) that owns a specific gamepad button.
    /// </summary>
    public static Controller GetControllerForGamepadButton(GamepadButton button)
    {
        switch (button)
        {
            case GamepadButton.LSClick:
            case GamepadButton.LSUp:
            case GamepadButton.LSDown:
            case GamepadButton.LSLeft:
            case GamepadButton.LSRight:
            case GamepadButton.DPadUp:
            case GamepadButton.DPadDown:
            case GamepadButton.DPadLeft:
            case GamepadButton.DPadRight:
            case GamepadButton.LB:
            case GamepadButton.LT:
            case GamepadButton.Select:
            case GamepadButton.DesktopButton:
            case GamepadButton.PageButton:
                return Controller.Left;
            default:
                return Controller.Right;
        }
    }

    /// <summary>
    /// Sets mapping for a standard gamepad button (same format as SetButtonMappingAdvanced).
    ///
    /// HID Command: 05 00 12 0A [ctrl] 01 11 01 [btn] [type] [mappings]
    /// </summary>
    /// <param name="button">The gamepad button to remap.</param>
    /// <param name="type">Mapping type (Gamepad, Keyboard, or Mouse).</param>
    /// <param name="mappings">The mapping value(s). For keyboard, can be up to 5 key codes.</param>
    /// <returns>True if command sent successfully.</returns>
    public bool SetGamepadButtonMappingAdvanced(GamepadButton button, MappingType type, params byte[] mappings)
    {
        var controller = GetControllerForGamepadButton(button);
        var bytes = new List<byte> { 0x00, 0x12, 0x0A, (byte)controller, 0x01, 0x11, 0x01, (byte)button, (byte)type };
        bytes.AddRange(mappings);
        return SendCommand(CreateCommand(bytes.ToArray()));
    }

    /// <summary>
    /// Clears a gamepad button mapping (restores default behavior).
    ///
    /// HID Command: 05 00 12 0A [ctrl] 01 11 01 [btn] 01
    /// </summary>
    /// <param name="button">The gamepad button to clear.</param>
    /// <returns>True if command sent successfully.</returns>
    public bool ClearGamepadButtonMapping(GamepadButton button)
    {
        var controller = GetControllerForGamepadButton(button);
        return SendCommand(CreateCommand(0x00, 0x12, 0x0A, (byte)controller, 0x01, 0x11, 0x01, (byte)button, 0x01));
    }

    #endregion

    #region Touchpad Control

    /// <summary>
    /// Enables or disables the touchpad.
    ///
    /// HID Command: 05 06 6B 02 04 [01/00] 01
    /// </summary>
    /// <param name="enabled">True to enable, false to disable.</param>
    /// <returns>True if command sent successfully.</returns>
    public bool SetTouchpadEnabled(bool enabled)
    {
        var command = CreateCommand(
            0x06, 0x6B, 0x02, 0x04,
            (byte)(enabled ? 0x01 : 0x00),
            0x01
        );
        return SendCommand(command);
    }

    /// <summary>
    /// Sets the touchpad haptic feedback vibration level.
    ///
    /// HID Command: 05 00 06 06 00 [level]
    /// </summary>
    /// <param name="level">Vibration level (Off=0x01, Low=0x02, Medium=0x03, High=0x04).</param>
    /// <returns>True if command sent successfully.</returns>
    public bool SetTouchpadVibration(TouchpadVibrationLevel level)
    {
        var command = CreateCommand(
            0x00, 0x06, 0x06, 0x00,
            (byte)level
        );
        return SendCommand(command);
    }

    /// <summary>
    /// Enables or disables touchpad haptic feedback (vibration).
    /// Convenience overload - enables at Medium level, or disables.
    /// </summary>
    /// <param name="enabled">True to enable haptics (Medium), false to disable.</param>
    /// <returns>True if command sent successfully.</returns>
    public bool SetTouchpadVibration(bool enabled)
    {
        return SetTouchpadVibration(enabled ? TouchpadVibrationLevel.Medium : TouchpadVibrationLevel.Off);
    }

    #endregion

    #region Vibration Control

    /// <summary>
    /// Sets the vibration intensity for a controller.
    ///
    /// HID Command: 05 06 67 02 [controller] [level] 01
    /// </summary>
    /// <param name="controller">Left or Right controller.</param>
    /// <param name="level">Vibration intensity level.</param>
    /// <returns>True if command sent successfully.</returns>
    public bool SetVibrationLevel(Controller controller, VibrationLevel level)
    {
        var command = CreateCommand(
            0x06, 0x67, 0x02,
            (byte)controller,
            (byte)level,
            0x01
        );
        return SendCommand(command);
    }

    /// <summary>
    /// Sets the vibration mode preset (game-specific vibration patterns).
    ///
    /// HID Command: 05 00 06 04 03 [mode]
    /// </summary>
    /// <param name="mode">Vibration mode preset.</param>
    /// <returns>True if command sent successfully.</returns>
    public bool SetVibrationMode(VibrationMode mode)
    {
        var command = CreateCommand(
            0x00, 0x06, 0x04, 0x03,
            (byte)mode
        );
        return SendCommand(command);
    }

    #endregion

    #region Gyro Control

    /// <summary>
    /// Sets the gyro target output (which stick/mouse the gyro controls).
    ///
    /// HID Command: 05 00 0E 02 04 [target]
    /// </summary>
    /// <param name="target">Disabled, LeftStick, RightStick, or Mouse.</param>
    /// <returns>True if command sent successfully.</returns>
    public bool SetGyroTarget(GyroTarget target)
    {
        var command = CreateCommand(
            0x00, 0x0E, 0x02, 0x04,
            (byte)target
        );
        return SendCommand(command);
    }

    /// <summary>
    /// Sets gyro mapping type (instant or continuous response).
    ///
    /// HID Command: 05 00 0E 03 04 00 00 [type] [sensX] [sensY] FF FF FF FF [invX] [invY]
    /// </summary>
    /// <param name="mappingType">Instant (snappy) or Continuous (smooth).</param>
    /// <param name="sensitivityX">X-axis sensitivity (1-100).</param>
    /// <param name="sensitivityY">Y-axis sensitivity (1-100).</param>
    /// <param name="invertX">Invert X-axis direction.</param>
    /// <param name="invertY">Invert Y-axis direction.</param>
    /// <returns>True if command sent successfully.</returns>
    public bool SetGyroSettings(
        GyroMappingType mappingType,
        int sensitivityX,
        int sensitivityY,
        bool invertX = false,
        bool invertY = false)
    {
        sensitivityX = Clamp(sensitivityX, 1, 100);
        sensitivityY = Clamp(sensitivityY, 1, 100);

        byte[] command = new byte[CommandLength];
        command[0] = ReportId;
        command[1] = 0x00;
        command[2] = 0x0E;
        command[3] = 0x03;
        command[4] = 0x04;
        command[5] = 0x00;  // Reserved
        command[6] = 0x00;  // Reserved
        command[7] = (byte)mappingType;
        command[8] = (byte)sensitivityX;
        command[9] = (byte)sensitivityY;
        command[10] = 0xFF; // Reserved
        command[11] = 0xFF; // Reserved
        command[12] = 0xFF; // Reserved
        command[13] = 0xFF; // Reserved
        command[14] = (byte)(invertX ? 0x02 : 0x01);
        command[15] = (byte)(invertY ? 0x02 : 0x01);

        for (int i = 16; i < CommandLength; i++)
            command[i] = PaddingByte;

        return SendCommand(command);
    }

    /// <summary>
    /// Convenience method to set only gyro sensitivity.
    /// </summary>
    /// <param name="sensitivityX">X-axis sensitivity (1-100).</param>
    /// <param name="sensitivityY">Y-axis sensitivity (1-100).</param>
    /// <returns>True if command sent successfully.</returns>
    public bool SetGyroSensitivity(int sensitivityX, int sensitivityY)
    {
        return SetGyroSettings(GyroMappingType.Instant, sensitivityX, sensitivityY);
    }

    /// <summary>
    /// Sets gyro activation buttons with the specified mode.
    ///
    /// HID Command: 05 00 0E 05 04 [mode] [button1] [button2] ... + 0x00 padding
    /// Mode: 0x02 = Hold (active while held), 0x03 = Toggle (on/off)
    /// Max 5 buttons can be specified.
    /// </summary>
    /// <param name="mode">Activation mode (Hold or Toggle).</param>
    /// <param name="buttons">Buttons that activate gyro (max 5).</param>
    /// <returns>True if command sent successfully.</returns>
    public bool SetGyroActivationButtons(GyroActivationMode mode, params GyroActivationButton[] buttons)
    {
        byte[] command = new byte[CommandLength];
        command[0] = ReportId;
        command[1] = 0x00;
        command[2] = 0x0E;
        command[3] = 0x05;
        command[4] = 0x04;
        command[5] = (byte)mode;

        int idx = 6;
        int count = 0;
        foreach (var button in buttons)
        {
            if (button != GyroActivationButton.None && idx < CommandLength && count < 5)
            {
                command[idx++] = (byte)button;
                count++;
            }
        }

        // Remaining bytes stay as 0x00 (as Legion Space does)

        return SendCommand(command);
    }

    /// <summary>
    /// Sets gyro activation buttons with Hold mode (default).
    /// </summary>
    /// <param name="buttons">Buttons that activate gyro (max 5).</param>
    /// <returns>True if command sent successfully.</returns>
    public bool SetGyroActivationButtons(params GyroActivationButton[] buttons)
    {
        return SetGyroActivationButtons(GyroActivationMode.Hold, buttons);
    }

    /// <summary>
    /// Resets gyro activation to "always enabled" mode (no activation buttons required).
    ///
    /// HID Command: 05 00 0E 05 04 01 + 0x00 padding
    /// </summary>
    /// <returns>True if command sent successfully.</returns>
    public bool ResetGyroActivation()
    {
        byte[] resetCommand = new byte[CommandLength];
        resetCommand[0] = ReportId;
        resetCommand[1] = 0x00;
        resetCommand[2] = 0x0E;
        resetCommand[3] = 0x05;
        resetCommand[4] = 0x04;
        resetCommand[5] = 0x01;  // Reset mode

        // Remaining bytes stay as 0x00 (as Legion Space does)

        return SendCommand(resetCommand);
    }

    /// <summary>
    /// Triggers firmware-level gyro calibration on both controllers.
    /// Controllers must be held still during calibration.
    ///
    /// HID Command: 05 00 0E 06 [03=left|04=right] 01
    /// </summary>
    /// <returns>True if both commands sent successfully.</returns>
    public bool CalibrateGyro()
    {
        bool ok = true;
        foreach (byte ctrl in new byte[] { 0x03, 0x04 })
        {
            byte[] command = new byte[CommandLength];
            command[0] = ReportId;
            command[1] = PaddingByte;
            command[2] = 0x0E;
            command[3] = 0x06;
            command[4] = ctrl;
            command[5] = 0x01;

            if (!SendCommand(command))
                ok = false;

            Thread.Sleep(CommandDelayMs);
        }
        return ok;
    }

    #endregion

    #region Face Button Remapping (Nintendo Layout)

    // Face button codes for remapping
    private const byte FaceButtonA = 0x12;
    private const byte FaceButtonB = 0x13;
    private const byte FaceButtonX = 0x14;
    private const byte FaceButtonY = 0x15;

    /// <summary>
    /// Remaps a single face button to another button.
    ///
    /// HID Command: 05 00 12 0A 04 01 11 01 [source] 01 [target]
    /// Face button codes: A=0x12, B=0x13, X=0x14, Y=0x15
    /// </summary>
    /// <param name="sourceButton">The button to remap.</param>
    /// <param name="targetButton">The button to map it to.</param>
    /// <returns>True if command sent successfully.</returns>
    public bool SetFaceButtonRemap(FaceButton sourceButton, FaceButton targetButton)
    {
        byte[] command = new byte[CommandLength];
        command[0] = ReportId;
        command[1] = 0x00;
        command[2] = 0x12;
        command[3] = 0x0A;
        command[4] = 0x04;
        command[5] = 0x01;
        command[6] = 0x11;
        command[7] = 0x01;
        command[8] = (byte)sourceButton;
        command[9] = 0x01;
        command[10] = (byte)targetButton;
        // Remaining bytes stay as 0x00
        return SendCommand(command);
    }

    /// <summary>
    /// Sets Nintendo-style face button layout (A↔B, X↔Y swap).
    /// This is an easter egg for Nintendo fans who prefer the classic layout!
    ///
    /// Nintendo layout swaps:
    /// - A (bottom) ↔ B (right)
    /// - X (left) ↔ Y (top)
    /// </summary>
    /// <param name="enabled">True for Nintendo layout, false for Xbox layout (default).</param>
    /// <returns>True if all commands sent successfully.</returns>
    public bool SetNintendoLayout(bool enabled)
    {
        bool success = true;

        if (enabled)
        {
            // Nintendo layout: A↔B, X↔Y
            success &= SetFaceButtonRemap(FaceButton.A, FaceButton.B);
            Thread.Sleep(CommandDelayMs);
            success &= SetFaceButtonRemap(FaceButton.B, FaceButton.A);
            Thread.Sleep(CommandDelayMs);
            success &= SetFaceButtonRemap(FaceButton.X, FaceButton.Y);
            Thread.Sleep(CommandDelayMs);
            success &= SetFaceButtonRemap(FaceButton.Y, FaceButton.X);
        }
        else
        {
            // Xbox layout (default): each button maps to itself
            success &= SetFaceButtonRemap(FaceButton.A, FaceButton.A);
            Thread.Sleep(CommandDelayMs);
            success &= SetFaceButtonRemap(FaceButton.B, FaceButton.B);
            Thread.Sleep(CommandDelayMs);
            success &= SetFaceButtonRemap(FaceButton.X, FaceButton.X);
            Thread.Sleep(CommandDelayMs);
            success &= SetFaceButtonRemap(FaceButton.Y, FaceButton.Y);
        }

        return success;
    }

    /// <summary>
    /// Sets joystick as mouse mode for a controller.
    /// When enabled, the joystick controls the mouse cursor instead of gamepad input.
    ///
    /// HID Command: 05 00 0C 07 [ctrl] [enable] 00 00 [sensitivity] [enable]
    /// </summary>
    /// <param name="controller">Left or Right controller.</param>
    /// <param name="enabled">True to enable joystick as mouse, false to disable.</param>
    /// <param name="sensitivity">Mouse sensitivity (1-100, default 50).</param>
    /// <returns>True if command sent successfully.</returns>
    public bool SetJoystickAsMouse(Controller controller, bool enabled, int sensitivity)
    {
        sensitivity = Clamp(sensitivity, 1, 100);
        byte enableByte = enabled ? (byte)0x02 : (byte)0x01;

        var command = CreateCommand(
            0x00, 0x0C, 0x07,
            (byte)controller,
            enableByte,
            0x00, 0x00,
            (byte)sensitivity,
            enableByte
        );
        return SendCommand(command);
    }

    /// <summary>
    /// Sets the gyro deadzone which suppresses small motions near center.
    ///
    /// HID Command: 05 05 6A 13 04 [value] 01
    /// </summary>
    /// <param name="deadzone">Deadzone value (1-100, default is 10).</param>
    /// <returns>True if command sent successfully.</returns>
    public bool SetGyroDeadzone(int deadzone)
    {
        deadzone = Clamp(deadzone, 1, 100);

        var command = CreateCommand(
            0x05, 0x6A, 0x13, 0x04,
            (byte)deadzone,
            0x01
        );
        return SendCommand(command);
    }

    #endregion

    #region Stick Deadzone

    /// <summary>
    /// Sets the deadzone for an analog stick.
    ///
    /// HID Command: 05 06 3F 06 [controller] [level] 01
    /// </summary>
    /// <param name="controller">Left or Right controller/stick.</param>
    /// <param name="deadzonePercent">Deadzone percentage (0-50, default is 4).</param>
    /// <returns>True if command sent successfully.</returns>
    public bool SetStickDeadzone(Controller controller, int deadzonePercent)
    {
        deadzonePercent = Clamp(deadzonePercent, 0, 50);
        var command = CreateCommand(
            0x06, 0x3F, 0x06,
            (byte)controller,
            (byte)deadzonePercent,
            0x01
        );
        return SendCommand(command);
    }

    /// <summary>
    /// Sets the trigger travel range for a controller.
    /// This controls when the trigger starts registering input and when it reports full press.
    ///
    /// HID Command: 05 00 0A 02 [controller] [start%] [end%]
    /// </summary>
    /// <param name="controller">Left (0x03) or Right (0x04) controller.</param>
    /// <param name="startPercent">Percentage of travel where trigger starts registering (0-100).</param>
    /// <param name="endPercent">Percentage from end where trigger reports full (0-100, e.g., 6 means 94% = full).</param>
    /// <returns>True if command sent successfully.</returns>
    public bool SetTriggerTravel(Controller controller, int startPercent, int endPercent)
    {
        startPercent = Clamp(startPercent, 0, 100);
        endPercent = Clamp(endPercent, 0, 100);
        var command = CreateCommand(
            0x00, 0x0A, 0x02,
            (byte)controller,
            (byte)startPercent,
            (byte)endPercent
        );
        return SendCommand(command);
    }

    #endregion

    #region Sleep Timer

    /// <summary>
    /// Sets the auto-sleep timer for a controller.
    ///
    /// HID Command: 05 06 33 01 [controller] [minutes] 01
    /// </summary>
    /// <param name="controller">Left or Right controller.</param>
    /// <param name="minutes">Minutes until sleep (0 = never, max 60).</param>
    /// <returns>True if command sent successfully.</returns>
    public bool SetSleepTimer(Controller controller, int minutes)
    {
        minutes = Clamp(minutes, 0, 60);
        var command = CreateCommand(
            0x06, 0x33, 0x01,
            (byte)controller,
            (byte)minutes,
            0x01
        );
        return SendCommand(command);
    }

    /// <summary>
    /// Sets the sleep timer for both controllers.
    /// </summary>
    /// <param name="minutes">Minutes until sleep (0 = never).</param>
    /// <returns>True if both commands sent successfully.</returns>
    public bool SetSleepTimerBoth(int minutes)
    {
        bool success = SetSleepTimer(Controller.Left, minutes);
        Thread.Sleep(CommandDelayMs);
        success &= SetSleepTimer(Controller.Right, minutes);
        return success;
    }

    #endregion

    #region Stick Lighting

    /// <summary>
    /// Enables or disables stick RGB lighting on a controller.
    ///
    /// HID Command: 05 06 70 02 [controller] [0/1] 01
    /// </summary>
    /// <param name="controller">Left or Right controller.</param>
    /// <param name="enabled">True to enable RGB, false to disable.</param>
    /// <returns>True if command sent successfully.</returns>
    public bool SetStickLightEnabled(Controller controller, bool enabled)
    {
        var command = CreateCommand(
            0x06, 0x70, 0x02,
            (byte)controller,
            (byte)(enabled ? 0x01 : 0x00),
            0x01
        );
        return SendCommand(command);
    }

    /// <summary>
    /// Sets the RGB profile for a controller's stick light.
    ///
    /// HID Command: 05 0C 72 01 [controller] [mode] [R] [G] [B] [brightness] [speed] [profile] 01
    /// </summary>
    /// <param name="controller">Left or Right controller.</param>
    /// <param name="mode">Light mode (Solid, Pulse, Dynamic, Spiral).</param>
    /// <param name="red">Red component (0-255).</param>
    /// <param name="green">Green component (0-255).</param>
    /// <param name="blue">Blue component (0-255).</param>
    /// <param name="brightness">Brightness (0.0-1.0, default 1.0).</param>
    /// <param name="speed">Animation speed (0.0-1.0, 1.0 fastest, default 0.5).</param>
    /// <param name="profile">Profile slot (default 0x03).</param>
    /// <returns>True if command sent successfully.</returns>
    public bool SetStickLightProfile(
        Controller controller,
        StickLightMode mode,
        byte red, byte green, byte blue,
        float brightness = 1.0f,
        float speed = 0.5f,
        byte profile = 0x03)
    {
        // Firmware uses raw 0–100 for brightness and inverted 0–100 for speed
        // (raw byte = 100 − percent). Verified against b0:01 readbacks.
        byte brightnessByte = (byte)Clamp((int)(100 * brightness), 0, 100);
        byte speedByte = (byte)Clamp((int)(100 * (1 - speed)), 0, 100);

        var command = CreateCommand(
            0x0C, 0x72, 0x01,
            (byte)controller,
            (byte)mode,
            red, green, blue,
            brightnessByte,
            speedByte,
            profile,
            0x01
        );
        return SendCommand(command);
    }

    /// <summary>
    /// Loads (applies) a previously saved RGB profile.
    ///
    /// HID Command: 05 06 73 02 [controller] [profile] 01
    /// </summary>
    /// <param name="controller">Left or Right controller.</param>
    /// <param name="profile">Profile slot to load (default 0x03).</param>
    /// <returns>True if command sent successfully.</returns>
    public bool LoadStickLightProfile(Controller controller, byte profile = 0x03)
    {
        var command = CreateCommand(
            0x06, 0x73, 0x02,
            (byte)controller,
            profile,
            0x01
        );
        return SendCommand(command);
    }

    /// <summary>
    /// Sets stick light mode and color for both controllers.
    /// </summary>
    /// <param name="mode">Light mode.</param>
    /// <param name="red">Red component (0-255).</param>
    /// <param name="green">Green component (0-255).</param>
    /// <param name="blue">Blue component (0-255).</param>
    /// <param name="brightness">Brightness (0.0-1.0).</param>
    /// <param name="speed">Animation speed (0.0-1.0).</param>
    /// <returns>True if all commands sent successfully.</returns>
    public bool SetStickLightBoth(
        StickLightMode mode,
        byte red, byte green, byte blue,
        float brightness = 1.0f,
        float speed = 0.5f)
    {
        bool success = true;
        success &= SetStickLightProfile(Controller.Left, mode, red, green, blue, brightness, speed);
        Thread.Sleep(CommandDelayMs);
        success &= SetStickLightProfile(Controller.Right, mode, red, green, blue, brightness, speed);
        Thread.Sleep(CommandDelayMs);
        success &= LoadStickLightProfile(Controller.Left);
        Thread.Sleep(CommandDelayMs);
        success &= LoadStickLightProfile(Controller.Right);
        return success;
    }

    #endregion

    #region Raw Commands

    /// <summary>
    /// Sends a raw HID command (for advanced use/debugging).
    /// </summary>
    /// <param name="hexString">Hex string like "05 07 6C 02 03 1C 12 01".</param>
    /// <returns>True if command sent successfully.</returns>
    public bool SendRawCommand(string hexString)
    {
        var bytes = ParseHexString(hexString);
        if (bytes.Length == 0)
            return false;

        var command = CreateRawCommand(bytes);
        return SendCommand(command);
    }

    /// <summary>
    /// Reads a response from the controller.
    /// </summary>
    /// <param name="timeoutMs">Read timeout in milliseconds.</param>
    /// <returns>Response bytes, or null if no response/timeout.</returns>
    public byte[]? ReadResponse(int timeoutMs = 100)
    {
        if (!IsConnected || _stream == null)
            return null;

        try
        {
            _stream.ReadTimeout = timeoutMs;
            byte[] buffer = new byte[64];
            int bytesRead = _stream.Read(buffer, 0, buffer.Length);

            if (bytesRead > 0)
            {
                var response = new byte[bytesRead];
                Array.Copy(buffer, response, bytesRead);
                CommandExecuted?.Invoke(this, new HidCommandEventArgs(response, false));
                return response;
            }
        }
        catch { }

        return null;
    }

    #endregion

    #region Device Status (b0:01)

    /// <summary>
    /// Latest parsed device status from the most recent b0:01 response, or null
    /// if no status has been received yet.
    /// </summary>
    public LegionGoStatus? LatestDeviceStatus
    {
        get { lock (_statusLock) return _latestStatus; }
    }

    /// <summary>
    /// Sends a b0:01 status request and waits for the response. Returns parsed
    /// status or null on timeout / not connected.
    ///
    /// Firmware takes ~150ms to populate the response, so the default timeout is
    /// generous. Safe to call whether or not <see cref="StartBatteryMonitoring"/>
    /// is active — when monitoring is on the battery thread routes the response
    /// via <see cref="DeviceStatusUpdated"/>; when off this method polls the
    /// stream directly.
    /// </summary>
    public LegionGoStatus? ReadDeviceStatus(int timeoutMs = 500)
    {
        if (!IsConnected || _stream == null)
            return null;

        var request = CreateCommand(0xB0, 0x01, 0x00);

        if (_monitoringBattery)
        {
            var waiter = new ManualResetEventSlim(false);
            lock (_statusLock) { _statusWaiter = waiter; }
            try
            {
                if (!SendCommand(request))
                    return null;
                if (!waiter.Wait(timeoutMs))
                    return null;
                lock (_statusLock) return _latestStatus;
            }
            finally
            {
                lock (_statusLock) { _statusWaiter = null; }
                waiter.Dispose();
            }
        }

        if (!SendCommand(request))
            return null;

        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            int remaining = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
            if (remaining <= 0) break;
            var response = ReadResponse(Math.Min(200, remaining));
            if (response == null) continue;

            var status = TryParseDeviceStatus(response);
            if (status != null)
            {
                lock (_statusLock) { _latestStatus = status; }
                DeviceStatusUpdated?.Invoke(this, status);
                return status;
            }
        }
        return null;
    }

    /// <summary>
    /// Parses a 64-byte HID input report into a <see cref="LegionGoStatus"/>.
    /// Returns null if the report isn't a b0:01 response.
    ///
    /// Byte map (offsets into the 64-byte report, validated against captured traffic):
    ///   [0..4]   header  04 00 B0 01 00
    ///   [8..10]  RGB     red, green, blue
    ///   [11]     brightness (0–100, raw decimal)
    ///   [12]     light mode (0=Solid, 1=Pulse, 2=Dynamic, 3=Spiral; setter enum − 1;
    ///            0xFF=light disabled — sentinel; RGB/brightness/speed all reset to defaults)
    ///   [13]     animation speed, raw = 100 − percent (so 12 → 88%, 32 → 68% default)
    ///   [15]     vibration (2=Weak, 3=Medium*, 4=Strong)
    ///   [18]     touchpad (1=on, 2=off)
    ///   [21][22] battery left/right (1–100)
    ///   [28..31] firmware version, 4 bytes (e.g. "02 51 02 1A" → "0251021A")
    /// (* Medium not directly observed; inferred from the weak/strong contrast.)
    /// </summary>
    public static LegionGoStatus? TryParseDeviceStatus(byte[] response)
    {
        if (response == null || response.Length < 32) return null;
        if (response[0] != 0x04 || response[1] != 0x00 ||
            response[2] != 0xB0 || response[3] != 0x01 || response[4] != 0x00)
            return null;

        return new LegionGoStatus
        {
            Red = response[8],
            Green = response[9],
            Blue = response[10],
            Brightness = response[11],
            LightModeRaw = response[12],
            Speed = (byte)Math.Max(0, Math.Min(100, 100 - response[13])),
            VibrationRaw = response[15],
            TouchpadEnabled = response[18] == 0x01,
            LeftBattery = response[21],
            RightBattery = response[22],
            // Bytes 28..30 are a little-endian version number rendered MSB-first, byte 31
            // is a trailing suffix. Printing raw wire order showed "3160020A" where
            // Legion Space shows "0260310A" for the same firmware (field-confirmed
            // 2026-07-21) - swap 28<->30 to match Lenovo's own rendering.
            FirmwareVersion = string.Format("{0:X2}{1:X2}{2:X2}{3:X2}",
                response[30], response[29], response[28], response[31]),
        };
    }

    #endregion

    #region Battery Monitoring

    /// <summary>
    /// Starts continuous battery monitoring in a background thread.
    /// Battery status is read from input reports pushed by the controllers.
    /// </summary>
    public void StartBatteryMonitoring()
    {
        if (_monitoringBattery)
            return;

        _monitoringBattery = true;
        _batteryMonitorThread = new Thread(ReadBatteryReports)
        {
            IsBackground = true,
            Name = "LegionGo-BatteryMonitor"
        };
        _batteryMonitorThread.Start();
    }

    /// <summary>
    /// Stops the battery monitoring thread.
    /// </summary>
    public void StopBatteryMonitoring()
    {
        _monitoringBattery = false;
        if (_batteryMonitorThread != null && _batteryMonitorThread.IsAlive)
        {
            _batteryMonitorThread.Join(500);
            _batteryMonitorThread = null;
        }
    }

    /// <summary>
    /// Background thread that reads battery status from input reports.
    /// Report format: 04 00 a1 [leftBat] [leftStatus] [rightBat] [rightStatus]
    /// Battery values: 0x01-0x64 (1-100%)
    /// Status: 0x01=discharging, 0x04=charging
    /// </summary>
    private void ReadBatteryReports()
    {
        byte[] buffer = new byte[64];
        int readCount = 0;
        int batteryReportCount = 0;

        while (_monitoringBattery && IsConnected && _stream != null)
        {
            try
            {
                _stream.ReadTimeout = 500;
                int bytesRead = _stream.Read(buffer, 0, buffer.Length);
                readCount++;

                // Log first few reads to debug what we're receiving
                if (readCount <= 5)
                {
                    System.Diagnostics.Debug.WriteLine($"[BatteryMonitor] Read #{readCount}: {bytesRead} bytes - {FormatHex(buffer, Math.Min(bytesRead, 10))}");
                }

                // Check for b0:01 device status response and route it via DeviceStatusUpdated.
                if (bytesRead >= 32 && buffer[0] == 0x04 && buffer[1] == 0x00 &&
                    buffer[2] == 0xB0 && buffer[3] == 0x01 && buffer[4] == 0x00)
                {
                    var status = TryParseDeviceStatus(buffer);
                    if (status != null)
                    {
                        ManualResetEventSlim? waiter;
                        lock (_statusLock)
                        {
                            _latestStatus = status;
                            waiter = _statusWaiter;
                        }
                        DeviceStatusUpdated?.Invoke(this, status);
                        waiter?.Set();
                    }
                    continue;
                }

                // Check for battery report format: 04 00 a1 ...
                if (bytesRead >= 7 && buffer[0] == 0x04 && buffer[1] == 0x00 && buffer[2] == 0xa1)
                {
                    int leftBattery = buffer[3];
                    bool leftCharging = buffer[4] == 0x04;
                    int rightBattery = buffer[5];
                    bool rightCharging = buffer[6] == 0x04;

                    batteryReportCount++;
                    if (batteryReportCount <= 3)
                    {
                        System.Diagnostics.Debug.WriteLine($"[BatteryMonitor] Battery report #{batteryReportCount}: L={leftBattery}% ({(leftCharging ? "charging" : "discharging")}), R={rightBattery}% ({(rightCharging ? "charging" : "discharging")})");
                    }

                    // Validate battery values (1-100)
                    if (leftBattery >= 1 && leftBattery <= 100 && rightBattery >= 1 && rightBattery <= 100)
                    {
                        bool changed = _leftControllerBattery != leftBattery ||
                                       _rightControllerBattery != rightBattery ||
                                       _leftControllerCharging != leftCharging ||
                                       _rightControllerCharging != rightCharging;

                        _leftControllerBattery = leftBattery;
                        _leftControllerCharging = leftCharging;
                        _rightControllerBattery = rightBattery;
                        _rightControllerCharging = rightCharging;

                        if (changed)
                        {
                            System.Diagnostics.Debug.WriteLine($"[BatteryMonitor] Raising BatteryUpdated event: L={leftBattery}%, R={rightBattery}%");
                            BatteryUpdated?.Invoke(this, new ControllerBatteryEventArgs(
                                leftBattery, leftCharging, rightBattery, rightCharging));
                        }
                    }
                }
            }
            catch (TimeoutException)
            {
                // Normal timeout, continue reading
            }
            catch (Exception ex)
            {
                // Connection lost or other error
                System.Diagnostics.Debug.WriteLine($"[BatteryMonitor] Error reading: {ex.Message}");
                Thread.Sleep(100);
            }
        }

        // Reset values when monitoring stops
        _leftControllerBattery = -1;
        _rightControllerBattery = -1;
        _leftControllerCharging = false;
        _rightControllerCharging = false;
    }

    #endregion

    #region Utility Methods

    /// <summary>
    /// Formats a command as a hex string for display.
    /// </summary>
    public static string FormatHex(byte[] data, int maxBytes = 16)
    {
        var bytes = data.Take(maxBytes).ToArray();
        return BitConverter.ToString(bytes).Replace("-", " ") + (data.Length > maxBytes ? "..." : "");
    }

    /// <summary>
    /// Parses a hex string into bytes.
    /// Accepts: "05 07 6C", "05076C", "0x05 0x07"
    /// </summary>
    public static byte[] ParseHexString(string hexString)
    {
        try
        {
            hexString = hexString.Replace("0x", "").Replace("0X", "")
                                 .Replace(" ", "").Replace("-", "")
                                 .Replace(",", "").Trim();

            if (hexString.Length % 2 != 0)
                return Array.Empty<byte>();

            byte[] bytes = new byte[hexString.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(hexString.Substring(i * 2, 2), 16);

            return bytes;
        }
        catch
        {
            return Array.Empty<byte>();
        }
    }

    #endregion

    #region Private Methods

    private byte[] CreateCommand(params byte[] data)
    {
        byte[] command = new byte[CommandLength];
        command[0] = ReportId;

        for (int i = 0; i < data.Length && i + 1 < CommandLength; i++)
            command[i + 1] = data[i];

        for (int i = data.Length + 1; i < CommandLength; i++)
            command[i] = PaddingByte;

        return command;
    }

    private byte[] CreateRawCommand(byte[] data)
    {
        byte[] command = new byte[CommandLength];
        int copyLength = Math.Min(data.Length, CommandLength);
        Array.Copy(data, command, copyLength);

        for (int i = copyLength; i < CommandLength; i++)
            command[i] = PaddingByte;

        return command;
    }

    private bool SendCommand(byte[] command)
    {
        if (!IsConnected || _stream == null)
            return false;

        try
        {
            _stream.Write(command);
            CommandExecuted?.Invoke(this, new HidCommandEventArgs(command, true));
            return true;
        }
        catch
        {
            Disconnect();
            return false;
        }
    }

    #endregion

    #region IDisposable

    public void Dispose()
    {
        if (!_disposed)
        {
            Disconnect();
            _disposed = true;
        }
    }

    #endregion
}

#region Data Classes

/// <summary>
/// Snapshot of controller state read from a b0:01 status response.
/// Raw firmware values are exposed alongside parsed booleans where the encoding
/// is non-trivial (mode/vibration use different enums than their setters).
/// </summary>
public sealed class LegionGoStatus
{
    public byte Red { get; set; }
    public byte Green { get; set; }
    public byte Blue { get; set; }
    /// <summary>Brightness 0–100 (raw decimal from firmware).</summary>
    public byte Brightness { get; set; }
    /// <summary>
    /// Light mode reported by firmware. Confirmed values (mapped to GoTweaks/setter names):
    /// 0 = Solid, 1 = Pulse, 2 = Dynamic, 3 = Spiral,
    /// 0xFF = light disabled (sentinel — RGB also reports FF FF FF and
    /// speed/brightness reset to defaults).
    /// Readback values are exactly <see cref="StickLightMode"/> − 1
    /// (setter Solid=1, Pulse=2, Dynamic=3, Spiral=4).
    /// </summary>
    public byte LightModeRaw { get; set; }
    /// <summary>
    /// Whether the stick light is on. On the button-monitor b0:01 stream the on/off flag is a
    /// dedicated byte (byte 6: 0x01=on, 0x02=off), independent of the animation mode (byte 12).
    /// When a parser knows that flag it sets this explicitly; otherwise it falls back to the
    /// legacy heuristic (mode != 0xFF). Verified via live capture 2026-05-29.
    /// </summary>
    public bool? LightOnFlag { get; set; }
    /// <summary>True when the stick light is enabled.</summary>
    public bool LightEnabled => LightOnFlag ?? (LightModeRaw != 0xFF);
    /// <summary>
    /// Animation speed, 0–100 (already inverted from the firmware's raw byte
    /// which stores 100 − percent; 0 = slowest, 100 = fastest).
    /// </summary>
    public byte Speed { get; set; }
    /// <summary>
    /// Vibration level reported by firmware. Observed values: 2 = weak, 4 = strong
    /// (3 = medium inferred). Does NOT match <see cref="VibrationLevel"/> setter enum.
    /// </summary>
    public byte VibrationRaw { get; set; }
    public bool TouchpadEnabled { get; set; }
    /// <summary>Left controller battery (1–100), 0 if absent/asleep.</summary>
    public byte LeftBattery { get; set; }
    /// <summary>Right controller battery (1–100), 0 if absent/asleep.</summary>
    public byte RightBattery { get; set; }
    /// <summary>Firmware version, e.g. "0251021A".</summary>
    public string FirmwareVersion { get; set; } = "";
}

#endregion

#region Enums

/// <summary>
/// Controller identifier (Left=0x03, Right=0x04).
/// </summary>
public enum Controller : byte
{
    Left = 0x03,
    Right = 0x04
}

/// <summary>
/// Remappable buttons on the Legion Go controllers.
/// </summary>
public enum RemappableButton : byte
{
    /// <summary>Left controller back button (upper)</summary>
    Y1 = 0x1C,
    /// <summary>Left controller back button (lower)</summary>
    Y2 = 0x1D,
    /// <summary>Right controller back button</summary>
    Y3 = 0x1E,
    /// <summary>Right controller grip button (Legion button)</summary>
    M1 = 0x20,
    /// <summary>Right controller grip button (upper)</summary>
    M2 = 0x21,
    /// <summary>Right controller grip button (lower)</summary>
    M3 = 0x22
}

/// <summary>
/// Standard gamepad buttons that can be remapped.
/// These buttons can be mapped to keyboard, mouse, or other gamepad buttons.
/// </summary>
public enum GamepadButton : byte
{
    LSClick = 0x03,
    LSUp = 0x04,
    LSDown = 0x05,
    LSLeft = 0x06,
    LSRight = 0x07,
    RSClick = 0x08,
    RSUp = 0x09,
    RSDown = 0x0A,
    RSLeft = 0x0B,
    RSRight = 0x0C,
    DPadUp = 0x0D,
    DPadDown = 0x0E,
    DPadLeft = 0x0F,
    DPadRight = 0x10,
    A = 0x12,
    B = 0x13,
    X = 0x14,
    Y = 0x15,
    LB = 0x16,
    LT = 0x17,
    RB = 0x18,
    RT = 0x19,
    Start = 0x23,
    Select = 0x24,
    /// <summary>Legion Desktop button (left controller, default: Win+G for Game Bar)</summary>
    DesktopButton = 0x25,
    /// <summary>Legion Page button (left controller, default: Win+Tab for Task View)</summary>
    PageButton = 0x26
}

/// <summary>
/// Mapping type for button remapping.
/// </summary>
public enum MappingType : byte
{
    /// <summary>Map to gamepad button</summary>
    Gamepad = 0x01,
    /// <summary>Map to keyboard key(s)</summary>
    Keyboard = 0x02,
    /// <summary>Map to mouse button</summary>
    Mouse = 0x03
}

/// <summary>
/// Keyboard key codes for button remapping.
/// HID usage codes from USB HID Usage Tables.
/// </summary>
public enum KeyboardKey : byte
{
    None = 0x00,
    A = 0x04, B = 0x05, C = 0x06, D = 0x07, E = 0x08, F = 0x09, G = 0x0A,
    H = 0x0B, I = 0x0C, J = 0x0D, K = 0x0E, L = 0x0F, M = 0x10, N = 0x11,
    O = 0x12, P = 0x13, Q = 0x14, R = 0x15, S = 0x16, T = 0x17, U = 0x18,
    V = 0x19, W = 0x1A, X = 0x1B, Y = 0x1C, Z = 0x1D,
    Num1 = 0x1E, Num2 = 0x1F, Num3 = 0x20, Num4 = 0x21, Num5 = 0x22,
    Num6 = 0x23, Num7 = 0x24, Num8 = 0x25, Num9 = 0x26, Num0 = 0x27,
    Enter = 0x28, Escape = 0x29, Backspace = 0x2A, Tab = 0x2B, Space = 0x2C,
    Minus = 0x2D, Equals = 0x2E, LeftBracket = 0x2F, RightBracket = 0x30,
    Backslash = 0x31, Semicolon = 0x33, Quote = 0x34, Grave = 0x35,
    Comma = 0x36, Period = 0x37, Slash = 0x38, CapsLock = 0x39,
    F1 = 0x3A, F2 = 0x3B, F3 = 0x3C, F4 = 0x3D, F5 = 0x3E, F6 = 0x3F,
    F7 = 0x40, F8 = 0x41, F9 = 0x42, F10 = 0x43, F11 = 0x44, F12 = 0x45,
    PrintScreen = 0x46, ScrollLock = 0x47, Pause = 0x48,
    Insert = 0x49, Home = 0x4A, PageUp = 0x4B, Delete = 0x4C, End = 0x4D, PageDown = 0x4E,
    Right = 0x4F, Left = 0x50, Down = 0x51, Up = 0x52,
    NumLock = 0x53, NumDivide = 0x54, NumMultiply = 0x55, NumMinus = 0x56,
    NumPlus = 0x57, NumEnter = 0x58, Num1Pad = 0x59, Num2Pad = 0x5A,
    Num3Pad = 0x5B, Num4Pad = 0x5C, Num5Pad = 0x5D, Num6Pad = 0x5E,
    Num7Pad = 0x5F, Num8Pad = 0x60, Num9Pad = 0x61, Num0Pad = 0x62, NumDecimal = 0x63,
    // Modifier keys
    LCtrl = 0xE0, LShift = 0xE1, LAlt = 0xE2, LMeta = 0xE3,
    RCtrl = 0xE4, RShift = 0xE5, RAlt = 0xE6, RMeta = 0xE7
}

/// <summary>
/// Mouse button codes for button remapping.
/// </summary>
public enum MouseButton : byte
{
    LeftClick = 0x01,
    RightClick = 0x02,
    MiddleClick = 0x03,
    ScrollUp = 0x04,
    ScrollDown = 0x05,
    ScrollLeft = 0x06,
    ScrollRight = 0x07
}

/// <summary>
/// Actions that can be assigned to remappable buttons.
/// HID values verified from Legion Space traffic captures.
/// </summary>
public enum RemapAction : byte
{
    Disabled = 0x00,
    // 0x01, 0x02 are skipped in HID protocol
    LeftStickClick = 0x03,
    LeftStickUp = 0x04,
    LeftStickDown = 0x05,
    LeftStickLeft = 0x06,
    LeftStickRight = 0x07,
    RightStickClick = 0x08,
    RightStickUp = 0x09,
    RightStickDown = 0x0A,
    RightStickLeft = 0x0B,
    RightStickRight = 0x0C,
    DpadUp = 0x0D,
    DpadDown = 0x0E,
    DpadLeft = 0x0F,
    DpadRight = 0x10,
    // 0x11 is skipped in HID protocol
    A = 0x12,
    B = 0x13,
    X = 0x14,
    Y = 0x15,
    LeftBumper = 0x16,
    LeftTrigger = 0x17,
    RightBumper = 0x18,
    RightTrigger = 0x19,
    // 0x1A-0x22 are skipped in HID protocol
    View = 0x23,
    Menu = 0x24,
    /// <summary>Legion Desktop button (Win+G default)</summary>
    DesktopButton = 0x25,
    /// <summary>Legion Page button (Win+Tab default)</summary>
    PageButton = 0x26
}

/// <summary>
/// Maps ComboBox indices to RemapAction values.
/// Used because HID action values have gaps (0x01-0x02, 0x11, 0x1A-0x22 are skipped).
/// </summary>
public static class RemapActionHelper
{
    private static readonly RemapAction[] IndexToAction = {
        RemapAction.Disabled,       // 0
        RemapAction.LeftStickClick, // 1
        RemapAction.LeftStickUp,    // 2
        RemapAction.LeftStickDown,  // 3
        RemapAction.LeftStickLeft,  // 4
        RemapAction.LeftStickRight, // 5
        RemapAction.RightStickClick,// 6
        RemapAction.RightStickUp,   // 7
        RemapAction.RightStickDown, // 8
        RemapAction.RightStickLeft, // 9
        RemapAction.RightStickRight,// 10
        RemapAction.DpadUp,         // 11
        RemapAction.DpadDown,       // 12
        RemapAction.DpadLeft,       // 13
        RemapAction.DpadRight,      // 14
        RemapAction.A,              // 15
        RemapAction.B,              // 16
        RemapAction.X,              // 17
        RemapAction.Y,              // 18
        RemapAction.LeftBumper,     // 19
        RemapAction.LeftTrigger,    // 20
        RemapAction.RightBumper,    // 21
        RemapAction.RightTrigger,   // 22
        RemapAction.View,           // 23
        RemapAction.Menu,           // 24
        RemapAction.DesktopButton,  // 25
        RemapAction.PageButton,     // 26
    };

    /// <summary>
    /// Gets the RemapAction for a ComboBox index.
    /// </summary>
    public static RemapAction GetByIndex(int index) =>
        index >= 0 && index < IndexToAction.Length
            ? IndexToAction[index]
            : RemapAction.Disabled;
}

/// <summary>
/// Vibration intensity levels.
/// </summary>
public enum VibrationLevel : byte
{
    Off = 0x00,
    Weak = 0x01,
    Medium = 0x02,
    Strong = 0x03
}

/// <summary>
/// Vibration mode presets (game-specific patterns).
/// </summary>
public enum VibrationMode : byte
{
    FPS = 0x01,
    Racing = 0x02,
    AVG = 0x03,
    SPG = 0x04,
    RPG = 0x05
}

/// <summary>
/// Touchpad haptic vibration levels.
/// Command: 05 00 06 06 00 [level]
/// </summary>
public enum TouchpadVibrationLevel : byte
{
    Off = 0x01,
    Low = 0x02,
    Medium = 0x03,
    High = 0x04
}

/// <summary>
/// Stick light modes for RGB lighting.
/// </summary>
public enum StickLightMode : byte
{
    /// <summary>Solid static color</summary>
    Solid = 1,
    /// <summary>Pulsing/breathing effect</summary>
    Pulse = 2,
    /// <summary>Dynamic color cycling</summary>
    Dynamic = 3,
    /// <summary>Spiral animation effect</summary>
    Spiral = 4
}

/// <summary>
/// Gyro output target.
/// </summary>
public enum GyroTarget : byte
{
    /// <summary>Gyro disabled</summary>
    Disabled = 0x01,
    /// <summary>Gyro controls left analog stick</summary>
    LeftStick = 0x02,
    /// <summary>Gyro controls right analog stick</summary>
    RightStick = 0x03,
    /// <summary>Gyro simulates mouse movement</summary>
    Mouse = 0x04
}

/// <summary>
/// Gyro response type.
/// </summary>
public enum GyroMappingType : byte
{
    /// <summary>Instant/snappy response</summary>
    Instant = 0x01,
    /// <summary>Smooth/continuous response</summary>
    Continuous = 0x02
}

/// <summary>
/// Buttons that can activate gyro (max 5 can be selected).
/// </summary>
public enum GyroActivationButton : byte
{
    None = 0x00,
    A = 0x12,
    B = 0x13,
    X = 0x14,
    Y = 0x15,
    LB = 0x16,
    LT = 0x17,
    RB = 0x18,
    RT = 0x19,
    /// <summary>Left controller back button (upper)</summary>
    Y1 = 0x1C,
    /// <summary>Left controller back button (lower)</summary>
    Y2 = 0x1D,
    /// <summary>Right controller grip button (M1 position)</summary>
    M1 = 0x20,
    /// <summary>Right controller grip button (upper)</summary>
    M2 = 0x21,
    /// <summary>Right controller grip button (lower)</summary>
    M3 = 0x22
}

/// <summary>
/// Gyro activation mode (how the activation button works).
/// </summary>
public enum GyroActivationMode : byte
{
    /// <summary>Gyro active while button is held</summary>
    Hold = 0x02,
    /// <summary>Button toggles gyro on/off</summary>
    Toggle = 0x03
}

/// <summary>
/// Face buttons (A, B, X, Y) for remapping.
/// Used with SetNintendoLayout() and SetFaceButtonRemap().
/// </summary>
public enum FaceButton : byte
{
    /// <summary>A button (bottom position on Xbox layout)</summary>
    A = 0x12,
    /// <summary>B button (right position on Xbox layout)</summary>
    B = 0x13,
    /// <summary>X button (left position on Xbox layout)</summary>
    X = 0x14,
    /// <summary>Y button (top position on Xbox layout)</summary>
    Y = 0x15
}

#endregion

#region Event Args

/// <summary>
/// Event args for HID command logging.
/// </summary>
public class HidCommandEventArgs : EventArgs
{
    /// <summary>The command data.</summary>
    public byte[] Data { get; }

    /// <summary>True if sent to device, false if received from device.</summary>
    public bool IsSent { get; }

    /// <summary>Timestamp when the command was executed.</summary>
    public DateTime Timestamp { get; }

    /// <summary>Hex string representation of the command.</summary>
    public string Hex => LegionGoController.FormatHex(Data);

    public HidCommandEventArgs(byte[] data, bool isSent)
    {
        Data = data;
        IsSent = isSent;
        Timestamp = DateTime.Now;
    }
}

/// <summary>
/// Event args for controller battery status updates.
/// </summary>
public class ControllerBatteryEventArgs : EventArgs
{
    /// <summary>Left controller battery percentage (1-100).</summary>
    public int LeftBattery { get; }

    /// <summary>Whether the left controller is charging.</summary>
    public bool LeftCharging { get; }

    /// <summary>Right controller battery percentage (1-100).</summary>
    public int RightBattery { get; }

    /// <summary>Whether the right controller is charging.</summary>
    public bool RightCharging { get; }

    /// <summary>Timestamp when the battery status was read.</summary>
    public DateTime Timestamp { get; }

    public ControllerBatteryEventArgs(int leftBattery, bool leftCharging, int rightBattery, bool rightCharging)
    {
        LeftBattery = leftBattery;
        LeftCharging = leftCharging;
        RightBattery = rightBattery;
        RightCharging = rightCharging;
        Timestamp = DateTime.Now;
    }
}

#endregion

} // namespace XboxGamingBarHelper.Devices.Libraries.Legion
