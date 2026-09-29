using WF = System.Windows.Forms;

namespace GammaHotkey;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        string mutexName = $@"Local\GammaHotkey-{System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value}";
        using var mutex = new Mutex(true, mutexName, out bool created);
        if (!created)
        {
            System.Windows.MessageBox.Show("Gamma Hotkey is already running in this session.", "Gamma Hotkey");
            return 0;
        }
        WF.Application.EnableVisualStyles();
        var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnMainWindowClose };
        MainWindow? window = null;
        bool crashed = false;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Write("Unhandled exception: " + e.ExceptionObject);
            window?.EmergencyRestore();
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Write("Unobserved task exception: " + e.Exception);
            e.SetObserved();
        };
        app.DispatcherUnhandledException += (_, e) =>
        {
            e.Handled = true;
            if (crashed) return;
            crashed = true;
            Log.Write("Unhandled UI exception: " + e.Exception);
            window?.CrashExit();
            System.Windows.MessageBox.Show("Gamma Hotkey hit an unexpected error and closed. Display gamma was restored where possible." +
                Environment.NewLine + Environment.NewLine + "Details: " + Log.FilePath, "Gamma Hotkey",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            app.Shutdown(1);
        };
        app.SessionEnding += (_, _) => window?.EmergencyRestore();
        Log.Write($"Started version {typeof(Program).Assembly.GetName().Version!.ToString(3)}");
        window = new MainWindow();
        app.MainWindow = window;
        if (!window.StartHidden) window.Show();
        return app.Run();
    }
}
