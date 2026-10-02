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

                /*
                 * GDK installs land in XboxGames with GameLaunchHelper as
                 * the AppX entry point (Microsoft updater). Prefer launching
                 * Minecraft.Windows.exe directly / via a loose FullTrust
                 * registration so Play does not require a manual start.
                 */
                if (v.PackageType == PackageType.GDK && !LaunchEditor)
                {
                    await PrepareGdkForLaunchAsync(v);

                    if (await TryLaunchExecutableAsync(
                            v,
                            KeepLauncherOpen))
                    {
                        return;
                    }
                }

                if (!IsPackageRegistered(v))
                {
                    Trace.WriteLine(
                        $"Package not registered for {v.Name} at {v.GameDirectory}, registering...");

                    await UnregisterPackage(v, keepVersion: false);
                    await RegisterPackage(v);

                    if (v.PackageType == PackageType.GDK)
                    {
                        await PrepareGdkForLaunchAsync(v);

                        if (!LaunchEditor &&
                            await TryLaunchExecutableAsync(
                                v,
                                KeepLauncherOpen))
                        {
                            return;
                        }
                    }
                }

                if (!LaunchEditor &&
                    IsRegisteredAtGameDirectory(v) &&
                    await TryLaunchViaAppDiagnosticInfoAsync(
                        v,
                        KeepLauncherOpen))
                {
                    return;
                }

                if (!LaunchEditor &&
                    await TryLaunchExecutableAsync(
                        v,
                        KeepLauncherOpen))
                {
                    return;
                }

                if (await Launcher.LaunchUriAsync(
                    new Uri(
                        $"{Constants.GetUri(v.Type)}:?Editor={LaunchEditor}")))
                {
                    Trace.WriteLine("App launch finished via URI!");
                    await FinishLaunchAsync(KeepLauncherOpen);
                }
                else
                {
                    SetException(
                        new AppLaunchFailedException(
                            "Impossible to launch Minecraft: package not found or failed to start",
                            new Exception()));
                }
            }
            catch (Exception e)
            {
                EndTask();
                SetException(new AppLaunchFailedException(e));
            }
        }

        public async Task InstallPackage(
            MCVersion v,
            string dirPath)
        {
            try
            {
                StartTask();

                bool hasFiles = v.HasPlayableFiles;

                if (!hasFiles)
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

                if (!IsPackageRegistered(v))
                {
                    await UnregisterPackage(v, keepVersion: false);
                    await RegisterPackage(v);
                }
                else
                {
                    Trace.WriteLine(
                        $"Skipping redeploy for {v.Name} — already registered at {v.GameDirectory}");
                }

                if (v.PackageType == PackageType.GDK)
                {
                    await PrepareGdkForLaunchAsync(v);
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
            await DecryptAndMoveEXEAsync(v);
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
            string exePath = null;
            string workingDirectory = null;

            bool localFolderLooksComplete =
                File.Exists(v.ExecutablePath) &&
                (File.Exists(
                     Path.Combine(
                         v.GameDirectory,
                         "MicrosoftGame.Config")) ||
                 File.Exists(v.ManifestPath));

            // Prefer XboxGames Content when the version folder only has a
            // decrypted exe stub — that folder cannot run the full game.
            string xboxExe = FindXboxGamesMinecraftExe(v.Type);

            if (localFolderLooksComplete)
            {
                exePath = v.ExecutablePath;
                workingDirectory = v.GameDirectory;
            }
            else if (!string.IsNullOrWhiteSpace(xboxExe))
            {
                exePath = xboxExe;
                workingDirectory = Path.GetDirectoryName(xboxExe);
            }
            else if (File.Exists(v.ExecutablePath))
            {
                exePath = v.ExecutablePath;
                workingDirectory = v.GameDirectory;
            }

            if (string.IsNullOrWhiteSpace(exePath) ||
                !File.Exists(exePath))
            {
                return false;
            }

            Trace.WriteLine(
                "Launching Minecraft.Windows.exe directly (bypass updater): " +
                exePath);

            var psi = new ProcessStartInfo(exePath)
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = true
            };

            Process.Start(psi);
            await FinishLaunchAsync(KeepLauncherOpen);
            return true;
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

        private async Task DecryptAndMoveEXEAsync(MCVersion v)
        {
            await Task.Run(() =>
            {
                try
                {
                    string directory =
                        Path.GetFullPath(v.GameDirectory);

                    string exeDstPath =
                        Path.Combine(
                            directory,
                            "Minecraft.Windows.exe");

                    if (File.Exists(exeDstPath))
                    {
                        Trace.WriteLine(
                            $"Minecraft.Windows.exe already exists at {exeDstPath}");

                        return;
                    }

                    string exeSrcPath =
                        FindXboxGamesMinecraftExe(v.Type);

                    if (string.IsNullOrWhiteSpace(exeSrcPath) ||
                        !File.Exists(exeSrcPath))
                    {
                        Trace.WriteLine(
                            "Minecraft.Windows.exe was not found in XboxGames — skipping decrypt copy.");
                        return;
                    }

                    string packageFamilyName =
                        Constants.GetPackageFamily(v.Type);

                    Directory.CreateDirectory(directory);

                    MainDataModel.Default.ProgressBarState
                        .SetProgressBarState(
                            LauncherState.isExtracting);

                    Trace.WriteLine(
                        $"Extracting Minecraft.Windows.exe -> {exeDstPath}");

                    string copyCommand =
                        $"Copy-Item -LiteralPath '{exeSrcPath}' " +
                        $"-Destination '{exeDstPath}' -Force";

                    string command =
                        "Invoke-CommandInDesktopPackage " +
                        $"-PackageFamilyName '{packageFamilyName}' " +
                        "-App 'Game' " +
                        "-Command 'powershell.exe' " +
                        $"-Args '-NoProfile -Command \"{copyCommand}\"'";

                    var psi = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments =
                            "-NoProfile -ExecutionPolicy Bypass -Command " +
                            "\"" + command.Replace("\"", "\\\"") + "\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };

                    using Process process =
                        Process.Start(psi);

                    if (process == null)
                    {
                        throw new InvalidOperationException(
                            "Failed to start PowerShell.");
                    }

                    string output =
                        process.StandardOutput.ReadToEnd();

                    string error =
                        process.StandardError.ReadToEnd();

                    process.WaitForExit();

                    if (!string.IsNullOrWhiteSpace(output))
                    {
                        Trace.WriteLine(
                            "DecryptAndMoveEXE output: " +
                            output);
                    }

                    if (!string.IsNullOrWhiteSpace(error))
                    {
                        Trace.WriteLine(
                            "DecryptAndMoveEXE error: " +
                            error);
                    }

                    // Fallback: plain copy if container invoke failed
                    // (exe may already be readable outside the package).
                    if (!File.Exists(exeDstPath))
                    {
                        try
                        {
                            File.Copy(exeSrcPath, exeDstPath, true);
                        }
                        catch (Exception copyEx)
                        {
                            Trace.WriteLine(
                                "Plain XboxGames exe copy failed: " +
                                copyEx.Message);
                        }
                    }

                    if (!File.Exists(exeDstPath))
                    {
                        Trace.WriteLine(
                            "Minecraft.Windows.exe was not copied successfully.");
                        return;
                    }

                    Trace.WriteLine(
                        $"Minecraft.Windows.exe extracted successfully: {exeDstPath}");
                }
                catch (Exception ex)
                {
                    Trace.WriteLine(
                        "DecryptAndMoveEXEAsync error: " + ex);
                }
            });
        }

        private static string FindXboxGamesMinecraftExe(VersionType versionType)
        {
            string contentDir =
                FindXboxGamesContentDirectory(versionType);

            if (string.IsNullOrWhiteSpace(contentDir))
                return null;

            string exe =
                Path.Combine(
                    contentDir,
                    "Minecraft.Windows.exe");

            return File.Exists(exe) ? exe : null;
        }

        private static string FindXboxGamesContentDirectory(VersionType versionType)
        {
            const string xboxGamesRoot = @"C:\XboxGames";

            if (!Directory.Exists(xboxGamesRoot))
                return null;

            string baseName =
                versionType == VersionType.Preview
                    ? "Minecraft Preview for Windows"
                    : "Minecraft for Windows";

            // Prefer the exact folder, then numbered leftovers like
            // "Minecraft for Windows (1)" from repeated GDK extracts.
            var directories = Directory
                .GetDirectories(xboxGamesRoot, baseName + "*")
                .OrderBy(path =>
                    string.Equals(
                        Path.GetFileName(path),
                        baseName,
                        StringComparison.OrdinalIgnoreCase)
                        ? 0
                        : 1)
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (string directory in directories)
            {
                string content =
                    Path.Combine(directory, "Content");

                if (Directory.Exists(content))
                    return content;
            }

            return null;
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
      <uap:VisualElements DisplayName=""{displayName}"" Square150x150Logo=""{square150}"" Square44x44Logo=""{square44}"" Description=""{description}"" ForegroundText=""light"" BackgroundColor=""#000000"">
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

                try
                {
                    using var fileStream =
                        File.OpenRead(pkgPath);

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
                catch (Exception ex)
                {
                    Trace.WriteLine(
                        "Zip extraction unavailable: " +
                        ex.Message);
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

        protected async Task DeploymentProgressWrapper(
            IAsyncOperationWithProgress<
                DeploymentResult,
                DeploymentProgress> t)
        {
            TaskCompletionSource<int> src =
                new TaskCompletionSource<int>();

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
                        string errorText =
                            v.GetResults().ErrorText;

                        Trace.WriteLine(
                            "Deployment failed: " +
                            errorText);

                        src.SetException(
                            new Exception(
                                "Deployment failed: " +
                                errorText));
                    }
                    else
                    {
                        Trace.WriteLine(
                            "Deployment done: " + p);

                        src.SetResult(1);
                    }
                };

            await src.Task;
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