using NLog;
using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using Windows.Devices.Sensors;
using XboxGamingBarHelper.Settings;

namespace XboxGamingBarHelper.Systems
{
    /// <summary>
    /// Helper-managed adaptive brightness loop. Replacement for Windows ADAPTBRIGHT.
    /// The design goal is iOS-style behavior: rarely needs adjustment, doesn't twitch on
    /// transient flashes, smooth ramps, and learns from manual overrides.
    ///
    /// Pipeline (per ALS reading):
    ///   raw lux
    ///     -> One-Euro filter on log(1 + lux)   adaptive low-pass; tightens when stable,
    ///                                          opens when changing fast
    ///     -> log curve   base = min + (filteredLogLux / log(1+lux_ref)) * (max-min) * sensitivity
    ///     -> learned offset for current lux bucket  target = base + offset
    ///     -> sustained-change requirement (target must hold N ticks before we act)
    ///     -> direction-aware response (brighten quickly, dim slowly)
    ///     -> eased ramp (sub-target updates every InterpTickMs toward committed target)
    ///     -> log-lux delta override hold (slider settles -> learned; new commits paused
    ///                                until ambient changes by ≥ OverrideLuxRatio)
    ///
    /// Learning: when the user manually adjusts brightness, we wait until the slider
    /// settles (3 consecutive identical reads), compute final - base against the lux
    /// observed *at settle time*, and EWMA-blend that into the corresponding lux
    /// bucket's offset. Future tick predictions in that lux range bias toward what
    /// the user picked. Same idea as iOS — base curve from the sensor, learned
    /// per-environment offset on top.
    ///
    /// Thread model: a single <c>gate</c> lock serializes every shared-state mutation
    /// across the three callback sources (ALS sensor thread, eval timer, ramp timer).
    /// WMI calls are issued inside the lock — they take tens of ms but the sensor
    /// only fires every ~250 ms, so blocking the sensor thread briefly is fine and
    /// avoids the snapshot/recheck dance that a release-during-WMI design would need.
    /// </summary>
    internal sealed class AdaptiveBrightnessManager : IDisposable
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        // ---- Curve / range ----
        private const int DefaultMinBrightness = 5;
        private const int MaxBrightness = 100;
        private const double LuxReference = 1000.0;
        private const double Sensitivity = 1.0;
        // Constant — hoisted out of the hot path.
        private static readonly double LuxReferenceLog = Math.Log(1.0 + LuxReference);
        private const string MinBrightnessSettingsKey = "HelperAdaptiveBrightnessMinFloor";

        // ---- One-Euro filter constants (operate on log(1+lux) for perceptual uniformity) ----
        // Lower MinCutoff = smoother but laggier; Beta scales how fast the cutoff opens
        // when the signal moves. Tuned against handheld ALS noise floor (~5–15% jitter
        // even in stable lighting): MinCutoff=0.30 Hz keeps idle drift < 1 brightness step,
        // Beta=0.40 lets a real lighting change land within ~600 ms.
        private const double OneEuroMinCutoff = 0.30;
        private const double OneEuroBeta = 0.40;
        private const double OneEuroDerivativeCutoff = 1.0;

        // ---- Sustained-change requirement (the iPhone "rarely needs adjustment" core) ----
        private const int SustainedTicksBrighten = 3;
        private const int SustainedTicksDim = 6;

        // ---- Action thresholds ----
        private const int BrightenThreshold = 3;
        private const int DimThreshold = 6;

        // ---- Eased ramp ----
        private const int InterpTickMs = 30;
        private const int RampDurationMsBrighten = 200;
        private const int RampDurationMsDim = 600;

        // ---- Override hold ----
        // Expressed as a ratio for readability, but compared in log(1+lux) space so the
        // check stays well-behaved when the commit lux is near zero (pitch-dark room).
        private const double OverrideLuxRatio = 2.0;
        private static readonly double OverrideLogLuxDelta = Math.Log(OverrideLuxRatio);

        // ---- Tick + safety ----
        private const int EvalTickMs = 250;
        private static readonly TimeSpan UserOverrideMaxHold = TimeSpan.FromHours(8);
        private const int SettleSamples = 3;

