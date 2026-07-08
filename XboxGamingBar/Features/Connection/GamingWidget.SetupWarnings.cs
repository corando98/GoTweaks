using System;
using System.Collections.Generic;
using System.Linq;
using Windows.Storage;
using Windows.UI.Xaml;
using XboxGamingBar.Data;

namespace XboxGamingBar
{
    public sealed partial class GamingWidget
    {
        // The exact warning set (id list) the user dismissed. A NEW warning —
        // or a warning coming back after being resolved — changes the key and
        // resurfaces the banner; re-pushes of the same set stay hidden.
        private const string DismissedSetupWarningsKey = "DismissedSetupWarnings";

        private List<SetupWarningsProperty.Warning> currentSetupWarnings = new List<SetupWarningsProperty.Warning>();

        private void OnSetupWarningsChanged(List<SetupWarningsProperty.Warning> warnings)
        {
            try
            {
                currentSetupWarnings = warnings ?? new List<SetupWarningsProperty.Warning>();

                if (currentSetupWarnings.Count == 0)
                {
                    SetupWarningsBanner.Visibility = Visibility.Collapsed;
                    // All clear — forget the dismissal so a future recurrence shows again.
                    ApplicationData.Current.LocalSettings.Values.Remove(DismissedSetupWarningsKey);
                    return;
                }

                string key = string.Join(",", currentSetupWarnings.Select(w => w.Id).OrderBy(id => id));
                string dismissed = ApplicationData.Current.LocalSettings.Values[DismissedSetupWarningsKey] as string;
                if (string.Equals(key, dismissed, StringComparison.Ordinal))
                {
                    SetupWarningsBanner.Visibility = Visibility.Collapsed;
                    return;
                }

                var first = currentSetupWarnings[0];
                SetupWarningsText.Text = currentSetupWarnings.Count == 1
                    ? first.Message
                    : $"{first.Message} (+{currentSetupWarnings.Count - 1} more issue{(currentSetupWarnings.Count > 2 ? "s" : "")})";

                // One action button; when several warnings carry actions, the
                // first actionable one wins (the banner cycles as they resolve).
                var actionable = currentSetupWarnings.FirstOrDefault(w => !string.IsNullOrEmpty(w.Action));
                if (actionable?.Action == "pawnio")
                {
                    SetupWarningsActionButton.Content = "Install PawnIO";
                    SetupWarningsActionButton.Visibility = Visibility.Visible;
                }
                else if (actionable?.Action == "usbip")
                {
                    SetupWarningsActionButton.Content = "Install usbip-win2";
                    SetupWarningsActionButton.Visibility = Visibility.Visible;
                }
                else
                {
                    SetupWarningsActionButton.Visibility = Visibility.Collapsed;
                }
                SetupWarningsActionButton.IsEnabled = true;

                SetupWarningsBanner.Visibility = Visibility.Visible;
                Logger.Info($"[SETUP] Warning banner shown: {key}");
            }
            catch (Exception ex)
            {
                Logger.Warn($"OnSetupWarningsChanged failed: {ex.Message}");
            }
        }

        private void SetupWarningsActionButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var actionable = currentSetupWarnings.FirstOrDefault(w => !string.IsNullOrEmpty(w.Action));
                if (actionable?.Action == "pawnio")
                {
                    Logger.Info("[SETUP] Install PawnIO requested from warning banner");
                    installPawnIO?.TriggerInstall();
                    SetupWarningsActionButton.Content = "Installing...";
                    SetupWarningsActionButton.IsEnabled = false;
                    // Helper restarts itself after a successful install; the next
                    // SetupWarnings push re-evaluates and clears the banner.
                }
                else if (actionable?.Action == "usbip")
                {
                    Logger.Info("[SETUP] Install usbip-win2 requested from warning banner");
                    installUsbip?.TriggerInstall();
                    SetupWarningsActionButton.Content = "Installing...";
                    SetupWarningsActionButton.IsEnabled = false;
                    // Helper refreshes UsbipInstalled + re-pushes SetupWarnings when
                    // the installer exits; the banner updates (or clears) from that.
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"SetupWarningsActionButton_Click failed: {ex.Message}");
            }
        }

        private void UsbipInstallButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Logger.Info("[SETUP] Install usbip-win2 requested from CE-tab card");
                installUsbip?.TriggerInstall();
                UsbipInstallButton.Content = "Installing...";
                UsbipInstallButton.IsEnabled = false;
            }
            catch (Exception ex)
            {
                Logger.Warn($"UsbipInstallButton_Click failed: {ex.Message}");
            }
        }

        private void DismissSetupWarningsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string key = string.Join(",", currentSetupWarnings.Select(w => w.Id).OrderBy(id => id));
                ApplicationData.Current.LocalSettings.Values[DismissedSetupWarningsKey] = key;
                SetupWarningsBanner.Visibility = Visibility.Collapsed;
                Logger.Info($"[SETUP] Warning banner dismissed: {key}");
            }
            catch (Exception ex)
            {
                Logger.Warn($"DismissSetupWarningsButton_Click failed: {ex.Message}");
            }
        }
    }
}
