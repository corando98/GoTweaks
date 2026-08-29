using System.Drawing;

using XboxGamingBarHelper.Performance;
namespace XboxGamingBarHelper.RTSS.OSDItems
{
    /// <summary>
    /// OSD item for displaying the application name (D3D11, etc.)
    /// </summary>
    internal class OSDItemAppName : OSDItem
    {
        public OSDItemAppName() : base("App", "AppName", Color.Red)
        {
        }

        public override string GetOSDString(int osdLevel)
        {
            // Show application/API name (e.g., D3D11, D3D12, Vulkan) in red, then back to text color
            // Apply opacity for OLED protection
            return $"<C={ApplyOpacity("FF0000")}><APP><C={GetTextColorWithOpacity()}>";
        }
    }
}
