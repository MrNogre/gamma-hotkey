using System.IO;
using System.Text.Json;

namespace GammaHotkey;

public sealed class GammaProfile
{
    public string Name { get; set; } = "Default";
    public double Gamma { get; set; } = 1.0;
    public string Hotkey { get; set; } = "";
    public override string ToString() => Name;
}

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
                warning = $"Settings version {version} is not supported; safe defaults are in use.";
                return new AppSettings();
            }
            var result = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            if (result is null) throw new JsonException("Invalid settings values.");
            if (version == 1) MigrateV1(result, root);
            if (!result.IsValid()) throw new JsonException("Invalid settings values.");
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            warning = "Settings could not be loaded; safe defaults are in use.";
            return new AppSettings();
        }
    }

    // v1 stored a single legacy gamma when profiles were absent, and bare hotkeys that were always Ctrl+Alt.
    private static void MigrateV1(AppSettings result, JsonElement root)
    {
        if (!root.TryGetProperty("profiles", out _))
        {
            double gamma = 1.0;
            if (root.TryGetProperty("brightGamma", out var legacy) && !legacy.TryGetDouble(out gamma))
                throw new JsonException("Invalid settings values.");
            result.Profiles = [new GammaProfile { Gamma = gamma }];
        }
        foreach (var profile in result.Profiles)
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
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private bool IsValid() => SchemaVersion == 2 && Theme is "Light" or "Dark" or "System" &&
        Profiles is { Count: > 0 } && SelectedProfile >= 0 && SelectedProfile < Profiles.Count &&
        Profiles.All(p => p is not null && !string.IsNullOrWhiteSpace(p.Name) && p.Name.Length <= 60 &&
            double.IsFinite(p.Gamma) && p.Gamma >= 0.5 && p.Gamma <= 6 &&
            (p.Hotkey == "" || Hotkey.TryParse(p.Hotkey, out _, out _))) &&
        Profiles.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == Profiles.Count &&
        Profiles.Where(p => p.Hotkey != "").Select(p => p.Hotkey).Distinct().Count() ==
            Profiles.Count(p => p.Hotkey != "");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
}

// Hotkey text is "Ctrl+Alt+Shift+Win+Key" with any subset of modifiers in that fixed order.
// Key is A-Z, 0-9 or F1-F12; letters and digits need Ctrl, Alt or Win so plain typing is never blocked.
internal static class Hotkey
{
    private static readonly (string Name, uint Flag)[] Modifiers = [("Ctrl", 0x2), ("Alt", 0x1), ("Shift", 0x4), ("Win", 0x8)];

    public static bool TryParse(string? text, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;
        if (string.IsNullOrEmpty(text)) return false;
        string[] parts = text.Split('+');
        int next = 0;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            int index = Array.FindIndex(Modifiers, next, m => m.Name == parts[i]);
            if (index < 0) return false;
            modifiers |= Modifiers[index].Flag;
            next = index + 1;
        }
        string key = parts[^1];
        if (key is [>= 'A' and <= 'Z' or >= '0' and <= '9'])
        {
            if ((modifiers & 0xB) == 0) return false;
            virtualKey = key[0];
            return true;
        }
        if (key.Length >= 2 && key[0] == 'F' && int.TryParse(key.AsSpan(1), out int number) &&
            number is >= 1 and <= 12 && key == $"F{number}")
        {
            virtualKey = (uint)(0x70 + number - 1);
            return true;
        }
        return false;
    }
}
