using System.Collections.Generic;
using System.Drawing;

namespace XboxGamingBarHelper.RTSS.OSDItems
{
    internal class OSDItemFrametimeStats : OSDItem
    {
        public float Min { get; set; } = -1;
        public float Max { get; set; } = -1;
        public float Avg { get; set; } = -1;

        public OSDItemFrametimeStats() : base("Frametime", Color.Orange)
        {
        }

        protected override List<OSDItemValue> GetValues(int osdLevel)
        {
            var osdItems = base.GetValues(osdLevel);

            if (osdLevel >= 4)
            {
                if (Min >= 0) osdItems.Add(new OSDItemValue(Min, "ms", "min:", false));
                if (Max >= 0) osdItems.Add(new OSDItemValue(Max > 999 ? 999 : Max, "ms", "max:", false));
                if (Avg >= 0) osdItems.Add(new OSDItemValue(Avg > 999 ? 999 : Avg, "ms", "avg:", false));
            }

            return osdItems;
        }

        public override string GetOSDString(int osdLevel)
        {
            if (osdLevel < 4) return string.Empty;
            return base.GetOSDString(osdLevel);
        }
    }
}
