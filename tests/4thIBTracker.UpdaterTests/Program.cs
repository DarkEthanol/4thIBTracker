using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FourthIBTracker.Models;
using FourthIBTracker.Services;
using FourthIBTracker.ViewModels;

var failures = new List<string>();
var checksRun = 0;

Check(UpdateService.TryParseReleaseVersion("v1.2.3", out var parsed) &&
      parsed == new Version(1, 2, 3), "stable version parsing");
Check(!UpdateService.TryParseReleaseVersion("v1.2.3-beta", out _),
    "prerelease tag rejection");
Check(!UpdateService.TryParseReleaseVersion("release-1.2.3", out _),
    "invalid tag rejection");

var unicodeOrbat = OrbatWebService.ParsePlatoonHtml("""
    <h3>1 Platoon</h3>
    <h4>1 Section</h4>
    <a href="user-3891.html">Pte. V. Bjørn</a>
    <a href="user-4000.html">Pte. J. D&apos;Arcy</a>
    <h3>2 Platoon</h3>
    """, 1);
Check(unicodeOrbat["1 Section"].Contains("V. Bjørn"),
    "Unicode ORBAT surname parsing");
Check(unicodeOrbat["1 Section"].Contains("J. D'Arcy"),
    "ORBAT HTML entity decoding");

var canonicallyEquivalent = OrbatWebService.Compare(
    new() { ["HQ"] = ["V. Éclair"] },
    new() { ["HQ"] = ["V. E\u0301clair"] });
Check(canonicallyEquivalent.Count == 0,
    "canonical Unicode ORBAT comparison");

var platoonSettings = new AppConfig.PlatoonSection
{
    OutstandingCourseExclusions = [" SERE ", "Advanced   MG"],
};
Check(platoonSettings.ExcludesOutstandingCourse("sere"),
    "case-insensitive outstanding-course exclusion");
Check(platoonSettings.ExcludesOutstandingCourse("Advanced MG"),
    "whitespace-tolerant outstanding-course exclusion");
Check(!platoonSettings.ExcludesOutstandingCourse("SERE Advanced"),
    "outstanding-course exclusion requires an exact name");
Check(new AppConfig.PlatoonSection().OperationDayOfWeek == DayOfWeek.Saturday,
    "operation night defaults to Saturday");

var legacyWebsiteSettings = JsonNode.Parse("""
    {
      "OrbatUrl": "https://4thib.co.uk/orbat.php",
      "Forum": {
        "CoursesForumUrl": "https://4thib.co.uk/forum-312.html",
        "UpcomingForumUrl": "https://4thib.co.uk/forum-16.html",
        "PatrolReportsForumUrl": "https://4thib.co.uk/forum-571.html",
        "TrainingReportsForumUrl": "https://4thib.co.uk/forum-300.html",
        "PlatoonForumUrl": "https://4thib.co.uk/forum-25.html",
        "OperationsIndexUrl": "https://4thib.co.uk/index.php",
        "PendingTransferForums": [
          "https://4thib.co.uk/forum-76.html",
          "https://4thib.co.uk/forum-79.html"
        ],
        "CompletedTransferForums": [
          "https://4thib.co.uk/forum-77.html",
          "https://4thib.co.uk/forum-80.html"
        ]
      }
    }
    """)!.AsObject();
Check(AppConfig.MigrateLegacyWebsiteSettings(legacyWebsiteSettings),
    "legacy website settings require migration");
var migratedForums = legacyWebsiteSettings["Forum"]!.AsObject();
Check(legacyWebsiteSettings["UnitWebsite"]!.GetValue<string>() == "https://4thib.co.uk" &&
      migratedForums["CoursesForumId"]!.GetValue<string>() == "312" &&
      migratedForums["TrainingReportsForumId"]!.GetValue<string>() == "300" &&
      migratedForums["PlatoonForumId"]!.GetValue<string>() == "25",
    "legacy full URLs migrate to a unit website and forum IDs");
Check(migratedForums["PendingTransferForumIds"]!.AsArray()
          .Select(value => value!.GetValue<string>()).SequenceEqual(["76", "79"]) &&
      migratedForums["CompletedTransferForumIds"]!.AsArray()
          .Select(value => value!.GetValue<string>()).SequenceEqual(["77", "80"]),
    "legacy transfer forum URL lists migrate to ID lists");
Check(!legacyWebsiteSettings.ContainsKey("OrbatUrl") &&
      !migratedForums.ContainsKey("OperationsIndexUrl") &&
      !migratedForums.ContainsKey("CoursesForumUrl") &&
      !AppConfig.MigrateLegacyWebsiteSettings(legacyWebsiteSettings),
    "legacy website keys are removed and migration is idempotent");

var compactWebsiteConfig = new AppConfig { UnitWebsite = "4thib.co.uk/index.php" };
compactWebsiteConfig.Forum.CoursesForumId = "312";
compactWebsiteConfig.Forum.PlatoonForumId = "25";
compactWebsiteConfig.Forum.PendingTransferForumIds = ["76", "79"];
Check(compactWebsiteConfig.UnitWebsite == "https://4thib.co.uk" &&
      compactWebsiteConfig.OrbatUrl == "https://4thib.co.uk/orbat.php" &&
      compactWebsiteConfig.Forum.OperationsIndexUrl == "https://4thib.co.uk/index.php" &&
      compactWebsiteConfig.Forum.CoursesForumUrl == "https://4thib.co.uk/forum-312.html" &&
      compactWebsiteConfig.Forum.PendingTransferForums.SequenceEqual(
          ["https://4thib.co.uk/forum-76.html", "https://4thib.co.uk/forum-79.html"]),
    "compact website settings resolve the URLs used by existing modules");
