using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using iDeviceInfo.Forms;

namespace iDeviceInfo
{
    /// <summary>
    /// Main application context. Manages the system tray icon, device watcher,
    /// and the popup DeviceInfoForm.
    /// </summary>
    public sealed class TrayApplicationContext : ApplicationContext
    {
        private readonly NotifyIcon          _tray;
        private readonly DeviceWatcher       _watcher;
        private readonly FloatingCopyButton  _floatingBtn;
        private DeviceInfo?                  _lastDevice;
        private DeviceInfoForm?              _popup;

        public TrayApplicationContext()
        {
            // ── Floating Copy All button (shows above taskbar when 3uTools runs)
            _floatingBtn = new FloatingCopyButton();

            // ── Tray icon ─────────────────────────────────────────────────
            _tray = new NotifyIcon
            {
                Icon    = BuildTrayIcon(hasDevice: false),
                Text    = "iDeviceInfo — No device connected",
                Visible = true
            };

            var menu = new ContextMenuStrip();
            var showItem = new ToolStripMenuItem("Show Device Info")
            {
                Enabled = false,
                Font    = new Font("Segoe UI", 9f, FontStyle.Bold)
            };
            var exitItem = new ToolStripMenuItem("Exit");

            showItem.Click += (_, _) => ShowPopup();
            exitItem.Click += (_, _) =>
            {
                _tray.Visible = false;
                Application.Exit();
            };

            var refreshItem = new ToolStripMenuItem("Refresh Device");
            refreshItem.Click += (s, e) => _watcher!.Restart();

            menu.Items.Add(showItem);
            menu.Items.Add(refreshItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exitItem);
            _tray.ContextMenuStrip = menu;

            // Left-click also shows popup
            _tray.MouseClick += (_, e) =>
            {
                if (e.Button == MouseButtons.Left)
                    ShowPopup();
            };

            // ── Device watcher ────────────────────────────────────────────
            _watcher = new DeviceWatcher();

            // Callbacks arrive on the UI thread (WinForms message pump)
            // so no InvokeOnUI marshalling needed
            _watcher.DeviceConnected += (_, info) =>
            {
                _lastDevice = info;
                _floatingBtn.UpdateDevice(info);
                UpdateTrayIcon(connected: true);
                showItem.Enabled = true;
                _tray.Text = $"iDeviceInfo — {info.DeviceName}";
                ShowBalloon("Device Connected", info.DeviceName);
                ShowPopup();
            };

            _watcher.DeviceDisconnected += (_, _) =>
            {
                _lastDevice = null;
                _floatingBtn.UpdateDevice(null);
                UpdateTrayIcon(connected: false);
                showItem.Enabled = false;
                _tray.Text = "iDeviceInfo — No device connected";
                _popup?.Close();
                _popup = null;
                ShowBalloon("Device Disconnected", "No iOS device connected.");
            };

            // Delay Start() until AFTER Application.Run() has started the message pump.
            // AMDeviceNotificationSubscribe needs the pump live to deliver callbacks.
            EventHandler? onIdle = null;
            onIdle = (s, e) =>
            {
                Application.Idle -= onIdle!; // one-shot
                _watcher.Start();
            };
            Application.Idle += onIdle;

            // Show a brief startup balloon
            _tray.BalloonTipTitle = "iDeviceInfo";
            _tray.BalloonTipText  = "Running in the system tray. Connect an iOS device to begin.";
            _tray.BalloonTipIcon  = ToolTipIcon.Info;
            _tray.ShowBalloonTip(3000);
        }

        // ── Popup ─────────────────────────────────────────────────────────

        private void ShowPopup()
        {
            if (_lastDevice == null) return;

            // Close any existing popup
            if (_popup != null && !_popup.IsDisposed)
            {
                _popup.Close();
                _popup.Dispose();
            }

            _popup = new DeviceInfoForm(_lastDevice);
            _popup.Show();
            _popup.Activate();
        }

        // ── Tray icon builder ─────────────────────────────────────────────

        private void UpdateTrayIcon(bool connected)
        {
            var oldIcon = _tray.Icon;
            _tray.Icon = BuildTrayIcon(connected);
            oldIcon?.Dispose();
        }

        private static Icon BuildTrayIcon(bool hasDevice)
        {
            const int size = 16;
            using var bmp = new Bitmap(size, size);
            using var g   = Graphics.FromImage(bmp);

            g.SmoothingMode     = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.Clear(Color.Transparent);

            // Phone body
            var bodyColor = hasDevice
                ? Color.FromArgb(10, 132, 255)  // iOS blue when connected
                : Color.FromArgb(130, 130, 130); // grey when idle

            using var bodyBrush  = new SolidBrush(bodyColor);

            // Rounded rectangle as phone body
            float x = 3, y = 1, w = 10, h = 14, r = 2.5f;
            using var path = RoundedRect(x, y, w, h, r);
            g.FillPath(bodyBrush, path);

            // Screen area (white rectangle inside)
            using var screenBrush = new SolidBrush(
                hasDevice ? Color.FromArgb(200, 235, 255) : Color.FromArgb(80, 80, 80));
            g.FillRectangle(screenBrush, x + 1.5f, y + 2, w - 3, h - 5.5f);

            // Home indicator line (bottom of screen)
            using var linePen = new Pen(bodyColor, 1.5f);
            g.DrawLine(linePen, x + 3.5f, y + h - 2, x + w - 3.5f, y + h - 2);

            // Green dot when connected
            if (hasDevice)
            {
                using var dotBrush = new SolidBrush(Color.FromArgb(52, 199, 89));
                g.FillEllipse(dotBrush, 10, 10, 6, 6);
            }

            IntPtr hIcon = bmp.GetHicon();
            return Icon.FromHandle(hIcon);
        }

        private static GraphicsPath RoundedRect(float x, float y, float w, float h, float r)
        {
            var path = new GraphicsPath();
            path.AddArc(x, y, r * 2, r * 2, 180, 90);
            path.AddArc(x + w - r * 2, y, r * 2, r * 2, 270, 90);
            path.AddArc(x + w - r * 2, y + h - r * 2, r * 2, r * 2, 0, 90);
            path.AddArc(x, y + h - r * 2, r * 2, r * 2, 90, 90);
            path.CloseFigure();
            return path;
        }

        // ── Helpers ───────────────────────────────────────────────────────

        private void ShowBalloon(string title, string message)
        {
            _tray.BalloonTipTitle = title;
            _tray.BalloonTipText  = message;
            _tray.BalloonTipIcon  = ToolTipIcon.Info;
            _tray.ShowBalloonTip(4000);
        }

        // ── Cleanup ───────────────────────────────────────────────────────

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _watcher.Dispose();
                _popup?.Dispose();
                _floatingBtn.Dispose();
                _tray.Visible = false;
                _tray.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
