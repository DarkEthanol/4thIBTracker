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
    private IReadOnlyList<CourseRecord> _courseRecords = [];
    private IReadOnlyDictionary<string, string> _orbatProfileLinks =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, IReadOnlyList<string>> _glossaryPrerequisites =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
    private int _trackerTabsChecked;
    private int _courseLoadVersion;
    private bool _suppressCourseSelection;
    private string _profileWarning = "";
    private string _glossaryWarning = "";

    public Func<string, Task<string>>? FetchHtml { get; set; }
    public Func<IReadOnlyList<string>, Task<IReadOnlyList<string>>>? FetchHtmlBatch { get; set; }
    public ObservableCollection<ForumThread> RecentCourses { get; } = new();
    public ObservableCollection<PromotionalCourseCandidate> Candidates { get; } = new();

    [ObservableProperty] private ForumThread? selectedCourseThread;
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

    partial void OnSelectedCourseThreadChanged(ForumThread? value)
    {
        if (!_suppressCourseSelection && HasLoaded && value is not null)
            _ = LoadCourseAsync(value);
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (FetchHtml is null) return;
        Interlocked.Increment(ref _courseLoadVersion);
        IsLoading = true;
        Error = null;
        HasLoaded = false;
        StatusMessage = "Finding recent promotional courses…";
        Course = null;
        Candidates.Clear();
        RecentCourses.Clear();
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

            var forumHtml = await FetchHtml(forumUrl);
            var recent = PromotionalCourseService.FindRecentThreads(forumHtml, forumUrl, 5);
            if (recent.Count == 0)
                throw new InvalidOperationException(
                    "No promotional-course threads were recognised on the first forum page.");

            foreach (var thread in recent) RecentCourses.Add(thread);

            StatusMessage = "Loading the BG course tracker and member profile links…";
            await LoadReferenceDataAsync();

            _suppressCourseSelection = true;
            SelectedCourseThread = RecentCourses[0];
            _suppressCourseSelection = false;
            HasLoaded = true;
            await LoadCourseAsync(SelectedCourseThread);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            _suppressCourseSelection = false;
            // Once reference data is ready, LoadCourseAsync owns this flag.
            // This avoids an overlapping dropdown selection hiding its spinner.
            if (!HasLoaded) IsLoading = false;
        }
    }

    private async Task LoadReferenceDataAsync()
    {
        var sheet = _config.Sheet("SectionCourses");
        if (string.IsNullOrWhiteSpace(sheet.Id))
            throw new InvalidOperationException(
                "The Section Courses spreadsheet is not configured in Settings.");

        var tabs = await _sheets.GetTabNamesAsync(sheet.Id);
        var linksTask = _sheets.ReadTabLinksAsync(sheet.Id, tabs);
        var profilesTask = LoadOrbatProfileLinksAsync();
        var glossaryTask = LoadCourseGlossaryAsync();
        await Task.WhenAll(linksTask, profilesTask, glossaryTask);

        var linksByTab = await linksTask;
        var records = new List<CourseRecord>();
        foreach (var tab in tabs)
        {
            if (!linksByTab.TryGetValue(tab, out var linkedCells)) continue;
            IList<IList<object>> rows = linkedCells
                .Select(row => (IList<object>)row
                    .Select(cell => (object)cell.Text)
                    .ToList())
                .ToList();
            records.AddRange(SheetParsers.ParseCourseRosterTab(rows, tab, linkedCells));
        }

        _courseRecords = records;
        _orbatProfileLinks = await profilesTask;
        _glossaryPrerequisites = await glossaryTask;
        _trackerTabsChecked = tabs.Count;
    }

    private async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>>
        LoadCourseGlossaryAsync()
    {
        _glossaryWarning = "";
        try
        {
            var glossaryUrl = _config.WebsiteUrl(PromotionalCourseService.CourseGlossaryPath);
            if (glossaryUrl.Length == 0)
                glossaryUrl = new Uri(new Uri(_config.Forum.UpcomingForumUrl),
                    PromotionalCourseService.CourseGlossaryPath).AbsoluteUri;
            var html = await FetchHtml!(glossaryUrl);
            if (ForumCoursesService.LooksLoggedOut(html))
                throw new InvalidOperationException("The forum is logged out.");
            var glossary = PromotionalCourseService.ParseGlossaryPrerequisites(html);
            if (glossary.Count == 0)
                throw new InvalidOperationException("No Course Glossary entries were recognised.");
            return glossary;
        }
        catch (Exception ex)
        {
            _glossaryWarning = $" Course Glossary unavailable ({ex.Message}); announcement prerequisites used.";
            return new Dictionary<string, IReadOnlyList<string>>(
                StringComparer.OrdinalIgnoreCase);
        }
    }

    private async Task<IReadOnlyDictionary<string, string>> LoadOrbatProfileLinksAsync()
    {
        _profileWarning = "";
        if (string.IsNullOrWhiteSpace(_config.OrbatUrl))
        {
            _profileWarning = " Website ORBAT is not configured, so missing tracker names cannot be linked.";
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            return await OrbatWebService.FetchProfileLinksAsync(_config.OrbatUrl);
        }
        catch
        {
            _profileWarning = " ORBAT profile links could not be loaded; tracker links are still available.";
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private async Task LoadCourseAsync(ForumThread thread)
    {
        if (FetchHtml is null) return;
        var loadVersion = Interlocked.Increment(ref _courseLoadVersion);
        IsLoading = true;
        Error = null;
        StatusMessage = $"Reading {thread.Title}…";
        try
        {
            var firstPage = await FetchHtml(thread.Url);
            var lastPage = ForumLoaService.LastThreadPage(firstPage, thread.Url);
            var pages = new List<string> { firstPage };
            if (lastPage > 1)
            {
                var urls = Enumerable.Range(2, lastPage - 1)
                    .Select(page => ForumLoaService.ThreadPageUrl(thread.Url, page))
                    .ToList();
                var remaining = FetchHtmlBatch is null
                    ? await FetchSequentiallyAsync(urls)
                    : await FetchHtmlBatch(urls);
                pages.AddRange(remaining);
            }

            var parsedCourse = PromotionalCourseService.ParseCourse(thread, pages);
            var usesGlossary = PromotionalCourseService.TryGetGlossaryPrerequisites(
                parsedCourse.Title, _glossaryPrerequisites, out var glossaryPrerequisites);
            if (usesGlossary)
                parsedCourse = parsedCourse with { Prerequisites = glossaryPrerequisites };
            var candidates = PromotionalCourseService.CheckCandidates(
                parsedCourse, _courseRecords, _orbatProfileLinks);
            if (loadVersion != _courseLoadVersion ||
                !string.Equals(SelectedCourseThread?.Url, thread.Url,
                    StringComparison.OrdinalIgnoreCase))
                return;

            Course = parsedCourse;
            Candidates.Clear();
            foreach (var candidate in candidates) Candidates.Add(candidate);
            StatusMessage = $"{parsedCourse.Prerequisites.Count} prerequisite(s) · " +
                            $"{Candidates.Count} signup(s) · {_trackerTabsChecked} BG tracker tab(s) checked." +
                            (usesGlossary
                                ? " Prerequisites from the Course Glossary."
                                : _glossaryWarning.Length > 0
                                    ? _glossaryWarning
                                    : " No matching Course Glossary entry; announcement prerequisites used.") +
                            _profileWarning;
        }
        catch (Exception ex)
        {
            if (loadVersion == _courseLoadVersion) Error = ex.Message;
        }
        finally
        {
            if (loadVersion == _courseLoadVersion) IsLoading = false;
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

    [RelayCommand]
    private void OpenProfile(PromotionalCourseCandidate candidate)
    {
        if (candidate.HasProfileUrl) OpenUrl(candidate.ProfileUrl);
    }

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
