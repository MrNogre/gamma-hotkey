namespace GammaHotkey;

internal sealed class DisplayState
{
    public required string DeviceName { get; init; }
    public string Label { get; init; } = "";
    public IReadOnlyList<string> MonitorIds { get; init; } = [];
    public GammaRamp? BaselineRamp { get; set; }
    public GammaRamp? LastAppliedRamp { get; set; }
    public GammaProfile? AppliedProfile { get; set; }
    public double AppliedGamma { get; set; }
    public DisplayStatusKind Kind { get; private set; }
    public string Detail { get; private set; } = "";
    public bool Overridden { get; set; }
    public bool RestoreFailed { get; set; }
    public bool Hdr { get; set; }
    public bool Confirmed => LastAppliedRamp is not null && !Overridden && !RestoreFailed && Kind == DisplayStatusKind.Confirmed;
    public bool IsWarning => Overridden || RestoreFailed || Kind is DisplayStatusKind.Failed or DisplayStatusKind.Unsupported or
        DisplayStatusKind.NotConfirmed or DisplayStatusKind.RestoreSkipped or DisplayStatusKind.Skipped;

    // UI and log text, e.g. "Failed: gamma write rejected".
    public string Status
    {
        get
        {
            string label = Kind switch
            {
                DisplayStatusKind.NotRead => "Not read",
                DisplayStatusKind.BaselineCaptured => "Baseline captured",
                DisplayStatusKind.Confirmed => "Profile confirmed",
                DisplayStatusKind.NotConfirmed => "Not confirmed",
                DisplayStatusKind.RestoreSkipped => "Restore skipped",
                DisplayStatusKind.RestoreFailed => "Restore failed",
                DisplayStatusKind.BaselineRestored => "Baseline restored",
                _ => Kind.ToString()
            };
            return Detail == "" ? label : $"{label}: {Detail}";
        }
    }

    public void SetStatus(DisplayStatusKind kind, string detail = "") => (Kind, Detail) = (kind, detail);
}
