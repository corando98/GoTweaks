using System.Drawing;

namespace XboxGamingBarHelper.RTSS.OSDItems
{
    /// <summary>
    /// #66 — PresentMon-derived "bound indicator": how much of each rendered
    /// frame's wall-clock budget the CPU and GPU were each busy. The higher
    /// number tells you which side is the bottleneck.
    ///
    ///   CPU 38% | GPU 82%    → GPU-bound
    ///   CPU 95% | GPU 41%    → CPU-bound (game's render thread is the limit)
    ///
    /// Source: PresentMon MsCPUBusy / MsGPUBusy / MsBetweenPresents on
    /// Application frames only (AFMF rows carry zeros — see
    /// PresentMonMetrics).
    ///
    /// Hidden when PresentMon isn't producing data (no game running, no
    /// PresentMon CLI, or the game is using a presentation path PresentMon
    /// can't see) — we render an empty string and RTSS skips the row.
    /// </summary>
    internal class OSDItemFrameBudget : OSDItem
    {
        public OSDItemFrameBudget() : base("BND", "FrameBudget", Color.Orange)
        {
        }

        public override string GetOSDString(int osdLevel)
        {
            var pm = Program.PresentMonMetrics;
            if (pm == null || !pm.IsLive(3000)) return string.Empty;

            int cpu = (int)System.Math.Round(pm.CpuBusyPct);
            int gpu = (int)System.Math.Round(pm.GpuBusyPct);
            if (cpu <= 0 && gpu <= 0) return string.Empty;

            var tc = GetTextColorWithOpacity();
            var yellow = ApplyOpacity("FFFF00");

            // Whichever side is higher is the bottleneck — highlight in yellow.
            string cpuColor = cpu >= gpu ? yellow : tc;
            string gpuColor = gpu >  cpu ? yellow : tc;

            return $"{GetNameString()} <C={cpuColor}>CPU {cpu}%<C={tc}> | <C={gpuColor}>GPU {gpu}%<C={tc}>";
        }
    }
}
