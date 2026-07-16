using Microsoft.Gaming.XboxGameBar;
using Microsoft.Gaming.XboxGameBar.Input;
using Microsoft.UI.Xaml.Controls;
using NLog;
using Shared.Data;
using Shared.Utilities;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Data.Json;
using Windows.Foundation;
using Windows.Foundation.Metadata;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;
using Windows.System.Power;
using Windows.Storage;
using Windows.System;
using Windows.UI.Xaml.Input;
using System.Runtime.InteropServices;
using Windows.UI;
using XboxGamingBar.Data;
using XboxGamingBar.Event;
using XboxGamingBar.IPC;
using XboxGamingBar.QuickSettings;
using Shared.Enums;

namespace XboxGamingBar
{
    public sealed partial class GamingWidget
    {
        /// <summary>
        /// Shows or hides the Legion tab based on device detection
        /// </summary>
        private void SetLegionTabVisibility(bool visible)
        {
            if (LegionNavItem != null)
            {
                LegionNavItem.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                Logger.Info($"Legion tab visibility set to: {visible}");
            }

            // TDP Mode card is always visible for all devices
            // Legion devices: uses hardware presets (Quiet/Balanced/Performance/Custom)
            // Generic devices: uses TDP value presets (8W/15W/25W/Custom)
            if (TDPModeCard != null)
            {
                TDPModeCard.Visibility = Visibility.Visible;
                Logger.Info($"TDP Mode card visibility set to: Visible (Legion={visible})");

                // Update XY focus bindings - TDP Mode card is always present now
                UpdatePerformanceTabXYFocus(true);

                // Sync TDP Mode with Legion Performance Mode if Legion device
                // Skip during initial sync - ApplyProfileTDPToHelper will set the correct value
                if (visible && LegionPerformanceModeComboBox != null && TDPModeComboBox != null && !isInitialSync)
                {
                    TDPModeComboBox.SelectedIndex = LegionPerformanceModeComboBox.SelectedIndex;
                }
            }

            // Show/hide Manufacturer WMI option in TDP Method dropdown based on Legion detection
            if (TdpMethodWmiItem != null)
            {
                TdpMethodWmiItem.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                Logger.Info($"TDP Method WMI option visibility set to: {visible}");

                // Auto-default only BEFORE the helper has supplied a value. After
                // helper sync, the helper's persisted choice is authoritative — its
                // queued NotifyPropertyChanged dispatcher entry will set SelectedIndex.
                // Racing it with SelectedIndex=0 here would push WMI=0 UP and clobber
                // the helper's PawnIO selection (issue #79 round-3 regression).
                bool helperOwnsTdpMethod = tdpMethod != null && tdpMethod.HasReceivedHelperSync;

                // If Legion detected and WMI option now visible, select it if not already selected.
                // Skip when helper owns the value — its sync will land the right index momentarily.
                if (visible && TdpMethodComboBox != null && TdpMethodComboBox.SelectedIndex < 0 && !helperOwnsTdpMethod)
                {
                    TdpMethodComboBox.SelectedIndex = 0; // ManufacturerWMI
                }
                // If Legion not detected and WMI was selected, switch to PawnIO.
                // Same rationale: helper-driven values must not be overridden here.
                else if (!visible && TdpMethodComboBox != null && !helperOwnsTdpMethod)
                {
                    var selectedItem = TdpMethodComboBox.SelectedItem as ComboBoxItem;
                    if (selectedItem?.Tag is string tag && tag == "ManufacturerWMI")
                    {
                        // Find and select PawnIO
                        for (int i = 0; i < TdpMethodComboBox.Items.Count; i++)
                        {
                            if (TdpMethodComboBox.Items[i] is ComboBoxItem item && item.Tag is string t && t == "PawnIO")
                            {
                                TdpMethodComboBox.SelectedIndex = i;
                                break;
                            }
                        }
                    }
                }
            }

            // Update the TDP Method description based on Legion detection
            if (TdpMethodDescription != null)
            {
                if (visible)
                {
                    TdpMethodDescription.Text = "Select TDP control method. Manufacturer WMI (Legion) and PawnIO are anti-cheat safe.";
                }
                else
                {
                    TdpMethodDescription.Text = "Select TDP control method. PawnIO is anti-cheat safe. WinRing0 may trigger anti-cheat.";
                }
            }

            // Refresh Quick Settings tiles to show/hide Legion-specific tiles
            RefreshQuickSettingsForLegion();
        }

        /// <summary>
        /// Updates the device name display in the Legion tab header.
        /// </summary>
        private void SetLegionDeviceName(string name)
        {
            if (LegionDeviceNameText != null && !string.IsNullOrEmpty(name))
            {
                LegionDeviceNameText.Text = name;
                Logger.Info($"Legion device name set to: {name}");
            }
        }

        /// <summary>
        /// Shows or hides the Controller Remapping section based on device support.
        /// Legion Go S has a different HID structure, so controller remapping doesn't work.
        /// </summary>
        private void SetControllerRemappingSectionVisibility(bool visible)
        {
            if (ControllerRemappingSection != null)
            {
                ControllerRemappingSection.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                Logger.Info($"Controller Remapping section visibility set to: {visible}");

                if (visible)
                {
                    RefreshLegionEnhancedRemapUi();
                }
            }
        }

        /// <summary>
        /// Shows or hides the Lighting section based on device support.
        /// Legion Go S has a different HID structure, so RGB lighting control doesn't work.
        /// </summary>
        private void SetLightingSectionVisibility(bool visible)
        {
            if (LightingSection != null)
            {
                LightingSection.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                Logger.Info($"Lighting section visibility set to: {visible}");
            }
        }

        /// <summary>
        /// Shows or hides the Gyro Settings card based on device support.
        /// Legion Go S has a different HID structure, so gyro configuration doesn't work.
        /// </summary>
        private void SetGyroSectionVisibility(bool visible)
        {
            if (GyroSection != null)
            {
                GyroSection.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                Logger.Info($"Gyro section visibility set to: {visible}");
            }
        }

        /// <summary>
        /// Fires a one-shot "CalibrateLegionGyro" pipe message. The helper sends the
        /// HID output report to both controllers; the controller firmware captures the
        /// new bias while the user holds the pads still.
        /// </summary>
        private async void LegionCalibrateGyroButton_Click(object sender, Windows.UI.Xaml.RoutedEventArgs e)
        {
            Logger.Info("Legion gyro calibration requested from widget");

            // Brief UI feedback: disable the button while the request is in flight.
            Button button = LegionCalibrateGyroButton;
            string originalText = button?.Content?.ToString() ?? "Calibrate gyro";
            if (button != null) { button.IsEnabled = false; button.Content = "Calibrating…"; }
            if (LegionCalibrateGyroStatus != null)
                LegionCalibrateGyroStatus.Text = "Hold the controllers still…";

            try
            {
                if (App.PipeClient?.IsConnected == true)
                {
                    var request = new Windows.Foundation.Collections.ValueSet();
                    request.Add("CalibrateLegionGyro", true);
                    await App.SendMessageAsync(request);
                    if (LegionCalibrateGyroStatus != null)
                        LegionCalibrateGyroStatus.Text = "Calibration command sent. Keep the pads still for a moment.";
                }
                else
                {
                    if (LegionCalibrateGyroStatus != null)
                        LegionCalibrateGyroStatus.Text = "Not connected to helper — try again after reconnecting.";
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"CalibrateLegionGyro send failed: {ex.Message}");
                if (LegionCalibrateGyroStatus != null)
                    LegionCalibrateGyroStatus.Text = $"Calibration send failed: {ex.Message}";
            }
            finally
            {
                await Task.Delay(1200);
                if (button != null) { button.IsEnabled = true; button.Content = originalText; }
            }
        }

        private void SetScrollWheelSectionVisibility(bool visible)
        {
            if (ScrollWheelSection != null)
            {
                ScrollWheelSection.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                Logger.Info($"Scroll wheel section visibility set to: {visible}");
            }
        }

        // ---- Driver Updates (Lenovo) --------------------------------------------------

        private void DriverUpdatesExpandToggle_Click(object sender, Windows.UI.Xaml.RoutedEventArgs e)
        {
            if (DriverUpdatesContent == null || DriverUpdatesExpandIcon == null) return;
            bool expand = DriverUpdatesExpandToggle?.IsChecked == true;
            DriverUpdatesContent.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
            // \uE70E = chevron down (collapsed), \uE70D = chevron up (expanded)
            DriverUpdatesExpandIcon.Glyph = expand ? "\uE70E" : "\uE70D";
        }

        private async void DriverUpdatesCheckButton_Click(object sender, Windows.UI.Xaml.RoutedEventArgs e)
        {
            if (DriverUpdatesCheckButton == null) return;

            string originalContent = DriverUpdatesCheckButton.Content?.ToString() ?? "Check for updates";
            DriverUpdatesCheckButton.IsEnabled = false;
            DriverUpdatesCheckButton.Content = "Checking…";
            if (DriverUpdatesStatusText != null)
                DriverUpdatesStatusText.Text = "Reading machine info and contacting Lenovo…";
            if (DriverUpdatesList != null)
                DriverUpdatesList.Visibility = Visibility.Collapsed;

            try
            {
                if (!App.IsConnected)
                {
                    if (DriverUpdatesStatusText != null)
                        DriverUpdatesStatusText.Text = "Helper not connected. Try again once the widget reconnects.";
                    return;
                }

                var request = new Windows.Foundation.Collections.ValueSet();
                request.Add("CheckDriverUpdates", true);
                var response = await App.SendMessageAsync(request);
                if (response == null)
                {
                    if (DriverUpdatesStatusText != null)
                        DriverUpdatesStatusText.Text = "No response from helper.";
                    return;
                }

                if (!response.TryGetValue("DriverUpdateResult", out var payloadObj) || !(payloadObj is string payload))
                {
                    if (DriverUpdatesStatusText != null)
                        DriverUpdatesStatusText.Text = "Helper returned an unexpected response shape.";
                    return;
                }

                RenderDriverUpdateResult(payload);
            }
            catch (Exception ex)
            {
                Logger.Warn($"DriverUpdatesCheckButton_Click failed: {ex.Message}");
                if (DriverUpdatesStatusText != null)
                    DriverUpdatesStatusText.Text = $"Check failed: {ex.Message}";
            }
            finally
            {
                DriverUpdatesCheckButton.IsEnabled = true;
                DriverUpdatesCheckButton.Content = originalContent;
            }
        }

        private async void DriverUpdatesOpenPageButton_Click(object sender, Windows.UI.Xaml.RoutedEventArgs e)
        {
            try
            {
                // If we already know the MT code from a previous Check, use that URL
                // directly; otherwise ask the helper for a fresh snapshot so we can
                // route to the correct machine-specific page.
                string url = _lastDriverPageUrl;
                if (string.IsNullOrEmpty(url) && App.IsConnected)
                {
                    var request = new Windows.Foundation.Collections.ValueSet();
                    request.Add("CheckDriverUpdates", true);
                    var response = await App.SendMessageAsync(request);
                    if (response != null && response.TryGetValue("DriverUpdateResult", out var payloadObj) && payloadObj is string payload)
                    {
                        RenderDriverUpdateResult(payload);
                        url = _lastDriverPageUrl;
                    }
                }
                if (string.IsNullOrEmpty(url))
                {
                    url = "https://pcsupport.lenovo.com/";
                }
                await Windows.System.Launcher.LaunchUriAsync(new Uri(url));
            }
            catch (Exception ex)
            {
                Logger.Warn($"DriverUpdatesOpenPageButton_Click failed: {ex.Message}");
                if (DriverUpdatesStatusText != null)
                    DriverUpdatesStatusText.Text = $"Couldn't open browser: {ex.Message}";
            }
        }

        private string _lastDriverPageUrl;

