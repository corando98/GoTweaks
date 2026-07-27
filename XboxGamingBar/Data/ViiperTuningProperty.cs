using Shared.Enums;

namespace XboxGamingBar.Data
{
    /// <summary>
    /// Widget-side property holding a per-source Gyro Tuning bundle for either the gyroscope
    /// (<see cref="Function.Viiper_GyroTuning"/>) or the accelerometer
    /// (<see cref="Function.Viiper_AccelTuning"/>). Format is
    /// "Left=csv|Right=csv|Mixed=csv|Handheld=csv" where each csv is
    /// "mapX,mapY,mapZ,invX,invY,invZ". Tuning is stored per gyro source because the two
    /// Legion halves have mirrored IMUs. The UI toggles/combos in GamingWidget.xaml don't
    /// bind individually — code-behind reads every control, rebuilds the csv for the current
    /// source, merges it into the bundle, and sets this property's value, which auto-syncs
    /// to the helper via the standard pipe + LocalSettings.
    /// </summary>
    internal class ViiperTuningProperty : WidgetProperty<string>
    {
        public const string Identity = "";               // empty bundle -> identity everywhere
        public const string IdentityCsv = "X,Y,Z,0,0,0"; // identity for a single source

        public ViiperTuningProperty(Function function, string initialValue)
            : base(initialValue ?? Identity, null, function)
        {
        }
    }
}
