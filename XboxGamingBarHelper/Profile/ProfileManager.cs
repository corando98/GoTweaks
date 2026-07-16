using NLog;
using Shared.Data;
using Shared.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using Windows.Storage;
using XboxGamingBarHelper.Core;

namespace XboxGamingBarHelper.Profile
{
    internal class ProfileManager : Manager
    {
        private const string PROFILE_FOLDER_NAME = "profiles";
        private const string XML_EXTENSION = ".xml";

        /// <summary>
        /// Package family name for constructing LocalState path when running outside MSIX
        /// </summary>
        private const string PACKAGE_FAMILY_NAME = "PlayandBuildCustom.10365195AA1EC_8edemd50ez3gg";

        /// <summary>
        /// Cached local folder path (works both in MSIX and elevated contexts)
        /// </summary>
        private static string _localFolderPath;

        /// <summary>
        /// Gets the local folder path, falling back to a known path when running outside MSIX
        /// </summary>
        private static string GetLocalFolderPath()
        {
            if (_localFolderPath != null)
                return _localFolderPath;

            try
            {
                _localFolderPath = ApplicationData.Current.LocalFolder.Path;
            }
            catch (InvalidOperationException)
            {
                // Running outside MSIX (elevated via scheduled task)
                // Use the same path that MSIX would use
                _localFolderPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Packages",
                    PACKAGE_FAMILY_NAME,
                    "LocalState"
                );
                Logger.Info($"Using fallback LocalFolder path: {_localFolderPath}");
            }

            return _localFolderPath;
        }

        public GameProfile GlobalProfile;

        private readonly Dictionary<GameId, GameProfile> gameProfiles;
        public IReadOnlyDictionary<GameId, GameProfile> GameProfiles
        {
            get { return gameProfiles; }
        }

        private readonly PerGameProfileProperty perGameProfile;
        public PerGameProfileProperty PerGameProfile
        {
            get { return  perGameProfile; }
        }

        private readonly GameProfileProperty currentProfile;
        public GameProfileProperty CurrentProfile
        {
            get { return currentProfile; }
        }

        private readonly DeleteGameProfileProperty deleteGameProfile;
        public DeleteGameProfileProperty DeleteGameProfile
        {
            get { return deleteGameProfile; }
        }

        public ProfileManager() : base()
        {
            gameProfiles = new Dictionary<GameId, GameProfile>();

            Logger.Info("Initialize global profile.");
            // Load global profile.
            var globalProfilePath = GetGlobalProfilePath();
            if (!File.Exists(globalProfilePath))
            {
                // First run: seed the global profile from the system's CURRENT power values
                // instead of hardcoded defaults. Applying the profile is then a no-op until
                // the user changes something in GoTweaks — we never overwrite a power plan
                // the user tuned outside the app (issue #103).
                bool systemCpuBoost = true;
                int systemCpuEpp = 80;
                try
                {
                    systemCpuBoost = Power.PowerManager.GetCpuBoostMode(false);
                    systemCpuEpp = (int)Power.PowerManager.GetEppValue(false);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"Could not read current power values for the initial global profile: {ex.Message}");
                }
                GlobalProfile = new GameProfile(GameProfile.GLOBAL_PROFILE_NAME, GameProfile.GLOBAL_PROFILE_NAME, true, 25, systemCpuBoost, systemCpuEpp, false, globalProfilePath, gameProfiles);
                GlobalProfile.Save();
                Logger.Info($"Created global profile from current system values: CPUBoost={systemCpuBoost}, EPP={systemCpuEpp}");
            }
            else
            {
                GlobalProfile = XmlHelper.FromXMLFile<GameProfile>(globalProfilePath);
                GlobalProfile.Path = globalProfilePath;
                GlobalProfile.Cache = gameProfiles;
            }

            Logger.Info("Create game profiles folder.");
            // Make sure game profiles folder is created.
            var gameProfilesFolder = GetGameProfilesFolder();
            if (!Directory.Exists(gameProfilesFolder))
            {
                Directory.CreateDirectory(gameProfilesFolder);
            }

            Logger.Info("Load game profiles.");
            // Read all existing game profiles.
            var xmlFiles = Directory.GetFiles(gameProfilesFolder, $"*{XML_EXTENSION}");
            foreach (string filePath in xmlFiles)
            {
                try
                {
                    var gameProfile = XmlHelper.FromXMLFile<GameProfile>(filePath);
                    gameProfile.Path = filePath;
                    gameProfile.Cache = gameProfiles;
                    gameProfiles.Add(gameProfile.GameId, gameProfile);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error reading or deserializing XML file '{filePath}': {ex.Message}.");
                }
            }

