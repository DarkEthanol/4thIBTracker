using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace FourthIBTracker.Services;

public record LoaForumSection(string Name, string Url, int SortOrder);
public record LoaThread(string Section, string Title, string Url);
public record LoaPost(
    string Person,
    DateTime Date,
    string Reason,
    string Url,
    DateTime PostedDate,
    bool UsesThreadOwner = false);
public record LoaMemberRow(
    string Name,
    string Section,
    bool IsLoa,
    bool HasThread,
    string Reason,
    string Url)
{
    public string StatusIcon => IsLoa ? "✕" : HasThread ? "✓" : "!";
    public string StatusLabel => IsLoa ? "LOA" : HasThread ? "Attending" : "No thread";
    public string StatusColor => IsLoa ? "#FF0000" : HasThread ? "#6AA84F" : "#FF9900";
    public bool MissingThread => !HasThread;
    public bool ShowThreadWarning => MissingThread && IsLoa;
    public bool CanOpen => Url.Length > 0;
    public string LinkLabel => IsLoa ? "Post ↗" : "Thread ↗";
}

public record LoaSectionGroup(string Name, IReadOnlyList<LoaMemberRow> Members)
{
    public int LoaCount => Members.Count(member => member.IsLoa);
    public int AttendingCount => Members.Count(member => !member.IsLoa && member.HasThread);
    public int MissingThreadCount => Members.Count(member => member.MissingThread);
    public string CountLabel => $"{LoaCount} LOA · {AttendingCount} attending" +
                                (MissingThreadCount == 0 ? "" : $" · {MissingThreadCount} missing thread");
}

/// <summary>
/// Parses the platoon forum's dynamically linked LOA areas. The forum is not a
/// database: members reuse personal threads and post in several different date
/// formats. Each reply is a record in the member named by the personal thread;
/// an explicit name in the reply body takes precedence for on-behalf posts.
/// </summary>
public static partial class ForumLoaService
{
    private static readonly string[] SectionNames = ["HQ", "1 Section", "2 Section", "3 Section"];

