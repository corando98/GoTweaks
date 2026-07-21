using NLog;
using System;
using System.Management.Instrumentation;
using System.Reflection;
using System.Runtime.InteropServices;

namespace XboxGamingBarHelper.AMD
{
    internal class AMDSetting<SettingType> : IDisposable where SettingType : IADLXInterface
    {
        protected static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        protected SettingType adlxSetting;
        public SettingType ADLXSetting { get {  return adlxSetting; } }

        protected GetBool isSupportedFunction;
        protected GetBool isEnabledFunction;
        protected MethodInfo setEnabledMethod;

        public AMDSetting(SettingType setting)
        {
            adlxSetting = setting;

            var settingType = typeof(SettingType);
            var isSupportedMethodInfo = settingType.GetMethod("IsSupported", BindingFlags.Public | BindingFlags.Instance);
            if (isSupportedMethodInfo == null)
            {
                Logger.Warn($"The method 'IsSupported' was not found in type '{settingType.Name}'.");
                isSupportedFunction = null;
            }
            else
            {
                isSupportedFunction = (GetBool)Delegate.CreateDelegate(typeof(GetBool), adlxSetting, isSupportedMethodInfo);
            }

            var isEnabledMethodInfo = settingType.GetMethod("IsEnabled", BindingFlags.Public | BindingFlags.Instance);
            if (isEnabledMethodInfo == null)
            {
                Logger.Warn($"The method 'IsEnabled' was not found in type '{settingType.Name}'.");
                isEnabledFunction = null;
            }
            else
            {
                isEnabledFunction = (GetBool)Delegate.CreateDelegate(typeof(GetBool), adlxSetting, isEnabledMethodInfo);
            }

            setEnabledMethod = settingType.GetMethod("SetEnabled", BindingFlags.Public | BindingFlags.Instance);
        }

        public virtual bool IsSupported()
        {
            if (isSupportedFunction == null)
            {
                Logger.Warn($"{GetType().Name} IsSupported function is not available.");
                return false;
            }

            return AMDUtilities.GetBoolValue(isSupportedFunction);
        }

        public virtual bool IsEnabled()
        {
            if (isEnabledFunction == null)
            {
                Logger.Warn($"{GetType().Name} IsEnabled function is not available.");
                return false;
            }

            return AMDUtilities.GetBoolValue(isEnabledFunction);
        }

        /// <summary>
        /// Returns true only when ADLX confirms the write with ADLX_OK - callers must not treat
        /// the reflected invoke succeeding (no exception) as proof the setting was actually
        /// applied, since ADLX reports its own result code independent of the .NET call.
        /// </summary>
        public virtual bool SetEnabled(bool enabled)
        {
            if (setEnabledMethod == null)
            {
                Logger.Warn($"{GetType().Name} SetEnabled method is not available.");
                return false;
            }

            object result = setEnabledMethod.Invoke(adlxSetting, new object[1] { enabled });
            if (result is ADLX_RESULT adlxResult)
            {
                if (adlxResult == ADLX_RESULT.ADLX_OK) return true;
                Logger.Error($"{GetType().Name} SetEnabled({enabled}) returned {adlxResult}.");
                return false;
            }

            // The reflected method didn't return an ADLX_RESULT (unexpected for the SWIG-generated
            // interfaces this is used against) - no result code to check, assume success rather
            // than falsely reporting failure for a case that was never actually observed.
            return true;
        }

        ~AMDSetting()
        {
            adlxSetting?.Dispose();
        }

        public virtual int Release()
        {
            if (adlxSetting == null) return 0;

            return adlxSetting.Release();
        }

        public void Dispose()
        {
            adlxSetting?.Dispose();
        }
    }
}
