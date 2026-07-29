using System;
using NLog;
using Shared.Data;
using XboxGamingBarHelper.Core;
using XboxGamingBarHelper.Devices.Libraries.Legion;
using XboxGamingBarHelper.Settings;

namespace XboxGamingBarHelper.ControllerEmulation.Viiper
{
    /// <summary>
    /// Owns the VIIPER runtime. Enabled/disabled in response to the
    /// <see cref="EmulationBackendProperty"/> toggle. Currently only brings up the
    /// USBIP server and a single virtual bus — device creation and input forwarding
    /// land in subsequent phases.
    /// </summary>
    internal sealed class ViiperEmulationManager : Manager
    {
        private new static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private const uint DefaultBusId = 1;
        private const string DefaultDeviceType = "xbox360";

        private readonly SettingsManager settingsManager;
        private readonly ControllerEmulationManager legacyManager;
        private readonly ViiperService service = new ViiperService();
        private readonly ViiperInputForwarder forwarder;

        // Helper-owned live-sample property pushed to the widget at ~30Hz while
        // the Sticks & Triggers preview panel is open. Owned here so Program.cs
        // can pick it up via its standard registration list; the forwarder
        // writes to it from the input hot path under its own throttle.
        public readonly ViiperStickTriggerLiveSampleProperty StickTriggerLiveSample;

        private bool isRunning;
        private uint activeBusId;
        private uint activeDeviceId;
        // Secondary device used when the user picks Nintendo → Joy-Con Pair.
        // libviiper publishes Joy-Cons as two distinct USB devices; we hold
        // the second device ID so the forwarder can mirror input to it and
        // Stop() can clean it up. Zero whenever we're in single-device mode.
        private uint activeSecondaryDeviceId;
        private string activeDeviceType = DefaultDeviceType;
        private bool viiperOwnsSuppression;

        // Guide-only mode: when backend=VIIPER and the master Controller-Emulation
        // toggle is OFF, but a Labs button is mapped to "Xbox Guide", we spin up a
        // minimal xbox360 device through libviiper just to deliver the Guide press.
        // This replaces the previous ViGEm X360 pad (which Steam mislabeled as
        // "Nintendo Switch Pro") so VIIPER-only users have zero runtime ViGEm
        // dependency. No forwarder runs in this mode — only TrySetGuideFromLabs
        // submits a 20-byte xbox360 input frame with the Guide bit toggled.
        private bool guideOnlyMode;
        private readonly byte[] guideOnlyBuffer = new byte[20];
        private const ushort XInputGuideBit = 0x0400;
        private static ViiperEmulationManager activeInstance;

        // Rumble-forwarding state for guide-only mode. CE master is OFF in this mode
        // so HidHide isn't suppressing the Legion at all — the Legion's XInput child
        // is fully visible, and XInputSetState works the conventional way. We detect
        // the Legion's XInput slot at plug-in time via XInputGetCapabilitiesEx
        // (matching VID 17EF) and forward rumble through it.
        private int guideOnlyPhysicalXInputSlot = -1;
        private DateTime guideOnlyLastRumbleLog = DateTime.MinValue;
        private const ushort LegionVendorIdShort = 0x17EF;

        private readonly LegionManager legionManager;

        public ViiperEmulationManager(SettingsManager inSettingsManager, ControllerEmulationManager inLegacyManager, LegionManager inLegionManager)
        {
            legionManager = inLegionManager;
            settingsManager = inSettingsManager;
            legacyManager = inLegacyManager;
            forwarder = new ViiperInputForwarder(service, inLegionManager);
            StickTriggerLiveSample = new ViiperStickTriggerLiveSampleProperty(this);
            forwarder.SetStickTriggerLiveSink(StickTriggerLiveSample);
            activeInstance = this;
            if (legacyManager != null)
            {
                // VIIPER respects the same "Enable Controller Emulation" toggle the legacy
                // backend observes — it is the master on/off switch for whichever backend
                // is selected. Flipping the Debug-panel backend selector alone should not
                // auto-start emulation.
                legacyManager.EmulationEnabledChanged += OnEmulationEnabledChanged;
            }
            if (settingsManager != null)
            {
                if (settingsManager.EmulationBackend != null)
                {
                    settingsManager.EmulationBackend.PropertyChanged += OnBackendChanged;
                }
                if (settingsManager.ViiperDeviceType != null)
                {
                    settingsManager.ViiperDeviceType.PropertyChanged += OnDeviceConfigChanged;
                }
                if (settingsManager.ViiperSteamSubDevice != null)
                {
                    settingsManager.ViiperSteamSubDevice.PropertyChanged += OnDeviceConfigChanged;
                }
                if (settingsManager.ViiperSonySubDevice != null)
                {
                    settingsManager.ViiperSonySubDevice.PropertyChanged += OnDeviceConfigChanged;
                }
                if (settingsManager.ViiperNintendoSubDevice != null)
                {
                    settingsManager.ViiperNintendoSubDevice.PropertyChanged += OnDeviceConfigChanged;
                }
                if (settingsManager.ViiperInputSource != null)
                {
                    settingsManager.ViiperInputSource.PropertyChanged += OnInputSourceChanged;
                }
                if (settingsManager.ViiperGyroSource != null)
                {
                    settingsManager.ViiperGyroSource.PropertyChanged += OnGyroSourceChanged;
                }
                if (settingsManager.ViiperJoyconGyroPerHalf != null)
                {
                    settingsManager.ViiperJoyconGyroPerHalf.PropertyChanged += OnJoyconGyroPerHalfChanged;
                }
                if (settingsManager.ViiperGuideButtonMode != null)
                {
                    settingsManager.ViiperGuideButtonMode.PropertyChanged += OnGuideModeChanged;
                }
                if (settingsManager.ViiperDesktopButtonTarget != null)
                {
                    settingsManager.ViiperDesktopButtonTarget.PropertyChanged += OnFrontButtonTargetChanged;
                }
                if (settingsManager.ViiperPageButtonTarget != null)
                {
                    settingsManager.ViiperPageButtonTarget.PropertyChanged += OnFrontButtonTargetChanged;
                }
                if (settingsManager.ViiperSwapRumbleMotors != null)
                {
                    settingsManager.ViiperSwapRumbleMotors.PropertyChanged += OnSwapRumbleMotorsChanged;
                }
                if (settingsManager.ViiperRumbleIntensity != null)
                {
                    settingsManager.ViiperRumbleIntensity.PropertyChanged += OnRumbleIntensityChanged;
                }
                if (settingsManager.ViiperMirrorLightbarToStick != null)
                {
                    settingsManager.ViiperMirrorLightbarToStick.PropertyChanged += OnMirrorLightbarChanged;
                }
                if (settingsManager.ViiperGyroAxisMapX != null)
                {
                    settingsManager.ViiperGyroAxisMapX.PropertyChanged += OnGyroAxisMapChanged;
                }
                if (settingsManager.ViiperGyroAxisMapY != null)
                {
                    settingsManager.ViiperGyroAxisMapY.PropertyChanged += OnGyroAxisMapChanged;
                }
                if (settingsManager.ViiperGyroAxisMapZ != null)
                {
                    settingsManager.ViiperGyroAxisMapZ.PropertyChanged += OnGyroAxisMapChanged;
                }
                if (settingsManager.ViiperGyroTuning != null)
                {
                    settingsManager.ViiperGyroTuning.PropertyChanged += OnGyroTuningChanged;
                }
                if (settingsManager.ViiperAccelTuning != null)
                {
                    settingsManager.ViiperAccelTuning.PropertyChanged += OnGyroTuningChanged;
                }
                if (settingsManager.ViiperStickTriggerConfig != null)
                {
                    settingsManager.ViiperStickTriggerConfig.PropertyChanged += OnStickTriggerConfigChanged;
                }
                if (settingsManager.ViiperStickTriggerPreviewEnabled != null)
                {
                    settingsManager.ViiperStickTriggerPreviewEnabled.PropertyChanged += OnStickTriggerPreviewEnabledChanged;
                    // Apply persisted state immediately so a session restart that
                    // had preview on keeps streaming until the widget collapses.
                    forwarder.SetStickTriggerPreviewEnabled(settingsManager.ViiperStickTriggerPreviewEnabled.Value);
                }
                // Apply initial state — deferred.
                //
                // ApplyBackend(true) walks through Start() which enables
                // HidHide suppression (Nefarius SetVariable + PnP cycle-port)
                // and creates the VIIPER USBIP bus / waits for Windows to
                // enumerate the virtual device. On Legion Go 2 that chain
                // costs ~6s of wall-clock time and used to block helper
                // Initialize() — keeping _managersReady=false, which makes
                // the widget show "Not Connected" spinners for ~12 s on
                // launch. Nothing downstream on the main init path needs
                // VIIPER running (no property reads from it), so the
                // startup apply runs on the thread pool and the bus stands
                // itself up in the background.
                if (settingsManager.EmulationBackend != null)
                {
                    var backendValue = settingsManager.EmulationBackend.Value;
                    _ = System.Threading.Tasks.Task.Run(async () =>
                    {
                        try
                        {
                            // SERIALISE with ControllerEmulationManager's
                            // deferred startup apply. Both tasks used to fire
                            // concurrently on the thread pool and race on the
                            // single HidHide suppression state: VIIPER enabled
                            // + cycle-ported correctly, then 140ms later the
                            // legacy manager's Apply disabled HidHide and its
                            // own re-enum request failed (Windows refuses a
                            // second cycle-port on the same device within that
                            // window), leaving the physical Legion controller
                            // visible to games next to the VIIPER virtual one
                            // (verified on reboot 2026-04-19 20:11:46 log).
                            // Waiting on the shared semaphore guarantees
                            // ControllerEmulationManager's legacy-settle pass
                            // finishes before VIIPER asserts its HidHide state.
                            await XboxGamingBarHelper.ControllerEmulation.ControllerEmulationStartupLock.WaitForLegacyApplyAsync();
                            ApplyBackend(backendValue);
                        }
                        catch (Exception ex) { Logger.Warn($"ViiperEmulationManager deferred startup ApplyBackend failed: {ex.Message}"); }
                    });
                }
            }
        }

