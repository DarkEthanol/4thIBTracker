using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FourthIBTracker.Services;

namespace FourthIBTracker.ViewModels;

/// <summary>One soldier available for selection.</summary>
public record UnitSoldier(string Name, string Origin)
{
    public string Display => $"{Name}  —  {Origin}";
}

/// <summary>A person queued for submission.</summary>
public partial class FillInEntry : ObservableObject
{
    public string Name { get; init; } = "";
    [ObservableProperty] private string from = "";
    [ObservableProperty] private string status = "";
    [ObservableProperty] private bool isSubmitted;
}

public partial class FillInsViewModel : ObservableObject
{
    private readonly GoogleSheetsService _sheets;
    private readonly AppConfig _config;
    private readonly FillInFormService _form;

    private List<UnitSoldier> _allSoldiers = new();

    public ObservableCollection<UnitSoldier> FilteredSoldiers { get; } = new();
    public ObservableCollection<FillInEntry> Pending { get; } = new();
    public ObservableCollection<string> FromOptions { get; } = new();
    public ObservableCollection<string> WhereOptions { get; } = new();

    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private string? error;
    [ObservableProperty] private string statusMessage = "";
    [ObservableProperty] private string searchText = "";
    [ObservableProperty] private UnitSoldier? selectedSoldier;
    [ObservableProperty] private DateTime eventDate = DateTime.Today;
    [ObservableProperty] private string whereFilledIn;
    [ObservableProperty] private bool testMode; // no UI toggle any more; kept for debugging
    [ObservableProperty] private string testLog = "";

    public FillInsViewModel(GoogleSheetsService sheets, AppConfig config)
    {
        _sheets = sheets;
        _config = config;
        _form = new FillInFormService(config.FillInFormId);
        whereFilledIn = config.Platoon.Name;
    }

    public bool HasData => _allSoldiers.Count > 0;

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsLoading = true; Error = null;
        try
        {
            // Form metadata and the SuT ORBAT are unrelated remote sources.
            // Load them together instead of making either wait for the other.
            var formTask = _form.LoadAsync();
            var soldiersTask = LoadSoldiersAsync();
            await Task.WhenAll(formTask, soldiersTask);

            FromOptions.Clear();
            foreach (var o in _form.FromOptions) FromOptions.Add(o);
            WhereOptions.Clear();
            foreach (var o in _form.WhereOptions) WhereOptions.Add(o);
            if (!WhereOptions.Contains(WhereFilledIn) && WhereOptions.Count > 0)
                WhereFilledIn = WhereOptions[0];

            _allSoldiers = await soldiersTask;
            ApplyFilter();
            StatusMessage = $"{_allSoldiers.Count} soldiers loaded from the SuT Record ORBAT.";

            async Task<List<UnitSoldier>> LoadSoldiersAsync()
            {
                var sut = _config.Sheet("SutRecord");
                var rows = await _sheets.ReadValuesFromConfiguredTabAsync(
                    sut.Id, sut.Tab, "A1:AH120");
                return SheetParsers.ParseOrbatSoldiers(rows)
                .Select(kv => new UnitSoldier(kv.Key, kv.Value))
                // Grouped by assignment (own platoon first, unknowns last), then surname.
                .OrderBy(s => s.Origin.Length == 0 ? 1 : 0)
                .ThenBy(s => s.Origin == _config.Platoon.Name ? "" : s.Origin)
                .ThenBy(s => s.Name.Split(' ').Last())
                .ToList();
            }
        }
        catch (Exception ex) { Error = ex.Message; }
        finally { IsLoading = false; }
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        FilteredSoldiers.Clear();
        var q = SearchText.Trim();
        foreach (var s in _allSoldiers.Where(s =>
                     q.Length == 0 || s.Name.Contains(q, StringComparison.OrdinalIgnoreCase)))
            FilteredSoldiers.Add(s);
    }

    [RelayCommand]
    private void Add()
    {
        if (SelectedSoldier is null)
        {
            // Allow adding someone who isn't on the ORBAT: use the typed text.
            var typed = SearchText.Trim();
            if (typed.Length == 0) return;
            if (Pending.Any(p => p.Name.Equals(typed, StringComparison.OrdinalIgnoreCase))) return;
            Pending.Add(new FillInEntry { Name = typed, From = FromOptions.FirstOrDefault() ?? "" });
            return;
        }
        if (Pending.Any(p => p.Name.Equals(SelectedSoldier.Name, StringComparison.OrdinalIgnoreCase)))
            return;
        Pending.Add(new FillInEntry { Name = SelectedSoldier.Name, From = SelectedSoldier.Origin });
    }

    [RelayCommand]
    private void Remove(FillInEntry entry) => Pending.Remove(entry);

    [RelayCommand]
    public async Task SubmitAllAsync()
    {
        var toSubmit = Pending.Where(p => !p.IsSubmitted).ToList();
        if (toSubmit.Count == 0) { StatusMessage = "Nothing to submit."; return; }

        if (TestMode)
        {
            // Preview only: show exactly what each POST would contain, send nothing.
            var log = new System.Text.StringBuilder();
            log.AppendLine($"POST {_form.SubmitUrl}");
            foreach (var entry in toSubmit)
            {
                var fields = _form.BuildFields(entry.Name, EventDate, entry.From, WhereFilledIn);
                log.AppendLine($"— {entry.Name}:");
                foreach (var (k, v) in fields.Where(f => f.Key.StartsWith("entry.")))
                    log.AppendLine($"    {k} = {v}");
                entry.Status = "TEST — not sent";
            }
            TestLog = log.ToString();
            StatusMessage = $"TEST MODE: previewed {toSubmit.Count} call(s) below — nothing was sent.";
            return;
        }

        IsLoading = true; Error = null;
        int ok = 0, failed = 0;
        foreach (var entry in toSubmit)
        {
            if (string.IsNullOrWhiteSpace(entry.From))
            {
                entry.Status = "✗ Pick where they're from first";
                failed++;
                continue;
            }
            try
            {
                entry.Status = "Submitting…";
                await _form.SubmitAsync(entry.Name, EventDate, entry.From, WhereFilledIn);
                entry.Status = "✓ Submitted";
                entry.IsSubmitted = true;
                ok++;
            }
            catch (Exception ex)
            {
                entry.Status = $"✗ {ex.Message}";
                failed++;
            }
        }
        IsLoading = false;
        StatusMessage = failed == 0
            ? $"All {ok} fill-in(s) submitted for {EventDate:dd MMM yyyy}."
            : $"{ok} submitted, {failed} failed — check the entries marked ✗.";
    }

    [RelayCommand]
    private void ClearSubmitted()
    {
        foreach (var e in Pending.Where(p => p.IsSubmitted).ToList())
            Pending.Remove(e);
    }
}
