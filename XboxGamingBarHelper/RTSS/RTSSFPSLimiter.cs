using NLog;
using Shared.Utilities;
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace XboxGamingBarHelper.RTSS
{
    /// <summary>
    /// Provides FPS limiting functionality via RTSS (RivaTuner Statistics Server).
    /// Uses P/Invoke to RTSSHooks64.dll for direct control.
    /// </summary>
    internal static class RTSSFPSLimiter
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private static bool _isInitialized = false;
        private static bool _isAvailable = false;
        private static bool _profileLoaded = false;

        private const string GLOBAL_PROFILE = "";

        #region P/Invoke Declarations

        [DllImport("RTSSHooks64.dll", CharSet = CharSet.Ansi)]
        private static extern bool GetProfileProperty(string propertyName, IntPtr value, uint size);

        [DllImport("RTSSHooks64.dll", CharSet = CharSet.Ansi)]
        private static extern bool SetProfileProperty(string propertyName, IntPtr value, uint size);

        [DllImport("RTSSHooks64.dll", CharSet = CharSet.Ansi)]
        private static extern void LoadProfile(string profile = GLOBAL_PROFILE);

        [DllImport("RTSSHooks64.dll", CharSet = CharSet.Ansi)]
        private static extern void SaveProfile(string profile = GLOBAL_PROFILE);

        [DllImport("RTSSHooks64.dll", CharSet = CharSet.Ansi)]
        private static extern void UpdateProfiles();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeLibrary(IntPtr hModule);

        #endregion

        private static IntPtr _hooksModule = IntPtr.Zero;

        /// <summary>
        /// Initializes the FPS limiter by loading RTSSHooks64.dll.
        /// </summary>
        public static bool Initialize()
        {
            // Only short-circuit once we've SUCCEEDED. A prior failed attempt
            // (RTSS not installed / DLL missing at helper start) must be able to
            // re-attempt later — callers gate this on the RTSS not-running→running
            // transition, so re-attempts are rare, not per-tick.
            if (_isInitialized && _isAvailable)
                return true;

            _isInitialized = true;

            try
            {
                // Check if RTSS is installed
                if (!RTSSHelper.IsInstalled())
                {
                    Logger.Info("RTSSFPSLimiter: RTSS is not installed");
                    _isAvailable = false;
                    return false;
                }

                // Get RTSS installation path
                string rtssPath = RTSSHelper.InstalledLocation();
                if (string.IsNullOrEmpty(rtssPath))
                {
                    Logger.Warn("RTSSFPSLimiter: Could not find RTSS installation path");
                    _isAvailable = false;
                    return false;
                }

                // Load RTSSHooks64.dll
                string hooksDllPath = Path.Combine(rtssPath, "RTSSHooks64.dll");
                if (!File.Exists(hooksDllPath))
                {
                    Logger.Warn($"RTSSFPSLimiter: RTSSHooks64.dll not found at {hooksDllPath}");
                    _isAvailable = false;
                    return false;
                }

                _hooksModule = LoadLibrary(hooksDllPath);
                if (_hooksModule == IntPtr.Zero)
                {
                    int error = Marshal.GetLastWin32Error();
                    Logger.Error($"RTSSFPSLimiter: Failed to load RTSSHooks64.dll, error code: {error}");
                    _isAvailable = false;
                    return false;
                }

                Logger.Info($"RTSSFPSLimiter: Successfully loaded RTSSHooks64.dll from {hooksDllPath}");
                _isAvailable = true;
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"RTSSFPSLimiter: Initialization failed: {ex.Message}");
                _isAvailable = false;
                return false;
            }
        }

        /// <summary>
        /// Gets whether the FPS limiter is available.
        /// </summary>
        public static bool IsAvailable => _isAvailable;

        /// <summary>
        /// Sets the global FPS limit.
        /// </summary>
        /// <param name="fpsLimit">FPS limit value (0 = unlimited)</param>
        /// <returns>True if successful</returns>
        public static bool SetFPSLimit(int fpsLimit)
        {
            if (!_isAvailable)
            {
                Logger.Debug("RTSSFPSLimiter: Not available, cannot set FPS limit");
                return false;
            }

            if (!RTSSHelper.IsRunning())
            {
                Logger.Debug("RTSSFPSLimiter: RTSS is not running");
                return false;
            }

            try
            {
                // Ensure Global profile is loaded
                LoadProfile(GLOBAL_PROFILE);

                // Set Framerate Limit as requested
                if (SetProfileProperty("FramerateLimit", fpsLimit))
                {
                    // Save and reload profile
                    SaveProfile(GLOBAL_PROFILE);
                    UpdateProfiles();

                    Logger.Info($"RTSSFPSLimiter: Set FramerateLimit to {fpsLimit}");
                    return true;
                }
                else
                {
                    Logger.Warn($"RTSSFPSLimiter: SetProfileProperty returned false for FramerateLimit={fpsLimit}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"RTSSFPSLimiter: Failed to set FPS limit: {ex.Message}");
            }

            return false;
        }

        /// <summary>
        /// Gets the current global FPS limit.
        /// </summary>
        /// <returns>Current FPS limit (0 = unlimited, -1 = error)</returns>
        public static int GetFPSLimit()
        {
            if (!_isAvailable)
            {
                return -1;
            }

            if (!RTSSHelper.IsRunning())
            {
                return -1;
            }

            try
            {
                // Load default profile if not loaded
                if (!_profileLoaded)
                {
                    LoadProfile(GLOBAL_PROFILE);
                    _profileLoaded = true;
                }

                if (GetProfileProperty("FramerateLimit", out int fpsLimit))
                {
                    return fpsLimit;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"RTSSFPSLimiter: Failed to get FPS limit: {ex.Message}");
            }

            return -1;
        }

        #region Generic Property Helpers

        private static bool GetProfileProperty<T>(string propertyName, out T value) where T : struct
        {
            var bytes = new byte[Marshal.SizeOf<T>()];
            var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            value = default;

            try
            {
                if (!GetProfileProperty(propertyName, handle.AddrOfPinnedObject(), (uint)bytes.Length))
                    return false;

                value = Marshal.PtrToStructure<T>(handle.AddrOfPinnedObject());
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"RTSSFPSLimiter: GetProfileProperty failed: {ex.Message}");
                return false;
            }
            finally
            {
                handle.Free();
            }
        }

        private static bool SetProfileProperty<T>(string propertyName, T value) where T : struct
        {
            var bytes = new byte[Marshal.SizeOf<T>()];
            var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);

            try
            {
                Marshal.StructureToPtr(value, handle.AddrOfPinnedObject(), false);
                return SetProfileProperty(propertyName, handle.AddrOfPinnedObject(), (uint)bytes.Length);
            }
            catch (Exception ex)
            {
                Logger.Error($"RTSSFPSLimiter: SetProfileProperty failed: {ex.Message}");
                return false;
            }
            finally
            {
                handle.Free();
            }
        }

        #endregion

        /// <summary>
        /// Cleans up resources.
        /// </summary>
        public static void Shutdown()
        {
            if (_hooksModule != IntPtr.Zero)
            {
                FreeLibrary(_hooksModule);
                _hooksModule = IntPtr.Zero;
                Logger.Info("RTSSFPSLimiter: Unloaded RTSSHooks64.dll");
            }
            _isAvailable = false;
            _isInitialized = false;
            _profileLoaded = false;
        }
    }
}
