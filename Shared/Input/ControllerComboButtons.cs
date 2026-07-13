using System;
using System.Collections.Generic;
using System.Text;

namespace Shared.Input
{
    /// <summary>
    /// Canonical controller-button bit vocabulary shared by the widget's combo picker and
    /// the helper's ControllerHotkeyMonitor. A tile "controller hotkey" is stored as the
    /// decimal string of a uint bitmask built by OR-ing these bits.
    ///
    /// Standard buttons use their native XInput wButtons values so the helper can test them
    /// directly against XInput state. Legion back paddles (Y1-Y3 / M1-M3) live in high bits;
    /// the helper feeds those in from LegionButtonMonitor's press-edge stream (they are not
    /// visible to XInput). Values are stable and persisted - do not renumber.
    /// </summary>
    public static class ControllerComboButtons
    {
        // Standard buttons (native XInput bit values)
        public const uint DpadUp        = 0x0001;
        public const uint DpadDown      = 0x0002;
        public const uint DpadLeft      = 0x0004;
        public const uint DpadRight     = 0x0008;
        public const uint Start         = 0x0010; // Menu (three lines)
        public const uint Back          = 0x0020; // View (two squares)
        public const uint LeftThumb     = 0x0040;
        public const uint RightThumb    = 0x0080;
        public const uint LeftShoulder  = 0x0100;
        public const uint RightShoulder = 0x0200;
        public const uint A             = 0x1000;
        public const uint B             = 0x2000;
        public const uint X             = 0x4000;
        public const uint Y             = 0x8000;

        // Legion back paddles (high bits, fed from LegionButtonMonitor.ButtonEdge)
        public const uint LegionY1 = 0x10000;
        public const uint LegionY2 = 0x20000;
        public const uint LegionY3 = 0x40000;
        public const uint LegionM1 = 0x80000;
        public const uint LegionM2 = 0x100000;
        public const uint LegionM3 = 0x200000;

        public struct ComboButton
        {
            public string Label;
            public uint Bit;
            public bool IsLegionPaddle;
            public ComboButton(string label, uint bit, bool paddle = false) { Label = label; Bit = bit; IsLegionPaddle = paddle; }
        }

        /// <summary>All selectable buttons, in picker display order. Standard first, paddles last.</summary>
        public static readonly ComboButton[] All = new[]
        {
            new ComboButton("View",    Back),
            new ComboButton("Menu",    Start),
            new ComboButton("A",       A),
            new ComboButton("B",       B),
            new ComboButton("X",       X),
            new ComboButton("Y",       Y),
            new ComboButton("LB",      LeftShoulder),
            new ComboButton("RB",      RightShoulder),
            new ComboButton("LS",      LeftThumb),
            new ComboButton("RS",      RightThumb),
            new ComboButton("D-Up",    DpadUp),
            new ComboButton("D-Down",  DpadDown),
            new ComboButton("D-Left",  DpadLeft),
            new ComboButton("D-Right", DpadRight),
            new ComboButton("Y1", LegionY1, true),
            new ComboButton("Y2", LegionY2, true),
            new ComboButton("Y3", LegionY3, true),
            new ComboButton("M1", LegionM1, true),
            new ComboButton("M2", LegionM2, true),
            new ComboButton("M3", LegionM3, true),
        };

        /// <summary>Human-readable "Menu+A" style label for a mask, or "" if none.</summary>
        public static string MaskToString(uint mask)
        {
            if (mask == 0) return "";
            var parts = new List<string>();
            foreach (var b in All)
                if ((mask & b.Bit) == b.Bit) parts.Add(b.Label);
            return string.Join("+", parts);
        }

        /// <summary>Count of set bits in the mask (combo requires >= 2).</summary>
        public static int BitCount(uint mask)
        {
            int c = 0;
            while (mask != 0) { mask &= (mask - 1); c++; }
            return c;
        }
    }
}
