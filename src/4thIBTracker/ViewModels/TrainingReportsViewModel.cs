using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FourthIBTracker.Services;

namespace FourthIBTracker.ViewModels;

/// <summary>
/// Scans the unit-wide Training Reports archive and presents only the configured
/// platoon's PDT reports in the same section layout as Patrol Reports.
/// </summary>
public partial class TrainingReportsViewModel : ObservableObject
{
    private static readonly string[] Units = ["1 Section", "2 Section", "3 Section"];
    private static readonly Regex TrainingTitleRx = new(
        @"\btraining\s+report\b", RegexOptions.IgnoreCase);

    private readonly AppConfig _config;
    private readonly Regex _platoonTitleRx;
    private readonly Regex _subunitRx;

    /// <summary>Set by the view, using the app's authenticated forum session.</summary>
    public Func<string, Task<string>>? FetchHtml { get; set; }

    /// <summary>Set by the view for concurrent archive-page downloads.</summary>
    public Func<IReadOnlyList<string>, Task<IReadOnlyList<string>>>? FetchHtmlBatch { get; set; }

    public ObservableCollection<PatrolNight> Nights { get; } = new();

    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private string? error;
    [ObservableProperty] private string statusMessage = "Open this page to scan the Training Reports archive.";

    public bool HasScanned { get; private set; }

    public TrainingReportsViewModel(AppConfig config)
    {
        _config = config;
        var platoon = config.Platoon.Number;
        var platoonName = $@"\b{platoon}\s*(?:Platoon|Plt|Pl)\b";
        _platoonTitleRx = new Regex(platoonName, RegexOptions.IgnoreCase);
        _subunitRx = new Regex(
            platoonName + @"\s*(?:,|-)?\s*(?:(?<hq>HQ)\b|(?<n>[123])\s*(?:Section|Sec)\b)",
            RegexOptions.IgnoreCase);
    }

    [RelayCommand]
    public async Task ScanAsync()
    {
        if (FetchHtml is null || IsLoading) return;

        var forumUrl = _config.Forum.TrainingReportsForumUrl.Trim();
        if (forumUrl.Length == 0 || forumUrl.Contains("PASTE", StringComparison.OrdinalIgnoreCase))
        {
            Error = "Set Forum.TrainingReportsForumUrl in Settings.";
            return;
        }

        IsLoading = true;
        Error = null;
        try
        {
            StatusMessage = "Opening the Training Reports archive…";
            var firstHtml = await FetchHtml(forumUrl);
            if (ForumCoursesService.LooksLoggedOut(firstHtml))
            {
                Error = "The forum isn't showing any training reports — log in on the " +
                        "4thIB Website tab, then scan again.";
                return;
            }

            var lastPage = ForumCoursesService.LastPage(firstHtml, forumUrl);
            var matching = new List<ForumThread>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (KeepMatching(firstHtml) == 0)
                throw new InvalidOperationException(
                    "No training-report threads were recognised on archive page 1. " +
                    "The forum layout may have changed.");

            // The archive currently contains dozens of pages. Download bounded
            // batches through the cookie-backed HTTP path used by Patrol Reports;
            // the view retains its sequential WebView fallback for compatibility.
            const int pageBatchSize = 12;
            for (var firstPage = 2; firstPage <= lastPage; firstPage += pageBatchSize)
            {
                var pageNumbers = Enumerable.Range(
                    firstPage, Math.Min(pageBatchSize, lastPage - firstPage + 1)).ToList();
                StatusMessage = $"Scanning archive pages {firstPage}–{pageNumbers[^1]} of {lastPage}…";
                var pages = await FetchManyAsync(pageNumbers
                    .Select(page => ForumCoursesService.PageUrl(forumUrl, page))
                    .ToList());
                for (var index = 0; index < pages.Count; index++)
                    if (KeepMatching(pages[index]) == 0)
                        throw new InvalidOperationException(
                            $"No threads were recognised on archive page {pageNumbers[index]}. " +
                            "The scan was stopped rather than returning an incomplete history.");
            }

            var nights = BuildNights(matching);
            Nights.Clear();
            foreach (var night in nights) Nights.Add(night);
            HasScanned = true;

            StatusMessage = $"{matching.Count} {_config.Platoon.Name} training report(s) across " +
                            $"{Nights.Count} PDT date(s) — " +
                            $"{lastPage} archive page(s) scanned at {DateTime.Now:HH:mm}.";

            int KeepMatching(string html)
            {
                var threads = ForumCoursesService.ParseThreads(html, forumUrl);
                foreach (var thread in threads)
                {
                    if (!seen.Add(thread.Url)) continue;
                    if (TrainingTitleRx.IsMatch(thread.Title) &&
                        _platoonTitleRx.IsMatch(thread.Title) &&
                        SubunitOf(thread.Title) != "HQ")
                        matching.Add(thread);
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
