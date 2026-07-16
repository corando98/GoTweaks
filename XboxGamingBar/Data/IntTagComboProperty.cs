using System;
using Shared.Enums;
using Windows.UI.Core;
using Windows.UI.Xaml.Controls;

namespace XboxGamingBar.Data
{
    /// <summary>
    /// Shared ComboBox-backed property for int-valued selectors where each ComboBoxItem's
    /// Tag is the value's string representation (e.g. "0", "60", "300"). Mirrors
    /// ViiperStringComboProperty's pattern: the UI is synced to the constructor's initial
    /// Value BEFORE SelectionChanged is wired up, so a XAML-mount default selection (index
    /// 0) never races the real helper-synced value and gets echoed back up as a bogus
    /// user change.
    /// </summary>
    internal class IntTagComboProperty : WidgetControlProperty<int, ComboBox>
    {
        public IntTagComboProperty(int initialValue, Function inFunction, ComboBox inUI, Page inOwner)
            : base(initialValue, inFunction, inUI, inOwner)
        {
            if (UI != null)
            {
                SyncSelectedIndexFromValue();
                UI.SelectionChanged += ComboBox_SelectionChanged;
            }
        }

        private void SyncSelectedIndexFromValue()
        {
            if (UI == null) return;
            string targetTag = Value.ToString();
            for (int i = 0; i < UI.Items.Count; i++)
            {
                if (UI.Items[i] is ComboBoxItem item && (item.Tag as string) == targetTag)
                {
                    if (UI.SelectedIndex != i)
                    {
                        UI.SelectedIndex = i;
                    }
                    return;
                }
            }
            if (UI.Items.Count > 0 && UI.SelectedIndex < 0)
            {
                UI.SelectedIndex = 0;
            }
        }

        private void ComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
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
                await Owner.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, SyncSelectedIndexFromValue);
            }
        }
    }
}
