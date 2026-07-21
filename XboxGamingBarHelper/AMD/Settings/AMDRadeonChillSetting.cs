using NLog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace XboxGamingBarHelper.AMD.Settings
{
    internal class AMDRadeonChillSetting : AMDSetting<IADLX3DChill>
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public AMDRadeonChillSetting(IADLX3DChill setting) : base(setting)
        {

        }

        public Tuple<int, int> GetFPSRange()
        {
            if (adlxSetting == null) return new Tuple<int, int>(0, 0);
            return AMDUtilities.GetIntRangeValue(adlxSetting.GetFPSRange);
        }

        public int GetMinFPS()
        {
            if (adlxSetting == null) return 0;
            return AMDUtilities.GetIntValue(adlxSetting.GetMinFPS);
        }

        public int GetMaxFPS()
        {
            if (adlxSetting == null) return 0;
            return AMDUtilities.GetIntValue(adlxSetting.GetMaxFPS);
        }

        public bool SetMinFPS(int minFPS)
        {
            if (adlxSetting == null)
            {
                Logger.Warn("AMDRadeonChillSetting.SetMinFPS: adlxSetting is null (Radeon Chill may not be supported)");
                return false;
            }
            return adlxSetting.SetMinFPS(minFPS) == ADLX_RESULT.ADLX_OK;
        }

        public bool SetMaxFPS(int maxFPS)
        {
            if (adlxSetting == null)
            {
                Logger.Warn("AMDRadeonChillSetting.SetMaxFPS: adlxSetting is null (Radeon Chill may not be supported)");
                return false;
            }
            return adlxSetting.SetMaxFPS(maxFPS) == ADLX_RESULT.ADLX_OK;
        }
    }
}
