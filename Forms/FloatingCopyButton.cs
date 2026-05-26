using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace iDeviceInfo.Forms
{
    /// <summary>
    /// A small floating "Copy All" button that hovers above the system tray
    /// whenever 3uTools is running. Clicking it copies all device info to clipboard.
    /// </summary>
    public sealed class FloatingCopyButton : Form
    {
        // ── Win32 ─────────────────────────────────────────────────────────

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int x, int y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        private static readonly IntPtr HWND_TOPMOST = new(-1);
        private const uint  SWP_NOSIZE     = 0x0001;
        private const uint  SWP_NOMOVE     = 0x0002;
        private const uint  SWP_NOACTIVATE = 0x0010;
        private const int   GWL_EXSTYLE    = -20;
        private const int   WS_EX_NOACTIVATE   = 0x08000000;
        private const int   WS_EX_TOOLWINDOW   = 0x00000080;
        private const int   WS_EX_LAYERED      = 0x00080000;

        // ── State ─────────────────────────────────────────────────────────

        private readonly System.Windows.Forms.Timer _pollTimer;
        private readonly Label       _btn;
        private DeviceInfo?          _currentDevice;

        // Colors
        private static readonly Color ColNormal  = Color.FromArgb(10, 132, 255);
        private static readonly Color ColHover   = Color.FromArgb(40, 155, 255);
        private static readonly Color ColPressed = Color.FromArgb(0,  100, 220);
        private static readonly Color ColText    = Color.White;

        // Sizes
        private const int BtnW       = 110;
        private const int BtnH       = 32;
        private const int MarginRight = 14;
        private const int MarginBottom = 10;  // gap above taskbar

        // ── Constructor ───────────────────────────────────────────────────

        public FloatingCopyButton()
        {
            // ── Borderless, always-on-top, non-activating form ────────────
            FormBorderStyle  = FormBorderStyle.None;
            BackColor        = ColNormal;
            Size             = new Size(BtnW, BtnH);
            ShowInTaskbar    = false;
            TopMost          = true;
            StartPosition    = FormStartPosition.Manual;
            Opacity          = 0.93;

            PositionAboveTray();

            // Don't steal focus when shown
            SetStyle(ControlStyles.Selectable, false);

            // ── Button label ──────────────────────────────────────────────
            _btn = new Label
            {
                Text      = "📋  Copy All",
                Font      = new Font("Segoe UI Semibold", 9f),
                ForeColor = ColText,
                Dock      = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Cursor    = Cursors.Hand,
                BackColor = Color.Transparent
            };
            Controls.Add(_btn);

            // ── Mouse events ──────────────────────────────────────────────
            _btn.MouseEnter += (_, _) => { BackColor = ColHover; };
            _btn.MouseLeave += (_, _) => { BackColor = _currentDevice != null ? ColNormal : Color.FromArgb(80, 80, 80); };
            _btn.MouseDown  += (_, _) => { BackColor = ColPressed; };
            _btn.MouseUp    += (_, e) =>
            {
                BackColor = ColHover;
                if (e.Button == MouseButtons.Left)
                    DoCopyAll();
            };

            // Click on form body too
            MouseUp += (_, e) => { if (e.Button == MouseButtons.Left) DoCopyAll(); };

            // ── 3uTools process poll timer (every 1.5 s) ──────────────────
            _pollTimer = new System.Windows.Forms.Timer { Interval = 1500 };
            _pollTimer.Tick += OnPollTick;
            _pollTimer.Start();

            // Initial check
            OnPollTick(null, EventArgs.Empty);
        }

        // ── Device update (called by TrayApplicationContext) ──────────────

        public void UpdateDevice(DeviceInfo? info)
        {
            _currentDevice = info;
            UpdateAppearance();
        }

        // ── Process polling ───────────────────────────────────────────────

        private bool _3uToolsWasRunning = false;

        private void OnPollTick(object? sender, EventArgs e)
        {
            bool running = Is3uToolsRunning();

            if (running && !Visible)
            {
                Show();
                KeepTopmost();
            }
            else if (!running && Visible)
            {
                Hide();
            }

            if (running != _3uToolsWasRunning)
            {
                _3uToolsWasRunning = running;
                UpdateAppearance();
            }

            // Re-pin position in case taskbar moved
            if (Visible) PositionAboveTray();
        }

        private static bool Is3uToolsRunning()
        {
            var procs = Process.GetProcessesByName("3uTools");
            if (procs.Length > 0) { foreach (var p in procs) p.Dispose(); return true; }

            // Also check common alt process names
            procs = Process.GetProcessesByName("3utools");
            if (procs.Length > 0) { foreach (var p in procs) p.Dispose(); return true; }

            return false;
        }

        // ── Copy action ───────────────────────────────────────────────────

        private void DoCopyAll()
        {
            if (_currentDevice == null)
            {
                ShowFlash("No device!", Color.FromArgb(200, 60, 60));
                return;
            }

            try
            {
                Clipboard.SetText(_currentDevice.ToClipboardText());
                ShowFlash("Copied!", Color.FromArgb(52, 199, 89));
            }
            catch
            {
                ShowFlash("Error", Color.FromArgb(200, 60, 60));
            }
        }

        // Briefly flash green/red to give feedback
        private async void ShowFlash(string message, Color color)
        {
            string original = _btn.Text;
            Color  origColor = BackColor;

            _btn.Text = message;
            BackColor = color;

            await System.Threading.Tasks.Task.Delay(1200);

            if (!IsDisposed)
            {
                _btn.Text = original;
                BackColor = _currentDevice != null ? ColNormal : Color.FromArgb(80, 80, 80);
            }
        }

        // ── Appearance ────────────────────────────────────────────────────

        private void UpdateAppearance()
        {
            if (_currentDevice != null)
            {
                _btn.Text = "📋  Copy All";
                BackColor = ColNormal;
                _btn.Font = new Font("Segoe UI Semibold", 9f);
            }
            else
            {
                _btn.Text = "No device";
                BackColor = Color.FromArgb(80, 80, 80);
                _btn.Font = new Font("Segoe UI", 8.5f);
            }
        }

        // ── Positioning ───────────────────────────────────────────────────

        private void PositionAboveTray()
        {
            var workArea = Screen.PrimaryScreen!.WorkingArea;
            Location = new Point(
                workArea.Right  - BtnW - MarginRight,
                workArea.Bottom - BtnH - MarginBottom);
        }

        private void KeepTopmost()
        {
            SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        // Prevent the button from stealing focus
        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        // Rounded corners via region
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            var path = new GraphicsPath();
            int r = 8;
            path.AddArc(0,           0,            r * 2, r * 2, 180, 90);
            path.AddArc(Width - r*2, 0,            r * 2, r * 2, 270, 90);
            path.AddArc(Width - r*2, Height - r*2, r * 2, r * 2, 0,   90);
            path.AddArc(0,           Height - r*2, r * 2, r * 2, 90,  90);
            path.CloseFigure();
            Region = new Region(path);
        }

        // ── Cleanup ───────────────────────────────────────────────────────

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _pollTimer.Stop();
                _pollTimer.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
