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

    [ObservableProperty] private DateTime? selectedDate = DateTime.Today;
    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private string? error;
    [ObservableProperty] private string statusMessage = "Choose an operation night to see the platoon's LOAs.";

    public bool HasScanned { get; private set; }

    public LoaViewModel(AppConfig config) => _config = config;

    [RelayCommand]
    private void SelectToday() => SelectedDate = DateTime.Today;

    [RelayCommand]
    public async Task ScanAsync()
    {
        if (FetchHtml is null || IsLoading) return;
        var parentUrl = _config.Forum.PlatoonForumUrl.Trim();
        if (parentUrl.Length == 0 || parentUrl.Contains("PASTE", StringComparison.OrdinalIgnoreCase))
        {
            Error = "Set the Platoon forum URL in Settings, then refresh.";
            return;
        }
        if (string.IsNullOrWhiteSpace(_config.OrbatUrl))
        {
            Error = "Set the Website ORBAT URL in Settings, then refresh.";
            return;
        }

        var date = (SelectedDate ?? DateTime.Today).Date;
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
                .GroupBy(thread => thread.Url, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
            if (threads.Count == 0)
                throw new InvalidOperationException(
                    "No personal LOA threads were found. The forum layout may have changed.");

            StatusMessage = $"Checking the latest replies in {threads.Count} LOA thread(s)…";
            var pageUrls = threads.Select(thread => ForumLoaService.LastPostUrl(thread.Url)).ToList();
            var threadPagesTask = FetchManyAsync(pageUrls);
            var orbatTask = OrbatWebService.FetchPlatoonAsync(
                _config.OrbatUrl, _config.Platoon.Number);
            await Task.WhenAll(threadPagesTask, orbatTask);

            var posts = threads.SelectMany((thread, index) =>
                    ForumLoaService.ParsePosts(threadPagesTask.Result[index], thread.Url))
                .ToList();
            var groups = ForumLoaService.MatchToOrbat(posts, orbatTask.Result, date);

            Sections.Clear();
            foreach (var group in groups) Sections.Add(group);
            HasScanned = true;
            var count = groups.Sum(group => group.Entries.Count);
            StatusMessage = $"{count} {(count == 1 ? "LOA" : "LOAs")} for " +
                            $"{date:dddd, dd MMMM yyyy} · refreshed at {DateTime.Now:HH:mm}.";
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

    [RelayCommand]
    private void Open(LoaEntry? entry)
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
