using System;
using Shared.Enums;
using Windows.UI.Core;
using Windows.UI.Xaml.Controls;

namespace XboxGamingBar.Data
{
    /// <summary>
    /// Read-only property indicating if handheld-agnostic controller emulation is supported.
    /// </summary>
    internal class ControllerEmulationAvailableProperty : WidgetProperty<bool>
    {
        private readonly Page owner;
        private Action<bool> availabilityCallback;

        public ControllerEmulationAvailableProperty(Page inOwner) : base(false, null, Function.ControllerEmulationAvailable)
        {
            owner = inOwner;
        }

        public void SetAvailabilityCallback(Action<bool> callback)
        {
            availabilityCallback = callback;
            callback?.Invoke(Value);
        }

        protected override async void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);

            if (owner != null && availabilityCallback != null)
            {
                await owner.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    availabilityCallback(Value);
                });
            }
        }
    }

    internal class ControllerEmulationEnabledProperty : WidgetToggleProperty
    {
        public ControllerEmulationEnabledProperty(ToggleSwitch inUI, Page inOwner)
            : base(false, Function.ControllerEmulationEnabled, inUI, inOwner)
        {
        }
    }

    /// <summary>
    /// Property for gyro activation behavior.
    /// 0 = Always On, 1 = Hold, 2 = Toggle
    /// </summary>
    internal class ControllerEmulationGyroActivationModeProperty : WidgetControlProperty<int, ComboBox>
    {
        public ControllerEmulationGyroActivationModeProperty(ComboBox inUI, Page inOwner)
            : base(0, Function.ControllerEmulationGyroActivationMode, inUI, inOwner)
        {
            if (UI != null)
            {
                UI.SelectionChanged += ComboBox_SelectionChanged;
                if (UI.Items.Count > Value)
                {
                    UI.SelectedIndex = Value;
                }
            }
        }

        private void ComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            int newIndex = UI.SelectedIndex;
            if (newIndex >= 0 && newIndex != Value)
            {
                Logger.Info($"{Function} combo box updated to index {newIndex}.");
                SetValue(newIndex);
            }
        }

        protected override async void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);

            if (UI != null && Owner != null)
            {
                await Owner.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    if (UI.Items.Count > Value && UI.SelectedIndex != Value)
                    {
                        Logger.Info($"{Function} combo box selected index {Value}.");
                        UI.SelectedIndex = Value;
                    }
                });
            }
        }
    }

    /// <summary>
    /// Property for gyro activation button binding.
    /// 0 = None, 1 = Right Trigger, 2 = Left Trigger, ...
    /// </summary>
    internal class ControllerEmulationGyroActivationButtonProperty : WidgetControlProperty<int, ComboBox>
    {
        public ControllerEmulationGyroActivationButtonProperty(ComboBox inUI, Page inOwner)
            : base(1, Function.ControllerEmulationGyroActivationButton, inUI, inOwner)
        {
            if (UI != null)
            {
                UI.SelectionChanged += ComboBox_SelectionChanged;
                if (UI.Items.Count > Value)
                {
                    UI.SelectedIndex = Value;
                }
            }
        }

        private void ComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            int newIndex = UI.SelectedIndex;
            if (newIndex >= 0 && newIndex != Value)
            {
                Logger.Info($"{Function} combo box updated to index {newIndex}.");
                SetValue(newIndex);
            }
        }

        protected override async void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);

            if (UI != null && Owner != null)
            {
                await Owner.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    if (UI.Items.Count > Value && UI.SelectedIndex != Value)
                    {
                        Logger.Info($"{Function} combo box selected index {Value}.");
                        UI.SelectedIndex = Value;
                    }
                });
            }
        }
    }

    internal class ControllerEmulationStickInvertXProperty : WidgetToggleProperty
    {
        public ControllerEmulationStickInvertXProperty(ToggleSwitch inUI, Page inOwner)
            : base(false, Function.ControllerEmulationStickInvertX, inUI, inOwner)
        {
        }
    }

    internal class ControllerEmulationStickInvertYProperty : WidgetToggleProperty
    {
        public ControllerEmulationStickInvertYProperty(ToggleSwitch inUI, Page inOwner)
            : base(false, Function.ControllerEmulationStickInvertY, inUI, inOwner)
        {
        }
    }

    internal class ControllerEmulationStickSelectProperty : WidgetControlProperty<int, ComboBox>
    {
        public ControllerEmulationStickSelectProperty(ComboBox inUI, Page inOwner)
            : base(1, Function.ControllerEmulationStickSelect, inUI, inOwner)
        {
            if (UI != null)
            {
                UI.SelectionChanged += ComboBox_SelectionChanged;
                if (UI.Items.Count > Value)
                {
                    UI.SelectedIndex = Value;
                }
            }
        }

        private void ComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            int newIndex = UI.SelectedIndex;
            if (newIndex >= 0 && newIndex != Value)
            {
                Logger.Info($"{Function} combo box updated to index {newIndex}.");
                SetValue(newIndex);
            }
        }

        protected override async void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);

            if (UI != null && Owner != null)
            {
                await Owner.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    if (UI.Items.Count > Value && UI.SelectedIndex != Value)
                    {
                        Logger.Info($"{Function} combo box selected index {Value}.");
                        UI.SelectedIndex = Value;
                    }
                });
            }
        }
    }

    // Stick v2 slider properties
    internal class ControllerEmulationStickSensitivityV2Property : WidgetSliderProperty
    {
        public ControllerEmulationStickSensitivityV2Property(Slider inUI, Page inOwner)
            : base(100, Function.ControllerEmulationStickSensitivityV2, inUI, inOwner) { }
    }

    // Stick v2 combo box properties
    internal class ControllerEmulationStickOrientationV2Property : WidgetControlProperty<int, ComboBox>
    {
        // Default 0 = Flat (no Y/Z swap). With the Mode 0 default, gyroY
        // already drives horizontal directly, so the swap isn't needed for
        // the laser-pointer-from-back behavior to work. Users who prefer the
        // Roll mode (1) for handheld can flip this to Handheld, which makes
        // gyroZ act as the new gyroY.
        public ControllerEmulationStickOrientationV2Property(ComboBox inUI, Page inOwner)
            : base(0, Function.ControllerEmulationStickOrientationV2, inUI, inOwner)
        {
            if (UI != null)
            {
                UI.SelectionChanged += ComboBox_SelectionChanged;
                if (UI.Items.Count > Value) UI.SelectedIndex = Value;
            }
        }

        private void ComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            int newIndex = UI.SelectedIndex;
            if (newIndex >= 0 && newIndex != Value)
            {
                Logger.Info($"{Function} combo box updated to index {newIndex}.");
                SetValue(newIndex);
            }
        }

        protected override async void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);
            if (UI != null && Owner != null)
            {
                await Owner.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    if (UI.Items.Count > Value && UI.SelectedIndex != Value)
                        UI.SelectedIndex = Value;
                });
            }
        }
    }

    internal class ControllerEmulationStickConversionProperty : WidgetControlProperty<int, ComboBox>
    {
        // Default 0 (Yaw) — "laser pointer from back of device" model.
        // horizontal = gyroY directly (no gravity projection, no axis remap),
        // so yawing the device around its own up-axis pans the camera left/
        // right and rolling doesn't change camera direction. Matches what
        // most users intuit when aiming with a handheld. Player/World Space
        // (3/4) use gravity as the yaw axis, which feels like the laser is
        // pointing at the sky on a tilted handheld.
        public ControllerEmulationStickConversionProperty(ComboBox inUI, Page inOwner)
            : base(0, Function.ControllerEmulationStickConversion, inUI, inOwner)
        {
            if (UI != null)
            {
                UI.SelectionChanged += ComboBox_SelectionChanged;
                if (UI.Items.Count > Value) UI.SelectedIndex = Value;
            }
        }

        private void ComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            int newIndex = UI.SelectedIndex;
            if (newIndex >= 0 && newIndex != Value)
            {
                Logger.Info($"{Function} combo box updated to index {newIndex}.");
                SetValue(newIndex);
            }
        }

        protected override async void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);
            if (UI != null && Owner != null)
            {
                await Owner.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    if (UI.Items.Count > Value && UI.SelectedIndex != Value)
                        UI.SelectedIndex = Value;
                });
            }
        }
    }
}
