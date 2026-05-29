using System;
using System.Drawing;
using System.Windows.Forms;

namespace iDeviceInfo.Forms
{
    /// <summary>
    /// A compact, borderless popup that shows all device fields with per-row
    /// Copy buttons and a "Copy All" button at the bottom.
    /// </summary>
    public sealed class DeviceInfoForm : Form
    {
        private readonly DeviceInfo _info;

        // Design constants
        private const int PaddingX   = 18;
        private const int PaddingY   = 14;
        private const int LabelWidth = 130;
        private const int ValueWidth = 230;
        private const int CopyWidth  = 54;
        private const int RowHeight  = 30;
        private const int RowGap     = 6;
        private const int HeaderH    = 48;
        private const int FooterH    = 52;

        // Colors
        private static readonly Color BgColor     = Color.FromArgb(30,  30,  30);
        private static readonly Color HeaderColor  = Color.FromArgb(20,  20,  20);
        private static readonly Color LabelColor   = Color.FromArgb(160, 160, 160);
        private static readonly Color ValueColor   = Color.White;
        private static readonly Color CopyBtnColor = Color.FromArgb(55,  55,  55);
        private static readonly Color CopyBtnHover = Color.FromArgb(75,  75,  75);
        private static readonly Color AccentColor  = Color.FromArgb(10,  132, 255); // iOS blue
        private static readonly Color BorderColor  = Color.FromArgb(60,  60,  60);

        // Instance fonts — created per-popup so Dispose() can safely clean them up
        private readonly Font TitleFont  = new("Segoe UI Semibold", 11f, FontStyle.Regular);
        private readonly Font SubFont    = new("Segoe UI",          8.5f, FontStyle.Regular);
        private readonly Font LabelFont  = new("Segoe UI",          9f,  FontStyle.Regular);
        private readonly Font ValueFont  = new("Segoe UI",          9f,  FontStyle.Bold);
        private readonly Font BtnFont    = new("Segoe UI",          8f,  FontStyle.Regular);

        public DeviceInfoForm(DeviceInfo info)
        {
            _info = info;
            BuildUI();
        }

        private void BuildUI()
        {
            // Rows to display  (label, value, allowCopy)
            var rows = new (string Label, string Value)[]
            {
                ("Device Name",    _info.DeviceName),
                ("Model",          string.IsNullOrEmpty(_info.ModelName) ? _info.ProductType : _info.ModelName),
                ("iOS Build",      _info.iOSVersion),
                ("Serial Number",  _info.SerialNumber),
                ("IMEI",           _info.IMEI),
                ("Battery Level",  _info.BatteryLevel + (_info.IsCharging ? "  (Charging)" : "")),
                ("Battery Health", _info.BatteryHealth),
                ("Crash Reports",  _info.CrashReportCount < 0 ? "N/A" : _info.CrashReportCount.ToString()),
            };

            // Add IMEI2 row only when present
            if (!string.IsNullOrWhiteSpace(_info.IMEI2) && _info.IMEI2 != "N/A")
            {
                var list = new System.Collections.Generic.List<(string, string)>(rows);
                list.Insert(5, ("IMEI 2", _info.IMEI2));
                rows = list.ToArray();
            }

            int totalRows  = rows.Length;
            int formWidth  = PaddingX * 2 + LabelWidth + 8 + ValueWidth + 8 + CopyWidth;
            int formHeight = HeaderH + PaddingY
                           + totalRows * (RowHeight + RowGap) - RowGap
                           + PaddingY + FooterH;

            // ── Form settings ────────────────────────────────────────────
            Text            = "iDeviceInfo";
            FormBorderStyle = FormBorderStyle.None;
            BackColor       = BgColor;
            Size            = new Size(formWidth, formHeight);
            StartPosition   = FormStartPosition.Manual;
            TopMost         = true;
            ShowInTaskbar   = false;

            // Position near bottom-right (tray area)
            var screen = Screen.PrimaryScreen!.WorkingArea;
            Location = new Point(
                screen.Right  - formWidth  - 12,
                screen.Bottom - formHeight - 12);

            // Close when focus is lost
            Deactivate += (_, _) => Close();

            // Keyboard close
            KeyPreview = true;
            KeyDown    += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };

            // ── Rounded border panel ─────────────────────────────────────
            var border = new Panel
            {
                Location  = new Point(0, 0),
                Size      = new Size(formWidth, formHeight),
                BackColor = BorderColor
            };
            Controls.Add(border);

            var inner = new Panel
            {
                Location  = new Point(1, 1),
                Size      = new Size(formWidth - 2, formHeight - 2),
                BackColor = BgColor
            };
            border.Controls.Add(inner);

            // ── Header ────────────────────────────────────────────────────
            var header = new Panel
            {
                Location  = new Point(0, 0),
                Size      = new Size(formWidth - 2, HeaderH),
                BackColor = HeaderColor
            };
            inner.Controls.Add(header);

            // Phone icon (drawn via label emoji)
            var iconLbl = new Label
            {
                Text      = "",   // iPhone emoji
                Font      = new Font("Segoe UI Emoji", 16f),
                ForeColor = AccentColor,
                Location  = new Point(PaddingX, 8),
                Size      = new Size(36, 36),
                TextAlign = ContentAlignment.MiddleCenter
            };
            header.Controls.Add(iconLbl);

