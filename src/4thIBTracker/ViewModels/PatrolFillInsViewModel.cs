using System.Collections.ObjectModel;
using System.Net;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FourthIBTracker.Services;

namespace FourthIBTracker.ViewModels;

public sealed record PatrolFillInMonthOption(DateTime Month)
{
    public string Label => Month.ToString("MMMM yyyy");
    public override string ToString() => Label;
}

public sealed record PatrolFillInPerson(
    string Name,
    string Origin,
    string Section,
    string ReportUrl)
{
    public string Detail => Origin.Length == 0
        ? $"Filled in with {Section} · origin not found on either ORBAT"
        : $"Filled in with {Section} · from {Origin}";
}

public sealed record PatrolFillInNight(
    DateTime Date,
    IReadOnlyList<PatrolFillInPerson> People)
{
    public string Title => Date.ToString("dddd, dd MMMM yyyy");
    public string CountLabel => $"{People.Count} fill-in{(People.Count == 1 ? "" : "s")}";
}

public partial class PatrolFillInsViewModel : ObservableObject
{
    private sealed record ForumLink(string Url, string Text, int Position);
    private sealed record PatrolForumSource(string Name, string Url, bool IsPatrolForum);
    private sealed record ForumMonthResult(
        IReadOnlyList<ForumThread> Matches,
        DateTime? Oldest,
        DateTime? Newest);

