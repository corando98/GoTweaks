using System;
using Shared.Enums;
using Windows.UI.Core;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;

namespace XboxGamingBar.Data
{
    internal class WidgetSliderProperty : WidgetControlProperty<int, Slider>
    {
        private Windows.UI.Xaml.DispatcherTimer debounceTimer;
        private int pendingValue;
        private bool hasPendingValue;
        private const int DEBOUNCE_DELAY_MS = 500; // Wait 500ms after last change before sending

        /// <summary>
        /// Static counter tracking how many widget properties are currently updating their UI
        /// from helper pipe sync. Used by SettingChanged to skip auto-saves during sync.
        /// Shared across WidgetSliderProperty, WidgetToggleProperty, and LegionPerformanceModeProperty.
        /// </summary>
        internal static int HelperSyncCount = 0;

        /// <summary>
        /// Flag to indicate when the UI is being updated programmatically (from helper sync or profile loading).
        /// When true, ValueChanged events should not trigger profile saves or debounce timer.
        /// </summary>
        public bool IsUpdatingUI { get; internal set; }

        public WidgetSliderProperty(int inValue, Function inFunction, Slider inControl, Page inOwner) : base(inValue, inFunction, inControl, inOwner)
        {
            if (UI != null)
            {
                UI.ValueChanged += Slider_ValueChanged;
                //UI.DragEnter += Slider_DragEnter;
                //UI.DragStarting += Slider_DragStarting;
                //UI.DragOver += Slider_DragOver;
                //UI.DragLeave += Slider_DragLeave;
                UI.Value = inValue;

                // Initialize debounce timer
                debounceTimer = new Windows.UI.Xaml.DispatcherTimer();
                debounceTimer.Interval = TimeSpan.FromMilliseconds(DEBOUNCE_DELAY_MS);
                debounceTimer.Tick += DebounceTimer_Tick;
            }
        }

        public void StopDebounceTimer()
        {
            if (debounceTimer == null || !debounceTimer.IsEnabled)
            {
                return;
            }

            // Clear pending state immediately to avoid stale sends
            hasPendingValue = false;

            // DispatcherTimer must be stopped on the UI thread
            if (Owner?.Dispatcher != null)
            {
                if (Owner.Dispatcher.HasThreadAccess)
                {
                    Logger.Info($"{Function} Stopping debounce timer.");
                    debounceTimer.Stop();
                }
                else
                {
                    _ = Owner.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                    {
                        if (debounceTimer != null && debounceTimer.IsEnabled)
                        {
                            Logger.Info($"{Function} Stopping debounce timer (dispatched).");
                            debounceTimer.Stop();
                        }
                    });
                }
            }
            else
            {
                Logger.Info($"{Function} Stopping debounce timer (no dispatcher).");
                debounceTimer.Stop();
            }
        }

        /// <summary>
        /// Override SetValue to cancel the debounce timer when receiving external updates.
        /// This prevents stale pending values from being sent back to the helper after
        /// a profile switch or AutoTDP adjustment.
        /// </summary>
        public override bool SetValue(object newValue, long updatedTime = 0)
        {
            // Cancel any pending debounce timer when receiving external updates
            // This is critical to prevent stale widget values from corrupting profiles
            // after a profile switch (e.g., game close -> global profile restore)
            if (SuppressRemoteSync && hasPendingValue)
            {
                Logger.Info($"{Function} Cancelling debounce timer due to external update (pending={pendingValue}, new={newValue})");
                StopDebounceTimer();
            }

            return base.SetValue(newValue, updatedTime);
        }

        public void Cleanup()
        {
            if (debounceTimer != null)
            {
                debounceTimer.Stop();
                debounceTimer.Tick -= DebounceTimer_Tick;
                debounceTimer = null;
            }

            if (UI != null)
            {
                UI.ValueChanged -= Slider_ValueChanged;
            }
        }

        //private void Slider_DragLeave(object sender, Windows.UI.Xaml.DragEventArgs e)
        //{
        //    Logger.Info($"{Function} Slider drag leave {e.Data.ToString()}.");
        //}

        //private void Slider_DragOver(object sender, Windows.UI.Xaml.DragEventArgs e)
        //{
        //    Logger.Info($"{Function} Slider drag over {e.Data.ToString()}.");
        //}

        //private void Slider_DragStarting(Windows.UI.Xaml.UIElement sender, Windows.UI.Xaml.DragStartingEventArgs args)
        //{
        //    Logger.Info($"{Function} Slider drag starting {args.Data.ToString()}.");
        //}

        //private void Slider_DragEnter(object sender, Windows.UI.Xaml.DragEventArgs e)
        //{
        //    Logger.Info($"{Function} Slider drag enter {e.Data.ToString()}.");
        //}

        private void DebounceTimer_Tick(object sender, object e)
        {
            try
            {
                if (debounceTimer != null)
                {
                    debounceTimer.Stop();
                }

                // Check if connection is available before sending (supports both AppService and pipe)
                if (!App.IsConnected)
                {
                    Logger.Debug($"{Function} Debounce timer tick - no connection yet, skipping send.");
                    hasPendingValue = false;
                    return;
                }

                if (hasPendingValue)
                {
                    // Send where the slider ACTUALLY settled, not the value latched when the
                    // timer was (re)started. A programmatic slider change can land between
                    // latch and fire without restarting the timer — Slider_ValueChanged
                    // compares against the property cache, so moving the slider BACK to the
                    // cached value looks like "no change" and leaves a stale pending armed.
                    // Field case (Desktop Mode, 0.3.2677): off-toggle set the slider 30→50
                    // (pending=50), on-toggle set it 50→30 (== cache, timer untouched), then
                    // the timer fired 50 — helper/profile stuck at 50 while the UI showed 30.
                    hasPendingValue = false;
                    int settled = UI != null ? (int)UI.Value : pendingValue;
                    if (settled != Value)
                    {
                        Logger.Info($"{Function} Debounce timer elapsed, applying settled value {settled} (latched {pendingValue}).");
                        SetValue(settled);
                    }
                    else if (settled != pendingValue)
                    {
                        Logger.Info($"{Function} Debounce timer elapsed, discarding stale pending {pendingValue} (slider settled back at {settled}).");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"{Function} Error in debounce timer tick: {ex.Message}");
            }
        }

        private void Slider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            // Skip if UI is being updated programmatically (from helper sync)
            // This prevents the debounce timer from being started with stale values
            if (IsUpdatingUI)
            {
                return;
            }

            var newValue = (int)e.NewValue;
            if (newValue != Value)
            {
                Logger.Info($"{Function} Slider value changed from {e.OldValue} to {e.NewValue}, debouncing update.");

                // Store the pending value - do NOT update internal value yet
                // The timer will call SetValue() which updates the value and sends to helper
                pendingValue = newValue;
                hasPendingValue = true;

                // Restart the debounce timer - this delays sending to helper
                debounceTimer.Stop();
                debounceTimer.Start();
            }
        }

        protected override async void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);

            if (UI != null && Owner != null)
            {
                Logger.Info($"Update {Function} slider value {Value}.");
                await Owner.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    // Set flags to prevent ValueChanged from triggering profile saves
                    HelperSyncCount++;
                    IsUpdatingUI = true;
                    try
                    {
                        UI.Value = Value;
                    }
                    finally
                    {
                        IsUpdatingUI = false;
                        HelperSyncCount--;
                    }
                });
            }
        }
    }
}
