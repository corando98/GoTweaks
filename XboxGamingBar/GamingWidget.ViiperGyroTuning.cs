using System;
using System.Collections.Generic;
using Windows.UI.Xaml.Controls;

namespace XboxGamingBar
{
    /// <summary>
    /// Gyro Tuning UI for the VIIPER backend: separate per-axis remap for the emulated
    /// device's gyroscope and accelerometer, stored PER gyro source (Left/Right/Mixed/
    /// Handheld) because the two Legion halves have physically mirrored IMUs. Each sensor
    /// has an Invert X/Y/Z toggle row and a Matrix X/Y/Z source-axis mapping. The 12 controls
    /// don't bind individually — code-behind reads them, rebuilds one csv per sensor
    /// ("mapX,mapY,mapZ,invX,invY,invZ") for the currently-selected source, merges it into a
    /// per-source bundle ("Left=csv|Right=csv|..."), and pushes the bundle to the helper via
    /// <see cref="Shared.Enums.Function.Viiper_GyroTuning"/> /
    /// <see cref="Shared.Enums.Function.Viiper_AccelTuning"/>. The editor always shows the
    /// entry for the current gyro source and reloads when the source changes.
    /// </summary>
    public sealed partial class GamingWidget
    {
        // Suppress the control change handlers while we apply persisted / helper-pushed /
        // source-switch values, so populating a control doesn't re-serialize-and-push.
        private bool gyroTuningSuppressEvents;

        private void InitGyroTuningControls()
        {
            ApplyTuningToControls();

            // Re-apply when the helper pushes a newer persisted bundle after connect, and
            // when the gyro source changes (a different per-source entry now applies).
            if (viiperGyroTuning != null) viiperGyroTuning.PropertyChanged += (_, __) => ApplyTuningToControls();
            if (viiperAccelTuning != null) viiperAccelTuning.PropertyChanged += (_, __) => ApplyTuningToControls();
            if (viiperGyroSource != null) viiperGyroSource.PropertyChanged += (_, __) => ApplyTuningToControls();

            // Wire every control to rebuild-and-push.
            WireGyroTuning(ViiperGyroInvertXToggle, ViiperGyroInvertYToggle, ViiperGyroInvertZToggle,
                ViiperGyroMatrixXComboBox, ViiperGyroMatrixYComboBox, ViiperGyroMatrixZComboBox);
            WireGyroTuning(ViiperAccelInvertXToggle, ViiperAccelInvertYToggle, ViiperAccelInvertZToggle,
                ViiperAccelMatrixXComboBox, ViiperAccelMatrixYComboBox, ViiperAccelMatrixZComboBox);
        }

        // Which source the tuning editor is currently bound to. "None" has no gyro, so we
        // fall back to Left so the controls still show/edit a meaningful entry.
        private string CurrentGyroTuningSource()
        {
            var s = viiperGyroSource?.Value;
            if (s == "Left" || s == "Right" || s == "Mixed" || s == "Handheld") return s;
            return "Left";
        }

        private void WireGyroTuning(ToggleSwitch ix, ToggleSwitch iy, ToggleSwitch iz,
            ComboBox mx, ComboBox my, ComboBox mz)
        {
            if (ix != null) ix.Toggled += GyroTuningChanged;
            if (iy != null) iy.Toggled += GyroTuningChanged;
            if (iz != null) iz.Toggled += GyroTuningChanged;
            if (mx != null) mx.SelectionChanged += GyroTuningChanged;
            if (my != null) my.SelectionChanged += GyroTuningChanged;
            if (mz != null) mz.SelectionChanged += GyroTuningChanged;
        }

        private void GyroTuningChanged(object sender, object e)
        {
            if (gyroTuningSuppressEvents) return;
            try
            {
                var source = CurrentGyroTuningSource();
                var gyroCsv = BuildTuningCsv(
                    ViiperGyroInvertXToggle, ViiperGyroInvertYToggle, ViiperGyroInvertZToggle,
                    ViiperGyroMatrixXComboBox, ViiperGyroMatrixYComboBox, ViiperGyroMatrixZComboBox);
                var accelCsv = BuildTuningCsv(
                    ViiperAccelInvertXToggle, ViiperAccelInvertYToggle, ViiperAccelInvertZToggle,
                    ViiperAccelMatrixXComboBox, ViiperAccelMatrixYComboBox, ViiperAccelMatrixZComboBox);
                viiperGyroTuning?.SetValue(MergeIntoBundle(viiperGyroTuning?.Value, source, gyroCsv));
                viiperAccelTuning?.SetValue(MergeIntoBundle(viiperAccelTuning?.Value, source, accelCsv));
            }
            catch (Exception ex)
            {
                Logger.Warn($"GyroTuningChanged threw: {ex.Message}");
            }
        }

        // Build "mapX,mapY,mapZ,invX,invY,invZ" from a sensor's controls.
        private static string BuildTuningCsv(ToggleSwitch ix, ToggleSwitch iy, ToggleSwitch iz,
            ComboBox mx, ComboBox my, ComboBox mz)
        {
            string Map(ComboBox c)
            {
                var tag = (c?.SelectedItem as ComboBoxItem)?.Tag as string;
                return (tag == "X" || tag == "Y" || tag == "Z") ? tag : "X";
            }
            string Inv(ToggleSwitch t) => (t != null && t.IsOn) ? "1" : "0";
            return Map(mx) + "," + Map(my) + "," + Map(mz) + "," + Inv(ix) + "," + Inv(iy) + "," + Inv(iz);
        }

