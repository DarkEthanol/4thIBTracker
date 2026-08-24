using System.IO;
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

    private MonthStats? _current;
    private MonthStats? _previous;

    public bool HasData => _current != null;

    private static string HistoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "4thIBTracker", "address-history.json");

    public AddressViewModel(GoogleSheetsService sheets, AppConfig config)
    { _sheets = sheets; _config = config; }

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsLoading = true; Error = null;
        try
        {
            var att = _config.Sheet("Attendance");
            var rows = await _sheets.ReadValuesFromConfiguredTabAsync(
                att.Id, att.Tab, "A1:X30");

            string Cell(int row1, int col0)
            {
                int r = row1 - 1;
                if (r >= rows.Count) return "";
                var row = rows[r];
                return col0 < row.Count ? row[col0]?.ToString()?.Trim() ?? "" : "";
            }

            static int? Pct(string s)
            {
                s = s.Replace("%", "").Trim();
                if (double.TryParse(s, out var d))
                    return (int)Math.Round(d <= 1 && d > 0 ? d * 100 : d);
                return null;
            }

            var stats = new MonthStats
            {
                Hq = Pct(Cell(6, 15)),       // HQ average
                S1 = Pct(Cell(23, 7)),       // 1 Section average
                S2 = Pct(Cell(23, 15)),      // 2 Section average
                S3 = Pct(Cell(23, 23)),      // 3 Section average
                Overall = Pct(Cell(25, 15)), // platoon overall
            };

            // 100%ers: every soldier whose % cell reads 100.
            foreach (var block in SheetParsers.AttendanceBlocks)
            {
                int pctCol = block.FirstWeekCol0 + 5;
                for (int row1 = block.FirstRow1; row1 <= block.LastRow1; row1++)
                {
                    var name = Cell(row1, block.NameCol0);
                    if (name.Length == 0 || name.StartsWith("Total")) continue;
                    if (Pct(Cell(row1, pctCol)) == 100)
                        stats.HundredPercenters.Add(name);
                }
            }

            _current = stats;
            _previous = LoadSnapshot(DateTime.Today.AddMonths(-1));
            SaveSnapshot(DateTime.Today, stats);
            Regenerate();
            StatusMessage = $"Stats pulled {DateTime.Now:HH:mm}. Edit the text below, then copy.";
        }
        catch (Exception ex) { Error = ex.Message; }
        finally { IsLoading = false; }
    }

    partial void OnExtraNotesChanged(string value) => Regenerate();

    [RelayCommand]
    private void Regenerate()
    {
        if (_current == null) return;
        var month = DateTime.Today.ToString("MMMM");
        var prevMonth = DateTime.Today.AddMonths(-1).ToString("MMMM");
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
