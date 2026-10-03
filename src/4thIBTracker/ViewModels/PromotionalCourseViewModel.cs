using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FourthIBTracker.Models;
using FourthIBTracker.Services;

namespace FourthIBTracker.ViewModels;

public partial class PromotionalCourseViewModel : ObservableObject
{
    private readonly GoogleSheetsService _sheets;
    private readonly AppConfig _config;

    public Func<string, Task<string>>? FetchHtml { get; set; }
    public Func<IReadOnlyList<string>, Task<IReadOnlyList<string>>>? FetchHtmlBatch { get; set; }
    public ObservableCollection<PromotionalCourseCandidate> Candidates { get; } = new();

    [ObservableProperty] private PromotionalCourseInfo? course;
    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private string statusMessage = "";
    [ObservableProperty] private string? error;
    [ObservableProperty] private bool hasLoaded;

    public PromotionalCourseViewModel(GoogleSheetsService sheets, AppConfig config)
    {
        _sheets = sheets;
        _config = config;
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (FetchHtml is null) return;
        IsLoading = true;
        Error = null;
        StatusMessage = "Finding the promotional-course forum…";
        Candidates.Clear();
        try
        {
            if (string.IsNullOrWhiteSpace(_config.Forum.UpcomingForumUrl))
                throw new InvalidOperationException(
                    "The upcoming courses forum is not configured in Settings.");

            var sourceHtml = await FetchHtml(_config.Forum.UpcomingForumUrl);
            if (ForumCoursesService.LooksLoggedOut(sourceHtml))
                throw new InvalidOperationException(
                    "The forum is logged out. Open the unit website tab, sign in, then refresh.");

            var forumUrl = PromotionalCourseService.FindPromotionalForumUrl(
                sourceHtml, _config.Forum.UpcomingForumUrl);
            if (forumUrl.Length == 0)
                throw new InvalidOperationException(
                    "The Promotional Courses forum was not found from the configured upcoming-courses page.");

            StatusMessage = "Reading the latest promotional course…";
            var forumHtml = await FetchHtml(forumUrl);
            var latest = PromotionalCourseService.FindLatestThread(forumHtml, forumUrl)
                ?? throw new InvalidOperationException(
                    "No promotional-course threads were recognised on the first forum page.");

            var firstPage = await FetchHtml(latest.Url);
            var lastPage = ForumLoaService.LastThreadPage(firstPage, latest.Url);
            var pages = new List<string> { firstPage };
            if (lastPage > 1)
            {
                var urls = Enumerable.Range(2, lastPage - 1)
                    .Select(page => ForumLoaService.ThreadPageUrl(latest.Url, page))
                    .ToList();
                var remaining = FetchHtmlBatch is null
                    ? await FetchSequentiallyAsync(urls)
                    : await FetchHtmlBatch(urls);
                pages.AddRange(remaining);
            }
            Course = PromotionalCourseService.ParseCourse(latest, pages);

            StatusMessage = "Checking signups against every BG course-tracker tab…";
            var sheet = _config.Sheet("SectionCourses");
            if (string.IsNullOrWhiteSpace(sheet.Id))
                throw new InvalidOperationException(
                    "The Section Courses spreadsheet is not configured in Settings.");

            var tabs = await _sheets.GetTabNamesAsync(sheet.Id);
            var valuesByTab = await _sheets.ReadTabsAsync(sheet.Id, tabs);
            var records = new List<CourseRecord>();
            foreach (var tab in tabs)
                if (valuesByTab.TryGetValue(tab, out var rows))
                    records.AddRange(SheetParsers.ParseCourseRosterTab(rows, tab));

            foreach (var candidate in PromotionalCourseService.CheckCandidates(Course, records))
                Candidates.Add(candidate);

            StatusMessage = $"{Course.Prerequisites.Count} prerequisite(s) · " +
                            $"{Candidates.Count} signup(s) · {tabs.Count} BG tracker tab(s) checked.";
            HasLoaded = true;
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

    private async Task<IReadOnlyList<string>> FetchSequentiallyAsync(IEnumerable<string> urls)
    {
        var pages = new List<string>();
        foreach (var url in urls) pages.Add(await FetchHtml!(url));
        return pages;
    }

    [RelayCommand]
    private void OpenCourse()
    {
        if (Course is not null) OpenUrl(Course.Url);
    }

    [RelayCommand]
    private void OpenSignup(PromotionalCourseCandidate candidate) =>
        OpenUrl(candidate.Signup.Url);

    private void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }
}