        /// <summary>
        /// VIIPER-specific HidHide mode selection. Mode 1 — hide only the Legion's
        /// native HID composite, leave its XInput child (045E:028E) visible.
        ///
        /// <para>Mode 3 (also hide the XInput child via parent-VID filter) was
        /// attempted but broke rumble. xinput1_4.dll's enumeration of the Legion
        /// goes through xinputhid.sys (the Legion uses HID-mode XInput, not
        /// xusb22.sys). When HidHide hides the Legion's native HID, xinputhid
        /// can no longer translate XInput calls for the device — XInputSetState
        /// returns ERROR_DEVICE_NOT_CONNECTED even from the HidHide-allowlisted
        /// helper. PadForge-style direct XUSB IOCTLs don't help because xusb22
        /// never binds. Direct HID-OUT writes don't help because HidHide filters
        /// the HID class device interface from enumeration globally (allowlist
        /// only governs CreateFile-by-known-path, and the path can't be obtained
        /// without enumeration).</para>
        ///
        /// <para>Trade-off accepted: a generic "Controller (Xbox 360 for Windows)"
        /// entry remains in joy.cpl alongside the VIIPER virtual pad. Steam and
        /// most games handle the duplicate gracefully. The much louder "Legion
        /// Controller for Windows" entry (native HID) stays hidden.</para>
        /// </summary>
        private static int ComputeHideTargetForViiper(string targetType)
        {
            return 1;
        }

        /// <summary>True once VIIPER is initialized and a bus is attached.</summary>
        public bool IsRunning { get { return isRunning; } }

        /// <summary>Backing service — exposed for future device/feedback wiring.</summary>
        public ViiperService Service { get { return service; } }

        private void OnBackendChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (settingsManager?.EmulationBackend == null) return;
            ApplyBackend(settingsManager.EmulationBackend.Value);
        }

        private void ApplyBackend(bool useViiper)
        {
            // Mutual exclusion: legacy ControllerEmulationManager is suppressed whenever
            // VIIPER is the active backend. Clearing the suppression re-applies the user's
            // saved legacy configuration.
            try { legacyManager?.SetSuppressedByViiper(useViiper); }
            catch (Exception ex) { Logger.Warn($"legacyManager.SetSuppressedByViiper threw: {ex.Message}"); }

            ReapplyMode();
        }

        private void OnEmulationEnabledChanged(bool emulationEnabled)
        {
            bool backendOn = settingsManager?.EmulationBackend?.Value ?? false;
            if (!backendOn) return; // legacy path handles its own state
            ReapplyMode();
        }

        /// <summary>
        /// Single decision point for which VIIPER mode (full / guide-only / stopped) should
        /// be active given the current backend selector, master CE toggle, and Labs Guide
        /// configuration. Called from ApplyBackend, OnEmulationEnabledChanged, and
        /// OnGuideRouteChanged so any of those inputs flipping converges on the same state.
        /// </summary>
        private void ReapplyMode()
        {
            // Hold startLock across the whole decision tree so any property
            // change that overlaps a still-running Start (HidHide cycle-port +
            // AddDevice can take 2-3s) waits until the current state stabilizes
            // before re-deciding. Without this, two concurrent ReapplyMode
            // callers each call Start(), and the second sees isRunning=false
            // (set late in Start) and tries to CreateBus on an already-init'd
            // service → "bus already allocated" → service.Dispose() wipes
            // everything → libviiper "not initialized" thereafter. Symptom
            // visible in helper_2026-05-20_23.log around 23:13:14.
            lock (startLock)
            {
                bool backendOn = settingsManager?.EmulationBackend?.Value ?? false;
                bool emulationEnabled = legacyManager?.EmulationEnabled ?? true;

                if (backendOn && emulationEnabled)
                {
                    if (guideOnlyMode) StopGuideOnly();
                    StartLocked();
                    return;
                }

                // Guide-only no longer requires backend=VIIPER (ViGEm retirement
                // phase 2: the dedicated Guide-only ViGEm pad in LegionButtonMonitor
                // is gone, so this pad serves the Guide route on EVERY backend).
                // Skip only when the legacy CE's own full pad already owns Guide
                // (backend=Legacy + CE on + xbox pad plugged) — mirroring the old
                // NeedsViGEm exclusion so we never run two virtual pads for one route.
                bool legacyPadOwnsGuide = ControllerEmulationManager.CanHandleExternalGuide();
                if (LabsHasGuideConfigured() && !legacyPadOwnsGuide)
                {
                    // Idempotent: if the guide-only pad is already up, DON'T tear it down and
                    // recreate it. StartGuideOnly runs CleanupAllKnownGhosts, which pnputil-
                    // removes 045E:028E nodes (the guide-only pad's own VID/PID); that removal
                    // triggers a controller re-enumeration, whose ControllerReconnected fires
                    // OnGuideRouteChanged -> back here -> another StartGuideOnly -> another
                    // cleanup... a self-sustaining restart loop that never let the pad settle
                    // and left Guide presses hitting a pad about to be torn down
                    // (field-diagnosed 2026-07-23). Only (re)start when not already active.
                    if (guideOnlyMode && !isRunning)
                    {
                        return;
                    }
                    if (isRunning) StopLocked();
                    StartGuideOnly();
                    return;
                }

                if (isRunning) StopLocked();
                if (guideOnlyMode) StopGuideOnly();
            }
        }

