using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using NLog;
using Shared.Enums;
using XboxGamingBarHelper.Core;

namespace XboxGamingBarHelper.Performance
{
    /// <summary>
    /// Property to trigger PawnIO installation.
    /// Write "install" to trigger the installation.
    /// Downloads the installer from GitHub and runs with -install -silent.
    /// </summary>
    internal class InstallPawnIOProperty : HelperProperty<string, PerformanceManager>
    {
        private new static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private const string PawnIODownloadUrl = "https://github.com/namazso/PawnIO.Setup/releases/latest/download/PawnIO_setup.exe";

        public InstallPawnIOProperty(PerformanceManager inManager)
            : base("", null, Function.TdpMethod_InstallPawnIO, inManager)
        {
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);

            if (Value?.ToLowerInvariant() == "install")
            {
                Logger.Info("PawnIO installation requested from widget");
                // Run installation asynchronously to avoid blocking
                Task.Run(() => InstallPawnIO());
                // Reset the value
                SetValue("");
            }
        }

        /// <summary>
        /// Downloads and installs PawnIO with silent install and admin elevation.
        /// </summary>
        private void InstallPawnIO()
        {
            string tempPath = Path.Combine(Path.GetTempPath(), "PawnIO_setup.exe");

            try
            {
                // Step 1: Download the installer
                Logger.Info($"Downloading PawnIO installer from {PawnIODownloadUrl}...");

                // TimedWebClient (60s): stock WebClient has no timeout, so a
                // firewall that drops outbound (issue #91) made this hang forever
                // in the background — the widget's Install button looked dead.
                using (var client = new TimedWebClient(TimeSpan.FromSeconds(60)))
                {
                    // Add user agent to avoid potential blocks
                    client.Headers.Add("User-Agent", "GoTweaks/1.0");
                    client.DownloadFile(PawnIODownloadUrl, tempPath);
                }

                Logger.Info($"PawnIO installer downloaded to {tempPath}");

                // Step 2: Run installer with silent install and UAC elevation
                var startInfo = new ProcessStartInfo
                {
                    FileName = tempPath,
                    Arguments = "-install -silent",
                    UseShellExecute = true,  // Required for Verb = "runas" to work
                    Verb = "runas"           // This triggers the UAC prompt
                };

                Logger.Info("Launching PawnIO installer with -install -silent...");

                using (var process = Process.Start(startInfo))
                {
                    if (process != null)
                    {
                        Logger.Info($"PawnIO installer started with PID: {process.Id}");

                        // Wait for up to 2 minutes for installation
                        bool completed = process.WaitForExit(120000);

                        if (completed)
                        {
                            Logger.Info($"PawnIO installation completed with exit code: {process.ExitCode}");
                        }
                        else
                        {
                            Logger.Warn("PawnIO installation timed out after 2 minutes");
                            try { process.Kill(); } catch { }
                        }
                    }
                    else
                    {
                        Logger.Warn("Failed to start PawnIO installer (UAC may have been cancelled)");
                    }
                }
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                // Error 1223 = ERROR_CANCELLED - User cancelled the UAC prompt
                Logger.Info("PawnIO installation cancelled by user (UAC prompt declined)");
            }
            catch (WebException ex)
            {
                Logger.Error($"Failed to download PawnIO installer: {ex.Message}");
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to install PawnIO: {ex.Message}");
            }
            finally
            {
                // Step 3: Cleanup temp file
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                        Logger.Info("Cleaned up temporary installer file");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn($"Failed to cleanup temp file: {ex.Message}");
                }

                // Step 4: Refresh the installed status
                Logger.Info("Refreshing PawnIO installed status...");
                Manager?.RefreshPawnIOInstalledStatus();

                // Step 5: If PawnIO is now installed, restart helper to reinitialize with PawnIO support
                if (Manager?.IsPawnIOInstalled == true)
                {
                    Logger.Info("PawnIO is now installed - restarting helper to reinitialize...");
                    // Give time for the status to be sent to widget
                    System.Threading.Thread.Sleep(1000);
                    // Exit helper - widget will detect disconnection and relaunch
                    Environment.Exit(0);
                }
            }
        }
    }
}
