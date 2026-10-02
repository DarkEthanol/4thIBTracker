using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FourthIBTracker.Models;
using FourthIBTracker.Services;
using GRequest = Google.Apis.Sheets.v4.Data.Request;

namespace FourthIBTracker.ViewModels;

// ===================================================================== Dashboard
public enum DashboardNcoCourseStatus { Completed, Upcoming, NotScheduled }

public sealed record DashboardNcoCourse(
    string Position,
    string Section,
    string NcoNames,
    DashboardNcoCourseStatus Status)
{
    public bool IsCompleted => Status == DashboardNcoCourseStatus.Completed;
    public bool IsUpcoming => Status == DashboardNcoCourseStatus.Upcoming;
    public string StatusText => Status switch
    {
        DashboardNcoCourseStatus.Completed => "✓ Completed",
        DashboardNcoCourseStatus.Upcoming => "~ Upcoming",
        _ => "✗ Not scheduled",
    };
}

public partial class DashboardViewModel : ObservableObject
{
    private readonly GoogleSheetsService _sheets;
    private readonly AppConfig _config;

    // The NCO positions whose monthly tracker entries the dashboard watches (from settings).
    private IEnumerable<string> NcoPositions => _config.Platoon.NcoTrackerPositions;

    public ObservableCollection<Disciplinary> ActiveDisciplinaries { get; } = new();
    public ObservableCollection<CourseGapItem> CourseGaps { get; } = new();
    public ObservableCollection<SectionRoster> Sections { get; } = new();
    public ObservableCollection<SheetParsers.LogiOrderItem> LogiOrder { get; } = new();
    public ObservableCollection<DashboardNcoCourse> NcoChecks { get; } = new();
    public ObservableCollection<OrbatWebService.OrbatMismatch> OrbatMismatches { get; } = new();
    public ObservableCollection<TransferItem> PendingTransfers { get; } = new();
    public ObservableCollection<TransferItem> CompletedTransfers { get; } = new();

    [ObservableProperty] private bool orbatInSync;
    [ObservableProperty] private string transferStatus = "Loading with the dashboard…";

    /// <summary>Set by the view: fetches authenticated forum HTML using the shared browser session.</summary>
    public Func<string, Task<string>>? FetchHtml { get; set; }

    private HashSet<string> _platoonMembers = new();

    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private string? error;
    [ObservableProperty] private string ncoMonthTitle = "";
    [ObservableProperty] private string ncoStatus = "";

    public DashboardViewModel(GoogleSheetsService sheets, AppConfig config)
    { _sheets = sheets; _config = config; }

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsLoading = true; Error = null;
        var errors = new List<string>();
        var sut = _config.Sheet("SutRecord");
        var sutRowsTask = _sheets.ReadValuesFromConfiguredTabAsync(
            sut.Id, sut.Tab, "A1:AH120");

