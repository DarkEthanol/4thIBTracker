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
    [ObservableProperty] private string addressFrom = "";
    [ObservableProperty] private string signOff = "";
    [ObservableProperty] private string ncoPositions = "";
    [ObservableProperty] private string outstandingCourseExclusions = "";
    [ObservableProperty] private string signOffPhrase = "";

    // URLs / IDs
    [ObservableProperty] private string orbatUrl = "";
    [ObservableProperty] private string fillInFormId = "";
    [ObservableProperty] private string coursesForumUrl = "";
    [ObservableProperty] private string upcomingForumUrl = "";
    [ObservableProperty] private string patrolReportsForumUrl = "";
    [ObservableProperty] private string trainingReportsForumUrl = "";
    [ObservableProperty] private string operationsIndexUrl = "";
    [ObservableProperty] private string pendingTransferForums = "";
    [ObservableProperty] private string completedTransferForums = "";

    // Browser tabs: one per line, "Name | Url"
    [ObservableProperty] private string browserTabs = "";

    [ObservableProperty] private string statusMessage = "";
    [ObservableProperty] private string credentialsStatus = "";

    public ObservableCollection<SheetEntryViewModel> Sheets { get; } = new();

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
        addressFrom = config.Platoon.AddressFrom;
        signOff = config.Platoon.SignOff;
        ncoPositions = string.Join(", ", config.Platoon.NcoTrackerPositions);
        outstandingCourseExclusions = string.Join(", ",
            config.Platoon.OutstandingCourseExclusions);
        signOffPhrase = config.Platoon.SignOffPhrase;

        orbatUrl = config.OrbatUrl;
        fillInFormId = config.FillInFormId;
        coursesForumUrl = config.Forum.CoursesForumUrl;
        upcomingForumUrl = config.Forum.UpcomingForumUrl;
        patrolReportsForumUrl = config.Forum.PatrolReportsForumUrl;
        trainingReportsForumUrl = config.Forum.TrainingReportsForumUrl;
        operationsIndexUrl = config.Forum.OperationsIndexUrl;
        pendingTransferForums = string.Join(Environment.NewLine, config.Forum.PendingTransferForums);
        completedTransferForums = string.Join(Environment.NewLine, config.Forum.CompletedTransferForums);

        browserTabs = string.Join(Environment.NewLine,
            config.BrowserTabs.Select(t => $"{t.Name} | {t.Url}"));

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

            _config.Platoon.Number = n;
            _config.Platoon.AddressFrom = AddressFrom.Trim();
            _config.Platoon.SignOff = SignOff.Trim();
            _config.Platoon.NcoTrackerPositions = SplitList(NcoPositions, ',');
            _config.Platoon.OutstandingCourseExclusions =
                SplitList(OutstandingCourseExclusions, ',');
            _config.Platoon.SignOffPhrase = SignOffPhrase.Trim();

            _config.OrbatUrl = OrbatUrl.Trim();
            _config.FillInFormId = FillInFormId.Trim();
            _config.Forum.CoursesForumUrl = CoursesForumUrl.Trim();
            _config.Forum.UpcomingForumUrl = UpcomingForumUrl.Trim();
            _config.Forum.PatrolReportsForumUrl = PatrolReportsForumUrl.Trim();
            _config.Forum.TrainingReportsForumUrl = TrainingReportsForumUrl.Trim();
            _config.Forum.OperationsIndexUrl = OperationsIndexUrl.Trim();
            _config.Forum.PendingTransferForums = SplitList(PendingTransferForums, '\n');
            _config.Forum.CompletedTransferForums = SplitList(CompletedTransferForums, '\n');

            foreach (var s in Sheets)
                if (_config.Spreadsheets.TryGetValue(s.Key, out var sheet))
                {
                    sheet.Id = s.Id.Trim();
                    sheet.Tab = s.Tab;
                }

            _config.BrowserTabs = SplitList(BrowserTabs, '\n')
                .Select(line => line.Split('|', 2))
                .Where(p => p.Length == 2 && p[1].Trim().Length > 0)
                .Select(p => new BrowserTab { Name = p[0].Trim(), Url = p[1].Trim() })
                .ToList();

            _config.Save();
            SettingsSaved?.Invoke();
            StatusMessage = "Saved and applied — no restart required.";
        }
        catch (Exception ex) { StatusMessage = $"Save failed: {ex.Message}"; }
    }

    private static List<string> SplitList(string text, char sep) =>
        text.Split(sep, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0).ToList();

}
