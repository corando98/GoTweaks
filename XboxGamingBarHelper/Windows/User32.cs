using NLog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace XboxGamingBarHelper.Windows
{
    internal class User32
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        internal struct DEVMODE
        {
            private const int CCHDEVICENAME = 32;
            private const int CCHFORMNAME = 32;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)]
            public string dmDeviceName;
            public short dmSpecVersion;
            public short dmDriverVersion;
            public short dmSize;
            public short dmDriverExtra;
            public int dmFields;

            public int dmPositionX;
            public int dmPositionY;
            public int dmDisplayOrientation;
            public int dmDisplayFixedOutput;

            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHFORMNAME)]
            public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel;
            public int dmPelsWidth;
            public int dmPelsHeight;
            public int dmDisplayFlags;
            public int dmDisplayFrequency;

            public int dmICMMethod;
            public int dmICMIntent;
            public int dmMediaType;
            public int dmDitherType;
            public int dmReserved1;
            public int dmReserved2;
            public int dmPanningWidth;
            public int dmPanningHeight;
        }

        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindowThreadProcessId(IntPtr hWnd, out IntPtr lpdwProcessId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        // Delegate for the EnumWindows callback function
        private delegate bool EnumWindowsProc(IntPtr hWnd, int lParam);

        // DllImport for the EnumWindows function
        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc enumFunc, int lParam);

        // DllImport for IsWindowVisible
        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;
        private const int SW_MINIMIZE = 6;
        private const int SW_RESTORE = 9;
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_APPWINDOW = 0x00040000;

        /// <summary>
        /// Finds the ApplicationFrameHost window whose title matches
        /// <paramref name="title"/> exactly. The Game Bar-hosted widget window
        /// is not an ApplicationFrameWindow, so the class filter keeps these
        /// helpers from ever touching it (#94).
        /// </summary>
        private static IntPtr FindAppFrameWindow(string title, bool visibleOnly)
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows((hWnd, lParam) =>
            {
                if (visibleOnly && !IsWindowVisible(hWnd)) return true;

                var classBuf = new StringBuilder(64);
                if (GetClassName(hWnd, classBuf, classBuf.Capacity) == 0) return true;
                if (classBuf.ToString() != "ApplicationFrameWindow") return true;

                int len = GetWindowTextLength(hWnd);
                if (len <= 0) return true;
                var titleBuf = new StringBuilder(len + 1);
                GetWindowText(hWnd, titleBuf, titleBuf.Capacity);
                if (titleBuf.ToString() != title) return true;

                found = hWnd;
                return false; // stop enumeration
            }, 0);
            return found;
        }

        // ITaskbarList lets us explicitly retire the taskbar button of a hidden
        // UWP frame window — the shell often keeps the button alive after a
        // bare SW_HIDE (observed on 2576: window hidden, button remained).
        [ComImport]
        [Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
        [ClassInterface(ClassInterfaceType.None)]
        private class TaskbarListInstance
        {
        }

        [ComImport]
        [Guid("56FDF342-FD6D-11d0-958A-006097C9A090")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ITaskbarList
        {
            void HrInit();
            void AddTab(IntPtr hwnd);
            void DeleteTab(IntPtr hwnd);
            void ActivateTab(IntPtr hwnd);
            void SetActiveAlt(IntPtr hwnd);
        }

        /// <summary>
        /// Hides the desktop app window entirely (no taskbar entry). Used by the
        /// widget's close-to-tray path (#94): the UWP process can't hide its own
        /// window, and closing it instead suspends the whole process — killing
        /// the Game Bar widget. Restore via <see cref="ShowAppFrameWindow"/>.
        /// Minimize-then-hide plus an explicit taskbar DeleteTab, because a bare
        /// SW_HIDE leaves the UWP frame window's taskbar button behind.
        /// </summary>
        public static bool HideAppFrameWindow(string title)
        {
            IntPtr hWnd = FindAppFrameWindow(title, visibleOnly: true);
            if (hWnd == IntPtr.Zero)
            {
                return false;
            }

            // ApplicationFrameHost fights external ShowWindow manipulation
            // while its CoreWindow is alive (which ours always is — the
            // keep-alive session is what saves the widget): plain hides get
            // re-presented (2578/2579), minimize sticks but keeps a taskbar
            // button (2577/2579). So make the SHELL exclude the window
            // instead: WS_EX_TOOLWINDOW windows never get taskbar buttons —
            // a styling rule the frame host doesn't override. Minimize (which
            // sticks), wait for the transition, then flip the style and hide.
            ShowWindow(hWnd, SW_MINIMIZE);
            for (int waited = 0; waited < 1000 && !IsIconic(hWnd); waited += 50)
            {
                System.Threading.Thread.Sleep(50);
            }

            int exStyle = GetWindowLong(hWnd, GWL_EXSTYLE);
            SetWindowLong(hWnd, GWL_EXSTYLE, (exStyle | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW);

            ShowWindow(hWnd, SW_HIDE);

            try
            {
                var taskbar = (ITaskbarList)new TaskbarListInstance();
                taskbar.HrInit();
                taskbar.DeleteTab(hWnd);
                Marshal.ReleaseComObject(taskbar);
            }
            catch
            {
                // Cosmetic only — the tool-window style already removes the button.
            }

            return true;
        }

        /// <summary>
        /// Re-shows (and foregrounds) a desktop app window previously hidden by
        /// <see cref="HideAppFrameWindow"/>, or restores it if minimized.
        /// Returns false when no such window exists (app view never created —
        /// caller should launch the app instead).
        /// </summary>
        public static bool ShowAppFrameWindow(string title)
        {
            IntPtr hWnd = FindAppFrameWindow(title, visibleOnly: false);
            if (hWnd == IntPtr.Zero)
            {
                return false;
            }

            // Undo the tray-hide styling so the taskbar button comes back.
            int exStyle = GetWindowLong(hWnd, GWL_EXSTYLE);
            if ((exStyle & WS_EX_TOOLWINDOW) != 0)
            {
                SetWindowLong(hWnd, GWL_EXSTYLE, (exStyle & ~WS_EX_TOOLWINDOW) | WS_EX_APPWINDOW);
            }

            ShowWindow(hWnd, IsIconic(hWnd) ? SW_RESTORE : SW_SHOW);
            SetForegroundWindow(hWnd);
            return true;
        }

        // UWP child-window resolution: ApplicationFrameHost.exe hosts the visible frame for
        // packaged (MSIX/UWP) apps. The window we see via GetForegroundWindow is the frame
        // host's, so GetWindowThreadProcessId returns ApplicationFrameHost's PID, not the
        // real app's. The real UWP process owns a child window with class
        // "Windows.UI.Core.CoreWindow" (or "ApplicationFrameInputSinkWindow" in some
        // shapes). We enumerate children to find it and pull the real PID. (#87 Forza
        // Horizon 4 and any other Microsoft Store / Game Pass game.)
        private delegate bool EnumChildProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr hWndParent, EnumChildProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        /// <summary>
        /// When <paramref name="frameHostWindow"/> is an ApplicationFrameHost top-level window
        /// (i.e. the chrome that wraps a UWP/MSIX app like Forza Horizon 4), resolve the real
        /// UWP app's PID and full image path by walking child windows for a Windows.UI.Core
        /// CoreWindow. Returns true and fills the out params on success; false otherwise.
        /// Cheap on non-UWP windows: EnumChildWindows on a normal app yields no CoreWindow and
        /// we bail without any process queries.
        /// </summary>
        public static bool TryResolveUwpAppFromFrameHost(IntPtr frameHostWindow, out int realPid, out string realPath, out string realProcessName)
        {
            realPid = -1;
            realPath = string.Empty;
            realProcessName = string.Empty;

            if (frameHostWindow == IntPtr.Zero) return false;

            try
            {
                GetWindowThreadProcessId(frameHostWindow, out IntPtr frameHostPidPtr);
                int frameHostPid = (int)frameHostPidPtr;

                IntPtr foundChildPidPtr = IntPtr.Zero;
                var classBuf = new StringBuilder(64);

                EnumChildWindows(frameHostWindow, (childHwnd, _) =>
                {
                    classBuf.Length = 0;
                    if (GetClassName(childHwnd, classBuf, classBuf.Capacity) == 0) return true;
                    var cls = classBuf.ToString();
                    if (cls != "Windows.UI.Core.CoreWindow") return true;

                    GetWindowThreadProcessId(childHwnd, out IntPtr childPidPtr);
                    if ((int)childPidPtr != 0 && (int)childPidPtr != frameHostPid)
                    {
                        foundChildPidPtr = childPidPtr;
                        return false; // stop enumeration
                    }
                    return true;
                }, IntPtr.Zero);

                if (foundChildPidPtr == IntPtr.Zero) return false;

                int childPid = (int)foundChildPidPtr;
                IntPtr hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)childPid);
                if (hProc == IntPtr.Zero) return false;
                try
                {
                    var pathBuf = new StringBuilder(1024);
                    uint size = (uint)pathBuf.Capacity;
                    if (!QueryFullProcessImageName(hProc, 0, pathBuf, ref size)) return false;
                    realPath = pathBuf.ToString();
                }
                finally { CloseHandle(hProc); }

                realPid = childPid;
                try { realProcessName = System.IO.Path.GetFileNameWithoutExtension(realPath); }
                catch { realProcessName = string.Empty; }
                return true;
            }
            catch (Exception ex)
            {
                Logger.Debug($"TryResolveUwpAppFromFrameHost failed: {ex.Message}");
                return false;
            }
        }

        // DllImport for GetShellWindow
        [DllImport("user32.dll")]
        private static extern IntPtr GetShellWindow();

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool EnumDisplaySettings(string lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int ChangeDisplaySettings(ref DEVMODE lpDevMode, int dwFlags);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceKey;
        }

        private const int DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x00000001;
        private const int DISPLAY_DEVICE_PRIMARY_DEVICE = 0x00000004;

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool EnumDisplayDevices(string lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int ChangeDisplaySettingsEx(string lpszDeviceName, ref DEVMODE lpDevMode, IntPtr hwnd, int dwFlags, IntPtr lParam);

        /// <summary>
        /// ChangeDisplaySettings pinned to the same PRIMARY display device that
        /// EnumDisplaySettingsPrimary reads modes from. Mixing a primary-device
        /// read with a NULL-device apply can target two different adapters on
        /// multi-GPU / eGPU setups.
        /// </summary>
        private static int ChangeDisplaySettingsPrimary(ref DEVMODE lpDevMode, int dwFlags)
        {
            string device = _primaryDisplayDeviceName ?? (_primaryDisplayDeviceName = GetPrimaryDisplayDeviceName());
            if (device != null)
            {
                return ChangeDisplaySettingsEx(device, ref lpDevMode, IntPtr.Zero, dwFlags, IntPtr.Zero);
            }
            return ChangeDisplaySettings(ref lpDevMode, dwFlags);
        }

        // Cached primary display device name (e.g. @"\\.\DISPLAY1"). Passing NULL
        // to EnumDisplaySettings means "the display device on the calling thread",
        // which for our elevated, scheduled-task-launched helper can resolve to
        // the wrong adapter on multi-GPU / eGPU / remote-session setups — users
        // then see a phantom 1920x1080@60 instead of the panel's real mode
        // (issues #91, #47). Resolving the PRIMARY_DEVICE by name pins every
        // query/change to the display the user actually sees. Cache is cheap to
        // refresh and display topology rarely changes, so re-resolve on failure.
        private static string _primaryDisplayDeviceName;

        private static string GetPrimaryDisplayDeviceName()
        {
            try
            {
                var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE)) };
                for (uint i = 0; EnumDisplayDevices(null, i, ref dd, 0); i++)
                {
                    if ((dd.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) != 0 &&
                        (dd.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0)
                    {
                        return dd.DeviceName;
                    }
                    dd.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"GetPrimaryDisplayDeviceName failed: {ex.Message}");
            }
            return null;    // fall back to NULL-device behaviour
        }

        /// <summary>
        /// EnumDisplaySettings against the PRIMARY display device. For
        /// ENUM_CURRENT_SETTINGS a failure means something is genuinely wrong
        /// (stale topology cache, device vanished), so re-resolve once and fall
        /// back to the NULL device. For mode-ENUMERATION indices a false return
        /// is the normal end-of-list signal — no retry and no NULL-device
        /// fallback there, or we'd splice two adapters' mode lists together.
        /// </summary>
        private static bool EnumDisplaySettingsPrimary(int iModeNum, ref DEVMODE lpDevMode)
        {
            string device = _primaryDisplayDeviceName ?? (_primaryDisplayDeviceName = GetPrimaryDisplayDeviceName());
            bool isCurrentQuery = iModeNum == ENUM_CURRENT_SETTINGS;

            if (device != null)
            {
                if (EnumDisplaySettings(device, iModeNum, ref lpDevMode))
                {
                    return true;
                }
                if (!isCurrentQuery)
                {
                    return false;   // end of mode enumeration — expected
                }
                // Stale cache (topology changed) — re-resolve once, then retry.
                _primaryDisplayDeviceName = null;
                device = _primaryDisplayDeviceName = GetPrimaryDisplayDeviceName();
                if (device != null && EnumDisplaySettings(device, iModeNum, ref lpDevMode))
                {
                    return true;
                }
            }
            return isCurrentQuery
                ? EnumDisplaySettings(null, iModeNum, ref lpDevMode)
                : device == null && EnumDisplaySettings(null, iModeNum, ref lpDevMode);
        }

        private static int GetWindowProcessId(IntPtr windowHandle)
        {
            if (windowHandle == IntPtr.Zero)
            {
                // This is normal when no window has focus (UAC prompts, protected processes, etc.)
                Logger.Debug("Can't get process id from invalid window handle.");
                return -1;
            }

            GetWindowThreadProcessId(windowHandle, out IntPtr windowProcessId);
            if (windowProcessId == IntPtr.Zero)
            {
                Logger.Debug("Can't get window process id.");
                return -1;
            }

            return (int)windowProcessId;
        }

        public static string GetWindowTitle(IntPtr windowHandle)
        {
            if (windowHandle == IntPtr.Zero)
            {
                Logger.Debug("Can't get window title from invalid window handle.");
                return string.Empty;
            }

            var length = GetWindowTextLength(windowHandle);
            if (length == 0)
            {
                //Logger.Error($"Window doesn't have window title??");
                return string.Empty;
            }

            var sb = new StringBuilder(length + 1);
            GetWindowText(windowHandle, sb, sb.Capacity);

            return sb.ToString();
        }

        /// <summary>
        /// Gets the process ID of the current foreground window.
        /// Returns -1 if no window has focus or process ID cannot be determined.
        /// For UWP/MSIX apps hosted by ApplicationFrameHost, resolves to the real app PID
        /// (callers like RTSS matching expect the rendering process, not the frame host). (#87)
        /// </summary>
        public static int GetForegroundProcessId()
        {
            IntPtr fg = GetForegroundWindow();
            int pid = GetWindowProcessId(fg);
            if (pid <= 0) return pid;

            try
            {
                var process = Process.GetProcessById(pid);
                bool isFrameHost = false;
                try { isFrameHost = string.Equals(process.ProcessName, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase); }
                catch { }
                if (isFrameHost && TryResolveUwpAppFromFrameHost(fg, out int realPid, out string realPath, out string realProcessName))
                {
                    // Don't surface GoTweaks itself as the foreground "game" — our widget is also
                    // a packaged UWP app and would otherwise be returned here. (#87)
                    bool isOwnPackage =
                        (realPath != null && realPath.IndexOf("PlayandBuildCustom", StringComparison.OrdinalIgnoreCase) >= 0) ||
                        string.Equals(realProcessName, "XboxGamingBar", StringComparison.OrdinalIgnoreCase);
                    if (!isOwnPackage)
                    {
                        return realPid;
                    }
                }
            }
            catch { /* process may have exited */ }

            return pid;
        }

        public static void GetOpenWindows(IDictionary<int, ProcessWindow> windows)
        {
            var shellWindow = GetShellWindow();
            // Get the exact window handle that has keyboard focus
            var focusedWindowHandle = GetForegroundWindow();

            if (windows == null)
            {
                windows = new Dictionary<int, ProcessWindow>();
            }
            else
            {
                windows.Clear();
            }

            EnumWindows(delegate (IntPtr hWnd, int lParam)
            {
                // Exclude the shell window itself
                if (hWnd == shellWindow) return true;

                // Exclude invisible windows
                if (!IsWindowVisible(hWnd)) return true;

                var processId = GetWindowProcessId(hWnd);
                var windowTitle = GetWindowTitle(hWnd);
                if (processId == -1)
                {
                    // This is normal for certain system windows
                    return true; // Continue enumeration
                }

                try
                {
                    var process = Process.GetProcessById(processId);
                    string processPath = string.Empty;
                    string processName = string.Empty;
                    try
                    {
                        processName = process.ProcessName;
                    }
                    catch (Exception)
                    {
                        // Can't access ProcessName for protected processes
                    }
                    try
                    {
                        // MainModule access can throw Win32Exception for protected processes
                        // even with null-conditional operator
                        var mainModule = process.MainModule;
                        if (mainModule != null)
                        {
                            processPath = mainModule.FileName ?? string.Empty;
                        }
                    }
                    catch (Exception)
                    {
                        // Can't access MainModule for protected processes (anti-cheat, system processes)
                        // Continue with empty path - this is normal
                    }

                    // UWP/MSIX apps (Forza Horizon 4, anything from the Microsoft Store / Game
                    // Pass) are hosted by ApplicationFrameHost.exe — the window we just saw is the
                    // frame host's, not the real app's. Walk the child windows to find the real
                    // UWP app PID + path; if found, present the window as belonging to the real
                    // app so downstream matching (package-prefix path match, profile keying,
                    // RTSS, AutoTDP) works the same as for a normal Win32 game. (#87)
                    //
                    // Skip the substitution when the resolved app IS GoTweaks itself — our widget
                    // is also a packaged UWP app, so the resolver would otherwise present the
                    // widget as a running "game" and the widget would detect itself.
                    bool isFrameHost =
                        string.Equals(processName, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase) ||
                        processPath.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase);
                    if (isFrameHost && TryResolveUwpAppFromFrameHost(hWnd, out int realPid, out string realPath, out string realProcessName))
                    {
                        bool isOwnPackage =
                            (realPath != null && realPath.IndexOf("PlayandBuildCustom", StringComparison.OrdinalIgnoreCase) >= 0) ||
                            string.Equals(realProcessName, "XboxGamingBar", StringComparison.OrdinalIgnoreCase);
                        if (!isOwnPackage)
                        {
                            processId = realPid;
                            processPath = realPath;
                            processName = realProcessName;
                        }
                    }

                    // Track focus by exact window handle - only the window with keyboard focus is marked as foreground
                    // This allows different windows from the same app to have different profiles
                    bool hasFocus = (hWnd == focusedWindowHandle);

                    windows[processId] = new ProcessWindow(processId, hWnd, windowTitle, processName, processPath, hasFocus);
                }
                catch (Exception)
                {
                    // Process may have exited, skip it
                }
                return true; // Continue enumeration
            }, 0);
        }

        private const int ENUM_CURRENT_SETTINGS = -1;
        private const int CDS_UPDATEREGISTRY = 0x01;
        private const int CDS_TEST = 0x02;
        private const int DISP_CHANGE_SUCCESSFUL = 0;
        private const int DM_DISPLAYFREQUENCY = 0x400000;
        private const int DM_PELSWIDTH = 0x80000;
        private const int DM_PELSHEIGHT = 0x100000;
        private const int DM_DISPLAYORIENTATION = 0x80;

        // Display orientation constants
        public const int DMDO_DEFAULT = 0;    // Landscape (0°)
        public const int DMDO_90 = 1;         // Portrait (90° clockwise)
        public const int DMDO_180 = 2;        // Landscape flipped (180°)
        public const int DMDO_270 = 3;        // Portrait flipped (270° clockwise)

        public static int GetCurrentRefreshRate()
        {
            DEVMODE vDevMode = new DEVMODE();
            vDevMode.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));

            if (EnumDisplaySettingsPrimary(ENUM_CURRENT_SETTINGS, ref vDevMode))
            {
                return vDevMode.dmDisplayFrequency;
            }
            return 0; // failed
        }

        public static List<int> GetSupportedRefreshRates()
        {
            List<int> refreshRates = new List<int>();
            DEVMODE devMode = new DEVMODE();
            devMode.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));

            int modeIndex = 0;
            while (EnumDisplaySettingsPrimary(modeIndex++, ref devMode))
            {
                int rate = devMode.dmDisplayFrequency;
                if (rate > 0 && !refreshRates.Contains(rate))
                    refreshRates.Add(rate);
            }

            refreshRates.Sort();
            foreach (var refreshRate in refreshRates)
            {
                Logger.Info($"Found refresh rate {refreshRate}");
            }
            return refreshRates;
        }

        /// <summary>
        /// Set monitor refresh rate to a supported value.
        /// </summary>
        public static bool SetRefreshRateTo(int targetRate)
        {
            /*var supported = GetSupportedRefreshRates();
            if (!supported.Contains(targetRate))
            {
                Console.WriteLine($"Error: {targetRate}Hz is not supported. Supported rates: {string.Join(", ", supported)}");
                return false;
            }*/

            DEVMODE mode = new DEVMODE { dmSize = (short)Marshal.SizeOf(typeof(DEVMODE)) };

            if (!EnumDisplaySettingsPrimary(ENUM_CURRENT_SETTINGS, ref mode))
            {
                Console.WriteLine("Error: Could not retrieve current display settings.");
                return false;
            }

            mode.dmDisplayFrequency = targetRate;
            mode.dmFields = DM_DISPLAYFREQUENCY;

            // Test before applying
            int testResult = ChangeDisplaySettingsPrimary(ref mode, CDS_TEST);
            if (testResult != DISP_CHANGE_SUCCESSFUL)
            {
                Console.WriteLine($"Test failed: {targetRate}Hz not valid on this mode.");
                return false;
            }

            // Apply permanently
            int result = ChangeDisplaySettingsPrimary(ref mode, CDS_UPDATEREGISTRY);
            if (result == DISP_CHANGE_SUCCESSFUL)
            {
                Console.WriteLine($"Successfully switched to {targetRate}Hz.");
                return true;
            }
            else
            {
                Console.WriteLine($"Failed to apply {targetRate}Hz (error code {result}).");
                return false;
            }
        }

        public static string GetCurrentResolution()
        {
            DEVMODE vDevMode = new DEVMODE();
            vDevMode.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));

            if (EnumDisplaySettingsPrimary(ENUM_CURRENT_SETTINGS, ref vDevMode))
            {
                return $"{vDevMode.dmPelsWidth}x{vDevMode.dmPelsHeight}";
            }
            return "1920x1080"; // default fallback
        }

        public static List<string> GetSupportedResolutions()
        {
            var resolutions = new HashSet<string>();
            DEVMODE devMode = new DEVMODE();
            devMode.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));

            // Find native resolution by highest total pixel count (width * height)
            // This correctly identifies 1920x1200 as native over 1920x1080
            int nativeWidth = 0;
            int nativeHeight = 0;
            long maxPixels = 0;
            int modeIndex = 0;

            // First pass: log all resolutions and find native
            var allResolutions = new List<(int w, int h)>();
            while (EnumDisplaySettingsPrimary(modeIndex++, ref devMode))
            {
                int w = devMode.dmPelsWidth;
                int h = devMode.dmPelsHeight;
                allResolutions.Add((w, h));

                long pixels = (long)w * h;
                if (pixels > maxPixels)
                {
                    maxPixels = pixels;
                    nativeWidth = w;
                    nativeHeight = h;
                }
            }

            // Log all found resolutions for debugging
            foreach (var res in allResolutions.Distinct())
            {
                Logger.Debug($"Display supports: {res.w}x{res.h}");
            }

            // Calculate aspect ratio as a fraction (reduce to simplest form)
            int gcd = GCD(nativeWidth, nativeHeight);
            int aspectW = nativeWidth / gcd;
            int aspectH = nativeHeight / gcd;
            Logger.Info($"Native resolution: {nativeWidth}x{nativeHeight} ({maxPixels} pixels), aspect ratio: {aspectW}:{aspectH}");

            // Filter resolutions by aspect ratio
            foreach (var res in allResolutions)
            {
                int w = res.w;
                int h = res.h;

                // Check if this resolution matches the native aspect ratio
                int resGcd = GCD(w, h);
                int resAspectW = w / resGcd;
                int resAspectH = h / resGcd;

                if (resAspectW == aspectW && resAspectH == aspectH)
                {
                    resolutions.Add($"{w}x{h}");
                }
            }

            // Sort by width then height descending
            var sortedList = resolutions.ToList();
            sortedList.Sort((a, b) =>
            {
                var partsA = a.Split('x');
                var partsB = b.Split('x');
                int widthA = int.Parse(partsA[0]);
                int widthB = int.Parse(partsB[0]);
                int heightA = int.Parse(partsA[1]);
                int heightB = int.Parse(partsB[1]);

                if (widthA != widthB) return widthB.CompareTo(widthA);
                return heightB.CompareTo(heightA);
            });

            foreach (var resolution in sortedList)
            {
                Logger.Info($"Resolution option: {resolution} (matches {aspectW}:{aspectH})");
            }
            return sortedList;
        }

        /// <summary>
        /// Calculate Greatest Common Divisor using Euclidean algorithm
        /// </summary>
        private static int GCD(int a, int b)
        {
            while (b != 0)
            {
                int temp = b;
                b = a % b;
                a = temp;
            }
            return a;
        }

        /// <summary>
        /// Set monitor resolution to a supported value.
        /// </summary>
        public static bool SetResolutionTo(string targetResolution)
        {
            var parts = targetResolution.Split('x');
            if (parts.Length != 2 ||
                !int.TryParse(parts[0], out int targetWidth) ||
                !int.TryParse(parts[1], out int targetHeight))
            {
                Logger.Error($"Error: Invalid resolution format {targetResolution}");
                return false;
            }

            DEVMODE mode = new DEVMODE { dmSize = (short)Marshal.SizeOf(typeof(DEVMODE)) };

            if (!EnumDisplaySettingsPrimary(ENUM_CURRENT_SETTINGS, ref mode))
            {
                Logger.Error("Error: Could not retrieve current display settings.");
                return false;
            }

            mode.dmPelsWidth = targetWidth;
            mode.dmPelsHeight = targetHeight;
            mode.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT;

            // Test before applying
            int testResult = ChangeDisplaySettingsPrimary(ref mode, CDS_TEST);
            if (testResult != DISP_CHANGE_SUCCESSFUL)
            {
                Logger.Error($"Test failed: {targetResolution} not valid on this display.");
                return false;
            }

            // Apply permanently
            int result = ChangeDisplaySettingsPrimary(ref mode, CDS_UPDATEREGISTRY);
            if (result == DISP_CHANGE_SUCCESSFUL)
            {
                Logger.Info($"Successfully switched to {targetResolution}.");
                return true;
            }
            else
            {
                Logger.Error($"Failed to apply {targetResolution} (error code {result}).");
                return false;
            }
        }

        /// <summary>
        /// Get current display orientation.
        /// Returns: 0=Landscape, 1=Portrait (90°), 2=Landscape flipped (180°), 3=Portrait flipped (270°)
        /// </summary>
        public static int GetCurrentOrientation()
        {
            DEVMODE vDevMode = new DEVMODE();
            vDevMode.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));

            if (EnumDisplaySettingsPrimary(ENUM_CURRENT_SETTINGS, ref vDevMode))
            {
                return vDevMode.dmDisplayOrientation;
            }
            return 0; // Default to landscape on failure
        }

        /// <summary>
        /// Set display orientation.
        /// orientation: 0=Landscape, 1=Portrait (90°), 2=Landscape flipped (180°), 3=Portrait flipped (270°)
        /// </summary>
        public static bool SetDisplayOrientation(int orientation)
        {
            if (orientation < 0 || orientation > 3)
            {
                Logger.Error($"Invalid orientation value: {orientation}");
                return false;
            }

            DEVMODE mode = new DEVMODE { dmSize = (short)Marshal.SizeOf(typeof(DEVMODE)) };

            if (!EnumDisplaySettingsPrimary(ENUM_CURRENT_SETTINGS, ref mode))
            {
                Logger.Error("Error: Could not retrieve current display settings.");
                return false;
            }

            int currentOrientation = mode.dmDisplayOrientation;
            if (currentOrientation == orientation)
            {
                Logger.Info($"Display already at orientation {orientation}");
                return true;
            }

            // When rotating between landscape (0,2) and portrait (1,3), swap width/height
            bool wasLandscape = (currentOrientation == DMDO_DEFAULT || currentOrientation == DMDO_180);
            bool willBeLandscape = (orientation == DMDO_DEFAULT || orientation == DMDO_180);

            if (wasLandscape != willBeLandscape)
            {
                // Swap width and height
                int temp = mode.dmPelsWidth;
                mode.dmPelsWidth = mode.dmPelsHeight;
                mode.dmPelsHeight = temp;
                mode.dmFields = DM_DISPLAYORIENTATION | DM_PELSWIDTH | DM_PELSHEIGHT;
            }
            else
            {
                mode.dmFields = DM_DISPLAYORIENTATION;
            }

            mode.dmDisplayOrientation = orientation;

            // Test before applying
            int testResult = ChangeDisplaySettingsPrimary(ref mode, CDS_TEST);
            if (testResult != DISP_CHANGE_SUCCESSFUL)
            {
                Logger.Error($"Test failed: orientation {orientation} not valid on this display (error {testResult}).");
                return false;
            }

            // Apply permanently
            int result = ChangeDisplaySettingsPrimary(ref mode, CDS_UPDATEREGISTRY);
            if (result == DISP_CHANGE_SUCCESSFUL)
            {
                string orientationName = orientation switch
                {
                    DMDO_DEFAULT => "Landscape",
                    DMDO_90 => "Portrait",
                    DMDO_180 => "Landscape (flipped)",
                    DMDO_270 => "Portrait (flipped)",
                    _ => "Unknown"
                };
                Logger.Info($"Successfully switched to {orientationName} orientation.");
                return true;
            }
            else
            {
                Logger.Error($"Failed to apply orientation {orientation} (error code {result}).");
                return false;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct INPUT
        {
            public uint type;
            public InputUnion u;
        }

        [StructLayout(LayoutKind.Explicit)]
        struct InputUnion
        {
            [FieldOffset(0)]
            public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        const uint INPUT_KEYBOARD = 1;
        const uint KEYEVENTF_KEYUP = 0x0002;

        public static void SendKeyCombo(ushort modifier1, ushort modifier2, ushort key)
        {
            INPUT[] inputs = new INPUT[6];

            // Press modifier1 (e.g., Ctrl)
            inputs[0].type = INPUT_KEYBOARD;
            inputs[0].u.ki.wVk = modifier1;

            // Press modifier2 (e.g., Shift)
            inputs[1].type = INPUT_KEYBOARD;
            inputs[1].u.ki.wVk = modifier2;

            // Press key (e.g., S)
            inputs[2].type = INPUT_KEYBOARD;
            inputs[2].u.ki.wVk = key;

            //// Release key
            //inputs[3].type = INPUT_KEYBOARD;
            //inputs[3].u.ki.wVk = key;
            //inputs[3].u.ki.dwFlags = KEYEVENTF_KEYUP;

            //// Release modifier2
            //inputs[4].type = INPUT_KEYBOARD;
            //inputs[4].u.ki.wVk = modifier2;
            //inputs[4].u.ki.dwFlags = KEYEVENTF_KEYUP;

            //// Release modifier1
            //inputs[5].type = INPUT_KEYBOARD;
            //inputs[5].u.ki.wVk = modifier1;
            //inputs[5].u.ki.dwFlags = KEYEVENTF_KEYUP;

            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
        }

        public const UInt32 WM_KEYDOWN = 0x0100;
        public const UInt32 WM_CLOSE = 0x0010;
        public const int VK_CONTROL = 0x11;
        public const int VK_SHIFT = 0x10;
        public const int VK_O = 0x4F; // Virtual key code for 'O'

        [DllImport("user32.dll")]
        public static extern bool PostMessage(IntPtr hWnd, UInt32 Msg, int wParam, int lParam);

        /// <summary>
        /// Close the foreground window by sending WM_CLOSE
        /// Skips Game Bar and other system windows
        /// </summary>
        public static bool CloseForegroundWindow()
        {
            try
            {
                IntPtr hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero)
                {
                    Logger.Warn("No foreground window found");
                    return false;
                }

                // Get window title and process info to avoid closing system windows
                GetWindowThreadProcessId(hwnd, out IntPtr processIdPtr);
                int processId = processIdPtr.ToInt32();

                string windowTitle = GetWindowTitle(hwnd);

                // Skip Game Bar, desktop, and other system windows
                if (string.IsNullOrEmpty(windowTitle) ||
                    windowTitle.Contains("Xbox Game Bar") ||
                    windowTitle.Contains("GameBar") ||
                    windowTitle == "Program Manager" ||
                    windowTitle == "Windows Shell Experience Host")
                {
                    Logger.Info($"Skipping system window: {windowTitle}");
                    return false;
                }

                Logger.Info($"Closing foreground window: '{windowTitle}' (PID: {processId})");
                PostMessage(hwnd, WM_CLOSE, 0, 0);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"Error closing foreground window: {ex.Message}");
                return false;
            }
        }

        [DllImport("kernel32.dll")]
        private static extern void Sleep(uint dwMilliseconds);

        // Virtual key codes for modifiers
        private const int VK_ALT = 0x12;

        // Extended keys that need the EXTENDEDKEY flag
        private static readonly HashSet<int> ExtendedKeys = new HashSet<int>
        {
            0x21, 0x22, 0x23, 0x24, // PageUp, PageDown, End, Home
            0x25, 0x26, 0x27, 0x28, // Arrow keys
            0x2D, 0x2E,             // Insert, Delete
            0x5B, 0x5C,             // Win keys
            0x6F,                   // NumpadDivide
            0x90,                   // ScrollLock
            0x91,                   // NumLock
            0x2C,                   // PrintScreen
        };

        private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;

        /// <summary>
        /// Dictionary mapping key names to virtual key codes
        /// </summary>
        private static readonly Dictionary<string, int> KeyNameToVK = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            // Function keys
            { "F1", 0x70 }, { "F2", 0x71 }, { "F3", 0x72 }, { "F4", 0x73 },
            { "F5", 0x74 }, { "F6", 0x75 }, { "F7", 0x76 }, { "F8", 0x77 },
            { "F9", 0x78 }, { "F10", 0x79 }, { "F11", 0x7A }, { "F12", 0x7B },

            // Special keys
            { "Enter", 0x0D }, { "Return", 0x0D },
            { "Tab", 0x09 },
            { "Escape", 0x1B }, { "Esc", 0x1B },
            { "Space", 0x20 }, { "Spacebar", 0x20 },
            { "Backspace", 0x08 }, { "Back", 0x08 },
            { "Delete", 0x2E }, { "Del", 0x2E },
            { "Insert", 0x2D }, { "Ins", 0x2D },
            { "Home", 0x24 },
            { "End", 0x23 },
            { "PageUp", 0x21 }, { "PgUp", 0x21 },
            { "PageDown", 0x22 }, { "PgDn", 0x22 },

            // Arrow keys
            { "Up", 0x26 }, { "Down", 0x28 }, { "Left", 0x25 }, { "Right", 0x27 },

            // Numpad
            { "Num0", 0x60 }, { "Num1", 0x61 }, { "Num2", 0x62 }, { "Num3", 0x63 },
            { "Num4", 0x64 }, { "Num5", 0x65 }, { "Num6", 0x66 }, { "Num7", 0x67 },
            { "Num8", 0x68 }, { "Num9", 0x69 },
            { "NumLock", 0x90 },
            { "NumMultiply", 0x6A }, { "NumAdd", 0x6B }, { "NumSubtract", 0x6D },
            { "NumDecimal", 0x6E }, { "NumDivide", 0x6F },

            // Media keys
            { "MediaPlayPause", 0xB3 }, { "MediaStop", 0xB2 },
            { "MediaNext", 0xB0 }, { "MediaPrev", 0xB1 },
            { "VolumeUp", 0xAF }, { "VolumeDown", 0xAE }, { "VolumeMute", 0xAD },

            // Other
            { "PrintScreen", 0x2C }, { "PrtSc", 0x2C },
            { "ScrollLock", 0x91 },
            { "Pause", 0x13 }, { "Break", 0x13 },
            { "CapsLock", 0x14 },

            // Number keys (top row)
            { "0", 0x30 }, { "1", 0x31 }, { "2", 0x32 }, { "3", 0x33 }, { "4", 0x34 },
            { "5", 0x35 }, { "6", 0x36 }, { "7", 0x37 }, { "8", 0x38 }, { "9", 0x39 },

            // Letter keys
            { "A", 0x41 }, { "B", 0x42 }, { "C", 0x43 }, { "D", 0x44 }, { "E", 0x45 },
            { "F", 0x46 }, { "G", 0x47 }, { "H", 0x48 }, { "I", 0x49 }, { "J", 0x4A },
            { "K", 0x4B }, { "L", 0x4C }, { "M", 0x4D }, { "N", 0x4E }, { "O", 0x4F },
            { "P", 0x50 }, { "Q", 0x51 }, { "R", 0x52 }, { "S", 0x53 }, { "T", 0x54 },
            { "U", 0x55 }, { "V", 0x56 }, { "W", 0x57 }, { "X", 0x58 }, { "Y", 0x59 },
            { "Z", 0x5A },

            // Symbols
            { "Plus", 0xBB }, { "Minus", 0xBD }, { "Equals", 0xBB },
            { "LeftBracket", 0xDB }, { "[", 0xDB }, { "RightBracket", 0xDD }, { "]", 0xDD },
            { "Backslash", 0xDC }, { "Semicolon", 0xBA }, { "Quote", 0xDE },
            { "Comma", 0xBC }, { "Period", 0xBE }, { "Slash", 0xBF },
            { "Backtick", 0xC0 }, { "Tilde", 0xC0 },
        };

        [DllImport("user32.dll")]
        private static extern short VkKeyScan(char ch);

        /// <summary>
        /// Parse and send a keyboard shortcut string (e.g., "Ctrl+Shift+S", "Alt+F4", "Win+G", "A+B+C")
        /// Supports multiple non-modifier keys for macro-style shortcuts
        /// </summary>
        public static bool SendKeyboardShortcut(string shortcut)
        {
            if (string.IsNullOrWhiteSpace(shortcut))
            {
                Logger.Warn("Empty shortcut string provided");
                return false;
            }

            try
            {
                var parts = shortcut.Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries);
                var modifiers = new List<int>();
                var mainKeys = new List<int>(); // Support multiple non-modifier keys

                foreach (var part in parts)
                {
                    var trimmed = part.Trim();
                    var upper = trimmed.ToUpperInvariant();

                    if (upper == "CTRL" || upper == "CONTROL" || upper == "LCTRL" || upper == "LCONTROL")
                    {
                        modifiers.Add(VK_CONTROL);
                    }
                    else if (upper == "RCTRL" || upper == "RCONTROL")
                    {
                        modifiers.Add(0xA3); // VK_RCONTROL
                    }
                    else if (upper == "ALT" || upper == "LALT")
                    {
                        modifiers.Add(VK_ALT);
                    }
                    else if (upper == "RALT")
                    {
                        modifiers.Add(0xA5); // VK_RMENU
                    }
                    else if (upper == "SHIFT" || upper == "LSHIFT")
                    {
                        modifiers.Add(VK_SHIFT);
                    }
                    else if (upper == "RSHIFT")
                    {
                        modifiers.Add(0xA1); // VK_RSHIFT
                    }
                    else if (upper == "WIN" || upper == "WINDOWS" || upper == "LWIN" || upper == "LMETA" || upper == "META")
                    {
                        modifiers.Add(0x5B); // VK_LWIN
                    }
                    else if (upper == "RWIN" || upper == "RMETA")
                    {
                        modifiers.Add(0x5C); // VK_RWIN
                    }
                    else
                    {
                        int keyCode = GetVirtualKeyCode(trimmed);
                        if (keyCode == 0)
                        {
                            Logger.Warn($"Unknown key in shortcut: {trimmed}");
                            return false;
                        }
                        mainKeys.Add(keyCode); // Add to list instead of overwriting
                    }
                }

                if (mainKeys.Count == 0 && modifiers.Count == 0)
                {
                    Logger.Warn($"No valid keys found in shortcut: {shortcut}");
                    return false;
                }

                // Press modifiers
                foreach (var mod in modifiers)
                {
                    SendSingleKey((ushort)mod, false);
                    Sleep(10);
                }

                // Press all main keys
                foreach (var key in mainKeys)
                {
                    SendSingleKey((ushort)key, false);
                    Sleep(10);
                }

                // Release all main keys in reverse order
                for (int i = mainKeys.Count - 1; i >= 0; i--)
                {
                    SendSingleKey((ushort)mainKeys[i], true);
                    Sleep(10);
                }

                // Release modifiers in reverse order
                for (int i = modifiers.Count - 1; i >= 0; i--)
                {
                    SendSingleKey((ushort)modifiers[i], true);
                    Sleep(10);
                }

                Sleep(50);

                Logger.Info($"Sent keyboard shortcut: {shortcut} (modifiers: {modifiers.Count}, keys: {mainKeys.Count})");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"Error sending keyboard shortcut '{shortcut}': {ex.Message}");
                return false;
            }
        }

        private static bool SendSingleKey(ushort vk, bool keyUp)
        {
            uint flags = keyUp ? KEYEVENTF_KEYUP : 0;

            if (ExtendedKeys.Contains(vk))
            {
                flags |= KEYEVENTF_EXTENDEDKEY;
            }

            INPUT[] inputs = new INPUT[1];
            inputs[0].type = INPUT_KEYBOARD;
            inputs[0].u.ki.wVk = vk;
            inputs[0].u.ki.dwFlags = flags;

            var result = SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT)));
            if (result != 1)
            {
                var error = Marshal.GetLastWin32Error();
                Logger.Warn($"SendInput failed for VK=0x{vk:X2} keyUp={keyUp}: result={result}, error={error}");
            }
            return result == 1;
        }

        private static int GetVirtualKeyCode(string keyName)
        {
            if (KeyNameToVK.TryGetValue(keyName, out int vk))
            {
                return vk;
            }

            if (keyName.Length == 1)
            {
                short result = VkKeyScan(keyName[0]);
                if (result != -1)
                {
                    return result & 0xFF;
                }
            }

            return 0;
        }

        #region HDR / Advanced Color APIs

        [DllImport("user32.dll")]
        private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO requestPacket);

        [DllImport("user32.dll")]
        private static extern int DisplayConfigSetDeviceInfo(ref DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE setPacket);

        [DllImport("user32.dll")]
        private static extern int DisplayConfigSetDeviceInfo(ref DISPLAYCONFIG_SET_SDR_WHITE_LEVEL setPacket);

        [DllImport("user32.dll")]
        private static extern int DisplayConfigSetDeviceInfo(ref DISPLAYCONFIG_SET_SDR_WHITE_LEVEL_UINT setPacket);

        [DllImport("user32.dll")]
        private static extern int GetDisplayConfigBufferSizes(
            uint flags,
            out uint numPathArrayElements,
            out uint numModeInfoArrayElements);

        [DllImport("user32.dll")]
        private static extern int QueryDisplayConfig(
            uint flags,
            ref uint numPathArrayElements,
            [Out] DISPLAYCONFIG_PATH_INFO[] pathInfoArray,
            ref uint numModeInfoArrayElements,
            [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
            IntPtr currentTopologyId);

        private const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
        private const int ERROR_SUCCESS = 0;

        private const int DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO = 9;
        private const int DISPLAYCONFIG_DEVICE_INFO_SET_ADVANCED_COLOR_STATE = 10;

        // Undocumented packet types for SDR white-level write — different reverse-engineered
        // sources cite different values. -18 is what Go2HDR ships with (verified working on
        // current Win11 builds for Legion Go 2); -3 is the older value used by ColorControl /
        // AutoHDRConfigurator and is still accepted by some builds. Try -18 first, fall back
        // to -3 if the driver returns ERROR_INVALID_PARAMETER. Layout cache below picks one
        // after the first success so subsequent calls skip the probe.
        private const int DISPLAYCONFIG_DEVICE_INFO_SET_SDR_WHITE_LEVEL_V1 = unchecked((int)0xFFFFFFEE); // -18
        private const int DISPLAYCONFIG_DEVICE_INFO_SET_SDR_WHITE_LEVEL_V2 = unchecked((int)0xFFFFFFFD); // -3

        // Constant from wingdi.h — paths whose target is the built-in panel.
        // SDR white-level writes to external displays are wrong target on multi-monitor.
        private const uint DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL = 0x80000000;

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_SOURCE_INFO
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_RATIONAL
        {
            public uint Numerator;
            public uint Denominator;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_TARGET_INFO
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public uint outputTechnology;
            public uint rotation;
            public uint scaling;
            public DISPLAYCONFIG_RATIONAL refreshRate;
            public uint scanLineOrdering;
            public bool targetAvailable;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_INFO
        {
            public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
            public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
            public uint flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_2DREGION
        {
            public uint cx;
            public uint cy;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_VIDEO_SIGNAL_INFO
        {
            public ulong pixelRate;
            public DISPLAYCONFIG_RATIONAL hSyncFreq;
            public DISPLAYCONFIG_RATIONAL vSyncFreq;
            public DISPLAYCONFIG_2DREGION activeSize;
            public DISPLAYCONFIG_2DREGION totalSize;
            public uint videoStandard;
            public uint scanLineOrdering;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_TARGET_MODE
        {
            public DISPLAYCONFIG_VIDEO_SIGNAL_INFO targetVideoSignalInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINTL
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_SOURCE_MODE
        {
            public uint width;
            public uint height;
            public uint pixelFormat;
            public POINTL position;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct DISPLAYCONFIG_MODE_INFO_UNION
        {
            [FieldOffset(0)]
            public DISPLAYCONFIG_TARGET_MODE targetMode;
            [FieldOffset(0)]
            public DISPLAYCONFIG_SOURCE_MODE sourceMode;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_MODE_INFO
        {
            public uint infoType;
            public uint id;
            public LUID adapterId;
            public DISPLAYCONFIG_MODE_INFO_UNION modeInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
        {
            public int type;
            public int size;
            public LUID adapterId;
            public uint id;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint value;
            public uint colorEncoding;
            public uint bitsPerColorChannel;

            public bool AdvancedColorSupported => (value & 0x1) == 0x1;
            public bool AdvancedColorEnabled => (value & 0x2) == 0x2;
            public bool WideColorEnforced => (value & 0x4) == 0x4;
            public bool AdvancedColorForceDisabled => (value & 0x8) == 0x8;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint enableAdvancedColor;
        }

        // SDR white level is sent in units of 1/1000 of 80 nits (i.e. nits * 1000 / 80).
        // Two known struct layouts in the wild: one ends with a UCHAR finalValue (Pack=4, total 28 bytes),
        // one with ULONG finalValue (Pack=4, total 32 bytes). Different Win11 builds accept different ones —
        // try byte first, fall back to uint on ERROR_INVALID_PARAMETER.
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct DISPLAYCONFIG_SET_SDR_WHITE_LEVEL
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint SDRWhiteLevel;
            public byte finalValue;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct DISPLAYCONFIG_SET_SDR_WHITE_LEVEL_UINT
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint SDRWhiteLevel;
            public uint finalValue;
        }

        // Cached struct flavor + type-id that works on this machine — set after the first
        // successful write so subsequent calls skip the probe.
        private enum SdrWhiteLevelLayout { Unknown = 0, ByteFinal = 1, UintFinal = 2, Unsupported = 3 }
        private static SdrWhiteLevelLayout sdrLayoutCache = SdrWhiteLevelLayout.Unknown;
        // 0 = unknown (probe both); otherwise one of the SET_SDR_WHITE_LEVEL_V* constants.
        private static int sdrTypeCache;
        private static int sdrFailureLogCount;
        private const int SdrFailureLogEvery = 30;

        /// <summary>
        /// Get HDR support and enabled status for the primary display.
        /// </summary>
        public static (bool Supported, bool Enabled) GetHDRStatus()
        {
            try
            {
                int result = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount);
                if (result != ERROR_SUCCESS)
                {
                    Logger.Error($"GetDisplayConfigBufferSizes failed with error {result}");
                    return (false, false);
                }

                var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
                var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];

                result = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
                if (result != ERROR_SUCCESS)
                {
                    Logger.Error($"QueryDisplayConfig failed with error {result}");
                    return (false, false);
                }

                // Check the first/primary display
                if (paths.Length > 0)
                {
                    var colorInfo = new DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO();
                    colorInfo.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO;
                    colorInfo.header.size = Marshal.SizeOf<DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO>();
                    colorInfo.header.adapterId = paths[0].targetInfo.adapterId;
                    colorInfo.header.id = paths[0].targetInfo.id;

                    result = DisplayConfigGetDeviceInfo(ref colorInfo);
                    if (result == ERROR_SUCCESS)
                    {
                        Logger.Info($"HDR Supported: {colorInfo.AdvancedColorSupported}, Enabled: {colorInfo.AdvancedColorEnabled}");
                        return (colorInfo.AdvancedColorSupported, colorInfo.AdvancedColorEnabled);
                    }
                    else
                    {
                        Logger.Error($"DisplayConfigGetDeviceInfo failed with error {result}");
                    }
                }

                return (false, false);
            }
            catch (Exception ex)
            {
                Logger.Error($"GetHDRStatus exception: {ex}");
                return (false, false);
            }
        }

        /// <summary>
        /// Enable or disable HDR on the primary display.
        /// </summary>
        public static bool SetHDREnabled(bool enable)
        {
            try
            {
                int result = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount);
                if (result != ERROR_SUCCESS)
                {
                    Logger.Error($"GetDisplayConfigBufferSizes failed with error {result}");
                    return false;
                }

                var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
                var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];

                result = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
                if (result != ERROR_SUCCESS)
                {
                    Logger.Error($"QueryDisplayConfig failed with error {result}");
                    return false;
                }

                if (paths.Length > 0)
                {
                    var setState = new DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE();
                    setState.header.type = DISPLAYCONFIG_DEVICE_INFO_SET_ADVANCED_COLOR_STATE;
                    setState.header.size = Marshal.SizeOf<DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE>();
                    setState.header.adapterId = paths[0].targetInfo.adapterId;
                    setState.header.id = paths[0].targetInfo.id;
                    setState.enableAdvancedColor = enable ? 1u : 0u;

                    result = DisplayConfigSetDeviceInfo(ref setState);
                    if (result == ERROR_SUCCESS)
                    {
                        Logger.Info($"HDR set to {enable}");
                        return true;
                    }
                    else
                    {
                        Logger.Error($"DisplayConfigSetDeviceInfo failed with error {result}");
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                Logger.Error($"SetHDREnabled exception: {ex}");
                return false;
            }
        }

        /// <summary>
        /// Set the SDR White Level (paper-white nits) on the built-in panel while HDR is active.
        /// Re-implements the same packet that Windows Settings → HDR → "SDR content brightness" writes.
        /// nits is clamped to [80, 480]; values outside the panel's range are ignored by the driver.
        /// External monitors are explicitly skipped — SDR white level applies only to the internal display.
        /// </summary>
        public static bool SetSdrWhiteLevelNits(int nits)
        {
            if (sdrLayoutCache == SdrWhiteLevelLayout.Unsupported) return false;

            if (nits < 80) nits = 80;
            if (nits > 480) nits = 480;

            try
            {
                int result = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount);
                if (result != ERROR_SUCCESS) { Logger.Error($"GetDisplayConfigBufferSizes failed with error {result}"); return false; }

                var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
                var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
                result = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
                if (result != ERROR_SUCCESS) { Logger.Error($"QueryDisplayConfig failed with error {result}"); return false; }
                if (paths.Length == 0) return false;

                uint level = (uint)(nits * 1000 / 80);
                bool wroteAny = false;
                int lastErr = 0;

                foreach (var path in paths)
                {
                    // Skip external monitors — SDR white level only makes sense on the built-in panel.
                    // Without this filter, on a docked Legion Go the write hits the wrong target.
                    if (path.targetInfo.outputTechnology != DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL)
                        continue;

                    // Confirm advanced color is enabled on this internal target. Writing to a
                    // non-HDR target returns ERROR_INVALID_PARAMETER and would spam the log.
                    var advInfo = new DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO();
                    advInfo.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO;
                    advInfo.header.size = Marshal.SizeOf<DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO>();
                    advInfo.header.adapterId = path.targetInfo.adapterId;
                    advInfo.header.id = path.targetInfo.id;
                    if (DisplayConfigGetDeviceInfo(ref advInfo) != ERROR_SUCCESS || !advInfo.AdvancedColorEnabled)
                        continue;

                    if (TryWriteSdrPacket(path.targetInfo.adapterId, path.targetInfo.id, level, out int err))
                    {
                        wroteAny = true;
                    }
                    else
                    {
                        lastErr = err;
                    }
                }

                if (wroteAny)
                {
                    sdrFailureLogCount = 0;
                    Logger.Info($"SDR white level set to {nits} nits ({level}/1000 units)");
                    return true;
                }

                // Reaching here means: either no internal HDR-active path existed (returns false
                // silently), or every probe failed. Don't flip Unsupported on the no-target case;
                // that's a transient state when the user toggles HDR off mid-session. The probe
                // function flips Unsupported itself once it has tried both type IDs and both layouts.
                if (lastErr != 0) ThrottledFailLog($"SDR white level write failed (last err {lastErr}) at {nits} nits");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Error($"SetSdrWhiteLevelNits exception: {ex}");
                return false;
            }
        }

        private static void ThrottledFailLog(string message)
        {
            sdrFailureLogCount++;
            if (sdrFailureLogCount == 1 || sdrFailureLogCount % SdrFailureLogEvery == 0)
                Logger.Error($"{message} (failure #{sdrFailureLogCount})");
        }

        // Probe both packet-type IDs and both struct layouts until one is accepted, then
        // cache the winning combination. (V1=-18 first because Go2HDR ships with it and it
        // works on current Win11 Legion Go 2 builds; falls through to V2=-3 which is what
        // ColorControl / AutoHDRConfigurator historically used.)
        private static bool TryWriteSdrPacket(LUID adapterId, uint targetId, uint level, out int lastError)
        {
            lastError = 0;

            // Probe order: cached-or-V1 first, then V2. Within each, cached-or-byte first, then uint.
            int[] typeOrder = sdrTypeCache != 0
                ? new[] { sdrTypeCache, sdrTypeCache == DISPLAYCONFIG_DEVICE_INFO_SET_SDR_WHITE_LEVEL_V1 ? DISPLAYCONFIG_DEVICE_INFO_SET_SDR_WHITE_LEVEL_V2 : DISPLAYCONFIG_DEVICE_INFO_SET_SDR_WHITE_LEVEL_V1 }
                : new[] { DISPLAYCONFIG_DEVICE_INFO_SET_SDR_WHITE_LEVEL_V1, DISPLAYCONFIG_DEVICE_INFO_SET_SDR_WHITE_LEVEL_V2 };

            foreach (int typeId in typeOrder)
            {
                if (sdrLayoutCache == SdrWhiteLevelLayout.Unknown || sdrLayoutCache == SdrWhiteLevelLayout.ByteFinal)
                {
                    var pkt = new DISPLAYCONFIG_SET_SDR_WHITE_LEVEL();
                    pkt.header.type = typeId;
                    pkt.header.size = Marshal.SizeOf<DISPLAYCONFIG_SET_SDR_WHITE_LEVEL>();
                    pkt.header.adapterId = adapterId;
                    pkt.header.id = targetId;
                    pkt.SDRWhiteLevel = level;
                    pkt.finalValue = 1;
                    int r = DisplayConfigSetDeviceInfo(ref pkt);
                    if (r == ERROR_SUCCESS)
                    {
                        if (sdrLayoutCache == SdrWhiteLevelLayout.Unknown || sdrTypeCache != typeId)
                            Logger.Info($"SDR white level: type=0x{typeId:X8} layout=ByteFinal accepted");
                        sdrLayoutCache = SdrWhiteLevelLayout.ByteFinal;
                        sdrTypeCache = typeId;
                        return true;
                    }
                    lastError = r;
                }

                if (sdrLayoutCache == SdrWhiteLevelLayout.Unknown || sdrLayoutCache == SdrWhiteLevelLayout.UintFinal)
                {
                    var pkt = new DISPLAYCONFIG_SET_SDR_WHITE_LEVEL_UINT();
                    pkt.header.type = typeId;
                    pkt.header.size = Marshal.SizeOf<DISPLAYCONFIG_SET_SDR_WHITE_LEVEL_UINT>();
                    pkt.header.adapterId = adapterId;
                    pkt.header.id = targetId;
                    pkt.SDRWhiteLevel = level;
                    pkt.finalValue = 1u;
                    int r = DisplayConfigSetDeviceInfo(ref pkt);
                    if (r == ERROR_SUCCESS)
                    {
                        if (sdrLayoutCache == SdrWhiteLevelLayout.Unknown || sdrTypeCache != typeId)
                            Logger.Info($"SDR white level: type=0x{typeId:X8} layout=UintFinal accepted");
                        sdrLayoutCache = SdrWhiteLevelLayout.UintFinal;
                        sdrTypeCache = typeId;
                        return true;
                    }
                    lastError = r;
                }
            }

            // Both type IDs and both layouts rejected. If we have no cached success yet,
            // mark the API unsupported so we stop probing. If we DID have a cached success
            // and a single write failed, leave the cache alone — could be a transient
            // ERROR_INVALID_PARAMETER from a momentarily-non-HDR target.
            if (sdrLayoutCache == SdrWhiteLevelLayout.Unknown)
            {
                Logger.Error($"SDR white level: every (type, layout) combination rejected (last err {lastError}). " +
                             $"Marking unsupported until restart.");
                sdrLayoutCache = SdrWhiteLevelLayout.Unsupported;
            }
            return false;
        }

        /// <summary>
        /// Get actual current refresh rate using QueryDisplayConfig (more accurate than EnumDisplaySettings).
        /// This properly reports the actual refresh rate on VRR displays and after display changes.
        /// </summary>
        public static int GetCurrentRefreshRateFromDisplayConfig()
        {
            try
            {
                int result = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount);
                if (result != ERROR_SUCCESS)
                {
                    Logger.Warn($"GetDisplayConfigBufferSizes failed with error {result}, falling back to EnumDisplaySettings");
                    return GetCurrentRefreshRate();
                }

                var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
                var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];

                result = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
                if (result != ERROR_SUCCESS)
                {
                    Logger.Warn($"QueryDisplayConfig failed with error {result}, falling back to EnumDisplaySettings");
                    return GetCurrentRefreshRate();
                }

                // Get refresh rate from the first/primary active display path
                if (paths.Length > 0)
                {
                    var refreshRate = paths[0].targetInfo.refreshRate;
                    if (refreshRate.Denominator > 0)
                    {
                        // Calculate actual refresh rate (e.g., 144000/1000 = 144Hz, 143998/1000 ≈ 144Hz)
                        int hz = (int)Math.Round((double)refreshRate.Numerator / refreshRate.Denominator);
                        Logger.Info($"QueryDisplayConfig refresh rate: {refreshRate.Numerator}/{refreshRate.Denominator} = {hz}Hz");
                        return hz;
                    }
                }

                Logger.Warn("No active display paths found, falling back to EnumDisplaySettings");
                return GetCurrentRefreshRate();
            }
            catch (Exception ex)
            {
                Logger.Error($"GetCurrentRefreshRateFromDisplayConfig exception: {ex}, falling back to EnumDisplaySettings");
                return GetCurrentRefreshRate();
            }
        }

        #endregion
    }
}
