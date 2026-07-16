using NLog;
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using XboxGamingBarHelper.Power;
using XboxGamingBarHelper.Windows;

namespace XboxGamingBarHelper
{
    /// <summary>
    /// GoTweaks-owned idle-to-hibernate timer. Windows only exposes a "sleep after X"
    /// idle timeout via the standard power-plan API (Program.PowerButtonAction/Display
    /// Timeout both read/write real Windows settings) - there's no equivalent reliable
    /// "hibernate after X" setting to piggyback on, so this polls idle time the same
    /// way the existing Screen Saver feature does (Program.ScreenSaver.cs) and calls
    /// PowrProf.SetSuspendState directly once the configured AC/DC threshold is
    /// exceeded. 0 minutes = disabled for that power state.
    ///
    /// The monitor only runs while at least one of AC/DC is non-zero (see
    /// UpdateHibernateTimeoutMonitorState, wired to both properties' PropertyChanged in
    /// Program.cs) - mirrors ControllerHotkeyMonitor's "don't poll for nothing" pattern
    /// (AnyComboEnabled) rather than running an always-on no-op timer.
    ///
    /// Idle detection combines two sources, since GetLastInputInfo only sees
    /// keyboard/mouse at the OS level and would consider a controller-only player
    /// "idle" for the whole session: (1) GetLastInputInfo (KBM), (2) XInput
    /// packet-number changes across all 4 slots (covers the emulated VIIPER pad, which
    /// may not land on slot 0 - same reasoning ControllerHotkeyMonitor uses). Whichever
    /// source saw activity more recently wins.
    /// </summary>
    internal partial class Program
    {
        private static System.Threading.Timer hibernateTimeoutTimer;
        private const int HibernateTimeoutCheckIntervalMs = 5000; // Same cadence as Screen Saver
        private static volatile bool hibernateTimeoutTriggered = false;
        private static uint _resumeBaselineTickCount = 0;

        #region XInput (gamepad activity)

        [StructLayout(LayoutKind.Sequential)]
        private struct XINPUT_GAMEPAD_HT
        {
            public ushort wButtons;
            public byte bLeftTrigger;
            public byte bRightTrigger;
            public short sThumbLX;
            public short sThumbLY;
            public short sThumbRX;
            public short sThumbRY;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct XINPUT_STATE_HT
        {
            public uint dwPacketNumber;
            public XINPUT_GAMEPAD_HT Gamepad;
        }

        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
        private static extern uint XInputGetState14(uint dwUserIndex, ref XINPUT_STATE_HT pState);

