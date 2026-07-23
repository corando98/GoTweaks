using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using NLog;
using XboxGamingBarHelper.Settings;

namespace XboxGamingBarHelper.Core
{
    /// <summary>
    /// Monitors Xbox controller for button combos using XInput.
    /// Works system-wide including in most games (but not Steam BPM or Moonlight).
    /// </summary>
    internal class ControllerHotkeyMonitor : IDisposable
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        #region XInput P/Invoke

        // XInput constants
        private const uint ERROR_SUCCESS = 0;
        private const uint ERROR_DEVICE_NOT_CONNECTED = 1167;

        // Gamepad button flags
        private const ushort XINPUT_GAMEPAD_DPAD_UP = 0x0001;
        private const ushort XINPUT_GAMEPAD_DPAD_DOWN = 0x0002;
        private const ushort XINPUT_GAMEPAD_DPAD_LEFT = 0x0004;
        private const ushort XINPUT_GAMEPAD_DPAD_RIGHT = 0x0008;
        private const ushort XINPUT_GAMEPAD_START = 0x0010;      // Menu button (right, three lines)
        private const ushort XINPUT_GAMEPAD_BACK = 0x0020;       // View button (left, two squares)
        private const ushort XINPUT_GAMEPAD_LEFT_THUMB = 0x0040;
        private const ushort XINPUT_GAMEPAD_RIGHT_THUMB = 0x0080;
        private const ushort XINPUT_GAMEPAD_LEFT_SHOULDER = 0x0100;
        private const ushort XINPUT_GAMEPAD_RIGHT_SHOULDER = 0x0200;
        private const ushort XINPUT_GAMEPAD_A = 0x1000;
        private const ushort XINPUT_GAMEPAD_B = 0x2000;
        private const ushort XINPUT_GAMEPAD_X = 0x4000;
        private const ushort XINPUT_GAMEPAD_Y = 0x8000;

        [StructLayout(LayoutKind.Sequential)]
        private struct XINPUT_GAMEPAD
        {
            public ushort wButtons;
            public byte bLeftTrigger;
            public byte bRightTrigger;
            public short sThumbLX;
            public short sThumbLY;
            public short sThumbRX;
            public short sThumbRY;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct XINPUT_STATE
        {
            public uint dwPacketNumber;
            public XINPUT_GAMEPAD Gamepad;
        }

        // Try multiple XInput DLLs for compatibility
        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
        private static extern uint XInputGetState14(uint dwUserIndex, ref XINPUT_STATE pState);

