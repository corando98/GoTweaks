using System;
using System.Threading;
using NLog;
using Shared.Data;
using XboxGamingBarHelper.Devices.Libraries.Legion;
using XboxGamingBarHelper.Labs;

namespace XboxGamingBarHelper.ControllerEmulation.Viiper
{
    /// <summary>Which physical controller source drives the VIIPER forwarder.</summary>
    internal enum ViiperInputSourceKind
    {
        XInput = 0,
        LegionHid = 1,
    }

    /// <summary>How a Guide/Mode press is handled by the forwarder.</summary>
    internal enum ViiperGuideButtonMode
    {
        /// <summary>Forward to the emulated device's native Guide/PS button.</summary>
        Native = 0,
        /// <summary>Suppress the native Guide press and fire Win+G to open Xbox Game Bar.</summary>
        GameBar = 1,
    }

    /// <summary>Which IMU source (if any) feeds the gyro bytes of the VIIPER wire format.</summary>
    internal enum ViiperGyroSourceKind
    {
        None = 0,
        Left = 1,
        Right = 2,
        // Built-in handheld IMU. Currently no separate Windows sensor wiring,
        // so we treat Handheld the same as Mixed (left+right merged) which is
        // the closest analogue on Legion Go / Go 2 / Go S.
        Handheld = 3,
        // Both controllers averaged with the right side mirror-inverted so axes
        // agree before the merge. Falls back to whichever single side is
        // currently reporting if only one is available.
        Mixed = 4,
    }

    /// <summary>
    /// Bitmasks for Legion Go auxiliary buttons (Y1/Y2/Y3, M3, Mode, Share, front-top/bot).
    /// Matches LegionButtonMonitor's AuxButtons output.
    /// </summary>
    internal static class LegionAux
    {
        public const ushort Y1     = 0x0001;
        public const ushort Y2     = 0x0002;
        public const ushort Y3     = 0x0004;
        public const ushort M3     = 0x0008;
        public const ushort M1     = 0x0010;
        public const ushort M2     = 0x0020;
        public const ushort Mode   = 0x0040;
        public const ushort Share  = 0x0080;
        public const ushort FrTop  = 0x0100;
        public const ushort FrBot  = 0x0200;
    }

    /// <summary>
    /// Phase 5a: minimal XInput -> VIIPER forwarding loop.
    /// Polls XInput for a single physical controller and forwards the state to a
    /// VIIPER virtual device at ~250 Hz. Currently supports Xbox 360, DualShock 4,
    /// DualSense Edge, and Xbox Elite 2 target types. Gyro, Legion HID input,
    /// button remap, and rumble feedback come in 5b/5c/5d.
    /// </summary>
    internal sealed class ViiperInputForwarder : IDisposable
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private readonly ViiperService service;
        private readonly LegionManager legionManager;
        private Thread pollThread;
        private volatile bool running;
        private volatile bool paused;

        private uint physicalIndex;
        private uint busId;
        private uint deviceId;
        // Set when the target topology requires two libviiper devices on the
        // same bus (currently only joycon-pair: joycon-left + joycon-right).
        // When non-zero the poll loop mirrors each SetInput onto this device
        // too. libviiper's per-profile InputState parser slices the same
        // 24-byte switchpro frame to the left/right half, so a single wire
        // build serves both halves correctly.
        private volatile uint secondaryDeviceId;
        // SteamDeck wire frame field (bytes 4-7). This is Steam's gyro TIMEBASE, not a
        // sequence number: Steam integrates gyro angle as velocity * (deltaFrame * tick),
        // where tick is the advertised ConnectionIntervalUs (4000us = 4ms; see
        // device.go AttributeConnectionIntervalUs). The real Deck puts a hardware
        // microsecond-ish timestamp here. We must therefore derive the frame from REAL
        // elapsed wall-clock in 4ms units — NOT increment by 1 per report. Our Legion HID
        // delivers ~125Hz (not the Deck's ~250Hz), so a per-report ++ would advance the
        // timebase at half real-time and Steam would integrate gyro at half speed. A
        // time-based frame keeps gyro speed correct at any send rate and is robust to the
        // 124-126Hz HID jitter (that jitter, against a per-report counter, is what caused
        // the gyro-to-mouse lag/overshoot). Anchor set on first SteamDeck report.
        private long steamDeckFrameAnchorTicks;
        private bool steamDeckFrameAnchored;
        // 4ms per Deck frame tick = advertised ConnectionIntervalUs (4000us).
        private const long SteamDeckFrameTickTicks = System.TimeSpan.TicksPerMillisecond * 4;

        // Monotonic frame counter for the steamcontroller (Gordon V1) wire format.
        // Same role as steamDeckFrameCounter — Gordon uses its own per-device
        // sequence number at bytes 4-7 of the 64-byte report.
        private uint gordonFrameCounter;

        // First-sighting set of steamdeck OutputState command IDs (data[0]). Used
        // to log new command bytes once each so "rumble doesn't work" reports can
        // be triaged: if no 0x8F / 0xEA / 0xEB shows up here, Steam Input is not
        // sending rumble to the virtual device at all.
        private readonly System.Collections.Generic.HashSet<byte> seenSteamDeckCommandIds = new System.Collections.Generic.HashSet<byte>();

        // Steam sends 0x8F TriggerHapticPulse separately per motor side (Left or
        // Right), and the XInput SetState we pass to the physical pad requires
        // BOTH motor strengths in every call. Track the most-recent per-side
        // strength so a "right motor only" pulse doesn't unintentionally silence
        // the left motor (and vice-versa).
        private byte steamDeckMotorLeft;
        private byte steamDeckMotorRight;
        // Per-side expiration ticks (DateTime.UtcNow.Ticks). Steam Deck haptic
        // pulses are FINITE — each 0x8F plays for Count * (Duration + Interval)
        // microseconds and then ends naturally on the real Deck. To create
        // continuous rumble Steam re-sends pulse trains; to stop rumble Steam
        // simply stops sending them, relying on the train's natural end.
        // XInput motors don't auto-stop, so without an explicit SetState(0,0)
        // the physical controller would buzz forever at the last commanded
        // strength. The poll loop checks these expirations every tick and
        // forces a zero SetState when they pass. Value 0 = no pending pulse.
        private long steamDeckMotorLeftExpiresTicks;
        private long steamDeckMotorRightExpiresTicks;
        // Minimum on-time (µs) for a Steam Deck haptic pulse. Steam's trigger click is
        // ~400µs — far shorter than the LRA's spin-up-from-rest time, so short floors produced
        // weak/inconsistent clicks (felt as "missed"): same-side pulses arrive 80-180ms apart,
        // so each starts from a fully-stopped motor and needs enough drive to reach perceptible
        // amplitude before the decay brakes it. Measured: 12µs floor -> ~12-21ms on-time felt
        // missed; 40ms floor merged dense bursts into sustained buzz. 20ms is the midpoint —
        // reliable LRA spin-up for a uniform click, still short enough that bursts stay crisp.
        private const long SteamDeckHapticMinPlayUs = 20000;

        // Same auto-decay machinery for the Switch 2 Pro (ns2pro) target.
        // Steam Input's Switch 2 Pro driver doesn't currently send an explicit
        // "stop rumble" subcommand — it relies on the LRA envelope baked into
        // the HD haptic pattern to fade out naturally, and on its built-in
        // 5-second test-rumble timeout to send a stop after that window. Our
        // virtual device doesn't simulate the LRA envelope; it just exposes
        // peak amplitude. Without a decay timer the motor latches at peak for
        // the full 5 s of Steam's hold, instead of the ~100 ms "ping" the
        // user actually expects. Same poll-loop tick that drives the Steam
        // Deck decay handles ns2pro decay too.
        private byte ns2proMotorLeft;
        private byte ns2proMotorRight;
        private long ns2proMotorLeftExpiresTicks;
        private long ns2proMotorRightExpiresTicks;

        // Throttle LED writes — Legion's WMI/USB write path is slow; don't re-send the same color.
        private long lastLedPacked = -1;
        private long lastLedWriteTicks;
        private const long LedWriteMinIntervalTicks = TimeSpan.TicksPerSecond / 8; // 125 ms
        private string targetType = "xbox360";
        private volatile ViiperInputSourceKind inputSource = ViiperInputSourceKind.XInput;
        private volatile ViiperGyroSourceKind gyroSource = ViiperGyroSourceKind.None;
        // joycon-pair only: when true, the left Joy-Con half is driven by the left
        // controller IMU and the right half by the right controller IMU. When false,
        // both halves share `gyroSource`.
        private volatile bool joyconGyroPerHalf;

