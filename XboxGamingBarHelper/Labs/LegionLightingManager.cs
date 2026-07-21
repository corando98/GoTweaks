using System;
using System.Threading;
using NLog;
using XboxGamingBarHelper.Devices.Libraries.Legion;

namespace XboxGamingBarHelper.Labs
{
    internal enum LegionReactiveMode
    {
        Disabled = 0,       // no reactive effect — static lighting (Legion tab) owns the RGB
        FlashOnPress = 1,   // flash to flash-color on press, decay back to the static color
        CyclePalette = 2,   // each press flashes the next palette color
        PerButtonColor = 3, // flash color depends on which button was pressed
        HueAdvance = 4,     // each press rotates the flash hue smoothly around the wheel
        TriggerGradient = 5,// continuous: LT/RT analog pull ramps static color -> flash color
        BatteryIndicator = 6,// continuous (ambient): color reflects controller charge
        CpuTempIndicator = 7,// continuous (ambient): color reflects CPU temperature
    }

    /// <summary>
    /// Reactive (input-driven) Legion controller lighting. Static modes (solid/pulse/rainbow/
    /// spiral) remain owned by the existing LegionManager static-lighting path; this only adds
    /// the press-reactive layer on top, driven by the shared
    /// <see cref="LegionButtonMonitor.ButtonEdge"/> stream. While a reactive mode is active the
    /// effect loop momentarily overrides the solid color via LegionManager.SetReactiveStickColor;
    /// when a flash fully decays (or the mode is disabled) it calls ReleaseReactiveLighting to
    /// hand the RGB back to the user's static setting. Single RGB writer (LegionControllerService).
    /// </summary>
    internal sealed class LegionLightingManager : IDisposable
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private const int EffectTickMs = 16;       // ~60Hz
        // Below the tick period on purpose: with the two equal (16/16), scheduler jitter made
        // roughly every other tick land just inside the window and get DROPPED by the rate
        // limiter — which is why flash decays sometimes eased back smoothly and sometimes
        // snapped (half the fade frames were missing, luck of the timing).
        private const int WriteMinIntervalMs = 8;
        // Re-assert an unchanged color periodically. The firmware can drop a write under the
        // effect-loop's sustained HID traffic; with pure change-detection the engine then
        // believes the light shows X while the hardware stuck at an older frame — the
        // "trigger effect stuck on the flash color" report. A periodic keepalive rewrite
        // self-heals within a second.
        private const int KeepaliveMs = 750;

        private readonly object _stateLock = new object();
        private readonly LegionManager _legion;

        private LegionReactiveMode _mode = LegionReactiveMode.Disabled;
        private (byte r, byte g, byte b) _flashColor = (255, 255, 255);
        private float _decaySeconds = 0.5f;
        private (byte r, byte g, byte b)[] _palette = DefaultPalette();
        private int _paletteIndex;
        // Current hue (degrees 0..360) for HueAdvance; advances a fixed step per press.
        private float _hueDegrees;
        private const float HueStepPerPress = 40f;

        // Live flash level (0..1) and the color currently flashing.
        private float _flashLevel;
        private (byte r, byte g, byte b) _activeFlashColor;
        private bool _releasedSinceFlash = true; // true once we've handed RGB back to static
        // Number of reactive buttons currently held. While > 0 the flash is held at full
        // (hold-aware, like the haptic release option); decay only runs once all are released.
        private int _heldCount;
        // TriggerGradient: true while the trigger is meaningfully pulled. On the pulled->rest
        // transition the engine RELEASES the RGB back to the static lighting (full profile
        // restore) instead of painting solid idle frames forever — that both lets animated
        // static modes (pulse/rainbow/spiral) run at rest and makes the return authoritative
        // rather than dependent on a single dedupe-able solid write.
        private bool _triggerEngaged;

