using NLog;
using System;

namespace XboxGamingBarHelper.AMD.Settings
{
    internal class AMDRadeonBoostSetting : AMDSetting<IADLX3DBoost>
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public AMDRadeonBoostSetting(IADLX3DBoost setting) : base(setting)
        {

        }

        public Tuple<int, int> GetResolutionRange()
        {
            if (adlxSetting == null) return new Tuple<int, int>(0, 0);
            return AMDUtilities.GetIntRangeValue(adlxSetting.GetResolutionRange);
        }

        public int GetResolution()
        {
            if (adlxSetting == null) return 0;
            return AMDUtilities.GetIntValue(adlxSetting.GetResolution);
        }

        public bool SetResolution(int resolution)
        {
            if (adlxSetting == null)
            {
                Logger.Warn("AMDRadeonBoostSetting.SetResolution: adlxSetting is null (Radeon Boost may not be supported)");
                return false;
            }
            return adlxSetting.SetResolution(resolution) == ADLX_RESULT.ADLX_OK;
        }
    }
}