        // The cards use independent sheets/sites. Start them together so the
        // dashboard waits for the slowest source rather than the sum of every source.
        // Each card still catches its own error, so one failure cannot blank the page.
        var loads = new[]
        {
            Load("Discipline", async () =>
            {
                var disc = _config.Sheet("Discipline");
                var rows = await _sheets.ReadValuesAsync(disc.Id, $"'{disc.Tab}'!A2:G200");
                ActiveDisciplinaries.Clear();
                foreach (var d in SheetParsers.ParseDisciplinaries(rows).Where(d => d.IsActive))
                    ActiveDisciplinaries.Add(d);
            }),

            Load("Course gaps", async () =>
            {
                // Counted directly from the Section Courses matrix.
                var sc = _config.Sheet("SectionCourses");
                var rows = await _sheets.ReadValuesFromFirstTabAsync(sc.Id,
                    new[] { _config.Platoon.Name, _config.Platoon.ShortName, sc.Tab });
                var (records, courseNames) = SheetParsers.ParseCourses(rows, _config.Platoon.Number);

                CourseGaps.Clear();
                foreach (var course in courseNames)
                {
                    if (_config.Platoon.ExcludesOutstandingCourse(course)) continue;

                    int outstanding = records.Count(r =>
                        !r.Courses.TryGetValue(course, out var v) ||
                        (!v.Equals("Complete", StringComparison.OrdinalIgnoreCase) &&
                         !v.Equals("Advanced", StringComparison.OrdinalIgnoreCase)));
                    if (outstanding > 0)
                        CourseGaps.Add(new CourseGapItem(course, outstanding));
                }
                var sorted = CourseGaps.OrderByDescending(g => g.Outstanding).ToList();
                CourseGaps.Clear();
                foreach (var g in sorted) CourseGaps.Add(g);
            }),

            Load("ORBAT", async () =>
            {
                var att = _config.Sheet("Attendance");
                var rows = await _sheets.ReadValuesAsync(att.Id, $"'{att.Tab}'!A1:W25");
                Sections.Clear();
                foreach (var g in SheetParsers.ParseAttendance(rows).GroupBy(r => r.SectionName))
                    Sections.Add(new SectionRoster(g.Key,
                        new ObservableCollection<string>(g.Select(r => r.SoldierName))));
            }),

            Load("Logistics", async () =>
            {
                var logi = _config.Sheet("Logistics");
                var tab = await _sheets.ResolveTabAsync(logi.Id, logi.Tab);
                // Read the used portion of a broad column range. The Values API
                // omits trailing empty rows, so this grows with the catalogue
                // without downloading the sheet's entire allocated grid.
                var rows = await _sheets.ReadValuesAsync(logi.Id, $"'{tab}'!A:Z");
                LogiOrder.Clear();
                foreach (var item in SheetParsers.ParseLogiOverview(rows))
                    LogiOrder.Add(item);
            }),

            Load("NCO tracker", async () =>
            {
                await LoadNcoCoursesAsync(await sutRowsTask);
            }),

            Load("ORBAT sync", async () =>
            {
                // The website does not depend on the sheet metadata/value read,
                // so let it download while the Google requests are in flight.
                var webTask = OrbatWebService.FetchPlatoonAsync(
                    _config.OrbatUrl, _config.Platoon.Number);
                await Task.WhenAll(sutRowsTask, webTask);

                var sheetSections = SheetParsers.ParsePlatoonSections(
                    await sutRowsTask, _config.Platoon.Number);
                _platoonMembers = sheetSections.SelectMany(kv => kv.Value)
                    .Select(NormName).ToHashSet();
                var webSections = await webTask;

                OrbatMismatches.Clear();
                foreach (var m in OrbatWebService.Compare(webSections, sheetSections))
                    OrbatMismatches.Add(m);
                OrbatInSync = OrbatMismatches.Count == 0;
            }),

            Load("Transfers", async () =>
            {
                try { await LoadTransfersAsync(await sutRowsTask); }
                catch (Exception ex)
                {
                    TransferStatus = $"Transfers unavailable: {ex.Message}";
                    throw;
                }
            }),
        };

        await Task.WhenAll(loads);

        Error = errors.Count > 0 ? string.Join("  •  ", errors) : null;
        IsLoading = false;
        return;

