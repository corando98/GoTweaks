using System;
using NLog;
using Shared.Data;
using XboxGamingBarHelper.ControllerEmulation;
using XboxGamingBarHelper.Devices;
using XboxGamingBarHelper.Settings;
using XboxGamingBarHelper.Labs;
using SharedDeviceType = Shared.Enums.DeviceType;

namespace XboxGamingBarHelper.ControllerEmulation.Viiper
{
    /// <summary>
    /// Synthesizes a right-stick override from gyro motion for VIIPER target types
    /// that have no native motion field in their wire format (xbox360, xboxelite2,
    /// switchpro family, etc.). Ported from <see cref="ControllerEmulationManager"/>'s
    /// legacy stick-gyro pipeline so users on the VIIPER backend get the same feel
    /// they had on legacy ViGEm Mode 1 ("Xbox / Stick").
    ///
    /// Reads <c>ControllerEmulationStick*</c> + <c>ControllerEmulationGyroActivation*</c>
    /// keys directly from <see cref="LocalSettingsHelper"/> so settings carry over
    /// between backends without a separate UI surface. Refreshes them lazily once
    /// per second on the poll thread.
    ///
    /// Gyro source is selected via <c>ControllerEmulationGyroSource</c> — same setting
    /// legacy CE uses — backed by the existing <see cref="LegionControllerGyroSourceAdapter"/>
    /// / <see cref="LegionControllerMixedGyroSourceAdapter"/> classes. All variants read
    /// through <see cref="LegionButtonMonitor"/>'s cached HID handle, so samples keep
    /// flowing regardless of HidHide visibility (see issue #79 round-3 findings).
    /// </summary>
    internal sealed class ViiperStickGyroProcessor
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        // ---- Settings cache (refreshed from LocalSettingsHelper periodically) ----
        // Pipeline pruned in #79 round 5:
        // conversion → invert → sensitivity → clamp. The deprecated min/max
        // gyro speed, min/max output, deadzone, precision speed, power curve,
        // and output mix sliders no longer affect anything; their UI was
        // removed at the same time.
        private bool enabled;            // ViiperStickGyroEnabled — master kill switch for the feature
        private int gyroSource;          // 0=Mixed (default), 1=Right, 2=Left, 3=Mixed; matches legacy CE convention
        private int stickSelect;         // 0=Left stick, 1=Right stick (Send-to-joystick)
        private int gyroActivationMode;
        private int gyroActivationButton;
        private bool stickInvertX;
        private bool stickInvertY;
        private int stickSensitivityV2;
        private int stickOrientationV2;
        private int stickConversion;
        // Anti-deadzone tunables — kept in sync with the legacy CE pipeline
        // via shared LocalSettings keys. Defaults match the previously-
        // hardcoded constants (10% min stick deflection, 0.5°/s noise floor).
        private int stickGyroAntiDeadzonePercent = 10;
        private int stickGyroAntiDeadzoneThresholdTenths = 3;
        private int stickGyroVerticalRatio = 100;
        private int stickGyroCurvePreset = 0;
        private int stickGyroTightenThreshold = 0;
        private int stickGyroTightenGain = 100;
        private bool stickGyroTouchDeactivateEnabled = false;
        private int stickGyroTouchDeactivateThreshold = 15;
        private int stickGyroTouchDeactivateHoldoff = 250;
        private int stickGyroSmoothing = 30;
        private float smoothedGyroXState;
        private float smoothedGyroYState;
        private float smoothedGyroZState;
        private bool smoothedGyroPrimed;
        private long lastSettingsRefreshTicks;
        // 200 ms refresh — fast enough that slider drags feel instant, slow enough
        // that the LocalSettings dictionary lookups don't show up in poll-loop
        // profiles. Was 1 s; that produced a noticeable lag when tuning sensitivity
        // / deadzone live during gameplay.
        private const long SettingsRefreshIntervalTicks = TimeSpan.TicksPerSecond / 5;

        // ---- Active gyro source adapter (rebuilt when gyroSource setting changes) ----
        // Reuses the legacy CE adapter classes so source switching matches Mode 1
        // behavior identically; no second implementation to keep in sync.
        private IGyroSourceAdapter activeAdapter;
        // Composite key for "is the active adapter still the right one?" — we have to
        // rebuild not only on gyroSource change but also when the detected device type
        // changes (rare, but happens during dock/undock or when the user reconfigures
        // which handheld profile is active). Pack both into a single int: low byte =
        // gyroSource, high byte = (int)deviceType.
        private int activeAdapterKey = -1;
        // Track conversion/orientation changes so we can reset the JSL fusion
        // when the user flips between modes (e.g., Yaw → Player Space). Without
        // a reset the carried-over orientation quaternion can produce a brief
        // burst of spurious motion while it re-converges.
        private int prevConversion = -1;
        private int prevOrientation = -1;

        // ---- Diagnostic counters (5s rolling window). Periodic Info-level emit so
        // "no gyro on Xbox 360 VIIPER" reports can be triaged from a single helper log. ----
        private int statsCalls;
        private int statsGateOff;
        private int statsNoSample;
        private int statsMerged;
        private long statsLastEmitTicks;
        private const long StatsEmitIntervalTicks = TimeSpan.TicksPerSecond * 5;

        // Per-pipeline-stage diagnostic — fires once per second when the gate is
        // open AND any axis is producing output, so users testing "horizontal
        // works, vertical doesn't" can see at which stage the value dies.
        // Throttled to avoid log spam during normal gameplay.
        private long pipelineDiagLastTicks;
        private const long PipelineDiagIntervalTicks = TimeSpan.TicksPerSecond;

        // Player Space (#79 round 5) diagnostic — fires once per second whenever
        // mode 3 is selected so we can verify JSL's gravity vector matches its
        // Y-up expectation (gravity ≈ +1g on Y when device flat).
        private long playerSpaceDiagLastTicks;

        // ---- Activation toggle state ----
        private bool gyroToggleActive;
        private bool lastGyroActivationButtonPressed;

        // ---- Bias correction (issue #79 round 4) ----
        // vvalente30: "gyro is moving the device by itself uncontrollable when
        // always on." Stats showed merged=259-293/323 calls per 5s — feature was
        // alive, but a typical 5-10°/s residual IMU bias survives the 2°/s
        // deadzone and produces a small persistent stick deflection that games
        // integrate as a slow camera spin. Estimator updates only during
        // stationary periods so deliberate slow pans aren't learned as bias.
        private readonly GyroBiasEstimator biasEstimator = new GyroBiasEstimator();

        // ---- Gyro/accel sensor fusion for Player Space conversion (#79 round 5) ----
        // Wraps Jibb Smart's GamepadMotionHelpers via P/Invoke. The native
        // library maintains a fused orientation/gravity estimate using a
        // complementary filter on gyro+accel — much more stable during motion
        // than a low-pass on accel alone, which is what the first cut here used.
        // Lifetime tied to this processor; Disposed via Dispose() / finalizer.
        private readonly GamepadMotion gamepadMotion = new GamepadMotion();
        private long gamepadMotionLastTicksUtc;

        // Sampling-rate constants — only kept for the JSL ProcessMotion delta
        // when the IMU sample timestamp gap is unavailable.
        private const float DefaultDeltaSeconds = 1.0f / 250.0f;

        // No EMA smoother. The earlier round-5 removal regressed
        // because we lacked gyro bias correction. JSL is now in
        // Stillness | SensorFusion mode (PlayerSpaceGyro.cs ctor) so it learns
        // the BMI260's static bias on its own and the smoother isn't needed.

        // Diagnostics — per-axis min/max gyro and accel-magnitude min/max over
        // the past second, plus a "spike count" (samples whose absolute delta
        // from the previous one exceeds a threshold). Helps spot whether
        // perceived stutter is real motion, sample aliasing, or parser noise.
        private float diagGyroXMin = float.MaxValue, diagGyroXMax = float.MinValue;
        private float diagGyroYMin = float.MaxValue, diagGyroYMax = float.MinValue;
        private float diagGyroZMin = float.MaxValue, diagGyroZMax = float.MinValue;
        private float diagAccelMagMin = float.MaxValue, diagAccelMagMax = float.MinValue;
        private int diagSampleCount;
        private int diagSpikeCount;
        private float diagPrevGyroX, diagPrevGyroY, diagPrevGyroZ;
        private bool diagPrevValid;
        private const float DiagSpikeThresholdDegPerSec = 250.0f;

        // ---- Standard XInput button bits (match ViiperXInput in XInputNative.cs) ----
        private const ushort BTN_DPAD_UP = 0x0001;
        private const ushort BTN_DPAD_DOWN = 0x0002;
        private const ushort BTN_DPAD_LEFT = 0x0004;
        private const ushort BTN_DPAD_RIGHT = 0x0008;
        private const ushort BTN_START = 0x0010;
        private const ushort BTN_BACK = 0x0020;
        private const ushort BTN_LEFT_THUMB = 0x0040;
        private const ushort BTN_RIGHT_THUMB = 0x0080;
        private const ushort BTN_LB = 0x0100;
        private const ushort BTN_RB = 0x0200;
        private const ushort BTN_A = 0x1000;
        private const ushort BTN_B = 0x2000;
        private const ushort BTN_X = 0x4000;
        private const ushort BTN_Y = 0x8000;
        private const byte XINPUT_TRIGGER_THRESHOLD = 30;

