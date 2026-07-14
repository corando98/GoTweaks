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

        private void DeleteProfileButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string gameName)
            {
                Logger.Info($"Delete button clicked for game: {gameName}");
                DeleteGameProfile(gameName);
            }
        }

        /// <summary>
        /// Delete handler for the bin button on the active "Now Playing" profile card.
        /// Reuses DeleteGameProfile against the currently-detected game so the user
        /// doesn't have to leave the running game to clean up its profile (the active
        /// game's card is always pinned at the top and never appears in the saved-
        /// profiles list below). After deletion, the per-game profile container is
        /// gone and the next per-game-profile-toggle event will recreate a fresh one.
        /// </summary>
        private void DeleteActiveProfileButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(currentGameName) || !HasValidGame(currentGameName))
            {
                Logger.Info("DeleteActiveProfileButton_Click ignored - no valid current game");
                return;
            }
            Logger.Info($"Delete active profile button clicked for: {currentGameName}");
            DeleteGameProfile(currentGameName);
        }

        private void CleanupInvalidProfiles()
        {
            var settings = ApplicationData.Current.LocalSettings;
            var profilesToDelete = new List<string>();
            var perGameSplitKeysToDelete = new List<string>();

            // Find all containers with invalid game names (case-insensitive check)
            foreach (var containerName in settings.Containers.Keys)
            {
                if (containerName.IndexOf("No game detected", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    profilesToDelete.Add(containerName);
                }
            }

            // Find all per-game split settings with invalid game names
            foreach (var key in settings.Values.Keys)
            {
                if (key.StartsWith(PerGamePowerSourceProfileSettingPrefix, StringComparison.OrdinalIgnoreCase) &&
                    key.IndexOf("No game detected", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    perGameSplitKeysToDelete.Add(key);
                }
            }

            // Delete invalid profiles
            foreach (var containerName in profilesToDelete)
            {
                settings.DeleteContainer(containerName);
                Logger.Info($"Cleaned up invalid profile container: {containerName}");
            }

            foreach (var key in perGameSplitKeysToDelete)
            {
                settings.Values.Remove(key);
                Logger.Info($"Cleaned up invalid per-game power split key: {key}");
            }

            if (profilesToDelete.Count > 0)
            {
                Logger.Info($"Cleaned up {profilesToDelete.Count} invalid profile(s)");
            }
        }

        private void DeleteGameProfile(string gameName)
        {
            if (string.IsNullOrWhiteSpace(gameName))
                return;

            var settings = ApplicationData.Current.LocalSettings;
            bool profileDeleted = false;

            // Try to delete single profile
            if (settings.Containers.ContainsKey($"Profile_Game_{gameName}"))
            {
                settings.DeleteContainer($"Profile_Game_{gameName}");
                Logger.Info($"Deleted game profile for {gameName}");
                profileDeleted = true;
            }

            // Try to delete AC/DC profiles
            if (settings.Containers.ContainsKey($"Profile_Game_{gameName}_AC"))
            {
                settings.DeleteContainer($"Profile_Game_{gameName}_AC");
                Logger.Info($"Deleted game AC profile for {gameName}");
                profileDeleted = true;
            }

            if (settings.Containers.ContainsKey($"Profile_Game_{gameName}_DC"))
            {
                settings.DeleteContainer($"Profile_Game_{gameName}_DC");
                Logger.Info($"Deleted game DC profile for {gameName}");
                profileDeleted = true;
            }

            string splitKey = GetPerGamePowerSourceProfileSettingKey(gameName);
            if (settings.Values.ContainsKey(splitKey))
            {
                settings.Values.Remove(splitKey);
                Logger.Info($"Deleted per-game power split setting for {gameName}");
            }

            // Also delete the game's controller profile so "delete" is a clean reset of the
            // whole game. Without this, the per-game controller remaps/lighting (stored in a
            // separate ControllerProfile_Game_<name> container) and the "disabled" preference
            // survive the delete and reappear the next time the game is detected.
            bool controllerDeleted = false;
            string controllerKey = $"ControllerProfile_Game_{gameName}";
            if (settings.Containers.ContainsKey(controllerKey))
            {
                settings.DeleteContainer(controllerKey);
                Logger.Info($"Deleted controller profile for {gameName}");
                controllerDeleted = true;
            }
            string controllerDisabledKey = $"ControllerProfileDisabled_{gameName}";
            if (settings.Values.ContainsKey(controllerDisabledKey))
            {
                settings.Values.Remove(controllerDisabledKey);
            }

            // Tell the helper to delete its per-game profile XML (profiles/<exe>.xml).
            // The helper implements this (ProfileManager.DeleteProfile) but the widget never
            // triggered it, so a deleted profile left an orphaned XML the helper could still
            // match and re-apply when the game reappeared. ForceSetValue so a repeat delete of
            // the same name still sends (the trigger value would otherwise dedupe).
            deleteGameProfile?.ForceSetValue(gameName);
            Logger.Info($"Sent DeleteGameProfile to helper for {gameName}");

            if (profileDeleted || controllerDeleted)
            {
                // If we deleted the current game's profile(s), reset its per-game toggles so the
                // UI falls back to the global profiles instead of pointing at deleted containers.
                if (gameName == currentGameName)
                {
                    if (PerGameProfileToggle?.IsOn == true)
                    {
                        Logger.Info($"Deleted profile for current game {gameName}, disabling per-game toggle");
                        PerGameProfileToggle.IsOn = false;
                    }
                    if (LegionControllerProfileToggle?.IsOn == true)
                    {
                        Logger.Info($"Deleted controller profile for current game {gameName}, disabling per-game controller toggle");
                        LegionControllerProfileToggle.IsOn = false;
                    }

                    // The controller toggle-off handler re-writes the "disabled" preference;
                    // remove it again so a deleted game returns to a pristine (global) state.
                    if (settings.Values.ContainsKey(controllerDisabledKey))
                    {
                        settings.Values.Remove(controllerDisabledKey);
                    }
                }

                // Refresh the display and the saved-controller-profiles list (if expanded).
                UpdateProfileDisplay();
                if (isSavedProfilesExpanded)
                {
                    RefreshSavedProfilesList();
                }
            }
        }

    }
}
