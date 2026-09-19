using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BedrockLauncher.UpdateProcessor.Authentication;
using BedrockLauncher.UpdateProcessor.Classes;
using BedrockLauncher.UpdateProcessor.Databases;
using BedrockLauncher.UpdateProcessor.Enums;

namespace BedrockLauncher.UpdateProcessor.Handlers
{
    public class VersionManager
    {
        #region Singleton management

        private static VersionManager _singleton;

        /// <summary>
        /// The single active <see cref="VersionManager"/> instance.
        /// Returns null (with a warning) if <see cref="VersionManager"/> has not yet been constructed.
        /// </summary>
        public static VersionManager Singleton
        {
            get
            {
                if (_singleton == null)
                    Trace.TraceWarning("Trying to access uninitialized VersionManager singleton.");
                return _singleton;
            }
            private set
            {
                if (_singleton != null)
                    Trace.TraceWarning("Attempt to override VersionManager singleton denied.");
                else
                    _singleton = value;
            }
        }

        public VersionManager()
        {
            Singleton = this;
        }

        #endregion

        // ------------------------------------------------------------------ //
        //  Delegates
        // ------------------------------------------------------------------ //
        public delegate void DownloadProgress(long current, long total);

        // ------------------------------------------------------------------ //
        //  Configuration
        // ------------------------------------------------------------------ //
        private int UserTokenIndex;

        /// <summary>Fallback community version-database URLs, tried in order.</summary>
        private static readonly string[] communityDBUrls =
        {
            "https://mrarm.io/r/w10-vdb",
            "https://www.raythnetwork.co.uk/versions.php?type=json"
        };

        /// <summary>GdkLinks manifest URLs (minified first for speed).</summary>
        private static readonly string[] gdkLinksUrls =
        {
            "https://raw.githubusercontent.com/MinecraftBedrockArchiver/GdkLinks/refs/heads/master/urls.min.json",
            "https://raw.githubusercontent.com/MinecraftBedrockArchiver/GdkLinks/master/urls.json"
        };

        private string winstoreDBFile;
        private string communityDBFile;
        private string gdkLinksDBFile;

        // ------------------------------------------------------------------ //
        //  Shared HTTP client (reused across requests to avoid socket exhaustion)
        // ------------------------------------------------------------------ //
        private readonly HttpClient HttpClient = new HttpClient();

        private readonly StoreNetwork StoreNetwork = new StoreNetwork();
        private readonly List<VersionInfoJson> Versions = new List<VersionInfoJson>();
        private readonly Dictionary<string, List<string>> GdkDownloadUrls =
            new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        // ------------------------------------------------------------------ //
        //  Public API
        // ------------------------------------------------------------------ //

        public List<VersionInfoJson> GetVersions() => Versions.ToList();

        /// <summary>
        /// Attempts to retrieve the list of direct download URLs for a GDK version.
        /// </summary>
        /// <param name="versionUuid">UUID string of the version.</param>
        /// <param name="urls">Output list of CDN URLs.</param>
        /// <returns>True when at least one URL is available.</returns>
        public bool TryGetGdkDownloadUrls(string versionUuid, out List<string> urls)
        {
            if (GdkDownloadUrls.TryGetValue(versionUuid ?? string.Empty, out urls) &&
                urls != null && urls.Count > 0)
                return true;
            urls = null;
            return false;
        }

        /// <summary>Initialises file-path configuration. Must be called before <see cref="LoadVersions"/>.</summary>
        public void Init(int userTokenIndex, string winstoreDBFile, string communityDBFile, string gdkLinksDBFile = null)
        {
            UserTokenIndex      = userTokenIndex;
            this.winstoreDBFile  = winstoreDBFile;
            this.communityDBFile = communityDBFile;
            this.gdkLinksDBFile  = gdkLinksDBFile
                ?? Path.Combine(Path.GetDirectoryName(communityDBFile) ?? ".", "gdk_links_versions.json");
        }

        /// <summary>
        /// Downloads a Minecraft package to <paramref name="destination"/>.
        /// GDK packages are downloaded directly from Xbox CDN (via GdkLinks);
        /// UWP packages fall back to the Microsoft Store download link.
        /// </summary>
        public async Task DownloadVersion(
            string versionName,
            string updateIdentity,
            int revisionNumber,
            string destination,
            DownloadProgress progress,
            CancellationToken cancellationToken,
            VersionType type)
        {
            // Prefer GdkLinks CDN URLs for GDK builds when available.
            if (TryGetGdkDownloadUrls(updateIdentity, out var gdkUrls))
            {
                Exception lastError = null;
                foreach (var url in gdkUrls)
                {
                    try
                    {
                        Trace.WriteLine($"Downloading GDK package from CDN: {url}");
                        await DownloadFromDirectUrl(url, destination, progress, cancellationToken);
                        return;
                    }
                    catch (Exception ex)
                    {
                        lastError = ex;
                        Trace.WriteLine($"CDN mirror failed: {url} — {ex.Message}");
                    }
                }
                throw new IOException($"All GdkLinks CDN mirrors failed for '{versionName}'", lastError);
            }

            // Fall back to UWP / Windows Store download.
            string link = await StoreNetwork.getDownloadLink(updateIdentity, revisionNumber, type);
            if (link == null)
                throw new ArgumentException($"Could not resolve download link for '{versionName}' (updateId: {updateIdentity})");

            Trace.WriteLine($"Downloading UWP package: {link}");
            await DownloadFromDirectUrl(link, destination, progress, cancellationToken);
        }

