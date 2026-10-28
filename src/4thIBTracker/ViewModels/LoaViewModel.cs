using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FourthIBTracker.Services;

namespace FourthIBTracker.ViewModels;

public partial class LoaViewModel : ObservableObject
{
    private readonly AppConfig _config;

    public Func<string, Task<string>>? FetchHtml { get; set; }
    public Func<IReadOnlyList<string>, Task<IReadOnlyList<string>>>? FetchHtmlBatch { get; set; }

    public ObservableCollection<LoaSectionGroup> Sections { get; } = new();

    [ObservableProperty] private DateTime? selectedDate;
    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private string? error;
    [ObservableProperty] private string statusMessage = "Choose an operation night to see the platoon's LOAs.";

    public bool HasScanned { get; private set; }

    public LoaViewModel(AppConfig config)
    {
        _config = config;
        selectedDate = NextOperationNight(DateTime.Today, config.Platoon.OperationDayOfWeek);
    }

    [RelayCommand]
    private void SelectNextOperationNight() => SelectedDate = NextOperationNight(
        DateTime.Today, _config.Platoon.OperationDayOfWeek);

    internal static DateTime NextOperationNight(DateTime from, DayOfWeek operationDay)
    {
        var daysAhead = ((int)operationDay - (int)from.DayOfWeek + 7) % 7;
        return from.Date.AddDays(daysAhead);
    }

    [RelayCommand]
    public async Task ScanAsync()
    {
        if (FetchHtml is null || IsLoading) return;
        var parentUrl = _config.Forum.PlatoonForumUrl.Trim();
        if (parentUrl.Length == 0 || parentUrl.Contains("PASTE", StringComparison.OrdinalIgnoreCase))
        {
            Error = "Set the Platoon LOA parent forum ID in Settings, then refresh.";
            return;
        }
        if (string.IsNullOrWhiteSpace(_config.OrbatUrl))
        {
            Error = "Set the Unit website in Settings, then refresh.";
            return;
        }

        var date = (SelectedDate ?? NextOperationNight(
            DateTime.Today, _config.Platoon.OperationDayOfWeek)).Date;
        IsLoading = true;
        Error = null;
        try
        {
            StatusMessage = "Finding the platoon's LOA forums…";
            var parentHtml = await FetchHtml(parentUrl);
            var forums = ForumLoaService.FindLoaSections(
                parentHtml, parentUrl, _config.Platoon.Number);
            if (forums.Count == 0)
                throw new InvalidOperationException(ForumCoursesService.LooksLoggedOut(parentHtml)
                    ? "The forum login session is not available. Open the 4thIB Website tab, log in, then refresh."
                    : "No section LOA forums were found beneath the configured platoon forum. " +
                      "Check the URL in Settings or refresh after the forum page has loaded.");

            StatusMessage = $"Reading {forums.Count} section forum(s)…";
            var forumPages = await FetchManyAsync(forums.Select(forum => forum.Url).ToList());
            var threads = forums.SelectMany((forum, index) =>
                    ForumLoaService.ParseThreads(forumPages[index], forum.Url, forum.Name))
                .ToList();

            // MyBB occasionally advertises additional forum pages that do not
            // exist (or redirect back to page one). Treat every page after the
            // first as optional and stop as soon as it contributes no new
            // personal threads. A bad pager must not break the whole LOA scan.
            foreach (var (forum, index) in forums.Select((forum, index) => (forum, index)))
            {
                var seen = threads
                    .Where(thread => thread.Section.Equals(
                        forum.Name, StringComparison.OrdinalIgnoreCase))
                    .Select(thread => thread.Url)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var lastPage = Math.Min(
                    ForumCoursesService.LastPage(forumPages[index], forum.Url), 25);
                for (var page = 2; page <= lastPage; page++)
                {
                    IReadOnlyList<LoaThread> pageThreads;
                    try
                    {
                        var html = await FetchHtml!(
                            ForumCoursesService.PageUrl(forum.Url, page));
                        pageThreads = ForumLoaService.ParseThreads(
                            html, forum.Url, forum.Name);
                    }
                    catch
                    {
                        break;
                    }

                    var additions = pageThreads
                        .Where(thread => seen.Add(thread.Url))
                        .ToList();
                    if (additions.Count == 0) break;
                    threads.AddRange(additions);
                }
            }

            threads = threads
                .GroupBy(thread => thread.Url, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
            if (threads.Count == 0)
                throw new InvalidOperationException(
                    "No personal LOA threads were found. The forum layout may have changed.");

            StatusMessage = $"Checking replies in {threads.Count} personal LOA thread(s)…";
            var lastPageUrls = threads.Select(thread => ForumLoaService.LastPostUrl(thread.Url)).ToList();
            var lastPagesTask = FetchManyAsync(lastPageUrls);
            var orbatTask = OrbatWebService.FetchPlatoonAsync(
                _config.OrbatUrl, _config.Platoon.Number);
            await Task.WhenAll(lastPagesTask, orbatTask);

            var posts = threads.SelectMany((thread, index) =>
                    ForumLoaService.ParsePosts(lastPagesTask.Result[index], thread))
                .ToList();
            var seenPosts = posts
                .Select(PostIdentity)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var advertisedOlderPages = threads.Select((thread, index) => Math.Max(0,
                    ForumLoaService.LastThreadPage(
                        lastPagesTask.Result[index], thread.Url) - 1))
                .Sum();
            if (advertisedOlderPages > 0)
            {
                StatusMessage = $"Checking up to {advertisedOlderPages + threads.Count} forum page(s) " +
                                "for the selected operation night…";
                foreach (var (thread, index) in threads.Select((thread, index) => (thread, index)))
                {
                    var lastThreadPage = Math.Min(
                        ForumLoaService.LastThreadPage(
                            lastPagesTask.Result[index], thread.Url), 25);
                    for (var page = 1; page < lastThreadPage; page++)
                    {
                        IReadOnlyList<LoaPost> pagePosts;
                        try
                        {
                            var html = await FetchHtml!(
                                ForumLoaService.ThreadPageUrl(thread.Url, page));
                            pagePosts = ForumLoaService.ParsePosts(html, thread);
                        }
                        catch
                        {
                            break;
                        }

                        var additions = pagePosts
                            .Where(post => seenPosts.Add(PostIdentity(post)))
                            .ToList();
                        if (additions.Count == 0) break;
                        posts.AddRange(additions);
                    }
                }
            }

            var groups = ForumLoaService.BuildRosterStatus(
                posts, threads, orbatTask.Result, date);

            Sections.Clear();
            foreach (var group in groups) Sections.Add(group);
            HasScanned = true;
            var loaCount = groups.Sum(group => group.LoaCount);
            var attendingCount = groups.Sum(group => group.AttendingCount);
            var missingThreads = groups.Sum(group => group.MissingThreadCount);
            StatusMessage = $"{loaCount} {(loaCount == 1 ? "LOA" : "LOAs")} and " +
                            $"{attendingCount} attending for {date:dddd, dd MMMM yyyy}" +
                            (missingThreads == 0
                                ? ""
                                : $" · {missingThreads} missing personal LOA thread(s)") +
                            $" · refreshed at {DateTime.Now:HH:mm}.";
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

    private static string PostIdentity(LoaPost post) =>
        $"{post.Url}\n{post.Date:yyyy-MM-dd}";

    [RelayCommand]
    private void Open(LoaMemberRow? entry)
    {
        if (entry is null) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = entry.Url,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }
}
