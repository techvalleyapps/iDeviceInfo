using System;
using System.IO;
using System.Windows.Forms;

namespace iDeviceInfo
{
    internal static class Program
    {
        // ── Crash counter ─────────────────────────────────────────────────────
        // Stored in %AppData%\iDeviceInfo\crashes.txt — one line per crash.
        // CrashCount is read by DeviceWatcher.DumpWithAmdState for the debug dump.

        private static readonly string CrashFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "iDeviceInfo", "crashes.txt");

        public static int CrashCount { get; private set; }

        private static void RecordCrash(string reason)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CrashFile)!);
                File.AppendAllText(CrashFile,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {reason}{Environment.NewLine}");
                CrashCount++;
            }
            catch { /* never crash inside the crash handler */ }
        }

        private static void LoadCrashCount()
        {
            try
            {
                if (File.Exists(CrashFile))
                    CrashCount = File.ReadAllLines(CrashFile).Length;
            }
            catch { }
        }

        // ── Entry point ───────────────────────────────────────────────────────

        [STAThread]
        static void Main()
        {
            // ── Single-instance guard ────────────────────────────────────────
            using var mutex = new System.Threading.Mutex(
                true, "iDeviceInfo_SingleInstance", out bool isNew);

            if (!isNew)
            {
                MessageBox.Show(
                    "iDeviceInfo is already running in the system tray.",
                    "iDeviceInfo",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            // Load previous crash count before anything else
            LoadCrashCount();

            // ── Global exception handlers ────────────────────────────────────
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

            Application.ThreadException += (_, e) =>
            {
                RecordCrash($"UI thread: {e.Exception.GetType().Name}: {e.Exception.Message}");
                MessageBox.Show(
                    $"Unexpected error:\n\n{e.Exception.Message}",
                    "iDeviceInfo Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            };

            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                if (e.ExceptionObject is Exception ex)
                {
                    RecordCrash($"Background thread: {ex.GetType().Name}: {ex.Message}");
                    MessageBox.Show(
                        $"Fatal error:\n\n{ex.Message}",
                        "iDeviceInfo",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
            };

            // ── Launch ───────────────────────────────────────────────────────
            ApplicationConfiguration.Initialize();
            Application.Run(new TrayApplicationContext());
        }
    }
}