        private static bool LabsHasGuideConfigured()
        {
            try { return Program.legionButtonMonitor?.HasGuideActionConfigured ?? false; }
            catch { return false; }
        }

        /// <summary>
        /// Re-evaluate mode when Labs button configuration changes (a Guide action is
        /// added or removed). Called from Program.NotifyGuideRouteChanged so the guide-only
        /// pad appears / disappears without waiting for the next backend or CE flip.
        /// </summary>
        public void OnGuideRouteChanged()
        {
            try { ReapplyMode(); }
            catch (Exception ex) { Logger.Warn($"ViiperEmulationManager.OnGuideRouteChanged threw: {ex.Message}"); }
        }

        /// <summary>
        /// Neutralize the emulated pad's input while Desktop Controls is active (the physical
        /// stick/buttons drive the mouse/keyboard, so the game must not also see them). No-op
        /// unless full emulation is running - guide-only mode has no forwarded input to pause.
        /// Called from LegionManager when the Desktop Controls state changes.
        /// </summary>
        public static void SetDesktopControlsActive(bool active)
        {
            var inst = activeInstance;
            if (inst == null) return;
            try { inst.forwarder?.SetDesktopControlsActive(active); }
            catch (Exception ex) { Logger.Warn($"SetDesktopControlsActive threw: {ex.Message}"); }
        }

        /// <summary>
        /// Route system sleep/resume into the forwarder so it stops emitting while suspended
        /// (a flooding virtual gyro can wake the device — HC #541) and clears any latched
        /// input + rumble on both edges (HC aade204f9). Wired from SystemManager in Program.
        /// No-op unless full emulation is running.
        /// </summary>
        public static void SetSystemSuspended(bool suspended)
        {
            var inst = activeInstance;
            if (inst == null) return;
            try { inst.forwarder?.SetSystemSuspended(suspended); }
            catch (Exception ex) { Logger.Warn($"SetSystemSuspended threw: {ex.Message}"); }
        }

        /// <summary>True when the minimal guide-only xbox360 pad is plugged.</summary>
        public static bool IsGuideOnlyActive
        {
            get
            {
                var inst = activeInstance;
                return inst != null && inst.guideOnlyMode;
            }
        }

        /// <summary>
        /// Called from LegionButtonMonitor when a mapped Labs button raises a Guide
        /// press/release. Returns true only when guide-only mode is currently active
        /// (full CE path owns Guide through ViiperInputForwarder.TryHandleGuideButtonFromLabs).
        /// </summary>
        // Minimum time the Guide bit stays latched on the virtual pad. A "tap" from the
        // Legion-button long-press mechanism fires press+release back-to-back (microseconds
        // apart); without a floor, Windows/XInput never polls the pad while the bit is set,
        // so the Guide press is invisible - "Guide works only when Legion L hold is
        // Disabled" (field-diagnosed 2026-07-23). 90ms comfortably spans a 60Hz+ poll.
        private const int GuideOnlyMinHoldMs = 90;
        private long _guideOnlyPressTicks;
        private int _guideOnlyReleaseScheduled;

        public static bool TrySetGuideFromLabs(bool pressed)
        {
            var inst = activeInstance;
            if (inst == null || !inst.guideOnlyMode || inst.activeDeviceId == 0) return false;

            if (pressed)
            {
                inst._guideOnlyPressTicks = DateTime.UtcNow.Ticks;
                return inst.SubmitGuideOnlyFrame(true);
            }

            // Release: honor the minimum hold so a fast tap-burst still registers.
            long heldMs = (DateTime.UtcNow.Ticks - inst._guideOnlyPressTicks) / TimeSpan.TicksPerMillisecond;
            if (heldMs >= GuideOnlyMinHoldMs)
            {
                return inst.SubmitGuideOnlyFrame(false);
            }
            // Defer the release on a background thread; coalesce concurrent taps.
            if (System.Threading.Interlocked.Exchange(ref inst._guideOnlyReleaseScheduled, 1) == 0)
            {
                int wait = (int)(GuideOnlyMinHoldMs - heldMs);
                System.Threading.Tasks.Task.Run(async () =>
                {
                    try { await System.Threading.Tasks.Task.Delay(wait); inst.SubmitGuideOnlyFrame(false); }
                    catch (Exception ex) { Logger.Warn($"guide-only deferred release threw: {ex.Message}"); }
                    finally { System.Threading.Interlocked.Exchange(ref inst._guideOnlyReleaseScheduled, 0); }
                });
            }
            return true;
        }

        private bool SubmitGuideOnlyFrame(bool pressed)
        {
            // xbox360 wire format (BuildXbox360Input): 20 bytes, [0..3] buttons (u32 LE),
            // [4]=LT, [5]=RT, [6..7]=LX, [8..9]=LY, [10..11]=RX, [12..13]=RY. All zero
            // except for the Guide bit (0x0400) at byte 1 when pressed.
            Array.Clear(guideOnlyBuffer, 0, guideOnlyBuffer.Length);
            if (pressed)
            {
                guideOnlyBuffer[1] = (byte)((XInputGuideBit >> 8) & 0xFF);
                guideOnlyBuffer[0] = (byte)(XInputGuideBit & 0xFF);
            }

            try
            {
                bool ok = service.SetInput(activeBusId, activeDeviceId, guideOnlyBuffer);
                if (!ok) Logger.Warn($"VIIPER guide-only SetInput(pressed={pressed}) failed");
                return ok;
            }
            catch (Exception ex)
            {
                Logger.Warn($"VIIPER guide-only SubmitGuideOnlyFrame threw: {ex.Message}");
                return false;
            }
        }

        private void StartGuideOnly()
        {
            if (guideOnlyMode) return;

            if (settingsManager?.UsbipInstalled != null && !settingsManager.UsbipInstalled.Value)
            {
                Logger.Warn("VIIPER guide-only requested but usbip-win2 is not installed; staying offline.");
                return;
            }

            if (!service.Initialize())
            {
                Logger.Warn("VIIPER guide-only: service init failed.");
                return;
            }

            if (!service.CreateBus(DefaultBusId))
            {
                Logger.Warn($"VIIPER guide-only: failed to create bus {DefaultBusId}.");
                service.Dispose();
                return;
            }
            activeBusId = DefaultBusId;

            // No HidHide here — we are NOT cloaking the physical controller in this mode.
            // CE master is off, so the user wants their real pad visible. The guide-only
            // virtual pad sits alongside it just to deliver Game-Bar guide presses.

            ViiperPnpCleanup.CleanupAllKnownGhosts();

            // A prior emulation session's pad may have been auto-reattached by usbip2_ude
            // after our DetachAll - force the next attach pass to sweep such zombies, else
            // this guide-only add sees a stale import as "already attached" and skips it,
            // leaving the guide route dead after an emu on->off cycle (field report 2026-07-23).
            UsbipCli.NoteServerStarted();

            var addResult = service.AddDevice(activeBusId, DefaultDeviceType, 0, 0);
            if (!addResult.Success)
            {
                Logger.Warn("VIIPER guide-only: failed to add xbox360 device; tearing down.");
                service.RemoveBus(activeBusId);
                service.Dispose();
                return;
            }
            activeDeviceId = addResult.DeviceId;
            activeDeviceType = DefaultDeviceType;
            guideOnlyMode = true;

            // Seed an idle frame so Windows sees the device as connected/centered before
            // any Labs press arrives. Some games XInput-poll on connect and would see
            // garbage if the first frame had Guide latched.
            SubmitGuideOnlyFrame(false);

            // Wire rumble forwarding via XInputSetState. CE master is off here,
            // so HidHide isn't applied — Legion XInput child is visible and
            // standard XInput works. Slot detection runs async after ~1.2s to
            // let Windows enumerate the new USB-IP virtual pad first; otherwise
            // the slot table may not show both pads yet.
            service.FeedbackReceived += OnGuideOnlyFeedback;
            System.Threading.Tasks.Task.Run(async () =>
            {
                await System.Threading.Tasks.Task.Delay(1200);
                DetectGuideOnlyLegionSlot();
            });

            Logger.Info($"VIIPER guide-only emulation started (bus={activeBusId}, dev={activeDeviceId}, type={DefaultDeviceType})");

            // Pad came up — Labs no longer needs its dedicated ViGEm Guide pad.
            Program.NotifyGuideRouteChanged();
        }

