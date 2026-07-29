using System;
using System.Threading;

namespace XboxGamingBarHelper.PresentMon
{
    /// <summary>
    /// Thread-safe rolling-window aggregates of PresentMon CSV output.
    ///
    /// Producer: <see cref="PresentMonRunner"/> background reader, one CSV line per frame.
    /// Consumer: OSD template build at 1 Hz (and AutoTDP tick).
    ///
    /// Strategy:
    /// - Per-frame increments to volatile counters keyed by FrameType.
    /// - 1 Hz tick (timer) drains the counters into the public fps numbers and resets.
    /// - The "is live" flag is set on every Push call and cleared by Reset on the runner
    ///   thread. Consumers check freshness via LastUpdateTicksUtc.
    ///
    /// Fields are atomic-read-safe (long/double swap on x64) and only written on the
    /// background reader thread; consumers see consistent values without locking.
    /// </summary>
    internal sealed class PresentMonMetrics
    {
        private long _appFrameCount;      // FrameType=Application
        private long _afmfFrameCount;     // FrameType=AMD_AFMF
        private long _displayedFrameCount; // application + repeated + generated (anything that hit the wire)

        // Render-cost sums: only Application rows carry non-zero CPU/GPU busy,
        // and only Application gaps are meaningful "frame time" (AFMF inserts
        // its own present halfway through, so its MsBetweenPresents is half of
        // the real render-to-render time). Keep these separate.
        private double _sumCpuBusyMs;
        private double _sumGpuBusyMs;
        private double _sumMsBetweenPresentsApp;
        private long _appSampleCount;
        // All-frame present-interval sum + sample count (Application + Repeated + generated),
        // used to derive the DISPLAYED rate the same burst-immune way as the rendered rate.
        private double _sumMsBetweenPresentsAll;
        private long _displayedSampleCount;

        private long _lastUpdateTicksUtc;

        // FPS is derived from AVERAGE frame time, not a per-window frame COUNT: PresentMon
        // block-buffers its stdout, so frames reach us in multi-second bursts. Counting
        // frames-per-flush-window then swings wildly (near 0 on an empty window, 100+ when a
        // backlog burst drains) even at a steady real frame rate (issue #104, Legion Go 2).
        // Each frame's MsBetweenPresents is its true interval regardless of when we receive
        // it, so averaging that gives a stable rate. A light EWMA smooths across windows, and
        // an empty window while still live HOLDS the last value instead of dropping to 0.
        private double _ewmaFtAppMs;   // EWMA of Application frame time (ms); 0 = uninitialized
        private double _ewmaFtAllMs;   // EWMA of all-frame present interval (ms)
        private const double FtEwmaAlpha = 0.5;
        private const int HoldMs = 3000; // keep the last rate this long into a delivery gap

        // Last computed rates / averages exposed to consumers.
        private volatile int _appFps;
        private volatile int _afmfFps;
        private volatile int _displayedFps;
        private double _cpuBusyAvgMs;
        private double _gpuBusyAvgMs;
        private double _frametimeAvgMs;
        private double _cpuBusyPct;
        private double _gpuBusyPct;

        public int RenderedFps => _appFps;
        public int AfmfFps => _afmfFps;
        public int DisplayedFps => _displayedFps;
        public double CpuBusyAvgMs => Interlocked.CompareExchange(ref _cpuBusyAvgMs, 0, 0);
        public double GpuBusyAvgMs => Interlocked.CompareExchange(ref _gpuBusyAvgMs, 0, 0);
        public double FrametimeAvgMs => Interlocked.CompareExchange(ref _frametimeAvgMs, 0, 0);
        /// <summary>% of frame time the CPU spent on render work (MsCPUBusy / MsBetweenPresents over Application frames).</summary>
        public double CpuBusyPct => Interlocked.CompareExchange(ref _cpuBusyPct, 0, 0);
        /// <summary>% of frame time the GPU spent on render work (MsGPUBusy / MsBetweenPresents over Application frames).</summary>
        public double GpuBusyPct => Interlocked.CompareExchange(ref _gpuBusyPct, 0, 0);
        public long LastUpdateTicksUtc => Interlocked.Read(ref _lastUpdateTicksUtc);

