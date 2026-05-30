using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace iDeviceInfo.Forms
{
    /// <summary>
    /// Unified main window — replaces both MainForm and DeviceInfoForm.
    ///
    /// Behaviour:
    ///   ─  Minimize  → standard Windows minimize to taskbar
    ///   ✕  Close     → hide to system tray (app keeps running)
    ///   TopMost      → stays above Windows toast notifications
    ///
    /// Starts in "waiting" state; UpdateDevice() fills in the rows when a device
    /// connects; ClearDevice() resets them when it disconnects.
    /// </summary>
    public sealed class MainForm : Form
    {
        // ── Layout constants ──────────────────────────────────────────────
        private const int PX          = 18;   // horizontal padding
        private const int PY          = 12;   // vertical padding
        private const int LabelW      = 130;
        private const int ValueW      = 230;
        private const int CopyW       = 54;
        private const int RowH        = 26;
        private const int RowGap      = 5;
        private const int HeaderH     = 50;
        private const int FooterH     = 52;

        // 9 fixed rows — IMEI 2 shows "—" when not applicable
        private static readonly string[] RowKeys =
        {
            "Device Name", "Model", "iOS Build", "Serial Number",
            "IMEI", "IMEI 2", "Battery Level", "Battery Health", "UDID"
        };

        private static readonly int FormW =
            PX * 2 + LabelW + 8 + ValueW + 8 + CopyW;          // 466

        private static readonly int FormH =
            HeaderH + PY
            + RowKeys.Length * (RowH + RowGap) - RowGap
            + PY + 2 + 14 + FooterH;                            // ≈ 413

        // ── Colors ────────────────────────────────────────────────────────
        private static readonly Color BgColor      = Color.FromArgb(30,  30,  30);
        private static readonly Color HeaderColor   = Color.FromArgb(20,  20,  20);
        private static readonly Color AccentColor   = Color.FromArgb(10,  132, 255);
        private static readonly Color LabelColor    = Color.FromArgb(160, 160, 160);
        private static readonly Color ValueColor    = Color.White;
        private static readonly Color CopyBtnColor  = Color.FromArgb(55,  55,  55);
        private static readonly Color CopyBtnHover  = Color.FromArgb(75,  75,  75);
        private static readonly Color BorderColor   = Color.FromArgb(60,  60,  60);
        private static readonly Color DisabledColor = Color.FromArgb(70,  70,  70);

        // ── Fonts ─────────────────────────────────────────────────────────
        private readonly Font _titleFont = new("Segoe UI Semibold", 11f);
        private readonly Font _subFont   = new("Segoe UI",          8.5f);
        private readonly Font _labelFont = new("Segoe UI",          9f);
        private readonly Font _valueFont = new("Segoe UI",          9f, FontStyle.Bold);
        private readonly Font _btnFont   = new("Segoe UI",          8f);

        // ── Live controls ─────────────────────────────────────────────────
        private readonly Label  _headerTitle;   // device name (or "iDeviceInfo")
        private readonly Label  _headerSub;     // model      (or "No device connected")
        private readonly Label[]  _valueLabels = new Label[RowKeys.Length];
        private readonly Button[] _copyButtons  = new Button[RowKeys.Length];
        private readonly Button   _copyAllBtn;

        // ── Drag ──────────────────────────────────────────────────────────
        private Point _dragStart;
        private bool  _dragging;

        // ── Current device ────────────────────────────────────────────────
        private DeviceInfo? _device;

        // ── WinAPI for minimize-to-taskbar on borderless form ─────────────
        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        private const int GWL_STYLE    = -16;
        private const int WS_MINIMIZEBOX = 0x00020000;
        private const int WS_SYSMENU    = 0x00080000;

        // ─────────────────────────────────────────────────────────────────

        public MainForm()
        {
            // ── Form settings ─────────────────────────────────────────────
            Text            = "iDeviceInfo";
            FormBorderStyle = FormBorderStyle.None;
            Icon            = LoadAppIcon();
            BackColor       = BgColor;
            Size            = new Size(FormW, FormH);
            StartPosition   = FormStartPosition.Manual;
            ShowInTaskbar   = true;
            TopMost         = false;
            KeyPreview      = true;

            // Position bottom-right near tray on first launch
            var wa = Screen.PrimaryScreen!.WorkingArea;
            Location = new Point(wa.Right - FormW - 12, wa.Bottom - FormH - 12);

            KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Escape) HideToTray();
            };

            // ── Border frame ──────────────────────────────────────────────
            var border = new Panel
            {
                Location  = Point.Empty,
                Size      = new Size(FormW, FormH),
                BackColor = BorderColor
            };
            Controls.Add(border);

            var inner = new Panel
            {
                Location  = new Point(1, 1),
                Size      = new Size(FormW - 2, FormH - 2),
                BackColor = BgColor
            };
            border.Controls.Add(inner);

            // ── Header ────────────────────────────────────────────────────
            var header = new Panel
            {
                Location  = Point.Empty,
                Size      = new Size(FormW - 2, HeaderH),
                BackColor = HeaderColor
            };
            inner.Controls.Add(header);

            var phoneIcon = new Label
            {
                Text      = "",
                Font      = new Font("Segoe UI Emoji", 16f),
                ForeColor = AccentColor,
                Location  = new Point(PX, 8),
                Size      = new Size(34, 34),
                TextAlign = ContentAlignment.MiddleCenter
            };
            header.Controls.Add(phoneIcon);

            _headerTitle = new Label
            {
                Text         = "iDeviceInfo",
                Font         = _titleFont,
                ForeColor    = ValueColor,
                Location     = new Point(PX + 38, 7),
                Size         = new Size(FormW - PX * 2 - 38 - 56, 22),
                TextAlign    = ContentAlignment.MiddleLeft,
                AutoEllipsis = true
            };
            header.Controls.Add(_headerTitle);

            _headerSub = new Label
            {
                Text      = "No device connected",
                Font      = _subFont,
                ForeColor = LabelColor,
                Location  = new Point(PX + 38, 28),
                Size      = new Size(FormW - PX * 2 - 38 - 56, 16),
                TextAlign = ContentAlignment.MiddleLeft
            };
            header.Controls.Add(_headerSub);

            // Minimize button (─) → standard Windows minimize to taskbar
            var minBtn = MakeHeaderBtn("─", new Point(FormW - 2 - 54, 4));
            minBtn.Click += (_, _) => WindowState = FormWindowState.Minimized;
            header.Controls.Add(minBtn);

            // Close button (✕) → hide to tray
            var closeBtn = MakeHeaderBtn("✕", new Point(FormW - 2 - 28, 4));
            closeBtn.Click += (_, _) => HideToTray();
            header.Controls.Add(closeBtn);

            // ── Rows ──────────────────────────────────────────────────────
            int y = HeaderH + PY;
            for (int i = 0; i < RowKeys.Length; i++)
            {
                int xi = PX;

                var lbl = new Label
                {
                    Text      = RowKeys[i] + ":",
                    Font      = _labelFont,
                    ForeColor = LabelColor,
                    Location  = new Point(xi, y + 5),
                    Size      = new Size(LabelW, RowH),
                    TextAlign = ContentAlignment.TopLeft
                };
                inner.Controls.Add(lbl);
                xi += LabelW + 8;

                var val = new Label
                {
                    Text         = "—",
                    Font         = _valueFont,
                    ForeColor    = DisabledColor,
                    Location     = new Point(xi, y + 5),
                    Size         = new Size(ValueW, RowH),
                    TextAlign    = ContentAlignment.TopLeft,
                    AutoEllipsis = true
                };
                inner.Controls.Add(val);
                _valueLabels[i] = val;
                xi += ValueW + 8;

                var copy = MakeButton("Copy", new Point(xi, y + 2), new Size(CopyW, 22));
                copy.Enabled = false;
                copy.Click += (_, _) =>
                {
                    try { Clipboard.SetText(val.Text); }
                    catch { }
                };
                inner.Controls.Add(copy);
                _copyButtons[i] = copy;

                y += RowH + RowGap;
            }

            // ── Separator ─────────────────────────────────────────────────
            y += 2;
            inner.Controls.Add(new Panel
            {
                Location  = new Point(PX, y),
                Size      = new Size(FormW - 2 - PX * 2, 1),
                BackColor = BorderColor
            });
            y += 14;

            // ── Footer ────────────────────────────────────────────────────
            // Minimize-to-tray label (left)
            var minToTrayBtn = MakeButton("Hide to tray", new Point(PX, y), new Size(120, 34));
            minToTrayBtn.Click += (_, _) => HideToTray();
            inner.Controls.Add(minToTrayBtn);

            // Copy All (right) — blue, disabled until device connected
            _copyAllBtn = MakeButton("Copy All", new Point(FormW - 2 - PX - (LabelW + 8 + ValueW)/2, y),
                new Size((LabelW + 8 + ValueW) / 2, 34));
            _copyAllBtn.Font                           = new Font("Segoe UI Semibold", 9f);
            _copyAllBtn.BackColor                      = AccentColor;
            _copyAllBtn.ForeColor                      = Color.White;
            _copyAllBtn.FlatAppearance.BorderColor     = AccentColor;
            _copyAllBtn.Enabled                        = false;
            _copyAllBtn.Click += (_, _) =>
            {
                if (_device == null) return;
                try { Clipboard.SetText(_device.ToClipboardText()); }
                catch { }
            };
            inner.Controls.Add(_copyAllBtn);

            // ── Drag ──────────────────────────────────────────────────────
            MakeDraggable(inner);
            MakeDraggable(header);

            // ── Close → tray ──────────────────────────────────────────────
            FormClosing += (_, e) =>
            {
                e.Cancel = true;
                HideToTray();
            };
        }

        // ── Minimise-to-taskbar on borderless form ────────────────────────

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // Add WS_MINIMIZEBOX + WS_SYSMENU so Windows allows minimize on a
            // borderless form and the taskbar button responds correctly.
            int style = GetWindowLong(Handle, GWL_STYLE);
            SetWindowLong(Handle, GWL_STYLE, style | WS_MINIMIZEBOX | WS_SYSMENU);
        }

        // ── Public API ────────────────────────────────────────────────────

        /// <summary>Called when a device connects — fills all rows with real data.</summary>
        public void UpdateDevice(DeviceInfo info)
        {
            if (InvokeRequired) { Invoke(() => UpdateDevice(info)); return; }

            _device = info;

            string batteryVal = info.BatteryLevel + (info.IsCharging ? "  (Charging)" : "");

            var values = new[]
            {
                info.DeviceName,
                info.FullModelName,
                info.iOSVersion,
                info.SerialNumber,
                info.IMEI,
                string.IsNullOrEmpty(info.IMEI2) || info.IMEI2 == "N/A" ? "—" : info.IMEI2,
                batteryVal,
                info.BatteryHealth,
                info.UDID
            };

            // Special copy value for Battery Level (strip charging emoji)
            var copyValues = new[]
            {
                info.DeviceName, info.FullModelName, info.iOSVersion,
                info.SerialNumber, info.IMEI,
                string.IsNullOrEmpty(info.IMEI2) || info.IMEI2 == "N/A" ? "—" : info.IMEI2,
                info.BatteryLevel + (info.IsCharging ? " (Charging)" : ""),
                info.BatteryHealth, info.UDID
            };

            for (int i = 0; i < RowKeys.Length; i++)
            {
                _valueLabels[i].Text      = values[i];
                _valueLabels[i].ForeColor = ValueColor;

                string cv = copyValues[i];
                _copyButtons[i].Enabled = cv != "—";
                _copyButtons[i].Tag     = cv;
                _copyButtons[i].Click  -= CopyTag; // remove old handler
                _copyButtons[i].Click  += CopyTag;
            }

            _headerTitle.Text = info.DeviceName;
            _headerSub.Text   = info.FullModelName;
            _copyAllBtn.Enabled = true;
        }

        /// <summary>Called when the device disconnects — resets all rows.</summary>
        public void ClearDevice()
        {
            if (InvokeRequired) { Invoke(ClearDevice); return; }

            _device = null;

            for (int i = 0; i < RowKeys.Length; i++)
            {
                _valueLabels[i].Text      = "—";
                _valueLabels[i].ForeColor = DisabledColor;
                _copyButtons[i].Enabled   = false;
            }

            _headerTitle.Text   = "iDeviceInfo";
            _headerSub.Text     = "No device connected";
            _copyAllBtn.Enabled = false;
        }

        public void ShowFromTray()
        {
            if (InvokeRequired) { Invoke(ShowFromTray); return; }
            ShowInTaskbar = true;
            Show();
            if (WindowState == FormWindowState.Minimized)
                WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
        }

        // ── Helpers ───────────────────────────────────────────────────────

        private void HideToTray()
        {
            Hide();
            ShowInTaskbar = false;
        }

        private static void CopyTag(object? sender, EventArgs e)
        {
            if (sender is Button btn)
                try { Clipboard.SetText(btn.Tag?.ToString() ?? ""); } catch { }
        }

        private Button MakeHeaderBtn(string text, Point location)
        {
            var btn = new Button
            {
                Text      = text,
                Font      = new Font("Segoe UI", 8f, FontStyle.Bold),
                ForeColor = LabelColor,
                BackColor = Color.Transparent,
                Location  = location,
                Size      = new Size(24, 24),
                FlatStyle = FlatStyle.Flat,
                Cursor    = Cursors.Hand
            };
            btn.FlatAppearance.BorderSize = 0;
            btn.MouseEnter += (_, _) => btn.ForeColor = Color.White;
            btn.MouseLeave += (_, _) => btn.ForeColor = LabelColor;
            return btn;
        }

        private Button MakeButton(string text, Point location, Size size)
        {
            var btn = new Button
            {
                Text      = text,
                Font      = _btnFont,
                ForeColor = Color.White,
                BackColor = CopyBtnColor,
                Location  = location,
                Size      = size,
                FlatStyle = FlatStyle.Flat,
                Cursor    = Cursors.Hand,
                UseVisualStyleBackColor = false
            };
            btn.FlatAppearance.BorderColor = CopyBtnColor;
            btn.FlatAppearance.BorderSize  = 1;
            btn.MouseEnter += (_, _) =>
            {
                if (!btn.Enabled) return;
                btn.BackColor = CopyBtnHover;
                btn.FlatAppearance.BorderColor = CopyBtnHover;
            };
            btn.MouseLeave += (_, _) =>
            {
                btn.BackColor = CopyBtnColor;
                btn.FlatAppearance.BorderColor = CopyBtnColor;
            };
            return btn;
        }

        private void MakeDraggable(Control ctrl)
        {
            ctrl.MouseDown += (_, e) =>
            {
                if (e.Button == MouseButtons.Left) { _dragging = true; _dragStart = e.Location; }
            };
            ctrl.MouseMove += (_, e) =>
            {
                if (!_dragging) return;
                var c = Cursor.Position;
                Location = new Point(c.X - _dragStart.X, c.Y - _dragStart.Y);
            };
            ctrl.MouseUp += (_, _) => _dragging = false;
        }

        internal static Icon? LoadAppIcon()
        {
            try
            {
                string path = Path.Combine(AppContext.BaseDirectory, "Resources", "iDeviceInfo.ico");
                if (File.Exists(path)) return new Icon(path);
            }
            catch { }
            return null;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _titleFont.Dispose(); _subFont.Dispose();
                _labelFont.Dispose(); _valueFont.Dispose(); _btnFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
