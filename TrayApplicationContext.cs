using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using iDeviceInfo.Forms;

namespace iDeviceInfo
{
    public sealed class TrayApplicationContext : ApplicationContext
    {
        // ── Core objects ──────────────────────────────────────────────────
        private readonly MainForm      _window;
        private readonly NotifyIcon    _tray;
        private readonly DeviceWatcher _watcher;
        private readonly FloatingCopyButton _floatingBtn;

        // ── Per-device tracking ───────────────────────────────────────────
        private readonly Dictionary<string, DeviceInfo>        _devices   = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ToolStripMenuItem> _menuItems = new(StringComparer.OrdinalIgnoreCase);
        private string? _lastSerial;

        // ── Menu structural items ─────────────────────────────────────────
        private readonly ToolStripSeparator _deviceSeparator;

        // ─────────────────────────────────────────────────────────────────

        public TrayApplicationContext()
        {
            // ── Unified window ─────────────────────────────────────────────
            _window = new MainForm();
            _window.Show();

            // ── Floating Copy All (stays above notifications) ──────────────
            _floatingBtn = new FloatingCopyButton();

            // ── Tray icon ──────────────────────────────────────────────────
            _tray = new NotifyIcon
            {
                Icon    = Forms.MainForm.LoadAppIcon() ?? BuildTrayIcon(false),
                Text    = "iDeviceInfo — No device connected",
                Visible = true
            };

            // ── Context menu ──────────────────────────────────────────────
            var menu             = new ContextMenuStrip();
            _deviceSeparator     = new ToolStripSeparator();
            var showItem         = new ToolStripMenuItem("Show Window");
            var refreshItem      = new ToolStripMenuItem("Refresh Devices");
            var debugItem        = new ToolStripMenuItem("Debug: Dump Device Info");
            var exitItem         = new ToolStripMenuItem("Exit");

            showItem.Click    += (_, _) => _window.ShowFromTray();
            refreshItem.Click += (_, _) => _watcher.Restart();
            debugItem.Click   += (_, _) => _watcher.DumpWithAmdState();
            exitItem.Click    += (_, _) => ExitApp();

            menu.Items.Add(_deviceSeparator);
            menu.Items.Add(showItem);
            menu.Items.Add(refreshItem);
            menu.Items.Add(debugItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exitItem);

            _tray.ContextMenuStrip = menu;

            // Left-click tray → show window
            _tray.MouseClick += (_, e) =>
            {
                if (e.Button == MouseButtons.Left)
                    _window.ShowFromTray();
            };

            // ── Device watcher ─────────────────────────────────────────────
            _watcher = new DeviceWatcher();

            _watcher.DeviceConnected += (_, info) =>
            {
                string serial = info.SerialNumber;
                bool   isNew  = !_devices.ContainsKey(serial);

                _devices[serial] = info;
                _lastSerial      = serial;

                if (isNew) AddDeviceMenuItem(serial, info.DeviceName);
                else       UpdateDeviceMenuItem(serial, info.DeviceName);

                UpdateTrayState();
                _floatingBtn.UpdateDevice(info);

                // Add/update device in the unified window (side panel + content)
                _window.AddDevice(info);

                if (isNew)
                {
                    ShowBalloon("Device Connected", info.DeviceName);
                    _window.ShowFromTray();
                }
            };

            _watcher.DeviceDisconnected += (_, serial) =>
            {
                if (!_devices.TryGetValue(serial, out DeviceInfo? info)) return;

                _devices.Remove(serial);
                RemoveDeviceMenuItem(serial);

                if (_lastSerial == serial)
                    _lastSerial = _devices.Count > 0 ? _devices.Keys.First() : null;

                // Remove device from unified window (it selects next or clears itself)
                _window.RemoveDevice(serial);

                // Keep floating button in sync
                if (_lastSerial != null)
                    _floatingBtn.UpdateDevice(_devices[_lastSerial]);
                else
                    _floatingBtn.UpdateDevice(null);

                UpdateTrayState();
                ShowBalloon("Device Disconnected", $"{info.DeviceName} disconnected.");
            };

            // ── Show-window signal (second launch) ────────────────────────
            Program.ShowWindowRequested += () =>
                _window.Invoke(() => _window.ShowFromTray());

            // ── Delay Start() until message pump is live ──────────────────
            EventHandler? onIdle = null;
            onIdle = (_, _) =>
            {
                Application.Idle -= onIdle!;
                _watcher.Start();
            };
            Application.Idle += onIdle;
        }