        /// <summary>
        /// Parses the helper's DriverUpdateResult JSON (shipped over the pipe as a
        /// camelCase JSON string) and writes it into the UI. Uses Windows.Data.Json
        /// because the UWP widget targets C# 7.3 without System.Text.Json. Defensive
        /// against missing fields — any field the helper couldn't populate shows "—".
        /// </summary>
        private async void RenderDriverUpdateResult(string json)
        {
            // Windows.Data.Json + Xaml property setters both require the UI
            // thread (WinRT single-apartment). This method is reachable from
            // the pipe-read threadpool path (PrefetchDriverUpdatesAsync /
            // UpdateDriverUpdatesTile on startup push) so force a dispatch
            // before we touch either. Without this the heartbeat-watcher
            // reconnect after a long UAC gap threw RPC_E_WRONG_THREAD and
            // left the driver list empty.
            if (Dispatcher != null && !Dispatcher.HasThreadAccess)
            {
                await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal,
                    () => RenderDriverUpdateResult(json));
                return;
            }
            try
            {
                if (!Windows.Data.Json.JsonObject.TryParse(json, out var root))
                {
                    if (DriverUpdatesStatusText != null)
                        DriverUpdatesStatusText.Text = "Helper returned a response the widget couldn't parse.";
                    return;
                }

                string GetStr(string name) =>
                    (root.TryGetValue(name, out var v) && v.ValueType == Windows.Data.Json.JsonValueType.String)
                        ? v.GetString() ?? "" : "";
                bool GetBool(string name) =>
                    root.TryGetValue(name, out var v) && v.ValueType == Windows.Data.Json.JsonValueType.Boolean && v.GetBoolean();

                string mt = GetStr("machineTypeCode");
                string model = GetStr("model");
                string modelVersion = GetStr("modelVersion");
                string bios = GetStr("biosVersion");
                string pageUrl = GetStr("driverPageUrl");
                bool isLenovo = GetBool("isLenovo");
                bool liveFetch = GetBool("liveFetchSucceeded");
                string error = GetStr("errorMessage");

                _lastDriverPageUrl = string.IsNullOrEmpty(pageUrl) ? "https://pcsupport.lenovo.com/" : pageUrl;
                if (DriverUpdatesMachineType != null) DriverUpdatesMachineType.Text = string.IsNullOrEmpty(mt) ? "—" : mt;
                if (DriverUpdatesModel != null)
                {
                    string modelText = string.IsNullOrEmpty(model) ? (string.IsNullOrEmpty(modelVersion) ? "—" : modelVersion) : model;
                    DriverUpdatesModel.Text = modelText;
                }
                if (DriverUpdatesBios != null) DriverUpdatesBios.Text = string.IsNullOrEmpty(bios) ? "—" : bios;

                if (!isLenovo)
                {
                    if (DriverUpdatesStatusText != null)
                        DriverUpdatesStatusText.Text = string.IsNullOrEmpty(error)
                            ? "This feature only works on Lenovo devices."
                            : $"Not a Lenovo device: {error}";
                    if (DriverUpdatesList != null) DriverUpdatesList.Visibility = Visibility.Collapsed;
                    return;
                }

                // Render the driver list if we got one. If Lenovo's API didn't respond
                // (live fetch failed or returned empty), fall back to prompting the
                // user to open the driver page in a browser.
                var items = new System.Collections.Generic.List<DriverDisplay>();
                if (root.TryGetValue("drivers", out var driversVal) && driversVal.ValueType == Windows.Data.Json.JsonValueType.Array)
                {
                    foreach (var elem in driversVal.GetArray())
                    {
                        if (elem.ValueType != Windows.Data.Json.JsonValueType.Object) continue;
                        var d = elem.GetObject();
                        string GetD(string key) =>
                            (d.TryGetValue(key, out var vv) && vv.ValueType == Windows.Data.Json.JsonValueType.String)
                                ? vv.GetString() ?? "" : "";
                        int statusCode = 0;
                        if (d.TryGetValue("updateStatus", out var usVal) && usVal.ValueType == Windows.Data.Json.JsonValueType.Number)
                            statusCode = (int)usVal.GetNumber();
                        string installed = GetD("installedVersion");
                        string downloadUrl = GetD("downloadUrl");
                        var (installLabel, installVis) = InstallButtonForStatus(statusCode, downloadUrl);
                        items.Add(new DriverDisplay
                        {
                            Name = GetD("name"),
                            Category = GetD("category"),
                            Version = GetD("version"),
                            ReleaseDate = GetD("releaseDate"),
                            DownloadUrl = downloadUrl,
                            Severity = SeverityLabel(GetD("severity")),
                            InstalledVersion = string.IsNullOrWhiteSpace(installed) ? "—" : installed,
                            StatusLabel = StatusLabelFor(statusCode),
                            StatusColor = StatusColorFor(statusCode),
                            InstallButtonLabel = installLabel,
                            InstallButtonVisibility = installVis,
                        });
                    }
                }

                // Remember the full list so the utilities/diagnostics checkbox
                // can filter it on the fly without another helper round-trip.
                _allDriverDisplays = items;

                // Restore checkbox state from LocalSettings (first render after
                // widget startup) — no-op on later renders since the state
                // already matches.
                if (DriverUpdatesShowUtilitiesCheckbox != null)
                {
                    var persisted = DriverUpdatesShowUtilities;
                    if ((DriverUpdatesShowUtilitiesCheckbox.IsChecked == true) != persisted)
                        DriverUpdatesShowUtilitiesCheckbox.IsChecked = persisted;
                }

                ApplyDriverFilters();
                UpdateUpdateAllButtonVisibility();

                if (DriverUpdatesStatusText != null)
                {
                    if (items.Count > 0)
                    {
                        int upToDate = 0, update = 0, unknown = 0, notInstalled = 0;
                        foreach (var it in items)
                        {
                            switch (it.StatusLabel)
                            {
                                case "Up to date": upToDate++; break;
                                case "Update": update++; break;
                                case "Not installed": notInstalled++; break;
                                default: unknown++; break;
                            }
                        }
                        DriverUpdatesStatusText.Text = $"{items.Count} drivers checked — {upToDate} up to date, {update} update available, {notInstalled} not installed, {unknown} unknown.";
                    }
                    else if (liveFetch)
                    {
                        DriverUpdatesStatusText.Text = "Lenovo returned no drivers for this machine type.";
                    }
                    else
                    {
                        DriverUpdatesStatusText.Text = "Lenovo's live driver list is unreachable. Use Open Lenovo driver page to browse on lenovo.com.";
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"RenderDriverUpdateResult parse failed: {ex.Message}");
                if (DriverUpdatesStatusText != null)
                    DriverUpdatesStatusText.Text = $"Couldn't parse response: {ex.Message}";
            }
        }

        private sealed class DriverDisplay
        {
            public string Name { get; set; }
            public string Category { get; set; }
            public string Version { get; set; }
            public string ReleaseDate { get; set; }
            public string DownloadUrl { get; set; }
            /// <summary>Human-readable severity label ("Critical", "Recommended", "Optional").</summary>
            public string Severity { get; set; }
            /// <summary>Installed driver version on this device (or "—" when unmatched).</summary>
            public string InstalledVersion { get; set; }
            /// <summary>Short badge label ("Up to date", "Update", "Not installed", "Unknown").</summary>
            public string StatusLabel { get; set; }
            /// <summary>Solid-color hex (#AARRGGBB) for the status pill background.</summary>
            public Windows.UI.Xaml.Media.Brush StatusColor { get; set; }
            /// <summary>"Install" when missing, "Update" when outdated — empty when neither applies (hides the button).</summary>
            public string InstallButtonLabel { get; set; }
            /// <summary>Visibility of the Install/Update button — hidden when the driver is up-to-date or has no download URL.</summary>
            public Windows.UI.Xaml.Visibility InstallButtonVisibility { get; set; }
        }

        // Cached full driver list so the "Show utilities and diagnostics"
        // checkbox can toggle visibility without re-querying the helper.
        // Populated by RenderDriverUpdateResult, consumed by ApplyDriverFilters.
        private System.Collections.Generic.List<DriverDisplay> _allDriverDisplays = new System.Collections.Generic.List<DriverDisplay>();

        // Categories suppressed by default. User toggles via the checkbox —
        // persisted in LocalSettings so the preference survives widget
        // restarts without needing a dedicated property.
        private static readonly System.Collections.Generic.HashSet<string> _lowSignalDriverCategories =
            new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
            {
                "Diagnostic",
                "Software and Utilities",
                "Tool",
            };

        private const string DriverUpdatesShowUtilitiesKey = "DriverUpdates_ShowUtilities";
        // Kept for back-compat — same storage key, new meaning: "CheckOnStart".
        // Historically this was "UpdateOnStart" (auto-install). Changed after
        // users asked for a way to skip the Lenovo check entirely — the name
        // stays so existing LocalSettings values carry over. Default true so
        // first-install users still see the banner after startup probe.
        private const string DriverUpdatesUpdateOnStartKey = "DriverUpdates_UpdateOnStart";
        private const string DriverUpdatesHideBannerKey    = "DriverUpdates_HideBanner";

        private static bool GetBoolSetting(string key, bool defaultValue)
        {
            try
            {
                var settings = Windows.Storage.ApplicationData.Current.LocalSettings;
                if (settings.Values.TryGetValue(key, out var v) && v is bool b) return b;
            }
            catch { }
            return defaultValue;
        }

        private static void SetBoolSetting(string key, bool value)
        {
            try { Windows.Storage.ApplicationData.Current.LocalSettings.Values[key] = value; }
            catch { }
        }

        private bool DriverUpdatesCheckOnStart
        {
            // Default true: users who haven't explicitly opted out expect the
            // helper's startup probe to populate the banner automatically.
            get => GetBoolSetting(DriverUpdatesUpdateOnStartKey, true);
            set => SetBoolSetting(DriverUpdatesUpdateOnStartKey, value);
        }
        private bool DriverUpdatesHideBanner
        {
            get => GetBoolSetting(DriverUpdatesHideBannerKey, false);
            set => SetBoolSetting(DriverUpdatesHideBannerKey, value);
        }

        private async void DriverUpdatesUpdateOnStartCheckbox_Changed(object sender, Windows.UI.Xaml.RoutedEventArgs e)
        {
            if (_isLoadingUpdatePreferenceCheckboxes) return;
            if (DriverUpdatesUpdateOnStartCheckbox == null) return;
            bool on = DriverUpdatesUpdateOnStartCheckbox.IsChecked == true;
            DriverUpdatesCheckOnStart = on;
            // Forward to helper so its next startup honours the toggle.
            // Helper persists via its own LocalSettingsHelper (separate store
            // from widget LocalSettings) and reads the value before running
            // the probe, so the following launch skips the Lenovo fetch when
            // the box is unchecked.
            try
            {
                if (App.IsConnected)
                {
                    var req = new Windows.Foundation.Collections.ValueSet();
                    req.Add("SetDriverCheckOnStart", on);
                    await App.SendMessageAsync(req);
                }
            }
            catch (Exception ex) { Logger.Warn($"SetDriverCheckOnStart forward failed: {ex.Message}"); }
        }

        private void DriverUpdatesHideBannerCheckbox_Changed(object sender, Windows.UI.Xaml.RoutedEventArgs e)
        {
            if (_isLoadingUpdatePreferenceCheckboxes) return;
            if (DriverUpdatesHideBannerCheckbox == null) return;
            DriverUpdatesHideBanner = DriverUpdatesHideBannerCheckbox.IsChecked == true;
            // Re-evaluate tile visibility right now so the setting has an
            // immediate effect without waiting for the next helper push.
            if (QuickDriverUpdatesTile != null && DriverUpdatesHideBanner)
            {
                QuickDriverUpdatesTile.Visibility = Windows.UI.Xaml.Visibility.Collapsed;
            }
        }

        private bool DriverUpdatesShowUtilities
        {
            get
            {
                try
                {
                    var settings = Windows.Storage.ApplicationData.Current.LocalSettings;
                    if (settings.Values.TryGetValue(DriverUpdatesShowUtilitiesKey, out var v) && v is bool b) return b;
                }
                catch { }
                return false;
            }
            set
            {
                try
                {
                    Windows.Storage.ApplicationData.Current.LocalSettings.Values[DriverUpdatesShowUtilitiesKey] = value;
                }
                catch { }
            }
        }

