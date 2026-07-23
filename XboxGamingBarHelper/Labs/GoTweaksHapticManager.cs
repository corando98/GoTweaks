using System;
using System.Threading;
using NLog;
using XboxGamingBarHelper.ControllerEmulation.Viiper;

namespace XboxGamingBarHelper.Labs
{
    /// <summary>
    /// Standalone button haptics ("GoTweaks Haptics"). On each bound Legion button press
    /// edge, plays a short crisp LRA click on the physical controller motors — the same
    /// envelope we tuned for the Steam Deck trigger haptic (~20ms on-time so the LRA spins
    /// up to a uniform click, then a hard stop). Works independent of VIIPER emulation by
    /// driving the Legion controller's own XInput slot directly. Per-group enable +
    /// intensity (ABXY / front / back / triggers).
    /// </summary>
    internal sealed class GoTweaksHapticManager : IDisposable
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private const ushort LegionVendorId = 0x17EF;
        // Pulse so the LRA produces a single tactile tap rather than a sustained buzz. Default
        // 10ms; user-tunable 4..40ms via the Pulse Length slider. (The 20ms SteamDeck value rang
        // on these motors; ~10ms reads as a crisp click.)
        private const int DefaultClickOnTimeMs = 10;
        private const int MinClickOnTimeMs = 4;
        private const int MaxClickOnTimeMs = 40;
        private int _clickOnTimeMs = DefaultClickOnTimeMs;
        private const int TickMs = 5;                     // stop-flush resolution
        private const long SlotRefreshTicks = TimeSpan.TicksPerSecond * 3; // re-probe slot every 3s

        private readonly object _stateLock = new object();

        private bool _enabled;
        // Per-group enable + intensity (0..100). Index by (int)LegionButtonGroup.
        private readonly bool[] _groupEnabled = new bool[5];
        private readonly int[] _groupIntensity = { 0, 100, 100, 100, 100 };
        // When true, also fire a click on the button RELEASE edge (not just press).
        private bool _clickOnRelease;

        private int _slot = -1;
        private long _lastSlotProbeTicks;

        // Active click state: motor strength + expiry.
        private byte _motorStrength;
        private long _motorExpiresTicks;
        private bool _motorActive;

        // Confirmation-pulse sequencer: back-to-back holds fire pulses microseconds apart;
        // just extending the active pulse blends them into one continuous buzz so the second
        // isn't felt (field report 2026-07-23). Queue them and play each with a forced OFF
        // gap so every confirmation is a distinct tap.
        private readonly System.Collections.Generic.Queue<(byte strength, int durMs)> _confirmQueue
            = new System.Collections.Generic.Queue<(byte, int)>();
        private long _confirmNextAllowedTicks;   // don't start the next queued pulse before this
        private const int ConfirmGapMs = 45;

        private Thread _tickThread;
        private volatile bool _running;
        private bool _edgeHooked;

        /// <summary>
        /// Parse and apply the delimited config string from GoTweaksHapticsConfigProperty:
        ///   "&lt;masterOn0/1&gt;|face:&lt;on&gt;,&lt;i&gt;|front:&lt;on&gt;,&lt;i&gt;|back:&lt;on&gt;,&lt;i&gt;|trigger:&lt;on&gt;,&lt;i&gt;"
        /// Applies group config first, then the master enable so threads start with state ready.
        /// </summary>
        public void ApplyConfigString(string config)
        {
            if (string.IsNullOrWhiteSpace(config))
            {
                SetEnabled(false);
                return;
            }

            string[] parts = config.Split('|');
            bool master = parts.Length > 0 && parts[0].Trim() == "1";

            for (int i = 1; i < parts.Length; i++)
            {
                string seg = parts[i].Trim();
                int colon = seg.IndexOf(':');
                if (colon <= 0) continue;
                string name = seg.Substring(0, colon).Trim().ToLowerInvariant();
                string[] kv = seg.Substring(colon + 1).Split(',');
                bool on = kv.Length > 0 && kv[0].Trim() == "1";
                int intensity = kv.Length > 1 && int.TryParse(kv[1].Trim(), out int v) ? v : 100;

                if (name == "rel")
                {
                    lock (_stateLock) { _clickOnRelease = on; }
                    continue;
                }
                if (name == "pulse")
                {
                    int ms = kv.Length > 0 && int.TryParse(kv[0].Trim(), out int pm) ? pm : DefaultClickOnTimeMs;
                    ms = Math.Min(Math.Max(ms, MinClickOnTimeMs), MaxClickOnTimeMs);
                    lock (_stateLock) { _clickOnTimeMs = ms; }
                    continue;
                }

                LegionButtonGroup group;
                switch (name)
                {
                    case "face": group = LegionButtonGroup.Face; break;
                    case "front": group = LegionButtonGroup.Front; break;
                    case "back": group = LegionButtonGroup.Back; break;
                    case "trigger": group = LegionButtonGroup.Trigger; break;
                    default: continue;
                }
                ConfigureGroup(group, on, intensity);
            }

            SetEnabled(master);
        }

