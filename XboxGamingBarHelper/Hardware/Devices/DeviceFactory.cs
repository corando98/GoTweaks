´╗┐using NLog;
using System;
using XboxGamingBarHelper.Hardware.Devices.ASUS;
using XboxGamingBarHelper.Hardware.Devices.Lenovo;
using XboxGamingBarHelper.Hardware.Devices.Onexplayer;

namespace XboxGamingBarHelper.Hardware.Devices
{
    internal class DeviceFactory
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private static readonly Type[] DEVICE_TYPES = new Type[]
        {
            typeof(Onexplayer_2_Pro),
            typeof(Lenovo_Legion_GO_2),
            typeof(ASUS_ROG_Flow_Z13),
        };

        public static Device Create(string mainboardId, CPU cpu)
        {
            foreach (Type deviceType in DEVICE_TYPES)
            {
                var deviceIdAttribute = (DeviceIdAttribute)Attribute.GetCustomAttribute(deviceType, typeof(DeviceIdAttribute));

                if (deviceIdAttribute == null)
                {
                    Logger.Error($"Device type {deviceType.FullName} is missing DeviceIdAttribute.");
                    continue;
                }

                if (mainboardId.Contains(deviceIdAttribute.MainboardId))
                {
                    return (Device)Activator.CreateInstance(deviceType, new object[1] {cpu});
                }
            }

            Logger.Info($"No matching Device found for mainboard id: {mainboardId}. Using GenericDevice.");
            // If no matching CPU type is found, return a GenericCPU instance
            return new GenericDevice(cpu);
        }
    }
}
