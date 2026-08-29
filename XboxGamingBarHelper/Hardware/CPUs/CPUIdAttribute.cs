using System;

namespace XboxGamingBarHelper.Hardware
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
    internal class CPUIdAttribute : Attribute
    {
        public string Id { get; }

        public CPUIdAttribute(string id)
        {
            Id = id;
        }
    }
}