            var titleLbl = new Label
            {
                Text      = _info.DeviceName,
                Font      = TitleFont,
                ForeColor = ValueColor,
                Location  = new Point(PaddingX + 40, 8),
                Size      = new Size(formWidth - PaddingX * 2 - 40 - 36, 22),
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true
            };
            header.Controls.Add(titleLbl);

            var subLbl = new Label
            {
                Text      = string.IsNullOrEmpty(_info.ModelName) ? _info.ProductType : _info.ModelName,
                Font      = SubFont,
                ForeColor = LabelColor,
                Location  = new Point(PaddingX + 40, 28),
                Size      = new Size(formWidth - PaddingX * 2 - 40, 16),
                TextAlign = ContentAlignment.MiddleLeft
            };
            header.Controls.Add(subLbl);

            // Close button
            var closeBtn = MakeButton("✕", new Point(formWidth - 2 - 28, 4), new Size(24, 24));
            closeBtn.Font      = new Font("Segoe UI", 8f, FontStyle.Bold);
            closeBtn.ForeColor = LabelColor;
            closeBtn.BackColor = Color.Transparent;
            closeBtn.FlatAppearance.BorderSize = 0;
            closeBtn.Click += (_, _) => Close();
            closeBtn.MouseEnter += (_, _) => closeBtn.ForeColor = Color.White;
            closeBtn.MouseLeave += (_, _) => closeBtn.ForeColor = LabelColor;
            header.Controls.Add(closeBtn);

            // ── Row area ──────────────────────────────────────────────────
            int y = HeaderH + PaddingY;
            foreach (var (label, value) in rows)
            {
                int x = PaddingX;

                // Label
                var lbl = new Label
                {
                    Text      = label + ":",
                    Font      = LabelFont,
                    ForeColor = LabelColor,
                    Location  = new Point(x, y + 7),
                    Size      = new Size(LabelWidth, RowHeight - 8),
                    TextAlign = ContentAlignment.TopLeft
                };
                inner.Controls.Add(lbl);
                x += LabelWidth + 8;

                // Value
                var val = new Label
                {
                    Text         = value,
                    Font         = ValueFont,
                    ForeColor    = ValueColor,
                    Location     = new Point(x, y + 7),
                    Size         = new Size(ValueWidth, RowHeight - 8),
                    TextAlign    = ContentAlignment.TopLeft,
                    AutoEllipsis = true
                };
                inner.Controls.Add(val);
                x += ValueWidth + 8;

                // Copy button
                string copyValue = label == "Battery Level"
                    ? (_info.BatteryLevel + (_info.IsCharging ? " (Charging)" : ""))
                    : value;

                var copy = MakeButton("Copy", new Point(x, y + 3), new Size(CopyWidth, 22));
                copy.Tag    = copyValue;
                copy.Click += (s, _) =>
                {
                    try { Clipboard.SetText(((Button)s!).Tag?.ToString() ?? ""); }
                    catch { /* Clipboard busy */ }
                };
                inner.Controls.Add(copy);

                y += RowHeight + RowGap;
            }

            y += 2; // small extra gap before footer

            // ── Separator ────────────────────────────────────────────────
            var sep = new Panel
            {
                Location  = new Point(PaddingX, y),
                Size      = new Size(formWidth - 2 - PaddingX * 2, 1),
                BackColor = BorderColor
            };
            inner.Controls.Add(sep);

            y += 10;

            // ── Footer: Copy All button ───────────────────────────────────
            var copyAll = MakeButton("Copy All", new Point(PaddingX, y),
                new Size(ValueWidth + LabelWidth + 8, 30));
            copyAll.Font      = new Font("Segoe UI Semibold", 9f);
            copyAll.BackColor = AccentColor;
            copyAll.ForeColor = Color.White;
            copyAll.FlatAppearance.BorderColor = AccentColor;
            copyAll.Click += (_, _) =>
            {
                try { Clipboard.SetText(_info.ToClipboardText()); }
                catch { /* Clipboard busy */ }
            };
            inner.Controls.Add(copyAll);

            // Drag to move (click anywhere on form)
            MakeDraggable(inner);
            MakeDraggable(header);
        }

        // ── UI Helpers ────────────────────────────────────────────────────

        private Button MakeButton(string text, Point location, Size size)
        {
            var btn = new Button
            {
                Text      = text,
                Font      = BtnFont,
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
            btn.MouseEnter += (_, _) => { btn.BackColor = CopyBtnHover; btn.FlatAppearance.BorderColor = CopyBtnHover; };
            btn.MouseLeave += (_, _) => { btn.BackColor = CopyBtnColor; btn.FlatAppearance.BorderColor = CopyBtnColor; };
            return btn;
        }

        // Enable dragging the whole form
        private Point _dragStart;
        private bool  _dragging;

        private void MakeDraggable(Control ctrl)
        {
            ctrl.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) { _dragging = true; _dragStart = e.Location; } };
            ctrl.MouseMove += (_, e) =>
            {
                if (!_dragging) return;
                var cur = Cursor.Position;
                Location = new Point(cur.X - _dragStart.X, cur.Y - _dragStart.Y);
            };
            ctrl.MouseUp += (_, _) => _dragging = false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                TitleFont.Dispose();
                SubFont.Dispose();
                LabelFont.Dispose();
                ValueFont.Dispose();
                BtnFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
