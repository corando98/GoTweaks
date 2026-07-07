using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using NLog;
using Shared.Enums;

namespace XboxGamingBarHelper.Labs
{
    /// <summary>
    /// Utility class for ViGEmBus detection and installation.
    /// Used by Program.cs to manage ViGEmBus driver status.
    /// </summary>
    internal static class ViGEmBusHelper
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        // ViGEmBus device interface GUID
        private static readonly Guid VIGEM_GUID = new Guid("96E42B22-F5E9-42F8-B043-ED0F932F014F");

        // Latest ViGEmBus release URL
        private const string ViGEmBusDownloadUrl = "https://github.com/nefarius/ViGEmBus/releases/download/v1.22.0/ViGEmBus_1.22.0_x64_x86_arm64.exe";

        private const uint DIGCF_PRESENT = 0x02;
        private const uint DIGCF_DEVICEINTERFACE = 0x10;

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(
            ref Guid classGuid,
            IntPtr enumerator,
            IntPtr hwndParent,
            uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(
            IntPtr deviceInfoSet,
            IntPtr deviceInfoData,
            ref Guid interfaceClassGuid,
            uint memberIndex,
            ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        /// <summary>
        /// Checks if ViGEmBus is installed by looking for the device interface.
        /// </summary>
        public static bool IsInstalled()
        {
            Guid vigemGuid = VIGEM_GUID;

            IntPtr deviceInfoSet = SetupDiGetClassDevs(
                ref vigemGuid,
                IntPtr.Zero,
                IntPtr.Zero,
                DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);

            if (deviceInfoSet == IntPtr.Zero || deviceInfoSet == new IntPtr(-1))
            {
                return false;
            }

            try
            {
                var interfaceData = new SP_DEVICE_INTERFACE_DATA
                {
                    cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>()
                };

                // Try to get the first device interface
                bool found = SetupDiEnumDeviceInterfaces(deviceInfoSet, IntPtr.Zero, ref vigemGuid, 0, ref interfaceData);
                return found;
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(deviceInfoSet);
            }
        }

        /// <summary>
        /// Downloads and installs ViGEmBus with silent install and admin elevation.
        /// Returns true if installation was successful.
        /// </summary>
        public static bool Install()
        {
            string tempPath = Path.Combine(Path.GetTempPath(), "ViGEmBus_setup.exe");

            try
            {
                // Step 1: Download the installer
                Logger.Info($"Downloading ViGEmBus installer from {ViGEmBusDownloadUrl}...");

                // TimedWebClient (60s): stock WebClient has no timeout, so a
                // firewall that drops outbound (issue #91) hangs this forever.
                using (var client = new XboxGamingBarHelper.Core.TimedWebClient(TimeSpan.FromSeconds(60)))
                {
                    // Add user agent to avoid potential blocks
                    client.Headers.Add("User-Agent", "GoTweaks/1.0");
                    client.DownloadFile(ViGEmBusDownloadUrl, tempPath);
                }

                Logger.Info($"ViGEmBus installer downloaded to {tempPath}");

                // Step 2: Run installer with silent install and UAC elevation
                var startInfo = new ProcessStartInfo
                {
                    FileName = tempPath,
                    Arguments = "/quiet /norestart",
                    UseShellExecute = true,  // Required for Verb = "runas" to work
                    Verb = "runas"           // This triggers the UAC prompt
                };

                Logger.Info("Launching ViGEmBus installer with /quiet /norestart...");

                using (var process = Process.Start(startInfo))
                {
                    if (process != null)
                    {
                        Logger.Info($"ViGEmBus installer started with PID: {process.Id}");

                        // Wait for up to 2 minutes for installation
                        bool completed = process.WaitForExit(120000);

                        if (completed)
                        {
                            Logger.Info($"ViGEmBus installation completed with exit code: {process.ExitCode}");
                            return process.ExitCode == 0;
                        }
                        else
                        {
                            Logger.Warn("ViGEmBus installation timed out after 2 minutes");
                            try { process.Kill(); } catch { }
                            return false;
                        }
                    }
                    else
                    {
                        Logger.Warn("Failed to start ViGEmBus installer (UAC may have been cancelled)");
                        return false;
                    }
                }
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                // Error 1223 = ERROR_CANCELLED - User cancelled the UAC prompt
                Logger.Info("ViGEmBus installation cancelled by user (UAC prompt declined)");
                return false;
            }
            catch (WebException ex)
            {
                Logger.Error($"Failed to download ViGEmBus installer: {ex.Message}");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to install ViGEmBus: {ex.Message}");
                return false;
            }
            finally
            {
                // Cleanup temp file
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
            }
        }
    }
}