        // SteamDeck gyro pacing: Steam Input integrates gyro velocity over the report frame
        // counter (bytes 4-7), so that field is Steam's timebase. We advance it once per report
        // sent, and we send one report per FRESH Legion HID sample (~125Hz HQ gyro). An earlier
        // fixed 250Hz steady-send (zero-order hold on stale samples) advanced the timebase on
        // duplicate frames, making Steam under-integrate then overshoot — visible as gyro
        // lag-then-snap. HHD sends one report per IMU timestamp for the same reason. 1ms system
        // timer resolution is still raised below so the loop's short waits/sleeps stay accurate.
        private bool _timerResolutionRaised;
        // Legion BMI accel is +/-8g (4096 counts/g); the Steam Deck wire format is +/-2g
        // (16384 counts/g). Scale accel up by this factor so the gravity vector reaches full
        // strength for Steam's gyro sensor fusion. See BuildSteamDeckInput for the derivation.
        private const int SteamDeckAccelScale = 4;
        [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        private static extern uint NativeTimeBeginPeriod(uint uMilliseconds);
        [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
        private static extern uint NativeTimeEndPeriod(uint uMilliseconds);
        private volatile ViiperGuideButtonMode guideMode = ViiperGuideButtonMode.Native;
        private volatile bool swapRumbleMotors;
        // Percent ×10 so we can apply it with integer math (1000 == unity). Range 0..2000.
        private volatile int rumbleIntensityScaled = 1000;
        // When false, suppress mirroring the emulated lightbar to the Legion stick lights
        // so the user's saved color stays untouched. Default false — the toggle has to be
        // explicitly enabled. Prior behavior (always-on) caused users to lose their picker
        // color the moment VIIPER asserted the DS Edge idle blue.
        private volatile bool mirrorLightbar;

        // IMU axis remap: each output axis reads source[src] × sign. Packed (src, sign) per
        // axis. Identity: X→(0,+1), Y→(1,+1), Z→(2,+1). Accel uses the same map as gyro
        // (the reference app keeps them locked together; only the 3 gyro selectors are
        // exposed in the UI).
        private volatile int axisMapXSrc = 0, axisMapXSign = 1;
        private volatile int axisMapYSrc = 1, axisMapYSign = 1;
        private volatile int axisMapZSrc = 2, axisMapZSign = 1;

        // Synthesizes a right-stick override from gyro for target types whose wire
        // format has no native motion field (xbox360, xboxelite2, switchpro family).
        // For DS4 / DSE the wire format already carries IMU bytes via TryBuildImuCounts,
        // so this processor stays dormant on those targets.
        private readonly ViiperStickGyroProcessor stickGyro = new ViiperStickGyroProcessor();

        // Per-stick / per-trigger shaping bundle: deadzone shapes, anti-deadzones,
        // sensitivity curves. Default is identity (passthrough); the manager pushes
        // updates via SetStickTriggerConfig when the user changes settings. Read by
        // ApplyStickTriggerShaping right before BuildDeviceInput, so transformations
        // affect every wire format consistently.
        private StickTriggerConfigBundle stickTriggerConfig = StickTriggerConfigBundle.Default;
        private readonly object stickTriggerConfigLock = new object();
        private bool stickTriggerConfigActive;  // fast bypass when bundle == defaults

        // Live-preview channel for the widget's Sticks & Triggers canvas. Sink
        // is wired once at manager init; the volatile bool flips when the
        // panel is expanded/collapsed. ApplyStickTriggerShaping reads the raw
        // sample before transforming it, and pushes one frame every ~33ms so
        // the widget can render both raw and shaped indicators at ~30Hz.
        // Throttled at the source (not on the pipe consumer) to keep IPC churn
        // bounded even when the controller is being moved rapidly.
        private volatile bool stickTriggerPreviewEnabled;
        private ViiperStickTriggerLiveSampleProperty stickTriggerLiveSink;
        private int stickTriggerLastPreviewTickMs;
        private const int StickTriggerPreviewIntervalMs = 33;

        // Edge-detection state for the Guide/Mode -> Win+G shortcut. We only fire the
        // shortcut on press-transition, not on every poll while the button is held.
        private bool guideWasPressed;
        private long lastGuideShortcutTicks;
        private const long GuideShortcutMinIntervalTicks = TimeSpan.TicksPerSecond;

        // When a Labs action (e.g. Legion L -> XboxGuide) intercepts a press that in Native
        // mode should become the emulated device's Guide/PS button, we track the press
        // state explicitly. The wire builders OR Guide into the buttons while the physical
        // button is held. A short minimum-hold covers press/release callbacks that don't
        // fire in strict order (e.g. ultra-short taps).
        private bool labsGuideHeld;
        private long labsGuideMinReleaseTicks;
        private const int LabsGuideMinHoldMilliseconds = 60;

        // Singleton so Labs-side code can reach the active forwarder without circular DI.
        private static ViiperInputForwarder activeInstance;
        internal static ViiperInputForwarder ActiveInstance => activeInstance;
        internal ViiperStickGyroProcessor StickGyroProcessor => stickGyro;

        // Rumble feedback counters. Incremented on the libviiper callback thread inside
        // OnFeedbackReceived; drained and logged from PollLoop's 5s stats window. Used
        // to triage "rumble silent" reports — distinguishes (a) game never sent rumble
        // events, (b) events arrived but ViiperXInput.SetState rejected them, (c) events
        // forwarded fine but the user can't feel them (then look at where physicalIndex
        // resolved to in DetectPhysicalXInputIndex's log line).
        private int statsRumbleEventsReceived;
        private int statsRumbleForwardedOk;
        private int statsRumbleForwardedErr;

        // Latest aux buttons sampled from Legion HID this cycle (0 when the active source
        // doesn't expose them, e.g. plain XInput). Read by the DS4/DSE wire builders to map
        // Legion Y1/Y2/Y3/M3/Mode/Share onto the virtual device's extended button bits.
        private ushort currentAuxButtons;

        // Latest touchpad sample from Legion HID, written into the DS4/DSE wire format.
        private bool currentTouchActive;
        private bool currentTouchPressed;
        private ushort currentTouchX;
        private ushort currentTouchY;

        // IMU axis counts/second and counts/G for Legion Go BMI323:
        //   gyro: 16 counts per deg/sec
        //   accel: 4096 counts per G (BMI323 ±8g)
        // DS4 host apps expect 8192 counts/g on accel → multiply by 2.
        private const float GyroDpsToRawCounts = 16.0f;
        private const float AccelGToRawCounts = 4096.0f;

        // Rumble delivery — uses XInputSetState via xinput1_4.dll, which on
        // the Legion ends up at xusb22.sys (bound to MI_00). The helper is on
        // HidHide's per-application allowlist, so even with MI_00 in the
        // suppression list xinput1_4's internal opens succeed for us. Mode 1
        // keeps the XInput child enumerable; mode 3 broke this path (rumble
        // would route via the now-hidden 045E:028E sibling and fail). The
        // duplicate "Controller (Xbox 360 for Windows)" entry users see in
        // joy.cpl is the cost of keeping the rumble channel open.

        public ViiperInputForwarder(ViiperService inService, LegionManager inLegionManager)
        {
            service = inService;
            legionManager = inLegionManager;
            if (service != null)
            {
                service.FeedbackReceived += OnFeedbackReceived;
            }
        }

        /// <summary>
        /// Called by the native thread when the virtual device receives a rumble/LED report
        /// from the consuming application. We parse the relevant motor bytes based on the
        /// current device type and forward them to the physical XInput controller.
        /// </summary>
        /// <summary>
        /// Zero out per-side steamdeck motor strengths whose pulse-train timer has
        /// elapsed. Returns true if any side just decayed from non-zero to zero,
        /// which the caller can use to know it needs to flush a SetState(0,0) to
        /// the physical pad (XInput motors don't auto-stop).
        /// </summary>
        private bool DecayExpiredSteamDeckRumble()
        {
            long now = DateTime.UtcNow.Ticks;
            bool changed = false;
            if (steamDeckMotorLeft != 0 && now >= steamDeckMotorLeftExpiresTicks)
            {
                steamDeckMotorLeft = 0;
                steamDeckMotorLeftExpiresTicks = 0;
                changed = true;
            }
            if (steamDeckMotorRight != 0 && now >= steamDeckMotorRightExpiresTicks)
            {
                steamDeckMotorRight = 0;
                steamDeckMotorRightExpiresTicks = 0;
                changed = true;
            }
            return changed;
        }

        /// <summary>
        /// Same idea as <see cref="DecayExpiredSteamDeckRumble"/> but for the
        /// ns2pro motor state — zeroes per-side strength when the rumble's
        /// natural play window has elapsed. Returns true if anything decayed
        /// so the caller can push a SetState(0,0) to the physical pad.
        /// </summary>
        private bool DecayExpiredNS2ProRumble()
        {
            long now = DateTime.UtcNow.Ticks;
            bool changed = false;
            if (ns2proMotorLeft != 0 && now >= ns2proMotorLeftExpiresTicks)
            {
                ns2proMotorLeft = 0;
                ns2proMotorLeftExpiresTicks = 0;
                changed = true;
            }
            if (ns2proMotorRight != 0 && now >= ns2proMotorRightExpiresTicks)
            {
                ns2proMotorRight = 0;
                ns2proMotorRightExpiresTicks = 0;
                changed = true;
            }
            return changed;
        }

        private void FlushNS2ProRumbleToXInput()
        {
            try
            {
                byte large = ns2proMotorLeft;
                byte small = ns2proMotorRight;
                if (swapRumbleMotors)
                {
                    byte tmp = large; large = small; small = tmp;
                }
                int scaled = rumbleIntensityScaled;
                int leftSpeed = (large * 257 * scaled) / 1000;
                int rightSpeed = (small * 257 * scaled) / 1000;
                if (leftSpeed > 65535) leftSpeed = 65535;
                if (rightSpeed > 65535) rightSpeed = 65535;
                var vib = new ViiperXInputVibration
                {
                    LeftMotorSpeed = (ushort)leftSpeed,
                    RightMotorSpeed = (ushort)rightSpeed,
                };
                ViiperXInput.SetState(physicalIndex, ref vib);
            }
            catch (Exception ex) { Logger.Warn($"FlushNS2ProRumbleToXInput threw: {ex.Message}"); }
        }

        /// <summary>
        /// Pushes the current per-side steamdeck motor strengths to the physical
        /// XInput controller. Mirrors the rumble-emit code path in
        /// OnFeedbackReceived so the poll-loop decay timer can stop a rumble
        /// without piggy-backing on an incoming feedback event.
        /// </summary>
        private void FlushSteamDeckRumbleToXInput()
        {
            try
            {
                byte large = steamDeckMotorLeft;
                byte small = steamDeckMotorRight;
                if (swapRumbleMotors)
                {
                    byte tmp = large; large = small; small = tmp;
                }
                int scaled = rumbleIntensityScaled;
                int leftSpeed = (large * 257 * scaled) / 1000;
                int rightSpeed = (small * 257 * scaled) / 1000;
                if (leftSpeed > 65535) leftSpeed = 65535;
                if (rightSpeed > 65535) rightSpeed = 65535;
                var vib = new ViiperXInputVibration
                {
                    LeftMotorSpeed = (ushort)leftSpeed,
                    RightMotorSpeed = (ushort)rightSpeed,
                };
                ViiperXInput.SetState(physicalIndex, ref vib);
            }
            catch (Exception ex)
            {
                Logger.Debug($"Steamdeck rumble decay XInput SetState failed: {ex.Message}");
            }
        }

        private bool IsSteamDeckLikeTarget()
        {
            switch (targetType)
            {
                case "steamdeck":
                case "steam-deck":
                case "steamdeck-generic":
                case "steam-generic":
                case "steam-controller":
                case "steam-controller-v1":
                case "steamcontroller-v1":
                case "gordon":
                case "legion-go":
                case "legion-go-s":
                case "legion-go-2":
                case "msi-claw":
                case "rog-ally":
                case "zotac-zone":
                    return true;
                default:
                    return false;
            }
        }

        private void LogSteamDeckCommandIdOnce(byte[] data)
        {
            if (data == null || data.Length == 0) return;
            byte id = data[0];
            bool isNew;
            lock (seenSteamDeckCommandIds)
            {
                isNew = seenSteamDeckCommandIds.Add(id);
            }
            if (!isNew) return;
            string name;
            switch (id)
            {
                case 0x80: name = "SetDigitalMappings"; break;
                case 0x81: name = "ClearDigitalMappings"; break;
                case 0x83: name = "GetAttributesValues"; break;
                case 0x87: name = "SetSettingsValues"; break;
                case 0x88: name = "ClearSettingsValues"; break;
                case 0x8D: name = "SetControllerMode"; break;
                case 0x8F: name = "TriggerHapticPulse"; break;
                case 0xA1: name = "GetDeviceInfo"; break;
                case 0xCE: name = "ResetIMU"; break;
                case 0xEA: name = "TriggerHapticCommand (LRA rumble)"; break;
                case 0xEB: name = "TriggerRumbleCommand (motor rumble)"; break;
                default:   name = "(unknown)"; break;
            }
            Logger.Info($"VIIPER steamdeck OutputState first sighting: cmd=0x{id:X2} ({name}) len={data.Length}");
        }

        private void OnFeedbackReceived(uint cbBusId, uint cbDeviceId, byte[] data)
        {
            if (!running || data == null || data.Length == 0) return;
            // Ignore late events from a hot-swapped-out device.
            if (cbBusId != busId || cbDeviceId != deviceId) return;
            System.Threading.Interlocked.Increment(ref statsRumbleEventsReceived);

            byte rumbleLarge = 0;
            byte rumbleSmall = 0;
            bool haveLed = false;
            byte ledR = 0, ledG = 0, ledB = 0;
            switch (targetType)
            {
                case "xbox360":
                    if (data.Length >= 2) { rumbleLarge = data[0]; rumbleSmall = data[1]; }
                    break;
                case "dualshock4":
                    // DS4 report: data[0]=rumbleSmall, data[1]=rumbleLarge, data[2..4]=LED RGB,
                    // data[5]=flashOn, data[6]=flashOff.
                    if (data.Length >= 2) { rumbleSmall = data[0]; rumbleLarge = data[1]; }
                    if (data.Length >= 5) { haveLed = true; ledR = data[2]; ledG = data[3]; ledB = data[4]; }
                    break;
                case "dualsenseedge":
                case "dualsense-edge":
                case "dualsense":
                case "ds5":
                    // DSE/DS5 report: data[0]=rumbleSmall, data[1]=rumbleLarge, data[2..4]=LED RGB,
                    // data[5]=playerLeds (DSE only — non-Edge stops at 5 bytes).
                    if (data.Length >= 2) { rumbleSmall = data[0]; rumbleLarge = data[1]; }
                    if (data.Length >= 5) { haveLed = true; ledR = data[2]; ledG = data[3]; ledB = data[4]; }
                    break;
                case "xboxelite2":
                case "xbox-one":
                case "xbox-elite":
                    if (data.Length >= 2) { rumbleLarge = data[0]; rumbleSmall = data[1]; }
                    break;
                // libviiper 32c8ed3 + clib rework — steam-handheld targets now use
                // the steamdeck OutputState container (64-byte command framing).
                // Multiple command types are multiplexed on data[0]. In the wild
                // Steam Input emits ONLY 0x8F TriggerHapticPulse for game rumble
                // (verified from helper_2026-05-17_20.log first-sighting trace);
                // 0xEA/0xEB are spec'd but unused on current Steam Deck virtual
                // device. The 0x8F handler is primary, 0xEA/0xEB kept for safety.
                //
                // 0x8F TriggerHapticPulse layout (per device/steamdeck/inputstate.go
                // AsHapticPulse):
                //   [0] = 0x8F
                //   [2] = Side (0=Left, 1=Right, 2=Both)
                //   [3..5] = Duration µs (u16 LE)   — pulse ON time
                //   [5..7] = Interval µs (u16 LE)   — pulse OFF time
                //   [7..9] = Count (u16 LE)         — number of pulses (or 0xFFFF/0 = continuous)
                //   [9]    = Gain (int8 dB)         — amplitude (0 = full)
                //
                // Steam Input emits one 0x8F per motor side independently, so we
                // must remember the OTHER motor's most-recent strength across
                // commands. Otherwise a "right motor only" pulse would arrive
                // with the left motor implicitly silenced at the bottom of this
                // method (XInput SetState needs BOTH motor speeds).
                //
                // Rumble strength heuristic: duty cycle of the pulse train
                // (Duration / (Duration + Interval)) scaled to 0..255. Steam's
                // typical continuous-rumble emission has Duration ≈ 600µs and
                // Interval ≈ 400µs (duty ~60%) at full strength, with Count set
                // to a large value. A "stop" command has Count == 0 OR Duration
                // == 0. Gain in dB scales further: each -6 dB ≈ halve strength.
                case "steamdeck":
                case "steam-deck":
                case "steamdeck-generic":
                case "steam-generic":
                case "steam-controller":
                case "steam-controller-v1":
                case "steamcontroller-v1":
                case "gordon":
                case "legion-go":
                case "legion-go-s":
                case "legion-go-2":
                case "msi-claw":
                case "rog-ally":
                case "zotac-zone":
                    LogSteamDeckCommandIdOnce(data);
                    if (data.Length >= 10 && data[0] == 0x8F)
                    {
                        byte side = data[2];
                        ushort duration = (ushort)(data[3] | (data[4] << 8));
                        ushort interval = (ushort)(data[5] | (data[6] << 8));
                        ushort count    = (ushort)(data[7] | (data[8] << 8));
                        sbyte gainDb    = (sbyte)data[9];

                        byte strength;
                        if (count == 0 || duration == 0)
                        {
                            strength = 0;
                        }
                        else
                        {
                            int total = duration + interval;
                            int dutyScaled = (total > 0) ? (duration * 255) / total : 0;
                            // Gain attenuation — each -6 dB drop halves amplitude.
                            // Positive gain (rare from Steam) treated as 0 dB cap.
                            if (gainDb < 0)
                            {
                                int dropSteps = (-gainDb + 3) / 6;
                                if (dropSteps > 8) dropSteps = 8;
                                dutyScaled >>= dropSteps;
                            }
                            if (dutyScaled < 0) dutyScaled = 0;
                            if (dutyScaled > 255) dutyScaled = 255;
                            strength = (byte)dutyScaled;
                        }

                        // Compute auto-stop time. Pulse train plays for
                        // Count * (Duration + Interval) microseconds; convert
                        // to .NET ticks (1µs = 10 ticks). PollLoop zeros the
                        // motor + sends XInput SetState(0,0) once we pass it.
                        // Steam's trigger haptic is a single ~0.4ms click (dur=400us, cnt=1).
                        // That is far shorter than both our ~8ms poll-decay tick AND the LRA's
                        // physical spin-up time, so the motor was often zeroed before (or on)
                        // the same tick it was set — felt as "sometimes no buzz" on rapid
                        // pulls. Floor the on-time to SteamDeckHapticMinPlayUs so every pulse
                        // survives a few ticks and the LRA actually registers the click.
                        long totalPlayUs = (long)count * ((long)duration + interval);
                        if (totalPlayUs < SteamDeckHapticMinPlayUs) totalPlayUs = SteamDeckHapticMinPlayUs;
                        long stopTicks = DateTime.UtcNow.Ticks + totalPlayUs * 10;
                        if (side == 0)      { steamDeckMotorLeft  = strength; steamDeckMotorLeftExpiresTicks  = stopTicks; }
                        else if (side == 1) { steamDeckMotorRight = strength; steamDeckMotorRightExpiresTicks = stopTicks; }
                        else                { steamDeckMotorLeft  = strength; steamDeckMotorLeftExpiresTicks  = stopTicks;
                                              steamDeckMotorRight = strength; steamDeckMotorRightExpiresTicks = stopTicks; }
                    }
                    else if (data.Length >= 9 && data[0] == 0xEB)
                    {
                        // FeatureTriggerRumbleCommand — direct motor speeds.
                        // No natural end; keep the strength latched until
                        // Steam sends another 0xEB to update it.
                        ushort steamLeft  = (ushort)(data[5] | (data[6] << 8));
                        ushort steamRight = (ushort)(data[7] | (data[8] << 8));
                        steamDeckMotorLeft  = (byte)(steamLeft  / 257);
                        steamDeckMotorRight = (byte)(steamRight / 257);
                        steamDeckMotorLeftExpiresTicks  = long.MaxValue;
                        steamDeckMotorRightExpiresTicks = long.MaxValue;
                    }
                    else if (data.Length >= 5 && data[0] == 0xEA)
                    {
                        // FeatureTriggerHapticCommand — newer command class,
                        // intensity ladder 0..4. Kept as a fallback channel.
                        // Treat as latched (no natural end) like 0xEB.
                        byte side = data[2];
                        byte hapticType = data[3];
                        byte intensity = data[4];
                        byte strength = (hapticType == 0) ? (byte)0 :
                            (intensity >= 4) ? (byte)255 : (byte)(intensity * 64);
                        if (side == 0)      { steamDeckMotorLeft  = strength; steamDeckMotorLeftExpiresTicks  = long.MaxValue; }
                        else if (side == 1) { steamDeckMotorRight = strength; steamDeckMotorRightExpiresTicks = long.MaxValue; }
                        else                { steamDeckMotorLeft  = strength; steamDeckMotorLeftExpiresTicks  = long.MaxValue;
                                              steamDeckMotorRight = strength; steamDeckMotorRightExpiresTicks = long.MaxValue; }
                    }
                    // Decay any per-side strength whose pulse train has elapsed
                    // since the last command arrived — Steam often coalesces a
                    // burst of pulses then goes silent expecting natural decay.
                    DecayExpiredSteamDeckRumble();
                    // Always populate rumbleLarge/Small from persistent per-side
                    // state — even on a command we don't recognize, the existing
                    // motor state must reach the XInput SetState below or the
                    // physical pad will receive (0, 0) and stop vibrating.
                    rumbleLarge = steamDeckMotorLeft;
                    rumbleSmall = steamDeckMotorRight;
                    break;
                case "switchpro":
                case "joycon-left":
                case "joycon-right":
                case "joycon-pair":
                    if (data.Length >= 2) { rumbleLarge = data[0]; rumbleSmall = data[1]; }
                    break;
                case "ns2pro":
                    // ns2pro OUT reports arrive in one of two shapes:
                    //
                    //   (a) Switch 2 Pro native HD-LRA pattern — 32 bytes,
                    //       two 16-byte amplitude windows (left then right).
                    //       Peak-of-window is the best ERM proxy because
                    //       click/heartbeat patterns sandwich the peak
                    //       between low samples. Pattern auto-decays in
                    //       ~16 ms on real hardware (16 samples × 1 ms).
                    //
                    //   (b) Switch 1 Pro rumble subcommand fallback —
                    //       Steam Input doesn't ship an HD-LRA driver for
                    //       Switch 2 Pro yet (2026-05), so when it sees our
                    //       057E:2069 it sends the legacy 0x10/0x11
                    //       subcommand: `cmd counter L_HFh L_HFa L_LFh
                    //       L_LFa R_HFh R_HFa R_LFh R_LFa ...`. Real Switch
                    //       firmware plays this for ~100 ms then auto-stops.
                    //
                    // Either way our virtual device doesn't simulate the
                    // natural decay — without a timer the XInput motor
                    // latches at peak for as long as Steam's hold window
                    // (5 s on the test-rumble button). Stash per-side
                    // strength + an expiry tick; the poll loop's decay path
                    // zeroes them and pushes SetState(0,0) when the window
                    // elapses, matching the real-hardware "ping" feel.
                    byte ns2L = 0, ns2R = 0;
                    long ns2DurationMs;
                    if (data.Length >= 10 && (data[0] == 0x10 || data[0] == 0x11))
                    {
                        // Switch 1 Pro Rumble-only / Rumble+subcommand.
                        // High-frequency amplitudes sit at offsets 3 (left)
                        // and 7 (right); low-frequency at 5 / 9.
                        if (data[3] > ns2L) ns2L = data[3];
                        if (data[5] > ns2L) ns2L = data[5];
                        if (data[7] > ns2R) ns2R = data[7];
                        if (data[9] > ns2R) ns2R = data[9];
                        ns2DurationMs = 120;
                    }
                    else if (data.Length >= 32)
                    {
                        for (int i = 0; i < 16; i++)
                        {
                            if (data[i] > ns2L) ns2L = data[i];
                            if (data[16 + i] > ns2R) ns2R = data[16 + i];
                        }
                        ns2DurationMs = 32;
                    }
                    else
                    {
                        ns2DurationMs = 0;
                    }

                    long ns2StopTicks = DateTime.UtcNow.Ticks + ns2DurationMs * TimeSpan.TicksPerMillisecond;
                    // Only update the side if the new pulse is non-zero;
                    // zero amplitude from Steam means "don't change this
                    // side" rather than "stop now" — Steam relies on
                    // expiry, not explicit zeros, for ns2pro.
                    if (ns2L != 0) { ns2proMotorLeft = ns2L; ns2proMotorLeftExpiresTicks = ns2StopTicks; }
                    if (ns2R != 0) { ns2proMotorRight = ns2R; ns2proMotorRightExpiresTicks = ns2StopTicks; }
                    rumbleLarge = ns2proMotorLeft;
                    rumbleSmall = ns2proMotorRight;
                    break;
                default:
                    return;
            }

            // Always forward rumble to the physical XInput controller. The user's pad is
            // almost always XInput-visible (Legion Go controllers expose XInput alongside
            // their native HID), so rumble should reach the hardware regardless of which
            // input source we're READING gamepad state from. Previously this was gated on
            // inputSource == XInput, which meant Legion-HID users got no rumble at all.
            try
            {
                // Apply user-configurable swap and intensity multiplier. Swap first so
                // intensity scales whichever motor ends up on each side.
                byte large = rumbleLarge;
                byte small = rumbleSmall;
                if (swapRumbleMotors)
                {
                    byte tmp = large; large = small; small = tmp;
                }
                int scaled = rumbleIntensityScaled; // snapshot to avoid volatile read race
                int leftSpeed = (large * 257 * scaled) / 1000;
                int rightSpeed = (small * 257 * scaled) / 1000;
                if (leftSpeed > 65535) leftSpeed = 65535;
                if (rightSpeed > 65535) rightSpeed = 65535;
                var vib = new ViiperXInputVibration
                {
                    LeftMotorSpeed = (ushort)leftSpeed,
                    RightMotorSpeed = (ushort)rightSpeed,
                };
                uint rc = ViiperXInput.SetState(physicalIndex, ref vib);
                if (rc == ViiperXInput.ErrorSuccess)
                {
                    System.Threading.Interlocked.Increment(ref statsRumbleForwardedOk);
                }
                else
                {
                    System.Threading.Interlocked.Increment(ref statsRumbleForwardedErr);
                }
            }
            catch (Exception ex)
            {
                System.Threading.Interlocked.Increment(ref statsRumbleForwardedErr);
                Logger.Debug($"XInput SetState rumble failed: {ex.Message}");
            }

            // LED color forwarding — the emulated device's RGB lightbar is pushed to the
            // Legion Go stick lights. Throttle: skip when unchanged and rate-limit writes.
            //
            // Suppress the initial (0,0,0) state: most games default the emulated DS/DS Edge
            // lightbar to black until they explicitly assert a color, and forwarding (0,0,0)
            // at startup overwrites the user's saved Legion Go stick color (we observed this
            // at 16:40:37 in the helper logs — VIIPER started, immediately wrote
            // SetLightColor("#000000") with no game asserting anything). Once the game
            // sends ANY non-zero color, start forwarding everything — including subsequent
            // explicit (0,0,0) "off" requests.
            if (haveLed && legionManager != null && mirrorLightbar)
            {
                long packed = ((long)ledR << 16) | ((long)ledG << 8) | ledB;
                long now = DateTime.UtcNow.Ticks;
                if (packed != lastLedPacked && (now - lastLedWriteTicks) >= LedWriteMinIntervalTicks)
                {
                    if (lastLedPacked < 0 && packed == 0)
                    {
                        // First observation is black — treat as "no game assertion yet" and
                        // record without forwarding so the user's stick color stays put.
                        lastLedPacked = 0;
                    }
                    else
                    {
                        lastLedPacked = packed;
                        lastLedWriteTicks = now;
                        try
                        {
                            string hex = string.Format("#{0:X2}{1:X2}{2:X2}", ledR, ledG, ledB);
                            legionManager.SetLightColor(hex);
                        }
                        catch (Exception ex) { Logger.Debug($"Legion SetLightColor failed: {ex.Message}"); }
                    }
                }
            }
        }

        /// <summary>Discover which XInput index (0-3) has a connected physical controller.</summary>
        public static uint DetectPhysicalXInputIndex()
        {
            // Log every slot's state, not just the first hit. When a user reports
            // "no input after toggle" or "rumble silent" we need to know whether
            // (a) the helper's XInput sees the physical Legion at all (HidHide
            //     allowlist working), and
            // (b) the picked slot is the physical or VIIPER's virtual device.
            // Without this, debug requires an OS-level XInput probe outside the app.
            var state = new ViiperXInputState();
            uint pick = 0;
            bool picked = false;
            var connected = new System.Collections.Generic.List<uint>();
            for (uint i = 0; i < 4; i++)
            {
                if (ViiperXInput.GetState(i, ref state) == ViiperXInput.ErrorSuccess)
                {
                    connected.Add(i);
                    if (!picked) { pick = i; picked = true; }
                }
            }
            string slots = connected.Count == 0 ? "(none)" : string.Join(",", connected);
            Logger.Info($"DetectPhysicalXInputIndex: connectedSlots=[{slots}], picked={(picked ? pick.ToString() : "0 (default — none connected)")}");
            return picked ? pick : 0u;
        }

        public void Start(uint inPhysicalIndex, uint inBusId, uint inDeviceId, string inTargetType)
        {
            if (running) return;

            physicalIndex = inPhysicalIndex;
            busId = inBusId;
            deviceId = inDeviceId;
            targetType = string.IsNullOrEmpty(inTargetType) ? "xbox360" : inTargetType;

            // Clear any stale filter / toggle state from a previous Start cycle.
            // Without this, a backend swap (e.g. xbox360 → DS4 → xbox360 again)
            // would carry over an old filter value and produce a sudden jump on
            // the first poll under the new device.
            stickGyro.Reset();

            activeInstance = this;
            running = true;
            pollThread = new Thread(PollLoop)
            {
                IsBackground = true,
                Name = "ViiperInputForwarder",
                Priority = ThreadPriority.AboveNormal,
            };
            pollThread.Start();
            Logger.Info($"VIIPER forwarder started (xinput={physicalIndex}, bus={busId}, dev={deviceId}, type={targetType})");
        }

        public void UpdateTarget(uint newBusId, uint newDeviceId, string newTypeName)
        {
            busId = newBusId;
            deviceId = newDeviceId;
            targetType = string.IsNullOrEmpty(newTypeName) ? "xbox360" : newTypeName;
            Logger.Info($"VIIPER forwarder target updated: bus={busId}, dev={deviceId}, type={targetType}");
        }

        /// <summary>
        /// Sets the secondary libviiper device ID that input frames should be
        /// mirrored to. Pass 0 to disable mirroring. Called by
        /// <see cref="ViiperEmulationManager"/> after a joycon-pair Start
        /// (both Joy-Con halves get the same 24-byte switchpro frame) and on
        /// any topology change.
        /// </summary>
        public void SetSecondaryDevice(uint deviceId)
        {
            secondaryDeviceId = deviceId;
            Logger.Info($"VIIPER forwarder secondary device set to {deviceId} (0 = none)");
        }

        /// <summary>
        /// Updates the XInput user index the forwarder reads from and writes rumble to.
        /// Used post-Start by ViiperEmulationManager to re-pin to the physical Legion's
        /// slot once Labs/LegionButtonMonitor has disposed its dedicated Guide-only
        /// ViGEm pad — that disposal can only happen after Start sets running=true
        /// (so CanHandleExternalGuide flips true), so the index detected pre-Start
        /// often points at the about-to-be-disposed Labs pad and goes ERROR_DEVICE_NOT_
        /// CONNECTED a few hundred ms later.
        /// </summary>
        public void UpdatePhysicalIndex(uint newPhysicalIndex)
        {
            uint old = physicalIndex;
            physicalIndex = newPhysicalIndex;
            Logger.Info($"VIIPER forwarder physicalIndex updated: {old} -> {newPhysicalIndex}");
        }

        /// <summary>
        /// Gates the poll loop from pushing input while a hot-swap is in flight. Between
        /// RemoveDevice() and AddDevice() (which can take ~2 seconds on the USBIP side)
        /// the virtual device doesn't exist, so continuing to send packets floods the
        /// log with "invalid input size" warnings and wastes CPU. Also kicks in when the
        /// new device's wire format differs from the old one so we never deliver a
        /// wrong-format packet to the new device before UpdateTarget switches builders.
        /// </summary>
        public void SetPaused(bool paused)
        {
            if (this.paused == paused) return;
            this.paused = paused;
            Logger.Info($"VIIPER forwarder paused -> {paused}");
        }

        public void SetInputSource(ViiperInputSourceKind kind)
        {
            if (inputSource == kind) return;
            inputSource = kind;
            Logger.Info($"VIIPER forwarder input source -> {kind}");
        }

        public void SetGyroSource(ViiperGyroSourceKind kind)
        {
            if (gyroSource == kind) return;
            gyroSource = kind;
            Logger.Info($"VIIPER forwarder gyro source -> {kind}");
        }

        public void SetJoyconGyroPerHalf(bool enabled)
        {
            if (joyconGyroPerHalf == enabled) return;
            joyconGyroPerHalf = enabled;
            Logger.Info($"VIIPER forwarder joycon-pair per-half gyro -> {enabled}");
        }

        /// <summary>
        /// Sends a built frame to the primary device and mirrors it to the secondary.
        /// For a joycon-pair in per-half gyro mode, the primary (joycon-left) gets the
        /// LEFT controller's IMU and the secondary (joycon-right) gets the RIGHT, by
        /// overwriting the switchpro-layout IMU bytes in a per-device copy. Otherwise both
        /// devices receive the same frame. Returns the primary SetInput result.
        /// </summary>
        private bool EmitDeviceFrame(byte[] data)
        {
            uint sec = secondaryDeviceId;
            if (joyconGyroPerHalf && targetType == "joycon-pair" && sec != 0
                && data != null && data.Length >= 24)
            {
                byte[] left = (byte[])data.Clone();
                OverwriteSwitchproImu(left, ViiperGyroSourceKind.Left);
                bool ok = service.SetInput(busId, deviceId, left);

                byte[] right = (byte[])data.Clone();
                OverwriteSwitchproImu(right, ViiperGyroSourceKind.Right);
                service.SetInput(busId, sec, right);
                return ok;
            }

            bool primaryOk = service.SetInput(busId, deviceId, data);
            if (sec != 0) service.SetInput(busId, sec, data);
            return primaryOk;
        }

        /// <summary>
        /// Overwrites the switchpro-layout IMU bytes (gyro-first 12-17, accel 18-23) of an
        /// already-built 24-byte Joy-Con frame with the counts from a specific source.
        /// </summary>
        private void OverwriteSwitchproImu(byte[] frame, ViiperGyroSourceKind src)
        {
            if (frame == null || frame.Length < 24) return;
            if (TryBuildImuCounts(src, out short gx, out short gy, out short gz,
                                  out short ax, out short ay, out short az))
            {
                // Right Joy-Con frame = left rotated 180° about the yaw (vertical) axis:
                // pitch and roll flip, yaw is unchanged. Verified on the WebHID demo
                // (#78, 2026-05-27 — right pitch then roll both read inverted, yaw fine).
                // A 180° turn is a proper rotation, so apply it identically to gyro and
                // accel to keep the IMU vector coherent.
                if (src == ViiperGyroSourceKind.Right)
                {
                    gx = SignedClampToShort(-(int)gx);   // pitch
                    gz = SignedClampToShort(-(int)gz);   // roll
                    ax = SignedClampToShort(-(int)ax);
                    az = SignedClampToShort(-(int)az);
                }
                WriteSwitchproImu(frame, gx, gy, gz, ax, ay, az);
            }
        }

        /// <summary>
        /// Writes the switchpro/Joy-Con wire IMU (gyro-first 12-17, accel 18-23) applying the
        /// Joy-Con frame correction. The consumer (Steam / joy-con-webhid) reads the switchpro
        /// IMU slots through the Joy-Con's native orientation, which relabels our axes cyclically
        /// (our pitch→roll, yaw→pitch, roll→yaw — verified 2026-05-27 on the WebHID demo). We
        /// pre-rotate so motions display correctly: wire (GyroX,GyroY,GyroZ) ← (roll,pitch,yaw)
        /// = (gz,gx,gy); accel takes the same rotation. A 3-cycle is a proper rotation so no sign
        /// flip is needed, and the left/right mirror signs carry through unchanged.
        /// </summary>
        private static void WriteSwitchproImu(byte[] frame,
                                              short gx, short gy, short gz,
                                              short ax, short ay, short az)
        {
            WriteI16(frame, 12, gz);   // GyroX  <- roll
            WriteI16(frame, 14, gx);   // GyroY  <- pitch
            WriteI16(frame, 16, gy);   // GyroZ  <- yaw
            WriteI16(frame, 18, az);   // AccelX <- roll axis
            WriteI16(frame, 20, ax);   // AccelY <- pitch axis
            WriteI16(frame, 22, ay);   // AccelZ <- yaw axis
        }

        public void SetGuideButtonMode(ViiperGuideButtonMode mode)
        {
            if (guideMode == mode) return;
            guideMode = mode;
            Logger.Info($"VIIPER forwarder guide-button mode -> {mode}");
        }

        public void SetSwapRumbleMotors(bool swap)
        {
            if (swapRumbleMotors == swap) return;
            swapRumbleMotors = swap;
            Logger.Info($"VIIPER forwarder swap-rumble-motors -> {swap}");
        }

        /// <summary>Enable/disable mirroring the emulated DS4/DSEdge lightbar onto the Legion stick lights.</summary>
        public void SetMirrorLightbarToStick(bool enabled)
        {
            if (mirrorLightbar == enabled) return;
            mirrorLightbar = enabled;
            // Forget the last forwarded color so the next observation re-evaluates from
            // scratch under the new policy (avoids stale dedup state if the user toggles
            // mid-game).
            lastLedPacked = -1;
            Logger.Info($"VIIPER forwarder mirror-lightbar-to-stick -> {enabled}");
        }

        /// <summary>
        /// Updates the per-stick + per-trigger shaping configuration. Applied
        /// inside PollLoop right before BuildDeviceInput so all wire formats
        /// see the transformed values. Read under a lock; the active flag is
        /// the fast bypass when the bundle is identity (no per-axis work).
        /// </summary>
        public void SetStickTriggerConfig(StickTriggerConfigBundle bundle)
        {
            bool active = IsStickTriggerConfigActive(bundle);
            lock (stickTriggerConfigLock)
            {
                stickTriggerConfig = bundle;
                stickTriggerConfigActive = active;
            }
            Logger.Info($"VIIPER forwarder stick/trigger config updated (active={active})");
        }

        private static bool IsStickTriggerConfigActive(StickTriggerConfigBundle b)
        {
            // Identity check: any non-default field means at least one axis is shaped.
            return IsStickActive(b.LeftStick) || IsStickActive(b.RightStick)
                || IsTriggerActive(b.LeftTrigger) || IsTriggerActive(b.RightTrigger);
        }
        private static bool IsStickActive(StickConfig c) =>
            c.DeadzoneX > 0f || c.DeadzoneY > 0f
            || c.AntiDeadzoneX > 0f || c.AntiDeadzoneY > 0f
            || c.CurveX != SensitivityCurve.Linear || c.CurveY != SensitivityCurve.Linear
            || c.Shape != DeadzoneShape.ScaledRadial;
        private static bool IsTriggerActive(TriggerConfig c) =>
            c.DeadzoneStart > 0f || c.RangeMax < 1f
            || c.AntiDeadzone > 0f || c.Curve != SensitivityCurve.Linear;

        /// <summary>
        /// Wires the helper-owned live-sample sink. Called once by
        /// <see cref="ViiperEmulationManager"/> at startup. The forwarder
        /// publishes raw stick/trigger frames to this property whenever the
        /// preview flag is set.
        /// </summary>
        public void SetStickTriggerLiveSink(ViiperStickTriggerLiveSampleProperty sink)
        {
            stickTriggerLiveSink = sink;
        }

        /// <summary>
        /// Turns the live-preview stream on or off. Called from the manager
        /// in response to the widget toggling
        /// <see cref="Function.Viiper_StickTriggerPreviewEnabled"/>.
        /// </summary>
        public void SetStickTriggerPreviewEnabled(bool enabled)
        {
            stickTriggerPreviewEnabled = enabled;
            if (!enabled) stickTriggerLastPreviewTickMs = 0;
            Logger.Info($"VIIPER stick/trigger preview -> {(enabled ? "on" : "off")}");
        }

        /// <summary>
        /// Applies the configured shaping to the gamepad sample in-place.
        /// Called right before BuildDeviceInput so both LegionHid and XInput
        /// input paths feed transformed values to the wire builders. Cheap
        /// no-op when no axis is configured.
        ///
        /// <para>Also pumps the live-preview stream at the top of the
        /// function when the widget panel is open: the raw values entering
        /// this method are exactly what the canvas needs to show, and the
        /// widget runs the same StickTriggerProcessor locally to render the
        /// shaped indicators against the user's current bundle.</para>
        /// </summary>
        private void ApplyStickTriggerShaping(ref ViiperXInputGamepad gp)
        {
            // Push raw frame to the widget BEFORE any shaping. Throttled to
            // ~30Hz so a 1kHz USB hot path doesn't flood the pipe.
            if (stickTriggerPreviewEnabled && stickTriggerLiveSink != null)
            {
                int nowMs = Environment.TickCount;
                if (stickTriggerLastPreviewTickMs == 0
                    || (nowMs - stickTriggerLastPreviewTickMs) >= StickTriggerPreviewIntervalMs)
                {
                    stickTriggerLastPreviewTickMs = nowMs;
                    string payload = gp.ThumbLX + "," + gp.ThumbLY + "," + gp.ThumbRX + "," + gp.ThumbRY + "," + gp.LeftTrigger + "," + gp.RightTrigger;
                    stickTriggerLiveSink.SetValue(payload);
                }
            }

            StickTriggerConfigBundle cfg;
            bool active;
            lock (stickTriggerConfigLock)
            {
                if (!stickTriggerConfigActive) return;
                cfg = stickTriggerConfig;
                active = true;
            }
            if (!active) return;

            StickTriggerProcessor.TransformStick(gp.ThumbLX, gp.ThumbLY, cfg.LeftStick, out var lx, out var ly);
            gp.ThumbLX = lx; gp.ThumbLY = ly;

            StickTriggerProcessor.TransformStick(gp.ThumbRX, gp.ThumbRY, cfg.RightStick, out var rx, out var ry);
            gp.ThumbRX = rx; gp.ThumbRY = ry;

            gp.LeftTrigger = StickTriggerProcessor.TransformTrigger(gp.LeftTrigger, cfg.LeftTrigger);
            gp.RightTrigger = StickTriggerProcessor.TransformTrigger(gp.RightTrigger, cfg.RightTrigger);
        }

        /// <summary>Sets the rumble intensity multiplier (percent, 0..200).</summary>
        public void SetRumbleIntensity(int percent)
        {
            if (percent < 0) percent = 0;
            if (percent > 200) percent = 200;
            int scaled = percent * 10; // Keep one decimal of resolution in integer math.
            if (rumbleIntensityScaled == scaled) return;
            rumbleIntensityScaled = scaled;
            Logger.Info($"VIIPER forwarder rumble-intensity -> {percent}%");
        }

        /// <summary>Sets the IMU axis remap. Each arg is "X", "Y", "Z", "-X", "-Y", or "-Z".</summary>
        public void SetGyroAxisMapping(string mapX, string mapY, string mapZ)
        {
            // The per-target gyro/accel frame is now hardcoded in the wire builders
            // (SteamDeck + all Sony targets, verified 2026-05-28), and the manual IMU
            // axis-mapping UI is hidden. Force identity here so any value persisted by an
            // older build can't silently compound with the hardcoded frame and re-break axes.
            axisMapXSrc = 0; axisMapXSign = 1;
            axisMapYSrc = 1; axisMapYSign = 1;
            axisMapZSrc = 2; axisMapZSign = 1;
        }


        /// <summary>
        /// Called from the Labs Legion-button action path when a user-mapped "Xbox Guide"
        /// press or release fires on a physical Legion button. Routes the event through
        /// the user's current VIIPER Guide-button mode:
        ///   • Native  → holds the emulated device's Guide/PS button while physically held
        ///   • GameBar → fires a single Win+G on press-edge (release is a no-op)
        /// Returns true if VIIPER consumed the event so the caller skips its legacy path.
        /// </summary>
        /// <summary>
        /// True when the VIIPER forwarder is up and will consume a Labs Guide press
        /// (either by routing it to the emulated device's Guide/PS button in Native mode
        /// or by firing Win+G in GameBar mode). LegionButtonMonitor uses this to decide
        /// whether a dedicated Guide-only ViGEm controller is still needed.
        /// </summary>
        public static bool CanHandleExternalGuide()
        {
            var instance = activeInstance;
            return instance != null && instance.running;
        }

        public static bool TryHandleGuideButtonFromLabs(bool pressed)
        {
            var instance = activeInstance;
            if (instance == null || !instance.running) return false;

            if (instance.guideMode == ViiperGuideButtonMode.Native)
            {
                if (pressed)
                {
                    instance.labsGuideHeld = true;
                    instance.labsGuideMinReleaseTicks = DateTime.UtcNow.Ticks
                        + TimeSpan.FromMilliseconds(LabsGuideMinHoldMilliseconds).Ticks;
                    Logger.Info("VIIPER guide-press handled from Labs (Native, hold while pressed)");
                }
                else
                {
                    instance.labsGuideHeld = false;
                    Logger.Info("VIIPER guide-release handled from Labs (Native)");
                }
                return true;
            }

            if (instance.guideMode == ViiperGuideButtonMode.GameBar)
            {
                if (!pressed) return true; // consume release, nothing to do
                long now = DateTime.UtcNow.Ticks;
                if ((now - instance.lastGuideShortcutTicks) >= GuideShortcutMinIntervalTicks)
                {
                    instance.lastGuideShortcutTicks = now;
                    try
                    {
                        Logger.Info("VIIPER guide-press handled from Labs (GameBar, firing Win+G)");
                        Program.SendKeyboardShortcut("Win+G");
                    }
                    catch (Exception ex) { Logger.Warn($"Labs guide Win+G failed: {ex.Message}"); }
                }
                return true;
            }

            return false;
        }

        /// <summary>Legacy single-shot press entry point — kept for callers that only
        /// fire on press (e.g. scroll actions). Emits a synthetic short hold.</summary>
        public static bool TryHandleGuidePressFromLabs()
        {
            bool handled = TryHandleGuideButtonFromLabs(true);
            if (handled)
            {
                // Auto-release after the minimum hold window on a background thread so
                // scroll-style one-shot actions still produce a clean press+release.
                System.Threading.Tasks.Task.Run(async () =>
                {
                    await System.Threading.Tasks.Task.Delay(LabsGuideMinHoldMilliseconds);
                    TryHandleGuideButtonFromLabs(false);
                });
            }
            return handled;
        }

        private bool IsGuideHoldActive()
            => labsGuideHeld || DateTime.UtcNow.Ticks < labsGuideMinReleaseTicks;

        /// <summary>
        /// Translates LegionButtonMonitor.AuxButtons (its own bitmap layout) into the
        /// reference VIIPER LegionAux layout used by all wire builders in this file.
        /// Without this translation, every paddle, Mode, and Share bit would alias to
        /// the wrong LegionAux value and the DSE/DS4/Elite2/Switch/etc. paddle mappings
        /// would be completely scrambled.
        /// </summary>
        private static ushort TranslateMonitorAuxToLegionAux(ushort monitorAux)
        {
            // LegionButtonMonitor bitmap (from LegionButtonMonitor.cs:68-75):
            //   MODE=0x0001, SHARE=0x0002, EXTRA_L1=0x0004 (back L upper = Y1),
            //   EXTRA_L2=0x0008 (back L lower = Y2), EXTRA_R1=0x0010 (back R upper = Y3),
            //   EXTRA_RM1=0x0020 (M1 side grip L), EXTRA_R2=0x0040 (back R lower = M3),
            //   EXTRA_R3=0x0080 (M2 side grip R).
            const ushort MON_MODE   = 0x0001;
            const ushort MON_SHARE  = 0x0002;
            const ushort MON_Y1     = 0x0004;
            const ushort MON_Y2     = 0x0008;
            const ushort MON_Y3     = 0x0010;
            const ushort MON_M1     = 0x0020;
            const ushort MON_M3     = 0x0040;
            const ushort MON_M2     = 0x0080;

            ushort result = 0;
            if ((monitorAux & MON_Y1)    != 0) result |= LegionAux.Y1;
            if ((monitorAux & MON_Y2)    != 0) result |= LegionAux.Y2;
            if ((monitorAux & MON_Y3)    != 0) result |= LegionAux.Y3;
            if ((monitorAux & MON_M3)    != 0) result |= LegionAux.M3;
            if ((monitorAux & MON_M1)    != 0) result |= LegionAux.M1;
            if ((monitorAux & MON_M2)    != 0) result |= LegionAux.M2;
            if ((monitorAux & MON_MODE)  != 0) result |= LegionAux.Mode;
            if ((monitorAux & MON_SHARE) != 0) result |= LegionAux.Share;
            return result;
        }

        /// <summary>
        /// Fetches the current gyro/accel sample from the selected source, converted to DS4
        /// wire-format int16 counts. Returns false when no source is selected, the source
        /// has no fresh data, or the source is "Handheld" (not wired yet).
        /// </summary>
        private bool TryBuildImuCounts(out short gyroXRaw, out short gyroYRaw, out short gyroZRaw,
                                        out short accelXRaw, out short accelYRaw, out short accelZRaw)
        {
            bool ok = TryBuildImuCounts(gyroSource, out gyroXRaw, out gyroYRaw, out gyroZRaw,
                                        out accelXRaw, out accelYRaw, out accelZRaw);
            // Single-source "Right" is the mirror of "Left" (the right Legion IMU is
            // mounted mirrored): pitch/yaw are sign-opposed, accel Z too. Bring it into
            // the Left/Mixed frame using the SAME constants the Mixed merge applies to the
            // right side, so Left, Right, and Mixed all agree on direction. Only the
            // gyroSource==Right standard path is touched — Mixed has its own merge, and the
            // joycon-pair per-half path calls the explicit-source overload (raw), so neither
            // is affected.
            if (ok && gyroSource == ViiperGyroSourceKind.Right)
            {
                gyroXRaw = SignedClampToShort((int)(gyroXRaw * LegionMixedGyroMerge.RightGyroXSign));
                gyroYRaw = SignedClampToShort((int)(gyroYRaw * LegionMixedGyroMerge.RightGyroYSign));
                gyroZRaw = SignedClampToShort((int)(gyroZRaw * LegionMixedGyroMerge.RightGyroZSign));
                accelXRaw = SignedClampToShort((int)(accelXRaw * LegionMixedGyroMerge.RightAccelXSign));
                accelYRaw = SignedClampToShort((int)(accelYRaw * LegionMixedGyroMerge.RightAccelYSign));
                accelZRaw = SignedClampToShort((int)(accelZRaw * LegionMixedGyroMerge.RightAccelZSign));
            }
            return ok;
        }

        /// <summary>
        /// Variant that reads an explicit source (used by joycon-pair per-half routing to
        /// pull the left controller IMU for one Joy-Con and the right for the other).
        /// </summary>
        private bool TryBuildImuCounts(ViiperGyroSourceKind src,
                                        out short gyroXRaw, out short gyroYRaw, out short gyroZRaw,
                                        out short accelXRaw, out short accelYRaw, out short accelZRaw)
        {
            gyroXRaw = gyroYRaw = gyroZRaw = 0;
            accelXRaw = accelYRaw = accelZRaw = 0;

            if (src == ViiperGyroSourceKind.None)
            {
                return false;
            }

            float gXdps, gYdps, gZdps, aXg, aYg, aZg;
            if (src == ViiperGyroSourceKind.Mixed || src == ViiperGyroSourceKind.Handheld)
            {
                bool hasLeft = LegionButtonMonitor.TryGetLatestGyroSample(true, out LegionGyroSample left);
                bool hasRight = LegionButtonMonitor.TryGetLatestGyroSample(false, out LegionGyroSample right);
                if (!hasLeft && !hasRight)
                {
                    return false;
                }
                if (hasLeft && hasRight)
                {
                    // Shared mirror-inversion + average; same convention the legacy
                    // Mixed adapter uses so both backends agree on axis signs.
                    GyroSample merged = LegionMixedGyroMerge.Merge(left, right);
                    gXdps = merged.GyroXDegPerSecond;
                    gYdps = merged.GyroYDegPerSecond;
                    gZdps = merged.GyroZDegPerSecond;
                    aXg = merged.AccelXG;
                    aYg = merged.AccelYG;
                    aZg = merged.AccelZG;
                }
                else
                {
                    // Single-side fallback: pass the available side through untouched.
                    LegionGyroSample one = hasLeft ? left : right;
                    gXdps = one.GyroXDegPerSecond;
                    gYdps = one.GyroYDegPerSecond;
                    gZdps = one.GyroZDegPerSecond;
                    aXg = one.AccelXG;
                    aYg = one.AccelYG;
                    aZg = one.AccelZG;
                }
            }
            else
            {
                bool useLeft = src == ViiperGyroSourceKind.Left;
                if (!LegionButtonMonitor.TryGetLatestGyroSample(useLeft, out LegionGyroSample sample))
                {
                    return false;
                }
                gXdps = sample.GyroXDegPerSecond;
                gYdps = sample.GyroYDegPerSecond;
                gZdps = sample.GyroZDegPerSecond;
                aXg = sample.AccelXG;
                aYg = sample.AccelYG;
                aZg = sample.AccelZG;
            }

            short gX = SaturateToShort(gXdps * GyroDpsToRawCounts);
            short gY = SaturateToShort(gYdps * GyroDpsToRawCounts);
            short gZ = SaturateToShort(gZdps * GyroDpsToRawCounts);
            short aX = SaturateToShort(aXg * AccelGToRawCounts);
            short aY = SaturateToShort(aYg * AccelGToRawCounts);
            short aZ = SaturateToShort(aZg * AccelGToRawCounts);

            // Apply user-selectable axis remap. Each output channel pulls from source[src]
            // and optionally flips sign. Accel tracks the same map as gyro so the vectors
            // stay coherent (matches the reference VIIPER Controller app behavior).
            int xSrc = axisMapXSrc, xSign = axisMapXSign;
            int ySrc = axisMapYSrc, ySign = axisMapYSign;
            int zSrc = axisMapZSrc, zSign = axisMapZSign;
            gyroXRaw = SignedClampToShort(PickAxis(gX, gY, gZ, xSrc) * xSign);
            gyroYRaw = SignedClampToShort(PickAxis(gX, gY, gZ, ySrc) * ySign);
            gyroZRaw = SignedClampToShort(PickAxis(gX, gY, gZ, zSrc) * zSign);
            accelXRaw = SignedClampToShort(PickAxis(aX, aY, aZ, xSrc) * xSign);
            accelYRaw = SignedClampToShort(PickAxis(aX, aY, aZ, ySrc) * ySign);
            accelZRaw = SignedClampToShort(PickAxis(aX, aY, aZ, zSrc) * zSign);
            return true;
        }

        private static short PickAxis(short x, short y, short z, int src)
        {
            switch (src)
            {
                case 0:  return x;
                case 1:  return y;
                default: return z;
            }
        }

        private static short SignedClampToShort(int value)
        {
            if (value > short.MaxValue) return short.MaxValue;
            if (value < short.MinValue) return short.MinValue;
            return (short)value;
        }

        private static short SaturateToShort(float value)
        {
            // Round to NEAREST, not truncate-toward-zero. A plain (short) cast drops the
            // fractional part every sample; since Steam integrates these counts over time,
            // the dropped fractions accumulate and a gesture (e.g. pitch up then back down)
            // doesn't return to its start — visible as lost precision / cursor drift. Rounding
            // is symmetric and unbiased, so up- and down-swings cancel cleanly.
            value = (float)Math.Round(value, MidpointRounding.AwayFromZero);
            if (value > short.MaxValue) return short.MaxValue;
            if (value < short.MinValue) return short.MinValue;
            return (short)value;
        }

        private static short ScaleDs4Accel(short raw)
        {
            int scaled = raw * 2;
            if (scaled > short.MaxValue) return short.MaxValue;
            if (scaled < short.MinValue) return short.MinValue;
            return (short)scaled;
        }

        public void Stop()
        {
            if (!running) return;
            running = false;
            if (activeInstance == this) activeInstance = null;
            try
            {
                if (pollThread != null && pollThread.IsAlive)
                {
                    pollThread.Join(500);
                }
            }
            catch (Exception ex) { Logger.Warn($"VIIPER forwarder join threw: {ex.Message}"); }
            pollThread = null;

            // Tear down the stick-gyro adapter so the LegionButtonMonitor reference is
            // released. EnsureGyroAdapter rebuilds on next Start.
            stickGyro.Shutdown();

            // Clear any lingering rumble on the physical controller.
            try
            {
                var zero = new ViiperXInputVibration();
                ViiperXInput.SetState(physicalIndex, ref zero);
            }
            catch { }

            Logger.Info("VIIPER forwarder stopped");
        }

        public void Dispose()
        {
            Stop();
            if (service != null)
            {
                service.FeedbackReceived -= OnFeedbackReceived;
            }
        }

        private void PollLoop()
        {
            var xiState = new ViiperXInputState();
            uint lastPacket = unchecked((uint)-1);
            long lastLegionTicks = 0;
            int errorCount = 0;

            // Raise the system timer resolution to 1ms so the SteamDeck 4ms paced send is
            // accurate (default Windows granularity is ~15.6ms). Balanced in finally below.
            try { if (NativeTimeBeginPeriod(1) == 0) _timerResolutionRaised = true; }
            catch (Exception ex) { Logger.Debug($"timeBeginPeriod(1) failed: {ex.Message}"); }
            try
            {

            // Diagnostic counters: emit a periodic stats line so we can tell the
            // difference between "no input arriving" (legion sample stale / XInput
            // packet not advancing), "input arriving but not forwarded" (reports sent
            // is zero), and "forwarded but not visible to Windows" (reports sent > 0
            // yet user reports no input — see issue #79 vvalente30). Without this,
            // SetInput failures are logged but successes are silent, and the LegionHid
            // path simply skips on stale samples without any trace.
            int statsLegionFreshSamples = 0;
            int statsLegionStaleSamples = 0;
            int statsLegionMissingSamples = 0;
            int statsXInputFreshPackets = 0;
            int statsXInputStalePackets = 0;
            int statsXInputErrors = 0;
            int statsReportsSent = 0;
            int statsReportsFailed = 0;
            long statsWindowStartTicks = DateTime.UtcNow.Ticks;
            const long StatsWindowTicks = TimeSpan.TicksPerSecond * 5;

            while (running)
            {
                long nowTicks = DateTime.UtcNow.Ticks;
                if (nowTicks - statsWindowStartTicks >= StatsWindowTicks)
                {
                    int rumbleRx = System.Threading.Interlocked.Exchange(ref statsRumbleEventsReceived, 0);
                    int rumbleOk = System.Threading.Interlocked.Exchange(ref statsRumbleForwardedOk, 0);
                    int rumbleErr = System.Threading.Interlocked.Exchange(ref statsRumbleForwardedErr, 0);
                    bool anyActivity = statsReportsSent > 0
                        || statsReportsFailed > 0
                        || statsLegionFreshSamples > 0
                        || statsLegionStaleSamples > 0
                        || statsLegionMissingSamples > 0
                        || statsXInputFreshPackets > 0
                        || statsXInputStalePackets > 0
                        || statsXInputErrors > 0
                        || rumbleRx > 0
                        || rumbleOk > 0
                        || rumbleErr > 0;
                    if (anyActivity)
                    {
                        Logger.Info(
                            "VIIPER forwarder 5s stats: source={0}, type={1}, physicalIdx={10}, " +
                            "reportsSent={2}, reportsFailed={3}, " +
                            "legionFresh={4}, legionStale={5}, legionMissing={6}, " +
                            "xinputFresh={7}, xinputStale={8}, xinputErrors={9}, " +
                            "rumbleRx={11}, rumbleOk={12}, rumbleErr={13}",
                            inputSource, targetType,
                            statsReportsSent, statsReportsFailed,
                            statsLegionFreshSamples, statsLegionStaleSamples, statsLegionMissingSamples,
                            statsXInputFreshPackets, statsXInputStalePackets, statsXInputErrors,
                            physicalIndex,
                            rumbleRx, rumbleOk, rumbleErr);
                    }
                    statsLegionFreshSamples = 0;
                    statsLegionStaleSamples = 0;
                    statsLegionMissingSamples = 0;
                    statsXInputFreshPackets = 0;
                    statsXInputStalePackets = 0;
                    statsXInputErrors = 0;
                    statsReportsSent = 0;
                    statsReportsFailed = 0;
                    statsWindowStartTicks = nowTicks;
                }

                // Steamdeck rumble auto-stop. Steam Deck haptic pulses are finite
                // (one 0x8F plays for Count * (Duration + Interval) microseconds);
                // when the train ends, Steam expects the physical Deck's LRAs to
                // fall silent naturally. XInput motors do NOT auto-stop, so we
                // detect expiration here and force a SetState(0,0) once the
                // pulse train has elapsed. Without this the physical pad buzzes
                // indefinitely after any single rumble event.
                if (IsSteamDeckLikeTarget() && DecayExpiredSteamDeckRumble())
                {
                    FlushSteamDeckRumbleToXInput();
                }
                // Switch 2 Pro auto-decay — Steam doesn't send explicit stops,
                // we model the real-hardware envelope locally and flush a
                // SetState(0,0) when the pulse window elapses.
                if (targetType == "ns2pro" && DecayExpiredNS2ProRumble())
                {
                    FlushNS2ProRumbleToXInput();
                }

                try
                {
                    // Pause gate: while the manager is hot-swapping the virtual device,
                    // skip pumping input. This avoids thousands of "invalid input size"
                    // warnings during the 1-2 second window between RemoveDevice() and
                    // AddDevice() completing, and prevents the first post-swap packet
                    // from being built with the old targetType.
                    if (paused)
                    {
                        Thread.Sleep(10);
                        continue;
                    }
                    if (inputSource == ViiperInputSourceKind.LegionHid)
                    {
                        LegionGamepadSample sample;
                        if (!LegionButtonMonitor.TryGetLatestGamepadSample(out sample))
                        {
                            statsLegionMissingSamples++;
                            Thread.Sleep(8);
                            continue;
                        }
                        if (sample.TimestampTicksUtc == lastLegionTicks)
                        {
                            statsLegionStaleSamples++;
                            // Skip stale samples for ALL targets, SteamDeck included. Steam
                            // integrates gyro velocity over the report frame counter (bytes 4-7),
                            // so the frame field is Steam's timebase. The frame counter only
                            // advances inside BuildSteamDeckInput, i.e. once per report we send —
                            // so sending duplicate (zero-order-hold) reports on stale samples
                            // would advance the timebase without new motion, making Steam under-
                            // integrate on the dupes and overshoot on the next real delta
                            // (visible as gyro lag-then-snap). HHD avoids this by sending one
                            // report per fresh IMU sample only ("send only when gyro sends a
                            // timestamp"). Match that: event-driven at the true ~125Hz HID rate,
                            // one frame-counter tick per real sample.
                            Thread.Sleep(4);
                            continue;
                        }
                        else
                        {
                            lastLegionTicks = sample.TimestampTicksUtc;
                            statsLegionFreshSamples++;
                        }
                        // LegionButtonMonitor uses its own AuxButtons bitmap — translate into
                        // the reference LegionAux layout so downstream wire builders see the
                        // correct paddle/Mode/Share bits.
                        currentAuxButtons = TranslateMonitorAuxToLegionAux(sample.AuxButtons);

                        // Guide-button mode: if the user wants Mode/Guide to open Xbox Game
                        // Bar instead of the emulated PS/Guide button, fire Win+G on press
                        // edge and strip the press from the outgoing state.
                        bool guidePressed = ((sample.Buttons & ViiperXInput.Guide) != 0)
                                         || ((currentAuxButtons & LegionAux.Mode) != 0);
                        ApplyGuideModeEdge(guidePressed);
                        if (guideMode == ViiperGuideButtonMode.GameBar)
                        {
                            currentAuxButtons &= unchecked((ushort)~LegionAux.Mode);
                        }

                        // Latest right-touchpad state from Legion HID (DS4/DSE wire format
                        // expects touchpad coordinates as 12-bit packed values).
                        LegionTouchpadSample touch;
                        if (LegionButtonMonitor.TryGetLatestRightTouchpadSample(out touch))
                        {
                            currentTouchActive = touch.IsTouching;
                            currentTouchPressed = touch.IsPressed;
                            currentTouchX = touch.RawX;
                            currentTouchY = touch.RawY;
                        }
                        else
                        {
                            currentTouchActive = false;
                            currentTouchPressed = false;
                        }

                        var gp = ConvertLegionToXInputGamepad(sample);
                        if (guideMode == ViiperGuideButtonMode.GameBar)
                        {
                            gp.Buttons &= unchecked((ushort)~ViiperXInput.Guide);
                        }
                        else if (IsGuideHoldActive())
                        {
                            gp.Buttons |= ViiperXInput.Guide;
                        }
                        // Stick-gyro override: for target types with no native motion
                        // field, synthesize a stick contribution from gyro motion so
                        // users get the same Mode-1 ("Xbox / Stick") feel they had on
                        // the legacy ViGEm backend. LegionHid path provides the raw
                        // aux buttons for activation gating (M1/M2/M3/Y1/Y2/Y3). The
                        // processor honors the user's "Send to joystick" choice via
                        // stickGyro.RoutesToLeftStick (Left vs Right).
                        if (ViiperStickGyroProcessor.IsApplicableForTarget(targetType))
                        {
                            short physX = stickGyro.RoutesToLeftStick ? gp.ThumbLX : gp.ThumbRX;
                            short physY = stickGyro.RoutesToLeftStick ? gp.ThumbLY : gp.ThumbRY;
                            if (stickGyro.TryComputeStickOverride(gp.Buttons, gp.LeftTrigger, gp.RightTrigger,
                                    sample.AuxButtons, physX, physY, out short sgX, out short sgY))
                            {
                                if (stickGyro.RoutesToLeftStick)
                                {
                                    ViiperStickGyroProcessor.MergeStickVectors(gp.ThumbLX, gp.ThumbLY, sgX, sgY,
                                        out short mergedX, out short mergedY);
                                    gp.ThumbLX = mergedX;
                                    gp.ThumbLY = mergedY;
                                }
                                else
                                {
                                    ViiperStickGyroProcessor.MergeStickVectors(gp.ThumbRX, gp.ThumbRY, sgX, sgY,
                                        out short mergedX, out short mergedY);
                                    gp.ThumbRX = mergedX;
                                    gp.ThumbRY = mergedY;
                                }
                            }
                        }
                        ApplyStickTriggerShaping(ref gp);
                        byte[] data = BuildDeviceInput(gp);
                        if (data != null && data.Length > 0)
                        {
                            // EmitDeviceFrame mirrors to the secondary device when
                            // joycon-pair is active. libviiper slices the same wire frame
                            // into the appropriate Joy-Con half via its profile; in per-half
                            // gyro mode each half instead gets its matching controller IMU.
                            if (EmitDeviceFrame(data)) statsReportsSent++;
                            else statsReportsFailed++;
                        }
                    }
                    else // XInput
                    {
                        var rc = ViiperXInput.GetState(physicalIndex, ref xiState);
                        if (rc != ViiperXInput.ErrorSuccess)
                        {
                            statsXInputErrors++;
                            if (errorCount++ < 5 && Logger.IsDebugEnabled)
                            {
                                Logger.Debug($"XInput.GetState({physicalIndex}) rc=0x{rc:X8}");
                            }
                            Thread.Sleep(16);
                            continue;
                        }
                        errorCount = 0;

                        if (xiState.PacketNumber == lastPacket)
                        {
                            statsXInputStalePackets++;
                            Thread.Sleep(4);
                            continue;
                        }
                        lastPacket = xiState.PacketNumber;
                        statsXInputFreshPackets++;
                        currentAuxButtons = 0;  // XInput has no Legion aux buttons.
                        currentTouchActive = false;
                        currentTouchPressed = false;

                        bool guidePressed = (xiState.Gamepad.Buttons & ViiperXInput.Guide) != 0;
                        ApplyGuideModeEdge(guidePressed);

                        var gp = xiState.Gamepad;
                        if (guideMode == ViiperGuideButtonMode.GameBar)
                        {
                            gp.Buttons &= unchecked((ushort)~ViiperXInput.Guide);
                        }
                        else if (IsGuideHoldActive())
                        {
                            gp.Buttons |= ViiperXInput.Guide;
                        }
                        // Stick-gyro override on the XInput input path. No Legion aux
                        // buttons are available here (XInput hardware can't see them),
                        // so activation buttons 17-22 will read 0 and never trigger.
                        if (ViiperStickGyroProcessor.IsApplicableForTarget(targetType))
                        {
                            short physX2 = stickGyro.RoutesToLeftStick ? gp.ThumbLX : gp.ThumbRX;
                            short physY2 = stickGyro.RoutesToLeftStick ? gp.ThumbLY : gp.ThumbRY;
                            if (stickGyro.TryComputeStickOverride(gp.Buttons, gp.LeftTrigger, gp.RightTrigger,
                                    0 /* no aux on XInput path */, physX2, physY2, out short sgX, out short sgY))
                            {
                                if (stickGyro.RoutesToLeftStick)
                                {
                                    ViiperStickGyroProcessor.MergeStickVectors(gp.ThumbLX, gp.ThumbLY, sgX, sgY,
                                        out short mergedXl, out short mergedYl);
                                    gp.ThumbLX = mergedXl;
                                    gp.ThumbLY = mergedYl;
                                }
                                else
                                {
                                    ViiperStickGyroProcessor.MergeStickVectors(gp.ThumbRX, gp.ThumbRY, sgX, sgY,
                                        out short mergedX, out short mergedY);
                                    gp.ThumbRX = mergedX;
                                    gp.ThumbRY = mergedY;
                                }
                            }
                        }
                        ApplyStickTriggerShaping(ref gp);
                        byte[] data = BuildDeviceInput(gp);
                        if (data != null && data.Length > 0)
                        {
                            // EmitDeviceFrame mirrors to the secondary device when
                            // joycon-pair is active. libviiper slices the same wire frame
                            // into the appropriate Joy-Con half via its profile; in per-half
                            // gyro mode each half instead gets its matching controller IMU.
                            if (EmitDeviceFrame(data)) statsReportsSent++;
                            else statsReportsFailed++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn($"VIIPER forwarder poll error: {ex.Message}");
                    Thread.Sleep(100);
                }

                // Block until LegionButtonMonitor signals a new sample (or 16ms
                // timeout as a fallback for non-Legion sources / XInput-only).
                // Replaces the old unconditional Thread.Sleep(4) which paced
                // the loop at ~64Hz independent of the 125Hz HID arrival rate
                // — overwrote half the samples in the cache before we could
                // read them. Event-driven design: zero CPU when idle, catches
                // every HID report at its arrival.
                if (inputSource == ViiperInputSourceKind.LegionHid)
                {
                    // Event-driven: block until the next fresh Legion HID sample (~125Hz HQ
                    // gyro rate) or a short timeout. SteamDeck targets use this same path so
                    // each report maps 1:1 to a real IMU sample and the frame counter advances
                    // once per fresh sample — the timebase Steam integrates gyro against. See
                    // the stale-sample skip above for why duplicate sends cause lag/overshoot.
                    LegionButtonMonitor.WaitForNewSample(16);
                }
                else
                {
                    Thread.Sleep(4);
                }
                }
            }
            finally
            {
                if (_timerResolutionRaised)
                {
                    try { NativeTimeEndPeriod(1); } catch { }
                    _timerResolutionRaised = false;
                }
            }
        }

        /// <summary>
        /// Detects a press-edge on the Guide/Mode button. In GameBar mode, fires a
        /// single Win+G keystroke via the helper's input-injector path. Rate-limited
        /// so a held button doesn't spam the shortcut.
        /// </summary>
        private void ApplyGuideModeEdge(bool isPressed)
        {
            bool edge = isPressed && !guideWasPressed;
            guideWasPressed = isPressed;
            if (!edge) return;
            if (guideMode != ViiperGuideButtonMode.GameBar) return;

            long now = DateTime.UtcNow.Ticks;
            if ((now - lastGuideShortcutTicks) < GuideShortcutMinIntervalTicks) return;
            lastGuideShortcutTicks = now;

            try
            {
                Logger.Info("VIIPER guide-button: firing Win+G");
                Program.SendKeyboardShortcut("Win+G");
            }
            catch (Exception ex) { Logger.Warn($"Guide Win+G failed: {ex.Message}"); }
        }

        /// <summary>
        /// Adapts a Legion Go HID gamepad sample to the XInput-shaped struct the
        /// wire-format builders already consume. The Buttons bitfield from the Legion
        /// monitor is already XInput-compatible.
        /// </summary>
        private static ViiperXInputGamepad ConvertLegionToXInputGamepad(LegionGamepadSample s)
        {
            return new ViiperXInputGamepad
            {
                Buttons = s.Buttons,
                LeftTrigger = s.LeftTrigger,
                RightTrigger = s.RightTrigger,
                ThumbLX = s.LeftStickX,
                ThumbLY = s.LeftStickY,
                ThumbRX = s.RightStickX,
                ThumbRY = s.RightStickY,
            };
        }

        private byte[] BuildDeviceInput(ViiperXInputGamepad gp)
        {
            switch (targetType)
            {
                case "xbox360":
                    return BuildXbox360Input(gp);
                case "dualshock4":
                    return BuildDualShock4Input(gp);
                case "dualsenseedge":
                case "dualsense-edge":
                    return BuildDualSenseEdgeInput(gp);
                case "dualsense":
                case "ds5":
                    return BuildDualSenseInput(gp);
                case "xboxelite2":
                case "xbox-one":
                case "xbox-elite":
                    return BuildXboxElite2Input(gp);
                // libviiper xboxgip target — 3-interface USB GIP device.
                // Wire format is the same 20-byte InputState as xbox360
                // (per device/xboxgip/inputstate.go); libviiper remaps to
                // GIP buttons internally and ships paddles via the
                // Reserved[0]/Reserved[1] bytes when PID=0x0B00. We use
                // the xbox360 builder here for now (no paddle bytes yet);
                // dedicated paddle path can be added once descriptor +
                // metadata path is verified by USB tree dump.
                case "xboxgip":
                    return BuildXbox360Input(gp);
                // libviiper 32c8ed3 + clib rework split Steam-handheld targets off into
                // a dedicated steamdeck device with a 64-byte native wire format. The
                // old steam-generic / steamdeck-generic aliases now route to steamdeck
                // (with deprecation warnings) and any per-handheld alias (legion-go-2,
                // rog-ally, etc.) does the same with a VID/PID override. All of them
                // need the new wire format; sending the prior 33-byte elite2 format
                // fails UnmarshalBinary inside libviiper and the virtual pad receives
                // no input.
                case "steamdeck":
                case "steam-deck":
                case "steamdeck-generic":
                case "steam-generic":
                case "legion-go":
                case "legion-go-s":
                case "legion-go-2":
                case "msi-claw":
                case "rog-ally":
                case "zotac-zone":
                    return BuildSteamDeckInput(gp);
                // Steam Controller V1 (Gordon) — separate libviiper device from
                // steamdeck, separate 64-byte wire layout. Routed here via the
                // Steam sub-device combo (Tag="gordon") which ViiperEmulationManager
                // translates to targetType = "gordon" in ResolveDeviceTargets.
                case "gordon":
                case "steam-controller-v1":
                case "steamcontroller-v1":
                case "steam-controller":
                    return BuildSteamControllerInput(gp);
                case "switchpro":
                case "joycon-left":
                case "joycon-right":
                case "joycon-pair":
                    return BuildSwitchProInput(gp);
                case "ns2pro":
                    return BuildNS2ProInput(gp);
                default:
                    return BuildXbox360Input(gp);
            }
        }

        /// <summary>
        /// Switch Pro / Joy-Con wire format (24 bytes). libviiper's switchpro InputState
        /// requires ≥24 bytes; falling through to BuildXbox360Input sent only 20 bytes
        /// and every poll logged "switchpro: input state too short: 20 &lt; 24".
        /// Button positions are mapped positionally (Xbox A → Switch B since both are
        /// the bottom face button).
        /// </summary>
        private byte[] BuildSwitchProInput(ViiperXInputGamepad gp)
        {
            var data = new byte[24];
            uint buttons = 0;

            // Face buttons — positional.
            if ((gp.Buttons & ViiperXInput.A) != 0) buttons |= 0x000004;  // B (bottom)
            if ((gp.Buttons & ViiperXInput.B) != 0) buttons |= 0x000008;  // A (right)
            if ((gp.Buttons & ViiperXInput.X) != 0) buttons |= 0x000001;  // Y (left)
            if ((gp.Buttons & ViiperXInput.Y) != 0) buttons |= 0x000002;  // X (top)
            if ((gp.Buttons & ViiperXInput.LB) != 0) buttons |= 0x400000; // L
            if ((gp.Buttons & ViiperXInput.RB) != 0) buttons |= 0x000040; // R

            // Switch has no analog triggers — map >50% press to ZL/ZR digital.
            if (gp.LeftTrigger > 128) buttons |= 0x800000;  // ZL
            if (gp.RightTrigger > 128) buttons |= 0x000080; // ZR

            if ((gp.Buttons & ViiperXInput.Back) != 0) buttons |= 0x000100;       // Minus
            if ((gp.Buttons & ViiperXInput.Start) != 0) buttons |= 0x000200;      // Plus
            if ((gp.Buttons & ViiperXInput.Guide) != 0) buttons |= 0x001000;      // Home
            if ((gp.Buttons & ViiperXInput.LeftThumb) != 0) buttons |= 0x000800;
            if ((gp.Buttons & ViiperXInput.RightThumb) != 0) buttons |= 0x000400;

            if ((gp.Buttons & ViiperXInput.DPadUp) != 0)    buttons |= 0x020000;
            if ((gp.Buttons & ViiperXInput.DPadDown) != 0)  buttons |= 0x010000;
            if ((gp.Buttons & ViiperXInput.DPadLeft) != 0)  buttons |= 0x080000;
            if ((gp.Buttons & ViiperXInput.DPadRight) != 0) buttons |= 0x040000;

            // Landscape Joy-Con SL/SR rail buttons — the two small inner-rail
            // buttons that become the user's shoulder buttons when holding a
            // single Joy-Con sideways. NOT L/ZL (those are top-edge buttons
            // on a real Joy-Con; unreachable in landscape grip).
            //   joycon-left:  SL=0x200000, SR=0x100000 (left-side buttons byte)
            //   joycon-right: SL=0x000020, SR=0x000010 (right-side buttons byte)
            // Source: LeGo2 back paddles, top→SL, bottom→SR.
            //   left side : Y1 (top) → SL, Y2 (bottom) → SR
            //   right side: Y3 (top) → SL, M3 (bottom) → SR
            if (targetType == "joycon-left")
            {
                ushort aux = currentAuxButtons;
                if ((aux & LegionAux.Y1) != 0) buttons |= 0x200000;  // SL
                if ((aux & LegionAux.Y2) != 0) buttons |= 0x100000;  // SR
            }
            else if (targetType == "joycon-right")
            {
                ushort aux = currentAuxButtons;
                if ((aux & LegionAux.Y3) != 0) buttons |= 0x000020;  // SL
                if ((aux & LegionAux.M3) != 0) buttons |= 0x000010;  // SR
            }

            WriteU32(data, 0, buttons);

            WriteI16(data, 4, gp.ThumbLX);
            WriteI16(data, 6, gp.ThumbLY);
            WriteI16(data, 8, gp.ThumbRX);
            WriteI16(data, 10, gp.ThumbRY);

            // IMU at bytes 12-23, gyro-first per libviiper switchpro InputState.
            //
            // 2026-06-01 — why this transform exists:
            //
            // SDL3's HIDAPI Switch driver
            // (SDL/src/joystick/hidapi/SDL_hidapi_switch.c:2356 SendSensorUpdate)
            // applies a fixed PlayStation-style remap on gyro AND accel:
            //   data[0] (SDL_X) = -scale * values[1]   (negated wire Y)
            //   data[1] (SDL_Y) =  scale * values[2]   (passthrough wire Z)
            //   data[2] (SDL_Z) = -scale * values[0]   (negated wire X)
            // expecting a physical Pro Controller held parallel to the ground
            // (controller frame: BMI's natural axes vs the gameplay-frame the
            // driver targets).
            //
            // The Legion Go 2 is NOT held that way. The user holds it vertically
            // with the screen facing them — a 90deg-about-pitch rotation off the
            // Pro Controller's resting orientation. Without an extra frame
            // correction, Steam would behave as if we were holding a Pro
            // Controller vertically (unusual gameplay angle), so its gyro→mouse
            // and motion-aim maps would all feel off-axis.
            //
            // Empirically refined live: gyro (-gy, -gx, -gz) on the wire,
            // accel (-ay, -ax, -az). The Y/Z swap (gy→GyroX, gz→GyroZ) is
            // the 90deg-about-pitch rotation that maps handheld-vertical to
            // controller-horizontal; the per-axis negations cancel SDL3's
            // negations so each physical motion lands on its named SDL axis.
            // Accel mirrors gyro one-for-one so gravity and the rotation axis
            // stay perpendicular for Steam's sensor fusion (avoids the
            // cross-axis bleed we hit on Steam Deck).
            //
            // Alt toggle = plain pass-through wire (no SDL inversion / no
            // handheld→controller rotation). Useful when the downstream
            // consumer reads the HID report directly without SDL3's Switch
            // driver in the chain, OR when testing with an actual Pro
            // Controller-like handheld grip.
            short gx, gy, gz, ax, ay, az;
            if (TryBuildImuCounts(out gx, out gy, out gz, out ax, out ay, out az))
            {
                bool altConvention = XboxGamingBarHelper.Settings.SettingsManager.GetInstance()?.ViiperAlternateGyroConvention?.Value ?? false;
                if (altConvention)
                {
                    WriteI16(data, 12, gx);
                    WriteI16(data, 14, gy);
                    WriteI16(data, 16, gz);
                    WriteI16(data, 18, ax);
                    WriteI16(data, 20, ay);
                    WriteI16(data, 22, az);
                }
                else
                {
                    // Frame-coherence rule: axis PERMUTATIONS apply to both
                    // gyro AND accel; pure SIGN FLIPS on rotation polarity
                    // apply to gyro ONLY (negating accel inverts gravity,
                    // which cancels the gyro polarity change at the fusion
                    // layer — net no-op).
                    //
                    // Gyro: gy/gz swap (permutation, propagates to accel),
                    // then gy/gz positive (rotation polarity flips, gyro-only).
                    // Accel stays at the swapped+negated baseline.
                    //
                    // Three Switch-family per-target gyro transforms, all
                    // sharing the same SDL3 HIDAPI Switch driver chain but
                    // differing by physical mounting / driver mini-gamepad
                    // special cases:
                    //   • switchpro (Pro Controller paired):
                    //       gyro (gy, -gx, gz)
                    //   • joycon-pair (combined L+R virtual gamepad):
                    //       gyro (-gy, +gx, gz) — gx & gy sign flips vs Pro
                    //   • joycon-left (single Joy-Con mini-gamepad — SDL3
                    //     applies an extra (X,Z)↔(Z,-X) rotation in
                    //     HandleMiniControllerStateL, so we pre-swap gx/gz
                    //     here; ax/az also swap by the permutation rule).
                    // All accel sign flips (-ay,-ax,-az) shared across modes
                    // since gravity orientation doesn't change.
                    short hidGyroX, hidGyroY, hidGyroZ;
                    short hidAccelX, hidAccelY, hidAccelZ;
                    switch (targetType)
                    {
                        case "joycon-pair":
                            hidGyroX  = SignedClampToShort(-(int)gy);
                            hidGyroY  = gx;
                            hidGyroZ  = gz;
                            hidAccelX = SignedClampToShort(-(int)ay);
                            hidAccelY = SignedClampToShort(-(int)ax);
                            hidAccelZ = SignedClampToShort(-(int)az);
                            break;
                        case "joycon-left":
                            // 2026-06-01 — derived by inverting SDL3's joycon-left
                            // mini-gamepad transform against the SdlGyroTester
                            // guided-test mismatches:
                            //   SDL_X = -wire[12]   SDL_Y = +wire[16]   SDL_Z = +wire[14]
                            // (Combined PS-frame remap + (X←Z, Z←-X)
                            // mini-gamepad rotation in SendSensorUpdate.)
                            //
                            // After the cyclic shift, ALL three gyro axes need
                            // to be negated to match the user's source frame
                            // (sign-flip rule: gyro-only, accel stays — the
                            // permutation alone keeps gyro/accel frames
                            // coherent without further accel adjustment).
                            // Verified live 2026-06-01.
                            hidGyroX  = SignedClampToShort(-(int)gz);
                            hidGyroY  = SignedClampToShort(-(int)gy);
                            hidGyroZ  = SignedClampToShort(-(int)gx);
                            hidAccelX = az;
                            hidAccelY = ay;
                            hidAccelZ = SignedClampToShort(-(int)ax);
                            break;
                        case "joycon-right":
                            // 2026-06-01 — root-cause fix. The LegionMixedGyroMerge
                            // Right*Sign multipliers only flip signs between
                            // the left and right gyro chips. They can't fix
                            // the AXIS PERMUTATION the two chips actually use:
                            //   • Left chip: pitch on gz, yaw on gx, roll on gy
                            //     (verified 2539 with left gyro source)
                            //   • Right chip: pitch on gx, yaw on gy, roll on gz
                            //     (derived from 2542 joycon-right test summary)
                            // So the gx/gy/gz values BuildSwitchProInput sees
                            // when using Right gyro source are in a permuted
                            // frame compared to Left source. The pure sign
                            // changes we kept trying couldn't compensate.
                            //
                            // For pass-through SDL output, the correct wire is
                            // (+gx, -gz, +gy) on gyro: SDL3's joycon-right
                            // mini-gamepad transform (SDL_X=+wire[12],
                            // SDL_Y=-wire[16], SDL_Z=+wire[14]) then lands
                            // pitch on +SDL_X, yaw-left on +SDL_Y, roll-right
                            // on +SDL_Z.
                            //
                            // Accel wire matches joycon-left (+az, +ay, -ax):
                            // SDL3's joycon-right axis flip cancels the source-
                            // side X-sign difference between the two chips.
                            hidGyroX  = gx;
                            hidGyroY  = SignedClampToShort(-(int)gz);
                            hidGyroZ  = gy;
                            hidAccelX = az;
                            hidAccelY = ay;
                            hidAccelZ = SignedClampToShort(-(int)ax);
                            break;

                        default:    // switchpro
                            hidGyroX  = gy;
                            hidGyroY  = SignedClampToShort(-(int)gx);
                            hidGyroZ  = gz;
                            hidAccelX = SignedClampToShort(-(int)ay);
                            hidAccelY = SignedClampToShort(-(int)ax);
                            hidAccelZ = SignedClampToShort(-(int)az);
                            break;
                    }
                    WriteI16(data, 12, hidGyroX);
                    WriteI16(data, 14, hidGyroY);
                    WriteI16(data, 16, hidGyroZ);
                    WriteI16(data, 18, hidAccelX);
                    WriteI16(data, 20, hidAccelY);
                    WriteI16(data, 22, hidAccelZ);
                }
            }
            return data;
        }

        /// <summary>
        /// Switch 2 Pro Controller (ns2pro) wire frame — 27 bytes. Differs
        /// from the legacy switchpro frame in three ways:
        ///   1. Sticks are unsigned 12-bit Switch native (0..0xFFF, 0x800 =
        ///      center), not signed XInput range.
        ///   2. IMU layout is Accel first, then Gyro (switchpro is the
        ///      reverse).
        ///   3. Three trailing power bytes: batteryLevel (0..9), charging,
        ///      externalPower.
        ///
        /// <para>Button mapping mirrors BuildSwitchProInput: positional face
        /// buttons (Xbox A → Switch B is bottom face on both, etc.), Xbox
        /// triggers digital-press to ZL/ZR above 50%. ns2pro adds C, GR/GL,
        /// Headset, Capture buttons — we don't have XInput counterparts so
        /// leave them off until we expose them through a separate property.</para>
        /// </summary>
        private byte[] BuildNS2ProInput(ViiperXInputGamepad gp)
        {
            var data = new byte[27];
            uint buttons = 0;

            // ns2pro button bit layout (see device/ns2pro/const.go in
            // libviiper). Bit 0 = B, bit 1 = A, bit 2 = Y, bit 3 = X, ...
            if ((gp.Buttons & ViiperXInput.A) != 0) buttons |= 1u << 0;  // B (bottom)
            if ((gp.Buttons & ViiperXInput.B) != 0) buttons |= 1u << 1;  // A (right)
            if ((gp.Buttons & ViiperXInput.X) != 0) buttons |= 1u << 2;  // Y (left)
            if ((gp.Buttons & ViiperXInput.Y) != 0) buttons |= 1u << 3;  // X (top)
            if ((gp.Buttons & ViiperXInput.RB) != 0) buttons |= 1u << 4; // R
            if (gp.RightTrigger > 128) buttons |= 1u << 5;               // ZR
            if ((gp.Buttons & ViiperXInput.Start) != 0) buttons |= 1u << 6;       // Plus
            if ((gp.Buttons & ViiperXInput.RightThumb) != 0) buttons |= 1u << 7;  // RightStick
            if ((gp.Buttons & ViiperXInput.DPadDown) != 0) buttons |= 1u << 8;
            if ((gp.Buttons & ViiperXInput.DPadRight) != 0) buttons |= 1u << 9;
            if ((gp.Buttons & ViiperXInput.DPadLeft) != 0) buttons |= 1u << 10;
            if ((gp.Buttons & ViiperXInput.DPadUp) != 0) buttons |= 1u << 11;
            if ((gp.Buttons & ViiperXInput.LB) != 0) buttons |= 1u << 12; // L
            if (gp.LeftTrigger > 128) buttons |= 1u << 13;                // ZL
            if ((gp.Buttons & ViiperXInput.Back) != 0) buttons |= 1u << 14;       // Minus
            if ((gp.Buttons & ViiperXInput.LeftThumb) != 0) buttons |= 1u << 15;  // LeftStick
            if ((gp.Buttons & ViiperXInput.Guide) != 0) buttons |= 1u << 16;      // Home
            // Map the Legion Go back paddles to the Switch 2 Pro grip buttons, grouped by
            // side (Y1/Y2 = left back, Y3/M3 = right back) since the Pro 2 exposes only two
            // grips. Bits 17 (Capture), 20 (C), 21 (Headset) need sources we don't expose.
            ushort aux = currentAuxButtons;
            if ((aux & (LegionAux.Y1 | LegionAux.Y2)) != 0) buttons |= 1u << 19;  // GL <- left back paddles
            if ((aux & (LegionAux.Y3 | LegionAux.M3)) != 0) buttons |= 1u << 18;  // GR <- right back paddles

            WriteU32(data, 0, buttons);

            // Sticks: XInput signed int16 (-32768..32767) → ns2pro u16 (0..4095).
            // Center maps to 0x800, full deflection clamps to 0..0xFFF.
            WriteU16(data, 4, StickXInputToNS2Pro(gp.ThumbLX));
            WriteU16(data, 6, StickXInputToNS2Pro(gp.ThumbLY));
            WriteU16(data, 8, StickXInputToNS2Pro(gp.ThumbRX));
            WriteU16(data, 10, StickXInputToNS2Pro(gp.ThumbRY));

            // IMU at offsets 12-23: Accel XYZ (i16) then Gyro XYZ (i16) per
            // ns2pro's inputstate.go layout — note this is the REVERSE of
            // the DS4 wire which puts gyro first. TryBuildImuCounts returns
            // raw int16 counts in the same BMI160/BMI260 native units that
            // Switch firmware expects, so no scaling factor is needed.
            //
            // ns2pro has the opposite IMU layout from legacy switchpro:
            // AccelXYZ at 12-17, GyroXYZ at 18-23.
            short gx, gy, gz, ax, ay, az;
            if (TryBuildImuCounts(out gx, out gy, out gz, out ax, out ay, out az))
            {
                WriteI16(data, 12, ax);
                WriteI16(data, 14, ay);
                WriteI16(data, 16, az);
                WriteI16(data, 18, gx);
                WriteI16(data, 20, gy);
                WriteI16(data, 22, gz);
            }

            // Power bytes — default to "9/9 battery, externally powered" so
            // games that gate features on charge level treat it as full.
            data[24] = 9;  // BatteryLevel (max 9 per ns2pro spec)
            data[25] = 0;  // Charging
            data[26] = 1;  // ExternalPower

            return data;
        }

        private static ushort StickXInputToNS2Pro(short xinputValue)
        {
            // Map signed -32768..32767 to unsigned 0..0xFFF. The +32768
            // shift turns it into 0..65535 then >>4 to 0..4095.
            int shifted = (int)xinputValue + 32768;
            if (shifted < 0) shifted = 0;
            if (shifted > 65535) shifted = 65535;
            return (ushort)(shifted >> 4);
        }

        // -------------------------------------------------------------------
        // Wire format builders (ported from ViiperController reference impl)
        // -------------------------------------------------------------------

        private static byte[] BuildXbox360Input(ViiperXInputGamepad gp)
        {
            var data = new byte[20];
            WriteU32(data, 0, gp.Buttons);
            data[4] = gp.LeftTrigger;
            data[5] = gp.RightTrigger;
            WriteI16(data, 6, gp.ThumbLX);
            WriteI16(data, 8, gp.ThumbLY);
            WriteI16(data, 10, gp.ThumbRX);
            WriteI16(data, 12, gp.ThumbRY);
            return data;
        }

        private byte[] BuildDualShock4Input(ViiperXInputGamepad gp)
        {
            var data = new byte[31];
            // DS4 sticks are int8. Y-axis: XInput positive=UP, DS4 positive=DOWN → negate.
            data[0] = (byte)(gp.ThumbLX >> 8);
            data[1] = (byte)(NegateClamp(gp.ThumbLY) >> 8);
            data[2] = (byte)(gp.ThumbRX >> 8);
            data[3] = (byte)(NegateClamp(gp.ThumbRY) >> 8);

            ushort ds4Buttons = 0;
            if ((gp.Buttons & ViiperXInput.A) != 0) ds4Buttons |= 0x0020;
            if ((gp.Buttons & ViiperXInput.B) != 0) ds4Buttons |= 0x0040;
            if ((gp.Buttons & ViiperXInput.X) != 0) ds4Buttons |= 0x0010;
            if ((gp.Buttons & ViiperXInput.Y) != 0) ds4Buttons |= 0x0080;
            if ((gp.Buttons & ViiperXInput.LB) != 0) ds4Buttons |= 0x0100;
            if ((gp.Buttons & ViiperXInput.RB) != 0) ds4Buttons |= 0x0200;
            if ((gp.Buttons & ViiperXInput.Back) != 0) ds4Buttons |= 0x1000;
            if ((gp.Buttons & ViiperXInput.Start) != 0) ds4Buttons |= 0x2000;
            if ((gp.Buttons & ViiperXInput.LeftThumb) != 0) ds4Buttons |= 0x4000;
            if ((gp.Buttons & ViiperXInput.RightThumb) != 0) ds4Buttons |= 0x8000;
            if ((gp.Buttons & ViiperXInput.Guide) != 0) ds4Buttons |= 0x0001;

            // Legion back buttons -> DS4 extensions (preserve the extra-button channel the
            // user gets from Legion HID, so they don't go to waste in the DS4 mapping).
            ushort aux = currentAuxButtons;
            if ((aux & LegionAux.Mode) != 0) ds4Buttons |= 0x0001;   // Mode -> PS
            if ((aux & LegionAux.Share) != 0) ds4Buttons |= 0x0002;  // Share -> Touchpad click
            WriteU16(data, 4, ds4Buttons);

            byte dpad = 0;
            if ((gp.Buttons & ViiperXInput.DPadUp) != 0) dpad |= 0x01;
            if ((gp.Buttons & ViiperXInput.DPadDown) != 0) dpad |= 0x02;
            if ((gp.Buttons & ViiperXInput.DPadLeft) != 0) dpad |= 0x04;
            if ((gp.Buttons & ViiperXInput.DPadRight) != 0) dpad |= 0x08;
            data[6] = dpad;

            data[7] = gp.LeftTrigger;
            data[8] = gp.RightTrigger;

            // Touchpad bytes (DS4): X at 9-10, Y at 11-12, active flag at 13.
            if (currentTouchActive)
            {
                ushort tx = ScaleTouchAxis(currentTouchX, 1919);
                ushort ty = ScaleTouchAxis(currentTouchY, 942);
                WriteU16(data, 9, tx);
                WriteU16(data, 11, ty);
                data[13] = 1;
            }

            // IMU bytes at offsets 19-30 (gyroX,Y,Z then accelX,Y,Z as int16).
            // Sony gyro frame: the DS4 wire gyro slots are pitch/yaw/roll, but the raw Legion
            // frame is gx=pitch, gy=roll, gz=yaw. So yaw and roll must be swapped (gy<->gz) —
            // the same swap the SteamDeck path needs. Without it, physical yaw lands in the
            // roll slot (no lateral camera movement) and physical roll lands in the yaw slot.
            // Accel is swapped to match so the gyro/accel frames stay coherent for fusion.
            short gx, gy, gz, ax, ay, az;
            if (TryBuildImuCounts(out gx, out gy, out gz, out ax, out ay, out az))
            {
                // 2026-05-31: minimal-delta from upstream — gyro Y/Z signs flipped
                // (yaw was -gz, now +gz; roll was +gy, now -gy). Accel left
                // unchanged from upstream's working pattern so we only touch
                // the two axes that felt inverted with native in-game gyro aim.
                //
                // ViiperAlternateGyroConvention inverts gyro Y/Z back to the
                // upstream pattern (yaw=-gz, roll=+gy) — Steam Input's
                // gyro-to-mouse mapping sometimes wants the opposite polarity
                // from native in-game gyro aim. Default false = native-correct.
                bool altConvention = XboxGamingBarHelper.Settings.SettingsManager.GetInstance()?.ViiperAlternateGyroConvention?.Value ?? false;
                WriteI16(data, 19, gx);                                                                                   // gyro pitch (X)
                WriteI16(data, 21, altConvention ? SignedClampToShort(-(int)gz) : gz);                                    // gyro yaw  — default +gz, alt -gz
                WriteI16(data, 23, altConvention ? gy                           : SignedClampToShort(-(int)gy));          // gyro roll — default -gy, alt +gy
                WriteI16(data, 25, ScaleDs4Accel(ax));
                WriteI16(data, 27, ScaleDs4Accel(SignedClampToShort(-(int)az)));                                          // accel Y-slot <- -Z (unchanged)
                WriteI16(data, 29, ScaleDs4Accel(ay));                                                                    // accel Z-slot <- +Y (unchanged)
            }
            return data;
        }

        private byte[] BuildDualSenseEdgeInput(ViiperXInputGamepad gp)
        {
            var data = new byte[33];
            data[0] = (byte)(gp.ThumbLX >> 8);
            data[1] = (byte)(NegateClamp(gp.ThumbLY) >> 8);
            data[2] = (byte)(gp.ThumbRX >> 8);
            data[3] = (byte)(NegateClamp(gp.ThumbRY) >> 8);

            uint dseButtons = 0;
            if ((gp.Buttons & ViiperXInput.A) != 0) dseButtons |= 0x0020;
            if ((gp.Buttons & ViiperXInput.B) != 0) dseButtons |= 0x0040;
            if ((gp.Buttons & ViiperXInput.X) != 0) dseButtons |= 0x0010;
            if ((gp.Buttons & ViiperXInput.Y) != 0) dseButtons |= 0x0080;
            if ((gp.Buttons & ViiperXInput.LB) != 0) dseButtons |= 0x0100;
            if ((gp.Buttons & ViiperXInput.RB) != 0) dseButtons |= 0x0200;
            if ((gp.Buttons & ViiperXInput.Back) != 0) dseButtons |= 0x1000;
            if ((gp.Buttons & ViiperXInput.Start) != 0) dseButtons |= 0x2000;
            if ((gp.Buttons & ViiperXInput.LeftThumb) != 0) dseButtons |= 0x4000;
            if ((gp.Buttons & ViiperXInput.RightThumb) != 0) dseButtons |= 0x8000;
            if ((gp.Buttons & ViiperXInput.Guide) != 0) dseButtons |= 0x00010000;

            // Legion back paddles -> DSE Edge paddle buttons. DSE paddle bit masks:
            //   ExtraL2=0x00100000, ExtraL1=0x00400000,
            //   ExtraR1=0x00200000, ExtraL3=0x00800000.
            ushort aux = currentAuxButtons;
            if ((aux & LegionAux.Y1) != 0) dseButtons |= 0x00100000;    // Y1 (left upper)  -> ExtraL2
            if ((aux & LegionAux.Y2) != 0) dseButtons |= 0x00400000;    // Y2 (left lower)  -> ExtraL1
            if ((aux & LegionAux.Y3) != 0) dseButtons |= 0x00200000;    // Y3 (right upper) -> ExtraR1
            if ((aux & LegionAux.M3) != 0) dseButtons |= 0x00800000;    // M3 (right lower) -> ExtraL3
            if ((aux & LegionAux.Mode) != 0) dseButtons |= 0x00010000;  // Mode  -> PS
            if ((aux & LegionAux.Share) != 0) dseButtons |= 0x00020000; // Share -> Touchpad click
            WriteU32(data, 4, dseButtons);

            byte dpad = 0;
            if ((gp.Buttons & ViiperXInput.DPadUp) != 0) dpad |= 0x01;
            if ((gp.Buttons & ViiperXInput.DPadDown) != 0) dpad |= 0x02;
            if ((gp.Buttons & ViiperXInput.DPadLeft) != 0) dpad |= 0x04;
            if ((gp.Buttons & ViiperXInput.DPadRight) != 0) dpad |= 0x08;
            data[8] = dpad;

            data[9] = gp.LeftTrigger;
            data[10] = gp.RightTrigger;

            // Touchpad bytes (DSE): X at 11-12, Y at 13-14, active flag at 15.
            if (currentTouchActive)
            {
                ushort tx = ScaleTouchAxis(currentTouchX, 1920);
                ushort ty = ScaleTouchAxis(currentTouchY, 1080);
                WriteU16(data, 11, tx);
                WriteU16(data, 13, ty);
                data[15] = 1;
            }

            // DSE IMU bytes at offsets 21..32. Same yaw/roll (gy<->gz) frame swap as DS4/DS5.
            // ViiperAlternateGyroConvention honored same as DS4 builder: default
            // = native-correct (yaw=+gz, roll=-gy); on = upstream/legacy
            // (yaw=-gz, roll=+gy) for Steam Input gyro-to-mouse.
            short gx, gy, gz, ax, ay, az;
            if (TryBuildImuCounts(out gx, out gy, out gz, out ax, out ay, out az))
            {
                bool altConvention = XboxGamingBarHelper.Settings.SettingsManager.GetInstance()?.ViiperAlternateGyroConvention?.Value ?? false;
                WriteI16(data, 21, gx);                                                                                   // gyro pitch (X)
                WriteI16(data, 23, altConvention ? SignedClampToShort(-(int)gz) : gz);                                    // gyro yaw  — default +gz, alt -gz
                WriteI16(data, 25, altConvention ? gy                           : SignedClampToShort(-(int)gy));          // gyro roll — default -gy, alt +gy
                WriteI16(data, 27, ScaleDs4Accel(ax));
                WriteI16(data, 29, ScaleDs4Accel(SignedClampToShort(-(int)az)));                                          // accel Y-slot <- -Z
                WriteI16(data, 31, ScaleDs4Accel(ay));                                                                    // accel Z-slot <- +Y
            }
            return data;
        }

        /// <summary>
        /// Steam Controller V1 (Gordon, PID 0x1102) wire format from libviiper
        /// device/steamcontroller/inputstate.go. 64-byte native report (matches
        /// the physical wired Steam Controller's USB report) — same framing as
        /// steamdeck (0x01 / 0x00 / ReportID / PayloadLen / u32 frame at 0..7)
        /// but the button bits, axis layout, and the lack of a right stick all
        /// differ. Gordon hardware has: ABXY/L1/R1, L2/R2 analog triggers,
        /// Menu/Steam/Options, dpad, L3, left+right grip buttons, twin clickable
        /// touchpads (no L4/L5/R4/R5/QuickAccess like the Deck), one analog
        /// stick on the LEFT only, and IMU.
        ///
        /// XInput → Gordon mapping:
        ///   A/B/X/Y, LB/RB        → byte 8 (A=0x80 X=0x40 B=0x20 Y=0x10
        ///                                   L1=0x08 R1=0x04 L2=0x02 R2=0x01)
        ///   LT/RT > deadzone      → L2/R2 digital bits in byte 8
        ///   DPad                  → byte 9 (Up=0x01 Right=0x02 Left=0x04 Down=0x08)
        ///   Start/Back/Guide      → Menu (0x10) / Options (0x40) / Steam (0x20) byte 9
        ///   LeftThumb (L3)        → byte 10 bit 0x40
        ///   LStick                → bytes 54..58 (LStickX/Y i16 LE)
        ///   RStick                → bytes 20..24 (RPadX/Y — Gordon has no right
        ///                           stick; Steam treats the right touchpad as
        ///                           the virtual right stick, so XInput RStick
        ///                           maps there for natural game compat)
        ///   LT/RT analog          → byte 11/12 (single-byte) AND bytes 24..28
        ///                           (u16 LE, scaled ×257). Gordon writes both
        ///                           slots; consumers may read either.
        ///   Legion paddles        → grip / pad-press: Y1 → LPadPress (byte 10
        ///                           bit 0x02), Y2 → LGrip (byte 9 bit 0x80),
        ///                           Y3 → RPadPress (byte 10 bit 0x04),
        ///                           M3 → RGrip (byte 10 bit 0x01). Mode → Steam,
        ///                           Share → unused (no DS-style touch click).
        ///
        /// Legion's right touchpad coords feed RPadX/Y (Steam Controller's
        /// right touchpad). LPadX/Y stays at zero — Gordon's left touchpad has
        /// no physical analog on the Legion.
        /// </summary>
        private byte[] BuildSteamControllerInput(ViiperXInputGamepad gp)
        {
            var data = new byte[64];
            data[0] = 0x01;
            data[1] = 0x00;
            data[2] = 0x01;     // InputReportID — Gordon's input report ID
            data[3] = 0x3C;     // payload length placeholder (60); device parses by offset
            unchecked { gordonFrameCounter++; }
            WriteU32(data, 4, gordonFrameCounter);

            ushort aux = currentAuxButtons;

            // Byte 8 — A/X/B/Y/L1/R1/L2digital/R2digital.
            byte b8 = 0;
            if ((gp.Buttons & ViiperXInput.A) != 0) b8 |= 0x80;
            if ((gp.Buttons & ViiperXInput.X) != 0) b8 |= 0x40;
            if ((gp.Buttons & ViiperXInput.B) != 0) b8 |= 0x20;
            if ((gp.Buttons & ViiperXInput.Y) != 0) b8 |= 0x10;
            if ((gp.Buttons & ViiperXInput.LB) != 0) b8 |= 0x08;
            if ((gp.Buttons & ViiperXInput.RB) != 0) b8 |= 0x04;
            if (gp.LeftTrigger > 30) b8 |= 0x02;
            if (gp.RightTrigger > 30) b8 |= 0x01;
            data[8] = b8;

            // Byte 9 — Up/Right/Left/Down/Menu/Steam/Options/LGrip.
            byte b9 = 0;
            if ((gp.Buttons & ViiperXInput.DPadUp) != 0) b9 |= 0x01;
            if ((gp.Buttons & ViiperXInput.DPadRight) != 0) b9 |= 0x02;
            if ((gp.Buttons & ViiperXInput.DPadLeft) != 0) b9 |= 0x04;
            if ((gp.Buttons & ViiperXInput.DPadDown) != 0) b9 |= 0x08;
            if ((gp.Buttons & ViiperXInput.Start) != 0) b9 |= 0x10;             // Menu
            if (((gp.Buttons & ViiperXInput.Guide) != 0)
                || ((aux & LegionAux.Mode) != 0)) b9 |= 0x20;                   // Steam
            if ((gp.Buttons & ViiperXInput.Back) != 0) b9 |= 0x40;              // Options
            if ((aux & LegionAux.Y2) != 0) b9 |= 0x80;                          // LGrip (Y2 = back L lower)
            data[9] = b9;

            // Byte 10 — RGrip/LPadPress/RPadPress/LPadTouch/RPadTouch/L3/LPadAndJoy.
            byte b10 = 0;
            if ((aux & LegionAux.M3) != 0) b10 |= 0x01;                         // RGrip (M3 = back R lower)
            if ((aux & LegionAux.Y1) != 0) b10 |= 0x02;                         // LPadPress (Y1 = back L upper)
            if ((aux & LegionAux.Y3) != 0) b10 |= 0x04;                         // RPadPress (Y3 = back R upper)
            if (currentTouchActive)        b10 |= 0x10;                         // RPadTouch (Legion's pad → Gordon's right pad)
            if ((gp.Buttons & ViiperXInput.LeftThumb) != 0) b10 |= 0x40;        // L3
            // LPadAndJoy (0x80) stays off — we route XInput LStick to the LStick slot, not the LPad slot.
            data[10] = b10;

            // Bytes 11/12 — analog trigger single-byte slots (Gordon writes them
            // alongside the 16-bit slots at 24..28).
            data[11] = gp.LeftTrigger;
            data[12] = gp.RightTrigger;

            // Bytes 20..24 — RPadX/Y (XInput right stick → right touchpad coords).
            WriteI16(data, 20, gp.ThumbRX);
            WriteI16(data, 22, gp.ThumbRY);

            // Bytes 24..28 — LTrigger/RTrigger u16 LE.
            WriteU16(data, 24, (ushort)(gp.LeftTrigger * 257));
            WriteU16(data, 26, (ushort)(gp.RightTrigger * 257));

            // Bytes 28..40 — Accel X/Y/Z then Gyro X/Y/Z (raw counts).
            short gx, gy, gz, ax, ay, az;
            if (TryBuildImuCounts(out gx, out gy, out gz, out ax, out ay, out az))
            {
                WriteI16(data, 28, ax);
                WriteI16(data, 30, ay);
                WriteI16(data, 32, az);
                WriteI16(data, 34, gx);
                WriteI16(data, 36, gy);
                WriteI16(data, 38, gz);
            }
            // Bytes 40..48 — gyroQuat W/X/Y/Z left zero (we don't emit a quaternion).

            // Bytes 50..54 — duplicate trigger slots Gordon firmware also fills.
            WriteU16(data, 50, (ushort)(gp.LeftTrigger * 257));
            WriteU16(data, 52, (ushort)(gp.RightTrigger * 257));

            // Bytes 54..58 — LStickX/Y (Gordon's only physical stick).
            WriteI16(data, 54, gp.ThumbLX);
            WriteI16(data, 56, gp.ThumbLY);

            // Bytes 58..62 — LPadX/Y (left touchpad — Legion has no left pad,
            // leave zero). Bytes 62..64 = BatteryMilliVolts; leave zero (wired
            // Gordon reports zero too).
            return data;
        }

        /// <summary>
        /// DualSense (non-Edge, PID 0x0CE6) wire format from libviiper
        /// device/dualsense/inputstate.go. Same 33-byte layout as DSE through
        /// offset 15 — sticks (i8×4) + buttons (u32) + dpad (u8) + L2/R2 (u8)
        /// + touch1X/Y (u16×2) + touchActive (bool). At offsets 16..20 DSE
        /// has touch2 (5 bytes); DS5 has 5 reserved bytes there. The IMU
        /// block at 21..32 is identical (gyro X/Y/Z + accel X/Y/Z, int16
        /// each, accel pre-scaled by ScaleDs4Accel). Skipping touch2 leaves
        /// those bytes zero, which matches DS5's reserved-bytes contract.
        ///
        /// XInput → DS5 button mapping mirrors BuildDualSenseEdgeInput but
        /// without the four paddle bits (0x00100000-0x00800000) — the
        /// non-Edge DS has no paddles, so those bits are unused in its
        /// Buttons u32. Legion paddles still map onto PS/Touchpad via
        /// Mode/Share so the user keeps some back-button signal.
        /// </summary>
        private byte[] BuildDualSenseInput(ViiperXInputGamepad gp)
        {
            var data = new byte[33];
            data[0] = (byte)(gp.ThumbLX >> 8);
            data[1] = (byte)(NegateClamp(gp.ThumbLY) >> 8);
            data[2] = (byte)(gp.ThumbRX >> 8);
            data[3] = (byte)(NegateClamp(gp.ThumbRY) >> 8);

            uint dsButtons = 0;
            if ((gp.Buttons & ViiperXInput.A) != 0) dsButtons |= 0x0020;
            if ((gp.Buttons & ViiperXInput.B) != 0) dsButtons |= 0x0040;
            if ((gp.Buttons & ViiperXInput.X) != 0) dsButtons |= 0x0010;
            if ((gp.Buttons & ViiperXInput.Y) != 0) dsButtons |= 0x0080;
            if ((gp.Buttons & ViiperXInput.LB) != 0) dsButtons |= 0x0100;
            if ((gp.Buttons & ViiperXInput.RB) != 0) dsButtons |= 0x0200;
            if ((gp.Buttons & ViiperXInput.Back) != 0) dsButtons |= 0x1000;       // Create
            if ((gp.Buttons & ViiperXInput.Start) != 0) dsButtons |= 0x2000;      // Options
            if ((gp.Buttons & ViiperXInput.LeftThumb) != 0) dsButtons |= 0x4000;  // L3
            if ((gp.Buttons & ViiperXInput.RightThumb) != 0) dsButtons |= 0x8000; // R3
            if ((gp.Buttons & ViiperXInput.Guide) != 0) dsButtons |= 0x00010000;  // PS

            ushort aux = currentAuxButtons;
            if ((aux & LegionAux.Mode) != 0) dsButtons |= 0x00010000;             // Mode  → PS
            if ((aux & LegionAux.Share) != 0) dsButtons |= 0x00020000;            // Share → Touchpad click
            WriteU32(data, 4, dsButtons);

            byte dpad = 0;
            if ((gp.Buttons & ViiperXInput.DPadUp) != 0) dpad |= 0x01;
            if ((gp.Buttons & ViiperXInput.DPadDown) != 0) dpad |= 0x02;
            if ((gp.Buttons & ViiperXInput.DPadLeft) != 0) dpad |= 0x04;
            if ((gp.Buttons & ViiperXInput.DPadRight) != 0) dpad |= 0x08;
            data[8] = dpad;

            data[9] = gp.LeftTrigger;
            data[10] = gp.RightTrigger;

            // DS5 single touch slot (offsets 11-15: X u16, Y u16, active bool).
            if (currentTouchActive)
            {
                ushort tx = ScaleTouchAxis(currentTouchX, 1920);
                ushort ty = ScaleTouchAxis(currentTouchY, 1080);
                WriteU16(data, 11, tx);
                WriteU16(data, 13, ty);
                data[15] = 1;
            }
            // Offsets 16..20 stay zero — reserved bytes on DS5 wire.

            // IMU at 21..32, same scaling and yaw/roll (gy<->gz) frame swap as DSE/DS4.
            // ViiperAlternateGyroConvention honored same as DS4 builder: default
            // = native-correct (yaw=+gz, roll=-gy); on = upstream/legacy
            // (yaw=-gz, roll=+gy) for Steam Input gyro-to-mouse.
            short gx, gy, gz, ax, ay, az;
            if (TryBuildImuCounts(out gx, out gy, out gz, out ax, out ay, out az))
            {
                bool altConvention = XboxGamingBarHelper.Settings.SettingsManager.GetInstance()?.ViiperAlternateGyroConvention?.Value ?? false;
                WriteI16(data, 21, gx);                                                                                   // gyro pitch (X)
                WriteI16(data, 23, altConvention ? SignedClampToShort(-(int)gz) : gz);                                    // gyro yaw  — default +gz, alt -gz
                WriteI16(data, 25, altConvention ? gy                           : SignedClampToShort(-(int)gy));          // gyro roll — default -gy, alt +gy
                WriteI16(data, 27, ScaleDs4Accel(ax));
                WriteI16(data, 29, ScaleDs4Accel(SignedClampToShort(-(int)az)));                                          // accel Y-slot <- -Z
                WriteI16(data, 31, ScaleDs4Accel(ay));                                                                    // accel Z-slot <- +Y
            }
            return data;
        }

        /// <summary>
        /// Legion touchpad raw range is 0-1023 (10-bit). DS4 host expects 0-1919x942,
        /// DSE expects 0-1920x1080. Scale linearly and clamp.
        /// </summary>
        private static ushort ScaleTouchAxis(ushort raw, int maxOut)
        {
            int scaled = raw * maxOut / 1023;
            if (scaled < 0) return 0;
            if (scaled > maxOut) return (ushort)maxOut;
            return (ushort)scaled;
        }

        /// <summary>
        /// Xbox Elite 2 wire format (33 bytes). Also used for Steam Generic, Steam Deck,
        /// and Steam Controller targets since libviiper routes those through the Elite 2
        /// InputState. Reads <see cref="currentAuxButtons"/> so Legion Go back-paddles
        /// (Y1/Y2/Y3/M3), Mode and Share reach the virtual pad — without this, Steam-
        /// target emulation was silent on every back-paddle press (issue reported via
        /// Steam Generic / Steam Deck Go S profiles).
        ///
        /// Paddle bit layout matches the reference VIIPER app (aligned with HHD):
        ///   Y1 → P1 (0x1000), Y3 → P2 (0x2000), Y2 → P3 (0x4000), M3 → P4 (0x8000).
        /// Mode → Guide (0x0400); Share → reserved bit at data[13] |= 0x01.
        /// </summary>
        private byte[] BuildXboxElite2Input(ViiperXInputGamepad gp)
        {
            var data = new byte[33];
            ushort buttons = 0;
            if ((gp.Buttons & ViiperXInput.A) != 0) buttons |= 0x0001;
            if ((gp.Buttons & ViiperXInput.B) != 0) buttons |= 0x0002;
            if ((gp.Buttons & ViiperXInput.X) != 0) buttons |= 0x0004;
            if ((gp.Buttons & ViiperXInput.Y) != 0) buttons |= 0x0008;
            if ((gp.Buttons & ViiperXInput.LB) != 0) buttons |= 0x0010;
            if ((gp.Buttons & ViiperXInput.RB) != 0) buttons |= 0x0020;
            if ((gp.Buttons & ViiperXInput.Back) != 0) buttons |= 0x0040;
            if ((gp.Buttons & ViiperXInput.Start) != 0) buttons |= 0x0080;
            if ((gp.Buttons & ViiperXInput.LeftThumb) != 0) buttons |= 0x0100;
            if ((gp.Buttons & ViiperXInput.RightThumb) != 0) buttons |= 0x0200;
            if ((gp.Buttons & ViiperXInput.Guide) != 0) buttons |= 0x0400;

            // Legion aux → Elite 2 paddles + Guide.
            // libviiper's Steam path decodes the P-bits as:
            //   P1 → R5 (right lower), P2 → L5 (left lower),
            //   P3 → R4 (right upper), P4 → L4 (left upper).
            // To land each physical Legion paddle on its matching position we therefore
            // send Y1→P4, Y3→P3, Y2→P2, M3→P1 — both the L/R and the upper/lower pair
            // are flipped vs. the HHD order I originally ported, confirmed empirically
            // by re-testing on Steam Generic / Steam Deck / GO S targets.
            ushort aux = currentAuxButtons;
            if ((aux & LegionAux.Mode) != 0) buttons |= 0x0400; // Mode → Guide
            if ((aux & LegionAux.Y1) != 0)   buttons |= 0x8000; // Y1 (back L upper) → P4 (L4)
            if ((aux & LegionAux.Y3) != 0)   buttons |= 0x4000; // Y3 (back R upper) → P3 (R4)
            if ((aux & LegionAux.Y2) != 0)   buttons |= 0x2000; // Y2 (back L lower) → P2 (L5)
            if ((aux & LegionAux.M3) != 0)   buttons |= 0x1000; // M3 (back R lower) → P1 (R5)
            WriteU16(data, 0, buttons);

            data[2] = gp.LeftTrigger;
            data[3] = gp.RightTrigger;

            WriteI16(data, 4, gp.ThumbLX);
            WriteI16(data, 6, gp.ThumbLY);
            WriteI16(data, 8, gp.ThumbRX);
            WriteI16(data, 10, gp.ThumbRY);

            byte dpad = 0;
            if ((gp.Buttons & ViiperXInput.DPadUp) != 0) dpad |= 0x01;
            if ((gp.Buttons & ViiperXInput.DPadDown) != 0) dpad |= 0x02;
            if ((gp.Buttons & ViiperXInput.DPadLeft) != 0) dpad |= 0x04;
            if ((gp.Buttons & ViiperXInput.DPadRight) != 0) dpad |= 0x08;
            data[12] = dpad;

            // Share → reserved bit at byte 13 (matches reference app's mapping).
            if ((aux & LegionAux.Share) != 0) data[13] |= 0x01;

            // Bytes 14-25: IMU (gyro X/Y/Z + accel X/Y/Z as int16). libviiper's
            // xboxelite2 InputState already carries these on the wire for Steam
            // Deck / Steam Generic profiles (see vigemtest/viiper/device/
            // xboxelite2/inputstate.go:18-19) — we just hadn't been populating
            // them, so games seeing the device through Steam Deck native HID
            // path got zero gyro/accel. Reuses TryBuildImuCounts, the same
            // helper our DS4/DSE wire builders use.
            short gx, gy, gz, ax, ay, az;
            if (TryBuildImuCounts(out gx, out gy, out gz, out ax, out ay, out az))
            {
                WriteI16(data, 14, gx);
                WriteI16(data, 16, gy);
                WriteI16(data, 18, gz);
                WriteI16(data, 20, ax);
                WriteI16(data, 22, ay);
                WriteI16(data, 24, az);
            }

            // Bytes 26-32: right-touchpad (Legion's only touchpad). libviiper's
            // xboxelite2 InputState uses signed int16 centered coords for
            // Steam Deck profiles (-32768..32767, 0 = center) per the wire
            // contract in device/xboxelite2/inputstate.go:21-24. Force/
            // pressure is a uint16 — we don't have a force sensor on Legion's
            // touchpad, so set a midrange value (0x4000 ≈ 16384) when pressed
            // and 0 when only touching, matching Steam Deck's typical wire
            // emission for a non-force-sensitive press.
            // Touchpad bytes already gated by ps4TouchpadEnabled at the source
            // (forwarder zeroes currentTouchActive when disabled) so this
            // honors the user's existing toggle.
            if (currentTouchActive)
            {
                byte flags = (byte)LibViiperTouchFlags.RightPadTouch;
                if (currentTouchPressed) flags |= (byte)LibViiperTouchFlags.RightPadPress;
                data[26] = flags;
                // Convert Legion's 10-bit raw (0..1023) to libviiper's centered int16.
                // (raw - 512) * 64 maps 0..1023 → -32768..32704 — close to the
                // full int16 range without overflow at the edges.
                short padX = (short)(((int)currentTouchX - 512) * 64);
                short padY = (short)(((int)currentTouchY - 512) * 64);
                WriteI16(data, 27, padX);
                WriteI16(data, 29, padY);
                ushort force = currentTouchPressed ? (ushort)0x4000 : (ushort)0;
                WriteU16(data, 31, force);
            }

            return data;
        }

        // Mirror of the libviiper xboxelite2 InputState touch-flag bits
        // (device/xboxelite2/inputstate.go:39-40).
        [Flags]
        private enum LibViiperTouchFlags : byte
        {
            RightPadTouch = 0x01,
            RightPadPress = 0x02,
        }

        // libviiper's steamdeck device wire format (device/steamdeck/inputstate.go +
        // const.go). 64-byte report:
        //   [0]  = 0x01           magic (zero would clear Frame in UnmarshalBinary)
        //   [1]  = 0x00
        //   [2]  = 0x09           InputReportID
        //   [3]  = 0x38           DeckInputPayloadLen (56)
        //   [4..7] = uint32 LE frame counter
        //   [8]  bits: A=0x80 X=0x40 B=0x20 Y=0x10 L1=0x08 R1=0x04 L2D=0x02 R2D=0x01
        //   [9]  bits: L5=0x80 Menu=0x40 Steam=0x20 Options=0x10 Down=0x08 Left=0x04 Right=0x02 Up=0x01
        //   [10] bits: L3=0x40 RPadTouch=0x10 LPadTouch=0x08 RPadPress=0x04 LPadPress=0x02 R5=0x01
        //   [11] bits: R3=0x04
        //   [12] = unused
        //   [13] bits: RStickTouch=0x80 LStickTouch=0x40 R4=0x04 L4=0x02
        //   [14] bits: QuickAccess=0x04
        //   [15] = Reserved15
        //   [16..23]  LPadX/Y, RPadX/Y      (i16 LE × 4)
        //   [24..29]  AccelX/Y/Z            (i16 LE × 3)
        //   [30..35]  Pitch/Yaw/Roll        (raw gyro X/Y/Z, i16 LE × 3)
        //   [36..43]  GyroQuatW/X/Y/Z       (i16 LE × 4, unused — we don't ship a quaternion)
        //   [44..47]  LTrigger, RTrigger    (u16 LE × 2; XInput byte is scaled ×257 → 0..65535)
        //   [48..55]  LStickX/Y, RStickX/Y  (i16 LE × 4, passthrough from XInput sticks)
        //   [56..63]  LPadForce, RPadForce, LStickForce, RStickForce (u16 LE × 4)
        //
        // Legion aux → SteamDeck back-button mapping mirrors the pattern we already
        // ship for BuildXboxElite2Input on the old Steam Deck profile (Y1→L4,
        // Y3→R4, Y2→L5, M3→R5) so users who relied on paddles via Steam Input on
        // the legacy emulation see no behavior change.
        private byte[] BuildSteamDeckInput(ViiperXInputGamepad gp)
        {
            var data = new byte[64];
            // ValveInReportHeader: unReportVersion=0x0001 LE, ucType=ID_CONTROLLER_DECK_STATE=9,
            // ucLength=64 (NOT 56 — 56 is the Steam Controller pre-Deck report length).
            // SDL3's HIDAPI Steam Deck driver gates every report at
            // src/joystick/hidapi/SDL_hidapi_steamdeck.c:394-397 on
            //   r == 64 && unReportVersion==1 && ucType==9 && ucLength==64
            // A wrong ucLength (we previously wrote 0x38=56) silently drops EVERY report,
            // which is why SDL_GamepadHasSensor returned true (driver bound) but
            // SDL_GetGamepadSensorData was reading all zeros — the data pipeline was
            // never receiving a parsed input frame.
            data[0] = 0x01;
            data[1] = 0x00;
            data[2] = 0x09;
            data[3] = 0x40;
            // Time-based frame: real elapsed wall-clock in 4ms (Deck tick) units so Steam
            // integrates gyro against true time regardless of our send rate. See the field
            // declaration for why a per-report ++ halves gyro speed on our 125Hz HID.
            long nowFrameTicks = DateTime.UtcNow.Ticks;
            if (!steamDeckFrameAnchored)
            {
                steamDeckFrameAnchorTicks = nowFrameTicks;
                steamDeckFrameAnchored = true;
            }
            uint deckFrame = unchecked((uint)((nowFrameTicks - steamDeckFrameAnchorTicks) / SteamDeckFrameTickTicks));
            WriteU32(data, 4, deckFrame);

            ushort aux = currentAuxButtons;

            // Byte 8 — face buttons, shoulders, digital triggers.
            byte b8 = 0;
            if ((gp.Buttons & ViiperXInput.A) != 0) b8 |= 0x80;
            if ((gp.Buttons & ViiperXInput.X) != 0) b8 |= 0x40;
            if ((gp.Buttons & ViiperXInput.B) != 0) b8 |= 0x20;
            if ((gp.Buttons & ViiperXInput.Y) != 0) b8 |= 0x10;
            if ((gp.Buttons & ViiperXInput.LB) != 0) b8 |= 0x08;
            if ((gp.Buttons & ViiperXInput.RB) != 0) b8 |= 0x04;
            if (gp.LeftTrigger > 30) b8 |= 0x02;                                // XInput trigger deadzone
            if (gp.RightTrigger > 30) b8 |= 0x01;
            data[8] = b8;

            // Byte 9 — system buttons + dpad (Steam button = Guide).
            byte b9 = 0;
            if ((aux & LegionAux.Y2) != 0) b9 |= 0x80;                          // L5 (back L lower)
            if ((gp.Buttons & ViiperXInput.Start) != 0) b9 |= 0x40;             // Menu
            if (((gp.Buttons & ViiperXInput.Guide) != 0)
                || ((aux & LegionAux.Mode) != 0)) b9 |= 0x20;                   // Steam (Mode → Steam)
            if ((gp.Buttons & ViiperXInput.Back) != 0) b9 |= 0x10;              // Options
            if ((gp.Buttons & ViiperXInput.DPadDown) != 0) b9 |= 0x08;
            if ((gp.Buttons & ViiperXInput.DPadLeft) != 0) b9 |= 0x04;
            if ((gp.Buttons & ViiperXInput.DPadRight) != 0) b9 |= 0x02;
            if ((gp.Buttons & ViiperXInput.DPadUp) != 0) b9 |= 0x01;
            data[9] = b9;

            // Byte 10 — L3, touchpad touches/presses, R5.
            byte b10 = 0;
            if ((gp.Buttons & ViiperXInput.LeftThumb) != 0) b10 |= 0x40;        // L3
            if ((aux & LegionAux.M3) != 0) b10 |= 0x01;                         // R5 (back R lower)
            if (currentTouchActive) b10 |= 0x10;                                // RPadTouch
            if (currentTouchPressed) b10 |= 0x04;                               // RPadPress
            data[10] = b10;

            // Byte 11 — R3.
            byte b11 = 0;
            if ((gp.Buttons & ViiperXInput.RightThumb) != 0) b11 |= 0x04;
            data[11] = b11;

            // Byte 13 — stick-cap touches + L4/R4 (upper paddles).
            byte b13 = 0;
            if ((aux & LegionAux.Y3) != 0) b13 |= 0x04;                         // R4 (back R upper)
            if ((aux & LegionAux.Y1) != 0) b13 |= 0x02;                         // L4 (back L upper)
            data[13] = b13;

            // Byte 14 — QuickAccess (Legion's Share button if present, else unused).
            if ((aux & LegionAux.Share) != 0) data[14] |= 0x04;

            // Touchpad coords (Legion has only the right touchpad). Left pad bytes
            // stay zero. Right pad maps to libviiper's steamdeck wire slots at
            // offsets 20-23 (RPadX/Y int16 LE).
            //
            // Y-axis sign: Steam Deck convention is +32767 = top of pad, -32768 =
            // bottom (joystick-style, Y-up). Legion's raw touchpad reports
            // screen-style coords with Y=0 at the top, Y=1023 at the bottom — so
            // an un-inverted (currentY - 512) * 64 produces NEGATIVE values when
            // the user touches the top of the pad, which Steam/SDL then interprets
            // as a downward swipe. Negate to convert screen-up → wire-up.
            //
            // Horizontal axis matches directly (both Legion and Steam Deck use
            // X=0..1023 left-to-right / -32768..+32767 left-to-right).
            if (currentTouchActive)
            {
                short padX = (short)(((int)currentTouchX - 512) * 64);
                short padY = (short)((512 - (int)currentTouchY) * 64);          // Y inverted: Legion 0=top → wire +32767
                WriteI16(data, 20, padX);
                WriteI16(data, 22, padY);
                if (currentTouchPressed)
                {
                    WriteU16(data, 58, (ushort)0x4000);                         // RPadForce midrange when pressed
                }
            }

            // IMU — accel at 24..29, raw gyro at 30..35 (the Pitch/Yaw/Roll slots).
            // Quaternion slots stay zero; Steam Input synthesizes orientation from
            // raw gyro when the quaternion is null. Same TryBuildImuCounts helper
            // the DS4/DSE/Elite2 builders use, so gyro behavior matches across
            // backends for the same Legion controller.
            short gx, gy, gz, ax, ay, az;
            if (TryBuildImuCounts(out gx, out gy, out gz, out ax, out ay, out az))
            {
                // 2026-06-01 — gyro and accel rotated together so Steam's sensor
                // fusion sees a coherent frame. Earlier "gyro-only rotation" left
                // gravity on the wrong device axis vs the rotated gyro frame, so
                // Steam's gyro calibration attributed part of every physical yaw to
                // its roll channel (yaw axis didn't align with the accel-derived
                // "down" vector). Applying the same 90deg-about-X rotation
                // (Y,Z) -> (-Z,+Y) to both gyro AND accel keeps the gravity vector
                // and the yaw axis perpendicular, which is what the fusion expects.
                //
                // Toggle ON (ViiperAlternateGyroConvention) flips both back to plain
                // pass-through (1:1 in SdlGyroTester via SDL3's internal driver remap).
                //
                // Accel magnitude: Legion BMI is +/-8g and Deck wire is +/-2g, so
                // multiply by SteamDeckAccelScale (=4) to reach Deck-native gravity.
                bool altConvention = XboxGamingBarHelper.Settings.SettingsManager.GetInstance()?.ViiperAlternateGyroConvention?.Value ?? false;
                int axOut = (int)ax;
                int ayOut = altConvention ? (int)ay : -(int)az;   // default rotated, alt pass-through
                int azOut = altConvention ? (int)az :  (int)ay;   // default rotated, alt pass-through
                short wAx = SignedClampToShort(axOut * SteamDeckAccelScale);
                short wAy = SignedClampToShort(ayOut * SteamDeckAccelScale);
                short wAz = SignedClampToShort(azOut * SteamDeckAccelScale);
                short wGx = gx;
                short wGy = altConvention ? gy : SignedClampToShort(-(int)gz);  // default rotated yaw=-gz, alt pass-through
                short wGz = altConvention ? gz : gy;                            // default rotated roll=+gy, alt pass-through
                WriteI16(data, 24, wAx);
                WriteI16(data, 26, wAy);
                WriteI16(data, 28, wAz);
                WriteI16(data, 30, wGx);
                WriteI16(data, 32, wGy);
                WriteI16(data, 34, wGz);
            }

            // Triggers — XInput is 0..255; the Steam Deck wire trigger field is a SIGNED
            // i16 with full-scale 32767 (matches HHD's "rt"/"lt" AM(i16) encode: 32767*val).
            // The old ×257 produced 0..65535: anything past raw 127 (~half press) exceeded
            // 32767 and wrapped NEGATIVE when Steam read it as signed (full press = 0xFFFF =
            // -1 -> clamped to 0), so a full pull registered as released and only ~half read
            // as max. Scale to 0..32767 instead. (Live capture 2026-05-28 confirmed raw hits
            // a clean 255.)
            WriteU16(data, 44, (ushort)(gp.LeftTrigger * 32767 / 255));
            WriteU16(data, 46, (ushort)(gp.RightTrigger * 32767 / 255));

            // Sticks — XInput int16 -> wire int16 passthrough.
            WriteI16(data, 48, gp.ThumbLX);
            WriteI16(data, 50, gp.ThumbLY);
            WriteI16(data, 52, gp.ThumbRX);
            WriteI16(data, 54, gp.ThumbRY);

            return data;
        }

        // -------------------------------------------------------------------
        // Wire helpers (replacements for BitConverter.TryWriteBytes/Span which
        // aren't available on .NET Framework 4.8)
        // -------------------------------------------------------------------

        private static void WriteU16(byte[] buf, int offset, ushort value)
        {
            buf[offset] = (byte)(value & 0xFF);
            buf[offset + 1] = (byte)((value >> 8) & 0xFF);
        }

        private static void WriteI16(byte[] buf, int offset, short value)
        {
            buf[offset] = (byte)(value & 0xFF);
            buf[offset + 1] = (byte)((value >> 8) & 0xFF);
        }

        private static void WriteU32(byte[] buf, int offset, uint value)
        {
            buf[offset] = (byte)(value & 0xFF);
            buf[offset + 1] = (byte)((value >> 8) & 0xFF);
            buf[offset + 2] = (byte)((value >> 16) & 0xFF);
            buf[offset + 3] = (byte)((value >> 24) & 0xFF);
        }

        private static int NegateClamp(short value)
        {
            int neg = -(int)value;
            if (neg > short.MaxValue) neg = short.MaxValue;
            if (neg < short.MinValue) neg = short.MinValue;
            return neg;
        }
    }
}
