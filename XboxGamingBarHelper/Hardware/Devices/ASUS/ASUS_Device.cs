´╗┐namespace XboxGamingBarHelper.Hardware.Devices.ASUS
{
    internal class ASUS_Device : Device
    {
        public ASUS_Device(string name, CPU cpu) : base(name, cpu)
        {
        }

        public ASUS_Device(CPU cpu) : base("ASUS Device", cpu)
        {
        }
    }
}