var compactWebsiteJson = JsonSerializer.Serialize(compactWebsiteConfig);
Check(compactWebsiteJson.Contains("CoursesForumId", StringComparison.Ordinal) &&
      !compactWebsiteJson.Contains("CoursesForumUrl", StringComparison.Ordinal) &&
      !compactWebsiteJson.Contains("OrbatUrl", StringComparison.Ordinal),
    "saved settings contain compact IDs rather than derived URLs");

var previousOAuthClientId = Environment.GetEnvironmentVariable("GoogleOAuthClientId");
var previousOAuthClientSecret = Environment.GetEnvironmentVariable("GoogleOAuthClientSecret");
try
{
    Environment.SetEnvironmentVariable("GoogleOAuthClientId", "test-client-id");
    Environment.SetEnvironmentVariable("GoogleOAuthClientSecret", "test-client-secret");
    var oauthSecrets = GoogleOAuthConfiguration.GetClientSecrets();
    Check(GoogleOAuthConfiguration.IsConfigured &&
          oauthSecrets.ClientId == "test-client-id" &&
          oauthSecrets.ClientSecret == "test-client-secret",
        "desktop OAuth client can be supplied without a credentials file");
}
finally
{
    Environment.SetEnvironmentVariable("GoogleOAuthClientId", previousOAuthClientId);
    Environment.SetEnvironmentVariable("GoogleOAuthClientSecret", previousOAuthClientSecret);
}
Check(GoogleOAuthConfiguration.TokenPath.EndsWith(
          "Google.Apis.Auth.OAuth2.Responses.TokenResponse-user",
          StringComparison.OrdinalIgnoreCase),
    "desktop OAuth preserves the existing per-user refresh-token location");
Check(LoaViewModel.NextOperationNight(
          new DateTime(2026, 10, 2), DayOfWeek.Saturday) == new DateTime(2026, 10, 3) &&
      LoaViewModel.NextOperationNight(
          new DateTime(2026, 10, 3), DayOfWeek.Saturday) == new DateTime(2026, 10, 3),
    "LOA page chooses this week's operation night, including today");

Check(DashboardViewModel.ResolveNcoCourseStatus(
          sheetDone: true, forumCompleted: false, forumUpcoming: true) ==
      DashboardNcoCourseStatus.Completed &&
      DashboardViewModel.ResolveNcoCourseStatus(
          sheetDone: false, forumCompleted: true, forumUpcoming: true) ==
      DashboardNcoCourseStatus.Completed,
    "NCO completion takes precedence over an upcoming course");
Check(DashboardViewModel.ResolveNcoCourseStatus(
          sheetDone: false, forumCompleted: false, forumUpcoming: true) ==
      DashboardNcoCourseStatus.Upcoming &&
      DashboardViewModel.ResolveNcoCourseStatus(
          sheetDone: false, forumCompleted: false, forumUpcoming: false) ==
      DashboardNcoCourseStatus.NotScheduled,
    "NCO upcoming and unscheduled states");
Check(new DashboardNcoCourse("1-1-C", "1 Section", "C. Example",
          DashboardNcoCourseStatus.Completed).StatusText == "✓ Completed" &&
      new DashboardNcoCourse("1-1-E", "2 Section", "D. Example",
          DashboardNcoCourseStatus.Upcoming).StatusText == "~ Upcoming" &&
      new DashboardNcoCourse("1-1-G", "3 Section", "E. Example",
          DashboardNcoCourseStatus.NotScheduled).StatusText == "✗ Not scheduled",
    "NCO dashboard status labels");

var dashboardConfig = new AppConfig();
var dashboard = new DashboardViewModel(
    new GoogleSheetsService(dashboardConfig), dashboardConfig);
dashboard.NcoChecks.Add(new DashboardNcoCourse(
    "1-1-C", "1 Section", "C. Example", DashboardNcoCourseStatus.NotScheduled));
dashboard.NcoChecks.Add(new DashboardNcoCourse(
    "1-1-E", "2 Section", "D. Example", DashboardNcoCourseStatus.Upcoming));
dashboard.Sections.Add(new SectionRoster(
    "HQ", new System.Collections.ObjectModel.ObservableCollection<string>(["A", "B"])));
dashboard.Sections.Add(new SectionRoster(
    "1 Section", new System.Collections.ObjectModel.ObservableCollection<string>(["C", "D", "E"])));
Check(dashboard.UnscheduledNcoAlertCount == 1 && dashboard.TotalStrength == 5,
    "dashboard alert and platoon-strength summaries");
dashboard.DisciplineStatus = "Error · unavailable";
Check(dashboard.DisciplineStatusIsError,
    "dashboard card error status detection");
DashboardDestination? dashboardDestination = null;
dashboard.NavigateRequested = destination => dashboardDestination = destination;
dashboard.NavigateCommand.Execute(DashboardDestination.Courses);
Check(dashboardDestination == DashboardDestination.Courses,
    "dashboard card navigation routing");

