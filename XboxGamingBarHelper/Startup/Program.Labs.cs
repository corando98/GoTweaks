using NLog;
using Shared.Constants;
using Shared.Data;
using Shared.IPC;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.System;
using Windows.UI.Input.Preview.Injection;
using XboxGamingBarHelper.AMD;
using XboxGamingBarHelper.Core;
using XboxGamingBarHelper.ControllerEmulation;
using XboxGamingBarHelper.Devices.Libraries.GPD;
using XboxGamingBarHelper.Devices.Libraries.Legion;
using XboxGamingBarHelper.LosslessScaling;
using XboxGamingBarHelper.OnScreenDisplay;
using XboxGamingBarHelper.Performance;
using XboxGamingBarHelper.Power;
using XboxGamingBarHelper.Profile;
using XboxGamingBarHelper.RTSS;
using XboxGamingBarHelper.Settings;
using XboxGamingBarHelper.Systems;
using XboxGamingBarHelper.AutoTDP;
using XboxGamingBarHelper.DefaultGameProfiles;
using XboxGamingBarHelper.Labs;
using Shared.Enums;

namespace XboxGamingBarHelper
{
    internal partial class Program
    {
        /// <summary>
        /// Get the current status of DAService (Legion Space service). Reports the startup
        /// state, NOT the transient run state (StartType is set instantly by sc.exe, so this
        /// is accurate immediately).
        ///   1 = Enabled  → auto-starts at boot (StartType == Automatic).
        ///   0 = Disabled → won't auto-start at boot. We use Manual ("demand"), NOT Windows-
        ///                  Disabled, so the user can still launch Legion Space on demand; the
        ///                  service just doesn't return after a reboot. (Windows-Disabled would
        ///                  also read as 0 here, for back-compat with older installs.)
        ///   2 = Not Found.
        /// </summary>
        private static int GetDAServiceStatus()
        {
            try
            {
                using (var sc = new ServiceController("DAService"))
                {
                    var startType = sc.StartType;
                    Logger.Debug($"Labs: DAService StartType = {startType}, Status = {sc.Status}");
                    return startType == ServiceStartMode.Automatic ? 1 : 0;
                }
            }
            catch (InvalidOperationException)
            {
                // Service not found
                return 2;
            }
            catch (Exception ex)
            {
                Logger.Error($"Labs: Error getting DAService status: {ex.Message}");
                return 2;
            }
        }