        /// <summary>
        /// Called from the pipe-message handler when the helper pushes a
        /// startup-probe result. Shows/hides the Quick-tab tile and updates
        /// the count badge on the UI thread.
        /// </summary>
        internal async void UpdateDriverUpdatesTile(int count)
        {
            try
            {
                bool hideBanner = DriverUpdatesHideBanner;
                bool checkOnStart = DriverUpdatesCheckOnStart;

                await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                {
                    if (QuickDriverUpdatesTile == null) return;
                    // Sync the checkboxes themselves with persisted state so
                    // the user sees the current setting on first render.
                    if (DriverUpdatesUpdateOnStartCheckbox != null && DriverUpdatesUpdateOnStartCheckbox.IsChecked != checkOnStart)
                        DriverUpdatesUpdateOnStartCheckbox.IsChecked = checkOnStart;
                    if (DriverUpdatesHideBannerCheckbox != null && DriverUpdatesHideBannerCheckbox.IsChecked != hideBanner)
                        DriverUpdatesHideBannerCheckbox.IsChecked = hideBanner;

                    bool visible = count > 0
                                   && legionGoDetected != null
                                   && legionGoDetected.Value
                                   && !hideBanner;
                    QuickDriverUpdatesTile.Visibility = visible
                        ? Windows.UI.Xaml.Visibility.Visible
                        : Windows.UI.Xaml.Visibility.Collapsed;
                    if (QuickDriverUpdatesCountText != null)
                        QuickDriverUpdatesCountText.Text = count.ToString();
                    if (QuickDriverUpdatesTitleText != null)
                        QuickDriverUpdatesTitleText.Text = count == 1
                            ? "1 driver update available"
                            : count + " driver updates available";

                    // Keep the in-card "Update all" button in sync with the
                    // same count — it's visible whenever there's at least one
                    // installable row (Install or Update).
                    UpdateUpdateAllButtonVisibility();
                });

                // Pre-populate the driver list so a later tile/tab click shows
                // it instantly instead of requiring another Check-for-updates
                // press. Helper serves this from its startup-probe cache so
                // there's no Lenovo round-trip here.
                if (count > 0 && (_allDriverDisplays == null || _allDriverDisplays.Count == 0))
                {
                    await PrefetchDriverUpdatesAsync();
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"UpdateDriverUpdatesTile failed: {ex.Message}");
            }
        }

        /// <summary>
        /// A row qualifies for "Update all" only when:
        ///   1. It has UpdateAvailable status (not NotInstalled — installing
        ///      drivers Windows doesn't have yet would be surprising, and the
        ///      user wasn't asking for that).
        ///   2. It's currently visible in the list — the utilities/diagnostics
        ///      filter + Tool category exclusion on the card shouldn't leak
        ///      into a bulk install. User's previous run launched all 24
        ///      candidate installers at once, including hidden ones, which
        ///      was the opposite of what they asked for.
        /// </summary>
        private bool IsUpdateAllCandidate(DriverDisplay d)
        {
            if (d == null) return false;
            // Status label is set by StatusLabelFor — "Update" is the
            // UpdateAvailable case, "Install" is NotInstalled, "Up to date"
            // is UpToDate, "Unknown" otherwise.
            if (!string.Equals(d.StatusLabel, "Update", StringComparison.Ordinal)) return false;
            if (string.IsNullOrWhiteSpace(d.DownloadUrl)) return false;
            // Respect the "Show utilities and diagnostics" checkbox: when
            // unchecked we hide those categories + Tool from the list, so
            // Update-all must hide them too.
            if (!DriverUpdatesShowUtilities && _lowSignalDriverCategories.Contains(d.Category ?? "")) return false;
            return true;
        }

        /// <summary>
        /// Shows "Update all" button when at least one visible row is an
        /// actionable UpdateAvailable (the same set IsUpdateAllCandidate
        /// returns true for — keeps the button visibility consistent with
        /// what the button would actually install).
        /// </summary>
        private void UpdateUpdateAllButtonVisibility()
        {
            if (DriverUpdatesUpdateAllButton == null) return;
            int updateCount = 0;
            foreach (var d in _allDriverDisplays)
            {
                if (IsUpdateAllCandidate(d)) updateCount++;
            }
            DriverUpdatesUpdateAllButton.Visibility = updateCount > 0
                ? Windows.UI.Xaml.Visibility.Visible
                : Windows.UI.Xaml.Visibility.Collapsed;
            // Surface the count so the user knows exactly what "Update all"
            // is about to touch — no more surprise 24-installer launches.
            DriverUpdatesUpdateAllButton.Content = updateCount > 1
                ? $"Update all ({updateCount})"
                : "Update all";
        }

        /// <summary>
        /// Clicking the Quick-tab driver-updates tile programmatically checks
        /// the Legion nav radio button, which NavRadioButton_Checked picks up
        /// and uses to switch to the Legion tab (scrolling to top).
        /// </summary>
        private async void QuickDriverUpdatesTile_Click(object sender, Windows.UI.Xaml.RoutedEventArgs e)
        {
            try
            {
                // Switch to Legion tab first (idempotent if already selected). This is
                // user-driven (they tapped a Quick-tab shortcut), so pre-arm with the
                // destination tag so the nav drift guard accepts the change.
                if (LegionNavItem != null && LegionNavItem.IsChecked != true)
                {
                    ArmUserNavIntent(LegionNavItem.Tag as string);
                    LegionNavItem.IsChecked = true;
                }

                // If we haven't yet populated the driver list in this session
                // (widget was re-opened since the helper pushed the count),
                // request from helper now. Helper serves from cache after the
                // first live fetch, so this is cheap — no Lenovo round-trip.
                if (_allDriverDisplays == null || _allDriverDisplays.Count == 0)
                {
                    await PrefetchDriverUpdatesAsync();
                }

                // Let XAML lay out and the ScrollViewer recognise its new
                // content before we try to bring the card into view — otherwise
                // the viewport math is still pointing at the old tab.
                await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Low, () =>
                {
                    try
                    {
                        if (DriverUpdatesCard != null)
                        {
                            DriverUpdatesCard.StartBringIntoView(new Windows.UI.Xaml.BringIntoViewOptions
                            {
                                VerticalAlignmentRatio = 0.0,
                                AnimationDesired = true,
                            });
                        }
                        else if (LegionScrollViewer != null)
                        {
                            // Fallback: no x:Name match, just scroll to the
                            // bottom since the card sits at the end of the tab.
                            LegionScrollViewer.ChangeView(null, LegionScrollViewer.ExtentHeight, null, false);
                        }
                    }
                    catch (Exception ex) { Logger.Debug($"Scroll-to-card failed: {ex.Message}"); }
                });
            }
            catch (Exception ex)
            {
                Logger.Warn($"QuickDriverUpdatesTile_Click failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Silently fetches the driver list from the helper cache and renders it
        /// without toggling any button states. Used when the user clicks the
        /// Quick-tab tile for the first time — they expect to see the list
        /// already populated, not have to press "Check for updates" again.
        /// </summary>
        private async Task PrefetchDriverUpdatesAsync()
        {
            try
            {
                if (!App.IsConnected) return;
                var request = new Windows.Foundation.Collections.ValueSet();
                request.Add("CheckDriverUpdates", true);
                var response = await App.SendMessageAsync(request);
                if (response == null) return;
                if (response.TryGetValue("DriverUpdateResult", out var payloadObj) && payloadObj is string payload)
                {
                    RenderDriverUpdateResult(payload);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"PrefetchDriverUpdatesAsync failed: {ex.Message}");
            }
        }

        /// <summary>
        /// "Update all" click: collect every row that has an Install/Update
        /// button visible, send all URLs in one batch pipe message to the
        /// helper. Helper downloads them in parallel then launches sequentially.
        /// </summary>
        private async void DriverUpdatesUpdateAllButton_Click(object sender, Windows.UI.Xaml.RoutedEventArgs e)
        {
            if (DriverUpdatesUpdateAllButton == null) return;
            var urls = new System.Collections.Generic.List<string>();
            foreach (var d in _allDriverDisplays)
            {
                // Update-all is strictly VISIBLE + UpdateAvailable. We
                // deliberately don't touch NotInstalled rows (the user
                // doesn't necessarily want Windows to gain drivers it
                // hasn't picked up yet) and we respect the category filter
                // (hidden utilities/diagnostics/tools never get queued).
                if (IsUpdateAllCandidate(d)) urls.Add(d.DownloadUrl);
            }
            if (urls.Count == 0)
            {
                if (DriverUpdatesStatusText != null)
                    DriverUpdatesStatusText.Text = "No visible driver updates to install.";
                return;
            }

            string originalLabel = DriverUpdatesUpdateAllButton.Content?.ToString() ?? "Update all";
            DriverUpdatesUpdateAllButton.IsEnabled = false;
            DriverUpdatesUpdateAllButton.Content = $"Updating {urls.Count}\u2026";
            if (DriverUpdatesStatusText != null)
                DriverUpdatesStatusText.Text = $"Downloading {urls.Count} installers\u2026";

            try
            {
                if (!App.IsConnected)
                {
                    if (DriverUpdatesStatusText != null)
                        DriverUpdatesStatusText.Text = "Helper not connected — can't install.";
                    return;
                }

                var request = new Windows.Foundation.Collections.ValueSet();
                // ValueSet can't nest arrays across the pipe contract — join
                // with newlines, helper splits on \n.
                request.Add("BatchInstallDrivers", string.Join("\n", urls));
                var response = await App.SendMessageAsync(request);

                string message = "Done.";
                if (response != null && response.TryGetValue("DriverBatchInstallResult", out var payloadObj) && payloadObj is string payload)
                {
                    try
                    {
                        if (Windows.Data.Json.JsonObject.TryParse(payload, out var root))
                        {
                            string msg = root.TryGetValue("message", out var m)
                                         && m.ValueType == Windows.Data.Json.JsonValueType.String
                                         ? m.GetString() : "";
                            if (!string.IsNullOrWhiteSpace(msg)) message = msg;
                        }
                    }
                    catch (Exception ex) { Logger.Warn($"Batch install parse failed: {ex.Message}"); }
                }
                if (DriverUpdatesStatusText != null)
                    DriverUpdatesStatusText.Text = message;
            }
            catch (Exception ex)
            {
                Logger.Warn($"DriverUpdatesUpdateAllButton_Click failed: {ex.Message}");
                if (DriverUpdatesStatusText != null)
                    DriverUpdatesStatusText.Text = $"Update all failed: {ex.Message}";
            }
            finally
            {
                DriverUpdatesUpdateAllButton.Content = originalLabel;
                DriverUpdatesUpdateAllButton.IsEnabled = true;
            }
        }

        private void DriverUpdatesShowUtilitiesCheckbox_Changed(object sender, Windows.UI.Xaml.RoutedEventArgs e)
        {
            if (_isLoadingUpdatePreferenceCheckboxes) return;
            if (DriverUpdatesShowUtilitiesCheckbox == null) return;
            DriverUpdatesShowUtilities = DriverUpdatesShowUtilitiesCheckbox.IsChecked == true;
            ApplyDriverFilters();
        }

        /// <summary>
        /// Filters <see cref="_allDriverDisplays"/> by the utilities/diagnostics
        /// checkbox and rebinds the visible list. Kept separate from the
        /// parse path so the checkbox can toggle without re-hitting Lenovo.
        /// </summary>
        private void ApplyDriverFilters()
        {
            if (DriverUpdatesList == null) return;
            bool showUtilities = DriverUpdatesShowUtilities;

            var visible = new System.Collections.Generic.List<DriverDisplay>();
            foreach (var d in _allDriverDisplays)
            {
                if (!showUtilities && _lowSignalDriverCategories.Contains(d.Category ?? ""))
                    continue;
                visible.Add(d);
            }

            DriverUpdatesList.ItemsSource = visible;
            DriverUpdatesList.Visibility = visible.Count > 0
                ? Windows.UI.Xaml.Visibility.Visible
                : Windows.UI.Xaml.Visibility.Collapsed;

            // Eligible-for-Update-all set tracks the same filter, so refresh
            // the button + its count label whenever the filter flips.
            UpdateUpdateAllButtonVisibility();
        }

        /// <summary>
        /// Click handler for the per-row Install/Update button. Sends the
        /// download URL to the helper, which runs elevated and can launch
        /// the Lenovo installer without an extra UAC prompt. Updates the
        /// button label to "Installing…" during the pipe round-trip so the
        /// user has feedback while the download runs.
        /// </summary>
        private async void DriverInstallButton_Click(object sender, Windows.UI.Xaml.RoutedEventArgs e)
        {
            var button = sender as Windows.UI.Xaml.Controls.Button;
            if (button == null) return;
            var url = button.Tag as string;
            if (string.IsNullOrWhiteSpace(url))
            {
                if (DriverUpdatesStatusText != null)
                    DriverUpdatesStatusText.Text = "No download URL for that driver.";
                return;
            }

            string originalLabel = button.Content?.ToString() ?? "Install";
            button.IsEnabled = false;
            button.Content = "Installing\u2026";

            try
            {
                if (!App.IsConnected)
                {
                    if (DriverUpdatesStatusText != null)
                        DriverUpdatesStatusText.Text = "Helper not connected — can't install.";
                    return;
                }

                var request = new Windows.Foundation.Collections.ValueSet();
                request.Add("InstallDriverUpdate", url);
                var response = await App.SendMessageAsync(request);
                string message = "Install started.";
                if (response != null && response.TryGetValue("DriverInstallResult", out var payloadObj) && payloadObj is string payload)
                {
                    try
                    {
                        if (Windows.Data.Json.JsonObject.TryParse(payload, out var root))
                        {
                            bool success = root.TryGetValue("success", out var s) &&
                                           s.ValueType == Windows.Data.Json.JsonValueType.Boolean && s.GetBoolean();
                            string msg = root.TryGetValue("message", out var m) &&
                                         m.ValueType == Windows.Data.Json.JsonValueType.String ? m.GetString() : "";
                            message = string.IsNullOrWhiteSpace(msg)
                                ? (success ? "Installer launched." : "Install failed.")
                                : msg;
                        }
                    }
                    catch (Exception ex) { Logger.Warn($"DriverInstallButton parse failed: {ex.Message}"); }
                }
                if (DriverUpdatesStatusText != null)
                    DriverUpdatesStatusText.Text = message;
            }
            catch (Exception ex)
            {
                Logger.Warn($"DriverInstallButton_Click failed: {ex.Message}");
                if (DriverUpdatesStatusText != null)
                    DriverUpdatesStatusText.Text = $"Install failed: {ex.Message}";
            }
            finally
            {
                button.Content = originalLabel;
                button.IsEnabled = true;
            }
        }

        /// <summary>
        /// Returns ("Install", Visible) for NotInstalled drivers, ("Update",
        /// Visible) for UpdateAvailable, and ("", Collapsed) otherwise.
        /// No button for up-to-date / unknown status since there's nothing
        /// actionable to download.
        /// </summary>
        private static (string label, Windows.UI.Xaml.Visibility vis) InstallButtonForStatus(int statusCode, string downloadUrl)
        {
            if (string.IsNullOrWhiteSpace(downloadUrl))
                return ("", Windows.UI.Xaml.Visibility.Collapsed);
            switch (statusCode)
            {
                case 2: return ("Update", Windows.UI.Xaml.Visibility.Visible);   // UpdateAvailable
                case 3: return ("Install", Windows.UI.Xaml.Visibility.Visible);  // NotInstalled
                default: return ("", Windows.UI.Xaml.Visibility.Collapsed);
            }
        }

        /// <summary>
        /// Maps Lenovo's numeric severity type (1/2/3) from the per-package XML
        /// into a short human label for the widget. Unknown values pass through
        /// so we don't hide information if Lenovo adds a new level.
        /// </summary>
        private static string SeverityLabel(string raw)
        {
            switch (raw?.Trim())
            {
                case "1": return "Critical";
                case "2": return "Recommended";
                case "3": return "Optional";
                default:  return string.IsNullOrWhiteSpace(raw) ? "" : raw;
            }
        }

        // UpdateStatus enum mirrors DriverUpdateStatus in the helper:
        //   0 = Unknown, 1 = UpToDate, 2 = UpdateAvailable, 3 = NotInstalled
        private static string StatusLabelFor(int code)
        {
            switch (code)
            {
                case 1: return "Up to date";
                case 2: return "Update";
                case 3: return "Not installed";
                default: return "Unknown";
            }
        }

        private static Windows.UI.Xaml.Media.Brush StatusColorFor(int code)
        {
            // Green for up-to-date, orange for "update available", dark gray
            // for not-installed / unknown. Matches the rest of the widget's
            // status chip palette.
            byte r, g, b;
            switch (code)
            {
                case 1: r = 0x55; g = 0xC8; b = 0x55; break; // #55C855 success green
                case 2: r = 0xFF; g = 0xB0; b = 0x60; break; // #FFB060 warning orange
                case 3: r = 0x66; g = 0x66; b = 0x66; break; // #666666 neutral gray
                default: r = 0x55; g = 0x55; b = 0x55; break; // #555555 unknown
            }
            return new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, r, g, b));
        }

