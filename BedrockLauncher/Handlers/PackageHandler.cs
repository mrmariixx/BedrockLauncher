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
using StorageFile = Windows.Storage.StorageFile;
using StorageFolder = Windows.Storage.StorageFolder;
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

        public async Task LaunchPackage(
            MCVersion v,
            string dirPath,
            bool KeepLauncherOpen,
            bool LaunchEditor)
        {
            try
            {
                StartTask();
                MainDataModel.Default.ProgressBarState
                    .SetProgressBarState(LauncherState.isLaunching);

                if (LaunchEditor)
                {
                    throw new NotSupportedException(
                        "The editor is unavailable for this installation.");
                }

                if (v.PackageType == PackageType.GDK)
                {
                    if (!IsGdkPackageRegistered(v))
                    {
                        throw new FileNotFoundException(
                            $"The GDK package {v.Name} is not registered with Windows. Install it first from the launcher.");
                    }

                    if (!await TryLaunchRegisteredGdkPackageAsync(
                            v,
                            KeepLauncherOpen))
                    {
                        throw new AppLaunchFailedException(
                            $"Windows could not launch registered GDK version {v.Name}.",
                            new InvalidOperationException(
                                "The registered package did not expose a launchable application entry."));
                    }
                }
                else if (!IsLocalPackageReady(v))
                {
                    throw new FileNotFoundException(
                        $"The local version {v.Name} is incomplete. Expected Minecraft.Windows.exe and an AppxManifest.xml or MicrosoftGame.Config in {v.GameDirectory}.");
                }
                else if (!await TryLaunchExecutableAsync(
                             v,
                             KeepLauncherOpen))
                {
                    throw new AppLaunchFailedException(
                        "Minecraft could not be started from its local version folder.",
                        new FileNotFoundException(v.ExecutablePath));
                }
            }
            catch (Exception e)
            {
                EndTask();
                SetException(new AppLaunchFailedException(e));
            }
        }

        public async Task<bool> InstallPackage(
            MCVersion v,
            string dirPath)
        {
            try
            {
                StartTask();

                bool versionIsInstalled =
                    v.PackageType == PackageType.GDK
                        ? IsGdkPackageRegistered(v)
                        : v.HasPlayableFiles;

                if (!versionIsInstalled)
                {
                    List<VersionInfoJson> versions =
                        VersionManager.Singleton.GetVersions();

                    bool known = versions.Any(ver =>
                        string.Equals(
                            v.UUID,
                            ver.uuid.ToString(),
                            StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(
                            v.Name,
                            ver.version,
                            StringComparison.OrdinalIgnoreCase));

                    if (!known)
                        throw new NoVersionAccessibleException();

                    await DownloadAndExtractPackage(v);
                }

                if (v.PackageType == PackageType.GDK)
                {
                    if (!IsGdkPackageRegistered(v))
                    {
                        throw new InvalidDataException(
                            $"Windows did not register GDK version {v.Name} after deployment.");
                    }

                    await SaveGdkRegistrationMarkerAsync(v);
                }
                else if (!IsLocalPackageReady(v))
                {
                    throw new InvalidDataException(
                        $"This UWP package could not be extracted into a runnable local version folder. Expected Minecraft.Windows.exe and an AppxManifest.xml or MicrosoftGame.Config in {v.GameDirectory}.");
                }

                await RedirectSaveData(dirPath, v.Type);
                return true;
            }
            catch (PackageManagerException e)
            {
                SetException(e);
                return false;
            }
            catch (NoVersionAccessibleException e)
            {
                SetException(e);
                return false;
            }
            catch (Exception e)
            {
                SetException(new AppInstallFailedException(e));
                return false;
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
                string title =
                    BedrockLauncher.Localization.Language.LanguageManager
                        .GetResource("Dialog_KillGame_Title") as string;

                string content =
                    BedrockLauncher.Localization.Language.LanguageManager
                        .GetResource("Dialog_KillGame_Text") as string;

                var result =
                    await DialogPrompt.ShowDialog_YesNo(title, content);

                if (result == System.Windows.Forms.DialogResult.Yes)
                    GameHandle.Kill();
            }
        }

        public async Task RemovePackage(MCVersion v)
        {
            try
            {
                StartTask();

                MainDataModel.Default.ProgressBarState
                    .SetProgressBarState(LauncherState.isUninstalling);

                await UnregisterPackage(v, false, true);

                await DirectoryExtensions.DeleteAsync(
                    v.GameDirectory,
                    (x, y, phase) =>
                        ProgressWrapper(x, y, phase),
                    "Files",
                    "Folders");

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

                var outputDirectoryName =
                    FileExtensions.GetAvaliableFileName(
                        Path.GetFileNameWithoutExtension(packagePath),
                        MainDataModel.Default.FilePaths.VersionsFolder);

                var outputDirectoryPath =
                    Path.Combine(
                        MainDataModel.Default.FilePaths.VersionsFolder,
                        outputDirectoryName);

                MainDataModel.Default.ProgressBarState
                    .SetProgressBarState(LauncherState.isExtracting);

                if (Directory.Exists(outputDirectoryPath))
                    Directory.Delete(outputDirectoryPath, true);

                using var fileStream =
                    File.OpenRead(packagePath);

                var progress = new Progress<ZipProgress>();

                progress.ProgressChanged += (s, z) =>
                    MainDataModel.Default.ProgressBarState
                        .SetProgressBarProgress(
                            currentProgress: z.Processed,
                            totalProgress: z.Total);

                await Task.Run(() =>
                {
                    using var archive = new ZipArchive(fileStream);

                    archive.ExtractToDirectory(
                        outputDirectoryPath,
                        progress,
                        CancelSource);
                });

                string signature =
                    Path.Combine(
                        outputDirectoryPath,
                        "AppxSignature.p7x");

                if (File.Exists(signature))
                    File.Delete(signature);

                string backupDirectory =
                    Path.Combine(
                        MainDataModel.Default.FilePaths.VersionsFolder,
                        "AppxBackups");

                Directory.CreateDirectory(backupDirectory);

                string backupPath =
                    Path.Combine(
                        backupDirectory,
                        Path.GetFileName(packagePath));

                if (File.Exists(backupPath))
                    File.Delete(backupPath);

                File.Move(packagePath, backupPath);

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
                SetException(
                    new PackageDownloadAndExtractFailedException(e));
            }
            finally
            {
                EndTask();
            }
        }

        public void Cancel()
        {
            if (CancelSource != null &&
                !CancelSource.IsCancellationRequested)
            {
                CancelSource.Cancel();
            }
        }

        #endregion

        #region Private Throwable Methods

        private async Task GetGameHandle(string processName)
        {
            await Task.Run(async () =>
            {
                try
                {
                    Process attached = null;

                    for (
                        int attempt = 0;
                        attempt < 60 && attached == null;
                        attempt++)
                    {
                        var processes =
                            Process.GetProcessesByName(processName);

                        if (processes.Length >= 1)
                        {
                            attached = processes[0];
                            break;
                        }

                        await Task.Delay(500);
                    }

                    if (attached != null)
                    {
                        MainDataModel.Default.ProgressBarState
                            .SetGameRunningStatus(true);

                        GameHandle = attached;
                        GameHandle.EnableRaisingEvents = true;
                        GameHandle.Exited += OnPackageExit;

                        void OnPackageExit(
                            object sender,
                            EventArgs e)
                        {
                            Process p = sender as Process;

                            if (p != null)
                                p.Exited -= OnPackageExit;

                            GameHandle = null;

                            MainDataModel.Default.ProgressBarState
                                .SetGameRunningStatus(false);
                        }

                        Trace.WriteLine(
                            "Successfully attached Minecraft process");
                    }
                    else
                    {
                        Trace.WriteLine(
                            "Failed to attach Minecraft process: timed out waiting for process");

                        GameHandle = null;

                        MainDataModel.Default.ProgressBarState
                            .SetGameRunningStatus(false);
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
        }

        private async Task DownloadAndExtractPackage(MCVersion v)
        {
            try
            {
                string versionsRoot =
                    Path.GetFullPath(
                        MainDataModel.Default.FilePaths.VersionsFolder);

                string gameDir =
                    Path.GetFullPath(v.GameDirectory);

                Trace.WriteLine(
                    $"Download start: {v.PackageID} ({v.PackageType})");

                Trace.WriteLine(
                    $"Versions root: {versionsRoot}");

                Trace.WriteLine(
                    $"Game directory: {gameDir}");

                SetCancelation(true);

                Directory.CreateDirectory(versionsRoot);

                string subDirectory =
                    Path.Combine(
                        versionsRoot,
                        "AppxBackups");

                Directory.CreateDirectory(subDirectory);

                string extension = ".Appx";

                if (v.PackageType == PackageType.GDK)
                {
                    extension = ".package";

                    if (VersionManager.Singleton != null &&
                        VersionManager.Singleton.TryGetGdkDownloadUrls(
                            v.PackageID,
                            out var urls) &&
                        urls.Count > 0)
                    {
                        string lowerUrl =
                            urls[0].ToLowerInvariant();

                        if (lowerUrl.EndsWith(".msixvc"))
                            extension = ".msixvc";
                        else if (lowerUrl.EndsWith(".msix"))
                            extension = ".msix";
                        else if (lowerUrl.EndsWith(".msixbundle"))
                            extension = ".msixbundle";
                    }
                }

                string fileName =
                    "Minecraft-" + v.Name + extension;

                string bkpsPath =
                    Path.Combine(
                        subDirectory,
                        fileName);

                string dlPath = bkpsPath;

                if (!File.Exists(bkpsPath))
                {
                    string altAppx =
                        Path.Combine(
                            subDirectory,
                            "Minecraft-" + v.Name + ".Appx");

                    if (File.Exists(altAppx))
                    {
                        bkpsPath = altAppx;
                        dlPath = altAppx;
                    }
                }

                string cwdLegacy =
                    Path.Combine(
                        Directory.GetCurrentDirectory(),
                        fileName);

                if (!File.Exists(bkpsPath) &&
                    File.Exists(cwdLegacy))
                {
                    File.Move(
                        cwdLegacy,
                        bkpsPath);
                }

                string pkgPath =
                    File.Exists(bkpsPath)
                        ? bkpsPath
                        : dlPath;

                if (!File.Exists(pkgPath))
                    await DownloadPackage(
                        v,
                        dlPath,
                        CancelSource);

                await ExtractPackage(
                    v,
                    dlPath,
                    bkpsPath,
                    pkgPath,
                    CancelSource);

                v.UpdateFolderSize();

                Trace.WriteLine(
                    $"Package ready at: {gameDir}");
            }
            catch (PackageManagerException)
            {
                ResetTask();
                throw;
            }
            catch (Exception ex)
            {
                ResetTask();
                throw new Exception(
                    "DownloadAndExtractPackage Failed",
                    ex);
            }
            finally
            {
                ResetTask();
                SetCancelation(false);
                CancelSource = null;
            }
        }

        private async Task DownloadPackage(
            MCVersion v,
            string dlPath,
            CancellationTokenSource cancelSource)
        {
            try
            {
                MainDataModel.Default.ProgressBarState
                    .SetProgressBarState(
                        LauncherState.isDownloading);

                Trace.WriteLine(
                    "Download starting -> " + dlPath);

                if (v.Type == VersionType.Beta &&
                    v.PackageType != PackageType.GDK)
                {
                    await AuthenticateBetaUser();
                }

                await VersionDownloader.DownloadVersion(
                    v.DisplayName,
                    v.PackageID,
                    1,
                    dlPath,
                    (x, y) => ProgressWrapper(x, y),
                    cancelSource.Token,
                    v.Type);

                Trace.WriteLine(
                    "Download complete");
            }
            catch (PackageManagerException)
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

        private async Task AuthenticateBetaUser()
        {
            try
            {
                var userIndex =
                    Properties.LauncherSettings.Default
                        .CurrentInsiderAccountIndex;
                var token =
                    await Task.Run(
                        () => AuthenticationManager.Default
                            .GetWUToken(userIndex));
                VersionDownloader.SetMSAUserToken(token);
            }
            catch (PackageManagerException)
            {
                throw;
            }
            catch (Exception e)
            {
                Trace.WriteLine(
                    "Error while Authenticating UserToken for Version Fetching:\n" +
                    e);
                throw new BetaAuthenticationFailedException(e);
            }
        }

        private async Task RegisterPackage(MCVersion v)
        {
            try
            {
                Trace.WriteLine(
                    $"Registering package ({v.PackageType}): {v.Name}");

                MainDataModel.Default.ProgressBarState
                    .SetProgressBarState(
                        LauncherState.isRegisteringPackage);

                if (v.PackageType == PackageType.GDK)
                {
                    if (File.Exists(v.ManifestPath) &&
                        File.Exists(v.ExecutablePath))
                    {
                        if (!FixGDKManifest(v.ManifestPath, v.Type))
                        {
                            throw new IOException(
                                $"Could not patch GDK manifest at {v.ManifestPath}.");
                        }

                        MainDataModel.Default.ProgressBarState
                            .SetProgressBarText(
                                v.GetPackageNameFromMainifest());

                        Trace.WriteLine(
                            "Registering loose GDK package from versions folder: " +
                            v.ManifestPath);

                        await DeploymentProgressWrapper(
                            PM.RegisterPackageAsync(
                                new Uri(v.ManifestPath),
                                null,
                                Constants.PackageDeploymentOptions));

                        return;
                    }

                    string packageFile =
                        FindPackageFromMarker(v)
                        ?? FindSignedPackageBackup(v);

                    if (string.IsNullOrWhiteSpace(packageFile) ||
                        !File.Exists(packageFile))
                    {
                        throw new FileNotFoundException(
                            $"Signed GDK package not found for {v.Name}.");
                    }

                    MainDataModel.Default.ProgressBarState
                        .SetProgressBarText(
                            Path.GetFileName(packageFile));

                    Trace.WriteLine(
                        "Registering signed GDK package: " +
                        packageFile);

                    await DeploymentProgressWrapper(
                        PM.AddPackageAsync(
                            new Uri(packageFile),
                            null,
                            Constants.StorePackageDeploymentOptions));

                    return;
                }

                if (!File.Exists(v.ManifestPath) &&
                    File.Exists(
                        Path.Combine(
                            v.GameDirectory,
                            "MicrosoftGame.Config")))
                {
                    EnsureWDAppManifest(
                        v.GameDirectory,
                        v.Type);
                }

                if (!File.Exists(v.ManifestPath))
                {
                    throw new FileNotFoundException(
                        $"Cannot register package {v.Name}: manifest not found at {v.ManifestPath}");
                }

                if (!FixGDKManifest(
                    v.ManifestPath,
                    v.Type))
                {
                    throw new IOException(
                        $"Could not patch manifest at {v.ManifestPath}.");
                }

                MainDataModel.Default.ProgressBarState
                    .SetProgressBarText(
                        v.GetPackageNameFromMainifest());

                Trace.WriteLine(
                    "Registering loose package: " +
                    v.ManifestPath);

                await DeploymentProgressWrapper(
                    PM.RegisterPackageAsync(
                        new Uri(v.ManifestPath),
                        null,
                        Constants.PackageDeploymentOptions));
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

        private async Task PrepareGdkForLaunchAsync(MCVersion v)
        {
            await RegisterGdkLoosePackageAsync(v);
        }

        private async Task RegisterGdkLoosePackageAsync(MCVersion v)
        {
            try
            {
                if (!File.Exists(v.ExecutablePath))
                {
                    Trace.WriteLine(
                        "GDK loose registration skipped — Minecraft.Windows.exe missing at " +
                        v.GameDirectory);
                    return;
                }

                // Only loose-register a version folder that already has GDK/UWP
                // content (zip extract). An exe-only stub cannot be launched
                // as a package — Play will fall back to XboxGames Content.
                bool hasLocalContent =
                    File.Exists(
                        Path.Combine(
                            v.GameDirectory,
                            "MicrosoftGame.Config")) ||
                    (File.Exists(v.ManifestPath) &&
                     Directory.EnumerateFileSystemEntries(v.GameDirectory)
                         .Take(5)
                         .Count() > 2);

                if (!hasLocalContent)
                {
                    Trace.WriteLine(
                        "GDK loose registration skipped — version folder has no extracted game content.");
                    return;
                }

                if (!File.Exists(v.ManifestPath) &&
                    File.Exists(
                        Path.Combine(
                            v.GameDirectory,
                            "MicrosoftGame.Config")))
                {
                    EnsureWDAppManifest(
                        v.GameDirectory,
                        v.Type);
                }

                if (!File.Exists(v.ManifestPath))
                {
                    Trace.WriteLine(
                        "GDK loose registration skipped — AppxManifest.xml missing.");
                    return;
                }

                if (!FixGDKManifest(v.ManifestPath, v.Type))
                {
                    Trace.WriteLine(
                        "GDK loose registration skipped — manifest patch failed.");
                    return;
                }

                if (IsRegisteredAtGameDirectory(v))
                {
                    Trace.WriteLine(
                        $"GDK already registered at {v.GameDirectory}");
                    return;
                }

                // Drop the signed XboxGames registration so Play does not
                // activate GameLaunchHelper / the Microsoft updater.
                await UnregisterPackage(v, keepVersion: true);

                Trace.WriteLine(
                    "Registering loose GDK package (DevelopmentMode): " +
                    v.ManifestPath);

                await DeploymentProgressWrapper(
                    PM.RegisterPackageAsync(
                        new Uri(v.ManifestPath),
                        null,
                        Constants.PackageDeploymentOptions));
            }
            catch (Exception ex)
            {
                Trace.WriteLine(
                    "RegisterGdkLoosePackageAsync error: " + ex);
            }
        }

        private async Task<bool> TryLaunchViaAppDiagnosticInfoAsync(
            MCVersion v,
            bool KeepLauncherOpen)
        {
            try
            {
                string registeredFamily =
                    Constants.GetPackageFamily(v.Type);

                foreach (var package in PM.FindPackagesForUser(string.Empty))
                {
                    try
                    {
                        string location =
                            package.InstalledLocation?.Path;

                        if (!string.IsNullOrWhiteSpace(location) &&
                            string.Equals(
                                Path.GetFullPath(location),
                                Path.GetFullPath(v.GameDirectory),
                                StringComparison.OrdinalIgnoreCase))
                        {
                            registeredFamily = package.Id.FamilyName;

                            Trace.WriteLine(
                                $"Registered package family: {registeredFamily}");

                            break;
                        }
                    }
                    catch
                    {
                    }
                }

                var pkgList =
                    await AppDiagnosticInfo.RequestInfoForPackageAsync(
                        registeredFamily);

                if (pkgList == null || pkgList.Count == 0)
                    return false;

                Trace.WriteLine(
                    $"Launching registered package {registeredFamily} from {v.GameDirectory}");

                var activationResult =
                    await pkgList[0].LaunchAsync();

                if (activationResult.ExtendedError != null)
                {
                    Trace.WriteLine(
                        "LaunchAsync warning: " +
                        activationResult.ExtendedError.Message);

                    return false;
                }

                Trace.WriteLine(
                    "App launch finished via AppDiagnosticInfo!");

                await FinishLaunchAsync(KeepLauncherOpen);
                return true;
            }
            catch (Exception ex)
            {
                Trace.WriteLine(
                    "AppDiagnosticInfo launch error: " + ex);

                return false;
            }
        }

        private async Task<bool> TryLaunchExecutableAsync(
            MCVersion v,
            bool KeepLauncherOpen)
        {
            if (!IsLocalPackageReady(v))
            {
                return false;
            }

            Trace.WriteLine(
                "Launching Minecraft.Windows.exe directly (bypass updater): " +
                v.ExecutablePath);

            var psi = new ProcessStartInfo(v.ExecutablePath)
            {
                WorkingDirectory = v.GameDirectory,
                UseShellExecute = true
            };

            Process.Start(psi);
            await FinishLaunchAsync(KeepLauncherOpen);
            return true;
        }

        private static bool IsLocalPackageReady(MCVersion version)
        {
            return File.Exists(version.ExecutablePath) &&
                   (File.Exists(version.ManifestPath) ||
                    File.Exists(Path.Combine(
                        version.GameDirectory,
                        "MicrosoftGame.Config")));
        }

        private async Task FinishLaunchAsync(bool KeepLauncherOpen)
        {
            if (!KeepLauncherOpen)
            {
                await Application.Current.Dispatcher.InvokeAsync(
                    () => Application.Current.MainWindow.Close());
            }
            else
            {
                await GetGameHandle(
                    Constants.MINECRAFT_PROCESS_NAME);
            }
        }

        private static string FindPackageFromMarker(MCVersion v)
        {
            string markerPath =
                Path.Combine(
                    v.GameDirectory,
                    "cdn_package.txt");

            if (!File.Exists(markerPath))
                return null;

            string packagePath =
                File.ReadAllText(markerPath).Trim();

            if (string.IsNullOrWhiteSpace(packagePath))
                return null;

            return File.Exists(packagePath)
                ? packagePath
                : null;
        }

        private static string FindSignedPackageBackup(MCVersion v)
        {
            string backupDirectory =
                Path.Combine(
                    MainDataModel.Default.FilePaths.VersionsFolder,
                    "AppxBackups");

            string filePrefix =
                "Minecraft-" + v.Name;

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

        private static bool FixGDKManifest(
            string path,
            VersionType versionType)
        {
            if (!File.Exists(path))
                return false;

            try
            {
                var attributes =
                    File.GetAttributes(path);

                if (attributes.HasFlag(
                    FileAttributes.ReadOnly))
                {
                    File.SetAttributes(
                        path,
                        attributes & ~FileAttributes.ReadOnly);
                }

                XDocument doc =
                    XDocument.Load(path);

                string targetName =
                    versionType == VersionType.Preview
                        ? "Microsoft.MinecraftWindowsBeta"
                        : "Microsoft.MinecraftUWP";

                MinecraftManifestPatcher.Apply(
                    doc,
                    targetName,
                    "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US");

                var settings =
                    new System.Xml.XmlWriterSettings
                    {
                        Encoding =
                            new System.Text.UTF8Encoding(false),
                        Indent = true
                    };

                using var writer =
                    System.Xml.XmlWriter.Create(
                        path,
                        settings);

                doc.Save(writer);

                Trace.WriteLine(
                    "[PatchMinecraftManifest] Patched manifest: EntryPoint=Minecraft_Win10.App, launch shim redirected: " +
                    path);

                return true;
            }
            catch (Exception ex)
            {
                Trace.WriteLine(
                    "[FixGDKManifest] ERROR: " + ex);

                return false;
            }
        }

        private static bool EnsureWDAppManifest(
            string gameDirectory,
            VersionType versionType)
        {
            try
            {
                string configPath =
                    Path.Combine(
                        gameDirectory,
                        "MicrosoftGame.Config");

                string manifestPath =
                    Path.Combine(
                        gameDirectory,
                        "AppxManifest.xml");

                if (!File.Exists(configPath))
                    return false;

                XDocument configDoc =
                    XDocument.Load(configPath);

                var root =
                    configDoc.Root;

                if (root == null)
                    return false;

                var identityElem =
                    root.Element("Identity");

                string name =
                    versionType == VersionType.Preview
                        ? "Microsoft.MinecraftWindowsBeta"
                        : "Microsoft.MinecraftUWP";

                string publisher =
                    "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US";

                string version =
                    identityElem?
                        .Attribute("Version")?
                        .Value ??
                    "1.0.0.0";

                string arch =
                    identityElem?
                        .Attribute("ProcessorArchitecture")?
                        .Value ??
                    "x64";

                var visualsElem =
                    root.Element("ShellVisuals");

                string displayName =
                    visualsElem?
                        .Attribute("DefaultDisplayName")?
                        .Value ??
                    "Minecraft";

                string publisherDisplayName =
                    visualsElem?
                        .Attribute("PublisherDisplayName")?
                        .Value ??
                    "Mojang";

                string storeLogo =
                    visualsElem?
                        .Attribute("StoreLogo")?
                        .Value ??
                    "StoreLogo.png";

                string square150 =
                    visualsElem?
                        .Attribute("Square150x150Logo")?
                        .Value ??
                    "Logo.png";

                string square44 =
                    visualsElem?
                        .Attribute("Square44x44Logo")?
                        .Value ??
                    "SmallLogo.png";

                string description =
                    visualsElem?
                        .Attribute("Description")?
                        .Value ??
                    "Minecraft";

                string splashImage =
                    visualsElem?
                        .Attribute("SplashScreenImage")?
                        .Value ??
                    "SplashScreen.png";

                string manifestContent =
                    $@"<?xml version=""1.0"" encoding=""utf-8""?>
<Package xmlns=""http://schemas.microsoft.com/appx/manifest/foundation/windows10""
         xmlns:uap=""http://schemas.microsoft.com/appx/manifest/uap/windows10""
         xmlns:desktop6=""http://schemas.microsoft.com/appx/manifest/desktop/windows10/6""
         IgnorableNamespaces=""uap desktop6"">
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
    <Application Id=""App"" Executable=""Minecraft.Windows.exe"" EntryPoint=""Minecraft_Win10.App"">
      <uap:VisualElements DisplayName=""{displayName}"" Square150x150Logo=""{square150}"" Square44x44Logo=""{square44}"" Description=""{description}"" ForegroundText=""light"" BackgroundColor=""transparent"">
        <uap:SplashScreen Image=""{splashImage}"" />
      </uap:VisualElements>
    </Application>
  </Applications>
  <Capabilities>
    <Capability Name=""internetClient"" />
  </Capabilities>
</Package>";

                File.WriteAllText(
                    manifestPath,
                    manifestContent);

                return true;
            }
            catch (Exception ex)
            {
                Trace.WriteLine(
                    "[EnsureWDAppManifest] ERROR: " + ex);

                return false;
            }
        }

        private async Task ExtractPackage(
            MCVersion v,
            string dlPath,
            string bkpsPath,
            string pkgPath,
            CancellationTokenSource cancelSource)
        {
            try
            {
                Trace.WriteLine(
                    "Extraction started");

                MainDataModel.Default.ProgressBarState
                    .SetProgressBarState(
                        LauncherState.isExtracting);

                if (Directory.Exists(v.GameDirectory))
                {
                    await DirectoryExtensions.DeleteAsync(
                        v.GameDirectory,
                        (x, y, phase) =>
                            ProgressWrapper(x, y, phase));
                }

                bool extractedAsZip = false;

                using (var fileStream =
                    File.OpenRead(pkgPath))
                {
                    byte[] header = new byte[4];

                    int read =
                        fileStream.Read(
                            header,
                            0,
                            4);

                    fileStream.Position = 0;

                    bool looksLikeZip =
                        read == 4 &&
                        header[0] == (byte)'P' &&
                        header[1] == (byte)'K';

                    if (looksLikeZip)
                    {
                        var progress =
                            new Progress<ZipProgress>();

                        progress.ProgressChanged +=
                            (s, z) =>
                                MainDataModel.Default.ProgressBarState
                                    .SetProgressBarProgress(
                                        currentProgress: z.Processed,
                                        totalProgress: z.Total);

                        await Task.Run(() =>
                        {
                            using var zipArchive =
                                new ZipArchive(fileStream);

                            zipArchive.ExtractToDirectory(
                                v.GameDirectory,
                                progress,
                                cancelSource);
                        });

                        extractedAsZip = true;
                    }
                }

                Directory.CreateDirectory(
                    v.GameDirectory);

                await File.WriteAllTextAsync(
                    v.IdentificationPath,
                    v.PackageID);

                if (extractedAsZip)
                {
                    string signaturePath =
                        Path.Combine(
                            v.GameDirectory,
                            "AppxSignature.p7x");

                    if (v.PackageType == PackageType.UWP &&
                        File.Exists(signaturePath))
                    {
                        File.Delete(signaturePath);
                    }
                }
                else
                {
                    string absolutePkg =
                        Path.GetFullPath(pkgPath);

                    string marker =
                        Path.Combine(
                            v.GameDirectory,
                            "cdn_package.txt");

                    await File.WriteAllTextAsync(
                        marker,
                        absolutePkg);

                    string versionsBackups =
                        Path.Combine(
                            Path.GetFullPath(
                                MainDataModel.Default.FilePaths.VersionsFolder),
                            "AppxBackups");

                    Directory.CreateDirectory(
                        versionsBackups);

                    string desiredBackup =
                        Path.Combine(
                            versionsBackups,
                            Path.GetFileName(absolutePkg));

                    if (!Path.GetFullPath(absolutePkg)
                        .Equals(
                            Path.GetFullPath(desiredBackup),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        if (File.Exists(desiredBackup))
                            File.Delete(desiredBackup);

                        File.Copy(
                            absolutePkg,
                            desiredBackup,
                            true);

                        await File.WriteAllTextAsync(
                            marker,
                            desiredBackup);
                    }

                    if (v.PackageType == PackageType.GDK)
                    {
                        await InstallGdkPackageAsync(
                            v,
                            desiredBackup);
                    }
                }

                if (File.Exists(dlPath) &&
                    !Path.GetFullPath(dlPath)
                        .Equals(
                            Path.GetFullPath(bkpsPath),
                            StringComparison.OrdinalIgnoreCase))
                {
                    if (!File.Exists(bkpsPath))
                        File.Move(dlPath, bkpsPath);
                    else
                        File.Delete(dlPath);
                }

                Trace.WriteLine(
                    "Extracted successfully -> " +
                    Path.GetFullPath(v.GameDirectory));
            }
            catch (PackageManagerException)
            {
                ResetTask();
                throw;
            }
            catch (TaskCanceledException e)
            {
                MainDataModel.Default.ProgressBarState
                    .SetProgressBarState(
                        LauncherState.isCanceling);

                await DirectoryExtensions.DeleteAsync(
                    v.GameDirectory,
                    (x, y, phase) =>
                        ProgressWrapper(x, y, phase));

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

        private async Task MaterializeGdkPackageAsync(
            MCVersion version,
            string packagePath,
            CancellationTokenSource cancelSource)
        {
            string packageFamily =
                Constants.GetPackageFamily(version.Type);

            var registeredPackages =
                PM.FindPackagesForUser(
                    string.Empty,
                    packageFamily)
                .ToList();

            string expectedVersion = version.Name;
            var existingVersion = registeredPackages.FirstOrDefault(
                package =>
                    IsPackageVersion(package, expectedVersion));

            bool restoreOriginalRegistration = false;
            string packageFullNameToRestore = null;
            string packageManifestToRestore = null;
            string packageFullNameToRemove = null;
            string stagingDirectory =
                version.GameDirectory +
                ".extracting-" +
                Guid.NewGuid().ToString("N");

            try
            {
                StorageFolder sourceFolder;

                if (existingVersion != null)
                {
                    sourceFolder =
                        existingVersion.InstalledLocation;
                }
                else
                {
                    if (registeredPackages.Count > 1)
                    {
                        throw new InvalidOperationException(
                            $"Multiple {packageFamily} versions are registered with Windows. The launcher cannot safely switch them temporarily.");
                    }

                    if (registeredPackages.Count == 1)
                    {
                        var packageToRestore = registeredPackages[0];
                        packageFullNameToRestore =
                            packageToRestore.Id.FullName;

                        string snapshotDirectory =
                            Path.Combine(
                                MainDataModel.Default.FilePaths.VersionsFolder,
                                "AppxBackups",
                                "WindowsRegistrationSnapshots",
                                SanitizePackageFolderName(
                                    packageFullNameToRestore));
                        packageManifestToRestore =
                            Path.Combine(
                                snapshotDirectory,
                                MCVersionExtensions.MainifestFileName);

                        if (Directory.Exists(snapshotDirectory) &&
                            !File.Exists(packageManifestToRestore))
                        {
                            await DirectoryExtensions.DeleteAsync(
                                snapshotDirectory,
                                (current, total, phase) =>
                                    ProgressWrapper(current, total, phase));
                        }

                        if (!Directory.Exists(snapshotDirectory))
                        {
                            MainDataModel.Default.ProgressBarState
                                .SetProgressBarState(
                                    LauncherState.isExtracting);
                            MainDataModel.Default.ProgressBarState
                                .SetProgressBarText(
                                    "Preparing backup of the installed version...");

                            await CopyGdkPackageDirectoryAsync(
                                packageToRestore.InstalledLocation,
                                snapshotDirectory,
                                cancelSource.Token,
                                CreatePackageCopyProgress());
                        }

                        if (!File.Exists(packageManifestToRestore))
                        {
                            throw new FileNotFoundException(
                                "The existing Windows game registration cannot be safely restored because its package manifest could not be backed up. The existing registration has not been changed.",
                                packageManifestToRestore);
                        }

                        MainDataModel.Default.ProgressBarState
                            .SetProgressBarState(
                                LauncherState.isRegisteringPackage);
                        MainDataModel.Default.ProgressBarState
                            .SetProgressBarText(
                                packageFullNameToRestore);

                        await DeploymentProgressWrapper(
                            PM.RemovePackageAsync(
                                packageFullNameToRestore,
                                Constants.PackageRemovalOptions));

                        restoreOriginalRegistration = true;
                    }

                    MainDataModel.Default.ProgressBarState
                        .SetProgressBarState(
                            LauncherState.isRegisteringPackage);
                    MainDataModel.Default.ProgressBarState
                        .SetProgressBarText(
                            Path.GetFileName(packagePath));

                    await DeploymentProgressWrapper(
                        PM.AddPackageAsync(
                            new Uri(packagePath),
                            null,
                            Constants.StorePackageDeploymentOptions));

                    var deployedPackages =
                        PM.FindPackagesForUser(
                                string.Empty,
                                packageFamily)
                            .ToList();

                    var deployedPackage =
                        deployedPackages.FirstOrDefault(
                            package =>
                                IsPackageVersion(
                                    package,
                                    expectedVersion));

                    if (deployedPackage == null &&
                        deployedPackages.Count == 1)
                    {
                        deployedPackage = deployedPackages[0];
                    }

                    if (deployedPackage == null)
                    {
                        throw new InvalidOperationException(
                            $"Windows deployed the GDK package, but its package identity could not be determined safely for temporary cleanup. Found {deployedPackages.Count} registered packages for {packageFamily}.");
                    }

                    packageFullNameToRemove =
                        deployedPackage.Id.FullName;

                    sourceFolder =
                        deployedPackage.InstalledLocation;
                }

                sourceFolder =
                    await FindGdkPayloadDirectoryAsync(sourceFolder);

                MainDataModel.Default.ProgressBarState
                    .SetProgressBarState(
                        LauncherState.isExtracting);
                MainDataModel.Default.ProgressBarState
                    .SetProgressBarText(
                        "Preparing local version files...");

                await CopyGdkPackageDirectoryAsync(
                    sourceFolder,
                    stagingDirectory,
                    cancelSource.Token,
                    CreatePackageCopyProgress());

                File.Copy(
                    version.IdentificationPath,
                    Path.Combine(
                        stagingDirectory,
                        MCVersionExtensions.IdentificationFilename),
                    true);

                string markerPath =
                    Path.Combine(
                        version.GameDirectory,
                        "cdn_package.txt");

                if (File.Exists(markerPath))
                {
                    File.Copy(
                        markerPath,
                        Path.Combine(
                            stagingDirectory,
                            "cdn_package.txt"),
                        true);
                }

                if (!HasRunnableGdkPayload(stagingDirectory))
                {
                    throw new InvalidDataException(
                        $"Windows deployed the GDK package, but its installed directory does not contain a runnable game at {sourceFolder.Path}.");
                }

                if (packageFullNameToRemove != null)
                {
                    MainDataModel.Default.ProgressBarState
                        .SetProgressBarState(
                            LauncherState.isRegisteringPackage);
                    MainDataModel.Default.ProgressBarState
                        .SetProgressBarText(
                            packageFullNameToRemove);

                    await DeploymentProgressWrapper(
                        PM.RemovePackageAsync(
                            packageFullNameToRemove,
                            Constants.PackageRemovalOptions));

                    packageFullNameToRemove = null;
                }

                if (Directory.Exists(version.GameDirectory))
                {
                    await DirectoryExtensions.DeleteAsync(
                        version.GameDirectory,
                        (current, total, phase) =>
                            ProgressWrapper(current, total, phase));
                }

                Directory.Move(
                    stagingDirectory,
                    version.GameDirectory);

                Trace.WriteLine(
                    $"GDK package materialized locally at {version.GameDirectory}");
            }
            finally
            {
                Exception cleanupError = null;

                try
                {
                    if (packageFullNameToRemove != null)
                    {
                        await DeploymentProgressWrapper(
                            PM.RemovePackageAsync(
                                packageFullNameToRemove,
                                Constants.PackageRemovalOptions));
                    }
                }
                catch (Exception ex)
                {
                    cleanupError = new InvalidOperationException(
                        $"Could not remove temporary Windows registration {packageFullNameToRemove}.",
                        ex);
                }

                try
                {
                    if (restoreOriginalRegistration)
                    {
                        if (cleanupError != null)
                        {
                            throw cleanupError;
                        }

                        if (!File.Exists(packageManifestToRestore))
                        {
                            throw new FileNotFoundException(
                                "The existing Windows game registration cannot be restored because its manifest is no longer available.",
                                packageManifestToRestore);
                        }

                        MainDataModel.Default.ProgressBarState
                            .SetProgressBarState(
                                LauncherState.isRegisteringPackage);
                        MainDataModel.Default.ProgressBarState
                            .SetProgressBarText(
                                packageFullNameToRestore);

                        await DeploymentProgressWrapper(
                            PM.RegisterPackageAsync(
                                new Uri(packageManifestToRestore),
                                null,
                                Constants.PackageDeploymentOptions));
                    }
                }
                catch (Exception ex)
                {
                    cleanupError = new InvalidOperationException(
                        $"Could not restore the pre-existing Windows registration {packageFullNameToRestore}. Its package files were preserved at {Path.GetDirectoryName(packageManifestToRestore)}.",
                        ex);
                }

                if (Directory.Exists(stagingDirectory))
                {
                    await DirectoryExtensions.DeleteAsync(
                        stagingDirectory,
                        (current, total, phase) =>
                            ProgressWrapper(current, total, phase));
                }

                if (cleanupError != null)
                    throw cleanupError;
            }
        }

        private async Task InstallGdkPackageAsync(
            MCVersion version,
            string packagePath)
        {
            if (!IsGdkPackageRegistered(version) &&
                !File.Exists(packagePath))
            {
                throw new FileNotFoundException(
                    $"The signed GDK package for {version.Name} was not found.",
                    packagePath);
            }

            if (!IsGdkPackageRegistered(version))
            {
                MainDataModel.Default.ProgressBarState
                    .SetProgressBarState(
                        LauncherState.isRegisteringPackage);
                MainDataModel.Default.ProgressBarState
                    .SetProgressBarText(
                        Path.GetFileName(packagePath));

                Trace.WriteLine(
                    $"Deploying signed GDK package through Windows: {packagePath}");

                await DeploymentProgressWrapper(
                    PM.AddPackageAsync(
                        new Uri(packagePath),
                        null,
                        Constants.StorePackageDeploymentOptions));
            }

            if (!IsGdkPackageRegistered(version))
            {
                throw new InvalidDataException(
                    $"Windows completed deployment but version {version.Name} is not registered for this user.");
            }

            await SaveGdkRegistrationMarkerAsync(version);

            Trace.WriteLine(
                $"GDK package {version.Name} is registered with Windows.");
        }

        private static async Task SaveGdkRegistrationMarkerAsync(
            MCVersion version)
        {
            Directory.CreateDirectory(version.GameDirectory);

            if (!File.Exists(version.IdentificationPath))
            {
                await File.WriteAllTextAsync(
                    version.IdentificationPath,
                    version.PackageID);
            }

            await File.WriteAllTextAsync(
                Path.Combine(
                    version.GameDirectory,
                    MCVersionExtensions.GdkRegistrationMarkerFilename),
                version.PackageID);
        }

        private bool IsGdkPackageRegistered(MCVersion version)
        {
            return FindRegisteredGdkPackage(version) != null;
        }

        private Package FindRegisteredGdkPackage(MCVersion version)
        {
            string packageFamily =
                Constants.GetPackageFamily(version.Type);

            return PM.FindPackagesForUser(
                    string.Empty,
                    packageFamily)
                .FirstOrDefault(
                    package =>
                        IsPackageVersion(
                            package,
                            version.Name));
        }

        private async Task<bool> TryLaunchRegisteredGdkPackageAsync(
            MCVersion version,
            bool keepLauncherOpen)
        {
            var package =
                FindRegisteredGdkPackage(version);
            if (package == null)
                return false;

            var appEntries =
                await package.GetAppListEntriesAsync();
            var appEntry =
                appEntries.FirstOrDefault(
                    entry =>
                        !string.IsNullOrWhiteSpace(
                            entry.DisplayInfo.DisplayName) &&
                        entry.DisplayInfo.DisplayName.Contains(
                            "Minecraft",
                            StringComparison.OrdinalIgnoreCase))
                ?? appEntries.FirstOrDefault();

            if (appEntry == null)
            {
                throw new InvalidOperationException(
                    $"Windows did not expose an application entry for GDK version {version.Name}.");
            }

            Trace.WriteLine(
                $"Launching registered GDK package {package.Id.FullName} via {appEntry.DisplayInfo.DisplayName}.");

            if (!await appEntry.LaunchAsync())
            {
                throw new InvalidOperationException(
                    $"Windows could not launch registered GDK version {version.Name}.");
            }

            await FinishLaunchAsync(keepLauncherOpen);
            return true;
        }

        private static string SanitizePackageFolderName(string packageFullName)
        {
            char[] invalidCharacters =
                Path.GetInvalidFileNameChars();

            return new string(
                packageFullName
                    .Select(character =>
                        invalidCharacters.Contains(character)
                            ? '_'
                            : character)
                    .ToArray());
        }

        private static async Task<StorageFolder>
            FindGdkPayloadDirectoryAsync(
                StorageFolder installedLocation)
        {
            if (await HasRunnableGdkPayloadAsync(installedLocation))
                return installedLocation;

            var contentFolder =
                (await installedLocation.GetFoldersAsync())
                    .FirstOrDefault(
                        folder => string.Equals(
                            folder.Name,
                            "Content",
                            StringComparison.OrdinalIgnoreCase));

            if (contentFolder != null &&
                await HasRunnableGdkPayloadAsync(contentFolder))
            {
                return contentFolder;
            }

            throw new InvalidDataException(
                $"The deployed GDK package has no runnable content in {installedLocation.Path} or its Content subdirectory.");
        }

        private static async Task<bool> HasRunnableGdkPayloadAsync(
            StorageFolder directory)
        {
            var files = await directory.GetFilesAsync();
            bool hasExecutable = files.Any(
                file => string.Equals(
                    file.Name,
                    "Minecraft.Windows.exe",
                    StringComparison.OrdinalIgnoreCase));
            bool hasManifest = files.Any(
                file => string.Equals(
                    file.Name,
                    MCVersionExtensions.MainifestFileName,
                    StringComparison.OrdinalIgnoreCase));
            bool hasGameConfig = files.Any(
                file => string.Equals(
                    file.Name,
                    "MicrosoftGame.Config",
                    StringComparison.OrdinalIgnoreCase));

            return hasExecutable && (hasManifest || hasGameConfig);
        }

        private static bool HasRunnableGdkPayload(string directory)
        {
            return File.Exists(
                       Path.Combine(
                           directory,
                           "Minecraft.Windows.exe")) &&
                   (File.Exists(
                        Path.Combine(
                            directory,
                            MCVersionExtensions.MainifestFileName)) ||
                    File.Exists(
                        Path.Combine(
                            directory,
                            "MicrosoftGame.Config")));
        }

        private static bool IsPackageVersion(
            Windows.ApplicationModel.Package package,
            string versionName)
        {
            if (!Version.TryParse(versionName, out var expected))
            {
                return false;
            }

            var actual = package.Id.Version;

            bool exactMatch =
                actual.Major == expected.Major &&
                actual.Minor == expected.Minor &&
                actual.Build == expected.Build &&
                actual.Revision == expected.Revision;

            if (exactMatch)
                return true;

            if (expected.Revision > 99)
                return false;

            string combinedBuild =
                expected.Build.ToString(
                    System.Globalization.CultureInfo.InvariantCulture) +
                expected.Revision.ToString(
                    "D2",
                    System.Globalization.CultureInfo.InvariantCulture);

            return int.TryParse(
                       combinedBuild,
                       System.Globalization.NumberStyles.None,
                       System.Globalization.CultureInfo.InvariantCulture,
                       out int gdkBuild) &&
                   actual.Major == expected.Major &&
                   actual.Minor == expected.Minor &&
                   actual.Build == gdkBuild &&
                   actual.Revision == 0;
        }

        private static async Task CopyPackageDirectoryAsync(
            StorageFolder sourceFolder,
            string destinationDirectory,
            CancellationToken cancellationToken,
            IProgress<(long Current, long Total, string File)> progress)
        {
            var files =
                new List<(StorageFile Source, string RelativePath, long Length)>();
            var folders =
                new Stack<(StorageFolder Folder, string RelativePath)>();
            folders.Push((sourceFolder, string.Empty));
            long totalBytes = 0;

            while (folders.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var current = folders.Pop();
                foreach (StorageFile sourceFile in
                    await current.Folder.GetFilesAsync())
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var properties =
                        await sourceFile.GetBasicPropertiesAsync();
                    long length = checked((long)properties.Size);
                    totalBytes = checked(totalBytes + length);

                    files.Add((
                        sourceFile,
                        Path.Combine(
                            current.RelativePath,
                            sourceFile.Name),
                        length));
                }

                foreach (StorageFolder childFolder in
                    await current.Folder.GetFoldersAsync())
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    folders.Push((
                        childFolder,
                        Path.Combine(
                            current.RelativePath,
                            childFolder.Name)));
                }
            }

            Directory.CreateDirectory(destinationDirectory);
            long completedBytes = 0;
            byte[] buffer =
                System.Buffers.ArrayPool<byte>.Shared.Rent(
                    1024 * 1024);
            var progressTimer = Stopwatch.StartNew();

            try
            {
                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    string destinationPath =
                        Path.Combine(
                            destinationDirectory,
                            file.RelativePath);
                    string parentDirectory =
                        Path.GetDirectoryName(destinationPath);

                    if (!string.IsNullOrEmpty(parentDirectory))
                        Directory.CreateDirectory(parentDirectory);

                    await using Stream source =
                        await file.Source.OpenStreamForReadAsync();
                    await using var destination = new FileStream(
                        destinationPath,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.None,
                        buffer.Length,
                        FileOptions.Asynchronous |
                        FileOptions.SequentialScan);

                    long currentFileBytes = 0;

                    while (true)
                    {
                        int bytesRead = await source.ReadAsync(
                            buffer.AsMemory(),
                            cancellationToken).ConfigureAwait(false);

                        if (bytesRead == 0)
                            break;

                        await destination.WriteAsync(
                            buffer.AsMemory(0, bytesRead),
                            cancellationToken).ConfigureAwait(false);

                        currentFileBytes += bytesRead;

                        if (progressTimer.ElapsedMilliseconds >= 200)
                        {
                            progress?.Report((
                                completedBytes + currentFileBytes,
                                totalBytes,
                                file.Source.Name));

                            progressTimer.Restart();
                        }
                    }

                    completedBytes += file.Length;

                    if (progressTimer.ElapsedMilliseconds >= 200 ||
                        completedBytes == totalBytes)
                    {
                        progress?.Report((
                            completedBytes,
                            totalBytes,
                            file.Source.Name));

                        progressTimer.Restart();
                    }
                }
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private async Task CopyGdkPackageDirectoryAsync(
            StorageFolder sourceFolder,
            string destinationDirectory,
            CancellationToken cancellationToken,
            IProgress<(long Current, long Total, string File)> progress)
        {
            await CopyPackageDirectoryAsync(
                sourceFolder,
                destinationDirectory,
                cancellationToken,
                progress);
        }

        private static IProgress<(long Current, long Total, string File)>
            CreatePackageCopyProgress()
        {
            return new Progress<(long Current, long Total, string File)>(
                progress =>
                {
                    MainDataModel.Default.ProgressBarState
                        .SetProgressBarText(progress.File);

                    if (progress.Total > 0)
                    {
                        MainDataModel.Default.ProgressBarState
                            .SetProgressBarProgress(
                                progress.Current,
                                progress.Total);
                    }
                });
        }

        /// <summary>
        /// Unregisters Minecraft packages with safe error handling.
        /// FIX #1: Gracefully handle packages that are no longer installed for current user.
        /// </summary>
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
                    foreach (var package in
                        PM.FindPackagesForUser(
                            string.Empty,
                            family))
                    {
                        string location = string.Empty;

                        try
                        {
                            location =
                                package.InstalledLocation?.Path
                                ?? string.Empty;
                        }
                        catch
                        {
                        }

                        bool sameLocation =
                            !string.IsNullOrWhiteSpace(location) &&
                            string.Equals(
                                Path.GetFullPath(location),
                                Path.GetFullPath(v.GameDirectory),
                                StringComparison.OrdinalIgnoreCase);

                        if (keepVersion && sameLocation)
                            continue;

                        if (mustMatchVersion &&
                            !sameLocation)
                            continue;

                        Trace.WriteLine(
                            "Removing Minecraft package: " +
                            package.Id.FullName);

                        MainDataModel.Default.ProgressBarState
                            .SetProgressBarText(
                                package.Id.FullName);

                        MainDataModel.Default.ProgressBarState
                            .SetProgressBarState(
                                LauncherState.isRemovingPackage);

                        try
                        {
                            await DeploymentProgressWrapper(
                                PM.RemovePackageAsync(
                                    package.Id.FullName,
                                    Constants.PackageRemovalOptions));
                        }
                        catch (Exception ex)
                        {
                            // FIX #1: Package may no longer be installed for current user
                            Trace.WriteLine(
                                $"Warning: Could not remove package {package.Id.FullName}: {ex.Message}. " +
                                "Package may already be uninstalled or inaccessible. Continuing...");
                            
                            // Continue with next package instead of crashing
                            continue;
                        }
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

        private async Task RedirectSaveData(
            string InstallationsFolderPath,
            VersionType type)
        {
            await Task.Run(() =>
            {
                try
                {
                    string localAppData =
                        Environment.GetFolderPath(
                            Environment.SpecialFolder.LocalApplicationData);

                    string LocalStateFolder =
                        Path.Combine(
                            localAppData,
                            "Packages",
                            Constants.GetPackageFamily(type),
                            "LocalState");

                    string PackageFolder =
                        Path.Combine(
                            LocalStateFolder,
                            "games",
                            "com.mojang");

                    string ProfileFolder =
                        Path.GetFullPath(
                            InstallationsFolderPath);

                    string RequiredDir =
                        Directory.GetParent(
                            PackageFolder).FullName;

                    if (Directory.Exists(PackageFolder))
                        Directory.Delete(
                            PackageFolder,
                            true);

                    if (!Directory.Exists(RequiredDir))
                        Directory.CreateDirectory(
                            RequiredDir);

                    DirectoryInfo profileDir =
                        Directory.CreateDirectory(
                            ProfileFolder);

                    bool symlinkCreated =
                        SymLinkHelper.CreateSymbolicLinkSafe(
                            PackageFolder,
                            ProfileFolder,
                            SymLinkHelper.SymbolicLinkType.Directory);

                    if (!symlinkCreated)
                    {
                        throw new SaveRedirectionFailedException(
                            new Exception(
                                "Failed to create symbolic link. Ensure Developer Mode is enabled or run as administrator."));
                    }

                    DirectoryInfo pkgDir =
                        Directory.CreateDirectory(
                            PackageFolder);

                    DirectoryInfo lsDir =
                        Directory.CreateDirectory(
                            LocalStateFolder);

                    SecurityIdentifier owner =
                        WindowsIdentity.GetCurrent().User;

                    SecurityIdentifier authenticatedUsersIdentity =
                        new SecurityIdentifier(
                            "S-1-5-11");

                    FileSystemAccessRule ownerAccess =
                        new FileSystemAccessRule(
                            owner,
                            FileSystemRights.FullControl,
                            InheritanceFlags.ObjectInherit |
                            InheritanceFlags.ContainerInherit,
                            PropagationFlags.None,
                            AccessControlType.Allow);

                    FileSystemAccessRule authenticatedUsersAccess =
                        new FileSystemAccessRule(
                            authenticatedUsersIdentity,
                            FileSystemRights.FullControl,
                            InheritanceFlags.ObjectInherit |
                            InheritanceFlags.ContainerInherit,
                            PropagationFlags.None,
                            AccessControlType.Allow);

                    var lsSecurity =
                        lsDir.GetAccessControl();

                    AuthorizationRuleCollection rules =
                        lsSecurity.GetAccessRules(
                            true,
                            true,
                            typeof(NTAccount));

                    List<FileSystemAccessRule> neededRules =
                        new List<FileSystemAccessRule>();

                    foreach (AccessRule rule in rules)
                    {
                        if (rule.IdentityReference
                            is SecurityIdentifier)
                        {
                            neededRules.Add(
                                new FileSystemAccessRule(
                                    rule.IdentityReference,
                                    FileSystemRights.FullControl,
                                    rule.InheritanceFlags,
                                    rule.PropagationFlags,
                                    rule.AccessControlType));
                        }
                    }

                    var pkgSecurity =
                        pkgDir.GetAccessControl();

                    pkgSecurity.SetOwner(owner);
                    pkgSecurity.AddAccessRule(
                        authenticatedUsersAccess);
                    pkgSecurity.AddAccessRule(
                        ownerAccess);

                    pkgDir.SetAccessControl(
                        pkgSecurity);

                    var profileSecurity =
                        profileDir.GetAccessControl();

                    profileSecurity.AddAccessRule(
                        authenticatedUsersAccess);

                    profileSecurity.AddAccessRule(
                        ownerAccess);

                    neededRules.ForEach(
                        x => profileSecurity.AddAccessRule(x));

                    profileDir.SetAccessControl(
                        profileSecurity);
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
        }

        private bool IsRegisteredAtGameDirectory(MCVersion v)
        {
            try
            {
                string expectedFamily =
                    Constants.GetPackageFamily(v.Type);

                foreach (var pkg in PM.FindPackagesForUser(string.Empty))
                {
                    string location = string.Empty;

                    try
                    {
                        location =
                            pkg.InstalledLocation?.Path
                            ?? string.Empty;
                    }
                    catch
                    {
                    }

                    bool sameDirectory =
                        !string.IsNullOrEmpty(location) &&
                        string.Equals(
                            Path.GetFullPath(location),
                            Path.GetFullPath(v.GameDirectory),
                            StringComparison.OrdinalIgnoreCase);

                    if (!sameDirectory)
                        continue;

                    if (PackageRegistrationMatcher.SameFamily(
                            expectedFamily,
                            pkg.Id.FamilyName))
                    {
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine(
                    "IsRegisteredAtGameDirectory check error: " +
                    ex.Message);
            }

            return false;
        }

        private bool IsPackageRegistered(MCVersion v)
        {
            try
            {
                string expectedFamily =
                    Constants.GetPackageFamily(v.Type);

                bool signedGdkRegistration =
                    v.PackageType == PackageType.GDK &&
                    !File.Exists(v.ExecutablePath) &&
                    File.Exists(
                        Path.Combine(
                            v.GameDirectory,
                            "cdn_package.txt"));

                foreach (var pkg in PM.FindPackagesForUser(string.Empty))
                {
                    string location = string.Empty;

                    try
                    {
                        location =
                            pkg.InstalledLocation?.Path
                            ?? string.Empty;
                    }
                    catch
                    {
                    }

                    bool sameDirectory =
                        !string.IsNullOrEmpty(location) &&
                        string.Equals(
                            Path.GetFullPath(location),
                            Path.GetFullPath(v.GameDirectory),
                            StringComparison.OrdinalIgnoreCase);

                    string installedVersion = null;

                    try
                    {
                        var id = pkg.Id;

                        if (id != null)
                        {
                            installedVersion =
                                $"{id.Version.Major}." +
                                $"{id.Version.Minor}." +
                                $"{id.Version.Build}." +
                                $"{id.Version.Revision}";
                        }
                    }
                    catch
                    {
                    }

                    if (PackageRegistrationMatcher.MatchesRegistration(
                            expectedFamily,
                            pkg.Id.FamilyName,
                            sameDirectory,
                            signedGdkRegistration,
                            v.Name,
                            installedVersion))
                    {
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine(
                    "IsPackageRegistered check error: " +
                    ex.Message);
            }

            return false;
        }

        #endregion

        #region Helpers

        protected async Task<DeploymentResult> DeploymentProgressWrapper(
            IAsyncOperationWithProgress<
                DeploymentResult,
                DeploymentProgress> t)
        {
            TaskCompletionSource<DeploymentResult> src =
                new TaskCompletionSource<DeploymentResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

            t.Progress +=
                (v, p) =>
                    MainDataModel.Default.ProgressBarState
                        .SetProgressBarProgress(
                            currentProgress:
                                Convert.ToInt64(p.percentage),
                            totalProgress: 100);

            t.Completed +=
                (v, p) =>
                {
                    MainDataModel.Default.ProgressBarState
                        .ResetProgressBarProgress();

                    if (p == AsyncStatus.Error)
                    {
                        try
                        {
                            string errorText =
                                v.GetResults().ErrorText;

                            Trace.WriteLine(
                                "Deployment failed: " +
                                errorText);

                            src.TrySetException(
                                new Exception(
                                    "Deployment failed: " +
                                    errorText));
                        }
                        catch (Exception ex)
                        {
                            src.TrySetException(ex);
                        }
                    }
                    else if (p == AsyncStatus.Canceled)
                    {
                        src.TrySetCanceled();
                    }
                    else
                    {
                        Trace.WriteLine(
                            "Deployment done: " + p);

                        try
                        {
                            src.TrySetResult(v.GetResults());
                        }
                        catch (Exception ex)
                        {
                            src.TrySetException(ex);
                        }
                    }
                };

            return await src.Task;
        }

        protected void ProgressWrapper(
            long current,
            long total,
            string text = null)
        {
            MainDataModel.Default.ProgressBarState
                .SetProgressBarProgress(
                    current,
                    total);

            MainDataModel.Default.ProgressBarState
                .SetProgressBarText(text);
        }

        protected void ResetTask()
        {
            MainDataModel.Default.ProgressBarState
                .ResetProgressBarProgress();

            MainDataModel.Default.ProgressBarState
                .SetProgressBarText();

            MainDataModel.Default.ProgressBarState
                .SetProgressBarState(
                    LauncherState.None);
        }

        protected void EndTask()
        {
            MainDataModel.Default.ProgressBarState
                .ResetProgressBarProgress();

            MainDataModel.Default.ProgressBarState
                .SetProgressBarText();

            MainDataModel.Default.ProgressBarState
                .SetProgressBarState(
                    LauncherState.None);

            MainDataModel.Default.ProgressBarState
                .SetProgressBarVisibility(false);
        }

        protected void StartTask()
        {
            MainDataModel.Default.ProgressBarState
                .SetProgressBarState(
                    LauncherState.isInitializing);

            MainDataModel.Default.ProgressBarState
                .SetProgressBarVisibility(true);
        }

        protected void SetCancelation(bool cancelState)
        {
            if (cancelState)
                CancelSource =
                    new CancellationTokenSource();

            MainDataModel.Default.ProgressBarState
                .AllowCancel = cancelState;

            MainDataModel.Default.ProgressBarState
                .CancelCommand =
                cancelState
                    ? new RelayCommand(o => Cancel())
                    : null;
        }

        protected void SetException(Exception e)
        {
            if (e.GetType() ==
                typeof(PackageExtractionFailedException))
            {
                SetError(
                    e,
                    "Extraction failed",
                    "Error_AppExtractionFailed_Title",
                    "Error_AppExtractionFailed");
            }
            else if (e.GetType() ==
                     typeof(PackageDownloadFailedException))
            {
                SetError(
                    e,
                    "Download failed",
                    "Error_AppDownloadFailed_Title",
                    "Error_AppDownloadFailed");
            }
            else if (e.GetType() ==
                     typeof(BetaAuthenticationFailedException))
            {
                SetError(
                    e,
                    "Authentication failed",
                    "Error_AuthenticationFailed_Title",
                    "Error_AuthenticationFailed");
            }
            else if (e.GetType() ==
                     typeof(AppLaunchFailedException))
            {
                SetError(
                    e,
                    "App launch failed",
                    "Error_AppLaunchFailed_Title",
                    "Error_AppLaunchFailed");
            }
            else if (e.GetType() ==
                     typeof(PackageRegistrationFailedException))
            {
                SetError(
                    e,
                    "App registration failed",
                    "Error_AppReregisterFailed_Title",
                    "Error_AppReregisterFailed");
            }
            else if (e.GetType() ==
                     typeof(PackageRemovalFailedException))
            {
                SetError(
                    e,
                    "App uninstall failed",
                    "Error_AppUninstallFailed_Title",
                    "Error_AppUninstallFailed");
            }
            else if (e.GetType() ==
                     typeof(SaveRedirectionFailedException))
            {
                SetError(
                    e,
                    "Save redirection failed",
                    "Error_SaveDirectoryRedirectionFailed_Title",
                    "Error_SaveDirectoryRedirectionFailed");
            }
            else if (e.GetType() ==
                     typeof(PackageDeregistrationFailedException))
            {
                SetError(
                    e,
                    "App deregistration failed",
                    "Error_AppDeregisteringFailed_Title",
                    "Error_AppDeregisteringFailed");
            }
            else if (e.GetType() ==
                     typeof(PackageDownloadAndExtractFailedException))
            {
                SetGenericError(e);
            }
            else if (e.GetType() ==
                     typeof(PackageProcessHookFailedException))
            {
                SetGenericError(e);
            }
            else if (e.GetType() ==
                     typeof(PackageExtractionCanceledException))
            {
                CancelAction();
            }
            else if (e.GetType() ==
                     typeof(PackageDownloadCanceledException))
            {
                CancelAction();
            }
            else
            {
                SetGenericError(e);
            }

            void CancelAction()
            {
                SetCancelation(false);
            }

            void SetGenericError(Exception ex)
            {
                _ = MainDataModel
                    .BackwardsCommunicationHost
                    .exceptionmsg(ex);
            }

            void SetError(
                Exception ex,
                string debugMessage,
                string dialogTitle,
                string dialogText)
            {
                Trace.WriteLine(
                    debugMessage + ":\n" +
                    ex);

                MainDataModel
                    .BackwardsCommunicationHost
                    .errormsg(
                        dialogTitle,
                        dialogText,
                        ex);
            }
        }

        #endregion

        #region IDisposable Implementation

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            CancelSource?.Dispose();
        }

        #endregion
    }
}