using Windows.UI.Xaml;

namespace XboxGamingBar
{
    public sealed partial class GamingWidget
    {
        // Fail-open default (true = internal panel assumed active) so a query failure or a
        // pre-BatchGet render never blocks functionality that worked before this gate existed.
        private bool _internalPanelActive = true;

        /// <summary>
        /// True when the built-in panel is the active display right now (vs. docked with only
        /// an external monitor active). Read by the Resolution/Rotation Quick tile click
        /// handlers (GamingWidget.QuickSettings.Actions.cs) to no-op instead of requesting a
        /// change that only makes sense against the internal panel.
        /// </summary>
        internal bool IsInternalPanelActive => _internalPanelActive;

        /// <summary>
        /// Applies the internal-panel-active gate to every control that only makes sense against
        /// the built-in panel: the Display card's Resolution/Refresh Rate combo boxes and the
        /// Resolution/Rotation Quick tiles. Settings stay visible (per design - this is a
        /// functional/visual block, not a hide) but are disabled and dimmed. Called from
        /// InternalPanelActiveProperty whenever the helper pushes a new value (dock/undock) and
        /// once after the initial BatchGet completes.
        /// </summary>
        internal void ApplyInternalPanelActiveGate(bool internalActive)
        {
            _internalPanelActive = internalActive;

            if (ResolutionComboBox != null)
            {
                ResolutionComboBox.IsEnabled = internalActive;
                ResolutionComboBox.Opacity = internalActive ? 1.0 : 0.4;
            }

            if (RefreshRatesComboBox != null)
            {
                RefreshRatesComboBox.IsEnabled = internalActive;
                RefreshRatesComboBox.Opacity = internalActive ? 1.0 : 0.4;
            }

            // Refresh the Quick tiles so Resolution/Rotation show the same blocked state.
            UpdateQuickSettingsTileStates();
        }
    }
}
