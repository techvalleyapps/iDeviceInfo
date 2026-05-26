using System;
using System.Windows.Forms;

namespace iDeviceInfo
{
    internal static class Program
    {
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

            // ── Global exception handler ─────────────────────────────────────
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, e) =>
                MessageBox.Show(
                    $"Unexpected error:\n\n{e.Exception.Message}",
                    "iDeviceInfo Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);

            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                if (e.ExceptionObject is Exception ex)
                    MessageBox.Show(
                        $"Fatal error:\n\n{ex.Message}",
                        "iDeviceInfo",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
            };

            // ── Launch ───────────────────────────────────────────────────────
            ApplicationConfiguration.Initialize();
            Application.Run(new TrayApplicationContext());
        }
    }
}
