using Microsoft.Win32;
using System.IO;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using TextBox = System.Windows.Controls.TextBox;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;
using MessageBox = System.Windows.MessageBox;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Controls.Primitives;
using WF = System.Windows.Forms;

namespace GammaHotkey;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "tray is disposed in OnClosed")]
public partial class MainWindow : Window
{
    private const int ProfileHotkeyId = 0x4800;
    private const string StartupKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupValue = "GammaHotkey";
    private readonly GammaController controller = new();
    private readonly AppSettings settings;
    private readonly WF.NotifyIcon tray;
    private readonly DispatcherTimer monitorTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer displayTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly List<int> registeredProfiles = [];
    private readonly Dictionary<string, string> loggedStatus = new(StringComparer.OrdinalIgnoreCase);
    private readonly object restoreGate = new();
    private nint hwnd;
    private string? settingsWarning;
    private string? profileHotkeyWarning;
    private string? altGrWarning;
    private string? shortcutFeedback;
    private bool loading;
    private bool capturingShortcut;
    private bool restoringStartupCheck;
    private bool exiting;
    private bool cleaned;
    private bool busy;
    private bool editingGamma;
    public bool StartHidden => settings.StartInTray;

    public MainWindow()
    {
        settings = AppSettings.Load(AppSettings.DefaultPath, out settingsWarning);
        InitializeComponent();
        VersionText.Text = $"Version {typeof(MainWindow).Assembly.GetName().Version!.ToString(3)}";
        loading = true;
        ProfileSelect.ItemsSource = settings.Profiles;
        ProfileSelect.SelectedIndex = settings.SelectedProfile;
        StartMinimized.IsChecked = settings.StartInTray;
        ThemeSelect.SelectedIndex = settings.Theme switch { "Light" => 0, "Dark" => 1, _ => 2 };
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupKey);
            StartWithWindows.IsChecked = key?.GetValue(StartupValue) as string == $"\"{WF.Application.ExecutablePath}\"";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        { settingsWarning = "Windows startup setting could not be read."; }
        loading = false;
        GammaSlider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => CommitGamma(GammaSlider.Value)));
        GammaInput.TextChanged += (_, _) => { if (!loading) { editingGamma = true; UpdateView(); } };
        LoadProfile();
        ApplyTheme();
        ShowTab(false);

        var menu = new WF.ContextMenuStrip();
        menu.Items.Add("Show", null, (_, _) => Dispatcher.Invoke(ShowWindow));
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(ExitApp));
#if DEBUG
        menu.Items.Add("Debug: throw", null, (_, _) =>
            Dispatcher.BeginInvoke(new Action(() => throw new InvalidOperationException("Debug crash test."))));
