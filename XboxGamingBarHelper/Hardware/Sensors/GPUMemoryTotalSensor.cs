#if !STORE
using LibreHardwareMonitor.Hardware;
#endif

namespace XboxGamingBarHelper.Hardware.Sensors
{
    internal class GPUMemoryTotalSensor : HardwareSensor
    {
        public GPUMemoryTotalSensor() : base("GPU Memory Total", HardwareType.GpuAmd, SensorType.SmallData)
        {
        }
    }
}