var attendanceIndex = """
    <a href="attendance.php?section=900">Battalion / 1 Platoon</a>
    <a href='attendance.php?section=901'>Battalion / 1 Platoon / 1 Section</a>
    <a href="attendance.php?section=902">Battalion / 1 Platoon / 2 Section</a>
    <a href="attendance.php?section=990">Battalion / 11 Platoon / 1 Section</a>
    """;
var attendanceLinks = PlatoonAttendanceService.FindPlatoonSections(
    attendanceIndex, "https://unit.invalid/attendance.php", 1);
Check(attendanceLinks.Select(link => link.Name).SequenceEqual(["HQ", "1 Section", "2 Section"]),
    "dynamic platoon attendance section discovery");
Check(attendanceLinks[1].Url == "https://unit.invalid/attendance.php?section=901",
    "relative attendance section URL resolution");

var attendanceGrid = """
    <table class="personal-card">
      <tr><th>Date</th><th>Event</th><th>State</th></tr>
      <tr><td>01-09-2026</td><td>PDT</td><td style="background:#6AA84F"></td></tr>
    </table>
    <table><tr><td class="wrapper">
      <table class="attendance-grid">
        <tr><th>Date</th><th>Event</th><th><a>Pte. V. Bj&oslash;rn</a></th><th>Cpl. J. Smith</th><th></th></tr>
        <tr><td>02-09-2026</td><td>PDT</td>
          <td style="background: #6AA84F"></td>
          <td style="background-color: rgb(60, 120, 216)"></td><td>edit</td></tr>
        <tr><td>26-08-2026</td><td>Operation TEST</td>
          <td style="background:#FFFF00"></td>
          <td style="background:#FF0000"></td><td>edit</td></tr>
      </table>
    </td></tr></table>
    """;
var parsedAttendance = PlatoonAttendanceService.ParseSection(
    attendanceGrid, attendanceLinks[1]);
Check(parsedAttendance.Members.SequenceEqual(["Pte. V. Bjørn", "Cpl. J. Smith"]),
    "attendance member and HTML entity parsing");
Check(parsedAttendance.Events.Count == 2 &&
      parsedAttendance.Events[0].Date == new DateTime(2026, 9, 2),
    "attendance record parsing and newest-first ordering");
Check(parsedAttendance.Events[0].Marks.Select(mark => mark.Status).SequenceEqual(
        [WebsiteAttendanceStatus.Present, WebsiteAttendanceStatus.Excused]),
    "attendance colour status parsing");
Check(parsedAttendance.Events[1].Marks.Select(mark => mark.Status).SequenceEqual(
        [WebsiteAttendanceStatus.Late, WebsiteAttendanceStatus.Absent]),
    "additional attendance colour status parsing");

var secondAttendanceSection = new WebsiteAttendanceSection(
    "2 Section", "https://unit.invalid/attendance.php?section=902", ["Pte. A. Other"],
    [new WebsiteAttendanceEvent(
        new DateTime(2026, 9, 2), "PDT",
        [new WebsiteAttendanceMark("Pte. A. Other", WebsiteAttendanceStatus.Late)])]);
var attendanceMonths = PlatoonAttendanceService.BuildMonths(
    [parsedAttendance, secondAttendanceSection]);
Check(attendanceMonths.Count == 2 && attendanceMonths[0].Label == "September 2026" &&
      attendanceMonths[0].Events.Count == 1,
    "platoon attendance grouped by month and event date");
Check(attendanceMonths[0].Events[0].Marks.Select(mark => mark.Status).SequenceEqual(
        [WebsiteAttendanceStatus.Present, WebsiteAttendanceStatus.Excused,
         WebsiteAttendanceStatus.Late]),
    "section attendance merged into platoon-wide event rows");
Check(attendanceMonths[1].Events[0].Marks[^1].Status == WebsiteAttendanceStatus.Unknown,
    "missing section attendance remains a neutral no-record state");
Check(attendanceMonths[0].ToString() == "September 2026",
    "attendance month selector display label");
Check(attendanceMonths[0].Sections.Select(section => section.Name)
        .SequenceEqual(["1 Section", "2 Section"]) &&
      attendanceMonths[0].Sections[0].Rows[0].Name == "Pte. V. Bjørn" &&
      attendanceMonths[0].Sections[1].Rows[0].Cells[0].Status == WebsiteAttendanceStatus.Late,
    "monthly attendance retains section and soldier-row layout");
Check(new WebsiteAttendanceMark("Test", WebsiteAttendanceStatus.Excused).ShortLabel == "LOA" &&
      new WebsiteAttendanceMark("Test", WebsiteAttendanceStatus.Absent).ShortLabel == "AWOL" &&
      new WebsiteAttendanceMark("Test", WebsiteAttendanceStatus.Late).ShortLabel == "Late",
    "website attendance uses sheet attendance acronyms");
Check(AttendanceStatus.Present.ToColor().ToString() == "#FF6AA84F" &&
      AttendanceStatus.Loa.ToColor().ToString() == "#FF3C78D8" &&
      AttendanceStatus.Awol.ToColor().ToString() == "#FFFF0000",
    "editable attendance uses website attendance colours");

var attendanceConfig = new AppConfig();
var editableAttendance = new AttendanceViewModel(
    new GoogleSheetsService(attendanceConfig), attendanceConfig);
