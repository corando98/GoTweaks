using System;
using System.Threading.Tasks;
using Windows.UI.Xaml;

namespace XboxGamingBar
{
    /// <summary>
    /// Code-behind for the System tab's collapsible "Power &amp; Sleep" card (power
    /// button behavior, screen/hibernate idle timers, and the one-off Windows Sleep
    /// timer disable button). The AC/DC combo boxes themselves are wired up as
    /// PowerButtonActionProperty/IntTagComboProperty instances in GamingWidget.xaml.cs.
    /// </summary>
    public sealed partial class GamingWidget
    {
        private bool isPowerAndSleepExpanded = false;

        // Segoe Fluent Icons: ChevronDown / ChevronUp. Built via char-cast rather than a
        // \uXXXX string literal - private-use-area glyph literals have silently corrupted
        // via file-write tooling in this repo before.
        private static readonly string ChevronDownGlyph = ((char)0xE70D).ToString();
        private static readonly string ChevronUpGlyph = ((char)0xE70E).ToString();

        private void PowerAndSleepExpandButton_Click(object sender, RoutedEventArgs e)
        {
            isPowerAndSleepExpanded = !isPowerAndSleepExpanded;

            if (PowerAndSleepContent != null)
            {
                PowerAndSleepContent.Visibility = isPowerAndSleepExpanded ? Visibility.Visible : Visibility.Collapsed;
            }

            if (PowerAndSleepExpandIcon != null)
            {
                PowerAndSleepExpandIcon.Glyph = isPowerAndSleepExpanded ? ChevronUpGlyph : ChevronDownGlyph;
            }
        }

        /// <summary>
        /// Zeroes Windows' own "Sleep after" idle timeout for both AC and DC via the
        /// helper, so it doesn't put the system to Sleep before the GoTweaks Hibernate
        /// Timeout above ever fires. One-off action (Extra-keyed pipe message), no
        /// Function/property - mirrors the existing "Hibernate now" button pattern.
        /// </summary>
        private async void DisableSleepTimersButton_Click(object sender, RoutedEventArgs e)
        {
            if (!App.IsConnected)
                return;

            try
            {
                if (DisableSleepTimersButton != null) DisableSleepTimersButton.IsEnabled = false;

                var request = new Windows.Foundation.Collections.ValueSet();
                request.Add("DisableWindowsSleepTimers", true);
                await App.SendMessageAsync(request);

                Logger.Info("DisableWindowsSleepTimers request sent to helper");

                if (DisableSleepTimersStatusText != null)
                {
                    DisableSleepTimersStatusText.Text = "Windows Sleep timer disabled (AC+DC)";
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to disable Windows sleep timers: {ex.Message}");
                if (DisableSleepTimersStatusText != null)
                {
                    DisableSleepTimersStatusText.Text = "Failed - see log";
                }
            }
            finally
            {
                if (DisableSleepTimersButton != null) DisableSleepTimersButton.IsEnabled = true;
            }

            // Clear the confirmation text after a few seconds so it doesn't look stale
            // if the user comes back to this card later.
            await Task.Delay(4000);
            if (DisableSleepTimersStatusText != null)
            {
                DisableSleepTimersStatusText.Text = "";
            }
        }
    }
}
