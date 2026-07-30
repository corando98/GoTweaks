using System;
using System.Runtime.InteropServices;
using Windows.UI.Core;

namespace XboxGamingBar
{
    public sealed partial class GamingWidget
    {
        // --- Dispatcher-death detection (post-resume blank widget) ---
        //
        // After a long Modern Standby, Game Bar can keep this widget instance alive
        // while tearing down its CoreWindow/CoreDispatcher. From then on every
        // dispatch throws InvalidComObjectException (RCW disconnected) or a
        // COMException with RPC_E_DISCONNECTED, background loops keep running
        // against a dead UI, and the widget renders as an empty panel. Nothing in
        // our code can revive it — Game Bar must recreate the widget — but
        // detecting the state (a) keeps async void handlers from crashing the
        // whole process when they dispatch, and (b) replaces the misleading
        // downstream errors (e.g. metrics-JSON parse noise) with one unambiguous
        // [WidgetDead] log line for triage.

        private const int RPC_E_DISCONNECTED = unchecked((int)0x80010108);
        private int dispatcherFailureStreak;
        private DateTime lastWidgetDeadLogUtc = DateTime.MinValue;

        /// <summary>
        /// True once dispatching has failed consecutively with COM-disconnect
        /// errors — the widget's UI thread is gone and stays gone.
        /// </summary>
        private bool IsDispatcherSeparated => dispatcherFailureStreak >= 3;

        /// <summary>
        /// Queues an action on the UI dispatcher, absorbing the COM-disconnect
        /// failures a dead dispatcher throws. Returns false when the dispatch could
        /// not be queued. <paramref name="site"/> names the caller in the
        /// [WidgetDead] log line.
        /// </summary>
        private bool TryRunOnDispatcher(DispatchedHandler action, string site)
        {
            try
            {
                if (Dispatcher == null)
                {
                    NoteDispatcherFailure(site, "Dispatcher is null");
                    return false;
                }
                _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, action);
                dispatcherFailureStreak = 0;
                return true;
            }
            catch (InvalidComObjectException ex)
            {
                NoteDispatcherFailure(site, ex.Message);
                return false;
            }
            catch (COMException ex) when (ex.HResult == RPC_E_DISCONNECTED)
            {
                NoteDispatcherFailure(site, ex.Message);
                return false;
            }
            catch (Exception ex)
            {
                // Unknown failure: log loudly but never let it escape into an
                // async void caller (that would fail-fast the process).
                Logger.Error($"TryRunOnDispatcher({site}) unexpected failure: {ex.Message}");
                return false;
            }
        }

        private void NoteDispatcherFailure(string site, string detail)
        {
            dispatcherFailureStreak++;
            var now = DateTime.UtcNow;
            if ((now - lastWidgetDeadLogUtc).TotalSeconds >= 5)
            {
                lastWidgetDeadLogUtc = now;
                Logger.Error($"[WidgetDead] dispatcher unreachable at {site} (streak={dispatcherFailureStreak}): {detail}. Game Bar likely tore down this widget's CoreWindow after standby; the widget stays blank until Game Bar recreates it.");
            }
        }
    }
}
