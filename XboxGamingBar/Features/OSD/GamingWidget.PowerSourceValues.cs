using Microsoft.Gaming.XboxGameBar;
using Microsoft.Gaming.XboxGameBar.Input;
using Microsoft.UI.Xaml.Controls;
using NLog;
using Shared.Data;
using Shared.Utilities;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Data.Json;
using Windows.Foundation;
using Windows.Foundation.Metadata;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;
using Windows.System.Power;
using Windows.Storage;
using Windows.System;
using Windows.UI.Xaml.Input;
using System.Runtime.InteropServices;
using Windows.UI;
using XboxGamingBar.Data;
using XboxGamingBar.Event;
using XboxGamingBar.IPC;
using XboxGamingBar.QuickSettings;
using Shared.Enums;

namespace XboxGamingBar
{
    public sealed partial class GamingWidget
    {
        /// <summary>
        /// Pipes the active profile's AC and DC TDP / TDPBoost values to the helper. Helper
        /// caches both states and applies the appropriate set when SystemManager fires
        /// PowerSourceChanged — fixes the case where the slider visually updates on AC/DC
        /// but the hardware lags because the widget never told the helper the new value.
        /// Call this after LoadOrCreateGameProfiles, on profile-related setting changes, and
        /// on pipe connect. Only sends per-game game AC/DC profile if a per-game profile is
        /// in use; otherwise sends the global AC/DC profiles.
        /// </summary>
        internal void SendPowerSourceProfileValuesToHelper()
        {
            try
            {
                if (!App.IsConnected) return;

                // Pick which AC/DC pair drives the helper's per-state cache.
                //   1. Per-game profile with per-game AC/DC split enabled → game AC/DC.
                //   2. Global AC/DC split enabled → acProfile / dcProfile.
                //   3. Otherwise → globalProfile for BOTH sides (the user has no AC/DC
                //      differentiation, so AC and DC should resolve to the same values
                //      the helper would apply when no profile-driven override exists).
                //      Without this, acProfile/dcProfile sit at their constructor
                //      defaults (TDP=15W etc.) and the helper would clobber the user's
                //      real global TDP on every AC/DC transition (logged as the
                //      "global TDP=17 jumps to 15 on plug/unplug" bug).
                bool hasGameAcDc = HasValidGame(currentGameName)
                    && GetPerGamePowerSourceProfileEnabled(currentGameName);
                bool hasGlobalAcDc = GetGlobalPowerSourceProfileEnabled();
                PerformanceProfile ac, dc;
                string source;
                if (hasGameAcDc)
                {
                    ac = gameACProfile;
                    dc = gameDCProfile;
                    source = "game-AC/DC";
                }
                else if (hasGlobalAcDc)
                {
                    ac = acProfile;
                    dc = dcProfile;
                    source = "global-AC/DC";
                }
                else
                {
                    ac = globalProfile;
                    dc = globalProfile;
                    source = "global (no AC/DC split)";
                }

                var jsonObj = new Windows.Data.Json.JsonObject();
                jsonObj["AcTdp"] = Windows.Data.Json.JsonValue.CreateNumberValue(ac.TDP);
                jsonObj["DcTdp"] = Windows.Data.Json.JsonValue.CreateNumberValue(dc.TDP);
                jsonObj["AcTdpBoost"] = Windows.Data.Json.JsonValue.CreateBooleanValue(ac.TDPBoostEnabled);
                jsonObj["DcTdpBoost"] = Windows.Data.Json.JsonValue.CreateBooleanValue(dc.TDPBoostEnabled);

                // Extended per-state values (build 2080+) — helper applies these on AC/DC
                // transitions independent of widget lifecycle, fixing FSE-only-helper drift.
                jsonObj["AcCpuBoost"] = Windows.Data.Json.JsonValue.CreateBooleanValue(ac.CPUBoost);
                jsonObj["DcCpuBoost"] = Windows.Data.Json.JsonValue.CreateBooleanValue(dc.CPUBoost);
                jsonObj["AcCpuEpp"] = Windows.Data.Json.JsonValue.CreateNumberValue(ac.CPUEPP);
                jsonObj["DcCpuEpp"] = Windows.Data.Json.JsonValue.CreateNumberValue(dc.CPUEPP);
                jsonObj["AcOsPowerMode"] = Windows.Data.Json.JsonValue.CreateNumberValue(ac.OSPowerMode);
                jsonObj["DcOsPowerMode"] = Windows.Data.Json.JsonValue.CreateNumberValue(dc.OSPowerMode);
                // FPSLimit collapses Enabled+Value into a single int on the wire: 0 = off,
                // non-zero = the cap. Matches the helper's FPSLimitProperty model where 0
                // means "no limit".
                jsonObj["AcFpsLimit"] = Windows.Data.Json.JsonValue.CreateNumberValue(ac.FPSLimitEnabled ? ac.FPSLimitValue : 0);
                jsonObj["DcFpsLimit"] = Windows.Data.Json.JsonValue.CreateNumberValue(dc.FPSLimitEnabled ? dc.FPSLimitValue : 0);

                // AutoTDP per-state (#94 item 3): without these the helper had no way
                // to follow a game profile whose AC and DC sides differ on AutoTDP —
                // the widget-side switch only runs while the widget is awake, and Game
                // Bar suspends it whenever the overlay closes.
                jsonObj["AcAutoTdp"] = Windows.Data.Json.JsonValue.CreateBooleanValue(ac.AutoTDPEnabled);
                jsonObj["DcAutoTdp"] = Windows.Data.Json.JsonValue.CreateBooleanValue(dc.AutoTDPEnabled);
                jsonObj["AcAutoTdpFps"] = Windows.Data.Json.JsonValue.CreateNumberValue(ac.AutoTDPTargetFPS);
                jsonObj["DcAutoTdpFps"] = Windows.Data.Json.JsonValue.CreateNumberValue(dc.AutoTDPTargetFPS);

                var request = new Windows.Foundation.Collections.ValueSet
                {
                    { "Command", (int)Shared.Enums.Command.Set },
                    { "Function", (int)Shared.Enums.Function.PowerSourceProfileValues },
                    { "Content", jsonObj.Stringify() },
                };
                App.PipeClient?.SendValueSet(request);
                Logger.Info($"Sent PowerSourceProfileValues to helper (source={source}, "
                    + $"AC: tdp={ac.TDP}W boost={ac.TDPBoostEnabled} cpuBoost={ac.CPUBoost} epp={ac.CPUEPP} osMode={ac.OSPowerMode} fps={(ac.FPSLimitEnabled ? ac.FPSLimitValue : 0)}; "
                    + $"DC: tdp={dc.TDP}W boost={dc.TDPBoostEnabled} cpuBoost={dc.CPUBoost} epp={dc.CPUEPP} osMode={dc.OSPowerMode} fps={(dc.FPSLimitEnabled ? dc.FPSLimitValue : 0)})");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error sending PowerSourceProfileValues: {ex.Message}");
            }
        }

    }
}
