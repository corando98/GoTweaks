using Microsoft.Gaming.XboxGameBar;
using NLog;
using System;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.ExtendedExecution;
using Windows.Foundation.Collections;
using Windows.UI.Core.Preview;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using XboxGamingBar.IPC;

namespace XboxGamingBar
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    sealed partial class App : Application
    {
        // Named Pipe client for communication with the helper
        public static NamedPipeClient PipeClient = null;
        public static event EventHandler PipeConnected;
        public static event EventHandler PipeDisconnected;
        public static event EventHandler<PipeMessageEventArgs> PipeMessageReceived;

        /// <summary>
        /// True after first successful pipe connection. Used to detect reconnection vs cold start.
        /// On reconnection, widget should accept helper's TDP mode instead of applying its own profile.
        /// This is static so it persists across widget instance recreations.
        /// </summary>
        public static bool HasEverConnectedToHelper { get; set; } = false;

        // Track the active GamingWidget instance to prevent multiple instances from handling messages
        private static GamingWidget activeGamingWidget = null;
        private static readonly object activeWidgetLock = new object();

        public static void RegisterActiveGamingWidget(GamingWidget widget)
        {
            lock (activeWidgetLock)
            {
                if (activeGamingWidget != null && activeGamingWidget != widget)
                {
                    Logger.Info($"Replacing active GamingWidget. Old instance being deactivated.");
                    // Notify the old instance that it's no longer active so it can clean up
                    try
                    {
                        activeGamingWidget.OnDeactivated();
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Error deactivating old widget instance: {ex.Message}");
                    }
                }
                activeGamingWidget = widget;
                Logger.Info($"GamingWidget registered as active instance.");
            }
        }

        public static void UnregisterActiveGamingWidget(GamingWidget widget)
        {
            lock (activeWidgetLock)
            {
                if (activeGamingWidget == widget)
                {
                    Logger.Info($"Active GamingWidget unregistered.");
                    activeGamingWidget = null;
                }
            }
        }

        public static GamingWidget GetActiveGamingWidget()
        {
            lock (activeWidgetLock)
            {
                return activeGamingWidget;
            }
        }

        private XboxGameBarWidget gamingXboxGameBarWidget = null;
        private XboxGameBarWidget gamingSettingsXboxGameBarWidget = null;
        private GamingWidget gamingWidget = null;
        private GamingWidgetSettings gamingWidgetSettings = null;
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            this.InitializeComponent();

            // Global safety net. As a hosted Xbox Game Bar widget, an unhandled exception
            // on the UI thread tears down the Game Bar widget host — the user sees Game Bar
            // "close the instant it opens" (issue #81 and a class of reports where a profile
            // applies a remembered TDP / setting during activation and something throws).
            // Catch it, log it, and mark it handled so the widget degrades instead of taking
            // the whole overlay down. The exception is still logged so the real cause can be
            // diagnosed from the widget log.
            this.UnhandledException += (s, e) =>
            {
                try { Logger.Error($"Unhandled widget exception (suppressed to keep Game Bar alive): {e.Message}\n{e.Exception}"); }
                catch { /* logging must never re-throw here */ }
                e.Handled = true;
            };
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                try { Logger.Warn($"Unobserved task exception: {e.Exception?.GetBaseException().Message}"); }
                catch { }
                e.SetObserved();
            };

            this.Suspending += OnSuspending;
            this.EnteredBackground += App_EnteredBackground;
            this.LeavingBackground += App_LeavingBackground;
            //var installedLocation = Windows.ApplicationModel.Package.Current.InstalledLocation.Path;
            //var localFolder = ApplicationData.Current.LocalFolder.Path;
            //var localCache = ApplicationData.Current.LocalCacheFolder.Path;
            //Logger.Info($"App initializing {installedLocation} {localFolder} {localCache}");
        }

        private async void App_LeavingBackground(object sender, LeavingBackgroundEventArgs e)
        {
            if (gamingWidget == null) return;

            await gamingWidget.GamingWidget_LeavingBackground(sender, e);
        }

        private void App_EnteredBackground(object sender, EnteredBackgroundEventArgs e)
        {
            gamingWidget?.GamingWidget_EnteredBackground(sender, e);
        }

        /// <summary>
        /// Connects to the helper via Named Pipe.
        /// This works even when the helper is running elevated.
        /// </summary>
        public static async Task<bool> ConnectPipeAsync(int timeoutMs = 5000)
        {
            try
            {
                // Dispose existing client if any
                if (PipeClient != null)
                {
                    PipeClient.MessageReceived -= PipeClient_MessageReceived;
                    PipeClient.Connected -= PipeClient_Connected;
                    PipeClient.Disconnected -= PipeClient_Disconnected;
                    PipeClient.Dispose();
                    PipeClient = null;
                }

                PipeClient = new NamedPipeClient();
                PipeClient.MessageReceived += PipeClient_MessageReceived;
                PipeClient.Connected += PipeClient_Connected;
                PipeClient.Disconnected += PipeClient_Disconnected;

                return await PipeClient.ConnectAsync(timeoutMs);
            }
            catch (Exception ex)
            {
                Logger.Error($"Error connecting to pipe: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Disconnects from the Named Pipe
        /// </summary>
        public static void DisconnectPipe()
        {
            if (PipeClient != null)
            {
                PipeClient.MessageReceived -= PipeClient_MessageReceived;
                PipeClient.Connected -= PipeClient_Connected;
                PipeClient.Disconnected -= PipeClient_Disconnected;
                PipeClient.Disconnect();
                PipeClient = null;
            }
        }

        /// <summary>
        /// Whether we have an active communication channel via Named Pipe
        /// </summary>
        public static bool IsConnected => PipeClient?.IsConnected == true;

        /// <summary>
        /// Sends a message and waits for a response via Named Pipe.
        /// </summary>
        public static async Task<ValueSet> SendMessageAsync(ValueSet message)
        {
            if (PipeClient?.IsConnected == true)
            {
                return await PipeClient.SendRequestAsync(message);
            }

            Logger.Warn("Cannot send message - pipe not connected");
            return null;
        }

        private static void PipeClient_MessageReceived(object sender, PipeMessageEventArgs e)
        {
            Logger.Debug($"Pipe message received from helper");
            PipeMessageReceived?.Invoke(sender, e);
        }

        private static void PipeClient_Connected(object sender, EventArgs e)
        {
            Logger.Info("Named pipe connected to helper");
            PipeConnected?.Invoke(sender, e);
        }

        private static void PipeClient_Disconnected(object sender, EventArgs e)
        {
            Logger.Info("Named pipe disconnected from helper");
            PipeDisconnected?.Invoke(sender, e);
        }

        protected override void OnActivated(IActivatedEventArgs args)
        {
            Logger.Info($"=== App.OnActivated START === Kind={args.Kind}, PreviousExecutionState={args.PreviousExecutionState}");
            XboxGameBarWidgetActivatedEventArgs widgetArgs = null;
            if (args.Kind == ActivationKind.Protocol)
            {
                var protocolArgs = args as IProtocolActivatedEventArgs;
                string scheme = protocolArgs.Uri.Scheme;
                Logger.Info($"Protocol activation: scheme={scheme}, Uri={protocolArgs.Uri}");
                if (scheme.Equals("ms-gamebarwidget"))
                {
                    widgetArgs = args as XboxGameBarWidgetActivatedEventArgs;
                    Logger.Info($"Game Bar widget activation: AppExtensionId={widgetArgs?.AppExtensionId}, IsLaunchActivation={widgetArgs?.IsLaunchActivation}");
                }
            }
            if (widgetArgs != null)
            {
                //
                // Activation Notes:
                //
                //    If IsLaunchActivation is true, this is Game Bar launching a new instance
                // of our widget. This means we have a NEW CoreWindow with corresponding UI
                // dispatcher, and we MUST create and hold onto a new XboxGameBarWidget.
                //
                // Otherwise this is a subsequent activation coming from Game Bar. We MUST
                // continue to hold the XboxGameBarWidget created during initial activation
                // and ignore this repeat activation, or just observe the URI command here and act 
                // accordingly.  It is ok to perform a navigate on the root frame to switch 
                // views/pages if needed.  Game Bar lets us control the URI for sending widget to
                // widget commands or receiving a command from another non-widget process. 
                //
                // Important Cleanup Notes:
                //    When our widget is closed--by Game Bar or us calling XboxGameBarWidget.Close()-,
                // the CoreWindow will get a closed event.  We can register for Window.Closed
                // event to know when our particular widget has shutdown, and cleanup accordingly.
                //
                // NOTE: If a widget's CoreWindow is the LAST CoreWindow being closed for the process
                // then we won't get the Window.Closed event.  However, we will get the OnSuspending
                // call and can use that for cleanup.
                //
                if (widgetArgs.IsLaunchActivation)
                {
                    Logger.Info($"IsLaunchActivation=true: Creating new widget window. Window.Current={Window.Current?.GetHashCode()}, CoreWindow={Window.Current?.CoreWindow?.GetHashCode()}");
                    var rootFrame = new Frame();
                    rootFrame.NavigationFailed += OnNavigationFailed;
                    Window.Current.Content = rootFrame;

                    if (widgetArgs.AppExtensionId == "GamingWidget")
                    {
                        Logger.Info("Creating XboxGameBarWidget for GamingWidget...");
                        try
                        {
                            // Create Game Bar widget object which bootstraps the connection with Game Bar
                            gamingXboxGameBarWidget = new XboxGameBarWidget(widgetArgs, Window.Current.CoreWindow, rootFrame);
                            Logger.Info($"XboxGameBarWidget created: {gamingXboxGameBarWidget?.GetHashCode()}");
                            rootFrame.Navigate(typeof(GamingWidget), gamingXboxGameBarWidget);
                            gamingWidget = rootFrame.Content as GamingWidget;
                            Logger.Info($"GamingWidget navigated: {gamingWidget?.GetHashCode()}");

                            Window.Current.Closed += GamingWidgetWindow_Closed;

                            // Keep the process alive while the widget exists (#94).
                            _ = EnsureWidgetKeepAliveSessionAsync("widget launch activation");
                        }
                        catch (Exception ex)
                        {
                            Logger.Error($"Failed to create GamingWidget on launch activation: {ex.Message}");
                            Logger.Error($"Stack trace: {ex.StackTrace}");
                        }
                    }
                    else if (widgetArgs.AppExtensionId == "GamingWidgetSettings")
                    {
                        Logger.Info("Creating XboxGameBarWidget for GamingWidgetSettings...");
                        gamingSettingsXboxGameBarWidget = new XboxGameBarWidget(widgetArgs, Window.Current.CoreWindow, rootFrame);
                        rootFrame.Navigate(typeof(GamingWidgetSettings), gamingSettingsXboxGameBarWidget);
                        gamingWidgetSettings = rootFrame.Content as GamingWidgetSettings;

                        Window.Current.Closed += GamingSettingsWidgetWindow_Closed;
                    }

                    Logger.Info("Calling Window.Current.Activate()...");
                    Window.Current.Activate();
                    Logger.Info("Window activated successfully");
                }
                else
                {
                    // Subsequent activation from Game Bar
                    // Check if we're running in app mode (no Game Bar widget) and should upgrade to widget mode
                    Logger.Info($"IsLaunchActivation=false: Subsequent Game Bar activation. AppExtensionId={widgetArgs.AppExtensionId}");
                    Logger.Info($"Current state: gamingXboxGameBarWidget={gamingXboxGameBarWidget?.GetHashCode() ?? 0}, gamingWidget={gamingWidget?.GetHashCode() ?? 0}, IsConnected={IsConnected}");
                    Logger.Info($"Window.Current={Window.Current?.GetHashCode()}, CoreWindow={Window.Current?.CoreWindow?.GetHashCode()}");

                    if (widgetArgs.AppExtensionId == "GamingWidget" && gamingXboxGameBarWidget == null)
                    {
                        Logger.Info("Running in app mode but received Game Bar activation. Attempting to upgrade to widget mode.");

                        // Get the existing frame or create a new one
                        var rootFrame = Window.Current.Content as Frame;
                        Logger.Info($"Existing rootFrame: {rootFrame?.GetHashCode() ?? 0}, Content type: {rootFrame?.Content?.GetType().Name ?? "null"}");
                        if (rootFrame == null)
                        {
                            Logger.Info("Creating new Frame for upgrade...");
                            rootFrame = new Frame();
                            rootFrame.NavigationFailed += OnNavigationFailed;
                            Window.Current.Content = rootFrame;
                        }

                        try
                        {
                            // Create the Game Bar widget object
                            Logger.Info("Creating XboxGameBarWidget for upgrade...");
                            gamingXboxGameBarWidget = new XboxGameBarWidget(widgetArgs, Window.Current.CoreWindow, rootFrame);
                            Logger.Info($"XboxGameBarWidget created successfully: {gamingXboxGameBarWidget.GetHashCode()}");

                            // Re-navigate to inject the widget context
                            Logger.Info("Navigating to GamingWidget with new widget context...");
                            rootFrame.Navigate(typeof(GamingWidget), gamingXboxGameBarWidget);
                            gamingWidget = rootFrame.Content as GamingWidget;
                            Logger.Info($"GamingWidget navigated: {gamingWidget?.GetHashCode() ?? 0}");

                            Window.Current.Closed -= GamingWidgetWindow_Closed; // Remove if already registered
                            Window.Current.Closed += GamingWidgetWindow_Closed;

                            // Keep the process alive while the widget exists (#94).
                            _ = EnsureWidgetKeepAliveSessionAsync("widget upgrade activation");

                            Logger.Info("Calling Window.Current.Activate() for upgrade...");
                            Window.Current.Activate();
                            Logger.Info("Successfully upgraded from app mode to Game Bar widget mode.");
                        }
                        catch (Exception ex)
                        {
                            Logger.Error($"Failed to upgrade to widget mode: {ex.Message}");
                            Logger.Error($"Stack trace: {ex.StackTrace}");
                        }
                    }
                    else if (widgetArgs.AppExtensionId == "GamingWidgetSettings" && gamingSettingsXboxGameBarWidget == null)
                    {
                        Logger.Info("Running in app mode but received Game Bar settings activation. Attempting to upgrade.");

                        var rootFrame = Window.Current.Content as Frame;
                        if (rootFrame == null)
                        {
                            rootFrame = new Frame();
                            rootFrame.NavigationFailed += OnNavigationFailed;
                            Window.Current.Content = rootFrame;
                        }

                        try
                        {
                            gamingSettingsXboxGameBarWidget = new XboxGameBarWidget(widgetArgs, Window.Current.CoreWindow, rootFrame);
                            rootFrame.Navigate(typeof(GamingWidgetSettings), gamingSettingsXboxGameBarWidget);
                            gamingWidgetSettings = rootFrame.Content as GamingWidgetSettings;

                            Window.Current.Closed -= GamingSettingsWidgetWindow_Closed;
                            Window.Current.Closed += GamingSettingsWidgetWindow_Closed;

                            Window.Current.Activate();
                            Logger.Info("Successfully upgraded settings to Game Bar widget mode.");
                        }
                        catch (Exception ex)
                        {
                            Logger.Error($"Failed to upgrade settings to widget mode: {ex.Message}");
                        }
                    }
                    else
                    {
                        Logger.Info($"Subsequent activation ignored. gamingXboxGameBarWidget is null: {gamingXboxGameBarWidget == null}");
                    }
                }
            }
            Logger.Info("=== App.OnActivated END ===");
        }

        private void GamingWidgetWindow_Closed(object sender, Windows.UI.Core.CoreWindowEventArgs e)
        {
            Logger.Info("App gaming widget closed");
            gamingXboxGameBarWidget = null;
            gamingWidget = null;
            Window.Current.Closed -= GamingWidgetWindow_Closed;
            // No widget left to keep alive — let normal suspend policy apply.
            ReleaseWidgetKeepAliveSession("widget window closed");
        }

        private void GamingSettingsWidgetWindow_Closed(object sender, Windows.UI.Core.CoreWindowEventArgs e)
        {
            Logger.Info("App gaming widget settings closed");
            gamingSettingsXboxGameBarWidget = null;
            gamingWidgetSettings = null;
            Window.Current.Closed -= GamingSettingsWidgetWindow_Closed;
        }

        /// <summary>
        /// Invoked when the application is launched normally by the end user.  Other entry points
        /// will be used such as when the application is launched to open a specific file.
        /// </summary>
        /// <param name="e">Details about the launch request and process.</param>
        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            Logger.Info("App launched");

            // #94 item 2 (desktop app + Game Bar widget coexistence): the
            // widget has its OWN CoreWindow, so this launch targets a separate
            // main view and both UIs genuinely coexist. The remaining wart is
            // OS policy, not ours: while the desktop window is up the overlay
            // is closed, so the widget view is invisible — closing the desktop
            // window leaves zero visible views and Windows terminates the
            // process (log: "App suspending", PreviousExecutionState=
            // ClosedByUser). Game Bar then briefly shows "Something went wrong
            // with this widget" and relaunches us with IsLaunchActivation=true
            // (verified in widget_2026-07-09_09.log: clean recreate 14s after
            // the close). An earlier attempt to no-op this launch while the
            // widget is active just hung the launch on its splash screen —
            // the window is created by the shell before OnLaunched runs.
            Frame rootFrame = Window.Current.Content as Frame;

            // Do not repeat app initialization when the Window already has content,
            // just ensure that the window is active
            if (rootFrame == null)
            {
                // Create a Frame to act as the navigation context and navigate to the first page
                rootFrame = new Frame();

                rootFrame.NavigationFailed += OnNavigationFailed;

                if (e.PreviousExecutionState == ApplicationExecutionState.Terminated)
                {
                    //TODO: Load state from previously suspended application
                }

                // Place the frame in the current Window
                Window.Current.Content = rootFrame;
            }

            if (e.PrelaunchActivated == false)
            {
                if (rootFrame.Content == null)
                {
                    // When the navigation stack isn't restored navigate to the first page,
                    // configuring the new page by passing required information as a navigation
                    // parameter
                    rootFrame.Navigate(typeof(GamingWidget), e.Arguments);
                }
                // Ensure the current window is active
                Window.Current.Activate();

                // If the window was hidden to the tray (#94), UWP Activate()
                // alone won't un-hide the Win32-level SW_HIDE — ask the helper
                // to re-show it. Harmless no-op when it wasn't hidden.
                if (IsConnected && PipeClient != null)
                {
                    try
                    {
                        PipeClient.SendValueSet(new ValueSet { { "ShowAppWindow", true } });
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"ShowAppWindow request on launch failed: {ex.Message}");
                    }
                }

                // #94: with the confirmAppClose capability, X on this desktop
                // window raises CloseRequested instead of terminating outright.
                // While a Game Bar widget is alive we hide this window to the
                // tray, so the process — and the widget — survive.
                if (!desktopCloseRequestedRegistered)
                {
                    SystemNavigationManagerPreview.GetForCurrentView().CloseRequested += DesktopView_CloseRequested;
                    desktopCloseRequestedRegistered = true;
                }
            }
        }

        private bool desktopCloseRequestedRegistered;

        // #94: UWP does not count the Game Bar-hosted widget view as visible,
        // so whenever our last desktop window goes away the process suspends —
        // and if the overlay is displaying the widget at that moment, Game Bar
        // watches its widget's process suspend mid-session and shows
        // "Something went wrong with this widget". An ExtendedExecutionSession
        // ("run while minimized") held while a widget is alive prevents that
        // suspend. Revocation (battery saver / resource pressure) degrades to
        // the old behavior: suspend, error card, Game Bar relaunches us.
        private ExtendedExecutionSession widgetKeepAliveSession;

        private async Task EnsureWidgetKeepAliveSessionAsync(string reason)
        {
            if (widgetKeepAliveSession != null)
            {
                return;
            }

            try
            {
                var session = new ExtendedExecutionSession
                {
                    Reason = ExtendedExecutionReason.Unspecified,
                    Description = "Keep the Game Bar widget connected while no desktop window is visible",
                };
                session.Revoked += WidgetKeepAliveSession_Revoked;
                var result = await session.RequestExtensionAsync();
                if (result == ExtendedExecutionResult.Allowed)
                {
                    widgetKeepAliveSession = session;
                    Logger.Info($"Widget keep-alive extended execution granted ({reason})");
                }
                else
                {
                    session.Dispose();
                    Logger.Warn($"Widget keep-alive extended execution DENIED ({reason}) — widget will suspend when no window is visible");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Widget keep-alive extended execution request threw ({reason}): {ex.Message}");
            }
        }

        private void WidgetKeepAliveSession_Revoked(object sender, ExtendedExecutionRevokedEventArgs args)
        {
            Logger.Warn($"Widget keep-alive extended execution revoked: {args.Reason} — widget may suspend until re-granted");
            try { widgetKeepAliveSession?.Dispose(); } catch { }
            widgetKeepAliveSession = null;
        }

        private void ReleaseWidgetKeepAliveSession(string reason)
        {
            if (widgetKeepAliveSession == null)
            {
                return;
            }

            try { widgetKeepAliveSession.Dispose(); } catch { }
            widgetKeepAliveSession = null;
            Logger.Info($"Widget keep-alive extended execution released ({reason})");
        }

        private async void DesktopView_CloseRequested(object sender, SystemNavigationCloseRequestedPreviewEventArgs e)
        {
            // No widget alive → this is a plain desktop app close; let it through.
            if (gamingXboxGameBarWidget == null)
            {
                Logger.Info("Desktop window close: no active widget, closing normally");
                return;
            }

            var deferral = e.GetDeferral();
            try
            {
                // MINIMIZE instead of close. Closing (even via
                // TryConsolidateAsync) leaves zero visible views — the widget's
                // Game Bar-hosted view doesn't count — and Windows suspends the
                // process, revoking extended execution with SystemPolicy at the
                // same instant (verified on 2574). A minimized window is the
                // state our keep-alive ExtendedExecutionSession is designed to
                // survive, so the process keeps running and the widget stays
                // connected. The UWP view can't minimize itself; the full-trust
                // helper does it via ShowWindow on our ApplicationFrameHost
                // window. (Full hide-to-tray is unwinnable in-process — the
                // frame host re-presents any externally-hidden window while its
                // CoreWindow lives; see User32.HideAppFrameWindow for the
                // 2576–2580 field-test ladder. Multi-instance is Game
                // Bar-incompatible per the 2582 test.) Restored from the
                // taskbar, the tray icon ("Open GoTweaks" / double-click), or a
                // Start relaunch.
                if (IsConnected && PipeClient != null)
                {
                    e.Handled = true;
                    var message = new ValueSet { { "HideAppWindow", true } };
                    PipeClient.SendValueSet(message);
                    Logger.Info("Desktop window close intercepted while widget active: requested minimize via helper");
                }
                else
                {
                    // No helper to minimize us — fall back to consolidating the
                    // view. The process will suspend and Game Bar will relaunch
                    // the widget (pre-2573 behavior), which beats a window that
                    // refuses to close.
                    e.Handled = true;
                    bool consolidated = await ApplicationView.GetForCurrentView().TryConsolidateAsync();
                    Logger.Info($"Desktop window close intercepted (helper unavailable): consolidated={consolidated}");
                    if (!consolidated)
                    {
                        e.Handled = false;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"CloseRequested handling threw: {ex.Message}; closing normally");
                e.Handled = false;
            }
            finally
            {
                deferral.Complete();
            }
        }

        /// <summary>
        /// Invoked when Navigation to a certain page fails
        /// </summary>
        /// <param name="sender">The Frame which failed navigation</param>
        /// <param name="e">Details about the navigation failure</param>
        void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            throw new Exception("Failed to load Page " + e.SourcePageType.FullName);
        }

        /// <summary>
        /// Invoked when application execution is being suspended.  Normally we
        /// wouldn't know if the app was being terminated or just suspended at this
        /// point. However, the app will never be suspended if Game Bar has an
        /// active widget connection to it, so if you see this call it's safe to
        /// cleanup any widget related objects. Keep in mind if all widgets are closed
        /// and you have a foreground window for your app, this could still result in 
        /// suspend or terminate. Regardless, it should always be safe to cleanup
        /// your widget related objects.
        /// </summary>
        /// <param name="sender">The source of the suspend request.</param>
        /// <param name="e">Details about the suspend request.</param>
        private void OnSuspending(object sender, SuspendingEventArgs e)
        {
            Logger.Info("App suspending");
            var deferral = e.SuspendingOperation.GetDeferral();

            // Don't manually complete widget activity here - let the widget's disconnect handler manage it
            // to avoid race conditions and double-disposal
            gamingXboxGameBarWidget = null;
            gamingWidget = null;
            gamingSettingsXboxGameBarWidget = null;
            gamingWidgetSettings = null;

            deferral.Complete();
        }
    }
}
