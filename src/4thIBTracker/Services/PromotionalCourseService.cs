using System.Net;
using System.Text.RegularExpressions;
using FourthIBTracker.Models;

namespace FourthIBTracker.Services;

public record PromotionalCourseSignup(
    string Rank,
    string Name,
    string Position,
    string Unit,
    string Url)
{
    public string DisplayName => string.Join(" ", new[] { Rank.Trim(), Name.Trim() }
        .Where(value => value.Length > 0));
}

public record PromotionalCourseInfo(
    string Title,
    string Url,
    string DateText,
    string SignupDeadline,
    string Details,
    IReadOnlyList<string> Prerequisites,
    IReadOnlyList<PromotionalCourseSignup> Signups);

public record ProfileQualification(string BadgeFile, string CourseName);

public enum PrerequisiteResult { Met, Missing, Review }

public record PromotionalPrerequisiteCheck(
    string Prerequisite,
    string ProfileQualification,
    bool IsSupersedingQualification,
    PrerequisiteResult Result)
{
    public string Symbol => Result switch
    {
        PrerequisiteResult.Met => "✓",
        PrerequisiteResult.Missing => "✕",
        _ => "?",
    };

    public string Color => Result switch
    {
        PrerequisiteResult.Met => "#6AA84F",
        PrerequisiteResult.Missing => "#FF5A5A",
        _ => "#FFB347",
    };

    public string Detail => Result switch
    {
        PrerequisiteResult.Review when ProfileQualification.Length > 0 => ProfileQualification,
        PrerequisiteResult.Review => "Profile qualifications unavailable",
        PrerequisiteResult.Missing => "No matching profile badge",
        _ when IsSupersedingQualification => $"{ProfileQualification} badge (supersedes this course)",
        _ => $"{ProfileQualification} badge",
    };
}

public record PromotionalCourseCandidate(
    PromotionalCourseSignup Signup,
    string TrackerUnit,
    string TrackerNote,
    string ProfileUrl,
    IReadOnlyList<PromotionalPrerequisiteCheck> Prerequisites)
{
    public bool HasProfileUrl => ProfileUrl.Length > 0;

    public string OverallLabel => Prerequisites.Any(item => item.Result == PrerequisiteResult.Missing)
        ? "Missing prerequisites"
        : Prerequisites.Any(item => item.Result == PrerequisiteResult.Review)
            ? "Needs review"
            : "Has prerequisites";

    public string OverallColor => Prerequisites.Any(item => item.Result == PrerequisiteResult.Missing)
        ? "#FF5A5A"
        : Prerequisites.Any(item => item.Result == PrerequisiteResult.Review)
            ? "#FFB347"
            : "#6AA84F";
}

public record CourseTrackerDiscrepancy(
    string Name,
    string Section,
    string Course,
    string TrackerStatus,
    string WebsiteStatus,
    string ProfileUrl)
{
    public string Detail => $"Tracker: {TrackerStatus} · Website: {WebsiteStatus}";
    public bool HasProfileUrl => !string.IsNullOrWhiteSpace(ProfileUrl);
}

/// <summary>
/// Parses the current promotional-course forum and checks signups against the
/// training-qualification badges on their forum profiles. Parsing is
/// label-driven because course posts are written by people rather than
/// generated from a rigid form.
/// </summary>
public static class PromotionalCourseService
{
    public const string CourseGlossaryPath = "thread-26082.html";