        private void SetControllerBatterySectionVisibility(bool visible)
        {
            // The device glyph box between them always stays visible.
            if (LeftControllerCard != null && RightControllerCard != null)
            {
                LeftControllerCard.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                RightControllerCard.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                Logger.Info($"Controller battery section visibility set to: {visible}");
            }
        }

        private void SetTouchpadVibrationSectionVisibility(bool visible)
        {
            if (TouchpadVibrationSection != null)
            {
                TouchpadVibrationSection.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                Logger.Info($"Touchpad & Vibration section visibility set to: {visible}");
            }
        }
        /// <summary>
        /// No-op. Manual XYFocus wiring was replaced by NavigationDirectionDistance
        /// spatial navigation + the LosingFocus shepherd. Kept because Legion
        /// detection paths still call it.
        /// </summary>
        private void UpdatePerformanceTabXYFocus(bool isLegion)
        {
        }

        /// <summary>
        /// Shows or hides the Default Game Profile card based on profile availability.
        /// </summary>
        private void SetDefaultProfileCardVisibility(bool isVisible)
        {
            if (DefaultGameProfileCard != null)
            {
                DefaultGameProfileCard.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
                Logger.Info($"Default Game Profile card visibility set to: {isVisible}");

                // Update XY navigation when DGP visibility changes
                UpdatePerformanceTabXYNavigation();
            }
        }

        /// <summary>
        /// No-op. Manual XYFocus wiring was replaced by NavigationDirectionDistance
        /// spatial navigation + the LosingFocus shepherd. Kept because DGP
        /// visibility/state-change paths still call it.
        /// </summary>
        private void UpdatePerformanceTabXYNavigation()
        {
        }

        /// <summary>
        /// Updates the Default Game Profile card display with profile settings.
        /// </summary>
        private void UpdateDefaultProfileDisplay(Shared.Data.DefaultGameProfile? profile)
        {
            if (profile.HasValue)
            {
                var p = profile.Value;

                // Store current profile first (needed by UpdateDefaultProfileGameIcon)
                currentDefaultGameProfile = p;

                // Update game name
                if (DefaultProfileGameName != null)
                {
                    DefaultProfileGameName.Text = p.GameName ?? "";
                }

                // Update game icon from Steam CDN if available
                UpdateDefaultProfileGameIcon();

                // Update settings text
                if (DefaultProfileSettingsText != null)
                {
                    var settings = new System.Collections.Generic.List<string>();

                    settings.Add($"{p.TDP}W");

                    if (p.FrameCap.HasValue && p.FrameCap.Value > 0)
                    {
                        settings.Add($"{p.FrameCap.Value}fps");
                    }

                    if (!string.IsNullOrEmpty(p.ResolutionCap))
                    {
                        settings.Add(p.ResolutionCap);
                    }

                    DefaultProfileSettingsText.Text = string.Join(" - ", settings);
                    Logger.Info($"Default Game Profile display updated: {DefaultProfileSettingsText.Text}");
                }

                // Update "Optimizing for Z2/Z1 Extreme" text based on hardware model
                if (DefaultProfileOptimizingText != null && DefaultProfileSeparator != null)
                {
                    string optimizingText = "Optimizing for your device";
                    if (!string.IsNullOrEmpty(p.HardwareModel))
                    {
                        if (p.HardwareModel == "HORSEM4N")
                        {
                            optimizingText = "Optimizing for Z2 Extreme";
                        }
                        else if (p.HardwareModel == "OMNI")
                        {
                            optimizingText = "Optimizing for Z1 Extreme";
                        }
                    }
                    DefaultProfileOptimizingText.Text = optimizingText;
                    DefaultProfileOptimizingText.Visibility = Visibility.Visible;
                    DefaultProfileSeparator.Visibility = Visibility.Visible;
                }
            }
            else
            {
                // Hide elements when no profile
                if (DefaultProfileOptimizingText != null)
                {
                    DefaultProfileOptimizingText.Visibility = Visibility.Collapsed;
                }
                if (DefaultProfileSeparator != null)
                {
                    DefaultProfileSeparator.Visibility = Visibility.Collapsed;
                }
                if (DefaultProfileGameName != null)
                {
                    DefaultProfileGameName.Text = "";
                }
                currentDefaultGameProfile = null;
            }
        }

        /// <summary>
        /// Updates the game icon in the Default Game Profile card.
        /// First tries helper-cached icons, then Steam's local cache.
        /// </summary>
        private async void UpdateDefaultProfileGameIcon()
        {
            // Ensure we're on the UI thread
            if (!Dispatcher.HasThreadAccess)
            {
                await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => UpdateDefaultProfileGameIcon());
                return;
            }

            if (DefaultProfileGameIcon == null)
            {
                Logger.Info("UpdateDefaultProfileGameIcon: DefaultProfileGameIcon element is null");
                return;
            }

            Logger.Info($"UpdateDefaultProfileGameIcon: Starting, currentGameExePath={currentGameExePath ?? "null"}");

            string iconPath = null;

            // First, try to use the helper-cached icon for the current running game
            // (Default profiles are shown when a matching game is detected)
            if (!string.IsNullOrEmpty(currentGameExePath))
            {
                iconPath = GetCachedIconPath(currentGameExePath);
                if (!string.IsNullOrEmpty(iconPath))
                {
                    Logger.Info($"UpdateDefaultProfileGameIcon: Using helper-cached icon: {iconPath}");
                }
                else
                {
                    Logger.Info($"UpdateDefaultProfileGameIcon: No cached icon found for {currentGameExePath}");
                }
            }

            // Fall back to Steam icon if we have a Steam App ID
            if (string.IsNullOrEmpty(iconPath) && currentDefaultGameProfile.HasValue)
            {
                iconPath = currentDefaultGameProfile.Value.GetSteamIconPath();
                if (!string.IsNullOrEmpty(iconPath))
                {
                    Logger.Info($"UpdateDefaultProfileGameIcon: Using Steam icon: {iconPath}");
                }
            }

            // Try to load the icon
            if (!string.IsNullOrEmpty(iconPath))
            {
                try
                {
                    // Load from local file using StorageFile for UWP compatibility
                    var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(iconPath);
                    using (var stream = await file.OpenReadAsync())
                    {
                        var bitmap = new Windows.UI.Xaml.Media.Imaging.BitmapImage();
                        await bitmap.SetSourceAsync(stream);
                        DefaultProfileGameIcon.Source = bitmap;
                        DefaultProfileGameIcon.Visibility = Visibility.Visible;
                        Logger.Info($"UpdateDefaultProfileGameIcon: Icon loaded successfully");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn($"UpdateDefaultProfileGameIcon: Failed to load icon from {iconPath}: {ex.Message}");
                }
            }
            else
            {
                Logger.Info("UpdateDefaultProfileGameIcon: No icon path available");
            }

            // Hide the icon if no icon available
            DefaultProfileGameIcon.Visibility = Visibility.Collapsed;
        }

