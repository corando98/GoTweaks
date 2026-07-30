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
        /// Handle Quick Metrics toggle change
        /// </summary>
        private async void QuickMetricsToggle_Toggled(object sender, RoutedEventArgs e)
        {
            try
            {
                quickMetricsEnabled = QuickMetricsToggle.IsOn;

                // Save setting to local storage
                var settings = ApplicationData.Current.LocalSettings;
                settings.Values[QuickMetricsEnabledKey] = quickMetricsEnabled;

                // Update visibility of metrics row and selection panel
                if (QuickMetricsRow != null)
                    QuickMetricsRow.Visibility = quickMetricsEnabled ? Visibility.Visible : Visibility.Collapsed;
                if (MetricsSelectionPanel != null)
                    MetricsSelectionPanel.Visibility = quickMetricsEnabled ? Visibility.Visible : Visibility.Collapsed;

                // Rebuild the metrics grid if enabling
                if (quickMetricsEnabled)
                {
                    RebuildMetricsGrid();
                }

                // Notify helper to start/stop pushing metrics
                if (App.IsConnected)
                {
                    var request = new Windows.Foundation.Collections.ValueSet
                    {
                        { "Command", (int)Shared.Enums.Command.Set },
                        { "Function", (int)Shared.Enums.Function.QuickMetricsEnabled },
                        { "Content", quickMetricsEnabled }
                    };
                    await App.SendMessageAsync(request);
                    Logger.Info($"Quick Metrics toggle set to: {quickMetricsEnabled}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error handling Quick Metrics toggle: {ex.Message}");
            }
        }

        /// <summary>
        /// Update Quick Metrics display from helper push data
        /// </summary>
        private void UpdateQuickMetrics(string json)
        {
            try
            {
                if (string.IsNullOrEmpty(json)) return;

                // Skip if Game Bar has replaced this widget instance. Otherwise the
                // dispatched UpdateMetricsDisplay below will hit detached XAML elements
                // and throw "COM object that has been separated from its underlying RCW".
                if (App.GetActiveGamingWidget() != this) return;

                // Parse all metrics from JSON
                var matches = System.Text.RegularExpressions.Regex.Matches(json,
                    @"""(\w+)""\s*:\s*(-?\d+\.?\d*|true|false)");

                foreach (System.Text.RegularExpressions.Match match in matches)
                {
                    var key = match.Groups[1].Value;
                    var value = match.Groups[2].Value;

                    if (key == "isCharging")
                    {
                        currentMetricsIsCharging = value == "true";
                    }
                    else if (double.TryParse(value, out double numValue))
                    {
                        currentMetricsData[key] = numValue;
                    }
                }

                // Update UI elements on dispatcher thread
                _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                {
                    try
                    {
                        UpdateMetricsDisplay();
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Error updating Quick Metrics UI: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                Logger.Error($"Error parsing Quick Metrics JSON: {ex.Message}");
            }
        }

        /// <summary>
        /// Update the metrics display based on current data and selected metrics
        /// </summary>
        private void UpdateMetricsDisplay()
        {
            foreach (var metricType in selectedMetrics)
            {
                if (!metricDefinitions.TryGetValue(metricType, out var info) || info.ValueTextBlock == null)
                    continue;

                string displayValue = "--";
                string label = info.Label;

                switch (metricType)
                {
                    case MetricType.BatteryDrain:
                        if (currentMetricsData.TryGetValue("batteryDrain", out var drain))
                        {
                            if (drain > 0)
                                displayValue = $"{drain:F1}W";
                            else if (drain < 0)
                                displayValue = $"+{-drain:F1}W";
                            else
                                displayValue = "0.0W"; // on AC, battery idle — "--W" read as broken
                        }
                        break;

                    case MetricType.BatteryLevel:
                        if (currentMetricsData.TryGetValue("batteryLevel", out var level) && level >= 0)
                            displayValue = $"{level:F0}%";
                        break;

                    case MetricType.CPUUsage:
                        if (currentMetricsData.TryGetValue("cpuUsage", out var cpuUse) && cpuUse >= 0)
                            displayValue = $"{cpuUse:F0}%";
                        break;

                    case MetricType.CPUTemp:
                        if (currentMetricsData.TryGetValue("cpuTemp", out var cpuTemp) && cpuTemp > 0)
                            displayValue = $"{cpuTemp:F0}°";
                        break;

                    case MetricType.CPUWattage:
                        if (currentMetricsData.TryGetValue("cpuWattage", out var cpuWatt) && cpuWatt >= 0)
                            displayValue = $"{cpuWatt:F1}W";
                        break;

                    case MetricType.GPUUsage:
                        if (currentMetricsData.TryGetValue("gpuUsage", out var gpuUse) && gpuUse >= 0)
                            displayValue = $"{gpuUse:F0}%";
                        break;

                    case MetricType.GPUTemp:
                        if (currentMetricsData.TryGetValue("gpuTemp", out var gpuTemp) && gpuTemp > 0)
                            displayValue = $"{gpuTemp:F0}°";
                        break;

                    case MetricType.GPUWattage:
                        if (currentMetricsData.TryGetValue("gpuWattage", out var gpuWatt) && gpuWatt >= 0)
                            displayValue = $"{gpuWatt:F1}W";
                        break;

                    case MetricType.MemoryUsage:
                        if (currentMetricsData.TryGetValue("memoryUsage", out var memUse) && memUse >= 0)
                            displayValue = $"{memUse:F0}%";
                        break;

                    case MetricType.TimeRemaining:
                        currentMetricsData.TryGetValue("timeRemaining", out var timeRem);
                        currentMetricsData.TryGetValue("timeToFull", out var timeFull);
                        if (currentMetricsIsCharging && timeFull > 0)
                        {
                            var hours = (int)(timeFull / 3600);
                            var mins = (int)((timeFull % 3600) / 60);
                            displayValue = $"{hours}:{mins:D2}";
                            label = "To Full";
                        }
                        else if (!currentMetricsIsCharging && timeRem > 0)
                        {
                            var hours = (int)(timeRem / 3600);
                            var mins = (int)((timeRem % 3600) / 60);
                            displayValue = $"{hours}:{mins:D2}";
                            label = "Remaining";
                        }
                        else
                        {
                            displayValue = "--:--";
                            label = currentMetricsIsCharging ? "Charging" : "Time";
                        }
                        break;
                }

                info.ValueTextBlock.Text = displayValue;
                if (info.LabelTextBlock != null)
                    info.LabelTextBlock.Text = label;
            }
        }

        /// <summary>
        /// Update checkbox states based on selected metrics
        /// </summary>
        private void UpdateMetricCheckboxes()
        {
            // Guard to prevent MetricCheckBox_Changed from firing during programmatic updates.
            // Without this, setting IsChecked=true on checkboxes fires the handler, which sees
            // selectedMetrics.Count >= MaxSelectedMetrics and reverts the checkbox to false,
            // triggering Unchecked which removes the metric from the list.
            isUpdatingMetricCheckboxes = true;
            try
            {
                // Map checkboxes to metric types
                var checkboxMap = new Dictionary<CheckBox, MetricType>
                {
                    { MetricCheck_BatteryDrain, MetricType.BatteryDrain },
                    { MetricCheck_BatteryLevel, MetricType.BatteryLevel },
                    { MetricCheck_CPUUsage, MetricType.CPUUsage },
                    { MetricCheck_CPUTemp, MetricType.CPUTemp },
                    { MetricCheck_CPUWattage, MetricType.CPUWattage },
                    { MetricCheck_GPUUsage, MetricType.GPUUsage },
                    { MetricCheck_GPUTemp, MetricType.GPUTemp },
                    { MetricCheck_GPUWattage, MetricType.GPUWattage },
                    { MetricCheck_MemoryUsage, MetricType.MemoryUsage },
                    { MetricCheck_TimeRemaining, MetricType.TimeRemaining }
                };

                foreach (var kvp in checkboxMap)
                {
                    if (kvp.Key != null)
                        kvp.Key.IsChecked = selectedMetrics.Contains(kvp.Value);
                }

                UpdateMetricsSelectionCount();
            }
            finally
            {
                isUpdatingMetricCheckboxes = false;
            }
        }

        /// <summary>
        /// Update the metrics selection count display
        /// </summary>
        private void UpdateMetricsSelectionCount()
        {
            if (MetricsSelectionCount != null)
            {
                MetricsSelectionCount.Text = $"{selectedMetrics.Count}/{MaxSelectedMetrics} selected";
                MetricsSelectionCount.Foreground = new SolidColorBrush(
                    selectedMetrics.Count >= MaxSelectedMetrics
                        ? Windows.UI.Color.FromArgb(255, 255, 150, 100)  // Orange when at max
                        : Windows.UI.Color.FromArgb(255, 102, 102, 102)); // Gray otherwise
            }
        }

        /// <summary>
        /// Handle metric checkbox changes
        /// </summary>
        private void MetricCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (isUpdatingMetricCheckboxes) return;
            if (!(sender is CheckBox checkbox)) return;

            // Map checkbox to metric type
            var checkboxMap = new Dictionary<CheckBox, MetricType>
            {
                { MetricCheck_BatteryDrain, MetricType.BatteryDrain },
                { MetricCheck_BatteryLevel, MetricType.BatteryLevel },
                { MetricCheck_CPUUsage, MetricType.CPUUsage },
                { MetricCheck_CPUTemp, MetricType.CPUTemp },
                { MetricCheck_CPUWattage, MetricType.CPUWattage },
                { MetricCheck_GPUUsage, MetricType.GPUUsage },
                { MetricCheck_GPUTemp, MetricType.GPUTemp },
                { MetricCheck_GPUWattage, MetricType.GPUWattage },
                { MetricCheck_MemoryUsage, MetricType.MemoryUsage },
                { MetricCheck_TimeRemaining, MetricType.TimeRemaining }
            };

            if (!checkboxMap.TryGetValue(checkbox, out var metricType))
                return;

            bool isChecked = checkbox.IsChecked == true;

            if (isChecked)
            {
                // Trying to add - check if at max
                if (selectedMetrics.Count >= MaxSelectedMetrics)
                {
                    checkbox.IsChecked = false;
                    return;
                }
                if (!selectedMetrics.Contains(metricType))
                    selectedMetrics.Add(metricType);
            }
            else
            {
                selectedMetrics.Remove(metricType);
            }

            // Save selection
            SaveMetricsSelection();

            // Update count display
            UpdateMetricsSelectionCount();

            // Rebuild the metrics grid
            RebuildMetricsGrid();
        }

        /// <summary>
        /// Save metrics selection to local settings
        /// </summary>
        private void SaveMetricsSelection()
        {
            try
            {
                var settings = ApplicationData.Current.LocalSettings;
                var selectionStr = string.Join(",", selectedMetrics.Select(m => m.ToString()));
                settings.Values[QuickMetricsSelectionKey] = selectionStr;
                Logger.Info($"Saved metrics selection: {selectionStr}");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error saving metrics selection: {ex.Message}");
            }
        }

        /// <summary>
        /// Rebuild the metrics grid based on selected metrics
        /// </summary>
        private void RebuildMetricsGrid()
        {
            if (QuickMetricsGrid == null) return;

            QuickMetricsGrid.Children.Clear();
            QuickMetricsGrid.ColumnDefinitions.Clear();

            if (selectedMetrics.Count == 0)
            {
                QuickMetricsRow.Visibility = Visibility.Collapsed;
                return;
            }

            // Win11 theme (fork commits 77a24806 + e1cb6ef9): icon centered on top,
            // value below the icon, gray label below the value, with a thin
            // translucent divider between metrics. Classic: inline icon+value row
            // with the label underneath, no dividers.
            bool win11 = IsWin11Theme;

            // Restyle the containing rows for the active theme (these are static
            // XAML elements with hardcoded classic colors, untouched by the card
            // walker because their CornerRadius is 12).
            var rowBg = new SolidColorBrush(win11
                ? Windows.UI.Color.FromArgb(255, 0x35, 0x39, 0x3F)   // #35393F flat neutral
                : Windows.UI.Color.FromArgb(255, 0x1A, 0x1C, 0x1E)); // #1A1C1E classic
            var rowBorder = new SolidColorBrush(win11
                ? Windows.UI.Color.FromArgb(0x22, 255, 255, 255)     // #22FFFFFF hairline
                : Windows.UI.Color.FromArgb(255, 0x50, 0x55, 0x5C)); // #50555C classic
            QuickMetricsRow.Background = rowBg;
            QuickMetricsRow.BorderBrush = rowBorder;
            if (PanelBrightnessRow != null)
            {
                PanelBrightnessRow.Background = rowBg;
                PanelBrightnessRow.BorderBrush = rowBorder;
            }

            // Create columns: one star column per metric; in Win11 also a thin Auto
            // divider column between each pair of metrics.
            for (int c = 0; c < selectedMetrics.Count; c++)
            {
                if (win11 && c > 0) QuickMetricsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                QuickMetricsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }

            // Create UI for each selected metric
            int gridCol = 0;
            for (int i = 0; i < selectedMetrics.Count; i++)
            {
                var metricType = selectedMetrics[i];
                if (!metricDefinitions.TryGetValue(metricType, out var info))
                    continue;

                if (win11 && i > 0)
                {
                    // Thin translucent divider between metrics.
                    var divider = new Border
                    {
                        Width = 1,
                        Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
                        Margin = new Thickness(0, 2, 0, 2)
                    };
                    Grid.SetColumn(divider, gridCol);
                    QuickMetricsGrid.Children.Add(divider);
                    gridCol++;
                }

                // Create metric panel
                var panel = new StackPanel
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };

                if (win11)
                {
                    // Icon-over-value-over-label layout. Font sizes match the Win11
                    // Quick Settings tiles (icon 21 / value 12 / label 13-14).
                    var icon = new FontIcon
                    {
                        Glyph = info.Glyph,
                        FontSize = 21,
                        Foreground = new SolidColorBrush((Windows.UI.Color)Application.Current.Resources["SystemAccentColorLight2"]),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 0, 0, 3)
                    };

                    var valueText = new TextBlock
                    {
                        Text = "--",
                        FontSize = 12,
                        FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                        Foreground = new SolidColorBrush(Windows.UI.Colors.White),
                        HorizontalAlignment = HorizontalAlignment.Center
                    };
                    info.ValueTextBlock = valueText;

                    var labelText = new TextBlock
                    {
                        Text = info.Label,
                        FontSize = qsColumnCount == 4 ? 13 : 14,
                        Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 136, 136, 136)), // #888888
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 2, 0, 0)
                    };
                    info.LabelTextBlock = labelText;

                    panel.Children.Add(icon);
                    panel.Children.Add(valueText);
                    panel.Children.Add(labelText);
                }
                else
                {
                    // Value row (icon + value)
                    var valueRow = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Center
                    };

                    var icon = new FontIcon
                    {
                        Glyph = info.Glyph,
                        // Bumped one step for readability (paired inline with the value text below).
                        FontSize = 16,
                        Foreground = new SolidColorBrush((Windows.UI.Color)Application.Current.Resources["SystemAccentColorLight2"]),
                        Margin = new Thickness(0, 0, 4, 0),
                        VerticalAlignment = VerticalAlignment.Center
                    };

                    var valueText = new TextBlock
                    {
                        Text = "--",
                        FontSize = 16,
                        FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                        Foreground = new SolidColorBrush(Windows.UI.Colors.White),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    info.ValueTextBlock = valueText;

                    valueRow.Children.Add(icon);
                    valueRow.Children.Add(valueText);

                    // Label
                    var labelText = new TextBlock
                    {
                        Text = info.Label,
                        // Bumped one step for readability.
                        FontSize = 12,
                        Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 136, 136, 136)), // #888888
                        HorizontalAlignment = HorizontalAlignment.Center
                    };
                    info.LabelTextBlock = labelText;

                    panel.Children.Add(valueRow);
                    panel.Children.Add(labelText);
                }

                Grid.SetColumn(panel, gridCol);
                QuickMetricsGrid.Children.Add(panel);

                gridCol++;
            }

            // Show the row if we have metrics
            if (quickMetricsEnabled && selectedMetrics.Count > 0)
            {
                QuickMetricsRow.Visibility = Visibility.Visible;
            }

            // Also rebuild the reorder list
            RebuildMetricsReorderList();
        }

        /// <summary>
        /// Rebuild the metrics reorder list UI
        /// </summary>
        private void RebuildMetricsReorderList()
        {
            if (MetricsReorderList == null || MetricsReorderSection == null) return;

            MetricsReorderList.Children.Clear();

            // Hide reorder section if no metrics selected
            if (selectedMetrics.Count == 0)
            {
                MetricsReorderSection.Visibility = Visibility.Collapsed;
                return;
            }

            MetricsReorderSection.Visibility = Visibility.Visible;

            // Create a row for each selected metric
            for (int i = 0; i < selectedMetrics.Count; i++)
            {
                var metricType = selectedMetrics[i];
                if (!metricDefinitions.TryGetValue(metricType, out var info))
                    continue;

                var row = new Grid
                {
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(40, 255, 255, 255)),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(8, 4, 4, 4),
                    Margin = new Thickness(0, 0, 0, 0)
                };

                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) }); // Index
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // Name
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) }); // Up button
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) }); // Down button

                // Index number
                var indexText = new TextBlock
                {
                    Text = $"{i + 1}",
                    Foreground = new SolidColorBrush((Windows.UI.Color)Application.Current.Resources["SystemAccentColorLight2"]),
                    FontSize = 11,
                    FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(indexText, 0);
                row.Children.Add(indexText);

                // Metric name
                var nameText = new TextBlock
                {
                    Text = info.Label,
                    Foreground = new SolidColorBrush(Windows.UI.Colors.White),
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(nameText, 1);
                row.Children.Add(nameText);

                // Up button
                var upButton = new Button
                {
                    Content = new FontIcon { Glyph = "\uE70E", FontSize = 10 }, // ChevronUp
                    Background = new SolidColorBrush(Windows.UI.Colors.Transparent),
                    Padding = new Thickness(4),
                    MinWidth = 24,
                    MinHeight = 24,
                    IsEnabled = i > 0,
                    Opacity = i > 0 ? 1.0 : 0.3,
                    Tag = metricType
                };
                upButton.Click += MetricMoveUp_Click;
                Grid.SetColumn(upButton, 2);
                row.Children.Add(upButton);

                // Down button
                var downButton = new Button
                {
                    Content = new FontIcon { Glyph = "\uE70D", FontSize = 10 }, // ChevronDown
                    Background = new SolidColorBrush(Windows.UI.Colors.Transparent),
                    Padding = new Thickness(4),
                    MinWidth = 24,
                    MinHeight = 24,
                    IsEnabled = i < selectedMetrics.Count - 1,
                    Opacity = i < selectedMetrics.Count - 1 ? 1.0 : 0.3,
                    Tag = metricType
                };
                downButton.Click += MetricMoveDown_Click;
                Grid.SetColumn(downButton, 3);
                row.Children.Add(downButton);

                MetricsReorderList.Children.Add(row);
            }
        }

        /// <summary>
        /// Handle move up button click for metric reordering
        /// </summary>
        private void MetricMoveUp_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button button) || !(button.Tag is MetricType metricType))
                return;

            int index = selectedMetrics.IndexOf(metricType);
            if (index <= 0) return;

            // Swap with previous item
            selectedMetrics.RemoveAt(index);
            selectedMetrics.Insert(index - 1, metricType);

            // Save and rebuild
            SaveMetricsSelection();
            RebuildMetricsGrid();
        }

        /// <summary>
        /// Handle move down button click for metric reordering
        /// </summary>
        private void MetricMoveDown_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button button) || !(button.Tag is MetricType metricType))
                return;

            int index = selectedMetrics.IndexOf(metricType);
            if (index < 0 || index >= selectedMetrics.Count - 1) return;

            // Swap with next item
            selectedMetrics.RemoveAt(index);
            selectedMetrics.Insert(index + 1, metricType);

            // Save and rebuild
            SaveMetricsSelection();
            RebuildMetricsGrid();
        }

    }
}
