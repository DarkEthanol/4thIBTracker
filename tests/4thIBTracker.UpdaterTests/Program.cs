using System.Net;
using System.Security.Cryptography;
using System.Text;
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
