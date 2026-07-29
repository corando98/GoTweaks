using System;
using System.Collections.Generic;
using System.Threading;
using XboxGamingBarHelper.Core;

namespace XboxGamingBarHelper.Devices.Libraries.Legion
{
    internal partial class LegionManager
    {
        // Coalesces rapid firmware slider writes (stick deadzone, trigger travel, gyro
        // deadzone). Dragging a slider in the widget fires a property change per tick, and
        // each of those setters opens a FRESH LegionGoController HID connection, connects,
        // writes, and disposes. At slider speed that hammers the controller MCU (which our
        // notes warn can lock under rapid writes) and contends with LegionButtonMonitor's
        // MI_02 vendor handle. Debouncing collapses a drag to a single settled write.
        //
        // Ported in the spirit of HandheldCompanion's "cache the per-side state and write
        // once" fix for Legion joycon settings (commit 7e052ca99) — our write path already
        // avoids their read-modify-write / off-by-one correctness bugs (we write values
        // directly), so only the write-storm mitigation applies to us.
        private readonly object firmwareDebounceLock = new object();
        private readonly Dictionary<string, Timer> firmwareDebounceTimers = new Dictionary<string, Timer>();
        private const int FirmwareWriteDebounceMs = 300;

        /// <summary>
        /// Cancels all pending debounced writes. Called from Dispose so a deferred slider
        /// write can't open a fresh controller handle mid-teardown.
        /// </summary>
        private void DisposeFirmwareDebounceTimers()
        {
            lock (firmwareDebounceLock)
            {
                foreach (var t in firmwareDebounceTimers.Values)
                {
                    try { t.Dispose(); } catch { }
                }
                firmwareDebounceTimers.Clear();
            }
        }

        /// <summary>
        /// Schedules <paramref name="write"/> to run once after a short quiet period, keyed
        /// by <paramref name="key"/>. A newer call for the same key cancels the pending one,
        /// so only the last value in a burst is written to the controller.
        /// </summary>
        private void DebounceFirmwareWrite(string key, Action write)
        {
            lock (firmwareDebounceLock)
            {
                if (firmwareDebounceTimers.TryGetValue(key, out var existing))
                {
                    existing.Dispose();
                }
                firmwareDebounceTimers[key] = new Timer(_ =>
                {
                    lock (firmwareDebounceLock)
                    {
                        if (firmwareDebounceTimers.TryGetValue(key, out var t))
                        {
                            t.Dispose();
                            firmwareDebounceTimers.Remove(key);
                        }
                    }
                    try { write(); }
                    catch (Exception ex) { Logger.Warn($"Debounced firmware write '{key}' threw: {ex.Message}"); }
                }, null, FirmwareWriteDebounceMs, Timeout.Infinite);
            }
        }
    }
}
