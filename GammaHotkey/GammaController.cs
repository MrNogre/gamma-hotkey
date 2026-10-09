namespace GammaHotkey;

internal enum DisplayStatusKind
{
    NotRead, BaselineCaptured, Confirmed, Failed, Unsupported, NotConfirmed,
    Overridden, RestoreSkipped, RestoreFailed, BaselineRestored, Removed
}

internal sealed class DisplayState
{
    public required string DeviceName { get; init; }
    public GammaRamp? BaselineRamp { get; set; }
    public GammaRamp? LastAppliedRamp { get; set; }
    public GammaProfile? AppliedProfile { get; set; }
    public double AppliedGamma { get; set; }
    public DisplayStatusKind Kind { get; private set; }
    public string Detail { get; private set; } = "";
    public bool Overridden { get; set; }
    public bool RestoreFailed { get; set; }
    public bool Confirmed => LastAppliedRamp is not null && !Overridden && !RestoreFailed && Kind == DisplayStatusKind.Confirmed;
    public bool IsWarning => Overridden || RestoreFailed || Kind is DisplayStatusKind.Failed or DisplayStatusKind.Unsupported or
        DisplayStatusKind.NotConfirmed or DisplayStatusKind.RestoreSkipped;

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

internal sealed class GammaController
{
    private readonly Dictionary<string, DisplayState> displays = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<IEnumerable<string>> screenNames;
    private readonly Func<string, GammaRamp?> read;
    private readonly Func<string, GammaRamp, bool> write;
    public IReadOnlyCollection<DisplayState> Displays => displays.Values;
    public bool HasAppliedRamp => displays.Values.Any(d => d.LastAppliedRamp is not null);
    public bool HasOverride => displays.Values.Any(d => d.Overridden);
    public bool HasFailedRestore => displays.Values.Any(d => d.RestoreFailed);
    public event Action? Changed;

    public GammaController(Func<IEnumerable<string>>? screenNames = null,
        Func<string, GammaRamp?>? read = null, Func<string, GammaRamp, bool>? write = null)
    {
        this.screenNames = screenNames ?? (() => Screen.AllScreens.Select(s => s.DeviceName));
        read ??= name => OnDc(name, dc => NativeMethods.Read(dc, out var ramp) ? ramp : null);
        write ??= (name, ramp) => OnDc(name, dc => NativeMethods.Write(dc, ramp));
        // Native errors count as failed reads/writes, so Restore never throws.
        this.read = name => { try { return read(name); } catch (Exception) { return null; } };
        this.write = (name, ramp) => { try { return write(name, ramp); } catch (Exception) { return false; } };
    }

    private static T OnDc<T>(string name, Func<nint, T> operation)
    {
        nint dc = NativeMethods.CreateDC("DISPLAY", name, 0, 0);
        if (dc == 0) return default!;
        try { return operation(dc); }
        finally { NativeMethods.DeleteDC(dc); }
    }

    public void RefreshBaseline()
    {
        if (HasAppliedRamp || HasFailedRestore) return;
        displays.Clear();
        foreach (string name in screenNames().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var state = new DisplayState { DeviceName = name };
            displays.Add(name, state);
            Capture(state);
        }
        Changed?.Invoke();
    }

    private void Capture(DisplayState display)
    {
        var ramp = read(display.DeviceName);
        if (ramp is null || !ramp.IsSafe())
        {
            if (ramp is null) display.SetStatus(DisplayStatusKind.Failed, "gamma read unavailable");
            else display.SetStatus(DisplayStatusKind.Unsupported, "unsafe or non-monotonic ramp");
            return;
        }
        display.BaselineRamp = ramp;
        display.SetStatus(DisplayStatusKind.BaselineCaptured);
    }

