using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace iDeviceInfo
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            // ── Add Apple Mobile Device Support to the DLL search path ──────
            // This ensures MobileDevice.dll and CoreFoundation.dll are found
            // without needing them in the same folder as our exe.
            const string appleMDPath =
                @"C:\Program Files\Common Files\Apple\Mobile Device Support";

            if (Directory.Exists(appleMDPath))
                SetDllDirectory(appleMDPath);

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

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetDllDirectory(string lpPathName);
    }
}
