using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FourthIBTracker.Services;

/// <summary>
/// Talks to the unit's "Fill in submission" Google Form.
/// On load it fetches the form page, reads FB_PUBLIC_LOAD_DATA_ (the JSON blob
/// Google embeds with every question's entry ID and options), and can then
/// submit responses directly to the form's /formResponse endpoint — the same
/// request the browser sends when you press Submit.
/// </summary>
public class FillInFormService
{
    private static readonly HttpClient Http = new();
    private readonly string _formId;

    private long _nameEntry, _dateEntry, _fromEntry, _whereEntry;
    public List<string> FromOptions { get; } = new();
    public List<string> WhereOptions { get; } = new();
    public bool IsLoaded { get; private set; }

    public FillInFormService(string formId) => _formId = formId;

    private string ViewUrl => $"https://docs.google.com/forms/d/e/{_formId}/viewform";
    public string SubmitUrl => $"https://docs.google.com/forms/d/e/{_formId}/formResponse";

    public async Task LoadAsync()
    {
        if (IsLoaded) return;
        if (string.IsNullOrWhiteSpace(_formId))
            throw new InvalidOperationException("FillInFormId is not set in appsettings.json.");

        var html = await Http.GetStringAsync(ViewUrl);
        var m = Regex.Match(html, @"FB_PUBLIC_LOAD_DATA_\s*=\s*(.*?);\s*</script>",
            RegexOptions.Singleline);
        if (!m.Success)
            throw new InvalidOperationException(
                "Could not read the form's field data. Google may have changed the form page format.");

        using var doc = JsonDocument.Parse(m.Groups[1].Value);
        var questions = doc.RootElement[1][1];

        foreach (var q in questions.EnumerateArray())
        {
            if (q.ValueKind != JsonValueKind.Array || q.GetArrayLength() < 5) continue;
            if (q[4].ValueKind != JsonValueKind.Array || q[4].GetArrayLength() == 0) continue;

            var title = q[1].ValueKind == JsonValueKind.String ? q[1].GetString() ?? "" : "";
            var entryId = q[4][0][0].GetInt64();
            var lower = title.ToLowerInvariant();

            if (lower.Contains("date")) _dateEntry = entryId;
            else if (lower.Contains("name")) _nameEntry = entryId;
            else if (lower.Contains("from"))
            {
                _fromEntry = entryId;
                ReadOptions(q, FromOptions);
            }
            else if (lower.Contains("fill"))
            {
                _whereEntry = entryId;
                ReadOptions(q, WhereOptions);
            }
        }

        if (_nameEntry == 0 || _dateEntry == 0 || _fromEntry == 0 || _whereEntry == 0)
            throw new InvalidOperationException(
                "The form's questions don't match what the app expects " +
                "(Name / Date / Where from / Where filled in). Has the form been changed?");
        IsLoaded = true;
    }

    private static void ReadOptions(JsonElement question, List<string> into)
    {
        into.Clear();
        var opts = question[4][0][1];
        if (opts.ValueKind != JsonValueKind.Array) return;
        foreach (var o in opts.EnumerateArray())
            if (o.ValueKind == JsonValueKind.Array && o.GetArrayLength() > 0 &&
                o[0].ValueKind == JsonValueKind.String)
                into.Add(o[0].GetString()!);
    }

    /// <summary>The exact form fields a submission would POST — used by test mode too.</summary>
    public Dictionary<string, string> BuildFields(string name, DateTime date, string from, string where) => new()
    {
        [$"entry.{_nameEntry}"] = name,
        [$"entry.{_dateEntry}_year"] = date.Year.ToString(),
        [$"entry.{_dateEntry}_month"] = date.Month.ToString(),
        [$"entry.{_dateEntry}_day"] = date.Day.ToString(),
        [$"entry.{_fromEntry}"] = from,
        [$"entry.{_whereEntry}"] = where,
        ["fvv"] = "1",
        ["pageHistory"] = "0",
    };

    /// <summary>Submit one fill-in. Throws on failure.</summary>
    public async Task SubmitAsync(string name, DateTime date, string from, string where)
    {
        if (!IsLoaded) await LoadAsync();

        var fields = BuildFields(name, date, from, where);
        var resp = await Http.PostAsync(SubmitUrl, new FormUrlEncodedContent(fields));
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Form rejected the submission (HTTP {(int)resp.StatusCode}).");
    }
}
