using System;
using Shared.Enums;
using Windows.UI.Core;
using Windows.UI.Xaml.Controls;

namespace XboxGamingBar.Data
{
    /// <summary>
    /// Property for what the physical power button does (0=Do nothing, 1=Sleep,
    /// 2=Hibernate, 3=Shut down). Two instances exist (AC/DC combo boxes on the
    /// System tab); the initial value always comes from the helper's live read
    /// of the active Windows power plan, not a GoTweaks-owned default.
    /// </summary>
    internal class PowerButtonActionProperty : WidgetControlProperty<int, ComboBox>
    {
        // Goes true once the helper has pushed the real Windows value via BatchSync.
        // Until then the ComboBox's XAML-mount SelectionChanged is a default-render
        // artifact, not a user action (same guard as TdpMethodProperty).
        private bool hasReceivedHelperSync;

        // Set true while NotifyPropertyChanged programmatically updates SelectedIndex,
        // so the resulting SelectionChanged is recognized as helper-driven.
        private bool isUpdatingUI;

        public PowerButtonActionProperty(Function inFunction, ComboBox inUI, Page inOwner) : base(1, inFunction, inUI, inOwner)
        {
            if (UI != null)
            {
                UI.SelectionChanged += ComboBox_SelectionChanged;
            }
        }

        public override bool SetValue(object newValue, long updatedTime = 0)
        {
            if (SuppressRemoteSync)
            {
                hasReceivedHelperSync = true;
            }
            return base.SetValue(newValue, updatedTime);
        }

        private void ComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!hasReceivedHelperSync)
            {
                Logger.Info($"{Function} SelectionChanged before first helper sync - ignoring (XAML mount artifact, idx={UI?.SelectedIndex}).");
                return;
            }

            if (isUpdatingUI)
            {
                return;
            }

            var selectedItem = UI.SelectedItem as ComboBoxItem;
            if (selectedItem?.Tag is string tagString && int.TryParse(tagString, out int newValue))
            {
                if (newValue != Value)
                {
                    Logger.Info($"{Function} combo box updated to {newValue}.");
                    SetValue(newValue);
                }
            }
        }

        protected override async void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);

            if (UI != null && Owner != null)
            {
                await Owner.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    string targetTag = Value.ToString();
                    for (int i = 0; i < UI.Items.Count; i++)
                    {
                        if (UI.Items[i] is ComboBoxItem item && item.Tag as string == targetTag)
                        {
                            if (UI.SelectedIndex != i)
                            {
                                isUpdatingUI = true;
                                try { UI.SelectedIndex = i; }
                                finally { isUpdatingUI = false; }
                            }
                            break;
                        }
                    }
                });
            }
        }
    }
}