    public void ApplyProfile(GammaProfile profile)
    {
        foreach (var display in displays.Values)
        {
            if (display.RestoreFailed) continue;
            var current = read(display.DeviceName);
            if (current is null)
            {
                display.Overridden = display.LastAppliedRamp is not null;
                display.SetStatus(DisplayStatusKind.Failed, "current ramp unreadable");
                continue;
            }
            bool owned = display.LastAppliedRamp is not null && current.Matches(display.LastAppliedRamp);
            if (owned && display.Confirmed && ReferenceEquals(display.AppliedProfile, profile) && display.AppliedGamma == profile.Gamma)
                continue;
            if (!owned && (display.BaselineRamp is null || !current.Matches(display.BaselineRamp)))
            {
                if (!current.IsSafe())
                {
                    display.Overridden = true;
                    display.SetStatus(DisplayStatusKind.Failed, "external ramp unsafe; not reapplied");
                    continue;
                }
                display.BaselineRamp = current;
            }
            if (display.BaselineRamp is null)
            {
                if (!current.IsSafe()) { display.SetStatus(DisplayStatusKind.Unsupported, "unsafe or non-monotonic ramp"); continue; }
                display.BaselineRamp = current;
            }
            GammaRamp target;
            try { target = display.BaselineRamp.Bright(profile.Gamma); }
            catch (ArgumentException) { display.SetStatus(DisplayStatusKind.Failed, "invalid profile ramp"); continue; }
            if (!write(display.DeviceName, target))
            {
                display.Overridden = true;
                display.SetStatus(DisplayStatusKind.Failed, "gamma write rejected");
                continue;
            }
            var observed = read(display.DeviceName);
            if (observed is null || !observed.Matches(target))
            {
                display.Overridden = true;
                display.SetStatus(DisplayStatusKind.NotConfirmed, observed is null ? "read-back failed" : "read-back differs");
                continue;
            }
            display.LastAppliedRamp = observed;
            display.AppliedProfile = profile;
            display.AppliedGamma = profile.Gamma;
            display.Overridden = false;
            display.SetStatus(DisplayStatusKind.Confirmed, profile.Name);
        }
        Changed?.Invoke();
    }

    public void CheckForOverrides()
    {
        bool changed = false;
        foreach (var display in displays.Values)
        {
            if (display.LastAppliedRamp is null || display.Overridden || display.RestoreFailed) continue;
            var current = read(display.DeviceName);
            if (current is not null && current.Matches(display.LastAppliedRamp)) continue;
            display.Overridden = true;
            display.SetStatus(DisplayStatusKind.Overridden, current is null ? "current ramp unreadable" :
                current.Matches(display.BaselineRamp!) ? "baseline restored externally" : "external ramp change");
            changed = true;
        }
        if (changed) Changed?.Invoke();
    }

    public void Restore()
    {
        foreach (var display in displays.Values)
        {
            if (display.LastAppliedRamp is null || display.BaselineRamp is null || display.RestoreFailed) continue;
            var current = read(display.DeviceName);
            if (current is null)
            {
                display.RestoreFailed = true;
                display.SetStatus(DisplayStatusKind.RestoreFailed, "current ramp unreadable");
                continue;
            }
            if (!current.Matches(display.LastAppliedRamp))
            {
                display.LastAppliedRamp = null;
                display.Overridden = false;
                display.SetStatus(DisplayStatusKind.RestoreSkipped, "external ramp change");
                continue;
            }
            if (!write(display.DeviceName, display.BaselineRamp) ||
                read(display.DeviceName) is not { } restored || !restored.Matches(display.BaselineRamp))
            {
                display.RestoreFailed = true;
                display.SetStatus(DisplayStatusKind.RestoreFailed, "write or read-back mismatch");
                continue;
            }
            display.LastAppliedRamp = null;
            display.AppliedProfile = null;
            display.Overridden = false;
            display.RestoreFailed = false;
            display.SetStatus(DisplayStatusKind.BaselineRestored);
        }
        Changed?.Invoke();
    }

    public void HandleDisplayChange()
    {
        var present = screenNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var display in displays.Values.Where(d => !present.Contains(d.DeviceName)))
        {
            display.LastAppliedRamp = null;
            display.SetStatus(DisplayStatusKind.Removed);
        }
        Restore();
        if (!HasFailedRestore)
        {
            displays.Clear();
            foreach (string name in present)
            {
                var state = new DisplayState { DeviceName = name };
                displays.Add(name, state);
                Capture(state);
            }
        }
        else
        {
            foreach (string name in present.Where(n => !displays.ContainsKey(n)))
            {
                var state = new DisplayState { DeviceName = name };
                displays.Add(name, state);
                Capture(state);
            }
        }
        Changed?.Invoke();
    }
}
