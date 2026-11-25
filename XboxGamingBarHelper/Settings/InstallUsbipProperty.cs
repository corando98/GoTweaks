using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using NLog;
using Shared.Enums;
using XboxGamingBarHelper.Core;

namespace XboxGamingBarHelper.Settings
{
    /// <summary>
    /// One-click usbip-win2 installer. Write "install" to trigger.
    ///
    /// usbip-win2 is the VIIPER backend's bus driver — the only prerequisite a
    /// user must install by hand now that VIIPER is the default backend. The
    /// upstream installer is InnoSetup, so a silent elevated run is
    /// "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART". The ude driver may need a
    /// reboot before the virtual bus works; UsbipInstalledProperty.Refresh()
    /// runs after install so the widget's prereq line updates immediately
    /// either way.
    /// </summary>
    internal class InstallUsbipProperty : HelperProperty<string, SettingsManager>
    {
        private new static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        // Pinned release. Bump deliberately — driver installers are not a place for "latest".
        // 0.9.7.8 (previous pin) has a usbip2_filter bug that corrupts kernel pool and BSODs
        // (upstream issues #180/#181; its release notes now carry a BSOD warning). The filter
        // attaches to EVERY USB 3 root hub, so it crashes even with no virtual pad attached
        // (field: 0x7E in usbip2_filter+0x2441 at restart, 2026-11-25). Fixed in 0.9.8.0;
        // 0.9.8.1 is a large AI-reviewed refactor the maintainer flags as regression-prone.
        internal static readonly Version RecommendedVersion = new Version(0, 9, 8, 0);
        private const string UsbipDownloadUrl =
            "https://github.com/vadimgrn/usbip-win2/releases/download/v.0.9.8.0/USBip-0.9.8.0-x64.exe";
        // SHA-256 of the published asset (GitHub release v.0.9.8.0, uploaded 2026-09-07).
        private const string UsbipInstallerSha256 =
            "81f426741f7ee2ed991febe24a22daca8400b6ae2f171054e3fb404897e15d39";

        /// <summary>
        /// Version of the installed usbip-win2 (from usbip.exe), or null if not installed /
        /// unreadable.
        /// </summary>
        internal static Version GetInstalledVersion()
        {
            try
            {
                string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "USBip", "usbip.exe");
                if (!File.Exists(exe)) return null;
                string v = FileVersionInfo.GetVersionInfo(exe).ProductVersion ?? FileVersionInfo.GetVersionInfo(exe).FileVersion;
                return Version.TryParse((v ?? "").Trim(), out var parsed) ? parsed : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>True when an installed usbip-win2 predates the BSOD fix.</summary>
        internal static bool IsInstalledVersionKnownBad()
        {
            var v = GetInstalledVersion();
            return v != null && v < RecommendedVersion;
        }

        public InstallUsbipProperty(SettingsManager inManager)
            : base("", null, Function.Viiper_InstallUsbip, inManager)
        {
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);

            if (Value?.ToLowerInvariant() == "install")
            {
                Logger.Info("usbip-win2 installation requested from widget");
                Task.Run(() => InstallUsbip());
                SetValue("");
            }
        }

        private void InstallUsbip()
        {
            string tempPath = Path.Combine(Path.GetTempPath(), "USBip_setup.exe");

            try
            {
                Logger.Info($"Downloading usbip-win2 installer from {UsbipDownloadUrl}...");
                using (var client = new TimedWebClient(TimeSpan.FromSeconds(60)))
                {
                    client.Headers.Add("User-Agent", "GoTweaks/1.0");
                    client.DownloadFile(UsbipDownloadUrl, tempPath);
                }
                Logger.Info($"usbip-win2 installer downloaded to {tempPath}");

                string actualHash;
                using (var sha = System.Security.Cryptography.SHA256.Create())
                using (var stream = File.OpenRead(tempPath))
                {
                    actualHash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                }
                if (!string.Equals(actualHash, UsbipInstallerSha256, StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Error($"usbip-win2 installer hash mismatch (got {actualHash}, expected {UsbipInstallerSha256}); not running it");
                    return;
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = tempPath,
                    Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
                    UseShellExecute = true,  // required for Verb = "runas"
                    Verb = "runas"
                };

                Logger.Info("Launching usbip-win2 installer silently...");
                using (var process = Process.Start(startInfo))
                {
                    if (process == null)
                    {
                        Logger.Warn("Failed to start usbip-win2 installer (UAC may have been cancelled)");
                        return;
                    }

                    if (process.WaitForExit(180000))
                    {
                        Logger.Info($"usbip-win2 installation completed with exit code: {process.ExitCode}");
                    }
                    else
                    {
                        Logger.Warn("usbip-win2 installation timed out after 3 minutes");
                        try { process.Kill(); } catch { }
                    }
                }
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                Logger.Info("usbip-win2 installation cancelled by user (UAC prompt declined)");
            }
            catch (WebException ex)
            {
                Logger.Error($"Failed to download usbip-win2 installer: {ex.Message}");
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to install usbip-win2: {ex.Message}");
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath)) File.Delete(tempPath);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"Failed to cleanup usbip installer temp file: {ex.Message}");
                }

                // Update the prereq line + setup banner. A reboot may still be
                // required before the virtual bus actually enumerates; detection
                // is registry/file based so it flips as soon as install lands.
                try { Manager?.UsbipInstalled?.Refresh(); } catch { }
                try { Program.SendSetupWarningsToWidget(); } catch { }
            }
        }
    }
}