AttendanceRowViewModel EditableAttendanceRow(string name) => new()
{
    Name = name,
    Cells = Enumerable.Range(0, 5)
        .Select(week => new AttendanceCellViewModel(
            10, week, AttendanceStatus.None, () => AttendanceStatus.Present, () => { }))
        .ToList(),
};
var bjornAttendanceRow = EditableAttendanceRow("V. Bjørn");
var smithAttendanceRow = EditableAttendanceRow("J. Smith");
var otherAttendanceRow = EditableAttendanceRow("A. Other");
editableAttendance.Sections.Add(new AttendanceSectionViewModel(
    "1 Section", new([bjornAttendanceRow, smithAttendanceRow])));
editableAttendance.Sections.Add(new AttendanceSectionViewModel(
    "2 Section", new([otherAttendanceRow])));
var stagedAttendance = editableAttendance.StageWebsiteAttendance(attendanceMonths[0]);
Check(stagedAttendance.Succeeded && stagedAttendance.Changed == 3 &&
      stagedAttendance.Unmatched == 0 && editableAttendance.DirtyCount == 3,
    "website attendance stages changes without saving them");
Check(bjornAttendanceRow.Cells[0].Status == AttendanceStatus.Present &&
      smithAttendanceRow.Cells[0].Status == AttendanceStatus.Loa &&
      otherAttendanceRow.Cells[0].Status == AttendanceStatus.Late,
    "website attendance maps statuses into the correct sheet week");
Check(AttendanceViewModel.AttendanceNameKey("Pte. V. Bjørn") ==
      AttendanceViewModel.AttendanceNameKey("V. Bjørn") &&
      AttendanceViewModel.AttendanceNameKey("A/Cpl. C. Rhodes") ==
      AttendanceViewModel.AttendanceNameKey("C. Rhodes"),
    "website-to-sheet attendance matching ignores rank");

Check(AddressViewModel.DefaultReportingMonth(new DateTime(2026, 10, 1)) ==
      new DateTime(2026, 9, 1),
    "Sergeant's Address defaults to the previous reporting month");
Check(new AddressMonthOption(new DateTime(2026, 9, 1)).Label == "September 2026",
    "Sergeant's Address month selector label");

var addressMonthRows = new List<IList<object>>
{
    new List<object>
    {
        "Month", "Link", "Platoon Average", "", "HQ", "1 Section", "2 Section",
        "3 Section", "LOA's", "AWOL's", "Late", "100%ers",
    },
    new List<object>
    {
        "Placeholders", "", "75%", "", "75%", "75%", "75%", "75%", "20", "1", "1",
        "LCpl. M. Eshers, Cpl. H. Bem",
    },
    new List<object>
    {
        "August", "", "79%", "", "75%", "72%", "75%", "95%", "19", "1", "0",
        "Cpl. C. Morgan, LCpl. M. Eshers",
    },
    new List<object>
    {
        "September", "", "", "", "", "", "", "", "", "", "", "",
    },
    new List<object>
    {
        "1 Platoon Attendance Figures 2025 (Hidden Below)", "", "", "", "", "", "", "",
        "", "", "", "",
    },
    new List<object>
    {
        "August", "", "82%", "", "100%", "88%", "78%", "60%", "", "", "",
        "Pte. Historical Member",
    },
    new List<object>
    {
        "September", "", "88%", "", "100%", "89%", "83%", "80%", "", "", "",
        "Pte. Former Member",
    },
    new List<object>
    {
        "1 Platoon Attendance Figures 2021 (Hidden Below)", "", "", "", "", "", "", "",
        "", "", "", "",
    },
    new List<object>
    {
        "September", "", "65%", "", "63%", "53%", "81%", "63%", "", "", "",
        "Pte. Very Old Member",
    },
};
var addressMonthTable = AddressViewModel.ParseMonthlyAttendanceTable(addressMonthRows, 2026);
Check(addressMonthTable.Months.TryGetValue(new DateTime(2026, 8, 1), out var augustStats) &&
      augustStats.Overall == 79 && augustStats.Hq == 75 && augustStats.S1 == 72 &&
      augustStats.S2 == 75 && augustStats.S3 == 95,
    "Sergeant's Address reads the selected month row");
Check(augustStats?.HundredPercenters.SequenceEqual(
          ["Cpl. C. Morgan", "LCpl. M. Eshers"]) == true,
    "Sergeant's Address reads monthly 100% attendees");
Check(!addressMonthTable.Months.ContainsKey(new DateTime(2026, 9, 1)) &&
      addressMonthTable.Placeholders?.Overall == 75,
    "blank month row remains distinct from live placeholder figures");
Check(addressMonthTable.Months[new DateTime(2025, 9, 1)].Overall == 88 &&
      addressMonthTable.Months[new DateTime(2021, 9, 1)].Overall == 65 &&
      addressMonthTable.Months[new DateTime(2026, 8, 1)].Overall == 79,
    "same month names remain isolated by attendance year");

