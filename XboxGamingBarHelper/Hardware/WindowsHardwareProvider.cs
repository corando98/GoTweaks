using NLog;
using System;
using System.Windows.Forms;
using XboxGamingBarHelper.Core;
using XboxGamingBarHelper.Power;
using XboxGamingBarHelper.Windows;

namespace XboxGamingBarHelper.Hardware
{
    internal class WindowsHardwareProvider : IHardwareProvider
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private float batteryLevel = -1.0f;
        private float batteryRemainingTime = -1.0f;
        private float batteryDischargeRate = -1.0f;
        private float batteryChargeRate = -1.0f;

        private System.Diagnostics.PerformanceCounter cpuCounter;
        private System.Diagnostics.PerformanceCounter cpuFreqCounter;
        private System.Diagnostics.PerformanceCounter[] cpuCoreCounters;
        private System.Diagnostics.PerformanceCounter[] cpuCoreFreqCounters;

        private float cpuUsage = -1.0f;
        private float cpuClock = -1.0f;
        private float[] cpuCoreUsages;
        private float[] cpuCoreClocks;
        private float maxCpuMhz = 0f;
        private int coreCount = 0;

        public WindowsHardwareProvider()
        {
            try
            {
                cpuCounter = new System.Diagnostics.PerformanceCounter("Processor", "% Processor Time", "_Total");
                cpuCounter.NextValue(); // First call usually returns 0
            }
            catch (System.Exception ex)
            {
                Logger.Error(ex, "Failed to initialize CPU usage counter");
            }

            try
            {
                cpuFreqCounter = new System.Diagnostics.PerformanceCounter("Processor Information", "% of Maximum Frequency", "_Total");
                cpuFreqCounter.NextValue();
            }
            catch (System.Exception ex)
            {
                Logger.Error(ex, "Failed to initialize CPU frequency counter");
            }

            // Initialize per-core counters
            coreCount = Math.Min(System.Environment.ProcessorCount, 8);
            cpuCoreCounters = new System.Diagnostics.PerformanceCounter[coreCount];
            cpuCoreFreqCounters = new System.Diagnostics.PerformanceCounter[coreCount];
            cpuCoreUsages = new float[coreCount];
            cpuCoreClocks = new float[coreCount];

            for (int i = 0; i < coreCount; i++)
            {
                cpuCoreUsages[i] = -1.0f;
                cpuCoreClocks[i] = -1.0f;

                try
                {
                    cpuCoreCounters[i] = new System.Diagnostics.PerformanceCounter("Processor", "% Processor Time", i.ToString());
                    cpuCoreCounters[i].NextValue();
                }
                catch (System.Exception ex)
                {
                    Logger.Error(ex, $"Failed to initialize CPU usage counter for core {i}");
                }

                try
                {
                    // Instance name for Processor Information is "0,0", "0,1" etc.
                    cpuCoreFreqCounters[i] = new System.Diagnostics.PerformanceCounter("Processor Information", "% of Maximum Frequency", $"0,{i}");
                    cpuCoreFreqCounters[i].NextValue();
                }
                catch
                {
                    // Fallback to "0" if "0,0" fails
                    try
                    {
                        cpuCoreFreqCounters[i] = new System.Diagnostics.PerformanceCounter("Processor Information", "% of Maximum Frequency", i.ToString());
                        cpuCoreFreqCounters[i].NextValue();
                    }
                    catch (System.Exception ex)
                    {
                        Logger.Error(ex, $"Failed to initialize CPU frequency counter for core {i}");
                    }
                }
            }
        }

        private float memoryUsage = -1.0f;
        private float memoryUsed = -1.0f;

