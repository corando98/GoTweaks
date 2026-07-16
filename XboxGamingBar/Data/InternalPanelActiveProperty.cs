using Shared.Enums;
using System.Threading.Tasks;
using Windows.UI.Core;

namespace XboxGamingBar.Data
{
    // Read-only status pushed from the helper: is the built-in panel the active display right
    // now? Headless (no single bound control) - it gates several unrelated controls at once
    // (the Resolution/Refresh Rate combo boxes in the Display card, and the Resolution/Rotation
    // Quick tiles), none of which make sense against an external-only monitor. The actual gating
    // logic lives on GamingWidget itself (ApplyInternalPanelActiveGate, in
    // GamingWidget.DisplayGating.cs) since it touches several controls plus a Quick-tile
    // refresh - not a good fit for a single-control property class.
    internal class InternalPanelActiveProperty : WidgetProperty<bool>
    {
        private readonly GamingWidget owner;

        public InternalPanelActiveProperty(bool inValue, GamingWidget inOwner) : base(inValue, null, Function.InternalPanelActive)
        {
            owner = inOwner;
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);
            ApplyGate();
        }

        public override Task OnBatchSyncCompleted()
        {
            ApplyGate();
            return Task.CompletedTask;
        }

        private void ApplyGate()
        {
            if (owner == null) return;
            var value = Value;
            _ = owner.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => owner.ApplyInternalPanelActiveGate(value));
        }
    }
}
