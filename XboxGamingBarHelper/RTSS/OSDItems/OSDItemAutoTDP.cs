using System.Drawing;
using XboxGamingBarHelper.AutoTDP;

using XboxGamingBarHelper.Performance;
namespace XboxGamingBarHelper.RTSS.OSDItems
{
    internal class OSDItemAutoTDP : OSDItem
    {
        private AutoTDPManager autoTDPManager;

        public OSDItemAutoTDP() : base("AutoTDP", "AutoTDP", Color.FromArgb(0x00, 0xBF, 0xFF)) // DeepSkyBlue
        {
        }

        public void SetAutoTDPManager(AutoTDPManager manager)
        {
            autoTDPManager = manager;
        }

        public override string GetOSDString(int osdLevel)
        {
            // Only show when AutoTDP is enabled
            if (autoTDPManager == null || !autoTDPManager.Enabled.Value)
            {
                return string.Empty;
            }

            var statusText = autoTDPManager.StatusText;
            var targetFPS = autoTDPManager.TargetFPS.Value;
            var currentFPS = autoTDPManager.CurrentFPS.Value;
            var currentTDP = autoTDPManager.CurrentTDPValue;
            var newTDP = autoTDPManager.NewTDPValue;
            var isProbing = autoTDPManager.IsProbing;
            var sweetSpotTDP = autoTDPManager.SweetSpotTDP;
            var sweetSpotConfidence = autoTDPManager.SweetSpotConfidence;
            var isMLMode = autoTDPManager.IsMLModeActive;
            var mlReward = autoTDPManager.LastMLReward;
            var mlUpdates = autoTDPManager.MLUpdateCount;
            var mlExploration = autoTDPManager.MLExplorationPercent;
            var mlCumulativeReward = autoTDPManager.MLCumulativeReward;
            var mlAvgReward = autoTDPManager.MLAverageReward;

            if (string.IsNullOrEmpty(statusText))
            {
                return string.Empty;
            }

            // Determine display status (probing takes precedence)
            string displayStatus = isProbing ? "Probing" : statusText;

            // Check if we're locked on sweet spot
            bool isLocked = displayStatus.StartsWith("Locked");

            // Get text color with opacity for OLED protection
            var tc = GetTextColorWithOpacity();

            // Color code based on status - apply opacity for OLED protection
            string statusColor;
            if (isLocked)
                statusColor = ApplyOpacity("00FF00"); // Green for locked
            else if (displayStatus == "On target")
                statusColor = ApplyOpacity("00FF00"); // Green
            else if (displayStatus == "Below target" || displayStatus.StartsWith("Increasing"))
                statusColor = ApplyOpacity("FFFF00"); // Yellow
            else if (displayStatus == "Above target" || displayStatus.StartsWith("Decreasing"))
                statusColor = ApplyOpacity("00BFFF"); // Cyan
            else if (displayStatus == "Probing")
                statusColor = ApplyOpacity("FF00FF"); // Magenta for probing
            else
                statusColor = tc; // Use text color with opacity

            // Build OSD string
            // Format: AutoTDP 58/60 FPS 14W Locked 13W
            string modePrefix = isMLMode ? "[ML] " : "";
            var osdString = $"<C={ApplyOpacity(colorCode)}>{modePrefix}{name}<C={tc}> {currentFPS}/{targetFPS} FPS";

            // Show TDP change when increasing, decreasing, or probing
            if (displayStatus.StartsWith("Increasing") || displayStatus.StartsWith("Decreasing") || displayStatus == "Probing")
            {
                osdString += $" {currentTDP}W->{newTDP}W";
            }
            else if (currentTDP > 0)
            {
                // Show current TDP for other states
                osdString += $" {currentTDP}W";
            }

            osdString += $" <C={statusColor}>{displayStatus}<C={tc}>";

            // Show ML info when in ML mode
            if (isMLMode)
            {
                // Color code reward: green for positive, red for negative
                string rewardColor;
                if (mlReward >= 0)
                    rewardColor = ApplyOpacity("00FF00"); // Green for positive reward
                else if (mlReward > -10)
                    rewardColor = ApplyOpacity("FFFF00"); // Yellow for small negative
                else
                    rewardColor = ApplyOpacity("FF6600"); // Orange for large negative

                // Color code average reward
                string avgColor;
                if (mlAvgReward >= 0)
                    avgColor = ApplyOpacity("00FF00"); // Green
                else if (mlAvgReward > -5)
                    avgColor = ApplyOpacity("FFFF00"); // Yellow
                else
                    avgColor = ApplyOpacity("FF6600"); // Orange

                // Show: R:current Avg:recent Sum:cumulative #updates Exp%
                osdString += $" <C={rewardColor}>R:{mlReward:F1}<C={tc}>";
                osdString += $" <C={avgColor}>Avg:{mlAvgReward:F1}<C={tc}>";
                osdString += $" <C={ApplyOpacity("888888")}>Sum:{mlCumulativeReward:F0} #{mlUpdates} Exp:{mlExploration}%<C={tc}>";
            }
            // Show sweet spot if detected but not yet locked (PID mode only)
            else if (sweetSpotConfidence >= 60 && !isLocked)
            {
                osdString += $" <C={ApplyOpacity("888888")}>(sweet:{sweetSpotTDP}W)<C={tc}>";
            }

            return osdString;
        }
    }
}