        async Task Load(string what, Func<Task> action)
        {
            try { await action(); }
            catch (Exception ex)
            {
                lock (errors) errors.Add($"{what}: {ex.Message}");
            }
        }
    }

    // ---------------- NCO course readiness ----------------
    private async Task LoadNcoCoursesAsync(IList<IList<object>> sutRows)
    {
        var today = DateTime.Today;
        var monthStart = new DateTime(today.Year, today.Month, 1);
        var nco = _config.Sheet("NcoTracker");

        // Year tab: prefer the current year, fall back to whatever is configured.
        string tab;
        try { tab = await _sheets.ResolveTabAsync(nco.Id, today.Year.ToString()); }
        catch { tab = await _sheets.ResolveTabAsync(nco.Id, nco.Tab); }

        var trackerRows = await _sheets.ReadValuesAsync(nco.Id, $"'{tab}'!A1:M60");
        var trackerByPosition = SheetParsers.ParseNcoMonth(
                trackerRows, NcoPositions, today.Month)
            .ToDictionary(item => item.Position, StringComparer.OrdinalIgnoreCase);

        var platoonSections = SheetParsers.ParsePlatoonSections(
            sutRows, _config.Platoon.Number);
        string[] sectionOrder = ["1 Section", "2 Section", "3 Section"];

        List<ForumThread> completed = [];
        List<ForumThread> upcoming = [];
        string forumStatus;
        if (FetchHtml is null)
        {
            forumStatus = "course forums unavailable";
        }
        else
        {
            try
            {
                var completedTask = ScanNcoCourseForumAsync(
                    _config.Forum.CoursesForumUrl,
                    Math.Max(1, _config.Forum.MaxPages),
                    thread => thread.Date is null ||
                              (thread.Date.Value.Year == today.Year &&
                               thread.Date.Value.Month == today.Month),
                    monthStart);
                var upcomingTask = ScanNcoCourseForumAsync(
                    _config.Forum.UpcomingForumUrl,
                    Math.Min(3, Math.Max(1, _config.Forum.MaxPages)),
                    thread => thread.Date is null || thread.Date.Value.Date >= monthStart,
                    stopBefore: null);
                await Task.WhenAll(completedTask, upcomingTask);
                completed = await completedTask;
                upcoming = await upcomingTask;
                forumStatus = "sheet + course forums";
            }
            catch (Exception ex)
            {
                // The sheet remains useful when the website is logged out or down.
                forumStatus = $"sheet loaded; forum schedule unavailable: {ex.Message}";
            }
        }

        NcoChecks.Clear();
        var positions = NcoPositions.ToList();
        for (var index = 0; index < positions.Count; index++)
        {
            var position = positions[index];
            var section = index < sectionOrder.Length ? sectionOrder[index] : "Section";
            var names = platoonSections.TryGetValue(section, out var members)
                ? members.Take(2).ToList()
                : [];
            var sheetDone = trackerByPosition.TryGetValue(position, out var check) && check.Done;
            var forumCompleted = completed.Any(thread => AuthorMatches(thread.Author, names));
            var forumUpcoming = upcoming.Any(thread => AuthorMatches(thread.Author, names));
            var status = ResolveNcoCourseStatus(sheetDone, forumCompleted, forumUpcoming);

            NcoChecks.Add(new DashboardNcoCourse(
                position,
                section,
                names.Count > 0 ? string.Join(" · ", names) : "IC / 2IC not found",
                status));
        }

        NcoMonthTitle = $"NCO COURSES — {today:MMMM}".ToUpperInvariant();
        NcoStatus = $"{forumStatus} · refreshed {DateTime.Now:HH:mm}";
    }

    internal static DashboardNcoCourseStatus ResolveNcoCourseStatus(
        bool sheetDone, bool forumCompleted, bool forumUpcoming) =>
        sheetDone || forumCompleted
            ? DashboardNcoCourseStatus.Completed
            : forumUpcoming
                ? DashboardNcoCourseStatus.Upcoming
                : DashboardNcoCourseStatus.NotScheduled;

    private static bool AuthorMatches(string author, IEnumerable<string> names) =>
        !string.IsNullOrWhiteSpace(author) && names.Any(name =>
            author.Contains(name, StringComparison.OrdinalIgnoreCase) ||
            name.Contains(author, StringComparison.OrdinalIgnoreCase) ||
            author.Replace(".", "").Contains(
                name.Replace(".", ""), StringComparison.OrdinalIgnoreCase));

    private async Task<List<ForumThread>> ScanNcoCourseForumAsync(
        string baseUrl, int maxPages, Func<ForumThread, bool> filter, DateTime? stopBefore)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) ||
            baseUrl.Contains("PASTE", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("NCO course forum URL is not configured.");

        var results = new List<ForumThread>();
        var seen = new HashSet<string>();
        var passedCutoff = false;

        for (var page = 1; page <= maxPages; page++)
        {
            var html = await FetchHtml!(ForumCoursesService.PageUrl(baseUrl, page));
            if (page == 1 && ForumCoursesService.LooksLoggedOut(html))
                throw new InvalidOperationException("log into the 4thIB website to read course dates");

            var threads = ForumCoursesService.ParseThreads(html, baseUrl);
            if (threads.Count == 0)
            {
                if (page == 1)
                    throw new InvalidOperationException("no course threads were recognised");
                break;
            }

            foreach (var thread in threads)
                if (seen.Add(thread.Url) && filter(thread)) results.Add(thread);

            if (stopBefore is not null)
            {
                var dated = threads.Where(thread => thread.Date is not null).ToList();
                if (dated.Count > 0 && dated.All(thread => thread.Date < stopBefore))
                {
                    if (passedCutoff) break;
                    passedCutoff = true;
                }
            }
        }

        return results;
    }

    // ---------------- transfers (loaded as a normal dashboard module) ----------------
    private static string NormName(string n) =>
        System.Text.RegularExpressions.Regex.Replace(n, @"\s+", " ").Trim().ToLowerInvariant();

    private static readonly System.Text.RegularExpressions.Regex TitleNameRx =
        new(@"([A-Z]\.\s*[A-Za-z'\-]+)\s*\.{0,3}\s*$");
    // Thread pages contain platoon names in site navigation/forum-jump lists, so a
    // whole-page match is useless. Only the labelled fields in the request
    // template count: where they're going, and where they currently are.
    private static readonly System.Text.RegularExpressions.Regex TransferFieldRx = new(
        @"(desired\s+transfer\s+location|current\s+section\s*/?\s*detachment|transfer(?:ring)?\s+to)\s*:?\s*(?<v>.{0,50})",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    // Fuzzy: the request fields are free text, so accept full/short platoon names
    // and section refs however they're punctuated. Word boundaries keep dates and
    // unrelated squadron numbers from matching.
    private System.Text.RegularExpressions.Regex PlatoonMentionRx => _platoonMentionRx ??=
        new($@"\b{_config.Platoon.Number}(st|nd|rd|th)?\s*(platoon|plt?)\b" +
            $@"|\b{_config.Platoon.Number}\s*[-–—/\.]\s*[123]\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private System.Text.RegularExpressions.Regex? _platoonMentionRx;
    private static readonly System.Text.RegularExpressions.Regex HtmlTagRx = new(@"<[^>]+>");

    private static readonly System.Text.RegularExpressions.Regex AwaitingApprovalRx =
        new(@"awaiting\s+approval", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// Primary check: the admins' "Awaiting Approval:" block lists exactly which
    /// units must sign the transfer off (e.g. "4 Pl [ X ] @Platoon Commander") —
    /// if the configured platoon appears there, the transfer involves us.
    /// Fallback (thread not yet processed by admins): the requester's own
    /// desired/current section fields.
    /// </summary>
    private string? RequestInvolvesPlatoon(string pageText)
    {
        bool sawBlock = false;
        foreach (System.Text.RegularExpressions.Match m in AwaitingApprovalRx.Matches(pageText))
        {
            sawBlock = true;
            var segment = pageText.Substring(m.Index,
                Math.Min(320, pageText.Length - m.Index));
            if (PlatoonMentionRx.IsMatch(segment))
                return $"{_config.Platoon.ShortName} sign-off";
        }
        if (sawBlock) return null; // admins listed the approvers and we're not one of them

        foreach (System.Text.RegularExpressions.Match m in TransferFieldRx.Matches(pageText))
            if (PlatoonMentionRx.IsMatch(m.Groups["v"].Value))
                return $"involves {_config.Platoon.Name}";
        return null;
    }

    private async Task LoadTransfersAsync(IList<IList<object>> sutRows)
    {
        if (FetchHtml is null)
        {
            TransferStatus = "Forum module unavailable.";
            return;
        }

        _platoonMembers = SheetParsers.ParsePlatoonSections(
                sutRows, _config.Platoon.Number)
            .SelectMany(kv => kv.Value)
            .Select(NormName)
            .ToHashSet();

        TransferStatus = "Loading transfers…";
        var pendingTask = ScanForumsAsync(_config.Forum.PendingTransferForums);
        var completedTask = ScanForumsAsync(_config.Forum.CompletedTransferForums);
        await Task.WhenAll(pendingTask, completedTask);

        PendingTransfers.Clear();
        foreach (var transfer in await pendingTask) PendingTransfers.Add(transfer);
        CompletedTransfers.Clear();
        foreach (var transfer in await completedTask) CompletedTransfers.Add(transfer);

        TransferStatus = $"Updated {DateTime.Now:HH:mm} · " +
                         $"{PendingTransfers.Count} pending, {CompletedTransfers.Count} completed";
    }

    private async Task<List<TransferItem>> ScanForumsAsync(IEnumerable<string> forumUrls)
    {
        var found = new List<TransferItem>();
        foreach (var url in forumUrls)
        {
            var html = await FetchHtml!(url);
            var threads = ForumCoursesService.ParseThreads(html, url).Take(10).ToList();

            int bodyBudget = 6; // opening thread pages is slow — cap it per forum
            foreach (var t in threads)
            {
                var nameMatch = TitleNameRx.Match(t.Title);
                if (nameMatch.Success && _platoonMembers.Contains(NormName(nameMatch.Groups[1].Value)))
                {
                    found.Add(new TransferItem(t.Title, t.Url, $"{_config.Platoon.ShortName} member", t.Date));
                    continue;
                }
                if (bodyBudget-- <= 0) continue;
                var body = HtmlTagRx.Replace(await FetchHtml!(t.Url), " ");
                if (RequestInvolvesPlatoon(body) is { } tag)
                    found.Add(new TransferItem(t.Title, t.Url, tag, t.Date));
            }
        }
        return found
            .GroupBy(t => t.Url).Select(g => g.First())
            .OrderByDescending(t => t.Date ?? DateTime.MinValue)
            .Take(5).ToList();
    }

    [RelayCommand]
    private void OpenTransfer(TransferItem item)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            { FileName = item.Url, UseShellExecute = true });
        }
        catch { /* browser launch failure isn't fatal */ }
    }
}

