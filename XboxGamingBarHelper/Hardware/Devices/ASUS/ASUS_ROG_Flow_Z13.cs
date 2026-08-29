namespace XboxGamingBarHelper.Hardware.Devices.ASUS
{
    [DeviceId("ASUS GZ302EA")]
    internal class ASUS_ROG_Flow_Z13 : ASUS_Device
    {
        public ASUS_ROG_Flow_Z13(CPU cpu) : base("ASUS ROG Flow Z13", cpu)
        {
        }

        public override int GetMaxTDP()
        {
            //return base.GetMaxTDP();
            return 93;
        }

        public override int GetMinTDP()
        {
            //return base.GetMinTDP();
            return 8;
        }
    }
}
