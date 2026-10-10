using System.Globalization;
using System.Windows.Input;
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
    public void PreviewGammaAppliesWithoutChangingProfile()
    {
        GammaRamp current = IdentityRamp();
        int writes = 0;
        var controller = new GammaController(() => ["A"], _ => current, (_, target) =>
        { writes++; current = target; return true; });
        controller.RefreshBaseline();
        var profile = new GammaProfile { Gamma = 1.5 };
        controller.ApplyProfile(profile, 2.0);
        Assert.True(writes == 0); // Preview never applies a profile that is not active.
        controller.ApplyProfile(profile);
        controller.ApplyProfile(profile, 2.0);
        var display = controller.Displays.Single();
        Assert.True(writes == 2 && display.Confirmed && display.AppliedGamma == 2.0 && profile.Gamma == 1.5);
        profile.Gamma = 2.0; // Commit after preview needs no second write.
        controller.ApplyProfile(profile);
        Assert.True(writes == 2);
        current = CurvedRamp(); // External change: preview leaves it alone.
        controller.ApplyProfile(profile, 1.2);
        Assert.True(writes == 2 && current.Matches(CurvedRamp()));
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
        Assert.True(writes.Count == 0 && controller.Displays.All(d => !d.Confirmed && d.Kind == DisplayStatusKind.BaselineCaptured && !d.IsWarning));
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
    public void KeyPressMapsToShortcutTextOrUserMessage()
    {
        Assert.Equal("Ctrl+Alt+K", Hotkey.FromKeyPress(Key.K, ModifierKeys.Control | ModifierKeys.Alt, out string? error));
        Assert.Null(error);
        Assert.Equal("Alt+7", Hotkey.FromKeyPress(Key.D7, ModifierKeys.Alt, out _));
        Assert.Equal("Ctrl+Alt+Shift+F12", Hotkey.FromKeyPress(Key.F12, ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift, out _));
        Assert.Equal("F9", Hotkey.FromKeyPress(Key.F9, ModifierKeys.None, out _));
        Assert.Null(Hotkey.FromKeyPress(Key.NumPad3, ModifierKeys.Control, out error));
        Assert.Equal("Use the top-row digits, not the numpad.", error);
        Assert.Null(Hotkey.FromKeyPress(Key.Space, ModifierKeys.Control, out error));
        Assert.Equal("Use a letter, digit, or F1–F12, with optional Ctrl, Alt or Shift.", error);
        Assert.Null(Hotkey.FromKeyPress(Key.Q, ModifierKeys.Shift, out error));
        Assert.Equal("Letters and digits need Ctrl or Alt.", error);
        Assert.Equal("Ctrl+Shift+", Hotkey.Prefix(ModifierKeys.Shift | ModifierKeys.Control));
    }

    [Fact]
    public void GammaTextAcceptsCurrentOrInvariantFormatInRange()
    {
        var hungarian = CultureInfo.GetCultureInfo("hu-HU");
        Assert.True(GammaProfile.TryParseGamma("1,25", hungarian, out double value) && value == 1.25);
        Assert.True(GammaProfile.TryParseGamma("1.25", hungarian, out value) && value == 1.25);
        Assert.True(GammaProfile.TryParseGamma("0.5", CultureInfo.InvariantCulture, out value) && value == 0.5);
        Assert.True(GammaProfile.TryParseGamma("4", CultureInfo.InvariantCulture, out value) && value == 4);
        foreach (string invalid in new[] { "0.49", "4.01", "1.255", "abc", "", "NaN", "Infinity" })
            Assert.False(GammaProfile.TryParseGamma(invalid, hungarian, out _));
    }

    [Fact]
    public void ProfileNamesAndHotkeysMustBeUnique()
    {
        var settings = new AppSettings();
        settings.Profiles.Add(new GammaProfile { Name = "Gaming", Hotkey = "Ctrl+Alt+G" });
        Assert.False(settings.NameAvailable("gaming"));
        Assert.False(settings.NameAvailable(""));
        Assert.True(settings.NameAvailable("Gaming", exclude: 1));
        Assert.True(settings.NameAvailable("Movies"));
        Assert.True(settings.HotkeyTaken("Ctrl+Alt+G", exclude: 0));
        Assert.False(settings.HotkeyTaken("Ctrl+Alt+G", exclude: 1));
        Assert.False(settings.HotkeyTaken("", exclude: 0));
        settings.BaselineHotkey = "Ctrl+Alt+N";
        Assert.True(settings.HotkeyTaken("Ctrl+Alt+N", exclude: 0));
        Assert.False(settings.HotkeyTaken("Ctrl+Alt+N", exclude: -1));
        Assert.True(settings.HotkeyTaken("Ctrl+Alt+G", exclude: -1));
        settings.Profiles[0].Hotkey = "Ctrl+Alt+N";
        Assert.Throws<ArgumentException>(() => settings.Save(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())));
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

    public static TheoryData<double> GammaSteps => [.. Enumerable.Range(2, 15).Select(i => i * 0.25)];

    [Theory]
    [MemberData(nameof(GammaSteps))]
    public void BrightIsMonotonicWithFixedEndpointsAcrossGammaRange(double gamma)
    {
        foreach (var source in new[] { IdentityRamp(), CurvedRamp() })
        {
            var bright = source.Bright(gamma);
            for (int channel = 0; channel < 3; channel++)
            {
                int offset = channel * 256;
                Assert.Equal(source[offset], bright[offset]);
                Assert.Equal(source[offset + 255], bright[offset + 255]);
                for (int i = 1; i < 256; i++) Assert.True(bright[offset + i] >= bright[offset + i - 1]);
            }
        }
    }

    [Fact]
    public void MatchesAcceptsDifferencesUpToTolerance()
    {
        var source = IdentityRamp();
        var values = source.ToArray();
        values[300] += GammaRamp.ReadbackTolerance;
        Assert.True(source.Matches(new GammaRamp(values)));
        values[300]++;
        Assert.False(source.Matches(new GammaRamp(values)));
    }

    [Fact]
    public void ReadBackMismatchIsNotConfirmed()
    {
        var original = IdentityRamp();
        // The driver accepts the write but keeps the old ramp.
        var controller = new GammaController(() => ["A"], _ => original, (_, _) => true);
        controller.RefreshBaseline();
        controller.ApplyProfile(new GammaProfile { Gamma = 2 });
        var display = controller.Displays.Single();
        Assert.False(display.Confirmed);
        Assert.True(display.Overridden);
        Assert.True(display.Kind == DisplayStatusKind.NotConfirmed && display.IsWarning);
        Assert.Equal("Not confirmed: read-back differs", display.Status);
        Assert.False(controller.HasAppliedRamp);
    }

    [Fact]
    public void ExternalBaselineRestoreIsReportedAndNotRestoredAgain()
    {
        var original = IdentityRamp();
        GammaRamp current = original;
        int writes = 0;
        var controller = new GammaController(() => ["A"], _ => current, (_, target) =>
        { writes++; current = target; return true; });
        controller.RefreshBaseline();
        controller.ApplyProfile(new GammaProfile { Gamma = 2 });
        current = original;
        controller.CheckForOverrides();
        var display = controller.Displays.Single();
        Assert.False(display.Confirmed);
        Assert.True(display.Kind == DisplayStatusKind.Overridden && display.Detail == "baseline restored externally");
        controller.Restore();
        Assert.Equal(1, writes);
        Assert.True(display.Kind == DisplayStatusKind.RestoreSkipped && display.IsWarning);
    }

    [Fact]
    public void DisplayLabelIsResolvedOncePerCapture()
    {
        var original = IdentityRamp();
        int lookups = 0;
        var controller = new GammaController(() => ["A"], _ => original, (_, _) => true,
            name => { lookups++; return "Label " + name; });
        controller.RefreshBaseline();
        controller.ApplyProfile(new GammaProfile { Gamma = 2 });
        controller.CheckForOverrides();
        Assert.Equal("Label A", controller.Displays.Single().Label);
        Assert.Equal(1, lookups);
        controller.HandleDisplayChange();
        Assert.Equal(2, lookups);
    }

    [Fact]
    public void DisplayChangeRestoresAndRecapturesDisplays()
    {
        var original = IdentityRamp();
        var ramps = new Dictionary<string, GammaRamp> { ["A"] = original, ["B"] = original, ["C"] = original };
        string[] present = ["A", "B"];
        var writes = new List<string>();
        var controller = new GammaController(() => present, name => ramps[name], (name, ramp) =>
        { writes.Add(name); ramps[name] = ramp; return true; });
        controller.RefreshBaseline();
        controller.ApplyProfile(new GammaProfile { Gamma = 2 });
        writes.Clear();
        present = ["A", "C"];
        controller.HandleDisplayChange();
        Assert.True(writes.SequenceEqual(["A"]) && ramps["A"].Matches(original));
        Assert.True(controller.Displays.Select(d => d.DeviceName).Order().SequenceEqual(["A", "C"]));
        Assert.All(controller.Displays, d => Assert.Equal(DisplayStatusKind.BaselineCaptured, d.Kind));
        Assert.False(controller.HasAppliedRamp);
    }

    [Fact]
    public void UnreadableOrUnsafeRampsAreNeverWritten()
    {
        var unsafeRamp = new GammaRamp(new ushort[GammaRamp.Length]);
        var ramps = new Dictionary<string, GammaRamp?> { ["A"] = null, ["B"] = unsafeRamp };
        int writes = 0;
        var controller = new GammaController(() => ["A", "B"], name => ramps[name], (_, _) => { writes++; return true; });
        controller.RefreshBaseline();
        var a = controller.Displays.Single(d => d.DeviceName == "A");
        var b = controller.Displays.Single(d => d.DeviceName == "B");
        Assert.True(a.Kind == DisplayStatusKind.Failed && a.Detail == "gamma read unavailable");
        Assert.True(b.Kind == DisplayStatusKind.Unsupported && b.BaselineRamp is null);
        controller.ApplyProfile(new GammaProfile { Gamma = 2 });
        Assert.Equal(0, writes);
        Assert.True(a.Kind == DisplayStatusKind.Failed && a.Detail == "current ramp unreadable" && !a.Overridden);
        Assert.True(b.Kind == DisplayStatusKind.Failed && b.Detail == "external ramp unsafe; not reapplied" && b.Overridden);
    }

    [Fact]
    public void SafeExternalRampBecomesTheNewBaseline()
    {
        var original = IdentityRamp();
        var external = original.Bright(1.5);
        GammaRamp current = original;
        var controller = new GammaController(() => ["A"], _ => current, (_, target) => { current = target; return true; });
        controller.RefreshBaseline();
        current = external;
        controller.ApplyProfile(new GammaProfile { Gamma = 2 });
        var display = controller.Displays.Single();
        Assert.True(display.Confirmed && display.BaselineRamp!.Matches(external) && !external.Matches(original));
        controller.Restore();
        Assert.True(current.Matches(external) && display.Kind == DisplayStatusKind.BaselineRestored);
    }

    [Fact]
    public void HdrDisplayIsNeverReadOrWritten()
    {
        var original = IdentityRamp();
        var ramps = new Dictionary<string, GammaRamp> { ["A"] = original, ["B"] = original };
        var touched = new List<string>();
        var controller = new GammaController(() => ["A", "B"], name => { touched.Add(name); return ramps[name]; },
            (name, ramp) => { touched.Add(name); ramps[name] = ramp; return true; }, isHdr: name => name == "B");
        controller.RefreshBaseline();
        controller.ApplyProfile(new GammaProfile { Gamma = 2 });
        controller.CheckForOverrides();
        controller.Restore();
        var b = controller.Displays.Single(d => d.DeviceName == "B");
        Assert.DoesNotContain("B", touched);
        Assert.True(b.Kind == DisplayStatusKind.Skipped && b.IsWarning);
        Assert.Equal("Skipped: HDR active", b.Status);
        Assert.True(ramps["A"].Matches(original) && touched.Contains("A"));
    }

    [Fact]
    public void HdrTurnedOnRestoresOwnedRampBeforeSkipping()
    {
        var original = IdentityRamp();
        GammaRamp current = original;
        bool hdr = false;
        int writes = 0;
        var controller = new GammaController(() => ["A"], _ => current, (_, target) =>
        { writes++; current = target; return true; }, isHdr: _ => hdr);
        controller.RefreshBaseline();
        controller.ApplyProfile(new GammaProfile { Gamma = 2 });
        hdr = true;
        controller.CheckForOverrides();
        var display = controller.Displays.Single();
        Assert.True(writes == 2 && current.Matches(original) && !controller.HasAppliedRamp);
        Assert.Equal(DisplayStatusKind.Skipped, display.Kind);
        controller.ApplyProfile(new GammaProfile { Gamma = 2 });
        controller.Restore();
        Assert.Equal(2, writes);
        hdr = false;
        controller.ApplyProfile(new GammaProfile { Gamma = 2 });
        Assert.True(display.Confirmed && display.BaselineRamp!.Matches(original));
    }

    [Fact]
    public void FailedRestoreUnderHdrStaysVisible()
    {
        GammaRamp current = IdentityRamp();
        bool hdr = false;
        var controller = new GammaController(() => ["A"], _ => current, (_, target) =>
        { if (hdr) return false; current = target; return true; }, isHdr: _ => hdr);
        controller.RefreshBaseline();
        controller.ApplyProfile(new GammaProfile { Gamma = 2 });
        hdr = true;
        controller.HandleDisplayChange();
        var display = controller.Displays.Single();
        Assert.True(controller.HasFailedRestore && display.Kind == DisplayStatusKind.RestoreFailed);
        hdr = false;
        controller.HandleDisplayChange();
        Assert.True(display.Kind == DisplayStatusKind.RestoreFailed && display.BaselineRamp!.Matches(IdentityRamp()));
    }

    [Fact]
    public void HdrQueryFailureTreatsDisplayAsSdr()
    {
        var original = IdentityRamp();
        GammaRamp current = original;
        var controller = new GammaController(() => ["A"], _ => current, (_, target) => { current = target; return true; },
            isHdr: _ => throw new InvalidOperationException());
        controller.RefreshBaseline();
        controller.ApplyProfile(new GammaProfile { Gamma = 2 });
        Assert.True(controller.Displays.Single().Confirmed);
    }

    [Fact]
    public void SettingsRejectLongAndDuplicateProfileNames()
    {
        string directory = Path.Combine(Path.GetTempPath(), "GammaHotkeyTests-" + Guid.NewGuid());
        string path = Path.Combine(directory, "settings.json");
        try
        {
            var settings = new AppSettings();
            settings.Profiles[0].Name = new string('N', 60);
            settings.Save(path);
            settings.Profiles[0].Name = new string('N', 61);
            Assert.ThrowsAny<ArgumentException>(() => settings.Save(path));
            settings.Profiles[0].Name = "Gaming";
            settings.Profiles.Add(new GammaProfile { Name = "GAMING" });
            Assert.ThrowsAny<ArgumentException>(() => settings.Save(path));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void SettingsWithNullProfileOrHotkeyFallBackToDefaults()
    {
        string directory = Path.Combine(Path.GetTempPath(), "GammaHotkeyTests-" + Guid.NewGuid());
        string path = Path.Combine(directory, "settings.json");
        try
        {
            Directory.CreateDirectory(directory);
            foreach (string json in new[] { "{\"schemaVersion\":2,\"profiles\":[null]}",
                "{\"schemaVersion\":2,\"profiles\":[{\"name\":\"A\",\"gamma\":1,\"hotkey\":null}]}" })
            {
                File.WriteAllText(path, json);
                var restored = AppSettings.Load(path, out var warning);
                Assert.True(warning is not null && restored.Profiles.Count == 1 && restored.Profiles[0].Name == "Default");
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void ProfileTargetsSelectedMonitorsAllWhenNullNoneWhenEmpty()
    {
        var clone = new DisplayState { DeviceName = "A", MonitorIds = ["mon-1", "mon-2"] };
        var unknown = new DisplayState { DeviceName = "B" };
        Assert.True(new GammaProfile().Targets(clone) && new GammaProfile().Targets(unknown));
        var profile = new GammaProfile { Displays = ["MON-2"] };
        Assert.True(profile.Targets(clone));
        Assert.False(profile.Targets(unknown));
        Assert.False(new GammaProfile { Displays = ["mon-3"] }.Targets(clone));
        Assert.False(new GammaProfile { Displays = [] }.Targets(clone));
    }

    [Fact]
    public void ApplyRestoresOwnedNonTargetedDisplayAndLeavesExternalAlone()
    {
        var original = IdentityRamp();
        var ramps = new Dictionary<string, GammaRamp> { ["A"] = original, ["B"] = original, ["C"] = original };
        var writes = new List<string>();
        var controller = new GammaController(() => ["A", "B", "C"], name => ramps[name],
            (name, ramp) => { writes.Add(name); ramps[name] = ramp; return true; }, monitorIds: name => ["mon-" + name]);
        controller.RefreshBaseline();
        controller.ApplyProfile(new GammaProfile { Gamma = 2 });
        var external = original.Bright(1.5);
        ramps["C"] = external;
        writes.Clear();
        var main = new GammaProfile { Name = "Main", Gamma = 1.5, Displays = ["mon-A"] };
        controller.ApplyProfile(main);
        var byName = controller.Displays.ToDictionary(d => d.DeviceName);
        Assert.True(byName["A"].Confirmed && ReferenceEquals(byName["A"].AppliedProfile, main));
        Assert.True(byName["B"].Kind == DisplayStatusKind.BaselineRestored && ramps["B"].Matches(original));
        Assert.True(byName["C"].Kind == DisplayStatusKind.RestoreSkipped && ramps["C"].Matches(external));
        Assert.DoesNotContain("C", writes);
    }

    [Fact]
    public void PreviewSkipsNonTargetedDisplays()
    {
        var original = IdentityRamp();
        var ramps = new Dictionary<string, GammaRamp> { ["A"] = original, ["B"] = original };
        var touched = new List<string>();
        var controller = new GammaController(() => ["A", "B"], name => { touched.Add(name); return ramps[name]; },
            (name, ramp) => { touched.Add(name); ramps[name] = ramp; return true; }, monitorIds: name => ["mon-" + name]);
        controller.RefreshBaseline();
        var all = new GammaProfile { Gamma = 2 };
        controller.ApplyProfile(all);
        all.Displays = ["mon-A"];
        touched.Clear();
        controller.ApplyProfile(all, 1.5);
        Assert.DoesNotContain("B", touched);
        Assert.True(ramps["A"].Matches(original.Bright(1.5)) && ramps["B"].Matches(original.Bright(2)));
    }

    [Fact]
    public void SettingsDisplaysDefaultToAllAndRejectInvalidEntries()
    {
        string directory = Path.Combine(Path.GetTempPath(), "GammaHotkeyTests-" + Guid.NewGuid());
        string path = Path.Combine(directory, "settings.json");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(path, "{\"schemaVersion\":2,\"profiles\":[{\"name\":\"A\",\"gamma\":1.2,\"hotkey\":\"\"}]}");
            var legacy = AppSettings.Load(path, out var warning);
            Assert.True(warning is null && legacy.Profiles[0].Displays is null);
            legacy.Profiles[0].Displays = ["mon-1"];
            legacy.Save(path);
            Assert.Equal(["mon-1"], AppSettings.Load(path, out _).Profiles[0].Displays);
            foreach (string displays in new[] { "[null]", "[\" \"]" })
            {
                File.WriteAllText(path, "{\"schemaVersion\":2,\"profiles\":[{\"name\":\"A\",\"gamma\":1,\"hotkey\":\"\",\"displays\":" + displays + "}]}");
                var restored = AppSettings.Load(path, out warning);
                Assert.True(warning is not null && restored.Profiles[0].Name == "Default");
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
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
