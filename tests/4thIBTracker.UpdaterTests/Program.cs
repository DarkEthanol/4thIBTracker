using System.Net;
using System.Security.Cryptography;
using System.Text;
using FourthIBTracker.Services;

var failures = new List<string>();

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

Console.WriteLine("Automated tests passed (23 checks).");
return 0;

void Check(bool condition, string name)
{
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
