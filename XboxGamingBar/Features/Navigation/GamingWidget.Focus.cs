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

        private void RegisterCardFocusHandlers()
        {
            // Get brushes from resources
            cardDefaultBorderBrush = (SolidColorBrush)Resources["CardBorderBrush"];
            cardFocusBorderBrush = (SolidColorBrush)Resources["CardFocusBorderBrush"];

            // Register focus handler on navigation items to clear card focus when tabs get focus
            foreach (var item in MainNavPanel.Children)
            {
                if (item is RadioButton radioButton)
                {
                    radioButton.GotFocus += NavItem_GotFocus;
                }
            }

            // Register GotFocus/LostFocus on interactive controls
            // Performance tab - Active Profile card
            PerGameProfileToggle.GotFocus += Control_GotFocus;
            PerGameProfileToggle.LostFocus += Control_LostFocus;

            // Performance tab - Default Game Profile card
            DefaultProfileToggle.GotFocus += Control_GotFocus;
            DefaultProfileToggle.LostFocus += Control_LostFocus;

            // Performance tab - Performance Overlay card
            PerformanceOverlayComboBox.GotFocus += Control_GotFocus;
            PerformanceOverlayComboBox.LostFocus += Control_LostFocus;

            // Performance tab - TDP Mode card (Legion only)
            TDPModeComboBox.GotFocus += Control_GotFocus;
            TDPModeComboBox.LostFocus += Control_LostFocus;

            // Performance tab - TDP card
            TDPSlider.GotFocus += Control_GotFocus;
            TDPSlider.LostFocus += Control_LostFocus;

            // Performance tab - AutoTDP card
            AutoTDPToggle.GotFocus += Control_GotFocus;
            AutoTDPToggle.LostFocus += Control_LostFocus;
            AutoTDPTargetFPSSlider.GotFocus += Control_GotFocus;
            AutoTDPTargetFPSSlider.LostFocus += Control_LostFocus;

            // Performance tab - CPU Boost card
            CPUBoostToggle.GotFocus += Control_GotFocus;
            CPUBoostToggle.LostFocus += Control_LostFocus;

            // Performance tab - CPU EPP card
            CPUEPPSlider.GotFocus += Control_GotFocus;
            CPUEPPSlider.LostFocus += Control_LostFocus;

            // Performance tab - CPU State card
            MinCPUStateComboBox.GotFocus += Control_GotFocus;
            MinCPUStateComboBox.LostFocus += Control_LostFocus;
            MaxCPUStateComboBox.GotFocus += Control_GotFocus;
            MaxCPUStateComboBox.LostFocus += Control_LostFocus;

            // Performance tab - FPS Limit card
            FPSLimitToggle.GotFocus += Control_GotFocus;
            FPSLimitToggle.LostFocus += Control_LostFocus;
            FPSLimitSlider.GotFocus += Control_GotFocus;
            FPSLimitSlider.LostFocus += Control_LostFocus;

            // Performance tab - OS Power Mode card
            OSPowerModeComboBox.GotFocus += Control_GotFocus;
            OSPowerModeComboBox.LostFocus += Control_LostFocus;

            // Profiles tab - Power Source Profile card
            PowerSourceProfileToggle.GotFocus += Control_GotFocus;
            PowerSourceProfileToggle.LostFocus += Control_LostFocus;

            // Graphics tab - Resolution card
            ResolutionComboBox.GotFocus += Control_GotFocus;
            ResolutionComboBox.LostFocus += Control_LostFocus;

            // Graphics tab - Refresh Rate card
            RefreshRatesComboBox.GotFocus += Control_GotFocus;
            RefreshRatesComboBox.LostFocus += Control_LostFocus;

            // Graphics tab - HDR card
            HDRToggle.GotFocus += Control_GotFocus;
            HDRToggle.LostFocus += Control_LostFocus;

            // Graphics tab - AMD cards
            AMDRadeonSuperResolutionToggle.GotFocus += Control_GotFocus;
            AMDRadeonSuperResolutionToggle.LostFocus += Control_LostFocus;
            AMDRadeonSuperResolutionSharpnessSlider.GotFocus += Control_GotFocus;
            AMDRadeonSuperResolutionSharpnessSlider.LostFocus += Control_LostFocus;

            // Graphics tab - Image Sharpening card
            AMDImageSharpeningToggle.GotFocus += Control_GotFocus;
            AMDImageSharpeningToggle.LostFocus += Control_LostFocus;
            AMDImageSharpeningSlider.GotFocus += Control_GotFocus;
            AMDImageSharpeningSlider.LostFocus += Control_LostFocus;

            // Graphics tab - Color Settings card
            ColorSettingsExpandButton.GotFocus += Control_GotFocus;
            ColorSettingsExpandButton.LostFocus += Control_LostFocus;
            AMDDisplayBrightnessSlider.GotFocus += Control_GotFocus;
            AMDDisplayBrightnessSlider.LostFocus += Control_LostFocus;
            AMDDisplayContrastSlider.GotFocus += Control_GotFocus;
            AMDDisplayContrastSlider.LostFocus += Control_LostFocus;
            AMDDisplaySaturationSlider.GotFocus += Control_GotFocus;
            AMDDisplaySaturationSlider.LostFocus += Control_LostFocus;
            AMDDisplayTemperatureSlider.GotFocus += Control_GotFocus;
            AMDDisplayTemperatureSlider.LostFocus += Control_LostFocus;
            AMDFluidMotionFrameToggle.GotFocus += Control_GotFocus;
            AMDFluidMotionFrameToggle.LostFocus += Control_LostFocus;
            AMDRadeonAntiLagToggle.GotFocus += Control_GotFocus;
            AMDRadeonAntiLagToggle.LostFocus += Control_LostFocus;
            AMDRadeonBoostToggle.GotFocus += Control_GotFocus;
            AMDRadeonBoostToggle.LostFocus += Control_LostFocus;
            AMDRadeonBoostResolutionSlider.GotFocus += Control_GotFocus;
            AMDRadeonBoostResolutionSlider.LostFocus += Control_LostFocus;
            AMDRadeonChillToggle.GotFocus += Control_GotFocus;
            AMDRadeonChillToggle.LostFocus += Control_LostFocus;
            AMDRadeonChillMinFPSSlider.GotFocus += Control_GotFocus;
            AMDRadeonChillMinFPSSlider.LostFocus += Control_LostFocus;
            AMDRadeonChillMaxFPSSlider.GotFocus += Control_GotFocus;
            AMDRadeonChillMaxFPSSlider.LostFocus += Control_LostFocus;

            // System tab - Profile Settings card (checkboxes have individual focus, not card focus)
            // These use FocusableCheckBoxStyle which shows its own focus visual
            ProfileSaveTDPCheckBox.GotFocus += StandaloneControl_GotFocus;
            ProfileSaveCPUBoostCheckBox.GotFocus += StandaloneControl_GotFocus;
            ProfileSaveCPUEPPCheckBox.GotFocus += StandaloneControl_GotFocus;
            ProfileSaveCPUStateCheckBox.GotFocus += StandaloneControl_GotFocus;
            ProfileSaveAMDFeaturesCheckBox.GotFocus += StandaloneControl_GotFocus;

            // System tab - Sticky TDP card
            StickyTDPToggle.GotFocus += Control_GotFocus;
            StickyTDPToggle.LostFocus += Control_LostFocus;
            StickyTDPIntervalSlider.GotFocus += Control_GotFocus;
            StickyTDPIntervalSlider.LostFocus += Control_LostFocus;

            // System tab - TDP Method card
            TdpMethodComboBox.GotFocus += Control_GotFocus;
            TdpMethodComboBox.LostFocus += Control_LostFocus;

            // System tab - TDP Settings card
            TDPSettingsExpandButton.GotFocus += Control_GotFocus;
            TDPSettingsExpandButton.LostFocus += Control_LostFocus;
            TDPLimitsMinSlider.GotFocus += Control_GotFocus;
            TDPLimitsMinSlider.LostFocus += Control_LostFocus;
            TDPLimitsMaxSlider.GotFocus += Control_GotFocus;
            TDPLimitsMaxSlider.LostFocus += Control_LostFocus;

            // Performance tab - Advanced card (Power Plan controls)
            ACPowerPlanComboBox.GotFocus += Control_GotFocus;
            ACPowerPlanComboBox.LostFocus += Control_LostFocus;
            DCPowerPlanComboBox.GotFocus += Control_GotFocus;
            DCPowerPlanComboBox.LostFocus += Control_LostFocus;
            PowerPlanAutoSwitchToggle.GotFocus += Control_GotFocus;
            PowerPlanAutoSwitchToggle.LostFocus += Control_LostFocus;

            // System tab - OSD Customization card
            OSDCustomizeExpandButton.GotFocus += Control_GotFocus;
            OSDCustomizeExpandButton.LostFocus += Control_LostFocus;

            // System tab - Controller Emulation card
            ControllerEmulationExpandButton.GotFocus += Control_GotFocus;
            ControllerEmulationExpandButton.LostFocus += Control_LostFocus;
            ControllerEmulationInputNotesExpandButton.GotFocus += Control_GotFocus;
            ControllerEmulationInputNotesExpandButton.LostFocus += Control_LostFocus;
            ControllerEmulationEnabledToggle.GotFocus += Control_GotFocus;
            ControllerEmulationEnabledToggle.LostFocus += Control_LostFocus;
            ControllerEmulationImprovedInputToggle.GotFocus += Control_GotFocus;
            ControllerEmulationImprovedInputToggle.LostFocus += Control_LostFocus;
            ControllerEmulationHideStockControllerToggle.GotFocus += Control_GotFocus;
            ControllerEmulationHideStockControllerToggle.LostFocus += Control_LostFocus;
            ControllerEmulationHideTargetComboBox.GotFocus += Control_GotFocus;
            ControllerEmulationHideTargetComboBox.LostFocus += Control_LostFocus;
            ControllerEmulationGyroSourceComboBox.GotFocus += Control_GotFocus;
            ControllerEmulationGyroSourceComboBox.LostFocus += Control_LostFocus;
            ControllerEmulationModeComboBox.GotFocus += Control_GotFocus;
            ControllerEmulationModeComboBox.LostFocus += Control_LostFocus;
            ControllerEmulationGyroActivationModeComboBox.GotFocus += Control_GotFocus;
            ControllerEmulationGyroActivationModeComboBox.LostFocus += Control_LostFocus;
            ControllerEmulationGyroActivationButtonComboBox.GotFocus += Control_GotFocus;
            ControllerEmulationGyroActivationButtonComboBox.LostFocus += Control_LostFocus;
            ControllerEmulationPs4TouchpadToggle.GotFocus += Control_GotFocus;
            ControllerEmulationPs4TouchpadToggle.LostFocus += Control_LostFocus;
            ControllerEmulationLedForwardingToggle.GotFocus += Control_GotFocus;
            ControllerEmulationLedForwardingToggle.LostFocus += Control_LostFocus;
            ControllerEmulationMouseSensitivitySlider.GotFocus += Control_GotFocus;
            ControllerEmulationMouseSensitivitySlider.LostFocus += Control_LostFocus;
            ControllerEmulationMouseThresholdSlider.GotFocus += Control_GotFocus;
            ControllerEmulationMouseThresholdSlider.LostFocus += Control_LostFocus;
            ControllerEmulationMouseAxisComboBox.GotFocus += Control_GotFocus;
            ControllerEmulationMouseAxisComboBox.LostFocus += Control_LostFocus;
            ControllerEmulationMouseInvertXToggle.GotFocus += Control_GotFocus;
            ControllerEmulationMouseInvertXToggle.LostFocus += Control_LostFocus;
            ControllerEmulationMouseInvertYToggle.GotFocus += Control_GotFocus;
            ControllerEmulationMouseInvertYToggle.LostFocus += Control_LostFocus;
            ControllerEmulationMouseGainXSlider.GotFocus += Control_GotFocus;
            ControllerEmulationMouseGainXSlider.LostFocus += Control_LostFocus;
            ControllerEmulationMouseGainYSlider.GotFocus += Control_GotFocus;
            ControllerEmulationMouseGainYSlider.LostFocus += Control_LostFocus;
            StickConversionComboBox.GotFocus += Control_GotFocus;
            StickConversionComboBox.LostFocus += Control_LostFocus;
            StickOrientationV2ComboBox.GotFocus += Control_GotFocus;
            StickOrientationV2ComboBox.LostFocus += Control_LostFocus;
            StickSensitivityV2Slider.GotFocus += Control_GotFocus;
            StickSensitivityV2Slider.LostFocus += Control_LostFocus;
            ControllerEmulationStickInvertXToggle.GotFocus += Control_GotFocus;
            ControllerEmulationStickInvertXToggle.LostFocus += Control_LostFocus;
            ControllerEmulationStickInvertYToggle.GotFocus += Control_GotFocus;
            ControllerEmulationStickInvertYToggle.LostFocus += Control_LostFocus;
            // Min/Max gyro speed, Min/Max output, Power curve, Deadzone, Precision speed,
            // Output mix sliders all removed in #79 round 5 (pipeline matches HC).
            ControllerEmulationStickSelectComboBox.GotFocus += Control_GotFocus;
            ControllerEmulationStickSelectComboBox.LostFocus += Control_LostFocus;
            GyroActivationExpandToggle.GotFocus += Control_GotFocus;
            GyroActivationExpandToggle.LostFocus += Control_LostFocus;
            FeaturesExpandToggle.GotFocus += Control_GotFocus;
            FeaturesExpandToggle.LostFocus += Control_LostFocus;
            JoystickOutputExpandToggle.GotFocus += Control_GotFocus;
            JoystickOutputExpandToggle.LostFocus += Control_LostFocus;
            ControllerEmulationStickOnlyJoystickToggle.GotFocus += Control_GotFocus;
            ControllerEmulationStickOnlyJoystickToggle.LostFocus += Control_LostFocus;
            ControllerEmulationVirtualAbxyLayoutComboBox.GotFocus += Control_GotFocus;
            ControllerEmulationVirtualAbxyLayoutComboBox.LostFocus += Control_LostFocus;

            // System tab - Advanced card
            AdvancedExpandButton.GotFocus += Control_GotFocus;
            AdvancedExpandButton.LostFocus += Control_LostFocus;

            // Scaling tab - Status card buttons
            ShowLosslessScalingWindowButton.GotFocus += Control_GotFocus;
            ShowLosslessScalingWindowButton.LostFocus += Control_LostFocus;
            LaunchLosslessScalingButton.GotFocus += Control_GotFocus;
            LaunchLosslessScalingButton.LostFocus += Control_LostFocus;

            // Scaling tab - Current Profile card
            LosslessScalingCreateProfileButton.GotFocus += Control_GotFocus;
            LosslessScalingCreateProfileButton.LostFocus += Control_LostFocus;

            // Scaling tab - Scale and Save buttons (not in cards, clear focus)
            LosslessScalingEnabledToggle.GotFocus += StandaloneControl_GotFocus;
            LosslessScalingSaveSettingsButton.GotFocus += StandaloneControl_GotFocus;

            // Scaling tab - AutoScale card
            LosslessScalingAutoScaleToggle.GotFocus += Control_GotFocus;
            LosslessScalingAutoScaleToggle.LostFocus += Control_LostFocus;
            LosslessScalingAutoScaleDelaySlider.GotFocus += Control_GotFocus;
            LosslessScalingAutoScaleDelaySlider.LostFocus += Control_LostFocus;

            // Scaling tab - Scaling Type card
            LosslessScalingScalingTypeComboBox.GotFocus += Control_GotFocus;
            LosslessScalingScalingTypeComboBox.LostFocus += Control_LostFocus;
            LosslessScalingSharpnessSlider.GotFocus += Control_GotFocus;
            LosslessScalingSharpnessSlider.LostFocus += Control_LostFocus;
            LosslessScalingScaleModeComboBox.GotFocus += Control_GotFocus;
            LosslessScalingScaleModeComboBox.LostFocus += Control_LostFocus;
            LosslessScalingScaleFactorSlider.GotFocus += Control_GotFocus;
            LosslessScalingScaleFactorSlider.LostFocus += Control_LostFocus;
            LosslessScalingFrameGenTypeComboBox.GotFocus += Control_GotFocus;
            LosslessScalingFrameGenTypeComboBox.LostFocus += Control_LostFocus;
            LosslessScalingLSFG3ModeComboBox.GotFocus += Control_GotFocus;
            LosslessScalingLSFG3ModeComboBox.LostFocus += Control_LostFocus;
            LosslessScalingLSFG3MultiplierComboBox.GotFocus += Control_GotFocus;
            LosslessScalingLSFG3MultiplierComboBox.LostFocus += Control_LostFocus;
            LosslessScalingLSFG3TargetSlider.GotFocus += Control_GotFocus;
            LosslessScalingLSFG3TargetSlider.LostFocus += Control_LostFocus;
            LosslessScalingFlowScaleSlider.GotFocus += Control_GotFocus;
            LosslessScalingFlowScaleSlider.LostFocus += Control_LostFocus;
            LosslessScalingSizeToggle.GotFocus += Control_GotFocus;
            LosslessScalingSizeToggle.LostFocus += Control_LostFocus;
            LosslessScalingLSFG2ModeComboBox.GotFocus += Control_GotFocus;
            LosslessScalingLSFG2ModeComboBox.LostFocus += Control_LostFocus;

            // Legion tab - Touchpad card
            LegionTouchpadToggle.GotFocus += Control_GotFocus;
            LegionTouchpadToggle.LostFocus += Control_LostFocus;

            // Legion tab - Vibration card
            LegionVibrationComboBox.GotFocus += Control_GotFocus;
            LegionVibrationComboBox.LostFocus += Control_LostFocus;

            // Legion tab - Light Mode card
            LegionLightModeComboBox.GotFocus += Control_GotFocus;
            LegionLightModeComboBox.LostFocus += Control_LostFocus;

            // Legion tab - Light Color card (ColorPicker)
            LegionColorExpandButton.GotFocus += Control_GotFocus;
            LegionColorExpandButton.LostFocus += Control_LostFocus;
            LegionColorPicker.GotFocus += Control_GotFocus;
            LegionColorPicker.LostFocus += Control_LostFocus;

            // Legion tab - Brightness card
            LegionBrightnessSlider.GotFocus += Control_GotFocus;
            LegionBrightnessSlider.LostFocus += Control_LostFocus;

            // Legion tab - Performance Mode card
            LegionPerformanceModeComboBox.GotFocus += Control_GotFocus;
            LegionPerformanceModeComboBox.LostFocus += Control_LostFocus;

            // Legion tab - Custom TDP card removed; SPL/SPPL/FPPT now live on Performance tab.

            // Legion tab - Fan Full Speed card
            LegionFanFullSpeedToggle.GotFocus += Control_GotFocus;
            LegionFanFullSpeedToggle.LostFocus += Control_LostFocus;

            // Legion tab - Power Light card
            LegionPowerLightToggle.GotFocus += Control_GotFocus;
            LegionPowerLightToggle.LostFocus += Control_LostFocus;

            // Legion tab - Charge Limit card
            LegionChargeLimitToggle.GotFocus += Control_GotFocus;
            LegionChargeLimitToggle.LostFocus += Control_LostFocus;
        }

        private void Control_GotFocus(object sender, RoutedEventArgs e)
        {
            // Card focus highlighting disabled - only controls show focus visuals.
            // Scroll the focused control into view so gamepad navigation down a long
            // tab never leaves the focus ring off-screen.
            BringFocusedControlIntoView(sender);
        }

        /// <summary>
        /// Scrolls the just-focused control into view within its ScrollViewer, keeping the
        /// gamepad focus ring visible when navigating down long tabs. StartBringIntoView is a
        /// no-op when the element is already fully visible, so it's safe on every GotFocus.
        /// </summary>
        private void BringFocusedControlIntoView(object sender)
        {
            try
            {
                (sender as FrameworkElement)?.StartBringIntoView(new BringIntoViewOptions
                {
                    AnimationDesired = false,
                    VerticalAlignmentRatio = 0.5 // center the control in the viewport
                });
            }
            catch { /* best-effort scroll assist */ }
        }

        private void Control_LostFocus(object sender, RoutedEventArgs e)
        {
            // Don't clear immediately - let GotFocus of next control handle it
            // This prevents flicker when focus moves between controls in same card
        }

        private void NavItem_GotFocus(object sender, RoutedEventArgs e)
        {
            // Clear card highlight when navigation tabs get focus
            ClearCardFocus();
        }

        private void StandaloneControl_GotFocus(object sender, RoutedEventArgs e)
        {
            // Clear card highlight when standalone controls (not in cards) get focus
            ClearCardFocus();
            BringFocusedControlIntoView(sender);
        }

        private void ClearCardFocus()
        {
            if (currentFocusedCard != null)
            {
                currentFocusedCard.BorderBrush = cardDefaultBorderBrush;
                currentFocusedCard = null;
            }
        }

        private Border FindParentCard(DependencyObject element)
        {
            while (element != null)
            {
                if (element is Border border && border.Style == (Style)Resources["CardStyle"])
                {
                    return border;
                }
                element = VisualTreeHelper.GetParent(element);
            }
            return null;
        }

    }
}