var trainingReports = new TrainingReportsViewModel(new AppConfig
{
    Platoon = new AppConfig.PlatoonSection { Number = 1 },
});
var trainingMonths = trainingReports.BuildMonths(
[
    new ForumThread("Training Report: 02/09/2026 - 1 Platoon, 1 Section",
        "https://unit.invalid/thread-1.html", "Cpl. One", new DateTime(2026, 9, 2)),
    new ForumThread("Training Report: 02/09/2026 - 1 Platoon, 2 Section",
        "https://unit.invalid/thread-2.html", "Cpl. Two", new DateTime(2026, 9, 2)),
    new ForumThread("Training Report: 26/08/2026 - 1 Platoon, 3 Section",
        "https://unit.invalid/thread-3.html", "Cpl. Three", new DateTime(2026, 8, 26)),
    new ForumThread("Training Report: 02/09/2026 - 1 Platoon, HQ",
        "https://unit.invalid/thread-4.html", "Sgt. HQ", new DateTime(2026, 9, 2)),
]);
Check(trainingMonths.Select(month => month.Label)
        .SequenceEqual(["September 2026", "August 2026"]),
    "training reports grouped into newest-first month choices");
Check(trainingMonths[0].Nights.Count == 1 &&
      trainingMonths[0].Nights[0].SubmittedCount == 2 &&
      trainingMonths[0].ReportCount == 2,
    "training month keeps section submissions and excludes HQ");

var platoonForum = """
    <a href="forum-24.html">1 Platoon, HQ</a>
    <span>Sub Forums:</span> <a href="forum-801.html">LOA</a>
    <a href="forum-68.html">1 Platoon, 1 Section</a>
    <a href="forum-802.html">LOA</a>
    <a href="forum-27.html">1 Platoon, 2 Section</a>
    <a href="forum-803.html">LOA</a>
    <a href="forum-999.html">11 Platoon, 1 Section</a>
    <a href="forum-9991.html">LOA</a>
    """;
var loaForums = ForumLoaService.FindLoaSections(
    platoonForum, "https://unit.invalid/forum-25.html", 1);
Check(loaForums.Select(section => section.Name)
        .SequenceEqual(["HQ", "1 Section", "2 Section"]),
    "dynamic platoon LOA forum discovery");
Check(loaForums[1].Url == "https://unit.invalid/forum-802.html",
    "relative LOA forum URL resolution");

var loaThreadList = """
    <a href="thread-100.html">LOA Format</a>
    <a href="thread-101.html">Pte. Someone</a>
    <a href="thread-101-lastpost.html">Last post</a>
    """;
var loaThreads = ForumLoaService.ParseThreads(
    loaThreadList, "https://unit.invalid/forum-802.html", "1 Section");
Check(loaThreads.Count == 1 && loaThreads[0].Url == "https://unit.invalid/thread-101.html",
    "personal LOA thread parsing and format-thread exclusion");
Check(ForumLoaService.LastPostUrl(loaThreads[0].Url) ==
      "https://unit.invalid/thread-101-lastpost.html",
    "LOA latest-page URL generation");
Check(ForumLoaService.LastThreadPage(
          "<a href=\"thread-101-page-4.html\">4</a>", loaThreads[0].Url) == 4 &&
      ForumLoaService.ThreadPageUrl(loaThreads[0].Url, 3) ==
          "https://unit.invalid/thread-101-page-3.html",
    "LOA thread pagination discovery");

var loaPostsHtml = """
    <div class="posts2 post classic" id="post_171113">
      <a href="user-200.html">Cpl. C. Morgan</a>
      <span class="post_date"><span title="25-09-2026, 04:50 PM">Yesterday</span>, 04:50 PM</span>
      <div class="post_body scaleimages">tomorrow<br><br>ruggers</div>
    </div>
    <div class="posts2 post classic" id="post_171114">
      <a href="user-201.html">A/Cpl. C. Rhodes</a>
      <span class="post_date">09-09-2026, 12:00 PM</span>
      <div class="post_body">Name: M. Sobczak<br>Rank: Pte.<br>Date: 26/09/26<br>Reason: Away</div>
    </div>
    <div class="posts2 post classic" id="post_171115">
      <a href="user-202.html">LCpl. A. Foica</a>
      <span class="post_date">20-09-2026, 12:00 PM</span>
      <div class="post_body">Rank and Name: LCpl. A. Foica / Date(s): 26.09.2026 / Reason: Work</div>
    </div>
    <div class="posts2 post classic" id="post_171116">
      <a href="user-203.html">Pte. B. Lucas</a>
      <span class="post_date">01-07-2026, 12:00 PM</span>
      <div class="post_body">Name: B. Lucas<br>Rank: Pte.<br>Date(s): 12.7, 19.7 and 26.7</div>
    </div>
    """;
var loaPosts = ForumLoaService.ParsePosts(
    loaPostsHtml, new LoaThread(
        "1 Section", "C. Morgan", "https://unit.invalid/thread-101.html"));
Check(loaPosts.Any(post => ForumLoaService.NormalizeName(post.Person) == "c. morgan" &&
                           post.Date == new DateTime(2026, 9, 26)),
    "relative LOA date resolved against forum post date");
Check(loaPosts.Any(post => ForumLoaService.NormalizeName(post.Person) == "m. sobczak" &&
                           post.Date == new DateTime(2026, 9, 26) && post.Reason == "Away"),
    "on-behalf LOA identity and two-digit date parsing");
Check(loaPosts.Any(post => ForumLoaService.NormalizeName(post.Person) == "a. foica" &&
                           post.Date == new DateTime(2026, 9, 26) && post.Reason == "Work"),
    "single-line LOA template and dotted date parsing");
