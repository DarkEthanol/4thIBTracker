using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

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
        Converters = { new JsonStringEnumConverter() },
    };

    private string unitWebsite = "";
    private ForumSection forum = new();

    public GoogleSection Google { get; set; } = new();
    public PlatoonSection Platoon { get; set; } = new();
    public Dictionary<string, SheetRef> Spreadsheets { get; set; } = new();
    public string FillInFormId { get; set; } = "";
    public string UnitWebsite
    {
        get => unitWebsite;
        set
        {
            unitWebsite = NormalizeUnitWebsite(value);
            forum.SetUnitWebsite(unitWebsite);
        }
    }
    public ForumSection Forum
    {
        get => forum;
        set
        {
            forum = value ?? new ForumSection();
            forum.SetUnitWebsite(unitWebsite);
        }
    }
    public List<BrowserTab> BrowserTabs { get; set; } = new();

    [JsonIgnore]
    public string OrbatUrl => WebsiteUrl("orbat.php");

    public string WebsiteUrl(string relativePath) =>
        BuildWebsiteUrl(UnitWebsite, relativePath);

    /// <summary>Which platoon this copy of the app is set up for.</summary>
    public class PlatoonSection
    {
        public int Number { get; set; } = 1;
        public DayOfWeek OperationDayOfWeek { get; set; } = DayOfWeek.Saturday;
        public string AddressFrom { get; set; } = "";
        public string SignOff { get; set; } = "";
        public List<string> NcoTrackerPositions { get; set; } = new();
        public List<string> OutstandingCourseExclusions { get; set; } = new();
        /// <summary>The phrase typed when signing off a patrol report.</summary>
        public string SignOffPhrase { get; set; } = "";

        public bool ExcludesOutstandingCourse(string course)
        {
            var normalizedCourse = NormalizeCourseName(course);
            return OutstandingCourseExclusions.Any(excluded => string.Equals(
                NormalizeCourseName(excluded), normalizedCourse,
                StringComparison.OrdinalIgnoreCase));
        }

        private static string NormalizeCourseName(string value) =>
            string.Join(" ", value.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        [System.Text.Json.Serialization.JsonIgnore]
        public string Name => $"{Number} Platoon";
        [System.Text.Json.Serialization.JsonIgnore]
        public string ShortName => $"{Number} Pl";
    }

    public class ForumSection
    {
        private string unitWebsite = "";

        public string CoursesForumId { get; set; } = "";
        public string UpcomingForumId { get; set; } = "";
        public string PatrolReportsForumId { get; set; } = "";
        public string TrainingReportsForumId { get; set; } = "";
        public string PlatoonForumId { get; set; } = "";
        public List<string> PendingTransferForumIds { get; set; } = new();
        public List<string> CompletedTransferForumIds { get; set; } = new();
        public int MaxPages { get; set; } = 10;
        public List<string> NcoNames { get; set; } = new();

        [JsonIgnore] public string CoursesForumUrl => ForumUrl(CoursesForumId);
        [JsonIgnore] public string UpcomingForumUrl => ForumUrl(UpcomingForumId);
        [JsonIgnore] public string PatrolReportsForumUrl => ForumUrl(PatrolReportsForumId);
        [JsonIgnore] public string TrainingReportsForumUrl => ForumUrl(TrainingReportsForumId);
        [JsonIgnore] public string PlatoonForumUrl => ForumUrl(PlatoonForumId);
        [JsonIgnore] public string OperationsIndexUrl => BuildWebsiteUrl(unitWebsite, "index.php");
        [JsonIgnore] public List<string> PendingTransferForums =>
            PendingTransferForumIds.Select(ForumUrl).Where(url => url.Length > 0).ToList();
        [JsonIgnore] public List<string> CompletedTransferForums =>
            CompletedTransferForumIds.Select(ForumUrl).Where(url => url.Length > 0).ToList();

        internal void SetUnitWebsite(string value) => unitWebsite = value;

        private string ForumUrl(string value)
        {
            var trimmed = value.Trim();
            if (Uri.TryCreate(trimmed, UriKind.Absolute, out var legacy))
                return legacy.ToString();

            var id = ForumIdFromValue(trimmed);
            return id.Length == 0
                ? ""
                : BuildWebsiteUrl(unitWebsite, $"forum-{id}.html");
        }

        /// <summary>Accepts either a numeric ID or a legacy forum URL.</summary>
        public static string ForumIdFromValue(string? value)
        {
            var trimmed = (value ?? "").Trim();
            if (Regex.IsMatch(trimmed, @"^\d+$")) return trimmed;
            var match = Regex.Match(trimmed, @"forum-(?<id>\d+)\.html", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups["id"].Value : "";
        }

        /// <summary>
        /// New settings become IDs, while an unusual legacy URL that does not
        /// follow the forum-N.html convention remains usable.
        /// </summary>
        public static string NormalizeForumReference(string? value)
        {
            var trimmed = (value ?? "").Trim();
            var id = ForumIdFromValue(trimmed);
            if (id.Length > 0) return id;
            return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) &&
                   (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                ? uri.ToString()
                : "";
        }
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
        var migratedWebsiteSettings = MigrateLegacyWebsiteSettings(configNode);
        var addedDefaults = MergeMissing(configNode, defaultNode);
        var removedObsoleteSettings =
            PruneUnknownObjectSettings(configNode, defaultNode, "Spreadsheets") |
            PruneUnknownObjectSettings(configNode, defaultNode, "Google");

        var config = configNode.Deserialize<AppConfig>(JsonOptions)
                     ?? throw new InvalidOperationException("appsettings.json could not be parsed.");

        // First run migrates the existing sidecar configuration. Later schema
        // additions only append missing fields; user-supplied values always win.
        if (!File.Exists(ConfigPath) || migratedWebsiteSettings || addedDefaults || removedObsoleteSettings)
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
    /// Converts the old repeated full-URL schema into one unit website and
    /// compact forum IDs. This runs before deserialisation and rewrites the
    /// user's AppData configuration automatically on first upgraded launch.
    /// </summary>
    public static bool MigrateLegacyWebsiteSettings(JsonObject root)
    {
        var changed = false;
        var forum = ObjectProperty(root, "Forum") ?? new JsonObject();
        if (ObjectProperty(root, "Forum") is null)
        {
            root["Forum"] = forum;
            changed = true;
        }

        var unitWebsite = StringProperty(root, "UnitWebsite");
        if (string.IsNullOrWhiteSpace(unitWebsite))
        {
            var candidates = new List<string?>
            {
                StringProperty(root, "OrbatUrl"),
                StringProperty(forum, "OperationsIndexUrl"),
                StringProperty(forum, "CoursesForumUrl"),
                StringProperty(forum, "UpcomingForumUrl"),
                StringProperty(forum, "PatrolReportsForumUrl"),
                StringProperty(forum, "TrainingReportsForumUrl"),
                StringProperty(forum, "PlatoonForumUrl"),
            };
            candidates.AddRange(StringArrayProperty(forum, "PendingTransferForums"));
            candidates.AddRange(StringArrayProperty(forum, "CompletedTransferForums"));
            unitWebsite = candidates
                .Select(value => NormalizeUnitWebsite(value ?? ""))
                .FirstOrDefault(value => value.Length > 0) ?? "";
            if (unitWebsite.Length > 0)
            {
                SetProperty(root, "UnitWebsite", unitWebsite);
                changed = true;
            }
        }

        changed |= MigrateForumValue(forum, "CoursesForumUrl", "CoursesForumId");
        changed |= MigrateForumValue(forum, "UpcomingForumUrl", "UpcomingForumId");
        changed |= MigrateForumValue(forum, "PatrolReportsForumUrl", "PatrolReportsForumId");
        changed |= MigrateForumValue(forum, "TrainingReportsForumUrl", "TrainingReportsForumId");
        changed |= MigrateForumValue(forum, "PlatoonForumUrl", "PlatoonForumId");
        changed |= MigrateForumList(forum, "PendingTransferForums", "PendingTransferForumIds");
        changed |= MigrateForumList(forum, "CompletedTransferForums", "CompletedTransferForumIds");
        changed |= RemoveProperty(root, "OrbatUrl");
        changed |= RemoveProperty(forum, "OperationsIndexUrl");
        return changed;
    }

    public static string NormalizeUnitWebsite(string? value)
    {
        var trimmed = (value ?? "").Trim();
        if (trimmed.Length == 0) return "";
        if (!trimmed.Contains("://", StringComparison.Ordinal))
            trimmed = "https://" + trimmed;
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return "";

        var path = uri.AbsolutePath.TrimEnd('/');
        if (Regex.IsMatch(path, @"/(?:forum-\d+\.html|orbat\.php|index\.php|attendance\.php)$",
                RegexOptions.IgnoreCase))
            path = path[..path.LastIndexOf('/')];
        return (uri.GetLeftPart(UriPartial.Authority) + path).TrimEnd('/');
    }

    internal static string BuildWebsiteUrl(string unitWebsite, string relativePath)
    {
        var normalized = NormalizeUnitWebsite(unitWebsite);
        if (normalized.Length == 0) return "";
        return new Uri(new Uri(normalized + "/"), relativePath.TrimStart('/')).ToString();
    }

    private static bool MigrateForumValue(JsonObject forum, string oldName, string newName)
    {
        var oldKey = PropertyKey(forum, oldName);
        if (oldKey is null) return false;
        var legacy = StringProperty(forum, oldName);
        if (string.IsNullOrWhiteSpace(StringProperty(forum, newName)) &&
            !string.IsNullOrWhiteSpace(legacy))
        {
            SetProperty(forum, newName, ForumSection.NormalizeForumReference(legacy));
        }
        forum.Remove(oldKey);
        return true;
    }

    private static bool MigrateForumList(JsonObject forum, string oldName, string newName)
    {
        var oldKey = PropertyKey(forum, oldName);
        if (oldKey is null) return false;
        if (PropertyKey(forum, newName) is null)
        {
            var values = StringArrayProperty(forum, oldName)
                .Select(ForumSection.NormalizeForumReference)
                .Where(value => value.Length > 0);
            forum[newName] = new JsonArray(values
                .Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
        }
        forum.Remove(oldKey);
        return true;
    }

    private static JsonObject? ObjectProperty(JsonObject parent, string name)
    {
        var key = PropertyKey(parent, name);
        return key is null ? null : parent[key] as JsonObject;
    }

    private static string? StringProperty(JsonObject parent, string name)
    {
        var key = PropertyKey(parent, name);
        return key is null ? null : parent[key]?.GetValue<string>();
    }

    private static IEnumerable<string> StringArrayProperty(JsonObject parent, string name)
    {
        var key = PropertyKey(parent, name);
        return key is null || parent[key] is not JsonArray array
            ? []
            : array.Select(node => node?.GetValue<string>() ?? "");
    }

    private static string? PropertyKey(JsonObject parent, string name) =>
        parent.Select(property => property.Key).FirstOrDefault(key =>
            string.Equals(key, name, StringComparison.OrdinalIgnoreCase));

    private static void SetProperty(JsonObject parent, string name, JsonNode? value)
    {
        var key = PropertyKey(parent, name) ?? name;
        parent[key] = value;
    }

    private static bool RemoveProperty(JsonObject parent, string name)
    {
        var key = PropertyKey(parent, name);
        return key is not null && parent.Remove(key);
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
