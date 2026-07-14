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

        // Last tag the user has actually committed to. The Checked guard reverts back
        // to this tag when focus restoration (or any code path that didn't pre-arm
        // pendingUserNavTag) tries to change the tab. Default matches Loaded's
        // initial QuickNavItem.IsChecked=true.
        private string lastUserNavTag = "Quick";

        // Pre-arm slot. Set by any code path that legitimately wants the next
        // NavRadioButton_Checked to apply — user input handlers on each nav
        // RadioButton, LT/RT trigger nav, programmatic IsChecked from code like
        // QuickDriverUpdatesTile_Click, and the Loaded initialization. Consumed
        // (cleared) by the guard on the first Checked event that arrives. State-
        // based instead of time-window-based, so there's no possibility of one
        // user click bleeding into accepting subsequent focus-driven Checked
        // events.
        private string pendingUserNavTag;

        /// <summary>
        /// Arm the drift guard to accept the next NavRadioButton_Checked event whose
        /// tag matches <paramref name="tag"/>. Call from any code path that legitimately
        /// changes the active tab. For PointerPressed/KeyDown handlers on nav
        /// RadioButtons we use the RadioButton's own Tag at handler-time. For
        /// programmatic IsChecked sets we pass the destination tag explicitly.
        /// </summary>
        internal void ArmUserNavIntent(string tag)
        {
            pendingUserNavTag = tag;
        }

        /// <summary>
        /// Wired once at widget init. Adds Pointer/Tap/KeyDown listeners on every nav
        /// RadioButton via AddHandler(handledEventsToo: true) — UWP's RadioButton marks
        /// these events as Handled internally when processing the click, so a normal
        /// `+=` subscription never fires for user clicks. handledEventsToo bypasses
        /// that.
        /// </summary>
        internal void AttachNavInteractionTracking()
        {
            foreach (var child in MainNavPanel.Children)
            {
                if (child is RadioButton rb)
                {
                    // PointerPressed: user clicked/tapped this nav button. Pre-arm
                    // BEFORE the Checked event fires (RadioButton's click
                    // → IsChecked=true → Checked is dispatched after PointerPressed).
                    rb.AddHandler(UIElement.PointerPressedEvent,
                        new PointerEventHandler((s, e) =>
                        {
                            if (s is RadioButton r) ArmUserNavIntent(r.Tag as string);
                        }),
                        handledEventsToo: true);

                    // KeyDown: Space / Enter / Gamepad A explicitly check the
                    // currently-focused RadioButton. Arrow keys / D-pad inside the
                    // group are how UWP's selection-follows-focus moves the check
                    // to the next sibling — user-driven, so we want them accepted.
                    rb.AddHandler(UIElement.KeyDownEvent,
                        new KeyEventHandler(NavRadio_KeyDown_ArmIntent),
                        handledEventsToo: true);

                    // Tapped fires after PointerReleased on a tap gesture. Pre-arm
                    // here too as a belt-and-suspenders against ordering quirks.
                    rb.Tapped += (s, e) =>
                    {
                        if (s is RadioButton r) ArmUserNavIntent(r.Tag as string);
                    };
                }
            }
        }

        private void NavRadio_KeyDown_ArmIntent(object sender, KeyRoutedEventArgs e)
        {
            switch (e.Key)
            {
                case VirtualKey.Space:
                case VirtualKey.Enter:
                case VirtualKey.GamepadA:
                    // These check the currently-focused nav RadioButton — use its
                    // own Tag as the pre-armed destination.
                    if (sender is RadioButton focused)
                    {
                        ArmUserNavIntent(focused.Tag as string);
                        // Switch to the FOCUSED tab synchronously here. Relying on the
                        // framework's default A->check can land after the deferred focus-into
                        // below, which dropped the user into the *previous* (still-active) tab
                        // instead of the one they pressed A on.
                        if (focused.IsChecked != true) focused.IsChecked = true;
                    }
                    // Committing to a tab with A/Enter drops focus straight into the tab's
                    // content so the user doesn't have to D-pad down from the tab strip.
                    // Deferred to the key RELEASE (PreviewKeyUp): moving focus during the
                    // press meant the key-up half of the same A press landed on the newly
                    // focused control and activated it (e.g. flipping the first toggle on
                    // the Profiles tab).
                    focusIntoTabOnKeyRelease = true;
                    break;
                case VirtualKey.Left:
                case VirtualKey.Right:
                case VirtualKey.Up:
                case VirtualKey.Down:
                case VirtualKey.GamepadDPadLeft:
                case VirtualKey.GamepadDPadRight:
                case VirtualKey.GamepadDPadUp:
                case VirtualKey.GamepadDPadDown:
                case VirtualKey.GamepadLeftThumbstickLeft:
                case VirtualKey.GamepadLeftThumbstickRight:
                case VirtualKey.GamepadLeftThumbstickUp:
                case VirtualKey.GamepadLeftThumbstickDown:
                    // Arrow/D-pad navigation inside the RadioButton group moves
                    // focus to the previous/next sibling. We don't know which way
                    // the framework will move focus from here, so pre-arm with a
                    // wildcard that matches any tag — guard will accept whichever
                    // sibling becomes Checked next.
                    pendingUserNavTag = "*";
                    break;
            }
        }

        private void NavRadioButton_Checked(object sender, RoutedEventArgs e)
        {
            // A tab switch by any means (touch, click, LT/RT) invalidates a pending
            // focus-into-tab arm from an A-press whose release we never saw. The
            // legitimate A-press path is unaffected: the synchronous IsChecked set in
            // NavRadio_KeyDown_ArmIntent runs this handler BEFORE the flag is armed.
            focusIntoTabOnKeyRelease = false;
            if (sender is RadioButton selectedItem)
            {
                string tag = selectedItem.Tag?.ToString() ?? "";

                // Pre-arm consume. A user-driven Checked is paired with a recent
                // ArmUserNavIntent call — either via the per-button input handlers,
                // LT/RT trigger nav, or programmatic IsChecked sites that set the
                // pending tag explicitly. The wildcard "*" is set by arrow/D-pad
                // arming where we don't know the target yet. Anything else is
                // either a re-fire of the already-current tag (no-op) or focus
                // restoration drift (revert).
                bool armed = (pendingUserNavTag == tag) || (pendingUserNavTag == "*");
                pendingUserNavTag = null;
                if (!armed && tag != lastUserNavTag)
                {
                    Logger.Info($"Nav drift suppressed: focus-driven Checked='{tag}' (lastUserTag='{lastUserNavTag}', no pre-armed intent) — reverting");
                    var revert = MainNavPanel.Children.OfType<RadioButton>()
                                              .FirstOrDefault(rb => string.Equals((rb.Tag as string) ?? string.Empty, lastUserNavTag, StringComparison.Ordinal));
                    if (revert != null && revert.IsChecked != true)
                    {
                        // Pre-arm for the revert so the resulting Checked event
                        // passes the guard cleanly instead of being treated as a
                        // second drift.
                        ArmUserNavIntent(lastUserNavTag);
                        revert.IsChecked = true;
                    }
                    return;
                }
                lastUserNavTag = tag;

                // Hide all sections
                QuickSettingsScrollViewer.Visibility = Visibility.Collapsed;
                PerformanceScrollViewer.Visibility = Visibility.Collapsed;
                GameScrollViewer.Visibility = Visibility.Collapsed;
                AMDScrollViewer.Visibility = Visibility.Collapsed;
                ScalingScrollViewer.Visibility = Visibility.Collapsed;
                LegionScrollViewer.Visibility = Visibility.Collapsed;
                GPDScrollViewer.Visibility = Visibility.Collapsed;
                SystemScrollViewer.Visibility = Visibility.Collapsed;

                // Stop fan curve updates when leaving Legion tab (will be re-enabled if Legion is selected)
                legionFanCurveVisible?.SetVisible(false);

                // Stop DAService status polling when leaving Legion tab
                daServiceStatusTimer?.Stop();

                // Show selected section and scroll to top
                switch (tag)
                {
                    case "Quick":
                        QuickSettingsScrollViewer.Visibility = Visibility.Visible;
                        QuickSettingsScrollViewer.ChangeView(null, 0, null, true);
                        UpdateQuickSettingsTileStates();
                        break;
                    case "Performance":
                        PerformanceScrollViewer.Visibility = Visibility.Visible;
                        PerformanceScrollViewer.ChangeView(null, 0, null, true);
                        break;
                    case "Game":
                        GameScrollViewer.Visibility = Visibility.Visible;
                        GameScrollViewer.ChangeView(null, 0, null, true);
                        break;
                    case "AMD":
                        AMDScrollViewer.Visibility = Visibility.Visible;
                        AMDScrollViewer.ChangeView(null, 0, null, true);
                        break;
                    case "Scaling":
                        ScalingScrollViewer.Visibility = Visibility.Visible;
                        ScalingScrollViewer.ChangeView(null, 0, null, true);
                        UpdateLosslessScalingStatus();
                        break;
                    case "Legion":
                        LegionScrollViewer.Visibility = Visibility.Visible;
                        LegionScrollViewer.ChangeView(null, 0, null, true);
                        // Update fan curve visibility when switching to Legion tab
                        legionFanCurveVisible?.SetVisible(isFanCurveExpanded);
                        // Start DAService status polling when on Legion tab
                        if (daServiceStatusTimer != null)
                        {
                            UpdateDAServiceStatus(); // Immediate update
                            daServiceStatusTimer.Start();
                        }
                        // Force remap UI refresh when Legion tab becomes active.
                        RefreshLegionEnhancedRemapUi();
                        break;
                    case "GPD":
                        GPDScrollViewer.Visibility = Visibility.Visible;
                        GPDScrollViewer.ChangeView(null, 0, null, true);
                        break;
                    case "System":
                        SystemScrollViewer.Visibility = Visibility.Visible;
                        SystemScrollViewer.ChangeView(null, 0, null, true);
                        RequestControllerEmulationDriverStatus();
                        break;
                }

                // Re-apply theme to newly visible tab (StaticResources don't update dynamically)
                // Defer with delay to ensure visual tree is fully loaded
                if (currentThemeName != "Default")
                {
                    _ = ApplyThemeToCurrentTabAsync();
                }
            }
        }

        private async Task ApplyThemeToCurrentTabAsync()
        {
            // Wait for visual tree to fully load
            await Task.Delay(50);
            ApplyThemeToCurrentTab();
        }

        private void ApplyThemeToCurrentTab()
        {
            if (!WidgetThemes.TryGetValue(currentThemeName, out var theme)) return;

            var cardBgBrush = new SolidColorBrush(theme.CardBackground);
            var cardBorderBrush = new SolidColorBrush(theme.CardBorder);
            var accentBrush = GetThemeAccentBrush(theme); // Win11 follows the live Windows accent
            var textSecondaryBrush = new SolidColorBrush(theme.TextSecondary);

            // Apply to all scroll viewers (only visible ones will have loaded content)
            ApplyThemeToVisualTree(QuickSettingsScrollViewer, theme, cardBgBrush, cardBorderBrush, accentBrush, textSecondaryBrush);
            ApplyThemeToVisualTree(PerformanceScrollViewer, theme, cardBgBrush, cardBorderBrush, accentBrush, textSecondaryBrush);
            ApplyThemeToVisualTree(GameScrollViewer, theme, cardBgBrush, cardBorderBrush, accentBrush, textSecondaryBrush);
            ApplyThemeToVisualTree(AMDScrollViewer, theme, cardBgBrush, cardBorderBrush, accentBrush, textSecondaryBrush);
            ApplyThemeToVisualTree(ScalingScrollViewer, theme, cardBgBrush, cardBorderBrush, accentBrush, textSecondaryBrush);
            ApplyThemeToVisualTree(LegionScrollViewer, theme, cardBgBrush, cardBorderBrush, accentBrush, textSecondaryBrush);
            ApplyThemeToVisualTree(SystemScrollViewer, theme, cardBgBrush, cardBorderBrush, accentBrush, textSecondaryBrush);
        }

        // Trigger-press edge tracking for tab navigation. Holding LT/RT would otherwise
        // auto-repeat (WasKeyDown=true) and cycle tabs continuously. We require the user
        // to release the trigger before accepting another press, and also apply a small
        // minimum interval as a belt-and-suspenders debounce.
        private bool ltTriggerHeld;
        private bool rtTriggerHeld;

        // Set when A/Enter/Space commits to a tab on key-down; consumed on the matching
        // key-up to move focus into the tab's content. Focus must not move during the
        // press itself, or the release lands on (and activates) the newly focused control.
        private bool focusIntoTabOnKeyRelease;
        private DateTime lastTriggerNavigateUtc = DateTime.MinValue;
        private static readonly TimeSpan TriggerNavigateDebounce = TimeSpan.FromMilliseconds(150);

        private void GamingWidget_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            // Handle LT (Left Trigger) and RT (Right Trigger) for tab navigation.
            // Using PreviewKeyDown to intercept before ScrollViewer handles it.
            // Skip when the key is auto-repeating while still held — only the initial
            // press-edge should advance a tab. One press == one tab.
            if (e.Key == VirtualKey.GamepadLeftTrigger)
            {
                // While the VIIPER Sticks & Triggers live-preview panel is
                // open the user is pulling the triggers ON PURPOSE to test
                // their shaping curve — jumping to the previous tab would
                // make the section impossible to verify. Swallow the press
                // (still mark Handled so ScrollViewer doesn't scroll) and
                // let the helper-side telemetry drive the visualizer.
                if (!IsStickTriggerPreviewOpen
                    && !ltTriggerHeld && !e.KeyStatus.WasKeyDown
                    && (DateTime.UtcNow - lastTriggerNavigateUtc) >= TriggerNavigateDebounce)
                {
                    ltTriggerHeld = true;
                    lastTriggerNavigateUtc = DateTime.UtcNow;
                    NavigateToPreviousTab();
                }
                e.Handled = true;
                return;
            }
            else if (e.Key == VirtualKey.GamepadRightTrigger)
            {
                if (!IsStickTriggerPreviewOpen
                    && !rtTriggerHeld && !e.KeyStatus.WasKeyDown
                    && (DateTime.UtcNow - lastTriggerNavigateUtc) >= TriggerNavigateDebounce)
                {
                    rtTriggerHeld = true;
                    lastTriggerNavigateUtc = DateTime.UtcNow;
                    NavigateToNextTab();
                }
                e.Handled = true;
                return;
            }
            // From the nav strip, D-pad/left-stick down enters the tab's content at the
            // top — the first focusable control — instead of letting spatial navigation
            // pick whatever control happens to sit geometrically below the tab strip
            // (which landed mid-page). Also covers overflow menu items.
            else if (e.Key == VirtualKey.GamepadDPadDown || e.Key == VirtualKey.GamepadLeftThumbstickDown)
            {
                var focusedElement = FocusManager.GetFocusedElement() as FrameworkElement;
                if (focusedElement != null && IsInNavigationArea(focusedElement))
                {
                    // Mark as handled immediately to prevent default XY navigation
                    e.Handled = true;
                    FocusFirstControlInActiveTab();
                }
            }
        }

        private void GamingWidget_PreviewKeyUp(object sender, KeyRoutedEventArgs e)
        {
            // Clear the press-edge state so the next LT/RT press advances exactly one
            // tab. Without this, auto-repeat during a held trigger would cycle through
            // the entire tab strip.
            if (e.Key == VirtualKey.GamepadLeftTrigger)
            {
                ltTriggerHeld = false;
            }
            else if (e.Key == VirtualKey.GamepadRightTrigger)
            {
                rtTriggerHeld = false;
            }
            // Focus-into-tab armed by A/Enter/Space on a nav item: perform it on the
            // release so this key press can't also activate the control we land on.
            else if (e.Key == VirtualKey.GamepadA || e.Key == VirtualKey.Enter || e.Key == VirtualKey.Space)
            {
                if (focusIntoTabOnKeyRelease)
                {
                    // Consume the arm exactly once, and only act while focus is still in
                    // the nav strip. If the matching release never reached us (B pressed
                    // mid-press, a flyout stole focus, widget hidden), acting on a later
                    // unrelated release would yank focus into the tab AND swallow that
                    // control's activation via e.Handled.
                    focusIntoTabOnKeyRelease = false;
                    if (IsInNavigationArea(FocusManager.GetFocusedElement() as FrameworkElement))
                    {
                        FocusFirstControlInActiveTab();
                        e.Handled = true;
                    }
                }
            }
        }

        /// <summary>
        /// Forcibly clears any held LT/RT press-edge state. Called when the widget gains
        /// focus or when VIIPER/controller emulation toggles. HidHide CyclePort on the
        /// physical pad during emulation setup can leave the OS believing RT/LT is
        /// stuck-down (no KeyUp arrives because the device disappeared between events),
        /// which would otherwise leave tab nav wedged until the user gets a fresh KeyUp.
        /// Resetting here lets the very next physical press act as a clean press-edge.
        /// </summary>
        internal void ResetTriggerTabNavState()
        {
            if (ltTriggerHeld || rtTriggerHeld)
            {
                Logger.Info("Clearing stuck LT/RT tab-nav state (focus/emulation transition)");
            }
            ltTriggerHeld = false;
            rtTriggerHeld = false;
            lastTriggerNavigateUtc = DateTime.MinValue;
        }

        private bool IsInNavigationArea(FrameworkElement element)
        {
            // Check if any of our nav items has focus
            // This works regardless of whether the item is in the main bar or overflow menu
            if (QuickNavItem.FocusState != FocusState.Unfocused) return true;
            if (PerformanceNavItem.FocusState != FocusState.Unfocused) return true;
            if (ProfilesNavItem.FocusState != FocusState.Unfocused) return true;
            if (GraphicsNavItem.FocusState != FocusState.Unfocused) return true;
            if (ScalingNavItem.FocusState != FocusState.Unfocused) return true;
            if (LegionNavItem.FocusState != FocusState.Unfocused) return true;
            if (GPDNavItem.FocusState != FocusState.Unfocused) return true;
            if (SystemNavItem.FocusState != FocusState.Unfocused) return true;

            // Fallback: walk visual tree for other nav-related elements
            var current = element;
            while (current != null)
            {
                // Check if we're in the nav panel
                if (current == MainNavPanel)
                    return true;
                current = VisualTreeHelper.GetParent(current) as FrameworkElement;
            }
            return false;
        }

        private void NavigateToPreviousTab()
        {
            var visibleItems = GetVisibleNavigationItems();
            if (visibleItems.Count == 0) return;

            // Find currently checked item
            var currentItem = visibleItems.FirstOrDefault(rb => rb.IsChecked == true);
            int currentIndex = currentItem != null ? visibleItems.IndexOf(currentItem) : 0;

            // LT trigger nav is explicit user intent — pre-arm with the destination
            // tag so the resulting Checked event passes the guard.
            var target = currentIndex > 0 ? visibleItems[currentIndex - 1] : visibleItems[visibleItems.Count - 1];
            ArmUserNavIntent(target.Tag as string);
            target.IsChecked = true;
        }

        private void NavigateToNextTab()
        {
            var visibleItems = GetVisibleNavigationItems();
            if (visibleItems.Count == 0) return;

            // Find currently checked item
            var currentItem = visibleItems.FirstOrDefault(rb => rb.IsChecked == true);
            int currentIndex = currentItem != null ? visibleItems.IndexOf(currentItem) : 0;

            var target = currentIndex < visibleItems.Count - 1 ? visibleItems[currentIndex + 1] : visibleItems[0];
            ArmUserNavIntent(target.Tag as string);
            target.IsChecked = true;
        }

        private List<RadioButton> GetVisibleNavigationItems()
        {
            var visibleItems = new List<RadioButton>();
            foreach (var item in MainNavPanel.Children)
            {
                if (item is RadioButton radioButton && radioButton.Visibility == Visibility.Visible)
                {
                    visibleItems.Add(radioButton);
                }
            }
            return visibleItems;
        }

        /// <summary>
        /// Boundary shepherd for XY navigation, wired to the page's LosingFocus event.
        /// Keeps gamepad focus from escaping the active tab's content vertically:
        /// a Down move past the last control is cancelled (bottom end-stop, instead of
        /// focus vanishing onto some off-tab candidate), and an Up move out the top is
        /// redirected to the active tab item in the nav strip. All navigation inside
        /// the content stays with the system's XY algorithm.
        /// </summary>
        private void GamingWidget_LosingFocus(UIElement sender, LosingFocusEventArgs args)
        {
            try
            {
                if (args.Direction != FocusNavigationDirection.Up && args.Direction != FocusNavigationDirection.Down)
                {
                    return;
                }

                // Shepherd only gamepad/keyboard navigation. Pointer taps and
                // programmatic focus moves (e.g. a flyout opening below the bottom
                // row) must never be cancelled or redirected.
                if (args.InputDevice != FocusInputDeviceKind.GameController
                    && args.InputDevice != FocusInputDeviceKind.Keyboard)
                {
                    return;
                }

                var sv = GetActiveTabScrollViewer();
                if (sv == null || sv.Visibility != Visibility.Visible) return;

                if (!(args.OldFocusedElement is UIElement oldElement) || !IsDescendantOf(oldElement, sv))
                {
                    return;
                }

                if (args.NewFocusedElement is UIElement newElement && IsDescendantOf(newElement, sv))
                {
                    return; // normal move within the tab content
                }

                if (args.Direction == FocusNavigationDirection.Down)
                {
                    // Bottom of the tab: stay put.
                    args.TryCancel();
                }
                else
                {
                    // Top of the tab: land on the active tab item, not whatever nav
                    // button happens to be geometrically nearest.
                    var active = MainNavPanel.Children.OfType<RadioButton>()
                        .FirstOrDefault(rb => rb.IsChecked == true && rb.Visibility == Visibility.Visible);
                    if (active != null && !ReferenceEquals(args.NewFocusedElement, active))
                    {
                        args.TrySetNewFocusedElement(active);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"LosingFocus shepherd failed: {ex.Message}");
            }
        }

        private static bool IsDescendantOf(DependencyObject element, DependencyObject ancestor)
        {
            var current = element;
            while (current != null)
            {
                if (ReferenceEquals(current, ancestor)) return true;
                current = VisualTreeHelper.GetParent(current);
            }
            return false;
        }

        /// <summary>
        /// The ScrollViewer of the currently active tab (matches lastUserNavTag), or null.
        /// </summary>
        private ScrollViewer GetActiveTabScrollViewer()
        {
            switch (lastUserNavTag)
            {
                case "Quick": return QuickSettingsScrollViewer;
                case "Performance": return PerformanceScrollViewer;
                case "Game": return GameScrollViewer;
                case "AMD": return AMDScrollViewer;
                case "Scaling": return ScalingScrollViewer;
                case "Legion": return LegionScrollViewer;
                case "GPD": return GPDScrollViewer;
                case "System": return SystemScrollViewer;
                default: return null;
            }
        }

        /// <summary>
        /// Moves gamepad focus onto the first focusable control inside the active tab's content,
        /// so committing to a tab (A/Enter on the nav item) doesn't require a manual D-pad-down.
        /// No-op if the tab has no focusable content.
        /// </summary>
        internal void FocusFirstControlInActiveTab()
        {
            try
            {
                var sv = GetActiveTabScrollViewer();
                if (sv == null || sv.Visibility != Visibility.Visible) return;
                if (FocusManager.FindFirstFocusableElement(sv) is Control first)
                {
                    first.Focus(FocusState.Programmatic);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"FocusFirstControlInActiveTab failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Puts gamepad focus on the currently active tab in the nav strip, so the pad can
        /// navigate immediately when the widget opens without the user first touching the screen.
        /// Focuses (doesn't re-check) the already-selected tab, so it can't cause nav drift.
        /// </summary>
        internal void FocusActiveNavItem()
        {
            try
            {
                var active = MainNavPanel.Children.OfType<RadioButton>()
                    .FirstOrDefault(rb => rb.IsChecked == true && rb.Visibility == Visibility.Visible);
                active?.Focus(FocusState.Programmatic);
            }
            catch (Exception ex)
            {
                Logger.Warn($"FocusActiveNavItem failed: {ex.Message}");
            }
        }

    }
}
