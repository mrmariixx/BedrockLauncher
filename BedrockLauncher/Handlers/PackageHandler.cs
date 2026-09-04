using BedrockLauncher.Classes;
using BedrockLauncher.Downloaders;
using JemExtensions;
using SymbolicLinkSupport;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Xml.Linq;
using Windows.ApplicationModel;
using Windows.Foundation;
using Windows.Management.Deployment;
using Windows.System;
using ZipProgress = JemExtensions.ZipFileExtensions.ZipProgress;
using BedrockLauncher.Enums;
using System.Windows.Input;
using BedrockLauncher.ViewModels;
using BedrockLauncher.Exceptions;
using BedrockLauncher.UpdateProcessor;
using BedrockLauncher.UpdateProcessor.Authentication;
using BedrockLauncher.UpdateProcessor.Handlers;
using BedrockLauncher.Classes.Launcher;
using Windows.System.Diagnostics;
using BedrockLauncher.UpdateProcessor.Enums;
using JemExtensions.WPF.Commands;
using BedrockLauncher.UI.Pages.Common;
using System.Collections;
using BedrockLauncher.UpdateProcessor.Classes;

namespace BedrockLauncher.Handlers
{
    public class PackageHandler : IDisposable
    {
        private CancellationTokenSource CancelSource = new CancellationTokenSource();
        private PackageManager PM = new PackageManager();

        public VersionDownloader VersionDownloader { get; private set; } = new VersionDownloader();
        public Process GameHandle { get; private set; } = null;
        public bool isGameRunning { get => GameHandle != null; }

        #region Public Methods