        public void SetEnabled(bool enabled)
        {
            lock (_stateLock) { _enabled = enabled; }
            if (enabled)
            {
                EnsureEdgeHooked();
                StartTickThread();
                ProbeSlot(force: true);
            }
            else
            {
                EnsureEdgeUnhooked();
                StopMotorNow();
                StopTickThread();
            }
        }

        public void ConfigureGroup(LegionButtonGroup group, bool enabled, int intensityPercent)
        {
            int idx = (int)group;
            if (idx < 0 || idx >= _groupEnabled.Length) return;
            lock (_stateLock)
            {
                _groupEnabled[idx] = enabled;
                _groupIntensity[idx] = Math.Min(Math.Max(intensityPercent, 0), 100);
            }
        }

        private void EnsureEdgeHooked()
        {
            if (_edgeHooked) return;
            LegionButtonMonitor.ButtonEdge += OnButtonEdge;
            _edgeHooked = true;
        }

        private void EnsureEdgeUnhooked()
        {
            if (!_edgeHooked) return;
            LegionButtonMonitor.ButtonEdge -= OnButtonEdge;
            _edgeHooked = false;
        }

        private void OnButtonEdge(object sender, LegionButtonEdgeEventArgs e)
        {
            int idx = (int)e.Group;
            int intensity;
            int pulseMs;
            lock (_stateLock)
            {
                if (!_enabled) return;
                // Press always clicks; release only when the user enabled on-release.
                if (!e.Pressed && !_clickOnRelease) return;
                if (idx < 0 || idx >= _groupEnabled.Length || !_groupEnabled[idx]) return;
                intensity = _groupIntensity[idx];
                pulseMs = _clickOnTimeMs;
            }
            if (intensity <= 0) return;

            // Map intensity 0..100 to motor strength 0..255, then latch with the tap expiry.
            byte strength = (byte)Math.Min(255, intensity * 255 / 100);
            long expires = DateTime.UtcNow.Ticks + pulseMs * TimeSpan.TicksPerMillisecond;
            lock (_stateLock)
            {
                _motorStrength = strength;
                _motorExpiresTicks = expires;
                _motorActive = true;
            }
            // Drive immediately for lowest latency; the tick thread handles the stop.
            EmitMotor(strength);
        }

        private void StartTickThread()
        {
            if (_running) return;
            _running = true;
            _tickThread = new Thread(TickLoop) { IsBackground = true, Name = "GoTweaksHapticTick" };
            _tickThread.Start();
        }

        private void StopTickThread()
        {
            _running = false;
            var t = _tickThread;
            _tickThread = null;
            if (t != null && t.IsAlive && t.ManagedThreadId != Thread.CurrentThread.ManagedThreadId)
            {
                try { t.Join(200); } catch { }
            }
        }

        private void TickLoop()
        {
            while (_running)
            {
                long now = DateTime.UtcNow.Ticks;

                bool stop = false;
                bool startNext = false;
                byte nextStrength = 0;
                lock (_stateLock)
                {
                    if (_motorActive && now >= _motorExpiresTicks)
                    {
                        _motorActive = false;
                        _motorStrength = 0;
                        stop = true;
                        // Enforce the OFF gap before the next queued confirmation.
                        _confirmNextAllowedTicks = now + ConfirmGapMs * TimeSpan.TicksPerMillisecond;
                    }
                    // Start the next queued confirmation pulse once the motor is idle and
                    // the inter-pulse gap has elapsed.
                    if (!_motorActive && _confirmQueue.Count > 0 && now >= _confirmNextAllowedTicks)
                    {
                        var (s, dur) = _confirmQueue.Dequeue();
                        _motorStrength = s;
                        _motorExpiresTicks = now + dur * TimeSpan.TicksPerMillisecond;
                        _motorActive = true;
                        startNext = true;
                        nextStrength = s;
                    }
                }
                if (stop) EmitMotor(0);
                if (startNext) EmitMotor(nextStrength);

                if (now - _lastSlotProbeTicks >= SlotRefreshTicks)
                {
                    ProbeSlot(force: false);
                }

                Thread.Sleep(TickMs);
            }
        }

