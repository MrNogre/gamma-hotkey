using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace GammaHotkey;

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 2;
    public bool StartInTray { get; set; }
    public string Theme { get; set; } = "System";
    public List<GammaProfile> Profiles { get; set; } = [new()];
    public int SelectedProfile { get; set; }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GammaHotkey", "settings.json");

    public static AppSettings Load(string path, out string? warning)
    {
        warning = null;
        if (!File.Exists(path)) return new AppSettings();
        try
        {
            string json = File.ReadAllText(path);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("schemaVersion", out var versionElement) ||
                !versionElement.TryGetInt32(out int version))
                throw new JsonException("Missing settings version.");
            if (version is not (1 or 2))
            {
                warning = $"Settings version {version} is not supported; safe defaults are in use." + BackupNote(path);
                return new AppSettings();
            }
            var result = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            if (result is null) throw new JsonException("Invalid settings values.");
            if (version == 1) MigrateV1(result, root);
            // Older builds allowed gamma up to 6; clamp to the new 4.0 maximum.
            foreach (var profile in result.Profiles ?? [])
                if (profile is { Gamma: > 4 and <= 6 }) profile.Gamma = 4;
            if (!result.IsValid()) throw new JsonException("Invalid settings values.");
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            warning = "Settings could not be loaded; safe defaults are in use." + BackupNote(path);
            return new AppSettings();
        }
    }

    // Copies an unreadable settings file aside so saving defaults won't overwrite it.
    private static string BackupNote(string path)
    {
        string backup = Path.Combine(Path.GetDirectoryName(path)!, $"settings.invalid-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        try
        {
            File.Copy(path, backup, true);
            return $" The old file was kept as {Path.GetFileName(backup)}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }

    // v1: hotkeys were always Ctrl+Alt; a file without profiles used brightGamma.
    private static void MigrateV1(AppSettings result, JsonElement root)
    {
        if (!root.TryGetProperty("profiles", out _))
        {
            double gamma = 1.0;
            if (root.TryGetProperty("brightGamma", out var legacy) && !legacy.TryGetDouble(out gamma))
                throw new JsonException("Invalid settings values.");
            result.Profiles = [new GammaProfile { Gamma = gamma }];
        }
        foreach (var profile in result.Profiles ?? [])
            if (profile is { Hotkey: { Length: > 0 } key }) profile.Hotkey = "Ctrl+Alt+" + key;
        result.SchemaVersion = 2;
    }

    public void Save(string path)
    {
        if (!IsValid()) throw new ArgumentException("Invalid settings values.");
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, Path.GetRandomFileName());
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, JsonOptions));
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private bool IsValid() => SchemaVersion == 2 && Theme is "Light" or "Dark" or "System" &&
        Profiles is { Count: > 0 } && SelectedProfile >= 0 && SelectedProfile < Profiles.Count &&
        Profiles.All(IsValidProfile) && HasUniqueNames() && HasUniqueHotkeys();

    private static bool IsValidProfile(GammaProfile? profile) => profile is not null &&
        !string.IsNullOrWhiteSpace(profile.Name) && profile.Name.Length <= 60 &&
        GammaProfile.IsValidGamma(profile.Gamma) && IsValidHotkey(profile.Hotkey) &&
        (profile.Displays is null || profile.Displays.All(d => !string.IsNullOrWhiteSpace(d)));

    // An empty hotkey means none is assigned.
    private static bool IsValidHotkey(string hotkey) => hotkey == "" || Hotkey.TryParse(hotkey, out _, out _);

    private bool HasUniqueNames() =>
        Profiles.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == Profiles.Count;

    private bool HasUniqueHotkeys()
    {
        var hotkeys = Profiles.Select(p => p.Hotkey).Where(h => h != "").ToList();
        return hotkeys.Distinct().Count() == hotkeys.Count;
    }

    internal bool NameAvailable(string name, int exclude = -1) => name.Length > 0 &&
        !Profiles.Where((_, i) => i != exclude).Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    internal bool HotkeyTaken(string key, int exclude) =>
        key != "" && Profiles.Where((_, i) => i != exclude).Any(p => p.Hotkey == key);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
