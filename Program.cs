using System;
using System.Threading;
using System.Windows.Forms;

namespace iDeviceInfo
{
    internal static class Program
    {
        // Named handles shared across processes for single-instance coordination
        private const string MutexName     = "iDeviceInfo_SingleInstance";
        private const string ShowEventName = "iDeviceInfo_ShowWindow";

        // Expose so TrayApplicationContext can raise it
        internal static event Action? ShowWindowRequested;

        [STAThread]
        static void Main()
        {
            // ── Single-instance guard ────────────────────────────────────────
            using var mutex = new Mutex(true, MutexName, out bool isNew);

            if (!isNew)
            {
                // Another instance is running — signal it to show its window
                try
                {
                    using var evt = EventWaitHandle.OpenExisting(ShowEventName);
                    evt.Set();
                }
                catch { }
                return;
            }

            // ── Listen for show-window signals from future instances ──────────
            using var showEvent = new EventWaitHandle(
                false, EventResetMode.AutoReset, ShowEventName);

            new Thread(() =>
            {
                while (true)
                {
                    showEvent.WaitOne();
                    ShowWindowRequested?.Invoke();
                }
            }) { IsBackground = true, Name = "iDeviceInfo-ShowSignal" }.Start();

            // ── Global exception handlers ────────────────────────────────────
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

            Application.ThreadException += (_, e) =>
            {
                MessageBox.Show(
                    $"Unexpected error:\n\n{e.Exception.Message}",
                    "iDeviceInfo Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            };

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
