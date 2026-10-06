using System.Text.RegularExpressions;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FourthIBTracker.Services;

namespace FourthIBTracker.ViewModels;

public partial class UpdateViewModel : ObservableObject
{
    private readonly UpdateService _service;
    private UpdateRelease? _availableRelease;

    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool updateAvailable;
    [ObservableProperty] private string availableVersion = "";
    [ObservableProperty] private string statusMessage;
    [ObservableProperty] private string progressText = "";
    [ObservableProperty] private string releaseNotesTitle = "";
    [ObservableProperty] private string releaseNotes = "";
    [ObservableProperty] private bool hasReleaseNotes;

    public string CurrentVersion => _service.CurrentVersionText;
    public bool IsConfigured => _service.IsConfigured;
    public string Repository => _service.Repository;
    public string BannerText => UpdateAvailable ? $"UPDATE  v{AvailableVersion}" : "";

    public Func<bool>? ConfirmInstall { get; set; }
    public Action? ShutdownApplication { get; set; }

    public UpdateViewModel(UpdateService service)
    {
        _service = service;
        statusMessage = service.IsConfigured
            ? "Updates are checked automatically when the app starts."
            : "Automatic updates are not configured in this development build.";
    }

    partial void OnUpdateAvailableChanged(bool value) => OnPropertyChanged(nameof(BannerText));
    partial void OnAvailableVersionChanged(string value) => OnPropertyChanged(nameof(BannerText));

    [RelayCommand(AllowConcurrentExecutions = false)]
    public async Task CheckForUpdatesAsync() => await CheckAsync(silent: false);

    public async Task CheckAsync(bool silent)
    {
        if (IsBusy || !_service.IsConfigured) return;
        IsBusy = true;
        if (!silent) StatusMessage = "Checking GitHub Releases…";
        try
        {
            var result = await _service.CheckForUpdateDetailsAsync();
            _availableRelease = result.AvailableRelease;
            UpdateAvailable = _availableRelease != null;
            AvailableVersion = _availableRelease is null
                ? ""
                : $"{_availableRelease.Version.Major}.{_availableRelease.Version.Minor}.{_availableRelease.Version.Build}";
            var displayedRelease = result.DisplayRelease;
            HasReleaseNotes = displayedRelease is not null;
            ReleaseNotesTitle = displayedRelease is null
                ? ""
                : $"RELEASE NOTES — v{FormatVersion(displayedRelease.Version)}";
            ReleaseNotes = displayedRelease is null
                ? ""
                : FormatReleaseNotes(displayedRelease.ReleaseNotes);
            StatusMessage = _availableRelease is null
                ? $"Version {CurrentVersion} is up to date."
                : $"Version {AvailableVersion} is ready to download.";
        }
        catch (Exception ex)
        {
            if (!silent) StatusMessage = $"Update check failed: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    internal static string FormatReleaseNotes(string notes)
    {
        if (string.IsNullOrWhiteSpace(notes))
            return "No release notes were published for this version.";

        var lines = notes.Replace("\r\n", "\n").Split('\n')
            .Select(line =>
            {
                var text = Regex.Replace(line, @"^\s{0,3}#{1,6}\s*", "");
                text = Regex.Replace(text, @"^\s*[-*]\s+", "• ");
                text = Regex.Replace(text, @"\[([^\]]+)\]\([^\)]+\)", "$1");
                text = Regex.Replace(text, @"`([^`]+)`", "$1");
                return text.Replace("**", "").Replace("__", "").TrimEnd();
            })
            .ToList();

        for (var index = lines.Count - 1; index > 0; index--)
            if (lines[index].Length == 0 && lines[index - 1].Length == 0)
                lines.RemoveAt(index);
        return string.Join(Environment.NewLine, lines).Trim();
    }

    private static string FormatVersion(Version version) =>
        $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task InstallUpdateAsync()
    {
        if (IsBusy || _availableRelease is null) return;
        if (ConfirmInstall != null && !ConfirmInstall())
        {
            StatusMessage = "Update cancelled — existing unsaved work was kept.";
            return;
        }
        if (MessageBox.Show(
                $"Download version {AvailableVersion}, close the app and restart automatically?\n\n" +
                "Your settings, Google sign-in and todo list in AppData will not be changed.",
                "Install update",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        IsBusy = true;
        ProgressText = "0%";
        StatusMessage = $"Downloading version {AvailableVersion}…";
        try
        {
            var progress = new Progress<double>(value =>
                ProgressText = $"{Math.Clamp(value, 0, 1):P0}");
            var path = await _service.DownloadAsync(_availableRelease, progress);
            StatusMessage = "Verified. Closing and applying the update…";
            _service.LaunchInstaller(_availableRelease, path);
            ShutdownApplication?.Invoke();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Update failed: {ex.Message}";
            ProgressText = "";
            IsBusy = false;
        }
    }
}