        // ------------------------------------------------------------------ //
        //  Internal download helper
        // ------------------------------------------------------------------ //

        private async Task DownloadFromDirectUrl(
            string url,
            string destination,
            DownloadProgress progress,
            CancellationToken cancellationToken)
        {
            string temporaryPath = destination + ".download";

            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);

            try
            {
                using var resp = await HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                resp.EnsureSuccessStatusCode();

                long totalSize   = resp.Content.Headers.ContentLength ?? -1;
                long transferred = 0;
                var  buf         = new byte[1024 * 1024]; // 1 MiB read buffer

                progress(0, totalSize > 0 ? totalSize : 1);

                // Progress is reported asynchronously to avoid blocking the I/O loop.
                Task progressTask   = null;
                CancellationTokenSource progressCts = new CancellationTokenSource();

                using (var inStream  = await resp.Content.ReadAsStreamAsync())
                using (var outStream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true))
                {
                    while (true)
                    {
                        int n = await inStream.ReadAsync(buf, 0, buf.Length, cancellationToken);
                        if (n == 0) break;
                        await outStream.WriteAsync(buf, 0, n, cancellationToken);
                        transferred += n;
                        ScheduleProgressReport(ref progressTask, ref progressCts, transferred,
                            totalSize > 0 ? totalSize : transferred, progress);
                    }
                }

                if (!File.Exists(temporaryPath) || new FileInfo(temporaryPath).Length == 0)
                {
                    throw new IOException("Il pacchetto scaricato è vuoto.");
                }

                if (File.Exists(destination))
                    File.Delete(destination);

                File.Move(temporaryPath, destination);