        private void OnGuideOnlyFeedback(uint cbBus, uint cbDev, byte[] data)
        {
            if (!guideOnlyMode || cbBus != activeBusId || cbDev != activeDeviceId) return;
            if (data == null || data.Length < 2) return;
            int slot = guideOnlyPhysicalXInputSlot;
            if (slot < 0) return;

            // xbox360 wire format: data[0] = large (left) motor, data[1] = small (right).
            byte large = data[0];
            byte small = data[1];
            try
            {
                var vib = new ViiperXInputVibration
                {
                    LeftMotorSpeed = (ushort)(large * 257),
                    RightMotorSpeed = (ushort)(small * 257),
                };
                uint rc = ViiperXInput.SetState((uint)slot, ref vib);
                if (rc != ViiperXInput.ErrorSuccess)
                {
                    DateTime now = DateTime.UtcNow;
                    if ((now - guideOnlyLastRumbleLog).TotalSeconds >= 5)
                    {
                        guideOnlyLastRumbleLog = now;
                        Logger.Debug($"VIIPER guide-only rumble forward failed (slot={slot}, rc={rc})");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"VIIPER guide-only OnGuideOnlyFeedback threw: {ex.Message}");
            }
        }

        /// <summary>
        /// Scan XInput slots 0..3 with XInputGetCapabilitiesEx, identify the
        /// slot whose VID is 0x17EF (Legion), cache it for OnGuideOnlyFeedback
        /// to use. CE master is off in this mode so HidHide isn't suppressing
        /// the Legion XInput child — it's fully discoverable.
        /// </summary>
        private void DetectGuideOnlyLegionSlot()
        {
            var caps = new ViiperXInputCapabilitiesEx();
            var summary = new System.Text.StringBuilder();
            int physical = -1;
            for (uint i = 0; i < 4; i++)
            {
                caps = default;
                uint rc;
                try { rc = ViiperXInput.GetCapabilitiesEx(1, i, 1, ref caps); }
                catch (Exception ex)
                {
                    Logger.Warn($"VIIPER guide-only: XInputGetCapabilitiesEx threw on slot {i}: {ex.Message}");
                    return;
                }
                if (rc != ViiperXInput.ErrorSuccess) continue;

                if (summary.Length > 0) summary.Append(", ");
                summary.Append($"slot{i}=VID:{caps.VendorId:X4}/PID:{caps.ProductId:X4}");
                if (caps.VendorId == LegionVendorIdShort && physical < 0)
                {
                    physical = (int)i;
                }
            }
            guideOnlyPhysicalXInputSlot = physical;
            if (physical < 0)
            {
                Logger.Warn($"VIIPER guide-only: no Legion (VID:17EF) XInput slot found [{summary}] — rumble forwarding disabled");
            }
            else
            {
                Logger.Info($"VIIPER guide-only: XInput slots [{summary}] — physical Legion=slot{physical}, rumble forwarded there");
            }
        }

        private void StopGuideOnly()
        {
            if (!guideOnlyMode) return;

            try { service.FeedbackReceived -= OnGuideOnlyFeedback; }
            catch { /* best-effort unsubscribe */ }

            // Zero out any rumble we left running so we don't strand the real Legion
            // vibrating when guide-only tears down.
            if (guideOnlyPhysicalXInputSlot >= 0)
            {
                try
                {
                    var stop = new ViiperXInputVibration { LeftMotorSpeed = 0, RightMotorSpeed = 0 };
                    ViiperXInput.SetState((uint)guideOnlyPhysicalXInputSlot, ref stop);
                }
                catch { /* harmless */ }
            }
            guideOnlyPhysicalXInputSlot = -1;

            // Detach our UDE imports first (we attach explicitly via usbip.exe on add — see
            // UsbipCli), then tear down the server side.
            try { UsbipCli.DetachAll(); }
            catch (Exception ex) { Logger.Warn($"VIIPER guide-only usbip detach threw: {ex.Message}"); }

            try
            {
                if (activeDeviceId != 0)
                {
                    service.RemoveDevice(activeBusId, activeDeviceId);
                }
            }
            catch (Exception ex) { Logger.Warn($"VIIPER guide-only RemoveDevice threw: {ex.Message}"); }

            try { service.RemoveBus(activeBusId); }
            catch (Exception ex) { Logger.Warn($"VIIPER guide-only RemoveBus threw: {ex.Message}"); }

            try { service.Dispose(); }
            catch (Exception ex) { Logger.Warn($"VIIPER guide-only Dispose threw: {ex.Message}"); }

            activeDeviceId = 0;
            guideOnlyMode = false;
            Logger.Info("VIIPER guide-only emulation stopped");

            // Pad went away — Labs may need to spin up the legacy ViGEm Guide pad if
            // backend just flipped to Legacy, or if CE is about to own Guide via the
            // full forwarder (in which case Labs will skip ViGEm and wait on CE).
            Program.NotifyGuideRouteChanged();
        }

        // Serializes Start() so concurrent callers can't both pass the isRunning
        // guard. isRunning is only set true at the END of Start (after a multi-
        // second HidHide cycle-port + AddDevice + forwarder.Start dance), so
        // without this lock a second call from OnBackendChanged / settings load
        // races and tries to CreateBus on an already-initialized service —
        // which fails with "bus already allocated" and was previously triggering
        // a service.Dispose() that wiped out the first caller's working state.
        private readonly object startLock = new object();

        /// <summary>
        /// Idempotent start. Returns true if VIIPER is (or became) running.
        /// Safe to call when usbip-win2 is missing — returns false with a logged error.
        /// </summary>
        public bool Start()
        {
            lock (startLock)
            {
                return StartLocked();
            }
        }

        private bool StartLocked()
        {
            if (isRunning) return true;

            if (settingsManager?.UsbipInstalled != null && !settingsManager.UsbipInstalled.Value)
            {
                Logger.Warn("VIIPER start requested but usbip-win2 is not installed; leaving service offline.");
                return false;
            }

            if (!service.Initialize())
            {
                Logger.Warn("VIIPER init failed; emulation backend will fall back to legacy at runtime.");
                return false;
            }

            if (!service.CreateBus(DefaultBusId))
            {
                Logger.Warn($"VIIPER failed to create bus {DefaultBusId}; shutting down service.");
                service.Dispose();
                return false;
            }
            activeBusId = DefaultBusId;

            // Resolve the target FIRST so the HidHide call below uses the right hide
            // mode for the device type we're about to publish. Without this, the user's
            // saved legacy HideTarget value (often 0 = "native HID only") leaks into
            // VIIPER and leaves the Legion's XInput child visible to games, producing
            // double input on Game Bar (Legion XInput + virtual DSE HID).
            string targetType;
            ushort vid, pid;
            ResolveDeviceTargets(out targetType, out vid, out pid);

            // Enable HidHide so the stock physical controller is hidden from games while
            // VIIPER is active. Reuses the existing ControllerSuppressionManager instance
            // owned by the legacy manager — sharing the same underlying hide/unhide state
            // prevents both backends from fighting over the same device IDs.
            try
            {
                var suppression = legacyManager?.SuppressionManager;
                if (suppression != null)
                {
                    int hideMode = ComputeHideTargetForViiper(targetType);
                    bool ok = suppression.Enable(
                        legacyManager.HandheldDeviceType,
                        hideMode);
                    Logger.Info($"VIIPER: HidHide suppression enable (target={targetType}, hideMode={hideMode}) => {ok}");
                    viiperOwnsSuppression = ok;
                }
            }
            catch (Exception ex) { Logger.Warn($"VIIPER HidHide Enable threw: {ex.Message}"); }

            // Firmware-level XInput suppression (register 04/0f) is NO LONGER applied
            // automatically on emulation start. It also disables the firmware's front-button
            // Desktop-controls behavior (field report 2026-07-23: Desktop controls break while
            // OS reporting is disabled), and the interaction needs more testing. HidHide still
            // cloaks the stock pad as before. Users who want the stronger firmware-level hide
            // can toggle "Disable OS Reporting" manually in Controller Settings.

            // Sweep ghost PnP entries left over from a previous helper session.
            // Hot-swaps in libviiper detach the old USB device, but Windows keeps
            // a Present=False entry for every (VID,PID,serial) it has ever seen.
            // Steam Input's controller manager enumerates those ghosts and can
            // pin a game's rumble routing to one, so live rumble events stop
            // reaching the active device. Run this BEFORE AddDevice so the bus
            // is clean when the new device arrives.
            ViiperPnpCleanup.CleanupAllKnownGhosts();

            // joycon-pair publishes two separate USB devices (left + right
            // Joy-Cons) on the same bus. libviiper has no "joycon-pair"
            // alias; we register the left half first, then add the right
            // half below, and keep activeDeviceType = "joycon-pair" so the
            // forwarder's BuildDeviceInput picks the paired wire format.
            string registryName = targetType == "joycon-pair" ? "joycon-left" : targetType;
            UsbipCli.NoteServerStarted(); // sweep any zombie import from a prior session before attaching
            var addResult = service.AddDevice(activeBusId, registryName, vid, pid);
            if (!addResult.Success)
            {
                Logger.Warn($"VIIPER failed to add {registryName} device (target={targetType}); tearing down.");
                service.RemoveBus(activeBusId);
                service.Dispose();
                return false;
            }
            activeDeviceId = addResult.DeviceId;
            activeDeviceType = targetType;
            activeSecondaryDeviceId = 0;

            if (targetType == "joycon-pair")
            {
                var secondary = service.AddDevice(activeBusId, "joycon-right", 0, 0);
                if (!secondary.Success)
                {
                    Logger.Warn("VIIPER joycon-pair: secondary joycon-right add failed; tearing down primary.");
                    service.RemoveDevice(activeBusId, activeDeviceId);
                    service.RemoveBus(activeBusId);
                    service.Dispose();
                    activeDeviceId = 0;
                    return false;
                }
                activeSecondaryDeviceId = secondary.DeviceId;
                Logger.Info($"VIIPER joycon-pair: primary={activeDeviceId} (joycon-left), secondary={activeSecondaryDeviceId} (joycon-right)");
            }

            // Start forwarding physical input -> virtual device.
            uint xinputIdx = ViiperInputForwarder.DetectPhysicalXInputIndex();
            forwarder.SetInputSource(ResolveInputSource());
            forwarder.SetGyroSource(ResolveGyroSource());
            forwarder.SetJoyconGyroPerHalf(settingsManager?.ViiperJoyconGyroPerHalf?.Value ?? false);
            forwarder.SetGuideButtonMode(ResolveGuideMode());
            forwarder.SetFrontButtonTargets(
                settingsManager?.ViiperDesktopButtonTarget?.Value ?? "guide",
                settingsManager?.ViiperPageButtonTarget?.Value ?? "touchpad");
            forwarder.SetSwapRumbleMotors(settingsManager?.ViiperSwapRumbleMotors?.Value ?? false);
            forwarder.SetRumbleIntensity(settingsManager?.ViiperRumbleIntensity?.Value ?? 100);
            forwarder.SetMirrorLightbarToStick(settingsManager?.ViiperMirrorLightbarToStick?.Value ?? false);
            forwarder.SetGyroAxisMapping(
                settingsManager?.ViiperGyroAxisMapX?.Value ?? "X",
                settingsManager?.ViiperGyroAxisMapY?.Value ?? "Y",
                settingsManager?.ViiperGyroAxisMapZ?.Value ?? "Z");
            ApplyTuningForActiveSource();
            forwarder.SetStickTriggerConfig(StickTriggerConfigBundle.Deserialize(
                settingsManager?.ViiperStickTriggerConfig?.Value ?? string.Empty));
            // Re-assert Desktop Controls neutralization for the fresh forwarder: if Desktop
            // Controls was already ON when emulation started, the property-change hook never
            // fired for this forwarder instance.
            forwarder.SetDesktopControlsActive(legionManager?.LegionDesktopControls?.Value ?? false);
            forwarder.Start(xinputIdx, activeBusId, activeDeviceId, activeDeviceType);
            forwarder.SetSecondaryDevice(activeSecondaryDeviceId);

            isRunning = true;
            Logger.Info($"VIIPER emulation manager started (bus={activeBusId}, dev={activeDeviceId}, sec={activeSecondaryDeviceId}, type={activeDeviceType}, xinput={xinputIdx})");

            // Re-run guide-route reconciliation now that the full forwarder owns
            // Guide delivery — this tears down our own guide-only pad if it was
            // running (ReapplyMode: full mode stops guide-only). MUST happen
            // after forwarder.Start because the delivery tier checks
            // ViiperInputForwarder.CanHandleExternalGuide(), which only returns
            // true once the forwarder's running flag is set inside Start().
            Program.NotifyGuideRouteChanged();

            // The guide-only pad lived on its own XInput user-index slot.
            // Detection above ran while that pad may still have occupied a slot,
            // so the picked index can be the about-to-be-disposed pad rather
            // than the physical Legion. After NotifyGuideRouteChanged disposes
            // it, re-probe XInput so the forwarder re-pins to the slot that's
            // actually connected. Without this re-pin: rumbleErr floods,
            // xinputFresh=1 then errors (build 2100/2101 logs, ViGEm era — the
            // slot-occupancy mechanics are identical for the VIIPER pad).
            System.Threading.Thread.Sleep(150);
            uint repinnedIdx = ViiperInputForwarder.DetectPhysicalXInputIndex();
            if (repinnedIdx != xinputIdx)
            {
                Logger.Info($"VIIPER post-Start re-pin: physicalIndex {xinputIdx} -> {repinnedIdx} (guide-only pad slot freed)");
                forwarder.UpdatePhysicalIndex(repinnedIdx);
            }
            return true;
        }

        /// <summary>
        /// Determines the (deviceType, vid, pid) tuple to create based on current settings.
        /// </summary>
        private void ResolveDeviceTargets(out string targetType, out ushort vid, out ushort pid)
        {
            targetType = DefaultDeviceType;
            vid = 0;
            pid = 0;

            if (settingsManager?.ViiperDeviceType != null && !string.IsNullOrEmpty(settingsManager.ViiperDeviceType.Value))
            {
                targetType = settingsManager.ViiperDeviceType.Value;
            }

            // Legacy fallback: "xboxelite2" was removed from the widget after
            // RE confirmed the GIP allow-list wall (no XInput child resolves).
            // Coerce persisted user settings forward so anyone who had it
            // selected before the UI change keeps getting a working pad.
            if (targetType == "xboxelite2")
            {
                Logger.Info("VIIPER: coercing persisted xboxelite2 target → xbox360 (Elite 2 disabled in UI).");
                targetType = "xbox360";
            }

            // "sony" is the widget's parent grouping for Sony virtual devices —
            // the actual libviiper target comes from ViiperSonySubDevice.
            // Values: dualsense / dualsense-edge / dualshock4. No VID/PID
            // override (libviiper's aliases supply correct defaults).
            if (targetType == "sony" && settingsManager?.ViiperSonySubDevice != null)
            {
                targetType = ViiperSonySubDeviceProperty.ResolveLibViiperTarget(settingsManager.ViiperSonySubDevice.Value);
                return;
            }

            // "nintendo" parent grouping: Switch Pro, Switch Pro 2 (placeholder),
            // and the three Joy-Con configurations. For joycon-pair we keep
            // targetType = "joycon-pair" so the forwarder picks the dual-Joy-Con
            // wire format; Start() then publishes BOTH joycon-left AND
            // joycon-right devices on the bus. ResolvePrimary returns the
            // libviiper alias for whichever device gets registered first.
            if (targetType == "nintendo" && settingsManager?.ViiperNintendoSubDevice != null)
            {
                string sub = settingsManager.ViiperNintendoSubDevice.Value;
                targetType = sub == "joycon-pair"
                    ? "joycon-pair"
                    : ViiperNintendoSubDeviceProperty.ResolvePrimaryLibViiperTarget(sub);
                return;
            }

            // Steam family. Sub-device determines BOTH the libviiper target and
            // the VID/PID override:
            //   "gordon"        → libviiper target "gordon" (Steam Controller V1,
            //                     own native wire format, native VID/PID — no override)
            //   anything else   → libviiper target stays "steam-generic" (routes
            //                     to the steamdeck device) with a per-handheld
            //                     VID/PID override applied.
            bool isSteam = targetType == "steam-generic"
                || targetType == "steam-controller"
                || targetType == "steamdeck-generic";
            if (isSteam && settingsManager?.ViiperSteamSubDevice != null)
            {
                string subDev = settingsManager.ViiperSteamSubDevice.Value;
                if (subDev == "gordon")
                {
                    targetType = "gordon";
                    return;
                }
                ViiperSteamSubDeviceProperty.TryGetSteamVidPid(subDev, out vid, out pid);
            }

            // xboxgip target removed from widget after 2026-05-19 RE
            // (see [[project_gip_definitive_walls_2026-05-19]]). For
            // USB-IP virtual gamepads, xboxgip.sys's descriptor-handler
            // state gate (slot+0x140==1 Interrogating) is never set by
            // any code path on USB main devices, so IGamepad child PDOs
            // can't publish. The target alias is left handled in libviiper
            // and in the BuildDeviceInput dispatcher for future experiments
            // (wireless-adapter emulation, sub-device-under-preloaded-PID
            // parent), but is no longer user-selectable.
        }

        private ViiperInputSourceKind ResolveInputSource()
        {
            var value = settingsManager?.ViiperInputSource?.Value ?? "XInput";
            if (string.Equals(value, "LegionHid", StringComparison.OrdinalIgnoreCase))
            {
                return ViiperInputSourceKind.LegionHid;
            }
            // XInput requested — but with "Disable OS Reporting" on, the firmware register
            // 04/0f is in vendor-exclusive mode and the physical pad emits NO XInput, so
            // this source would read nothing and the emulated pad would sit neutral
            // ("emulation broken"). The vendor report stream still flows, so fall back to
            // LegionHid for the session on Legion hardware.
            if (legionManager?.LegionOsReportingDisabled?.Value == true
                && legionManager?.LegionGoDetected?.Value == true)
            {
                Logger.Warn("VIIPER input source XInput is unavailable while OS reporting is disabled (04/0f vendor-exclusive) — using Legion Go HID for this session");
                return ViiperInputSourceKind.LegionHid;
            }
            return ViiperInputSourceKind.XInput;
        }

        private void OnInputSourceChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            try { forwarder.SetInputSource(ResolveInputSource()); }
            catch (Exception ex) { Logger.Warn($"OnInputSourceChanged threw: {ex.Message}"); }
            // (Firmware XInput suppression is no longer auto-driven by emulation - see
            // StartLocked. The manual "Disable OS Reporting" toggle owns register 04/0f.)
        }

