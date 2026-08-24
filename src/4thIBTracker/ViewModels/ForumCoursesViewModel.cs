using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FourthIBTracker.Services;

namespace FourthIBTracker.ViewModels;

public partial class ForumCoursesViewModel : ObservableObject
{
    private readonly AppConfig _config;
    private readonly GoogleSheetsService _sheets;
    private List<string> _ncoNames = new();

    /// <summary>Set by the view: fetches a URL through the embedded WebView2 and returns the page HTML.</summary>
    public Func<string, Task<string>>? FetchHtml { get; set; }

    public ObservableCollection<ForumThread> Results { get; } = new();
    public ObservableCollection<ForumThread> Upcoming { get; } = new();
    public ObservableCollection<DateTime> MonthOptions { get; } = new();

    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private string? error;
    [ObservableProperty] private string statusMessage = "";
    [ObservableProperty] private DateTime selectedMonth;
    [ObservableProperty] private string ncoNamesDisplay = "";

    public ForumCoursesViewModel(GoogleSheetsService sheets, AppConfig config)
    {
        _sheets = sheets;
        _config = config;
        var thisMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        for (int i = 0; i < 12; i++) MonthOptions.Add(thisMonth.AddMonths(-i));
        selectedMonth = thisMonth;
        NcoNamesDisplay = "Loaded from the SuT tracker on scan.";
    }

    /// <summary>
    /// The watched NCOs are the IC and 2IC of each configured-platoon section — the first two
    /// slots of each section block in the SuT tracker's ORBAT 2.0. Falls back to
    /// Forum.NcoNames in appsettings.json if the sheet can't be read.
    /// </summary>
    private async Task RefreshNcoNamesAsync()
    {
        var freshNames = new List<string>();
        try
        {
            var sut = _config.Sheet("SutRecord");
            var rows = await _sheets.ReadValuesFromConfiguredTabAsync(
                sut.Id, sut.Tab, "A1:AH120");
            freshNames = SheetParsers.ParsePlatoonSections(rows, _config.Platoon.Number)
                .Where(kv => kv.Key != "HQ")
                .SelectMany(kv => kv.Value.Take(2)) // slot 1 = IC, slot 2 = 2IC
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch { /* fall through to config */ }

        // Never retain the previous scan's roster: an NCO appointment can
        // change while this cached view remains open for the whole app session.
        _ncoNames = freshNames.Count > 0
            ? freshNames
            : _config.Forum.NcoNames.ToList();
        NcoNamesDisplay = string.Join("  ·  ", _ncoNames);
    }

    private bool IsByNco(ForumThread t) => _ncoNames.Any(n =>
        t.Author.Contains(n, StringComparison.OrdinalIgnoreCase) ||
        n.Contains(t.Author, StringComparison.OrdinalIgnoreCase) ||
        t.Author.Replace(".", "").Contains(n.Replace(".", ""), StringComparison.OrdinalIgnoreCase));

    [RelayCommand]
    public async Task ScanAsync()
    {
        if (FetchHtml == null) return;
        IsLoading = true; Error = null; StatusMessage = "";
        Results.Clear();
        Upcoming.Clear();
        try
        {
            StatusMessage = "Reading IC/2ICs from the SuT tracker…";
            await RefreshNcoNamesAsync();

            // Completed courses: filter to the selected month, stop once we've paged past it.
            int completedPages = await ScanForumAsync(
                _config.Forum.CoursesForumUrl,
                Math.Max(1, _config.Forum.MaxPages),
                t => t.Date == null ||
                     (t.Date.Value.Year == SelectedMonth.Year && t.Date.Value.Month == SelectedMonth.Month),
                stopBefore: SelectedMonth,
                into: Results,
                label: "completed");
            if (Error != null) return;

            // Upcoming courses: anything dated today or later (or undated).
            await ScanForumAsync(
                _config.Forum.UpcomingForumUrl,
                Math.Min(3, Math.Max(1, _config.Forum.MaxPages)),
                t => t.Date == null || t.Date.Value.Date >= new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1),
                stopBefore: null,
                into: Upcoming,
                label: "upcoming");

            StatusMessage = $"{Results.Count} completed in {SelectedMonth:MMMM yyyy} " +
                            $"({completedPages} page(s) scanned) · {Upcoming.Count} upcoming.";
        }
        catch (Exception ex) { Error = ex.Message; }
        finally { IsLoading = false; }
    }

    /// <summary>Scans one forum's pages, adding NCO-run threads that pass the filter. Returns pages scanned.</summary>
    private async Task<int> ScanForumAsync(
        string baseUrl, int maxPages, Func<ForumThread, bool> filter,
        DateTime? stopBefore, ObservableCollection<ForumThread> into, string label)
    {
        var seen = new HashSet<string>();
        int pages = 0;
        bool passedCutoff = false;

        for (int page = 1; page <= maxPages; page++)
        {
            StatusMessage = $"Scanning {label} courses — page {page}…";
            var html = await FetchHtml!(ForumCoursesService.PageUrl(baseUrl, page));

            if (page == 1 && ForumCoursesService.LooksLoggedOut(html))
            {
                Error = "The forum isn't showing any threads — you're probably not logged in. " +
                        "Open the 4thIB Website tab, log into the forum, then scan again.";
                return pages;
            }

            var threads = ForumCoursesService.ParseThreads(html, baseUrl);
            pages = page;
            if (threads.Count == 0)
            {
                if (page == 1)
                    Error = "No threads recognised on the first page — the page layout may " +
                            "have changed. Check you're logged in on the 4thIB Website tab.";
                break;
            }

            foreach (var t in threads)
            {
                if (!seen.Add(t.Url)) continue;
                if (IsByNco(t) && filter(t)) into.Add(t);
            }

            // Threads are newest-first; once a whole page is older than the
            // cutoff there's nothing further worth scanning.
            if (stopBefore != null)
            {
                var dated = threads.Where(t => t.Date != null).ToList();
                if (dated.Count > 0 && dated.All(t => t.Date < stopBefore))
                {
                    if (passedCutoff) break;
                    passedCutoff = true; // one extra page (sticky threads skew dates)
                }
            }
        }
        return pages;
    }

    [RelayCommand]
    private void Open(ForumThread thread)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            { FileName = thread.Url, UseShellExecute = true });
        }
        catch (Exception ex) { Error = ex.Message; }
    }
}