        /// <summary>True when a frame line has been parsed in the last <paramref name="staleMs"/>.</summary>
        public bool IsLive(int staleMs = 2000)
        {
            long last = LastUpdateTicksUtc;
            if (last == 0) return false;
            return (DateTime.UtcNow.Ticks - last) < (staleMs * TimeSpan.TicksPerMillisecond);
        }

        /// <summary>Called by PresentMonRunner for each CSV frame line.</summary>
        public void Push(FrameType frameType, double msBetweenPresents, double cpuBusyMs, double gpuBusyMs)
        {
            Interlocked.Increment(ref _displayedFrameCount);
            switch (frameType)
            {
                case FrameType.Application:
                    Interlocked.Increment(ref _appFrameCount);
                    // Only Application rows carry meaningful CPU/GPU busy times and the real
                    // render-to-render gap. Skip non-positive intervals (a swapchain's first
                    // present reports 0) — they'd drag the average down and inflate FPS.
                    if (msBetweenPresents > 0)
                    {
                        AddDouble(ref _sumCpuBusyMs, cpuBusyMs);
                        AddDouble(ref _sumGpuBusyMs, gpuBusyMs);
                        AddDouble(ref _sumMsBetweenPresentsApp, msBetweenPresents);
                        Interlocked.Increment(ref _appSampleCount);
                    }
                    break;
                case FrameType.AMD_AFMF:
                case FrameType.Intel_XEFG:
                    // Any frame-generated row (AMD AFMF / Intel XeSS-FG / re-tagged Lossless
                    // Scaling) marks FG as active for the window — gates the OSD [FG] badge.
                    Interlocked.Increment(ref _afmfFrameCount);
                    break;
            }
            // Every frame (any type) contributes its present interval to the displayed-rate
            // average — that's the rate at which unique buffers hit the swap chain.
            if (msBetweenPresents > 0)
            {
                AddDouble(ref _sumMsBetweenPresentsAll, msBetweenPresents);
                Interlocked.Increment(ref _displayedSampleCount);
            }
            Interlocked.Exchange(ref _lastUpdateTicksUtc, DateTime.UtcNow.Ticks);
        }