    private static readonly Regex AnchorRx = new(
        @"<a\b[^>]*href\s*=\s*['""](?<href>[^'""]+)['""][^>]*>(?<text>.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex OptionRx = new(
        @"<option\b[^>]*value\s*=\s*['""]?(?<value>[^'""\s>]+)[^>]*>(?<text>.*?)</option>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex PostStartRx = new(
        @"<(?:div|article)\b[^>]*\bid\s*=\s*['""]post_(?<id>\d+)['""][^>]*>",
        RegexOptions.IgnoreCase);
    private static readonly Regex PostBodyStartRx = new(
        @"<(?<tag>div|article)\b[^>]*class\s*=\s*['""][^'""]*\bpost_body\b[^'""]*['""][^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex TagRx = new(@"<[^>]+>", RegexOptions.Singleline);
    private static readonly Regex ImageRx = new(
        @"<img\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline);

    // Live forum badge filenames mapped to their displayed qualifications.
    // The filename is the stable identifier; alt/title text is only a fallback
    // for a newly-added badge that has not reached this table yet.
    private static readonly IReadOnlyDictionary<string, string> BadgeCourseNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["AACwings.png"] = "Army Air Corps Wings",
            ["Advanced_Apache.png"] = "Advanced Apache Conversion Course",
            ["Advanced_Chinook_CC.png"] = "Chinook HC6 Advanced Operational Conversion Training",
            ["Advanced-Anti-Tank.png"] = "Advanced Anti-Tank Course",
            ["Advanced-Driving.png"] = "Driving Course",
            ["Advanced-Machine-Gunners.png"] = "Advanced Machine Gunners Course",
            ["Advanced-Medical.png"] = "Advanced Medical Course",
            ["Advanced-Searcher.png"] = "Advanced Search Team Course",
            ["Advanced-Signals.png"] = "Signals Course",
            ["Air-Navigation.png"] = "Air Navigation",
            ["Aircraft_Denial.png"] = "Aircraft Denial",
            ["AirHandle.png"] = "HHC",
            ["Ammunition-Technician.png"] = "Ammunition Technician",
            ["AVic.png"] = "Mechanised Infantry Qualified",
            ["Basic_Apache.png"] = "Initial Apache Conversion Course",
            ["Basic_Chinook_CC.png"] = "Chinook HC6 Initial Operational Conversion Training",
            ["Basic-Anti-Tank.png"] = "Basic Anti-Tank Course",
            ["Basic-Machine-Gunners.png"] = "Basic Machine Gunners Course",
            ["Basic-Medical.png"] = "Basic Medical Course",
            ["Basic-Searcher.png"] = "Basic Searcher Course",
            ["CBRN.png"] = "CBRN Course",
            ["Commissioned-Officer.png"] = "Commissioned Officers Course",
            ["CVRT.png"] = "CVR(T) Qualified",
            ["Defence-Technical.png"] = "Defence Technical",
            ["DIT.png"] = "DIT Qualified",
            ["Doctors.png"] = "Doctors Course",
            ["ECAS.png"] = "ECAS",
            ["FAC.png"] = "FAC",
            ["JAC_Aerial_Gunnery.png"] = "JAC Aerial Gunnery",
            ["JAC_Signals_Course.png"] = "JAC Signals",
            ["JNCO.png"] = "JNCO",
            ["JTAC.png"] = "JTAC",
            ["Junior-Management-Leadership-Course.png"] = "JMLC",
            ["Land-Navigation.png"] = "Infantry Land Navigation",
            ["Marksman-Qualified.png"] = "Sharpshooter Course",
            ["MFC.png"] = "MFC Course",
            ["Mortarman.png"] = "Mortarman Course",
            ["NCACITC.png"] = "NCAITC",
            ["NightFlying.png"] = "Night Flying",
            ["PNCO.png"] = "PNCO",
            ["Pointman.png"] = "Pointman Course",
            ["PPW.png"] = "PPW Qualified",
            ["PSBC.png"] = "PSBC",
            ["RAFwings1.png"] = "Royal Air Force Wings",
            ["RPAS.png"] = "RPAS",
            ["SCBC.png"] = "SCBC",
            ["SereA.png"] = "SERE A",
            ["SereB.png"] = "SERE B",
            ["Skill-At-Arms.png"] = "Skill at Arms Instructor",
            ["SlingLoading.png"] = "Cargo Handling",
            ["Sniper.png"] = "Sniper Course",
            ["SoI.png"] = "School Of Infantry",
            ["Spotter.png"] = "Spotter Course",
            ["UAV.png"] = "UAV",
            ["UGL.png"] = "UGL Qualified",
            ["verifiedveteran.png"] = "Verified Veteran",
            ["Zeus-Qualified.png"] = "Zeus Qualified",
        };

    // A profile shows only the highest badge in several course families.
    // These are therefore qualifications proven by the displayed badge even
    // though the lower badge image is deliberately absent from the profile.
    // Verified against the live Course Glossary (thread 26082) on 2026-10-06.
    // Only explicit Supersedes fields belong here: a prerequisite alone does
    // not establish supersession. Transitive entries are expanded so callers
    // do not need to understand the hierarchy (Doctors -> Advanced -> Basic).
    private static readonly IReadOnlyDictionary<string, string[]> SupersededBadgeCourses =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Advanced_Apache.png"] = ["Initial Apache Conversion Course"],
            ["Advanced_Chinook_CC.png"] = ["Chinook HC6 Initial Operational Conversion Training"],
            ["Advanced-Anti-Tank.png"] = ["Basic Anti-Tank badge"],
            ["Advanced-Machine-Gunners.png"] = ["Basic Machine Gunners Course"],
            ["Advanced-Medical.png"] = ["Basic Medical Course"],
            ["Doctors.png"] = ["Advanced Medical Course", "Basic Medical Course"],
            ["FAC.png"] = ["Helicopter Handling Course"],
            ["JTAC.png"] = ["ECAS"],
            ["Pointman.png"] = ["Infantry Land Navigation"],
            ["RPAS.png"] = ["UAV"],
            ["SereB.png"] = ["SERE A"],
            ["Sniper.png"] = ["Sharpshooter Course"],
        };