        // Cached default game profile for UI state management
        private Shared.Data.DefaultGameProfile? currentDefaultGameProfile;

        /// <summary>
        /// Called when the Default Game Profile enabled state changes.
        /// Greys out TDP controls and syncs FPS limit when enabled.
        /// </summary>
        private void OnDefaultProfileEnabledChanged(bool enabled)
        {
            Logger.Info($"Default Game Profile enabled changed to: {enabled}");

            // IMPORTANT: When DISABLING, load the appropriate profile for current power state!
            // Don't restore saved values - they may be from a different power state (e.g., DC when now on AC)
            // Set flag to suppress profile saves during restoration (toggle handlers would otherwise save wrong values)
            if (!enabled)
            {
                isRestoringFromDefaultProfile = true;
                try
                {
                    // Clear saved state - we'll load from profile instead of restoring
                    // FPS limit and TDP can differ between AC/DC profiles, so restoring pre-DGP state is wrong
                    originalFpsLimitToggleState = null;
                    originalFpsLimitSliderValue = null;
                    originalTdpSliderValue = null;

                    // Load the appropriate profile for current power state
                    // This ensures AC profile is loaded when on AC, DC profile when on DC
                    // Profile loading handles TDP, FPS limit, and all other settings
                    string targetProfile = GetTargetProfileName();
                    Logger.Info($"DGP disabled - loading profile for current power state: {targetProfile}");
                    LoadProfileSettings(targetProfile, isExplicitSwitch: false);
                }
                finally
                {
                    isRestoringFromDefaultProfile = false;
                }
            }

            // Update TDP controls enabled state (now with correct values if disabling)
            UpdateTDPControlsForDefaultProfile(enabled);

            // Update per-game profile toggle state
            UpdatePerGameProfileForDefaultProfile(enabled);

            if (enabled && currentDefaultGameProfile.HasValue)
            {
                var profile = currentDefaultGameProfile.Value;

                // Save original FPS limit state before changing
                if (FPSLimitToggle != null && !originalFpsLimitToggleState.HasValue)
                {
                    originalFpsLimitToggleState = FPSLimitToggle.IsOn;
                    originalFpsLimitSliderValue = FPSLimitSlider?.Value ?? 60;
                    Logger.Info($"Saved original FPS limit state: toggle={originalFpsLimitToggleState}, value={originalFpsLimitSliderValue}");
                }

                // Save original TDP slider value before changing
                if (TDPSlider != null && !originalTdpSliderValue.HasValue)
                {
                    originalTdpSliderValue = TDPSlider.Value;
                    Logger.Info($"Saved original TDP slider value: {originalTdpSliderValue}W");
                }

                // Sync FPS limit toggle and slider to match profile
                if (profile.FrameCap.HasValue && profile.FrameCap.Value > 0)
                {
                    if (FPSLimitToggle != null)
                    {
                        FPSLimitToggle.IsOn = true;
                    }
                    if (FPSLimitSlider != null)
                    {
                        FPSLimitSlider.Value = profile.FrameCap.Value;
                    }
                    Logger.Info($"FPS limit synced to default profile: {profile.FrameCap.Value}fps");
                }

                // Sync TDP slider to match profile
                if (profile.TDP > 0 && TDPSlider != null)
                {
                    TDPSlider.Value = profile.TDP;
                    Logger.Info($"TDP slider synced to default profile: {profile.TDP}W");
                }
            }

            // Update Quick tab tile styling
            UpdateQuickSettingsTileStates();

            // Update XY navigation for controller support
            UpdatePerformanceTabXYNavigation();
        }

        // Store original state for restoration when default profile is disabled
        private bool? originalFpsLimitToggleState;
        private double? originalFpsLimitSliderValue;
        private double? originalTdpSliderValue;
        private bool isRestoringFromDefaultProfile; // Flag to suppress profile saves during DGP restoration

        /// <summary>
        /// Updates per-game profile toggle state based on Default Game Profile.
        /// </summary>
        private void UpdatePerGameProfileForDefaultProfile(bool defaultProfileEnabled)
        {
            if (defaultProfileEnabled)
            {
                // Hide the Active Profile card when default game profile is enabled
                if (ActiveProfileCard != null)
                {
                    ActiveProfileCard.Visibility = Visibility.Collapsed;
                }

                Logger.Debug("Active Profile card hidden - Default Game Profile is active");
            }
            else
            {
                // Show the Active Profile card when default game profile is disabled
                if (ActiveProfileCard != null)
                {
                    ActiveProfileCard.Visibility = Visibility.Visible;
                }

                // Re-enable the per-game profile toggle
                if (PerGameProfileToggle != null)
                {
                    PerGameProfileToggle.IsEnabled = runningGame?.Value.IsValid() == true;
                }

                Logger.Debug("Active Profile card shown - Default Game Profile is inactive");
            }
        }

        /// <summary>
        /// Updates TDP control enabled states based on Default Game Profile.
        /// </summary>
        private void UpdateTDPControlsForDefaultProfile(bool defaultProfileEnabled)
        {
            if (defaultProfileEnabled)
            {
                // Disable TDP controls when default profile is active
                if (TDPModeComboBox != null)
                {
                    TDPModeComboBox.IsEnabled = false;
                }
                if (TDPSlider != null)
                {
                    TDPSlider.IsEnabled = false;
                }
                if (TDPBoostToggle != null)
                {
                    TDPBoostToggle.IsEnabled = false;
                }
                if (AutoTDPToggle != null)
                {
                    AutoTDPToggle.IsEnabled = false;
                }
                if (StickyTDPToggle != null)
                {
                    StickyTDPToggle.IsEnabled = false;
                }

                // Also disable FPS limit controls (controlled by Default Game Profile)
                if (FPSLimitToggle != null)
                {
                    FPSLimitToggle.IsEnabled = false;
                }
                if (FPSLimitSlider != null)
                {
                    FPSLimitSlider.IsEnabled = false;
                }

                Logger.Debug("TDP and FPS controls disabled - Default Game Profile is active");
            }
            else
            {
                // Re-enable TDP controls based on current mode
                if (TDPModeComboBox != null)
                {
                    TDPModeComboBox.IsEnabled = true;
                }

                // Re-enable FPS limit controls
                if (FPSLimitToggle != null)
                {
                    FPSLimitToggle.IsEnabled = true;
                }
                if (FPSLimitSlider != null)
                {
                    FPSLimitSlider.IsEnabled = true;
                }

                // Re-evaluate other controls based on current TDP mode
                UpdateTDPSliderEnabledState();

                Logger.Debug("TDP and FPS controls re-enabled - Default Game Profile is inactive");
            }
        }

        /// <summary>
        /// Updates WinRing0 option visibility in TDP Method dropdown based on file availability.
        /// </summary>
        private void UpdateWinRing0Visibility(bool available)
        {
            if (TdpMethodWinRing0Item != null)
            {
                TdpMethodWinRing0Item.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
                Logger.Info($"WinRing0 TDP option visibility set to: {available}");

                // If WinRing0 was selected but is no longer available, switch to WMI or PawnIO
                if (!available && TdpMethodComboBox != null)
                {
                    var selectedItem = TdpMethodComboBox.SelectedItem as ComboBoxItem;
                    if (selectedItem?.Tag is string tag && tag == "WinRing0")
                    {
                        // Try to select ManufacturerWMI first, then PawnIO
                        if (TdpMethodWmiItem?.Visibility == Visibility.Visible)
                        {
                            TdpMethodComboBox.SelectedItem = TdpMethodWmiItem;
                        }
                        else if (TdpMethodPawnIOItem?.Visibility == Visibility.Visible)
                        {
                            TdpMethodComboBox.SelectedItem = TdpMethodPawnIOItem;
                        }
                    }
                }
            }

            // Ensure a valid option is selected after visibility changes
            EnsureValidTdpMethodSelected();
        }

        /// <summary>
        /// Ensures a valid (visible and enabled) TDP method is selected in the dropdown.
        /// IMPORTANT: Never auto-select WinRing0 - user must explicitly choose it.
        /// </summary>
        private void EnsureValidTdpMethodSelected()
        {
            if (TdpMethodComboBox == null) return;

            // Same race as SetLegionTabVisibility (issue #79 round-3 PawnIO regression).
            // This path runs from UpdateWinRing0Visibility / UpdatePawnIOInstalledUI,
            // which fire during BatchSync DOWN. If the TdpMethodProperty's own
            // NotifyPropertyChanged dispatcher entry hasn't run yet, SelectedIndex
            // is still -1 and we'd auto-pick WMI here — fires SelectionChanged
            // and pushes 0 UP, clobbering helper's PawnIO=1. Skip auto-pick when
            // helper owns the value; its NPC dispatcher will land the right index.
            if (tdpMethod != null && tdpMethod.HasReceivedHelperSync)
            {
                Logger.Debug("EnsureValidTdpMethodSelected: helper has synced — skipping auto-pick, helper's value will land via NotifyPropertyChanged");
                return;
            }

            var selectedItem = TdpMethodComboBox.SelectedItem as ComboBoxItem;
            var selectedIndex = TdpMethodComboBox.SelectedIndex;

            // If current selection is valid (visible and enabled), do nothing
            if (selectedItem != null && selectedItem.Visibility == Visibility.Visible && selectedItem.IsEnabled)
            {
                return;
            }

            // If ManufacturerWMI is selected but collapsed, wait for Legion detection
            // Don't auto-select PawnIO - Legion detection will make WMI visible if it's a Legion device
            if (selectedItem != null && selectedItem == TdpMethodWmiItem && selectedItem.Visibility == Visibility.Collapsed)
            {
                Logger.Debug("EnsureValidTdpMethodSelected: ManufacturerWMI selected but collapsed, waiting for Legion detection");
                return;
            }

            // If selectedIndex is 0 (ManufacturerWMI position) but selectedItem isn't matching,
            // it means WMI was intended but may be collapsed - wait for Legion detection
            if (selectedIndex == 0 && TdpMethodWmiItem?.Visibility == Visibility.Collapsed)
            {
                Logger.Debug("EnsureValidTdpMethodSelected: SelectedIndex=0 (WMI) but WMI collapsed, waiting for Legion detection");
                return;
            }

            // If nothing is selected yet and WMI is collapsed, wait for Legion detection
            // This handles the case where the ComboBox rejected the initial Collapsed selection
            if (selectedItem == null && TdpMethodWmiItem?.Visibility == Visibility.Collapsed)
            {
                Logger.Debug("EnsureValidTdpMethodSelected: No selection and WMI collapsed, waiting for Legion detection");
                return;
            }

            // Find the first visible and enabled option and select it
            // Priority: ManufacturerWMI > PawnIO (if installed)
            // NEVER auto-select WinRing0 - it's a legacy option that may trigger anti-cheat
            if (TdpMethodWmiItem?.Visibility == Visibility.Visible && TdpMethodWmiItem.IsEnabled)
            {
                TdpMethodComboBox.SelectedItem = TdpMethodWmiItem;
                Logger.Info("TDP Method auto-selected: ManufacturerWMI");
            }
            else if (TdpMethodPawnIOItem?.Visibility == Visibility.Visible && TdpMethodPawnIOItem.IsEnabled)
            {
                TdpMethodComboBox.SelectedItem = TdpMethodPawnIOItem;
                Logger.Info("TDP Method auto-selected: PawnIO");
            }
            else
            {
                // Don't auto-select WinRing0 - user must explicitly choose it
                Logger.Warn("No safe TDP method available - user must select WinRing0 manually if desired");
            }
        }

