using NLog;
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace XboxGamingBarHelper.Windows
{
    // Enable/disable a Device Manager node (SetupAPI DIF_PROPERTYCHANGE), the same mechanism
    // Device Manager's own "Disable device" context-menu entry uses. Requires elevation
    // (our helper always runs elevated).
    internal static class SetupApi
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private static readonly Guid GUID_DEVCLASS_HIDCLASS = new Guid("745a17a0-74d3-11d0-b6fe-00a0c90f57da");

        private const uint DIGCF_PRESENT = 0x02;
        private const uint SPDRP_DEVICEDESC = 0x00;
        private const uint SPDRP_HARDWAREID = 0x01;
        private const uint SPDRP_COMPATIBLEIDS = 0x02;
        private const uint SPDRP_FRIENDLYNAME = 0x0C;
        private const uint DIF_PROPERTYCHANGE = 0x12;
        private const uint DICS_ENABLE = 1;
        private const uint DICS_DISABLE = 2;
        private const uint DICS_FLAG_GLOBAL = 1;

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVINFO_DATA
        {
            public uint cbSize;
            public Guid ClassGuid;
            public uint DevInst;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_CLASSINSTALL_HEADER
        {
            public uint cbSize;
            public uint InstallFunction;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_PROPCHANGE_PARAMS
        {
            public SP_CLASSINSTALL_HEADER ClassInstallHeader;
            public uint StateChange;
            public uint Scope;
            public uint HwProfile;
        }

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(ref Guid ClassGuid, IntPtr Enumerator, IntPtr hwndParent, uint Flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInfo(IntPtr DeviceInfoSet, uint MemberIndex, ref SP_DEVINFO_DATA DeviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool SetupDiGetDeviceRegistryProperty(IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData,
            uint Property, out uint PropertyRegDataType, byte[] PropertyBuffer, uint PropertyBufferSize, out uint RequiredSize);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiSetClassInstallParams(IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData,
            ref SP_PROPCHANGE_PARAMS ClassInstallParams, uint ClassInstallParamsSize);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiCallClassInstaller(uint InstallFunction, IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

        /// <summary>
        /// Enables or disables every present HID-class device whose Hardware IDs / Compatible
        /// IDs contain <paramref name="idContains"/> (case-insensitive) - e.g.
        /// "HID_DEVICE_UP:000D_U:0004", Windows' own generic compatible ID for any HID device
        /// declaring Usage Page 0x0D (Digitizer) + Usage 0x04 (Touch Screen) in its report
        /// descriptor. Matching on this instead of the friendly name/device description is
        /// deliberate: those strings are LOCALIZED by Windows ("HID-compliant touch screen"
        /// becomes e.g. "Ekran dotykowy zgodny z HID" on Polish Windows), so name-based
        /// matching silently fails on any non-English install. Hardware/Compatible IDs are
        /// never localized. Returns the number of matching devices successfully toggled.
        /// </summary>
        public static int SetHidDeviceEnabled(string idContains, bool enabled)
        {
            int touched = 0;
            Guid hidClassGuid = GUID_DEVCLASS_HIDCLASS;
            IntPtr deviceInfoSet = SetupDiGetClassDevs(ref hidClassGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT);
            if (deviceInfoSet == IntPtr.Zero || deviceInfoSet.ToInt64() == -1)
            {
                Logger.Warn($"SetHidDeviceEnabled: SetupDiGetClassDevs failed (error {Marshal.GetLastWin32Error()})");
                return 0;
            }

            try
            {
                uint index = 0;
                while (true)
                {
                    var devInfoData = new SP_DEVINFO_DATA();
                    devInfoData.cbSize = (uint)Marshal.SizeOf(typeof(SP_DEVINFO_DATA));
                    if (!SetupDiEnumDeviceInfo(deviceInfoSet, index, ref devInfoData))
                    {
                        break; // ERROR_NO_MORE_ITEMS
                    }
                    index++;

                    if (!HasMatchingId(deviceInfoSet, ref devInfoData, idContains))
                    {
                        continue;
                    }
                    string name = GetDeviceName(deviceInfoSet, ref devInfoData) ?? "(unnamed)";

                    var propChangeParams = new SP_PROPCHANGE_PARAMS
                    {
                        ClassInstallHeader = new SP_CLASSINSTALL_HEADER
                        {
                            cbSize = (uint)Marshal.SizeOf(typeof(SP_CLASSINSTALL_HEADER)),
                            InstallFunction = DIF_PROPERTYCHANGE
                        },
                        StateChange = enabled ? DICS_ENABLE : DICS_DISABLE,
                        Scope = DICS_FLAG_GLOBAL,
                        HwProfile = 0
                    };

                    if (!SetupDiSetClassInstallParams(deviceInfoSet, ref devInfoData, ref propChangeParams, (uint)Marshal.SizeOf(typeof(SP_PROPCHANGE_PARAMS))))
                    {
                        Logger.Warn($"SetHidDeviceEnabled: SetupDiSetClassInstallParams failed for '{name}' (error {Marshal.GetLastWin32Error()})");
                        continue;
                    }

                    if (!SetupDiCallClassInstaller(DIF_PROPERTYCHANGE, deviceInfoSet, ref devInfoData))
                    {
                        Logger.Warn($"SetHidDeviceEnabled: SetupDiCallClassInstaller failed for '{name}' (error {Marshal.GetLastWin32Error()})");
                        continue;
                    }

                    Logger.Info($"SetHidDeviceEnabled: '{name}' -> {(enabled ? "enabled" : "disabled")}");
                    touched++;
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(deviceInfoSet);
            }

            return touched;
        }

        private static bool HasMatchingId(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA devInfoData, string idContains)
        {
            foreach (var id in GetDeviceRegistryPropertyMultiSz(deviceInfoSet, ref devInfoData, SPDRP_COMPATIBLEIDS))
            {
                if (id.IndexOf(idContains, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            foreach (var id in GetDeviceRegistryPropertyMultiSz(deviceInfoSet, ref devInfoData, SPDRP_HARDWAREID))
            {
                if (id.IndexOf(idContains, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        private static string GetDeviceName(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA devInfoData)
        {
            string friendly = GetDeviceRegistryProperty(deviceInfoSet, ref devInfoData, SPDRP_FRIENDLYNAME);
            if (!string.IsNullOrEmpty(friendly))
            {
                return friendly;
            }
            return GetDeviceRegistryProperty(deviceInfoSet, ref devInfoData, SPDRP_DEVICEDESC);
        }

        private static string GetDeviceRegistryProperty(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA devInfoData, uint property)
        {
            SetupDiGetDeviceRegistryProperty(deviceInfoSet, ref devInfoData, property, out _, null, 0, out uint requiredSize);
            if (requiredSize == 0)
            {
                return null;
            }

            var buffer = new byte[requiredSize];
            if (!SetupDiGetDeviceRegistryProperty(deviceInfoSet, ref devInfoData, property, out _, buffer, requiredSize, out _))
            {
                return null;
            }

            return Encoding.Unicode.GetString(buffer).TrimEnd('\0');
        }

        // SPDRP_HARDWAREID / SPDRP_COMPATIBLEIDS are REG_MULTI_SZ: a sequence of null-terminated
        // strings, terminated by an extra empty string (double null overall). These IDs are
        // driver/PnP-generated and never localized (unlike SPDRP_FRIENDLYNAME/SPDRP_DEVICEDESC).
        private static string[] GetDeviceRegistryPropertyMultiSz(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA devInfoData, uint property)
        {
            SetupDiGetDeviceRegistryProperty(deviceInfoSet, ref devInfoData, property, out _, null, 0, out uint requiredSize);
            if (requiredSize == 0)
            {
                return Array.Empty<string>();
            }

            var buffer = new byte[requiredSize];
            if (!SetupDiGetDeviceRegistryProperty(deviceInfoSet, ref devInfoData, property, out _, buffer, requiredSize, out _))
            {
                return Array.Empty<string>();
            }

            string raw = Encoding.Unicode.GetString(buffer);
            return raw.Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries);
        }
    }
}
