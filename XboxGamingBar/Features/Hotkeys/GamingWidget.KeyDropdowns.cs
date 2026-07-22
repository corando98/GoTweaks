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
        // Storage for selected keys (used for hotkeys, Legion buttons, scroll wheel, custom shortcuts)
        private Dictionary<string, List<int>> _selectedKeys = new Dictionary<string, List<int>>();
        private List<int> _customShortcutKeys = new List<int>();

        private List<int> GetSelectedKeys(string keyName)
        {
            if (!_selectedKeys.ContainsKey(keyName))
                _selectedKeys[keyName] = new List<int>();
            return _selectedKeys[keyName];
        }

        private void AddKeyToSelection(string keyName, int keyCode, ItemsControl keyTags, ComboBox keyComboBox, Action onKeysChanged = null)
        {
            var keys = GetSelectedKeys(keyName);
            if (keys.Count >= 5) return; // Max 5 keys
            if (!keys.Contains(keyCode) && keyCode > 0)
            {
                keys.Add(keyCode);
                UpdateKeyTagsDisplay(keyName, keyTags, onKeysChanged);
                onKeysChanged?.Invoke();
            }
            if (keyComboBox != null)
                keyComboBox.SelectedIndex = 0;
        }

        private void RemoveKeyFromSelection(string keyName, int keyCode, ItemsControl keyTags, Action onKeysChanged = null)
        {
            var keys = GetSelectedKeys(keyName);
            keys.Remove(keyCode);
            UpdateKeyTagsDisplay(keyName, keyTags, onKeysChanged);
            onKeysChanged?.Invoke();
        }

        private void UpdateKeyTagsDisplay(string keyName, ItemsControl keyTags, Action onKeysChanged = null)
        {
            if (keyTags == null) return;
            keyTags.Items.Clear();
            var keys = GetSelectedKeys(keyName);
            foreach (var key in keys)
            {
                var tagBorder = new Border
                {
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 60, 60, 60)),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 2, 6, 2),
                    Margin = new Thickness(0, 0, 4, 0)
                };
                var tagPanel = new StackPanel { Orientation = Orientation.Horizontal };
                var keyText = new TextBlock
                {
                    Text = GetKeyDisplayName(key),
                    Foreground = new SolidColorBrush(Windows.UI.Colors.White),
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center
                };
                var removeButton = new Button
                {
                    Content = "×",
                    FontSize = 10,
                    Padding = new Thickness(4, 0, 0, 0),
                    Background = new SolidColorBrush(Windows.UI.Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 180, 180, 180)),
                    VerticalAlignment = VerticalAlignment.Center,
                    MinWidth = 0,
                    MinHeight = 0
                };
                int keyCode = key;
                removeButton.Click += (s, e) => RemoveKeyFromSelection(keyName, keyCode, keyTags, onKeysChanged);
                tagPanel.Children.Add(keyText);
                tagPanel.Children.Add(removeButton);
                tagBorder.Child = tagPanel;
                keyTags.Items.Add(tagBorder);
            }
        }

        private string GetKeysAsString(string keyName)
        {
            var keys = GetSelectedKeys(keyName);
            if (keys.Count == 0) return "";
            return string.Join("+", keys.Select(k => GetKeyDisplayName(k)));
        }

        private void LoadKeysFromString(string keyName, string keysString, ItemsControl keyTags)
        {
            var keys = GetSelectedKeys(keyName);
            keys.Clear();
            if (!string.IsNullOrEmpty(keysString))
            {
                var parts = keysString.Split('+');
                foreach (var part in parts)
                {
                    int keyCode = GetKeyCodeFromDisplayName(part.Trim());
                    if (keyCode > 0)
                        keys.Add(keyCode);
                }
            }
            UpdateKeyTagsDisplay(keyName, keyTags);
        }

        private int GetKeyCodeFromDisplayName(string name)
        {
            var keyNames = new Dictionary<string, int>
            {
                { "A", 0x04 }, { "B", 0x05 }, { "C", 0x06 }, { "D", 0x07 }, { "E", 0x08 },
                { "F", 0x09 }, { "G", 0x0A }, { "H", 0x0B }, { "I", 0x0C }, { "J", 0x0D },
                { "K", 0x0E }, { "L", 0x0F }, { "M", 0x10 }, { "N", 0x11 }, { "O", 0x12 },
                { "P", 0x13 }, { "Q", 0x14 }, { "R", 0x15 }, { "S", 0x16 }, { "T", 0x17 },
                { "U", 0x18 }, { "V", 0x19 }, { "W", 0x1A }, { "X", 0x1B }, { "Y", 0x1C },
                { "Z", 0x1D }, { "1", 0x1E }, { "2", 0x1F }, { "3", 0x20 }, { "4", 0x21 },
                { "5", 0x22 }, { "6", 0x23 }, { "7", 0x24 }, { "8", 0x25 }, { "9", 0x26 },
                { "0", 0x27 }, { "Enter", 0x28 }, { "Esc", 0x29 }, { "Backspace", 0x2A },
                { "Tab", 0x2B }, { "Space", 0x2C },
                { "F1", 0x3A }, { "F2", 0x3B }, { "F3", 0x3C }, { "F4", 0x3D }, { "F5", 0x3E },
                { "F6", 0x3F }, { "F7", 0x40 }, { "F8", 0x41 }, { "F9", 0x42 }, { "F10", 0x43 },
                { "F11", 0x44 }, { "F12", 0x45 },
                { "Right", 0x4F }, { "Left", 0x50 }, { "Down", 0x51 }, { "Up", 0x52 },
                { "Home", 0x4A }, { "PgUp", 0x4B }, { "Delete", 0x4C }, { "Del", 0x4C }, { "End", 0x4D },
                { "PgDn", 0x4E }, { "Insert", 0x49 }, { "Ins", 0x49 }, { "PrintScr", 0x46 }, { "PrtSc", 0x46 }, { "Pause", 0x48 },
                { "LCtrl", 0xE0 }, { "LShift", 0xE1 }, { "LAlt", 0xE2 }, { "LWin", 0xE3 }, { "LMeta", 0xE3 },
                { "RCtrl", 0xE4 }, { "RShift", 0xE5 }, { "RAlt", 0xE6 }, { "RWin", 0xE7 }, { "RMeta", 0xE7 },
                { "VolMute", 0x7F }, { "VolUp", 0x80 }, { "VolDown", 0x81 },
                { "[", 0x2F }, { "]", 0x30 }
            };
            return keyNames.TryGetValue(name, out int code) ? code : 0;
        }

        // Hotkey key selection handlers
        private void HotkeyMenuAKey_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (isLoadingHotkeys || HotkeyMenuAKeyComboBox?.SelectedIndex <= 0) return;
            int keyCode = GetKeyCodeFromDropdownIndex(HotkeyMenuAKeyComboBox.SelectedIndex);
            AddKeyToSelection("HotkeyMenuA", keyCode, HotkeyMenuAKeyTags, HotkeyMenuAKeyComboBox, () => SaveHotkeyKeys("MenuA", "HotkeyMenuA"));
        }

        private void HotkeyMenuBKey_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (isLoadingHotkeys || HotkeyMenuBKeyComboBox?.SelectedIndex <= 0) return;
            int keyCode = GetKeyCodeFromDropdownIndex(HotkeyMenuBKeyComboBox.SelectedIndex);
            AddKeyToSelection("HotkeyMenuB", keyCode, HotkeyMenuBKeyTags, HotkeyMenuBKeyComboBox, () => SaveHotkeyKeys("MenuB", "HotkeyMenuB"));
        }

        private void HotkeyMenuXKey_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (isLoadingHotkeys || HotkeyMenuXKeyComboBox?.SelectedIndex <= 0) return;
            int keyCode = GetKeyCodeFromDropdownIndex(HotkeyMenuXKeyComboBox.SelectedIndex);
            AddKeyToSelection("HotkeyMenuX", keyCode, HotkeyMenuXKeyTags, HotkeyMenuXKeyComboBox, () => SaveHotkeyKeys("MenuX", "HotkeyMenuX"));
        }

        private void HotkeyMenuYKey_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (isLoadingHotkeys || HotkeyMenuYKeyComboBox?.SelectedIndex <= 0) return;
            int keyCode = GetKeyCodeFromDropdownIndex(HotkeyMenuYKeyComboBox.SelectedIndex);
            AddKeyToSelection("HotkeyMenuY", keyCode, HotkeyMenuYKeyTags, HotkeyMenuYKeyComboBox, () => SaveHotkeyKeys("MenuY", "HotkeyMenuY"));
        }

        private void HotkeyMenuDpadUpKey_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (isLoadingHotkeys || HotkeyMenuDpadUpKeyComboBox?.SelectedIndex <= 0) return;
            int keyCode = GetKeyCodeFromDropdownIndex(HotkeyMenuDpadUpKeyComboBox.SelectedIndex);
            AddKeyToSelection("HotkeyMenuDpadUp", keyCode, HotkeyMenuDpadUpKeyTags, HotkeyMenuDpadUpKeyComboBox, () => SaveHotkeyKeys("MenuDpadUp", "HotkeyMenuDpadUp"));
        }

        private void HotkeyMenuDpadDownKey_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (isLoadingHotkeys || HotkeyMenuDpadDownKeyComboBox?.SelectedIndex <= 0) return;
            int keyCode = GetKeyCodeFromDropdownIndex(HotkeyMenuDpadDownKeyComboBox.SelectedIndex);
            AddKeyToSelection("HotkeyMenuDpadDown", keyCode, HotkeyMenuDpadDownKeyTags, HotkeyMenuDpadDownKeyComboBox, () => SaveHotkeyKeys("MenuDpadDown", "HotkeyMenuDpadDown"));
        }

        private void HotkeyMenuDpadLeftKey_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (isLoadingHotkeys || HotkeyMenuDpadLeftKeyComboBox?.SelectedIndex <= 0) return;
            int keyCode = GetKeyCodeFromDropdownIndex(HotkeyMenuDpadLeftKeyComboBox.SelectedIndex);
            AddKeyToSelection("HotkeyMenuDpadLeft", keyCode, HotkeyMenuDpadLeftKeyTags, HotkeyMenuDpadLeftKeyComboBox, () => SaveHotkeyKeys("MenuDpadLeft", "HotkeyMenuDpadLeft"));
        }

        private void HotkeyMenuDpadRightKey_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (isLoadingHotkeys || HotkeyMenuDpadRightKeyComboBox?.SelectedIndex <= 0) return;
            int keyCode = GetKeyCodeFromDropdownIndex(HotkeyMenuDpadRightKeyComboBox.SelectedIndex);
            AddKeyToSelection("HotkeyMenuDpadRight", keyCode, HotkeyMenuDpadRightKeyTags, HotkeyMenuDpadRightKeyComboBox, () => SaveHotkeyKeys("MenuDpadRight", "HotkeyMenuDpadRight"));
        }

        private void SaveHotkeyKeys(string hotkeyName, string keyStorageName)
        {
            var keysString = GetKeysAsString(keyStorageName);
            ApplicationData.Current.LocalSettings.Values[$"Hotkey_{hotkeyName}_Key"] = keysString;
            Logger.Info($"Hotkey {hotkeyName} keys saved: {keysString}");

            // Sync updated config to helper so its XInput monitor uses the new key
            SendControllerHotkeyConfigToHelper();
        }

        // Legion L/R key selection handlers
        private void LegionLKeyComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LegionLKeyComboBox?.SelectedIndex <= 0) return;
            int keyCode = GetKeyCodeFromDropdownIndex(LegionLKeyComboBox.SelectedIndex);
            AddKeyToSelection("LegionL", keyCode, LegionLKeyTags, LegionLKeyComboBox, SaveLegionLKeys);
        }

        private void LegionRKeyComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LegionRKeyComboBox?.SelectedIndex <= 0) return;
            int keyCode = GetKeyCodeFromDropdownIndex(LegionRKeyComboBox.SelectedIndex);
            AddKeyToSelection("LegionR", keyCode, LegionRKeyTags, LegionRKeyComboBox, SaveLegionRKeys);
        }

        private void SaveLegionLKeys()
        {
            var keysString = GetKeysAsString("LegionL");
            ApplicationData.Current.LocalSettings.Values["LegionL_Shortcut"] = keysString;
            SaveLegionRemapSettings();
            ApplyLegionButtonConfig(true);
        }

        private void SaveLegionRKeys()
        {
            var keysString = GetKeysAsString("LegionR");
            ApplicationData.Current.LocalSettings.Values["LegionR_Shortcut"] = keysString;
            SaveLegionRemapSettings();
            ApplyLegionButtonConfig(false);
        }

        // Long-press variants of the Legion L/R key pickers.
        private void LegionLLongKeyComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var combo = FindName("LegionLLongKeyComboBox") as ComboBox;
            if (combo?.SelectedIndex <= 0) return;
            int keyCode = GetKeyCodeFromDropdownIndex(combo.SelectedIndex);
            AddKeyToSelection("LegionLLong", keyCode, FindName("LegionLLongKeyTags") as ItemsControl, combo, SaveLegionLLongKeys);
        }

        private void LegionRLongKeyComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var combo = FindName("LegionRLongKeyComboBox") as ComboBox;
            if (combo?.SelectedIndex <= 0) return;
            int keyCode = GetKeyCodeFromDropdownIndex(combo.SelectedIndex);
            AddKeyToSelection("LegionRLong", keyCode, FindName("LegionRLongKeyTags") as ItemsControl, combo, SaveLegionRLongKeys);
        }

        private void SaveLegionLLongKeys()
        {
            ApplicationData.Current.LocalSettings.Values["LegionL_LongShortcut"] = GetKeysAsString("LegionLLong");
            SaveLegionRemapSettings();
            ApplyLegionButtonLongConfig(true);
        }

        private void SaveLegionRLongKeys()
        {
            ApplicationData.Current.LocalSettings.Values["LegionR_LongShortcut"] = GetKeysAsString("LegionRLong");
            SaveLegionRemapSettings();
            ApplyLegionButtonLongConfig(false);
        }

        // Scroll wheel key selection handlers
        private void ScrollKeyComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ScrollKeyComboBox?.SelectedIndex <= 0) return;
            int keyCode = GetKeyCodeFromDropdownIndex(ScrollKeyComboBox.SelectedIndex);
            AddKeyToSelection("Scroll", keyCode, ScrollKeyTags, ScrollKeyComboBox, SaveScrollKeys);
        }

        private void ScrollClickKeyComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ScrollClickKeyComboBox?.SelectedIndex <= 0) return;
            int keyCode = GetKeyCodeFromDropdownIndex(ScrollClickKeyComboBox.SelectedIndex);
            AddKeyToSelection("ScrollClick", keyCode, ScrollClickKeyTags, ScrollClickKeyComboBox, SaveScrollClickKeys);
        }

        private void SaveScrollKeys()
        {
            var keysString = GetKeysAsString("Scroll");
            ApplicationData.Current.LocalSettings.Values["Scroll_Shortcut"] = keysString;
            SaveScrollRemapSettings();
            ApplyScrollWheelConfig("Scroll");
        }

        private void SaveScrollClickKeys()
        {
            var keysString = GetKeysAsString("ScrollClick");
            ApplicationData.Current.LocalSettings.Values["ScrollClick_Shortcut"] = keysString;
            SaveScrollRemapSettings();
            ApplyScrollWheelConfig("Click");
        }

        // Custom shortcut key selection handler
        private void CustomShortcutKey_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CustomShortcutKeyComboBox?.SelectedIndex <= 0) return;
            int keyCode = GetKeyCodeFromDropdownIndex(CustomShortcutKeyComboBox.SelectedIndex);
            if (_customShortcutKeys.Count < 5 && !_customShortcutKeys.Contains(keyCode) && keyCode > 0)
            {
                _customShortcutKeys.Add(keyCode);
                UpdateCustomShortcutKeyTags();
            }
            CustomShortcutKeyComboBox.SelectedIndex = 0;
        }

        private void UpdateCustomShortcutKeyTags()
        {
            if (CustomShortcutKeyTags == null) return;
            CustomShortcutKeyTags.Items.Clear();
            foreach (var key in _customShortcutKeys)
            {
                var tagBorder = new Border
                {
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 60, 60, 60)),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 2, 6, 2),
                    Margin = new Thickness(0, 0, 4, 0)
                };
                var tagPanel = new StackPanel { Orientation = Orientation.Horizontal };
                var keyText = new TextBlock
                {
                    Text = GetKeyDisplayName(key),
                    Foreground = new SolidColorBrush(Windows.UI.Colors.White),
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center
                };
                var removeButton = new Button
                {
                    Content = "×",
                    FontSize = 10,
                    Padding = new Thickness(4, 0, 0, 0),
                    Background = new SolidColorBrush(Windows.UI.Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 180, 180, 180)),
                    VerticalAlignment = VerticalAlignment.Center,
                    MinWidth = 0,
                    MinHeight = 0
                };
                int keyCode = key;
                removeButton.Click += (s, args) => { _customShortcutKeys.Remove(keyCode); UpdateCustomShortcutKeyTags(); };
                tagPanel.Children.Add(keyText);
                tagPanel.Children.Add(removeButton);
                tagBorder.Child = tagPanel;
                CustomShortcutKeyTags.Items.Add(tagBorder);
            }
        }

        private string GetCustomShortcutKeysString()
        {
            if (_customShortcutKeys.Count == 0) return "";
            return string.Join("+", _customShortcutKeys.Select(k => GetKeyDisplayName(k)));
        }

    }
}