        // ---- Learning ----
        // 6 buckets: [0,2) [2,10) [10,50) [50,200) [200,1000) [1000,inf).
        private static readonly double[] BucketUpper = { 2.0, 10.0, 50.0, 200.0, 1000.0, double.PositiveInfinity };
        private const double LearningAlpha = 0.3;
        private const int OffsetClamp = 40;
        private const string OffsetsSettingsKey = "HelperAdaptiveBrightnessOffsets";

        // ---- State (all access guarded by gate unless noted) ----
        private readonly object gate = new object();
        private volatile bool disposed;
        private int minBrightness = DefaultMinBrightness;

        private LightSensor sensor;
        private Timer evalTimer;
        private Timer rampTimer;

        // One-Euro filter state (tracking log(1+lux)).
        private double oeLastLogLux = double.NaN;
        private double oeLastDerivative;
        private long oeLastTimestamp;
        private bool oeInitialized;

        // Sustained-change accumulator.
        private int sustainedDir;          // -1, 0, +1
        private int sustainedCount;

        // Eased ramp.
        private int rampCurrentBrightness;
        private int rampTargetBrightness = -1;
        private long rampStartTicks;
        private int rampDurationMs;

        // Override / learning.
        private int lastWrittenBrightness = -1;
        private bool overrideActive;
        private double overrideLogLuxAtCommit;
        private DateTime overrideUntilUtc;
        private bool overridePending;
        private int overrideLastSample = -1;
        private int overrideStableCount;
        private bool bootstrapped;

        private readonly double[] offsets = new double[BucketUpper.Length];

        // Telemetry counters (lock-guarded; read out at Stop()).
        private long telemetryCommits;
        private long telemetryOverrides;
        private long telemetryOverrideReleases;
        private long telemetryUserOverrideStomps;
        private DateTime startedAtUtc;

        public AdaptiveBrightnessManager()
        {
            LoadOffsets();
            LoadMinBrightness();
        }

        /// <summary>
        /// Configurable lower-bound on commanded brightness. Range [0, 50]. Persisted.
        /// </summary>
        public int MinBrightnessFloor
        {
            get { lock (gate) return minBrightness; }
            set
            {
                int v = value;
                if (v < 0) v = 0;
                if (v > 50) v = 50;
                lock (gate) minBrightness = v;
                try { LocalSettingsHelper.SetValue(MinBrightnessSettingsKey, v); }
                catch (Exception ex) { Logger.Warn($"AdaptiveBrightness: SaveMinBrightness: {ex.Message}"); }
            }
        }