    private static readonly IReadOnlyDictionary<string, string> CourseAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["l2a1asmilaw"] = "basicat",
            ["l2a1ilawasm"] = "basicat",
            ["l2a1ilaworasm"] = "basicat",
            ["k170a1nlaw"] = "basicat",
            ["basicantitank"] = "basicat",
            ["basicmachinegunner"] = "l7a2gpmg",
            ["basicmachinegunners"] = "l7a2gpmg",
            ["basicsearcher"] = "searcher",
            ["basicsearchers"] = "searcher",
            ["searchers"] = "searcher",
            ["infantrylandnavigation"] = "landnav",
            ["landnavigation"] = "landnav",
            ["basiclandnav"] = "landnav",
            ["defenceinstructortechnique"] = "dit",
            ["defenceinstructorstechnique"] = "dit",
            ["defenceinstructortechniques"] = "dit",
            ["defenceinstructorstechniques"] = "dit",
            ["skillatarmsinstructor"] = "saa",
            ["potentialnoncommissionedofficer"] = "pnco",
            ["potentialnoncommissionedofficers"] = "pnco",
            ["juniornoncommissionedofficer"] = "jnco",
            ["juniornoncommissionedofficers"] = "jnco",
            ["sectioncommanderbattle"] = "scbc",
            ["sectioncommandersbattle"] = "scbc",
            ["platoonsergeantbattle"] = "psbc",
            ["platoonsergeantsbattle"] = "psbc",
            ["platooncommanderbattle"] = "pcbc",
            ["platooncommandersbattle"] = "pcbc",
            ["commissionedofficer"] = "pcbc",
            ["commissionedofficers"] = "pcbc",
            ["advancedantitank"] = "advancedat",
            ["advancedmachinegunner"] = "advancedmg",
            ["advancedmachinegunners"] = "advancedmg",
            ["advancedsearchteam"] = "advancedsearcher",
            ["ammunitiontechnician"] = "ammotech",
            ["driver"] = "driving",
            ["drivers"] = "driving",
            ["helicopterhandling"] = "hhc",
            ["l131a1gsp"] = "ppw",
            ["gsp"] = "ppw",
            ["pistol"] = "ppw",
            ["ctm"] = "basicmedical",
            ["combatteammedic"] = "basicmedical",
            ["l123ugl"] = "ugl",
            ["l129a1ssr"] = "sharpshooter",
            ["sere"] = "serea",
        };

    // Some tracker columns represent both the basic and advanced award. In
    // those columns the literal value "Advanced" must be checked against the
    // higher website badge, not merely treated as another spelling of complete.
    private static readonly IReadOnlyDictionary<string, string> AdvancedTrackerCourses =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["basicat"] = "Advanced Anti-Tank Course",
            ["l7a2gpmg"] = "Advanced Machine Gunners Course",
            ["basicmedical"] = "Advanced Medical Course",
            ["searcher"] = "Advanced Search Team Course",
        };

    public static string FindPromotionalForumUrl(string html, string sourceUrl)
    {
        foreach (Match match in AnchorRx.Matches(html))
        {
            if (!DecodeInline(match.Groups["text"].Value)
                    .Contains("Promotional Courses", StringComparison.OrdinalIgnoreCase))
                continue;
            var href = WebUtility.HtmlDecode(match.Groups["href"].Value);
            if (Regex.IsMatch(href, @"forum-\d+", RegexOptions.IgnoreCase))
                return new Uri(new Uri(sourceUrl), href).AbsoluteUri;
        }

        foreach (Match match in OptionRx.Matches(html))
        {
            if (!DecodeInline(match.Groups["text"].Value)
                    .Contains("Promotional Courses", StringComparison.OrdinalIgnoreCase))
                continue;
            var value = WebUtility.HtmlDecode(match.Groups["value"].Value);
            if (Regex.IsMatch(value, @"^\d+$"))
                return new Uri(new Uri(sourceUrl), $"forum-{value}.html").AbsoluteUri;
            if (Regex.IsMatch(value, @"forum-\d+", RegexOptions.IgnoreCase))
                return new Uri(new Uri(sourceUrl), value).AbsoluteUri;
        }

        return "";
    }

    public static PromotionalCourseInfo ParseCourse(
        ForumThread thread, IEnumerable<string> pageHtml)
    {
        var bodies = ExtractPostBodies(pageHtml);

        if (bodies.Count == 0)
            throw new InvalidOperationException("No posts were recognised in the latest promotional course.");

        var details = bodies[0].Body;
        var lines = details.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => Regex.Replace(line, @"\s+", " ").Trim())
            .Where(line => line.Length > 0)
            .ToList();
        var prerequisites = ParsePrerequisites(lines);
        var date = Field(lines, "Date");
        var deadline = lines.FirstOrDefault(line =>
            line.Contains("bids are to be placed", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("sign up by", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("signup", StringComparison.OrdinalIgnoreCase) &&
            line.Contains("deadline", StringComparison.OrdinalIgnoreCase)) ?? "";

        var signups = new Dictionary<string, PromotionalCourseSignup>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var post in bodies.Skip(1))
        {
            var signupLines = post.Body.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => Regex.Replace(line, @"\s+", " ").Trim())
                .ToList();
            var name = Field(signupLines, "Name");
            if (name.Length == 0) continue;
            var signup = new PromotionalCourseSignup(
                Field(signupLines, "Rank"),
                name,
                Field(signupLines, "Position"),
                Field(signupLines, "Section/Platoon", "Section / Platoon", "Platoon/Section"),
                PostUrl(thread.Url, post.Id));
            signups[ForumLoaService.NormalizeName(name)] = signup;
        }

        return new PromotionalCourseInfo(
            thread.Title, thread.Url, date, deadline, details,
            prerequisites, signups.Values.ToList());
    }

    public static IReadOnlyDictionary<string, IReadOnlyList<string>>
        ParseGlossaryPrerequisites(string html)
    {
        var body = ExtractPostBodies([html]).FirstOrDefault().Body;
        if (string.IsNullOrWhiteSpace(body)) return
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

        var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => Regex.Replace(line, @"\s+", " ").Trim())
            .Where(line => line.Length > 0)
            .ToList();
        var result = new Dictionary<string, IReadOnlyList<string>>(
            StringComparer.OrdinalIgnoreCase);

        for (var index = 1; index < lines.Count; index++)
        {
            if (!Regex.IsMatch(lines[index], @"^Details\s*:", RegexOptions.IgnoreCase))
                continue;

            var courseName = lines[index - 1];
            for (var detailIndex = index + 1; detailIndex < lines.Count; detailIndex++)
            {
                if (Regex.IsMatch(lines[detailIndex], @"^Details\s*:", RegexOptions.IgnoreCase))
                    break;
                var match = Regex.Match(lines[detailIndex],
                    @"^Pre-?requisites?\s*:\s*(?<value>.*)$", RegexOptions.IgnoreCase);
                if (!match.Success) continue;

                var value = match.Groups["value"].Value.Trim().Trim('.');
                result[courseName] = value.Length == 0 ||
                                     value.Equals("N/A", StringComparison.OrdinalIgnoreCase)
                    ? []
                    : SplitPrerequisites(value)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                break;
            }
        }

        return result;
    }

    public static bool TryGetGlossaryPrerequisites(
        string courseTitle,
        IReadOnlyDictionary<string, IReadOnlyList<string>> glossary,
        out IReadOnlyList<string> prerequisites)
    {
        var wanted = PromotionalCourseKey(courseTitle);
        foreach (var entry in glossary)
        {
            if (!string.Equals(PromotionalCourseKey(entry.Key), wanted,
                    StringComparison.OrdinalIgnoreCase))
                continue;
            prerequisites = entry.Value;
            return true;
        }

        prerequisites = [];
        return false;
    }

    public static IReadOnlyList<ForumThread> FindRecentThreads(
        string html, string forumUrl, int count = 5) =>
        ForumCoursesService.ParseThreads(html, forumUrl)
            // MyBB assigns monotonically increasing numeric thread IDs. Using
            // the ID prevents a reply to an older course from making it appear
            // to be the newest course merely because it was bumped.
            .OrderByDescending(thread =>
            {
                var match = Regex.Match(thread.Url,
                    @"thread-(?<id>\d+)", RegexOptions.IgnoreCase);
                return long.TryParse(match.Groups["id"].Value, out var id) ? id : 0;
            })
            .Take(Math.Max(0, count))
            .ToList();

    public static ForumThread? FindLatestThread(string html, string forumUrl) =>
        FindRecentThreads(html, forumUrl, 1).FirstOrDefault();

    public static IReadOnlyList<PromotionalCourseCandidate> CheckCandidates(
        PromotionalCourseInfo course, IEnumerable<CourseRecord> records,
        IReadOnlyDictionary<string, string>? orbatProfileLinks = null,
        IReadOnlyDictionary<string, IReadOnlyList<ProfileQualification>>?
            profileQualifications = null)
    {
        var roster = records
            .GroupBy(record => ForumLoaService.NormalizeName(record.Name),
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(),
                StringComparer.OrdinalIgnoreCase);

        return course.Signups.Select(signup =>
        {
            var key = ForumLoaService.NormalizeName(signup.Name);
            roster.TryGetValue(key, out var matches);
            matches ??= [];
            var record = ChooseRecord(matches, signup.Unit);
            var profileUrl = record?.ProfileUrl.Trim() ?? "";
            if (profileUrl.Length == 0 && orbatProfileLinks is not null)
                orbatProfileLinks.TryGetValue(key, out profileUrl);
            profileUrl ??= "";
            var note = profileUrl.Length == 0
                ? "Forum profile not found in the course tracker or website ORBAT"
                : "";

            IReadOnlyList<ProfileQualification>? qualifications = null;
            if (profileUrl.Length > 0 && profileQualifications is not null)
                profileQualifications.TryGetValue(profileUrl, out qualifications);

            var checks = course.Prerequisites.Select(prerequisite =>
            {
                if (qualifications is null)
                    return new PromotionalPrerequisiteCheck(
                        prerequisite, "", false, PrerequisiteResult.Review);

                var qualification = MatchProfileQualification(
                    prerequisite, qualifications, out var superseding);
                if (qualification.Length == 0)
                {
                    var knownCourse = CanMatchProfileCourse(prerequisite);
                    return new PromotionalPrerequisiteCheck(
                        prerequisite,
                        knownCourse
                            ? ""
                            : "No badge mapping exists for this prerequisite",
                        false,
                        knownCourse
                            ? PrerequisiteResult.Missing
                            : PrerequisiteResult.Review);
                }

                return new PromotionalPrerequisiteCheck(
                    prerequisite, qualification, superseding, PrerequisiteResult.Met);
            }).ToList();

            return new PromotionalCourseCandidate(
                signup, record?.Section ?? "", note, profileUrl, checks);
        }).ToList();
    }

    public static bool HasProfileQualificationSection(string html) =>
        Regex.IsMatch(html,
            @"(?:id\s*=\s*['""]teachingqual['""]|perscom_profile_training_qualifications)",
            RegexOptions.IgnoreCase);

    public static IReadOnlyList<ProfileQualification> ParseProfileQualifications(string html)
    {
        var qualifications = new Dictionary<string, ProfileQualification>(
            StringComparer.OrdinalIgnoreCase);
        foreach (Match image in ImageRx.Matches(html))
        {
            var tag = image.Value;
            var source = HtmlAttribute(tag, "src");
            var marker = source.LastIndexOf("/tradebadges/",
                StringComparison.OrdinalIgnoreCase);
            if (marker < 0) continue;

            var badgeFile = source[(marker + "/tradebadges/".Length)..];
            var suffix = badgeFile.IndexOfAny(['?', '#']);
            if (suffix >= 0) badgeFile = badgeFile[..suffix];
            badgeFile = WebUtility.UrlDecode(badgeFile).Trim();
            if (badgeFile.Length == 0 || qualifications.ContainsKey(badgeFile)) continue;

            var displayedName = BadgeCourseNames.GetValueOrDefault(badgeFile, "");
            if (displayedName.Length == 0) displayedName = HtmlAttribute(tag, "alt");
            if (displayedName.Length == 0) displayedName = HtmlAttribute(tag, "title");
            if (displayedName.Length == 0) continue;

            qualifications[badgeFile] = new ProfileQualification(badgeFile, displayedName);
        }
        return qualifications.Values.ToList();
    }

    public static string MatchProfileQualification(
        string prerequisite, IEnumerable<ProfileQualification> qualifications,
        out bool superseding)
    {
        superseding = false;
        var wanted = CanonicalCourseKey(prerequisite);
        foreach (var qualification in qualifications)
            if (string.Equals(CanonicalCourseKey(qualification.CourseName), wanted,
                    StringComparison.OrdinalIgnoreCase))
                return qualification.CourseName;

        foreach (var qualification in qualifications)
        {
            if (!SupersededBadgeCourses.TryGetValue(
                    qualification.BadgeFile, out var supersededCourses))
                continue;
            if (!supersededCourses.Any(course => string.Equals(
                    CanonicalCourseKey(course), wanted,
                    StringComparison.OrdinalIgnoreCase)))
                continue;
            superseding = true;
            return qualification.CourseName;
        }
        return "";
    }

    public static IReadOnlyList<CourseTrackerDiscrepancy> FindTrackerDiscrepancies(
        CourseRecord record,
        IReadOnlyList<ProfileQualification> qualifications,
        string profileUrl)
    {
        var discrepancies = new List<CourseTrackerDiscrepancy>();
        foreach (var (course, rawStatus) in record.Courses)
        {
            if (!CanMatchProfileCourse(course) ||
                !TryTrackerCompletion(rawStatus, out var trackerComplete))
                continue;

            var expectedProfileCourse = ExpectedProfileCourse(course, rawStatus);
            var qualification = MatchProfileQualification(
                expectedProfileCourse, qualifications, out var superseding);
            var websiteComplete = qualification.Length > 0;
            if (trackerComplete == websiteComplete) continue;

            discrepancies.Add(new CourseTrackerDiscrepancy(
                record.Name,
                record.Section,
                course,
                rawStatus.Trim().Length == 0 ? "Not done" : rawStatus.Trim(),
                websiteComplete
                    ? superseding
                        ? $"{qualification} (superseding badge)"
                        : qualification
                    : $"No matching {expectedProfileCourse} badge",
                profileUrl));
        }
        return discrepancies;
    }

    public static IReadOnlyList<CourseTrackerDiscrepancy>
        FindOrbatMembershipDiscrepancies(
            IEnumerable<CourseRecord> trackerRecords,
            IEnumerable<OrbatCourseMember> orbatMembers)
    {
        var tracker = trackerRecords
            .GroupBy(record => ForumLoaService.NormalizeName(record.Name),
                StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Key.Length > 0)
            .ToDictionary(group => group.Key, group => group.First(),
                StringComparer.OrdinalIgnoreCase);
        var website = orbatMembers
            .GroupBy(member => ForumLoaService.NormalizeName(member.Name),
                StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Key.Length > 0)
            .ToDictionary(group => group.Key, group => group.First(),
                StringComparer.OrdinalIgnoreCase);
        var discrepancies = new List<CourseTrackerDiscrepancy>();

        foreach (var (key, record) in tracker)
            if (!website.ContainsKey(key))
                discrepancies.Add(new CourseTrackerDiscrepancy(
                    record.Name,
                    record.Section,
                    "ORBAT membership",
                    "On course tracker",
                    "Not found on website ORBAT",
                    record.ProfileUrl));

        foreach (var (key, member) in website)
            if (!tracker.ContainsKey(key))
                discrepancies.Add(new CourseTrackerDiscrepancy(
                    member.Name,
                    member.Section,
                    "ORBAT membership",
                    "Not found on course tracker",
                    "On website ORBAT",
                    member.ProfileUrl));

        return discrepancies
            .OrderBy(item => item.Section, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool CanMatchProfileCourse(string prerequisite)
    {
        var wanted = CanonicalCourseKey(prerequisite);
        return BadgeCourseNames.Values.Any(course => string.Equals(
                   CanonicalCourseKey(course), wanted,
                   StringComparison.OrdinalIgnoreCase)) ||
               SupersededBadgeCourses.Values.SelectMany(courses => courses).Any(course =>
                   string.Equals(CanonicalCourseKey(course), wanted,
                       StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryTrackerCompletion(string value, out bool completed)
    {
        value = value.Trim();
        if (value.Equals("Complete", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("Advanced", StringComparison.OrdinalIgnoreCase))
        {
            completed = true;
            return true;
        }
        if (value.Length == 0 || value.Equals("Not Done", StringComparison.OrdinalIgnoreCase))
        {
            completed = false;
            return true;
        }

        // Upcoming, booked and other non-final values are not completion
        // claims, so they cannot be safely compared to awarded badges.
        completed = false;
        return false;
    }

    private static string ExpectedProfileCourse(string trackerCourse, string trackerStatus)
    {
        if (!trackerStatus.Trim().Equals("Advanced", StringComparison.OrdinalIgnoreCase))
            return trackerCourse;
        var key = CanonicalCourseKey(trackerCourse);
        return AdvancedTrackerCourses.GetValueOrDefault(key, trackerCourse);
    }

    public static string MatchTrackerCourse(
        string prerequisite, IEnumerable<string> trackerCourses)
    {
        // These aliases bridge wording used in the forum's official Course
        // Glossary to the compact column headings in the BG tracker. In
        // particular, the glossary defines Basic AT as completion of both the
        // L2A1 ASM/ILAW and K170A1 NLAW courses. Unknown wording is never
        // guessed: it remains unmatched and is surfaced as Needs review.
        var wanted = CanonicalCourseKey(prerequisite);

        return trackerCourses.FirstOrDefault(course =>
            string.Equals(CanonicalCourseKey(course), wanted, StringComparison.OrdinalIgnoreCase)) ?? "";
    }

    private static CourseRecord? ChooseRecord(
        IReadOnlyList<CourseRecord> matches, string signupUnit)
    {
        if (matches.Count == 1) return matches[0];
        if (matches.Count == 0) return null;

        var platoon = Regex.Match(signupUnit, @"(?<!\d)(?<number>\d{1,2})(?!\d)");
        if (platoon.Success)
        {
            var byPlatoon = matches.Where(record => Regex.IsMatch(
                record.Section,
                $@"(?<!\d){Regex.Escape(platoon.Groups["number"].Value)}(?:\s*Platoon|\b)",
                RegexOptions.IgnoreCase)).ToList();
            if (byPlatoon.Count == 1) return byPlatoon[0];
        }
        return null;
    }

    private static IReadOnlyList<string> ParsePrerequisites(IReadOnlyList<string> lines)
    {
        var header = lines.ToList().FindIndex(line =>
            line.Contains("prerequisite", StringComparison.OrdinalIgnoreCase));
        if (header < 0) return [];

        var prerequisites = new List<string>();
        var colon = lines[header].IndexOf(':');
        if (colon >= 0 && colon + 1 < lines[header].Length)
        {
            var inline = lines[header][(colon + 1)..].Trim();
            if (inline.Length > 0 && !inline.Equals("N/A", StringComparison.OrdinalIgnoreCase))
                prerequisites.AddRange(SplitPrerequisites(inline));
        }

        for (var index = header + 1; index < lines.Count; index++)
        {
            var line = lines[index].Trim();
            if (!Regex.IsMatch(line, @"^[-–—•*]\s*"))
            {
                if (prerequisites.Count > 0) break;
                continue;
            }
            var value = Regex.Replace(line, @"^[-–—•*]\s*", "").Trim();
            if (value.Length > 0) prerequisites.Add(value);
        }
        return prerequisites.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<string> SplitPrerequisites(string value) =>
        Regex.Split(value, @"\s*(?:,|&|\band\b)\s*", RegexOptions.IgnoreCase)
            .Select(item => item.Trim().Trim('.'))
            .Where(item => item.Length > 0);

    private static string Field(IReadOnlyList<string> lines, params string[] labels)
    {
        foreach (var line in lines)
            foreach (var label in labels)
            {
                var match = Regex.Match(line,
                    $@"^{Regex.Escape(label)}\s*:\s*(?<value>.+)$",
                    RegexOptions.IgnoreCase);
                if (match.Success) return match.Groups["value"].Value.Trim();
            }
        return "";
    }

    private static string CourseKey(string value)
    {
        var words = Regex.Matches(value.ToLowerInvariant(), @"[a-z0-9]+")
            .Select(match => match.Value)
            .Where(word => word is not "course" and not "badge" and not "qualified" and not "cadre")
            .ToList();
        return string.Concat(words);
    }

    private static string CanonicalCourseKey(string value)
    {
        var key = CourseKey(value);
        return CourseAliases.GetValueOrDefault(key, key);
    }

    private static string HtmlAttribute(string tag, string attribute)
    {
        var match = Regex.Match(tag,
            $@"\b{Regex.Escape(attribute)}\s*=\s*(?:""(?<value>[^""]*)""|'(?<value>[^']*)')",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success
            ? WebUtility.HtmlDecode(match.Groups["value"].Value).Trim()
            : "";
    }

    private static string PromotionalCourseKey(string value)
    {
        var key = CourseKey(value);
        if (key.Contains("pnco", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("potentialnoncommissioned", StringComparison.OrdinalIgnoreCase))
            return "pnco";
        if (key.Contains("jnco", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("juniornoncommissioned", StringComparison.OrdinalIgnoreCase))
            return "jnco";
        if (key.Contains("scbc", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("sectioncommander", StringComparison.OrdinalIgnoreCase))
            return "scbc";
        if (key.Contains("psbc", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("platoonsergeant", StringComparison.OrdinalIgnoreCase))
            return "psbc";
        if (key.Contains("pcbc", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("platooncommander", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("commissionedofficer", StringComparison.OrdinalIgnoreCase))
            return "pcbc";
        return Regex.Replace(key, @"\d+", "");
    }

    private static List<(string Id, string Body)> ExtractPostBodies(
        IEnumerable<string> pageHtml)
    {
        var bodies = new List<(string Id, string Body)>();
        var seenPosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var html in pageHtml)
        {
            var starts = PostStartRx.Matches(html).Cast<Match>().ToList();
            for (var index = 0; index < starts.Count; index++)
            {
                var start = starts[index];
                var end = index + 1 < starts.Count ? starts[index + 1].Index : html.Length;
                var postHtml = html[start.Index..end];
                var bodyStart = PostBodyStartRx.Match(postHtml);
                if (!bodyStart.Success || !seenPosts.Add(start.Groups["id"].Value)) continue;
                bodies.Add((start.Groups["id"].Value,
                    DecodeBody(ExtractElementBody(postHtml, bodyStart))));
            }
        }
        return bodies;
    }

    private static string ExtractElementBody(string html, Match start)
    {
        var tag = start.Groups["tag"].Value;
        var tagRx = new Regex($@"</?{Regex.Escape(tag)}\b[^>]*>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var contentStart = start.Index + start.Length;
        var depth = 1;
        foreach (Match tagMatch in tagRx.Matches(html, contentStart))
        {
            if (tagMatch.Value.StartsWith("</", StringComparison.Ordinal))
            {
                depth--;
                if (depth == 0)
                    return html.Substring(contentStart, tagMatch.Index - contentStart);
            }
            else if (!tagMatch.Value.EndsWith("/>", StringComparison.Ordinal))
            {
                depth++;
            }
        }

        // Malformed user-authored HTML occasionally reaches MyBB. Retaining
        // the rest of the post is safer than silently dropping later courses.
        return html[contentStart..];
    }

    private static string DecodeBody(string html)
    {
        // MyBB renders signatures and medal blocks inside post_body. They are
        // profile decoration, not course/sign-up content, and can contain names
        // that would otherwise look like form fields or course information.
        var decorationStarts = new[]
        {
            html.IndexOf("<!-- start: postbit_signature", StringComparison.OrdinalIgnoreCase),
            html.IndexOf("class=\"signature", StringComparison.OrdinalIgnoreCase),
            html.IndexOf("class=\"battlehonours", StringComparison.OrdinalIgnoreCase),
        }.Where(index => index >= 0).ToList();
        if (decorationStarts.Count > 0) html = html[..decorationStarts.Min()];

        var withLines = Regex.Replace(html,
            @"<(?:br\s*/?|/p|/div|/li)\s*>", "\n", RegexOptions.IgnoreCase);
        return WebUtility.HtmlDecode(TagRx.Replace(withLines, ""))
            .Replace("\r", "")
            .Trim();
    }

    private static string DecodeInline(string html) => Regex.Replace(
        WebUtility.HtmlDecode(TagRx.Replace(html, "")), @"\s+", " ").Trim();

    private static string PostUrl(string threadUrl, string postId)
    {
        var uri = new Uri(threadUrl);
        var match = Regex.Match(uri.AbsolutePath,
            @"thread-(?<thread>\d+)", RegexOptions.IgnoreCase);
        return match.Success
            ? new Uri(uri, $"thread-{match.Groups["thread"].Value}-post-{postId}.html#pid{postId}")
                .AbsoluteUri
            : threadUrl;
    }
}
