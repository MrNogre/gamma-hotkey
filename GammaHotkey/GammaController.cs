namespace GammaHotkey;

internal sealed class GammaController
{
    private readonly Dictionary<string, DisplayState> displays = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<List<string>?> screenNames;
    private readonly Func<string, GammaRamp?> read;
    private readonly Func<string, GammaRamp, bool> write;
    private readonly Func<string, string> label;
    private readonly Func<string, bool> isHdr;
    private readonly Func<string, IReadOnlyList<string>> monitorIds;
    public IReadOnlyCollection<DisplayState> Displays => displays.Values;
    public bool HasAppliedRamp => displays.Values.Any(d => d.LastAppliedRamp is not null);
    public bool HasOverride => displays.Values.Any(d => d.Overridden);
    public bool HasFailedRestore => displays.Values.Any(d => d.RestoreFailed);
    public event Action? Changed;

    public GammaController(Func<IEnumerable<string>>? screenNames = null,
        Func<string, GammaRamp?>? read = null, Func<string, GammaRamp, bool>? write = null, Func<string, string>? label = null,
        Func<string, bool>? isHdr = null, Func<string, IReadOnlyList<string>>? monitorIds = null)
    {
        monitorIds ??= NativeMethods.MonitorPaths;
        screenNames ??= () => Screen.AllScreens.Select(s => s.DeviceName);
        label ??= NativeMethods.DisplayLabel;
        isHdr ??= NativeMethods.IsHdr;
        // Enumeration failure yields null so callers keep the current display state.
        this.screenNames = () =>
        {
            try { return screenNames().ToList(); }
            catch (Exception ex) { Log.Write($"Display enumeration failed: {ex.Message}"); return null; }
        };
        this.label = name =>
        {
            try { return label(name); }
            catch (Exception ex) { Log.Write($"{name}: label query failed: {ex.Message}"); return NativeMethods.FormatDisplayLabel(name, null); }
        };
        this.monitorIds = name =>
        {
            try { return monitorIds(name); }
            catch (Exception ex) { Log.Write($"{name}: monitor id query failed: {ex.Message}"); return []; }
        };
        read ??= name => OnDc(name, dc => NativeMethods.Read(dc, out var ramp) ? ramp : null);
        write ??= (name, ramp) => OnDc(name, dc => NativeMethods.Write(dc, ramp));
        // Native errors count as failed reads/writes, so Restore never throws.
        this.read = name => { try { return read(name); } catch (Exception) { return null; } };
        this.write = (name, ramp) => { try { return write(name, ramp); } catch (Exception) { return false; } };
        this.isHdr = name =>
        {
            try { return isHdr(name); }
            catch (Exception ex) { Log.Write($"{name}: HDR query failed, treated as SDR: {ex.Message}"); return false; }
        };
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
        if (HasAppliedRamp || HasFailedRestore || screenNames() is not { } names) return;
        displays.Clear();
        foreach (string name in names.Distinct(StringComparer.OrdinalIgnoreCase))
            Add(name);
        Changed?.Invoke();
    }

    // Label and monitor ids are resolved once here; the UI reads them on every refresh.
    private void Add(string name)
    {
        var state = new DisplayState { DeviceName = name, Label = label(name), MonitorIds = monitorIds(name) };
        displays.Add(name, state);
        if (!SkipIfHdr(state)) Capture(state);
    }

    // HDR displays get no capture or profile; Windows keeps applying our ramp under HDR, so an owned one is restored first.
    private bool SkipIfHdr(DisplayState display)
    {
        if (isHdr(display.DeviceName))
        {
            bool owned = display.LastAppliedRamp is not null && !display.RestoreFailed;
            RestoreDisplay(display);
            display.Hdr = true;
            if (!display.RestoreFailed && !(owned && display.Kind == DisplayStatusKind.RestoreSkipped))
                display.SetStatus(DisplayStatusKind.Skipped, "HDR active");
            return true;
        }
        // A failed restore keeps its original baseline and diagnostic.
        if (display.Hdr) { display.Hdr = false; if (!display.RestoreFailed) Capture(display); }
        return false;
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

    // gamma = live preview: overrides profile.Gamma and only touches displays
    // still confirmed with this profile and owned; external ramps are left alone.
    // Non-targeted displays get their owned ramp restored (skipped during preview).
    public void ApplyProfile(GammaProfile profile, double? gamma = null)
    {
        double value = gamma ?? profile.Gamma;
        foreach (var display in displays.Values)
        {
            if (display.RestoreFailed || SkipIfHdr(display)) continue;
            if (!profile.Targets(display))
            {
                if (gamma is null) RestoreDisplay(display);
                continue;
            }
            if (gamma is not null && !(display.Confirmed && ReferenceEquals(display.AppliedProfile, profile))) continue;
            var current = read(display.DeviceName);
            if (current is null)
            {
                display.Overridden = display.LastAppliedRamp is not null;
                display.SetStatus(DisplayStatusKind.Failed, "current ramp unreadable");
                continue;
            }
            bool owned = display.LastAppliedRamp is not null && current.Matches(display.LastAppliedRamp);
            if (gamma is not null && !owned) continue;
            if (owned && display.Confirmed && ReferenceEquals(display.AppliedProfile, profile) && display.AppliedGamma == value)
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
            try { target = display.BaselineRamp.Bright(value); }
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
            display.AppliedGamma = value;
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
            if (SkipIfHdr(display)) { changed = true; continue; }
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
            RestoreDisplay(display);
        Changed?.Invoke();
    }

    // Restores only a ramp the app still owns, confirmed by read-back.
    private void RestoreDisplay(DisplayState display)
    {
        if (display.LastAppliedRamp is not { } applied || display.BaselineRamp is not { } baseline || display.RestoreFailed) return;
        var current = read(display.DeviceName);
        if (current is null)
        {
            display.RestoreFailed = true;
            display.SetStatus(DisplayStatusKind.RestoreFailed, "current ramp unreadable");
            return;
        }
        if (!current.Matches(applied))
        {
            display.LastAppliedRamp = null;
            display.Overridden = false;
            display.SetStatus(DisplayStatusKind.RestoreSkipped, "external ramp change");
            return;
        }
        if (!write(display.DeviceName, baseline) ||
            read(display.DeviceName) is not { } restored || !restored.Matches(baseline))
        {
            display.RestoreFailed = true;
            display.SetStatus(DisplayStatusKind.RestoreFailed, "write or read-back mismatch");
            return;
        }
        display.LastAppliedRamp = null;
        display.AppliedProfile = null;
        display.Overridden = false;
        display.RestoreFailed = false;
        display.SetStatus(DisplayStatusKind.BaselineRestored);
    }

    // Returns false when enumeration failed and nothing was restored.
    public bool HandleDisplayChange()
    {
        if (screenNames() is not { } names) return false;
        var present = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
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
                Add(name);
        }
        else
        {
            foreach (var display in displays.Values.Where(d => present.Contains(d.DeviceName)))
                SkipIfHdr(display);
            foreach (string name in present.Where(n => !displays.ContainsKey(n)))
                Add(name);
        }
        Changed?.Invoke();
        return true;
    }
}
