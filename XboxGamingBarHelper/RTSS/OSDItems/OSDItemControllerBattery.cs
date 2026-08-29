using System;
using System.Collections.Generic;
using System.Drawing;

using XboxGamingBarHelper.Performance;
namespace XboxGamingBarHelper.RTSS.OSDItems
{
    internal class OSDItemControllerBattery : OSDItem
    {
        private Func<int> getLeftBattery;
        private Func<int> getRightBattery;
        private Func<bool> getLeftCharging;
        private Func<bool> getRightCharging;

        public OSDItemControllerBattery(Func<int> getLeftBattery, Func<int> getRightBattery,
            Func<bool> getLeftCharging = null, Func<bool> getRightCharging = null)
            : base("Ctrl", "ControllerBattery", Color.DarkCyan)
        {
            this.getLeftBattery = getLeftBattery;
            this.getRightBattery = getRightBattery;
            this.getLeftCharging = getLeftCharging;
            this.getRightCharging = getRightCharging;
        }

        public void SetCallbacks(Func<int> getLeftBattery, Func<int> getRightBattery,
            Func<bool> getLeftCharging, Func<bool> getRightCharging)
        {
            this.getLeftBattery = getLeftBattery;
            this.getRightBattery = getRightBattery;
            this.getLeftCharging = getLeftCharging;
            this.getRightCharging = getRightCharging;
        }

        protected override List<OSDItemValue> GetValues(int osdLevel)
        {
            var osdItems = base.GetValues(osdLevel);

            int leftBat = getLeftBattery?.Invoke() ?? -1;
            int rightBat = getRightBattery?.Invoke() ?? -1;

            bool leftConnected = leftBat > 0;
            bool rightConnected = rightBat > 0;

            if (leftConnected || rightConnected)
            {
                bool leftCharging = getLeftCharging?.Invoke() ?? false;
                bool rightCharging = getRightCharging?.Invoke() ?? false;

                if (leftConnected)
                {
                    string leftPrefix = leftCharging && leftBat < 100 ? "L+" : "L";
                    osdItems.Add(new OSDItemValue(leftBat, "%", leftPrefix, OSDValueType.PercentageInv));
                }

                if (rightConnected)
                {
                    string rightPrefix = rightCharging && rightBat < 100 ? "R+" : "R";
                    osdItems.Add(new OSDItemValue(rightBat, "%", rightPrefix, OSDValueType.PercentageInv));
                }
            }
            else
            {
                // Neither controller connected
                osdItems.Add(new OSDItemValue(0, "", "N/A", OSDValueType.None));
            }

            return osdItems;
        }
    }
}
