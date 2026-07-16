using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using NLog;

namespace XboxGamingBarHelper.Services
{
    /// <summary>
    /// Detects environment problems that make GoTweaks look broken to a new
    /// user even though nothing crashed: conflicting OEM software holding the
    /// controller HID, or missing drivers on hardware that needs them.
    /// Inspired by Handheld Companion's Welcome-flow OEM software review.
    ///
    /// Evaluation is cheap (process list walk + flags the callers already
    /// have), so callers run it on widget connect and on a slow timer, then
    /// push the JSON to the widget which renders a dismissible banner.
    /// </summary>
    internal static class SetupHealthService
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        // Lenovo software known to fight GoTweaks for the controller HID or
        // TDP control. Legion Space's daemon holds the Legion controller
        // device and its per-mode logic stomps our settings — issue #56
        // ("controllers no longer work after helper starts, uninstalling
        // Legion Space fixes it"). Match is case-insensitive on the process
        // name without extension.
        private static readonly string[] ConflictingProcessNames =
        {
            "LegionSpace",
            "LegionSpaceService",
            "LegionGoQuickSettings",
            "LegionZoneService",
        };

        /// <summary>
        /// One evaluated warning. Id is stable across evaluations so the
        /// widget can key dismissals; Action is an optional machine hint the
        /// widget maps to a button ("pawnio" → Install PawnIO).
        /// </summary>
        internal sealed class Warning
        {
            public string Id;
            public string Message;
            public string Action;
        }

        /// <summary>
        /// Runs all checks and returns the warning list serialized as the
        /// JSON array the SetupWarnings pipe function carries. Returns "[]"
        /// when everything is healthy. Never throws.
        /// </summary>
        public static string EvaluateJson(bool isLegionDevice, bool supportsControllerFeatures, bool pawnIOInstalled,
                                          bool usbipNeeded, bool usbipInstalled, string allowlistedLauncher = null)
        {
            try
            {
                var warnings = new List<Warning>();

                // A game launcher on the HidHide allowlist sees every "hidden" device, so
                // the stock controller stays visible (and double-inputs) inside it no
                // matter what GoTweaks hides. Common leftover from DS4Windows-style
                // setups that add Steam to the allowlist. Field report: Steam listing
                // "LeGo2 Default" alongside the emulated pad while a browser gamepad
                // tester (not allowlisted) correctly saw only the emulated one.
                if (!string.IsNullOrEmpty(allowlistedLauncher))
                {
                    warnings.Add(new Warning
                    {
                        Id = "hidhide-allowlist",
                        Message = $"{allowlistedLauncher} is on the HidHide allowlist, so it can still see the stock controller while emulation hides it. Remove it from the allowlist in the HidHide Configuration Client to avoid double input in that app.",
                    });
                }

                if (isLegionDevice && supportsControllerFeatures)
                {
                    string running = FindRunningConflictingProcess();
                    if (running != null)
                    {
                        warnings.Add(new Warning
                        {
                            Id = "legionspace",
                            Message = $"{running} is running. It can take over the controller and undo GoTweaks settings. Consider closing or uninstalling it.",
                        });
                    }
                }

                if (isLegionDevice && !pawnIOInstalled)
                {
                    warnings.Add(new Warning
                    {
                        Id = "pawnio",
                        Message = "PawnIO driver is not installed. Custom TDP and fan control need it.",
                        Action = "pawnio",
                    });
                }

                // VIIPER is the default emulation backend since the ViGEm
                // retirement, and it also serves the Legion-button Guide route on
                // every backend; without usbip-win2 both stay offline silently, so
                // surface the prerequisite prominently instead of relying on the
                // user finding the install card inside the CE tab.
                if (usbipNeeded && !usbipInstalled)
                {
                    warnings.Add(new Warning
                    {
                        Id = "usbip",
                        Message = "usbip-win2 driver is not installed. Controller emulation needs it (a reboot may be required after install).",
                        Action = "usbip",
                    });
                }

                return Serialize(warnings);
            }
            catch (Exception ex)
            {
                Logger.Warn($"SetupHealthService.EvaluateJson failed: {ex.Message}");
                return "[]";
            }
        }

        private static string FindRunningConflictingProcess()
        {
            try
            {
                foreach (var process in Process.GetProcesses())
                {
                    string name;
                    try { name = process.ProcessName; }
                    catch { continue; }
                    finally { process.Dispose(); }

                    foreach (var conflict in ConflictingProcessNames)
                    {
                        if (string.Equals(name, conflict, StringComparison.OrdinalIgnoreCase))
                        {
                            return conflict;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"SetupHealthService process walk failed: {ex.Message}");
            }
            return null;
        }

        private static string Serialize(List<Warning> warnings)
        {
            // Hand-rolled to avoid a JSON dependency for three fields; message
            // text is our own constants so only quotes/backslashes need escaping.
            var sb = new StringBuilder("[");
            for (int i = 0; i < warnings.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"id\":\"").Append(Escape(warnings[i].Id))
                  .Append("\",\"msg\":\"").Append(Escape(warnings[i].Message)).Append('"');
                if (!string.IsNullOrEmpty(warnings[i].Action))
                {
                    sb.Append(",\"action\":\"").Append(Escape(warnings[i].Action)).Append('"');
                }
                sb.Append('}');
            }
            sb.Append(']');
            return sb.ToString();
        }

        private static string Escape(string s)
        {
            return (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