        public void Update()
        {
            var powerStatus = System.Windows.Forms.SystemInformation.PowerStatus;

            batteryLevel = powerStatus.BatteryLifePercent * 100;
            batteryRemainingTime = powerStatus.BatteryLifeRemaining;

            if (PowerManager.TryGetBatteryState(out var battery))
            {
                if (battery.Charging)
                {
                    batteryDischargeRate = -1.0f;
                    batteryChargeRate = battery.Rate / 1000.0f;
                }
                else
                {
                    batteryDischargeRate = battery.Rate / 1000.0f;
                    batteryChargeRate = -1.0f;
                }
            }
            else
            {
                // Logger.Warn("Can't get battery charge/discharge rate.");
            }

            // Update CPU Usage
            if (cpuCounter != null)
            {
                try
                {
                    cpuUsage = cpuCounter.NextValue();
                }
                catch { }
            }

            // Update CPU Clock
            if (maxCpuMhz <= 0)
            {
                maxCpuMhz = GetMaxCpuFrequency();
            }

            if (maxCpuMhz > 0 && cpuFreqCounter != null)
            {
                try
                {
                    float percent = cpuFreqCounter.NextValue();
                    cpuClock = maxCpuMhz * (percent / 100.0f);
                }
                catch
                {
                    cpuClock = -1.0f;
                }
            }
            else
            {
                // Fallback or just keep 0 if failed
                cpuClock = maxCpuMhz;
            }

            // Update Per-Core CPU
            for (int i = 0; i < coreCount; i++)
            {
                if (cpuCoreCounters[i] != null)
                {
                    try { cpuCoreUsages[i] = cpuCoreCounters[i].NextValue(); } catch { }
                }

                if (maxCpuMhz > 0 && cpuCoreFreqCounters[i] != null)
                {
                    try
                    {
                        float percent = cpuCoreFreqCounters[i].NextValue();
                        cpuCoreClocks[i] = maxCpuMhz * (percent / 100.0f);
                    }
                    catch { cpuCoreClocks[i] = maxCpuMhz; }
                }
                else
                {
                    cpuCoreClocks[i] = maxCpuMhz;
                }
            }

            // Update Memory
            try
            {
                var memStatus = new Kernel32.MEMORYSTATUSEX();
                if (Kernel32.GlobalMemoryStatusEx(memStatus))
                {
                    memoryUsage = memStatus.dwMemoryLoad;
                    // convert bytes to GB with double precision to ensure float result
                    double usedBytes = (double)(memStatus.ullTotalPhys - memStatus.ullAvailPhys);
                    memoryUsed = (float)(usedBytes / (1024.0 * 1024.0 * 1024.0));
                }
            }
            catch (System.Exception ex)
            {
                Logger.Error(ex, "Failed to get memory status");
            }
        }

        private float GetMaxCpuFrequency()
        {
            try
            {
                int coreCount = System.Environment.ProcessorCount;
                int size = System.Runtime.InteropServices.Marshal.SizeOf(typeof(XboxGamingBarHelper.Windows.PROCESSOR_POWER_INFORMATION)) * coreCount;
                System.IntPtr buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(size);

                try
                {
                    uint ret = XboxGamingBarHelper.Windows.PowrProf.CallNtPowerInformation(
                        (int)XboxGamingBarHelper.Windows.POWER_INFORMATION_LEVEL.ProcessorInformation,
                        System.IntPtr.Zero,
                        0,
                        buffer,
                        size);

                    if (ret == 0) // STATUS_SUCCESS
                    {
                        float maxMhz = 0;
                        long stride = System.Runtime.InteropServices.Marshal.SizeOf(typeof(XboxGamingBarHelper.Windows.PROCESSOR_POWER_INFORMATION));
                        for (int i = 0; i < coreCount; i++)
                        {
                            System.IntPtr ptr = (System.IntPtr)((long)buffer + (i * stride));
                            var info = (XboxGamingBarHelper.Windows.PROCESSOR_POWER_INFORMATION)System.Runtime.InteropServices.Marshal.PtrToStructure(ptr, typeof(XboxGamingBarHelper.Windows.PROCESSOR_POWER_INFORMATION));
                            if (info.MaxMhz > maxMhz) maxMhz = info.MaxMhz;
                        }
                        return maxMhz;
                    }
                }
                finally
                {
                    System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
                }
            }
            catch (System.Exception ex)
            {
                Logger.Error(ex, "Failed to get CPU frequency");
            }

            return -1.0f;
        }

        public float GetCpuClock() => cpuClock;
        public float GetCpuUsage() => cpuUsage;
        public float GetCpuWattage() => -1.0f; // Not possible without Kernel Driver (Store unsafe)

        public float GetCpuTemperature()
        {
            return -1.0f;

            //float temp = GetTempFromMSAcpi();
            //if (temp > 0) return temp;

            //temp = GetTempFromPerfCounters();
            //if (temp > 0) return temp;

            //temp = GetTempFromWin32Probe();
            //if (temp > 0) return temp;

            //Logger.Debug("All WMI temperature queries failed or returned invalid data.");
            //return -1.0f;
        }

        //private float GetTempFromMSAcpi()
        //{
        //    try
        //    {
        //        using (var searcher = new System.Management.ManagementObjectSearcher(@"root\WMI", "SELECT * FROM MSAcpi_ThermalZoneTemperature"))
        //        using (var collection = searcher.Get())
        //        {
        //            if (collection.Count > 0)
        //            {
        //                foreach (var obj in collection)
        //                {
        //                    try
        //                    {
        //                        float tempKelvin = System.Convert.ToSingle(obj["CurrentTemperature"]);
        //                        float tempCelsius = (tempKelvin - 2732) / 10.0f;

        //                        Logger.Info($"MSAcpi Temp: {tempCelsius} C");
        //                        if (tempCelsius > 0) return tempCelsius;
        //                    }
        //                    catch (Exception ex1)
        //                    {
        //                        Logger.Debug("MSAcpi temperature read failed.");
        //                    }
        //                }
        //            }
        //            else
        //            {
        //                Logger.Debug("MSAcpi_ThermalZoneTemperature returned no results.");
        //            }
        //        }
        //    }
        //    catch (Exception ex) { Logger.Debug(ex, "MSAcpi query failed"); }
        //    return -1.0f;
        //}

