using System.Globalization;
using System.Text.Json.Serialization;

namespace GammaHotkey;

public sealed class GammaProfile
{
    public string Name { get; set; } = "Default";
    public double Gamma { get; set; } = 1.0;
    public double Brightness { get; set; }
    public double Contrast { get; set; } = 1.0;
    public string Hotkey { get; set; } = "";
    // Monitor ids (monitorDevicePath) this profile applies to; null means all displays, empty means none.
    public List<string>? Displays { get; set; }
    [JsonIgnore] public Adjustment Adjustment => new(Gamma, Brightness, Contrast);
    public override string ToString() => Name;

    // Accepts the given culture or invariant format; in range and in steps of 0.01.
    internal static bool TryParseValue(string text, CultureInfo culture, Func<double, bool> isValid, out double value) =>
        (double.TryParse(text, NumberStyles.Float, culture, out value) ||
         double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) &&
        isValid(value) && Math.Abs(value * 100 - Math.Round(value * 100)) < 0.000001;

    // Clone mode: a display is targeted when any of its monitors is selected.
    // ponytail: a display without a monitor id has no switch, so it is always targeted; add an "unknown monitor" switch if it must be excludable.
    internal bool Targets(DisplayState display) =>
        Displays is null || display.MonitorIds.Count == 0 ||
        display.MonitorIds.Any(id => Displays.Contains(id, StringComparer.OrdinalIgnoreCase));

    internal static bool IsValidGamma(double gamma) => double.IsFinite(gamma) && gamma >= 0.5 && gamma <= 4;
    internal static bool IsValidBrightness(double brightness) => double.IsFinite(brightness) && brightness >= -0.5 && brightness <= 0.5;
    internal static bool IsValidContrast(double contrast) => double.IsFinite(contrast) && contrast >= 0.5 && contrast <= 1.5;
}
