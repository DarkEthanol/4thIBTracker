using System.Windows.Media;

namespace FourthIBTracker.Models;

/// <summary>One row from the Discipline Tracker.</summary>
public record Disciplinary(
    string Recipient,
    string Issuer,
    string Area,
    string Offence,
    DateTime? DateGiven,
    DateTime? DateExpired,
    string Notes)
{
    public bool IsActive => DateExpired.HasValue && DateExpired.Value.Date >= DateTime.Today;
    public int DaysRemaining => IsActive ? (DateExpired!.Value.Date - DateTime.Today).Days : 0;
}

/// <summary>A soldier's row in the Section Courses matrix.</summary>
public class CourseRecord
{
    public string Section { get; init; } = "";
    public string Name { get; init; } = "";
    public string Acmt { get; init; } = "";
    /// <summary>CourseName -> "Complete" | "Not Done" | "Advanced" | ...</summary>
    public Dictionary<string, string> Courses { get; init; } = new();

    public int CompletedCount => Courses.Values.Count(v =>
        v.Equals("Complete", StringComparison.OrdinalIgnoreCase) ||
        v.Equals("Advanced", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Attendance status. The sheet stores these as text values ("Present", "LOA",
/// "Late", "AWOL") and its own conditional formatting applies the colours.
/// </summary>
public enum AttendanceStatus { None, Present, Loa, Late, Awol }

public static class AttendanceStatusExtensions
{
    // Colours mirror the sheet's conditional-formatting rules (for in-app display only).
    private static readonly Dictionary<AttendanceStatus, string> Hex = new()
    {
        // Use the same palette as the website attendance history so the two
        // halves of the combined page read as one tracker.
        [AttendanceStatus.None]    = "#555B60",
        [AttendanceStatus.Present] = "#6AA84F",
        [AttendanceStatus.Loa]     = "#3C78D8",
        [AttendanceStatus.Late]    = "#FFFF00",
        [AttendanceStatus.Awol]    = "#FF0000",
    };

    public static Color ToColor(this AttendanceStatus s) =>
        (Color)ColorConverter.ConvertFromString(Hex[s]);

    /// <summary>The exact text the sheet's conditional formatting expects.</summary>
    public static string ToSheetValue(this AttendanceStatus s) => s switch
    {
        AttendanceStatus.None => "",
        AttendanceStatus.Loa  => "LOA",
        AttendanceStatus.Awol => "AWOL",
        _ => s.ToString(), // Present, Late
    };

    public static AttendanceStatus FromText(string? text) =>
        (text ?? "").Trim().ToLowerInvariant() switch
        {
            "present" => AttendanceStatus.Present,
            "loa"     => AttendanceStatus.Loa,
            "late"    => AttendanceStatus.Late,
            "awol"    => AttendanceStatus.Awol,
            _         => AttendanceStatus.None,
        };
}

/// <summary>One soldier's attendance row: name + up to five week cells.</summary>
public class AttendanceRow
{
    public string SectionName { get; init; } = "";
    public string SoldierName { get; init; } = "";
    /// <summary>Sheet row (1-based) this soldier lives on.</summary>
    public int SheetRow { get; init; }
    public AttendanceStatus[] Weeks { get; } = new AttendanceStatus[5];
}

/// <summary>A CEFO loadout for one role, e.g. "Platoon Sergeant".</summary>
public record CefoRole(string Group, string Role, IReadOnlyList<string> Items);

/// <summary>
/// The highest accumulated campaign medal a soldier has earned but has not yet
/// been recorded as receiving.
/// </summary>
public record CampaignMedalDue(
    string Section,
    string Name,
    int Deployments,
    int DueAt,
    int AwardedAt)
{
    public string Medal => DueAt == 5
        ? "Accumulated Campaign Service Medal (5)"
        : $"{DueAt} Deployments";

    public string PreviousAward => AwardedAt == 0
        ? "No previous medal recorded"
        : AwardedAt == 5
            ? "Last awarded: service medal (5)"
            : $"Last awarded: {AwardedAt} deployments";
}

public record CampaignMedalUnmatched(string Section, string Name);

public record CampaignMedalCheckResult(
    IReadOnlyList<CampaignMedalDue> Due,
    IReadOnlyList<CampaignMedalUnmatched> Unmatched,
    int OrbatCount);
