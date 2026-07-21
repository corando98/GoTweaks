using NLog;
using System;

namespace XboxGamingBarHelper.AMD.Settings
{
    internal class AMDRadeonSuperResolutionSetting : AMDSetting<IADLX3DRadeonSuperResolution>
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public AMDRadeonSuperResolutionSetting(IADLX3DRadeonSuperResolution setting) : base(setting)
        {
        }

        public Tuple<int, int> GetSharpnessRange()
        {
            if (adlxSetting == null) return new Tuple<int, int>(0, 0);
            return AMDUtilities.GetIntRangeValue(adlxSetting.GetSharpnessRange);
        }

        public int GetSharpness()
        {
            if (adlxSetting == null) return 0;
            return AMDUtilities.GetIntValue(adlxSetting.GetSharpness);
        }

        public bool SetSharpness(int sharpness)
        {
            if (adlxSetting == null)
            {
                Logger.Warn("AMDRadeonSuperResolutionSetting.SetSharpness: adlxSetting is null (RSR may not be supported)");
                return false;
            }
            return adlxSetting.SetSharpness(sharpness) == ADLX_RESULT.ADLX_OK;
        }
    }
}
