using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FourthIBTracker.Services;

namespace FourthIBTracker.ViewModels;

public partial class PlatoonAttendanceViewModel : ObservableObject
{
    private readonly AppConfig _config;

    public Func<string, Task<string>>? FetchHtml { get; set; }
    public Func<IReadOnlyList<string>, Task<IReadOnlyList<string>>>? FetchHtmlBatch { get; set; }

    public ObservableCollection<WebsiteAttendanceSection> Sections { get; } = new();
    public IReadOnlyList<WebsiteAttendanceMark> Legend { get; } =
    [
        new("", WebsiteAttendanceStatus.Present),
        new("", WebsiteAttendanceStatus.Late),
        new("", WebsiteAttendanceStatus.Absent),
        new("", WebsiteAttendanceStatus.Excused),
        new("", WebsiteAttendanceStatus.Reserves),
        new("", WebsiteAttendanceStatus.NotRequired),
    ];

    [ObservableProperty] private WebsiteAttendanceSection? selectedSection;
    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private string? error;
    [ObservableProperty] private string statusMessage =
        "Open this page to load the attendance tracker records.";

    public bool HasLoaded { get; private set; }

    public PlatoonAttendanceViewModel(AppConfig config) => _config = config;

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (FetchHtml is null || IsLoading) return;
        IsLoading = true;
        Error = null;
        try
        {
            var attendanceUrl = PlatoonAttendanceService.AttendanceUrl(_config);
            StatusMessage = $"Finding {_config.Platoon.Name} sections…";
            var indexHtml = await FetchHtml(attendanceUrl);
            var links = PlatoonAttendanceService.FindPlatoonSections(
                indexHtml, attendanceUrl, _config.Platoon.Number);
            if (links.Count == 0)
            {
                throw new InvalidOperationException(
                    $"No attendance sections were found for {_config.Platoon.Name}. " +
                    "Log in on the 4thIB Website tab, then refresh again.");
            }

            StatusMessage = $"Loading {links.Count} attendance section(s)…";
            IReadOnlyList<string> pages;
            if (FetchHtmlBatch is not null)
                pages = await FetchHtmlBatch(links.Select(link => link.Url).ToList());
            else
            {
                var sequential = new List<string>();
                foreach (var link in links) sequential.Add(await FetchHtml(link.Url));
                pages = sequential;
            }
            if (pages.Count != links.Count)
                throw new InvalidOperationException("The attendance website returned an incomplete page batch.");

            var parsed = links.Select((link, index) =>
                PlatoonAttendanceService.ParseSection(pages[index], link)).ToList();
            var selectedName = SelectedSection?.Name;
            Sections.Clear();
            foreach (var section in parsed) Sections.Add(section);
            SelectedSection = Sections.FirstOrDefault(section => section.Name == selectedName) ??
                              Sections.FirstOrDefault();
            HasLoaded = true;

            var records = Sections.Sum(section => section.Events.Count);
            StatusMessage = $"{records} record(s) across {Sections.Count} {_config.Platoon.Name} " +
                            $"section(s), refreshed at {DateTime.Now:HH:mm}.";
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }
}