        // ---- Legion HID raw aux button bits (match LegionButtonMonitor parser output) ----
        private const ushort LEGION_AUX_L1 = 0x0004;   // Y1
        private const ushort LEGION_AUX_L2 = 0x0008;   // Y2
        private const ushort LEGION_AUX_R1 = 0x0010;   // Y3
        private const ushort LEGION_AUX_RM1 = 0x0020;  // M1
        private const ushort LEGION_AUX_R2 = 0x0040;   // M3
        private const ushort LEGION_AUX_R3 = 0x0080;   // M2

        public ViiperStickGyroProcessor()
        {
            RefreshSettings();
            lastSettingsRefreshTicks = DateTime.UtcNow.Ticks;
            statsLastEmitTicks = lastSettingsRefreshTicks;

            // Apply any saved JSL gyro bias from a previous Calibrate Gyro click.
            // Manager-side LoadJslCalibrationOffset can't reach the Viiper
            // processor on startup because the forwarder singleton hasn't been
            // created yet — so the processor loads its own offset on construction.
            // Same key names as ControllerEmulationManager.JslCalibKey* (kept in sync).
            try
            {
                bool hasX = LocalSettingsHelper.TryGetValue("ControllerEmulationGyroBiasX", out float bx);
                bool hasY = LocalSettingsHelper.TryGetValue("ControllerEmulationGyroBiasY", out float by);
                bool hasZ = LocalSettingsHelper.TryGetValue("ControllerEmulationGyroBiasZ", out float bz);
                LocalSettingsHelper.TryGetValue("ControllerEmulationGyroBiasWeight", out int bw);
                if (hasX && hasY && hasZ)
                {
                    gamepadMotion.SetCalibrationOffset(bx, by, bz, bw);
                    gamepadMotion.SetCalibrationMode(GamepadMotion.JslCalibrationManual);
                    Logger.Info($"VIIPER stick-gyro: loaded saved JSL bias offset ({bx:F3}, {by:F3}, {bz:F3}) weight={bw} — JSL switched to Manual");
                }
                else
                {
                    Logger.Info("VIIPER stick-gyro: no saved JSL bias — JSL stays in auto-cal (Stillness|SensorFusion) until user runs Calibrate Gyro");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"VIIPER stick-gyro: failed loading saved JSL bias: {ex.Message}");
            }

            // Run a quick self-test on the JSL pipeline so the helper log
            // contains a known-input → known-output trace right after startup.
            // Lets us A/B-compare against expected values without needing the
            // user to wave the device around. Only runs once per helper boot.
            try { RunSelfTest(); }
            catch (Exception ex) { Logger.Warn($"Stick-gyro self-test threw: {ex.Message}"); }
        }

        /// <summary>
        /// True when "Send to joystick" is set to Left. Forwarder reads this AFTER
        /// <see cref="TryComputeStickOverride"/> returns true to know whether to merge
        /// the stick override into ThumbLX/LY or ThumbRX/RY.
        /// </summary>
        public bool RoutesToLeftStick => stickSelect == 0;