        [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")]
        private static extern uint XInputGetState910(uint dwUserIndex, ref XINPUT_STATE pState);

        private delegate uint XInputGetStateDelegate(uint dwUserIndex, ref XINPUT_STATE pState);
        private static XInputGetStateDelegate XInputGetState;

        #endregion

        // Combo detection
        private ushort _lastButtons = 0;
        private ushort _lastVendorButtons;
        private uint _lastPacketNumber = 0;
        private DateTime _comboStartTime = DateTime.MinValue;
        private ushort _comboModifier = 0;
        private ushort _comboButton = 0;
        private const int COMBO_HOLD_MS = 50; // How long buttons must be held together

        // Quick-tile controller combos (arbitrary >=2-button bitmask -> callback). Runs
        // alongside the fixed legacy combos above. Standard buttons use XInput bit values;
        // Legion back paddles arrive as high bits via SetExtraButtons (fed from
        // LegionButtonMonitor.ButtonEdge, since XInput can't see them).
        private readonly object _tileHotkeyLock = new object();
        private readonly Dictionary<uint, Action> _tileHotkeys = new Dictionary<uint, Action>();
        private readonly Dictionary<uint, string> _tileHotkeyNames = new Dictionary<uint, string>();
        private volatile uint _extraButtonBits = 0;   // Legion paddle bits (0x10000+)
        private uint _activeTileMask = 0;              // latched match, cleared on release (no re-fire)
        private uint _tileComboCandidate = 0;
        private DateTime _tileComboStart = DateTime.MinValue;

        // Thread control
        private Thread _monitorThread;
        private volatile bool _running;
        private int _pollIntervalMs = 16; // ~60Hz polling

        // Callbacks for each combo type
        public Action OnMenuDPadUp { get; set; }
        public Action OnMenuDPadDown { get; set; }
        public Action OnMenuDPadLeft { get; set; }
        public Action OnMenuDPadRight { get; set; }
        public Action OnViewA { get; set; }
        public Action OnViewB { get; set; }
        public Action OnViewX { get; set; }
        public Action OnViewY { get; set; }

        // Enable/disable individual combos
        public bool MenuDPadUpEnabled { get; set; }
        public bool MenuDPadDownEnabled { get; set; }
        public bool MenuDPadLeftEnabled { get; set; }
        public bool MenuDPadRightEnabled { get; set; }
        public bool ViewAEnabled { get; set; }
        public bool ViewBEnabled { get; set; }
        public bool ViewXEnabled { get; set; }
        public bool ViewYEnabled { get; set; }

        /// <summary>
        /// True when at least one of the 8 combo bindings is enabled. When false the
        /// monitor's 60Hz XInput polling thread can be left stopped — it would only
        /// be burning CPU sampling state nothing would react to.
        /// </summary>
        public bool AnyComboEnabled =>
            MenuDPadUpEnabled || MenuDPadDownEnabled || MenuDPadLeftEnabled || MenuDPadRightEnabled ||
            ViewAEnabled || ViewBEnabled || ViewXEnabled || ViewYEnabled;

        /// <summary>
        /// True when the polling thread is currently running.
        /// </summary>
        public bool IsRunning => _running;

        /// <summary>True when any quick-tile combo is registered (keeps the poll thread alive).</summary>
        public bool HasTileHotkeys { get { lock (_tileHotkeyLock) { return _tileHotkeys.Count > 0; } } }

        /// <summary>Register a tile combo. mask is the OR of ControllerComboButtons bits (>=2).</summary>
        public void RegisterTileHotkey(uint mask, Action callback, string name)
        {
            if (mask == 0 || callback == null) return;
            lock (_tileHotkeyLock)
            {
                _tileHotkeys[mask] = callback;
                _tileHotkeyNames[mask] = name ?? "";
            }
        }

        public void ClearTileHotkeys()
        {
            lock (_tileHotkeyLock)
            {
                _tileHotkeys.Clear();
                _tileHotkeyNames.Clear();
            }
            _activeTileMask = 0;
            _tileComboCandidate = 0;
            _tileComboStart = DateTime.MinValue;
        }

        /// <summary>
        /// Feed the current Legion back-paddle state (high bits, Y1-Y3 / M1-M3) from
        /// LegionButtonMonitor. These are OR-ed into the XInput mask when matching tile combos.
        /// </summary>
        public void SetExtraButtons(uint extraBits) => _extraButtonBits = extraBits;

        public ControllerHotkeyMonitor()
        {
            // Try to load XInput - prefer 1.4 (Windows 8+), fall back to 9.1.0 (Vista+)
            try
            {
                // Test xinput1_4.dll
                var state = new XINPUT_STATE();
                XInputGetState14(0, ref state);
                XInputGetState = XInputGetState14;
                Logger.Info("ControllerHotkeyMonitor: Using xinput1_4.dll");
            }
            catch
            {
                try
                {
                    // Fall back to xinput9_1_0.dll
                    var state = new XINPUT_STATE();
                    XInputGetState910(0, ref state);
                    XInputGetState = XInputGetState910;
                    Logger.Info("ControllerHotkeyMonitor: Using xinput9_1_0.dll");
                }
                catch (Exception ex)
                {
                    Logger.Error($"ControllerHotkeyMonitor: Failed to load XInput: {ex.Message}");
                    XInputGetState = null;
                }
            }
        }

        public void Start()
        {
            if (XInputGetState == null)
            {
                Logger.Error("ControllerHotkeyMonitor: Cannot start - XInput not available");
                return;
            }

            if (_running)
            {
                Logger.Warn("ControllerHotkeyMonitor: Already running");
                return;
            }

            _running = true;
            _monitorThread = new Thread(MonitorLoop)
            {
                IsBackground = true,
                Name = "ControllerHotkeyMonitor",
                Priority = ThreadPriority.AboveNormal
            };
            _monitorThread.Start();
            Logger.Info("ControllerHotkeyMonitor: Started");
        }

        public void Stop()
        {
            _running = false;
            if (_monitorThread != null && _monitorThread.IsAlive)
            {
                _monitorThread.Join(1000);
            }
            Logger.Info("ControllerHotkeyMonitor: Stopped");
        }

        private void MonitorLoop()
        {
            var state = new XINPUT_STATE();
            bool[] wasConnected = new bool[4];
            uint[] lastPacketNumbers = new uint[4];

            while (_running)
            {
                try
                {
                    ushort combinedXInput = 0;

                    // Poll all 4 possible controller slots and process input from any with button activity
                    // This handles cases where ViGEmController is on a different slot than the real controller
                    for (uint i = 0; i < 4; i++)
                    {
                        uint result = XInputGetState(i, ref state);

                        if (result == ERROR_SUCCESS)
                        {
                            if (!wasConnected[i])
                            {
                                Logger.Info($"ControllerHotkeyMonitor: Controller {i} connected");
                                wasConnected[i] = true;
                            }

                            combinedXInput |= state.Gamepad.wButtons;

                            // Process if state changed for this controller
                            if (state.dwPacketNumber != lastPacketNumbers[i])
                            {
                                lastPacketNumbers[i] = state.dwPacketNumber;

                                // Only process if there are actual button presses
                                // This filters out virtual controllers with no input
                                if (state.Gamepad.wButtons != 0 || _lastButtons != 0)
                                {
                                    ProcessButtonState(state.Gamepad.wButtons);
                                }
                            }
                        }
                        else if (result == ERROR_DEVICE_NOT_CONNECTED && wasConnected[i])
                        {
                            Logger.Info($"ControllerHotkeyMonitor: Controller {i} disconnected");
                            wasConnected[i] = false;
                            lastPacketNumbers[i] = 0;
                        }
                    }

                    // Firmware XInput suppression (controller emulation, register 04/0f)
                    // removes the physical pad from XInput entirely - and DS-family
                    // emulated targets aren't XInput either - which silenced every
                    // System-tab hotkey and quick-tile combo while emulation ran (field
                    // report 2026-07-23). The button monitor's vendor-stream sample
                    // carries the same XInput-format button bitmask, so merge it in as
                    // an input source; it is fresh only while the vendor stream runs.
                    try
                    {
                        if (XboxGamingBarHelper.Labs.LegionButtonMonitor.TryGetLatestGamepadSample(out var vendorSample)
                            && (DateTime.UtcNow.Ticks - vendorSample.TimestampTicksUtc) < TimeSpan.TicksPerSecond)
                        {
                            combinedXInput |= vendorSample.Buttons;
                            if (vendorSample.Buttons != _lastVendorButtons)
                            {
                                _lastVendorButtons = vendorSample.Buttons;
                                if (vendorSample.Buttons != 0 || _lastButtons != 0)
                                {
                                    ProcessButtonState(vendorSample.Buttons);
                                }
                            }
                        }
                    }
                    catch { }

                    // Quick-tile combos are evaluated every pass (not gated on XInput packet
                    // changes) so all-paddle combos and the hold timer still work when the
                    // sticks are perfectly still. Cheap no-op when none are registered.
                    if (HasTileHotkeys)
                    {
                        EvaluateTileHotkeys((uint)combinedXInput | _extraButtonBits);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"ControllerHotkeyMonitor: Error polling controller: {ex.Message}");
                }

                Thread.Sleep(_pollIntervalMs);
            }
        }

        private void ProcessButtonState(ushort buttons)
        {
            // Log when modifier buttons are pressed (INFO level for visibility)
            if (buttons != _lastButtons)
            {
                if ((buttons & XINPUT_GAMEPAD_START) != 0 && (_lastButtons & XINPUT_GAMEPAD_START) == 0)
                    Logger.Info($"ControllerHotkeyMonitor: START/Menu pressed (buttons=0x{buttons:X4})");
                if ((buttons & XINPUT_GAMEPAD_BACK) != 0 && (_lastButtons & XINPUT_GAMEPAD_BACK) == 0)
                    Logger.Info($"ControllerHotkeyMonitor: BACK/View pressed (buttons=0x{buttons:X4})");
            }

            // Check for Menu (Start) + DPad combos
            if ((buttons & XINPUT_GAMEPAD_START) != 0)
            {
                CheckAndTriggerCombo(buttons, XINPUT_GAMEPAD_START, XINPUT_GAMEPAD_DPAD_UP, MenuDPadUpEnabled, OnMenuDPadUp, "Menu+DPadUp");
                CheckAndTriggerCombo(buttons, XINPUT_GAMEPAD_START, XINPUT_GAMEPAD_DPAD_DOWN, MenuDPadDownEnabled, OnMenuDPadDown, "Menu+DPadDown");
                CheckAndTriggerCombo(buttons, XINPUT_GAMEPAD_START, XINPUT_GAMEPAD_DPAD_LEFT, MenuDPadLeftEnabled, OnMenuDPadLeft, "Menu+DPadLeft");
                CheckAndTriggerCombo(buttons, XINPUT_GAMEPAD_START, XINPUT_GAMEPAD_DPAD_RIGHT, MenuDPadRightEnabled, OnMenuDPadRight, "Menu+DPadRight");
            }

            // Check for View (Back) + ABXY combos
            if ((buttons & XINPUT_GAMEPAD_BACK) != 0)
            {
                CheckAndTriggerCombo(buttons, XINPUT_GAMEPAD_BACK, XINPUT_GAMEPAD_A, ViewAEnabled, OnViewA, "View+A");
                CheckAndTriggerCombo(buttons, XINPUT_GAMEPAD_BACK, XINPUT_GAMEPAD_B, ViewBEnabled, OnViewB, "View+B");
                CheckAndTriggerCombo(buttons, XINPUT_GAMEPAD_BACK, XINPUT_GAMEPAD_X, ViewXEnabled, OnViewX, "View+X");
                CheckAndTriggerCombo(buttons, XINPUT_GAMEPAD_BACK, XINPUT_GAMEPAD_Y, ViewYEnabled, OnViewY, "View+Y");
            }

            // Reset combo tracking if neither modifier is pressed
            if ((buttons & (XINPUT_GAMEPAD_START | XINPUT_GAMEPAD_BACK)) == 0)
            {
                _comboModifier = 0;
                _comboButton = 0;
                _comboStartTime = DateTime.MinValue;
            }

            _lastButtons = buttons;
        }

        private void CheckAndTriggerCombo(ushort buttons, ushort modifier, ushort button, bool enabled, Action callback, string comboName)
        {
            if (!enabled)
                return;

            if (callback == null)
            {
                Logger.Warn($"ControllerHotkeyMonitor: {comboName} enabled but callback is null!");
                return;
            }

            bool modifierHeld = (buttons & modifier) != 0;
            bool buttonHeld = (buttons & button) != 0;

            if (modifierHeld && buttonHeld)
            {
                // Combo is being held
                if (_comboModifier != modifier || _comboButton != button)
                {
                    // New combo detected - log it
                    Logger.Info($"ControllerHotkeyMonitor: {comboName} combo detected, starting hold timer");
                    _comboModifier = modifier;
                    _comboButton = button;
                    _comboStartTime = DateTime.Now;
                }
                else if (_comboStartTime != DateTime.MinValue)
                {
                    // Check if held long enough
                    double heldMs = (DateTime.Now - _comboStartTime).TotalMilliseconds;
                    if (heldMs >= COMBO_HOLD_MS)
                    {
                        Logger.Info($"ControllerHotkeyMonitor: {comboName} triggered");
                        _comboStartTime = DateTime.MinValue; // Prevent re-triggering until released

                        // Execute callback on thread pool
                        ThreadPool.QueueUserWorkItem(_ =>
                        {
                            try
                            {
                                callback();
                            }
                            catch (Exception ex)
                            {
                                Logger.Error($"ControllerHotkeyMonitor: Error executing {comboName} callback: {ex.Message}");
                            }
                        });
                    }
                }
            }
        }

        /// <summary>
        /// Match the current combined button state against registered tile combos. Fires the
        /// most-specific (most-buttons) fully-satisfied combo after a short hold, and latches
        /// it so it can't re-fire until the buttons are released.
        /// </summary>
        private void EvaluateTileHotkeys(uint combined)
        {
            KeyValuePair<uint, Action>[] snapshot;
            lock (_tileHotkeyLock)
            {
                if (_tileHotkeys.Count == 0) return;
                snapshot = new KeyValuePair<uint, Action>[_tileHotkeys.Count];
                int n = 0;
                foreach (var kv in _tileHotkeys) snapshot[n++] = kv;
            }

            // Pick the fully-satisfied combo with the most buttons (so Menu+A+M1 wins over Menu+A).
            uint bestMask = 0;
            Action bestCb = null;
            int bestBits = 0;
            foreach (var kv in snapshot)
            {
                uint mask = kv.Key;
                int bits = CountBits(mask);
                if (bits < 2) continue;
                if ((combined & mask) == mask && bits > bestBits)
                {
                    bestBits = bits;
                    bestMask = mask;
                    bestCb = kv.Value;
                }
            }

            if (bestMask == 0)
            {
                _tileComboCandidate = 0;
                _tileComboStart = DateTime.MinValue;
                // Clear the latch once the previously-fired combo's buttons are released.
                if (_activeTileMask != 0 && (combined & _activeTileMask) != _activeTileMask)
                    _activeTileMask = 0;
                return;
            }

            // Already fired and still held - wait for release.
            if (_activeTileMask == bestMask) return;

            if (_tileComboCandidate != bestMask)
            {
                _tileComboCandidate = bestMask;
                _tileComboStart = DateTime.Now;
                return;
            }

            if (_tileComboStart != DateTime.MinValue &&
                (DateTime.Now - _tileComboStart).TotalMilliseconds >= COMBO_HOLD_MS)
            {
                _activeTileMask = bestMask;
                _tileComboCandidate = 0;
                _tileComboStart = DateTime.MinValue;

                string name;
                lock (_tileHotkeyLock) { _tileHotkeyNames.TryGetValue(bestMask, out name); }
                Logger.Info($"ControllerHotkeyMonitor: tile combo '{name}' (mask=0x{bestMask:X}) triggered");
                try { Program.goTweaksHapticManager?.PlayConfirmationPulse(); } catch { }

                var cb = bestCb;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { cb(); }
                    catch (Exception ex) { Logger.Error($"ControllerHotkeyMonitor: tile combo callback error: {ex.Message}"); }
                });
            }
        }

