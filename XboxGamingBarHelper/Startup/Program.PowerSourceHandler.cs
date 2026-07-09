using NLog;
using Shared.Enums;
using System;
using System.Collections.Generic;
using XboxGamingBarHelper.Power;

namespace XboxGamingBarHelper
{
    /// <summary>
    /// Helper-side AC/DC transition handling. The widget is UWP and stops receiving
    /// PowerManager.PowerSourceChanged callbacks while Game Bar is dismissed, so any
    /// AC↔DC transition that happens between Game Bar sessions used to be dropped
    /// (issue #72). The helper mirrors the three widget settings that drive its power-plan
    /// auto-switch decision, subscribes to SystemManager.PowerSourceChanged, and does the
    /// same work the widget would have done — independent of widget lifecycle.
    /// </summary>
    internal partial class Program
    {
        private static class PowerSourceProfileState
        {
            public static bool AutoSwitchEnabled = false;
            public static Guid AcGuid = Guid.Empty;
            public static Guid DcGuid = Guid.Empty;

            // Per-state TDP / TDPBoost values. Populated via PowerSourceProfileValues pipe
            // message from the widget. null = no override for that field (helper falls back
            // to whatever the active profile / current property already holds).
            public static int? AcTdp = null;
            public static int? DcTdp = null;
            public static bool? AcTdpBoost = null;
            public static bool? DcTdpBoost = null;

            // Extended per-state values (build 2080+). Same null-means-no-override
            // convention. Covers the full set the widget previously sent piecemeal via
            // per-property pipes — now the helper can reapply all of them on AC/DC
            // transitions independent of widget lifecycle (FSE-only-helper scenarios).
            public static bool? AcCpuBoost = null;
            public static bool? DcCpuBoost = null;
            public static int? AcCpuEpp = null;
            public static int? DcCpuEpp = null;
            public static int? AcMaxCpuState = null;
            public static int? DcMaxCpuState = null;
            public static int? AcMinCpuState = null;
            public static int? DcMinCpuState = null;
            public static int? AcOsPowerMode = null;
            public static int? DcOsPowerMode = null;
            // FPSLimit: 0 means "disabled", non-zero is the cap. Widget's profile splits
            // these into FPSLimitEnabled+FPSLimitValue; we collapse on the wire.
            public static int? AcFpsLimit = null;
            public static int? DcFpsLimit = null;

            // AutoTDP per-state (#94 item 3).
            public static bool? AcAutoTdp = null;
            public static bool? DcAutoTdp = null;
            public static int? AcAutoTdpFps = null;
            public static int? DcAutoTdpFps = null;
        }

        // Tracks last observed isOnAC so we can skip the (relatively expensive) plan-switch
        // and TDP-reapply work when SystemManager fires PowerSourceChanged for a status
        // transition that doesn't actually cross the AC/DC boundary. Real-world example
        // (Diego's box, build 2068): a flaky or underspec USB-C charger produces a torrent
        // of Inadequate ↔ Adequate transitions — both map to isOnAC=true, so re-pushing
        // TDP on each is wasted work and could fight the hardware. null on first call so
        // the first real transition always fires (initial seeding is done in SystemManager).
        private static bool? _lastIsOnAC;

        internal static void ApplyPowerSourceProfileConfig(string configJson)
        {
            try
            {
                var cfg = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(configJson);
                if (cfg == null) return;

                if (cfg.TryGetValue("AutoSwitchEnabled", out var autoEl) &&
                    (autoEl.ValueKind == System.Text.Json.JsonValueKind.True || autoEl.ValueKind == System.Text.Json.JsonValueKind.False))
                {
                    PowerSourceProfileState.AutoSwitchEnabled = autoEl.GetBoolean();
                }
                if (cfg.TryGetValue("AcGuid", out var acEl) && acEl.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    Guid.TryParse(acEl.GetString(), out var ac);
                    PowerSourceProfileState.AcGuid = ac;
                }
                if (cfg.TryGetValue("DcGuid", out var dcEl) && dcEl.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    Guid.TryParse(dcEl.GetString(), out var dc);
                    PowerSourceProfileState.DcGuid = dc;
                }

                Logger.Info($"Applied PowerSourceProfileConfig (autoSwitch={PowerSourceProfileState.AutoSwitchEnabled}, ac={PowerSourceProfileState.AcGuid}, dc={PowerSourceProfileState.DcGuid})");
            }
            catch (Exception ex)
            {
                Logger.Error($"ApplyPowerSourceProfileConfig: {ex.Message}");
            }
        }