        /// <summary>
        /// Targets without a native motion field in their wire format. Stick-gyro
        /// makes sense for these because games can't read gyro any other way.
        /// DS4 / DualSense / DualSense Edge are deliberately excluded — their wire
        /// formats already carry IMU bytes (see ViiperInputForwarder.BuildDualShock4Input
        /// / BuildDualSenseInput / BuildDualSenseEdgeInput) so synthesizing a stick
        /// override on top of real gyro would double-feed motion to games.
        /// </summary>
        public static bool IsApplicableForTarget(string targetType)
        {
            if (string.IsNullOrEmpty(targetType)) return false;
            switch (targetType)
            {
                case "xbox360":
                case "xboxelite2":
                case "xbox-one":
                case "xbox-elite":
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
                // "dualsense" / "ds5" / "dualsense-edge" were wrongly listed here:
                // both DualSense builders carry native IMU bytes (wire offsets 21..32),
                // so the synthesized stick override double-fed motion - field report
                // "gyro assigned to right stick of DualSense, PS4 emulation unaffected"
                // (dualshock4 was correctly absent). Removed to match the documented
                // native-motion exclusion rule above.
                case "switchpro":
                case "joycon-left":
                case "joycon-right":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Clear activation + JSL state. Call on backend swap / forwarder restart.</summary>
        public void Reset()
        {
            gyroToggleActive = false;
            lastGyroActivationButtonPressed = false;
            gamepadMotionLastTicksUtc = 0;
            gamepadMotion.Reset();
            // Bias state is held across forwarder Stop/Start because the IMU's
            // calibration doesn't change when our process pauses. Only forced
            // recalibration (the future "Calibrate now" button or backend
            // swap on dock/undock) should drop it.
        }

        /// <summary>
        /// Drop the bias estimate so the next stationary period re-snaps. Hooked
        /// up to the future "Calibrate now" UX. Public so corando can fire it
        /// from a settings change handler if we want a "Reset gyro calibration"
        /// command.
        /// </summary>
        public void RecalibrateBias() => biasEstimator.Reset();

        /// <summary>
        /// Apply a previously-persisted gyro bias offset so JSL subtracts it from
        /// every sample. Called on startup with the saved offset (if any).
        /// </summary>
        public void ApplyJslCalibrationOffset(float xOffset, float yOffset, float zOffset, int weight)
        {
            gamepadMotion.SetCalibrationOffset(xOffset, yOffset, zOffset, weight);
            Logger.Info($"VIIPER stick-gyro: applied saved JSL bias offset ({xOffset:F3}, {yOffset:F3}, {zOffset:F3}) weight={weight}");
        }

        /// <summary>
        /// Run the calibration flow on the Viiper-side JSL instance: reset
        /// accumulator, switch to Stillness|SensorFusion, wait up to
        /// <paramref name="timeoutMs"/> for confidence to reach 1.0, capture the
        /// learned offset, switch back to Manual, and apply the offset so JSL
        /// uses it immediately. Returns the (xOffset, yOffset, zOffset, weight)
        /// tuple via out params; caller persists.
        /// </summary>
        /// <summary>
        /// Start an explicit JSL calibration capture (Stillness | SensorFusion).
        /// Pair with <see cref="EndJslCalibration"/>. Caller drives the wait
        /// loop and progress reporting.
        /// </summary>
        public void BeginJslCalibration()
        {
            try
            {
                diagPumpSuccess = 0;
                diagPumpNoAdapter = 0;
                diagPumpNoSample = 0;
                gamepadMotion.ResetContinuousCalibration();
                // Use manual continuous calibration (StartContinuousCalibration)
                // rather than stillness auto-detection. The stillness detector's
                // auto-adapting MinDeltaGyro threshold never converges fast
                // enough on this IMU within a 5s window (confidence stays at
                // 0); manual continuous mode just averages every sample we
                // feed it during the window, which is what the user actually
                // intends when they put the device flat and click Calibrate.
                gamepadMotion.StartContinuousCalibration();
            }
            catch (Exception ex)
            {
                Logger.Warn($"BeginJslCalibration failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Polling-friendly current confidence (0.0-1.0) of the auto-calibration.
        /// </summary>
        public float GetJslCalibrationConfidence()
        {
            return gamepadMotion.GetAutoCalibrationConfidence();
        }

        /// <summary>
        /// Feed one IMU sample directly to JSL during the calibration window.
        /// Bypasses the activation gate (Hold / Toggle button) and the
        /// stick-gyro enable flag — calibration must work regardless of those.
        /// Returns true if a fresh sample was pumped through. Caller drives
        /// the timing (typically once per ~50–250 ms in the calibration loop).
        /// </summary>
        // Diagnostic counters for the calibration pump. Reset by
        // BeginJslCalibration so each capture window's stats are clean.
        private int diagPumpSuccess;
        private int diagPumpNoAdapter;
        private int diagPumpNoSample;
        private float diagPumpLastGyroX, diagPumpLastGyroY, diagPumpLastGyroZ;
        private float diagPumpLastAccelX, diagPumpLastAccelY, diagPumpLastAccelZ;

        public bool PumpJslSampleForCalibration()
        {
            try
            {
                // Make sure we have an adapter; the regular tick path may not
                // have run yet (e.g. CE just enabled, or stick-gyro disabled).
                EnsureGyroAdapter();
                if (activeAdapter == null)
                {
                    diagPumpNoAdapter++;
                    return false;
                }
                if (!activeAdapter.TryGetLatestSample(out GyroSample sample))
                {
                    diagPumpNoSample++;
                    return false;
                }

                long sampleTicks = sample.TimestampTicksUtc;
                float dt = gamepadMotionLastTicksUtc > 0 && sampleTicks > gamepadMotionLastTicksUtc
                    ? (float)((sampleTicks - gamepadMotionLastTicksUtc) / (double)TimeSpan.TicksPerSecond)
                    : DefaultDeltaSeconds;
                gamepadMotionLastTicksUtc = sampleTicks;

                gamepadMotion.Update(
                    sample.GyroXDegPerSecond, sample.GyroYDegPerSecond, sample.GyroZDegPerSecond,
                    sample.AccelXG, sample.AccelYG, sample.AccelZG,
                    dt);
                diagPumpSuccess++;
                diagPumpLastGyroX = sample.GyroXDegPerSecond;
                diagPumpLastGyroY = sample.GyroYDegPerSecond;
                diagPumpLastGyroZ = sample.GyroZDegPerSecond;
                diagPumpLastAccelX = sample.AccelXG;
                diagPumpLastAccelY = sample.AccelYG;
                diagPumpLastAccelZ = sample.AccelZG;
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn($"PumpJslSampleForCalibration failed: {ex.Message}");
                return false;
            }
        }

        public string GetPumpDiagSummary()
        {
            return string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "pump: success={0} noAdapter={1} noSample={2} | last gyro=({3:F2},{4:F2},{5:F2}) accel=({6:F2},{7:F2},{8:F2})G",
                diagPumpSuccess, diagPumpNoAdapter, diagPumpNoSample,
                diagPumpLastGyroX, diagPumpLastGyroY, diagPumpLastGyroZ,
                diagPumpLastAccelX, diagPumpLastAccelY, diagPumpLastAccelZ);
        }

        /// <summary>
        /// Capture the learned offset, apply it back via SetCalibrationOffset
        /// (so JSL uses it on the very next sample), and switch JSL back to
        /// Manual mode. Pair with <see cref="BeginJslCalibration"/>.
        /// </summary>
        public void EndJslCalibration(
            out float xOffset, out float yOffset, out float zOffset, out int weight)
        {
            xOffset = 0; yOffset = 0; zOffset = 0; weight = 0;
            try
            {
                // Manual continuous calibration ends here — Pause clears
                // IsCalibrating, GetCalibrationOffset returns the average
                // gyro across all samples pushed during the window.
                gamepadMotion.PauseContinuousCalibration();
                gamepadMotion.GetCalibrationOffset(out xOffset, out yOffset, out zOffset);

                // Weight = 10 when we collected enough samples to trust the
                // capture (≥ ~50 samples during the 5s window), tapering
                // down linearly below that to surface "low-confidence" in
                // the widget UI when the source wasn't producing data.
                int samples = diagPumpSuccess;
                weight = samples >= 50 ? 10 : (samples / 5);
                if (weight < 0) weight = 0;
                if (weight > 10) weight = 10;

                gamepadMotion.SetCalibrationOffset(xOffset, yOffset, zOffset, weight);
                // SetCalibrationMode left at JslCalibrationManual (the ctor
                // default) — no Stillness auto-cal running outside this flow.
                Logger.Info($"VIIPER stick-gyro JSL calibration end: samples={samples} offset=({xOffset:F3}, {yOffset:F3}, {zOffset:F3}) weight={weight} | {GetPumpDiagSummary()}");
            }
            catch (Exception ex)
            {
                Logger.Warn($"EndJslCalibration failed: {ex.Message}");
            }
        }

        public bool RunJslCalibration(int timeoutMs,
            out float xOffset, out float yOffset, out float zOffset, out int weight)
        {
            xOffset = 0; yOffset = 0; zOffset = 0; weight = 0;
            try
            {
                gamepadMotion.ResetContinuousCalibration();
                gamepadMotion.SetCalibrationMode(GamepadMotion.JslCalibrationStillnessFusion);

                var sw = System.Diagnostics.Stopwatch.StartNew();
                float confidence = 0.0f;
                while (sw.ElapsedMilliseconds < timeoutMs)
                {
                    confidence = gamepadMotion.GetAutoCalibrationConfidence();
                    if (confidence >= 1.0f) break;
                    System.Threading.Thread.Sleep(50);
                }

                gamepadMotion.GetCalibrationOffset(out xOffset, out yOffset, out zOffset);
                weight = (int)Math.Round(confidence * 10.0f);
                gamepadMotion.SetCalibrationOffset(xOffset, yOffset, zOffset, weight);
                gamepadMotion.SetCalibrationMode(GamepadMotion.JslCalibrationManual);

                Logger.Info($"VIIPER stick-gyro JSL calibration: confidence={confidence:F2} offset=({xOffset:F3}, {yOffset:F3}, {zOffset:F3}) weight={weight} elapsed={sw.ElapsedMilliseconds}ms");
                return confidence > 0.0f;
            }
            catch (Exception ex)
            {
                Logger.Warn($"VIIPER stick-gyro JSL calibration failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Tear down the active gyro source adapter (call from forwarder Stop). Safe
        /// to call repeatedly; idempotent.
        /// </summary>
        public void Shutdown()
        {
            try { activeAdapter?.Stop(); }
            catch (Exception ex) { Logger.Debug($"VIIPER stick-gyro adapter Stop threw: {ex.Message}"); }
            try { activeAdapter?.Dispose(); }
            catch (Exception ex) { Logger.Debug($"VIIPER stick-gyro adapter Dispose threw: {ex.Message}"); }
            activeAdapter = null;
            activeAdapterKey = -1;
        }

        /// <summary>
        /// Returns true with a non-zero stick X/Y when the feature is enabled,
        /// activation is engaged, and a fresh gyro sample is available. Caller merges
        /// (stickX, stickY) into ThumbLX/LY or ThumbRX/RY based on
        /// <see cref="RoutesToLeftStick"/>.
        /// </summary>
        public bool TryComputeStickOverride(
            ushort xinputButtons,
            byte leftTrigger,
            byte rightTrigger,
            ushort legionAuxButtonsRaw,
            out short stickX,
            out short stickY)
        {
            return TryComputeStickOverride(xinputButtons, leftTrigger, rightTrigger,
                legionAuxButtonsRaw, 0, 0, out stickX, out stickY);
        }

        public bool TryComputeStickOverride(
            ushort xinputButtons,
            byte leftTrigger,
            byte rightTrigger,
            ushort legionAuxButtonsRaw,
            short physicalStickX,
            short physicalStickY,
            out short stickX,
            out short stickY)
        {
            stickX = 0;
            stickY = 0;

            long nowTicksUtc = DateTime.UtcNow.Ticks;
            if (nowTicksUtc - lastSettingsRefreshTicks >= SettingsRefreshIntervalTicks)
            {
                RefreshSettings();
                EnsureGyroAdapter();
                lastSettingsRefreshTicks = nowTicksUtc;
            }

            statsCalls++;
            MaybeEmitStats(nowTicksUtc);

            if (!enabled)
            {
                statsGateOff++;
                return false;
            }

            // Always pull a sample (even when activation gate is closed) so the
            // bias estimator keeps learning continuously. Otherwise users who
            // play with Hold/Toggle activation get bias=uncal until the moment
            // they engage the gate, then suffer the full pre-correction drift
            // for the first ~500ms while the estimator catches up. Cheap path:
            // adapter samples are already cached from the polling backend.
            bool gateOpen = IsActivationEnabled(xinputButtons, leftTrigger, rightTrigger, legionAuxButtonsRaw);

            // Sample arrives via the gyro source adapter selected from the
            // ControllerEmulationGyroSource setting (see EnsureGyroAdapter). Same
            // adapter set legacy CE uses — Right/Left/Mixed all backed by
            // LegionButtonMonitor's cached HID handle.
            if (activeAdapter == null || !activeAdapter.TryGetLatestSample(out GyroSample sample))
            {
                statsNoSample++;
                return false;
            }

            // Feed the bias estimator on every sample, even when the gate is
            // closed. Without this, calibration would only happen during active
            // gyro engagement, leaving the user with a fully uncalibrated
            // estimator at the moment they first press the activation button.
            // Discard the corrected sample when the gate is closed — the work
            // is just to keep the estimator's state current.
            if (!gateOpen)
            {
                _ = biasEstimator.Correct(sample);
                statsGateOff++;
                return false;
            }

            ApplyStickFromGyro(sample, physicalStickX, physicalStickY, out stickX, out stickY);
            bool produced = stickX != 0 || stickY != 0;
            if (produced) statsMerged++;
            return produced;
        }

        // ----------------------------------------------------------------------
        // Gyro source adapter selection
        // ----------------------------------------------------------------------

        /// <summary>
        /// Build the gyro source adapter matching the current <c>gyroSource</c>
        /// setting AND the detected handheld device type. Idempotent — short-circuits
        /// when the active adapter already matches both. Disposes and rebuilds when
        /// either changes.
        ///
        /// Routing mirrors legacy CE <c>BuildGyroSourceAdapter</c>:
        /// • Legion Go / Go 2:        Legion controller IMU adapters (Right/Left/Mixed)
        ///                             driven by the user's gyroSource setting (1/2/3),
        ///                             defaulting to Mixed.
        /// • Legion Go S / GPD Win 5: Windows Sensor stack (handheld IMU exposed by
        ///                             the device's own Windows driver). The
        ///                             gyroSource setting is ignored — there's only
        ///                             one source available on these devices.
        /// • Generic / unknown:       Windows Sensor fallback as well, so non-Legion
        ///                             handhelds (ROG Ally, Steam Deck, MSI Claw, etc.)
        ///                             still get a working gyro→stick path. Same
        ///                             behavior the user requested: "non-Legion
        ///                             devices can be pulled from the Handheld (device
        ///                             IMU) since it's provided by Windows."
        /// </summary>
        private void EnsureGyroAdapter()
        {
            SharedDeviceType deviceType = TryDetectDeviceType();
            int targetKey = ((int)deviceType << 8) | (gyroSource & 0xFF);
            if (activeAdapter != null && activeAdapterKey == targetKey) return;

            try { activeAdapter?.Stop(); }
            catch (Exception ex) { Logger.Debug($"VIIPER stick-gyro previous adapter Stop threw: {ex.Message}"); }
            try { activeAdapter?.Dispose(); }
            catch (Exception ex) { Logger.Debug($"VIIPER stick-gyro previous adapter Dispose threw: {ex.Message}"); }
            activeAdapter = null;

            switch (deviceType)
            {
                case SharedDeviceType.LegionGo:
                case SharedDeviceType.LegionGo2:
                    switch (gyroSource)
                    {
                        case 1: activeAdapter = new LegionControllerGyroSourceAdapter(false); break;  // Right
                        case 2: activeAdapter = new LegionControllerGyroSourceAdapter(true); break;   // Left
                        default: activeAdapter = new LegionControllerMixedGyroSourceAdapter(); break; // Mixed (incl. default 0)
                    }
                    break;

                case SharedDeviceType.LegionGoS:
                    activeAdapter = new WindowsSensorGyroSourceAdapter("Legion Go S Internal Gyro");
                    break;

                case SharedDeviceType.GPDWin5:
                    activeAdapter = new WindowsSensorGyroSourceAdapter("GPD Internal Gyro");
                    break;

                default:
                    // ROG Ally, Steam Deck, MSI Claw, generic — fall back to the OS
                    // Windows Sensor stack with a generic name. Same path legacy CE
                    // uses for unknown devices when gyroSource defaults to Internal.
                    activeAdapter = new WindowsSensorGyroSourceAdapter("Handheld Internal Gyro");
                    break;
            }

            try
            {
                if (!activeAdapter.Start())
                {
                    Logger.Warn($"VIIPER stick-gyro adapter '{activeAdapter.Name}' failed to start; gyro→stick will be silent until next refresh");
                    activeAdapter.Dispose();
                    activeAdapter = null;
                    activeAdapterKey = -1;
                    return;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"VIIPER stick-gyro adapter Start threw: {ex.Message}");
                activeAdapter = null;
                activeAdapterKey = -1;
                return;
            }

            activeAdapterKey = targetKey;
            Logger.Info($"VIIPER stick-gyro adapter active: {activeAdapter.Name} (device={deviceType}, gyroSource={gyroSource})");
        }

        /// <summary>
        /// Best-effort device-type probe. Cheap (DeviceDetector caches), safe to call
        /// per refresh tick. Returns Generic on detection failure so the Windows
        /// Sensor fallback path still engages.
        /// </summary>
        private static SharedDeviceType TryDetectDeviceType()
        {
            try
            {
                var info = DeviceDetector.DetectDevice();
                return info?.DeviceType ?? SharedDeviceType.Generic;
            }
            catch (Exception ex)
            {
                Logger.Debug($"VIIPER stick-gyro DeviceDetector threw: {ex.Message}");
                return SharedDeviceType.Generic;
            }
        }

        // ----------------------------------------------------------------------
        // 5s diagnostic stats
        // ----------------------------------------------------------------------

        private void MaybeEmitStats(long nowTicksUtc)
        {
            if (nowTicksUtc - statsLastEmitTicks < StatsEmitIntervalTicks) return;
            // Only log when there's traffic — otherwise we'd spam the log when
            // the section is mounted but the user isn't on an applicable target.
            if (statsCalls > 0)
            {
                string biasField = biasEstimator.IsCalibrated
                    ? string.Format("bias=[{0:F1},{1:F1},{2:F1}]",
                        biasEstimator.BiasXDegPerSec,
                        biasEstimator.BiasYDegPerSec,
                        biasEstimator.BiasZDegPerSec)
                    : "bias=uncal";
                Logger.Info(
                    "VIIPER stick-gyro 5s stats: enabled={0} calls={1} gateOff={2} noSample={3} merged={4} src={5} route={6} mode={7} btn={8} {9}",
                    enabled, statsCalls, statsGateOff, statsNoSample, statsMerged,
                    gyroSource, stickSelect == 0 ? "Left" : "Right",
                    gyroActivationMode, gyroActivationButton, biasField);
            }
            statsCalls = 0;
            statsGateOff = 0;
            statsNoSample = 0;
            statsMerged = 0;
            statsLastEmitTicks = nowTicksUtc;
        }

        // ----------------------------------------------------------------------
        // Settings
        // ----------------------------------------------------------------------

        private void RefreshSettings()
        {
            // Master enable for the VIIPER stick-gyro feature. Defaults true so the
            // feature works out of the box on existing installs (it shipped on by
            // default in 0.3.2152). Users on the VIIPER backend can untoggle it
            // from the panel UI to get raw stick passthrough on xbox360 targets.
            enabled = !LocalSettingsHelper.TryGetValue("ViiperStickGyroEnabled", out bool savedEnabled) || savedEnabled;

            // Gyro source selector (shared with legacy CE — same key).
            gyroSource = LocalSettingsHelper.TryGetValue("ControllerEmulationGyroSource", out int savedGyroSource)
                ? Math.Max(0, Math.Min(3, savedGyroSource)) : 0;
            // Send-to-joystick. Default 1 (Right) per vvalente30's recommended settings.
            stickSelect = LocalSettingsHelper.TryGetValue("ControllerEmulationStickSelect", out int savedStickSelect)
                ? (savedStickSelect == 0 ? 0 : 1) : 1;

            gyroActivationMode = LocalSettingsHelper.TryGetValue("ControllerEmulationGyroActivationMode", out int savedMode)
                ? Math.Max(0, Math.Min(2, savedMode)) : 0;
            gyroActivationButton = LocalSettingsHelper.TryGetValue("ControllerEmulationGyroActivationButton", out int savedBtn)
                ? Math.Max(1, Math.Min(22, savedBtn)) : 1;

            stickInvertX = LocalSettingsHelper.TryGetValue("ControllerEmulationStickInvertX", out bool savedInvX) && savedInvX;
            stickInvertY = LocalSettingsHelper.TryGetValue("ControllerEmulationStickInvertY", out bool savedInvY) && savedInvY;

            // Sensitivity multiplier (slider value / 100). Single knob the user
            // tweaks for feel — collapses what would otherwise be per-axis
            // sensitivity into one slider since the conversion math is symmetric.
            stickSensitivityV2 = LocalSettingsHelper.TryGetValue("ControllerEmulationStickSensitivityV2", out int v6)
                ? Math.Max(1, Math.Min(400, v6)) : 100;
            // 0 = Flat (no Y/Z swap), 1 = Handheld (Y/Z swap applied). Default
            // Flat: the swap is mainly for users who want roll-of-device to act
            // as their horizontal stick input; the new default uses Mode 0
            // (Yaw) where horizontal = gyroY directly, which is the natural
            // "laser pointer from back of device" feel and works for both flat
            // and handheld holds because device-Y stays approximately aligned
            // with world-up in both poses. Saved value wins.
            stickOrientationV2 = LocalSettingsHelper.TryGetValue("ControllerEmulationStickOrientationV2", out int v10)
                ? (v10 == 1 ? 1 : 0) : 0;
            // 0=Yaw, 1=Roll, 2=Yaw+Roll, 3=Player Space, 4=World Space.
            // Default 0 (Yaw) — "laser pointer from back of device" mental
            // model: yawing the device around its own up-axis (gyroY) pans
            // the camera left/right; pitching (gyroX) pans up/down; rolling
            // doesn't change the laser direction so produces no stick output.
            // Player/World Space treat *gravity* as the yaw axis, which means
            // a tilted handheld feels like the laser is pointing at the sky
            // instead of out the back of the device — perceptually wrong for
            // most handheld users. Saved value wins.
            stickConversion = LocalSettingsHelper.TryGetValue("ControllerEmulationStickConversion", out int v11)
                ? Math.Max(0, Math.Min(4, v11)) : 0;

            stickGyroAntiDeadzonePercent = LocalSettingsHelper.TryGetValue("ControllerEmulationStickGyroAntiDeadzone", out int v12)
                ? Math.Max(0, Math.Min(30, v12)) : 10;
            stickGyroAntiDeadzoneThresholdTenths = LocalSettingsHelper.TryGetValue("ControllerEmulationStickGyroAntiDeadzoneThreshold", out int v13)
                ? Math.Max(0, Math.Min(50, v13)) : 3;
            stickGyroVerticalRatio = LocalSettingsHelper.TryGetValue("ControllerEmulationStickGyroVerticalRatio", out int v14)
                ? Math.Max(10, Math.Min(200, v14)) : 100;
            stickGyroCurvePreset = LocalSettingsHelper.TryGetValue("ControllerEmulationStickGyroCurvePreset", out int v15)
                ? Math.Max(0, Math.Min(2, v15)) : 0;
            stickGyroTightenThreshold = LocalSettingsHelper.TryGetValue("ControllerEmulationStickGyroTightenThreshold", out int v16)
                ? Math.Max(0, Math.Min(500, v16)) : 0;
            stickGyroTightenGain = LocalSettingsHelper.TryGetValue("ControllerEmulationStickGyroTightenGain", out int v17)
                ? Math.Max(100, Math.Min(300, v17)) : 100;
            stickGyroTouchDeactivateEnabled = LocalSettingsHelper.TryGetValue("ControllerEmulationStickGyroTouchDeactivateEnabled", out bool v18) && v18;
            stickGyroTouchDeactivateThreshold = LocalSettingsHelper.TryGetValue("ControllerEmulationStickGyroTouchDeactivateThreshold", out int v19)
                ? Math.Max(0, Math.Min(50, v19)) : 15;
            stickGyroTouchDeactivateHoldoff = LocalSettingsHelper.TryGetValue("ControllerEmulationStickGyroTouchDeactivateHoldoff", out int v20)
                ? Math.Max(0, Math.Min(1000, v20)) : 250;
            int newSmoothing = LocalSettingsHelper.TryGetValue("ControllerEmulationStickGyroSmoothing", out int v21)
                ? Math.Max(0, Math.Min(90, v21)) : 30;
            if (newSmoothing != stickGyroSmoothing)
            {
                stickGyroSmoothing = newSmoothing;
                smoothedGyroPrimed = false;
            }

            // Reset the JSL fusion when the conversion or orientation changes —
            // the orientation quaternion carries over otherwise and produces a
            // brief burst of spurious output as it re-converges.
            if (prevConversion != -1 && (prevConversion != stickConversion || prevOrientation != stickOrientationV2))
            {
                gamepadMotion.Reset();
                gamepadMotionLastTicksUtc = 0;
            }
            prevConversion = stickConversion;
            prevOrientation = stickOrientationV2;
        }

        // ----------------------------------------------------------------------
        // Activation gate
        // ----------------------------------------------------------------------

        private bool IsActivationEnabled(ushort buttons, byte lt, byte rt, ushort aux)
        {
            switch (gyroActivationMode)
            {
                case 1: // Hold
                    bool holdPressed = IsActivationButtonPressed(buttons, lt, rt, aux);
                    lastGyroActivationButtonPressed = holdPressed;
                    return holdPressed;
                case 2: // Toggle
                    bool togglePressed = IsActivationButtonPressed(buttons, lt, rt, aux);
                    if (togglePressed && !lastGyroActivationButtonPressed)
                    {
                        gyroToggleActive = !gyroToggleActive;
                    }
                    lastGyroActivationButtonPressed = togglePressed;
                    return gyroToggleActive;
                default: // Always
                    lastGyroActivationButtonPressed = IsActivationButtonPressed(buttons, lt, rt, aux);
                    return true;
            }
        }

        private bool IsActivationButtonPressed(ushort buttons, byte lt, byte rt, ushort aux)
        {
            switch (gyroActivationButton)
            {
                case 1: return rt > XINPUT_TRIGGER_THRESHOLD;
                case 2: return lt > XINPUT_TRIGGER_THRESHOLD;
                case 3: return (buttons & BTN_RB) != 0;
                case 4: return (buttons & BTN_LB) != 0;
                case 5: return (buttons & BTN_A) != 0;
                case 6: return (buttons & BTN_B) != 0;
                case 7: return (buttons & BTN_X) != 0;
                case 8: return (buttons & BTN_Y) != 0;
                case 9: return (buttons & BTN_RIGHT_THUMB) != 0;
                case 10: return (buttons & BTN_LEFT_THUMB) != 0;
                case 11: return (buttons & BTN_DPAD_UP) != 0;
                case 12: return (buttons & BTN_DPAD_DOWN) != 0;
                case 13: return (buttons & BTN_DPAD_LEFT) != 0;
                case 14: return (buttons & BTN_DPAD_RIGHT) != 0;
                case 15: return (buttons & BTN_START) != 0;
                case 16: return (buttons & BTN_BACK) != 0;
                // Legion-only aux buttons; only meaningful on LegionHid input source
                // (XInput input source can't see these — aux will be 0).
                case 17: return (aux & LEGION_AUX_R2) != 0;   // M3
                case 18: return (aux & LEGION_AUX_RM1) != 0;  // M1
                case 19: return (aux & LEGION_AUX_R3) != 0;   // M2
                case 20: return (aux & LEGION_AUX_L1) != 0;   // Y1
                case 21: return (aux & LEGION_AUX_L2) != 0;   // Y2
                case 22: return (aux & LEGION_AUX_R1) != 0;   // Y3
                default: return false;
            }
        }

        // ----------------------------------------------------------------------
        // Math (port of ControllerEmulationManager.GyroStick.cs:24-171)
        // ----------------------------------------------------------------------

        private long stickTouchActiveTicks = 0;

        // Optional sink for live readings used by the widget visualizer.
        // Manager registers a delegate on init to forward into its throttled
        // PublishStickGyroLiveReadings. Keeping this loose avoids a hard
        // dependency from the Viiper processor back to the manager.
        public static Action<float, float, float, short, short, bool> LiveReadingsSink;

        private void ApplyStickFromGyro(GyroSample sample, out short outputX, out short outputY)
        {
            ApplyStickFromGyro(sample, 0, 0, out outputX, out outputY);
        }

        private void ApplyStickFromGyro(GyroSample sample, short physicalStickX, short physicalStickY, out short outputX, out short outputY)
        {
            // Capture the raw input before the bias-passthrough and conversion so
            // the per-stage diagnostic below has the unmodified gyro for comparison.
            float rawGyroX = sample.GyroXDegPerSecond;
            float rawGyroY = sample.GyroYDegPerSecond;
            float rawGyroZ = sample.GyroZDegPerSecond;

            // 0a. Sensor fusion update — push the RAW sample (pre-bias-correction)
            //     into JSL so its continuous-calibration "is steady" detector and
            //     internal gyro-bias estimator see actual sensor data rather than
            //     a sample we already corrected. Without raw input, JSL learns
            //     bias~=0 against an already-flat signal and the gravity vector
            //     drifts when our biasEstimator is wrong; with raw input, JSL
            //     subtracts the right offset on its own and produces a stable
            //     gravity estimate even during tilt motion.
            long sampleTicks = sample.TimestampTicksUtc;
            float jslDeltaSeconds = gamepadMotionLastTicksUtc > 0 && sampleTicks > gamepadMotionLastTicksUtc
                ? (float)((sampleTicks - gamepadMotionLastTicksUtc) / (double)TimeSpan.TicksPerSecond)
                : DefaultDeltaSeconds;
            gamepadMotionLastTicksUtc = sampleTicks;

            // Always feed JSL the real accelerometer reading. We previously
            // overrode it with a synthetic (0,1,0) when orientation==Handheld
            // to make Player/World Space behave like Mode 0 in tilted poses,
            // but that permanently broke JSL's complementary filter — gravity
            // tracking died, every transient looked like "not gravity," and
            // the projections degenerated. JSL must always see the real accel;
            // the orientation toggle is meant for the legacy modes
            // 0/1/2 only and Player/World Space self-adapt to held orientation
            // through their gravity-aware math.
            gamepadMotion.Update(
                rawGyroX, rawGyroY, rawGyroZ,
                sample.AccelXG, sample.AccelYG, sample.AccelZG,
                jslDeltaSeconds);

            // 0b. Read gyro from JSL's calibrated path (raw - learned bias) for
            //     ALL conversion modes, not just the JSL-projected ones. This
            //     puts modes 0/1/2 on the same input feed as Player/World
            //     Space, so the user-perceived "smooth motion in Player Space
            //     vs jittery in Yaw" gap closes — they were eating different
            //     bias-correction paths before.
            gamepadMotion.GetCalibratedGyro(out float jslGyroX, out float jslGyroY, out float jslGyroZ);

            // Spike rejection — see legacy CE GyroStick.cs for the rationale.
            // Filters single-sample USB/EMI transmission glitches that would
            // otherwise produce one-frame stick flickers at idle.
            const float SpikeIdleFloorDps = 5.0f;
            const float SpikeMagnitudeDps = 60.0f;
            float curMaxAbs = Math.Max(Math.Abs(jslGyroX), Math.Max(Math.Abs(jslGyroY), Math.Abs(jslGyroZ)));
            float smoothedMaxAbs = Math.Max(Math.Abs(smoothedGyroXState), Math.Max(Math.Abs(smoothedGyroYState), Math.Abs(smoothedGyroZState)));
            if (smoothedGyroPrimed && smoothedMaxAbs < SpikeIdleFloorDps && curMaxAbs > SpikeMagnitudeDps)
            {
                jslGyroX = smoothedGyroXState;
                jslGyroY = smoothedGyroYState;
                jslGyroZ = smoothedGyroZState;
            }

            // Light EMA smoothing on the calibrated gyro stream. See legacy CE
            // GyroStick file for the rationale; same constants/formula here.
            if (stickGyroSmoothing > 0)
            {
                float alpha = stickGyroSmoothing / 100.0f;
                if (!smoothedGyroPrimed)
                {
                    smoothedGyroXState = jslGyroX;
                    smoothedGyroYState = jslGyroY;
                    smoothedGyroZState = jslGyroZ;
                    smoothedGyroPrimed = true;
                }
                else
                {
                    smoothedGyroXState = alpha * smoothedGyroXState + (1.0f - alpha) * jslGyroX;
                    smoothedGyroYState = alpha * smoothedGyroYState + (1.0f - alpha) * jslGyroY;
                    smoothedGyroZState = alpha * smoothedGyroZState + (1.0f - alpha) * jslGyroZ;
                }
                jslGyroX = smoothedGyroXState;
                jslGyroY = smoothedGyroYState;
                jslGyroZ = smoothedGyroZState;
            }

            // Diagnostics window accumulators — flushed in the per-second log block below.
            diagSampleCount++;
            if (rawGyroX < diagGyroXMin) diagGyroXMin = rawGyroX; if (rawGyroX > diagGyroXMax) diagGyroXMax = rawGyroX;
            if (rawGyroY < diagGyroYMin) diagGyroYMin = rawGyroY; if (rawGyroY > diagGyroYMax) diagGyroYMax = rawGyroY;
            if (rawGyroZ < diagGyroZMin) diagGyroZMin = rawGyroZ; if (rawGyroZ > diagGyroZMax) diagGyroZMax = rawGyroZ;
            float accelMagSq = sample.AccelXG * sample.AccelXG + sample.AccelYG * sample.AccelYG + sample.AccelZG * sample.AccelZG;
            float accelMag = (float)Math.Sqrt(accelMagSq);
            if (accelMag < diagAccelMagMin) diagAccelMagMin = accelMag; if (accelMag > diagAccelMagMax) diagAccelMagMax = accelMag;
            if (diagPrevValid)
            {
                float dx = Math.Abs(rawGyroX - diagPrevGyroX);
                float dy = Math.Abs(rawGyroY - diagPrevGyroY);
                float dz = Math.Abs(rawGyroZ - diagPrevGyroZ);
                if (dx > DiagSpikeThresholdDegPerSec || dy > DiagSpikeThresholdDegPerSec || dz > DiagSpikeThresholdDegPerSec)
                {
                    diagSpikeCount++;
                }
            }
            diagPrevGyroX = rawGyroX; diagPrevGyroY = rawGyroY; diagPrevGyroZ = rawGyroZ; diagPrevValid = true;

            // Run the legacy biasEstimator just to keep its internal "is
            // stationary" state warm (some other callers consult it).
            biasEstimator.Correct(sample);

            // 1. Orientation correction — SwapYawRoll:
            //    (X, Y, Z) → (X, -Z, -Y). Both Y and Z are negated, not just Z.
            //    Ours used to do (X, Z, -Y) which dropped the sign on Y, so a
            //    handheld-posture user's real yaw signal landed on +gyroZ
            //    (Mode 0 doesn't use Z) and pure noise floor landed on +gyroY
            //    (Mode 0's horizontal). That is what produced the saturated
            //    noise-driven stutter in vvalente30's logs.
            float gyroX = jslGyroX;
            float gyroY = jslGyroY;
            float gyroZ = jslGyroZ;
            if (stickOrientationV2 == 1)
            {
                float origY = gyroY;
                float origZ = gyroZ;
                gyroY = -origZ;
                gyroZ = -origY;
            }

            // 2. 3DOF → 2D
            float horizontal;
            float vertical;
            switch (stickConversion)
            {
                case 1: // Roll
                    horizontal = gyroZ;
                    vertical = gyroX;
                    break;
                case 2: // Yaw + Roll (averaged so horizontal stays magnitude-symmetric with vertical;
                        // summing the two horizontal sources gave horizontal an effective 2x boost
                        // from incidental wrist roll during pure-yaw motion, killing slow-end pitch
                        // accuracy. Sensitivity slider now applies to both axes equally.)
                    horizontal = (gyroY + gyroZ) * 0.5f;
                    vertical = gyroX;
                    break;
                case 3: // Player Space — gravity-aware projection. Good for
                        // device held roughly flat; assumes pitch axis = device-X.
                        // For tilted handhelds the pitch axis assumption breaks
                        // down — use Player Space when the device is held flat,
                        // World Space (case 4) when held at an angle.
                    gamepadMotion.GetPlayerSpaceGyro(out horizontal, out vertical, gyroY, gyroX);

                    // Diagnostic — log JSL's gravity vector + raw IMU + projection
                    // result periodically. Compare to expected: device flat with
                    // screen up should give gravity ≈ (0, +1, 0) in JSL's Y-up
                    // convention. Anything else means our IMU sign/axis convention
                    // doesn't match what JSL expects, which is the most likely
                    // cause of the "roll/yaw swapped" feel.
                    {
                        long nowPsDiag = DateTime.UtcNow.Ticks;
                        if (nowPsDiag - playerSpaceDiagLastTicks >= PipelineDiagIntervalTicks)
                        {
                            playerSpaceDiagLastTicks = nowPsDiag;
                            gamepadMotion.GetGravity(out float gvx, out float gvy, out float gvz);
                            Logger.Info(
                                "PlayerSpace diag: rawGyro=({0:F1},{1:F1},{2:F1}) " +
                                "rawAccel=({3:F2},{4:F2},{5:F2})G | " +
                                "JSL.gravity=({6:F2},{7:F2},{8:F2}) | " +
                                "JSL→(h={9:F1},v={10:F1})",
                                rawGyroX, rawGyroY, rawGyroZ,
                                sample.AccelXG, sample.AccelYG, sample.AccelZG,
                                gvx, gvy, gvz,
                                horizontal, vertical);
                        }
                    }
                    break;
                case 4: // World Space — gravity-aware projection that re-derives
                        // the pitch axis from the current gravity vector instead
                        // of assuming pitch = device-X. Better than Player Space
                        // when the device is held at an angle (tilted handheld);
                        // for flat-held devices the two produce identical output.
                        // Includes JSL's "side reduction" — output fades to zero
                        // when the device is held nearly vertical or upside down,
                        // preventing huge swings from edge-case orientations.
                    gamepadMotion.GetWorldSpaceGyro(out horizontal, out vertical, gyroY, gyroX);

                    // Reuse the Player Space diagnostic throttle so World Space
                    // also emits one log line per second with gravity + output.
                    {
                        long nowWsDiag = DateTime.UtcNow.Ticks;
                        if (nowWsDiag - playerSpaceDiagLastTicks >= PipelineDiagIntervalTicks)
                        {
                            playerSpaceDiagLastTicks = nowWsDiag;
                            gamepadMotion.GetGravity(out float gvx, out float gvy, out float gvz);
                            Logger.Info(
                                "WorldSpace diag: rawGyro=({0:F1},{1:F1},{2:F1}) " +
                                "rawAccel=({3:F2},{4:F2},{5:F2})G | " +
                                "JSL.gravity=({6:F2},{7:F2},{8:F2}) | " +
                                "JSL→(h={9:F1},v={10:F1})",
                                rawGyroX, rawGyroY, rawGyroZ,
                                sample.AccelXG, sample.AccelYG, sample.AccelZG,
                                gvx, gvy, gvz,
                                horizontal, vertical);
                        }
                    }
                    break;
                default: // Yaw
                    horizontal = gyroY;
                    vertical = gyroX;
                    break;
            }
            // Bake in the Invert X preference — see legacy CE GyroStick.cs for
            // the rationale. Inverting horizontal at this single point covers
            // every conv mode (Yaw/Roll/Yaw+Roll/Player Space/World Space).
            horizontal = -horizontal;

            // 3. Axis invert (user-facing toggles, for in-game preference)
            if (stickInvertX) horizontal = -horizontal;
            if (stickInvertY) vertical = -vertical;

            // 4. Sensitivity curve + clamp:
            //      output * ApplyCustomSensitivity(curve, threshold)
            //              * sensitivityX/Y                (= sensitivity * 1000)
            //              -> clamp to int16
            //    The lookup-table curve is currently a flat 0.5-everywhere stub,
            //    so the effective factor before per-axis sens is 1.0 across all
            //    gyro magnitudes. Per-axis sens defaults to
            //    stickSensitivityV2/100 * 1000 so a user-facing "1×" produces
            //    ~1000 stick units per °/s.
            //
            //    All previous band-aids (noise floor, anti-deadzone offset, EMA
            //    smoother) are removed. They were patching around the absence of
            //    a real sensitivity curve; with the curve, slow precision motion
            //    naturally produces small-but-visible stick output (~5% at 1°/s
            //    with default 1× sens) and fast motion saturates cleanly.
            float[] curveTable = SelectStickGyroCurveViiper(stickGyroCurvePreset);
            float effectiveCurveX = ApplyCustomSensitivity(horizontal, GyroThresholdDegPerSec, curveTable);
            float effectiveCurveY = ApplyCustomSensitivity(vertical,   GyroThresholdDegPerSec, curveTable);
            float baseSens = Math.Max(0.01f, stickSensitivityV2 / 100.0f) * HcSensitivityScale;
            float vertSens = baseSens * Math.Max(10, Math.Min(200, stickGyroVerticalRatio)) / 100.0f;
            float tightenX = ComputeTightenGainViiper(horizontal, stickGyroTightenThreshold, stickGyroTightenGain);
            float tightenY = ComputeTightenGainViiper(vertical,   stickGyroTightenThreshold, stickGyroTightenGain);
            float scaledX = horizontal * effectiveCurveX * baseSens * tightenX;
            float scaledY = vertical   * effectiveCurveY * vertSens * tightenY;

            // Stick-touch deactivation: suppress gyro output if user is moving
            // the physical stick. Hold-off prevents gyro snap-back when stick
            // returns to center.
            if (stickGyroTouchDeactivateEnabled)
            {
                if (UpdateStickTouchSuppressionViiper(physicalStickX, physicalStickY,
                        stickGyroTouchDeactivateThreshold, stickGyroTouchDeactivateHoldoff))
                {
                    scaledX = 0.0f; scaledY = 0.0f;
                }
            }

            // Anti-deadzone for small-motion precision. See legacy CE
            // GyroStick file for the rationale; same constants/formula here.
            float adzInt16 = (stickGyroAntiDeadzonePercent / 100.0f) * short.MaxValue;
            float adzThresholdDps = stickGyroAntiDeadzoneThresholdTenths / 10.0f;
            scaledX = ApplyAntiDeadzoneStickGyro(horizontal, scaledX, adzInt16, adzThresholdDps);
            scaledY = ApplyAntiDeadzoneStickGyro(vertical,   scaledY, adzInt16, adzThresholdDps);

            outputX = ClampToInt16(scaledX);
            outputY = ClampToInt16(scaledY);

            LiveReadingsSink?.Invoke(horizontal, vertical, 0.0f, outputX, outputY, true);

            // Per-second diagnostic — logs raw IMU + JSL gravity (so we can see
            // why a pure-axis motion produces unexpected horizontal/vertical
            // splits — usually the gravity tilt is making one device axis
            // align with world-up and JSL's projection is doing the right
            // thing on a held-funny handheld) + final output.
            long nowDiag = DateTime.UtcNow.Ticks;
            float rawMag = Math.Abs(rawGyroX) + Math.Abs(rawGyroY) + Math.Abs(rawGyroZ);
            if (rawMag > 5.0f && (nowDiag - pipelineDiagLastTicks) >= PipelineDiagIntervalTicks)
            {
                pipelineDiagLastTicks = nowDiag;
                gamepadMotion.GetGravity(out float gvx, out float gvy, out float gvz);
                float gravMag = (float)Math.Sqrt(gvx * gvx + gvy * gvy + gvz * gvz);
                Logger.Info(
                    "VIIPER stick-gyro pipeline: raw=({0:F1},{1:F1},{2:F1}) " +
                    "accel=({3:F2},{4:F2},{5:F2})G grav=({6:F2},{7:F2},{8:F2})|m={15:F2} | " +
                    "conv{9}→H={10:F1} V={11:F1} | sens=x{12:F2} | out=({13},{14})",
                    rawGyroX, rawGyroY, rawGyroZ,
                    sample.AccelXG, sample.AccelYG, sample.AccelZG,
                    gvx, gvy, gvz,
                    stickConversion, horizontal, vertical,
                    Math.Max(0.01f, stickSensitivityV2 / 100.0f),
                    outputX, outputY,
                    gravMag);
                Logger.Info(
                    "VIIPER stick-gyro window (1s): n={0} spikes={1} | gyroX[{2:F0},{3:F0}] gyroY[{4:F0},{5:F0}] gyroZ[{6:F0},{7:F0}] | accelMag[{8:F2},{9:F2}]G | gravMag={10:F2}",
                    diagSampleCount, diagSpikeCount,
                    diagGyroXMin == float.MaxValue ? 0 : diagGyroXMin, diagGyroXMax == float.MinValue ? 0 : diagGyroXMax,
                    diagGyroYMin == float.MaxValue ? 0 : diagGyroYMin, diagGyroYMax == float.MinValue ? 0 : diagGyroYMax,
                    diagGyroZMin == float.MaxValue ? 0 : diagGyroZMin, diagGyroZMax == float.MinValue ? 0 : diagGyroZMax,
                    diagAccelMagMin == float.MaxValue ? 0 : diagAccelMagMin, diagAccelMagMax == float.MinValue ? 0 : diagAccelMagMax,
                    gravMag);
                diagGyroXMin = float.MaxValue; diagGyroXMax = float.MinValue;
                diagGyroYMin = float.MaxValue; diagGyroYMax = float.MinValue;
                diagGyroZMin = float.MaxValue; diagGyroZMax = float.MinValue;
                diagAccelMagMin = float.MaxValue; diagAccelMagMax = float.MinValue;
                diagSampleCount = 0;
                diagSpikeCount = 0;
            }
        }

        // ----------------------------------------------------------------------
        // Self-test — runs the full pipeline on synthetic inputs so we can
        // verify the math without needing live IMU data. Called from the helper
        // CLI / pipe debug commands; logs each test case's input → output for
        // comparison against expected values. Exposed as a static helper so it
        // doesn't depend on a live forwarder/JSL handle (it builds its own).
        // ----------------------------------------------------------------------
        public static void RunSelfTest()
        {
            Logger.Info("=== VIIPER stick-gyro self-test ===");

            // Test 1: device flat (gravity ≈ (0, +1, 0) after JSL fusion converges)
            // Expected: pure yaw → horizontal, pure pitch → vertical, pure roll → ~0
            RunTestCase("FLAT, pure yaw +30deg/s", flat: true,
                gyroX: 0, gyroY: 30, gyroZ: 0,
                expectedHorizontal: -30 * 1.41f, // -worldYaw with sign flip
                expectedVertical: 0);
            RunTestCase("FLAT, pure pitch +30deg/s", flat: true,
                gyroX: 30, gyroY: 0, gyroZ: 0,
                expectedHorizontal: 0,
                expectedVertical: 30);
            RunTestCase("FLAT, pure roll +30deg/s", flat: true,
                gyroX: 0, gyroY: 0, gyroZ: 30,
                expectedHorizontal: 0,
                expectedVertical: 0);

            // Test 2: handheld tilted forward 45° — gravity ≈ (0, -0.5, +0.5*sqrt(2))
            // Player Space mixes yaw+roll → roll motion produces horizontal output
            RunTestCase("TILTED 45° forward, pure yaw +30", flat: false,
                gyroX: 0, gyroY: 30, gyroZ: 0,
                expectedHorizontal: float.NaN, // depends on tilt; just for inspection
                expectedVertical: 0);
            RunTestCase("TILTED 45° forward, pure roll +30", flat: false,
                gyroX: 0, gyroY: 0, gyroZ: 30,
                expectedHorizontal: float.NaN,
                expectedVertical: 0);

            Logger.Info("=== self-test end ===");
        }

        private static void RunTestCase(string name, bool flat,
            float gyroX, float gyroY, float gyroZ,
            float expectedHorizontal, float expectedVertical)
        {
            // Build a fresh JSL instance and prime it with a few accel samples to
            // converge gravity in the chosen orientation.
            using var motion = new GamepadMotion();
            float ax, ay, az;
            if (flat)
            {
                // Y-up at rest: accelerometer reads pull-against-gravity along +Y.
                ax = 0; ay = 1.0f; az = 0;
            }
            else
            {
                // Tilted 45° forward — gravity has equal Y and Z components.
                ax = 0; ay = 0.707f; az = -0.707f;
            }
            // Prime fusion with ~50 samples at 16ms each = 0.8s of "rest" so JSL
            // converges its gravity vector before we apply the gyro motion.
            for (int i = 0; i < 50; i++)
                motion.Update(0, 0, 0, ax, ay, az, 0.016f);
            // Apply the test gyro for one frame (with the same accel reading).
            motion.Update(gyroX, gyroY, gyroZ, ax, ay, az, 0.016f);
            motion.GetPlayerSpaceGyro(out float h, out float v, gyroY, gyroX);
            motion.GetGravity(out float gx, out float gy, out float gz);

            string expectedStr = float.IsNaN(expectedHorizontal)
                ? "(no specific expectation)"
                : $"expected H≈{expectedHorizontal:F1}, V≈{expectedVertical:F1}";
            Logger.Info($"  [{name}] grav=({gx:F2},{gy:F2},{gz:F2}) → H={h:F1} V={v:F1}  {expectedStr}");
        }

        // ----- Sensitivity curve (replaces noise gate / anti-deadzone / EMA) -----
        //
        // 49-node lookup table with values in [0, 1];
        // default is flat 0.5 across the board (== effective multiplier 1.0 after the
        // ×2 below). Each node represents one multiple of GyroThresholdDegPerSec;
        // linear interpolation between adjacent nodes. Above the last node, output
        // saturates at 2 × curve[last].
        //
        // GyroThresholdDegPerSec = 124 °/s. Tunes how fast the user has to move
        // before they "fall off" node 0 of the curve.
        //
        // HcSensitivityScale = 1000 — per-axis sensitivity default is 1.0,
        // multiplied by 1000 to convert deg/s into stick units. We bake the same
        // scale into a single user-facing slider that defaults to 1×.
        private const float GyroThresholdDegPerSec = 124.0f;
        private const float HcSensitivityScale = 1000.0f;
        private const int SensCurveNodeCount = 49;
        private static readonly float[] DefaultSensCurve = MakeFlatCurve(0.5f);

        private static float[] MakeFlatCurve(float value)
        {
            var arr = new float[SensCurveNodeCount];
            for (int i = 0; i < arr.Length; i++) arr[i] = value;
            return arr;
        }

        /// <summary>
        /// Lookup-and-interpolate sensitivity multiplier for a given gyro axis
        /// magnitude: position = |value| / threshold; clamped to curve range; ×2
        /// scale on output so a flat 0.5 curve produces an effective 1.0 multiplier.
        /// </summary>
        private static float ApplyCustomSensitivity(float value, float threshold, float[] curve)
        {
            if (curve == null || curve.Length == 0 || threshold <= 0.0f) return 1.0f;

            float position = Math.Abs(value) / threshold;
            if (position >= curve.Length - 1)
            {
                return curve[curve.Length - 1] * 2.0f;
            }

            int lo = (int)Math.Floor(position);
            int hi = lo + 1;
            float t = position - lo;
            return ((1.0f - t) * curve[lo] + t * curve[hi]) * 2.0f;
        }

        // Anti-deadzone constants — see ControllerEmulationManager.GyroStick.cs
        // for full rationale. Smooth rescale: tiny non-zero motions land just
        // past typical game stick deadzones (5–10%) instead of being silently
        // killed; large motions still saturate at int16 max cleanly.
        // Smooth ramp version — see ControllerEmulationManager.GyroStick.cs
        // for the rationale. Mirror implementation; both pipelines share the
        // same persisted threshold/anti-deadzone settings.
        // Curve presets — same shapes as the legacy CE GyroStick variant so
        // both pipelines feel identical with the same preset selection.
        private static readonly float[] StickGyroCurveLinearViiper = MakeFlatCurve(0.5f);
        private static readonly float[] StickGyroCurveSlowViiper = MakeSlowPreciseCurve();
        private static readonly float[] StickGyroCurveSnapViiper = MakeSnapAimCurve();

        private static float[] SelectStickGyroCurveViiper(int preset)
        {
            switch (preset)
            {
                case 1: return StickGyroCurveSlowViiper;
                case 2: return StickGyroCurveSnapViiper;
                default: return StickGyroCurveLinearViiper;
            }
        }

        private static float[] MakeSlowPreciseCurve()
        {
            var arr = new float[SensCurveNodeCount];
            for (int i = 0; i < arr.Length; i++)
            {
                if (i <= 5) arr[i] = 0.20f;
                else if (i <= 15) arr[i] = 0.20f + (0.5f - 0.20f) * (i - 5) / 10.0f;
                else arr[i] = 0.5f;
            }
            return arr;
        }

        private static float[] MakeSnapAimCurve()
        {
            var arr = new float[SensCurveNodeCount];
            for (int i = 0; i < arr.Length; i++)
            {
                if (i <= 3) arr[i] = 0.20f;
                else if (i <= 10) arr[i] = 0.20f + (0.5f - 0.20f) * (i - 3) / 7.0f;
                else if (i <= 25) arr[i] = 0.5f;
                else if (i <= 40) arr[i] = 0.5f + (0.85f - 0.5f) * (i - 25) / 15.0f;
                else arr[i] = 0.85f;
            }
            return arr;
        }

        private static float ComputeTightenGainViiper(float gyroDps, int thresholdDps, int gainPercent)
        {
            if (thresholdDps <= 0 || gainPercent <= 100) return 1.0f;
            float absGyro = Math.Abs(gyroDps);
            if (absGyro <= thresholdDps) return 1.0f;
            float rampWidth = thresholdDps * 2.0f;
            float ramp = Math.Min(1.0f, (absGyro - thresholdDps) / rampWidth);
            float maxBoost = gainPercent / 100.0f;
            return 1.0f + (maxBoost - 1.0f) * ramp;
        }

        private bool UpdateStickTouchSuppressionViiper(short physicalX, short physicalY, int thresholdPct, int holdoffMs)
        {
            float magPct = (float)Math.Sqrt(physicalX * (double)physicalX + physicalY * (double)physicalY) / short.MaxValue * 100.0f;
            long now = DateTime.UtcNow.Ticks;
            if (magPct >= thresholdPct)
            {
                stickTouchActiveTicks = now;
                return true;
            }
            long heldOffTicks = holdoffMs * TimeSpan.TicksPerMillisecond;
            return stickTouchActiveTicks > 0 && (now - stickTouchActiveTicks) < heldOffTicks;
        }

        // Decoupled noise-floor gate + anti-deadzone offset. The threshold
        // ALWAYS gates noise below threshold/2 (regardless of anti-deadzone
        // setting); the anti-deadzone offset is optional (skipped when
        // adzInt16 == 0). Previously these were coupled and setting anti-
        // deadzone to 0 silently disabled the threshold too.
        private static float ApplyAntiDeadzoneStickGyro(float gyroDps, float scaledOutput, float adzInt16, float adzThresholdDps)
        {
            float absGyro = Math.Abs(gyroDps);
            if (adzThresholdDps > 0.0f && absGyro < adzThresholdDps * 0.5f) return 0.0f;
            if (adzInt16 <= 0.0f) return scaledOutput;
            float deadFloor = adzThresholdDps * 0.5f;
            float ramp = adzThresholdDps > deadFloor
                ? Math.Min(1.0f, (absGyro - deadFloor) / (adzThresholdDps - deadFloor))
                : 1.0f;
            float sign = scaledOutput >= 0.0f ? 1.0f : -1.0f;
            float absScaled = Math.Abs(scaledOutput);
            float effectiveAdz = adzInt16 * ramp;
            float remap = effectiveAdz + absScaled * (1.0f - effectiveAdz / short.MaxValue);
            return sign * remap;
        }

        private static short ClampToInt16(float value)
        {
            if (value > short.MaxValue) return short.MaxValue;
            if (value < short.MinValue) return short.MinValue;
            return (short)value;
        }

        /// <summary>
        /// Sum-then-clamp merge of physical right-stick + gyro stick contribution.
        /// Mirrors <c>ControllerEmulationManager.MergeStickVectors</c>. When the
        /// vector sum exceeds the int16 range, scales both axes proportionally so
        /// direction is preserved.
        /// </summary>
        public static void MergeStickVectors(short physicalX, short physicalY, short gyroX, short gyroY,
            out short mergedX, out short mergedY)
        {
            float sumX = physicalX + gyroX;
            float sumY = physicalY + gyroY;
            float magnitude = (float)Math.Sqrt((sumX * sumX) + (sumY * sumY));
            if (magnitude > short.MaxValue && magnitude > 0.0f)
            {
                float scale = short.MaxValue / magnitude;
                sumX *= scale;
                sumY *= scale;
            }
            mergedX = ClampToInt16(sumX);
            mergedY = ClampToInt16(sumY);
        }
    }
}
