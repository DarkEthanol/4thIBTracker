using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FourthIBTracker.Services;

namespace FourthIBTracker.ViewModels;

public record LinkItem(string Name, string Description, string Url);
public record LinkGroup(string Name, ObservableCollection<LinkItem> Links);

public partial class LinksViewModel : ObservableObject
{
    private readonly GoogleSheetsService _sheets;
    private readonly AppConfig _config;

    public ObservableCollection<LinkGroup> Groups { get; } = new();

    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private string? error;
    [ObservableProperty] private string searchText = "";

    private readonly List<LinkGroup> _all = new();

    public LinksViewModel(GoogleSheetsService sheets, AppConfig config)
    { _sheets = sheets; _config = config; }

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsLoading = true; Error = null;
        try
        {
            _all.Clear();

            var main = _config.Sheet("DocuFortMain");
            var pl = _config.Sheet("DocuFortPlatoon");

            // These are independent tabs in the same workbook. Fetch both at
            // once; the sheets service deduplicates their shared metadata call.
            var mainCellsTask = ReadLinksAsync(main, "A1:N20");
            var plCellsTask = ReadLinksAsync(pl, "A1:E60");
            await Task.WhenAll(mainCellsTask, plCellsTask);

            // Hot list on the Main tab: cells that are themselves links (label ≠ "here").
            var mainCells = await mainCellsTask;
            var hot = new ObservableCollection<LinkItem>();
            foreach (var row in mainCells)
                foreach (var (text, url) in row)
                    if (url != null && text.Length > 0 &&
                        !text.Equals("here", StringComparison.OrdinalIgnoreCase))
                        hot.Add(new LinkItem(text, "", url));
            if (hot.Count > 0) _all.Add(new LinkGroup("Hot List", hot));

            // Platoon document index: B=ID, C=Name, D=Use, E=link ("here").
            var plCells = await plCellsTask;
            var docs = new ObservableCollection<LinkItem>();
            foreach (var row in plCells)
            {
                string name = row.Length > 2 ? row[2].Text : "";
                string use = row.Length > 3 ? row[3].Text : "";
                string? url = row.Length > 4 ? row[4].Url : null;
                if (url != null && name.Length > 0)
                    docs.Add(new LinkItem(name, use, url));
            }
            if (docs.Count > 0) _all.Add(new LinkGroup($"{_config.Platoon.Name} Documents", docs));

            Apply();

            async Task<(string Text, string? Url)[][]> ReadLinksAsync(
                SheetRef sheet, string range)
            {
                return await _sheets.ReadLinksFromConfiguredTabAsync(
                    sheet.Id, sheet.Tab, range);
            }
        }
        catch (Exception ex) { Error = ex.Message; }
        finally { IsLoading = false; }
    }

    partial void OnSearchTextChanged(string value) => Apply();

    private void Apply()
    {
        Groups.Clear();
        var q = SearchText.Trim();
        foreach (var g in _all)
        {
            var links = g.Links.Where(l =>
                q.Length == 0 ||
                l.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                l.Description.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
            if (links.Count > 0)
                Groups.Add(new LinkGroup(g.Name, new ObservableCollection<LinkItem>(links)));
        }
    }

    [RelayCommand]
    private void Open(LinkItem link)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            { FileName = link.Url, UseShellExecute = true });
        }
        catch (Exception ex) { Error = $"Couldn't open link: {ex.Message}"; }
    }
}
