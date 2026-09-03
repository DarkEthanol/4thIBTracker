using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace FourthIBTracker.Services;

public enum WebsiteAttendanceStatus
{
    Unknown,
    Present,
    Late,
    Absent,
    Excused,
    Reserves,
    NotRequired,
}

public record AttendanceSectionLink(string Name, string Url, int SortOrder);

public record WebsiteAttendanceMark(string Member, WebsiteAttendanceStatus Status)
{
    public string Label => Status switch
    {
        WebsiteAttendanceStatus.Absent => "AWOL",
        WebsiteAttendanceStatus.Excused => "LOA",
        WebsiteAttendanceStatus.NotRequired => "Not Required",
        WebsiteAttendanceStatus.Unknown => "No record",
        _ => Status.ToString(),
    };

    public string Background => Status switch
    {
        WebsiteAttendanceStatus.Present => "#6AA84F",
        WebsiteAttendanceStatus.Late => "#FFFF00",
        WebsiteAttendanceStatus.Absent => "#FF0000",
        WebsiteAttendanceStatus.Excused => "#3C78D8",
        WebsiteAttendanceStatus.Reserves => "#FF9900",
        WebsiteAttendanceStatus.NotRequired => "#666666",
        _ => "#555B60",
    };

    public string Foreground => Status is WebsiteAttendanceStatus.Late or
        WebsiteAttendanceStatus.Reserves ? "#16181A" : "#FFFFFF";

    public string ShortLabel => Status switch
    {
        WebsiteAttendanceStatus.Present => "P",
        WebsiteAttendanceStatus.Late => "Late",
        WebsiteAttendanceStatus.Absent => "AWOL",
        WebsiteAttendanceStatus.Excused => "LOA",
        WebsiteAttendanceStatus.Reserves => "R",
        WebsiteAttendanceStatus.NotRequired => "N/R",
        _ => "",
    };
}

public record WebsiteAttendanceEvent(
    DateTime Date,
    string Name,
    IReadOnlyList<WebsiteAttendanceMark> Marks)
{
    public string DateLabel => Date.ToString("ddd, dd MMM yyyy");
}

public record WebsiteAttendanceSection(
    string Name,
    string Url,
    IReadOnlyList<string> Members,
    IReadOnlyList<WebsiteAttendanceEvent> Events)
{
    public string Summary => $"{Members.Count} member{(Members.Count == 1 ? "" : "s")} · " +
                             $"{Events.Count} record{(Events.Count == 1 ? "" : "s")}";
}

public record WebsiteAttendanceMonth(
    DateTime Month,
    IReadOnlyList<string> Members,
    IReadOnlyList<WebsiteAttendanceEvent> Events,
    IReadOnlyList<WebsiteAttendanceNight> Nights,
    IReadOnlyList<WebsiteAttendanceMonthSection> Sections)
{
    public string Label => Month.ToString("MMMM yyyy");
    public string Summary => $"{Members.Count} member{(Members.Count == 1 ? "" : "s")} · " +
                             $"{Events.Count} event{(Events.Count == 1 ? "" : "s")}";
    public override string ToString() => Label;
}

public record WebsiteAttendanceNight(DateTime Date, string Name)
{
    public string DateLabel => Date.ToString("dd MMM");
    public string ToolTip => $"{Date:dddd, dd MMMM yyyy} — {Name}";
}

public record WebsiteAttendanceSoldierRow(
    string Name,
    IReadOnlyList<WebsiteAttendanceMark> Cells);

public record WebsiteAttendanceMonthSection(
    string Name,
    IReadOnlyList<WebsiteAttendanceNight> Nights,
    IReadOnlyList<WebsiteAttendanceSoldierRow> Rows)
{
    public double CardWidth => Math.Max(430, 220 + Nights.Count * 62);
}

