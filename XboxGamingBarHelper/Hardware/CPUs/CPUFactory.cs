´╗┐using NLog;
using System;
using XboxGamingBarHelper.Hardware.CPUs;
using XboxGamingBarHelper.Hardware.CPUs.AMD;

namespace XboxGamingBarHelper.Hardware
{
    internal class CPUFactory
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        
        private static readonly Type[] CPU_TYPES = new Type[]
        {
            typeof(AMD_Ryzen_7_7840U),
            typeof(AMD_Ryzen_Z1),
            typeof(AMD_Ryzen_Z1_Extreme),
            typeof(AMD_Ryzen_Z2),
            typeof(AMD_Ryzen_Z2_Extreme),
            typeof(AMD_Ryzen_AI_Max_Plus_395),
        };

        public static CPU Create(string cpuName)
        {
            foreach (Type cpuType in CPU_TYPES)
            {
                var cpuIdAttribute = (CPUIdAttribute)Attribute.GetCustomAttribute(cpuType, typeof(CPUIdAttribute));

                if (cpuIdAttribute == null)
                {
                    Logger.Error($"CPU type {cpuType.FullName} is missing CPUIdAttribute.");
                    continue;
                }

                if (cpuName.ToLower() == cpuIdAttribute.Id.ToLower())
                {
                    return (CPU)Activator.CreateInstance(cpuType);
                }
            }

            Logger.Info($"No matching CPU type found for CPU name: {cpuName}. Using GenericCPU.");
            // If no matching CPU type is found, return a GenericCPU instance
            return new GenericCPU();
        }
    }
}
