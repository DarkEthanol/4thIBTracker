using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace FourthIBTracker.Services;

public record ProfileQualificationFetchResult(
    IReadOnlyList<ProfileQualification> Qualifications,
    string FailureReason)
{
    public bool Success => FailureReason.Length == 0;
}

/// <summary>
/// Reads the configured platoon's structure from the website ORBAT and
/// compares it against the SuT tracker's ORBAT 2.0 sheet. Membership is
/// compared per section, while each rifle section's IC/2IC order is also checked.
/// </summary>
public static class OrbatWebService
{
    private static readonly HttpClient Http = new();

    private static readonly Regex HeadingRx = new(
        @"<h[3-5][^>]*>(?<text>.+?)</h[3-5]>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex UserLinkRx = new(
        @"<a[^>]*href=""(?<href>[^""]*user-\d+\.html[^""]*)""[^>]*>(?<name>.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex TagRx = new(@"<[^>]+>");
    private static readonly Regex NameRx = new(
        @"([\p{L}]\.\s*[\p{L}\p{M}'’\-]+(?:\s+[\p{L}\p{M}'’\-]+)*)\s*$");

    /// <summary>Section name → rank-stripped members, for HQ / 1 Section / 2 Section / 3 Section.</summary>
    public static async Task<Dictionary<string, List<string>>> FetchPlatoonAsync(string orbatUrl, int platoon)
    {
        var html = await Http.GetStringAsync(orbatUrl);
        return ParsePlatoonHtml(html, platoon);
    }

    /// <summary>
    /// Returns every website ORBAT member's profile URL, keyed by the same
    /// rank-insensitive name normalisation used for course and forum matching.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, string>> FetchProfileLinksAsync(
        string orbatUrl)
    {
        var html = await Http.GetStringAsync(orbatUrl);
        return ParseProfileLinksHtml(html, orbatUrl);
    }

