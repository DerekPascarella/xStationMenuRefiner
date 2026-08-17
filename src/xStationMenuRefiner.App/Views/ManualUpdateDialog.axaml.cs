using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using xStationMenuRefiner.Core;

// Written by Derek Pascarella (ateam)

namespace xStationMenuRefiner.App.Views;

public partial class ManualUpdateDialog : Window
{
    public string LatestTag { get; private set; } = "";

    public ManualUpdateDialog()
    {
        InitializeComponent();
    }

    public ManualUpdateDialog(string latestTag, string latestVersion, ManualUpdateReason reason)
    {
        InitializeComponent();

        LatestTag = latestTag;

        ReasonText.Text = reason == ManualUpdateReason.UnsupportedPlatform
            ? $"A new version of xStation Menu Refiner ({latestVersion}) is available. Automatic updates are not supported on this platform."
            : $"A new version of xStation Menu Refiner ({latestVersion}) is available, but this release cannot be updated automatically.";

        KeyDown += (sender, e) =>
        {
            if (e.Key == Key.Escape)
                Close();
        };
    }

    private void OkButton_Click(object? sender, RoutedEventArgs e) => Close();

    private void SkipButton_Click(object? sender, RoutedEventArgs e)
    {
        UpdateAvailableDialog.SaveSkippedVersion(LatestTag);
        Close();
    }

    private void ReleasesLink_PointerPressed(object? sender, PointerPressedEventArgs e) =>
        PlatformUtil.OpenUrl(Constants.AppUrl + "/releases");
}
