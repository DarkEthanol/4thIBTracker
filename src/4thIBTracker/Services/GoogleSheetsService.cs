using System.IO;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;
using Google.Apis.Util.Store;

namespace FourthIBTracker.Services;

/// <summary>
/// Thin async wrapper around the Google Sheets v4 API.
/// Handles OAuth (token cached in %APPDATA%\4thIBTracker), value reads,
/// background-colour reads, and colour/value writes.
/// </summary>
public class GoogleSheetsService
{
    private readonly AppConfig _config;
    private SheetsService? _service;
    private readonly SemaphoreSlim _serviceInitLock = new(1, 1);
    private readonly Dictionary<string, Task<IDictionary<string, int>>> _sheetTabTasks = new();
    private readonly object _sheetTabTasksLock = new();

    public GoogleSheetsService(AppConfig config) => _config = config;

    public async Task<SheetsService> GetServiceAsync()
    {
        if (_service != null) return _service;

        // Dashboard cards load concurrently. Ensure they share one OAuth/service
        // initialization instead of starting several authorization flows at once.
        await _serviceInitLock.WaitAsync();
        try
        {
            if (_service != null) return _service;

            GoogleCredentialsService.EnsureMigrated();
            var credPath = GoogleCredentialsService.CredentialsPath;
            if (!File.Exists(credPath))
                throw new FileNotFoundException(
                    "Google credentials are not installed. Open Settings and choose " +
                    $"‘Import credentials.json’. The per-user location is: {credPath}");

            using var stream = new FileStream(credPath, FileMode.Open, FileAccess.Read);
            var tokenDir = Path.GetDirectoryName(GoogleCredentialsService.CredentialsPath)!;

            var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                GoogleClientSecrets.FromStream(stream).Secrets,
                new[] { SheetsService.Scope.Spreadsheets },
                "user",
                CancellationToken.None,
                new FileDataStore(tokenDir, fullPath: true));

            _service = new SheetsService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = _config.Google.ApplicationName,
            });
            return _service;
        }
        finally
        {
            _serviceInitLock.Release();
        }
    }

    /// <summary>Read a range of cell values, e.g. "4 Pl!B12:G21".</summary>
    public async Task<IList<IList<object>>> ReadValuesAsync(string spreadsheetId, string range)
    {
        var svc = await GetServiceAsync();
        var resp = await svc.Spreadsheets.Values.Get(spreadsheetId, range).ExecuteAsync();
        return resp.Values ?? new List<IList<object>>();
    }

    /// <summary>
    /// Fast path for a configured tab: try its exact name without a metadata
    /// request, then retain the old case/spacing-tolerant resolution as fallback.
    /// </summary>
    public async Task<IList<IList<object>>> ReadValuesFromConfiguredTabAsync(
        string spreadsheetId, string configuredTab, string unqualifiedRange)
    {
        static string Range(string tab, string range) =>
            $"'{tab.Replace("'", "''")}'!{range}";

        try
        {
            return await ReadValuesAsync(
                spreadsheetId, Range(configuredTab, unqualifiedRange));
        }
        catch (Google.GoogleApiException ex)
            when (ex.HttpStatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            var resolved = await ResolveTabAsync(spreadsheetId, configuredTab);
            return await ReadValuesAsync(
                spreadsheetId, Range(resolved, unqualifiedRange));
        }
    }

    /// <summary>
    /// Reads the same unqualified range from the first existing tab in the
    /// supplied list. This avoids loading metadata for a large workbook and
    /// lets platoon-specific sheets try the full name, short name, then a configured
    /// fallback without hard-coding one copy of the app to one tab. When no
    /// sub-range is supplied, Google returns the tab's complete used range.
    /// </summary>
    public async Task<IList<IList<object>>> ReadValuesFromFirstTabAsync(
        string spreadsheetId, IEnumerable<string> tabNames, string? unqualifiedRange = null)
    {
        var attempted = new List<string>();
        Google.GoogleApiException? lastMissingTab = null;

        foreach (var tab in tabNames
                     .Where(t => !string.IsNullOrWhiteSpace(t))
                     .Select(t => t.Trim())
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            attempted.Add(tab);
            try
            {
                var escaped = tab.Replace("'", "''");
                var range = string.IsNullOrWhiteSpace(unqualifiedRange)
                    ? $"'{escaped}'"
                    : $"'{escaped}'!{unqualifiedRange}";
                return await ReadValuesAsync(spreadsheetId, range);
            }
            catch (Google.GoogleApiException ex)
                when (ex.HttpStatusCode == System.Net.HttpStatusCode.BadRequest)
            {
                // A missing tab is reported as an invalid A1 range. Try the
                // next candidate; authentication and spreadsheet errors still
                // bubble immediately instead of being hidden.
                lastMissingTab = ex;
            }
        }

        throw new InvalidOperationException(
            $"None of the expected tabs exist: {string.Join(" | ", attempted)}.",
            lastMissingTab);
    }

    /// <summary>
    /// Read effective background colours for a range.
    /// Returns [row][col] of (red, green, blue) in 0..1, aligned to the top-left of the range.
    /// </summary>
    public async Task<(double? R, double? G, double? B)[][]> ReadBackgroundsAsync(
        string spreadsheetId, string range)
    {
        var svc = await GetServiceAsync();
        var req = svc.Spreadsheets.Get(spreadsheetId);
        req.Ranges = range;
        req.Fields = "sheets(data(rowData(values(effectiveFormat(backgroundColor)))))";
        var resp = await req.ExecuteAsync();

        var rows = resp.Sheets?.FirstOrDefault()?.Data?.FirstOrDefault()?.RowData;
        if (rows == null) return Array.Empty<(double?, double?, double?)[]>();

        return rows.Select(r =>
            (r.Values ?? new List<CellData>()).Select(c =>
            {
                var bg = c.EffectiveFormat?.BackgroundColor;
                return ((double?)(bg?.Red), (double?)(bg?.Green), (double?)(bg?.Blue));
            }).ToArray()
        ).ToArray();
    }

    private async Task<IDictionary<string, int>> GetTabsAsync(string spreadsheetId)
    {
        Task<IDictionary<string, int>> task;
        lock (_sheetTabTasksLock)
        {
            if (!_sheetTabTasks.TryGetValue(spreadsheetId, out task!))
            {
                task = LoadTabsAsync(spreadsheetId);
                _sheetTabTasks[spreadsheetId] = task;
            }
        }

        try
        {
            return await task;
        }
        catch
        {
            // A transient metadata failure must be retryable on the next load.
            lock (_sheetTabTasksLock)
                if (_sheetTabTasks.TryGetValue(spreadsheetId, out var cached) &&
                    ReferenceEquals(cached, task))
                    _sheetTabTasks.Remove(spreadsheetId);
            throw;
        }
    }

    private async Task<IDictionary<string, int>> LoadTabsAsync(string spreadsheetId)
    {
        var svc = await GetServiceAsync();
        var request = svc.Spreadsheets.Get(spreadsheetId);
        request.Fields = "sheets(properties(sheetId,title))";
        var meta = await request.ExecuteAsync();
        return meta.Sheets.ToDictionary(
            s => s.Properties.Title,
            s => s.Properties.SheetId ?? 0);
    }

    /// <summary>Numeric sheetId (gid) for a tab name — needed for formatting requests.</summary>
    public async Task<int> GetSheetIdAsync(string spreadsheetId, string tabName)
    {
        var tabs = await GetTabsAsync(spreadsheetId);
        return tabs.TryGetValue(await ResolveTabAsync(spreadsheetId, tabName), out var id)
            ? id
            : throw new InvalidOperationException($"Tab '{tabName}' not found in spreadsheet.");
    }

    /// <summary>
    /// Returns the real tab title, tolerating stray spaces and case differences
    /// (tab names in Google Sheets often drift from what you'd expect).
    /// </summary>
    public async Task<string> ResolveTabAsync(string spreadsheetId, string tabName)
    {
        var tabs = await GetTabsAsync(spreadsheetId);
        if (tabs.ContainsKey(tabName)) return tabName;

        var match = tabs.Keys.FirstOrDefault(t =>
                        string.Equals(t.Trim(), tabName.Trim(), StringComparison.OrdinalIgnoreCase))
                    ?? tabs.Keys.FirstOrDefault(t =>
                        t.Contains(tabName.Trim(), StringComparison.OrdinalIgnoreCase));

        return match ?? throw new InvalidOperationException(
            $"Tab '{tabName}' not found. Available tabs: {string.Join(" | ", tabs.Keys)}");
    }

    /// <summary>Set the background colour of a single cell (row/col are 0-based).</summary>
    public Request BuildColorRequest(int sheetId, int row0, int col0, string hex)
    {
        var c = System.Drawing.ColorTranslator.FromHtml(hex);
        return new Request
        {
            RepeatCell = new RepeatCellRequest
            {
                Range = new GridRange
                {
                    SheetId = sheetId,
                    StartRowIndex = row0, EndRowIndex = row0 + 1,
                    StartColumnIndex = col0, EndColumnIndex = col0 + 1,
                },
                Cell = new CellData
                {
                    UserEnteredFormat = new CellFormat
                    {
                        BackgroundColor = new Color
                        { Red = c.R / 255f, Green = c.G / 255f, Blue = c.B / 255f },
                    },
                },
                Fields = "userEnteredFormat.backgroundColor",
            },
        };
    }

    /// <summary>Execute a batch of formatting requests.</summary>
    public async Task BatchUpdateAsync(string spreadsheetId, IList<Request> requests)
    {
        if (requests.Count == 0) return;
        var svc = await GetServiceAsync();
        await svc.Spreadsheets.BatchUpdate(
            new BatchUpdateSpreadsheetRequest { Requests = requests }, spreadsheetId)
            .ExecuteAsync();
    }

    /// <summary>
    /// Read a range with hyperlinks: returns [row][col] of (Text, Url).
    /// Url is null for cells without a link.
    /// </summary>
    public async Task<(string Text, string? Url)[][]> ReadLinksAsync(string spreadsheetId, string range)
    {
        var svc = await GetServiceAsync();
        var req = svc.Spreadsheets.Get(spreadsheetId);
        req.Ranges = range;
        req.Fields = "sheets(data(rowData(values(formattedValue,hyperlink))))";
        var resp = await req.ExecuteAsync();

        var rows = resp.Sheets?.FirstOrDefault()?.Data?.FirstOrDefault()?.RowData;
        if (rows == null) return Array.Empty<(string, string?)[]>();

        return rows.Select(r =>
            (r.Values ?? new List<CellData>()).Select(c =>
                (c.FormattedValue ?? "", (string?)c.Hyperlink)).ToArray()
        ).ToArray();
    }

    public async Task<(string Text, string? Url)[][]> ReadLinksFromConfiguredTabAsync(
        string spreadsheetId, string configuredTab, string unqualifiedRange)
    {
        static string Range(string tab, string range) =>
            $"'{tab.Replace("'", "''")}'!{range}";

        try
        {
            return await ReadLinksAsync(
                spreadsheetId, Range(configuredTab, unqualifiedRange));
        }
        catch (Google.GoogleApiException ex)
            when (ex.HttpStatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            var resolved = await ResolveTabAsync(spreadsheetId, configuredTab);
            return await ReadLinksAsync(
                spreadsheetId, Range(resolved, unqualifiedRange));
        }
    }

    /// <summary>Write individual cell values in one batch (row1 is 1-based, col0 is 0-based).</summary>
    public async Task WriteCellsAsync(
        string spreadsheetId, string tab, IEnumerable<(int Row1, int Col0, string Value)> cells)
    {
        var svc = await GetServiceAsync();
        var data = cells.Select(c => new ValueRange
        {
            Range = $"'{tab}'!{ColLetter(c.Col0)}{c.Row1}",
            Values = new List<IList<object>> { new List<object> { c.Value } },
        }).ToList();
        if (data.Count == 0) return;
        var body = new BatchUpdateValuesRequest { ValueInputOption = "USER_ENTERED", Data = data };
        await svc.Spreadsheets.Values.BatchUpdate(body, spreadsheetId).ExecuteAsync();
    }

    /// <summary>0-based column index → A1 letter(s), e.g. 0→A, 10→K, 26→AA.</summary>
    public static string ColLetter(int col0)
    {
        var s = "";
        for (int n = col0; n >= 0; n = n / 26 - 1)
            s = (char)('A' + n % 26) + s;
        return s;
    }

    /// <summary>Append a row of values to the top table of a tab.</summary>
    public async Task AppendRowAsync(string spreadsheetId, string tab, IList<object> row)
    {
        var svc = await GetServiceAsync();
        var body = new ValueRange { Values = new List<IList<object>> { row } };
        var req = svc.Spreadsheets.Values.Append(body, spreadsheetId, $"'{tab}'!A1");
        req.ValueInputOption = SpreadsheetsResource.ValuesResource.AppendRequest
            .ValueInputOptionEnum.USERENTERED;
        await req.ExecuteAsync();
    }
}
