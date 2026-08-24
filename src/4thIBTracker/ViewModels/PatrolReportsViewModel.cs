using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FourthIBTracker.Services;

namespace FourthIBTracker.ViewModels;

/// <summary>One of the four expected reports for a night.</summary>
public record PatrolSlot(string Unit, ForumThread? Report, bool? SignedOff = null)
{
    public bool IsSubmitted => Report != null;
    public string Detail => Report?.Author is { Length: > 0 } a ? $"by {a}" : "";
}

public record PatrolNight(string Title, List<PatrolSlot> Slots, List<ForumThread> Extras)
{
    public int SubmittedCount => Slots.Count(s => s.IsSubmitted);
    public string CountLabel => $"{SubmittedCount} / {Slots.Count} submitted";
    public bool Complete => SubmittedCount == Slots.Count;
    public bool HasExtras => Extras.Count > 0;
}

/// <summary>An entry in the operation picker. When <see cref="IsPatrolForum"/> is
/// false, Url points at the op's forum and its Patrol Reports subforum is
/// resolved on demand.</summary>
public record OperationOption(string Name, string Url, bool IsPatrolForum)
{
    public override string ToString() => Name;
}

public partial class PatrolReportsViewModel : ObservableObject
{
    private static readonly string[] Units = { "HQ", "1 Section", "2 Section", "3 Section" };

    private static readonly Regex ForumLinkRx = new(
        @"<a[^>]*href=""(?<url>[^""]*forum-\d+[^""]*\.html)""[^>]*>(?<text>[^<]+)</a>",
        RegexOptions.IgnoreCase);

    // Built per-platoon from settings:
    private readonly Regex PlTitleRx;
    private readonly Regex SubunitRx;

    private readonly AppConfig _config;

    /// <summary>Set by the view: fetches a URL through the embedded WebView2 (forum login reused).</summary>
    public Func<string, Task<string>>? FetchHtml { get; set; }

    /// <summary>
    /// Set by the view: fetches several forum pages concurrently using the same
    /// authenticated browser profile. Falls back to sequential WebView loads.
    /// </summary>
    public Func<IReadOnlyList<string>, Task<IReadOnlyList<string>>>? FetchHtmlBatch { get; set; }

    public ObservableCollection<PatrolNight> Nights { get; } = new();
    public ObservableCollection<OperationOption> Operations { get; } = new();

    [ObservableProperty] private OperationOption? selectedOperation;
    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private string? error;
    [ObservableProperty] private string statusMessage = "";

    public PatrolReportsViewModel(AppConfig config)
    {
        _config = config;
        int n = config.Platoon.Number;
        PlTitleRx = new Regex($@"\b{n}\s*platoon\b", RegexOptions.IgnoreCase);
        SubunitRx = new Regex(
            $@"{n}\s*platoon\s*(?<hq>hq)?\s*,?\s*(?:(?<n>[123])\s*section)?",
            RegexOptions.IgnoreCase);
    }

    public bool HasData => Nights.Count > 0;

    private static List<(string Url, string Text, int Pos)> ParseForumLinks(string html, string baseUrl)
    {
        var root = new Uri(baseUrl);
        var result = new List<(string, string, int)>();
        foreach (Match m in ForumLinkRx.Matches(html))
        {
            var text = System.Net.WebUtility.HtmlDecode(m.Groups["text"].Value).Trim();
            if (text.Length == 0) continue;
            result.Add((new Uri(root, m.Groups["url"].Value).ToString(), text, m.Index));
        }
        return result;
    }

    /// <summary>
    /// Reads the Operations section of the forum index: the current op's
    /// "Patrol Reports" subforum link, plus the archived operations list.
    /// </summary>
    private async Task EnsureOperationsAsync()
    {
        if (Operations.Count > 0) return;

        var idxUrl = _config.Forum.OperationsIndexUrl;
        if (string.IsNullOrWhiteSpace(idxUrl) || idxUrl.Contains("PASTE"))
        {
            // Fall back to a directly configured patrol reports forum.
            var direct = _config.Forum.PatrolReportsForumUrl;
            if (!string.IsNullOrWhiteSpace(direct) && !direct.Contains("PASTE"))
            {
                Operations.Add(new OperationOption("Configured forum", direct, true));
                SelectedOperation = Operations[0];
                return;
            }
            throw new InvalidOperationException(
                "Set Forum.OperationsIndexUrl (the forum page listing Operations) in Settings.");
        }

        StatusMessage = "Finding operations…";
        var html = await FetchHtml!(idxUrl);
        var links = ParseForumLinks(html, idxUrl);

        // Current op: the first "Patrol Reports" link; its op name is the last
        // "Operation …" link that appears before it.
        var patrol = links.FirstOrDefault(l => l.Text.Equals("Patrol Reports", StringComparison.OrdinalIgnoreCase));
        if (patrol.Url != null)
        {
            var opName = links.LastOrDefault(l =>
                l.Pos < patrol.Pos &&
                l.Text.StartsWith("Operation", StringComparison.OrdinalIgnoreCase)).Text ?? "Current operation";
            Operations.Add(new OperationOption($"{opName}  (current)", patrol.Url, true));
        }

        // Archived ops live as subforums of "Archives".
        var archives = links.FirstOrDefault(l => l.Text.Equals("Archives", StringComparison.OrdinalIgnoreCase));
        if (archives.Url != null)
        {
            var archHtml = await FetchHtml!(archives.Url);
            var seen = new HashSet<string>();
            foreach (var l in ParseForumLinks(archHtml, archives.Url))
                if (l.Text.StartsWith("Operation", StringComparison.OrdinalIgnoreCase) && seen.Add(l.Url))
                    Operations.Add(new OperationOption(l.Text, l.Url, false));
        }

        if (Operations.Count == 0)
            throw new InvalidOperationException(
                "Couldn't find any operations on that page — check Forum.OperationsIndexUrl in Settings.");
        SelectedOperation = Operations[0];
    }

