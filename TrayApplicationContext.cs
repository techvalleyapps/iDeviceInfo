using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using iDeviceInfo.Forms;

namespace iDeviceInfo
{
    /// <summary>
    /// Main application context. Manages the main window, system tray icon,
    /// device watcher, and the popup DeviceInfoForm.
    /// Supports multiple simultaneously-connected devices.
    /// </summary>
    public sealed class TrayApplicationContext : ApplicationContext
    {
        // ── Core objects ──────────────────────────────────────────────────

        private readonly MainForm           _mainForm;
        private readonly NotifyIcon         _tray;
        private readonly DeviceWatcher      _watcher;
        private readonly FloatingCopyButton _floatingBtn;

        // ── Per-device tracking ───────────────────────────────────────────

        private readonly Dictionary<string, DeviceInfo>        _devices   = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ToolStripMenuItem> _menuItems = new(StringComparer.OrdinalIgnoreCase);

        private string?         _lastSerial;
        private DeviceInfoForm? _popup;
        private string?         _popupSerial;

        // ── Menu structural items ─────────────────────────────────────────

        private readonly ToolStripSeparator _deviceSeparator;
        private readonly ToolStripMenuItem  _refreshItem;
        private readonly ToolStripMenuItem  _debugItem;

        // ─────────────────────────────────────────────────────────────────