        public async Task LaunchPackage(MCVersion v, string dirPath, bool KeepLauncherOpen, bool LaunchEditor)
        {
            try
            {
                StartTask();
                MainDataModel.Default.ProgressBarState.SetProgressBarState(LauncherState.isLaunching);

                // 1. Ensure the package is registered at v.GameDirectory before launching
                if (!IsPackageRegistered(v))
                {
                    Trace.WriteLine($"Package not registered for {v.Name} at {v.GameDirectory}, registering in DevelopmentMode...");
                    await UnregisterPackage(v, keepVersion: false);
                    await RegisterPackage(v);
                }

                // 2. Launch registered development package directly via AppDiagnosticInfo (the correct UWP/GDK launch method)
                try
                {
                    var pkgList = await AppDiagnosticInfo.RequestInfoForPackageAsync(Constants.GetPackageFamily(v.Type));
                    if (pkgList != null && pkgList.Count > 0)
                    {
                        Trace.WriteLine($"Launching registered package {Constants.GetPackageFamily(v.Type)} from {v.GameDirectory}");
                        var activationResult = await pkgList[0].LaunchAsync();
                        if (activationResult.ExtendedError != null)
                        {
                            Trace.WriteLine("LaunchAsync warning: " + activationResult.ExtendedError.Message);
                        }
                        else
                        {
                            Trace.WriteLine("App launch finished via AppDiagnosticInfo!");
                            if (!KeepLauncherOpen)
                                await Application.Current.Dispatcher.InvokeAsync(() => Application.Current.MainWindow.Close());
                            else
                                await GetGameHandle(Constants.MINECRAFT_PROCESS_NAME);
                            return;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Trace.WriteLine("AppDiagnosticInfo launch error: " + ex.Message);
                }

                // 3. Fallback: launch local executable directly
                if (!LaunchEditor && File.Exists(v.ExecutablePath))
                {
                    Trace.WriteLine("Launching local executable directly: " + v.ExecutablePath);
                    var psi = new ProcessStartInfo(v.ExecutablePath)
                    {
                        WorkingDirectory = v.GameDirectory,
                        UseShellExecute = true
                    };
                    Process.Start(psi);
                    Trace.WriteLine("App launch finished!");
                    if (!KeepLauncherOpen)
                        await Application.Current.Dispatcher.InvokeAsync(() => Application.Current.MainWindow.Close());
                    else
                        await GetGameHandle(Constants.MINECRAFT_PROCESS_NAME);
                    return;
                }

                // 4. Fallback: protocol URI
                if (await Launcher.LaunchUriAsync(new Uri($"{Constants.GetUri(v.Type)}:?Editor={LaunchEditor}")))
                {
                    Trace.WriteLine("App launch finished via URI!");
                    if (!KeepLauncherOpen)
                        await Application.Current.Dispatcher.InvokeAsync(() => Application.Current.MainWindow.Close());
                    else
                        await GetGameHandle(Constants.MINECRAFT_PROCESS_NAME);
                }
                else
                {
                    SetException(new AppLaunchFailedException($"Impossible to launch Minecraft: package not found or failed to start", new Exception()));
                }
            }
            catch (Exception e)
            {
                EndTask();
                SetException(new AppLaunchFailedException(e));
            }
        }


        public async Task InstallPackage(MCVersion v, string dirPath)
        {
            try
            {
                StartTask();

                bool hasFiles = v.HasPlayableFiles;
                if (!hasFiles)
                {
                    List<VersionInfoJson> versions = VersionManager.Singleton.GetVersions();
                    // Match by UUID or by version name (GdkLinks may remap UUIDs)
                    bool known = versions.Any(ver =>
                        string.Equals(v.UUID, ver.uuid.ToString(), StringComparison.OrdinalIgnoreCase)
                        || string.Equals(v.Name, ver.version, StringComparison.OrdinalIgnoreCase));

                    if (!known)
                        throw new NoVersionAccessibleException();

                    await DownloadAndExtractPackage(v);
                }

                if (!IsPackageRegistered(v))
                {
                    await UnregisterPackage(v, keepVersion: false);
                    await RegisterPackage(v);
                }
                else
                {
                    Trace.WriteLine($"Skipping redeploy for {v.Name} — already registered at {v.GameDirectory}");
                }

                await RedirectSaveData(dirPath, v.Type);
            }
            catch (PackageManagerException e)
            {
                SetException(e);
            }
            catch (NoVersionAccessibleException e)
            {
                SetException(e);
            }
            catch (Exception e)
            {
                SetException(new AppInstallFailedException(e));
            }
            finally
            {
                EndTask();
            }
        }
        public async Task ClosePackage()
        {
            if (GameHandle != null)
            {
                string title = BedrockLauncher.Localization.Language.LanguageManager.GetResource("Dialog_KillGame_Title") as string;
                string content = BedrockLauncher.Localization.Language.LanguageManager.GetResource("Dialog_KillGame_Text") as string;
                var result = await DialogPrompt.ShowDialog_YesNo(title, content);

                if (result == System.Windows.Forms.DialogResult.Yes) GameHandle.Kill();
            }
        }
        public async Task RemovePackage(MCVersion v)
        {
            try
            {
                StartTask();

                MainDataModel.Default.ProgressBarState.SetProgressBarState(LauncherState.isUninstalling);
                await UnregisterPackage(v, false, true);
                MainDataModel.Default.ProgressBarState.SetProgressBarState(LauncherState.isUninstalling);
                await DirectoryExtensions.DeleteAsync(v.GameDirectory, (x, y, phase) => ProgressWrapper(x, y, phase), "Files", "Folders");
                if (Directory.Exists(v.GameDirectory)) Directory.Delete(v.GameDirectory, true);
                v.UpdateFolderSize();
                await Task.Run(Program.OnApplicationRefresh);
                foreach (var ver in MainDataModel.Default.Versions) ver.UpdateFolderSize();
            }
            catch (PackageManagerException e)
            {
                SetException(e);
            }
            catch (Exception ex)
            {
                SetException(new PackageRemovalFailedException(ex));
            }
            finally
            {
                EndTask();
            }
        }
        public async Task AddPackage(string packagePath)
        {
            try
            {
                if (!File.Exists(packagePath)) return;
                StartTask();
                var outputDirectoryName = FileExtensions.GetAvaliableFileName(Path.GetFileNameWithoutExtension(packagePath), MainDataModel.Default.FilePaths.VersionsFolder);
                var outputDirectoryPath = Path.Combine(MainDataModel.Default.FilePaths.VersionsFolder, outputDirectoryName);
                Trace.WriteLine("Extraction started");
                MainDataModel.Default.ProgressBarState.SetProgressBarState(LauncherState.isExtracting);
                if (Directory.Exists(outputDirectoryPath)) Directory.Delete(outputDirectoryPath, true);
                var fileStream = File.OpenRead(packagePath);
                var progress = new Progress<ZipProgress>();
                progress.ProgressChanged += (s, z) => MainDataModel.Default.ProgressBarState.SetProgressBarProgress(currentProgress: z.Processed, totalProgress: z.Total);
                await Task.Run(() => new ZipArchive(fileStream).ExtractToDirectory(outputDirectoryPath, progress, CancelSource));
                fileStream.Close();
                File.Delete(Path.Combine(outputDirectoryPath, "AppxSignature.p7x"));
                File.Move(packagePath, Path.Combine(MainDataModel.Default.FilePaths.VersionsFolder, "AppxBackups", packagePath));
                Trace.WriteLine("Extracted successfully");
                await Task.Run(Program.OnApplicationRefresh);
                foreach (var ver in MainDataModel.Default.Versions) ver.UpdateFolderSize();
            }
            catch (PackageManagerException e)
            {
                SetException(e);
            }
            catch (Exception e)
            {
                SetException(new PackageAddFailedException(e));
            }
            finally
            {
                EndTask();
            }
        }
        public async Task DownloadPackage(MCVersion v)
        {
            try
            {
                StartTask();
                await DownloadAndExtractPackage(v);
            }
            catch (PackageManagerException e)
            {
                SetException(e);
            }
            catch (Exception e)
            {
                SetException(new PackageDownloadAndExtractFailedException(e));
            }
            finally
            {
                EndTask();
            }
        }
        public void Cancel()
        {
            if (CancelSource != null && !CancelSource.IsCancellationRequested) CancelSource.Cancel();
        }

        #endregion

        #region Private Throwable Methods

        private async Task GetGameHandle(string processName) => await Task.Run(async () =>
                                                                         {
                                                                             try
                                                                             {
                                                                                 Process attached = null;
                                                                                 for (int attempt = 0; attempt < 60 && attached == null; attempt++)
                                                                                 {
                                                                                     var processes = Process.GetProcessesByName(processName);
                                                                                     if (processes.Length >= 1)
                                                                                     {
                                                                                         attached = processes[0];
                                                                                         break;
                                                                                     }
                                                                                     await Task.Delay(500);
                                                                                 }

                                                                                 if (attached != null)
                                                                                 {
                                                                                     MainDataModel.Default.ProgressBarState.SetGameRunningStatus(true);
                                                                                     GameHandle = attached;
                                                                                     GameHandle.EnableRaisingEvents = true;
                                                                                     GameHandle.Exited += OnPackageExit;

                                                                                     void OnPackageExit(object sender, EventArgs e)
                                                                                     {
                                                                                         Process p = sender as Process;
                                                                                         p.Exited -= OnPackageExit;
                                                                                         GameHandle = null;
                                                                                         MainDataModel.Default.ProgressBarState.SetGameRunningStatus(false);
                                                                                     }

                                                                                     Trace.WriteLine("Successfully attached Minecraft process");
                                                                                 }
                                                                                 else
                                                                                 {
                                                                                     Trace.WriteLine("Failed to attach Minecraft process: timed out waiting for process");
                                                                                     GameHandle = null;
                                                                                     MainDataModel.Default.ProgressBarState.SetGameRunningStatus(false);
                                                                                 }
                                                                             }
                                                                             catch (InvalidOperationException e)
                                                                             {
                                                                                 throw e;
                                                                             }
                                                                             catch (Exception e)
                                                                             {
                                                                                 throw new PackageProcessHookFailedException(e);
                                                                             }
                                                                             finally
                                                                             {
                                                                                 EndTask();
                                                                             }
                                                                         });

        private async Task DownloadAndExtractPackage(MCVersion v)
        {
            try
            {
                string versionsRoot = Path.GetFullPath(MainDataModel.Default.FilePaths.VersionsFolder);
                string gameDir = Path.GetFullPath(v.GameDirectory);
                Trace.WriteLine($"Download start: {v.PackageID} ({v.PackageType})");
                Trace.WriteLine($"Versions root: {versionsRoot}");
                Trace.WriteLine($"Game directory: {gameDir}");
                SetCancelation(true);

                Directory.CreateDirectory(versionsRoot);
                string subDirectory = Path.Combine(versionsRoot, "AppxBackups");
                Directory.CreateDirectory(subDirectory);

                bool isGdkCdn = VersionManager.Singleton != null && VersionManager.Singleton.TryGetGdkDownloadUrls(v.PackageID, out _);
                string extension = isGdkCdn || v.PackageType == PackageType.GDK ? ".msixvc" : ".Appx";
                string fileName = "Minecraft-" + v.Name + extension;
                // Always download/store under %AppData%\.minecraft_bedrock\versions\AppxBackups (or FixedDirectory equivalent)
                string bkpsPath = Path.Combine(subDirectory, fileName);
                string dlPath = bkpsPath;

                // Prefer existing backups (msixvc or legacy appx)
                if (!File.Exists(bkpsPath))
                {
                    string altAppx = Path.Combine(subDirectory, "Minecraft-" + v.Name + ".Appx");
                    if (File.Exists(altAppx))
                    {
                        bkpsPath = altAppx;
                        dlPath = altAppx;
                    }
                }

                // Migrate accidental downloads left next to the exe
                string cwdLegacy = Path.Combine(Directory.GetCurrentDirectory(), fileName);
                if (!File.Exists(bkpsPath) && File.Exists(cwdLegacy))
                {
                    Trace.WriteLine($"Moving legacy download into versions folder: {cwdLegacy} -> {bkpsPath}");
                    File.Move(cwdLegacy, bkpsPath);
                }

                string pkgPath = File.Exists(bkpsPath) ? bkpsPath : dlPath;

                if (!File.Exists(pkgPath))
                    await DownloadPackage(v, dlPath, CancelSource);

                await ExtractPackage(v, dlPath, bkpsPath, pkgPath, CancelSource);

                v.UpdateFolderSize();
                Trace.WriteLine($"Package ready at: {gameDir}");
            }
            catch (PackageManagerException e)
            {
                ResetTask();
                throw e;
            }
            catch (Exception ex)
            {
                ResetTask();
                throw new Exception("DownloadAndExtractPackage Failed", ex);
            }
            finally
            {
                ResetTask();
                SetCancelation(false);
                CancelSource = null;
            }

        }
        private async Task DownloadPackage(MCVersion v, string dlPath, CancellationTokenSource cancelSource)
        {
            try
            {
                // Beta Store auth is handled inside VersionManager when needed; GdkLinks CDN needs no MSA token.
                MainDataModel.Default.ProgressBarState.SetProgressBarState(LauncherState.isDownloading);
                Trace.WriteLine("Download starting -> " + dlPath);
                await VersionDownloader.DownloadVersion(v.DisplayName, v.PackageID, 1, dlPath, (x, y) => ProgressWrapper(x, y), cancelSource.Token, v.Type);
                Trace.WriteLine("Download complete");
            }
            catch (PackageManagerException e)
            {
                ResetTask();
                throw e;
            }
            catch (TaskCanceledException e)
            {
                ResetTask();
                throw new PackageDownloadCanceledException(e);
            }
            catch (Exception e)
            {
                ResetTask();
                throw new PackageDownloadFailedException(e);
            }
            finally
            {
                ResetTask();
            }
        }
        private async Task RegisterPackage(MCVersion v)
        {
            try
            {
                Trace.WriteLine($"Registering package ({v.PackageType}): {v.Name}");
                MainDataModel.Default.ProgressBarState.SetProgressBarState(LauncherState.isRegisteringPackage);

                if (!File.Exists(v.ManifestPath) && File.Exists(Path.Combine(v.GameDirectory, "MicrosoftGame.Config")))
                {
                    EnsureWDAppManifest(v.GameDirectory, v.Type);
                }

                if (File.Exists(v.ManifestPath))
                {
                    if (!FixGDKManifest(v.ManifestPath, v.Type))
                        throw new IOException($"Could not patch manifest at {v.ManifestPath} (it may be read-only or locked by another process). Registration aborted to avoid installing with an unpatched manifest.");

                    MainDataModel.Default.ProgressBarState.SetProgressBarText(v.GetPackageNameFromMainifest());
                    Trace.WriteLine("Registering loose package from manifest (DevelopmentMode): " + v.ManifestPath);
                    await DeploymentProgressWrapper(PM.RegisterPackageAsync(new Uri(v.ManifestPath), null, Constants.PackageDeploymentOptions));
                }
                else
                {
                    string packageFile = FindSignedPackageBackup(v);
                    if (!string.IsNullOrEmpty(packageFile) && File.Exists(packageFile))
                    {
                        MainDataModel.Default.ProgressBarState.SetProgressBarText(Path.GetFileName(packageFile));
                        Trace.WriteLine("Staging signed package via AddPackageAsync: " + packageFile);
                        DeploymentOptions options = Constants.StorePackageDeploymentOptions;
                        await DeploymentProgressWrapper(PM.AddPackageAsync(new Uri(packageFile), null, options));
                    }
                    else
                    {
                        throw new FileNotFoundException($"Cannot register package {v.Name}: manifest not found at {v.ManifestPath}");
                    }
                }

                Trace.WriteLine("App re-register done!");
            }
            catch (PackageManagerException e)
            {
                ResetTask();
                throw e;
            }
            catch (Exception e)
            {
                ResetTask();
                throw new PackageRegistrationFailedException(e);
            }
            finally
            {
                ResetTask();
            }

        }

        private static string FindSignedPackageBackup(MCVersion v)
        {
            string subDirectory = Path.Combine(MainDataModel.Default.FilePaths.VersionsFolder, "AppxBackups");
            string[] candidates =
            {
                Path.Combine(subDirectory, "Minecraft-" + v.Name + ".msixvc"),
                Path.Combine(subDirectory, "Minecraft-" + v.Name + ".Appx"),
                Path.Combine(subDirectory, "Minecraft-" + v.Name + ".Msix"),
                Path.Combine(Directory.GetCurrentDirectory(), "Minecraft-" + v.Name + ".msixvc"),
                Path.Combine(Directory.GetCurrentDirectory(), "Minecraft-" + v.Name + ".Appx")
            };
            return candidates.FirstOrDefault(File.Exists);
        }

        private static bool FixGDKManifest(string path, VersionType versionType)
        {
            if (!File.Exists(path)) return false;
            try
            {
                // Files extracted from .msixvc/.Appx packages often come through read-only, which used to make
                // XmlWriter.Create fail silently below and leave the manifest unpatched. Clear it so the patch always applies.
                var attributes = File.GetAttributes(path);
                if (attributes.HasFlag(FileAttributes.ReadOnly))
                {
                    File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
                    Trace.WriteLine("[FixGDKManifest] Cleared read-only attribute on manifest: " + path);
                }

                XDocument doc = XDocument.Load(path);
                XNamespace ns = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
                XNamespace uap = "http://schemas.microsoft.com/appx/manifest/uap/windows10";
                XNamespace rescap = "http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities";

                // Ensure proper package identity
                var identity = doc.Descendants(ns + "Identity").FirstOrDefault();
                if (identity != null)
                {
                    string targetName = versionType == VersionType.Preview ? "Microsoft.MinecraftWindowsBeta" : "Microsoft.MinecraftUWP";
                    identity.SetAttributeValue("Name", targetName);
                    identity.SetAttributeValue("Publisher", "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US");
                }

                var apps = doc.Descendants(ns + "Application");
                foreach (var app in apps)
                {
                    var executable = app.Attribute("Executable");
                    if (executable != null && (executable.Value == "GameLaunchHelper.exe" || executable.Value == "gamelaunchhelper.exe" || executable.Value == "GDKLaunchShim.exe"))
                    {
                        executable.Value = "Minecraft.Windows.exe";
                    }
                    app.SetAttributeValue("EntryPoint", "Windows.FullTrustApplication");
                }

                // Remove DesktopAppX / GamingServices extensions that cause msgamelaunch or Store update checks
                var extensions = doc.Root?.Elements(ns + "Extensions").ToList();
                if (extensions != null)
                {
                    foreach (var ext in extensions)
                    {
                        ext.Remove();
                    }
                }

                // Also remove App-level extensions that register msgamelaunch
                var appExtensions = doc.Descendants(ns + "Extensions").ToList();
                foreach (var ext in appExtensions)
                {
                    ext.Remove();
                }

                var capabilities = doc.Descendants(ns + "Capabilities");
                var customInstall = capabilities
                    .Elements(rescap + "Capability")
                    .Where(c => c.Attribute("Name")?.Value == "customInstallActions")
                    .ToList();
                foreach (var cap in customInstall)
                {
                    cap.Remove();
                }

                var settings = new System.Xml.XmlWriterSettings
                {
                    Encoding = new System.Text.UTF8Encoding(false), // no BOM
                    Indent = true
                };
                using (var w = System.Xml.XmlWriter.Create(path, settings))
                {
                    doc.Save(w);
                }
                Trace.WriteLine("[FixGDKManifest] Patched manifest to prevent auto-update and msgamelaunch errors: " + path);
                return true;
            }
            catch (Exception ex)
            {
                Trace.WriteLine("[FixGDKManifest] ERROR: could not patch manifest at " + path + ": " + ex);
                return false;
            }
        }

        private static bool EnsureWDAppManifest(string gameDirectory, VersionType versionType)
        {
            try
            {
                string configPath = Path.Combine(gameDirectory, "MicrosoftGame.Config");
                string manifestPath = Path.Combine(gameDirectory, "AppxManifest.xml");

                if (!File.Exists(configPath)) return false;

                XDocument configDoc = XDocument.Load(configPath);
                var root = configDoc.Root;
                if (root == null) return false;

                var identityElem = root.Element("Identity");
                string name = versionType == VersionType.Preview ? "Microsoft.MinecraftWindowsBeta" : "Microsoft.MinecraftUWP";
                string publisher = "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US";
                string version = identityElem?.Attribute("Version")?.Value ?? "1.0.0.0";
                string arch = identityElem?.Attribute("ProcessorArchitecture")?.Value ?? "x64";

                var visualsElem = root.Element("ShellVisuals");
                string displayName = visualsElem?.Attribute("DefaultDisplayName")?.Value ?? "Minecraft";
                string publisherDisplayName = visualsElem?.Attribute("PublisherDisplayName")?.Value ?? "Mojang";
                string storeLogo = visualsElem?.Attribute("StoreLogo")?.Value ?? "StoreLogo.png";
                string square150 = visualsElem?.Attribute("Square150x150Logo")?.Value ?? "Logo.png";
                string square44 = visualsElem?.Attribute("Square44x44Logo")?.Value ?? "SmallLogo.png";
                string description = visualsElem?.Attribute("Description")?.Value ?? "Minecraft";
                string splashImage = visualsElem?.Attribute("SplashScreenImage")?.Value ?? "SplashScreen.png";

                string manifestContent = $@"<?xml version=""1.0"" encoding=""utf-8""?>
<Package xmlns=""http://schemas.microsoft.com/appx/manifest/foundation/windows10""
         xmlns:uap=""http://schemas.microsoft.com/appx/manifest/uap/windows10""
         xmlns:desktop6=""http://schemas.microsoft.com/appx/manifest/desktop/windows10/6""
         xmlns:rescap=""http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities""
         IgnorableNamespaces=""uap desktop6 rescap"">
  <Identity Name=""{name}"" Publisher=""{publisher}"" Version=""{version}"" ProcessorArchitecture=""{arch}"" />
  <Properties>
    <DisplayName>{displayName}</DisplayName>
    <PublisherDisplayName>{publisherDisplayName}</PublisherDisplayName>
    <Logo>{storeLogo}</Logo>
    <Description>{description}</Description>
    <desktop6:RegistryWriteVirtualization>disabled</desktop6:RegistryWriteVirtualization>
    <desktop6:FileSystemWriteVirtualization>disabled</desktop6:FileSystemWriteVirtualization>
  </Properties>
  <Dependencies>
    <TargetDeviceFamily Name=""Windows.Desktop"" MinVersion=""10.0.18362.0"" MaxVersionTested=""10.0.18362.0"" />
  </Dependencies>
  <Resources>
    <Resource Language=""en-us"" />
  </Resources>
  <Applications>
    <Application Id=""App"" Executable=""Minecraft.Windows.exe"" EntryPoint=""Windows.FullTrustApplication"">
      <uap:VisualElements DisplayName=""{displayName}"" Square150x150Logo=""{square150}"" Square44x44Logo=""{square44}"" Description=""{description}"" ForegroundText=""light"" BackgroundColor=""#000000"">
        <uap:SplashScreen Image=""{splashImage}"" />
      </uap:VisualElements>
    </Application>
  </Applications>
  <Capabilities>
    <Capability Name=""internetClient"" />
    <rescap:Capability Name=""runFullTrust"" />
    <rescap:Capability Name=""appLicensing"" />
    <rescap:Capability Name=""unvirtualizedResources"" />
  </Capabilities>
</Package>";

                File.WriteAllText(manifestPath, manifestContent);
                Trace.WriteLine("[EnsureWDAppManifest] Created manifest from MicrosoftGame.Config: " + manifestPath);
                return true;
            }
            catch (Exception ex)
            {
                Trace.WriteLine("[EnsureWDAppManifest] ERROR: could not create manifest: " + ex);
                return false;
            }
        }
        private async Task ExtractPackage(MCVersion v, string dlPath, string bkpsPath, string pkgPath, CancellationTokenSource cancelSource)
        {
            try
            {
                Trace.WriteLine("Extraction started");
                MainDataModel.Default.ProgressBarState.SetProgressBarState(LauncherState.isExtracting);

                if (Directory.Exists(v.GameDirectory))
                    await DirectoryExtensions.DeleteAsync(v.GameDirectory, (x, y, phase) => ProgressWrapper(x, y, phase));

                bool extractedAsZip = false;
                try
                {
                    using var fileStream = File.OpenRead(pkgPath);
                    byte[] header = new byte[4];
                    int read = fileStream.Read(header, 0, 4);
                    fileStream.Position = 0;
                    bool looksLikeZip = read == 4 && header[0] == (byte)'P' && header[1] == (byte)'K';

                    if (looksLikeZip)
                    {
                        var progress = new Progress<ZipProgress>();
                        progress.ProgressChanged += (s, z) => MainDataModel.Default.ProgressBarState.SetProgressBarProgress(currentProgress: z.Processed, totalProgress: z.Total);
                        await Task.Run(() =>
                        {
                            using var zipArchive = new ZipArchive(fileStream);
                            zipArchive.ExtractToDirectory(v.GameDirectory, progress, cancelSource);
                        });
                        extractedAsZip = true;
                    }
                }
                catch (Exception ex)
                {
                    Trace.WriteLine("Zip extraction not available for this package (expected for some .msixvc):");
                    Trace.WriteLine(ex);
                }

                Directory.CreateDirectory(v.GameDirectory);
                await File.WriteAllTextAsync(v.IdentificationPath, v.PackageID);

                if (extractedAsZip)
                {
                    string signaturePath = Path.Combine(v.GameDirectory, "AppxSignature.p7x");
                    // Keep signature for GDK/signed packages; strip only classic UWP sideload packages.
                    if (v.PackageType == PackageType.UWP && File.Exists(signaturePath))
                        File.Delete(signaturePath);
                }
                else
                {
                    // Non-zip CDN package: keep backup under versions\AppxBackups and marker in version folder.
                    Trace.WriteLine("Package is not a zip archive; will install from signed package file.");
                    string absolutePkg = Path.GetFullPath(pkgPath);
                    await File.WriteAllTextAsync(Path.Combine(v.GameDirectory, "cdn_package.txt"), absolutePkg);

                    // Ensure the package file itself lives under the versions tree.
                    string versionsBackups = Path.Combine(Path.GetFullPath(MainDataModel.Default.FilePaths.VersionsFolder), "AppxBackups");
                    Directory.CreateDirectory(versionsBackups);
                    string desiredBackup = Path.Combine(versionsBackups, Path.GetFileName(absolutePkg));
                    if (!Path.GetFullPath(absolutePkg).Equals(Path.GetFullPath(desiredBackup), StringComparison.OrdinalIgnoreCase))
                    {
                        if (File.Exists(desiredBackup)) File.Delete(desiredBackup);
                        File.Copy(absolutePkg, desiredBackup, true);
                        await File.WriteAllTextAsync(Path.Combine(v.GameDirectory, "cdn_package.txt"), desiredBackup);
                    }
                }

                // Download already targets AppxBackups; only clean up accidental relative downloads.
                if (File.Exists(dlPath) && !Path.GetFullPath(dlPath).Equals(Path.GetFullPath(bkpsPath), StringComparison.OrdinalIgnoreCase))
                {
                    if (!File.Exists(bkpsPath))
                        File.Move(dlPath, bkpsPath);
                    else
                        File.Delete(dlPath);
                }

                Trace.WriteLine("Extracted successfully -> " + Path.GetFullPath(v.GameDirectory));
            }
            catch (PackageManagerException e)
            {
                ResetTask();
                throw e;
            }
            catch (TaskCanceledException e)
            {
                MainDataModel.Default.ProgressBarState.SetProgressBarState(LauncherState.isCanceling);
                await DirectoryExtensions.DeleteAsync(v.GameDirectory, (x, y, phase) => ProgressWrapper(x, y, phase));
                ResetTask();
                throw new PackageExtractionCanceledException(e);
            }
            catch (Exception e)
            {
                ResetTask();
                throw new PackageExtractionFailedException(e);
            }
            finally
            {
                ResetTask();
            }
        }
        private async Task UnregisterPackage(MCVersion v, bool keepVersion = false, bool mustMatchVersion = false)
        {
            try
            {
                foreach (var pkg in PM.FindPackagesForUser(string.Empty, Constants.GetPackageFamily(v.Type)))
                {
                    string location;

                    try { location = pkg.InstalledLocation.Path; }
                    catch (FileNotFoundException) { location = string.Empty; }

                    if (location == v.GameDirectory && keepVersion)
                    {
                        Trace.WriteLine("Skipping package removal - same path: " + pkg.Id.FullName + " " + location);
                        continue;
                    }

                    if (location != v.GameDirectory && mustMatchVersion) continue;

                    Trace.WriteLine("Removing package: " + pkg.Id.FullName);

                    MainDataModel.Default.ProgressBarState.SetProgressBarText(pkg.Id.FullName);
                    MainDataModel.Default.ProgressBarState.SetProgressBarState(LauncherState.isRemovingPackage);
                    await DeploymentProgressWrapper(PM.RemovePackageAsync(pkg.Id.FullName, Constants.PackageRemovalOptions));
                    Trace.WriteLine("Removal of package done: " + pkg.Id.FullName);
                }
            }
            catch (PackageManagerException e)
            {
                ResetTask();
                throw e;
            }
            catch (Exception ex)
            {
                ResetTask();
                throw new PackageDeregistrationFailedException(ex);
            }
            finally
            {
                ResetTask();
            }
        }
        private async Task RedirectSaveData(string InstallationsFolderPath, VersionType type) => await Task.Run(() =>
                                                                                                          {
                                                                                                              try
                                                                                                              {
                                                                                                                  string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

                                                                                                                  string LocalStateFolder = Path.Combine(localAppData, "Packages", Constants.GetPackageFamily(type), "LocalState");
                                                                                                                  string PackageFolder = Path.Combine(localAppData, "Packages", Constants.GetPackageFamily(type), "LocalState", "games", "com.mojang");
                                                                                                                  string PackageBakFolder = Path.Combine(localAppData, "Packages", Constants.GetPackageFamily(type), "LocalState", "games", "com.mojang.default");
                                                                                                                  string ProfileFolder = Path.GetFullPath(InstallationsFolderPath);

                                                                                                                  string RequiredDir = Directory.GetParent(PackageFolder).FullName;
                                                                                                                  if (Directory.Exists(PackageFolder)) Directory.Delete(PackageFolder, true);
                                                                                                                  if (!Directory.Exists(RequiredDir)) Directory.CreateDirectory(RequiredDir);
                                                                                                                  DirectoryInfo profileDir = Directory.CreateDirectory(ProfileFolder);

                                                                                                                  // Attempt to create a symlink without elevated privileges
                                                                                                                  bool symlinkCreated = SymLinkHelper.CreateSymbolicLinkSafe(PackageFolder, ProfileFolder, SymLinkHelper.SymbolicLinkType.Directory);
                                                                                                                  if (!symlinkCreated)
                                                                                                                  {
                                                                                                                      throw new SaveRedirectionFailedException(new Exception("Failed to create symbolic link. Ensure Developer Mode is enabled or run as administrator."));
                                                                                                                  }

                                                                                                                  DirectoryInfo pkgDir = Directory.CreateDirectory(PackageFolder);
                                                                                                                  DirectoryInfo lsDir = Directory.CreateDirectory(LocalStateFolder);

                                                                                                                  SecurityIdentifier owner = WindowsIdentity.GetCurrent().User;
                                                                                                                  SecurityIdentifier authenticated_users_identity = new SecurityIdentifier("S-1-5-11");

                                                                                                                  FileSystemAccessRule owner_access_rules = new FileSystemAccessRule(owner, FileSystemRights.FullControl, InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow);
                                                                                                                  FileSystemAccessRule au_access_rules = new FileSystemAccessRule(authenticated_users_identity, FileSystemRights.FullControl, InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow);

                                                                                                                  var lsSecurity = lsDir.GetAccessControl();
                                                                                                                  AuthorizationRuleCollection rules = lsSecurity.GetAccessRules(true, true, typeof(NTAccount));
                                                                                                                  List<FileSystemAccessRule> needed_rules = new List<FileSystemAccessRule>();
                                                                                                                  foreach (AccessRule rule in rules)
                                                                                                                  {
                                                                                                                      if (rule.IdentityReference is SecurityIdentifier)
                                                                                                                      {
                                                                                                                          var required_rule = new FileSystemAccessRule(rule.IdentityReference, FileSystemRights.FullControl, rule.InheritanceFlags, rule.PropagationFlags, rule.AccessControlType);
                                                                                                                          needed_rules.Add(required_rule);
                                                                                                                      }
                                                                                                                  }

                                                                                                                  var pkgSecurity = pkgDir.GetAccessControl();
                                                                                                                  pkgSecurity.SetOwner(owner);
                                                                                                                  pkgSecurity.AddAccessRule(au_access_rules);
                                                                                                                  pkgSecurity.AddAccessRule(owner_access_rules);
                                                                                                                  pkgDir.SetAccessControl(pkgSecurity);

                                                                                                                  var profileSecurity = profileDir.GetAccessControl();
                                                                                                                  //profileSecurity.SetOwner(owner);
                                                                                                                  profileSecurity.AddAccessRule(au_access_rules);
                                                                                                                  profileSecurity.AddAccessRule(owner_access_rules);
                                                                                                                  needed_rules.ForEach(x => profileSecurity.AddAccessRule(x));
                                                                                                                  profileDir.SetAccessControl(profileSecurity);
                                                                                                              }
                                                                                                              catch (PackageManagerException e)
                                                                                                              {
                                                                                                                  throw e;
                                                                                                              }
                                                                                                              catch (Exception e)
                                                                                                              {
                                                                                                                  throw new SaveRedirectionFailedException(e);
                                                                                                              }
                                                                                                          });
        private bool IsPackageRegistered(MCVersion v)
        {
            try
            {
                var packages = PM.FindPackagesForUser(string.Empty, Constants.GetPackageFamily(v.Type));
                foreach (var pkg in packages)
                {
                    string location = string.Empty;
                    try { location = pkg.InstalledLocation?.Path ?? string.Empty; }
                    catch { }

                    if (!string.IsNullOrEmpty(location) && string.Equals(Path.GetFullPath(location), Path.GetFullPath(v.GameDirectory), StringComparison.OrdinalIgnoreCase))
                        return true;

                    try
                    {
                        var id = pkg.Id;
                        if (id != null)
                        {
                            string verStr = $"{id.Version.Major}.{id.Version.Minor}.{id.Version.Build}.{id.Version.Revision}";
                            if (string.Equals(verStr, v.Name, StringComparison.OrdinalIgnoreCase))
                                return true;
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine("IsPackageRegistered check error: " + ex.Message);
            }
            return false;
        }

        private async Task MaterializeVersionFolderAsync(MCVersion v) => await Task.Run(async () =>
                                                                                  {
                                                                                      try
                                                                                      {
                                                                                          if (File.Exists(v.ExecutablePath))
                                                                                          {
                                                                                              Trace.WriteLine($"Version {v.Name} already materialized at {v.GameDirectory}");
                                                                                              return;
                                                                                          }

                                                                                          string sourcePath = null;
                                                                                          var packages = PM.FindPackagesForUser(string.Empty, Constants.GetPackageFamily(v.Type));
                                                                                          foreach (var pkg in packages)
                                                                                          {
                                                                                              try
                                                                                              {
                                                                                                  string loc = pkg.InstalledLocation?.Path;
                                                                                                  if (!string.IsNullOrEmpty(loc) && Directory.Exists(loc) && File.Exists(Path.Combine(loc, "Minecraft.Windows.exe")))
                                                                                                  {
                                                                                                      sourcePath = loc;
                                                                                                      break;
                                                                                                  }
                                                                                              }
                                                                                              catch { }
                                                                                          }

                                                                                          if (string.IsNullOrEmpty(sourcePath) || !Directory.Exists(sourcePath))
                                                                                          {
                                                                                              string windowsApps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
                                                                                              if (Directory.Exists(windowsApps))
                                                                                              {
                                                                                                  try
                                                                                                  {
                                                                                                      var dirs = Directory.GetDirectories(windowsApps, "Microsoft.Minecraft*");
                                                                                                      foreach (var d in dirs)
                                                                                                      {
                                                                                                          if (File.Exists(Path.Combine(d, "Minecraft.Windows.exe")))
                                                                                                          {
                                                                                                              sourcePath = d;
                                                                                                              break;
                                                                                                          }
                                                                                                      }
                                                                                                  }
                                                                                                  catch { }
                                                                                              }
                                                                                          }

                                                                                          if (!string.IsNullOrEmpty(sourcePath) && Directory.Exists(sourcePath))
                                                                                          {
                                                                                              Trace.WriteLine($"Copying deployed GDK files from {sourcePath} to {v.GameDirectory}...");
                                                                                              MainDataModel.Default.ProgressBarState.SetProgressBarState(LauncherState.isExtracting);

                                                                                              Directory.CreateDirectory(v.GameDirectory);
                                                                                              await CopyDirectoryAsync(sourcePath, v.GameDirectory);

                                                                                              await File.WriteAllTextAsync(v.IdentificationPath, v.PackageID);

                                                                                              Trace.WriteLine($"Materialization complete for {v.Name} at {v.GameDirectory}");
                                                                                              v.UpdateFolderSize();
                                                                                          }
                                                                                      }
                                                                                      catch (Exception ex)
                                                                                      {
                                                                                          Trace.WriteLine("MaterializeVersionFolderAsync error: " + ex);
                                                                                      }
                                                                                  });

        private static async Task CopyDirectoryAsync(string sourceDir, string targetDir) => await Task.Run(() =>
                                                                                                     {
                                                                                                         Directory.CreateDirectory(targetDir);

                                                                                                         foreach (string file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
                                                                                                         {
                                                                                                             string relativePath = Path.GetRelativePath(sourceDir, file);
                                                                                                             string targetFilePath = Path.Combine(targetDir, relativePath);
                                                                                                             string targetFileDir = Path.GetDirectoryName(targetFilePath);
                                                                                                             if (!string.IsNullOrEmpty(targetFileDir))
                                                                                                                 Directory.CreateDirectory(targetFileDir);

                                                                                                             try
                                                                                                             {
                                                                                                                 if (!File.Exists(targetFilePath) || new FileInfo(targetFilePath).Length != new FileInfo(file).Length)
                                                                                                                 {
                                                                                                                     File.Copy(file, targetFilePath, true);
                                                                                                                 }
                                                                                                             }
                                                                                                             catch (Exception ex)
                                                                                                             {
                                                                                                                 Trace.WriteLine($"Could not copy file {file}: {ex.Message}");
                                                                                                             }
                                                                                                         }
                                                                                                     });
        #endregion

        #region Helpers

        protected async Task DeploymentProgressWrapper(IAsyncOperationWithProgress<DeploymentResult, DeploymentProgress> t)
        {
            TaskCompletionSource<int> src = new TaskCompletionSource<int>();
            t.Progress += (v, p) => MainDataModel.Default.ProgressBarState.SetProgressBarProgress(currentProgress: Convert.ToInt64(p.percentage), totalProgress: 100);
            t.Completed += (v, p) =>
            {
                MainDataModel.Default.ProgressBarState.ResetProgressBarProgress();

                if (p == AsyncStatus.Error)
                {
                    Trace.WriteLine("Deployment failed: " + v.GetResults().ErrorText);
                    src.SetException(new Exception("Deployment failed: " + v.GetResults().ErrorText));
                }
                else
                {
                    Trace.WriteLine("Deployment done: " + p);
                    src.SetResult(1);
                }
            };
            await src.Task;
        }
        protected void ProgressWrapper(long current, long total, string text = null)
        {
            MainDataModel.Default.ProgressBarState.SetProgressBarProgress(current, total);
            MainDataModel.Default.ProgressBarState.SetProgressBarText(text);
        }
        protected void ResetTask()
        {
            MainDataModel.Default.ProgressBarState.ResetProgressBarProgress();
            MainDataModel.Default.ProgressBarState.SetProgressBarText();
            MainDataModel.Default.ProgressBarState.SetProgressBarState(LauncherState.None);
        }
        protected void EndTask()
        {
            MainDataModel.Default.ProgressBarState.ResetProgressBarProgress();
            MainDataModel.Default.ProgressBarState.SetProgressBarText();
            MainDataModel.Default.ProgressBarState.SetProgressBarState(LauncherState.None);
            MainDataModel.Default.ProgressBarState.SetProgressBarVisibility(false);
        }
        protected void StartTask()
        {
            MainDataModel.Default.ProgressBarState.SetProgressBarState(LauncherState.isInitializing);
            MainDataModel.Default.ProgressBarState.SetProgressBarVisibility(true);

        }
        protected void SetCancelation(bool cancelState)
        {
            if (cancelState) CancelSource = new CancellationTokenSource();
            MainDataModel.Default.ProgressBarState.AllowCancel = cancelState ? true : false;
            MainDataModel.Default.ProgressBarState.CancelCommand = cancelState ? new RelayCommand((o) => Cancel()) : null;
        }
        protected void SetException(Exception e)
        {
            if (e.GetType() == typeof(PackageExtractionFailedException)) SetError(e, "Extraction failed", "Error_AppExtractionFailed_Title", "Error_AppExtractionFailed");
            else if (e.GetType() == typeof(PackageDownloadFailedException)) SetError(e, "Download failed", "Error_AppDownloadFailed_Title", "Error_AppDownloadFailed");
            else if (e.GetType() == typeof(BetaAuthenticationFailedException)) SetError(e, "Authentication failed", "Error_AuthenticationFailed_Title", "Error_AuthenticationFailed");
            else if (e.GetType() == typeof(AppLaunchFailedException)) SetError(e, "App launch failed", "Error_AppLaunchFailed_Title", "Error_AppLaunchFailed");
            else if (e.GetType() == typeof(PackageRegistrationFailedException)) SetError(e, "App registeration failed", "Error_AppReregisterFailed_Title", "Error_AppReregisterFailed");
            else if (e.GetType() == typeof(PackageRemovalFailedException)) SetError(e, "App uninstall failed", "Error_AppUninstallFailed_Title", "Error_AppUninstallFailed");
            else if (e.GetType() == typeof(SaveRedirectionFailedException)) SetError(e, "Save redirection failed", "Error_SaveDirectoryRedirectionFailed_Title", "Error_SaveDirectoryRedirectionFailed");
            else if (e.GetType() == typeof(PackageDeregistrationFailedException)) SetError(e, "App deregisteration failed", "Error_AppDeregisteringFailed_Title", "Error_AppDeregisteringFailed");

            else if (e.GetType() == typeof(PackageDownloadAndExtractFailedException)) SetGenericError(e);
            else if (e.GetType() == typeof(PackageProcessHookFailedException)) SetGenericError(e);

            else if (e.GetType() == typeof(PackageExtractionCanceledException)) CancelAction();
            else if (e.GetType() == typeof(PackageDownloadCanceledException)) CancelAction();

            else SetGenericError(e);

            void CancelAction()
            {
                SetCancelation(false);
            }

            void SetGenericError(Exception ex)
            {
                _ = MainDataModel.BackwardsCommunicationHost.exceptionmsg(ex);
            }

            void SetError(Exception ex2, string debugMessage, string dialogTitle, string dialogText)
            {
                Trace.WriteLine(debugMessage + ":\n" + ex2.ToString());
                MainDataModel.BackwardsCommunicationHost.errormsg(dialogTitle, dialogText, ex2);
            }
        }

        #endregion

        #region IDisposable Implementation

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        protected virtual void Dispose(bool disposing) => CancelSource?.Dispose();

        #endregion







    }
}