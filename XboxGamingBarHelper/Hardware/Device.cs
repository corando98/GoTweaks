´╗┐namespace XboxGamingBarHelper.Hardware
{
    internal abstract class Device
    {
        public string Name { get; }
        public CPU CPU { get; }

        protected Device(string name, CPU cpu)
        {
            Name = name;
            CPU = cpu;
        }

        public virtual int GetMinTDP()
        {
            return CPU.MinTDP;
        }

        public virtual int GetMaxTDP()
        {
            return CPU.MaxTDP;
        }
    }
}
