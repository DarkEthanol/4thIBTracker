using System.Text.RegularExpressions;
using FourthIBTracker.Models;

namespace FourthIBTracker.Services;

/// <summary>
/// Converts raw ranges from the unit's sheets into typed models.
/// Layout constants were reverse-engineered from the real spreadsheets —
/// if someone restructures a sheet, this is the file to fix.
/// </summary>
public static class SheetParsers
{
    private static string S(IList<object> row, int i) =>
        i < row.Count ? row[i]?.ToString()?.Trim() ?? "" : "";

    // ---------------------------------------------------------------- Discipline
    /// <summary>Expects range 'Disciplinaries'!A2:G (header row skipped).</summary>
    public static List<Disciplinary> ParseDisciplinaries(IList<IList<object>> rows)
    {
        var list = new List<Disciplinary>();
        foreach (var r in rows)
        {
            if (S(r, 0).Length == 0) continue;
            list.Add(new Disciplinary(
                S(r, 0), S(r, 1), S(r, 2), S(r, 3),
                ParseDate(S(r, 4)), ParseDate(S(r, 5)), S(r, 6)));
        }
        return list;

        static DateTime? ParseDate(string s) =>
            DateTime.TryParse(s, out var d) ? d : null;
    }

    // ---------------------------------------------------------------- Courses
    /// <summary>
    /// Discovers every course matrix block by its Name/Rank and ACMT headers.
    /// Section markers are searched above each block and course columns are all
    /// non-empty headers to the right of ACMT, wherever the table is positioned.
    /// Repeated header variants are merged (for example Driver/Drivers), and
    /// blocks belonging to a different configured platoon are ignored.
    /// </summary>
    public static (List<CourseRecord> Records, List<string> CourseNames)
        ParseCourses(IList<IList<object>> rows, int platoon)
    {
        static string Clean(string value) => Regex.Replace(value, @"\s+", " ").Trim();

        static string HeaderKey(string value) =>
            Regex.Replace(Clean(value).ToLowerInvariant(), @"[^a-z0-9]+", "");

        // A light stem makes harmless wording differences across repeated
        // blocks converge without relying on a fixed list of course names.
        static string CourseKey(string value)
        {
            static string Stem(string word)
            {
                if (word.Length > 5 && word.EndsWith("ing")) return word[..^3];
                if (word.Length > 5 && word.EndsWith("ers")) return word[..^3];
                if (word.Length > 4 && word.EndsWith("er")) return word[..^2];
                if (word.Length > 4 && word.EndsWith('s')) return word[..^1];
                return word;
            }

            return string.Concat(Regex.Matches(Clean(value).ToLowerInvariant(), @"[a-z0-9]+")
                .Select(m => Stem(m.Value)));
        }

        static bool TrySection(string value, int wantedPlatoon, out string? section)
        {
            section = null;
            value = Clean(value);
            if (value.Equals("HQ", StringComparison.OrdinalIgnoreCase))
            {
                section = "HQ";
                return true;
            }

            var numbered = Regex.Match(value,
                @"^(?<pl>\d+)\s*[-./]\s*(?<sec>\d+|HQ)$", RegexOptions.IgnoreCase);
            if (numbered.Success)
            {
                if (int.Parse(numbered.Groups["pl"].Value) == wantedPlatoon)
                    section = numbered.Groups["sec"].Value.Equals("HQ", StringComparison.OrdinalIgnoreCase)
                        ? "HQ"
                        : $"{wantedPlatoon}-{numbered.Groups["sec"].Value}";
                return true;
            }

            var named = Regex.Match(value, @"^(?<sec>\d+)\s*(Section|Sec)$",
                RegexOptions.IgnoreCase);
            if (named.Success)
            {
                section = $"{wantedPlatoon}-{named.Groups["sec"].Value}";
                return true;
            }
            return false;
        }

        static string? FindSection(IList<IList<object>> values, int headerRow, int wantedPlatoon)
        {
            for (int r = headerRow - 1; r >= 0; r--)
                for (int c = 0; c < values[r].Count; c++)
                    if (TrySection(S(values[r], c), wantedPlatoon, out var section))
                        return section; // null means the closest block belongs to another platoon
            return null;
        }

        var rawBlocks = new List<(
            int HeaderRow, string Section, int NameCol, int AcmtCol,
            List<(int Col, string Key, string Variant)> Courses)>();
        var allHeaderRows = new List<int>();

        for (int r = 0; r < rows.Count; r++)
        {
            int nameCol = -1, acmtCol = -1;
            for (int c = 0; c < rows[r].Count; c++)
            {
                var key = HeaderKey(S(rows[r], c));
                if (key is "namerank" or "rankname") nameCol = c;
                else if (key == "acmt") acmtCol = c;
            }
            if (nameCol < 0 || acmtCol < 0) continue;
            allHeaderRows.Add(r);

            var section = FindSection(rows, r, platoon);
            if (section == null) continue;

            var courses = new List<(int, string, string)>();
            for (int c = acmtCol + 1; c < rows[r].Count; c++)
            {
                var name = Clean(S(rows[r], c));
                var key = CourseKey(name);
                if (name.Length > 0 && key.Length > 0)
                    courses.Add((c, key, name));
            }
            if (courses.Count > 0)
                rawBlocks.Add((r, section, nameCol, acmtCol, courses));
        }

        if (rawBlocks.Count == 0)
            throw new InvalidOperationException(
                $"No course blocks were found for platoon {platoon}. Expected Name/Rank and ACMT headers.");

        var courseKeys = new List<string>();
        var variants = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var block in rawBlocks)
            foreach (var course in block.Courses)
            {
                if (!variants.TryGetValue(course.Key, out var counts))
                {
                    variants[course.Key] = counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    courseKeys.Add(course.Key);
                }
                counts[course.Variant] = counts.GetValueOrDefault(course.Variant) + 1;
            }