        private static int CountBits(uint v)
        {
            int c = 0;
            while (v != 0) { v &= (v - 1); c++; }
            return c;
        }

        /// <summary>
        /// Load hotkey settings using LocalSettingsHelper (works outside package context)
        /// </summary>
        public void LoadSettings()
        {
            try
            {
                // Menu + DPad combos (Menu button = Start/three-lines button)
                // Widget saves as "Hotkey_MenuDpadUp_Action", etc.
                if (LocalSettingsHelper.TryGetValue<int>("Hotkey_MenuDpadUp_Action", out var actionUp))
                    MenuDPadUpEnabled = actionUp > 0;

                if (LocalSettingsHelper.TryGetValue<int>("Hotkey_MenuDpadDown_Action", out var actionDown))
                    MenuDPadDownEnabled = actionDown > 0;

                if (LocalSettingsHelper.TryGetValue<int>("Hotkey_MenuDpadLeft_Action", out var actionLeft))
                    MenuDPadLeftEnabled = actionLeft > 0;

                if (LocalSettingsHelper.TryGetValue<int>("Hotkey_MenuDpadRight_Action", out var actionRight))
                    MenuDPadRightEnabled = actionRight > 0;

                // View + ABXY combos (View button = Back/two-squares button)
                // Widget saves as "Hotkey_MenuA_Action", etc. (confusingly named, but View+ABXY in code)
                if (LocalSettingsHelper.TryGetValue<int>("Hotkey_MenuA_Action", out var actionA))
                    ViewAEnabled = actionA > 0;

                if (LocalSettingsHelper.TryGetValue<int>("Hotkey_MenuB_Action", out var actionB))
                    ViewBEnabled = actionB > 0;

                if (LocalSettingsHelper.TryGetValue<int>("Hotkey_MenuX_Action", out var actionX))
                    ViewXEnabled = actionX > 0;

                if (LocalSettingsHelper.TryGetValue<int>("Hotkey_MenuY_Action", out var actionY))
                    ViewYEnabled = actionY > 0;

                Logger.Info($"ControllerHotkeyMonitor: Settings loaded - Menu+DPad: Up={MenuDPadUpEnabled}, Down={MenuDPadDownEnabled}, Left={MenuDPadLeftEnabled}, Right={MenuDPadRightEnabled}");
                Logger.Info($"ControllerHotkeyMonitor: Settings loaded - View+ABXY: A={ViewAEnabled}, B={ViewBEnabled}, X={ViewXEnabled}, Y={ViewYEnabled}");
            }
            catch (Exception ex)
            {
                Logger.Error($"ControllerHotkeyMonitor: Error loading settings: {ex.Message}");
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
