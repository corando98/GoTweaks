using System;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media;

namespace XboxGamingBar
{
    public sealed partial class GamingWidget
    {
        /// <summary>
        /// Shows or hides the "usbip-win2 required" warning card based on the
        /// emulation backend toggle and the helper-reported USBIP install status.
        /// Also refreshes the always-visible status line under the VIIPER toggle —
        /// added per issue #79 vvalente30 so users see the prereq state proactively
        /// (not just when they flip the toggle and discover input doesn't work).
        /// </summary>
        private async void UpdateUsbipCardVisibility()
        {
            if (emulationBackend == null || usbipInstalled == null)
            {
                return;
            }

            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                bool backendOn = emulationBackend.Value;
                bool installed = usbipInstalled.Value;

                if (UsbipInstallCard != null)
                {
                    UsbipInstallCard.Visibility = (backendOn && !installed)
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                }

                if (UsbipPrereqStatusLine != null)
                {
                    UsbipPrereqStatusLine.Text = installed
                        ? "usbip-win2 driver: detected ✓"
                        : "usbip-win2 driver: not detected — install + reboot required";
                    UsbipPrereqStatusLine.Foreground = installed
                        ? new SolidColorBrush(Color.FromArgb(0xFF, 0x66, 0xCC, 0x66))
                        : new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xC1, 0x07));
                }
            });
        }

        /// <summary>
        /// Swaps the Controller Emulation card body between the legacy ControllerEmulationContent
        /// and the new ViiperEmulationContent based on the backend toggle state. When expanded,
        /// only one of the two is visible at a time — they are not run concurrently.
        /// Also manages the Steam sub-device sub-panel visibility inside the VIIPER panel.
        /// </summary>
        private async void UpdateViiperConfigVisibility()
        {
            if (emulationBackend == null)
            {
                return;
            }

            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                bool backendOn = emulationBackend.Value;

                // Determine whether the Controller Emulation card is currently expanded.
                // ControllerEmulationContent's visibility is driven by ControllerEmulationExpandButton_Click;
                // we preserve that visibility state and only swap which body shows.
                bool cardExpanded = ControllerEmulationContent != null
                    && ControllerEmulationContent.Visibility == Visibility.Visible;

                if (backendOn)
                {
                    // VIIPER backend owns the card body.
                    if (ControllerEmulationContent != null) ControllerEmulationContent.Visibility = Visibility.Collapsed;
                    if (ViiperEmulationContent != null)
                    {
                        // Mirror the expand state. If the card was collapsed, keep VIIPER panel collapsed too.
                        ViiperEmulationContent.Visibility = cardExpanded || LastCardExpandedBeforeHide
                            ? Visibility.Visible
                            : Visibility.Collapsed;
                    }
                }
                else
                {
                    // Legacy backend owns the card body.
                    if (ViiperEmulationContent != null) ViiperEmulationContent.Visibility = Visibility.Collapsed;
                    if (ControllerEmulationContent != null)
                    {
                        ControllerEmulationContent.Visibility = cardExpanded || LastCardExpandedBeforeHide
                            ? Visibility.Visible
                            : Visibility.Collapsed;
                    }
                }

                if (viiperDeviceType != null)
                {
                    string t = viiperDeviceType.Value ?? string.Empty;
                    bool isSteam = t == "steam-generic" || t == "steam-controller" || t == "steamdeck-generic";
                    bool isSony = t == "sony";
                    bool isNintendo = t == "nintendo";
                    if (ViiperSteamSubDevicePanel != null)
                    {
                        ViiperSteamSubDevicePanel.Visibility = (backendOn && isSteam) ? Visibility.Visible : Visibility.Collapsed;
                    }
                    if (ViiperSonySubDevicePanel != null)
                    {
                        ViiperSonySubDevicePanel.Visibility = (backendOn && isSony) ? Visibility.Visible : Visibility.Collapsed;
                    }
                    if (ViiperNintendoSubDevicePanel != null)
                    {
                        ViiperNintendoSubDevicePanel.Visibility = (backendOn && isNintendo) ? Visibility.Visible : Visibility.Collapsed;
                    }
                    if (ViiperJoyconGyroPerHalfPanel != null)
                    {
                        // Per-half gyro only applies to the Joy-Con Pair (two physical IMUs → two halves).
                        bool isJoyconPair = isNintendo && (viiperNintendoSubDevice?.Value == "joycon-pair");
                        ViiperJoyconGyroPerHalfPanel.Visibility = (backendOn && isJoyconPair) ? Visibility.Visible : Visibility.Collapsed;
                    }
                }

                // Backend swap can change which body (legacy vs VIIPER) is currently
                // visible, so rebuild the System tab D-pad chain (ExpandButton →
                // EnabledToggle → first-body-item → …) for the new configuration.
                // Without this, gamepad navigation lands wherever the previous
                // backend's chain pointed.
                UpdateSystemControllerEmulationNavigation();
            });
        }

        // Track whether the user had the Controller Emulation card expanded so that we can
        // restore the expanded body after switching backends.
        private bool LastCardExpandedBeforeHide;
    }
}
