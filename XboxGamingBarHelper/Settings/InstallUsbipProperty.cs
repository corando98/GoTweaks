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

        // Pinned release (matches the version verified against libviiper).
        // Bump deliberately — driver installers are not a place for "latest".
        private const string UsbipDownloadUrl =
            "https://github.com/vadimgrn/usbip-win2/releases/download/v.0.9.7.8/USBip-0.9.7.8-x64.exe";

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
