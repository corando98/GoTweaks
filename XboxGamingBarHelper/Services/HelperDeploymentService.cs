using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using NLog;

namespace XboxGamingBarHelper.Services
{
    /// <summary>
    /// Manages deployment of the helper executable to a stable location outside the MSIX package.
    /// This ensures the scheduled task path remains valid across MSIX updates.
    /// </summary>
    public static class HelperDeploymentService
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        /// <summary>
        /// Stable folder for deployed helper files inside package LocalCache.
        /// Uses the actual package path so scheduled tasks can find it.
        /// Gets cleaned up automatically on package uninstall.
        /// </summary>
        public static readonly string HelperFolder = Path.Combine(
            GetPackageLocalCachePath(),
            "GoTweaks", "Helper"
        );

        /// <summary>
        /// Gets the actual package LocalCache path (not virtualized).
        /// This path is accessible from outside the MSIX context.
        /// </summary>
        private static string GetPackageLocalCachePath()
        {
            try
            {
                // Get the actual LocalCache folder path from the package
                var localCache = global::Windows.Storage.ApplicationData.Current.LocalCacheFolder;
                return localCache.Path;
            }
            catch (Exception)
            {
                // Fallback if not running in package context (e.g., deployed helper)
                // Use the path we're running from if it's in the LocalCache
                var exePath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
                if (exePath.Contains("LocalCache"))
                {
                    // Extract the LocalCache path from current exe path
                    int idx = exePath.IndexOf("LocalCache", StringComparison.OrdinalIgnoreCase);
                    if (idx > 0)
                    {
                        return exePath.Substring(0, idx + "LocalCache".Length);
                    }
                }

                // Final fallback
                return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            }
        }

        /// <summary>
        /// Path to the deployed helper executable
        /// </summary>
        public static readonly string DeployedExePath = Path.Combine(HelperFolder, "XboxGamingBarHelper.exe");