    private static readonly Regex ForumLinkRx = new(
        @"<a[^>]*href=['\""](?<url>[^'\""]*forum-\d+[^'\""]*\.html)['\""][^>]*>" +
        @"(?<text>.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex TagRx = new(@"<[^>]+>", RegexOptions.Singleline);

    private readonly AppConfig _config;
    private readonly GoogleSheetsService? _sheets;
    private Regex _platoonTitleRx = null!;
    private Regex _subunitRx = null!;
    private int _matcherPlatoonNumber;
    private bool _hasScanned;

    public Func<string, Task<string>>? FetchHtml { get; set; }
    public Func<IReadOnlyList<string>, Task<IReadOnlyList<string>>>? FetchHtmlBatch { get; set; }
    public Func<Task<IList<IList<object>>>>? LoadSutRows { get; set; }

    public ObservableCollection<PatrolFillInMonthOption> MonthOptions { get; } = new();
    public ObservableCollection<PatrolFillInNight> Nights { get; } = new();

    [ObservableProperty] private PatrolFillInMonthOption? selectedMonth;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    private bool isLoading;
    [ObservableProperty] private string? error;
    [ObservableProperty] private string statusMessage =
        "Choose a month to find fill-ins recorded in patrol reports.";

    public bool HasData => Nights.Count > 0;

    public PatrolFillInsViewModel(AppConfig config) : this(null, config)
    {
    }

    public PatrolFillInsViewModel(GoogleSheetsService? sheets, AppConfig config)
    {
        _sheets = sheets;
        _config = config;
        RefreshPlatoonMatchers();

        var currentMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        for (var offset = 0; offset < 24; offset++)
            MonthOptions.Add(new PatrolFillInMonthOption(currentMonth.AddMonths(-offset)));
        selectedMonth = MonthOptions[0];
    }

    partial void OnSelectedMonthChanged(PatrolFillInMonthOption? value)
    {
        if (_hasScanned && value is not null && !IsLoading && FetchHtml is not null)
            _ = ScanAsync();
    }

    private bool CanScan() => !IsLoading && SelectedMonth is not null;

    [RelayCommand(CanExecute = nameof(CanScan))]
    public async Task ScanAsync()
    {
        if (FetchHtml is null || SelectedMonth is null || IsLoading) return;

        IsLoading = true;
        Error = null;
        Nights.Clear();
        OnPropertyChanged(nameof(HasData));
        try
        {
            // Settings can be changed without restarting the application. Do
            // not retain the platoon identity that happened to be configured
            // when this page was first constructed.
            RefreshPlatoonMatchers();
            var monthStart = SelectedMonth.Month;
            var monthEnd = monthStart.AddMonths(1).AddTicks(-1);
            StatusMessage = $"Finding patrol reports for {monthStart:MMMM yyyy}…";

            var indexTask = DiscoverPatrolForumsAsync();
            var orbatTask = FetchHtml(_config.OrbatUrl);
            var sutRowsTask = LoadSutRowsAsync();
            await Task.WhenAll(indexTask, orbatTask, sutRowsTask);

            var orbatHtml = await orbatTask;
            var orbat = OrbatWebService.ParsePlatoonHtml(
                orbatHtml, _config.Platoon.Number);
            if (orbat.Values.All(members => members.Count == 0))
                throw new InvalidOperationException(
                    "The website ORBAT did not return any members for this platoon.");

            var sutRows = await sutRowsTask;
            var sheetSections = sutRows.Count == 0
                ? new Dictionary<string, List<string>>()
                : SheetParsers.ParsePlatoonSections(sutRows, _config.Platoon.Number);
            var sheetOrigins = sutRows.Count == 0
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : SheetParsers.ParseOrbatSoldiers(sutRows)
                    .GroupBy(entry => ForumLoaService.NormalizeName(entry.Key),
                        StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First().Value,
                        StringComparer.OrdinalIgnoreCase);
            var websiteOrigins = OrbatWebService.ParseUnitAssignmentsHtml(orbatHtml);

            var sectionMembers = orbat.ToDictionary(
                section => section.Key,
                section => section.Value
                    .Concat(sheetSections.GetValueOrDefault(section.Key, []))
                    .Select(ForumLoaService.NormalizeName)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);

            var reports = new List<ForumThread>();
            var sourcesChecked = 0;
            foreach (var source in await indexTask)
            {
                StatusMessage = $"Checking {source.Name} for {monthStart:MMMM yyyy}…";
                var patrolUrl = await ResolvePatrolForumAsync(source);
                if (patrolUrl.Length == 0) continue;

                var result = await ReadMonthAsync(patrolUrl, monthStart, monthEnd);
                sourcesChecked++;
                reports.AddRange(result.Matches);

                // Sources are newest-first. Once an operation is wholly older
                // than the requested month, every following archive is older.
                if (result.Newest is not null && result.Newest < monthStart) break;
            }

            reports = reports
                .Where(report => report.Date.HasValue &&
                                 _platoonTitleRx.IsMatch(report.Title) &&
                                 SubunitOf(report.Title) is not null)
                .DistinctBy(report => report.Url, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(report => report.Date)
                .ToList();

            var fillIns = new List<(DateTime Date, PatrolFillInPerson Person)>();
            var unrecognisedRosters = new List<string>();
            if (reports.Count > 0)
            {
                StatusMessage = $"Reading {reports.Count} {_config.Platoon.Name} patrol report(s)…";
                var pages = await FetchManyAsync(reports.Select(report => report.Url).ToList());
                for (var index = 0; index < reports.Count; index++)
                {
                    var report = reports[index];
                    var section = SubunitOf(report.Title)!;
                    var roster = PatrolFillInService.ParseRoster(pages[index]);
                    if (roster.Count == 0)
                    {
                        unrecognisedRosters.Add($"{report.Date:dd MMM} {section}");
                        continue;
                    }

                    var regulars = sectionMembers.GetValueOrDefault(section, []);
                    foreach (var member in roster)
                    {
                        if (FindMatchingName(member.NormalizedName, regulars) is not null)
                            continue;

                        var origin = "";
                        var websiteKey = FindMatchingName(
                            member.NormalizedName, websiteOrigins.Keys);
                        if (websiteKey is not null)
                            origin = websiteOrigins[websiteKey];
                        if (origin.Length == 0)
                        {
                            var sheetKey = FindMatchingName(
                                member.NormalizedName, sheetOrigins.Keys);
                            if (sheetKey is not null)
                                origin = sheetOrigins[sheetKey];
                        }

                        fillIns.Add((report.Date!.Value.Date, new PatrolFillInPerson(
                            member.Name, origin, section, report.Url)));
                    }
                }
            }

            foreach (var night in fillIns
                         .GroupBy(entry => entry.Date)
                         .OrderByDescending(group => group.Key))
            {
                Nights.Add(new PatrolFillInNight(
                    night.Key,
                    night.Select(entry => entry.Person)
                        .OrderBy(person => person.Section)
                        .ThenBy(person => ForumLoaService.NormalizeName(person.Name))
                        .ToList()));
            }

            _hasScanned = true;
            OnPropertyChanged(nameof(HasData));
            StatusMessage = $"{fillIns.Count} fill-in{(fillIns.Count == 1 ? "" : "s")} " +
                            $"across {Nights.Count} operation night{(Nights.Count == 1 ? "" : "s")} " +
                            $"in {monthStart:MMMM yyyy} · {reports.Count} platoon report(s) checked " +
                            $"across {sourcesChecked} operation forum{(sourcesChecked == 1 ? "" : "s")}. " +
                            "Compared with the current website and SuT ORBATs." +
                            (unrecognisedRosters.Count == 0
                                ? ""
                                : $" {unrecognisedRosters.Count} report roster(s) could not be read: " +
                                  string.Join(", ", unrecognisedRosters) + ".");
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

    private async Task<IList<IList<object>>> LoadSutRowsAsync()
    {
        if (LoadSutRows is not null) return await LoadSutRows();
        if (_sheets is null) return new List<IList<object>>();

        var sut = _config.Sheet("SutRecord");
        return await _sheets.ReadValuesFromConfiguredTabAsync(
            sut.Id, sut.Tab, "A1:AH120");
    }

    /// <summary>
    /// Patrol reports are hand typed. Accept a single-character surname typo
    /// only when the initial is identical; this catches Connor/Conner and
    /// Weyer/Weyner without merging unrelated soldiers who share a surname.
    /// </summary>
    private static string? FindMatchingName(
        string name, IEnumerable<string> candidates)
    {
        var exact = candidates.FirstOrDefault(candidate =>
            candidate.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;

        var wanted = NameParts(name);
        if (wanted is null) return null;
        foreach (var candidate in candidates)
        {
            var parts = NameParts(candidate);
            if (parts is null || !parts.Value.Initial.Equals(
                    wanted.Value.Initial, StringComparison.OrdinalIgnoreCase))
                continue;
            if (EditDistanceAtMostOne(parts.Value.Surname, wanted.Value.Surname))
                return candidate;
        }
        return null;

        static (string Initial, string Surname)? NameParts(string value)
        {
            var match = Regex.Match(value,
                @"^(?<initial>[\p{L}])\.\s*(?<surname>.+)$",
                RegexOptions.IgnoreCase);
            return match.Success
                ? (match.Groups["initial"].Value,
                    match.Groups["surname"].Value.Replace(" ", "").ToLowerInvariant())
                : null;
        }

        static bool EditDistanceAtMostOne(string left, string right)
        {
            if (left.Equals(right, StringComparison.OrdinalIgnoreCase)) return true;
            if (Math.Abs(left.Length - right.Length) > 1) return false;
            if (left.Length > right.Length) (left, right) = (right, left);

            var i = 0;
            var j = 0;
            var differences = 0;
            while (i < left.Length && j < right.Length)
            {
                if (left[i] == right[j])
                {
                    i++;
                    j++;
                    continue;
                }

                if (++differences > 1) return false;
                if (left.Length == right.Length) i++;
                j++;
            }
            return differences + (right.Length - j) <= 1;
        }
    }

    private async Task<List<PatrolForumSource>> DiscoverPatrolForumsAsync()
    {
        var result = new List<PatrolForumSource>();
        var indexUrl = _config.Forum.OperationsIndexUrl;
        if (!string.IsNullOrWhiteSpace(indexUrl))
        {
            var html = await FetchHtml!(indexUrl);
            if (ForumCoursesService.LooksLoggedOut(html))
                throw new InvalidOperationException(
                    "The forum isn't showing operations. Log in on the 4thIB Website tab, then try again.");

            var links = ParseForumLinks(html, indexUrl);
            var patrol = links.FirstOrDefault(link =>
                link.Text.Equals("Patrol Reports", StringComparison.OrdinalIgnoreCase));
            if (patrol is not null)
            {
                var operation = links.LastOrDefault(link =>
                    link.Position < patrol.Position &&
                    link.Text.StartsWith("Operation", StringComparison.OrdinalIgnoreCase));
                result.Add(new PatrolForumSource(
                    operation?.Text ?? "Current operation", patrol.Url, true));
            }

            var archives = links.FirstOrDefault(link =>
                link.Text.Equals("Archives", StringComparison.OrdinalIgnoreCase));
            if (archives is not null)
            {
                var archiveHtml = await FetchHtml!(archives.Url);
                foreach (var operation in ParseForumLinks(archiveHtml, archives.Url)
                             .Where(link => link.Text.StartsWith(
                                 "Operation", StringComparison.OrdinalIgnoreCase))
                             .DistinctBy(link => link.Url, StringComparer.OrdinalIgnoreCase))
                    result.Add(new PatrolForumSource(operation.Text, operation.Url, false));
            }
        }

        var direct = _config.Forum.PatrolReportsForumUrl;
        if (result.Count == 0 && !string.IsNullOrWhiteSpace(direct))
            result.Add(new PatrolForumSource("Configured patrol reports", direct, true));
        if (result.Count == 0)
            throw new InvalidOperationException(
                "No patrol-report forum could be found. Check the Unit website and forum settings.");
        return result;
    }

    private async Task<string> ResolvePatrolForumAsync(PatrolForumSource source)
    {
        if (source.IsPatrolForum) return source.Url;
        var html = await FetchHtml!(source.Url);
        return ParseForumLinks(html, source.Url)
                   .FirstOrDefault(link => link.Text.Equals(
                       "Patrol Reports", StringComparison.OrdinalIgnoreCase))?.Url ?? "";
    }

    private async Task<ForumMonthResult> ReadMonthAsync(
        string forumUrl, DateTime monthStart, DateTime monthEnd)
    {
        var firstHtml = await FetchHtml!(forumUrl);
        var firstThreads = ForumCoursesService.ParseThreads(firstHtml, forumUrl);
        if (firstThreads.Count == 0) return new ForumMonthResult([], null, null);

        var lastPage = Math.Min(40, ForumCoursesService.LastPage(firstHtml, forumUrl));
        var pages = new Dictionary<int, string> { [1] = firstHtml };
        if (lastPage > 1)
        {
            var lastHtml = await FetchHtml!(ForumCoursesService.PageUrl(forumUrl, lastPage));
            pages[lastPage] = lastHtml;
        }

        var edgeThreads = pages.Values
            .SelectMany(html => ForumCoursesService.ParseThreads(html, forumUrl))
            .ToList();
        var edgeDates = edgeThreads.Where(thread => thread.Date.HasValue)
            .Select(thread => thread.Date!.Value.Date)
            .ToList();
        var oldest = edgeDates.Count == 0 ? (DateTime?)null : edgeDates.Min();
        var newest = edgeDates.Count == 0 ? (DateTime?)null : edgeDates.Max();

        // The selected month is outside this operation's date range; avoid
        // downloading every middle page just to prove there are no matches.
        if (oldest is not null && newest is not null &&
            (monthEnd < oldest || monthStart > newest))
            return new ForumMonthResult([], oldest, newest);

        var missingPages = Enumerable.Range(2, Math.Max(0, lastPage - 2)).ToList();
        if (missingPages.Count > 0)
        {
            var htmlPages = await FetchManyAsync(missingPages
                .Select(page => ForumCoursesService.PageUrl(forumUrl, page)).ToList());
            for (var index = 0; index < missingPages.Count; index++)
                pages[missingPages[index]] = htmlPages[index];
        }

        var threads = pages.OrderBy(page => page.Key)
            .SelectMany(page => ForumCoursesService.ParseThreads(page.Value, forumUrl))
            .DistinctBy(thread => thread.Url, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var allDates = threads.Where(thread => thread.Date.HasValue)
            .Select(thread => thread.Date!.Value.Date).ToList();
        oldest = allDates.Count == 0 ? oldest : allDates.Min();
        newest = allDates.Count == 0 ? newest : allDates.Max();

        return new ForumMonthResult(
            threads.Where(thread => thread.Date >= monthStart && thread.Date <= monthEnd)
                .ToList(),
            oldest,
            newest);
    }

    private static List<ForumLink> ParseForumLinks(string html, string baseUrl)
    {
        var root = new Uri(baseUrl);
        var result = new List<ForumLink>();
        foreach (Match match in ForumLinkRx.Matches(html))
        {
            var text = WebUtility.HtmlDecode(
                TagRx.Replace(match.Groups["text"].Value, "")).Trim();
            if (text.Length == 0) continue;
            var href = WebUtility.HtmlDecode(match.Groups["url"].Value);
            result.Add(new ForumLink(new Uri(root, href).AbsoluteUri, text, match.Index));
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
        return match.Groups["n"].Success ? $"{match.Groups["n"].Value} Section" : null;
    }

    private void RefreshPlatoonMatchers()
    {
        var platoon = _config.Platoon.Number;
        if (_matcherPlatoonNumber == platoon && _platoonTitleRx is not null) return;

        _matcherPlatoonNumber = platoon;
        // Fire-support and other specialist platoons may include a qualifier
        // between their number and the word Platoon.
        var platoonName =
            $@"\b{platoon}\s*(?:\([^)]*\)\s*)?(?:Platoon|Plt|Pl)\b";
        _platoonTitleRx = new Regex(platoonName, RegexOptions.IgnoreCase);
        _subunitRx = new Regex(
            platoonName +
            @"\s*(?:,|-)?\s*(?:(?<hq>HQ)\b|(?<n>\d+)\s*(?:Section|Sec)\b)",
            RegexOptions.IgnoreCase);
    }

    [RelayCommand]
    private void Open(PatrolFillInPerson? person)
    {
        if (person is null) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = person.ReportUrl,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }
}
