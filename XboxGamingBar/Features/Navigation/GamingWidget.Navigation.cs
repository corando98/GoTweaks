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

        // --- Soft-press LT/RT tab cycling (analog poll) ---
        // The GamepadLeftTrigger/RightTrigger KEY events only synthesize near a FULL
        // pull, so trigger tab-cycling felt stiff. This poll reads the analog trigger
        // values and fires at a soft threshold with press-edge latching: exactly one
        // tab per pull (hold does NOT repeat), re-armed only after releasing below the
        // hysteresis threshold. It shares ltTriggerHeld/rtTriggerHeld with the key
        // handler, so a soft press that keeps going and crosses the OS key threshold
        // cannot double-cycle; the 150ms TriggerNavigateDebounce still applies on top.
        private DispatcherTimer analogTriggerPollTimer;
        private bool ltAnalogPressed;
        private bool rtAnalogPressed;
        private const double TriggerSoftPressThreshold = 0.20;   // fire here (soft press)
        private const double TriggerSoftReleaseThreshold = 0.10; // re-arm below this

        internal void StartAnalogTriggerPoll()
        {
            if (analogTriggerPollTimer != null) return;
            analogTriggerPollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
            analogTriggerPollTimer.Tick += AnalogTriggerPoll_Tick;
            analogTriggerPollTimer.Start();
        }

        /// <summary>
        /// Stops the analog LT/RT poll while the widget is hidden — no reason to touch
        /// Windows.Gaming.Input at all when the widget isn't on screen (and it reassures
        /// that the poll can't interact with anything while a game has the controller).
        /// Restarted by VisibleChanged when the widget reopens.
        /// </summary>
        internal void StopAnalogTriggerPoll()
        {
            if (analogTriggerPollTimer == null) return;
            analogTriggerPollTimer.Stop();
            analogTriggerPollTimer.Tick -= AnalogTriggerPoll_Tick;
            analogTriggerPollTimer = null;
            ltAnalogPressed = false;
            rtAnalogPressed = false;
        }

        private void AnalogTriggerPoll_Tick(object sender, object e)
        {
            try
            {
                // Only react while the widget is the foreground-activated window.
                // Windows.Gaming.Input is process-global — without this gate, a pinned
                // widget would cycle tabs while the user is PLAYING the game.
                var cw = Window.Current?.CoreWindow;
                if (cw == null || cw.ActivationMode != CoreWindowActivationMode.ActivatedInForeground)
                {
                    // Drop any half-tracked pull so a press that started in-game can't
                    // fire retroactively when the widget regains focus.
                    ltAnalogPressed = false;
                    rtAnalogPressed = false;
                    return;
                }

                var pads = Windows.Gaming.Input.Gamepad.Gamepads;
                if (pads.Count == 0) return;
                // Merge all pads (virtual VIIPER pad + physical can coexist; whichever
                // the user is holding wins).
                double lt = 0, rt = 0;
                foreach (var pad in pads)
                {
                    var r = pad.GetCurrentReading();
                    if (r.LeftTrigger > lt) lt = r.LeftTrigger;
                    if (r.RightTrigger > rt) rt = r.RightTrigger;
                }

                HandleAnalogTrigger(lt, ref ltAnalogPressed, ref ltTriggerHeld, NavigateToPreviousTab);
                HandleAnalogTrigger(rt, ref rtAnalogPressed, ref rtTriggerHeld, NavigateToNextTab);
            }
            catch
            {
                // Input-stack hiccups (device churn during emu toggles) are non-fatal —
                // the key-event path still works as the fallback.
            }
        }

        private void HandleAnalogTrigger(double value, ref bool analogPressed, ref bool navLatch, Action navigate)
        {
            if (!analogPressed && value >= TriggerSoftPressThreshold)
            {
                analogPressed = true;
                if (!IsStickTriggerPreviewOpen && !navLatch
                    && (DateTime.UtcNow - lastTriggerNavigateUtc) >= TriggerNavigateDebounce)
                {
                    navLatch = true; // consumed by this pull; the key handler skips too
                    lastTriggerNavigateUtc = DateTime.UtcNow;
                    navigate();
                }
            }
            else if (analogPressed && value < TriggerSoftReleaseThreshold)
            {
                analogPressed = false;
                navLatch = false; // fully released — the next pull may cycle again
            }
        }

        private void GamingWidget_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            // KEYBOARD engagement model for value controls. Gamepad D-pad passes over
            // Sliders/ComboBoxes because UWP focus engagement requires A before the
            // control consumes directional input — but keyboard arrows hit the control
            // DIRECTLY, so merely passing over a closed dropdown changed its selection
            // and vertical arrows on a slider changed its value (field feedback
            // 2026-07-29). Recreate engagement for keyboard: arrows on a CLOSED ComboBox
            // (all directions) and vertical arrows on a Slider move FOCUS instead; an
            // open dropdown keeps its arrows (that's the engaged state), and sliders
            // keep Left/Right for value adjust.
            if (e.Key == VirtualKey.Up || e.Key == VirtualKey.Down
                || e.Key == VirtualKey.Left || e.Key == VirtualKey.Right)
            {
                var focusedCtl = FocusManager.GetFocusedElement();
                bool vertical = e.Key == VirtualKey.Up || e.Key == VirtualKey.Down;
                bool intercept =
                    (focusedCtl is ComboBox cbCtl && !cbCtl.IsDropDownOpen)
                    || (focusedCtl is Slider && vertical);
                if (intercept)
                {
                    e.Handled = true;
                    var dir = e.Key == VirtualKey.Up ? FocusNavigationDirection.Up
                            : e.Key == VirtualKey.Down ? FocusNavigationDirection.Down
                            : e.Key == VirtualKey.Left ? FocusNavigationDirection.Left
                            : FocusNavigationDirection.Right;
                    // FindNextElement WITH a SearchRoot: the no-options overloads only
                    // consider candidates VISIBLE in the viewport, so the move failed
                    // whenever the next control was scrolled off-screen — Down looked
                    // stuck at dropdowns and Up fell through to the nav-bar fallback
                    // (field feedback 2026-07-29). A SearchRoot includes off-screen
                    // elements; we then scroll the target into view ourselves.
                    Control next = null;
                    try
                    {
                        var searchRoot = (DependencyObject)GetActiveTabScrollViewer() ?? this;
                        next = FocusManager.FindNextElement(dir, new FindNextElementOptions
                        {
                            SearchRoot = searchRoot,
                        }) as Control;
                    }
                    catch (Exception fex) { Logger.Debug($"FindNextElement({dir}) failed: {fex.Message}"); }

                    if (next != null && !(next is ScrollViewer))
                    {
                        next.Focus(FocusState.Keyboard);
                        next.StartBringIntoView();
                    }
                    else if (dir == FocusNavigationDirection.Up
                        && Windows.UI.Xaml.Media.VisualTreeHelper.GetOpenPopups(Window.Current).Count == 0)
                    {
                        // Top of the tab: nothing above (nav strip is XY-disabled) —
                        // return to the strip explicitly.
                        FocusActiveNavItem();
                    }
                    return;
                }
            }

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
            // VirtualKey.Down included: KEYBOARD down-arrow on a focused nav pill would
            // otherwise be handled by the RadioButton GROUP (up/down arrows cycle
            // prev/next within a GroupName — sideways motion). Route it into the tab
            // like the gamepad path instead. Content controls are unaffected: the
            // intercept only acts when focus is in the nav area / on a ScrollViewer.
            else if (e.Key == VirtualKey.GamepadDPadDown || e.Key == VirtualKey.GamepadLeftThumbstickDown || e.Key == VirtualKey.Down)
            {
                var focusedElement = FocusManager.GetFocusedElement() as FrameworkElement;
                Logger.Info($"DPadDown: focus on {(focusedElement == null ? "null" : focusedElement.GetType().Name + " '" + focusedElement.Name + "'")}, inNav={focusedElement != null && IsInNavigationArea(focusedElement)}");
                if (focusedElement != null && IsInNavigationArea(focusedElement))
                {
                    // Mark as handled immediately to prevent default XY navigation.
                    // The focus move itself is DEFERRED to the dispatcher: moving focus
                    // synchronously inside the D-pad KeyDown proved unreliable (focus
                    // silently vanished — field feedback 2026-07-28), while the A-press
                    // path, which moves focus on KeyUp, always worked. Deferring gives
                    // the same after-the-key-event timing.
                    e.Handled = true;
                    _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => FocusFirstControlInActiveTab());
                }
                else if (focusedElement is ScrollViewer)
                {
                    // Focus parked on a bare ScrollViewer (invisible, whole-viewport
                    // bounds): default XY "down" would measure from the viewport bottom
                    // and land on the LAST element of the page. Enter the tab at the
                    // top instead. (LosingFocus also redirects new parks; this covers
                    // focus that was already parked before the shepherd existed.)
                    e.Handled = true;
                    _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => FocusFirstControlInActiveTab());
                }
            }
            // KEYBOARD up-arrow handling:
            //  - On the nav strip: swallow it — the RadioButton group would otherwise
            //    cycle prev/next (sideways motion). Nothing sits above the strip.
            //  - In content at the TOP of the tab: the XY search finds nothing above
            //    (the strip is XY-disabled), so the press would die — return to the
            //    active nav pill instead. Guarded against open popups/flyouts.
            // Gamepad Up never reaches us (Game Bar owns it) — keyboard only.
            else if (e.Key == VirtualKey.Up)
            {
                if (IsInNavigationArea(FocusManager.GetFocusedElement() as FrameworkElement))
                {
                    e.Handled = true;
                }
                else
                {
                    // SearchRoot-based lookup (viewport-limited no-options search would
                    // report null for off-screen controls above and wrongly jump to the
                    // nav bar mid-page).
                    object above = null;
                    try
                    {
                        var searchRoot = (DependencyObject)GetActiveTabScrollViewer() ?? this;
                        above = FocusManager.FindNextElement(FocusNavigationDirection.Up, new FindNextElementOptions
                        {
                            SearchRoot = searchRoot,
                        });
                    }
                    catch (Exception fex) { Logger.Debug($"FindNextElement(Up) failed: {fex.Message}"); }
                    if (above == null
                        && Windows.UI.Xaml.Media.VisualTreeHelper.GetOpenPopups(Window.Current).Count == 0)
                    {
                        e.Handled = true;
                        FocusActiveNavItem();
                    }
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
            ltAnalogPressed = false;
            rtAnalogPressed = false;
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
            // Anchor focus on the new pill: switching tabs collapses the old content,
            // and if gamepad focus was inside it, focus died INVISIBLY — the user saw
            // the indicator vanish and the next D-pad move jumped somewhere arbitrary
            // (field feedback 2026-07-28). Keyboard state so the focus ring shows.
            target.Focus(FocusState.Keyboard);
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
            // Anchor focus on the new pill — see NavigateToPreviousTab for rationale.
            target.Focus(FocusState.Keyboard);
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
        /// <summary>
        /// Entry shepherd: when focus ENTERS the widget window from outside (Game Bar
        /// chrome hands focus over — e.g. D-pad Down or A on the widget icon in the home
        /// bar), OldFocusedElement is null and XAML's entry logic picks an arbitrary
        /// element (clearing the nav pill's anchored ring — "indicator lost until I press
        /// Up", field feedback 2026-07-28). Land every gamepad/keyboard entry on the
        /// active nav pill instead, so the widget always starts navigable and visible.
        /// </summary>
        // Armed when focus leaves our window for Game Bar chrome (Up to the widget icon)
        // or the window deactivates; the NEXT focus entry then re-anchors to the active
        // pill. Without this, the icon→widget→icon→widget round-trip broke: the second
        // entry got no Activated event, so Game Bar's focus placement landed invisibly.
        private bool pendingEntryAnchor;

        private void GamingWidget_GettingFocus(UIElement sender, GettingFocusEventArgs args)
        {
            try
            {
                if (args.InputDevice != FocusInputDeviceKind.GameController
                    && args.InputDevice != FocusInputDeviceKind.Keyboard)
                {
                    return;
                }

                bool isEntry = args.OldFocusedElement == null || pendingEntryAnchor;
                if (!isEntry) return; // in-window move
                pendingEntryAnchor = false;

                // Only steer entries that land OUTSIDE the nav strip. An entry landing on
                // any pill is already fine (selection follows focus) — redirecting it to
                // the checked pill caused sideways jumps and swallowed strip exits.
                if (args.NewFocusedElement is UIElement newEl && IsDescendantOf(newEl, MainNavPanel)) return;

                var active = MainNavPanel.Children.OfType<RadioButton>()
                    .FirstOrDefault(rb => rb.IsChecked == true && rb.Visibility == Visibility.Visible);
                if (active == null || ReferenceEquals(args.NewFocusedElement, active)) return;

                bool redirected = args.TrySetNewFocusedElement(active);
                Logger.Info($"Entry-shepherd: window entry was landing on {args.NewFocusedElement?.GetType().Name}; redirect to nav pill {(redirected ? "ok" : "REFUSED")}");
                if (!redirected)
                {
                    // Some system-driven entry moves (Game Bar focusing the Page root)
                    // cannot be redirected in-event — TrySetNewFocusedElement is refused
                    // (field logs 2026-07-29). Anchor right AFTER the move completes
                    // instead, so the entry still ends on the nav pill.
                    _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => FocusActiveNavItem());
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"GettingFocus entry shepherd failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Selection follows focus on the nav strip: focusing a pill (D-pad L/R, entry
        /// anchor, shepherd redirects) CHECKS it, switching the tab immediately. Without
        /// this, focus could sit on an unchecked pill while every shepherd redirect
        /// (down-into-tab, up-to-active-pill, entry anchor) targeted the CHECKED tab —
        /// D-pad up/down appeared to jump left/right toward the old tab and exits got
        /// yanked back (field feedback 2026-07-28). Wired to each pill's GotFocus.
        /// </summary>
        /// <summary>
        /// Wires each visible pill's explicit XYFocusLeft/XYFocusRight to its sibling
        /// (self-loop at the ends). D-pad key events never reach the widget (Game Bar
        /// drives XY focus directly) and the LosingFocus L/R redirect only fires when the
        /// XY engine finds SOME candidate — with no focusable element beside the strip it
        /// finds none, no event fires, and L/R silently dies (field regression
        /// 2026-07-29). Explicit XYFocus targets override the spatial search entirely,
        /// making pill-to-pill L/R deterministic. Recomputed on every pill focus so
        /// device-tab visibility changes (Legion/GPD) are always reflected.
        /// </summary>
        /// <summary>
        /// Points a pill's explicit XYFocusDown at the active tab's first focusable
        /// control (null-safe: leaves the previous target when the content isn't
        /// realized yet — the deferred re-resolve in NavPill_GotFocus catches up).
        /// </summary>
        private void SetPillXYFocusDown(RadioButton pill)
        {
            try
            {
                var sv = GetActiveTabScrollViewer();
                var first = sv != null ? FindFirstRealControl(sv) : null;
                if (first != null)
                {
                    pill.XYFocusDown = first;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"SetPillXYFocusDown failed: {ex.Message}");
            }
        }

        private void UpdateNavPillXYFocus()
        {
            try
            {
                var items = GetVisibleNavigationItems();
                for (int i = 0; i < items.Count; i++)
                {
                    items[i].XYFocusLeft = items[i == 0 ? 0 : i - 1];
                    items[i].XYFocusRight = items[i == items.Count - 1 ? items.Count - 1 : i + 1];
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"UpdateNavPillXYFocus failed: {ex.Message}");
            }
        }

        internal void NavPill_GotFocus(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is RadioButton pill)
                {
                    UpdateNavPillXYFocus();
                    if (pill.IsChecked != true)
                    {
                        ArmUserNavIntent(pill.Tag as string);
                        pill.IsChecked = true;
                    }
                    // Explicit D-pad DOWN target: the narrow icon pills often have no
                    // spatial candidate below, so the XY engine generates NO focus event
                    // and the press dies (same event-scarcity class the L/R XYFocus
                    // wiring fixed). Point Down straight at the active tab's first real
                    // control; re-resolved deferred too, in case the tab's content only
                    // realizes after the switch.
                    SetPillXYFocusDown(pill);
                    _ = Dispatcher.RunAsync(CoreDispatcherPriority.Low, () => SetPillXYFocusDown(pill));
                    // Re-assert the Focused visual state AFTER the Checked transition:
                    // both storyboards animate the label color (Checked=accent,
                    // Focused=dark-on-light) and whichever applies LAST wins — without
                    // this the focused pill showed accent-on-light (hard to read,
                    // field feedback 2026-07-28). The Unfocused hop forces the Focused
                    // storyboard to re-run.
                    VisualStateManager.GoToState(pill, "Unfocused", false);
                    VisualStateManager.GoToState(pill, "Focused", false);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"NavPill_GotFocus failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Entry anchor: fires when Game Bar hands INPUT to our window (D-pad Down or A
        /// on the widget icon activates it). The pill ring shown before this point is
        /// only logical focus in an inactive window — the first real input transfer used
        /// to land focus on an arbitrary element and clear it. Deferred so it runs after
        /// whatever focus placement accompanies the activation.
        /// </summary>
        private void CoreWindow_Activated(Windows.UI.Core.CoreWindow sender, Windows.UI.Core.WindowActivatedEventArgs args)
        {
            try
            {
                if (args.WindowActivationState == Windows.UI.Core.CoreWindowActivationState.Deactivated)
                {
                    // Input went to Game Bar chrome (or elsewhere) — anchor on return.
                    pendingEntryAnchor = true;
                    return;
                }
                Logger.Info($"Window activated ({args.WindowActivationState}); anchoring focus to active nav item");
                _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => FocusActiveNavItem());
            }
            catch (Exception ex)
            {
                Logger.Warn($"CoreWindow_Activated anchor failed: {ex.Message}");
            }
        }

        private void GamingWidget_LosingFocus(UIElement sender, LosingFocusEventArgs args)
        {
            try
            {
                // Shepherd only gamepad/keyboard navigation. Pointer taps and
                // programmatic focus moves (e.g. a flyout opening below the bottom
                // row) must never be cancelled or redirected.
                if (args.InputDevice != FocusInputDeviceKind.GameController
                    && args.InputDevice != FocusInputDeviceKind.Keyboard)
                {
                    return;
                }

                // Focus leaving the window entirely (Up from the strip to Game Bar's
                // widget icon): let it go, but arm the re-entry anchor so the next
                // entry lands on the active pill instead of wherever Game Bar picks.
                if (args.NewFocusedElement == null)
                {
                    pendingEntryAnchor = true;
                    Logger.Info("LosingFocus: focus leaving window; armed re-entry anchor");
                    return;
                }

                // Never let XY focus PARK on a bare ScrollViewer: it draws no focus
                // visual (user sees focus "disappear") and its whole-viewport bounds
                // wreck the next spatial move — "down" measures from the viewport
                // bottom and lands on the LAST element of the page (field feedback
                // 2026-07-28). Redirect to the active tab's first real control.
                if (args.NewFocusedElement is ScrollViewer)
                {
                    var tabSv = GetActiveTabScrollViewer();
                    var firstCtl = tabSv != null ? FindFirstRealControl(tabSv) : null;
                    if (firstCtl != null)
                    {
                        bool ok = args.TrySetNewFocusedElement(firstCtl);
                        Logger.Info($"LosingFocus: SV-park redirect to {firstCtl.GetType().Name} '{firstCtl.Name}' {(ok ? "ok" : "REFUSED")}");
                    }
                    else
                    {
                        bool ok = args.TryCancel();
                        Logger.Info($"LosingFocus: SV-park cancel {(ok ? "ok" : "REFUSED")}");
                    }
                    return;
                }

                // Nav-strip departure shepherd. IMPORTANT: in the Game Bar hosting
                // environment D-pad key events never reach our PreviewKeyDown (verified
                // via logging 2026-07-28 — Game Bar drives XY focus directly), so THIS
                // focus-event hook is the only reliable place to steer strip exits:
                //  - Down: enter the active tab at its FIRST control (spatial nav would
                //    pick whatever sits geometrically below — mid-page or worse).
                //  - Left/Right past the strip edge: stay put (LT/RT cycle tabs).
                if (args.OldFocusedElement is UIElement oldNav && IsDescendantOf(oldNav, MainNavPanel)
                    && !(args.NewFocusedElement is UIElement newNav && IsDescendantOf(newNav, MainNavPanel)))
                {
                    if (args.Direction == FocusNavigationDirection.Down)
                    {
                        var tabSvDown = GetActiveTabScrollViewer();
                        var firstDown = tabSvDown != null ? FindFirstRealControl(tabSvDown) : null;
                        if (firstDown != null && !ReferenceEquals(args.NewFocusedElement, firstDown))
                        {
                            bool ok = args.TrySetNewFocusedElement(firstDown);
                            Logger.Info($"LosingFocus: nav-down redirect to {firstDown.GetType().Name} '{firstDown.Name}' {(ok ? "ok" : "REFUSED")}");
                        }
                        return;
                    }
                    if (args.Direction == FocusNavigationDirection.Left || args.Direction == FocusNavigationDirection.Right)
                    {
                        // Pill-to-pill D-pad navigation. MainNavPanel has
                        // XYFocusKeyboardNavigation="Disabled", so the XY engine NEVER
                        // proposes a sibling pill — every L/R from a pill computes an
                        // outside-the-strip destination. A plain cancel here made L/R
                        // feel dead (field feedback 2026-07-28); redirect to the
                        // adjacent visible pill instead, and stay put at the ends.
                        var items = GetVisibleNavigationItems();
                        int idx = items.FindIndex(rb => ReferenceEquals(rb, oldNav) || IsDescendantOf(oldNav, rb));
                        int step = args.Direction == FocusNavigationDirection.Right ? 1 : -1;
                        int nextIdx = idx + step;
                        if (idx >= 0 && nextIdx >= 0 && nextIdx < items.Count)
                        {
                            bool ok = args.TrySetNewFocusedElement(items[nextIdx]);
                            Logger.Info($"LosingFocus: nav-{args.Direction} redirect to pill '{items[nextIdx].Name}' {(ok ? "ok" : "REFUSED")}");
                        }
                        else
                        {
                            args.TryCancel();
                        }
                        return;
                    }
                }

                if (args.Direction != FocusNavigationDirection.Up && args.Direction != FocusNavigationDirection.Down)
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
                var first = FindFirstRealControl(sv);
                if (first == null)
                {
                    Logger.Warn("FocusFirstControlInActiveTab: no focusable control found in active tab");
                    return;
                }
                // Keyboard, not Programmatic: programmatic focus draws NO focus
                // visual, so gamepad users can't see where they landed.
                if (!first.Focus(FocusState.Keyboard))
                {
                    Logger.Warn($"FocusFirstControlInActiveTab: Focus() refused on {first.GetType().Name} '{(first as FrameworkElement)?.Name}'");
                }
                else
                {
                    Logger.Info($"FocusFirstControlInActiveTab: focused {first.GetType().Name} '{(first as FrameworkElement)?.Name}'");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"FocusFirstControlInActiveTab failed: {ex.Message}");
            }
        }

        /// <summary>
        /// First focusable ACTUAL control inside <paramref name="root"/> — skips bare
        /// ScrollViewers (they draw no focus visual and their viewport-sized bounds wreck
        /// the next spatial move) by diving into them instead of focusing them.
        /// </summary>
        private static Control FindFirstRealControl(DependencyObject root)
        {
            for (int depth = 0; depth < 4; depth++)
            {
                var candidate = FocusManager.FindFirstFocusableElement(root) as Control;
                if (candidate == null) return null;
                if (!(candidate is ScrollViewer inner)) return candidate;
                root = inner; // dive into the nested ScrollViewer
            }
            return null;
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
                    .FirstOrDefault(rb => rb.IsChecked == true && rb.Visibility == Visibility.Visible)
                    ?? MainNavPanel.Children.OfType<RadioButton>()
                        .FirstOrDefault(rb => rb.Visibility == Visibility.Visible);
                if (active == null)
                {
                    Logger.Warn("FocusActiveNavItem: no visible nav item to focus");
                    return;
                }
                // Keyboard, not Programmatic: programmatic focus draws NO focus visual,
                // which left first-open gamepad focus invisible (field report 0.3.2742).
                if (!active.Focus(FocusState.Keyboard))
                {
                    // Early in the open transition the pill can refuse focus (not yet
                    // loaded/arranged). One deferred retry covers that window.
                    Logger.Info($"FocusActiveNavItem: Focus() refused on '{active.Name}', retrying in 250ms");
                    _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
                    {
                        await Task.Delay(250);
                        bool ok = active.Focus(FocusState.Keyboard);
                        Logger.Info($"FocusActiveNavItem retry: {(ok ? "ok" : "still refused")} on '{active.Name}'");
                    });
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"FocusActiveNavItem failed: {ex.Message}");
            }
        }

    }
}
