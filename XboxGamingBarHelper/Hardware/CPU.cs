namespace XboxGamingBarHelper.Hardware
{
    internal abstract class CPU
    {
        public string Name { get; }
        public int MinTDP { get; }
        public int MaxTDP { get; }

        protected CPU(string name, int minTDP, int maxTDP)
        {
            Name = name;
            MinTDP = minTDP;
            MaxTDP = maxTDP;
        }
    }
}