        private void ApplyTuningToControls()
        {
            gyroTuningSuppressEvents = true;
            try
            {
                var source = CurrentGyroTuningSource();
                if (ViiperGyroTuningSourceLabel != null)
                    ViiperGyroTuningSourceLabel.Text = "Editing tuning for: " + SourceDisplayName(source);
                ApplyOneTuning(ExtractFromBundle(viiperGyroTuning?.Value, source),
                    ViiperGyroInvertXToggle, ViiperGyroInvertYToggle, ViiperGyroInvertZToggle,
                    ViiperGyroMatrixXComboBox, ViiperGyroMatrixYComboBox, ViiperGyroMatrixZComboBox);
                ApplyOneTuning(ExtractFromBundle(viiperAccelTuning?.Value, source),
                    ViiperAccelInvertXToggle, ViiperAccelInvertYToggle, ViiperAccelInvertZToggle,
                    ViiperAccelMatrixXComboBox, ViiperAccelMatrixYComboBox, ViiperAccelMatrixZComboBox);
            }
            finally
            {
                gyroTuningSuppressEvents = false;
            }
        }

        private static string SourceDisplayName(string source)
        {
            switch (source)
            {
                case "Left": return "Left controller";
                case "Right": return "Right controller";
                case "Mixed": return "Mixed (L+R)";
                case "Handheld": return "Handheld";
                default: return source;
            }
        }

        private static void ApplyOneTuning(string csv,
            ToggleSwitch ix, ToggleSwitch iy, ToggleSwitch iz,
            ComboBox mx, ComboBox my, ComboBox mz)
        {
            // Identity fallback for null/empty/malformed.
            string[] p = { "X", "Y", "Z", "0", "0", "0" };
            if (!string.IsNullOrWhiteSpace(csv))
            {
                var parts = csv.Split(',');
                if (parts.Length >= 6) p = parts;
            }
            SelectByTag(mx, Norm(p[0], "X"));
            SelectByTag(my, Norm(p[1], "Y"));
            SelectByTag(mz, Norm(p[2], "Z"));
            if (ix != null) ix.IsOn = p[3]?.Trim() == "1";
            if (iy != null) iy.IsOn = p[4]?.Trim() == "1";
            if (iz != null) iz.IsOn = p[5]?.Trim() == "1";
        }

        // ---- Per-source bundle helpers: "Left=csv|Right=csv|Mixed=csv|Handheld=csv" ----

        private static Dictionary<string, string> ParseBundle(string bundle)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(bundle)) return map;
            foreach (var entry in bundle.Split('|'))
            {
                int eq = entry.IndexOf('=');
                if (eq <= 0) continue;
                var key = entry.Substring(0, eq).Trim();
                var val = entry.Substring(eq + 1).Trim();
                if (!string.IsNullOrEmpty(key)) map[key] = val;
            }
            return map;
        }

        private static string ExtractFromBundle(string bundle, string source)
        {
            var map = ParseBundle(bundle);
            if (map.TryGetValue(source, out var csv) && !string.IsNullOrWhiteSpace(csv)) return csv;
            return Data.ViiperTuningProperty.IdentityCsv;
        }

        private static string MergeIntoBundle(string bundle, string source, string csv)
        {
            var map = ParseBundle(bundle);
            map[source] = csv;
            // Drop pure-identity entries so the stored bundle stays minimal / empty when untuned.
            var parts = new List<string>();
            foreach (var kv in map)
            {
                if (kv.Value == Data.ViiperTuningProperty.IdentityCsv) continue;
                parts.Add(kv.Key + "=" + kv.Value);
            }
            return string.Join("|", parts);
        }

        private static string Norm(string s, string fallback)
        {
            s = s?.Trim().ToUpperInvariant();
            return (s == "X" || s == "Y" || s == "Z") ? s : fallback;
        }

        private static void SelectByTag(ComboBox combo, string tag)
        {
            if (combo == null) return;
            foreach (var obj in combo.Items)
            {
                if (obj is ComboBoxItem item && (item.Tag as string) == tag)
                {
                    combo.SelectedItem = item;
                    return;
                }
            }
        }

        // Reset only the current source's entries (gyro + accel) to identity.
        private void ViiperGyroTuningResetButton_Click(object sender, Windows.UI.Xaml.RoutedEventArgs e)
        {
            var source = CurrentGyroTuningSource();
            gyroTuningSuppressEvents = true;
            try
            {
                ApplyOneTuning(Data.ViiperTuningProperty.IdentityCsv,
                    ViiperGyroInvertXToggle, ViiperGyroInvertYToggle, ViiperGyroInvertZToggle,
                    ViiperGyroMatrixXComboBox, ViiperGyroMatrixYComboBox, ViiperGyroMatrixZComboBox);
                ApplyOneTuning(Data.ViiperTuningProperty.IdentityCsv,
                    ViiperAccelInvertXToggle, ViiperAccelInvertYToggle, ViiperAccelInvertZToggle,
                    ViiperAccelMatrixXComboBox, ViiperAccelMatrixYComboBox, ViiperAccelMatrixZComboBox);
            }
            finally
            {
                gyroTuningSuppressEvents = false;
            }
            viiperGyroTuning?.SetValue(MergeIntoBundle(viiperGyroTuning?.Value, source, Data.ViiperTuningProperty.IdentityCsv));
            viiperAccelTuning?.SetValue(MergeIntoBundle(viiperAccelTuning?.Value, source, Data.ViiperTuningProperty.IdentityCsv));
        }
    }
}