        /// <summary>
        /// Drain the per-second counters into the public fields and reset. Called by the
        /// runner at ~1 Hz from its own timer so the public values are stable for that
        /// window (OSD reads see a steady number for the second).
        /// </summary>
        public void FlushPerSecond()
        {
            // Drain the raw per-window counters. FPS no longer derives from these
            // count-per-window values (see the field comment), but the FG count still
            // gates the [FG] badge: presence of generated frames is a per-window fact,
            // not a rate, so the count is reliable for that even with bursty delivery.
            Interlocked.Exchange(ref _appFrameCount, 0);
            long afmfCount = Interlocked.Exchange(ref _afmfFrameCount, 0);
            Interlocked.Exchange(ref _displayedFrameCount, 0);

            long appSamples = Interlocked.Exchange(ref _appSampleCount, 0);
            long dispSamples = Interlocked.Exchange(ref _displayedSampleCount, 0);
            double sumCpu = Interlocked.Exchange(ref _sumCpuBusyMs, 0);
            double sumGpu = Interlocked.Exchange(ref _sumGpuBusyMs, 0);
            double sumPresentApp = Interlocked.Exchange(ref _sumMsBetweenPresentsApp, 0);
            double sumPresentAll = Interlocked.Exchange(ref _sumMsBetweenPresentsAll, 0);

            bool live = IsLive(HoldMs);

            // Rendered (Application) FPS from the average render-to-render frame time.
            if (appSamples > 0)
            {
                double ftAppAvg = sumPresentApp / appSamples;
                _ewmaFtAppMs = _ewmaFtAppMs <= 0 ? ftAppAvg : FtEwmaAlpha * ftAppAvg + (1 - FtEwmaAlpha) * _ewmaFtAppMs;
                _appFps = _ewmaFtAppMs > 0 ? (int)Math.Round(1000.0 / _ewmaFtAppMs) : 0;

                double cpuAvg = sumCpu / appSamples;
                double gpuAvg = sumGpu / appSamples;
                Interlocked.Exchange(ref _cpuBusyAvgMs, cpuAvg);
                Interlocked.Exchange(ref _gpuBusyAvgMs, gpuAvg);
                Interlocked.Exchange(ref _frametimeAvgMs, _ewmaFtAppMs);
                // Busy % = render-cost / available frame time. Clamp at 100 because
                // PresentMon's CPU/GPU busy can overshoot the present interval when a present
                // is delayed (queue drain).
                double cpuPct = _ewmaFtAppMs > 0 ? Math.Min(100.0, cpuAvg / _ewmaFtAppMs * 100.0) : 0;
                double gpuPct = _ewmaFtAppMs > 0 ? Math.Min(100.0, gpuAvg / _ewmaFtAppMs * 100.0) : 0;
                Interlocked.Exchange(ref _cpuBusyPct, cpuPct);
                Interlocked.Exchange(ref _gpuBusyPct, gpuPct);
            }
            else if (!live)
            {
                // Genuinely no frames for HoldMs (game paused / closed / alt-tabbed) — clear.
                _appFps = 0;
                _ewmaFtAppMs = 0;
                Interlocked.Exchange(ref _cpuBusyAvgMs, 0);
                Interlocked.Exchange(ref _gpuBusyAvgMs, 0);
                Interlocked.Exchange(ref _frametimeAvgMs, 0);
                Interlocked.Exchange(ref _cpuBusyPct, 0);
                Interlocked.Exchange(ref _gpuBusyPct, 0);
            }
            // else: live but this window was empty (a stdout delivery gap) — HOLD last values.

            // Displayed FPS from the average all-frame present interval; AFMF/FG presence is
            // the displayed rate above the rendered rate.
            if (dispSamples > 0)
            {
                double ftAllAvg = sumPresentAll / dispSamples;
                _ewmaFtAllMs = _ewmaFtAllMs <= 0 ? ftAllAvg : FtEwmaAlpha * ftAllAvg + (1 - FtEwmaAlpha) * _ewmaFtAllMs;
                _displayedFps = _ewmaFtAllMs > 0 ? (int)Math.Round(1000.0 / _ewmaFtAllMs) : 0;
                // Only report an FG rate when generated frames were actually seen this
                // window — displayed-vs-rendered rounding jitter (±1) must not light the
                // OSD's [FG] badge on a non-frame-generated game.
                _afmfFps = afmfCount > 0 ? Math.Max(0, _displayedFps - _appFps) : 0;
            }
            else if (!live)
            {
                _displayedFps = 0;
                _afmfFps = 0;
                _ewmaFtAllMs = 0;
            }
            // else: HOLD last displayed/afmf values through the delivery gap.
        }

        /// <summary>Called when the runner stops so consumers see "no data" immediately.</summary>
        public void Reset()
        {
            Interlocked.Exchange(ref _appFrameCount, 0);
            Interlocked.Exchange(ref _afmfFrameCount, 0);
            Interlocked.Exchange(ref _displayedFrameCount, 0);
            Interlocked.Exchange(ref _appSampleCount, 0);
            Interlocked.Exchange(ref _displayedSampleCount, 0);
            Interlocked.Exchange(ref _sumCpuBusyMs, 0);
            Interlocked.Exchange(ref _sumGpuBusyMs, 0);
            Interlocked.Exchange(ref _sumMsBetweenPresentsApp, 0);
            Interlocked.Exchange(ref _sumMsBetweenPresentsAll, 0);
            Interlocked.Exchange(ref _lastUpdateTicksUtc, 0);
            _ewmaFtAppMs = 0;
            _ewmaFtAllMs = 0;
            _appFps = 0;
            _afmfFps = 0;
            _displayedFps = 0;
            Interlocked.Exchange(ref _cpuBusyAvgMs, 0);
            Interlocked.Exchange(ref _gpuBusyAvgMs, 0);
            Interlocked.Exchange(ref _frametimeAvgMs, 0);
            Interlocked.Exchange(ref _cpuBusyPct, 0);
            Interlocked.Exchange(ref _gpuBusyPct, 0);
        }

        private static void AddDouble(ref double target, double delta)
        {
            double initial, computed;
            do
            {
                initial = Interlocked.CompareExchange(ref target, 0, 0);
                computed = initial + delta;
            } while (Interlocked.CompareExchange(ref target, computed, initial) != initial);
        }
    }

    internal enum FrameType : byte
    {
        Unknown = 0,
        Application = 1,
        Repeated = 2,
        Intel_XEFG = 50,
        AMD_AFMF = 100,
    }
}
