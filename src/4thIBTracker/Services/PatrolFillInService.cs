using System.Net;
using System.Text.RegularExpressions;

namespace FourthIBTracker.Services;

public sealed record PatrolRosterMember(
    string Name,
    string NormalizedName,
    string Origin);

/// <summary>
/// Extracts the personnel roster from the first post of a patrol report. The
/// forum template is free text, so only the labelled A-to-B block is parsed;
/// tasks, signatures, replies, and other dotted text are deliberately ignored.
/// </summary>
public static class PatrolFillInService
{
    private static readonly Regex PostBodyRx = new(
        @"<[^>]*class\s*=\s*['\""][^'\""]*\bpost_body\b[^'\""]*['\""][^>]*>(?<body>.*?)</(?:div|article)>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex RosterRx = new(
        @"\bA\s*[.:]\s*SIZE\s+(?:AND|&amp;|&)\s+COMPOSITION\s+OF\s+PATROL\s*:?\s*" +
        @"(?<roster>.*?)(?=\r?\n\s*B\s*[.:]\s*TASK\b)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex TrailingOriginRx = new(
        @"\s*\((?<origin>[^()]*)\)\s*$", RegexOptions.IgnoreCase);
    private static readonly Regex EmbeddedMemberRx = new(
        @"(?<rank>(?:A/)?(?:LCpl|Cpl|Pte|CSgt|Sgt|WO[12]|2Lt|Lt\s+Col|Lt|Capt|Maj|Col|Brig|Flt\s+Lt|FS|Rct))" +
        @"[.,]*\s*(?<name>(?:[\p{L}]\.\s*)+[\p{L}\p{M}'’\-]+(?:\s+[\p{L}\p{M}'’\-]+)*)",
        RegexOptions.IgnoreCase);
    private static readonly Regex InlineOriginRx = new(
        @"^[\s|I:\-–—]*(?<origin>(?:\d+\s*[-/]\s*(?:\d+|HQ)|\d+\s*(?:Plt|Platoon)\s*HQ|BGHQ|ARC\s*[- ]?\s*1|ELOA|[ABC]\s*Coy\s*HQ))[\s|I.,]*$",
        RegexOptions.IgnoreCase);
    private static readonly Regex TagRx = new(@"<[^>]+>", RegexOptions.Singleline);

    public static IReadOnlyList<PatrolRosterMember> ParseRoster(string html)
    {
        var bodyMatch = PostBodyRx.Match(html);
        var source = bodyMatch.Success ? bodyMatch.Groups["body"].Value : html;
        var text = DecodeBody(source);
        var rosterMatch = RosterRx.Match(text);
        if (!rosterMatch.Success) return [];

        var result = new List<PatrolRosterMember>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in rosterMatch.Groups["roster"].Value.Split('\n'))
        {
            var line = Regex.Replace(rawLine.Trim(), @"^[\-–—•*]+\s*", "");
            if (line.Length == 0) continue;

            var origin = "";
            var originMatch = TrailingOriginRx.Match(line);
            if (originMatch.Success)
            {
                origin = Regex.Replace(originMatch.Groups["origin"].Value.Trim(),
                    @"^fill\s*-?\s*in\s*", "", RegexOptions.IgnoreCase).Trim();
                line = line[..originMatch.Index].Trim();
            }

            // Hand-entered reports occasionally use "Pte," instead of "Pte.".
            line = Regex.Replace(line, @"^(?<rank>[\p{L}0-9/() ]{2,12}),\s+",
                "${rank}. ");
            // Two recurring section templates accidentally omit the leading P
            // after a role label: "CTM (UGL)te. B. Sansom".
            line = Regex.Replace(line, @"(?<=\))\s*te\.", ": Pte.",
                RegexOptions.IgnoreCase);

            var memberMatch = EmbeddedMemberRx.Match(line);
            if (!memberMatch.Success) continue;

            var rank = memberMatch.Groups["rank"].Value.Trim().TrimEnd('.', ',');
            var personalName = Regex.Replace(
                memberMatch.Groups["name"].Value.Trim(), @"\.\s*", ". ").Trim();
            // Some reports use a bare I as a column separator after the name.
            personalName = Regex.Replace(personalName, @"\s+I$", "",
                RegexOptions.IgnoreCase);
            var displayName = $"{rank}. {personalName}";
            var normalized = ForumLoaService.NormalizeName(displayName);
            if (normalized.Length == 0 || !seen.Add(normalized)) continue;

            if (origin.Length == 0)
            {
                var remainder = line[(memberMatch.Index + memberMatch.Length)..];
                var inlineOrigin = InlineOriginRx.Match(remainder);
                if (inlineOrigin.Success)
                    origin = Regex.Replace(inlineOrigin.Groups["origin"].Value,
                        @"\s*[-/]\s*", "-").Trim();
            }

            result.Add(new PatrolRosterMember(displayName, normalized, origin));
        }
        return result;
    }

    private static string DecodeBody(string html)
    {
        var withLines = Regex.Replace(html, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        withLines = Regex.Replace(withLines, @"</(?:p|li|div|tr|h[1-6])\s*>", "\n",
            RegexOptions.IgnoreCase);
        var decoded = WebUtility.HtmlDecode(TagRx.Replace(withLines, ""))
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Replace('\u00a0', ' ');
        return Regex.Replace(decoded, @"[ \t]+", " ");
    }
}
