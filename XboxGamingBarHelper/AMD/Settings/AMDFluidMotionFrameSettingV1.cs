using NLog;

namespace XboxGamingBarHelper.AMD.Settings
{
    /// <summary>
    /// AFMF 2.x extended controls (ADLX 1.5+): Algorithm, Search Mode, Performance Mode,
    /// Fast Motion Response. Wraps the IADLX3DAMDFluidMotionFrames1 interface obtained
    /// via QueryInterface from the v0 IADLX3DAMDFluidMotionFrames pointer. The v0
    /// interface is the IsSupported / IsEnabled / SetEnabled surface (already wrapped by
    /// AMDFluidMotionFrameSetting); this v1 surface only adds the dropdown-style getters
    /// and setters for the four extra Adrenalin controls.
    ///
    /// Older drivers that don't ship AFMF 2.x return null from QueryInterface — callers
    /// should null-check the wrapper before using any v1-specific control.
    /// </summary>
    internal class AMDFluidMotionFrameSettingV1
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private readonly IADLX3DAMDFluidMotionFrames1 adlxSetting;

        public AMDFluidMotionFrameSettingV1(IADLX3DAMDFluidMotionFrames1 setting)
        {
            adlxSetting = setting;
        }

        public bool IsAvailable => adlxSetting != null;

        public bool IsAlgorithmSupported()
        {
            if (adlxSetting == null) return false;
            var ptr = ADLX.new_boolP();
            try
            {
                if (adlxSetting.IsSupportedAlgorithm(ptr) != ADLX_RESULT.ADLX_OK)
                {
                    return false;
                }
                return ADLX.boolP_value(ptr);
            }
            finally
            {
                ADLX.delete_boolP(ptr);
            }
        }

        public ADLX_AFMF_ALGORITHM GetAlgorithm()
        {
            if (adlxSetting == null) return ADLX_AFMF_ALGORITHM.AFMF_ALGORITHM_AUTO;
            var ptr = ADLX.new_afmfAlgorithmP();
            try
            {
                if (adlxSetting.GetAlgorithm(ptr) != ADLX_RESULT.ADLX_OK)
                {
                    return ADLX_AFMF_ALGORITHM.AFMF_ALGORITHM_AUTO;
                }
                return ADLX.afmfAlgorithmP_value(ptr);
            }
            finally
            {
                ADLX.delete_afmfAlgorithmP(ptr);
            }
        }

        public bool SetAlgorithm(ADLX_AFMF_ALGORITHM value)
        {
            if (adlxSetting == null)
            {
                Logger.Warn("AMDFluidMotionFrameSettingV1.SetAlgorithm: adlxSetting is null (AFMF 2.x not supported on this driver)");
                return false;
            }
            var result = adlxSetting.SetAlgorithm(value);
            if (result != ADLX_RESULT.ADLX_OK)
                Logger.Error($"AMDFluidMotionFrameSettingV1.SetAlgorithm({value}) returned {result}.");
            return result == ADLX_RESULT.ADLX_OK;
        }

        public ADLX_AFMF_SEARCH_MODE_TYPE GetSearchMode()
        {
            if (adlxSetting == null) return ADLX_AFMF_SEARCH_MODE_TYPE.AFMF_SEARCH_MODE_AUTO;
            var ptr = ADLX.new_afmfSearchModeP();
            try
            {
                if (adlxSetting.GetSearchMode(ptr) != ADLX_RESULT.ADLX_OK)
                {
                    return ADLX_AFMF_SEARCH_MODE_TYPE.AFMF_SEARCH_MODE_AUTO;
                }
                return ADLX.afmfSearchModeP_value(ptr);
            }
            finally
            {
                ADLX.delete_afmfSearchModeP(ptr);
            }
        }

