using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace iDeviceInfo.Forms
{
    /// <summary>
    /// Unified main window.
    ///
    /// Left side panel  — always visible; lists connected devices.
    ///                    Click any entry to switch to that device's details.
    /// Right content    — device info rows with per-row Copy buttons.
    ///
    /// ─  Minimize      → standard Windows minimize to taskbar (toggle works).
    /// ✕  Close         → hide to system tray (app keeps running).
    /// </summary>
    public sealed class MainForm : Form
    {
        // ── Dimensions ────────────────────────────────────────────────────
        private const int SIDE_W    = 132;   // left device-list panel
        private const int SEP_W     = 1;     // divider
        private const int CONTENT_W = 466;   // right info panel
        private const int FULL_W    = SIDE_W + SEP_W + CONTENT_W;  // 599
        private const int HEADER_H  = 50;
        private const int PX        = 18;
        private const int PY        = 12;
        private const int LABEL_W   = 130;
        private const int VALUE_W   = 230;
        private const int COPY_W    = 54;
        private const int ROW_H     = 26;
        private const int ROW_GAP   = 5;
        private const int FOOTER_H  = 52;

        private static readonly string[] RowKeys =
        {
            "Device Name", "Model", "iOS Build", "Serial Number",
            "IMEI", "IMEI 2", "Battery Level", "Battery Health", "UDID"
        };

        private static readonly int FORM_H =
            HEADER_H + PY
            + RowKeys.Length * (ROW_H + ROW_GAP) - ROW_GAP
            + PY + 16 + FOOTER_H;   // ≈ 416

        // ── Colors ────────────────────────────────────────────────────────
        private static readonly Color Bg        = Color.FromArgb(30,  30,  30);
        private static readonly Color HeaderBg  = Color.FromArgb(20,  20,  20);
        private static readonly Color SideBg    = Color.FromArgb(24,  24,  24);
        private static readonly Color SideSelBg = Color.FromArgb(40,  40,  40);
        private static readonly Color Accent    = Color.FromArgb(10,  132, 255);
        private static readonly Color LabelFg   = Color.FromArgb(160, 160, 160);
        private static readonly Color ValueFg   = Color.White;
        private static readonly Color BorderCol = Color.FromArgb(60,  60,  60);
        private static readonly Color BtnBg     = Color.FromArgb(55,  55,  55);
        private static readonly Color BtnHover  = Color.FromArgb(75,  75,  75);
        private static readonly Color Dim       = Color.FromArgb(70,  70,  70);

        // ── Fonts ─────────────────────────────────────────────────────────
        private readonly Font _title    = new("Segoe UI Semibold", 11f);
        private readonly Font _sub      = new("Segoe UI",           8.5f);
        private readonly Font _lbl      = new("Segoe UI",           9f);
        private readonly Font _val      = new("Segoe UI",           9f, FontStyle.Bold);
        private readonly Font _btn      = new("Segoe UI",           8f);
        private readonly Font _sideName = new("Segoe UI Semibold",  8.5f);
        private readonly Font _sideMod  = new("Segoe UI",           7.5f);
        private readonly Font _sideCap  = new("Segoe UI",           7f,  FontStyle.Bold);

        // ── Live controls ─────────────────────────────────────────────────
        private readonly Label    _hTitle;
        private readonly Label    _hSub;
        private readonly Label[]  _vals = new Label[RowKeys.Length];
        private readonly Button[] _cpys = new Button[RowKeys.Length];
        private readonly Button   _cpyAll;

        // ── Side panel ────────────────────────────────────────────────────
        private readonly Panel _sideList;   // scrollable area for device entries
        private readonly Dictionary<string, Panel> _entries = new(StringComparer.OrdinalIgnoreCase);

        // ── Device state ──────────────────────────────────────────────────
        private readonly Dictionary<string, DeviceInfo> _devs =
            new(StringComparer.OrdinalIgnoreCase);
        private string? _sel;   // currently selected serial

        // ── Drag ──────────────────────────────────────────────────────────
        private Point _dragPt;
        private bool  _dragging;

        // ── Taskbar minimize/restore fix for borderless forms ─────────────
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.Style |= 0x00020000  // WS_MINIMIZEBOX
                          | 0x00080000; // WS_SYSMENU
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_SYSCOMMAND = 0x0112;
            const int WM_ACTIVATE   = 0x0006;
            const int SC_MINIMIZE   = 0xF020;
            const int SC_RESTORE    = 0xF120;

            if (m.Msg == WM_SYSCOMMAND)
            {
                int cmd = m.WParam.ToInt32() & 0xFFF0;
                if (cmd == SC_MINIMIZE) { WindowState = FormWindowState.Minimized; return; }
                if (cmd == SC_RESTORE)  { WindowState = FormWindowState.Normal; Activate(); return; }
            }

            // Clicking the taskbar button on a minimized borderless form sends WM_ACTIVATE
            // before WM_SYSCOMMAND/SC_RESTORE — restore here so the toggle works.
            if (m.Msg == WM_ACTIVATE && (m.WParam.ToInt32() & 0xFFFF) != 0)
            {
                if (WindowState == FormWindowState.Minimized)
                    WindowState = FormWindowState.Normal;
            }

            base.WndProc(ref m);
        }

        // ─────────────────────────────────────────────────────────────────

        public MainForm()
        {
            Text            = "iDeviceInfo";
            FormBorderStyle = FormBorderStyle.None;
            Icon            = LoadAppIcon();
            BackColor       = BorderCol;           // 1-px border via form background
            ClientSize      = new Size(FULL_W, FORM_H);
            StartPosition   = FormStartPosition.Manual;
            ShowInTaskbar   = true;
            TopMost         = false;
            KeyPreview      = true;

            var wa = Screen.PrimaryScreen!.WorkingArea;
            Location = new Point(wa.Right - FULL_W - 12, wa.Bottom - FORM_H - 12);

            KeyDown     += (_, e) => { if (e.KeyCode == Keys.Escape) HideToTray(); };
            FormClosing += (_, e) => { e.Cancel = true; HideToTray(); };

            // ── Main inner panel (1-px inset = the "border") ──────────────
            var inner = new Panel
            {
                Location  = new Point(1, 1),
                Size      = new Size(FULL_W - 2, FORM_H - 2),
                BackColor = Bg,
                Anchor    = AnchorStyles.Top | AnchorStyles.Bottom |
                            AnchorStyles.Left | AnchorStyles.Right
            };
            Controls.Add(inner);

            // ── Header (full width) ───────────────────────────────────────
            var header = new Panel
            {
                Location  = new Point(0, 0),
                Size      = new Size(FULL_W - 2, HEADER_H),
                BackColor = HeaderBg,
                Anchor    = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            inner.Controls.Add(header);

            header.Controls.Add(new Label
            {
                Text      = "",
                Font      = new Font("Segoe UI Emoji", 16f),
                ForeColor = Accent,
                Location  = new Point(PX, 8),
                Size      = new Size(34, 34),
                TextAlign = ContentAlignment.MiddleCenter
            });

            _hTitle = new Label
            {
                Text         = "iDeviceInfo",
                Font         = _title,
                ForeColor    = ValueFg,
                Location     = new Point(PX + 38, 7),
                Size         = new Size(300, 22),
                TextAlign    = ContentAlignment.MiddleLeft,
                AutoEllipsis = true
            };
            header.Controls.Add(_hTitle);

            _hSub = new Label
            {
                Text      = "No device connected",
                Font      = _sub,
                ForeColor = LabelFg,
                Location  = new Point(PX + 38, 28),
                Size      = new Size(300, 16),
                TextAlign = ContentAlignment.MiddleLeft
            };
            header.Controls.Add(_hSub);

            // ─ button → minimize to taskbar  (anchored to right edge of full-width header)
            var minBtn = HeaderBtn("─", new Point(FULL_W - 2 - 54, 4));
            minBtn.Click += (_, _) => WindowState = FormWindowState.Minimized;
            header.Controls.Add(minBtn);

            // ✕ button → hide to tray
            var cls = HeaderBtn("✕", new Point(FULL_W - 2 - 28, 4));
            cls.Click += (_, _) => HideToTray();
            header.Controls.Add(cls);

            // ── Side panel (always visible) ───────────────────────────────
            var side = new Panel
            {
                Location  = new Point(0, HEADER_H),
                Size      = new Size(SIDE_W, FORM_H - HEADER_H - 2),
                BackColor = SideBg
            };
            inner.Controls.Add(side);

            side.Controls.Add(new Label
            {
                Text      = "DEVICES",
                Font      = _sideCap,
                ForeColor = LabelFg,
                Location  = new Point(10, 8),
                Size      = new Size(SIDE_W - 14, 14),
                TextAlign = ContentAlignment.MiddleLeft
            });
            side.Controls.Add(new Panel
            {
                Location  = new Point(0, 26),
                Size      = new Size(SIDE_W, 1),
                BackColor = BorderCol
            });

            _sideList = new Panel
            {
                Location   = new Point(0, 28),
                Size       = new Size(SIDE_W, FORM_H - HEADER_H - 30),
                BackColor  = SideBg,
                AutoScroll = true
            };
            side.Controls.Add(_sideList);

            // Separator between side and content (always visible)
            inner.Controls.Add(new Panel
            {
                Location  = new Point(SIDE_W, HEADER_H),
                Size      = new Size(SEP_W, FORM_H - HEADER_H - 2),
                BackColor = BorderCol
            });

            // ── Content rows (offset right by side panel + separator) ──────
            int cx = SIDE_W + SEP_W;
            int y  = HEADER_H + PY;

            for (int i = 0; i < RowKeys.Length; i++)
            {
                int x = cx + PX;

                inner.Controls.Add(new Label
                {
                    Text      = RowKeys[i] + ":",
                    Font      = _lbl,
                    ForeColor = LabelFg,
                    Location  = new Point(x, y + 5),
                    Size      = new Size(LABEL_W, ROW_H),
                    TextAlign = ContentAlignment.TopLeft
                });
                x += LABEL_W + 8;

                var valLbl = new Label
                {
                    Text         = "—",
                    Font         = _val,
                    ForeColor    = Dim,
                    Location     = new Point(x, y + 5),
                    Size         = new Size(VALUE_W, ROW_H),
                    TextAlign    = ContentAlignment.TopLeft,
                    AutoEllipsis = true
                };
                inner.Controls.Add(valLbl);
                _vals[i] = valLbl;
                x += VALUE_W + 8;

                var cpy = Btn("Copy", new Point(x, y + 2), new Size(COPY_W, 22));
                cpy.Enabled = false;
                inner.Controls.Add(cpy);
                _cpys[i] = cpy;

                y += ROW_H + ROW_GAP;
            }

            // Separator above footer
            y += 2;
            inner.Controls.Add(new Panel
            {
                Location  = new Point(cx + PX, y),
                Size      = new Size(CONTENT_W - PX * 2 - 4, 1),
                BackColor = BorderCol
            });
            y += 14;

            // Footer buttons
            var hideTray = Btn("Hide to tray", new Point(cx + PX, y), new Size(118, 34));
            hideTray.Click += (_, _) => HideToTray();
            inner.Controls.Add(hideTray);

            _cpyAll = Btn("Copy All", new Point(cx + CONTENT_W - PX - 2 - 148, y), new Size(148, 34));
            _cpyAll.Font                       = new Font("Segoe UI Semibold", 9f);
            _cpyAll.BackColor                  = Accent;
            _cpyAll.ForeColor                  = Color.White;
            _cpyAll.FlatAppearance.BorderColor = Accent;
            _cpyAll.Enabled                    = false;
            _cpyAll.Click += (_, _) =>
            {
                if (_sel != null && _devs.TryGetValue(_sel, out var d))
                    try { Clipboard.SetText(d.ToClipboardText()); } catch { }
            };
            inner.Controls.Add(_cpyAll);

            MakeDraggable(inner);
            MakeDraggable(header);
            MakeDraggable(side);
        }

        // ── Public API ────────────────────────────────────────────────────

        public void AddDevice(DeviceInfo info)
        {
            if (InvokeRequired) { Invoke(() => AddDevice(info)); return; }
            _devs[info.SerialNumber] = info;
            RefreshSideList();
            SelectDevice(info.SerialNumber);
        }

        public void RemoveDevice(string serial)
        {
            if (InvokeRequired) { Invoke(() => RemoveDevice(serial)); return; }
            _devs.Remove(serial);
            RefreshSideList();

            if (_sel == serial)
            {
                string? next = _devs.Keys.FirstOrDefault();
                if (next != null) SelectDevice(next);
                else { _sel = null; ClearContent(); }
            }
        }

        public void SelectDevice(string serial)
        {
            if (InvokeRequired) { Invoke(() => SelectDevice(serial)); return; }
            if (!_devs.TryGetValue(serial, out var info)) return;
            _sel = serial;
            FillContent(info);
            HighlightEntry(serial);
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

        // ── Content fill / clear ──────────────────────────────────────────

        private void FillContent(DeviceInfo d)
        {
            string batt  = d.BatteryLevel + (d.IsCharging ? "  (Charging)" : "");
            string imei2 = string.IsNullOrEmpty(d.IMEI2) || d.IMEI2 == "N/A" ? "—" : d.IMEI2;

            string[] display = { d.DeviceName, d.FullModelName, d.iOSVersion,
                                 d.SerialNumber, d.IMEI, imei2, batt, d.BatteryHealth, d.UDID };
            string[] copy    = { d.DeviceName, d.FullModelName, d.iOSVersion,
                                 d.SerialNumber, d.IMEI, imei2 == "—" ? "" : imei2,
                                 d.BatteryLevel + (d.IsCharging ? " (Charging)" : ""),
                                 d.BatteryHealth, d.UDID };

            for (int i = 0; i < RowKeys.Length; i++)
            {
                _vals[i].Text      = display[i];
                _vals[i].ForeColor = display[i] == "—" ? Dim : ValueFg;
                _cpys[i].Enabled   = !string.IsNullOrEmpty(copy[i]) && copy[i] != "—";
                string cv = copy[i];
                _cpys[i].Click -= CopyTag;
                _cpys[i].Tag    = cv;
                _cpys[i].Click += CopyTag;
            }

            _hTitle.Text    = d.DeviceName;
            _hSub.Text      = d.FullModelName;
            _cpyAll.Enabled = true;
        }

        private void ClearContent()
        {
            foreach (var l in _vals)  { l.Text = "—"; l.ForeColor = Dim; }
            foreach (var b in _cpys)    b.Enabled = false;
            _hTitle.Text    = "iDeviceInfo";
            _hSub.Text      = "No device connected";
            _cpyAll.Enabled = false;
        }

        // ── Side panel ────────────────────────────────────────────────────

        private void RefreshSideList()
        {
            foreach (var p in _entries.Values)
            {
                _sideList.Controls.Remove(p);
                p.Dispose();
            }
            _entries.Clear();

            int ey = 2;
            foreach (var (serial, info) in _devs)
            {
                string s        = serial;
                bool   selected = s == _sel;

                var entry = new Panel
                {
                    Location  = new Point(0, ey),
                    Size      = new Size(SIDE_W, 52),
                    BackColor = selected ? SideSelBg : SideBg,
                    Cursor    = Cursors.Hand
                };

                // Coloured left-edge indicator
                var bar = new Panel
                {
                    Location  = new Point(0, 0),
                    Size      = new Size(3, 52),
                    BackColor = selected ? Accent : Color.Transparent
                };
                entry.Controls.Add(bar);

                var nameLbl = new Label
                {
                    Text         = info.DeviceName,
                    Font         = _sideName,
                    ForeColor    = ValueFg,
                    Location     = new Point(10, 8),
                    Size         = new Size(SIDE_W - 14, 18),
                    AutoEllipsis = true,
                    TextAlign    = ContentAlignment.MiddleLeft
                };
                entry.Controls.Add(nameLbl);

                var modLbl = new Label
                {
                    Text         = string.IsNullOrEmpty(info.ModelName) ? info.ProductType : info.ModelName,
                    Font         = _sideMod,
                    ForeColor    = LabelFg,
                    Location     = new Point(10, 28),
                    Size         = new Size(SIDE_W - 14, 16),
                    AutoEllipsis = true,
                    TextAlign    = ContentAlignment.MiddleLeft
                };
                entry.Controls.Add(modLbl);

                void OnClick() => SelectDevice(s);
                void OnEnter() { if (_sel != s) entry.BackColor = SideSelBg; }
                void OnLeave() { if (_sel != s) entry.BackColor = SideBg; }

                foreach (Control c in new Control[] { entry, nameLbl, modLbl })
                {
                    c.Click      += (_, _) => OnClick();
                    c.MouseEnter += (_, _) => OnEnter();
                    c.MouseLeave += (_, _) => OnLeave();
                }

                _sideList.Controls.Add(entry);
                _entries[serial] = entry;
                ey += 54;
            }
        }

        private void HighlightEntry(string serial)
        {
            foreach (var (s, p) in _entries)
            {
                bool sel = s == serial;
                p.BackColor = sel ? SideSelBg : SideBg;
                if (p.Controls.Count > 0)
                    p.Controls[0].BackColor = sel ? Accent : Color.Transparent;
            }
        }

        // ── UI helpers ────────────────────────────────────────────────────

        private void HideToTray() { Hide(); ShowInTaskbar = false; }

        private static void CopyTag(object? s, EventArgs _)
        {
            if (s is Button b && b.Tag is string t && !string.IsNullOrEmpty(t))
                try { Clipboard.SetText(t); } catch { }
        }

        private Button HeaderBtn(string text, Point loc)
        {
            var b = new Button
            {
                Text      = text,
                Font      = new Font("Segoe UI", 8f, FontStyle.Bold),
                ForeColor = LabelFg,
                BackColor = Color.Transparent,
                Location  = loc,
                Size      = new Size(24, 24),
                FlatStyle = FlatStyle.Flat,
                Cursor    = Cursors.Hand
            };
            b.FlatAppearance.BorderSize = 0;
            b.MouseEnter += (_, _) => b.ForeColor = Color.White;
            b.MouseLeave += (_, _) => b.ForeColor = LabelFg;
            return b;
        }

        private Button Btn(string text, Point loc, Size size)
        {
            var b = new Button
            {
                Text      = text,
                Font      = _btn,
                ForeColor = Color.White,
                BackColor = BtnBg,
                Location  = loc,
                Size      = size,
                FlatStyle = FlatStyle.Flat,
                Cursor    = Cursors.Hand,
                UseVisualStyleBackColor = false
            };
            b.FlatAppearance.BorderColor = BtnBg;
            b.FlatAppearance.BorderSize  = 1;
            b.MouseEnter += (_, _) => { if (!b.Enabled) return; b.BackColor = BtnHover; b.FlatAppearance.BorderColor = BtnHover; };
            b.MouseLeave += (_, _) => { b.BackColor = BtnBg; b.FlatAppearance.BorderColor = BtnBg; };
            return b;
        }

        private void MakeDraggable(Control c)
        {
            c.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) { _dragging = true; _dragPt = e.Location; } };
            c.MouseMove += (_, e) => { if (!_dragging) return; var p = Cursor.Position; Location = new Point(p.X - _dragPt.X, p.Y - _dragPt.Y); };
            c.MouseUp   += (_, _) => _dragging = false;
        }

        internal static Icon? LoadAppIcon()
        {
            try
            {
                var p = Path.Combine(AppContext.BaseDirectory, "Resources", "iDeviceInfo.ico");
                if (File.Exists(p)) return new Icon(p);
            }
            catch { }
            return null;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _title.Dispose(); _sub.Dispose(); _lbl.Dispose(); _val.Dispose();
                _btn.Dispose(); _sideName.Dispose(); _sideMod.Dispose(); _sideCap.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