        /// <summary>
        /// Cache the per-state TDP / TDPBoost values for the active profile so the helper
        /// can apply them on AC/DC transitions. Widget should call this whenever the
        /// active profile or its AC/DC sub-profile changes. JSON value of `null` for any
        /// field means "no override" — helper leaves that property alone on transition.
        /// </summary>
        internal static void ApplyPowerSourceProfileValues(string configJson)
        {
            try
            {
                var cfg = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(configJson);
                if (cfg == null) return;

                int? ParseInt(string key)
                {
                    if (!cfg.TryGetValue(key, out var el)) return null;
                    if (el.ValueKind == System.Text.Json.JsonValueKind.Null) return null;
                    if (el.ValueKind == System.Text.Json.JsonValueKind.Number && el.TryGetInt32(out int v)) return v;
                    return null;
                }
                bool? ParseBool(string key)
                {
                    if (!cfg.TryGetValue(key, out var el)) return null;
                    if (el.ValueKind == System.Text.Json.JsonValueKind.True) return true;
                    if (el.ValueKind == System.Text.Json.JsonValueKind.False) return false;
                    return null;
                }

                PowerSourceProfileState.AcTdp = ParseInt("AcTdp");
                PowerSourceProfileState.DcTdp = ParseInt("DcTdp");
                PowerSourceProfileState.AcTdpBoost = ParseBool("AcTdpBoost");
                PowerSourceProfileState.DcTdpBoost = ParseBool("DcTdpBoost");

                PowerSourceProfileState.AcCpuBoost = ParseBool("AcCpuBoost");
                PowerSourceProfileState.DcCpuBoost = ParseBool("DcCpuBoost");
                PowerSourceProfileState.AcCpuEpp = ParseInt("AcCpuEpp");
                PowerSourceProfileState.DcCpuEpp = ParseInt("DcCpuEpp");
                PowerSourceProfileState.AcMaxCpuState = ParseInt("AcMaxCpuState");
                PowerSourceProfileState.DcMaxCpuState = ParseInt("DcMaxCpuState");
                PowerSourceProfileState.AcMinCpuState = ParseInt("AcMinCpuState");
                PowerSourceProfileState.DcMinCpuState = ParseInt("DcMinCpuState");
                PowerSourceProfileState.AcOsPowerMode = ParseInt("AcOsPowerMode");
                PowerSourceProfileState.DcOsPowerMode = ParseInt("DcOsPowerMode");
                PowerSourceProfileState.AcFpsLimit = ParseInt("AcFpsLimit");
                PowerSourceProfileState.DcFpsLimit = ParseInt("DcFpsLimit");
                PowerSourceProfileState.AcAutoTdp = ParseBool("AcAutoTdp");
                PowerSourceProfileState.DcAutoTdp = ParseBool("DcAutoTdp");
                PowerSourceProfileState.AcAutoTdpFps = ParseInt("AcAutoTdpFps");
                PowerSourceProfileState.DcAutoTdpFps = ParseInt("DcAutoTdpFps");

                Logger.Info($"Applied PowerSourceProfileValues "
                    + $"(AC: tdp={PowerSourceProfileState.AcTdp?.ToString() ?? "-"}W, "
                    + $"boost={PowerSourceProfileState.AcTdpBoost?.ToString() ?? "-"}, "
                    + $"cpuBoost={PowerSourceProfileState.AcCpuBoost?.ToString() ?? "-"}, "
                    + $"epp={PowerSourceProfileState.AcCpuEpp?.ToString() ?? "-"}, "
                    + $"cpuState={PowerSourceProfileState.AcMinCpuState?.ToString() ?? "-"}–{PowerSourceProfileState.AcMaxCpuState?.ToString() ?? "-"}, "
                    + $"osMode={PowerSourceProfileState.AcOsPowerMode?.ToString() ?? "-"}, "
                    + $"fpsLimit={PowerSourceProfileState.AcFpsLimit?.ToString() ?? "-"}; "
                    + $"DC: tdp={PowerSourceProfileState.DcTdp?.ToString() ?? "-"}W, "
                    + $"boost={PowerSourceProfileState.DcTdpBoost?.ToString() ?? "-"}, "
                    + $"cpuBoost={PowerSourceProfileState.DcCpuBoost?.ToString() ?? "-"}, "
                    + $"epp={PowerSourceProfileState.DcCpuEpp?.ToString() ?? "-"}, "
                    + $"cpuState={PowerSourceProfileState.DcMinCpuState?.ToString() ?? "-"}–{PowerSourceProfileState.DcMaxCpuState?.ToString() ?? "-"}, "
                    + $"osMode={PowerSourceProfileState.DcOsPowerMode?.ToString() ?? "-"}, "
                    + $"fpsLimit={PowerSourceProfileState.DcFpsLimit?.ToString() ?? "-"})");
            }
            catch (Exception ex)
            {
                Logger.Error($"ApplyPowerSourceProfileValues: {ex.Message}");
            }
        }