public record TransferItem(string Title, string Url, string Tag, DateTime? Date);

public record CourseGapItem(string Course, int Outstanding);
public record SectionRoster(string Name, ObservableCollection<string> Soldiers);

// ===================================================================== Attendance
public partial class AttendanceCellViewModel : ObservableObject
{
    /// <summary>All statuses, used by the palette at the bottom of the view.</summary>
    public static AttendanceStatus[] StatusOptions { get; } = Enum.GetValues<AttendanceStatus>();

    public int SheetRow { get; }
    public int Col0 { get; }
    private readonly Action _onChanged;
    private readonly Func<AttendanceStatus> _selectedStatus;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Brush))]
    [NotifyPropertyChangedFor(nameof(Foreground))]
    private AttendanceStatus status;

    public bool IsDirty { get; set; }

    public Brush Brush => new SolidColorBrush(Status.ToColor());
    public Brush Foreground => Status == AttendanceStatus.Late
        ? new SolidColorBrush(Color.FromRgb(0x16, 0x18, 0x1A))
        : Brushes.White;

    public AttendanceCellViewModel(
        int sheetRow, int col0, AttendanceStatus initial,
        Func<AttendanceStatus> selectedStatus, Action onChanged)
    {
        SheetRow = sheetRow; Col0 = col0; status = initial;
        _selectedStatus = selectedStatus; _onChanged = onChanged;
    }

    /// <summary>Paint this cell with the currently selected palette status.</summary>
    [RelayCommand]
    private void Apply() => Status = _selectedStatus();

    /// <summary>Right-click: clear the cell.</summary>
    [RelayCommand]
    private void Clear() => Status = AttendanceStatus.None;

    // Fires only on an actual change (constructor writes the backing field directly).
    partial void OnStatusChanged(AttendanceStatus value)
    {
        IsDirty = true;
        _onChanged();
    }
}