Check(loaPosts.Count(post => ForumLoaService.NormalizeName(post.Person) == "b. lucas") == 3,
    "multiple abbreviated LOA dates parsing");

var resilientLoaDates = ForumLoaService.ParsePosts("""
    <div class="posts2 post classic" id="post_171117">
      <span class="post_date">03-10-2026, 12:00 PM</span>
      <div class="post_body">Date: 03 Oct '26</div>
    </div>
    <div class="posts2 post classic" id="post_171118">
      <span class="post_date">04-10-2026, 12:00 PM</span>
      <div class="post_body">Date: 04.10.2026</div>
    </div>
    <div class="posts2 post classic" id="post_171119">
      <span class="post_date">04-10-2026, 12:00 PM</span>
      <div class="post_body">Date: 04/10/2026</div>
    </div>
    <div class="posts2 post classic" id="post_171120">
      <span class="post_date">04-10-2026, 12:00 PM</span>
      <div class="post_body">Date: 04-10-2026</div>
    </div>
    """, new LoaThread(
        "1 Section", "A. Example", "https://unit.invalid/thread-102.html"));
Check(resilientLoaDates.Count == 4 &&
      resilientLoaDates.Count(post => post.Date == new DateTime(2026, 10, 3)) == 1 &&
      resilientLoaDates.Count(post => post.Date == new DateTime(2026, 10, 4)) == 3,
    "LOA dates accept named, dotted, slashed and dashed formats");

var labelledLoaThread = new LoaThread(
    "1 Section", "Adrian C.", "https://unit.invalid/thread-103.html");
var labelledLoaPost = ForumLoaService.ParsePosts("""
    <div class="posts2 post classic" id="post_171121">
      <span class="post_date">04-10-2026, 12:00 PM</span>
      <div class="post_body">Name: Adrian C.<br>Rank: Pte.<br>
      Date(s) of LOA: 04.10.2026<br>Reason: Out of home for the weekend</div>
    </div>
    """, labelledLoaThread);
var labelledLoaRoster = ForumLoaService.BuildRosterStatus(
    labelledLoaPost,
    [labelledLoaThread],
    new Dictionary<string, List<string>>
    {
        ["HQ"] = [], ["1 Section"] = ["Pte. Adrian C."],
        ["2 Section"] = [], ["3 Section"] = [],
    },
    new DateTime(2026, 10, 4));
Check(labelledLoaPost.Single() is
          { Person: "Pte. Adrian C.", Date: var labelledDate,
            Reason: "Out of home for the weekend" } &&
      labelledDate == new DateTime(2026, 10, 4) &&
      labelledLoaRoster.Single(group => group.Name == "1 Section").Members.Single() is
          { IsLoa: true },
    "Date(s) of LOA label creates a dated roster LOA record");

var wrongAuthorPost = ForumLoaService.ParsePosts("""
    <div class="posts2 post classic" id="post_171120">
      <a href="user-202.html">LCpl. A. Foica</a>
      <span class="post_date"><span title="26-09-2026, 11:00 AM">2 hours ago</span></span>
      <div class="post_body">Date: 26/09/2026<br>Reason: Dinner</div>
    </div>
    """, new LoaThread(
        "2 Section", "M. Atilla", "https://unit.invalid/thread-300.html"));
Check(wrongAuthorPost.Count == 1 &&
      ForumLoaService.NormalizeName(wrongAuthorPost[0].Person) == "m. atilla",
    "personal LOA thread owner takes precedence over reply author");
var wrongAuthorRoster = ForumLoaService.BuildRosterStatus(
    wrongAuthorPost,
    [
        new LoaThread("2 Section", "M. Atilla", "https://unit.invalid/thread-300.html"),
        new LoaThread("2 Section", "A. Foica", "https://unit.invalid/thread-301.html"),
    ],
    new Dictionary<string, List<string>>
    {
        ["HQ"] = [], ["1 Section"] = [],
        ["2 Section"] = ["A. Foica", "M. Atilla"], ["3 Section"] = [],
    },
    new DateTime(2026, 9, 26));
Check(!wrongAuthorRoster.Single(group => group.Name == "2 Section").Members[0].IsLoa &&
      wrongAuthorRoster.Single(group => group.Name == "2 Section").Members[1].IsLoa,
    "reply in another member's thread does not mark the author as LOA");

var datedTitleThread = new LoaThread(
    "2 Section", "M. Atilla - LOA 2026", "https://unit.invalid/thread-302.html");
var datedTitlePosts = ForumLoaService.ParsePosts("""
    <div class="posts2 post classic" id="post_171121">
      <span class="post_date"><span title="26-09-2026, 11:00 AM">Today</span></span>
      <div class="post_body">Date: 26/09/2026<br>Reason: Work</div>
    </div>
    """, datedTitleThread);
var datedTitleRoster = ForumLoaService.BuildRosterStatus(
    datedTitlePosts,
    [datedTitleThread],
    new Dictionary<string, List<string>>
    {
        ["HQ"] = [], ["1 Section"] = [],
        ["2 Section"] = ["Pte. M. Atilla"], ["3 Section"] = [],
    },
    new DateTime(2026, 9, 26));
