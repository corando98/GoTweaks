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
        private async void LoadPowerPlans()
        {
            isLoadingPowerPlans = true;

            try
            {
                // Request power plans from helper
                if (App.IsConnected)
                {
                    var request = new Windows.Foundation.Collections.ValueSet();
                    request.Add("GetPowerPlans", true);

                    var response = await App.SendMessageAsync(request);

                    if (response != null)
                    {
                        availablePowerPlans.Clear();

                        // Parse response: "GUID1|Name1;GUID2|Name2;..."
                        if (response.TryGetValue("PowerPlans", out object plansValue) && plansValue is string plansStr)
                        {
                            var planParts = plansStr.Split(';');
                            foreach (var part in planParts)
                            {
                                if (string.IsNullOrWhiteSpace(part)) continue;

                                var segments = part.Split('|');
                                if (segments.Length >= 2 && Guid.TryParse(segments[0], out Guid planGuid))
                                {
                                    availablePowerPlans.Add(new PowerPlanItem
                                    {
                                        Guid = planGuid,
                                        Name = segments[1]
                                    });
                                }
                            }
                        }

                        // Get currently active plan
                        if (response.TryGetValue("ActivePowerPlan", out object activeValue) && activeValue is string activeStr)
                        {
                            if (Guid.TryParse(activeStr, out Guid activeGuid))
                            {
                                // If no saved preferences, use current active plan as default
                                if (acPowerPlanGuid == Guid.Empty)
                                {
                                    acPowerPlanGuid = activeGuid;
                                }
                                if (dcPowerPlanGuid == Guid.Empty)
                                {
                                    dcPowerPlanGuid = activeGuid;
                                }
                            }
                        }

                        Logger.Info($"Received {availablePowerPlans.Count} power plans from helper");
                    }
                }

                // Fallback to well-known plans if helper didn't respond
                if (availablePowerPlans.Count == 0)
                {
                    Logger.Warn("No power plans received from helper, using defaults");
                    availablePowerPlans.Add(new PowerPlanItem
                    {
                        Guid = new Guid("381b4222-f694-41f0-9685-ff5bb260df2e"),
                        Name = "Balanced"
                    });
                    availablePowerPlans.Add(new PowerPlanItem
                    {
                        Guid = new Guid("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c"),
                        Name = "High Performance"
                    });
                    availablePowerPlans.Add(new PowerPlanItem
                    {
                        Guid = new Guid("a1841308-3541-4fab-bc81-f71556f20b4a"),
                        Name = "Power Saver"
                    });
                }

                // Populate ComboBoxes
                if (ACPowerPlanComboBox != null)
                {
                    ACPowerPlanComboBox.Items.Clear();
                    foreach (var plan in availablePowerPlans)
                    {
                        ACPowerPlanComboBox.Items.Add(new ComboBoxItem { Content = plan.Name, Tag = plan.Guid.ToString() });
                    }

                    // Select saved or default
                    SelectPowerPlanInComboBox(ACPowerPlanComboBox, acPowerPlanGuid);
                }

                if (DCPowerPlanComboBox != null)
                {
                    DCPowerPlanComboBox.Items.Clear();
                    foreach (var plan in availablePowerPlans)
                    {
                        DCPowerPlanComboBox.Items.Add(new ComboBoxItem { Content = plan.Name, Tag = plan.Guid.ToString() });
                    }

                    // Select saved or default
                    SelectPowerPlanInComboBox(DCPowerPlanComboBox, dcPowerPlanGuid);
                }

                // Update toggle state
                if (PowerPlanAutoSwitchToggle != null)
                {
                    PowerPlanAutoSwitchToggle.IsOn = powerPlanAutoSwitch;
                }

                Logger.Info($"Loaded {availablePowerPlans.Count} power plans");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error loading power plans: {ex.Message}");
            }
            finally
            {
                isLoadingPowerPlans = false;
            }
        }

        private void SelectPowerPlanInComboBox(ComboBox comboBox, Guid planGuid)
        {
            if (comboBox == null) return;

            for (int i = 0; i < comboBox.Items.Count; i++)
            {
                if (comboBox.Items[i] is ComboBoxItem item && item.Tag is string guidStr)
                {
                    if (Guid.TryParse(guidStr, out Guid itemGuid) && itemGuid == planGuid)
                    {
                        comboBox.SelectedIndex = i;
                        return;
                    }
                }
            }

            // Default to first item (Balanced) if not found
            if (comboBox.Items.Count > 0)
            {
                comboBox.SelectedIndex = 0;
            }
        }

        private void ACPowerPlanComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (isLoadingPowerPlans) return;

            if (ACPowerPlanComboBox?.SelectedItem is ComboBoxItem selected && selected.Tag is string guidStr)
            {
                if (Guid.TryParse(guidStr, out Guid planGuid))
                {
                    acPowerPlanGuid = planGuid;
                    SavePowerPlanSettings();
                    SendPowerSourceProfileConfigToHelper();

                    // If currently on AC power, apply the plan immediately
                    if (powerPlanAutoSwitch && PowerManager.PowerSupplyStatus == PowerSupplyStatus.Adequate)
                    {
                        ApplyPowerPlan(planGuid);
                    }

                    Logger.Info($"AC Power Plan set to: {selected.Content} ({planGuid})");
                }
            }
        }

        private void DCPowerPlanComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (isLoadingPowerPlans) return;

            if (DCPowerPlanComboBox?.SelectedItem is ComboBoxItem selected && selected.Tag is string guidStr)
            {
                if (Guid.TryParse(guidStr, out Guid planGuid))
                {
                    dcPowerPlanGuid = planGuid;
                    SavePowerPlanSettings();
                    SendPowerSourceProfileConfigToHelper();

                    // If currently on battery, apply the plan immediately
                    if (powerPlanAutoSwitch && PowerManager.PowerSupplyStatus != PowerSupplyStatus.Adequate)
                    {
                        ApplyPowerPlan(planGuid);
                    }

                    Logger.Info($"DC Power Plan set to: {selected.Content} ({planGuid})");
                }
            }
        }

        private void PowerPlanAutoSwitchToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (isLoadingPowerPlans) return;

            powerPlanAutoSwitch = PowerPlanAutoSwitchToggle?.IsOn ?? false;
            SavePowerPlanSettings();
            SendPowerSourceProfileConfigToHelper();

            Logger.Info($"Power Plan auto-switch set to: {powerPlanAutoSwitch}");
        }

        /// <summary>
        /// Mirrors the widget's AC/DC power-plan auto-switch config to the helper so the
        /// helper can act on AC/DC transitions even while the widget is suspended (issue #72).
        /// Sent on pipe connect and on any change to the three inputs that feed the decision.
        /// </summary>
        internal void SendPowerSourceProfileConfigToHelper()
        {
            try
            {
                if (!App.IsConnected) return;

                var jsonObj = new Windows.Data.Json.JsonObject();
                jsonObj["AutoSwitchEnabled"] = Windows.Data.Json.JsonValue.CreateBooleanValue(powerPlanAutoSwitch);
                jsonObj["AcGuid"] = Windows.Data.Json.JsonValue.CreateStringValue(acPowerPlanGuid.ToString());
                jsonObj["DcGuid"] = Windows.Data.Json.JsonValue.CreateStringValue(dcPowerPlanGuid.ToString());

                var request = new Windows.Foundation.Collections.ValueSet
                {
                    { "Command", (int)Shared.Enums.Command.Set },
                    { "Function", (int)Shared.Enums.Function.PowerSourceProfileConfig },
                    { "Content", jsonObj.Stringify() },
                };
                App.PipeClient?.SendValueSet(request);
                Logger.Info($"Sent PowerSourceProfileConfig to helper (autoSwitch={powerPlanAutoSwitch}, ac={acPowerPlanGuid}, dc={dcPowerPlanGuid})");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error sending PowerSourceProfileConfig: {ex.Message}");
            }
        }

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
                jsonObj["AcMaxCpuState"] = Windows.Data.Json.JsonValue.CreateNumberValue(ac.MaxCPUState);
                jsonObj["DcMaxCpuState"] = Windows.Data.Json.JsonValue.CreateNumberValue(dc.MaxCPUState);
                jsonObj["AcMinCpuState"] = Windows.Data.Json.JsonValue.CreateNumberValue(ac.MinCPUState);
                jsonObj["DcMinCpuState"] = Windows.Data.Json.JsonValue.CreateNumberValue(dc.MinCPUState);
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
                    + $"AC: tdp={ac.TDP}W boost={ac.TDPBoostEnabled} cpuBoost={ac.CPUBoost} epp={ac.CPUEPP} cpuState={ac.MinCPUState}-{ac.MaxCPUState} osMode={ac.OSPowerMode} fps={(ac.FPSLimitEnabled ? ac.FPSLimitValue : 0)}; "
                    + $"DC: tdp={dc.TDP}W boost={dc.TDPBoostEnabled} cpuBoost={dc.CPUBoost} epp={dc.CPUEPP} cpuState={dc.MinCPUState}-{dc.MaxCPUState} osMode={dc.OSPowerMode} fps={(dc.FPSLimitEnabled ? dc.FPSLimitValue : 0)})");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error sending PowerSourceProfileValues: {ex.Message}");
            }
        }

    }
}
