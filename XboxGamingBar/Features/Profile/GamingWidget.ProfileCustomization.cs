using Microsoft.Gaming.XboxGameBar;
using Microsoft.Gaming.XboxGameBar.Input;
using Microsoft.UI.Xaml.Controls;
using NLog;
using Shared.Data;
using Shared.Utilities;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Data.Json;
using Windows.Foundation;
using Windows.Foundation.Metadata;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;
using Windows.System.Power;
using Windows.Storage;
using Windows.System;
using Windows.UI.Xaml.Input;
using System.Runtime.InteropServices;
using Windows.UI;
using XboxGamingBar.Data;
using XboxGamingBar.Event;
using XboxGamingBar.IPC;
using XboxGamingBar.QuickSettings;
using Shared.Enums;

namespace XboxGamingBar
{
    public sealed partial class GamingWidget
    {

        private void LoadProfileCustomizationSettings()
        {
            isLoadingProfileSettings = true;
            try
            {
                var settings = ApplicationData.Current.LocalSettings;

                // Load values from settings
                _saveTDP = settings.Values.ContainsKey("ProfileSaveTDP") ? (bool)settings.Values["ProfileSaveTDP"] : true;
                _saveCPUBoost = settings.Values.ContainsKey("ProfileSaveCPUBoost") ? (bool)settings.Values["ProfileSaveCPUBoost"] : true;
                _saveCPUEPP = settings.Values.ContainsKey("ProfileSaveCPUEPP") ? (bool)settings.Values["ProfileSaveCPUEPP"] : true;
                _saveAMDFeatures = settings.Values.ContainsKey("ProfileSaveAMDFeatures") ? (bool)settings.Values["ProfileSaveAMDFeatures"] : false;
                _saveFPSLimit = settings.Values.ContainsKey("ProfileSaveFPSLimit") ? (bool)settings.Values["ProfileSaveFPSLimit"] : true;
                _saveAutoTDP = settings.Values.ContainsKey("ProfileSaveAutoTDP") ? (bool)settings.Values["ProfileSaveAutoTDP"] : true;
                _saveOSPowerMode = settings.Values.ContainsKey("ProfileSaveOSPowerMode") ? (bool)settings.Values["ProfileSaveOSPowerMode"] : true;
                // HDR and Resolution - check for new separate settings first, fall back to combined setting for migration
                if (settings.Values.ContainsKey("ProfileSaveHDR"))
                {
                    _saveHDR = (bool)settings.Values["ProfileSaveHDR"];
                }
                else if (settings.Values.ContainsKey("ProfileSaveHDRResolution"))
                {
                    _saveHDR = (bool)settings.Values["ProfileSaveHDRResolution"]; // Migrate from combined setting
                }
                else
                {
                    _saveHDR = false;
                }

                if (settings.Values.ContainsKey("ProfileSaveResolution"))
                {
                    _saveResolution = (bool)settings.Values["ProfileSaveResolution"];
                }
                else if (settings.Values.ContainsKey("ProfileSaveHDRResolution"))
                {
                    _saveResolution = (bool)settings.Values["ProfileSaveHDRResolution"]; // Migrate from combined setting
                }
                else
                {
                    _saveResolution = false;
                }

                _saveRefreshRate = settings.Values.ContainsKey("ProfileSaveRefreshRate") ? (bool)settings.Values["ProfileSaveRefreshRate"] : false;
                _saveStickyTDP = settings.Values.ContainsKey("ProfileSaveStickyTDP") ? (bool)settings.Values["ProfileSaveStickyTDP"] : false;
                _saveOverlayLevel = settings.Values.ContainsKey("ProfileSaveOverlayLevel") ? (bool)settings.Values["ProfileSaveOverlayLevel"] : false;
                _saveNintendoLayout = settings.Values.ContainsKey("ProfileSaveNintendoLayout") ? (bool)settings.Values["ProfileSaveNintendoLayout"] : false;
                _saveVibration = settings.Values.ContainsKey("ProfileSaveVibration") ? (bool)settings.Values["ProfileSaveVibration"] : false;
                _saveLighting = settings.Values.ContainsKey("ProfileSaveLighting") ? (bool)settings.Values["ProfileSaveLighting"] : false;
                _saveButtonMappings = settings.Values.ContainsKey("ProfileSaveButtonMappings") ? (bool)settings.Values["ProfileSaveButtonMappings"] : false;

                // Update UI checkboxes
                if (ProfileSaveTDPCheckBox != null) ProfileSaveTDPCheckBox.IsChecked = _saveTDP;
                if (ProfileSaveCPUBoostCheckBox != null) ProfileSaveCPUBoostCheckBox.IsChecked = _saveCPUBoost;
                if (ProfileSaveCPUEPPCheckBox != null) ProfileSaveCPUEPPCheckBox.IsChecked = _saveCPUEPP;
                if (ProfileSaveAMDFeaturesCheckBox != null) ProfileSaveAMDFeaturesCheckBox.IsChecked = _saveAMDFeatures;
                if (ProfileSaveFPSLimitCheckBox != null) ProfileSaveFPSLimitCheckBox.IsChecked = _saveFPSLimit;
                if (ProfileSaveAutoTDPCheckBox != null) ProfileSaveAutoTDPCheckBox.IsChecked = _saveAutoTDP;
                if (ProfileSaveOSPowerModeCheckBox != null) ProfileSaveOSPowerModeCheckBox.IsChecked = _saveOSPowerMode;
                if (ProfileSaveHDRCheckBox != null) ProfileSaveHDRCheckBox.IsChecked = _saveHDR;
                if (ProfileSaveResolutionCheckBox != null) ProfileSaveResolutionCheckBox.IsChecked = _saveResolution;
                if (ProfileSaveRefreshRateCheckBox != null) ProfileSaveRefreshRateCheckBox.IsChecked = _saveRefreshRate;
                if (ProfileSaveStickyTDPCheckBox != null) ProfileSaveStickyTDPCheckBox.IsChecked = _saveStickyTDP;
                if (ProfileSaveOverlayLevelCheckBox != null) ProfileSaveOverlayLevelCheckBox.IsChecked = _saveOverlayLevel;
                if (ProfileSaveNintendoLayoutCheckBox != null) ProfileSaveNintendoLayoutCheckBox.IsChecked = _saveNintendoLayout;
                if (ProfileSaveVibrationCheckBox != null) ProfileSaveVibrationCheckBox.IsChecked = _saveVibration;
                if (ProfileSaveLightingCheckBox != null) ProfileSaveLightingCheckBox.IsChecked = _saveLighting;
                if (ProfileSaveButtonMappingsCheckBox != null) ProfileSaveButtonMappingsCheckBox.IsChecked = _saveButtonMappings;
            }
            finally
            {
                isLoadingProfileSettings = false;
            }
        }