        var displayByKey = courseKeys.ToDictionary(
            key => key,
            key => variants[key]
                .OrderByDescending(v => v.Value)
                .ThenBy(v => v.Key, StringComparer.OrdinalIgnoreCase)
                .First().Key,
            StringComparer.OrdinalIgnoreCase);
        var courseNames = courseKeys.Select(key => displayByKey[key]).ToList();

        var records = new List<CourseRecord>();
        for (int b = 0; b < rawBlocks.Count; b++)
        {
            var block = rawBlocks[b];
            // Even a block belonging to another platoon is a hard boundary;
            // never let its member rows bleed into the preceding block.
            int endRow = allHeaderRows
                .Where(r => r > block.HeaderRow)
                .Select(r => (int?)r)
                .FirstOrDefault() ?? rows.Count;
            for (int r = block.HeaderRow + 1; r < endRow; r++)
            {
                var name = Clean(S(rows[r], block.NameCol));
                if (name.Length == 0 || Regex.IsMatch(name,
                        @"^(Vacant|Empty|N/?A|-)$", RegexOptions.IgnoreCase))
                    continue;

                var rec = new CourseRecord
                {
                    Section = block.Section,
                    Name = name,
                    Acmt = Clean(S(rows[r], block.AcmtCol)),
                };
                foreach (var course in courseNames) rec.Courses[course] = "";
                foreach (var course in block.Courses)
                    rec.Courses[displayByKey[course.Key]] = Clean(S(rows[r], course.Col));
                records.Add(rec);
            }
        }

