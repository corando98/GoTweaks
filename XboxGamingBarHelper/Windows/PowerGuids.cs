using System;

namespace XboxGamingBarHelper.Windows
{
    internal static class PowerGuids
    {
        // Processor settings subgroup
        public static readonly Guid GUID_PROCESSOR_SETTINGS_SUBGROUP = new Guid("54533251-82be-4824-96c1-47b60b740d00");

        // CPU Boost
        public static readonly Guid GUID_PROCESSOR_PERFBOOST_MODE = new Guid("be337238-0d82-4146-a960-4f3749d470c7");

        // CPU EPP
        public static readonly Guid GUID_PROCESSOR_EPP = new Guid("36687f9e-e3a5-4dbf-b1dc-15eb381c6863");

        // Power & Sleep buttons subgroup
        public static readonly Guid GUID_BUTTONS_SUBGROUP = new Guid("4f971e89-eebd-4455-a8de-9e59040e7347");
        public static readonly Guid GUID_POWERBUTTON_ACTION = new Guid("7648efa3-dd9c-4e3e-b566-50f929386280"); // 0=Do nothing, 1=Sleep, 2=Hibernate, 3=Shut down

        // Display subgroup
        public static readonly Guid GUID_VIDEO_SUBGROUP = new Guid("7516b95f-f776-4464-8c53-06167f40cc99");
        public static readonly Guid GUID_VIDEO_IDLE = new Guid("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e"); // seconds, 0=never (Turn off display after)

        // Sleep subgroup
        public static readonly Guid GUID_SLEEP_SUBGROUP = new Guid("238c9fa8-0aad-41ed-83f4-97be242c8f20");
        public static readonly Guid GUID_SLEEP_IDLE = new Guid("29f6c1db-86da-48c5-9fdb-f2b67b1f44da"); // seconds, 0=never (Sleep after)

        // Processor state limits (percentage). The Max/Min CPU State feature is retired
        // (issue #103 — don't mutate the user's power plan); these remain solely for the
        // one-time startup restore of values older builds wrote (SystemRestoreService).
        public static readonly Guid GUID_PROCESSOR_THROTTLE_MAX = new Guid("bc5038f7-23e0-4960-96da-33abaf5935ec"); // Maximum processor state %
        public static readonly Guid GUID_PROCESSOR_THROTTLE_MIN = new Guid("893dee8e-2bef-41e0-89c6-b55d0929964c"); // Minimum processor state %
        public static readonly Guid GUID_PROCESSOR_THROTTLE_MAX1 = new Guid("bc5038f7-23e0-4960-96da-33abaf5935ed"); // Maximum processor state % for Processor Power Efficiency Class 1
        public static readonly Guid GUID_PROCESSOR_THROTTLE_MIN1 = new Guid("893dee8e-2bef-41e0-89c6-b55d0929964d"); // Minimum processor state % for Processor Power Efficiency Class 1
    }
}
