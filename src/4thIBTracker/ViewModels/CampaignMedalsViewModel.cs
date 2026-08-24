using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FourthIBTracker.Models;
using FourthIBTracker.Services;

namespace FourthIBTracker.ViewModels;

public sealed class CampaignMedalSection
{
    public string Name { get; }
    public IReadOnlyList<CampaignMedalDue> Soldiers { get; }
    public string CountLabel => $"{Soldiers.Count} due";

    public CampaignMedalSection(string name, IReadOnlyList<CampaignMedalDue> soldiers)
    {
        Name = name;
        Soldiers = soldiers;
    }
}

public partial class CampaignMedalsViewModel : ObservableObject
{
    private static readonly string[] SectionOrder =
        ["HQ", "1 Section", "2 Section", "3 Section"];

    private readonly GoogleSheetsService _sheets;
    private readonly AppConfig _config;

    public ObservableCollection<CampaignMedalSection> Sections { get; } = new();

    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private bool hasLoaded;
    [ObservableProperty] private bool hasNoRequirements;
    [ObservableProperty] private string statusMessage = "Loading campaign medal checks…";
    [ObservableProperty] private string unmatchedMessage = "";
    [ObservableProperty] private string error = "";

    public string Subtitle =>
        $"Medals due for {_config.Platoon.Name}, cross-checked with the SuT ORBAT.";

    public CampaignMedalsViewModel(GoogleSheetsService sheets, AppConfig config)
    {
        _sheets = sheets;
        _config = config;
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        Error = "";
        UnmatchedMessage = "";
        StatusMessage = "Reading medal outcomes, recorded awards and the platoon ORBAT…";

        try
        {
            var outcomes = _config.Sheet("CampaignMedalOutcomes");
            var awards = _config.Sheet("CampaignMedalAwards");
            var orbat = _config.Sheet("SutRecord");

            // These sources are independent and modest in size. Reading their
            // complete used ranges keeps the parser resilient to appended rows,
            // moved tables and future operations/medal columns.
            var outcomesTask = _sheets.ReadValuesFromFirstTabAsync(
                outcomes.Id, [outcomes.Tab]);
            var awardsTask = _sheets.ReadValuesFromFirstTabAsync(
                awards.Id, [awards.Tab]);
            var orbatTask = _sheets.ReadValuesFromFirstTabAsync(
                orbat.Id, [orbat.Tab]);
            await Task.WhenAll(outcomesTask, awardsTask, orbatTask);

            var platoonSections = SheetParsers.ParsePlatoonSections(
                await orbatTask, _config.Platoon.Number);
            var result = SheetParsers.ParseCampaignMedals(
                await outcomesTask, await awardsTask, platoonSections);

            Sections.Clear();
            foreach (var section in SectionOrder)
            {
                var soldiers = result.Due
                    .Where(item => item.Section.Equals(section, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (soldiers.Count > 0)
                    Sections.Add(new CampaignMedalSection(section, soldiers));
            }

            HasNoRequirements = result.Due.Count == 0;
            StatusMessage = result.Due.Count == 0
                ? $"Checked {result.OrbatCount} soldiers from {_config.Platoon.Name} — no medals are currently due."
                : $"Checked {result.OrbatCount} soldiers from {_config.Platoon.Name} — " +
                  $"{result.Due.Count} {(result.Due.Count == 1 ? "soldier needs a medal" : "soldiers need medals")}.";

            if (result.Unmatched.Count > 0)
            {
                var names = string.Join(", ", result.Unmatched.Select(item => item.Name));
                UnmatchedMessage =
                    $"Not yet present in the campaign medal tracker ({result.Unmatched.Count}): {names}. " +
                    "They are excluded until an Outcomes record exists.";
            }
            HasLoaded = true;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            StatusMessage = "Campaign medal check failed.";
            HasNoRequirements = false;
        }
        finally
        {
            IsLoading = false;
        }
    }
}
