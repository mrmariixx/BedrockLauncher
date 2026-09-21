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
        public bool isGameRunning => GameHandle != null;

        #region Public Methods

        public async Task LaunchPackage(MCVersion v, string dirPath, bool KeepLauncherOpen, bool LaunchEditor)
        {
            try
            {
                StartTask();
                MainDataModel.Default.ProgressBarState.SetProgressBarState(LauncherState.isLaunching);

                if (!IsPackageRegistered(v))
                {
                    Trace.WriteLine($"Package not registered for {v.Name} at {v.GameDirectory}, registering in DevelopmentMode...");
                    await UnregisterPackage(v, keepVersion: false);
                    await RegisterPackage(v);
                }

                try
                {
                    string registeredFamily = Constants.GetPackageFamily(v.Type);
                    foreach (var package in PM.FindPackagesForUser(string.Empty))
                    {
                        try
                        {
                            if (string.Equals(
                                Path.GetFullPath(package.InstalledLocation.Path),
                                Path.GetFullPath(v.GameDirectory),
                                StringComparison.OrdinalIgnoreCase))
                            {
                                registeredFamily = package.Id.FamilyName;
                                Trace.WriteLine($"Registered package family: {registeredFamily}");
                                break;
                            }
                        }
                        catch
                        {
                            // Ignore inaccessible packages.
                        }
                    }

                    var pkgList = await AppDiagnosticInfo.RequestInfoForPackageAsync(registeredFamily);
                    if (pkgList != null && pkgList.Count > 0)
                    {
                        Trace.WriteLine($"Launching registered package {registeredFamily} from {v.GameDirectory}");
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

                if (result == System.Windows.Forms.DialogResult.Yes)
                    GameHandle.Kill();
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
                if (Directory.Exists(v.GameDirectory))
                    Directory.Delete(v.GameDirectory, true);

                v.UpdateFolderSize();
                await Task.Run(Program.OnApplicationRefresh);
                foreach (var ver in MainDataModel.Default.Versions)
                    ver.UpdateFolderSize();
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
                if (!File.Exists(packagePath))
                    return;

                StartTask();
                var outputDirectoryName = FileExtensions.GetAvaliableFileName(Path.GetFileNameWithoutExtension(packagePath), MainDataModel.Default.FilePaths.VersionsFolder);
                var outputDirectoryPath = Path.Combine(MainDataModel.Default.FilePaths.VersionsFolder, outputDirectoryName);

                Trace.WriteLine("Extraction started");
                MainDataModel.Default.ProgressBarState.SetProgressBarState(LauncherState.isExtracting);

                if (Directory.Exists(outputDirectoryPath))
                    Directory.Delete(outputDirectoryPath, true);

                var fileStream = File.OpenRead(packagePath);
                var progress = new Progress<ZipProgress>();
                progress.ProgressChanged += (s, z) => MainDataModel.Default.ProgressBarState.SetProgressBarProgress(currentProgress: z.Processed, totalProgress: z.Total);

                await Task.Run(() => new ZipArchive(fileStream).ExtractToDirectory(outputDirectoryPath, progress, CancelSource));
                fileStream.Close();

                File.Delete(Path.Combine(outputDirectoryPath, "AppxSignature.p7x"));

                string backupDirectory = Path.Combine(MainDataModel.Default.FilePaths.VersionsFolder, "AppxBackups");
                Directory.CreateDirectory(backupDirectory);

                string backupPath = Path.Combine(backupDirectory, Path.GetFileName(packagePath));
                if (File.Exists(backupPath))
                    File.Delete(backupPath);

                File.Move(packagePath, backupPath);

                Trace.WriteLine("Extracted successfully");
                await Task.Run(Program.OnApplicationRefresh);
                foreach (var ver in MainDataModel.Default.Versions)
                    ver.UpdateFolderSize();
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
            if (CancelSource != null && !CancelSource.IsCancellationRequested)
                CancelSource.Cancel();
        }

        #endregion

        #region Private Methods

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
            catch (InvalidOperationException)
            {
                throw;
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

                string extension = ".Appx";
                if (v.PackageType == PackageType.GDK)
                {
                    extension = ".package";
                    if (VersionManager.Singleton != null &&
                        VersionManager.Singleton.TryGetGdkDownloadUrls(v.PackageID, out var urls) &&
                        urls.Count > 0)
                    {
                        var lowerUrl = urls[0].ToLowerInvariant();
                        if (lowerUrl.EndsWith(".msixvc")) extension = ".msixvc";
                        else if (lowerUrl.EndsWith(".msix")) extension = ".msix";
                        else if (lowerUrl.EndsWith(".msixbundle")) extension = ".msixbundle";
                    }
                }

                string fileName = "Minecraft-" + v.Name + extension;

                string bkpsPath = Path.Combine(subDirectory, fileName);
                string dlPath = bkpsPath;

                if (!File.Exists(bkpsPath))
                {
                    string altAppx = Path.Combine(subDirectory, "Minecraft-" + v.Name + ".Appx");
                    if (File.Exists(altAppx))
                    {
                        bkpsPath = altAppx;
                        dlPath = altAppx;
                    }
                }

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
                throw;
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
                MainDataModel.Default.ProgressBarState.SetProgressBarState(LauncherState.isDownloading);
                Trace.WriteLine("Download starting -> " + dlPath);

                await VersionDownloader.DownloadVersion(
                    v.DisplayName,
                    v.PackageID,
                    1,
                    dlPath,
                    (x, y) => ProgressWrapper(x, y),
                    cancelSource.Token,
                    v.Type);

                Trace.WriteLine("Download complete");
            }
            catch (PackageManagerException e)
            {
                ResetTask();
                throw;
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

                if (v.PackageType == PackageType.GDK)
                {
                    string packageFile = FindPackageFromMarker(v) ?? FindSignedPackageBackup(v);

                    if (string.IsNullOrWhiteSpace(packageFile) || !File.Exists(packageFile))
                    {
                        throw new FileNotFoundException($"Signed GDK package not found for {v.Name}.");
                    }

                    MainDataModel.Default.ProgressBarState.SetProgressBarText(Path.GetFileName(packageFile));
                    Trace.WriteLine("Registering signed GDK package: " + packageFile);

                    await DeploymentProgressWrapper(
                        PM.AddPackageAsync(
                            new Uri(packageFile),
                            null,
                            Constants.StorePackageDeploymentOptions));

                    return;
                }

                if (!File.Exists(v.ManifestPath) &&
                    File.Exists(Path.Combine(v.GameDirectory, "MicrosoftGame.Config")))
                {
                    EnsureWDAppManifest(v.GameDirectory, v.Type);
                }

                if (File.Exists(v.ManifestPath))
                {
                    if (!PatchMinecraftManifest(v.ManifestPath, v.Type))
                    {
                        throw new IOException(
                            $"Could not patch manifest at {v.ManifestPath} " +
                            "(it may be read-only or locked by another process). " +
                            "Registration aborted to avoid installing with an unpatched manifest.");
                    }

                    MainDataModel.Default.ProgressBarState.SetProgressBarText(v.GetPackageNameFromMainifest());

                    Trace.WriteLine("Registering loose package from manifest (DevelopmentMode): " + v.ManifestPath);

                    await DeploymentProgressWrapper(
                        PM.RegisterPackageAsync(
                            new Uri(v.ManifestPath),
                            null,
                            Constants.PackageDeploymentOptions));
                }
                else
                {
                    throw new FileNotFoundException(
                        $"Cannot register package {v.Name}: manifest not found at {v.ManifestPath}");
                }
            }
            catch (PackageManagerException)
            {
                ResetTask();
                throw;
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

        private static string FindPackageFromMarker(MCVersion v)
        {
            string markerPath = Path.Combine(v.GameDirectory, "cdn_package.txt");
            if (!File.Exists(markerPath))
                return null;

            string packagePath = File.ReadAllText(markerPath).Trim();
            if (string.IsNullOrWhiteSpace(packagePath))
                return null;

            return File.Exists(packagePath) ? packagePath : null;
        }

        private static string FindSignedPackageBackup(MCVersion v)
        {
            string backupDirectory = Path.Combine(
                MainDataModel.Default.FilePaths.VersionsFolder,
                "AppxBackups");

            string filePrefix = "Minecraft-" + v.Name;

            string[] candidates =
            {
                Path.Combine(backupDirectory, filePrefix + ".msixvc"),
                Path.Combine(backupDirectory, filePrefix + ".msixbundle"),
                Path.Combine(backupDirectory, filePrefix + ".msix"),
                Path.Combine(backupDirectory, filePrefix + ".package"),
                Path.Combine(backupDirectory, filePrefix + ".Appx"),
                Path.Combine(backupDirectory, filePrefix + ".appx")
            };

            return candidates.FirstOrDefault(File.Exists);
        }

        private static bool PatchMinecraftManifest(string path, VersionType versionType)
        {
            if (!File.Exists(path))
                return false;

            try
            {
                var attributes = File.GetAttributes(path);
                if (attributes.HasFlag(FileAttributes.ReadOnly))
                {
                    File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
                    Trace.WriteLine("[PatchMinecraftManifest] Cleared read-only attribute on manifest: " + path);
                }

                XDocument doc = XDocument.Load(path);
                XNamespace ns = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
                XNamespace uap = "http://schemas.microsoft.com/appx/manifest/uap/windows10";
                XNamespace rescap = "http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities";

                var identity = doc.Descendants(ns + "Identity").FirstOrDefault();
                if (identity != null)
                {
                    string targetName = versionType == VersionType.Preview
                        ? "Microsoft.MinecraftWindowsBeta"
                        : "Microsoft.MinecraftUWP";

                    identity.SetAttributeValue("Name", targetName);
                    identity.SetAttributeValue("Publisher", "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US");
                }

                var apps = doc.Descendants(ns + "Application");
                foreach (var app in apps)
                {
                    var executable = app.Attribute("Executable");
                    if (executable != null &&
                        (executable.Value == "GameLaunchHelper.exe" ||
                         executable.Value == "gamelaunchhelper.exe" ||
                         executable.Value == "GDKLaunchShim.exe"))
                    {
                        executable.Value = "Minecraft.Windows.exe";
                    }

                    app.SetAttributeValue("EntryPoint", "Windows.FullTrustApplication");
                }

                var extensions = doc
                    .Descendants()
                    .Where(element =>
                        element.Name.LocalName == "Extensions" ||
                        element.Name.LocalName == "Extension")
                    .ToList();

                foreach (var extension in extensions)
                {
                    extension.Remove();
                }

                var capabilities = doc.Descendants(ns + "Capabilities").FirstOrDefault();
                if (capabilities == null)
                {
                    capabilities = new XElement(ns + "Capabilities");
                    doc.Root?.Add(capabilities);
                }

                bool hasRunFullTrust = capabilities
                    .Elements(rescap + "Capability")
                    .Any(c => c.Attribute("Name")?.Value == "runFullTrust");

                if (!hasRunFullTrust)
                {
                    capabilities.Add(new XElement(rescap + "Capability",
                        new XAttribute("Name", "runFullTrust")));
                }

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
                    Encoding = new System.Text.UTF8Encoding(false),
                    Indent = true
                };

                using (var w = System.Xml.XmlWriter.Create(path, settings))
                {
                    doc.Save(w);
                }

                Trace.WriteLine("[PatchMinecraftManifest] Patched manifest to prevent auto-update and msgamelaunch issues: " + path);
                return true;
            }
            catch (Exception ex)
            {
                Trace.WriteLine("[PatchMinecraftManifest] ERROR: could not patch manifest at " + path + ": " + ex);
                return false;
            }
        }

        private static bool EnsureWDAppManifest(string gameDirectory, VersionType versionType)
        {
            try
            {
                string configPath = Path.Combine(gameDirectory, "MicrosoftGame.Config");
                string manifestPath = Path.Combine(gameDirectory, "AppxManifest.xml");

                if (!File.Exists(configPath))
                    return false;

                XDocument configDoc = XDocument.Load(configPath);
                var root = configDoc.Root;
                if (root == null)
                    return false;

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

                    bool looksLikeZip = read == 4 &&
                        header[0] == (byte)'P' &&
                        header[1] == (byte)'K';

                    if (looksLikeZip)
                    {
                        var progress = new Progress<ZipProgress>();
                        progress.ProgressChanged += (s, z) =>
                            MainDataModel.Default.ProgressBarState.SetProgressBarProgress(
                                currentProgress: z.Processed,
                                totalProgress: z.Total);

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
                    if (v.PackageType == PackageType.UWP && File.Exists(signaturePath))
                        File.Delete(signaturePath);
                }
                else
                {
                    Trace.WriteLine("Package is not a zip archive; will install from signed package file.");
                    string absolutePkg = Path.GetFullPath(pkgPath);
                    await File.WriteAllTextAsync(Path.Combine(v.GameDirectory, "cdn_package.txt"), absolutePkg);

                    string versionsBackups = Path.Combine(Path.GetFullPath(MainDataModel.Default.FilePaths.VersionsFolder), "AppxBackups");
                    Directory.CreateDirectory(versionsBackups);

                    string desiredBackup = Path.Combine(versionsBackups, Path.GetFileName(absolutePkg));
                    if (!Path.GetFullPath(absolutePkg).Equals(Path.GetFullPath(desiredBackup), StringComparison.OrdinalIgnoreCase))
                    {
                        if (File.Exists(desiredBackup))
                            File.Delete(desiredBackup);

                        File.Copy(absolutePkg, desiredBackup, true);
                        await File.WriteAllTextAsync(Path.Combine(v.GameDirectory, "cdn_package.txt"), desiredBackup);
                    }
                }

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
                throw;
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

        private async Task UnregisterPackage(
            MCVersion v,
            bool keepVersion = false,
            bool mustMatchVersion = false)
        {
            try
            {
                string[] minecraftFamilies =
                {
                    Constants.GetPackageFamily(VersionType.Release),
                    Constants.GetPackageFamily(VersionType.Preview)
                };

                foreach (string family in minecraftFamilies)
                {
                    foreach (var package in PM.FindPackagesForUser(string.Empty, family))
                    {
                        string location = string.Empty;

                        try
                        {
                            location = package.InstalledLocation?.Path ?? string.Empty;
                        }
                        catch
                        {
                            // ignore inaccessible installed location
                        }

                        bool sameLocation =
                            !string.IsNullOrWhiteSpace(location) &&
                            string.Equals(
                                Path.GetFullPath(location),
                                Path.GetFullPath(v.GameDirectory),
                                StringComparison.OrdinalIgnoreCase);

                        if (keepVersion && sameLocation)
                            continue;

                        if (mustMatchVersion && !sameLocation)
                            continue;

                        Trace.WriteLine("Removing Minecraft package: " + package.Id.FullName);

                        MainDataModel.Default.ProgressBarState.SetProgressBarText(package.Id.FullName);
                        MainDataModel.Default.ProgressBarState.SetProgressBarState(LauncherState.isRemovingPackage);

                        await DeploymentProgressWrapper(
                            PM.RemovePackageAsync(
                                package.Id.FullName,
                                Constants.PackageRemovalOptions));
                    }
                }
            }
            catch (PackageManagerException)
            {
                ResetTask();
                throw;
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
                string PackagesRoot = Path.Combine(localAppData, "Packages");

                string LocalStateFolder = Path.Combine(localAppData, "Packages", Constants.GetPackageFamily(type), "LocalState");
                string PackageFolder = Path.Combine(localAppData, "Packages", Constants.GetPackageFamily(type), "LocalState");
                string PackageBakFolder = Path.Combine(localAppData, "Packages", Constants.GetPackageFamily(type), "LocalState_Backup");
                string ProfileFolder = Path.GetFullPath(InstallationsFolderPath);

                string RequiredDir = Directory.GetParent(PackageFolder).FullName;
                if (Directory.Exists(PackageFolder))
                    Directory.Delete(PackageFolder, true);

                if (!Directory.Exists(RequiredDir))
                    Directory.CreateDirectory(RequiredDir);

                DirectoryInfo profileDir = Directory.CreateDirectory(ProfileFolder);

                bool symlinkCreated = SymLinkHelper.CreateSymbolicLinkSafe(PackageFolder, ProfileFolder);
                if (!symlinkCreated)
                    throw new SaveRedirectionFailedException(new Exception("Failed to create symbolic link for save redirection."));

                DirectoryInfo pkgDir = Directory.CreateDirectory(PackageFolder);
                DirectoryInfo lsDir = Directory.CreateDirectory(LocalStateFolder);

                SecurityIdentifier owner = WindowsIdentity.GetCurrent().User;
                SecurityIdentifier authenticated_users_identity = new SecurityIdentifier("S-1-5-1");

                FileSystemAccessRule owner_access_rules = new FileSystemAccessRule(owner, FileSystemRights.FullControl, InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow);
                FileSystemAccessRule au_access_rules = new FileSystemAccessRule(authenticated_users_identity, FileSystemRights.Modify, InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow);

                var lsSecurity = lsDir.GetAccessControl();
                AuthorizationRuleCollection rules = lsSecurity.GetAccessRules(true, true, typeof(SecurityIdentifier));
                List<FileSystemAccessRule> needed_rules = new List<FileSystemAccessRule>();

                foreach (AccessRule rule in rules)
                {
                    if (rule.IdentityReference is SecurityIdentifier)
                    {
                        var required_rule = new FileSystemAccessRule(
                            rule.IdentityReference,
                            rule.FileSystemRights,
                            InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit,
                            PropagationFlags.None,
                            AccessControlType.Allow);

                        needed_rules.Add(required_rule);
                    }
                }

                var pkgSecurity = pkgDir.GetAccessControl();
                pkgSecurity.SetOwner(owner);
                pkgSecurity.AddAccessRule(au_access_rules);
                pkgSecurity.AddAccessRule(owner_access_rules);
                pkgDir.SetAccessControl(pkgSecurity);

                var profileSecurity = profileDir.GetAccessControl();
                profileSecurity.AddAccessRule(au_access_rules);
                profileSecurity.AddAccessRule(owner_access_rules);
                needed_rules.ForEach(x => profileSecurity.AddAccessRule(x));
                profileDir.SetAccessControl(profileSecurity);
            }
            catch (PackageManagerException)
            {
                throw;
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
                var packages = PM.FindPackagesForUser(string.Empty);
                foreach (var pkg in packages)
                {
                    string location = string.Empty;
                    try
                    {
                        location = pkg.InstalledLocation?.Path ?? string.Empty;
                    }
                    catch
                    {
                        // ignore
                    }

                    if (!string.IsNullOrEmpty(location) &&
                        string.Equals(Path.GetFullPath(location), Path.GetFullPath(v.GameDirectory), StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }

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
                    catch
                    {
                        // ignore
                    }
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
                var packages = PM.FindPackagesForUser(string.Empty);

                foreach (var pkg in packages)
                {
                    try
                    {
                        string loc = pkg.InstalledLocation?.Path;
                        if (!string.IsNullOrEmpty(loc) &&
                            Directory.Exists(loc) &&
                            File.Exists(Path.Combine(loc, "Minecraft.Windows.exe")))
                        {
                            sourcePath = loc;
                            break;
                        }
                    }
                    catch
                    {
                        // ignore
                    }
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
                        catch
                        {
                            // ignore
                        }
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

            t.Progress += (v, p) =>
                MainDataModel.Default.ProgressBarState.SetProgressBarProgress(
                    currentProgress: Convert.ToInt64(p.percentage),
                    totalProgress: 100);

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
            if (cancelState)
                CancelSource = new CancellationTokenSource();

            MainDataModel.Default.ProgressBarState.AllowCancel = cancelState ? true : false;
            MainDataModel.Default.ProgressBarState.CancelCommand = cancelState ? new RelayCommand((o) => Cancel()) : null;
        }

        protected void SetException(Exception e)
        {
            if (e.GetType() == typeof(PackageExtractionFailedException))
                SetError(e, "Extraction failed", "Error_AppExtractionFailed_Title", "Error_AppExtractionFailed");
            else if (e.GetType() == typeof(PackageDownloadFailedException))
                SetError(e, "Download failed", "Error_AppDownloadFailed_Title", "Error_AppDownloadFailed");
            else if (e.GetType() == typeof(BetaAuthenticationFailedException))
                SetError(e, "Authentication failed", "Error_AuthenticationFailed_Title", "Error_AuthenticationFailed");
            else if (e.GetType() == typeof(AppLaunchFailedException))
                SetError(e, "App launch failed", "Error_AppLaunchFailed_Title", "Error_AppLaunchFailed");
            else if (e.GetType() == typeof(PackageRegistrationFailedException))
                SetError(e, "App registeration failed", "Error_AppReregisterFailed_Title", "Error_AppReregisterFailed");
            else if (e.GetType() == typeof(PackageRemovalFailedException))
                SetError(e, "App uninstall failed", "Error_AppUninstallFailed_Title", "Error_AppUninstallFailed");
            else if (e.GetType() == typeof(SaveRedirectionFailedException))
                SetError(e, "Save redirection failed", "Error_SaveDirectoryRedirectionFailed_Title", "Error_SaveDirectoryRedirectionFailed");
            else if (e.GetType() == typeof(PackageDeregistrationFailedException))
                SetError(e, "App deregisteration failed", "Error_AppDeregisteringFailed_Title", "Error_AppDeregisteringFailed");
            else if (e.GetType() == typeof(PackageDownloadAndExtractFailedException))
                SetGenericError(e);
            else if (e.GetType() == typeof(PackageProcessHookFailedException))
                SetGenericError(e);
            else if (e.GetType() == typeof(PackageExtractionCanceledException))
                CancelAction();
            else if (e.GetType() == typeof(PackageDownloadCanceledException))
                CancelAction();
            else
                SetGenericError(e);

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

        #region IDisposable

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing) => CancelSource?.Dispose();

        #endregion
    }
}