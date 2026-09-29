using System.IO;

namespace GammaHotkey;

// Daily log in %LOCALAPPDATA%\GammaHotkey\logs, keeps the newest 5 files. Never throws.
internal static class Log
{
    private const int KeepFiles = 5;
    private static readonly object Gate = new();
    public static string Folder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GammaHotkey", "logs");
    public static string FilePath => Path.Combine(Folder, $"gamma-hotkey-{DateTime.Now:yyyyMMdd}.log");

    public static void Write(string message)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                string path = FilePath;
                bool created = !File.Exists(path);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
                if (created)
                    foreach (string old in Directory.GetFiles(Folder, "gamma-hotkey-*.log")
                        .OrderDescending(StringComparer.Ordinal).Skip(KeepFiles))
                        File.Delete(old);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
