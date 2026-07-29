using System;
using System.Collections.Generic;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Markup;
using Windows.UI.Xaml.Media;
using Windows.Storage;

namespace XboxGamingBar
{
    /// <summary>
    /// Nav bar customization (Customization card on the System tab): switches the nav
    /// pills between text labels and per-tab icons. Icon language follows the Fluent
    /// PathIcon-in-18x18-Viewbox pattern (inspired by ClawTweaks' nav bar), with MDL2
    /// controller glyphs for the device tabs. In icon mode the label moves into the
    /// pill's tooltip. Only the CONTENT swaps — all navigation logic keys on Tag.
    /// </summary>
    public sealed partial class GamingWidget
    {
        private bool navBarIconsSuppressEvents;

        // Tag -> display label (nav logic keys on Tag; Content is presentation only).
        private static readonly Dictionary<string, string> NavLabels = new Dictionary<string, string>
        {
            { "Quick", "Quick" },
            { "Performance", "Power" },
            { "Game", "Profiles" },
            { "AMD", "Display" },
            { "Scaling", "Scale" },
            { "Legion", "Legion" },
            { "GPD", "GPD" },
            { "System", "System" },
        };

        // Fluent-style vector paths (24x24 coordinate space, rendered in an 18x18 Viewbox).
        private static readonly Dictionary<string, string> NavIconPaths = new Dictionary<string, string>
        {
            // Home
            { "Quick", "M13.4508 2.53318C12.6128 1.82618 11.3872 1.82618 10.5492 2.53318L3.79916 8.22772C3.29241 8.65523 3 9.28447 3 9.94747V19.2526C3 20.2191 3.7835 21.0026 4.75 21.0026H7.75C8.7165 21.0026 9.5 20.2191 9.5 19.2526V15.25C9.5 14.5707 10.0418 14.018 10.7169 14.0004H13.2831C13.9582 14.018 14.5 14.5707 14.5 15.25V19.2526C14.5 20.2191 15.2835 21.0026 16.25 21.0026H19.25C20.2165 21.0026 21 20.2191 21 19.2526V9.94747C21 9.28447 20.7076 8.65523 20.2008 8.22772L13.4508 2.53318Z" },
            // Gauge / speedometer (Power tab = TDP & performance)
            { "Performance", "M12 2C17.5228 2 22 6.47715 22 12C22 15.1418 20.5497 17.9457 18.2861 19.7773C17.8568 20.1246 17.2273 20.0582 16.8799 19.6289C16.5326 19.1996 16.599 18.57 17.0283 18.2227C18.5979 16.9526 19.6732 15.1034 19.9355 13.001H18.4141C17.8903 13.001 17.5 12.5238 17.5 12C17.5 11.4772 17.8903 11.001 18.4131 11.001H19.9365C19.4856 7.38196 16.6189 4.51483 13 4.06348V5.58594C13 6.10919 12.5233 6.5 12 6.5C11.4767 6.5 11 6.10919 11 5.58594V4.06348C9.53942 4.24565 8.20216 4.82157 7.0957 5.68164L8.17188 6.75781C8.54177 7.12772 8.48123 7.74143 8.11133 8.11133C7.74143 8.48123 7.12772 8.54178 6.75781 8.17188L5.68164 7.0957C4.82139 8.2024 4.24551 9.54002 4.06348 11.001H5.58691C6.10969 11.001 6.5 11.4772 6.5 12C6.5 12.5238 6.10971 13.001 5.58594 13.001H4.06445C4.30786 14.9516 5.25085 16.6841 6.63965 17.9385L6.97168 18.2227L7.04785 18.291C7.40761 18.648 7.44571 19.2264 7.12012 19.6289C6.79436 20.0315 6.22013 20.1154 5.7959 19.8379L5.71387 19.7773L5.29883 19.4229C3.27439 17.5941 2 14.9455 2 12C2 6.47715 6.47715 2 12 2ZM15.9512 6.64941C16.1818 6.45621 16.5206 6.44956 16.7588 6.63379C16.997 6.81815 17.0688 7.14212 16.9297 7.40625L16.7979 7.65527C16.7141 7.81375 16.5937 8.04065 16.4482 8.31445C16.1573 8.86231 15.7648 9.59907 15.3584 10.3525C14.9523 11.1055 14.5311 11.8781 14.1836 12.4971C14.0101 12.806 13.853 13.0797 13.7246 13.2949C13.6053 13.495 13.4878 13.6839 13.4004 13.792C12.7474 14.5995 11.549 14.7364 10.7236 14.0977C9.89824 13.4588 9.75813 12.286 10.4111 11.4785C10.4985 11.3704 10.6594 11.2154 10.8311 11.0557C11.0157 10.8838 11.2519 10.671 11.5195 10.4346C12.0553 9.96123 12.7272 9.38319 13.3828 8.82324C14.0389 8.26288 14.6811 7.71951 15.1592 7.31641C15.3981 7.11493 15.596 6.94825 15.7344 6.83203L15.9512 6.64941Z" },
            // Save / profiles
            { "Game", "M15,9H5V5H15M12,19A3,3 0 0,1 9,16A3,3 0 0,1 12,13A3,3 0 0,1 15,16A3,3 0 0,1 12,19M17,3H5C3.89,3 3,3.9 3,5V19A2,2 0 0,0 5,21H19A2,2 0 0,0 21,19V7L17,3Z" },
            // Monitor
            { "AMD", "M6.75 22.0004C6.33579 22.0004 6 21.6647 6 21.2504C6 20.8707 6.28215 20.557 6.64823 20.5073L6.75 20.5004L8.499 20.5V18.002L4.25 18.0023C3.05914 18.0023 2.08436 17.0771 2.00519 15.9063L2 15.7523V5.25C2 4.05914 2.92516 3.08436 4.09595 3.00519L4.25 3H19.7488C20.9397 3 21.9145 3.92516 21.9936 5.09595L21.9988 5.25V15.7523C21.9988 16.9431 21.0737 17.9179 19.9029 17.9971L19.7488 18.0023L15.499 18.002V20.5L17.25 20.5004C17.6642 20.5004 18 20.8362 18 21.2504C18 21.6301 17.7178 21.9439 17.3518 21.9936L17.25 22.0004H6.75ZM13.998 18.002H9.998L9.999 20.5004H13.999L13.998 18.002Z" },
            // Resize brackets / scaling
            { "Scaling", "M10.25 3H6.25C4.45507 3 3 4.45507 3 6.25V8.25C3 8.66421 3.33579 9 3.75 9C4.16421 9 4.5 8.66421 4.5 8.25V6.25C4.5 5.2835 5.2835 4.5 6.25 4.5H10.25C10.6642 4.5 11 4.16421 11 3.75C11 3.33579 10.6642 3 10.25 3ZM10.75 21C12.5449 21 14 19.5449 14 17.75V13.25C14 11.4551 12.5449 10 10.75 10H6.25C4.45507 10 3 11.4551 3 13.25V17.75C3 19.5449 4.45507 21 6.25 21H10.75ZM15.75 21C15.3358 21 15 20.6642 15 20.25C15 19.8358 15.3358 19.5 15.75 19.5H17.75C18.7165 19.5 19.5 18.7165 19.5 17.75V13.75C19.5 13.3358 19.8358 13 20.25 13C20.6642 13 21 13.3358 21 13.75V17.75C21 19.5449 19.5449 21 17.75 21H15.75ZM21 10.25V6.25C21 4.45507 19.5449 3 17.75 3H13.75C13.3358 3 13 3.33579 13 3.75C13 4.16421 13.3358 4.5 13.75 4.5H17.75C18.7165 4.5 19.5 5.2835 19.5 6.25V10.25C19.5 10.6642 19.8358 11 20.25 11C20.6642 11 21 10.6642 21 10.25Z" },
            // Settings gear
            { "System", "M12.0122 2.25C12.7462 2.25846 13.4773 2.34326 14.1937 2.50304C14.5064 2.57279 14.7403 2.83351 14.7758 3.15196L14.946 4.67881C15.0231 5.37986 15.615 5.91084 16.3206 5.91158C16.5103 5.91188 16.6979 5.87238 16.8732 5.79483L18.2738 5.17956C18.5651 5.05159 18.9055 5.12136 19.1229 5.35362C20.1351 6.43464 20.8889 7.73115 21.3277 9.14558C21.4223 9.45058 21.3134 9.78203 21.0564 9.9715L19.8149 10.8866C19.4607 11.1468 19.2516 11.56 19.2516 11.9995C19.2516 12.4389 19.4607 12.8521 19.8157 13.1129L21.0582 14.0283C21.3153 14.2177 21.4243 14.5492 21.3297 14.8543C20.8911 16.2685 20.1377 17.5649 19.1261 18.6461C18.9089 18.8783 18.5688 18.9483 18.2775 18.8206L16.8712 18.2045C16.4688 18.0284 16.0068 18.0542 15.6265 18.274C15.2463 18.4937 14.9933 18.8812 14.945 19.3177L14.7759 20.8444C14.741 21.1592 14.5122 21.4182 14.204 21.4915C12.7556 21.8361 11.2465 21.8361 9.79803 21.4915C9.48991 21.4182 9.26105 21.1592 9.22618 20.8444L9.05736 19.32C9.00777 18.8843 8.75434 18.498 8.37442 18.279C7.99451 18.06 7.5332 18.0343 7.1322 18.2094L5.72557 18.8256C5.43422 18.9533 5.09403 18.8833 4.87678 18.6509C3.86462 17.5685 3.11119 16.2705 2.6732 14.8548C2.57886 14.5499 2.68786 14.2186 2.94485 14.0293L4.18818 13.1133C4.54232 12.8531 4.75147 12.4399 4.75147 12.0005C4.75147 11.561 4.54232 11.1478 4.18771 10.8873L2.94516 9.97285C2.6878 9.78345 2.5787 9.45178 2.67337 9.14658C3.11212 7.73215 3.86594 6.43564 4.87813 5.35462C5.09559 5.12236 5.43594 5.05259 5.72724 5.18056L7.12762 5.79572C7.53056 5.97256 7.9938 5.94585 8.37577 5.72269C8.75609 5.50209 9.00929 5.11422 9.05817 4.67764L9.22824 3.15196C9.26376 2.83335 9.49786 2.57254 9.8108 2.50294C10.5281 2.34342 11.26 2.25865 12.0122 2.25ZM11.9997 8.99995C10.3428 8.99995 8.9997 10.3431 8.9997 12C8.9997 13.6568 10.3428 15 11.9997 15C13.6565 15 14.9997 13.6568 14.9997 12C14.9997 10.3431 13.6565 8.99995 11.9997 8.99995Z" },
        };

