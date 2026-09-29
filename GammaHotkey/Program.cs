using WF = System.Windows.Forms;

namespace GammaHotkey;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        string mutexName = $@"Local\GammaHotkey-{System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value}";
        using var mutex = new Mutex(true, mutexName, out bool created);
        if (!created)
        {
            System.Windows.MessageBox.Show("Gamma Hotkey is already running in this session.", "Gamma Hotkey");
            return;
        }
        WF.Application.EnableVisualStyles();
        var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnMainWindowClose };
        var window = new MainWindow();
        app.MainWindow = window;
        if (!window.StartHidden) window.Show();
        app.Run();
    }
}