public class AttendanceRowViewModel
{
    public string Name { get; init; } = "";
    public List<AttendanceCellViewModel> Cells { get; init; } = new();
}

public record AttendanceSectionViewModel(
    string Name, ObservableCollection<AttendanceRowViewModel> Rows);

public partial class AttendanceViewModel : ObservableObject
{
    private readonly GoogleSheetsService _sheets;
    private readonly AppConfig _config;

    public ObservableCollection<AttendanceSectionViewModel> Sections { get; } = new();

    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private string? error;
    [ObservableProperty] private int dirtyCount;
    [ObservableProperty] private string statusMessage = "";
    [ObservableProperty] private AttendanceStatus selectedStatus = AttendanceStatus.Present;

    public AttendanceViewModel(GoogleSheetsService sheets, AppConfig config)
    { _sheets = sheets; _config = config; }

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsLoading = true; Error = null; StatusMessage = "";
        try
        {
            var att = _config.Sheet("Attendance");
            var values = await _sheets.ReadValuesFromConfiguredTabAsync(
                att.Id, att.Tab, "A1:W25");
            var parsed = SheetParsers.ParseAttendance(values);

            Sections.Clear();
            DirtyCount = 0;
            foreach (var block in SheetParsers.AttendanceBlocks)
            {
                var section = new AttendanceSectionViewModel(block.Name, new ObservableCollection<AttendanceRowViewModel>());
                foreach (var r in parsed.Where(p => p.SectionName == block.Name))
                {
                    var vm = new AttendanceRowViewModel { Name = r.SoldierName };
                    for (int w = 0; w < 5; w++)
                        vm.Cells.Add(new AttendanceCellViewModel(
                            r.SheetRow, block.FirstWeekCol0 + w, r.Weeks[w],
                            () => SelectedStatus,
                            () => DirtyCount = CountDirty()));
                    section.Rows.Add(vm);
                }
                if (section.Rows.Count > 0) Sections.Add(section);
            }
        }
        catch (Exception ex) { Error = ex.Message; }
        finally { IsLoading = false; }
    }

    private IEnumerable<AttendanceCellViewModel> AllCells() =>
        Sections.SelectMany(s => s.Rows).SelectMany(r => r.Cells);

    private int CountDirty() => AllCells().Count(c => c.IsDirty);

    [RelayCommand]
    public async Task SaveAsync()
    {
        var dirty = AllCells().Where(c => c.IsDirty).ToList();
        if (dirty.Count == 0) { StatusMessage = "Nothing to save."; return; }
        IsLoading = true; Error = null;
        try
        {
            var att = _config.Sheet("Attendance");
            var tab = await _sheets.ResolveTabAsync(att.Id, att.Tab);
            // Write the text values only — the sheet's own conditional
            // formatting turns them the right colour.
            await _sheets.WriteCellsAsync(att.Id, tab,
                dirty.Select(c => (c.SheetRow, c.Col0, c.Status.ToSheetValue())));
            foreach (var c in dirty) c.IsDirty = false;
            DirtyCount = 0;
            StatusMessage = $"Saved {dirty.Count} cell(s) at {DateTime.Now:HH:mm:ss}.";
        }
        catch (Exception ex) { Error = ex.Message; }
        finally { IsLoading = false; }
    }
}

