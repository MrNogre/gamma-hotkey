using GammaHotkey;

var tests = new (string Name, Action Run)[]
{
    ("Gamma 1 copies all channels exactly", () =>
    {
        var source = CurvedRamp();
        var copy = source.Bright(1);
        Check(source.ToArray().SequenceEqual(copy.ToArray()));
    }),
    ("Bright preserves endpoints and raises identity midtones", () =>
    {
        var source = IdentityRamp();
        var bright = source.Bright(2.0);
        Check(bright[0] == source[0] && bright[255] == source[255]);
        Check(bright[128] > source[128]);
        Check(bright.IsSafe());
    }),
    ("Extended gamma preserves monotonic channels and endpoints", () =>
    {
        var source = IdentityRamp();
        foreach (double gamma in new[] { 0.5, 4.0 })
        {
            var changed = source.Bright(gamma);
            for (int channel = 0; channel < 3; channel++)
            {
                int offset = channel * 256;
                Check(changed[offset] == source[offset] && changed[offset + 255] == source[offset + 255]);
                for (int i = 1; i < 256; i++) Check(changed[offset + i] >= changed[offset + i - 1]);
            }
            Check(gamma < 1 ? changed[128] < source[128] : changed[128] > source[128]);
        }
        GammaRamp current = source;
        int writes = 0;
        var controller = new GammaController(() => ["A"], _ => current, (_, target) =>
        { writes++; current = target; return true; });
        controller.RefreshBaseline();
        foreach (double gamma in new[] { 0.5, 4.0 })
        {
            controller.ApplyProfile(new GammaProfile { Gamma = gamma });
            Check(controller.Displays.Single().Confirmed);
        }
        controller.Restore();
        Check(writes == 3 && current.Matches(source));
    }),
    ("Distinct RGB baselines stay distinct", () =>
    {
        var bright = CurvedRamp().Bright(2.0);
        Check(bright[128] != bright[256 + 128]);
        Check(bright[256 + 128] != bright[512 + 128]);
    }),
    ("Invalid gamma and unsafe ramps are rejected", () =>
    {
        var ramp = IdentityRamp();
        foreach (double value in new[] { double.NaN, double.PositiveInfinity, 0.49, 4.01 })
            Throws(() => ramp.Bright(value));
        Throws(() => new GammaRamp(new ushort[1]));
        var descending = ramp.ToArray();
        descending[130] = 0;
        Throws(() => new GammaRamp(descending).Bright(2.0));
        var excessive = ramp.ToArray();
        excessive[1] = 50000;
        Throws(() => new GammaRamp(excessive).Bright(2.0));
    }),
    ("Profile transitions, partial failures, monitoring, and safe restore", () =>
    {
        var original = IdentityRamp();
        var external = CurvedRamp();
        var ramps = new Dictionary<string, GammaRamp> { ["A"] = original, ["B"] = original };
        var writes = new List<string>();
        bool failB = false;
        var controller = new GammaController(() => ["A", "B"], name => ramps[name], (name, ramp) =>
        {
            writes.Add(name);
            if (name == "B" && failB) return false;
            ramps[name] = ramp;
            return true;
        });
        var first = new GammaProfile { Name = "First", Gamma = 1.5 };
        var second = new GammaProfile { Name = "Second", Gamma = 1.7 };
        controller.RefreshBaseline();
        Check(writes.Count == 0 && controller.Displays.All(d => !d.Confirmed && d.Status == "Baseline captured"));
        first.Gamma = 1.6; // Selection and edits do not call the controller.
        Check(writes.Count == 0);
        failB = true;
        controller.ApplyProfile(first);
        Check(controller.Displays.Count(d => d.Confirmed) == 1 && writes.Count == 2);
        failB = false;
        writes.Clear();
        controller.ApplyProfile(first);
        Check(writes.SequenceEqual(["B"]) && controller.Displays.Count(d => d.Confirmed) == 2);
        ramps["B"] = external;
        writes.Clear();
        controller.CheckForOverrides();
        Check(writes.Count == 0 && controller.HasOverride && controller.Displays.Count(d => d.Confirmed) == 1);
        controller.ApplyProfile(first);
        Check(writes.SequenceEqual(["B"]) && controller.Displays.Single(d => d.DeviceName == "B").BaselineRamp!.Matches(external));
        writes.Clear();
        first.Gamma = 1.8;
        controller.ApplyProfile(first);
        Check(writes.Count == 2 && controller.Displays.Count(d => d.Confirmed) == 2);
        writes.Clear();
        controller.ApplyProfile(second);
        Check(writes.Count == 2 && controller.Displays.All(d => ReferenceEquals(d.AppliedProfile, second)));
        ramps["A"] = original;
        writes.Clear();
        controller.Restore();
        Check(writes.SequenceEqual(["B"]) && ramps["B"].Matches(external));
        var failed = new GammaController(() => ["A"], name => ramps[name], (name, ramp) =>
        {
            writes.Add(name);
            return false;
        });
        failed.RefreshBaseline();
        writes.Clear();
        failed.ApplyProfile(first);
        Check(writes.Count == 1);
        ramps["A"] = external;
        writes.Clear();
        failed.CheckForOverrides();
        Check(writes.Count == 0);
        failed.ApplyProfile(first);
        Check(writes.Count == 1 && failed.Displays.Single().BaselineRamp!.Matches(external));
    }),
    ("Failed restore is not retried or recaptured on display change", () =>
    {
        var ramp = IdentityRamp();
        GammaRamp current = ramp;
        int writes = 0;
        bool failRestore = false;
        var controller = new GammaController(() => ["A"], _ => current, (_, target) =>
        {
            writes++;
            if (failRestore) return false;
            current = target;
            return true;
        });
        controller.RefreshBaseline();
        controller.ApplyProfile(new GammaProfile { Gamma = 1.5 });
        failRestore = true;
        controller.HandleDisplayChange();
        Check(controller.HasFailedRestore && writes == 2);
        controller.HandleDisplayChange();
        Check(controller.HasFailedRestore && writes == 2 && controller.Displays.Single().BaselineRamp!.Matches(ramp));
    }),
    ("Monitor labels prefer specific names", () =>
    {
        Check(NativeMethods.FormatDisplayLabel(@"\\.\DISPLAY1", "LG UltraGear") == "DISPLAY 1 - LG UltraGear");
        Check(NativeMethods.FormatDisplayLabel(@"\\.\DISPLAY2", "Generic PnP Monitor") == "DISPLAY 2");
        Check(NativeMethods.FormatDisplayLabel(@"\\.\DISPLAY3", null) == "DISPLAY 3");
    }),
    ("Settings defaults, round trip, and invalid JSON", () =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "GammaHotkeyTests-" + Guid.NewGuid());
        string path = Path.Combine(directory, "settings.json");
        try
        {
            var defaults = AppSettings.Load(path, out var warning);
            Check(warning is null && defaults.SchemaVersion == 2 && !defaults.StartInTray && defaults.Theme == "System" &&
                defaults.Profiles.Count == 1 && defaults.Profiles[0].Gamma == 1.0);
            defaults.StartInTray = true;
            defaults.Theme = "Dark";
            defaults.Profiles[0].Gamma = 1.21;
            defaults.Profiles.Add(new GammaProfile { Name = "Gaming", Gamma = 1.6, Hotkey = "Ctrl+Alt+G" });
            defaults.SelectedProfile = 1;
            defaults.Save(path);
            string saved = File.ReadAllText(path);
            Check(saved.Contains("\"schemaVersion\": 2") && !saved.Contains("brightGamma") && !saved.Contains("periodicReapply") &&
                saved.Contains("\"hotkey\": \"Ctrl+Alt+G\""));
            var restored = AppSettings.Load(path, out warning);
            Check(warning is null && restored.StartInTray && restored.Theme == "Dark" &&
                restored.SelectedProfile == 1 && restored.Profiles.Count == 2 &&
                restored.Profiles[0].Gamma == 1.21 && restored.Profiles[1].Name == "Gaming" &&
                restored.Profiles[1].Gamma == 1.6 && restored.Profiles[1].Hotkey == "Ctrl+Alt+G");
            defaults.Profiles[0].Hotkey = "F9";
            defaults.Profiles[1].Hotkey = "Ctrl+Alt+Shift+F12";
            defaults.Save(path);
            restored = AppSettings.Load(path, out warning);
            Check(warning is null && restored.Profiles[0].Hotkey == "F9" && restored.Profiles[1].Hotkey == "Ctrl+Alt+Shift+F12");
            foreach (string invalid in new[] { "G", "5", "Shift+G", "Alt+Ctrl+G", "Ctrl+Ctrl+G", "Ctrl+", "+G", "ctrl+G",
                "Ctrl+F0", "F01", "F13", "F1x", "Ctrl+Numpad5", "Win+G", "Ctrl+Win+F5" })
            {
                defaults.Profiles[0].Hotkey = invalid;
                Throws(() => defaults.Save(path));
            }
            defaults.Profiles[0].Hotkey = "Ctrl+Alt+Shift+F12";
            Throws(() => defaults.Save(path));
            defaults.Profiles[0].Hotkey = "F9";
            defaults.Profiles[0].Gamma = 4;
            defaults.Profiles[1].Gamma = 0.5;
            defaults.Save(path);
            restored = AppSettings.Load(path, out warning);
            Check(warning is null && restored.Profiles[0].Gamma == 4 && restored.Profiles[1].Gamma == 0.5);
            defaults.Profiles[0].Gamma = 4.01;
            Throws(() => defaults.Save(path));
            File.WriteAllText(path, "{\"schemaVersion\":2,\"profiles\":[{\"name\":\"High\",\"gamma\":5.5}]}");
            restored = AppSettings.Load(path, out warning);
            Check(warning is null && restored.Profiles[0].Name == "High" && restored.Profiles[0].Gamma == 4);
            File.WriteAllText(path, "{\"schemaVersion\":2,\"profiles\":[{\"name\":\"High\",\"gamma\":6.5}]}");
            restored = AppSettings.Load(path, out warning);
            Check(warning is not null && restored.Profiles[0].Name == "Default");
            File.WriteAllText(path, "{\"schemaVersion\":2}");
            restored = AppSettings.Load(path, out warning);
            Check(warning is null && !restored.StartInTray && restored.Theme == "System" &&
                restored.Profiles.Count == 1 && restored.Profiles[0].Gamma == 1.0);
            File.WriteAllText(path, "{\"schemaVersion\":3}");
            restored = AppSettings.Load(path, out warning);
            Check(warning is not null && warning.Contains("version 3") && restored.Profiles[0].Gamma == 1.0);
            File.WriteAllText(path, "{\"theme\":\"Dark\"}");
            restored = AppSettings.Load(path, out warning);
            Check(warning is not null && restored.Theme == "System");
            File.WriteAllText(path, "{\"schemaVersion\":1,\"brightGamma\":2.0,\"periodicReapply\":false}");
            restored = AppSettings.Load(path, out warning);
            Check(warning is null && restored.SchemaVersion == 2 && !restored.StartInTray && restored.Theme == "System" &&
                restored.Profiles.Count == 1 && restored.Profiles[0].Gamma == 2.0);
            File.WriteAllText(path, "{\"schemaVersion\":1,\"brightGamma\":1.37}");
            restored = AppSettings.Load(path, out warning);
            Check(warning is null && restored.Profiles[0].Gamma == 1.37);
            File.WriteAllText(path, "{\"schemaVersion\":1}");
            restored = AppSettings.Load(path, out warning);
            Check(warning is null && restored.Profiles.Count == 1 && restored.Profiles[0].Gamma == 1.0);
            File.WriteAllText(path, "{\"schemaVersion\":1,\"brightGamma\":2,\"periodicReapply\":true,\"startInTray\":true,\"profiles\":[{\"name\":\"One\",\"gamma\":1.5,\"hotkey\":\"A\"},{\"name\":\"Two\",\"gamma\":1.6,\"hotkey\":\"F5\"},{\"name\":\"Three\",\"gamma\":1.7,\"hotkey\":\"\"}],\"selectedProfile\":1}");
            restored = AppSettings.Load(path, out warning);
            Check(warning is null && restored.StartInTray && restored.SelectedProfile == 1 && restored.Profiles.Count == 3 &&
                restored.Profiles[0].Hotkey == "Ctrl+Alt+A" && restored.Profiles[1].Hotkey == "Ctrl+Alt+F5" &&
                restored.Profiles[2].Hotkey == "" && restored.Profiles[1].Gamma == 1.6);
            File.WriteAllText(path, "{\"schemaVersion\":1,\"brightGamma\":2,\"periodicReapply\":false,\"profiles\":[{\"name\":\"One\",\"gamma\":1.5,\"hotkey\":\"A\"},{\"name\":\"Two\",\"gamma\":1.6,\"hotkey\":\"A\"}]}");
            restored = AppSettings.Load(path, out warning);
            Check(warning is not null && restored.Profiles.Count == 1);
            File.WriteAllText(path, "{\"schemaVersion\":1,\"profiles\":[{\"name\":\"One\",\"gamma\":1.5,\"hotkey\":\"F13\"}]}");
            restored = AppSettings.Load(path, out warning);
            Check(warning is not null);
            File.WriteAllText(path, "{\"schemaVersion\":1,\"brightGamma\":2.0,\"periodicReapply\":false,\"theme\":\"Unknown\"}");
            restored = AppSettings.Load(path, out warning);
            Check(warning is not null && restored.Theme == "System");
            File.WriteAllText(path, "{invalid");
            restored = AppSettings.Load(path, out warning);
            Check(warning is not null && restored.Profiles[0].Gamma == 1.0 && !restored.StartInTray);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }),
    ("Hotkey text maps to RegisterHotKey modifiers and virtual keys", () =>
    {
        Check(Hotkey.TryParse("F9", out uint modifiers, out uint key) && modifiers == 0 && key == 0x78);
        Check(Hotkey.TryParse("Ctrl+Shift+F5", out modifiers, out key) && modifiers == 0x6 && key == 0x74);
        Check(Hotkey.TryParse("Ctrl+Alt+A", out modifiers, out key) && modifiers == 0x3 && key == 'A');
        Check(Hotkey.TryParse("Alt+7", out modifiers, out key) && modifiers == 0x1 && key == '7');
        Check(!Hotkey.TryParse("Alt+Win+7", out _, out _) && !Hotkey.TryParse("Win+F5", out _, out _));
        Check(Hotkey.TryParse("Shift+F1", out modifiers, out key) && modifiers == 0x4 && key == 0x70);
        Check(!Hotkey.TryParse("Shift+Q", out _, out _) && !Hotkey.TryParse("Q", out _, out _) && !Hotkey.TryParse(null, out _, out _));
        // Only exact Ctrl+Alt(+Shift) letter/digit combinations can collide with AltGr typing; layout-specific results are checked manually.
        Check(NativeMethods.AltGrCharacter(0x6, 'V') is null && NativeMethods.AltGrCharacter(0xB, 'V') is null &&
            NativeMethods.AltGrCharacter(0x3, 0x74) is null && NativeMethods.AltGrCharacter(0x0, 0x78) is null);
        // Exercise the native path on every letter and digit; a mis-marshaled buffer corrupts the heap here.
        for (int round = 0; round < 50; round++)
            foreach (uint combination in new uint[] { 0x3, 0x7 })
                foreach (char letter in "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789")
                    Check(NativeMethods.AltGrCharacter(combination, letter) is null or { Length: >= 1 and <= 8 });
    })
};

int failures = 0;
foreach (var (name, run) in tests)
{
    try { run(); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
}
return failures == 0 ? 0 : 1;

static GammaRamp IdentityRamp()
{
    var values = new ushort[GammaRamp.Length];
    for (int channel = 0; channel < 3; channel++)
        for (int i = 0; i < 256; i++) values[channel * 256 + i] = (ushort)(i * 257);
    return new GammaRamp(values);
}

static GammaRamp CurvedRamp()
{
    var values = IdentityRamp().ToArray();
    for (int i = 1; i < 255; i++)
    {
        values[256 + i] = (ushort)Math.Min(65535, i * 257 + 100);
        values[512 + i] = (ushort)Math.Max(0, i * 257 - 100);
    }
    return new GammaRamp(values);
}

static void Check(bool value)
{
    if (!value) throw new Exception("Assertion failed.");
}

static void Throws(Action action)
{
    try { action(); }
    catch (ArgumentException) { return; }
    throw new Exception("Expected an argument exception.");
}