        /// <summary>
        /// Gets the current MSIX helper executable path (in WindowsApps).
        /// This path changes with each MSIX update.
        /// </summary>
        public static string GetMsixHelperPath()
        {
            try
            {
                var exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exePath) && exePath.IndexOf("WindowsApps", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return exePath;
                }

                // If not running from MSIX, try to construct the path from package info
                var package = global::Windows.ApplicationModel.Package.Current;
                var installPath = package.InstalledLocation.Path;
                return Path.Combine(installPath, "XboxGamingBarHelper", "XboxGamingBarHelper.exe");
            }
            catch (Exception ex)
            {
                Logger.Debug($"Could not get MSIX helper path: {ex.Message}");
                // Fallback to current process path
                return Process.GetCurrentProcess().MainModule?.FileName ?? DeployedExePath;
            }
        }

        /// <summary>
        /// Path to the version tracking file
        /// </summary>
        private static readonly string VersionFilePath = Path.Combine(HelperFolder, ".version");

        /// <summary>
        /// Files that must be copied to the deployed location
        /// </summary>
        private static readonly string[] RequiredFiles = new[]
        {
            "XboxGamingBarHelper.exe",
            "XboxGamingBarHelper.exe.config",
            "ADLXCSharpBind.dll",
            "libryzenadj.dll",
            "inpoutx64.dll",
            "PresentMon.exe", // #66 Intel PresentMon CLI for FPS / displayed / FrameType capture
            "NLog.config",
            "NLog.dll",
            "Shared.dll",
            // Managed DLLs
            "LibreHardwareMonitorLib.dll",
            "HidSharp.dll",
            "RTSSSharedMemoryNET.dll",
            "RAMSPDToolkit-NDD.dll",
            // System dependencies
            "System.Text.Json.dll",
            "System.Text.Encodings.Web.dll",
            "System.Buffers.dll",
            "System.Memory.dll",
            "System.Numerics.Vectors.dll",
            "System.Runtime.CompilerServices.Unsafe.dll",
            "System.Threading.Tasks.Extensions.dll",
            "System.ValueTuple.dll",
            "System.IO.Pipelines.dll",
            "System.CodeDom.dll",
            "Microsoft.Bcl.AsyncInterfaces.dll"
        };

        /// <summary>
        /// Gets the current package version from MSIX
        /// </summary>
        public static string GetCurrentPackageVersion()
        {
            try
            {
                var package = global::Windows.ApplicationModel.Package.Current;
                var version = package.Id.Version;
                return $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
            }
            catch (Exception ex)
            {
                Logger.Debug($"Could not get package version (not running in MSIX?): {ex.Message}");
                // Fall back to assembly version
                try
                {
                    var assembly = Assembly.GetExecutingAssembly();
                    var version = assembly.GetName().Version;
                    return version?.ToString() ?? "0.0.0.0";
                }
                catch
                {
                    return "0.0.0.0";
                }
            }
        }

        /// <summary>
        /// Reads the deployed version from .version file
        /// </summary>
        public static string GetDeployedVersion()
        {
            try
            {
                if (File.Exists(VersionFilePath))
                {
                    return File.ReadAllText(VersionFilePath).Trim();
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"Could not read deployed version: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// Checks if deployment is needed (missing or version mismatch)
        /// </summary>
        public static bool IsDeploymentNeeded()
        {
            // Check if exe exists
            if (!File.Exists(DeployedExePath))
            {
                Logger.Info("Deployment needed: Helper exe not found at deployed location");
                return true;
            }

            // Check version
            var currentVersion = GetCurrentPackageVersion();
            var deployedVersion = GetDeployedVersion();

            if (string.IsNullOrEmpty(deployedVersion) || deployedVersion != currentVersion)
            {
                Logger.Info($"Deployment needed: Version mismatch (deployed={deployedVersion}, current={currentVersion})");
                return true;
            }

            Logger.Info($"Deployment up to date: Version {currentVersion}");
            return false;
        }

        /// <summary>
        /// Checks if currently running from the MSIX WindowsApps folder
        /// </summary>
        public static bool IsRunningFromMsix()
        {
            try
            {
                var exePath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
                return exePath.IndexOf("WindowsApps", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch (Exception ex)
            {
                Logger.Debug($"Could not determine if running from MSIX: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Checks if currently running from the deployed location
        /// </summary>
        public static bool IsRunningFromDeployedLocation()
        {
            try
            {
                var exePath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
                return string.Equals(exePath, DeployedExePath, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Logger.Debug($"Could not determine if running from deployed location: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Gets the source directory (MSIX helper folder path)
        /// </summary>
        public static string GetSourceDirectory()
        {
            try
            {
                var exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exePath))
                {
                    return Path.GetDirectoryName(exePath);
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"Could not get source directory: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// Deploys the helper files to the stable LocalAppData location
        /// </summary>
        /// <returns>True if deployment succeeded</returns>
        public static bool DeployHelper()
        {
            try
            {
                var sourceDir = GetSourceDirectory();
                if (string.IsNullOrEmpty(sourceDir))
                {
                    Logger.Error("Cannot deploy: Could not determine source directory");
                    return false;
                }

                Logger.Info($"Deploying helper from {sourceDir} to {HelperFolder}");

                // CRITICAL: never deploy onto ourselves. When the *deployed* exe (the one
                // already living in HelperFolder) is the process invoking a deploy/--setup,
                // GetSourceDirectory() resolves to HelperFolder, making source == target.
                // CopyFileWithRetry deletes the target before copying, and with source==target
                // that delete removes the only copy — then File.Copy fails with "Could not find
                // file", permanently destroying the deployed install (observed 2026-05-26:
                // 24/24 files lost, leaving only .old backups + a stale .version, which then
                // makes IsDeploymentNeeded believe the install is fine forever → widget stuck
                // on "Initial Setup in progress"). If we're already running from HelperFolder,
                // the files are by definition present — treat as a successful no-op.
                if (PathsAreSame(sourceDir, HelperFolder))
                {
                    Logger.Warn($"Deploy source equals deployed folder ({HelperFolder}); skipping self-copy (files already in place).");
                    return File.Exists(DeployedExePath);
                }

                // Create target directory
                Directory.CreateDirectory(HelperFolder);

                int successCount = 0;
                int failCount = 0;

                // Copy all required files
                foreach (var fileName in RequiredFiles)
                {
                    var sourcePath = Path.Combine(sourceDir, fileName);
                    var targetPath = Path.Combine(HelperFolder, fileName);

                    if (!File.Exists(sourcePath))
                    {
                        // Some files may be optional
                        Logger.Debug($"Source file not found (may be optional): {fileName}");
                        continue;
                    }

                    try
                    {
                        CopyFileWithRetry(sourcePath, targetPath);
                        successCount++;
                        Logger.Debug($"Copied: {fileName}");
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"Failed to copy {fileName}: {ex.Message}");
                        failCount++;
                    }
                }

                // Also copy any other DLLs that might be in the source directory
                try
                {
                    foreach (var dllPath in Directory.GetFiles(sourceDir, "*.dll"))
                    {
                        var fileName = Path.GetFileName(dllPath);
                        if (RequiredFiles.Contains(fileName, StringComparer.OrdinalIgnoreCase))
                            continue; // Already handled

                        var targetPath = Path.Combine(HelperFolder, fileName);
                        try
                        {
                            CopyFileWithRetry(dllPath, targetPath);
                            successCount++;
                            Logger.Debug($"Copied additional DLL: {fileName}");
                        }
                        catch (Exception ex)
                        {
                            Logger.Debug($"Could not copy additional DLL {fileName}: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Debug($"Error scanning for additional DLLs: {ex.Message}");
                }

                // Only stamp the .version file when the deploy actually succeeded — the
                // exe is present and nothing failed to copy. Writing it on a partial/failed
                // deploy is what let a half-wiped folder masquerade as "up to date" and
                // blocked the self-healing redeploy path. With this guard, a broken deploy
                // leaves no (or a stale-mismatched) version stamp, so IsDeploymentNeeded
                // returns true next launch and we recover automatically.
                bool deployOk = failCount == 0 && File.Exists(DeployedExePath);
                if (deployOk)
                {
                    var currentVersion = GetCurrentPackageVersion();
                    try
                    {
                        File.WriteAllText(VersionFilePath, currentVersion);
                        Logger.Info($"Version file written: {currentVersion}");
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"Could not write version file: {ex.Message}");
                    }
                }
                else
                {
                    Logger.Warn($"Skipping .version stamp — deploy incomplete (failCount={failCount}, exePresent={File.Exists(DeployedExePath)}); next launch will retry.");
                }

                Logger.Info($"Deployment complete: {successCount} files copied, {failCount} failures");
                return successCount > 0 && File.Exists(DeployedExePath);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error deploying helper");
                return false;
            }
        }

        /// <summary>
        /// True when two paths resolve to the same filesystem location (case-insensitive,
        /// separator- and trailing-slash-normalized). Used to refuse self-deploys.
        /// </summary>
        private static bool PathsAreSame(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try
            {
                string na = Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string nb = Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return string.Equals(na, nb, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Copies a file with retry logic for locked files
        /// </summary>
        private static void CopyFileWithRetry(string sourcePath, string targetPath, int maxRetries = 3)
        {
            // Defense-in-depth against the destructive self-copy: if source and target are
            // the same file, the delete-then-copy below would wipe it. Nothing to do.
            if (PathsAreSame(sourcePath, targetPath))
            {
                return;
            }

            Exception lastException = null;

            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    // Try to delete existing file first
                    if (File.Exists(targetPath))
                    {
                        try
                        {
                            File.Delete(targetPath);
                        }
                        catch
                        {
                            // Try rename-then-delete approach
                            var tempPath = targetPath + ".old." + Guid.NewGuid().ToString("N").Substring(0, 8);
                            try
                            {
                                File.Move(targetPath, tempPath);
                                // Schedule deletion on reboot if we can't delete now
                                try { File.Delete(tempPath); } catch { }
                            }
                            catch
                            {
                                // File is truly locked, will try overwrite
                            }
                        }
                    }

                    File.Copy(sourcePath, targetPath, overwrite: true);
                    return; // Success
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    if (i < maxRetries - 1)
                    {
                        System.Threading.Thread.Sleep(100 * (i + 1)); // Exponential backoff
                    }
                }
            }

            throw lastException ?? new IOException($"Failed to copy {sourcePath}");
        }

        /// <summary>
        /// Removes the deployed helper files (for uninstall)
        /// </summary>
        public static void RemoveDeployment()
        {
            try
            {
                if (Directory.Exists(HelperFolder))
                {
                    Logger.Info($"Removing deployment folder: {HelperFolder}");
                    Directory.Delete(HelperFolder, recursive: true);
                    Logger.Info("Deployment removed successfully");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Could not remove deployment folder: {ex.Message}");
            }
        }

        /// <summary>
        /// Checks if deployment exists and is valid
        /// </summary>
        public static bool IsDeploymentValid()
        {
            return File.Exists(DeployedExePath) && !string.IsNullOrEmpty(GetDeployedVersion());
        }
    }
}