        /// <summary>
        /// Updates the PawnIO install button state and dropdown option based on driver installation status.
        /// PawnIO option is always visible but disabled if not installed.
        /// </summary>
        private void UpdatePawnIOInstalledUI(bool installed)
        {
            // PawnIO option is always visible, but enable/disable based on installation status
            // This prevents WinRing0 from being auto-selected when PawnIO detection is delayed
            if (TdpMethodPawnIOItem != null)
            {
                // Keep PawnIO visible always - just update text to show status
                TdpMethodPawnIOItem.Visibility = Visibility.Visible;
                TdpMethodPawnIOItem.IsEnabled = installed;
                TdpMethodPawnIOItem.Content = installed ? "PawnIO" : "PawnIO (Not Installed)";
                Logger.Info($"PawnIO TDP option enabled: {installed}");

                // If PawnIO was selected but is no longer installed, switch to WMI only
                // NEVER auto-switch to WinRing0 - user must explicitly choose it
                if (!installed && TdpMethodComboBox != null)
                {
                    var selectedItem = TdpMethodComboBox.SelectedItem as ComboBoxItem;
                    if (selectedItem?.Tag is string tag && tag == "PawnIO")
                    {
                        // Try to select ManufacturerWMI, don't fall back to WinRing0
                        if (TdpMethodWmiItem?.Visibility == Visibility.Visible)
                        {
                            TdpMethodComboBox.SelectedItem = TdpMethodWmiItem;
                        }
                        // If WMI not available, leave selection as-is or clear it
                        // User will need to reinstall PawnIO or manually select WinRing0
                    }
                }
            }

            if (InstallPawnIOButton != null)
            {
                InstallPawnIOButton.Content = installed ? "Installed" : "Install";
                InstallPawnIOButton.IsEnabled = !installed;
                Logger.Info($"PawnIO install button updated: installed={installed}");

                // Update XY navigation to skip disabled button
                // TDPSettingsExpandButton.XYFocusUp must always point to SystemNavItem (for navigating out of card)
            }

            if (PawnIOStatusText != null)
            {
                if (installed)
                {
                    PawnIOStatusText.Text = "PawnIO driver is installed. Signed kernel driver for anti-cheat safe hardware access.";
                }
                else
                {
                    PawnIOStatusText.Text = "Signed kernel driver for hardware access. Replaces WinRing0.";
                }
            }

            // Ensure a valid option is selected after visibility changes
            EnsureValidTdpMethodSelected();
        }

        /// <summary>
        /// Handles the PawnIO install button click.
        /// After installation, the helper restarts to reinitialize with PawnIO support.
        /// </summary>
        private async void InstallPawnIOButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Logger.Info("InstallPawnIOButton clicked - triggering PawnIO installation");

                // Update button to show installing state
                if (InstallPawnIOButton != null)
                {
                    InstallPawnIOButton.Content = "Installing...";
                    InstallPawnIOButton.IsEnabled = false;
                }

                // Trigger the installation via the property
                installPawnIO?.TriggerInstall();

                // Wait for helper to complete installation and exit
                // The helper exits after successful PawnIO installation
                Logger.Info("Waiting for PawnIO installation to complete...");
                await Task.Delay(5000);

                // Check if helper is still connected, if not, relaunch it
                // The helper will have exited after successful installation
                Logger.Info("Relaunching helper after PawnIO installation...");
                await FullTrustProcessLauncher.LaunchFullTrustProcessForCurrentAppAsync();

