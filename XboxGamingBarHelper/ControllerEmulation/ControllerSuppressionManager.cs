using Microsoft.Win32;
using Nefarius.Drivers.HidHide;
using Nefarius.Utilities.DeviceManagement.Extensions;
using Nefarius.Utilities.DeviceManagement.PnP;
using NLog;
using Shared.Enums;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;

namespace XboxGamingBarHelper.ControllerEmulation
{
    /// <summary>
    /// Best-effort physical controller suppression using HidHide API (preferred) or CLI fallback.
    /// This avoids double-input by hiding handheld physical devices while forwarding to a virtual controller.
    /// </summary>
    internal sealed class ControllerSuppressionManager : IDisposable
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private readonly object syncRoot = new object();
        private readonly HashSet<string> hiddenDeviceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private string cliPath;
        private bool appRegistered;
        private bool cloakingEnabled;
        private bool apiMissingLogged;
        private bool cliMissingLogged;
        private const int DeviceRestartTimeoutMs = 5000;
        private const string XboxGamingOverlayPackageName = "Microsoft.XboxGamingOverlay";
        private const string XboxGamingOverlayPackageFamilySuffix = "_8wekyb3d8bbwe";
        private const string XboxGamingAppPackageName = "Microsoft.GamingApp";
        private const string XboxGamingAppPackageFamilySuffix = "_8wekyb3d8bbwe";
        private static readonly string[] GameInputServiceNames =
        {
            "GameInputRedistService",
            "GameInputSvc",
        };
        private static readonly string[] SystemInputBrokerPaths =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "ApplicationFrameHost.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "RuntimeBroker.exe"),
            // Temporary diagnostic allow-list entry: confirms whether GameInput-hosted
            // services (running under svchost.exe) are the process path HidHide needs.
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "svchost.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "GameBarPresenceWriter.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SystemApps", "ShellExperienceHost_cw5n1h2txyewy", "ShellExperienceHost.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SystemApps", "Microsoft.Windows.StartMenuExperienceHost_cw5n1h2txyewy", "StartMenuExperienceHost.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SystemApps", "MicrosoftWindows.Client.CBS_cw5n1h2txyewy", "TextInputHost.exe"),
        };
        private static readonly string[] RuntimeInputBrokerProcessNames =
        {
            "ApplicationFrameHost",
            "RuntimeBroker",
            "explorer",
            "ShellExperienceHost",
            "StartMenuExperienceHost",
            "TextInputHost",
        };
        private static readonly string[] GameInputKnownExecutablePaths =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft GameInput", "x64", "GameInputRedistService.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft GameInput", "x64", "GameInputRawInputProxy.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft GameInput", "GameInputRedistService.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "GameInputSvc.exe"),
        };
        private static readonly string[] XboxGameBarExecutableNames =
        {
            "GameBar.exe",
            "GameBarFTServer.exe",
            "GameBarElevatedFT.exe",
        };
        private static readonly string[] XboxGamingAppExecutableNames =
        {
            "XboxGameBarWidgets.exe",
            "XboxPcApp.exe",
            "XboxPcAppFT.exe",
        };
        private static readonly Regex HidHideAppRegistrationRegex =
            new Regex("--app-reg\\s+\"([^\"]+)\"", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public bool IsAvailable => TryGetApiService(out _, logMissing: false) || EnsureCliResolved(logMissing: false);

        // Game launchers that defeat stock-controller hiding when they sit on the
        // HidHide allowlist: an allowlisted app sees every hidden device, so the
        // stock pad stays visible (and double-inputs) inside it regardless of what
        // GoTweaks cloaks. Usually a leftover from DS4Windows-style setups.
        private static readonly string[] LauncherAllowlistOffenders =
        {
            "steam.exe",
            "epicgameslauncher.exe",
            "playnite.desktopapp.exe",
            "playnite.fullscreenapp.exe",
        };

        /// <summary>
        /// Returns the file name of a known game launcher found on the HidHide
        /// allowlist (e.g. "steam.exe"), or null when none is present or the
        /// allowlist can't be read. Used by SetupHealthService to warn that
        /// stock-controller hiding won't apply inside that app.
        /// </summary>
        public string GetAllowlistedGameLauncher()
        {
            try
            {
                if (!TryGetApiService(out IHidHideControlService service, logMissing: false))
                {
                    return null;
                }

                foreach (string path in service.ApplicationPaths ?? Array.Empty<string>())
                {
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    string file = System.IO.Path.GetFileName(path.Trim());
                    foreach (string offender in LauncherAllowlistOffenders)
                    {
                        if (string.Equals(file, offender, StringComparison.OrdinalIgnoreCase))
                        {
                            return file;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"GetAllowlistedGameLauncher failed: {ex.Message}");
            }
            return null;
        }

        public ControllerSuppressionManager()
        {
            EnsureCliResolved(logMissing: true);

            // Register the helper on the HidHide allowlist proactively at startup.
            // Previously this only fired on first emulation Enable, which made
            // "controller invisible to helper" bugs (vvalente30, #79) only show up
            // mid-session and required a follow-up log to triage. By doing it here
            // we get a definitive boot-time log line saying whether the helper is
            // on the allowlist before any toggle happens.
            try
            {
                if (TryGetApiService(out IHidHideControlService apiService, logMissing: false))
                {
                    EnsureApplicationRegistered(apiService);
                }
                else if (!string.IsNullOrEmpty(cliPath))
                {
                    EnsureApplicationRegistered();
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"HidHide proactive app registration at startup failed: {ex.Message}");
            }
        }

        internal static string GetDetectedCliPath()
        {
            string path = ResolveCliPath();
            return (!string.IsNullOrEmpty(path) && File.Exists(path)) ? path : null;
        }

        public bool Enable(DeviceType deviceType, int hideTargetMode, IReadOnlyCollection<string> excludedDeviceIds = null)
        {
            lock (syncRoot)
            {
                Logger.Info($"HidHide suppression enable requested: deviceType={deviceType}, hideTargetMode={hideTargetMode}, excludedCount={(excludedDeviceIds?.Count ?? 0)}");

                if (TryEnableWithApi(deviceType, hideTargetMode, excludedDeviceIds, out bool apiResult))
                {
                    return apiResult;
                }

                if (!EnsureCliResolved(logMissing: true))
                {
                    Logger.Warn("HidHide suppression enable aborted: API unavailable and CLI not found.");
                    return false;
                }

                Logger.Info("HidHide suppression backend selected: CLI fallback");
                return EnableWithCli(deviceType, hideTargetMode, excludedDeviceIds);
            }
        }

        private bool EnableWithCli(DeviceType deviceType, int hideTargetMode, IReadOnlyCollection<string> excludedDeviceIds)
        {
            EnsureInverseApplicationListDisabled();
            EnsureApplicationRegistered();

            RefreshHiddenDeviceIdsFromCli();

            HashSet<string> desiredIds = new HashSet<string>(
                EnumerateDeviceInstanceIds(deviceType, hideTargetMode),
                StringComparer.OrdinalIgnoreCase);

            if (excludedDeviceIds != null && excludedDeviceIds.Count > 0)
            {
                HashSet<string> excluded = new HashSet<string>(excludedDeviceIds, StringComparer.OrdinalIgnoreCase);
                desiredIds.RemoveWhere(id => excluded.Contains(id));
            }

            HashSet<string> changedDeviceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string id in hiddenDeviceIds.ToArray())
            {
                if (desiredIds.Contains(id))
                {
                    continue;
                }

                if (RunCli($"--dev-unhide \"{id}\""))
                {
                    hiddenDeviceIds.Remove(id);
                    changedDeviceIds.Add(id);
                }
            }

            foreach (string id in desiredIds)
            {
                if (hiddenDeviceIds.Contains(id))
                {
                    continue;
                }

                if (RunCli($"--dev-hide \"{id}\""))
                {
                    hiddenDeviceIds.Add(id);
                    changedDeviceIds.Add(id);
                }
            }

            if (!cloakingEnabled)
            {
                if (RunCli("--cloak-on"))
                {
                    cloakingEnabled = true;
                }
                else
                {
                    Logger.Warn("HidHide suppression requested but --cloak-on failed");
                }
            }

            if (changedDeviceIds.Count > 0)
            {
                Logger.Info($"HidHide suppression updated: hidden={hiddenDeviceIds.Count}, target={hideTargetMode}");
                // HidHide updates can need re-enumeration before all applications observe the new visibility.
                RestartDevices(changedDeviceIds);
            }

            if (desiredIds.Count == 0)
            {
                Logger.Warn($"HidHide suppression: no matching physical device IDs found for {deviceType} (target={hideTargetMode})");
            }

            return hiddenDeviceIds.Count > 0;
        }

        private bool TryEnableWithApi(
            DeviceType deviceType,
            int hideTargetMode,
            IReadOnlyCollection<string> excludedDeviceIds,
            out bool result)
        {
            result = false;
            if (!TryGetApiService(out IHidHideControlService service, logMissing: true))
            {
                return false;
            }

            try
            {
                EnsureInverseApplicationListDisabled(service);
                EnsureApplicationRegistered(service);
                RefreshHiddenDeviceIdsFromApi(service);

                HashSet<string> desiredIds = new HashSet<string>(
                    EnumerateDeviceInstanceIds(deviceType, hideTargetMode),
                    StringComparer.OrdinalIgnoreCase);

                if (excludedDeviceIds != null && excludedDeviceIds.Count > 0)
                {
                    HashSet<string> excluded = new HashSet<string>(excludedDeviceIds, StringComparer.OrdinalIgnoreCase);
                    desiredIds.RemoveWhere(id => excluded.Contains(id));
                }

                HashSet<string> changedDeviceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string id in hiddenDeviceIds.ToArray())
                {
                    if (desiredIds.Contains(id))
                    {
                        continue;
                    }

                    if (TryApiRemoveBlockedInstanceId(service, id))
                    {
                        hiddenDeviceIds.Remove(id);
                        changedDeviceIds.Add(id);
                    }
                }

                foreach (string id in desiredIds)
                {
                    if (hiddenDeviceIds.Contains(id))
                    {
                        continue;
                    }

                    if (TryApiAddBlockedInstanceId(service, id))
                    {
                        hiddenDeviceIds.Add(id);
                        changedDeviceIds.Add(id);
                    }
                }

                if (!service.IsActive)
                {
                    service.IsActive = true;
                }

                cloakingEnabled = service.IsActive;

                if (changedDeviceIds.Count > 0)
                {
                    Logger.Info($"HidHide suppression updated via API: hidden={hiddenDeviceIds.Count}, target={hideTargetMode}");
                    RestartDevices(changedDeviceIds);
                }

                if (desiredIds.Count == 0)
                {
                    Logger.Warn($"HidHide suppression: no matching physical device IDs found for {deviceType} (target={hideTargetMode})");
                }

                Logger.Info("HidHide suppression backend selected: API");
                result = hiddenDeviceIds.Count > 0;
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn($"HidHide API suppression enable failed: {ex.Message}. Falling back to CLI.");
                return false;
            }
        }

        public void Disable()
        {
            lock (syncRoot)
            {
                Logger.Info("HidHide suppression disable requested");

                if (TryDisableWithApi())
                {
                    return;
                }

                if (!EnsureCliResolved(logMissing: true))
                {
                    Logger.Warn("HidHide suppression disable aborted: API unavailable and CLI not found.");
                    return;
                }

                Logger.Info("HidHide suppression disable using CLI fallback");
                DisableWithCli();
            }
        }

        private bool TryDisableWithApi()
        {
            if (!TryGetApiService(out IHidHideControlService service, logMissing: true))
            {
                return false;
            }

            try
            {
                RefreshHiddenDeviceIdsFromApi(service);

                HashSet<string> changedDeviceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int unhiddenCount = 0;
                foreach (string id in hiddenDeviceIds.ToArray())
                {
                    if (TryApiRemoveBlockedInstanceId(service, id))
                    {
                        hiddenDeviceIds.Remove(id);
                        unhiddenCount++;
                        changedDeviceIds.Add(id);
                    }
                }

                if (service.IsActive)
                {
                    service.IsActive = false;
                }

                cloakingEnabled = false;

                if (unhiddenCount > 0)
                {
                    Logger.Info($"HidHide suppression disabled via API for {unhiddenCount} previously hidden device path(s)");
                    RestartDevices(changedDeviceIds);
                }
                else
                {
                    Logger.Info("HidHide suppression disable requested; no hidden device path(s) were registered");
                }

                Logger.Info("HidHide suppression backend selected: API");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn($"HidHide API suppression disable failed: {ex.Message}. Falling back to CLI.");
                return false;
            }
        }

        private void DisableWithCli()
        {
            RefreshHiddenDeviceIdsFromCli();

            HashSet<string> changedDeviceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int unhiddenCount = 0;
            foreach (string id in hiddenDeviceIds.ToArray())
            {
                if (RunCli($"--dev-unhide \"{id}\""))
                {
                    hiddenDeviceIds.Remove(id);
                    unhiddenCount++;
                    changedDeviceIds.Add(id);
                }
            }

            if (RunCli("--cloak-off"))
            {
                cloakingEnabled = false;
            }

            if (unhiddenCount > 0)
            {
                Logger.Info($"HidHide suppression disabled for {unhiddenCount} previously hidden device path(s)");
                RestartDevices(changedDeviceIds);
            }
            else
            {
                Logger.Info("HidHide suppression disable requested; no hidden device path(s) were registered");
            }
        }

        private void RestartDevices(IEnumerable<string> deviceInstanceIds)
        {
            if (deviceInstanceIds == null)
            {
                return;
            }

            int successCount = 0;
            int cyclePortSuccessCount = 0;
            int pnputilSuccessCount = 0;
            int failureCount = 0;
            int totalCount = 0;
            foreach (string deviceInstanceId in deviceInstanceIds.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(deviceInstanceId))
                {
                    continue;
                }

                if (!ShouldRestartDevice(deviceInstanceId))
                {
                    continue;
                }

                totalCount++;
                string method = "none";
                bool succeeded = false;
                if (TryCyclePort(deviceInstanceId))
                {
                    succeeded = true;
                    method = "cycle-port";
                    cyclePortSuccessCount++;
                }
                else if (TryRestartDevice(deviceInstanceId))
                {
                    succeeded = true;
                    method = "pnputil";
                    pnputilSuccessCount++;
                }
                else
                {
                    failureCount++;
                    method = "failed";
                }

                Logger.Info($"HidHide re-enumeration device result: deviceId='{deviceInstanceId}', method={method}, success={succeeded}");
                if (succeeded)
                {
                    successCount++;
                }
            }

            if (totalCount == 0)
            {
                return;
            }

            if (successCount > 0)
            {
                Logger.Info($"HidHide re-enumeration completed for {successCount}/{totalCount} device(s) [cycle-port={cyclePortSuccessCount}, pnputil={pnputilSuccessCount}, failed={failureCount}]");
            }
            else
            {
                Logger.Warn($"HidHide re-enumeration failed for all {totalCount} device(s). Device visibility may require manual reconnect/restart.");
            }
        }

        private static bool ShouldRestartDevice(string deviceInstanceId)
        {
            if (string.IsNullOrWhiteSpace(deviceInstanceId))
            {
                return false;
            }

            string normalized = deviceInstanceId.Trim();
            if (!normalized.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase) &&
                !normalized.StartsWith("HID\\", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Keep restart scope narrow to avoid churning Legion HID collections.
            // Restarting IG_* interfaces has shown unstable behavior (including devices
            // ending in disabled state on some systems) while MI_00 is the meaningful
            // XInput interface to refresh for stock-controller visibility changes.
            return normalized.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase) &&
                   normalized.IndexOf("&MI_00\\", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private bool TryCyclePort(string deviceInstanceId)
        {
            if (string.IsNullOrWhiteSpace(deviceInstanceId))
            {
                return false;
            }

            try
            {
                PnPDevice device = PnPDevice.GetDeviceByInstanceId(deviceInstanceId, DeviceLocationFlags.Normal);
                if (device == null)
                {
                    return false;
                }

                PnPDevice usbDevice = ResolveUsbParentDevice(device);
                if (usbDevice == null)
                {
                    return false;
                }

                UsbPnPDevice usbPnPDevice = usbDevice.ToUsbPnPDevice();
                if (usbPnPDevice == null)
                {
                    return false;
                }

                usbPnPDevice.CyclePort();
                return true;
            }
            catch (Exception ex)
            {
                Logger.Debug($"USB cycle-port failed for '{deviceInstanceId}': {ex.Message}");
                return false;
            }
        }

        private static PnPDevice ResolveUsbParentDevice(PnPDevice device)
        {
            PnPDevice current = device;
            for (int depth = 0; depth < 4 && current != null; depth++)
            {
                string enumerator = current.GetProperty<string>(DevicePropertyKey.Device_EnumeratorName);
                if (string.Equals(enumerator, "USB", StringComparison.OrdinalIgnoreCase))
                {
                    return current;
                }

                string parentId = current.GetProperty<string>(DevicePropertyKey.Device_Parent);
                if (string.IsNullOrWhiteSpace(parentId))
                {
                    return null;
                }

                current = PnPDevice.GetDeviceByInstanceId(parentId, DeviceLocationFlags.Normal);
            }

            return null;
        }

        private bool TryRestartDevice(string deviceInstanceId)
        {
            if (string.IsNullOrWhiteSpace(deviceInstanceId))
            {
                return false;
            }

            // Use restart-device only; avoid disable/enable fallback because a failed re-enable
            // can leave the stock controller disabled in Device Manager.
            bool launched = RunPnpUtil($"/restart-device \"{deviceInstanceId}\"", out int restartExitCode, out string restartStdErr);
            if (launched && restartExitCode == 0)
            {
                return true;
            }

            Logger.Debug($"PnP restart-device failed for '{deviceInstanceId}' (exit={restartExitCode}): {restartStdErr}");
            return false;
        }

        private static bool RunPnpUtil(string arguments, out int exitCode, out string stdErr)
        {
            exitCode = -1;
            stdErr = string.Empty;

            try
            {
                using Process process = new Process();
                process.StartInfo.FileName = "pnputil.exe";
                process.StartInfo.Arguments = arguments;
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.CreateNoWindow = true;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = true;

                if (!process.Start())
                {
                    stdErr = "failed to start";
                    return false;
                }

                if (!process.WaitForExit(DeviceRestartTimeoutMs))
                {
                    try { process.Kill(); } catch { }
                    stdErr = "timeout";
                    return false;
                }

                exitCode = process.ExitCode;
                stdErr = process.StandardError.ReadToEnd() ?? string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                stdErr = ex.Message;
                return false;
            }
        }

        private bool TryGetApiService(out IHidHideControlService service, bool logMissing)
        {
            service = null;

            try
            {
                HidHideControlService controlService = new HidHideControlService();
                if (!controlService.IsInstalled)
                {
                    if (logMissing && !apiMissingLogged)
                    {
                        Logger.Warn("HidHide API unavailable (driver not installed). Falling back to CLI.");
                        apiMissingLogged = true;
                    }

                    return false;
                }

                apiMissingLogged = false;
                service = controlService;
                return true;
            }
            catch (Exception ex)
            {
                if (logMissing && !apiMissingLogged)
                {
                    Logger.Warn($"HidHide API unavailable ({ex.Message}). Falling back to CLI.");
                    apiMissingLogged = true;
                }

                return false;
            }
        }

        private bool EnsureCliResolved(bool logMissing)
        {
            if (!string.IsNullOrEmpty(cliPath) && File.Exists(cliPath))
            {
                cliMissingLogged = false;
                return true;
            }

            string resolvedPath = ResolveCliPath();
            if (!string.IsNullOrEmpty(resolvedPath) && File.Exists(resolvedPath))
            {
                if (!string.Equals(cliPath, resolvedPath, StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Info($"HidHide CLI detected at '{resolvedPath}'");
                }

                cliPath = resolvedPath;
                appRegistered = false;
                cliMissingLogged = false;
                return true;
            }

            cliPath = null;
            if (logMissing && !cliMissingLogged)
            {
                Logger.Warn("HidHide CLI not found. Install HidHide and ensure HidHideCLI.exe is available to enable physical controller suppression.");
                cliMissingLogged = true;
            }

            return false;
        }

        private static string ResolveCliPath()
        {
            List<string> candidates = new List<string>();

            AddCandidateFromValue(candidates, Environment.GetEnvironmentVariable("HIDHIDE_CLI"));
            AddRegistryCandidates(candidates, RegistryView.Registry64);
            AddRegistryCandidates(candidates, RegistryView.Registry32);
            AddPathEnvironmentCandidates(candidates);
            AddKnownInstallCandidates(candidates);
            AddVendorDirectoryCandidates(candidates, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
            AddVendorDirectoryCandidates(candidates, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));

            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        private static void AddRegistryCandidates(List<string> candidates, RegistryView view)
        {
            try
            {
                using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                foreach (string keyPath in new[]
                {
                    @"SOFTWARE\Nefarius Software Solutions e.U.\HidHide",
                    @"SOFTWARE\Nefarius Software Solutions\HidHide",
                    @"SOFTWARE\HidHide"
                })
                {
                    using RegistryKey key = baseKey.OpenSubKey(keyPath);
                    if (key == null)
                    {
                        continue;
                    }

                    AddCandidateFromValue(candidates, key.GetValue(string.Empty) as string);
                    AddCandidateFromValue(candidates, key.GetValue("Path") as string);
                    AddCandidateFromValue(candidates, key.GetValue("InstallPath") as string);
                    AddCandidateFromValue(candidates, key.GetValue("InstallDir") as string);
                    AddCandidateFromValue(candidates, key.GetValue("InstallLocation") as string);
                    AddCandidateFromValue(candidates, key.GetValue("Location") as string);
                }

                using RegistryKey appPaths = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\HidHideCLI.exe");
                if (appPaths != null)
                {
                    AddCandidateFromValue(candidates, appPaths.GetValue(string.Empty) as string);
                    AddCandidateFromValue(candidates, appPaths.GetValue("Path") as string);
                }
            }
            catch
            {
                // Best-effort only.
            }
        }

        private static void AddPathEnvironmentCandidates(List<string> candidates)
        {
            try
            {
                string path = Environment.GetEnvironmentVariable("PATH");
                if (string.IsNullOrWhiteSpace(path))
                {
                    return;
                }

                foreach (string segment in path.Split(';'))
                {
                    if (string.IsNullOrWhiteSpace(segment))
                    {
                        continue;
                    }

                    string expanded = Environment.ExpandEnvironmentVariables(segment.Trim().Trim('"'));
                    if (!Path.IsPathRooted(expanded))
                    {
                        continue;
                    }

                    AddCandidate(candidates, Path.Combine(expanded, "HidHideCLI.exe"));
                }
            }
            catch
            {
                // Best-effort only.
            }
        }

        private static void AddKnownInstallCandidates(List<string> candidates)
        {
            foreach (string root in new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            })
            {
                if (string.IsNullOrWhiteSpace(root))
                {
                    continue;
                }

                foreach (string vendorFolder in new[]
                {
                    "Nefarius Software Solutions e.U.",
                    "Nefarius Software Solutions",
                    "HidHide"
                })
                {
                    AddInstallLayoutCandidates(candidates, Path.Combine(root, vendorFolder, "HidHide"));
                    AddInstallLayoutCandidates(candidates, Path.Combine(root, vendorFolder));
                }

                AddInstallLayoutCandidates(candidates, Path.Combine(root, "HidHide"));
            }
        }

        private static void AddVendorDirectoryCandidates(List<string> candidates, string root)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                return;
            }

            try
            {
                foreach (string vendorDir in Directory.GetDirectories(root, "Nefarius*"))
                {
                    AddInstallLayoutCandidates(candidates, Path.Combine(vendorDir, "HidHide"));
                    AddInstallLayoutCandidates(candidates, vendorDir);
                }
            }
            catch
            {
                // Best-effort only.
            }
        }

        private static void AddCandidateFromValue(List<string> candidates, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            string normalized = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return;
            }

            if (normalized.IndexOf(';') >= 0)
            {
                foreach (string part in normalized.Split(';'))
                {
                    AddCandidateFromValue(candidates, part);
                }

                return;
            }

            if (normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                AddCandidate(candidates, normalized);
                return;
            }

            AddInstallLayoutCandidates(candidates, normalized);
        }

        private static void AddInstallLayoutCandidates(List<string> candidates, string installRoot)
        {
            if (string.IsNullOrWhiteSpace(installRoot))
            {
                return;
            }

            AddCandidate(candidates, Path.Combine(installRoot, "HidHideCLI.exe"));
            AddCandidate(candidates, Path.Combine(installRoot, "x64", "HidHideCLI.exe"));
            AddCandidate(candidates, Path.Combine(installRoot, "x86", "HidHideCLI.exe"));
            AddCandidate(candidates, Path.Combine(installRoot, "CLI", "HidHideCLI.exe"));
        }

        private static void AddCandidate(List<string> candidates, string candidatePath)
        {
            if (string.IsNullOrWhiteSpace(candidatePath))
            {
                return;
            }

            string normalized = candidatePath.Trim().Trim('"');
            if (!Path.IsPathRooted(normalized))
            {
                return;
            }

            if (!candidates.Any(path => string.Equals(path, normalized, StringComparison.OrdinalIgnoreCase)))
            {
                candidates.Add(normalized);
            }
        }

        private void EnsureApplicationRegistered(IHidHideControlService service)
        {
            // Diagnostic: silent early-return here was costing us a round-trip to vvalente30
            // when his logs showed suppression succeeding but registration never logged.
            // Always say which branch fired so future "no input after toggle" reports
            // can be triaged from a single helper log.
            if (appRegistered || service == null)
            {
                Logger.Debug($"EnsureApplicationRegistered(API) skipped: appRegistered={appRegistered}, serviceNull={service == null}");
                return;
            }

            IReadOnlyCollection<string> desiredAppPaths;
            try
            {
                desiredAppPaths = EnumerateAllowedApplicationPaths();
            }
            catch (Exception ex)
            {
                Logger.Warn($"HidHide app path enumeration failed: {ex.Message}");
                return;
            }

            if (desiredAppPaths.Count == 0)
            {
                // If this branch ever fires we have no helper exe path to register, which means
                // HidHide will hide the controller from the helper itself once suppression engages.
                // Surface it loudly so users with this state get a clear log signal instead of
                // the previous "it just stops working" mystery.
                string helperProbe;
                try { helperProbe = Process.GetCurrentProcess().MainModule?.FileName ?? "<MainModule.FileName=null>"; }
                catch (Exception ex) { helperProbe = $"<MainModule access failed: {ex.GetType().Name}: {ex.Message}>"; }
                Logger.Warn($"HidHide application registration skipped: 0 paths to register (helperPath probe: {helperProbe}). Controller will be invisible to helper after suppression engages.");
                return;
            }

            HashSet<string> registered;
            try
            {
                registered = new HashSet<string>(
                    service.ApplicationPaths ?? Array.Empty<string>(),
                    StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Logger.Warn($"HidHide app path query failed: {ex.Message}");
                return;
            }

            // Register each path in its own try so one bad entry (e.g. Gaming App
            // exe not installed, GameInputService path missing) can't abort the rest.
            // Previously a single PathNotFound threw out of the loop with the helper
            // still unregistered, leading to "Controller not connected" once VIIPER
            // enabled HidHide suppression (helper saw the hidden device too).
            int addedCount = 0;
            int failedCount = 0;
            foreach (string appPath in desiredAppPaths)
            {
                if (registered.Contains(appPath)) continue;

                try
                {
                    service.AddApplicationPath(appPath);
                    registered.Add(appPath);
                    addedCount++;
                }
                catch (Exception ex) when (
                    ex is System.IO.FileNotFoundException
                    || ex is System.IO.DirectoryNotFoundException
                    || (ex is ArgumentException && ex.Message.IndexOf("doesn't exist", StringComparison.OrdinalIgnoreCase) >= 0)
                    || (ex is ArgumentException && ex.Message.IndexOf("does not exist", StringComparison.OrdinalIgnoreCase) >= 0)
                    || !File.Exists(appPath))
                {
                    // Expected on systems without optional third-party apps (Lenovo Gaming App,
                    // GameInputService, etc.) — keep at Debug so non-Lenovo users don't see a
                    // wall of warnings every helper boot. We catch FileNotFound and the
                    // HidHide-API-throws-ArgumentException-with-"doesn't exist"-message variant,
                    // and as a final guard fall through to Debug for any exception when the
                    // path itself doesn't exist on disk.
                    failedCount++;
                    Logger.Debug($"HidHide AddApplicationPath skipped (path missing) for '{appPath}': {ex.GetType().Name}: {ex.Message}");
                }
                catch (Exception ex)
                {
                    // Real failures (permission issues, HidHide service errors, etc.) stay at
                    // Warn so we can diagnose them in production logs.
                    failedCount++;
                    Logger.Warn($"HidHide AddApplicationPath failed for '{appPath}': {ex.GetType().Name}: {ex.Message}");
                }
            }

            // Treat as registered once the helper's own path made it in, even if some
            // optional third-party paths failed. Re-entering this method is cheap since
            // the registered-set check above skips already-added entries.
            appRegistered = true;

            // Confirm the helper's own exe path is on the allowlist after this pass.
            // Without this, "added 0 failed 0" looks identical whether the helper is
            // present or absent — we want to see the steady-state in logs so VIIPER /
            // controller-emulation reports can be triaged without re-running the user.
            string helperPath = Process.GetCurrentProcess().MainModule?.FileName;
            bool helperOnAllowlist = !string.IsNullOrWhiteSpace(helperPath) && registered.Contains(helperPath);
            if (addedCount > 0 || failedCount > 0)
            {
                Logger.Info($"HidHide application registration via API: added {addedCount}, failed {failedCount}, helperAllowlisted={helperOnAllowlist}, totalAllowed={registered.Count}");
            }
            else
            {
                Logger.Info($"HidHide application allowlist already current: helperAllowlisted={helperOnAllowlist}, totalAllowed={registered.Count}");
            }
        }

        private void EnsureApplicationRegistered()
        {
            if (appRegistered || string.IsNullOrEmpty(cliPath))
            {
                Logger.Debug($"EnsureApplicationRegistered(CLI) skipped: appRegistered={appRegistered}, cliPathEmpty={string.IsNullOrEmpty(cliPath)}");
                return;
            }

            try
            {
                IReadOnlyCollection<string> desiredAppPaths = EnumerateAllowedApplicationPaths();
                if (desiredAppPaths.Count == 0)
                {
                    string helperProbe;
                    try { helperProbe = Process.GetCurrentProcess().MainModule?.FileName ?? "<MainModule.FileName=null>"; }
                    catch (Exception ex) { helperProbe = $"<MainModule access failed: {ex.GetType().Name}: {ex.Message}>"; }
                    Logger.Warn($"HidHide CLI app registration skipped: 0 paths to register (helperPath probe: {helperProbe})");
                    return;
                }

                HashSet<string> registered = QueryRegisteredApplicationPathsFromCli();
                int addedCount = 0;
                foreach (string appPath in desiredAppPaths)
                {
                    if (registered.Contains(appPath))
                    {
                        continue;
                    }

                    if (!RunCli($"--app-reg \"{appPath}\""))
                    {
                        Logger.Warn($"HidHide app registration failed for '{appPath}'");
                        continue;
                    }

                    registered.Add(appPath);
                    addedCount++;
                }

                appRegistered = true;
                if (addedCount > 0)
                {
                    Logger.Info($"HidHide application registration completed via CLI (added {addedCount} app path(s))");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"HidHide app registration failed: {ex.Message}");
            }
        }

        private static IReadOnlyCollection<string> EnumerateAllowedApplicationPaths()
        {
            HashSet<string> appPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string helperPath = Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(helperPath))
            {
                appPaths.Add(helperPath.Trim());
            }

            foreach (string gameBarPath in EnumeratePackageExecutablePaths(
                XboxGamingOverlayPackageName,
                XboxGamingOverlayPackageFamilySuffix,
                XboxGameBarExecutableNames))
            {
                appPaths.Add(gameBarPath);
            }

            foreach (string gamingAppPath in EnumeratePackageExecutablePaths(
                XboxGamingAppPackageName,
                XboxGamingAppPackageFamilySuffix,
                XboxGamingAppExecutableNames))
            {
                appPaths.Add(gamingAppPath);
            }

            foreach (string gameInputPath in EnumerateGameInputServiceExecutablePaths())
            {
                appPaths.Add(gameInputPath);
            }

            foreach (string brokerPath in EnumerateSystemInputBrokerPaths())
            {
                appPaths.Add(brokerPath);
            }

            foreach (string runtimeBrokerPath in EnumerateRuntimeInputBrokerProcessPaths())
            {
                appPaths.Add(runtimeBrokerPath);
            }

            return appPaths.ToArray();
        }

        private static IEnumerable<string> EnumerateSystemInputBrokerPaths()
        {
            foreach (string candidate in SystemInputBrokerPaths)
            {
                if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                {
                    yield return candidate;
                }
            }
        }

        private static IEnumerable<string> EnumerateRuntimeInputBrokerProcessPaths()
        {
            HashSet<string> results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string processName in RuntimeInputBrokerProcessNames)
            {
                if (string.IsNullOrWhiteSpace(processName))
                {
                    continue;
                }

                try
                {
                    foreach (Process process in Process.GetProcessesByName(processName))
                    {
                        try
                        {
                            string path = process.MainModule?.FileName;
                            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                            {
                                results.Add(path);
                            }
                        }
                        catch
                        {
                            // Best effort only.
                        }
                        finally
                        {
                            process.Dispose();
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Debug($"HidHide runtime input broker probe failed ({processName}): {ex.Message}");
                }
            }

            foreach (string path in results)
            {
                yield return path;
            }
        }

        private static IEnumerable<string> EnumerateGameInputServiceExecutablePaths()
        {
            HashSet<string> paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Query installed service definitions first (most reliable for active path).
            foreach (string serviceName in GameInputServiceNames)
            {
                try
                {
                    using ManagementObjectSearcher searcher =
                        new ManagementObjectSearcher($"SELECT PathName FROM Win32_Service WHERE Name='{serviceName}'");
                    foreach (ManagementObject service in searcher.Get().Cast<ManagementObject>())
                    {
                        string rawPath = service["PathName"] as string;
                        string executablePath = NormalizeServiceExecutablePath(rawPath);
                        if (!string.IsNullOrWhiteSpace(executablePath) && File.Exists(executablePath))
                        {
                            paths.Add(executablePath);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Debug($"HidHide GameInput service path probe failed ({serviceName}): {ex.Message}");
                }
            }

            // Known install fallbacks.
            foreach (string candidate in GameInputKnownExecutablePaths)
            {
                if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                {
                    paths.Add(candidate);
                }
            }

            return paths;
        }

        private static string NormalizeServiceExecutablePath(string servicePath)
        {
            if (string.IsNullOrWhiteSpace(servicePath))
            {
                return null;
            }

            string value = Environment.ExpandEnvironmentVariables(servicePath.Trim());
            if (value.StartsWith("\"", StringComparison.Ordinal))
            {
                int endQuote = value.IndexOf('"', 1);
                if (endQuote > 1)
                {
                    return value.Substring(1, endQuote - 1);
                }
            }

            int firstSpace = value.IndexOf(' ');
            return firstSpace > 0 ? value.Substring(0, firstSpace) : value;
        }

        private static IEnumerable<string> EnumeratePackageExecutablePaths(
            string packageName,
            string packageFamilySuffix,
            IReadOnlyCollection<string> executableNames)
        {
            HashSet<string> installRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string appxInstallRoot = QueryPackageInstallLocationFromAppx(packageName);
            if (!string.IsNullOrWhiteSpace(appxInstallRoot) && Directory.Exists(appxInstallRoot))
            {
                installRoots.Add(appxInstallRoot);
            }

            try
            {
                string windowsAppsPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "WindowsApps");

                if (Directory.Exists(windowsAppsPath))
                {
                    foreach (string directory in Directory.EnumerateDirectories(
                        windowsAppsPath,
                        $"{packageName}_*{packageFamilySuffix}"))
                    {
                        installRoots.Add(directory);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"HidHide package WindowsApps probe failed ({packageName}): {ex.Message}");
            }

            foreach (string installRoot in installRoots)
            {
                foreach (string exeName in executableNames)
                {
                    string exePath = Path.Combine(installRoot, exeName);
                    if (File.Exists(exePath))
                    {
                        yield return exePath;
                    }
                }
            }
        }

        private static string QueryPackageInstallLocationFromAppx(string packageName)
        {
            try
            {
                string command =
                    $"(Get-AppxPackage -Name {packageName} -ErrorAction SilentlyContinue | Sort-Object Version -Descending | Select-Object -First 1 -ExpandProperty InstallLocation)";

                using Process process = new Process();
                process.StartInfo.FileName = "powershell.exe";
                process.StartInfo.Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{command}\"";
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.CreateNoWindow = true;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = true;

                if (!process.Start())
                {
                    return null;
                }

                if (!process.WaitForExit(3000))
                {
                    try { process.Kill(); } catch { }
                    return null;
                }

                if (process.ExitCode != 0)
                {
                    return null;
                }

                string output = process.StandardOutput.ReadToEnd();
                if (string.IsNullOrWhiteSpace(output))
                {
                    return null;
                }

                string installPath = output
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.Trim().Trim('"'))
                    .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));

                return string.IsNullOrWhiteSpace(installPath) ? null : installPath;
            }
            catch (Exception ex)
            {
                Logger.Debug($"HidHide package Appx query failed ({packageName}): {ex.Message}");
                return null;
            }
        }

        private HashSet<string> QueryRegisteredApplicationPathsFromCli()
        {
            HashSet<string> appPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!TryQueryCliOutput("--app-list", out string output) || string.IsNullOrWhiteSpace(output))
            {
                return appPaths;
            }

            foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                Match match = HidHideAppRegistrationRegex.Match(line);
                if (!match.Success)
                {
                    continue;
                }

                string path = match.Groups[1].Value?.Trim();
                if (!string.IsNullOrWhiteSpace(path))
                {
                    appPaths.Add(path);
                }
            }

            return appPaths;
        }

        private static bool TryApiAddBlockedInstanceId(IHidHideControlService service, string instanceId)
        {
            if (service == null || string.IsNullOrWhiteSpace(instanceId))
            {
                return false;
            }

            try
            {
                service.AddBlockedInstanceId(instanceId);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Debug($"HidHide API failed to hide '{instanceId}': {ex.Message}");
                return false;
            }
        }

        private static bool TryApiRemoveBlockedInstanceId(IHidHideControlService service, string instanceId)
        {
            if (service == null || string.IsNullOrWhiteSpace(instanceId))
            {
                return false;
            }

            try
            {
                service.RemoveBlockedInstanceId(instanceId);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Debug($"HidHide API failed to unhide '{instanceId}': {ex.Message}");
                return false;
            }
        }

        private static IEnumerable<string> EnumerateDeviceInstanceIds(
            DeviceType deviceType,
            int hideTargetMode)
        {
            IEnumerable<string> nativeIds;
            switch (deviceType)
            {
                case DeviceType.LegionGo:
                case DeviceType.LegionGo2:
                    nativeIds = QueryPnpDeviceIds(0x17EF, 0x6182, 0x6183, 0x6184, 0x6185, 0x61EB, 0x61EC, 0x61ED, 0x61EE)
                        .Concat(QueryUsbDeviceIds(0x17EF, 0x6182, 0x6183, 0x6184, 0x6185, 0x61EB, 0x61EC, 0x61ED, 0x61EE));
                    break;
                case DeviceType.LegionGoS:
                    nativeIds = QueryPnpDeviceIds(0x1A86, 0xE310, 0xE311)
                        .Concat(QueryUsbDeviceIds(0x1A86, 0xE310, 0xE311));
                    break;

                case DeviceType.GPDWin5:
                    nativeIds = QueryPnpDeviceIds(0x2F24, 0x0137, 0x0135)
                        .Concat(QueryUsbDeviceIds(0x2F24, 0x0137, 0x0135));
                    break;

                default:
                    nativeIds = Enumerable.Empty<string>();
                    break;
            }

            nativeIds = FilterNativeDeviceInstanceIds(deviceType, nativeIds);
            IEnumerable<string> xboxBridgeIds = QueryXboxBridgeDeviceIds();

            switch (hideTargetMode)
            {
                case 1:
                    return nativeIds;
                case 2:
                    return xboxBridgeIds;
                case 3:
                    return nativeIds.Concat(xboxBridgeIds);
                default:
                    // Auto keeps legacy behavior to avoid hiding unrelated controllers by default.
                    return nativeIds;
            }
        }

        internal static IReadOnlyCollection<string> QueryXboxBridgeDeviceIds()
        {
            // Filter the raw 045E:028E enumeration down to ones parented by a Lenovo
            // (VID 17EF) USB device. Without this, hiding "all Xbox 360 bridge devices"
            // would also hide:
            //   • The user's real external Xbox 360 / One controller (parented by a USB hub)
            //   • VIIPER's virtual xbox360 pad (parented by usbip-win2 bus)
            //   • The legacy backend's ViGEm pad (parented by Virtual Gamepad Emulation Bus)
            // We only ever want to hide the Legion's *own* XInput child — the 045E:028E
            // device that xusb22.sys publishes underneath the Legion's USB endpoint.
            // Walking each candidate's PnP parent chain and matching VID_17EF identifies
            // it unambiguously, regardless of how the user has the controller attached.
            return QueryPnpDeviceIds(0x045E, 0x028E)
                .Concat(QueryUsbDeviceIds(0x045E, 0x028E))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(IsLegionParentedXboxBridge)
                .ToArray();
        }

        /// <summary>
        /// True when the given 045E:028E PnP instance has a Lenovo USB device (VID 17EF)
        /// somewhere in its parent chain. Conservative: returns false on any lookup
        /// failure so an enumeration glitch never accidentally widens the hide list.
        /// </summary>
        private static bool IsLegionParentedXboxBridge(string xboxBridgeInstanceId)
        {
            if (string.IsNullOrWhiteSpace(xboxBridgeInstanceId)) return false;

            try
            {
                PnPDevice current = PnPDevice.GetDeviceByInstanceId(xboxBridgeInstanceId, DeviceLocationFlags.Normal);
                // Walk up to ~6 levels — typical depth from a HID child to the USB hub
                // is 2-3 (HIDClass → USB composite child → USB device). 6 is plenty.
                for (int depth = 0; depth < 6 && current != null; depth++)
                {
                    string id = current.InstanceId;
                    if (!string.IsNullOrEmpty(id) &&
                        id.IndexOf("VID_17EF", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return true;
                    }
                    string parentId = current.GetProperty<string>(DevicePropertyKey.Device_Parent);
                    if (string.IsNullOrWhiteSpace(parentId)) return false;
                    current = PnPDevice.GetDeviceByInstanceId(parentId, DeviceLocationFlags.Normal);
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"IsLegionParentedXboxBridge({xboxBridgeInstanceId}) lookup failed: {ex.Message}");
            }
            return false;
        }

        private static IEnumerable<string> FilterNativeDeviceInstanceIds(
            DeviceType deviceType,
            IEnumerable<string> deviceInstanceIds)
        {
            if (deviceInstanceIds == null)
            {
                return Enumerable.Empty<string>();
            }

            // Hide only gamepad endpoints for Legion devices.
            // Avoid cloaking Legion control/report endpoints (MI_01/MI_02/MI_03 or composite root),
            // which can break helper probing and button monitor reads.
            if (deviceType == DeviceType.LegionGo || deviceType == DeviceType.LegionGo2)
            {
                List<string> filtered = deviceInstanceIds
                    .Where(IsLegionSuppressibleGamepadId)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (filtered.Count == 0)
                {
                    Logger.Warn("HidHide native target filter found no Legion gamepad interfaces; skipping native hide to avoid masking control endpoints.");
                    return Enumerable.Empty<string>();
                }

                return filtered;
            }

            if (deviceType == DeviceType.LegionGoS)
            {
                List<string> filtered = deviceInstanceIds
                    .Where(IsLegionGoSSuppressibleGamepadId)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (filtered.Count == 0)
                {
                    Logger.Warn("HidHide native target filter found no Legion Go S gamepad interfaces; skipping native hide to avoid masking non-gamepad endpoints.");
                    return Enumerable.Empty<string>();
                }

                return filtered;
            }

            return deviceInstanceIds.Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static bool IsLegionSuppressibleGamepadId(string deviceInstanceId)
        {
            if (string.IsNullOrWhiteSpace(deviceInstanceId))
            {
                return false;
            }

            string normalized = deviceInstanceId.Trim();
            if (normalized.IndexOf("VID_17EF", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }

            // USB MI_00 is the primary stock HID gamepad interface — hide it so
            // games / Steam reading raw HID don't see the Legion.
            //
            // USB &IG_xx interfaces are the parent USB endpoints that xinputhid.sys
            // builds the XInput / XUSB device-interface on top of. Earlier code
            // assumed XInput always read from the standard 045E:028E (Xbox 360)
            // node so hiding only the HID\IG_xx children was sufficient. In
            // practice, on Windows 11 25H2 with VIIPER's ViGEm Xbox-360 pad live
            // at slot 0, XInput slot 1 still latches onto the Legion's native
            // USB\IG_05 interface, so Game Bar reads BOTH pads and the Legion's
            // residual stick/trigger noise leaks in (visible as "RT mixed with
            // every button press" — tab moves right on every UI click). Hiding
            // the USB&IG_xx endpoint closes that channel.
            if (normalized.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase))
            {
                return normalized.IndexOf("&MI_00\\", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       normalized.IndexOf("&IG_", StringComparison.OrdinalIgnoreCase) >= 0;
            }

            // HID-class IG_xx children published by xinputhid.sys (HID joystick
            // views of the gamepad). These appear in joy.cpl as duplicate
            // "Legion Controller for Windows" entries even with USB MI_00
            // hidden, because Windows enumerates them as separate HID-class
            // nodes. Hide them too — VIIPER's ViGEm Xbox-360 pad keeps games on
            // a working XInput slot, so this is what gets joy.cpl clean.
            if (normalized.StartsWith("HID\\", StringComparison.OrdinalIgnoreCase))
            {
                return normalized.IndexOf("&IG_", StringComparison.OrdinalIgnoreCase) >= 0;
            }

            return false;
        }

        private static bool IsLegionGoSSuppressibleGamepadId(string deviceInstanceId)
        {
            if (string.IsNullOrWhiteSpace(deviceInstanceId))
            {
                return false;
            }

            string normalized = deviceInstanceId.Trim();
            if (normalized.IndexOf("VID_1A86", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }

            if (normalized.IndexOf("PID_E310", StringComparison.OrdinalIgnoreCase) < 0 &&
                normalized.IndexOf("PID_E311", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }

            // Restrict to the primary USB gamepad interface to avoid hiding touchpad/keyboard-like endpoints.
            if (normalized.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase))
            {
                return normalized.IndexOf("&MI_00\\", StringComparison.OrdinalIgnoreCase) >= 0;
            }

            return false;
        }

        private static IEnumerable<string> QueryPnpDeviceIds(int vendorId, params int[] productIds)
        {
            HashSet<string> results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (int pid in productIds)
            {
                string query = $"SELECT PNPDeviceID FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'HID\\\\VID_{vendorId:X4}&PID_{pid:X4}%'";
                try
                {
                    using ManagementObjectSearcher searcher = new ManagementObjectSearcher(query);
                    foreach (ManagementObject obj in searcher.Get().Cast<ManagementObject>())
                    {
                        string id = obj["PNPDeviceID"] as string;
                        if (!string.IsNullOrWhiteSpace(id))
                        {
                            results.Add(id);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Debug($"HidHide query failed for VID_{vendorId:X4}&PID_{pid:X4}: {ex.Message}");
                }
            }

            return results;
        }

        private static IEnumerable<string> QueryUsbDeviceIds(int vendorId, params int[] productIds)
        {
            HashSet<string> results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (int pid in productIds)
            {
                string query = $"SELECT PNPDeviceID FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'USB\\\\VID_{vendorId:X4}&PID_{pid:X4}%'";
                try
                {
                    using ManagementObjectSearcher searcher = new ManagementObjectSearcher(query);
                    foreach (ManagementObject obj in searcher.Get().Cast<ManagementObject>())
                    {
                        string id = obj["PNPDeviceID"] as string;
                        if (!string.IsNullOrWhiteSpace(id))
                        {
                            results.Add(id);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Debug($"HidHide USB query failed for VID_{vendorId:X4}&PID_{pid:X4}: {ex.Message}");
                }
            }

            return results;
        }

        private bool RunCli(string arguments)
        {
            if (!EnsureCliResolved(logMissing: true))
            {
                return false;
            }

            try
            {
                using Process process = new Process();
                process.StartInfo.FileName = cliPath;
                process.StartInfo.Arguments = arguments;
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.CreateNoWindow = true;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = true;

                if (!process.Start())
                {
                    Logger.Warn($"HidHide command failed to start: {arguments}");
                    return false;
                }

                if (!process.WaitForExit(3000))
                {
                    try { process.Kill(); } catch { }
                    Logger.Warn($"HidHide command timeout: {arguments}");
                    return false;
                }

                string stderr = process.StandardError.ReadToEnd();
                if (process.ExitCode != 0)
                {
                    Logger.Warn($"HidHide command failed (exit {process.ExitCode}): {arguments} {stderr}");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn($"HidHide command exception ({arguments}): {ex.Message}");
                return false;
            }
        }

        private void EnsureInverseApplicationListDisabled(IHidHideControlService service)
        {
            if (service == null)
            {
                return;
            }

            try
            {
                if (!service.IsAppListInverted)
                {
                    return;
                }

                service.IsAppListInverted = false;
                Logger.Info("HidHide inverse application list disabled (required for helper visibility of hidden controllers)");
            }
            catch (Exception ex)
            {
                Logger.Warn($"HidHide inverse application list check via API failed: {ex.Message}");
            }
        }

        private void EnsureInverseApplicationListDisabled()
        {
            if (!EnsureCliResolved(logMissing: true))
            {
                return;
            }

            if (!TryQueryCliOutput("--inv-state", out string output))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(output) ||
                output.IndexOf("--inv-on", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return;
            }

            if (RunCli("--inv-off"))
            {
                Logger.Info("HidHide inverse application list disabled (required for helper visibility of hidden controllers)");
            }
            else
            {
                Logger.Warn("HidHide inverse application list appears enabled and could block helper input visibility");
            }
        }

        private bool TryQueryCliOutput(string arguments, out string output)
        {
            output = string.Empty;
            if (!EnsureCliResolved(logMissing: true))
            {
                return false;
            }

            try
            {
                using Process process = new Process();
                process.StartInfo.FileName = cliPath;
                process.StartInfo.Arguments = arguments;
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.CreateNoWindow = true;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = true;

                if (!process.Start())
                {
                    return false;
                }

                if (!process.WaitForExit(3000))
                {
                    try { process.Kill(); } catch { }
                    return false;
                }

                if (process.ExitCode != 0)
                {
                    return false;
                }

                output = process.StandardOutput.ReadToEnd() ?? string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                Logger.Debug($"HidHide query command exception ({arguments}): {ex.Message}");
                return false;
            }
        }

        private void RefreshHiddenDeviceIdsFromApi(IHidHideControlService service)
        {
            if (service == null)
            {
                return;
            }

            try
            {
                HashSet<string> currentHidden = new HashSet<string>(
                    service.BlockedInstanceIds ?? Enumerable.Empty<string>(),
                    StringComparer.OrdinalIgnoreCase);

                hiddenDeviceIds.Clear();
                foreach (string id in currentHidden)
                {
                    hiddenDeviceIds.Add(id);
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"HidHide hidden-device API refresh failed: {ex.Message}");
            }
        }

        private void RefreshHiddenDeviceIdsFromCli()
        {
            if (!EnsureCliResolved(logMissing: true))
            {
                return;
            }

            try
            {
                HashSet<string> currentHidden = QueryHiddenDeviceIdsFromCli();
                hiddenDeviceIds.Clear();
                foreach (string id in currentHidden)
                {
                    hiddenDeviceIds.Add(id);
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"HidHide hidden-device refresh failed: {ex.Message}");
            }
        }

        private HashSet<string> QueryHiddenDeviceIdsFromCli()
        {
            HashSet<string> hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            using Process process = new Process();
            process.StartInfo.FileName = cliPath;
            process.StartInfo.Arguments = "--dev-list";
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;

            if (!process.Start())
            {
                return hidden;
            }

            if (!process.WaitForExit(3000))
            {
                try { process.Kill(); } catch { }
                return hidden;
            }

            if (process.ExitCode != 0)
            {
                return hidden;
            }

            string output = process.StandardOutput.ReadToEnd();
            if (string.IsNullOrWhiteSpace(output))
            {
                return hidden;
            }

            MatchCollection matches = Regex.Matches(output, "--dev-hide\\s+\"([^\"]+)\"");
            foreach (Match match in matches)
            {
                if (match.Groups.Count < 2)
                {
                    continue;
                }

                string id = match.Groups[1].Value;
                if (!string.IsNullOrWhiteSpace(id))
                {
                    hidden.Add(id);
                }
            }

            return hidden;
        }

        public void Dispose()
        {
            Disable();
        }
    }
}
