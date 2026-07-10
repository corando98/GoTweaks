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
        // Tile brushes
        private SolidColorBrush tileOffBrush;
        private SolidColorBrush tileOnBrush;
        private SolidColorBrush tileActiveBrush;
        private SolidColorBrush tileTriggerBrush;
        private LinearGradientBrush tileDefaultProfileBrush;
        private bool quickSettingsInitialized = false;

        // Tile definitions with visibility tracking
        private class TileDefinition
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string Glyph { get; set; }
            public bool IsVisible { get; set; } = true;
            public bool IsTrigger { get; set; } = false;  // True for tiles that trigger actions (keyboard, custom shortcuts)
            public bool IsAction { get; set; } = false;   // True for action tiles (Task Manager, Explorer, etc.) - shown at bottom
            public string CustomShortcut { get; set; }    // For custom shortcut tiles
            public int Order { get; set; } = 0;           // Display order (lower = first)
            public Button TileButton { get; set; }
            public TextBlock StateText { get; set; }
            public CheckBox VisibilityCheckBox { get; set; }

            // For scrolling text animation (Profile tile)
            public Canvas StateTextCanvas { get; set; }
            public TranslateTransform StateTextTransform { get; set; }
            public Storyboard ScrollStoryboard { get; set; }
        }

        // List of custom shortcut tiles
        private List<TileDefinition> qsCustomShortcuts = new List<TileDefinition>();

        private List<TileDefinition> qsTileDefinitions = new List<TileDefinition>();
        private Dictionary<string, TileDefinition> qsTileMap = new Dictionary<string, TileDefinition>();

        // Edit mode state for tile customization
        private bool qsEditMode = false;
        private TileDefinition qsSelectedTileForMove = null;

        // Column count setting (3 or 4 columns)
        private int qsColumnCount = 4;

        // Quick Metrics row state
        private bool quickMetricsEnabled = false;
        private bool isUpdatingMetricCheckboxes = false;
        private bool screenSaverEnabled = false;
        private const string ScreenSaverEnabledKey = "QS_ScreenSaverEnabled";
        private const int ScreenSaverTimeoutSeconds = 60;
        private DispatcherTimer screenSaverCountdownTimer;
        private const string QuickMetricsEnabledKey = "QS_MetricsEnabled";
        private const string QuickMetricsSelectionKey = "QS_MetricsSelection";
        private const int MaxSelectedMetrics = 6;

        // Available metric types with their display properties
        private enum MetricType
        {
            BatteryDrain,
            BatteryLevel,
            CPUUsage,
            CPUTemp,
            CPUWattage,
            GPUUsage,
            GPUTemp,
            GPUWattage,
            MemoryUsage,
            TimeRemaining
        }

        // Metric display info
        private class MetricInfo
        {
            public string Id { get; set; }
            public string Label { get; set; }
            public string Glyph { get; set; }
            public string Unit { get; set; }
            public TextBlock ValueTextBlock { get; set; }
            public TextBlock LabelTextBlock { get; set; }
        }

        // Map of metric type to display info
        private readonly Dictionary<MetricType, MetricInfo> metricDefinitions = new Dictionary<MetricType, MetricInfo>
        {
            { MetricType.BatteryDrain, new MetricInfo { Id = "BatteryDrain", Label = "Battery", Glyph = "\uE83F", Unit = "W" } },
            { MetricType.BatteryLevel, new MetricInfo { Id = "BatteryLevel", Label = "Battery", Glyph = "\uE83F", Unit = "%" } },
            { MetricType.CPUUsage, new MetricInfo { Id = "CPUUsage", Label = "CPU", Glyph = "\uE950", Unit = "%" } },
            { MetricType.CPUTemp, new MetricInfo { Id = "CPUTemp", Label = "CPU Temp", Glyph = "\uE9CA", Unit = "°" } },
            { MetricType.CPUWattage, new MetricInfo { Id = "CPUWattage", Label = "CPU", Glyph = "\uE945", Unit = "W" } },
            { MetricType.GPUUsage, new MetricInfo { Id = "GPUUsage", Label = "GPU", Glyph = "\uE7F4", Unit = "%" } },
            { MetricType.GPUTemp, new MetricInfo { Id = "GPUTemp", Label = "GPU Temp", Glyph = "\uE9CA", Unit = "°" } },
            { MetricType.GPUWattage, new MetricInfo { Id = "GPUWattage", Label = "GPU", Glyph = "\uE945", Unit = "W" } },
            { MetricType.MemoryUsage, new MetricInfo { Id = "MemoryUsage", Label = "Memory", Glyph = "\uE964", Unit = "%" } },
            { MetricType.TimeRemaining, new MetricInfo { Id = "TimeRemaining", Label = "Time", Glyph = "\uE916", Unit = "" } }
        };

        // Currently selected metrics (in order of display)
        private List<MetricType> selectedMetrics = new List<MetricType>();

        // Current metrics data from helper
        private Dictionary<string, double> currentMetricsData = new Dictionary<string, double>();
        private bool currentMetricsIsCharging = false;

        // Timer for TDP reapply when switching to Custom mode
        private Windows.UI.Xaml.DispatcherTimer qsTdpReapplyTimer;

        /// <summary>
        /// Initialize Quick Settings resources and build tiles
        /// </summary>
        private void InitializeQuickSettings()
        {
            if (quickSettingsInitialized) return;

            try
            {
                // Clear any stale state from previous initialization attempts
                // This ensures fresh state when widget is reloaded
                qsTileDefinitions.Clear();
                qsTileMap.Clear();
                qsCustomShortcuts.Clear();
                qsEditMode = false;
                qsSelectedTileForMove = null;

                // Dark mode colors with sharp contrast for handheld devices
                // On state: use desaturated system accent color for subtle indication
                var accentDark3 = (Windows.UI.Color)Application.Current.Resources["SystemAccentColorDark3"];
                // Blend accent with dark gray to reduce saturation (40% accent, 60% dark base)
                var darkBase = Windows.UI.Color.FromArgb(255, 26, 28, 30); // Same as tile off
                var desaturatedAccent = Windows.UI.Color.FromArgb(
                    255,
                    (byte)((accentDark3.R * 0.4) + (darkBase.R * 0.6)),
                    (byte)((accentDark3.G * 0.4) + (darkBase.G * 0.6)),
                    (byte)((accentDark3.B * 0.4) + (darkBase.B * 0.6)));
                tileOnBrush = new SolidColorBrush(desaturatedAccent);

                // Other tile brushes - dark mode
                tileOffBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 26, 28, 30));   // #1A1C1E
                tileActiveBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 26, 37, 48)); // #1A2530 - dark blue
                tileTriggerBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 37, 32, 48)); // #252030 - dark purple

                // Default Game Profile gradient brush (matches Performance tab card)
                tileDefaultProfileBrush = new LinearGradientBrush
                {
                    StartPoint = new Windows.Foundation.Point(0, 0),
                    EndPoint = new Windows.Foundation.Point(1, 1),
                    GradientStops = new GradientStopCollection
                    {
                        new GradientStop { Color = Windows.UI.Color.FromArgb(0x40, 0xC0, 0x40, 0x40), Offset = 0.0 },
                        new GradientStop { Color = Windows.UI.Color.FromArgb(0x40, 0x40, 0x80, 0x50), Offset = 0.35 },
                        new GradientStop { Color = Windows.UI.Color.FromArgb(0x40, 0x40, 0x50, 0x80), Offset = 0.65 },
                        new GradientStop { Color = Windows.UI.Color.FromArgb(0x40, 0x80, 0x40, 0x60), Offset = 1.0 }
                    }
                };

                // Define all tiles
                DefineQuickSettingsTiles();

                // Load visibility settings from storage
                LoadQuickSettingsConfig();

                // Build tile UI
                RebuildQuickSettingsTiles();

                // Build sortable grid (for customize panel, initially hidden)
                BuildSortableGrid();

                quickSettingsInitialized = true;
                Logger.Info("Quick Settings initialized with system accent color");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error initializing Quick Settings: {ex.Message}");
            }
        }

        /// <summary>
        /// Refresh Quick Settings tiles when Legion status changes
        /// </summary>
        private void RefreshQuickSettingsForLegion()
        {
            if (!quickSettingsInitialized) return;

            try
            {
                RebuildQuickSettingsTiles();
                BuildSortableGrid();
                UpdateQuickSettingsTileStates();
                Logger.Info("Quick Settings refreshed for Legion detection change");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error refreshing Quick Settings for Legion: {ex.Message}");
            }
        }

        /// <summary>
        /// Define all available Quick Settings tiles
        /// </summary>
        private void DefineQuickSettingsTiles()
        {
            qsTileDefinitions.Clear();
            qsTileMap.Clear();

            int order = 0;

            // Row 1 - Performance Core (most used)
            AddTileDefinition("TDPMode", "TDP Mode", "\uE945", order: order++);
            AddTileDefinition("AutoTDP", "AutoTDP", "\uE9F5", order: order++);
            AddTileDefinition("PowerMode", "Power Mode", "\uE945", order: order++);
            AddTileDefinition("CPUBoost", "CPU Boost", "\uE7F4", order: order++);

            // Row 2 - Performance Fine-tuning
            AddTileDefinition("EPP", "EPP", "\uE83E", order: order++);
            AddTileDefinition("FPSLimit", "FPS Limit", "\uE916", order: order++);
            AddTileDefinition("RadeonChill", "Chill", "\uE9CA", order: order++);
            AddTileDefinition("Profile", "Profile", "\uE77B", order: order++);

            // Row 3 - Display
            AddTileDefinition("Resolution", "Resolution", "\uE7F8", order: order++);
            AddTileDefinition("Rotation", "Rotation", "\uE7AD", order: order++);
            AddTileDefinition("HDR", "HDR", "\uE706", order: order++);
            AddTileDefinition("Fullscreen", "Fullscreen", "\uE740", order: order++);

            // Row 4 - AMD Graphics Features
            AddTileDefinition("RSR", "RSR", "\uE8B3", order: order++);
            AddTileDefinition("RIS", "RIS", "\uE8B3", order: order++);
            AddTileDefinition("AFMF", "AFMF", "\uE916", order: order++);
            AddTileDefinition("AntiLag", "Anti-Lag", "\uE916", order: order++);

            // Row 5 - Scaling/Quality
            AddTileDefinition("LosslessScaling", "Lossless", "\uE740", order: order++);
            AddTileDefinition("Overlay", "Overlay", "\uE7B3", order: order++);

            // Row 6 - Input & Interaction
            AddTileDefinition("ScreenSaver", "Idle Screen Off", "\uE7E8", order: order++);
            AddTileDefinition("Keyboard", "Keyboard", "\uE765", isTrigger: true, order: order++);
            AddTileDefinition("LegionTouchpad", "Touchpad", "\uE962", order: order++);
            AddTileDefinition("LegionRemapControls", "Remap", "\uE7FC", order: order++);
            AddTileDefinition("LegionDesktopControls", "Desktop", "\uE7F4", order: order++);
            // Quick toggle for the legacy controller emulation backend; state text shows
            // the active backend mode (Xbox / DS4 / Mouse) when on, "Off" otherwise.
            AddTileDefinition("ControllerEmulation", "Controller", "\uE7FC", order: order++);

            // Row 7 - System/Device
            AddTileDefinition("LegionLightMode", "Light Mode", "\uE781", order: order++);
            AddTileDefinition("LegionPowerLight", "Power Light", "\uE7E8", order: order++);
            AddTileDefinition("LegionChargeLimit", "Charge Limit", "\uE83F", order: order++);
            AddTileDefinition("LegionFanFullSpeed", "Fan Max", "\uE9CA", order: order++);
            AddTileDefinition("Battery", "Battery", "\uE83F", order: order++);

            // Load custom shortcut tiles from storage
            LoadCustomShortcutTiles();

            // Row 8 - Quick Actions (high order numbers to keep at bottom)
            int actionOrder = 1000;
            AddTileDefinition("ActionTaskManager", "Task Mgr", "\uE7EF", isAction: true, order: actionOrder++);
            AddTileDefinition("ActionExplorer", "Explorer", "\uEC50", isAction: true, order: actionOrder++);
            AddTileDefinition("ActionEndTask", "End Task", "\uE711", isAction: true, order: actionOrder++);
            AddTileDefinition("ActionHibernate", "Hibernate", "\uE708", isAction: true, order: actionOrder++);
        }

        private void AddTileDefinition(string id, string name, string glyph, bool isTrigger = false, bool isAction = false, string customShortcut = null, int order = 0)
        {
            var def = new TileDefinition { Id = id, Name = name, Glyph = glyph, IsVisible = true, IsTrigger = isTrigger, IsAction = isAction, CustomShortcut = customShortcut, Order = order };
            qsTileDefinitions.Add(def);
            qsTileMap[id] = def;
        }

        /// <summary>
        /// Load custom shortcut tiles from storage using QuickSettingsConfig
        /// </summary>
        private void LoadCustomShortcutTiles()
        {
            try
            {
                // Load from QuickSettingsConfig (the new unified storage)
                var config = QuickSettings.QuickSettingsConfig.Instance;
                var customTiles = config.Tiles.Where(t => t.Type == QuickSettings.TileType.CustomShortcut).ToList();

                // Calculate starting order (after built-in tiles)
                int startingOrder = qsTileDefinitions.Count > 0 ? qsTileDefinitions.Max(t => t.Order) + 1 : 100;

                int index = 0;
                foreach (var tile in customTiles)
                {
                    if (!string.IsNullOrEmpty(tile.CustomShortcut))
                    {
                        // Use the stable GUID from QuickSettingsConfig instead of index-based ID
                        // This prevents tile ID mismatch when widget is reloaded
                        string tileId = tile.Id;
                        var def = new TileDefinition
                        {
                            Id = tileId,
                            Name = tile.Name,
                            Glyph = tile.Icon ?? "\uE768",
                            IsVisible = tile.IsVisible,
                            IsTrigger = true,
                            CustomShortcut = tile.CustomShortcut,
                            Order = startingOrder + index  // Order will be overridden by LoadQuickSettingsConfig if saved
                        };
                        qsTileDefinitions.Add(def);
                        qsTileMap[tileId] = def;
                        qsCustomShortcuts.Add(def);
                        index++;
                    }
                }
                Logger.Info($"Loaded {index} custom shortcut tiles from QuickSettingsConfig (using stable GUIDs)");

                // Migration: If old storage has shortcuts that aren't in the new system, migrate them
                MigrateOldCustomShortcuts();
            }
            catch (Exception ex)
            {
                Logger.Error($"Error loading custom shortcut tiles: {ex.Message}");
            }
        }

        /// <summary>
        /// Migrate old custom shortcuts from the legacy storage format to QuickSettingsConfig
        /// </summary>
        private void MigrateOldCustomShortcuts()
        {
            try
            {
                var settings = ApplicationData.Current.LocalSettings;
                if (settings.Values.TryGetValue("QS_CustomShortcuts", out object val) && val is string data && !string.IsNullOrEmpty(data))
                {
                    var config = QuickSettings.QuickSettingsConfig.Instance;
                    var existingShortcuts = config.Tiles
                        .Where(t => t.Type == QuickSettings.TileType.CustomShortcut)
                        .Select(t => t.CustomShortcut)
                        .ToHashSet();

                    var shortcuts = data.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                    int migratedCount = 0;

                    foreach (var shortcut in shortcuts)
                    {
                        var parts = shortcut.Split('|');
                        if (parts.Length == 2 && !existingShortcuts.Contains(parts[1]))
                        {
                            // Add to QuickSettingsConfig if not already present
                            config.AddCustomTile(parts[0], "\uE768", parts[1]);
                            migratedCount++;
                        }
                    }

                    if (migratedCount > 0)
                    {
                        Logger.Info($"Migrated {migratedCount} custom shortcuts from legacy storage");
                        // Clear old storage after migration
                        settings.Values.Remove("QS_CustomShortcuts");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error migrating old custom shortcuts: {ex.Message}");
            }
        }

        /// <summary>
        /// Save custom shortcut tiles to QuickSettingsConfig
        /// Note: This is now handled automatically by QuickSettingsConfig.AddCustomTile
        /// This method is kept for compatibility but delegates to QuickSettingsConfig
        /// </summary>
        private void SaveCustomShortcutTiles()
        {
            try
            {
                // QuickSettingsConfig.Save() is called automatically by AddCustomTile
                // This method now just triggers a save to ensure consistency
                QuickSettings.QuickSettingsConfig.Instance.Save();
                Logger.Info($"Custom shortcut tiles saved to QuickSettingsConfig");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error saving custom shortcut tiles: {ex.Message}");
            }
        }

        /// <summary>
        /// Add a new custom shortcut tile using QuickSettingsConfig
        /// </summary>
        private void AddCustomShortcutTile(string name, string shortcut)
        {
            try
            {
                // Add to QuickSettingsConfig (saves automatically) - returns tile with GUID
                var config = QuickSettings.QuickSettingsConfig.Instance;
                var configTile = config.AddCustomTile(name, "\uE768", shortcut);

                // Calculate new order (place at end)
                int maxOrder = qsTileDefinitions.Count > 0 ? qsTileDefinitions.Max(t => t.Order) : 0;

                // Use the GUID from QuickSettingsConfig for stable tile identification
                string tileId = configTile.Id;
                var def = new TileDefinition
                {
                    Id = tileId,
                    Name = name,
                    Glyph = "\uE768",
                    IsVisible = true,
                    IsTrigger = true,
                    CustomShortcut = shortcut,
                    Order = maxOrder + 1
                };
                qsTileDefinitions.Add(def);
                qsTileMap[tileId] = def;
                qsCustomShortcuts.Add(def);

                RebuildQuickSettingsTiles();
                BuildSortableGrid();

                Logger.Info($"Added custom shortcut tile: {name} -> {shortcut} (id: {tileId})");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error adding custom shortcut tile: {ex.Message}");
            }
        }

        /// <summary>
        /// Load Quick Settings configuration from storage
        /// </summary>
        private void LoadQuickSettingsConfig()
        {
            try
            {
                var settings = ApplicationData.Current.LocalSettings;

                // Load column count setting
                if (settings.Values.TryGetValue("QS_ColumnCount", out object colVal) && colVal is int colCount)
                {
                    qsColumnCount = Math.Max(3, Math.Min(5, colCount));  // Clamp to 3-5
                }

                // Load Quick Metrics toggle state
                if (settings.Values.TryGetValue(QuickMetricsEnabledKey, out object metricsVal) && metricsVal is bool metricsEnabled)
                {
                    quickMetricsEnabled = metricsEnabled;
                }

                // Load Screen Saver toggle state
                if (settings.Values.TryGetValue(ScreenSaverEnabledKey, out object ssVal) && ssVal is bool ssEnabled)
                {
                    screenSaverEnabled = ssEnabled;
                    if (screenSaverEnabled)
                    {
                        StartScreenSaverCountdown();
                    }
                }

                // Load Quick Metrics selection
                selectedMetrics.Clear();
                if (settings.Values.TryGetValue(QuickMetricsSelectionKey, out object selectionVal) && selectionVal is string selectionStr)
                {
                    // Parse comma-separated metric IDs
                    foreach (var id in selectionStr.Split(','))
                    {
                        if (Enum.TryParse<MetricType>(id.Trim(), out var metricType))
                        {
                            selectedMetrics.Add(metricType);
                        }
                    }
                }
                else
                {
                    // Default selection: Battery Drain, CPU Usage, CPU Temp, GPU Usage, Time Remaining
                    selectedMetrics.AddRange(new[] { MetricType.BatteryDrain, MetricType.CPUUsage, MetricType.CPUTemp, MetricType.GPUUsage, MetricType.TimeRemaining });
                }

                // Update Quick Metrics UI
                if (QuickMetricsToggle != null)
                    QuickMetricsToggle.IsOn = quickMetricsEnabled;
                if (QuickMetricsRow != null)
                    QuickMetricsRow.Visibility = quickMetricsEnabled ? Visibility.Visible : Visibility.Collapsed;
                if (MetricsSelectionPanel != null)
                    MetricsSelectionPanel.Visibility = quickMetricsEnabled ? Visibility.Visible : Visibility.Collapsed;

                // Update checkboxes and rebuild metrics grid
                UpdateMetricCheckboxes();
                RebuildMetricsGrid();

                foreach (var tile in qsTileDefinitions)
                {
                    string visKey = $"QS_{tile.Id}_Visible";
                    string orderKey = $"QS_{tile.Id}_Order";

                    if (settings.Values.TryGetValue(visKey, out object val) && val is bool visible)
                    {
                        tile.IsVisible = visible;
                    }
                    if (settings.Values.TryGetValue(orderKey, out object orderVal) && orderVal is int order)
                    {
                        tile.Order = order;
                    }
                }

                Logger.Info($"Quick Settings config loaded (columns: {qsColumnCount})");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error loading Quick Settings config: {ex.Message}");
            }
        }

        /// <summary>
        /// Save Quick Settings configuration to storage
        /// </summary>
        private void SaveQuickSettingsConfig()
        {
            try
            {
                var settings = ApplicationData.Current.LocalSettings;

                // Save column count setting
                settings.Values["QS_ColumnCount"] = qsColumnCount;

                foreach (var tile in qsTileDefinitions)
                {
                    settings.Values[$"QS_{tile.Id}_Visible"] = tile.IsVisible;
                    settings.Values[$"QS_{tile.Id}_Order"] = tile.Order;
                }

                Logger.Info($"Quick Settings config saved (columns: {qsColumnCount})");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error saving Quick Settings config: {ex.Message}");
            }
        }

        private void CalibrateGyroButton_Click(object sender, Windows.UI.Xaml.RoutedEventArgs e)
        {
            try
            {
                if (!App.IsConnected) return;

                var request = new Windows.Foundation.Collections.ValueSet
                {
                    { "Command", (int)Shared.Enums.Command.Set },
                    { "Function", (int)Shared.Enums.Function.ControllerEmulationCalibrateGyro },
                    { "Content", true }
                };
                App.PipeClient?.SendValueSet(request);
                Logger.Info("Sent gyro calibration request to helper");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error sending gyro calibration: {ex.Message}");
            }
        }

        /// <summary>
        /// Send Quick Metrics enabled state to helper
        /// </summary>
        private void SendQuickMetricsEnabledToHelper()
        {
            try
            {
                if (!App.IsConnected) return;

                var request = new Windows.Foundation.Collections.ValueSet
                {
                    { "Command", (int)Shared.Enums.Command.Set },
                    { "Function", (int)Shared.Enums.Function.QuickMetricsEnabled },
                    { "Content", quickMetricsEnabled }
                };
                // Fire-and-forget: helper processes this but doesn't send a response,
                // so using SendRequestAsync would timeout after 10s for no reason
                App.PipeClient?.SendValueSet(request);
                Logger.Info($"Sent Quick Metrics enabled state to helper: {quickMetricsEnabled}");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error sending Quick Metrics enabled state: {ex.Message}");
            }
        }

        private void SendScreenSaverEnabledToHelper()
        {
            try
            {
                if (!App.IsConnected) return;

                var request = new Windows.Foundation.Collections.ValueSet
                {
                    { "Command", (int)Shared.Enums.Command.Set },
                    { "Function", (int)Shared.Enums.Function.ScreenSaverEnabled },
                    { "Content", screenSaverEnabled }
                };
                // Fire-and-forget: helper processes this but doesn't send a response,
                // so using SendRequestAsync would timeout after 10s for no reason
                App.PipeClient?.SendValueSet(request);
                Logger.Info($"Sent Screen Saver enabled state to helper: {screenSaverEnabled}");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error sending Screen Saver enabled state: {ex.Message}");
            }
        }

        /// <summary>
        /// Send current controller hotkey config to helper via pipe so it can update
        /// its cached config for XInput-based button combo detection.
        /// </summary>
        private void SendControllerHotkeyConfigToHelper()
        {
            try
            {
                if (!App.IsConnected) return;

                var settings = ApplicationData.Current.LocalSettings;
                var hotkeyNames = new[] { "MenuA", "MenuB", "MenuX", "MenuY", "MenuDpadUp", "MenuDpadDown", "MenuDpadLeft", "MenuDpadRight" };

                // Build JSON config matching what ApplyControllerHotkeyConfig expects
                var jsonObj = new Windows.Data.Json.JsonObject();
                foreach (var name in hotkeyNames)
                {
                    int action = (int)(settings.Values[$"Hotkey_{name}_Action"] ?? 0);
                    string key = settings.Values[$"Hotkey_{name}_Key"] as string ?? "";
                    jsonObj[$"{name}_Action"] = Windows.Data.Json.JsonValue.CreateNumberValue(action);
                    jsonObj[$"{name}_Key"] = Windows.Data.Json.JsonValue.CreateStringValue(key);
                }

                string configJson = jsonObj.Stringify();

                var request = new Windows.Foundation.Collections.ValueSet
                {
                    { "Command", (int)Shared.Enums.Command.Set },
                    { "Function", (int)Shared.Enums.Function.ControllerHotkeyConfig },
                    { "Content", configJson }
                };
                App.PipeClient?.SendValueSet(request);
                Logger.Info($"Sent controller hotkey config to helper");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error sending controller hotkey config: {ex.Message}");
            }
        }
    }
}
