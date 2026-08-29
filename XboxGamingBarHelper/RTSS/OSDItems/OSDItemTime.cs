using System;
using System.Drawing;

using XboxGamingBarHelper.Performance;
namespace XboxGamingBarHelper.RTSS.OSDItems
{
    /// <summary>
    /// OSD item for displaying the current time. 12-hour (h:mm tt) by default,
    /// switchable to 24-hour (HH:mm) via the "Clock24Hour" OSD config flag.
    /// </summary>
    internal class OSDItemTime : OSDItem
    {
        // Set from RTSSManager when it parses the OSD config; default 12-hour.
        public bool Use24Hour { get; set; } = false;

        public OSDItemTime() : base("TIME", "Time", Color.White)
        {
        }

        public override string GetOSDString(int osdLevel)
        {
            var now = DateTime.Now;
            var timeString = now.ToString(Use24Hour ? "HH:mm" : "h:mm tt"); // "14:30" or "2:30 PM"
            return $"<C={GetTextColorWithOpacity()}>{timeString}";
        }
    }
}