        private Thread _effectThread;
        private volatile bool _running;
        private bool _edgeHooked;
        private long _lastWriteTicks;
        private (byte r, byte g, byte b) _lastSent = (1, 2, 3);
        // True once an effect actually started running (we took over the lighting from the static
        // path). The Disabled-branch release is only meaningful after a takeover; on a cold init
        // with persisted "disabled" the release would gratuitously re-apply the firmware-cached
        // light state (briefly differing from the widget's saved state) — the visible "white flash"
        // users saw after Kill/Update (#81 white stick flash).
        private bool _hasTakenOver;

        public LegionLightingManager(LegionManager legion)
        {
            _legion = legion ?? throw new ArgumentNullException(nameof(legion));
        }

        private static (byte, byte, byte)[] DefaultPalette()
        {
            return new (byte, byte, byte)[]
            {
                (255, 0, 0), (255, 128, 0), (255, 255, 0), (0, 255, 0),
                (0, 255, 255), (0, 0, 255), (180, 0, 255), (255, 0, 180),
            };
        }

        /// <summary>
        /// Parse the reactive portion of the lighting config string. The static portion
        /// (solid/pulse/rainbow/spiral, base color, brightness, speed) is owned by the Legion-tab
        /// lighting and ignored here. Format:
        ///   "&lt;mode&gt;|&lt;baseHex&gt;|&lt;flashHex&gt;|&lt;decayMs&gt;|&lt;brightness&gt;|&lt;speed&gt;"
        /// Only "flash"/"cycle"/"perbutton" map to a reactive mode; everything else = Disabled.
        /// </summary>
        public void ApplyConfigString(string config)
        {
            if (string.IsNullOrWhiteSpace(config))
            {
                Configure(LegionReactiveMode.Disabled, (255, 255, 255), 0.5f);
                return;
            }

            string[] p = config.Split('|');
            LegionReactiveMode mode = ParseMode(p.Length > 0 ? p[0] : "");
            var flashColor = ParseHex(p.Length > 2 ? p[2] : "FFFFFF");
            float decaySec = ParseInt(p.Length > 3 ? p[3] : "500", 500) / 1000f;

            // Optional user palette field "pal:RRGGBB,RRGGBB,..." — used by CyclePalette. Order
            // of fields after index 5 isn't fixed, so scan for the pal: token.
            for (int i = 6; i < p.Length; i++)
            {
                string seg = (p[i] ?? "").Trim();
                if (seg.StartsWith("pal:", StringComparison.OrdinalIgnoreCase))
                {
                    var colors = ParsePalette(seg.Substring(4));
                    if (colors != null && colors.Length > 0) SetPalette(colors);
                    break;
                }
            }

            Configure(mode, flashColor, decaySec);
        }

        private static (byte r, byte g, byte b)[] ParsePalette(string csv)
        {
            if (string.IsNullOrWhiteSpace(csv)) return null;
            var parts = csv.Split(',');
            var list = new System.Collections.Generic.List<(byte, byte, byte)>(parts.Length);
            foreach (var raw in parts)
            {
                string h = (raw ?? "").Trim().TrimStart('#');
                if (h.Length == 6
                    && byte.TryParse(h.Substring(0, 2), System.Globalization.NumberStyles.HexNumber, null, out byte r)
                    && byte.TryParse(h.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, null, out byte g)
                    && byte.TryParse(h.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, null, out byte b))
                {
                    list.Add((r, g, b));
                }
            }
            return list.Count > 0 ? list.ToArray() : null;
        }

