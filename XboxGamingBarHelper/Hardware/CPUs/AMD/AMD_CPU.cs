´╗┐namespace XboxGamingBarHelper.Hardware.CPUs.AMD
{
    internal abstract class AMD_CPU : CPU
    {
        protected AMD_CPU(string name, int minTDP, int maxTDP) : base(name, minTDP, maxTDP)
        {
        }
    }
}
