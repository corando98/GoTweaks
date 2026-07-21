using System;
using System.Runtime.InteropServices;
using System.Threading;
using NLog;

namespace XboxGamingBarHelper.PresentMon
{
    /// <summary>
    /// #66 / VRR-confidence test. Samples the actual display refresh rate via DWM's
    /// composition timing API on a fast timer. The value reflects what the OS scanout
    /// sees: if VRR is active for the foreground game the rate tracks rendered FPS;
    /// if VRR is disabled (DWM composition fallback) the rate stays pinned at the
    /// panel's max Hz regardless of game frame rate.
    ///
    /// Usage: run while a game is in foreground. Compare two windows:
    ///   A. RTSS off — refresh rate should fluctuate / track rendered FPS
    ///   B. RTSS on  — refresh rate likely pins at panel max if RTSS forces composition
    ///
    /// The deltaPerSec value is the ACTUAL recent refresh rate, computed from the
    /// growth of cRefreshesDisplayed between samples. That field is the count of
    /// physical refreshes the display has performed since DWM started; it grows
    /// at the real scanout rate regardless of what rateRefresh reports.
    /// </summary>
    internal sealed class VrrProbe : IDisposable
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private Timer _timer;
        private long _lastCount;
        private long _lastTickUtc;
        private long _samples;
        private bool _disposed;
        // Latest configured panel Hz from EnumDisplaySettings(ENUM_CURRENT_SETTINGS).
        // Reflects whatever Windows sees as the panel's set refresh rate; on a VRR
        // panel this often holds at the max (e.g. 144) regardless of dynamic rate.
        // Useful as ground-truth to compare against our event-derived rates.
        private volatile int _configuredHz;
        public int ConfiguredHz => _configuredHz;

        public void Start()
        {
            if (_timer != null) return; // already running - avoid leaking a second Timer
            try
            {
                _timer = new Timer(_ => OnTick(), null, dueTime: 1000, period: 500);
                Logger.Info("VrrProbe: started (sampling DwmGetCompositionTimingInfo every 500ms)");
            }
            catch (Exception ex)
            {
                Logger.Warn($"VrrProbe: start threw {ex.GetType().Name}: {ex.Message}");
            }
        }

        public void Stop()
        {
            try { _timer?.Dispose(); } catch { }
            _timer = null;
            _lastCount = 0;
            _lastTickUtc = 0;
        }