Check(ForumLoaService.ThreadTitleMatchesName(
          "Pte. M. Atilla - 26/09/2026", "M. Atilla") &&
      !ForumLoaService.ThreadTitleMatchesName("M. Atillan - 26/09/2026", "M. Atilla") &&
      datedTitleRoster.Single(group => group.Name == "2 Section").Members[0] is
          { HasThread: true, IsLoa: true },
    "LOA thread title suffix is ignored without partial-name collisions");

var loaOrbat = new Dictionary<string, List<string>>
{
    ["HQ"] = ["N. Missing"],
    ["1 Section"] = ["C. Morgan"],
    ["2 Section"] = ["A. Foica", "B. Lucas"],
    ["3 Section"] = ["M. Sobczak"],
};
var rosterThreads = new List<LoaThread>
{
    new("1 Section", "C. Morgan", "https://unit.invalid/thread-201.html"),
    new("2 Section", "A. Foica", "https://unit.invalid/thread-202.html"),
    new("2 Section", "B. Lucas", "https://unit.invalid/thread-203.html"),
};
var rosterStatus = ForumLoaService.BuildRosterStatus(
    loaPosts, rosterThreads, loaOrbat, new DateTime(2026, 9, 26));
Check(rosterStatus.SelectMany(group => group.Members).Count() == 5 &&
      rosterStatus.Sum(group => group.LoaCount) == 3 &&
      !rosterStatus.Single(group => group.Name == "2 Section").Members[1].IsLoa &&
      rosterStatus.Single(group => group.Name == "HQ").Members[0].StatusLabel == "No thread",
    "full ORBAT roster reports LOA and attending states");
var missingThreadMember = rosterStatus.Single(group => group.Name == "3 Section").Members[0];
Check(missingThreadMember.Name == "M. Sobczak" && missingThreadMember.IsLoa &&
      missingThreadMember.MissingThread,
    "missing personal LOA thread is reported independently of attendance state");

var promotionalSource = """
    <select><option value="16">Phase 2 &amp; 3 Training</option>
    <option value="193">-- Promotional Courses</option></select>
    """;
Check(PromotionalCourseService.FindPromotionalForumUrl(
          promotionalSource, "https://unit.invalid/forum-16.html") ==
      "https://unit.invalid/forum-193.html",
    "promotional-course forum is discovered rather than hard-coded");
var latestPromotional = PromotionalCourseService.FindLatestThread("""
    <a href="thread-200.html">Older course bumped today</a>
    <span>Topic started by <a href="member.php?id=1">Trainer One</a></span>
    <a href="thread-205.html">New promotional course</a>
    <span>Topic started by <a href="member.php?id=2">Trainer Two</a></span>
    """, "https://unit.invalid/forum-193.html");
Check(latestPromotional?.Url == "https://unit.invalid/thread-205.html",
    "latest promotional course uses creation order rather than last-reply bump order");

var promotionalHtml = """
    <div class="posts2 post classic" id="post_1">
      <div class="post_body">To: All members<br>Date: 06/10/2026<br>
      The following promotional courses are a prerequisite to this course:<br>
      - L7A2 GPMG Course<br>- L2A1 ASM/ILAW Course<br>- K170A1 NLAW Course<br>
      - Driving Course<br>- Signals Course<br>
      All bids are to be placed by 04/10/2026.</div>
    </div>
    <div class="posts2 post classic" id="post_2">
      <div class="post_body">Rank: Pte<br>Name: V. Example<br>
      Position: Pointman<br>Section/Platoon: 4-1</div>
    </div>
    <div class="posts2 post classic" id="post_3">
      <div class="post_body">Rank: Flt Lt<br>Name: D. Missing<br>
      Position: JAC HQ 2IC<br>Section/Platoon: JAC HQ</div>
    </div>
    """;
var promotionalInfo = PromotionalCourseService.ParseCourse(
    new ForumThread(
        "Potential Non-Commissioned Officer Course 10/26",
        "https://unit.invalid/thread-47325.html", "Capt. T. Trainer", null),
    [promotionalHtml]);
Check(promotionalInfo.Prerequisites.Count == 5 &&
      promotionalInfo.Signups.Count == 2 &&
      promotionalInfo.DateText == "06/10/2026",
    "promotional course details, prerequisites and signups parsing");

var bgCourseRows = new List<IList<object>>
{
    new List<object> { "4-1" },
    new List<object>(),
    new List<object> { "", "Nr.", "Name/Rank", "ACMT", "L7A2 GPMG", "Basic AT", "Driving", "Signals" },
    new List<object> { "", "1", "Pte. V. Example", "58", "Complete", "Complete", "Complete", "Complete" },
};
var bgCourseRecords = SheetParsers.ParseCourseRosterTab(bgCourseRows, "4 Platoon");
var promotionalChecks = PromotionalCourseService.CheckCandidates(
    promotionalInfo, bgCourseRecords);
Check(bgCourseRecords.Count == 1 &&
      promotionalChecks[0].OverallLabel == "Has prerequisites" &&
      promotionalChecks[0].Prerequisites.All(item => item.Result == PrerequisiteResult.Met) &&
      promotionalChecks[1].OverallLabel == "Needs review" &&
      promotionalChecks[1].TrackerNote.Contains("Not found", StringComparison.OrdinalIgnoreCase),
    "BG tracker eligibility check uses official Basic AT aggregation and flags unknown members");
