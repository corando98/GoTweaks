using System.Collections.Generic;
using System.Drawing;
using XboxGamingBarHelper.Hardware;

namespace XboxGamingBarHelper.RTSS.OSDItems
{
    internal class OSDItemCPUPerCore : OSDItem
    {
        private HardwareSensor cpuCoreUsageSensor;
        private HardwareSensor cpuCoreClockSensor;

        public OSDItemCPUPerCore(int coreIndex, HardwareSensor cpuCoreUsageSensor, HardwareSensor cpuCoreClockSensor) : base($"CPU<S=50>{coreIndex}<S>", Color.Turquoise)
        {
            this.cpuCoreUsageSensor = cpuCoreUsageSensor;
            this.cpuCoreClockSensor = cpuCoreClockSensor;
        }

        protected override List<OSDItemValue> GetValues(int osdLevel)
        {
            var osdItems = new List<OSDItemValue>();

            if (osdLevel < 4)
            {
                return osdItems;
            }

            osdItems.Add(new OSDItemValue(cpuCoreUsageSensor.Value, "%"));
            osdItems.Add(new OSDItemValue(cpuCoreClockSensor.Value, "MHz"));

            return osdItems;
        }
    }
}
