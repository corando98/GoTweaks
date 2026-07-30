using System;
using System.Collections.Generic;
using System.Linq;
using Windows.Storage;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace XboxGamingBar
{
    public sealed partial class GamingWidget
    {
        // --- Tab Settings: user-configurable nav tab order, visibility, default tab ---
        //
        // Persisted in LocalSettings:
        //   TabOrder  — csv of tab Tags in display order
        //   TabHidden — csv of Tags the user hid
        //   DefaultTab — Tag selected whenever the widget opens ("" = last used)
        //
        // Two visibility layers compose: DEVICE availability (Legion/GPD/Scaling gates,
        // which used to write pill.Visibility directly) and USER hidden. A pill shows
        // only when device-available AND not user-hidden, so a device gate re-running
        // after sync can never resurrect a tab the user hid, and hiding can never
        // force-show a tab the device doesn't have.

        private static readonly string[] DefaultTabOrder = { "Setup", "Quick", "Performance", "Game", "AMD", "Scaling", "Legion", "GPD", "System" };

        private List<string> userTabOrder = new List<string>(DefaultTabOrder);
        private readonly HashSet<string> userHiddenTabs = new HashSet<string>();
        private string defaultTabTag = "";
        private bool isLoadingTabPrefs;

        // Device availability, updated by the existing device gates. Tabs without an
        // entry are unconditionally available. Legion/GPD start Collapsed in XAML.
        private readonly Dictionary<string, bool> tabDeviceAvailable = new Dictionary<string, bool>
        {
            { "Legion", false },
            { "GPD", false },
            // Setup only shows once the helper reports a required tool missing —
            // fully-installed systems never see it flicker in.
            { "Setup", false },
        };

        private RadioButton GetNavPillByTag(string tag) =>
            MainNavPanel.Children.OfType<RadioButton>().FirstOrDefault(rb => (rb.Tag as string) == tag);

        private FrameworkElement GetTabRowByTag(string tag) => FindName($"TabRow_{tag}") as FrameworkElement;
        private ToggleSwitch GetTabShowToggleByTag(string tag) => FindName($"TabShow_{tag}") as ToggleSwitch;

        private void LoadTabSettings()
        {
            isLoadingTabPrefs = true;
            try
            {
                var values = ApplicationData.Current.LocalSettings.Values;

                userTabOrder = EffectiveTabOrder(values["TabOrder"] as string);

                userHiddenTabs.Clear();
                var hiddenCsv = values["TabHidden"] as string;
                if (!string.IsNullOrEmpty(hiddenCsv))
                {
                    foreach (var tag in hiddenCsv.Split(','))
                    {
                        // System is never hideable — it hosts these settings.
                        if (DefaultTabOrder.Contains(tag) && tag != "System")
                        {
                            userHiddenTabs.Add(tag);
                        }
                    }
                }

                defaultTabTag = values["DefaultTab"] as string ?? "";
                if (!DefaultTabOrder.Contains(defaultTabTag))
                {
                    defaultTabTag = "";
                }

                // Setup-tab override ("Show Setup tab" in Customization). Loaded here
                // (under the isLoadingTabPrefs guard) so the Toggled handler doesn't
                // re-persist during init; UpdateSetupTabStatus applies it after sync.
                setupTabForced = values["SetupTabForced"] as bool? ?? false;
                if (ShowSetupTabToggle != null)
                {
                    ShowSetupTabToggle.IsOn = setupTabForced;
                }
                if (setupTabForced)
                {
                    // Forced tab shows immediately at construction instead of waiting
                    // for the first sync-driven UpdateSetupTabStatus.
                    tabDeviceAvailable["Setup"] = true;
                }

                // Combo index 0 = "Last used"; 1..N follow DefaultTabOrder.
                if (DefaultTabComboBox != null)
                {
                    int idx = defaultTabTag == "" ? 0 : Array.IndexOf(DefaultTabOrder, defaultTabTag) + 1;
                    DefaultTabComboBox.SelectedIndex = ClampComboIndex(DefaultTabComboBox, idx);
                }

                foreach (var tag in DefaultTabOrder)
                {
                    var toggle = GetTabShowToggleByTag(tag);
                    if (toggle != null)
                    {
                        toggle.IsOn = !userHiddenTabs.Contains(tag);
                    }
                }

                ApplyTabPrefs();
            }
            catch (Exception ex)
            {
                Logger.Error($"LoadTabSettings failed: {ex.Message}");
            }
            finally
            {
                isLoadingTabPrefs = false;
            }
        }

        /// <summary>
        /// Stored order, filtered to known tags, with any tag missing from storage
        /// (e.g. a tab added by an app update) appended in default position order —
        /// an update can add tabs without users ever losing them.
        /// </summary>
        private static List<string> EffectiveTabOrder(string storedCsv)
        {
            var order = new List<string>();
            if (!string.IsNullOrEmpty(storedCsv))
            {
                foreach (var tag in storedCsv.Split(','))
                {
                    if (DefaultTabOrder.Contains(tag) && !order.Contains(tag))
                    {
                        order.Add(tag);
                    }
                }
            }
            foreach (var tag in DefaultTabOrder)
            {
                if (!order.Contains(tag))
                {
                    order.Add(tag);
                }
            }
            return order;
        }

        private void SaveTabPrefs()
        {
            try
            {
                var values = ApplicationData.Current.LocalSettings.Values;
                values["TabOrder"] = string.Join(",", userTabOrder);
                values["TabHidden"] = string.Join(",", userHiddenTabs);
                values["DefaultTab"] = defaultTabTag;
            }
            catch (Exception ex)
            {
                Logger.Error($"SaveTabPrefs failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Applies the stored order to the nav strip and the settings rows, then
        /// refreshes visibility. Idempotent; safe to call after device gates run.
        /// </summary>
        private void ApplyTabPrefs()
        {
            try
            {
                for (int target = 0; target < userTabOrder.Count; target++)
                {
                    var pill = GetNavPillByTag(userTabOrder[target]);
                    if (pill != null && MainNavPanel.Children.IndexOf(pill) != target)
                    {
                        MainNavPanel.Children.Remove(pill);
                        MainNavPanel.Children.Insert(Math.Min(target, MainNavPanel.Children.Count), pill);
                    }

                    var row = GetTabRowByTag(userTabOrder[target]);
                    if (row != null && TabSettingsList != null)
                    {
                        int current = TabSettingsList.Children.IndexOf(row);
                        if (current >= 0 && current != target)
                        {
                            TabSettingsList.Children.Remove(row);
                            TabSettingsList.Children.Insert(Math.Min(target, TabSettingsList.Children.Count), row);
                        }
                    }
                }

                foreach (var tag in DefaultTabOrder)
                {
                    RefreshNavItemVisibility(tag);
                }

                EnsureValidActiveTab();
                UpdateNavPillXYFocus();
            }
            catch (Exception ex)
            {
                Logger.Error($"ApplyTabPrefs failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Device gates (Legion/GPD detection, Lossless Scaling install check) call
        /// this instead of writing pill.Visibility directly, so user-hidden state
        /// survives every gate re-run.
        /// </summary>
        private void SetNavTabDeviceAvailability(string tag, bool available)
        {
            tabDeviceAvailable[tag] = available;
            RefreshNavItemVisibility(tag);
            EnsureValidActiveTab();
            UpdateNavPillXYFocus();
        }

        private void RefreshNavItemVisibility(string tag)
        {
            var pill = GetNavPillByTag(tag);
            if (pill == null) return;
            bool available = !tabDeviceAvailable.TryGetValue(tag, out bool avail) || avail;
            pill.Visibility = (available && !userHiddenTabs.Contains(tag)) ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// If the active tab just became hidden/unavailable, switch to the first
        /// visible tab so the widget never shows a header with no selection.
        /// </summary>
        private void EnsureValidActiveTab()
        {
            // Only act when a CHECKED pill became invisible. When no pill is checked
            // at all (widget construction), leave selection to the normal init flow —
            // forcing one here would fire NavRadioButton_Checked before the tab
            // machinery is ready.
            var checkedPill = MainNavPanel.Children.OfType<RadioButton>().FirstOrDefault(rb => rb.IsChecked == true);
            if (checkedPill == null || checkedPill.Visibility == Visibility.Visible) return;

            var visible = GetVisibleNavigationItems();
            if (visible.Count == 0) return;

            var target = visible[0];
            Logger.Info($"Active tab '{checkedPill.Tag}' no longer visible — switching to '{target.Tag}'");
            ArmUserNavIntent(target.Tag as string);
            target.IsChecked = true;
        }

        /// <summary>
        /// Called from the widget-visible path: selects the configured default tab
        /// each time the widget opens. No-op when unset, already active, or hidden.
        /// </summary>
        private void ApplyDefaultTabOnOpen()
        {
            if (string.IsNullOrEmpty(defaultTabTag)) return;
            var pill = GetNavPillByTag(defaultTabTag);
            if (pill == null || pill.Visibility != Visibility.Visible) return;
            if (pill.IsChecked == true) return;

            Logger.Info($"Default tab on open: switching to '{defaultTabTag}'");
            ArmUserNavIntent(defaultTabTag);
            pill.IsChecked = true;
        }

        private void TabMoveUp_Click(object sender, RoutedEventArgs e) => MoveTab((sender as FrameworkElement)?.Tag as string, -1);
        private void TabMoveDown_Click(object sender, RoutedEventArgs e) => MoveTab((sender as FrameworkElement)?.Tag as string, +1);

        private void MoveTab(string tag, int delta)
        {
            if (string.IsNullOrEmpty(tag)) return;
            int idx = userTabOrder.IndexOf(tag);
            int other = idx + delta;
            if (idx < 0 || other < 0 || other >= userTabOrder.Count) return;

            userTabOrder[idx] = userTabOrder[other];
            userTabOrder[other] = tag;
            SaveTabPrefs();
            ApplyTabPrefs();
        }

        private void TabShow_Toggled(object sender, RoutedEventArgs e)
        {
            if (isLoadingTabPrefs) return;
            var toggle = sender as ToggleSwitch;
            var tag = toggle?.Tag as string;
            if (string.IsNullOrEmpty(tag)) return;

            if (tag == "System")
            {
                toggle.IsOn = true; // never hideable
                return;
            }

            if (toggle.IsOn) userHiddenTabs.Remove(tag);
            else userHiddenTabs.Add(tag);

            SaveTabPrefs();
            RefreshNavItemVisibility(tag);
            EnsureValidActiveTab();
            UpdateNavPillXYFocus();
        }

        private void DefaultTabComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (isLoadingTabPrefs) return;
            int idx = DefaultTabComboBox?.SelectedIndex ?? 0;
            defaultTabTag = (idx <= 0 || idx > DefaultTabOrder.Length) ? "" : DefaultTabOrder[idx - 1];
            SaveTabPrefs();
        }
    }
}
