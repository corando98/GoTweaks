using NLog;
using System;

namespace XboxGamingBarHelper.AMD.Settings
{
    internal class AMDImageSharpeningSetting : AMDSetting<IADLX3DImageSharpening>
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public AMDImageSharpeningSetting(IADLX3DImageSharpening setting) : base(setting)
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
                Logger.Warn("AMDImageSharpeningSetting.SetSharpness: adlxSetting is null (Image Sharpening may not be supported)");
                return false;
            }
            return adlxSetting.SetSharpness(sharpness) == ADLX_RESULT.ADLX_OK;
        }
    }
}
