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
        /// Build sortable grid for tile customization
        /// </summary>
        private void BuildSortableGrid()
        {
            if (TileSortableGrid == null) return;

            TileSortableGrid.Children.Clear();

            // Get all tiles sorted by order (including hidden ones)
            var allTiles = qsTileDefinitions
                .Where(t => !ShouldSkipTile(t))
                .OrderBy(t => t.Order)
                .ToList();

            // Build rows of tiles (3 or 4 columns based on setting)
            Grid currentRow = null;
            int colIndex = 0;

            for (int i = 0; i < allTiles.Count; i++)
            {
                if (colIndex == 0)
                {
                    currentRow = new Grid { Margin = new Thickness(0, 4, 0, 4) };
                    // Add column definitions dynamically based on qsColumnCount
                    for (int c = 0; c < qsColumnCount; c++)
                    {
                        if (c > 0) currentRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });  // Spacer
                        currentRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    }
                    TileSortableGrid.Children.Add(currentRow);
                }

                var tile = allTiles[i];
                var miniTile = CreateMiniTileForSort(tile, i);
                Grid.SetColumn(miniTile, colIndex * 2);
                currentRow.Children.Add(miniTile);

                colIndex++;
                if (colIndex >= qsColumnCount)
                {
                    colIndex = 0;
                }
            }
        }

        /// <summary>
        /// Create a mini tile button for the sortable grid
        /// </summary>
        private Button CreateMiniTileForSort(TileDefinition tile, int index)
        {
            bool isSelected = qsSelectedTileForMove?.Id == tile.Id;

            var button = new Button
            {
                Tag = tile.Id,
                MinHeight = 60,
                Padding = new Thickness(4),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Background = isSelected
                    ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 120, 180))  // Highlight selected
                    : (tile.IsVisible
                        ? tileOffBrush
                        : new SolidColorBrush(Windows.UI.Color.FromArgb(128, 26, 28, 30))),  // Dimmed if hidden
                BorderBrush = isSelected
                    ? new SolidColorBrush(Windows.UI.Colors.White)
                    : new SolidColorBrush(Windows.UI.Color.FromArgb(80, 80, 85, 92)),
                BorderThickness = new Thickness(isSelected ? 2 : 1),
                CornerRadius = new CornerRadius(8),
                UseSystemFocusVisuals = true,
                FocusVisualPrimaryBrush = new SolidColorBrush(Windows.UI.Colors.White),
                FocusVisualSecondaryBrush = new SolidColorBrush(Windows.UI.Colors.Transparent),
                TabIndex = index
            };

            var content = new Grid();

            // Icon and name stack
            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            stack.Children.Add(new FontIcon
            {
                Glyph = tile.Glyph,
                FontSize = 18,
                Foreground = new SolidColorBrush(tile.IsVisible ? Windows.UI.Colors.White : Windows.UI.Colors.Gray)
            });
            stack.Children.Add(new TextBlock
            {
                Text = tile.Name,
                FontSize = 10,
                Foreground = new SolidColorBrush(tile.IsVisible ? Windows.UI.Colors.White : Windows.UI.Colors.Gray),
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            content.Children.Add(stack);

            // Eye icon (top-right) - shows visibility status
            var eyeIcon = new FontIcon
            {
                Glyph = tile.IsVisible ? "\uE7B3" : "\uED1A",  // Eye / Eye crossed
                FontSize = 12,
                Foreground = new SolidColorBrush(tile.IsVisible
                    ? Windows.UI.Color.FromArgb(255, 100, 200, 100)   // Green for visible
                    : Windows.UI.Color.FromArgb(255, 200, 100, 100)), // Red for hidden
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 2, 2, 0)
            };
            content.Children.Add(eyeIcon);

            // Order number badge (bottom-left)
            var orderText = new TextBlock
            {
                Text = (index + 1).ToString(),
                FontSize = 10,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(128, 255, 255, 255)),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(4, 0, 0, 2)
            };
            content.Children.Add(orderText);

            // Custom shortcut indicator (bottom-right) - shows it can be deleted
            if (!string.IsNullOrEmpty(tile.CustomShortcut))
            {
                var customIcon = new FontIcon
                {
                    Glyph = "\uE932",  // Pin icon to indicate custom
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(180, 255, 200, 100)),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(0, 0, 4, 2)
                };
                content.Children.Add(customIcon);
            }

            button.Content = content;
            button.Click += SortableTile_Click;

            return button;
        }

        /// <summary>
        /// Handle tap on sortable tile - select, swap, or toggle visibility
        /// </summary>
        private void SortableTile_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!(sender is Button button) || !(button.Tag is string tileId))
                    return;

                if (!qsTileMap.TryGetValue(tileId, out var clickedTile))
                    return;

                if (qsSelectedTileForMove == null)
                {
                    // First tap: select tile - just update visuals, don't rebuild
                    qsSelectedTileForMove = clickedTile;
                    UpdateSelectedTileIndicator(clickedTile);
                    UpdateSortableGridVisuals(tileId);
                }
                else if (qsSelectedTileForMove.Id == clickedTile.Id)
                {
                    // Tap same tile: toggle visibility - need rebuild for eye icon change
                    clickedTile.IsVisible = !clickedTile.IsVisible;
                    qsSelectedTileForMove = null;
                    UpdateSelectedTileIndicator(null);
                    BuildSortableGridPreserveScroll(tileId);
                    Logger.Info($"Toggled visibility for {clickedTile.Name}: {clickedTile.IsVisible}");
                }
                else
                {
                    // Tap different tile: swap Order values - need rebuild for reorder
                    int tempOrder = qsSelectedTileForMove.Order;
                    qsSelectedTileForMove.Order = clickedTile.Order;
                    clickedTile.Order = tempOrder;

                    Logger.Info($"Swapped tile order: {qsSelectedTileForMove.Name} <-> {clickedTile.Name}");

                    qsSelectedTileForMove = null;
                    UpdateSelectedTileIndicator(null);
                    BuildSortableGridPreserveScroll(tileId);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error handling sortable tile click: {ex.Message}");
            }
        }

        /// <summary>
        /// Build sortable grid while preserving scroll position and focus
        /// </summary>
        private void BuildSortableGridPreserveScroll(string focusTileId = null)
        {
            // Save scroll position
            double scrollOffset = 0;
            if (QuickSettingsScrollViewer != null)
            {
                scrollOffset = QuickSettingsScrollViewer.VerticalOffset;
            }

            BuildSortableGrid();

            // Restore scroll position and focus after layout update
            _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Low, () =>
            {
                if (QuickSettingsScrollViewer != null && scrollOffset > 0)
                {
                    QuickSettingsScrollViewer.ChangeView(null, scrollOffset, null, true);
                }

                // Restore focus to the specified tile
                if (!string.IsNullOrEmpty(focusTileId) && TileSortableGrid != null)
                {
                    foreach (var child in TileSortableGrid.Children)
                    {
                        if (child is Grid row)
                        {
                            foreach (var cell in row.Children)
                            {
                                if (cell is Button btn && btn.Tag is string id && id == focusTileId)
                                {
                                    btn.Focus(FocusState.Programmatic);
                                    return;
                                }
                            }
                        }
                    }
                }
            });
        }

        /// <summary>
        /// Update visual state of sortable tiles without rebuilding (for selection changes)
        /// </summary>
        private void UpdateSortableGridVisuals(string focusTileId = null)
        {
            if (TileSortableGrid == null) return;

            foreach (var child in TileSortableGrid.Children)
            {
                if (child is Grid row)
                {
                    foreach (var cell in row.Children)
                    {
                        if (cell is Button btn && btn.Tag is string id && qsTileMap.TryGetValue(id, out var tile))
                        {
                            bool isSelected = qsSelectedTileForMove?.Id == id;

                            // Update button background and border
                            btn.Background = isSelected
                                ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 120, 180))
                                : (tile.IsVisible
                                    ? tileOffBrush
                                    : new SolidColorBrush(Windows.UI.Color.FromArgb(128, 26, 28, 30)));
                            btn.BorderBrush = isSelected
                                ? new SolidColorBrush(Windows.UI.Colors.White)
                                : new SolidColorBrush(Windows.UI.Color.FromArgb(80, 80, 85, 92));
                            btn.BorderThickness = new Thickness(isSelected ? 2 : 1);

                            // Focus the specified tile
                            if (!string.IsNullOrEmpty(focusTileId) && id == focusTileId)
                            {
                                btn.Focus(FocusState.Programmatic);
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Update the selected tile indicator text
        /// </summary>
        private void UpdateSelectedTileIndicator(TileDefinition tile)
        {
            if (SelectedTileIndicator == null || SelectedTileText == null)
                return;

            if (tile == null)
            {
                SelectedTileIndicator.Visibility = Visibility.Collapsed;
                if (DeleteSelectedTileButton != null)
                    DeleteSelectedTileButton.Visibility = Visibility.Collapsed;
            }
            else
            {
                SelectedTileIndicator.Visibility = Visibility.Visible;
                SelectedTileText.Text = $"Selected: {tile.Name}\nTap another tile to swap, or tap again to toggle visibility";

                // Show delete button for custom shortcuts (identified by having a CustomShortcut value)
                if (DeleteSelectedTileButton != null)
                {
                    DeleteSelectedTileButton.Visibility = !string.IsNullOrEmpty(tile.CustomShortcut)
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                }
            }
        }

        /// <summary>
        /// Handle delete button click in the selected tile indicator
        /// </summary>
        private void DeleteSelectedTile_Click(object sender, RoutedEventArgs e)
        {
            if (qsSelectedTileForMove != null && !string.IsNullOrEmpty(qsSelectedTileForMove.CustomShortcut))
            {
                DeleteCustomShortcutTile(qsSelectedTileForMove);
            }
        }

        /// <summary>
        /// Delete a custom shortcut tile
        /// </summary>
        private void DeleteCustomShortcutTile(TileDefinition tile)
        {
            try
            {
                // Remove from QuickSettingsConfig persistent storage first
                // Need to find the matching config tile by custom shortcut path
                var config = QuickSettings.QuickSettingsConfig.Instance;
                var configTile = config.Tiles.FirstOrDefault(t =>
                    t.Type == QuickSettings.TileType.CustomShortcut &&
                    t.CustomShortcut == tile.CustomShortcut);
                if (configTile != null)
                {
                    config.RemoveTile(configTile.Id);
                }

                // Remove from local lists
                qsTileDefinitions.Remove(tile);
                qsTileMap.Remove(tile.Id);
                qsCustomShortcuts.Remove(tile);

                // Clear selection if we deleted the selected tile
                if (qsSelectedTileForMove?.Id == tile.Id)
                {
                    qsSelectedTileForMove = null;
                    UpdateSelectedTileIndicator(null);
                }

                BuildSortableGridPreserveScroll();
                // Don't rebuild main tiles here - they'll update when panel closes

                Logger.Info($"Deleted custom shortcut tile: {tile.Name}");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error deleting custom shortcut tile: {ex.Message}");
            }
        }

        /// <summary>
        /// Check if a tile should be skipped based on hardware detection
        /// </summary>
        private bool ShouldSkipTile(TileDefinition tile)
        {
            // Skip Legion tiles if not detected
            if ((tile.Id == "LegionTouchpad" || tile.Id == "LegionLightMode" ||
                 tile.Id == "LegionDesktopControls" || tile.Id == "LegionRemapControls" ||
                 tile.Id == "LegionChargeLimit" || tile.Id == "LegionPowerLight" ||
                 tile.Id == "Touchscreen") &&
                (legionGoDetected?.Value != true))
            {
                return true;
            }

            // TDP Mode tile is now available for all devices (Legion uses hardware presets, generic uses TDP values)

            // Skip Lossless Scaling tile if not installed
            if (tile.Id == "LosslessScaling" && (losslessScalingInstalled?.Value != true))
            {
                return true;
            }

            // Skip Controller Emulation tile if helper has reported the backend as unavailable
            // (handheld-agnostic emulation requires LegionGo / GPD / similar, gated by the helper).
            if (tile.Id == "ControllerEmulation" && (controllerEmulationAvailable?.Value != true))
            {
                return true;
            }

            return false;
        }

    }
}