        public TrayApplicationContext()
        {
            // ── Main window ────────────────────────────────────────────────
            // Shown on launch so the app is visible in the taskbar and can be
            // pinned.  Closing the window hides it to the tray rather than
            // exiting.
            _mainForm = new MainForm();
            _mainForm.Show();

            // ── Floating Copy All button ───────────────────────────────────
            _floatingBtn = new FloatingCopyButton();

            // ── Tray icon ──────────────────────────────────────────────────
            _tray = new NotifyIcon
            {
                Icon    = Forms.MainForm.LoadAppIcon() ?? BuildTrayIcon(connected: false),
                Text    = "iDeviceInfo — No device connected",
                Visible = true
            };

            // ── Context menu ──────────────────────────────────────────────
            var menu = new ContextMenuStrip();

            _deviceSeparator = new ToolStripSeparator();
            _refreshItem     = new ToolStripMenuItem("Refresh Devices");
            _debugItem       = new ToolStripMenuItem("Debug: Dump Device Info");
            var showItem     = new ToolStripMenuItem("Show Window");
            var exitItem     = new ToolStripMenuItem("Exit");

            _refreshItem.Click += (_, _) => _watcher.Restart();
            _debugItem.Click   += (_, _) => _watcher.DumpWithAmdState();
            showItem.Click += (_, _) => _mainForm.ShowFromTray();
            exitItem.Click += (_, _) => ExitApp();

            menu.Items.Add(_deviceSeparator);
            menu.Items.Add(_refreshItem);
            menu.Items.Add(_debugItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(showItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exitItem);

            _tray.ContextMenuStrip = menu;

            // Left-click tray: show main window if no device, else show device popup
            _tray.MouseClick += (_, e) =>
            {
                if (e.Button != MouseButtons.Left) return;

                if (_lastSerial != null)
                    ShowPopup(_lastSerial);
                else
                    _mainForm.ShowFromTray();
            };

            // ── Device watcher ─────────────────────────────────────────────
            _watcher = new DeviceWatcher();

            _watcher.DeviceConnected += (_, info) =>
            {
                string serial = info.SerialNumber;
                bool   isNew  = !_devices.ContainsKey(serial);

                _devices[serial] = info;
                _lastSerial      = serial;

                if (isNew)
                    AddDeviceMenuItem(serial, info.DeviceName);
                else
                    UpdateDeviceMenuItem(serial, info.DeviceName);

                _mainForm.UpdateStatus(_devices.Count, info.DeviceName);
                _floatingBtn.UpdateDevice(info);
                UpdateTrayState();

                if (isNew)
                {
                    ShowBalloon("Device Connected", info.DeviceName);
                    ShowPopup(serial);
                }
                else if (_popupSerial == serial)
                {
                    ShowPopup(serial);
                }
            };

            _watcher.DeviceDisconnected += (_, serial) =>
            {
                if (!_devices.TryGetValue(serial, out DeviceInfo? info)) return;

                _devices.Remove(serial);
                RemoveDeviceMenuItem(serial);

                if (_popupSerial == serial)
                {
                    _popup?.Close();
                    _popup       = null;
                    _popupSerial = null;
                }

                if (_lastSerial == serial)
                    _lastSerial = _devices.Count > 0 ? _devices.Keys.First() : null;

                _mainForm.UpdateStatus(_devices.Count,
                    _lastSerial != null ? _devices[_lastSerial].DeviceName : null);
                _floatingBtn.UpdateDevice(
                    _lastSerial != null ? _devices[_lastSerial] : null);

                UpdateTrayState();
                ShowBalloon("Device Disconnected", $"{info.DeviceName} disconnected.");
            };

            // ── Show-window signal (from a second launch attempt) ─────────────
            Program.ShowWindowRequested += () =>
                _mainForm.Invoke(() => _mainForm.ShowFromTray());

            // ── Delay Start() until AFTER Application.Run() starts the pump ──
            EventHandler? onIdle = null;
            onIdle = (_, _) =>
            {
                Application.Idle -= onIdle!;
                _watcher.Start();
            };
            Application.Idle += onIdle;
        }

        // ── Dynamic device menu items ─────────────────────────────────────

        private void AddDeviceMenuItem(string serial, string deviceName)
        {
            var item = new ToolStripMenuItem($"📱  {deviceName}")
            {
                Font = new Font("Segoe UI", 9f, FontStyle.Bold)
            };
            item.Click += (_, _) => ShowPopup(serial);
            _menuItems[serial] = item;

            int idx = _tray.ContextMenuStrip!.Items.IndexOf(_deviceSeparator);
            _tray.ContextMenuStrip.Items.Insert(idx, item);
        }

        private void UpdateDeviceMenuItem(string serial, string deviceName)
        {
            if (_menuItems.TryGetValue(serial, out var item))
                item.Text = $"📱  {deviceName}";
        }

        private void RemoveDeviceMenuItem(string serial)
        {
            if (!_menuItems.TryGetValue(serial, out var item)) return;
            _tray.ContextMenuStrip!.Items.Remove(item);
            item.Dispose();
            _menuItems.Remove(serial);
        }

        // ── Popup ─────────────────────────────────────────────────────────

        private void ShowPopup(string serial)
        {
            if (!_devices.TryGetValue(serial, out DeviceInfo? info)) return;

            if (_popup != null && !_popup.IsDisposed)
            {
                _popup.Close();
                _popup.Dispose();
            }

            _popup       = new DeviceInfoForm(info);
            _popupSerial = serial;
            _popup.FormClosed += (_, _) => { _popupSerial = null; };
            _popup.Show();
            _popup.Activate();
        }

        // ── Tray icon + tooltip ───────────────────────────────────────────

        private void UpdateTrayState()
        {
            bool connected = _devices.Count > 0;
            var  oldIcon   = _tray.Icon;
            _tray.Icon = BuildTrayIcon(connected);
            oldIcon?.Dispose();

            _tray.Text = _devices.Count switch
            {
                0 => "iDeviceInfo — No device connected",
                1 => $"iDeviceInfo — {_devices.Values.First().DeviceName}",
                _ => $"iDeviceInfo — {_devices.Count} devices connected"
            };
        }

        // ── Tray icon drawing ─────────────────────────────────────────────

        private static Icon BuildTrayIcon(bool connected)
        {
            const int size = 16;
            using var bmp = new Bitmap(size, size);
            using var g   = Graphics.FromImage(bmp);

            g.SmoothingMode     = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.Clear(Color.Transparent);

            var bodyColor = connected
                ? Color.FromArgb(10, 132, 255)
                : Color.FromArgb(130, 130, 130);

            using var bodyBrush = new SolidBrush(bodyColor);
            float x = 3, y = 1, w = 10, h = 14, r = 2.5f;
            using var path = RoundedRect(x, y, w, h, r);
            g.FillPath(bodyBrush, path);

            using var screenBrush = new SolidBrush(
                connected ? Color.FromArgb(200, 235, 255) : Color.FromArgb(80, 80, 80));
            g.FillRectangle(screenBrush, x + 1.5f, y + 2, w - 3, h - 5.5f);

            using var linePen = new Pen(bodyColor, 1.5f);
            g.DrawLine(linePen, x + 3.5f, y + h - 2, x + w - 3.5f, y + h - 2);

            if (connected)
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
            path.AddArc(x,             y,             r * 2, r * 2, 180, 90);
            path.AddArc(x + w - r * 2, y,             r * 2, r * 2, 270, 90);
            path.AddArc(x + w - r * 2, y + h - r * 2, r * 2, r * 2,   0, 90);
            path.AddArc(x,             y + h - r * 2, r * 2, r * 2,  90, 90);
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

        // ── Exit ─────────────────────────────────────────────────────────

        private void ExitApp()
        {
            try { _tray.Visible = false; } catch { }
            try { _watcher.Dispose(); }   catch { }
            try { _popup?.Dispose(); }    catch { }
            try { _mainForm.Dispose(); }  catch { }
            try { _floatingBtn.Dispose(); } catch { }
            try { _tray.Dispose(); }      catch { }
            Environment.Exit(0);          // hard kill — ensures no ghost process
        }

        // ── Cleanup ───────────────────────────────────────────────────────

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _watcher.Dispose();
                _popup?.Dispose();
                _mainForm.Dispose();
                _floatingBtn.Dispose();
                _tray.Visible = false;
                _tray.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