        // ── Device menu items ─────────────────────────────────────────────

        private void AddDeviceMenuItem(string serial, string name)
        {
            var item = new ToolStripMenuItem($"📱  {name}")
            {
                Font = new Font("Segoe UI", 9f, FontStyle.Bold)
            };
            item.Click += (_, _) =>
            {
                if (_devices.TryGetValue(serial, out var info))
                    _window.UpdateDevice(info);
                _window.ShowFromTray();
            };
            _menuItems[serial] = item;

            int idx = _tray.ContextMenuStrip!.Items.IndexOf(_deviceSeparator);
            _tray.ContextMenuStrip.Items.Insert(idx, item);
        }

        private void UpdateDeviceMenuItem(string serial, string name)
        {
            if (_menuItems.TryGetValue(serial, out var item))
                item.Text = $"📱  {name}";
        }

        private void RemoveDeviceMenuItem(string serial)
        {
            if (!_menuItems.TryGetValue(serial, out var item)) return;
            _tray.ContextMenuStrip!.Items.Remove(item);
            item.Dispose();
            _menuItems.Remove(serial);
        }

        // ── Tray state ────────────────────────────────────────────────────

        private void UpdateTrayState()
        {
            bool connected = _devices.Count > 0;
            var  old       = _tray.Icon;
            _tray.Icon = connected
                ? BuildTrayIcon(true)
                : (Forms.MainForm.LoadAppIcon() ?? BuildTrayIcon(false));
            old?.Dispose();

            _tray.Text = _devices.Count switch
            {
                0 => "iDeviceInfo — No device connected",
                1 => $"iDeviceInfo — {_devices.Values.First().DeviceName}",
                _ => $"iDeviceInfo — {_devices.Count} devices connected"
            };
        }

        private static Icon BuildTrayIcon(bool connected)
        {
            const int sz = 16;
            using var bmp = new Bitmap(sz, sz);
            using var g   = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            var body = connected ? Color.FromArgb(10, 132, 255) : Color.FromArgb(130, 130, 130);
            using var bodyBrush = new SolidBrush(body);
            float x = 3, y = 1, w = 10, h = 14, r = 2.5f;
            using var path = RoundedRect(x, y, w, h, r);
            g.FillPath(bodyBrush, path);
            using var scrBrush = new SolidBrush(connected ? Color.FromArgb(200,235,255) : Color.FromArgb(80,80,80));
            g.FillRectangle(scrBrush, x+1.5f, y+2, w-3, h-5.5f);
            using var lp = new Pen(body, 1.5f);
            g.DrawLine(lp, x+3.5f, y+h-2, x+w-3.5f, y+h-2);
            if (connected)
            {
                using var dot = new SolidBrush(Color.FromArgb(52,199,89));
                g.FillEllipse(dot, 10, 10, 6, 6);
            }
            return Icon.FromHandle(bmp.GetHicon());
        }

        private static GraphicsPath RoundedRect(float x, float y, float w, float h, float r)
        {
            var p = new GraphicsPath();
            p.AddArc(x, y, r*2, r*2, 180, 90);
            p.AddArc(x+w-r*2, y, r*2, r*2, 270, 90);
            p.AddArc(x+w-r*2, y+h-r*2, r*2, r*2, 0, 90);
            p.AddArc(x, y+h-r*2, r*2, r*2, 90, 90);
            p.CloseFigure();
            return p;
        }

        // ── Helpers ───────────────────────────────────────────────────────

        private void ShowBalloon(string title, string msg)
        {
            _tray.BalloonTipTitle = title;
            _tray.BalloonTipText  = msg;
            _tray.BalloonTipIcon  = ToolTipIcon.Info;
            _tray.ShowBalloonTip(4000);
        }

        private void ExitApp()
        {
            try { _tray.Visible = false; }   catch { }
            try { _watcher.Dispose(); }       catch { }
            try { _window.Dispose(); }        catch { }
            try { _floatingBtn.Dispose(); }   catch { }
            try { _tray.Dispose(); }          catch { }
            Environment.Exit(0);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _watcher.Dispose();
                _window.Dispose();
                _floatingBtn.Dispose();
                _tray.Visible = false;
                _tray.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
