using System;

namespace Shared.Utilities
{
    public static class TypeHelper
    {
        public static bool IsStruct(this Type type)
        {
            return type.IsValueType && !type.IsPrimitive && !type.IsEnum && type != typeof(decimal);
        }

        public static bool IsStruct<T>()
        {
            return IsStruct(typeof(T));
        }
    }
}
