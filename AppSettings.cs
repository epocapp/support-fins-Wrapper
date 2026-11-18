using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PrintFins;

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(AppSettings))]
partial class AppSettingsJsonContext : JsonSerializerContext;

sealed class AppSettings
{
    public string RepositoryUrl { get; set; } = "";
    public string CloneDirectory { get; set; } = "";
    public string WebRoot { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public int HttpsPort { get; set; }
    public string Display { get; set; } = "";
    public bool ScanDownloads { get; set; } = true;
    public string AntivirusCommand { get; set; } = "";

    [JsonIgnore]
    public string SettingsDirectory { get; set; } = "";

    public string ResolvedCloneDirectory => ResolveFromSettings(CloneDirectory);

    public string ResolvedWebRoot =>
        Path.IsPathRooted(WebRoot)
            ? Path.GetFullPath(WebRoot)
            : Path.GetFullPath(Path.Combine(ResolvedCloneDirectory, WebRoot));

    public bool IsWebView => string.Equals(Display, "webview", StringComparison.OrdinalIgnoreCase);

    public bool IsBrowser => string.Equals(Display, "browser", StringComparison.OrdinalIgnoreCase);

    public static string ApplicationDirectory =>
        Path.GetDirectoryName(Environment.ProcessPath)
        ?? AppContext.BaseDirectory;

    public static AppSettings Load()
    {
        var appDirectory = ApplicationDirectory;
        AppSettings settings;
        try
        {
            settings = JsonSerializer.Deserialize(ReadEmbeddedDefaults(), AppSettingsJsonContext.Default.AppSettings)
                ?? throw new InvalidDataException("Embedded settings.json is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Could not read embedded settings: {ex.Message}", ex);
        }

        var overridePath = Path.Combine(appDirectory, "settings.json");
        if (File.Exists(overridePath))
        {
            try
            {
                using var document = JsonDocument.Parse(
                    File.ReadAllText(overridePath),
                    new JsonDocumentOptions
                    {
                        CommentHandling = JsonCommentHandling.Skip,
                        AllowTrailingCommas = true,
                    });
                ApplyOverrides(settings, document.RootElement, overridePath);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"Could not read {overridePath}: {ex.Message}", ex);
            }
        }

        settings.SettingsDirectory = appDirectory;
        settings.Validate();
        return settings;
    }

    static void ApplyOverrides(AppSettings settings, JsonElement root, string path)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{path} must contain a JSON object.");

        foreach (var property in root.EnumerateObject())
        {
            switch (property.Name.ToLowerInvariant())
            {
                case "repositoryurl":
                    settings.RepositoryUrl = ReadString(property, path);
                    break;
                case "clonedirectory":
                    settings.CloneDirectory = ReadString(property, path);
                    break;
                case "webroot":
                    settings.WebRoot = ReadString(property, path);
                    break;
                case "host":
                    settings.Host = ReadString(property, path);
                    break;
                case "port":
                    settings.Port = ReadInt(property, path);
                    break;
                case "httpsport":
                    settings.HttpsPort = ReadInt(property, path);
                    break;
                case "display":
                    settings.Display = ReadString(property, path);
                    break;
                case "scandownloads":
                    settings.ScanDownloads = ReadBool(property, path);
                    break;
                case "antiviruscommand":
                    settings.AntivirusCommand = ReadString(property, path);
                    break;
                default:
                    throw new InvalidDataException($"{path}: unknown setting \"{property.Name}\".");
            }
        }
    }

    static string ReadString(JsonProperty property, string path)
    {
        if (property.Value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"{path}: {property.Name} must be a string.");
        return property.Value.GetString() ?? "";
    }

    static int ReadInt(JsonProperty property, string path)
    {
        if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out var value))
            throw new InvalidDataException($"{path}: {property.Name} must be an integer.");
        return value;
    }

    static bool ReadBool(JsonProperty property, string path)
    {
        if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException($"{path}: {property.Name} must be true or false.");
        return property.Value.GetBoolean();
    }

    static string ReadEmbeddedDefaults()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("PrintFins.settings.json")
            ?? throw new InvalidDataException("Embedded settings.json was not found in this executable.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    void Validate()
    {
        if (string.IsNullOrWhiteSpace(RepositoryUrl))
            throw new InvalidDataException("settings.json: repositoryUrl is required.");
        if (string.IsNullOrWhiteSpace(CloneDirectory))
            throw new InvalidDataException("settings.json: cloneDirectory is required.");
        if (string.IsNullOrWhiteSpace(WebRoot))
            throw new InvalidDataException("settings.json: webRoot is required.");
        if (string.IsNullOrWhiteSpace(Host))
            throw new InvalidDataException("settings.json: host is required.");
        if (Port is < 1 or > 65535)
            throw new InvalidDataException("settings.json: port must be between 1 and 65535.");
        if (HttpsPort is < 1 or > 65535)
            throw new InvalidDataException("settings.json: httpsPort must be between 1 and 65535.");
        if (HttpsPort == Port)
            throw new InvalidDataException("settings.json: httpsPort must be different from port.");
        if (!IsBrowser && !IsWebView)
            throw new InvalidDataException("settings.json: display must be \"browser\" or \"webview\".");
        if (ScanDownloads
            && !string.IsNullOrWhiteSpace(AntivirusCommand)
            && AntivirusCommand.IndexOf("{path}", StringComparison.OrdinalIgnoreCase) < 0)
        {
            throw new InvalidDataException("settings.json: antivirusCommand must include {path}.");
        }

        var clone = Path.GetFullPath(ResolvedCloneDirectory);
        var home = Path.GetFullPath(SettingsDirectory);
        if (IsSameOrNested(clone, home))
        {
            throw new InvalidDataException(
                "settings.json: cloneDirectory would contain this program. Choose a folder inside or beside the executable, not a parent of it.");
        }
    }

    string ResolveFromSettings(string path) =>
        Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(SettingsDirectory, path));

    static bool IsSameOrNested(string parent, string child)
    {
        var root = parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var nested = child.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return nested.Equals(root, StringComparison.OrdinalIgnoreCase)
            || nested.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