// ===================================================================== Courses
public partial class CoursesViewModel : ObservableObject
{
    private readonly GoogleSheetsService _sheets;
    private readonly AppConfig _config;

    public ObservableCollection<CourseRecord> Records { get; } = new();
    public ObservableCollection<string> CourseNames { get; } = new();
    public ObservableCollection<string> FilterOptions { get; } = new();

    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private string? error;
    [ObservableProperty] private string selectedFilter = "All courses";

    private List<CourseRecord> _all = new();

    public CoursesViewModel(GoogleSheetsService sheets, AppConfig config)
    { _sheets = sheets; _config = config; }

    public event Action? DataLoaded;

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsLoading = true; Error = null;
        try
        {
            var sc = _config.Sheet("SectionCourses");
            var rows = await _sheets.ReadValuesFromFirstTabAsync(sc.Id,
                new[] { _config.Platoon.Name, _config.Platoon.ShortName, sc.Tab });
            var (records, names) = SheetParsers.ParseCourses(rows, _config.Platoon.Number);
            _all = records;

            CourseNames.Clear();
            foreach (var n in names) CourseNames.Add(n);

            // Rebuilding the options momentarily nulls the ComboBox selection —
            // remember it and put it back (or fall back to "All courses").
            var previous = SelectedFilter;
            FilterOptions.Clear();
            FilterOptions.Add("All courses");
            foreach (var n in names) FilterOptions.Add($"Needs: {n}");
            SelectedFilter = previous != null && FilterOptions.Contains(previous)
                ? previous
                : "All courses";

            ApplyFilter();
            DataLoaded?.Invoke();
        }
        catch (Exception ex) { Error = ex.Message; }
        finally { IsLoading = false; }
    }

    partial void OnSelectedFilterChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        Records.Clear();
        IEnumerable<CourseRecord> src = _all;
        var filter = SelectedFilter ?? "All courses";
        if (filter.StartsWith("Needs: "))
        {
            var course = filter["Needs: ".Length..];
            src = _all.Where(r =>
                r.Courses.TryGetValue(course, out var v) &&
                !v.Equals("Complete", StringComparison.OrdinalIgnoreCase) &&
                !v.Equals("Advanced", StringComparison.OrdinalIgnoreCase));
        }
        foreach (var r in src) Records.Add(r);
    }
}

