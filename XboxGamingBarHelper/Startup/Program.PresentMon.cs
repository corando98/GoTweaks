using System;
using System.IO;
using NLog;
using XboxGamingBarHelper.PresentMon;

namespace XboxGamingBarHelper
{
    /// <summary>
    /// #66 PresentMon CLI subprocess integration. Owns the per-game PresentMon.exe
    /// lifecycle and the VrrProbe diagnostic.
    /// </summary>
    internal partial class Program
    {
        private static PresentMonRunner _presentMonRunner;
        private static VrrProbe _vrrProbe;
        internal static PresentMonMetrics PresentMonMetrics { get; private set; }
        internal static int ConfiguredPanelHz => _vrrProbe?.ConfiguredHz ?? 0;

        private static void InitializePresentMon()
        {
            try
            {
                string exe = ResolvePresentMonExePath();
                if (exe == null)
                {
                    Logger.Warn("PresentMon: bundled PresentMon.exe not found in helper folder; integration disabled");
                    return;
                }

                // Kill any orphan PresentMon.exe from a previous helper crash. Orphans
                // hold open trace sessions and cause ERROR_INSTANCE_NOT_FOUND (4201)
                // on a fresh spawn even with our unique-session-name workaround.
                try
                {
                    foreach (var p in System.Diagnostics.Process.GetProcessesByName("PresentMon"))
                    {
                        try { p.Kill(); Logger.Info($"PresentMon: killed orphan PresentMon.exe pid={p.Id}"); }
                        catch (Exception kex) { Logger.Debug($"PresentMon: could not kill orphan pid={p.Id}: {kex.Message}"); }
                        finally { p.Dispose(); }
                    }
                }
                catch (Exception ex) { Logger.Debug($"PresentMon: orphan cleanup threw {ex.GetType().Name}: {ex.Message}"); }

                // Then stop any orphaned ETW realtime sessions named GoTweaks_PMon_*.
                // taskkill on PresentMon does not release its ETW session, so the
                // kernel keeps the session alive and the providers stay locked.
                // The next PresentMon spawn then hangs silently on EnableTraceEx
                // because Microsoft-Windows-DxgKrnl / Intel-PresentMon can only
                // be in so many realtime sessions at once.
                CleanupOrphanEtwSessions();

                PresentMonMetrics = new PresentMonMetrics();
                _presentMonRunner = new PresentMonRunner(exe, PresentMonMetrics);
                Logger.Info($"PresentMon: PresentMonRunner integration ready (exe={exe})");

                // Win32 ground-truth panel Hz, sampled every 500 ms. Useful as a
                // ground-truth refresh-rate reference compared to PresentMon's
                // event-derived metrics. Not started here - it rides the same
                // per-game lifecycle as _presentMonRunner (see
                // OnRunningGameChangedForPresentMon) instead of running unconditionally
                // for the whole helper lifetime; it's only meaningful while a game
                // (and PresentMon) is actually being tracked.
                _vrrProbe = new VrrProbe();
            }
            catch (Exception ex)
            {
                Logger.Warn($"PresentMon: initialize threw {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Shell logman.exe to enumerate realtime ETW sessions and stop any
        /// whose name starts with "GoTweaks_PMon_". These leak when PresentMon
        /// is force-killed instead of exiting cleanly.
        /// </summary>
        internal static void CleanupOrphanEtwSessions()
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "logman.exe",
                    Arguments = "query -ets",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                string output;
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(5000);
                }
                int stopped = 0;
                foreach (var rawLine in output.Split('\n'))
                {
                    string line = rawLine.Trim();
                    if (!line.StartsWith("GoTweaks_PMon_", StringComparison.OrdinalIgnoreCase)) continue;
                    // Line shape: "GoTweaks_PMon_<pid>_<ticks>          Trace                       Running"
                    int sp = line.IndexOf(' ');
                    string name = sp > 0 ? line.Substring(0, sp) : line;
                    try
                    {
                        var stopPsi = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = "logman.exe",
                            Arguments = $"stop \"{name}\" -ets",
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                        };
                        using (var sp2 = System.Diagnostics.Process.Start(stopPsi))
                        {
                            sp2.WaitForExit(3000);
                            if (sp2.ExitCode == 0)
                            {
                                Logger.Info($"PresentMon: stopped orphan ETW session {name}");
                                stopped++;
                            }
                            else
                            {
                                Logger.Debug($"PresentMon: logman stop {name} exit={sp2.ExitCode}");
                            }
                        }
                    }
                    catch (Exception ex) { Logger.Debug($"PresentMon: stop {name} threw {ex.Message}"); }
                }
                if (stopped > 0) Logger.Info($"PresentMon: cleaned {stopped} orphan ETW session(s)");
            }
            catch (Exception ex)
            {
                Logger.Debug($"PresentMon: ETW session cleanup threw {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// PresentMon.exe sits next to the helper exe. We try the current process
        /// directory; that covers both MSIX install and the deployed scheduled-task
        /// location.
        /// </summary>
        private static string ResolvePresentMonExePath()
        {
            try
            {
                var exeDir = Path.GetDirectoryName(System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName);
                if (string.IsNullOrEmpty(exeDir)) return null;
                string candidate = Path.Combine(exeDir, "PresentMon.exe");
                return File.Exists(candidate) ? candidate : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Called from RunningGame_PropertyChanged. Wrapped in try/catch so the profile
        /// handler critical path is never affected by PresentMon faults.
        /// </summary>
        private static void OnRunningGameChangedForPresentMon()
        {
            if (_presentMonRunner == null) return;
            try
            {
                var rg = systemManager?.RunningGame;
                if (rg == null) return;
                if (rg.Value.IsValid())
                {
                    int pid = rg.Value.ProcessId;
                    string path = rg.Value.GameId.Path;
                    string exeName = !string.IsNullOrEmpty(path) ? Path.GetFileName(path) : null;
                    if (pid > 0)
                    {
                        _presentMonRunner.Start(pid, exeName);
                        _vrrProbe?.Start();
                    }
                }
                else
                {
                    _presentMonRunner.Stop("game stopped");
                    _vrrProbe?.Stop();
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"PresentMon: OnRunningGameChanged threw {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
