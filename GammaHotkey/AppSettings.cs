using System.Globalization;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows.Input;

namespace GammaHotkey;

public sealed class GammaProfile
{
    public string Name { get; set; } = "Default";
    public double Gamma { get; set; } = 1.0;
    public string Hotkey { get; set; } = "";
    public override string ToString() => Name;

    // Accepts the given culture or invariant format; 0.50–4.00 in steps of 0.01.
    internal static bool TryParseGamma(string text, CultureInfo culture, out double value) =>
        (double.TryParse(text, NumberStyles.Float, culture, out value) ||
         double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) &&
        IsValidGamma(value) && Math.Abs(value * 100 - Math.Round(value * 100)) < 0.000001;

    internal static bool IsValidGamma(double gamma) => double.IsFinite(gamma) && gamma >= 0.5 && gamma <= 4;
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
        GammaProfile.IsValidGamma(profile.Gamma) && IsValidHotkey(profile.Hotkey);

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

// Format: "Ctrl+Alt+Shift+Key", modifiers optional but in this order; Key is A-Z, 0-9 or F1-F12.
// Letters and digits need Ctrl or Alt so normal typing is never blocked.
internal static class Hotkey
{
    private static readonly (string Name, uint Flag)[] Modifiers = [("Ctrl", 0x2), ("Alt", 0x1), ("Shift", 0x4)];

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
            if ((modifiers & 0x3) == 0) return false;
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

    public static string Prefix(ModifierKeys modifiers) =>
        (modifiers.HasFlag(ModifierKeys.Control) ? "Ctrl+" : "") + (modifiers.HasFlag(ModifierKeys.Alt) ? "Alt+" : "") +
        (modifiers.HasFlag(ModifierKeys.Shift) ? "Shift+" : "");

    // Captured key press to shortcut text; on rejection returns null and the message to show.
    public static string? FromKeyPress(Key key, ModifierKeys modifiers, out string? error)
    {
        error = null;
        if (key is >= Key.NumPad0 and <= Key.NumPad9) { error = "Use the top-row digits, not the numpad."; return null; }
        string? value = key switch
        {
            >= Key.A and <= Key.Z => key.ToString(),
            >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(CultureInfo.InvariantCulture),
            >= Key.F1 and <= Key.F12 => $"F{key - Key.F1 + 1}",
            _ => null
        };
        if (value is null) { error = "Use a letter, digit, or F1–F12, with optional Ctrl, Alt or Shift."; return null; }
        string text = Prefix(modifiers) + value;
        if (!TryParse(text, out _, out _)) { error = "Letters and digits need Ctrl or Alt."; return null; }
        return text;
    }
}