        private static LegionReactiveMode ParseMode(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "flash": return LegionReactiveMode.FlashOnPress;
                case "cycle": return LegionReactiveMode.CyclePalette;
                case "perbutton": return LegionReactiveMode.PerButtonColor;
                case "hue": return LegionReactiveMode.HueAdvance;
                case "trigger": return LegionReactiveMode.TriggerGradient;
                case "battery": return LegionReactiveMode.BatteryIndicator;
                case "cputemp": return LegionReactiveMode.CpuTempIndicator;
                default: return LegionReactiveMode.Disabled;
            }
        }

        public void Configure(LegionReactiveMode mode, (byte r, byte g, byte b) flashColor, float decaySeconds)
        {
            lock (_stateLock)
            {
                _mode = mode;
                _flashColor = flashColor;
                _decaySeconds = Math.Max(0.05f, decaySeconds);
            }
            ApplyModeChange();
        }

        public void SetPalette((byte r, byte g, byte b)[] palette)
        {
            if (palette == null || palette.Length == 0) return;
            lock (_stateLock) { _palette = palette; _paletteIndex = 0; }
        }

        private void ApplyModeChange()
        {
            LegionReactiveMode mode;
            lock (_stateLock) { mode = _mode; }

            if (mode == LegionReactiveMode.Disabled)
            {
                EnsureEdgeUnhooked();
                StopEffectThread();
                // Hand RGB back to the user's static lighting only if a reactive effect actually
                // took over the lighting at some point. On a cold init with persisted Disabled the
                // reactive manager never touched the RGB, so releasing here would just re-assert
                // whatever the firmware cached — which differs from the widget's saved state until
                // wave-2 sync arrives, producing the visible "white flash" (#81).
                if (_hasTakenOver)
                {
                    try { _legion.ReleaseReactiveLighting(); } catch { }
                    _hasTakenOver = false;
                }
                return;
            }

            // Press-driven modes need the edge stream; continuous modes (trigger/battery) don't.
            if (IsPressDriven(mode)) EnsureEdgeHooked();
            else EnsureEdgeUnhooked();
            StartEffectThread();
            // From here on the effect loop will be writing RGB — mark the takeover so a later
            // transition back to Disabled will properly release to the static lighting.
            _hasTakenOver = true;
            // _releasedSinceFlash starts true: there's no pending flash to hand back to static on
            // startup. Initializing it false made the effect loop's first idle tick immediately
            // call ReleaseReactiveLighting -> RestoreLightSettings, which applied the helper's
            // default mode=Solid/brightness=100 = a white 100% flash before the widget synced the
            // user's real values (#81 brightness flash, confirmed via SDBright capture).
            lock (_stateLock) { _flashLevel = 0; _heldCount = 0; _releasedSinceFlash = true; }
            _triggerEngaged = false;
        }

        // Press-driven: flash level set on button edges and decays. Continuous: color computed
        // every tick from live state (trigger pull / battery), no edge needed.
        private static bool IsPressDriven(LegionReactiveMode m) =>
            m == LegionReactiveMode.FlashOnPress
            || m == LegionReactiveMode.CyclePalette
            || m == LegionReactiveMode.PerButtonColor
            || m == LegionReactiveMode.HueAdvance;

        private static bool IsContinuous(LegionReactiveMode m) =>
            m == LegionReactiveMode.TriggerGradient
            || m == LegionReactiveMode.BatteryIndicator
            || m == LegionReactiveMode.CpuTempIndicator;

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
            lock (_stateLock)
            {
                if (_mode == LegionReactiveMode.Disabled) return;

                if (!e.Pressed)
                {
                    // Release: a held button let go. Once none are held, the effect loop's
                    // decay resumes from full (the flash held while pressed).
                    if (_heldCount > 0) _heldCount--;
                    return;
                }

                (byte r, byte g, byte b) color;
                switch (_mode)
                {
                    case LegionReactiveMode.CyclePalette:
                        color = _palette[_paletteIndex % _palette.Length];
                        _paletteIndex++;
                        break;
                    case LegionReactiveMode.PerButtonColor:
                        color = ColorForButton(e.Button);
                        break;
                    case LegionReactiveMode.HueAdvance:
                        _hueDegrees = (_hueDegrees + HueStepPerPress) % 360f;
                        color = HsvToRgb(_hueDegrees, 1f, 1f);
                        break;
                    default: // FlashOnPress
                        color = _flashColor;
                        break;
                }
                _activeFlashColor = color;
                _flashLevel = 1f;
                _heldCount++;
                _releasedSinceFlash = false;
            }
        }

        /// <summary>
        /// Continuous-mode color (computed every tick, no decay). TriggerGradient ramps the
        /// user's static color -> flash color by the stronger of LT/RT analog pull. BatteryIndicator
        /// maps the lower controller charge to green -> amber -> red.
        /// </summary>
        private (byte r, byte g, byte b) ComputeContinuousColor(LegionReactiveMode mode)
        {
            if (mode == LegionReactiveMode.TriggerGradient)
            {
                byte lt = 0, rt = 0;
                if (LegionButtonMonitor.TryGetLatestGamepadSample(out var sample))
                {
                    lt = sample.LeftTrigger;
                    rt = sample.RightTrigger;
                }
                float pull = Math.Max(lt, rt) / 255f;     // 0..1, stronger trigger wins
                (byte r, byte g, byte b) idle, full;
                lock (_stateLock) { full = _flashColor; }
                idle = _legion.CurrentLightColorRgb;        // static color = resting state
                byte r = (byte)(idle.r + (full.r - idle.r) * pull);
                byte g = (byte)(idle.g + (full.g - idle.g) * pull);
                byte b = (byte)(idle.b + (full.b - idle.b) * pull);
                return (r, g, b);
            }

            if (mode == LegionReactiveMode.CpuTempIndicator)
            {
                // CPU temperature -> blue (cold) .. cyan .. green .. amber .. red (hot).
                // 45C and below is fully blue, 90C and above fully red — the range where
                // the APU actually lives under load, so mid-session color movement is
                // visible. Hue 240 (blue) sweeps down to 0 (red).
                int temp = _legion.LegionCPUCurrentTemp?.Value ?? 0;
                float norm = Math.Max(0f, Math.Min(1f, (temp - 45f) / 45f));
                return HsvToRgb(240f * (1f - norm), 1f, 1f);
            }

            // BatteryIndicator: controller charge normally, system battery when controllers are
            // charging/attached (LegionManager decides). 0% -> red, 100% -> green via amber
            // (hue 0 red .. 60 amber .. 120 green, continuous by percent).
            int pct = _legion.GetIndicatorBatteryPercent();
            float hue = pct * 1.2f; // 0 -> hue 0 (red), 100 -> hue 120 (green)
            return HsvToRgb(hue, 1f, 1f);
        }

        /// <summary>HSV (h in degrees 0..360, s/v 0..1) -> RGB bytes.</summary>
        private static (byte r, byte g, byte b) HsvToRgb(float h, float s, float v)
        {
            h = ((h % 360f) + 360f) % 360f;
            float c = v * s;
            float x = c * (1f - Math.Abs((h / 60f) % 2f - 1f));
            float m = v - c;
            float r1, g1, b1;
            if (h < 60) { r1 = c; g1 = x; b1 = 0; }
            else if (h < 120) { r1 = x; g1 = c; b1 = 0; }
            else if (h < 180) { r1 = 0; g1 = c; b1 = x; }
            else if (h < 240) { r1 = 0; g1 = x; b1 = c; }
            else if (h < 300) { r1 = x; g1 = 0; b1 = c; }
            else { r1 = c; g1 = 0; b1 = x; }
            return ((byte)((r1 + m) * 255), (byte)((g1 + m) * 255), (byte)((b1 + m) * 255));
        }

        private static (byte r, byte g, byte b) ColorForButton(LegionInputButton b)
        {
            switch (b)
            {
                // Face buttons — Xbox color convention.
                case LegionInputButton.A: return (0, 220, 0);     // green
                case LegionInputButton.B: return (220, 0, 0);     // red
                case LegionInputButton.X: return (0, 80, 255);    // blue
                case LegionInputButton.Y: return (230, 200, 0);   // yellow

                // D-pad — cyan/teal family.
                case LegionInputButton.DpadUp: return (0, 200, 200);
                case LegionInputButton.DpadDown: return (0, 140, 180);
                case LegionInputButton.DpadLeft: return (0, 200, 140);
                case LegionInputButton.DpadRight: return (0, 160, 220);

                // System / sticks — neutral + distinct accents.
                case LegionInputButton.Start: return (255, 255, 255);   // white
                case LegionInputButton.Back: return (160, 160, 160);    // grey
                case LegionInputButton.LeftThumb: return (255, 120, 0);  // orange
                case LegionInputButton.RightThumb: return (255, 60, 120); // pink
                case LegionInputButton.Mode: return (120, 255, 0);      // lime
                case LegionInputButton.Share: return (0, 255, 180);     // mint

                // Shoulders — warm.
                case LegionInputButton.LeftShoulder: return (255, 160, 0);  // amber
                case LegionInputButton.RightShoulder: return (255, 90, 0);  // deep orange

                // Back paddles / extra buttons — magenta/purple family.
                case LegionInputButton.ExtraL1: return (200, 0, 255);   // purple
                case LegionInputButton.ExtraL2: return (255, 0, 200);   // magenta
                case LegionInputButton.ExtraR1: return (140, 0, 255);   // violet
                case LegionInputButton.ExtraRM1: return (255, 0, 120);  // rose
                case LegionInputButton.ExtraR2: return (180, 0, 200);   // orchid
                case LegionInputButton.ExtraR3: return (220, 40, 255);  // bright purple

                // Triggers.
                case LegionInputButton.LeftTrigger: return (0, 120, 255);  // azure
                case LegionInputButton.RightTrigger: return (255, 200, 0); // gold

                default: return (255, 255, 255);
            }
        }

        private void StartEffectThread()
        {
            if (_running) return;
            _running = true;
            _effectThread = new Thread(EffectLoop) { IsBackground = true, Name = "LegionReactiveLighting" };
            _effectThread.Start();
        }

        private void StopEffectThread()
        {
            _running = false;
            var t = _effectThread;
            _effectThread = null;
            if (t != null && t.IsAlive && t.ManagedThreadId != Thread.CurrentThread.ManagedThreadId)
            {
                try { t.Join(200); } catch { }
            }
        }

        private void EffectLoop()
        {
            while (_running)
            {
                LegionReactiveMode mode;
                lock (_stateLock) { mode = _mode; }

                if (IsContinuous(mode))
                {
                    if (mode == LegionReactiveMode.TriggerGradient)
                    {
                        byte lt = 0, rt = 0;
                        if (LegionButtonMonitor.TryGetLatestGamepadSample(out var sample))
                        {
                            lt = sample.LeftTrigger;
                            rt = sample.RightTrigger;
                        }
                        float pull = Math.Max(lt, rt) / 255f;
                        if (pull > 0.03f)
                        {
                            _triggerEngaged = true;
                            WriteColor(ComputeContinuousColor(mode));
                        }
                        else if (_triggerEngaged)
                        {
                            // Trigger returned to rest: hand the RGB back to the static
                            // lighting with a full authoritative profile restore instead of
                            // trusting one more solid write to land.
                            _triggerEngaged = false;
                            try { _legion.ReleaseReactiveLighting(); } catch { }
                            _lastSent = (1, 2, 3); // force the next engage to write
                        }
                    }
                    else
                    {
                        WriteColor(ComputeContinuousColor(mode));
                    }
                    Thread.Sleep(EffectTickMs);
                    continue;
                }

                float dt = EffectTickMs / 1000f;
                float level;
                (byte r, byte g, byte b) flash;
                bool released;
                lock (_stateLock)
                {
                    // Hold the flash at full while any reactive button is held; only decay once
                    // everything is released (hold-aware, mirrors the haptic on-release option).
                    if (_heldCount > 0)
                    {
                        _flashLevel = 1f;
                    }
                    else
                    {
                        float step = dt / _decaySeconds;
                        _flashLevel = Math.Max(0f, _flashLevel - step);
                    }
                    level = _flashLevel;
                    flash = _activeFlashColor;
                    released = _releasedSinceFlash;
                }

                // Flash on press also tracks analog input continuously: trigger pull and
                // stick deflection blend static -> flash color by how far the input is
                // pushed (full pull/deflection = full flash color, halfway = half blend),
                // on top of the button-flash decay. The strongest source wins, so a
                // button flash still decays normally once sticks/triggers are at rest.
                if (mode == LegionReactiveMode.FlashOnPress)
                {
                    float analog = ReadAnalogInputLevel();
                    if (analog > level)
                    {
                        level = analog;
                        lock (_stateLock) { flash = _flashColor; }
                    }
                }

                if (level > 0.001f)
                {
                    // Blend flash color -> the user's STATIC color by the decaying level, so the
                    // fade lands exactly on the static color (level 0 == static). Fading toward
                    // black and then re-applying the static profile caused a visible snap back to
                    // full brightness at the end of a flash; blending to the static color is seamless.
                    var baseColor = _legion.CurrentLightColorRgb;
                    byte r = (byte)(baseColor.r + (flash.r - baseColor.r) * level);
                    byte g = (byte)(baseColor.g + (flash.g - baseColor.g) * level);
                    byte b = (byte)(baseColor.b + (flash.b - baseColor.b) * level);
                    WriteColor((r, g, b));
                    lock (_stateLock) { _releasedSinceFlash = false; }
                }
                else if (!released)
                {
                    // Flash fully decayed and the last frame already equals the static color, so
                    // hand control back to the static lighting system without a visible change.
                    try { _legion.ReleaseReactiveLighting(); } catch { }
                    lock (_stateLock) { _releasedSinceFlash = true; }
                    _lastSent = (1, 2, 3); // force next flash to write
                }

                Thread.Sleep(EffectTickMs);
            }
        }

        /// <summary>
        /// Strongest current analog input, 0..1: trigger pull (0-255) vs stick deflection
        /// magnitude (XInput short range). A small deadzone keeps resting sticks from
        /// holding the reactive takeover (and its keepalive writes) forever; the range
        /// above the deadzone is rescaled so a full pull/deflection still reaches 1.0.
        /// </summary>
        private static float ReadAnalogInputLevel()
        {
            if (!LegionButtonMonitor.TryGetLatestGamepadSample(out var s)) return 0f;
            float trigger = Math.Max(s.LeftTrigger, s.RightTrigger) / 255f;
            float sticks = Math.Max(
                StickMagnitude(s.LeftStickX, s.LeftStickY),
                StickMagnitude(s.RightStickX, s.RightStickY));
            float analog = Math.Max(trigger, sticks);

            const float Deadzone = 0.10f;
            if (analog <= Deadzone) return 0f;
            return (analog - Deadzone) / (1f - Deadzone);
        }

        private static float StickMagnitude(short x, short y)
        {
            float fx = x / 32768f;
            float fy = y / 32768f;
            float mag = (float)Math.Sqrt(fx * fx + fy * fy);
            return mag > 1f ? 1f : mag;
        }

        private void WriteColor((byte r, byte g, byte b) color)
        {
            long now = DateTime.UtcNow.Ticks;
            bool unchanged = color.r == _lastSent.r && color.g == _lastSent.g && color.b == _lastSent.b;
            if (unchanged && _lastWriteTicks != 0
                && (now - _lastWriteTicks) < KeepaliveMs * TimeSpan.TicksPerMillisecond)
            {
                return;
            }
            if (_lastWriteTicks != 0 && (now - _lastWriteTicks) < WriteMinIntervalMs * TimeSpan.TicksPerMillisecond)
            {
                return;
            }
            try
            {
                _legion.SetReactiveStickColor(color.r, color.g, color.b, _legion.CurrentLightBrightness);
                _lastSent = color;
                _lastWriteTicks = now;
            }
            catch (Exception ex) { Logger.Debug($"LegionReactiveLighting: write failed: {ex.Message}"); }
        }

        private static (byte r, byte g, byte b) ParseHex(string s)
        {
            s = (s ?? "").Trim().TrimStart('#');
            if (s.Length == 6
                && byte.TryParse(s.Substring(0, 2), System.Globalization.NumberStyles.HexNumber, null, out byte r)
                && byte.TryParse(s.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, null, out byte g)
                && byte.TryParse(s.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, null, out byte b))
            {
                return (r, g, b);
            }
            return (255, 255, 255);
        }

        private static int ParseInt(string s, int fallback)
        {
            return int.TryParse((s ?? "").Trim(), out int v) ? v : fallback;
        }

        public void Dispose()
        {
            EnsureEdgeUnhooked();
            StopEffectThread();
            try { _legion.ReleaseReactiveLighting(); } catch { }
        }
    }
}
