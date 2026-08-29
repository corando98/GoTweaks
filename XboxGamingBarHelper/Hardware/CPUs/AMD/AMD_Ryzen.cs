´╗┐namespace XboxGamingBarHelper.Hardware.CPUs.AMD
{
    internal abstract class AMD_Ryzen : AMD_CPU
    {
        protected AMD_Ryzen(string name, int minTDP, int maxTDP) : base(name, minTDP, maxTDP)
        {
        }
    }
}
