using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using NLog;

namespace XboxGamingBarHelper.Services
{
    /// <summary>
    /// Lightweight driver-update check for Lenovo Legion devices.
    ///
    /// Two layers, both surfaced to the widget:
    ///   1. Machine-type + installed BIOS version (always available, WMI-only).
    ///      Used to build a direct link to Lenovo's public driver-download page
    ///      for this exact machine so the user can land on the right place with
    ///      one click.
    ///   2. A best-effort HTTP probe of Lenovo's pcsupport API for a structured
    ///      driver list so we can show installed-vs-latest comparisons similar
    ///      to G-Helper on ASUS. The API endpoint shape is undocumented and
    ///      prone to change, so the parse is defensive: if we can't reach the
    ///      API or get an unexpected shape, the widget still gets layer 1.
    /// </summary>
    internal static class LenovoDriverCheckService
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        // Cache of the most-recent Check so unsolicited push messages and
        // the widget's explicit "Check for updates" button both share one
        // result — no duplicate live fetch on widget open if the startup
        // probe already ran. Refreshed by every CheckAsync call.
        private static DriverUpdateResult _lastResult;
        public static DriverUpdateResult LastResult => _lastResult;

        // Shared HttpClient — small timeout so a hanging Lenovo endpoint doesn't
        // wedge the helper's pipe thread.
        private static readonly HttpClient _http = CreateHttpClient();

