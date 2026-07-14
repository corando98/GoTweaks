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
        private bool isAdvancedExpanded = false;
        private bool isLoadingCPUCoreConfig = false;
        private int totalPCores = 3;  // Default for Z2E
        private int totalECores = 5;  // Default for Z2E
        private int totalCores = 8;   // Total logical cores
        private int activePCores = 3;
        private int activeECores = 5;
        private int parkedCores = 0;  // Number of cores to park (0 = all active)
        private bool isHybridCPU = false;
        private bool isLoadingCoreParking = false;

        private void AdvancedExpandButton_Click(object sender, RoutedEventArgs e)
        {
            isAdvancedExpanded = !isAdvancedExpanded;

            if (AdvancedContent != null)
            {
                AdvancedContent.Visibility = isAdvancedExpanded ? Visibility.Visible : Visibility.Collapsed;
            }

            if (AdvancedExpandIcon != null)
            {
                // E70D = ChevronDown, E70E = ChevronUp
                AdvancedExpandIcon.Glyph = isAdvancedExpanded ? "\uE70E" : "\uE70D";
            }

            // Load power plans when expanding for the first time
            if (isAdvancedExpanded && availablePowerPlans.Count == 0)
            {
                LoadPowerPlans();
            }
        }

        private void CoreParkingComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (isLoadingCoreParking) return;
            if (CoreParkingComboBox?.SelectedItem is ComboBoxItem selected && selected.Tag is string tagStr)
            {
                if (int.TryParse(tagStr, out int activeCores))
                {
                    parkedCores = totalCores - activeCores;
                    UpdateCoreParkingDescription(activeCores);
                    UpdateCPUCoreConfigSummary();
                    SaveCoreParkingToStorage();
                    SendCoreParkingToHelper(activeCores);
                    Logger.Info($"Core parking changed to: {activeCores} active cores ({parkedCores} parked)");
                }
            }
        }

        private void UpdateCoreParkingDescription(int activeCores)
        {
            if (CoreParkingDescription != null)
            {
                if (activeCores >= totalCores)
                {
                    CoreParkingDescription.Text = "All cores active";
                }
                else
                {
                    CoreParkingDescription.Text = $"{totalCores - activeCores} cores parked";
                }
            }
        }

        private void SetupCoreParkingUI()
        {
            isLoadingCoreParking = true;
            try
            {
                // Get total logical processor count
                totalCores = Environment.ProcessorCount;

                if (CoreParkingComboBox != null)
                {
                    CoreParkingComboBox.Items.Clear();

                    // Add "All" option first
                    var allItem = new ComboBoxItem { Content = $"All ({totalCores})", Tag = totalCores.ToString() };
                    CoreParkingComboBox.Items.Add(allItem);

                    // Add options for reducing cores (by 2s for larger counts)
                    int step = totalCores > 8 ? 2 : 1;
                    for (int i = totalCores - step; i >= 2; i -= step)
                    {
                        var item = new ComboBoxItem { Content = i.ToString(), Tag = i.ToString() };
                        CoreParkingComboBox.Items.Add(item);
                    }

                    // Load saved setting
                    LoadCoreParkingFromStorage();
                }

                Logger.Info($"Core Parking UI setup: {totalCores} total cores");
            }
            finally
            {
                isLoadingCoreParking = false;
            }
        }

        private void SaveCoreParkingToStorage()
        {
            try
            {
                var settings = ApplicationData.Current.LocalSettings;
                settings.Values["CoreParkingActiveCores"] = totalCores - parkedCores;
                Logger.Info($"Saved core parking: {totalCores - parkedCores} active");
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to save core parking: {ex.Message}");
            }
        }

        private void LoadCoreParkingFromStorage()
        {
            try
            {
                var settings = ApplicationData.Current.LocalSettings;
                int activeCores = totalCores; // Default to all active

                if (settings.Values.TryGetValue("CoreParkingActiveCores", out object val) && val is int saved)
                {
                    activeCores = Math.Min(saved, totalCores); // Clamp to current max
                }

                parkedCores = totalCores - activeCores;

                // Select the matching item
                if (CoreParkingComboBox != null)
                {
                    foreach (ComboBoxItem item in CoreParkingComboBox.Items)
                    {
                        if (item.Tag is string tagStr && int.TryParse(tagStr, out int tagVal) && tagVal == activeCores)
                        {
                            CoreParkingComboBox.SelectedItem = item;
                            break;
                        }
                    }

                    // If no match, select first (all cores)
                    if (CoreParkingComboBox.SelectedItem == null && CoreParkingComboBox.Items.Count > 0)
                    {
                        CoreParkingComboBox.SelectedIndex = 0;
                    }
                }

                UpdateCoreParkingDescription(activeCores);
                Logger.Info($"Loaded core parking: {activeCores} active");
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to load core parking: {ex.Message}");
            }
        }

        private void SendCoreParkingToHelper(int activeCores)
        {
            // Calculate percentage for CPMAXCORES
            // activeCores / totalCores * 100 = percentage of cores that can be unparked
            int percent = (int)Math.Ceiling((double)activeCores / totalCores * 100);
            percent = Math.Clamp(percent, 1, 100); // At least 1%, max 100%

            if (coreParkingPercent != null)
            {
                coreParkingPercent.SetValue(percent);
                Logger.Info($"Core parking: set {percent}% ({activeCores}/{totalCores} cores)");
            }
        }

        private void PCoreCountComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (isLoadingCPUCoreConfig) return;
            if (PCoreCountComboBox?.SelectedItem is ComboBoxItem selected && selected.Tag is string tagStr)
            {
                if (int.TryParse(tagStr, out int count))
                {
                    // Prevent both P-Cores and E-Cores from being 0
                    if (count == 0 && activeECores == 0)
                    {
                        Logger.Warn("Cannot disable both P-Cores and E-Cores, reverting selection");
                        // Revert to previous value
                        isLoadingCPUCoreConfig = true;
                        UpdatePCoreComboBox();
                        isLoadingCPUCoreConfig = false;
                        return;
                    }

                    activePCores = count;
                    UpdateCPUCoreConfigSummary();
                    SaveCPUCoreConfigToStorage();
                    SendCPUCoreConfigToHelper();
                    if (SaveCPUAffinity)
                    {
                        SaveCurrentSettingsToProfile(currentProfileName);
                    }
                    Logger.Info($"P-Core count changed to: {activePCores}");
                }
            }
        }

        private void ECoreCountComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (isLoadingCPUCoreConfig) return;
            if (ECoreCountComboBox?.SelectedItem is ComboBoxItem selected && selected.Tag is string tagStr)
            {
                if (int.TryParse(tagStr, out int count))
                {
                    // Prevent both P-Cores and E-Cores from being 0
                    if (count == 0 && activePCores == 0)
                    {
                        Logger.Warn("Cannot disable both P-Cores and E-Cores, reverting selection");
                        // Revert to previous value
                        isLoadingCPUCoreConfig = true;
                        UpdateECoreComboBox();
                        isLoadingCPUCoreConfig = false;
                        return;
                    }

                    activeECores = count;
                    UpdateCPUCoreConfigSummary();
                    SaveCPUCoreConfigToStorage();
                    SendCPUCoreConfigToHelper();
                    if (SaveCPUAffinity)
                    {
                        SaveCurrentSettingsToProfile(currentProfileName);
                    }
                    Logger.Info($"E-Core count changed to: {activeECores}");
                }
            }
        }

        private void SendCPUCoreConfigToHelper()
        {
            if (cpuCoreActiveConfig != null && isHybridCPU)
            {
                // Send affinity config
                string configString = $"{activePCores},{activeECores}";
                cpuCoreActiveConfig.SetValue(configString);
                Logger.Info($"Sent CPU core config to helper: {configString}");

                // Also send core parking percentage based on total active cores
                // For hybrid: active cores = activePCores threads + activeECores threads
                // Assuming SMT: P-Cores have 2 threads, E-Cores have 1 thread (AMD Z2E)
                int activeThreads = (activePCores * 2) + activeECores;
                int percent = (int)Math.Ceiling((double)activeThreads / totalCores * 100);
                percent = Math.Clamp(percent, 1, 100);

                if (coreParkingPercent != null)
                {
                    coreParkingPercent.SetValue(percent);
                    Logger.Info($"Core parking: set {percent}% ({activeThreads}/{totalCores} threads)");
                }
            }
        }

        private void ForceParkModeToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (ForceParkModeToggle == null) return;
            if (isLoadingCPUCoreConfig) return;

            bool enabled = ForceParkModeToggle.IsOn;
            Logger.Info($"Force Park Mode toggled to: {enabled}");

            // Send to helper
            forceParkMode?.SetValue(enabled);

            // Save to local settings
            var settings = ApplicationData.Current.LocalSettings;
            settings.Values["ForceParkMode"] = enabled;
        }

        // True while the toggle is being updated programmatically (helper sync) so
        // Toggled doesn't record a synced device default as an explicit user choice.
        private bool isSyncingDgpEnabledToggle;

        private void ForceDefaultGameProfileToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (ForceDefaultGameProfileToggle == null || isSyncingDgpEnabledToggle) return;

            bool enabled = ForceDefaultGameProfileToggle.IsOn;
            Logger.Info($"Enable Default Game Profiles toggled to: {enabled}");

            // Send to helper
            forceDefaultGameProfile?.SetValue(enabled);

            // Save to local settings
            var settings = ApplicationData.Current.LocalSettings;
            settings.Values["ForceDefaultGameProfile"] = enabled;
        }

        /// <summary>
        /// Reflects the helper's Default-Game-Profiles enabled state into the System-tab
        /// toggle. The helper default is device-dependent (on for auto-detected hardware),
        /// so the toggle can't just load a static local value.
        /// </summary>
        private void OnDgpEnabledSynced()
        {
            _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
            {
                if (ForceDefaultGameProfileToggle == null || forceDefaultGameProfile == null) return;
                bool enabled = forceDefaultGameProfile.Value;
                if (ForceDefaultGameProfileToggle.IsOn != enabled)
                {
                    isSyncingDgpEnabledToggle = true;
                    try { ForceDefaultGameProfileToggle.IsOn = enabled; }
                    finally { isSyncingDgpEnabledToggle = false; }
                }
            });
        }
        private void UpdateCPUCoreConfigSummary()
        {
            // Update the Advanced card summary with current settings
            if (AdvancedSummary != null)
            {
                int activeCoresParking = totalCores - parkedCores;
                if (isHybridCPU)
                {
                    if (parkedCores > 0)
                    {
                        AdvancedSummary.Text = $"Parking: {activeCoresParking}/{totalCores} cores | Affinity: {activePCores}P + {activeECores}E";
                    }
                    else
                    {
                        AdvancedSummary.Text = $"Affinity: {activePCores}P + {activeECores}E cores";
                    }
                }
                else
                {
                    if (parkedCores > 0)
                    {
                        AdvancedSummary.Text = $"Core parking: {activeCoresParking}/{totalCores} cores active";
                    }
                    else
                    {
                        AdvancedSummary.Text = "Core parking and affinity settings";
                    }
                }
            }
        }

        private void SaveCPUCoreConfigToStorage()
        {
            try
            {
                var settings = ApplicationData.Current.LocalSettings;
                settings.Values["ActivePCores"] = activePCores;
                settings.Values["ActiveECores"] = activeECores;
                Logger.Info($"Saved CPU core config: P={activePCores}, E={activeECores}");
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to save CPU core config: {ex.Message}");
            }
        }

        private void LoadCPUCoreConfigFromStorage()
        {
            isLoadingCPUCoreConfig = true;
            try
            {
                var settings = ApplicationData.Current.LocalSettings;

                if (settings.Values.TryGetValue("ActivePCores", out object pObj) && pObj is int pCores)
                {
                    activePCores = pCores;
                }

                if (settings.Values.TryGetValue("ActiveECores", out object eObj) && eObj is int eCores)
                {
                    activeECores = eCores;
                }

                // Load Force Park Mode setting
                if (settings.Values.TryGetValue("ForceParkMode", out object fpObj) && fpObj is bool fpEnabled)
                {
                    if (ForceParkModeToggle != null)
                    {
                        ForceParkModeToggle.IsOn = fpEnabled;
                    }
                    // Send to helper on startup
                    forceParkMode?.SetValue(fpEnabled);
                    Logger.Info($"Loaded Force Park Mode: {fpEnabled}");
                }

                // Update UI
                UpdatePCoreComboBox();
                UpdateECoreComboBox();
                UpdateCPUCoreConfigSummary();

                Logger.Info($"Loaded CPU core config: P={activePCores}, E={activeECores}");
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to load CPU core config: {ex.Message}");
            }
            finally
            {
                isLoadingCPUCoreConfig = false;
            }
        }

        private void UpdatePCoreComboBox()
        {
            if (PCoreCountComboBox == null) return;

            foreach (ComboBoxItem item in PCoreCountComboBox.Items)
            {
                if (item.Tag is string tagStr && int.TryParse(tagStr, out int val) && val == activePCores)
                {
                    PCoreCountComboBox.SelectedItem = item;
                    break;
                }
            }
        }

        private void UpdateECoreComboBox()
        {
            if (ECoreCountComboBox == null) return;

            foreach (ComboBoxItem item in ECoreCountComboBox.Items)
            {
                if (item.Tag is string tagStr && int.TryParse(tagStr, out int val) && val == activeECores)
                {
                    ECoreCountComboBox.SelectedItem = item;
                    break;
                }
            }
        }

        private void SetupCPUCoreConfigUI(int pCoreCount, int eCoreCount)
        {
            isLoadingCPUCoreConfig = true;
            try
            {
                totalPCores = pCoreCount;
                totalECores = eCoreCount;
                isHybridCPU = pCoreCount > 0 && eCoreCount > 0;

                // For hybrid CPUs: show affinity section, hide core parking dropdown
                // For non-hybrid: show core parking dropdown, hide affinity section
                if (CoreAffinitySection != null)
                {
                    CoreAffinitySection.Visibility = isHybridCPU ? Visibility.Visible : Visibility.Collapsed;
                }
                if (CoreParkingSection != null)
                {
                    CoreParkingSection.Visibility = isHybridCPU ? Visibility.Collapsed : Visibility.Visible;
                }

                // Setup core parking UI for non-hybrid CPUs
                if (!isHybridCPU)
                {
                    SetupCoreParkingUI();
                }

                if (!isHybridCPU) return;

                // Populate P-Core combobox
                if (PCoreCountComboBox != null)
                {
                    PCoreCountComboBox.Items.Clear();
                    for (int i = 0; i <= pCoreCount; i++)
                    {
                        var item = new ComboBoxItem { Content = i.ToString(), Tag = i.ToString() };
                        PCoreCountComboBox.Items.Add(item);
                    }
                }

                // Populate E-Core combobox
                if (ECoreCountComboBox != null)
                {
                    ECoreCountComboBox.Items.Clear();
                    for (int i = 0; i <= eCoreCount; i++)
                    {
                        var item = new ComboBoxItem { Content = i.ToString(), Tag = i.ToString() };
                        ECoreCountComboBox.Items.Add(item);
                    }
                }

                // Load saved config or use defaults (all cores active)
                LoadCPUCoreConfigFromStorage();

                // Ensure at least 1 core total is active
                if (activePCores == 0 && activeECores == 0)
                {
                    activePCores = pCoreCount;
                    activeECores = eCoreCount;
                }

                UpdatePCoreComboBox();
                UpdateECoreComboBox();
                UpdateCPUCoreConfigSummary();

                // Send the saved config to helper to apply on startup
                SendCPUCoreConfigToHelper();

                Logger.Info($"CPU Core Config UI setup: {pCoreCount}P + {eCoreCount}E cores (hybrid={isHybridCPU})");
            }
            finally
            {
                isLoadingCPUCoreConfig = false;
            }
        }

    }
}