        public bool SetSearchMode(ADLX_AFMF_SEARCH_MODE_TYPE value)
        {
            if (adlxSetting == null)
            {
                Logger.Warn("AMDFluidMotionFrameSettingV1.SetSearchMode: adlxSetting is null");
                return false;
            }
            var result = adlxSetting.SetSearchMode(value);
            // ADLX_ALREADY_ENABLED means the driver is already at the requested value (confirmed
            // via on-device logging - this specific driver returns it for AFMF_SEARCH_MODE_AUTO
            // every time, not just on a genuine no-op), same false-failure pattern already fixed
            // for AMDSetting.SetEnabled - not a real error.
            if (result != ADLX_RESULT.ADLX_OK && result != ADLX_RESULT.ADLX_ALREADY_ENABLED)
                Logger.Error($"AMDFluidMotionFrameSettingV1.SetSearchMode({value}) returned {result}.");
            return result == ADLX_RESULT.ADLX_OK || result == ADLX_RESULT.ADLX_ALREADY_ENABLED;
        }

        public ADLX_AFMF_PERFORMANCE_MODE_TYPE GetPerformanceMode()
        {
            if (adlxSetting == null) return ADLX_AFMF_PERFORMANCE_MODE_TYPE.AFMF_PERFORMANCE_MODE_AUTO;
            var ptr = ADLX.new_afmfPerformanceModeP();
            try
            {
                if (adlxSetting.GetPerformanceMode(ptr) != ADLX_RESULT.ADLX_OK)
                {
                    return ADLX_AFMF_PERFORMANCE_MODE_TYPE.AFMF_PERFORMANCE_MODE_AUTO;
                }
                return ADLX.afmfPerformanceModeP_value(ptr);
            }
            finally
            {
                ADLX.delete_afmfPerformanceModeP(ptr);
            }
        }

        public bool SetPerformanceMode(ADLX_AFMF_PERFORMANCE_MODE_TYPE value)
        {
            if (adlxSetting == null)
            {
                Logger.Warn("AMDFluidMotionFrameSettingV1.SetPerformanceMode: adlxSetting is null");
                return false;
            }
            var result = adlxSetting.SetPerformanceMode(value);
            // See SetSearchMode - same ADLX_ALREADY_ENABLED false-failure, confirmed on-device.
            if (result != ADLX_RESULT.ADLX_OK && result != ADLX_RESULT.ADLX_ALREADY_ENABLED)
                Logger.Error($"AMDFluidMotionFrameSettingV1.SetPerformanceMode({value}) returned {result}.");
            return result == ADLX_RESULT.ADLX_OK || result == ADLX_RESULT.ADLX_ALREADY_ENABLED;
        }

        public ADLX_AFMF_FAST_MOTION_RESP GetFastMotionResponse()
        {
            if (adlxSetting == null) return ADLX_AFMF_FAST_MOTION_RESP.AFMF_RESP_REPEAT_FRAMES;
            var ptr = ADLX.new_afmfFastMotionRespP();
            try
            {
                if (adlxSetting.GetFastMotionResponse(ptr) != ADLX_RESULT.ADLX_OK)
                {
                    return ADLX_AFMF_FAST_MOTION_RESP.AFMF_RESP_REPEAT_FRAMES;
                }
                return ADLX.afmfFastMotionRespP_value(ptr);
            }
            finally
            {
                ADLX.delete_afmfFastMotionRespP(ptr);
            }
        }

        public bool SetFastMotionResponse(ADLX_AFMF_FAST_MOTION_RESP value)
        {
            if (adlxSetting == null)
            {
                Logger.Warn("AMDFluidMotionFrameSettingV1.SetFastMotionResponse: adlxSetting is null");
                return false;
            }
            var result = adlxSetting.SetFastMotionResponse(value);
            // See SetSearchMode - same ADLX_ALREADY_ENABLED false-failure, confirmed on-device.
            if (result != ADLX_RESULT.ADLX_OK && result != ADLX_RESULT.ADLX_ALREADY_ENABLED)
                Logger.Error($"AMDFluidMotionFrameSettingV1.SetFastMotionResponse({value}) returned {result}.");
            return result == ADLX_RESULT.ADLX_OK || result == ADLX_RESULT.ADLX_ALREADY_ENABLED;
        }
    }
}