            Logger.Info("Initialize game profile properties.");
            perGameProfile = new PerGameProfileProperty(null, this);
            currentProfile = new GameProfileProperty(GlobalProfile, this);
            deleteGameProfile = new DeleteGameProfileProperty(this);
        }

        public static string GetGameProfilesFolder()
        {
            return Path.Combine(GetLocalFolderPath(), PROFILE_FOLDER_NAME);
        }

        public static string GetGlobalProfilePath()
        {
            return Path.Combine(GetLocalFolderPath(), $"{GameProfile.GLOBAL_PROFILE_NAME}{XML_EXTENSION}");
        }

        /// <summary>
        /// Refreshes the GlobalProfile field from disk first, then from the in-memory
        /// cache. Disk re-read catches edits the widget made to global.xml that the
        /// helper's in-memory copy hasn't seen — without this, helper boots with a
        /// snapshot of the file and never re-reads, so user-edited lighting/perf
        /// values get reverted to the boot-time snapshot the next time the game
        /// stops and helper applies "global" (issue #79: "stick light always
        /// changes to dynamic, where I had it at pulse slow speed").
        ///
        /// Property change handlers (TDP_PropertyChanged, etc.) update CurrentProfile
        /// which is a struct copy. Save() writes to the cache/disk, but the
        /// GlobalProfile field stays stale. Call this before reading GlobalProfile
        /// to get the latest values.
        /// </summary>
        public void RefreshGlobalProfile()
        {
            // Re-read disk first. Widget edits to global.xml are otherwise invisible
            // to helper's in-memory cache because the two processes don't share state.
            // FlushPendingWrites first so any debounced helper-side save lands before
            // we read (else we'd read our own pre-write snapshot).
            try
            {
                var globalProfilePath = GetGlobalProfilePath();
                Shared.Data.GameProfile.FlushAllPendingWrites();
                if (File.Exists(globalProfilePath))
                {
                    var fromDisk = Shared.Utilities.XmlHelper.FromXMLFile<Shared.Data.GameProfile>(globalProfilePath);
                    fromDisk.Path = globalProfilePath;
                    fromDisk.Cache = gameProfiles;
                    gameProfiles[fromDisk.GameId] = fromDisk;
                    GlobalProfile = fromDisk;
                    Logger.Info($"Refreshed GlobalProfile from disk: TDP={GlobalProfile.TDP}, CPUBoost={GlobalProfile.CPUBoost}, EPP={GlobalProfile.CPUEPP}, LightMode={GlobalProfile.LegionLightMode}, LightSpeed={GlobalProfile.LegionLightSpeed}");
                    return;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"RefreshGlobalProfile: disk re-read failed, falling back to cache: {ex.Message}");
            }

            // Fallback: use in-memory cache (matches prior behavior on read failure).
            if (gameProfiles.TryGetValue(GlobalProfile.GameId, out GameProfile cached))
            {
                GlobalProfile = cached;
                Logger.Info($"Refreshed GlobalProfile from cache: TDP={GlobalProfile.TDP}, CPUBoost={GlobalProfile.CPUBoost}, EPP={GlobalProfile.CPUEPP}");
            }
        }

        public bool TryGetProfile(GameId gameId, out GameProfile gameProfile)
        {
            // Fast path: exact match
            if (gameProfiles.TryGetValue(gameId, out gameProfile))
            {
                return true;
            }

            // Fallback: try matching by path only (handles name variations like "Game Name" vs "Game: Name")
            if (!string.IsNullOrEmpty(gameId.Path))
            {
                foreach (var kvp in gameProfiles)
                {
                    if (string.Equals(kvp.Key.Path, gameId.Path, StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.Debug($"Profile matched by path (name mismatch: '{gameId.Name}' vs '{kvp.Key.Name}'): {gameId.Path}");
                        gameProfile = kvp.Value;
                        return true;
                    }
                }
            }

            // Fallback: try normalized name match (removes punctuation, case-insensitive)
            var normalizedName = NormalizeName(gameId.Name);
            foreach (var kvp in gameProfiles)
            {
                if (NormalizeName(kvp.Key.Name) == normalizedName)
                {
                    Logger.Debug($"Profile matched by normalized name: '{gameId.Name}' -> '{kvp.Key.Name}'");
                    gameProfile = kvp.Value;
                    return true;
                }
            }

            gameProfile = default;
            return false;
        }

        /// <summary>
        /// Normalizes a game name for fuzzy matching by removing punctuation and converting to lowercase.
        /// </summary>
        private static string NormalizeName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return string.Empty;

            // Remove common punctuation that varies between sources (colons, dashes, etc.)
            // and convert to lowercase for case-insensitive comparison
            var normalized = new System.Text.StringBuilder();
            foreach (var c in name.ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c) || c == ' ')
                {
                    normalized.Append(c);
                }
            }
            return normalized.ToString().Trim();
        }

        public GameProfile AddNewProfile(GameId gameId)
        {
            if (TryGetProfile(gameId, out var gameProfile))
            {
                Logger.Warn($"Already have profile for {gameId.Name}.");
                return gameProfile;
            }

            var newGameProfilePath = Path.Combine(GetGameProfilesFolder(), $"{Path.GetFileNameWithoutExtension(gameId.Path)}{XML_EXTENSION}");
            var newGameProfile = new GameProfile(gameId.Name, gameId.Path, true, CurrentProfile.TDP, CurrentProfile.CPUBoost, CurrentProfile.CPUEPP, CurrentProfile.TDPBoostEnabled, newGameProfilePath, gameProfiles);
            newGameProfile.Save();
            Logger.Info($"Add new profile for {gameId.Name} at {newGameProfilePath}.");
            return newGameProfile;
        }

        /// <summary>
        /// Deletes a game profile by name.
        /// </summary>
        public bool DeleteProfile(string gameName)
        {
            if (string.IsNullOrEmpty(gameName))
            {
                return false;
            }

            // Find the profile by name
            GameId? targetKey = null;
            foreach (var kvp in gameProfiles)
            {
                if (string.Equals(kvp.Key.Name, gameName, StringComparison.OrdinalIgnoreCase))
                {
                    targetKey = kvp.Key;
                    break;
                }
            }

            if (!targetKey.HasValue)
            {
                Logger.Warn($"No profile found for {gameName} to delete");
                return false;
            }

            var profile = gameProfiles[targetKey.Value];
            var filePath = profile.Path;

            // Remove from dictionary
            gameProfiles.Remove(targetKey.Value);

            // Delete the XML file
            if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
            {
                try
                {
                    File.Delete(filePath);
                    Logger.Info($"Deleted profile XML for {gameName} at {filePath}");
                }
                catch (Exception ex)
                {
                    Logger.Error($"Failed to delete profile XML for {gameName}: {ex.Message}");
                }
            }

            Logger.Info($"Deleted profile for {gameName}");
            return true;
        }

        /// <summary>
        /// Gets a profile by game path.
        /// </summary>
        public GameProfile? GetProfile(string gamePath)
        {
            if (string.IsNullOrEmpty(gamePath))
            {
                return null;
            }

            foreach (var kvp in gameProfiles)
            {
                if (string.Equals(kvp.Key.Path, gamePath, StringComparison.OrdinalIgnoreCase))
                {
                    return kvp.Value;
                }
            }

            return null;
        }

        /// <summary>
        /// Updates DGP preference for a game profile.
        /// </summary>
        public void UpdateDgpPreference(string gamePath, bool isOnBattery, bool enabled)
        {
            if (string.IsNullOrEmpty(gamePath))
            {
                return;
            }

            // Find the profile by path
            GameId? targetKey = null;
            foreach (var kvp in gameProfiles)
            {
                if (string.Equals(kvp.Key.Path, gamePath, StringComparison.OrdinalIgnoreCase))
                {
                    targetKey = kvp.Key;
                    break;
                }
            }

            if (!targetKey.HasValue)
            {
                Logger.Warn($"No profile found for {gamePath} to update DGP preference");
                return;
            }

            // Get mutable copy, update, and save
            var profile = gameProfiles[targetKey.Value];
            if (isOnBattery)
            {
                profile.DgpEnabledOnDC = enabled;
            }
            else
            {
                profile.DgpEnabledOnAC = enabled;
            }

            // Update cache (profile.Save() already does this via its setter)
            gameProfiles[targetKey.Value] = profile;
            Logger.Info($"Updated DGP preference for {targetKey.Value.Name}: {(isOnBattery ? "DC" : "AC")}={enabled}");
        }
    }
}