/// <summary>
/// Parses the staff attendance tracker without depending on its internal ORBAT
/// section IDs. The section links are rediscovered from the tracker whenever it
/// is refreshed, so the same build works for each configured platoon.
/// </summary>
public static partial class PlatoonAttendanceService
{
    private static readonly Regex AnchorRx = new(
        """<a\b[^>]*\bhref\s*=\s*(?:"(?<double>[^"]*)"|'(?<single>[^']*)')[^>]*>(?<text>.*?)</a>""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex TableRx = new(
        @"<table\b[^>]*>(?<content>(?:(?!<table\b).)*?)</table>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex RowRx = new(
        @"<tr\b[^>]*>(?<content>(?:(?!<tr\b).)*?)</tr>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex CellRx = new(
        @"<t[dh]\b(?<attributes>[^>]*)>(?<content>.*?)</t[dh]>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex TagRx = new("<[^>]+>", RegexOptions.Singleline);
    private static readonly Regex WhitespaceRx = new(@"\s+");
    private static readonly Regex BackgroundRx = new(
        @"background(?:-color)?\s*:\s*(?<colour>#[0-9a-f]{3,8}|rgb\s*\([^)]*\))",
        RegexOptions.IgnoreCase);

    public static string AttendanceUrl(AppConfig config)
    {
        var candidates = new[]
        {
            config.OrbatUrl,
            config.Forum.TrainingReportsForumUrl,
            config.Forum.PatrolReportsForumUrl,
            config.Forum.CoursesForumUrl,
            config.BrowserTabs.FirstOrDefault(tab =>
                tab.Url.Contains("attendance.php", StringComparison.OrdinalIgnoreCase))?.Url,
            config.BrowserTabs.FirstOrDefault()?.Url,
        };

        foreach (var candidate in candidates)
        {
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)) continue;
            return new Uri(uri, "/attendance.php").ToString();
        }

        throw new InvalidOperationException(
            "The attendance website could not be determined. Configure the ORBAT or a forum URL in Settings.");
    }

    public static IReadOnlyList<AttendanceSectionLink> FindPlatoonSections(
        string html, string baseUrl, int platoonNumber)
    {
        var result = new List<AttendanceSectionLink>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var platoonRx = new Regex(
            $@"^{platoonNumber}(?:\s*\([^)]*\))?\s+Platoon$",
            RegexOptions.IgnoreCase);
        var sectionRx = new Regex(@"^(?<number>\d+)\s+Section$", RegexOptions.IgnoreCase);
        var root = new Uri(baseUrl);

        foreach (Match anchor in AnchorRx.Matches(html))
        {
            var href = WebUtility.HtmlDecode(
                anchor.Groups["double"].Success
                    ? anchor.Groups["double"].Value
                    : anchor.Groups["single"].Value);
            if (!href.Contains("attendance.php", StringComparison.OrdinalIgnoreCase) ||
                !href.Contains("section=", StringComparison.OrdinalIgnoreCase))
                continue;

            var text = CleanText(anchor.Groups["text"].Value);
            var parts = text.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var platoonIndex = Array.FindIndex(parts, part => platoonRx.IsMatch(part));
            if (platoonIndex < 0) continue;

            string name;
            int order;
            if (platoonIndex == parts.Length - 1)
            {
                name = "HQ";
                order = 0;
            }
            else if (platoonIndex == parts.Length - 2 &&
                     sectionRx.Match(parts[^1]) is { Success: true } sectionMatch)
            {
                name = $"{sectionMatch.Groups["number"].Value} Section";
                order = int.TryParse(sectionMatch.Groups["number"].Value, out var number)
                    ? number
                    : int.MaxValue;
            }
            else
            {
                continue;
            }

            var absolute = new Uri(root, href).ToString();
            if (seen.Add(absolute)) result.Add(new AttendanceSectionLink(name, absolute, order));
        }

        return result.OrderBy(link => link.SortOrder).ThenBy(link => link.Name).ToList();
    }

    public static WebsiteAttendanceSection ParseSection(
        string html, AttendanceSectionLink section)
    {
        foreach (Match table in TableRx.Matches(html))
        {
            var rows = RowRx.Matches(table.Groups["content"].Value)
                .Select(ParseCells)
                .Where(cells => cells.Count > 0)
                .ToList();
            var headerIndex = rows.FindIndex(cells =>
                cells.Count >= 3 &&
                CleanText(cells[0].Content).Equals("Date", StringComparison.OrdinalIgnoreCase) &&
                CleanText(cells[1].Content).Equals("Event", StringComparison.OrdinalIgnoreCase));
            if (headerIndex < 0) continue;

            var members = rows[headerIndex]
                .Skip(2)
                .Select(cell => CleanText(cell.Content))
                .TakeWhile(name => name.Length > 0 &&
                    !name.Equals("Actions", StringComparison.OrdinalIgnoreCase))
                .ToList();
            // Every attendance page also contains the signed-in user's personal
            // card (Date / Event / State). It is not the selected section grid.
            if (members.Count == 0 ||
                members.SequenceEqual(["State"], StringComparer.OrdinalIgnoreCase))
                continue;

            var events = new List<WebsiteAttendanceEvent>();
            foreach (var cells in rows.Skip(headerIndex + 1))
            {
                if (cells.Count < members.Count + 2 ||
                    !DateTime.TryParseExact(
                        CleanText(cells[0].Content),
                        ["dd-MM-yyyy", "d-M-yyyy", "dd/MM/yyyy", "d/M/yyyy"],
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out var date))
                    continue;

                var eventName = CleanText(cells[1].Content);
                var marks = new List<WebsiteAttendanceMark>(members.Count);
                for (var index = 0; index < members.Count; index++)
                {
                    var cell = cells[index + 2];
                    marks.Add(new WebsiteAttendanceMark(
                        members[index], ParseStatus(cell.Attributes + " " + cell.Content)));
                }
                events.Add(new WebsiteAttendanceEvent(date, eventName, marks));
            }

            return new WebsiteAttendanceSection(
                section.Name,
                section.Url,
                members,
                events.OrderByDescending(record => record.Date).ToList());
        }

        throw new InvalidOperationException(
            $"The attendance grid for {section.Name} was not recognised. The website layout may have changed.");
    }

    public static IReadOnlyList<WebsiteAttendanceMonth> BuildMonths(
        IReadOnlyList<WebsiteAttendanceSection> sections)
    {
        var members = sections
            .SelectMany(section => section.Members)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var combinedEvents = new List<WebsiteAttendanceEvent>();
        foreach (var dateGroup in sections
                     .SelectMany(section => section.Events)
                     .GroupBy(record => record.Date.Date)
                     .OrderByDescending(group => group.Key))
        {
            var eventName = dateGroup
                .Where(record => !string.IsNullOrWhiteSpace(record.Name))
                .GroupBy(record => record.Name, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key)
                .Select(group => group.First().Name)
                .FirstOrDefault() ?? "Event";

            var statuses = new Dictionary<string, WebsiteAttendanceStatus>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var record in dateGroup)
                foreach (var mark in record.Marks)
                    statuses[mark.Member] = mark.Status;

            combinedEvents.Add(new WebsiteAttendanceEvent(
                dateGroup.Key,
                eventName,
                members.Select(member => new WebsiteAttendanceMark(
                    member,
                    statuses.GetValueOrDefault(member, WebsiteAttendanceStatus.Unknown)))
                    .ToList()));
        }

        return combinedEvents
            .GroupBy(record => new DateTime(record.Date.Year, record.Date.Month, 1))
            .OrderByDescending(group => group.Key)
            .Select(group =>
            {
                var monthEvents = group.OrderBy(record => record.Date).ToList();
                var nights = monthEvents
                    .Select(record => new WebsiteAttendanceNight(record.Date, record.Name))
                    .ToList();
                var monthSections = new List<WebsiteAttendanceMonthSection>();
                foreach (var section in sections)
                {
                    var sectionEvents = section.Events
                        .Where(record => record.Date.Year == group.Key.Year &&
                                         record.Date.Month == group.Key.Month)
                        .ToDictionary(record => record.Date.Date);
                    var rows = new List<WebsiteAttendanceSoldierRow>();
                    foreach (var member in section.Members)
                    {
                        var cells = new List<WebsiteAttendanceMark>();
                        foreach (var night in nights)
                        {
                            var status = WebsiteAttendanceStatus.Unknown;
                            if (sectionEvents.TryGetValue(night.Date.Date, out var sectionEvent))
                            {
                                status = sectionEvent.Marks.FirstOrDefault(mark =>
                                    mark.Member.Equals(member, StringComparison.OrdinalIgnoreCase))?.Status ??
                                    WebsiteAttendanceStatus.Unknown;
                            }
                            cells.Add(new WebsiteAttendanceMark(member, status));
                        }
                        rows.Add(new WebsiteAttendanceSoldierRow(member, cells));
                    }
                    monthSections.Add(new WebsiteAttendanceMonthSection(
                        section.Name, nights, rows));
                }

                return new WebsiteAttendanceMonth(
                    group.Key,
                    members,
                    monthEvents.OrderByDescending(record => record.Date).ToList(),
                    nights,
                    monthSections);
            })
            .ToList();
    }

    public static WebsiteAttendanceStatus ParseStatus(string cellHtml)
    {
        var match = BackgroundRx.Match(WebUtility.HtmlDecode(cellHtml));
        if (!match.Success) return WebsiteAttendanceStatus.Unknown;
        var colour = match.Groups["colour"].Value
            .Replace(" ", "", StringComparison.Ordinal)
            .ToUpperInvariant();
        return colour switch
        {
            "#6AA84F" or "RGB(106,168,79)" => WebsiteAttendanceStatus.Present,
            "#FFFF00" or "#FF0" or "RGB(255,255,0)" => WebsiteAttendanceStatus.Late,
            "#FF0000" or "#F00" or "RGB(255,0,0)" => WebsiteAttendanceStatus.Absent,
            "#3C78D8" or "RGB(60,120,216)" => WebsiteAttendanceStatus.Excused,
            "#FF9900" or "RGB(255,153,0)" => WebsiteAttendanceStatus.Reserves,
            "#666666" or "#666" or "RGB(102,102,102)" => WebsiteAttendanceStatus.NotRequired,
            _ => WebsiteAttendanceStatus.Unknown,
        };
    }

    private static List<HtmlCell> ParseCells(Match row) =>
        CellRx.Matches(row.Groups["content"].Value)
            .Select(cell => new HtmlCell(
                cell.Groups["attributes"].Value,
                cell.Groups["content"].Value))
            .ToList();

    private static string CleanText(string html) => WhitespaceRx.Replace(
        WebUtility.HtmlDecode(TagRx.Replace(html, " ")),
        " ").Trim();

    private record HtmlCell(string Attributes, string Content);
}
