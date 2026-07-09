using System;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;

namespace XboxGamingBar
{
    /// <summary>
    /// Custom entry point (task #12): the desktop app and the Game Bar widget
    /// run as SEPARATE processes so closing the app window is a real close
    /// that cannot suspend or kill the widget's process (the #94 family — a
    /// UWP process suspends whenever it has no visible desktop windows, and
    /// the Game Bar-hosted widget view does not count as visible).
    ///
    /// Routing: every activation lands in a fresh instance (the manifest
    /// declares desktop4:SupportsMultipleInstances); we immediately claim or
    /// redirect based on activation kind:
    ///   - ms-gamebarwidget protocol activations → the "widget" instance
    ///     (main widget, settings widget, and all Game Bar re-activations
    ///     share one process, preserving the App-level widget statics);
    ///   - everything else (Start menu launch, tiles) → the "app" instance.
    ///
    /// Requires DISABLE_XAML_GENERATED_MAIN in the project's DefineConstants.
    /// </summary>
    public static class Program
    {
        private static void Main(string[] args)
        {
            string key = "app";
            try
            {
                var activatedArgs = AppInstance.GetActivatedEventArgs();
                if (activatedArgs is ProtocolActivatedEventArgs protocolArgs
                    && protocolArgs.Uri != null
                    && protocolArgs.Uri.Scheme.StartsWith("ms-gamebar", StringComparison.OrdinalIgnoreCase))
                {
                    key = "widget";
                }
            }
            catch
            {
                // Can't inspect the activation — treat as an app launch.
            }

            var instance = AppInstance.FindOrRegisterInstanceForKey(key);
            if (instance.IsCurrentInstance)
            {
                global::Windows.UI.Xaml.Application.Start((p) => new App());
            }
            else
            {
                // An instance for this role already exists — hand the
                // activation to it and exit this transient process.
                instance.RedirectActivationTo();
            }
        }
    }
}