        private static void SystemManager_PowerSourceChanged(object sender, global::Windows.System.Power.PowerSupplyStatus newStatus)
        {
            // Match the widget's interpretation (GamingWidget.PowerSourceEvents.cs:70 and
            // GamingWidget.xaml.cs:2742, :2777): "Inadequate" means a charger is connected
            // but can't keep up with current load — common with the Legion Go's stock
            // USB-C charger under heavy draw. Treat that as AC, not battery, so we match
            // what the user expects when they have something physically plugged in.
            // Only NotPresent (truly unplugged) means DC.
            bool isOnAC = newStatus != global::Windows.System.Power.PowerSupplyStatus.NotPresent;

            // Short-circuit when isOnAC didn't actually change. SystemManager fires
            // PowerSourceChanged on every PowerSupplyStatus transition (Adequate ↔
            // Inadequate ↔ NotPresent), but our work below — power plan switch and
            // TDP reapply — only depends on the AC/DC boolean. On a flaky charger
            // (Inadequate ↔ Adequate flapping) we'd otherwise re-push TDP dozens of
            // times in a few minutes for no behavioral change.
            if (_lastIsOnAC == isOnAC)
            {
                Logger.Debug($"Helper-side AC/DC handler: status {newStatus} but isOnAC unchanged ({isOnAC}); skipping plan/TDP work");
                return;
            }
            _lastIsOnAC = isOnAC;

            // 1) Apply the user's selected AC/DC power plan, if auto-switch is on.
            if (PowerSourceProfileState.AutoSwitchEnabled)
            {
                Guid planToApply = isOnAC ? PowerSourceProfileState.AcGuid : PowerSourceProfileState.DcGuid;
                if (planToApply != Guid.Empty)
                {
                    try
                    {
                        bool ok = PowerManager.SetActivePowerPlan(planToApply);
                        Logger.Info($"Helper-side AC/DC handler: applied {(isOnAC ? "AC" : "DC")} power plan {planToApply} (success={ok})");
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"Helper-side AC/DC handler: SetActivePowerPlan threw: {ex.Message}");
                    }
                }
                else
                {
                    Logger.Debug($"Helper-side AC/DC handler: no {(isOnAC ? "AC" : "DC")} plan configured, skipping plan switch");
                }
            }