    /// <summary>
    /// Reads the public training-qualification block from several forum
    /// profiles. Individual failures are omitted so callers can report them
    /// without losing the successfully checked profiles.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, IReadOnlyList<ProfileQualification>>>
        FetchProfileQualificationsAsync(
            IEnumerable<string> profileUrls,
            CancellationToken cancellationToken = default)
    {
        var results = await FetchProfileQualificationResultsAsync(
            profileUrls, cancellationToken);
        return results
            .Where(result => result.Value.Success)
            .ToDictionary(result => result.Key,
                result => result.Value.Qualifications,
                StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads public qualification blocks while retaining a concise failure
    /// reason for every requested URL. This lets callers identify the member
    /// that was skipped instead of reducing all failures to a count.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, ProfileQualificationFetchResult>>
        FetchProfileQualificationResultsAsync(
            IEnumerable<string> profileUrls,
            CancellationToken cancellationToken = default)
    {
        var urls = profileUrls
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Select(url => url.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        using var gate = new SemaphoreSlim(8, 8);
        var reads = urls.Select(async url =>
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https"))
                return (Url: url, Result: new ProfileQualificationFetchResult(
                    [], "invalid profile link"));

            await gate.WaitAsync(cancellationToken);
            try
            {
                var html = await Http.GetStringAsync(url, cancellationToken);
                if (!PromotionalCourseService.HasProfileQualificationSection(html))
                    return (Url: url, Result: new ProfileQualificationFetchResult(
                        [], "qualification section missing"));
                return (Url: url, Result: new ProfileQualificationFetchResult(
                    PromotionalCourseService.ParseProfileQualifications(html), ""));
            }
            catch (HttpRequestException ex) when (!cancellationToken.IsCancellationRequested)
            {
                var reason = ex.StatusCode is null
                    ? "profile request failed"
                    : $"website returned {(int)ex.StatusCode.Value}";
                return (Url: url, Result: new ProfileQualificationFetchResult([], reason));
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return (Url: url, Result: new ProfileQualificationFetchResult(
                    [], "profile request timed out"));
            }
            catch when (!cancellationToken.IsCancellationRequested)
            {
                return (Url: url, Result: new ProfileQualificationFetchResult(
                    [], "profile could not be loaded"));
            }
            finally
            {
                gate.Release();
            }
        });

        return (await Task.WhenAll(reads))
            .ToDictionary(result => result.Url, result => result.Result,
                StringComparer.OrdinalIgnoreCase);
    }

    internal static IReadOnlyDictionary<string, string> ParseProfileLinksHtml(
        string html, string orbatUrl)
    {
        var profiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in UserLinkRx.Matches(html))
        {
            var name = DecodeText(match.Groups["name"].Value);
            var key = ForumLoaService.NormalizeName(name);
            if (key.Length == 0 || profiles.ContainsKey(key)) continue;

            var href = WebUtility.HtmlDecode(match.Groups["href"].Value);
            if (Uri.TryCreate(new Uri(orbatUrl), href, out var profile))
                profiles[key] = profile.AbsoluteUri;
        }
        return profiles;
    }

    internal static Dictionary<string, List<string>> ParsePlatoonHtml(string html, int platoon)
    {
        // Matches ordinary and qualified headings such as "3 (Fire Support) Platoon".
        var platoonHeadingRx = new Regex($@"^{platoon}\b.*Platoon$", RegexOptions.IgnoreCase);

        // Interleave headings and member links in document order.
        var events = new List<(int Pos, bool IsHeading, string Text)>();
        foreach (Match m in HeadingRx.Matches(html))
            events.Add((m.Index, true, DecodeText(m.Groups["text"].Value)));
        foreach (Match m in UserLinkRx.Matches(html))
            events.Add((m.Index, false, DecodeText(m.Groups["name"].Value)));
        events.Sort((a, b) => a.Pos.CompareTo(b.Pos));

        var sections = new Dictionary<string, List<string>>
        {
            ["HQ"] = new(), ["1 Section"] = new(), ["2 Section"] = new(), ["3 Section"] = new(),
        };

        string? current = null; // null = not inside the configured platoon block yet
        foreach (var (_, isHeading, text) in events)
        {
            if (isHeading)
            {
                if (current == null && platoonHeadingRx.IsMatch(text))
                    current = "HQ";
                else if (current != null && sections.ContainsKey(text))
                    current = text;
                else if (current != null)
                    break; // left the configured platoon block
                continue;
            }
            if (current == null) continue;
            var m = NameRx.Match(text);
            if (m.Success) sections[current].Add(m.Groups[1].Value);
        }
        return sections;
    }

    private static string DecodeText(string value) =>
        WebUtility.HtmlDecode(TagRx.Replace(value, "")).Trim();

    public record OrbatMismatch(string Name, string Detail);

    /// <summary>
    /// Membership is compared order-insensitively per section. The first two
    /// slots of each rifle section are additionally compared in order because
    /// they are the IC and 2IC appointments used by the NCO course scan.
    /// </summary>
    public static List<OrbatMismatch> Compare(
        Dictionary<string, List<string>> web, Dictionary<string, List<string>> sheet)
    {
        static Dictionary<string, string> Flatten(Dictionary<string, List<string>> d) =>
            d.SelectMany(kv => kv.Value.Select(n => (Name: Norm(n), Display: n, Section: kv.Key)))
             .GroupBy(x => x.Name)
             .ToDictionary(g => g.Key, g => g.First().Section);

        static string Norm(string n) => Regex.Replace(
                n.Normalize(NormalizationForm.FormC), @"\s+", " ")
            .Trim().ToLowerInvariant();

        var webFlat = Flatten(web);
        var sheetFlat = Flatten(sheet);
        var display = web.SelectMany(kv => kv.Value)
            .Concat(sheet.SelectMany(kv => kv.Value))
            .GroupBy(Norm).ToDictionary(g => g.Key, g => g.First());

        var mismatches = new List<OrbatMismatch>();
        foreach (var (name, webSec) in webFlat)
        {
            if (!sheetFlat.TryGetValue(name, out var sheetSec))
                mismatches.Add(new(display[name], $"On website ({webSec}) but not in the SuT tracker's configured platoon"));
            else if (sheetSec != webSec)
                mismatches.Add(new(display[name], $"Website: {webSec} · SuT tracker: {sheetSec}"));
        }
        foreach (var (name, sheetSec) in sheetFlat)
            if (!webFlat.ContainsKey(name))
                mismatches.Add(new(display[name], $"In the SuT tracker ({sheetSec}) but not on the website ORBAT"));

        foreach (var section in new[] { "1 Section", "2 Section", "3 Section" })
        {
            web.TryGetValue(section, out var webMembers);
            sheet.TryGetValue(section, out var sheetMembers);
            webMembers ??= new List<string>();
            sheetMembers ??= new List<string>();

            for (int slot = 0; slot < 2; slot++)
            {
                string? webName = slot < webMembers.Count ? webMembers[slot] : null;
                string? sheetName = slot < sheetMembers.Count ? sheetMembers[slot] : null;
                if (webName != null && sheetName != null && Norm(webName) == Norm(sheetName))
                    continue;
                if (webName == null && sheetName == null) continue;

                var role = slot == 0 ? "IC" : "2IC";
                mismatches.Add(new($"{section} {role}",
                    $"Website: {webName ?? "empty"} · SuT tracker: {sheetName ?? "empty"}"));
            }
        }

        return mismatches.OrderBy(m => m.Name).ToList();
    }
}