        private void OnTick()
        {
            // Always sample the Win32 configured refresh — it works in our session
            // even though DwmGetCompositionTimingInfo doesn't.
            try
            {
                var dm = new DEVMODE { dmSize = (ushort)Marshal.SizeOf(typeof(DEVMODE)) };
                if (EnumDisplaySettingsEx(null, ENUM_CURRENT_SETTINGS, ref dm, 0))
                {
                    _configuredHz = (int)dm.dmDisplayFrequency;
                }
            }
            catch { /* keep last value */ }

            try
            {
                var ti = new DWM_TIMING_INFO { cbSize = (uint)Marshal.SizeOf(typeof(DWM_TIMING_INFO)) };
                int hr = DwmGetCompositionTimingInfo(IntPtr.Zero, out ti);
                if (hr != 0)
                {
                    // DWM call doesn't work in our scheduled-task session. Log once and
                    // give up on it; we still get the Win32 configured Hz above which is
                    // what the user wants to compare against the event-derived panelHz.
                    if (Interlocked.Read(ref _samples) == 0)
                    {
                        Logger.Warn($"VrrProbe: DwmGetCompositionTimingInfo returned hr=0x{hr:X8}, will rely on EnumDisplaySettings only");
                        Interlocked.Increment(ref _samples);
                    }
                    return;
                }

                long now = DateTime.UtcNow.Ticks;
                long count = unchecked((long)ti.cRefreshesDisplayed);

                long last = Interlocked.Read(ref _lastCount);
                long lastT = Interlocked.Read(ref _lastTickUtc);

                if (last == 0 || lastT == 0)
                {
                    Interlocked.Exchange(ref _lastCount, count);
                    Interlocked.Exchange(ref _lastTickUtc, now);
                    return;
                }

                long deltaRefreshes = count - last;
                double deltaMs = (now - lastT) / (double)TimeSpan.TicksPerMillisecond;
                double observedHz = deltaMs > 0 ? (deltaRefreshes * 1000.0 / deltaMs) : 0;

                Interlocked.Exchange(ref _lastCount, count);
                Interlocked.Exchange(ref _lastTickUtc, now);

                double reportedHz = ti.rateRefresh.uiDenominator == 0 ? 0
                    : (double)ti.rateRefresh.uiNumerator / ti.rateRefresh.uiDenominator;
                double composeHz = ti.rateCompose.uiDenominator == 0 ? 0
                    : (double)ti.rateCompose.uiNumerator / ti.rateCompose.uiDenominator;

                long s = Interlocked.Increment(ref _samples);
                // Log every 4th sample (~2 s cadence) so the log stays readable.
                if (s % 4 == 0)
                {
                    Logger.Info($"VrrProbe: observedHz={observedHz:F1} (refreshes={deltaRefreshes} in {deltaMs:F0}ms), reportedRefresh={reportedHz:F1}, compose={composeHz:F1}");
                }
            }
            catch (Exception ex)
            {
                if (Interlocked.Read(ref _samples) == 0)
                    Logger.Warn($"VrrProbe: tick threw {ex.GetType().Name}: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct UNSIGNED_RATIO
        {
            public uint uiNumerator;
            public uint uiDenominator;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DWM_TIMING_INFO
        {
            public uint cbSize;
            public UNSIGNED_RATIO rateRefresh;
            public ulong qpcRefreshPeriod;
            public UNSIGNED_RATIO rateCompose;
            public ulong qpcVBlank;
            public ulong cRefresh;
            public uint cDXRefresh;
            public ulong qpcCompose;
            public ulong cFrame;
            public uint cDXPresent;
            public ulong cRefreshFrame;
            public ulong cFrameSubmitted;
            public uint cDXPresentSubmitted;
            public ulong cFrameConfirmed;
            public uint cDXPresentConfirmed;
            public ulong cRefreshConfirmed;
            public uint cDXRefreshConfirmed;
            public ulong cFramesLate;
            public uint cFramesOutstanding;
            public ulong cFrameDisplayed;
            public ulong qpcFrameDisplayed;
            public ulong cRefreshFrameDisplayed;
            public ulong cFrameComplete;
            public ulong qpcFrameComplete;
            public ulong cFramePending;
            public ulong qpcFramePending;
            public ulong cRefreshesDisplayed;
            public ulong cRefreshesPresented;
            public ulong cRefreshStarted;
            public ulong cPixelsReceived;
            public ulong cPixelsDrawn;
            public ulong cBuffersEmpty;
        }

        [DllImport("Dwmapi.dll")]
        private static extern int DwmGetCompositionTimingInfo(IntPtr hwnd, out DWM_TIMING_INFO pTimingInfo);

        private const int ENUM_CURRENT_SETTINGS = -1;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public ushort dmSpecVersion;
            public ushort dmDriverVersion;
            public ushort dmSize;
            public ushort dmDriverExtra;
            public uint dmFields;
            public int dmPositionX;
            public int dmPositionY;
            public uint dmDisplayOrientation;
            public uint dmDisplayFixedOutput;
            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public ushort dmLogPixels;
            public uint dmBitsPerPel;
            public uint dmPelsWidth;
            public uint dmPelsHeight;
            public uint dmDisplayFlags;
            public uint dmDisplayFrequency;
            public uint dmICMMethod;
            public uint dmICMIntent;
            public uint dmMediaType;
            public uint dmDitherType;
            public uint dmReserved1;
            public uint dmReserved2;
            public uint dmPanningWidth;
            public uint dmPanningHeight;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool EnumDisplaySettingsEx(string lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode, uint dwFlags);
    }
}
