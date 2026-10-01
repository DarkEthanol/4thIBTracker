using System.IO;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FourthIBTracker.Services;

namespace FourthIBTracker.ViewModels;

public class MonthStats
{
    public int? Overall { get; set; }
    public int? Hq { get; set; }
    public int? S1 { get; set; }
    public int? S2 { get; set; }
    public int? S3 { get; set; }
    public List<string> HundredPercenters { get; set; } = new();
}

internal sealed record AttendanceMonthTable(
    IReadOnlyDictionary<int, MonthStats> Months,
    MonthStats? Placeholders);

public sealed record AddressMonthOption(DateTime Month)
{
    public string Label => Month.ToString("MMMM yyyy");
    public override string ToString() => Label;
}

/// <summary>
/// Builds the monthly "Sergeant's Address" Discord post from the live
/// attendance sheet. Each generation saves a snapshot so next month's post
/// can say "up from X%".
/// </summary>
public partial class AddressViewModel : ObservableObject
{
    private readonly GoogleSheetsService _sheets;
    private readonly AppConfig _config;

    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private string? error;
    [ObservableProperty] private string generatedText = "";
    [ObservableProperty] private string extraNotes = "";
    [ObservableProperty] private string statusMessage = "";
    [ObservableProperty] private AddressMonthOption? selectedReportingMonth;

    public ObservableCollection<AddressMonthOption> ReportingMonths { get; } = new();

    private MonthStats? _current;
    private MonthStats? _previous;
    private MonthStats? _liveStats;
    private AttendanceMonthTable _sheetMonthTable = new(
        new Dictionary<int, MonthStats>(), null);

    public bool HasData => _current != null;

