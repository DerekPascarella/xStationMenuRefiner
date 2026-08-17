using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MsBox.Avalonia.Enums;
using xStationMenuRefiner.App.Views.Shared;
using xStationMenuRefiner.Core;

// Written by Derek Pascarella (ateam)

namespace xStationMenuRefiner.App.Views;

public partial class UpdateWizardWindow : Window
{
    private readonly string _tag;
    private CancellationTokenSource? _cts;
    private bool _downloadComplete;
    private bool _installing;
    private bool _lockHeld;

    public UpdateWizardWindow()
    {
        InitializeComponent();
        _tag = "";
    }

    public UpdateWizardWindow(string tag, string version)
    {
        InitializeComponent();

        _tag = tag;
        StatusText.Text = $"Downloading update {version}...";

        KeyDown += (sender, e) =>
        {
            if (e.Key == Key.Escape && !_installing)
                CancelAndClose();
        };

        Opened += async (sender, e) =>
        {
            if (await StagedChangesBlockUpdateAsync())
            {
                Close();
                return;
            }

            await StartDownloadAsync();
        };
    }

    // Installing exits the process, which drops anything staged but not yet
    // applied to the card.
    private async Task<bool> StagedChangesBlockUpdateAsync()
    {
        if (Owner is not MainWindow main)
            return false;

        int pending = main.PendingChangeCount;

        if (pending == 0)
            return false;

        var answer = await DialogBox.ShowAsync(
            this,
            "Confirmation",
            $"{PlatformUtil.Counted(pending, "pending change")} not yet applied to the card. Installing an update now will discard them.\n\nContinue?",
            ButtonEnum.YesNo);

        return answer != ButtonResult.Yes;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_installing)
        {
            e.Cancel = true;
            return;
        }

        _cts?.Cancel();

        if (!_downloadComplete)
            UpdateManager.CleanupStagingDirectory();

        if (_lockHeld)
            UpdateManager.EndUpdate();

        base.OnClosing(e);
    }

    private void CancelDownloadButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_installing)
            return;

        CancelAndClose();
    }

    private void CancelAndClose()
    {
        _cts?.Cancel();
        UpdateManager.CleanupStagingDirectory();
        Close();
    }

    private async Task StartDownloadAsync()
    {
        if (!UpdateManager.TryBeginUpdate())
        {
            await DialogBox.ShowAsync(
                this,
                "Information",
                "Another update is already in progress.\n\nWait for it to finish before starting a new one.");

            Close();
            return;
        }

        _lockHeld = true;
        _cts = new CancellationTokenSource();

        var progress = new Progress<DownloadProgress>(report =>
        {
            if (report.TotalBytes > 0)
            {
                DownloadBar.Value = (double)report.BytesRead / report.TotalBytes * 100;
                SizeText.Text = $"{PlatformUtil.FormatSize(report.BytesRead)} of {PlatformUtil.FormatSize(report.TotalBytes)}";
            }
            else
            {
                DownloadBar.IsIndeterminate = true;
                SizeText.Text = $"{PlatformUtil.FormatSize(report.BytesRead)} downloaded";
            }

            SpeedText.Text = $"Download speed: {FormatSpeed(report.SpeedBytesPerSecond)}";
        });

        try
        {
            await UpdateManager.DownloadUpdateAsync(_tag, progress, _cts.Token);

            StatusText.Text = "Extracting update...";
            DownloadBar.IsIndeterminate = true;
            SpeedText.Text = "";
            SizeText.Text = "";
            await UpdateManager.ExtractUpdateAsync(_tag, _cts.Token);

            StatusText.Text = "Preparing update...";
            await UpdateManager.PrepareUpdateAsync();

            _downloadComplete = true;
            StatusText.Text = "Update ready to install.\n\nThe application will close and reopen automatically.";
            DownloadBar.IsIndeterminate = false;
            DownloadBar.Value = 100;
            SpeedText.Text = "";
            SizeText.Text = "";
            InstallButton.IsVisible = true;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            UpdateManager.CleanupStagingDirectory();
            await DialogBox.ShowAsync(this, "Error", FriendlyError(ex));
            Close();
        }
    }

    private void InstallButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_installing)
            return;

        _installing = true;
        InstallButton.IsEnabled = false;
        CancelDownloadButton.IsEnabled = false;

        // LaunchUpdaterAndExit ends in Environment.Exit, which skips Closing, so
        // the main window never gets to save its own placement.
        if (Owner is MainWindow main)
            main.PersistPlacement();

        UpdateManager.LaunchUpdaterAndExit();
    }

    private static string FriendlyError(Exception ex) => ex switch
    {
        // UpdateException carries text meant for this dialog.
        UpdateException => ex.Message,

        HttpRequestException http when http.StatusCode == HttpStatusCode.NotFound =>
            "The update file could not be downloaded.\n\n" +
            "It may have been removed from GitHub. Try again later, or download the latest version manually from the project page.",

        HttpRequestException =>
            "The update could not be downloaded.\n\n" +
            "Check your internet connection and try again.\n\n" +
            $"Details: {ex.Message}",

        InvalidDataException =>
            "The downloaded update file appears to be damaged.\n\n" +
            "Try again. If it keeps happening, the release may need to be uploaded again.\n\n" +
            $"Details: {ex.Message}",

        UnauthorizedAccessException =>
            "The update could not be installed.\n\n" +
            "The application cannot write to its own folder. Make sure no other program is using that folder, or try running as administrator.\n\n" +
            $"Details: {ex.Message}",

        IOException =>
            "The update could not be installed.\n\n" +
            "A file error occurred while writing the new version. Check that you have enough free disk space and that no antivirus is locking the application's folder.\n\n" +
            $"Details: {ex.Message}",

        _ =>
            "An unexpected error occurred while updating.\n\n" +
            $"Details: {ex.Message}",
    };

    private static string FormatSpeed(double bytesPerSecond)
    {
        if (bytesPerSecond < 1024)
            return $"{bytesPerSecond:0} B/s";

        if (bytesPerSecond < 1024 * 1024)
            return $"{bytesPerSecond / 1024.0:0.#} KB/s";

        return $"{bytesPerSecond / 1024.0 / 1024.0:0.#} MB/s";
    }
}
