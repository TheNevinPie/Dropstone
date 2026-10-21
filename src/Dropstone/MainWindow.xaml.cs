using System.Diagnostics;
using System.Text;
using Dropstone.Models;
using Dropstone.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Dropstone;

public sealed partial class MainWindow : Window
{
    private readonly InstallerService _installer = new();
    private readonly DeploymentConfig _deployment = DeploymentConfig.Load();
    private LauncherInfo? _launcher;
    private InstalledMarker? _installed;
    private string? _archivePath;
    private string? _payloadUrl;
    private bool _replaceMode;
    private bool _busy;
    private CancellationTokenSource? _operation;

    public MainWindow()
    {
        InitializeComponent();

        try
        {
            AppWindow.Resize(new SizeInt32(860, 620));
            SystemBackdrop = new MicaBackdrop();
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppHeader);
            AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
            AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            AppWindow.Closing += (_, args) =>
            {
                // While files are being written the window must not close.
                // A running download may be cancelled by closing the window.
                if (_busy)
                {
                    args.Cancel = true;
                }
                else
                {
                    _operation?.Cancel();
                }
            };
        }
        catch
        {
            // Window chrome customization is best-effort.
        }

        ApplyDirectText();
        ShowDeploymentPreset();
        RefreshState();
    }

    private void ApplyDirectText()
    {
        if (!string.IsNullOrWhiteSpace(_deployment.Title))
        {
            DirectTitleText.Text = _deployment.Title;
        }

        if (!string.IsNullOrWhiteSpace(_deployment.Description))
        {
            DirectDescriptionText.Text = _deployment.Description;
        }
    }

    private void ShowDeploymentPreset()
    {
        if (!_deployment.Exists)
        {
            return;
        }

        var details = new List<string>();
        var defaultInstallDir = InstalledStateDetector.DefaultInstallDir;
        var hasCustomInstallDir =
            !string.IsNullOrWhiteSpace(_deployment.InstallDir) &&
            !string.Equals(
                Path.TrimEndingDirectorySeparator(_deployment.InstallDir),
                Path.TrimEndingDirectorySeparator(defaultInstallDir),
                StringComparison.OrdinalIgnoreCase);
        if (hasCustomInstallDir)
        {
            details.Add($"Install folder: {_deployment.InstallDir}");
        }
        if (!string.IsNullOrWhiteSpace(_deployment.LauncherExe))
        {
            details.Add($"Launcher: {_deployment.LauncherExe}");
        }
        if (!string.IsNullOrWhiteSpace(_deployment.VersionsDir))
        {
            details.Add($"Versions folder: {_deployment.VersionsDir}");
        }
        if ((_deployment.Mode is PayloadMode.Local or PayloadMode.Dll) && !string.IsNullOrWhiteSpace(_deployment.PayloadPath))
        {
            details.Add($"Payload: {_deployment.PayloadPath}");
        }
        else if (_deployment.Mode == PayloadMode.Url && _deployment.PayloadUri is not null)
        {
            details.Add($"Payload URL: {_deployment.PayloadUri}");
        }

        if (details.Count == 0 && _deployment.Warning is null)
        {
            return;
        }

        if (_deployment.Warning is not null)
        {
            details.Add($"Warning: {_deployment.Warning}");
        }

        PresetInfoBar.Severity = _deployment.Warning is null
            ? InfoBarSeverity.Informational
            : InfoBarSeverity.Warning;
        PresetInfoBar.Message = string.Join(Environment.NewLine, details);
        PresetInfoBar.IsOpen = true;
    }

    private LauncherInfo? DetectPresetLauncher()
    {
        var hasVersionsDir =
            !string.IsNullOrWhiteSpace(_deployment.VersionsDir) &&
            Directory.Exists(_deployment.VersionsDir);

        if (!string.IsNullOrWhiteSpace(_deployment.LauncherExe) && File.Exists(_deployment.LauncherExe))
        {
            var exePath = Path.GetFullPath(_deployment.LauncherExe);
            var versionsDir = hasVersionsDir
                ? _deployment.VersionsDir
                : LauncherDetector.FromExe(exePath).VersionsDir;
            return new LauncherInfo(exePath, versionsDir);
        }

        return hasVersionsDir ? new LauncherInfo(null, _deployment.VersionsDir) : null;
    }

    private void RefreshState()
    {
        _launcher = DetectPresetLauncher() ?? LauncherDetector.Detect();
        _installed = InstalledStateDetector.Detect(
            _deployment.IsDirectInstall ? _deployment.InstallDir : null);
        InstallPathBox.Text = _installed?.InstallDir ??
            (!string.IsNullOrWhiteSpace(_deployment.InstallDir)
                ? _deployment.InstallDir
                : InstalledStateDetector.DefaultInstallDir);
        UpdateLauncherInfo();

        if (_installed is not null)
        {
            ShowInstalled();
        }
        else if (_deployment.IsDirectInstall)
        {
            ShowDirect();
        }
        else
        {
            ShowSetup();
        }
    }

    private void UpdateLauncherInfo()
    {
        if (_launcher is null)
        {
            LauncherInfoBar.Severity = InfoBarSeverity.Warning;
            LauncherInfoBar.Title = "LeviLauncher not found";
            LauncherInfoBar.Message =
                "Install LeviLauncher first, or locate LeviLauncher.exe manually. The Dropstone task is configured for your Windows account.";
            LocateLauncherButton.Visibility = Visibility.Visible;
            NextButton.IsEnabled = false;
            return;
        }

        var hasExe = !string.IsNullOrWhiteSpace(_launcher.ExePath);
        LauncherInfoBar.Severity = hasExe ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
        LauncherInfoBar.Title = hasExe ? "LeviLauncher detected" : "LeviLauncher data found";

        var details = new List<string>();
        if (hasExe)
        {
            details.Add($"Launcher: {_launcher.ExePath}");
        }

        details.Add(string.IsNullOrWhiteSpace(_launcher.VersionsDir)
            ? "Versions folder: resolved automatically when the sync runs."
            : $"Versions folder: {_launcher.VersionsDir}");

        LauncherInfoBar.Message = string.Join(Environment.NewLine, details);
        LocateLauncherButton.Visibility = hasExe ? Visibility.Collapsed : Visibility.Visible;
        NextButton.IsEnabled = true;
    }

    private void ShowSetup()
    {
        ShowOnly(SetupPanel);
        SetupErrorBar.IsOpen = false;
    }

    private void ShowInstalled()
    {
        if (_installed is null)
        {
            return;
        }

        ShowOnly(InstalledPanel);

        InstalledInfoBar.Title = string.IsNullOrWhiteSpace(_installed.InstalledTitle)
            ? "Dropstone is already installed"
            : _installed.InstalledTitle;
        InstalledDescriptionText.Text = string.IsNullOrWhiteSpace(_installed.InstalledDescription)
            ? "The Dropstone worker is installed and keeps the payload in sync automatically. You only need this installer to replace the payload or uninstall Dropstone."
            : _installed.InstalledDescription;
        InstalledDescriptionText.Visibility = Visibility.Visible;

        var summary = new StringBuilder();
        summary.AppendLine($"Install folder: {_installed.InstallDir}");
        summary.AppendLine($"Installed: {_installed.InstalledAt.LocalDateTime:g}");
        summary.AppendLine($"Payload files: {_installed.PayloadFileCount}");

        if (!string.IsNullOrWhiteSpace(_installed.VersionsDir))
        {
            summary.AppendLine($"Versions folder: {_installed.VersionsDir}");
        }

        if (!string.IsNullOrWhiteSpace(_installed.PayloadArchive))
        {
            summary.AppendLine($"Source: {_installed.PayloadArchive}");
        }

        InstalledSummaryText.Text = summary.ToString().TrimEnd();
    }

    private void ShowDirect()
    {
        ShowOnly(DirectPanel);
        DirectErrorBar.IsOpen = false;
        DirectInstallPathText.Text = _deployment.InstallDir;
        DirectPayloadText.Text =
            _deployment.Mode == PayloadMode.Url && _deployment.PayloadUri is not null
                ? _deployment.PayloadUri.AbsoluteUri
                : _deployment.PayloadPath;
        DirectVersionsText.Text = string.IsNullOrWhiteSpace(_launcher?.VersionsDir)
            ? "Resolved automatically when the sync runs."
            : _launcher.VersionsDir;
    }

    private void ShowArchive()
    {
        ShowOnly(ArchivePanel);
        ArchiveTitleText.Text = _replaceMode ? "Choose replacement payload" : "Choose payload source";
        ArchiveErrorBar.IsOpen = false;
        ArchiveErrorBar.Severity = InfoBarSeverity.Error;
        ArchivePathText.Text = "No payload selected.";
        InstallButton.Content = _replaceMode ? "Replace payload" : "Install";
        InstallButton.IsEnabled = false;
        _archivePath = null;
        _payloadUrl = null;
        ApplyPresetPayload();
        ApplyLastArchive();
    }

    private void ApplyPresetPayload()
    {
        if ((_deployment.Mode is PayloadMode.Local or PayloadMode.Dll) && !string.IsNullOrWhiteSpace(_deployment.PayloadPath))
        {
            ArchivePathText.Text = _deployment.PayloadPath;
            if (File.Exists(_deployment.PayloadPath))
            {
                _archivePath = _deployment.PayloadPath;
                InstallButton.IsEnabled = true;
            }
            else
            {
                ArchiveErrorBar.Severity = InfoBarSeverity.Warning;
                ArchiveErrorBar.Message = $"The preset payload was not found: {_deployment.PayloadPath}";
                ArchiveErrorBar.IsOpen = true;
            }
        }
        else if (_deployment.Mode == PayloadMode.Url && _deployment.PayloadUri is not null)
        {
            _payloadUrl = _deployment.PayloadUri.AbsoluteUri;
            ArchivePathText.Text = _payloadUrl;
            InstallButton.IsEnabled = true;
        }

        if (_deployment.Warning is not null)
        {
            ArchiveErrorBar.Severity = InfoBarSeverity.Warning;
            ArchiveErrorBar.Message = _deployment.Warning;
            ArchiveErrorBar.IsOpen = true;
        }
    }

    private void ApplyLastArchive()
    {
        if (!_replaceMode || _installed is null || _archivePath is not null || _payloadUrl is not null)
        {
            return;
        }

        var previous = _installed.PayloadArchive;
        if (string.IsNullOrWhiteSpace(previous))
        {
            return;
        }

        if (Uri.TryCreate(previous, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            _payloadUrl = previous;
            ArchivePathText.Text = previous;
            InstallButton.IsEnabled = true;
            return;
        }

        if (File.Exists(previous))
        {
            _archivePath = previous;
            ArchivePathText.Text = previous;
            InstallButton.IsEnabled = true;
        }
    }

    private void ShowArchiveWithError(string message)
    {
        ShowOnly(ArchivePanel);
        ArchiveErrorBar.Severity = InfoBarSeverity.Error;
        ArchiveErrorBar.Message = message;
        ArchiveErrorBar.IsOpen = true;
        InstallButton.IsEnabled = _archivePath is not null || _payloadUrl is not null;
    }

    private void ShowOnly(FrameworkElement panel)
    {
        foreach (var candidate in new FrameworkElement[]
                 {
                     SetupPanel, InstalledPanel, DirectPanel, ArchivePanel, ProgressPanel, ResultPanel
                 })
        {
            candidate.Visibility = ReferenceEquals(candidate, panel)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private void ApplyProgress(ProgressUpdate update)
    {
        ProgressText.Text = update.Message;

        if (update.Percent < 0 || double.IsNaN(update.Percent))
        {
            ProgressBar.IsIndeterminate = true;
            ProgressPercentText.Text = string.Empty;
            return;
        }

        ProgressBar.IsIndeterminate = false;
        ProgressBar.Value = Math.Clamp(update.Percent, 0, 100);
        ProgressPercentText.Text = $"{Math.Round(update.Percent):0}%";
    }

    private void SetProgress(string message, double percent) =>
        ApplyProgress(new ProgressUpdate { Message = message, Percent = percent });

    private void ShowResult(string title, string message, bool isError)
    {
        ShowOnly(ResultPanel);
        ResultIcon.Glyph = isError ? "\uE783" : "\uE73E";
        ResultTitle.Text = title;
        ResultMessage.Text = message;
    }

    private static string NormalizeChosenInstallDir(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException("Choose an installation folder.");
        }

        var fullPath = Path.GetFullPath(value.Trim());
        var root = Path.GetPathRoot(fullPath);
        if (!string.IsNullOrEmpty(root) &&
            string.Equals(
                fullPath.TrimEnd(Path.DirectorySeparatorChar),
                root.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Choose a subfolder, not the root of a drive.");
        }

        return fullPath.TrimEnd(Path.DirectorySeparatorChar);
    }

    private async void BrowseInstall_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.ComputerFolder
        };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            InstallPathBox.Text = folder.Path;
            SetupErrorBar.IsOpen = false;
        }
    }

    private async void LocateLauncher_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.ComputerFolder
        };
        picker.FileTypeFilter.Add(".exe");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        _launcher = LauncherDetector.FromExe(file.Path);
        UpdateLauncherInfo();
        SetupErrorBar.IsOpen = false;
    }

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        SetupErrorBar.IsOpen = false;

        string installDir;
        try
        {
            installDir = NormalizeChosenInstallDir(InstallPathBox.Text);
            if (File.Exists(installDir))
            {
                throw new InvalidOperationException("The install location points to a file.");
            }
        }
        catch (Exception exception)
        {
            SetupErrorBar.Message = exception.Message;
            SetupErrorBar.IsOpen = true;
            return;
        }

        if (!await ConfirmForeignFolderAsync(installDir))
        {
            return;
        }

        InstallPathBox.Text = installDir;
        InstallPathBox.IsEnabled = true;
        _replaceMode = false;
        ShowArchive();
    }

    private async void BrowseArchive_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.Downloads
        };
        picker.FileTypeFilter.Add(".zip");
        picker.FileTypeFilter.Add(".rar");
        picker.FileTypeFilter.Add(".dll");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        _archivePath = file.Path;
        _payloadUrl = null;
        ArchivePathText.Text = file.Path;
        ArchiveErrorBar.IsOpen = false;
        InstallButton.IsEnabled = true;
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_replaceMode && _installed is not null)
        {
            _replaceMode = false;
            InstallPathBox.IsEnabled = true;
            ShowInstalled();
            return;
        }

        ShowSetup();
    }

    private void Replace_Click(object sender, RoutedEventArgs e)
    {
        if (_installed is null)
        {
            return;
        }

        _replaceMode = true;
        InstallPathBox.Text = _installed.InstallDir;
        InstallPathBox.IsEnabled = false;
        ShowArchive();
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_archivePath is null && _payloadUrl is null)
        {
            return;
        }

        var installDir = _replaceMode && _installed is not null
            ? _installed.InstallDir
            : InstallPathBox.Text.Trim();

        await RunInstallAsync(installDir, _replaceMode, ShowArchiveWithError);
    }

    private async void DirectInstall_Click(object sender, RoutedEventArgs e)
    {
        string installDir;
        try
        {
            installDir = NormalizeChosenInstallDir(_deployment.InstallDir);
            if (File.Exists(installDir))
            {
                throw new InvalidOperationException("The install location points to a file.");
            }
        }
        catch (Exception exception)
        {
            DirectErrorBar.Message = exception.Message;
            DirectErrorBar.IsOpen = true;
            return;
        }

        _archivePath = _deployment.Mode is PayloadMode.Local or PayloadMode.Dll
            ? _deployment.PayloadPath
            : null;
        _payloadUrl = _deployment.Mode == PayloadMode.Url
            ? _deployment.PayloadUri?.AbsoluteUri
            : null;
        _replaceMode = false;

        if (!await ConfirmForeignFolderAsync(installDir))
        {
            return;
        }

        await RunInstallAsync(
            installDir,
            false,
            ShowDirectError,
            _deployment.InstalledTitle,
            _deployment.InstalledDescription);
    }

    private void ShowDirectError(string message)
    {
        ShowOnly(DirectPanel);
        DirectErrorBar.Message = message;
        DirectErrorBar.IsOpen = true;
    }

    private async Task RunInstallAsync(
        string installDir,
        bool isReplace,
        Action<string> showError,
        string? installedTitle = null,
        string? installedDescription = null)
    {
        if (_archivePath is null && _payloadUrl is null)
        {
            return;
        }

        ShowOnly(ProgressPanel);
        SetProgress("Preparing...", ProgressUpdate.IndeterminatePercent);

        string? downloadedArchive = null;
        try
        {
            var progress = new Progress<ProgressUpdate>(ApplyProgress);
            var archivePath = _archivePath;
            if (archivePath is null)
            {
                // A download can take a long time. The window stays closable
                // while it runs, and closing it cancels the download.
                SetProgress("Downloading payload...", ProgressUpdate.IndeterminatePercent);
                using var download = new CancellationTokenSource();
                _operation = download;
                downloadedArchive = await Task.Run(
                    () => PayloadDownloader.Download(new Uri(_payloadUrl!), progress, download.Token));
                _operation = null;
                archivePath = downloadedArchive;
            }

            // from here on the installation changes files: the window must not
            // be closed in the middle of it
            _busy = true;

            var request = new InstallRequest
            {
                InstallDir = installDir,
                ArchivePath = archivePath,
                PayloadSource = _payloadUrl,
                LauncherExe = _launcher?.ExePath ?? _installed?.LauncherExe,
                VersionsDir = _launcher?.VersionsDir ?? _installed?.VersionsDir,
                IsReplace = isReplace,
                InstalledTitle = installedTitle,
                InstalledDescription = installedDescription
            };

            var outcome = await Task.Run(() => _installer.Install(request, progress));

            var title = isReplace ? "Payload replaced" : "Installed";
            var workerStatus = isReplace
                ? "The Dropstone worker is already installed and keeps the payload in sync automatically."
                : "The Dropstone worker is now installed and keeps the payload in sync automatically.";
            var message =
                $"Payload installed to {Path.GetFullPath(installDir)} ({outcome.PayloadFileCount} file(s)).{Environment.NewLine}" +
                $"The sync task and event watch were registered, and the payload was applied to existing LeviLauncher versions.{Environment.NewLine}{Environment.NewLine}" +
                $"{workerStatus} You can close this window; keep the installer only if you may want to replace the payload or uninstall Dropstone later.";

            if (!string.IsNullOrWhiteSpace(outcome.Warning))
            {
                message += $"{Environment.NewLine}{Environment.NewLine}Warning: {outcome.Warning}";
            }

            SetProgress(ProgressText.Text, 100);
            _busy = false;
            ShowResult(title, message, isError: false);
        }
        catch (Exception exception)
        {
            _busy = false;
            showError(exception.Message);
        }
        finally
        {
            _operation = null;
            if (downloadedArchive is not null)
            {
                PayloadDownloader.TryDelete(downloadedArchive);
            }
        }
    }

    private void OpenPayload_Click(object sender, RoutedEventArgs e)
    {
        if (_installed is null)
        {
            return;
        }

        var payloadDirectory = Path.Combine(_installed.InstallDir, "payload");
        var target = Directory.Exists(payloadDirectory) ? payloadDirectory : _installed.InstallDir;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{target}\"",
                UseShellExecute = true
            });
        }
        catch
        {
            // The shell could not open the folder.
        }
    }

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (_installed is null)
        {
            return;
        }

        var deployedFiles = DeployedPayloadScanner.Scan(_installed.InstallDir, _installed.VersionsDir);

        var dialogContent = new StackPanel { Spacing = 12 };
        dialogContent.Children.Add(new TextBlock
        {
            Text = "This removes the Dropstone worker, payload, scheduled task, and settings. LeviLauncher itself is not touched.",
            TextWrapping = TextWrapping.Wrap
        });

        var fileChoices = new List<(CheckBox Box, DeployedPayloadFile File)>();
        var removeRequested = false;
        if (deployedFiles.Count > 0)
        {
            var fileList = new StackPanel { Spacing = 4, Visibility = Visibility.Collapsed };
            foreach (var deployedFile in deployedFiles)
            {
                var box = new CheckBox
                {
                    Content = $"{deployedFile.RelativePath} ({deployedFile.Locations.Count} version folder(s))",
                    IsChecked = true
                };
                fileChoices.Add((box, deployedFile));
                fileList.Children.Add(box);
            }

            var removeToggle = new CheckBox
            {
                Content = "Also remove the deployed payload files from the version folders"
            };
            removeToggle.Checked += (_, _) =>
            {
                removeRequested = true;
                fileList.Visibility = Visibility.Visible;
            };
            removeToggle.Unchecked += (_, _) =>
            {
                removeRequested = false;
                fileList.Visibility = Visibility.Collapsed;
            };

            dialogContent.Children.Add(removeToggle);
            dialogContent.Children.Add(new ScrollViewer
            {
                MaxHeight = 220,
                Content = fileList
            });
        }

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Uninstall Dropstone?",
            Content = dialogContent,
            PrimaryButtonText = "Uninstall",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var selectedFiles = removeRequested
            ? fileChoices
                .Where(choice => choice.Box.IsChecked == true)
                .Select(choice => choice.File)
                .ToList()
            : new List<DeployedPayloadFile>();

        ShowOnly(ProgressPanel);
        SetProgress("Removing...", ProgressUpdate.IndeterminatePercent);
        _busy = true;

        try
        {
            var installDir = _installed.InstallDir;
            var progress = new Progress<ProgressUpdate>(ApplyProgress);
            var outcome = await Task.Run(() =>
                _installer.Uninstall(installDir, progress, selectedFiles));

            SetProgress(ProgressText.Text, 100);
            _busy = false;

            var message =
                "The Dropstone task, settings, and files were removed. LeviLauncher itself was not touched.";
            if (outcome.RemovedFiles > 0)
            {
                message +=
                    $"{Environment.NewLine}{outcome.RemovedFiles} deployed payload file(s) were removed from the version folders.";
            }

            if (!string.IsNullOrWhiteSpace(outcome.Warning))
            {
                message += $"{Environment.NewLine}{Environment.NewLine}Warning: {outcome.Warning}";
            }

            message += $"{Environment.NewLine}{Environment.NewLine}You can close this window when you are done.";
            ShowResult("Uninstalled", message, isError: false);
        }
        catch (Exception exception)
        {
            _busy = false;
            ShowResult("Uninstall failed", exception.Message, isError: true);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Shows a short notice and closes the window again.</summary>
    public async Task ShowNoticeAsync(string title, string message)
    {
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = title,
                Content = message,
                CloseButtonText = "OK"
            };
            await dialog.ShowAsync();
        }
        catch
        {
            // A dialog that cannot be shown must not crash the app.
        }

        Close();
    }

    /// <summary>
    /// A folder that is not empty and does not belong to Dropstone is accepted
    /// only after the user confirmed it - the uninstall removes the items
    /// Dropstone created, but the folder itself may hold important files.
    /// </summary>
    private async Task<bool> ConfirmForeignFolderAsync(string installDir)
    {
        if (!Directory.Exists(installDir))
        {
            return true;
        }

        if (File.Exists(Path.Combine(installDir, "install.json")))
        {
            return true;
        }

        var entries = Directory.EnumerateFileSystemEntries(installDir).Take(5).ToList();
        if (entries.Count == 0)
        {
            return true;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "The folder is not empty",
            Content = new StackPanel { Spacing = 8 },
            PrimaryButtonText = "Use this folder",
            CloseButtonText = "Choose another folder",
            DefaultButton = ContentDialogButton.Close
        };

        var panel = (StackPanel)dialog.Content;
        panel.Children.Add(new TextBlock
        {
            Text = "This folder already contains files. Dropstone adds its own files next to them and " +
                   "removes only those on uninstall.",
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(new TextBlock
        {
            Text = string.Join(Environment.NewLine, entries.Select(entry => "  " + entry)),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7
        });

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