    private static string HistoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "4thIBTracker", "address-history.json");

    public AddressViewModel(GoogleSheetsService sheets, AppConfig config)
    {
        _sheets = sheets;
        _config = config;

        var currentMonth = MonthStart(DateTime.Today);
        for (var offset = 0; offset < 24; offset++)
            ReportingMonths.Add(new AddressMonthOption(currentMonth.AddMonths(-offset)));
        selectedReportingMonth = ReportingMonths.First(option =>
            option.Month == DefaultReportingMonth(DateTime.Today));
    }

    internal static DateTime DefaultReportingMonth(DateTime today) =>
        MonthStart(today).AddMonths(-1);

    private static DateTime MonthStart(DateTime date) => new(date.Year, date.Month, 1);

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsLoading = true; Error = null;
        try
        {
            var att = _config.Sheet("Attendance");
            var rows = await _sheets.ReadValuesFromConfiguredTabAsync(
                att.Id, att.Tab, "A1:Z150");

            string Cell(int row1, int col0)
            {
                int r = row1 - 1;
                if (r >= rows.Count) return "";
                var row = rows[r];
                return col0 < row.Count ? row[col0]?.ToString()?.Trim() ?? "" : "";
            }

            var stats = new MonthStats
            {
                Hq = ParsePercentage(Cell(6, 15)),       // HQ average
                S1 = ParsePercentage(Cell(23, 7)),       // 1 Section average
                S2 = ParsePercentage(Cell(23, 15)),      // 2 Section average
                S3 = ParsePercentage(Cell(23, 23)),      // 3 Section average
                Overall = ParsePercentage(Cell(25, 15)), // platoon overall
            };

            // 100%ers: every soldier whose % cell reads 100.
            foreach (var block in SheetParsers.AttendanceBlocks)
            {
                int pctCol = block.FirstWeekCol0 + 5;
                for (int row1 = block.FirstRow1; row1 <= block.LastRow1; row1++)
                {
                    var name = Cell(row1, block.NameCol0);
                    if (name.Length == 0 || name.StartsWith("Total")) continue;
                    if (ParsePercentage(Cell(row1, pctCol)) == 100)
                        stats.HundredPercenters.Add(name);
                }
            }

            _liveStats = stats;
            _sheetMonthTable = ParseMonthlyAttendanceTable(rows);
            ApplySelectedMonth();
        }
        catch (Exception ex) { Error = ex.Message; }
        finally { IsLoading = false; }
    }

    partial void OnExtraNotesChanged(string value) => Regenerate();

    partial void OnSelectedReportingMonthChanged(AddressMonthOption? value)
    {
        if (value is null) return;
        ApplySelectedMonth();
    }

    private void ApplySelectedMonth()
    {
        if (_liveStats == null) return;

        var reportingMonth = SelectedReportingMonth?.Month ??
                             DefaultReportingMonth(DateTime.Today);
        var defaultMonth = DefaultReportingMonth(DateTime.Today);
        var tableYear = defaultMonth.Year;
        var source = "saved local history";

        if (reportingMonth.Year == tableYear &&
            _sheetMonthTable.Months.TryGetValue(reportingMonth.Month, out var monthStats))
        {
            _current = monthStats;
            source = $"the {reportingMonth:MMMM} row";
        }
        else if (reportingMonth == defaultMonth)
        {
            _current = _sheetMonthTable.Placeholders ?? _liveStats;
            source = "the live Placeholders row";
        }
        else if (reportingMonth.Year == tableYear)
        {
            // Never reuse a local snapshot for a blank row in the current sheet year.
            // Older builds could save the live figures under whichever month happened
            // to be selected, which is the bug this lookup replaces.
            _current = null;
            source = $"the blank {reportingMonth:MMMM} row";
        }
        else
        {
            _current = LoadSnapshot(reportingMonth);
        }

        var previousMonth = reportingMonth.AddMonths(-1);
        if (previousMonth.Year == tableYear)
            _previous = _sheetMonthTable.Months.TryGetValue(previousMonth.Month, out var previous)
                ? previous
                : null;
        else
            _previous = LoadSnapshot(previousMonth);

        OnPropertyChanged(nameof(HasData));
        if (_current == null)
        {
            GeneratedText = "";
            StatusMessage = $"No attendance figures are recorded in {source}.";
            return;
        }

        SaveSnapshot(reportingMonth, _current);
        Regenerate();
        StatusMessage = $"Stats for {reportingMonth:MMMM yyyy} loaded from {source} at " +
                        $"{DateTime.Now:HH:mm}. Edit the text below, then copy.";
    }

    internal static AttendanceMonthTable ParseMonthlyAttendanceTable(
        IList<IList<object>> rows)
    {
        static string Cell(IList<object> row, int col) =>
            col >= 0 && col < row.Count ? row[col]?.ToString()?.Trim() ?? "" : "";

        static string HeaderKey(string value) => new(
            value.Where(char.IsLetterOrDigit)
                .Select(char.ToLowerInvariant)
                .ToArray());

        var monthNames = DateTimeFormatInfo.InvariantInfo.MonthNames
            .Select((name, index) => (Name: name, Month: index + 1))
            .Where(item => item.Name.Length > 0)
            .ToDictionary(item => item.Name, item => item.Month,
                StringComparer.OrdinalIgnoreCase);

        for (var headerRow = 0; headerRow < rows.Count; headerRow++)
        {
            var headers = rows[headerRow]
                .Select((value, col) => (Key: HeaderKey(value?.ToString() ?? ""), Col: col))
                .Where(item => item.Key.Length > 0)
                .GroupBy(item => item.Key)
                .ToDictionary(group => group.Key, group => group.First().Col);

            if (!headers.TryGetValue("month", out var monthCol) ||
                !headers.TryGetValue("platoonaverage", out var overallCol) ||
                !headers.TryGetValue("hq", out var hqCol) ||
                !headers.TryGetValue("1section", out var s1Col) ||
                !headers.TryGetValue("2section", out var s2Col) ||
                !headers.TryGetValue("3section", out var s3Col))
                continue;

            headers.TryGetValue("100ers", out var hundredCol);
            var months = new Dictionary<int, MonthStats>();
            MonthStats? placeholders = null;

            for (var rowIndex = headerRow + 1; rowIndex < rows.Count; rowIndex++)
            {
                var row = rows[rowIndex];
                var label = Cell(row, monthCol);
                if (!label.Equals("Placeholders", StringComparison.OrdinalIgnoreCase) &&
                    !monthNames.TryGetValue(label, out _))
                    continue;

                var stats = new MonthStats
                {
                    Overall = ParsePercentage(Cell(row, overallCol)),
                    Hq = ParsePercentage(Cell(row, hqCol)),
                    S1 = ParsePercentage(Cell(row, s1Col)),
                    S2 = ParsePercentage(Cell(row, s2Col)),
                    S3 = ParsePercentage(Cell(row, s3Col)),
                    HundredPercenters = hundredCol > 0
                        ? Cell(row, hundredCol)
                            .Split(',', StringSplitOptions.RemoveEmptyEntries |
                                        StringSplitOptions.TrimEntries)
                            .ToList()
                        : new List<string>(),
                };

                var hasData = stats.Overall.HasValue || stats.Hq.HasValue ||
                              stats.S1.HasValue || stats.S2.HasValue ||
                              stats.S3.HasValue || stats.HundredPercenters.Count > 0;
                if (!hasData) continue;

                if (label.Equals("Placeholders", StringComparison.OrdinalIgnoreCase))
                    placeholders = stats;
                else
                    months[monthNames[label]] = stats;
            }

            return new AttendanceMonthTable(months, placeholders);
        }

        return new AttendanceMonthTable(new Dictionary<int, MonthStats>(), null);
    }

    private static int? ParsePercentage(string value)
    {
        value = value.Replace("%", "").Trim();
        if (double.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
            return (int)Math.Round(number <= 1 && number > 0 ? number * 100 : number);
        return null;
    }

    [RelayCommand]
    private void Regenerate()
    {
        if (_current == null) return;
        var reportingMonth = SelectedReportingMonth?.Month ??
                             DefaultReportingMonth(DateTime.Today);
        var month = reportingMonth.ToString("MMMM");
        var prevMonth = reportingMonth.AddMonths(-1).ToString("MMMM");
        int n = _config.Platoon.Number;
        var sb = new StringBuilder();

        sb.AppendLine($"To: @{_config.Platoon.Name}");
        sb.AppendLine($"From: @{_config.Platoon.AddressFrom}");
        sb.AppendLine();
        sb.AppendLine($"Sergeant's Address, {month}");
        sb.AppendLine();
        sb.AppendLine("Hello, the monthly Sergeant's Address is here. For those that are new, " +
                      "this is a short message to show attendance over the last month and any " +
                      "additional things that need to be mentioned.");
        sb.AppendLine();
        sb.AppendLine("**Attendance**");
        sb.AppendLine($"Overall, the Platoon achieved an average attendance of " +
                      $"{Fmt(_current.Overall)} throughout the month of {month}" +
                      $"{Delta(_current.Overall, _previous?.Overall, $" in {prevMonth}")}.");
        sb.AppendLine($"HQ: {Fmt(_current.Hq)}{Delta(_current.Hq, _previous?.Hq)}");
        sb.AppendLine($"{n}-1: {Fmt(_current.S1)}{Delta(_current.S1, _previous?.S1)}");
        sb.AppendLine($"{n}-2: {Fmt(_current.S2)}{Delta(_current.S2, _previous?.S2)}");
        sb.AppendLine($"{n}-3: {Fmt(_current.S3)}{Delta(_current.S3, _previous?.S3)}");
        sb.AppendLine();
        sb.AppendLine("**100%ers**");
        sb.AppendLine(_current.HundredPercenters.Count > 0
            ? string.Join(", ", _current.HundredPercenters)
            : "None this month.");
        if (_current.HundredPercenters.Count > 0) sb.AppendLine("Good work.");
        sb.AppendLine();
        if (ExtraNotes.Trim().Length > 0)
        {
            sb.AppendLine(ExtraNotes.Trim());
            sb.AppendLine();
        }
        sb.AppendLine("This is the end of the monthly round up, I hope you are all well. " +
                      "If you need anything feel free to message me or catch me on TeamSpeak.");
        sb.AppendLine();
        sb.Append(_config.Platoon.SignOff);

        GeneratedText = sb.ToString();

        static string Fmt(int? v) => v.HasValue ? $"{v}%" : "??%";
        static string Delta(int? now, int? prev, string suffix = "")
        {
            if (!now.HasValue || !prev.HasValue || now == prev) return "";
            return now > prev
                ? $", up from {prev}%{suffix}"
                : $", down from {prev}%{suffix}";
        }
    }

    [RelayCommand]
    private void Copy()
    {
        if (GeneratedText.Length == 0) return;
        Clipboard.SetText(GeneratedText);
        StatusMessage = "Copied to clipboard — ready to paste into Discord.";
    }

    // ---- snapshot persistence -------------------------------------------
    private static Dictionary<string, MonthStats> LoadHistory()
    {
        try
        {
            if (File.Exists(HistoryPath))
                return JsonSerializer.Deserialize<Dictionary<string, MonthStats>>(
                    File.ReadAllText(HistoryPath)) ?? new();
        }
        catch { /* corrupt history is not fatal */ }
        return new();
    }

    private static MonthStats? LoadSnapshot(DateTime month) =>
        LoadHistory().TryGetValue(month.ToString("yyyy-MM"), out var s) ? s : null;

    private static void SaveSnapshot(DateTime month, MonthStats stats)
    {
        var all = LoadHistory();
        all[month.ToString("yyyy-MM")] = stats;
        Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);
        File.WriteAllText(HistoryPath,
            JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
    }
}
