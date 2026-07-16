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

        private void SubscribeToSettingsChanges()
        {
            // Performance settings
            // Sliders use the debounced save so a single drag coalesces to one
            // profile-storage write instead of dozens; toggles/combos stay immediate.
            TDPSlider.ValueChanged += SettingChangedDebounced;
            CPUBoostToggle.Toggled += SettingChanged;
            CPUEPPSlider.ValueChanged += SettingChangedDebounced;
            FPSLimitToggle.Toggled += FPSLimitToggle_Toggled;
            FPSLimitSlider.ValueChanged += FPSLimitSlider_ValueChanged;

            // Graphics settings (HDR and Resolution for profile feature)
            HDRToggle.Toggled += SettingChanged;
            ResolutionComboBox.SelectionChanged += SettingChanged;

            // AMD settings
            AMDFluidMotionFrameToggle.Toggled += SettingChanged;
            AMDRadeonSuperResolutionToggle.Toggled += AMDRadeonSuperResolutionToggle_Toggled;
            AMDRadeonSuperResolutionSharpnessSlider.ValueChanged += SettingChangedDebounced;
            AMDImageSharpeningToggle.Toggled += AMDImageSharpeningToggle_Toggled;
            AMDImageSharpeningSlider.ValueChanged += SettingChangedDebounced;
            AMDRadeonAntiLagToggle.Toggled += AMDRadeonAntiLagToggle_Toggled;
            AMDRadeonBoostToggle.Toggled += AMDRadeonBoostToggle_Toggled;
            AMDRadeonBoostResolutionSlider.ValueChanged += SettingChangedDebounced;
            AMDRadeonChillToggle.Toggled += AMDRadeonChillToggle_Toggled;
            AMDRadeonChillMinFPSSlider.ValueChanged += SettingChangedDebounced;
            AMDRadeonChillMaxFPSSlider.ValueChanged += SettingChangedDebounced;

            // Legion controller button mapping settings
            InitializeButtonMappingEvents("Y1");
            InitializeButtonMappingEvents("Y2");
            InitializeButtonMappingEvents("Y3");
            InitializeButtonMappingEvents("M1");
            InitializeButtonMappingEvents("M2");
            InitializeButtonMappingEvents("M3");
            InitializeButtonMappingEvents("Desktop");
            InitializeButtonMappingEvents("Page");

            if (LegionNintendoLayoutToggle != null)
                LegionNintendoLayoutToggle.Toggled += LegionNintendoLayout_Toggled;
            if (LegionDesktopControlsToggle != null)
                LegionDesktopControlsToggle.Toggled += LegionDesktopControls_Toggled;
            if (LegionVibrationComboBox != null)
                LegionVibrationComboBox.SelectionChanged += ControllerSettingChanged;
            if (LegionVibrationModeComboBox != null)
                LegionVibrationModeComboBox.SelectionChanged += ControllerSettingChanged;

            // Gyro settings (per-game profile). Continuous-drag sliders use the
            // debounced save; discrete combos/toggles stay immediate.
            if (LegionGyroTargetComboBox != null)
                LegionGyroTargetComboBox.SelectionChanged += ControllerSettingChanged;
            if (LegionGyroSensitivityXSlider != null)
                LegionGyroSensitivityXSlider.ValueChanged += ControllerSliderSettingChanged;
            if (LegionGyroSensitivityYSlider != null)
                LegionGyroSensitivityYSlider.ValueChanged += ControllerSliderSettingChanged;
            if (LegionGyroInvertXToggle != null)
                LegionGyroInvertXToggle.Toggled += ControllerSettingChanged;
            if (LegionGyroInvertYToggle != null)
                LegionGyroInvertYToggle.Toggled += ControllerSettingChanged;
            if (LegionGyroMappingTypeComboBox != null)
                LegionGyroMappingTypeComboBox.SelectionChanged += ControllerSettingChanged;
            if (LegionGyroActivationModeComboBox != null)
                LegionGyroActivationModeComboBox.SelectionChanged += ControllerSettingChanged;
            if (LegionGyroActivationButtonComboBox != null)
                LegionGyroActivationButtonComboBox.SelectionChanged += ControllerSettingChanged;

            // Advanced gyro settings (per-game profile)
            if (LegionGyroDeadzoneSlider != null)
                LegionGyroDeadzoneSlider.ValueChanged += ControllerSliderSettingChanged;

            // Stick deadzones (per-game profile)
            if (LegionLeftStickDeadzoneSlider != null)
                LegionLeftStickDeadzoneSlider.ValueChanged += ControllerSliderSettingChanged;
            if (LegionRightStickDeadzoneSlider != null)
                LegionRightStickDeadzoneSlider.ValueChanged += ControllerSliderSettingChanged;

            // Trigger travel (per-game profile)
            if (LegionLeftTriggerStartSlider != null)
                LegionLeftTriggerStartSlider.ValueChanged += ControllerSliderSettingChanged;
            if (LegionLeftTriggerEndSlider != null)
                LegionLeftTriggerEndSlider.ValueChanged += ControllerSliderSettingChanged;
            if (LegionRightTriggerStartSlider != null)
                LegionRightTriggerStartSlider.ValueChanged += ControllerSliderSettingChanged;
            if (LegionRightTriggerEndSlider != null)
                LegionRightTriggerEndSlider.ValueChanged += ControllerSliderSettingChanged;
            if (LegionHairTriggersToggle != null)
                LegionHairTriggersToggle.Toggled += LegionHairTriggers_Toggled;

            // Joystick as mouse (per-game profile)
            if (LegionJoystickAsMouseComboBox != null)
                LegionJoystickAsMouseComboBox.SelectionChanged += ControllerSettingChanged;
            if (LegionJoystickMouseSensSlider != null)
                LegionJoystickMouseSensSlider.ValueChanged += ControllerSliderSettingChanged;

            // Lighting settings (per-game profile).
            // NOTE: LegionLightModeComboBox / LegionColorPicker / LegionBrightnessSlider /
            // LegionSpeedSlider are ALREADY wired to their save handlers in XAML
            // (SelectionChanged/ColorChanged/ValueChanged), so subscribing here too would
            // double-save + double-send IPC on every drag tick. Only PowerLightToggle needs
            // a code-behind wire (it has no XAML handler).
            if (LegionPowerLightToggle != null)
                LegionPowerLightToggle.Toggled += ControllerSettingChanged;

            // Gamepad button remapping (per-game profile)
            if (LegionGamepadButtonSelectorComboBox != null)
                LegionGamepadButtonSelectorComboBox.SelectionChanged += LegionGamepadButtonSelector_SelectionChanged;
            if (LegionGamepadTypeComboBox != null)
                LegionGamepadTypeComboBox.SelectionChanged += LegionGamepadMapping_Changed;
            if (LegionGamepadActionComboBox != null)
                LegionGamepadActionComboBox.SelectionChanged += LegionGamepadMapping_Changed;
            if (LegionGamepadMouseComboBox != null)
                LegionGamepadMouseComboBox.SelectionChanged += LegionGamepadMapping_Changed;
            if (LegionGamepadKeyComboBox != null)
                LegionGamepadKeyComboBox.SelectionChanged += LegionGamepadKey_SelectionChanged;
            if (LegionGamepadResetAllButton != null)
                LegionGamepadResetAllButton.Click += LegionGamepadResetAll_Click;


            foreach (string buttonName in LegionRemapButtonNames)
            {
                UpdateButtonGamepadComboControls(buttonName);
            }
        }

        private void SettingChanged(object sender, object e)
        {
            // Update Sticky TDP target if TDP slider changed and Sticky TDP is enabled
            // But ONLY if the change is from the user, not from helper sync/updates
            if (sender == TDPSlider && StickyTDPToggle?.IsOn == true && !isApplyingHelperUpdate)
            {
                targetTDPLimit = TDPSlider.Value;
                Logger.Info($"Sticky TDP target updated to: {targetTDPLimit}W (user change)");
            }

            // Don't save during profile loading, switching, initial sync, when helper is updating values,
            // when any property is syncing from helper pipe, or when Default Game Profile is active
            if (isLoadingProfile || isSwitchingProfile || isApplyingHelperUpdate || isInitialSync
                || WidgetSliderProperty.HelperSyncCount > 0 || defaultGameProfileEnabled?.Value == true)
            {
                Logger.Debug($"Skipping auto-save during profile operation (loading={isLoadingProfile}, switching={isSwitchingProfile}, helperUpdate={isApplyingHelperUpdate}, initialSync={isInitialSync}, defaultGameProfile={defaultGameProfileEnabled?.Value})");
                return;
            }

            // Auto-save to current profile
            SaveCurrentSettingsToProfile(currentProfileName);
        }

        // Coalesces rapid slider-drag saves into a single profile-storage write ~300ms
        // after the drag settles. A single drag would otherwise fire SaveCurrentSettingsToProfile
        // dozens of times per second.
        private DispatcherTimer settingsSaveDebounceTimer;

        private void SettingChangedDebounced(object sender, object e)
        {
            // Sticky TDP target tracking must stay immediate (not debounced).
            if (sender == TDPSlider && StickyTDPToggle?.IsOn == true && !isApplyingHelperUpdate)
            {
                targetTDPLimit = TDPSlider.Value;
            }

            if (isLoadingProfile || isSwitchingProfile || isApplyingHelperUpdate || isInitialSync
                || WidgetSliderProperty.HelperSyncCount > 0 || defaultGameProfileEnabled?.Value == true)
            {
                return;
            }

            if (settingsSaveDebounceTimer == null)
            {
                settingsSaveDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                settingsSaveDebounceTimer.Tick += (s, ev) =>
                {
                    settingsSaveDebounceTimer.Stop();
                    // Re-check guards at fire time (state may have changed during the debounce window).
                    if (isLoadingProfile || isSwitchingProfile || isApplyingHelperUpdate || isInitialSync
                        || WidgetSliderProperty.HelperSyncCount > 0 || defaultGameProfileEnabled?.Value == true)
                    {
                        return;
                    }
                    SaveCurrentSettingsToProfile(currentProfileName);
                };
            }
            settingsSaveDebounceTimer.Stop();
            settingsSaveDebounceTimer.Start();
        }

    }
}
