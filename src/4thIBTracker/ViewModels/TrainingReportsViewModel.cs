using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FourthIBTracker.Services;

namespace FourthIBTracker.ViewModels;

public sealed record TrainingReportMonth(DateTime Month, List<PatrolNight> Nights)
{
    public string Label => Month.ToString("MMMM yyyy");
    public int ReportCount => Nights.Sum(night => night.SubmittedCount + night.Extras.Count);
    public string Summary => $"{Nights.Count} PDT date(s) · {ReportCount} report(s)";
    public override string ToString() => Label;
}

/// <summary>
/// Scans the unit-wide Training Reports archive and presents only the configured
/// platoon's PDT reports, grouped by month and training date. The parsed archive
/// is cached per platoon so repeat visits are instant and later refreshes only
/// need to scan the newest forum pages.
/// </summary>
public partial class TrainingReportsViewModel : ObservableObject
{
    private static readonly string[] Units = ["1 Section", "2 Section", "3 Section"];
    private static readonly Regex TrainingTitleRx = new(
        @"\btraining\s+report\b", RegexOptions.IgnoreCase);

    private readonly AppConfig _config;
    private readonly Regex _platoonTitleRx;
    private readonly Regex _subunitRx;
    private List<ForumThread> _cachedReports = new();

    private sealed class TrainingReportsCache
    {
        public string ForumUrl { get; set; } = "";
        public DateTime SavedUtc { get; set; }
        public List<ForumThread> Reports { get; set; } = new();
    }

    /// <summary>Set by the view, using the app's authenticated forum session.</summary>
    public Func<string, Task<string>>? FetchHtml { get; set; }

    /// <summary>Set by the view for concurrent archive-page downloads.</summary>
    public Func<IReadOnlyList<string>, Task<IReadOnlyList<string>>>? FetchHtmlBatch { get; set; }

    public ObservableCollection<TrainingReportMonth> Months { get; } = new();

    [ObservableProperty] private TrainingReportMonth? selectedMonth;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    private bool isLoading;

    [ObservableProperty] private string? error;
    [ObservableProperty] private string statusMessage =
        "Open this page to scan the Training Reports archive.";

    public bool HasScanned { get; private set; }
    public bool HasData => SelectedMonth != null;