        public bool Start()
        {
            LightSensor sensorLocal;
            lock (gate)
            {
                if (evalTimer != null) return true;
                try
                {
                    sensorLocal = LightSensor.GetDefault();
                    if (sensorLocal == null)
                    {
                        Logger.Warn("AdaptiveBrightness: no LightSensor available on this device");
                        return false;
                    }
                    sensorLocal.ReportInterval = Math.Max(sensorLocal.MinimumReportInterval, 250);
                    sensor = sensorLocal;
                    disposed = false;
                    startedAtUtc = DateTime.UtcNow;
                    telemetryCommits = 0;
                    telemetryOverrides = 0;
                    telemetryOverrideReleases = 0;
                    telemetryUserOverrideStomps = 0;
                    bootstrapped = false;
                    overridePending = false;
                    overrideActive = false;
                    sustainedDir = 0;
                    sustainedCount = 0;
                    lastWrittenBrightness = -1;
                    rampTargetBrightness = -1;
                    oeInitialized = false;
                    oeLastLogLux = double.NaN;
                }
                catch (Exception ex)
                {
                    Logger.Error($"AdaptiveBrightness: start failed: {ex.Message}");
                    return false;
                }
            }

            // Subscribe + arm the timer outside the lock so the very first ALS callback
            // can't block on a lock the caller still holds.
            try
            {
                sensorLocal.ReadingChanged += OnReading;
                lock (gate) evalTimer = new Timer(OnEvalTick, null, EvalTickMs, EvalTickMs);
                Logger.Info($"AdaptiveBrightness: started (ALS report {sensorLocal.ReportInterval} ms, eval {EvalTickMs} ms, minBrightness={minBrightness}, offsets=[{string.Join(",", offsets.Select(o => o.ToString("F1", CultureInfo.InvariantCulture)))}])");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"AdaptiveBrightness: start arming failed: {ex.Message}");
                Stop();
                return false;
            }
        }

        public void Stop()
        {
            LightSensor s;
            Timer e;
            Timer r;
            long uptime;
            long commits, overrides, holdReleases, stomps;

            lock (gate)
            {
                disposed = true;
                s = sensor; sensor = null;
                e = evalTimer; evalTimer = null;
                r = rampTimer; rampTimer = null;
                uptime = (long)(DateTime.UtcNow - startedAtUtc).TotalSeconds;
                commits = telemetryCommits;
                overrides = telemetryOverrides;
                holdReleases = telemetryOverrideReleases;
                stomps = telemetryUserOverrideStomps;
                oeInitialized = false;
                oeLastLogLux = double.NaN;
                sustainedDir = 0;
                sustainedCount = 0;
                rampTargetBrightness = -1;
                lastWrittenBrightness = -1;
                overridePending = false;
                overrideActive = false;
                bootstrapped = false;
            }

            try { if (s != null) s.ReadingChanged -= OnReading; } catch { }
            DisposeTimerAndWait(e);
            DisposeTimerAndWait(r);

            Logger.Info($"AdaptiveBrightness: stopped (uptime {uptime}s, commits={commits}, overrides={overrides}, holdReleases={holdReleases}, stomps={stomps})");
        }

        private static void DisposeTimerAndWait(Timer t)
        {
            if (t == null) return;
            try
            {
                using (var done = new ManualResetEvent(false))
                {
                    if (t.Dispose(done)) done.WaitOne(1000);
                }
            }
            catch { try { t.Dispose(); } catch { } }
        }

        // --------------------------------------------------------------
        // ALS reading: feed One-Euro filter on log(1+lux)
        // --------------------------------------------------------------
        private void OnReading(LightSensor s, LightSensorReadingChangedEventArgs e)
        {
            if (disposed) return;
            try
            {
                double lux = e.Reading.IlluminanceInLux;
                if (lux < 0) return;
                double logLux = Math.Log(1.0 + lux);
                long now = DateTime.UtcNow.Ticks;

                lock (gate)
                {
                    if (disposed) return;
                    if (!oeInitialized)
                    {
                        oeLastLogLux = logLux;
                        oeLastDerivative = 0.0;
                        oeLastTimestamp = now;
                        oeInitialized = true;
                        return;
                    }

                    double dt = Math.Max(0.001, (now - oeLastTimestamp) / (double)TimeSpan.TicksPerSecond);
                    oeLastTimestamp = now;

                    // Derivative low-pass.
                    double rawDerivative = (logLux - oeLastLogLux) / dt;
                    double dAlpha = OneEuroAlpha(OneEuroDerivativeCutoff, dt);
                    double smoothedDerivative = oeLastDerivative + (rawDerivative - oeLastDerivative) * dAlpha;
                    oeLastDerivative = smoothedDerivative;

                    // Adaptive cutoff: opens proportionally to |derivative|. Stable signal -> tight
                    // cutoff (smooth, idle-stable). Fast change -> high cutoff (fast tracking).
                    double dynamicCutoff = OneEuroMinCutoff + OneEuroBeta * Math.Abs(smoothedDerivative);
                    double vAlpha = OneEuroAlpha(dynamicCutoff, dt);
                    oeLastLogLux = oeLastLogLux + (logLux - oeLastLogLux) * vAlpha;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"AdaptiveBrightness: reading handler: {ex.Message}");
            }
        }

        private static double OneEuroAlpha(double cutoff, double dt)
        {
            double tau = 1.0 / (2.0 * Math.PI * Math.Max(0.001, cutoff));
            return 1.0 / (1.0 + tau / dt);
        }

        // --------------------------------------------------------------
        // Eval tick: decide and commit a new target
        // --------------------------------------------------------------
        private void OnEvalTick(object _)
        {
            if (disposed) return;
            lock (gate)
            {
                if (disposed) return;
                try
                {
                    if (!oeInitialized) return;
                    double logLux = oeLastLogLux;
                    double luxFiltered = Math.Max(0.0, Math.Exp(logLux) - 1.0);

                    int currentBrightness = BrightnessManager.GetBrightness();

                    // ---- Bootstrap: first stable reading. Adopt the user's current
                    // brightness as their preference for this lux band, and arm
                    // override-hold so we don't immediately ramp away.
                    //
                    // We *seed* (not EWMA-blend) the offset because there is no
                    // follow-up sample in this session for blending to converge
                    // toward. But only do so when our prediction actually disagrees
                    // with the user's current value — if the previously-persisted
                    // offset already lands close, leave it alone rather than
                    // re-anchoring on a single sample.
                    if (!bootstrapped)
                    {
                        bootstrapped = true;
                        lastWrittenBrightness = currentBrightness;
                        int predicted = ComputeTargetLocked(luxFiltered);
                        if (Math.Abs(currentBrightness - predicted) > BrightenThreshold)
                        {
                            SeedOffsetLocked(luxFiltered, currentBrightness, predicted);
                        }
                        else
                        {
                            Logger.Info($"AdaptiveBrightness: bootstrap (lux={luxFiltered:F0}, brightness={currentBrightness}, predicted={predicted}) — within threshold, keeping learned offsets");
                        }
                        overrideActive = true;
                        overrideLogLuxAtCommit = logLux;
                        overrideUntilUtc = DateTime.UtcNow + UserOverrideMaxHold;
                        return;
                    }

                    // ---- Override detection: hardware diverged from our last write.
                    // We do this even mid-ramp so we can cancel a ramp the moment the
                    // user takes over with the slider.
                    if (lastWrittenBrightness >= 0
                        && Math.Abs(currentBrightness - lastWrittenBrightness) > 1
                        && !overridePending)
                    {
                        overridePending = true;
                        overrideLastSample = currentBrightness;
                        overrideStableCount = 1;
                        telemetryOverrides++;
                        if (rampTargetBrightness >= 0)
                        {
                            telemetryUserOverrideStomps++;
                            CancelRampLocked();
                        }
                        Logger.Info($"AdaptiveBrightness: override start (slider {currentBrightness} vs lastWrite {lastWrittenBrightness}, lux {luxFiltered:F0})");
                    }

                    // ---- Settle slider, then train.
                    if (overridePending)
                    {
                        if (currentBrightness == overrideLastSample) overrideStableCount++;
                        else { overrideLastSample = currentBrightness; overrideStableCount = 1; }

                        if (overrideStableCount >= SettleSamples)
                        {
                            // Train against lux at settle time — the user is responding
                            // to their *current* perceived ambient, not the lux that
                            // was sampled when they first touched the slider (which
                            // might have been mid-transition into a new room).
                            TrainOffset(luxFiltered, currentBrightness);
                            lastWrittenBrightness = currentBrightness;
                            overridePending = false;
                            overrideActive = true;
                            overrideLogLuxAtCommit = logLux;
                            overrideUntilUtc = DateTime.UtcNow + UserOverrideMaxHold;
                        }
                        return;
                    }

                    // ---- Override hold: don't move until ambient changes meaningfully.
                    if (overrideActive)
                    {
                        bool ratioBroken = Math.Abs(logLux - overrideLogLuxAtCommit) >= OverrideLogLuxDelta;
                        if (!ratioBroken)
                        {
                            // Ambient still matches the user's chosen environment —
                            // refresh the safety timer so we don't silently re-engage
                            // after the 8h max hold while nothing has actually changed.
                            overrideUntilUtc = DateTime.UtcNow + UserOverrideMaxHold;
                            sustainedDir = 0;
                            sustainedCount = 0;
                            return;
                        }
                        bool expired = DateTime.UtcNow > overrideUntilUtc;
                        overrideActive = false;
                        telemetryOverrideReleases++;
                        Logger.Info($"AdaptiveBrightness: override released (Δlog={(logLux - overrideLogLuxAtCommit):F2} thresh={OverrideLogLuxDelta:F2}, expired={expired})");
                    }

                    // Don't kick off a new ramp while one is in flight. The previous
                    // override-detection branch is still allowed to *cancel* an
                    // in-flight ramp; we just don't stack a new one on top.
                    if (rampTargetBrightness >= 0) return;

                    int target = ComputeTargetLocked(luxFiltered);
                    int delta = target - currentBrightness;
                    int absDelta = Math.Abs(delta);
                    int dir = Math.Sign(delta);

                    int threshold = dir > 0 ? BrightenThreshold : DimThreshold;
                    int sustainNeeded = dir > 0 ? SustainedTicksBrighten : SustainedTicksDim;

                    if (absDelta < threshold || dir == 0)
                    {
                        // Dead-band — don't act, and decay any in-flight sustain counter.
                        sustainedDir = 0;
                        sustainedCount = 0;
                        return;
                    }

                    if (dir != sustainedDir)
                    {
                        sustainedDir = dir;
                        sustainedCount = 1;
                        return;
                    }

                    sustainedCount++;
                    if (sustainedCount < sustainNeeded) return;

                    StartRampToLocked(currentBrightness, target, dir);
                    sustainedDir = 0;
                    sustainedCount = 0;
                }
                catch (Exception ex)
                {
                    Logger.Warn($"AdaptiveBrightness: eval tick: {ex.Message}");
                }
            }
        }

        // --------------------------------------------------------------
        // Eased ramp: cubic ease-out from current to committed target
        // --------------------------------------------------------------
        private void StartRampToLocked(int from, int to, int dir)
        {
            if (to == from) return;
            if (to < minBrightness) to = minBrightness;
            if (to > MaxBrightness) to = MaxBrightness;

            rampCurrentBrightness = from;
            rampTargetBrightness = to;
            rampStartTicks = DateTime.UtcNow.Ticks;
            rampDurationMs = dir > 0 ? RampDurationMsBrighten : RampDurationMsDim;

            // Replace any prior timer — should be null here but be defensive.
            try { rampTimer?.Dispose(); } catch { }
            rampTimer = new Timer(OnRampTick, null, InterpTickMs, InterpTickMs);
            telemetryCommits++;
            Logger.Info($"AdaptiveBrightness: ramp {from} -> {to} ({rampDurationMs} ms)");
        }

        private void CancelRampLocked()
        {
            if (rampTargetBrightness < 0 && rampTimer == null) return;
            rampTargetBrightness = -1;
            var r = rampTimer;
            rampTimer = null;
            // Plain Dispose (no WaitHandle) — we hold the gate that any in-flight
            // ramp tick needs. Waiting here would deadlock. The in-flight tick
            // will re-enter the lock, see rampTargetBrightness == -1, and bail.
            try { r?.Dispose(); } catch { }
        }

        private void OnRampTick(object _)
        {
            if (disposed) return;
            lock (gate)
            {
                if (disposed) return;
                try
                {
                    int target = rampTargetBrightness;
                    if (target < 0) return; // canceled or already done
                    int from = rampCurrentBrightness;

                    long elapsedMs = (DateTime.UtcNow.Ticks - rampStartTicks) / TimeSpan.TicksPerMillisecond;
                    double t = rampDurationMs <= 0 ? 1.0 : Math.Min(1.0, elapsedMs / (double)rampDurationMs);
                    double eased = 1.0 - Math.Pow(1.0 - t, 3.0); // cubic ease-out
                    int next = (int)Math.Round(from + (target - from) * eased);

                    if (next != lastWrittenBrightness)
                    {
                        BrightnessManager.SetBrightness(next);
                        lastWrittenBrightness = next;
                    }

                    if (t >= 1.0)
                    {
                        rampTargetBrightness = -1;
                        var r = rampTimer;
                        rampTimer = null;
                        try { r?.Dispose(); } catch { }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn($"AdaptiveBrightness: ramp tick: {ex.Message}");
                }
            }
        }

        // --------------------------------------------------------------
        // Curve + learned-offset application
        // --------------------------------------------------------------
        private int ComputeTargetLocked(double lux)
        {
            int baseTarget = ComputeBaseTargetLocked(lux);
            double offset = InterpolateOffsetLocked(lux);
            int adjusted = baseTarget + (int)Math.Round(offset);
            if (adjusted < minBrightness) adjusted = minBrightness;
            if (adjusted > MaxBrightness) adjusted = MaxBrightness;
            return adjusted;
        }

        private int ComputeBaseTargetLocked(double lux)
        {
            double scale = Math.Log(1.0 + lux) / LuxReferenceLog;
            scale *= Sensitivity;
            if (scale > 1.0) scale = 1.0;
            if (scale < 0.0) scale = 0.0;
            int range = MaxBrightness - minBrightness;
            return minBrightness + (int)Math.Round(scale * range);
        }

        // Blend between the bucket lux falls in and whichever neighbor lies on the
        // same side of the bucket center. This avoids the discontinuity the previous
        // "pick nearer center" logic introduced at every bucket boundary.
        private double InterpolateOffsetLocked(double lux)
        {
            int idx = BucketIndex(lux);
            double centerThis = BucketCenterLog(idx);
            double l = Math.Log(1.0 + lux);

            int otherIdx;
            if (l < centerThis) otherIdx = idx == 0 ? idx : idx - 1;
            else otherIdx = idx == BucketUpper.Length - 1 ? idx : idx + 1;

            if (otherIdx == idx) return offsets[idx];

            double otherCenter = BucketCenterLog(otherIdx);
            double span = otherCenter - centerThis;
            if (Math.Abs(span) < 1e-9) return offsets[idx];

            double t = (l - centerThis) / span;
            if (t < 0) t = 0;
            if (t > 1) t = 1;
            return offsets[idx] * (1 - t) + offsets[otherIdx] * t;
        }

        private void TrainOffset(double luxAtTraining, int finalBrightness)
        {
            int idx = BucketIndex(luxAtTraining);
            int baseTarget = ComputeBaseTargetLocked(luxAtTraining);
            double sampleOffset = finalBrightness - baseTarget;
            double updated = LearningAlpha * sampleOffset + (1 - LearningAlpha) * offsets[idx];
            if (updated > OffsetClamp) updated = OffsetClamp;
            if (updated < -OffsetClamp) updated = -OffsetClamp;
            offsets[idx] = updated;
            SaveOffsets();
            Logger.Info($"AdaptiveBrightness: learned bucket[{idx}] (lux≈{luxAtTraining:F0}) base={baseTarget} user={finalBrightness} sampleΔ={sampleOffset:F1} -> offset={offsets[idx]:F1}");
        }

        // Authoritative seed used only at bootstrap. EWMA-blending here is wrong:
        // there's no follow-up sample in this session to converge against, so we
        // must adopt the user's preference fully (clamped to the safety range).
        private void SeedOffsetLocked(double luxAtTraining, int finalBrightness, int predicted)
        {
            int idx = BucketIndex(luxAtTraining);
            int baseTarget = ComputeBaseTargetLocked(luxAtTraining);
            double sampleOffset = finalBrightness - baseTarget;
            if (sampleOffset > OffsetClamp) sampleOffset = OffsetClamp;
            if (sampleOffset < -OffsetClamp) sampleOffset = -OffsetClamp;
            double prior = offsets[idx];
            offsets[idx] = sampleOffset;
            SaveOffsets();
            Logger.Info($"AdaptiveBrightness: bootstrap-seed bucket[{idx}] (lux≈{luxAtTraining:F0}) base={baseTarget} user={finalBrightness} predicted={predicted} prior={prior:F1} -> offset={offsets[idx]:F1}");
        }

        private static int BucketIndex(double lux)
        {
            for (int i = 0; i < BucketUpper.Length; i++)
                if (lux < BucketUpper[i]) return i;
            return BucketUpper.Length - 1;
        }

        private static double BucketCenterLog(int idx)
        {
            double lower = idx == 0 ? 0.0 : BucketUpper[idx - 1];
            double upper = double.IsPositiveInfinity(BucketUpper[idx]) ? lower * 5.0 + 1.0 : BucketUpper[idx];
            return (Math.Log(1.0 + lower) + Math.Log(1.0 + upper)) * 0.5;
        }

        private void LoadOffsets()
        {
            try
            {
                if (!LocalSettingsHelper.TryGetValue<string>(OffsetsSettingsKey, out var s) || string.IsNullOrEmpty(s)) return;
                var parts = s.Split(',');
                int n = Math.Min(parts.Length, offsets.Length);
                for (int i = 0; i < n; i++)
                {
                    if (double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) offsets[i] = v;
                }
            }
            catch (Exception ex) { Logger.Warn($"AdaptiveBrightness: LoadOffsets: {ex.Message}"); }
        }

        private void SaveOffsets()
        {
            try
            {
                var s = string.Join(",", offsets.Select(o => o.ToString("F2", CultureInfo.InvariantCulture)));
                LocalSettingsHelper.SetValue(OffsetsSettingsKey, s);
            }
            catch (Exception ex) { Logger.Warn($"AdaptiveBrightness: SaveOffsets: {ex.Message}"); }
        }

        private void LoadMinBrightness()
        {
            try
            {
                if (LocalSettingsHelper.TryGetValue<int>(MinBrightnessSettingsKey, out var v))
                {
                    if (v < 0) v = 0;
                    if (v > 50) v = 50;
                    minBrightness = v;
                }
            }
            catch { }
        }

        public void Dispose() => Stop();
    }
}
