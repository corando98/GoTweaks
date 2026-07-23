using System;
using Windows.Foundation.Collections;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace XboxGamingBar
{
    public sealed partial class GamingWidget
    {
        // Suppress outbound sends while we populate controls from the helper's current values on
        // load (otherwise every programmatic SelectedIndex/Value change echoes back).
        private bool _goTweaksSyncing;

        // The reactive-lighting flash color (separate from the static Light Color picker).
        private byte _reactiveFlashR = 255, _reactiveFlashG = 255, _reactiveFlashB = 255;

        // User-editable Cycle palette (ordered list of colors). Default mirrors the helper default.
        private readonly System.Collections.Generic.List<Color> _palette = new System.Collections.Generic.List<Color>
        {
            Color.FromArgb(255, 0xFF, 0x00, 0x00), Color.FromArgb(255, 0xFF, 0x80, 0x00),
            Color.FromArgb(255, 0xFF, 0xFF, 0x00), Color.FromArgb(255, 0x00, 0xFF, 0x00),
            Color.FromArgb(255, 0x00, 0xFF, 0xFF), Color.FromArgb(255, 0x00, 0x00, 0xFF),
            Color.FromArgb(255, 0xB4, 0x00, 0xFF), Color.FromArgb(255, 0xFF, 0x00, 0xB4),
        };
        private int _paletteSelectedIndex = -1;

        // --- Reactive lighting (new controls appended to the existing Lighting card) ---------

        private void GoTweaksReactiveFlashExpandButton_Click(object sender, RoutedEventArgs e)
        {
            if (GoTweaksReactiveFlashColorPicker == null) return;
            GoTweaksReactiveFlashColorPicker.Visibility =
                GoTweaksReactiveFlashColorPicker.Visibility == Visibility.Visible
                    ? Visibility.Collapsed : Visibility.Visible;
        }

        private void GoTweaksReactiveFlashColorPicker_ColorChanged(Microsoft.UI.Xaml.Controls.ColorPicker sender, Microsoft.UI.Xaml.Controls.ColorChangedEventArgs args)
        {
            Color c = args.NewColor;
            _reactiveFlashR = c.R; _reactiveFlashG = c.G; _reactiveFlashB = c.B;
            if (GoTweaksReactiveFlashPreview != null)
            {
                GoTweaksReactiveFlashPreview.Background =
                    new Windows.UI.Xaml.Media.SolidColorBrush(c);
            }
            if (_goTweaksSyncing) return;
            SendLightingConfig();
        }

        // --- Cycle palette editor -----------------------------------------------------------

        // Rebuild the swatch strip from _palette. Each swatch is a clickable Border; the selected
        // one gets a highlight ring and binds the ColorPicker.
        private void RebuildPaletteSwatches()
        {
            if (GoTweaksPaletteSwatches == null) return;
            GoTweaksPaletteSwatches.Items.Clear();
            for (int i = 0; i < _palette.Count; i++)
            {
                var border = new Border
                {
                    Width = 24,
                    Height = 24,
                    CornerRadius = new Windows.UI.Xaml.CornerRadius(4),
                    Margin = new Thickness(0, 0, 6, 0),
                    Background = new Windows.UI.Xaml.Media.SolidColorBrush(_palette[i]),
                    BorderBrush = new Windows.UI.Xaml.Media.SolidColorBrush(
                        i == _paletteSelectedIndex ? Colors.White : Color.FromArgb(255, 0x50, 0x55, 0x5C)),
                    BorderThickness = new Thickness(i == _paletteSelectedIndex ? 2 : 1),
                    Tag = i,
                };
                border.Tapped += PaletteSwatch_Tapped;
                GoTweaksPaletteSwatches.Items.Add(border);
            }
        }

        private void PaletteSwatch_Tapped(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            if (!(sender is Border b) || !(b.Tag is int idx)) return;
            _paletteSelectedIndex = idx;
            RebuildPaletteSwatches();
            if (GoTweaksPaletteColorPicker != null)
            {
                _goTweaksSyncing = true;
                try { GoTweaksPaletteColorPicker.Color = _palette[idx]; }
                finally { _goTweaksSyncing = false; }
                GoTweaksPaletteColorPicker.Visibility = Visibility.Visible;
            }
        }

        private void GoTweaksPaletteColorPicker_ColorChanged(Microsoft.UI.Xaml.Controls.ColorPicker sender, Microsoft.UI.Xaml.Controls.ColorChangedEventArgs args)
        {
            if (_paletteSelectedIndex < 0 || _paletteSelectedIndex >= _palette.Count) return;
            _palette[_paletteSelectedIndex] = args.NewColor;
            RebuildPaletteSwatches();
            if (_goTweaksSyncing) return;
            SendLightingConfig();
        }

        private void GoTweaksPaletteAddButton_Click(object sender, RoutedEventArgs e)
        {
            if (_palette.Count >= 16) return; // keep the config string and strip sane
            _palette.Add(Colors.White);
            _paletteSelectedIndex = _palette.Count - 1;
            RebuildPaletteSwatches();
            if (GoTweaksPaletteColorPicker != null)
            {
                _goTweaksSyncing = true;
                try { GoTweaksPaletteColorPicker.Color = Colors.White; }
                finally { _goTweaksSyncing = false; }
                GoTweaksPaletteColorPicker.Visibility = Visibility.Visible;
            }
            SendLightingConfig();
        }

        private void GoTweaksPaletteRemoveButton_Click(object sender, RoutedEventArgs e)
        {
            if (_palette.Count <= 1) return; // never empty
            int idx = _paletteSelectedIndex >= 0 ? _paletteSelectedIndex : _palette.Count - 1;
            _palette.RemoveAt(idx);
            _paletteSelectedIndex = Math.Min(idx, _palette.Count - 1);
            RebuildPaletteSwatches();
            SendLightingConfig();
        }

        private void GoTweaksReactive_Changed(object sender, object e)
        {
            UpdateReactiveCardVisibility();
            if (_goTweaksSyncing) return;
            SendLightingConfig();
        }

        private void UpdateReactiveCardVisibility()
        {
            string mode = (GoTweaksReactiveModeComboBox?.SelectedItem as ComboBoxItem)?.Tag as string ?? "disabled";
            // Flash color is the target color for "flash" and the full-pull color for "trigger".
            if (GoTweaksReactiveFlashColorCard != null)
            {
                GoTweaksReactiveFlashColorCard.Visibility = (mode == "flash" || mode == "trigger") ? Visibility.Visible : Visibility.Collapsed;
            }
            // Decay applies only to press-flash modes; continuous (trigger/battery) and hue don't use it.
            if (GoTweaksReactiveDecayCard != null)
            {
                bool usesDecay = mode == "flash" || mode == "cycle" || mode == "perbutton" || mode == "hue";
                GoTweaksReactiveDecayCard.Visibility = usesDecay ? Visibility.Visible : Visibility.Collapsed;
            }
            // Palette editor only for Cycle palette.
            if (GoTweaksPaletteCard != null)
            {
                GoTweaksPaletteCard.Visibility = (mode == "cycle") ? Visibility.Visible : Visibility.Collapsed;
            }
            // Lighting-card header badge (with tooltip): visible whenever a reactive
            // effect is active, so the collapsed card still signals that something is
            // driving the stick RGB.
            if (LightingReactiveBadge != null)
            {
                LightingReactiveBadge.Visibility = (mode != "disabled") ? Visibility.Visible : Visibility.Collapsed;
            }
            if (GoTweaksReactiveDecayValue != null && GoTweaksReactiveDecaySlider != null)
            {
                GoTweaksReactiveDecayValue.Text = $"{(int)GoTweaksReactiveDecaySlider.Value} ms";
            }

            // Plain-language summary of what the selected effect does, plus a note when the
            // static Light Mode is Off — a reactive effect still lights the sticks then,
            // which otherwise reads as a contradiction ("Light Mode: Off" while glowing).
            if (GoTweaksReactiveDescription != null)
            {
                string desc;
                switch (mode)
                {
                    case "flash":
                        desc = "Sticks flash to the chosen color on any button press, then fade back. Trigger pull and stick deflection also blend toward the color by how far they're pushed.";
                        break;
                    case "cycle":
                        desc = "Each button press flashes the next color from your palette.";
                        break;
                    case "perbutton":
                        desc = "Each button has its own flash color (Xbox face-button colors, cyan D-pad, purple paddles).";
                        break;
                    case "hue":
                        desc = "Each press rotates the flash color a step around the color wheel.";
                        break;
                    case "trigger":
                        desc = "Trigger pull blends the stick color toward the flash color — the harder the pull, the closer to it. Releases back to your Light Mode at rest.";
                        break;
                    case "battery":
                        desc = "Sticks show charge as a color: green when full, through amber, red when empty.";
                        break;
                    case "cputemp":
                        desc = "Sticks show CPU temperature as a color: blue when cool (45°C and below) sweeping to red when hot (90°C and up).";
                        break;
                    default:
                        desc = null;
                        break;
                }

                bool staticOff = LegionLightModeComboBox != null && LegionLightModeComboBox.SelectedIndex == 0;
                if (desc != null && staticOff)
                {
                    desc += " Runs even while Light Mode is Off.";
                }

                GoTweaksReactiveDescription.Text = desc ?? "";
                GoTweaksReactiveDescription.Visibility = desc != null ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        /// <summary>
        /// Build and send the lighting config string. The static fields (mode/base/brightness/
        /// speed) come from the EXISTING Legion lighting controls so the helper has the full
        /// picture; the helper's reactive manager only consumes mode/flash/decay. Static mode is
        /// reported as its own name so a future consolidation can read it, but today the static
        /// lighting still flows through its own LegionLight* properties — here we only need the
        /// reactive fields to be correct.
        /// </summary>
        private void SendLightingConfig()
        {
            try
            {
                string mode = (GoTweaksReactiveModeComboBox?.SelectedItem as ComboBoxItem)?.Tag as string ?? "disabled";
                string flashHex = $"{_reactiveFlashR:X2}{_reactiveFlashG:X2}{_reactiveFlashB:X2}";
                int decayMs = (int)(GoTweaksReactiveDecaySlider?.Value ?? 500);
                int bright = (int)(LegionBrightnessSlider?.Value ?? 100);
                int speed = (int)(LegionSpeedSlider?.Value ?? 50);
                string baseHex = "00AAFF"; // base color is owned by the static Light Color picker

                // Format: mode|baseHex|flashHex|decayMs|brightness|speed|pal:RRGGBB,...
                var palHex = new System.Text.StringBuilder();
                for (int i = 0; i < _palette.Count; i++)
                {
                    if (i > 0) palHex.Append(',');
                    palHex.Append($"{_palette[i].R:X2}{_palette[i].G:X2}{_palette[i].B:X2}");
                }
                string config = $"{mode}|{baseHex}|{flashHex}|{decayMs}|{bright}|{speed}|pal:{palHex}";
                SendConfigToHelper(Shared.Enums.Function.GoTweaksLightingConfig, config);
            }
            catch (Exception ex) { Logger.Warn($"SendLightingConfig failed: {ex.Message}"); }
        }

        // --- Haptics ------------------------------------------------------------------------

        // Card-header badge mirroring the Lighting card's Reactive chip: visible while
        // Reactive Haptics is enabled so the collapsed card still signals it.
        private void UpdateHapticsReactiveBadge()
        {
            if (HapticsReactiveBadge != null)
            {
                HapticsReactiveBadge.Visibility =
                    (GoTweaksHapticsMasterToggle?.IsOn ?? false) ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void GoTweaksHaptics_Changed(object sender, object e)
        {
            if (GoTweaksHapticsGroupsPanel != null && GoTweaksHapticsMasterToggle != null)
            {
                GoTweaksHapticsGroupsPanel.Visibility =
                    GoTweaksHapticsMasterToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
            }
            UpdateHapticsReactiveBadge();
            if (_goTweaksSyncing) return;
            SendHapticsConfig();
        }

        private void SendHapticsConfig()
        {
            try
            {
                int master = (GoTweaksHapticsMasterToggle?.IsOn ?? false) ? 1 : 0;
                int onRelease = (GoTweaksHapticsReleaseToggle?.IsOn ?? false) ? 1 : 0;
                int pulseMs = (int)(GoTweaksHapticsPulseSlider?.Value ?? 10);
                if (GoTweaksHapticsPulseValue != null) GoTweaksHapticsPulseValue.Text = $"{pulseMs} ms";
                string face = GroupSeg("face", GoTweaksHapticsFaceToggle, GoTweaksHapticsFaceSlider);
                string front = GroupSeg("front", GoTweaksHapticsFrontToggle, GoTweaksHapticsFrontSlider);
                string back = GroupSeg("back", GoTweaksHapticsBackToggle, GoTweaksHapticsBackSlider);
                string trigger = GroupSeg("trigger", GoTweaksHapticsTriggerToggle, GoTweaksHapticsTriggerSlider);

                string config = $"{master}|{face}|{front}|{back}|{trigger}|rel:{onRelease}|pulse:{pulseMs}";
                SendConfigToHelper(Shared.Enums.Function.GoTweaksHapticsConfig, config);
            }
            catch (Exception ex) { Logger.Warn($"SendHapticsConfig failed: {ex.Message}"); }
        }

        private static string GroupSeg(string name, ToggleSwitch toggle, Slider slider)
        {
            int on = (toggle?.IsOn ?? false) ? 1 : 0;
            int intensity = (int)(slider?.Value ?? 100);
            return $"{name}:{on},{intensity}";
        }

        // --- Shared helpers -----------------------------------------------------------------

        private static void SendConfigToHelper(Shared.Enums.Function fn, string value)
        {
            if (!App.IsConnected) return;
            var msg = new ValueSet
            {
                { "Command", (int)Shared.Enums.Command.Set },
                { "Function", (int)fn },
                { "Content", value }
            };
            App.PipeClient?.SendValueSet(msg);
        }

        private static void SendIntToHelper(Shared.Enums.Function fn, int value)
        {
            if (!App.IsConnected) return;
            var msg = new ValueSet
            {
                { "Command", (int)Shared.Enums.Command.Set },
                { "Function", (int)fn },
                { "Content", value }
            };
            App.PipeClient?.SendValueSet(msg);
        }

        // --- Controller auto-sleep timeout --------------------------------------------------

        private void LegionControllerSleepComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_goTweaksSyncing) return;
            try
            {
                string tag = (LegionControllerSleepComboBox?.SelectedItem as ComboBoxItem)?.Tag as string ?? "15";
                if (int.TryParse(tag, out int minutes))
                {
                    SendIntToHelper(Shared.Enums.Function.LegionControllerSleepMinutes, minutes);
                }
            }
            catch (Exception ex) { Logger.Warn($"LegionControllerSleepComboBox_SelectionChanged failed: {ex.Message}"); }
        }

        /// <summary>
        /// Request persisted lighting + haptics config from the helper and populate the controls.
        /// Guards sends with _goTweaksSyncing during the programmatic fill.
        /// </summary>
        internal async void SyncGoTweaksFeaturesFromHelper()
        {
            try
            {
                if (!App.IsConnected) return;
                string lighting = await RequestStringAsync(Shared.Enums.Function.GoTweaksLightingConfig);
                string haptics = await RequestStringAsync(Shared.Enums.Function.GoTweaksHapticsConfig);
                string sleep = await RequestStringAsync(Shared.Enums.Function.LegionControllerSleepMinutes);

                _goTweaksSyncing = true;
                try
                {
                    if (!string.IsNullOrEmpty(lighting)) ApplyLightingToUi(lighting);
                    if (!string.IsNullOrEmpty(haptics)) ApplyHapticsToUi(haptics);
                    if (!string.IsNullOrEmpty(sleep)) SelectComboByTag(LegionControllerSleepComboBox, sleep.Trim());
                }
                finally { _goTweaksSyncing = false; }

                UpdateReactiveCardVisibility();
            }
            catch (Exception ex) { Logger.Warn($"SyncGoTweaksFeaturesFromHelper failed: {ex.Message}"); }
        }

        private static async System.Threading.Tasks.Task<string> RequestStringAsync(Shared.Enums.Function fn)
        {
            try
            {
                var request = new ValueSet
                {
                    { "Command", (int)Shared.Enums.Command.Get },
                    { "Function", (int)fn }
                };
                var resp = await App.PipeClient.SendRequestAsync(request);
                if (resp != null && resp.TryGetValue("Content", out object content) && content != null)
                {
                    // Content may be a string (config blobs) or a boxed int (sleep minutes).
                    return content as string ?? content.ToString();
                }
            }
            catch (Exception ex) { Logger.Warn($"RequestStringAsync {fn} failed: {ex.Message}"); }
            return null;
        }

        private void ApplyLightingToUi(string config)
        {
            string[] p = config.Split('|');
            string mode = p.Length > 0 ? p[0].Trim().ToLowerInvariant() : "disabled";
            // Only the reactive modes live in our combo; static modes belong to the existing
            // Light Mode combo and are not echoed here.
            if (mode != "flash" && mode != "cycle" && mode != "perbutton"
                && mode != "hue" && mode != "trigger" && mode != "battery"
                && mode != "cputemp") mode = "disabled";
            SelectComboByTag(GoTweaksReactiveModeComboBox, mode);

            if (p.Length > 2)
            {
                var (r, g, b) = ParseHex(p[2], 255, 255, 255);
                _reactiveFlashR = r; _reactiveFlashG = g; _reactiveFlashB = b;
                var c = Color.FromArgb(255, r, g, b);
                if (GoTweaksReactiveFlashColorPicker != null) GoTweaksReactiveFlashColorPicker.Color = c;
                if (GoTweaksReactiveFlashPreview != null) GoTweaksReactiveFlashPreview.Background = new Windows.UI.Xaml.Media.SolidColorBrush(c);
            }
            if (p.Length > 3) SetSlider(GoTweaksReactiveDecaySlider, p[3], 500);

            // Palette field "pal:RRGGBB,..." (search after the fixed fields).
            for (int i = 6; i < p.Length; i++)
            {
                string seg = (p[i] ?? "").Trim();
                if (seg.StartsWith("pal:", StringComparison.OrdinalIgnoreCase))
                {
                    var parsed = ParsePaletteCsv(seg.Substring(4));
                    if (parsed.Count > 0)
                    {
                        _palette.Clear();
                        _palette.AddRange(parsed);
                        _paletteSelectedIndex = -1;
                    }
                    break;
                }
            }
            RebuildPaletteSwatches();
        }

        private static System.Collections.Generic.List<Color> ParsePaletteCsv(string csv)
        {
            var list = new System.Collections.Generic.List<Color>();
            if (string.IsNullOrWhiteSpace(csv)) return list;
            foreach (var raw in csv.Split(','))
            {
                var (r, g, b) = ParseHex(raw, 255, 255, 255);
                if (!string.IsNullOrWhiteSpace(raw)) list.Add(Color.FromArgb(255, r, g, b));
            }
            return list;
        }

        private void ApplyHapticsToUi(string config)
        {
            string[] parts = config.Split('|');
            if (GoTweaksHapticsMasterToggle != null)
            {
                GoTweaksHapticsMasterToggle.IsOn = parts.Length > 0 && parts[0].Trim() == "1";
            }
            for (int i = 1; i < parts.Length; i++)
            {
                string seg = parts[i].Trim();
                int colon = seg.IndexOf(':');
                if (colon <= 0) continue;
                string name = seg.Substring(0, colon).Trim().ToLowerInvariant();
                string[] kv = seg.Substring(colon + 1).Split(',');
                bool on = kv.Length > 0 && kv[0].Trim() == "1";
                int intensity = kv.Length > 1 && int.TryParse(kv[1].Trim(), out int v) ? v : 100;

                switch (name)
                {
                    case "face": SetGroupUi(GoTweaksHapticsFaceToggle, GoTweaksHapticsFaceSlider, on, intensity); break;
                    case "front": SetGroupUi(GoTweaksHapticsFrontToggle, GoTweaksHapticsFrontSlider, on, intensity); break;
                    case "back": SetGroupUi(GoTweaksHapticsBackToggle, GoTweaksHapticsBackSlider, on, intensity); break;
                    case "trigger": SetGroupUi(GoTweaksHapticsTriggerToggle, GoTweaksHapticsTriggerSlider, on, intensity); break;
                    case "rel": if (GoTweaksHapticsReleaseToggle != null) GoTweaksHapticsReleaseToggle.IsOn = on; break;
                    case "pulse":
                        if (GoTweaksHapticsPulseSlider != null && kv.Length > 0 && int.TryParse(kv[0].Trim(), out int pm))
                        {
                            GoTweaksHapticsPulseSlider.Value = Math.Min(Math.Max(pm, 4), 40);
                            if (GoTweaksHapticsPulseValue != null) GoTweaksHapticsPulseValue.Text = $"{(int)GoTweaksHapticsPulseSlider.Value} ms";
                        }
                        break;
                }
            }
            if (GoTweaksHapticsGroupsPanel != null && GoTweaksHapticsMasterToggle != null)
            {
                GoTweaksHapticsGroupsPanel.Visibility =
                    GoTweaksHapticsMasterToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
            }
            UpdateHapticsReactiveBadge();
        }

        private static void SetGroupUi(ToggleSwitch toggle, Slider slider, bool on, int intensity)
        {
            if (toggle != null) toggle.IsOn = on;
            if (slider != null) slider.Value = Math.Min(Math.Max(intensity, 0), 100);
        }

        private static void SelectComboByTag(ComboBox combo, string tag)
        {
            if (combo == null) return;
            for (int i = 0; i < combo.Items.Count; i++)
            {
                if (combo.Items[i] is ComboBoxItem item && (item.Tag as string) == tag)
                {
                    combo.SelectedIndex = i;
                    return;
                }
            }
        }

        private static void SetSlider(Slider slider, string value, double fallback)
        {
            if (slider == null) return;
            slider.Value = double.TryParse((value ?? "").Trim(), out double v) ? v : fallback;
        }

        private static (byte r, byte g, byte b) ParseHex(string s, byte dr, byte dg, byte db)
        {
            s = (s ?? "").Trim().TrimStart('#');
            if (s.Length == 6
                && byte.TryParse(s.Substring(0, 2), System.Globalization.NumberStyles.HexNumber, null, out byte r)
                && byte.TryParse(s.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, null, out byte g)
                && byte.TryParse(s.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, null, out byte b))
            {
                return (r, g, b);
            }
            return (dr, dg, db);
        }
    }
}