    private string CachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "4thIBTracker", $"training-reports-{_config.Platoon.Number}.json");

    public TrainingReportsViewModel(AppConfig config)
    {
        _config = config;
        var platoon = config.Platoon.Number;
        var platoonName = $@"\b{platoon}\s*(?:Platoon|Plt|Pl)\b";
        _platoonTitleRx = new Regex(platoonName, RegexOptions.IgnoreCase);
        _subunitRx = new Regex(
            platoonName + @"\s*(?:,|-)?\s*(?:(?<hq>HQ)\b|(?<n>[123])\s*(?:Section|Sec)\b)",
            RegexOptions.IgnoreCase);
        LoadCache();
    }

    private bool CanScan() => !IsLoading;

    [RelayCommand(CanExecute = nameof(CanScan))]
    public async Task ScanAsync()
    {
        if (FetchHtml is null || IsLoading) return;

        var forumUrl = _config.Forum.TrainingReportsForumUrl.Trim();
        if (forumUrl.Length == 0 || forumUrl.Contains("PASTE", StringComparison.OrdinalIgnoreCase))
        {
            Error = "Set the Training reports forum ID in Settings.";
            return;
        }

        IsLoading = true;
        Error = null;
        try
        {
            var knownAtStart = _cachedReports
                .Select(report => report.Url)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var reportsByUrl = _cachedReports.ToDictionary(
                report => report.Url, StringComparer.OrdinalIgnoreCase);
            var incremental = reportsByUrl.Count > 0;
            var encounteredCachedReport = false;
            var pagesScanned = 0;

            StatusMessage = incremental
                ? "Checking the newest Training Reports pages…"
                : "Opening the Training Reports archive…";
            var firstHtml = await FetchHtml(forumUrl);
            if (ForumCoursesService.LooksLoggedOut(firstHtml))
            {
                Error = "The forum isn't showing any training reports — log in on the " +
                        "4thIB Website tab, then scan again.";
                return;
            }

            var lastPage = ForumCoursesService.LastPage(firstHtml, forumUrl);
            if (KeepMatching(firstHtml) == 0)
                throw new InvalidOperationException(
                    "No training-report threads were recognised on archive page 1. " +
                    "The forum layout may have changed.");
            pagesScanned = 1;

            // A cached report means every following page is older. Parse the
            // complete page first, then stop before downloading the old archive.
            // Even when page 1 already contains a cached report, check the
            // first four pages. This tolerates an older thread being bumped by
            // a reply without allowing it to hide newer reports on page 2.
            var minimumIncrementalPages = Math.Min(4, lastPage);
            var pageBatchSize = incremental ? 3 : 12;
            for (var firstPage = 2;
                 firstPage <= lastPage &&
                 (!incremental || pagesScanned < minimumIncrementalPages ||
                  !encounteredCachedReport);
                 firstPage += pageBatchSize)
            {
                var pageNumbers = Enumerable.Range(
                    firstPage, Math.Min(pageBatchSize, lastPage - firstPage + 1)).ToList();
                StatusMessage = incremental
                    ? $"Checking recent archive pages {firstPage}–{pageNumbers[^1]}…"
                    : $"Building cache: pages {firstPage}–{pageNumbers[^1]} of {lastPage}…";
                var pages = await FetchManyAsync(pageNumbers
                    .Select(page => ForumCoursesService.PageUrl(forumUrl, page))
                    .ToList());
                for (var index = 0; index < pages.Count; index++)
                {
                    if (KeepMatching(pages[index]) == 0)
                        throw new InvalidOperationException(
                            $"No threads were recognised on archive page {pageNumbers[index]}. " +
                            "The scan was stopped rather than returning an incomplete history.");
                    pagesScanned = pageNumbers[index];
                }
            }

            _cachedReports = reportsByUrl.Values
                .OrderByDescending(report => report.Date ?? DateTime.MinValue)
                .ToList();
            PopulateMonths(_cachedReports);
            SaveCache(forumUrl);
            HasScanned = true;

            var undated = _cachedReports.Count(report => !report.Date.HasValue);
            var cacheNote = incremental
                ? $"checked {pagesScanned} newest archive page(s)"
                : $"{lastPage} archive page(s) cached";
            StatusMessage = $"{_cachedReports.Count} {_config.Platoon.Name} training report(s) " +
                            $"across {Months.Count} month(s) — {cacheNote} at {DateTime.Now:HH:mm}" +
                            (undated > 0 ? $" · {undated} report(s) have no recognised date." : ".");

            int KeepMatching(string html)
            {
                var threads = ForumCoursesService.ParseThreads(html, forumUrl);
                foreach (var thread in threads)
                {
                    if (!TrainingTitleRx.IsMatch(thread.Title) ||
                        !_platoonTitleRx.IsMatch(thread.Title) ||
                        SubunitOf(thread.Title) == "HQ")
                        continue;

                    if (knownAtStart.Contains(thread.Url))
                        encounteredCachedReport = true;
                    reportsByUrl[thread.Url] = thread;
                }
                return threads.Count;
            }
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    internal List<TrainingReportMonth> BuildMonths(IEnumerable<ForumThread> reports) =>
        reports
            .Where(report => report.Date.HasValue &&
                             TrainingTitleRx.IsMatch(report.Title) &&
                             _platoonTitleRx.IsMatch(report.Title) &&
                             SubunitOf(report.Title) != "HQ")
            .GroupBy(report => new DateTime(
                report.Date!.Value.Year, report.Date.Value.Month, 1))
            .OrderByDescending(group => group.Key)
            .Select(group => new TrainingReportMonth(group.Key, BuildNights(group)))
            .ToList();

    private void PopulateMonths(IEnumerable<ForumThread> reports)
    {
        var selected = SelectedMonth?.Month;
        var months = BuildMonths(reports);
        Months.Clear();
        foreach (var month in months) Months.Add(month);
        SelectedMonth = Months.FirstOrDefault(month => month.Month == selected) ??
                        Months.FirstOrDefault();
        OnPropertyChanged(nameof(HasData));
    }

    private List<PatrolNight> BuildNights(IEnumerable<ForumThread> reports)
    {
        var result = new List<PatrolNight>();
        foreach (var night in reports
                     .GroupBy(thread => thread.Date?.Date)
                     .OrderByDescending(group => group.Key ?? DateTime.MinValue))
        {
            var available = night.ToList();
            var used = new HashSet<ForumThread>();
            var slots = new List<PatrolSlot>();
            foreach (var unit in Units)
            {
                var match = available.FirstOrDefault(thread =>
                    !used.Contains(thread) && SubunitOf(thread.Title) == unit);
                if (match is not null) used.Add(match);
                slots.Add(new PatrolSlot(unit, match));
            }

            result.Add(new PatrolNight(
                night.Key?.ToString("dddd, dd MMMM yyyy") ?? "Date unknown",
                slots,
                available.Where(thread => !used.Contains(thread)).ToList()));
        }
        return result;
    }

    private void LoadCache()
    {
        try
        {
            if (!File.Exists(CachePath)) return;
            var cache = JsonSerializer.Deserialize<TrainingReportsCache>(
                File.ReadAllText(CachePath));
            var configuredUrl = _config.Forum.TrainingReportsForumUrl.Trim();
            if (cache is null || cache.Reports.Count == 0 ||
                !string.Equals(cache.ForumUrl, configuredUrl, StringComparison.OrdinalIgnoreCase))
                return;

            _cachedReports = cache.Reports
                .Where(report => TrainingTitleRx.IsMatch(report.Title) &&
                                 _platoonTitleRx.IsMatch(report.Title) &&
                                 SubunitOf(report.Title) != "HQ")
                .DistinctBy(report => report.Url, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(report => report.Date ?? DateTime.MinValue)
                .ToList();
            if (_cachedReports.Count == 0) return;

            PopulateMonths(_cachedReports);
            HasScanned = true;
            StatusMessage = $"Loaded {_cachedReports.Count} cached report(s) instantly · last checked " +
                            $"{cache.SavedUtc.ToLocalTime():dd MMM yyyy HH:mm}. " +
                            "Use refresh to check for newer reports.";
        }
        catch
        {
            // A stale or partial cache is disposable; the next scan rebuilds it.
            _cachedReports.Clear();
        }
    }

    private void SaveCache(string forumUrl)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
        var cache = new TrainingReportsCache
        {
            ForumUrl = forumUrl,
            SavedUtc = DateTime.UtcNow,
            Reports = _cachedReports,
        };
        File.WriteAllText(CachePath, JsonSerializer.Serialize(cache));
    }

    private async Task<IReadOnlyList<string>> FetchManyAsync(IReadOnlyList<string> urls)
    {
        if (urls.Count == 0) return Array.Empty<string>();
        if (FetchHtmlBatch is not null)
        {
            var pages = await FetchHtmlBatch(urls);
            if (pages.Count != urls.Count)
                throw new InvalidOperationException("The forum returned an incomplete page batch.");
            return pages;
        }

        var fallback = new List<string>(urls.Count);
        foreach (var url in urls) fallback.Add(await FetchHtml!(url));
        return fallback;
    }

    private string? SubunitOf(string title)
    {
        var match = _subunitRx.Match(title);
        if (!match.Success) return null;
        if (match.Groups["hq"].Success) return "HQ";
        return match.Groups["n"].Success
            ? $"{match.Groups["n"].Value} Section"
            : null;
    }

    [RelayCommand]
    private void Open(ForumThread? thread)
    {
        if (thread is null) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = thread.Url,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }
}
