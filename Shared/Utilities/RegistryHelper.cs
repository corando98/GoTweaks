´╗┐using Microsoft.Win32;

namespace Shared.Utilities
{
    internal static class RegistryHelper
    {
        public static string ReadStringValue(RegistryKey registryKey, string subKey, string valueName)
        {
            var key = registryKey.OpenSubKey(subKey);
            if (key == null)
            {
                return string.Empty;
            }

            var value = key.GetValue(valueName);
            if (value == null || !(value is string))
            {
                key.Dispose();
                return string.Empty;
            }

            key.Dispose();
            return (string)value;
        }
    }
}
