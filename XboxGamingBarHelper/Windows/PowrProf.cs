using System;
using System.Runtime.InteropServices;

namespace XboxGamingBarHelper.Windows
{
    internal static class PowrProf
    {
        [DllImport("powrprof.dll", SetLastError = true)]
        public static extern uint PowerGetActiveScheme(IntPtr UserRootPowerKey, out IntPtr ActivePolicyGuid);

        [DllImport("powrprof.dll", SetLastError = true)]
        public static extern uint PowerSetActiveScheme(IntPtr UserRootPowerKey, ref Guid SchemeGuid);

        // Power Plan enumeration APIs
        [DllImport("powrprof.dll", SetLastError = true)]
        public static extern uint PowerEnumerate(
            IntPtr RootPowerKey,
            IntPtr SchemeGuid,
            IntPtr SubGroupOfPowerSettingsGuid,
            uint AccessFlags,
            uint Index,
            IntPtr Buffer,
            ref uint BufferSize);

        [DllImport("powrprof.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern uint PowerReadFriendlyName(
            IntPtr RootPowerKey,
            ref Guid SchemeGuid,
            IntPtr SubGroupOfPowerSettingsGuid,
            IntPtr PowerSettingGuid,
            IntPtr Buffer,
            ref uint BufferSize);

        // Access flags for PowerEnumerate
        public const uint ACCESS_SCHEME = 16;

        // Power Overlay Scheme APIs (Windows 10/11 power mode slider)
        [DllImport("powrprof.dll", SetLastError = true)]
        public static extern uint PowerGetActualOverlayScheme(out Guid ActualOverlayGuid);

        [DllImport("powrprof.dll", SetLastError = true)]
        public static extern uint PowerGetEffectiveOverlayScheme(out Guid EffectiveOverlayGuid);

        [DllImport("powrprof.dll", SetLastError = true)]
        public static extern uint PowerSetActiveOverlayScheme(Guid OverlaySchemeGuid);

        [DllImport("powrprof.dll", SetLastError = true)]
        public static extern uint PowerReadACValueIndex(
            IntPtr RootPowerKey,
            ref Guid SchemeGuid,
            ref Guid SubGroupOfPowerSettingsGuid,
            ref Guid PowerSettingGuid,
            out uint AcValueIndex);

        [DllImport("powrprof.dll", SetLastError = true)]
        public static extern uint PowerWriteACValueIndex(
            IntPtr RootPowerKey,
            ref Guid SchemeGuid,
            ref Guid SubGroupOfPowerSettingsGuid,
            ref Guid PowerSettingGuid,
            uint AcValueIndex);

        [DllImport("powrprof.dll", SetLastError = true)]
        public static extern uint PowerReadDCValueIndex(
            IntPtr RootPowerKey,
            ref Guid SchemeGuid,
            ref Guid SubGroupOfPowerSettingsGuid,
            ref Guid PowerSettingGuid,
            out uint DcValueIndex);

        [DllImport("powrprof.dll", SetLastError = true)]
        public static extern uint PowerWriteDCValueIndex(
            IntPtr RootPowerKey,
            ref Guid SchemeGuid,
            ref Guid SubGroupOfPowerSettingsGuid,
            ref Guid PowerSettingGuid,
            uint DcValueIndex);

        /// <summary>
        /// Suspends or hibernates the system.
        /// </summary>
        /// <param name="bHibernate">If true, hibernates. If false, sleeps.</param>
        /// <param name="bForce">If true, forces the suspension even if apps refuse.</param>
        /// <param name="bWakeupEventsDisabled">If true, disables wake events.</param>
        /// <returns>True if successful, false otherwise.</returns>
        [DllImport("powrprof.dll", SetLastError = true)]
        public static extern bool SetSuspendState(bool bHibernate, bool bForce, bool bWakeupEventsDisabled);

        // --- Suspend/resume notifications (Modern Standby aware) ---
        //
        // SystemEvents.PowerModeChanged (WM_POWERBROADCAST) is unreliable under
        // S0 Modern Standby — on affected machines (issue #94, LTSC 24H2 +
        // Legion Go 2) neither Suspend/Resume nor StatusChange are delivered
        // around a standby cycle. PowerRegisterSuspendResumeNotification with
        // DEVICE_NOTIFY_CALLBACK is the Modern-Standby-aware path: the kernel
        // invokes the callback directly (no message pump) for both classic S3
        // sleep and S0 standby transitions.

        public const uint DEVICE_NOTIFY_CALLBACK = 2;

        // WM_POWERBROADCAST event types delivered to the callback.
        public const int PBT_APMSUSPEND = 0x0004;
        public const int PBT_APMRESUMESUSPEND = 0x0007;
        public const int PBT_APMRESUMEAUTOMATIC = 0x0012;

        /// <summary>
        /// DEVICE_NOTIFY_CALLBACK_ROUTINE. Keep the delegate instance rooted
        /// (stored in a field) for the lifetime of the registration — the
        /// kernel holds only the native thunk, and a collected delegate means
        /// a crash on the next power transition.
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate int DeviceNotifyCallbackRoutine(IntPtr context, int type, IntPtr setting);

        [StructLayout(LayoutKind.Sequential)]
        public struct DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS
        {
            public DeviceNotifyCallbackRoutine Callback;
            public IntPtr Context;
        }

        [DllImport("powrprof.dll", SetLastError = false)]
        public static extern uint PowerRegisterSuspendResumeNotification(
            uint flags,
            ref DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS recipient,
            out IntPtr registrationHandle);

        [DllImport("powrprof.dll", SetLastError = false)]
        public static extern uint PowerUnregisterSuspendResumeNotification(IntPtr registrationHandle);
    }
}
