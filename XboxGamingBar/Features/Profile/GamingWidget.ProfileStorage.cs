using Microsoft.Gaming.XboxGameBar;
using Microsoft.Gaming.XboxGameBar.Input;
using Microsoft.UI.Xaml.Controls;
using NLog;
using Shared.Data;
using Shared.Utilities;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Data.Json;
using Windows.Foundation;
using Windows.Foundation.Metadata;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;
using Windows.System.Power;
using Windows.Storage;
using Windows.System;
using Windows.UI.Xaml.Input;
using System.Runtime.InteropServices;
using Windows.UI;
using XboxGamingBar.Data;
using XboxGamingBar.Event;
using XboxGamingBar.IPC;
using XboxGamingBar.QuickSettings;
using Shared.Enums;

namespace XboxGamingBar
{
    public sealed partial class GamingWidget
    {

        private void SaveProfileToStorage(string profileName, PerformanceProfile profile)
        {
            // Never save to "No game detected" profile (case-insensitive check)
            if (profileName.IndexOf("No game detected", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Logger.Warn($"Attempted to save to storage with invalid profile name: {profileName}, skipping");
                return;
            }

            var settings = ApplicationData.Current.LocalSettings;
            var container = settings.CreateContainer($"Profile_{profileName}", ApplicationDataCreateDisposition.Always);

            container.Values["TDP"] = profile.TDP;
            container.Values["CPUBoost"] = profile.CPUBoost;
            container.Values["CPUEPP"] = profile.CPUEPP;
            container.Values["FluidMotionFrames"] = profile.FluidMotionFrames;
            container.Values["RadeonSuperResolution"] = profile.RadeonSuperResolution;
            container.Values["RadeonSuperResolutionSharpness"] = profile.RadeonSuperResolutionSharpness;
            container.Values["ImageSharpening"] = profile.ImageSharpening;
            container.Values["ImageSharpeningSharpness"] = profile.ImageSharpeningSharpness;
            container.Values["RadeonAntiLag"] = profile.RadeonAntiLag;
            container.Values["RadeonBoost"] = profile.RadeonBoost;
            container.Values["RadeonBoostResolution"] = profile.RadeonBoostResolution;
            container.Values["RadeonChill"] = profile.RadeonChill;
            container.Values["RadeonChillMinFPS"] = profile.RadeonChillMinFPS;
            container.Values["RadeonChillMaxFPS"] = profile.RadeonChillMaxFPS;
            container.Values["FPSLimitEnabled"] = profile.FPSLimitEnabled;
            container.Values["FPSLimitValue"] = profile.FPSLimitValue;
            container.Values["AutoTDPEnabled"] = profile.AutoTDPEnabled;
            container.Values["AutoTDPTargetFPS"] = profile.AutoTDPTargetFPS;
            container.Values["AutoTDPMinTDP"] = profile.AutoTDPMinTDP;
            container.Values["AutoTDPMaxTDP"] = profile.AutoTDPMaxTDP;
            container.Values["AutoTDPUseMLMode"] = profile.AutoTDPUseMLMode;
            container.Values["AutoTDPControllerType"] = profile.AutoTDPControllerType;
            container.Values["OSPowerMode"] = profile.OSPowerMode;
            container.Values["LegionPerformanceMode"] = profile.LegionPerformanceMode;
            container.Values["TDPModeIndex"] = profile.TDPModeIndex;
            container.Values["TDPBoostEnabled"] = profile.TDPBoostEnabled;
            container.Values["TDPBoostSPPT"] = profile.TDPBoostSPPT;
            container.Values["TDPBoostFPPT"] = profile.TDPBoostFPPT;
            container.Values["HDREnabled"] = profile.HDREnabled;
            container.Values["Resolution"] = profile.Resolution;
            if (profile.RefreshRate.HasValue)
                container.Values["RefreshRate"] = profile.RefreshRate.Value;
            else
                container.Values.Remove("RefreshRate");
            container.Values["StickyTDPEnabled"] = profile.StickyTDPEnabled;
            container.Values["StickyTDPInterval"] = profile.StickyTDPInterval;
            container.Values["OverlayLevel"] = profile.OverlayLevel;
            // Last-saved timestamp drives the "modified Nm/h/d ago" line on the profile
            // card and the "Last Modified" sort option in the Profiles tab. Stored as
            // UTC ticks so it survives timezone changes.
            container.Values["LastModifiedUtc"] = DateTime.UtcNow.Ticks;
        }

        private void LoadProfileFromStorage(string profileName, PerformanceProfile profile)
        {
            var settings = ApplicationData.Current.LocalSettings;
            if (settings.Containers.ContainsKey($"Profile_{profileName}"))
            {
                var container = settings.Containers[$"Profile_{profileName}"];

                // When the widget-side Profile_* LocalSettings container doesn't have a TDP
                // entry, fall back to the helper's current TDP (authoritative across reboots
                // via global.xml) rather than a hardcoded 15 W — otherwise non-Legion devices
                // reset TDP to 15 on every cold start (issues #74, #79).
                profile.TDP = container.Values.ContainsKey("TDP") ? (double)container.Values["TDP"] : (tdp?.Value ?? 15);
                // Use current system values as defaults for EPP and CPU Boost (synced from helper)
                profile.CPUBoost = container.Values.ContainsKey("CPUBoost") ? (bool)container.Values["CPUBoost"] : (cpuBoost?.Value ?? false);
                profile.CPUEPP = container.Values.ContainsKey("CPUEPP") ? (double)container.Values["CPUEPP"] : (cpuEPP?.Value ?? 80);
                profile.FluidMotionFrames = container.Values.ContainsKey("FluidMotionFrames") ? (bool)container.Values["FluidMotionFrames"] : false;
                profile.RadeonSuperResolution = container.Values.ContainsKey("RadeonSuperResolution") ? (bool)container.Values["RadeonSuperResolution"] : false;
                profile.RadeonSuperResolutionSharpness = container.Values.ContainsKey("RadeonSuperResolutionSharpness") ? (double)container.Values["RadeonSuperResolutionSharpness"] : 80;
                profile.ImageSharpening = container.Values.ContainsKey("ImageSharpening") ? (bool)container.Values["ImageSharpening"] : false;
                profile.ImageSharpeningSharpness = container.Values.ContainsKey("ImageSharpeningSharpness") ? (double)container.Values["ImageSharpeningSharpness"] : 80;
                profile.RadeonAntiLag = container.Values.ContainsKey("RadeonAntiLag") ? (bool)container.Values["RadeonAntiLag"] : false;
                profile.RadeonBoost = container.Values.ContainsKey("RadeonBoost") ? (bool)container.Values["RadeonBoost"] : false;
                profile.RadeonBoostResolution = container.Values.ContainsKey("RadeonBoostResolution") ? (double)container.Values["RadeonBoostResolution"] : 0;
                profile.RadeonChill = container.Values.ContainsKey("RadeonChill") ? (bool)container.Values["RadeonChill"] : false;
                profile.RadeonChillMinFPS = container.Values.ContainsKey("RadeonChillMinFPS") ? (double)container.Values["RadeonChillMinFPS"] : 30;
                profile.RadeonChillMaxFPS = container.Values.ContainsKey("RadeonChillMaxFPS") ? (double)container.Values["RadeonChillMaxFPS"] : 60;
                profile.FPSLimitEnabled = container.Values.ContainsKey("FPSLimitEnabled") ? (bool)container.Values["FPSLimitEnabled"] : false;
                profile.FPSLimitValue = container.Values.ContainsKey("FPSLimitValue") ? (int)container.Values["FPSLimitValue"] : 60;
                profile.AutoTDPEnabled = container.Values.ContainsKey("AutoTDPEnabled") ? (bool)container.Values["AutoTDPEnabled"] : false;
                profile.AutoTDPTargetFPS = container.Values.ContainsKey("AutoTDPTargetFPS") ? (int)container.Values["AutoTDPTargetFPS"] : 60;
                profile.AutoTDPMinTDP = container.Values.ContainsKey("AutoTDPMinTDP") ? (int)container.Values["AutoTDPMinTDP"] : 8;
                profile.AutoTDPMaxTDP = container.Values.ContainsKey("AutoTDPMaxTDP") ? (int)container.Values["AutoTDPMaxTDP"] : 30;
                profile.AutoTDPUseMLMode = container.Values.ContainsKey("AutoTDPUseMLMode") ? (bool)container.Values["AutoTDPUseMLMode"] : false;
                profile.AutoTDPControllerType = container.Values.ContainsKey("AutoTDPControllerType") ? (int)container.Values["AutoTDPControllerType"] : 0;
                profile.OSPowerMode = container.Values.ContainsKey("OSPowerMode") ? (int)container.Values["OSPowerMode"] : 1;
                // Only load LegionPerformanceMode if it exists in storage - keep profile's existing value otherwise
                // This preserves the default (Balanced=2) for new profiles but doesn't override if storage key is missing
                if (container.Values.ContainsKey("LegionPerformanceMode"))
                {
                    profile.LegionPerformanceMode = (int)container.Values["LegionPerformanceMode"];
                }
                // Load TDPModeIndex for custom presets (-1 means use LegionPerformanceMode to determine index)
                profile.TDPModeIndex = container.Values.ContainsKey("TDPModeIndex") ? (int)container.Values["TDPModeIndex"] : -1;
                profile.TDPBoostEnabled = container.Values.ContainsKey("TDPBoostEnabled") ? (bool)container.Values["TDPBoostEnabled"] : false;
                profile.TDPBoostSPPT = container.Values.ContainsKey("TDPBoostSPPT") ? (int)container.Values["TDPBoostSPPT"] : 1;
                profile.TDPBoostFPPT = container.Values.ContainsKey("TDPBoostFPPT") ? (int)container.Values["TDPBoostFPPT"] : 3;
                profile.HDREnabled = container.Values.ContainsKey("HDREnabled") ? (bool)container.Values["HDREnabled"] : false;
                profile.Resolution = container.Values.ContainsKey("Resolution") ? (string)container.Values["Resolution"] : "";
                profile.RefreshRate = container.Values.ContainsKey("RefreshRate") ? (int?)container.Values["RefreshRate"] : null;
                profile.StickyTDPEnabled = container.Values.ContainsKey("StickyTDPEnabled") ? (bool)container.Values["StickyTDPEnabled"] : true;
                profile.StickyTDPInterval = container.Values.ContainsKey("StickyTDPInterval") ? (int)container.Values["StickyTDPInterval"] : 5;
                profile.OverlayLevel = container.Values.ContainsKey("OverlayLevel") ? (int)container.Values["OverlayLevel"] : 0;

                Logger.Info($"Loaded {profileName} profile from storage");
            }
        }

    }
}
