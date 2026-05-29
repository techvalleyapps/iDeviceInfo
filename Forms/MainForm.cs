using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace iDeviceInfo.Forms
{
    /// <summary>
    /// The main application window.
    /// Visible on launch so the app appears in the taskbar and can be pinned.
    /// Closing the window minimises to the system tray rather than exiting —
    /// the app keeps running for the whole Windows session.
    /// </summary>
    public sealed class MainForm : Form
    {
        // ── Design constants ──────────────────────────────────────────────
        private const int W = 380;
        private const int H = 190;

        private static readonly Color BgColor     = Color.FromArgb(22,  22,  22);
        private static readonly Color HeaderColor  = Color.FromArgb(14,  14,  14);
        private static readonly Color AccentColor  = Color.FromArgb(10,  132, 255);
        private static readonly Color LabelColor   = Color.FromArgb(155, 155, 155);
        private static readonly Color ValueColor   = Color.White;
        private static readonly Color BorderColor  = Color.FromArgb(55,  55,  55);

        private readonly Font _titleFont  = new("Segoe UI Semibold", 11f);
        private readonly Font _labelFont  = new("Segoe UI",          9f);
        private readonly Font _statusFont = new("Segoe UI",          9.5f, FontStyle.Bold);
        private readonly Font _hintFont   = new("Segoe UI",          8f);

        // ── Status label — updated by TrayApplicationContext ──────────────
        private readonly Label _statusLabel;
        private readonly Label _subLabel;

        // ── Drag support ──────────────────────────────────────────────────
        private Point _dragStart;
        private bool  _dragging;

        // ─────────────────────────────────────────────────────────────────

        public MainForm()
        {
            // ── Form settings ─────────────────────────────────────────────
            Text             = "iDeviceInfo";
            FormBorderStyle  = FormBorderStyle.None;
            BackColor        = BgColor;
            Size             = new Size(W, H);
            StartPosition    = FormStartPosition.Manual;
            ShowInTaskbar    = true;          // enables taskbar pinning
            TopMost          = false;

            // Position near bottom-right on first open
            var wa = Screen.PrimaryScreen!.WorkingArea;
            Location = new Point(wa.Right - W - 20, wa.Bottom - H - 20);

            // ── Outer border ──────────────────────────────────────────────
            var border = new Panel { Location = Point.Empty, Size = new Size(W, H), BackColor = BorderColor };
            Controls.Add(border);

            var inner = new Panel { Location = new Point(1, 1), Size = new Size(W - 2, H - 2), BackColor = BgColor };
            border.Controls.Add(inner);

            // ── Header ────────────────────────────────────────────────────
            var header = new Panel { Location = Point.Empty, Size = new Size(W - 2, 46), BackColor = HeaderColor };
            inner.Controls.Add(header);

            var icon = new Label
            {
                Text      = "",
                Font      = new Font("Segoe UI Emoji", 16f),
                ForeColor = AccentColor,
                Location  = new Point(14, 6),
                Size      = new Size(34, 34),
                TextAlign = ContentAlignment.MiddleCenter
            };
            header.Controls.Add(icon);

            var title = new Label
            {
                Text      = "iDeviceInfo",
                Font      = _titleFont,
                ForeColor = ValueColor,
                Location  = new Point(52, 7),
                Size      = new Size(260, 22),
                TextAlign = ContentAlignment.MiddleLeft
            };
            header.Controls.Add(title);

            var hint = new Label
            {
                Text      = "Closes to tray  •  Launch this shortcut to start",
                Font      = _hintFont,
                ForeColor = LabelColor,
                Location  = new Point(52, 27),
                Size      = new Size(280, 14),
                TextAlign = ContentAlignment.MiddleLeft
            };
            header.Controls.Add(hint);

            // Close button
            var closeBtn = new Button
            {
                Text      = "✕",
                Font      = new Font("Segoe UI", 8f, FontStyle.Bold),
                ForeColor = LabelColor,
                BackColor = Color.Transparent,
                Location  = new Point(W - 2 - 28, 4),
                Size      = new Size(24, 24),
                FlatStyle = FlatStyle.Flat,
                Cursor    = Cursors.Hand
            };
            closeBtn.FlatAppearance.BorderSize  = 0;
            closeBtn.Click      += (_, _) => HideToTray();
            closeBtn.MouseEnter += (_, _) => closeBtn.ForeColor = Color.White;
            closeBtn.MouseLeave += (_, _) => closeBtn.ForeColor = LabelColor;
            header.Controls.Add(closeBtn);

            // ── Status area ───────────────────────────────────────────────
            var sep1 = new Panel { Location = new Point(14, 56), Size = new Size(W - 30, 1), BackColor = BorderColor };
            inner.Controls.Add(sep1);

            var statusLbl = new Label
            {
                Text      = "STATUS",
                Font      = _labelFont,
                ForeColor = LabelColor,
                Location  = new Point(16, 68),
                Size      = new Size(80, 18),
                TextAlign = ContentAlignment.MiddleLeft
            };
            inner.Controls.Add(statusLbl);

            _statusLabel = new Label
            {
                Text      = "Waiting for iOS device...",
                Font      = _statusFont,
                ForeColor = LabelColor,
                Location  = new Point(16, 88),
                Size      = new Size(W - 34, 22),
                TextAlign = ContentAlignment.MiddleLeft
            };
            inner.Controls.Add(_statusLabel);

            _subLabel = new Label
            {
                Text      = "Connect an iPhone or iPad via USB.",
                Font      = _labelFont,
                ForeColor = LabelColor,
                Location  = new Point(16, 112),
                Size      = new Size(W - 34, 18),
                TextAlign = ContentAlignment.MiddleLeft
            };
            inner.Controls.Add(_subLabel);

            // ── Footer ────────────────────────────────────────────────────
            var sep2 = new Panel { Location = new Point(14, 140), Size = new Size(W - 30, 1), BackColor = BorderColor };
            inner.Controls.Add(sep2);

            var footerLbl = new Label
            {
                Text      = "✓  Running with administrator rights  —  device pairing enabled",
                Font      = _hintFont,
                ForeColor = LabelColor,
                Location  = new Point(16, 150),
                Size      = new Size(W - 34, 30),
                TextAlign = ContentAlignment.MiddleLeft
            };
            // Dim the footer if not elevated
            if (!IsRunningAsAdmin())
            {
                footerLbl.Text      = "⚠  Not running as administrator  —  new device pairing may not work";
                footerLbl.ForeColor = Color.FromArgb(255, 180, 0);
            }
            inner.Controls.Add(footerLbl);

            // ── Drag to move ──────────────────────────────────────────────
            MakeDraggable(inner);
            MakeDraggable(header);

            // ── Close intercept ───────────────────────────────────────────
            FormClosing += (_, e) =>
            {
                e.Cancel = true;   // never actually close — hide to tray instead
                HideToTray();
            };
        }

        // ── Public status update (called from TrayApplicationContext) ─────

        public void UpdateStatus(int deviceCount, string? lastName)
        {
            if (InvokeRequired) { Invoke(() => UpdateStatus(deviceCount, lastName)); return; }

            if (deviceCount == 0)
            {
                _statusLabel.Text      = "Waiting for iOS device...";
                _statusLabel.ForeColor = LabelColor;
                _subLabel.Text         = "Connect an iPhone or iPad via USB.";
            }
            else if (deviceCount == 1)
            {
                _statusLabel.Text      = lastName ?? "Device connected";
                _statusLabel.ForeColor = AccentColor;
                _subLabel.Text         = "Click the tray icon or device name to view details.";
            }
            else
            {
                _statusLabel.Text      = $"{deviceCount} devices connected";
                _statusLabel.ForeColor = AccentColor;
                _subLabel.Text         = "Right-click the tray icon to choose a device.";
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────

        private void HideToTray()
        {
            Hide();
            ShowInTaskbar = false;
        }

        public void ShowFromTray()
        {
            ShowInTaskbar = true;
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        private static bool IsRunningAsAdmin()
        {
            try
            {
                using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(id);
                return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        private void MakeDraggable(Control ctrl)
        {
            ctrl.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) { _dragging = true; _dragStart = e.Location; } };
            ctrl.MouseMove += (_, e) =>
            {
                if (!_dragging) return;
                var c = Cursor.Position;
                Location = new Point(c.X - _dragStart.X, c.Y - _dragStart.Y);
            };
            ctrl.MouseUp += (_, _) => _dragging = false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _titleFont.Dispose();
                _labelFont.Dispose();
                _statusFont.Dispose();
                _hintFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