            // 2) Apply per-state values cached by the widget on AC/DC transitions.
            //
            // Layered gating, in order of how much they should restrict the work:
            //   - performanceManager null / AutoTDP active → skip everything (no manager
            //     to call, or AutoTDP overwrites TDP every tick anyway).
            //   - DGP active → skip everything; DGP curates all per-game settings.
            //   - TDP-specific gate (Legion Custom OR Power-Source-Profile auto-switch):
            //     only blocks TDP/TDPBoost reapply. In Legion preset modes the system
            //     manages TDP itself, so pushing our value would fight the preset.
            //   - Extended fields (CPUBoost / CPUEPP / CPUState / OSPowerMode / FPSLimit)
            //     are NOT gated by Legion mode — those settings work regardless of preset
            //     mode, and the per-property equality check below ensures we no-op when
            //     the user's AC and DC profiles are identical for a given field.
            //
            // Net effect: helper-side AC/DC apply does useful work whenever the user has
            // configured ANY AC/DC differences for the active profile, whether or not
            // they've also enabled the Power-Source-Profile power-plan auto-switch.
            try
            {
                if (performanceManager == null)
                {
                    return;
                }

                bool dgpActive = defaultGameProfileManager != null && defaultGameProfileManager.ProfileEnabled.Value;
                if (dgpActive)
                {
                    Logger.Debug("Helper-side AC/DC handler: skipping AC/DC apply — DGP is active");
                    return;
                }

                // 2-pre) Per-state AutoTDP (#94 item 3). MUST run before the
                // IsAutoTDPActive gate below: when AutoTDP is currently running and
                // the new power state wants it off, the old gate returned without
                // ever disabling it — an AC-on/DC-off split could never switch off
                // helper-side, and the widget-side switch only runs while the widget
                // is awake (Game Bar suspends it whenever the overlay closes).
                // Ordering rules (see RestoreGlobalProfileSettings):
                //  - disable AutoTDP and clear IsAutoTDPActive BEFORE the TDP apply
                //    below, or TDPProperty.NotifyPropertyChanged skips the hardware
                //    write;
                //  - ForceSetValue so the widget's toggle gets the pipe push even if
                //    a stale widget-side value matches.
                bool? perStateAutoTdp = isOnAC ? PowerSourceProfileState.AcAutoTdp : PowerSourceProfileState.DcAutoTdp;
                if (perStateAutoTdp.HasValue && autoTDPManager != null)
                {
                    int? perStateAutoTdpFps = isOnAC ? PowerSourceProfileState.AcAutoTdpFps : PowerSourceProfileState.DcAutoTdpFps;
                    if (perStateAutoTdp.Value && perStateAutoTdpFps.HasValue && perStateAutoTdpFps.Value > 0
                        && autoTDPManager.TargetFPS != null
                        && perStateAutoTdpFps.Value != autoTDPManager.TargetFPS.Value)
                    {
                        Logger.Info($"Helper-side AC/DC handler: applying AutoTDP target FPS {perStateAutoTdpFps.Value} from per-state {(isOnAC ? "AC" : "DC")} profile");
                        autoTDPManager.TargetFPS.SetValue(perStateAutoTdpFps.Value);
                    }

                    if (autoTDPManager.Enabled != null && perStateAutoTdp.Value != autoTDPManager.Enabled.Value)
                    {
                        Logger.Info($"Helper-side AC/DC handler: applying AutoTDP={perStateAutoTdp.Value} from per-state {(isOnAC ? "AC" : "DC")} profile");
                        autoTDPManager.Enabled.ForceSetValue(perStateAutoTdp.Value);
                    }

                    if (!perStateAutoTdp.Value && performanceManager.IsAutoTDPActive)
                    {
                        // Only the AutoTDP tick clears this; do it now so the TDP
                        // apply below isn't skipped.
                        performanceManager.IsAutoTDPActive = false;
                    }
                }

                if (performanceManager.IsAutoTDPActive)
                {
                    // AutoTDP stays active for this power state — it overwrites TDP
                    // every tick, so skip the static per-state applies below.
                    return;
                }

                bool isLegionCustomMode = legionManager != null && legionManager.CurrentPerformanceMode == 255;
                bool powerSourceProfileEnabled = PowerSourceProfileState.AutoSwitchEnabled;
                bool tdpGateAllowed = isLegionCustomMode || powerSourceProfileEnabled;

                // 2a) TDP / TDPBoost — gated by Legion Custom or Power-Source-Profile.
                if (!tdpGateAllowed)
                {
                    Logger.Debug("Helper-side AC/DC handler: skipping TDP/TDPBoost reapply — not in Legion Custom mode and Power-Source Profile auto-switch is off (extended fields below still apply)");
                }
                else
                {
                    // Pick the per-state TDP value for the new power state, if the widget
                    // has configured one via PowerSourceProfileValues. Falls back to the
                    // helper's current cached TDP so behavior matches the pre-AC/DC-values
                    // build for users who haven't configured per-state values.
                    int? perStateTdp = isOnAC ? PowerSourceProfileState.AcTdp : PowerSourceProfileState.DcTdp;
                    int targetTdp = perStateTdp ?? performanceManager.TDP.Value;
                    if (targetTdp > 0)
                    {
                        string source = perStateTdp.HasValue ? $"per-state {(isOnAC ? "AC" : "DC")} profile" : "current cached value";
                        Logger.Info($"Helper-side AC/DC handler: applying TDP {targetTdp}W from {source} (legionCustom={isLegionCustomMode}, powerSourceProfile={powerSourceProfileEnabled})");
                        // Update the helper's TDP property so the widget's slider stays
                        // in sync (the property change pipes back to the widget). Use
                        // SetValue rather than just calling SetTDP so the widget UI
                        // reflects the new value.
                        if (perStateTdp.HasValue && perStateTdp.Value != performanceManager.TDP.Value)
                        {
                            performanceManager.TDP.SetValue(targetTdp);
                        }
                        performanceManager.SetTDP(targetTdp);
                    }

                    // Apply per-state TDP Boost too. SetValue triggers the property's
                    // PropertyChanged handler which calls SetTDP again — that's fine, the
                    // debounce in PerformanceManager.SetTDP coalesces the calls.
                    bool? perStateBoost = isOnAC ? PowerSourceProfileState.AcTdpBoost : PowerSourceProfileState.DcTdpBoost;
                    if (perStateBoost.HasValue && performanceManager.TDPBoostEnabled != null)
                    {
                        if (perStateBoost.Value != performanceManager.TDPBoostEnabled.Value)
                        {
                            Logger.Info($"Helper-side AC/DC handler: applying TDPBoost={perStateBoost.Value} from per-state {(isOnAC ? "AC" : "DC")} profile");
                            performanceManager.TDPBoostEnabled.SetValue(perStateBoost.Value);
                        }
                    }
                }

                // 2b) Extended fields (build 2080+): CPUBoost / CPUEPP / CPUState (Min+Max)
                // / OSPowerMode / FPSLimit. Apply unconditionally — these settings aren't
                // managed by Legion preset modes, so they're safe to reapply anytime the
                // cached value differs from current. Skip silently when the cache has no
                // override (legacy widget that didn't pipe extended fields, or first
                // transition before widget had a chance to reconnect). null-coalescing on
                // each SetValue mirrors the TDP block above.
                string state = isOnAC ? "AC" : "DC";

                bool? perStateCpuBoost = isOnAC ? PowerSourceProfileState.AcCpuBoost : PowerSourceProfileState.DcCpuBoost;
                if (perStateCpuBoost.HasValue && powerManager?.CPUBoost != null
                    && perStateCpuBoost.Value != powerManager.CPUBoost.Value)
                {
                    Logger.Info($"Helper-side AC/DC handler: applying CPUBoost={perStateCpuBoost.Value} from per-state {state} profile");
                    powerManager.CPUBoost.SetValue(perStateCpuBoost.Value);
                }

                int? perStateCpuEpp = isOnAC ? PowerSourceProfileState.AcCpuEpp : PowerSourceProfileState.DcCpuEpp;
                if (perStateCpuEpp.HasValue && powerManager?.CPUEPP != null
                    && perStateCpuEpp.Value != powerManager.CPUEPP.Value)
                {
                    Logger.Info($"Helper-side AC/DC handler: applying CPUEPP={perStateCpuEpp.Value} from per-state {state} profile");
                    powerManager.CPUEPP.SetValue(perStateCpuEpp.Value);
                }

                int? perStateMaxCpuState = isOnAC ? PowerSourceProfileState.AcMaxCpuState : PowerSourceProfileState.DcMaxCpuState;
                if (perStateMaxCpuState.HasValue && powerManager?.MaxCPUState != null
                    && perStateMaxCpuState.Value != powerManager.MaxCPUState.Value)
                {
                    Logger.Info($"Helper-side AC/DC handler: applying MaxCPUState={perStateMaxCpuState.Value}% from per-state {state} profile");
                    powerManager.MaxCPUState.SetValue(perStateMaxCpuState.Value);
                }

                int? perStateMinCpuState = isOnAC ? PowerSourceProfileState.AcMinCpuState : PowerSourceProfileState.DcMinCpuState;
                if (perStateMinCpuState.HasValue && powerManager?.MinCPUState != null
                    && perStateMinCpuState.Value != powerManager.MinCPUState.Value)
                {
                    Logger.Info($"Helper-side AC/DC handler: applying MinCPUState={perStateMinCpuState.Value}% from per-state {state} profile");
                    powerManager.MinCPUState.SetValue(perStateMinCpuState.Value);
                }

                int? perStateOsMode = isOnAC ? PowerSourceProfileState.AcOsPowerMode : PowerSourceProfileState.DcOsPowerMode;
                if (perStateOsMode.HasValue && powerManager?.OSPowerMode != null
                    && perStateOsMode.Value != powerManager.OSPowerMode.Value)
                {
                    Logger.Info($"Helper-side AC/DC handler: applying OSPowerMode={perStateOsMode.Value} from per-state {state} profile");
                    powerManager.OSPowerMode.SetValue(perStateOsMode.Value);
                }

                int? perStateFpsLimit = isOnAC ? PowerSourceProfileState.AcFpsLimit : PowerSourceProfileState.DcFpsLimit;
                if (perStateFpsLimit.HasValue && rtssManager?.FPSLimit != null
                    && perStateFpsLimit.Value != rtssManager.FPSLimit.Value)
                {
                    Logger.Info($"Helper-side AC/DC handler: applying FPSLimit={perStateFpsLimit.Value} from per-state {state} profile");
                    rtssManager.FPSLimit.SetValue(perStateFpsLimit.Value);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Helper-side AC/DC handler: reapply TDP threw: {ex.Message}");
            }
        }
    }
}
