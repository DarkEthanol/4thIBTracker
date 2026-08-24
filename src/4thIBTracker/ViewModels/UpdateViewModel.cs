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
            _availableRelease = await _service.CheckForUpdateAsync();
            UpdateAvailable = _availableRelease != null;
            AvailableVersion = _availableRelease is null
                ? ""
                : $"{_availableRelease.Version.Major}.{_availableRelease.Version.Minor}.{_availableRelease.Version.Build}";
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
                "Your settings, credentials and todo list in AppData will not be changed.",
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
