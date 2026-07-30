using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media;

namespace XboxGamingBar
{
    public sealed partial class GamingWidget
    {
        // --- Setup tab: first-run tool status + install actions ---
        //
        // Aggregates the tool checks the helper already performs (usbip-win2 for
        // VIIPER emulation, HidHide, RTSS, PawnIO) into one guided page. The tab
        // itself only appears while something is missing: UpdateSetupTabStatus
        // drives the tab-settings device-availability layer, so a fully-installed
        // system never sees it and it auto-hides right after the last install
        // completes. ViGEm is deliberately absent — VIIPER/usbip replaced it.

        private static readonly SolidColorBrush SetupOkBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 96, 192, 96));
        private static readonly SolidColorBrush SetupMissingBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 200, 160, 64));

        // "Show Setup tab" override (System → Customization): keeps the tab visible
        // even when every tool is installed — the auto-hide otherwise makes the tab
        // unreachable on a completed system. Persisted in LocalSettings.
        private bool setupTabForced;

        private void ShowSetupTabToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (isLoadingTabPrefs) return;
            setupTabForced = ShowSetupTabToggle?.IsOn == true;
            try
            {
                Windows.Storage.ApplicationData.Current.LocalSettings.Values["SetupTabForced"] = setupTabForced;
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to persist SetupTabForced: {ex.Message}");
            }
            UpdateSetupTabStatus();
        }

        /// <summary>
        /// Refreshes all Setup rows from current property values and shows/hides the
        /// Setup tab. Wired to PropertyChanged of the four *Installed properties, so
        /// it re-runs on initial sync and whenever the helper reports an install
        /// finished. Safe to call from any thread.
        /// </summary>
        private void UpdateSetupTabStatus()
        {
            TryRunOnDispatcher(() =>
            {
                try
                {
                    bool usbipOk = usbipInstalled?.Value == true;
                    bool hidHideOk = hidHideInstalled?.Value == true;
                    bool rtssOk = rtssInstalled?.Value == true;
                    bool pawnIOOk = pawnIOInstalled?.Value == true;

                    UpdateSetupRow(SetupStatusUsbip, SetupInstallUsbipButton, usbipOk);
                    UpdateSetupRow(SetupStatusHidHide, SetupInstallHidHideButton, hidHideOk);
                    UpdateSetupRow(SetupStatusRtss, SetupInstallRtssButton, rtssOk);
                    UpdateSetupRow(SetupStatusPawnIO, SetupInstallPawnIOButton, pawnIOOk);

                    bool complete = usbipOk && hidHideOk && rtssOk && pawnIOOk;
                    SetNavTabDeviceAvailability("Setup", !complete || setupTabForced);
                }
                catch (Exception ex)
                {
                    Logger.Error($"UpdateSetupTabStatus failed: {ex.Message}");
                }
            }, "UpdateSetupTabStatus");
        }

        private void UpdateSetupRow(Windows.UI.Xaml.Controls.TextBlock status, Windows.UI.Xaml.Controls.Button action, bool installed)
        {
            if (status != null)
            {
                status.Text = installed ? "Installed" : "Not installed";
                status.Foreground = installed ? SetupOkBrush : SetupMissingBrush;
            }
            if (action != null)
            {
                action.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        private void SetupInstallUsbip_Click(object sender, RoutedEventArgs e)
        {
            if (SetupStatusUsbip != null) SetupStatusUsbip.Text = "Installing...";
            installUsbip?.TriggerInstall();
        }

        private void SetupInstallHidHide_Click(object sender, RoutedEventArgs e)
        {
            if (SetupStatusHidHide != null) SetupStatusHidHide.Text = "Installing...";
            installHidHide?.TriggerInstall();
        }

        private void SetupInstallPawnIO_Click(object sender, RoutedEventArgs e)
        {
            if (SetupStatusPawnIO != null) SetupStatusPawnIO.Text = "Installing...";
            installPawnIO?.TriggerInstall();
        }

        private async void SetupDownloadRtss_Click(object sender, RoutedEventArgs e)
        {
            // No silent-install path for RTSS (Guru3D installer only) — open the
            // official download page in the default browser.
            try
            {
                await Windows.System.Launcher.LaunchUriAsync(new Uri("https://www.guru3d.com/download/rtss-rivatuner-statistics-server-download/"));
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to launch RTSS download page: {ex.Message}");
            }
        }
    }
}
