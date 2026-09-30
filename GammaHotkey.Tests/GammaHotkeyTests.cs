using GammaHotkey;
using Xunit;

namespace GammaHotkey.Tests;

public class GammaHotkeyTests
{
    [Fact]
    public void GammaOneCopiesAllChannelsExactly()
    {
        var source = CurvedRamp();
        var copy = source.Bright(1);
        Assert.True(source.ToArray().SequenceEqual(copy.ToArray()));
    }

    [Fact]
    public void BrightPreservesEndpointsAndRaisesIdentityMidtones()
    {
        var source = IdentityRamp();
        var bright = source.Bright(2.0);
        Assert.True(bright[0] == source[0] && bright[255] == source[255]);
        Assert.True(bright[128] > source[128]);
        Assert.True(bright.IsSafe());
    }

    [Fact]
    public void ExtendedGammaPreservesMonotonicChannelsAndEndpoints()
    {
        var source = IdentityRamp();
        foreach (double gamma in new[] { 0.5, 4.0 })
        {
            var changed = source.Bright(gamma);
            for (int channel = 0; channel < 3; channel++)
            {
                int offset = channel * 256;
                Assert.True(changed[offset] == source[offset] && changed[offset + 255] == source[offset + 255]);
                for (int i = 1; i < 256; i++) Assert.True(changed[offset + i] >= changed[offset + i - 1]);
            }
            Assert.True(gamma < 1 ? changed[128] < source[128] : changed[128] > source[128]);
        }
        GammaRamp current = source;
        int writes = 0;
        var controller = new GammaController(() => ["A"], _ => current, (_, target) =>
        { writes++; current = target; return true; });
        controller.RefreshBaseline();
        foreach (double gamma in new[] { 0.5, 4.0 })
        {
            controller.ApplyProfile(new GammaProfile { Gamma = gamma });
            Assert.True(controller.Displays.Single().Confirmed);
        }
        controller.Restore();
        Assert.True(writes == 3 && current.Matches(source));
    }

    [Fact]
    public void DistinctRgbBaselinesStayDistinct()
    {
        var bright = CurvedRamp().Bright(2.0);
        Assert.True(bright[128] != bright[256 + 128]);
        Assert.True(bright[256 + 128] != bright[512 + 128]);
    }

    [Fact]
    public void InvalidGammaAndUnsafeRampsAreRejected()
    {
        var ramp = IdentityRamp();
        foreach (double value in new[] { double.NaN, double.PositiveInfinity, 0.49, 4.01 })
            Assert.ThrowsAny<ArgumentException>(() => ramp.Bright(value));
        Assert.ThrowsAny<ArgumentException>(() => _ = new GammaRamp(new ushort[1]));
        var descending = ramp.ToArray();
        descending[130] = 0;
        Assert.ThrowsAny<ArgumentException>(() => new GammaRamp(descending).Bright(2.0));
        var excessive = ramp.ToArray();
        excessive[1] = 50000;
        Assert.ThrowsAny<ArgumentException>(() => new GammaRamp(excessive).Bright(2.0));
    }

