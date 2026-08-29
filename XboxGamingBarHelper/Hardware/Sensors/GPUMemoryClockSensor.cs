#if !STORE
using LibreHardwareMonitor.Hardware;
#endif

namespace XboxGamingBarHelper.Hardware.Sensors
{
    internal class GPUMemoryClockSensor : HardwareSensor
    {
        public GPUMemoryClockSensor() : base("GPU Memory", HardwareType.GpuAmd, SensorType.Clock)
        {
        }
    }
}
