using System.Text;
using LeviLauncherAutoCopyInstaller.Models;
using LeviLauncherAutoCopyInstaller.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace LeviLauncherAutoCopyInstaller;

public sealed partial class MainWindow : Window
{
    private readonly InstallerService _installer = new();
    private LauncherInfo? _launcher;
    private InstalledMarker? _installed;
    private string? _archivePath;
    private bool _replaceMode;
    private bool _closed;

    public MainWindow()
    {
        InitializeComponent();

        try
        {
            AppWindow.Resize(new SizeInt32(860, 620));
        }
        catch
        {
            // The default window size is still usable.
        }

        Closed += (_, _) => _closed = true;
        RefreshState();
    }

    private void RefreshState()
    {
        _launcher = LauncherDetector.Detect();
        _installed = InstalledStateDetector.Detect();
        InstallPathBox.Text = _installed?.InstallDir ?? InstalledStateDetector.DefaultInstallDir;
        UpdateLauncherInfo();

        if (_installed is not null)
        {
            ShowInstalled();
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
                "Install LeviLauncher first, or locate LeviLauncher.exe manually. The AutoCopy task is configured for your Windows account.";
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
            summary.AppendLine($"Source archive: {_installed.PayloadArchive}");
        }

        InstalledSummaryText.Text = summary.ToString().TrimEnd();
    }

    private void ShowArchive()
    {
        ShowOnly(ArchivePanel);
        ArchiveTitleText.Text = _replaceMode ? "Choose replacement payload" : "Choose payload archive";
        ArchiveErrorBar.IsOpen = false;
        ArchivePathText.Text = "No archive selected.";
        InstallButton.Content = _replaceMode ? "Replace payload" : "Install";
        InstallButton.IsEnabled = false;
        _archivePath = null;
    }

    private void ShowArchiveWithError(string message)
    {
        ShowOnly(ArchivePanel);
        ArchiveErrorBar.Message = message;
        ArchiveErrorBar.IsOpen = true;
        InstallButton.IsEnabled = _archivePath is not null;
    }

    private void ShowOnly(FrameworkElement panel)
    {
        foreach (var candidate in new FrameworkElement[]
                 {
                     SetupPanel, InstalledPanel, ArchivePanel, ProgressPanel, ResultPanel
                 })
        {
            candidate.Visibility = ReferenceEquals(candidate, panel)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private void ShowResult(string title, string message, bool isError)
    {
        ShowOnly(ResultPanel);
        ResultIcon.Glyph = isError ? "\uE783" : "\uE73E";
        ResultTitle.Text = title;
        ResultMessage.Text = message;
    }

    private async Task CloseAfterDelayAsync()
    {
        await Task.Delay(1400);
        if (!_closed)
        {
            Close();
        }
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

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        SetupErrorBar.IsOpen = false;

        try
        {
            var installDir = NormalizeChosenInstallDir(InstallPathBox.Text);
            if (File.Exists(installDir))
            {
                throw new InvalidOperationException("The install location points to a file.");
            }

            InstallPathBox.Text = installDir;
            InstallPathBox.IsEnabled = true;
            _replaceMode = false;
            ShowArchive();
        }
        catch (Exception exception)
        {
            SetupErrorBar.Message = exception.Message;
            SetupErrorBar.IsOpen = true;
        }
    }

    private async void BrowseArchive_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.Downloads
        };
        picker.FileTypeFilter.Add(".zip");
        picker.FileTypeFilter.Add(".rar");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        _archivePath = file.Path;
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
        if (_archivePath is null)
        {
            return;
        }

        var installDir = _replaceMode && _installed is not null
            ? _installed.InstallDir
            : InstallPathBox.Text.Trim();

        var request = new InstallRequest
        {
            InstallDir = installDir,
            ArchivePath = _archivePath,
            LauncherExe = _launcher?.ExePath ?? _installed?.LauncherExe,
            VersionsDir = _launcher?.VersionsDir ?? _installed?.VersionsDir,
            IsReplace = _replaceMode
        };

        ShowOnly(ProgressPanel);
        ProgressText.Text = "Preparing...";

        try
        {
            var progress = new Progress<string>(message => ProgressText.Text = message);
            var outcome = await Task.Run(() => _installer.Install(request, progress));

            var title = _replaceMode ? "Payload replaced" : "Installed";
            var message =
                $"Payload installed to {Path.GetFullPath(installDir)} ({outcome.PayloadFileCount} file(s)).{Environment.NewLine}" +
                $"The sync task was registered and the payload was applied to existing LeviLauncher versions.";

            if (!string.IsNullOrWhiteSpace(outcome.Warning))
            {
                message += $"{Environment.NewLine}{Environment.NewLine}Warning: {outcome.Warning}";
            }

            ShowResult(title, message, isError: false);

            if (string.IsNullOrWhiteSpace(outcome.Warning))
            {
                await CloseAfterDelayAsync();
            }
        }
        catch (Exception exception)
        {
            ShowArchiveWithError(exception.Message);
        }
    }

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (_installed is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Uninstall LeviLauncher AutoCopy?",
            Content = "This removes the logic files, payload, scheduled task, and settings. LeviLauncher itself is not touched.",
            PrimaryButtonText = "Uninstall",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        ShowOnly(ProgressPanel);
        ProgressText.Text = "Removing...";

        try
        {
            var installDir = _installed.InstallDir;
            var progress = new Progress<string>(message => ProgressText.Text = message);
            await Task.Run(() => _installer.Uninstall(installDir, progress));

            ShowResult(
                "Uninstalled",
                "The AutoCopy task, settings, and files were removed. LeviLauncher was not touched.",
                isError: false);
            await CloseAfterDelayAsync();
        }
        catch (Exception exception)
        {
            ShowResult("Uninstall failed", exception.Message, isError: true);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
