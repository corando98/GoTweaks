#if !STORE
using LibreHardwareMonitor.Hardware;
#endif

using XboxGamingBarHelper.Performance;
namespace XboxGamingBarHelper.Hardware.Sensors
{
    internal class GPUMemoryClockSensor : HardwareSensor
    {
        public GPUMemoryClockSensor() : base("GPU Memory", HardwareType.GpuAmd, SensorType.Clock)
        {
        }
    }
}