#endif
        tray = new WF.NotifyIcon
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(WF.Application.ExecutablePath) ?? System.Drawing.SystemIcons.Application,
            Text = "Gamma Hotkey", ContextMenuStrip = menu, Visible = true
        };
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowWindow);

        controller.Changed += UpdateView;
        controller.Changed += LogStatusChanges;
        monitorTimer.Tick += (_, _) => RunAction(controller.CheckForOverrides);
        displayTimer.Tick += (_, _) => { displayTimer.Stop(); RunAction(controller.HandleDisplayChange); };
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SourceInitialized += (_, _) =>
        {
            hwnd = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
            RegisterProfileHotkeys();
            ApplyTheme();
        };
        Activated += (_, _) =>
        {
            if (settings.Theme == "System") ApplyTheme();
            UpdateAltGrWarning();
            UpdateView();
            if (!exiting && ShortcutInput.IsKeyboardFocused && !capturingShortcut)
            { capturingShortcut = true; RegisterProfileHotkeys(); }
        };
        Deactivated += (_, _) => { if (capturingShortcut) StopShortcutCapture(); };
        Closing += (_, e) =>
        {
            if (!exiting) { e.Cancel = true; Hide(); return; }
            Cleanup();
        };
        controller.RefreshBaseline();
        UpdateView();
        if (StartHidden) new WindowInteropHelper(this).EnsureHandle();
    }

    private void SaveSettings()
    {
        if (loading) return;
        settings.StartInTray = StartMinimized.IsChecked == true;
        try { settings.Save(AppSettings.DefaultPath); settingsWarning = null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // ArgumentException here is an app bug, not a user error.
            Log.Write("Settings save failed: " + ex);
            settingsWarning = "Settings could not be saved.";
        }
        UpdateView();
    }

    private void LoadProfile()
    {
        loading = true;
        var profile = settings.Profiles[settings.SelectedProfile];
        GammaSlider.Value = profile.Gamma;
        GammaInput.Text = profile.Gamma.ToString("F2", CultureInfo.CurrentCulture);
        ShortcutInput.Text = profile.Hotkey == "" ? "None" : profile.Hotkey;
        loading = false;
        editingGamma = false;
        UpdateView();
    }

    private void SelectProfile(int index)
    {
        if (index < 0 || busy || exiting) return;
        settings.SelectedProfile = index;
        LoadProfile();
        SaveSettings();
    }

    private void ProfileSelect_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!loading) SelectProfile(ProfileSelect.SelectedIndex);
    }

    private void ApplyButton_Click(object sender, RoutedEventArgs e) =>
        RunAction(() => controller.ApplyProfile(settings.Profiles[settings.SelectedProfile]));

    private void GammaSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (loading || GammaInput is null) return;
        GammaInput.Text = GammaSlider.Value.ToString("F2", CultureInfo.CurrentCulture);
        editingGamma = true;
        UpdateView();
    }

    private void CommitGamma(double value)
    {
        value = Math.Round(value, 2);
        loading = true;
        GammaSlider.Value = value;
        GammaInput.Text = value.ToString("F2", CultureInfo.CurrentCulture);
        loading = false;
        editingGamma = false;
        if (settings.Profiles[settings.SelectedProfile].Gamma != value)
        {
            settings.Profiles[settings.SelectedProfile].Gamma = value;
            SaveSettings();
        }
        UpdateView();
    }

    private void CommitGammaInput()
    {
        if ((double.TryParse(GammaInput.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double value) ||
             double.TryParse(GammaInput.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) &&
            double.IsFinite(value) && value >= 0.5 && value <= 4 && Math.Abs(value * 100 - Math.Round(value * 100)) < 0.000001)
        {
            shortcutFeedback = null;
            CommitGamma(value);
        }
        else
        {
            shortcutFeedback = "Enter a gamma from 0.50 to 4.00 in steps of 0.01.";
            GammaInput.Text = settings.Profiles[settings.SelectedProfile].Gamma.ToString("F2", CultureInfo.CurrentCulture);
            editingGamma = false;
            UpdateView();
        }
    }

    private void GammaInput_LostFocus(object sender, RoutedEventArgs e) => CommitGammaInput();
    private void GammaInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitGammaInput(); Keyboard.ClearFocus(); e.Handled = true; }
        else if (e.Key == Key.Escape) { LoadProfile(); Keyboard.ClearFocus(); e.Handled = true; }
    }

    private void SliderKeyCommit(object sender, System.Windows.Input.KeyEventArgs e) { if (editingGamma) CommitGamma(GammaSlider.Value); }

    private void ShortcutInput_GotFocus(object sender, RoutedEventArgs e)
    {
        capturingShortcut = true;
        RegisterProfileHotkeys();
    }
    private void ShortcutInput_LostFocus(object sender, RoutedEventArgs e) => StopShortcutCapture();
    private void StopShortcutCapture()
    {
        capturingShortcut = false;
        LoadProfile();
        RegisterProfileHotkeys();
    }

    private void ShortcutInput_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape) { LoadProfile(); return; }
        if ((key == Key.Delete || key == Key.Back) && Keyboard.Modifiers == ModifierKeys.None) { SetShortcut(""); return; }
        var modifiers = Keyboard.Modifiers;
        string prefix = (modifiers.HasFlag(ModifierKeys.Control) ? "Ctrl+" : "") + (modifiers.HasFlag(ModifierKeys.Alt) ? "Alt+" : "") +
            (modifiers.HasFlag(ModifierKeys.Shift) ? "Shift+" : "");
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift)
        {
            ShortcutInput.Text = prefix + "…";
            return;
        }
        if (key is >= Key.NumPad0 and <= Key.NumPad9)
        { shortcutFeedback = "Use the top-row digits, not the numpad."; UpdateView(); return; }
        string? value = key switch
        {
            >= Key.A and <= Key.Z => key.ToString(),
            >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(CultureInfo.InvariantCulture),
            >= Key.F1 and <= Key.F12 => $"F{key - Key.F1 + 1}",
            _ => null
        };
        if (value is null) { shortcutFeedback = "Use a letter, digit, or F1–F12, with optional Ctrl, Alt or Shift."; UpdateView(); return; }
        if (!Hotkey.TryParse(prefix + value, out _, out _))
        { shortcutFeedback = "Letters and digits need Ctrl or Alt."; UpdateView(); return; }
        SetShortcut(prefix + value);
    }

    private void SetShortcut(string key)
    {
        if (key != "" && settings.Profiles.Where((_, i) => i != settings.SelectedProfile).Any(p => p.Hotkey == key))
        { shortcutFeedback = "Already assigned to another profile."; UpdateView(); return; }
        shortcutFeedback = null;
        settings.Profiles[settings.SelectedProfile].Hotkey = key;
        LoadProfile();
        RegisterProfileHotkeys();
        SaveSettings();
    }

    private void RegisterProfileHotkeys()
    {
        if (hwnd == 0) return;
        foreach (int id in registeredProfiles) NativeMethods.UnregisterHotKey(hwnd, id);
        registeredProfiles.Clear();
        profileHotkeyWarning = null;
        UpdateAltGrWarning();
        if (!capturingShortcut)
            for (int i = 0; i < settings.Profiles.Count; i++)
            {
                string key = settings.Profiles[i].Hotkey;
                if (key == "") continue;
                if (!Hotkey.TryParse(key, out uint modifiers, out uint virtualKey)) continue;
                int id = ProfileHotkeyId + i;
                if (NativeMethods.RegisterHotKey(hwnd, id, NativeMethods.ModNoRepeat | modifiers, virtualKey))
                    registeredProfiles.Add(id);
                else profileHotkeyWarning = $"Hotkey unavailable: {key} ({settings.Profiles[i].Name}).";
            }
        UpdateView();
    }

    private void UpdateAltGrWarning()
    {
        var conflicts = settings.Profiles
            .Select(p => Hotkey.TryParse(p.Hotkey, out uint modifiers, out uint virtualKey) &&
                NativeMethods.AltGrCharacter(modifiers, virtualKey) is { } character ? $"{p.Hotkey} types \"{character}\"" : null)
            .Where(c => c is not null).ToArray();
        altGrWarning = conflicts.Length == 0 ? null :
            string.Join(", ", conflicts) + " on this keyboard layout; the shortcut blocks that character.";
    }

    private nint WndProc(nint handle, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == NativeMethods.WmHotkey && (int)wParam >= ProfileHotkeyId &&
            (int)wParam < ProfileHotkeyId + settings.Profiles.Count)
        {
            SelectProfile((int)wParam - ProfileHotkeyId);
            ProfileSelect.SelectedIndex = settings.SelectedProfile;
            RunAction(() => controller.ApplyProfile(settings.Profiles[settings.SelectedProfile]));
            handled = true;
        }
        else if (message == NativeMethods.WmDisplayChange) ScheduleDisplayRefresh();
        else if (message == NativeMethods.WmInputLangChange) { UpdateAltGrWarning(); UpdateView(); }
        else if (message == 0x001A && settings.Theme == "System" && !exiting) Dispatcher.BeginInvoke(ApplyTheme);
        return 0;
    }

    private void RunAction(Action action)
    {
        if (busy || exiting) return;
        busy = true;
        try { action(); }
        finally { busy = false; UpdateView(); }
    }

    private void UpdateView()
    {
        if (tray is null || !IsInitialized) return;
        DeleteButton.IsEnabled = settings.Profiles.Count > 1;
        var selected = settings.Profiles[settings.SelectedProfile];
        var confirmed = controller.Displays.Where(d => d.Confirmed).ToArray();
        var active = confirmed.GroupBy(d => d.AppliedProfile)
            .OrderByDescending(g => g.Count()).ThenByDescending(g => ReferenceEquals(g.Key, selected)).FirstOrDefault();
        string summary = active is null ? "None · Apply required" :
            $"{active.Key!.Name}: active on {active.Count()}/{controller.Displays.Count} displays";
        if (controller.HasFailedRestore) summary = "Restore failed — check display settings or restart";
        else if (active is not null && (!ReferenceEquals(active.Key, selected) ||
            active.Any(d => d.AppliedGamma != selected.Gamma) || editingGamma))
            summary += " (selection pending)";
        AppliedSummary.Text = summary;
        AppliedSummary.Foreground = controller.HasFailedRestore || controller.HasOverride ? (Brush)Resources["Warning"] : (Brush)Resources["Text"];
        DisplayList.Children.Clear();
        if (controller.Displays.Count == 0) DisplayList.Children.Add(new TextBlock { Text = "No active display found." });
        foreach (var display in controller.Displays)
        {
            bool warning = display.Overridden || display.RestoreFailed ||
                display.Status.StartsWith("Failed", StringComparison.Ordinal) ||
                display.Status.StartsWith("Unsupported", StringComparison.Ordinal) ||
                display.Status.StartsWith("Not confirmed", StringComparison.Ordinal) ||
                display.Status.StartsWith("Restore skipped", StringComparison.Ordinal);
            string name = NativeMethods.DisplayLabel(display.DeviceName);
            var row = new Grid { Margin = new Thickness(0, 5, 0, 5), MinHeight = 22 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(new System.Windows.Shapes.Ellipse { Width = 10, Height = 10,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Fill = (Brush)Resources[warning ? "Warning" : "Accent"] });
            var title = new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(title, 1);
            row.Children.Add(title);
            var status = new TextBlock { Text = display.Status, VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)Resources[warning ? "Warning" : "Muted"] };
            Grid.SetColumn(status, 2);
            row.Children.Add(status);
            DisplayList.Children.Add(row);
        }
        Note.Text = string.Join("  ·  ", new[] { shortcutFeedback, settingsWarning, profileHotkeyWarning, altGrWarning }.Where(w => w is not null));
        Note.SetResourceReference(TextBlock.ForegroundProperty, Note.Text == "" ? "Muted" : "Warning");
        Note.ToolTip = Note.Text == "" ? null : Note.Text;
        if (Note.Text == "") Note.Text = "Close hides to tray";
        tray.Text = controller.HasFailedRestore ? "Gamma Hotkey: restore failed" :
            controller.HasOverride ? "Gamma Hotkey: override detected" : "Gamma Hotkey";
        monitorTimer.IsEnabled = !exiting && controller.HasAppliedRamp;
    }

    private void ShowTab(bool isSettings)
    {
        MainPage.Visibility = isSettings ? Visibility.Collapsed : Visibility.Visible;
        SettingsPage.Visibility = isSettings ? Visibility.Visible : Visibility.Collapsed;
        MainTab.Foreground = (Brush)Resources[isSettings ? "Muted" : "Accent"];
        SettingsTab.Foreground = (Brush)Resources[isSettings ? "Accent" : "Muted"];
        MainTab.BorderThickness = new Thickness(0, 0, 0, isSettings ? 0 : 4);
        SettingsTab.BorderThickness = new Thickness(0, 0, 0, isSettings ? 4 : 0);
        MainTab.BorderBrush = SettingsTab.BorderBrush = (Brush)Resources["Accent"];
        MainTab.FontWeight = isSettings ? FontWeights.Normal : FontWeights.Bold;
        SettingsTab.FontWeight = isSettings ? FontWeights.Bold : FontWeights.Normal;
    }
    private void MainTab_Click(object sender, RoutedEventArgs e) => ShowTab(false);
    private void SettingsTab_Click(object sender, RoutedEventArgs e) => ShowTab(true);

    private void ApplyTheme()
    {
        bool dark = settings.Theme == "Dark" || settings.Theme == "System" && SystemDarkTheme();
        string[] colors = dark ?
            ["#17191B", "#24272A", "#1C1F22", "#F0F2F3", "#C3C9CC", "#25C5B8", "#4C5257", "#FFB36B", "#35393C", "#37D6C8"] :
            ["#F5F6F6", "#FFFFFF", "#F0F2F2", "#202528", "#4C5659", "#007F75", "#B5BEC0", "#9C4300", "#E7ECEC", "#00988D"];
        string[] keys = ["Background", "Surface", "Field", "Text", "Muted", "Accent", "Outline", "Warning", "Hover", "AccentHover"];
        for (int i = 0; i < keys.Length; i++) Resources[keys[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i])!);
        ShowTab(SettingsPage.Visibility == Visibility.Visible);
        if (hwnd != 0)
        {
            int darkTitle = dark ? 1 : 0;
            if (DwmSetWindowAttribute(hwnd, 20, ref darkTitle, sizeof(int)) != 0)
                _ = DwmSetWindowAttribute(hwnd, 19, ref darkTitle, sizeof(int));
        }
        UpdateView();
    }

    private static bool SystemDarkTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int valueSize);

    private void ThemeSelect_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (loading) return;
        settings.Theme = ((ComboBoxItem)ThemeSelect.SelectedItem).Content.ToString()!;
        ApplyTheme();
        SaveSettings();
    }
    private void StartMinimized_Changed(object sender, RoutedEventArgs e) { if (!loading) SaveSettings(); }
    private void StartWithWindows_Changed(object sender, RoutedEventArgs e)
    {
        if (loading || restoringStartupCheck) return;
        try
        {
            if (StartWithWindows.IsChecked == true)
            {
                using var key = Registry.CurrentUser.CreateSubKey(StartupKey);
                key.SetValue(StartupValue, $"\"{WF.Application.ExecutablePath}\"");
            }
            else
            {
                using var key = Registry.CurrentUser.OpenSubKey(StartupKey, writable: true);
                key?.DeleteValue(StartupValue, throwOnMissingValue: false);
            }
            settingsWarning = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            settingsWarning = "Windows startup setting could not be changed.";
            restoringStartupCheck = true;
            StartWithWindows.IsChecked = StartWithWindows.IsChecked != true;
            restoringStartupCheck = false;
        }
        UpdateView();
    }

    private string? PromptName(string title, string initial)
    {
        var dialog = Dialog(title, 350, 185);
        var panel = (StackPanel)dialog.Content;
        var input = new TextBox { Text = initial, MaxLength = 60, Margin = new Thickness(0, 0, 0, 18),
            Style = (Style)Resources[typeof(TextBox)] };
        panel.Children.Add(input);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        var ok = new Button { Content = title == "New profile" ? "Create" : "Rename", Width = 90, IsDefault = true,
            Style = (Style)Resources[typeof(Button)], Background = (Brush)Resources["Accent"],
            Foreground = (Brush)Resources["Background"], Tag = "Accent" };
        var cancel = new Button { Content = "Cancel", Width = 90, Margin = new Thickness(8, 0, 0, 0), IsCancel = true,
            Style = (Style)Resources[typeof(Button)] };
        ok.Click += (_, _) => dialog.DialogResult = true;
        buttons.Children.Add(ok); buttons.Children.Add(cancel); panel.Children.Add(buttons);
        dialog.Loaded += (_, _) => { input.SelectAll(); input.Focus(); };
        return dialog.ShowDialog() == true ? input.Text.Trim() : null;
    }

    private Window Dialog(string title, double width, double height)
    {
        var dialog = new Window { Title = title, Owner = this, Width = width, Height = height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (Brush)Resources["Background"], Foreground = (Brush)Resources["Text"],
            FontFamily = FontFamily, FontSize = FontSize };
        dialog.Resources.MergedDictionaries.Add(Resources);
        var panel = new StackPanel { Margin = new Thickness(18) };
        dialog.Content = panel;
        dialog.SourceInitialized += (_, _) =>
        {
            int darkTitle = settings.Theme == "Dark" || settings.Theme == "System" && SystemDarkTheme() ? 1 : 0;
            nint handle = new WindowInteropHelper(dialog).Handle;
            if (DwmSetWindowAttribute(handle, 20, ref darkTitle, sizeof(int)) != 0)
                _ = DwmSetWindowAttribute(handle, 19, ref darkTitle, sizeof(int));
        };
        return dialog;
    }

    private bool ValidName(string? name, int exclude = -1)
    {
        if (name is null) return false;
        if (name.Length > 0 && !settings.Profiles.Where((_, i) => i != exclude)
            .Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
        { shortcutFeedback = null; return true; }
        shortcutFeedback = "Enter a unique, non-empty profile name.";
        UpdateView();
        return false;
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        string? name = PromptName("New profile", "");
        if (!ValidName(name)) return;
        settings.Profiles.Add(new GammaProfile { Name = name!, Gamma = settings.Profiles[settings.SelectedProfile].Gamma });
        ProfileSelect.Items.Refresh();
        ProfileSelect.SelectedIndex = settings.Profiles.Count - 1;
        RegisterProfileHotkeys();
    }
    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        int index = settings.SelectedProfile;
        string? name = PromptName("Rename profile", settings.Profiles[index].Name);
        if (!ValidName(name, index)) return;
        settings.Profiles[index].Name = name!;
        ProfileSelect.Items.Refresh();
        RegisterProfileHotkeys();
        SaveSettings();
    }
    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (settings.Profiles.Count == 1) return;
        int index = settings.SelectedProfile;
        var dialog = Dialog("Delete profile", 390, 190);
        var panel = (StackPanel)dialog.Content;
        panel.Children.Add(new TextBlock { Text = $"Delete profile '{settings.Profiles[index].Name}'?", Margin = new Thickness(0, 0, 0, 18), Foreground = (Brush)Resources["Text"] });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        var yes = new Button { Content = "Delete", Width = 90, Style = (Style)Resources[typeof(Button)],
            Background = (Brush)Resources["Accent"], Foreground = (Brush)Resources["Background"], Tag = "Accent" };
        var no = new Button { Content = "Cancel", Width = 90, Margin = new Thickness(8, 0, 0, 0), IsCancel = true,
            Style = (Style)Resources[typeof(Button)] };
        yes.Click += (_, _) => dialog.DialogResult = true;
        buttons.Children.Add(yes); buttons.Children.Add(no); panel.Children.Add(buttons);
        if (dialog.ShowDialog() != true) return;
        loading = true;
        settings.Profiles.RemoveAt(index);
        settings.SelectedProfile = Math.Min(index, settings.Profiles.Count - 1);
        ProfileSelect.Items.Refresh();
        ProfileSelect.SelectedIndex = settings.SelectedProfile;
        loading = false;
        LoadProfile();
        SaveSettings();
        RegisterProfileHotkeys();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => ScheduleDisplayRefresh();
    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    { if (e.Mode == PowerModes.Resume) ScheduleDisplayRefresh(); }
    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (settings.Theme == "System" && !exiting) Dispatcher.BeginInvoke(ApplyTheme);
    }
    private void ScheduleDisplayRefresh()
    {
        if (exiting || Dispatcher.HasShutdownStarted) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(ScheduleDisplayRefresh); return; }
        monitorTimer.Stop();
        displayTimer.Stop();
        displayTimer.Start();
    }
    private void ShowWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }
    private void LogStatusChanges()
    {
        foreach (var display in controller.Displays)
            if (!loggedStatus.TryGetValue(display.DeviceName, out string? last) || last != display.Status)
            {
                loggedStatus[display.DeviceName] = display.Status;
                Log.Write($"{display.DeviceName}: {display.Status}");
            }
    }

    // Called from crash handlers, possibly off the UI thread. Never throws.
    internal void EmergencyRestore()
    {
        lock (restoreGate)
            try { controller.Restore(); }
            catch (Exception) { }
    }

    internal void CrashExit()
    {
        EmergencyRestore();
        exiting = true;
        try { Cleanup(); }
        catch (Exception) { }
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => ExitApp();
    private void ExitApp() { exiting = true; Close(); }
    private void Cleanup()
    {
        if (cleaned) return;
        cleaned = true;
        monitorTimer.Stop(); displayTimer.Stop();
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        controller.Restore();
        foreach (int id in registeredProfiles) NativeMethods.UnregisterHotKey(hwnd, id);
        registeredProfiles.Clear();
        tray.Visible = false; tray.Dispose();
        if (controller.HasFailedRestore)
            MessageBox.Show("A display ramp could not be restored. Review Windows display settings or restart if needed.",
                "Gamma Hotkey", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