        private void SaveProfileCustomizationSettings()
        {
            if (isLoadingProfileSettings) return;

            var settings = ApplicationData.Current.LocalSettings;
            settings.Values["ProfileSaveTDP"] = ProfileSaveTDPCheckBox?.IsChecked ?? true;
            settings.Values["ProfileSaveCPUBoost"] = ProfileSaveCPUBoostCheckBox?.IsChecked ?? true;
            settings.Values["ProfileSaveCPUEPP"] = ProfileSaveCPUEPPCheckBox?.IsChecked ?? true;
            settings.Values["ProfileSaveAMDFeatures"] = ProfileSaveAMDFeaturesCheckBox?.IsChecked ?? false;
            settings.Values["ProfileSaveFPSLimit"] = ProfileSaveFPSLimitCheckBox?.IsChecked ?? true;
            settings.Values["ProfileSaveAutoTDP"] = ProfileSaveAutoTDPCheckBox?.IsChecked ?? true;
            settings.Values["ProfileSaveOSPowerMode"] = ProfileSaveOSPowerModeCheckBox?.IsChecked ?? true;
            settings.Values["ProfileSaveHDR"] = ProfileSaveHDRCheckBox?.IsChecked ?? false;
            settings.Values["ProfileSaveResolution"] = ProfileSaveResolutionCheckBox?.IsChecked ?? false;
            settings.Values["ProfileSaveRefreshRate"] = ProfileSaveRefreshRateCheckBox?.IsChecked ?? false;
            settings.Values["ProfileSaveStickyTDP"] = ProfileSaveStickyTDPCheckBox?.IsChecked ?? false;
            settings.Values["ProfileSaveOverlayLevel"] = ProfileSaveOverlayLevelCheckBox?.IsChecked ?? false;
            settings.Values["ProfileSaveNintendoLayout"] = ProfileSaveNintendoLayoutCheckBox?.IsChecked ?? false;
            settings.Values["ProfileSaveVibration"] = ProfileSaveVibrationCheckBox?.IsChecked ?? false;
            settings.Values["ProfileSaveLighting"] = ProfileSaveLightingCheckBox?.IsChecked ?? false;
            settings.Values["ProfileSaveButtonMappings"] = ProfileSaveButtonMappingsCheckBox?.IsChecked ?? false;
        }

        private void ProfileSettingsCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (isLoadingProfileSettings) return;

            // Update backing fields from UI checkboxes
            SyncProfileSettingsBackingFields();
            SaveProfileCustomizationSettings();
            SendProfileSaveFlagsToHelper();
            // Re-render the profile tables immediately so the row visibility (which
            // categories are shown/hidden) reflects the new Save* selection without
            // requiring the widget to be closed and reopened.
            UpdateProfileDisplay();
            Logger.Info($"Profile customization settings updated");
        }

