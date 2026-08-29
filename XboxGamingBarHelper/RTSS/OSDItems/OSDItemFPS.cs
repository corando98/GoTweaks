using XboxGamingBarHelper.Performance;
namespace XboxGamingBarHelper.RTSS.OSDItems
{
    internal class OSDItemFPS : OSDItem
    {
        public override string GetOSDString(int osdLevel)
        {
            return $"<C=FF0000><APP><C>{(osdLevel == 1 ? " " : (osdLevel == 2 ? "  " : "\t\t"))}<C=FFFFFF><FR><S=50> FPS<S><C>";
        }
    }
}
