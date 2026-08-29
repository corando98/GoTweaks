´╗┐using Microsoft.Win32;
using System;
using System.Diagnostics;

namespace Shared.Utilities
{
    public class AMDHelper
    {
        public const string AMD_SOFTWARE_ADRENALINE_EDITION_FILE_NAME = "RadeonSoftware";

        public static bool IsInstalled(out string installDir)
        {
            installDir = RegistryHelper.ReadStringValue(Registry.LocalMachine, @"SOFTWARE\AMD\CN", "InstallDir");
            return !string.IsNullOrEmpty(installDir);
        }

        public static bool IsRunning(out Process process)
        {
            var amdProcesses = Process.GetProcessesByName(AMD_SOFTWARE_ADRENALINE_EDITION_FILE_NAME);
            if (amdProcesses.Length == 0)
            {
                process = null;
                return false;
            }

            process = amdProcesses[0];
            return (DateTime.Now - process.StartTime).TotalSeconds >= 3.0f;
        }
    }
}
