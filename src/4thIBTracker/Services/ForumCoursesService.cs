using System.Text.RegularExpressions;

namespace FourthIBTracker.Services;

public record ForumThread(string Title, string Url, string Author, DateTime? Date, int? Replies = null);

/// <summary>
/// Parses MyBB forum thread-list pages (forumdisplay). Written to be
/// theme-agnostic: it keys off thread-NNN.html links and member.php author
/// links rather than any particular CSS classes, because the 4thIB theme is
/// heavily customised. The HTML itself is fetched by a WebView2 in the view,
/// so the user's forum login session is reused.
/// </summary>
public static class ForumCoursesService
{
    /// <summary>forum-312.html → forum-312-page-3.html</summary>
    public static string PageUrl(string baseUrl, int page) =>
        page <= 1 ? baseUrl : Regex.Replace(baseUrl, @"\.html$", $"-page-{page}.html");

    private static readonly Regex ForumIdRx = new(
        @"forum-(?<id>\d+)", RegexOptions.IgnoreCase);
    private static readonly Regex ForumPageRx = new(
        @"forum-(?<id>\d+)-page-(?<page>\d+)\.html", RegexOptions.IgnoreCase);

    /// <summary>
    /// Finds the final page advertised by a MyBB forum's pager. Returning one
    /// when no pager exists also handles a newly created single-page forum.
    /// </summary>
    public static int LastPage(string html, string baseUrl)
    {
        var forum = ForumIdRx.Match(baseUrl).Groups["id"].Value;
        var last = 1;
        foreach (Match match in ForumPageRx.Matches(html))
        {
            if (forum.Length > 0 && match.Groups["id"].Value != forum) continue;
            if (int.TryParse(match.Groups["page"].Value, out var page))
                last = Math.Max(last, page);
        }
        return last;
    }

    private static readonly Regex ThreadLinkRx = new(
        @"<a[^>]*href=""(?<url>[^""]*thread-(?<id>\d+)[^""]*?\.html[^""]*)""[^>]*>(?<title>.+?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex StartedByRx = new(
        @"started\s+by.{0,200}?<a[^>]*>(?<name>[^<]+)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex MemberLinkRx = new(
        @"member\.php[^""]*""[^>]*>(?<name>[^<]+)</a>", RegexOptions.IgnoreCase);
    // Dates like 15.07.2026, 11/07/2026, 10/07/26, 30-06-2026
    private static readonly Regex DateRx = new(
        @"(?<d>\d{1,2})[./-](?<m>\d{1,2})[./-](?<y>\d{2,4})");
    private static readonly Regex RepliesRx = new(
        @"(?<n>\d[\d,]*)\s*Repl(y|ies)", RegexOptions.IgnoreCase);
    private static readonly Regex TagRx = new(@"<[^>]+>");

    public static List<ForumThread> ParseThreads(string html, string baseUrl)
    {
        var root = new Uri(baseUrl);
        var matches = ThreadLinkRx.Matches(html);

        // First pass: keep the first usable link per thread id (the title link
        // comes before any "last post" link to the same thread).
        var kept = new List<(string Id, string Title, string Url, int Start, int End)>();
        var seenIds = new HashSet<string>();
        foreach (Match m in matches)
        {
            var url = m.Groups["url"].Value;
            if (url.Contains("lastpost", StringComparison.OrdinalIgnoreCase) ||
                url.Contains("newpost", StringComparison.OrdinalIgnoreCase) ||
                url.Contains("action=", StringComparison.OrdinalIgnoreCase)) continue;

            var title = TagRx.Replace(m.Groups["title"].Value, "").Trim();
            title = Regex.Replace(title, @"\s+", " ");
            if (title.Length < 3) continue; // icons / pager numbers

            var id = m.Groups["id"].Value;
            if (!seenIds.Add(id)) continue;

            kept.Add((id, title, new Uri(root, url).ToString(), m.Index, m.Index + m.Length));
        }

        // Second pass: the segment between one title link and the next holds
        // that thread's author ("Topic started by …") and dates.
        var results = new List<ForumThread>();
        for (int i = 0; i < kept.Count; i++)
        {
            var (_, title, url, _, end) = kept[i];
            int segEnd = i + 1 < kept.Count ? kept[i + 1].Start : Math.Min(html.Length, end + 3000);
            var segment = html[end..segEnd];

            var author = StartedByRx.Match(segment) is { Success: true } sb
                ? sb.Groups["name"].Value.Trim()
                : MemberLinkRx.Match(segment) is { Success: true } ml
                    ? ml.Groups["name"].Value.Trim()
                    : "";

            // The course date lives in the thread title; fall back to any
            // date in the row (last-post date) only if the title has none.
            var date = ParseDate(title) ?? ParseDate(segment);

            int? replies = null;
            var stripped = TagRx.Replace(segment, " ");
            if (RepliesRx.Match(stripped) is { Success: true } rm &&
                int.TryParse(rm.Groups["n"].Value.Replace(",", ""), out var rn))
                replies = rn;

            results.Add(new ForumThread(title, url, author, date, replies));
        }
        return results;
    }

    private static DateTime? ParseDate(string text)
    {
        var m = DateRx.Match(text);
        if (!m.Success) return null;
        int d = int.Parse(m.Groups["d"].Value);
        int mo = int.Parse(m.Groups["m"].Value);
        int y = int.Parse(m.Groups["y"].Value);
        if (y < 100) y += 2000;
        if (mo is < 1 or > 12 || d is < 1 or > 31 || y is < 2015 or > 2100) return null;
        return new DateTime(y, mo, Math.Min(d, DateTime.DaysInMonth(y, mo)));
    }

    /// <summary>True when the page looks like a guest-login wall rather than a thread list.</summary>
    public static bool LooksLoggedOut(string html) =>
        !ThreadLinkRx.IsMatch(html) &&
        (html.Contains("login", StringComparison.OrdinalIgnoreCase) ||
         html.Contains("register", StringComparison.OrdinalIgnoreCase));
}