        // Device tabs use MDL2 controller glyphs (no good Fluent path equivalent).
        private static readonly Dictionary<string, string> NavIconGlyphs = new Dictionary<string, string>
        {
            { "Legion", "\uE7FC" }, // Game (controller outline)
            { "GPD", "\uE7EF" },    // alternate controller glyph (matches ClawTweaks' GPD tab)
        };

        internal void NavBarIconsToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (navBarIconsSuppressEvents) return;
            try
            {
                bool icons = NavBarIconsToggle?.IsOn == true;
                ApplicationData.Current.LocalSettings.Values["NavBarIcons"] = icons;
                ApplyNavBarStyle(icons);
                Logger.Info($"Nav bar style -> {(icons ? "icons" : "text")}");
            }
            catch (Exception ex)
            {
                Logger.Warn($"NavBarIconsToggle_Toggled failed: {ex.Message}");
            }
        }

        private void LoadNavBarIconsSetting()
        {
            try
            {
                bool icons = ApplicationData.Current.LocalSettings.Values.TryGetValue("NavBarIcons", out var v) && v is bool b && b;
                navBarIconsSuppressEvents = true;
                try { if (NavBarIconsToggle != null) NavBarIconsToggle.IsOn = icons; }
                finally { navBarIconsSuppressEvents = false; }
                if (icons) ApplyNavBarStyle(true); // text is the XAML default — only apply when icons
            }
            catch (Exception ex)
            {
                Logger.Warn($"LoadNavBarIconsSetting failed: {ex.Message}");
            }
        }

