´╗┐using Shared.Enums;
using XboxGamingBarHelper.Core;

namespace XboxGamingBarHelper.Hardware
{
    internal class TDPControlSupportProperty : HelperProperty<bool, HardwareManager>
    {
        public TDPControlSupportProperty(bool inValue, HardwareManager inManager) : base(inValue, null, Function.Support_TDPControl, inManager)
        {
        }
    }
}