        private static HttpClient CreateHttpClient()
        {
            // .NET Framework 4.8 defaults ServicePointManager.SecurityProtocol to
            // SystemDefault, which on older Windows installs still lets SSL3/TLS1.0
            // attempts happen first. Lenovo's download CDN rejects anything below
            // TLS 1.2 — without this line the request silently hangs until timeout
            // and we log nothing useful. Explicitly prefer TLS 1.2+ (the | keeps
            // any newer protocol the OS adds in the future).
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                // Tls13 = 12288. Using the raw int so this still compiles on
                // .NET Framework 4.8 build targets that don't expose the enum.
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)12288;
            }
            catch { /* best-effort; don't block HttpClient creation */ }

            var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(12),
            };
            // Lenovo's CDN serves the catalog fine to generic UAs but we still
            // send a recognisable token in case their telemetry cares.
            client.DefaultRequestHeaders.Add("User-Agent",
                "GoTweaks/1.0 (Windows NT 11.0; Lenovo driver catalog check)");
            // The catalog is XML, per-package descriptors are XML too.
            client.DefaultRequestHeaders.Add("Accept", "application/xml, text/xml, */*");
            return client;
        }

        /// <summary>
        /// Runs the full driver-update probe: machine type + BIOS version from
        /// WMI, direct driver page URL, and a best-effort API fetch of the
        /// current driver list. Never throws — failure paths return a result
        /// with IsLenovo=false or an empty Drivers list so the widget has
        /// something to render either way.
        /// </summary>
        public static async Task<DriverUpdateResult> CheckAsync()
        {
            var result = new DriverUpdateResult();
            try
            {
                PopulateMachineInfo(result);
                if (!result.IsLenovo)
                {
                    result.ErrorMessage = "Not running on a Lenovo device (WMI manufacturer mismatch).";
                    return result;
                }

                result.DriverPageUrl = BuildDriverPageUrl(result.MachineTypeCode);

                // Best-effort live fetch. Don't let a slow/blocked Lenovo
                // endpoint block the widget's response — the HttpClient has
                // its own 8 s timeout.
                try
                {
                    result.Drivers = await TryFetchDriversAsync(result.MachineTypeCode);
                    result.LiveFetchSucceeded = result.Drivers.Count > 0;
                }
                catch (Exception ex)
                {
                    Logger.Debug($"Lenovo driver API probe failed: {ex.Message}");
                    result.Drivers = new List<DriverEntry>();
                    result.LiveFetchSucceeded = false;
                }

                // Once we have the catalog entries, match each one against the
                // locally-installed driver snapshot so the widget can show
                // installed-vs-latest and tag each row as up-to-date / update
                // available / not installed. BIOS is compared against the
                // Win32_BIOS version we already read; everything else comes
                // from Win32_PnPSignedDriver.
                if (result.Drivers.Count > 0)
                {
                    try
                    {
                        var index = BuildInstalledDriverIndex(result.BiosVersion);
                        foreach (var entry in result.Drivers)
                        {
                            PopulateInstalledStatus(entry, index);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"Installed driver comparison threw: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"LenovoDriverCheckService.CheckAsync threw: {ex.Message}");
                result.ErrorMessage = ex.Message;
            }
            _lastResult = result;
            return result;
        }

        /// <summary>
        /// Batch install: downloads every URL in parallel (bounded by 4
        /// concurrent fetches so we don't saturate the Lenovo CDN or the
        /// user's connection), then launches installers strictly sequentially.
        /// Launching in parallel would collide on the Windows Installer
        /// service mutex and at best queue, at worst error out, so we wait
        /// between launches.
        /// </summary>
        public static async Task<string> BatchInstallAsync(IList<string> urls)
        {
            if (urls == null || urls.Count == 0)
                return "{\"success\":false,\"total\":0,\"launched\":0,\"message\":\"No URLs.\"}";

            Logger.Info($"BatchInstallAsync: starting — {urls.Count} url(s)");

            // Phase 1: parallel download (throttled to 4).
            var downloadSemaphore = new SemaphoreSlim(4);
            var localPaths = new string[urls.Count];
            var downloadErrors = new string[urls.Count];
            var downloadTasks = new List<Task>();
            for (int i = 0; i < urls.Count; i++)
            {
                int idx = i;
                string url = urls[idx];
                downloadTasks.Add(Task.Run(async () =>
                {
                    await downloadSemaphore.WaitAsync();
                    try
                    {
                        var (path, err) = await DownloadToTempAsync(url);
                        localPaths[idx] = path;
                        downloadErrors[idx] = err;
                    }
                    finally { downloadSemaphore.Release(); }
                }));
            }
            await Task.WhenAll(downloadTasks);

            int downloadedOk = localPaths.Count(p => !string.IsNullOrEmpty(p));
            Logger.Info($"BatchInstallAsync: downloaded {downloadedOk}/{urls.Count}");

            // Phase 2: sequential launch.
            int launched = 0;
            for (int i = 0; i < localPaths.Length; i++)
            {
                string path = localPaths[i];
                if (string.IsNullOrEmpty(path)) continue;
                string ext = (System.IO.Path.GetExtension(path) ?? "").ToLowerInvariant();
                if (ext != ".exe" && ext != ".msi") continue;
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = path,
                        UseShellExecute = true,
                    };
                    System.Diagnostics.Process.Start(psi);
                    launched++;
                    // Brief delay so successive installers don't race on the
                    // Windows Installer service mutex.
                    await Task.Delay(800);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"BatchInstallAsync: launch failed for {path} — {ex.Message}");
                }
            }

            Logger.Info($"BatchInstallAsync: launched {launched}/{urls.Count}");
            return "{\"success\":" + (launched > 0 ? "true" : "false") +
                   ",\"total\":" + urls.Count +
                   ",\"downloaded\":" + downloadedOk +
                   ",\"launched\":" + launched +
                   ",\"message\":\"Launched " + launched + " of " + urls.Count + " installers.\"}";
        }

        /// <summary>
        /// Shared download helper used by both single-install and batch-install
        /// paths. Returns (absolute file path, null) on success or (null, error
        /// message) on failure. Host/scheme validation matches single install.
        /// </summary>
        private static async Task<(string path, string error)> DownloadToTempAsync(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return (null, "Missing URL");
            Uri uri;
            try { uri = new Uri(url); }
            catch { return (null, "Invalid URL"); }
            if (uri.Scheme != "https") return (null, "Not https");
            var host = uri.Host.ToLowerInvariant();
            if (!host.EndsWith("lenovo.com")) return (null, "Host not allowed: " + host);

            string fileName = System.IO.Path.GetFileName(uri.LocalPath);
            if (string.IsNullOrWhiteSpace(fileName)) fileName = "lenovo_installer";
            foreach (var bad in System.IO.Path.GetInvalidFileNameChars())
                fileName = fileName.Replace(bad, '_');

            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "GoTweaksLenovoInstallers");
            try { System.IO.Directory.CreateDirectory(dir); }
            catch (Exception ex) { return (null, "Temp dir: " + ex.Message); }

            string target = System.IO.Path.Combine(dir, fileName);
            try
            {
                using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                if (!response.IsSuccessStatusCode)
                    return (null, "HTTP " + (int)response.StatusCode);
                using (var src = await response.Content.ReadAsStreamAsync())
                using (var dst = System.IO.File.Create(target))
                    await src.CopyToAsync(dst);
                return (target, null);
            }
            catch (Exception ex)
            {
                return (null, ex.Message);
            }
        }

        /// <summary>
        /// Downloads a Lenovo driver installer to a temp folder and launches
        /// it. Returns a small JSON blob the widget can inspect (the caller
        /// wraps it in a pipe response). The helper is elevated so launching
        /// a Lenovo EXE here avoids a second UAC prompt for the user.
        ///
        /// Security notes:
        /// - URLs are accepted only if they're HTTPS on a Lenovo-owned host
        ///   (download.lenovo.com / pcsupport.lenovo.com). Other hosts are
        ///   rejected so this endpoint can't be abused as a general-purpose
        ///   "download + run" primitive.
        /// - The filename comes from the URL path only — no server-provided
        ///   Content-Disposition is used. We sanitise it to disallow path
        ///   separators before writing to the temp directory.
        /// - Non-EXE payloads (readmes, zips, etc.) are still downloaded but
        ///   only .exe / .msi files are launched automatically.
        /// </summary>
        public static async Task<string> InstallDriverAsync(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return "{\"success\":false,\"message\":\"Missing download URL.\"}";

            Uri uri;
            try { uri = new Uri(url); }
            catch { return "{\"success\":false,\"message\":\"Invalid URL.\"}"; }

            if (uri.Scheme != "https")
                return "{\"success\":false,\"message\":\"Only https URLs are accepted.\"}";
            var host = uri.Host.ToLowerInvariant();
            bool trusted = host.EndsWith("lenovo.com") || host.EndsWith("download.lenovo.com");
            if (!trusted)
                return "{\"success\":false,\"message\":\"Host not allowed: " + host + "\"}";

            string fileName = System.IO.Path.GetFileName(uri.LocalPath);
            if (string.IsNullOrWhiteSpace(fileName)) fileName = "lenovo_installer";
            // Strip any path chars that managed to survive — GetFileName
            // should have handled this, but belt-and-suspenders.
            foreach (var bad in System.IO.Path.GetInvalidFileNameChars())
            {
                fileName = fileName.Replace(bad, '_');
            }

            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "GoTweaksLenovoInstallers");
            try { System.IO.Directory.CreateDirectory(dir); }
            catch (Exception ex)
            {
                return "{\"success\":false,\"message\":\"Couldn't create temp dir: " + ex.Message.Replace("\"", "'") + "\"}";
            }

            string targetPath = System.IO.Path.Combine(dir, fileName);
            Logger.Info($"InstallDriverAsync: downloading {url} -> {targetPath}");
            try
            {
                using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                if (!response.IsSuccessStatusCode)
                {
                    return "{\"success\":false,\"message\":\"HTTP " + (int)response.StatusCode + " from Lenovo CDN.\"}";
                }
                using (var src = await response.Content.ReadAsStreamAsync())
                using (var dst = System.IO.File.Create(targetPath))
                {
                    await src.CopyToAsync(dst);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"InstallDriverAsync: download failed — {ex.Message}");
                return "{\"success\":false,\"message\":\"Download failed: " + ex.Message.Replace("\"", "'") + "\"}";
            }

            string ext = (System.IO.Path.GetExtension(targetPath) ?? "").ToLowerInvariant();
            bool launchable = ext == ".exe" || ext == ".msi";
            if (!launchable)
            {
                Logger.Info($"InstallDriverAsync: downloaded {fileName} but not launchable ({ext}), user must open manually.");
                string safePath = targetPath.Replace("\\", "\\\\").Replace("\"", "'");
                return "{\"success\":true,\"launched\":false,\"path\":\"" + safePath + "\",\"message\":\"Downloaded. Open the file manually to install.\"}";
            }

            try
            {
                // UseShellExecute lets the OS handle .msi via msiexec and .exe
                // with its embedded manifest (which often requires admin — the
                // helper already runs elevated, so the child inherits that).
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = targetPath,
                    UseShellExecute = true,
                };
                System.Diagnostics.Process.Start(psi);
            }
            catch (Exception ex)
            {
                Logger.Warn($"InstallDriverAsync: launch failed — {ex.Message}");
                return "{\"success\":false,\"message\":\"Launch failed: " + ex.Message.Replace("\"", "'") + "\"}";
            }

            Logger.Info($"InstallDriverAsync: launched {fileName}");
            return "{\"success\":true,\"launched\":true,\"message\":\"Installer launched.\"}";
        }

        /// <summary>Reads Lenovo-specific WMI fields: machine type, model, BIOS version.</summary>
        private static void PopulateMachineInfo(DriverUpdateResult result)
        {
            try
            {
                using var csp = new ManagementObjectSearcher("SELECT Vendor, Name, Version FROM Win32_ComputerSystemProduct");
                foreach (ManagementObject obj in csp.Get())
                {
                    result.Manufacturer = obj["Vendor"]?.ToString()?.Trim() ?? "";
                    result.Model = obj["Name"]?.ToString()?.Trim() ?? "";
                    result.ModelVersion = obj["Version"]?.ToString()?.Trim() ?? "";
                    break;
                }
            }
            catch (Exception ex) { Logger.Debug($"Win32_ComputerSystemProduct read failed: {ex.Message}"); }

            try
            {
                using var bios = new ManagementObjectSearcher("SELECT SMBIOSBIOSVersion, Version FROM Win32_BIOS");
                foreach (ManagementObject obj in bios.Get())
                {
                    result.BiosVersion = obj["SMBIOSBIOSVersion"]?.ToString()?.Trim()
                                     ?? obj["Version"]?.ToString()?.Trim()
                                     ?? "";
                    break;
                }
            }
            catch (Exception ex) { Logger.Debug($"Win32_BIOS read failed: {ex.Message}"); }

            result.IsLenovo = result.Manufacturer.IndexOf("LENOVO", StringComparison.OrdinalIgnoreCase) >= 0;

            // Lenovo machine-type codes live in either Model or ModelVersion. Examples:
            //   Model = "83E1", Version = "Legion Go 83E1CTO1WW"
            //   Model = "Legion Go 83Q2", Version = "83Q2CTO1WW"
            // We want the 4-character MT code (letters+digits) to build the
            // support URL. Pull the first 4-char token that looks like a
            // machine type from either field.
            result.MachineTypeCode = ExtractMachineType(result.Model)
                                  ?? ExtractMachineType(result.ModelVersion)
                                  ?? "";
        }

        private static string ExtractMachineType(string source)
        {
            if (string.IsNullOrWhiteSpace(source)) return null;
            // Lenovo machine types are 4 uppercase alphanumeric chars (e.g. 83E1).
            var match = System.Text.RegularExpressions.Regex.Match(source, @"\b([0-9][0-9A-Z]{3})\b");
            return match.Success ? match.Groups[1].Value : null;
        }

        /// <summary>
        /// Builds Lenovo's public "downloads/driver-list" URL for the given
        /// machine-type code. Works for every Lenovo product and always opens
        /// on the correct driver page for the user's exact machine.
        /// </summary>
        public static string BuildDriverPageUrl(string machineTypeCode)
        {
            if (string.IsNullOrWhiteSpace(machineTypeCode))
            {
                return "https://pcsupport.lenovo.com/";
            }
            return $"https://pcsupport.lenovo.com/products/{machineTypeCode}/downloads/driver-list";
        }

        /// <summary>
        /// Fetches Lenovo's driver catalog for the user's exact machine type.
        ///
        /// Primary source is the undocumented pcsupport.lenovo.com JSON API
        /// that backs the public driver-list page — it covers every current
        /// product (including newly-released hardware Lenovo hasn't yet
        /// published an LSU XML catalog for) and returns the full driver list
        /// in a single HTTP call. The legacy LSU XML catalog is retained as a
        /// fallback: it's N+1 round trips (catalog + per-package descriptor)
        /// and occasionally sparse for new products (Legion Go 2 MT 83N0 only
        /// has the Dolby Vision kit in LSU at the time of writing), but it's
        /// what Lenovo System Update itself consumes so its data is authoritative
        /// where it exists.
        ///
        /// We intentionally DON'T scan nearby MT codes (83N0..83N9 etc.). Lenovo
        /// reuses the same 3-char prefix across entirely different products —
        /// 83E1 is Legion Go 1, 83N0 is a Legion Go 2 SKU, but 83N2 is an
        /// unrelated laptop. Iterating the family would mix drivers from
        /// completely different machines into the list.
        /// </summary>
        private static async Task<List<DriverEntry>> TryFetchDriversAsync(string machineTypeCode)
        {
            var entries = new List<DriverEntry>();
            if (string.IsNullOrWhiteSpace(machineTypeCode)) return entries;

            // 1) Primary: pcsupport web API (same data the driver-list page shows).
            var fromPcSupport = await TryFetchDriversFromPcSupportAsync(machineTypeCode);
            if (fromPcSupport.Count > 0)
            {
                return fromPcSupport
                    .OrderBy(e => e.Category ?? "", StringComparer.OrdinalIgnoreCase)
                    .ThenBy(e => e.Name ?? "", StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            // 2) Fallback: LSU XML catalog. Win11 then Win10 (older products
            // only exist as Win10 catalogs).
            var candidateUrls = new[]
            {
                $"https://download.lenovo.com/catalog/{machineTypeCode}_Win11.xml",
                $"https://download.lenovo.com/catalog/{machineTypeCode}_Win10.xml",
            };

            List<XElement> packages = null;
            string workingUrl = null;
            foreach (var url in candidateUrls)
            {
                var result = await FetchAndParseCatalogAsync(url);
                if (result != null && result.Count > 0)
                {
                    packages = result;
                    workingUrl = url;
                    break;
                }
            }

            if (packages == null || packages.Count == 0)
            {
                Logger.Info($"Lenovo driver catalog: pcsupport returned 0 AND no LSU catalog for MT {machineTypeCode} — user can use the Open Driver Page button.");
                return entries;
            }

            // Expand each package's per-package XML into a full DriverEntry.
            var semaphore = new SemaphoreSlim(4);
            var expandTasks = packages.Select(async pkg =>
            {
                await semaphore.WaitAsync();
                try { return await ExpandPackageAsync(pkg); }
                finally { semaphore.Release(); }
            }).ToList();

            var expanded = await Task.WhenAll(expandTasks);
            entries.AddRange(expanded.Where(r => r != null));

            // Sort by category then name so the list is stable and readable.
            entries = entries
                .OrderBy(e => e.Category ?? "", StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.Name ?? "", StringComparer.OrdinalIgnoreCase)
                .ToList();
            Logger.Info($"Lenovo driver catalog: MT {machineTypeCode} via {workingUrl} → {entries.Count} expanded drivers");
            return entries;
        }

        /// <summary>
        /// HTTP-fetch a single Lenovo catalog URL, parse it, and return its
        /// &lt;package&gt; elements. Returns null (not an empty list) when the
        /// URL 404s or the body is unparseable so the caller can distinguish
        /// "catalog missing" from "catalog empty" for stats.
        /// </summary>
        private static async Task<List<XElement>> FetchAndParseCatalogAsync(string url)
        {
            try
            {
                using var response = await _http.GetAsync(url);
                if (!response.IsSuccessStatusCode)
                {
                    // 404 is normal for gap MTs within a family — downgrade to Debug.
                    if ((int)response.StatusCode == 404)
                    {
                        Logger.Debug($"Lenovo driver catalog: {url} → 404 (skipping)");
                    }
                    else
                    {
                        Logger.Warn($"Lenovo driver catalog: {url} → HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
                    }
                    return null;
                }
                string body = StripUtf8Bom(await response.Content.ReadAsStringAsync());
                if (string.IsNullOrWhiteSpace(body)) return null;
                XDocument doc;
                try { doc = XDocument.Parse(body); }
                catch (Exception ex)
                {
                    Logger.Warn($"Lenovo driver catalog: parse failed for {url} — {ex.Message}");
                    return null;
                }
                var packages = doc.Root?.Elements("package").ToList() ?? new List<XElement>();
                Logger.Info($"Lenovo driver catalog: {url} → {packages.Count} package(s)");
                return packages;
            }
            catch (Exception ex)
            {
                Logger.Warn($"Lenovo driver catalog: fetch failed for {url} — {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Given a <c>&lt;package&gt;</c> element from the top-level catalog,
        /// fetches its per-package XML and extracts Title/Version/ReleaseDate
        /// plus the installer filename. Returns null on any failure (caller
        /// filters nulls out of the final list).
        /// </summary>
        private static async Task<DriverEntry> ExpandPackageAsync(XElement pkgEntry)
        {
            try
            {
                string category = pkgEntry.Element("category")?.Value?.Trim() ?? "";
                string location = pkgEntry.Element("location")?.Value?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(location)) return null;

                string body;
                try
                {
                    using var response = await _http.GetAsync(location);
                    if (!response.IsSuccessStatusCode) return null;
                    body = await response.Content.ReadAsStringAsync();
                }
                catch { return null; }
                if (string.IsNullOrWhiteSpace(body)) return null;

                XDocument doc;
                try { doc = XDocument.Parse(StripUtf8Bom(body)); }
                catch { return null; }
                var root = doc.Root;
                if (root == null) return null;

                // The per-package XML is deep enough that we grab fields by name
                // wherever they land — Title text is the first <Desc> under
                // <Title>, installer filename lives under Files/Installer/File/Name.
                string title = root.Element("Title")?.Element("Desc")?.Value?.Trim()
                             ?? root.Element("Title")?.Value?.Trim()
                             ?? "";
                string version = root.Element("Version")?.Value?.Trim() ?? "";
                string releaseDate = root.Element("ReleaseDate")?.Value?.Trim() ?? "";
                string severity = root.Element("Severity")?.Attribute("type")?.Value ?? "";

                // Build the installer download URL. Installer filename is
                // relative to the per-package XML URL, same folder.
                string installerName = root
                    .Element("Files")?
                    .Element("Installer")?
                    .Element("File")?
                    .Element("Name")?.Value?.Trim() ?? "";
                string downloadUrl = "";
                if (!string.IsNullOrWhiteSpace(installerName))
                {
                    int lastSlash = location.LastIndexOf('/');
                    if (lastSlash >= 0)
                        downloadUrl = location.Substring(0, lastSlash + 1) + installerName;
                }

                return new DriverEntry
                {
                    Name = title,
                    Category = category,
                    Version = version,
                    ReleaseDate = releaseDate,
                    DownloadUrl = downloadUrl,
                    Severity = severity,
                };
            }
            catch (Exception ex)
            {
                Logger.Debug($"ExpandPackageAsync failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Fallback path when the LSU XML catalog is missing for a product
        /// (common on newly-released hardware — Legion Go 2 MT 83N0 only has
        /// the Dolby Vision kit in LSU at the time of writing).
        ///
        /// pcsupport.lenovo.com's public "driver-list" page is populated by an
        /// undocumented JSON endpoint:
        ///
        ///   GET https://pcsupport.lenovo.com/us/en/api/v4/downloads/drivers?productId={MT}
        ///
        /// Response shape (verified with live traffic, 2026-04):
        ///   {
        ///     "message": "succeed",
        ///     "body": {
        ///       "DownloadItems": [
        ///         {
        ///           "Title": "BIOS Update for Windows 11 (64-bit) ...",
        ///           "Category": { "Name": "BIOS/UEFI", "ID": "..." },
        ///           "Date":    { "Unix": 1776066660000 },  // epoch ms
        ///           "Updated": { "Unix": 1776066779000 },
        ///           "Files": [
        ///             { "Name":"BIOS Update", "TypeString":"EXE",
        ///               "Version":"RRCN16WW",
        ///               "URL":"https://download.lenovo.com/...",
        ///               "Priority":"Recommended", ... },
        ///             { "Name":"BIOS Readme", "TypeString":"TXT README", ... }
        ///           ]
        ///         }, ...
        ///       ]
        ///     }
        ///   }
        ///
        /// Cloudflare on this endpoint returns HTTP 403 to bare requests — we
        /// must send a browser-like UA AND a Referer of the driver-list page
        /// for the same MT, otherwise the request is blocked.
        ///
        /// The API keys on the machine-type code (83N0, 83E1 etc.) — the
        /// consumer SKU names (8ASP2/8AHP2 for Legion Go 2) return 0 items.
        /// WMI always gives us the MT code so this is fine.
        /// </summary>
        private static async Task<List<DriverEntry>> TryFetchDriversFromPcSupportAsync(string machineTypeCode)
        {
            var entries = new List<DriverEntry>();
            if (string.IsNullOrWhiteSpace(machineTypeCode)) return entries;

            string url = $"https://pcsupport.lenovo.com/us/en/api/v4/downloads/drivers?productId={machineTypeCode}";
            string body;
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                // Shared _http has a GoTweaks User-Agent which Cloudflare
                // blocks on this host — overwrite with a browser UA per-request.
                request.Headers.TryAddWithoutValidation("User-Agent",
                    "Mozilla/5.0 (Windows NT 11.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
                request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
                request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
                request.Headers.TryAddWithoutValidation("Referer",
                    $"https://pcsupport.lenovo.com/us/en/products/{machineTypeCode}/downloads/driver-list");

                using var response = await _http.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    Logger.Warn($"pcsupport API: {url} → HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
                    return entries;
                }
                body = await response.Content.ReadAsStringAsync();
            }
            catch (Exception ex)
            {
                Logger.Warn($"pcsupport API: request failed — {ex.GetType().Name}: {ex.Message}");
                return entries;
            }
            if (string.IsNullOrWhiteSpace(body)) return entries;

            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return entries;

                if (!root.TryGetProperty("body", out var bodyObj) || bodyObj.ValueKind != JsonValueKind.Object)
                {
                    Logger.Warn("pcsupport API: response missing 'body' object");
                    return entries;
                }
                if (!bodyObj.TryGetProperty("DownloadItems", out var items) || items.ValueKind != JsonValueKind.Array)
                {
                    Logger.Warn("pcsupport API: response missing 'body.DownloadItems' array");
                    return entries;
                }

                foreach (var item in items.EnumerateArray())
                {
                    var entry = ParsePcSupportEntry(item);
                    if (entry != null) entries.Add(entry);
                }
                Logger.Info($"pcsupport API: MT {machineTypeCode} → {entries.Count} drivers parsed");
            }
            catch (Exception ex)
            {
                Logger.Warn($"pcsupport API: JSON parse failed — {ex.Message}");
            }
            return entries;
        }

        /// <summary>
        /// Extracts Name/Category/Version/ReleaseDate/DownloadUrl from a single
        /// DownloadItems entry. Picks the first non-README file for version +
        /// URL; formats epoch-ms timestamps as ISO dates.
        /// </summary>
        private static DriverEntry ParsePcSupportEntry(JsonElement item)
        {
            if (item.ValueKind != JsonValueKind.Object) return null;

            string title = GetJsonString(item, "Title") ?? "";
            if (string.IsNullOrWhiteSpace(title)) return null;

            // Category is an object { Name, Classify, ID } — only Name is user-facing.
            string category = "";
            if (item.TryGetProperty("Category", out var catEl) && catEl.ValueKind == JsonValueKind.Object)
            {
                category = GetJsonString(catEl, "Name") ?? "";
            }

            // Release date: prefer Updated.Unix (last-updated), fall back to
            // Date.Unix (original). Lenovo returns epoch milliseconds.
            string releaseDate = FormatEpochMs(item, "Updated") ?? FormatEpochMs(item, "Date") ?? "";

            // Files[]: pick the first EXE/BIN/ZIP (skip README/TXT). That's the
            // one the user would download, and its Version is the driver
            // version shown on the website.
            string version = "";
            string downloadUrl = "";
            string severity = "";
            if (item.TryGetProperty("Files", out var filesEl) && filesEl.ValueKind == JsonValueKind.Array)
            {
                JsonElement? primary = null;
                foreach (var f in filesEl.EnumerateArray())
                {
                    var typeStr = (GetJsonString(f, "TypeString") ?? "").ToUpperInvariant();
                    if (typeStr.Contains("README") || typeStr == "TXT") continue;
                    primary = f;
                    break;
                }
                // No non-readme file? fall back to the first file so at least
                // we have a version/URL to show.
                if (primary == null && filesEl.GetArrayLength() > 0)
                {
                    primary = filesEl[0];
                }
                if (primary != null)
                {
                    version = GetJsonString(primary.Value, "Version") ?? "";
                    downloadUrl = GetJsonString(primary.Value, "URL") ?? "";
                    // Lenovo priorities: "Recommended" / "Critical" / "Optional".
                    severity = GetJsonString(primary.Value, "Priority") ?? "";
                }
            }

            return new DriverEntry
            {
                Name = title.Trim(),
                Category = category.Trim(),
                Version = version.Trim(),
                ReleaseDate = releaseDate.Trim(),
                DownloadUrl = downloadUrl.Trim(),
                Severity = severity.Trim(),
            };
        }

        private static string GetJsonString(JsonElement obj, string name)
        {
            if (obj.ValueKind != JsonValueKind.Object) return null;
            if (!obj.TryGetProperty(name, out var v)) return null;
            if (v.ValueKind == JsonValueKind.String) return v.GetString();
            if (v.ValueKind == JsonValueKind.Number) return v.ToString();
            return null;
        }

        /// <summary>
        /// Reads <c>obj[propName].Unix</c> as epoch milliseconds and formats
        /// as an ISO yyyy-MM-dd date. Returns null when the field is missing
        /// or unparseable.
        /// </summary>
        private static string FormatEpochMs(JsonElement obj, string propName)
        {
            if (obj.ValueKind != JsonValueKind.Object) return null;
            if (!obj.TryGetProperty(propName, out var wrapper) || wrapper.ValueKind != JsonValueKind.Object) return null;
            if (!wrapper.TryGetProperty("Unix", out var unixEl) || unixEl.ValueKind != JsonValueKind.Number) return null;
            if (!unixEl.TryGetInt64(out long ms)) return null;
            if (ms <= 0) return null;
            try
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.ToString("yyyy-MM-dd");
            }
            catch { return null; }
        }

        private static string StripUtf8Bom(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            if (s.Length > 0 && s[0] == '\uFEFF') return s.Substring(1);
            return s;
        }

        // -------- Installed driver matching ----------------------------------
        // Build a one-shot snapshot of every PnP driver on the system, then do
        // a fuzzy match (name tokens → device-name tokens) for each catalog
        // entry. Lenovo's category/name text doesn't line up 1:1 with PnP's
        // DeviceName, so we tokenise both sides and require a threshold of
        // shared tokens. Version strings are normalised (slash-separated
        // Lenovo "package/system" versions pick the longer numeric part).

        private sealed class InstalledDriverIndex
        {
            public readonly List<InstalledDriver> Drivers;
            public readonly List<InstalledApp> Apps;
            public readonly string BiosVersion;
            public InstalledDriverIndex(List<InstalledDriver> drivers, List<InstalledApp> apps, string biosVersion)
            {
                Drivers = drivers;
                Apps = apps ?? new List<InstalledApp>();
                BiosVersion = biosVersion ?? "";
            }
        }

        private sealed class InstalledDriver
        {
            public string DeviceName;
            public string DriverProviderName;
            public string DriverVersion;
            public string DriverDate;
            public HashSet<string> NameTokens;
        }

        /// <summary>
        /// An entry from the Add/Remove Programs uninstall registry. Bundle-style
        /// driver installers (AMD Chipset Software, Realtek Audio Driver, NVIDIA
        /// app suites) register here with a single DisplayName + DisplayVersion
        /// instead of as a single PnP row, so this is where we find the version
        /// the catalog wants to compare against. Without this lookup the fuzzy
        /// matcher falls onto a sub-component PnP driver (AMD SMBUS, Realtek
        /// Audio Universal Service) whose version is unrelated to the bundle's.
        /// </summary>
        private sealed class InstalledApp
        {
            public string DisplayName;
            public string DisplayVersion;
            public string Publisher;
            public HashSet<string> NameTokens;
        }

        /// <summary>
        /// Query Win32_PnPSignedDriver once, keep the fields we need, and
        /// pre-tokenise device names. Used to fuzzy-match every catalog entry.
        /// </summary>
        private static InstalledDriverIndex BuildInstalledDriverIndex(string biosVersion)
        {
            var drivers = new List<InstalledDriver>();
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT DeviceName, DriverProviderName, DriverVersion, DriverDate FROM Win32_PnPSignedDriver WHERE DriverVersion IS NOT NULL");
                foreach (ManagementObject obj in searcher.Get())
                {
                    string deviceName = (obj["DeviceName"]?.ToString() ?? "").Trim();
                    string version = (obj["DriverVersion"]?.ToString() ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(deviceName) || string.IsNullOrWhiteSpace(version)) continue;
                    drivers.Add(new InstalledDriver
                    {
                        DeviceName = deviceName,
                        DriverProviderName = (obj["DriverProviderName"]?.ToString() ?? "").Trim(),
                        DriverVersion = version,
                        DriverDate = (obj["DriverDate"]?.ToString() ?? "").Trim(),
                        NameTokens = TokeniseLower(deviceName + " " + (obj["DriverProviderName"]?.ToString() ?? "")),
                    });
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Win32_PnPSignedDriver query failed: {ex.Message}");
            }
            var apps = BuildInstalledAppIndex();
            Logger.Info($"Installed driver index: {drivers.Count} signed PnP drivers loaded, {apps.Count} apps loaded from uninstall registry");
            return new InstalledDriverIndex(drivers, apps, biosVersion);
        }

        /// <summary>
        /// Walks the standard Add/Remove Programs uninstall registry hives so
        /// catalog entries that map to bundle installers (e.g. "AMD Chipset
        /// Driver" → "AMD Chipset Software" with a version like 7.06.02.123;
        /// "Realtek Audio Driver" → "Realtek Audio Driver" with the actual codec
        /// version 6.x.x.x) can be matched against the right installed version
        /// instead of a sub-component PnP driver. Both 32- and 64-bit views are
        /// scanned because some Lenovo-bundled installers register only under
        /// WOW6432Node.
        /// </summary>
        private static List<InstalledApp> BuildInstalledAppIndex()
        {
            var apps = new List<InstalledApp>();
            string[] registryPaths =
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
            };
            foreach (var path in registryPaths)
            {
                try
                {
                    using var hive = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(path);
                    if (hive == null) continue;
                    foreach (var subName in hive.GetSubKeyNames())
                    {
                        try
                        {
                            using var sub = hive.OpenSubKey(subName);
                            if (sub == null) continue;
                            string displayName = (sub.GetValue("DisplayName") as string)?.Trim() ?? "";
                            string displayVersion = (sub.GetValue("DisplayVersion") as string)?.Trim() ?? "";
                            if (string.IsNullOrEmpty(displayName) || string.IsNullOrEmpty(displayVersion)) continue;
                            // Skip Windows updates / hotfixes — they spam the registry and
                            // never match a Lenovo catalog entry by name.
                            if (displayName.StartsWith("Update for Microsoft", StringComparison.OrdinalIgnoreCase)
                                || displayName.StartsWith("Security Update", StringComparison.OrdinalIgnoreCase)
                                || subName.StartsWith("KB", StringComparison.OrdinalIgnoreCase))
                                continue;
                            string publisher = (sub.GetValue("Publisher") as string)?.Trim() ?? "";
                            apps.Add(new InstalledApp
                            {
                                DisplayName = displayName,
                                DisplayVersion = displayVersion,
                                Publisher = publisher,
                                NameTokens = TokeniseLower(displayName + " " + publisher),
                            });
                        }
                        catch (Exception ex)
                        {
                            Logger.Debug($"Uninstall registry sub-key '{subName}' read failed: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Debug($"Uninstall registry hive open failed for '{path}': {ex.Message}");
                }
            }
            return apps;
        }

        private static HashSet<string> TokeniseLower(string s)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(s)) return set;
            // Normalise hyphenated compound terms that Windows writes with a
            // hyphen and catalog writes as one word. Without this pass,
            // "RZ616 Wi-Fi 6E 160MHz" tokenises to {rz616,160mhz,mediatek,inc}
            // — no wifi/wlan/wireless tokens at all — so a MediaTek WLAN catalog
            // row with {mediatek,wlan,wifi,...} only overlaps by 1 and fails the
            // 2-token threshold.
            var normalised = s.ToLowerInvariant()
                .Replace("wi-fi", "wifi")
                .Replace("wi fi", "wifi");
            foreach (var raw in System.Text.RegularExpressions.Regex.Split(normalised, @"[^a-z0-9]+"))
            {
                if (raw.Length < 3) continue;         // skip noise like "a", "of"
                if (_stopWords.Contains(raw)) continue;
                set.Add(raw);
                // Synonym expansion — Lenovo's catalog titles use one name
                // ("WLAN", "Bluetooth") while Windows PnP entries use another
                // ("Wi-Fi", "Wireless", "BT"). Without expansion a MediaTek
                // WLAN row and the installed MT7927 Wi-Fi 7 card share only
                // "mediatek" as a token (1 shared token &lt; 2 threshold) and
                // the row is marked NotInstalled. Adding both sides of each
                // alias makes the threshold catchable.
                if (_tokenAliases.TryGetValue(raw, out var aliases))
                {
                    foreach (var a in aliases) set.Add(a);
                }
            }
            return set;
        }

        /// <summary>
        /// Token synonym map — each alias set is symmetric (each token maps
        /// to every other token in the set). When any member is tokenised we
        /// also add the rest so Lenovo-titled and Windows-PnP-named entries
        /// can still overlap.
        /// </summary>
        private static readonly Dictionary<string, string[]> _tokenAliases = BuildTokenAliases();

        private static Dictionary<string, string[]> BuildTokenAliases()
        {
            var groups = new[]
            {
                // Wireless LAN: catalog says "WLAN", device manager shows "Wi-Fi" /
                // "Wireless" / "WiFi" / "network"; all three are interchangeable.
                new[] { "wlan", "wifi", "wireless", "wlancard" },
                // Bluetooth: catalog says "bluetooth", device sometimes just "bt".
                new[] { "bluetooth", "bthusb", "btdriver" },
                // Display adapter: catalog "VGA" or "graphics" or "video" — Windows usually "display" / "graphics".
                new[] { "vga", "graphics", "display", "video" },
                // Audio: catalog "audio", Windows device "sound" / "audio".
                new[] { "audio", "sound", "hdaudio" },
                // Energy/Power management: the catalog package "Lenovo Energy Management"
                // (category "Power Management") installs as an ACPI device whose DeviceName
                // carries "power" but not "energy" — so catalog {energy,management} and device
                // {acpi,virtual,power,...} share NO literal tokens and the matcher falsely
                // reports the driver as not installed. Bridging energy<->power gives the
                // 2-token overlap the threshold needs (fixes the false "Energy Management
                // driver" update warning on Legion Go handhelds).
                new[] { "energy", "power" },
                // Cardreader: catalog "cardreader", Windows typically "card" / "reader".
                new[] { "cardreader", "reader" },
                // Fingerprint reader: catalog "fingerprinter" (typo in Lenovo's catalog), Windows says "fingerprint" / "biometric".
                new[] { "fingerprint", "fingerprinter", "biometric" },
                // Chipset: was treating "smbus" as a synonym so AMD's SMBUS
                // sub-component would match "AMD Chipset Driver" catalog rows,
                // but that's the wrong match — SMBUS is one of ~10 components
                // in the chipset bundle and its version (2.0.0.x) bears no
                // relation to the bundle version (5.x / 7.x). The bundle's
                // "AMD Chipset Software" entry in the uninstall registry
                // carries the right version, picked up by BuildInstalledAppIndex.
                // Synonym set kept for future expansion (keep "chipset" alone).
                new[] { "chipset" },
            };
            var map = new Dictionary<string, string[]>(StringComparer.Ordinal);
            foreach (var g in groups)
            {
                foreach (var token in g)
                {
                    // Every token maps to the rest of its group.
                    map[token] = g.Where(t => t != token).ToArray();
                }
            }
            return map;
        }

        // Words we exclude from the token match — present in nearly every
        // driver name and therefore useless as a discriminator.
        private static readonly HashSet<string> _stopWords = new HashSet<string>(StringComparer.Ordinal)
        {
            "driver", "drivers", "utility", "software", "and", "for", "the",
            "windows", "win", "x64", "x86", "64", "bit", "download", "legion",
            "lenovo", "universal", "application", "edition",
        };

        /// <summary>
        /// Populates <see cref="DriverEntry.InstalledVersion"/> and
        /// <see cref="DriverEntry.UpdateStatus"/> for a catalog entry by
        /// looking for the best-matching PnP driver (or BIOS for the BIOS
        /// category) in the index.
        /// </summary>
        private static void PopulateInstalledStatus(DriverEntry entry, InstalledDriverIndex index)
        {
            if (entry == null || index == null) return;

            // BIOS is a special case — compared directly to Win32_BIOS.SMBIOSBIOSVersion.
            // Pass the raw catalog Version (e.g. "RRCN16WW") rather than a
            // pre-extracted candidate. The comparator's Lenovo letter-code
            // path ("RRCN14WW" vs "RRCN16WW") needs the full "letters+digits+letters"
            // string on BOTH sides — pre-extracting would strip the "RRCN"
            // prefix and leave "16WW", which the regex can't match.
            bool isBios = LooksLikeBios(entry.Category) || LooksLikeBios(entry.Name);
            if (isBios)
            {
                entry.InstalledVersion = index.BiosVersion;
                entry.UpdateStatus = CompareVersions(index.BiosVersion, entry.Version);
                return;
            }

            // Everything else: fuzzy-match by token overlap against the PnP list.
            var entryTokens = TokeniseLower(entry.Name);
            if (entryTokens.Count == 0)
            {
                entry.UpdateStatus = DriverUpdateStatus.Unknown;
                return;
            }

            // Pre-extract the catalog version's numeric prefix once so the
            // tie-break below can compare it against each candidate's version
            // without re-parsing per iteration.
            var catalogVersionPrefix = ParseVersion(ExtractLatestVersion(entry.Version));

            InstalledDriver bestDriver = null;
            int bestDriverScore = 0;
            int bestDriverVerAffinity = -1;
            foreach (var d in index.Drivers)
            {
                int overlap = 0;
                foreach (var tok in entryTokens)
                {
                    if (d.NameTokens.Contains(tok)) overlap++;
                }
                if (overlap == 0) continue;
                int affinity = VersionPrefixMatch(d.DriverVersion, catalogVersionPrefix);
                // Prefer higher token overlap; on ties prefer the candidate whose
                // version's leading numeric tuple shares more components with
                // the catalog version. Catches the Realtek case where "Realtek
                // High Definition Audio" 6.0.9879.1 and "Realtek Audio Universal
                // Service" 1.0.890.0 both score 4 on token overlap, and PnP
                // iteration order alone picked the latter — but the codec
                // driver's 6.x.y.z shares all four components with catalog
                // RTK_6.0.9879.1, so the version-affinity tie-break flips to
                // the codec.
                if (overlap > bestDriverScore
                    || (overlap == bestDriverScore && affinity > bestDriverVerAffinity))
                {
                    bestDriverScore = overlap;
                    bestDriverVerAffinity = affinity;
                    bestDriver = d;
                }
            }

            // Also score the catalog name against the uninstall-registry app list.
            // For bundle-style installers (AMD Chipset Software, Realtek Audio
            // Driver) this is where the version string Lenovo's catalog actually
            // wants to compare against lives — the PnP list only has the bundle's
            // sub-components and matches them with versions unrelated to the
            // bundle itself.
            InstalledApp bestApp = null;
            int bestAppScore = 0;
            int bestAppVerAffinity = -1;
            foreach (var a in index.Apps)
            {
                int overlap = 0;
                foreach (var tok in entryTokens)
                {
                    if (a.NameTokens.Contains(tok)) overlap++;
                }
                if (overlap == 0) continue;
                int affinity = VersionPrefixMatch(a.DisplayVersion, catalogVersionPrefix);
                if (overlap > bestAppScore
                    || (overlap == bestAppScore && affinity > bestAppVerAffinity))
                {
                    bestAppScore = overlap;
                    bestAppVerAffinity = affinity;
                    bestApp = a;
                }
            }

            // Pick the source whose score is higher. Apps win ties because
            // bundle installers carry the version string Lenovo's catalog is
            // explicitly authoring against — the PnP sub-component matches
            // that tied with them by token count are typically components of
            // the same bundle (AMD Chipset Software → AMD SMBUS scoring 3 vs
            // AMD Chipset Software scoring 3) and a sub-component's version
            // bears no fixed relationship to the bundle version. Both must
            // still meet the 2-token-minimum threshold or we treat as
            // not-installed (avoids 1-shared-token false matches like generic
            // "realtek" hitting unrelated Realtek-prefixed apps).
            const int MinTokensForMatch = 2;
            bool driverEligible = bestDriver != null && bestDriverScore >= MinTokensForMatch;
            bool appEligible = bestApp != null && bestAppScore >= MinTokensForMatch;

            // vvalente30 issue #79: AMD Chipset + Lenovo Energy Mgmt show
            // "not recognized" on his LGO2 while corando + 1 other LGO2 user
            // see them detected. Log the candidate compare so we can tell
            // whether his uninstall registry / PnP layout differs.
            string driverDesc = bestDriver != null
                ? $"'{bestDriver.DeviceName}' v={bestDriver.DriverVersion} score={bestDriverScore} ver-affinity={bestDriverVerAffinity}"
                : "<none>";
            string appDesc = bestApp != null
                ? $"'{bestApp.DisplayName}' v={bestApp.DisplayVersion} score={bestAppScore} ver-affinity={bestAppVerAffinity}"
                : "<none>";

            if (!driverEligible && !appEligible)
            {
                Logger.Info($"Lenovo driver match: '{entry.Name}' catalogVer='{entry.Version}' tokens=[{string.Join(",", entryTokens)}] bestDriver={driverDesc} bestApp={appDesc} → NotInstalled (both below {MinTokensForMatch}-token threshold)");
                entry.InstalledVersion = "";
                entry.UpdateStatus = DriverUpdateStatus.NotInstalled;
                return;
            }

            bool useApp = appEligible && (!driverEligible || bestAppScore >= bestDriverScore);
            if (useApp)
            {
                entry.InstalledVersion = bestApp.DisplayVersion;
                entry.MatchedDeviceName = bestApp.DisplayName ?? "";
                entry.MatchedProvider = bestApp.Publisher ?? "";
                entry.MatchScore = bestAppScore;
            }
            else
            {
                entry.InstalledVersion = bestDriver.DriverVersion;
                entry.MatchedDeviceName = bestDriver.DeviceName ?? "";
                entry.MatchedProvider = bestDriver.DriverProviderName ?? "";
                entry.MatchScore = bestDriverScore;
            }
            // Pass raw catalog Version (could be "Realtek_10.0.26200.21385",
            // "Genesys_1.1.55.0;Realtek_10.0.26200.21385", etc.). CompareVersions
            // splits internally and picks the candidate whose major.minor
            // matches the installed driver's first two fields.
            entry.UpdateStatus = CompareVersions(entry.InstalledVersion, entry.Version);
            Logger.Info($"Lenovo driver match: '{entry.Name}' catalogVer='{entry.Version}' tokens=[{string.Join(",", entryTokens)}] bestDriver={driverDesc} bestApp={appDesc} → picked={(useApp ? "App" : "Driver")} installedVer='{entry.InstalledVersion}' status={entry.UpdateStatus}");
        }

        private static bool LooksLikeBios(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            var lower = s.ToLowerInvariant();
            return lower.Contains("bios") || lower.Contains("uefi");
        }

        /// <summary>
        /// Lenovo's &lt;Version&gt; field is messy and ships several shapes that
        /// the LSU XML catalog and pcsupport web API use interchangeably:
        ///   "10.0.26100.21373"                          - bare dotted numeric
        ///   "1.1.49.0/10.0.26100.21373"                 - "package/system"
        ///   "Genesys_1.1.55.0;Realtek_10.0.26200.21385" - multi-vendor combo
        ///   "Realtek_10.0.26200.21385"                  - single vendor-prefixed
        ///   "RRCN16WW"                                  - BIOS (prefix+digits+suffix)
        ///
        /// Splits on every separator Lenovo has ever shipped and returns all
        /// dotted-numeric substrings so <see cref="CompareVersions"/> can match
        /// the installed driver against the right vendor component.
        /// </summary>
        private static List<string> ExtractAllNumericVersions(string raw)
        {
            var results = new List<string>();
            if (string.IsNullOrWhiteSpace(raw)) return results;
            // Split generously — any separator Lenovo has ever used between
            // vendor tag and numeric version goes here.
            foreach (var piece in raw.Split('/', ';', ',', ' ', '\t', '_'))
            {
                var p = piece.Trim();
                if (p.Length == 0) continue;
                // Strip leading non-digits (e.g. "v1.2.3" -> "1.2.3").
                int i = 0;
                while (i < p.Length && !char.IsDigit(p[i])) i++;
                if (i >= p.Length) continue;
                p = p.Substring(i);
                if (CountDots(p) == 0 && !char.IsDigit(p[0])) continue;
                // Must be valid dotted numeric (at least one dot or a pure integer).
                if (ParseVersion(p) != null) results.Add(p);
            }
            if (results.Count == 0)
            {
                // Fallback: if nothing parsed, keep the original trimmed — the
                // caller has string-equality and BIOS-pattern fallbacks.
                results.Add(raw.Trim());
            }
            return results;
        }

        /// <summary>
        /// Back-compat wrapper used by BIOS comparison — returns the single
        /// longest numeric-dotted version from the raw string.
        /// </summary>
        private static string ExtractLatestVersion(string raw)
        {
            var all = ExtractAllNumericVersions(raw);
            if (all.Count == 0) return raw?.Trim() ?? "";
            string best = all[0];
            int bestDots = CountDots(best);
            for (int i = 1; i < all.Count; i++)
            {
                int dots = CountDots(all[i]);
                if (dots > bestDots) { best = all[i]; bestDots = dots; }
            }
            return best;
        }

        /// <summary>
        /// Counts how many leading numeric components the candidate's version
        /// shares with the catalog's parsed version. Used as a tie-break when
        /// two PnP/app candidates score equally on token overlap — same-codec
        /// driver versions cluster numerically (Realtek High Definition Audio
        /// 6.0.9879.1 vs the same catalog 6.0.9879.1 → affinity 4) while a
        /// companion service in the same vendor namespace doesn't (Realtek
        /// Audio Universal Service 1.0.890.0 vs catalog 6.0.9879.1 →
        /// affinity 0). Returns 0 if either side is unparseable, so the
        /// tie-break degrades to first-found behaviour rather than throwing.
        /// </summary>
        private static int VersionPrefixMatch(string candidateVersion, long[] catalogParts)
        {
            if (catalogParts == null || catalogParts.Length == 0) return 0;
            var c = ParseVersion(candidateVersion);
            if (c == null) return 0;
            int len = Math.Min(c.Length, catalogParts.Length);
            int i = 0;
            while (i < len && c[i] == catalogParts[i]) i++;
            return i;
        }

        private static int CountDots(string s)
        {
            int n = 0;
            foreach (var c in s) if (c == '.') n++;
            return n;
        }

        /// <summary>
        /// Compares an installed driver version string against a Lenovo
        /// catalog version string. Three matching strategies in order:
        ///   1. Extract every dotted-numeric candidate from the catalog
        ///      string. If any matches the installed version's first two
        ///      fields (major.minor), compare full dotted tuples and use
        ///      that result. This handles the "Genesys_1.1.55.0;Realtek_10.0.26200.21385"
        ///      vs installed "10.0.26100.21377" case — we pick the Realtek
        ///      candidate because it shares major.minor with the installed
        ///      driver, then compare build numbers (26100 &lt; 26200 =&gt; update).
        ///   2. Lenovo BIOS pattern ([LETTERS][DIGITS][LETTERS], same
        ///      prefix+suffix). "RRCN14WW" vs "RRCN16WW" compares 14 vs 16.
        ///   3. Fall back to string-equality for anything else.
        /// </summary>
        private static DriverUpdateStatus CompareVersions(string installed, string latest)
        {
            if (string.IsNullOrWhiteSpace(installed)) return DriverUpdateStatus.NotInstalled;
            if (string.IsNullOrWhiteSpace(latest)) return DriverUpdateStatus.Unknown;

            var inst = ParseVersion(installed);
            if (inst != null)
            {
                var candidates = ExtractAllNumericVersions(latest)
                    .Select(c => new { Raw = c, Parts = ParseVersion(c) })
                    .Where(c => c.Parts != null)
                    .ToList();

                // 1) Exact-tuple match wins. Catches the multi-vendor catalog case
                // ("Elan_3.4.12404.14001;Goodix_3.4.523.1010" vs installed
                // "3.4.523.1010") cleanly: when the installed version IS one of the
                // catalog vendors verbatim, treat it as up-to-date without falling
                // into the ambiguous major.minor branch below — which used to pick
                // the first candidate sharing major.minor (Elan), then compare
                // 523 < 12404 and flag it as an update for Goodix users.
                var exactMatch = candidates.FirstOrDefault(c =>
                    c.Parts.Length == inst.Length &&
                    c.Parts.SequenceEqual(inst));
                if (exactMatch != null)
                {
                    return DriverUpdateStatus.UpToDate;
                }

                // 2) Among candidates that share major.minor with installed, prefer
                // the one whose third field (build) is closest. Same-vendor versions
                // in Lenovo's multi-vendor catalogs cluster — Goodix's older builds
                // are near 3.4.523.x, Elan's near 3.4.12404.x — so this picks the
                // right vendor when installed sits between them but isn't an exact
                // match for either.
                var majorMinorMatches = candidates.Where(c =>
                    c.Parts.Length >= 2 && inst.Length >= 2 &&
                    c.Parts[0] == inst[0] && c.Parts[1] == inst[1]).ToList();

                long[] chosen;
                if (majorMinorMatches.Count == 1)
                {
                    chosen = majorMinorMatches[0].Parts;
                }
                else if (majorMinorMatches.Count > 1)
                {
                    chosen = majorMinorMatches
                        .OrderBy(c => (c.Parts.Length >= 3 && inst.Length >= 3)
                            ? Math.Abs(c.Parts[2] - inst[2])
                            : long.MaxValue)
                        .First().Parts;
                }
                else
                {
                    chosen = candidates.Count > 0
                        ? candidates.OrderByDescending(c => c.Parts.Length).First().Parts
                        : null;
                }

                if (chosen != null)
                {
                    int len = Math.Max(inst.Length, chosen.Length);
                    for (int i = 0; i < len; i++)
                    {
                        long a = i < inst.Length ? inst[i] : 0;
                        long b = i < chosen.Length ? chosen[i] : 0;
                        if (a < b) return DriverUpdateStatus.UpdateAvailable;
                        if (a > b) return DriverUpdateStatus.UpToDate;
                    }
                    return DriverUpdateStatus.UpToDate;
                }
            }

            // Lenovo BIOS-style code: letters + digits + letters, same bookends.
            // E.g. installed "RRCN14WW" vs latest "RRCN16WW" — strip the shared
            // "RRCN"/"WW" and compare the 14 vs 16 numerically.
            var biosCompare = CompareLenovoLetterCodes(installed, latest);
            if (biosCompare.HasValue) return biosCompare.Value;

            return string.Equals(installed, latest, StringComparison.OrdinalIgnoreCase)
                ? DriverUpdateStatus.UpToDate
                : DriverUpdateStatus.Unknown;
        }

        /// <summary>Parses a dotted numeric string to long[]; returns null on junk.</summary>
        private static long[] ParseVersion(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var parts = s.Split('.');
            var result = new long[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                // Accept leading digits only — tolerates suffixes like "26100.21373-legion"
                var digits = new string(parts[i].TakeWhile(char.IsDigit).ToArray());
                if (digits.Length == 0) return null;
                if (!long.TryParse(digits, out result[i])) return null;
            }
            return result;
        }

        // Regex for Lenovo BIOS/firmware codes like "RRCN14WW": letter prefix,
        // numeric middle, letter suffix. Captures the three pieces so we can
        // compare the middle numerically when prefix+suffix match.
        private static readonly System.Text.RegularExpressions.Regex _lenovoCodeRegex =
            new System.Text.RegularExpressions.Regex(@"^([A-Z]+)(\d+)([A-Z]+)$",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        private static DriverUpdateStatus? CompareLenovoLetterCodes(string installed, string latest)
        {
            var mi = _lenovoCodeRegex.Match(installed?.Trim().ToUpperInvariant() ?? "");
            var ml = _lenovoCodeRegex.Match(latest?.Trim().ToUpperInvariant() ?? "");
            if (!mi.Success || !ml.Success) return null;
            if (!string.Equals(mi.Groups[1].Value, ml.Groups[1].Value, StringComparison.Ordinal)) return null;
            if (!string.Equals(mi.Groups[3].Value, ml.Groups[3].Value, StringComparison.Ordinal)) return null;
            if (!long.TryParse(mi.Groups[2].Value, out long a)) return null;
            if (!long.TryParse(ml.Groups[2].Value, out long b)) return null;
            if (a < b) return DriverUpdateStatus.UpdateAvailable;
            if (a > b) return DriverUpdateStatus.UpToDate;
            return DriverUpdateStatus.UpToDate;
        }
    }

    internal enum DriverUpdateStatus
    {
        Unknown = 0,
        UpToDate = 1,
        UpdateAvailable = 2,
        NotInstalled = 3,
    }

    internal sealed class DriverUpdateResult
    {
        public bool IsLenovo { get; set; }
        public string Manufacturer { get; set; } = "";
        public string Model { get; set; } = "";
        public string ModelVersion { get; set; } = "";
        public string MachineTypeCode { get; set; } = "";
        public string BiosVersion { get; set; } = "";
        public string DriverPageUrl { get; set; } = "";
        public bool LiveFetchSucceeded { get; set; }
        public List<DriverEntry> Drivers { get; set; } = new List<DriverEntry>();
        public string ErrorMessage { get; set; } = "";

        /// <summary>Serializes to the compact JSON shape the widget consumes via pipe.</summary>
        public string ToJson()
        {
            return JsonSerializer.Serialize(this, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            });
        }
    }

    internal sealed class DriverEntry
    {
        public string Name { get; set; } = "";
        public string Category { get; set; } = "";
        public string Version { get; set; } = "";
        public string ReleaseDate { get; set; } = "";
        public string DownloadUrl { get; set; } = "";
        /// <summary>Lenovo severity type attribute: "1"=critical, "2"=recommended, "3"=optional.</summary>
        public string Severity { get; set; } = "";
        /// <summary>Installed version detected on this device (PnP DriverVersion, or Win32_BIOS for BIOS rows). Empty when not installed / unmatched.</summary>
        public string InstalledVersion { get; set; } = "";
        /// <summary>Comparison status vs <see cref="Version"/>. Serialises as an int the widget interprets.</summary>
        public DriverUpdateStatus UpdateStatus { get; set; } = DriverUpdateStatus.Unknown;
        /// <summary>PnP DeviceName the fuzzy-match picked. Diagnostic-only; serialised but the widget ignores it.</summary>
        public string MatchedDeviceName { get; set; } = "";
        /// <summary>PnP DriverProviderName for the matched driver. Diagnostic — helps spot wrong-vendor matches without re-running the user.</summary>
        public string MatchedProvider { get; set; } = "";
        /// <summary>Token-overlap score (entry-name tokens shared with the matched PnP driver). Diagnostic.</summary>
        public int MatchScore { get; set; }
    }
}
