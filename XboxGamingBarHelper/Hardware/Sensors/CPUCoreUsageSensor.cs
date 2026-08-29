#if !STORE
using LibreHardwareMonitor.Hardware;
#endif

namespace XboxGamingBarHelper.Hardware.Sensors
{
    internal class CPUCoreUsageSensor : HardwareSensor
    {
        public int CoreIndex { get; }

        public CPUCoreUsageSensor(int coreIndex) : base($"CPU Core #{coreIndex + 1}", HardwareType.Cpu, SensorType.Load)
        {
            CoreIndex = coreIndex;
        }
    }
}
