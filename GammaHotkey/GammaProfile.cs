using System.Globalization;

namespace GammaHotkey;

public sealed class GammaProfile
{
    public string Name { get; set; } = "Default";
    public double Gamma { get; set; } = 1.0;
    public string Hotkey { get; set; } = "";
    // Monitor ids (monitorDevicePath) this profile applies to; null means all displays, empty means none.
    public List<string>? Displays { get; set; }
    public override string ToString() => Name;

    // Accepts the given culture or invariant format; 0.50–4.00 in steps of 0.01.
    internal static bool TryParseGamma(string text, CultureInfo culture, out double value) =>
        (double.TryParse(text, NumberStyles.Float, culture, out value) ||
         double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) &&
        IsValidGamma(value) && Math.Abs(value * 100 - Math.Round(value * 100)) < 0.000001;

    // Clone mode: a display is targeted when any of its monitors is selected.
    internal bool Targets(DisplayState display) =>
        Displays is null || display.MonitorIds.Any(id => Displays.Contains(id, StringComparer.OrdinalIgnoreCase));

    internal static bool IsValidGamma(double gamma) => double.IsFinite(gamma) && gamma >= 0.5 && gamma <= 4;
}