        /// <summary>
        /// Scan XInput slots 0..3 for the Legion controller (VID 0x17EF) and cache its slot.
        /// Re-probed periodically because the slot can change on reconnect / emulation toggle.
        /// </summary>
        private void ProbeSlot(bool force)
        {
            _lastSlotProbeTicks = DateTime.UtcNow.Ticks;
            int found = -1;
            for (uint i = 0; i < 4; i++)
            {
                var caps = default(ViiperXInputCapabilitiesEx);
                uint rc;
                try { rc = ViiperXInput.GetCapabilitiesEx(1, i, 1, ref caps); }
                catch { return; }
                if (rc != ViiperXInput.ErrorSuccess) continue;
                if (caps.VendorId == LegionVendorId) { found = (int)i; break; }
            }
            lock (_stateLock)
            {
                if (found != _slot)
                {
                    _slot = found;
                    Logger.Info($"GoTweaksHaptics: Legion XInput slot = {(_slot < 0 ? "none" : _slot.ToString())}");
                }
            }
        }

        /// <summary>
        /// One-shot confirmation buzz for executed actions (Legion L/R hold bindings,
        /// quick-tile combos, System-tab hotkeys). Independent of the per-button click
        /// engine: fires even when GoTweaks Haptics is disabled, since it confirms an
        /// action rather than echoing a button press. Slightly longer than a click so
        /// it reads as "something happened" (default 70ms @ ~55%).
        /// </summary>
        public void PlayConfirmationPulse(int intensityPercent = 55, int durationMs = 70)
        {
            try
            {
                if (intensityPercent <= 0 || durationMs <= 0) return;
                byte strength = (byte)Math.Min(255, intensityPercent * 255 / 100);

                // The tick thread owns the pulse cutoff, gap sequencing and slot re-probing;
                // make sure it runs even if the click engine was never enabled.
                StartTickThread();
                bool needProbe;
                bool playNow;
                long now = DateTime.UtcNow.Ticks;
                lock (_stateLock)
                {
                    needProbe = _slot < 0;
                    // If nothing is playing and no gap is pending, play immediately for lowest
                    // latency; otherwise queue so the tick loop sequences it after the gap.
                    playNow = !_motorActive && _confirmQueue.Count == 0 && now >= _confirmNextAllowedTicks;
                    if (playNow)
                    {
                        _motorStrength = strength;
                        _motorExpiresTicks = now + durationMs * TimeSpan.TicksPerMillisecond;
                        _motorActive = true;
                    }
                    else if (_confirmQueue.Count < 4) // cap so a mash doesn't buzz for seconds
                    {
                        _confirmQueue.Enqueue((strength, durationMs));
                    }
                }
                if (needProbe) ProbeSlot(force: true);
                if (playNow) EmitMotor(strength);
            }
            catch (Exception ex)
            {
                Logger.Debug($"GoTweaksHaptics: confirmation pulse failed: {ex.Message}");
            }
        }

        private void EmitMotor(byte strength)
        {
            int slot;
            lock (_stateLock) { slot = _slot; }
            if (slot < 0) return;

            ushort speed = (ushort)(strength * 257); // 0..255 -> 0..65535
            var vib = new ViiperXInputVibration { LeftMotorSpeed = speed, RightMotorSpeed = speed };
            try { ViiperXInput.SetState((uint)slot, ref vib); }
            catch (Exception ex) { Logger.Debug($"GoTweaksHaptics: SetState failed: {ex.Message}"); }
        }

        private void StopMotorNow()
        {
            lock (_stateLock) { _motorActive = false; _motorStrength = 0; }
            EmitMotor(0);
        }

        public void Dispose()
        {
            EnsureEdgeUnhooked();
            StopMotorNow();
            StopTickThread();
        }
    }
}