                // Wait for helper to start and reinitialize
                await Task.Delay(2000);
                Logger.Info("Helper relaunched after PawnIO installation");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error during PawnIO installation: {ex.Message}");
                // Reset button state on error
                if (InstallPawnIOButton != null)
                {
                    InstallPawnIOButton.Content = "Install";
                    InstallPawnIOButton.IsEnabled = true;
                }
            }
        }

        /// <summary>
        /// Labs Guide-remap prerequisite: usbip-win2 status + one-click install.
        /// Drives the repurposed ViGEmBusStatusText / ViGEmBusInstallButton pair
        /// in the Labs section (control names kept from the ViGEm era).
        /// </summary>
        private void UpdateLabsUsbipUI(bool installed)
        {
            if (ViGEmBusStatusText != null)
            {
                ViGEmBusStatusText.Text = installed ? "Status: Installed" : "Status: Not Installed";
                ViGEmBusStatusText.Foreground = installed
                    ? new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x6C, 0xCB, 0x5F)) // #6CCB5F - matches Quick Settings tile green
                    : new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 136, 136, 136));
            }

            if (ViGEmBusInstallButton != null)
            {
                ViGEmBusInstallButton.Content = installed ? "Installed" : "Install usbip-win2";
                ViGEmBusInstallButton.IsEnabled = !installed;
            }
        }

        // A prereq install button flips to "Installing..." optimistically and relies on an
        // …Installed bool going false->true to reset. If the install FAILS the value stays
        // false, the helper's re-push of false is echo-suppressed (GenericProperty equality
        // skip), the completion callback never fires, and the button is stuck on "Installing..."
        // until app restart. This safety net resets any button still showing "Installing..."
        // after the timeout so it can be retried without a restart.
        private const int PREREQ_INSTALL_TIMEOUT_MS = 45000;

        private async void ScheduleInstallButtonTimeout(Windows.UI.Xaml.Controls.Button button,
            Windows.UI.Xaml.Controls.TextBlock statusText, string idleContent, string idleStatusText)
        {
            await Task.Delay(PREREQ_INSTALL_TIMEOUT_MS);
            if (button != null && (button.Content as string) == "Installing...")
            {
                Logger.Warn("Prerequisite install did not report completion within the timeout - resetting button (install may have failed; check the helper log)");
                button.Content = idleContent;
                button.IsEnabled = true;
                if (statusText != null && idleStatusText != null) statusText.Text = idleStatusText;
            }
        }

        private void LabsUsbipInstallButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Logger.Info("Labs usbip install button clicked - triggering usbip-win2 installation");
                installUsbip?.TriggerInstall();
                if (ViGEmBusInstallButton != null)
                {
                    ViGEmBusInstallButton.Content = "Installing...";
                    ViGEmBusInstallButton.IsEnabled = false;
                }
                if (ViGEmBusStatusText != null)
                {
                    ViGEmBusStatusText.Text = "Status: Installing...";
                }
                ScheduleInstallButtonTimeout(ViGEmBusInstallButton, ViGEmBusStatusText,
                    "Install usbip-win2", "Status: Install may have failed - check the log");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error during usbip installation: {ex.Message}");
            }
        }

        /// <summary>
        /// Kept for callers that still toggle Custom-mode UI visibility — the three Legion-tab
        /// SPL/SPPL/FPPT cards have been removed (replaced by the Performance-tab master TDP slider
        /// + per-profile TDP Boost deltas), so this is now just a focus-chain bookkeeping shim:
        /// PerformanceMode dropdown navigates straight to the Fan controls in every mode.
        /// </summary>
        private void SetCustomTDPVisibility(bool visible)
        {

            // Fan curve card stays fully interactive in every power mode now. Per-mode
            // storage means each mode has its own saved curve + EC-override unlock state,
            // and the user can edit any mode's slot from the dropdown without changing
            // the running power mode. The old Custom-only gate predated per-mode storage
            // and would mislead users into thinking fan control is only available in
            // Custom — keep it always enabled.
            if (LegionFanCurveCard != null)
            {
                LegionFanCurveCard.IsHitTestVisible = true;
                LegionFanCurveCard.Opacity = 1.0;
            }
            if (FanCurvePresetComboBox != null)
            {
                FanCurvePresetComboBox.IsEnabled = true;
            }
        }

        /// <summary>
        /// Toggles the ColorPicker visibility
        /// </summary>
        private void LegionColorExpandButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (LegionColorPicker != null)
                {
                    bool isExpanded = LegionColorPicker.Visibility == Visibility.Visible;
                    LegionColorPicker.Visibility = isExpanded ? Visibility.Collapsed : Visibility.Visible;

                    // Update button icon (chevron down/up)
                    if (LegionColorExpandButton != null)
                    {
                        LegionColorExpandButton.Content = isExpanded ? "\uE70D" : "\uE70E";
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in LegionColorExpandButton_Click: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles ColorPicker color changes and updates the preview
        /// </summary>
        private void LegionColorPicker_ColorChanged(Microsoft.UI.Xaml.Controls.ColorPicker sender, Microsoft.UI.Xaml.Controls.ColorChangedEventArgs args)
        {
            try
            {
                // Update color preview
                if (LegionColorPreview != null)
                {
                    LegionColorPreview.Background = new SolidColorBrush(args.NewColor);
                }

                legionLightColor?.OnColorChanged(args.NewColor);

                // Save to controller profile (debounced - color picker fires continuously while dragging)
                ControllerSliderSettingChanged(sender, null);
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in LegionColorPicker_ColorChanged: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles brightness slider changes
        /// </summary>
        private void LegionBrightnessSlider_ValueChanged(object sender, Windows.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            try
            {
                if (LegionBrightnessSlider != null && LegionBrightnessValue != null)
                {
                    int brightness = (int)LegionBrightnessSlider.Value;
                    LegionBrightnessValue.Text = $"{brightness}%";
                }
                // Save to controller profile (debounced - slider fires continuously while dragging)
                ControllerSliderSettingChanged(sender, null);
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in LegionBrightnessSlider_ValueChanged: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles speed slider changes
        /// </summary>
        private void LegionSpeedSlider_ValueChanged(object sender, Windows.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            try
            {
                if (LegionSpeedSlider != null && LegionSpeedValue != null)
                {
                    int speed = (int)LegionSpeedSlider.Value;
                    LegionSpeedValue.Text = $"{speed}%";
                }

                // Save to controller profile (debounced - slider fires continuously while dragging)
                ControllerSliderSettingChanged(sender, null);
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in LegionSpeedSlider_ValueChanged: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles light mode ComboBox selection - shows/hides appropriate controls
        /// Mode options visibility:
        /// - Off (0): hide all
        /// - Solid (1): Color + Brightness
        /// - Pulse (2): Color + Speed
        /// - Dynamic (3): Brightness + Speed
        /// - Spiral (4): Brightness + Speed
        /// </summary>
        private void LegionLightModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                UpdateLegionLightControlsVisibility();

                // Save to controller profile (handler is detached during profile loading)
                ControllerSettingChanged(sender, null);
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in LegionLightModeComboBox_SelectionChanged: {ex.Message}");
            }
        }

        /// <summary>
        /// Updates the visibility of Legion light controls based on the selected mode
        /// </summary>
        private void UpdateLegionLightControlsVisibility()
        {
            if (LegionLightModeComboBox == null || LegionColorCard == null ||
                LegionBrightnessCard == null || LegionSpeedCard == null)
                return;

            int mode = LegionLightModeComboBox.SelectedIndex;

            // Off (0): hide all
            // Solid (1): Color + Brightness
            // Pulse (2): Color + Brightness + Speed
            // Dynamic (3): Brightness + Speed
            // Spiral (4): Brightness + Speed

            bool showColor = mode == 1 || mode == 2; // Solid, Pulse
            bool showBrightness = mode >= 1; // All modes except Off have brightness
            bool showSpeed = mode == 2 || mode == 3 || mode == 4; // Pulse, Dynamic, Spiral

            LegionColorCard.Visibility = showColor ? Visibility.Visible : Visibility.Collapsed;
            LegionBrightnessCard.Visibility = showBrightness ? Visibility.Visible : Visibility.Collapsed;
            LegionSpeedCard.Visibility = showSpeed ? Visibility.Visible : Visibility.Collapsed;

            Logger.Info($"Legion light mode {mode}: Color={showColor}, Brightness={showBrightness}, Speed={showSpeed}");
        }

        /// <summary>
        /// Handles performance mode ComboBox selection in Legion tab
        /// </summary>
        private void LegionPerformanceModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Check if this change came from a pipe/property sync (not user interaction).
            // When true, TDPModeComboBox_SelectionChanged must skip profile saves to prevent
            // corrupting game profiles with global values during game exit transitions.
            bool fromHelperSync = legionPerformanceMode?.IsUpdatingUI == true;
            Logger.Info($"Legion Performance mode selection changed (fromHelperSync={fromHelperSync})");

            // Sync TDP Mode dropdown in Performance tab
            // Skip during initial sync - ApplyProfileTDPToHelper will set the correct value
            if (TDPModeComboBox != null && LegionPerformanceModeComboBox != null && !isInitialSync)
            {
                if (TDPModeComboBox.SelectedIndex != LegionPerformanceModeComboBox.SelectedIndex)
                {
                    // Set isApplyingHelperUpdate during sync so TDPModeComboBox_SelectionChanged
                    // returns early (skips profile save and value sends).
                    if (fromHelperSync)
                        isApplyingHelperUpdate = true;
                    try
                    {
                        TDPModeComboBox.SelectedIndex = LegionPerformanceModeComboBox.SelectedIndex;
                    }
                    finally
                    {
                        if (fromHelperSync)
                            isApplyingHelperUpdate = false;
                    }
                }
            }

            // Update TDP slider enabled state based on mode
            UpdateTDPSliderEnabledState();

            // Keep the fan-curve power-mode dropdown in sync with whatever power mode
            // is now active. Fires for both user-driven and helper-pushed mode changes,
            // so the fan curve dropdown follows the Lenovo button, TDP card, etc.
            try { SyncFanCurvePresetComboToActiveMode(); }
            catch (Exception ex) { Logger.Debug($"SyncFanCurvePresetComboToActiveMode failed: {ex.Message}"); }
        }

        /// <summary>
        /// Handles TDP Mode ComboBox selection in Performance tab (Legion devices only)
        /// </summary>
        private int lastTDPModeIndex = 1; // Track last index to avoid redundant updates (init to XAML default: Balanced)
        private double savedCustomTDP = 15; // Saved custom TDP value when switching away from Custom mode
        private void TDPModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TDPModeComboBox == null) return;

            int selectedIndex = TDPModeComboBox.SelectedIndex;
            if (selectedIndex < 0) return;

            Logger.Debug($"TDPModeComboBox_SelectionChanged: selectedIndex={selectedIndex}, lastTDPModeIndex={lastTDPModeIndex}, isApplyingHelperUpdate={isApplyingHelperUpdate}, isLoadingProfile={isLoadingProfile}");

            // Block only during active pipe operations (sync/message handling) or profile loading.
            // Use isApplyingHelperUpdate instead of isInitialSync so user clicks work during the
            // init window (20-50s). Programmatic changes from pipe sync are blocked by isApplyingHelperUpdate,
            // and post-sync ComboBox updates are blocked by the equality check (lastTDPModeIndex set first).
            if (isApplyingHelperUpdate || isLoadingProfile)
            {
                lastTDPModeIndex = selectedIndex;
                return;
            }

            // Skip if this is the same index as last time (avoid redundant processing)
            if (selectedIndex == lastTDPModeIndex)
            {
                Logger.Debug($"TDPModeComboBox_SelectionChanged skipped: selectedIndex={selectedIndex} == lastTDPModeIndex={lastTDPModeIndex}");
                return;
            }

            // Check if switching away from Custom mode (to save slider value)
            // Use lastTDPModeIndex to check the PREVIOUS selection, not the new one
            bool wasCustomMode = lastTDPModeIndex >= 0 && IsCustomTdpModeIndex(lastTDPModeIndex);
            if (wasCustomMode && TDPSlider != null)
            {
                savedCustomTDP = TDPSlider.Value;
                Logger.Info($"Saved custom TDP value: {savedCustomTDP}W before switching to preset mode");
            }

            lastTDPModeIndex = selectedIndex;

            // Use the new preset system to get TDP and Legion mode values
            bool isCustomMode = IsCustomTdpModeSelected();
            int presetTdpValue = GetCurrentPresetTdpValue();
            int legionModeValue = GetCurrentPresetLegionMode();
            bool? presetTdpBoost = GetCurrentPresetTdpBoost();

            Logger.Info($"TDP Mode selection changed to index {selectedIndex}: isCustom={isCustomMode}, presetTDP={presetTdpValue}W, legionMode={legionModeValue}, boost={presetTdpBoost}");

            bool isLegion = legionGoDetected?.Value == true;

            if (isLegion)
            {
                // Legion device: use hardware presets via WMI
                // For built-in presets with LegionModeValue (1/2/3), use hardware mode
                // For custom presets (LegionModeValue == 255), use Custom mode + software TDP

                // Sync with Legion Performance Mode ComboBox if using default mode (not custom presets)
                if (!useCustomTDPPresets && LegionPerformanceModeComboBox != null && LegionPerformanceModeComboBox.SelectedIndex != selectedIndex)
                {
                    LegionPerformanceModeComboBox.SelectedIndex = selectedIndex;
                }

                // Send Legion mode to helper - force send even if cached value matches
                if (legionPerformanceMode != null)
                {
                    if (legionPerformanceMode.Value == legionModeValue)
                    {
                        legionPerformanceMode.ForceSetValue(legionModeValue);
                    }
                    else
                    {
                        legionPerformanceMode.SetValue(legionModeValue);
                    }
                }

                // For custom presets without hardware mode (255), also apply TDP via software
                if (legionModeValue == 255 && !isCustomMode && presetTdpValue > 0)
                {
                    // Custom preset on Legion: apply TDP value via software
                    if (TDPSlider != null)
                    {
                        TDPSlider.Value = presetTdpValue;
                    }
                    if (tdp != null)
                    {
                        tdp.SetValue(presetTdpValue);
                        Logger.Info($"Legion device: Applied custom preset TDP {presetTdpValue}W via software");
                    }
                }
                // For actual Custom mode on Legion, restore saved custom TDP value
                else if (isCustomMode)
                {
                    if (TDPSlider != null)
                    {
                        TDPSlider.Value = savedCustomTDP;
                        Logger.Info($"Legion device: Restored custom TDP value to slider: {savedCustomTDP}W");
                    }
                }
            }
            else
            {
                // Generic device: apply TDP value directly based on preset
                if (isCustomMode)
                {
                    // Custom mode: restore saved custom TDP value to slider
                    if (TDPSlider != null)
                    {
                        TDPSlider.Value = savedCustomTDP;
                        Logger.Info($"Restored custom TDP value to slider: {savedCustomTDP}W");
                    }
                }
                else
                {
                    // Preset mode: set TDP directly and update slider to show the value
                    int targetTDP = presetTdpValue > 0 ? presetTdpValue : 15; // Default to 15W if something goes wrong
                    if (TDPSlider != null)
                    {
                        TDPSlider.Value = targetTDP;
                    }
                    if (tdp != null)
                    {
                        tdp.SetValue(targetTDP);
                        Logger.Info($"Generic device: Applied TDP preset {targetTDP}W (mode index {selectedIndex})");
                    }
                }
            }

            // Update TDP slider enabled state based on mode
            UpdateTDPSliderEnabledState();

            // Apply TDP Boost setting from preset (only for custom presets with TdpBoostEnabled set)
            // This should use software TDP control, so only apply when not using Lenovo hardware modes
            if (presetTdpBoost.HasValue && !isCustomMode)
            {
                bool shouldEnableTdpBoost = presetTdpBoost.Value;
                // Only apply if we're using software TDP control (not Lenovo hardware modes)
                // For Legion devices using hardware modes (legionModeValue != 255), TDP Boost is controlled by hardware
                if (legionModeValue == 255 || !(legionGoDetected?.Value == true))
                {
                    if (TDPBoostToggle != null)
                    {
                        TDPBoostToggle.IsOn = shouldEnableTdpBoost;
                    }
                    if (tdpBoostEnabled != null && tdpBoostEnabled.Value != shouldEnableTdpBoost)
                    {
                        tdpBoostEnabled.SetValue(shouldEnableTdpBoost);
                        Logger.Info($"Applied preset TDP Boost: {shouldEnableTdpBoost}");
                    }
                }
            }

            // Save profile when TDP Mode changes (if not during initialization or helper update)
            // Allow save if user-initiated from Quick Tab tile (bypasses isApplyingHelperUpdate)
            // Don't save when Default Game Profile is active (to avoid contaminating user's profile)
            if (!isInitialSync && !isLoadingProfile && SaveTDP && (!isApplyingHelperUpdate || isUserInitiatedTDPModeChange) && defaultGameProfileEnabled?.Value != true)
            {
                // Don't save to game profile if per-game profile is disabled.
                // During game close, helper sends global mode via pipe → ComboBox changes → handler fires.
                // At this point, perGameProfile.Value is already false (pipe message processed) but
                // currentProfileName still points to the game profile (SwitchProfile hasn't run yet).
                // Without this check, the global mode gets saved to the game profile, corrupting it.
                bool isGameProfile = currentProfileName?.StartsWith("Game_") == true;
                if (isGameProfile && perGameProfile?.Value != true)
                {
                    Logger.Warn($"TDP Mode save skipped: per-game profile is disabled but currentProfileName is still '{currentProfileName}' (game closing)");
                }
                else
                {
                    Logger.Info($"Saving TDP Mode change to profile: {currentProfileName}");
                    SaveCurrentSettingsToProfile(currentProfileName);
                }
            }
            else
            {
                Logger.Warn($"TDP Mode save skipped: isInitialSync={isInitialSync}, isApplyingHelperUpdate={isApplyingHelperUpdate}, isLoadingProfile={isLoadingProfile}, SaveTDP={SaveTDP}, isUserInitiatedTDPModeChange={isUserInitiatedTDPModeChange}, defaultGameProfile={defaultGameProfileEnabled?.Value}");
            }
        }

        /// <summary>
        /// Updates TDP slider enabled state based on TDP Mode
        /// For all devices: slider disabled in preset modes (Quiet/Balanced/Performance), enabled in Custom mode
        /// Also updates XY focus bindings to skip disabled TDP slider
        /// </summary>
        private void UpdateTDPSliderEnabledState()
        {
            if (TDPSlider == null) return;

            // If Default Game Profile is active, keep TDP controls disabled
            if (defaultGameProfileEnabled?.Value == true)
            {
                Logger.Debug("TDP slider state update skipped - Default Game Profile is active");
                return;
            }

            // Check if in Custom mode (uses preset system for proper detection)
            bool isCustomMode = IsCustomTdpModeSelected();
            bool isLegion = legionGoDetected?.Value == true;

            // Check if the mode change came from helper pipe sync (not user interaction).
            // When true, widget's profile may have stale values - use helper's property values instead.
            bool isHelperModeSync = legionPerformanceMode?.IsUpdatingUI == true;

            // TDP slider, TDP Boost, and AutoTDP should only be enabled in Custom mode
            // Note: TDP slider also requires tdp property to be ready (IsEnabled is set elsewhere too)
            if (!isCustomMode)
            {
                TDPSlider.IsEnabled = false;
                // Preset modes manage TDP themselves — hide the whole power-limit
                // section (slider, limits line, boost/sticky indicators). It lives
                // inside the TDP Mode card and only appears in Custom mode.
                if (TDPSliderSection != null) TDPSliderSection.Visibility = Visibility.Collapsed;

                // Update display to show preset name and TDP value
                int modeIndex = TDPModeComboBox?.SelectedIndex ?? 1;
                string modeName = "Balanced";
                int presetTdp = GetCurrentPresetTdpValue();

                if (useCustomTDPPresets && tdpPresets != null && modeIndex >= 0 && modeIndex < tdpPresets.Count)
                {
                    modeName = tdpPresets[modeIndex].Name;
                }
                else
                {
                    string[] defaultModeNames = { "Quiet", "Balanced", "Performance" };
                    modeName = (modeIndex >= 0 && modeIndex < defaultModeNames.Length) ? defaultModeNames[modeIndex] : "Balanced";
                }

                if (TDPValueText != null)
                {
                    // Show TDP value for custom presets, mode name for defaults
                    TDPValueText.Text = (useCustomTDPPresets && presetTdp > 0) ? $"{presetTdp}W" : $"{modeName} mode";
                }
                if (CurrentTDPValueText != null)
                {
                    CurrentTDPValueText.Text = (useCustomTDPPresets && presetTdp > 0) ? $"{modeName} ({presetTdp}W)" : $"{modeName} mode";
                }

                // Set flag to prevent toggle handlers from saving forced-off state to LocalSettings
                isUpdatingTDPMode = true;
                bool wasAutoTDPOn = AutoTDPToggle?.IsOn == true;
                bool wasStickyTDPOn = StickyTDPToggle?.IsOn == true;
                try
                {
                    // Also disable TDP Boost and AutoTDP controls in preset modes
                    if (TDPBoostToggle != null)
                    {
                        TDPBoostToggle.IsEnabled = false;
                        TDPBoostToggle.IsOn = false; // Turn off when switching to preset mode (binding handles slider visibility)
                    }
                    if (AutoTDPToggle != null)
                    {
                        AutoTDPToggle.IsEnabled = false;
                        AutoTDPToggle.IsOn = false; // Turn off when switching to preset mode
                    }
                    if (AutoTDPTargetFPSSlider != null) AutoTDPTargetFPSSlider.IsEnabled = false;
                    if (AutoTDPMinSlider != null) AutoTDPMinSlider.IsEnabled = false;
                    if (AutoTDPMaxSlider != null) AutoTDPMaxSlider.IsEnabled = false;
                    if (StickyTDPToggle != null)
                    {
                        StickyTDPToggle.IsEnabled = false;
                        StickyTDPToggle.IsOn = false; // Turn off when switching to preset mode
                    }
                    if (StickyTDPIntervalSlider != null) StickyTDPIntervalSlider.IsEnabled = false;
                }
                finally
                {
                    isUpdatingTDPMode = false;
                }

                // Explicitly notify helper to disable AutoTDP/StickyTDP since toggle handlers were blocked.
                // Skip when:
                // - isLoadingProfile: helper manages state during profile switches, ComboBox may show wrong mode
                // - isHelperModeSync: mode change came from helper pipe sync (not user).
                //   During game close, helper sends global mode → widget shouldn't send stale values back.
                //   During game open, helper sends game mode → widget's profile may have stale AutoTDP.
                if (wasAutoTDPOn && !isLoadingProfile && !isHelperModeSync)
                {
                    autoTDPEnabled?.SetValue(false);
                    Logger.Info("AutoTDP disabled due to TDP mode change away from Custom");
                }
                if (wasStickyTDPOn)
                {
                    StopStickyTDPTimer();
                    Logger.Info("Sticky TDP disabled due to TDP mode change away from Custom");
                }

                // Update XY focus to skip disabled controls
                // TDPModeComboBox -> OSPowerModeComboBox (skip all TDP controls)

                Logger.Debug($"TDP slider disabled - using {modeName} mode");
            }
            else
            {
                // In Custom mode, enable if tdp property is ready
                TDPSlider.IsEnabled = tdp != null;
                if (TDPSliderSection != null) TDPSliderSection.Visibility = Visibility.Visible;

                // Reset the "Limits" line — when switching into Custom mode the non-Custom
                // branch above (or the widget's initial default-to-Balanced state) leaves
                // a stale "Balanced mode" string here. CurrentTDPProperty pushes the real
                // hardware values from the helper, but only on the next CurrentTDP update,
                // which can be deferred until the user applies a setting. Pre-fill from the
                // helper's last-known value if BatchGet already populated it; otherwise
                // show a neutral placeholder so the user doesn't think the helper is in
                // Balanced when it isn't.
                if (CurrentTDPValueText != null)
                {
                    CurrentTDPValueText.Text = !string.IsNullOrEmpty(currentTdp?.Value) ? currentTdp.Value : "-- W";
                }

                // Re-enable TDP Boost, AutoTDP, and Sticky TDP controls in Custom mode
                if (TDPBoostToggle != null) TDPBoostToggle.IsEnabled = true;
                if (AutoTDPToggle != null) AutoTDPToggle.IsEnabled = true;
                if (AutoTDPTargetFPSSlider != null) AutoTDPTargetFPSSlider.IsEnabled = true;
                if (AutoTDPMinSlider != null) AutoTDPMinSlider.IsEnabled = true;
                if (AutoTDPMaxSlider != null) AutoTDPMaxSlider.IsEnabled = true;
                if (StickyTDPToggle != null) StickyTDPToggle.IsEnabled = true;
                if (StickyTDPIntervalSlider != null) StickyTDPIntervalSlider.IsEnabled = true;

                // Restore toggle states when switching back to Custom mode.
                // When SaveAutoTDP/SaveTDP is enabled, use the CURRENT PROFILE's values (source of truth)
                // instead of LocalSettings. LocalSettings stores a global value that can be stale
                // (e.g., AutoTDP=true from a per-game profile bleeds into global via deferred UI updates).
                // Only fall back to LocalSettings when the profile system doesn't manage the setting.
                isUpdatingTDPMode = true;
                try
                {
                    var settings = ApplicationData.Current.LocalSettings;

                    // Skip sending to helper when:
                    // - isLoadingProfile: profile is being loaded, helper manages state
                    // - isHelperModeSync: mode change came from helper pipe sync, widget's profile
                    //   may have stale values (e.g., AutoTDP=false saved during game close)
                    bool canSendToHelper = !isLoadingProfile && !isHelperModeSync;

                    if (TDPBoostToggle != null)
                    {
                        if (SaveTDP)
                        {
                            var profile = GetProfile(currentProfileName);
                            // When helper is syncing mode, use the helper's property value.
                            bool tdpBoostState = isHelperModeSync && this.tdpBoostEnabled != null
                                ? this.tdpBoostEnabled.Value
                                : profile.TDPBoostEnabled;
                            TDPBoostToggle.IsOn = tdpBoostState;
                            if (canSendToHelper)
                                this.tdpBoostEnabled?.SetValue(tdpBoostState);
                            Logger.Debug($"Restored TDP Boost from {(isHelperModeSync ? "helper" : "profile")} '{currentProfileName}': {tdpBoostState}");
                        }
                        else if (settings.Values.TryGetValue("TDPBoostEnabled", out object tdpBoostVal) && tdpBoostVal is bool tdpBoostEnabledVal)
                        {
                            TDPBoostToggle.IsOn = tdpBoostEnabledVal;
                            if (canSendToHelper)
                                this.tdpBoostEnabled?.SetValue(tdpBoostEnabledVal);
                            Logger.Debug($"Restored TDP Boost from LocalSettings: {tdpBoostEnabledVal}");
                        }
                    }

                    if (AutoTDPToggle != null)
                    {
                        isLoadingAutoTDPSettings = true;
                        try
                        {
                            if (SaveAutoTDP)
                            {
                                var profile = GetProfile(currentProfileName);
                                // When helper is syncing mode, use the helper's property value.
                                // The widget's profile may be stale (e.g., AutoTDP=false saved during
                                // game close). The helper's autoTDPEnabled.Value is the source of truth.
                                bool autoTDPState = isHelperModeSync && autoTDPEnabled != null
                                    ? autoTDPEnabled.Value
                                    : profile.AutoTDPEnabled;
                                AutoTDPToggle.IsOn = autoTDPState;
                                if (canSendToHelper)
                                    autoTDPEnabled?.SetValue(autoTDPState);
                                Logger.Debug($"Restored AutoTDP from {(isHelperModeSync ? "helper" : "profile")} '{currentProfileName}': {autoTDPState}");
                            }
                            else if (settings.Values.TryGetValue("AutoTDPEnabled", out object autoTdpVal) && autoTdpVal is bool autoTdpEnabled)
                            {
                                AutoTDPToggle.IsOn = autoTdpEnabled;
                                if (canSendToHelper)
                                    autoTDPEnabled?.SetValue(autoTdpEnabled);
                                Logger.Debug($"Restored AutoTDP from LocalSettings: {autoTdpEnabled}");
                            }
                        }
                        finally
                        {
                            isLoadingAutoTDPSettings = false;
                        }
                    }

                    if (StickyTDPToggle != null && settings.Values.TryGetValue("StickyTDPEnabled", out object stickyVal) && stickyVal is bool stickyEnabled)
                    {
                        StickyTDPToggle.IsOn = stickyEnabled;
                        if (stickyEnabled)
                        {
                            targetTDPLimit = TDPSlider.Value;
                            StartStickyTDPTimer();
                            Logger.Debug($"Restored Sticky TDP enabled - monitoring TDP: {targetTDPLimit}W");
                        }
                        else
                        {
                            StopStickyTDPTimer();
                        }
                        Logger.Debug($"Restored Sticky TDP toggle state from LocalSettings: {stickyEnabled}");
                    }
                }
                finally
                {
                    isUpdatingTDPMode = false;
                }

                Logger.Debug($"TDP slider, TDP Boost, AutoTDP, and Sticky TDP enabled in Custom mode: {TDPSlider.IsEnabled}");

                // Update display to show wattage in Custom mode
                UpdateTDPDisplayText();

                // CRITICAL FIX: Sync TDPProperty.Value with the slider's current visual value
                // When TDP sync is skipped (preset modes), TDPProperty.Value stays at initial value (4).
                // Profile loads set TDPSlider.Value but not TDPProperty.Value.
                // Without this sync, Slider_ValueChanged comparison (newValue != Value) fails
                // because the property's Value doesn't match what the user sees on screen.
                if (tdp != null)
                {
                    int currentSliderValue = (int)TDPSlider.Value;
                    tdp.StopDebounceTimer(); // Cancel any pending debounce
                    tdp.SetValueSilent(currentSliderValue); // Update internal Value without sending

                    // Also send current value to helper to ensure hardware matches UI
                    tdp.ForceSetValue(currentSliderValue);
                    Logger.Info($"Custom mode enabled - synced TDP property to slider value: {currentSliderValue}W");
                }

                // Restore normal XY focus chain in Custom mode
                // TDPModeComboBox -> TDPSlider -> TDPExtrasExpandToggle -> [expanded: TDPBoost/AutoTDP/Sticky] -> OSPowerModeComboBox
                if (TDPModeComboBox != null && OSPowerModeComboBox != null)
                {

                    // Internal chain when TDP Extras is expanded

                    // TDPExtrasExpandToggle.XYFocusDown depends on expanded state
                    bool isTDPExtrasOpen = TDPExtrasContent?.Visibility == Visibility.Visible;
                    Logger.Debug($"XY focus restored for Custom mode (TDP Extras expanded: {isTDPExtrasOpen})");
                }
            }
        }

        // LegionCustomTDPSlider_ValueChanged removed — the Slow/Fast/Peak sliders no longer
        // exist on the Legion tab. The Performance-tab master TDP slider + per-profile TDP
        // Boost deltas own all three values now.

        /// <summary>
        /// Updates the TDP display text when slider value changes (Custom mode only)
        /// </summary>
        private void TDPSlider_ValueChanged_UpdateDisplay(object sender, Windows.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            // Only update display if in Custom mode and slider is enabled
            // The slider is disabled in preset modes, so this prevents overwriting mode names
            if (IsCustomTdpModeSelected() && TDPSlider?.IsEnabled == true)
            {
                UpdateTDPDisplayText();
            }
        }

        /// <summary>
        /// Updates TDP display text to show current wattage (for Custom mode)
        /// </summary>
        private void UpdateTDPDisplayText()
        {
            if (TDPSlider == null) return;

            int tdpValue = (int)TDPSlider.Value;
            if (TDPValueText != null)
            {
                TDPValueText.Text = $"{tdpValue}W";
            }
            // Note: CurrentTDPValueText is updated by the helper with actual hardware limits
            // via CurrentTDPProperty. Don't set it here - slider values would overwrite
            // the detailed hardware readout (e.g., "S:21W F:21W L:21W").
        }

    }
}
