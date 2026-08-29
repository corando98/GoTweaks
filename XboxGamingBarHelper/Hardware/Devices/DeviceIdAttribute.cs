using System;

namespace XboxGamingBarHelper.Hardware.Devices
{
    internal class DeviceIdAttribute : Attribute
    {
        public string MainboardId { get; }

        public DeviceIdAttribute(string mainboardId)
        {
            MainboardId = mainboardId;
        }
    }
}
