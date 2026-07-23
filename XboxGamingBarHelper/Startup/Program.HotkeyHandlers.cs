using NLog;
using Shared.Constants;
using Shared.Data;
using Shared.IPC;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.System;
using Windows.UI.Input.Preview.Injection;
using XboxGamingBarHelper.AMD;
using XboxGamingBarHelper.Core;
using XboxGamingBarHelper.ControllerEmulation;
using XboxGamingBarHelper.Devices.Libraries.GPD;
using XboxGamingBarHelper.Devices.Libraries.Legion;
using XboxGamingBarHelper.LosslessScaling;
using XboxGamingBarHelper.OnScreenDisplay;
using XboxGamingBarHelper.Performance;
using XboxGamingBarHelper.Power;
using XboxGamingBarHelper.Profile;
using XboxGamingBarHelper.RTSS;
using XboxGamingBarHelper.Settings;
using XboxGamingBarHelper.Systems;
using XboxGamingBarHelper.AutoTDP;
using XboxGamingBarHelper.DefaultGameProfiles;
using XboxGamingBarHelper.Labs;
using Shared.Enums;

namespace XboxGamingBarHelper
{
    internal partial class Program
    {

        /// <summary>
        /// Initializes the hotkey manager and registers global hotkeys
        /// </summary>
        private static void InitializeHotkeyManager()
        {
            try
            {
                hotkeyManager = new HotkeyManager();

                // Register Ctrl+Shift+D for Desktop Controls toggle
                int hotkeyId = hotkeyManager.RegisterHotkey(
                    HotkeyManager.MOD_CONTROL | HotkeyManager.MOD_SHIFT,
                    HotkeyManager.VK_D,
                    ToggleDesktopControls);

                if (hotkeyId > 0)
                {
                    Logger.Info("Registered global hotkey Ctrl+Shift+D for Desktop Controls toggle");
                }
                else
                {
                    Logger.Warn("Failed to register Ctrl+Shift+D hotkey - may be in use by another application");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to initialize hotkey manager: {ex.Message}");
            }

            // Initialize controller hotkey monitor for XInput-based button combos
            InitializeControllerHotkeyMonitor();
        }

        /// <summary>
        /// Initializes the controller hotkey monitor for XInput-based button combos.
        /// This allows detection of Menu+DPad and View+ABXY combos in games.
        /// </summary>
        private static void InitializeControllerHotkeyMonitor()
        {
            try
            {
                controllerHotkeyMonitor = new ControllerHotkeyMonitor();
                controllerHotkeyMonitor.LoadSettings();

                // Bridge Legion back-paddle presses into the combo monitor (XInput can't see them).
                Labs.LegionButtonMonitor.ButtonEdge -= OnLegionButtonEdgeForHotkeys;
                Labs.LegionButtonMonitor.ButtonEdge += OnLegionButtonEdgeForHotkeys;
                Logger.Info("ControllerHotkey: Legion paddle bridge wired to ButtonEdge (aux -> tile combos)");

                // Set up callbacks for each combo
                // These will execute the same actions as the Xbox Game Bar hotkey watchers
                // Names must match widget's LocalSettings keys (without "Hotkey_" prefix)
                controllerHotkeyMonitor.OnMenuDPadUp = () => ExecuteControllerHotkeyAction("MenuDpadUp");
                controllerHotkeyMonitor.OnMenuDPadDown = () => ExecuteControllerHotkeyAction("MenuDpadDown");
                controllerHotkeyMonitor.OnMenuDPadLeft = () => ExecuteControllerHotkeyAction("MenuDpadLeft");
                controllerHotkeyMonitor.OnMenuDPadRight = () => ExecuteControllerHotkeyAction("MenuDpadRight");
                controllerHotkeyMonitor.OnViewA = () => ExecuteControllerHotkeyAction("MenuA");
                controllerHotkeyMonitor.OnViewB = () => ExecuteControllerHotkeyAction("MenuB");
                controllerHotkeyMonitor.OnViewX = () => ExecuteControllerHotkeyAction("MenuX");
                controllerHotkeyMonitor.OnViewY = () => ExecuteControllerHotkeyAction("MenuY");

                // Skip the 60Hz XInput polling thread until at least one combo is bound.
                // ApplyControllerHotkeyConfig will call SyncControllerHotkeyMonitorRunState
                // to start it when the widget enables any binding.
                if (controllerHotkeyMonitor.AnyComboEnabled)
                {
                    controllerHotkeyMonitor.Start();
                    Logger.Info("Controller hotkey monitor initialized and started (combos enabled)");
                }
                else
                {
                    Logger.Info("Controller hotkey monitor initialized but not started (no combos enabled)");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to initialize controller hotkey monitor: {ex.Message}");
            }
        }

        /// <summary>
        /// Start or stop the XInput polling thread to match current binding state.
        /// Idempotent — safe to call after every config apply or settings reload.
        /// </summary>
        private static void SyncControllerHotkeyMonitorRunState()
        {
            if (controllerHotkeyMonitor == null) return;
            bool shouldRun = controllerHotkeyMonitor.AnyComboEnabled || controllerHotkeyMonitor.HasTileHotkeys;
            bool isRunning = controllerHotkeyMonitor.IsRunning;
            if (shouldRun && !isRunning)
            {
                controllerHotkeyMonitor.Start();
                Logger.Info("Controller hotkey monitor started (a combo became enabled)");
            }
            else if (!shouldRun && isRunning)
            {
                controllerHotkeyMonitor.Stop();
                Logger.Info("Controller hotkey monitor stopped (no combos enabled)");
            }
        }

        // ---- Quick-tile controller combos ----

        private static uint _legionExtraBits = 0;

        /// <summary>
        /// Widget-&gt;helper: JSON array of tiles with a controller-combo binding
        /// [{ "id":..., "name":..., "mask":&lt;uint&gt; }]. Registers each with the monitor.
        /// </summary>
        private static void ApplyTileHotkeys(string json)
        {
            try
            {
                if (controllerHotkeyMonitor == null)
                {
                    Logger.Warn("ApplyTileHotkeys: monitor not initialized");
                    return;
                }

                controllerHotkeyMonitor.ClearTileHotkeys();

                int count = 0;
                if (!string.IsNullOrWhiteSpace(json))
                {
                    using (var doc = System.Text.Json.JsonDocument.Parse(json))
                    {
                        if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            foreach (var el in doc.RootElement.EnumerateArray())
                            {
                                string id = el.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                                string name = el.TryGetProperty("name", out var nEl) ? nEl.GetString() : "";
                                uint mask = 0;
                                // mask can exceed int range (Legion paddle high bits) - read as long.
                                if (el.TryGetProperty("mask", out var mEl) && mEl.TryGetInt64(out var m)) mask = (uint)m;
                                if (string.IsNullOrEmpty(id) || mask == 0) continue;

                                string capturedId = id;
                                string capturedName = name;
                                controllerHotkeyMonitor.RegisterTileHotkey(mask,
                                    () => FireTileHotkeyToWidget(capturedId, capturedName), name);
                                Logger.Info($"ControllerHotkey: registered tile combo '{name}' id={id} mask=0x{mask:X}");
                                count++;
                            }
                        }
                    }
                }

                SyncControllerHotkeyMonitorRunState();
                Logger.Info($"ApplyTileHotkeys: registered {count} tile combo(s)");
            }
            catch (Exception ex)
            {
                Logger.Error($"ApplyTileHotkeys error: {ex.Message}");
            }
        }

        /// <summary>Helper-&gt;widget: tell the widget a tile combo fired so it runs the tile action.</summary>
        private static void FireTileHotkeyToWidget(string tileId, string name)
        {
            try
            {
                if (!IsPipeConnected)
                {
                    Logger.Info($"Tile combo '{name}' fired but widget not connected - id={tileId}");
                    return;
                }
                var msg = new Shared.IPC.PipeMessage
                {
                    Command = Shared.Enums.Command.Set,
                    Function = Shared.Enums.Function.TileHotkeyFired,
                    Content = tileId
                };
                SendPipeMessage(msg);
                Logger.Info($"Tile combo '{name}' -> widget (id={tileId})");
            }
            catch (Exception ex)
            {
                Logger.Error($"FireTileHotkeyToWidget error: {ex.Message}");
            }
        }

        /// <summary>
        /// Bridges Legion back-paddle presses (Y1-Y3 / M1-M3, invisible to XInput) into the
        /// combo monitor as high bits. Subscribed to LegionButtonMonitor.ButtonEdge.
        /// </summary>
        private static void OnLegionButtonEdgeForHotkeys(object sender, Labs.LegionButtonEdgeEventArgs e)
        {
            // Helper-side button remaps (Type=System actions; scroll directions that
            // repeat while held). Registered by LegionManager when a remap can't be
            // expressed in firmware; the firmware mapping is cleared so this is the
            // button's only behavior.
            try
            {
                if (legionManager != null)
                {
                    if (e.Pressed && legionManager.TryGetSystemActionRemap(e.Button, out int sysAction))
                    {
                        Logger.Info($"Legion button {e.Button} -> system action {sysAction}");
                        Task.Run(() => ExecuteSystemAction(sysAction, null, $"legion-button-{e.Button}"));
                    }
                    if (legionManager.TryGetScrollRepeatRemap(e.Button, out int scrollCode))
                    {
                        if (e.Pressed) StartScrollRepeat(e.Button, scrollCode);
                        else StopScrollRepeat(e.Button);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Helper-side button remap dispatch failed: {ex.Message}");
            }

            uint bit = LegionButtonToComboBit(e.Button);
            if (bit == 0) return;
            if (e.Pressed) _legionExtraBits |= bit;
            else _legionExtraBits &= ~bit;
            controllerHotkeyMonitor?.SetExtraButtons(_legionExtraBits);
            // Debug-level trace of paddle edges reaching the combo bridge (kept for future
            // diagnosis; not logged at INFO to avoid noise on every paddle press).
            Logger.Debug($"ControllerHotkey: paddle {e.Button} {(e.Pressed ? "down" : "up")} -> bit=0x{bit:X}, extraBits=0x{_legionExtraBits:X}");
        }

        // Hold-to-repeat scroll (field request: firmware scroll remaps are
        // one-press-one-notch; holding the button now scrolls continuously).
        // One tick fires immediately on press - a quick tap still scrolls exactly
        // one notch - then repeats after a short initial delay, standard key-repeat
        // feel. Codes are the 1-based firmware mouse enum: 4=Up, 5=Down, 6=Left, 7=Right.
        private static readonly Dictionary<Labs.LegionInputButton, CancellationTokenSource> _scrollRepeats =
            new Dictionary<Labs.LegionInputButton, CancellationTokenSource>();
        private const int ScrollRepeatInitialDelayMs = 280;
        private const int ScrollRepeatIntervalMs = 55;

        private static void StartScrollRepeat(Labs.LegionInputButton button, int scrollCode)
        {
            StopScrollRepeat(button);
            var cts = new CancellationTokenSource();
            lock (_scrollRepeats) { _scrollRepeats[button] = cts; }
            var token = cts.Token;
            Task.Run(async () =>
            {
                try
                {
                    InjectScrollTick(scrollCode);
                    await Task.Delay(ScrollRepeatInitialDelayMs, token);
                    while (!token.IsCancellationRequested)
                    {
                        InjectScrollTick(scrollCode);
                        await Task.Delay(ScrollRepeatIntervalMs, token);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Logger.Warn($"Scroll repeat loop failed: {ex.Message}"); }
            });
        }

        private static void StopScrollRepeat(Labs.LegionInputButton button)
        {
            CancellationTokenSource cts = null;
            lock (_scrollRepeats)
            {
                if (_scrollRepeats.TryGetValue(button, out cts)) _scrollRepeats.Remove(button);
            }
            try { cts?.Cancel(); cts?.Dispose(); } catch { }
        }

        private static void InjectScrollTick(int scrollCode)
        {
            var injector = inputInjector;
            if (injector == null) return;

            bool horizontal = scrollCode == 6 || scrollCode == 7;
            int delta = (scrollCode == 4 || scrollCode == 7) ? 120 : -120;   // up/right positive
            var info = new InjectedInputMouseInfo
            {
                MouseOptions = horizontal ? InjectedInputMouseOptions.HWheel : InjectedInputMouseOptions.Wheel,
                MouseData = unchecked((uint)delta),
            };
            try { injector.InjectMouseInput(new[] { info }); }
            catch (Exception ex) { Logger.Debug($"Scroll inject failed: {ex.Message}"); }
        }

        private static uint LegionButtonToComboBit(Labs.LegionInputButton b)
        {
            // Physical->LegionInputButton verified on hardware by pressing Y1,Y2,Y3,M1,M2,M3 in
            // order (diagnostic log 2026-07-13): Y1=ExtraL1, Y2=ExtraL2, Y3=ExtraR1, M1=ExtraRM1,
            // M2=ExtraR3, M3=ExtraR2. (The ViiperInputForwarder comment had Y1/Y2 and R2/R3 flipped.)
            switch (b)
            {
                case Labs.LegionInputButton.ExtraL1:  return Shared.Input.ControllerComboButtons.LegionY1;
                case Labs.LegionInputButton.ExtraL2:  return Shared.Input.ControllerComboButtons.LegionY2;
                case Labs.LegionInputButton.ExtraR1:  return Shared.Input.ControllerComboButtons.LegionY3;
                case Labs.LegionInputButton.ExtraRM1: return Shared.Input.ControllerComboButtons.LegionM1;
                case Labs.LegionInputButton.ExtraR3:  return Shared.Input.ControllerComboButtons.LegionM2;
                case Labs.LegionInputButton.ExtraR2:  return Shared.Input.ControllerComboButtons.LegionM3;
                default: return 0;
            }
        }

        /// <summary>
        /// Executes the configured action for a controller hotkey combo.
        /// Uses cached config from Named Pipe, falls back to LocalSettings.
        /// </summary>
        private static void ExecuteControllerHotkeyAction(string hotkeyName)
        {
            try
            {
                int action = 0;
                string keyParam = "";
                bool foundInConfig = false;

                // Try cached config first (received from widget via pipe)
                if (_controllerHotkeyConfig != null)
                {
                    if (_controllerHotkeyConfig.TryGetValue($"{hotkeyName}_Action", out var actionElement))
                    {
                        action = actionElement.GetInt32();
                        foundInConfig = true;
                    }
                    if (_controllerHotkeyConfig.TryGetValue($"{hotkeyName}_Key", out var keyElement))
                        keyParam = keyElement.GetString() ?? "";
                }

                // Fallback to LocalSettings if not in cached config
                // Widget saves as "Hotkey_{hotkeyName}_Action" and "Hotkey_{hotkeyName}_Key"
                if (!foundInConfig)
                {
                    if (LocalSettingsHelper.TryGetValue<int>($"Hotkey_{hotkeyName}_Action", out var localAction))
                        action = localAction;
                    if (LocalSettingsHelper.TryGetValue<string>($"Hotkey_{hotkeyName}_Key", out var localKey))
                        keyParam = localKey ?? "";
                    Logger.Debug($"ExecuteControllerHotkeyAction: Using LocalSettings fallback for {hotkeyName}");
                }

                Logger.Info($"ExecuteControllerHotkeyAction: {hotkeyName} action={action} key={keyParam}");

                ExecuteSystemAction(action, keyParam, hotkeyName);
            }
            catch (Exception ex)
            {
                Logger.Error($"ExecuteControllerHotkeyAction: Error executing {hotkeyName}: {ex.Message}");
            }
        }

        /// <summary>
        /// Executes a HotkeyAction id. Shared by the System-tab controller hotkeys and
        /// the Legion button remaps with Type=System (the remap JSON carries the same
        /// action ids in its MouseButton field).
        /// </summary>
        internal static void ExecuteSystemAction(int action, string keyParam = null, string sourceName = "system-action")
        {
            // Confirmation buzz on every executed system action (System-tab hotkeys and
            // system-action button remaps) so the user feels the trigger land.
            try { goTweaksHapticManager?.PlayConfirmationPulse(); } catch { }
            try
            {
                // HotkeyAction enum from widget:
                // 0=Disabled, 1=KeyboardKey, 2=KeyboardShortcut, 3=ToggleOSD, 4=Screenshot,
                // 5=AltTab, 6=AltF4, 7=OpenKeyboard, 8=CtrlAltDel, 9=TaskManager, 10=FocusGoTweaks
                switch (action)
                {
                    case 0: // Disabled
                        break;
                    case 1: // KeyboardKey - single key press
                        if (!string.IsNullOrEmpty(keyParam))
                        {
                            ExecuteKeyboardShortcut(keyParam);
                        }
                        break;
                    case 2: // KeyboardShortcut - combo like Ctrl+Alt+X
                        if (!string.IsNullOrEmpty(keyParam))
                        {
                            ExecuteKeyboardShortcut(keyParam);
                        }
                        break;
                    case 3: // Toggle OSD
                        ToggleOSD();
                        break;
                    case 4: // Screenshot (Win+Shift+S)
                        ExecuteKeyboardShortcut("Win+Shift+S");
                        break;
                    case 5: // Alt+Tab
                        ExecuteKeyboardShortcut("Alt+Tab");
                        break;
                    case 6: // Alt+F4
                        ExecuteKeyboardShortcut("Alt+F4");
                        break;
                    case 7: // Open On-Screen Keyboard
                        OpenOnScreenKeyboard();
                        break;
                    case 8: // Ctrl+Alt+Del
                        ExecuteKeyboardShortcut("Ctrl+Alt+Delete");
                        break;
                    case 9: // Task Manager (Ctrl+Shift+Esc)
                        ExecuteKeyboardShortcut("Ctrl+Shift+Escape");
                        break;
                    case 10: // Focus GoTweaks widget
                        FocusGoTweaksWidget();
                        break;
                    default:
                        Logger.Warn($"ExecuteSystemAction: Unknown action {action} for {sourceName}");
                        break;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"ExecuteSystemAction: Error executing {sourceName}: {ex.Message}");
            }
        }

        /// <summary>
        /// Executes a keyboard shortcut string (e.g., "Ctrl+Alt+Delete")
        /// Uses InputInjector which works properly from elevated helper context
        /// </summary>
        private static void ExecuteKeyboardShortcut(string shortcut)
        {
            try
            {
                Logger.Info($"ExecuteKeyboardShortcut: {shortcut}");
                // Use InputInjector (same as widget's SendKeyboardShortcutViaHelper)
                SendKeyboardShortcutViaInputInjector(shortcut);
            }
            catch (Exception ex)
            {
                Logger.Error($"ExecuteKeyboardShortcut: Failed to send {shortcut}: {ex.Message}");
            }
        }

        /// <summary>
        /// Opens the Windows on-screen keyboard using TouchKeyboardHelper
        /// </summary>
        private static void OpenOnScreenKeyboard()
        {
            try
            {
                Logger.Info("OpenOnScreenKeyboard: Toggling touch keyboard");
                TouchKeyboardHelper.Toggle();
                Logger.Info("OpenOnScreenKeyboard: Touch keyboard toggled");
            }
            catch (Exception ex)
            {
                Logger.Error($"OpenOnScreenKeyboard: {ex.Message}");
            }
        }

        /// <summary>
        /// Toggles OSD visibility by cycling through OSD levels (0=Off, 1, 2, 3)
        /// </summary>
        private static void ToggleOSD()
        {
            try
            {
                if (onScreenDisplay == null)
                {
                    Logger.Warn("ToggleOSD: OSD property not initialized");
                    return;
                }

                // Cycle through levels: 0 -> 1 -> 2 -> 3 -> 0
                int currentLevel = onScreenDisplay.Value;
                int newLevel = (currentLevel + 1) % 4;  // 0, 1, 2, 3, then back to 0

                onScreenDisplay.SetValue(newLevel);
                Logger.Info($"ToggleOSD: OSD level changed from {currentLevel} to {newLevel}");
            }
            catch (Exception ex)
            {
                Logger.Error($"ToggleOSD: {ex.Message}");
            }
        }

        /// <summary>
        /// Apply controller hotkey configuration received from the widget.
        /// Enables/disables XInput-based button combo detection.
        /// </summary>
        private static void ApplyControllerHotkeyConfig(string configJson)
        {
            try
            {
                Logger.Info($"Applying controller hotkey config from widget");

                var config = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(configJson);
                if (config == null || controllerHotkeyMonitor == null)
                {
                    Logger.Warn("ApplyControllerHotkeyConfig: Config is null or monitor not initialized");
                    return;
                }

                // Apply View + ABXY settings (View button = Back/two-squares button in XInput)
                // Widget saves as "Hotkey_MenuA_Action", etc.
                if (config.TryGetValue("MenuA_Action", out var menuAAction))
                    controllerHotkeyMonitor.ViewAEnabled = menuAAction.GetInt32() > 0;
                if (config.TryGetValue("MenuB_Action", out var menuBAction))
                    controllerHotkeyMonitor.ViewBEnabled = menuBAction.GetInt32() > 0;
                if (config.TryGetValue("MenuX_Action", out var menuXAction))
                    controllerHotkeyMonitor.ViewXEnabled = menuXAction.GetInt32() > 0;
                if (config.TryGetValue("MenuY_Action", out var menuYAction))
                    controllerHotkeyMonitor.ViewYEnabled = menuYAction.GetInt32() > 0;

                // Apply Menu + DPad settings (Menu button = Start/three-lines button in XInput)
                // Widget saves as "Hotkey_MenuDpadUp_Action", etc.
                if (config.TryGetValue("MenuDpadUp_Action", out var dpadUpAction))
                    controllerHotkeyMonitor.MenuDPadUpEnabled = dpadUpAction.GetInt32() > 0;
                if (config.TryGetValue("MenuDpadDown_Action", out var dpadDownAction))
                    controllerHotkeyMonitor.MenuDPadDownEnabled = dpadDownAction.GetInt32() > 0;
                if (config.TryGetValue("MenuDpadLeft_Action", out var dpadLeftAction))
                    controllerHotkeyMonitor.MenuDPadLeftEnabled = dpadLeftAction.GetInt32() > 0;
                if (config.TryGetValue("MenuDpadRight_Action", out var dpadRightAction))
                    controllerHotkeyMonitor.MenuDPadRightEnabled = dpadRightAction.GetInt32() > 0;

                // Store config for action execution
                _controllerHotkeyConfig = config;

                Logger.Info($"Controller hotkey config applied - Menu+DPad: Up={controllerHotkeyMonitor.MenuDPadUpEnabled}, Down={controllerHotkeyMonitor.MenuDPadDownEnabled}, Left={controllerHotkeyMonitor.MenuDPadLeftEnabled}, Right={controllerHotkeyMonitor.MenuDPadRightEnabled}");
                Logger.Info($"Controller hotkey config applied - View+ABXY: A={controllerHotkeyMonitor.ViewAEnabled}, B={controllerHotkeyMonitor.ViewBEnabled}, X={controllerHotkeyMonitor.ViewXEnabled}, Y={controllerHotkeyMonitor.ViewYEnabled}");

                SyncControllerHotkeyMonitorRunState();
            }
            catch (Exception ex)
            {
                Logger.Error($"ApplyControllerHotkeyConfig: {ex.Message}");
            }
        }

        // Cached controller hotkey config for action execution
        private static Dictionary<string, System.Text.Json.JsonElement> _controllerHotkeyConfig;

        /// <summary>
        /// Toggles Desktop Controls preset via global hotkey (Ctrl+Shift+D)
        /// Applies Joystick-as-Mouse and button mappings for desktop navigation
        /// </summary>
        private static void ToggleDesktopControls()
        {
            try
            {
                if (legionManager == null || !legionManager.LegionGoDetected.Value)
                {
                    Logger.Warn("ToggleDesktopControls: Legion Go not detected, skipping");
                    return;
                }

                // Single state owner: when the widget is connected, route the toggle through
                // it (same path as the Quick tile) so ONLY the widget's Desktop-profile
                // overlay logic runs. Previously this method applied its own hardcoded
                // mappings/joystick-mode AND the property push flipped the widget toggle,
                // firing the widget's overlay apply as well — two appliers per press. When
                // the widget swallowed one of those Toggled events, its isDesktopModeActive
                // inverted against the toggle and every later press applied the OPPOSITE
                // state: Desktop Mode and Joystick-as-Mouse "synced but alternating", with
                // the underlying profile's default 50% sensitivity applied while the UI
                // showed the user's Desktop-profile value (field report, 0.3.2637).
                if (IsPipeConnected)
                {
                    Logger.Info("ToggleDesktopControls: Hotkey pressed, routing to widget (tile path)");
                    FireTileHotkeyToWidget("LegionDesktopControls", "Desktop Controls (Ctrl+Shift+D)");
                    return;
                }

                // Widget not connected (Game Bar closed): minimal helper-side fallback so the
                // hotkey still works on the desktop. Applies the built-in preset only — the
                // user's customized Desktop profile lives widget-side and reconciles when the
                // widget reconnects (the LegionDesktopControls property sync flips its toggle
                // and runs the real overlay apply). Deliberately does NOT touch
                // LegionJoystickMouseSens so the user's sensitivity survives the fallback.
                bool newState = !legionManager.LegionDesktopControls.Value;
                Logger.Info($"ToggleDesktopControls: Hotkey pressed (widget offline), toggling from {!newState} to {newState}");

                if (newState)
                {
                    // Enable Desktop Controls:
                    // 1. Set Right Stick as Mouse (mode 2)
                    legionManager.LegionJoystickAsMouseMode.ForceSetValue(2);

                    // 2. Apply desktop button mappings JSON
                    // Format: {"ButtonName":{"Type":X,"GamepadAction":Y,"KeyboardKeys":[...],"MouseButton":Z},...}
                    // Desktop Controls preset: DPAD/LS→Arrows, LSClick→Win, A→Enter, B→Esc, LB→LClick, LT→RClick
                    string desktopMappingsJson = @"{
                        ""DPadUp"":{""Type"":1,""GamepadAction"":0,""KeyboardKeys"":[82],""MouseButton"":0},
                        ""DPadDown"":{""Type"":1,""GamepadAction"":0,""KeyboardKeys"":[81],""MouseButton"":0},
                        ""DPadLeft"":{""Type"":1,""GamepadAction"":0,""KeyboardKeys"":[80],""MouseButton"":0},
                        ""DPadRight"":{""Type"":1,""GamepadAction"":0,""KeyboardKeys"":[79],""MouseButton"":0},
                        ""LSUp"":{""Type"":1,""GamepadAction"":0,""KeyboardKeys"":[82],""MouseButton"":0},
                        ""LSDown"":{""Type"":1,""GamepadAction"":0,""KeyboardKeys"":[81],""MouseButton"":0},
                        ""LSClick"":{""Type"":1,""GamepadAction"":0,""KeyboardKeys"":[227],""MouseButton"":0},
                        ""A"":{""Type"":1,""GamepadAction"":0,""KeyboardKeys"":[40],""MouseButton"":0},
                        ""B"":{""Type"":1,""GamepadAction"":0,""KeyboardKeys"":[41],""MouseButton"":0},
                        ""LB"":{""Type"":2,""GamepadAction"":0,""KeyboardKeys"":[],""MouseButton"":0},
                        ""LT"":{""Type"":2,""GamepadAction"":0,""KeyboardKeys"":[],""MouseButton"":1}
                    }";
                    legionManager.LegionGamepadMapping.ForceSetValue(desktopMappingsJson);
                }
                else
                {
                    // Disable Desktop Controls:
                    // 1. Disable Joystick as Mouse (mode 0)
                    legionManager.LegionJoystickAsMouseMode.ForceSetValue(0);

                    // 2. Clear button mappings (empty JSON resets to defaults)
                    string resetMappingsJson = @"{
                        ""DPadUp"":{""Type"":0,""GamepadAction"":0,""KeyboardKeys"":[],""MouseButton"":0},
                        ""DPadDown"":{""Type"":0,""GamepadAction"":0,""KeyboardKeys"":[],""MouseButton"":0},
                        ""DPadLeft"":{""Type"":0,""GamepadAction"":0,""KeyboardKeys"":[],""MouseButton"":0},
                        ""DPadRight"":{""Type"":0,""GamepadAction"":0,""KeyboardKeys"":[],""MouseButton"":0},
                        ""LSUp"":{""Type"":0,""GamepadAction"":0,""KeyboardKeys"":[],""MouseButton"":0},
                        ""LSDown"":{""Type"":0,""GamepadAction"":0,""KeyboardKeys"":[],""MouseButton"":0},
                        ""LSClick"":{""Type"":0,""GamepadAction"":0,""KeyboardKeys"":[],""MouseButton"":0},
                        ""A"":{""Type"":0,""GamepadAction"":0,""KeyboardKeys"":[],""MouseButton"":0},
                        ""B"":{""Type"":0,""GamepadAction"":0,""KeyboardKeys"":[],""MouseButton"":0},
                        ""LB"":{""Type"":0,""GamepadAction"":0,""KeyboardKeys"":[],""MouseButton"":0},
                        ""LT"":{""Type"":0,""GamepadAction"":0,""KeyboardKeys"":[],""MouseButton"":0}
                    }";
                    legionManager.LegionGamepadMapping.ForceSetValue(resetMappingsJson);
                }

                // Update the Desktop Controls property (syncs to widget UI)
                legionManager.LegionDesktopControls.ForceSetValue(newState);

                Logger.Info($"ToggleDesktopControls: Desktop Controls now {(newState ? "ENABLED" : "DISABLED")}");
            }
            catch (Exception ex)
            {
                Logger.Error($"ToggleDesktopControls: Error toggling desktop controls: {ex.Message}");
            }
        }


        /// <summary>
        /// Parse and send a keyboard shortcut using InputInjector (works in widget context unlike SendInput)
        /// </summary>
        internal static void SendKeyboardShortcut(string shortcut) => SendKeyboardShortcutViaInputInjector(shortcut);

        /// <summary>
        /// Parse and send a keyboard shortcut using InputInjector (works in widget context unlike SendInput)
        /// </summary>
        private static void SendKeyboardShortcutViaInputInjector(string shortcut)
        {
            if (inputInjector == null)
            {
                Logger.Error("InputInjector not available - falling back to User32.SendKeyboardShortcut");
                Windows.User32.SendKeyboardShortcut(shortcut);
                return;
            }

            if (string.IsNullOrWhiteSpace(shortcut))
            {
                Logger.Warn("Empty shortcut string provided");
                return;
            }

            try
            {
                var parts = shortcut.Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries);
                var keyInfos = new List<InjectedInputKeyboardInfo>();
                var modifierKeys = new List<ushort>();
                var mainKeys = new List<ushort>(); // Support multiple non-modifier keys

                foreach (var part in parts)
                {
                    var trimmed = part.Trim();
                    var upper = trimmed.ToUpperInvariant();
                    ushort vk = 0;

                    if (upper == "CTRL" || upper == "CONTROL" || upper == "LCTRL" || upper == "LCONTROL")
                        vk = (ushort)VirtualKey.LeftControl;
                    else if (upper == "RCTRL" || upper == "RCONTROL")
                        vk = (ushort)VirtualKey.RightControl;
                    else if (upper == "ALT" || upper == "LALT")
                        vk = (ushort)VirtualKey.LeftMenu;
                    else if (upper == "RALT")
                        vk = (ushort)VirtualKey.RightMenu;
                    else if (upper == "SHIFT" || upper == "LSHIFT")
                        vk = (ushort)VirtualKey.LeftShift;
                    else if (upper == "RSHIFT")
                        vk = (ushort)VirtualKey.RightShift;
                    else if (upper == "WIN" || upper == "WINDOWS" || upper == "LWIN" || upper == "LMETA" || upper == "META")
                        vk = (ushort)VirtualKey.LeftWindows;
                    else if (upper == "RWIN" || upper == "RMETA")
                        vk = (ushort)VirtualKey.RightWindows;
                    else if (upper == "TAB")
                        vk = (ushort)VirtualKey.Tab;
                    else if (upper == "ENTER" || upper == "RETURN")
                        vk = (ushort)VirtualKey.Enter;
                    else if (upper == "ESCAPE" || upper == "ESC")
                        vk = (ushort)VirtualKey.Escape;
                    else if (upper == "SPACE")
                        vk = (ushort)VirtualKey.Space;
                    else if (upper == "BACKSPACE" || upper == "BACK")
                        vk = (ushort)VirtualKey.Back;
                    else if (upper == "DELETE" || upper == "DEL")
                        vk = (ushort)VirtualKey.Delete;
                    else if (upper == "HOME")
                        vk = (ushort)VirtualKey.Home;
                    else if (upper == "END")
                        vk = (ushort)VirtualKey.End;
                    else if (upper == "PGUP" || upper == "PAGEUP")
                        vk = (ushort)VirtualKey.PageUp;
                    else if (upper == "PGDN" || upper == "PAGEDOWN")
                        vk = (ushort)VirtualKey.PageDown;
                    else if (upper == "INSERT" || upper == "INS")
                        vk = (ushort)VirtualKey.Insert;
                    else if (upper == "UP")
                        vk = (ushort)VirtualKey.Up;
                    else if (upper == "DOWN")
                        vk = (ushort)VirtualKey.Down;
                    else if (upper == "LEFT")
                        vk = (ushort)VirtualKey.Left;
                    else if (upper == "RIGHT")
                        vk = (ushort)VirtualKey.Right;
                    else if (upper == "PAUSE")
                        vk = (ushort)VirtualKey.Pause;
                    else if (upper == "PRINTSCREEN" || upper == "PRTSC")
                        vk = (ushort)VirtualKey.Snapshot;
                    else if (upper == "VOLUME_UP" || upper == "VOLUMEUP")
                        vk = 0xAF; // VK_VOLUME_UP
                    else if (upper == "VOLUME_DOWN" || upper == "VOLUMEDOWN")
                        vk = 0xAE; // VK_VOLUME_DOWN
                    else if (upper == "VOLUME_MUTE" || upper == "VOLUMEMUTE" || upper == "MUTE")
                        vk = 0xAD; // VK_VOLUME_MUTE
                    else if (trimmed == "[" || upper == "LEFTBRACKET")
                        vk = 0xDB; // VK_OEM_4 (left bracket)
                    else if (trimmed == "]" || upper == "RIGHTBRACKET")
                        vk = 0xDD; // VK_OEM_6 (right bracket)
                    else if (upper.Length == 1)
                    {
                        char c = upper[0];
                        if (c >= 'A' && c <= 'Z')
                            vk = (ushort)(VirtualKey.A + (c - 'A'));
                        else if (c >= '0' && c <= '9')
                            vk = (ushort)(VirtualKey.Number0 + (c - '0'));
                    }
                    else if (upper.StartsWith("F") && upper.Length <= 3)
                    {
                        if (int.TryParse(upper.Substring(1), out int fNum) && fNum >= 1 && fNum <= 24)
                            vk = (ushort)(VirtualKey.F1 + (fNum - 1));
                    }

                    if (vk == 0)
                    {
                        Logger.Warn($"Unknown key in shortcut: {trimmed}");
                        continue;
                    }

                    // Check if modifier
                    if (upper == "CTRL" || upper == "CONTROL" || upper == "LCTRL" || upper == "LCONTROL" ||
                        upper == "RCTRL" || upper == "RCONTROL" ||
                        upper == "ALT" || upper == "LALT" || upper == "RALT" ||
                        upper == "SHIFT" || upper == "LSHIFT" || upper == "RSHIFT" ||
                        upper == "WIN" || upper == "WINDOWS" || upper == "LWIN" || upper == "RWIN" ||
                        upper == "LMETA" || upper == "RMETA" || upper == "META")
                    {
                        modifierKeys.Add(vk);
                    }
                    else
                    {
                        mainKeys.Add(vk); // Add to list instead of overwriting
                    }
                }

                // Build key sequence: press modifiers, press all main keys, release all main keys in reverse, release modifiers
                // Press modifiers
                foreach (var mod in modifierKeys)
                {
                    keyInfos.Add(new InjectedInputKeyboardInfo { VirtualKey = mod, KeyOptions = InjectedInputKeyOptions.None });
                }

                // Press all main keys
                foreach (var key in mainKeys)
                {
                    keyInfos.Add(new InjectedInputKeyboardInfo { VirtualKey = key, KeyOptions = InjectedInputKeyOptions.None });
                }

                // Release all main keys in reverse order
                for (int i = mainKeys.Count - 1; i >= 0; i--)
                {
                    keyInfos.Add(new InjectedInputKeyboardInfo { VirtualKey = mainKeys[i], KeyOptions = InjectedInputKeyOptions.KeyUp });
                }

                // Release modifiers in reverse order
                for (int i = modifierKeys.Count - 1; i >= 0; i--)
                {
                    keyInfos.Add(new InjectedInputKeyboardInfo { VirtualKey = modifierKeys[i], KeyOptions = InjectedInputKeyOptions.KeyUp });
                }

                inputInjector.InjectKeyboardInput(keyInfos);
                Logger.Info($"Sent keyboard shortcut via InputInjector: {shortcut} (modifiers: {modifierKeys.Count}, keys: {mainKeys.Count})");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error sending keyboard shortcut '{shortcut}': {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Helper class to toggle the Windows touch keyboard via COM interop
    /// </summary>
    internal static class TouchKeyboardHelper
    {
        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

        [System.Runtime.InteropServices.ComImport]
        [System.Runtime.InteropServices.Guid("4ce576fa-83dc-4F88-951c-9d0782b4e376")]
        private class UIHostNoLaunch { }

        [System.Runtime.InteropServices.ComImport]
        [System.Runtime.InteropServices.Guid("37c994e7-432b-4834-a2f7-dce1f13b834b")]
        [System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
        private interface ITipInvocation
        {
            void Toggle(IntPtr hwnd);
        }

        public static void Toggle()
        {
            try
            {
                var uiHostNoLaunch = new UIHostNoLaunch();
                var tipInvocation = (ITipInvocation)uiHostNoLaunch;
                tipInvocation.Toggle(IntPtr.Zero);
                System.Runtime.InteropServices.Marshal.ReleaseComObject(uiHostNoLaunch);
                Logger.Info("Touch keyboard toggle executed via COM");
            }
            catch (System.Runtime.InteropServices.COMException ex)
            {
                Logger.Error($"COM error toggling touch keyboard: {ex.Message}");
                // Fallback: try launching TabTip.exe
                TryLaunchTabTip();
            }
            catch (Exception ex)
            {
                Logger.Error($"Error toggling touch keyboard: {ex.Message}");
                TryLaunchTabTip();
            }
        }

        private static void TryLaunchTabTip()
        {
            try
            {
                var tabtipPath = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
                    "microsoft shared", "ink", "TabTip.exe");

                if (System.IO.File.Exists(tabtipPath))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = tabtipPath,
                        UseShellExecute = true
                    });
                    Logger.Info("Launched TabTip.exe as fallback");
                }
                else
                {
                    Logger.Warn("TabTip.exe not found for fallback");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to launch TabTip.exe: {ex.Message}");
            }
        }

    }
}