        /// <summary>
        /// Control DAService (Legion Space service) startup type.
        /// Action: 0 = Disable auto-start — set startup to Manual ("demand") + stop the running
        ///         instance. The daemon no longer runs at boot and does NOT return after a reboot,
        ///         but Legion Space can STILL start it on demand (unlike Windows-Disabled, which
        ///         blocks launching it entirely). 1 = Enable — set startup to auto + start now.
        ///
        /// The startup-type change (sc.exe config) is applied synchronously and is fast — that's
        /// the part GetDAServiceStatus reports. The actual stop/start of the running instance is
        /// kicked off on a background task so we never block the pipe handler thread on
        /// WaitForStatus (which could exceed the widget's 10 s request timeout and wedge the pipe).
        /// </summary>
        private static void ControlDAService(int action)
        {
            try
            {
                if (action == 0) // Disable auto-start (Manual/demand)
                {
                    // Save the original ENABLED (auto-start) state before the first modification
                    // so a clean uninstall can restore it correctly.
                    try
                    {
                        using (var sc = new ServiceController("DAService"))
                        {
                            bool wasEnabled = sc.StartType == ServiceStartMode.Automatic;
                            Services.SystemRestoreService.SaveOriginalDAServiceState(wasEnabled);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"Failed to save original DAService state: {ex.Message}");
                    }

                    // Set startup to Manual ("demand"): won't auto-start at boot (so it doesn't
                    // return after a reboot), but stays launchable on demand by Legion Space.
                    SetDAServiceStartType("demand");

                    // Then stop the running instance in the background (best-effort).
                    Task.Run(() =>
                    {
                        try
                        {
                            using (var sc = new ServiceController("DAService"))
                            {
                                if (sc.Status == ServiceControllerStatus.Running)
                                {
                                    Logger.Info("Labs: Stopping DAService (background)...");
                                    sc.Stop();
                                    sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
                                    Logger.Info("Labs: DAService stopped");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn($"Labs: background DAService stop failed: {ex.Message}");
                        }
                    });
                }
                else // Enable auto-start
                {
                    // Restore the startup type to auto.
                    SetDAServiceStartType("auto");

                    // Then start the service in the background (best-effort).
                    Task.Run(() =>
                    {
                        try
                        {
                            using (var sc = new ServiceController("DAService"))
                            {
                                if (sc.Status == ServiceControllerStatus.Stopped)
                                {
                                    Logger.Info("Labs: Starting DAService (background)...");
                                    sc.Start();
                                    sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
                                    Logger.Info("Labs: DAService started");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn($"Labs: background DAService start failed: {ex.Message}");
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Labs: Error controlling DAService: {ex.Message}");
            }
        }

        /// <summary>
        /// Sets DAService's startup type via sc.exe ("demand" = Manual, or "auto"). Synchronous
        /// and fast; this is the startup state GetDAServiceStatus reports.
        /// </summary>
        private static void SetDAServiceStartType(string startMode)
        {
            try
            {
                Logger.Info($"Labs: Setting DAService startup to {startMode}...");
                using (var proc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "sc.exe",
                        Arguments = $"config DAService start= {startMode}",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true
                    }
                })
                {
                    proc.Start();
                    proc.WaitForExit(5000);
                    Logger.Info($"Labs: DAService startup set to {startMode} (exit code: {proc.ExitCode})");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Labs: Failed to set DAService startup to {startMode}: {ex.Message}");
            }
        }

        /// <summary>
        /// Export all Default Game Profiles to a text file on the Desktop.
        /// </summary>
        private static string ExportDefaultGameProfiles()
        {
            var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            var exportPath = System.IO.Path.Combine(desktopPath, $"GoTweaks_DGPs_{DateTime.Now:yyyy-MM-dd_HHmmss}.txt");

            using (var writer = new System.IO.StreamWriter(exportPath))
            {
                writer.WriteLine($"GoTweaks Default Game Profiles Export");
                writer.WriteLine($"Exported: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                writer.WriteLine($"========================================");
                writer.WriteLine();

                if (defaultGameProfileManager == null)
                {
                    writer.WriteLine("DefaultGameProfileManager not initialized.");
                    return exportPath;
                }

                var service = defaultGameProfileManager.GetService();
                if (service == null)
                {
                    writer.WriteLine("DefaultGameProfileService not available.");
                    return exportPath;
                }

                writer.WriteLine($"Hardware Variant: {service.HardwareVariant}");
                writer.WriteLine($"Primary Profile Key: {service.PrimaryProfileKey}");
                writer.WriteLine($"Effective Profile Key: {service.EffectiveProfileKey}");
                writer.WriteLine($"Total Profiles: {service.ProfileCount}");
                writer.WriteLine();
                writer.WriteLine("========================================");
                writer.WriteLine("PROFILE LIST (key -> game name [hardware]: TDP/FPS)");
                writer.WriteLine("========================================");
                writer.WriteLine();

                var keys = service.GetAllProfileKeys().OrderBy(k => k).ToList();
                foreach (var key in keys)
                {
                    var info = service.GetProfileDebugInfo(key);
                    // Format: key -> info (which already includes game name and profiles)
                    writer.WriteLine($"{key} -> {info}");
                }
            }

            return exportPath;
        }

        /// <summary>
        /// Parses global widget settings from a serialized ValueSet XML string.
        /// The widget sends settings as a ValueSet serialized to XML.
        /// </summary>
        private static Shared.Data.GlobalWidgetSettings ParseGlobalSettingsFromValueSet(string valueSetXml)
        {
            // The widget sends a compact XML representation of a ValueSet
            // We need to extract the key-value pairs and build a GlobalWidgetSettings object
            var gs = new Shared.Data.GlobalWidgetSettings();

            try
            {
                // Parse the XML to extract values
                var doc = new System.Xml.XmlDocument();
                doc.LoadXml(valueSetXml);

                // Helper to get int value
                int? GetInt(string key)
                {
                    var node = doc.SelectSingleNode($"//*[local-name()='{key}']");
                    if (node != null && int.TryParse(node.InnerText, out int val))
                        return val;
                    return null;
                }

                // Helper to get string value
                string GetString(string key)
                {
                    var node = doc.SelectSingleNode($"//*[local-name()='{key}']");
                    return node?.InnerText;
                }

                // Legion Button Remapping
                gs.LegionL_Action = GetInt("LegionL_Action");
                gs.LegionL_Shortcut = GetString("LegionL_Shortcut");
                gs.LegionL_Command = GetString("LegionL_Command");
                gs.LegionR_Action = GetInt("LegionR_Action");
                gs.LegionR_Shortcut = GetString("LegionR_Shortcut");
                gs.LegionR_Command = GetString("LegionR_Command");

                // Scroll Wheel Remapping
                gs.Scroll_Action = GetInt("Scroll_Action");
                gs.Scroll_Shortcut = GetString("Scroll_Shortcut");
                gs.Scroll_Command = GetString("Scroll_Command");
                gs.ScrollClick_Action = GetInt("ScrollClick_Action");
                gs.ScrollClick_Shortcut = GetString("ScrollClick_Shortcut");
                gs.ScrollClick_Command = GetString("ScrollClick_Command");

                // Device TDP Limits
                gs.DeviceTDPMin = GetInt("DeviceTDPMin");
                gs.DeviceTDPMax = GetInt("DeviceTDPMax");

                // OSD Customization
                gs.OSD_TextSize = GetInt("OSD_TextSize");
                gs.OSD_TextColor = GetString("OSD_TextColor");
                gs.OSD_LabelColor = GetString("OSD_LabelColor");
                gs.OSD_Opacity = GetInt("OSD_Opacity");

                // OSD Level Configuration - Order
                gs.OSD_L1_Order = GetString("OSD_L1_Order");
                gs.OSD_L2_Order = GetString("OSD_L2_Order");
                gs.OSD_L3_Order = GetString("OSD_L3_Order");

                // OSD Level Configuration - Enabled items
                gs.OSD_L1_Enabled = GetString("OSD_L1_Enabled");
                gs.OSD_L2_Enabled = GetString("OSD_L2_Enabled");
                gs.OSD_L3_Enabled = GetString("OSD_L3_Enabled");

                // OSD Level Configuration - Per-item colors
                gs.OSD_L1_ItemColors = GetString("OSD_L1_ItemColors");
                gs.OSD_L2_ItemColors = GetString("OSD_L2_ItemColors");
                gs.OSD_L3_ItemColors = GetString("OSD_L3_ItemColors");

                // OSD Level Configuration - Columns
                gs.OSD_L1_Columns = GetInt("OSD_L1_Columns");
                gs.OSD_L2_Columns = GetInt("OSD_L2_Columns");
                gs.OSD_L3_Columns = GetInt("OSD_L3_Columns");

                Logger.Info($"Parsed global settings: TDPMin={gs.DeviceTDPMin}, TDPMax={gs.DeviceTDPMax}, " +
                           $"LegionL={gs.LegionL_Action}, LegionR={gs.LegionR_Action}");
            }
            catch (Exception ex)
            {
                Logger.Warn($"Error parsing global settings XML: {ex.Message}");
            }

            return gs;
        }

        /// <summary>
        /// Export all per-game profiles to a folder on the Desktop.
        /// Copies profile XML files and creates an index file.
        /// </summary>
        private static string ExportProfiles()
        {
            var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            var exportFolderName = $"GoTweaks_Profiles_{DateTime.Now:yyyy-MM-dd_HHmmss}";
            var exportPath = Path.Combine(desktopPath, exportFolderName);

            // Create export folder
            Directory.CreateDirectory(exportPath);

            // Get profiles folder path
            var profilesFolder = XboxGamingBarHelper.Profile.ProfileManager.GetGameProfilesFolder();
            var globalProfilePath = XboxGamingBarHelper.Profile.ProfileManager.GetGlobalProfilePath();

            int copiedCount = 0;

            // Copy global profile
            if (File.Exists(globalProfilePath))
            {
                var destPath = Path.Combine(exportPath, Path.GetFileName(globalProfilePath));
                File.Copy(globalProfilePath, destPath, true);
                copiedCount++;
            }

            // Copy all per-game profile XMLs
            if (Directory.Exists(profilesFolder))
            {
                var xmlFiles = Directory.GetFiles(profilesFolder, "*.xml");
                foreach (var xmlFile in xmlFiles)
                {
                    var destPath = Path.Combine(exportPath, Path.GetFileName(xmlFile));
                    File.Copy(xmlFile, destPath, true);
                    copiedCount++;
                }
            }

            // Create index file with summary
            var indexPath = Path.Combine(exportPath, "_index.txt");
            using (var writer = new StreamWriter(indexPath))
            {
                writer.WriteLine($"GoTweaks Per-Game Profiles Export");
                writer.WriteLine($"Exported: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                writer.WriteLine($"========================================");
                writer.WriteLine();
                writer.WriteLine($"Total profiles exported: {copiedCount}");
                writer.WriteLine();
                writer.WriteLine("Files:");

                // List global profile
                if (File.Exists(globalProfilePath))
                {
                    writer.WriteLine($"  - {Path.GetFileName(globalProfilePath)} (Global)");
                }

                // List per-game profiles
                if (Directory.Exists(profilesFolder))
                {
                    var xmlFiles = Directory.GetFiles(profilesFolder, "*.xml").OrderBy(f => f);
                    foreach (var xmlFile in xmlFiles)
                    {
                        writer.WriteLine($"  - {Path.GetFileName(xmlFile)}");
                    }
                }
            }

            Logger.Info($"Exported {copiedCount} profiles to {exportPath}");
            return exportPath;
        }

        /// <summary>
        /// Export all GoTweaks data: profiles, settings, Q-learning model, OSD config.
        /// Creates a comprehensive backup folder on the Desktop.
        /// </summary>
        /// <param name="widgetSettings">JSON string of widget LocalSettings to include in export</param>
        /// <returns>Path to the export folder</returns>
        private static string ExportAllData(string widgetSettings = null)
        {
            var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            var exportFolderName = $"GoTweaks_Backup_{DateTime.Now:yyyy-MM-dd_HHmmss}";
            var exportPath = Path.Combine(desktopPath, exportFolderName);

            // Create export folder structure
            Directory.CreateDirectory(exportPath);
            var profilesFolder = Path.Combine(exportPath, "profiles");
            Directory.CreateDirectory(profilesFolder);

            int itemCount = 0;
            var manifest = new System.Text.StringBuilder();
            manifest.AppendLine("{");
            manifest.AppendLine($"  \"exportDate\": \"{DateTime.Now:O}\",");
            manifest.AppendLine($"  \"version\": \"1.0\",");
            manifest.AppendLine($"  \"appVersion\": \"{typeof(Program).Assembly.GetName().Version}\",");

            // 1. Export per-game profiles
            var srcProfilesFolder = XboxGamingBarHelper.Profile.ProfileManager.GetGameProfilesFolder();
            var profileNames = new List<string>();
            if (Directory.Exists(srcProfilesFolder))
            {
                var xmlFiles = Directory.GetFiles(srcProfilesFolder, "*.xml");
                foreach (var xmlFile in xmlFiles)
                {
                    var destPath = Path.Combine(profilesFolder, Path.GetFileName(xmlFile));
                    File.Copy(xmlFile, destPath, true);
                    profileNames.Add(Path.GetFileNameWithoutExtension(xmlFile));
                    itemCount++;
                }
            }

            // 2. Export global profile
            var globalProfilePath = XboxGamingBarHelper.Profile.ProfileManager.GetGlobalProfilePath();
            if (File.Exists(globalProfilePath))
            {
                var destPath = Path.Combine(exportPath, "global.xml");
                File.Copy(globalProfilePath, destPath, true);
                itemCount++;
            }

            // 3. Export Q-learning model (AutoTDP)
            var localStatePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Packages", "PlayandBuildCustom.10365195AA1EC_8edemd50ez3gg", "LocalState"
            );
            var qTablePath = Path.Combine(localStatePath, "autotdp_qtable.json");
            if (File.Exists(qTablePath))
            {
                var destPath = Path.Combine(exportPath, "autotdp_qtable.json");
                File.Copy(qTablePath, destPath, true);
                itemCount++;
                Logger.Info("Exported Q-learning model");
            }

            // 4. Export helper settings (from LocalCache/settings.json)
            var localCachePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Packages", "PlayandBuildCustom.10365195AA1EC_8edemd50ez3gg", "LocalCache"
            );
            var helperSettingsPath = Path.Combine(localCachePath, "settings.json");
            if (File.Exists(helperSettingsPath))
            {
                var destPath = Path.Combine(exportPath, "helper_settings.json");
                File.Copy(helperSettingsPath, destPath, true);
                itemCount++;
                Logger.Info("Exported helper settings");
            }

            // 5. Export widget settings (passed from widget)
            if (!string.IsNullOrEmpty(widgetSettings))
            {
                var destPath = Path.Combine(exportPath, "widget_settings.json");
                File.WriteAllText(destPath, widgetSettings);
                itemCount++;
                Logger.Info("Exported widget settings");
            }

            // 6. Export system restore data
            var systemRestorePath = Path.Combine(localStatePath, "system_restore_data.json");
            if (File.Exists(systemRestorePath))
            {
                var destPath = Path.Combine(exportPath, "system_restore_data.json");
                File.Copy(systemRestorePath, destPath, true);
                itemCount++;
            }

            // Build manifest
            manifest.AppendLine($"  \"profileCount\": {profileNames.Count},");
            manifest.AppendLine($"  \"profiles\": [{string.Join(", ", profileNames.Select(p => $"\"{p}\""))}],");
            manifest.AppendLine($"  \"hasGlobalProfile\": {(File.Exists(globalProfilePath) ? "true" : "false")},");
            manifest.AppendLine($"  \"hasQTable\": {(File.Exists(qTablePath) ? "true" : "false")},");
            manifest.AppendLine($"  \"hasHelperSettings\": {(File.Exists(helperSettingsPath) ? "true" : "false")},");
            manifest.AppendLine($"  \"hasWidgetSettings\": {(!string.IsNullOrEmpty(widgetSettings) ? "true" : "false")},");
            manifest.AppendLine($"  \"totalItems\": {itemCount}");
            manifest.AppendLine("}");

            File.WriteAllText(Path.Combine(exportPath, "manifest.json"), manifest.ToString());

            Logger.Info($"Exported {itemCount} items to {exportPath}");
            return exportPath;
        }

        /// <summary>
        /// Import all GoTweaks data from a backup folder.
        /// </summary>
        /// <param name="importPath">Path to the backup folder</param>
        /// <returns>Summary of imported items and widget settings JSON to apply</returns>
        private static (string summary, string widgetSettings) ImportAllData(string importPath)
        {
            if (!Directory.Exists(importPath))
            {
                throw new DirectoryNotFoundException($"Import folder not found: {importPath}");
            }

            var summary = new System.Text.StringBuilder();
            summary.AppendLine("=== Import Results ===");
            summary.AppendLine();

            int importedCount = 0;
            int skippedCount = 0;
            string widgetSettings = null;

            // 1. Import per-game profiles
            var srcProfilesFolder = Path.Combine(importPath, "profiles");
            var destProfilesFolder = XboxGamingBarHelper.Profile.ProfileManager.GetGameProfilesFolder();
            Directory.CreateDirectory(destProfilesFolder);

            if (Directory.Exists(srcProfilesFolder))
            {
                var xmlFiles = Directory.GetFiles(srcProfilesFolder, "*.xml");
                foreach (var xmlFile in xmlFiles)
                {
                    try
                    {
                        var destPath = Path.Combine(destProfilesFolder, Path.GetFileName(xmlFile));
                        File.Copy(xmlFile, destPath, true);
                        importedCount++;
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"Failed to import profile {Path.GetFileName(xmlFile)}: {ex.Message}");
                        skippedCount++;
                    }
                }
                summary.AppendLine($"✓ Imported {xmlFiles.Length} per-game profiles");
            }

            // 2. Import global profile
            var srcGlobalProfile = Path.Combine(importPath, "global.xml");
            if (File.Exists(srcGlobalProfile))
            {
                try
                {
                    var destPath = XboxGamingBarHelper.Profile.ProfileManager.GetGlobalProfilePath();
                    var destDir = Path.GetDirectoryName(destPath);
                    if (!string.IsNullOrEmpty(destDir))
                        Directory.CreateDirectory(destDir);
                    File.Copy(srcGlobalProfile, destPath, true);
                    importedCount++;
                    summary.AppendLine("✓ Imported global profile");
                }
                catch (Exception ex)
                {
                    Logger.Warn($"Failed to import global profile: {ex.Message}");
                    summary.AppendLine($"✗ Failed to import global profile: {ex.Message}");
                }
            }

            // 3. Import Q-learning model
            var srcQTable = Path.Combine(importPath, "autotdp_qtable.json");
            if (File.Exists(srcQTable))
            {
                try
                {
                    var localStatePath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Packages", "PlayandBuildCustom.10365195AA1EC_8edemd50ez3gg", "LocalState"
                    );
                    Directory.CreateDirectory(localStatePath);
                    var destPath = Path.Combine(localStatePath, "autotdp_qtable.json");
                    File.Copy(srcQTable, destPath, true);
                    importedCount++;
                    summary.AppendLine("✓ Imported AutoTDP Q-learning model");

                    // Notify AutoTDPManager to reload if active
                    if (autoTDPManager != null)
                    {
                        autoTDPManager.ReloadQLearningModel();
                        summary.AppendLine("  (Q-learning model reloaded)");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn($"Failed to import Q-learning model: {ex.Message}");
                    summary.AppendLine($"✗ Failed to import Q-learning model: {ex.Message}");
                }
            }

            // 4. Import helper settings
            var srcHelperSettings = Path.Combine(importPath, "helper_settings.json");
            if (File.Exists(srcHelperSettings))
            {
                try
                {
                    var localCachePath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Packages", "PlayandBuildCustom.10365195AA1EC_8edemd50ez3gg", "LocalCache"
                    );
                    Directory.CreateDirectory(localCachePath);
                    var destPath = Path.Combine(localCachePath, "settings.json");
                    File.Copy(srcHelperSettings, destPath, true);
                    importedCount++;
                    summary.AppendLine("✓ Imported helper settings");
                }
                catch (Exception ex)
                {
                    Logger.Warn($"Failed to import helper settings: {ex.Message}");
                    summary.AppendLine($"✗ Failed to import helper settings: {ex.Message}");
                }
            }

            // 5. Read widget settings (to be applied by widget)
            var srcWidgetSettings = Path.Combine(importPath, "widget_settings.json");
            if (File.Exists(srcWidgetSettings))
            {
                try
                {
                    widgetSettings = File.ReadAllText(srcWidgetSettings);
                    importedCount++;
                    summary.AppendLine("✓ Widget settings loaded (will be applied)");
                }
                catch (Exception ex)
                {
                    Logger.Warn($"Failed to read widget settings: {ex.Message}");
                    summary.AppendLine($"✗ Failed to read widget settings: {ex.Message}");
                }
            }

            // 6. Import system restore data
            var srcSystemRestore = Path.Combine(importPath, "system_restore_data.json");
            if (File.Exists(srcSystemRestore))
            {
                try
                {
                    var localStatePath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Packages", "PlayandBuildCustom.10365195AA1EC_8edemd50ez3gg", "LocalState"
                    );
                    Directory.CreateDirectory(localStatePath);
                    var destPath = Path.Combine(localStatePath, "system_restore_data.json");
                    File.Copy(srcSystemRestore, destPath, true);
                    importedCount++;
                    summary.AppendLine("✓ Imported system restore data");
                }
                catch (Exception ex)
                {
                    Logger.Warn($"Failed to import system restore data: {ex.Message}");
                }
            }

            summary.AppendLine();
            summary.AppendLine($"Total: {importedCount} imported, {skippedCount} skipped");
            summary.AppendLine();
            summary.AppendLine("Note: Restart the widget to apply all changes.");

            Logger.Info($"Import complete: {importedCount} imported, {skippedCount} skipped");
            return (summary.ToString(), widgetSettings);
        }

        /// <summary>
        /// Instantiate the GoTweaks lighting + standalone haptic managers (idempotent) and
        /// apply current settings. Subscribes to the two config properties so widget edits
        /// reconfigure the managers live. Called once the LegionButtonMonitor is running.
        /// </summary>
        private static void InitGoTweaksLightingAndHaptics(LegionButtonMonitor monitor)
        {
            try
            {
                if (legionLightingManager == null && legionManager != null)
                {
                    legionLightingManager = new XboxGamingBarHelper.Labs.LegionLightingManager(legionManager);
                }
                if (goTweaksHapticManager == null)
                {
                    goTweaksHapticManager = new XboxGamingBarHelper.Labs.GoTweaksHapticManager();
                }

                // Apply persisted config now.
                legionLightingManager?.ApplyConfigString(settingsManager?.GoTweaksLightingConfig?.Value);
                goTweaksHapticManager?.ApplyConfigString(settingsManager?.GoTweaksHapticsConfig?.Value);
                if (settingsManager?.LegionControllerSleepMinutes != null)
                {
                    try { monitor.SetAutoSleepTime(settingsManager.LegionControllerSleepMinutes.Value); }
                    catch (Exception ex) { Logger.Warn($"Apply sleep time at init threw: {ex.Message}"); }
                }

                if (!goTweaksFeaturesHooked)
                {
                    if (settingsManager?.GoTweaksLightingConfig != null)
                    {
                        settingsManager.GoTweaksLightingConfig.PropertyChanged += OnGoTweaksLightingConfigChanged;
                    }
                    if (settingsManager?.GoTweaksHapticsConfig != null)
                    {
                        settingsManager.GoTweaksHapticsConfig.PropertyChanged += OnGoTweaksHapticsConfigChanged;
                    }
                    if (settingsManager?.LegionControllerSleepMinutes != null)
                    {
                        settingsManager.LegionControllerSleepMinutes.PropertyChanged += OnLegionControllerSleepMinutesChanged;
                    }
                    goTweaksFeaturesHooked = true;
                }

                Logger.Info("GoTweaks lighting + haptics initialized");
            }
            catch (Exception ex)
            {
                Logger.Warn($"InitGoTweaksLightingAndHaptics threw: {ex.Message}");
            }
        }

        private static void OnGoTweaksLightingConfigChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            try { legionLightingManager?.ApplyConfigString(settingsManager?.GoTweaksLightingConfig?.Value); }
            catch (Exception ex) { Logger.Warn($"OnGoTweaksLightingConfigChanged threw: {ex.Message}"); }
        }

        private static void OnLegionControllerSleepMinutesChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            try { legionButtonMonitor?.SetAutoSleepTime(settingsManager?.LegionControllerSleepMinutes?.Value ?? 15); }
            catch (Exception ex) { Logger.Warn($"OnLegionControllerSleepMinutesChanged threw: {ex.Message}"); }
        }

        private static void OnGoTweaksHapticsConfigChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            try { goTweaksHapticManager?.ApplyConfigString(settingsManager?.GoTweaksHapticsConfig?.Value); }
            catch (Exception ex) { Logger.Warn($"OnGoTweaksHapticsConfigChanged threw: {ex.Message}"); }
        }

        private static LegionButtonMonitor EnsureLegionButtonMonitor()
        {
            lock (legionButtonMonitorLock)
            {
                if (legionButtonMonitor == null)
                {
                    legionButtonMonitor = new LegionButtonMonitor();
                    Logger.Info("Labs: Created unified Legion button monitor with battery support");
                }

                if (!legionButtonMonitorBatteryHooked)
                {
                    legionButtonMonitor.BatteryUpdated += LegionButtonMonitor_BatteryUpdated;
                    // Route b0:01 light/device status (parsed on the button monitor's handle) into
                    // the LegionManager Info-card pipeline. Needed because on devices where the
                    // button monitor owns the HID device, LegionControllerService's own status
                    // read loop never runs, leaving the card showing stale defaults.
                    legionButtonMonitor.DeviceStatusUpdated += LegionButtonMonitor_DeviceStatusUpdated;
                    legionButtonMonitorBatteryHooked = true;
                }

                return legionButtonMonitor;
            }
        }

        /// <summary>
        /// Called when a controller-emulation backend (VIIPER or legacy) changes state.
        /// Re-runs VIIPER's guide-only reconciliation (ReapplyMode) — the sole
        /// owner of the Guide route since the ViGEm retirement.
        /// </summary>
        internal static void NotifyGuideRouteChanged()
        {
            try
            {
                viiperEmulationManager?.OnGuideRouteChanged();
            }
            catch (Exception ex)
            {
                Logger.Warn($"Labs: viiperEmulationManager.OnGuideRouteChanged threw: {ex.Message}");
            }
        }

        private static void LegionButtonMonitor_BatteryUpdated(object sender, LegionButtonBatteryEventArgs e)
        {
            try
            {
                legionManager?.UpdateControllerBatteryFromButtonMonitor(
                    e.LeftBattery, e.LeftCharging, e.LeftConnected,
                    e.RightBattery, e.RightCharging, e.RightConnected);

                LegionButtonMonitor monitor = sender as LegionButtonMonitor;
                string vidPid = monitor?.DetectedVidPid ?? legionButtonMonitor?.DetectedVidPid;
                if (!string.IsNullOrEmpty(vidPid))
                {
                    legionManager?.UpdateControllerVidPid(vidPid);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"BatteryUpdated handler exception: {ex.Message}");
            }
        }

        private static void LegionButtonMonitor_DeviceStatusUpdated(object sender, XboxGamingBarHelper.Devices.Libraries.Legion.LegionGoStatus status)
        {
            try
            {
                legionManager?.IngestDeviceStatus(status);
            }
            catch (Exception ex)
            {
                Logger.Error($"DeviceStatusUpdated handler exception: {ex.Message}");
            }
        }

        private static void DisposeLegionButtonMonitor()
        {
            lock (legionButtonMonitorLock)
            {
                if (legionButtonMonitor == null)
                {
                    legionButtonMonitorBatteryHooked = false;
                    return;
                }

                try
                {
                    if (legionButtonMonitorBatteryHooked)
                    {
                        legionButtonMonitor.BatteryUpdated -= LegionButtonMonitor_BatteryUpdated;
                    }
                }
                catch
                {
                    // Best-effort cleanup only.
                }
                finally
                {
                    legionButtonMonitorBatteryHooked = false;
                }

                try
                {
                    legionButtonMonitor.Dispose();
                }
                finally
                {
                    legionButtonMonitor = null;
                }
            }
        }

        /// <summary>
        /// Load and apply Legion button remap settings from LocalSettings on startup.
        /// Uses LocalSettingsHelper which works both inside and outside package context.
        /// </summary>
        private static void LoadLegionButtonRemapSettings()
        {
            try
            {
                // Load Legion L settings using LocalSettingsHelper
                // Action: 0=Disabled, 1=Xbox Guide, 2=Keyboard Shortcut, 3=Run Command, 4=Focus GoTweaks
                int lAction = 0;
                string lShortcut = "";
                string lCommand = "";

                bool lFound = Settings.LocalSettingsHelper.TryGetValue<int>("LegionL_Action", out var lActionVal);
                if (lFound) lAction = lActionVal;
                if (Settings.LocalSettingsHelper.TryGetValue<string>("LegionL_Shortcut", out var lShortcutVal))
                    lShortcut = lShortcutVal;
                if (Settings.LocalSettingsHelper.TryGetValue<string>("LegionL_Command", out var lCommandVal))
                    lCommand = lCommandVal;
                Logger.Info($"Labs: Read LegionL_Action={lAction} (found={lFound}), Shortcut='{lShortcut}', Command='{lCommand}'");

                // Load Legion R settings
                int rAction = 0;
                string rShortcut = "";
                string rCommand = "";

                bool rFound = Settings.LocalSettingsHelper.TryGetValue<int>("LegionR_Action", out var rActionVal);
                if (rFound) rAction = rActionVal;
                if (Settings.LocalSettingsHelper.TryGetValue<string>("LegionR_Shortcut", out var rShortcutVal))
                    rShortcut = rShortcutVal;
                if (Settings.LocalSettingsHelper.TryGetValue<string>("LegionR_Command", out var rCommandVal))
                    rCommand = rCommandVal;
                Logger.Info($"Labs: Read LegionR_Action={rAction} (found={rFound}), Shortcut='{rShortcut}', Command='{rCommand}'");

                // Apply Legion L settings (always configure, even when disabled, to clear any stale state)
                if (lAction > 0)
                {
                    // Map action index to actionType: 1=Xbox Guide(0), 2=Shortcut(1), 3=Command(2), 4=FocusGoTweaks(3)
                    int actionType = lAction - 1;
                    string shortcutOrCommand = actionType == 1 ? lShortcut : (actionType == 2 ? lCommand : "");
                    bool success = ConfigureLegionButtonRemap("L", true, actionType, shortcutOrCommand);
                    Logger.Info($"Labs: Loaded Legion L remap from settings - Action={lAction}, Success={success}");
                }
                else
                {
                    ConfigureLegionButtonRemap("L", false, 0, "");
                    Logger.Info("Labs: Legion L remap disabled (Action=0)");
                }

                // Apply Legion R settings (always configure, even when disabled, to clear any stale state)
                if (rAction > 0)
                {
                    int actionType = rAction - 1;
                    string shortcutOrCommand = actionType == 1 ? rShortcut : (actionType == 2 ? rCommand : "");
                    bool success = ConfigureLegionButtonRemap("R", true, actionType, shortcutOrCommand);
                    Logger.Info($"Labs: Loaded Legion R remap from settings - Action={rAction}, Success={success}");
                }
                else
                {
                    ConfigureLegionButtonRemap("R", false, 0, "");
                    Logger.Info("Labs: Legion R remap disabled (Action=0)");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Labs: Failed to load Legion button remap settings: {ex.Message}");
            }
        }

        /// <summary>
        /// Load and apply Legion scroll wheel remap settings from LocalSettings on startup.
        /// Uses LocalSettingsHelper which works both inside and outside package context.
        /// </summary>
        private static void LoadLegionScrollRemapSettings()
        {
            try
            {
                // Load Scroll (unified Up/Down) settings
                // Action: 0=Disabled, 1=Xbox Guide, 2=Keyboard Shortcut, 3=Run Command, 4=Focus GoTweaks
                int scrollAction = 0;
                string scrollShortcut = "";
                string scrollCommand = "";

                if (Settings.LocalSettingsHelper.TryGetValue<int>("Scroll_Action", out var scrollActionVal))
                    scrollAction = scrollActionVal;
                if (Settings.LocalSettingsHelper.TryGetValue<string>("Scroll_Shortcut", out var scrollShortcutVal))
                    scrollShortcut = scrollShortcutVal;
                if (Settings.LocalSettingsHelper.TryGetValue<string>("Scroll_Command", out var scrollCommandVal))
                    scrollCommand = scrollCommandVal;

                // Load Scroll Click settings
                int clickAction = 0;
                string clickShortcut = "";
                string clickCommand = "";

                if (Settings.LocalSettingsHelper.TryGetValue<int>("ScrollClick_Action", out var clickActionVal))
                    clickAction = clickActionVal;
                if (Settings.LocalSettingsHelper.TryGetValue<string>("ScrollClick_Shortcut", out var clickShortcutVal))
                    clickShortcut = clickShortcutVal;
                if (Settings.LocalSettingsHelper.TryGetValue<string>("ScrollClick_Command", out var clickCommandVal))
                    clickCommand = clickCommandVal;

                // Apply Scroll Up/Down settings (always configure, even when disabled, to clear stale state)
                if (scrollAction > 0)
                {
                    // Map action index to actionType: 1=Xbox Guide(0), 2=Shortcut(1), 3=Command(2), 4=FocusGoTweaks(3)
                    int actionType = scrollAction - 1;
                    string shortcutOrCommand = actionType == 1 ? scrollShortcut : (actionType == 2 ? scrollCommand : "");

                    // Apply to both Up and Down (unified scroll action)
                    bool successUp = ConfigureLegionScrollRemap("Up", true, actionType, shortcutOrCommand);
                    bool successDown = ConfigureLegionScrollRemap("Down", true, actionType, shortcutOrCommand);
                    Logger.Info($"Labs: Loaded Scroll Up/Down remap from settings - Action={scrollAction}, SuccessUp={successUp}, SuccessDown={successDown}");
                }
                else
                {
                    ConfigureLegionScrollRemap("Up", false, 0, "");
                    ConfigureLegionScrollRemap("Down", false, 0, "");
                    Logger.Info("Labs: Scroll Up/Down remap disabled (Action=0)");
                }

                // Apply Scroll Click settings (always configure, even when disabled, to clear stale state)
                if (clickAction > 0)
                {
                    int actionType = clickAction - 1;
                    string shortcutOrCommand = actionType == 1 ? clickShortcut : (actionType == 2 ? clickCommand : "");
                    bool success = ConfigureLegionScrollRemap("Click", true, actionType, shortcutOrCommand);
                    Logger.Info($"Labs: Loaded Scroll Click remap from settings - Action={clickAction}, Success={success}");
                }
                else
                {
                    ConfigureLegionScrollRemap("Click", false, 0, "");
                    Logger.Info("Labs: Scroll Click remap disabled (Action=0)");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Labs: Failed to load Legion scroll wheel remap settings: {ex.Message}");
            }
        }

        /// <summary>
        /// Configure Legion button remap (L or R to Xbox Guide or Keyboard Shortcut).
        /// Uses a single unified monitor that handles both buttons and battery.
        /// </summary>
        /// <param name="button">"L" for Legion L, "R" for Legion R</param>
        /// <param name="enabled">Whether to enable the remap</param>
        /// <param name="actionType">0=Xbox Guide, 1=Keyboard Shortcut, 2=Run Command, 3=Focus GoTweaks</param>
        /// <param name="shortcutOrCommand">Keyboard shortcut string (e.g., "Win+G") or command path</param>
        /// <returns>True if successful</returns>
        private static bool ConfigureLegionButtonRemap(string button, bool enabled, int actionType, string shortcutOrCommand)
        {
            try
            {
                lock (legionButtonMonitorLock)
                {
                    LegionButtonMonitor monitor = EnsureLegionButtonMonitor();

                    // Remember state before configuration
                    bool wasRunning = monitor.IsRunning;

                    // Configure the button on the unified monitor
                    // This just updates internal flags - the monitor loop will pick up changes on next iteration
                    monitor.ConfigureButton(
                        button,
                        enabled,
                        actionType,
                        shortcutOrCommand,
                        (shortcutKeys) =>
                        {
                            // Execute the keyboard shortcut when the button is pressed
                            Logger.Debug($"Labs: Executing shortcut '{shortcutKeys}'");
                            SendKeyboardShortcutViaInputInjector(shortcutKeys);
                        },
                        (commandPath) =>
                        {
                            // Execute the command when the button is pressed
                            Logger.Debug($"Labs: Executing command '{commandPath}'");
                            ExecuteCommand(commandPath);
                        },
                        () =>
                        {
                            // Focus GoTweaks widget when the button is pressed
                            Logger.Debug("Labs: Focusing GoTweaks widget");
                            FocusGoTweaksWidget();
                        }
                    );

                    // Handle different scenarios. (ViGEm retirement: the old
                    // restart-on-pad-requirement-change branch is gone — button
                    // config is always hot-applied while running; Guide delivery
                    // rides VIIPER which reconciles itself via NotifyGuideRouteChanged.)
                    if (!monitor.HasAnyButtonConfigured)
                    {
                        if (!wasRunning)
                        {
                            // Monitor wasn't running, start for battery monitoring
                            monitor.StartForBatteryMonitoring();
                        }
                        // else: already running - buttons off, battery monitoring continues
                        Logger.Info($"Labs: Legion {button} button disabled, no buttons configured - battery monitoring continues");
                        return true;
                    }
                    else if (!wasRunning)
                    {
                        // Monitor wasn't running - start it
                        if (!monitor.Start())
                        {
                            Logger.Error($"Labs: Failed to start Legion button monitoring (controller not found)");
                            return false;
                        }
                    }
                    // else: Monitor was running - config is hot-applied

                    string actionName = !enabled ? "Disabled" :
                                       actionType == 0 ? "Xbox Guide" :
                                       actionType == 1 ? $"Shortcut: {shortcutOrCommand}" :
                                       actionType == 2 ? $"Command: {shortcutOrCommand}" :
                                       "Focus GoTweaks";
                    Logger.Info($"Labs: Legion {button} button configured -> {actionName}");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Labs: Error configuring Legion {button} button remap: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Configure scroll wheel remap (Up/Down/Click to Xbox Guide, Keyboard Shortcut, Command, or Focus GoTweaks).
        /// Uses the unified LegionButtonMonitor which handles both buttons and scroll wheel.
        /// </summary>
        /// <param name="direction">"Up", "Down", or "Click"</param>
        /// <param name="enabled">Whether to enable the remap</param>
        /// <param name="actionType">0=Xbox Guide, 1=Keyboard Shortcut, 2=Run Command, 3=Focus GoTweaks</param>
        /// <param name="shortcutOrCommand">Keyboard shortcut string or command path</param>
        /// <returns>True if successful</returns>
        private static bool ConfigureLegionScrollRemap(string direction, bool enabled, int actionType, string shortcutOrCommand)
        {
            try
            {
                lock (legionButtonMonitorLock)
                {
                    LegionButtonMonitor monitor = EnsureLegionButtonMonitor();

                    // Remember state before configuration
                    bool wasRunning = monitor.IsRunning;

                    // Configure the scroll wheel action on the unified monitor
                    monitor.ConfigureScrollWheel(
                        direction,
                        enabled,
                        actionType,
                        shortcutOrCommand,
                        (shortcutKeys) =>
                        {
                            // Execute the keyboard shortcut when scroll action is triggered
                            Logger.Debug($"Labs: Executing shortcut '{shortcutKeys}' for scroll {direction}");
                            SendKeyboardShortcutViaInputInjector(shortcutKeys);
                        },
                        (commandPath) =>
                        {
                            // Execute the command when scroll action is triggered
                            Logger.Debug($"Labs: Executing command '{commandPath}' for scroll {direction}");
                            ExecuteCommand(commandPath);
                        },
                        () =>
                        {
                            // Focus GoTweaks widget when scroll action is triggered
                            Logger.Debug($"Labs: Focusing GoTweaks widget for scroll {direction}");
                            FocusGoTweaksWidget();
                        }
                    );

                    // Handle different scenarios. (ViGEm retirement: no more
                    // restart-on-pad-requirement-change — config hot-applies.)
                    if (!monitor.HasAnyButtonConfigured && !monitor.HasAnyScrollConfigured)
                    {
                        if (!wasRunning)
                        {
                            monitor.StartForBatteryMonitoring();
                        }
                        Logger.Info($"Labs: Scroll {direction} disabled - battery monitoring continues");
                        return true;
                    }
                    else if (!wasRunning)
                    {
                        // Monitor wasn't running - start it
                        if (!monitor.Start())
                        {
                            Logger.Error($"Labs: Failed to start monitoring (controller not found)");
                            return false;
                        }
                    }
                    // else: Monitor was running - config is hot-applied

                    string actionName = !enabled ? "Disabled" :
                                       actionType == 0 ? "Xbox Guide" :
                                       actionType == 1 ? $"Shortcut: {shortcutOrCommand}" :
                                       actionType == 2 ? $"Command: {shortcutOrCommand}" :
                                       "Focus GoTweaks";
                    Logger.Info($"Labs: Scroll {direction} configured -> {actionName}");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Labs: Error configuring scroll {direction} remap: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Execute a command/executable with optional arguments.
        /// </summary>
        private static void ExecuteCommand(string commandPath)
        {
            try
            {
                if (string.IsNullOrEmpty(commandPath))
                    return;

                // Parse the command - first part is the executable, rest are arguments
                string exe;
                string args = "";

                // Check if the path is quoted
                if (commandPath.StartsWith("\""))
                {
                    int endQuote = commandPath.IndexOf('"', 1);
                    if (endQuote > 0)
                    {
                        exe = commandPath.Substring(1, endQuote - 1);
                        if (endQuote + 1 < commandPath.Length)
                            args = commandPath.Substring(endQuote + 1).Trim();
                    }
                    else
                    {
                        exe = commandPath;
                    }
                }
                else
                {
                    // Find the first space that's not inside the exe path
                    int spaceIndex = commandPath.IndexOf(' ');
                    if (spaceIndex > 0)
                    {
                        exe = commandPath.Substring(0, spaceIndex);
                        args = commandPath.Substring(spaceIndex + 1).Trim();
                    }
                    else
                    {
                        exe = commandPath;
                    }
                }

                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = args,
                    UseShellExecute = true,
                    WindowStyle = System.Diagnostics.ProcessWindowStyle.Normal
                };

                System.Diagnostics.Process.Start(startInfo);
                Logger.Info($"Labs: Executed command: {exe} {args}");
            }
            catch (Exception ex)
            {
                Logger.Error($"Labs: Failed to execute command '{commandPath}': {ex.Message}");
            }
        }

        /// <summary>
        /// Focus GoTweaks widget by opening Game Bar and sending activation command to widget.
        /// Win+G is required to open Game Bar before widget can be activated.
        /// </summary>
        private static async void FocusGoTweaksWidget()
        {
            try
            {
                // Debounce: ignore rapid button presses
                var now = DateTime.Now;
                if ((now - lastFocusWidgetTime).TotalMilliseconds < FocusWidgetDebounceMs)
                {
                    Logger.Debug("Labs: Focus widget debounced (rapid press ignored)");
                    return;
                }
                lastFocusWidgetTime = now;

                // (Beta sidebar overlay removed — Game Bar is the only overlay UI.)

                // Open Game Bar (required for widget activation)
                SendKeyboardShortcutViaInputInjector("Win+G");
                Logger.Info("Labs: Sent Win+G to open Game Bar");

                // Delay to ensure Game Bar is fully open and widget is ready
                await Task.Delay(200);

                // Send focus command to widget via Named Pipes (works when running elevated)
                if (IsPipeConnected)
                {
                    var pipeMsg = new Shared.IPC.PipeMessage
                    {
                        Command = Shared.Enums.Command.Set,
                        Function = Shared.Enums.Function.Labs_FocusWidget
                    };
                    if (SendPipeMessage(pipeMsg))
                    {
                        Logger.Info("Labs: Sent focus widget command via Named Pipe");
                    }
                    else
                    {
                        Logger.Warn("Labs: Failed to send focus widget command via Named Pipe");
                    }
                }
                else
                {
                    Logger.Warn("Labs: Cannot send focus widget command - no pipe connection available");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Labs: Failed to focus GoTweaks widget: {ex.Message}");
            }
        }

    }
}