// ===================================================================== CEFO
public record CefoGroup(string Name, ObservableCollection<CefoRole> Roles);

public partial class CefoViewModel : ObservableObject
{
    private readonly GoogleSheetsService _sheets;
    private readonly AppConfig _config;

    public ObservableCollection<CefoGroup> Groups { get; } = new();

    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private string? error;
    [ObservableProperty] private string searchText = "";

    private List<CefoRole> _all = new();

    public CefoViewModel(GoogleSheetsService sheets, AppConfig config)
    { _sheets = sheets; _config = config; }

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsLoading = true; Error = null;
        try
        {
            var cefo = _config.Sheet("Cefo");
            var stores = _config.Sheet("CefoStores");

            // Both tabs are independent (and normally share one workbook), so
            // resolve/read them together and share the in-flight metadata lookup.
            var cefoRowsTask = ReadRowsAsync(cefo);
            var storeRowsTask = ReadRowsAsync(stores);
            await Task.WhenAll(cefoRowsTask, storeRowsTask);

            _all = SheetParsers.ParseCefo(await cefoRowsTask);
            var storeRows = await storeRowsTask;
            _all.AddRange(SheetParsers.ParseCefo(storeRows)
                .Select(r => r with { Group = $"Coy Stores · {(r.Group.Length == 0 ? "Misc" : r.Group)}" }));

            Apply();

            async Task<IList<IList<object>>> ReadRowsAsync(SheetRef sheet)
            {
                return await _sheets.ReadValuesFromConfiguredTabAsync(
                    sheet.Id, sheet.Tab, "A1:AZ150");
            }
        }
        catch (Exception ex) { Error = ex.Message; }
        finally { IsLoading = false; }
    }

    partial void OnSearchTextChanged(string value) => Apply();

    private void Apply()
    {
        Groups.Clear();
        var q = SearchText.Trim();
        var matches = _all.Where(r =>
            q.Length == 0 ||
            r.Role.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            r.Group.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            r.Items.Any(i => i.Contains(q, StringComparison.OrdinalIgnoreCase)));

        foreach (var g in matches.GroupBy(r => r.Group.Length == 0 ? "Other" : r.Group))
            Groups.Add(new CefoGroup(g.Key, new ObservableCollection<CefoRole>(g)));
    }
}