    [RelayCommand]
    public async Task ScanAsync()
    {
        if (FetchHtml == null || IsLoading) return;
        IsLoading = true; Error = null;
        try
        {
            await EnsureOperationsAsync();
            var op = SelectedOperation ?? Operations[0];

            // Archived ops: resolve their Patrol Reports subforum first.
            string patrolUrl;
            if (op.IsPatrolForum) patrolUrl = op.Url;
            else
            {
                StatusMessage = $"Opening {op.Name}…";
                var opHtml = await FetchHtml(op.Url);
                var link = ParseForumLinks(opHtml, op.Url)
                    .FirstOrDefault(l => l.Text.Equals("Patrol Reports", StringComparison.OrdinalIgnoreCase));
                if (link.Url == null)
                {
                    Error = $"{op.Name} has no Patrol Reports subforum.";
                    return;
                }
                patrolUrl = link.Url;
            }

            // The per-op Patrol Reports subforum only holds that op's threads,
            // so scan it front to back (with a safety cap).
            const int maxPages = 40;
            var all = new List<ForumThread>();
            var seen = new HashSet<string>();
            int pages = 0;
            const int pageBatchSize = 4;
            bool reachedEnd = false;
            for (int firstPage = 1; firstPage <= maxPages && !reachedEnd;
                 firstPage += pageBatchSize)
            {
                var pageNumbers = Enumerable.Range(
                    firstPage, Math.Min(pageBatchSize, maxPages - firstPage + 1)).ToList();
                StatusMessage = pageNumbers.Count == 1
                    ? $"{op.Name} — scanning page {firstPage}…"
                    : $"{op.Name} — scanning pages {firstPage}–{pageNumbers[^1]}…";

                var htmlPages = await FetchManyAsync(pageNumbers
                    .Select(page => ForumCoursesService.PageUrl(patrolUrl, page)).ToList());
                for (int i = 0; i < pageNumbers.Count; i++)
                {
                    var page = pageNumbers[i];
                    var html = htmlPages[i];
                    if (page == 1 && ForumCoursesService.LooksLoggedOut(html))
                    {
                        Error = "The forum isn't showing any threads — log in on the " +
                                "4thIB Website tab, then scan again.";
                        return;
                    }
                    var threads = ForumCoursesService.ParseThreads(html, patrolUrl);
                    if (threads.Count == 0)
                    {
                        reachedEnd = true;
                        break;
                    }
                    pages = page;
                    int before = seen.Count;
                    foreach (var t in threads)
                        if (seen.Add(t.Url)) all.Add(t);
                    if (seen.Count == before)
                    {
                        reachedEnd = true; // page repeated — past the last page
                        break;
                    }
                }
            }

            var mine = all.Where(t => PlTitleRx.IsMatch(t.Title)).ToList();

            // Sign-off check: a report with zero replies can't contain the
            // sign-off comment, so only threads with replies are opened.
            var signedOff = new HashSet<string>();
            var phrase = _config.Platoon.SignOffPhrase;
            var toCheck = mine.Where(t => t.Replies is null or > 0).ToList();
            var checks = toCheck.Take(40).ToList();
            var tagRx = new Regex("<[^>]+>");
            if (checks.Count > 0)
            {
                StatusMessage = $"Checking {checks.Count} sign-off thread(s)…";
                var bodies = await FetchManyAsync(checks.Select(t => t.Url).ToList());
                for (int i = 0; i < checks.Count; i++)
                {
                    var body = tagRx.Replace(bodies[i], " ");
                    if (body.Contains(phrase, StringComparison.OrdinalIgnoreCase))
                        signedOff.Add(checks[i].Url);
                }
            }

            Nights.Clear();
            foreach (var night in mine
                         .GroupBy(t => t.Date?.Date)
                         .OrderByDescending(g => g.Key ?? DateTime.MinValue))
            {
                var reports = night.ToList();
                var slots = new List<PatrolSlot>();
                var used = new HashSet<ForumThread>();
                foreach (var unit in Units)
                {
                    var match = reports.FirstOrDefault(r => !used.Contains(r) && SubunitOf(r.Title) == unit);
                    if (match != null) used.Add(match);
                    slots.Add(new PatrolSlot(unit, match,
                        match == null ? null : signedOff.Contains(match.Url)));
                }
                var extras = reports.Where(r => !used.Contains(r)).ToList();

                Nights.Add(new PatrolNight(
                    night.Key?.ToString("dddd, dd MMMM yyyy") ?? "Date unknown",
                    slots, extras));
            }

            StatusMessage = $"{op.Name}: {mine.Count} {_config.Platoon.Name} report(s) across " +
                            $"{Nights.Count} night(s), {signedOff.Count} signed off — " +
                            $"{pages} page(s) scanned at {DateTime.Now:HH:mm}.";
        }
        catch (Exception ex) { Error = ex.Message; }
        finally { IsLoading = false; }
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
        foreach (var url in urls)
            fallback.Add(await FetchHtml!(url));
        return fallback;
    }

    private string? SubunitOf(string title)
    {
        var m = SubunitRx.Match(title);
        if (!m.Success) return null;
        if (m.Groups["hq"].Success) return "HQ";
        if (m.Groups["n"].Success) return $"{m.Groups["n"].Value} Section";
        return null;
    }

    [RelayCommand]
    private void Open(ForumThread? thread)
    {
        if (thread == null) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            { FileName = thread.Url, UseShellExecute = true });
        }
        catch (Exception ex) { Error = ex.Message; }
    }
}
