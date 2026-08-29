using System;
using System.Collections.Generic;
using System.Drawing;
using XboxGamingBarHelper.Hardware;

namespace XboxGamingBarHelper.RTSS.OSDItems
{
    internal class OSDItemVideoMemory : OSDItem
    {
        private HardwareSensor gpuMemoryUsedSensor;
        private HardwareSensor gpuMemoryTotalSensor;

        public OSDItemVideoMemory(HardwareSensor gpuMemoryUsedSensor, HardwareSensor gpuMemoryTotalSensor) : base("VRAM", Color.MediumOrchid)
        {
            this.gpuMemoryUsedSensor = gpuMemoryUsedSensor;
            this.gpuMemoryTotalSensor = gpuMemoryTotalSensor;
        }

        protected override List<OSDItemValue> GetValues(int osdLevel)
        {
            var osdItems = base.GetValues(osdLevel);

            if (osdLevel >= 2)
            {
                // Calculate percentage
                float percent = (gpuMemoryUsedSensor.Value / gpuMemoryTotalSensor.Value) * 100.0f;
                osdItems.Add(new OSDItemValue(percent, "%"));
            }

            if (osdLevel >= 3)
            {
                // Convert MB to GB
                float usedGb = gpuMemoryUsedSensor.Value / 1024.0f;
                osdItems.Add(new OSDItemValue(usedGb, "GiB", string.Empty, false));
            }

            return osdItems;
        }
    }
}
