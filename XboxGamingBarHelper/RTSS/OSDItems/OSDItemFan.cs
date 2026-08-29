using System.Collections.Generic;
using System.Drawing;
using XboxGamingBarHelper.Devices.Libraries.Legion;

using XboxGamingBarHelper.Performance;
namespace XboxGamingBarHelper.RTSS.OSDItems
{
    /// <summary>
    /// OSD item for displaying CPU fan speed (Legion Go only)
    /// </summary>
    internal class OSDItemFan : OSDItem
    {
        private LegionManager legionManager;

        public OSDItemFan() : base("FAN", "Fan", Color.Cyan)
        {
        }

        /// <summary>
        /// Sets the Legion Manager reference to read fan speed from.
        /// Must be called after LegionManager is initialized.
        /// </summary>
        public void SetLegionManager(LegionManager manager)
        {
            legionManager = manager;
        }

        protected override List<OSDItemValue> GetValues(int osdLevel)
        {
            var osdItems = base.GetValues(osdLevel);

            // Show fan speed if Legion is available
            if (legionManager != null)
            {
                int fanRpm = legionManager.GetCpuFanSpeed();
                if (fanRpm > 0)
                {
                    osdItems.Add(new OSDItemValue(fanRpm, "RPM", OSDValueType.Speed));
                }
            }

            return osdItems;
        }
    }
}