    private static readonly Regex AnchorRx = new(
        @"<a\b[^>]*href\s*=\s*['""](?<href>[^'""]+)['""][^>]*>(?<text>.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex TagRx = new(@"<[^>]+>", RegexOptions.Singleline);
    private static readonly Regex ThreadHrefRx = new(
        @"(?:^|/)thread-(?<id>\d+)(?:-[^/?#]+)?\.html(?:[?#].*)?$",
        RegexOptions.IgnoreCase);
    private static readonly Regex PostStartRx = new(
        @"<(?:div|article)\b[^>]*\bid\s*=\s*['""]post_(?<id>\d+)['""][^>]*>",
        RegexOptions.IgnoreCase);
    private static readonly Regex PostBodyRx = new(
        @"<[^>]*class\s*=\s*['""][^'""]*\bpost_body\b[^'""]*['""][^>]*>(?<body>.*?)</(?:div|article)>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex PostDateRx = new(
        @"<[^>]*class\s*=\s*['""][^'""]*\bpost_date\b[^'""]*['""][^>]*>(?<date>.*?)</",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex NumericDateRx = new(
        @"(?<!\d)(?<day>\d{1,2})\s*[./-]\s*(?<month>\d{1,2})(?:\s*[./-]\s*(?<year>\d{2,4}))?(?!\d)",
        RegexOptions.IgnoreCase);
    private static readonly Regex RankNameRx = new(
        @"(?im)^\s*Rank\s*(?:and|&)\s*Name\s*:\s*(?<value>[^\r\n]+)");
    private static readonly Regex NameLineRx = new(
        @"(?im)^\s*Name\s*:\s*(?<value>[^\r\n]+)");
    private static readonly Regex RankLineRx = new(
        @"(?im)^\s*Rank\s*:\s*(?<value>[^\r\n]+)");
    private static readonly Regex DateLineRx = new(
        @"(?im)^\s*Date(?:\(s\)|s)?\s*:\s*(?<value>[^\r\n]+)");
    private static readonly Regex ReasonLineRx = new(
        @"(?im)^\s*Reason\s*:\s*(?<value>[^\r\n]+)");
    private static readonly Regex RankPrefixRx = new(
        @"^(?:(?:A/)?(?:LCpl|Cpl|Sgt|SSgt|CSgt|WO\d?)|Pte|Rct|2Lt|Lt|Capt|Maj|Col|Bdr|LBdr|Gnr|Tpr|Flt\s+Lt|Plt\s+Off|Fg\s+Off|Sqn\s+Ldr|Wg\s+Cdr|Gp\s+Capt|Air\s+Cdre|FS|SAC(?:\(T\))?|AS[12]|Cdt|OCdt)\.?\s+",
        RegexOptions.IgnoreCase);

    public static IReadOnlyList<LoaForumSection> FindLoaSections(
        string html, string parentUrl, int platoonNumber)
    {
        var result = new List<LoaForumSection>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? currentSection = null;

        foreach (Match anchor in AnchorRx.Matches(html))
        {
            var text = DecodeText(anchor.Groups["text"].Value);
            var section = SectionFromText(text, platoonNumber);
            if (section is not null)
            {
                currentSection = section;
                continue;
            }
            if (Regex.IsMatch(text,
                    @"(?:^|\b)\d+\s*Platoon\s*,?\s*(?:HQ|[123]\s*Section)\b",
                    RegexOptions.IgnoreCase))
            {
                currentSection = null;
                continue;
            }

            if (!string.Equals(text.Trim(), "LOA", StringComparison.OrdinalIgnoreCase) ||
                currentSection is null)
                continue;

            var absolute = ResolveUrl(parentUrl, WebUtility.HtmlDecode(anchor.Groups["href"].Value));
            if (seen.Add(absolute))
                result.Add(new LoaForumSection(
                    currentSection, absolute, Array.IndexOf(SectionNames, currentSection)));
        }

        return result.OrderBy(section => section.SortOrder).ToList();
    }

    public static IReadOnlyList<LoaThread> ParseThreads(
        string html, string forumUrl, string section)
    {
        var result = new List<LoaThread>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match anchor in AnchorRx.Matches(html))
        {
            var href = WebUtility.HtmlDecode(anchor.Groups["href"].Value);
            var match = ThreadHrefRx.Match(href);
            if (!match.Success) continue;

            var title = DecodeText(anchor.Groups["text"].Value);
            if (title.Length == 0 || title.Contains("LOA Format", StringComparison.OrdinalIgnoreCase))
                continue;

            var uri = new Uri(new Uri(forumUrl), $"thread-{match.Groups["id"].Value}.html");
            if (seen.Add(uri.AbsoluteUri))
                result.Add(new LoaThread(section, title, uri.AbsoluteUri));
        }
        return result;
    }

    public static string LastPostUrl(string threadUrl)
    {
        var uri = new Uri(threadUrl);
        var match = Regex.Match(uri.AbsolutePath, @"thread-(?<id>\d+)", RegexOptions.IgnoreCase);
        if (!match.Success) return threadUrl;
        return new Uri(uri, $"thread-{match.Groups["id"].Value}-lastpost.html").AbsoluteUri;
    }

    public static int LastThreadPage(string html, string threadUrl)
    {
        var threadId = Regex.Match(new Uri(threadUrl).AbsolutePath,
            @"thread-(?<id>\d+)", RegexOptions.IgnoreCase).Groups["id"].Value;
        var last = 1;
        foreach (Match match in Regex.Matches(html,
                     @"thread-(?<id>\d+)-page-(?<page>\d+)\.html",
                     RegexOptions.IgnoreCase))
        {
            if (threadId.Length > 0 && match.Groups["id"].Value != threadId) continue;
            if (int.TryParse(match.Groups["page"].Value, out var page))
                last = Math.Max(last, page);
        }
        return last;
    }

    public static string ThreadPageUrl(string threadUrl, int page)
    {
        if (page <= 1) return threadUrl;
        var uri = new Uri(threadUrl);
        var match = Regex.Match(uri.AbsolutePath, @"thread-(?<id>\d+)", RegexOptions.IgnoreCase);
        return !match.Success
            ? threadUrl
            : new Uri(uri, $"thread-{match.Groups["id"].Value}-page-{page}.html").AbsoluteUri;
    }

    public static IReadOnlyList<LoaPost> ParsePosts(string html, LoaThread thread)
    {
        var starts = PostStartRx.Matches(html).Cast<Match>().ToList();
        var posts = new List<LoaPost>();
        for (var index = 0; index < starts.Count; index++)
        {
            var start = starts[index];
            var end = index + 1 < starts.Count ? starts[index + 1].Index : html.Length;
            var block = html[start.Index..end];
            var bodyMatch = PostBodyRx.Match(block);
            if (!bodyMatch.Success) continue;

            var body = DecodeBody(bodyMatch.Groups["body"].Value);
            // Some live posts put the entire template on one line separated by
            // slashes. Only turn a slash into a line break when another known
            // field label follows, so ranks such as A/Sgt remain intact.
            body = Regex.Replace(body,
                @"\s*/\s*(?=(?:Rank(?:\s+(?:and|&)\s+Name)?|Name|Date(?:\(s\)|s)?|Reason)\s*:)",
                "\n", RegexOptions.IgnoreCase);
            var posted = ParsePostedDate(PostDateRx.Match(block).Groups["date"].Value);
            if (posted is null) continue;

            // The thread is the member's personal LOA record. Reply authors may
            // be NCOs posting in that thread, so only an explicit body name may
            // override the thread owner.
            var usesThreadOwner = !HasExplicitPerson(body);
            var person = PersonFrom(body, thread.Title);
            if (person.Length == 0) continue;
            var reason = ReasonLineRx.Match(body) is { Success: true } reasonMatch
                ? CleanField(reasonMatch.Groups["value"].Value)
                : "";
            var postUrl = PostUrl(thread.Url, start.Groups["id"].Value);
            foreach (var date in DatesFrom(body, posted.Value))
                posts.Add(new LoaPost(
                    person, date, reason, postUrl, posted.Value, usesThreadOwner));
        }
        return posts;
    }

    public static IReadOnlyList<LoaSectionGroup> BuildRosterStatus(
        IEnumerable<LoaPost> posts,
        IEnumerable<LoaThread> threads,
        IReadOnlyDictionary<string, List<string>> orbat,
        DateTime selectedDate)
    {
        var datedPosts = posts
            .Where(post => post.Date.Date == selectedDate.Date)
            .ToList();
        var threadList = threads.ToList();

        return SectionNames.Select(section => new LoaSectionGroup(
            section,
            orbat.GetValueOrDefault(section, [])
                .Select(name =>
                {
                    var key = NormalizeName(name);
                    var thread = threadList.FirstOrDefault(candidate =>
                        string.Equals(candidate.Section, section,
                            StringComparison.OrdinalIgnoreCase) &&
                        ThreadTitleMatchesName(candidate.Title, name));
                    var loa = datedPosts
                        .Where(post =>
                            string.Equals(NormalizeName(post.Person), key,
                                StringComparison.OrdinalIgnoreCase) ||
                            post.UsesThreadOwner && thread is not null &&
                            SameThread(post.Url, thread.Url))
                        .OrderByDescending(post => post.PostedDate)
                        .FirstOrDefault();
                    var hasLoa = loa is not null;
                    var hasThread = thread is not null;
                    return new LoaMemberRow(
                        name,
                        section,
                        hasLoa,
                        hasThread,
                        loa?.Reason ?? "",
                        loa?.Url ?? thread?.Url ?? "");
                })
                .ToList())).ToList();
    }

    public static string NormalizeName(string value)
    {
        var decoded = WebUtility.HtmlDecode(value).Normalize(NormalizationForm.FormC);
        decoded = decoded.Replace('’', '\'');
        decoded = RankPrefixRx.Replace(decoded.Trim(), "");
        return Regex.Replace(decoded, @"\s+", " ").Trim().ToLowerInvariant();
    }

    /// <summary>
    /// Personal LOA threads are sometimes titled "Name - date" or "Name notes".
    /// Match the ORBAT name at the start of the title and deliberately ignore the
    /// suffix, while retaining a boundary so similar surnames cannot collide.
    /// </summary>
    public static bool ThreadTitleMatchesName(string title, string rosterName)
    {
        var normalizedTitle = NormalizeName(title);
        var normalizedName = NormalizeName(rosterName);
        if (normalizedName.Length == 0 || normalizedTitle.Length < normalizedName.Length)
            return false;
        if (!normalizedTitle.StartsWith(normalizedName, StringComparison.OrdinalIgnoreCase))
            return false;
        return normalizedTitle.Length == normalizedName.Length ||
               !char.IsLetterOrDigit(normalizedTitle[normalizedName.Length]);
    }

    private static IEnumerable<DateTime> DatesFrom(string body, DateTime postedDate)
    {
        var dateLine = DateLineRx.Match(body);
        var source = dateLine.Success
            ? dateLine.Groups["value"].Value
            : string.Join("\n", body.Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(3));
        var dates = new HashSet<DateTime>();

        if (Regex.IsMatch(source, @"\btoday\b", RegexOptions.IgnoreCase))
            dates.Add(postedDate.Date);
        if (Regex.IsMatch(source, @"\btomorrow\b", RegexOptions.IgnoreCase))
            dates.Add(postedDate.Date.AddDays(1));

        var numeric = NumericDateRx.Matches(source).Cast<Match>()
            .Select(match => ParseNumericDate(match, postedDate))
            .Where(date => date.HasValue)
            .Select(date => date!.Value.Date)
            .ToList();
        foreach (var date in numeric) dates.Add(date);

        if (numeric.Count == 2 && Regex.IsMatch(
                source, @"\b(?:to|until|through)\b|\s[-–—]\s", RegexOptions.IgnoreCase))
        {
            var first = numeric.Min();
            var last = numeric.Max();
            if ((last - first).TotalDays <= 93)
                for (var date = first; date <= last; date = date.AddDays(1)) dates.Add(date);
        }

        return dates.OrderBy(date => date);
    }

    private static DateTime? ParseNumericDate(Match match, DateTime postedDate)
    {
        if (!int.TryParse(match.Groups["day"].Value, out var day) ||
            !int.TryParse(match.Groups["month"].Value, out var month))
            return null;

        var year = postedDate.Year;
        if (match.Groups["year"].Success && int.TryParse(match.Groups["year"].Value, out var parsedYear))
            year = parsedYear < 100 ? 2000 + parsedYear : parsedYear;
        try { return new DateTime(year, month, day); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static DateTime? ParsePostedDate(string html)
    {
        var title = Regex.Match(html, @"\btitle\s*=\s*['""](?<date>\d{1,2}-\d{1,2}-\d{4})(?:,?\s+[^'""]+)?['""]",
            RegexOptions.IgnoreCase);
        var text = title.Success ? title.Groups["date"].Value : DecodeText(html);
        foreach (var format in new[] { "d-M-yyyy", "dd-MM-yyyy", "d/M/yyyy", "dd/MM/yyyy" })
            if (DateTime.TryParseExact(text, format, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var date))
                return date.Date;
        return DateTime.TryParse(text, CultureInfo.GetCultureInfo("en-GB"),
            DateTimeStyles.AllowWhiteSpaces, out var parsed) ? parsed.Date : null;
    }

    private static string PersonFrom(string body, string author)
    {
        var combined = RankNameRx.Match(body);
        if (combined.Success) return CleanField(combined.Groups["value"].Value);

        var name = NameLineRx.Match(body);
        if (name.Success)
        {
            var value = CleanField(name.Groups["value"].Value);
            var rank = RankLineRx.Match(body);
            if (rank.Success && !RankPrefixRx.IsMatch(value))
                return $"{CleanField(rank.Groups["value"].Value)} {value}".Trim();
            return value;
        }
        return CleanField(author);
    }

    private static bool HasExplicitPerson(string body) =>
        RankNameRx.IsMatch(body) || NameLineRx.IsMatch(body);

    private static bool SameThread(string postUrl, string threadUrl)
    {
        static string ThreadId(string url) => Regex.Match(
            new Uri(url).AbsolutePath,
            @"thread-(?<id>\d+)", RegexOptions.IgnoreCase).Groups["id"].Value;

        var postThread = ThreadId(postUrl);
        return postThread.Length > 0 && string.Equals(
            postThread, ThreadId(threadUrl), StringComparison.OrdinalIgnoreCase);
    }

    private static string CleanField(string value) =>
        Regex.Replace(WebUtility.HtmlDecode(value), @"\s+", " ").Trim().Trim('-', '–', '—');

    private static string DecodeBody(string html)
    {
        var withLines = Regex.Replace(html, @"<(?:br\s*/?|/p|/div|/li)\s*>", "\n",
            RegexOptions.IgnoreCase);
        return WebUtility.HtmlDecode(TagRx.Replace(withLines, ""))
            .Replace("\r", "")
            .Trim();
    }

    private static string DecodeText(string html) =>
        Regex.Replace(WebUtility.HtmlDecode(TagRx.Replace(html, "")), @"\s+", " ").Trim();

    private static string? SectionFromText(string text, int platoon)
    {
        var match = Regex.Match(text,
            $@"(?:^|\b){platoon}\s*Platoon\s*,?\s*(?<section>HQ|[123]\s*Section)\b",
            RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        var section = Regex.Replace(match.Groups["section"].Value, @"\s+", " ");
        return section.Equals("HQ", StringComparison.OrdinalIgnoreCase)
            ? "HQ"
            : $"{section[0]} Section";
    }

    private static string ResolveUrl(string baseUrl, string href) =>
        new Uri(new Uri(baseUrl), href).AbsoluteUri;

    private static string PostUrl(string threadUrl, string postId)
    {
        var origin = new Uri(threadUrl);
        var match = Regex.Match(origin.AbsolutePath, @"thread-(?<thread>\d+)", RegexOptions.IgnoreCase);
        return match.Success
            ? new Uri(origin, $"thread-{match.Groups["thread"].Value}-post-{postId}.html#pid{postId}").AbsoluteUri
            : threadUrl;
    }
}