var joinedInitialCourse = promotionalInfo with
{
    Signups =
    [
        new PromotionalCourseSignup(
            "Pte", "V.Example", "Pointman", "4-1",
            "https://unit.invalid/thread-47325-post-4.html#pid4"),
    ],
};
var joinedInitialChecks = PromotionalCourseService.CheckCandidates(
    joinedInitialCourse, bgCourseRecords);
Check(ForumLoaService.NormalizeName("Pte.V.Example") ==
          ForumLoaService.NormalizeName("Pte. V. Example") &&
      joinedInitialChecks.Single().OverallLabel == "Has prerequisites",
    "joined initials and rank punctuation match spaced BG tracker names");
Check(PromotionalCourseService.MatchTrackerCourse(
          "K170A1 NLAW Course", bgCourseRecords[0].Courses.Keys) == "Basic AT" &&
      PromotionalCourseService.MatchTrackerCourse(
          "L2A1 ASM/ILAW Course", bgCourseRecords[0].Courses.Keys) == "Basic AT",
    "official Basic AT component courses map to the tracker badge column");
Check(PromotionalCourseService.MatchTrackerCourse(
          "Driving Course", ["Drivers", "Signals"]) == "Drivers" &&
      PromotionalCourseService.MatchTrackerCourse(
          "Drivers Course", ["Driving", "Signals"]) == "Driving",
    "Driving and Drivers course headings are equivalent");

var checksum = new string('a', 64);
Check(UpdateService.ParseChecksum($"{checksum}  4thIBTracker.exe") == checksum,
    "checksum parsing");

var payload = Encoding.UTF8.GetBytes("verified updater payload");
var payloadHash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
var releaseJson = $$"""
{
  "tag_name": "v1.2.0",
  "html_url": "https://github.com/example/tracker/releases/tag/v1.2.0",
  "body": "Test release",
  "draft": false,
  "prerelease": false,
  "assets": [
    {
      "name": "4thIBTracker.exe",
      "browser_download_url": "https://downloads.invalid/4thIBTracker.exe",
      "digest": "sha256:{{payloadHash}}"
    },
    {
      "name": "4thIBTracker.exe.sha256",
      "browser_download_url": "https://downloads.invalid/4thIBTracker.exe.sha256"
    }
  ]
}
""";

var temporaryRoot = Path.GetFullPath(Path.Combine(
    Path.GetTempPath(), "4thIBTracker-UpdaterTests", Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(temporaryRoot);
try
{
    using var http = new HttpClient(new StubHandler(request =>
    {
        var uri = request.RequestUri?.AbsoluteUri ?? "";
        if (uri.EndsWith("/releases/latest", StringComparison.Ordinal))
            return TextResponse(releaseJson, "application/json");
        if (uri.EndsWith(".sha256", StringComparison.Ordinal))
            return TextResponse($"{payloadHash}  4thIBTracker.exe\n", "text/plain");
        if (uri.EndsWith("4thIBTracker.exe", StringComparison.Ordinal))
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload),
            };
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }));

    var service = new UpdateService(
        "example/tracker", new Version(1, 0, 0), http, temporaryRoot);
    var release = await service.CheckForUpdateAsync();
    Check(release?.Version == new Version(1, 2, 0), "newer release discovery");
    Check(release?.Sha256 == payloadHash, "GitHub/checksum agreement");

    if (release is not null)
    {
        var downloaded = await service.DownloadAsync(release);
        Check(File.Exists(downloaded), "release download");
        Check(UpdateService.ComputeSha256(downloaded) == payloadHash,
            "download verification");
    }

    var currentService = new UpdateService(
        "example/tracker", new Version(1, 2, 0), http, temporaryRoot);
    Check(await currentService.CheckForUpdateAsync() is null,
        "current release is not offered again");

    var replacement = Path.Combine(temporaryRoot, "new.exe");
    var target = Path.Combine(temporaryRoot, "installed.exe");
    await File.WriteAllBytesAsync(replacement, payload);
    await File.WriteAllTextAsync(target, "previous version");
    UpdateService.ReplaceExecutable(replacement, target, payloadHash);
    Check(await File.ReadAllBytesAsync(target) is var replaced && replaced.SequenceEqual(payload),
        "atomic executable replacement");
    Check(await File.ReadAllTextAsync(target + ".previous") == "previous version",
        "rollback copy creation");
}
finally
{
    var safeParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "4thIBTracker-UpdaterTests"))
        .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if (temporaryRoot.StartsWith(safeParent, StringComparison.OrdinalIgnoreCase) &&
        Directory.Exists(temporaryRoot))
        Directory.Delete(temporaryRoot, recursive: true);
}

if (failures.Count > 0)
{
    Console.Error.WriteLine("Updater tests failed:");
    foreach (var failure in failures) Console.Error.WriteLine($"- {failure}");
    return 1;
}

Console.WriteLine($"Automated tests passed ({checksRun} checks).");
return 0;

void Check(bool condition, string name)
{
    checksRun++;
    if (!condition) failures.Add(name);
}

static HttpResponseMessage TextResponse(string text, string mediaType) => new(HttpStatusCode.OK)
{
    Content = new StringContent(text, Encoding.UTF8, mediaType),
};

sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
    : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(responder(request));
}