        /// <summary>
        /// Pushes the current per-setting save flags to the helper so it can route writes:
        /// true => write to CurrentProfile (per-game if active, else global), false => write to
        /// GlobalProfile regardless of the active profile. Sent on startup and on any checkbox
        /// change. Helper stores a snapshot and consults it from AutoTDP / Legion save handlers.
        /// </summary>
        internal void SendProfileSaveFlagsToHelper()
        {
            try
            {
                if (!App.IsConnected) return;

                var jsonObj = new Windows.Data.Json.JsonObject();
                jsonObj["TDP"] = Windows.Data.Json.JsonValue.CreateBooleanValue(_saveTDP);
                jsonObj["CPUBoost"] = Windows.Data.Json.JsonValue.CreateBooleanValue(_saveCPUBoost);
                jsonObj["CPUEPP"] = Windows.Data.Json.JsonValue.CreateBooleanValue(_saveCPUEPP);
                jsonObj["AMDFeatures"] = Windows.Data.Json.JsonValue.CreateBooleanValue(_saveAMDFeatures);
                jsonObj["FPSLimit"] = Windows.Data.Json.JsonValue.CreateBooleanValue(_saveFPSLimit);
                jsonObj["AutoTDP"] = Windows.Data.Json.JsonValue.CreateBooleanValue(_saveAutoTDP);
                jsonObj["OSPowerMode"] = Windows.Data.Json.JsonValue.CreateBooleanValue(_saveOSPowerMode);
                jsonObj["HDR"] = Windows.Data.Json.JsonValue.CreateBooleanValue(_saveHDR);
                jsonObj["Resolution"] = Windows.Data.Json.JsonValue.CreateBooleanValue(_saveResolution);
                jsonObj["RefreshRate"] = Windows.Data.Json.JsonValue.CreateBooleanValue(_saveRefreshRate);
                jsonObj["StickyTDP"] = Windows.Data.Json.JsonValue.CreateBooleanValue(_saveStickyTDP);
                jsonObj["OverlayLevel"] = Windows.Data.Json.JsonValue.CreateBooleanValue(_saveOverlayLevel);
                jsonObj["NintendoLayout"] = Windows.Data.Json.JsonValue.CreateBooleanValue(_saveNintendoLayout);
                jsonObj["Vibration"] = Windows.Data.Json.JsonValue.CreateBooleanValue(_saveVibration);
                jsonObj["Lighting"] = Windows.Data.Json.JsonValue.CreateBooleanValue(_saveLighting);
                jsonObj["ButtonMappings"] = Windows.Data.Json.JsonValue.CreateBooleanValue(_saveButtonMappings);

                var request = new Windows.Foundation.Collections.ValueSet
                {
                    { "Command", (int)Shared.Enums.Command.Set },
                    { "Function", (int)Shared.Enums.Function.ProfileSaveFlags },
                    { "Content", jsonObj.Stringify() },
                };
                App.PipeClient?.SendValueSet(request);
                Logger.Info("Sent ProfileSaveFlags to helper");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error sending ProfileSaveFlags: {ex.Message}");
            }
        }

        /// <summary>
        /// Sync backing fields from UI checkboxes. Called when checkboxes change.
        /// This ensures the backing fields are always in sync with the UI.
        /// </summary>
        private void SyncProfileSettingsBackingFields()
        {
            _saveTDP = ProfileSaveTDPCheckBox?.IsChecked ?? true;
            _saveCPUBoost = ProfileSaveCPUBoostCheckBox?.IsChecked ?? true;
            _saveCPUEPP = ProfileSaveCPUEPPCheckBox?.IsChecked ?? true;
            _saveAMDFeatures = ProfileSaveAMDFeaturesCheckBox?.IsChecked ?? false;
            _saveFPSLimit = ProfileSaveFPSLimitCheckBox?.IsChecked ?? true;
            _saveAutoTDP = ProfileSaveAutoTDPCheckBox?.IsChecked ?? true;
            _saveOSPowerMode = ProfileSaveOSPowerModeCheckBox?.IsChecked ?? true;
            _saveHDR = ProfileSaveHDRCheckBox?.IsChecked ?? false;
            _saveResolution = ProfileSaveResolutionCheckBox?.IsChecked ?? false;
            _saveRefreshRate = ProfileSaveRefreshRateCheckBox?.IsChecked ?? false;
            _saveStickyTDP = ProfileSaveStickyTDPCheckBox?.IsChecked ?? false;
            _saveOverlayLevel = ProfileSaveOverlayLevelCheckBox?.IsChecked ?? false;
            _saveNintendoLayout = ProfileSaveNintendoLayoutCheckBox?.IsChecked ?? false;
            _saveVibration = ProfileSaveVibrationCheckBox?.IsChecked ?? false;
            _saveLighting = ProfileSaveLightingCheckBox?.IsChecked ?? false;
            _saveButtonMappings = ProfileSaveButtonMappingsCheckBox?.IsChecked ?? false;
        }

    }
}
