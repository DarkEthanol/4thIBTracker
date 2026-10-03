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

public enum PrerequisiteResult { Met, Missing, Review }

public record PromotionalPrerequisiteCheck(
    string Prerequisite,
    string TrackerCourse,
    string TrackerValue,
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

    public string Detail => TrackerCourse.Length == 0
        ? "No matching tracker column"
        : $"{TrackerCourse}: {(TrackerValue.Length == 0 ? "Not Done" : TrackerValue)}";
}

public record PromotionalCourseCandidate(
    PromotionalCourseSignup Signup,
    string TrackerUnit,
    string TrackerNote,
    IReadOnlyList<PromotionalPrerequisiteCheck> Prerequisites)
{
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

/// <summary>
/// Parses the current promotional-course forum and checks signups against the
/// unit-wide Section Courses workbook. Parsing is label-driven because course
/// posts are written by people rather than generated from a rigid form.
/// </summary>
public static class PromotionalCourseService
{
    private static readonly Regex AnchorRx = new(
        @"<a\b[^>]*href\s*=\s*['""](?<href>[^'""]+)['""][^>]*>(?<text>.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex OptionRx = new(
        @"<option\b[^>]*value\s*=\s*['""]?(?<value>[^'""\s>]+)[^>]*>(?<text>.*?)</option>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex PostStartRx = new(
        @"<(?:div|article)\b[^>]*\bid\s*=\s*['""]post_(?<id>\d+)['""][^>]*>",
        RegexOptions.IgnoreCase);
    private static readonly Regex PostBodyRx = new(
        @"<[^>]*class\s*=\s*['""][^'""]*\bpost_body\b[^'""]*['""][^>]*>(?<body>.*?)</(?:div|article)>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex TagRx = new(@"<[^>]+>", RegexOptions.Singleline);

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
        var bodies = new List<(string Id, string Body)>();
        var seenPosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var html in pageHtml)
        {
            var starts = PostStartRx.Matches(html).Cast<Match>().ToList();
            for (var index = 0; index < starts.Count; index++)
            {
                var start = starts[index];
                var end = index + 1 < starts.Count ? starts[index + 1].Index : html.Length;
                var body = PostBodyRx.Match(html[start.Index..end]);
                if (!body.Success || !seenPosts.Add(start.Groups["id"].Value)) continue;
                bodies.Add((start.Groups["id"].Value, DecodeBody(body.Groups["body"].Value)));
            }
        }

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

    public static ForumThread? FindLatestThread(string html, string forumUrl) =>
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
            .FirstOrDefault();

    public static IReadOnlyList<PromotionalCourseCandidate> CheckCandidates(
        PromotionalCourseInfo course, IEnumerable<CourseRecord> records)
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
            var ambiguous = matches.Count > 1 && record is null;
            var note = matches.Count == 0
                ? "Not found in the BG course tracker"
                : ambiguous
                    ? "Multiple tracker records match this name"
                    : "";

            var checks = course.Prerequisites.Select(prerequisite =>
            {
                if (record is null)
                    return new PromotionalPrerequisiteCheck(
                        prerequisite, "", "", PrerequisiteResult.Review);

                var trackerCourse = MatchTrackerCourse(prerequisite, record.Courses.Keys);
                if (trackerCourse.Length == 0)
                    return new PromotionalPrerequisiteCheck(
                        prerequisite, "", "", PrerequisiteResult.Review);

                var value = record.Courses.GetValueOrDefault(trackerCourse, "").Trim();
                var result = value.Equals("Complete", StringComparison.OrdinalIgnoreCase) ||
                             value.Equals("Advanced", StringComparison.OrdinalIgnoreCase)
                    ? PrerequisiteResult.Met
                    : value.Length == 0 || value.Equals("Not Done", StringComparison.OrdinalIgnoreCase)
                        ? PrerequisiteResult.Missing
                        : PrerequisiteResult.Review;
                return new PromotionalPrerequisiteCheck(
                    prerequisite, trackerCourse, value, result);
            }).ToList();

            return new PromotionalCourseCandidate(
                signup, record?.Section ?? "", note, checks);
        }).ToList();
    }

    public static string MatchTrackerCourse(
        string prerequisite, IEnumerable<string> trackerCourses)
    {
        // These aliases bridge wording used in the forum's official Course
        // Glossary to the compact column headings in the BG tracker. In
        // particular, the glossary defines Basic AT as completion of both the
        // L2A1 ASM/ILAW and K170A1 NLAW courses. Unknown wording is never
        // guessed: it remains unmatched and is surfaced as Needs review.
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["l2a1asmilaw"] = "basicat",
            ["l2a1ilawasm"] = "basicat",
            ["k170a1nlaw"] = "basicat",
            ["basicantitank"] = "basicat",
            ["basicmachinegunner"] = "l7a2gpmg",
            ["basicmachinegunners"] = "l7a2gpmg",
            ["infantrylandnavigation"] = "landnav",
            ["landnavigation"] = "landnav",
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
            ["advancedantitank"] = "advancedat",
            ["advancedmachinegunner"] = "advancedmg",
            ["ammunitiontechnician"] = "ammotech",
            ["driver"] = "driving",
            ["drivers"] = "driving",
        };

        string CanonicalKey(string value)
        {
            var key = CourseKey(value);
            return aliases.GetValueOrDefault(key, key);
        }

        var wanted = CanonicalKey(prerequisite);

        return trackerCourses.FirstOrDefault(course =>
            string.Equals(CanonicalKey(course), wanted, StringComparison.OrdinalIgnoreCase)) ?? "";
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