                // Ensure the final progress report fires.
                progress(transferred, totalSize > 0 ? totalSize : transferred);
            }
            catch
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
                throw;
            }
        }

        private static void ScheduleProgressReport(
            ref Task task,
            ref CancellationTokenSource cts,
            long current,
            long total,
            DownloadProgress progress)
        {
            // Cancel any in-flight progress task and start a fresh one.
            if (task != null && !task.IsCompleted)
            {
                cts.Cancel();
                cts.Dispose();
                cts = new CancellationTokenSource();
            }
            task = Task.Run(() => progress(current, total), cts.Token);
        }

        // ------------------------------------------------------------------ //
        //  Version loading pipeline
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Refreshes the in-memory version list.
        /// </summary>
        /// <param name="getNewVersions">When true, fetches updated data from remote sources.</param>
        /// <param name="checkMicrosoftStore">When true, also queries the Microsoft Store / WU network.</param>
        public async Task LoadVersions(bool getNewVersions, bool checkMicrosoftStore)
        {
            Versions.Clear();
            GdkDownloadUrls.Clear();

            await EnableUserAuthorization();

            // 1. Community JSON database (UWP versions).
            VersionJsonDb communityDB = LoadJsonDBVersions(communityDBFile);
            if (getNewVersions)
                await UpdateDBFromURL(communityDB, communityDBFile, communityDBUrls);

            // 2. Windows Store database (UWP versions via WU API).
            VersionJsonDb winStoreDB = LoadJsonDBVersions(winstoreDBFile);
            if (getNewVersions && checkMicrosoftStore)
                await UpdateDBFromStore(winStoreDB, winstoreDBFile);

            // 3. GdkLinks (direct CDN URLs for GDK builds) — always loaded.
            await LoadGdkLinksVersions(getNewVersions);
        }

        private async Task LoadGdkLinksVersions(bool getNewVersions)
        {
            try
            {
                string cachePath = gdkLinksDBFile;
                string rawJson   = null;

                if (getNewVersions)
                {
                    foreach (var url in gdkLinksUrls)
                    {
                        try
                        {
                            Trace.WriteLine($"Fetching GdkLinks manifest from: {url}");
                            var resp = await HttpClient.GetAsync(url);
                            resp.EnsureSuccessStatusCode();
                            rawJson = await resp.Content.ReadAsStringAsync();
                            Directory.CreateDirectory(Path.GetDirectoryName(cachePath) ?? ".");
                            File.WriteAllText(cachePath, rawJson);
                            Trace.WriteLine($"GdkLinks cache saved: {cachePath}");
                            break; // Success — no need to try remaining URLs.
                        }
                        catch (Exception ex)
                        {
                            Trace.WriteLine($"GdkLinks fetch failed [{url}]: {ex.Message}");
                        }
                    }
                }

                // Fall back to cached file when the network is unavailable.
                if (rawJson == null && File.Exists(cachePath))
                    rawJson = File.ReadAllText(cachePath);

                if (string.IsNullOrWhiteSpace(rawJson))
                {
                    Trace.WriteLine("No GdkLinks data available (no network and no cache).");
                    return;
                }

                var gdkDb = new GdkLinksDb();
                gdkDb.Parse(rawJson);

                foreach (var pair in gdkDb.DownloadUrlsByUuid)
                    GdkDownloadUrls[pair.Key] = pair.Value;

                int added = 0;
                foreach (var version in gdkDb.Versions)
                {
                    if (!MinecraftVersion.TryParse(version.GetVersion(), out _)) continue;

                    // Skip exact UUID duplicates.
                    if (Versions.Exists(x => x.GetUUID() == version.GetUUID())) continue;

                    // If an equivalent entry exists from another source, replace it with the
                    // GdkLinks entry so the caller can use its direct CDN URLs.
                    Versions.RemoveAll(x =>
                        x.GetVersion() == version.GetVersion() &&
                        x.GetArchitecture() == version.GetArchitecture() &&
                        x.GetVersionType() == version.GetVersionType() &&
                        x.GetPackageType() == version.GetPackageType());

                    Versions.Add(version);
                    added++;
                }

                Trace.WriteLine($"GdkLinks: {added} new version(s) added (total CDN entries: {GdkDownloadUrls.Count}).");
            }
            catch (Exception ex)
            {
                Trace.WriteLine("LoadGdkLinksVersions failed:");
                Trace.WriteLine(ex);
            }
        }

        private async Task UpdateDBFromURL(VersionJsonDb db, string filePath, string[] urls)
        {
            foreach (var url in urls)
            {
                try
                {
                    if (File.Exists(filePath)) File.Delete(filePath);
                    var resp = await HttpClient.GetAsync(url);
                    resp.EnsureSuccessStatusCode();
                    var data = await resp.Content.ReadAsStringAsync();
                    db.ParseRaw(data, GetVersionArches());
                    db.Save(filePath);
                    InsertVersionsFromDB(db);
                    Trace.WriteLine($"Community DB updated from: {url}");
                    return; // Success — stop trying.
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"UpdateDBFromURL failed [{url}]: {ex.Message}");
                }
            }
            Trace.TraceWarning("All community DB URLs failed.");
        }

        /// <summary>
        /// Queries the Microsoft Store / WU network for new UWP versions and persists them.
        /// </summary>
        private async Task UpdateDBFromStore(VersionJsonDb jsonDb, string jsonFilePath)
        {
            try
            {
                if (File.Exists(jsonFilePath)) File.Delete(jsonFilePath);
                await FetchStoreVersions(VersionType.Release, jsonDb);
                await FetchStoreVersions(VersionType.Preview, jsonDb);
                jsonDb.Save(jsonFilePath);
                InsertVersionsFromDB(jsonDb);
            }
            catch (Exception ex)
            {
                Trace.WriteLine("UpdateDBFromStore failed:");
                Trace.WriteLine(ex);
            }
        }

        private async Task FetchStoreVersions(VersionType type, VersionJsonDb jsonDb)
        {
            try
            {
                var config = await StoreNetwork.fetchConfigLastChanged();
                var cookie = await StoreNetwork.fetchCookie(config, type);

                var knownVersions = jsonDb.GetVersions().ConvertAll(x => x.GetUUID().ToString());
                var result = await StoreManager.CheckForUWPVersions(StoreNetwork, type, cookie, knownVersions);
                jsonDb.AddVersion(result, type);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"FetchStoreVersions failed (type={type}):");
                Trace.WriteLine(ex);
            }
        }

        private VersionJsonDb LoadJsonDBVersions(string filePath)
        {
            try
            {
                var db = new VersionJsonDb();
                db.ReadJson(filePath, GetVersionArches());
                db.WriteJson(filePath); // Re-save to normalise formatting.
                InsertVersionsFromDB(db);
                return db;
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"LoadJsonDBVersions failed for '{filePath}' — creating empty DB.");
                Trace.WriteLine(ex);
                var db = new VersionJsonDb();
                db.Save(filePath);
                return db;
            }
        }

        private void InsertVersionsFromDB(VersionJsonDb db)
        {
            foreach (var version in db.list)
            {
                if (!MinecraftVersion.TryParse(version.GetVersion(), out _)) continue;
                if (Versions.Exists(x => x.GetUUID() == version.GetUUID())) continue;
                if (Versions.Exists(x =>
                        x.GetVersion() == version.GetVersion() &&
                        x.GetArchitecture() == version.GetArchitecture() &&
                        x.GetVersionType() == version.GetVersionType() &&
                        x.GetPackageType() == version.GetPackageType())) continue;
                Versions.Add(version);
            }
        }

        private async Task EnableUserAuthorization()
        {
            try
            {
                var token = await Task.Run(() => AuthenticationManager.Default.GetWUToken(UserTokenIndex));
                StoreNetwork.setMSAUserToken(token);
            }
            catch (Exception ex)
            {
                // Non-fatal: the launcher can still fetch public (unauthenticated) versions.
                Trace.WriteLine($"EnableUserAuthorization failed (token index {UserTokenIndex}): {ex.Message}");
            }
        }

        private Dictionary<Guid, string> GetVersionArches()
            => Versions.ToDictionary(x => x.GetUUID(), x => x.GetArchitecture());
    }
}
