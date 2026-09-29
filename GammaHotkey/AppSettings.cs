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
    public int SchemaVersion { get; set; } = 1;
    public double BrightGamma { get; set; } = 1.0;
    public bool PeriodicReapply { get; set; }
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
            if (!document.RootElement.TryGetProperty("schemaVersion", out _) ||
                !document.RootElement.TryGetProperty("brightGamma", out _) ||
                !document.RootElement.TryGetProperty("periodicReapply", out _))
                throw new JsonException("Missing settings values.");
            var result = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            if (result is null) throw new JsonException("Invalid settings values.");
            if (!document.RootElement.TryGetProperty("profiles", out _))
                result.Profiles = [new GammaProfile { Gamma = result.BrightGamma }];
            if (!result.IsValid()) throw new JsonException("Invalid settings values.");
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            warning = "Settings could not be loaded; safe defaults are in use.";
            return new AppSettings();
        }
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

    private bool IsValid() => SchemaVersion == 1 && double.IsFinite(BrightGamma) &&
        BrightGamma >= 0.5 && BrightGamma <= 6 && Theme is "Light" or "Dark" or "System" &&
        Profiles is { Count: > 0 } && SelectedProfile >= 0 && SelectedProfile < Profiles.Count &&
        Profiles.All(p => p is not null && !string.IsNullOrWhiteSpace(p.Name) && p.Name.Length <= 60 &&
            double.IsFinite(p.Gamma) && p.Gamma >= 0.5 && p.Gamma <= 6 &&
            (p.Hotkey == "" || p.Hotkey is { Length: 1 } &&
                (p.Hotkey[0] is >= 'A' and <= 'Z' or >= '0' and <= '9') ||
                p.Hotkey is { Length: >= 2 } && p.Hotkey.StartsWith('F') &&
                int.TryParse(p.Hotkey.AsSpan(1), out int functionKey) &&
                functionKey is >= 1 and <= 12 && p.Hotkey == $"F{functionKey}")) &&
        Profiles.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == Profiles.Count &&
        Profiles.Where(p => p.Hotkey != "").Select(p => p.Hotkey).Distinct().Count() ==
            Profiles.Count(p => p.Hotkey != "");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
}
