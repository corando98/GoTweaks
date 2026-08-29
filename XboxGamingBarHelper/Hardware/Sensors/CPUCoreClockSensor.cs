#if !STORE
using LibreHardwareMonitor.Hardware;
#endif

namespace XboxGamingBarHelper.Hardware.Sensors
{
    internal class CPUCoreClockSensor : HardwareSensor
    {
        public int CoreIndex { get; }

        public CPUCoreClockSensor(int coreIndex) : base($"Core #{coreIndex + 1}", HardwareType.Cpu, SensorType.Clock)
        {
            CoreIndex = coreIndex;
        }
    }
}