        [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")]
        private static extern uint XInputGetState910(uint dwUserIndex, ref XINPUT_STATE_HT pState);

        private delegate uint XInputGetStateDelegate(uint dwUserIndex, ref XINPUT_STATE_HT pState);
        private static XInputGetStateDelegate hibernateXInputGetState;
        private static bool hibernateXInputResolved = false;
        private static readonly uint[] hibernateLastXInputPacket = new uint[4];
        private static bool hibernateXInputSeeded = false;
        private static uint hibernateLastGamepadActivityTick;

        private static void ResolveHibernateXInput()
        {
            if (hibernateXInputResolved) return;
            hibernateXInputResolved = true;

            try
            {
                var state = new XINPUT_STATE_HT();
                XInputGetState14(0, ref state);
                hibernateXInputGetState = XInputGetState14;
                Logger.Debug("Hibernate Timeout: Using xinput1_4.dll for gamepad-activity detection");
            }
            catch
            {
                try
                {
                    var state = new XINPUT_STATE_HT();
                    XInputGetState910(0, ref state);
                    hibernateXInputGetState = XInputGetState910;
                    Logger.Debug("Hibernate Timeout: Using xinput9_1_0.dll for gamepad-activity detection");
                }
                catch (Exception ex)
                {
                    Logger.Warn($"Hibernate Timeout: XInput unavailable - gamepad activity won't be tracked, falling back to keyboard/mouse only: {ex.Message}");
                    hibernateXInputGetState = null;
                }
            }
        }

        /// <summary>
        /// Time since the last detected XInput packet-number change, across all 4
        /// slots. Returns a huge value (never blocks hibernation via this signal) when
        /// XInput isn't available at all.
        /// </summary>
        private static uint GetGamepadIdleMs(uint nowTick)
        {
            if (hibernateXInputGetState == null) return uint.MaxValue;

            bool anyChanged = false;
            for (uint i = 0; i < 4; i++)
            {
                var state = new XINPUT_STATE_HT();
                uint result = hibernateXInputGetState(i, ref state);
                if (result == 0 && state.dwPacketNumber != hibernateLastXInputPacket[i])
                {
                    hibernateLastXInputPacket[i] = state.dwPacketNumber;
                    anyChanged = true;
                }
            }

            if (anyChanged || !hibernateXInputSeeded)
            {
                hibernateXInputSeeded = true;
                hibernateLastGamepadActivityTick = nowTick;
            }

            return nowTick - hibernateLastGamepadActivityTick;
        }

        #endregion

        /// <summary>
        /// Starts or stops the idle monitor based on whether either AC or DC threshold
        /// is currently non-zero. Wired to both HibernateTimeout properties'
        /// PropertyChanged in Program.cs, and also called once at startup.
        /// </summary>
        private static void UpdateHibernateTimeoutMonitorState(object sender = null, PropertyChangedEventArgs e = null)
        {
            bool shouldRun = (powerManager?.HibernateTimeoutAC?.Value ?? 0) > 0
                           || (powerManager?.HibernateTimeoutDC?.Value ?? 0) > 0;

            if (shouldRun)
            {
                StartHibernateTimeoutMonitor();
            }
            else
            {
                StopHibernateTimeoutMonitor();
            }
        }

        private static void StartHibernateTimeoutMonitor()
        {
            if (hibernateTimeoutTimer == null)
            {
                ResolveHibernateXInput();
                _resumeBaselineTickCount = (uint)Environment.TickCount;
                hibernateTimeoutTimer = new System.Threading.Timer(HibernateTimeoutIdleCheck, null, HibernateTimeoutCheckIntervalMs, HibernateTimeoutCheckIntervalMs);
                Logger.Info("Hibernate Timeout: Started idle monitoring timer");
            }
        }

        private static void StopHibernateTimeoutMonitor()
        {
            if (hibernateTimeoutTimer != null)
            {
                hibernateTimeoutTimer.Dispose();
                hibernateTimeoutTimer = null;
                hibernateTimeoutTriggered = false;
                Logger.Info("Hibernate Timeout: Stopped idle monitoring timer (both AC and DC set to Never)");
            }
        }

        /// <summary>
        /// Re-arms the monitor after a real sleep/hibernate/resume cycle so a stale
        /// pre-sleep idle timestamp doesn't make the very next tick look like it's been
        /// idle for the whole suspended duration and immediately re-trigger.
        /// </summary>
        internal static void ResetHibernateTimeoutAfterResume()
        {
            hibernateTimeoutTriggered = false;
            _resumeBaselineTickCount = (uint)Environment.TickCount;
        }

        private static void HibernateTimeoutIdleCheck(object state)
        {
            try
            {
                if (powerManager?.HibernateTimeoutAC == null || powerManager?.HibernateTimeoutDC == null) return;

                // BatteryStatus, not PowerSupplyStatus: when the battery hits a charge limit
                // (Legion Go 2 80% cap) the EC pauses charge current and Windows downgrades
                // PowerSupplyStatus to NotPresent/Inadequate even though the cable is
                // physically connected. BatteryStatus.Discharging is the authoritative
                // "actually on battery" signal (same fix as the old AutoHibernate, issue #88).
                bool isOnAC = global::Windows.System.Power.PowerManager.BatteryStatus != global::Windows.System.Power.BatteryStatus.Discharging;
                int minutes = isOnAC ? powerManager.HibernateTimeoutAC.Value : powerManager.HibernateTimeoutDC.Value;
                if (minutes <= 0)
                {
                    hibernateTimeoutTriggered = false;
                    return;
                }

                var lastInput = new LASTINPUTINFO();
                lastInput.cbSize = (uint)Marshal.SizeOf(lastInput);
                if (!GetLastInputInfo(ref lastInput)) return;

                uint nowTick = (uint)Environment.TickCount;
                uint kbmIdleMs = nowTick - lastInput.dwTime;
                uint gamepadIdleMs = GetGamepadIdleMs(nowTick);
                uint idleMs = Math.Min(kbmIdleMs, gamepadIdleMs);

                // Clamp to time-since-last-resume so a stale pre-sleep timestamp can't
                // make the first post-resume tick look like it's been idle for the whole
                // suspended duration.
                uint sinceResumeMs = nowTick - _resumeBaselineTickCount;
                idleMs = Math.Min(idleMs, sinceResumeMs);

                uint timeoutMs = (uint)minutes * 60000;
                if (idleMs >= timeoutMs)
                {
                    // Never hibernate out from under a foreground game (kept from the old
                    // AutoHibernate) - a paused-but-foreground game still counts.
                    if (systemManager?.RunningGame?.Value.IsValid() == true && systemManager.RunningGame.Value.IsForeground)
                    {
                        Logger.Info("Hibernate Timeout: Skipping - game is in foreground");
                        return;
                    }

                    if (!hibernateTimeoutTriggered)
                    {
                        hibernateTimeoutTriggered = true;
                        Logger.Info($"Hibernate Timeout: Idle for {idleMs}ms (limit {timeoutMs}ms, {(isOnAC ? "AC" : "DC")}), hibernating");
                        PowrProf.SetSuspendState(true, false, false);
                    }
                }
                else if (hibernateTimeoutTriggered)
                {
                    // User provided input (or system just resumed) — re-arm for next idle period
                    hibernateTimeoutTriggered = false;
                    Logger.Info("Hibernate Timeout: Input detected, re-armed");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Hibernate Timeout: Idle check failed: {ex.Message}");
            }
        }

        /// <summary>
        /// One-time migration from the retired AutoHibernate feature (single idle-minutes
        /// value + Always/AC-Only/DC-Only mode) to the per-AC/DC Hibernate Timeout. Runs at
        /// every startup but only acts when the old settings exist and the new ones haven't
        /// been touched yet, then clears the old keys.
        /// </summary>
        private static void MigrateAutoHibernateSettings()
        {
            try
            {
                if (!Settings.LocalSettingsHelper.TryGetValue("AutoHibernateEnabled", out bool oldEnabled))
                {
                    return;
                }

                if (oldEnabled
                    && (powerManager?.HibernateTimeoutAC?.Value ?? 0) == 0
                    && (powerManager?.HibernateTimeoutDC?.Value ?? 0) == 0)
                {
                    int minutes = 15;
                    if (Settings.LocalSettingsHelper.TryGetValue("AutoHibernateIdleMinutes", out int savedMinutes) && savedMinutes > 0)
                    {
                        minutes = savedMinutes;
                    }
                    int mode = 0;
                    Settings.LocalSettingsHelper.TryGetValue("AutoHibernateMode", out mode);

                    if (mode == 0 || mode == 1) powerManager.HibernateTimeoutAC.SetValue(minutes);
                    if (mode == 0 || mode == 2) powerManager.HibernateTimeoutDC.SetValue(minutes);
                    Logger.Info($"Migrated AutoHibernate settings to Hibernate Timeout: {minutes}min, mode={mode}");
                }

                Settings.LocalSettingsHelper.Remove("AutoHibernateEnabled");
                Settings.LocalSettingsHelper.Remove("AutoHibernateIdleMinutes");
                Settings.LocalSettingsHelper.Remove("AutoHibernateMode");
            }
            catch (Exception ex)
            {
                Logger.Warn($"AutoHibernate migration failed: {ex.Message}");
            }
        }
    }
}
