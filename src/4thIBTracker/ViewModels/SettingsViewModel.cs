using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FourthIBTracker.Services;
using Microsoft.Win32;

namespace FourthIBTracker.ViewModels;

public partial class SheetEntryViewModel : ObservableObject
{
    public string Key { get; init; } = "";
    [ObservableProperty] private string id = "";
    [ObservableProperty] private string tab = "";
}

public partial class BrowserTabEntryViewModel : ObservableObject
{
    [ObservableProperty] private string name = "";
    [ObservableProperty] private string url = "";
}

/// <summary>
/// Edits the per-user appsettings.json and notifies the main window to rebuild
/// any views that cache config-derived state.
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly AppConfig _config;

    public UpdateViewModel Updates { get; }

    public event Action? SettingsSaved;

    /// <summary>
    /// Supplied by the main window so it can warn before reloading pages that
    /// still contain unsaved user input.
    /// </summary>
    public Func<bool>? ConfirmApply { get; set; }

    // Platoon
    [ObservableProperty] private string platoonNumber = "";
    [ObservableProperty] private DayOfWeek operationDayOfWeek;
    [ObservableProperty] private string addressFrom = "";
    [ObservableProperty] private string signOff = "";
    [ObservableProperty] private string ncoPositions = "";
    [ObservableProperty] private string outstandingCourseExclusions = "";
    [ObservableProperty] private string signOffPhrase = "";

    // URLs / IDs
    [ObservableProperty] private string unitWebsite = "";
    [ObservableProperty] private string fillInFormId = "";
    [ObservableProperty] private string coursesForumId = "";
    [ObservableProperty] private string upcomingForumId = "";
    [ObservableProperty] private string patrolReportsForumId = "";
    [ObservableProperty] private string trainingReportsForumId = "";
    [ObservableProperty] private string platoonForumId = "";
    [ObservableProperty] private string pendingTransferForumIds = "";
    [ObservableProperty] private string completedTransferForumIds = "";

    [ObservableProperty] private string statusMessage = "";
    [ObservableProperty] private string credentialsStatus = "";

    public ObservableCollection<SheetEntryViewModel> Sheets { get; } = new();
    public ObservableCollection<BrowserTabEntryViewModel> BrowserTabs { get; } = new();
    public IReadOnlyList<DayOfWeek> OperationDays { get; } = Enum.GetValues<DayOfWeek>();

    public SettingsViewModel(AppConfig config, UpdateViewModel updates)
    {
        _config = config;
        Updates = updates;

        try
        {
            GoogleCredentialsService.EnsureMigrated();
            RefreshCredentialsStatus();
        }
        catch (Exception ex)
        {
            CredentialsStatus = $"Existing credentials could not be migrated: {ex.Message}";
        }

        platoonNumber = config.Platoon.Number.ToString();
        operationDayOfWeek = config.Platoon.OperationDayOfWeek;
        addressFrom = config.Platoon.AddressFrom;
        signOff = config.Platoon.SignOff;
        ncoPositions = string.Join(", ", config.Platoon.NcoTrackerPositions);
        outstandingCourseExclusions = string.Join(", ",
            config.Platoon.OutstandingCourseExclusions);
        signOffPhrase = config.Platoon.SignOffPhrase;

        unitWebsite = config.UnitWebsite;
        fillInFormId = config.FillInFormId;
        coursesForumId = config.Forum.CoursesForumId;
        upcomingForumId = config.Forum.UpcomingForumId;
        patrolReportsForumId = config.Forum.PatrolReportsForumId;
        trainingReportsForumId = config.Forum.TrainingReportsForumId;
        platoonForumId = config.Forum.PlatoonForumId;
        pendingTransferForumIds = string.Join(Environment.NewLine,
            config.Forum.PendingTransferForumIds);
        completedTransferForumIds = string.Join(Environment.NewLine,
            config.Forum.CompletedTransferForumIds);

        foreach (var tab in config.BrowserTabs)
            BrowserTabs.Add(new BrowserTabEntryViewModel
            {
                Name = tab.Name,
                Url = tab.Url,
            });

        foreach (var (key, sheet) in config.Spreadsheets)
            Sheets.Add(new SheetEntryViewModel { Key = key, Id = sheet.Id, Tab = sheet.Tab });
    }

    [RelayCommand]
    private void ImportCredentials()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose Google OAuth credentials.json",
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            DefaultExt = ".json",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            GoogleCredentialsService.Validate(dialog.FileName);

            if (GoogleCredentialsService.Exists && MessageBox.Show(
                    "Replace the installed Google credentials?\n\n" +
                    "The current file will be backed up and you will be asked to " +
                    "authorise Google again on the next sheet load.",
                    "Replace Google credentials",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            if (ConfirmApply != null && !ConfirmApply())
            {
                StatusMessage = "Credential import cancelled — existing unsaved work was kept.";
                return;
            }

            var result = GoogleCredentialsService.Import(dialog.FileName);
            RefreshCredentialsStatus();
            SettingsSaved?.Invoke();

            var backup = result.BackupPath is null
                ? ""
                : $" Previous credentials backed up to {result.BackupPath}.";
            StatusMessage = "Google credentials installed. Open a Google-backed page " +
                            "to authorise the account again." + backup;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Credential import failed: {ex.Message}";
        }
    }

    private void RefreshCredentialsStatus()
    {
        CredentialsStatus = GoogleCredentialsService.Exists
            ? $"Installed per-user: {GoogleCredentialsService.CredentialsPath}"
            : "Not installed. Import the Desktop OAuth JSON downloaded from Google Cloud.";
    }

    [RelayCommand]
    private void AddBrowserTab() => BrowserTabs.Add(new BrowserTabEntryViewModel());

    [RelayCommand]
    private void RemoveBrowserTab(BrowserTabEntryViewModel? tab)
    {
        if (tab is not null) BrowserTabs.Remove(tab);
    }

    [RelayCommand]
    private void Save()
    {
        try
        {
            if (!int.TryParse(PlatoonNumber.Trim(), out var n) || n < 1 || n > 20)
            {
                StatusMessage = "Platoon number must be a number (1–20).";
                return;
            }

            if (ConfirmApply != null && !ConfirmApply())
            {
                StatusMessage = "Save cancelled — existing unsaved work was kept.";
                return;
            }

            var normalizedWebsite = AppConfig.NormalizeUnitWebsite(UnitWebsite);
            if (UnitWebsite.Trim().Length > 0 && normalizedWebsite.Length == 0)
            {
                StatusMessage = "Unit website must be a valid HTTP or HTTPS address.";
                return;
            }
            var forumIds = new[]
            {
                CoursesForumId, UpcomingForumId, PatrolReportsForumId,
                TrainingReportsForumId, PlatoonForumId,
            }.Concat(SplitList(PendingTransferForumIds, '\n'))
             .Concat(SplitList(CompletedTransferForumIds, '\n'))
             .Where(value => value.Trim().Length > 0)
             .Select(AppConfig.ForumSection.NormalizeForumReference)
             .ToList();
            if (forumIds.Any(id => id.Length == 0))
            {
                StatusMessage = "Forum fields must contain numeric IDs (for example, 300).";
                return;
            }
            if (forumIds.Count > 0 && normalizedWebsite.Length == 0)
            {
                StatusMessage = "Set the Unit website before adding forum IDs.";
                return;
            }

            var browserTabs = BrowserTabs
                .Select(tab => new BrowserTab
                {
                    Name = tab.Name.Trim(),
                    Url = tab.Url.Trim(),
                })
                .Where(tab => tab.Name.Length > 0 || tab.Url.Length > 0)
                .ToList();
            if (browserTabs.Any(tab => tab.Name.Length == 0 || tab.Url.Length == 0))
            {
                StatusMessage = "Each sidebar browser tab needs both a name and a URL.";
                return;
            }
            if (browserTabs.Any(tab => !Uri.TryCreate(tab.Url, UriKind.Absolute, out _)))
            {
                StatusMessage = "Each sidebar browser tab needs a valid absolute URL.";
                return;
            }

            _config.Platoon.Number = n;
            _config.Platoon.OperationDayOfWeek = OperationDayOfWeek;
            _config.Platoon.AddressFrom = AddressFrom.Trim();
            _config.Platoon.SignOff = SignOff.Trim();
            _config.Platoon.NcoTrackerPositions = SplitList(NcoPositions, ',');
            _config.Platoon.OutstandingCourseExclusions =
                SplitList(OutstandingCourseExclusions, ',');
            _config.Platoon.SignOffPhrase = SignOffPhrase.Trim();

            _config.UnitWebsite = normalizedWebsite;
            _config.FillInFormId = FillInFormId.Trim();
            _config.Forum.CoursesForumId = NormalizeForumId(CoursesForumId);
            _config.Forum.UpcomingForumId = NormalizeForumId(UpcomingForumId);
            _config.Forum.PatrolReportsForumId = NormalizeForumId(PatrolReportsForumId);
            _config.Forum.TrainingReportsForumId = NormalizeForumId(TrainingReportsForumId);
            _config.Forum.PlatoonForumId = NormalizeForumId(PlatoonForumId);
            _config.Forum.PendingTransferForumIds = SplitList(PendingTransferForumIds, '\n')
                .Select(NormalizeForumId).ToList();
            _config.Forum.CompletedTransferForumIds = SplitList(CompletedTransferForumIds, '\n')
                .Select(NormalizeForumId).ToList();

            foreach (var s in Sheets)
                if (_config.Spreadsheets.TryGetValue(s.Key, out var sheet))
                {
                    sheet.Id = s.Id.Trim();
                    sheet.Tab = s.Tab;
                }

            _config.BrowserTabs = browserTabs;

            _config.Save();
            SettingsSaved?.Invoke();
            StatusMessage = "Saved and applied — no restart required.";
        }
        catch (Exception ex) { StatusMessage = $"Save failed: {ex.Message}"; }
    }

    private static List<string> SplitList(string text, char sep) =>
        text.Split(sep, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0).ToList();

    private static string NormalizeForumId(string value) =>
        AppConfig.ForumSection.NormalizeForumReference(value);

}