        return (records, courseNames);
    }

    // ---------------------------------------------------------------- Attendance
    public record AttendanceBlock(string Name, int NameCol0, int FirstWeekCol0, int FirstRow1, int LastRow1);

    /// <summary>Column/row layout of a platoon attendance tab (0-based cols, 1-based rows).</summary>
    public static readonly AttendanceBlock[] AttendanceBlocks =
    {
        new("Pl HQ",     9,  10, 3,  4),   // names col J, weeks K-O
        new("1 Section", 1,  2,  12, 21),  // names col B, weeks C-G
        new("2 Section", 9,  10, 12, 21),  // names col J, weeks K-O
        new("3 Section", 17, 18, 12, 21),  // names col R, weeks S-W
    };

    /// <summary>Values must cover A1 onward so indices line up (request range 'A1:W25').</summary>
    public static List<AttendanceRow> ParseAttendance(IList<IList<object>> values)
    {
        var result = new List<AttendanceRow>();
        foreach (var block in AttendanceBlocks)
        {
            for (int row1 = block.FirstRow1; row1 <= block.LastRow1; row1++)
            {
                int r = row1 - 1;
                if (r >= values.Count) break;
                var name = S(values[r], block.NameCol0);
                if (name.Length == 0 || name.StartsWith("Total")) continue;

                var ar = new AttendanceRow
                { SectionName = block.Name, SoldierName = name, SheetRow = row1 };

                for (int w = 0; w < 5; w++)
                    ar.Weeks[w] = AttendanceStatusExtensions.FromText(
                        S(values[r], block.FirstWeekCol0 + w));
                result.Add(ar);
            }
        }
        return result;
    }

    // ---------------------------------------------------------------- CEFO
    /// <summary>
    /// Expects the whole 'Infantry CEFO ' grid (e.g. A1:AZ120).
    /// Loadouts are vertical runs of non-empty cells: [group,] role, then "Nx item" lines.
    /// </summary>
    public static List<CefoRole> ParseCefo(IList<IList<object>> rows)
    {
        var roles = new List<CefoRole>();
        int maxCols = rows.Count == 0 ? 0 : rows.Max(r => r.Count);
        var itemRx = new Regex(@"^\d+x\s", RegexOptions.IgnoreCase);

        for (int c = 0; c < maxCols; c++)
        {
            int r = 0;
            while (r < rows.Count)
            {
                // find start of a run
                while (r < rows.Count && S(rows[r], c).Length == 0) r++;
                int runStart = r;
                var run = new List<string>();
                while (r < rows.Count && S(rows[r], c).Length > 0)
                    run.Add(S(rows[r++], c));

                if (run.Count < 4) continue;
                int firstItem = run.FindIndex(v => itemRx.IsMatch(v));
                if (firstItem < 0) continue;

                // Some CEFO tabs put the role heading above the item run with a
                // blank spacer row. Recover that heading instead of requiring it
                // to be part of the same vertical run.
                if (firstItem == 0)
                {
                    string heading = "";
                    for (int hr = runStart - 1; hr >= Math.Max(0, runStart - 4); hr--)
                    {
                        heading = S(rows[hr], c);
                        if (heading.Length > 0) break;
                    }

                    if (heading.Length == 0) continue;
                    var headingLines = heading
                        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    var separatedRole = headingLines.LastOrDefault() ?? heading;
                    var separatedGroup = headingLines.Length > 1
                        ? string.Join(" ", headingLines.Take(headingLines.Length - 1))
                        : "";
                    if (separatedRole.Contains("IMPORTANT", StringComparison.OrdinalIgnoreCase)) continue;
                    roles.Add(new CefoRole(separatedGroup, separatedRole, run));
                    continue;
                }

                var role = run[firstItem - 1];
                var group = firstItem >= 2 ? run[firstItem - 2] : "";

                // Some sheets put the group header one column to the side of the
                // loadout column — search the row above the role for it.
                if (group.Length == 0)
                {
                    int roleRow = runStart + firstItem - 1;
                    for (int dc = -2; dc <= 2 && group.Length == 0 && roleRow > 0; dc++)
                        if (c + dc >= 0) group = S(rows[roleRow - 1], c + dc);
                }

                if (role.Contains("IMPORTANT", StringComparison.OrdinalIgnoreCase)) continue;
                roles.Add(new CefoRole(group, role, run.Skip(firstItem).ToList()));
            }
        }
        return roles;
    }

    // ---------------------------------------------------------------- ORBAT (unit roster)
    // A person cell = rank/prefix followed by "X. Surname" (the prefix is required,
    // so headers like "1 Troop, SCOTS DG" don't false-match).
    private static readonly Regex OrbatNameRx = new(@"([\p{L}]\.\s*[\p{L}'\-]+(?:\s+[\p{L}'\-]+)*)\s*$");
    // Cells that sit inside a block but aren't its header: slot numbers,
    // position labels, "5 (Crew)" style labels, totals.
    private static readonly Regex OrbatLabelRx = new(
        @"^(\d+(\.\d+)?|IC|2IC|OC|CO|CSM|CQMS|RSM|Sgt|Pl Cmdr|Adm Off|OPs Off|Jr Off|Total.*|\d+\s*\(.*\))$",
        RegexOptions.IgnoreCase);

    /// <summary>
    /// Expects the SuT Record 'ORBAT 2.0' grid (e.g. A1:AD90).
    /// Name-first strategy: find every ranked name in the grid, then walk UP from it
    /// (same column ±1) to the nearest block header, and map that header to one of the
    /// fill-in form's "Where are they from?" options. Origin is "" when no header maps.
    /// </summary>
    public static Dictionary<string, string> ParseOrbatSoldiers(IList<IList<object>> rows)
    {
        var soldiers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int maxCols = rows.Count == 0 ? 0 : rows.Max(r => r.Count);

        for (int r = 0; r < rows.Count; r++)
        {
            for (int c = 0; c < maxCols; c++)
            {
                var m = MatchName(S(rows[r], c));
                if (m == null) continue;
                soldiers.TryAdd(m, FindOrigin(rows, r, c) ?? "");
            }
        }
        return soldiers;

        static string? MatchName(string cell)
        {
            var m = OrbatNameRx.Match(cell);
            return m.Success && m.Index > 0 ? m.Groups[1].Value : null;
        }

        static string? FindOrigin(IList<IList<object>> rows, int r, int c)
        {
            for (int rr = r - 1; rr >= 0; rr--)
            {
                for (int cc = c - 1; cc <= c + 1; cc++)
                {
                    if (cc < 0) continue;
                    var s = S(rows[rr], cc);
                    if (s.Length == 0 || MatchName(s) != null || OrbatLabelRx.IsMatch(s)) continue;
                    var o = HeaderToOrigin(s);
                    if (o != null) return o;
                }
            }
            return null;
        }
    }

    /// <summary>
    /// The configured platoon's sections from the SuT ORBAT 2.0 grid, for
    /// comparison with the website ORBAT. Keys: HQ / 1 Section / 2 Section / 3 Section.
    /// </summary>
    public static Dictionary<string, List<string>> ParsePlatoonSections(
        IList<IList<object>> rows, int platoon)
    {
        var wanted = new (string Key, Regex HeaderRx)[]
        {
            ("HQ",        new Regex($@"{platoon}\s*Pl\s*HQ", RegexOptions.IgnoreCase)),
            ("1 Section", new Regex($@"{platoon}\s*Pl.*1\s*Section", RegexOptions.IgnoreCase)),
            ("2 Section", new Regex($@"{platoon}\s*Pl.*2\s*Section", RegexOptions.IgnoreCase)),
            ("3 Section", new Regex($@"{platoon}\s*Pl.*3\s*Section", RegexOptions.IgnoreCase)),
        };
        var result = wanted.ToDictionary(w => w.Key, _ => new List<string>());
        int maxCols = rows.Count == 0 ? 0 : rows.Max(r => r.Count);

        for (int r = 0; r < rows.Count; r++)
        {
            for (int c = 0; c < maxCols; c++)
            {
                var h = S(rows[r], c);
                if (h.Length == 0 || OrbatNameRx.IsMatch(h)) continue;
                var match = wanted.FirstOrDefault(w => w.HeaderRx.IsMatch(h));
                if (match.Key == null) continue;

                int blanks = 0;
                for (int rr = r + 1; rr < rows.Count && blanks < 2; rr++)
                {
                    var cell = S(rows[rr], c + 1);
                    if (cell.Length == 0) { blanks++; continue; }
                    blanks = 0;
                    var m = OrbatNameRx.Match(cell);
                    if (m.Success && m.Index > 0) result[match.Key].Add(m.Groups[1].Value);
                }
            }
        }
        return result;
    }

    // ------------------------------------------------------- Campaign medals
    private static readonly int[] CampaignMedalThresholds = [5, 10, 15, 20, 25, 30];

    /// <summary>
    /// Cross-checks the campaign medal workbook's all-soldier Outcomes table and
    /// corrected Accum Medals lists with the configured platoon's parsed ORBAT.
    /// Headers are discovered by their labels; no row or column positions are fixed.
    /// The award workbook stores only each soldier's highest awarded tier, so all
    /// lower tiers are implicitly already awarded.
    /// </summary>
    public static CampaignMedalCheckResult ParseCampaignMedals(
        IList<IList<object>> outcomesRows,
        IList<IList<object>> awardedRows,
        Dictionary<string, List<string>> platoonSections)
    {
        static int FindColumn(IList<object> row, string heading)
        {
            for (var c = 0; c < row.Count; c++)
                if (CleanCell(S(row, c)).Equals(heading, StringComparison.OrdinalIgnoreCase))
                    return c;
            return -1;
        }

        var outcomesHeader = -1;
        var maxCol = -1;
        var nameCol = -1;
        for (var r = 0; r < outcomesRows.Count; r++)
        {
            var foundMax = FindColumn(outcomesRows[r], "MAX");
            var foundName = FindColumn(outcomesRows[r], "Name");
            if (foundMax < 0 || foundName < 0) continue;
            outcomesHeader = r;
            maxCol = foundMax;
            nameCol = foundName;
            break;
        }
        if (outcomesHeader < 0)
            throw new InvalidOperationException(
                "Could not find the campaign medal Outcomes headers (MAX and Name).");

        var deploymentCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var r = outcomesHeader + 1; r < outcomesRows.Count; r++)
        {
            var key = MedalNameKey(S(outcomesRows[r], nameCol));
            if (key.Length == 0 || !int.TryParse(CleanCell(S(outcomesRows[r], maxCol)), out var count))
                continue;
            deploymentCounts[key] = Math.Max(count, deploymentCounts.GetValueOrDefault(key));
        }

        static int? ThresholdFromHeading(string value)
        {
            value = CleanCell(value);
            if (Regex.IsMatch(value,
                    @"^Accumulated Campaign Service Medal(?:\s*\(5\))?$",
                    RegexOptions.IgnoreCase))
                return 5;
            var match = Regex.Match(value,
                @"^(10|15|20|25|30)\s*Deployments$", RegexOptions.IgnoreCase);
            return match.Success ? int.Parse(match.Groups[1].Value) : null;
        }

        // The sheet contains RAW and Name Corrected copies of these headers.
        // Taking the right-most matching column selects the corrected list while
        // still supporting versions that contain only one block.
        var awardsHeader = -1;
        Dictionary<int, int> awardColumns = new();
        for (var r = 0; r < awardedRows.Count; r++)
        {
            var found = new Dictionary<int, int>();
            for (var c = 0; c < awardedRows[r].Count; c++)
                if (ThresholdFromHeading(S(awardedRows[r], c)) is { } threshold)
                    found[threshold] = c;
            if (found.Count <= awardColumns.Count) continue;
            awardsHeader = r;
            awardColumns = found;
        }
        if (awardsHeader < 0 || CampaignMedalThresholds.Any(t => !awardColumns.ContainsKey(t)))
            throw new InvalidOperationException(
                "Could not find all campaign medal award columns (5, 10, 15, 20, 25 and 30).");

        var highestAwarded = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (threshold, column) in awardColumns)
            for (var r = awardsHeader + 1; r < awardedRows.Count; r++)
            {
                var key = MedalNameKey(S(awardedRows[r], column));
                if (key.Length > 0)
                    highestAwarded[key] = Math.Max(
                        threshold, highestAwarded.GetValueOrDefault(key));
            }

        var due = new List<CampaignMedalDue>();
        var unmatched = new List<CampaignMedalUnmatched>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var orbatCount = 0;

        foreach (var (section, members) in platoonSections)
            foreach (var member in members)
            {
                var key = MedalNameKey(member);
                if (key.Length == 0 || !seen.Add(key)) continue;
                orbatCount++;

                if (!deploymentCounts.TryGetValue(key, out var deployments))
                {
                    unmatched.Add(new CampaignMedalUnmatched(section, member));
                    continue;
                }

                var earnedAt = CampaignMedalThresholds
                    .Where(threshold => deployments >= threshold)
                    .DefaultIfEmpty(0)
                    .Max();
                var awardedAt = highestAwarded.GetValueOrDefault(key);
                if (earnedAt > awardedAt)
                    due.Add(new CampaignMedalDue(
                        section, member, deployments, earnedAt, awardedAt));
            }

        return new CampaignMedalCheckResult(due, unmatched, orbatCount);
    }

    private static string MedalNameKey(string value)
    {
        var match = Regex.Match(CleanCell(value),
            @"([\p{L}]\.\s*[\p{L}'\-]+(?:\s+[\p{L}'\-]+)*)\s*$");
        if (!match.Success) return "";
        return Regex.Replace(match.Groups[1].Value.Normalize(), @"\s+", " ")
            .Trim().ToLowerInvariant();
    }

    private static string CleanCell(string value) =>
        Regex.Replace(value.Trim(), @"\s+", " ");

    /// <summary>Block header text → the form's "Where are they from?" option (null = not a header).</summary>
    private static string? HeaderToOrigin(string h)
    {
        var m = Regex.Match(h, @"\b(\d)\s*Pl\b|\b(\d)\s*Platoon", RegexOptions.IgnoreCase);
        if (m.Success) return $"{(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)} Platoon";
        if (Regex.IsMatch(h, @"Trp|Troop", RegexOptions.IgnoreCase)) return "1 Troop";
        if (Regex.IsMatch(h, @"12\s*MI", RegexOptions.IgnoreCase)) return "12 MI";
        if (Regex.IsMatch(h, @"JAC|Sqn|No\.?\s*\d+|Pilot|JHC|Air Land|ALIC", RegexOptions.IgnoreCase)) return "JAC";
        if (Regex.IsMatch(h, @"MED", RegexOptions.IgnoreCase)) return "3 MED";
        if (Regex.IsMatch(h, @"Helmand|ARC|ELOA", RegexOptions.IgnoreCase)) return "ARC 1/ELOA";
        if (Regex.IsMatch(h, @"Coy|HQ|YORKS", RegexOptions.IgnoreCase)) return "Coy HQ/BGHQ";
        return null;
    }

    // ---------------------------------------------------------------- Logistics order
    public record LogiOrderItem(string Item, int Total, string Breakdown);

    /// <summary>
    /// Finds the logistics table by its section headers rather than fixed row or
    /// column numbers. The item column is the nearest column to the left of the
    /// discovered quantity columns. Returns only items with something ordered.
    /// </summary>
    public static List<LogiOrderItem> ParseLogiOverview(IList<IList<object>> rows)
    {
        static string? QuantityLabel(string value)
        {
            value = Regex.Replace(value.Trim(), @"\s+", " ");
            var section = Regex.Match(value, @"^(?<n>\d+)\s*(Section|Sec)$",
                RegexOptions.IgnoreCase);
            if (section.Success) return $"{section.Groups["n"].Value} Sec";
            return Regex.IsMatch(value, @"^(Pl(atoon)?\s*)?HQ$", RegexOptions.IgnoreCase)
                ? "Pl HQ"
                : null;
        }

        int headerRow = -1;
        List<(int Col, string Label)> quantityColumns = new();
        int maxCols = rows.Count == 0 ? 0 : rows.Max(r => r.Count);

        // Prefer the row with the most recognised section columns. Requiring
        // at least two avoids mistaking a category or item name for the table header.
        for (int r = 0; r < rows.Count; r++)
        {
            var found = new List<(int Col, string Label)>();
            for (int c = 0; c < maxCols; c++)
                if (QuantityLabel(S(rows[r], c)) is { } label)
                    found.Add((c, label));

            if (found.Count >= 2 && found.Count > quantityColumns.Count)
            {
                headerRow = r;
                quantityColumns = found;
            }
        }

        if (headerRow < 0)
            throw new InvalidOperationException(
                "Could not find the logistics table headers (section columns and Pl HQ).");

        int firstQuantityCol = quantityColumns.Min(c => c.Col);
        int itemCol = -1;

        // Some versions label this column, others leave its header blank.
        // Honour a descriptive header when present, otherwise use the nearest
        // column on the left, which is where the catalogue entries live.
        for (int c = firstQuantityCol - 1; c >= 0; c--)
            if (Regex.IsMatch(S(rows[headerRow], c),
                    @"^(Item|Items|Equipment|Stores|Description)$", RegexOptions.IgnoreCase))
            {
                itemCol = c;
                break;
            }
        if (itemCol < 0) itemCol = firstQuantityCol - 1;
        if (itemCol < 0)
            throw new InvalidOperationException(
                "Could not determine the logistics item-name column.");

        var result = new List<LogiOrderItem>();
        for (int r = headerRow + 1; r < rows.Count; r++)
        {
            var row = rows[r];
            var item = S(row, itemCol);
            if (item.Length == 0) continue;
            var parts = new List<string>();
            int total = 0;
            foreach (var (col, label) in quantityColumns)
            {
                if (int.TryParse(S(row, col), out var n) && n > 0)
                {
                    total += n;
                    parts.Add($"{label} ×{n}");
                }
            }
            if (total > 0)
                result.Add(new LogiOrderItem(item, total, string.Join(", ", parts)));
        }
        return result;
    }

    // ---------------------------------------------------------------- NCO course tracker
    public record NcoCheck(string Position, bool Done);

    /// <summary>
    /// Expects the NCO tracker year tab, range A1:M60.
    /// Rows: position code in col A (for example "4-1-C"), then Jan..Dec as TRUE/FALSE.
    /// Returns the given positions' status for the given month (1-12).
    /// </summary>
    public static List<NcoCheck> ParseNcoMonth(
        IList<IList<object>> rows, IEnumerable<string> positions, int month)
    {
        var wanted = new HashSet<string>(positions, StringComparer.OrdinalIgnoreCase);
        var result = new List<NcoCheck>();
        foreach (var row in rows)
        {
            var pos = S(row, 0);
            if (!wanted.Contains(pos)) continue;
            var v = S(row, month); // col B = Jan = index 1
            result.Add(new NcoCheck(pos,
                v.Equals("TRUE", StringComparison.OrdinalIgnoreCase)));
        }
        return result;
    }

}