        //private float GetTempFromPerfCounters()
        //{
        //    try
        //    {
        //        using (var searcher = new System.Management.ManagementObjectSearcher(@"root\CIMV2", "SELECT * FROM Win32_PerfFormattedData_Counters_ThermalZoneInformation"))
        //        using (var collection = searcher.Get())
        //        {
        //            if (collection.Count > 0)
        //            {
        //                foreach (var obj in collection)
        //                {
        //                    try
        //                    {
        //                        // Usually in Kelvin 
        //                        float tempKelvin = System.Convert.ToSingle(obj["Temperature"]);
        //                        float tempCelsius = tempKelvin - 273.15f;

        //                        Logger.Debug($"PerfCounter Temp: {tempCelsius} C");
        //                        if (tempCelsius > 0) return tempCelsius;
        //                    }
        //                    catch (Exception ex1) { Logger.Debug("PerfCounter read temperature failed."); }
        //                }
        //            }
        //            else
        //            {
        //                Logger.Debug("Win32_PerfFormattedData_Counters_ThermalZoneInformation returned no results.");
        //            }
        //        }
        //    }
        //    catch (Exception ex) { Logger.Debug("PerfCounter query failed"); }
        //    return -1.0f;
        //}

        //private float GetTempFromWin32Probe()
        //{
        //    try
        //    {
        //        using (var searcher = new System.Management.ManagementObjectSearcher(@"root\CIMV2", "SELECT * FROM Win32_TemperatureProbe"))
        //        using (var collection = searcher.Get())
        //        {
        //            if (collection.Count > 0)
        //            {
        //                foreach (var obj in collection)
        //                {
        //                    try
        //                    {
        //                        // Usually in 10ths of degrees Celsius or Kelvin. 
        //                        // Win32_TemperatureProbe.CurrentReading is mostly "Current Reading" in deci-degrees Celsius? 
        //                        // Documentation is vague, often 10ths of degrees C.
        //                        float tempRaw = System.Convert.ToSingle(obj["CurrentReading"]);
        //                        // Assuming standard 10ths of degrees C if > 100, otherwise raw C
        //                        float tempCelsius = (tempRaw > 100) ? tempRaw / 10.0f : tempRaw;

        //                        Logger.Debug($"Win32Probe Temp: {tempCelsius} C");
        //                        if (tempCelsius > 0) return tempCelsius;
        //                    }
        //                    catch (Exception ex1) { Logger.Debug("Win32Probe temperature read failed"); }
        //                }
        //            }
        //            else
        //            {
        //                Logger.Debug("Win32_TemperatureProbe returned no results.");
        //            }
        //        }
        //    }
        //    catch (Exception ex) { Logger.Debug("Win32Probe query failed"); }
        //    return -1.0f;
        //}

        public float GetGpuClock()
        {
            return (float)(XboxGamingBarHelper.AMD.AMDManager.Instance?.GetGPUClock() ?? -1.0);
        }

        public float GetGpuUsage()
        {
            return (float)(XboxGamingBarHelper.AMD.AMDManager.Instance?.GetGPUUsage() ?? -1.0);
        }

        public float GetGpuMemoryUsed()
        {
            return (float)(XboxGamingBarHelper.AMD.AMDManager.Instance?.GetGPUMemoryUsed() ?? -1.0);
        }

        public float GetGpuMemoryTotal()
        {
            return (float)(XboxGamingBarHelper.AMD.AMDManager.Instance?.GetGPUMemoryTotal() ?? -1.0);
        }

        public float GetGpuMemoryClock()
        {
            return (float)(XboxGamingBarHelper.AMD.AMDManager.Instance?.GetGPUMemoryClock() ?? -1.0);
        }

        public float GetGpuWattage()
        {
            return (float)(XboxGamingBarHelper.AMD.AMDManager.Instance?.GetGPUWattage() ?? -1.0);
        }

        public float GetGpuTemperature()
        {
            return (float)(XboxGamingBarHelper.AMD.AMDManager.Instance?.GetGPUTemperature() ?? -1.0);
        }

        public float GetMemoryUsage() => memoryUsage;
        public float GetMemoryUsed() => memoryUsed;

        public float GetBatteryLevel() => batteryLevel;
        public float GetBatteryRemainingTime() => batteryRemainingTime;
        public float GetBatteryDischargeRate() => batteryDischargeRate;
        public float GetBatteryChargeRate() => -1.0f;

        public int GetCpuCoreCount() => coreCount;
        public float GetCpuCoreUsage(int coreIndex) => (coreIndex >= 0 && coreIndex < coreCount) ? cpuCoreUsages[coreIndex] : -1.0f;
        public float GetCpuCoreClock(int coreIndex) => (coreIndex >= 0 && coreIndex < coreCount) ? cpuCoreClocks[coreIndex] : -1.0f;

        public string GetCpuName() => "Unknown CPU";
        public string GetMotherboardName() => string.Empty;
    }
}
