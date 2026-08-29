#if !STORE
using LibreHardwareMonitor.Hardware;
#endif

using XboxGamingBarHelper.Performance;
namespace XboxGamingBarHelper.Hardware.Sensors
{
    internal class GPUMemoryUsedSensor : HardwareSensor
    {
        public GPUMemoryUsedSensor() : base("GPU Memory Used", HardwareType.GpuAmd, SensorType.SmallData)
        {
        }
    }
}