        private ViiperGyroSourceKind ResolveGyroSource()
        {
            var value = settingsManager?.ViiperGyroSource?.Value ?? "None";
            switch (value)
            {
                case "Left":     return ViiperGyroSourceKind.Left;
                case "Right":    return ViiperGyroSourceKind.Right;
                case "Mixed":    return ViiperGyroSourceKind.Mixed;
                case "Handheld": return ViiperGyroSourceKind.Handheld;
                default:         return ViiperGyroSourceKind.None;
            }
        }

        private void OnGyroSourceChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            try
            {
                forwarder.SetGyroSource(ResolveGyroSource());
                // The active source changed, so the per-source tuning entry that applies
                // changed too — re-resolve and push it.
                ApplyTuningForActiveSource();
            }
            catch (Exception ex) { Logger.Warn($"OnGyroSourceChanged threw: {ex.Message}"); }
        }

        private void OnJoyconGyroPerHalfChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            try { forwarder.SetJoyconGyroPerHalf(settingsManager?.ViiperJoyconGyroPerHalf?.Value ?? false); }
            catch (Exception ex) { Logger.Warn($"OnJoyconGyroPerHalfChanged threw: {ex.Message}"); }
        }

        private ViiperGuideButtonMode ResolveGuideMode()
        {
            var value = settingsManager?.ViiperGuideButtonMode?.Value ?? "Native";
            return string.Equals(value, "GameBar", StringComparison.OrdinalIgnoreCase)
                ? ViiperGuideButtonMode.GameBar
                : ViiperGuideButtonMode.Native;
        }

        private void OnGuideModeChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            try { forwarder.SetGuideButtonMode(ResolveGuideMode()); }
            catch (Exception ex) { Logger.Warn($"OnGuideModeChanged threw: {ex.Message}"); }
        }

        private void OnFrontButtonTargetChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            try
            {
                forwarder.SetFrontButtonTargets(
                    settingsManager?.ViiperDesktopButtonTarget?.Value ?? "guide",
                    settingsManager?.ViiperPageButtonTarget?.Value ?? "touchpad");
            }
            catch (Exception ex) { Logger.Warn($"OnFrontButtonTargetChanged threw: {ex.Message}"); }
        }

        private void OnSwapRumbleMotorsChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            try { forwarder.SetSwapRumbleMotors(settingsManager?.ViiperSwapRumbleMotors?.Value ?? false); }
            catch (Exception ex) { Logger.Warn($"OnSwapRumbleMotorsChanged threw: {ex.Message}"); }
        }

        private void OnRumbleIntensityChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            try { forwarder.SetRumbleIntensity(settingsManager?.ViiperRumbleIntensity?.Value ?? 100); }
            catch (Exception ex) { Logger.Warn($"OnRumbleIntensityChanged threw: {ex.Message}"); }
        }

        private void OnStickTriggerConfigChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            try
            {
                var serialized = settingsManager?.ViiperStickTriggerConfig?.Value ?? string.Empty;
                var bundle = StickTriggerConfigBundle.Deserialize(serialized);
                forwarder.SetStickTriggerConfig(bundle);
            }
            catch (Exception ex) { Logger.Warn($"OnStickTriggerConfigChanged threw: {ex.Message}"); }
        }

        private void OnStickTriggerPreviewEnabledChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            try { forwarder.SetStickTriggerPreviewEnabled(settingsManager?.ViiperStickTriggerPreviewEnabled?.Value ?? false); }
            catch (Exception ex) { Logger.Warn($"OnStickTriggerPreviewEnabledChanged threw: {ex.Message}"); }
        }

        private void OnMirrorLightbarChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            try { forwarder.SetMirrorLightbarToStick(settingsManager?.ViiperMirrorLightbarToStick?.Value ?? false); }
            catch (Exception ex) { Logger.Warn($"OnMirrorLightbarChanged threw: {ex.Message}"); }
        }

        private void OnGyroAxisMapChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            try
            {
                forwarder.SetGyroAxisMapping(
                    settingsManager?.ViiperGyroAxisMapX?.Value ?? "X",
                    settingsManager?.ViiperGyroAxisMapY?.Value ?? "Y",
                    settingsManager?.ViiperGyroAxisMapZ?.Value ?? "Z");
            }
            catch (Exception ex) { Logger.Warn($"OnGyroAxisMapChanged threw: {ex.Message}"); }
        }

        private void OnGyroTuningChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            try { ApplyTuningForActiveSource(); }
            catch (Exception ex) { Logger.Warn($"OnGyroTuningChanged threw: {ex.Message}"); }
        }

        // Gyro Tuning is stored per gyro source (Left/Right/Mixed/Handheld) because the two
        // Legion halves have physically mirrored IMUs, so each source can need its own axis
        // map. Both the gyro and accel properties hold a bundle string
        // "Left=csv|Right=csv|Mixed=csv|Handheld=csv"; here we pull the entry matching the
        // currently-selected source and push it (as a single "mapX,mapY,mapZ,invX,invY,invZ"
        // csv) to the forwarder, which only ever emulates one source at a time.
        private void ApplyTuningForActiveSource()
        {
            if (forwarder == null) return;
            var source = settingsManager?.ViiperGyroSource?.Value ?? "None";
            forwarder.SetGyroTuning(ExtractTuningForSource(settingsManager?.ViiperGyroTuning?.Value, source));
            forwarder.SetAccelTuning(ExtractTuningForSource(settingsManager?.ViiperAccelTuning?.Value, source));
        }

        // Parse "Left=csv|Right=csv|..." and return the csv for the given source, or identity
        // if the bundle is empty / has no entry for that source.
        private static string ExtractTuningForSource(string bundle, string source)
        {
            const string identity = "X,Y,Z,0,0,0";
            if (string.IsNullOrWhiteSpace(bundle) || string.IsNullOrWhiteSpace(source)) return identity;
            foreach (var entry in bundle.Split('|'))
            {
                int eq = entry.IndexOf('=');
                if (eq <= 0) continue;
                if (string.Equals(entry.Substring(0, eq).Trim(), source, StringComparison.OrdinalIgnoreCase))
                {
                    var csv = entry.Substring(eq + 1).Trim();
                    return string.IsNullOrWhiteSpace(csv) ? identity : csv;
                }
            }
            return identity;
        }

        private System.Threading.Timer _deviceConfigDebounceTimer;
        private readonly object _deviceSwapLock = new object();
        private const int DeviceConfigDebounceMs = 500;

        private void OnDeviceConfigChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (!isRunning) return; // Will be picked up on next Start().

            // Debounce rapid config changes (e.g. spinning the Sony sub-device dropdown) into a
            // single hot-swap once the selection settles. Without this, every intermediate value
            // fired a full RemoveDevice+AddDevice, churning virtual devices faster than the async
            // ghost-PnP cleanup could evict them — leaving phantom duplicate gamepads (all fed by
            // the one forwarder, hence identical input).
            var t = _deviceConfigDebounceTimer;
            if (t == null)
                _deviceConfigDebounceTimer = new System.Threading.Timer(
                    _ => PerformDeviceConfigSwap(), null, DeviceConfigDebounceMs, System.Threading.Timeout.Infinite);
            else
                t.Change(DeviceConfigDebounceMs, System.Threading.Timeout.Infinite);
        }

        private void PerformDeviceConfigSwap()
        {
            lock (_deviceSwapLock)
            {
            try
            {
            if (!isRunning) return;

            string oldType = activeDeviceType;
            string newType;
            ushort vid, pid;
            ResolveDeviceTargets(out newType, out vid, out pid);
            if (newType == activeDeviceType && vid == 0 && pid == 0)
            {
                return;
            }

            // joycon-pair is a two-device topology; libviiper's SwitchDeviceType
            // only operates on a single device. Crossing the pair boundary in
            // either direction requires a clean teardown so we don't leak the
            // secondary (entering) or end up with an orphan (leaving).
            if (oldType == "joycon-pair" || newType == "joycon-pair")
            {
                Logger.Info($"VIIPER pair-boundary swap ({oldType} -> {newType}): doing full Stop+Start.");
                Stop();
                Start();
                return;
            }

            Logger.Info($"VIIPER hot-swap: {activeDeviceType} -> {newType} (vid=0x{vid:X4}, pid=0x{pid:X4})");

            // Pause the forwarder for the whole swap: RemoveDevice is instant but AddDevice
            // can take 1-2 seconds (USBIP round-trip). Without this gate the poll loop
            // floods the log with "invalid input size" warnings and may push a wrong-format
            // packet at the new device before we can flip targetType.
            forwarder.SetPaused(true);
            try
            {
                // Deliberately detach our vhci import BEFORE removing the device. If the
                // import instead drops out from under the driver (device removed while
                // attached), usbip2_ude's auto-reattach (ReattachMaxAttempts=20 by default)
                // schedules its own re-import of the busid — which races the attach pass we
                // run on device-added and lands a SECOND import of the same virtual pad
                // (field-confirmed 2026-07-16: "1-1 is imported 2 times" on roughly every
                // other swap, with the duplicate sweeper winning it back each time). A
                // user-initiated detach is not auto-reattached, so detaching first leaves
                // exactly one import path: ours, after the new device appears.
                try { UsbipCli.DetachAll(); }
                catch (Exception ex) { Logger.Warn($"VIIPER hot-swap pre-detach threw: {ex.Message}"); }

                var swap = service.SwitchDeviceType(activeBusId, activeDeviceId, newType, vid, pid);
                if (!swap.Success)
                {
                    Logger.Warn($"VIIPER hot-swap failed ({oldType} -> {newType}); attempting to re-add old device to recover.");
                    // RemoveDevice already succeeded inside SwitchDeviceType, so the old
                    // pad is gone. Re-add the old type with fresh vid/pid so the user
                    // isn't left with no virtual controller.
                    ushort oldVid = 0, oldPid = 0;
                    if (oldType == "steam-generic" || oldType == "steam-controller" || oldType == "steamdeck-generic")
                    {
                        ViiperSteamSubDeviceProperty.TryGetSteamVidPid(
                            settingsManager?.ViiperSteamSubDevice?.Value ?? "generic",
                            out oldVid, out oldPid);
                    }
                    var recover = service.AddDevice(activeBusId, oldType, oldVid, oldPid);
                    if (recover.Success)
                    {
                        activeDeviceId = recover.DeviceId;
                        activeDeviceType = oldType;
                        forwarder.UpdateTarget(activeBusId, activeDeviceId, activeDeviceType);
                        Logger.Info($"VIIPER hot-swap recovery: restored {oldType} device (dev={activeDeviceId})");
                    }
                    else
                    {
                        Logger.Warn($"VIIPER hot-swap recovery failed — no virtual device active.");
                        activeDeviceId = 0;
                    }
                    return;
                }
                activeDeviceId = swap.DeviceId;
                activeDeviceType = newType;
                forwarder.UpdateTarget(activeBusId, activeDeviceId, activeDeviceType);

                // If the hide mode required by the new target differs from the
                // old one (xbox360 ↔ anything else), re-apply HidHide so the
                // Legion's XInput child gets hidden/unhidden to match. Without
                // this re-apply, swapping DSE → X360 would leave the XInput
                // child hidden (and double-hide our X360 with itself); swapping
                // X360 → DSE would leave it visible (double input returns).
                int oldHideMode = ComputeHideTargetForViiper(oldType);
                int newHideMode = ComputeHideTargetForViiper(newType);
                if (oldHideMode != newHideMode && viiperOwnsSuppression)
                {
                    try
                    {
                        var suppression = legacyManager?.SuppressionManager;
                        if (suppression != null)
                        {
                            bool ok = suppression.Enable(legacyManager.HandheldDeviceType, newHideMode);
                            Logger.Info($"VIIPER: HidHide re-applied for hot-swap (target={newType}, hideMode={newHideMode}) => {ok}");
                        }
                    }
                    catch (Exception ex) { Logger.Warn($"VIIPER HidHide re-apply on hot-swap threw: {ex.Message}"); }
                }

                // RemoveDevice in SwitchDeviceType succeeded, but Windows keeps
                // the OLD target's PnP entry as Present=False — and Steam Input
                // can still see/route to it. Run a focused cleanup pass for the
                // old target's VID/PID set so Steam's controller manager doesn't
                // show or rumble-route to stale entries. Async + best-effort:
                // a slow pnputil can't block the swap or the forwarder unpause.
                ViiperPnpCleanup.CleanupGhostsForTarget(oldType);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "VIIPER hot-swap threw");
            }
            finally
            {
                forwarder.SetPaused(false);
            }
            }
            catch (Exception ex) { Logger.Error(ex, "PerformDeviceConfigSwap threw"); }
            } // _deviceSwapLock
        }

        public void Stop()
        {
            lock (startLock)
            {
                StopLocked();
            }
        }

        private void StopLocked()
        {
            if (!isRunning) return;

            try { forwarder.Stop(); }
            catch (Exception ex) { Logger.Warn($"forwarder.Stop threw: {ex.Message}"); }

            if (viiperOwnsSuppression)
            {
                try
                {
                    legacyManager?.SuppressionManager?.Disable();
                    Logger.Info("VIIPER: HidHide suppression disabled");
                }
                catch (Exception ex) { Logger.Warn($"VIIPER HidHide Disable threw: {ex.Message}"); }
                viiperOwnsSuppression = false;
            }

            // (No physical-XInput restore here: register 04/0f is now owned solely by the
            // manual "Disable OS Reporting" toggle, not by emulation start/stop.)

            // Detach our UDE imports first (we attach explicitly via usbip.exe on add — see
            // UsbipCli), then tear down the server side.
            try { UsbipCli.DetachAll(); }
            catch (Exception ex) { Logger.Warn($"usbip detach threw: {ex.Message}"); }

            try
            {
                if (activeDeviceId != 0)
                {
                    service.RemoveDevice(activeBusId, activeDeviceId);
                }
            }
            catch (Exception ex) { Logger.Warn($"RemoveDevice threw: {ex.Message}"); }

            try
            {
                if (activeSecondaryDeviceId != 0)
                {
                    service.RemoveDevice(activeBusId, activeSecondaryDeviceId);
                }
            }
            catch (Exception ex) { Logger.Warn($"RemoveDevice (secondary) threw: {ex.Message}"); }

            try { service.RemoveBus(activeBusId); }
            catch (Exception ex) { Logger.Warn($"RemoveBus threw: {ex.Message}"); }

            try { service.Dispose(); }
            catch (Exception ex) { Logger.Warn($"VIIPER Dispose threw: {ex.Message}"); }

            activeDeviceId = 0;
            activeSecondaryDeviceId = 0;
            isRunning = false;
            Logger.Info("VIIPER emulation manager stopped");

            // Emulation just went away — tell Labs to re-spin the dedicated Guide-only
            // ViGEm pad if a Guide action is still mapped.
            Program.NotifyGuideRouteChanged();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (legacyManager != null)
                {
                    legacyManager.EmulationEnabledChanged -= OnEmulationEnabledChanged;
                }
                if (settingsManager != null)
                {
                    if (settingsManager.EmulationBackend != null)
                    {
                        settingsManager.EmulationBackend.PropertyChanged -= OnBackendChanged;
                    }
                    if (settingsManager.ViiperDeviceType != null)
                    {
                        settingsManager.ViiperDeviceType.PropertyChanged -= OnDeviceConfigChanged;
                    }
                    if (settingsManager.ViiperSteamSubDevice != null)
                    {
                        settingsManager.ViiperSteamSubDevice.PropertyChanged -= OnDeviceConfigChanged;
                    }
                    if (settingsManager.ViiperSonySubDevice != null)
                    {
                        settingsManager.ViiperSonySubDevice.PropertyChanged -= OnDeviceConfigChanged;
                    }
                    if (settingsManager.ViiperNintendoSubDevice != null)
                    {
                        settingsManager.ViiperNintendoSubDevice.PropertyChanged -= OnDeviceConfigChanged;
                    }
                    if (settingsManager.ViiperInputSource != null)
                    {
                        settingsManager.ViiperInputSource.PropertyChanged -= OnInputSourceChanged;
                    }
                    if (settingsManager.ViiperGyroSource != null)
                    {
                        settingsManager.ViiperGyroSource.PropertyChanged -= OnGyroSourceChanged;
                    }
                    if (settingsManager.ViiperJoyconGyroPerHalf != null)
                    {
                        settingsManager.ViiperJoyconGyroPerHalf.PropertyChanged -= OnJoyconGyroPerHalfChanged;
                    }
                    if (settingsManager.ViiperGuideButtonMode != null)
                    {
                        settingsManager.ViiperGuideButtonMode.PropertyChanged -= OnGuideModeChanged;
                    }
                    if (settingsManager.ViiperStickTriggerConfig != null)
                    {
                        settingsManager.ViiperStickTriggerConfig.PropertyChanged -= OnStickTriggerConfigChanged;
                    }
                    if (settingsManager.ViiperStickTriggerPreviewEnabled != null)
                    {
                        settingsManager.ViiperStickTriggerPreviewEnabled.PropertyChanged -= OnStickTriggerPreviewEnabledChanged;
                    }
                    if (settingsManager.ViiperGyroTuning != null)
                    {
                        settingsManager.ViiperGyroTuning.PropertyChanged -= OnGyroTuningChanged;
                    }
                    if (settingsManager.ViiperAccelTuning != null)
                    {
                        settingsManager.ViiperAccelTuning.PropertyChanged -= OnGyroTuningChanged;
                    }
                }
                Stop();
                if (guideOnlyMode) StopGuideOnly();
                try { forwarder?.Dispose(); }
                catch (Exception ex) { Logger.Warn($"forwarder.Dispose threw: {ex.Message}"); }
                if (object.ReferenceEquals(activeInstance, this)) activeInstance = null;
            }
            base.Dispose(disposing);
        }
    }
}