        private void ApplyNavBarStyle(bool icons)
        {
            foreach (var child in MainNavPanel.Children)
            {
                if (!(child is RadioButton pill) || !(pill.Tag is string tag)) continue;
                if (!NavLabels.TryGetValue(tag, out var label)) label = tag;
                if (icons)
                {
                    pill.Content = BuildNavIcon(tag);
                    ToolTipService.SetToolTip(pill, label);
                    // Icon pills are much narrower than text — spread them out so the
                    // strip doesn't crowd into the center.
                    pill.Margin = new Thickness(5, 0, 5, 0);
                }
                else
                {
                    pill.Content = label;
                    ToolTipService.SetToolTip(pill, null);
                    pill.Margin = new Thickness(0);
                }
            }
        }

        private static UIElement BuildNavIcon(string tag)
        {
            // Bottom margin lifts the glyph clear of the checked-state underline
            // (3px bar + 2px margin at the pill's bottom edge) — without it the
            // underline clipped into the lower part of several icons.
            var underlineClearance = new Thickness(0, 0, 0, 4);
            if (NavIconGlyphs.TryGetValue(tag, out var glyph))
            {
                return new FontIcon
                {
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    Glyph = glyph,
                    FontSize = 17,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = underlineClearance,
                };
            }
            if (NavIconPaths.TryGetValue(tag, out var data))
            {
                var icon = new PathIcon
                {
                    Data = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), data),
                    // Pin the layout box to the 24x24 design canvas: PathIcon's natural
                    // size is origin->geometry-right/bottom (NOT the canvas), so ink with
                    // a left/top inset sat off-center in the Viewbox by half the inset —
                    // visibly misaligned against the selection underline for some icons.
                    Width = 24,
                    Height = 24,
                };
                return new Viewbox
                {
                    Width = 18,
                    Height = 18,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = underlineClearance,
                    Child = icon,
                };
            }
            // Unknown tag: fall back to text so the pill is never empty.
            return new TextBlock { Text = tag, VerticalAlignment = VerticalAlignment.Center };
        }
    }
}