    [Fact]
    public void ProfileTransitionsPartialFailuresMonitoringAndSafeRestore()
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
        Assert.True(writes.Count == 0 && controller.Displays.All(d => !d.Confirmed && d.Status == "Baseline captured"));
        first.Gamma = 1.6; // Selection and edits do not call the controller.
        Assert.True(writes.Count == 0);
        failB = true;
        controller.ApplyProfile(first);
        Assert.True(controller.Displays.Count(d => d.Confirmed) == 1 && writes.Count == 2);
        failB = false;
        writes.Clear();
        controller.ApplyProfile(first);
        Assert.True(writes.SequenceEqual(["B"]) && controller.Displays.Count(d => d.Confirmed) == 2);
        ramps["B"] = external;
        writes.Clear();
        controller.CheckForOverrides();
        Assert.True(writes.Count == 0 && controller.HasOverride && controller.Displays.Count(d => d.Confirmed) == 1);
        controller.ApplyProfile(first);
        Assert.True(writes.SequenceEqual(["B"]) && controller.Displays.Single(d => d.DeviceName == "B").BaselineRamp!.Matches(external));
        writes.Clear();
        first.Gamma = 1.8;
        controller.ApplyProfile(first);
        Assert.True(writes.Count == 2 && controller.Displays.Count(d => d.Confirmed) == 2);
        writes.Clear();
        controller.ApplyProfile(second);
        Assert.True(writes.Count == 2 && controller.Displays.All(d => ReferenceEquals(d.AppliedProfile, second)));
        ramps["A"] = original;
        writes.Clear();
        controller.Restore();
        Assert.True(writes.SequenceEqual(["B"]) && ramps["B"].Matches(external));
        var failed = new GammaController(() => ["A"], name => ramps[name], (name, ramp) =>
        {
            writes.Add(name);
            return false;
        });
        failed.RefreshBaseline();
        writes.Clear();
        failed.ApplyProfile(first);
        Assert.True(writes.Count == 1);
        ramps["A"] = external;
        writes.Clear();
        failed.CheckForOverrides();
        Assert.True(writes.Count == 0);
        failed.ApplyProfile(first);
        Assert.True(writes.Count == 1 && failed.Displays.Single().BaselineRamp!.Matches(external));
    }

    [Fact]
    public void FailedRestoreIsNotRetriedOrRecapturedOnDisplayChange()
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
        Assert.True(controller.HasFailedRestore && writes == 2);
        controller.HandleDisplayChange();
        Assert.True(controller.HasFailedRestore && writes == 2 && controller.Displays.Single().BaselineRamp!.Matches(ramp));
    }

    [Fact]
    public void RestoreNeverThrowsAndIsIdempotentWhenNativeCallsThrow()
    {
        GammaRamp current = IdentityRamp();
        bool fail = false;
        int writes = 0;
        var controller = new GammaController(() => ["A"],
            _ => fail ? throw new InvalidOperationException() : current,
            (_, target) => { writes++; if (fail) throw new InvalidOperationException(); current = target; return true; });
        controller.RefreshBaseline();
        controller.ApplyProfile(new GammaProfile { Gamma = 2 });
        fail = true;
        controller.Restore();
        controller.Restore();
        Assert.True(controller.HasFailedRestore && writes == 1);
        fail = false;
        var healthy = new GammaController(() => ["A"], _ => current, (_, target) => { writes++; current = target; return true; });
        current = IdentityRamp();
        healthy.RefreshBaseline();
        healthy.ApplyProfile(new GammaProfile { Gamma = 2 });
        writes = 0;
        healthy.Restore();
        healthy.Restore();
        Assert.True(writes == 1 && current.Matches(IdentityRamp()) && !healthy.HasAppliedRamp);
    }

    [Fact]
    public void LogKeepsOnlyTheNewestFiveFiles()
    {
        string directory = Path.Combine(Path.GetTempPath(), "GammaHotkeyTests-" + Guid.NewGuid());
        try
        {
            Directory.CreateDirectory(directory);
            for (int day = 1; day <= 6; day++) File.WriteAllText(Path.Combine(directory, $"gamma-hotkey-2020010{day}.log"), "");
            Log.Folder = directory;
            Log.Write("first");
            Log.Write("second");
            var files = Directory.GetFiles(directory).Select(Path.GetFileName).Order().ToArray();
            Assert.True(files.Length == 5 && files[0] == "gamma-hotkey-20200103.log" && File.Exists(Log.FilePath) &&
                File.ReadAllLines(Log.FilePath).Length == 2);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void MonitorLabelsPreferSpecificNames()
    {
        Assert.True(NativeMethods.FormatDisplayLabel(@"\\.\DISPLAY1", "LG UltraGear") == "DISPLAY 1 - LG UltraGear");
        Assert.True(NativeMethods.FormatDisplayLabel(@"\\.\DISPLAY2", "Generic PnP Monitor") == "DISPLAY 2");
        Assert.True(NativeMethods.FormatDisplayLabel(@"\\.\DISPLAY3", null) == "DISPLAY 3");
    }

    [Fact]
    public void SettingsDefaultsRoundTripAndInvalidJson()
    {
        string directory = Path.Combine(Path.GetTempPath(), "GammaHotkeyTests-" + Guid.NewGuid());
        string path = Path.Combine(directory, "settings.json");
        try
        {
            var defaults = AppSettings.Load(path, out var warning);
            Assert.True(warning is null && defaults.SchemaVersion == 2 && !defaults.StartInTray && defaults.Theme == "System" &&
                defaults.Profiles.Count == 1 && defaults.Profiles[0].Gamma == 1.0);
            defaults.StartInTray = true;
            defaults.Theme = "Dark";
            defaults.Profiles[0].Gamma = 1.21;
            defaults.Profiles.Add(new GammaProfile { Name = "Gaming", Gamma = 1.6, Hotkey = "Ctrl+Alt+G" });
            defaults.SelectedProfile = 1;
            defaults.Save(path);
            string saved = File.ReadAllText(path);
            Assert.True(saved.Contains("\"schemaVersion\": 2") && !saved.Contains("brightGamma") && !saved.Contains("periodicReapply") &&
                saved.Contains("\"hotkey\": \"Ctrl+Alt+G\""));
            var restored = AppSettings.Load(path, out warning);
            Assert.True(warning is null && restored.StartInTray && restored.Theme == "Dark" &&
                restored.SelectedProfile == 1 && restored.Profiles.Count == 2 &&
                restored.Profiles[0].Gamma == 1.21 && restored.Profiles[1].Name == "Gaming" &&
                restored.Profiles[1].Gamma == 1.6 && restored.Profiles[1].Hotkey == "Ctrl+Alt+G");
            defaults.Profiles[0].Hotkey = "F9";
            defaults.Profiles[1].Hotkey = "Ctrl+Alt+Shift+F12";
            defaults.Save(path);
            restored = AppSettings.Load(path, out warning);
            Assert.True(warning is null && restored.Profiles[0].Hotkey == "F9" && restored.Profiles[1].Hotkey == "Ctrl+Alt+Shift+F12");
            foreach (string invalid in new[] { "G", "5", "Shift+G", "Alt+Ctrl+G", "Ctrl+Ctrl+G", "Ctrl+", "+G", "ctrl+G",
                "Ctrl+F0", "F01", "F13", "F1x", "Ctrl+Numpad5", "Win+G", "Ctrl+Win+F5" })
            {
                defaults.Profiles[0].Hotkey = invalid;
                Assert.ThrowsAny<ArgumentException>(() => defaults.Save(path));
            }
            defaults.Profiles[0].Hotkey = "Ctrl+Alt+Shift+F12";
            Assert.ThrowsAny<ArgumentException>(() => defaults.Save(path));
            defaults.Profiles[0].Hotkey = "F9";
            defaults.Profiles[0].Gamma = 4;
            defaults.Profiles[1].Gamma = 0.5;
            defaults.Save(path);
            restored = AppSettings.Load(path, out warning);
            Assert.True(warning is null && restored.Profiles[0].Gamma == 4 && restored.Profiles[1].Gamma == 0.5);
            defaults.Profiles[0].Gamma = 4.01;
            Assert.ThrowsAny<ArgumentException>(() => defaults.Save(path));
            File.WriteAllText(path, "{\"schemaVersion\":2,\"profiles\":[{\"name\":\"High\",\"gamma\":5.5}]}");
            restored = AppSettings.Load(path, out warning);
            Assert.True(warning is null && restored.Profiles[0].Name == "High" && restored.Profiles[0].Gamma == 4);
            File.WriteAllText(path, "{\"schemaVersion\":2,\"profiles\":[{\"name\":\"High\",\"gamma\":6.5}]}");
            restored = AppSettings.Load(path, out warning);
            Assert.True(warning is not null && restored.Profiles[0].Name == "Default");
            File.WriteAllText(path, "{\"schemaVersion\":2}");
            restored = AppSettings.Load(path, out warning);
            Assert.True(warning is null && !restored.StartInTray && restored.Theme == "System" &&
                restored.Profiles.Count == 1 && restored.Profiles[0].Gamma == 1.0);
            File.WriteAllText(path, "{\"schemaVersion\":3}");
            restored = AppSettings.Load(path, out warning);
            Assert.True(warning is not null && warning.Contains("version 3") && warning.Contains("settings.invalid-") &&
                restored.Profiles[0].Gamma == 1.0);
            Assert.True(Directory.GetFiles(directory, "settings.invalid-*.json").Any(f => File.ReadAllText(f) == "{\"schemaVersion\":3}") &&
                File.ReadAllText(path) == "{\"schemaVersion\":3}");
            File.WriteAllText(path, "{\"schemaVersion\":2,\"profiles\":null}");
            restored = AppSettings.Load(path, out warning);
            Assert.True(warning is not null && restored.Profiles.Count == 1);
            File.WriteAllText(path, "{\"schemaVersion\":1,\"profiles\":null}");
            restored = AppSettings.Load(path, out warning);
            Assert.True(warning is not null && restored.Profiles.Count == 1);
            File.WriteAllText(path, "{\"theme\":\"Dark\"}");
            restored = AppSettings.Load(path, out warning);
            Assert.True(warning is not null && restored.Theme == "System");
            File.WriteAllText(path, "{\"schemaVersion\":1,\"brightGamma\":2.0,\"periodicReapply\":false}");
            restored = AppSettings.Load(path, out warning);
            Assert.True(warning is null && restored.SchemaVersion == 2 && !restored.StartInTray && restored.Theme == "System" &&
                restored.Profiles.Count == 1 && restored.Profiles[0].Gamma == 2.0);
            File.WriteAllText(path, "{\"schemaVersion\":1,\"brightGamma\":1.37}");
            restored = AppSettings.Load(path, out warning);
            Assert.True(warning is null && restored.Profiles[0].Gamma == 1.37);
            File.WriteAllText(path, "{\"schemaVersion\":1}");
            restored = AppSettings.Load(path, out warning);
            Assert.True(warning is null && restored.Profiles.Count == 1 && restored.Profiles[0].Gamma == 1.0);
            File.WriteAllText(path, "{\"schemaVersion\":1,\"brightGamma\":2,\"periodicReapply\":true,\"startInTray\":true,\"profiles\":[{\"name\":\"One\",\"gamma\":1.5,\"hotkey\":\"A\"},{\"name\":\"Two\",\"gamma\":1.6,\"hotkey\":\"F5\"},{\"name\":\"Three\",\"gamma\":1.7,\"hotkey\":\"\"}],\"selectedProfile\":1}");
            restored = AppSettings.Load(path, out warning);
            Assert.True(warning is null && restored.StartInTray && restored.SelectedProfile == 1 && restored.Profiles.Count == 3 &&
                restored.Profiles[0].Hotkey == "Ctrl+Alt+A" && restored.Profiles[1].Hotkey == "Ctrl+Alt+F5" &&
                restored.Profiles[2].Hotkey == "" && restored.Profiles[1].Gamma == 1.6);
            File.WriteAllText(path, "{\"schemaVersion\":1,\"brightGamma\":2,\"periodicReapply\":false,\"profiles\":[{\"name\":\"One\",\"gamma\":1.5,\"hotkey\":\"A\"},{\"name\":\"Two\",\"gamma\":1.6,\"hotkey\":\"A\"}]}");
            restored = AppSettings.Load(path, out warning);
            Assert.True(warning is not null && restored.Profiles.Count == 1);
            File.WriteAllText(path, "{\"schemaVersion\":1,\"profiles\":[{\"name\":\"One\",\"gamma\":1.5,\"hotkey\":\"F13\"}]}");
            restored = AppSettings.Load(path, out warning);
            Assert.True(warning is not null);
            File.WriteAllText(path, "{\"schemaVersion\":1,\"brightGamma\":2.0,\"periodicReapply\":false,\"theme\":\"Unknown\"}");
            restored = AppSettings.Load(path, out warning);
            Assert.True(warning is not null && restored.Theme == "System");
            foreach (string file in Directory.GetFiles(directory, "settings.invalid-*.json")) File.Delete(file);
            File.WriteAllText(path, "{invalid");
            restored = AppSettings.Load(path, out warning);
            Assert.True(warning is not null && warning.Contains("settings.invalid-") && restored.Profiles[0].Gamma == 1.0 && !restored.StartInTray);
            Assert.True(File.ReadAllText(path) == "{invalid" &&
                File.ReadAllText(Directory.GetFiles(directory, "settings.invalid-*.json").Single()) == "{invalid");
            restored.Save(path);
            Assert.True(File.ReadAllText(path + ".bak") == "{invalid" && AppSettings.Load(path, out warning).Profiles.Count == 1 && warning is null);
            restored.Theme = "Light";
            restored.Save(path);
            Assert.True(AppSettings.Load(path + ".bak", out warning).Theme == "System" && warning is null &&
                AppSettings.Load(path, out _).Theme == "Light");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void HotkeyTextMapsToRegisterHotKeyModifiersAndVirtualKeys()
    {
        Assert.True(Hotkey.TryParse("F9", out uint modifiers, out uint key) && modifiers == 0 && key == 0x78);
        Assert.True(Hotkey.TryParse("Ctrl+Shift+F5", out modifiers, out key) && modifiers == 0x6 && key == 0x74);
        Assert.True(Hotkey.TryParse("Ctrl+Alt+A", out modifiers, out key) && modifiers == 0x3 && key == 'A');
        Assert.True(Hotkey.TryParse("Alt+7", out modifiers, out key) && modifiers == 0x1 && key == '7');
        Assert.True(!Hotkey.TryParse("Alt+Win+7", out _, out _) && !Hotkey.TryParse("Win+F5", out _, out _));
        Assert.True(Hotkey.TryParse("Shift+F1", out modifiers, out key) && modifiers == 0x4 && key == 0x70);
        Assert.True(!Hotkey.TryParse("Shift+Q", out _, out _) && !Hotkey.TryParse("Q", out _, out _) && !Hotkey.TryParse(null, out _, out _));
        // Layout-specific results are checked manually.
        Assert.True(NativeMethods.AltGrCharacter(0x6, 'V') is null && NativeMethods.AltGrCharacter(0xB, 'V') is null &&
            NativeMethods.AltGrCharacter(0x3, 0x74) is null && NativeMethods.AltGrCharacter(0x0, 0x78) is null);
        // Regression: a mis-marshaled buffer crashed here.
        for (int round = 0; round < 50; round++)
            foreach (uint combination in new uint[] { 0x3, 0x7 })
                foreach (char letter in "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789")
                    Assert.True(NativeMethods.AltGrCharacter(combination, letter) is null or { Length: >= 1 and <= 8 });
    }

    private static GammaRamp IdentityRamp()
    {
        var values = new ushort[GammaRamp.Length];
        for (int channel = 0; channel < 3; channel++)
            for (int i = 0; i < 256; i++) values[channel * 256 + i] = (ushort)(i * 257);
        return new GammaRamp(values);
    }

    private static GammaRamp CurvedRamp()
    {
        var values = IdentityRamp().ToArray();
        for (int i = 1; i < 255; i++)
        {
            values[256 + i] = (ushort)Math.Min(65535, i * 257 + 100);
            values[512 + i] = (ushort)Math.Max(0, i * 257 - 100);
        }
        return new GammaRamp(values);
    }
}
