using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FourthIBTracker.Services;

public class SheetRef
{
    public string Id { get; set; } = "";
    public string Tab { get; set; } = "";
}

public class BrowserTab
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
}

public class AppConfig
{
    private const string DefaultConfigResource = "FourthIBTracker.DefaultAppSettings.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public GoogleSection Google { get; set; } = new();
    public PlatoonSection Platoon { get; set; } = new();
    public Dictionary<string, SheetRef> Spreadsheets { get; set; } = new();
    public string FillInFormId { get; set; } = "";
    public string OrbatUrl { get; set; } = "";
    public ForumSection Forum { get; set; } = new();
    public List<BrowserTab> BrowserTabs { get; set; } = new();

    /// <summary>Which platoon this copy of the app is set up for.</summary>
    public class PlatoonSection
    {
        public int Number { get; set; } = 1;
        public string AddressFrom { get; set; } = "";
        public string SignOff { get; set; } = "";
        public List<string> NcoTrackerPositions { get; set; } = new();
        /// <summary>The phrase typed when signing off a patrol report.</summary>
        public string SignOffPhrase { get; set; } = "";

        [System.Text.Json.Serialization.JsonIgnore]
        public string Name => $"{Number} Platoon";
        [System.Text.Json.Serialization.JsonIgnore]
        public string ShortName => $"{Number} Pl";
    }

    public class ForumSection
    {
        public string CoursesForumUrl { get; set; } = "";
        public string UpcomingForumUrl { get; set; } = "";
        public string PatrolReportsForumUrl { get; set; } = "";
        public string TrainingReportsForumUrl { get; set; } = "";
        public string OperationsIndexUrl { get; set; } = "";
        public List<string> PendingTransferForums { get; set; } = new();
        public List<string> CompletedTransferForums { get; set; } = new();
        public int MaxPages { get; set; } = 10;
        public List<string> NcoNames { get; set; } = new();
    }

    public class GoogleSection
    {
        public string ApplicationName { get; set; } = "4thIB Tracker";
    }

    /// <summary>
    /// The editable per-user configuration. Keeping it outside the application
    /// directory means replacing the executable cannot overwrite platoon settings.
    /// </summary>
    public static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "4thIBTracker", "appsettings.json");

    /// <summary>
    /// Location used by versions before per-user configuration was introduced.
    /// It is read once as a migration source and is never overwritten.
    /// </summary>
    public static string LegacyConfigPath =>
        Path.Combine(AppContext.BaseDirectory, "appsettings.json");

    public static AppConfig Load()
    {
        var defaultJson = ReadEmbeddedDefaults();
        var sourcePath = File.Exists(ConfigPath)
            ? ConfigPath
            : File.Exists(LegacyConfigPath)
                ? LegacyConfigPath
                : null;

        var sourceJson = sourcePath is null
            ? defaultJson
            : File.ReadAllText(sourcePath);

        var configNode = ParseObject(sourceJson, sourcePath ?? "embedded defaults");
        var defaultNode = ParseObject(defaultJson, "embedded defaults");
        var addedDefaults = MergeMissing(configNode, defaultNode);
        var removedObsoleteSettings =
            PruneUnknownObjectSettings(configNode, defaultNode, "Spreadsheets") |
            PruneUnknownObjectSettings(configNode, defaultNode, "Google");

        var config = configNode.Deserialize<AppConfig>(JsonOptions)
                     ?? throw new InvalidOperationException("appsettings.json could not be parsed.");

        // First run migrates the existing sidecar configuration. Later schema
        // additions only append missing fields; user-supplied values always win.
        if (!File.Exists(ConfigPath) || addedDefaults || removedObsoleteSettings)
            WriteUserConfig(configNode.ToJsonString(JsonOptions));

        return config;
    }

    public void Save() =>
        WriteUserConfig(JsonSerializer.Serialize(this, JsonOptions));

    private static string ReadEmbeddedDefaults()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream(DefaultConfigResource)
            ?? throw new FileNotFoundException(
                $"Embedded default configuration '{DefaultConfigResource}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static JsonObject ParseObject(string json, string source)
    {
        try
        {
            return JsonNode.Parse(json) as JsonObject
                   ?? throw new InvalidOperationException($"Configuration in {source} is not a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Configuration in {source} is not valid JSON.", ex);
        }
    }

    /// <summary>
    /// Deep-merges only absent properties. Existing strings, arrays, IDs, URLs,
    /// and platoon values are retained exactly, including intentionally empty ones.
    /// </summary>
    private static bool MergeMissing(JsonObject target, JsonObject defaults)
    {
        var changed = false;
        foreach (var (defaultKey, defaultValue) in defaults)
        {
            var targetKey = target
                .Select(property => property.Key)
                .FirstOrDefault(key => string.Equals(
                    key, defaultKey, StringComparison.OrdinalIgnoreCase));

            if (targetKey is null)
            {
                target[defaultKey] = defaultValue?.DeepClone();
                changed = true;
                continue;
            }

            if (target[targetKey] is JsonObject targetObject &&
                defaultValue is JsonObject defaultObject)
            {
                changed |= MergeMissing(targetObject, defaultObject);
            }
            else if (target[targetKey] is null && defaultValue is not null)
            {
                target[targetKey] = defaultValue.DeepClone();
                changed = true;
            }
        }
        return changed;
    }

    /// <summary>
    /// Entries in application-defined configuration objects are not extensible
    /// user data. Removing one from the embedded schema therefore removes its
    /// stale setting from upgraded user configurations as well.
    /// </summary>
    private static bool PruneUnknownObjectSettings(
        JsonObject target, JsonObject defaults, string objectName)
    {
        static JsonObject? ObjectProperty(JsonObject parent, string name)
        {
            var key = parent.Select(property => property.Key).FirstOrDefault(candidate =>
                string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase));
            return key is null ? null : parent[key] as JsonObject;
        }

        var targetObject = ObjectProperty(target, objectName);
        var defaultObject = ObjectProperty(defaults, objectName);
        if (targetObject is null || defaultObject is null) return false;

        var allowed = defaultObject.Select(property => property.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var obsolete = targetObject.Select(property => property.Key)
            .Where(key => !allowed.Contains(key))
            .ToList();
        foreach (var key in obsolete)
            targetObject.Remove(key);
        return obsolete.Count > 0;
    }

    private static void WriteUserConfig(string json)
    {
        var directory = Path.GetDirectoryName(ConfigPath)!;
        Directory.CreateDirectory(directory);

        var tempPath = ConfigPath + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, ConfigPath, overwrite: true);
    }

    public SheetRef Sheet(string key) =>
        Spreadsheets.TryGetValue(key, out var s) && !string.IsNullOrWhiteSpace(s.Id) && s.Id != "PASTE_SPREADSHEET_ID"
            ? s
            : throw new InvalidOperationException(
                $"Spreadsheet '{key}' is not configured. Paste its ID in Settings or at " +
                $"{ConfigPath} " +
                "(the long string in the sheet's URL between /d/ and /edit).");
}
